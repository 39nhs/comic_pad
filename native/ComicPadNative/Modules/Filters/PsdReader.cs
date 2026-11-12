// PSD/PSB 열기 — 웹 core/psdread.js 와 같은 범위:
//  - 색상 모드: 비트맵, 흑백, 인덱스, RGB, CMYK, 다중채널, 듀오톤(흑백으로), Lab / 비트 깊이 1·8·16·32 / 압축: 무압축·RLE·ZIP·ZIP+예측
//  - 레이어 이름·불투명도·합성 모드·클리핑·숨김·투명 잠금, 폴더, 레이어 마스크
//  - 레이어 효과(그림자·획·외부 광선 → ComicPad 레이어 효과), 조정 레이어(레벨·곡선·밝기/대비·색조/채도·반전·포스터화·한계값·흑백·색상 균형·그라데이션 맵·자연 채도)
//  - 단색 칠/셰이프 레이어: 그림이 비어 있으면 벡터 마스크·선 설정으로 다시 그림
//  - 텍스트 레이어: 그림은 그대로, 글자 내용/크기/색/글꼴을 meta.psdText 에 보관 (텍스트 도구에서 다시 편집용)
using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

public sealed class PsdReader
{
    static readonly Dictionary<string, string> Blend = new()
    {
        ["norm"] = "source-over", ["pass"] = "source-over", ["mul "] = "multiply", ["scrn"] = "screen", ["over"] = "overlay", ["dark"] = "darken", ["lite"] = "lighten",
        ["div "] = "color-dodge", ["idiv"] = "color-burn", ["hLit"] = "hard-light", ["sLit"] = "soft-light", ["diff"] = "difference",
        ["hue "] = "hue", ["sat "] = "saturation", ["colr"] = "color", ["lum "] = "luminosity", ["lddg"] = "lighter", ["lbrn"] = "color-burn",
        ["smud"] = "difference", ["dkCl"] = "darken", ["lgCl"] = "lighten", ["vLit"] = "hard-light", ["lLit"] = "hard-light", ["pLit"] = "hard-light", ["hMix"] = "hard-light", ["fsub"] = "difference", ["fdiv"] = "color-dodge",
    };
    // PSB 에서 길이가 8바이트인 추가 정보 키
    static readonly HashSet<string> LongKeys = new() { "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD" };
    static readonly Dictionary<int, string> ModeNames = new() { [0] = "비트맵", [1] = "흑백", [2] = "인덱스", [3] = "RGB", [4] = "CMYK", [7] = "다중채널", [8] = "듀오톤", [9] = "Lab" };

    // ---- 읽기 도구 ----
    public sealed class R
    {
        public readonly byte[] B; public long O;
        public R(byte[] b) { B = b; }
        public int U8() => B[O++];
        public int U16() { int x = BinaryPrimitives.ReadUInt16BigEndian(B.AsSpan((int)O)); O += 2; return x; }
        public int I16() { int x = BinaryPrimitives.ReadInt16BigEndian(B.AsSpan((int)O)); O += 2; return x; }
        public long U32() { long x = BinaryPrimitives.ReadUInt32BigEndian(B.AsSpan((int)O)); O += 4; return x; }
        public int I32() { int x = BinaryPrimitives.ReadInt32BigEndian(B.AsSpan((int)O)); O += 4; return x; }
        public long U64() { long hi = U32(), lo = U32(); return hi * 4294967296L + lo; }
        public double F64() { double x = BinaryPrimitives.ReadDoubleBigEndian(B.AsSpan((int)O)); O += 8; return x; }
        public string Str(int n) { var s = Encoding.Latin1.GetString(B, (int)O, n); O += n; return s; }
        public byte[] Bytes(long n) { n = Math.Max(0, Math.Min(n, B.Length - O)); var b = B.AsSpan((int)O, (int)n).ToArray(); O += n; return b; }
        public void Skip(long n) => O += n;
        public string Peek4() => O + 4 <= B.Length ? Encoding.Latin1.GetString(B, (int)O, 4) : "";
    }

    sealed class Ctx { public bool Psb; public int Depth, Mode, W, H; public byte[] Palette; public int? GlobalAngle; }

    // ---- 압축 해제 ----
    static void UnpackBits(byte[] src, int si, int sl, byte[] dst, int di, int dl)
    {
        int i = si, end = si + sl, o = 0;
        while (i < end && o < dl)
        {
            int n = src[i++]; if (n > 127) n -= 256;
            if (n >= 0) { for (int k = 0; k <= n && o < dl && i < end; k++) dst[di + o++] = src[i++]; }
            else if (n != -128) { if (i >= end) break; byte v = src[i++]; for (int k = 0; k < 1 - n && o < dl; k++) dst[di + o++] = v; }
        }
    }
    static byte[] Inflate(byte[] b, int off)
    {
        using var ms = new MemoryStream(b, off, b.Length - off);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }
    static int RowBytes(int w, int depth) => depth == 1 ? (w + 7) / 8 : w * (depth / 8);

