// 메쉬 렌더러: 텍스처 삼각형 (쌍선형 샘플 · 미리 곱한 알파 · 밉맵 · 클리핑 마스크).
// IRigRenderer 뒤에 숨겨 두어 나중에 GPU(Direct3D) 판을 끼울 수 있다 — 이 파일의 CpuRigRenderer 는 CPU 판:
//   Begin → AddMask/DrawMesh 를 모아 두었다가 End 에서 화면을 가로 띠로 나눠 여러 코어로 그린다 (띠 안에서는 그리는 순서 그대로).
// 삼각형 채우기는 "픽셀 가운데" 규칙 + 같은 변은 같은 식으로 계산 → 이웃 삼각형 사이에 틈·겹침이 없다.
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.VRig2;

/// <summary>부위 텍스처 (작품 좌표 X,Y 에서 시작하는 W×H, 미리 곱한 BGRA) + 밉맵</summary>
public sealed class RigTexture
{
    public string Key;
    public int X, Y, W, H;
    public uint[][] Mip;
    public int[] MW, MH;
    public int Levels => Mip.Length;

    public static RigTexture FromPixels(uint[] p, int x, int y, int w, int h, string key)
    {
        var t = new RigTexture { Key = key, X = x, Y = y, W = w, H = h };
        t.BuildMips(p);
        return t;
    }

    /// <summary>레이어 그림 → 텍스처 (그림이 있는 곳 + 둘레 3px 투명)</summary>
    public static RigTexture FromSurface(Surface s, string key)
    {
        var b = s.Bounds();
        if (b.IsEmpty) return FromPixels(new uint[4], 0, 0, 2, 2, key);
        var buf = s.Read(b);
        int x0 = b.W, y0 = b.H, x1 = -1, y1 = -1;
        for (int y = 0; y < b.H; y++) for (int x = 0; x < b.W; x++) if (buf[y * b.W + x] >> 24 != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 < 0) return FromPixels(new uint[4], 0, 0, 2, 2, key);
        const int pad = 3;
        int w = x1 - x0 + 1 + pad * 2, h = y1 - y0 + 1 + pad * 2;
        var p = new uint[w * h];
        for (int y = y0; y <= y1; y++) Array.Copy(buf, y * b.W + x0, p, (y - y0 + pad) * w + pad, x1 - x0 + 1);
        return FromPixels(p, b.X + x0 - pad, b.Y + y0 - pad, w, h, key);
    }

    /// <summary>폴더 → 안의 보이는 레이어를 표준 합성으로 합친 텍스처</summary>
    public static RigTexture FromFolder(Document d, Layer folder)
    {
        var sum = new Surface(d.W, d.H);
        var kids = d.Layers.Where(l => !l.IsFolder && !l.IsAdjust && l.Px != null && l.Visible && d.IsInside(l, folder)).ToList();
        foreach (var l in kids)
        {
            var b = l.Px.Bounds();
            if (b.IsEmpty) continue;
            var src = l.Px.Read(b); var dst = sum.Read(b);
            uint k = (uint)Math.Round(Math.Clamp(l.Opacity, 0, 1) * 256);
            for (int i = 0; i < src.Length; i++) { uint c = src[i]; if (c == 0) continue; if (k < 256) c = Mul(c, k); dst[i] = Over(c, dst[i]); }
            sum.Write(b, dst);
        }
        return FromSurface(sum, folder.Id);
    }

