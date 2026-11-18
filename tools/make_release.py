"""ComicPad 업데이트 꾸러미 만들기 (+ 원하면 GitHub 릴리스로 올리기).

처음 한 번:  python tools/make_release.py --genkey
    서명 비밀키를 %USERPROFILE%\\.comicpad\\release_key.json 에 만들고 (저장소에 넣지 말 것, 따로 백업!)
    공개키를 release_pubkey.json 으로 저장 (이건 앱과 함께 배포).
릴리스마다:
    1) VERSION 파일의 번호를 올린다 (예: 1.0.0 -> 1.0.1)
    2) python tools/make_release.py --notes "바뀐 점"            -> dist/release/ 에 꾸러미·update.json·서명
    3) python tools/make_release.py --notes "바뀐 점" --upload   -> GitHub 릴리스 v<버전> 로 올림 (gh 필요)
       --installer 를 붙이면 dist 의 설치파일도 함께 올림, --with-models 는 내장 AI 모델도 꾸러미에 넣음
       --native 를 붙이면 네이티브 판(native/ComicPadNative, .NET 8)을 dotnet publish 해서 꾸러미의 native/ComicPadNative/ 에 넣음
         (기본: 자체 포함(self-contained) — PC 에 .NET 이 없어도 바로 실행. --native-framework 면 .NET 8 데스크톱 런타임이 필요한 작은 판)
         같은 결과가 dist/native/ComicPadNative 에도 남아 installer\build_setup.ps1 -Native 가 설치파일에 넣을 수 있음
         --native 는 창 프로그램(ComicPad.exe)도 launcher\\build.ps1 로 다시 만들어 넣음 (웹 판 ↔ 네이티브 판 스위치를 읽는 판)
       --notes-file 파일 로 바뀐 점을 파일에서 (예: native/릴리스_노트_1.2.0_초안.md)
시험(올리지 않음):  python tools/make_release.py --native --dry-run --out <임시 폴더>
    서명 키·앱 파일 목록 갱신·업로드 없이 꾸러미 zip 과 서명 없는 update.json 만 만들어 배치를 확인 (기본 동작은 그대로)
    이어서 tools/release_sim.py 가 그 zip 을 1.1.3 판 server.py 의 업데이트 코드로 임시 설치 폴더에 적용해 보고
    (data·python 보존, 잠긴 ComicPad.exe 비키기, 새 창 프로그램의 웹/네이티브 판 고르기, 되돌리기) 결과를 알려 줌.
    --sim-launch 면 네이티브 판을 정말 켜 봄(화면 찍고 끝남), --no-sim 이면 시험 생략.
    꾸러미 안의 VERSION 은 --version 으로 (기본: 지금 VERSION, 1.1.3 이하면 시험을 위해 1.2.0). VERSION 파일은 바꾸지 않음.

설치된 ComicPad 는 시작할 때(하루 한 번) 최신 릴리스의 update.json 서명을 공개키로 확인하고,
맞을 때만 꾸러미를 받아 SHA-256 을 확인한 뒤 적용한다. 서명은 표준 라이브러리만으로 만드는 RSA-3072 (PKCS#1 v1.5, SHA-256).
"""
import argparse
import hashlib
import json
import os
import secrets
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KEY_PATH = os.path.join(os.path.expanduser("~"), ".comicpad", "release_key.json")
PUB_PATH = os.path.join(ROOT, "release_pubkey.json")
OUT = os.path.join(ROOT, "dist", "release")
REPO = "39nhs/comic_pad"
SHA256_PREFIX = bytes.fromhex("3031300d060960864801650304020105000420")

