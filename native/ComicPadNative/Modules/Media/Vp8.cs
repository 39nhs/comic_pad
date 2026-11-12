// VP8 손실 압축 (WebP 손실 그림·WebM 동영상) — RFC 6386 을 보고 직접 짬 (외부 코덱 없음).
//  - 풀기: 키 프레임 전부 (구역(segment)·여러 토큰 파티션·단순/보통 루프 필터·선명도·필터 차이값). 웹 판(브라우저 libwebp)이 만든 손실 WebP 를 연다.
//  - 만들기: 키 프레임만 (화면 안 예측: 16×16 4가지 + 4×4 10가지, DCT/WHT, 화질 → 양자화 단계, 불(bool) 산술 부호기,
//    계수 확률은 실제 통계로 다시 계산해 이득일 때만 갱신, 루프 필터 세기는 몇 가지를 실제로 걸어 보고 고름)
// 화면 안 예측은 필터 걸기 전 픽셀을 쓰므로 (RFC 15.1) 만드는 쪽은 필터 없이 복원하고, 필터는 맨 끝에 한 번만 건다.
// 색: BT.601 (Y 16~235), 색차는 가로세로 절반. 픽셀은 미리 곱하지 않은 ARGB (알파는 따로 — WebP 는 ALPH 덩이).
namespace ComicPadNative.Modules.Media;

/// <summary>불 산술 복호기 (RFC 6386 7.3)</summary>
sealed class Vp8BoolDec
{
    readonly byte[] b; int pos; readonly int end;
    uint value, range = 255; int count;
    public Vp8BoolDec(byte[] data, int off, int len)
    {
        b = data; pos = off; end = Math.Min(data.Length, off + Math.Max(0, len));
        value = (uint)((Next() << 8) | Next());
    }
    int Next() => pos < end ? b[pos++] : 0;   // 끝을 넘으면 0 (libvpx 와 같음)
    public int Bit(int prob)
    {
        uint split = 1 + (((range - 1) * (uint)prob) >> 8), big = split << 8;
        int r;
        if (value >= big) { r = 1; range -= split; value -= big; } else { r = 0; range = split; }
        while (range < 128) { value <<= 1; range <<= 1; if (++count == 8) { count = 0; value |= (uint)Next(); } }
        return r;
    }
    public int Lit(int n) { int v = 0; while (n-- > 0) v = (v << 1) | Bit(128); return v; }
    public int Signed(int n) { int v = Lit(n); return Bit(128) != 0 ? -v : v; }
    public bool Flag() => Bit(128) != 0;
    public int Tree(sbyte[] tree, byte[] probs, int off = 0) { int i = 0; while ((i = tree[i + Bit(probs[off + (i >> 1)])]) > 0) { } return -i; }
}

/// <summary>불 산술 부호기 (RFC 6386 7.3)</summary>
sealed class Vp8BoolEnc
{
    byte[] buf = new byte[1 << 14]; int len;
    uint range = 255, bottom; int count = 24;
    void Carry() { int q = len; while (buf[--q] == 255) buf[q] = 0; buf[q]++; }
    public void Put(int prob, int bit)
    {
        uint split = 1 + (((range - 1) * (uint)prob) >> 8);
        if (bit != 0) { bottom += split; range -= split; } else range = split;
        while (range < 128)
        {
            range <<= 1;
            if ((bottom & (1u << 31)) != 0) Carry();
            bottom <<= 1;
            if (--count == 0)
            {
                if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
                buf[len++] = (byte)(bottom >> 24); bottom &= (1 << 24) - 1; count = 8;
            }
        }
    }
    public void Lit(int v, int n) { for (int i = n - 1; i >= 0; i--) Put(128, (v >> i) & 1); }
    public void Tree(sbyte[] tree, byte[] probs, int value, int off = 0)
    {
        var path = Vp8.Path(tree, value);
        foreach (var (node, bit) in path) Put(probs[off + (node >> 1)], bit);
    }
    public byte[] Finish()
    {
        int c = count; uint v = bottom;
        if ((v & (1u << (32 - c))) != 0) Carry();
        v <<= c & 7; c >>= 3;
        while (--c >= 0) v <<= 8;
        if (len + 4 > buf.Length) Array.Resize(ref buf, len + 4);
        for (c = 0; c < 4; c++) { buf[len++] = (byte)(v >> 24); v <<= 8; }
        return buf[..len];
    }
}

/// <summary>Y·U·V 판 (매크로블록 16 배수 크기)</summary>
public sealed class Vp8Planes
{
    public int W, H, MbW, MbH, Ys, Cs;
    public byte[] Y, U, V;
    public Vp8Planes(int w, int h)
    {
        W = w; H = h; MbW = (w + 15) >> 4; MbH = (h + 15) >> 4; Ys = MbW * 16; Cs = MbW * 8;
        Y = new byte[Ys * MbH * 16]; U = new byte[Cs * MbH * 8]; V = new byte[Cs * MbH * 8];
    }
    public Vp8Planes Clone() { var o = (Vp8Planes)MemberwiseClone(); o.Y = (byte[])Y.Clone(); o.U = (byte[])U.Clone(); o.V = (byte[])V.Clone(); return o; }
}

public static class Vp8
{
    // ------------------------------------------------------------------ 공통 표·나무
    // 16×16 모드 (RFC): DC 0, V 1, H 2, TM 3, B_PRED 4. 4×4 모드: DC 0, TM 1, VE 2, HE 3, LD 4, RD 5, VR 6, VL 7, HD 8, HU 9
    const int DC = 0, VP = 1, HP = 2, TM = 3, BP = 4;
    internal static readonly sbyte[] KfYTree = { -BP, 2, 4, 6, -DC, -VP, -HP, -TM };
    internal static readonly byte[] KfYProb = { 145, 156, 163, 128 };
    internal static readonly sbyte[] UvTree = { -DC, 2, -VP, 4, -HP, -TM };
    internal static readonly byte[] UvProb = { 142, 114, 183 };
    internal static readonly sbyte[] BTree = { 0, 2, -1, 4, -2, 6, 8, 12, -3, 10, -5, -6, -4, 14, -7, 16, -8, -9 };
    static readonly int[] BOfY = { 0, 2, 3, 1 };   // 16×16 모드 → 이웃 4×4 모드 문맥 (DC→B_DC, V→B_VE, H→B_HE, TM→B_TM)
    static readonly byte[] SegTreeProbDefault = { 255, 255, 255 };
    internal static readonly int[] Zigzag = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
    internal static readonly int[] Bands = { 0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0 };
    static readonly byte[][] Cats =
    {
        new byte[] { 173, 148, 140 }, new byte[] { 176, 155, 140, 135 }, new byte[] { 180, 157, 141, 134, 130 },
        new byte[] { 254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129 },
    };

    static readonly Dictionary<(sbyte[], int), (int node, int bit)[]> paths = new();
    /// <summary>나무에서 값까지 가는 길 (마디 번호, 비트)</summary>
    internal static (int node, int bit)[] Path(sbyte[] tree, int value)
    {
        lock (paths)
        {
            if (paths.TryGetValue((tree, value), out var p)) return p;
            var list = new List<(int, int)>();
            bool Find(int i)
            {
                for (int bit = 0; bit < 2; bit++)
                {
                    int t = tree[i + bit];
                    list.Add((i, bit));
                    if (t > 0 ? Find(t) : -t == value) return true;
                    list.RemoveAt(list.Count - 1);
                }
                return false;
            }
            if (!Find(0)) throw new ArgumentException("나무에 없는 값");
            return paths[(tree, value)] = list.ToArray();
        }
    }

