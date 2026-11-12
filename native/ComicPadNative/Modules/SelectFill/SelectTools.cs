// 선택 도구 (웹 tools/select.js): 사각형 / 타원 / 올가미 / 자동 선택(허용 오차·틈 막기·영역 확장),
// 방식 새로·추가·빼기·교차 (Shift = 추가, Alt = 빼기, Shift+Alt = 교차), 빈 곳 탭 = 선택 해제.
// 선택 펜 (웹 tools/paint2.js selPenTool): 브러시로 칠한 곳을 선택에 더하기/빼기 (Alt = 빼기).
using System.Drawing.Drawing2D;
using ComicPadNative.Core;
using ComicPadNative.Engine;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.SelectFill;

public sealed class SelectTool : ToolBase
{
    public override string Id => "select";
    public override string Name => "선택";
    public override string Icon => "⬚";
    public override Keys Key => Keys.M;
    public override bool AllowAltPick => false;

    // 옵션 (웹 opt 와 같은 기본값)
    public string Shape = SelUtil.GetS("select.shape", "rect");      // rect | ellipse | lasso | wand
    public string Op = SelUtil.GetS("select.op", "replace");         // replace | add | subtract | intersect
    public string Source = SelUtil.GetS("select.source", "all");     // all | ref | layer
    public double Tolerance = SelUtil.GetD("select.tolerance", 12), Gap = SelUtil.GetD("select.gap", 0), Expand = SelUtil.GetD("select.expand", 0);
    public bool KeepRatio;

    sealed class Drag { public string Op; public double X0, Y0, SX0, SY0, X1, Y1; public List<PointF> Pts = new(); public bool Moved; }
    Drag drag;
    bool working;
    public override bool Busy => drag != null;

    string OpOf(PointerArgs e) => e.Shift && e.Alt ? "intersect" : e.Shift ? "add" : e.Alt ? "subtract" : Op;
    static double Zoom => (App.Main as MainForm)?.Canvas.V.Zoom ?? 1;

    public override async void Down(PointerArgs e)
    {
        var doc = App.Doc;
        if (doc == null) return;
        string op = OpOf(e);
        if (Shape == "wand")
        {
            int x = (int)Math.Floor(e.X), y = (int)Math.Floor(e.Y);
            if (x < 0 || y < 0 || x >= doc.W || y >= doc.H || working) return;
            working = true;
            try
            {
                var img = SelUtil.ReferenceImage(Source);
                var fo = new Raster.FillOpt { Tolerance = Tolerance, Gap = Gap, Expand = Expand };
                var res = await Task.Run(() => Raster.FillMask(img, doc.W, doc.H, x, y, fo));
                if (!res.bounds.IsEmpty && App.Doc == doc) SelUtil.SelHistory("선택 영역", () => SelUtil.FromMask(doc.Sel, res.mask, res.bounds, op));
            }
            catch (Exception ex) { App.Toast("자동 선택: " + ex.Message); }
            finally { working = false; }
            return;
        }
        drag = new Drag { Op = op, X0 = e.X, Y0 = e.Y, SX0 = e.SX, SY0 = e.SY, X1 = e.X, Y1 = e.Y };
        drag.Pts.Add(new PointF((float)e.X, (float)e.Y));
    }

    public override void Move(PointerArgs e)
    {
        var d = drag;
        if (d == null) return;
        if (Math.Sqrt((e.SX - d.SX0) * (e.SX - d.SX0) + (e.SY - d.SY0) * (e.SY - d.SY0)) > 4) d.Moved = true;
        double x = e.X, y = e.Y;
        if ((KeepRatio || e.Shift) && Shape != "lasso")
        {
            double s = Math.Max(Math.Abs(x - d.X0), Math.Abs(y - d.Y0));
            x = d.X0 + Sign(x - d.X0) * s; y = d.Y0 + Sign(y - d.Y0) * s;
        }
        d.X1 = x; d.Y1 = y;
        if (Shape == "lasso")
        {
            var last = d.Pts[^1];
            if (Math.Sqrt((e.X - last.X) * (e.X - last.X) + (e.Y - last.Y) * (e.Y - last.Y)) * Zoom > 2) d.Pts.Add(new PointF((float)e.X, (float)e.Y));
        }
    }
    static double Sign(double v) => v < 0 ? -1 : 1;   // Math.sign(v || 1)

