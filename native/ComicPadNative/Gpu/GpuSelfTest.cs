// GPU 자체 시험: --selftest gpu 결과폴더 (끝 코드 = 실패 수, report.txt)
//  1) 장치: 그래픽 카드(자동) + WARP(소프트웨어 D3D) — 카드가 없으면 WARP 로만
//  2) GPU 합성 = CPU 합성 (채널 차이 ≤ 2): parity_test.cpad, 합성 모드 20가지 하나씩·모두 쌓기, 클리핑(표준·다른 모드·조정·숨김),
//     마스크·레이어 컬러·폴더(겹친 폴더·모드·불투명도)·초안 빼기·보이기, 조정 레이어, CPU 로 넘기는 것(효과·톤·용지 결)
//  3) 타일 올리기: 일부만 고친 뒤 그 영역만 다시 합성해도 전체가 CPU 와 같고, 바뀐 타일만 올라감 / 칸이 모자라 조각을 줄여도 같음
//  4) 화면: 보기 변환 (확대 = CPU 와 같음, 픽셀 아트 보기 = 정확히 같음, 축소 평균 = 같은 상자 거르기와 가까움), 선택 테두리, 겹침 그림
//  5) GPU 끔(CPU 길): 같은 그림판이 GPU ↔ CPU 로 바뀌고 화면이 같음, DrawToBitmap(화면 찍기)이 GPU 화면을 읽음
//  6) 시간 재기: B5 600dpi (4441×6213) 20 레이어 — 전체 합성, 이동·확대 한 장, 붓질 한 번 (CPU ↔ GPU)
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Gpu;

public static class GpuSelfTest
{
    static readonly List<string> log = new();
    static int fails;
    static string outDir;

    static void Line(string s) { log.Add(s); Console.WriteLine(s); }
    static void Check(bool ok, string name, string detail = "")
    {
        if (!ok) fails++;
        Line((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? " — " + detail : ""));
    }

    public static int Run(string dir)
    {
        outDir = dir; log.Clear(); fails = 0;
        Directory.CreateDirectory(dir);
        try
        {
            var hw = GpuDevice.Create("auto", out string e1);
            Line(hw != null ? $"장치(자동): {hw.AdapterName} · D3D11 {GpuDevice.LevelName(hw.Level)} · 전용 {hw.DedicatedMB}MB · 공유 {hw.SharedMB}MB · 셰이더 {hw.ShaderSource}" : "장치(자동): 없음 — " + e1);
            var warp = GpuDevice.Create("warp", out string e2);
            Line(warp != null ? $"장치(WARP): {warp.AdapterName} · D3D11 {GpuDevice.LevelName(warp.Level)}" : "장치(WARP): 없음 — " + e2);
            Check(GpuDevice.Create("off", out _) == null, "설정 off → 장치 만들지 않음");
            Check(hw != null || warp != null, "D3D11 장치 (카드 또는 WARP)");
            bool benchOnly = Environment.GetEnvironmentVariable("COMICPAD_GPU_BENCHONLY") == "1";   // 시간 재기만 (메모리 적게)
            foreach (var (g, label) in new[] { (hw, "GPU"), (warp, "WARP") })
            {
                if (g == null || benchOnly) continue;
                Line($"==== {label}: {g.AdapterName}");
                Compose(g, label);
                Tiles(g, label);
                View(g, label);
            }
            warp?.Dispose();
            var main = hw;
            if (main == null) { main = GpuDevice.Create("warp", out _); Line("(그래픽 카드가 없어 WARP 로 그림판·시간 재기)"); }
            main?.Dispose();
            if (Environment.GetEnvironmentVariable("COMICPAD_GPU_NOCANVAS") != "1" && !benchOnly) CanvasSwitch();
            GC.Collect(); GC.WaitForPendingFinalizers();   // 창을 띄우는 시험 (1 이면 건너뜀)
            if (Environment.GetEnvironmentVariable("COMICPAD_GPU_NOBENCH") != "1") Bench();
        }
        catch (Exception ex) { Check(false, "예외", ex.ToString()); }
        File.WriteAllLines(Path.Combine(dir, "report.txt"), log);
        return fails;
    }

