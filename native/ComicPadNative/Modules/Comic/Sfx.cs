// 효과음(의성어) 글자 (웹 tools/sfx.js): 글자마다 크기·각도·위치, 휘기(아치·물결·원근·흔들림·통통), 2중 외곽선, 그라데이션, 기울임.
// 글자 레이어(kind 'text') 에 meta.sfx = true 로 저장 → 변형·이동·실행취소가 글자 레이어와 같고 언제든 다시 편집 (웹 판과 같은 meta).
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

public static class Sfx
{
    public static JsonObject Defaults() => new()
    {
        ["sfx"] = true, ["text"] = "쾅!", ["font"] = "Malgun Gothic", ["size"] = 140, ["bold"] = true, ["italic"] = false, ["vertical"] = false,
        ["color"] = "#111111", ["gradient"] = false, ["color2"] = "#555555",
        ["in"] = new JsonObject { ["w"] = 8, ["color"] = "#ffffff" }, ["out"] = new JsonObject { ["w"] = 4, ["color"] = "#111111" },
        ["warp"] = "none", ["warpAmt"] = 0.5, ["slant"] = 0, ["tracking"] = 0, ["chaos"] = 0, ["seed"] = 1,
        ["chars"] = new JsonArray(), ["align"] = "center", ["lineHeight"] = 1, ["strokeWidth"] = 0, ["strokeColor"] = "#ffffff",
    };

    static JsonObject P(string text, double size, string color, double inW, string inC, double outW, string outC, string warp = "none", double warpAmt = 0.5, double slant = 0, double chaos = 0, double tracking = 0, bool? bold = null, bool vertical = false, bool gradient = false, string color2 = null)
    {
        var o = new JsonObject { ["text"] = text, ["size"] = size, ["color"] = color, ["in"] = new JsonObject { ["w"] = inW, ["color"] = inC }, ["out"] = new JsonObject { ["w"] = outW, ["color"] = outC }, ["warp"] = warp, ["warpAmt"] = warpAmt };
        if (slant != 0) o["slant"] = slant; if (chaos != 0) o["chaos"] = chaos; if (tracking != 0) o["tracking"] = tracking;
        if (bold != null) o["bold"] = bold; if (vertical) o["vertical"] = true; if (gradient) { o["gradient"] = true; o["color2"] = color2; }
        return o;
    }
    public static readonly (string name, JsonObject p)[] Presets =
    {
        ("쾅!", P("쾅!", 160, "#111111", 10, "#ffffff", 5, "#111111", "persp", 0.7, -8, 0.25)),
        ("두근", P("두근", 110, "#e0245e", 7, "#ffffff", 3, "#7a0a2c", "bounce", 0.35, 0, 0.15, gradient: true, color2: "#ff9ab3")),
        ("스윽", P("스으윽", 90, "#444444", 5, "#ffffff", 0, "#000000", "wave", 0.25, 22, 0, 30, bold: false)),
        ("ドーン", P("ドーン", 150, "#ffffff", 10, "#111111", 6, "#ffffff", "arc", 0.4, 0, 0.1, vertical: true)),
        ("파바박", P("파바박", 120, "#111111", 7, "#ffffff", 3, "#111111", "shake", 0.9, 0, 0.5)),
        ("휘익", P("휘이익", 100, "#2a6bd1", 6, "#ffffff", 2, "#0b2a5c", "wave", 0.6, 15, 0, 10, gradient: true, color2: "#9fd0ff")),
    };
    public static JsonObject Preset(string name) => J.Clone(Presets.FirstOrDefault(p => p.name == name).p);
    public static readonly (string, string)[] Warps = { ("none", "없음"), ("arc", "아치"), ("wave", "물결"), ("persp", "원근 (점점 크게)"), ("shake", "흔들림"), ("bounce", "통통") };

    /// <summary>기본값 + meta (웹 { ...SFX_DEFAULTS, ...layer.meta })</summary>
    public static JsonObject Merged(JsonObject meta)
    {
        var m = Defaults();
        foreach (var (k, v) in meta) m[k] = v?.DeepClone();
        return m;
    }

