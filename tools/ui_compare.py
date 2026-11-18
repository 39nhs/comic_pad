#!/usr/bin/env python3
"""웹 판 ↔ 네이티브 판 화면 자동 비교 (개발용).

  python tools/ui_compare.py                    # 전부 찍고 비교 → native/ui_compare/report.html
  python tools/ui_compare.py --only "menu_|main" # 장면 이름(정규식)으로 골라서
  python tools/ui_compare.py --list             # 장면 목록만

하는 일
  1. 웹 기준 서버를 직접 띄움: server.py --no-browser --no-idle-exit --port <빈 포트> --data <임시 폴더>
     (사용자 data 폴더·app/precache.json 은 건드리지 않음 — precache.json 이 바뀌면 되돌림). 끝나면 내가 띄운 PID 만 끔.
  2. 같은 장면을 양쪽에서 찍음 (메인 창, 위 메뉴 하나씩, 패널 탭, 도구 옵션, 설정·새 캔버스·기능 검색 창, 패널 잘라 보기).
     - 웹: Playwright + 설치된 Edge (headless, 1600×900). 처음 켤 때 뜨는 "처음 세팅" 창을 닫으면 기본 세팅(클립스튜디오식, csp)이
       적용되고 서버에 저장됨 → 그 설정 JSON 을 "프리셋" 으로 삼아 장면마다 웹(서버 설정 파일)·네이티브(COMICPAD_SETTINGS)에 똑같이 넣음.
     - 네이티브: COMICPAD_UI_SHOT=<png> + COMICPAD_UI_SHOT_RUN=<단계> (UI/Chrome/UiShot.cs), 임시 COMICPAD_SETTINGS·COMICPAD_DATA.
       창은 숨은 데스크톱(CreateDesktop)에 띄워 사용자 화면에 나타나지 않음 (--visible 이면 보통 화면). 내가 띄운 프로세스만 기다리고 끔.
  3. 장면마다 비교: 배치 상자 (웹 DOM 상자 ↔ 네이티브 <png>.txt), 픽셀 차이 (줄인 흑백 그림의 SSIM·평균 차이),
     차이 그림을 만들어 native/ui_compare/report.html 에 나란히 (나쁜 순).

필요한 것: Playwright 가 든 파이썬 (없으면 만드는 방법을 알려 줌), 설치된 Microsoft Edge, 빌드한 네이티브 exe.
아무것도 인터넷으로 보내지 않음 (웹 페이지의 바깥 주소 요청·업데이트 확인은 막음).
"""
import argparse
import base64
import ctypes
import datetime
import hashlib
import html
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(REPO, "native", "ComicPadNative", "bin", "Release", "net8.0-windows", "ComicPadNative.exe")
DEFAULT_OUT = os.path.join(REPO, "native", "ui_compare")
VENV_HINT = os.path.join(REPO, "tools", ".pwenv")
# Playwright 파이썬을 찾는 곳 (앞에서부터): --pw-python, COMICPAD_PW_PYTHON, tools/.pwenv, 예전 작업 때 만든 임시 venv
PW_CANDIDATES = [
    os.environ.get("COMICPAD_PW_PYTHON", ""),
    os.path.join(VENV_HINT, "Scripts", "python.exe"),
    os.path.expandvars(r"%LOCALAPPDATA%\Temp\claude\b1b4028a03b9b112\d5ebbe01-cf41-4a14-b191-fb53cd678407\scratchpad\pwenv\Scripts\python.exe"),
]
W, H = 1600, 900

# ---------------------------------------------------------------------------------------------
# 장면
# ---------------------------------------------------------------------------------------------
MENUS = [("파일", "file"), ("편집", "edit"), ("레이어", "layer"), ("선택", "select"), ("만화", "comic"), ("보정", "adjust"),
         ("필터", "filter"), ("AI", "ai"), ("보기", "view"), ("도움말", "help"), ("애니", "anim")]
TABS = [("color", "색"), ("nav", "내비"), ("pages", "페이지"), ("assets", "에셋"), ("history", "기록"), ("anim", "애니")]
TOOLS = [("brush", "브러시"), ("eraser", "지우개"), ("fill", "채우기"), ("select", "선택"), ("lasso", "올가미"), ("shape", "도형"),
         ("gradient", "그라데이션"), ("text", "글자"), ("bubble", "말풍선"), ("panel", "컷")]
BOX_KEYS = ["topbar", "menubar", "toolbar", "stage", "sidepanel", "dock_top", "dock_bottom", "bottombar", "statusbar", "popup", "modal"]


def all_scenes():
    s = [dict(id="main", title="메인 화면 (클립스튜디오식 세팅)", web=[], native="")]
    for name, mid in MENUS:
        s.append(dict(id=f"menu_{mid}", title=f"위 메뉴 › {name}", web=[("menu", name)], native=f"menu:{mid}"))
    for tid, name in TABS:
        s.append(dict(id=f"tab_{tid}", title=f"패널 탭 › {name}", web=[("tab", tid)], native=f"tab:{tid}"))
    for tid, name in TOOLS:
        s.append(dict(id=f"tool_{tid}", title=f"도구 옵션 › {name} ({tid})", web=[("tool", tid), ("tab", "tool")], native=f"tool:{tid} ; tab:tool",
                      crops=[("dock_top", "옵션 패널")]))
    s.append(dict(id="dlg_settings", title="환경 설정 창", web=[("run", "settings.open")], native="web:settings.open"))
    s.append(dict(id="dlg_new", title="새 캔버스 창", web=[("run", "file.new")], native="web:file.new"))
    s.append(dict(id="dlg_palette", title="기능 검색 (Ctrl+K)", web=[("run", "palette.open")], native="web:palette.open"))
    # 메인 화면에서 잘라 보기
    s[0]["crops"] = [("dock_bottom", "레이어 패널"), ("dock_top", "위 패널 (도구 옵션)"), ("toolbar", "도구 막대"), ("topbar", "위 막대"), ("bottombar", "아래 막대")]
    return s