    void BuildMips(uint[] p)
    {
        var mips = new List<uint[]> { p }; var mw = new List<int> { W }; var mh = new List<int> { H };
        int w = W, h = H;
        while ((w > 1 || h > 1) && mips.Count < 10)
        {
            int nw = Math.Max(1, (w + 1) / 2), nh = Math.Max(1, (h + 1) / 2);
            var src = mips[^1]; var o = new uint[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int sx = x * 2, sy = y * 2, sx1 = Math.Min(w - 1, sx + 1), sy1 = Math.Min(h - 1, sy + 1);
                    uint a = src[sy * w + sx], b = src[sy * w + sx1], c = src[sy1 * w + sx], d = src[sy1 * w + sx1];
                    uint ch(int s) => ((a >> s & 255) + (b >> s & 255) + (c >> s & 255) + (d >> s & 255) + 2) >> 2;
                    o[y * nw + x] = ch(24) << 24 | ch(16) << 16 | ch(8) << 8 | ch(0);
                }
            mips.Add(o); mw.Add(nw); mh.Add(nh); w = nw; h = nh;
        }
        Mip = mips.ToArray(); MW = mw.ToArray(); MH = mh.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Mul(uint c, uint k)   // k 0~256
    {
        uint rb = ((c & 0xFF00FF) * k >> 8) & 0xFF00FF;
        uint ag = (((c >> 8) & 0xFF00FF) * k) & 0xFF00FF00;
        return rb | ag;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Over(uint s, uint d)
    {
        uint sa = s >> 24;
        if (sa == 255) return s;
        if (sa == 0) return s + d;
        uint inv = 255 - sa;
        uint rb = (d & 0xFF00FF) * inv + 0x800080; rb = ((rb + ((rb >> 8) & 0xFF00FF)) >> 8) & 0xFF00FF;
        uint ag = ((d >> 8) & 0xFF00FF) * inv + 0x800080; ag = (ag + ((ag >> 8) & 0xFF00FF)) & 0xFF00FF00;
        return s + (rb | ag);
    }
}

/// <summary>메쉬 한 개 그리기 (xy: 출력 픽셀 좌표, uv: 텍스처 밑판 픽셀 좌표, idx: 삼각형 번호)</summary>
public sealed record RigMeshDraw(float[] Xy, float[] Uv, int[] Idx, RigTexture Tex, float Opacity);

/// <summary>리그 렌더러 (CPU·GPU 공용 입구)</summary>
public interface IRigRenderer : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>새 장 (clear = 미리 곱한 ARGB 바탕, 0 = 투명)</summary>
    void Begin(int width, int height, uint clear);
    /// <summary>마스크 만들기 (그 메쉬들의 알파 합) → 번호</summary>
    int AddMask(IReadOnlyList<RigMeshDraw> meshes);
    /// <summary>메쉬 그리기 (mask: AddMask 번호, -1 = 없음)</summary>
    void DrawMesh(float[] xy, float[] uv, int[] idx, RigTexture tex, float opacity, int mask = -1, bool invertMask = false);
    /// <summary>장 끝 (모은 명령을 그림)</summary>
    void End();
    /// <summary>결과를 같은 크기의 32bpp PArgb 그림에 복사</summary>
    void CopyTo(Bitmap bmp);
}

public sealed unsafe class CpuRigRenderer : IRigRenderer
{
    sealed class Cmd { public RigMeshDraw M; public int Mask = -1; public bool Inv; public int Level; public float MinY, MaxY; }
    int w, h; uint clear;
    uint[] buf = Array.Empty<uint>();
    readonly List<Cmd> cmds = new();
    readonly List<List<Cmd>> masks = new();
    public string Name => "CPU";
    public int Width => w;
    public int Height => h;
    public uint[] Pixels => buf;
    /// <summary>여러 코어 쓰기 (끄면 한 줄로 — 시험용)</summary>
    public bool Parallel = true;

    public void Begin(int width, int height, uint clearColor)
    {
        w = Math.Max(1, width); h = Math.Max(1, height); clear = clearColor;
        if (buf.Length != w * h) buf = new uint[w * h];
        cmds.Clear(); masks.Clear();
    }

    static Cmd Prep(RigMeshDraw m)
    {
        var c = new Cmd { M = m };
        var xy = m.Xy;
        float y0 = float.MaxValue, y1 = float.MinValue;
        for (int i = 1; i < xy.Length; i += 2) { if (xy[i] < y0) y0 = xy[i]; if (xy[i] > y1) y1 = xy[i]; }
        c.MinY = y0; c.MaxY = y1;
        // 밉 단계: 화면 넓이 대 텍스처 넓이
        double sa = 0, ta = 0; var ix = m.Idx; var uv = m.Uv;
        int step = Math.Max(3, ix.Length / 3 / 64 * 3);
        for (int t = 0; t + 2 < ix.Length; t += step)
        {
            int a = ix[t] * 2, b = ix[t + 1] * 2, cc = ix[t + 2] * 2;
            sa += Math.Abs((xy[b] - xy[a]) * (xy[cc + 1] - xy[a + 1]) - (xy[b + 1] - xy[a + 1]) * (xy[cc] - xy[a]));
            ta += Math.Abs((uv[b] - uv[a]) * (uv[cc + 1] - uv[a + 1]) - (uv[b + 1] - uv[a + 1]) * (uv[cc] - uv[a]));
        }
        int lv = 0;
        if (sa > 1e-6 && ta > sa * 1.0001) lv = (int)Math.Floor(Math.Log2(Math.Sqrt(ta / sa)) + 0.15);
        c.Level = Math.Clamp(lv, 0, m.Tex.Levels - 1);
        return c;
    }