    public override void Up(PointerArgs e)
    {
        var d = drag;
        drag = null;
        if (d == null) return;
        var sel = App.Doc.Sel;
        if (!d.Moved)
        {
            if (d.Op == "replace" && SelUtil.Active(sel)) SelUtil.SelHistory("선택 영역", () => sel.None());
            return;
        }
        if (Shape == "lasso")
        {
            if (d.Pts.Count < 3) return;
            var r = SelUtil.PointsBounds(d.Pts, 1);
            SelUtil.SelHistory("선택 영역", () => SelUtil.ApplyShape(sel, r, SelUtil.PolygonShape(r, d.Pts), d.Op));
            return;
        }
        double rx = Math.Min(d.X0, d.X1), ry = Math.Min(d.Y0, d.Y1), rw = Math.Abs(d.X1 - d.X0), rh = Math.Abs(d.Y1 - d.Y0);
        if (rw < 1 || rh < 1) return;
        if (Shape == "ellipse")
        {
            var r = Rect.FromPoints(rx, ry, rx + rw, ry + rh, 1);
            SelUtil.SelHistory("선택 영역", () => SelUtil.ApplyShape(sel, r, SelUtil.EllipseShape(r, rx, ry, rw, rh), d.Op));
        }
        else
        {
            var r = new Rect(Raster.JsRound(rx), Raster.JsRound(ry), Raster.JsRound(rw), Raster.JsRound(rh));
            var shape = new byte[r.W * r.H];
            Array.Fill(shape, (byte)255);
            SelUtil.SelHistory("선택 영역", () => SelUtil.ApplyShape(sel, r, shape, d.Op));
        }
    }

    public override void Cancel() { drag = null; }

    // ---- 화면 표시 ------------------------------------------------------------------------
    List<PointF> ScreenPath(ViewXf v)
    {
        var d = drag;
        var o = new List<PointF>();
        if (d == null || !d.Moved) return o;
        PointF S(double x, double y) { var (sx, sy) = v.ToScreen(x, y); return new PointF((float)sx, (float)sy); }
        if (Shape == "lasso") { foreach (var p in d.Pts) o.Add(S(p.X, p.Y)); return o; }
        double rx = Math.Min(d.X0, d.X1), ry = Math.Min(d.Y0, d.Y1), rw = Math.Abs(d.X1 - d.X0), rh = Math.Abs(d.Y1 - d.Y0);
        if (Shape == "ellipse")
        {
            const int n = 64;
            for (int i = 0; i <= n; i++) { double a = (double)i / n * Math.PI * 2; o.Add(S(rx + rw / 2 + Math.Cos(a) * rw / 2, ry + rh / 2 + Math.Sin(a) * rh / 2)); }
        }
        else { o.Add(S(rx, ry)); o.Add(S(rx + rw, ry)); o.Add(S(rx + rw, ry + rh)); o.Add(S(rx, ry + rh)); }
        return o;
    }

    public override void DrawOverlay(Graphics g, ViewXf v) => DrawMarquee(g, ScreenPath(v));

    public static void DrawMarquee(Graphics g, List<PointF> p, Color? fill = null, bool fillPath = true)
    {
        if (p.Count < 2) return;
        var arr = p.ToArray();
        if (fillPath && p.Count >= 3) { using var br = new SolidBrush(fill ?? Color.FromArgb(31, 60, 130, 255)); g.FillPolygon(br, arr, FillMode.Winding); }
        using var p1 = new Pen(Color.Black, 1.2f) { DashPattern = new[] { 4f / 1.2f, 4f / 1.2f } };
        using var p2 = new Pen(Color.White, 1.2f) { DashPattern = new[] { 4f / 1.2f, 4f / 1.2f }, DashOffset = 4f / 1.2f };
        g.DrawPolygon(p1, arr); g.DrawPolygon(p2, arr);
    }