# ---------------------------------------------------------------------------------------------
# 도우미
# ---------------------------------------------------------------------------------------------
def log(*a):
    print(*a, flush=True)


def free_port():
    with socket.socket() as so:
        so.bind(("127.0.0.1", 0))
        return so.getsockname()[1]


def sha(path):
    try:
        with open(path, "rb") as fh:
            return hashlib.sha256(fh.read()).hexdigest()
    except OSError:
        return None


def ensure_playwright(args):
    try:
        import playwright  # noqa: F401
        return
    except ImportError:
        pass
    cands = ([args.pw_python] if args.pw_python else []) + PW_CANDIDATES
    for c in cands:
        if c and os.path.isfile(c) and os.path.abspath(c).lower() != os.path.abspath(sys.executable).lower():
            log(f"Playwright 파이썬으로 다시 실행: {c}")
            r = subprocess.run([c, os.path.abspath(__file__)] + sys.argv[1:], env=dict(os.environ, PYTHONIOENCODING="utf-8"))
            sys.exit(r.returncode)
    log("Playwright 를 찾지 못했습니다. 이렇게 만들어 주세요 (설치된 Edge 를 쓰므로 브라우저는 따로 받지 않아도 됨):")
    log(f'  python -m venv "{VENV_HINT}"')
    log(f'  "{os.path.join(VENV_HINT, "Scripts", "pip")}" install playwright')
    log("  그다음 다시: python tools/ui_compare.py   (또는 --pw-python <venv 의 python.exe>)")
    sys.exit(2)


