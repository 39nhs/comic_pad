// 브러시 엔진 (시제품): 웹 판(core/brush.js)의 선 만들기를 옮겨 옴 —
// 1€ 필터 손떨림 보정 → 점 사이 2차 곡선 → 간격마다 원 찍기.
// 다른 점: 원을 픽셀마다 "덮인 정도(coverage)" 로 계산하고, 한 획 안에서는 덮인 정도의 최댓값만 남긴다.
// 그래서 원이 아무리 겹쳐도 가장자리가 진해지거나 뭉치지 않는다 (선뭉침이 구조적으로 없음).
namespace ComicPadNative;

public sealed class BrushSettings
{
    public float Size = 6;          // 지름 (문서 px)
    public float MinSize = 0.04f;   // 가장 약할 때 크기 비율 (G펜처럼 거의 0까지)
    public float Gamma = 1;         // 필압 곡선 (1 = 직선)
    public float Stabilizer = 3;    // 손떨림 보정 (0 = 없음)
    public float Spacing = 0.05f;   // 찍는 간격 (지름 비율)
    public uint Color = 0xFF000000; // BGRA
    public float Curve(double p) => (float)Math.Pow(Math.Clamp(p, 0, 1), Gamma);
}

public struct Pt { public double X, Y, P, T; public Pt(double x, double y, double p, double t) { X = x; Y = y; P = p; T = t; } }

public sealed class Stroke
{
    readonly Doc doc;
    readonly BrushSettings b;
    readonly bool erase;
    public double Zoom = 1;
    readonly Dictionary<int, uint[]> before = new();
    readonly Dictionary<int, byte[]> cov = new();
    readonly List<Pt> pts = new();
    Pt lastMid;
    double next;
    // 1€ 필터
    bool euInit; double ex, ey, ev, et; double lastP;
    public Rect Dirty = Rect.Empty;
    public int Dabs;

    public Stroke(Doc d, BrushSettings bs, bool eraser) { doc = d; b = bs; erase = eraser; }

    public void Begin(Pt p) { lastP = p.P; Push(Smooth(p)); }
    public void Add(Pt p) { if (p.P > 0) lastP = p.P; Push(Smooth(p)); }

    /// <returns>실행취소용 "그리기 전" 타일들</returns>
    public Dictionary<int, uint[]> End(Pt last)
    {
        double p1 = Math.Min(lastP, last.P > 0 ? last.P : lastP);
        if (b.Stabilizer > 0 && euInit)
        {
            // 보정 지연분을 펜 끝까지 따라잡기 (필압은 펜을 뗄 때 값까지 점점)
            double p0 = lastP, t0 = et;
            for (int i = 1; i <= 8; i++) Push(Euro(new Pt(last.X, last.Y, p0 + (p1 - p0) * i / 8, t0 + i * 12)));
            Push(new Pt(last.X, last.Y, p1, t0 + 120));
        }
        if (pts.Count > 1) Line(lastMid, pts[^1]);
        return before;
    }

    // ---- 손떨림 보정 -----------------------------------------------------------
    Pt Smooth(Pt p) => b.Stabilizer > 0 ? Euro(p) : p;

    Pt Euro(Pt p)
    {
        double s = Math.Max(1, b.Stabilizer);
        double minCut = 4 / (1 + 0.5 * s), beta = 0.006, dCut = 1;
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

    // ---- 점 → 곡선 → 찍기 ---------------------------------------------------------
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

    double SizeAt(double p) => b.Size * (b.MinSize + (1 - b.MinSize) * b.Curve(p));
    double SpacingAt(double p) => Math.Max(0.35, SizeAt(p) * b.Spacing);

    void Quad(Pt a, Pt c, Pt e)
    {
        double len = Hyp(c.X - a.X, c.Y - a.Y) + Hyp(e.X - c.X, e.Y - c.Y);
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
        double dx = c.X - a.X, dy = c.Y - a.Y, len = Hyp(dx, dy);
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

    static double Hyp(double x, double y) => Math.Sqrt(x * x + y * y);

    // ---- 원 하나: 픽셀마다 덮인 정도를 계산해 최댓값만 ------------------------------
    unsafe void Dab(double cx, double cy, double p)
    {
        Dabs++;
        double r = SizeAt(p) / 2;
        double rr = Math.Max(r, 0.5), fade = r < 0.5 ? r * 2 : 1;   // 아주 가는 끝은 옅게
        int x0 = Math.Max(0, (int)Math.Floor(cx - rr - 1)), y0 = Math.Max(0, (int)Math.Floor(cy - rr - 1));
        int x1 = Math.Min(doc.W, (int)Math.Ceiling(cx + rr + 1)), y1 = Math.Min(doc.H, (int)Math.Ceiling(cy + rr + 1));
        if (x1 <= x0 || y1 <= y0) return;
        Dirty = Dirty.Union(Rect.FromLTRB(x0, y0, x1, y1));
        uint col = b.Color;
        int T = Doc.T;
        for (int ty = y0 / T; ty <= (y1 - 1) / T; ty++)
        {
            for (int tx = x0 / T; tx <= (x1 - 1) / T; tx++)
            {
                int ti = ty * doc.TW + tx;
                if (!before.TryGetValue(ti, out var bt)) { bt = doc.CopyTile(ti); before[ti] = bt; cov[ti] = new byte[T * T]; }
                var ct = cov[ti];
                int px0 = Math.Max(x0, tx * T), px1 = Math.Min(x1, tx * T + T), py0 = Math.Max(y0, ty * T), py1 = Math.Min(y1, ty * T + T);
                fixed (uint* ink = doc.Ink) fixed (uint* bp = bt) fixed (byte* cp = ct)
                {
                    for (int y = py0; y < py1; y++)
                    {
                        double dy = y + 0.5 - cy, dy2 = dy * dy;
                        int lo = (y - ty * T) * T - tx * T;
                        for (int x = px0; x < px1; x++)
                        {
                            double dx = x + 0.5 - cx;
                            double c = rr + 0.5 - Math.Sqrt(dx * dx + dy2);
                            if (c <= 0) continue;
                            int cv = (int)(Math.Min(1, c) * fade * 255 + 0.5);
                            int li = lo + x;
                            if (cv <= cp[li]) continue;            // 이미 더 진하게 덮였음: 쌓지 않음
                            cp[li] = (byte)cv;
                            uint bpx = bp[li];
                            ink[y * doc.W + x] = erase ? Scale(bpx, 255 - cv) : Over(col, cv, bpx);
                        }
                    }
                }
            }
        }
    }

    // 불투명 색을 cov 만큼 before 위에 (미리 곱한 알파)
    static uint Over(uint c, int cv, uint d)
    {
        int ic = 255 - cv;
        uint b = (uint)(((c & 0xFF) * cv + (d & 0xFF) * ic + 127) / 255);
        uint g = (uint)((((c >> 8) & 0xFF) * cv + ((d >> 8) & 0xFF) * ic + 127) / 255);
        uint r = (uint)((((c >> 16) & 0xFF) * cv + ((d >> 16) & 0xFF) * ic + 127) / 255);
        uint a = (uint)((255 * cv + (d >> 24) * ic + 127) / 255);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    static uint Scale(uint d, int k)
    {
        uint b = (uint)(((d & 0xFF) * k + 127) / 255), g = (uint)((((d >> 8) & 0xFF) * k + 127) / 255);
        uint r = (uint)((((d >> 16) & 0xFF) * k + 127) / 255), a = (uint)(((d >> 24) * k + 127) / 255);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }
}
