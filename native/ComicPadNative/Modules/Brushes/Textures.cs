// 브러시 끝 모양(팁) · 종이 결 · 디더 · 팔레트 도우미
//  - 팁: 웹 makeMaskTip(128px 원형 그라데이션 + 연필결 노이즈), 이미지 팁(에셋 PNG, .abr 에서 가져온 것). 축소용 밉맵.
//  - 종이 결: 웹 core/paper.js 의 paperTile · grainMask 를 같은 노이즈로 (문서 좌표에 고정된 무늬)
//  - 디더: Bayer 2×2 / 4×4 (core/pixel.js bayerAt)
//  - 팔레트: paletteOf · nearestIndex · shadeStep (픽셀 명암 잉크, 팔레트 고정 작품)
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Brushes;

/// <summary>팁 그림: 알파(0~1) 밉맵 + (색이 있는 이미지 팁이면) 미리 곱한 색 밉맵</summary>
public sealed class TipImage
{
    public readonly List<float[]> A = new();
    public readonly List<uint[]> C;            // null = 모양(알파)만
    public readonly List<(int w, int h)> Sz = new();
    public int W => Sz[0].w;
    public int H => Sz[0].h;
    public bool HasColor => C != null;

    public TipImage(int w, int h, float[] alpha, uint[] colors = null)
    {
        A.Add(alpha); Sz.Add((w, h));
        if (colors != null) C = new List<uint[]> { colors };
        // 반씩 줄인 밉맵 (작게 찍을 때 계단·깜빡임 없이)
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var pa = A[^1]; var na = new float[nw * nh];
            uint[] pc = C?[^1], nc = C != null ? new uint[nw * nh] : null;
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1), y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                    na[y * nw + x] = (pa[y0 * w + x0] + pa[y0 * w + x1] + pa[y1 * w + x0] + pa[y1 * w + x1]) * 0.25f;
                    if (nc != null)
                    {
                        uint a = 0, r = 0, g = 0, b = 0;
                        foreach (var c in new[] { pc[y0 * w + x0], pc[y0 * w + x1], pc[y1 * w + x0], pc[y1 * w + x1] }) { a += c >> 24; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; }
                        nc[y * nw + x] = Px.Pack((a + 2) / 4, (r + 2) / 4, (g + 2) / 4, (b + 2) / 4);
                    }
                }
            A.Add(na); C?.Add(nc); Sz.Add((nw, nh));
            w = nw; h = nh;
        }
    }

    /// <summary>size 픽셀 너비로 찍을 때 쓸 밉맵 단계</summary>
    public int LevelFor(double size)
    {
        double sc = W / Math.Max(0.01, size);
        int l = 0;
        while (sc >= 2 && l + 1 < Sz.Count) { sc /= 2; l++; }
        return l;
    }

    /// <summary>양선형 보간 (u,v = 그 단계의 픽셀 좌표, 픽셀 가운데가 i+0.5 가 아니라 i) — 밖은 0</summary>
    public float Alpha(int l, double u, double v)
    {
        var (w, h) = Sz[l]; var a = A[l];
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        float A0(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? a[y * w + x] : 0f;
        float t = A0(x0, y0) + (A0(x0 + 1, y0) - A0(x0, y0)) * fx;
        float b = A0(x0, y0 + 1) + (A0(x0 + 1, y0 + 1) - A0(x0, y0 + 1)) * fx;
        return t + (b - t) * fy;
    }

    /// <summary>색 (미리 곱한 BGRA) 양선형 보간</summary>
    public uint Color(int l, double u, double v)
    {
        var (w, h) = Sz[l]; var c = C[l];
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        float fx = (float)(u - x0), fy = (float)(v - y0);
        float sa = 0, sr = 0, sg = 0, sb = 0;
        void Add(int x, int y, float k)
        {
            if (k <= 0 || (uint)x >= (uint)w || (uint)y >= (uint)h) return;
            uint p = c[y * w + x]; sa += (p >> 24) * k; sr += ((p >> 16) & 255) * k; sg += ((p >> 8) & 255) * k; sb += (p & 255) * k;
        }
        Add(x0, y0, (1 - fx) * (1 - fy)); Add(x0 + 1, y0, fx * (1 - fy)); Add(x0, y0 + 1, (1 - fx) * fy); Add(x0 + 1, y0 + 1, fx * fy);
        uint P(float f) => (uint)Math.Clamp((int)(f + 0.5f), 0, 255);
        return Px.Pack(P(sa), P(sr), P(sg), P(sb));
    }
}

