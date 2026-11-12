// 브러시 획 엔진 — 웹 판 core/brush.js 의 선 만들기(1€ 손떨림 보정·2차 곡선·간격 찍기)를 옮김.
// 원을 픽셀마다 "덮인 정도" 로 계산하고 한 획 안에서는 최댓값만 남겨 선뭉침이 없다.
// 결과 = 그리기 전(before) 위에 색을 (덮인 정도 × 불투명도 × 선택 영역) 만큼 합성 — 지우개·투명 픽셀 잠금·합성 모드 반영.
using ComicPadNative.Core;

namespace ComicPadNative.Engine;

public sealed class BrushParams
{
    public double Size = 6;            // 지름 (문서 px)
    public double Opacity = 1;         // 획 불투명도
    public double Hardness = 1;        // 1 = 딱딱한 원, 0 = 아주 부드러움
    public bool PressureSize = true;
    public double MinSize = 0.04;
    public bool PressureOpacity;
    public double MinOpacity = 0.2;
    public double Stabilizer = 3;
    public double Spacing = 0.05;
    public string Blend = "source-over";
    public Func<double, double> Curve = p => Math.Clamp(p, 0, 1);   // 필압 곡선 (설정)
}

public struct Pt { public double X, Y, P, T; public Pt(double x, double y, double p, double t) { X = x; Y = y; P = p; T = t; } }

public sealed class Stroke
{
    readonly Document doc;
    readonly BrushParams b;
    readonly PixelEdit edit;
    readonly Surface s;
    readonly uint color;               // 미리 곱한 BGRA
    readonly bool erase, alphaLock;
    readonly Blend mode;
    readonly Dictionary<int, byte[]> cov = new();
    readonly List<Pt> pts = new();
    Pt lastMid;
    double next, lastP;
    bool euInit; double ex, ey, ev, et;
    public double Zoom = 1;
    public Rect Dirty = Rect.Empty;     // 화면에 다시 그릴 영역 (가져가면 비움)

    public Stroke(Document d, PixelEdit e, BrushParams p, uint col, bool eraser, bool alphaLockLayer)
    {
        doc = d; edit = e; s = e.Target; b = p; color = col; erase = eraser; alphaLock = alphaLockLayer; mode = Px.Parse(p.Blend);
    }

    public void Begin(Pt p) { lastP = p.P; Push(Smooth(p)); }
    public void Add(Pt p) { if (p.P > 0) lastP = p.P; Push(Smooth(p)); }

    public void End(Pt last)
    {
        double p1 = Math.Min(lastP, last.P > 0 ? last.P : lastP);
        if (b.Stabilizer > 0 && euInit)
        {
            double p0 = lastP, t0 = et;
            for (int i = 1; i <= 8; i++) Push(Euro(new Pt(last.X, last.Y, p0 + (p1 - p0) * i / 8, t0 + i * 12)));
            Push(new Pt(last.X, last.Y, p1, t0 + 120));
        }
        if (pts.Count > 1) Line(lastMid, pts[^1]);
    }

    public Rect TakeDirty() { var r = Dirty; Dirty = Rect.Empty; return r; }

    Pt Smooth(Pt p) => b.Stabilizer > 0 ? Euro(p) : p;

    Pt Euro(Pt p)
    {
        double sv = Math.Max(1, b.Stabilizer);
        double minCut = 4 / (1 + 0.5 * sv), beta = 0.006, dCut = 1;
        double dt = euInit ? Math.Max(0.001, (p.T - et) / 1000) : 1 / 120.0;
        et = p.T;
        double A(double cut) { double tau = 1 / (2 * Math.PI * cut); return 1 / (1 + tau / dt); }
        if (!euInit) { euInit = true; ex = p.X; ey = p.Y; ev = 0; return p; }
        double speed = Math.Sqrt((p.X - ex) * (p.X - ex) + (p.Y - ey) * (p.Y - ey)) * Zoom / dt;
        ev += (speed - ev) * A(dCut);
        double a = A(minCut + beta * ev);
        ex += (p.X - ex) * a; ey += (p.Y - ey) * a;
        return new Pt(ex, ey, p.P, p.T);
    }

    void Push(Pt p)
    {
        pts.Add(p);
        int n = pts.Count;
        if (n == 1) { Dab(p.X, p.Y, p.P); next = SpacingAt(p.P); lastMid = p; return; }
        var a = pts[n - 2];
        var mid = new Pt((a.X + p.X) / 2, (a.Y + p.Y) / 2, (a.P + p.P) / 2, p.T);
        if (n == 2) Line(lastMid, mid); else Quad(lastMid, a, mid);
        lastMid = mid;
        if (pts.Count > 4) pts.RemoveAt(0);
    }

