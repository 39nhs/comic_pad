// PSD 공통: 큰 끝(big-endian) 쓰기, 포토샵 설명자(descriptor), 글자 레이어(TySh) — 웹 core/psdtext.js 그대로.
// 글자 레이어를 "글자 그대로" 넣어 포토샵에서 텍스트 도구로 고칠 수 있게 (그림도 함께 저장)
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.Filters;

/// <summary>큰 끝 바이트 쓰기</summary>
public sealed class Be
{
    readonly MemoryStream ms = new();
    public long Length => ms.Length;
    public void Bytes(byte[] b) => ms.Write(b);
    public void Bytes(ReadOnlySpan<byte> b) => ms.Write(b);
    public void U8(int v) => ms.WriteByte((byte)v);
    public void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
    public void I16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); ms.Write(b); }
    public void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); ms.Write(b); }
    public void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
    public void F64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); ms.Write(b); }
    public void Ascii(string s) { foreach (char c in s) ms.WriteByte((byte)(c & 255)); }
    public void Writer(Be w) => w.ms.WriteTo(ms);
    public byte[] ToArray() => ms.ToArray();
    public void CopyTo(Stream s) => ms.WriteTo(s);
}

/// <summary>설명자 값</summary>
public sealed class DV
{
    public string T, Type, Unit, Cls; public string S; public int L; public double D; public bool B; public byte[] Data;
    public List<(string key, DV v)> Items; public List<DV> List;
    public static DV Text(string s) => new() { T = "TEXT", S = s };
    public static DV Enum(string type, string v) => new() { T = "enum", Type = type, S = v };
    public static DV Long(int v) => new() { T = "long", L = v };
    public static DV Doub(double v) => new() { T = "doub", D = v };
    public static DV Bool(bool v) => new() { T = "bool", B = v };
    public static DV Tdta(byte[] v) => new() { T = "tdta", Data = v };
    public static DV UntF(string unit, double v) => new() { T = "UntF", Unit = unit, D = v };
    public static DV Objc(string cls, params (string, DV)[] items) => new() { T = "Objc", Cls = cls, Items = items.ToList() };
    public static DV VlLs(params DV[] v) => new() { T = "VlLs", List = v.ToList() };
}

public static class PsdText
{
    static void Uni(Be b, string s) { b.U32(s.Length + 1); foreach (char c in s) b.U16(c); b.U16(0); }
    static void Key(Be b, string k) { if (k.Length == 4) { b.U32(0); b.Ascii(k); } else { b.U32(k.Length); b.Ascii(k); } }
    static void Value(Be b, DV v)
    {
        switch (v.T)
        {
            case "TEXT": b.Ascii("TEXT"); Uni(b, v.S); break;
            case "enum": b.Ascii("enum"); Key(b, v.Type); Key(b, v.S); break;
            case "long": b.Ascii("long"); b.I32(v.L); break;
            case "doub": b.Ascii("doub"); b.F64(v.D); break;
            case "bool": b.Ascii("bool"); b.U8(v.B ? 1 : 0); break;
            case "tdta": b.Ascii("tdta"); b.U32(v.Data.Length); b.Bytes(v.Data); break;
            case "UntF": b.Ascii("UntF"); b.Ascii(v.Unit); b.F64(v.D); break;
            case "Objc": b.Ascii("Objc"); Descriptor(b, v.Cls, v.Items); break;
            case "VlLs": b.Ascii("VlLs"); b.U32(v.List.Count); foreach (var x in v.List) Value(b, x); break;
        }
    }
    public static void Descriptor(Be b, string cls, List<(string key, DV v)> items)
    {
        Uni(b, "");
        Key(b, cls);
        b.U32(items.Count);
        foreach (var (k, v) in items) { Key(b, k); Value(b, v); }
    }

    /// <summary>'8BIM' + 키 + 길이(짝수로 채움) + 데이터</summary>
    public static byte[] KeyedBlock(string key, byte[] data)
    {
        var o = new Be();
        o.Ascii("8BIM"); o.Ascii(key);
        int pad = data.Length % 2;
        o.U32(data.Length + pad);
        o.Bytes(data);
        if (pad != 0) o.U8(0);
        return o.ToArray();
    }
    /// <summary>'8BIM' + 키 + 길이 + (설명자 버전 16 + 설명자)</summary>
    public static byte[] DescriptorBlock(string key, string cls, params (string, DV)[] items)
    {
        var d = new Be();
        d.U32(16);
        Descriptor(d, cls, items.ToList());
        return KeyedBlock(key, d.ToArray());
    }

    // ---- EngineData ----
    /// <summary>"þÿ" + UTF-16BE, 괄호·역슬래시는 바이트마다 이스케이프</summary>
    static byte[] PsString(string s)
    {
        var o = new List<byte> { 0x28, 0xfe, 0xff };
        foreach (char c in s)
            foreach (int bt in new[] { c >> 8, c & 255 }) { if (bt == 0x28 || bt == 0x29 || bt == 0x5c) o.Add(0x5c); o.Add((byte)bt); }
        o.Add(0x29);
        return o.ToArray();
    }

