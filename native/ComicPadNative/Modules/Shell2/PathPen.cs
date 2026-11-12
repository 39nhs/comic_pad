// 펜 도구 (베지어 패스, 웹 tools/pathpen.js): 탭 = 꺾인 점, 끌기 = 부드러운 점(손잡이). 패스 → 선택 영역 / 선 그리기 / 칠하기.
// 패스는 문서 Extra["paths"] = [{ id, name, closed, pts: [{ x, y, ix, iy, ox, oy }] }] (웹 판 doc.paths 와 같은 모양, .cpad 에 그대로)
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.SelectFill;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public sealed class BPoint { public double X, Y, IX, IY, OX, OY; public BPoint Clone() => (BPoint)MemberwiseClone(); }
public sealed class BPath { public string Id, Name; public bool Closed; public List<BPoint> Pts = new(); }

public static class Paths
{
    static double N(JsonNode n, double fb) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : fb;

    public static List<BPath> Read(Document d)
    {
        var l = new List<BPath>();
        foreach (var o in (d?.Extra["paths"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var p = new BPath { Id = (string)o["id"] ?? U.Uid("pa"), Name = (string)o["name"] ?? "패스", Closed = o["closed"] is JsonValue c && c.TryGetValue<bool>(out var b) && b };
            foreach (var q in (o["pts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                double x = N(q["x"], 0), y = N(q["y"], 0);
                p.Pts.Add(new BPoint { X = x, Y = y, IX = N(q["ix"], x), IY = N(q["iy"], y), OX = N(q["ox"], x), OY = N(q["oy"], y) });
            }
            l.Add(p);
        }
        return l;
    }

    public static JsonArray ToJson(List<BPath> l) => new(l.Select(p => (JsonNode)new JsonObject
    {
        ["id"] = p.Id, ["name"] = p.Name, ["closed"] = p.Closed,
        ["pts"] = new JsonArray(p.Pts.Select(q => (JsonNode)new JsonObject { ["x"] = R(q.X), ["y"] = R(q.Y), ["ix"] = R(q.IX), ["iy"] = R(q.IY), ["ox"] = R(q.OX), ["oy"] = R(q.OY) }).ToArray()),
    }).ToArray());
    static double R(double v) => Math.Round(v, 2);

    /// <summary>실행취소 가능하게 패스 목록 바꾸기</summary>
    public static void Save(Document d, List<BPath> l, string label)
    {
        var next = ToJson(l);
        if (U.Same(d.Extra["paths"], next)) return;
        ExtraEdit.Set(d, "paths", next, label);
        Changed?.Invoke();
    }
    public static event Action Changed;

    /// <summary>패스를 따라 촘촘한 점들 (간격 step px)</summary>
    public static List<PointF> Sample(BPath p, double step = 2)
    {
        var o = new List<PointF>(); var a = p.Pts;
        if (a.Count == 0) return o;
        var segs = new List<(BPoint, BPoint)>();
        for (int i = 1; i < a.Count; i++) segs.Add((a[i - 1], a[i]));
        if (p.Closed && a.Count > 2) segs.Add((a[^1], a[0]));
        if (segs.Count == 0) { o.Add(new PointF((float)a[0].X, (float)a[0].Y)); return o; }
        foreach (var (q, r) in segs)
        {
            double[] P0 = { q.X, q.Y }, P1 = { q.OX, q.OY }, P2 = { r.IX, r.IY }, P3 = { r.X, r.Y };
            double len = Math.Sqrt(Math.Pow(P1[0] - P0[0], 2) + Math.Pow(P1[1] - P0[1], 2)) + Math.Sqrt(Math.Pow(P2[0] - P1[0], 2) + Math.Pow(P2[1] - P1[1], 2)) + Math.Sqrt(Math.Pow(P3[0] - P2[0], 2) + Math.Pow(P3[1] - P2[1], 2));
            int n = Math.Max(2, (int)Math.Ceiling(len / step));
            for (int k = o.Count > 0 ? 1 : 0; k <= n; k++)
            {
                double t = k / (double)n, u = 1 - t;
                o.Add(new PointF((float)(u * u * u * P0[0] + 3 * u * u * t * P1[0] + 3 * u * t * t * P2[0] + t * t * t * P3[0]), (float)(u * u * u * P0[1] + 3 * u * u * t * P1[1] + 3 * u * t * t * P2[1] + t * t * t * P3[1])));
            }
        }
        return o;
    }

    public static GraphicsPath ToGdi(BPath p, bool close, float ox = 0, float oy = 0)
    {
        var g = new GraphicsPath(); var a = p.Pts;
        if (a.Count < 2) return g;
        PointF P(double x, double y) => new((float)x - ox, (float)y - oy);
        for (int i = 1; i < a.Count; i++) g.AddBezier(P(a[i - 1].X, a[i - 1].Y), P(a[i - 1].OX, a[i - 1].OY), P(a[i].IX, a[i].IY), P(a[i].X, a[i].Y));
        if ((close || p.Closed) && a.Count > 2) g.AddBezier(P(a[^1].X, a[^1].Y), P(a[^1].OX, a[^1].OY), P(a[0].IX, a[0].IY), P(a[0].X, a[0].Y));
        g.CloseFigure();
        return g;
    }

    static Rect BoundsOf(BPath p, int pad)
    {
        var s = Sample(p, true, 4);
        if (s.Count == 0) return Rect.Empty;
        return Rect.FromLTRB((int)Math.Floor(s.Min(q => q.X)) - pad, (int)Math.Floor(s.Min(q => q.Y)) - pad, (int)Math.Ceiling(s.Max(q => q.X)) + pad + 1, (int)Math.Ceiling(s.Max(q => q.Y)) + pad + 1);
    }
    static List<PointF> Sample(BPath p, bool with_closed, double step) => Sample(new BPath { Closed = with_closed || p.Closed, Pts = p.Pts }, step);

    // ---- 패스 명령 ---------------------------------------------------------------------------------
    public static void ToSelection(BPath p, string op = "replace")
    {
        var d = App.Doc;
        if (p == null || p.Pts.Count < 2) { App.Toast("점이 2개 이상인 패스가 필요합니다"); return; }
        var r = BoundsOf(p, 2).Clip(d.W, d.H);
        if (r.IsEmpty) return;
        var shape = SelUtil.RasterShape(r, g => { using var gp = ToGdi(p, true, r.X, r.Y); g.FillPath(Brushes.White, gp); });
        SelUtil.SelHistory("패스 → 선택 영역", () => SelUtil.ApplyShape(d.Sel, r, shape, op));
        SelUtil.RedrawSel();
    }

    /// <summary>전경색으로 칠하기</summary>
    public static void Fill(BPath p)
    {
        var d = App.Doc; var l = d?.Active;
        if (p == null || p.Pts.Count < 3) { App.Toast("점이 3개 이상인 패스가 필요합니다"); return; }
        if (!App.CheckEditable(l)) return;
        var r = BoundsOf(p, 2).Clip(d.W, d.H);
        if (r.IsEmpty) return;
        var shape = SelUtil.RasterShape(r, g => { using var gp = ToGdi(p, true, r.X, r.Y); g.FillPath(Brushes.White, gp); });
        var t = l.Target;
        var pe = App.History.BeginPixels(l, "패스 칠하기");
        pe.Touch(r);
        var buf = t.Read(r);
        uint col = l.EditMask ? 0xFFFFFFFF : App.Fg;
        for (int i = 0; i < buf.Length; i++) { byte s = shape[i]; if (s == 0) continue; buf[i] = l.AlphaLock ? Px.Composite(Px.Scale(col, s), buf[i], Core.Blend.Atop) : Px.Over(Px.Scale(col, s), buf[i]); }
        t.Write(r, buf);
        pe.Commit();
        d.Modified = true; l.Touch();
        App.Invalidate(r); App.NotifyLayers();
    }

    /// <summary>지금 브러시로 패스 따라 그리기 (필압: 시작·끝을 가늘게)</summary>
    public static void Stroke(BPath p, bool taper = true)
    {
        var d = App.Doc; var l = d?.Active;
        if (p == null || p.Pts.Count < 2) { App.Toast("점이 2개 이상인 패스가 필요합니다"); return; }
        if (l?.Kind != "vector" && !App.CheckEditable(l)) return;
        var pre = PresetStore.Active("brush"); if (pre == null) return;
        var pts = Sample(p, Math.Max(1, pre.Size * 0.15));
        var arr = new JsonArray();
        for (int i = 0; i < pts.Count; i++)
        {
            double t = i / (double)Math.Max(1, pts.Count - 1);
            double pr = taper ? Math.Min(1, Math.Sin(Math.PI * t) * 1.6 + 0.05) : 1;
            arr.Add(Math.Round(pts[i].X, 2)); arr.Add(Math.Round(pts[i].Y, 2)); arr.Add(Math.Round(pr, 3)); arr.Add(i * 8);
        }
        var stroke = new JsonObject { ["preset"] = VectorLayer.PresetSnapshot(pre), ["color"] = Px.ToHex(App.Fg), ["pts"] = arr };
        if (l.Kind == "vector")
        {
            var next = (JsonArray)VectorLayer.Strokes(l).DeepClone(); next.Add(stroke);
            VectorLayer.SetStrokes(l, next, "패스 선 그리기");
            return;
        }
        var r = BoundsOf(p, (int)Math.Ceiling(pre.Size) + 4).Clip(d.W, d.H);
        if (r.IsEmpty) return;
        var pe = App.History.BeginPixels(l, "패스 선 그리기");
        pe.Touch(r);
        var drawn = VectorLayer.DrawStroke(l, d, stroke);
        if (drawn.IsEmpty) { pe.Cancel(); return; }
        pe.Commit();
        d.Modified = true; l.Touch();
        App.Invalidate(r); App.NotifyLayers();
    }

    /// <summary>선택 영역 테두리 → 패스 (가장 바깥 윤곽, 점 줄이고 부드러운 곡선)</summary>
    public static BPath FromSelection(double tolerance = 2)
    {
        var d = App.Doc; var sel = d?.Sel;
        if (sel?.Mask == null) { App.Toast("선택 영역이 없습니다"); return null; }
        var b = SelUtil.Bounds(sel);
        if (b.IsEmpty) return null;
        int W = b.W + 2, H = b.H + 2;
        bool Inside(int x, int y) { int gx = x + b.X - 1, gy = y + b.Y - 1; return x >= 0 && y >= 0 && x < W && y < H && (uint)gx < (uint)d.W && (uint)gy < (uint)d.H && sel.Mask[gy * d.W + gx] > 127; }
        int sx = -1, sy = -1;
        for (int y = 0; y < H && sx < 0; y++) for (int x = 0; x < W; x++) if (Inside(x, y)) { sx = x; sy = y; break; }
        if (sx < 0) return null;
        int[][] dirs = { new[] { 1, 0 }, new[] { 1, 1 }, new[] { 0, 1 }, new[] { -1, 1 }, new[] { -1, 0 }, new[] { -1, -1 }, new[] { 0, -1 }, new[] { 1, -1 } };
        var contour = new List<(int x, int y)> { (sx, sy) };
        int cx = sx, cy = sy, dir = 6; long guard = 0;
        do
        {
            bool found = false;
            for (int k = 0; k < 8; k++)
            {
                int nd = (dir + 6 + k) % 8, nx = cx + dirs[nd][0], ny = cy + dirs[nd][1];
                if (Inside(nx, ny)) { cx = nx; cy = ny; dir = nd; found = true; break; }
            }
            if (!found) break;
            contour.Add((cx, cy));
        } while ((cx != sx || cy != sy) && guard++ < (long)W * H * 4);
        List<(int x, int y)> Rdp(List<(int x, int y)> pts, double eps)
        {
            if (pts.Count < 3) return pts;
            var (ax, ay) = pts[0]; var (bx, by) = pts[^1];
            double md = 0; int mi = 0, L2 = 0;
            double L = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)); if (L == 0) L = 1;
            for (int i = 1; i < pts.Count - 1; i++) { var (px, py) = pts[i]; double dd = Math.Abs((bx - ax) * (ay - py) - (ax - px) * (by - ay)) / L; if (dd > md) { md = dd; mi = i; } }
            _ = L2;
            if (md <= eps) return new() { pts[0], pts[^1] };
            var left = Rdp(pts.GetRange(0, mi + 1), eps); var right = Rdp(pts.GetRange(mi, pts.Count - mi), eps);
            left.RemoveAt(left.Count - 1); left.AddRange(right); return left;
        }
        int far = 0; long fd = -1;
        for (int i = 0; i < contour.Count; i++) { long d2 = (long)(contour[i].x - sx) * (contour[i].x - sx) + (long)(contour[i].y - sy) * (contour[i].y - sy); if (d2 > fd) { fd = d2; far = i; } }
        List<(int x, int y)> simp;
        if (far > 0) { simp = Rdp(contour.GetRange(0, far + 1), tolerance); simp.RemoveAt(simp.Count - 1); simp.AddRange(Rdp(contour.GetRange(far, contour.Count - far), tolerance)); }
        else simp = contour;
        if (simp.Count > 2 && simp[0] == simp[^1]) simp.RemoveAt(simp.Count - 1);
        int n = simp.Count;
        var pts2 = simp.Select(q => new BPoint { X = q.x + b.X - 1 + 0.5, Y = q.y + b.Y - 1 + 0.5 }).ToList();
        for (int i = 0; i < n; i++)
        {
            var p0 = pts2[(i - 1 + n) % n]; var p1 = pts2[i]; var p2 = pts2[(i + 1) % n];
            double a1 = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X), a2 = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
            double turn = Math.Abs(a2 - a1); if (turn > Math.PI) turn = 2 * Math.PI - turn;
            if (turn > 0.9) { p1.IX = p1.OX = p1.X; p1.IY = p1.OY = p1.Y; continue; }
            double tx = (p2.X - p0.X) / 6, ty = (p2.Y - p0.Y) / 6;
            p1.IX = p1.X - tx; p1.IY = p1.Y - ty; p1.OX = p1.X + tx; p1.OY = p1.Y + ty;
        }
        var list = Read(d);
        var path = new BPath { Id = U.Uid("pa"), Name = $"패스 {list.Count + 1}", Closed = true, Pts = pts2 };
        list.Add(path);
        Save(d, list, "선택 영역 → 패스");
        PathPenTool.Cur = path.Id;
        return path;
    }

    public static BPath CurrentOrLast() { var l = Read(App.Doc); return l.Find(p => p.Id == PathPenTool.Cur) ?? l.LastOrDefault(); }
}

public sealed class PathPenTool : ToolBase
{
    public static string Cur;
    public override string Id => "pathpen";
    public override string Name => "펜 (패스)";
    public override string Icon => "✒";
    public override bool AllowAltPick => false;
    public override bool Busy => drag != null;

