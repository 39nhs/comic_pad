// 벡터 선 고르기 (웹 tools/vselect.js + 클립스튜디오 "오브젝트" 처럼): Modules/Shell2/MoreTools.cs 에서 옮겨 와 늘림.
//  - 탭: 고르기 · Shift+탭: 여러 개 · 빈 곳을 끌기: 올가미로 고르기 (걸친 선 / 다 들어간 선)
//  - 고른 선: 테두리 상자 안을 끌면 이동, 모서리 = 크기 (Shift = 가로세로 따로), 위 동그라미 = 회전 (Shift = 15°)
//  - 선 하나만 고르면 흰 점을 끌어 모양 고치기 (예전 그대로)
//  - 옵션: 단순화·부드럽게·선 끝 연결·선 끝 다듬기·굵기·색·삭제·뒤집기
// Shell2 의 시험(Shell2Test)과 등록(Shell2Module)이 이 이름을 쓰므로 네임스페이스는 Shell2 그대로.
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.VectorTools;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

/// <summary>벡터 선 고르기: 탭·올가미로 고르고 옮기기·크기·회전·다듬기·굵기·색·삭제, 선 하나면 점을 끌어 모양 고치기</summary>
public sealed class VectorSelectTool : ToolBase
{
    public override string Id => "vselect";
    public override string Name => "벡터 선 고르기";
    public override string Icon => "➚";
    public override Keys Key => Keys.O;
    public override Cursor Cursor => Cursors.Default;
    public override bool AllowAltPick => false;
    public override bool Busy => drag != null;
    public readonly HashSet<int> Sel = new();
    public double Reach = 0.12;
    public bool LassoAll = App.Get("vt.lassoAll", false);
    public bool ScaleWidth = App.Get("vt.scaleWidth", true);

    enum Mode { Move, Point, Scale, Rotate, Lasso }
    sealed class Drag
    {
        public Layer L; public Mode M; public int? Point; public int Si; public double X0, Y0, SX0, SY0; public bool Moved; public JsonArray Before;
        public int Corner; public (double x, double y) Anchor, CornerPt, Center; public bool Shift, Additive;
        public List<(double x, double y)> Lasso;
    }
    Drag drag;
    string selLayer; int selCount = -1;       // 고른 선이 어느 레이어·몇 개짜리 선 목록 기준인지 (다른 곳에서 선이 늘거나 줄면 고른 것 풀기)

    static Layer LayerOf() { var l = App.Doc?.Active; return l?.Kind == "vector" ? l : null; }
    static double[] Pts(JsonNode s) => VectorLayer.Pts((JsonObject)s);
    static JsonArray PtsJson(double[] p) => new(p.Select(v => (JsonNode)Math.Round(v, 2)).ToArray());

    // ---- 고른 선 기록 --------------------------------------------------------------------------------------
    /// <summary>이 레이어에서 지금도 유효한 고른 선 (다른 편집으로 선 목록이 바뀌었으면 비움)</summary>
    public HashSet<int> SelectionFor(Layer l)
    {
        if (l == null || Sel.Count == 0) return Sel;
        if (selLayer != l.Id || VectorLayer.Strokes(l).Count != selCount) Sel.Clear();
        return Sel;
    }
    public void Restamp(Layer l) { selLayer = l?.Id; selCount = l == null ? -1 : VectorLayer.Strokes(l).Count; }
    public void SetSelection(Layer l, IEnumerable<int> idx) { Sel.Clear(); foreach (int i in idx) Sel.Add(i); Restamp(l); Refresh(); }
    void Validate() => SelectionFor(LayerOf());

    public override void Activate() { var l = LayerOf(); if (l == null || l.Id != selLayer) Sel.Clear(); else Validate(); }
    public override void Deactivate() { drag = null; Shell2Ui.Canvas?.RedrawAll(); }

    static List<int> Handles(double[] p) { int n = p.Length / 4, step = Math.Max(1, (int)Math.Ceiling(n / 24.0)); var o = new List<int>(); for (int i = 0; i < n; i += step) o.Add(i); if (o.Count == 0 || o[^1] != n - 1) o.Add(n - 1); return o; }