    double SizeAt(double p) => b.PressureSize ? b.Size * (b.MinSize + (1 - b.MinSize) * b.Curve(p)) : b.Size;
    double AlphaAt(double p) => b.Opacity * (b.PressureOpacity ? b.MinOpacity + (1 - b.MinOpacity) * b.Curve(p) : 1);
    double SpacingAt(double p) => Math.Max(0.35, SizeAt(p) * b.Spacing);

    void Quad(Pt a, Pt c, Pt e)
    {
        double len = Math.Sqrt((c.X - a.X) * (c.X - a.X) + (c.Y - a.Y) * (c.Y - a.Y)) + Math.Sqrt((e.X - c.X) * (e.X - c.X) + (e.Y - c.Y) * (e.Y - c.Y));
        int n = Math.Clamp((int)Math.Ceiling(len / 3), 1, 32);
        var prev = a;
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n, it = 1 - t;
            var q = new Pt(it * it * a.X + 2 * it * t * c.X + t * t * e.X, it * it * a.Y + 2 * it * t * c.Y + t * t * e.Y, a.P + (e.P - a.P) * t, e.T);
            Line(prev, q);
            prev = q;
        }
    }

    void Line(Pt a, Pt c)
    {
        double dx = c.X - a.X, dy = c.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return;
        double t = next;
        while (t <= len)
        {
            double u = t / len, pr = a.P + (c.P - a.P) * u;
            Dab(a.X + dx * u, a.Y + dy * u, pr);
            t += SpacingAt(pr);
        }
        next = t - len;
    }

    /// <summary>원 하나: 픽셀마다 덮인 정도(0~255)를 계산해 이 획의 최댓값만 남기고, 바뀐 픽셀은 before 에서 다시 계산</summary>
    void Dab(double cx, double cy, double p)
    {
        double r = SizeAt(p) / 2, rr = Math.Max(r, 0.5), fade = r < 0.5 ? r * 2 : 1;
        double amp = AlphaAt(p) * fade;
        double soft = Math.Max(0.5, rr * (1 - Math.Clamp(b.Hardness, 0, 1)));   // 부드러운 가장자리 폭
        var box = Rect.FromPoints(cx - rr - 1, cy - rr - 1, cx + rr + 1, cy + rr + 1).Clip(doc.W, doc.H);
        if (box.IsEmpty) return;
        edit.Touch(box);
        Dirty = Dirty.Union(box);
        var sel = doc.Sel;
        const int TS = Surface.TS;
        foreach (int ti in s.TilesIn(box))
        {
            int tx = ti % s.TX, ty = ti / s.TX;
            var tr = s.TileRect(ti).Intersect(box);
            var bt = edit.BeforeTile(ti);
            if (bt == null && (erase || alphaLock)) continue;          // 투명한 곳: 지울 것·칠할 것 없음
            if (!cov.TryGetValue(ti, out var ct)) cov[ti] = ct = new byte[TS * TS];
            var t = s.Tile(tx, ty, true);
            for (int y = tr.Y; y < tr.B; y++)
            {
                double dy = y + 0.5 - cy, dy2 = dy * dy;
                int row = (y - ty * TS) * TS - tx * TS;
                for (int x = tr.X; x < tr.R; x++)
                {
                    double dx = x + 0.5 - cx;
                    double d = Math.Sqrt(dx * dx + dy2);
                    double c = b.Hardness >= 0.98 ? rr + 0.5 - d : (rr - d) / soft + 0.5;
                    if (c <= 0) continue;
                    int cv = (int)(Math.Min(1, c) * amp * 255 + 0.5);
                    if (sel.Mask != null) cv = cv * sel.Mask[y * doc.W + x] / 255;
                    int li = row + x;
                    if (cv <= ct[li]) continue;                         // 이미 더 진하게 덮였음: 쌓지 않음
                    ct[li] = (byte)cv;
                    uint before = bt == null ? 0 : bt[li];
                    t[li] = Apply(before, (uint)cv);
                }
            }
        }
    }

    uint Apply(uint before, uint cv)
    {
        if (erase) return Px.Scale(before, 255 - cv);
        uint src = Px.Scale(color, cv);
        if (alphaLock) return Px.Composite(src, before, Blend.Atop);
        return mode == Blend.Normal ? Px.Over(src, before) : Px.Composite(src, before, mode);
    }
}