    public override Rectangle OverlayBounds(ViewXf v)
    {
        var p = ScreenPath(v);
        return Bounds(p, 3);
    }

    public static Rectangle Bounds(List<PointF> p, int pad)
    {
        if (p.Count == 0) return Rectangle.Empty;
        float x0 = p.Min(q => q.X), y0 = p.Min(q => q.Y), x1 = p.Max(q => q.X), y1 = p.Max(q => q.Y);
        return Rectangle.FromLTRB((int)Math.Floor(x0) - pad, (int)Math.Floor(y0) - pad, (int)Math.Ceiling(x1) + pad, (int)Math.Ceiling(y1) + pad);
    }

    // ---- 옵션 -----------------------------------------------------------------------------
    public override Control BuildOptions()
    {
        var host = (FlowLayoutPanel)Opt.Panel();
        void Rebuild()
        {
            foreach (Control c in host.Controls) c.Dispose();
            host.Controls.Clear();
            bool wand = Shape == "wand";
            host.Controls.Add(Opt.Panel(
                Opt.Title("선택 방식"),
                Ui.Segmented(new[] { ("rect", "사각형"), ("ellipse", "타원"), ("lasso", "올가미"), ("wand", "자동 선택") }, () => Shape, v => { Shape = v; SelUtil.Put("select.shape", v); host.BeginInvoke(Rebuild); }),
                Ui.Segmented(new[] { ("replace", "새로"), ("add", "추가"), ("subtract", "빼기"), ("intersect", "교차") }, () => Op, v => { Op = v; SelUtil.Put("select.op", v); }),
                wand ? Ui.Segmented(new[] { ("all", "모든 레이어"), ("ref", "참조 레이어"), ("layer", "현재 레이어") }, () => Source, v => { Source = v; SelUtil.Put("select.source", v); }) : null,
                wand ? Opt.Slider("색 허용 오차", 0, 255, () => Tolerance, v => { Tolerance = v; SelUtil.Put("select.tolerance", v); }, step: 1) : null,
                wand ? Opt.Slider("틈 막기 (이 너비까지 빈틈은 선으로)", 0, 50, () => Gap, v => { Gap = v; SelUtil.Put("select.gap", v); }, step: 1) : null,
                wand ? Opt.Slider("영역 확장", 0, 10, () => Expand, v => { Expand = v; SelUtil.Put("select.expand", v); }, step: 1) : null,
                Shape != "lasso" && !wand ? Opt.Check("가로세로 비율 고정", () => KeepRatio, v => KeepRatio = v) : null,
                Opt.Help("Shift: 추가 · Alt: 빼기 · 빈 곳 탭: 선택 해제. 선택하면 아래에 선택 메뉴가 나타납니다.")));
        }
        Rebuild();
        return host;
    }
}

/// <summary>선택 펜: 브러시로 칠한 곳이 선택됨 (Alt = 선택 지우기)</summary>
public sealed class SelPenTool : ToolBase
{
    public override string Id => "selpen";
    public override string Name => "선택 펜";
    public override string Icon => "🖍";
    public override Keys Key => Keys.Q;
    public override bool AllowAltPick => false;
    public override bool DrawsSizeCircle => true;
    public override double CircleSize => Size;

    public double Size = SelUtil.GetD("selpen.size", 40), Hardness = SelUtil.GetD("selpen.hardness", 1);
    public string Mode = SelUtil.GetS("selpen.mode", "add");    // add | sub

    sealed class St { public Surface Buf; public PixelEdit Edit; public Stroke Stroke; public bool Sub; public List<PointF> Pts = new(); }
    St st;
    public override bool Busy => st != null;

