// 2D 아핀 변환 (웹 DOMMatrix 와 같은 a~f) + 옮겨 그리기 (쌍선형 / 가까운 픽셀)
// x' = A·x + C·y + E,  y' = B·x + D·y + F
using System.Globalization;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public readonly struct Affine
{
    public readonly double A, B, C, D, E, F;
    public Affine(double a, double b, double c, double d, double e, double f) { A = a; B = b; C = c; D = d; E = e; F = f; }
    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    public (double x, double y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>this · o (o 를 먼저 적용)</summary>
    public Affine Mul(Affine o) => new(A * o.A + C * o.B, B * o.A + D * o.B, A * o.C + C * o.D, B * o.C + D * o.D, A * o.E + C * o.F + E, B * o.E + D * o.F + F);

    public Affine Inverse()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return new(0, 0, 0, 0, 0, 0);
        double ia = D / det, ib = -B / det, ic = -C / det, id = A / det;
        return new(ia, ib, ic, id, -(ia * E + ic * F), -(ib * E + id * F));
    }

    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Affine Scale(double s) => new(s, 0, 0, s, 0, 0);
    public static Affine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
    public static Affine Rotate(double deg) { double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r); return new(c, s, -s, c, 0, 0); }

    public bool IsIdentity => Math.Abs(A - 1) < 1e-9 && Math.Abs(D - 1) < 1e-9 && Math.Abs(B) < 1e-9 && Math.Abs(C) < 1e-9 && Math.Abs(E) < 1e-9 && Math.Abs(F) < 1e-9;

    /// <summary>DOMMatrix.toString 처럼 (굳히기 캐시 키)</summary>
    public override string ToString() => string.Join(",", new[] { A, B, C, D, E, F }.Select(v => v.ToString("0.######", CultureInfo.InvariantCulture)));

    /// <summary>사각형을 옮긴 범위</summary>
    public Rect Bounds(Rect r, int pad = 1)
    {
        if (r.IsEmpty) return r;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (px, py) in new (double, double)[] { (r.X, r.Y), (r.R, r.Y), (r.X, r.B), (r.R, r.B) })
        {
            var (x, y) = Apply(px, py);
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        return Rect.FromPoints(x0, y0, x1, y1, pad);
    }
}

public static class Xform
{
    /// <summary>쌍선형 표본 (미리 곱한 알파 그대로). (x,y) 는 픽셀 가운데가 +0.5 인 좌표</summary>
    public static uint Bilinear(uint[] s, int w, int h, double x, double y)
    {
        x -= 0.5; y -= 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        uint P(int xx, int yy) => (uint)xx < (uint)w && (uint)yy < (uint)h ? s[yy * w + xx] : 0;
        uint p00 = P(x0, y0), p10 = P(x0 + 1, y0), p01 = P(x0, y0 + 1), p11 = P(x0 + 1, y0 + 1);
        if ((p00 | p10 | p01 | p11) == 0) return 0;
        double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        uint Ch(int sh) => (uint)Math.Clamp((int)(((p00 >> sh) & 255) * w00 + ((p10 >> sh) & 255) * w10 + ((p01 >> sh) & 255) * w01 + ((p11 >> sh) & 255) * w11 + 0.5), 0, 255);
        uint a = Ch(24), r = Math.Min(a, Ch(16)), g = Math.Min(a, Ch(8)), b = Math.Min(a, Ch(0));
        return Px.Pack(a, r, g, b);
    }

    public static uint Nearest(uint[] s, int w, int h, double x, double y)
    {
        int xx = (int)Math.Floor(x), yy = (int)Math.Floor(y);
        return (uint)xx < (uint)w && (uint)yy < (uint)h ? s[yy * w + xx] : 0;
    }