public static class Tips
{
    public const int TIP = 128;
    static readonly Dictionary<string, TipImage> maskCache = new();
    static readonly Dictionary<string, (DateTime t, TipImage tip)> imageCache = new();

    /// <summary>웹의 noise(seed): 같은 무늬가 나오는 간단한 난수 (LCG)</summary>
    public static Func<double> Noise(long seed)
    {
        long s = seed * 9301 + 49297;
        return () => { s = (s * 9301 + 49297) % 233280; return s / 233280.0; };
    }

    /// <summary>원형 그라데이션 알파 (웹 makeMaskTip 의 색 띠 그대로): t = 가운데에서의 거리 / 반지름</summary>
    public static float Grad(double hardness, double t)
    {
        double hh = Math.Clamp(hardness, 0, 0.99);
        double s2 = Math.Min(1, hh + (1 - hh) * 0.5), a2 = hardness > 0.5 ? 0.6 : 0.35;
        if (t <= hh) return 1;
        if (t >= 1) return 0;
        if (t <= s2) return (float)(1 + (a2 - 1) * (t - hh) / Math.Max(1e-9, s2 - hh));
        return (float)(a2 * (1 - (t - s2) / Math.Max(1e-9, 1 - s2)));
    }

    /// <summary>makeMaskTip(hardness, grain, variant): 128px 원형 + 연필결 노이즈</summary>
    public static TipImage MaskTip(double hardness, double grain, int variant)
    {
        string key = $"{hardness:0.00}|{grain:0.00}|{variant}";
        lock (maskCache) if (maskCache.TryGetValue(key, out var hit)) return hit;
        var a = new float[TIP * TIP];
        double r = TIP / 2.0;
        var rnd = grain > 0 ? Noise(variant + 1) : null;
        for (int y = 0; y < TIP; y++)
            for (int x = 0; x < TIP; x++)
            {
                double d = Math.Sqrt((x + 0.5 - r) * (x + 0.5 - r) + (y + 0.5 - r) * (y + 0.5 - r)) / r;
                double v = Math.Round(Grad(Math.Round(hardness, 2), d) * 255);
                if (rnd != null)
                {
                    double n = rnd();
                    double keep = n > grain * 0.85 ? 1 : n / Math.Max(0.01, grain * 0.85) * 0.35;
                    v = Math.Round(v * keep);
                }
                a[y * TIP + x] = (float)(v / 255);
            }
        var tip = new TipImage(TIP, TIP, a);
        lock (maskCache) { if (maskCache.Count > 64) maskCache.Clear(); maskCache[key] = tip; }
        return tip;
    }

    /// <summary>에셋 폴더 (웹 판 data/assets 와 같은 곳)</summary>
    public static string AssetsDir => Path.Combine(App.DataDir, "assets");

    /// <summary>이미지 팁 읽기 (tipImage = 에셋 안 경로). 못 읽으면 null</summary>
    public static TipImage LoadImage(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        try
        {
            string full = Path.IsPathRooted(assetPath) ? assetPath : Path.Combine(AssetsDir, assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return null;
            var t = File.GetLastWriteTimeUtc(full);
            lock (imageCache) if (imageCache.TryGetValue(full, out var hit) && hit.t == t) return hit.tip;
            using var bmp = new Bitmap(full);
            var tip = FromBitmap(bmp);
            lock (imageCache) imageCache[full] = (t, tip);
            return tip;
        }
        catch { return null; }
    }

    public static TipImage FromBitmap(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, new Rectangle(0, 0, w, h));
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var raw = new int[w * h];
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, raw, y * w, w);
        bmp.UnlockBits(data);
        var a = new float[w * h]; var c = new uint[w * h];
        for (int i = 0; i < raw.Length; i++) { uint v = (uint)raw[i]; a[i] = (v >> 24) / 255f; c[i] = Px.Premul(v); }
        return new TipImage(w, h, a, c);
    }

    /// <summary>알파 마스크(0~255, 255 = 칠함) → 팁 (ABR 모양)</summary>
    public static TipImage FromMask(int w, int h, byte[] mask) => new(w, h, mask.Select(b => b / 255f).ToArray());

    public static void ForgetImage(string assetPath) { lock (imageCache) imageCache.Clear(); }
}

/// <summary>종이 결 (웹 core/paper.js)</summary>
public static class PaperGrain
{
    public const int SIZE = 512;
    public static readonly (string id, string label)[] Types = { ("none", "없음"), ("fine", "고운"), ("rough", "수채지"), ("canvas", "캔버스"), ("kraft", "섬유") };
    static readonly Dictionary<string, byte[]> tiles = new();     // 결 타일의 밝기 (R) 0~255
    static readonly Dictionary<string, byte[]> masks = new();     // 브러시 결 알파

