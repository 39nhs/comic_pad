// 보정 대화상자 (미리보기) · 조정 레이어 · 레이어 효과 · 톤화 · 용지 질감 — 웹 ui/adjust.js, ui/viewaids.js 와 같은 구성
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Filters;

/// <summary>그림 레이어 미리보기: 원본 면을 잠시 보정한 사본으로 바꿔 보여 줌 (닫으면 되돌림)</summary>
public sealed class SurfacePreview
{
    readonly Layer l; readonly bool mask; readonly Surface orig;
    public bool Showing { get; private set; }
    public SurfacePreview(Layer layer)
    {
        l = layer; mask = layer.EditMask && layer.Mask != null;
        orig = mask ? layer.Mask : layer.Px;
    }
    public bool Valid => orig != null;
    public void Show(FilterChain f)
    {
        if (orig == null) return;
        var s = orig.Clone();
        for (int ti = 0; ti < s.TileCount; ti++) { var t = s.TileAt(ti); if (t != null) f.Apply(t); }
        Set(s); Showing = true;
    }
    public void Restore() { if (orig == null || !Showing) return; Set(orig); Showing = false; }
    void Set(Surface s) { if (mask) l.Mask = s; else l.Px = s; App.InvalidateAll(); }
}

public static class AdjustDialogs
{
    static Document D => App.Doc;

    // ---------------------------------------------------------------- 히스토그램
    public static int[] Histogram(Layer layer)
    {
        var d = D; var hist = new int[256];
        Surface s;
        if (layer != null && !layer.IsFolder && !layer.IsAdjust && layer.Px != null) s = layer.Px;
        else s = App.Comp.Flatten(d, new RenderOptions { Paper = true });
        int step = Math.Max(1, (int)Math.Ceiling(Math.Max(d.W, d.H) / 512.0));
        for (int y = 0; y < s.H; y += step)
            for (int x = 0; x < s.W; x += step)
            {
                uint c = s.Get(x, y);
                Raster.Unpack(c, out int r, out int g, out int b, out int a);
                if (a > 8) hist[(int)Math.Round(0.299 * r + 0.587 * g + 0.114 * b, MidpointRounding.AwayFromZero)]++;
            }
        return hist;
    }

    static void DrawHist(Graphics g, int W, int H, int[] hist)
    {
        g.Clear(Color.FromArgb(30, 30, 34));
        int max = 1; for (int i = 2; i < 254; i++) max = Math.Max(max, hist[i]);
        using var br = new SolidBrush(Color.FromArgb(107, 107, 120));
        for (int i = 0; i < 256; i++) { float v = Math.Min(1f, hist[i] / (float)max) * H; g.FillRectangle(br, i * W / 256f, H - v, W / 256f + 0.5f, v); }
    }

    sealed class Canvas : Panel { public Canvas() { DoubleBuffered = true; ResizeRedraw = true; } }