# ---------------------------------------------------------------------------------------------
# 웹 기준 서버 (임시 data 폴더, 내가 띄운 PID 만 끔)
# ---------------------------------------------------------------------------------------------
class WebServer:
    def __init__(self, data_dir, logfile):
        self.port = free_port()
        self.data = data_dir
        self.precache = os.path.join(REPO, "app", "precache.json")
        self.precache_bytes = open(self.precache, "rb").read() if os.path.exists(self.precache) else None
        self.logfh = open(logfile, "w", encoding="utf-8", errors="replace")
        env = dict(os.environ)
        env.pop("COMICPAD_DATA_DIR", None)
        env["PYTHONIOENCODING"] = "utf-8"
        self.proc = subprocess.Popen([sys.executable, os.path.join(REPO, "server.py"), "--no-browser", "--no-idle-exit", "--port", str(self.port), "--data", data_dir],
                                     cwd=REPO, stdout=self.logfh, stderr=subprocess.STDOUT, env=env,
                                     creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.url = f"http://127.0.0.1:{self.port}/"
        for _ in range(100):
            if self.proc.poll() is not None:
                raise RuntimeError(f"server.py 가 바로 끝났습니다 (기록: {logfile})")
            try:
                urllib.request.urlopen(self.url, timeout=1).read(100)
                return
            except Exception:
                time.sleep(0.2)
        raise RuntimeError("server.py 가 응답하지 않습니다")

    def stop(self):
        if self.proc.poll() is None:
            self.proc.terminate()            # 내가 띄운 PID 만
            try:
                self.proc.wait(10)
            except subprocess.TimeoutExpired:
                self.proc.kill()
        self.logfh.close()
        # app/precache.json 은 원래대로 (임시 data 로 띄울 때는 server.py 가 쓰지 않지만 혹시 바뀌었으면)
        if self.precache_bytes is not None:
            now = open(self.precache, "rb").read() if os.path.exists(self.precache) else None
            if now != self.precache_bytes:
                with open(self.precache, "wb") as fh:
                    fh.write(self.precache_bytes)
                log("  app/precache.json 이 바뀌어 되돌렸습니다")


# ---------------------------------------------------------------------------------------------
# 웹 찍기 (Playwright, headless Edge)
# ---------------------------------------------------------------------------------------------
CLOSE_JS = """() => { const ms = document.querySelectorAll('#modal-root .modal'); if (!ms.length) return 0;
  const btn = ms[ms.length - 1].querySelector('.modal-head .icon-btn'); if (btn) btn.click(); else document.querySelector('#modal-root .modal-backdrop')?.remove(); return ms.length; }"""

BOX_JS = """() => {
  const box = el => { if (!el) return null; const s = getComputedStyle(el); if (s.display === 'none' || s.visibility === 'hidden') return null;
    const b = el.getBoundingClientRect(); if (b.width < 1 || b.height < 1) return null; return [Math.round(b.x), Math.round(b.y), Math.round(b.width), Math.round(b.height)]; };
  const q = s => document.querySelector(s);
  const ms = document.querySelectorAll('#modal-root .modal');
  return {
    topbar: box(q('#topbar')), menubar: box(q('#menubar')), toolbar: box(q('#toolbar')), stage: box(q('#stage')), sidepanel: box(q('#sidepanel')),
    dock_top: box(q('#dock-top')), dock_bottom: box(q('#dock-bottom')), bottombar: box(q('#bottombar')), statusbar: box(q('#statusbar')),
    popup: box(q('#menu-layer .menu')), modal: ms.length ? box(ms[ms.length - 1]) : null,
    menus: [...document.querySelectorAll('#menubar button')].map(b => b.textContent.trim()),
    tools: [...document.querySelectorAll('#toolbar button[data-tool]')].map(b => b.dataset.tool),
  };
}"""


def allowed(url, base):
    if url.startswith(("data:", "blob:")):
        return True
    if not url.startswith(base):
        return False                         # 바깥 주소는 막음 (아무것도 보내지 않음)
    return "/api/update" not in url and "/api/drive" not in url


def web_open(browser, base, preset, data_dir):
    if preset is not None:
        cfg = os.path.join(data_dir, "config")
        os.makedirs(cfg, exist_ok=True)
        with open(os.path.join(cfg, "settings.json"), "w", encoding="utf-8") as fh:
            json.dump(preset, fh, ensure_ascii=False, indent=1)
    ctx = browser.new_context(viewport={"width": W, "height": H}, device_scale_factor=1, locale="ko-KR")
    ctx.route("**/*", lambda route: route.continue_() if allowed(route.request.url, base) else route.abort())
    page = ctx.new_page()
    page.set_default_timeout(5000)
    page.goto(base)
    page.wait_for_timeout(2500)
    close_dialogs(page)
    page.wait_for_timeout(500)
    return ctx, page


def close_dialogs(page):
    for _ in range(6):
        n = page.evaluate(CLOSE_JS)
        page.wait_for_timeout(400)
        if not n:
            break


def web_bootstrap(browser, base, data_dir):
    """처음 켜기: "처음 세팅" 창을 닫으면 기본 세팅(csp)이 적용·저장됨 → 그 설정을 프리셋으로"""
    ctx, page = web_open(browser, base, None, data_dir)
    path = os.path.join(data_dir, "config", "settings.json")
    preset = None
    for _ in range(40):
        try:
            with open(path, encoding="utf-8") as fh:
                preset = json.load(fh)
            if preset.get("setupDone"):
                break
        except (OSError, ValueError):
            pass
        page.wait_for_timeout(250)
    ctx.close()
    if not preset or not preset.get("setupDone"):
        log("  (경고) 웹 처음 세팅을 읽지 못해 {\"setupDone\":\"csp\"} 만 씁니다")
        preset = {"setupDone": "csp"}
    preset.pop("setupBackup", None)
    return preset


def web_step(page, step):
    kind, arg = step
    if kind == "menu":
        idx = page.evaluate("n => [...document.querySelectorAll('#menubar button')].findIndex(e => e.textContent.trim() === n)", arg)
        if idx < 0:
            raise LookupError(f"웹 메뉴 없음: {arg}")
        page.locator("#menubar button").nth(idx).click()
        page.wait_for_timeout(450)
    elif kind == "tab":
        if not page.evaluate("id => comicpad.panels.has(id)", arg):
            raise LookupError(f"웹 패널 없음: {arg}")
        page.evaluate("id => comicpad.bus.emit('dock:show', id)", arg)
        page.wait_for_timeout(450)
    elif kind == "tool":
        if not page.evaluate("id => comicpad.tools.has(id)", arg):
            raise LookupError(f"웹 도구 없음: {arg}")
        page.evaluate("id => comicpad.setTool(id)", arg)
        page.wait_for_timeout(400)
    elif kind == "run":
        page.evaluate("id => { setTimeout(() => comicpad.run(id), 0); }", arg)
        page.wait_for_timeout(900)


def web_capture(scenes, base, preset, data_dir, outdir, browser):
    res = {}
    for sc in scenes:
        png = os.path.join(outdir, sc["id"] + ".png")
        try:
            ctx, page = web_open(browser, base, preset, data_dir)
            try:
                for st in sc["web"]:
                    web_step(page, st)
                page.mouse.move(W - 2, H - 2)      # 마우스 올림 표시가 남지 않게
                page.wait_for_timeout(300)
                page.screenshot(path=png)
                boxes = page.evaluate(BOX_JS)
                res[sc["id"]] = dict(png=png, boxes=boxes)
            finally:
                ctx.close()
        except LookupError as e:
            res[sc["id"]] = dict(skip=str(e))
        except Exception as e:  # noqa: BLE001
            res[sc["id"]] = dict(error=f"{type(e).__name__}: {e}")
        log(f"  웹 {sc['id']}: {'OK' if 'png' in res[sc['id']] else res[sc['id']].get('skip') or res[sc['id']].get('error')}")
    return res


# ---------------------------------------------------------------------------------------------
# 네이티브 찍기 (숨은 데스크톱, 내가 띄운 프로세스만)
# ---------------------------------------------------------------------------------------------
if os.name == "nt":
    from ctypes import wintypes as WT

    class STARTUPINFOW(ctypes.Structure):
        _fields_ = [("cb", WT.DWORD), ("lpReserved", WT.LPWSTR), ("lpDesktop", WT.LPWSTR), ("lpTitle", WT.LPWSTR),
                    ("dwX", WT.DWORD), ("dwY", WT.DWORD), ("dwXSize", WT.DWORD), ("dwYSize", WT.DWORD),
                    ("dwXCountChars", WT.DWORD), ("dwYCountChars", WT.DWORD), ("dwFillAttribute", WT.DWORD), ("dwFlags", WT.DWORD),
                    ("wShowWindow", WT.WORD), ("cbReserved2", WT.WORD), ("lpReserved2", ctypes.c_void_p),
                    ("hStdInput", WT.HANDLE), ("hStdOutput", WT.HANDLE), ("hStdError", WT.HANDLE)]

    class PROCESS_INFORMATION(ctypes.Structure):
        _fields_ = [("hProcess", WT.HANDLE), ("hThread", WT.HANDLE), ("dwProcessId", WT.DWORD), ("dwThreadId", WT.DWORD)]

    _user32 = ctypes.WinDLL("user32", use_last_error=True)
    _kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    _user32.CreateDesktopW.restype = WT.HANDLE
    _user32.CreateDesktopW.argtypes = [WT.LPCWSTR, WT.LPCWSTR, ctypes.c_void_p, WT.DWORD, WT.DWORD, ctypes.c_void_p]
    _user32.CloseDesktop.argtypes = [WT.HANDLE]
    _kernel32.CreateProcessW.argtypes = [WT.LPCWSTR, WT.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, WT.BOOL, WT.DWORD, ctypes.c_void_p, WT.LPCWSTR,
                                         ctypes.POINTER(STARTUPINFOW), ctypes.POINTER(PROCESS_INFORMATION)]
    _kernel32.WaitForSingleObject.argtypes = [WT.HANDLE, WT.DWORD]
    _kernel32.GetExitCodeProcess.argtypes = [WT.HANDLE, ctypes.POINTER(WT.DWORD)]
    _kernel32.TerminateProcess.argtypes = [WT.HANDLE, WT.UINT]
    _kernel32.CloseHandle.argtypes = [WT.HANDLE]


class HiddenDesktop:
    """사용자 화면과 다른 데스크톱: 여기에 띄운 창은 화면에 나타나지 않음 (그림은 DrawToBitmap 으로 찍힘)"""

    def __init__(self, tag):
        self.name = f"ComicPadUiCompare_{os.getpid()}_{tag}"
        self.handle = _user32.CreateDesktopW(self.name, None, None, 0, 0x10000000, None)   # GENERIC_ALL
        if not self.handle:
            raise OSError(f"CreateDesktop 실패 ({ctypes.get_last_error()})")

    def close(self):
        if self.handle:
            _user32.CloseDesktop(self.handle)
            self.handle = None


def run_on_desktop(exe, env, cwd, desktop, timeout):
    """exe 를 그 데스크톱에서 실행하고 기다림. 시간 초과면 이 프로세스(내가 띄운 것)만 끝냄 → 끝 코드"""
    block = "".join(f"{k}={v}\0" for k, v in env.items()) + "\0"
    envbuf = ctypes.create_unicode_buffer(block, len(block))
    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    si.lpDesktop = "WinSta0\\" + desktop.name if desktop else None
    pi = PROCESS_INFORMATION()
    cmd = ctypes.create_unicode_buffer(f'"{exe}"')
    if not _kernel32.CreateProcessW(exe, cmd, None, None, False, 0x400, envbuf, cwd, ctypes.byref(si), ctypes.byref(pi)):   # CREATE_UNICODE_ENVIRONMENT
        raise OSError(f"CreateProcess 실패 ({ctypes.get_last_error()})")
    try:
        r = _kernel32.WaitForSingleObject(pi.hProcess, int(timeout * 1000))
        if r != 0:
            _kernel32.TerminateProcess(pi.hProcess, 1)      # 내가 띄운 PID 만
            _kernel32.WaitForSingleObject(pi.hProcess, 5000)
            return None
        code = WT.DWORD()
        _kernel32.GetExitCodeProcess(pi.hProcess, ctypes.byref(code))
        return code.value
    finally:
        _kernel32.CloseHandle(pi.hThread)
        _kernel32.CloseHandle(pi.hProcess)


def parse_native_txt(path):
    boxes = {}
    if not os.path.exists(path):
        return boxes
    for line in open(path, encoding="utf-8", errors="replace"):
        m = re.match(r"^(\w+) \{X=(-?\d+),Y=(-?\d+),Width=(\d+),Height=(\d+)\}", line.strip())
        if m:
            x, y, w, h = map(int, m.groups()[1:])
            boxes[m.group(1)] = [x, y, w, h] if w > 0 and h > 0 else None
        elif line.startswith("menus: "):
            boxes["menus"] = [t.rsplit("@", 1)[0] for t in line[7:].split()]
        elif line.startswith("tools: "):
            boxes["tools"] = line[7:].split()
    return boxes


def native_capture(scenes, exe, preset, tmp, outdir, visible, workers, timeout):
    res = {}
    desks = []
    if not visible:
        for i in range(workers):
            desks.append(HiddenDesktop(i))
    lock = threading.Lock()
    pool_desks = list(desks) if desks else [None] * workers

    def one(sc):
        with lock:
            desk = pool_desks.pop()
        try:
            d = os.path.join(tmp, "native", sc["id"])
            shutil.rmtree(d, ignore_errors=True)
            os.makedirs(os.path.join(d, "data"))
            with open(os.path.join(d, "settings.json"), "w", encoding="utf-8") as fh:
                json.dump(preset, fh, ensure_ascii=False, indent=1)
            png = os.path.join(outdir, sc["id"] + ".png")
            for p in (png, png + ".txt", png + ".error.txt"):
                if os.path.exists(p):
                    os.remove(p)
            env = {k: v for k, v in os.environ.items() if not k.upper().startswith("COMICPAD_")}
            env.update(COMICPAD_SETTINGS=os.path.join(d, "settings.json"), COMICPAD_DATA=os.path.join(d, "data"),
                       COMICPAD_UI_SHOT=png, COMICPAD_UI_SHOT_RUN=sc["native"])
            t0 = time.time()
            code = run_on_desktop(exe, env, d, desk, timeout)
            r = dict(secs=round(time.time() - t0, 1))
            if code is None:
                r["error"] = f"시간 초과 ({timeout}초)"
            elif os.path.exists(png + ".error.txt"):
                r["error"] = open(png + ".error.txt", encoding="utf-8", errors="replace").read().strip().splitlines()[0]
            elif not os.path.exists(png):
                r["error"] = f"그림이 없음 (끝 코드 {code})"
            else:
                r.update(png=png, boxes=parse_native_txt(png + ".txt"))
            return sc["id"], r
        finally:
            with lock:
                pool_desks.append(desk)

    try:
        with ThreadPoolExecutor(max_workers=workers) as ex:
            for sid, r in ex.map(one, scenes):
                res[sid] = r
                log(f"  네이티브 {sid}: {'OK' if 'png' in r else r.get('error')} ({r.get('secs')}초)")
    finally:
        for dk in desks:
            dk.close()
    return res


# ---------------------------------------------------------------------------------------------
# 비교 (headless Edge 안의 캔버스로: 줄인 흑백 SSIM·평균 차이, 차이 그림, 잘라 보기)
# ---------------------------------------------------------------------------------------------
LAB_JS = r"""
async ({ a, b, ra, rb, sw }) => {
  const load = src => new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = () => no(new Error('그림을 읽지 못함')); i.src = src; });
  const A = await load(a), B = await load(b);
  // 잘라 보기: 각자의 상자 (없으면 전체). 네이티브 쪽을 웹 크기에 맞춰 늘이고 줄임
  const ca = ra || [0, 0, A.width, A.height], cb = rb || [0, 0, B.width, B.height];
  const Wd = ca[2], Ht = ca[3];
  const canvas = (w, h) => { const c = document.createElement('canvas'); c.width = w; c.height = h; return c; };
  const draw = (img, r, w, h) => { const c = canvas(w, h), x = c.getContext('2d'); x.imageSmoothingQuality = 'high'; x.drawImage(img, r[0], r[1], r[2], r[3], 0, 0, w, h); return c; };
  const fa = draw(A, ca, Wd, Ht), fb = draw(B, cb, Wd, Ht);
  // 줄인 흑백
  const s = Math.min(1, sw / Wd), w = Math.max(8, Math.round(Wd * s)), h = Math.max(8, Math.round(Ht * s));
  const gray = c => { const d = draw(c, [0, 0, c.width, c.height], w, h).getContext('2d').getImageData(0, 0, w, h).data; const g = new Float64Array(w * h); for (let i = 0; i < w * h; i++) g[i] = 0.299 * d[i * 4] + 0.587 * d[i * 4 + 1] + 0.114 * d[i * 4 + 2]; return g; };
  const ga = gray(fa), gb = gray(fb);
  let mad = 0; for (let i = 0; i < ga.length; i++) mad += Math.abs(ga[i] - gb[i]); mad /= ga.length * 255;
  const C1 = (0.01 * 255) ** 2, C2 = (0.03 * 255) ** 2, K = 8; let ss = 0, n = 0;
  for (let by = 0; by + K <= h; by += K) for (let bx = 0; bx + K <= w; bx += K) {
    let ma = 0, mb = 0; for (let y = 0; y < K; y++) for (let x = 0; x < K; x++) { const i = (by + y) * w + bx + x; ma += ga[i]; mb += gb[i]; }
    ma /= K * K; mb /= K * K; let va = 0, vb = 0, cv = 0;
    for (let y = 0; y < K; y++) for (let x = 0; x < K; x++) { const i = (by + y) * w + bx + x, da = ga[i] - ma, db = gb[i] - mb; va += da * da; vb += db * db; cv += da * db; }
    va /= K * K - 1; vb /= K * K - 1; cv /= K * K - 1;
    ss += ((2 * ma * mb + C1) * (2 * cv + C2)) / ((ma * ma + mb * mb + C1) * (va + vb + C2)); n++;
  }
  const ssim = n ? ss / n : 1;
  // 차이 그림: 웹 그림을 어둡게 + 다른 곳을 빨갛게
  const da = fa.getContext('2d').getImageData(0, 0, Wd, Ht).data, db = fb.getContext('2d').getImageData(0, 0, Wd, Ht).data;
  const dc = canvas(Wd, Ht), dx = dc.getContext('2d'), out = dx.createImageData(Wd, Ht), o = out.data; let changed = 0;
  for (let i = 0; i < Wd * Ht; i++) {
    const p = i * 4, d = Math.max(Math.abs(da[p] - db[p]), Math.abs(da[p + 1] - db[p + 1]), Math.abs(da[p + 2] - db[p + 2]));
    const g = (0.299 * da[p] + 0.587 * da[p + 1] + 0.114 * da[p + 2]) * 0.35;
    if (d > 24) { changed++; const t = Math.min(1, d / 160); o[p] = 255; o[p + 1] = g * (1 - t); o[p + 2] = g * (1 - t); } else { o[p] = o[p + 1] = o[p + 2] = g; }
    o[p + 3] = 255;
  }
  dx.putImageData(out, 0, 0);
  const res = { ssim, mad, changed: changed / (Wd * Ht), diff: dc.toDataURL('image/png'), w: Wd, h: Ht };
  if (ra || rb) { res.ca = fa.toDataURL('image/png'); res.cb = fb.toDataURL('image/png'); }
  return res;
}
"""


def data_url(path):
    with open(path, "rb") as fh:
        return "data:image/png;base64," + base64.b64encode(fh.read()).decode()


def save_data_url(u, path):
    with open(path, "wb") as fh:
        fh.write(base64.b64decode(u.split(",", 1)[1]))


def box_compare(wb, nb):
    rows, errs = [], []
    for k in BOX_KEYS:
        a, b = (wb or {}).get(k), (nb or {}).get(k)
        if a is None and b is None:
            continue
        if a is None or b is None:
            rows.append(dict(key=k, web=a, native=b, err=None, note="웹에만" if b is None else "네이티브에만"))
            errs.append(60.0)
            continue
        e = (abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2]) + abs(a[3] - b[3])) / 4
        rows.append(dict(key=k, web=a, native=b, err=round(e, 1), note=""))
        errs.append(e)
    return rows, (round(sum(errs) / len(errs), 1) if errs else None)