    // ---- 테두리 상자 -----------------------------------------------------------------------------------------
    /// <summary>고른 선의 상자 (문서 좌표, 화면에서 8px 여유)</summary>
    (double x0, double y0, double x1, double y1)? Box(Layer l, ViewXf v)
    {
        if (l == null || Sel.Count == 0) return null;
        var strokes = VectorLayer.Strokes(l);
        var list = Sel.Where(i => i < strokes.Count).Select(i => (JsonObject)strokes[i]).ToList();
        if (list.Count == 0) return null;
        var b = VecOps.Bounds(list);
        if (b.x1 < b.x0) return null;
        double pad = 8 / Math.Max(0.01, v.Zoom) + list.Max(VecOps.SizeOf) / 2;
        return (b.x0 - pad, b.y0 - pad, b.x1 + pad, b.y1 + pad);
    }
    static (double x, double y)[] Corners((double x0, double y0, double x1, double y1) b) => new[] { (b.x0, b.y0), (b.x1, b.y0), (b.x1, b.y1), (b.x0, b.y1) };
    static (double x, double y) RotHandle((double x0, double y0, double x1, double y1) b, ViewXf v)
    {
        // 위쪽 가운데에서 화면 26px 위 (화면 회전과 상관없이 상자 바깥쪽)
        var (tx, ty) = v.ToScreen((b.x0 + b.x1) / 2, b.y0);
        var (cx, cy) = v.ToScreen((b.x0 + b.x1) / 2, (b.y0 + b.y1) / 2);
        double dx = tx - cx, dy = ty - cy, L = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        return v.ToDoc(tx + dx / L * 26, ty + dy / L * 26);
    }
    static double ScreenDist(ViewXf v, (double x, double y) doc, double sx, double sy) { var q = v.ToScreen(doc.x, doc.y); return Math.Sqrt((q.x - sx) * (q.x - sx) + (q.y - sy) * (q.y - sy)); }

    // ---- 포인터 ----------------------------------------------------------------------------------------------
    public override void Down(PointerArgs e)
    {
        var l = LayerOf();
        if (l == null) { App.Toast("벡터 레이어를 고르세요"); return; }
        Validate();
        var v = Shell2Ui.Canvas?.V ?? new ViewXf();
        var strokes = VectorLayer.Strokes(l);
        var before = (JsonArray)strokes.DeepClone();
        Drag D(Mode m) => new() { L = l, M = m, X0 = e.X, Y0 = e.Y, SX0 = e.SX, SY0 = e.SY, Before = before, Shift = e.Shift };
        var box = Box(l, v);
        if (box != null)
        {
            var b = box.Value;
            if (ScreenDist(v, RotHandle(b, v), e.SX, e.SY) <= 9) { drag = D(Mode.Rotate); drag.Center = ((b.x0 + b.x1) / 2, (b.y0 + b.y1) / 2); return; }
            var cs = Corners(b);
            for (int k = 0; k < 4; k++)
                if (ScreenDist(v, cs[k], e.SX, e.SY) <= 8)
                {
                    drag = D(Mode.Scale); drag.Corner = k; drag.CornerPt = cs[k]; drag.Anchor = cs[(k + 2) % 4];
                    return;
                }
        }
        if (Sel.Count == 1)
        {
            int si = Sel.First();
            if (si < strokes.Count)
            {
                var p = Pts(strokes[si]); int? k = null; double best = 11;
                foreach (int i in Handles(p)) { double d = ScreenDist(v, (p[i * 4], p[i * 4 + 1]), e.SX, e.SY); if (d <= best) { best = d; k = i; } }
                if (k != null) { drag = D(Mode.Point); drag.Point = k; drag.Si = si; return; }
            }
        }
        double tol = 8 / Math.Max(0.01, v.Zoom);
        var hit = VectorLayer.StrokeAt(l, e.X, e.Y, tol);
        if (hit == null)
        {
            // 상자 안 빈 곳 = 이동, 바깥 = 올가미
            if (box != null && !e.Shift && e.X >= box.Value.x0 && e.X <= box.Value.x1 && e.Y >= box.Value.y0 && e.Y <= box.Value.y1) { drag = D(Mode.Move); return; }
            drag = D(Mode.Lasso); drag.Additive = e.Shift; drag.Lasso = new() { (e.X, e.Y) };
            return;
        }
        int idx = hit.Value.index;
        if (e.Shift) { if (!Sel.Remove(idx)) Sel.Add(idx); }
        else if (!Sel.Contains(idx)) { Sel.Clear(); Sel.Add(idx); }
        Restamp(l);
        drag = D(Mode.Move);
        Refresh();
    }

