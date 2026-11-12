// 포토샵 브러시(.abr) 읽기 (웹 core/abr.js · ui/abrimport.js): 그림으로 된 브러시 모양(샘플)과 설정 일부.
// 버전 1·2 (예전 포토샵) 와 6 (포토샵 7 ~ CC) 지원. 형식 참고: GIMP gimpbrush-load.c
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.Brushes;

public sealed class AbrSample
{
    public string Id = "", Name = "";
    public double Spacing = 0.25;
    public int W, H;
    public byte[] Mask;                      // 255 = 칠함
    public Dictionary<string, object> Settings;
}

public static class Abr
{
    sealed class Reader
    {
        public readonly byte[] B; public int P; public int Len => B.Length;
        public Reader(byte[] b) { B = b; }
        void Need(int n) { if (P + n > B.Length) throw new EndOfStreamException("파일이 중간에 끝났습니다"); }
        public byte U8() { Need(1); return B[P++]; }
        public sbyte I8() { Need(1); return (sbyte)B[P++]; }
        public int U16() { Need(2); int v = (B[P] << 8) | B[P + 1]; P += 2; return v; }
        public int I32() { Need(4); int v = (B[P] << 24) | (B[P + 1] << 16) | (B[P + 2] << 8) | B[P + 3]; P += 4; return v; }
        public uint U32() => (uint)I32();
        public double F64() { Need(8); var t = new byte[8]; Array.Copy(B, P, t, 0, 8); Array.Reverse(t); P += 8; return BitConverter.ToDouble(t, 0); }
        public string Str4() { Need(4); var s = Encoding.Latin1.GetString(B, P, 4); P += 4; return s; }
        public string Ucs2() { uint n = U32(); var sb = new StringBuilder(); for (uint i = 0; i < n; i++) { int c = U16(); if (c != 0) sb.Append((char)c); } return sb.ToString(); }
    }

    /// <summary>줄마다 PackBits 로 줄인 그림 풀기</summary>
    static byte[] UnRLE(Reader r, int w, int h, int depth)
    {
        int bpp = depth >> 3, rowLen = w * bpp;
        var lens = new int[h];
        for (int i = 0; i < h; i++) lens[i] = r.U16();
        var o = new byte[rowLen * h];
        int k = 0;
        for (int i = 0; i < h; i++)
        {
            int end = r.P + lens[i], row = 0;
            while (r.P < end)
            {
                int n = r.I8();
                if (n == -128) continue;
                if (n < 0) { n = -n + 1; byte c = r.U8(); for (int j = 0; j < n && row < rowLen; j++, row++) o[k++] = c; }
                else for (int j = 0; j <= n; j++) { byte c = r.U8(); if (row < rowLen) { o[k++] = c; row++; } }
            }
            r.P = end;
            k = (i + 1) * rowLen;
        }
        return o;
    }

    static (int w, int h, byte[] mask) ReadSample(Reader r, int w, int h, int depth, int compression)
    {
        if (w <= 0 || h <= 0 || w > 10000 || h > 10000) throw new InvalidDataException("브러시 크기가 이상합니다");
        int bpp = Math.Max(1, depth >> 3);
        byte[] data;
        if (compression != 0) data = UnRLE(r, w, h, depth);
        else { if (r.P + w * h * bpp > r.Len) throw new EndOfStreamException(); data = new byte[w * h * bpp]; Array.Copy(r.B, r.P, data, 0, data.Length); r.P += data.Length; }
        if (bpp == 2) { var d8 = new byte[w * h]; for (int i = 0; i < d8.Length; i++) d8[i] = data[i * 2]; data = d8; }   // 16비트면 위쪽 바이트만
        return (w, h, data);
    }

    static List<AbrSample> V12(Reader r, int version)
    {
        int count = r.U16(); var o = new List<AbrSample>();
        for (int i = 0; i < count && r.P < r.Len; i++)
        {
            int type = r.U16(); uint size = r.U32(); int next = (int)Math.Min(r.Len, r.P + size);
            if (type == 2)
            {
                try
                {
                    r.U32();                         // misc
                    int spacing = r.U16();
                    string name = version == 2 ? r.Ucs2() : "";
                    r.U8();                          // antialias
                    r.P += 8;                        // short bounds
                    int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32();
                    int depth = r.U16(), comp = r.U8();
                    var (w, h, m) = ReadSample(r, right - left, bottom - top, depth, comp);
                    o.Add(new AbrSample { Name = name, Spacing = spacing / 100.0, W = w, H = h, Mask = m });
                }
                catch { /* 건너뜀 */ }
            }
            r.P = next;
        }
        return o;
    }

