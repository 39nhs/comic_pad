// 펜·브러시 모듈 자체 시험 (창 없이): 엔진·도구·실행취소·벡터·ABR 등을 정해진 입력으로 그려 보고 결과 픽셀을 확인한다.
// 앱 안에서는 부르지 않음. 시험용 콘솔에서 SelfTest.Run(출력폴더) 를 부르면 결과 목록과 미리보기 그림(PNG)을 남긴다.
using System.Diagnostics;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Brushes;

public static class SelfTest
{
    public sealed record Result(string Name, bool Ok, string Detail);

    static Document NewDoc(int w = 200, int h = 100) { var d = Document.New(w, h); App.Doc = d; App.History.Clear(); return d; }
    static uint A(Surface s, int x, int y) => s.Get(x, y) >> 24;

    static BrushPreset Pre(Action<BrushPreset> f) { var p = new BrushPreset { Id = "t", Name = "t", Stabilizer = 0, PressureSize = false }; f(p); return p; }

    /// <summary>엔진으로 한 획을 레이어에 바로 (보정 없이 점 목록)</summary>
    static Rect Draw(Layer l, Document d, BrushPreset p, IList<BPt> pts, uint color = 0xFF000000, string sym = "none", bool erase = false, Selection sel = null, Func<int, int, uint> sample = null, int symN = 6, PenFeel feel = null)
    {
        var st = new BrushStroke(new StrokeSetup { W = d.W, H = d.H, Preset = p, Color = color, Symmetry = sym, SymN = symN, Seed = 3, Sample = sample, Feel = feel ?? new PenFeel(), Palette = PixelArt.PaletteOf(d) });
        st.Begin(pts[0]);
        for (int i = 1; i < pts.Count; i++) st.Add(pts[i]);
        var r = st.End(pts[^1]).Clip(d.W, d.H);
        new StrokeApply { Buf = st.Buf, Target = l.Px, Erase = erase, AlphaLock = l.AlphaLock, Mode = Px.Parse(p.Blend), Opacity = p.Opacity, Sel = sel, Post = p.HasPost ? p : null, Dpi = d.Dpi }.Apply(r);
        return r;
    }

    static List<BPt> HLine(double x0, double x1, double y, double p = 1, double step = 2)
    {
        var l = new List<BPt>(); int i = 0;
        for (double x = x0; x <= x1; x += step) l.Add(new BPt(x, y, p, i++ * 8));
        return l;
    }

    static PointerArgs PA(double x, double y, double p = 1, double t = 0, bool shift = false) => new() { X = x, Y = y, SX = x, SY = y, Pressure = p, Time = t, Type = "pen", Buttons = 1, Shift = shift };

