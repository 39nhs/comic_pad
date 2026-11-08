"""app/ai-models.json 의 모델 파일마다 SHA-256 을 적어 고정한다 (받은 모델이 바꿔치기·손상됐는지 확인용).

- Hugging Face: 'main' 대신 지금 커밋으로 주소를 고정하고, 대용량(LFS) 파일의 SHA-256 은 서버가 알려 주는 값을 씀 (다시 받지 않음)
- MediaPipe: 'latest' 대신 번호 붙은 판으로 고정
- 그 밖(GitHub 릴리스 등): 한 번 받아서 계산
모델 목록을 바꾼 뒤 다시 실행:  python tools/pin_models.py
"""
import hashlib
import json
import os
import re
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REG = os.path.join(ROOT, "app", "ai-models.json")
UA = {"User-Agent": "ComicPad-pin"}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def head(url):
    """리다이렉트를 따라가지 않는 HEAD (HF 는 첫 응답에 X-Linked-Etag/X-Linked-Size 를 줌)"""
    op = urllib.request.build_opener(NoRedirect)
    req = urllib.request.Request(url, method="HEAD", headers=UA)
    try:
        r = op.open(req, timeout=30)
        return r.status, r.headers
    except urllib.error.HTTPError as e:
        return e.code, e.headers


def sha_download(url, tries=4):
    import time
    for i in range(tries):
        try:
            h, n = hashlib.sha256(), 0
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=120) as r:
                for b in iter(lambda: r.read(1 << 20), b""):
                    h.update(b)
                    n += len(b)
            return h.hexdigest(), n
        except (urllib.error.URLError, OSError) as e:
            print("  다시 시도", i + 1, url[:80], e)
            time.sleep(3 * (i + 1))
    raise RuntimeError("받지 못함: " + url)


HF_COMMIT = {}
# GitHub 릴리스 파일은 가끔 직접 받기가 504 로 실패해 gh release download 로 받아 계산한 값 (2026-11-08)
KNOWN = {
    "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesr-animevideov3.pth":
        "b8a8376811077954d82ca3fcf476f1ac3da3e8a68a4f4d71363008000a18b75d",
    "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.2.4/RealESRGAN_x4plus_anime_6B.pth":
        "f872d837d3c90ed2e05227bed711af5671a6fd1c9f7d7e91c911a61f155e99da",
}


def pin(f):
    url = f.get("url")
    if not url:
        return "-"
    m = re.match(r"https://huggingface\.co/([^/]+/[^/]+)/resolve/([^/]+)/(.+)$", url)
    if m:
        repo, rev, path = m.groups()
        if not re.fullmatch(r"[0-9a-f]{40}", rev):
            if repo not in HF_COMMIT:
                with urllib.request.urlopen(urllib.request.Request(f"https://huggingface.co/api/models/{repo}", headers=UA), timeout=30) as r:
                    HF_COMMIT[repo] = json.load(r)["sha"]
            rev = HF_COMMIT[repo]
            f["url"] = url = f"https://huggingface.co/{repo}/resolve/{rev}/{path}"
        st, hd = head(url)
        etag = (hd.get("X-Linked-Etag") or hd.get("ETag") or "").strip('"').replace("W/", "").strip('"')
        size = hd.get("X-Linked-Size") or hd.get("Content-Length")
        if re.fullmatch(r"[0-9a-f]{64}", etag):
            f["sha256"] = etag
            if size:
                f["size"] = int(size)
            return "hf-lfs"
        f["sha256"], f["size"] = sha_download(url)
        return "hf-download"
    if "storage.googleapis.com/mediapipe-models/" in url and "/latest/" in url:
        fixed = url.replace("/latest/", "/1/")
        st, _ = head(fixed)
        if st == 200:
            f["url"] = url = fixed
    f["sha256"], n = sha_download(url)
    if f.get("size") and f["size"] != n:
        print("  ! 크기 다름", url, f["size"], n)
    f["size"] = n
    return "download"


def main():
    with open(REG, encoding="utf-8") as fh:
        reg = json.load(fh)
    for m in reg["models"]:
        for f in m.get("files", []) + ((m.get("convert") or {}).get("from") or []):
            if f.get("sha256") and "--force" not in sys.argv:
                continue
            print(f"  {m['id']} {f['name']} ...", flush=True)
            if f.get("url") in KNOWN:
                f["sha256"] = KNOWN[f["url"]]
                how = "known"
            else:
                try:
                    how = pin(f)
                except Exception as e:   # noqa: BLE001 — 하나가 실패해도 나머지는 저장
                    print("  ! 실패:", e)
                    continue
            print(f"{m['id']:16} {f['name']:40} {how:12} {f.get('sha256', '')[:16]}")
    with open(REG, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(reg, fh, ensure_ascii=False, indent=1)
        fh.write("\n")


if __name__ == "__main__":
    main()
