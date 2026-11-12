// 만화 모듈 공통: JSON 값 읽기, 2D 점·행렬(웹 DOMMatrix 와 같은 a,b,c,d,e,f), 다각형·원근 계산,
// GDI+ 로 그린 것을 Surface 픽셀로, 글꼴(시스템 + data/fonts), 메타 레이어 미리보기·확정(실행취소), 작은 UI 부품.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

// =====================================================================
// JSON
// =====================================================================
public static class J
{
    public static double Num(JsonNode n, double def = 0)
    {
        if (n is not JsonValue v) return def;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : def;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ds)) return ds;
        return def;
    }
    public static double Num(JsonObject o, string k, double def = 0) => o == null ? def : Num(o[k], def);
    public static double? NumOrNull(JsonObject o, string k) { var n = o?[k]; if (n is not JsonValue) return null; double v = Num(n, double.NaN); return double.IsNaN(v) ? null : v; }
    public static bool Bool(JsonObject o, string k, bool def = false)
    {
        var n = o?[k];
        if (n is not JsonValue v) return def;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<JsonElement>(out var e)) { if (e.ValueKind == JsonValueKind.True) return true; if (e.ValueKind == JsonValueKind.False) return false; }
        double d = Num(n, double.NaN);
        return double.IsNaN(d) ? def : d != 0;
    }
    public static string Str(JsonObject o, string k, string def = null)
    {
        var n = o?[k];
        if (n is not JsonValue v) return def;
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.String) return e.GetString();
        return n.ToJsonString();
    }
    public static JsonObject Obj(JsonObject o, string k) => o?[k] as JsonObject;
    public static JsonArray Arr(JsonObject o, string k) => o?[k] as JsonArray;
    public static JsonObject Clone(JsonObject o) => (JsonObject)o?.DeepClone();
    /// <summary>소수 넷째 자리까지 (파일 크기·웹 판과 비교 편하게)</summary>
    public static double R(double v) => Math.Round(v, 4);
    public static JsonObject Pt(double x, double y) => new() { ["x"] = R(x), ["y"] = R(y) };
    public static P2 ToP(JsonNode n) => n is JsonObject o ? new P2(Num(o, "x"), Num(o, "y")) : new P2(0, 0);
    public static double[] Doubles(JsonNode n) => n is JsonArray a ? a.Select(x => Num(x)).ToArray() : null;
    public static JsonArray ArrOf(IEnumerable<double> v) { var a = new JsonArray(); foreach (var x in v) a.Add(R(x)); return a; }
}

// =====================================================================
// 점·행렬
// =====================================================================
public readonly record struct P2(double X, double Y)
{
    public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
    public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
    public static P2 operator *(P2 a, double k) => new(a.X * k, a.Y * k);
    public double Len => Math.Sqrt(X * X + Y * Y);
    public P2 Norm() { double l = Len; return l < 1e-12 ? new(0, 0) : new(X / l, Y / l); }
    public PointF F => new((float)X, (float)Y);
}

/// <summary>2D 아핀 행렬 (웹 DOMMatrix 와 같은 순서: x' = a x + c y + e, y' = b x + d y + f)</summary>
public readonly record struct Mat2(double A, double B, double C, double D, double E, double F)
{
    public static readonly Mat2 Id = new(1, 0, 0, 1, 0, 0);
    public static Mat2 Translate(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Mat2 Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
    public static Mat2 Rotate(double deg) { double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r); return new(c, s, -s, c, 0, 0); }
    /// <summary>this × m (DOMMatrix.multiply 와 같음: m 을 먼저 적용)</summary>
    public Mat2 Mul(Mat2 m) => new(A * m.A + C * m.B, B * m.A + D * m.B, A * m.C + C * m.D, B * m.C + D * m.D, A * m.E + C * m.F + E, B * m.E + D * m.F + F);
    public P2 Apply(P2 p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);
    public P2 Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
    public double Det => A * D - B * C;
    public Mat2 Inverse() { double det = Det; if (Math.Abs(det) < 1e-15) det = 1e-15; return new(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det); }
    public Matrix Gdi() => new((float)A, (float)B, (float)C, (float)D, (float)E, (float)F);
    public static Mat2 From(double[] m) => m == null || m.Length < 6 ? Id : new(m[0], m[1], m[2], m[3], m[4], m[5]);
    public double[] Arr() => new[] { A, B, C, D, E, F };
    public bool IsId => this == Id;
}