    sealed class Drag { public string Kind; public int I; public double X0, Y0; public BPoint Orig; }
    Drag drag; List<BPath> work; JsonNode before;
    (int i, long t)? lastTap;

    BPath Path(List<BPath> l) => l.Find(p => p.Id == Cur);

    (string kind, int i)? Hit(PointerArgs e, BPath p, ViewXf v)
    {
        if (p == null) return null;
        bool Near(double x, double y) { var s = v.ToScreen(x, y); return Math.Sqrt((s.x - e.SX) * (s.x - e.SX) + (s.y - e.SY) * (s.y - e.SY)) < 9; }
        for (int i = 0; i < p.Pts.Count; i++)
        {
            var q = p.Pts[i];
            if ((q.OX != q.X || q.OY != q.Y) && Near(q.OX, q.OY)) return ("out", i);
            if ((q.IX != q.X || q.IY != q.Y) && Near(q.IX, q.IY)) return ("in", i);
        }
        for (int i = 0; i < p.Pts.Count; i++) if (Near(p.Pts[i].X, p.Pts[i].Y)) return ("anchor", i);
        return null;
    }

    static ViewXf View => Shell2Ui.Canvas?.V ?? new ViewXf();

    public override void Down(PointerArgs e)
    {
        var d = App.Doc; if (d == null) return;
        before = d.Extra["paths"]?.DeepClone();
        work = Paths.Read(d);
        var p = Path(work);
        var hit = Hit(e, p, View);
        long now = Environment.TickCount64;
        if (hit?.kind == "anchor")
        {
            int i = hit.Value.i;
            bool dbl = lastTap?.i == i && now - lastTap.Value.t < 350;
            lastTap = (i, now);
            if (e.Alt) { p.Pts.RemoveAt(i); Commit("패스 점 지우기"); return; }
            if (i == 0 && !p.Closed && p.Pts.Count > 2) { p.Closed = true; Commit("패스 닫기"); return; }
            if (dbl)
            {
                var q = p.Pts[i];
                if (q.OX == q.X && q.OY == q.Y)
                {
                    var a = i > 0 ? p.Pts[i - 1] : p.Pts[^1]; var c = i + 1 < p.Pts.Count ? p.Pts[i + 1] : p.Pts[0];
                    double tx = (c.X - a.X) / 6, ty = (c.Y - a.Y) / 6;
                    q.IX = q.X - tx; q.IY = q.Y - ty; q.OX = q.X + tx; q.OY = q.Y + ty;
                }
                else { q.IX = q.OX = q.X; q.IY = q.OY = q.Y; }
                Commit("점 모양 바꾸기");
                return;
            }
            drag = new Drag { Kind = "anchor", I = i, X0 = e.X, Y0 = e.Y, Orig = p.Pts[i].Clone() };
            return;
        }
        if (hit != null) { drag = new Drag { Kind = hit.Value.kind, I = hit.Value.i }; return; }
        if (p == null || p.Closed)
        {
            p = new BPath { Id = U.Uid("pa"), Name = $"패스 {work.Count + 1}" };
            work.Add(p); Cur = p.Id;
        }
        p.Pts.Add(new BPoint { X = e.X, Y = e.Y, IX = e.X, IY = e.Y, OX = e.X, OY = e.Y });
        drag = new Drag { Kind = "new", I = p.Pts.Count - 1 };
        d.Extra["paths"] = Paths.ToJson(work);
    }

