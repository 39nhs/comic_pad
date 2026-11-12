// 텍스트 (웹 tools/comic.js 텍스트 부분): 다시 편집할 수 있는 글자 레이어.
// 글자 레이어: kind 'text', meta { text, font, size, color, bold, italic, lineHeight, vertical, align, strokeWidth, strokeColor, x, y, mat?, tracking?, kern?, lbox, quad, box }.
// x, y 는 기준점(정렬에 따라 왼쪽/가운데/오른쪽, 위쪽), mat 은 변형 행렬 [a,b,c,d,e,f]. 루비 {漢字|かんじ}, 문장부호 좁게, 세로쓰기(괄호·장음 회전) 지원.
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

public static class TextRender
{
    public static JsonObject Defaults() => new()
    {
        ["text"] = "", ["font"] = "Malgun Gothic", ["size"] = 42, ["color"] = "#000000", ["bold"] = true, ["italic"] = false,
        ["lineHeight"] = 1.3, ["vertical"] = false, ["align"] = "center", ["strokeWidth"] = 0, ["strokeColor"] = "#ffffff",
    };

    static readonly HashSet<string> PUNCT = new("、。，．・：；！？!?…‥「」『』（）()〈〉《》“”‘’,.~～".Select(c => c.ToString()));
    static readonly HashSet<string> ROTATE_V = new("ー－—―‐-~～…‥()（）[]［］{}｛｝「」『』〈〉《》<>＜＞=＝".Select(c => c.ToString()));
    static readonly Regex RubyRe = new(@"\{([^{}|]+)\|([^{}]+)\}");

