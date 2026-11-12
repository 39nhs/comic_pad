// 픽셀아트 도우미 — 웹 판 app/js/core/pixel.js
// 디더 무늬(Bayer), 팔레트(가까운 색·색 수 줄이기·명암 단계), 팔레트 파일(.gpl/.hex)
// doc.Extra["pixel"] = { art: 가까운 픽셀로 보기, lock: 팔레트 색으로만 칠하기, palette: ["#rrggbb", …] | null }
// 다른 모듈(채우기 등)이 쓰는 공개 도우미: BayerAt, DitherMask, NearestColor, SnapToPalette
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public static class PixelArt
{
    // ---------------------------------------------------------------------
    // 디더 (Bayer 행렬)
    // ---------------------------------------------------------------------
    static readonly int[,] B2 = { { 0, 2 }, { 3, 1 } };
    static readonly int[,] B4 = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };

    /// <summary>(x, y) 칸의 문턱값 0~1 (이 값보다 농도가 크면 칠함)</summary>
    public static double BayerAt(int x, int y, int n = 4)
    {
        if (n != 2) n = 4;
        var m = n == 2 ? B2 : B4;
        return (m[((y % n) + n) % n, ((x % n) + n) % n] + 0.5) / (n * n);
    }

    /// <summary>
    /// 디더 무늬로 칸을 비움 (destination-in): buf 는 문서 영역 r 의 픽셀. 문서 좌표 기준이라 획을 겹쳐도 무늬가 어긋나지 않음.
    /// level 0~1 (1 이상·0 이하면 그대로)
    /// </summary>
    public static void DitherMask(uint[] buf, Rect r, double level, int n = 4)
    {
        if (!(level > 0 && level < 1)) return;
        for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
                if (!(level > BayerAt(r.X + x, r.Y + y, n) - 1e-6)) buf[y * r.W + x] = 0;
    }

    // ---------------------------------------------------------------------
    // 팔레트
    // ---------------------------------------------------------------------
    public static string ToHex(int r, int g, int b) => $"#{r:x2}{g:x2}{b:x2}";
    public static int[] ParseHex(string h)
    {
        var s = (h ?? "").Replace("#", "");
        if (s.Length == 3) s = string.Concat(s.Select(c => $"{c}{c}"));
        int v = s.Length >= 6 && int.TryParse(s[..6], System.Globalization.NumberStyles.HexNumber, null, out var q) ? q : 0;
        return new[] { (v >> 16) & 255, (v >> 8) & 255, v & 255 };
    }

    public static JsonObject PixelOf(Document d, bool create = false)
    {
        if (d == null) return null;
        if (d.Extra["pixel"] is JsonObject p) return p;
        if (!create) return null;
        p = new JsonObject { ["art"] = false, ["lock"] = false, ["palette"] = null };
        d.Extra["pixel"] = p;
        return p;
    }
    public static bool IsArt(Document d) => J.T(PixelOf(d)?["art"]);
    public static bool IsLocked(Document d) => J.T(PixelOf(d)?["lock"]);

    /// <summary>앱 팔레트 (색 패널의 설정 palette)</summary>
    public static List<string> AppPalette() => (App.Settings["palette"] as JsonArray)?.Select(J.S).Where(s => s != null).ToList() ?? new();

    /// <summary>이 작품이 쓰는 팔레트 이름표 (작품 팔레트가 없으면 앱 팔레트)</summary>
    public static List<string> PaletteHex(Document d)
    {
        var p = (PixelOf(d)?["palette"] as JsonArray)?.Select(J.S).Where(s => s != null).ToList();
        return p != null && p.Count > 0 ? p : AppPalette();
    }
    public static List<int[]> PaletteOf(Document d) => PaletteHex(d).Select(ParseHex).ToList();

    public static double Lum(int[] c) => c[0] * 0.299 + c[1] * 0.587 + c[2] * 0.114;

    /// <summary>가까운 색 (사람 눈에 맞춘 가중 거리) → 팔레트 번호</summary>
    public static int NearestIndex(int r, int g, int b, List<int[]> pal)
    {
        int best = 0; double bd = double.MaxValue;
        for (int i = 0; i < pal.Count; i++)
        {
            var c = pal[i];
            double rm = (r + c[0]) / 2.0;
            int dr = r - c[0], dg = g - c[1], db = b - c[2];
            double dd = (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
            if (dd < bd) { bd = dd; best = i; }
        }
        return best;
    }

    /// <summary>불투명 색(BGRA)을 팔레트의 가까운 색으로 (팔레트가 비면 그대로)</summary>
    public static uint NearestColor(uint c, List<int[]> pal)
    {
        if (pal == null || pal.Count == 0) return c;
        uint u = Px.Unpremul(c);
        var p = pal[NearestIndex((int)((u >> 16) & 255), (int)((u >> 8) & 255), (int)(u & 255), pal)];
        return Px.Premul(Px.Pack(u >> 24, (uint)p[0], (uint)p[1], (uint)p[2]));
    }

    /// <summary>영역 픽셀을 팔레트 색으로 (알파는 반 넘으면 불투명, 아니면 투명 = 계단 없는 픽셀)</summary>
    public static void SnapToPalette(uint[] buf, List<int[]> pal, bool binaryAlpha = true, Func<int, bool> only = null)
    {
        if (pal == null || pal.Count == 0) return;
        var cache = new Dictionary<int, int>();
        for (int i = 0; i < buf.Length; i++)
        {
            uint c = buf[i];
            if (c == 0 || (only != null && !only(i))) continue;
            uint u = Px.Unpremul(c), a = u >> 24;
            if (binaryAlpha) { if (a < 128) { buf[i] = 0; continue; } a = 255; }
            int key = (int)(u & 0xFFFFFF);
            if (!cache.TryGetValue(key, out int k)) { k = NearestIndex((int)((u >> 16) & 255), (int)((u >> 8) & 255), (int)(u & 255), pal); cache[key] = k; }
            var p = pal[k];
            buf[i] = Px.Premul(Px.Pack(a, (uint)p[0], (uint)p[1], (uint)p[2]));
        }
    }

    /// <summary>미디언 컷: 그림 색을 n 가지로 대표 (투명 픽셀은 뺌). buf 는 미리 곱한 BGRA</summary>
    public static List<int[]> MedianCut(uint[] buf, int n)
    {
        var px = new List<int[]>();
        int step = Math.Max(1, buf.Length / 200000);     // 큰 그림은 골고루 뽑아서
        for (int i = 0; i < buf.Length; i += step)
        {
            uint u = Px.Unpremul(buf[i]);
            if ((u >> 24) > 127) px.Add(new[] { (int)((u >> 16) & 255), (int)((u >> 8) & 255), (int)(u & 255) });
        }
        if (px.Count == 0) return new();
        var boxes = new List<List<int[]>> { px };
        while (boxes.Count < n)
        {
            // 가장 넓게 퍼진 상자를 가장 넓은 채널로 반 나누기
            int bi = -1, bw = -1, bc = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                if (b.Count < 2) continue;
                for (int c = 0; c < 3; c++)
                {
                    int lo = 255, hi = 0;
                    foreach (var p in b) { if (p[c] < lo) lo = p[c]; if (p[c] > hi) hi = p[c]; }
                    if (hi - lo > bw) { bw = hi - lo; bi = i; bc = c; }
                }
            }
            if (bi < 0 || bw <= 0) break;
            int ch = bc;
            var sorted = boxes[bi].OrderBy(p => p[ch]).ToList();
            int mid = sorted.Count >> 1;
            boxes.RemoveAt(bi);
            boxes.Insert(bi, sorted.GetRange(mid, sorted.Count - mid));
            boxes.Insert(bi, sorted.GetRange(0, mid));
        }
        return boxes.Select(b => { var s = new double[3]; foreach (var p in b) for (int c = 0; c < 3; c++) s[c] += p[c]; return s.Select(v => (int)Math.Round(v / b.Count)).ToArray(); }).ToList();
    }

    /// <summary>그림(미리 곱한 BGRA, w×h)을 팔레트 색으로. dither: "none" | "bayer2" | "bayer4" | "fs" (Floyd–Steinberg)</summary>
    public static void QuantizeImage(uint[] buf, int w, int h, List<int[]> pal, string dither = "none", double strength = 1)
    {
        if (pal.Count == 0) return;
        uint Out(int[] c) => Px.Pack(255, (uint)c[0], (uint)c[1], (uint)c[2]);
        if (dither == "fs")
        {
            var err = new float[w * h * 3];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    uint u = Px.Unpremul(buf[i]);
                    if ((u >> 24) < 128) { buf[i] = 0; continue; }
                    double r = ((u >> 16) & 255) + err[i * 3], g = ((u >> 8) & 255) + err[i * 3 + 1], b = (u & 255) + err[i * 3 + 2];
                    var c = pal[NearestIndex((int)Math.Clamp(r, 0, 255), (int)Math.Clamp(g, 0, 255), (int)Math.Clamp(b, 0, 255), pal)];
                    buf[i] = Out(c);
                    double er = (r - c[0]) * strength, eg = (g - c[1]) * strength, eb = (b - c[2]) * strength;
                    void Spread(int xx, int yy, double k) { if (xx < 0 || xx >= w || yy >= h) return; int j = (yy * w + xx) * 3; err[j] += (float)(er * k); err[j + 1] += (float)(eg * k); err[j + 2] += (float)(eb * k); }
                    Spread(x + 1, y, 7 / 16.0); Spread(x - 1, y + 1, 3 / 16.0); Spread(x, y + 1, 5 / 16.0); Spread(x + 1, y + 1, 1 / 16.0);
                }
            return;
        }
        int n = dither == "bayer2" ? 2 : dither == "bayer4" ? 4 : 0;
        double amp = n > 0 ? 48 * strength : 0;
        var cache = n == 0 ? new Dictionary<int, int>() : null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                uint u = Px.Unpremul(buf[i]);
                if ((u >> 24) < 128) { buf[i] = 0; continue; }
                double o = n > 0 ? (BayerAt(x, y, n) - 0.5) * amp : 0;
                int r = (int)Math.Clamp(((u >> 16) & 255) + o, 0, 255), g = (int)Math.Clamp(((u >> 8) & 255) + o, 0, 255), b = (int)Math.Clamp((u & 255) + o, 0, 255);
                int key = (r << 16) | (g << 8) | b, k;
                if (cache == null || !cache.TryGetValue(key, out k)) { k = NearestIndex(r, g, b, pal); cache?.Add(key, k); }
                buf[i] = Out(pal[k]);
            }
    }

    /// <summary>색상(0~360)·채도(0~1)</summary>
    static (double, double) HueSat(int[] c)
    {
        int r = c[0], g = c[1], b = c[2];
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
        double sat = mx > 0 ? (double)d / mx : 0, hue = 0;
        if (d > 0) { hue = mx == r ? ((double)(g - b) / d) % 6 : mx == g ? (double)(b - r) / d + 2 : (double)(r - g) / d + 4; hue = (hue * 60 + 360) % 360; }
        return (hue, sat);
    }

    /// <summary>
    /// 명암 잉크: 팔레트에서 같은 계열(비슷한 색상, 회색은 회색끼리) 색만 밝기 순으로 세운 사다리에서 한 칸 위/아래.
    /// 팔레트에 없는 색이면 null
    /// </summary>
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
        int j = Math.Max(0, Math.Min(ramp.Count - 1, at + (dir < 0 ? -1 : 1)));
        return ramp[j];
    }

    /// <summary>한 색을 다른 색으로 (허용 오차 안) — 바꾼 픽셀 수</summary>
    public static int ReplaceColor(uint[] buf, int[] from, int[] to, int tol = 0)
    {
        int n = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] == 0) continue;
            uint u = Px.Unpremul(buf[i]);
            int r = (int)((u >> 16) & 255), g = (int)((u >> 8) & 255), b = (int)(u & 255);
            if (Math.Abs(r - from[0]) <= tol && Math.Abs(g - from[1]) <= tol && Math.Abs(b - from[2]) <= tol)
            {
                buf[i] = Px.Premul(Px.Pack(u >> 24, (uint)to[0], (uint)to[1], (uint)to[2])); n++;
            }
        }
        return n;
    }

    // ---------------------------------------------------------------------
    // 팔레트 파일
    // ---------------------------------------------------------------------
    /// <summary>.gpl (GIMP/Aseprite), .hex (Lospec), .txt(Paint.NET) → ["#rrggbb", ...]</summary>
    public static List<string> ParsePaletteFile(string text, string name = "")
    {
        var o = new List<string>();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r'));
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^GIMP Palette", System.Text.RegularExpressions.RegexOptions.Multiline) || name.EndsWith(".gpl", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in lines)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^(\d{1,3})\s+(\d{1,3})\s+(\d{1,3})");
                if (m.Success) o.Add(ToHex(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)));
            }
        }
        else
        {
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith(";") || t.StartsWith("//")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(t, @"#?([0-9a-fA-F]{8}|[0-9a-fA-F]{6})\b");
                if (m.Success) o.Add("#" + m.Groups[1].Value[^6..].ToLowerInvariant());
            }
        }
        return o.Distinct().ToList();
    }

    public static string PaletteToGpl(List<string> colors, string name = "ComicPad") =>
        $"GIMP Palette\nName: {name}\nColumns: 8\n#\n" + string.Join("\n", colors.Select(c => { var p = ParseHex(c); return $"{p[0],3} {p[1],3} {p[2],3}\t{c}"; })) + "\n";
    public static string PaletteToHex(List<string> colors) => string.Join("\n", colors.Select(c => c.Replace("#", ""))) + "\n";
}