    public override void Move(PointerArgs e)
    {
        var d = drag; if (d == null) return;
        if (Math.Abs(e.SX - d.SX0) + Math.Abs(e.SY - d.SY0) > 4) d.Moved = true;
        if (d.M == Mode.Lasso)
        {
            var last = d.Lasso[^1];
            if (Math.Abs(e.X - last.x) + Math.Abs(e.Y - last.y) > 1) d.Lasso.Add((e.X, e.Y));
            Shell2Ui.Canvas?.RedrawAll();
            return;
        }
        if (!d.Moved) return;
        var next = Preview(d, e.X, e.Y, e.Shift);
        var m = d.L.Meta != null ? (JsonObject)d.L.Meta.DeepClone() : new JsonObject(); m["strokes"] = next; d.L.Meta = m;
        Restamp(d.L);
        VectorLayer.Render(d.L, App.Doc);
        App.InvalidateAll();
    }

    /// <summary>끄는 중의 선 목록 (끌기 전 상태에서 다시 계산)</summary>
    JsonArray Preview(Drag d, double x, double y, bool shift)
    {
        var next = (JsonArray)d.Before.DeepClone();
        double dx = x - d.X0, dy = y - d.Y0;
        switch (d.M)
        {
            case Mode.Point:
            {
                var p = Pts(next[d.Si]); int n = p.Length / 4; double R = Math.Max(2, Reach * n);
                for (int j = 0; j < n; j++) { double w = Math.Exp(-Math.Pow((j - d.Point.Value) / R, 2)); if (w < 0.01) continue; p[j * 4] += dx * w; p[j * 4 + 1] += dy * w; }
                next[d.Si]["pts"] = PtsJson(p);
                return next;
            }
            case Mode.Move: return XformSel(next, 1, 0, 0, 1, dx, dy, 1);
            case Mode.Scale:
            {
                double ax = d.Anchor.x, ay = d.Anchor.y, cx = d.CornerPt.x - ax, cy = d.CornerPt.y - ay;
                double sx, sy;
                if (shift) { sx = Math.Abs(cx) > 1e-6 ? (x - ax) / cx : 1; sy = Math.Abs(cy) > 1e-6 ? (y - ay) / cy : 1; }
                else { double L = cx * cx + cy * cy; sx = sy = L > 0 ? ((x - ax) * cx + (y - ay) * cy) / L : 1; }
                sx = Math.Abs(sx) < 0.02 ? 0.02 * Math.Sign(sx == 0 ? 1 : sx) : sx; sy = Math.Abs(sy) < 0.02 ? 0.02 * Math.Sign(sy == 0 ? 1 : sy) : sy;
                double wk = ScaleWidth ? Math.Sqrt(Math.Abs(sx * sy)) : 1;
                return XformSel(next, sx, 0, 0, sy, ax - sx * ax, ay - sy * ay, wk);
            }
            case Mode.Rotate:
            {
                double a = Math.Atan2(y - d.Center.y, x - d.Center.x) - Math.Atan2(d.Y0 - d.Center.y, d.X0 - d.Center.x);
                if (shift) a = Math.Round(a / (Math.PI / 12)) * (Math.PI / 12);
                double c = Math.Cos(a), s = Math.Sin(a), ox = d.Center.x, oy = d.Center.y;
                return XformSel(next, c, s, -s, c, ox - c * ox + s * oy, oy - s * ox - c * oy, 1);
            }
        }
        return next;
    }

    JsonArray XformSel(JsonArray list, double a, double b, double c, double d, double e, double f, double wk)
    {
        foreach (int i in Sel)
        {
            if (i >= list.Count || list[i] is not JsonObject s) continue;
            list[i] = VecOps.Transform((JsonObject)s.DeepClone(), a, b, c, d, e, f, wk);
        }
        return list;
    }

    /// <summary>고른 선을 행렬로 한 번에 (메뉴·단추·시험용, 실행취소 가능)</summary>
    public void TransformSelected(double a, double b, double c, double d, double e, double f, double wk, string label)
    {
        var l = LayerOf(); Validate();
        if (l == null || Sel.Count == 0) { App.Toast("먼저 선을 고르세요"); return; }
        var before = (JsonArray)VectorLayer.Strokes(l).DeepClone();
        var next = XformSel((JsonArray)before.DeepClone(), a, b, c, d, e, f, wk);
        VectorLayer.SetStrokes(l, next, label, before);
        Restamp(l); Refresh();
    }

