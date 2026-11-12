// 웹 판 core/filters.js 의 SVG 필터(feColorMatrix · feComponentTransfer · feComposite) 를 픽셀 계산으로 옮긴 것.
//  - color-interpolation-filters = sRGB 이므로 sRGB 값 그대로 계산
//  - feColorMatrix / feComponentTransfer 는 "미리 곱하지 않은" 색에 적용 (SVG 규격), feComposite arithmetic 은 미리 곱한 색에
//  - feComponentTransfer 는 크로미움처럼 0~255 한 칸마다 표를 만들어 씀 (값은 버림)
//  - 표 값은 웹처럼 소수 넷째 자리로 (toFixed(4))
using System.Globalization;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

/// <summary>조정 종류 (웹 ADJUST_TYPES 와 같은 이름·기본값)</summary>
public static class AdjustTypes
{
    public static readonly (string id, string name)[] List =
    {
        ("levels", "레벨 (밝기 범위)"), ("curves", "톤 커브 (곡선)"), ("hsl", "색조/채도/명도 (HSL)"), ("balance", "색상 균형"),
        ("bc", "밝기/대비"), ("gradmap", "그라데이션 맵"), ("posterize", "포스터화"), ("threshold", "2값화 (한계값)"),
        ("invert", "반전"), ("bw", "흑백"), ("colorize", "단색 입히기 (세피아 등)"), ("vibrance", "자연 채도"),
    };

    public static string Name(string type) => List.FirstOrDefault(x => x.id == type).name ?? type;

    public static JsonObject Default(string type) => type switch
    {
        "levels" => new JsonObject { ["inB"] = 0, ["inW"] = 255, ["gamma"] = 1, ["outB"] = 0, ["outW"] = 255 },
        "curves" => new JsonObject { ["master"] = Line(), ["r"] = Line(), ["g"] = Line(), ["b"] = Line() },
        "hsl" => new JsonObject { ["hue"] = 0, ["sat"] = 0, ["light"] = 0 },
        "balance" => new JsonObject { ["sr"] = 0, ["sg"] = 0, ["sb"] = 0, ["mr"] = 0, ["mg"] = 0, ["mb"] = 0, ["hr"] = 0, ["hg"] = 0, ["hb"] = 0 },
        "bc" => new JsonObject { ["b"] = 0, ["c"] = 0 },
        "gradmap" => new JsonObject { ["stops"] = new JsonArray(Stop(0, "#1b1430"), Stop(0.5, "#c2577a"), Stop(1, "#ffe9c4")) },
        "posterize" => new JsonObject { ["levels"] = 4 },
        "threshold" => new JsonObject { ["t"] = 128 },
        "invert" => new JsonObject(),
        "bw" => new JsonObject { ["r"] = 30, ["g"] = 59, ["b"] = 11 },
        "colorize" => new JsonObject { ["color"] = "#c08040", ["amount"] = 100 },
        "vibrance" => new JsonObject { ["v"] = 30 },
        _ => new JsonObject(),
    };
    static JsonArray Line() => new(new JsonArray(0, 0), new JsonArray(255, 255));
    public static JsonObject Stop(double pos, string color) => new() { ["pos"] = pos, ["color"] = color };

    /// <summary>{ ...기본값, ...p } (웹과 같이 얕은 합치기)</summary>
    public static JsonObject Merge(string type, JsonObject p)
    {
        var o = Default(type);
        if (p != null) foreach (var (k, v) in p) o[k] = v?.DeepClone();
        return o;
    }
}

/// <summary>필터 한 단계</summary>
public abstract class FOp { }
/// <summary>feColorMatrix (4x5, 오프셋은 0~1). 미리 곱하지 않은 색에</summary>
public sealed class MatrixOp : FOp { public float[] M; public MatrixOp(float[] m) { M = m; } }
/// <summary>feComponentTransfer (채널마다 256칸 표, null = 그대로)</summary>
public sealed class TransferOp : FOp { public byte[] R, G, B; }
/// <summary>단색 입히기: tint = 행렬(원본), 결과 = k2*tint + k3*원본 (feComposite arithmetic)</summary>
public sealed class ArithTintOp : FOp { public float[] M; public float K2, K3; }

/// <summary>필터 단계 목록 = SVG filter 하나</summary>
public sealed class FilterChain
{
    public readonly List<FOp> Ops = new();
    public bool IsEmpty => Ops.Count == 0;

