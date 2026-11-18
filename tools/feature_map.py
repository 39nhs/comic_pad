"""기능 ↔ 코드 지도 만들기: native/기능_코드_지도.html

기능을 추가할 때마다 다시 돌린다:
    python tools/feature_map.py            (네이티브를 빌드하고 --dump-features 로 재료를 뽑아 HTML 로)
    python tools/feature_map.py --no-build (이미 빌드한 exe 로)

재료
  - 네이티브 앱이 직접 뽑은 목록 (UI/FeatureDump.cs): 등록된 메뉴·도구·패널마다
      등록한 곳 (호출 스택) · 실제 동작 코드 (동작 함수의 PDB 줄, 람다면 안에서 부르는 함수) · 웹 판 메뉴 → 네이티브 연결
  - 웹 판 코드 (app/js): registerCommand / C('id', '이름') 줄
  - 모듈 머리 설명 (IModule 파일 첫 주석), 자체 시험 (--selftest, COMICPAD_* 환경 변수)
링크: vscode://file/... (VS Code 가 있으면 그 줄로 열림) + ⧉ (경로:줄 복사)
"""
import bisect, datetime, html, json, os, re, subprocess, sys, tempfile

sys.stdout.reconfigure(encoding="utf-8")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NATIVE = os.path.join(ROOT, "native", "ComicPadNative")
EXE = os.path.join(NATIVE, "bin", "Release", "net8.0-windows", "ComicPadNative.exe")
WEB = os.path.join(ROOT, "app", "js")
OUT = os.path.join(ROOT, "native", "기능_코드_지도.html")


def read(p):
    with open(p, encoding="utf-8", errors="replace") as f:
        return f.read()


def rel(p):
    try:
        return os.path.relpath(p, ROOT).replace("\\", "/")
    except ValueError:
        return p.replace("\\", "/")