    // 윈도 글꼴 이름 → 포스트스크립트 이름 (포토샵이 찾는 이름)
    static readonly Dictionary<string, (string reg, string bold)> PsFonts = new()
    {
        ["Malgun Gothic"] = ("MalgunGothic", "MalgunGothicBold"), ["맑은 고딕"] = ("MalgunGothic", "MalgunGothicBold"),
        ["NanumGothic"] = ("NanumGothic", "NanumGothicBold"), ["NanumMyeongjo"] = ("NanumMyeongjo", "NanumMyeongjoBold"), ["NanumBarunGothic"] = ("NanumBarunGothic", "NanumBarunGothicBold"), ["NanumPen"] = ("NanumPen", "NanumPen"),
        ["Gulim"] = ("Gulim", "Gulim"), ["Dotum"] = ("Dotum", "Dotum"), ["Batang"] = ("Batang", "Batang"), ["Gungsuh"] = ("Gungsuh", "Gungsuh"),
        ["Arial"] = ("ArialMT", "Arial-BoldMT"), ["Comic Sans MS"] = ("ComicSansMS", "ComicSansMS-Bold"), ["Impact"] = ("Impact", "Impact"),
        ["Yu Gothic"] = ("YuGothic-Regular", "YuGothic-Bold"), ["MS Gothic"] = ("MS-Gothic", "MS-Gothic"),
    };
    public static string PsFontName(string font, bool bold) => font != null && PsFonts.TryGetValue(font, out var f) ? (bold ? f.bold : f.reg) : System.Text.RegularExpressions.Regex.Replace(font ?? "MalgunGothic", "\\s+", "") + (bold ? "-Bold" : "");

