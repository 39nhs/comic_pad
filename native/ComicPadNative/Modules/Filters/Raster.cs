// 픽셀 알고리즘 모음: 가우스 흐림(SVG 규격의 상자 흐림 3번 근사), 팽창(최댓값), 침식, 선택 영역 섞기 등.
// 값 배열은 float 평면(채널 하나, w*h, 바깥은 0 = 투명) 으로 다룬다.
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

public static class Raster
{
    // ---- 가우스 흐림 ---------------------------------------------------------------
    /// <summary>
    /// 평면을 표준편차 sigma 로 흐리게 (SVG feGaussianBlur 규격: sigma ≥ 2 면 상자 흐림 3번, 작으면 진짜 가우스).
    /// 바깥은 0 으로 본다. 결과는 같은 배열에.
    /// </summary>
    public static void Blur(float[] p, int w, int h, double sigmaX, double sigmaY)
    {
        if (sigmaX > 0) BlurAxis(p, w, h, sigmaX, true);
        if (sigmaY > 0) BlurAxis(p, w, h, sigmaY, false);
    }

    static void BlurAxis(float[] p, int w, int h, double s, bool horiz)
    {
        int len = horiz ? w : h, lines = horiz ? h : w;
        var a = new float[len]; var b = new float[len];
        if (s < 2)
        {
            // 작은 반경: 진짜 가우스 커널
            int rad = Math.Max(1, (int)Math.Ceiling(s * 3));
            var k = new float[rad * 2 + 1]; double sum = 0;
            for (int i = -rad; i <= rad; i++) { double v = Math.Exp(-i * i / (2 * s * s)); k[i + rad] = (float)v; sum += v; }
            for (int i = 0; i < k.Length; i++) k[i] = (float)(k[i] / sum);
            for (int ln = 0; ln < lines; ln++)
            {
                Get(p, w, ln, horiz, a, len);
                for (int x = 0; x < len; x++)
                {
                    float acc = 0;
                    int lo = Math.Max(0, x - rad), hi = Math.Min(len - 1, x + rad);
                    for (int j = lo; j <= hi; j++) acc += a[j] * k[j - x + rad];
                    b[x] = acc;
                }
                Put(p, w, ln, horiz, b, len);
            }
            return;
        }
        int d = (int)Math.Floor(s * 3 * Math.Sqrt(2 * Math.PI) / 4 + 0.5);
        for (int ln = 0; ln < lines; ln++)
        {
            Get(p, w, ln, horiz, a, len);
            if (d % 2 == 1)
            {
                int r = (d - 1) / 2;
                Box(a, b, len, r, r); Box(b, a, len, r, r); Box(a, b, len, r, r);
            }
            else
            {
                int hf = d / 2;
                Box(a, b, len, hf, hf - 1);       // 왼쪽으로 반 칸
                Box(b, a, len, hf - 1, hf);       // 오른쪽으로 반 칸
                Box(a, b, len, hf, hf);           // d+1 가운데
            }
            Put(p, w, ln, horiz, b, len);
        }
    }

    /// <summary>상자 흐림: dst[x] = 평균(src[x-lo .. x+hi]), 바깥은 0</summary>
    static void Box(float[] src, float[] dst, int n, int lo, int hi)
    {
        float inv = 1f / (lo + hi + 1);
        double acc = 0;
        for (int j = 0; j <= Math.Min(n - 1, hi); j++) acc += src[j];
        for (int x = 0; x < n; x++)
        {
            dst[x] = (float)(acc * inv);
            int add = x + hi + 1, sub = x - lo;
            if (add < n) acc += src[add];
            if (sub >= 0) acc -= src[sub];
        }
    }

    static void Get(float[] p, int w, int ln, bool horiz, float[] a, int len)
    {
        if (horiz) Array.Copy(p, ln * w, a, 0, len);
        else for (int i = 0; i < len; i++) a[i] = p[i * w + ln];
    }
    static void Put(float[] p, int w, int ln, bool horiz, float[] a, int len)
    {
        if (horiz) Array.Copy(a, 0, p, ln * w, len);
        else for (int i = 0; i < len; i++) p[i * w + ln] = a[i];
    }

