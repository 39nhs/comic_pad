// 픽셀 단위 알고리즘 — 웹 판 app/js/core/raster.js 를 그대로 옮김 (채우기, 팽창/침식, 경계).
// 입력 그림은 웹의 ImageData 와 같은 "미리 곱하지 않은" RGBA 바이트 배열 (w*h*4).
// 결과 마스크는 0/1 바이트 배열 (문서 크기). 순서까지 웹과 같게 해서 경쟁 되돌려 채우기 결과가 똑같이 나온다.
using ComicPadNative.Core;

namespace ComicPadNative.Modules.SelectFill;

public static class Raster
{
    /// <summary>JS Math.round 와 같은 반올림 (0.5 는 위로)</summary>
    public static int JsRound(double v) => (int)Math.Floor(v + 0.5);

    /// <summary>0/1 마스크를 정사각 커널로 팽창 (raster.js dilate). r&lt;=0 이면 src 그대로</summary>
    public static byte[] Dilate(byte[] src, int w, int h, double rr)
    {
        int r = JsRound(rr);
        if (r <= 0) return src;
        var tmp = new byte[w * h];
        var o = new byte[w * h];
        // 가로
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int count = 0;
            for (int x = 0; x < Math.Min(r, w); x++) count += src[row + x];
            for (int x = 0; x < w; x++)
            {
                if (x + r < w) count += src[row + x + r];
                if (x - r - 1 >= 0) count -= src[row + x - r - 1];
                tmp[row + x] = count > 0 ? (byte)1 : (byte)0;
            }
        }
        // 세로 (열마다 따로 세던 것을 줄 단위로 — 결과는 같고 메모리 접근만 빠름)
        var cnt = new int[w];
        for (int y = 0; y < Math.Min(r, h); y++) { int row = y * w; for (int x = 0; x < w; x++) cnt[x] += tmp[row + x]; }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            if (y + r < h) { int ar = (y + r) * w; for (int x = 0; x < w; x++) cnt[x] += tmp[ar + x]; }
            if (y - r - 1 >= 0) { int sr = (y - r - 1) * w; for (int x = 0; x < w; x++) cnt[x] -= tmp[sr + x]; }
            for (int x = 0; x < w; x++) o[row + x] = cnt[x] > 0 ? (byte)1 : (byte)0;
        }
        return o;
    }

    /// <summary>침식 (raster.js erode): 반전 → 팽창 → 반전</summary>
    public static byte[] Erode(byte[] src, int w, int h, double r)
    {
        if (r <= 0) return src;
        var inv = new byte[w * h];
        for (int i = 0; i < inv.Length; i++) inv[i] = src[i] != 0 ? (byte)0 : (byte)1;
        var d = Dilate(inv, w, h, r);
        if (ReferenceEquals(d, inv)) d = (byte[])inv.Clone();
        for (int i = 0; i < d.Length; i++) d[i] = d[i] != 0 ? (byte)0 : (byte)1;
        return d;
    }

    /// <summary>스캔라인 플러드필 (raster.js flood). pass[i]!=0 인 곳만 진행</summary>
    public static (byte[] mask, Rect bounds) Flood(byte[] pass, int w, int h, int sx, int sy)
    {
        var o = new byte[w * h];
        if (sx < 0 || sy < 0 || sx >= w || sy >= h || pass[sy * w + sx] == 0) return (o, Rect.Empty);
        int bx0 = sx, by0 = sy, bx1 = sx, by1 = sy;
        var stack = new Stack<long>();
        stack.Push(((long)sx << 32) | (uint)sy);
        while (stack.Count > 0)
        {
            long v = stack.Pop();
            int x = (int)(v >> 32), y = (int)(v & 0xFFFFFFFF);
            int row = y * w;
            if (pass[row + x] == 0 || o[row + x] != 0) continue;
            int x0 = x, x1 = x;
            while (x0 > 0 && pass[row + x0 - 1] != 0 && o[row + x0 - 1] == 0) x0--;
            while (x1 < w - 1 && pass[row + x1 + 1] != 0 && o[row + x1 + 1] == 0) x1++;
            Array.Fill(o, (byte)1, row + x0, x1 - x0 + 1);
            if (x0 < bx0) bx0 = x0; if (x1 > bx1) bx1 = x1;
            if (y < by0) by0 = y; if (y > by1) by1 = y;
            for (int ny = y - 1; ny <= y + 1; ny += 2)
            {
                if (ny < 0 || ny >= h) continue;
                int nrow = ny * w;
                bool inRun = false;
                for (int xx = x0; xx <= x1; xx++)
                {
                    int j = nrow + xx;
                    if (pass[j] != 0 && o[j] == 0) { if (!inRun) { stack.Push(((long)xx << 32) | (uint)ny); inRun = true; } }
                    else inRun = false;
                }
            }
        }
        return (o, new Rect(bx0, by0, bx1 - bx0 + 1, by1 - by0 + 1));
    }

    public static Rect MaskBounds(byte[] mask, int w, int h)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
                if (mask[row + x] != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; y1 = y; }
        }
        return x1 < 0 ? Rect.Empty : new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    public sealed class FillOpt
    {
        public double Tolerance = 10;   // 0~255
        public double Gap;              // 틈 막기 (px)
        public double Expand;           // 영역 확장 (px)
    }

    /// <summary>
    /// 색 영역 채우기 마스크 (raster.js fillMask). rgba: 미리 곱하지 않은 RGBA, (sx, sy): 시작점.
    /// 틈 막기: 선을 gap/2 만큼 두껍게 해서 막고 플러드, 그다음 모든 영역이 동시에 한 칸씩 자라며
    /// (먼저 닿는 쪽이 차지) 두껍게 한 만큼을 되돌려 채운다 → 틈 너머로 새지 않음.
    /// </summary>
    public static (byte[] mask, Rect bounds) FillMask(byte[] px, int w, int h, int sx, int sy, FillOpt opt, CancellationToken ct = default)
    {
        double tol = opt.Tolerance;
        int gap = JsRound(opt.Gap), expand = JsRound(opt.Expand);
        int n = w * h;
        int si = (sy * w + sx) * 4;
        int sa = px[si + 3];
        double sr = px[si] * sa / 255.0, sg = px[si + 1] * sa / 255.0, sb = px[si + 2] * sa / 255.0;
        var match = new byte[n];
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, e = i + w, p = i * 4; i < e; i++, p += 4)
            {
                int a = px[p + 3];
                double k = a / 255.0;
                double d = Math.Max(Math.Max(Math.Abs(px[p] * k - sr), Math.Abs(px[p + 1] * k - sg)), Math.Max(Math.Abs(px[p + 2] * k - sb), Math.Abs(a - sa)));
                match[i] = d <= tol ? (byte)1 : (byte)0;
            }
        });
        ct.ThrowIfCancellationRequested();
        // 틈 막기: gap(px) 너비 이하의 빈틈은 선이 이어진 것으로 (선을 gap/2 만큼 두껍게 해서 막음)
        int r = (int)Math.Ceiling(gap / 2.0);
        var pass = match;
        bool gapped = false;
        if (r > 0)
        {
            var wall = new byte[n];
            for (int i = 0; i < n; i++) wall[i] = match[i] != 0 ? (byte)0 : (byte)1;
            var thick = Dilate(wall, w, h, r);
            pass = new byte[n];
            for (int i = 0; i < n; i++) pass[i] = match[i] != 0 && thick[i] == 0 ? (byte)1 : (byte)0;
            if (pass[sy * w + sx] == 0) pass = match; else gapped = true;
        }
        ct.ThrowIfCancellationRequested();
        var (mask, bounds) = Flood(pass, w, h, sx, sy);
        if (bounds.IsEmpty) return (mask, Rect.Empty);
        if (gapped)
        {
            // 두껍게 한 만큼 선 쪽으로 되돌려 채우기. 다른 영역도 동시에 자라게 해서 (먼저 닿는 쪽이 차지)
            var own = new byte[n];                 // 0 = 아직, 1 = 이 영역, 2 = 다른 영역
            var front = new List<int>();
            for (int i = 0; i < n; i++) if (pass[i] != 0) { own[i] = mask[i] != 0 ? (byte)1 : (byte)2; front.Add(i); }
            Span<int> nb = stackalloc int[4];
            for (int st = 0; st <= r && front.Count > 0; st++)
            {
                ct.ThrowIfCancellationRequested();
                var nf = new List<int>();
                foreach (int i in front)
                {
                    int x = i % w; byte o = own[i];
                    nb[0] = x > 0 ? i - 1 : -1; nb[1] = x < w - 1 ? i + 1 : -1; nb[2] = i - w; nb[3] = i + w;
                    for (int q = 0; q < 4; q++)
                    {
                        int j = nb[q];
                        if (j < 0 || j >= n || own[j] != 0 || match[j] == 0) continue;
                        own[j] = o; nf.Add(j);
                    }
                }
                front = nf;
            }
            for (int i = 0; i < n; i++) if (own[i] == 1) mask[i] = 1;
        }
        if (expand > 0) mask = Dilate(mask, w, h, expand);
        return (mask, MaskBounds(mask, w, h));
    }

    /// <summary>상자 흐림 (heal.js boxBlur): 가장자리는 끝 값을 늘려 씀. 누적은 double, 저장은 float (웹 Float32Array 와 같음)</summary>
    public static float[] BoxBlur(float[] src, int w, int h, int r, int passes = 2)
    {
        var a = src;
        int k = 2 * r + 1;
        int Cx(int v) => v < 0 ? 0 : v >= w ? w - 1 : v;
        int Cy(int v) => v < 0 ? 0 : v >= h ? h - 1 : v;
        for (int p = 0; p < passes; p++)
        {
            var b = new float[a.Length]; var o = new float[a.Length];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                double s = 0;
                for (int x = -r; x <= r; x++) s += a[row + Cx(x)];
                for (int x = 0; x < w; x++) { b[row + x] = (float)(s / k); s += a[row + Cx(x + r + 1)] - a[row + Cx(x - r)]; }
            }
            for (int x = 0; x < w; x++)
            {
                double s = 0;
                for (int y = -r; y <= r; y++) s += b[Cy(y) * w + x];
                for (int y = 0; y < h; y++) { o[y * w + x] = (float)(s / k); s += b[Cy(y + r + 1) * w + x] - b[Cy(y - r) * w + x]; }
            }
            a = o;
        }
        return a;
    }

    /// <summary>
    /// 가우스 흐림 근사 (캔버스 filter: blur(σpx) 대신): 상자 흐림 3번, 바깥은 투명(0)으로 봄.
    /// a: 0~255 값 (w*h), 결과도 0~255 로 반올림
    /// </summary>
    public static byte[] GaussBlurZero(byte[] a, int w, int h, double sigma)
    {
        if (sigma <= 0) return (byte[])a.Clone();
        // 표준 "상자 3개로 가우스" 크기
        int nBox = 3;
        double wIdeal = Math.Sqrt(12 * sigma * sigma / nBox + 1);
        int wl = (int)Math.Floor(wIdeal); if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - nBox * wl * wl - 4 * nBox * wl - 3 * nBox) / (-4.0 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        var cur = new float[a.Length];
        for (int i = 0; i < a.Length; i++) cur[i] = a[i];
        var tmp = new float[a.Length];
        for (int bi = 0; bi < nBox; bi++)
        {
            int r = ((bi < m ? wl : wu) - 1) / 2;
            BoxZeroH(cur, tmp, w, h, r);
            BoxZeroV(tmp, cur, w, h, r);
        }
        var o = new byte[a.Length];
        for (int i = 0; i < o.Length; i++) o[i] = (byte)Math.Clamp((int)(cur[i] + 0.5f), 0, 255);
        return o;
    }
    static void BoxZeroH(float[] s, float[] d, int w, int h, int r)
    {
        float k = 2 * r + 1;
        Parallel.For(0, h, y =>
        {
            int row = y * w; double acc = 0;
            for (int x = 0; x <= Math.Min(r, w - 1); x++) acc += s[row + x];
            for (int x = 0; x < w; x++)
            {
                d[row + x] = (float)(acc / k);
                if (x + r + 1 < w) acc += s[row + x + r + 1];
                if (x - r >= 0) acc -= s[row + x - r];
            }
        });
    }
    static void BoxZeroV(float[] s, float[] d, int w, int h, int r)
    {
        float k = 2 * r + 1;
        var acc = new double[w];
        for (int y = 0; y <= Math.Min(r, h - 1); y++) for (int x = 0; x < w; x++) acc[x] += s[y * w + x];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++) d[row + x] = (float)(acc[x] / k);
            if (y + r + 1 < h) { int ar = (y + r + 1) * w; for (int x = 0; x < w; x++) acc[x] += s[ar + x]; }
            if (y - r >= 0) { int sr = (y - r) * w; for (int x = 0; x < w; x++) acc[x] -= s[sr + x]; }
        }
    }

    // ---- 디더 (core/pixel.js) -------------------------------------------------------------
    static readonly int[][] Bayer2 = { new[] { 0, 2 }, new[] { 3, 1 } };
    static readonly int[][] Bayer4 = { new[] { 0, 8, 2, 10 }, new[] { 12, 4, 14, 6 }, new[] { 3, 11, 1, 9 }, new[] { 15, 7, 13, 5 } };
    /// <summary>(x, y) 칸의 문턱값 0~1 (bayerAt)</summary>
    public static double BayerAt(int x, int y, int n = 4)
    {
        var m = n == 2 ? Bayer2 : Bayer4; if (n != 2) n = 4;
        return (m[((y % n) + n) % n][((x % n) + n) % n] + 0.5) / (n * n);
    }
    /// <summary>디더 무늬에서 (문서 좌표) 이 칸을 칠하는지 (ditherTile: level &gt; bayer - 1e-6)</summary>
    public static bool DitherOn(double level, int n, int x, int y) => level > BayerAt(x, y, n) - 1e-6;

    // ---- 색 ---------------------------------------------------------------------------
    /// <summary>미리 곱한 BGRA → 미리 곱하지 않은 RGBA 4바이트 (getImageData 처럼 반올림)</summary>
    public static void ToRgba(uint c, byte[] dst, int p)
    {
        uint a = c >> 24;
        if (a == 0) { dst[p] = dst[p + 1] = dst[p + 2] = dst[p + 3] = 0; return; }
        uint r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        if (a != 255) { r = Math.Min(255, (r * 255 + a / 2) / a); g = Math.Min(255, (g * 255 + a / 2) / a); b = Math.Min(255, (b * 255 + a / 2) / a); }
        dst[p] = (byte)r; dst[p + 1] = (byte)g; dst[p + 2] = (byte)b; dst[p + 3] = (byte)a;
    }
    /// <summary>RGBA(미리 곱하지 않음) → 미리 곱한 BGRA</summary>
    public static uint FromRgba(byte r, byte g, byte b, byte a) => Px.Premul(Px.Pack(a, r, g, b));
}
