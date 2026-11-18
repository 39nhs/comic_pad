"""업데이트 꾸러미 시험 (올리지 않음): 1.1.3 판의 업데이트 프로그램(server.py)으로 임시 설치 폴더에 적용해 본다.

    python tools/release_sim.py <ComicPad_update_X.zip> [--launch] [--keep] [--work 폴더]
    (python tools/make_release.py --native --dry-run 이 꾸러미를 만든 뒤 저절로 부름)

하는 일 (모두 임시 폴더 안에서, 진짜 설치·data·%APPDATA%·서명 키는 건드리지 않음):
 1) 1.1.3 설치 폴더 흉내: git 의 1.1.3 커밋(server.py 업데이트 코드가 그 판)에서 프로그램 파일을 꺼내고,
    ComicPad.exe 자리에는 "실행 중인 창 프로그램" 대신 잠깐 자는 작은 exe 를 실제로 띄워 파일을 잠근다.
    data(작품·설정·자동 저장)·python·redist 에 시험 파일을 넣고 지문(SHA-256)을 적어 둔다.
 2) 버리는 시험용 RSA 키로 update.json 에 서명하고(진짜 키 아님), 127.0.0.1 에 가짜 GitHub 를 띄운다.
 3) 1.1.3 server.py 의 update_check → update_install 을 그대로 부른다 (서명·크기·SHA-256·UPDATE_KEEP·잠긴 파일 비키기).
 4) 확인: 새 VERSION·server.py·웹 앱·새 창 프로그램·네이티브 판 파일이 들어왔는지, data·python·redist 가 그대로인지,
    잠긴 옛 ComicPad.exe 가 .cpold 로 비켜졌는지, 이전 판 zip 이 남았는지.
 5) 새 창 프로그램(ComicPad.exe --which)으로 판 고르기: 스위치 없음 → 웹, native → 네이티브, --web → 웹, 파일 없음 → 웹.
    (스위치는 새 server.py 의 app_switch 로 써서 웹 판 쪽 코드도 함께 시험)
 6) --launch: 스위치를 native 로 두고 창 프로그램을 정말 켠다 → 네이티브 판이 임시 설정(COMICPAD_SETTINGS)으로 떠서
    화면을 찍고(COMICPAD_UI_SHOT) 끝나는지, 처음 켤 때 웹 판 설정을 가져왔는지. 띄운 프로세스는 이 시험이 띄운 것만(PID) 정리.
 7) 1.1.3 의 되돌리기(update_rollback)로 원래대로 돌아가는지.
끝 코드 = 실패 수.
"""
import argparse
import hashlib
import http.server
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OLD_COMMIT = "1ec433e5db5f308941946ba1af8379d1cd4acce3"   # ComicPad 1.1.3 ("ComicPad 1.1.3: 펜 커서·기본 필압")
OLD_PATHS = ["app", "plugins", "server.py", "VERSION", "release_pubkey.json", "LICENSE", "NOTICE", "README.md", "ComicPad.ico",
             "launcher", "installer", "tools", "ComicPad 실행.bat", "ComicPad 설치.bat", "ComicPad 제거.bat"]
REPO = "39nhs/comic_pad"

RESULTS = []
# data 비교에서 뺄 곳: 업데이트가 쓰는 updates·server.json, 시험이 쓰는 스위치 app.json, --launch 때 네이티브 판의 하루 한 번 자동 백업
DATA_SKIP = ("updates", "backups", "config/server.json", "config/app.json")


