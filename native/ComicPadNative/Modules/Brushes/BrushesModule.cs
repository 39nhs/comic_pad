// 펜·브러시 모듈 등록: 브러시·지우개(기본 도구 대체)·손끝·흐림/선명 브러시, 펜·필압 설정, ABR 가져오기, 대칭, 벡터 레이어 메뉴
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.BrushEngine;

public sealed class BrushesModule : IModule
{
    static PenFeel cachedFeel;

    public void Init()
    {
        Registry.Tool(new PaintTool("brush", "브러시", "✎", Keys.B, "brush", false));
        Registry.Tool(new PaintTool("eraser", "지우개", "⌫", Keys.E, "eraser", true));
        Registry.Tool(new SmudgeTool());
        Registry.Tool(new BlurBrushTool());

        // 다른 도구(기본 BrushTool 을 쓰는 것)도 같은 필압 곡선으로
        App.SettingsChanged += () => cachedFeel = null;
        Tools.BrushTool.GlobalCurve = p => (cachedFeel ??= PenFeel.Current()).Curve(p);

        bool HasDoc() => App.Doc != null;
        bool IsVector() => App.Doc?.Active?.Kind == "vector";
        Registry.Menu("파일/포토샵 브러시(.abr) 가져오기…", () => ImportAbr(), order: 60);
        Registry.Menu("파일/설정/펜·필압…", PenSettings.Show, order: 85);

        Registry.Menu("레이어/새 벡터 레이어 (선 굵기·지우기 나중에 수정)", NewVectorLayer, Keys.None, HasDoc, order: 12);
        Registry.Menu("레이어/벡터: 선 굵기 일괄 조정…", VectorWidth, Keys.None, IsVector, order: 13);
        Registry.Menu("레이어/벡터: 선 색을 지금 색으로", () => { var l = App.Doc?.Active; if (l?.Kind == "vector") VectorLayer.Recolor(l, Px.ToHex(App.Fg)); }, Keys.None, IsVector, order: 14);
        Registry.Menu("레이어/벡터를 일반 레이어로", VectorToRaster, Keys.None, IsVector, order: 15);

        Registry.Menu("보기/-대칭", null, order: 84);
        Registry.Menu("보기/대칭 그리기 (좌우)", CycleSymmetry, Keys.None, null, () => App.Get("symmetry", "none") != "none", order: 85);
        Registry.Menu("보기/방사 대칭 (만다라)…", RadialSymmetry, order: 86);
        Registry.Menu("보기/대칭 끄기", () => { App.Set("symmetry", "none"); App.NotifySettings(); App.Toast("대칭: 끔"); }, order: 87);
    }

    // ---- 대칭 ------------------------------------------------------------------------
    static void CycleSymmetry()
    {
        string s = App.Get("symmetry", "none");
        s = s == "none" ? "v" : s == "v" ? "h" : s == "h" ? "vh" : s == "vh" ? "radial" : "none";
        App.Set("symmetry", s); App.NotifySettings();
        App.Toast("대칭: " + (s switch { "none" => "끔", "v" => "좌우", "h" => "상하", "vh" => "상하좌우", _ => $"방사 {App.Get("symN", 6)}조각" }));
    }

    static void RadialSymmetry()
    {
        using var f = new Form { Text = "방사 대칭", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), ShowInTaskbar = false };
        UI.Dialogs.Modals.Adopt(f);
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        p.Controls.Add(new Label { Text = "조각 수", AutoSize = true });
        var n = new NumericUpDown { Minimum = 2, Maximum = 36, Value = Math.Clamp(App.Get("symN", 6), 2, 36), Width = 200, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
        var mirror = new CheckBox { Text = "거울 대칭도 함께", Checked = App.Get("symMirror", false), AutoSize = true };
        var ok = new Button { Text = "켜기", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        p.Controls.AddRange(new Control[] { n, mirror, Opt.Row(ok, cancel) });
        f.Controls.Add(p); f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(App.Main) != DialogResult.OK) return;
        App.Set("symmetry", "radial"); App.Set("symN", (int)n.Value); App.Set("symMirror", mirror.Checked);
        App.NotifySettings();
    }