    /// <summary>이어 붙여도 자국이 안 나는 값 노이즈 (0~1)</summary>
    static float[] TileNoise(int size, int cells, int seed)
    {
        var g = new float[cells * cells];
        int s = seed;
        double Rnd()
        {
            int t = s ^ (int)((uint)s >> 15);
            s = unchecked(unchecked(t * (int)2246822519u) + (int)0x9e3779b9u);
            return (uint)s / 4294967296.0;
        }
        for (int i = 0; i < g.Length; i++) g[i] = (float)Rnd();
        var o = new float[size * size];
        static double F(double t) => t * t * (3 - 2 * t);
        for (int y = 0; y < size; y++)
        {
            double gy = (double)y / size * cells; int y0 = (int)Math.Floor(gy); double ty = F(gy - y0);
            for (int x = 0; x < size; x++)
            {
                double gx = (double)x / size * cells; int x0 = (int)Math.Floor(gx); double tx = F(gx - x0);
                double a = g[(y0 % cells) * cells + x0 % cells], b = g[(y0 % cells) * cells + (x0 + 1) % cells];
                double c = g[((y0 + 1) % cells) * cells + x0 % cells], d = g[((y0 + 1) % cells) * cells + (x0 + 1) % cells];
                o[y * size + x] = (float)((a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty);
            }
        }
        return o;
    }

    static float[] Oct(params (int cells, double w, int seed)[] list)
    {
        var acc = new float[SIZE * SIZE]; double tw = 0;
        foreach (var (cells, w, seed) in list) { var n = TileNoise(SIZE, cells, seed); for (int i = 0; i < acc.Length; i++) acc[i] += (float)(n[i] * w); tw += w; }
        for (int i = 0; i < acc.Length; i++) acc[i] = (float)(acc[i] / tw);
        return acc;
    }

    /// <summary>질감 타일 밝기 (흰색 = 변화 없음). 모르는 종류면 null</summary>
    public static byte[] Tile(string type)
    {
        lock (tiles) if (tiles.TryGetValue(type ?? "", out var hit)) return hit;
        float[] v;
        if (type == "fine") v = Oct((128, 1, 11), (256, 1, 12), (32, 0.4, 13)).Select(n => (float)(1 - Math.Max(0, n - 0.35) * 0.35)).ToArray();
        else if (type == "rough") v = Oct((8, 1.2, 21), (32, 1, 22), (128, 0.8, 23), (256, 0.5, 24)).Select(n => (float)(1 - Math.Pow(Math.Max(0, n - 0.3), 1.5) * 0.7)).ToArray();
        else if (type == "canvas")
        {
            var n = Oct((64, 1, 31), (256, 0.6, 32));
            v = new float[SIZE * SIZE];
            for (int y = 0; y < SIZE; y++)
                for (int x = 0; x < SIZE; x++)
                {
                    double w = (Math.Sin((double)x / SIZE * Math.PI * 2 * 64) * Math.Sin((double)y / SIZE * Math.PI * 2 * 64) + 1) / 2;
                    v[y * SIZE + x] = (float)(1 - (w * 0.18 + n[y * SIZE + x] * 0.12));
                }
        }
        else if (type == "kraft") v = Oct((16, 1, 41), (64, 0.7, 42)).Select(n => (float)(1 - n * 0.12)).ToArray();
        else return null;
        var r = new double[SIZE * SIZE];
        for (int i = 0; i < r.Length; i++) r[i] = Math.Round(Math.Clamp(v[i], 0, 1) * 255);
        if (type == "kraft") Fibers(r);
        var o = r.Select(x => (byte)Math.Clamp((int)Math.Round(x), 0, 255)).ToArray();
        lock (tiles) tiles[type] = o;
        return o;
    }

    /// <summary>섬유 종이: 짧은 곡선을 여기저기 (이어 붙임 자리까지 감싸도록 9번) — 색 rgba(90,70,50,a) 를 R 채널로만</summary>
    static void Fibers(double[] R)
    {
        int s = 7;
        double Rnd() { s = unchecked(s * 1103515245 + 12345); return (uint)s / 4294967296.0; }
        for (int i = 0; i < 260; i++)
        {
            double x0 = Rnd() * SIZE, y0 = Rnd() * SIZE, a = Rnd() * Math.PI * 2, L = 10 + Rnd() * 40, bend = (Rnd() - 0.5) * 20;
            double alpha = 0.05 + Rnd() * 0.12, lw = 0.6 + Rnd() * 1.2;
            foreach (int ox in new[] { -SIZE, 0, SIZE })
                foreach (int oy in new[] { -SIZE, 0, SIZE })
                {
                    double sx = x0 + ox, sy = y0 + oy;
                    double cx = sx + Math.Cos(a) * L / 2 - Math.Sin(a) * bend, cy = sy + Math.Sin(a) * L / 2 + Math.Cos(a) * bend;
                    double ex = sx + Math.Cos(a) * L, ey = sy + Math.Sin(a) * L;
                    const int N = 16;
                    var px = new double[N + 1]; var py = new double[N + 1];
                    for (int k = 0; k <= N; k++) { double t = (double)k / N, it = 1 - t; px[k] = it * it * sx + 2 * it * t * cx + t * t * ex; py[k] = it * it * sy + 2 * it * t * cy + t * t * ey; }
                    double hw = lw / 2;
                    int bx0 = (int)Math.Floor(px.Min() - hw - 1), bx1 = (int)Math.Ceiling(px.Max() + hw + 1), by0 = (int)Math.Floor(py.Min() - hw - 1), by1 = (int)Math.Ceiling(py.Max() + hw + 1);
                    if (bx1 < 0 || by1 < 0 || bx0 >= SIZE || by0 >= SIZE) continue;
                    for (int y = Math.Max(0, by0); y < Math.Min(SIZE, by1); y++)
                        for (int x = Math.Max(0, bx0); x < Math.Min(SIZE, bx1); x++)
                        {
                            double qx = x + 0.5, qy = y + 0.5, best = double.MaxValue;
                            for (int k = 0; k < N; k++)
                            {
                                double dx = px[k + 1] - px[k], dy = py[k + 1] - py[k], ll = dx * dx + dy * dy;
                                double t = ll > 0 ? Math.Clamp(((qx - px[k]) * dx + (qy - py[k]) * dy) / ll, 0, 1) : 0;
                                double ddx = px[k] + dx * t - qx, ddy = py[k] + dy * t - qy;
                                best = Math.Min(best, ddx * ddx + ddy * ddy);
                            }
                            double cov = Math.Clamp(hw + 0.5 - Math.Sqrt(best), 0, 1);
                            if (cov <= 0) continue;
                            double k2 = alpha * cov;
                            R[y * SIZE + x] = R[y * SIZE + x] * (1 - k2) + 90 * k2;
                        }
                }
        }
    }

    /// <summary>브러시 종이 결: 결이 진한 곳일수록 물감이 덜 묻는 알파 (255 = 결 없음)</summary>
    public static byte[] Mask(string type, double strength)
    {
        string key = type + ":" + strength.ToString("0.00");
        lock (masks) if (masks.TryGetValue(key, out var hit)) return hit;
        var t = Tile(type);
        if (t == null) return null;
        var m = new byte[t.Length];
        double st = Math.Round(strength, 2);
        for (int i = 0; i < t.Length; i++)
        {
            double dark = Math.Min(1, (255 - t[i]) / 255.0 * 3.2);
            m[i] = (byte)Math.Clamp((int)Math.Round(255 * (1 - dark * st)), 0, 255);
        }
        lock (masks) { if (masks.Count > 32) masks.Clear(); masks[key] = m; }
        return m;
    }

    /// <summary>패턴 확대한 결 마스크를 (x,y) 픽셀에서 (양선형, 이어 붙임)</summary>
    public static float Bilinear(byte[] m, double scale, int x, int y)
    {
        double u = (x + 0.5) / scale - 0.5, v = (y + 0.5) / scale - 0.5;
        int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
        double fx = u - x0, fy = v - y0;
        int X(int a) => ((a % SIZE) + SIZE) % SIZE;
        double A(int a, int b) => m[X(b) * SIZE + X(a)];
        double t = A(x0, y0) + (A(x0 + 1, y0) - A(x0, y0)) * fx, bt = A(x0, y0 + 1) + (A(x0 + 1, y0 + 1) - A(x0, y0 + 1)) * fx;
        return (float)(t + (bt - t) * fy);
    }
    public static byte Nearest(byte[] m, double scale, int x, int y)
    {
        int ty = (((int)Math.Floor(y / scale) % SIZE) + SIZE) % SIZE, tx = (((int)Math.Floor(x / scale) % SIZE) + SIZE) % SIZE;
        return m[ty * SIZE + tx];
    }
}

/// <summary>디더 (Bayer) · 팔레트</summary>
public static class PixelArt
{
    static readonly int[][] B2 = { new[] { 0, 2 }, new[] { 3, 1 } };
    static readonly int[][] B4 = { new[] { 0, 8, 2, 10 }, new[] { 12, 4, 14, 6 }, new[] { 3, 11, 1, 9 }, new[] { 15, 7, 13, 5 } };
    /// <summary>(x, y) 칸의 문턱값 0~1 (이 값보다 농도가 크면 칠함)</summary>
    public static double BayerAt(int x, int y, int n = 4)
    {
        if (n != 2) n = 4;
        var m = n == 2 ? B2 : B4;
        return (m[((y % n) + n) % n][((x % n) + n) % n] + 0.5) / (n * n);
    }

