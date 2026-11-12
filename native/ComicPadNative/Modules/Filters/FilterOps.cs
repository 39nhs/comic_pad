// 레이어 픽셀을 바꾸는 필터 (실행취소 됨). 웹 ops.applyPixelFilter / applyCanvasOp 과 같은 규칙:
//  - 대상: 지금 레이어 (마스크 편집 중이면 마스크)
//  - 선택 영역이 있으면 그 안만 (경계는 선택 값만큼 섞음)
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

public static class FilterOps
{
    static Document D => App.Doc;

    /// <summary>필터를 걸 수 있는 대상 면 (안 되면 알림 후 null)</summary>
    public static Surface Target(out Layer l)
    {
        l = App.Active;
        if (!App.CheckEditable(l)) return null;
        return l.Target;
    }

    /// <summary>픽셀 하나씩 (미리 곱하지 않은 RGBA 바이트, 웹 ImageData 와 같음). 선택 영역 경계는 값 섞기</summary>
    public static void PixelFilter(string label, Action<byte[]> fn)
    {
        var s = Target(out var l); if (s == null) return;
        var d = D;
        var r = (d.Sel.Active ? d.Sel.Bounds() : s.Full).Clip(s.W, s.H);
        if (r.IsEmpty) return;
        var buf = s.Read(r);
        var img = ToBytes(buf);
        var orig = d.Sel.Active ? (byte[])img.Clone() : null;
        fn(img);
        if (orig != null)
        {
            for (int y = 0; y < r.H; y++)
                for (int x = 0; x < r.W; x++)
                {
                    int k = d.Sel.At(r.X + x, r.Y + y);
                    if (k >= 255) continue;
                    int p = (y * r.W + x) * 4;
                    for (int c = 0; c < 4; c++) img[p + c] = Clamped(orig[p + c] + (img[p + c] - orig[p + c]) * (k / 255.0));
                }
        }
        Commit(l, s, r, FromBytes(img), label);
    }

    /// <summary>레이어 전체를 받아 새 그림을 돌려주는 필터. 선택 영역이 있으면 그 안만 바뀜 (웹 applyCanvasOp)</summary>
    public static void CanvasOp(string label, Func<uint[], int, int, uint[]> op)
    {
        var s = Target(out var l); if (s == null) return;
        Ui.Busy(() =>
        {
            var full = s.Full;
            var src = s.Read(full);
            var res = op(src, s.W, s.H);
            var outp = CombineSel(src, res, full);
            Commit(l, s, full, outp, label);
        });
    }

    /// <summary>선택 영역 반영: t = 결과 × 선택, 원본에서 선택만큼 빼고 그 위에 t (웹과 같은 순서)</summary>
    public static uint[] CombineSel(uint[] orig, uint[] res, Rect r)
    {
        var sel = D.Sel;
        if (!sel.Active) return res;
        var o = new uint[res.Length];
        for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
            {
                int i = y * r.W + x;
                uint k = sel.At(r.X + x, r.Y + y);
                uint t = Px.Scale(res[i], k), dd = Px.Scale(orig[i], 255 - k);
                o[i] = Px.Over(t, dd);
            }
        return o;
    }

    /// <summary>바뀐 픽셀을 실행취소 기록과 함께 쓰기</summary>
    public static void Commit(Layer l, Surface s, Rect r, uint[] px, string label)
    {
        var pe = new PixelEdit(App.History, l, s, label);
        pe.Touch(r);
        s.Write(r, px);
        // Write 는 빈 타일을 만들지 않으므로, 전부 지워진 타일은 직접 비움
        foreach (int ti in s.TilesIn(r))
        {
            var t = s.TileAt(ti);
            if (t == null) continue;
            var tr = s.TileRect(ti);
            if (r.Intersect(tr) == tr) { bool any = false; foreach (var v in t) if (v != 0) { any = true; break; } if (!any) s.SetTile(ti % s.TX, ti / s.TX, null); }
        }
        pe.Commit(label);
        D.Modified = true;
        App.Invalidate(r);
        App.NotifyLayers();
    }