    static Func<double> Rng(double seed)
    {
        double s = (seed * 9301 + 49297) % 233280; if (s == 0) s = 1;
        return () => (s = (s * 9301 + 49297) % 233280) / 233280;
    }

    public sealed record Glyph(string Ch, double X, double Y, double Size, double Rot, double W);

    /// <summary>글자 배치: 각 글자의 가운데·크기·각도 (변형 전, 문서 좌표)</summary>
    public static List<Glyph> Layout(JsonObject m, Func<string, double, double> measure)
    {
        var glyphs = TextRender.Chars((J.Str(m, "text", "") ?? "").Replace("\n", "").Replace("\r", ""));
        int n = glyphs.Count;
        var o = new List<Glyph>();
        if (n == 0) return o;
        var R = Rng(J.Num(m, "seed", 1) == 0 ? 1 : J.Num(m, "seed", 1));
        double size = J.Num(m, "size", 140), warpAmt = J.Num(m, "warpAmt", 0.5), chaos = J.Num(m, "chaos"), mx = J.Num(m, "x"), my = J.Num(m, "y");
        string warp = J.Str(m, "warp", "none"); bool vertical = J.Bool(m, "vertical");
        var chars = m["chars"] as JsonArray;
        JsonObject C(int i) => chars != null && i < chars.Count ? chars[i] as JsonObject : null;
        var sizes = new double[n];
        for (int i = 0; i < n; i++)
        {
            double u = n > 1 ? (double)i / (n - 1) : 0.5, s = size;
            if (warp == "persp") s *= 1 - warpAmt * 0.6 + warpAmt * 1.2 * u;
            var c = C(i); double sc = J.Num(c, "scale", 0); if (sc != 0) s *= sc;
            sizes[i] = s;
        }
        double track = J.Num(m, "tracking") / 100;
        var adv = new double[n];
        for (int i = 0; i < n; i++) adv[i] = (vertical ? sizes[i] : measure(glyphs[i], sizes[i])) + sizes[i] * track;
        double total = adv.Sum() - sizes[n - 1] * track;
        double pos = -total / 2;
        for (int i = 0; i < n; i++)
        {
            double a = adv[i] - (i == n - 1 ? sizes[i] * track : 0);
            double center = pos + (vertical ? sizes[i] * 0.5 : measure(glyphs[i], sizes[i]) / 2);
            pos += adv[i];
            double u = n > 1 ? (double)i / (n - 1) * 2 - 1 : 0;
            double off = 0, rot = 0, A = warpAmt * size;
            if (warp == "arc") { off = -A * 0.6 * (1 - u * u); rot = Math.Atan(A * 1.2 * u / Math.Max(1, total / 2)) * 180 / Math.PI; }
            else if (warp == "wave") { off = Math.Sin(u * Math.PI * 1.5) * A * 0.35; rot = Math.Atan(Math.Cos(u * Math.PI * 1.5) * A * 0.35 * Math.PI * 1.5 / Math.Max(1, total / 2)) * 180 / Math.PI; }
            else if (warp == "bounce") { off = (i % 2 == 1 ? 1 : -1) * A * 0.22; rot = (i % 2 == 1 ? 6 : -6) * warpAmt; }
            else if (warp == "shake") { off = (R() - 0.5) * A * 0.5; rot = (R() - 0.5) * 40 * warpAmt; }
            if (chaos != 0) { off += (R() - 0.5) * size * 0.3 * chaos; rot += (R() - 0.5) * 50 * chaos; }
            var c = C(i);
            double x, y;
            if (vertical) { x = mx - off + J.Num(c, "dx") * size; y = my + center + J.Num(c, "dy") * size; }
            else { x = mx + center + J.Num(c, "dx") * size; y = my + off + J.Num(c, "dy") * size; }
            if (warp == "persp" && !vertical) y += (size - sizes[i]) * 0.35;
            o.Add(new Glyph(glyphs[i], x, y, sizes[i], rot + J.Num(c, "rot"), a));
        }
        return o;
    }