    static string F(double v, int d) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    static byte[] EngineData(JsonObject m, string text)
    {
        int n = text.Length + 1;
        var (cr, cg, cb) = Svg.HexRgb((string)m["color"] ?? "#000000");
        string rgb = $"{F(cr, 5)} {F(cg, 5)} {F(cb, 5)}";
        int just = (string)m["align"] switch { "right" => 1, "center" => 2, _ => 0 };
        double size = Svg.Num(m, "size", 24), lh = Svg.Num(m, "lineHeight", 0); if (lh == 0) lh = 1.2;
        string lead = F(size * lh, 3);
        int track = (int)Math.Round(Svg.Num(m, "tracking") * 10, MidpointRounding.AwayFromZero);
        bool bold = (bool?)m["bold"] == true, italic = (bool?)m["italic"] == true, vertical = (bool?)m["vertical"] == true;
        string fontName = (string)m["font"];
        string font = PsFontName(fontName, bold);
        string style = $"/Font 0 /FontSize {F(size, 3)} /FauxBold {(bold && !(fontName != null && PsFonts.ContainsKey(fontName)) ? "true" : "false")} /FauxItalic {(italic ? "true" : "false")} /AutoLeading false /Leading {lead} /Tracking {track} /FillColor << /Type 1 /Values [ 1.00000 {rgb} ] >> /FillFlag true /StrokeFlag {(Svg.Num(m, "strokeWidth") > 0 ? "true" : "false")}";
        string para = "/Justification " + just + " /FirstLineIndent 0.0 /StartIndent 0.0 /EndIndent 0.0 /SpaceBefore 0.0 /SpaceAfter 0.0 /AutoHyphenate false";
        string fontSet = "/FontSet [ << /Name \u0001 /Script 0 /FontType 1 /Synthetic 0 >> << /Name \u0002 /Script 0 /FontType 0 /Synthetic 0 >> ]";
        string res = $"<< /KinsokuSet [ ] /MojiKumiSet [ ] /TheNormalStyleSheet 0 /TheNormalParagraphSheet 0 /ParagraphSheetSet [ << /Name \u0003 /DefaultStyleSheet 0 /Properties << {para} >> >> ] /StyleSheetSet [ << /Name \u0003 /StyleSheetData << {style} >> >> ] {fontSet} /SuperscriptSize .583 /SuperscriptPosition .333 /SubscriptSize .583 /SubscriptPosition .333 /SmallCapSize .7 >>";
        int wd = vertical ? 2 : 0;
        string body = "\n\n<<\n\t/EngineDict\n\t<<\n"
            + "\t\t/Editor << /Text \u0000 >>\n"
            + $"\t\t/ParagraphRun << /DefaultRunData << /ParagraphSheet << /DefaultStyleSheet 0 /Properties << >> >> /Adjustments << /Axis [ 1.0 0.0 1.0 ] /XY [ 0.0 0.0 ] >> >> /RunArray [ << /ParagraphSheet << /DefaultStyleSheet 0 /Properties << {para} >> >> /Adjustments << /Axis [ 1.0 0.0 1.0 ] /XY [ 0.0 0.0 ] >> >> ] /RunLengthArray [ {n} ] /IsJoinable 1 >>\n"
            + $"\t\t/StyleRun << /DefaultRunData << /StyleSheet << /StyleSheetData << >> >> >> /RunArray [ << /StyleSheet << /StyleSheetData << {style} >> >> >> ] /RunLengthArray [ {n} ] /IsJoinable 2 >>\n"
            + "\t\t/GridInfo << /GridIsOn false /ShowGrid false /GridSize 18.0 /GridLeading 22.0 /GridColor << /Type 1 /Values [ 0.0 0.0 0.0 1.0 ] >> /GridLeadingFillColor << /Type 1 /Values [ 0.0 0.0 0.0 1.0 ] >> /AlignLineHeightToGridFlags false >>\n"
            + "\t\t/AntiAlias 4\n"
            + "\t\t/UseFractionalGlyphWidths true\n"
            + $"\t\t/Rendered << /Version 1 /Shapes << /WritingDirection {wd} /Children [ << /ShapeType 0 /Procession 0 /Lines << /WritingDirection {wd} /Children [ ] >> /Cookie << /Photoshop << /ShapeType 0 /PointBase [ 0.0 0.0 ] /Base << /ShapeType 0 /TransformPoint0 [ 1.0 0.0 ] /TransformPoint1 [ 0.0 1.0 ] /TransformPoint2 [ 0.0 0.0 ] >> >> >> >> ] >> >>\n"
            + "\t>>\n"
            + $"\t/ResourceDict {res}\n"
            + $"\t/DocumentResources {res}\n"
            + ">>";
        // 자리표시(\u0000 글자, \u0001·\u0002 글꼴 이름, \u0003 스타일 이름)를 UTF-16 문자열로
        var o = new List<byte>();
        int last = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char ch = body[i];
            if (ch <= '\u0003')
            {
                o.AddRange(Encoding.UTF8.GetBytes(body[last..i]));
                o.AddRange(PsString(ch == '\u0000' ? text + "\r" : ch == '\u0001' ? font : ch == '\u0002' ? "AdobeInvisFont" : "Normal RGB"));
                last = i + 1;
            }
        }
        o.AddRange(Encoding.UTF8.GetBytes(body[last..]));
        return o.ToArray();
    }

    /// <summary>글자 레이어 meta → TySh 블록 바이트 ('8BIM' 'TySh' 길이 포함). bounds = 레이어 그림 범위</summary>
    public static byte[] TyshBlock(JsonObject m, Core.Rect bounds)
    {
        string plain = System.Text.RegularExpressions.Regex.Replace((string)m["text"] ?? "", "\\{([^{}|]+)\\|[^{}]+\\}", "$1");   // 루비 표시는 빼고 본문만
        string text = System.Text.RegularExpressions.Regex.Replace(plain, "\r?\n", "\r");
        double size = Svg.Num(m, "size", 24);
        double ox = Svg.Num(m, "x"), oy = Svg.Num(m, "y") + size * 0.82;
        double[] t = { 1, 0, 0, 1, ox, oy };
        if (m["mat"] is JsonArray mat && mat.Count >= 6)
        {
            double a = Svg.Num(mat[0]), b = Svg.Num(mat[1]), c = Svg.Num(mat[2]), d = Svg.Num(mat[3]), e = Svg.Num(mat[4]), f = Svg.Num(mat[5]);
            t = new[] { a, b, c, d, a * ox + c * oy + e, b * ox + d * oy + f };
        }
        var w = new Be();
        w.U16(1);
        foreach (var v in t) w.F64(v);
        w.U16(50); w.U32(16);
        Descriptor(w, "TxLr", new()
        {
            ("Txt ", DV.Text(text)),
            ("textGridding", DV.Enum("textGridding", "None")),
            ("Ornt", DV.Enum("Ornt", (bool?)m["vertical"] == true ? "Vrtc" : "Hrzn")),
            ("AntA", DV.Enum("Annt", "antiAliasSharp")),
            ("TextIndex", DV.Long(0)),
            ("EngineData", DV.Tdta(EngineData(m, text))),
        });
        w.U16(1); w.U32(16);
        Descriptor(w, "warp", new()
        {
            ("warpStyle", DV.Enum("warpStyle", "warpNone")),
            ("warpValue", DV.Doub(0)),
            ("warpPerspective", DV.Doub(0)),
            ("warpPerspectiveOther", DV.Doub(0)),
            ("warpRotate", DV.Enum("Ornt", "Hrzn")),
        });
        int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        w.I32(R(bounds.X - ox)); w.I32(R(bounds.Y - oy)); w.I32(R(bounds.X + bounds.W - ox)); w.I32(R(bounds.Y + bounds.H - oy));
        return KeyedBlock("TySh", w.ToArray());
    }
}