def compare(scenes, web, nat, outdir, browser):
    page = browser.new_page()
    page.set_content("<html><body></body></html>")
    rows = []
    for sc in scenes:
        wr, nr = web.get(sc["id"], {}), nat.get(sc["id"], {})
        base = dict(id=sc["id"], title=sc["title"], native_run=sc["native"], web_steps=sc["web"])
        if "png" not in wr or "png" not in nr:
            base.update(status="skip", note=wr.get("skip") or wr.get("error") or nr.get("error") or "찍지 못함")
            rows.append(base)
            continue
        boxes, berr = box_compare(wr.get("boxes"), nr.get("boxes"))
        variants = [(sc["id"], sc["title"], None)] + [(f"{sc['id']}__{key}", f"{sc['title']} › {label}", key) for key, label in sc.get("crops", [])]
        for vid, vtitle, key in variants:
            r = dict(base, id=vid, title=vtitle, crop=key)
            ra = rb = None
            if key:
                ra, rb = (wr.get("boxes") or {}).get(key), (nr.get("boxes") or {}).get(key)
                if not ra or not rb:
                    r.update(status="skip", note=f"잘라 볼 상자({key})가 한쪽에 없음")
                    rows.append(r)
                    continue
            m = page.evaluate(LAB_JS, dict(a=data_url(wr["png"]), b=data_url(nr["png"]), ra=ra, rb=rb, sw=480 if not key else 300))
            img = os.path.join(outdir, "img")
            if key:
                save_data_url(m["ca"], os.path.join(img, f"{vid}_web.png"))
                save_data_url(m["cb"], os.path.join(img, f"{vid}_native.png"))
                r.update(web_img=f"img/{vid}_web.png", native_img=f"img/{vid}_native.png")
            else:
                r.update(web_img=f"img/{vid}_web.png", native_img=f"img/{vid}_native.png")
            save_data_url(m["diff"], os.path.join(img, f"{vid}_diff.png"))
            r["diff_img"] = f"img/{vid}_diff.png"
            r.update(status="ok", ssim=round(m["ssim"], 4), mad=round(m["mad"], 4), changed=round(m["changed"], 4))
            if not key:
                r.update(boxes=boxes, box_err=berr, web_menus=(wr.get("boxes") or {}).get("menus"), native_menus=(nr.get("boxes") or {}).get("menus"))
                pen = min(30.0, (berr or 0) / 4)
            else:
                a, b = ra, rb
                e = (abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2]) + abs(a[3] - b[3])) / 4
                r.update(box_err=round(e, 1), boxes=[dict(key=key, web=a, native=b, err=round(e, 1), note="")])
                pen = min(30.0, e / 4)
            # 종합 점수 (클수록 다름): 100·(1-SSIM) + 100·평균 차이 + 배치 오차/4 (최대 30)
            r["score"] = round(100 * (1 - m["ssim"]) + 100 * m["mad"] + pen, 1)
            rows.append(r)
    page.close()
    return rows


