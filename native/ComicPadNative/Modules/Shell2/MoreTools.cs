// 웹 판에 있고 네이티브에 없던 도구: 복제 도장·픽셀 유동화 (웹 tools/retouch.js), 벡터 선 고르기 (웹 tools/vselect.js).
// 복제·유동화는 브러시 모듈의 DabToolBase(펜 경로를 따라 간격마다 찍기, 실행취소·선택 영역·투명 잠금 처리)를 그대로 씀.
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public sealed class CloneTool : DabToolBase
{
    public override string Id => "clone";
    public override string Name => "복제 도장";
    public override string Icon => "⎘";
    public override bool AllowAltPick => false;
    protected override double SpacingK => 0.1;
    protected override string Label => "복제 도장";
    protected override double MaxHard => 0.97;
    public double Opacity = 1; public bool Aligned = true, SampleAll;
    public (double x, double y)? Source; (double x, double y)? offset;
    Surface src; double hx, hy;

    public CloneTool() { Size = 60; Hardness = 0.5; }

    protected override void Begin(PointerArgs e)
    {
        if (e.Alt)
        {
            Source = (e.X, e.Y); offset = null;
            App.Toast("복제할 원본 위치를 정했습니다");
            edit.Cancel(); edit = null; return;
        }
        if (Source == null) { App.Toast("먼저 Alt+클릭으로 복제할 원본을 찍으세요"); edit.Cancel(); edit = null; return; }
        if (offset == null || !Aligned) offset = (Source.Value.x - e.X, Source.Value.y - e.Y);
        // 원본: 시작할 때 모습으로 고정 (방금 찍은 걸 또 복제하지 않도록)
        src = SampleAll ? App.Comp.Flatten(App.Doc, new RenderOptions { Paper = true }) : target.Clone();
        Dab(e.X, e.Y, e.Pressure > 0 ? e.Pressure : 1);
    }

    protected override void Dab(double x, double y, double p)
    {
        if (src == null || offset == null) return;
        double r = D / 2.0;
        int X = (int)Math.Floor(x - r + 0.5), Y = (int)Math.Floor(y - r + 0.5);
        int SX = (int)Math.Round(x + offset.Value.x - r), SY = (int)Math.Round(y + offset.Value.y - r);
        var tmp = new uint[D * D];
        for (int j = 0; j < D; j++) for (int i = 0; i < D; i++)
        {
            int sx = SX + i, sy = SY + j;
            if ((uint)sx < (uint)src.W && (uint)sy < (uint)src.H) tmp[j * D + i] = src.Get(sx, sy);
        }
        Stamp(tmp, X, Y, Opacity * Math.Max(0.2, p));
    }

    public override void Up(PointerArgs e) { base.Up(e); src = null; }
    public override void Hover(PointerArgs e) { hx = e.X; hy = e.Y; }

    public override Rectangle OverlayBounds(ViewXf v)
    {
        if (Source == null) return Rectangle.Empty;
        var (sx, sy) = Mark(v); int rad = (int)(Size / 2 * v.Zoom) + 12;
        return new Rectangle((int)sx - rad, (int)sy - rad, rad * 2, rad * 2);
    }
    (double, double) Mark(ViewXf v) => offset != null ? v.ToScreen(hx + offset.Value.x, hy + offset.Value.y) : v.ToScreen(Source.Value.x, Source.Value.y);
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (Source == null) return;
        var (x, y) = Mark(v);
        using var p = new Pen(Color.FromArgb(47, 124, 246), 2);
        g.DrawLine(p, (float)x - 8, (float)y, (float)x + 8, (float)y); g.DrawLine(p, (float)x, (float)y - 8, (float)x, (float)y + 8);
        float rad = (float)(Size / 2 * v.Zoom); g.DrawEllipse(p, (float)x - rad, (float)y - rad, rad * 2, rad * 2);
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("복제 도장"),
        BUI.Slider("크기", 4, 400, () => Size, v => Size = v, step: 1, log: true),
        BUI.Slider("경도", 0, 0.97, () => Hardness, v => Hardness = v, fmt: BUI.Pct),
        BUI.Slider("불투명도", 0.05, 1, () => Opacity, v => Opacity = v, fmt: BUI.Pct),
        BUI.Toggle("정렬 (원본 위치를 계속 따라감)", () => Aligned, v => { Aligned = v; offset = null; }),
        BUI.Toggle("보이는 그림 전체에서 복제", () => SampleAll, v => SampleAll = v),
        BUI.Help("Alt+클릭으로 원본 지정 → 고칠 곳을 칠하면 원본이 복사됩니다."));
}