/// <summary>단위 정사각형 (u,v) ↔ 네 점 사각형 원근 변환 (Heckbert, 웹 transform.js _quadMap 과 같음)</summary>
public sealed class QuadMap
{
    readonly double a, b, c, d, e, f, g, h;
    public QuadMap(P2 p0, P2 p1, P2 p2, P2 p3)
    {
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dx3 = p0.X - p1.X + p2.X - p3.X;
        double dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, dy3 = p0.Y - p1.Y + p2.Y - p3.Y;
        double den = dx1 * dy2 - dx2 * dy1;
        if ((Math.Abs(dx3) > 1e-6 || Math.Abs(dy3) > 1e-6) && Math.Abs(den) > 1e-9) { g = (dx3 * dy2 - dx2 * dy3) / den; h = (dx1 * dy3 - dx3 * dy1) / den; }
        a = p1.X - p0.X + g * p1.X; b = p3.X - p0.X + h * p3.X; c = p0.X;
        d = p1.Y - p0.Y + g * p1.Y; e = p3.Y - p0.Y + h * p3.Y; f = p0.Y;
    }
    public P2 Map(double u, double v) { double w = g * u + h * v + 1; return new((a * u + b * v + c) / w, (d * u + e * v + f) / w); }
    /// <summary>문서 점 → (u,v). 3x3 역행렬</summary>
    public P2 Inverse(double x, double y)
    {
        // M = [[a,b,c],[d,e,f],[g,h,1]] 의 역
        double A_ = e - f * h, B_ = -(d - f * g), C_ = d * h - e * g;
        double D_ = -(b - c * h), E_ = a - c * g, F_ = -(a * h - b * g);
        double G_ = b * f - c * e, H_ = -(a * f - c * d), I_ = a * e - b * d;
        double u = A_ * x + D_ * y + G_, v = B_ * x + E_ * y + H_, w = C_ * x + F_ * y + I_;
        if (Math.Abs(w) < 1e-12) w = 1e-12;
        return new(u / w, v / w);
    }
}

public static class Geo
{
    public static bool InPoly(IReadOnlyList<P2> q, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = q.Count - 1; i < q.Count; j = i++)
            if ((q[i].Y > y) != (q[j].Y > y) && x < (q[j].X - q[i].X) * (y - q[i].Y) / (q[j].Y - q[i].Y) + q[i].X) inside = !inside;
        return inside;
    }
    public static double Area(IReadOnlyList<P2> q)
    {
        double a = 0;
        for (int i = 0, j = q.Count - 1; i < q.Count; j = i++) a += (q[j].X + q[i].X) * (q[j].Y - q[i].Y);
        return Math.Abs(a / 2);
    }
    public static (double x0, double y0, double x1, double y1) Bounds(IEnumerable<P2> pts)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        return (x0, y0, x1, y1);
    }
    public static Rect BoundsRect(IEnumerable<P2> pts, double pad = 0)
    {
        var (x0, y0, x1, y1) = Bounds(pts);
        return x1 < x0 ? Rect.Empty : Rect.FromPoints(x0, y0, x1, y1, pad);
    }
    public static GraphicsPath Poly(IReadOnlyList<P2> q, bool close = true)
    {
        var p = new GraphicsPath(FillMode.Winding);
        if (q.Count >= 2) { p.AddLines(q.Select(v => v.F).ToArray()); if (close) p.CloseFigure(); }
        return p;
    }
    /// <summary>2차 베지어 → GDI+ 3차 베지어</summary>
    public static void Quad(GraphicsPath p, P2 a, P2 q, P2 b) => p.AddBezier(a.F, (a + (q - a) * (2.0 / 3)).F, (b + (q - b) * (2.0 / 3)).F, b.F);
    public static Rect RectOf(double x0, double y0, double x1, double y1) => Rect.FromPoints(x0, y0, x1, y1);
    /// <summary>웹 rectFromPoints: 두 점으로 사각형 (x,y,w,h)</summary>
    public static (double x, double y, double w, double h) FromPoints(double x0, double y0, double x1, double y1) => (Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
}