    public static readonly string[] DefaultPalette = { "#000000", "#ffffff", "#3a3a3a", "#7a7a7a", "#bdbdbd", "#3a7bd5", "#e04646", "#f2a93b", "#f7e15a", "#4caf50", "#8e5bd6", "#f4c7b8" };

    public static int[] ParseHex(string h)
    {
        var s = (h ?? "").Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        int v = s.Length >= 6 && int.TryParse(s[..6], System.Globalization.NumberStyles.HexNumber, null, out var x) ? x : 0;
        return new[] { (v >> 16) & 255, (v >> 8) & 255, v & 255 };
    }
    public static string ToHex(int r, int g, int b) => $"#{r:x2}{g:x2}{b:x2}";

    /// <summary>이 작품이 쓰는 팔레트 (작품 팔레트가 없으면 앱 팔레트)</summary>
    public static List<int[]> PaletteOf(Document doc)
    {
        IEnumerable<string> list = null;
        if (doc?.Extra["pixel"] is JsonObject px && px["palette"] is JsonArray pa && pa.Count > 0) list = pa.Select(x => (string)x);
        else if (App.Settings["palette"] is JsonArray sp && sp.Count > 0) list = sp.Select(x => (string)x);
        list ??= DefaultPalette;
        return list.Where(x => x != null).Select(ParseHex).ToList();
    }
    public static bool PaletteLocked(Document doc) => doc?.Extra["pixel"] is JsonObject px && px["lock"] is JsonNode l && BrushPreset.Bool(l);