public sealed class LiquifyTool : DabToolBase
{
    public override string Id => "liquify";
    public override string Name => "픽셀 유동화";
    public override string Icon => "〰";
    protected override double SpacingK => 0.12;
    protected override string Label => "픽셀 유동화";
    protected override double MaxHard => 1;
    public string Mode = "push";
    double px, py; bool first;

    public LiquifyTool() { Size = 120; Strength = 0.6; }

    protected override void Begin(PointerArgs e) { px = e.X; py = e.Y; first = true; }

    protected override void Dab(double x, double y, double p)
    {
        double ddx = x - px, ddy = y - py, len = Math.Sqrt(ddx * ddx + ddy * ddy);
        double ux = len > 0 ? ddx / len : 0, uy = len > 0 ? ddy / len : 0;
        px = x; py = y;
        if (first && Mode == "push") { first = false; return; }
        first = false;
        int R = Math.Max(1, (int)Math.Round(D / 2.0)), Dd = R * 2;
        int X = (int)Math.Round(x) - R, Y = (int)Math.Round(y) - R;
        var r = new Rect(X, Y, Dd, Dd).Clip(target.W, target.H);
        if (r.IsEmpty) return;
        edit.Touch(r);
        var srcBuf = target.Read(r); var o = (uint[])srcBuf.Clone();
        int sw = r.W, sh = r.H;
        double st = Strength * Math.Max(0.3, p), step = D * SpacingK;
        var sel = App.Doc.Sel;
        for (int j = 0; j < sh; j++)
            for (int i = 0; i < sw; i++)
            {
                double dx = r.X + i - x, dy = r.Y + j - y, d2 = dx * dx + dy * dy;
                if (d2 >= R * R) continue;
                if (sel.Mask != null && sel.Mask[(r.Y + j) * sel.W + r.X + i] < 128) continue;
                double f = 1 - Math.Sqrt(d2) / R, w = f * f * (3 - 2 * f) * st;
                double sx, sy;
                if (Mode == "push") { sx = dx - ux * step * w * 1.6; sy = dy - uy * step * w * 1.6; }
                else if (Mode == "bloat") { sx = dx * (1 - w * 0.25); sy = dy * (1 - w * 0.25); }
                else if (Mode == "pinch") { sx = dx * (1 + w * 0.25); sy = dy * (1 + w * 0.25); }
                else { double a = w * 0.25 * (Mode == "twirlL" ? -1 : 1), c = Math.Cos(a), s = Math.Sin(a); sx = dx * c - dy * s; sy = dx * s + dy * c; }
                double fx = x + sx - r.X, fy = y + sy - r.Y;
                int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                if (ix < 0 || iy < 0 || ix >= sw - 1 || iy >= sh - 1) continue;
                double tx = fx - ix, ty = fy - iy;
                uint a00 = srcBuf[iy * sw + ix], a10 = srcBuf[iy * sw + ix + 1], a01 = srcBuf[(iy + 1) * sw + ix], a11 = srcBuf[(iy + 1) * sw + ix + 1];
                uint res = 0;
                for (int sh8 = 0; sh8 < 32; sh8 += 8)
                {
                    double v = (((a00 >> sh8) & 255) * (1 - tx) + ((a10 >> sh8) & 255) * tx) * (1 - ty) + (((a01 >> sh8) & 255) * (1 - tx) + ((a11 >> sh8) & 255) * tx) * ty;
                    res |= (uint)Math.Clamp((int)Math.Round(v), 0, 255) << sh8;
                }
                o[j * sw + i] = res;
            }
        target.Write(r, o);
        App.Invalidate(r);
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("픽셀 유동화"),
        BUI.Seg(new[] { ("push", "밀기"), ("bloat", "부풀리기"), ("pinch", "오므리기"), ("twirlR", "오른쪽 회전"), ("twirlL", "왼쪽 회전") }, () => Mode, v => Mode = v),
        BUI.Slider("크기", 10, 400, () => Size, v => Size = v, step: 1, log: true),
        BUI.Slider("세기", 0.05, 1, () => Strength, v => Strength = v, fmt: BUI.Pct),
        BUI.Help("얼굴형·눈 크기·옷 주름 등을 손으로 밀어서 고칩니다. 부풀리기/오므리기/회전은 제자리에서 문지르세요."));
}