    // ---- 포토샵 "설명자(descriptor)" ----
    static string IdOrStr(Reader r) { uint n = r.U32(); if (n == 0) return r.Str4(); var sb = new StringBuilder(); for (uint i = 0; i < n; i++) sb.Append((char)r.U8()); return sb.ToString(); }

    sealed class Unit { public string U; public double V; }

    static object ReadValue(Reader r, string type)
    {
        switch (type)
        {
            case "Objc": case "GlbO": return ReadDescriptor(r);
            case "VlLs": { uint n = r.U32(); var a = new List<object>(); for (uint i = 0; i < n; i++) a.Add(ReadValue(r, r.Str4())); return a; }
            case "doub": return r.F64();
            case "UntF": { string unit = r.Str4(); return new Unit { U = unit, V = r.F64() }; }
            case "UnFl": { string unit = r.Str4(); uint n = r.U32(); var a = new List<double>(); for (uint i = 0; i < n; i++) a.Add(r.F64()); return a; }
            case "TEXT": return r.Ucs2();
            case "enum": { string t = IdOrStr(r); return t + ":" + IdOrStr(r); }
            case "long": return (double)r.I32();
            case "comp": { long hi = r.I32(); long lo = r.U32(); return (double)(hi * 4294967296L + lo); }
            case "bool": return r.U8() != 0;
            case "type": case "GlbC": { r.Ucs2(); return IdOrStr(r); }
            case "alis": case "tdta": case "Pth ": { uint n = r.U32(); r.P += (int)n; return null; }
            case "obj ":
            {
                uint n = r.U32();
                for (uint i = 0; i < n; i++)
                {
                    string t = r.Str4();
                    if (t == "prop") { r.Ucs2(); IdOrStr(r); IdOrStr(r); }
                    else if (t == "Clss") { r.Ucs2(); IdOrStr(r); }
                    else if (t == "Enmr") { r.Ucs2(); IdOrStr(r); IdOrStr(r); IdOrStr(r); }
                    else if (t == "rele") { r.Ucs2(); IdOrStr(r); r.U32(); }
                    else if (t == "Idnt" || t == "indx") r.U32();
                    else if (t == "name") { r.Ucs2(); IdOrStr(r); r.Ucs2(); }
                }
                return null;
            }
            default: throw new InvalidDataException("알 수 없는 설정 형식 " + type);
        }
    }

    static Dictionary<string, object> ReadDescriptor(Reader r)
    {
        r.Ucs2();
        string cls = IdOrStr(r); uint n = r.U32();
        var o = new Dictionary<string, object> { ["_class"] = cls };
        for (uint i = 0; i < n; i++) { string key = IdOrStr(r); o[key] = ReadValue(r, r.Str4()); }
        return o;
    }