    // ---- 바이트 RGBA ↔ 미리 곱한 픽셀 ----
    public static byte[] ToBytes(uint[] buf)
    {
        var o = new byte[buf.Length * 4];
        for (int i = 0; i < buf.Length; i++)
        {
            Raster.Unpack(buf[i], out int r, out int g, out int b, out int a);
            o[i * 4] = (byte)r; o[i * 4 + 1] = (byte)g; o[i * 4 + 2] = (byte)b; o[i * 4 + 3] = (byte)a;
        }
        return o;
    }
    public static uint[] FromBytes(byte[] d)
    {
        var o = new uint[d.Length / 4];
        for (int i = 0; i < o.Length; i++) o[i] = Raster.Pack(d[i * 4], d[i * 4 + 1], d[i * 4 + 2], d[i * 4 + 3]);
        return o;
    }
    /// <summary>Uint8ClampedArray 대입과 같이 (반올림, 짝수 쪽)</summary>
    public static byte Clamped(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Round(v, MidpointRounding.ToEven);

    // =================================================================================
    // 웹 필터 메뉴의 계산들
    // =================================================================================

    /// <summary>선화 추출 (흰 배경 → 투명)</summary>
    public static void LineExtract(byte[] d)
    {
        for (int i = 0; i < d.Length; i += 4)
        {
            double lum = 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2];
            double a = (255 - lum) * d[i + 3] / 255;
            d[i] = d[i + 1] = d[i + 2] = 0;
            d[i + 3] = Clamped(Math.Min(255, a * 1.15));
        }
    }
    public static void Gray(byte[] d)
    {
        for (int i = 0; i < d.Length; i += 4) { byte v = Clamped(0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2]); d[i] = d[i + 1] = d[i + 2] = v; }
    }
    public static void Invert(byte[] d)
    {
        for (int i = 0; i < d.Length; i += 4) { d[i] = (byte)(255 - d[i]); d[i + 1] = (byte)(255 - d[i + 1]); d[i + 2] = (byte)(255 - d[i + 2]); }
    }
    public static void Mono(byte[] d, double t)
    {
        for (int i = 0; i < d.Length; i += 4)
        {
            if (d[i + 3] < 8) continue;
            double lum = 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2];
            bool on = lum < t && d[i + 3] > 96;
            d[i] = d[i + 1] = d[i + 2] = 0; d[i + 3] = (byte)(on ? 255 : 0);
        }
    }

    /// <summary>언샤프 마스크: 원본×(1+양) − 흐림×양 (미리 곱한 값, feComposite arithmetic)</summary>
    public static uint[] Unsharp(uint[] src, int w, int h, double radius, double amount)
    {
        var bl = Raster.BlurRgba(src, w, h, radius);
        var o = new uint[src.Length];
        double k2 = 1 + amount, k3 = -amount;
        for (int i = 0; i < src.Length; i++)
        {
            uint s = src[i], b = bl[i];
            if ((s | b) == 0) continue;
            uint A(int sh) => (uint)Math.Clamp(Math.Round(k2 * ((s >> sh) & 255) + k3 * ((b >> sh) & 255)), 0, 255);
            uint a = A(24);
            o[i] = a == 0 ? 0 : Px.Pack(a, Math.Min(a, A(16)), Math.Min(a, A(8)), Math.Min(a, A(0)));
        }
        return o;
    }

    /// <summary>노이즈: feTurbulence(fractalNoise 0.9, 2옥타브) × 양 + 원본 − 양/2, 원본 모양 안만</summary>
    public static uint[] Noise(uint[] src, int w, int h, double amount, bool mono, int seed)
    {
        double a = amount / 100;
        var turb = new Turbulence(seed);
        var o = new uint[src.Length];
        var sat0 = Svg.Saturate(0).M;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                uint s = src[i];
                if ((s >> 24) == 0) continue;
                // 노이즈 (미리 곱하지 않은 0~1 값)
                double nr = turb.Fractal(0, x, y, 0.9, 2), ng = turb.Fractal(1, x, y, 0.9, 2), nb = turb.Fractal(2, x, y, 0.9, 2), na = turb.Fractal(3, x, y, 0.9, 2);
                if (mono)
                {
                    double r2 = sat0[0] * nr + sat0[1] * ng + sat0[2] * nb, g2 = sat0[5] * nr + sat0[6] * ng + sat0[7] * nb, b2 = sat0[10] * nr + sat0[11] * ng + sat0[12] * nb;
                    nr = Math.Clamp(r2, 0, 1); ng = Math.Clamp(g2, 0, 1); nb = Math.Clamp(b2, 0, 1);
                }
                // 미리 곱해서 arithmetic
                double sa = (s >> 24) / 255.0;
                double ma = Math.Clamp(a * na + sa - a / 2, 0, 1);
                double M(double n, uint sc) => Math.Clamp(a * n * na + sc / 255.0 - a / 2, 0, ma);
                double mr = M(nr, (s >> 16) & 255), mg = M(ng, (s >> 8) & 255), mb = M(nb, s & 255);
                // in SourceGraphic: × 원본 알파
                uint P(double v) => (uint)Math.Clamp(Math.Round(v * sa * 255), 0, 255);
                uint oa = P(ma);
                o[i] = oa == 0 ? 0 : Px.Pack(oa, Math.Min(oa, P(mr)), Math.Min(oa, P(mg)), Math.Min(oa, P(mb)));
            }
        return o;
    }

    /// <summary>모자이크: 작게 줄였다가(평균) 칸 크기로 늘림</summary>
    public static uint[] Mosaic(uint[] src, int w, int h, int s)
    {
        int sw = (w + s - 1) / s, sh = (h + s - 1) / s;
        double fx = (double)w / sw, fy = (double)h / sh;
        var small = new uint[sw * sh];
        for (int j = 0; j < sh; j++)
        {
            int y0 = (int)Math.Floor(j * fy), y1 = Math.Max(y0 + 1, (int)Math.Floor((j + 1) * fy));
            for (int i = 0; i < sw; i++)
            {
                int x0 = (int)Math.Floor(i * fx), x1 = Math.Max(x0 + 1, (int)Math.Floor((i + 1) * fx));
                double A = 0, R = 0, G = 0, B = 0; int n = 0;
                for (int y = y0; y < Math.Min(h, y1); y++) for (int x = x0; x < Math.Min(w, x1); x++) { uint c = src[y * w + x]; A += c >> 24; R += (c >> 16) & 255; G += (c >> 8) & 255; B += c & 255; n++; }
                if (n == 0) continue;
                uint a = (uint)Math.Round(A / n);
                small[j * sw + i] = a == 0 ? 0 : Px.Pack(a, Math.Min(a, (uint)Math.Round(R / n)), Math.Min(a, (uint)Math.Round(G / n)), Math.Min(a, (uint)Math.Round(B / n)));
            }
        }
        var o = new uint[src.Length];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) o[y * w + x] = small[Math.Min(sh - 1, y / s) * sw + Math.Min(sw - 1, x / s)];
        return o;
    }

    /// <summary>쌍선형으로 (fx,fy) 위치 색 (미리 곱한 float 4개). 바깥은 투명</summary>
    static void Sample(uint[] src, int w, int h, double fx, double fy, float[] acc)
    {
        fx -= 0.5; fy -= 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        acc[0] = acc[1] = acc[2] = acc[3] = 0;
        for (int k = 0; k < 4; k++)
        {
            int xx = x0 + (k & 1), yy = y0 + (k >> 1);
            if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
            float wgt = ((k & 1) != 0 ? tx : 1 - tx) * ((k >> 1) != 0 ? ty : 1 - ty);
            if (wgt == 0) continue;
            uint c = src[yy * w + xx];
            if (c == 0) continue;
            acc[0] += (c >> 24) * wgt; acc[1] += ((c >> 16) & 255) * wgt; acc[2] += ((c >> 8) & 255) * wgt; acc[3] += (c & 255) * wgt;
        }
    }

    /// <summary>여러 번 옮겨 그리기 (불투명도 1/(i+1), 표준 덮기) — 방향 흐림·방사형 흐림 공통. map: 대상 → 원본 좌표</summary>
    static uint[] Accumulate(uint[] src, int w, int h, int n, Func<int, double, double, (double, double)> map, Rect area)
    {
        var acc = new float[src.Length * 4];
        var tmp = new float[4];
        // 그림이 있는 범위만 (빈 곳은 계산 안 함)
        for (int i = 0; i < n; i++)
        {
            float op = 1f / (i + 1);
            for (int y = area.Y; y < area.B; y++)
                for (int x = area.X; x < area.R; x++)
                {
                    var (sx, sy) = map(i, x + 0.5, y + 0.5);
                    Sample(src, w, h, sx, sy, tmp);
                    float sa = tmp[0] * op;
                    if (sa <= 0) continue;
                    int p = (y * w + x) * 4;
                    float ia = 1 - sa / 255;
                    acc[p] = sa + acc[p] * ia;
                    acc[p + 1] = tmp[1] * op + acc[p + 1] * ia;
                    acc[p + 2] = tmp[2] * op + acc[p + 2] * ia;
                    acc[p + 3] = tmp[3] * op + acc[p + 3] * ia;
                }
        }
        var o = new uint[src.Length];
        for (int i = 0; i < o.Length; i++)
        {
            uint a = Raster.B(acc[i * 4]);
            if (a == 0) continue;
            o[i] = Px.Pack(a, Math.Min(a, Raster.B(acc[i * 4 + 1])), Math.Min(a, Raster.B(acc[i * 4 + 2])), Math.Min(a, Raster.B(acc[i * 4 + 3])));
        }
        return o;
    }

    public static uint[] Motion(uint[] src, int w, int h, double angle, double dist)
    {
        int n = Math.Min(64, Math.Max(4, (int)Math.Round(dist / 2, MidpointRounding.AwayFromZero)));
        double rad = angle * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
        var area = Raster.AlphaBounds(src, w, h).Inflate((int)Math.Ceiling(Math.Abs(dist) / 2) + 2).Clip(w, h);
        return Accumulate(src, w, h, n, (i, x, y) => { double k = (i / (double)(n - 1) - 0.5) * dist; return (x - c * k, y - s * k); }, area);
    }

    public static uint[] ZoomBlur(uint[] src, int w, int h, double amt, double cx, double cy)
    {
        int n = 24;
        var b = Raster.AlphaBounds(src, w, h);
        if (b.IsEmpty) return (uint[])src.Clone();
        // 가장 멀리 밀려나는 정도만큼 넓힘
        double far = 0;
        foreach (var (px, py) in new[] { (b.X, b.Y), (b.R, b.Y), (b.X, b.B), (b.R, b.B) }) far = Math.Max(far, Math.Max(Math.Abs(px - cx), Math.Abs(py - cy)));
        var area = b.Inflate((int)Math.Ceiling(far * Math.Max(0, amt) / 100) + 2).Clip(w, h);
        return Accumulate(src, w, h, n, (i, x, y) =>
        {
            double s = 1 + i / (double)(n - 1) * amt / 100;
            return ((x - cx * (1 - s)) / s, (y - cy * (1 - s)) / s);
        }, area);
    }

    /// <summary>빛 번짐: 원본 위에 (흐림 + 밝기 1.2) 를 스크린으로</summary>
    public static uint[] GlowBlur(uint[] src, int w, int h, double r, double amount)
    {
        var bl = Raster.BlurRgba(src, w, h, r);
        var bright = Svg.CssColor(120, 100, 100, 0);
        bright.Apply(bl);
        var o = (uint[])src.Clone();
        float op = (float)Math.Clamp(amount / 100, 0, 1);
        for (int i = 0; i < o.Length; i++) if (bl[i] != 0) o[i] = Px.Composite(bl[i], o[i], Blend.Screen, op);
        return o;
    }

    /// <summary>외곽선 두르기: 그림 모양을 색으로 칠해 둘레 여러 방향으로 찍은 고리를 그림 아래에</summary>
    public static uint[] Outline(uint[] src, int w, int h, double width, string color, Selection sel)
    {
        double r = Math.Max(1, width);
        var b = Raster.AlphaBounds(src, w, h);
        if (b.IsEmpty) return (uint[])src.Clone();
        var area = b.Inflate((int)Math.Ceiling(r) + 2).Clip(w, h);
        var A = new float[w * h];
        for (int i = 0; i < A.Length; i++) A[i] = (src[i] >> 24) / 255f;
        var offs = new List<(double, double)>();
        int n = Math.Max(16, (int)Math.Ceiling(r * 2.5));
        for (int i = 0; i < n; i++) { double a = i / (double)n * Math.PI * 2; offs.Add((Math.Cos(a) * r, Math.Sin(a) * r)); }
        for (double rr = r / 2; rr > 0.5; rr /= 2) for (int i = 0; i < 12; i++) { double a = i / 12.0 * Math.PI * 2; offs.Add((Math.Cos(a) * rr, Math.Sin(a) * rr)); }
        offs.Add((0, 0));
        var ring = new float[w * h];
        foreach (var (ox, oy) in offs)
            for (int y = area.Y; y < area.B; y++)
                for (int x = area.X; x < area.R; x++)
                {
                    float v = SampleA(A, w, h, x + 0.5 - ox, y + 0.5 - oy);
                    if (v <= 0) continue;
                    int i = y * w + x;
                    ring[i] = v + ring[i] * (1 - v);
                }
        uint col = Px.FromHex(color, 0xFFFFFFFF);
        var o = (uint[])src.Clone();
        for (int y = area.Y; y < area.B; y++)
            for (int x = area.X; x < area.R; x++)
            {
                int i = y * w + x;
                float v = ring[i];
                if (sel.Active) v *= sel.At(x, y) / 255f;
                if (v <= 0) continue;
                o[i] = Px.Over(o[i], Px.Scale(col, Raster.B(v * 255)));   // destination-over: 원래 그림이 위
            }
        return o;
    }
    static float SampleA(float[] A, int w, int h, double fx, double fy)
    {
        fx -= 0.5; fy -= 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = (float)(fx - x0), ty = (float)(fy - y0), s = 0;
        for (int k = 0; k < 4; k++)
        {
            int xx = x0 + (k & 1), yy = y0 + (k >> 1);
            if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) continue;
            s += A[yy * w + xx] * ((k & 1) != 0 ? tx : 1 - tx) * ((k >> 1) != 0 ? ty : 1 - ty);
        }
        return s;
    }

    /// <summary>그림자 넣기: (dx,dy) 옮겨 흐린 모양을 색으로, 진하기만큼 그림 아래에</summary>
    public static uint[] DropShadow(uint[] src, int w, int h, double dx, double dy, double blur, string color, double alpha)
    {
        var A = new float[w * h];
        int ix = (int)Math.Round(dx), iy = (int)Math.Round(dy);
        for (int y = 0; y < h; y++) { int sy = y - iy; if (sy < 0 || sy >= h) continue; for (int x = 0; x < w; x++) { int sx = x - ix; if (sx >= 0 && sx < w) A[y * w + x] = (src[sy * w + sx] >> 24) / 255f; } }
        if (blur > 0) Raster.Blur(A, w, h, blur, blur);
        uint col = Px.FromHex(color, 0xFF000000);
        var o = (uint[])src.Clone();
        for (int i = 0; i < o.Length; i++)
        {
            float v = (float)(A[i] * alpha);
            if (v <= 0) continue;
            o[i] = Px.Over(o[i], Px.Scale(col, Raster.B(v * 255)));
        }
        return o;
    }

    /// <summary>단색으로 바꾸기: 모양은 그대로 색만 전경색 (선택 영역은 그 부분만 source-atop)</summary>
    public static uint[] Colorize(uint[] src, int w, int h, uint fg, Selection sel)
    {
        var o = new uint[src.Length];
        fg |= 0xFF000000;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                uint c = src[i], a = c >> 24;
                if (a == 0) continue;
                if (!sel.Active) { o[i] = Px.Scale(fg, a); continue; }
                uint t = Px.Scale(Px.Scale(fg, a), sel.At(x, y));
                o[i] = Px.Composite(t, c, Blend.Atop);
            }
        return o;
    }

    // ---- 선 정리 (웹 core/lineclean.js) ---------------------------------------------------
    public sealed class CleanOpt { public int Specks, Close, Smooth, Weight; public bool Darken, FromScan; public string Color = "#000000"; }

    /// <summary>RGBA 바이트(미리 곱하지 않음)를 제자리에서 정리. 지운 픽셀 수</summary>
    public static int CleanLines(byte[] d, int w, int hgt, CleanOpt o)
    {
        int n = w * hgt;
        var alpha = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int a = d[i * 4 + 3];
            double lum = d[i * 4] * 0.3 + d[i * 4 + 1] * 0.59 + d[i * 4 + 2] * 0.11;
            alpha[i] = o.FromScan ? Clamped(255 - lum) : (byte)a;
        }
        int removed = 0;
        if (o.Specks > 0) removed = RemoveSpecks(alpha, w, hgt, o.Specks, 96);
        if (o.Close > 0)
        {
            var m = Ink(alpha, 96);
            var closed = Raster.ErodeMask(Raster.DilateMask(m, w, hgt, o.Close), w, hgt, o.Close);
            for (int i = 0; i < n; i++) if (closed[i] != 0 && m[i] == 0) alpha[i] = 255;
        }
        if (o.Weight != 0)
        {
            var m = Ink(alpha, 96);
            int r = Math.Abs(o.Weight);
            var res = o.Weight > 0 ? Raster.DilateMask(m, w, hgt, r) : Raster.ErodeMask(m, w, hgt, r);
            for (int i = 0; i < n; i++) alpha[i] = res[i] != 0 ? Math.Max(alpha[i], o.Weight > 0 ? (byte)255 : alpha[i]) : (o.Weight < 0 ? (byte)0 : alpha[i]);
        }
        if (o.Smooth > 0) SmoothEdges(alpha, w, hgt, o.Smooth);
        int[] col = null;
        if (o.Darken) { uint c = Px.Unpremul(Px.FromHex(o.Color)); col = new[] { (int)((c >> 16) & 255), (int)((c >> 8) & 255), (int)(c & 255) }; }
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            if (o.FromScan || col != null)
            {
                if (o.FromScan && col == null) { d[p] = d[p + 1] = d[p + 2] = 0; }
                else { d[p] = (byte)col[0]; d[p + 1] = (byte)col[1]; d[p + 2] = (byte)col[2]; }
            }
            d[p + 3] = alpha[i];
        }
        return removed;
    }

    static byte[] Ink(byte[] a, int thr) { var m = new byte[a.Length]; for (int i = 0; i < a.Length; i++) m[i] = (byte)(a[i] >= thr ? 1 : 0); return m; }

    static int RemoveSpecks(byte[] alpha, int w, int hgt, int area, int thr)
    {
        var ink = Ink(alpha, thr);
        var seen = new byte[ink.Length];
        var stack = new int[ink.Length];
        var comp = new List<int>();
        int removed = 0;
        for (int s = 0; s < ink.Length; s++)
        {
            if (ink[s] == 0 || seen[s] != 0) continue;
            int sp = 0; comp.Clear();
            stack[sp++] = s; seen[s] = 1;
            while (sp > 0)
            {
                int i = stack[--sp]; comp.Add(i);
                int x = i % w;
                Span<int> nb = stackalloc int[8] { i - w, i + w, x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, x > 0 ? i - w - 1 : -1, x < w - 1 ? i - w + 1 : -1, x > 0 ? i + w - 1 : -1, x < w - 1 ? i + w + 1 : -1 };
                foreach (int j in nb)
                {
                    if (j < 0 || j >= ink.Length || seen[j] != 0 || ink[j] == 0) continue;
                    seen[j] = 1; stack[sp++] = j;
                }
            }
            if (comp.Count < area)
            {
                foreach (int i in comp)
                {
                    alpha[i] = 0; removed++;
                    int x = i % w;
                    foreach (int j in new[] { i - 1, i + 1, i - w, i + w })
                        if (j >= 0 && j < alpha.Length && Math.Abs(j % w - x) <= 1 && alpha[j] < thr) alpha[j] = 0;
                }
            }
        }
        return removed;
    }

    static void SmoothEdges(byte[] alpha, int w, int hgt, int r)
    {
        var tmp = new float[alpha.Length]; var outp = new float[alpha.Length];
        for (int y = 0; y < hgt; y++)
        {
            float acc = 0; int o = y * w;
            for (int x = -r; x < w + r; x++)
            {
                float add = x + r < w ? alpha[o + Math.Min(w - 1, x + r)] : 0, sub = x - r - 1 >= 0 ? alpha[o + x - r - 1] : 0;
                acc += add - sub;
                if (x >= 0 && x < w) tmp[o + x] = acc / (2 * r + 1);
            }
        }
        for (int x = 0; x < w; x++)
        {
            float acc = 0;
            for (int y = -r; y < hgt + r; y++)
            {
                float add = y + r < hgt ? tmp[Math.Min(hgt - 1, y + r) * w + x] : 0, sub = y - r - 1 >= 0 ? tmp[(y - r - 1) * w + x] : 0;
                acc += add - sub;
                if (y >= 0 && y < hgt) outp[y * w + x] = acc / (2 * r + 1);
            }
        }
        for (int i = 0; i < alpha.Length; i++) { double v = (outp[i] - 128) * 2.6 + 128; alpha[i] = Clamped(v); }
    }
}