def ok(cond, name, detail=""):
    RESULTS.append((bool(cond), name, detail))
    print(("PASS " if cond else "FAIL ") + name + (f"  ({detail})" if detail else ""), flush=True)
    return bool(cond)


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def tree_hashes(base, skip=()):
    out = {}
    for dp, _d, fs in os.walk(base):
        for fn in fs:
            full = os.path.join(dp, fn)
            rel = os.path.relpath(full, base).replace("\\", "/")
            if any(rel == s or rel.startswith(s.rstrip("/") + "/") for s in skip):
                continue
            out[rel] = sha(full)
    return out


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ---- 1) 1.1.3 설치 폴더 흉내 ---------------------------------------------------------------
def make_old_install(inst):
    os.makedirs(inst)
    try:
        tar = subprocess.run(["git", "archive", "--format=tar", OLD_COMMIT, "--", *OLD_PATHS], cwd=ROOT, capture_output=True, check=True).stdout
        with tarfile.open(fileobj=io.BytesIO(tar)) as t:
            t.extractall(inst, filter="data") if sys.version_info >= (3, 12) else t.extractall(inst)
        src = "git " + OLD_COMMIT[:7]
    except (OSError, subprocess.CalledProcessError) as e:
        # git 이 없으면: 지금 파일로 흉내 (server.py 의 업데이트 적용 코드는 1.1.3 과 같음)
        print("  git 1.1.3 을 꺼내지 못해 지금 파일로 흉내 냅니다:", e)
        for p in OLD_PATHS:
            s = os.path.join(ROOT, p)
            if os.path.isdir(s):
                shutil.copytree(s, os.path.join(inst, p), ignore=shutil.ignore_patterns("__pycache__"))
            elif os.path.isfile(s):
                shutil.copy2(s, os.path.join(inst, p))
        with open(os.path.join(inst, "VERSION"), "w", encoding="utf-8") as f:
            f.write("1.1.3\n")
        src = "지금 파일"
    with open(os.path.join(inst, "VERSION"), encoding="utf-8") as f:
        ok(f.read().strip() == "1.1.3", "1.1.3 설치 폴더 흉내", src)
    # WebView2 파일 (1.1.3 설치에 있는 것)
    lib = os.path.join(ROOT, "launcher", "lib", "net462")
    for dll in ("Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll"):
        if os.path.isfile(os.path.join(lib, dll)):
            shutil.copy2(os.path.join(lib, dll), os.path.join(inst, dll))
    # 작업물·런타임 (업데이트가 건드리면 안 되는 곳)
    data = os.path.join(inst, "data")
    for d in ("projects", "autosave", "config", "assets", "versions/시험"):
        os.makedirs(os.path.join(data, d), exist_ok=True)
    pt = os.path.join(ROOT, "native", "parity_test.cpad")
    if os.path.isfile(pt):
        shutil.copy2(pt, os.path.join(data, "projects", "시험.cpad"))
        shutil.copy2(pt, os.path.join(data, "autosave", "시험.cpad"))
    web_settings = {"pressureGamma": 1.3, "penFeel": {"filter": "euro", "pressureSmooth": 0, "startRamp": False, "predict": True},
                    "keys": {"view.grid": "ctrl+alt+g"}, "palette": ["#000000", "#336699"], "recentColors": ["#336699"],
                    "setupDone": "clip", "ui": {"mode": "simple"}, "nativePen": True, "brushCursor": "nib"}
    with open(os.path.join(data, "config", "settings.json"), "w", encoding="utf-8") as f:
        json.dump(web_settings, f, ensure_ascii=False, indent=1)
    with open(os.path.join(data, "config", "server.json"), "w", encoding="utf-8") as f:
        json.dump({"update": {"auto": True}, "backup": {"on": True, "keep": 7}}, f)
    with open(os.path.join(data, "versions", "시험", "20260101-000000.cpad"), "w", encoding="utf-8") as f:
        f.write("{}")
    for d, fn in (("python", "pythonw.exe"), ("redist", "vc_redist.x64.exe")):
        os.makedirs(os.path.join(inst, d))
        with open(os.path.join(inst, d, fn), "wb") as f:
            f.write(os.urandom(4096))
    with open(os.path.join(inst, "python_path.txt"), "w", encoding="utf-8") as f:
        f.write(r"C:\Python\pythonw.exe")
    return web_settings


