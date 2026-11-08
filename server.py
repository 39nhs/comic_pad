"""ComicPad 로컬 서버.

표준 라이브러리만 사용. 앱 정적 파일을 제공하고, 프로젝트/에셋/설정을 G: 의
data 폴더에 읽고 쓴다. 실행하면 Edge 앱 창으로 화면을 연다.
"""
import http.server
import queue
import secrets
import socket
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
import urllib.error
import webbrowser

ROOT = os.path.dirname(os.path.abspath(__file__))
APP_DIR = os.path.join(ROOT, "app")
PLUGIN_DIR = os.path.join(ROOT, "plugins")
DATA_DIR = os.path.join(ROOT, "data")
PORT = int(os.environ.get("COMICPAD_PORT", "8787"))
if "--port" in sys.argv:
    PORT = int(sys.argv[sys.argv.index("--port") + 1])
IDLE_EXIT_SEC = 15 * 60

KINDS = {
    "projects": os.path.join(DATA_DIR, "projects"),
    "assets": os.path.join(DATA_DIR, "assets"),
    "autosave": os.path.join(DATA_DIR, "autosave"),
    "fonts": os.path.join(DATA_DIR, "fonts"),
    "exports": os.path.join(DATA_DIR, "exports"),
    "config": os.path.join(DATA_DIR, "config"),
    "versions": os.path.join(DATA_DIR, "versions"),
    "backups": os.path.join(DATA_DIR, "backups"),
    "plugins": PLUGIN_DIR,
}
# Windows 에 들어 있는 색 프로파일 (인쇄용 CMYK 변환, 읽기만)
_COLOR_DIR = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "spool", "drivers", "color")
if os.path.isdir(_COLOR_DIR):
    KINDS["colorprofiles"] = _COLOR_DIR
KINDS["models"] = os.path.join(DATA_DIR, "models")
WRITABLE = {"projects", "assets", "autosave", "exports", "config", "fonts", "models"}
TRASH_DIR = os.path.join(DATA_DIR, "trash")

# ---------------------------------------------------------------------------
# 내장 AI 모델: app/ai-models.json 목록에 있는 주소에서만 받아 data/models/<id>/ 에 둔다.
# PyTorch 가중치는 tools/convert_models.py 로 ONNX 변환 (PyTorch 가 있는 PC 에서만).
# ---------------------------------------------------------------------------
MODELS_DIR = os.path.join(DATA_DIR, "models")
MODEL_JOBS = {}


def model_registry():
    with open(os.path.join(APP_DIR, "ai-models.json"), encoding="utf-8") as fh:
        return {m["id"]: m for m in json.load(fh)["models"]}


def model_status():
    import importlib.util
    out = {"_env": {"torch": importlib.util.find_spec("torch") is not None}}
    for mid, m in model_registry().items():
        d = os.path.join(MODELS_DIR, mid)
        paths = [os.path.join(d, f["name"]) for f in m["files"]]
        out[mid] = {"installed": all(os.path.isfile(p) for p in paths),
                    "bytes": sum(os.path.getsize(p) for p in paths if os.path.isfile(p)),
                    "job": MODEL_JOBS.get(mid)}
    return out


def fetch_model(mid):
    m = model_registry()[mid]
    job = MODEL_JOBS[mid]
    d = os.path.join(MODELS_DIR, mid)
    os.makedirs(d, exist_ok=True)
    srcs = m["convert"]["from"] if m.get("convert") else m["files"]
    bundled = os.path.join(ROOT, "models_bundled", mid)
    try:
        if os.path.isdir(bundled):
            # 설치 파일에 미리 변환해 넣어 둔 작은 모델 (인터넷·PyTorch 없이)
            for f in os.listdir(bundled):
                shutil.copyfile(os.path.join(bundled, f), os.path.join(d, f))
            job["state"] = "done"
            return
        job["total"] = sum(f.get("size") or 0 for f in srcs)
        for f in srcs:
            dst = os.path.join(d, f["name"])
            if os.path.isfile(dst) and (not f.get("size") or os.path.getsize(dst) == f["size"]):
                job["done"] += os.path.getsize(dst)
                continue
            part = dst + ".part"
            req = urllib.request.Request(f["url"], headers={"User-Agent": "ComicPad"})
            with urllib.request.urlopen(req, timeout=60) as resp, open(part, "wb") as out:
                if not f.get("size"):
                    job["total"] += int(resp.headers.get("Content-Length") or 0)
                while True:
                    chunk = resp.read(1 << 20)
                    if not chunk:
                        break
                    out.write(chunk)
                    job["done"] += len(chunk)
            if f.get("size") and os.path.getsize(part) != f["size"]:
                raise RuntimeError(f"{f['name']} 크기가 맞지 않습니다 (받다가 끊긴 것 같습니다)")
            os.replace(part, dst)
        if m.get("convert"):
            job["state"] = "convert"
            src = os.path.join(d, srcs[0]["name"])
            dst = os.path.join(d, m["files"][0]["name"])
            p = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "convert_models.py"), m["convert"]["kind"], src, dst + ".part"],
                               capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=3600)
            if p.returncode:
                err = (p.stderr or p.stdout or "").strip().splitlines()[-1:] or ["?"]
                if "No module named" in err[0]:
                    raise RuntimeError("이 모델은 변환이 필요한데 PyTorch 가 없습니다. 명령 창에서 'pip install torch onnx' 후 다시 받으세요")
                raise RuntimeError("변환 실패: " + err[0])
            os.replace(dst + ".part", dst)
            os.remove(src)
        job["state"] = "done"
    except Exception as e:  # noqa: BLE001 — 받기 실패는 화면에 알리기만
        job["state"], job["error"] = "error", str(e)


MIME = {
    ".js": "text/javascript; charset=utf-8",
    ".mjs": "text/javascript; charset=utf-8",
    ".wasm": "application/wasm",
    ".css": "text/css; charset=utf-8",
    ".html": "text/html; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".cpad": "application/json; charset=utf-8",
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".webp": "image/webp",
    ".svg": "image/svg+xml",
    ".ttf": "font/ttf",
    ".otf": "font/otf",
    ".woff": "font/woff",
    ".woff2": "font/woff2",
    ".md": "text/plain; charset=utf-8",
    ".webmanifest": "application/manifest+json",
    ".glb": "model/gltf-binary",
    ".gltf": "model/gltf+json",
    ".obj": "text/plain; charset=utf-8",
}

last_seen = time.time()
LAN = False          # 같은 와이파이의 휴대폰/태블릿 접속 허용
RELAUNCH = None      # 다시 시작할 때 쓸 옵션
SERVER_CONF = os.path.join(DATA_DIR, "config", "server.json")
KEY_FILE = os.path.join(DATA_DIR, "config", "lan_key.txt")
LOCAL_ADDRS = ("127.0.0.1", "::1", "::ffff:127.0.0.1")