    public override void Move(PointerArgs e)
    {
        var p = work == null ? null : Path(work);
        if (drag == null || p == null || drag.I >= p.Pts.Count) return;
        var q = p.Pts[drag.I];
        if (drag.Kind == "anchor")
        {
            double dx = e.X - drag.X0, dy = e.Y - drag.Y0; var o = drag.Orig;
            q.X = o.X + dx; q.Y = o.Y + dy; q.IX = o.IX + dx; q.IY = o.IY + dy; q.OX = o.OX + dx; q.OY = o.OY + dy;
        }
        else if (drag.Kind is "new" or "out") { q.OX = e.X; q.OY = e.Y; if (!e.Alt) { q.IX = 2 * q.X - e.X; q.IY = 2 * q.Y - e.Y; } }
        else if (drag.Kind == "in") { q.IX = e.X; q.IY = e.Y; if (!e.Alt) { q.OX = 2 * q.X - e.X; q.OY = 2 * q.Y - e.Y; } }
        App.Doc.Extra["paths"] = Paths.ToJson(work);
    }

    public override void Up(PointerArgs e)
    {
        if (drag == null) return;
        string k = drag.Kind; drag = null;
        Commit(k == "new" ? "패스 점 추가" : "패스 고치기");
    }

    void Commit(string label)
    {
        var d = App.Doc;
        var next = Paths.ToJson(work);
        if (before != null) d.Extra["paths"] = before.DeepClone(); else d.Extra.Remove("paths");
        Paths.Save(d, work, label);
        before = null;
        Shell2Ui.Canvas?.RedrawAll();
        options?.Invoke();
    }