    public static List<Result> Run(string outDir = null)
    {
        var res = new List<Result>();
        void Check(string name, Func<(bool ok, string detail)> f)
        {
            try { var (ok, detail) = f(); res.Add(new Result(name, ok, detail)); }
            catch (Exception ex) { res.Add(new Result(name, false, "예외: " + ex)); }
        }
        App.Settings = new JsonObject();

        Check("필압 곡선 LUT (곧게·단조)", () =>
        {
            var id = Pressure.CurveLut(new[] { new[] { 0.0, 0 }, new[] { 1.0, 1 } });
            var soft = Pressure.CurveLut(Pressure.Presets[1].pts);
            bool mono = true; for (int i = 1; i < 256; i++) if (soft[i] < soft[i - 1] - 1e-6) mono = false;
            bool ok = Math.Abs(id[128] - 128 / 255.0) < 1e-3 && id[0] == 0 && Math.Abs(id[255] - 1) < 1e-6 && mono && Math.Abs(soft[64] - 0.5) < 0.03;
            return (ok, $"id[128]={id[128]:0.000} soft[64]={soft[64]:0.000} 단조={mono}");
        });
        Check("필압 범위·감마·펜별 곡선", () =>
        {
            App.Settings["penFeel"] = new JsonObject { ["pmin"] = 0.2, ["pmax"] = 0.6 };
            App.Settings["pressureGamma"] = 2;
            var f = PenFeel.Current();
            double a = f.Curve(0.1), b = f.Curve(0.4), c = f.Curve(0.9);
            var pre = new BrushPreset { PressurePts = new[] { new[] { 0.0, 1 }, new[] { 1.0, 1 } } };
            double d = f.Curve(0.1, pre);
            App.Settings = new JsonObject();
            return (a == 0 && Math.Abs(b - 0.25) < 1e-9 && c == 1 && d == 1, $"{a} {b:0.###} {c} 펜별={d}");
        });
        Check("프리셋 JSON 왕복 · 기본 목록", () =>
        {
            var all = DefaultPresets.Of("brush").Concat(DefaultPresets.Of("eraser")).ToList();
            bool rt = all.All(p => BrushPreset.FromJson(p.ToJson()).ToJson().ToJsonString() == p.ToJson().ToJsonString());
            var j = DefaultPresets.Of("brush").Find(p => p.Id == "pencilgrain").ToJson();
            var ext = BrushPreset.FromJson(new JsonObject { ["id"] = "x", ["name"] = "x", ["abr"] = "foo", ["size"] = 7 });
            bool keep = (string)ext.ToJson()["abr"] == "foo" && ext.Size == 7 && ext.Hardness == 1;
            return (rt && keep && DefaultPresets.Of("brush").Count == 24 && DefaultPresets.Of("eraser").Count == 4 && (string)j["texture"] == "rough" && j.ContainsKey("pressurePts") && j.ContainsKey("textureMode"),
                $"왕복={rt} 모르는키보관={keep} 키 {j.Count}개");
        });
        Check("프리셋 저장소 (설정 brushes·activePreset·seenPresets)", () =>
        {
            App.Settings = new JsonObject();
            var l = PresetStore.List("brush");
            var act = PresetStore.Active("brush");
            PresetStore.SetActive("brush", "gpen");
            l.RemoveAll(p => p.Id == "maru"); PresetStore.Save("brush");
            PresetStore.Reload();
            var l2 = PresetStore.List("brush");
            bool ok = l.Count == 23 && act.Id == "notepen" && PresetStore.Active("brush").Id == "gpen" && l2.Count == 23 && !l2.Any(p => p.Id == "maru")
                && App.Settings["brushes"]?["brush"] is JsonArray a && a.Count == 23 && App.Settings["seenPresets"]?["brush"] is JsonArray sp && sp.Count == 24;
            App.Settings = new JsonObject(); PresetStore.Reload();
            return (ok, $"목록 {l2.Count} 활성 {act.Id}→gpen, 지운 기본 프리셋은 다시 안 생김");
        });
        Check("딱딱한 원: 선뭉침 없음 (덮인 정도 최댓값)", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            var p = Pre(p => { p.Size = 20; p.Hardness = 1; p.Spacing = 0.05; });
            Draw(l, d, p, HLine(20, 180, 50.3));
            int mn = 255, mx = 0;
            for (int x = 40; x < 160; x++) { int a = (int)A(l.Px, x, 60); mn = Math.Min(mn, a); mx = Math.Max(mx, a); }
            var d2 = NewDoc(); var l2 = d2.Layers[0];
            var p2 = Pre(p => { p.Size = 20; p.Hardness = 1; p.Spacing = 0.05; p.Flow = 0.999; });   // 합쳐 칠하기 끔
            Draw(l2, d2, p2, HLine(20, 180, 50.3));
            int acc = (int)A(l2.Px, 100, 60);
            return (A(l.Px, 100, 50) == 255 && mx <= 80 && mx - mn <= 12 && acc > 150, $"가장자리 줄 알파 {mn}~{mx} (겹쳐 쌓기면 {acc})");
        });
        Check("부드러운 원: 흐름 누적 (0.5 두 번 = 75%)", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            var p = Pre(p => { p.Size = 30; p.Hardness = 0; p.Flow = 0.5; });
            var st = new BrushStroke(new StrokeSetup { W = d.W, H = d.H, Preset = p, Feel = new PenFeel() });
            st.Polyline(new[] { new BPt(50, 50, 1) }); st.Polyline(new[] { new BPt(50, 50, 1) });
            uint a = st.Buf.Get(50, 50) >> 24, e = st.Buf.Get(50, 63) >> 24;
            double g = Tips.Grad(0, Math.Sqrt(0.5) / 15), want = (1 - (1 - 0.5 * g) * (1 - 0.5 * g)) * 255;
            return (Math.Abs(a - want) <= 3 && e < a, $"가운데 {a} (계산 {want:0}) 가장자리 {e}");
        });
        Check("불투명도는 획 전체에 한 번 (겹쳐도 진해지지 않음)", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            var p = Pre(p => { p.Size = 10; p.Opacity = 0.5; });
            Draw(l, d, p, HLine(20, 180, 50).Concat(HLine(20, 180, 50).AsEnumerable().Reverse()).ToList());
            uint a = A(l.Px, 100, 50);
            return (Math.Abs((int)a - 128) <= 1, $"알파 {a}");
        });
        Check("지우개", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            l.Px.Write(new Rect(0, 0, 200, 100), Enumerable.Repeat(0xFFFF0000u, 200 * 100).ToArray());
            Draw(l, d, Pre(p => { p.Size = 10; }), HLine(20, 180, 50), erase: true);
            return (A(l.Px, 100, 50) == 0 && l.Px.Get(100, 20) == 0xFFFF0000u, $"지운 곳 {A(l.Px, 100, 50)}, 밖 {l.Px.Get(100, 20):X8}");
        });
        Check("투명 픽셀 잠금", () =>
        {
            var d = NewDoc(); var l = d.Layers[0]; l.AlphaLock = true;
            l.Px.Write(new Rect(90, 40, 20, 20), Enumerable.Repeat(0xFFFF0000u, 400).ToArray());
            Draw(l, d, Pre(p => { p.Size = 10; }), HLine(20, 180, 50), color: 0xFF0000FF);
            return (l.Px.Get(100, 50) == 0xFF0000FFu && A(l.Px, 50, 50) == 0, $"안 {l.Px.Get(100, 50):X8} 밖 {A(l.Px, 50, 50)}");
        });
        Check("선택 영역 밖은 그대로", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            var m = new byte[200 * 100]; for (int y = 0; y < 100; y++) for (int x = 0; x < 100; x++) m[y * 200 + x] = 255;
            d.Sel.Mask = m;
            Draw(l, d, Pre(p => { p.Size = 10; }), HLine(20, 180, 50), sel: d.Sel);
            return (A(l.Px, 50, 50) == 255 && A(l.Px, 150, 50) == 0, $"안 {A(l.Px, 50, 50)} 밖 {A(l.Px, 150, 50)}");
        });
        Check("대칭 (좌우·방사)", () =>
        {
            var d = NewDoc(200, 200); var l = d.Layers[0];
            Draw(l, d, Pre(p => { p.Size = 6; }), new[] { new BPt(30, 40, 1) }, sym: "v");
            bool v = A(l.Px, 170, 40) == 255;
            var d2 = NewDoc(200, 200); var l2 = d2.Layers[0];
            Draw(l2, d2, Pre(p => { p.Size = 6; }), new[] { new BPt(130, 100, 1) }, sym: "radial", symN: 4);
            bool r = A(l2.Px, 100, 130) == 255 && A(l2.Px, 70, 100) == 255 && A(l2.Px, 100, 70) == 255;
            return (v && r, $"좌우={v} 방사4={r}");
        });
        Check("픽셀 끝: 픽셀 퍼펙트 (ㄴ자 모서리 지움)", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            var p = Pre(p => { p.Tip = "pixel"; p.Size = 1; p.Spacing = 0.1; p.PixelPerfect = true; });
            Draw(l, d, p, new[] { new BPt(10.5, 10.5, 1), new BPt(11.5, 10.5, 1), new BPt(11.5, 11.5, 1), new BPt(11.5, 11.5, 1) });
            bool ok = A(l.Px, 10, 10) == 255 && A(l.Px, 11, 11) == 255 && A(l.Px, 11, 10) == 0;
            return (ok, $"(10,10)={A(l.Px, 10, 10)} (11,10)={A(l.Px, 11, 10)} (11,11)={A(l.Px, 11, 11)}");
        });
        Check("픽셀 디더 50% (2×2)", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            var p = Pre(p => { p.Tip = "pixel"; p.Size = 4; p.Dither = 0.5; p.DitherSize = 2; p.PixelPerfect = false; });
            Draw(l, d, p, new[] { new BPt(20.5, 20.5, 1) });
            int n = 0; for (int y = 19; y < 23; y++) for (int x = 19; x < 23; x++) if (A(l.Px, x, y) == 255) n++;
            return (n == 8, $"4×4 중 {n}칸");
        });
        Check("입출 보정 (시작이 가늘게)", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            var p = Pre(p => { p.Size = 16; p.TaperIn = 60; p.TaperOut = 60; p.Spacing = 0.05; });
            Draw(l, d, p, HLine(20, 180, 50));
            int Thick(int x) { int n = 0; for (int y = 0; y < 100; y++) if (A(l.Px, x, y) > 128) n++; return n; }
            int a = Thick(22), b = Thick(100), c = Thick(178);
            return (a < b - 6 && c < b - 6, $"굵기 시작 {a} 가운데 {b} 끝 {c}");
        });
        Check("끈 당기기 보정 + 펜을 떼면 따라잡기", () =>
        {
            var d = NewDoc(); var p = Pre(p => { p.Size = 4; p.StabMode = "lazy"; p.Stabilizer = 8; });
            var st = new BrushStroke(new StrokeSetup { W = d.W, H = d.H, Preset = p, Feel = new PenFeel() });
            st.Begin(new BPt(20, 50, 1, 0)); st.Add(new BPt(30, 50, 1, 8)); st.Add(new BPt(36, 50, 1, 16));
            uint before = st.Buf.Get(34, 50) >> 24;
            st.End(new BPt(36, 50, 1, 24));
            uint after = st.Buf.Get(34, 50) >> 24;
            return (before == 0 && after > 200, $"끝 {before} → {after}");
        });
        Check("1€·무거운 펜·평균 보정 (끝까지 그려짐)", () =>
        {
            var outp = new List<string>(); bool ok = true;
            foreach (var mode in new[] { "euro", "spring", "avg" })
            {
                var d = NewDoc(); var p = Pre(p => { p.Size = 4; p.StabMode = mode; p.Stabilizer = 6; });
                var st = new BrushStroke(new StrokeSetup { W = d.W, H = d.H, Preset = p, Feel = new PenFeel() });
                var pts = HLine(20, 150, 50);
                st.Begin(pts[0]); foreach (var q in pts.Skip(1)) st.Add(q);
                st.End(pts[^1]);
                uint a = st.Buf.Get(148, 50) >> 24;
                outp.Add($"{mode}={a}"); ok &= a > 200;
            }
            return (ok, string.Join(" ", outp));
        });
        Check("곱하기 합성 모드", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            l.Px.Write(new Rect(0, 0, 200, 100), Enumerable.Repeat(0xFF808080u, 200 * 100).ToArray());
            Draw(l, d, Pre(p => { p.Size = 10; p.Blend = "multiply"; }), HLine(20, 180, 50), color: 0xFF808080);
            uint c = l.Px.Get(100, 50) & 255;
            return (Math.Abs((int)c - 64) <= 2, $"128×128 → {c}");
        });
        Check("혼색 (밑 색을 끌고 감)", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            l.Px.Write(new Rect(0, 0, 100, 100), Enumerable.Repeat(0xFFFF0000u, 100 * 100).ToArray());
            var before = l.Px.Clone();
            var p = Pre(p => { p.Size = 12; p.Hardness = 0.9; p.Mix = 0.45; p.Spacing = 0.06; });
            Draw(l, d, p, HLine(20, 180, 50).AsEnumerable().Reverse().ToList(), color: 0xFF0000FF, sample: (x, y) => before.Get(x, y));
            uint c1 = l.Px.Get(150, 50), c2 = l.Px.Get(60, 50);
            uint b1 = c1 & 255, r2 = (c2 >> 16) & 255, b2 = c2 & 255;
            return (b1 > 200 && r2 > 150 && b2 < 100, $"빈 곳 파랑 {b1}, 빨강 위 → 빨강 {r2} 파랑 {b2}");
        });
        Check("명암 잉크 (팔레트에서 한 칸 어둡게)", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            l.Px.Write(new Rect(0, 0, 200, 100), Enumerable.Repeat(0xFF7A7A7Au, 200 * 100).ToArray());
            var before = l.Px.Clone();
            var p = Pre(p => { p.Tip = "pixel"; p.Size = 1; p.Shading = true; p.ShadeDir = -1; });
            Draw(l, d, p, new[] { new BPt(10.5, 10.5, 1), new BPt(30.5, 10.5, 1) }, sample: (x, y) => before.Get(x, y));
            uint c = l.Px.Get(20, 10) & 0xFFFFFF;
            return (c == 0x3A3A3A, $"#7a7a7a → #{c:x6}");
        });
        Check("끝 모양 전부 (연필결·스프레이·납작 펜·이미지·리본·듀얼) 그려짐", () =>
        {
            var parts = new List<string>(); bool ok = true;
            var img = new System.Drawing.Bitmap(32, 16); using (var g = System.Drawing.Graphics.FromImage(img)) g.FillEllipse(System.Drawing.Brushes.Black, 0, 0, 32, 16);
            string tmp = Path.Combine(Path.GetTempPath(), "comicpad-selftest-tip.png"); img.Save(tmp, System.Drawing.Imaging.ImageFormat.Png); img.Dispose();
            var kinds = new (string n, Action<BrushPreset> f)[]
            {
                ("grain", p => { p.Tip = "grain"; p.Grain = 0.6; p.Hardness = 0.5; }),
                ("spray", p => { p.Tip = "spray"; p.Density = 26; p.Spacing = 0.25; }),
                ("flat", p => { p.Tip = "flat"; p.FlatRatio = 0.2; p.Angle = 40; }),
                ("image", p => { p.Tip = "image"; p.TipImage = tmp; p.Spacing = 0.3; }),
                ("image색", p => { p.Tip = "image"; p.TipImage = tmp; p.TintTip = false; }),
                ("ribbon", p => { p.Tip = "grain"; p.Grain = 0.5; p.Ribbon = true; p.Hardness = 0.7; }),
                ("dual", p => { p.Hardness = 0.8; p.DualTip = "grain"; p.DualSize = 0.4; p.DualDensity = 7; }),
                ("dual원", p => { p.DualTip = "round"; }), ("dual점", p => { p.DualTip = "spray"; }),
                ("jitter", p => { p.HueJitter = 0.2; p.SatJitter = 0.3; p.ValJitter = 0.3; p.SizeJitter = 0.5; p.Scatter = 0.5; p.RotJitter = 1; }),
                ("texture", p => { p.Texture = "rough"; p.TextureStrength = 0.75; }),
                ("texture빼기", p => { p.Texture = "canvas"; p.TextureMode = "subtract"; }),
                ("texture문턱", p => { p.Texture = "kraft"; p.TextureMode = "hard"; }),
                ("dither", p => { p.Dither = 0.5; }),
            };
            foreach (var (n, f) in kinds)
            {
                var d = NewDoc(); var l = d.Layers[0];
                var p = Pre(p => { p.Size = 24; }); f(p);
                Draw(l, d, p, HLine(20, 180, 50));
                int cnt = 0; for (int x = 30; x < 170; x++) for (int y = 30; y < 70; y++) if (A(l.Px, x, y) > 0) cnt++;
                parts.Add($"{n}:{cnt}"); ok &= cnt > 300;
            }
            try { File.Delete(tmp); } catch { }
            return (ok, string.Join(" ", parts));
        });
        Check("종이 결 타일 (무늬가 있음)", () =>
        {
            var m = PaperGrain.Mask("rough", 0.75);
            int mn = m.Min(b => b), mx = m.Max(b => b);
            var k = PaperGrain.Tile("kraft");
            return (mx - mn > 100 && k != null && k.Distinct().Count() > 10, $"수채지 결 {mn}~{mx}");
        });
        Check("브러시 도구: 그리기 → 실행취소 → 다시실행", () =>
        {
            App.Settings = new JsonObject(); PresetStore.Reload();
            var d = NewDoc(); var l = d.Layers[0];
            var tool = new PaintTool("brush", "브러시", "✎", Keys.B, "brush", false);
            PresetStore.SetActive("brush", "gpen");
            tool.Down(PA(20, 50, 0.8)); for (int i = 1; i <= 40; i++) tool.Move(PA(20 + i * 4, 50, 0.8, i * 8)); tool.Up(PA(180, 50, 0.8, 330));
            uint a1 = A(l.Px, 100, 50);
            App.History.Undo(d); uint a0 = A(l.Px, 100, 50);
            App.History.Redo(d); uint a2 = A(l.Px, 100, 50);
            return (a1 == 255 && a0 == 0 && a2 == 255 && d.Modified, $"{a1} → 취소 {a0} → 다시 {a2}");
        });
        Check("Shift 직선 · Ctrl 15도", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            var tool = new PaintTool("brush", "브러시", "✎", Keys.B, "brush", false);
            tool.Down(PA(20, 20, 1, 0, shift: true));
            tool.Move(PA(100, 70, 1, 8)); tool.Move(PA(150, 60, 1, 16));
            var e = PA(180, 62, 1, 24); e.Ctrl = true; tool.Move(e); tool.Up(e);
            // 15도 단위: (20,20)→ 각도 약 14.4° → 15° 로
            double ang = 15 * Math.PI / 180, dist = Math.Sqrt(160 * 160 + 42 * 42);
            int ex = (int)(20 + Math.Cos(ang) * dist * 0.5), ey = (int)(20 + Math.Sin(ang) * dist * 0.5);
            bool ok = A(l.Px, ex, ey) > 0 && A(l.Px, 100, 70) == 0;
            return (ok, $"직선 위 ({ex},{ey})={A(l.Px, ex, ey)}, 지난 끝점 자리={A(l.Px, 100, 70)}");
        });
        Check("벡터 레이어: 선 기록 → 다시 그리기 → 실행취소", () =>
        {
            App.Settings = new JsonObject(); PresetStore.Reload();
            var d = NewDoc(); var l = new Layer(d, "벡터", "vector") { Meta = new JsonObject { ["strokes"] = new JsonArray() } };
            d.Layers.Add(l); d.ActiveId = l.Id;
            var tool = new PaintTool("brush", "브러시", "✎", Keys.B, "brush", false);
            PresetStore.SetActive("brush", "gpen");
            tool.Down(PA(20, 50, 0.8)); for (int i = 1; i <= 40; i++) tool.Move(PA(20 + i * 4, 50, 0.8, i * 8)); tool.Up(PA(180, 50, 0.8, 330));
            var s = VectorLayer.Strokes(l);
            bool rec = s.Count == 1 && ((JsonArray)s[0]["pts"]).Count % 4 == 0 && ((JsonArray)s[0]["pts"]).Count >= 8 && (string)s[0]["color"] == "#000000" && s[0]["preset"]?["size"] != null;
            uint live = A(l.Px, 100, 50);
            VectorLayer.Render(l, d); uint re = A(l.Px, 100, 50);
            App.History.Undo(d); uint un = A(l.Px, 100, 50); int n0 = VectorLayer.Strokes(l).Count;
            App.History.Redo(d); uint rd = A(l.Px, 100, 50);
            return (rec && live == 255 && re == 255 && un == 0 && n0 == 0 && rd == 255, $"기록={rec} 그림 {live} 다시 {re} 취소 {un}/{n0}선 다시실행 {rd}");
        });
        Check("벡터 지우개 (닿은 부분만 / 선 전체 / 교차점까지)", () =>
        {
            var d = NewDoc(); var l = new Layer(d, "벡터", "vector");
            JsonObject Line(double x0, double y0, double x1, double y1)
            {
                var pts = new JsonArray();
                for (int i = 0; i <= 20; i++) { pts.Add(x0 + (x1 - x0) * i / 20); pts.Add(y0 + (y1 - y0) * i / 20); pts.Add(1); pts.Add(i * 8); }
                return new JsonObject { ["pts"] = pts, ["preset"] = new JsonObject { ["size"] = 4, ["pressureSize"] = false }, ["color"] = "#000000" };
            }
            l.Meta = new JsonObject { ["strokes"] = new JsonArray(Line(20, 50, 180, 50)) };
            VectorLayer.EraseAt(l, 100, 50, 3, false); int part = VectorLayer.Strokes(l).Count;
            l.Meta = new JsonObject { ["strokes"] = new JsonArray(Line(20, 50, 180, 50)) };
            VectorLayer.EraseAt(l, 100, 50, 3, true); int whole = VectorLayer.Strokes(l).Count;
            l.Meta = new JsonObject { ["strokes"] = new JsonArray(Line(20, 50, 180, 50), Line(60, 10, 60, 90), Line(140, 10, 140, 90)) };
            VectorLayer.EraseToCross(l, 100, 50, 3);
            var st = VectorLayer.Strokes(l);
            var xs = st.Take(st.Count).Where(x => VectorLayer.Pts((JsonObject)x)[1] == 50 || VectorLayer.Pts((JsonObject)x).Length > 0).Select(x => VectorLayer.Pts((JsonObject)x)).ToList();
            bool cross = st.Count == 4 && xs.Any(p => Math.Abs(p[^4] - 60) < 0.5) && xs.Any(p => Math.Abs(p[0] - 140) < 0.5);
            return (part == 2 && whole == 0 && cross, $"부분={part}선 전체={whole}선 교차점={st.Count}선");
        });
        Check("벡터 지우개 도구 (실행취소)", () =>
        {
            App.Settings = new JsonObject(); PresetStore.Reload();
            var d = NewDoc(); var l = new Layer(d, "벡터", "vector");
            var pts = new JsonArray(); for (int i = 0; i <= 20; i++) { pts.Add(20 + i * 8); pts.Add(50); pts.Add(1); pts.Add(i * 8); }
            l.Meta = new JsonObject { ["strokes"] = new JsonArray(new JsonObject { ["pts"] = pts, ["preset"] = new JsonObject { ["size"] = 6 }, ["color"] = "#ff0000" }) };
            d.Layers.Add(l); d.ActiveId = l.Id; VectorLayer.Render(l, d);
            App.Settings["vectorErase"] = "whole";
            var er = new PaintTool("eraser", "지우개", "⌫", Keys.E, "eraser", true);
            er.Down(PA(100, 50)); er.Move(PA(101, 50)); er.Up(PA(101, 50));
            int after = VectorLayer.Strokes(l).Count; uint px = A(l.Px, 60, 50);
            App.History.Undo(d);
            int back = VectorLayer.Strokes(l).Count; uint px2 = A(l.Px, 60, 50);
            return (after == 0 && px == 0 && back == 1 && px2 == 255, $"지움 {after}선/{px} → 취소 {back}선/{px2}");
        });
        Check("지우개 도구 (래스터) + 마스크 편집", () =>
        {
            App.Settings = new JsonObject(); PresetStore.Reload();
            var d = NewDoc(); var l = d.Layers[0];
            l.Px.Write(new Rect(0, 0, 200, 100), Enumerable.Repeat(0xFF00FF00u, 200 * 100).ToArray());
            var er = new PaintTool("eraser", "지우개", "⌫", Keys.E, "eraser", true);
            er.Down(PA(20, 50)); for (int i = 1; i <= 20; i++) er.Move(PA(20 + i * 8, 50, 1, i * 8)); er.Up(PA(180, 50, 1, 170));
            bool er1 = A(l.Px, 100, 50) == 0 && A(l.Px, 100, 10) == 255;
            l.Mask = new Surface(200, 100); l.EditMask = true;
            var br = new PaintTool("brush", "브러시", "✎", Keys.B, "brush", false); PresetStore.SetActive("brush", "marker");
            br.Down(PA(20, 50)); for (int i = 1; i <= 20; i++) br.Move(PA(20 + i * 8, 50, 1, i * 8)); br.Up(PA(180, 50, 1, 170));
            bool m = A(l.Mask, 100, 50) == 255 && l.Px.Get(100, 10) == 0xFF00FF00u;
            return (er1 && m, $"지우개={er1} 마스크={m}");
        });
        Check("손끝·흐림 브러시 (실행취소)", () =>
        {
            var d = NewDoc(); var l = d.Layers[0];
            l.Px.Write(new Rect(0, 0, 100, 100), Enumerable.Repeat(0xFF000000u, 100 * 100).ToArray());
            var sm = new SmudgeTool();
            sm.Down(PA(90, 50)); for (int i = 1; i <= 10; i++) sm.Move(PA(90 + i * 4, 50, 1, i * 8)); sm.Up(PA(130, 50));
            uint smA = A(l.Px, 108, 50);
            App.History.Undo(d); uint und = A(l.Px, 108, 50);
            l.Px.Write(new Rect(100, 0, 100, 100), Enumerable.Repeat(0xFFFFFFFFu, 100 * 100).ToArray());
            var bl = new BlurBrushTool();
            bl.Down(PA(100, 50)); bl.Move(PA(101, 50)); bl.Up(PA(101, 50));
            uint g1 = l.Px.Get(98, 50) & 255, g2 = l.Px.Get(102, 50) & 255;
            App.History.Undo(d); uint g3 = l.Px.Get(98, 50) & 255;
            return (smA > 0 && und == 0 && g1 > 20 && g2 < 235 && g3 == 0, $"문지름 {smA}→취소 {und}, 흐림 경계 {g1}/{g2} → 취소 {g3}");
        });
        Check("ABR 읽기 (v2 압축 없음·v1 RLE)", () =>
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            void U16(int v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }
            void I32(int v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
            U16(2); U16(1); U16(2);
            var body = new MemoryStream(); var bw = new BinaryWriter(body);
            void BU16(int v) { bw.Write((byte)(v >> 8)); bw.Write((byte)v); }
            void BI32(int v) { bw.Write((byte)(v >> 24)); bw.Write((byte)(v >> 16)); bw.Write((byte)(v >> 8)); bw.Write((byte)v); }
            BI32(0); BU16(25); BI32(1); BU16('T'); bw.Write((byte)1); bw.Write(new byte[8]); BI32(0); BI32(0); BI32(4); BI32(3); BU16(8); bw.Write((byte)0);
            for (int i = 0; i < 12; i++) bw.Write((byte)(i * 20));
            I32((int)body.Length); w.Write(body.ToArray());
            var s = Abr.Read(ms.ToArray());
            bool v2 = s.Count == 1 && s[0].W == 3 && s[0].H == 4 && s[0].Name == "T" && Math.Abs(s[0].Spacing - 0.25) < 1e-9 && s[0].Mask[5] == 100;
            // v1 + PackBits: 한 줄 4픽셀 [7,7,7,9] = (-2,7)(0,9)
            var m2 = new MemoryStream(); var w2 = new BinaryWriter(m2);
            void W16(int v) { w2.Write((byte)(v >> 8)); w2.Write((byte)v); }
            void W32(int v) { w2.Write((byte)(v >> 24)); w2.Write((byte)(v >> 16)); w2.Write((byte)(v >> 8)); w2.Write((byte)v); }
            W16(1); W16(1); W16(2);
            var b2 = new MemoryStream(); var x2 = new BinaryWriter(b2);
            void X16(int v) { x2.Write((byte)(v >> 8)); x2.Write((byte)v); }
            void X32(int v) { x2.Write((byte)(v >> 24)); x2.Write((byte)(v >> 16)); x2.Write((byte)(v >> 8)); x2.Write((byte)v); }
            X32(0); X16(50); x2.Write((byte)1); x2.Write(new byte[8]); X32(0); X32(0); X32(2); X32(4); X16(8); x2.Write((byte)1);
            X16(4); X16(4); foreach (var bb in new byte[] { 0xFE, 7, 0, 9, 0xFE, 7, 0, 9 }) x2.Write(bb);
            W32((int)b2.Length); w2.Write(b2.ToArray());
            var s1 = Abr.Read(m2.ToArray());
            bool v1 = s1.Count == 1 && s1[0].W == 4 && s1[0].H == 2 && s1[0].Mask.SequenceEqual(new byte[] { 7, 7, 7, 9, 7, 7, 7, 9 });
            return (v2 && v1, $"v2={v2} v1(RLE)={v1}");
        });
        Check("프리셋 가져오기·내보내기 (.cpset 형식)", () =>
        {
            App.Settings = new JsonObject(); PresetStore.Reload();
            var p = PresetStore.List("brush")[1].Clone(); p.Id = "exp1"; p.Name = "내보낸 펜"; p.Size = 33;
            var file = BrushesModule.BuildSetFile("brush", new[] { p });
            int n = BrushesModule.ImportSetFile(file.ToJsonString(), "brush");
            var got = PresetStore.List("brush").Find(x => x.Id == "exp1");
            int n2 = BrushesModule.ImportSetFile(file.ToJsonString(), "brush");   // 같은 id 는 바꿈
            int cnt = PresetStore.List("brush").Count(x => x.Id == "exp1");
            App.Settings = new JsonObject(); PresetStore.Reload();
            return (n == 1 && got?.Size == 33 && n2 == 1 && cnt == 1 && (string)file["format"] == "comicpad-settings", $"가져옴 {n}, 크기 {got?.Size}, 중복 {cnt}");
        });
        Check("모듈 등록 (도구·메뉴)", () =>
        {
            new BrushesModule().Init();
            bool tools = Registry.Tools.Find(t => t.Id == "brush") is PaintTool && Registry.Tools.Find(t => t.Id == "eraser") is PaintTool
                && Registry.Tools.Any(t => t.Id == "smudge") && Registry.Tools.Any(t => t.Id == "blurbrush");
            bool menus = new[] { "파일/설정/펜·필압…", "파일/포토샵 브러시(.abr) 가져오기…", "레이어/벡터를 일반 레이어로", "보기/방사 대칭 (만다라)…" }.All(m => Registry.Menus.Any(x => x.Path == m));
            double g = Tools.BrushTool.GlobalCurve(0.5);
            return (tools && menus && Math.Abs(g - 0.5) < 0.01, $"도구={tools} 메뉴={menus} 공통 곡선(0.5)={g:0.00}");
        });
        Check(".cpad 왕복: 벡터 선 meta 그대로", () =>
        {
            var d = NewDoc(); var l = new Layer(d, "벡터", "vector");
            var pts = new JsonArray(); for (int i = 0; i <= 5; i++) { pts.Add(20.25 + i * 8); pts.Add(50.5); pts.Add(0.75); pts.Add(i * 8); }
            l.Meta = new JsonObject { ["strokes"] = new JsonArray(new JsonObject { ["pts"] = pts, ["preset"] = VectorLayer.PresetSnapshot(DefaultPresets.Find("brush", "gpen")), ["color"] = "#112233" }) };
            d.Layers.Add(l); VectorLayer.Render(l, d);
            var back = CpadIO.FromJson(JsonNode.Parse(CpadIO.ToJson(d).ToJsonString()).AsObject());
            var bl = back.Layers.Find(x => x.Kind == "vector");
            bool same = bl != null && bl.Meta["strokes"].ToJsonString() == l.Meta["strokes"].ToJsonString() && A(bl.Px, 40, 50) == A(l.Px, 40, 50) && A(l.Px, 40, 50) > 0;
            return (same, $"meta 같음={same}");
        });
        Check("속도: 큰 원고(2480×3508) G펜 긴 획", () =>
        {
            var d = NewDoc(2480, 3508); var l = d.Layers[0];
            var p = DefaultPresets.Find("brush", "gpen"); p.Size = 12;
            var pts = new List<BPt>(); for (int i = 0; i < 1500; i++) pts.Add(new BPt(200 + i * 1.3, 1500 + Math.Sin(i / 60.0) * 400, 0.5 + 0.5 * Math.Sin(i / 30.0), i * 4));
            var sw = Stopwatch.StartNew();
            Draw(l, d, p, pts);
            double ms = sw.Elapsed.TotalMilliseconds;
            var p2 = DefaultPresets.Find("brush", "air");
            sw.Restart(); Draw(l, d, p2, pts.Take(400).ToList()); double ms2 = sw.Elapsed.TotalMilliseconds;
            return (ms < 3000, $"G펜 1500점 {ms:0}ms, 에어브러시 400점 {ms2:0}ms");
        });

        // 미리보기 그림: 모든 기본 프리셋
        if (outDir != null)
        {
            try
            {
                Directory.CreateDirectory(outDir);
                App.Settings = new JsonObject(); PresetStore.Reload();
                var all = DefaultPresets.Of("brush").Concat(DefaultPresets.Of("eraser")).ToList();
                int W = 300, H = 64;
                using var sheet = new Bitmap(W * 2 + 20, (H + 18) * ((all.Count + 1) / 2) + 10);
                using (var g = Graphics.FromImage(sheet))
                {
                    g.Clear(Color.FromArgb(47, 47, 53));
                    using var f = new Font("Malgun Gothic", 9);
                    for (int i = 0; i < all.Count; i++)
                    {
                        int cx = (i % 2) * (W + 10) + 5, cy = (i / 2) * (H + 18) + 5;
                        bool er = i >= 24;
                        using var bmp = BrushPreview.Bitmap(all[i], W, H, er ? null : 0xFF1B1B1Fu, er);
                        g.DrawImage(bmp, cx, cy + 16);
                        g.DrawString(all[i].Name, f, System.Drawing.Brushes.White, cx, cy);
                    }
                }
                sheet.Save(Path.Combine(outDir, "presets.png"), System.Drawing.Imaging.ImageFormat.Png);
                App.Settings = new JsonObject(); PresetStore.Reload();
                // 확대 그림: 선뭉침 비교 (G펜)
                var d = NewDoc(240, 120); var l = d.Layers[0];
                Draw(l, d, Pre(p => { p.Size = 18; p.Hardness = 1; p.Spacing = 0.05; }), HLine(20, 220, 40.3, 1, 1.3));
                Draw(l, d, Pre(p => { p.Size = 18; p.Hardness = 1; p.Spacing = 0.05; p.Flow = 0.999; }), HLine(20, 220, 85.3, 1, 1.3));
                using var bm = BUI.ToBitmap(l.Px, new Rect(0, 0, 240, 120));
                using var big = new Bitmap(960, 480);
                using (var g = Graphics.FromImage(big)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half; g.DrawImage(bm, 0, 0, 960, 480); }
                big.Save(Path.Combine(outDir, "union_vs_accumulate.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex) { res.Add(new Result("미리보기 그림 저장", false, ex.ToString())); }
        }
        App.Doc = null; App.History.Clear(); App.Settings = new JsonObject(); PresetStore.Reload();
        return res;
    }
}
