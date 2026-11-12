// 이 모듈의 자동 시험 (창 없이). 실행:
//   set COMICPAD_FILTERS_SELFTEST=결과폴더   →   ComicPadNative.exe --render 아무.cpad 아무.png
// 결과폴더에 report.txt (통과/실패) 와 눈으로 확인할 그림(PNG)·PSD 가 생긴다. 실패 수가 종료 코드.
// 기대값은 웹 판 filters.js 의 SVG 필터 식을 손으로 계산한 값 (8비트 표 계산 때문에 ±2 허용).
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

public static class SelfTest
{
    static StringBuilder log; static int fails, passes;
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) passes++; else fails++;
        log.AppendLine($"{(ok ? "통과" : "실패")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
    }
    static void Near(string name, double got, double want, double tol = 2) => Check(name, Math.Abs(got - want) <= tol, $"값 {got:0.##}, 기대 {want:0.##} (±{tol})");
    static void NearRgb(string name, (int r, int g, int b) got, (double r, double g, double b) want, double tol = 2)
        => Check(name, Math.Abs(got.r - want.r) <= tol && Math.Abs(got.g - want.g) <= tol && Math.Abs(got.b - want.b) <= tol, $"값 ({got.r},{got.g},{got.b}), 기대 ({want.r:0.#},{want.g:0.#},{want.b:0.#})");

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        log = new StringBuilder(); fails = passes = 0;
        void Section(string s) => log.AppendLine("\n== " + s);
        try
        {
            Section("조정 (SVG 필터 식)"); Adjustments();
            Section("조정 레이어 합성"); AdjustLayers(outDir);
            Section("레이어 효과"); Effects(outDir);
            Section("톤화"); Tone(outDir);
            Section("용지"); PaperTest(outDir);
            Section("필터"); Filters();
            Section("PSD"); Psd(outDir);
            Section("ICC · 인쇄"); Print(outDir);
            Section("대화상자 (자동으로 열고 닫음)"); Dialogs(outDir);
            Section("메뉴"); Menus();
            if (Environment.GetEnvironmentVariable("COMICPAD_FILTERS_PERF") == "1") { Section("성능 (참고, B5 350dpi 크기)"); Perf(); }
        }
        catch (Exception ex) { fails++; log.AppendLine("예외: " + ex); }
        log.AppendLine($"\n통과 {passes} · 실패 {fails}");
        File.WriteAllText(Path.Combine(outDir, "report.txt"), log.ToString(), Encoding.UTF8);
        return fails;
    }

    static (int, int, int) A(string type, JsonObject p, int r, int g, int b) => Svg.Adjust(type, p).ApplyRgb(r, g, b);
    static double Lum(double r, double g, double b) => 0.299 * r + 0.587 * g + 0.114 * b;

    static void Adjustments()
    {
        NearRgb("반전", A("invert", null, 10, 128, 250), (245, 127, 5), 1);
        // 2값화: 밝기 >= t 면 흰색
        NearRgb("2값화 밝음", A("threshold", new JsonObject { ["t"] = 128 }, 200, 180, 160), (255, 255, 255), 0);
        NearRgb("2값화 어두움", A("threshold", new JsonObject { ["t"] = 128 }, 60, 80, 40), (0, 0, 0), 0);
        // 포스터화 4단계: discrete k = floor(i*4/255) → 255*k/3
        NearRgb("포스터화", A("posterize", new JsonObject { ["levels"] = 4 }, 100, 30, 250), (85, 0, 255), 1);
        // 흑백 (30,59,11)
        double bw = 0.3 * 200 + 0.59 * 100 + 0.11 * 50;
        NearRgb("흑백", A("bw", null, 200, 100, 50), (bw, bw, bw), 1);
        // 밝기/대비: b=50 → +0.25, c=50 → tan(67.5°) 배
        NearRgb("밝기 +50", A("bc", new JsonObject { ["b"] = 50, ["c"] = 0 }, 100, 0, 200), (163.75, 63.75, 255), 1.5);
        double k = Math.Tan(0.75 * Math.PI / 2);
        NearRgb("대비 +50", A("bc", new JsonObject { ["b"] = 0, ["c"] = 50 }, 100, 128, 160), ((100 / 255.0 - 0.5) * k * 255 + 127.5, (128 / 255.0 - 0.5) * k * 255 + 127.5, (160 / 255.0 - 0.5) * k * 255 + 127.5), 2);
        // HSL: 명도 +50 → v*0.5 + 0.5, 색조 180 → 빨강이 청록 쪽으로
        NearRgb("HSL 명도", A("hsl", new JsonObject { ["light"] = 50 }, 0, 100, 255), (127.5, 177.5, 255), 1.5);
        NearRgb("HSL 명도 −50", A("hsl", new JsonObject { ["light"] = -50 }, 0, 100, 255), (0, 50, 127.5), 1.5);
        NearRgb("HSL 색조 180", A("hsl", new JsonObject { ["hue"] = 180 }, 255, 0, 0), (0, 0.426 * 255, 0.426 * 255), 2);
        NearRgb("HSL 채도 −100", A("hsl", new JsonObject { ["sat"] = -100 }, 255, 0, 0), (0.213 * 255, 0.213 * 255, 0.213 * 255), 2);
        NearRgb("자연 채도 −100", A("vibrance", new JsonObject { ["v"] = -100 }, 40, 200, 90), (0.213 * 40 + 0.715 * 200 + 0.072 * 90, 0.213 * 40 + 0.715 * 200 + 0.072 * 90, 0.213 * 40 + 0.715 * 200 + 0.072 * 90), 2);
        // 단색 입히기: tint = 1.6 × 밝기 × 색, amount 만큼 섞음
        NearRgb("단색 입히기 흰색", A("colorize", null, 255, 255, 255), (255, 1.6 * 128 / 255 * 255, 1.6 * 64 / 255 * 255), 2);
        double l128 = Lum(128, 128, 128) / 255 * 1.6;
        NearRgb("단색 입히기 50%", A("colorize", new JsonObject { ["amount"] = 50 }, 128, 128, 128), (0.5 * 128 + 0.5 * l128 * 192, 0.5 * 128 + 0.5 * l128 * 128, 0.5 * 128 + 0.5 * l128 * 64), 2);
        // 그라데이션 맵: 검정→흰색 이면 밝기, 기본값 0 이면 첫 색
        var gm = new JsonObject { ["stops"] = new JsonArray(AdjustTypes.Stop(0, "#000000"), AdjustTypes.Stop(1, "#ffffff")) };
        double lm = Lum(200, 50, 120);
        NearRgb("그라데이션 맵 흑백", A("gradmap", gm, 200, 50, 120), (lm, lm, lm), 3);
        NearRgb("그라데이션 맵 기본 (검정)", A("gradmap", null, 0, 0, 0), (0x1b, 0x14, 0x30), 1);
        NearRgb("그라데이션 맵 기본 (흰색)", A("gradmap", null, 255, 255, 255), (0xff, 0xe9, 0xc4), 1);
        // 레벨: 입력 50~200 → (v−50)/150
        NearRgb("레벨", A("levels", new JsonObject { ["inB"] = 50, ["inW"] = 200 }, 125, 50, 200), (127.5, 0, 255), 2);
        double gmV = Math.Pow(0.5, 1 / 2.0) * 255;
        NearRgb("레벨 감마 2", A("levels", new JsonObject { ["gamma"] = 2 }, 128, 128, 128), (gmV, gmV, gmV), 2.5);
        // 곡선: 점 (128,200) 을 지남
        var cv = AdjustTypes.Default("curves"); cv["master"] = new JsonArray(new JsonArray(0, 0), new JsonArray(128, 200), new JsonArray(255, 255));
        NearRgb("곡선 점 통과", A("curves", cv, 128, 0, 255), (200, 0, 255), 2);
        var cvr = AdjustTypes.Default("curves"); cvr["r"] = new JsonArray(new JsonArray(0, 255), new JsonArray(255, 0));
        NearRgb("곡선 빨강 채널 반전", A("curves", cvr, 30, 30, 30), (225, 30, 30), 1.5);
        // 단조 3차 보간: 값이 점 사이에서 넘치지 않음
        var f = Svg.CurveFn(new JsonArray(new JsonArray(0, 0), new JsonArray(64, 48), new JsonArray(192, 208), new JsonArray(255, 255)));
        bool mono = true; double prev = -1; for (int i = 0; i <= 100; i++) { double v = f(i / 100.0); if (v < prev - 1e-9) mono = false; prev = v; }
        Check("곡선 단조", mono);
        // 색상 균형: 어두운 부분 빨강 +100 → v + 0.4(1−v)²
        NearRgb("색상 균형", A("balance", new JsonObject { ["sr"] = 100 }, 0, 0, 0), (0.4 * 255, 0, 0), 1.5);
        // 반투명 픽셀: 미리 곱하지 않은 색에 적용
        var buf = new[] { Px.Premul(Px.Pack(128, 200, 100, 0)) };
        Svg.Adjust("invert", null).Apply(buf);
        var u = Px.Unpremul(buf[0]);
        Check("반투명 반전 (알파 유지)", (u >> 24) == 128 && Math.Abs((int)((u >> 16) & 255) - 55) <= 2 && Math.Abs((int)((u >> 8) & 255) - 155) <= 2 && Math.Abs((int)(u & 255) - 255) <= 2, $"{u:x8}");
        // 기본값이 없는 값은 기본값으로 (웹 {...def, ...p})
        Check("기본값 합치기", Svg.Num(AdjustTypes.Merge("levels", new JsonObject { ["inB"] = 10 }), "inW") == 255);
    }

    static Document Doc(int w, int h) { var d = new Document(w, h); return d; }
    static Layer Fill(Document d, string name, Func<int, int, uint> px)
    {
        var l = new Layer(d, name);
        var r = new Rect(0, 0, d.W, d.H); var buf = new uint[d.W * d.H];
        for (int y = 0; y < d.H; y++) for (int x = 0; x < d.W; x++) buf[y * d.W + x] = px(x, y);
        l.Px.Write(r, buf);
        d.Layers.Add(l); d.ActiveId = l.Id;
        return l;
    }
    static (int r, int g, int b, int a) Get(uint c) { Raster.Unpack(c, out int r, out int g, out int b, out int a); return (r, g, b, a); }
    static void SavePng(Surface s, string path) { using var bmp = CpadIO.ToBitmap(s, s.Full); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); }

    static void AdjustLayers(string outDir)
    {
        // 바탕: 가로 그라데이션 · 그 위 조정 레이어
        var d = Doc(96, 64);
        Fill(d, "바탕", (x, y) => Px.Pack(255, (uint)(x * 255 / 95), 128, (uint)(255 - x * 255 / 95)));
        var adj = new Layer(d, "반전", "adjust") { Meta = new JsonObject { ["type"] = "invert", ["params"] = new JsonObject() }, Opacity = 0.5f };
        d.Layers.Add(adj);
        var px = App.Comp.Sample(d, 48, 32, new RenderOptions { Paper = false });
        var (r, g, b, _) = Get(px);
        uint src = Px.Pack(255, 48 * 255 / 95, 128, 255 - 48 * 255 / 95);
        Near("조정 레이어 불투명도 50% (빨강)", r, (((src >> 16) & 255) + (255 - ((src >> 16) & 255))) / 2.0, 2);
        // 마스크: 왼쪽 절반만
        adj.Opacity = 1;
        adj.Mask = new Surface(d.W, d.H);
        var m = new uint[d.W * d.H]; for (int y = 0; y < d.H; y++) for (int x = 0; x < 48; x++) m[y * d.W + x] = 0xFFFFFFFF;
        adj.Mask.Write(new Rect(0, 0, d.W, d.H), m);
        var left = Get(App.Comp.Sample(d, 10, 10, new RenderOptions { Paper = false }));
        var right = Get(App.Comp.Sample(d, 80, 10, new RenderOptions { Paper = false }));
        Near("마스크 안 (반전됨)", left.g, 127, 1.5);
        Near("마스크 밖 (그대로)", right.r, 80 * 255 / 95, 1);
        // 클리핑: 위 작은 레이어에만
        adj.Mask = null;
        var d2 = Doc(32, 32);
        Fill(d2, "흰 바탕", (x, y) => 0xFFFFFFFF);
        Fill(d2, "작은 네모", (x, y) => x < 16 ? 0xFF204060 : 0);
        d2.Layers.Add(new Layer(d2, "반전", "adjust") { Clip = true, Meta = new JsonObject { ["type"] = "invert", ["params"] = new JsonObject() } });
        var inBox = Get(App.Comp.Sample(d2, 4, 4, new RenderOptions { Paper = false }));
        var outBox = Get(App.Comp.Sample(d2, 24, 4, new RenderOptions { Paper = false }));
        NearRgb("클리핑 조정: 기준 레이어만", (inBox.r, inBox.g, inBox.b), (255 - 0x20, 255 - 0x40, 255 - 0x60), 1);
        NearRgb("클리핑 조정: 바깥은 그대로 (흰색)", (outBox.r, outBox.g, outBox.b), (255, 255, 255), 0);
        // 같은 단계 조정 + 용지: 용지도 보정됨 (웹 _paperInLevel)
        var d3 = Doc(16, 16); d3.PaperColor = "#ffffff";
        d3.Layers.Add(new Layer(d3, "반전", "adjust") { Meta = new JsonObject { ["type"] = "invert", ["params"] = new JsonObject() } });
        var pp = Get(App.Comp.Sample(d3, 3, 3));
        NearRgb("용지까지 조정", (pp.r, pp.g, pp.b), (0, 0, 0), 0);

        // 눈으로 볼 그림: 12가지 조정을 한 장에
        var types = AdjustTypes.List.Select(t => t.id).ToArray();
        var sheet = new Surface(96 * 4, 64 * 3);
        for (int i = 0; i < types.Length; i++)
        {
            var dd = Doc(96, 64);
            Fill(dd, "색", (x, y) => Px.Pack(255, (uint)(x * 255 / 95), (uint)(y * 255 / 63), (uint)((x + y) * 255 / 158)));
            dd.Layers.Add(new Layer(dd, types[i], "adjust") { Meta = new JsonObject { ["type"] = types[i], ["params"] = AdjustTypes.Default(types[i]) } });
            var flat = App.Comp.Flatten(dd, new RenderOptions { Paper = false });
            sheet.Write(new Rect((i % 4) * 96, (i / 4) * 64, 96, 64), flat.Read(flat.Full));
        }
        SavePng(sheet, Path.Combine(outDir, "adjust_types.png"));
    }

    static void Effects(string outDir)
    {
        var d = Doc(120, 120);
        var l = Fill(d, "네모", (x, y) => x >= 40 && x < 80 && y >= 40 && y < 80 ? 0xFF3060C0 : 0);
        l.Fx = new JsonObject { ["stroke"] = new JsonObject { ["on"] = true, ["size"] = 6, ["color"] = "#ff0000" } };
        var o = new RenderOptions { Paper = false };
        Check("효과 있음 (HasFx)", Hooks.HasFx(l));
        var s1 = Get(App.Comp.Sample(d, 36, 60, o));
        var s2 = Get(App.Comp.Sample(d, 30, 60, o));
        var s3 = Get(App.Comp.Sample(d, 60, 60, o));
        Check("테두리 안쪽 (4px 바깥)", s1.r == 255 && s1.a == 255 && s1.b == 0, $"{s1}");
        Check("테두리 바깥 (10px)", s2.a == 0, $"{s2}");
        Check("그림은 위에", s3.b == 0xC0, $"{s3}");
        // 타일 경계 너머에서도 (영역 렌더 = 전체 렌더)
        var part = App.Comp.Render(d, new Rect(30, 30, 12, 12), o);
        var full = App.Comp.Flatten(d, o);
        bool same = true; for (int y = 0; y < 12; y++) for (int x = 0; x < 12; x++) if (part[y * 12 + x] != full.Get(30 + x, 30 + y)) same = false;
        Check("부분 렌더 = 전체 렌더 (여백 계산)", same);
        // 그림자: 흐림 0, (10,10) 이동, 진하기 0.5
        l.Fx = new JsonObject { ["shadow"] = new JsonObject { ["on"] = true, ["dx"] = 10, ["dy"] = 10, ["blur"] = 0, ["color"] = "#000000", ["opacity"] = 0.5 } };
        var sh = Get(App.Comp.Sample(d, 85, 85, o));
        Check("그림자 위치·진하기", Math.Abs(sh.a - 128) <= 2 && sh.r == 0, $"{sh}");
        Check("그림자 없는 곳", Get(App.Comp.Sample(d, 35, 35, o)).a == 0);
        // 외부 광선: 가장자리 가까울수록 진함
        l.Fx = new JsonObject { ["glow"] = new JsonObject { ["on"] = true, ["size"] = 16, ["color"] = "#ffe680", ["opacity"] = 0.8 } };
        int g1 = Get(App.Comp.Sample(d, 38, 60, o)).a, g2 = Get(App.Comp.Sample(d, 26, 60, o)).a, g3 = Get(App.Comp.Sample(d, 5, 60, o)).a;
        Check("광선 감쇠", g1 > g2 && g2 > g3, $"{g1} > {g2} > {g3}");
        Check("광선 최대 ≤ 진하기", g1 <= 205, $"{g1}");
        // 세 효과 함께 → 그림
        l.Fx = new JsonObject
        {
            ["stroke"] = new JsonObject { ["on"] = true, ["size"] = 4, ["color"] = "#ffffff" },
            ["shadow"] = new JsonObject { ["on"] = true, ["dx"] = 8, ["dy"] = 8, ["blur"] = 10, ["color"] = "#000000", ["opacity"] = 0.5 },
            ["glow"] = new JsonObject { ["on"] = true, ["size"] = 16, ["color"] = "#ffe680", ["opacity"] = 0.8 },
        };
        d.PaperColor = "#9aa0b0";
        SavePng(App.Comp.Flatten(d), Path.Combine(outDir, "fx.png"));
        // 폴더에 효과
        var d2 = Doc(60, 60);
        var folder = new Layer(d2, "폴더", "folder") { Fx = new JsonObject { ["stroke"] = new JsonObject { ["on"] = true, ["size"] = 3, ["color"] = "#00ff00" } } };
        var kid = new Layer(d2, "안") { Parent = folder.Id };
        var kb = new uint[60 * 60]; for (int y = 20; y < 40; y++) for (int x = 20; x < 40; x++) kb[y * 60 + x] = 0xFF000000;
        kid.Px.Write(new Rect(0, 0, 60, 60), kb);
        d2.Layers.Add(kid); d2.Layers.Add(folder);
        var fs = Get(App.Comp.Sample(d2, 18, 30, o));
        Check("폴더 효과 테두리", fs.g == 255 && fs.a == 255, $"{fs}");
    }

    static void Tone(string outDir)
    {
        var d = Doc(256, 128);
        var l = Fill(d, "회색", (x, y) => x < 128 ? Px.Pack(255, 128, 128, 128) : Px.Pack(255, 64, 64, 64));
        l.Tone = new JsonObject { ["spacing"] = 8, ["angle"] = 45, ["shape"] = "dot", ["color"] = "#000000" };
        var flat = App.Comp.Flatten(d, new RenderOptions { Paper = false });
        int on = 0, n = 0; bool binary = true;
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) { uint c = flat.Get(x, y); n++; if (c >> 24 == 255) on++; else if (c != 0) binary = false; }
        double dark = 1 - 128 / 255.0;
        Near("망점 덮인 비율 ≈ 어두운 정도", on / (double)n, dark, 0.06);
        Check("망점은 0/1", binary);
        // 바뀐 타일만 다시 계산: 그림을 바꾸고 Invalidate 하면 반영
        var buf = new uint[16 * 16]; Array.Fill(buf, 0xFF000000u);
        l.Px.Write(new Rect(10, 10, 16, 16), buf);
        App.Invalidate(new Rect(10, 10, 16, 16));
        var after = App.Comp.Sample(d, 15, 15, new RenderOptions { Paper = false });
        Check("그리는 중 망점 갱신", after == 0xFF000000, $"{after:x8}");
        l.Tone["shape"] = "line";
        d.PaperColor = "#ffffff";
        SavePng(App.Comp.Flatten(d), Path.Combine(outDir, "tone.png"));
    }

    static void PaperTest(string outDir)
    {
        var d = Doc(600, 200); d.PaperColor = "#f0e6d2";
        var flat = App.Comp.Flatten(d);
        Check("질감 없음 = 단색", flat.Get(10, 10) == Px.FromHex("#f0e6d2") && flat.Get(500, 150) == Px.FromHex("#f0e6d2"));
        var sheet = new Surface(600, 200 * 4);
        int i = 0;
        foreach (var t in new[] { "fine", "rough", "canvas", "kraft" })
        {
            d.PaperExtra["texture"] = new JsonObject { ["type"] = t, ["strength"] = 1, ["scale"] = 1 };
            var f = App.Comp.Flatten(d);
            var tile = Paper.Tile(t);
            Check($"질감 {t} 타일", tile != null && tile.Length == 512 * 512);
            uint mn = 255, mx = 0; bool under = true;
            for (int y = 0; y < 200; y += 3) for (int x = 0; x < 600; x += 3) { uint c = f.Get(x, y); uint g = (c >> 8) & 255; mn = Math.Min(mn, g); mx = Math.Max(mx, g); if (g > 0xe6) under = false; }
            Check($"질감 {t}: 곱하기 (용지보다 밝지 않음, 결 있음)", under && mx > mn, $"{mn}~{mx}");
            sheet.Write(new Rect(0, i * 200, 600, 200), f.Read(f.Full));
            i++;
        }
        // 그림 위에도 (over)
        d.PaperExtra["texture"] = new JsonObject { ["type"] = "rough", ["strength"] = 1, ["scale"] = 1, ["over"] = true };
        Fill(d, "검정 아님", (x, y) => 0xFF8080FF);
        var ov = App.Comp.Flatten(d);
        bool varied = false; uint first = ov.Get(0, 0); for (int x = 0; x < 600 && !varied; x += 7) if (ov.Get(x, 50) != first) varied = true;
        Check("그림 위에도 결", varied);
        SavePng(sheet, Path.Combine(outDir, "paper.png"));
    }

    static void Filters()
    {
        var b = new byte[] { 255, 255, 255, 255, 0, 0, 0, 255, 128, 128, 128, 255 };
        FilterOps.LineExtract(b);
        Check("선화 추출: 흰색 → 투명", b[3] == 0);
        Check("선화 추출: 검정 → 불투명 검정", b[7] == 255 && b[4] == 0);
        Near("선화 추출: 회색 → 알파×1.15", b[11], Math.Min(255, (255 - 128) * 1.15), 1);
        var g = new byte[] { 255, 0, 0, 255 }; FilterOps.Gray(g); Near("흑백", g[0], 0.299 * 255, 0.6);
        var mono = new byte[] { 50, 50, 50, 200, 200, 200, 200, 255, 50, 50, 50, 50 }; FilterOps.Mono(mono, 128);
        Check("2값화", mono[3] == 255 && mono[7] == 0 && mono[11] == 0);
        // 흐림: 알파 합 보존
        int w = 64, h = 64; var src = new uint[w * h]; for (int y = 28; y < 36; y++) for (int x = 28; x < 36; x++) src[y * w + x] = 0xFF000000;
        var bl = Raster.BlurRgba(src, w, h, 4);
        double s0 = src.Sum(c => (double)(c >> 24)), s1 = bl.Sum(c => (double)(c >> 24));
        Near("흐림: 알파 합 보존", s1, s0, s0 * 0.02);
        var bl1 = Raster.BlurRgba(src, w, h, 1.2);
        Near("작은 반경 흐림: 알파 합 보존", bl1.Sum(c => (double)(c >> 24)), s0, s0 * 0.02);
        // 모자이크: 칸 안은 한 색
        var grad = new uint[w * h]; for (int i = 0; i < grad.Length; i++) grad[i] = Px.Pack(255, (uint)(i % w * 4), 0, 0);
        var mo = FilterOps.Mosaic(grad, w, h, 8);
        Check("모자이크 칸 한 색", mo[0] == mo[7] && mo[0] == mo[7 * w + 7] && mo[0] != mo[8]);
        // 언샤프: 평평한 곳은 그대로
        var flatc = Enumerable.Repeat(0xFF808080u, w * h).ToArray();
        var us = FilterOps.Unsharp(flatc, w, h, 2, 0.8);
        Check("언샤프: 평평한 곳 그대로", us[32 * w + 32] == 0xFF808080u, $"{us[32 * w + 32]:x8}");
        // 외곽선: 바깥에 색
        var ol = FilterOps.Outline(src, w, h, 4, "#ff0000", new Selection(w, h));
        var oc = Get(ol[32 * w + 25]);
        Check("외곽선", oc.r == 255 && oc.a > 200, $"{oc}");
        Check("외곽선: 원래 그림 그대로 위에", ol[32 * w + 32] == 0xFF000000);
        // 그림자 넣기
        var ds = FilterOps.DropShadow(src, w, h, 10, 0, 0, "#000000", 0.5);
        Near("그림자 넣기", ds[32 * w + 44] >> 24, 128, 1);
        // 방향 흐림: 가로로 번짐
        var mb = FilterOps.Motion(src, w, h, 0, 16);
        Check("방향 흐림: 가로로 번짐, 세로는 그대로", (mb[32 * w + 40] >> 24) > 0 && (mb[20 * w + 32] >> 24) == 0);
        // 선 정리: 작은 잡티 지움
        var img = new byte[32 * 32 * 4];
        void Ink(int x, int y) { int p = (y * 32 + x) * 4; img[p + 3] = 255; }
        Ink(3, 3);                                        // 잡티 1px
        for (int x = 5; x < 30; x++) { Ink(x, 16); Ink(x, 17); }   // 선
        int removed = FilterOps.CleanLines(img, 32, 32, new FilterOps.CleanOpt { Specks = 5 });
        Check("선 정리: 잡티 지움, 선은 남음", img[(3 * 32 + 3) * 4 + 3] == 0 && img[(16 * 32 + 10) * 4 + 3] == 255 && removed == 1, $"지운 {removed}");
        // 끊긴 선 잇기
        var img2 = new byte[32 * 32 * 4];
        for (int x = 2; x < 30; x++) if (x != 15 && x != 16) { img2[(10 * 32 + x) * 4 + 3] = 255; img2[(11 * 32 + x) * 4 + 3] = 255; }
        FilterOps.CleanLines(img2, 32, 32, new FilterOps.CleanOpt { Close = 2 });
        Check("선 정리: 끊긴 곳 잇기", img2[(10 * 32 + 15) * 4 + 3] == 255);
        // 노이즈: 모양 밖은 투명 그대로
        var nz = FilterOps.Noise(src, w, h, 50, true, 3);
        Check("노이즈: 모양 밖 투명", nz[0] == 0 && (nz[32 * w + 32] >> 24) > 0);
        // 실행취소: 필터 → Undo 로 원래대로
        var d = Doc(40, 40); App.Doc = d;
        var l = Fill(d, "x", (x, y) => Px.Pack(255, (uint)(x * 6), 10, 20));
        var before = l.Px.Read(l.Px.Full);
        FilterOps.PixelFilter("색 반전", FilterOps.Invert);
        var mid = l.Px.Get(5, 5);
        App.History.Undo(d);
        var back = l.Px.Read(l.Px.Full);
        Check("필터 실행취소", mid != before[5 * 40 + 5] && back.AsSpan().SequenceEqual(before));
        // 선택 영역 안만
        d.Sel.Mask = new byte[40 * 40]; for (int y = 0; y < 40; y++) for (int x = 0; x < 20; x++) d.Sel.Mask[y * 40 + x] = 255;
        FilterOps.CanvasOp("반전", (s, ww, hh) => { var o = (uint[])s.Clone(); Svg.Adjust("invert", null).Apply(o); return o; });
        Check("선택 영역 안만", l.Px.Get(5, 5) != before[5 * 40 + 5] && l.Px.Get(30, 5) == before[5 * 40 + 30]);
        App.History.Clear(); App.Doc = null;
    }

    static void Psd(string outDir)
    {
        // 레이어 여러 개·폴더·마스크·글자·컷·조정 레이어가 있는 작품
        var d = Doc(200, 150); d.Name = "PSD 시험"; d.PaperColor = "#fffaf0";
        var bg = Fill(d, "바탕", (x, y) => 0xFFFFFFFF);
        var a = Fill(d, "반투명 빨강", (x, y) => x > 20 && x < 120 && y > 20 && y < 100 ? Px.Premul(Px.Pack(160, 230, 30, 40)) : 0);
        a.Blend = "multiply"; a.Opacity = 0.7f; a.AlphaLock = true;
        var folder = new Layer(d, "폴더 가", "folder") { Expanded = false, Blend = "screen", Opacity = 0.8f };
        var inF = new Layer(d, "안쪽 레이어") { Parent = folder.Id };
        var ib = new uint[200 * 150]; for (int y = 50; y < 140; y++) for (int x = 90; x < 190; x++) ib[y * 200 + x] = Px.Pack(255, 20, (uint)(x + 40), 200);
        inF.Px.Write(new Rect(0, 0, 200, 150), ib);
        inF.Mask = new Surface(200, 150);
        var mb = new uint[200 * 150]; for (int y = 0; y < 150; y++) for (int x = 0; x < 200; x++) { uint v = (uint)(x * 255 / 199); mb[y * 200 + x] = Px.Pack(v, v, v, v); }
        inF.Mask.Write(new Rect(0, 0, 200, 150), mb); inF.MaskOn = false;
        var clip = new Layer(d, "클리핑") { Parent = folder.Id, Clip = true, Visible = false };
        var cb = new uint[200 * 150]; for (int y = 60; y < 70; y++) for (int x = 0; x < 200; x++) cb[y * 200 + x] = 0xFF00FF00;
        clip.Px.Write(new Rect(0, 0, 200, 150), cb);
        d.Layers.Add(inF); d.Layers.Add(clip); d.Layers.Add(folder);
        var txt = Fill(d, "대사", (x, y) => x >= 30 && x < 60 && y >= 110 && y < 130 ? 0xFF000000 : 0);
        txt.Kind = "text"; txt.Meta = new JsonObject { ["text"] = "안녕 (세상)\n둘째 줄", ["x"] = 30, ["y"] = 110, ["size"] = 20, ["color"] = "#123456", ["font"] = "Malgun Gothic", ["align"] = "center" };
        var panel = Fill(d, "컷", (x, y) => x < 5 || y < 5 || x >= 195 || y >= 145 ? 0xFF000000 : 0);
        panel.Kind = "panel"; panel.Meta = new JsonObject { ["panels"] = new JsonArray(new JsonObject { ["x"] = 10, ["y"] = 10, ["w"] = 180, ["h"] = 130 }), ["line"] = 3, ["color"] = "#000000" };
        var bub = Fill(d, "말풍선", (x, y) => 0);
        bub.Kind = "bubble"; bub.Meta = new JsonObject { ["bubbles"] = new JsonArray(new JsonObject { ["x"] = 120, ["y"] = 10, ["w"] = 60, ["h"] = 40, ["fill"] = "#ffffff", ["stroke"] = "#000000", ["line"] = 2, ["tail"] = new JsonObject { ["x"] = 110, ["y"] = 70 } }) };
        d.Layers.Add(new Layer(d, "조정: 반전", "adjust") { Meta = new JsonObject { ["type"] = "invert", ["params"] = new JsonObject() }, Visible = false });

        var flat = App.Comp.Flatten(d, new RenderOptions { Paper = true });
        var bytes = PsdWriter.Write(d, flat);
        File.WriteAllBytes(Path.Combine(outDir, "roundtrip.psd"), bytes);
        Check("PSD 머리", Encoding.ASCII.GetString(bytes, 0, 4) == "8BPS");
        var back = PsdReader.Read(bytes, "되읽기", out var notes);
        Check("크기", back.W == 200 && back.H == 150);
        string Names(Document x) => string.Join("|", x.Layers.Select(l => l.Name));
        Check("레이어 이름·순서 (한글)", Names(back) == Names(d), $"{Names(back)}");
        foreach (var l0 in d.Layers)
        {
            var l1 = back.Layers.FirstOrDefault(x => x.Name == l0.Name);
            if (l1 == null) { Check("레이어 " + l0.Name, false, "없음"); continue; }
            bool props = l1.Blend == l0.Blend && Math.Abs(l1.Opacity - l0.Opacity) <= 1 / 255f + 1e-6 && l1.Visible == l0.Visible && (l0.IsFolder || l1.Clip == l0.Clip) && (l0.IsFolder || l1.AlphaLock == l0.AlphaLock);
            Check($"속성 {l0.Name}", props, $"{l1.Blend} {l1.Opacity} {l1.Visible} {l1.Clip} {l1.AlphaLock}");
            string p0 = l0.Parent == null ? null : d.ById(l0.Parent)?.Name, p1 = l1.Parent == null ? null : back.ById(l1.Parent)?.Name;
            Check($"폴더 구조 {l0.Name}", p0 == p1, $"{p1}");
            if (l0.IsFolder) { Check($"폴더 열림 {l0.Name}", l1.IsFolder && l1.Expanded == l0.Expanded); continue; }
            if (l0.Kind == "bubble")
            {
                // 그림이 빈 셰이프 레이어 → 벡터 마스크·선으로 다시 그림 (웹 drawFillLayer)
                var cin = Get(l1.Px.Get(150, 30)); var ring = Get(l1.Px.Get(119, 30)); var outside = Get(l1.Px.Get(100, 120));
                Check("빈 셰이프 레이어 → 경로로 다시 그림", cin.r == 255 && cin.a == 255 && ring.a > 200 && ring.r < 60 && outside.a == 0, $"{cin} {ring} {outside}");
                continue;
            }
            if (l0.Px != null && l1.Px != null)
            {
                var x0 = l0.Px.Read(l0.Px.Full); var x1 = l1.Px.Read(l1.Px.Full);
                int bad = 0; for (int i = 0; i < x0.Length; i++) { var u0 = Get(x0[i]); var u1 = Get(x1[i]); if (Math.Abs(u0.a - u1.a) > 0 || (u0.a > 0 && (Math.Abs(u0.r - u1.r) > 1 || Math.Abs(u0.g - u1.g) > 1 || Math.Abs(u0.b - u1.b) > 1))) bad++; }
                Check($"픽셀 {l0.Name}", bad == 0, $"다른 픽셀 {bad}");
            }
            if (l0.Mask != null)
            {
                var m0 = l0.Mask.Read(l0.Mask.Full); var m1 = l1.Mask?.Read(l1.Mask.Full);
                Check($"마스크 {l0.Name}", m1 != null && m0.Select(v => v >> 24).SequenceEqual(m1.Select(v => v >> 24)) && l1.MaskOn == l0.MaskOn);
            }
        }
        var bt = back.Layers.First(x => x.Name == "대사");
        var pt = bt.Meta?["psdText"] as JsonObject;
        Check("글자 레이어 → TySh → psdText", pt != null && (string)pt["text"] == "안녕 (세상)\n둘째 줄" && Math.Abs(Svg.Num(pt, "size") - 20) < 1e-6 && (string)pt["color"] == "#123456" && (string)pt["align"] == "center" && (string)pt["font"] == "MalgunGothic",
            pt?.ToJsonString() ?? "없음");
        Check("조정 레이어는 빈 레이어로 (웹과 같음)", back.Layers.First(x => x.Name == "조정: 반전").Px?.Bounds().IsEmpty == true);
        // 다시 써서 같은 바이트 (결정적)
        var flat2 = App.Comp.Flatten(d, new RenderOptions { Paper = true });
        Check("같은 입력 → 같은 PSD", PsdWriter.Write(d, flat2).AsSpan().SequenceEqual(bytes));
        // 셰이프 블록
        string ascii = Encoding.Latin1.GetString(bytes);
        Check("컷/말풍선 → 셰이프 (vmsk·SoCo·vstk)", ascii.Contains("8BIMvmsk") && ascii.Contains("8BIMSoCo") && ascii.Contains("8BIMvstk"));
        // 벡터 경로 쓰기 → 읽기
        var loops = new List<Loop> { new() { Pts = new double[] { 10, 20, 110, 20, 110, 80, 10, 80 } }, new() { Pts = new double[] { 30, 30, 50, 30, 50, 50 }, Hole = true } };
        var rec = PsdVector.ReadVectorPaths(new byte[8].Concat(PsdVector.PathRecords(loops, 200, 150)).ToArray(), 200, 150);
        Check("벡터 경로 되읽기", rec.Count == 2 && rec[0].Knots.Count == 4 && Math.Abs(rec[0].Knots[1].Anchor.x - 110) < 0.01 && Math.Abs(rec[0].Knots[2].Anchor.y - 80) < 0.01 && rec[1].Op == 2);
        // 윤곽선 따기: 네모 하나 → 고리 1개, 넓이 ≈ 네모
        var V = new byte[40 * 40]; for (int y = 10; y < 30; y++) for (int x = 5; x < 25; x++) V[y * 40 + x] = 255;
        var tl = PsdVector.ShapeLoops(V, 40, 40, 0, 0);
        Check("윤곽선: 네모 → 고리 1개", tl.Count == 1 && Math.Abs(Math.Abs(tl[0].Area) - 400) < 25, $"{tl.Count} {tl.FirstOrDefault()?.Area}");
        // 레이어 없는 PSD → 합성 이미지로
        var empty = Doc(20, 10);
        var ef = new Surface(20, 10); var eb = new uint[200]; for (int i = 0; i < 200; i++) eb[i] = Px.Pack(255, (uint)(i % 20 * 10), 50, 100); ef.Write(ef.Full, eb);
        var ebytes = PsdWriter.Write(empty, ef);
        var eback = PsdReader.Read(ebytes, "합성", out _);
        Check("레이어 없는 PSD → 배경 레이어", eback.Layers.Count == 1 && eback.Layers[0].Px.Read(eback.Layers[0].Px.Full).SequenceEqual(eb));
        // 손으로 만든 PSD: 16비트 흑백 무압축, 8비트 CMYK 무압축 (레이어 없이 합성 이미지만)
        byte[] Craft(int nch, int w2, int h2, int depth, int mode, byte[] body)
        {
            var be = new Be(); be.Ascii("8BPS"); be.U16(1); be.Bytes(new byte[6]); be.U16(nch); be.U32(h2); be.U32(w2); be.U16(depth); be.U16(mode);
            be.U32(0); be.U32(0); be.U32(0); be.U16(0); be.Bytes(body); return be.ToArray();
        }
        var g16 = Craft(1, 3, 1, 16, 1, new byte[] { 0x00, 0x10, 0x80, 0xff, 0xff, 0x00 });
        var gd = PsdReader.Read(g16, "g16", out _).Layers[0].Px;
        Check("16비트 흑백 PSD", Get(gd.Get(0, 0)).r == 0 && Get(gd.Get(1, 0)).r == 0x80 && Get(gd.Get(2, 0)).r == 0xff);
        var cmyk = Craft(4, 2, 1, 8, 4, new byte[] { 0, 255, /*C*/ 255, 255, /*M*/ 255, 255, /*Y*/ 255, 128 /*K*/ });
        var cd = PsdReader.Read(cmyk, "cmyk", out _).Layers[0].Px;
        var c0 = Get(cd.Get(0, 0)); var c1 = Get(cd.Get(1, 0));
        Check("CMYK PSD (0 = 잉크 가득)", c0.r == 0 && c0.g == 255 && c0.b == 255 && c1.r == 128 && c1.g == 128, $"{c0} {c1}");
        // ZIP 압축 채널 (예측 포함)
        var raw = new byte[] { 1, 2, 3, 10, 10, 10, 5, 4, 3 };
        byte[] Z(byte[] x) { using var ms = new MemoryStream(); using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(x); return ms.ToArray(); }
        Check("ZIP 채널", PsdReader.RawChannelForTest(2, Z(raw), 3, 3, 8).SequenceEqual(raw));
        var pred = new byte[] { 1, 1, 1, 10, 0, 0, 5, 255, 255 };   // 차분 → 1,2,3 / 10,10,10 / 5,4,3
        Check("ZIP+예측 채널", PsdReader.RawChannelForTest(3, Z(pred), 3, 3, 8).SequenceEqual(raw));
        // 되읽은 작품 합성 = 원래 합성 (모양 레이어 그림 포함, 숨김 반영)
        bub.Visible = false; back.Layers.First(x => x.Name == "말풍선").Visible = false;
        var f0 = App.Comp.Flatten(d, new RenderOptions { Paper = false }); var f1 = App.Comp.Flatten(back, new RenderOptions { Paper = false });
        int diff = 0; var a0 = f0.Read(f0.Full); var a1 = f1.Read(f1.Full);
        for (int i = 0; i < a0.Length; i++) { var u0 = Get(a0[i]); var u1 = Get(a1[i]); if (Math.Abs(u0.a - u1.a) > 2 || Math.Abs(u0.r - u1.r) > 3 || Math.Abs(u0.g - u1.g) > 3 || Math.Abs(u0.b - u1.b) > 3) diff++; }
        Check("되읽은 작품 합성 = 원래 합성", diff == 0, $"다른 픽셀 {diff}");
        back.Layers.First(x => x.Name == "말풍선").Visible = true;
        SavePng(App.Comp.Flatten(back, new RenderOptions { Paper = false }), Path.Combine(outDir, "psd_roundtrip.png"));
    }

    /// <summary>대화상자를 열어 그림으로 남기고 바로 닫는다 (예외 없이 만들어지는지 · 닫으면 미리보기가 되돌아가는지)</summary>
    static void Dialogs(string outDir)
    {
        var d = Doc(160, 120); App.Doc = d;
        var l = Fill(d, "그림", (x, y) => Px.Pack(255, (uint)(x * 255 / 159), (uint)(y * 2), 90));
        var before = l.Px.Read(l.Px.Full);
        void Auto(string name, Action open)
        {
            var t = new System.Windows.Forms.Timer { Interval = 500 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                foreach (var f in Application.OpenForms.Cast<Form>().Where(f => f.Modal).ToList())
                {
                    try { using var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height)); f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(Path.Combine(outDir, "dlg_" + name + ".png")); } catch { }
                    f.Close();
                }
            };
            t.Start();
            try { open(); Check("대화상자 " + name, true); }
            catch (Exception ex) { Check("대화상자 " + name, false, ex.GetType().Name + ": " + ex.Message); }
            finally { t.Stop(); t.Dispose(); }
        }
        foreach (var (type, _) in AdjustTypes.List) Auto(type, () => AdjustDialogs.AdjustDialog(type));
        Check("보정 미리보기 취소 → 그림 그대로", l.Px.Read(l.Px.Full).AsSpan().SequenceEqual(before) && d.Active == l);
        var adj = AdjustDialogs.AddAdjustLayer("curves");
        Check("조정 레이어 추가 + 실행취소 기록", d.Layers.Contains(adj) && App.History.CanUndo && (string)adj.Meta["type"] == "curves");
        Auto("curves_edit", () => AdjustDialogs.AdjustDialog("curves", adj));
        Check("조정 레이어 편집 취소 → 미리보기 끔", LayerEffects.PreviewLayer == null);
        App.History.Undo(d);
        Check("조정 레이어 추가 실행취소", !d.Layers.Contains(adj));
        d.ActiveId = l.Id;
        Auto("fx", AdjustDialogs.FxDialog);
        Check("효과 대화상자 취소 → 효과 없음", l.Fx == null);
        Auto("tone", AdjustDialogs.ToneDialog);
        Check("톤화 대화상자 취소 → 톤 없음", l.Tone == null);
        Auto("paper", AdjustDialogs.PaperTextureDialog);
        Check("용지 질감 취소 → 그대로", d.PaperExtra["texture"] == null);
        App.History.Clear(); App.Doc = null;
    }

    static void Perf()
    {
        var d = Doc(2480, 3508);
        Fill(d, "바탕", (x, y) => Px.Pack(255, (uint)(x & 255), (uint)(y & 255), 128));
        var top = Fill(d, "그림", (x, y) => (x / 64 + y / 64) % 5 == 0 ? 0xFF202020 : 0);
        double T(Action a) { var sw = System.Diagnostics.Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }
        log.AppendLine($"참고  합성 (그대로): {T(() => App.Comp.Flatten(d)):0} ms");
        d.PaperExtra["texture"] = new JsonObject { ["type"] = "rough", ["strength"] = 0.6, ["scale"] = 1 };
        log.AppendLine($"참고  + 용지 질감: {T(() => App.Comp.Flatten(d)):0} ms");
        d.PaperExtra.Remove("texture");
        var adj = new Layer(d, "곡선", "adjust") { Meta = new JsonObject { ["type"] = "curves", ["params"] = AdjustTypes.Default("curves") } };
        d.Layers.Add(adj);
        log.AppendLine($"참고  + 조정 레이어: {T(() => App.Comp.Flatten(d)):0} ms");
        d.Layers.Remove(adj);
        top.Fx = new JsonObject { ["stroke"] = new JsonObject { ["on"] = true, ["size"] = 6, ["color"] = "#ffffff" }, ["glow"] = new JsonObject { ["on"] = true, ["size"] = 16, ["color"] = "#ffe680", ["opacity"] = 0.8 } };
        log.AppendLine($"참고  + 레이어 효과 (테두리·광선): {T(() => App.Comp.Flatten(d)):0} ms");
        top.Fx = null; top.Tone = new JsonObject { ["spacing"] = 8, ["angle"] = 45, ["shape"] = "dot", ["color"] = "#000000" };
        log.AppendLine($"참고  + 톤화 (처음): {T(() => App.Comp.Flatten(d)):0} ms, (캐시): {T(() => App.Comp.Flatten(d)):0} ms");
        top.Tone = null;
        App.Doc = d; d.ActiveId = top.Id;
        log.AppendLine($"참고  필터 흐리게 8px: {T(() => FilterOps.CanvasOp("흐리게", (s, w, h) => Raster.BlurRgba(s, w, h, 8))):0} ms");
        log.AppendLine($"참고  필터 레벨 적용: {T(() => FilterOps.CanvasOp("레벨", (s, w, h) => { var o = (uint[])s.Clone(); Svg.Adjust("levels", new JsonObject { ["inB"] = 20 }).Apply(o); return o; })):0} ms");
        var flat = App.Comp.Flatten(d);
        log.AppendLine($"참고  PSD 쓰기: {T(() => PsdWriter.Write(d, flat)):0} ms");
        App.History.Clear(); App.Doc = null;
    }

    static void Menus()
    {
        string[] must =
        {
            "보정/레벨 (밝기 범위)…", "보정/톤 커브 (곡선)…", "보정/색조∕채도∕명도 (HSL)…", "보정/조정 레이어: 자연 채도", "보정/레이어 효과 (테두리∕그림자∕광선)…",
            "필터/선화 추출 (흰 배경 → 투명)", "필터/언샤프 마스크 (선명하게)…", "필터/빛 번짐 (블룸)…", "필터/선 정리 (잡티·끊김·울퉁불퉁)…",
            "파일/PSD로 저장 (레이어 유지)…", "파일/웹툰 플랫폼 규격으로 내보내기…", "파일/인쇄용 내보내기 (PDF ∕ 1비트 TIFF)…", "파일/이미지 가져오기 (새 레이어)…",
            "편집/복사", "편집/병합 복사 (보이는 그림)", "편집/붙여넣기", "레이어/조정 레이어 값 수정", "보기/용지 질감 (종이 결)…", "선택/색역 선택 (같은 색 전부)…",
        };
        var missing = must.Where(m => !Registry.Menus.Any(x => x.Path == m)).ToList();
        Check("메뉴 등록", missing.Count == 0, string.Join(", ", missing));
        Check("메뉴 경로에 '/' 이름 없음", Registry.Menus.Where(m => m.Path.StartsWith("보정/") || m.Path.StartsWith("필터/")).All(m => m.Path.Count(c => c == '/') == 1));
        Check("PSD 열기 등록", UI.FileCommands.Openers.ContainsKey(".psd") && UI.FileCommands.Openers.ContainsKey(".PSB"));
        // 메인 창 메뉴 만들기 + "보정" 을 "필터" 다음으로
        using var mf = new UI.MainForm();
        FiltersModule.MoveAdjustMenu(null, EventArgs.Empty);
        var items = mf.MainMenuStrip.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();
        int fi = items.IndexOf("필터"), ai = items.IndexOf("보정");
        Check("메인 창 메뉴: 보정이 필터 다음", fi >= 0 && ai == fi + 1, string.Join(",", items));
        App.Main = null;
    }

    static void Print(string outDir)
    {
        // 1비트 TIFF: GDI+ 로 다시 읽어 비교
        int w = 37, h = 9;
        var gray = new byte[w * h]; for (int i = 0; i < gray.Length; i++) gray[i] = (byte)((i % w) * 255 / (w - 1));
        var bits = PrintExport.ToBits(gray, w, h, 128);
        var tif = PrintExport.Tiff1Bit(bits, w, h, 350);
        File.WriteAllBytes(Path.Combine(outDir, "mono.tif"), tif);
        using (var ms = new MemoryStream(tif))
        using (var bmp = new Bitmap(ms))
        {
            bool ok = bmp.Width == w && bmp.Height == h;
            for (int x = 0; x < w && ok; x++) { bool wh = bmp.GetPixel(x, 4).R > 128; if (wh != gray[4 * w + x] >= 128) ok = false; }
            Check("1비트 TIFF 되읽기", ok);
        }
        var pdf = PrintExport.BuildPdf(new() { new PrintExport.PdfPage(100, 50, w, h, 1, "DeviceGray", "FlateDecode", PrintExport.Deflate(bits)) });
        File.WriteAllBytes(Path.Combine(outDir, "mono.pdf"), pdf);
        string ps = Encoding.Latin1.GetString(pdf);
        Check("PDF 구조", ps.StartsWith("%PDF-1.4") && ps.Contains("/Type /XObject") && ps.TrimEnd().EndsWith("%%EOF"));
        // xref 위치가 맞는지
        int sx = ps.LastIndexOf("startxref", StringComparison.Ordinal);
        int xo = int.Parse(ps[(sx + 10)..].Split('\n')[0]);
        Check("PDF xref 위치", ps.Substring(xo, 4) == "xref");
        var t8 = PrintExport.Tiff8(gray, w, h, 1, 350);
        Check("8비트 TIFF 머리", t8[0] == 0x49 && t8[1] == 0x49 && t8[2] == 42);
        // ICC: Windows 의 SWOP 프로파일 (있으면)
        var swop = PrintExport.ListProfiles().FirstOrDefault(p => p.Path.EndsWith("RSWOP.icm", StringComparison.OrdinalIgnoreCase));
        if (swop == null) { log.AppendLine("건너뜀  ICC (RSWOP.icm 없음)"); return; }
        var prof = IccProfile.Parse(File.ReadAllBytes(swop.Path));
        Check("ICC 읽기 (CMYK)", prof.Space == "CMYK", prof.Space + " " + prof.Pcs + " " + prof.Name);
        var xf = new IccTransform(prof, 0);
        var white = xf.RgbToCmyk(1, 1, 1); var black = xf.RgbToCmyk(0, 0, 0);
        Check("흰색 → 잉크 거의 없음", white.All(v => v < 0.06), string.Join(",", white.Select(v => v.ToString("0.00"))));
        Check("검정 → 먹(K) 많음", black[3] > 0.6, string.Join(",", black.Select(v => v.ToString("0.00"))));
        var back = xf.CmykToRgb(white[0], white[1], white[2], white[3]);
        Check("흰색 되돌리기", back.All(v => v > 0.9), string.Join(",", back.Select(v => v.ToString("0.00"))));
        Check("빨강(sRGB 원색)은 인쇄로 못 냄 / 회색은 냄", xf.OutOfGamut(0, 0, 1) && !xf.OutOfGamut(0.5, 0.5, 0.5));
        var cube = IccCube.Build(xf, 17);
        var cm = cube.ToCmyk(new byte[] { 255, 255, 255, 128, 128, 128 });
        Check("CMYK 표 변환", cm[0] < 16 && cm[4] > 20, string.Join(",", cm));
        var tifc = PrintExport.Tiff8(cm, 2, 1, 4, 350, prof.Bytes);
        Check("CMYK TIFF (프로파일 포함)", tifc.Length > prof.Bytes.Length);
    }
}