    /// <summary>미리 곱한 BGRA 배열(일부)에 적용</summary>
    public void Apply(uint[] buf, int start = 0, int count = -1)
    {
        if (Ops.Count == 0) return;
        if (count < 0) count = buf.Length - start;
        var ops = Ops.ToArray();
        int end = start + count;
        // 같은 색이 이어지는 경우가 많아서 직전 결과를 기억
        uint lastIn = 0, lastOut = 0;
        for (int i = start; i < end; i++)
        {
            uint c = buf[i];
            uint a = c >> 24;
            if (a == 0) continue;
            if (c == lastIn && i > start) { buf[i] = lastOut; continue; }
            int r, g, b;
            if (a == 255) { r = (int)((c >> 16) & 255); g = (int)((c >> 8) & 255); b = (int)(c & 255); }
            else
            {
                r = (int)Math.Min(255, (((c >> 16) & 255) * 255 + a / 2) / a);
                g = (int)Math.Min(255, (((c >> 8) & 255) * 255 + a / 2) / a);
                b = (int)Math.Min(255, ((c & 255) * 255 + a / 2) / a);
            }
            foreach (var op in ops)
            {
                switch (op)
                {
                    case TransferOp t:
                        if (t.R != null) r = t.R[r];
                        if (t.G != null) g = t.G[g];
                        if (t.B != null) b = t.B[b];
                        break;
                    case MatrixOp m:
                        Mat(m.M, ref r, ref g, ref b, a);
                        break;
                    case ArithTintOp k:
                    {
                        int tr = r, tg = g, tb = b;
                        Mat(k.M, ref tr, ref tg, ref tb, a);
                        r = B8(k.K2 * tr + k.K3 * r);
                        g = B8(k.K2 * tg + k.K3 * g);
                        b = B8(k.K2 * tb + k.K3 * b);
                        break;
                    }
                }
            }
            uint o = a == 255 ? Px.Pack(255, (uint)r, (uint)g, (uint)b) : Px.Pack(a, Px.Mul((uint)r, a), Px.Mul((uint)g, a), Px.Mul((uint)b, a));
            lastIn = c; lastOut = o;
            buf[i] = o;
        }
    }

    static void Mat(float[] m, ref int r, ref int g, ref int b, uint a)
    {
        float R = r / 255f, G = g / 255f, B = b / 255f, A = a / 255f;
        float nr = m[0] * R + m[1] * G + m[2] * B + m[3] * A + m[4];
        float ng = m[5] * R + m[6] * G + m[7] * B + m[8] * A + m[9];
        float nb = m[10] * R + m[11] * G + m[12] * B + m[13] * A + m[14];
        r = B8(nr * 255); g = B8(ng * 255); b = B8(nb * 255);
    }
    static int B8(float v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5f);

    /// <summary>한 색(불투명 #rrggbb 0~255)을 계산 — 시험·미리보기용</summary>
    public (int r, int g, int b) ApplyRgb(int r, int g, int b)
    {
        var buf = new[] { Px.Pack(255, (uint)r, (uint)g, (uint)b) };
        Apply(buf);
        return ((int)((buf[0] >> 16) & 255), (int)((buf[0] >> 8) & 255), (int)(buf[0] & 255));
    }
}

/// <summary>SVG 필터 부품 만들기 + 조정 종류 → 필터 (웹 adjustInner 와 같은 계산)</summary>
public static class Svg
{
    static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    /// <summary>JS toFixed(4) 처럼 반올림한 값</summary>
    public static double F4(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    /// <summary>웹 table(fn, n): 0~1 을 n칸으로 나눠 fn 값 (0~1 로 자르고 소수 넷째 자리)</summary>
    public static double[] Table(Func<double, double> fn, int n = 64)
    {
        var t = new double[n];
        for (int i = 0; i < n; i++) t[i] = F4(Clamp01(fn(i / (double)(n - 1))));
        return t;
    }

    // ---- feFunc → 256칸 표 (크로미움 FEComponentTransfer 와 같은 식) ----
    public static byte[] LutTable(double[] v)
    {
        int n = v.Length; var o = new byte[256];
        if (n < 1) { for (int i = 0; i < 256; i++) o[i] = (byte)i; return o; }
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            int k = (int)(c * (n - 1));
            double v1 = v[k], v2 = v[Math.Min(k + 1, n - 1)];
            double val = 255.0 * (v1 + (c * (n - 1) - k) * (v2 - v1));
            o[i] = (byte)Math.Clamp(val, 0, 255);
        }
        return o;
    }
    public static byte[] LutDiscrete(double[] v)
    {
        int n = v.Length; var o = new byte[256];
        if (n < 1) { for (int i = 0; i < 256; i++) o[i] = (byte)i; return o; }
        for (int i = 0; i < 256; i++)
        {
            int k = Math.Min(i * n / 255, n - 1);
            o[i] = (byte)Math.Clamp(255.0 * v[k], 0, 255);
        }
        return o;
    }
    public static byte[] LutLinear(double slope, double intercept)
    {
        var o = new byte[256];
        for (int i = 0; i < 256; i++) o[i] = (byte)Math.Clamp(slope * i + 255 * intercept, 0, 255);
        return o;
    }
    public static TransferOp Ct(byte[] r, byte[] g, byte[] b) => new() { R = r, G = g, B = b };

