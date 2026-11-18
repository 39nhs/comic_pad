// 틈 닫기 (클립스튜디오 "틈 닫기"처럼): 선이 끊긴 곳의 "선 끝"을 찾아, 가까운 다른 선까지 보이지 않는 선을 그어 막고 채운다.
//  1) 벽(선) 마스크를 가늘게 깎아 뼈대 (Zhang–Suen, 두께 24px 까지) → 이웃이 하나뿐인 뼈대 점 = 선 끝
//  2) 선 끝에서 뼈대를 거슬러 올라가 선의 방향을 구하고, 방향대로 나아가 선의 실제 끝(끝 픽셀)을 찾음
//  3) 자기 선(선 끝에서 선을 따라 일정 거리 안의 벽)은 빼고, 앞쪽(±75°)에서 maxGap 칸 이내의 가장 가까운 벽 픽셀까지 선분
//  → 기존 "틈 막기"(선을 두껍게)와 달리 좁은 칸·평행선은 막지 않고 끊긴 선 끝만 잇는다.
// 덜 칠한 곳 메우기: 칠한 영역 안에 갇힌 작은 구멍(이 크기 이하)을 채움.
// 여러 칸 칠하기: 여러 시작점 → 영역마다 채운 마스크를 합침 (시작점 색이 첫 점과 다르면 건너뜀 = 선 위는 안 칠함).
using ComicPadNative.Core;

namespace ComicPadNative.Modules.SelectFill;

public static class GapClose
{
    public readonly record struct Seg(int X0, int Y0, int X1, int Y1);