    /// <summary>고른 선의 가운데 (점 기준)</summary>
    public (double x, double y)? SelCenter()
    {
        var l = LayerOf(); Validate();
        if (l == null || Sel.Count == 0) return null;
        var strokes = VectorLayer.Strokes(l);
        var b = VecOps.Bounds(Sel.Where(i => i < strokes.Count).Select(i => (JsonObject)strokes[i]));
        return b.x1 < b.x0 ? null : ((b.x0 + b.x1) / 2, (b.y0 + b.y1) / 2);
    }

    public override void Up(PointerArgs e)
    {
        var d = drag; drag = null;
        if (d == null) return;
        if (d.M == Mode.Lasso)
        {
            if (d.Lasso.Count >= 3 && d.Moved)
            {
                var strokes = VectorLayer.Strokes(d.L).Select(n => (JsonObject)n).ToList();
                var picked = VecOps.LassoPick(strokes, d.Lasso, LassoAll);
                if (!d.Additive) Sel.Clear();
                foreach (int i in picked) Sel.Add(i);
            }
            else if (!d.Additive) Sel.Clear();
            Restamp(d.L);
            Refresh();
            return;
        }
        if (d.Moved != true) return;
        // 마지막 위치까지 (Move 없이 바로 Up 이 와도)
        var fin = Preview(d, e.X, e.Y, e.Shift);
        VecEdit.PutStrokes(d.L, fin);
        string label = d.M switch { Mode.Point => "벡터 점 옮기기", Mode.Scale => "벡터 선 크기", Mode.Rotate => "벡터 선 회전", _ => "벡터 선 이동" };
        VectorLayer.SetStrokes(d.L, VectorLayer.Strokes(d.L), label, d.Before);
        Restamp(d.L);
        Refresh();
    }

    public override void Cancel()
    {
        var d = drag; drag = null; if (d == null) return;
        if (d.M == Mode.Lasso) { Shell2Ui.Canvas?.RedrawAll(); return; }
        var m = d.L.Meta != null ? (JsonObject)d.L.Meta.DeepClone() : new JsonObject(); m["strokes"] = d.Before; d.L.Meta = m;
        Restamp(d.L);
        VectorLayer.Render(d.L, App.Doc); App.InvalidateAll();
    }

    public override bool HandleKey(Keys k)
    {
        Validate();
        if (k is Keys.Delete or Keys.Back && Sel.Count > 0) { Apply(_ => null, "벡터 선 삭제"); return true; }
        if (k == Keys.Escape && Sel.Count > 0) { Sel.Clear(); Refresh(); return true; }
        return false;
    }

    /// <summary>고른 선마다 fn (null 을 돌려주면 지움)</summary>
    public void Apply(Func<JsonObject, JsonObject> fn, string label)
    {
        var l = LayerOf(); Validate();
        if (l == null || Sel.Count == 0) { App.Toast("먼저 선을 고르세요"); return; }
        var next = new JsonArray(); bool deleting = false; int i = 0;
        var keep = new List<int>();
        foreach (var s in VectorLayer.Strokes(l))
        {
            if (!Sel.Contains(i++)) { next.Add(s.DeepClone()); continue; }
            var r = fn((JsonObject)s.DeepClone());
            if (r != null) { keep.Add(next.Count); next.Add(r); } else deleting = true;
        }
        VectorLayer.SetStrokes(l, next, label);
        Sel.Clear(); if (!deleting) foreach (int k in keep) Sel.Add(k);
        Restamp(l);
        Refresh();
    }

    public static double[] Smooth(double[] p, int k) => VecOps.Smooth(p, k, null, true);