    // ---- feColorMatrix ----
    static readonly float[] LumRow = { 0.299f, 0.587f, 0.114f, 0, 0 };
    public static MatrixOp LumMatrix() => new(new[] { 0.299f, 0.587f, 0.114f, 0, 0, 0.299f, 0.587f, 0.114f, 0, 0, 0.299f, 0.587f, 0.114f, 0, 0, 0, 0, 0, 1, 0 });
    public static MatrixOp Saturate(double s)
    {
        float S = (float)s;
        return new(new[]
        {
            0.213f + 0.787f * S, 0.715f - 0.715f * S, 0.072f - 0.072f * S, 0, 0,
            0.213f - 0.213f * S, 0.715f + 0.285f * S, 0.072f - 0.072f * S, 0, 0,
            0.213f - 0.213f * S, 0.715f - 0.715f * S, 0.072f + 0.928f * S, 0, 0,
            0, 0, 0, 1, 0,
        });
    }
    public static MatrixOp HueRotate(double deg)
    {
        double a = deg * Math.PI / 180;
        float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
        return new(new[]
        {
            0.213f + c * 0.787f - s * 0.213f, 0.715f - c * 0.715f - s * 0.715f, 0.072f - c * 0.072f + s * 0.928f, 0, 0,
            0.213f - c * 0.213f + s * 0.143f, 0.715f + c * 0.285f + s * 0.140f, 0.072f - c * 0.072f - s * 0.283f, 0, 0,
            0.213f - c * 0.213f - s * 0.787f, 0.715f - c * 0.715f + s * 0.715f, 0.072f + c * 0.928f + s * 0.072f, 0, 0,
            0, 0, 0, 1, 0,
        });
    }