    // ---------------------------------------------------------------- 톤 커브 편집기
    static Control CurveEditor(JsonObject p, Action onChange)
    {
        string ch = "master";
        var hist = Histogram(App.Active);
        var col = Ui.Column();
        var cv = new Canvas { Width = 300, Height = 300, Margin = new Padding(0, 4, 0, 4), Cursor = Cursors.Cross };
        List<double[]> Pts(string k)
        {
            var list = new List<double[]>();
            if (p[k] is JsonArray a) foreach (var n in a) if (n is JsonArray q && q.Count >= 2) list.Add(new[] { Svg.Num(q[0]), Svg.Num(q[1]) });
            if (list.Count < 2) list = new() { new double[] { 0, 0 }, new double[] { 255, 255 } };
            return list;
        }
        void Save(string k, List<double[]> pts) { var a = new JsonArray(); foreach (var q in pts) a.Add(new JsonArray(Math.Round(q[0], 3), Math.Round(q[1], 3))); p[k] = a; }
        var colors = new Dictionary<string, Color> { ["master"] = Color.White, ["r"] = Color.FromArgb(255, 92, 92), ["g"] = Color.FromArgb(92, 214, 92), ["b"] = Color.FromArgb(92, 156, 255) };
        cv.Paint += (_, e) =>
        {
            var g = e.Graphics; int W = cv.Width, H = cv.Height;
            DrawHist(g, W, H, hist);
            using (var grid = new Pen(Color.FromArgb(58, 58, 66))) for (int i = 1; i < 4; i++) { g.DrawLine(grid, i * W / 4, 0, i * W / 4, H); g.DrawLine(grid, 0, i * H / 4, W, i * H / 4); }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var k in new[] { "r", "g", "b", "master" })
            {
                var pts = Pts(k);
                if (k != ch && k != "master" && pts.Count == 2 && pts[0][1] == 0 && pts[1][1] == 255) continue;
                var f = Svg.CurveFn(p[k] as JsonArray);
                using var pen = new Pen(Color.FromArgb(k == ch ? 255 : 90, colors[k]), k == ch ? 2.5f : 1.5f);
                var line = new PointF[129];
                for (int i = 0; i <= 128; i++) { double t = i / 128.0; line[i] = new PointF((float)(t * W), (float)((1 - f(t)) * H)); }
                g.DrawLines(pen, line);
            }
            using var dot = new SolidBrush(Opt.Accent);
            foreach (var q in Pts(ch)) g.FillEllipse(dot, (float)(q[0] / 255 * W) - 5, (float)((1 - q[1] / 255) * H) - 5, 10, 10);
        };
        int drag = -1;
        double[] ToPt(Point m) => new[] { Math.Clamp(m.X / (double)cv.Width * 255, 0, 255), Math.Clamp((1 - m.Y / (double)cv.Height) * 255, 0, 255) };
        cv.MouseDown += (_, e) =>
        {
            var pt = ToPt(e.Location); var pts = Pts(ch);
            double tol = 10 * 255.0 / cv.Width * 1.7;
            drag = pts.FindIndex(q => Math.Sqrt((q[0] - pt[0]) * (q[0] - pt[0]) + (q[1] - pt[1]) * (q[1] - pt[1])) < tol);
            if (drag < 0) { pts.Add(pt); pts.Sort((a, b) => a[0].CompareTo(b[0])); drag = pts.IndexOf(pt); }
            Save(ch, pts); cv.Invalidate(); onChange();
        };
        cv.MouseMove += (_, e) =>
        {
            if (drag < 0 || e.Button != MouseButtons.Left) return;
            var pts = Pts(ch); if (drag >= pts.Count) { drag = -1; return; }
            var pt = ToPt(e.Location);
            bool outside = e.Y < -30 || e.Y > cv.Height + 30;
            if (outside && pts.Count > 2 && drag > 0 && drag < pts.Count - 1) { pts.RemoveAt(drag); drag = -1; }
            else
            {
                double lo = drag > 0 ? pts[drag - 1][0] + 1 : 0, hi = drag < pts.Count - 1 ? pts[drag + 1][0] - 1 : 255;
                double x = drag == 0 ? Math.Min(pt[0], hi) : drag == pts.Count - 1 ? Math.Max(pt[0], lo) : Math.Clamp(pt[0], lo, Math.Max(lo, hi));
                pts[drag] = new[] { x, pt[1] };
            }
            Save(ch, pts); cv.Invalidate(); onChange();
        };
        cv.MouseUp += (_, _) => drag = -1;
        cv.MouseDoubleClick += (_, e) =>
        {
            var pt = ToPt(e.Location); var pts = Pts(ch);
            double tol = 14 * 255.0 / cv.Width * 1.7;
            int i = pts.FindIndex(q => Math.Sqrt((q[0] - pt[0]) * (q[0] - pt[0]) + (q[1] - pt[1]) * (q[1] - pt[1])) < tol);
            if (i > 0 && i < pts.Count - 1) { pts.RemoveAt(i); Save(ch, pts); cv.Invalidate(); onChange(); }
        };
        var seg = Ui.RowP();
        var segBtns = new List<Button>();
        foreach (var (id, text) in new[] { ("master", "RGB"), ("r", "빨강"), ("g", "초록"), ("b", "파랑") })
        {
            var key = id;
            var b = Ui.Btn(text, null);
            b.Click += (_, _) => { ch = key; foreach (var x in segBtns) x.BackColor = (string)x.Tag == ch ? Opt.Accent : Opt.Bg2; foreach (var x in segBtns) x.ForeColor = (string)x.Tag == ch ? Color.Black : Opt.Fg; cv.Invalidate(); };
            b.Tag = id; segBtns.Add(b); seg.Controls.Add(b);
        }
        segBtns[0].BackColor = Opt.Accent; segBtns[0].ForeColor = Color.Black;
        var row = Ui.RowP();
        row.Controls.Add(Ui.Btn("초기화", () => { Save(ch, new() { new double[] { 0, 0 }, new double[] { 255, 255 } }); cv.Invalidate(); onChange(); }));
        row.Controls.Add(Ui.Btn("S자 (대비↑)", () => { Save(ch, new() { new double[] { 0, 0 }, new double[] { 64, 48 }, new double[] { 192, 208 }, new double[] { 255, 255 } }); cv.Invalidate(); onChange(); }));
        row.Controls.Add(Ui.Btn("밝게", () => { Save(ch, new() { new double[] { 0, 0 }, new double[] { 128, 168 }, new double[] { 255, 255 } }); cv.Invalidate(); onChange(); }));
        col.Controls.Add(seg); col.Controls.Add(cv); col.Controls.Add(row);
        col.Controls.Add(Ui.Lbl("탭: 점 추가 · 끌기: 이동 · 위아래 밖으로 끌거나 더블탭: 점 삭제", true));
        return col;
    }

    // ---------------------------------------------------------------- 그라데이션 맵 편집기
    static readonly (string name, (double, string)[] stops)[] GradPresets =
    {
        ("노을", new[] { (0.0, "#1b1430"), (0.5, "#c2577a"), (1.0, "#ffe9c4") }),
        ("세피아", new[] { (0.0, "#2b1a0e"), (0.6, "#a87a4f"), (1.0, "#fbf0dc") }),
        ("흑백", new[] { (0.0, "#000000"), (1.0, "#ffffff") }),
        ("푸른 밤", new[] { (0.0, "#05081a"), (0.55, "#3b5ba5"), (1.0, "#d9f2ff") }),
        ("파스텔", new[] { (0.0, "#6c5b7b"), (0.5, "#f8b195"), (1.0, "#fff6e0") }),
        ("청록·주황", new[] { (0.0, "#0c2f3a"), (0.5, "#5d8a8c"), (1.0, "#ffc58a") }),
    };

    static Color ToColor(string hex) { uint c = Px.Unpremul(Px.FromHex(hex)); return Color.FromArgb((int)((c >> 16) & 255), (int)((c >> 8) & 255), (int)(c & 255)); }

    static Control GradmapEditor(JsonObject p, Action onChange)
    {
        var box = Ui.Column();
        var bar = new Canvas { Width = 360, Height = 24, Margin = new Padding(0, 4, 0, 4) };
        bar.Paint += (_, e) =>
        {
            var stops = Svg.Stops(p).OrderBy(s => s.pos).ToList();
            if (stops.Count == 1) { e.Graphics.Clear(ToColor(stops[0].color)); return; }
            using var br = new LinearGradientBrush(new Rectangle(0, 0, bar.Width, bar.Height), Color.Black, Color.White, 0f);
            var cb = new ColorBlend(stops.Count + 2);
            var pos = new List<float> { 0 }; var cols = new List<Color> { ToColor(stops[0].color) };
            foreach (var s in stops) { pos.Add((float)Math.Clamp(s.pos, 0, 1)); cols.Add(ToColor(s.color)); }
            pos.Add(1); cols.Add(ToColor(stops[^1].color));
            for (int i = 1; i < pos.Count; i++) if (pos[i] < pos[i - 1]) pos[i] = pos[i - 1];
            cb.Positions = pos.ToArray(); cb.Colors = cols.ToArray();
            br.InterpolationColors = cb;
            e.Graphics.FillRectangle(br, 0, 0, bar.Width, bar.Height);
        };
        void Render()
        {
            box.SuspendLayout();
            foreach (Control c in box.Controls.Cast<Control>().ToList()) { box.Controls.Remove(c); if (c != bar) c.Dispose(); }
            box.Controls.Add(bar);
            var arr = p["stops"] as JsonArray ?? new JsonArray();
            p["stops"] = arr;
            var sorted = arr.OfType<JsonObject>().OrderBy(o => Svg.Num(o, "pos")).ToList();
            arr.Clear(); foreach (var s in sorted) arr.Add(s);
            foreach (var s in sorted)
            {
                var stop = s;
                var row = Ui.RowP();
                row.Controls.Add(Ui.ColorBtn("", () => (string)stop["color"] ?? "#000000", v => { stop["color"] = v; bar.Invalidate(); onChange(); }));
                var sl = Ui.Slider("위치", 0, 1, 0.01, () => Svg.Num(stop, "pos"), v => { stop["pos"] = Math.Round(v, 3); bar.Invalidate(); onChange(); }, fmt: v => Math.Round(v * 100) + "%");
                sl.Width = 180; foreach (Control c in sl.Controls) if (c is TrackBar tb) tb.Width = 180; else if (c is Label lb && lb.TextAlign == ContentAlignment.TopRight) { lb.Left = 90; lb.Width = 88; }
                row.Controls.Add(sl);
                if (sorted.Count > 2) row.Controls.Add(Ui.Btn("×", () => { arr.Remove(stop); Render(); onChange(); }));
                box.Controls.Add(row);
            }
            var r2 = Ui.RowP();
            r2.Controls.Add(Ui.Btn("+ 색 추가", () => { arr.Add(AdjustTypes.Stop(0.5, Px.ToHex(App.Fg))); Render(); onChange(); }));
            var presets = new[] { ("", "(프리셋 선택)") }.Concat(GradPresets.Select(g => (g.name, g.name))).ToArray();
            r2.Controls.Add(Ui.Combo(presets, () => "", v =>
            {
                if (string.IsNullOrEmpty(v)) return;
                var pr = GradPresets.First(g => g.name == v);
                var na = new JsonArray(); foreach (var (pos, c) in pr.stops) na.Add(AdjustTypes.Stop(pos, c));
                p["stops"] = na;
                box.BeginInvoke(new Action(() => { Render(); onChange(); }));
            }, 160));
            box.Controls.Add(r2);
            box.ResumeLayout();
            bar.Invalidate();
        }
        Render();
        return box;
    }

    // ---------------------------------------------------------------- 종류별 조절
    static List<Control> ControlsFor(string type, JsonObject p, Action onChange)
    {
        Control S(string label, string key, double min, double max, double step = 1, Func<double, string> fmt = null)
            => Ui.Slider(label, min, max, step, () => Svg.Num(p, key), v => { p[key] = v; onChange(); }, fmt: fmt);
        switch (type)
        {
            case "levels":
            {
                var hist = Histogram(App.Active);
                var cv = new Canvas { Width = 360, Height = 90, Margin = new Padding(0, 4, 0, 4) };
                cv.Paint += (_, e) =>
                {
                    DrawHist(e.Graphics, cv.Width, cv.Height, hist);
                    using var acc = new SolidBrush(Opt.Accent);
                    foreach (var v in new[] { Svg.Num(p, "inB"), Svg.Num(p, "inW") }) e.Graphics.FillRectangle(acc, (float)(v / 255 * cv.Width) - 1, 0, 3, cv.Height);
                    double mid = Svg.Num(p, "inB") + (Svg.Num(p, "inW") - Svg.Num(p, "inB")) * Math.Pow(0.5, Svg.Num(p, "gamma"));
                    using var gr = new SolidBrush(Color.FromArgb(204, 204, 204));
                    e.Graphics.FillRectangle(gr, (float)(mid / 255 * cv.Width) - 1, 0, 2, cv.Height);
                };
                void Ch() { cv.Invalidate(); onChange(); }
                var list = new List<Control> { cv };
                list.Add(Ui.Slider("입력 검정", 0, 254, 1, () => Svg.Num(p, "inB"), v => { p["inB"] = Math.Min(v, Svg.Num(p, "inW") - 1); Ch(); }));
                list.Add(Ui.Slider("감마 (중간톤)", 0.1, 5, 0.01, () => Svg.Num(p, "gamma"), v => { p["gamma"] = v; Ch(); }, log: true, fmt: v => v.ToString("0.00")));
                list.Add(Ui.Slider("입력 흰색", 1, 255, 1, () => Svg.Num(p, "inW"), v => { p["inW"] = Math.Max(v, Svg.Num(p, "inB") + 1); Ch(); }));
                list.Add(Ui.Slider("출력 검정", 0, 255, 1, () => Svg.Num(p, "outB"), v => { p["outB"] = v; Ch(); }));
                list.Add(Ui.Slider("출력 흰색", 0, 255, 1, () => Svg.Num(p, "outW"), v => { p["outW"] = v; Ch(); }));
                Button auto = null;
                auto = Ui.Btn("자동", () =>
                {
                    long tot = hist.Sum(x => (long)x), acc = 0; int lo = 0, hi = 255;
                    for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc > tot * 0.005) { lo = i; break; } }
                    acc = 0;
                    for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc > tot * 0.005) { hi = i; break; } }
                    p["inB"] = lo; p["inW"] = Math.Max(lo + 1, hi); Ch();
                    var root = auto.FindForm(); if (root != null) Ui.SyncAll(root);
                });
                list.Add(auto);
                return list;
            }
            case "curves": return new() { CurveEditor(p, onChange) };
            case "hsl": return new() { S("색조", "hue", -180, 180, 1, v => v + "°"), S("채도", "sat", -100, 100), S("명도", "light", -100, 100) };
            case "balance":
                return new()
                {
                    Ui.Section("어두운 부분"), S("시안 ↔ 빨강", "sr", -100, 100), S("마젠타 ↔ 초록", "sg", -100, 100), S("노랑 ↔ 파랑", "sb", -100, 100),
                    Ui.Section("중간 톤"), S("시안 ↔ 빨강", "mr", -100, 100), S("마젠타 ↔ 초록", "mg", -100, 100), S("노랑 ↔ 파랑", "mb", -100, 100),
                    Ui.Section("밝은 부분"), S("시안 ↔ 빨강", "hr", -100, 100), S("마젠타 ↔ 초록", "hg", -100, 100), S("노랑 ↔ 파랑", "hb", -100, 100),
                };
            case "bc": return new() { S("밝기", "b", -100, 100), S("대비", "c", -100, 100) };
            case "gradmap": return new() { GradmapEditor(p, onChange) };
            case "posterize": return new() { S("단계 수", "levels", 2, 32) };
            case "threshold": return new() { S("경계값", "t", 1, 255) };
            case "bw": return new() { S("빨강", "r", 0, 100), S("초록", "g", 0, 100), S("파랑", "b", 0, 100) };
            case "colorize": return new() { Ui.ColorBtn("색", () => (string)p["color"] ?? "#c08040", v => { p["color"] = v; onChange(); }), S("세기", "amount", 0, 100, 1, v => v + "%") };
            case "vibrance": return new() { S("채도 강화", "v", -100, 100) };
            default: return new() { Ui.Lbl("설정할 값이 없습니다.", true) };
        }
    }

    /// <summary>보정 대화상자. editLayer 가 조정 레이어면 그 값을 편집, 아니면 지금 레이어에 적용(또는 조정 레이어로 만들기)</summary>
    public static void AdjustDialog(string type, Layer editLayer = null)
    {
        var d = D; if (d == null) return;
        string name = AdjustTypes.Name(type);
        var p = AdjustTypes.Merge(type, editLayer?.Meta?["params"] as JsonObject);
        var target = editLayer ?? d.Active;
        bool pixelTarget = editLayer == null && target != null && !target.IsFolder && !target.IsAdjust && target.Target != null;
        if (editLayer == null && !pixelTarget) App.Toast("보정할 그림 레이어를 고르거나, \"조정 레이어\"로 만드세요");
        var prev = pixelTarget ? new SurfacePreview(target) : null;
        bool previewOn = true;
        var timer = new System.Windows.Forms.Timer { Interval = 30 };
        void Update()
        {
            if (editLayer != null)
            {
                LayerEffects.PreviewLayer = previewOn ? editLayer : null;
                LayerEffects.PreviewChain = previewOn ? Svg.Adjust(type, p) : null;
                App.InvalidateAll();
            }
            else if (prev != null)
            {
                if (previewOn) prev.Show(Svg.Adjust(type, p)); else prev.Restore();
            }
        }
        timer.Tick += (_, _) => { timer.Stop(); Update(); };
        void Changed() { timer.Stop(); timer.Start(); }
        void Close() { timer.Stop(); LayerEffects.PreviewLayer = null; LayerEffects.PreviewChain = null; prev?.Restore(); App.InvalidateAll(); }

        using var f = Ui.Dialog(name + (editLayer != null ? " (조정 레이어)" : ""));
        var col = Ui.Column();
        foreach (var c in ControlsFor(type, p, Changed)) col.Controls.Add(c);
        col.Controls.Add(Ui.Check("미리보기", () => previewOn, v => { previewOn = v; Changed(); }));
        var row = Ui.RowP();
        string result = null;
        Button B(string t, string r, bool primary = false) { var b = Ui.Btn(t, () => { result = r; f.Close(); }, primary); return b; }
        if (editLayer != null) { row.Controls.Add(B("취소", "cancel")); row.Controls.Add(B("확인", "ok", true)); }
        else { row.Controls.Add(B("취소", "cancel")); row.Controls.Add(B("조정 레이어로", "layer")); row.Controls.Add(B("적용", "apply", true)); }
        col.Controls.Add(row);
        f.Controls.Add(col);
        f.Shown += (_, _) => Update();
        f.ShowDialog(App.Main);
        Close();
        timer.Dispose();
        if (result == "ok" && editLayer != null)
        {
            var meta = (JsonObject)editLayer.Meta?.DeepClone() ?? new JsonObject { ["type"] = type };
            meta["params"] = p.DeepClone();
            App.History.Struct(d, name + " 수정", () => editLayer.Meta = meta);
            App.NotifyLayers(); App.InvalidateAll();
        }
        else if (result == "layer") AddAdjustLayer(type, p);
        else if (result == "apply")
        {
            var chain = Svg.Adjust(type, p);
            FilterOps.CanvasOp(name, (src, w, h) => { var o = (uint[])src.Clone(); chain.Apply(o); return o; });
        }
    }

    public static Layer AddAdjustLayer(string type, JsonObject p = null)
    {
        var d = D; if (d == null) return null;
        Layer l = null;
        App.History.Struct(d, "레이어 추가", () =>
        {
            var a = d.Active;
            l = new Layer(d, AdjustTypes.Name(type), "adjust") { Parent = a?.IsFolder == true && a.Expanded ? a.Id : a?.Parent };
            l.Meta = new JsonObject { ["type"] = type, ["params"] = (p ?? AdjustTypes.Default(type)).DeepClone() };
            int i = a == null ? d.Layers.Count : d.Layers.IndexOf(a) + 1;
            d.Layers.Insert(i, l); d.ActiveId = l.Id;
        });
        App.NotifyLayers(); App.InvalidateAll();
        App.Toast("조정 레이어: 더블탭으로 값 수정, 클리핑하면 바로 아래 레이어에만 적용");
        return l;
    }

    // ---------------------------------------------------------------- 레이어 효과
    public static void FxDialog()
    {
        var d = D; var layer = d?.Active;
        if (layer == null || layer.IsAdjust) { App.Toast("그림/폴더 레이어를 고르세요"); return; }
        var before = (JsonObject)layer.Fx?.DeepClone();
        var fx = (JsonObject)before?.DeepClone() ?? new JsonObject
        {
            ["stroke"] = new JsonObject { ["on"] = false, ["size"] = 6, ["color"] = "#ffffff" },
            ["shadow"] = new JsonObject { ["on"] = false, ["dx"] = 8, ["dy"] = 8, ["blur"] = 10, ["color"] = "#000000", ["opacity"] = 0.5 },
            ["glow"] = new JsonObject { ["on"] = false, ["size"] = 16, ["color"] = "#ffe680", ["opacity"] = 0.8 },
        };
        foreach (var k in new[] { "stroke", "shadow", "glow" }) if (fx[k] is not JsonObject) fx[k] = new JsonObject();
        layer.Fx = fx;
        void Upd() { layer.Touch(); App.InvalidateAll(); }
        JsonObject O(string k) => (JsonObject)fx[k];
        Control S(string k, string label, string key, double min, double max, double step = 1, Func<double, string> fmt = null)
            => Ui.Slider(label, min, max, step, () => Svg.Num(O(k), key), v => { O(k)[key] = v; Upd(); }, fmt: fmt);
        Control On(string k) => Ui.Check("켜기", () => (bool?)O(k)["on"] == true, v => { O(k)["on"] = v; Upd(); });
        Control Col(string k) => Ui.ColorBtn("색", () => (string)O(k)["color"] ?? "#000000", v => { O(k)["color"] = v; Upd(); });
        Func<double, string> pct = v => Math.Round(v * 100) + "%";

        using var f = Ui.Dialog("레이어 효과: " + layer.Name);
        var col = Ui.Column();
        col.Controls.Add(Ui.Section("테두리 (경계 효과)")); col.Controls.Add(On("stroke")); col.Controls.Add(S("stroke", "두께", "size", 1, 60)); col.Controls.Add(Col("stroke"));
        col.Controls.Add(Ui.Section("그림자")); col.Controls.Add(On("shadow")); col.Controls.Add(S("shadow", "가로", "dx", -100, 100)); col.Controls.Add(S("shadow", "세로", "dy", -100, 100));
        col.Controls.Add(S("shadow", "흐림", "blur", 0, 100)); col.Controls.Add(S("shadow", "진하기", "opacity", 0, 1, 0.01, pct)); col.Controls.Add(Col("shadow"));
        col.Controls.Add(Ui.Section("외부 광선")); col.Controls.Add(On("glow")); col.Controls.Add(S("glow", "크기", "size", 1, 120)); col.Controls.Add(S("glow", "진하기", "opacity", 0, 1, 0.01, pct)); col.Controls.Add(Col("glow"));
        col.Controls.Add(Ui.Lbl("그림을 바꾸지 않는 효과입니다. 나중에 그림을 고쳐도 효과가 따라옵니다 (스티커 테두리, 글자 외곽선, 발광 등).", true));
        bool ok = false;
        var row = Ui.RowP();
        row.Controls.Add(Ui.Btn("취소", () => f.Close()));
        row.Controls.Add(Ui.Btn("확인", () => { ok = true; f.Close(); }, true));
        col.Controls.Add(row);
        var scroll = new Panel { AutoScroll = true, Width = 400, Height = Math.Min(760, Ui.WorkArea().Height - 120), BackColor = Opt.Bg };
        scroll.Controls.Add(col);
        f.Controls.Add(scroll);
        Upd();
        f.ShowDialog(App.Main);
        var after = (JsonObject)layer.Fx.DeepClone();
        layer.Fx = before;
        if (ok)
        {
            bool any = new[] { "stroke", "shadow", "glow" }.Any(k => (bool?)(after[k] as JsonObject)?["on"] == true);
            App.History.Struct(d, "레이어 효과", () => layer.Fx = any ? after : null);
        }
        Upd(); App.NotifyLayers();
    }

    // ---------------------------------------------------------------- 톤화
    public static void ToneDialog()
    {
        var d = D; var layer = d?.Active;
        if (layer == null || layer.IsFolder || layer.IsAdjust) { App.Toast("그림 레이어를 고르세요"); return; }
        var before = (JsonObject)layer.Tone?.DeepClone();
        var t = new JsonObject { ["spacing"] = 8, ["angle"] = 45, ["shape"] = "dot", ["color"] = "#000000" };
        if (before != null) foreach (var (k, v) in before) t[k] = v?.DeepClone();
        var timer = new System.Windows.Forms.Timer { Interval = 120 };
        void Apply() { layer.Tone = (JsonObject)t.DeepClone(); App.InvalidateAll(); }
        timer.Tick += (_, _) => { timer.Stop(); Apply(); };
        void Upd() { timer.Stop(); timer.Start(); }
        Control S(string label, string key, double min, double max, double step = 1, Func<double, string> fmt = null)
            => Ui.Slider(label, min, max, step, () => Svg.Num(t, key), v => { t[key] = v; Upd(); }, fmt: fmt);

        using var f = Ui.Dialog("톤화: " + layer.Name);
        var col = Ui.Column();
        col.Controls.Add(Ui.Combo(new[] { ("dot", "점 망점"), ("line", "선 망점") }, () => (string)t["shape"] ?? "dot", v => { t["shape"] = v; Upd(); }));
        col.Controls.Add(S("망점 간격 (px)", "spacing", 3, 40, 0.5));
        col.Controls.Add(S("각도", "angle", 0, 90, 1, v => v + "°"));
        col.Controls.Add(Ui.ColorBtn("망점 색", () => (string)t["color"] ?? "#000000", v => { t["color"] = v; Upd(); }));
        col.Controls.Add(Ui.Lbl($"현재 간격 ≈ {Math.Round((d.Dpi > 0 ? d.Dpi : 350) / 8.0)}선 (350dpi 기준). 회색으로 칠하면 진하기에 맞춰 점이 커집니다. 그리는 동안은 회색으로 보이고, 펜을 떼면 망점으로 바뀝니다.", true));
        string result = null;
        var row = Ui.RowP();
        if (before != null) row.Controls.Add(Ui.Btn("톤화 끄기", () => { result = "off"; f.Close(); }));
        row.Controls.Add(Ui.Btn("취소", () => { result = "cancel"; f.Close(); }));
        row.Controls.Add(Ui.Btn("확인", () => { result = "ok"; f.Close(); }, true));
        col.Controls.Add(row);
        f.Controls.Add(col);
        Apply();
        f.ShowDialog(App.Main);
        timer.Stop(); timer.Dispose();
        layer.Tone = before;
        if (result == "ok") { var after = (JsonObject)t.DeepClone(); App.History.Struct(d, "톤화", () => layer.Tone = after); }
        else if (result == "off") App.History.Struct(d, "톤화 끄기", () => layer.Tone = null);
        App.InvalidateAll(); App.NotifyLayers();
    }

    // ---------------------------------------------------------------- 용지
    /// <summary>용지 설정(색·보이기·질감) 실행취소 기록</summary>
    sealed class PaperEdit : IEdit
    {
        readonly (string color, bool vis, JsonObject extra) a, b;
        public string Label { get; }
        public PaperEdit(string label, (string, bool, JsonObject) before, (string, bool, JsonObject) after) { Label = label; a = before; b = after; }
        public long Bytes => 512;
        public Rect Undo(Document d) { Put(d, a); return new Rect(0, 0, d.W, d.H); }
        public Rect Redo(Document d) { Put(d, b); return new Rect(0, 0, d.W, d.H); }
        static void Put(Document d, (string color, bool vis, JsonObject extra) s) { d.PaperColor = s.color; d.PaperVisible = s.vis; d.PaperExtra = (JsonObject)s.extra.DeepClone(); }
    }
    static (string, bool, JsonObject) PaperState(Document d) => (d.PaperColor, d.PaperVisible, (JsonObject)d.PaperExtra.DeepClone());

    public static void PaperTextureDialog()
    {
        var d = D; if (d == null) return;
        var t = Paper.Texture(d);
        var before = PaperState(d);
        void Apply(Values v)
        {
            d.PaperExtra["texture"] = new JsonObject
            {
                ["type"] = v.S("type"), ["strength"] = Math.Clamp(v.N("strength") / 100, 0, 1), ["scale"] = Math.Clamp(v.N("scale") > 0 ? v.N("scale") : 1, 0.25, 4), ["over"] = v.B("over"),
            };
            d.PaperVisible = true;
            App.InvalidateAll();
        }
        var vals = Ui.Ask("용지 질감", new[]
        {
            Field.Pick("type", "종류", Paper.Types, (string)t?["type"] ?? "none"),
            Field.N("strength", "진하기 (%)", Math.Round(Svg.Num(t, "strength", 0.6) * 100), 0, 100),
            Field.N("scale", "결 크기 (배)", Svg.Num(t, "scale", 1) is var sc && sc > 0 ? sc : 1, 0.25, 4, 0.25),
            Field.C("over", "칠한 그림 위에도 결 보이기 (수채·연필 느낌)", (bool?)t?["over"] == true),
            Field.Info("용지에 곱하기로 얹혀서 그림 전체가 종이에 그린 듯 보입니다. 용지를 켠 채 내보내면 이미지에도 들어갑니다 (투명 PNG 로 내보내면 빠짐)."),
        }, "적용", Apply);
        if (vals == null) { d.PaperColor = before.Item1; d.PaperVisible = before.Item2; d.PaperExtra = before.Item3; App.InvalidateAll(); return; }
        Apply(vals);
        App.History.Push(new PaperEdit("용지 질감", before, PaperState(d)));
        d.Modified = true;
        App.NotifyLayers();
    }

    public static void PaperColorDialog()
    {
        var d = D; if (d == null) return;
        using var dlg = new ColorDialog { FullOpen = true, Color = ToColor(d.PaperColor) };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        var before = PaperState(d);
        d.PaperColor = $"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
        App.History.Push(new PaperEdit("용지 색", before, PaperState(d)));
        d.Modified = true;
        App.InvalidateAll(); App.NotifyLayers();
    }

    public static void TogglePaper()
    {
        var d = D; if (d == null) return;
        var before = PaperState(d);
        d.PaperVisible = !d.PaperVisible;
        App.History.Push(new PaperEdit("용지 보이기", before, PaperState(d)));
        d.Modified = true;
        App.InvalidateAll(); App.NotifyLayers();
    }
}