    static double? Num(object v) => v switch { double d => d, Unit u => u.V, _ => null };
    static Dictionary<string, object> D(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as Dictionary<string, object> ?? new() : new();
    static object G(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v : null;

    /// <summary>브러시 설명자 → 우리 브러시 설정 (가능한 것만)</summary>
    static Dictionary<string, object> BrushSettings(Dictionary<string, object> d)
    {
        var b = D(d, "Brsh");
        var o = new Dictionary<string, object> { ["name"] = G(d, "Nm  ") as string ?? "", ["sampleId"] = G(b, "sampledData") as string };
        double? dm = Num(G(b, "Dmtr")), sp = Num(G(b, "Spcn")), an = Num(G(b, "Angl")), rn = Num(G(b, "Rndn")), hd = Num(G(b, "Hrdn"));
        if (dm is double dmv && dmv != 0) o["size"] = dmv;
        if (sp != null) o["spacing"] = Math.Max(0.02, sp.Value / 100);
        if (an != null) o["angle"] = an.Value;
        if (rn != null && rn < 99) o["roundness"] = rn.Value / 100;
        if (hd != null) o["hardness"] = hd.Value / 100;
        if (!(G(d, "useTipDynamics") is bool utd && !utd))
        {
            var sv = D(d, "szVr"); double? mn = Num(G(d, "minimumDiameter"));
            if (Num(G(sv, "bVTy")) == 2) { o["pressureSize"] = true; o["minSize"] = mn != null ? mn.Value / 100 : 0.2; }
            else if (G(d, "useTipDynamics") is bool u2 && u2) o["pressureSize"] = false;
            if (Num(G(sv, "jitter")) is double j && j != 0) o["sizeJitter"] = j / 100;
            var av = D(d, "angleDynamics");
            if (Num(G(av, "jitter")) is double aj && aj != 0) o["rotJitter"] = aj / 100;
            if (Num(G(av, "bVTy")) == 6) o["followDir"] = true;
        }
        if (G(d, "useScatter") is bool us && us) { double? sc = Num(G(d, "Sctr")) ?? Num(G(D(d, "scatterDynamics"), "jitter")); if (sc is double scv && scv != 0) o["scatter"] = Math.Min(3, scv / 100); }
        if (G(d, "usePaintDynamics") is bool up && up)
        {
            var ov = D(d, "opVr");
            if (Num(G(ov, "bVTy")) == 2) { o["pressureOpacity"] = true; o["minOpacity"] = (Num(G(d, "minimumOpacity")) ?? 0) / 100; }
        }
        if (G(d, "useColorDynamics") is bool uc && uc)
        {
            if (Num(G(d, "H   ")) is double hj && hj != 0) o["hueJitter"] = hj / 100 / 2;
            if (Num(G(d, "Strt")) is double sj && sj != 0) o["satJitter"] = sj / 100;
            if (Num(G(d, "Brgh")) is double bj && bj != 0) o["valJitter"] = bj / 100;
        }
        return o;
    }

    static List<AbrSample> V6(Reader r, int sub)
    {
        var o = new List<AbrSample>();
        var settings = new List<Dictionary<string, object>>();
        while (r.P + 12 <= r.Len)
        {
            string sig = r.Str4(), key = r.Str4();
            uint size = r.U32(); long end = r.P + (long)size;
            if (sig != "8BIM") break;
            if (key == "samp")
            {
                while (r.P < end - 4)
                {
                    uint bsize = r.U32();
                    long next = r.P + bsize + ((4 - bsize % 4) % 4);
                    try
                    {
                        int idStart = r.P, idLen = r.U8();
                        var sb = new StringBuilder(); for (int i = 0; i < idLen; i++) sb.Append((char)r.U8());
                        r.P = idStart + (sub == 1 ? 47 : 301);
                        int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32();
                        int depth = r.U16(), comp = r.U8();
                        var (w, h, m) = ReadSample(r, right - left, bottom - top, depth, comp);
                        o.Add(new AbrSample { Id = sb.ToString(), W = w, H = h, Mask = m });
                    }
                    catch { /* 이 모양은 건너뜀 */ }
                    if (next > r.Len) break;
                    r.P = (int)next;
                }
            }
            if (key == "desc")
            {
                try
                {
                    r.U32(); var d = ReadDescriptor(r);
                    if (G(d, "Brsh") is List<object> list) settings = list.OfType<Dictionary<string, object>>().Select(BrushSettings).ToList();
                }
                catch { /* 설정은 못 읽어도 모양은 씀 */ }
            }
            long np = end + (size % 2);
            if (np > r.Len) break;
            r.P = (int)np;
        }
        // 모양과 설정 짝짓기: 모양 ID 로, 없으면 순서대로
        var sampled = settings.Where(s => s["sampleId"] != null).ToList();
        for (int i = 0; i < o.Count; i++)
        {
            var s = o[i];
            var st = settings.FirstOrDefault(x => x["sampleId"] is string id && id == s.Id) ?? (i < sampled.Count ? sampled[i] : null);
            if (st != null)
            {
                s.Settings = st;
                if (st["name"] is string nm && nm.Length > 0) s.Name = nm;
                if (st.TryGetValue("spacing", out var spv) && spv is double spd && spd != 0) s.Spacing = spd;
            }
        }
        return o;
    }

    /// <summary>.abr 바이트 → 모양 목록</summary>
    public static List<AbrSample> Read(byte[] buf)
    {
        var r = new Reader(buf);
        int version = r.U16();
        if (version == 1 || version == 2) return V12(r, version);
        if (version == 6 || version == 7 || version == 10) { int sub = r.U16(); return V6(r, sub); }
        throw new InvalidDataException($"지원하지 않는 ABR 버전입니다 ({version})");
    }

    /// <summary>모양 → 검정 + 투명도 그림 (너무 크면 줄임)</summary>
    public static Bitmap ToBitmap(AbrSample s, int max = 512)
    {
        var bmp = new Bitmap(s.W, s.H, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, s.W, s.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new int[s.W];
        for (int y = 0; y < s.H; y++)
        {
            for (int x = 0; x < s.W; x++) row[x] = s.Mask[y * s.W + x] << 24;
            System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, s.W);
        }
        bmp.UnlockBits(data);
        double k = Math.Min(1, (double)max / Math.Max(s.W, s.H));
        if (k >= 1) return bmp;
        var d = new Bitmap(Math.Max(1, (int)Math.Round(s.W * k)), Math.Max(1, (int)Math.Round(s.H * k)), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(d)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(bmp, 0, 0, d.Width, d.Height); }
        bmp.Dispose();
        return d;
    }

    /// <summary>포토샵 브러시 설정 중 우리가 쓸 수 있는 것만 (abrimport.js abrSettings)</summary>
    static void ApplySettings(JsonObject o, Dictionary<string, object> st)
    {
        if (st == null) return;
        foreach (var k in new[] { "pressureSize", "minSize", "pressureOpacity", "minOpacity", "sizeJitter", "rotJitter", "followDir", "scatter", "hueJitter", "satJitter", "valJitter" })
            if (st.TryGetValue(k, out var v) && v != null) o[k] = v is bool b ? JsonValue.Create(b) : JsonValue.Create(Convert.ToDouble(v));
        if (st.TryGetValue("size", out var sz) && sz is double s && s != 0) o["size"] = Math.Round(Math.Min(600, Math.Max(1, s)));
        if (st.TryGetValue("spacing", out var spc) && spc is double sp && sp != 0) o["spacing"] = Math.Max(0.02, Math.Min(2, sp));
    }

    static string SafeName(string s) { foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { ' ', '#' })) s = s.Replace(c, '_'); return s.Length == 0 ? "brush" : s; }

