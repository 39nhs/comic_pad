// 용지 색 + 질감 (웹 core/paper.js): 용지 색 위에 종이 결(512px 이어 붙는 무늬)을 곱하기로 얹는다.
// doc.paper.texture = { type: none|fine|rough|canvas|kraft, strength 0~1, scale 0.25~4, over: 그림 위에도 }
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

public static class Paper
{
    public static readonly (string id, string name)[] Types = { ("none", "없음"), ("fine", "고운 종이"), ("rough", "거친 수채지"), ("canvas", "캔버스 천"), ("kraft", "섬유 종이") };
    public const int SIZE = 512;
    static readonly Dictionary<string, uint[]> cache = new();

    /// <summary>이어 붙여도 자국이 안 나는 값 노이즈 (0~1)</summary>
    static float[] TileNoise(int size, int cells, int seed)
    {
        var g = new float[cells * cells];
        int s = seed;
        double Rnd()
        {
            uint us = (uint)s;
            uint t = us ^ (us >> 15);
            uint n = unchecked(t * 2246822519u + 0x9e3779b9u);
            s = (int)n;
            return n / 4294967296.0;
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
                double a = g[(y0 % cells) * cells + (x0 % cells)], b = g[(y0 % cells) * cells + ((x0 + 1) % cells)];
                double c = g[((y0 + 1) % cells) * cells + (x0 % cells)], d = g[((y0 + 1) % cells) * cells + ((x0 + 1) % cells)];
                o[y * size + x] = (float)((a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty);
            }
        }
        return o;
    }

    static float[] Oct((int cells, double w, int seed)[] list)
    {
        var acc = new float[SIZE * SIZE]; double tw = 0;
        foreach (var (cells, w, seed) in list) { var n = TileNoise(SIZE, cells, seed); for (int i = 0; i < acc.Length; i++) acc[i] += (float)(n[i] * w); tw += w; }
        for (int i = 0; i < acc.Length; i++) acc[i] = (float)(acc[i] / tw);
        return acc;
    }

    /// <summary>질감 타일 (불투명 BGRA, 흰색 = 변화 없음). 없는 종류면 null</summary>
    public static uint[] Tile(string type)
    {
        lock (cache)
        {
            if (cache.TryGetValue(type ?? "", out var t)) return t;
            t = Make(type);
            cache[type ?? ""] = t;
            return t;
        }
    }

    static uint[] Make(string type)
    {
        float[] v;
        if (type == "fine") v = Oct(new[] { (128, 1.0, 11), (256, 1.0, 12), (32, 0.4, 13) }).Select(n => (float)(1 - Math.Max(0, n - 0.35) * 0.35)).ToArray();
        else if (type == "rough") v = Oct(new[] { (8, 1.2, 21), (32, 1.0, 22), (128, 0.8, 23), (256, 0.5, 24) }).Select(n => (float)(1 - Math.Pow(Math.Max(0, n - 0.3), 1.5) * 0.7)).ToArray();
        else if (type == "canvas")
        {
            var n = Oct(new[] { (64, 1.0, 31), (256, 0.6, 32) });
            v = new float[SIZE * SIZE];
            for (int y = 0; y < SIZE; y++) for (int xx = 0; xx < SIZE; xx++)
            {
                double w = (Math.Sin((double)xx / SIZE * Math.PI * 2 * 64) * Math.Sin((double)y / SIZE * Math.PI * 2 * 64) + 1) / 2;
                v[y * SIZE + xx] = (float)(1 - (w * 0.18 + n[y * SIZE + xx] * 0.12));
            }
        }
        else if (type == "kraft") v = Oct(new[] { (16, 1.0, 41), (64, 0.7, 42) }).Select(n => (float)(1 - n * 0.12)).ToArray();
        else return null;
        var px = new uint[SIZE * SIZE];
        for (int i = 0; i < v.Length; i++) { uint g = (uint)Math.Round(Math.Clamp(v[i], 0f, 1f) * 255, MidpointRounding.AwayFromZero); px[i] = Px.Pack(255, g, g, g); }
        if (type == "kraft") DrawFibers(px);
        return px;
    }