    /// <summary>"{漢字|かんじ}" → [(글자, 루비)]</summary>
    public static List<(string text, string ruby)> ParseRuby(string ln)
    {
        var o = new List<(string, string)>(); int last = 0;
        foreach (Match mm in RubyRe.Matches(ln))
        {
            if (mm.Index > last) o.Add((ln[last..mm.Index], null));
            o.Add((mm.Groups[1].Value, mm.Groups[2].Value));
            last = mm.Index + mm.Length;
        }
        if (last < ln.Length) o.Add((ln[last..], null));
        return o;
    }
    /// <summary>글자 단위 (한글·한자·이모지 서로게이트 묶음)</summary>
    public static List<string> Chars(string s)
    {
        var o = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s ?? "");
        while (e.MoveNext()) o.Add((string)e.Current);
        return o;
    }

    /// <summary>그릴 글자 한 조각</summary>
    public sealed record Run(string Text, double X, double Y, double Size, bool Bold, string Base = "top", bool CenterX = false, double Rot = 0);

    /// <summary>배치 계산: 그릴 조각들과 변형 전 상자</summary>
    public static (List<Run> runs, (double x, double y, double w, double h) box) Layout(JsonObject m, FontFamily fam, FontStyle st)
    {
        string text = J.Str(m, "text", "") ?? "";
        double size = J.Num(m, "size", 42), lhK = J.Num(m, "lineHeight", 1.3), mx = J.Num(m, "x"), my = J.Num(m, "y");
        bool vertical = J.Bool(m, "vertical"), kern = J.Bool(m, "kern"), bold = J.Bool(m, "bold", true);
        string align = J.Str(m, "align", "center");
        double track = J.Num(m, "tracking") / 100;
        var lines = text.Replace("\r", "").Split('\n');
        double lh = size * lhK;
        var runs = new List<Run>();
        bool rich = RubyRe.IsMatch(text) || kern;
        double rubySize = size * 0.42;
        var rubySt = Fonts.Style(fam, false, J.Bool(m, "italic"));
        double W(string s) => Fonts.Measure(s, fam, st, size);
        if (!vertical && rich)
        {
            double maxW = 0; bool rubyTop = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var rs = ParseRuby(lines[i]);
                double Adv(string ch) => (W(ch) + size * track) * (kern && PUNCT.Contains(ch) ? 0.62 : 1);
                double w = rs.Sum(r => Chars(r.text).Sum(Adv)) - size * track;
                double x = align == "center" ? mx - w / 2 : align == "right" ? mx - w : mx;
                double y = my + i * lh;
                foreach (var (t, ruby) in rs)
                {
                    double x0 = x;
                    foreach (var ch in Chars(t)) { runs.Add(new Run(ch, x, y, size, bold)); x += Adv(ch); }
                    if (ruby != null)
                    {
                        runs.Add(new Run(ruby, (x0 + x) / 2, y - rubySize * 1.08, rubySize, false, "top", true));
                        if (i == 0) rubyTop = true;
                    }
                }
                maxW = Math.Max(maxW, w);
            }
            double left = align == "center" ? mx - maxW / 2 : align == "right" ? mx - maxW : mx;
            double top = rubyTop ? my - rubySize * 1.1 : my;
            return (runs, (left, top, maxW, lines.Length * lh + (my - top)));
        }
        if (!vertical)
        {
            double maxW = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                double y = my + i * lh;
                if (track == 0)
                {
                    double w = W(ln);
                    double x = align == "center" ? mx - w / 2 : align == "right" ? mx - w : mx;
                    runs.Add(new Run(ln, x, y, size, bold));
                    maxW = Math.Max(maxW, w);
                }
                else
                {
                    var cs = Chars(ln);
                    double w = cs.Sum(c => W(c) + size * track) - size * track;
                    double x = align == "center" ? mx - w / 2 : align == "right" ? mx - w : mx;
                    foreach (var c in cs) { runs.Add(new Run(c, x, y, size, bold)); x += W(c) + size * track; }
                    maxW = Math.Max(maxW, w);
                }
            }
            double left = align == "center" ? mx - maxW / 2 : align == "right" ? mx - maxW : mx;
            return (runs, (left, my, maxW, lines.Length * lh));
        }
        // 세로쓰기: 오른쪽 줄부터 왼쪽으로
        double colW = lh, step = size * (1.05 + track);
        double maxLen = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            double cx = mx - i * colW, j = 0;
            foreach (var (t, ruby) in ParseRuby(lines[i]))
            {
                double j0 = j;
                foreach (var ch in Chars(t))
                {
                    double y = my + j * step;
                    if (ROTATE_V.Contains(ch)) runs.Add(new Run(ch, cx, y + size / 2, size, bold, "middle", true, 90));
                    else runs.Add(new Run(ch, cx, y, size, bold, "top", true));
                    j += kern && PUNCT.Contains(ch) ? 0.62 : 1;
                }
                if (ruby != null)
                {
                    var rc = Chars(ruby);
                    double span = (j - j0) * step, rs = Math.Min(rubySize * 1.05, span / Math.Max(1, rc.Count));
                    for (int k = 0; k < rc.Count; k++)
                        runs.Add(new Run(rc[k], cx + size * 0.5 + rubySize * 0.6, my + j0 * step + (span - rc.Count * rs) / 2 + k * rs, rubySize, false, "top", true));
                }
            }
            maxLen = Math.Max(maxLen, j);
        }
        return (runs, (mx - (lines.Length - 0.5) * colW, my, lines.Length * colW, maxLen * step));
    }

    /// <summary>한 조각을 path 로 (웹 fillText 의 정렬·기준선 규칙)</summary>
    public static GraphicsPath RunPath(Run r, FontFamily fam, FontStyle st)
    {
        var p = new GraphicsPath(FillMode.Winding);
        var (asc, desc) = Fonts.Metrics(fam, st);
        double w = r.CenterX ? Fonts.Measure(r.Text, fam, st, r.Size) : 0;
        if (r.Rot != 0)
        {
            // 가운데 기준점에서 돌림 (웹: translate → rotate → textBaseline middle, textAlign center)
            double baseline = r.Size * (asc - desc) / 2;
            Fonts.AddText(p, r.Text, fam, st, r.Size, -w / 2, baseline);
            using var mt = new Matrix();
            mt.Translate((float)r.X, (float)r.Y); mt.Rotate((float)r.Rot);
            p.Transform(mt);
            return p;
        }
        double bl = r.Base == "middle" ? r.Y + r.Size * (asc - desc) / 2 : r.Y + r.Size * asc;
        Fonts.AddText(p, r.Text, fam, st, r.Size, r.X - w / 2, bl);
        return p;
    }

    /// <summary>글자 레이어 다시 그리기: 새 면 + 그린 범위. meta 의 lbox·quad·box 도 갱신</summary>
    public static (Surface s, Rect r) Render(Document d, Layer l)
    {
        var m = l.Meta;
        var fam = Fonts.Family(J.Str(m, "font"));
        var st = Fonts.Style(fam, J.Bool(m, "bold", true), J.Bool(m, "italic"));
        var rubySt = Fonts.Style(fam, false, J.Bool(m, "italic"));
        var (runs, box) = Layout(m, fam, st);
        double sw = J.Num(m, "strokeWidth"), size = J.Num(m, "size", 42);
        var mat = m["mat"] is JsonArray ? Mat2.From(J.Doubles(m["mat"])) : Mat2.Id;
        SetBoxes(m, box, sw + 4, mat);
        // 그릴 범위: 변형 뒤 상자 + 글자가 em 상자를 넘는 몫
        double pad = sw + size * 0.5 + 4;
        var lb = (x: box.x - pad, y: box.y - pad, w: box.w + pad * 2, h: box.h + pad * 2);
        var q = new[] { mat.Apply(lb.x, lb.y), mat.Apply(lb.x + lb.w, lb.y), mat.Apply(lb.x + lb.w, lb.y + lb.h), mat.Apply(lb.x, lb.y + lb.h) };
        var r = Geo.BoundsRect(q, 2).Clip(d.W, d.H);
        if (r.IsEmpty || runs.Count == 0) return (new Surface(d.W, d.H), Rect.Empty);
        var fill = Gfx.Col(J.Str(m, "color", "#000000")); var strokeC = Gfx.Col(J.Str(m, "strokeColor", "#ffffff"));
        var px = Gfx.Render(r, g =>
        {
            if (!mat.IsId) g.MultiplyTransform(mat.Gdi());
            using var br = new SolidBrush(fill);
            using var pen = new Pen(strokeC, (float)(sw * 2)) { LineJoin = LineJoin.Round };
            foreach (var run in runs)
            {
                using var p = RunPath(run, fam, run.Bold ? st : rubySt);
                if (sw > 0) g.DrawPath(pen, p);
                g.FillPath(br, p);
            }
        });
        return (Gfx.NewSurface(d, r, px), r);
    }

    /// <summary>웹과 같은 lbox(변형 전)·quad·box(변형 후) 기록</summary>
    public static void SetBoxes(JsonObject m, (double x, double y, double w, double h) box, double pad, Mat2 mat)
    {
        var lb = (x: box.x - pad, y: box.y - pad, w: box.w + pad * 2, h: box.h + pad * 2);
        m["lbox"] = new JsonObject { ["x"] = J.R(lb.x), ["y"] = J.R(lb.y), ["w"] = J.R(lb.w), ["h"] = J.R(lb.h) };
        var q = new[] { mat.Apply(lb.x, lb.y), mat.Apply(lb.x + lb.w, lb.y), mat.Apply(lb.x + lb.w, lb.y + lb.h), mat.Apply(lb.x, lb.y + lb.h) };
        m["quad"] = new JsonArray(q.Select(p => (JsonNode)J.Pt(p.X, p.Y)).ToArray());
        var b = Geo.Bounds(q);
        m["box"] = new JsonObject { ["x"] = J.R(b.x0), ["y"] = J.R(b.y0), ["w"] = J.R(b.x1 - b.x0), ["h"] = J.R(b.y1 - b.y0) };
    }

    public static bool InBox(JsonObject m, double x, double y)
    {
        if (m?["box"] is not JsonObject b) return false;
        return x >= J.Num(b, "x") && y >= J.Num(b, "y") && x <= J.Num(b, "x") + J.Num(b, "w") && y <= J.Num(b, "y") + J.Num(b, "h");
    }
}

