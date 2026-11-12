// 내용 인식 채우기 (웹 판 core/inpaint.js·inpaintworker.js 이식): 여러 크기 PatchMatch + 투표 (Barnes 외 2009, Wexler 외 2007 방식).
// AI 모델이 아니라 계산만으로 선택한 곳을 주변 무늬로 메운다. 픽셀은 미리 곱한 ARGB 그대로 다룬다 (투명한 곳도 자연스럽게).
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Vision;

public static class Inpaint
{
    const int R = 3, PS = 2 * R + 1;

    sealed class Level { public float[] Img; public byte[] Hole; public int W, H; }

    /// <summary>
    /// 구멍 채우기. img: 미리 곱한 ARGB w*h, hole: 1 = 채울 곳. 결과는 새 배열 (구멍 밖은 그대로).
    /// seed 를 고정하면 결과가 늘 같다.
    /// </summary>
    public static uint[] Fill(uint[] img, byte[] hole, int w, int h, IProgress<double> prog = null, CancellationToken ct = default, int seed = 12345)
    {
        var rnd = new Random(seed);
        var f = new float[w * h * 4];
        for (int i = 0; i < w * h; i++) { uint c = img[i]; f[i * 4] = (c >> 16) & 255; f[i * 4 + 1] = (c >> 8) & 255; f[i * 4 + 2] = c & 255; f[i * 4 + 3] = c >> 24; }
        // 여러 크기 단계 (가장 작은 단계에서 구멍이 몇 픽셀이 되도록)
        int hx0 = w, hy0 = h, hx1 = 0, hy1 = 0;
        for (int i = 0; i < w * h; i++) if (hole[i] != 0) { int x = i % w, y = i / w; hx0 = Math.Min(hx0, x); hy0 = Math.Min(hy0, y); hx1 = Math.Max(hx1, x); hy1 = Math.Max(hy1, y); }
        int holeSize = Math.Max(1, Math.Max(hx1 - hx0 + 1, hy1 - hy0 + 1));
        var levels = new List<Level> { new() { Img = f, Hole = (byte[])hole.Clone(), W = w, H = h } };
        while (levels.Count < 8)
        {
            var L = levels[^1];
            if (Math.Min(L.W, L.H) < 24 || holeSize >> levels.Count < 6) break;
            levels.Add(Down(L));
        }
        var top = levels[^1];
        DiffuseFill(top.Img, top.Hole, top.W, top.H);
        int[] prev = null; int prevW = 0, prevH = 0;
        int total = levels.Count;
        for (int li = total - 1; li >= 0; li--)
        {
            ct.ThrowIfCancellationRequested();
            var L = levels[li];
            if (li < total - 1)
            {
                // 위 단계 결과를 늘려 와서 이 단계 구멍을 먼저 칠해 두기
                var up = levels[li + 1]; int pw = up.W, ph = up.H;
                for (int i = 0; i < L.W * L.H; i++)
                {
                    if (L.Hole[i] == 0) continue;
                    int x = i % L.W, y = i / L.W, X = Math.Min(pw - 1, x >> 1), Y = Math.Min(ph - 1, y >> 1), j = (Y * pw + X) * 4;
                    for (int c = 0; c < 4; c++) L.Img[i * 4 + c] = up.Img[j + c];
                }
            }
            Func<int, int> upNnf = null;
            if (prev != null)
            {
                var pv = prev; int pW = prevW, pH = prevH, LW = L.W, LH = L.H;
                upNnf = t =>
                {
                    int x = t % LW, y = t / LW, X = Math.Min(pW - 1, x >> 1), Y = Math.Min(pH - 1, y >> 1), s = pv[Y * pW + X];
                    if (s < 0) return -1;
                    int sx = (s % pW) * 2 + (x & 1), sy = (s / pW) * 2 + (y & 1);
                    return sx < LW && sy < LH ? sy * LW + sx : -1;
                };
            }
            int iters = li == 0 ? 2 : li == total - 1 ? 6 : 4;
            int lvl = li;
            var nnf = Solve(L, upNnf, iters, rnd, fr => prog?.Report(((total - 1 - lvl) + fr) / total), ct);
            if (nnf == null) { if (li == 0) DiffuseFill(L.Img, L.Hole, L.W, L.H); prev = null; continue; }
            prev = nnf; prevW = L.W; prevH = L.H;
        }
        var outPx = new uint[w * h];
        for (int i = 0; i < w * h; i++)
        {
            if (hole[i] == 0) { outPx[i] = img[i]; continue; }
            uint a = B(f[i * 4 + 3]), r = Math.Min(a, B(f[i * 4])), g = Math.Min(a, B(f[i * 4 + 1])), bl = Math.Min(a, B(f[i * 4 + 2]));
            outPx[i] = (a << 24) | (r << 16) | (g << 8) | bl;
        }
        return outPx;
    }
    static uint B(float v) => (uint)Math.Clamp((int)(v + 0.5f), 0, 255);