    // ---- 화면 ------------------------------------------------------------------------------------------------
    public override Rectangle OverlayBounds(ViewXf v) { var c = Shell2Ui.Canvas; return (Sel.Count == 0 && drag?.M != Mode.Lasso) || c == null ? Rectangle.Empty : new Rectangle(0, 0, c.Width, c.Height); }
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var blue = Color.FromArgb(47, 124, 246);
        if (drag?.M == Mode.Lasso && drag.Lasso.Count >= 2)
        {
            var lp = drag.Lasso.Select(q => { var s = v.ToScreen(q.x, q.y); return new PointF((float)s.x, (float)s.y); }).ToArray();
            using var lw = new Pen(Color.White, 1); using var lb = new Pen(blue, 1) { DashPattern = new[] { 4f, 3f } };
            g.DrawPolygon(lw, lp); g.DrawPolygon(lb, lp);
        }
        var l = LayerOf(); if (l == null || Sel.Count == 0) return;
        if (selLayer != l.Id || VectorLayer.Strokes(l).Count != selCount) return;
        var strokes = VectorLayer.Strokes(l);
        using var pen = new Pen(blue, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        foreach (int i in Sel)
        {
            if (i >= strokes.Count) continue;
            var p = Pts(strokes[i]);
            var pts = Enumerable.Range(0, p.Length / 4).Select(k => { var q = v.ToScreen(p[k * 4], p[k * 4 + 1]); return new PointF((float)q.x, (float)q.y); }).ToArray();
            if (pts.Length >= 2) g.DrawLines(pen, pts);
        }
        var box = Box(l, v);
        if (box != null && drag?.M is not (Mode.Point))
        {
            var cs = Corners(box.Value).Select(q => { var s = v.ToScreen(q.x, q.y); return new PointF((float)s.x, (float)s.y); }).ToArray();
            using var bp = new Pen(blue, 1) { DashPattern = new[] { 3f, 3f } };
            g.DrawPolygon(bp, cs);
            using var hp = new Pen(blue, 1.5f);
            foreach (var c in cs) { g.FillRectangle(Brushes.White, c.X - 4, c.Y - 4, 8, 8); g.DrawRectangle(hp, c.X - 4, c.Y - 4, 8, 8); }
            var rh = RotHandle(box.Value, v); var rs = v.ToScreen(rh.x, rh.y);
            var top = v.ToScreen((box.Value.x0 + box.Value.x1) / 2, box.Value.y0);
            g.DrawLine(hp, (float)top.x, (float)top.y, (float)rs.x, (float)rs.y);
            g.FillEllipse(Brushes.White, (float)rs.x - 5, (float)rs.y - 5, 10, 10); g.DrawEllipse(hp, (float)rs.x - 5, (float)rs.y - 5, 10, 10);
        }
        if (Sel.Count == 1 && Sel.First() < strokes.Count)
        {
            var p = Pts(strokes[Sel.First()]);
            using var hp = new Pen(blue, 1.5f);
            foreach (int i in Handles(p)) { var q = v.ToScreen(p[i * 4], p[i * 4 + 1]); g.FillEllipse(Brushes.White, (float)q.x - 4.5f, (float)q.y - 4.5f, 9, 9); g.DrawEllipse(hp, (float)q.x - 4.5f, (float)q.y - 4.5f, 9, 9); }
        }
    }

    Action refreshOpts; int shownCount = -1;
    void Refresh() { Shell2Ui.Canvas?.RedrawAll(); if (Sel.Count != shownCount) refreshOpts?.Invoke(); }