    /// <summary>
    /// 변환 T (원본 문서 좌표 → 대상 문서 좌표)로 옮겨서 dst(영역 dr)에 alpha 로 덮기.
    /// render(원본 영역) 는 그 영역의 원본 픽셀 배열을 돌려준다. bounds 는 원본이 있을 수 있는 범위 (문서 전체 등)
    /// </summary>
    public static void DrawTransformed(uint[] dst, Rect dr, Func<Rect, uint[]> render, Rect bounds, Affine T, float alpha, bool nearest)
    {
        var inv = T.Inverse();
        var sr = inv.Bounds(dr, 2).Intersect(bounds);
        if (sr.IsEmpty || alpha <= 0) return;
        var src = render(sr);
        var tmp = new uint[dr.W * dr.H];
        bool any = false;
        for (int y = 0; y < dr.H; y++)
        {
            double cy = dr.Y + y + 0.5;
            for (int x = 0; x < dr.W; x++)
            {
                double cx = dr.X + x + 0.5;
                double sx = inv.A * cx + inv.C * cy + inv.E - sr.X, sy = inv.B * cx + inv.D * cy + inv.F - sr.Y;
                uint c = nearest ? Nearest(src, sr.W, sr.H, sx, sy) : Bilinear(src, sr.W, sr.H, sx, sy);
                if (c != 0) { tmp[y * dr.W + x] = c; any = true; }
            }
        }
        if (any) Px.CompositeRow(dst, 0, tmp, 0, tmp.Length, Blend.Normal, Math.Clamp(alpha, 0, 1));
    }

    /// <summary>면 src 를 T 로 옮겨 dst 면에 덮기 (모션 굳히기)</summary>
    public static void DrawSurface(Surface dst, Surface src, Affine T, float alpha, bool nearest)
    {
        var b = src.Bounds();
        if (b.IsEmpty) return;
        var target = T.Bounds(b, 2).Clip(dst.W, dst.H);
        if (target.IsEmpty) return;
        for (int y = target.Y; y < target.B; y += Surface.TS)
            for (int x = target.X; x < target.R; x += Surface.TS)
            {
                var part = Rect.FromLTRB(x, y, Math.Min(target.R, x + Surface.TS), Math.Min(target.B, y + Surface.TS));
                var buf = dst.Read(part);
                DrawTransformed(buf, part, r => src.Read(r), b, T, alpha, nearest);
                dst.Write(part, buf);
            }
    }

    /// <summary>배열 그림 크기 바꾸기: 정수 배로 키우면 가까운 픽셀(또렷하게), 줄이면 영역 평균, 그 밖은 쌍선형</summary>
    public static uint[] Resize(uint[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return src;
        var o = new uint[dw * dh];
        double kx = (double)sw / dw, ky = (double)sh / dh;
        bool intUp = dw % sw == 0 && dh % sh == 0 && dw >= sw && dh >= sh;
        if (intUp)
        {
            for (int y = 0; y < dh; y++) { int sy = y * sh / dh; for (int x = 0; x < dw; x++) o[y * dw + x] = src[sy * sw + x * sw / dw]; }
            return o;
        }
        if (kx >= 1 && ky >= 1)
        {
            // 영역 평균 (줄이기)
            for (int y = 0; y < dh; y++)
            {
                int y0 = (int)(y * ky), y1 = Math.Max(y0 + 1, Math.Min(sh, (int)((y + 1) * ky)));
                for (int x = 0; x < dw; x++)
                {
                    int x0 = (int)(x * kx), x1 = Math.Max(x0 + 1, Math.Min(sw, (int)((x + 1) * kx)));
                    long a = 0, r = 0, g = 0, b = 0; int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            uint c = src[yy * sw + xx];
                            a += c >> 24; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; n++;
                        }
                    if (n > 0) o[y * dw + x] = Px.Pack((uint)(a / n), (uint)(r / n), (uint)(g / n), (uint)(b / n));
                }
            }
            return o;
        }
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
                o[y * dw + x] = Bilinear(src, sw, sh, (x + 0.5) * kx, (y + 0.5) * ky);
        return o;
    }
}