# ---------------------------------------------------------------------------------------------
# 보고서
# ---------------------------------------------------------------------------------------------
def write_report(rows, outdir, meta):
    ok = sorted([r for r in rows if r.get("status") == "ok"], key=lambda r: -r["score"])
    skip = [r for r in rows if r.get("status") != "ok"]
    e = html.escape

    def fmt_box(b):
        return "—" if not b else f"{b[0]},{b[1]} {b[2]}×{b[3]}"

    parts = []
    for i, r in enumerate(ok, 1):
        boxes = "".join(f"<tr><td>{e(b['key'])}</td><td>{fmt_box(b['web'])}</td><td>{fmt_box(b['native'])}</td><td>{'' if b['err'] is None else b['err']}</td><td>{e(b['note'])}</td></tr>" for b in r.get("boxes") or [])
        menus = ""
        if r.get("web_menus") is not None or r.get("native_menus") is not None:
            menus = f"<p class=m>웹 메뉴: {e(' · '.join(r.get('web_menus') or []))}<br>네이티브 메뉴: {e(' · '.join(r.get('native_menus') or []))}</p>"
        sev = "bad" if r["score"] >= 40 else "mid" if r["score"] >= 20 else "good"
        parts.append(f"""
<details class="row {sev}" id="{e(r['id'])}"{' open' if i <= 3 else ''}>
  <summary><span class=rank>{i}</span><span class=t>{e(r['title'])}</span><span class=id>{e(r['id'])}</span>
    <span class=sc>{r['score']}</span><span class=k>SSIM {r['ssim']:.3f}</span><span class=k>평균 차이 {r['mad'] * 100:.1f}%</span>
    <span class=k>바뀐 픽셀 {r['changed'] * 100:.0f}%</span><span class=k>배치 오차 {'—' if r.get('box_err') is None else str(r['box_err']) + 'px'}</span></summary>
  <div class=imgs>
    <figure><figcaption>웹</figcaption><a href="{e(r['web_img'])}" target=_blank><img loading=lazy src="{e(r['web_img'])}"></a></figure>
    <figure><figcaption>네이티브</figcaption><a href="{e(r['native_img'])}" target=_blank><img loading=lazy src="{e(r['native_img'])}"></a></figure>
    <figure><figcaption>차이 (빨강 = 다름)</figcaption><a href="{e(r['diff_img'])}" target=_blank><img loading=lazy src="{e(r['diff_img'])}"></a></figure>
  </div>
  <p class=m>네이티브 단계: <code>{e(r['native_run'] or '(없음)')}</code> · 웹 단계: <code>{e(json.dumps(r['web_steps'], ensure_ascii=False))}</code></p>
  {menus}
  {f'<table class=bx><tr><th>상자</th><th>웹</th><th>네이티브</th><th>오차(px)</th><th></th></tr>{boxes}</table>' if boxes else ''}
</details>""")
    skips = "".join(f"<li><b>{e(r['id'])}</b> — {e(r.get('note', ''))}</li>" for r in skip)
    top = "".join(f"<tr><td>{i}</td><td><a href='#{e(r['id'])}'>{e(r['title'])}</a></td><td>{r['score']}</td><td>{r['ssim']:.3f}</td><td>{r['mad'] * 100:.1f}%</td><td>{'—' if r.get('box_err') is None else r['box_err']}</td></tr>" for i, r in enumerate(ok[:10], 1))
    page = f"""<!doctype html>
<html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>웹 ↔ 네이티브 화면 비교</title>
<style>
:root {{ --bg:#1f1f24; --panel:#2a2a30; --line:#3c3c44; --text:#e8e8ec; --muted:#9c9ca8; --accent:#e8a33d; --bad:#e05a5a; --mid:#d6a03a; --good:#5aaa6a; }}
* {{ box-sizing:border-box; }}
body {{ margin:0; background:var(--bg); color:var(--text); font:14px/1.5 "Malgun Gothic", system-ui, sans-serif; }}
main {{ max-width:1500px; margin:0 auto; padding:20px 16px 60px; }}
h1 {{ font-size:20px; margin:0 0 4px; }} .sub {{ color:var(--muted); margin:0 0 16px; }}
table {{ border-collapse:collapse; }} td, th {{ border-bottom:1px solid var(--line); padding:4px 10px; text-align:left; font-variant-numeric:tabular-nums; }}
th {{ color:var(--muted); font-weight:normal; }}
a {{ color:var(--accent); }}
.top {{ background:var(--panel); border-radius:10px; padding:12px 16px; margin-bottom:18px; overflow-x:auto; }}
details.row {{ background:var(--panel); border-radius:10px; margin:8px 0; border-left:4px solid var(--good); }}
details.row.mid {{ border-left-color:var(--mid); }} details.row.bad {{ border-left-color:var(--bad); }}
summary {{ cursor:pointer; padding:10px 14px; display:flex; flex-wrap:wrap; gap:6px 14px; align-items:baseline; }}
.rank {{ color:var(--muted); min-width:2em; }} .t {{ font-weight:bold; }} .id {{ color:var(--muted); font-family:Consolas, monospace; font-size:12px; }}
.sc {{ margin-left:auto; color:var(--accent); font-weight:bold; font-size:16px; }} .k {{ color:var(--muted); font-size:12px; }}
.imgs {{ display:grid; grid-template-columns:repeat(3, minmax(0, 1fr)); gap:10px; padding:0 14px; }}
@media (max-width: 900px) {{ .imgs {{ grid-template-columns:1fr; }} }}
figure {{ margin:0; }} figcaption {{ color:var(--muted); font-size:12px; margin-bottom:4px; }}
img {{ width:100%; height:auto; display:block; border:1px solid var(--line); border-radius:4px; background:#000; }}
.m {{ color:var(--muted); font-size:12px; padding:0 14px; margin:8px 0; word-break:break-all; }} code {{ color:var(--text); }}
.bx {{ margin:4px 14px 14px; font-size:12px; }}
</style></head><body><main>
<h1>웹 ↔ 네이티브 화면 비교</h1>
<p class=sub>{e(meta['when'])} · 커밋 {e(meta['commit'])} · 장면 {len(ok)}개 비교, {len(skip)}개 건너뜀 · 1600×900 · 프리셋 setupDone={e(str(meta['preset']))} · 네이티브 창: {e(meta['desktop'])}<br>
점수 = 100·(1−SSIM) + 100·평균 차이 + 배치 오차/4 (최대 30). 클수록 다름. SSIM 은 가로 480px(잘라 보기는 300px)로 줄인 흑백 그림, 8×8 칸 평균.</p>
<div class=top><b>가장 다른 10개</b>
<table><tr><th>#</th><th>장면</th><th>점수</th><th>SSIM</th><th>평균 차이</th><th>배치 오차(px)</th></tr>{top}</table></div>
{''.join(parts)}
{f'<h2 style="font-size:16px">건너뛴 장면</h2><ul>{skips}</ul>' if skips else ''}
</main></body></html>"""
    with open(os.path.join(outdir, "report.html"), "w", encoding="utf-8") as fh:
        fh.write(page)
    with open(os.path.join(outdir, "summary.json"), "w", encoding="utf-8") as fh:
        json.dump(dict(meta=meta, rows=[{k: v for k, v in r.items() if k not in ("web_steps",)} for r in ok + skip]), fh, ensure_ascii=False, indent=1)
    return ok, skip