    public override void Cancel() { drag = null; if (App.Doc != null) { if (before != null) App.Doc.Extra["paths"] = before.DeepClone(); } }

    public override bool HandleKey(Keys k)
    {
        if (k is Keys.Enter or Keys.Escape) { Cur = null; Shell2Ui.Canvas?.RedrawAll(); options?.Invoke(); return true; }
        if (k is Keys.Delete or Keys.Back && Cur != null && App.Doc != null)
        {
            var l = Paths.Read(App.Doc); l.RemoveAll(p => p.Id == Cur); Cur = null;
            Paths.Save(App.Doc, l, "패스 지우기"); Shell2Ui.Canvas?.RedrawAll(); options?.Invoke();
            return true;
        }
        return false;
    }

    public override Rectangle OverlayBounds(ViewXf v)
    {
        var d = App.Doc; if (d == null || d.Extra["paths"] is not JsonArray a || a.Count == 0) return Rectangle.Empty;
        var c = Shell2Ui.Canvas; return c == null ? Rectangle.Empty : new Rectangle(0, 0, c.Width, c.Height);
    }

    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var d = App.Doc; if (d == null) return;
        foreach (var p in Paths.Read(d))
        {
            bool on = p.Id == Cur;
            var pts = Paths.Sample(p, 3 / Math.Max(0.05, v.Zoom)).Select(q => { var s = v.ToScreen(q.X, q.Y); return new PointF((float)s.x, (float)s.y); }).ToArray();
            if (pts.Length >= 2)
            {
                using var w = new Pen(Color.FromArgb(200, 255, 255, 255), on ? 3f : 2.2f);
                using var b = new Pen(on ? Color.FromArgb(47, 124, 246) : Color.FromArgb(102, 102, 102), on ? 1.4f : 1f) { DashStyle = on ? DashStyle.Solid : DashStyle.Dash };
                if (p.Closed) { g.DrawPolygon(w, pts); g.DrawPolygon(b, pts); } else { g.DrawLines(w, pts); g.DrawLines(b, pts); }
            }
            if (!on) continue;
            using var hp = new Pen(Color.FromArgb(47, 124, 246));
            using var fb = new SolidBrush(Color.FromArgb(47, 124, 246));
            foreach (var q in p.Pts)
            {
                var s = v.ToScreen(q.X, q.Y);
                foreach (var (hx, hy) in new[] { (q.IX, q.IY), (q.OX, q.OY) })
                {
                    if (hx == q.X && hy == q.Y) continue;
                    var t = v.ToScreen(hx, hy);
                    g.DrawLine(hp, (float)s.x, (float)s.y, (float)t.x, (float)t.y);
                    g.FillEllipse(Brushes.White, (float)t.x - 3.5f, (float)t.y - 3.5f, 7, 7); g.DrawEllipse(hp, (float)t.x - 3.5f, (float)t.y - 3.5f, 7, 7);
                }
                g.FillRectangle(fb, (float)s.x - 4, (float)s.y - 4, 8, 8); g.DrawRectangle(Pens.White, (float)s.x - 4, (float)s.y - 4, 8, 8);
            }
        }
    }