// =====================================================================
// GDI+ → 픽셀
// =====================================================================
public static class Gfx
{
    public const int MaxSide = 16000;

    /// <summary>문서 영역 r 에 GDI+ 로 그린 결과 (미리 곱한 BGRA, r.W*r.H). draw 는 문서 좌표로 그림</summary>
    public static unsafe uint[] Render(Rect r, Action<Graphics> draw)
    {
        var buf = new uint[Math.Max(0, r.W) * Math.Max(0, r.H)];
        if (r.IsEmpty) return buf;
        using var bmp = new Bitmap(r.W, r.H, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            Setup(g);
            g.TranslateTransform(-r.X, -r.Y);
            draw(g);
        }
        var bd = bmp.LockBits(new Rectangle(0, 0, r.W, r.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < r.H; y++)
            {
                uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride);
                for (int x = 0; x < r.W; x++) buf[y * r.W + x] = row[x];
            }
        }
        finally { bmp.UnlockBits(bd); }
        return buf;
    }

    /// <summary>덮인 정도(0~255)만: draw 에 흰 붓을 넘겨 줌</summary>
    public static byte[] Coverage(Rect r, Action<Graphics, Brush> draw)
    {
        var px = Render(r, g => draw(g, Brushes.White));
        var cov = new byte[px.Length];
        for (int i = 0; i < px.Length; i++) cov[i] = (byte)(px[i] >> 24);
        return cov;
    }

    public static void Setup(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
    }

    /// <summary>"#rrggbb" → GDI+ 색</summary>
    public static Color Col(string hex, Color? fallback = null)
    {
        uint c = Px.Unpremul(Px.FromHex(hex, 0));
        if (c == 0 && string.IsNullOrEmpty(hex)) return fallback ?? Color.Black;
        return Color.FromArgb((int)(c >> 24), (int)((c >> 16) & 255), (int)((c >> 8) & 255), (int)(c & 255));
    }
    public static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
    public static string Hex(uint premul) => Px.ToHex(premul);

    /// <summary>그린 버퍼(src, 영역 r)를 레이어 면에 얹음: 선택 영역·투명 픽셀 잠금 반영. pe 는 Touch 를 미리</summary>
    public static void Paint(Surface s, Rect r, uint[] src, Document d, bool useSel, bool alphaLock, float opacity = 1)
    {
        r = r.Clip(s.W, s.H).Intersect(r);
        if (r.IsEmpty) return;
        var buf = s.Read(r);
        var sel = useSel ? d.Sel.Mask : null;
        uint k = (uint)Math.Clamp((int)(opacity * 255 + 0.5), 0, 255);
        for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
            {
                int i = y * r.W + x;
                uint c = src[i];
                if (c == 0) continue;
                if (k < 255) c = Px.Scale(c, k);
                if (sel != null) { byte m = sel[(r.Y + y) * d.W + r.X + x]; if (m == 0) continue; c = Px.Scale(c, m); }
                buf[i] = alphaLock ? Px.Composite(c, buf[i], Core.Blend.Atop) : Px.Over(c, buf[i]);
            }
        s.Write(r, buf);
    }

    /// <summary>면 전체를 새로 (r 바깥은 투명): 메타로 다시 그리는 레이어용</summary>
    public static Surface NewSurface(Document d, Rect r, uint[] src)
    {
        var s = new Surface(d.W, d.H);
        var c = r.Clip(d.W, d.H);
        if (c.IsEmpty) return s;
        if (c == r) { s.Write(r, src); return s; }
        var part = new uint[c.W * c.H];
        for (int y = 0; y < c.H; y++) Array.Copy(src, (c.Y - r.Y + y) * r.W + (c.X - r.X), part, y * c.W, c.W);
        s.Write(c, part);
        return s;
    }

    /// <summary>문서 사각형을 화면 사각형으로 (회전 시 감싸는 상자)</summary>
    public static Rectangle ToScreen(ViewXf v, double x, double y, double w, double h)
    {
        var pts = new[] { v.ToScreen(x, y), v.ToScreen(x + w, y), v.ToScreen(x + w, y + h), v.ToScreen(x, y + h) };
        double x0 = pts.Min(p => p.x), y0 = pts.Min(p => p.y), x1 = pts.Max(p => p.x), y1 = pts.Max(p => p.y);
        return Rectangle.FromLTRB((int)Math.Floor(x0), (int)Math.Floor(y0), (int)Math.Ceiling(x1), (int)Math.Ceiling(y1));
    }
    public static PointF S(ViewXf v, double x, double y) { var (sx, sy) = v.ToScreen(x, y); return new((float)sx, (float)sy); }
    public static PointF S(ViewXf v, P2 p) => S(v, p.X, p.Y);
}