    public static double Lum(int[] c) => c[0] * 0.299 + c[1] * 0.587 + c[2] * 0.114;

    /// <summary>가까운 색 (사람 눈에 맞춘 가중 거리) → 팔레트 번호</summary>
    public static int NearestIndex(int r, int g, int b, List<int[]> pal)
    {
        int best = 0; double bd = double.MaxValue;
        for (int i = 0; i < pal.Count; i++)
        {
            var c = pal[i]; double rm = (r + c[0]) / 2.0;
            double dr = r - c[0], dg = g - c[1], db = b - c[2];
            double d = (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    static (double h, double s) HueSat(int[] c)
    {
        int mx = Math.Max(c[0], Math.Max(c[1], c[2])), mn = Math.Min(c[0], Math.Min(c[1], c[2])), d = mx - mn;
        double sat = mx != 0 ? (double)d / mx : 0, hue = 0;
        if (d != 0)
        {
            hue = mx == c[0] ? ((double)(c[1] - c[2]) / d) % 6 : mx == c[1] ? (double)(c[2] - c[0]) / d + 2 : (double)(c[0] - c[1]) / d + 4;
            hue = (hue * 60 + 360) % 360;
        }
        return (hue, sat);
    }

    /// <summary>명암 잉크: 팔레트에서 같은 계열 색만 밝기 순으로 세운 사다리에서 한 칸 위/아래. 팔레트에 없는 색이면 null</summary>
    public static int[] ShadeStep(int r, int g, int b, List<int[]> pal, int dir = -1)
    {
        int me = pal.FindIndex(c => c[0] == r && c[1] == g && c[2] == b);
        if (me < 0) return null;
        var (h0, s0) = HueSat(pal[me]);
        bool gray = s0 < 0.15;
        var ramp = pal.Where((c, i) =>
        {
            if (i == me) return true;
            var (hh, ss) = HueSat(c);
            if (gray) return ss < 0.15;
            if (ss < 0.15) return false;
            double dh = Math.Abs(hh - h0);
            return Math.Min(dh, 360 - dh) <= 40;
        }).OrderBy(Lum).ToList();
        int at = ramp.IndexOf(pal[me]);
        int j = Math.Clamp(at + (dir < 0 ? -1 : 1), 0, ramp.Count - 1);
        return ramp[j];
    }
}