    Action options;
    public override Control BuildOptions()
    {
        var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Opt.Bg, Padding = new Padding(6) };
        void Render()
        {
            if (box.IsDisposed) return;
            box.SuspendLayout();
            foreach (Control c in box.Controls.Cast<Control>().ToList()) c.Dispose();
            var list = App.Doc == null ? new List<BPath>() : Paths.Read(App.Doc);
            box.Controls.Add(Opt.Title("패스"));
            foreach (var p in list)
            {
                var pp = p;
                box.Controls.Add(Opt.Button($"{(p.Id == Cur ? "● " : "")}{p.Name} (점 {p.Pts.Count}{(p.Closed ? ", 닫힘" : "")})", () => { Cur = pp.Id; Render(); Shell2Ui.Canvas?.RedrawAll(); }, p.Id == Cur));
            }
            if (list.Count == 0) box.Controls.Add(Opt.Help("아직 패스가 없습니다. 그림 위를 탭(꺾인 점)하거나 끌어(곡선) 점을 찍으세요."));
            BPath P() { var x = Paths.Read(App.Doc).Find(q => q.Id == Cur); if (x == null) App.Toast("패스를 먼저 고르세요"); return x; }
            box.Controls.Add(Opt.Row(Opt.Button("새 패스", () => { Cur = null; Render(); }), Opt.Button("이름", () =>
            {
                var p = P(); if (p == null) return;
                var v = Dlg.Form("패스 이름", new[] { Field.Text("n", "이름", p.Name) });
                if (string.IsNullOrWhiteSpace((string)v?["n"])) return;
                var l = Paths.Read(App.Doc); l.Find(q => q.Id == p.Id).Name = (string)v["n"]; Paths.Save(App.Doc, l, "패스 이름"); Render();
            }), Opt.Button("지우기", () => { if (Cur == null) return; var l = Paths.Read(App.Doc); l.RemoveAll(q => q.Id == Cur); Cur = null; Paths.Save(App.Doc, l, "패스 지우기"); Shell2Ui.Canvas?.RedrawAll(); Render(); })));
            box.Controls.Add(Opt.Row(Opt.Button("선택 영역으로", () => Paths.ToSelection(P())), Opt.Button("선택에 더하기", () => Paths.ToSelection(P(), "add"))));
            box.Controls.Add(Opt.Row(Opt.Button("선택 → 패스", () => { Paths.FromSelection(); Render(); Shell2Ui.Canvas?.RedrawAll(); }), Opt.Button("선 그리기 (지금 브러시)", () => Paths.Stroke(P()))));
            box.Controls.Add(Opt.Row(Opt.Button("칠하기 (전경색)", () => Paths.Fill(P()))));
            box.Controls.Add(Opt.Help("탭 = 꺾인 점, 끌기 = 곡선 점. 첫 점을 탭하면 닫힘. 점 끌기 = 옮기기, 손잡이 끌기 = 곡선 (Alt = 한쪽만). 점 두 번 탭 = 꺾임↔곡선, Alt+탭 = 점 지우기, Enter = 이 패스 끝내기, Delete = 이 패스 지우기."));
            box.ResumeLayout();
        }
        options = () => { if (!box.IsDisposed && box.IsHandleCreated) box.BeginInvoke((Action)Render); };
        Render();
        return box;
    }
}