// =====================================================================
// 글꼴: 시스템 글꼴 + data/fonts 의 .ttf/.otf (이름 = 파일 이름, 웹 판과 같음)
// =====================================================================
public static class Fonts
{
    public static readonly (string id, string label)[] Sys =
    {
        ("Malgun Gothic", "맑은 고딕"), ("Gulim", "굴림"), ("Dotum", "돋움"), ("Batang", "바탕"), ("Gungsuh", "궁서"),
        ("NanumGothic", "나눔고딕"), ("NanumMyeongjo", "나눔명조"), ("NanumPen", "나눔손글씨 펜"), ("NanumBarunGothic", "나눔바른고딕"),
        ("Arial", "Arial"), ("Comic Sans MS", "Comic Sans"), ("Impact", "Impact"), ("Yu Gothic", "Yu Gothic"), ("MS Gothic", "MS Gothic"),
    };
    static PrivateFontCollection pfc;
    static readonly Dictionary<string, FontFamily> mine = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, FontFamily> cache = new(StringComparer.OrdinalIgnoreCase);
    static HashSet<string> installed;
    public static List<string> Mine { get; } = new();

    public static void LoadDataFonts()
    {
        try
        {
            string dir = Path.Combine(App.DataDir, "fonts");
            if (!Directory.Exists(dir)) return;
            pfc ??= new PrivateFontCollection();
            foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    int before = pfc.Families.Length;
                    pfc.AddFontFile(f);
                    var fams = pfc.Families;
                    var fam = fams.Length > before ? fams[^1] : fams.FirstOrDefault();
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (fam != null && !mine.ContainsKey(name)) { mine[name] = fam; Mine.Add(name); }
                }
                catch { }
            }
        }
        catch { }
    }

    public static (string id, string label)[] List() => Sys.Concat(Mine.Select(m => (m, m + " (내 폰트)"))).ToArray();

    public static bool Has(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (mine.ContainsKey(name)) return true;
        installed ??= new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        return installed.Contains(name);
    }

    public static FontFamily Family(string name)
    {
        name ??= "Malgun Gothic";
        if (mine.TryGetValue(name, out var m)) return m;
        if (cache.TryGetValue(name, out var c)) return c;
        FontFamily f;
        try { f = new FontFamily(name); } catch { try { f = new FontFamily("Malgun Gothic"); } catch { f = FontFamily.GenericSansSerif; } }
        cache[name] = f;
        return f;
    }

    public static FontStyle Style(FontFamily f, bool bold, bool italic)
    {
        var s = (bold ? FontStyle.Bold : 0) | (italic ? FontStyle.Italic : 0);
        if (f.IsStyleAvailable(s)) return s;
        if (bold && f.IsStyleAvailable(FontStyle.Bold)) return FontStyle.Bold;
        if (italic && f.IsStyleAvailable(FontStyle.Italic)) return FontStyle.Italic;
        return f.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : s;
    }

    /// <summary>em 상자 위쪽에서 기준선까지 (글자 크기 비율). 웹 textBaseline 'top'/'middle' 맞추기용</summary>
    public static (double asc, double desc) Metrics(FontFamily f, FontStyle st)
    {
        double em = f.GetEmHeight(st), a = f.GetCellAscent(st), d = f.GetCellDescent(st);
        if (em <= 0) return (0.8, 0.2);
        double s = a + d;
        return s <= 0 ? (0.8, 0.2) : (a / s, d / s);
    }
    public static double CellAscent(FontFamily f, FontStyle st) { double em = f.GetEmHeight(st); return em <= 0 ? 0.9 : f.GetCellAscent(st) / em; }

    static readonly Bitmap measureBmp = new(1, 1);
    static readonly Graphics measureG = Graphics.FromImage(measureBmp);
    static readonly StringFormat typo = MakeTypo();
    static StringFormat MakeTypo() { var f = (StringFormat)StringFormat.GenericTypographic.Clone(); f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoClip; return f; }
    public static StringFormat Typo => typo;

    /// <summary>글자 폭 (px). 웹 measureText().width 와 같은 뜻</summary>
    public static double Measure(string s, FontFamily f, FontStyle st, double size)
    {
        if (string.IsNullOrEmpty(s) || size <= 0) return 0;
        lock (measureG)
        {
            using var font = new Font(f, (float)size, st, GraphicsUnit.Pixel);
            return measureG.MeasureString(s, font, PointF.Empty, typo).Width;
        }
    }

    /// <summary>글자 하나를 path 에 (x = 왼쪽, baseline y). 웹 fillText 의 글자 모양</summary>
    public static void AddText(GraphicsPath p, string s, FontFamily f, FontStyle st, double size, double x, double baseline)
    {
        if (string.IsNullOrEmpty(s)) return;
        double top = baseline - CellAscent(f, st) * size;
        p.AddString(s, f, (int)st, (float)size, new PointF((float)x, (float)top), typo);
    }
}