    /// <summary>벽(1 = 선) 에서 maxGap 칸 이하로 끊긴 선 끝을 잇는 선분들</summary>
    public static List<Seg> Find(byte[] wall, int w, int h, double maxGap, CancellationToken ct = default)
    {
        int G = (int)Math.Round(maxGap);
        var result = new List<Seg>();
        if (G <= 0 || w <= 2 || h <= 2) return result;
        var sk = Thin(wall, w, h, 12, ct);
        ct.ThrowIfCancellationRequested();
        // 선 끝: 뼈대 이웃이 하나
        var ends = new List<int>();
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                if (sk[i] == 0) continue;
                int c = sk[i - w - 1] + sk[i - w] + sk[i - w + 1] + sk[i - 1] + sk[i + 1] + sk[i + w - 1] + sk[i + w] + sk[i + w + 1];
                if (c == 1) ends.Add(i);
            }
        }
        if (ends.Count == 0 || ends.Count > 400_000) return result;
        int D = 2 * G + 12;                     // 자기 선으로 볼 거리 (선을 따라)
        double cosMax = Math.Cos(75 * Math.PI / 180);
        var bag = new System.Collections.Concurrent.ConcurrentBag<(int k, Seg s)>();
        Parallel.For(0, ends.Count, new ParallelOptions { CancellationToken = ct }, k =>
        {
            int e = ends[k], ex = e % w, ey = e / w;
            // 방향: 뼈대를 따라 거슬러 (최대 10칸, 갈림길에서 멈춤)
            int cx = ex, cy = ey, prev = -1, cur = e, steps = 0;
            for (; steps < 10; steps++)
            {
                int nx = -1, cnt = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int qx = cur % w + dx, qy = cur / w + dy;
                        if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                        int q = qy * w + qx;
                        if (sk[q] == 0 || q == prev) continue;
                        cnt++; nx = q;
                    }
                if (cnt != 1) break;
                prev = cur; cur = nx; cx = cur % w; cy = cur / w;
            }
            double dirx = ex - cx, diry = ey - cy, dl = Math.Sqrt(dirx * dirx + diry * diry);
            bool hasDir = steps >= 2 && dl > 0.5;
            if (hasDir) { dirx /= dl; diry /= dl; }
            // 자기 선: 벽을 따라 D 걸음 안
            var own = new HashSet<int> { e };
            var q0 = new Queue<(int i, int d)>(); q0.Enqueue((e, 0));
            while (q0.Count > 0)
            {
                var (i, d) = q0.Dequeue();
                if (d >= D) continue;
                int ix = i % w, iy = i / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int qx = ix + dx, qy = iy + dy;
                        if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                        int q = qy * w + qx;
                        if (wall[q] == 0 || !own.Add(q)) continue;
                        q0.Enqueue((q, d + 1));
                    }
                if (own.Count > 20000) break;
            }
            // 선의 실제 끝: 방향대로 벽 안에서 나아감
            double tx = ex, ty = ey;
            if (hasDir)
                for (double t = 0.5; t < G + 30; t += 0.5)
                {
                    int px = (int)Math.Round(ex + dirx * t), py = (int)Math.Round(ey + diry * t);
                    if (px < 0 || py < 0 || px >= w || py >= h || wall[py * w + px] == 0) break;
                    tx = px; ty = py;
                }
            int Tx = (int)tx, Ty = (int)ty;
            // 앞쪽의 가장 가까운 남의 벽
            int R = G + 1; double best = R + 0.01; int bx = -1, by = -1;
            for (int y = Math.Max(0, Ty - R); y <= Math.Min(h - 1, Ty + R); y++)
                for (int x = Math.Max(0, Tx - R); x <= Math.Min(w - 1, Tx + R); x++)
                {
                    int q = y * w + x;
                    if (wall[q] == 0 || own.Contains(q)) continue;
                    double vx = x - Tx, vy = y - Ty, d = Math.Sqrt(vx * vx + vy * vy);
                    if (d >= best || d < 0.5) continue;
                    if (hasDir && d > 1.5 && (vx * dirx + vy * diry) / d < cosMax) continue;
                    best = d; bx = x; by = y;
                }
            if (bx < 0) return;
            bag.Add((k, new Seg(Tx, Ty, bx, by)));
        });
        // 같은 틈을 양쪽 끝에서 찾은 것은 하나만 (순서는 끝점 순서대로 → 결과가 늘 같게)
        var seen = new HashSet<(int, int, int, int)>();
        foreach (var (_, s) in bag.OrderBy(t => t.k))
        {
            var key = s.X0 < s.X1 || (s.X0 == s.X1 && s.Y0 <= s.Y1) ? (s.X0, s.Y0, s.X1, s.Y1) : (s.X1, s.Y1, s.X0, s.Y0);
            if (seen.Add(key)) result.Add(s);
        }
        return result;
    }

    /// <summary>Zhang–Suen 가늘게 (경계 후보만 다시 봄, 최대 maxIter 번 → 두께 2×maxIter 까지 뼈대)</summary>
    public static byte[] Thin(byte[] wall, int w, int h, int maxIter, CancellationToken ct = default)
    {
        var s = new byte[w * h];
        for (int i = 0; i < s.Length; i++) s[i] = wall[i] != 0 ? (byte)1 : (byte)0;
        // 바깥 테두리 한 줄은 배경으로 (이웃 계산을 단순하게)
        for (int x = 0; x < w; x++) { s[x] = 0; s[(h - 1) * w + x] = 0; }
        for (int y = 0; y < h; y++) { s[y * w] = 0; s[y * w + w - 1] = 0; }
        var cand = new List<int>();
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                if (s[i] != 0 && (s[i - 1] == 0 || s[i + 1] == 0 || s[i - w] == 0 || s[i + w] == 0)) cand.Add(i);
            }
        var stamp = new int[w * h]; int gen = 0;
        for (int it = 0; it < maxIter && cand.Count > 0; it++)
        {
            bool any = false;
            for (int sub = 0; sub < 2; sub++)
            {
                ct.ThrowIfCancellationRequested();
                var del = new List<int>();
                foreach (int i in cand)
                {
                    if (s[i] == 0) continue;
                    int p2 = s[i - w], p3 = s[i - w + 1], p4 = s[i + 1], p5 = s[i + w + 1], p6 = s[i + w], p7 = s[i + w - 1], p8 = s[i - 1], p9 = s[i - w - 1];
                    int b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                    if (b < 2 || b > 6) continue;
                    int a = (p2 == 0 && p3 == 1 ? 1 : 0) + (p3 == 0 && p4 == 1 ? 1 : 0) + (p4 == 0 && p5 == 1 ? 1 : 0) + (p5 == 0 && p6 == 1 ? 1 : 0)
                          + (p6 == 0 && p7 == 1 ? 1 : 0) + (p7 == 0 && p8 == 1 ? 1 : 0) + (p8 == 0 && p9 == 1 ? 1 : 0) + (p9 == 0 && p2 == 1 ? 1 : 0);
                    if (a != 1) continue;
                    if (sub == 0 ? (p2 * p4 * p6 != 0 || p4 * p6 * p8 != 0) : (p2 * p4 * p8 != 0 || p2 * p6 * p8 != 0)) continue;
                    del.Add(i);
                }
                if (del.Count == 0) continue;
                any = true;
                foreach (int i in del) s[i] = 0;
                // 다음 후보: 남은 후보 + 지운 점의 이웃
                gen++;
                var next = new List<int>(cand.Count);
                foreach (int i in cand) if (s[i] != 0 && stamp[i] != gen) { stamp[i] = gen; next.Add(i); }
                foreach (int i in del)
                    foreach (int q in new[] { i - w - 1, i - w, i - w + 1, i - 1, i + 1, i + w - 1, i + w, i + w + 1 })
                        if (q >= 0 && q < s.Length && s[q] != 0 && stamp[q] != gen) { stamp[q] = gen; next.Add(q); }
                cand = next;
            }
            if (!any) break;
        }
        return s;
    }

    /// <summary>선분 픽셀 (8-연결 브레젠험)</summary>
    public static IEnumerable<(int x, int y)> Pixels(Seg s)
    {
        int x0 = s.X0, y0 = s.Y0, x1 = s.X1, y1 = s.Y1;
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            yield return (x0, y0);
            if (x0 == x1 && y0 == y1) yield break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>통과 마스크에 닫는 선을 그어 막음 (시작점은 빼고). 막은 픽셀 목록</summary>
    public static List<int> Burn(byte[] pass, int w, int h, IEnumerable<Seg> segs, int keep = -1)
    {
        var o = new List<int>();
        foreach (var s in segs)
            foreach (var (x, y) in Pixels(s))
            {
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                int i = y * w + x;
                if (i == keep || pass[i] == 0) continue;
                pass[i] = 0; o.Add(i);
            }
        return o;
    }

    /// <summary>막았던 픽셀 중 칠한 영역에 붙은 것은 칠함 (닫는 선 자리까지 빈틈 없이)</summary>
    public static void Unburn(byte[] mask, int w, int h, List<int> burned)
    {
        for (int pass = 0; pass < 2; pass++)
            foreach (int i in burned)
            {
                if (mask[i] != 0) continue;
                int x = i % w;
                if ((x > 0 && mask[i - 1] != 0) || (x < w - 1 && mask[i + 1] != 0) || (i >= w && mask[i - w] != 0) || (i + w < mask.Length && mask[i + w] != 0)) mask[i] = 1;
            }
    }

    /// <summary>미리 보기용 벽: 빈 곳(투명·흰색)과 tol 이상 다른 픽셀 (rgba: 미리 곱하지 않은 RGBA)</summary>
    public static byte[] PreviewWall(byte[] px, int w, int h, double tol)
    {
        var o = new byte[w * h];
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, e = i + w, p = i * 4; i < e; i++, p += 4)
            {
                int a = px[p + 3];
                if (a <= tol) continue;                         // 투명과 비슷
                double k = a / 255.0;
                double dw = Math.Max(Math.Max(Math.Abs(px[p] * k - 255), Math.Abs(px[p + 1] * k - 255)), Math.Max(Math.Abs(px[p + 2] * k - 255), Math.Abs(a - 255)));
                if (dw <= tol) continue;                        // 흰색과 비슷
                o[i] = 1;
            }
        });
        return o;
    }

    /// <summary>덜 칠한 곳 메우기: 영역 안에 갇힌 구멍 중 가로·세로 모두 maxDim 이하인 것을 채움. 채운 픽셀 수</summary>
    public static int FillHoles(byte[] mask, int w, int h, Rect bounds, int maxDim)
    {
        if (maxDim <= 0 || bounds.IsEmpty) return 0;
        var b = bounds.Inflate(1).Clip(w, h);
        int bw = b.W, bh = b.H;
        var mark = new byte[bw * bh];             // 1 = 바깥과 이어진 빈 곳
        var q = new Stack<int>();
        void Seed(int lx, int ly) { int li = ly * bw + lx; if (mark[li] == 0 && mask[(b.Y + ly) * w + b.X + lx] == 0) { mark[li] = 1; q.Push(li); } }
        for (int x = 0; x < bw; x++) { Seed(x, 0); Seed(x, bh - 1); }
        for (int y = 0; y < bh; y++) { Seed(0, y); Seed(bw - 1, y); }
        void Spread()
        {
            while (q.Count > 0)
            {
                int li = q.Pop(), lx = li % bw, ly = li / bw;
                if (lx > 0) Seed(lx - 1, ly); if (lx < bw - 1) Seed(lx + 1, ly);
                if (ly > 0) Seed(lx, ly - 1); if (ly < bh - 1) Seed(lx, ly + 1);
            }
        }
        Spread();
        int filled = 0;
        var comp = new List<int>();
        for (int li0 = 0; li0 < mark.Length; li0++)
        {
            if (mark[li0] != 0 || mask[(b.Y + li0 / bw) * w + b.X + li0 % bw] != 0) continue;
            // 구멍 하나 (4-연결)
            comp.Clear(); mark[li0] = 2; q.Push(li0);
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            while (q.Count > 0)
            {
                int li = q.Pop(), lx = li % bw, ly = li / bw;
                comp.Add(li);
                x0 = Math.Min(x0, lx); x1 = Math.Max(x1, lx); y0 = Math.Min(y0, ly); y1 = Math.Max(y1, ly);
                foreach (var (nx, ny) in new[] { (lx - 1, ly), (lx + 1, ly), (lx, ly - 1), (lx, ly + 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                    int ni = ny * bw + nx;
                    if (mark[ni] != 0 || mask[(b.Y + ny) * w + b.X + nx] != 0) continue;
                    mark[ni] = 2; q.Push(ni);
                }
            }
            if (x1 - x0 + 1 <= maxDim && y1 - y0 + 1 <= maxDim)
                foreach (int li in comp) { mask[(b.Y + li / bw) * w + b.X + li % bw] = 1; filled++; }
        }
        return filled;
    }

    /// <summary>여러 칸 칠하기: 시작점마다 채운 마스크를 합침. 첫 점과 색이 다른 시작점(선 위 등)·이미 칠한 곳은 건너뜀</summary>
    public static (byte[] mask, Rect bounds, int regions) FillMulti(byte[] px, int w, int h, IList<(int x, int y)> seeds, Raster.FillOpt opt, CancellationToken ct = default)
    {
        var all = new byte[w * h]; var bounds = Rect.Empty; int regions = 0;
        if (seeds.Count == 0) return (all, bounds, 0);
        var (fx, fy) = seeds[0];
        foreach (var (x, y) in seeds)
        {
            if (x < 0 || y < 0 || x >= w || y >= h || all[y * w + x] != 0) continue;
            if (!SameColor(px, w, fx, fy, x, y, opt.Tolerance)) continue;
            var (m, b) = Raster.FillMask(px, w, h, x, y, opt, ct);
            if (b.IsEmpty) continue;
            for (int yy = b.Y; yy < b.B; yy++) for (int xx = b.X, i = yy * w + b.X; xx < b.R; xx++, i++) if (m[i] != 0) all[i] = 1;
            bounds = bounds.Union(b); regions++;
        }
        return (all, bounds, regions);
    }

    /// <summary>두 픽셀이 허용 오차 안에서 같은 색인지 (FillMask 와 같은 거리)</summary>
    public static bool SameColor(byte[] px, int w, int ax, int ay, int bx, int by, double tol)
    {
        int p = (ay * w + ax) * 4, q = (by * w + bx) * 4;
        double ka = px[p + 3] / 255.0, kb = px[q + 3] / 255.0;
        double d = Math.Max(Math.Max(Math.Abs(px[p] * ka - px[q] * kb), Math.Abs(px[p + 1] * ka - px[q + 1] * kb)), Math.Max(Math.Abs(px[p + 2] * ka - px[q + 2] * kb), Math.Abs(px[p + 3] - px[q + 3])));
        return d <= tol;
    }
}