    // ---- 팽창 (feMorphology dilate) ----------------------------------------------------
    /// <summary>사각 커널(2r+1) 최댓값. 바깥은 0. van Herk 방식이라 반경이 커도 빠름</summary>
    public static void Dilate(float[] p, int w, int h, int r)
    {
        if (r <= 0) return;
        int maxLen = Math.Max(w, h);
        var line = new float[maxLen]; var outl = new float[maxLen];
        for (int y = 0; y < h; y++) { Array.Copy(p, y * w, line, 0, w); MaxLine(line, outl, w, r); Array.Copy(outl, 0, p, y * w, w); }
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++) line[y] = p[y * w + x];
            MaxLine(line, outl, h, r);
            for (int y = 0; y < h; y++) p[y * w + x] = outl[y];
        }
    }

    static void MaxLine(float[] src, float[] dst, int n, int r)
    {
        int k = 2 * r + 1, N = n + 2 * r;
        var pad = new float[N]; Array.Copy(src, 0, pad, r, n);
        var g = new float[N]; var hh = new float[N];
        for (int i = 0; i < N; i++) g[i] = i % k == 0 ? pad[i] : Math.Max(g[i - 1], pad[i]);
        for (int i = N - 1; i >= 0; i--) hh[i] = (i % k == k - 1 || i == N - 1) ? pad[i] : Math.Max(hh[i + 1], pad[i]);
        for (int i = 0; i < n; i++) dst[i] = Math.Max(hh[i], g[Math.Min(N - 1, i + k - 1)]);
    }

    // ---- 0/1 마스크 팽창·침식 (웹 raster.js dilate/erode) -----------------------------------
    public static byte[] DilateMask(byte[] src, int w, int h, int r)
    {
        if (r <= 0) return src;
        var tmp = new byte[w * h]; var outp = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int o = y * w, count = 0;
            for (int x = 0; x < Math.Min(r, w); x++) count += src[o + x];
            for (int x = 0; x < w; x++)
            {
                if (x + r < w) count += src[o + x + r];
                if (x - r - 1 >= 0) count -= src[o + x - r - 1];
                tmp[o + x] = (byte)(count > 0 ? 1 : 0);
            }
        }
        for (int x = 0; x < w; x++)
        {
            int count = 0;
            for (int y = 0; y < Math.Min(r, h); y++) count += tmp[y * w + x];
            for (int y = 0; y < h; y++)
            {
                if (y + r < h) count += tmp[(y + r) * w + x];
                if (y - r - 1 >= 0) count -= tmp[(y - r - 1) * w + x];
                outp[y * w + x] = (byte)(count > 0 ? 1 : 0);
            }
        }
        return outp;
    }
    public static byte[] ErodeMask(byte[] src, int w, int h, int r)
    {
        if (r <= 0) return src;
        var inv = new byte[src.Length];
        for (int i = 0; i < inv.Length; i++) inv[i] = (byte)(src[i] != 0 ? 0 : 1);
        var d = DilateMask(inv, w, h, r);
        for (int i = 0; i < d.Length; i++) d[i] = (byte)(d[i] != 0 ? 0 : 1);
        return d;
    }

    // ---- RGBA(미리 곱한) ↔ 평면 ----------------------------------------------------------
    public static float[][] Planes(uint[] buf)
    {
        int n = buf.Length;
        var P = new[] { new float[n], new float[n], new float[n], new float[n] };   // a r g b
        for (int i = 0; i < n; i++)
        {
            uint c = buf[i];
            if (c == 0) continue;
            P[0][i] = c >> 24; P[1][i] = (c >> 16) & 255; P[2][i] = (c >> 8) & 255; P[3][i] = c & 255;
        }
        return P;
    }
    public static uint[] FromPlanes(float[][] P)
    {
        int n = P[0].Length;
        var o = new uint[n];
        for (int i = 0; i < n; i++)
        {
            uint a = B(P[0][i]);
            if (a == 0) continue;
            o[i] = Px.Pack(a, Math.Min(a, B(P[1][i])), Math.Min(a, B(P[2][i])), Math.Min(a, B(P[3][i])));
        }
        return o;
    }
    public static uint B(float v) => v <= 0 ? 0u : v >= 255 ? 255u : (uint)(v + 0.5f);

    /// <summary>미리 곱한 RGBA 를 가우스 흐림 (CSS blur(px) = 표준편차 px)</summary>
    public static uint[] BlurRgba(uint[] buf, int w, int h, double sigma)
    {
        if (sigma <= 0) return (uint[])buf.Clone();
        var P = Planes(buf);
        foreach (var p in P) Blur(p, w, h, sigma, sigma);
        return FromPlanes(P);
    }

    /// <summary>buf 를 (dx,dy) 만큼 옮긴 새 배열 (바깥은 투명)</summary>
    public static uint[] Shift(uint[] buf, int w, int h, int dx, int dy)
    {
        var o = new uint[buf.Length];
        for (int y = 0; y < h; y++)
        {
            int sy = y - dy; if (sy < 0 || sy >= h) continue;
            for (int x = 0; x < w; x++) { int sx = x - dx; if (sx >= 0 && sx < w) o[y * w + x] = buf[sy * w + sx]; }
        }
        return o;
    }

    /// <summary>미리 곱한 픽셀 → (r,g,b,a) 0~255 미리 곱하지 않은 값 (캔버스 getImageData 와 같은 반올림)</summary>
    public static void Unpack(uint c, out int r, out int g, out int b, out int a)
    {
        a = (int)(c >> 24);
        if (a == 0) { r = g = b = 0; return; }
        if (a == 255) { r = (int)((c >> 16) & 255); g = (int)((c >> 8) & 255); b = (int)(c & 255); return; }
        r = (int)Math.Min(255, (((c >> 16) & 255) * 255 + (uint)a / 2) / (uint)a);
        g = (int)Math.Min(255, (((c >> 8) & 255) * 255 + (uint)a / 2) / (uint)a);
        b = (int)Math.Min(255, ((c & 255) * 255 + (uint)a / 2) / (uint)a);
    }
    public static uint Pack(int r, int g, int b, int a)
    {
        a = Math.Clamp(a, 0, 255);
        if (a == 0) return 0;
        return Px.Premul(Px.Pack((uint)a, (uint)Math.Clamp(r, 0, 255), (uint)Math.Clamp(g, 0, 255), (uint)Math.Clamp(b, 0, 255)));
    }

    /// <summary>알파가 있는 범위 (배열 기준)</summary>
    public static Rect AlphaBounds(uint[] buf, int w, int h)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if ((buf[y * w + x] >> 24) != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; y1 = y; }
        return x1 < 0 ? Rect.Empty : Rect.FromLTRB(x0, y0, x1 + 1, y1 + 1);
    }
}