    // ---- 벡터 레이어 -----------------------------------------------------------------
    static void NewVectorLayer()
    {
        var d = App.Doc; if (d == null) return;
        App.History.Struct(d, "새 레이어", () =>
        {
            var a = d.Active;
            var l = new Layer(d, "벡터 펜선", "vector") { Meta = new JsonObject { ["strokes"] = new JsonArray() }, Parent = a?.IsFolder == true && a.Expanded ? a.Id : a?.Parent };
            int i = a == null ? d.Layers.Count : d.Layers.IndexOf(a) + 1;
            d.Layers.Insert(i, l); d.ActiveId = l.Id;
        });
        App.NotifyLayers(); App.InvalidateAll();
        App.SetTool("brush");
    }

    static void VectorWidth()
    {
        var l = App.Doc?.Active;
        if (l?.Kind != "vector") { App.Toast("벡터 레이어를 고르세요"); return; }
        var v = Opt.AskNumbers("선 굵기 일괄 조정", ("배율 (%) — 100보다 작으면 가늘게, 크면 굵게. 이 레이어의 모든 선에 적용됩니다.", 80, 1, 1000));
        if (v != null) VectorLayer.ScaleWidths(l, v[0] / 100);
    }

    static void VectorToRaster()
    {
        var l = App.Doc?.Active;
        if (l?.Kind != "vector") return;
        App.History.Struct(App.Doc, "일반 레이어로", () => { l.Kind = "raster"; l.Meta = null; });
        App.NotifyLayers();
    }

    // ---- ABR · 프리셋 파일 ----------------------------------------------------------
    public static void ImportAbr(string file = null)
    {
        if (file == null)
        {
            using var dlg = new OpenFileDialog { Filter = "포토샵 브러시 (*.abr)|*.abr" };
            if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
            file = dlg.FileName;
        }
        try
        {
            int total = Abr.Read(File.ReadAllBytes(file)).Count;
            int n = Abr.Import(file);
            App.Toast($"브러시 {n}개를 가져왔습니다 (브러시 목록 맨 아래 \"{Path.GetFileNameWithoutExtension(file)}\")" + (total > 200 ? " · 200개까지만" : ""));
            App.NotifySettings();
        }
        catch (Exception ex) { App.Toast("ABR 를 읽지 못했습니다: " + ex.Message); }
    }

    /// <summary>웹 "내 세팅 내보내기" 와 같은 .cpset 형식 (브러시 부분만)</summary>
    public static JsonObject BuildSetFile(string cat, IEnumerable<BrushPreset> items)
    {
        var list = items.ToList();
        return new JsonObject
        {
            ["format"] = "comicpad-settings", ["version"] = 1, ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["parts"] = new JsonObject
            {
                ["brushes"] = new JsonObject
                {
                    ["brushes"] = new JsonObject { [cat] = new JsonArray(list.Select(p => (JsonNode)p.ToJson()).ToArray()) },
                    ["activePreset"] = new JsonObject { [cat] = list.FirstOrDefault()?.Id },
                },
            },
        };
    }

    /// <summary>.cpset (브러시 부분) 또는 프리셋 JSON(하나·배열) 가져오기: 같은 id 는 바꾸고 없으면 추가. 가져온 수</summary>
    public static int ImportSetFile(string text, string fallbackCat)
    {
        var root = JsonNode.Parse(text);
        int n = 0;
        void Merge(string cat, JsonArray arr)
        {
            if (!PresetStore.Cats.Contains(cat)) return;
            var cur = PresetStore.List(cat);
            foreach (var b in arr.OfType<JsonObject>())
            {
                var p = BrushPreset.FromJson(b);
                if (string.IsNullOrEmpty(p.Id)) p.Id = PresetStore.NewId();
                if (string.IsNullOrEmpty(p.Name)) p.Name = "가져온 브러시";
                int i = cur.FindIndex(x => x.Id == p.Id);
                if (i >= 0) cur[i] = p; else cur.Add(p);
                n++;
            }
            PresetStore.Save(cat);
        }
        if (root is JsonObject o && (string)o["format"] == "comicpad-settings")
        {
            if (o["parts"]?["brushes"] is JsonObject part)
            {
                if (part["brushes"] is JsonObject br) foreach (var (cat, v) in br) if (v is JsonArray arr) Merge(cat, arr);
                if (part["activePreset"] is JsonObject ap)
                    foreach (var (cat, v) in ap) if (v != null && PresetStore.List(cat).Any(x => x.Id == (string)v)) PresetStore.SetActive(cat, (string)v);
            }
        }
        else if (root is JsonArray arr) Merge(fallbackCat, arr);
        else if (root is JsonObject one && one.ContainsKey("name")) Merge(fallbackCat, new JsonArray(one.DeepClone()));
        return n;
    }
}