/// <summary>SVG feTurbulence 규격의 참고 구현 (펄린 노이즈)</summary>
public sealed class Turbulence
{
    const int BSize = 0x100, BM = 0xff, PerlinN = 0x1000;
    readonly int[] lattice = new int[BSize + BSize + 2];
    readonly double[,,] gradient = new double[4, BSize + BSize + 2, 2];

    public Turbulence(int seed)
    {
        long s = seed;
        if (s <= 0) s = -(s % (2147483647 - 1)) + 1;
        if (s > 2147483647 - 1) s = 2147483647 - 1;
        long Random() { s = 16807 * (s % 127773) - 2836 * (s / 127773); if (s <= 0) s += 2147483647; return s; }
        for (int k = 0; k < 4; k++)
            for (int i = 0; i < BSize; i++)
            {
                lattice[i] = i;
                for (int j = 0; j < 2; j++) gradient[k, i, j] = (double)((Random() % (BSize + BSize)) - BSize) / BSize;
                double len = Math.Sqrt(gradient[k, i, 0] * gradient[k, i, 0] + gradient[k, i, 1] * gradient[k, i, 1]);
                if (len > 0) { gradient[k, i, 0] /= len; gradient[k, i, 1] /= len; }
            }
        for (int i = BSize - 1; i > 0; i--)
        {
            int k = lattice[i];
            int j = (int)(Random() % BSize);
            lattice[i] = lattice[j]; lattice[j] = k;
        }
        for (int i = 0; i < BSize + 2; i++)
        {
            lattice[BSize + i] = lattice[i];
            for (int k = 0; k < 4; k++) for (int j = 0; j < 2; j++) gradient[k, BSize + i, j] = gradient[k, i, j];
        }
    }