# 꾸러미에 넣는 것 (data·python·redist·dist 는 절대 넣지 않음)
INCLUDE_DIRS = ["app", "plugins", "tools", "runtimes", "launcher", "installer"]
INCLUDE_FILES = ["server.py", "VERSION", "release_pubkey.json", "LICENSE", "NOTICE", "README.md", "ComicPad.ico",
                 "ComicPad.exe", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll"]
SKIP_EXT = (".pyc", ".tmp", ".part", ".cpold")


# ---- RSA (표준 라이브러리만) ------------------------------------------------
SMALL_PRIMES = [p for p in range(3, 2000) if all(p % q for q in range(2, int(p ** 0.5) + 1))]


def is_probable_prime(n, rounds=64):
    if n < 2:
        return False
    for p in SMALL_PRIMES:
        if n % p == 0:
            return n == p
    d, r = n - 1, 0
    while d % 2 == 0:
        d //= 2
        r += 1
    for _ in range(rounds):
        a = secrets.randbelow(n - 3) + 2
        x = pow(a, d, n)
        if x in (1, n - 1):
            continue
        for _ in range(r - 1):
            x = pow(x, 2, n)
            if x == n - 1:
                break
        else:
            return False
    return True


def gen_prime(bits, e):
    while True:
        c = secrets.randbits(bits) | (1 << (bits - 1)) | (1 << (bits - 2)) | 1   # 위 두 비트를 켜서 n 이 정확히 2*bits
        if (c - 1) % e and is_probable_prime(c):
            return c


def gen_key(bits=3072, e=65537):
    while True:
        p, q = gen_prime(bits // 2, e), gen_prime(bits // 2, e)
        n = p * q
        if p != q and n.bit_length() == bits:
            d = pow(e, -1, (p - 1) * (q - 1))
            return {"n": format(n, "x"), "e": e, "d": format(d, "x"), "p": format(p, "x"), "q": format(q, "x")}


def rsa_sign(data, key):
    n, d = int(key["n"], 16), int(key["d"], 16)
    k = (n.bit_length() + 7) // 8
    t = SHA256_PREFIX + hashlib.sha256(data).digest()
    em = b"\x00\x01" + b"\xff" * (k - len(t) - 3) + b"\x00" + t
    s = pow(int.from_bytes(em, "big"), d, n)
    return s.to_bytes(k, "big")


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


# ---- 명령 -------------------------------------------------------------------
def cmd_genkey(args):
    key_path = args.key or KEY_PATH
    if os.path.exists(key_path) and not args.force:
        sys.exit(f"이미 비밀키가 있습니다: {key_path}\n  (새로 만들면 이미 설치된 ComicPad 는 새 키로 서명한 업데이트를 받지 못합니다)")
    print("RSA-3072 키 만드는 중…")
    key = gen_key()
    os.makedirs(os.path.dirname(key_path), exist_ok=True)
    with open(key_path, "w", encoding="utf-8") as f:
        json.dump(key, f)
    pub_path = args.pub or PUB_PATH
    with open(pub_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"alg": "RSA-3072 PKCS#1 v1.5 SHA-256", "n": key["n"], "e": key["e"]}, f, indent=1)
    # 확인
    from_server = load_server_verify()
    test = b"comicpad-key-test"
    assert from_server(test, rsa_sign(test, key), int(key["n"], 16), key["e"]), "서명 확인 실패"
    print(f"비밀키: {key_path}  ← 저장소에 넣지 말고 USB 등에 따로 백업하세요")
    print(f"공개키: {pub_path}")


def load_server_verify():
    sys.path.insert(0, ROOT)
    import server   # noqa: E402  (main() 은 실행되지 않음)
    return server.rsa_verify


NATIVE_PROJ = os.path.join(ROOT, "native", "ComicPadNative")
NATIVE_REL = "native/ComicPadNative"


def build_native(stage, framework=False):
    """네이티브 판 publish -> stage/native/ComicPadNative. (파일 목록, exe 경로)

    기본은 자체 포함(win-x64, .NET 런타임 포함): 업데이트 꾸러미는 런타임을 설치할 수 없으므로 어느 PC 에서나 바로 실행되게.
    ONNX Runtime(DirectML) 의 네이티브 DLL (onnxruntime.dll, DirectML.dll) 이 꼭 있어야 AI·얼굴 추적이 돌아감 → 확인."""
    out = os.path.join(stage, *NATIVE_REL.split("/"))
    if os.path.isdir(out):
        shutil.rmtree(out)
    cmd = ["dotnet", "publish", NATIVE_PROJ, "-c", "Release", "-r", "win-x64", "--self-contained", "false" if framework else "true",
           "-p:DebugType=none", "-p:DebugSymbols=false", "-o", out, "-nologo", "-v", "q"]
    print("네이티브 판 만들기:", " ".join(cmd))
    subprocess.run(cmd, check=True, cwd=ROOT)
    files = []
    for dp, sub, fs in os.walk(out):
        for fn in fs:
            if fn.endswith(SKIP_EXT) or fn.endswith(".pdb"):
                continue
            files.append(os.path.join(dp, fn))
    names = {os.path.basename(f).lower() for f in files}
    exe = os.path.join(out, "ComicPadNative.exe")
    missing = [n for n in ("comicpadnative.exe", "comicpadnative.dll", "onnxruntime.dll", "directml.dll") if n not in names]
    if missing:
        sys.exit("네이티브 판 publish 결과에 없는 파일: " + ", ".join(missing))
    if not framework and "coreclr.dll" not in names:
        sys.exit("자체 포함 publish 인데 .NET 런타임(coreclr.dll)이 없습니다")
    return files, exe


def cmd_build(args):
    with open(os.path.join(ROOT, "VERSION"), encoding="utf-8") as f:
        version = f.read().strip()
    if args.dry_run:
        return cmd_dry_run(args, version)
    key_path = args.key or KEY_PATH
    if not os.path.exists(key_path):
        sys.exit("서명 비밀키가 없습니다. 먼저 python tools/make_release.py --genkey")
    with open(key_path, encoding="utf-8") as f:
        key = json.load(f)
    with open(args.pub or PUB_PATH, encoding="utf-8") as f:
        pubs = json.load(f)
        if key["n"] not in [k["n"] for k in (pubs.get("keys") or [pubs])]:
            sys.exit("release_pubkey.json 과 비밀키가 짝이 아닙니다")
    # 앱 파일 목록(오프라인 캐시) 갱신
    subprocess.run([sys.executable, os.path.join(ROOT, "server.py"), "--precache-only"], check=True, cwd=ROOT)
    out = args.out or OUT
    os.makedirs(out, exist_ok=True)
    for f in os.listdir(out):
        os.remove(os.path.join(out, f))
    zname = f"ComicPad_update_{version}.zip"
    zpath = os.path.join(out, zname)
    dirs = INCLUDE_DIRS + (["models_bundled"] if args.with_models else [])
    if args.native:
        build_launcher()
    native = build_native(os.path.join(ROOT, "dist"), args.native_framework) if args.native else None
    n = write_package(zpath, dirs, native, os.path.join(ROOT, "dist"))
    exe = os.path.join(ROOT, "ComicPad.exe")
    import datetime
    man = {"product": "ComicPad", "version": version, "file": zname, "size": os.path.getsize(zpath), "sha256": sha256_file(zpath),
           "exe_sha256": sha256_file(exe) if os.path.exists(exe) else "", "notes": notes_of(args),
           "date": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
           "models": bool(args.with_models)}
    if native:
        man["native"] = native_info(native[1], args.native_framework)
    if getattr(args, "installer", False):
        # 네이티브 판 업데이트: 설치파일도 서명된 정보(이름·크기·SHA-256)에 넣는다 (GitHub 는 한글 이름을 바꾸므로 영문 이름으로)
        inst = os.path.join(ROOT, "dist", "ComicPad 설치파일.exe")
        if not os.path.exists(inst):
            sys.exit("dist 에 설치파일이 없습니다 (installer\\build_setup.ps1 로 먼저 만들기)")
        iname = f"ComicPad_Setup_{version}.exe"
        shutil.copy2(inst, os.path.join(out, iname))
        man["installer"] = {"file": iname, "size": os.path.getsize(os.path.join(out, iname)), "sha256": sha256_file(os.path.join(out, iname))}
    man_bytes = json.dumps(man, ensure_ascii=False, indent=1).encode("utf-8")
    with open(os.path.join(out, "update.json"), "wb") as f:
        f.write(man_bytes)
    sig = rsa_sign(man_bytes, key)
    assert load_server_verify()(man_bytes, sig, int(key["n"], 16), key["e"])
    with open(os.path.join(out, "update.json.sig"), "w", encoding="ascii") as f:
        f.write(sig.hex())
    print(f"v{version}: {zname} ({man['size'] / 1e6:.1f}MB, 파일 {n}개) + update.json + update.json.sig -> {out}")
    if args.upload:
        check_pushed()
        upload(version, out, args)


def upload(version, out, args):
    tag = f"v{version}"
    if args.installer:
        # GitHub 는 한글·띄어쓰기 파일 이름을 바꿔 버리므로 영문 이름으로 복사해 올림
        inst = os.path.join(ROOT, "dist", "ComicPad 설치파일.exe")
        if not os.path.exists(inst):
            sys.exit("dist 에 설치파일이 없습니다 (installer\\build_setup.ps1 로 먼저 만들기)")
        shutil.copy2(inst, os.path.join(out, f"ComicPad_Setup_{version}.exe"))
    files = [os.path.join(out, f) for f in sorted(os.listdir(out))]
    notes = notes_of(args) or f"ComicPad {version}"
    r = subprocess.run(["gh", "release", "view", tag, "--repo", args.repo], capture_output=True)
    if r.returncode == 0:
        sys.exit(f"{tag} 릴리스가 이미 있습니다. VERSION 을 올리세요")
    subprocess.run(["gh", "release", "create", tag, "--repo", args.repo, "--title", f"ComicPad {version}",
                    "--notes", notes, "--latest", *files], check=True)
    print(f"올림: https://github.com/{args.repo}/releases/tag/{tag}")
    if not args.no_pages:
        deploy_pages(args.repo)


def git(*a, check=True):
    r = subprocess.run(["git", *a], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if check and r.returncode:
        sys.exit(f"git {' '.join(a)} 실패: {r.stderr.strip()}")
    return r.stdout.strip()


def check_pushed():
    """릴리스는 GitHub 에 올라간 코드와 같아야 함 (휴대폰판도 같은 코드로)"""
    if git("status", "--porcelain"):
        sys.exit("커밋하지 않은 변경이 있습니다 (precache.json 포함). 커밋·푸시한 뒤 다시 실행하세요")
    git("fetch", "-q", "origin")
    if git("rev-parse", "HEAD") != git("rev-parse", "origin/main"):
        sys.exit("main 을 GitHub 에 먼저 푸시하세요 (git push)")


def deploy_pages(repo):
    """휴대폰·태블릿판: app 폴더만 gh-pages 브랜치로 올림 → GitHub Pages (https://<계정>.github.io/<저장소>/)"""
    sha = git("subtree", "split", "--prefix", "app", "HEAD").splitlines()[-1]
    git("push", "-q", "-f", "origin", f"{sha}:refs/heads/gh-pages")
    owner, name = repo.split("/")
    print(f"휴대폰판 올림: https://{owner}.github.io/{name}/ (반영까지 1~2분)")


UPDATE_MAX = 600 * 1000 * 1000      # server.py (1.1.3 포함) 가 받는 꾸러미 최대 크기


def build_launcher():
    """창 프로그램(ComicPad.exe)을 launcher\\build.ps1 로 다시 만든다 (네이티브 판 스위치를 읽는 판인지 확인)"""
    if os.name != "nt":
        sys.exit("창 프로그램은 Windows 에서만 만들 수 있습니다")
    print("창 프로그램 만들기: launcher\\build.ps1")
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", os.path.join(ROOT, "launcher", "build.ps1")], check=True, cwd=ROOT)
    exe = os.path.join(ROOT, "ComicPad.exe")
    with open(exe, "rb") as f:
        if "app.json".encode("utf-16-le") not in f.read():
            sys.exit("만든 ComicPad.exe 가 네이티브 판 스위치(data/config/app.json)를 읽지 않습니다")
    return exe


def write_package(zpath, dirs, native=None, stage=None, version=None):
    """업데이트 꾸러미 zip. native = (파일 목록, exe) 이면 native/ComicPadNative/ 로. version 을 주면 꾸러미 안 VERSION 만 그 값으로 (시험용)"""
    n = 0
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        if native:
            n += add_native(z, native[0], stage)
        for d in dirs:
            for dp, sub, files in os.walk(os.path.join(ROOT, d)):
                sub[:] = [s for s in sub if s != "__pycache__"]
                for fn in files:
                    if fn.endswith(SKIP_EXT):
                        continue
                    full = os.path.join(dp, fn)
                    z.write(full, os.path.relpath(full, ROOT).replace("\\", "/"))
                    n += 1
        for fn in INCLUDE_FILES + [f for f in os.listdir(ROOT) if f.endswith((".bat", ".md")) and f not in INCLUDE_FILES]:
            full = os.path.join(ROOT, fn)
            if fn == "VERSION" and version:
                z.writestr("VERSION", version + "\n")
                n += 1
            elif os.path.isfile(full):
                z.write(full, fn)
                n += 1
            elif fn in ("server.py", "VERSION", "release_pubkey.json"):
                sys.exit(f"{fn} 이 없습니다")
            elif fn == "ComicPad.exe" and native:
                sys.exit("ComicPad.exe 가 없습니다 (네이티브 판 꾸러미에는 스위치를 읽는 새 창 프로그램이 꼭 들어가야 함)")
    size = os.path.getsize(zpath)
    if size > UPDATE_MAX:
        sys.exit(f"꾸러미가 {size / 1e6:.0f}MB 로 설치된 ComicPad 가 받는 최대 {UPDATE_MAX / 1e6:.0f}MB 를 넘습니다 (--native-framework 로 작게)")
    return n


def add_native(z, files, stage):
    for full in files:
        z.write(full, os.path.relpath(full, stage).replace("\\", "/"))
    return len(files)


def native_info(exe, framework):
    return {"exe": NATIVE_REL + "/ComicPadNative.exe", "exe_sha256": sha256_file(exe), "self_contained": not framework}


def notes_of(args):
    if getattr(args, "notes_file", None):
        with open(args.notes_file, encoding="utf-8") as f:
            return f.read().strip()
    return args.notes or ""


def cmd_dry_run(args, version):
    """올리지 않는 시험: 서명 키·precache·업로드 없이 zip + 서명 없는 update.json 을 --out 에, 이어서 1.1.3 업데이트로 적용 시험"""
    if args.upload:
        sys.exit("--dry-run 과 --upload 는 함께 쓸 수 없습니다")
    out = args.out or os.path.join(ROOT, "dist", "release_dryrun")
    os.makedirs(out, exist_ok=True)
    stage = os.path.join(out, "_stage")

    # 꾸러미 안 VERSION: 1.1.3 설치에 "새 버전"으로 보여야 시험이 되므로 (VERSION 파일 자체는 그대로)
    def vt(v):
        return tuple(int(x) for x in v.split(".")[:3])
    pkg_version = args.version or (version if vt(version) > (1, 1, 3) else "1.2.0")
    if args.native:
        build_launcher()
    native = build_native(stage, args.native_framework) if args.native else None
    zname = f"ComicPad_update_{pkg_version}.zip"
    zpath = os.path.join(out, zname)
    for f in os.listdir(out):
        if f.startswith("ComicPad_update_") and f.endswith(".zip"):
            os.remove(os.path.join(out, f))
    dirs = INCLUDE_DIRS + (["models_bundled"] if args.with_models else [])
    n = write_package(zpath, dirs, native, stage, pkg_version if pkg_version != version else None)
    exe = os.path.join(ROOT, "ComicPad.exe")
    man = {"product": "ComicPad", "version": pkg_version, "file": zname, "size": os.path.getsize(zpath), "sha256": sha256_file(zpath),
           "exe_sha256": sha256_file(exe) if os.path.exists(exe) else "", "notes": notes_of(args), "dry_run": True, "models": bool(args.with_models)}
    if native:
        man["native"] = native_info(native[1], args.native_framework)
    with open(os.path.join(out, "update.json"), "w", encoding="utf-8") as f:
        json.dump(man, f, ensure_ascii=False, indent=1)
    note = "" if pkg_version == version else f" (VERSION 은 {version} 그대로, 꾸러미 안만 {pkg_version})"
    print(f"[시험] v{pkg_version}{note}: {zname} ({man['size'] / 1e6:.1f}MB, 파일 {n}개) + update.json (서명 없음) -> {out}")
    if native:
        print(f"[시험] 네이티브 판: {os.path.join(stage, *NATIVE_REL.split('/'))} ({'자체 포함' if not args.native_framework else '.NET 8 런타임 필요'})")
    if args.no_sim:
        return
    sys.path.insert(0, os.path.join(ROOT, "tools"))
    import release_sim   # noqa: E402
    fails = release_sim.simulate(zpath, man, launch=args.sim_launch, keep=args.sim_keep)
    if fails:
        sys.exit(f"[시험] 1.1.3 업데이트 적용 시험 실패 {fails}개")


def main():
    ap = argparse.ArgumentParser(description="ComicPad 업데이트 꾸러미 만들기")
    ap.add_argument("--genkey", action="store_true", help="서명 키 만들기 (처음 한 번)")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--key", help="비밀키 파일 (기본 ~/.comicpad/release_key.json)")
    ap.add_argument("--pub", help="공개키 파일 (기본 release_pubkey.json)")
    ap.add_argument("--out", help="결과 폴더 (기본 dist/release)")
    ap.add_argument("--notes", help="바뀐 점 (앱의 업데이트 창에 보임)")
    ap.add_argument("--with-models", action="store_true", help="models_bundled 도 넣기")
    ap.add_argument("--upload", action="store_true", help="GitHub 릴리스로 올리기 (gh 필요)")
    ap.add_argument("--installer", action="store_true", help="설치파일도 함께 올리기")
    ap.add_argument("--no-pages", action="store_true", help="휴대폰판(GitHub Pages)은 올리지 않기")
    ap.add_argument("--repo", default=REPO)
    ap.add_argument("--native", action="store_true", help="네이티브 판(.NET 8)을 publish 해서 꾸러미 native/ComicPadNative/ 에 넣기")
    ap.add_argument("--native-framework", action="store_true", help="네이티브 판을 자체 포함 대신 .NET 8 런타임이 필요한 작은 판으로")
    ap.add_argument("--dry-run", action="store_true", help="시험: 서명·업로드 없이 --out 에 꾸러미만 (기본 dist/release_dryrun) + 1.1.3 업데이트 적용 시험")
    ap.add_argument("--notes-file", help="바뀐 점을 이 파일에서 (UTF-8)")
    ap.add_argument("--version", help="--dry-run 꾸러미 안 VERSION (기본: 지금 VERSION, 1.1.3 이하면 1.2.0)")
    ap.add_argument("--no-sim", action="store_true", help="--dry-run 뒤 1.1.3 업데이트 적용 시험(tools/release_sim.py) 생략")
    ap.add_argument("--sim-launch", action="store_true", help="적용 시험에서 창 프로그램 → 네이티브 판을 정말 켜 보기")
    ap.add_argument("--sim-keep", action="store_true", help="적용 시험 임시 폴더 남기기")
    args = ap.parse_args()
    if args.version and not args.dry_run:
        sys.exit("--version 은 --dry-run 에서만 씁니다 (실제 릴리스는 VERSION 파일)")
    cmd_genkey(args) if args.genkey else cmd_build(args)


if __name__ == "__main__":
    main()