/// <summary>펜·필압 설정 (웹 ui/settings.js 의 "펜" 부분)</summary>
public static class PenSettings
{
    static JsonObject Feel()
    {
        if (App.Settings["penFeel"] is JsonObject f) return f;
        var n = new JsonObject { ["filter"] = "euro", ["pressureSmooth"] = 0, ["startRamp"] = false, ["predict"] = true };
        App.Settings["penFeel"] = n; return n;
    }
    static void Save() { App.NotifySettings(); }
    static double N(string key, double fb) => BrushPreset.Num(App.Settings[key]) ?? fb;

    public static void Show()
    {
        using var f = new Form { Text = "펜·필압 설정", StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, BackColor = Opt.Bg, ForeColor = Opt.Fg, ShowInTaskbar = false, Size = new Size(640, 760), Font = new Font("Malgun Gothic", 9f) };
        UI.Dialogs.Modals.Adopt(f);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Opt.Bg };
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Opt.Bg, Padding = new Padding(10) };
        var tester = new PressureTester();
        var s = App.Settings;
        p.Controls.Add(Opt.Title("펜 (S펜·와콤·휴이온·XP-Pen·서피스 펜·애플 펜슬)"));
        p.Controls.Add(new CurveEditor("필압 곡선 (모든 브러시)",
            // 예전 감마 값만 있으면 그 모양을 점으로 바꿔서 보여 줌
            () => Pressure.ParsePts(s["pressurePts"]) ?? ((N("pressureGamma", 1) is double gm && gm != 1 && gm > 0) ? new[] { 0, 0.25, 0.5, 0.75, 1 }.Select(x => new[] { x, Math.Round(Math.Pow(x, gm), 3) }).ToArray() : null),
            pts => { App.Settings["pressurePts"] = Pressure.ToJson(pts); App.Settings["pressureGamma"] = 1; tester.Invalidate(); Save(); }));
        p.Controls.Add(BUI.Slider("가장 약한 힘 (이보다 약하면 0)", 0, 0.5, () => BrushPreset.Num(Feel()["pmin"]) ?? 0, v => { Feel()["pmin"] = Math.Round(v, 2); tester.Invalidate(); Save(); }, BUI.Pct, step: 0.01));
        p.Controls.Add(BUI.Slider("가장 센 힘 (이만큼만 눌러도 최대)", 0.4, 1, () => BrushPreset.Num(Feel()["pmax"]) ?? 1, v => { Feel()["pmax"] = Math.Round(v, 2); tester.Invalidate(); Save(); }, BUI.Pct, step: 0.01));
        p.Controls.Add(tester);
        p.Controls.Add(BUI.Toggle("필압 없이 쓰기 (필압을 못 보내는 펜: 굵기 고정)", () => App.Get("penNoPressure", false), v => { App.Set("penNoPressure", v); Save(); }));
        p.Controls.Add(BUI.Help("와콤·휴이온·XP-Pen 등 PC 용 태블릿은 드라이버 설정에서 \"Windows Ink\" 를 켜야 필압·기울기가 옵니다. 펜 뒤쪽 지우개는 자동으로 지우개가 됩니다."));
        p.Controls.Add(BUI.Seg(new[] { ("euro", "자동 보정"), ("avg", "평균"), ("lazy", "끈 당기기"), ("spring", "무거운 펜") }, () => (string)Feel()["filter"] ?? "euro", v => { Feel()["filter"] = v; Save(); }));
        p.Controls.Add(BUI.Help("흔들림 보정 방식 (브러시 옵션에서 브러시마다 따로 정할 수도 있음). 끈 당기기 = 펜이 끈 길이만큼 멀어질 때만 선이 따라옴, 무거운 펜 = 살짝 늦게 부드럽게 따라옴."));
        p.Controls.Add(BUI.Toggle("선 끝에서 펜을 멈추고 있으면 직선으로", () => App.Get("holdLine", false), v => { App.Set("holdLine", v); Save(); }));
        p.Controls.Add(BUI.Slider("필압 다듬기", 0, 0.8, () => BrushPreset.Num(Feel()["pressureSmooth"]) ?? 0, v => { Feel()["pressureSmooth"] = Math.Round(v, 2); Save(); }, BUI.Pct, step: 0.05));
        p.Controls.Add(BUI.Toggle("펜을 대는 순간 굵게 찍히는 것 줄이기 (선 시작이 늘 가늘어짐)", () => Feel()["startRamp"] is JsonNode n && BrushPreset.Bool(n), v => { Feel()["startRamp"] = v; Save(); }));
        p.Controls.Add(BUI.Select("측면 버튼 누르고 그리기", new[] { ("eyedropper", "스포이트"), ("eraser", "지우개"), ("pan", "화면 이동"), ("none", "사용 안 함") }, () => App.Get("penButton", "eyedropper"), v => { App.Set("penButton", v); Save(); }));
        p.Controls.Add(BUI.Select("두 번째 측면 버튼 (와콤 등)", new[] { ("pan", "화면 이동"), ("eyedropper", "스포이트"), ("eraser", "지우개"), ("radial", "원형 메뉴"), ("none", "사용 안 함") }, () => App.Get("penButton2", "pan"), v => { App.Set("penButton2", v); Save(); }));
        p.Controls.Add(BUI.Toggle("펜 위치 예측 (선보다 조금 앞을 흐리게 미리 보여 줌)", () => Feel()["predict"] is not JsonNode n || BrushPreset.Bool(n), v => { Feel()["predict"] = v; Save(); }));
        p.Controls.Add(BUI.Slider("펜을 뗀 뒤 터치 무시 시간", 0, 1500, () => N("palmGuardMs", 350), v => { App.Set("palmGuardMs", (int)v); Save(); }, v => $"{v:0}ms", step: 50));
        p.Controls.Add(BUI.Slider("마우스 필압", 0.05, 1, () => N("mousePressure", 1), v => { App.Set("mousePressure", Math.Round(v, 2)); Save(); }, BUI.Pct, step: 0.05));
        scroll.Controls.Add(p);
        f.Controls.Add(scroll);
        var close = new Button { Text = "닫기", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 32 };
        f.Controls.Add(close); f.AcceptButton = close;
        f.ShowDialog(App.Main);
        App.SaveSettings();
    }

    /// <summary>필압 시험 칸: 그리면 굵기 1+14×곡선(필압), 오른쪽에 지금 곡선</summary>
    sealed class PressureTester : FlowLayoutPanel
    {
        readonly PenInputControl pad = new() { Width = 420, Height = 140, BackColor = Color.FromArgb(30, 30, 34), Margin = new Padding(0, 0, 6, 0) };
        readonly Control curve = new Panel { Width = 140, Height = 140, BackColor = Color.FromArgb(30, 30, 34) };
        readonly Label info = BUI.Help("여기에 펜으로 그려 보세요");
        Bitmap ink = new(420, 140);
        PointF? last;

        public PressureTester()
        {
            FlowDirection = FlowDirection.TopDown; AutoSize = true; WrapContents = false; BackColor = Opt.Bg;
            var row = Opt.Row(pad, curve);
            Controls.Add(row); Controls.Add(info);
            pad.Paint += (_, e) => e.Graphics.DrawImageUnscaled(ink, 0, 0);
            pad.PenDown += (x, y, pr, pen) => { using (var g = Graphics.FromImage(ink)) g.Clear(Color.Transparent); last = null; pad.Invalidate(); };
            pad.PenMove += (x, y, pr, pen) =>
            {
                double p = pen ? pr : 1, c = Pressure.Curve(p);
                if (last is PointF l)
                {
                    using var g = Graphics.FromImage(ink); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using var pn = new Pen(Color.FromArgb(221, 221, 221), (float)(1 + 14 * c)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                    g.DrawLine(pn, l, new PointF((float)x, (float)y));
                }
                last = new PointF((float)x, (float)y);
                info.Text = $"{(pen ? "pen" : "mouse")} · 필압 {p:0.00} → {c:0.00}";
                pad.Invalidate();
            };
            curve.Paint += (_, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var bp = new Pen(Color.FromArgb(85, 85, 85)); g.DrawRectangle(bp, 0, 0, 139, 139);
                var feel = PenFeel.Current();
                var ptsL = Enumerable.Range(0, 51).Select(i => { double p = i / 50.0; return new PointF((float)(p * 140), (float)(140 - feel.Curve(p) * 140)); }).ToArray();
                using var cp = new Pen(Opt.Accent, 2); g.DrawLines(cp, ptsL);
            };
        }
        public new void Invalidate() { curve.Invalidate(); base.Invalidate(); }
    }
}