// =====================================================================
// 메타 레이어(컷·말풍선·글자): 끄는 동안 미리보기, 끝나면 실행취소 한 번으로 확정
// =====================================================================
public sealed class MetaPreview
{
    public readonly Layer L;
    readonly JsonObject meta0;
    readonly Surface px0;
    readonly string name0;
    Rect last = Rect.Empty;
    public MetaPreview(Layer l) { L = l; meta0 = J.Clone(l.Meta); px0 = l.Px; name0 = l.Name; }
    public JsonObject OrigMeta => J.Clone(meta0);
    public Surface OrigPx => px0;

    /// <summary>meta 로 그려 보이기 (레이어 픽셀은 임시 면). dirty = 화면 다시 합성할 영역 (null 이면 전체)</summary>
    public void Show(JsonObject meta, Func<Layer, (Surface s, Rect r)> render)
    {
        L.Meta = meta;
        var (s, r) = render(L);
        L.Px = s; L.Touch();
        var inv = last.Union(r);
        last = r;
        App.Invalidate(inv.IsEmpty ? new Rect(0, 0, App.Doc.W, App.Doc.H) : inv.Inflate(4));
    }
    /// <summary>원래대로</summary>
    public void Restore() { L.Meta = J.Clone(meta0); L.Px = px0; L.Name = name0; L.Touch(); }
    public Rect LastRect => last;
}

public static class MetaLayers
{
    /// <summary>레이어 meta 를 바꾸고 다시 그림 (실행취소 = 구조 스냅숏: meta·픽셀 면 참조를 함께 되돌림)</summary>
    public static void Commit(Layer l, string label, JsonObject meta, Func<Layer, (Surface s, Rect r)> render, string name = null, Action also = null)
    {
        var d = App.Doc;
        Rect r = Rect.Empty;
        var old = l.Px?.Bounds() ?? Rect.Empty;
        App.History.Struct(d, label, () =>
        {
            l.Meta = meta;
            if (name != null) l.Name = name;
            var (s, rr) = render(l);
            l.Px = s; r = rr;
            also?.Invoke();
        });
        l.Touch();
        d.Modified = true;
        var inv = old.Union(r);
        App.Invalidate(inv.IsEmpty ? new Rect(0, 0, d.W, d.H) : inv.Inflate(4));
        App.NotifyLayers();
    }