    static double SCurve(double t) => t * t * (3 - 2 * t);
    static double Lerp(double t, double a, double b) => a + t * (b - a);

    double Noise2(int ch, double vx, double vy)
    {
        double t = vx + PerlinN; int bx0 = ((int)t) & BM, bx1 = (bx0 + 1) & BM; double rx0 = t - (int)t, rx1 = rx0 - 1;
        t = vy + PerlinN; int by0 = ((int)t) & BM, by1 = (by0 + 1) & BM; double ry0 = t - (int)t, ry1 = ry0 - 1;
        int i = lattice[bx0], j = lattice[bx1];
        int b00 = lattice[i + by0], b10 = lattice[j + by0], b01 = lattice[i + by1], b11 = lattice[j + by1];
        double sx = SCurve(rx0), sy = SCurve(ry0);
        double u = rx0 * gradient[ch, b00, 0] + ry0 * gradient[ch, b00, 1];
        double v = rx1 * gradient[ch, b10, 0] + ry0 * gradient[ch, b10, 1];
        double a = Lerp(sx, u, v);
        u = rx0 * gradient[ch, b01, 0] + ry1 * gradient[ch, b01, 1];
        v = rx1 * gradient[ch, b11, 0] + ry1 * gradient[ch, b11, 1];
        double b = Lerp(sx, u, v);
        return Lerp(sy, a, b);
    }

    /// <summary>fractalNoise 값 (0~1)</summary>
    public double Fractal(int ch, double x, double y, double freq, int oct)
    {
        double sum = 0, ratio = 1, vx = x * freq, vy = y * freq;
        for (int n = 0; n < oct; n++) { sum += Noise2(ch, vx, vy) / ratio; vx *= 2; vy *= 2; ratio *= 2; }
        return Math.Clamp((sum + 1) / 2, 0, 1);
    }
}