    // ---- 비교 도우미 ----------------------------------------------------------------------
    static (int max, int count) Diff(uint[] a, uint[] b)
    {
        int mx = 0, n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            uint x = a[i], y = b[i];
            if (x == y) continue;
            int d = 0;
            for (int s = 0; s < 32; s += 8) d = Math.Max(d, Math.Abs((int)((x >> s) & 255) - (int)((y >> s) & 255)));
            if (d > 0) n++;
            mx = Math.Max(mx, d);
        }
        return (mx, n);
    }

    static uint[] Cpu(Document d, RenderOptions o) => new Compositor().Render(d, new Rect(0, 0, d.W, d.H), o);
    static uint[] GpuR(GpuCompositor gc, Document d, RenderOptions o, Rect? r = null)
    {
        var rr = r ?? new Rect(0, 0, d.W, d.H);
        var a = new uint[rr.W * rr.H];
        gc.Render(d, rr, o, null, a);
        return a;
    }

    static void Same(GpuCompositor gc, Document d, string name, RenderOptions o = null, int tol = 2)
    {
        var c = Cpu(d, o);
        var g = GpuR(gc, d, o);
        var (mx, n) = Diff(c, g);
        Check(mx <= tol, name, $"최대 차이 {mx}, 다른 픽셀 {n}/{c.Length}" + (gc.CpuLayers + gc.CpuHooks > 0 ? $", CPU 로 넘긴 레이어 {gc.CpuLayers}·보정/용지 {gc.CpuHooks}" : "") + (gc.GpuAdjusts > 0 ? $", GPU 보정 {gc.GpuAdjusts}" : ""));
    }

    static string Root()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "native", "parity_test.cpad"))) return Path.Combine(dir.FullName, "native");
                if (File.Exists(Path.Combine(dir.FullName, "parity_test.cpad"))) return dir.FullName;
            }
        }
        return null;
    }

    // ---- 합성 비교 --------------------------------------------------------------------------
    static readonly string[] AllModes = { "source-over", "multiply", "screen", "overlay", "darken", "lighten", "color-dodge", "color-burn", "hard-light", "soft-light", "difference", "hue", "saturation", "color", "luminosity", "lighter", "source-atop", "destination-in", "destination-out", "copy" };

    static Layer Add(Document d, string name, Action<Surface> paint, string blend = "source-over", float op = 1)
    {
        var l = new Layer(d, name) { Blend = blend, Opacity = op };
        paint?.Invoke(l.Px);
        d.Layers.Add(l); d.ActiveId = l.Id;
        return l;
    }
    static uint Pm(int a, int r, int g, int b) => Px.Premul(Px.Pack((uint)a, (uint)r, (uint)g, (uint)b));

    /// <summary>색·알파가 고르게 바뀌는 무늬 (경계 타일·빈 타일 포함)</summary>
    static void Pattern(Surface s, int seed, double cover = 0.8, bool hole = true)
    {
        var rnd = new Random(seed);
        int cx = rnd.Next(s.W), cy = rnd.Next(s.H);
        double rr = Math.Max(s.W, s.H) * cover;
        for (int y = 0; y < s.H; y++)
            for (int x = 0; x < s.W; x++)
            {
                double dd = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (dd > rr) continue;
                if (hole && x / 64 % 3 == 1 && y / 64 % 2 == 1) continue;
                int a = (int)(255 * Math.Clamp(1.2 - dd / rr, 0, 1));
                if ((x / 37 + y / 23 + seed) % 5 == 0) a = 255;
                int r = (x * 7 + seed * 31) & 255, g = (y * 5 + seed * 17) & 255, b = ((x + y) * 3 + seed * 11) & 255;
                s.Set(x, y, Pm(a, r, g, b));
            }
    }

    static Document NewDoc(int w, int h, bool paper = true)
    {
        var d = new Document(w, h) { PaperVisible = paper };
        return d;
    }

    static void Compose(GpuDevice g, string label)
    {
        using var gc = new GpuCompositor(g);
        // parity_test.cpad
        string root = Root();
        if (root != null)
        {
            var d = CpadIO.Load(Path.Combine(root, "parity_test.cpad"));
            Same(gc, d, $"[{label}] parity_test.cpad ({d.W}×{d.H}, 레이어 {d.Layers.Count}) 기본");
            Same(gc, d, $"[{label}] parity_test.cpad 내보내기(초안 빼기)", new RenderOptions { ExcludeDraft = true });
            Same(gc, d, $"[{label}] parity_test.cpad 화면(어니언)", new RenderOptions { Onion = true });
            Same(gc, d, $"[{label}] parity_test.cpad 용지 없이", new RenderOptions { Paper = false });
            // 내보내기 결과를 그림으로 (눈으로 보기)
            var flat = new Surface(d.W, d.H);
            flat.Write(flat.Full, GpuR(gc, d, new RenderOptions { ExcludeDraft = true }));
            using (var bmp = CpadIO.ToBitmap(flat, flat.Full)) bmp.Save(Path.Combine(outDir, $"parity_{label}.png"));
        }
        else Check(false, "parity_test.cpad 를 찾지 못함");

        // 합성 모드 하나씩 (경계 타일이 걸리게 300×280)
        foreach (var m in AllModes)
        {
            var d = NewDoc(300, 280, paper: false);
            Add(d, "아래", s => Pattern(s, 3));
            Add(d, m, s => Pattern(s, 9, 0.7), m, 0.75f);
            Same(gc, d, $"[{label}] 모드 {m} (불투명도 0.75)");
        }
        // 모두 쌓기 + 용지
        {
            var d = NewDoc(600, 530);
            int i = 0;
            foreach (var m in AllModes.Where(x => x != "copy" && x != "destination-in"))
                Add(d, m, s => Pattern(s, 20 + i, 0.5 + (i % 4) * 0.1), m, i++ % 3 == 0 ? 1f : 0.6f);
            Same(gc, d, $"[{label}] 모드 {AllModes.Length - 2}가지 쌓기 + 용지");
        }
        // 클리핑
        {
            var d = NewDoc(520, 400);
            Add(d, "바탕", s => Pattern(s, 1, 1.2, false));
            Add(d, "기준", s => Pattern(s, 2, 0.5));
            Add(d, "클립 표준", s => Pattern(s, 3, 0.9), op: 0.8f).Clip = true;
            Add(d, "클립 곱하기", s => Pattern(s, 4, 0.9), "multiply").Clip = true;
            Add(d, "클립 스크린", s => Pattern(s, 5, 0.9), "screen", 0.5f).Clip = true;
            var hid = Add(d, "숨긴 클립", s => Pattern(s, 6, 0.9), "difference"); hid.Clip = true; hid.Visible = false;
            var adj = new Layer { Name = "클립 조정", Kind = "adjust", Clip = true, Meta = new JsonObject { ["type"] = "invert" } };
            d.Layers.Add(adj);
            Add(d, "클립 색조", s => Pattern(s, 7, 0.9), "hue", 0.9f).Clip = true;
            Add(d, "위", s => Pattern(s, 8, 0.4), "overlay");
            Same(gc, d, $"[{label}] 클리핑 (표준·곱하기·스크린·숨김·조정·색조)");
        }
        // 마스크·컬러·폴더·초안·보이기
        {
            var d = NewDoc(700, 600);
            Add(d, "바탕", s => Pattern(s, 11, 1.2, false));
            var f1 = new Layer { Name = "폴더 곱하기", Kind = "folder", Blend = "multiply", Opacity = 0.7f }; d.Layers.Add(f1);
            var a = Add(d, "마스크", s => Pattern(s, 12)); a.Parent = f1.Id; a.Mask = new Surface(d.W, d.H); Pattern(a.Mask, 13, 0.6);
            var b = Add(d, "컬러", s => Pattern(s, 14), "screen", 0.8f); b.Parent = f1.Id; b.Tint = "#3366ff";
            var f2 = new Layer { Name = "안쪽 폴더", Kind = "folder", Blend = "overlay", Opacity = 0.9f, Parent = f1.Id }; d.Layers.Add(f2);
            var c = Add(d, "안쪽", s => Pattern(s, 15)); c.Parent = f2.Id;
            var c2 = Add(d, "안쪽 클립", s => Pattern(s, 16), "color-burn"); c2.Parent = f2.Id; c2.Clip = true;
            var f2m = f2; f2m.Mask = new Surface(d.W, d.H); Pattern(f2m.Mask, 17, 0.8, false);
            var dr = Add(d, "초안", s => Pattern(s, 18, 0.5), "darken"); dr.Draft = true;
            var inv = Add(d, "숨김", s => Pattern(s, 19)); inv.Visible = false;
            var zero = Add(d, "불투명도 0", s => Pattern(s, 20)); zero.Opacity = 0;
            var off = Add(d, "마스크 끔", s => Pattern(s, 21, 0.3), "lighten"); off.Mask = new Surface(d.W, d.H); off.MaskOn = false;
            Same(gc, d, $"[{label}] 마스크·컬러·폴더(겹침·모드·마스크)·초안 보임");
            Same(gc, d, $"[{label}] 같은 문서 초안 빼기", new RenderOptions { ExcludeDraft = true });
        }
        // 조정 레이어 (아래 전체 / 폴더 안 / 마스크 / 불투명도)
        {
            var d = NewDoc(500, 450);
            Add(d, "바탕", s => Pattern(s, 31, 1.2, false));
            Add(d, "그림", s => Pattern(s, 32));
            var lv = new Layer { Name = "레벨", Kind = "adjust", Opacity = 0.8f, Meta = new JsonObject { ["type"] = "levels", ["params"] = new JsonObject { ["inB"] = 30, ["inW"] = 220, ["gamma"] = 1.4, ["outB"] = 0, ["outW"] = 255 } } };
            d.Layers.Add(lv);
            var hs = new Layer { Name = "색조", Kind = "adjust", Meta = new JsonObject { ["type"] = "hsl", ["params"] = new JsonObject { ["hue"] = 40, ["sat"] = 30, ["light"] = 0 } } };
            hs.Mask = new Surface(d.W, d.H); Pattern(hs.Mask, 33, 0.7);
            d.Layers.Add(hs);
            var f = new Layer { Name = "폴더", Kind = "folder" }; d.Layers.Add(f);
            var k = Add(d, "폴더 안", s => Pattern(s, 34, 0.6), "multiply"); k.Parent = f.Id;
            d.Layers.Add(new Layer { Name = "폴더 안 반전", Kind = "adjust", Parent = f.Id, Meta = new JsonObject { ["type"] = "invert" } });
            Add(d, "맨 위", s => Pattern(s, 35, 0.3));
            Same(gc, d, $"[{label}] 조정 레이어 (레벨·색조+마스크·폴더 안 반전, 용지 포함)");
        }
        // 조정 종류 모두 (GPU 보정 = CPU 필터 묶음, 반투명 그림 위에서)
        foreach (var (type, prm) in new (string, JsonObject)[]
        {
            ("levels", new JsonObject { ["inB"] = 20, ["inW"] = 230, ["gamma"] = 0.7 }),
            ("curves", new JsonObject { ["master"] = new JsonArray(new JsonArray(0, 0), new JsonArray(90, 140), new JsonArray(255, 255)), ["r"] = new JsonArray(new JsonArray(0, 30), new JsonArray(255, 230)) }),
            ("hsl", new JsonObject { ["hue"] = -70, ["sat"] = 45, ["light"] = -20 }),
            ("balance", new JsonObject { ["sr"] = 40, ["mg"] = -30, ["hb"] = 25 }),
            ("bc", new JsonObject { ["b"] = 25, ["c"] = 40 }),
            ("gradmap", null), ("posterize", new JsonObject { ["levels"] = 5 }), ("threshold", new JsonObject { ["t"] = 100 }),
            ("invert", null), ("bw", null), ("colorize", new JsonObject { ["color"] = "#40a0c0", ["amount"] = 70 }), ("vibrance", new JsonObject { ["v"] = 60 }),
        })
        {
            var d = NewDoc(300, 260, paper: false);
            Add(d, "그림", s => Pattern(s, 90, 0.9));
            var adj = new Layer { Name = type, Kind = "adjust", Opacity = 0.85f, Meta = new JsonObject { ["type"] = type } };
            if (prm != null) adj.Meta["params"] = prm;
            d.Layers.Add(adj);
            Same(gc, d, $"[{label}] 조정 {type}");
        }
        // CPU 로 넘기는 것: 레이어 효과·톤·용지 결
        {
            var d = NewDoc(400, 380);
            Add(d, "바탕", s => Pattern(s, 41, 1.2, false));
            var fx = Add(d, "효과", s => Pattern(s, 42, 0.4), "multiply");
            fx.Fx = new JsonObject { ["stroke"] = new JsonObject { ["on"] = true, ["size"] = 4, ["color"] = "#ff0000" }, ["shadow"] = new JsonObject { ["on"] = true, ["dx"] = 6, ["dy"] = 6, ["blur"] = 8 } };
            var tn = Add(d, "톤", s => Pattern(s, 43, 0.5));
            tn.Tone = new JsonObject { ["lpi"] = 60, ["angle"] = 45 };
            d.PaperExtra["texture"] = new JsonObject { ["type"] = "fine", ["strength"] = 0.6, ["scale"] = 1, ["over"] = true };
            Same(gc, d, $"[{label}] 레이어 효과·톤·용지 결(위에도) → 그 부분만 CPU");
            Check(gc.CpuLayers > 0 && gc.CpuHooks > 0, $"[{label}] CPU 로 넘긴 것이 실제로 있음", $"레이어 {gc.CpuLayers}, 보정·용지 {gc.CpuHooks}");
        }
        // 채우기 참조(Only)는 GPU 가 하지 않음
        Check(!GpuCompositor.Supports(NewDoc(10, 10), new RenderOptions { Only = new HashSet<Layer>() }), $"[{label}] Only 합성은 전체 CPU 로");
    }

    // ---- 타일 올리기 -----------------------------------------------------------------------
    static void Tiles(GpuDevice g, string label)
    {
        var d = NewDoc(1500, 1100);
        var a = Add(d, "A", s => Pattern(s, 51));
        var b = Add(d, "B", s => Pattern(s, 52, 0.5), "multiply", 0.9f);
        var c = Add(d, "C", s => Pattern(s, 53, 0.4), "screen");
        using var gv = new GpuView(g, IntPtr.Zero, 64, 64);
        var full = new Rect(0, 0, d.W, d.H);
        gv.Composite(d, full, null);
        long up0 = gv.Compositor.Cache.Uploads;
        // 같은 것을 다시: 올릴 것 없음
        gv.Composite(d, full, null);
        Check(gv.Compositor.Cache.Uploads == up0, $"[{label}] 바뀐 것 없으면 타일을 다시 올리지 않음", $"첫 합성 {up0}장, 두 번째 +{gv.Compositor.Cache.Uploads - up0}");
        // (1) 있는 타일을 제자리에서 고침 (Touch 없이 — 붓질처럼)
        var t = b.Px.Tile(1, 1, true);
        for (int y = 10; y < 60; y++) for (int x = 20; x < 90; x++) t[y * 256 + x] = Pm(255, 10, 200, 30);
        // (2) 빈 타일에 새로 그림
        a.Px.Set(1450, 1050, Pm(200, 255, 0, 0)); a.Px.Set(1451, 1050, Pm(255, 0, 0, 255));
        // (3) 타일을 통째로 비움 (실행취소처럼)
        c.Px.SetTile(2, 2, null);
        // (4) 타일 배열을 새 것으로 바꿈
        var nt = new uint[256 * 256]; Array.Fill(nt, Pm(128, 50, 60, 70)); a.Px.SetTile(4, 0, nt);
        long up1 = gv.Compositor.Cache.Uploads;
        var dirty = new Rect(256 + 20, 256 + 10, 70, 50).Union(new Rect(1450, 1050, 2, 1)).Union(c.Px.TileRect(2 * c.Px.TX + 2)).Union(a.Px.TileRect(4));
        // 영역마다 따로 (붓질처럼 작은 영역)
        gv.Composite(d, new Rect(256 + 20, 256 + 10, 70, 50), null);
        gv.Composite(d, new Rect(1450, 1050, 2, 1), null);
        gv.Composite(d, c.Px.TileRect(2 * c.Px.TX + 2), null);
        gv.Composite(d, a.Px.TileRect(4), null);
        long upN = gv.Compositor.Cache.Uploads - up1;
        var got = gv.ReadComp(full);
        var (mx, n) = Diff(Cpu(d, null), got);
        Check(mx <= 2, $"[{label}] 일부 고친 뒤 고친 영역만 다시 합성 = CPU 전체 합성", $"최대 차이 {mx}, 다른 픽셀 {n}");
        Check(upN >= 3 && upN <= 6, $"[{label}] 바뀐 타일만 올림", $"{upN}장 (고친 타일 3장 + 비운 타일은 올리지 않음)");
        // 칸이 아주 적은 저장소: 조각을 줄여 가며 그려도 같음
        using (var small = new GpuCompositor(g, 24))
        {
            var d2 = NewDoc(1300, 900);
            for (int i = 0; i < 6; i++) Add(d2, "L" + i, s => Pattern(s, 60 + i, 0.6), i % 2 == 0 ? "source-over" : "multiply", 0.85f);
            Same(small, d2, $"[{label}] 타일 칸 24개(조각 줄이기·칸 비우기)", null);
        }
    }

    // ---- 화면 (보기 변환) ---------------------------------------------------------------------
    /// <summary>CanvasView.RenderScreen 과 같은 계산 (k 를 정해서)</summary>
    static uint[] CpuView(uint[] comp, int W, int H, byte[] sel, ViewXf V, int bw, int bh, int k)
    {
        var o = new uint[bw * bh];
        uint bg = 0xFF4B4B52;
        var (ox, oy) = V.ToDoc(0, 0); var (xx, xy) = V.ToDoc(1, 0); var (yx, yy) = V.ToDoc(0, 1);
        double ax = xx - ox, ay = xy - oy, bxv = yx - ox, byv = yy - oy;
        static uint Opq(uint c) => (c >> 24) == 255 ? c : Px.Over(c, 0xFFFFFFFF) | 0xFF000000;
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                double dx = ox + ax * (x + 0.5) + bxv * (y + 0.5), dy = oy + ay * (x + 0.5) + byv * (y + 0.5);
                uint c;
                if (k == 1)
                {
                    int ix = (int)Math.Floor(dx), iy = (int)Math.Floor(dy);
                    c = (uint)ix >= (uint)W || (uint)iy >= (uint)H ? bg : Opq(comp[iy * W + ix]);
                }
                else
                {
                    uint sb = 0, sg = 0, srr = 0; int n = 0, outside = 0;
                    for (int j = 0; j < k; j++) for (int i = 0; i < k; i++)
                    {
                        double fx = dx + ax * ((i + 0.5) / k - 0.5) + bxv * ((j + 0.5) / k - 0.5), fy = dy + ay * ((i + 0.5) / k - 0.5) + byv * ((j + 0.5) / k - 0.5);
                        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                        if ((uint)ix >= (uint)W || (uint)iy >= (uint)H) { outside++; continue; }
                        uint v = Opq(comp[iy * W + ix]);
                        sb += v & 255; sg += (v >> 8) & 255; srr += (v >> 16) & 255; n++;
                    }
                    c = n == 0 || outside > n ? bg : 0xFF000000 | (srr / (uint)n << 16) | (sg / (uint)n << 8) | (sb / (uint)n);
                }
                if (sel != null)
                {
                    bool In(double fx, double fy) { int a = (int)Math.Floor(fx), b2 = (int)Math.Floor(fy); return (uint)a < (uint)W && (uint)b2 < (uint)H && sel[b2 * W + a] > 127; }
                    bool here = In(dx, dy);
                    if (here != In(dx + ax, dy + ay) || here != In(dx + bxv, dy + byv)) c = ((x + y) / 4 % 2 == 0) ? 0xFF000000 : 0xFFFFFFFF;
                }
                o[y * bw + x] = c;
            }
        return o;
    }

    static unsafe uint[] Pixels(Bitmap b)
    {
        var a = new uint[b.Width * b.Height];
        var bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < b.Height; y++) { uint* r = (uint*)((byte*)bd.Scan0 + (long)y * bd.Stride); for (int x = 0; x < b.Width; x++) a[y * b.Width + x] = r[x] | 0xFF000000; }
        b.UnlockBits(bd);
        return a;
    }
    static unsafe Bitmap ToBmp(uint[] px, int w, int h)
    {
        var b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = b.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) { uint* r = (uint*)((byte*)bd.Scan0 + (long)y * bd.Stride); for (int x = 0; x < w; x++) r[x] = px[y * w + x]; }
        b.UnlockBits(bd);
        return b;
    }

    static void View(GpuDevice g, string label)
    {
        var d = NewDoc(1000, 760, paper: false);
        Add(d, "A", s => Pattern(s, 71, 0.8));
        Add(d, "B", s => Pattern(s, 72, 0.5), "multiply");
        d.Sel.Mask = new byte[d.W * d.H];
        for (int y = 200; y < 500; y++) for (int x = 150; x < 700; x++) if ((x - 400) * (x - 400) + (y - 350) * (y - 350) < 200 * 200) d.Sel.Mask[y * d.W + x] = 255;
        int bw = 640, bh = 480;
        using var gv = new GpuView(g, IntPtr.Zero, bw, bh);
        gv.Composite(d, new Rect(0, 0, d.W, d.H), null);
        var comp = gv.ReadComp(new Rect(0, 0, d.W, d.H));
        // 회전 없는 보기는 줄·칸 표(double)로 경계에 딱 걸려도 같아야 함. 회전 보기는 float 계산이라 경계가 아닌 값으로
        var cases = new (string name, double zoom, double px, double py, double ang, bool flip, bool crisp, bool exact)[]
        {
            ("확대 1, 이동 정수+0.25", 1, -120.25, -60.25, 0, false, false, true),
            ("확대 3.5", 3.5, -900.3, -700.7, 0, false, false, true),
            ("확대 2.2·회전 30·반전", 2.2, 300.4, -500.6, 30, true, false, false),
            ("픽셀 아트 보기 축소 0.3713", 0.3713, 40.31, 20.77, 0, false, true, true),
            ("축소 0.6 (k=2)", 0.6, 20.1, 15.3, 0, false, false, false),
            ("축소 0.3 (k=4)", 0.3, 100.2, 80.6, 0, false, false, false),
            ("축소 0.1537 (k=7)", 0.1537, 200.37, 150.81, 0, false, false, false),
            ("축소 0.42·회전 -20", 0.42, 120.2, 160.8, -20, false, false, false),
            ("경계에 딱 걸림: 맞춤 보기 2.42·이동 x.5", 2.42, 137.5, 20.0, 0, false, false, true),
            ("경계에 딱 걸림: 0.37·이동 x.5 (k=3)·반전", 0.37, 600.5, 20.7, 0, true, false, true),
            ("경계에 딱 걸림: 픽셀 아트 0.25", 0.25, 100.5, 50.5, 0, false, true, true),
        };
        foreach (var cs in cases)
        {
            var v = new ViewXf { Zoom = cs.zoom, PanX = cs.px, PanY = cs.py, Angle = cs.ang, Flip = cs.flip };
            gv.Render(v, d, cs.crisp);
            using var shot = gv.Snapshot();
            var got = Pixels(shot);
            int k = cs.zoom >= 1 || cs.crisp ? 1 : Math.Min(GpuView.MaxTaps, (int)Math.Ceiling(1 / cs.zoom));
            var want = CpuView(comp, d.W, d.H, d.Sel.Mask, v, bw, bh, k);
            var (mx, n) = Diff(want, got);
            double meanAbs = 0;
            for (int i = 0; i < got.Length; i++) for (int s = 0; s < 24; s += 8) meanAbs += Math.Abs((int)((got[i] >> s) & 255) - (int)((want[i] >> s) & 255));
            meanAbs /= got.Length * 3.0;
            double frac = (double)n / got.Length;
            bool ok = cs.exact ? n == 0 : frac < 0.003 && meanAbs < 0.5;
            Check(ok, $"[{label}] 화면 {cs.name} (k={k})", $"다른 픽셀 {n} ({frac * 100:0.000}%), 평균 차이 {meanAbs:0.000}, 최대 {mx}");
            if (!ok || cs.name.StartsWith("축소 0.1537")) { shot.Save(Path.Combine(outDir, $"view_{label}_{k}_{cases.ToList().IndexOf(cs)}.png")); using var w = ToBmp(want, bw, bh); w.Save(Path.Combine(outDir, $"view_{label}_{k}_{cases.ToList().IndexOf(cs)}_cpu.png")); }
        }
        // 겹침 그림: GDI+ 로 투명 바탕에 그려 얹은 것 ≈ CPU 처럼 바로 그린 것
        {
            var v = new ViewXf { Zoom = 0.5, PanX = 30, PanY = 20 };
            void DrawOv(Graphics gg)
            {
                gg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var p1 = new Pen(Color.FromArgb(160, 0, 0, 0)); using var p2 = new Pen(Color.FromArgb(200, 255, 255, 255));
                gg.DrawEllipse(p1, 200.5f, 150.5f, 120, 120); gg.DrawEllipse(p2, 199.5f, 149.5f, 122, 122);
                using var br = new SolidBrush(Color.FromArgb(90, 255, 120, 0)); gg.FillRectangle(br, 40, 300, 200, 80);
                using var p3 = new Pen(Color.FromArgb(255, 0, 160, 255), 2); gg.DrawLine(p3, 10, 10, 600, 420);
            }
            gv.ClearOverlay(new Rectangle(0, 0, bw, bh));
            using (var og = gv.OverlayGraphics()) DrawOv(og);
            gv.MarkOverlay(new Rectangle(0, 0, bw, bh));
            gv.Render(v, d, false);
            using var shot = gv.Snapshot();
            var got = Pixels(shot);
            var basePx = CpuView(comp, d.W, d.H, d.Sel.Mask, v, bw, bh, 2);
            using var refb = ToBmp(basePx, bw, bh);
            using (var rg = Graphics.FromImage(refb)) DrawOv(rg);
            var (mx, n) = Diff(Pixels(refb), got);
            Check(mx <= 3, $"[{label}] 겹침 그림(GDI+ 투명 비트맵 → GPU 얹기) = 바로 그린 것", $"최대 차이 {mx}, 다른 픽셀 {n}");
            gv.ClearOverlay(new Rectangle(0, 0, bw, bh));
        }
    }

    // ---- 그림판 GPU ↔ CPU -------------------------------------------------------------------
    static void CanvasSwitch()
    {
        string prev = Environment.GetEnvironmentVariable("COMICPAD_GPU");
        try
        {
            Environment.SetEnvironmentVariable("COMICPAD_GPU", "on");
            using var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(800, 600), ShowInTaskbar = false };
            var cv = new CanvasView { Dock = DockStyle.Fill };
            f.Controls.Add(cv);
            f.Show(); Application.DoEvents();
            var d = NewDoc(600, 420);
            Add(d, "A", s => Pattern(s, 81, 0.8));
            Add(d, "B", s => Pattern(s, 82, 0.5), "multiply");
            App.SetDoc(d);
            Application.DoEvents();
            Check(cv.GpuActive, "그림판: gpu=on → GPU 화면", GpuDevice.Status);
            using var gShot = cv.Snapshot();
            // DrawToBitmap (화면 찍기 길) 이 GPU 화면을 읽는지
            using var printed = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
            f.DrawToBitmap(printed, new Rectangle(0, 0, printed.Width, printed.Height));
            var (pm, pn) = Diff(Pixels(gShot), Pixels(printed));
            Check(pm == 0, "DrawToBitmap(UI 찍기) = GPU 화면 읽기", $"최대 차이 {pm}, 다른 픽셀 {pn}");
            Environment.SetEnvironmentVariable("COMICPAD_GPU", "off");
            cv.ApplyGpuSetting();
            Application.DoEvents();
            Check(!cv.GpuActive, "그림판: gpu=off → CPU(GDI) 화면으로 바뀜");
            using var cShot = cv.Snapshot();
            var (mx, n) = Diff(Pixels(gShot), Pixels(cShot));
            Check(mx == 0, "GPU 화면 = CPU 화면 (같은 문서·같은 보기)", $"최대 차이 {mx}, 다른 픽셀 {n}");
            gShot.Save(Path.Combine(outDir, "canvas_gpu.png")); cShot.Save(Path.Combine(outDir, "canvas_cpu.png"));
            Environment.SetEnvironmentVariable("COMICPAD_GPU", "on");
            cv.ApplyGpuSetting();
            Application.DoEvents();
            Check(cv.GpuActive, "그림판: 다시 gpu=on → GPU 화면");
            // 실제 화면(DWM 이 합친 것)에도 GPU 화면이 그대로 보이는지: 창을 맨 위로 올려 화면에서 찍어 비교
            f.TopMost = true; f.Activate(); cv.RedrawAll();
            for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25); }
            using (var screen = new Bitmap(cv.Width, cv.Height))
            {
                using (var sg = Graphics.FromImage(screen)) sg.CopyFromScreen(cv.PointToScreen(Point.Empty), Point.Empty, cv.Size);
                using var snap = cv.Snapshot();
                var (sm, sn) = Diff(Pixels(snap), Pixels(screen));
                Check(sn < snap.Width * snap.Height / 100, "실제 화면 찍기(스왑 체인 → DWM) = GPU 화면", $"최대 차이 {sm}, 다른 픽셀 {sn}");
                screen.Save(Path.Combine(outDir, "canvas_screen.png"));
            }
            f.TopMost = false;
            // 장치가 빠짐 → 다시 만들어 GPU 로 / 3번 넘게 빠지면 CPU 로
            cv.SimulateGpuFailure();
            for (int i = 0; i < 5; i++) Application.DoEvents();
            Check(cv.GpuActive, "장치 빠짐 한 번 → 새 장치로 GPU 화면 다시", GpuDevice.Status);
            cv.SimulateGpuFailure(); for (int i = 0; i < 5; i++) Application.DoEvents();
            cv.SimulateGpuFailure(); for (int i = 0; i < 5; i++) Application.DoEvents();
            Check(!cv.GpuActive, "장치가 자꾸 빠짐 → CPU 화면으로", GpuDevice.Status);
            using (var after = cv.Snapshot())
            {
                var (am, an) = Diff(Pixels(gShot), Pixels(after));
                Check(am == 0, "CPU 로 돌아간 뒤 화면 같음", $"최대 차이 {am}, 다른 픽셀 {an}");
            }
            Environment.SetEnvironmentVariable("COMICPAD_GPU", "auto"); cv.ApplyGpuSetting(); Application.DoEvents();   // 설정을 바꾸면 다시 시도
            Check(cv.GpuActive, "설정을 다시 바꾸면 GPU 다시 시도");
            f.ClientSize = new Size(640, 360); Application.DoEvents();
            using var rs = cv.Snapshot();
            Check(rs.Width == 640 && rs.Height == 360 && cv.GpuActive, "창 크기 바꾸기 → 스왑 체인 크기 맞춤", $"{rs.Width}×{rs.Height}");
            f.WindowState = FormWindowState.Minimized; Application.DoEvents();
            cv.RedrawAll(); Application.DoEvents();
            f.WindowState = FormWindowState.Normal; Application.DoEvents();
            Check(cv.GpuActive, "최소화했다가 되돌려도 GPU 화면 유지");
            f.Close();
        }
        finally { Environment.SetEnvironmentVariable("COMICPAD_GPU", prev); }
    }

    // ---- 시간 재기 ---------------------------------------------------------------------------
    static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    /// <summary>B5 600dpi 20 레이어 (타일의 약 35%에 그림, 모드·마스크·폴더·클리핑 섞음)</summary>
    static Document BenchDoc()
    {
        var d = new Document(4441, 6213);
        var rnd = new Random(7);
        var modes = new[] { "source-over", "source-over", "source-over", "multiply", "screen", "overlay", "source-over", "soft-light" };
        Layer folder = null;
        for (int i = 0; i < 20; i++)
        {
            var l = new Layer(d, "레이어 " + (i + 1)) { Blend = modes[i % modes.Length], Opacity = i % 5 == 4 ? 0.7f : 1f };
            if (i == 10) { folder = new Layer { Name = "폴더", Kind = "folder", Blend = "multiply", Opacity = 0.9f }; d.Layers.Add(folder); }
            if (i >= 10 && i < 14) l.Parent = folder.Id;
            if (i == 15 || i == 16) l.Clip = true;
            var s = l.Px;
            var pick = Enumerable.Range(0, s.TileCount).Where(_ => rnd.NextDouble() < 0.35).ToList();
            uint seed = (uint)i * 2654435761u;
            Parallel.ForEach(pick, ti =>
            {
                var t = s.Tile(ti % s.TX, ti / s.TX, true);
                for (int y = 0; y < 256; y++)
                    for (int x = 0; x < 256; x++)
                    {
                        uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)ti * 83492791u ^ seed;
                        int a = ((x + y + i * 9) & 63) < 40 ? 255 : (int)(h & 255);
                        if (((x / 16) + (y / 16)) % 4 == 0) a = 0;
                        t[y * 256 + x] = a == 0 ? 0 : Pm(a, (int)((h >> 8) & 255), (int)((h >> 16) & 255), (int)((x * 2 + i * 30) & 255));
                    }
            });
            if (i == 5) { l.Mask = new Surface(d.W, d.H); for (int ty = 0; ty < l.Mask.TY; ty += 2) for (int tx = 0; tx < l.Mask.TX; tx++) Array.Fill(l.Mask.Tile(tx, ty, true), Pm(255, 255, 255, 255)); }
            d.Layers.Add(l);
        }
        d.ActiveId = d.Layers[^1].Id;
        return d;
    }

    static void Bench()
    {
        Line("==== 시간 재기: B5 600dpi 4441×6213, 20 레이어 (+폴더·마스크·클리핑), 화면 1600×900");
        string prev = Environment.GetEnvironmentVariable("COMICPAD_GPU");
        long t0 = Stopwatch.GetTimestamp();
        var d = BenchDoc();
        Line($"문서 만들기 {Ms(t0):0}ms, 레이어 메모리 {d.Layers.Sum(l => (l.Px?.MemoryBytes ?? 0) + (l.Mask?.MemoryBytes ?? 0)) / 1_000_000}MB");
        var sb = new StringBuilder();
        try
        {
            bool quick = Environment.GetEnvironmentVariable("COMICPAD_GPU_BENCHQUICK") == "1";   // GPU 그림판만 빨리
            // 순수 합성 (화면 없이)
            if (!quick)
            {
                var o = new RenderOptions { Onion = true };
                t0 = Stopwatch.GetTimestamp();
                var cpu = new Compositor();
                for (int y = 0; y < d.H; y += 256) for (int x = 0; x < d.W; x += 256) cpu.Render(d, Rect.FromLTRB(x, y, Math.Min(d.W, x + 256), Math.Min(d.H, y + 256)), o);
                Line($"CPU 전체 합성 (그림판과 같은 256 조각, 한 코어): {Ms(t0):0}ms");
                var g = GpuDevice.Create("auto", out _) ?? GpuDevice.Create("warp", out _);
                if (g != null)
                {
                    using var gv = new GpuView(g, IntPtr.Zero, 64, 64);
                    t0 = Stopwatch.GetTimestamp();
                    gv.Composite(d, new Rect(0, 0, d.W, d.H), o); gv.Finish();
                    var c = gv.Compositor;
                    Line($"GPU 전체 합성 처음 (타일 올리기 포함, {c.Cache.Uploads}장 {c.Cache.UploadBytes / 1_000_000}MB, 조각 {c.Chunks}, 디스패치 {c.Dispatches}): {Ms(t0):0}ms");
                    for (int rep = 0; rep < 2; rep++)
                    {
                        long u0 = c.Cache.Uploads;
                        t0 = Stopwatch.GetTimestamp();
                        gv.Composite(d, new Rect(0, 0, d.W, d.H), o); gv.Finish();
                        Line($"GPU 전체 합성 다시 ({rep + 1}, 지문 확인 + 타일 다시 올림 {c.Cache.Uploads - u0}장, 칸 {c.Cache.Capacity}): {Ms(t0):0}ms");
                    }
                    // 정확도 확인 (한 부분)
                    var part = new Rect(1000, 2000, 900, 700);
                    var (mx, n) = Diff(new Compositor().Render(d, part, o), gv.ReadComp(part));
                    Check(mx <= 2, "큰 문서 GPU 합성 = CPU (한 부분)", $"최대 차이 {mx}, 다른 픽셀 {n}");
                    gv.Dispose(); g.Dispose();
                    GC.Collect(); GC.WaitForPendingFinalizers();
                }
            }
            // 그림판 그대로 (CanvasView): CPU ↔ GPU
            using var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(1600, 900), ShowInTaskbar = false };
            var cv = new CanvasView { Dock = DockStyle.Fill };
            f.Controls.Add(cv);
            var results = new Dictionary<string, Dictionary<string, string>>();
            foreach (var mode in quick ? new[] { "on" } : new[] { "off", "on" })
            {
                Environment.SetEnvironmentVariable("COMICPAD_GPU", mode);
                if (!f.Visible) { f.Show(); Application.DoEvents(); }
                cv.ApplyGpuSetting(); Application.DoEvents();
                GC.Collect(); GC.WaitForPendingFinalizers();   // 앞 방식의 합성 캐시를 치우고 재기
                string tag = cv.GpuActive ? "GPU" : "CPU";
                if (mode == "on" && !cv.GpuActive) { Line("GPU 그림판을 못 켬 — 건너뜀"); continue; }
                var res = results[tag] = new();
                void Finish() { if (cv.Gpu_ != null) cv.Gpu_.Finish(); }
                t0 = Stopwatch.GetTimestamp();
                App.SetDoc(d);   // 맞춤 보기 + 전체 합성
                Finish();
                res["문서 열기 (전체 합성+첫 화면)"] = $"{Ms(t0):0}ms";
                t0 = Stopwatch.GetTimestamp();
                App.InvalidateAll(); Finish();
                res["전체 다시 합성 (레이어 속성 바꿈 등)"] = $"{Ms(t0):0}ms";
                // 이동·확대 한 장
                foreach (var (vn, setup) in new (string, Action)[]
                {
                    ("맞춤 보기(축소 ~0.13)", () => cv.Fit()),
                    ("100%", () => { cv.SetZoom(1); }),
                    ("33% 회전 15°", () => { cv.SetZoom(0.33); cv.RotateBy(15, 800, 450); }),
                })
                {
                    setup(); Finish();
                    var times = new List<double>();
                    for (int i = 0; i < 24; i++)
                    {
                        t0 = Stopwatch.GetTimestamp();
                        cv.V.PanX += (i % 2 == 0 ? 9 : -7); cv.RedrawAll(); Finish();
                        times.Add(Ms(t0));
                    }
                    times.Sort();
                    res[$"이동 한 장 {vn}"] = $"평균 {times.Average():0.0}ms · 중간 {times[times.Count / 2]:0.0}ms";
                    cv.V.Angle = 0;
                }
                // 붓질: 실제 브러시 도구로 200 점 (점마다 그리기 → 바뀐 영역 합성 → 화면 올림)
                cv.SetZoom(0.5); Finish();
                App.SetTool("brush");
                var tool = App.Tool;
                if (tool != null && tool.Id == "brush")
                {
                    var times = new List<double>(); double sumComp = 0, sumFrame = 0, sumPresent = 0;
                    PointerArgs P(int i) { double tt = i / 200.0; double x = 1200 + 1600 * tt, y = 2500 + 400 * Math.Sin(tt * 9); var (sx, sy) = cv.V.ToScreen(x, y); return new PointerArgs { X = x, Y = y, SX = sx, SY = sy, Pressure = 0.6 + 0.3 * Math.Sin(tt * 5), Type = "pen", Buttons = 1, Time = i * 4 }; }
                    tool.Down(P(0)); cv.Present(); Finish();
                    for (int i = 1; i <= 200; i++)
                    {
                        t0 = Stopwatch.GetTimestamp();
                        cv.InputBatch = true;            // 포인터 메시지 처리와 같게: 중간 화면 올리기는 모으고
                        try { tool.Move(P(i)); } finally { cv.InputBatch = false; }
                        cv.Present(t0);                  // 끝에 한 번
                        Finish();
                        times.Add(Ms(t0));
                        sumComp += cv.CompositeMs; sumFrame += cv.FrameMs; sumPresent += cv.Gpu_?.LastPresentMs ?? 0;
                    }
                    tool.Up(P(200)); cv.Present();
                    times.Sort();
                    res["붓질 한 점 (입력→그리기→합성→화면 올림 끝)"] = $"평균 {times.Average():0.00}ms · 중간 {times[100]:0.00}ms · 95% {times[190]:0.00}ms";
                    if (cv.GpuActive) res["붓질 한 점 GPU 나눠 보기"] = $"합성 {sumComp / 200:0.00}ms · 그리기+올리기 {sumFrame / 200:0.00}ms (그중 Present {sumPresent / 200:0.00}ms)";
                    App.Invalidate(App.History.Undo(d));
                }
                else res["붓질"] = "브러시 도구 없음";
            }
            f.Close();
            foreach (var key in results.Values.SelectMany(r => r.Keys).Distinct())
                Line($"  {key}: " + string.Join("  |  ", results.Select(kv => $"{kv.Key} {(kv.Value.TryGetValue(key, out var v) ? v : "-")}")));
        }
        catch (Exception ex) { Check(false, "시간 재기 예외", ex.ToString()); }
        finally { Environment.SetEnvironmentVariable("COMICPAD_GPU", prev); }
    }
}