    public static (Surface s, Rect r) Render(Document d, Layer l)
    {
        var m = Merged(l.Meta);
        var fam = Fonts.Family(J.Str(m, "font"));
        var st = Fonts.Style(fam, J.Bool(m, "bold", true), J.Bool(m, "italic"));
        var (asc, desc) = Fonts.Metrics(fam, st);
        double size = J.Num(m, "size", 140);
        var gl = Layout(m, (g, s) => Fonts.Measure(g, fam, st, s));
        double inW = Math.Max(0, J.Num(J.Obj(m, "in"), "w")), outW = Math.Max(0, J.Num(J.Obj(m, "out"), "w"));
        double slant = Math.Tan(J.Num(m, "slant") * Math.PI / 180);
        var mat = m["mat"] is JsonArray ? Mat2.From(J.Doubles(m["mat"])) : Mat2.Id;
        // 상자 (글자 레이어와 같은 형식)
        double pad = inW + outW + 4;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var g in gl)
        {
            double r = Math.Sqrt(g.W * g.W + g.Size * g.Size) / 2 + pad + Math.Abs(slant) * g.Size / 2;
            x0 = Math.Min(x0, g.X - r); y0 = Math.Min(y0, g.Y - r); x1 = Math.Max(x1, g.X + r); y1 = Math.Max(y1, g.Y + r);
        }
        if (gl.Count == 0) { double mx = J.Num(m, "x"), my = J.Num(m, "y"); x0 = mx - 10; y0 = my - 10; x1 = mx + 10; y1 = my + 10; }
        TextRender.SetBoxes(l.Meta, (x0, y0, x1 - x0, y1 - y0), 0, mat);
        var q = new[] { mat.Apply(x0, y0), mat.Apply(x1, y0), mat.Apply(x1, y1), mat.Apply(x0, y1) };
        var rr = Geo.BoundsRect(q, size * 0.3 + 4).Clip(d.W, d.H);
        if (rr.IsEmpty || gl.Count == 0) return (new Surface(d.W, d.H), Rect.Empty);
        var outC = Gfx.Col(J.Str(J.Obj(m, "out"), "color", "#111111")); var inC = Gfx.Col(J.Str(J.Obj(m, "in"), "color", "#ffffff"));
        var col = Gfx.Col(J.Str(m, "color", "#111111")); var col2 = Gfx.Col(J.Str(m, "color2") ?? J.Str(m, "color", "#111111"));
        bool grad = J.Bool(m, "gradient");
        var px = Gfx.Render(rr, g =>
        {
            var baseT = g.Transform;
            var paths = gl.Select(gg =>
            {
                var p = new GraphicsPath(FillMode.Winding);
                double w = Fonts.Measure(gg.Ch, fam, st, gg.Size);
                Fonts.AddText(p, gg.Ch, fam, st, gg.Size, -w / 2, gg.Size * (asc - desc) / 2);
                var t = mat.Mul(Mat2.Translate(gg.X, gg.Y)).Mul(Mat2.Rotate(gg.Rot)).Mul(new Mat2(1, 0, -slant, 1, 0, 0));
                return (gg, p, t);
            }).ToList();
            void Each(Action<Glyph, GraphicsPath> fn)
            {
                foreach (var (gg, p, t) in paths)
                {
                    using var mt = t.Gdi();
                    g.Transform = baseT; g.MultiplyTransform(mt);
                    fn(gg, p);
                }
            }
            // 바깥 외곽선 → 안쪽 외곽선 → 글자 (외곽선이 다른 글자를 덮지 않게 층별로)
            if (outW > 0) Each((gg, p) => { using var pen = new Pen(outC, (float)((inW + outW) * 2 * gg.Size / size)) { LineJoin = LineJoin.Round }; g.DrawPath(pen, p); });
            if (inW > 0) Each((gg, p) => { using var pen = new Pen(inC, (float)(inW * 2 * gg.Size / size)) { LineJoin = LineJoin.Round }; g.DrawPath(pen, p); });
            Each((gg, p) =>
            {
                if (grad)
                {
                    float hs = (float)(gg.Size / 2);
                    using var br = new LinearGradientBrush(new PointF(0, -hs * 4), new PointF(0, hs * 4), col, col2);
                    br.InterpolationColors = new ColorBlend { Colors = new[] { col, col, col2, col2 }, Positions = new[] { 0f, 0.375f, 0.625f, 1f } };
                    g.FillPath(br, p);
                }
                else { using var br = new SolidBrush(col); g.FillPath(br, p); }
            });
            g.Transform = baseT;
            foreach (var (_, p, _) in paths) p.Dispose();
        });
        return (Gfx.NewSurface(d, rr, px), rr);
    }

    /// <summary>새 효과음 (임시로 넣고 편집 창 → 확인하면 실행취소 한 번으로 추가)</summary>
    public static Layer CreateAt(double x, double y, string preset = null)
    {
        var p = Preset(preset ?? SfxTool.PresetName);
        var meta = Defaults();
        if (p != null) foreach (var (k, v) in p) meta[k] = v?.DeepClone();
        meta["x"] = J.R(x); meta["y"] = J.R(y); meta["font"] = SfxTool.Font ?? "Malgun Gothic";
        if (SfxTool.SizePct > 0) meta["size"] = Bubbles.JsRound(J.Num(p ?? meta, "size", 140) * SfxTool.SizePct / 100);
        return TextTool.NewLayer("효과음", meta, l => SfxDialog.Show(l, true, new MetaPreview(l)), null, "효과음");
    }

    public static void ToPixels(Layer l)
    {
        if (l?.Kind != "text" || !J.Bool(l.Meta, "sfx")) { App.Toast("효과음 레이어를 고르세요"); return; }
        App.History.Struct(App.Doc, "효과음을 그림으로", () => { l.Kind = "raster"; l.Meta = null; });
        App.NotifyLayers();
        App.Toast("효과음을 그림 레이어로 바꿨습니다 (이제 글자 편집은 안 됩니다)");
    }
}