    static int Clip255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    static int Clip(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    // ------------------------------------------------------------------ 변환 (RFC 14.3·14.4, 앞쪽은 libvpx 와 같은 정수식)
    /// <summary>역 DCT 를 해서 pred 에 더해 dst 에 씀 (4×4)</summary>
    static void IdctAdd(short[] c, int co, byte[] dst, int di, int ds)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int i0 = c[co + i], i4 = c[co + 4 + i], i8 = c[co + 8 + i], i12 = c[co + 12 + i];
            int a1 = i0 + i8, b1 = i0 - i8;
            int c1 = ((i4 * 35468) >> 16) - (i12 + ((i12 * 20091) >> 16));
            int d1 = (i4 + ((i4 * 20091) >> 16)) + ((i12 * 35468) >> 16);
            t[i] = a1 + d1; t[12 + i] = a1 - d1; t[4 + i] = b1 + c1; t[8 + i] = b1 - c1;
        }
        for (int r = 0; r < 4; r++)
        {
            int i0 = t[r * 4], i1 = t[r * 4 + 1], i2 = t[r * 4 + 2], i3 = t[r * 4 + 3];
            int a1 = i0 + i2, b1 = i0 - i2;
            int c1 = ((i1 * 35468) >> 16) - (i3 + ((i3 * 20091) >> 16));
            int d1 = (i1 + ((i1 * 20091) >> 16)) + ((i3 * 35468) >> 16);
            int o = di + r * ds;
            dst[o] = (byte)Clip255(dst[o] + ((a1 + d1 + 4) >> 3));
            dst[o + 3] = (byte)Clip255(dst[o + 3] + ((a1 - d1 + 4) >> 3));
            dst[o + 1] = (byte)Clip255(dst[o + 1] + ((b1 + c1 + 4) >> 3));
            dst[o + 2] = (byte)Clip255(dst[o + 2] + ((b1 - c1 + 4) >> 3));
        }
    }

    /// <summary>역 WHT: 16개 → 각 블록의 DC (c[blk*16])</summary>
    static void InvWht(short[] y2, int yo, short[] c)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a1 = y2[yo + i] + y2[yo + 12 + i], b1 = y2[yo + 4 + i] + y2[yo + 8 + i];
            int c1 = y2[yo + 4 + i] - y2[yo + 8 + i], d1 = y2[yo + i] - y2[yo + 12 + i];
            t[i] = a1 + b1; t[4 + i] = c1 + d1; t[8 + i] = a1 - b1; t[12 + i] = d1 - c1;
        }
        for (int i = 0; i < 4; i++)
        {
            int a1 = t[i * 4] + t[i * 4 + 3], b1 = t[i * 4 + 1] + t[i * 4 + 2];
            int c1 = t[i * 4 + 1] - t[i * 4 + 2], d1 = t[i * 4] - t[i * 4 + 3];
            c[(i * 4 + 0) * 16] = (short)((a1 + b1 + 3) >> 3);
            c[(i * 4 + 1) * 16] = (short)((c1 + d1 + 3) >> 3);
            c[(i * 4 + 2) * 16] = (short)((a1 - b1 + 3) >> 3);
            c[(i * 4 + 3) * 16] = (short)((d1 - c1 + 3) >> 3);
        }
    }

    /// <summary>앞 DCT (차이 4×4 → 계수, libvpx vp8_short_fdct4x4_c)</summary>
    static void Fdct(Span<int> d, short[] o, int oo)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a1 = (d[i * 4] + d[i * 4 + 3]) * 8, b1 = (d[i * 4 + 1] + d[i * 4 + 2]) * 8;
            int c1 = (d[i * 4 + 1] - d[i * 4 + 2]) * 8, d1 = (d[i * 4] - d[i * 4 + 3]) * 8;
            t[i * 4] = a1 + b1; t[i * 4 + 2] = a1 - b1;
            t[i * 4 + 1] = (c1 * 2217 + d1 * 5352 + 14500) >> 12;
            t[i * 4 + 3] = (d1 * 2217 - c1 * 5352 + 7500) >> 12;
        }
        for (int i = 0; i < 4; i++)
        {
            int a1 = t[i] + t[12 + i], b1 = t[4 + i] + t[8 + i], c1 = t[4 + i] - t[8 + i], d1 = t[i] - t[12 + i];
            o[oo + i] = (short)((a1 + b1 + 7) >> 4); o[oo + 8 + i] = (short)((a1 - b1 + 7) >> 4);
            o[oo + 4 + i] = (short)(((c1 * 2217 + d1 * 5352 + 12000) >> 16) + (d1 != 0 ? 1 : 0));
            o[oo + 12 + i] = (short)((d1 * 2217 - c1 * 5352 + 51000) >> 16);
        }
    }

    /// <summary>앞 WHT (16개 DC → Y2, libvpx vp8_short_walsh4x4_c)</summary>
    static void Fwht(Span<int> dc, short[] o, int oo)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a1 = (dc[i * 4] + dc[i * 4 + 2]) * 4, d1 = (dc[i * 4 + 1] + dc[i * 4 + 3]) * 4;
            int c1 = (dc[i * 4 + 1] - dc[i * 4 + 3]) * 4, b1 = (dc[i * 4] - dc[i * 4 + 2]) * 4;
            t[i * 4] = a1 + d1 + (a1 != 0 ? 1 : 0); t[i * 4 + 1] = b1 + c1; t[i * 4 + 2] = b1 - c1; t[i * 4 + 3] = a1 - d1;
        }
        for (int i = 0; i < 4; i++)
        {
            int a1 = t[i] + t[8 + i], d1 = t[4 + i] + t[12 + i], c1 = t[4 + i] - t[12 + i], b1 = t[i] - t[8 + i];
            int a2 = a1 + d1, b2 = b1 + c1, c2 = b1 - c1, d2 = a1 - d1;
            a2 += a2 < 0 ? 1 : 0; b2 += b2 < 0 ? 1 : 0; c2 += c2 < 0 ? 1 : 0; d2 += d2 < 0 ? 1 : 0;
            o[oo + i] = (short)((a2 + 3) >> 3); o[oo + 4 + i] = (short)((b2 + 3) >> 3);
            o[oo + 8 + i] = (short)((c2 + 3) >> 3); o[oo + 12 + i] = (short)((d2 + 3) >> 3);
        }
    }

    // ------------------------------------------------------------------ 예측 (RFC 12)
    /// <summary>판의 픽셀 (위 바깥 127, 왼쪽 바깥 129, 오른쪽 바깥은 그 줄 끝 픽셀)</summary>
    static int Px(byte[] p, int stride, int x, int y)
    {
        if (y < 0) return 127;
        if (x < 0) return 129;
        if (x >= stride) x = stride - 1;
        return p[y * stride + x];
    }

    /// <summary>16×16 또는 8×8 예측 (mode: DC·V·H·TM)</summary>
    static void PredBig(byte[] p, int stride, int x0, int y0, int n, int mode, byte[] pred)
    {
        switch (mode)
        {
            case DC:
            {
                bool top = y0 > 0, left = x0 > 0;
                int s = 0, sh = n == 16 ? 4 : 3, v;
                if (top) for (int i = 0; i < n; i++) s += p[(y0 - 1) * stride + x0 + i];
                if (left) for (int i = 0; i < n; i++) s += p[(y0 + i) * stride + x0 - 1];
                v = top && left ? (s + n) >> (sh + 1) : top || left ? (s + (n >> 1)) >> sh : 128;
                Array.Fill(pred, (byte)v, 0, n * n);
                break;
            }
            case VP:
                for (int c = 0; c < n; c++) { byte a = (byte)Px(p, stride, x0 + c, y0 - 1); for (int r = 0; r < n; r++) pred[r * n + c] = a; }
                break;
            case HP:
                for (int r = 0; r < n; r++) { byte l = (byte)Px(p, stride, x0 - 1, y0 + r); for (int c = 0; c < n; c++) pred[r * n + c] = l; }
                break;
            default:
            {
                int tl = Px(p, stride, x0 - 1, y0 - 1);
                for (int r = 0; r < n; r++)
                {
                    int l = Px(p, stride, x0 - 1, y0 + r) - tl;
                    for (int c = 0; c < n; c++) pred[r * n + c] = (byte)Clip255(l + Px(p, stride, x0 + c, y0 - 1));
                }
                break;
            }
        }
    }

    /// <summary>4×4 블록 둘레: A[0]=왼쪽 위, A[1..8]=위 8개, L[0..3]=왼쪽 (오른쪽 열 블록의 위 오른쪽은 매크로블록 위 줄에서)</summary>
    static void Edges(byte[] p, int stride, int mbx, int mby, int bx, int by, int[] A, int[] L)
    {
        int x0 = mbx * 16 + bx * 4, y0 = mby * 16 + by * 4;
        for (int i = -1; i < 4; i++) A[i + 1] = Px(p, stride, x0 + i, y0 - 1);
        // 위 오른쪽 4개: 오른쪽 열 블록은 매크로블록 위 줄의 16~19 열 (맨 위 줄 127, 맨 오른쪽 매크로블록은 위 줄 끝 픽셀)
        for (int i = 0; i < 4; i++) A[5 + i] = bx < 3 ? Px(p, stride, x0 + 4 + i, y0 - 1) : Px(p, stride, mbx * 16 + 16 + i, mby * 16 - 1);
        for (int i = 0; i < 4; i++) L[i] = Px(p, stride, x0 - 1, y0 + i);
    }

    static int Avg3(int x, int y, int z) => (x + y + y + z + 2) >> 2;
    static int Avg2(int x, int y) => (x + y + 1) >> 1;

    /// <summary>4×4 예측 (RFC 12.3 subblock_intra_predict)</summary>
    static void Pred4(int[] Ae, int[] L, int mode, int[] B, int[] E)
    {
        // Ae[0] = P (A[-1]), Ae[1..8] = A[0..7]
        int P = Ae[0];
        int A(int i) => Ae[i + 1];
        int Lx(int i) => i < 0 ? P : L[i];
        E[0] = L[3]; E[1] = L[2]; E[2] = L[1]; E[3] = L[0]; E[4] = P; E[5] = A(0); E[6] = A(1); E[7] = A(2); E[8] = A(3);
        int E3(int i) => Avg3(E[i - 1], E[i], E[i + 1]);
        int E2(int i) => Avg2(E[i], E[i + 1]);
        int A3(int i) => Avg3(A(i - 1), A(i), A(i + 1));
        int A2(int i) => Avg2(A(i), A(i + 1));
        void S(int r, int c, int v) => B[r * 4 + c] = v;
        switch (mode)
        {
            case 0: { int v = 4; for (int i = 0; i < 4; i++) v += A(i) + L[i]; v >>= 3; for (int i = 0; i < 16; i++) B[i] = v; break; }
            case 1: for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) S(r, c, Clip255(L[r] + A(c) - P)); break;
            case 2: for (int c = 0; c < 4; c++) { int v = A3(c); for (int r = 0; r < 4; r++) S(r, c, v); } break;
            case 3:
            {
                for (int r = 0; r < 4; r++)
                {
                    int v = r == 3 ? Avg3(L[2], L[3], L[3]) : Avg3(Lx(r - 1), L[r], L[r + 1]);
                    for (int c = 0; c < 4; c++) S(r, c, v);
                }
                break;
            }
            case 4:
                S(0, 0, A3(1));
                S(0, 1, A3(2)); S(1, 0, A3(2));
                S(0, 2, A3(3)); S(1, 1, A3(3)); S(2, 0, A3(3));
                S(0, 3, A3(4)); S(1, 2, A3(4)); S(2, 1, A3(4)); S(3, 0, A3(4));
                S(1, 3, A3(5)); S(2, 2, A3(5)); S(3, 1, A3(5));
                S(2, 3, A3(6)); S(3, 2, A3(6));
                S(3, 3, Avg3(A(6), A(7), A(7)));
                break;
            case 5:
                S(3, 0, E3(1));
                S(3, 1, E3(2)); S(2, 0, E3(2));
                S(3, 2, E3(3)); S(2, 1, E3(3)); S(1, 0, E3(3));
                S(3, 3, E3(4)); S(2, 2, E3(4)); S(1, 1, E3(4)); S(0, 0, E3(4));
                S(2, 3, E3(5)); S(1, 2, E3(5)); S(0, 1, E3(5));
                S(1, 3, E3(6)); S(0, 2, E3(6));
                S(0, 3, E3(7));
                break;
            case 6:
                S(3, 0, E3(2));
                S(2, 0, E3(3));
                S(3, 1, E3(4)); S(1, 0, E3(4));
                S(2, 1, E2(4)); S(0, 0, E2(4));
                S(3, 2, E3(5)); S(1, 1, E3(5));
                S(2, 2, E2(5)); S(0, 1, E2(5));
                S(3, 3, E3(6)); S(1, 2, E3(6));
                S(2, 3, E2(6)); S(0, 2, E2(6));
                S(1, 3, E3(7));
                S(0, 3, E2(7));
                break;
            case 7:
                S(0, 0, A2(0));
                S(1, 0, A3(1));
                S(2, 0, A2(1)); S(0, 1, A2(1));
                S(1, 1, A3(2)); S(3, 0, A3(2));
                S(2, 1, A2(2)); S(0, 2, A2(2));
                S(3, 1, A3(3)); S(1, 2, A3(3));
                S(2, 2, A2(3)); S(0, 3, A2(3));
                S(3, 2, A3(4)); S(1, 3, A3(4));
                S(2, 3, A3(5));
                S(3, 3, A3(6));
                break;
            case 8:
                S(3, 0, E2(0));
                S(3, 1, E3(1));
                S(2, 0, E2(1)); S(3, 2, E2(1));
                S(2, 1, E3(2)); S(3, 3, E3(2));
                S(2, 2, E2(2)); S(1, 0, E2(2));
                S(2, 3, E3(3)); S(1, 1, E3(3));
                S(1, 2, E2(3)); S(0, 0, E2(3));
                S(1, 3, E3(4)); S(0, 1, E3(4));
                S(0, 2, E3(5));
                S(0, 3, E3(6));
                break;
            default:
                S(0, 0, Avg2(L[0], L[1]));
                S(0, 1, Avg3(L[0], L[1], L[2]));
                S(0, 2, Avg2(L[1], L[2])); S(1, 0, Avg2(L[1], L[2]));
                S(0, 3, Avg3(L[1], L[2], L[3])); S(1, 1, Avg3(L[1], L[2], L[3]));
                S(1, 2, Avg2(L[2], L[3])); S(2, 0, Avg2(L[2], L[3]));
                S(1, 3, Avg3(L[2], L[3], L[3])); S(2, 1, Avg3(L[2], L[3], L[3]));
                S(2, 2, L[3]); S(2, 3, L[3]); S(3, 0, L[3]); S(3, 1, L[3]); S(3, 2, L[3]); S(3, 3, L[3]);
                break;
        }
    }

    // ------------------------------------------------------------------ 양자화 단계
    internal readonly struct Quant
    {
        public readonly int Y1Dc, Y1Ac, Y2Dc, Y2Ac, UvDc, UvAc;
        public Quant(int q, int y1dc, int y2dc, int y2ac, int uvdc, int uvac)
        {
            static int I(int v) => Clip(v, 0, 127);
            Y1Dc = Vp8Tab.DcQ[I(q + y1dc)]; Y1Ac = Vp8Tab.AcQ[I(q)];
            Y2Dc = Vp8Tab.DcQ[I(q + y2dc)] * 2; Y2Ac = Math.Max(8, Vp8Tab.AcQ[I(q + y2ac)] * 155 / 100);
            UvDc = Math.Min(132, (int)Vp8Tab.DcQ[I(q + uvdc)]); UvAc = Vp8Tab.AcQ[I(q + uvac)];
        }
    }

    // ------------------------------------------------------------------ 루프 필터 (RFC 15)
    struct FInfo { public int Limit, ILevel, Hev; public bool Inner; }

    static int U2S(int v) => v - 128;
    static int SClamp(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

    static bool Needs(byte[] p, int i, int s, int t2) => 4 * Math.Abs(p[i - s] - p[i]) + Math.Abs(p[i - 2 * s] - p[i + s]) <= t2;
    static bool Needs2(byte[] p, int i, int s, int t2, int it)
    {
        int p3 = p[i - 4 * s], p2 = p[i - 3 * s], p1 = p[i - 2 * s], p0 = p[i - s], q0 = p[i], q1 = p[i + s], q2 = p[i + 2 * s], q3 = p[i + 3 * s];
        if (4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) > t2) return false;
        return Math.Abs(p3 - p2) <= it && Math.Abs(p2 - p1) <= it && Math.Abs(p1 - p0) <= it && Math.Abs(q3 - q2) <= it && Math.Abs(q2 - q1) <= it && Math.Abs(q1 - q0) <= it;
    }
    static bool Hev(byte[] p, int i, int s, int th) => Math.Abs(p[i - 2 * s] - p[i - s]) > th || Math.Abs(p[i + s] - p[i]) > th;
    static void Filter2(byte[] p, int i, int s)
    {
        int p1 = U2S(p[i - 2 * s]), p0 = U2S(p[i - s]), q0 = U2S(p[i]), q1 = U2S(p[i + s]);
        int a = SClamp(SClamp(p1 - q1) + 3 * (q0 - p0));
        int b = SClamp(a + 3) >> 3; a = SClamp(a + 4) >> 3;
        p[i] = (byte)(SClamp(q0 - a) + 128); p[i - s] = (byte)(SClamp(p0 + b) + 128);
    }
    static void Filter4(byte[] p, int i, int s)
    {
        int p1 = U2S(p[i - 2 * s]), p0 = U2S(p[i - s]), q0 = U2S(p[i]), q1 = U2S(p[i + s]);
        int a = SClamp(3 * (q0 - p0));
        int b = SClamp(a + 3) >> 3; a = SClamp(a + 4) >> 3;
        p[i] = (byte)(SClamp(q0 - a) + 128); p[i - s] = (byte)(SClamp(p0 + b) + 128);
        a = (a + 1) >> 1;
        p[i + s] = (byte)(SClamp(q1 - a) + 128); p[i - 2 * s] = (byte)(SClamp(p1 + a) + 128);
    }
    static void Filter6(byte[] p, int i, int s)
    {
        int p2 = U2S(p[i - 3 * s]), p1 = U2S(p[i - 2 * s]), p0 = U2S(p[i - s]), q0 = U2S(p[i]), q1 = U2S(p[i + s]), q2 = U2S(p[i + 2 * s]);
        int w = SClamp(SClamp(p1 - q1) + 3 * (q0 - p0));
        int a = SClamp((27 * w + 63) >> 7);
        p[i] = (byte)(SClamp(q0 - a) + 128); p[i - s] = (byte)(SClamp(p0 + a) + 128);
        a = SClamp((18 * w + 63) >> 7);
        p[i + s] = (byte)(SClamp(q1 - a) + 128); p[i - 2 * s] = (byte)(SClamp(p1 + a) + 128);
        a = SClamp((9 * w + 63) >> 7);
        p[i + 2 * s] = (byte)(SClamp(q2 - a) + 128); p[i - 3 * s] = (byte)(SClamp(p2 + a) + 128);
    }
    /// <summary>모서리 하나 (i = 첫 q0 위치, s = 모서리를 가로지르는 걸음, along = 모서리 따라 걸음)</summary>
    static void EdgeMb(byte[] p, int i, int s, int along, int n, int t2, int it, int hev)
    {
        for (int k = 0; k < n; k++, i += along)
            if (Needs2(p, i, s, t2, it)) { if (Hev(p, i, s, hev)) Filter2(p, i, s); else Filter6(p, i, s); }
    }
    static void EdgeIn(byte[] p, int i, int s, int along, int n, int t2, int it, int hev)
    {
        for (int k = 0; k < n; k++, i += along)
            if (Needs2(p, i, s, t2, it)) { if (Hev(p, i, s, hev)) Filter2(p, i, s); else Filter4(p, i, s); }
    }
    static void EdgeSimple(byte[] p, int i, int s, int along, int n, int t2)
    {
        for (int k = 0; k < n; k++, i += along) if (Needs(p, i, s, t2)) Filter2(p, i, s);
    }

    static FInfo FilterInfo(int level, int sharp, bool inner)
    {
        var f = new FInfo { Inner = inner };
        level = Clip(level, 0, 63);
        if (level == 0) return f;
        int il = level;
        if (sharp > 0) { il >>= sharp > 4 ? 2 : 1; if (il > 9 - sharp) il = 9 - sharp; }
        if (il < 1) il = 1;
        f.ILevel = il; f.Limit = 2 * level + il; f.Hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;
        return f;
    }

    /// <summary>판 전체에 루프 필터 (매크로블록 차례대로: 왼쪽 모서리 → 안 세로 → 위 모서리 → 안 가로)</summary>
    static void LoopFilter(Vp8Planes f, FInfo[] info, bool simple)
    {
        int ys = f.Ys, cs = f.Cs;
        for (int my = 0; my < f.MbH; my++)
            for (int mx = 0; mx < f.MbW; mx++)
            {
                var fi = info[my * f.MbW + mx];
                if (fi.Limit == 0) continue;
                int yi = my * 16 * ys + mx * 16, ci = my * 8 * cs + mx * 8;
                int mbT = 2 * (fi.Limit + 4) + 1, inT = 2 * fi.Limit + 1;
                if (simple)
                {
                    if (mx > 0) EdgeSimple(f.Y, yi, 1, ys, 16, mbT);
                    if (fi.Inner) for (int k = 4; k < 16; k += 4) EdgeSimple(f.Y, yi + k, 1, ys, 16, inT);
                    if (my > 0) EdgeSimple(f.Y, yi, ys, 1, 16, mbT);
                    if (fi.Inner) for (int k = 4; k < 16; k += 4) EdgeSimple(f.Y, yi + k * ys, ys, 1, 16, inT);
                    continue;
                }
                int il = fi.ILevel, hv = fi.Hev;
                if (mx > 0) { EdgeMb(f.Y, yi, 1, ys, 16, mbT, il, hv); EdgeMb(f.U, ci, 1, cs, 8, mbT, il, hv); EdgeMb(f.V, ci, 1, cs, 8, mbT, il, hv); }
                if (fi.Inner)
                {
                    for (int k = 4; k < 16; k += 4) EdgeIn(f.Y, yi + k, 1, ys, 16, inT, il, hv);
                    EdgeIn(f.U, ci + 4, 1, cs, 8, inT, il, hv); EdgeIn(f.V, ci + 4, 1, cs, 8, inT, il, hv);
                }
                if (my > 0) { EdgeMb(f.Y, yi, ys, 1, 16, mbT, il, hv); EdgeMb(f.U, ci, cs, 1, 8, mbT, il, hv); EdgeMb(f.V, ci, cs, 1, 8, mbT, il, hv); }
                if (fi.Inner)
                {
                    for (int k = 4; k < 16; k += 4) EdgeIn(f.Y, yi + k * ys, ys, 1, 16, inT, il, hv);
                    EdgeIn(f.U, ci + 4 * cs, cs, 1, 8, inT, il, hv); EdgeIn(f.V, ci + 4 * cs, cs, 1, 8, inT, il, hv);
                }
            }
    }

    // ================================================================== 풀기
    /// <summary>크기만 (키 프레임 머리)</summary>
    public static (int w, int h) Size(byte[] b, int off, int len)
    {
        if (len < 10 || (b[off] & 1) != 0 || b[off + 3] != 0x9d || b[off + 4] != 0x01 || b[off + 5] != 0x2a) throw new InvalidDataException("VP8 키 프레임이 아닙니다");
        return ((b[off + 6] | (b[off + 7] << 8)) & 0x3fff, (b[off + 8] | (b[off + 9] << 8)) & 0x3fff);
    }

    /// <summary>VP8 키 프레임 → Y·U·V (필터까지)</summary>
    public static Vp8Planes DecodePlanes(byte[] b, int off, int len)
    {
        if (len < 10) throw new InvalidDataException("VP8 데이터가 너무 짧습니다");
        int tag = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16);
        if ((tag & 1) != 0) throw new NotSupportedException("VP8 사이 프레임(키 프레임이 아님)은 풀 수 없습니다");
        int firstSize = (tag >> 5) & 0x7ffff;
        var (w, h) = Size(b, off, len);
        if (w == 0 || h == 0) throw new InvalidDataException("VP8 크기가 0 입니다");
        int p = off + 10, end = off + len;
        if (p + firstSize > end) throw new InvalidDataException("VP8 첫 파티션이 잘렸습니다");
        var d = new Vp8BoolDec(b, p, firstSize);
        d.Lit(1); d.Lit(1);   // 색 공간·자르기 방식
        // 구역
        bool segOn = d.Flag(), updMap = false, absDelta = false;
        var segQ = new int[4]; var segLf = new int[4]; var segProb = (byte[])SegTreeProbDefault.Clone();
        if (segOn)
        {
            updMap = d.Flag();
            if (d.Flag())
            {
                absDelta = d.Flag();
                for (int i = 0; i < 4; i++) segQ[i] = d.Flag() ? d.Signed(7) : 0;
                for (int i = 0; i < 4; i++) segLf[i] = d.Flag() ? d.Signed(6) : 0;
            }
            if (updMap) for (int i = 0; i < 3; i++) segProb[i] = d.Flag() ? (byte)d.Lit(8) : (byte)255;
        }
        // 필터
        bool simple = d.Flag(); int level = d.Lit(6), sharp = d.Lit(3);
        bool useDelta = d.Flag(); var refDelta = new int[4]; var modeDelta = new int[4];
        if (useDelta && d.Flag())
        {
            for (int i = 0; i < 4; i++) if (d.Flag()) refDelta[i] = d.Signed(6);
            for (int i = 0; i < 4; i++) if (d.Flag()) modeDelta[i] = d.Signed(6);
        }
        // 토큰 파티션
        int nParts = 1 << d.Lit(2);
        int pp = p + firstSize, sizesAt = pp; pp += 3 * (nParts - 1);
        if (pp > end) throw new InvalidDataException("VP8 파티션 크기가 잘렸습니다");
        var parts = new Vp8BoolDec[nParts];
        for (int i = 0; i < nParts; i++)
        {
            int sz = i < nParts - 1 ? b[sizesAt + i * 3] | (b[sizesAt + i * 3 + 1] << 8) | (b[sizesAt + i * 3 + 2] << 16) : end - pp;
            sz = Math.Max(0, Math.Min(sz, end - pp));
            parts[i] = new Vp8BoolDec(b, pp, sz); pp += sz;
        }
        // 양자화
        int baseQ = d.Lit(7);
        int dY1Dc = d.Flag() ? d.Signed(4) : 0, dY2Dc = d.Flag() ? d.Signed(4) : 0, dY2Ac = d.Flag() ? d.Signed(4) : 0, dUvDc = d.Flag() ? d.Signed(4) : 0, dUvAc = d.Flag() ? d.Signed(4) : 0;
        var quants = new Quant[4];
        var finfo = new FInfo[8];
        for (int s = 0; s < 4; s++)
        {
            int q = segOn ? (absDelta ? segQ[s] : baseQ + segQ[s]) : baseQ;
            quants[s] = new Quant(q, dY1Dc, dY2Dc, dY2Ac, dUvDc, dUvAc);
            int bl = segOn ? (absDelta ? segLf[s] : level + segLf[s]) : level;
            for (int i4 = 0; i4 < 2; i4++)
            {
                int lv = bl;
                if (useDelta) { lv += refDelta[0]; if (i4 == 1) lv += modeDelta[0]; }
                finfo[s * 2 + i4] = FilterInfo(lv, sharp, i4 == 1);
            }
        }
        d.Lit(1);   // 확률 되돌리기 (키 프레임 하나라 상관없음)
        var probs = (byte[])Vp8Tab.CoefDefault.Clone();
        for (int i = 0; i < probs.Length; i++) if (d.Bit(Vp8Tab.CoefUpdate[i]) != 0) probs[i] = (byte)d.Lit(8);
        bool useSkip = d.Flag(); int skipP = useSkip ? d.Lit(8) : 0;

        var f = new Vp8Planes(w, h);
        int mbw = f.MbW, mbh = f.MbH;
        var mbInfo = new FInfo[mbw * mbh];
        var topB = new int[mbw * 4]; var topNz = new int[mbw]; var topNzDc = new int[mbw];
        var coef = new short[25 * 16];
        var pred = new byte[256];
        var bmodes = new int[16];
        int[] A = new int[9], L = new int[4], B4 = new int[16], E = new int[9];
        var leftB = new int[4];
        for (int my = 0; my < mbh; my++)
        {
            var tok = parts[my & (nParts - 1)];
            Array.Clear(leftB);
            int leftNz = 0, leftNzDc = 0;
            for (int mx = 0; mx < mbw; mx++)
            {
                // ---- 모드
                int seg = updMap ? (d.Bit(segProb[0]) == 0 ? d.Bit(segProb[1]) : 2 + d.Bit(segProb[2])) : 0;
                bool skip = useSkip && d.Bit(skipP) != 0;
                int ymode = d.Tree(KfYTree, KfYProb);
                if (ymode == BP)
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int a = topB[mx * 4 + x], l = leftB[y];
                            int m = d.Tree(BTree, Vp8Tab.KfBmode, (a * 10 + l) * 9);
                            bmodes[y * 4 + x] = m; topB[mx * 4 + x] = m; leftB[y] = m;
                        }
                }
                else
                {
                    int bm = BOfY[ymode];
                    for (int i = 0; i < 4; i++) { topB[mx * 4 + i] = bm; leftB[i] = bm; }
                }
                int uvmode = d.Tree(UvTree, UvProb);
                // ---- 계수
                Array.Clear(coef);
                var q = quants[seg];
                bool any = false;
                if (!skip)
                {
                    int tnz = topNz[mx], lnz = leftNz;
                    int first, type;
                    if (ymode != BP)
                    {
                        int ctx = topNzDc[mx] + leftNzDc;
                        int nz = Tokens(tok, probs, 1, ctx, coef, 24 * 16, 0, q.Y2Dc, q.Y2Ac);
                        topNzDc[mx] = leftNzDc = nz > 0 ? 1 : 0;
                        if (nz > 0) { InvWht(coef, 24 * 16, coef); any = true; }
                        first = 1; type = 0;
                    }
                    else { first = 0; type = 3; }
                    int ntop = 0, nleft = 0;
                    for (int y = 0; y < 4; y++)
                    {
                        int l = (lnz >> y) & 1;
                        for (int x = 0; x < 4; x++)
                        {
                            int t = (tnz >> x) & 1;
                            int nz = Tokens(tok, probs, type, l + t, coef, (y * 4 + x) * 16, first, q.Y1Dc, q.Y1Ac);
                            int fl = nz > first ? 1 : 0;
                            l = fl; tnz = (tnz & ~(1 << x)) | (fl << x);
                            if (fl != 0) any = true;
                        }
                        nleft |= l << y;
                    }
                    ntop = tnz & 15;
                    // 색차: U(16~19), V(20~23) — 문맥 비트: 위 4~5(U)·6~7(V), 왼쪽 같은 자리
                    int ctop = topNz[mx] >> 4, cleft = leftNz >> 4;
                    for (int ch = 0; ch < 2; ch++)
                        for (int y = 0; y < 2; y++)
                        {
                            int l = (cleft >> (ch * 2 + y)) & 1;
                            for (int x = 0; x < 2; x++)
                            {
                                int t = (ctop >> (ch * 2 + x)) & 1;
                                int nz = Tokens(tok, probs, 2, l + t, coef, (16 + ch * 4 + y * 2 + x) * 16, 0, q.UvDc, q.UvAc);
                                int fl = nz > 0 ? 1 : 0;
                                l = fl; ctop = (ctop & ~(1 << (ch * 2 + x))) | (fl << (ch * 2 + x));
                                if (fl != 0) any = true;
                            }
                            cleft = (cleft & ~(1 << (ch * 2 + y))) | (l << (ch * 2 + y));
                        }
                    topNz[mx] = ntop | (ctop << 4); leftNz = nleft | (cleft << 4);
                }
                else
                {
                    topNz[mx] = 0; leftNz = 0;
                    if (ymode != BP) { topNzDc[mx] = 0; leftNzDc = 0; }
                }
                // ---- 복원
                ReconMb(f, mx, my, ymode, bmodes, uvmode, coef, pred, A, L, B4, E);
                var fi = finfo[seg * 2 + (ymode == BP ? 1 : 0)];
                fi.Inner = ymode == BP || any;
                mbInfo[my * mbw + mx] = fi;
            }
        }
        if (level > 0 || segOn || useDelta) LoopFilter(f, mbInfo, simple);
        return f;
    }

    /// <summary>한 블록의 계수 읽기 (libwebp GetCoeffs 와 같은 순서). 돌려주는 값: 마지막 0 아닌 계수 다음 위치 (없으면 first)</summary>
    static int Tokens(Vp8BoolDec br, byte[] probs, int type, int ctx, short[] o, int oo, int n, int dcq, int acq)
    {
        int bi(int pos, int c) => ((type * 8 + Bands[pos]) * 3 + c) * 11;
        int p = bi(n, ctx);
        for (; n < 16; ++n)
        {
            if (br.Bit(probs[p]) == 0) return n;   // 끝
            while (br.Bit(probs[p + 1]) == 0)
            {
                p = bi(++n, 0);
                if (n == 16) return 16;
            }
            int v, nc;
            if (br.Bit(probs[p + 2]) == 0) { v = 1; nc = 1; }
            else
            {
                if (br.Bit(probs[p + 3]) == 0) v = br.Bit(probs[p + 4]) == 0 ? 2 : 3 + br.Bit(probs[p + 5]);
                else if (br.Bit(probs[p + 6]) == 0)
                {
                    if (br.Bit(probs[p + 7]) == 0) v = 5 + br.Bit(159);
                    else { v = 7 + 2 * br.Bit(165); v += br.Bit(145); }
                }
                else
                {
                    int b1 = br.Bit(probs[p + 8]), b0 = br.Bit(probs[p + 9 + b1]);
                    int cat = 2 * b1 + b0;
                    v = 0;
                    foreach (var pr in Cats[cat]) v += v + br.Bit(pr);
                    v += 3 + (8 << cat);
                }
                nc = 2;
            }
            if (br.Bit(128) != 0) v = -v;
            o[oo + Zigzag[n]] = (short)Math.Clamp(v * (n > 0 ? acq : dcq), short.MinValue, short.MaxValue);
            if (n < 15) p = bi(n + 1, nc);
        }
        return 16;
    }

    /// <summary>예측 + 계수 → 판에 복원 (필터 전)</summary>
    static void ReconMb(Vp8Planes f, int mx, int my, int ymode, int[] bmodes, int uvmode, short[] coef, byte[] pred, int[] A, int[] L, int[] B4, int[] E)
    {
        int ys = f.Ys, cs = f.Cs;
        int x0 = mx * 16, y0 = my * 16;
        if (ymode != BP)
        {
            PredBig(f.Y, ys, x0, y0, 16, ymode, pred);
            for (int r = 0; r < 16; r++) Array.Copy(pred, r * 16, f.Y, (y0 + r) * ys + x0, 16);
            for (int k = 0; k < 16; k++) if (!Zero(coef, k * 16)) IdctAdd(coef, k * 16, f.Y, (y0 + (k >> 2) * 4) * ys + x0 + (k & 3) * 4, ys);
        }
        else
        {
            for (int k = 0; k < 16; k++)
            {
                int bx = k & 3, by = k >> 2;
                Edges(f.Y, ys, mx, my, bx, by, A, L);
                Pred4(A, L, bmodes[k], B4, E);
                int di = (y0 + by * 4) * ys + x0 + bx * 4;
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) f.Y[di + r * ys + c] = (byte)B4[r * 4 + c];
                if (!Zero(coef, k * 16)) IdctAdd(coef, k * 16, f.Y, di, ys);
            }
        }
        int cx = mx * 8, cy = my * 8;
        for (int ch = 0; ch < 2; ch++)
        {
            var pl = ch == 0 ? f.U : f.V;
            PredBig(pl, cs, cx, cy, 8, uvmode, pred);
            for (int r = 0; r < 8; r++) Array.Copy(pred, r * 8, pl, (cy + r) * cs + cx, 8);
            for (int k = 0; k < 4; k++)
            {
                int ci = (16 + ch * 4 + k) * 16;
                if (!Zero(coef, ci)) IdctAdd(coef, ci, pl, (cy + (k >> 1) * 4) * cs + cx + (k & 1) * 4, cs);
            }
        }
    }

    static bool Zero(short[] c, int o) { for (int i = 0; i < 16; i++) if (c[o + i] != 0) return false; return true; }

    /// <summary>VP8 키 프레임 → 그림 (알파 255, 색차는 부드럽게 늘림)</summary>
    public static Argb Decode(byte[] b, int off, int len) => ToArgb(DecodePlanes(b, off, len));

    public static Argb ToArgb(Vp8Planes f)
    {
        int w = f.W, h = f.H, cs = f.Cs, cw = (w + 1) >> 1, ch = (h + 1) >> 1;
        var o = new Argb(w, h);
        Parallel.For(0, h, y =>
        {
            // 색차 자리: 큰 그림 (2i+0.5) → 이웃 둘을 3:1 로
            int cy0 = (y - 1) >> 1, cy1 = cy0 + 1; int wy0 = (y & 1) == 1 ? 3 : 1, wy1 = 4 - wy0;
            if (cy0 < 0) cy0 = 0; if (cy1 >= ch) cy1 = ch - 1;
            for (int x = 0; x < w; x++)
            {
                int cx0 = (x - 1) >> 1, cx1 = cx0 + 1; int wx0 = (x & 1) == 1 ? 3 : 1, wx1 = 4 - wx0;
                if (cx0 < 0) cx0 = 0; if (cx1 >= cw) cx1 = cw - 1;
                int U(byte[] p) => (p[cy0 * cs + cx0] * wx0 * wy0 + p[cy0 * cs + cx1] * wx1 * wy0 + p[cy1 * cs + cx0] * wx0 * wy1 + p[cy1 * cs + cx1] * wx1 * wy1 + 8) >> 4;
                int yy = f.Y[y * f.Ys + x], u = U(f.U), v = U(f.V);
                o.P[y * w + x] = YuvToArgb(yy, u, v);
            }
        });
        return o;
    }

    // BT.601 (libwebp 와 같은 14비트 고정 소수)
    static uint YuvToArgb(int y, int u, int v)
    {
        int c = (y - 16) * 76309, d = u - 128, e = v - 128;
        int r = (c + 104597 * e + 32768) >> 16, g = (c - 25675 * d - 53279 * e + 32768) >> 16, b = (c + 132201 * d + 32768) >> 16;
        return 0xff000000u | ((uint)Clip255(r) << 16) | ((uint)Clip255(g) << 8) | (uint)Clip255(b);
    }

    // ================================================================== 만들기
    /// <summary>"화질" 0~1 → 양자화 번호 0~127 (1 에 가까울수록 작은 단계)</summary>
    public static int QIndex(double quality) => Clip((int)Math.Round(127 * Math.Pow(Math.Clamp(1 - quality, 0, 1), 0.95)), 0, 127);

    /// <summary>그림 → Y·U·V (16 배수로 늘린 곳은 가장자리 복사, 투명한 곳 색은 옆에서 끌어와 부호가 적게 들게)</summary>
    public static Vp8Planes FromArgb(Argb im)
    {
        var f = new Vp8Planes(im.W, im.H);
        int w = im.W, h = im.H, W16 = f.Ys, H16 = f.MbH * 16;
        bool alpha = im.HasAlpha();
        uint[] src = im.P;
        if (alpha)
        {
            src = (uint[])im.P.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if ((src[i] >> 24) != 0) continue;
                    src[i] = x > 0 ? src[i - 1] & 0x00ffffff : y > 0 ? src[i - w] & 0x00ffffff : 0x00808080;
                }
        }
        Parallel.For(0, H16, y =>
        {
            int sy = Math.Min(y, h - 1);
            for (int x = 0; x < W16; x++)
            {
                uint c = src[sy * w + Math.Min(x, w - 1)];
                int r = (int)((c >> 16) & 255), g = (int)((c >> 8) & 255), b = (int)(c & 255);
                f.Y[y * W16 + x] = (byte)Clip255((16839 * r + 33059 * g + 6420 * b + (16 << 16) + 32768) >> 16);
            }
        });
        int cs = f.Cs, CH = f.MbH * 8;
        Parallel.For(0, CH, cy =>
        {
            for (int cx = 0; cx < cs; cx++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int x = Math.Min(cx * 2 + dx, w - 1), y = Math.Min(cy * 2 + dy, h - 1);
                        uint c = src[y * w + x];
                        int a = alpha ? (int)(c >> 24) + 1 : 1;   // 보이는 픽셀 색을 더 따름
                        r += (int)((c >> 16) & 255) * a; g += (int)((c >> 8) & 255) * a; b += (int)(c & 255) * a; n += a;
                    }
                double rr = r / (double)n, gg = g / (double)n, bb = b / (double)n;
                f.U[cy * cs + cx] = (byte)Clip255((int)Math.Round(128 - 0.148223 * rr - 0.290993 * gg + 0.439216 * bb));
                f.V[cy * cs + cx] = (byte)Clip255((int)Math.Round(128 + 0.439216 * rr - 0.367788 * gg - 0.071427 * bb));
            }
        });
        return f;
    }

    /// <summary>비트 비용 표 (1/256 비트가 아니라 실수 비트)</summary>
    static readonly double[] Cost0 = new double[256], Cost1 = new double[256];
    static Vp8()
    {
        for (int p = 1; p < 256; p++) { Cost0[p] = -Math.Log2(p / 256.0); Cost1[p] = -Math.Log2((256 - p) / 256.0); }
        Cost0[0] = Cost1[0] = 8;
    }

    /// <summary>토큰 쓰기·세기·비용 셈 (같은 순서로 세 가지 일)</summary>
    sealed class Tok
    {
        public byte[] Probs; public Vp8BoolEnc Enc; public int[] Stats; public double Cost;
        public int Mode;   // 0 비용, 1 통계, 2 쓰기
        void P(int pi, int bit)
        {
            if (Mode == 0) Cost += bit != 0 ? Cost1[Probs[pi]] : Cost0[Probs[pi]];
            else if (Mode == 1) Stats[pi * 2 + bit]++;
            else Enc.Put(Probs[pi], bit);
        }
        void F(int prob, int bit)
        {
            if (Mode == 0) Cost += bit != 0 ? Cost1[prob] : Cost0[prob];
            else if (Mode == 2) Enc.Put(prob, bit);
        }
        static int Bi(int type, int pos, int ctx) => ((type * 8 + Bands[pos]) * 3 + ctx) * 11;
        /// <summary>zz = 지그재그 순서 양자화 계수. 돌려주는 값: first 뒤에 0 아닌 것이 있으면 1</summary>
        public int Write(int type, int ctx, short[] zz, int zo, int first)
        {
            int last = -1;
            for (int i = 15; i >= first; i--) if (zz[zo + i] != 0) { last = i; break; }
            int n = first, b = Bi(type, n, ctx);
            if (last < 0) { P(b, 0); return 0; }
            while (true)
            {
                P(b, 1);
                while (zz[zo + n] == 0) { P(b + 1, 0); n++; b = Bi(type, n, 0); }
                P(b + 1, 1);
                int v = Math.Abs((int)zz[zo + n]), nc;
                if (v == 1) { P(b + 2, 0); nc = 1; }
                else
                {
                    P(b + 2, 1); nc = 2;
                    if (v <= 4) { P(b + 3, 0); if (v == 2) P(b + 4, 0); else { P(b + 4, 1); P(b + 5, v == 4 ? 1 : 0); } }
                    else if (v <= 10)
                    {
                        P(b + 3, 1); P(b + 6, 0);
                        if (v <= 6) { P(b + 7, 0); F(159, v - 5); }
                        else { P(b + 7, 1); F(165, (v - 7) >> 1); F(145, (v - 7) & 1); }
                    }
                    else
                    {
                        P(b + 3, 1); P(b + 6, 1);
                        int cat = v <= 18 ? 0 : v <= 34 ? 1 : v <= 66 ? 2 : 3;
                        P(b + 8, cat >> 1); P(b + 9 + (cat >> 1), cat & 1);
                        int rem = v - (3 + (8 << cat)); var tab = Cats[cat];
                        for (int i = 0; i < tab.Length; i++) F(tab[i], (rem >> (tab.Length - 1 - i)) & 1);
                    }
                }
                F(128, zz[zo + n] < 0 ? 1 : 0);
                n++;
                if (n == 16) return 1;
                b = Bi(type, n, nc);
                if (n > last) { P(b, 0); return 1; }
            }
        }
    }

    /// <summary>매크로블록 하나의 결정 (모드·지그재그 양자화 계수)</summary>
    sealed class Mb
    {
        public int YMode, UvMode; public int[] BModes = new int[16];
        public short[] Zz = new short[25 * 16];   // 0~15 Y, 16~19 U, 20~23 V, 24 Y2
        public bool Skip;
    }

    /// <summary>양자화 (차이 계수 → 지그재그 정수, 되돌린 값은 deq 에 자연 순서로)</summary>
    static bool Quantize(short[] c, int co, int first, int dcq, int acq, double dcBias, double acBias, short[] zz, int zo, short[] deq, int dq)
    {
        bool nz = false;
        for (int n = 0; n < 16; n++)
        {
            int k = Zigzag[n];
            if (n < first) { zz[zo + n] = 0; deq[dq + k] = 0; continue; }
            int step = n == 0 ? dcq : acq, v = c[co + k], a = Math.Abs(v);
            int qv = (int)((a + step * (n == 0 ? dcBias : acBias)) / step);
            if (qv > 2048) qv = 2048;
            if (v < 0) qv = -qv;
            zz[zo + n] = (short)qv; deq[dq + k] = (short)(qv * step);
            if (qv != 0) nz = true;
        }
        return nz;
    }

    static long Sse(byte[] a, int ai, int astr, byte[] b, int bi, int bstr, int w, int h)
    {
        long s = 0;
        for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) { int d = a[ai + r * astr + c] - b[bi + r * bstr + c]; s += d * d; }
        return s;
    }

    /// <summary>그림 → VP8 키 프레임 (RIFF 없이 프레임 데이터만)</summary>
    public static byte[] Encode(Argb im, double quality) => EncodePlanes(FromArgb(im), QIndex(quality), out _);

    /// <summary>Y·U·V → VP8 키 프레임. recon = 디코더가 보게 될 그림 (필터까지)</summary>
    public static byte[] EncodePlanes(Vp8Planes src, int qi, out Vp8Planes recon)
    {
        int mbw = src.MbW, mbh = src.MbH, ys = src.Ys, cs = src.Cs;
        var q = new Quant(qi, 0, 0, 0, 0, 0);
        var rec = new Vp8Planes(src.W, src.H);
        var mbs = new Mb[mbw * mbh];
        double lambda = 0.11 * q.Y1Ac * q.Y1Ac + 1;
        const double acBias = 0.36, dcBias = 0.45;
        var probs = Vp8Tab.CoefDefault;
        var tok = new Tok { Probs = probs, Mode = 0 };
        var topB = new int[mbw * 4]; var topNz = new int[mbw]; var topNzDc = new int[mbw];
        var pred = new byte[256]; var predUv = new byte[64]; var predV = new byte[64];
        var coefs = new short[25 * 16]; var deq = new short[25 * 16];
        var recI16 = new byte[256];
        var zz16 = new short[25 * 16]; var zz4 = new short[16 * 16];
        int[] A = new int[9], L = new int[4], B4 = new int[16], E = new int[9], diff = new int[16], dcs = new int[16];
        var bestB = new short[16]; var bestZ = new short[16]; var tmpC = new short[16]; var tmpZ = new short[16]; var tmpD = new short[16];
        var blk = new byte[16]; var bestBlk = new byte[16];
        var modes4 = new int[16]; var leftB = new int[4]; var tb = new int[4]; var lb = new int[4];

        for (int my = 0; my < mbh; my++)
        {
            Array.Clear(leftB); int leftNz = 0, leftNzDc = 0;
            for (int mx = 0; mx < mbw; mx++)
            {
                var mb = new Mb(); mbs[my * mbw + mx] = mb;
                int x0 = mx * 16, y0 = my * 16, yo = y0 * ys + x0;
                // ---------- 16×16: 예측이 가장 가까운 모드 → 끝까지 부호화
                int m16 = 0; double best = double.MaxValue;
                for (int m = 0; m < 4; m++)
                {
                    PredBig(rec.Y, ys, x0, y0, 16, m, pred);
                    double j = Sse(src.Y, yo, ys, pred, 0, 16, 16, 16) + lambda * ModeCostY(m);
                    if (j < best) { best = j; m16 = m; }
                }
                PredBig(rec.Y, ys, x0, y0, 16, m16, pred);
                for (int k = 0; k < 16; k++)
                {
                    int bx = (k & 3) * 4, by = (k >> 2) * 4;
                    for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) diff[r * 4 + c] = src.Y[yo + (by + r) * ys + bx + c] - pred[(by + r) * 16 + bx + c];
                    Fdct(diff, coefs, k * 16);
                    dcs[k] = coefs[k * 16];
                }
                Fwht(dcs, coefs, 24 * 16);
                Array.Clear(zz16);
                bool y2nz = Quantize(coefs, 24 * 16, 0, q.Y2Dc, q.Y2Ac, dcBias, dcBias, zz16, 24 * 16, deq, 24 * 16);
                for (int k = 0; k < 16; k++) Quantize(coefs, k * 16, 1, q.Y1Dc, q.Y1Ac, dcBias, acBias, zz16, k * 16, deq, k * 16);
                InvWht(deq, 24 * 16, deq);
                Array.Copy(pred, recI16, 256);
                for (int k = 0; k < 16; k++) if (!Zero(deq, k * 16)) IdctAdd(deq, k * 16, recI16, (k >> 2) * 64 + (k & 3) * 4, 16);
                double d16 = Sse(src.Y, yo, ys, recI16, 0, 16, 16, 16);
                tok.Mode = 0; tok.Cost = 0;
                {
                    int tnz = topNz[mx], lnz = leftNz;
                    tok.Write(1, topNzDc[mx] + leftNzDc, zz16, 24 * 16, 0);
                    for (int y = 0; y < 4; y++)
                    {
                        int l = (lnz >> y) & 1;
                        for (int x = 0; x < 4; x++) { int t = (tnz >> x) & 1; int fl = tok.Write(0, l + t, zz16, (y * 4 + x) * 16, 1); l = fl; tnz = (tnz & ~(1 << x)) | (fl << x); }
                    }
                }
                double j16 = d16 + lambda * (tok.Cost + ModeCostY(m16));

                // ---------- 4×4: 블록마다 10가지 모드 중 비용이 가장 적은 것
                double j4 = lambda * Cost0[KfYProb[0]];   // B_PRED 고르는 값
                bool i4ok = true;
                {
                    int tnz = topNz[mx], lnz = leftNz;
                    for (int i = 0; i < 4; i++) { tb[i] = topB[mx * 4 + i]; lb[i] = leftB[i]; }
                    for (int k = 0; k < 16 && i4ok; k++)
                    {
                        int bx = k & 3, by = k >> 2;
                        int di = yo + by * 4 * ys + bx * 4;
                        Edges(rec.Y, ys, mx, my, bx, by, A, L);
                        int ctxA = tb[bx], ctxL = lb[by];
                        int t = (tnz >> bx) & 1, l = (lnz >> by) & 1;
                        double bj = double.MaxValue; int bm = 0, bfl = 0;
                        for (int m = 0; m < 10; m++)
                        {
                            Pred4(A, L, m, B4, E);
                            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) { blk[r * 4 + c] = (byte)B4[r * 4 + c]; diff[r * 4 + c] = src.Y[di + r * ys + c] - B4[r * 4 + c]; }
                            Fdct(diff, tmpC, 0);
                            bool nz = Quantize(tmpC, 0, 0, q.Y1Dc, q.Y1Ac, dcBias, acBias, tmpZ, 0, tmpD, 0);
                            if (nz) IdctAdd(tmpD, 0, blk, 0, 4);
                            double dd = Sse(src.Y, di, ys, blk, 0, 4, 4, 4);
                            tok.Mode = 0; tok.Cost = 0;
                            int fl = tok.Write(3, l + t, tmpZ, 0, 0);
                            double jj = dd + lambda * (tok.Cost + BModeCost(ctxA, ctxL, m));
                            if (jj < bj) { bj = jj; bm = m; bfl = fl; Array.Copy(tmpZ, bestZ, 16); Array.Copy(blk, bestBlk, 16); }
                        }
                        j4 += bj;
                        if (j4 > j16) { i4ok = false; break; }
                        modes4[k] = bm; Array.Copy(bestZ, 0, zz4, k * 16, 16);
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) rec.Y[di + r * ys + c] = bestBlk[r * 4 + c];
                        tb[bx] = bm; lb[by] = bm;
                        tnz = (tnz & ~(1 << bx)) | (bfl << bx); lnz = (lnz & ~(1 << by)) | (bfl << by);
                    }
                }
                bool useI4 = i4ok && j4 < j16;
                Array.Clear(mb.Zz);
                if (useI4)
                {
                    mb.YMode = BP;
                    for (int k = 0; k < 16; k++) { mb.BModes[k] = modes4[k]; topB[mx * 4 + (k & 3)] = modes4[k]; leftB[k >> 2] = modes4[k]; }
                    Array.Copy(zz4, mb.Zz, 256);
                }
                else
                {
                    mb.YMode = m16;
                    for (int r = 0; r < 16; r++) Array.Copy(recI16, r * 16, rec.Y, yo + r * ys, 16);
                    int bmc = BOfY[m16];
                    for (int i = 0; i < 4; i++) { topB[mx * 4 + i] = bmc; leftB[i] = bmc; }
                    Array.Copy(zz16, mb.Zz, 25 * 16);
                }
                // ---------- 색차
                int cx = mx * 8, cy = my * 8, co = cy * cs + cx;
                int muv = 0; best = double.MaxValue;
                for (int m = 0; m < 4; m++)
                {
                    PredBig(rec.U, cs, cx, cy, 8, m, predUv); PredBig(rec.V, cs, cx, cy, 8, m, predV);
                    double j = Sse(src.U, co, cs, predUv, 0, 8, 8, 8) + Sse(src.V, co, cs, predV, 0, 8, 8, 8) + lambda * ModeCostUv(m);
                    if (j < best) { best = j; muv = m; }
                }
                mb.UvMode = muv;
                for (int ch = 0; ch < 2; ch++)
                {
                    var sp = ch == 0 ? src.U : src.V; var rp = ch == 0 ? rec.U : rec.V;
                    PredBig(rp, cs, cx, cy, 8, muv, predUv);
                    for (int r = 0; r < 8; r++) Array.Copy(predUv, r * 8, rp, co + r * cs, 8);
                    for (int k = 0; k < 4; k++)
                    {
                        int bx = (k & 1) * 4, by = (k >> 1) * 4, bi = 16 + ch * 4 + k;
                        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) diff[r * 4 + c] = sp[co + (by + r) * cs + bx + c] - predUv[(by + r) * 8 + bx + c];
                        Fdct(diff, tmpC, 0);
                        if (Quantize(tmpC, 0, 0, q.UvDc, q.UvAc, dcBias, dcBias, mb.Zz, bi * 16, tmpD, 0)) IdctAdd(tmpD, 0, rp, co + by * cs + bx, cs);
                    }
                }
                // ---------- 건너뛰기·문맥 갱신 (디코더와 같게)
                bool anyNz = false;
                for (int i = 0; i < 25 * 16 && !anyNz; i++) if (mb.Zz[i] != 0) anyNz = true;
                mb.Skip = !anyNz;
                UpdateCtx(mb, ref topNz[mx], ref leftNz, ref topNzDc[mx], ref leftNzDc);
            }
        }

        // ---------- 루프 필터 세기: 몇 가지를 실제로 걸어 보고 원본과 가장 가까운 것
        int baseLv = Clip((int)Math.Round(q.Y1Ac * 0.35), 0, 63);
        var levels = new[] { 0, baseLv / 2, baseLv, Math.Min(63, baseLv * 3 / 2), Math.Min(63, baseLv * 2) }.Distinct().ToArray();
        int bestLv = 0; long bestE = long.MaxValue; Vp8Planes bestRec = rec;
        foreach (int lv in levels)
        {
            var tr = lv == 0 ? rec : rec.Clone();
            if (lv > 0) LoopFilter(tr, FilterMap(mbs, mbw, mbh, lv), false);
            long e = 0;
            for (int y = 0; y < src.H; y++) e += Sse(src.Y, y * ys, ys, tr.Y, y * ys, ys, src.W, 1);
            if (e < bestE) { bestE = e; bestLv = lv; bestRec = tr; }
        }
        recon = bestRec;

        // ---------- 계수 확률: 실제 통계로 다시 계산 (갱신 비용보다 아낄 때만)
        var stats = new int[1056 * 2];
        tok.Mode = 1; tok.Stats = stats;
        WalkTokens(mbs, mbw, mbh, tok);
        var newProbs = (byte[])Vp8Tab.CoefDefault.Clone();
        var upd = new bool[1056];
        for (int i = 0; i < 1056; i++)
        {
            int n0 = stats[i * 2], n1 = stats[i * 2 + 1];
            if (n0 + n1 == 0) continue;
            int np = Clip((int)Math.Round(256.0 * n0 / (n0 + n1)), 1, 255);
            int op = Vp8Tab.CoefDefault[i];
            double save = n0 * (Cost0[op] - Cost0[np]) + n1 * (Cost1[op] - Cost1[np]);
            double cost = 8 + Cost1[Vp8Tab.CoefUpdate[i]] - Cost0[Vp8Tab.CoefUpdate[i]];
            if (save > cost) { newProbs[i] = (byte)np; upd[i] = true; }
        }
        int nSkip = mbs.Count(m => m.Skip);
        int skipP = Clip((int)Math.Round(256.0 * (mbs.Length - nSkip) / mbs.Length), 1, 255);

        // ---------- 첫 파티션: 머리 + 모드
        var e1 = new Vp8BoolEnc();
        e1.Lit(0, 1); e1.Lit(0, 1);            // 색 공간, 자르기 필요
        e1.Lit(0, 1);                           // 구역 없음
        e1.Lit(0, 1); e1.Lit(bestLv, 6); e1.Lit(0, 3);   // 보통 필터, 세기, 선명도 0
        e1.Lit(0, 1);                           // 필터 차이값 없음
        e1.Lit(0, 2);                           // 토큰 파티션 1개
        e1.Lit(qi, 7); for (int i = 0; i < 5; i++) e1.Lit(0, 1);
        e1.Lit(0, 1);                           // 확률 되돌리기
        for (int i = 0; i < 1056; i++) { e1.Put(Vp8Tab.CoefUpdate[i], upd[i] ? 1 : 0); if (upd[i]) e1.Lit(newProbs[i], 8); }
        e1.Lit(1, 1); e1.Lit(skipP, 8);
        {
            var tbm = new int[mbw * 4]; var lbm = new int[4];
            for (int my = 0; my < mbh; my++)
            {
                Array.Clear(lbm);
                for (int mx = 0; mx < mbw; mx++)
                {
                    var mb = mbs[my * mbw + mx];
                    e1.Put(skipP, mb.Skip ? 1 : 0);
                    e1.Tree(KfYTree, KfYProb, mb.YMode);
                    if (mb.YMode == BP)
                        for (int k = 0; k < 16; k++)
                        {
                            int m = mb.BModes[k];
                            e1.Tree(BTree, Vp8Tab.KfBmode, m, (tbm[mx * 4 + (k & 3)] * 10 + lbm[k >> 2]) * 9);
                            tbm[mx * 4 + (k & 3)] = m; lbm[k >> 2] = m;
                        }
                    else { int bm = BOfY[mb.YMode]; for (int i = 0; i < 4; i++) { tbm[mx * 4 + i] = bm; lbm[i] = bm; } }
                    e1.Tree(UvTree, UvProb, mb.UvMode);
                }
            }
        }
        var first = e1.Finish();
        // ---------- 토큰 파티션
        var e2 = new Vp8BoolEnc();
        tok.Mode = 2; tok.Enc = e2; tok.Probs = newProbs;
        WalkTokens(mbs, mbw, mbh, tok);
        var toks = e2.Finish();

        var o = new byte[10 + first.Length + toks.Length];
        int tag = (first.Length << 5) | (1 << 4);   // 키 프레임, 판 0, 보임
        o[0] = (byte)tag; o[1] = (byte)(tag >> 8); o[2] = (byte)(tag >> 16);
        o[3] = 0x9d; o[4] = 0x01; o[5] = 0x2a;
        o[6] = (byte)src.W; o[7] = (byte)(src.W >> 8); o[8] = (byte)src.H; o[9] = (byte)(src.H >> 8);
        Array.Copy(first, 0, o, 10, first.Length);
        Array.Copy(toks, 0, o, 10 + first.Length, toks.Length);
        return o;
    }

    static double ModeCostY(int m)
    {
        double c = 0; foreach (var (node, bit) in Path(KfYTree, m)) c += bit != 0 ? Cost1[KfYProb[node >> 1]] : Cost0[KfYProb[node >> 1]];
        return c;
    }
    static double ModeCostUv(int m)
    {
        double c = 0; foreach (var (node, bit) in Path(UvTree, m)) c += bit != 0 ? Cost1[UvProb[node >> 1]] : Cost0[UvProb[node >> 1]];
        return c;
    }
    static double BModeCost(int a, int l, int m)
    {
        double c = 0; int off = (a * 10 + l) * 9;
        foreach (var (node, bit) in Path(BTree, m)) { int p = Vp8Tab.KfBmode[off + (node >> 1)]; c += bit != 0 ? Cost1[p] : Cost0[p]; }
        return c;
    }

    /// <summary>디코더와 같은 규칙으로 0 아닌 계수 문맥 갱신</summary>
    static void UpdateCtx(Mb mb, ref int top, ref int left, ref int topDc, ref int leftDc)
    {
        if (mb.Skip) { top = 0; left = 0; if (mb.YMode != BP) { topDc = 0; leftDc = 0; } return; }
        int first = mb.YMode != BP ? 1 : 0;
        if (mb.YMode != BP) { int f = NzFrom(mb.Zz, 24 * 16, 0); topDc = f; leftDc = f; }
        // 다음 블록 문맥: 위 = 맨 아래 줄 블록, 왼쪽 = 맨 오른쪽 열 블록
        int t = 0, l = 0;
        for (int x = 0; x < 4; x++) t |= NzFrom(mb.Zz, (12 + x) * 16, first) << x;
        for (int y = 0; y < 4; y++) l |= NzFrom(mb.Zz, (y * 4 + 3) * 16, first) << y;
        for (int ch = 0; ch < 2; ch++)
        {
            for (int x = 0; x < 2; x++) t |= NzFrom(mb.Zz, (16 + ch * 4 + 2 + x) * 16, 0) << (4 + ch * 2 + x);
            for (int y = 0; y < 2; y++) l |= NzFrom(mb.Zz, (16 + ch * 4 + y * 2 + 1) * 16, 0) << (4 + ch * 2 + y);
        }
        top = t; left = l;
    }
    static int NzFrom(short[] zz, int o, int first) { for (int i = first; i < 16; i++) if (zz[o + i] != 0) return 1; return 0; }

    /// <summary>모든 매크로블록의 토큰을 디코더와 같은 문맥 순서로 (세기·쓰기)</summary>
    static void WalkTokens(Mb[] mbs, int mbw, int mbh, Tok tok)
    {
        var topNz = new int[mbw]; var topNzDc = new int[mbw];
        for (int my = 0; my < mbh; my++)
        {
            int leftNz = 0, leftNzDc = 0;
            for (int mx = 0; mx < mbw; mx++)
            {
                var mb = mbs[my * mbw + mx];
                if (!mb.Skip)
                {
                    int first = 1, type = 0;
                    if (mb.YMode != BP) tok.Write(1, topNzDc[mx] + leftNzDc, mb.Zz, 24 * 16, 0);
                    else { first = 0; type = 3; }
                    int tnz = topNz[mx], lnz = leftNz;
                    for (int y = 0; y < 4; y++)
                    {
                        int l = (lnz >> y) & 1;
                        for (int x = 0; x < 4; x++) { int t = (tnz >> x) & 1; int fl = tok.Write(type, l + t, mb.Zz, (y * 4 + x) * 16, first); l = fl; tnz = (tnz & ~(1 << x)) | (fl << x); }
                    }
                    int ctop = topNz[mx] >> 4, cleft = leftNz >> 4;
                    for (int ch = 0; ch < 2; ch++)
                        for (int y = 0; y < 2; y++)
                        {
                            int l = (cleft >> (ch * 2 + y)) & 1;
                            for (int x = 0; x < 2; x++)
                            {
                                int t = (ctop >> (ch * 2 + x)) & 1;
                                int fl = tok.Write(2, l + t, mb.Zz, (16 + ch * 4 + y * 2 + x) * 16, 0);
                                l = fl; ctop = (ctop & ~(1 << (ch * 2 + x))) | (fl << (ch * 2 + x));
                            }
                        }
                }
                UpdateCtx(mb, ref topNz[mx], ref leftNz, ref topNzDc[mx], ref leftNzDc);
            }
        }
    }

    static FInfo[] FilterMap(Mb[] mbs, int mbw, int mbh, int level)
    {
        var o = new FInfo[mbs.Length];
        for (int i = 0; i < mbs.Length; i++)
        {
            bool i4 = mbs[i].YMode == BP;
            o[i] = FilterInfo(level, 0, i4);
            o[i].Inner = i4 || !mbs[i].Skip;
        }
        return o;
    }

    /// <summary>PSNR (Y·U·V 를 4:1:1 무게로, 두 그림 크기는 같아야 함)</summary>
    public static double Psnr(Argb a, Argb b)
    {
        double s = 0; long n = 0;
        for (int i = 0; i < a.P.Length; i++)
        {
            uint x = a.P[i], y = b.P[i];
            for (int sh = 0; sh < 24; sh += 8) { int d = (int)((x >> sh) & 255) - (int)((y >> sh) & 255); s += d * d; n++; }
        }
        return s == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * n / s);
    }
}