    /// <summary>단조 3차 보간 (웹 curveFn 그대로). pts: [[x,y],...] 0~255</summary>
    public static Func<double, double> CurveFn(JsonArray pts)
    {
        var raw = new List<(double x, double y)>();
        if (pts != null) foreach (var pn in pts) if (pn is JsonArray a && a.Count >= 2) raw.Add((Num(a[0]), Num(a[1])));
        var p = raw.OrderBy(q => q.x).Select(q => (x: q.x / 255, y: q.y / 255)).ToArray();   // 안정 정렬 (JS sort 와 같음)
        if (p.Length < 2) return v => v;
        int n = p.Length;
        var dx = new double[n]; var m = new double[n]; var sl = new double[n];
        for (int i = 0; i < n - 1; i++) { dx[i] = p[i + 1].x - p[i].x; if (dx[i] == 0) dx[i] = 1e-6; sl[i] = (p[i + 1].y - p[i].y) / dx[i]; }
        m[0] = sl[0]; m[n - 1] = sl[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = sl[i - 1] * sl[i] <= 0 ? 0 : (sl[i - 1] + sl[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (sl[i] == 0) { m[i] = m[i + 1] = 0; continue; }
            double a = m[i] / sl[i], b = m[i + 1] / sl[i], s = a * a + b * b;
            if (s > 9) { double t = 3 / Math.Sqrt(s); m[i] = t * a * sl[i]; m[i + 1] = t * b * sl[i]; }
        }
        return x =>
        {
            if (x <= p[0].x) return p[0].y;
            if (x >= p[n - 1].x) return p[n - 1].y;
            int i = 0;
            while (i < n - 2 && x > p[i + 1].x) i++;
            double h = dx[i], t = (x - p[i].x) / h, t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * p[i].y + (t3 - 2 * t2 + t) * h * m[i] + (-2 * t3 + 3 * t2) * p[i + 1].y + (t3 - t2) * h * m[i + 1];
        };
    }

    public static double Num(JsonNode n, double fallback = 0)
    {
        if (n == null) return fallback;
        try
        {
            if (n is JsonValue v)
            {
                // JsonValue 는 담긴 형(int·long·double…)이 다르면 TryGetValue<double> 이 실패하므로 숫자는 글자로 읽음
                if (v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                if (v.TryGetValue<bool>(out var bo)) return bo ? 1 : 0;
                if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
        }
        catch { }
        return fallback;
    }
    public static double Num(JsonObject o, string k, double fallback = 0) => o == null ? fallback : Num(o[k], fallback);

    public static (double r, double g, double b) HexRgb(string hex)
    {
        uint c = Px.Unpremul(Px.FromHex(hex, 0xFF000000));
        return (((c >> 16) & 255) / 255.0, ((c >> 8) & 255) / 255.0, (c & 255) / 255.0);
    }

    static (double, double, double) GradSample(List<(double pos, string color)> stops, double t)
    {
        var s = stops.OrderBy(x => x.pos).ToList();
        if (t <= s[0].pos) return HexRgb(s[0].color);
        for (int i = 0; i < s.Count - 1; i++)
        {
            if (t <= s[i + 1].pos)
            {
                double d = s[i + 1].pos - s[i].pos; if (d == 0) d = 1;
                double u = (t - s[i].pos) / d;
                var a = HexRgb(s[i].color); var b = HexRgb(s[i + 1].color);
                return (a.r + (b.r - a.r) * u, a.g + (b.g - a.g) * u, a.b + (b.b - a.b) * u);
            }
        }
        return HexRgb(s[^1].color);
    }

    public static List<(double pos, string color)> Stops(JsonObject p)
    {
        var list = new List<(double, string)>();
        if (p?["stops"] is JsonArray arr) foreach (var sn in arr) if (sn is JsonObject o) list.Add((Num(o, "pos"), (string)o["color"] ?? "#000000"));
        if (list.Count == 0) list.Add((0, "#000000"));
        return list;
    }

    /// <summary>조정 종류와 값 → 필터 (웹 adjustInner)</summary>
    public static FilterChain Adjust(string type, JsonObject p0)
    {
        var p = AdjustTypes.Merge(type, p0);
        var f = new FilterChain();
        double N(string k) => Num(p, k);
        switch (type)
        {
            case "levels":
            {
                double inB = N("inB"), inW = N("inW"), gamma = N("gamma"), outB = N("outB"), outW = N("outW");
                var t = LutTable(Table(v =>
                {
                    double x = Clamp01((v * 255 - inB) / Math.Max(1, inW - inB));
                    return (outB + (outW - outB) * Math.Pow(x, 1 / Math.Max(0.05, gamma))) / 255;
                }, 128));
                f.Ops.Add(Ct(t, t, t));
                break;
            }
            case "curves":
            {
                var M = CurveFn(p["master"] as JsonArray); var R = CurveFn(p["r"] as JsonArray); var G = CurveFn(p["g"] as JsonArray); var B = CurveFn(p["b"] as JsonArray);
                f.Ops.Add(Ct(LutTable(Table(v => R(M(v)), 128)), LutTable(Table(v => G(M(v)), 128)), LutTable(Table(v => B(M(v)), 128))));
                break;
            }
            case "hsl":
            {
                double l = N("light") / 100;
                double slope = l > 0 ? 1 - l : 1 + l, icpt = l > 0 ? l : 0;
                f.Ops.Add(HueRotate(N("hue")));
                f.Ops.Add(Saturate(Math.Max(0, 1 + N("sat") / 100)));
                var t = LutLinear(slope, icpt);
                f.Ops.Add(Ct(t, t, t));
                break;
            }
            case "balance":
            {
                byte[] F(double s, double m, double h) => LutTable(Table(v => v + (s / 100) * 0.4 * Math.Pow(1 - v, 2) + (m / 100) * 0.4 * 4 * v * (1 - v) + (h / 100) * 0.4 * v * v, 128));
                f.Ops.Add(Ct(F(N("sr"), N("mr"), N("hr")), F(N("sg"), N("mg"), N("hg")), F(N("sb"), N("mb"), N("hb"))));
                break;
            }
            case "bc":
            {
                double c = Math.Tan((N("c") + 100) / 200 * Math.PI / 2), b = N("b");
                var t = LutTable(Table(v => (v - 0.5) * c + 0.5 + b / 200, 128));
                f.Ops.Add(Ct(t, t, t));
                break;
            }
            case "gradmap":
            {
                var stops = Stops(p);
                int n = 64; var rs = new double[n]; var gs = new double[n]; var bs = new double[n];
                for (int i = 0; i < n; i++) { var (r, g, b) = GradSample(stops, i / (double)(n - 1)); rs[i] = F4(r); gs[i] = F4(g); bs[i] = F4(b); }
                f.Ops.Add(LumMatrix());
                f.Ops.Add(Ct(LutTable(rs), LutTable(gs), LutTable(bs)));
                break;
            }
            case "posterize":
            {
                int n = Math.Max(2, (int)Math.Round(N("levels"), MidpointRounding.AwayFromZero));
                var v = new double[n]; for (int i = 0; i < n; i++) v[i] = F4(i / (double)(n - 1));
                var t = LutDiscrete(v);
                f.Ops.Add(Ct(t, t, t));
                break;
            }
            case "threshold":
            {
                double th = N("t");
                var v = new double[256]; for (int i = 0; i < 256; i++) v[i] = i >= th ? 1 : 0;
                var t = LutDiscrete(v);
                f.Ops.Add(LumMatrix());
                f.Ops.Add(Ct(t, t, t));
                break;
            }
            case "invert":
            {
                var t = LutTable(new double[] { 1, 0 });
                f.Ops.Add(Ct(t, t, t));
                break;
            }
            case "bw":
            {
                double s = N("r") + N("g") + N("b"); if (s == 0) s = 1;
                float wr = (float)(N("r") / s), wg = (float)(N("g") / s), wb = (float)(N("b") / s);
                f.Ops.Add(new MatrixOp(new[] { wr, wg, wb, 0, 0, wr, wg, wb, 0, 0, wr, wg, wb, 0, 0, 0, 0, 0, 1, 0 }));
                break;
            }
            case "colorize":
            {
                var (r, g, b) = HexRgb((string)p["color"] ?? "#c08040");
                double a = N("amount") / 100;
                float[] Row(double c) => new[] { (float)F4(0.299 * c * 1.6), (float)F4(0.587 * c * 1.6), (float)F4(0.114 * c * 1.6), 0f, 0f };
                var m = Row(r).Concat(Row(g)).Concat(Row(b)).Concat(new[] { 0f, 0, 0, 1, 0 }).ToArray();
                f.Ops.Add(new ArithTintOp { M = m, K2 = (float)Math.Round(a, 3, MidpointRounding.AwayFromZero), K3 = (float)Math.Round(1 - a, 3, MidpointRounding.AwayFromZero) });
                break;
            }
            case "vibrance":
                f.Ops.Add(Saturate(1 + N("v") / 100));
                break;
        }
        return f;
    }

    /// <summary>CSS filter: brightness(b%) contrast(c%) saturate(s%) hue-rotate(deg) (웹 "밝기/대비/채도" 필터)</summary>
    public static FilterChain CssColor(double bPct, double cPct, double sPct, double hueDeg)
    {
        var f = new FilterChain();
        double b = bPct / 100, c = cPct / 100;
        if (b != 1) { var t = LutLinear(b, 0); f.Ops.Add(Ct(t, t, t)); }
        if (c != 1) { var t = LutLinear(c, -(0.5 * c) + 0.5); f.Ops.Add(Ct(t, t, t)); }
        if (sPct != 100) f.Ops.Add(Saturate(sPct / 100));
        if (hueDeg != 0) f.Ops.Add(HueRotate(hueDeg));
        return f;
    }

    // ---- 조정 레이어 필터 캐시 (값이 같으면 다시 만들지 않음) ----
    static readonly Dictionary<string, FilterChain> cache = new();
    public static FilterChain AdjustCached(string type, JsonObject p)
    {
        string key = type + "|" + (p?.ToJsonString() ?? "");
        lock (cache)
        {
            if (cache.TryGetValue(key, out var f)) return f;
            if (cache.Count > 200) cache.Clear();
            return cache[key] = Adjust(type, p);
        }
    }
}