def make_sleeper(path):
    """실행 중인 옛 창 프로그램 흉내: 잠깐 자는 .NET Framework exe (실행 파일이라 Windows 가 덮어쓰기를 막음)"""
    csc = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe")
    if not os.path.isfile(csc):
        csc = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe")
    src = path + ".cs"
    with open(src, "w", encoding="utf-8") as f:
        f.write("class P { static void Main() { System.Threading.Thread.Sleep(300000); } }")
    subprocess.run([csc, "/nologo", "/target:winexe", f"/out:{path}", src], check=True, capture_output=True)
    os.remove(src)


# ---- 2) 가짜 GitHub ------------------------------------------------------------------------
def serve(files):
    """files: {이름: 바이트}. /repos/<repo>/releases/latest 와 /a/<이름>"""
    class H(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def do_GET(self):
            if self.path == f"/repos/{REPO}/releases/latest":
                base = f"http://127.0.0.1:{self.server.server_address[1]}/a/"
                body = json.dumps({"tag_name": "v-sim", "assets": [{"name": n, "browser_download_url": base + n} for n in files]}).encode()
            elif self.path.startswith("/a/") and self.path[3:] in files:
                body = files[self.path[3:]]
            else:
                self.send_response(404)
                self.end_headers()
                return
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


# ---- 5) 창 프로그램 판 고르기 ---------------------------------------------------------------
def which(inst, *args):
    out = os.path.join(inst, "_which.txt")
    if os.path.exists(out):
        os.remove(out)
    r = subprocess.run([os.path.join(inst, "ComicPad.exe"), *args, "--which", out], timeout=60)
    try:
        with open(out, encoding="utf-8") as f:
            lines = f.read().split("\n")
        return lines[0], lines[1], lines[2] if len(lines) > 2 else "", r.returncode
    except OSError:
        return None, None, None, r.returncode


def own_children(parent_pid):
    """이 시험이 띄운 프로세스의 자식 PID (이름이 아니라 부모 PID 로만 찾음)"""
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command",
                            f"(Get-CimInstance Win32_Process -Filter 'ParentProcessId={int(parent_pid)}').ProcessId"],
                           capture_output=True, text=True, timeout=30)
        return [int(x) for x in r.stdout.split() if x.strip().isdigit()]
    except (OSError, subprocess.SubprocessError):
        return []


def kill_pid(pid):
    subprocess.run(["taskkill", "/PID", str(int(pid)), "/F"], capture_output=True)


def launch_native(inst, tmp, web_settings, new_srv):
    """스위치 native → 창 프로그램을 정말 켬 → 네이티브 판이 화면을 찍고 끝나는지, 웹 판 설정을 가져왔는지"""
    new_srv.app_switch("native")
    shot, settings = os.path.join(tmp, "native_shot.png"), os.path.join(tmp, "native-settings.json")
    env = dict(os.environ, COMICPAD_SETTINGS=settings, COMICPAD_UI_SHOT=shot)
    env.pop("COMICPAD_DATA", None)        # 네이티브 판이 설치 폴더의 data 를 스스로 찾는지
    p = subprocess.Popen([os.path.join(inst, "ComicPad.exe")], env=env)
    kids = []
    for _ in range(40):                    # 창 프로그램은 네이티브 판을 켜고 4초쯤 지켜본 뒤 끝남
        if not kids:
            kids = own_children(p.pid)
        if p.poll() is not None:
            break
        time.sleep(0.5)
    ok(p.poll() == 0, "창 프로그램: 네이티브 판을 켜고 끝냄 (서버·웹 창 없이)", f"끝 코드 {p.poll()}")
    for _ in range(120):
        if os.path.isfile(shot):
            break
        time.sleep(0.5)
    time.sleep(1)
    ok(os.path.isfile(shot) and os.path.getsize(shot) > 10000, "네이티브 판이 설치 폴더 구조에서 켜짐 (화면 찍기)", shot)
    for k in kids:                         # 이 시험이 띄운 네이티브 판이 남아 있으면 그 PID 만 정리
        kill_pid(k)
    if p.poll() is None:
        kill_pid(p.pid)
    try:
        with open(settings, encoding="utf-8") as f:
            ns = json.load(f)
    except (OSError, ValueError):
        ns = {}
    ok(ns.get("pressureGamma") == web_settings["pressureGamma"] and ns.get("keys") == web_settings["keys"] and ns.get("setupDone") == "clip"
       and "nativePen" not in ns and isinstance(ns.get("webImport"), dict), "처음 켤 때 웹 판 설정(data/config/settings.json) 가져옴",
       ", ".join(k for k in ("pressureGamma", "keys", "palette", "setupDone", "webImport") if k in ns))
    ok(ns.get("backup", {}).get("keep") == 7, "server.json 의 자동 백업 설정도 가져옴")