/// <summary>효과음 편집 창</summary>
public static class SfxDialog
{
    public static void EditExisting(Layer l)
    {
        var prev = new MetaPreview(l);
        bool toPixels = false;
        bool ok = Show(l, false, prev, () => toPixels = true);
        if (!ok) { prev.Restore(); App.InvalidateAll(); return; }
        var meta = J.Clone(l.Meta); string name = l.Name;
        prev.Restore();
        MetaLayers.Commit(l, "효과음 편집", meta, MetaLayers.Render, name);
        if (toPixels) Sfx.ToPixels(l);
    }

    public static bool Show(Layer l, bool isNew, MetaPreview prev, Action toPixels = null)
    {
        var m = Sfx.Merged(l.Meta);
        int ci = 0;
        void Upd() { var mm = J.Clone(m); prev.Show(mm, MetaLayers.Render); foreach (var k in new[] { "lbox", "box", "quad" }) m[k] = mm[k]?.DeepClone(); }
        using var f = new Form { Text = isNew ? "효과음 넣기" : "효과음 고치기", StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, Width = 620, Height = 720, Font = new Font("Malgun Gothic", 9f), MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.SizableToolWindow };
        UI.Dialogs.Modals.Adopt(f);
        var ta = new TextBox { Dock = DockStyle.Top, Text = J.Str(m, "text", ""), BackColor = Opt.Bg2, ForeColor = Opt.Fg, Font = new Font("Malgun Gothic", 13f), PlaceholderText = "효과음 (예: 쾅!, 두근, 스윽)" };
        CUI.Host host = null;
        JsonObject Ch()
        {
            if (m["chars"] is not JsonArray a) m["chars"] = a = new JsonArray();
            while (a.Count <= ci) a.Add(new JsonObject { ["dx"] = 0, ["dy"] = 0, ["rot"] = 0, ["scale"] = 1 });
            if (a[ci] is not JsonObject o) a[ci] = o = new JsonObject { ["dx"] = 0, ["dy"] = 0, ["rot"] = 0, ["scale"] = 1 };
            return o;
        }
        IEnumerable<Control> Build()
        {
            yield return Opt.Title("모양 모음");
            var pr = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(560, 0), BackColor = Opt.Bg };
            foreach (var (name, _) in Sfx.Presets)
            {
                var nm = name;
                pr.Controls.Add(Opt.Button(nm, () =>
                {
                    var keep = (x: m["x"]?.DeepClone(), y: m["y"]?.DeepClone(), mat: m["mat"]?.DeepClone(), font: m["font"]?.DeepClone());
                    string text = J.Str(m, "text", "");
                    m = Sfx.Defaults();
                    foreach (var (k, v) in Sfx.Preset(nm)) m[k] = v?.DeepClone();
                    m["x"] = keep.x; m["y"] = keep.y; if (keep.mat != null) m["mat"] = keep.mat; m["font"] = keep.font;
                    m["text"] = !string.IsNullOrEmpty(text) && !isNew ? text : J.Str(Sfx.Preset(nm), "text");
                    m["chars"] = new JsonArray();
                    ta.Text = J.Str(m, "text", ""); Upd(); host.Rebuild();
                }));
            }
            yield return pr;
            yield return Opt.Choice("글꼴", Fonts.List(), () => J.Str(m, "font", "Malgun Gothic"), v => { m["font"] = v; Upd(); });
            yield return Opt.Slider("크기", 12, 800, () => J.Num(m, "size", 140), v => { m["size"] = Math.Round(v); Upd(); }, log: true);
            yield return Opt.Row(Opt.Check("굵게", () => J.Bool(m, "bold", true), v => { m["bold"] = v; Upd(); }), Opt.Check("기울임 글꼴", () => J.Bool(m, "italic"), v => { m["italic"] = v; Upd(); }), Opt.Check("세로", () => J.Bool(m, "vertical"), v => { m["vertical"] = v; Upd(); }));
            yield return Opt.Slider("기울기", -40, 40, () => J.Num(m, "slant"), v => { m["slant"] = Math.Round(v); Upd(); }, step: 1);
            yield return Opt.Slider("자간", -40, 120, () => J.Num(m, "tracking"), v => { m["tracking"] = Math.Round(v); Upd(); }, step: 1);
            yield return Opt.Title("휘기");
            yield return CUI.Seg(Sfx.Warps, () => J.Str(m, "warp", "none"), v => { m["warp"] = v; Upd(); });
            yield return Opt.Slider("세기", 0, 1.5, () => J.Num(m, "warpAmt", 0.5), v => { m["warpAmt"] = Math.Round(v, 2); Upd(); }, step: 0.05);
            yield return Opt.Slider("제멋대로 (글자마다 무작위)", 0, 1, () => J.Num(m, "chaos"), v => { m["chaos"] = Math.Round(v, 2); Upd(); }, step: 0.05);
            yield return Opt.Button("무작위 다시", () => { m["seed"] = Random.Shared.Next(1, 100000); Upd(); });
            yield return Opt.Title("색");
            yield return Opt.Row(CUI.ColorBtn("글자색", () => J.Str(m, "color", "#111111"), v => { m["color"] = v; Upd(); }), Opt.Check("그라데이션", () => J.Bool(m, "gradient"), v => { m["gradient"] = v; Upd(); host.Rebuild(); }));
            if (J.Bool(m, "gradient")) yield return CUI.ColorBtn("아래쪽 색", () => J.Str(m, "color2", "#555555"), v => { m["color2"] = v; Upd(); });
            JsonObject In() => m["in"] as JsonObject ?? (JsonObject)(m["in"] = new JsonObject { ["w"] = 8, ["color"] = "#ffffff" });
            JsonObject Out() => m["out"] as JsonObject ?? (JsonObject)(m["out"] = new JsonObject { ["w"] = 4, ["color"] = "#111111" });
            yield return CUI.ColorBtn("안쪽 외곽선", () => J.Str(In(), "color", "#ffffff"), v => { In()["color"] = v; Upd(); });
            yield return Opt.Slider("굵기 (안쪽)", 0, 40, () => J.Num(In(), "w"), v => { In()["w"] = Math.Round(v * 2) / 2; Upd(); }, step: 0.5);
            yield return CUI.ColorBtn("바깥 외곽선", () => J.Str(Out(), "color", "#111111"), v => { Out()["color"] = v; Upd(); });
            yield return Opt.Slider("굵기 (바깥)", 0, 40, () => J.Num(Out(), "w"), v => { Out()["w"] = Math.Round(v * 2) / 2; Upd(); }, step: 0.5);
            yield return Opt.Title("글자 하나씩");
            var gl = TextRender.Chars(J.Str(m, "text", ""));
            if (gl.Count > 0)
            {
                ci = Math.Min(ci, gl.Count - 1);
                var row = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(560, 0), BackColor = Opt.Bg };
                for (int i = 0; i < gl.Count; i++) { int k = i; var b = Opt.Button(gl[i] == " " ? "␣" : gl[i], () => { ci = k; host.Rebuild(); }, k == ci); row.Controls.Add(b); }
                yield return row;
                yield return Opt.Slider("크기", 0.3, 2.5, () => J.Num(Ch(), "scale", 1), v => { Ch()["scale"] = Math.Round(v, 2); Upd(); }, pct: true, step: 0.05);
                yield return Opt.Slider("각도", -90, 90, () => J.Num(Ch(), "rot"), v => { Ch()["rot"] = Math.Round(v); Upd(); }, step: 1);
                yield return Opt.Slider("가로 이동", -1.5, 1.5, () => J.Num(Ch(), "dx"), v => { Ch()["dx"] = Math.Round(v, 2); Upd(); }, step: 0.02);
                yield return Opt.Slider("세로 이동", -1.5, 1.5, () => J.Num(Ch(), "dy"), v => { Ch()["dy"] = Math.Round(v, 2); Upd(); }, step: 0.02);
                yield return Opt.Button("이 글자 원래대로", () => { var a = (JsonArray)m["chars"]; Ch(); a[ci] = new JsonObject { ["dx"] = 0, ["dy"] = 0, ["rot"] = 0, ["scale"] = 1 }; Upd(); host.Rebuild(); });
            }
            yield return Opt.Help("다 만든 뒤에도 효과음 도구로 탭하면 다시 고칠 수 있고, 변형 도구(Ctrl+T)로 크기·회전·원근을 줘도 글자 정보는 그대로입니다. 그림으로 굳히려면 레이어를 \"그림으로 바꾸기\".");
        }
        host = new CUI.Host(Build);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Opt.Bg };
        scroll.Controls.Add(host.Panel);
        ta.TextChanged += (_, _) =>
        {
            m["text"] = ta.Text;
            if (m["chars"] is JsonArray a) while (a.Count > TextRender.Chars(ta.Text).Count) a.RemoveAt(a.Count - 1);
            Upd(); host.Rebuild();
        };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true, BackColor = Opt.Accent, ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, FlatStyle = FlatStyle.Flat };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = Opt.Bg, Padding = new Padding(6) };
        bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
        if (!isNew && toPixels != null)
        {
            var tp = new Button { Text = "그림으로 바꾸기", AutoSize = true, FlatStyle = FlatStyle.Flat };
            tp.Click += (_, _) => { toPixels(); f.DialogResult = DialogResult.OK; f.Close(); };
            bottom.Controls.Add(tp);
        }
        f.Controls.Add(scroll); f.Controls.Add(ta); f.Controls.Add(bottom);
        f.CancelButton = cancel;
        f.Shown += (_, _) => { Upd(); ta.Focus(); ta.SelectAll(); };
        bool res = f.ShowDialog(App.Main) == DialogResult.OK && !string.IsNullOrWhiteSpace(J.Str(m, "text", ""));
        if (!res) return false;
        l.Meta = J.Clone(m);
        string t = J.Str(m, "text", "");
        l.Name = "효과음: " + (t.Length > 10 ? t[..10] : t);
        return true;
    }
}