    static Level Down(Level L)
    {
        int W = Math.Max(1, L.W >> 1), H = Math.Max(1, L.H >> 1);
        var o = new float[W * H * 4]; var oh = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int n = 0; byte any = 0; float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(L.W - 1, x * 2 + dx), sy = Math.Min(L.H - 1, y * 2 + dy), i = sy * L.W + sx;
                        if (L.Hole[i] != 0) { any = 1; continue; }
                        a0 += L.Img[i * 4]; a1 += L.Img[i * 4 + 1]; a2 += L.Img[i * 4 + 2]; a3 += L.Img[i * 4 + 3]; n++;
                    }
                int j = y * W + x;
                oh[j] = any;
                if (n > 0) { o[j * 4] = a0 / n; o[j * 4 + 1] = a1 / n; o[j * 4 + 2] = a2 / n; o[j * 4 + 3] = a3 / n; }
            }
        return new Level { Img = o, Hole = oh, W = W, H = H };
    }

    static readonly (int dx, int dy)[] N8 = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) };

    /// <summary>구멍을 주변 색으로 차례차례 채우기 (가장 작은 크기에서 시작값)</summary>
    static void DiffuseFill(float[] img, byte[] hole, int w, int h)
    {
        var known = new byte[w * h];
        int left = 0;
        for (int i = 0; i < known.Length; i++) { known[i] = (byte)(hole[i] != 0 ? 0 : 1); if (known[i] == 0) left++; }
        int guard = 0;
        while (left > 0 && guard++ < w + h)
        {
            var next = (byte[])known.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (known[i] != 0) continue;
                    int n = 0; float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                    foreach (var (dx, dy) in N8)
                    {
                        int X = x + dx, Y = y + dy;
                        if (X < 0 || Y < 0 || X >= w || Y >= h || known[Y * w + X] == 0) continue;
                        int j = Y * w + X;
                        a0 += img[j * 4]; a1 += img[j * 4 + 1]; a2 += img[j * 4 + 2]; a3 += img[j * 4 + 3]; n++;
                    }
                    if (n > 0) { img[i * 4] = a0 / n; img[i * 4 + 1] = a1 / n; img[i * 4 + 2] = a2 / n; img[i * 4 + 3] = a3 / n; next[i] = 1; left--; }
                }
            known = next;
        }
    }

    static byte[] Dilate(byte[] mask, int w, int h, int r)
    {
        var t = new byte[mask.Length]; var o = new byte[mask.Length];
        for (int y = 0; y < h; y++)
        {
            int last = -1_000_000_000;
            for (int x = 0; x < w; x++) { if (mask[y * w + x] != 0) last = x; t[y * w + x] = (byte)(x - last <= r ? 1 : 0); }
            last = 1_000_000_000;
            for (int x = w - 1; x >= 0; x--) { if (mask[y * w + x] != 0) last = x; if (last - x <= r) t[y * w + x] = 1; }
        }
        for (int x = 0; x < w; x++)
        {
            int last = -1_000_000_000;
            for (int y = 0; y < h; y++) { if (t[y * w + x] != 0) last = y; o[y * w + x] = (byte)(y - last <= r ? 1 : 0); }
            last = 1_000_000_000;
            for (int y = h - 1; y >= 0; y--) { if (t[y * w + x] != 0) last = y; if (last - y <= r) o[y * w + x] = 1; }
        }
        return o;
    }

    static int[] Solve(Level L, Func<int, int> nnfIn, int iters, Random rnd, Action<double> progress, CancellationToken ct)
    {
        var img = L.Img; var hole = L.Hole; int w = L.W, h = L.H;
        var near = Dilate(hole, w, h, R);                          // 원본 조각이 구멍에 닿으면 안 됨
        var valid = new List<int>();
        for (int y = R; y < h - R; y++) for (int x = R; x < w - R; x++) if (near[y * w + x] == 0) valid.Add(y * w + x);
        if (valid.Count == 0) return null;
        var targets = new List<int>();
        for (int i = 0; i < w * h; i++) if (near[i] != 0) targets.Add(i);
        bool IsValid(int i) { int x = i % w, y = i / w; return x >= R && y >= R && x < w - R && y < h - R && near[i] == 0; }
        var nnf = new int[w * h]; Array.Fill(nnf, -1);
        var cost = new float[w * h]; Array.Fill(cost, float.PositiveInfinity);
        float Dist(int t, int s, float best)
        {
            int tx = t % w, ty = t / w, sx = s % w, sy = s / w;
            float d = 0;
            for (int dy = -R; dy <= R; dy++)
            {
                int TY = Math.Clamp(ty + dy, 0, h - 1), SY = sy + dy;
                for (int dx = -R; dx <= R; dx++)
                {
                    int TX = Math.Clamp(tx + dx, 0, w - 1);
                    int a = (TY * w + TX) * 4, b = (SY * w + sx + dx) * 4;
                    float e0 = img[a] - img[b], e1 = img[a + 1] - img[b + 1], e2 = img[a + 2] - img[b + 2], e3 = img[a + 3] - img[b + 3];
                    d += e0 * e0 + e1 * e1 + e2 * e2 + e3 * e3;
                }
                if (d >= best) return d;
            }
            return d;
        }
        void TryS(int t, int s) { if (s < 0 || s >= w * h || !IsValid(s)) return; float d = Dist(t, s, cost[t]); if (d < cost[t]) { cost[t] = d; nnf[t] = s; } }
        foreach (int t in targets)
        {
            int s = nnfIn != null ? nnfIn(t) : -1;
            if (s < 0 || !IsValid(s)) s = valid[rnd.Next(valid.Count)];
            nnf[t] = s; cost[t] = Dist(t, s, float.PositiveInfinity);
        }
        void Vote()
        {
            var acc = new float[w * h * 4]; var wsum = new float[w * h];
            double mean = 0; foreach (int t in targets) mean += cost[t]; mean = mean / targets.Count / (PS * PS) + 1;
            foreach (int t in targets)
            {
                int s = nnf[t], tx = t % w, ty = t / w, sx = s % w, sy = s / w;
                float wt = (float)Math.Exp(-cost[t] / (PS * PS) / (2 * mean));
                for (int dy = -R; dy <= R; dy++)
                {
                    int Y = ty + dy; if (Y < 0 || Y >= h) continue;
                    for (int dx = -R; dx <= R; dx++)
                    {
                        int X = tx + dx; if (X < 0 || X >= w) continue;
                        int p = Y * w + X; if (hole[p] == 0) continue;
                        int q = ((sy + dy) * w + sx + dx) * 4;
                        acc[p * 4] += img[q] * wt; acc[p * 4 + 1] += img[q + 1] * wt; acc[p * 4 + 2] += img[q + 2] * wt; acc[p * 4 + 3] += img[q + 3] * wt;
                        wsum[p] += wt;
                    }
                }
            }
            for (int p = 0; p < w * h; p++) if (hole[p] != 0 && wsum[p] > 0) for (int c = 0; c < 4; c++) img[p * 4 + c] = acc[p * 4 + c] / wsum[p];
        }
        int maxR = Math.Max(w, h);
        for (int it = 0; it < iters; it++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                ct.ThrowIfCancellationRequested();
                bool rev = (it + pass) % 2 == 1; int dd = rev ? -1 : 1;
                for (int k = 0; k < targets.Count; k++)
                {
                    int t = targets[rev ? targets.Count - 1 - k : k];
                    int tx = t % w;
                    // 이웃에서 전파
                    if (tx - dd >= 0 && tx - dd < w) { int n = nnf[t - dd]; if (n >= 0) TryS(t, n + dd); }
                    int up = t - dd * w;
                    if (up >= 0 && up < w * h) { int n = nnf[up]; if (n >= 0) TryS(t, n + dd * w); }
                    // 무작위 탐색 (범위를 반씩 줄이며)
                    int s0 = nnf[t], sx = s0 % w, sy = s0 / w;
                    for (int rr = maxR; rr >= 1; rr >>= 1)
                    {
                        int X = sx + (int)Math.Round((rnd.NextDouble() * 2 - 1) * rr), Y = sy + (int)Math.Round((rnd.NextDouble() * 2 - 1) * rr);
                        if (X >= R && Y >= R && X < w - R && Y < h - R) TryS(t, Y * w + X);
                    }
                }
            }
            // 구멍 색이 바뀌었으니 비용 다시
            Vote();
            foreach (int t in targets) cost[t] = Dist(t, nnf[t], float.PositiveInfinity);
            progress?.Invoke((it + 1.0) / iters);
        }
        return nnf;
    }

    // ---------------------------------------------------------------- 문서에 적용
    /// <summary>줄이기·늘리기 (미리 곱한 ARGB, 겹치는 넓이 평균 / 쌍선형)</summary>
    public static uint[] Resize(uint[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return (uint[])src.Clone();
        var o = new uint[dw * dh];
        double kx = (double)sw / dw, ky = (double)sh / dh;
        Parallel.For(0, dh, y =>
        {
            for (int x = 0; x < dw; x++)
            {
                double a = 0, r = 0, g = 0, b = 0, wsum = 0;
                if (kx > 1 || ky > 1)
                {
                    double x0 = x * kx, x1 = x0 + kx, y0 = y * ky, y1 = y0 + ky;
                    for (int sy = (int)y0; sy < Math.Min(sh, (int)Math.Ceiling(y1)); sy++)
                    {
                        double wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                        for (int sx = (int)x0; sx < Math.Min(sw, (int)Math.Ceiling(x1)); sx++)
                        {
                            double wgt = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                            uint c = src[sy * sw + sx];
                            a += (c >> 24) * wgt; r += ((c >> 16) & 255) * wgt; g += ((c >> 8) & 255) * wgt; b += (c & 255) * wgt; wsum += wgt;
                        }
                    }
                }
                else
                {
                    double fx = Math.Clamp((x + 0.5) * kx - 0.5, 0, sw - 1), fy = Math.Clamp((y + 0.5) * ky - 0.5, 0, sh - 1);
                    int ix = (int)fx, iy = (int)fy, ix1 = Math.Min(sw - 1, ix + 1), iy1 = Math.Min(sh - 1, iy + 1);
                    double tx = fx - ix, ty = fy - iy;
                    void Add(int px, int py, double wgt) { uint c = src[py * sw + px]; a += (c >> 24) * wgt; r += ((c >> 16) & 255) * wgt; g += ((c >> 8) & 255) * wgt; b += (c & 255) * wgt; wsum += wgt; }
                    Add(ix, iy, (1 - tx) * (1 - ty)); Add(ix1, iy, tx * (1 - ty)); Add(ix, iy1, (1 - tx) * ty); Add(ix1, iy1, tx * ty);
                }
                if (wsum <= 0) continue;
                uint A = (uint)Math.Clamp(a / wsum + 0.5, 0, 255);
                o[y * dw + x] = (A << 24) | ((uint)Math.Min(A, Math.Clamp(r / wsum + 0.5, 0, 255)) << 16) | ((uint)Math.Min(A, Math.Clamp(g / wsum + 0.5, 0, 255)) << 8) | (uint)Math.Min(A, Math.Clamp(b / wsum + 0.5, 0, 255));
            }
        });
        return o;
    }

    /// <summary>계산할 범위: 선택 상자 + 둘레 (선택 크기 × margin, 최소 48px)</summary>
    public static Rect FillRegion(Document d, double margin = 1)
    {
        var b = d.Sel.Bounds();
        if (b.IsEmpty) return Rect.Empty;
        int m = Math.Max(48, (int)Math.Round(Math.Max(b.W, b.H) * margin));
        return Rect.FromLTRB(Math.Max(0, b.X - m), Math.Max(0, b.Y - m), Math.Min(d.W, b.R + m), Math.Min(d.H, b.B + m));
    }

    /// <summary>
    /// 선택 영역을 주변으로 채운 그림 (rect 크기, 미리 곱한 ARGB). full: rect 부분의 원본 픽셀 (레이어 또는 보이는 그림 전체).
    /// maxSide: 이보다 크면 줄여서 계산 (웹 판과 같음)
    /// </summary>
    public static uint[] ContentAwareFill(Document d, Rect rect, uint[] full, int maxSide = 1200, IProgress<double> prog = null, CancellationToken ct = default)
    {
        double s = Math.Min(1, (double)maxSide / Math.Max(rect.W, rect.H));
        int W = Math.Max(8, (int)Math.Round(rect.W * s)), H = Math.Max(8, (int)Math.Round(rect.H * s));
        var small = Resize(full, rect.W, rect.H, W, H);
        // 선택 마스크를 같은 크기로 (알파 8 넘으면 구멍)
        var mk = new uint[rect.W * rect.H];
        for (int y = 0; y < rect.H; y++) for (int x = 0; x < rect.W; x++) { uint a = d.Sel.At(rect.X + x, rect.Y + y); mk[y * rect.W + x] = a << 24; }
        var mks = Resize(mk, rect.W, rect.H, W, H);
        var hole = new byte[W * H];
        for (int i = 0; i < hole.Length; i++) hole[i] = (byte)((mks[i] >> 24) > 8 ? 1 : 0);
        // 가장자리 한 줄 더 (반쯤 선택된 테두리도 새로 채움)
        var grown = (byte[])hole.Clone();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (hole[y * W + x] != 0)
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) { int X = x + dx, Y = y + dy; if (X >= 0 && Y >= 0 && X < W && Y < H) grown[Y * W + X] = 1; }
        var outPx = Fill(small, grown, W, H, prog, ct);
        return s >= 1 ? outPx : Resize(outPx, W, H, rect.W, rect.H);
    }

    /// <summary>채운 그림을 선택 모양대로 레이어에 (newLayer 면 빈 레이어에 얹음). 기록은 부른 쪽이 PixelEdit 로</summary>
    public static void Apply(Document d, Surface target, PixelEdit edit, uint[] filled, Rect rect, bool replace)
    {
        edit.Touch(rect);
        var cur = target.Read(rect);
        for (int y = 0; y < rect.H; y++)
            for (int x = 0; x < rect.W; x++)
            {
                int i = y * rect.W + x;
                uint k = d.Sel.At(rect.X + x, rect.Y + y);
                if (k == 0) continue;
                uint f = Px.Scale(filled[i], k);
                // 웹 판: (이 레이어면) 선택 모양만큼 지우고 → 선택 모양으로 자른 채움을 얹음
                cur[i] = replace ? Px.Over(f, Px.Scale(cur[i], 255 - k)) : Px.Over(f, cur[i]);
            }
        target.Write(rect, cur);
    }
}