    public override void Down(PointerArgs e)
    {
        var doc = App.Doc;
        if (doc == null) return;
        // 선택 영역에 잘리지 않게 (noClip): 빈 선택의 임시 문서에 그림
        var tmpDoc = new Document(doc.W, doc.H);
        var buf = new Surface(doc.W, doc.H);
        var edit = new PixelEdit(new History(), new Layer(), buf, "선택 펜");
        var bp = new BrushParams { Size = Size, Hardness = Hardness, PressureSize = false, Stabilizer = 2 };
        st = new St { Buf = buf, Edit = edit, Sub = Mode == "sub" || e.Alt, Stroke = new Stroke(tmpDoc, edit, bp, 0xFFFFFFFF, false, false) { Zoom = (App.Main as MainForm)?.Canvas.V.Zoom ?? 1 } };
        st.Pts.Add(new PointF((float)e.X, (float)e.Y));
        st.Stroke.Begin(new Pt(e.X, e.Y, 1, e.Time));
    }
    public override void Move(PointerArgs e)
    {
        if (st == null) return;
        st.Pts.Add(new PointF((float)e.X, (float)e.Y));
        st.Stroke.Add(new Pt(e.X, e.Y, 1, e.Time));
    }
    public override void Up(PointerArgs e)
    {
        var s = st;
        if (s == null) return;
        st = null;
        s.Stroke.End(new Pt(e.X, e.Y, 1, e.Time));
        var r = s.Edit.Dirty.Clip(App.Doc.W, App.Doc.H);
        if (r.IsEmpty) return;
        var px = s.Buf.Read(r);
        var shape = new byte[px.Length];
        for (int i = 0; i < px.Length; i++) shape[i] = (byte)(px[i] >> 24);
        var sel = App.Doc.Sel;
        SelUtil.SelHistory("선택 펜", () => SelUtil.ApplyShape(sel, r, shape, s.Sub ? "subtract" : "add"));
    }
    public override void Cancel() { st = null; }

    List<PointF> ScreenPts(ViewXf v) => st == null ? new() : st.Pts.Select(p => { var (x, y) = v.ToScreen(p.X, p.Y); return new PointF((float)x, (float)y); }).ToList();
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var p = ScreenPts(v);
        if (p.Count == 0) return;
        float w = (float)Math.Max(1, Size * v.Zoom);
        using var pen = new Pen(Color.FromArgb(128, 255, 58, 106), w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (p.Count == 1) { using var b = new SolidBrush(pen.Color); g.FillEllipse(b, p[0].X - w / 2, p[0].Y - w / 2, w, w); }
        else g.DrawLines(pen, p.ToArray());
    }
    public override Rectangle OverlayBounds(ViewXf v) => SelectTool.Bounds(ScreenPts(v), (int)Math.Ceiling(Size * v.Zoom / 2) + 3);

    public override bool HandleKey(Keys k)
    {
        if (k == Keys.OemOpenBrackets) { Size = Math.Max(1, Size / 1.15); SelUtil.Put("selpen.size", Size); App.NotifySettings(); return true; }
        if (k == Keys.OemCloseBrackets) { Size = Math.Min(400, Size * 1.15); SelUtil.Put("selpen.size", Size); App.NotifySettings(); return true; }
        return false;
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("선택 펜"),
        Ui.Segmented(new[] { ("add", "선택 추가"), ("sub", "선택 지우기") }, () => Mode, v => { Mode = v; SelUtil.Put("selpen.mode", v); }),
        Opt.Slider("크기", 1, 400, () => Size, v => { Size = v; SelUtil.Put("selpen.size", v); }, log: true, step: 1),
        Opt.Slider("경도", 0, 1, () => Hardness, v => { Hardness = v; SelUtil.Put("selpen.hardness", v); }, pct: true, step: 0.01),
        Opt.Help("브러시로 칠한 곳이 선택됩니다. Alt를 누르고 칠하면 선택 지우기."));
}