public sealed class TextTool : ToolBase
{
    public override string Id => "text";
    public override string Name => "텍스트";
    public override string Icon => "T";
    public override Keys Key => Keys.T;
    public override Cursor Cursor => Cursors.IBeam;

    // 새 텍스트 기본값 (웹 textTool.opt)
    public static string Font = "Malgun Gothic"; public static double Size = 42; public static string Color_ = "#000000"; public static bool Bold = true, Vertical;
    public static string Align = "center"; public static double StrokeWidth; public static string StrokeColor = "#ffffff"; public static double LineHeight = 1.3;

    sealed class DragSt { public Layer L; public double X0, Y0, SX0, SY0; public bool Moved, Create; public MetaPreview Prev; public JsonObject Orig; }
    DragSt drag;

    public static Layer TextAt(double x, double y, bool sfxOnly = false)
    {
        var d = App.Doc;
        bool Hit(Layer l)
        {
            if (!l.Visible) return false;
            if (l.Kind == "text" && l.Meta != null) return (!sfxOnly || J.Bool(l.Meta, "sfx")) && TextRender.InBox(l.Meta, x, y);
            // PSD 에서 가져온 텍스트(아직 그림): 글자 픽셀 위를 탭했는지
            if (!sfxOnly && l.Meta?["psdText"] is JsonObject && l.Px != null) return (l.Px.Get((int)Math.Floor(x), (int)Math.Floor(y)) >> 24) > 0;
            return false;
        }
        var act = d.Active;
        if (act != null && Hit(act)) return act;
        for (int i = d.Layers.Count - 1; i >= 0; i--) if (Hit(d.Layers[i])) return d.Layers[i];
        return null;
    }