    public int AddMask(IReadOnlyList<RigMeshDraw> meshes) { masks.Add(meshes.Select(Prep).ToList()); return masks.Count - 1; }

    public void DrawMesh(float[] xy, float[] uv, int[] idx, RigTexture tex, float opacity, int mask = -1, bool invertMask = false)
    {
        if (opacity <= 0.001f || idx.Length < 3) return;
        var c = Prep(new RigMeshDraw(xy, uv, idx, tex, opacity));
        c.Mask = mask; c.Inv = invertMask;
        cmds.Add(c);
    }

    public void End()
    {
        int bands = Parallel ? Math.Clamp(Environment.ProcessorCount * 2, 1, Math.Max(1, h / 16)) : 1;
        int bh = (h + bands - 1) / bands;
        if (bands == 1) Band(0, h);
        else System.Threading.Tasks.Parallel.For(0, bands, b => Band(b * bh, Math.Min(h, (b + 1) * bh)));
    }

    void Band(int y0, int y1)
    {
        if (y0 >= y1) return;
        Array.Fill(buf, clear, y0 * w, (y1 - y0) * w);
        byte[][] mb = masks.Count > 0 ? new byte[masks.Count][] : null;
        foreach (var c in cmds)
        {
            if (c.MaxY < y0 - 1 || c.MinY > y1 + 1) continue;
            byte[] m = null;
            if (c.Mask >= 0 && c.Mask < masks.Count)
            {
                m = mb[c.Mask];
                if (m == null)
                {
                    m = mb[c.Mask] = new byte[w * (y1 - y0)];
                    foreach (var mc in masks[c.Mask]) Raster(mc, y0, y1, null, m, false);
                }
            }
            Raster(c, y0, y1, m, null, c.Inv && m != null);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Lerp(uint a, uint b, uint f)
    {
        uint g = 256 - f;
        uint rb = (((a & 0xFF00FF) * g + (b & 0xFF00FF) * f) >> 8) & 0xFF00FF;
        uint ag = (((a >> 8) & 0xFF00FF) * g + ((b >> 8) & 0xFF00FF) * f) & 0xFF00FF00;
        return rb | ag;
    }

    static double EdgeX(double x0, double y0, double x1, double y1, double y) => y1 == y0 ? x0 : x0 + (y - y0) * (x1 - x0) / (y1 - y0);

    /// <summary>띠 [y0,y1) 안에 메쉬 그리기. maskOut 이 있으면 알파만 마스크에 쌓음</summary>
    void Raster(Cmd c, int y0, int y1, byte[] mask, byte[] maskOut, bool inv)
    {
        var m = c.M; var xy = m.Xy; var uv = m.Uv; var ix = m.Idx;
        int lv = c.Level;
        var texArr = m.Tex.Mip[lv]; int tw = m.Tex.MW[lv], th = m.Tex.MH[lv];
        double ls = 1.0 / (1 << lv);
        uint op = (uint)Math.Clamp((int)Math.Round(m.Opacity * 256), 0, 256);
        fixed (uint* dstBase = buf) fixed (uint* tex = texArr)
        {
            for (int t = 0; t + 2 < ix.Length; t += 3)
            {
                int ia = ix[t], ib = ix[t + 1], ic = ix[t + 2];
                double ax = xy[ia * 2], ay = xy[ia * 2 + 1], bx = xy[ib * 2], by = xy[ib * 2 + 1], cx = xy[ic * 2], cy = xy[ic * 2 + 1];
                double minY = Math.Min(ay, Math.Min(by, cy)), maxY = Math.Max(ay, Math.Max(by, cy));
                if (maxY < y0 - 0.5 || minY > y1 + 0.5) continue;
                double minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx));
                if (maxX < -0.5 || minX > w + 0.5) continue;
                // uv 기울기 (밉 단계 좌표, 텍셀 가운데 = 정수)
                double ua = uv[ia * 2] * ls - 0.5, va = uv[ia * 2 + 1] * ls - 0.5, ub = uv[ib * 2] * ls - 0.5, vb = uv[ib * 2 + 1] * ls - 0.5, uc = uv[ic * 2] * ls - 0.5, vc = uv[ic * 2 + 1] * ls - 0.5;
                double den = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                if (Math.Abs(den) < 1e-7) continue;
                double dudx = ((ub - ua) * (cy - ay) - (uc - ua) * (by - ay)) / den, dudy = ((uc - ua) * (bx - ax) - (ub - ua) * (cx - ax)) / den;
                double dvdx = ((vb - va) * (cy - ay) - (vc - va) * (by - ay)) / den, dvdy = ((vc - va) * (bx - ax) - (vb - va) * (cx - ax)) / den;
                // y 순서 (같으면 x) — 같은 변은 늘 같은 끝점 순서로 계산
                double px0 = ax, py0 = ay, px1 = bx, py1 = by, px2 = cx, py2 = cy;
                if (py0 > py1 || (py0 == py1 && px0 > px1)) { (px0, px1) = (px1, px0); (py0, py1) = (py1, py0); }
                if (py1 > py2 || (py1 == py2 && px1 > px2)) { (px1, px2) = (px2, px1); (py1, py2) = (py2, py1); }
                if (py0 > py1 || (py0 == py1 && px0 > px1)) { (px0, px1) = (px1, px0); (py0, py1) = (py1, py0); }
                int ys = Math.Max(y0, (int)Math.Ceiling(py0 - 0.5)), ye = Math.Min(y1, (int)Math.Ceiling(py2 - 0.5));
                for (int y = ys; y < ye; y++)
                {
                    double yc = y + 0.5;
                    double xl = EdgeX(px0, py0, px2, py2, yc);
                    double xr = yc < py1 ? EdgeX(px0, py0, px1, py1, yc) : EdgeX(px1, py1, px2, py2, yc);
                    if (xl > xr) (xl, xr) = (xr, xl);
                    int xs = Math.Max(0, (int)Math.Ceiling(xl - 0.5)), xe = Math.Min(w, (int)Math.Ceiling(xr - 0.5));
                    if (xs >= xe) continue;
                    double u0 = ua + dudx * (xs + 0.5 - ax) + dudy * (yc - ay), v0 = va + dvdx * (xs + 0.5 - ax) + dvdy * (yc - ay);
                    long U = (long)Math.Round(u0 * 65536), V = (long)Math.Round(v0 * 65536);
                    long dU = (long)Math.Round(dudx * 65536), dV = (long)Math.Round(dvdx * 65536);
                    uint* drow = dstBase + y * w;
                    int mrow = (y - y0) * w;
                    for (int x = xs; x < xe; x++, U += dU, V += dV)
                    {
                        int tx = (int)(U >> 16), ty = (int)(V >> 16);
                        if (tx < -1 || ty < -1 || tx >= tw || ty >= th) continue;
                        uint fx = (uint)(U >> 8) & 255, fy = (uint)(V >> 8) & 255;
                        uint t00, t10, t01, t11;
                        if (tx >= 0 && ty >= 0 && tx < tw - 1 && ty < th - 1)
                        {
                            uint* p = tex + ty * tw + tx;
                            t00 = p[0]; t10 = p[1]; t01 = p[tw]; t11 = p[tw + 1];
                        }
                        else
                        {
                            bool x0ok = tx >= 0, x1ok = tx + 1 < tw, y0ok = ty >= 0, y1ok = ty + 1 < th;
                            t00 = x0ok && y0ok ? tex[ty * tw + tx] : 0; t10 = x1ok && y0ok ? tex[ty * tw + tx + 1] : 0;
                            t01 = x0ok && y1ok ? tex[(ty + 1) * tw + tx] : 0; t11 = x1ok && y1ok ? tex[(ty + 1) * tw + tx + 1] : 0;
                        }
                        if ((t00 | t10 | t01 | t11) == 0) continue;
                        uint s = Lerp(Lerp(t00, t10, fx), Lerp(t01, t11, fx), fy);
                        uint k = op;
                        if (mask != null) { uint mv = mask[mrow + x]; if (inv) mv = 255 - mv; k = k * (mv + (mv >> 7)) >> 8; }
                        if (k < 256) s = RigTexture.Mul(s, k);
                        uint sa = s >> 24;
                        if (sa == 0) continue;
                        if (maskOut != null)
                        {
                            uint mo = maskOut[mrow + x];
                            maskOut[mrow + x] = (byte)(mo + ((sa * (255 - mo) + 127) / 255));
                        }
                        else drow[x] = RigTexture.Over(s, drow[x]);
                    }
                }
            }
        }
    }

    public void CopyTo(Bitmap bmp)
    {
        var bd = bmp.LockBits(new Rectangle(0, 0, Math.Min(w, bmp.Width), Math.Min(h, bmp.Height)), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int rw = Math.Min(w, bmp.Width);
            for (int y = 0; y < bd.Height; y++) Marshal.Copy((int[])(object)buf, y * w, bd.Scan0 + y * bd.Stride, rw);
        }
        finally { bmp.UnlockBits(bd); }
    }

    public void Dispose() { buf = Array.Empty<uint>(); cmds.Clear(); masks.Clear(); }
}
