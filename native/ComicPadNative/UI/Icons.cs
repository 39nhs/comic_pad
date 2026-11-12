// 선 아이콘: 웹 app/js/ui/icons.js 의 24×24 SVG 를 그대로 그린다 (path·rect·circle·line·ellipse·polyline·polygon,
// fill / fill-opacity / opacity / stroke / stroke-width / stroke-dasharray / transform rotate·translate).
// 기본: fill none, stroke currentColor, stroke-width 1.8, 둥근 끝·이음 (웹 icon() 과 같음).
//   Icons.Get("pen", 22, Theme.Text) → Bitmap (캐시됨, Dispose 하지 말 것)
//   Icons.Add("이름", "<path d=…/>")   → 모듈이 아이콘을 더할 때 (웹 addIcon)
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ComicPadNative.UI;

public static partial class Icons
{
    static readonly Dictionary<(string, int, int, float), Bitmap> cache = new();
    static readonly Regex ElRe = new(@"<(path|rect|circle|line|ellipse|polyline|polygon)\b([^>]*?)/?>", RegexOptions.Compiled);
    static readonly Regex AttrRe = new(@"([a-zA-Z-]+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    public static bool Has(string name) => name != null && Data.ContainsKey(name);
    public static IEnumerable<string> Names => Data.Keys;
    public static void Add(string name, string svgBody) { Data[name] = svgBody; lock (cache) foreach (var k in cache.Keys.Where(k => k.Item1 == name).ToList()) cache.Remove(k); }

    /// <summary>아이콘 그림 (size×size, color 색). 없는 이름이면 "dots"</summary>
    public static Bitmap Get(string name, int size, Color color, float dpiScale = 1f)
    {
        int px = Math.Max(4, (int)Math.Round(size * dpiScale));
        var key = (name ?? "", px, color.ToArgb(), 1f);
        lock (cache)
        {
            if (cache.TryGetValue(key, out var b)) return b;
            var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); Draw(g, name, new RectangleF(0, 0, px, px), color); }
            cache[key] = bmp;
            return bmp;
        }
    }

    /// <summary>아이콘을 rect 안에 그림 (viewBox 0 0 24 24)</summary>
    public static void Draw(Graphics g, string name, RectangleF rect, Color color, float strokeWidth = 1.8f)
    {
        if (name == null || !Data.TryGetValue(name, out var body)) body = Data.TryGetValue("dots", out var d) ? d : "";
        var st = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(rect.X, rect.Y);
        float k = Math.Min(rect.Width, rect.Height) / 24f;
        g.ScaleTransform(k, k);
        foreach (Match m in ElRe.Matches(body))
        {
            var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match am in AttrRe.Matches(m.Groups[2].Value)) a[am.Groups[1].Value] = am.Groups[2].Value;
            using var path = Shape(m.Groups[1].Value, a);
            if (path == null) continue;
            var es = g.Save();
            if (a.TryGetValue("transform", out var tf)) ApplyTransform(g, tf);
            float op = F(a, "opacity", 1);
            string fill = a.TryGetValue("fill", out var fv) ? fv : "none";
            if (fill != "none")
            {
                float fo = F(a, "fill-opacity", 1) * op;
                var fc = fill == "currentColor" ? color : ParseColor(fill, color);
                using var br = new SolidBrush(Color.FromArgb((int)Math.Clamp(fc.A * fo, 0, 255), fc));
                g.FillPath(br, path);
            }
            string stroke = a.TryGetValue("stroke", out var sv) ? sv : "currentColor";
            if (stroke != "none")
            {
                var sc = stroke == "currentColor" ? color : ParseColor(stroke, color);
                using var pen = new Pen(Color.FromArgb((int)Math.Clamp(sc.A * op * F(a, "stroke-opacity", 1), 0, 255), sc), F(a, "stroke-width", strokeWidth))
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                if (a.TryGetValue("stroke-linecap", out var lc) && lc == "butt") { pen.StartCap = pen.EndCap = LineCap.Flat; }
                if (a.TryGetValue("stroke-linecap", out lc) && lc == "square") { pen.StartCap = pen.EndCap = LineCap.Square; }
                if (a.TryGetValue("stroke-dasharray", out var da))
                {
                    var nums = Nums(da).Select(v => (float)v).Where(v => v > 0).ToArray();
                    if (nums.Length > 0)
                    {
                        if (nums.Length % 2 == 1) nums = nums.Concat(nums).ToArray();
                        pen.DashCap = DashCap.Flat; pen.StartCap = pen.EndCap = LineCap.Flat;
                        pen.DashPattern = nums.Select(v => v / pen.Width).ToArray();
                    }
                }
                g.DrawPath(pen, path);
            }
            g.Restore(es);
        }
        g.Restore(st);
    }

    static float F(Dictionary<string, string> a, string k, float def) => a.TryGetValue(k, out var v) && float.TryParse(v.Trim().TrimEnd('%'), NumberStyles.Float, IC, out var f) ? f : def;

    static Color ParseColor(string s, Color def)
    {
        try { return ColorTranslator.FromHtml(s); } catch { return def; }
    }

    static void ApplyTransform(Graphics g, string tf)
    {
        foreach (Match m in Regex.Matches(tf, @"(rotate|translate|scale)\(([^)]*)\)"))
        {
            var n = Nums(m.Groups[2].Value);
            switch (m.Groups[1].Value)
            {
                case "rotate":
                    if (n.Count >= 3) { g.TranslateTransform((float)n[1], (float)n[2]); g.RotateTransform((float)n[0]); g.TranslateTransform(-(float)n[1], -(float)n[2]); }
                    else if (n.Count >= 1) g.RotateTransform((float)n[0]);
                    break;
                case "translate": if (n.Count >= 1) g.TranslateTransform((float)n[0], n.Count > 1 ? (float)n[1] : 0); break;
                case "scale": if (n.Count >= 1) g.ScaleTransform((float)n[0], n.Count > 1 ? (float)n[1] : (float)n[0]); break;
            }
        }
    }

    static GraphicsPath Shape(string tag, Dictionary<string, string> a)
    {
        var p = new GraphicsPath();
        switch (tag)
        {
            case "path": if (a.TryGetValue("d", out var d)) PathData(p, d); break;
            case "rect":
                {
                    float x = F(a, "x", 0), y = F(a, "y", 0), w = F(a, "width", 0), h = F(a, "height", 0);
                    float rx = a.ContainsKey("rx") ? F(a, "rx", 0) : F(a, "ry", 0), ry = a.ContainsKey("ry") ? F(a, "ry", 0) : rx;
                    rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
                    if (rx <= 0 || ry <= 0) p.AddRectangle(new RectangleF(x, y, w, h));
                    else
                    {
                        p.AddArc(x, y, rx * 2, ry * 2, 180, 90); p.AddArc(x + w - rx * 2, y, rx * 2, ry * 2, 270, 90);
                        p.AddArc(x + w - rx * 2, y + h - ry * 2, rx * 2, ry * 2, 0, 90); p.AddArc(x, y + h - ry * 2, rx * 2, ry * 2, 90, 90);
                        p.CloseFigure();
                    }
                    break;
                }
            case "circle": { float cx = F(a, "cx", 0), cy = F(a, "cy", 0), r = F(a, "r", 0); p.AddEllipse(cx - r, cy - r, r * 2, r * 2); break; }
            case "ellipse": { float cx = F(a, "cx", 0), cy = F(a, "cy", 0), rx = F(a, "rx", 0), ry = F(a, "ry", 0); p.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2); break; }
            case "line": p.AddLine(F(a, "x1", 0), F(a, "y1", 0), F(a, "x2", 0), F(a, "y2", 0)); break;
            case "polyline":
            case "polygon":
                {
                    var n = a.TryGetValue("points", out var pts) ? Nums(pts) : new List<double>();
                    var pp = new List<PointF>();
                    for (int i = 0; i + 1 < n.Count; i += 2) pp.Add(new PointF((float)n[i], (float)n[i + 1]));
                    if (pp.Count >= 2) { p.AddLines(pp.ToArray()); if (tag == "polygon") p.CloseFigure(); }
                    break;
                }
        }
        return p;
    }

    static List<double> Nums(string s)
    {
        var o = new List<double>();
        foreach (Match m in Regex.Matches(s, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")) o.Add(double.Parse(m.Value, IC));
        return o;
    }

    /// <summary>SVG path d → GraphicsPath (M L H V C S Q T A Z, 대·소문자)</summary>
    public static void PathData(GraphicsPath p, string d)
    {
        var toks = Regex.Matches(d, @"[MmLlHhVvCcSsQqTtAaZz]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Select(m => m.Value).ToList();
        int i = 0; char cmd = ' ';
        double cx = 0, cy = 0, sx = 0, sy = 0, lcx = 0, lcy = 0; char prev = ' ';
        bool open = false;
        bool IsCmd(int k) => k < toks.Count && char.IsLetter(toks[k][0]) && toks[k][0] != 'e' && toks[k][0] != 'E';
        double N() => double.Parse(toks[i++], IC);
        bool HasNum() => i < toks.Count && !IsCmd(i);
        // 플래그(0/1)가 붙어 쓰인 경우 ("a1 1 0 01 2 2") 를 위해 A 의 플래그는 한 글자씩
        double Flag()
        {
            var t = toks[i];
            if (t.Length > 1 && (t[0] == '0' || t[0] == '1') && t[1] != '.') { toks[i] = t[1..]; return t[0] - '0'; }
            i++; return double.Parse(t, IC);
        }
        void Line(double x, double y) { p.AddLine((float)cx, (float)cy, (float)x, (float)y); cx = x; cy = y; }
        while (i < toks.Count)
        {
            if (IsCmd(i)) { cmd = toks[i][0]; i++; }
            else if (cmd == ' ') { i++; continue; }
            bool rel = char.IsLower(cmd);
            double ox = rel ? cx : 0, oy = rel ? cy : 0;
            switch (char.ToUpper(cmd))
            {
                case 'M':
                    {
                        double x = N() + ox, y = N() + oy;
                        p.StartFigure(); cx = sx = x; cy = sy = y; open = true;
                        cmd = rel ? 'l' : 'L';      // 뒤 숫자는 선
                        lcx = cx; lcy = cy; prev = 'M';
                        continue;
                    }
                case 'L': { double x = N() + ox, y = N() + oy; Line(x, y); break; }
                case 'H': { double x = N() + ox; Line(x, cy); break; }
                case 'V': { double y = N() + (rel ? cy : 0); Line(cx, y); break; }
                case 'C':
                    {
                        double x1 = N() + ox, y1 = N() + oy, x2 = N() + ox, y2 = N() + oy, x = N() + ox, y = N() + oy;
                        p.AddBezier((float)cx, (float)cy, (float)x1, (float)y1, (float)x2, (float)y2, (float)x, (float)y);
                        lcx = x2; lcy = y2; cx = x; cy = y; prev = 'C'; continue;
                    }
                case 'S':
                    {
                        double x1 = prev == 'C' ? 2 * cx - lcx : cx, y1 = prev == 'C' ? 2 * cy - lcy : cy;
                        double x2 = N() + ox, y2 = N() + oy, x = N() + ox, y = N() + oy;
                        p.AddBezier((float)cx, (float)cy, (float)x1, (float)y1, (float)x2, (float)y2, (float)x, (float)y);
                        lcx = x2; lcy = y2; cx = x; cy = y; prev = 'C'; continue;
                    }
                case 'Q':
                    {
                        double qx = N() + ox, qy = N() + oy, x = N() + ox, y = N() + oy;
                        Quad(qx, qy, x, y); lcx = qx; lcy = qy; prev = 'Q'; continue;
                    }
                case 'T':
                    {
                        double qx = prev == 'Q' ? 2 * cx - lcx : cx, qy = prev == 'Q' ? 2 * cy - lcy : cy;
                        double x = N() + ox, y = N() + oy;
                        Quad(qx, qy, x, y); lcx = qx; lcy = qy; prev = 'Q'; continue;
                    }
                case 'A':
                    {
                        double rx = N(), ry = N(), rot = N(); double laf = Flag(), swf = Flag();
                        double x = N() + ox, y = N() + oy;
                        Arc(p, cx, cy, rx, ry, rot, laf != 0, swf != 0, x, y);
                        cx = x; cy = y; break;
                    }
                case 'Z':
                    p.CloseFigure(); cx = sx; cy = sy; open = false; prev = 'Z';
                    continue;
                default: i++; break;
            }
            prev = char.ToUpper(cmd);
            if (!HasNum() && !IsCmd(i)) break;
        }
        _ = open;

        void Quad(double qx, double qy, double x, double y)
        {
            double c1x = cx + 2.0 / 3 * (qx - cx), c1y = cy + 2.0 / 3 * (qy - cy), c2x = x + 2.0 / 3 * (qx - x), c2y = y + 2.0 / 3 * (qy - y);
            p.AddBezier((float)cx, (float)cy, (float)c1x, (float)c1y, (float)c2x, (float)c2y, (float)x, (float)y);
            cx = x; cy = y;
        }
    }

    /// <summary>SVG 호(끝점 표기) → 베지어 여러 개</summary>
    static void Arc(GraphicsPath p, double x1, double y1, double rx, double ry, double phiDeg, bool large, bool sweep, double x2, double y2)
    {
        if (rx == 0 || ry == 0 || (x1 == x2 && y1 == y2)) { if (x1 != x2 || y1 != y2) p.AddLine((float)x1, (float)y1, (float)x2, (float)y2); return; }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        double phi = phiDeg * Math.PI / 180, cph = Math.Cos(phi), sph = Math.Sin(phi);
        double dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
        double x1p = cph * dx + sph * dy, y1p = -sph * dx + cph * dy;
        double lam = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lam > 1) { double s = Math.Sqrt(lam); rx *= s; ry *= s; }
        double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p, den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        double co = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
        double cxp = co * rx * y1p / ry, cyp = -co * ry * x1p / rx;
        double cx = cph * cxp - sph * cyp + (x1 + x2) / 2, cy = sph * cxp + cph * cyp + (y1 + y2) / 2;
        double Ang(double ux, double uy, double vx, double vy) { double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy); return a; }
        double t1 = Ang(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double dt = Ang((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && dt > 0) dt -= 2 * Math.PI; else if (sweep && dt < 0) dt += 2 * Math.PI;
        int segs = (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2));
        double step = dt / segs, kk = 4.0 / 3 * Math.Tan(step / 4);
        double px = x1, py = y1;
        for (int s = 0; s < segs; s++)
        {
            double a1 = t1 + step * s, a2 = a1 + step;
            double c1 = Math.Cos(a1), s1 = Math.Sin(a1), c2 = Math.Cos(a2), s2 = Math.Sin(a2);
            (double, double) P(double ex, double ey) => (cx + cph * rx * ex - sph * ry * ey, cy + sph * rx * ex + cph * ry * ey);
            var q1 = P(c1 - kk * s1, s1 + kk * c1); var q2 = P(c2 + kk * s2, s2 - kk * c2); var q3 = P(c2, s2);
            p.AddBezier((float)px, (float)py, (float)q1.Item1, (float)q1.Item2, (float)q2.Item1, (float)q2.Item2, (float)q3.Item1, (float)q3.Item2);
            (px, py) = q3;
        }
    }
}