public sealed class SfxTool : ToolBase
{
    public override string Id => "sfx";
    public override string Name => "효과음 글자";
    public override string Icon => "💥";
    public override Cursor Cursor => Cursors.IBeam;
    public static string PresetName = "쾅!"; public static double SizePct = 100; public static string Font = "Malgun Gothic";

    sealed class DragSt { public Layer L; public double X0, Y0, SX0, SY0; public bool Moved, Create; public MetaPreview Prev; public JsonObject Orig; }
    DragSt drag;
    public override bool Busy => drag != null;

    public override void Down(PointerArgs e)
    {
        var l = TextTool.TextAt(e.X, e.Y, sfxOnly: true);
        if (l != null)
        {
            App.Doc.ActiveId = l.Id; App.NotifyLayers();
            if (!App.CheckEditable(l)) return;
            drag = new DragSt { L = l, X0 = e.X, Y0 = e.Y, SX0 = e.SX, SY0 = e.SY, Orig = J.Clone(l.Meta) };
        }
        else drag = new DragSt { Create = true, X0 = e.X, Y0 = e.Y };
    }
    public override void Move(PointerArgs e)
    {
        var d = drag;
        if (d?.L == null) return;
        if (Math.Sqrt((e.SX - d.SX0) * (e.SX - d.SX0) + (e.SY - d.SY0) * (e.SY - d.SY0)) > 5) d.Moved = true;
        if (!d.Moved) return;
        d.Prev ??= new MetaPreview(d.L);
        d.Prev.Show(Bubbles.MovedText(d.Orig, e.X - d.X0, e.Y - d.Y0), MetaLayers.Render);
    }
    public override void Up(PointerArgs e)
    {
        var d = drag; drag = null;
        if (d == null) return;
        if (d.Create) { Sfx.CreateAt(d.X0, d.Y0); return; }
        if (!d.Moved) { SfxDialog.EditExisting(d.L); return; }
        var meta = J.Clone(d.L.Meta); d.Prev.Restore();
        MetaLayers.Commit(d.L, "효과음 이동", meta, MetaLayers.Render);
    }
    public override void Cancel() { if (drag?.Prev != null) { drag.Prev.Restore(); App.InvalidateAll(); } drag = null; }