/// <summary>벡터 선 고르기: 탭해서 고르고 옮기기·다듬기·굵기·색·삭제, 선 하나면 점을 끌어 모양 고치기</summary>
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
    sealed class Drag { public Layer L; public int? Point; public int Si; public double X0, Y0, SX0, SY0; public bool Moved; public JsonArray Before; }
    Drag drag;

    static Layer LayerOf() { var l = App.Doc?.Active; return l?.Kind == "vector" ? l : null; }
    static double[] Pts(JsonNode s) => VectorLayer.Pts((JsonObject)s);
    static JsonArray PtsJson(double[] p) => new(p.Select(v => (JsonNode)Math.Round(v, 2)).ToArray());

    public override void Activate() => Sel.Clear();
    public override void Deactivate() { Sel.Clear(); drag = null; Shell2Ui.Canvas?.RedrawAll(); }

    static List<int> Handles(double[] p) { int n = p.Length / 4, step = Math.Max(1, (int)Math.Ceiling(n / 24.0)); var o = new List<int>(); for (int i = 0; i < n; i += step) o.Add(i); if (o.Count == 0 || o[^1] != n - 1) o.Add(n - 1); return o; }

    public override void Down(PointerArgs e)
    {
        var l = LayerOf();
        if (l == null) { App.Toast("벡터 레이어를 고르세요"); return; }
        var v = Shell2Ui.Canvas?.V ?? new ViewXf();
        var strokes = VectorLayer.Strokes(l);
        if (Sel.Count == 1)
        {
            int si = Sel.First();
            if (si < strokes.Count)
            {
                var p = Pts(strokes[si]); int? k = null; double best = 11;
                foreach (int i in Handles(p)) { var q = v.ToScreen(p[i * 4], p[i * 4 + 1]); double d = Math.Sqrt((q.x - e.SX) * (q.x - e.SX) + (q.y - e.SY) * (q.y - e.SY)); if (d <= best) { best = d; k = i; } }
                if (k != null) { drag = new Drag { L = l, Point = k, Si = si, X0 = e.X, Y0 = e.Y, SX0 = e.SX, SY0 = e.SY, Before = (JsonArray)strokes.DeepClone() }; return; }
            }
        }
        double tol = 8 / Math.Max(0.01, v.Zoom);
        var hit = VectorLayer.StrokeAt(l, e.X, e.Y, tol);
        if (hit == null) { if (!e.Shift) Sel.Clear(); Refresh(); return; }
        int idx = hit.Value.index;
        if (e.Shift) { if (!Sel.Remove(idx)) Sel.Add(idx); }
        else if (!Sel.Contains(idx)) { Sel.Clear(); Sel.Add(idx); }
        drag = new Drag { L = l, X0 = e.X, Y0 = e.Y, SX0 = e.SX, SY0 = e.SY, Before = (JsonArray)strokes.DeepClone() };
        Refresh();
    }

    public override void Move(PointerArgs e)
    {
        var d = drag; if (d == null) return;
        if (Math.Abs(e.SX - d.SX0) + Math.Abs(e.SY - d.SY0) > 4) d.Moved = true;
        if (!d.Moved) return;
        double dx = e.X - d.X0, dy = e.Y - d.Y0;
        var next = (JsonArray)d.Before.DeepClone();
        if (d.Point != null)
        {
            var p = Pts(next[d.Si]); int n = p.Length / 4; double R = Math.Max(2, Reach * n);
            for (int j = 0; j < n; j++) { double w = Math.Exp(-Math.Pow((j - d.Point.Value) / R, 2)); if (w < 0.01) continue; p[j * 4] += dx * w; p[j * 4 + 1] += dy * w; }
            next[d.Si]["pts"] = PtsJson(p);
        }
        else
            foreach (int i in Sel) { if (i >= next.Count) continue; var p = Pts(next[i]); for (int k = 0; k + 1 < p.Length; k += 4) { p[k] += dx; p[k + 1] += dy; } next[i]["pts"] = PtsJson(p); }
        var m = d.L.Meta != null ? (JsonObject)d.L.Meta.DeepClone() : new JsonObject(); m["strokes"] = next; d.L.Meta = m;
        VectorLayer.Render(d.L, App.Doc);
        App.InvalidateAll();
    }

    public override void Up(PointerArgs e)
    {
        var d = drag; drag = null;
        if (d?.Moved != true) return;
        VectorLayer.SetStrokes(d.L, VectorLayer.Strokes(d.L), d.Point != null ? "벡터 점 옮기기" : "벡터 선 이동", d.Before);
    }

    public override void Cancel()
    {
        var d = drag; drag = null; if (d == null) return;
        var m = d.L.Meta != null ? (JsonObject)d.L.Meta.DeepClone() : new JsonObject(); m["strokes"] = d.Before; d.L.Meta = m;
        VectorLayer.Render(d.L, App.Doc); App.InvalidateAll();
    }

    public override bool HandleKey(Keys k)
    {
        if (k is Keys.Delete or Keys.Back && Sel.Count > 0) { Apply(_ => null, "벡터 선 삭제"); return true; }
        if (k == Keys.Escape && Sel.Count > 0) { Sel.Clear(); Refresh(); return true; }
        return false;
    }

    /// <summary>고른 선마다 fn (null 을 돌려주면 지움)</summary>
    public void Apply(Func<JsonObject, JsonObject> fn, string label)
    {
        var l = LayerOf();
        if (l == null || Sel.Count == 0) { App.Toast("먼저 선을 고르세요"); return; }
        var next = new JsonArray(); bool deleting = false; int i = 0;
        foreach (var s in VectorLayer.Strokes(l))
        {
            if (!Sel.Contains(i++)) { next.Add(s.DeepClone()); continue; }
            var r = fn((JsonObject)s.DeepClone());
            if (r != null) next.Add(r); else deleting = true;
        }
        VectorLayer.SetStrokes(l, next, label);
        if (deleting) Sel.Clear();
        Refresh();
    }

    public static double[] Smooth(double[] p, int k)
    {
        var a = (double[])p.Clone();
        for (int r = 0; r < k; r++)
        {
            var b = (double[])a.Clone();
            for (int i = 4; i + 4 < a.Length; i += 4) { b[i] = (a[i - 4] + a[i] * 2 + a[i + 4]) / 4; b[i + 1] = (a[i - 3] + a[i + 1] * 2 + a[i + 5]) / 4; b[i + 2] = (a[i - 2] + a[i + 2] * 2 + a[i + 6]) / 4; }
            a = b;
        }
        return a;
    }

    public override Rectangle OverlayBounds(ViewXf v) { var c = Shell2Ui.Canvas; return Sel.Count == 0 || c == null ? Rectangle.Empty : new Rectangle(0, 0, c.Width, c.Height); }
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var l = LayerOf(); if (l == null || Sel.Count == 0) return;
        var strokes = VectorLayer.Strokes(l);
        using var pen = new Pen(Color.FromArgb(47, 124, 246), 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        foreach (int i in Sel)
        {
            if (i >= strokes.Count) continue;
            var p = Pts(strokes[i]);
            var pts = Enumerable.Range(0, p.Length / 4).Select(k => { var q = v.ToScreen(p[k * 4], p[k * 4 + 1]); return new PointF((float)q.x, (float)q.y); }).ToArray();
            if (pts.Length >= 2) g.DrawLines(pen, pts);
        }
        if (Sel.Count == 1 && Sel.First() < strokes.Count)
        {
            var p = Pts(strokes[Sel.First()]);
            using var hp = new Pen(Color.FromArgb(47, 124, 246), 1.5f);
            foreach (int i in Handles(p)) { var q = v.ToScreen(p[i * 4], p[i * 4 + 1]); g.FillEllipse(Brushes.White, (float)q.x - 4.5f, (float)q.y - 4.5f, 9, 9); g.DrawEllipse(hp, (float)q.x - 4.5f, (float)q.y - 4.5f, 9, 9); }
        }
    }

    Action refreshOpts;
    void Refresh() { Shell2Ui.Canvas?.RedrawAll(); refreshOpts?.Invoke(); }

    public override Control BuildOptions()
    {
        var box = (FlowLayoutPanel)Opt.Panel();
        void Render()
        {
            if (box.IsDisposed) return;
            box.SuspendLayout();
            foreach (Control c in box.Controls.Cast<Control>().ToList()) c.Dispose();
            int n = Sel.Count;
            box.Controls.Add(Opt.Title(n > 0 ? $"고른 선 {n}개" : "벡터 선 고르기"));
            if (n > 0)
            {
                box.Controls.Add(Opt.Row(Opt.Button("다듬기", () => Apply(s => { s["pts"] = PtsJson(Smooth(VectorLayer.Pts(s), 2)); return s; }, "선 다듬기")),
                    Opt.Button("많이 다듬기", () => Apply(s => { s["pts"] = PtsJson(Smooth(VectorLayer.Pts(s), 6)); return s; }, "선 다듬기"))));
                box.Controls.Add(Opt.Row(Opt.Button("지금 색으로", () => Apply(s => { s["color"] = Px.ToHex(App.Fg); return s; }, "선 색")), Opt.Button("삭제", () => Apply(_ => null, "벡터 선 삭제"))));
                double k = 1;
                box.Controls.Add(Opt.Slider("굵기 배율 (손을 떼면 적용)", 0.2, 3, () => k, v => k = v, pct: true, step: 0.05, onEnd: () =>
                {
                    double f = k; k = 1;
                    Apply(s => { var pre = s["preset"] as JsonObject ?? new JsonObject(); double sz = BrushPreset.Num(pre["size"]) ?? 4; pre["size"] = Math.Max(0.3, sz * f); s["preset"] = pre; return s; }, "선 굵기");
                }));
                box.Controls.Add(Opt.Row(Opt.Button("모두 고르기", () => { var l = LayerOf(); if (l == null) return; Sel.Clear(); for (int i = 0; i < VectorLayer.Strokes(l).Count; i++) Sel.Add(i); Refresh(); }), Opt.Button("선택 해제", () => { Sel.Clear(); Refresh(); })));
                if (n == 1) box.Controls.Add(Opt.Slider("점을 끌 때 같이 움직이는 범위", 0.02, 0.5, () => Reach, v => Reach = v, pct: true));
            }
            box.Controls.Add(Opt.Help("벡터 레이어에서 선을 탭: 고르기 · Shift+탭: 여러 개 · 끌기: 이동 · 선 하나를 고르면 흰 점을 끌어 모양 고치기 · Delete: 삭제."));
            box.ResumeLayout();
        }
        refreshOpts = () => { if (!box.IsDisposed && box.IsHandleCreated) box.BeginInvoke((Action)Render); };
        Render();
        return box;
    }
}