def simulate(zpath, man=None, launch=False, keep=False, work=None):
    RESULTS.clear()
    tmp = tempfile.mkdtemp(prefix="comicpad_sim_", dir=work)
    inst = os.path.join(tmp, "ComicPad")
    sleeper = None
    print(f"[시험] 임시 설치 폴더: {inst}")
    try:
        web_settings = make_old_install(inst)
        exe = os.path.join(inst, "ComicPad.exe")
        make_sleeper(exe)
        old_exe_sha = sha(exe)
        sleeper = subprocess.Popen([exe])   # 실행 중 → 파일 잠김
        time.sleep(0.5)
        before = tree_hashes(os.path.join(inst, "data"), skip=DATA_SKIP)
        before_rt = {d: tree_hashes(os.path.join(inst, d)) for d in ("python", "redist")}
        with open(os.path.join(inst, "python_path.txt"), "rb") as f:
            pp = f.read()

        # 꾸러미 자체 검사
        with zipfile.ZipFile(zpath) as z:
            names = z.namelist()
            ver = z.read("VERSION").decode().strip()
            zexe = hashlib.sha256(z.read("ComicPad.exe")).hexdigest() if "ComicPad.exe" in names else None
        size = os.path.getsize(zpath)
        ok(size <= 600 * 1000 * 1000, "꾸러미 크기 ≤ 600MB (1.1.3 UPDATE_MAX)", f"{size / 1e6:.1f}MB")
        ok(not any(n.startswith(("data/", "python/", "redist/", "dist/", ".git/")) or ".." in n.split("/") for n in names), "꾸러미에 data·python·redist·dist 없음")
        ok("native/ComicPadNative/ComicPadNative.exe" in names, "꾸러미에 네이티브 판 (native/ComicPadNative)")
        ok(zexe is not None, "꾸러미에 새 창 프로그램 ComicPad.exe")
        ok(not any(n.startswith("native/ComicPadNative/") and n.endswith(".cs") for n in names), "네이티브 판 소스는 안 들어감")

        # 서명 (버리는 시험 키)
        sys.path.insert(0, os.path.join(ROOT, "tools"))
        import make_release as mr   # noqa: E402
        print("[시험] 시험용 RSA-3072 키 만드는 중 (진짜 서명 키 아님)…", flush=True)
        key = mr.gen_key()
        with open(os.path.join(inst, "release_pubkey.json"), "w", encoding="utf-8") as f:
            json.dump({"alg": "RSA-3072 PKCS#1 v1.5 SHA-256", "n": key["n"], "e": key["e"], "note": "release_sim 시험 키"}, f)
        m = dict(man or {})
        m.update({"product": "ComicPad", "version": ver, "file": os.path.basename(zpath), "size": size, "sha256": sha(zpath)})
        m.setdefault("exe_sha256", zexe or "")
        m.setdefault("notes", "시험")
        m.pop("dry_run", None)
        man_bytes = json.dumps(m, ensure_ascii=False, indent=1).encode("utf-8")
        with open(zpath, "rb") as f:
            files = {"update.json": man_bytes, "update.json.sig": mr.rsa_sign(man_bytes, key).hex().encode(), os.path.basename(zpath): f.read()}
        gh = serve(files)
        os.environ["COMICPAD_UPDATE_API"] = f"http://127.0.0.1:{gh.server_address[1]}"

        # 3) 1.1.3 업데이트 프로그램
        old = load_module("comicpad_old_server", os.path.join(inst, "server.py"))
        ok(old.app_version() == "1.1.3" and not hasattr(old, "app_switch"), "1.1.3 server.py 를 불러옴 (업데이트 코드 그대로)")
        info = old.update_check()
        ok(info["available"] and info["latest"] == ver, "1.1.3 update_check: 서명·이름·크기·버전 통과", f"{info['current']} → {info['latest']}, 창 프로그램 바뀜={info['launcher']}")
        old.update_install()
        st = old.UPDATE_STATE
        ok(st["state"] == "done", "1.1.3 update_install: 받기·SHA-256·적용", st.get("error") or f"파일 {st.get('applied', {}).get('files')}개")
        gh.shutdown()

        # 4) 결과
        with open(os.path.join(inst, "VERSION"), encoding="utf-8") as f:
            ok(f.read().strip() == ver, "VERSION", ver)
        ok(sha(os.path.join(inst, "server.py")) == sha(os.path.join(ROOT, "server.py")), "새 server.py")
        ok(os.path.isfile(os.path.join(inst, "app", "js", "ui", "appswitch.js")), "새 웹 앱 (네이티브 판 써 보기 메뉴)")
        nexe = os.path.join(inst, "native", "ComicPadNative", "ComicPadNative.exe")
        ok(os.path.isfile(nexe), "네이티브 판 설치됨", "native/ComicPadNative/ComicPadNative.exe")
        ok(zexe and sha(exe) == zexe, "새 창 프로그램 ComicPad.exe")
        olds = [f for f in os.listdir(inst) if f.startswith("ComicPad.exe") and f.endswith(".cpold")]
        ok(olds and sha(os.path.join(inst, olds[0])) == old_exe_sha, "실행 중이라 잠긴 옛 ComicPad.exe 는 .cpold 로 비킴", ", ".join(olds))
        after = tree_hashes(os.path.join(inst, "data"), skip=DATA_SKIP)
        ok(after == before, "data (작품·설정·자동 저장·버전 기록) 그대로", f"{len(before)}개 파일")
        ok(all(tree_hashes(os.path.join(inst, d)) == before_rt[d] for d in before_rt), "python·redist 그대로")
        with open(os.path.join(inst, "python_path.txt"), "rb") as f:
            ok(f.read() == pp, "python_path.txt 그대로")
        ok(os.path.isfile(os.path.join(inst, "data", "updates", "ComicPad_이전판_1.1.3.zip")), "이전 판(1.1.3) zip 보관")
        ok(not os.path.isdir(os.path.join(inst, "data", "updates", "staging")), "임시 풀기 폴더 정리")

        # 5) 판 고르기 (새 창 프로그램 + 새 server.py)
        new = load_module("comicpad_new_server", os.path.join(inst, "server.py"))
        ai = new.app_info()
        ok(ai["native"]["present"] and ai["launcher"] and ai["app"] == "web", "새 server.py: 네이티브 판 있음·창 프로그램이 스위치를 읽는 판·지금은 웹")
        if os.name == "nt":
            r = which(inst)
            ok(r[0] == "web", "스위치 없음 → 웹 판 (지금과 똑같이)", f"{r[0]} / {r[1]}")
            new.app_switch("native")
            with open(os.path.join(inst, "data", "config", "app.json"), encoding="utf-8") as f:
                ok(json.load(f).get("app") == "native", "웹 판이 쓰는 스위치 data/config/app.json")
            r = which(inst)
            ok(r[0] == "native" and os.path.normcase(r[2]) == os.path.normcase(nexe), "스위치 native → 네이티브 판", f"{r[0]} / {r[1]} / {r[2]}")
            r = which(inst, "--web")
            ok(r[0] == "web" and r[1] == "flag", "--web → 이번만 웹 판 (비상구)", f"{r[0]} / {r[1]}")
            os.rename(nexe, nexe + ".hide")
            r = which(inst)
            os.rename(nexe + ".hide", nexe)
            ok(r[0] == "web" and r[1] == "missing", "네이티브 판 파일이 없으면 웹 판", f"{r[0]} / {r[1]}")
            new.app_switch("web")
            r = which(inst)
            ok(r[0] == "web" and r[1] == "switch", "웹 판으로 돌아가기 (스위치 web) → 웹 판", f"{r[0]} / {r[1]}")
            try:
                os.rename(nexe, nexe + ".hide")
                try:
                    new.app_switch("native")
                    ok(False, "네이티브 판 파일이 없으면 웹 판에서 바꾸기 거절")
                except ValueError:
                    ok(True, "네이티브 판 파일이 없으면 웹 판에서 바꾸기 거절")
            finally:
                if os.path.isfile(nexe + ".hide"):
                    os.rename(nexe + ".hide", nexe)
                new.app_switch("web")
            if launch:
                launch_native(inst, tmp, web_settings, new)
                new.app_switch("web")

        # 7) 1.1.3 의 되돌리기
        old.UPDATE_STATE.update(state="idle")
        old.update_rollback()
        ok(old.UPDATE_STATE["state"] == "done", "1.1.3 update_rollback", old.UPDATE_STATE.get("error", ""))
        with open(os.path.join(inst, "VERSION"), encoding="utf-8") as f:
            ok(f.read().strip() == "1.1.3", "되돌린 뒤 VERSION 1.1.3")
        ok(sha(exe) == old_exe_sha, "되돌린 뒤 옛 창 프로그램 (스위치를 읽지 않으므로 늘 웹 판)")
        ok(tree_hashes(os.path.join(inst, "data"), skip=DATA_SKIP) == before, "되돌린 뒤에도 data 그대로")
    finally:
        if sleeper and sleeper.poll() is None:
            kill_pid(sleeper.pid)
            sleeper.wait(10)
        os.environ.pop("COMICPAD_UPDATE_API", None)
        if not keep:
            time.sleep(0.5)
            shutil.rmtree(tmp, ignore_errors=True)
        else:
            print(f"[시험] 남겨 둠: {tmp}")
    fails = [r for r in RESULTS if not r[0]]
    print(f"[시험] {len(RESULTS) - len(fails)}/{len(RESULTS)} 통과" + (f", 실패 {len(fails)}" if fails else ""))
    return len(fails)


def main():
    ap = argparse.ArgumentParser(description="업데이트 꾸러미를 1.1.3 업데이트 코드로 임시 설치 폴더에 적용해 보는 시험")
    ap.add_argument("zip")
    ap.add_argument("--update-json", help="make_release --dry-run 의 update.json (native 등 더 붙은 정보)")
    ap.add_argument("--launch", action="store_true", help="창 프로그램 → 네이티브 판을 정말 켜 보기 (화면 찍고 끝남)")
    ap.add_argument("--keep", action="store_true", help="임시 폴더 남기기")
    ap.add_argument("--work", help="임시 폴더를 만들 곳")
    a = ap.parse_args()
    man = None
    if a.update_json:
        with open(a.update_json, encoding="utf-8") as f:
            man = json.load(f)
    sys.exit(simulate(a.zip, man, a.launch, a.keep, a.work))


if __name__ == "__main__":
    main()