# ---------------------------------------------------------------------------------------------
def main():
    for st in (sys.stdout, sys.stderr):
        try:
            st.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description="웹 판 ↔ 네이티브 판 화면 자동 비교 → native/ui_compare/report.html")
    ap.add_argument("--only", help="장면 id 정규식 (예: 'main|menu_')")
    ap.add_argument("--list", action="store_true", help="장면 목록만 보기")
    ap.add_argument("--exe", default=DEFAULT_EXE, help="네이티브 exe (기본: Release 빌드)")
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--visible", action="store_true", help="네이티브 창을 숨은 데스크톱 대신 보통 화면에 (잠깐 보임)")
    ap.add_argument("--workers", type=int, default=3, help="네이티브 동시 찍기 수")
    ap.add_argument("--timeout", type=float, default=45, help="네이티브 장면 하나 시간 제한 (초)")
    ap.add_argument("--keep-temp", action="store_true", help="임시 폴더(웹·네이티브 data) 남기기")
    ap.add_argument("--pw-python", help="Playwright 가 든 python.exe")
    args = ap.parse_args()

    scenes = all_scenes()
    if args.only:
        rx = re.compile(args.only)
        scenes = [s for s in scenes if rx.search(s["id"])]
    if args.list:
        for s in scenes:
            print(f"{s['id']:<16} {s['title']}   네이티브: {s['native'] or '(없음)'}")
        return 0
    ensure_playwright(args)
    from playwright.sync_api import sync_playwright

    if not os.path.isfile(args.exe):
        log(f"네이티브 exe 가 없습니다: {args.exe}\n  먼저: dotnet build native/ComicPadNative -c Release")
        return 2
    out = os.path.abspath(args.out)
    for sub in ("img", "web", "native"):
        shutil.rmtree(os.path.join(out, sub), ignore_errors=True)
        os.makedirs(os.path.join(out, sub))
    tmp = tempfile.mkdtemp(prefix="comicpad_uicompare_")
    log(f"임시 폴더: {tmp}")
    try:
        commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=REPO, capture_output=True, text=True).stdout.strip() or "?"
    except OSError:
        commit = "?"
    srv = WebServer(os.path.join(tmp, "webdata"), os.path.join(tmp, "server.log"))
    log(f"웹 기준 서버: {srv.url} (PID {srv.proc.pid}, data {srv.data})")
    try:
        with sync_playwright() as pw:
            browser = pw.chromium.launch(channel="msedge", headless=True)
            try:
                log("웹 처음 세팅 → 프리셋")
                preset = web_bootstrap(browser, srv.url, srv.data)
                with open(os.path.join(out, "preset_settings.json"), "w", encoding="utf-8") as fh:
                    json.dump(preset, fh, ensure_ascii=False, indent=1)
                log(f"웹 찍기 ({len(scenes)}장면)")
                web = web_capture(scenes, srv.url, preset, srv.data, os.path.join(out, "web"), browser)
                log(f"네이티브 찍기 ({'보통 화면' if args.visible else '숨은 데스크톱'}, 동시 {args.workers})")
                nat = native_capture(scenes, os.path.abspath(args.exe), preset, tmp, os.path.join(out, "native"), args.visible, max(1, args.workers), args.timeout)
                # 원본 그림을 img 로 (보고서가 한 폴더 안에서 열리게)
                for sc in scenes:
                    for side, res in (("web", web), ("native", nat)):
                        p = res.get(sc["id"], {}).get("png")
                        if p:
                            shutil.copyfile(p, os.path.join(out, "img", f"{sc['id']}_{side}.png"))
                log("비교")
                rows = compare(scenes, web, nat, out, browser)
            finally:
                browser.close()
    finally:
        srv.stop()
        if not args.keep_temp:
            shutil.rmtree(tmp, ignore_errors=True)
    meta = dict(when=datetime.datetime.now().strftime("%Y-%m-%d %H:%M"), commit=commit, preset=preset.get("setupDone"),
                desktop="보통 화면" if args.visible else "숨은 데스크톱 (사용자 화면에 안 보임)")
    ok, skip = write_report(rows, out, meta)
    log(f"\n보고서: {os.path.join(out, 'report.html')}  (비교 {len(ok)}, 건너뜀 {len(skip)})")
    log("가장 다른 10개:")
    for i, r in enumerate(ok[:10], 1):
        log(f"  {i:>2}. {r['score']:>6}  SSIM {r['ssim']:.3f}  차이 {r['mad'] * 100:4.1f}%  배치 {'—' if r.get('box_err') is None else r['box_err']}px  {r['title']}")
    for r in skip:
        log(f"  건너뜀 {r['id']}: {r.get('note')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