    /// <summary>섬유: 짧은 곡선을 여기저기 (이어 붙임 자리까지 감싸도록 9번 찍기)</summary>
    static unsafe void DrawFibers(uint[] px)
    {
        using var bmp = new Bitmap(SIZE, SIZE, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, SIZE, SIZE), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < SIZE; y++) { uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride); for (int x = 0; x < SIZE; x++) row[x] = px[y * SIZE + x]; }
        bmp.UnlockBits(bd);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int s = 7;
            double Rnd() { s = unchecked(s * 1103515245 + 12345); return (uint)s / 4294967296.0; }
            for (int i = 0; i < 260; i++)
            {
                double x0 = Rnd() * SIZE, y0 = Rnd() * SIZE, a = Rnd() * Math.PI * 2, L = 10 + Rnd() * 40, bend = (Rnd() - 0.5) * 20;
                double alpha = 0.05 + Rnd() * 0.12;
                double lw = 0.6 + Rnd() * 1.2;
                using var pen = new Pen(Color.FromArgb((int)Math.Round(alpha * 255), 90, 70, 50), (float)lw) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                foreach (int ox in new[] { -SIZE, 0, SIZE })
                    foreach (int oy in new[] { -SIZE, 0, SIZE })
                    {
                        // 2차 곡선 → 3차 베지어
                        double p0x = x0 + ox, p0y = y0 + oy;
                        double qx = x0 + ox + Math.Cos(a) * L / 2 - Math.Sin(a) * bend, qy = y0 + oy + Math.Sin(a) * L / 2 + Math.Cos(a) * bend;
                        double p2x = x0 + ox + Math.Cos(a) * L, p2y = y0 + oy + Math.Sin(a) * L;
                        g.DrawBezier(pen, (float)p0x, (float)p0y, (float)(p0x + 2.0 / 3 * (qx - p0x)), (float)(p0y + 2.0 / 3 * (qy - p0y)),
                            (float)(p2x + 2.0 / 3 * (qx - p2x)), (float)(p2y + 2.0 / 3 * (qy - p2y)), (float)p2x, (float)p2y);
                    }
            }
        }
        bd = bmp.LockBits(new Rectangle(0, 0, SIZE, SIZE), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < SIZE; y++) { uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride); for (int x = 0; x < SIZE; x++) px[y * SIZE + x] = row[x] | 0xFF000000; }
        bmp.UnlockBits(bd);
    }

    public static JsonObject Texture(Document d) => d.PaperExtra["texture"] as JsonObject;

    static bool Active(JsonObject t, out uint[] tile, out double k, Document d)
    {
        tile = null; k = 1;
        if (t == null) return false;
        string type = (string)t["type"];
        if (type == null || type == "none" || !(Svg.Num(t, "strength") > 0)) return false;
        tile = Tile(type);
        if (tile == null) return false;
        k = (d.Dpi > 0 ? d.Dpi : 350) / 350.0 * (Svg.Num(t, "scale", 1) is var sc && sc > 0 ? sc : 1);
        return true;
    }

    /// <summary>Hooks.Paper: 용지 색 + 결</summary>
    public static void Draw(uint[] buf, Rect r, Document d)
    {
        Array.Fill(buf, Px.FromHex(d.PaperColor, 0xFFFFFFFF) | 0xFF000000);
        Multiply(buf, r, d, 1);
    }

    /// <summary>Hooks.PaperOver: "칠한 그림 위에도 결" (진하기 0.7배)</summary>
    public static void Over(uint[] buf, Rect r, Document d)
    {
        var t = Texture(d);
        if (t == null || (bool?)t["over"] != true) return;
        Multiply(buf, r, d, 0.7);
    }

    /// <summary>결 무늬를 곱하기로 (globalAlpha = 진하기 × k0). 무늬는 dpi/350 × 결 크기 배율로 늘림 (부드럽게 보간)</summary>
    static void Multiply(uint[] buf, Rect r, Document d, double k0)
    {
        var t = Texture(d);
        if (!Active(t, out var tile, out double k, d)) return;
        float al = (float)Math.Min(1, Svg.Num(t, "strength") * k0);
        for (int y = 0; y < r.H; y++)
        {
            double ty = (r.Y + y + 0.5) / k - 0.5;
            int y0 = (int)Math.Floor(ty); float fy = (float)(ty - y0);
            int ya = ((y0 % SIZE) + SIZE) % SIZE, yb = (ya + 1) % SIZE;
            for (int x = 0; x < r.W; x++)
            {
                int i = y * r.W + x;
                uint dc = buf[i];
                if (dc == 0) continue;
                double tx = (r.X + x + 0.5) / k - 0.5;
                int x0 = (int)Math.Floor(tx); float fx = (float)(tx - x0);
                int xa = ((x0 % SIZE) + SIZE) % SIZE, xb = (xa + 1) % SIZE;
                uint c00 = tile[ya * SIZE + xa], c10 = tile[ya * SIZE + xb], c01 = tile[yb * SIZE + xa], c11 = tile[yb * SIZE + xb];
                float Ch(int sh) => LerpF(Lerp((c00 >> sh) & 255, (c10 >> sh) & 255, fx), Lerp((c01 >> sh) & 255, (c11 >> sh) & 255, fx), fy) / 255f;
                float tr = Ch(16), tg = Ch(8), tb = Ch(0);
                uint da = dc >> 24;
                if (da == 255)
                {
                    buf[i] = Px.Pack(255, M((dc >> 16) & 255, tr, al), M((dc >> 8) & 255, tg, al), M(dc & 255, tb, al));
                }
                else
                {
                    uint src = Px.Pack(255, (uint)(tr * 255 + 0.5f), (uint)(tg * 255 + 0.5f), (uint)(tb * 255 + 0.5f));
                    buf[i] = Px.Composite(src, dc, Core.Blend.Multiply, al);
                }
            }
        }
    }
    static float Lerp(uint a, uint b, float t) => a + (b - (float)a) * t;
    static float LerpF(float a, float b, float t) => a + (b - a) * t;
    static uint M(uint c, float t, float al) => (uint)Math.Clamp(c * (1 - al + al * t) + 0.5f, 0, 255);
}