    /// <summary>레이어 종류별 다시 그리기 (컷·말풍선·글자·효과음)</summary>
    public static (Surface s, Rect r) Render(Layer l)
    {
        var d = App.Doc;
        return l.Kind switch
        {
            "panel" => Panels.Render(d, l),
            "bubble" => Bubbles.Render(d, l),
            "text" => J.Bool(l.Meta, "sfx") ? Sfx.Render(d, l) : TextRender.Render(d, l),
            _ => (l.Px, Rect.Empty),
        };
    }
    public static bool IsMeta(Layer l) => l?.Meta != null && (l.Kind == "panel" && l.Meta["panels"] is JsonArray || l.Kind == "bubble" && l.Meta["bubbles"] is JsonArray || l.Kind == "text");

    /// <summary>새 레이어를 맨 위(또는 지정 위치)에 추가 (실행취소 가능)</summary>
    public static Layer Add(string name, string kind, JsonObject meta, int index = -1, string label = null, Action<Layer> after = null)
    {
        var d = App.Doc; Layer l = null;
        App.History.Struct(d, label ?? "새 레이어", () =>
        {
            l = new Layer(d, name, kind) { Meta = meta };
            int i = index < 0 || index > d.Layers.Count ? d.Layers.Count : index;
            d.Layers.Insert(i, l); d.ActiveId = l.Id;
            if (meta != null && IsMeta(l)) l.Px = Render(l).s;
            after?.Invoke(l);
        });
        d.Modified = true;
        App.InvalidateAll(); App.NotifyLayers();
        return l;
    }

    public static void Redraw() => SelectCommands.RedrawView();
    public static CanvasView Canvas => (App.Main as MainForm)?.Canvas;
    public static double Zoom => Canvas?.V.Zoom ?? 1;
}

