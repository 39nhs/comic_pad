// 벡터 선 다듬기 계산 (클립스튜디오의 벡터 선 도구와 같은 일): 화면·실행취소와 상관없는 순수 계산만 모아 둠.
//  - 점 목록은 벡터 레이어와 같은 [x, y, 필압, 시간, …] (4개씩)
//  - 선 굵기는 필압으로 표현 (웹 판과 같은 .cpad 모양 그대로): 굵기 = size × (minSize + (1 − minSize) × 필압곡선(p))
//    굵기를 바꿀 때는 원하는 굵기 → 필압곡선을 거꾸로 풀어 필압을 정함 (지금 펜 설정의 곡선 기준)
//  - 단순화 (Ramer–Douglas–Peucker), 부드럽게 (이동 평균·Chaikin, 양 끝점 고정), 선 끝 연결, 입·출 다듬기, 고른 선 변형
using System.Text.Json.Nodes;
using ComicPadNative.Modules.BrushEngine;

namespace ComicPadNative.Modules.VectorTools;

public static class VecOps
{
    public static int Count(double[] p) => p.Length / 4;
    public static double[] Pts(JsonObject s) => VectorLayer.Pts(s);
    public static JsonArray ToJson(IEnumerable<double> v) => new(v.Select(x => (JsonNode)JsonValue.Create(Math.Round(x, 3))).ToArray());
    public static JsonObject With(JsonObject s, double[] pts) { var c = (JsonObject)s.DeepClone(); c["pts"] = ToJson(pts); return c; }
    public static double SizeOf(JsonObject s) { var v = BrushPreset.Num(s["preset"]?["size"]); return v is double d && d > 0 ? d : 4; }