    /// <summary>효과음 상자 윤곽 (효과음 도구일 때, 늘 그리는 겹침 그림)</summary>
    public static void DrawBoxes(Graphics g, ViewXf v)
    {
        if (App.Tool?.Id != "sfx") return;
        foreach (var l in App.Doc?.Layers ?? new())
        {
            if (l.Kind != "text" || !J.Bool(l.Meta, "sfx") || !l.Visible || l.Meta["quad"] is not JsonArray q) continue;
            using var pen = CUI.Dash(l == App.Doc.Active ? Color.FromArgb(232, 163, 61) : Color.FromArgb(115, 232, 163, 61), 1, 4, 3);
            g.DrawPolygon(pen, q.Select(J.ToP).Select(p => Gfx.S(v, p)).ToArray());
        }
    }

    public override Control BuildOptions()
    {
        CUI.Host host = null;
        IEnumerable<Control> Items()
        {
            yield return Opt.Title("새 효과음");
            var row = new UI.Controls.WebRow();
            foreach (var (name, _) in Sfx.Presets) { var nm = name; row.Controls.Add(Opt.Button(nm, () => { PresetName = nm; host.Rebuild(); }, nm == PresetName)); }
            yield return row;
            yield return Opt.Choice("글꼴", Fonts.List(), () => Font, v => Font = v);
            yield return Opt.Slider("크기 배율", 20, 400, () => SizePct, v => SizePct = Math.Round(v / 5) * 5, step: 5);
            yield return Opt.Help("빈 곳 탭: 새 효과음 · 효과음 탭: 고치기 · 끌기: 옮기기. 아치·물결·원근·흔들림으로 휘고, 글자마다 크기·각도를 따로 줄 수 있습니다.");
        }
        host = new CUI.Host(Items);
        return host.Panel;
    }
}