    /// <summary>압축된 채널 → 원본 바이트 (rowBytes*h). data[0..1] = 압축 방식</summary>
    internal static byte[] RawChannelForTest(int comp, byte[] body, int w, int h, int depth) => RawChannel(comp, body, 0, w, h, new Ctx { Depth = depth, Mode = 3, W = w, H = h });
    static byte[] RawChannel(int comp, byte[] body, int bo, int w, int h, Ctx c)
    {
        int rb = RowBytes(w, c.Depth); long total = (long)rb * h;
        if (comp == 0) { var o = new byte[total]; Array.Copy(body, bo, o, 0, Math.Min(total, body.Length - bo)); return o; }
        if (comp == 1)
        {
            int cs = c.Psb ? 4 : 2;
            var o = new byte[total];
            int p = bo + cs * h;
            for (int y = 0; y < h; y++)
            {
                int n = cs == 4 ? (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(bo + y * 4)) : BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(bo + y * 2));
                UnpackBits(body, p, Math.Min(n, body.Length - p), o, y * rb, rb);
                p += n;
            }
            return o;
        }
        if (comp == 2 || comp == 3)
        {
            var raw = Inflate(body, bo);
            if (raw.Length < total) Array.Resize(ref raw, (int)total);
            if (comp == 2) return raw;
            if (c.Depth == 8) { for (int y = 0; y < h; y++) for (int x = 1; x < rb; x++) raw[y * rb + x] = (byte)(raw[y * rb + x] + raw[y * rb + x - 1]); return raw; }
            if (c.Depth == 16)
            {
                for (int y = 0; y < h; y++) for (int x = 1; x < w; x++)
                {
                    int p = y * rb + x * 2;
                    int v = (BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(p)) + BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(p - 2))) & 0xffff;
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(p), (ushort)v);
                }
                return raw;
            }
            if (c.Depth == 32)
            {
                var o = new byte[total];
                for (int y = 0; y < h; y++)
                {
                    int bs = y * rb;
                    for (int i = 1; i < rb; i++) raw[bs + i] = (byte)(raw[bs + i] + raw[bs + i - 1]);
                    for (int x = 0; x < w; x++) for (int b = 0; b < 4; b++) o[bs + x * 4 + b] = raw[bs + b * w + x];
                }
                return o;
            }
        }
        throw new InvalidDataException("지원하지 않는 압축 방식: " + comp);
    }

    /// <summary>원본 바이트 → 8비트 값 (w*h)</summary>
    static byte[] To8(byte[] raw, int ro, int w, int h, Ctx c, bool linear)
    {
        var o = new byte[w * h];
        int rb = RowBytes(w, c.Depth);
        if (c.Depth == 8) { Array.Copy(raw, ro, o, 0, Math.Min(w * h, raw.Length - ro)); return o; }
        if (c.Depth == 1) { for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) o[y * w + x] = (byte)(((raw[ro + y * rb + (x >> 3)] >> (7 - (x & 7))) & 1) != 0 ? 0 : 255); return o; }
        if (c.Depth == 16) { for (int i = 0; i < w * h; i++) o[i] = raw[ro + i * 2]; return o; }
        if (c.Depth == 32)
        {
            for (int i = 0; i < w * h; i++)
            {
                double f = Math.Clamp(BinaryPrimitives.ReadSingleBigEndian(raw.AsSpan(ro + i * 4)), 0, 1);
                o[i] = (byte)Math.Round((linear ? f : Math.Pow(f, 1 / 2.2)) * 255, MidpointRounding.AwayFromZero);
            }
            return o;
        }
        throw new InvalidDataException($"{c.Depth}비트는 지원하지 않습니다");
    }

    static byte[] DecodeChannel(byte[] data, int w, int h, Ctx c, bool linear)
    {
        if (data.Length < 2 || w <= 0 || h <= 0) return new byte[Math.Max(0, w * h)];
        int comp = (data[0] << 8) | data[1];
        return To8(RawChannel(comp, data, 2, w, h, c), 0, w, h, c, linear);
    }

    static (int, int, int) LabToRgb(double L, double a, double b)
    {
        double y = (L + 16) / 116, x = a / 500 + y, z = y - b / 200;
        double F(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116) / 7.787;
        x = 0.95047 * F(x); y = F(y); z = 1.08883 * F(z);
        double r = x * 3.2406 + y * -1.5372 + z * -0.4986, g = x * -0.9689 + y * 1.8758 + z * 0.0415, bb = x * 0.0557 + y * -0.2040 + z * 1.0570;
        int Gm(double v) => (int)Math.Round(255 * Math.Clamp(v > 0.0031308 ? 1.055 * Math.Pow(v, 1 / 2.4) - 0.055 : 12.92 * v, 0, 1), MidpointRounding.AwayFromZero);
        return (Gm(r), Gm(g), Gm(bb));
    }

    /// <summary>채널들 → 미리 곱한 픽셀 배열 (w*h)</summary>
    static uint[] ToPixels(int w, int h, Dictionary<int, byte[]> ch, Ctx c)
    {
        int n = w * h; var o = new uint[n];
        var z = new byte[n];
        byte[] c0 = ch.GetValueOrDefault(0) ?? z, c1 = ch.GetValueOrDefault(1) ?? z, c2 = ch.GetValueOrDefault(2) ?? z, c3 = ch.GetValueOrDefault(3), A = ch.GetValueOrDefault(-1);
        for (int i = 0; i < n; i++)
        {
            int r, g, b;
            switch (c.Mode)
            {
                case 3: r = c0[i]; g = c1[i]; b = c2[i]; break;
                case 4: { int k = c3 != null ? c3[i] : 255; r = c0[i] * k / 255; g = c1[i] * k / 255; b = c2[i] * k / 255; break; }
                case 2: { int v = c0[i]; r = c.Palette != null ? c.Palette[v] : v; g = c.Palette != null ? c.Palette[256 + v] : v; b = c.Palette != null ? c.Palette[512 + v] : v; break; }
                case 9: (r, g, b) = LabToRgb(c0[i] * 100 / 255.0, c1[i] - 128, c2[i] - 128); break;
                default: r = g = b = c0[i]; break;
            }
            o[i] = Raster.Pack(r, g, b, A != null ? A[i] : 255);
        }
        return o;
    }

    static int ColorChannels(int mode) => mode switch { 0 or 1 or 2 or 7 or 8 => 1, 3 or 9 => 3, 4 => 4, _ => 3 };

    // ---- 설명자 읽기 ----
    static string ReadUnicode(R r) { long n = r.U32(); var sb = new StringBuilder(); for (long i = 0; i < n; i++) sb.Append((char)r.U16()); return sb.ToString().TrimEnd('\0'); }
    static string ReadKey(R r) { long n = r.U32(); return r.Str((int)(n == 0 ? 4 : n)); }

    public static Dictionary<string, object> ReadDescriptor(R r)
    {
        ReadUnicode(r); ReadKey(r);
        long n = r.U32(); var o = new Dictionary<string, object>();
        for (long i = 0; i < n; i++) { string k = ReadKey(r); o[k.Trim()] = ReadItem(r, r.Str(4)); }
        return o;
    }

    static object ReadItem(R r, string t)
    {
        switch (t)
        {
            case "Objc": case "GlbO": return ReadDescriptor(r);
            case "VlLs": { long n = r.U32(); var a = new List<object>(); for (long i = 0; i < n; i++) a.Add(ReadItem(r, r.Str(4))); return a; }
            case "doub": return r.F64();
            case "UntF": r.Str(4); return r.F64();
            case "UnFl": { r.Str(4); long n = r.U32(); var a = new List<object>(); for (long i = 0; i < n; i++) a.Add(r.F64()); return a; }
            case "TEXT": return ReadUnicode(r);
            case "enum": ReadKey(r); return ReadKey(r);
            case "long": return (double)r.I32();
            case "comp": r.Skip(8); return 0.0;
            case "bool": return r.U8() != 0;
            case "type": case "GlbC": ReadUnicode(r); return ReadKey(r);
            case "alis": case "Pth ": r.Skip(r.U32()); return null;
            case "tdta": return r.Bytes(r.U32());
            case "obj ":
            {
                long n = r.U32();
                for (long i = 0; i < n; i++)
                {
                    string k = r.Str(4);
                    if (k == "prop") { ReadUnicode(r); ReadKey(r); ReadKey(r); }
                    else if (k == "Clss") { ReadUnicode(r); ReadKey(r); }
                    else if (k == "Enmr") { ReadUnicode(r); ReadKey(r); ReadKey(r); ReadKey(r); }
                    else if (k == "rele") { ReadUnicode(r); ReadKey(r); r.U32(); }
                    else if (k == "Idnt" || k == "indx") r.U32();
                    else if (k == "name") { ReadUnicode(r); ReadKey(r); ReadUnicode(r); }
                    else throw new InvalidDataException("ref " + k);
                }
                return null;
            }
            default: throw new InvalidDataException("descriptor type " + t);
        }
    }

    static double? D(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is double x ? x : null;
    static Dictionary<string, object> O(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;

    /// <summary>설명자 속 색(RGBC·Grsc·HSBC·CMYC, redFloat 등) → '#rrggbb'</summary>
    static string DescColor(Dictionary<string, object> c)
    {
        if (c == null) return "#000000";
        string Hx(double v) => ((int)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 255)).ToString("x2");
        double r = 0, g = 0, b = 0;
        if (c.ContainsKey("Rd") || c.ContainsKey("Grn")) { r = D(c, "Rd") ?? 0; g = D(c, "Grn") ?? 0; b = D(c, "Bl") ?? 0; }
        else if (c.ContainsKey("redFloat")) { r = (D(c, "redFloat") ?? 0) * 255; g = (D(c, "greenFloat") ?? 0) * 255; b = (D(c, "blueFloat") ?? 0) * 255; }
        else if (c.ContainsKey("Gry")) { r = g = b = 255 - (D(c, "Gry") ?? 0) * 2.55; }
        else if (c.ContainsKey("H"))
        {
            double h = ((D(c, "H") ?? 0) % 360) / 60, s2 = (D(c, "Strt") ?? 0) / 100, v = (D(c, "Brgh") ?? 0) / 100; int i = (int)Math.Floor(h); double f = h - i;
            double p2 = v * (1 - s2), q = v * (1 - s2 * f), t = v * (1 - s2 * (1 - f));
            var arr = new[] { new[] { v, t, p2 }, new[] { q, v, p2 }, new[] { p2, v, t }, new[] { p2, q, v }, new[] { t, p2, v }, new[] { v, p2, q } }[((i % 6) + 6) % 6];
            r = arr[0] * 255; g = arr[1] * 255; b = arr[2] * 255;
        }
        else if (c.ContainsKey("Cyn"))
        {
            double k = 1 - (D(c, "Blck") ?? 0) / 100;
            r = 255 * (1 - (D(c, "Cyn") ?? 0) / 100) * k; g = 255 * (1 - (D(c, "Mgnt") ?? 0) / 100) * k; b = 255 * (1 - (D(c, "Ylw") ?? 0) / 100) * k;
        }
        return "#" + Hx(r) + Hx(g) + Hx(b);
    }

    static int Rnd(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>레이어 효과 설명자(lfx2 / lmfx) → ComicPad fx</summary>
    static JsonObject ReadEffects(Dictionary<string, object> d, int? globalAngle)
    {
        if (d == null || (d.TryGetValue("masterFXSwitch", out var mf) && mf is bool mb && !mb)) return null;
        double k = (D(d, "Scl") ?? 100) / 100;
        Dictionary<string, object> Pick(string one, string many)
        {
            var list = d.TryGetValue(many, out var m) && m is List<object> lo ? lo.OfType<Dictionary<string, object>>().ToList() : (O(d, one) is { } x ? new() { x } : new());
            return list.FirstOrDefault(e => !(e.TryGetValue("enab", out var en) && en is bool eb && !eb));
        }
        var fx = new JsonObject();
        var ds = Pick("DrSh", "dropShadowMulti");
        if (ds != null)
        {
            bool uglg = !(ds.TryGetValue("uglg", out var u) && u is bool ub && !ub);
            double ang = (uglg && globalAngle != null ? globalAngle.Value : (D(ds, "lagl") ?? 120)) * Math.PI / 180;
            double dist = (D(ds, "Dstn") ?? 5) * k;
            fx["shadow"] = new JsonObject { ["on"] = true, ["dx"] = Rnd(-Math.Cos(ang) * dist), ["dy"] = Rnd(Math.Sin(ang) * dist), ["blur"] = Rnd((D(ds, "blur") ?? 5) * k), ["color"] = DescColor(O(ds, "Clr")), ["opacity"] = Math.Clamp((D(ds, "Opct") ?? 75) / 100, 0, 1) };
        }
        var st = Pick("FrFX", "frameFXMulti");
        if (st != null)
        {
            double size = (D(st, "Sz") ?? 3) * k;
            string pos = st.TryGetValue("Styl", out var ps) ? ps as string : null;
            fx["stroke"] = new JsonObject { ["on"] = true, ["size"] = Math.Max(1, Rnd(pos == "InsF" || pos == "CtrF" ? size / 2 : size)), ["color"] = DescColor(O(st, "Clr")) };
        }
        var og = Pick("OrGl", "outerGlowMulti");
        if (og != null) fx["glow"] = new JsonObject { ["on"] = true, ["size"] = Rnd((D(og, "blur") ?? 10) * k), ["color"] = DescColor(O(og, "Clr")), ["opacity"] = Math.Clamp((D(og, "Opct") ?? 75) / 100, 0, 1) };
        return fx.Count > 0 ? fx : null;
    }

    /// <summary>예전 형식 레이어 효과(lrFX): 그림자·외부 광선만</summary>
    static JsonObject ReadLegacyEffects(R r, long end)
    {
        var fx = new JsonObject();
        r.U16();
        int n = r.U16();
        double Fixed(long v) => v > 65535 ? v / 65536.0 : v;
        string Color() { r.U16(); var c = new[] { r.U16(), r.U16(), r.U16() }; r.U16(); return "#" + string.Concat(c.Select(v => (v >> 8).ToString("x2"))); }
        for (int i = 0; i < n && r.O + 12 <= end; i++)
        {
            if (r.Str(4) != "8BIM") break;
            string key = r.Str(4); long size = r.U32(), start = r.O;
            if (key == "dsdw")
            {
                r.U32(); double blur = Fixed(r.U32()); r.U32(); int ang = r.I32(), dist = r.I32();
                string c = Color(); r.Skip(8); int on = r.U8(); r.U8(); int op = r.U8();
                if (on != 0) fx["shadow"] = new JsonObject { ["on"] = true, ["dx"] = Rnd(-Math.Cos(ang * Math.PI / 180) * dist), ["dy"] = Rnd(Math.Sin(ang * Math.PI / 180) * dist), ["blur"] = Rnd(blur), ["color"] = c, ["opacity"] = op / 255.0 };
            }
            else if (key == "oglw")
            {
                r.U32(); double blur = Fixed(r.U32()); r.U32();
                string c = Color(); r.Skip(8); int on = r.U8(); int op = r.U8();
                if (on != 0) fx["glow"] = new JsonObject { ["on"] = true, ["size"] = Rnd(blur), ["color"] = c, ["opacity"] = op / 255.0 };
            }
            r.O = start + size;
        }
        return fx.Count > 0 ? fx : null;
    }

    static string DecodePSString(string s)
    {
        s = Regex.Replace(s, @"\\(.)", "$1", RegexOptions.Singleline);
        if (s.StartsWith("þÿ"))
        {
            var sb = new StringBuilder();
            for (int i = 2; i + 1 < s.Length; i += 2) sb.Append((char)((s[i] << 8) | s[i + 1]));
            return sb.ToString();
        }
        return s;
    }

    static JsonObject ParseTySh(byte[] data)
    {
        var r = new R(data);
        r.U16();
        var mat = new JsonArray(); for (int i = 0; i < 6; i++) mat.Add(r.F64());
        r.U16(); r.U32();
        var d = ReadDescriptor(r);
        string txt = d.TryGetValue("Txt", out var tv) ? tv as string ?? "" : "";
        var info = new JsonObject { ["text"] = txt.Replace("\r", "\n"), ["mat"] = mat, ["size"] = 24, ["color"] = "#000000", ["font"] = null, ["align"] = "left" };
        if (d.TryGetValue("EngineData", out var ed) && ed is byte[] eb)
        {
            string s = Encoding.Latin1.GetString(eb);
            var fs = Regex.Match(s, @"/FontSize ([\d.]+)");
            if (fs.Success) info["size"] = double.Parse(fs.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var fc = Regex.Match(s, @"/FillColor << /Type 1 /Values \[ ?([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+)");
            if (fc.Success) info["color"] = "#" + string.Concat(new[] { 2, 3, 4 }.Select(g => Rnd(double.Parse(fc.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture) * 255).ToString("x2")));
            var fi = Regex.Match(s, @"/StyleRunArray[\s\S]*?/Font (\d+)");
            int fontIdx = fi.Success ? int.Parse(fi.Groups[1].Value) : 0;
            var set = Regex.Match(s, @"/FontSet \[[\s\S]*?\]\s*>>");
            if (set.Success)
            {
                var list = Regex.Matches(set.Value, @"/Name \(((?:\\.|[^)])*)\)").Select(m => DecodePSString(m.Groups[1].Value)).ToList();
                if (list.Count > 0) info["font"] = fontIdx < list.Count ? list[fontIdx] : list[0];
            }
            var j = Regex.Match(s, @"/Justification (\d)");
            if (j.Success) info["align"] = (int.Parse(j.Groups[1].Value)) switch { 1 => "right", 2 => "center", _ => "left" };
            if (Regex.IsMatch(s, @"/Orientation 1")) info["vertical"] = true;
        }
        if (d.TryGetValue("Ornt", out var or) && or as string == "Vrtc") info["vertical"] = true;
        return info;
    }

    // ---- 조정 레이어 ----
    /// <summary>포토샵 조정 → ComicPad 조정 레이어 meta (모르는 것은 null)</summary>
    public static JsonObject ReadAdjust(string key, R r)
    {
        try
        {
            JsonObject M(string type, JsonObject p) => new() { ["type"] = type, ["params"] = p };
            if (key == "nvrt") return M("invert", new JsonObject());
            if (key == "post") return M("posterize", new JsonObject { ["levels"] = Math.Max(2, r.U16()) });
            if (key == "thrs") return M("threshold", new JsonObject { ["t"] = r.U16() });
            if (key == "blwh") return M("bw", new JsonObject { ["r"] = 30, ["g"] = 59, ["b"] = 11 });
            if (key == "brit") { int b = r.I16(), c = r.I16(); return M("bc", new JsonObject { ["b"] = Math.Clamp(Rnd(b / 1.5), -100, 100), ["c"] = Math.Clamp(c, -100, 100) }); }
            if (key == "levl")
            {
                r.U16();
                int inB = r.U16(), inW = r.U16(), outB = r.U16(), outW = r.U16(), g = r.U16();
                return M("levels", new JsonObject { ["inB"] = inB, ["inW"] = inW == 0 ? 255 : inW, ["outB"] = outB, ["outW"] = outW == 0 ? 255 : outW, ["gamma"] = (g == 0 ? 100 : g) / 100.0 });
            }
            if (key == "curv")
            {
                r.U8(); int ver = r.U16(); long bits = r.U32();
                var p = AdjustTypes.Default("curves");
                string[] names = { "master", "r", "g", "b" };
                if (ver == 1) for (int ch = 0; ch < 32; ch++)
                {
                    if ((bits & (1L << ch)) == 0) continue;
                    int n = r.U16(); var pts = new JsonArray();
                    for (int k = 0; k < n; k++) { int outv = r.U16(), inp = r.U16(); pts.Add(new JsonArray(inp, outv)); }
                    if (ch < names.Length) p[names[ch]] = pts;
                }
                return M("curves", p);
            }
            if (key == "blnc")
            {
                var v = Enumerable.Range(0, 9).Select(_ => Math.Clamp(r.I16(), -100, 100)).ToArray();
                return M("balance", new JsonObject { ["sr"] = v[0], ["sg"] = v[1], ["sb"] = v[2], ["mr"] = v[3], ["mg"] = v[4], ["mb"] = v[5], ["hr"] = v[6], ["hg"] = v[7], ["hb"] = v[8] });
            }
            if (key == "grdm")
            {
                int ver = r.U16();
                if (ver != 1 && ver != 3) return null;
                bool reversed = r.U8() != 0; r.U8();
                if (ver == 3) r.Skip(4);
                r.Skip(r.U32() * 2);
                int n = r.U16();
                var stops = new List<(double pos, string color)>();
                for (int i = 0; i < n; i++)
                {
                    long loc = r.U32(); r.U32();
                    int space = r.U16(), c1 = r.U16(), c2 = r.U16(), c3 = r.U16(); r.U16();
                    r.Skip(2);
                    int[] rgb = { c1 >> 8, c2 >> 8, c3 >> 8 };
                    if (space == 8) { int gv = 255 - Rnd(c1 / 10000.0 * 255); rgb = new[] { gv, gv, gv }; }
                    double pos = Math.Clamp(loc / 4096.0, 0, 1);
                    stops.Add((reversed ? 1 - pos : pos, "#" + string.Concat(rgb.Select(x => Math.Clamp(x, 0, 255).ToString("x2")))));
                }
                if (stops.Count < 2) return null;
                var arr = new JsonArray(); foreach (var s in stops.OrderBy(s => s.pos)) arr.Add(AdjustTypes.Stop(s.pos, s.color));
                return M("gradmap", new JsonObject { ["stops"] = arr });
            }
            if (key == "vibA")
            {
                r.U32();
                var d = ReadDescriptor(r);
                return M("vibrance", new JsonObject { ["v"] = Math.Clamp(D(d, "vibrance") ?? 0, -100, 100) });
            }
            if (key == "hue2" || key == "hue ")
            {
                r.U16(); r.U8(); r.U8();
                r.I16(); r.I16(); r.I16();
                int hue = r.I16(), sat = r.I16(), light = r.I16();
                return M("hsl", new JsonObject { ["hue"] = hue, ["sat"] = sat, ["light"] = light });
            }
        }
        catch { }
        return null;
    }

    sealed class Rec
    {
        public int Top, Left, Bottom, Right; public List<(int id, long len)> Chans = new();
        public string BlendKey, Name; public double Opacity; public bool Clip, Visible, AlphaLock;
        public (int top, int left, int bottom, int right, int def, int flags)? Mask;
        public int Section; public JsonObject Text, Fx, FxLegacy; public bool HasLfx2;
        public List<PsdVector.SubPath> VPaths; public Dictionary<string, object> VStroke; public string FillColor, FillLayer;
        public string AdjustKey; public JsonObject Adj;
        public uint[] Pixels; public byte[] MaskData;
    }

    static List<Rec> ReadLayerInfo(R r, Ctx c)
    {
        int count = Math.Abs(r.I16());
        var recs = new List<Rec>();
        for (int i = 0; i < count; i++)
        {
            var rec = new Rec { Top = r.I32(), Left = r.I32(), Bottom = r.I32(), Right = r.I32() };
            int nc = r.U16();
            for (int k = 0; k < nc; k++) rec.Chans.Add((r.I16(), c.Psb ? r.U64() : r.U32()));
            r.Skip(4);
            rec.BlendKey = r.Str(4);
            rec.Opacity = r.U8() / 255.0;
            rec.Clip = r.U8() == 1;
            int flags = r.U8();
            rec.Visible = (flags & 2) == 0; rec.AlphaLock = (flags & 1) != 0;
            r.Skip(1);
            long exLen = r.U32(), exEnd = r.O + exLen;
            long mLen = r.U32(), mEnd = r.O + mLen;
            if (mLen >= 18) rec.Mask = (r.I32(), r.I32(), r.I32(), r.I32(), r.U8(), r.U8());
            r.O = mEnd;
            r.Skip(r.U32());
            int nl = r.U8();
            rec.Name = Encoding.Latin1.GetString(r.B, (int)r.O, nl); r.O += nl;
            r.O += (4 - (nl + 1) % 4) % 4;
            while (r.O + 12 <= exEnd)
            {
                string sig = r.Str(4);
                if (sig != "8BIM" && sig != "8B64") break;
                string key = r.Str(4);
                long len = c.Psb && LongKeys.Contains(key) ? r.U64() : r.U32();
                long dStart = r.O;
                try
                {
                    if (key == "luni") { long n = r.U32(); var sb = new StringBuilder(); for (long k = 0; k < n; k++) sb.Append((char)r.U16()); rec.Name = sb.ToString().TrimEnd('\0'); }
                    else if (key == "lsct" || key == "lsdk") rec.Section = (int)r.U32();
                    else if (key == "TySh") rec.Text = ParseTySh(r.Bytes(len));
                    else if (key == "lfx2" || key == "lmfx") { r.U32(); r.U32(); rec.Fx = ReadEffects(ReadDescriptor(r), c.GlobalAngle); rec.HasLfx2 = true; }
                    else if (key == "lrFX") rec.FxLegacy = ReadLegacyEffects(r, dStart + len);
                    else if (key == "vmsk" || key == "vsms") rec.VPaths = PsdVector.ReadVectorPaths(r.Bytes(len), c.W, c.H);
                    else if (key == "vstk") { r.U32(); rec.VStroke = ReadDescriptor(r); }
                    else if (key == "SoCo") { r.U32(); rec.FillColor = DescColor(O(ReadDescriptor(r), "Clr")); rec.FillLayer = "SoCo"; }
                    else if (key == "GdFl" || key == "PtFl") rec.FillLayer = key;
                    else if (new[] { "brit", "levl", "curv", "expA", "vibA", "hue ", "hue2", "blnc", "blwh", "phfl", "mixr", "clrL", "nvrt", "post", "thrs", "selc", "grdm" }.Contains(key))
                    {
                        rec.AdjustKey = key;
                        rec.Adj = ReadAdjust(key, new R(r.B.AsSpan((int)r.O, (int)Math.Min(len, r.B.Length - r.O)).ToArray()));
                    }
                }
                catch { /* 이 정보만 건너뜀 */ }
                r.O = dStart + len + (len % 2);
            }
            r.O = exEnd;
            if (!rec.HasLfx2 && rec.FxLegacy != null) rec.Fx = rec.FxLegacy;
            recs.Add(rec);
        }
        foreach (var rec in recs)
        {
            int w = rec.Right - rec.Left, hh = rec.Bottom - rec.Top;
            var ch = new Dictionary<int, byte[]>();
            foreach (var (id, len) in rec.Chans)
            {
                var data = r.Bytes(len);
                if (id == -2 && rec.Mask != null)
                {
                    var m = rec.Mask.Value;
                    rec.MaskData = DecodeChannel(data, m.right - m.left, m.bottom - m.top, c, true);
                }
                else if (id >= -1 && id <= 3) ch[id] = DecodeChannel(data, w, hh, c, id == -1);
            }
            rec.Pixels = w > 0 && hh > 0 ? ToPixels(w, hh, ch, c) : null;
        }
        return recs;
    }

    /// <summary>단색 칠 + 벡터 마스크 + 선(vstk) → 그림 (GDI+)</summary>
    static unsafe void DrawFillLayer(Surface s, Rec rec, int W, int H)
    {
        var st = rec.VStroke ?? new Dictionary<string, object>();
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var path = rec.VPaths?.Count > 0 ? PsdVector.ToGraphicsPath(rec.VPaths) : null;
            bool fillOn = !(st.TryGetValue("fillEnabled", out var fe) && fe is bool fb && !fb);
            if (fillOn)
            {
                using var br = new SolidBrush(PsdVector.CssColor(rec.FillColor ?? "#000000"));
                if (path != null) g.FillPath(br, path); else g.FillRectangle(br, 0, 0, W, H);
            }
            if (path != null && st.TryGetValue("strokeEnabled", out var se) && se is bool sb && sb)
            {
                double w = D(st, "strokeStyleLineWidth") ?? 1; if (w == 0) w = 1;
                var col = PsdVector.CssColor(DescColor(O(O(st, "strokeStyleContent"), "Clr") ?? O(st, "strokeStyleContent")));
                string joinK = st.TryGetValue("strokeStyleLineJoinType", out var jk) ? jk as string : null;
                var join = joinK == "strokeStyleRoundJoin" ? LineJoin.Round : joinK == "strokeStyleBevelJoin" ? LineJoin.Bevel : LineJoin.Miter;
                string align = st.TryGetValue("strokeStyleLineAlignment", out var al) ? al as string : null;
                if (align == "strokeStyleAlignInside" || align == "strokeStyleAlignOutside")
                {
                    // 안쪽/바깥쪽 선: 2배 굵기로 그리고 모양 안/밖으로 잘라 냄
                    using var pen = new Pen(col, (float)(w * 2)) { LineJoin = join };
                    var state = g.Save();
                    using var region = new Region(path);
                    if (align == "strokeStyleAlignInside") g.SetClip(region, CombineMode.Replace); else g.SetClip(region, CombineMode.Exclude);
                    g.DrawPath(pen, path);
                    g.Restore(state);
                }
                else { using var pen = new Pen(col, (float)w) { LineJoin = join }; g.DrawPath(pen, path); }
            }
        }
        var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var row = new uint[W];
        for (int y = 0; y < H; y++)
        {
            uint* p = (uint*)((byte*)bd.Scan0 + y * bd.Stride);
            for (int x = 0; x < W; x++) row[x] = p[x];
            s.Write(new Rect(0, y, W, 1), row);
        }
        bmp.UnlockBits(bd);
    }

    // ---- 메인 ----
    public static Document Open(string path)
    {
        var d = Read(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), out var notes);
        if (notes.Count > 0) App.Toast(string.Join(" · ", notes));
        return d;
    }

    public static Document Read(byte[] buf, string name, out List<string> notesOut)
    {
        var r = new R(buf);
        if (r.Str(4) != "8BPS") throw new InvalidDataException("PSD 파일이 아닙니다");
        int ver = r.U16();
        if (ver != 1 && ver != 2) throw new InvalidDataException("알 수 없는 PSD 버전");
        r.Skip(6);
        int nch = r.U16(); int H = (int)r.U32(), W = (int)r.U32(); int depth = r.U16(), mode = r.U16();
        if (!new[] { 1, 8, 16, 32 }.Contains(depth)) throw new InvalidDataException($"{depth}비트 PSD는 지원하지 않습니다");
        if (!ModeNames.ContainsKey(mode)) throw new InvalidDataException("지원하지 않는 색상 모드입니다 (" + mode + ")");
        if ((long)W * H > 268000000) throw new InvalidDataException("너무 큰 이미지입니다");
        var c = new Ctx { Psb = ver == 2, Depth = depth, Mode = mode, W = W, H = H };
        long cmLen = r.U32();
        if (mode == 2 && cmLen >= 768) { c.Palette = r.Bytes(768); r.Skip(cmLen - 768); } else r.Skip(cmLen);
        {
            // 이미지 리소스: 전체 조명 각도(1037, 그림자 방향에 씀)
            long irLen = r.U32(), irEnd = r.O + irLen;
            while (r.O + 12 <= irEnd)
            {
                if (r.Str(4) != "8BIM") break;
                int id = r.U16();
                int nl = r.U8(); r.Skip(nl + (nl + 1) % 2);
                long size = r.U32(), start = r.O;
                if (id == 1037 && size >= 4) c.GlobalAngle = r.I32();
                r.O = start + size + size % 2;
            }
            r.O = irEnd;
        }
        long lmLen = c.Psb ? r.U64() : r.U32(), lmEnd = r.O + lmLen;
        var recs = new List<Rec>();
        if (lmLen > 0)
        {
            long liLen = c.Psb ? r.U64() : r.U32(), liStart = r.O;
            if (liLen > 0) recs = ReadLayerInfo(r, c);
            r.O = liStart + liLen;
            if (r.O + 4 <= lmEnd) r.Skip(r.U32());
            // 16/32비트 PSD 는 레이어 정보가 Lr16 / Lr32 블록 안에
            while (r.O + 12 <= lmEnd)
            {
                string sig = r.Peek4();
                if (sig != "8BIM" && sig != "8B64") { r.O = (r.O + 3) / 4 * 4; sig = r.Peek4(); if (sig != "8BIM" && sig != "8B64") break; }
                r.Skip(4);
                string key = r.Str(4);
                long len = c.Psb && LongKeys.Contains(key) ? r.U64() : r.U32();
                long dStart = r.O;
                if ((key == "Lr16" || key == "Lr32" || key == "Layr") && recs.Count == 0 && len > 0) recs = ReadLayerInfo(r, c);
                r.O = dStart + len;
            }
        }
        r.O = lmEnd;

        var doc = new Document(W, H) { Name = name, PaperVisible = true };
        var stack = new List<(string id, string parent)>();
        var notes = new List<string>();
        void Note(string s) { if (!notes.Contains(s)) notes.Add(s); }
        foreach (var rec in recs)
        {
            if (rec.Section == 3) { stack.Add((Layer.NewId("L"), stack.Count > 0 ? stack[^1].id : null)); continue; }
            string parent = stack.Count > 0 ? stack[^1].id : null;
            if (rec.Section == 1 || rec.Section == 2)
            {
                var g = stack.Count > 0 ? stack[^1] : (Layer.NewId("L"), null);
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                doc.Layers.Add(new Layer
                {
                    Id = g.Item1, Kind = "folder", Name = string.IsNullOrEmpty(rec.Name) ? "폴더" : rec.Name, Parent = g.Item2, Expanded = rec.Section == 1,
                    Visible = rec.Visible, Opacity = (float)rec.Opacity, Blend = Blend.GetValueOrDefault(rec.BlendKey, "source-over"),
                });
                continue;
            }
            if (rec.AdjustKey != null && rec.Adj != null)
            {
                doc.Layers.Add(new Layer { Name = string.IsNullOrEmpty(rec.Name) ? "조정" : rec.Name, Kind = "adjust", Parent = parent, Visible = rec.Visible, Opacity = (float)rec.Opacity, Clip = rec.Clip, Meta = rec.Adj });
                continue;
            }
            if (rec.AdjustKey != null) Note("일부 조정 레이어(선택 색상·채널 혼합 등)는 효과 없이 빈 레이어로 들어왔습니다");
            var l = new Layer(doc, string.IsNullOrEmpty(rec.Name) ? "레이어" : rec.Name)
            {
                Parent = parent, Visible = rec.Visible, Opacity = (float)rec.Opacity, Blend = Blend.GetValueOrDefault(rec.BlendKey, "source-over"),
                Clip = rec.Clip, AlphaLock = rec.AlphaLock, Meta = rec.Text != null ? new JsonObject { ["psdText"] = rec.Text } : null, Fx = rec.Fx,
            };
            if (rec.Fx != null) Note("레이어 효과(그림자·획·외부 광선)는 ComicPad 레이어 효과로 들어왔습니다 (그 밖의 효과는 빠짐)");
            if (rec.Pixels != null) l.Px.Write(new Rect(rec.Left, rec.Top, rec.Right - rec.Left, rec.Bottom - rec.Top), rec.Pixels);
            // 단색 칠/셰이프 레이어인데 그림이 비어 있으면 벡터 마스크·선 설정으로 직접 그림
            if (rec.FillLayer != null && (rec.Pixels == null || rec.Pixels.All(p => p >> 24 == 0)))
            {
                if (rec.FillLayer == "SoCo") { DrawFillLayer(l.Px, rec, W, H); Note("단색 칠·셰이프 레이어는 그림 레이어로 들어왔습니다"); }
                else Note("그라데이션·패턴 칠 레이어는 빈 레이어로 들어왔습니다");
            }
            if (rec.MaskData != null)
            {
                var m = rec.Mask.Value; int mw = m.right - m.left, mh = m.bottom - m.top;
                var mk = new Surface(W, H);
                if (m.def != 0)
                    for (int ti = 0; ti < mk.TileCount; ti++) { var tr = mk.TileRect(ti); var full = new uint[tr.W * tr.H]; Array.Fill(full, 0xFFFFFFFF); mk.Write(tr, full); }
                if (mw > 0 && mh > 0)
                {
                    var px = new uint[mw * mh];
                    for (int i = 0; i < px.Length; i++) { uint a = rec.MaskData[i]; px[i] = Px.Pack(a, a, a, a); }
                    mk.Write(new Rect(m.left, m.top, mw, mh), px);
                }
                l.Mask = mk;
                l.MaskOn = (m.flags & 2) == 0;
            }
            if (rec.Text != null) Note("텍스트 레이어: 텍스트 도구로 탭하면 다시 편집할 수 있는 글자로 바뀝니다");
            doc.Layers.Add(l);
        }

        // 레이어가 없으면 합성 이미지 사용
        if (!doc.Layers.Any(l => !l.IsFolder))
        {
            int comp = r.U16();
            int cc = ColorChannels(mode), rb = RowBytes(W, depth);
            var ch = new Dictionary<int, byte[]>();
            if (comp == 1)
            {
                int cs = c.Psb ? 4 : 2;
                var counts = new long[nch * H];
                for (int i = 0; i < counts.Length; i++) counts[i] = cs == 4 ? r.U32() : r.U16();
                for (int k = 0; k < nch; k++)
                {
                    var raw = new byte[(long)rb * H];
                    for (int y = 0; y < H; y++) { var bytes = r.Bytes(counts[k * H + y]); UnpackBits(bytes, 0, bytes.Length, raw, y * rb, rb); }
                    if (k < cc) ch[k] = To8(raw, 0, W, H, c, false);
                    else if (k == cc) ch[-1] = To8(raw, 0, W, H, c, true);
                }
            }
            else if (comp == 0)
            {
                for (int k = 0; k < Math.Min(nch, cc + 1); k++) { var raw = r.Bytes((long)rb * H); ch[k < cc ? k : -1] = To8(raw, 0, W, H, c, k >= cc); }
            }
            else
            {
                var all = RawChannel(comp, r.B, (int)r.O, W, H * nch, c);
                for (int k = 0; k < Math.Min(nch, cc + 1); k++) ch[k < cc ? k : -1] = To8(all, k * rb * H, W, H, c, k >= cc);
            }
            var l = new Layer(doc, "배경");
            l.Px.Write(new Rect(0, 0, W, H), ToPixels(W, H, ch, c));
            doc.Layers.Add(l);
        }
        doc.ActiveId = doc.Layers.LastOrDefault(l => !l.IsFolder)?.Id;
        notesOut = notes;
        return doc;
    }
}