    // ---- 단순화 (Ramer–Douglas–Peucker) --------------------------------------------------------------------
    /// <summary>
    /// 점 줄이기: 선에서 tol(px) 이상 벗어나는 점만 남김. 양 끝점은 늘 남김.
    /// pressW: 필압 차이도 거리로 봄 (필압 1 차이 = pressW px) — 입·출처럼 굵기가 바뀌는 곳의 점을 지키려고
    /// </summary>
    public static double[] Simplify(double[] p, double tol, double pressW = 0, int from = 0, int to = -1)
    {
        int n = Count(p);
        if (to < 0) to = n - 1;
        if (n < 3 || tol <= 0 || to - from < 2) return (double[])p.Clone();
        var keep = new bool[n];
        for (int i = 0; i < n; i++) keep[i] = i < from || i > to;
        keep[from] = keep[to] = true;
        var stack = new Stack<(int a, int b)>();
        stack.Push((from, to));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            double ax = p[a * 4], ay = p[a * 4 + 1], ap = p[a * 4 + 2], bx = p[b * 4], by = p[b * 4 + 1], bp = p[b * 4 + 2];
            double dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
            double best = -1; int bi = -1;
            for (int i = a + 1; i < b; i++)
            {
                double x = p[i * 4], y = p[i * 4 + 1];
                double t = L > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / L, 0, 1) : 0;
                double ex = ax + dx * t - x, ey = ay + dy * t - y;
                double d = Math.Sqrt(ex * ex + ey * ey);
                if (pressW > 0) d = Math.Max(d, Math.Abs(ap + (bp - ap) * t - p[i * 4 + 2]) * pressW);
                if (d > best) { best = d; bi = i; }
            }
            if (best > tol) { keep[bi] = true; stack.Push((a, bi)); stack.Push((bi, b)); }
        }
        var o = new List<double>();
        for (int i = 0; i < n; i++) if (keep[i]) o.AddRange(new[] { p[i * 4], p[i * 4 + 1], p[i * 4 + 2], p[i * 4 + 3] });
        return CornerGuard(o.ToArray(), Math.Max(1, tol * 2));
    }

    /// <summary>
    /// 꺾인 점 양옆(guard px)에 점을 하나씩 더 둠: 다시 그릴 때 브러시가 점 사이를 곡선(중점 이음)으로 그리므로,
    /// 점이 듬성하면 꺾인 곳이 크게 깎임 → 꺾인 점 가까이에 점을 두어 깎이는 폭을 guard/4 정도로 (긴 선분에만)
    /// </summary>
    static double[] CornerGuard(double[] q, double guard)
    {
        int n = Count(q);
        if (n < 3) return q;
        var o = new List<double>(q.Length * 2);
        o.AddRange(q.AsSpan(0, 4).ToArray());
        for (int i = 1; i < n; i++)
        {
            double L = Math.Sqrt(Sq(q[i * 4] - q[i * 4 - 4]) + Sq(q[i * 4 + 1] - q[i * 4 - 3]));
            bool aIn = i - 1 > 0, bIn = i < n - 1;               // 선분 양끝이 꺾인 점(가운데 점)인지
            if (L > guard * 3)
            {
                if (aIn) for (int d = 0; d < 4; d++) o.Add(q[i * 4 - 4 + d] + (q[i * 4 + d] - q[i * 4 - 4 + d]) * (guard / L));
                if (bIn) for (int d = 0; d < 4; d++) o.Add(q[i * 4 + d] + (q[i * 4 - 4 + d] - q[i * 4 + d]) * (guard / L));
            }
            o.AddRange(q.AsSpan(i * 4, 4).ToArray());
        }
        return o.ToArray();
    }

    /// <summary>점이 선 p 에서 얼마나 떨어져 있는지 (선분까지 최단 거리)</summary>
    public static double DistToPolyline(double[] p, double x, double y)
    {
        int n = Count(p); double best = double.MaxValue;
        if (n == 1) return Math.Sqrt((p[0] - x) * (p[0] - x) + (p[1] - y) * (p[1] - y));
        for (int i = 0; i + 1 < n; i++)
        {
            double ax = p[i * 4], ay = p[i * 4 + 1], dx = p[i * 4 + 4] - ax, dy = p[i * 4 + 5] - ay, L = dx * dx + dy * dy;
            double t = L > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / L, 0, 1) : 0;
            double ex = ax + dx * t - x, ey = ay + dy * t - y;
            best = Math.Min(best, ex * ex + ey * ey);
        }
        return Math.Sqrt(best);
    }

    // ---- 부드럽게 -------------------------------------------------------------------------------------------
    /// <summary>이동 평균 (1·2·1) 을 k 번. 양 끝점은 그대로. weight(i) 가 있으면 점마다 그만큼만 (0 = 그대로)</summary>
    public static double[] Smooth(double[] p, int k, Func<int, double> weight = null, bool pressure = false)
    {
        var a = (double[])p.Clone();
        int n = Count(p);
        if (n < 3) return a;
        for (int r = 0; r < k; r++)
        {
            var b = (double[])a.Clone();
            for (int i = 1; i + 1 < n; i++)
            {
                double w = weight?.Invoke(i) ?? 1;
                if (w <= 0) continue;
                int c = i * 4;
                for (int d = 0; d < (pressure ? 3 : 2); d++)
                {
                    double avg = (a[c - 4 + d] + a[c + d] * 2 + a[c + 4 + d]) / 4;
                    b[c + d] = a[c + d] + (avg - a[c + d]) * w;
                }
            }
            a = b;
        }
        return a;
    }

    /// <summary>Chaikin 모서리 깎기 (점이 듬성한 선용). 양 끝점은 그대로, 필압·시간도 같은 비율로</summary>
    public static double[] Chaikin(double[] p, int iterations)
    {
        var a = p;
        for (int it = 0; it < iterations; it++)
        {
            int n = Count(a);
            if (n < 3) return (double[])a.Clone();
            var o = new List<double>(n * 8);
            o.AddRange(a.AsSpan(0, 4).ToArray());
            for (int i = 0; i + 1 < n; i++)
            {
                for (int q = 0; q < 2; q++)
                {
                    if (i == 0 && q == 0) continue;            // 첫 점은 이미 넣음
                    if (i == n - 2 && q == 1) continue;        // 끝 점은 따로
                    double t = q == 0 ? 0.25 : 0.75;
                    for (int d = 0; d < 4; d++) o.Add(a[i * 4 + d] + (a[i * 4 + 4 + d] - a[i * 4 + d]) * t);
                }
            }
            o.AddRange(a.AsSpan((n - 1) * 4, 4).ToArray());
            a = o.ToArray();
        }
        return (double[])a.Clone();
    }

    /// <summary>"부드럽게": 점이 촘촘하면 이동 평균, 듬성하면 Chaikin (양 끝점은 그대로)</summary>
    public static double[] SmoothAuto(double[] p, double strength)
    {
        int n = Count(p);
        if (n < 3) return (double[])p.Clone();
        double len = Length(p), avg = len / (n - 1);
        int k = Math.Max(1, (int)Math.Round(strength));
        // 떨림은 이동 평균으로 펴고, 점이 듬성하면 Chaikin 으로 모서리를 둥글게 (다시 그릴 때 각지지 않게)
        var a = Smooth(p, avg > 6 ? k : k * 2);
        return avg > 6 ? Chaikin(a, 1) : a;
    }

    public static double Length(double[] p)
    {
        double s = 0; int n = Count(p);
        for (int i = 0; i + 1 < n; i++) s += Math.Sqrt(Sq(p[i * 4 + 4] - p[i * 4]) + Sq(p[i * 4 + 5] - p[i * 4 + 1]));
        return s;
    }
    static double Sq(double v) => v * v;

    /// <summary>긴 선분에 점을 끼워 넣어 maxSeg 이하로 (near 가 있으면 그 조건에 맞는 선분만)</summary>
    public static double[] Resample(double[] p, double maxSeg, Func<int, bool> near = null)
    {
        int n = Count(p);
        if (n < 2 || maxSeg <= 0) return (double[])p.Clone();
        var o = new List<double>(p.Length);
        o.AddRange(p.AsSpan(0, 4).ToArray());
        for (int i = 0; i + 1 < n; i++)
        {
            double L = Math.Sqrt(Sq(p[i * 4 + 4] - p[i * 4]) + Sq(p[i * 4 + 5] - p[i * 4 + 1]));
            int k = L > maxSeg && (near == null || near(i)) ? (int)Math.Ceiling(L / maxSeg) : 1;
            for (int j = 1; j < k; j++)
            {
                double t = (double)j / k;
                for (int d = 0; d < 4; d++) o.Add(p[i * 4 + d] + (p[i * 4 + 4 + d] - p[i * 4 + d]) * t);
            }
            o.AddRange(p.AsSpan(i * 4 + 4, 4).ToArray());
        }
        return o.ToArray();
    }

    // ---- 선 굵기 (필압) -----------------------------------------------------------------------------------
    /// <summary>필압 p 일 때 이 선의 굵기 (px)</summary>
    public static double WidthAt(PenFeel f, BrushPreset pre, double pr) => pre.PressureSize ? pre.Size * (pre.MinSize + (1 - pre.MinSize) * f.Curve(pr, pre)) : pre.Size;

    /// <summary>필압곡선을 거꾸로: Curve(p) ≥ c 가 되는 가장 작은 p</summary>
    public static double InvCurve(PenFeel f, BrushPreset pre, double c)
    {
        if (c <= f.Curve(0, pre)) return 0;
        if (c >= f.Curve(1, pre)) return 1;
        double lo = 0, hi = 1;
        for (int i = 0; i < 40; i++) { double m = (lo + hi) / 2; if (f.Curve(m, pre) >= c) hi = m; else lo = m; }
        return hi;
    }

    /// <summary>점마다 굵기 배율 k(i) 를 곱한 선 (바뀐 게 없으면 null).
    /// 원래 굵기 안에서 바뀌면 바뀐 점의 필압만 고치고, 더 굵어지거나 최소 굵기보다 가늘어지면 size·minSize 를 바꾸고 모든 점의 필압을 다시 풂</summary>
    public static JsonObject ScaleWidthAt(JsonObject s, Func<int, double> k, PenFeel f)
    {
        var p = Pts(s); int n = Count(p);
        if (n == 0) return null;
        var preJ = s["preset"] as JsonObject ?? new JsonObject();
        var pre = BrushPreset.FromJson(preJ);
        var w = new double[n]; var nw = new double[n]; var changed = new bool[n]; bool any = false;
        for (int i = 0; i < n; i++)
        {
            w[i] = WidthAt(f, pre, p[i * 4 + 2]);
            double ki = k(i);
            changed[i] = Math.Abs(ki - 1) > 1e-6;
            nw[i] = changed[i] ? Math.Max(0.2, w[i] * ki) : w[i];
            any |= changed[i];
        }
        if (!any) return null;
        double S = pre.Size, m = pre.PressureSize ? pre.MinSize : 1;
        double lo = S * m, hi = S;
        bool inside = pre.PressureSize && m < 1;
        for (int i = 0; i < n && inside; i++) if (changed[i] && (nw[i] > hi * 1.0001 || nw[i] < lo * 0.9999)) inside = false;
        var c = (JsonObject)s.DeepClone();
        var cp = (JsonObject)preJ.DeepClone(); c["preset"] = cp;
        if (inside)
        {
            for (int i = 0; i < n; i++) if (changed[i]) p[i * 4 + 2] = InvCurve(f, pre, Math.Clamp((nw[i] / S - m) / (1 - m), 0, 1));
        }
        else
        {
            double nS = Math.Max(0.3, Math.Max(S, nw.Max()));
            double nm = Math.Min(pre.PressureSize ? m : 1, nw.Min() / nS);
            nm = Math.Clamp(nm, 0, 1) * 0.999;                     // 1 이면 필압이 소용없으니 살짝 아래로
            cp["size"] = Math.Round(nS, 3); cp["pressureSize"] = true; cp["minSize"] = Math.Round(nm, 4);
            var np = BrushPreset.FromJson(cp);
            for (int i = 0; i < n; i++) p[i * 4 + 2] = InvCurve(f, np, Math.Clamp((nw[i] / np.Size - np.MinSize) / Math.Max(1e-6, 1 - np.MinSize), 0, 1));
        }
        c["pts"] = ToJson(p);
        return c;
    }

    /// <summary>모든 점의 굵기 (지금 펜 설정 기준)</summary>
    public static double[] Widths(JsonObject s, PenFeel f)
    {
        var p = Pts(s); var pre = BrushPreset.FromJson(s["preset"] as JsonObject ?? new JsonObject());
        return Enumerable.Range(0, Count(p)).Select(i => WidthAt(f, pre, p[i * 4 + 2])).ToArray();
    }

    /// <summary>굵기 브러시: (x, y) 반경 r 안의 점 굵기에 (1 ± 세기 × 감쇠) 를 곱함. 바뀌면 새 선, 아니면 null</summary>
    public static JsonObject WidthBrush(JsonObject s, double x, double y, double r, double amount, PenFeel f)
    {
        var p = Pts(s); int n = Count(p);
        if (n == 0 || !Near(p, x, y, r + SizeOf(s) / 2)) return null;
        // 점이 듬성하면 붓 근처 선분에 점을 끼워 넣어야 부분만 바뀜
        var rp = Resample(p, Math.Max(1.5, r / 3), i => SegDist(p, i, x, y) <= r * 1.5);
        var src = rp.Length != p.Length ? With(s, rp) : s;
        var res = ScaleWidthAt(src, i =>
        {
            double d = Math.Sqrt(Sq(rp[i * 4] - x) + Sq(rp[i * 4 + 1] - y));
            if (d >= r) return 1;
            double t = 1 - d * d / (r * r), fall = t * t;
            return Math.Max(0.05, 1 + amount * fall);
        }, f);
        return res;
    }

    static double SegDist(double[] p, int i, double x, double y)
    {
        double ax = p[i * 4], ay = p[i * 4 + 1], dx = p[i * 4 + 4] - ax, dy = p[i * 4 + 5] - ay, L = dx * dx + dy * dy;
        double t = L > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / L, 0, 1) : 0;
        return Math.Sqrt(Sq(ax + dx * t - x) + Sq(ay + dy * t - y));
    }
    static bool Near(double[] p, double x, double y, double r)
    {
        int n = Count(p);
        if (n == 1) return Sq(p[0] - x) + Sq(p[1] - y) <= r * r;
        for (int i = 0; i + 1 < n; i++) if (SegDist(p, i, x, y) <= r) return true;
        return false;
    }

    /// <summary>부드럽게 브러시: 반경 안의 점만 감쇠만큼 이동 평균 쪽으로 (끝점 고정)</summary>
    public static JsonObject SmoothBrush(JsonObject s, double x, double y, double r, double strength)
    {
        var p = Pts(s); int n = Count(p);
        if (n < 3 || !Near(p, x, y, r)) return null;
        bool any = false;
        var q = Smooth(p, 1, i =>
        {
            double d = Math.Sqrt(Sq(p[i * 4] - x) + Sq(p[i * 4 + 1] - y));
            if (d >= r) return 0;
            any = true; double t = 1 - d * d / (r * r); return Math.Clamp(strength * t * t, 0, 1);
        });
        return any ? With(s, q) : null;
    }

    /// <summary>단순화 브러시: 반경 안에 든 구간만 RDP</summary>
    public static JsonObject SimplifyBrush(JsonObject s, double x, double y, double r, double tol)
    {
        var p = Pts(s); int n = Count(p);
        if (n < 3) return null;
        int a = -1, b = -1;
        for (int i = 0; i < n; i++) if (Sq(p[i * 4] - x) + Sq(p[i * 4 + 1] - y) <= r * r) { if (a < 0) a = i; b = i; }
        if (a < 0) return null;
        a = Math.Max(0, a - 1); b = Math.Min(n - 1, b + 1);
        if (b - a < 2) return null;
        var q = Simplify(p, tol, SizeOf(s) * 0.5, a, b);
        return q.Length < p.Length ? With(s, q) : null;
    }

    // ---- 입·출 다듬기 --------------------------------------------------------------------------------------
    /// <summary>선 끝을 가늘게: 시작에서 inLen px, 끝에서 outLen px 동안 굵기를 minK → 1 로 (점이 듬성하면 끼워 넣음)</summary>
    public static JsonObject Taper(JsonObject s, double inLen, double outLen, PenFeel f, double minK = 0.08)
    {
        var p = Pts(s); int n = Count(p);
        if (n < 2 || (inLen <= 0 && outLen <= 0)) return null;
        double total = Length(p);
        double seg = Math.Max(1, Math.Min(inLen > 0 ? inLen : double.MaxValue, outLen > 0 ? outLen : double.MaxValue) / 6);
        var rp = Resample(p, seg);
        int m = Count(rp);
        var dist = new double[m];
        for (int i = 1; i < m; i++) dist[i] = dist[i - 1] + Math.Sqrt(Sq(rp[i * 4] - rp[i * 4 - 4]) + Sq(rp[i * 4 + 1] - rp[i * 4 - 3]));
        total = dist[m - 1];
        // 선이 짧으면 입·출이 겹치지 않게 줄임
        double li = inLen, lo = outLen;
        if (li + lo > total && li + lo > 0) { double k = total / (li + lo); li *= k; lo *= k; }
        double Ease(double t) => t <= 0 ? 0 : t >= 1 ? 1 : Math.Sin(t * Math.PI / 2);
        return ScaleWidthAt(With(s, rp), i =>
        {
            double k = 1;
            if (li > 0) k *= minK + (1 - minK) * Ease(dist[i] / li);
            if (lo > 0) k *= minK + (1 - minK) * Ease((total - dist[i]) / lo);
            return k;
        }, f);
    }

    // ---- 선 끝 연결 ---------------------------------------------------------------------------------------
    /// <summary>선 하나의 시작(0)·끝(1) 점</summary>
    public static (double x, double y) EndOf(double[] p, int end) => end == 0 ? (p[0], p[1]) : (p[^4], p[^3]);

    /// <summary>두 선을 끝끼리 이음: a 의 endA 쪽과 b 의 endB 쪽. 결과는 a 의 설정·색 (a 의 반대 끝 → 이음 → b 의 반대 끝)</summary>
    public static JsonObject Join(JsonObject a, int endA, JsonObject b, int endB)
    {
        var pa = Pts(a); var pb = Pts(b);
        if (endA == 0) pa = Reverse(pa);
        if (endB == 1) pb = Reverse(pb);
        // 겹치는 점(거의 같은 자리)은 하나만
        int skip = Sq(pa[^4] - pb[0]) + Sq(pa[^3] - pb[1]) < 0.25 ? 4 : 0;
        var o = new double[pa.Length + pb.Length - skip];
        pa.CopyTo(o, 0); Array.Copy(pb, skip, o, pa.Length, pb.Length - skip);
        // 시간은 이어지게 (다시 그릴 때 속도 계산이 튀지 않게)
        double t0 = pa[^1], tb = skip + 3 < pb.Length ? pb[skip + 3] : 0;
        for (int i = pa.Length + 3; i < o.Length; i += 4) o[i] = o[i] - tb + t0 + 8;
        return With(a, o);
    }

    /// <summary>선을 닫음 (끝 → 시작)</summary>
    public static JsonObject Close(JsonObject a)
    {
        var p = Pts(a);
        var o = p.Concat(new[] { p[0], p[1], p[2], p[^1] + 8 }).ToArray();
        return With(a, o);
    }

    public static double[] Reverse(double[] p)
    {
        int n = Count(p); var o = new double[p.Length];
        for (int i = 0; i < n; i++) { int s = (n - 1 - i) * 4; o[i * 4] = p[s]; o[i * 4 + 1] = p[s + 1]; o[i * 4 + 2] = p[s + 2]; o[i * 4 + 3] = p[(n - 1) * 4 + 3] - p[s + 3]; }
        return o;
    }

    /// <summary>
    /// 선 끝 자동 연결: idx 에 든 선들의 끝점 중 dist 안에 있는 것끼리 (가까운 쌍부터) 이음. 한 선의 양 끝이 가까우면 닫음.
    /// 결과: 새 선 목록 (이어진 선은 맨 앞 선 자리에) + 이은 횟수 + 새 목록 각 선의 원래 번호
    /// </summary>
    public static (List<JsonObject> strokes, int joins, List<int> origin) ConnectEnds(IList<JsonObject> strokes, ICollection<int> idx, double dist)
    {
        var sel = idx.Where(i => i >= 0 && i < strokes.Count && Count(Pts(strokes[i])) >= 1).Distinct().OrderBy(i => i).ToList();
        var pts = sel.ToDictionary(i => i, i => Pts(strokes[i]));
        // 후보 쌍 (끝점 격자로 찾기)
        var ends = new List<(int s, int e, double x, double y)>();
        foreach (int i in sel) for (int e = 0; e < 2; e++) { var (x, y) = EndOf(pts[i], e); ends.Add((i, e, x, y)); }
        double cell = Math.Max(1, dist);
        var grid = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < ends.Count; k++)
        {
            var key = ((int)Math.Floor(ends[k].x / cell), (int)Math.Floor(ends[k].y / cell));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
            l.Add(k);
        }
        var pairs = new List<(double d, int a, int b)>();
        for (int k = 0; k < ends.Count; k++)
        {
            int gx = (int)Math.Floor(ends[k].x / cell), gy = (int)Math.Floor(ends[k].y / cell);
            for (int yy = gy - 1; yy <= gy + 1; yy++)
                for (int xx = gx - 1; xx <= gx + 1; xx++)
                {
                    if (!grid.TryGetValue((xx, yy), out var l)) continue;
                    foreach (int j in l)
                    {
                        if (j <= k) continue;
                        double d = Math.Sqrt(Sq(ends[k].x - ends[j].x) + Sq(ends[k].y - ends[j].y));
                        if (d > dist) continue;
                        if (ends[k].s == ends[j].s)
                        {
                            // 자기 양 끝: 선이 충분히 길 때만 닫기 (짧은 점·획은 그대로)
                            if (Length(pts[ends[k].s]) < Math.Max(3 * dist, 12)) continue;
                        }
                        pairs.Add((d, k, j));
                    }
                }
        }
        pairs.Sort((p, q) => p.d.CompareTo(q.d));
        // 이음 관계 (끝점 → 짝 끝점), 무리(union-find)
        var partner = new Dictionary<int, int>();
        var root = sel.ToDictionary(i => i, i => i);
        int Find(int i) { while (root[i] != i) { root[i] = root[root[i]]; i = root[i]; } return i; }
        var closed = new HashSet<int>();
        int joins = 0;
        foreach (var (d, a, b) in pairs)
        {
            if (partner.ContainsKey(a) || partner.ContainsKey(b)) continue;
            int ra = Find(ends[a].s), rb = Find(ends[b].s);
            if (ra == rb)
            {
                // 같은 무리의 두 자유 끝 → 고리로 닫기
                if (closed.Contains(ra)) continue;
                closed.Add(ra);
            }
            else root[ra] = rb;
            partner[a] = b; partner[b] = a; joins++;
        }
        if (joins == 0) return (strokes.ToList(), 0, Enumerable.Range(0, strokes.Count).ToList());
        // 무리마다 이어 붙이기
        var keyOf = new Dictionary<(int, int), int>();
        for (int k = 0; k < ends.Count; k++) keyOf[(ends[k].s, ends[k].e)] = k;
        var used = new HashSet<int>();
        var merged = new Dictionary<int, JsonObject>();    // 무리의 첫 선 번호 → 새 선
        var drop = new HashSet<int>();
        foreach (int s0 in sel)
        {
            if (used.Contains(s0)) continue;
            // 무리의 한쪽 자유 끝에서 시작 (없으면 고리: 아무 데서나)
            var group = sel.Where(i => Find(i) == Find(s0)).ToList();
            if (group.Count == 1 && !partner.ContainsKey(keyOf[(s0, 0)]) && !partner.ContainsKey(keyOf[(s0, 1)])) { used.Add(s0); continue; }
            int start = -1, startEnd = 0;
            foreach (int g in group)
            {
                if (!partner.ContainsKey(keyOf[(g, 0)])) { start = g; startEnd = 0; break; }
                if (!partner.ContainsKey(keyOf[(g, 1)])) { start = g; startEnd = 1; break; }
            }
            bool loop = start < 0;
            if (loop) { start = group.Min(); startEnd = 0; }
            // 시작 선: 자유 끝이 startEnd → 반대 끝으로 나감
            JsonObject acc = startEnd == 0 ? strokes[start] : With(strokes[start], Reverse(pts[start]));
            int cur = start, outEnd = 1 - startEnd; used.Add(cur);
            for (int guard = 0; guard < group.Count + 2; guard++)
            {
                if (!partner.TryGetValue(keyOf[(cur, outEnd)], out int pk)) break;
                var (ns, ne) = (ends[pk].s, ends[pk].e);
                if (used.Contains(ns)) { if (ns == start) acc = Close(acc); break; }   // 고리가 처음으로 돌아옴
                acc = Join(acc, 1, strokes[ns], ne);
                used.Add(ns); cur = ns; outEnd = 1 - ne;
            }
            int first = group.Min();
            merged[first] = acc;
            foreach (int g in group) if (g != first) drop.Add(g);
        }
        var o = new List<JsonObject>(); var origin = new List<int>();
        for (int i = 0; i < strokes.Count; i++)
        {
            if (drop.Contains(i)) continue;
            o.Add(merged.TryGetValue(i, out var m) ? m : strokes[i]); origin.Add(i);
        }
        return (o, joins, origin);
    }

    // ---- 변형 ------------------------------------------------------------------------------------------------
    /// <summary>점마다 2×3 행렬 (x' = a x + c y + e, y' = b x + d y + f). widthK: 선 굵기 배율 (1 = 그대로)</summary>
    public static JsonObject Transform(JsonObject s, double a, double b, double c, double d, double e, double f, double widthK = 1)
    {
        var p = Pts(s);
        for (int i = 0; i + 1 < p.Length; i += 4) { double x = p[i], y = p[i + 1]; p[i] = a * x + c * y + e; p[i + 1] = b * x + d * y + f; }
        var o = With(s, p);
        if (Math.Abs(widthK - 1) > 1e-6)
        {
            var pre = o["preset"] as JsonObject ?? new JsonObject(); o["preset"] = pre;
            pre["size"] = Math.Round(Math.Max(0.3, SizeOf(s) * widthK), 3);
        }
        return o;
    }

    /// <summary>고른 선들의 범위 (점 기준, 선 굵기 빼고)</summary>
    public static (double x0, double y0, double x1, double y1) Bounds(IEnumerable<JsonObject> strokes)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var s in strokes)
        {
            var p = Pts(s);
            for (int i = 0; i + 1 < p.Length; i += 4) { x0 = Math.Min(x0, p[i]); x1 = Math.Max(x1, p[i]); y0 = Math.Min(y0, p[i + 1]); y1 = Math.Max(y1, p[i + 1]); }
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>다각형 안인지 (짝홀 규칙)</summary>
    public static bool InPoly(IList<(double x, double y)> poly, double x, double y)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (xi, yi) = poly[i]; var (xj, yj) = poly[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) c = !c;
        }
        return c;
    }

    /// <summary>올가미로 고르기: all = 점이 모두 안에 든 선만, 아니면 점이 하나라도 든 선</summary>
    public static List<int> LassoPick(IList<JsonObject> strokes, IList<(double x, double y)> poly, bool all)
    {
        var o = new List<int>();
        if (poly.Count < 3) return o;
        double x0 = poly.Min(q => q.x), x1 = poly.Max(q => q.x), y0 = poly.Min(q => q.y), y1 = poly.Max(q => q.y);
        for (int k = 0; k < strokes.Count; k++)
        {
            var p = Pts(strokes[k]); int n = Count(p), inside = 0;
            for (int i = 0; i < n; i++)
            {
                double x = p[i * 4], y = p[i * 4 + 1];
                if (x >= x0 && x <= x1 && y >= y0 && y <= y1 && InPoly(poly, x, y)) inside++;
            }
            if (n > 0 && (all ? inside == n : inside > 0)) o.Add(k);
        }
        return o;
    }
}