def lan_key():
    os.makedirs(os.path.dirname(KEY_FILE), exist_ok=True)
    if os.path.isfile(KEY_FILE):
        k = open(KEY_FILE, encoding="utf-8").read().strip()
        if k:
            return k
    k = secrets.token_urlsafe(9)
    with open(KEY_FILE, "w", encoding="utf-8") as fh:
        fh.write(k)
    return k


_LAN_IPS = {"t": 0, "ips": []}


def lan_ips_cached():
    if time.time() - _LAN_IPS["t"] > 60:
        _LAN_IPS.update(t=time.time(), ips=lan_ips())
    return _LAN_IPS["ips"]


def lan_ips():
    ips = []
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("10.255.255.255", 1))     # 실제로 보내지는 않음 (기본 경로의 내 주소 확인용)
        ips.append(s.getsockname()[0])
        s.close()
    except OSError:
        pass
    try:
        for ip in socket.gethostbyname_ex(socket.gethostname())[2]:
            if not ip.startswith("127.") and ip not in ips:
                ips.append(ip)
    except OSError:
        pass
    return ips


def read_conf():
    try:
        with open(SERVER_CONF, encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return {}


# ---------------------------------------------------------------------
# 구글 드라이브: PC 에 설치된 "Google Drive 데스크톱" 앱의 내 드라이브 폴더로 자동 복사
# (작업은 계속 G: data 에, 저장할 때마다 내 드라이브\ComicPad 에도 사본. Google 계정 연동·API 불필요)
# ---------------------------------------------------------------------
DRIVE_KINDS = ("projects", "assets", "exports", "fonts")


def find_my_drive():
    """Google Drive 데스크톱이 연결한 '내 드라이브' 폴더 찾기."""
    if os.name == "nt":
        for letter in "DEFGHIJKLMNOPQRSTUVWXYZC":
            for name in ("내 드라이브", "My Drive"):
                path = f"{letter}:\\{name}"
                if os.path.isdir(path):
                    return path
    for path in (os.path.expanduser("~/My Drive"), os.path.expanduser("~/Google Drive/My Drive"), os.path.expanduser("~/내 드라이브")):
        if os.path.isdir(path):
            return path
    return None


class DriveSync:
    def __init__(self):
        self.q = queue.Queue()
        self.last_ok = None
        self.last_error = ""
        self.copied = 0
        threading.Thread(target=self._worker, daemon=True).start()

    def conf(self):
        return read_conf().get("drive") or {}

    @property
    def on(self):
        return bool(self.conf().get("on")) and bool(self.target())

    def target(self):
        c = self.conf()
        if c.get("path"):
            return c["path"]
        found = find_my_drive()
        return os.path.join(found, "ComicPad") if found else None

    def set(self, on, path=None):
        conf = read_conf()
        d = conf.get("drive") or {}
        d["on"] = bool(on)
        if path is not None:
            d["path"] = path
        conf["drive"] = d
        write_conf(conf)
        if self.on:
            threading.Thread(target=self.sync_all, daemon=True).start()

    def put(self, kind, full):
        if kind in DRIVE_KINDS and self.on:
            self.q.put(("put", kind, os.path.relpath(full, KINDS[kind])))

    def delete(self, kind, full):
        if kind in DRIVE_KINDS and self.on:
            self.q.put(("del", kind, os.path.relpath(full, KINDS[kind])))

    def sync_all(self):
        """PC 쪽이 더 새롭거나 드라이브에 없는 파일을 모두 복사 대기열에."""
        tgt, n = self.target(), 0
        if not tgt:
            return 0
        for kind in DRIVE_KINDS:
            for dirpath, _dirs, files in os.walk(KINDS[kind]):
                for f in files:
                    if f.endswith(".tmp"):
                        continue
                    src = os.path.join(dirpath, f)
                    rel = os.path.relpath(src, KINDS[kind])
                    dst = os.path.join(tgt, kind, rel)
                    try:
                        if os.path.isfile(dst) and os.path.getmtime(dst) >= os.path.getmtime(src) - 1:
                            continue
                    except OSError:
                        pass
                    self.q.put(("put", kind, rel))
                    n += 1
        return n

    def _worker(self):
        while True:
            op, kind, rel = self.q.get()
            try:
                tgt = self.target()
                if not tgt:
                    raise OSError("내 드라이브 폴더를 찾지 못했습니다 (Google Drive 데스크톱이 켜져 있는지 확인)")
                dst = os.path.join(tgt, kind, rel)
                if op == "put":
                    src = os.path.join(KINDS[kind], rel)
                    if os.path.isfile(src):
                        os.makedirs(os.path.dirname(dst), exist_ok=True)
                        tmp = dst + ".tmp"
                        shutil.copy2(src, tmp)
                        os.replace(tmp, dst)
                        self.copied += 1
                elif op == "del" and os.path.isfile(dst):
                    # 드라이브 쪽도 지우지 않고 ComicPad\trash 로 옮긴다
                    tdir = os.path.join(tgt, "trash")
                    os.makedirs(tdir, exist_ok=True)
                    shutil.move(dst, os.path.join(tdir, time.strftime("%Y%m%d-%H%M%S_") + os.path.basename(dst)))
                self.last_ok = time.time()
                self.last_error = ""
            except OSError as e:
                self.last_error = f"{rel}: {e}"
            finally:
                self.q.task_done()

    def pull(self, name):
        """드라이브의 파일을 PC(G: data)로 가져오기. 덮어쓰기 전 작품은 버전 기록에 보관."""
        tgt = self.target()
        parts = name.replace("\\", "/").split("/", 1)
        if not tgt or len(parts) != 2 or parts[0] not in DRIVE_KINDS:
            raise ValueError("bad name")
        kind, rel = parts
        src = safe_join(os.path.join(tgt, kind), rel)
        dst = safe_join(KINDS[kind], rel)
        if not os.path.isfile(src):
            raise ValueError("드라이브에 파일이 없습니다")
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if kind == "projects" and dst.endswith(".cpad") and os.path.isfile(dst):
            backup_version(dst)
        shutil.copy2(src, dst)
        png = src[:-5] + ".png"
        if src.endswith(".cpad") and os.path.isfile(png):
            shutil.copy2(png, dst[:-5] + ".png")
        return dst

    def status(self):
        return {
            "on": self.on, "enabled": bool(self.conf().get("on")), "path": self.target(), "detected": find_my_drive(),
            "pending": self.q.unfinished_tasks, "copied": self.copied, "lastOk": self.last_ok, "lastError": self.last_error,
        }


def write_conf(conf):
    os.makedirs(os.path.dirname(SERVER_CONF), exist_ok=True)
    with open(SERVER_CONF, "w", encoding="utf-8") as fh:
        json.dump(conf, fh, ensure_ascii=False)


# ---------------------------------------------------------------------------
# 작업물 자동 백업 묶음: 하루 한 번 작품·에셋·글꼴·설정을 zip 하나로 (data/backups, 최근 N개)
# 구글 드라이브 사본이 켜져 있으면 드라이브의 ComicPad\백업 에도 한 부
# ---------------------------------------------------------------------------
import zipfile
BACKUP_DIR = os.path.join(DATA_DIR, "backups")
BACKUP_STATE = {"running": False, "last": None, "error": ""}
BACKUP_KINDS = ("projects", "assets", "fonts")


def backup_conf():
    c = read_conf().get("backup") or {}
    return {"on": c.get("on", True), "keep": max(1, int(c.get("keep", 14))), "every_h": max(1, int(c.get("every_h", 24))), "drive": c.get("drive", True)}


def list_backups(folder=BACKUP_DIR):
    if not os.path.isdir(folder):
        return []
    out = []
    for f in os.listdir(folder):
        if f.endswith(".zip"):
            st = os.stat(os.path.join(folder, f))
            out.append({"name": f, "size": st.st_size, "mtime": st.st_mtime})
    return sorted(out, key=lambda e: -e["mtime"])


def _rotate(folder, keep):
    for e in list_backups(folder)[keep:]:
        try:
            os.remove(os.path.join(folder, e["name"]))      # 오래된 자동 백업만 (ComicPad 가 만든 것)
        except OSError:
            pass


def make_backup():
    if BACKUP_STATE["running"]:
        return None
    BACKUP_STATE["running"] = True
    BACKUP_STATE["error"] = ""
    try:
        os.makedirs(BACKUP_DIR, exist_ok=True)
        name = time.strftime("ComicPad_백업_%Y-%m-%d_%H%M%S.zip")
        final = os.path.join(BACKUP_DIR, name)
        tmp = final + ".part"
        n = 0
        with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for kind in BACKUP_KINDS:
                base = KINDS[kind]
                for dp, _dirs, files in os.walk(base):
                    for f in files:
                        if f.endswith((".tmp", ".part")):
                            continue
                        full = os.path.join(dp, f)
                        z.write(full, kind + "/" + os.path.relpath(full, base).replace("\\", "/"))
                        n += 1
            # 설정 (휴대폰 연결 키·서버 설정은 빼고)
            cfg = KINDS["config"]
            for f in os.listdir(cfg) if os.path.isdir(cfg) else []:
                if f.endswith(".json") and f != "server.json":
                    z.write(os.path.join(cfg, f), "config/" + f)
        os.replace(tmp, final)
        c = backup_conf()
        _rotate(BACKUP_DIR, c["keep"])
        drive_copy = None
        if c["drive"] and DRIVE.on and DRIVE.target():
            ddir = os.path.join(DRIVE.target(), "백업")
            try:
                os.makedirs(ddir, exist_ok=True)
                shutil.copyfile(final, os.path.join(ddir, name))
                _rotate(ddir, c["keep"])
                drive_copy = ddir
            except OSError as e:
                BACKUP_STATE["error"] = f"드라이브에 복사하지 못했습니다: {e}"
        BACKUP_STATE["last"] = {"name": name, "at": time.time(), "size": os.path.getsize(final), "files": n, "drive": drive_copy}
        return final
    except Exception as e:  # noqa: BLE001 — 백업 실패는 알리기만
        BACKUP_STATE["error"] = str(e)
        try:
            os.remove(tmp)
        except Exception:
            pass
        return None
    finally:
        BACKUP_STATE["running"] = False


def backup_watch():
    """10분마다: 켜져 있고, 작품이 있고, 마지막 백업이 오래됐으면 새로"""
    time.sleep(20)
    while True:
        try:
            c = backup_conf()
            has_work = any(os.scandir(KINDS["projects"])) if os.path.isdir(KINDS["projects"]) else False
            last = list_backups()
            if c["on"] and has_work and (not last or time.time() - last[0]["mtime"] > c["every_h"] * 3600):
                make_backup()
        except Exception as e:  # noqa: BLE001
            BACKUP_STATE["error"] = str(e)
        time.sleep(600)


def backup_contents(name):
    full = safe_join(BACKUP_DIR, name)
    with zipfile.ZipFile(full) as z:
        return [{"name": i.filename, "size": i.file_size} for i in z.infolist() if i.filename.startswith("projects/") and i.filename.endswith(".cpad")]


def backup_restore(name, member):
    """백업 속 작품 하나를 '이름 (복원 날짜).cpad' 로 꺼냄 — 지금 파일은 덮어쓰지 않음"""
    full = safe_join(BACKUP_DIR, name)
    if not member.startswith("projects/") or not member.endswith(".cpad") or ".." in member:
        raise ValueError("작품 파일만 꺼낼 수 있습니다")
    with zipfile.ZipFile(full) as z:
        data = z.read(member)
    rel = member[len("projects/"):-5]
    stamp = name.replace("ComicPad_백업_", "").replace(".zip", "")
    out = safe_join(KINDS["projects"], f"{rel} (복원 {stamp}).cpad")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as fh:
        fh.write(data)
    return os.path.relpath(out, KINDS["projects"]).replace("\\", "/")


def backup_version(full):
    """프로젝트를 덮어쓰기 전에 이전 판을 versions 폴더에 보관 (작품마다 최근 30개)."""
    rel = os.path.relpath(full, KINDS["projects"])[:-5]
    vdir = os.path.join(KINDS["versions"], rel)
    os.makedirs(vdir, exist_ok=True)
    shutil.copy2(full, os.path.join(vdir, time.strftime("%Y%m%d-%H%M%S", time.localtime(os.path.getmtime(full))) + ".cpad"))
    old = sorted(f for f in os.listdir(vdir) if f.endswith(".cpad"))
    for f in old[:-30]:
        os.remove(os.path.join(vdir, f))


DRIVE = None


# ---------------------------------------------------------------------------
# 자동 업데이트: GitHub Releases 의 최신판을 확인하고, 서명이 맞는 업데이트 꾸러미만 받아 적용한다.
#  - 릴리스에 update.json(버전·파일·SHA-256) + update.json.sig(RSA-3072 서명) + ComicPad_update_<버전>.zip
#  - 서명은 release_pubkey.json 의 공개키로 검사 (비밀키는 만드는 사람 PC 에만, tools/make_release.py)
#  - data·python 폴더는 건드리지 않고, 바꾸기 전 파일은 data/updates 에 zip 으로 남긴다
# ---------------------------------------------------------------------------
import hashlib
import re
UPDATE_DIR = os.path.join(DATA_DIR, "updates")
UPDATE_REPO = "39nhs/comic_pad"
UPDATE_API = os.environ.get("COMICPAD_UPDATE_API", "https://api.github.com")   # 시험할 때만 바꿈
UPDATE_STATE = {"state": "idle", "done": 0, "total": 0, "error": "", "info": None, "applied": None}
UPDATE_KEEP = ("data/", "python/", "redist/", "dist/", ".git/")   # 업데이트가 절대 건드리지 않는 곳
OLD_SUFFIX = ".cpold"                                             # 쓰는 중이라 비켜 둔 옛 파일 (다음 시작 때 지움)
_SHA256_PREFIX = bytes.fromhex("3031300d060960864801650304020105000420")


def app_version():
    try:
        with open(os.path.join(ROOT, "VERSION"), encoding="utf-8") as f:
            return f.read().strip() or "0.0.0"
    except OSError:
        return "0.0.0"


def vtuple(v):
    nums = [int(x) for x in re.findall(r"\d+", str(v))[:3]]
    return tuple(nums + [0] * (3 - len(nums)))


def update_conf():
    u = read_conf().get("update") or {}
    return {"auto": u.get("auto", True), "repo": u.get("repo") or UPDATE_REPO, "checked": u.get("checked", 0)}


def release_pubkeys():
    """믿는 공개키 목록. 키를 바꿀 때는 옛 키로 서명한 업데이트에 새 키를 함께 넣어 보낸다 ("keys" 에 여러 개)."""
    try:
        with open(os.path.join(ROOT, "release_pubkey.json"), encoding="utf-8") as f:
            k = json.load(f)
        keys = k.get("keys") or [k]
        return [(int(x["n"], 16), int(x["e"])) for x in keys if int(x["n"], 16).bit_length() >= 3072]
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        return []


def release_pubkey():
    keys = release_pubkeys()
    return keys[0] if keys else None


def verify_any(data, sig):
    return any(rsa_verify(data, sig, n, e) for n, e in release_pubkeys())


def rsa_verify(data, sig, n, e):
    """RSASSA-PKCS1-v1_5 + SHA-256 서명 검사 (기대하는 블록 전체를 만들어 통째로 비교)."""
    k = (n.bit_length() + 7) // 8
    if len(sig) != k:
        return False
    s = int.from_bytes(sig, "big")
    if s >= n:
        return False
    em = pow(s, e, n).to_bytes(k, "big")
    t = _SHA256_PREFIX + hashlib.sha256(data).digest()
    want = b"\x00\x01" + b"\xff" * (k - len(t) - 3) + b"\x00" + t
    return secrets.compare_digest(em, want)


def exe_changed(man):
    """업데이트에 창 프로그램(ComicPad.exe) 변경이 들어 있나 (있으면 창을 껐다 켜야 적용)."""
    want = man.get("exe_sha256")
    exe = os.path.join(ROOT, "ComicPad.exe")
    if not want or not os.path.isfile(exe):
        return False
    h = hashlib.sha256()
    with open(exe, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest() != want


UPDATE_MAX = 600 * 1000 * 1000      # 업데이트 꾸러미 최대 크기


def _https_only(r):
    if UPDATE_API == "https://api.github.com" and not r.geturl().startswith("https://"):
        raise ValueError("업데이트 주소가 https 가 아니어서 받지 않습니다")


def gh_get(url, timeout=20, limit=2 * 1000 * 1000):
    req = urllib.request.Request(url, headers={"User-Agent": "ComicPad-Updater", "Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        _https_only(r)
        data = r.read(limit + 1)
    if len(data) > limit:
        raise ValueError("응답이 너무 큽니다")
    return data


def update_check():
    """최신 릴리스 확인. 서명이 맞는 update.json 만 믿는다."""
    repo = update_conf()["repo"]
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", repo):
        raise ValueError("저장소 이름이 이상합니다")
    pub = release_pubkey()
    if not pub:
        raise ValueError("업데이트 서명 키(release_pubkey.json)가 없어 업데이트를 확인할 수 없습니다")
    try:
        rel = json.loads(gh_get(f"{UPDATE_API}/repos/{repo}/releases/latest"))
        assets = {a.get("name"): a.get("browser_download_url") for a in rel.get("assets") or []}
        if "update.json" not in assets or "update.json.sig" not in assets:
            raise ValueError(f"최신 릴리스({rel.get('tag_name')})에 업데이트 꾸러미가 없습니다")
        man_bytes = gh_get(assets["update.json"])
        sig_text = gh_get(assets["update.json.sig"]).decode("ascii", "replace").strip()
    except urllib.error.HTTPError as e:
        if e.code == 404:
            raise ValueError("아직 올라온 업데이트가 없습니다")
        raise ValueError(f"GitHub 에 연결하지 못했습니다 ({e.code})")
    except OSError as e:
        raise ValueError(f"인터넷에 연결되어 있지 않거나 GitHub 에 연결하지 못했습니다 ({e})")
    try:
        sig = bytes.fromhex(sig_text)
    except ValueError:
        sig = b""
    if not verify_any(man_bytes, sig):
        raise ValueError("업데이트 서명이 맞지 않아 받지 않습니다 (만든 사람이 올린 파일이 아닐 수 있음)")
    man = json.loads(man_bytes)
    # 서명된 내용 확인: 이 프로그램용인지, 파일 이름·크기·해시 형식
    if man.get("product") not in (None, "ComicPad"):
        raise ValueError("다른 프로그램용 업데이트입니다")
    if man.get("file") not in assets or not re.fullmatch(r"ComicPad_update_[\w.-]+\.zip", str(man.get("file"))) \
            or not re.fullmatch(r"[0-9a-f]{64}", str(man.get("sha256", ""))) \
            or not (0 < int(man.get("size") or 0) <= UPDATE_MAX) or not re.fullmatch(r"\d+\.\d+\.\d+", str(man.get("version", ""))):
        raise ValueError("업데이트 정보가 올바르지 않습니다")
    cur = app_version()
    info = {"current": cur, "latest": str(man["version"]), "available": vtuple(man["version"]) > vtuple(cur),
            "size": int(man["size"]), "notes": str(man.get("notes") or "").strip()[:4000],
            "page": f"https://github.com/{repo}/releases/tag/v{man['version']}", "date": str(man.get("date") or ""),
            "launcher": exe_changed(man)}
    UPDATE_STATE.update(info=info, _man=man, _url=assets[man["file"]])
    conf = read_conf()
    u = conf.get("update") or {}
    u["checked"] = int(time.time())
    conf["update"] = u
    write_conf(conf)
    return info


def _update_files(z):
    """zip 안 파일 중 적용할 것 (이상한 경로·건드리면 안 되는 곳은 뺌)."""
    out = []
    for n in z.namelist():
        rel = n.replace("\\", "/")
        if rel.endswith("/") or rel.startswith("/") or ".." in rel.split("/") or ":" in rel:
            continue
        if rel.startswith(UPDATE_KEEP) or rel == "python_path.txt":
            continue
        out.append((n, rel))
    return out


def _put(src, dst):
    """dst 를 src 로 바꾼다. 실행 중이라 잠긴 파일(ComicPad.exe, DLL)은 이름을 바꿔 비키게 하고 넣는다."""
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    try:
        os.replace(src, dst)
        return
    except PermissionError:
        pass
    old, i = dst + OLD_SUFFIX, 0
    while os.path.exists(old):
        try:
            os.remove(old)
        except OSError:
            i += 1
            old = f"{dst}.{i}{OLD_SUFFIX}"
    os.rename(dst, old)
    os.replace(src, dst)


def update_install():
    st = UPDATE_STATE
    try:
        man, url = st.get("_man"), st.get("_url")
        if not man:
            raise ValueError("먼저 업데이트를 확인하세요")
        if vtuple(man["version"]) <= vtuple(app_version()):
            raise ValueError("이미 최신판입니다")
        os.makedirs(UPDATE_DIR, exist_ok=True)
        zpath = os.path.join(UPDATE_DIR, os.path.basename(man["file"]))
        # 1) 받기 + SHA-256 확인 (SHA-256 은 서명된 update.json 에 들어 있음)
        st.update(state="download", done=0, total=int(man.get("size") or 0), error="")
        h = hashlib.sha256()
        req = urllib.request.Request(url, headers={"User-Agent": "ComicPad-Updater"})
        try:
            with urllib.request.urlopen(req, timeout=60) as r, open(zpath + ".part", "wb") as f:
                _https_only(r)
                st["total"] = int(man["size"])
                while True:
                    b = r.read(1 << 18)
                    if not b:
                        break
                    st["done"] += len(b)
                    if st["done"] > int(man["size"]):      # 서명된 크기보다 크면 바로 중단
                        raise ValueError("받는 파일이 예상보다 커서 중단했습니다")
                    f.write(b)
                    h.update(b)
        except OSError as e:
            raise ValueError(f"업데이트를 받지 못했습니다 ({e})")
        if st["done"] != int(man["size"]) or h.hexdigest() != man["sha256"]:
            os.remove(zpath + ".part")
            raise ValueError("받은 파일이 손상되었습니다 (SHA-256 이 다름). 다시 시도하세요")
        os.replace(zpath + ".part", zpath)
        launcher = exe_changed(man)
        # 2) 따로 풀어 두기
        st.update(state="apply", done=0, total=0)
        stage = os.path.join(UPDATE_DIR, "staging")
        shutil.rmtree(stage, ignore_errors=True)
        with zipfile.ZipFile(zpath) as z:
            files = _update_files(z)
            for name, rel in files:
                out = safe_join(stage, rel)
                os.makedirs(os.path.dirname(out), exist_ok=True)
                with z.open(name) as src, open(out, "wb") as dst:
                    shutil.copyfileobj(src, dst)
        rels = sorted((r for _n, r in files), key=lambda r: r == "VERSION")   # VERSION 은 맨 끝에
        # 3) 바꾸기 전 판을 zip 으로 남기고 하나씩 바꾸기
        cur = app_version()
        keep = os.path.join(UPDATE_DIR, f"ComicPad_이전판_{cur}.zip")
        with zipfile.ZipFile(keep, "w", zipfile.ZIP_DEFLATED) as kz:
            for rel in rels:
                full = os.path.join(ROOT, rel)
                if os.path.isfile(full):
                    kz.write(full, rel)
        st["total"] = len(rels)
        done = []
        try:
            for rel in rels:
                _put(safe_join(stage, rel), safe_join(ROOT, rel))
                done.append(rel)
                st["done"] = len(done)
        except Exception:
            # 중간에 실패하면 바꾼 것을 되돌린다 (새로 생긴 파일은 그대로 둬도 무해)
            with zipfile.ZipFile(keep) as kz:
                have = set(kz.namelist())
                for rel in done:
                    if rel in have:
                        tmp = safe_join(stage, rel + ".restore")
                        with kz.open(rel) as src, open(tmp, "wb") as dst:
                            shutil.copyfileobj(src, dst)
                        _put(tmp, safe_join(ROOT, rel))
            raise
        shutil.rmtree(stage, ignore_errors=True)
        try:
            os.remove(zpath)
        except OSError:
            pass
        olds = sorted((f for f in os.listdir(UPDATE_DIR) if f.startswith("ComicPad_이전판_")),
                      key=lambda f: os.path.getmtime(os.path.join(UPDATE_DIR, f)))
        for f in olds[:-3]:   # 이전판은 최근 3개만
            try:
                os.remove(os.path.join(UPDATE_DIR, f))
            except OSError:
                pass
        try:   # Windows "설치된 앱" 목록의 버전도 맞춰 둠 (설치기로 설치한 경우만)
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Uninstall\ComicPad", 0,
                                winreg.KEY_READ | winreg.KEY_SET_VALUE) as k:
                loc = winreg.QueryValueEx(k, "InstallLocation")[0]
                if os.path.normcase(os.path.abspath(loc)) == os.path.normcase(ROOT):
                    winreg.SetValueEx(k, "DisplayVersion", 0, winreg.REG_SZ, str(man["version"]))
        except (OSError, ImportError):
            pass
        st.update(state="done", applied={"from": cur, "to": str(man["version"]), "files": len(rels),
                                         "launcher": launcher})
    except Exception as e:   # noqa: BLE001 — 사용자에게 그대로 보여 줌
        st.update(state="error", error=str(e) if isinstance(e, ValueError) else f"업데이트 실패: {e}")


def cleanup_old_files():
    """지난 업데이트 때 비켜 둔 옛 파일(.cpold) 지우기."""
    for dirpath, dirs, files in os.walk(ROOT):
        if os.path.abspath(dirpath) == ROOT:
            dirs[:] = [d for d in dirs if d not in ("data", "python", "redist", "dist", ".git")]
        for f in files:
            if f.endswith(OLD_SUFFIX):
                try:
                    os.remove(os.path.join(dirpath, f))
                except OSError:
                    pass


def safe_join(base, rel):
    rel = rel.replace("\\", "/").lstrip("/")
    path = os.path.normpath(os.path.join(base, rel))
    if os.path.commonpath([path, base]) != base:
        raise ValueError("bad path")
    return path


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    # ---- helpers -------------------------------------------------------
    def send_bytes(self, data, ctype="application/octet-stream", status=200):
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(data)

    def send_file(self, full):
        ext = os.path.splitext(full)[1].lower()
        size = os.path.getsize(full)
        self.send_response(200)
        self.send_header("Content-Type", MIME.get(ext, "application/octet-stream"))
        self.send_header("Content-Length", str(size))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        if self.command == "HEAD":
            return
        with open(full, "rb") as fh:
            while True:
                chunk = fh.read(1 << 20)
                if not chunk:
                    break
                self.wfile.write(chunk)

    def send_json(self, obj, status=200):
        self.send_bytes(json.dumps(obj, ensure_ascii=False).encode("utf-8"),
                        MIME[".json"], status)

    def read_body(self):
        n = int(self.headers.get("Content-Length", "0") or 0)
        return self.rfile.read(n) if n else b""

    def query(self):
        return {k: v[0] for k, v in urllib.parse.parse_qs(
            urllib.parse.urlparse(self.path).query).items()}

    def kind_path(self, q, write=False):
        kind = q.get("kind", "")
        if kind == "drive" and not write:
            base = DRIVE.target()
            if not base or not os.path.isdir(base):
                raise ValueError("드라이브의 ComicPad 폴더가 아직 없습니다")
            return base, safe_join(base, q.get("name", "")) if q.get("name") else None
        if kind not in KINDS or (write and kind not in WRITABLE):
            raise ValueError("bad kind")
        base = KINDS[kind]
        os.makedirs(base, exist_ok=True)
        return base, safe_join(base, q.get("name", "")) if q.get("name") else None

    @staticmethod
    def protected(full):
        """서버 설정·연결 키 (휴대폰 등 다른 기기는 못 바꿈)"""
        return os.path.normcase(os.path.abspath(full)) in {os.path.normcase(SERVER_CONF), os.path.normcase(KEY_FILE)}

    def is_local(self):
        return self.client_address[0] in LOCAL_ADDRS

    def allowed_hosts(self):
        hosts = {"127.0.0.1", "localhost", "[::1]"}
        if LAN:
            hosts.update(lan_ips_cached())
        return hosts

    def check_origin(self):
        """웹사이트가 이 PC 의 ComicPad 서버에 몰래 요청하는 것을 막는다.
        - Host: 이 PC 주소로 온 요청만 (다른 도메인 이름을 127.0.0.1 로 돌리는 DNS 리바인딩 차단)
        - 바꾸는 요청(POST/PUT/DELETE): 다른 사이트에서 온 것(Origin·Sec-Fetch-Site)은 거절"""
        host = (self.headers.get("Host") or "").strip().lower()
        name = host.rsplit(":", 1)[0] if not host.endswith("]") else host
        if name not in self.allowed_hosts():
            self.send_json({"error": "허용되지 않은 주소로 온 요청입니다"}, 403)
            return False
        if self.command in ("POST", "PUT", "DELETE"):
            site = (self.headers.get("Sec-Fetch-Site") or "").lower()
            origin = self.headers.get("Origin")
            bad = site == "cross-site"
            if origin is not None:
                u = urllib.parse.urlparse(origin)
                bad = bad or u.scheme != "http" or (u.hostname or "").lower() not in {h.strip("[]") for h in self.allowed_hosts()} \
                    or (u.port or 80) != PORT
            if bad:
                self.send_json({"error": "다른 사이트에서 온 요청은 받지 않습니다"}, 403)
                return False
        return True

    def check_access(self, path):
        """다른 기기(휴대폰 등)는 키가 있어야 작업물/저장에 접근 가능. PC 전용 기능은 로컬에서만."""
        if not self.check_origin():
            return False
        if self.is_local():
            return True
        if path in ("/api/open-folder", "/api/lan", "/api/info", "/api/drive/config", "/api/models/fetch", "/api/backup/now", "/api/backup/config", "/api/backup/restore") or path.startswith("/api/update") or path == "/api/shutdown":
            self.send_json({"error": "PC 에서만 쓸 수 있는 기능입니다"}, 403)
            return False
        if path.startswith("/api/") or path.startswith("/data/"):
            key = self.headers.get("X-ComicPad-Key") or self.query().get("k", "")
            if not secrets.compare_digest(key, lan_key()):
                self.send_json({"error": "연결 키가 맞지 않습니다. PC 의 QR 코드로 다시 접속하세요"}, 403)
                return False
        return True

    def ai_proxy(self, body=None):
        """이 PC(127.0.0.1/localhost)에서 돌아가는 이미지 AI 서버로만 요청을 넘긴다."""
        q = self.query()
        base, sub = q.get("base", "http://127.0.0.1:7860"), q.get("path", "/")
        u = urllib.parse.urlparse(base)
        if u.scheme not in ("http", "https") or u.hostname not in ("127.0.0.1", "localhost") or not sub.startswith("/"):
            return self.send_json({"error": "이 PC 의 AI 서버 주소만 쓸 수 있습니다"}, 400)
        req = urllib.request.Request(base.rstrip("/") + sub, data=body, method="POST" if body is not None else "GET",
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=600) as resp:
                return self.send_bytes(resp.read(), "application/json; charset=utf-8", resp.status)
        except urllib.error.HTTPError as e:
            return self.send_bytes(e.read() or b"{}", "application/json; charset=utf-8", e.code)
        except OSError as e:
            return self.send_json({"error": f"AI 서버에 연결하지 못했습니다 ({base}): {e}"}, 502)

    # ---- routes --------------------------------------------------------
    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        global last_seen
        last_seen = time.time()
        path = urllib.parse.unquote(urllib.parse.urlparse(self.path).path)
        if not self.check_access(path):
            return
        try:
            if path == "/api/ping":
                return self.send_json({"ok": True, "root": ROOT if self.is_local() else "", "remote": not self.is_local(), "version": app_version()})
            if path == "/api/update":
                st = {k: v for k, v in UPDATE_STATE.items() if not k.startswith("_")}
                return self.send_json({"current": app_version(), "conf": update_conf(), "signed": bool(release_pubkey()), **st})
            if path == "/api/drive":
                return self.send_json(DRIVE.status())
            if path == "/api/ai":
                return self.ai_proxy()
            if path == "/api/models":
                return self.send_json(model_status())
            if path == "/api/backup":
                return self.send_json({"conf": backup_conf(), "list": list_backups(), "state": BACKUP_STATE, "drive": DRIVE.on, "folder": BACKUP_DIR})
            if path == "/api/backup/contents":
                return self.send_json(backup_contents(self.query().get("name", "")))
            if path == "/api/info":
                urls = [f"http://{ip}:{PORT}/?k={lan_key()}" for ip in lan_ips()] if LAN else []
                return self.send_json({"lan": LAN, "port": PORT, "urls": urls, "ips": lan_ips()})
            if path == "/api/list":
                base, _ = self.kind_path(self.query())
                out = []
                for dirpath, _dirs, files in os.walk(base):
                    for f in files:
                        full = os.path.join(dirpath, f)
                        rel = os.path.relpath(full, base).replace("\\", "/")
                        st = os.stat(full)
                        out.append({"name": rel, "size": st.st_size, "mtime": st.st_mtime})
                out.sort(key=lambda e: -e["mtime"])
                return self.send_json(out)
            if path == "/api/stat":
                # 저장 충돌 확인용: 파일이 마지막으로 바뀐 시각
                _, full = self.kind_path(self.query())
                return self.send_json({"mtime": os.path.getmtime(full) if full and os.path.isfile(full) else None})
            if path == "/api/file":
                _, full = self.kind_path(self.query())
                if not full or not os.path.isfile(full):
                    return self.send_json({"error": "not found"}, 404)
                with open(full, "rb") as fh:
                    data = fh.read()
                ext = os.path.splitext(full)[1].lower()
                return self.send_bytes(data, MIME.get(ext, "application/octet-stream"))
            # static
            if path.startswith("/plugins/"):
                full = safe_join(PLUGIN_DIR, path[len("/plugins/"):])
            elif path.startswith("/data/"):
                full = safe_join(DATA_DIR, path[len("/data/"):])
            else:
                full = safe_join(APP_DIR, path if path != "/" else "index.html")
            if os.path.isdir(full):
                full = os.path.join(full, "index.html")
            if not os.path.isfile(full):
                return self.send_bytes(b"not found", "text/plain", 404)
            return self.send_file(full)
        except ValueError as e:
            return self.send_json({"error": str(e)}, 400)

    def do_PUT(self):
        global last_seen
        last_seen = time.time()
        path = urllib.parse.urlparse(self.path).path
        if not self.check_access(path):
            return
        try:
            if path != "/api/file":
                return self.send_json({"error": "not found"}, 404)
            _, full = self.kind_path(self.query(), write=True)
            if not full:
                raise ValueError("name required")
            if not self.is_local() and self.protected(full):
                return self.send_json({"error": "PC 에서만 바꿀 수 있는 파일입니다"}, 403)
            data = self.read_body()
            os.makedirs(os.path.dirname(full), exist_ok=True)
            # 프로젝트를 덮어쓰기 전에 이전 판을 versions 폴더에 보관 (작품마다 최근 30개)
            if self.query().get("kind") == "projects" and full.endswith(".cpad") and os.path.isfile(full):
                backup_version(full)
            tmp = full + ".tmp"
            with open(tmp, "wb") as fh:
                fh.write(data)
            os.replace(tmp, full)
            DRIVE.put(self.query().get("kind"), full)
            return self.send_json({"ok": True, "path": full})
        except ValueError as e:
            return self.send_json({"error": str(e)}, 400)

    def do_DELETE(self):
        """삭제는 data/trash 로 이동만 한다 (영구 삭제 없음)."""
        global last_seen
        last_seen = time.time()
        if not self.check_access(urllib.parse.urlparse(self.path).path):
            return
        try:
            _, full = self.kind_path(self.query(), write=True)
            if not full or not os.path.isfile(full):
                return self.send_json({"error": "not found"}, 404)
            if not self.is_local() and self.protected(full):
                return self.send_json({"error": "PC 에서만 지울 수 있는 파일입니다"}, 403)
            os.makedirs(TRASH_DIR, exist_ok=True)
            dest = os.path.join(TRASH_DIR, time.strftime("%Y%m%d-%H%M%S_") + os.path.basename(full))
            DRIVE.delete(self.query().get("kind"), full)
            shutil.move(full, dest)
            return self.send_json({"ok": True, "trash": dest})
        except ValueError as e:
            return self.send_json({"error": str(e)}, 400)

    def do_POST(self):
        global last_seen
        last_seen = time.time()
        path = urllib.parse.urlparse(self.path).path
        if not self.check_access(path):
            return
        try:
            if path == "/api/lan":
                # 휴대폰 연결 켜기/끄기: 설정을 저장하고 서버를 다시 시작
                global RELAUNCH
                on = self.query().get("on") == "1"
                conf = read_conf()
                conf["lan"] = on
                write_conf(conf)
                RELAUNCH = ["--no-browser", "--port", str(PORT)]
                self.send_json({"ok": True, "lan": on})
                threading.Timer(0.3, self.server.shutdown).start()
                return
            if path == "/api/ai":
                return self.ai_proxy(self.read_body())
            if path == "/api/models/fetch":
                mid = self.query().get("id", "")
                if mid not in model_registry() or model_registry()[mid].get("manual"):
                    return self.send_json({"error": "목록에 없는 모델이거나 직접 받아야 하는 모델입니다"}, 400)
                job = MODEL_JOBS.get(mid)
                if not job or job["state"] in ("done", "error"):
                    MODEL_JOBS[mid] = {"state": "download", "done": 0, "total": 0, "error": None}
                    threading.Thread(target=fetch_model, args=(mid,), daemon=True).start()
                return self.send_json({"ok": True})
            if path == "/api/update/check":
                return self.send_json(update_check())
            if path == "/api/update/install":
                if UPDATE_STATE["state"] not in ("download", "apply"):
                    UPDATE_STATE.update(state="download", done=0, total=0, error="")
                    threading.Thread(target=update_install, daemon=True).start()
                return self.send_json({"ok": True})
            if path == "/api/update/config":
                body = json.loads(self.read_body() or b"{}")
                conf = read_conf()
                u = conf.get("update") or {}
                if "auto" in body:
                    u["auto"] = bool(body["auto"])
                conf["update"] = u
                write_conf(conf)
                return self.send_json(update_conf())
            if path == "/api/shutdown":
                # ComicPad 창을 닫으면 창 프로그램이 부름: 서버(python)도 같이 끈다.
                # 휴대폰 연결이 켜져 있으면 휴대폰이 쓸 수 있게 그대로 둠. 백업·업데이트 중이면 끝난 뒤에.
                if LAN:
                    return self.send_json({"ok": False, "kept": "lan"})
                self.send_json({"ok": True})

                def stop(srv=self.server):
                    for _ in range(600):
                        if not BACKUP_STATE["running"] and UPDATE_STATE["state"] not in ("download", "apply"):
                            break
                        time.sleep(0.5)
                    srv.shutdown()
                threading.Thread(target=stop, daemon=True).start()
                return
            if path == "/api/update/restart":
                # 새 server.py 로 다시 시작 (창은 그대로, 화면만 새로 고침). RELAUNCH 는 위 /api/lan 의 global 선언
                RELAUNCH = ["--no-browser", "--port", str(PORT)]
                self.send_json({"ok": True})
                threading.Timer(0.3, self.server.shutdown).start()
                return
            if path == "/api/backup/now":
                if not BACKUP_STATE["running"]:
                    threading.Thread(target=make_backup, daemon=True).start()
                return self.send_json({"ok": True})
            if path == "/api/backup/config":
                body = json.loads(self.read_body() or b"{}")
                conf = read_conf()
                b = conf.get("backup") or {}
                for k in ("on", "keep", "every_h", "drive"):
                    if k in body:
                        b[k] = body[k]
                conf["backup"] = b
                write_conf(conf)
                return self.send_json(backup_conf())
            if path == "/api/backup/restore":
                q = self.query()
                return self.send_json({"ok": True, "name": backup_restore(q.get("name", ""), q.get("file", ""))})
            if path == "/api/drive/config":
                q = self.query()
                p = q.get("path")
                if p is not None and p and not os.path.isdir(os.path.dirname(os.path.abspath(p))):
                    raise ValueError("그 위치를 찾을 수 없습니다")
                DRIVE.set(q.get("on") == "1", p if p is not None else None)
                return self.send_json(DRIVE.status())
            if path == "/api/drive/sync":
                return self.send_json({"queued": DRIVE.sync_all(), **DRIVE.status()})
            if path == "/api/drive/pull":
                return self.send_json({"ok": True, "path": DRIVE.pull(self.query().get("name", ""))})
            if path == "/api/open-folder":
                base, _ = self.kind_path(self.query())
                if hasattr(os, "startfile"):
                    os.startfile(base)
                return self.send_json({"ok": True, "path": base})
            return self.send_json({"error": "not found"}, 404)
        except ValueError as e:
            return self.send_json({"error": str(e)}, 400)


def find_browser():
    candidates = [
        os.path.expandvars(r"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
        os.path.expandvars(r"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
        os.path.expandvars(r"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
        os.path.expandvars(r"%LocalAppData%\Google\Chrome\Application\chrome.exe"),
    ]
    for c in candidates:
        if os.path.isfile(c):
            return c
    return None


def open_window(url):
    # 전용 창(ComicPad.exe, WebView2)이 있으면 그것으로: Edge 창·브라우저 알림 없이
    app_exe = os.path.join(ROOT, "ComicPad.exe")
    if os.name == "nt" and os.path.isfile(app_exe):
        try:
            subprocess.Popen([app_exe, "--port", str(PORT)], cwd=ROOT)
            return
        except OSError:
            pass
    exe = find_browser()
    if exe:
        subprocess.Popen([exe, f"--app={url}", "--start-maximized"])
    else:
        webbrowser.open(url)


def already_running(url):
    try:
        with urllib.request.urlopen(url + "api/ping", timeout=1) as r:
            return json.loads(r.read()).get("ok", False)
    except Exception:
        return False


def idle_watch(server):
    while True:
        time.sleep(30)
        if time.time() - last_seen > IDLE_EXIT_SEC:
            server.shutdown()
            return


def make_precache():
    """서비스 워커가 미리 받아 둘 앱 파일 목록 (서버가 꺼져 있어도 앱이 열리도록)."""
    import hashlib
    files, h = [], hashlib.sha1()
    for dirpath, _dirs, names in os.walk(APP_DIR):
        for n in sorted(names):
            full = os.path.join(dirpath, n)
            rel = os.path.relpath(full, APP_DIR).replace("\\", "/")
            if rel in ("precache.json", "sw.js") or rel.startswith("vendor/ai/"):
                continue                     # AI 실행기(수십 MB)는 미리 받지 않음 (AI 는 서버 모드에서만)
            st = os.stat(full)
            files.append(urllib.parse.quote(rel))
            h.update(f"{rel}:{st.st_size}:{int(st.st_mtime)}".encode())
    files.sort()
    data = json.dumps({"version": h.hexdigest()[:16], "files": ["./"] + files}, ensure_ascii=False, indent=0)
    path = os.path.join(APP_DIR, "precache.json")
    try:
        with open(path, encoding="utf-8") as fh:
            if fh.read() == data:
                return
    except OSError:
        pass
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(data)


def main():
    global LAN, DRIVE
    for k, d in list(KINDS.items()) + [("trash", TRASH_DIR)]:
        if k != "colorprofiles":
            os.makedirs(d, exist_ok=True)
    try:
        make_precache()
    except OSError as e:
        print("precache.json 을 만들지 못했습니다:", e)
    if "--precache-only" in sys.argv:
        return
    DRIVE = DriveSync()
    LAN = "--lan" in sys.argv or bool(read_conf().get("lan"))
    url = f"http://127.0.0.1:{PORT}/"
    no_browser = "--no-browser" in sys.argv
    if already_running(url):
        if not no_browser:
            open_window(url)
        return
    host = "0.0.0.0" if LAN else "127.0.0.1"
    try:
        server = http.server.ThreadingHTTPServer((host, PORT), Handler)
    except OSError:
        # 다시 시작 직후 포트가 아직 안 풀렸으면 잠깐 기다렸다가 재시도
        time.sleep(1.5)
        server = http.server.ThreadingHTTPServer((host, PORT), Handler)
    server.daemon_threads = True
    # 기본은 계속 실행 (절전 후 저장 실패 방지). --idle-exit 를 주면 15분 무응답 시 종료
    if "--idle-exit" in sys.argv:
        threading.Thread(target=idle_watch, args=(server,), daemon=True).start()
    if not no_browser:
        threading.Timer(0.3, open_window, args=(url,)).start()
    print(f"ComicPad: {url}  (data: {DATA_DIR})")
    threading.Thread(target=backup_watch, daemon=True).start()
    threading.Thread(target=cleanup_old_files, daemon=True).start()
    if DRIVE.on:
        threading.Thread(target=DRIVE.sync_all, daemon=True).start()
        print(f"  구글 드라이브 복사: {DRIVE.target()}")
    if LAN:
        for ip in lan_ips():
            print(f"  휴대폰/태블릿: http://{ip}:{PORT}/?k={lan_key()}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    server.server_close()
    if RELAUNCH is not None:
        subprocess.Popen([sys.executable, os.path.abspath(__file__)] + RELAUNCH, cwd=ROOT)


if __name__ == "__main__":
    main()