    public override void Down(PointerArgs e)
    {
        var l = TextAt(e.X, e.Y);
        if (l != null && l.Kind != "text") { drag = null; ConvertPsdText(l); return; }
        if (l != null)
        {
            App.Doc.ActiveId = l.Id; App.NotifyLayers();
            if (!App.CheckEditable(l)) { drag = null; return; }
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
        if (d.Create) { CreateAt(d.X0, d.Y0); return; }
        if (!d.Moved) { Edit(d.L); return; }
        var meta = J.Clone(d.L.Meta); d.Prev.Restore();
        MetaLayers.Commit(d.L, J.Bool(meta, "sfx") ? "효과음 이동" : "텍스트 이동", meta, MetaLayers.Render);
    }
    public override void Cancel() { if (drag?.Prev != null) { drag.Prev.Restore(); App.InvalidateAll(); } drag = null; }
    public override bool Busy => drag != null;

    /// <summary>새 글자 레이어 (center = 말풍선 가운데에 맞춤). also = 만드는 기록 안에서 함께 할 일 (말풍선 연결 등)</summary>
    public static Layer CreateAt(double x, double y, bool center = false, Action<Layer> also = null)
    {
        var d = App.Doc; if (d == null) return null;
        var meta = TextRender.Defaults();
        meta["font"] = Font; meta["size"] = Size; meta["color"] = Color_; meta["bold"] = Bold; meta["vertical"] = Vertical;
        meta["align"] = center ? "center" : Align; meta["strokeWidth"] = StrokeWidth; meta["strokeColor"] = StrokeColor; meta["lineHeight"] = LineHeight;
        meta["x"] = J.R(x); meta["y"] = J.R(center ? y - Size * LineHeight / 2 : y);
        return NewLayer("텍스트", meta, m => TextDialog.Show(m, true), also, "텍스트");
    }

    /// <summary>임시로 넣어 편집 창을 띄우고, 확인하면 실행취소 한 번으로 추가</summary>
    public static Layer NewLayer(string name, JsonObject meta, Func<Layer, bool> dialog, Action<Layer> also, string label)
    {
        var d = App.Doc;
        string prevActive = d.ActiveId;
        var l = new Layer(d, name, "text") { Meta = meta };
        l.Px = MetaLayers.Render(l).s;
        d.Layers.Add(l); d.ActiveId = l.Id;
        App.InvalidateAll(); App.NotifyLayers();
        bool ok = dialog(l);
        var finalMeta = J.Clone(l.Meta); string finalName = l.Name;
        d.Layers.Remove(l); d.ActiveId = prevActive;
        if (!ok) { App.InvalidateAll(); App.NotifyLayers(); return null; }
        App.History.Struct(d, label, () =>
        {
            l.Meta = finalMeta; l.Name = finalName; l.Px = MetaLayers.Render(l).s;
            d.Layers.Add(l); d.ActiveId = l.Id;
            also?.Invoke(l);
        });
        d.Modified = true;
        App.InvalidateAll(); App.NotifyLayers();
        return l;
    }

    /// <summary>있는 글자(효과음) 레이어 편집</summary>
    public static void Edit(Layer l)
    {
        if (l?.Meta == null) return;
        if (!App.CheckEditable(l)) return;
        if (J.Bool(l.Meta, "sfx")) { SfxDialog.EditExisting(l); return; }
        var prev = new MetaPreview(l);
        bool ok = TextDialog.Show(l, false, prev);
        if (!ok) { prev.Restore(); App.InvalidateAll(); return; }
        var meta = J.Clone(l.Meta); string name = l.Name;
        prev.Restore();
        MetaLayers.Commit(l, "텍스트 편집", meta, MetaLayers.Render, name);
    }

    /// <summary>PSD 에서 가져온 텍스트 레이어(그림)를 편집 가능한 텍스트 레이어로</summary>
    public static void ConvertPsdText(Layer l)
    {
        var t = l.Meta?["psdText"] as JsonObject;
        if (t == null) return;
        string txt = J.Str(t, "text", "");
        if (!CUI.Confirm($"PSD 텍스트 \"{(txt.Length > 24 ? txt[..24] : txt)}\" 를 다시 편집할 수 있는 텍스트로 바꿀까요?\n(글꼴이 이 컴퓨터에 없으면 비슷한 글꼴로 보입니다)")) return;
        var meta = TextRender.Defaults();
        double size = J.Num(t, "size", 42);
        meta["text"] = txt; meta["size"] = size; meta["color"] = J.Str(t, "color", "#000000"); meta["font"] = J.Str(t, "font") ?? "Malgun Gothic"; meta["bold"] = false;
        meta["align"] = J.Str(t, "align", "left"); meta["vertical"] = J.Bool(t, "vertical"); meta["lineHeight"] = 1.2; meta["x"] = 0; meta["y"] = J.R(-size * 0.82);
        if (t["mat"] is JsonArray mat) meta["mat"] = mat.DeepClone();
        App.History.Struct(App.Doc, "PSD 텍스트 변환", () => { l.Kind = "text"; l.Meta = meta; l.Px = MetaLayers.Render(l).s; });
        App.Doc.ActiveId = l.Id;
        App.InvalidateAll(); App.NotifyLayers();
        Edit(l);
    }

    /// <summary>글자 상자 윤곽 (텍스트 도구일 때, 늘 그리는 겹침 그림)</summary>
    public static void DrawBoxes(Graphics g, ViewXf v)
    {
        if (App.Tool?.Id != "text") return;
        foreach (var l in App.Doc?.Layers ?? new())
        {
            if (l.Kind != "text" || !l.Visible || l.Meta?["box"] is not JsonObject) continue;
            var q = (l.Meta["quad"] as JsonArray)?.Select(J.ToP).ToList();
            if (q == null || q.Count < 4) { var b = (JsonObject)l.Meta["box"]; q = Panels.RectPts(J.Num(b, "x"), J.Num(b, "y"), J.Num(b, "w"), J.Num(b, "h")); }
            using var pen = CUI.Dash(l == App.Doc.Active ? Color.FromArgb(47, 124, 246) : Color.FromArgb(102, 47, 124, 246), 1, 4, 3);
            g.DrawPolygon(pen, q.Select(p => Gfx.S(v, p)).ToArray());
        }
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("새 텍스트 기본값"),
        Opt.Choice("글꼴", Fonts.List(), () => Font, v => Font = v),
        Opt.Slider("크기", 6, 400, () => Size, v => Size = Math.Round(v), log: true),
        Opt.Row(Opt.Check("굵게", () => Bold, v => Bold = v), Opt.Check("세로쓰기", () => Vertical, v => Vertical = v)),
        CUI.ColorBtn("글자색", () => Color_, v => Color_ = v),
        Opt.Slider("외곽선", 0, 30, () => StrokeWidth, v => StrokeWidth = v, step: 0.5),
        Opt.Help("빈 곳 탭: 새 텍스트 · 텍스트 탭: 편집 · 텍스트 드래그: 이동. data/fonts 폴더에 폰트 파일(.ttf/.otf)을 넣으면 목록에 나타납니다."));
}

/// <summary>텍스트 입력·편집 창. 입력하는 동안 그림판에 바로 반영</summary>
public static class TextDialog
{
    public static bool Show(Layer l, bool isNew, MetaPreview prev = null)
    {
        var m = J.Clone(l.Meta);
        prev ??= new MetaPreview(l);
        void Upd()
        {
            var mm = J.Clone(m);
            prev.Show(mm, MetaLayers.Render);
            foreach (var k in new[] { "lbox", "box", "quad" }) m[k] = mm[k]?.DeepClone();
        }
        using var f = new Form { Text = isNew ? "텍스트 입력" : "텍스트 편집", StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, Width = 640, Height = 560, Font = new Font("Malgun Gothic", 9f), MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.SizableToolWindow };
        var ta = new TextBox { Multiline = true, Dock = DockStyle.Top, Height = 110, Text = (J.Str(m, "text", "") ?? "").Replace("\n", "\r\n"), BackColor = Opt.Bg2, ForeColor = Opt.Fg, Font = new Font("Malgun Gothic", 12f), ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, PlaceholderText = "대사를 입력하세요" };
        ta.TextChanged += (_, _) => { m["text"] = ta.Text.Replace("\r\n", "\n"); Upd(); };
        var ctl = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = true, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(6) };
        ctl.Controls.Add(Opt.Choice("글꼴", Fonts.List(), () => J.Str(m, "font", "Malgun Gothic"), v => { m["font"] = v; Upd(); }));
        ctl.Controls.Add(Opt.Slider("크기", 6, 400, () => J.Num(m, "size", 42), v => { m["size"] = Math.Round(v); Upd(); }, log: true));
        ctl.Controls.Add(Opt.Slider("줄 간격", 0.8, 2.5, () => J.Num(m, "lineHeight", 1.3), v => { m["lineHeight"] = Math.Round(v, 2); Upd(); }, step: 0.05));
        ctl.Controls.Add(Opt.Slider("자간", -30, 100, () => J.Num(m, "tracking"), v => { m["tracking"] = Math.Round(v); Upd(); }, step: 1));
        ctl.Controls.Add(Opt.Check("문장부호 좁게 (…, !?, 「」 앞뒤를 당김)", () => J.Bool(m, "kern"), v => { m["kern"] = v; Upd(); }));
        ctl.Controls.Add(Opt.Help("루비(읽는 법): {漢字|かんじ} 처럼 쓰면 글자 위(세로쓰기는 오른쪽)에 작게 붙습니다."));
        ctl.Controls.Add(Opt.Row(Opt.Check("굵게", () => J.Bool(m, "bold", true), v => { m["bold"] = v; Upd(); }), Opt.Check("기울임", () => J.Bool(m, "italic"), v => { m["italic"] = v; Upd(); }), Opt.Check("세로쓰기", () => J.Bool(m, "vertical"), v => { m["vertical"] = v; Upd(); })));
        ctl.Controls.Add(CUI.Seg(new[] { ("left", "왼쪽"), ("center", "가운데"), ("right", "오른쪽") }, () => J.Str(m, "align", "center"), v =>
        {
            // 상자 위치를 유지하도록 기준점 이동
            var b = (m["lbox"] ?? m["box"]) as JsonObject; double sw = J.Num(m, "strokeWidth");
            if (b != null && !J.Bool(m, "vertical"))
                m["x"] = J.R(v == "left" ? J.Num(b, "x") + sw + 4 : v == "center" ? J.Num(b, "x") + J.Num(b, "w") / 2 : J.Num(b, "x") + J.Num(b, "w") - sw - 4);
            m["align"] = v; Upd();
        }));
        ctl.Controls.Add(Opt.Row(CUI.ColorBtn("글자색", () => J.Str(m, "color", "#000000"), v => { m["color"] = v; Upd(); }), CUI.ColorBtn("외곽선", () => J.Str(m, "strokeColor", "#ffffff"), v => { m["strokeColor"] = v; Upd(); })));
        ctl.Controls.Add(Opt.Slider("외곽선 굵기(효과음용)", 0, 30, () => J.Num(m, "strokeWidth"), v => { m["strokeWidth"] = Math.Round(v * 2) / 2; Upd(); }, step: 0.5));
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true, BackColor = Opt.Accent, ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, FlatStyle = FlatStyle.Flat };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = Opt.Bg, Padding = new Padding(6) };
        bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
        f.Controls.Add(ctl); f.Controls.Add(ta); f.Controls.Add(bottom);
        f.CancelButton = cancel;
        f.Shown += (_, _) => { ta.Focus(); ta.SelectAll(); };
        bool res = f.ShowDialog(App.Main) == DialogResult.OK && !string.IsNullOrWhiteSpace(J.Str(m, "text", ""));
        if (!res) return false;
        l.Meta = J.Clone(m);
        string t = Regex.Replace(J.Str(m, "text", ""), @"\s+", " ");
        l.Name = "텍스트: " + (t.Length > 12 ? t[..12] : t);
        return true;
    }
}