// =====================================================================
// UI 부품
// =====================================================================
public static class CUI
{
    /// <summary>나란히 고르는 단추들 (웹 segmented). label 이 있으면 위에 흐린 이름</summary>
    public static Control Seg((string id, string text)[] items, Func<string> get, Action<string> set, string label = null)
    {
        var seg = UI.Controls.WebSeg.Of(items, get, set);
        if (label == null) return seg;
        var box = new UI.Controls.WebStack { FillParent = false, Margin = new Padding(0, 4, 0, 4) };
        box.Controls.Add(new Label { Text = label, AutoSize = true, Font = UI.Controls.Web.Small, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 4) });
        seg.Margin = Padding.Empty;
        box.Controls.Add(seg);
        return box;
    }

    /// <summary>색 단추 (색 칩 + 이름, 누르면 색 고르기)</summary>
    public static Control ColorBtn(string label, Func<string> get, Action<string> set) => new UI.Controls.WebColorButton(label, get, set);

    /// <summary>다시 만들 수 있는 옵션 상자 (고른 대상이 바뀌면 Rebuild)</summary>
    public sealed class Host
    {
        public readonly FlowLayoutPanel Panel = Opt.Panel();
        readonly Func<IEnumerable<Control>> build;
        public Host(Func<IEnumerable<Control>> b) { build = b; Rebuild(); }
        public void Rebuild()
        {
            if (Panel.IsDisposed) return;
            Panel.SuspendLayout();
            foreach (Control c in Panel.Controls.Cast<Control>().ToList()) c.Dispose();
            Panel.Controls.Clear();
            foreach (var c in build()) if (c != null) Panel.Controls.Add(c);
            Panel.ResumeLayout();
        }
    }

    // ---- 입력 대화상자 (웹 app.ui.form) ----
    public sealed class Field
    {
        public string Key, Label, Type = "number";      // number | text | textarea | select | check | info | color
        public object Value;
        public double Min = -1e9, Max = 1e9;
        public (string id, string text)[] Options;
        public int Decimals = -1;
    }
    public static Field Num(string key, string label, double v, double min = -1e9, double max = 1e9, int dec = -1) => new() { Key = key, Label = label, Value = v, Min = min, Max = max, Decimals = dec };
    public static Field Sel(string key, string label, (string, string)[] opts, string v) => new() { Key = key, Label = label, Type = "select", Options = opts, Value = v };
    public static Field Chk(string key, string label, bool v) => new() { Key = key, Label = label, Type = "check", Value = v };
    public static Field Txt(string key, string label, string v, bool multi = false) => new() { Key = key, Label = label, Type = multi ? "textarea" : "text", Value = v };
    public static Field Info(string text) => new() { Type = "info", Label = text };

    /// <summary>확인 → 값 사전, 취소 → null, 추가 단추 → {"$extra": 값} (모양은 UI.Dialogs.WebForm)</summary>
    public static Dictionary<string, object> Form(string title, IEnumerable<Field> fields, string okLabel = "확인", (string label, string value)[] extra = null)
    {
        var list = fields.Where(f => f != null).ToList();
        var spec = list.Select(fd =>
        {
            double v = fd.Type == "number" ? Convert.ToDouble(fd.Value ?? 0, CultureInfo.InvariantCulture) : 0;
            int dec = fd.Decimals >= 0 ? fd.Decimals : (v % 1 == 0 ? 0 : 2);
            return new UI.Dialogs.FormField
            {
                Key = fd.Key, Label = fd.Label, Options = fd.Options, Min = fd.Min, Max = fd.Max,
                Type = fd.Type switch { "check" => "checkbox", _ => fd.Type },
                Value = fd.Type == "number" ? v : fd.Value,
                Decimals = fd.Type == "number" ? dec : -1, Step = fd.Type == "number" && dec > 0 ? 0.1 : 1,
            };
        }).ToList();
        var r = UI.Dialogs.WebForm.Show(title, spec, okLabel, "취소", extra);
        if (r == null) return null;
        if (r.TryGetValue("__extra", out var ex)) return new Dictionary<string, object> { ["$extra"] = ex };
        return list.Where(f => f.Key != null && f.Type != "info" && r.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => r[f.Key]);
    }
    public static double D(Dictionary<string, object> v, string k) => v != null && v.TryGetValue(k, out var o) && o is double d ? d : 0;
    public static string S(Dictionary<string, object> v, string k) => v != null && v.TryGetValue(k, out var o) ? o as string : null;
    public static bool B(Dictionary<string, object> v, string k) => v != null && v.TryGetValue(k, out var o) && o is bool b && b;

    public static bool Confirm(string msg, string title = "ComicPad") => UI.Dialogs.Modals.Confirm(msg, "확인", false, title);

    /// <summary>화면 겹침 그림 공통: 점선 펜</summary>
    public static Pen Dash(Color c, float w = 1f, float a = 5, float b = 4) => new(c, w) { DashPattern = new[] { a / Math.Max(1, w), b / Math.Max(1, w) } };
    public static void Handle(Graphics g, PointF p, bool round = false, Color? fill = null, float r = 5)
    {
        using var br = new SolidBrush(fill ?? Color.White);
        using var pen = new Pen(Color.FromArgb(47, 124, 246), 1.5f);
        if (round) { g.FillEllipse(br, p.X - r - 1, p.Y - r - 1, (r + 1) * 2, (r + 1) * 2); g.DrawEllipse(pen, p.X - r - 1, p.Y - r - 1, (r + 1) * 2, (r + 1) * 2); }
        else { g.FillRectangle(br, p.X - r, p.Y - r, r * 2, r * 2); g.DrawRectangle(pen, p.X - r, p.Y - r, r * 2, r * 2); }
    }
    /// <summary>화면 점들을 감싸는 사각형 (도구 겹침 그림 범위)</summary>
    public static Rectangle Bounds(IEnumerable<PointF> pts, int pad = 12)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        if (x1 < x0) return Rectangle.Empty;
        var all = ScreenAll();
        var r = Rectangle.FromLTRB((int)Math.Floor(x0) - pad, (int)Math.Floor(y0) - pad, (int)Math.Ceiling(x1) + pad, (int)Math.Ceiling(y1) + pad);
        return all.IsEmpty ? r : Rectangle.Intersect(r, Rectangle.Inflate(all, 4, 4));
    }
    public static Rectangle ScreenAll() { var c = MetaLayers.Canvas; return c == null ? Rectangle.Empty : new Rectangle(0, 0, c.Width, c.Height); }
}
