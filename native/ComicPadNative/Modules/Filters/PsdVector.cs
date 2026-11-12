// PSD 셰이프 레이어 (웹 core/psdvector.js): 말풍선·컷·벡터 레이어를 포토샵에서 고칠 수 있는 모양(벡터 마스크 + 단색 칠 + 선)으로.
//  - 모양: 칠한 알파를 윤곽선(마칭 스퀘어)으로 따서 고리들로 → 바깥은 합치기, 구멍은 빼기
//  - 'SoCo' (단색 칠) + 'vmsk' (벡터 마스크 경로) + 'vstk' (선 스타일)
//  - 읽기: 'vmsk'/'vsms' 경로 → GDI+ 경로 (열 때 그림으로)
// 말풍선 모양(웹 tools/comic.js bubblePaths)도 여기서 GDI+ 경로로 만든다.
using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.Filters;

public sealed class Loop { public double[] Pts; public bool Hole; public double Area; }

public static class PsdVector
{
    // ---- CSS 색 → [r,g,b,a(0~1)] ----
    static readonly Dictionary<string, (int, int, int)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = (0, 0, 0), ["white"] = (255, 255, 255), ["red"] = (255, 0, 0), ["green"] = (0, 128, 0), ["blue"] = (0, 0, 255), ["gray"] = (128, 128, 128), ["grey"] = (128, 128, 128),
        ["yellow"] = (255, 255, 0), ["orange"] = (255, 165, 0), ["pink"] = (255, 192, 203), ["purple"] = (128, 0, 128), ["cyan"] = (0, 255, 255), ["magenta"] = (255, 0, 255),
    };
    public static (int r, int g, int b, double a) CssRGB(string c)
    {
        c = (c ?? "#000").Trim();
        if (c.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return (0, 0, 0, 0);
        if (Named.TryGetValue(c, out var nm)) return (nm.Item1, nm.Item2, nm.Item3, 1);
        if (c.StartsWith("#"))
        {
            var h = c[1..];
            if (h.Length == 3 || h.Length == 4) h = string.Concat(h.Select(ch => new string(ch, 2)));
            if ((h.Length == 6 || h.Length == 8) && uint.TryParse(h, NumberStyles.HexNumber, null, out uint v))
                return h.Length == 6 ? ((int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255, 1) : ((int)(v >> 24) & 255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (v & 255) / 255.0);
        }
        var m = System.Text.RegularExpressions.Regex.Match(c, @"rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:[\s,/]+([\d.]+%?))?\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            double P(string s) => double.Parse(s, CultureInfo.InvariantCulture);
            double a = 1;
            if (m.Groups[4].Success) { var s = m.Groups[4].Value; a = s.EndsWith("%") ? P(s[..^1]) / 100 : P(s); }
            return ((int)Math.Round(P(m.Groups[1].Value)), (int)Math.Round(P(m.Groups[2].Value)), (int)Math.Round(P(m.Groups[3].Value)), Math.Clamp(a, 0, 1));
        }
        return (0, 0, 0, 1);
    }
    public static Color CssColor(string c) { var (r, g, b, a) = CssRGB(c); return Color.FromArgb((int)Math.Round(a * 255), Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255)); }

    static DV RgbObj(string c) { var (r, g, b, _) = CssRGB(c); return DV.Objc("RGBC", ("Rd  ", DV.Doub(r)), ("Grn ", DV.Doub(g)), ("Bl  ", DV.Doub(b))); }

    // ---- 윤곽선 따기 (마칭 스퀘어) ----
    // 모서리 비트: TL=8, TR=4, BR=2, BL=1. 변: T, R, B, L
    static readonly Dictionary<char, (double, double)> EdgeMid = new() { ['T'] = (0.5, 0), ['R'] = (1, 0.5), ['B'] = (0.5, 1), ['L'] = (0, 0.5) };
    static readonly Dictionary<int, (double, double)> Corner = new() { [8] = (0, 0), [4] = (1, 0), [2] = (1, 1), [1] = (0, 1) };
    static readonly Dictionary<int, (char, char)> Isolate = new() { [8] = ('T', 'L'), [4] = ('T', 'R'), [2] = ('R', 'B'), [1] = ('B', 'L') };
    static (char, char) Orient(char a, char b, int corner, bool inside)
    {
        var P = EdgeMid[a]; var Q = EdgeMid[b]; var C = Corner[corner];
        double cross = (Q.Item1 - P.Item1) * (C.Item2 - P.Item2) - (Q.Item2 - P.Item2) * (C.Item1 - P.Item1);
        return (cross > 0) == inside ? (a, b) : (b, a);
    }
    sealed class Case { public (char, char)[] Segs; public bool Saddle; public (char, char)[] CenterIn, CenterOut; }
    static readonly Case[] Cases = BuildCases();
    static Case[] BuildCases()
    {
        var o = new Case[16];
        int[] bits = { 8, 4, 2, 1 };
        for (int c = 0; c < 16; c++)
        {
            bool Ins(int bit) => (c & bit) != 0;
            var crossed = new List<char>();
            if (Ins(8) != Ins(4)) crossed.Add('T');
            if (Ins(4) != Ins(2)) crossed.Add('R');
            if (Ins(2) != Ins(1)) crossed.Add('B');
            if (Ins(1) != Ins(8)) crossed.Add('L');
            if (crossed.Count == 2) { int rf = bits.First(Ins); o[c] = new Case { Segs = new[] { Orient(crossed[0], crossed[1], rf, true) } }; }
            else if (crossed.Count == 4)
            {
                var insideC = bits.Where(Ins).ToArray(); var outsideC = bits.Where(b => !Ins(b)).ToArray();
                o[c] = new Case
                {
                    Saddle = true,
                    CenterIn = outsideC.Select(k => Orient(Isolate[k].Item1, Isolate[k].Item2, k, false)).ToArray(),
                    CenterOut = insideC.Select(k => Orient(Isolate[k].Item1, Isolate[k].Item2, k, true)).ToArray(),
                };
            }
            else o[c] = new Case { Segs = Array.Empty<(char, char)>() };
        }
        return o;
    }

    /// <summary>값(0~255, w×h)에서 th 이상인 곳의 윤곽선 고리들 (문서 좌표). 바깥·구멍은 넓이 부호가 반대</summary>
    public static List<Loop> TraceContours(byte[] V, int w, int h, int ox = 0, int oy = 0, int th = 128)
    {
        long W2 = w + 2;
        int Val(int i, int j) => i < 0 || j < 0 || i >= w || j >= h ? 0 : V[j * w + i];
        long KeyH(int i, int j) => ((j + 1) * W2 + (i + 1)) * 2;
        long KeyV(int i, int j) => ((j + 1) * W2 + (i + 1)) * 2 + 1;
        var segStart = new Dictionary<long, (long next, double x, double y)>();
        var order = new List<long>();
        double Lerp(int a, int b) { int d = b - a; return d == 0 ? 0.5 : Math.Min(1, Math.Max(0, (th - a) / (double)d)); }
        for (int j = -1; j < h; j++)
            for (int i = -1; i < w; i++)
            {
                int tl = Val(i, j), tr = Val(i + 1, j), br = Val(i + 1, j + 1), bl = Val(i, j + 1);
                int c = (tl >= th ? 8 : 0) | (tr >= th ? 4 : 0) | (br >= th ? 2 : 0) | (bl >= th ? 1 : 0);
                if (c == 0 || c == 15) continue;
                var cs = Cases[c];
                var segs = cs.Saddle ? ((tl + tr + br + bl) / 4.0 >= th ? cs.CenterIn : cs.CenterOut) : cs.Segs;
                (long k, double x, double y) Point(char e) => e switch
                {
                    'T' => (KeyH(i, j), i + Lerp(tl, tr), j),
                    'B' => (KeyH(i, j + 1), i + Lerp(bl, br), j + 1),
                    'L' => (KeyV(i, j), i, j + Lerp(tl, bl)),
                    _ => (KeyV(i + 1, j), i + 1, j + Lerp(tr, br)),
                };
                foreach (var (a, b) in segs)
                {
                    var A = Point(a); var B = Point(b);
                    if (!segStart.ContainsKey(A.k)) order.Add(A.k);
                    segStart[A.k] = (B.k, A.x, A.y);
                }
            }
        var loops = new List<Loop>();
        foreach (var k0 in order)
        {
            if (!segStart.ContainsKey(k0)) continue;
            var pts = new List<double>();
            long k = k0; int guard = 0;
            while (segStart.TryGetValue(k, out var s) && guard++ < 10_000_000)
            {
                segStart.Remove(k);
                pts.Add(s.x + 0.5 + ox); pts.Add(s.y + 0.5 + oy);   // 값은 픽셀 가운데에 있다
                k = s.next;
            }
            if (pts.Count < 6) continue;
            double area = 0; int n = pts.Count;
            for (int p = 0; p < n; p += 2) { int q = (p + 2) % n; area += pts[p] * pts[q + 1] - pts[q] * pts[p + 1]; }
            loops.Add(new Loop { Pts = pts.ToArray(), Area = area / 2 });
        }
        return loops;
    }

    /// <summary>닫힌 고리를 더글러스-포이커로 줄이기 (tol 픽셀)</summary>
    public static double[] SimplifyLoop(double[] pts, double tol = 0.4)
    {
        int n = pts.Length / 2;
        if (n <= 4) return pts;
        int far = 0; double fd = -1;
        for (int i = 1; i < n; i++) { double d = Math.Pow(pts[i * 2] - pts[0], 2) + Math.Pow(pts[i * 2 + 1] - pts[1], 2); if (d > fd) { fd = d; far = i; } }
        var keep = new bool[n]; keep[0] = keep[far] = true;
        var stack = new Stack<(int, int)>(); stack.Push((0, far)); stack.Push((far, n));
        (double, double) P(int i) => (pts[(i % n) * 2], pts[(i % n) * 2 + 1]);
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            var (ax, ay) = P(a); var (bx, by) = P(b);
            double dx = bx - ax, dy = by - ay, L = Math.Sqrt(dx * dx + dy * dy); if (L == 0) L = 1;
            int mi = -1; double md = tol;
            for (int i = a + 1; i < b; i++) { var (x, y) = P(i); double d = Math.Abs((x - ax) * dy - (y - ay) * dx) / L; if (d > md) { md = d; mi = i; } }
            if (mi >= 0) { keep[mi % n] = true; stack.Push((a, mi)); stack.Push((mi, b)); }
        }
        var o = new List<double>();
        for (int i = 0; i < n; i++) if (keep[i]) { o.Add(pts[i * 2]); o.Add(pts[i * 2 + 1]); }
        return o.ToArray();
    }

    /// <summary>값 평면에서 모양 고리들 (가장 큰 고리 방향 = 바깥, 반대 = 구멍)</summary>
    public static List<Loop> ShapeLoops(byte[] V, int w, int h, int bx, int by)
    {
        var loops = TraceContours(V, w, h, bx, by).Where(l => Math.Abs(l.Area) >= 0.6).ToList();
        if (loops.Count == 0) return loops;
        double outerSign = Math.Sign(loops.Aggregate((m, l) => Math.Abs(l.Area) > Math.Abs(m.Area) ? l : m).Area);
        foreach (var l in loops) { l.Hole = Math.Sign(l.Area) != outerSign; l.Pts = SimplifyLoop(l.Pts); }
        return loops;
    }

    // ---- 포토샵 경로 레코드 ----
    public static byte[] PathRecords(List<Loop> loops, int W, int H)
    {
        var b = new Be();
        void Zeros(int n) { for (int i = 0; i < n; i++) b.U8(0); }
        b.U16(6); Zeros(24);                       // 경로 채우기 규칙
        b.U16(8); b.U16(0); Zeros(22);             // 처음 채우기: 비어 있음에서 시작
        int Fx(double v, int size) => (int)Math.Round(v / size * 0x1000000, MidpointRounding.AwayFromZero);
        foreach (var l in loops)
        {
            int n = l.Pts.Length / 2;
            b.U16(0); b.U16(n); b.U16(l.Hole ? 2 : 1); b.U16(1); Zeros(18);
            for (int i = 0; i < n; i++)
            {
                double x = l.Pts[i * 2], y = l.Pts[i * 2 + 1];
                b.U16(2);
                for (int k = 0; k < 3; k++) { b.I32(Fx(y, H)); b.I32(Fx(x, W)); }
            }
        }
        return b.ToArray();
    }
    public static byte[] VmskBlock(List<Loop> loops, int W, int H)
    {
        var d = new Be(); d.U32(3); d.U32(0); d.Bytes(PathRecords(loops, W, H));
        return PsdText.KeyedBlock("vmsk", d.ToArray());
    }
    public static byte[] SocoBlock(string color) => PsdText.DescriptorBlock("SoCo", "null", ("Clr ", RgbObj(color)));
    public static byte[] VstkBlock(bool stroke = true, bool fill = true, double width = 0, string color = "#000000", string align = "center", string join = "round", double opacity = 100)
    {
        string alignKey = align switch { "inside" => "strokeStyleAlignInside", "outside" => "strokeStyleAlignOutside", _ => "strokeStyleAlignCenter" };
        string joinKey = join switch { "miter" => "strokeStyleMiterJoin", "bevel" => "strokeStyleBevelJoin", _ => "strokeStyleRoundJoin" };
        return PsdText.DescriptorBlock("vstk", "strokeStyle",
            ("strokeStyleVersion", DV.Long(2)),
            ("strokeEnabled", DV.Bool(stroke && width > 0)),
            ("fillEnabled", DV.Bool(fill)),
            ("strokeStyleLineWidth", DV.UntF("#Pxl", width)),
            ("strokeStyleLineDashOffset", DV.UntF("#Pnt", 0)),
            ("strokeStyleMiterLimit", DV.Doub(100)),
            ("strokeStyleLineCapType", DV.Enum("strokeStyleLineCapType", "strokeStyleButtCap")),
            ("strokeStyleLineJoinType", DV.Enum("strokeStyleLineJoinType", joinKey)),
            ("strokeStyleLineAlignment", DV.Enum("strokeStyleLineAlignment", alignKey)),
            ("strokeStyleScaleLock", DV.Bool(false)),
            ("strokeStyleStrokeAdjust", DV.Bool(false)),
            ("strokeStyleLineDashSet", DV.VlLs()),
            ("strokeStyleBlendMode", DV.Enum("BlnM", "Nrml")),
            ("strokeStyleOpacity", DV.UntF("#Prc", opacity)),
            ("strokeStyleContent", DV.Objc("solidColorLayer", ("Clr ", RgbObj(color)))),
            ("strokeStyleResolution", DV.Doub(72)));
    }

    // ---- 읽기 ----
    public sealed class Knot { public (double x, double y) Before, Anchor, After; }
    public sealed class SubPath { public List<Knot> Knots = new(); public bool Closed; public int Op; }

    public static List<SubPath> ReadVectorPaths(byte[] bytes, int W, int H)
    {
        int o = 8;
        var paths = new List<SubPath>();
        SubPath cur = null;
        double Fx(int p, int size) => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(p)) / (double)0x1000000 * size;
        while (o + 26 <= bytes.Length)
        {
            int sel = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(o));
            if (sel == 0 || sel == 3)
            {
                int op = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(o + 4));
                cur = new SubPath { Closed = sel == 0, Op = op == -1 ? 1 : op };
                paths.Add(cur);
            }
            else if ((sel == 1 || sel == 2 || sel == 4 || sel == 5) && cur != null)
            {
                (double, double) Pt(int k) => (Fx(o + 2 + k * 8 + 4, W), Fx(o + 2 + k * 8, H));
                cur.Knots.Add(new Knot { Before = Pt(0), Anchor = Pt(1), After = Pt(2) });
            }
            o += 26;
        }
        return paths;
    }

    /// <summary>읽은 경로 → GDI+ 경로 (빼기가 있으면 짝홀 규칙으로 근사)</summary>
    public static GraphicsPath ToGraphicsPath(List<SubPath> paths)
    {
        bool evenOdd = paths.Any(p => p.Op == 2 || p.Op == 0);
        var gp = new GraphicsPath(evenOdd ? FillMode.Alternate : FillMode.Winding);
        foreach (var s in paths)
        {
            var k = s.Knots;
            if (k.Count == 0) continue;
            gp.StartFigure();
            for (int i = 1; i <= k.Count; i++)
            {
                if (i == k.Count && !s.Closed) break;
                var a = k[i - 1]; var b = k[i % k.Count];
                gp.AddBezier((float)a.Anchor.x, (float)a.Anchor.y, (float)a.After.x, (float)a.After.y, (float)b.Before.x, (float)b.Before.y, (float)b.Anchor.x, (float)b.Anchor.y);
            }
            if (s.Closed) gp.CloseFigure();
        }
        return gp;
    }

    // ---- 말풍선 모양 (웹 tools/comic.js bubblePaths) ----
    static Func<double> Seeded(double seed)
    {
        long s = (long)seed % 2147483647; if (s <= 0) s += 2147483646;
        return () => (s = s * 16807 % 2147483647) / 2147483647.0;
    }

    /// <summary>말풍선 몸통·꼬리 경로 (문서 좌표)</summary>
    public static (GraphicsPath body, GraphicsPath tail) BubblePaths(JsonObject b)
    {
        double bx = Svg.Num(b, "x"), by = Svg.Num(b, "y"), bw = Svg.Num(b, "w"), bh = Svg.Num(b, "h");
        double cx = bx + bw / 2, cy = by + bh / 2, rx = bw / 2, ry = bh / 2;
        var rnd = Seeded(Svg.Num(b, "seed") is var sd && sd != 0 ? sd : 7);
        string shape = (string)b["shape"];
        var body = new GraphicsPath(FillMode.Winding);
        if (shape == "rect")
        {
            double r = Math.Min(Math.Min(bw, bh) * 0.18, Math.Min(bw, bh) / 2);
            if (r <= 0) body.AddRectangle(new RectangleF((float)bx, (float)by, (float)bw, (float)bh));
            else
            {
                float x = (float)bx, y = (float)by, w = (float)bw, h = (float)bh, d = (float)(r * 2);
                body.AddArc(x, y, d, d, 180, 90); body.AddArc(x + w - d, y, d, d, 270, 90);
                body.AddArc(x + w - d, y + h - d, d, d, 0, 90); body.AddArc(x, y + h - d, d, d, 90, 90);
                body.CloseFigure();
            }
        }
        else if (shape == "shout")
        {
            int spikes = Math.Max(10, (int)Math.Round((rx + ry) / 18, MidpointRounding.AwayFromZero));
            var pts = new PointF[spikes * 2];
            for (int i = 0; i < spikes * 2; i++)
            {
                double a = i / (double)(spikes * 2) * Math.PI * 2;
                bool outp = i % 2 == 0;
                double k = outp ? 1.12 + rnd() * 0.18 : 0.86 + rnd() * 0.04;
                pts[i] = new PointF((float)(cx + Math.Cos(a) * rx * k), (float)(cy + Math.Sin(a) * ry * k));
            }
            body.AddPolygon(pts);
        }
        else if (shape == "cloud")
        {
            int n = Math.Max(8, (int)Math.Round((rx + ry) / 22, MidpointRounding.AwayFromZero));
            body.AddEllipse((float)(cx - rx * 0.86), (float)(cy - ry * 0.86), (float)(rx * 1.72), (float)(ry * 1.72));
            for (int i = 0; i < n; i++)
            {
                double a = i / (double)n * Math.PI * 2;
                double r = Math.PI * (rx + ry) / n * (0.55 + rnd() * 0.12);
                double x = cx + Math.Cos(a) * rx * 0.9, y = cy + Math.Sin(a) * ry * 0.9;
                body.AddEllipse((float)(x - r), (float)(y - r), (float)(r * 2), (float)(r * 2));
            }
        }
        else body.AddEllipse((float)(cx - rx), (float)(cy - ry), (float)(rx * 2), (float)(ry * 2));
        GraphicsPath tail = null;
        if (b["tail"] is JsonObject t)
        {
            tail = new GraphicsPath(FillMode.Winding);
            double tx = Svg.Num(t, "x"), ty = Svg.Num(t, "y");
            double dx = tx - cx, dy = ty - cy, L = Math.Sqrt(dx * dx + dy * dy); if (L == 0) L = 1;
            double nx = -dy / L, ny = dx / L;
            if (shape == "cloud")
            {
                for (int i = 1; i <= 3; i++)
                {
                    double tt = 0.62 + i * 0.12;
                    double x = cx + dx * tt, y = cy + dy * tt;
                    double r = Math.Min(rx, ry) * (0.16 - i * 0.035);
                    if (r > 0) tail.AddEllipse((float)(x - r * 1.3), (float)(y - r), (float)(r * 2.6), (float)(r * 2));
                }
            }
            else
            {
                double tw = Math.Min(rx, ry) * (b["tailWidth"] is JsonNode twn ? Svg.Num(twn, 0.45) : 0.45);
                double bxx = cx + dx * 0.25, byy = cy + dy * 0.25;
                var b1 = (x: bxx + nx * tw / 2, y: byy + ny * tw / 2); var b2 = (x: bxx - nx * tw / 2, y: byy - ny * tw / 2);
                double bend = shape == "shout" ? 0 : L * 0.08;
                tail.StartFigure();
                Quad(tail, b1.x, b1.y, (b1.x + tx) / 2 + nx * bend, (b1.y + ty) / 2 + ny * bend, tx, ty);
                Quad(tail, tx, ty, (b2.x + tx) / 2 + nx * bend * 0.4, (b2.y + ty) / 2 + ny * bend * 0.4, b2.x, b2.y);
                tail.CloseFigure();
            }
        }
        return (body, tail);
    }
    static void Quad(GraphicsPath p, double x0, double y0, double qx, double qy, double x1, double y1)
        => p.AddBezier((float)x0, (float)y0, (float)(x0 + 2.0 / 3 * (qx - x0)), (float)(y0 + 2.0 / 3 * (qy - y0)), (float)(x1 + 2.0 / 3 * (qx - x1)), (float)(y1 + 2.0 / 3 * (qy - y1)), (float)x1, (float)y1);
}