    public override Control BuildOptions()
    {
        var box = (FlowLayoutPanel)Opt.Panel();
        void Render()
        {
            if (box.IsDisposed) return;
            Validate();
            box.SuspendLayout();
            foreach (Control c in box.Controls.Cast<Control>().ToList()) c.Dispose();
            int n = Sel.Count; shownCount = n;
            box.Controls.Add(Opt.Title(n > 0 ? $"고른 선 {n}개" : "벡터 선 고르기"));
            box.Controls.Add(Opt.Seg(new[] { (false, "올가미: 걸친 선"), (true, "다 들어간 선") }, () => LassoAll, v => { LassoAll = v; App.Set("vt.lassoAll", v); }));
            if (n > 0)
            {
                double k = 1;
                box.Controls.Add(Opt.Row(
                    Opt.Button("단순화", () => { VecEdit.Simplify(LayerOf(), VectorLineTool.SimplifyTol); }),
                    Opt.Button("부드럽게", () => { VecEdit.Smooth(LayerOf(), 1); }),
                    Opt.Button("많이 부드럽게", () => { VecEdit.Smooth(LayerOf(), 3); })));
                box.Controls.Add(Opt.Slider("단순화 허용 오차", 0.2, 10, () => VectorLineTool.SimplifyTol, v => VectorLineTool.SimplifyTol = v, step: 0.1, fmt: v => $"{v:0.0}px"));
                box.Controls.Add(Opt.Row(
                    Opt.Button("선 끝 연결", () => { int j = VecEdit.Connect(LayerOf(), VectorLineTool.ConnectDist); App.Toast(j > 0 ? $"선 끝 {j}곳을 이었습니다" : "가까운 선 끝이 없습니다"); }),
                    Opt.Button("선 끝 다듬기", () => VecEdit.Taper(LayerOf(), VectorLineTool.TaperIn, VectorLineTool.TaperOut))));
                box.Controls.Add(Opt.Slider("연결 거리", 1, 60, () => VectorLineTool.ConnectDist, v => VectorLineTool.ConnectDist = v, step: 1, fmt: v => $"{v:0}px"));
                box.Controls.Add(Opt.Slider("입 (시작 가늘게)", 0, 300, () => VectorLineTool.TaperIn, v => VectorLineTool.TaperIn = v, step: 1, fmt: v => v > 0 ? $"{v:0}px" : "끔"));
                box.Controls.Add(Opt.Slider("출 (끝 가늘게)", 0, 300, () => VectorLineTool.TaperOut, v => VectorLineTool.TaperOut = v, step: 1, fmt: v => v > 0 ? $"{v:0}px" : "끔"));
                box.Controls.Add(Opt.Slider("굵기 배율 (손을 떼면 적용)", 0.2, 3, () => k, v => k = v, pct: true, step: 0.05, onEnd: () =>
                {
                    double f = k; k = 1;
                    Apply(s => { var pre = s["preset"] as JsonObject ?? new JsonObject(); double sz = BrushPreset.Num(pre["size"]) ?? 4; pre["size"] = Math.Max(0.3, sz * f); s["preset"] = pre; return s; }, "선 굵기");
                }));
                box.Controls.Add(Opt.Row(Opt.Button("지금 색으로", () => Apply(s => { s["color"] = Px.ToHex(App.Fg); return s; }, "선 색")), Opt.Button("삭제", () => Apply(_ => null, "벡터 선 삭제"))));
                box.Controls.Add(Opt.Row(
                    Opt.Button("좌우 반전", () => { if (SelCenter() is { } c) TransformSelected(-1, 0, 0, 1, 2 * c.x, 0, 1, "벡터 선 뒤집기"); }),
                    Opt.Button("상하 반전", () => { if (SelCenter() is { } c) TransformSelected(1, 0, 0, -1, 0, 2 * c.y, 1, "벡터 선 뒤집기"); }),
                    Opt.Button("90° 회전", () => { if (SelCenter() is { } c) TransformSelected(0, 1, -1, 0, c.x + c.y, c.y - c.x, 1, "벡터 선 회전"); })));
                box.Controls.Add(Opt.Check("크기 바꿀 때 선 굵기도", () => ScaleWidth, v => { ScaleWidth = v; App.Set("vt.scaleWidth", v); }));
                box.Controls.Add(Opt.Row(Opt.Button("모두 고르기", () => { var l = LayerOf(); if (l == null) return; Sel.Clear(); for (int i = 0; i < VectorLayer.Strokes(l).Count; i++) Sel.Add(i); Restamp(l); Refresh(); }), Opt.Button("선택 해제", () => { Sel.Clear(); Refresh(); })));
                if (n == 1) box.Controls.Add(Opt.Slider("점을 끌 때 같이 움직이는 범위", 0.02, 0.5, () => Reach, v => Reach = v, pct: true));
            }
            else box.Controls.Add(Opt.Row(Opt.Button("모두 고르기", () => { var l = LayerOf(); if (l == null) return; Sel.Clear(); for (int i = 0; i < VectorLayer.Strokes(l).Count; i++) Sel.Add(i); Restamp(l); Refresh(); })));
            box.Controls.Add(Opt.Help("벡터 레이어에서 선을 탭: 고르기 · Shift+탭: 여러 개 · 빈 곳을 끌기: 올가미로 고르기 · 상자 안을 끌기: 이동 · 모서리: 크기 (Shift = 가로세로 따로) · 위 동그라미: 회전 (Shift = 15°) · 선 하나를 고르면 흰 점을 끌어 모양 고치기 · Delete: 삭제."));
            box.ResumeLayout();
        }
        refreshOpts = () => { if (!box.IsDisposed && box.IsHandleCreated) box.BeginInvoke((Action)Render); };
        Render();
        return box;
    }
}