# ---- 1) 네이티브 재료 ---------------------------------------------------------------------
if "--no-build" not in sys.argv:
    r = subprocess.run(["dotnet", "build", NATIVE, "-c", "Release", "-nologo", "-v", "q"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        print(r.stdout[-3000:], r.stderr[-2000:])
        sys.exit("빌드 실패")
tmp = tempfile.mkdtemp(prefix="cp-featmap-")
dump = os.path.join(tmp, "features.json")
env = dict(os.environ, COMICPAD_DATA=os.path.join(tmp, "data"), COMICPAD_SETTINGS=os.path.join(tmp, "settings.json"))   # 사용자 설정·작업물은 건드리지 않게
subprocess.run([EXE, "--dump-features", dump], env=env, timeout=300, check=True)
D = json.load(open(dump, encoding="utf-8"))

# ---- 2) 웹 판 명령 줄 -----------------------------------------------------------------------
WEBCMD = {}
for dp, dn, fn in os.walk(WEB):
    dn[:] = [d for d in dn if d != "vendor"]
    for f in fn:
        if not f.endswith(".js"):
            continue
        p = os.path.join(dp, f)
        t = read(p)
        nl = [m.start() for m in re.finditer("\n", t)]
        def ln(i, nl=nl):
            return bisect.bisect_left(nl, i) + 1
        for m in re.finditer(r"\bC\(\s*'([\w.]+)'\s*,\s*'([^']+)'", t):
            WEBCMD.setdefault(m.group(1), {"label": m.group(2), "file": p, "line": ln(m.start())})
        for m in re.finditer(r"registerCommand\(\s*'([\w.]+)'\s*,\s*\{\s*label:\s*(['`\"])(.+?)\2", t):
            WEBCMD.setdefault(m.group(1), {"label": m.group(3), "file": p, "line": ln(m.start())})

# ---- 3) 모듈 설명·시험 -----------------------------------------------------------------------
MODULES, TESTS, seen = [], [], set()
for dp, dn, fn in os.walk(NATIVE):
    dn[:] = [d for d in dn if d not in ("bin", "obj")]
    for f in fn:
        if not f.endswith(".cs"):
            continue
        p = os.path.join(dp, f)
        lines = read(p).split("\n")
        for i, l in enumerate(lines):
            m = re.search(r"class\s+(\w+)\s*:\s*IModule", l)
            if m:
                head = []
                for hl in lines[:8]:
                    if hl.startswith("//"):
                        head.append(hl.lstrip("/ ").strip())
                    else:
                        break
                MODULES.append({"name": m.group(1), "desc": " ".join(head)[:500], "file": p, "line": i + 1, "dir": rel(dp)})
            for m in re.finditer(r'GetEnvironmentVariable\("(COMICPAD_\w*(?:TEST|SELFTEST|SHOT|PANELS)\w*)"\)', l):
                if m.group(1) not in seen:
                    seen.add(m.group(1))
                    TESTS.append({"name": m.group(1), "file": p, "line": i + 1})
            if f == "Program.cs":
                for m in re.finditer(r'args\[1\]\s*==\s*"(\w+)"', l):
                    TESTS.append({"name": "--selftest " + m.group(1), "file": p, "line": i + 1})
                for m in re.finditer(r'args\[0\]\s*==\s*"(--[\w-]+)"', l):
                    TESTS.append({"name": m.group(1), "file": p, "line": i + 1})


# ---- 4) HTML ---------------------------------------------------------------------------------
def esc(s):
    return html.escape(str(s) if s is not None else "")


def link(loc, via=None):
    if not loc or not loc.get("file"):
        return '<span class="none">—</span>'
    p, n = loc["file"], loc["line"]
    absp = os.path.abspath(p).replace("\\", "/")
    meth = f'<div class="sub">{esc(loc.get("method", ""))}</div>' if loc.get("method") else ""
    extra = ""
    if via and via.get("file"):
        va = os.path.abspath(via["file"]).replace("\\", "/")
        extra = f'<div class="sub">도우미: <a href="vscode://file/{esc(va)}:{via["line"]}">{esc(rel(via["file"]))}:{via["line"]}</a></div>'
    return (f'<a href="vscode://file/{esc(absp)}:{n}" title="VS Code 로 그 줄 열기">{esc(rel(p))}:{n}</a>'
            f'<button class="cp" data-c="{esc(os.path.abspath(p))}:{n}" title="경로:줄 복사">⧉</button>{meth}{extra}')


def impl_cell(m):
    if not m:
        return ""
    if m.get("inner"):
        return link(m["inner"]) + f'<div class="sub">감싼 단계: {link(m.get("impl"))}</div>'
    return link(m.get("impl"))


menus = {m["path"]: m for m in D["menus"]}
used = set()
sections = []
stats = {"web": 0, "webMissing": 0, "nativeOnly": 0}


def web_rows(items, depth=0):
    out = []
    for it in items:
        if it.get("sub"):
            out.append(f'<tr class="grp"><td colspan="4" style="padding-left:{10 + depth * 16}px">▸ {esc(it["label"])}</td></tr>')
            out += web_rows(it["sub"], depth + 1)
            continue
        if it.get("native"):
            continue                     # 네이티브 전용은 아래 따로
        key, path = it.get("key") or "", it.get("path")
        stats["web"] += 1
        m = menus.get(path) if path else None
        if path:
            used.add(path)
        w = WEBCMD.get(key)
        is_tool = key.startswith("@")
        cls = "ok" if m else ("tool" if is_tool else "miss")
        if not m and not is_tool:
            stats["webMissing"] += 1
        keytxt = f' · {esc(m["key"])}' if m and m.get("key") else ""
        sub = f'{esc(key)}{" → " + esc(path) if path else ""}{keytxt}'
        if m:
            reg = link(m.get("reg"), (m.get("reg") or {}).get("via"))
        elif is_tool:
            reg = '<span class="none">도구 바꾸기 (도구 표 참고)</span>'
        else:
            reg = '<span class="miss">네이티브 없음</span>'
        out.append(f'<tr class="{cls}"><td style="padding-left:{10 + depth * 16}px"><b>{esc(it["label"])}</b><div class="sub">{sub}</div></td>'
                   f'<td>{reg}</td><td>{impl_cell(m)}</td><td>{link(w) if w else ""}</td></tr>')
    return out


for top in D.get("webMenus", []):
    sections.append((f'{top["label"]} 메뉴', "menu", web_rows(top["items"])))

rows = []
for path in sorted(p for p in menus if p not in used):
    m = menus[path]
    stats["nativeOnly"] += 1
    keytxt = " · " + esc(m["key"]) if m.get("key") else ""
    rows.append(f'<tr class="nat"><td><b>{esc(path.split("/")[-1])}</b><div class="sub">{esc(path)}{keytxt}</div></td>'
                f'<td>{link(m.get("reg"), (m.get("reg") or {}).get("via"))}</td><td>{impl_cell(m)}</td><td></td></tr>')
sections.append(("네이티브에만 있는 메뉴 항목", "nat", rows))
rows = [f'<tr><td><b>{esc(t["name"])}</b><div class="sub">도구 id: {esc(t["id"])} · {esc(t["type"].split(".")[-1])}</div></td><td>{link(t.get("reg"))}</td><td>{link(t.get("impl"))}</td><td></td></tr>'
        for t in D["tools"]]
sections.append(("도구 (왼쪽 도구 막대)", "tool", rows))
rows = [f'<tr><td><b>{esc(p["title"])}</b><div class="sub">자리: {esc(p["dock"])}</div></td><td>{link(p.get("reg"))}</td><td>{link(p.get("impl"))}</td><td></td></tr>' for p in D["panels"]]
sections.append(("패널", "panel", rows))
rows = [f'<tr><td><b>{esc(m["name"])}</b><div class="sub">{esc(m["dir"])}</div><div class="desc">{esc(m["desc"])}</div></td><td>{link(m)}</td><td></td><td></td></tr>'
        for m in sorted(MODULES, key=lambda x: x["dir"])]
sections.append(("모듈 (기능 묶음 — 폴더와 설명)", "mod", rows))
rows = [f'<tr><td><b>{esc(t["name"])}</b></td><td>{link(t)}</td><td></td><td></td></tr>' for t in TESTS]
sections.append(("자체 시험 · 명령줄", "test", rows))

try:
    commit = subprocess.run(["git", "-C", ROOT, "log", "-1", "--format=%h %s"], capture_output=True, text=True, encoding="utf-8").stdout.strip()
except Exception:
    commit = ""
now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
parts = []
for title, kind, rows in sections:
    n = sum(1 for r in rows if not r.startswith('<tr class="grp"'))
    parts.append(f'<details open class="sec {kind}"><summary>{esc(title)} <span class="n">{n}</span></summary><div class="wrap"><table>'
                 f'<thead><tr><th>기능</th><th>등록한 곳</th><th>실제 동작 코드</th><th>웹 판 코드</th></tr></thead><tbody>{"".join(rows)}</tbody></table></div></details>')

page = f"""<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>기능 코드 지도</title><style>
:root{{--bg:#26262b;--panel:#2f2f35;--line:#4a4a54;--text:#e8e8ec;--muted:#9c9ca8;--accent:#e8a33d;--blue:#7fb2ff;--red:#ef8a8a}}
*{{box-sizing:border-box}} body{{margin:0;background:var(--bg);color:var(--text);font:13px/1.45 "Malgun Gothic","Segoe UI",sans-serif}}
header{{position:sticky;top:0;background:var(--panel);border-bottom:1px solid #1c1c20;padding:10px 16px;display:flex;gap:8px 14px;align-items:center;flex-wrap:wrap;z-index:2}}
h1{{font-size:16px;margin:0;color:var(--accent)}} .meta{{color:var(--muted);font-size:12px}}
#q{{background:var(--bg);border:1px solid var(--line);color:var(--text);border-radius:8px;padding:7px 10px;width:min(340px,100%);font:inherit}}
label.f{{color:var(--muted);font-size:12px;display:flex;gap:4px;align-items:center}}
main{{padding:12px 16px 40px}} .sec{{background:var(--panel);border-radius:10px;margin:0 0 12px;overflow:hidden}}
summary{{cursor:pointer;padding:10px 14px;font-weight:700;font-size:14px}} summary .n{{color:var(--muted);font-weight:400;font-size:12px;margin-left:6px}}
.wrap{{overflow-x:auto}} table{{width:100%;border-collapse:collapse;table-layout:fixed;min-width:720px}}
th{{text-align:left;color:var(--muted);font-weight:400;font-size:12px;padding:6px 10px;border-top:1px solid var(--line)}}
td{{padding:6px 10px;border-top:1px solid #38383f;vertical-align:top;word-break:break-all}} th:first-child,td:first-child{{width:32%}}
tr.grp td{{color:var(--accent);font-weight:700;background:#2a2a30}}
.sub{{color:var(--muted);font-size:11.5px}} .desc{{color:#c6c6cc;font-size:12px;margin-top:2px}}
a{{color:var(--blue);text-decoration:none}} a:hover{{text-decoration:underline}}
.cp{{background:none;border:0;color:var(--muted);cursor:pointer;margin-left:4px;font-size:12px}} .cp:hover{{color:var(--accent)}}
.none{{color:#77777f}} .miss{{color:var(--red)}} tr.nat td:first-child b::after{{content:" 네이티브 전용";color:var(--accent);font-size:11px;font-weight:400;margin-left:6px}}
.hidden{{display:none}}
</style></head><body>
<header><h1>기능 코드 지도</h1><input id="q" placeholder="기능·파일·웹 id 로 찾기 (예: 레이어, Brush, file.export)">
<label class="f"><input type="checkbox" id="miss"> 네이티브 없는 것만</label>
<span class="meta">만든 때 {now} · 커밋 {esc(commit)}<br>웹 메뉴 항목 {stats['web']} (네이티브 없음 {stats['webMissing']}) · 네이티브 전용 {stats['nativeOnly']} · 도구 {len(D['tools'])} · 패널 {len(D['panels'])} · 모듈 {len(MODULES)}</span>
<span class="meta">등록한 곳 = 메뉴·도구를 등록한 줄 · 실제 동작 코드 = 눌렀을 때 도는 함수 · 링크 = VS Code 로 그 줄 열기 · ⧉ = 경로:줄 복사</span></header>
<main>{"".join(parts)}</main>
<script>
const q=document.getElementById('q'),mo=document.getElementById('miss');
function f(){{const v=q.value.trim().toLowerCase(),only=mo.checked;
 document.querySelectorAll('.sec').forEach(s=>{{let any=0;s.querySelectorAll('tbody tr').forEach(r=>{{const ok=(!v||r.textContent.toLowerCase().includes(v))&&(!only||r.classList.contains('miss'));r.classList.toggle('hidden',!ok);any+=ok}});s.classList.toggle('hidden',!any);if(v||only)s.open=true}})}}
q.addEventListener('input',f);mo.addEventListener('change',f);
document.addEventListener('click',e=>{{const b=e.target.closest('.cp');if(!b)return;navigator.clipboard?.writeText(b.dataset.c);b.textContent='✓';setTimeout(()=>b.textContent='⧉',900)}});
</script></body></html>"""
with open(OUT, "w", encoding="utf-8") as f:
    f.write(page)
print(f"{OUT}\n웹 메뉴 {stats['web']} (네이티브 없음 {stats['webMissing']}), 네이티브 전용 {stats['nativeOnly']}, 도구 {len(D['tools'])}, 패널 {len(D['panels'])}, 모듈 {len(MODULES)}, 시험 {len(TESTS)}")