    /// <summary>ABR 파일 → 에셋 폴더(brushes/이름/001.png …)에 모양을 저장하고 브러시 목록에 "이미지 팁" 프리셋으로 추가. 추가한 수</summary>
    public static int Import(string file, string assetsDir = null)
    {
        string baseName = SafeName(Path.GetFileNameWithoutExtension(file));
        var samples = Read(File.ReadAllBytes(file));
        if (samples.Count == 0) throw new InvalidDataException("이 파일에는 가져올 수 있는 그림 브러시가 없습니다 (계산형 브러시만 있음)");
        assetsDir ??= Tips.AssetsDir;
        var list = PresetStore.List("brush");
        string stamp = DateTime.Now.Ticks.ToString("x")[^8..];
        int n = 0;
        foreach (var (s, i) in samples.Take(200).Select((s, i) => (s, i)))
        {
            string rel = $"brushes/{baseName}/{i + 1:000}.png";
            string full = Path.Combine(assetsDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            using (var bmp = ToBitmap(s)) bmp.Save(full, ImageFormat.Png);
            var o = new JsonObject
            {
                ["id"] = $"abr_{stamp}_{i}", ["name"] = $"{(s.Name.Length > 0 ? s.Name : baseName)} {(samples.Count > 1 ? "#" + (i + 1) : "")}".Trim(),
                ["tip"] = "image", ["tipImage"] = rel, ["tintTip"] = true,
                ["size"] = Math.Round(Math.Min(120, Math.Max(8, Math.Max(s.W, s.H) / 4.0))), ["spacing"] = Math.Max(0.05, Math.Min(1, s.Spacing > 0 ? s.Spacing : 0.25)),
                ["hardness"] = 1, ["pressureSize"] = true, ["minSize"] = 0.2, ["pressureOpacity"] = false, ["opacity"] = 1, ["flow"] = 1, ["abr"] = baseName,
            };
            ApplySettings(o, s.Settings);
            list.Add(BrushPreset.FromJson(o));
            n++;
        }
        Tips.ForgetImage(null);
        PresetStore.Save("brush");
        return n;
    }
}
