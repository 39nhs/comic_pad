// 픽셀 계산: 색 변환, 합성 모드 16가지 (웹 캔버스 globalCompositeOperation 과 같은 W3C 합성 공식).
using System.Runtime.CompilerServices;

namespace ComicPadNative.Core;

public enum Blend
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn, HardLight, SoftLight, Difference,
    Hue, Saturation, Color, Luminosity, Add,
    // 합성 연산 (모드가 아닌 것)
    Atop, DestIn, DestOut, Copy,
}

public static class Px
{
    // ---- 웹 이름 ↔ Blend -------------------------------------------------------
    static readonly Dictionary<string, Blend> names = new()
    {
        ["source-over"] = Blend.Normal, ["multiply"] = Blend.Multiply, ["screen"] = Blend.Screen, ["overlay"] = Blend.Overlay,
        ["darken"] = Blend.Darken, ["lighten"] = Blend.Lighten, ["color-dodge"] = Blend.ColorDodge, ["color-burn"] = Blend.ColorBurn,
        ["hard-light"] = Blend.HardLight, ["soft-light"] = Blend.SoftLight, ["difference"] = Blend.Difference,
        ["hue"] = Blend.Hue, ["saturation"] = Blend.Saturation, ["color"] = Blend.Color, ["luminosity"] = Blend.Luminosity,
        ["lighter"] = Blend.Add, ["source-atop"] = Blend.Atop, ["destination-in"] = Blend.DestIn, ["destination-out"] = Blend.DestOut, ["copy"] = Blend.Copy,
    };
    public static Blend Parse(string s) => s != null && names.TryGetValue(s, out var b) ? b : Blend.Normal;
    public static string Name(Blend b) { foreach (var (k, v) in names) if (v == b) return k; return "source-over"; }
    public static readonly (string id, string label)[] Modes =
    {
        ("source-over", "표준"), ("multiply", "곱하기"), ("screen", "스크린"), ("overlay", "오버레이"), ("darken", "어둡게"), ("lighten", "밝게"),
        ("color-dodge", "닷지"), ("color-burn", "번"), ("hard-light", "하드 라이트"), ("soft-light", "소프트 라이트"), ("difference", "차이"),
        ("hue", "색조"), ("saturation", "채도"), ("color", "컬러"), ("luminosity", "광도"), ("lighter", "더하기 (발광)"),
    };

    // ---- 색 -----------------------------------------------------------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint A(uint c) => c >> 24;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Pack(uint a, uint r, uint g, uint b) => (a << 24) | (r << 16) | (g << 8) | b;

    /// <summary>"#rrggbb" → 불투명 BGRA</summary>
    public static uint FromHex(string hex, uint fallback = 0xFF000000)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length < 6 || !uint.TryParse(h[..6], System.Globalization.NumberStyles.HexNumber, null, out uint v)) return fallback;
        uint a = 255;
        if (h.Length >= 8 && uint.TryParse(h[6..8], System.Globalization.NumberStyles.HexNumber, null, out uint av)) a = av;
        return Premul(Pack(a, (v >> 16) & 255, (v >> 8) & 255, v & 255));
    }
    public static string ToHex(uint c) { c = Unpremul(c); return $"#{(c >> 16) & 255:x2}{(c >> 8) & 255:x2}{c & 255:x2}"; }

    public static uint Premul(uint c)
    {
        uint a = c >> 24;
        if (a == 255) return c;
        if (a == 0) return 0;
        return Pack(a, Mul((c >> 16) & 255, a), Mul((c >> 8) & 255, a), Mul(c & 255, a));
    }
    public static uint Unpremul(uint c)
    {
        uint a = c >> 24;
        if (a == 255 || a == 0) return a == 0 ? 0 : c;
        return Pack(a, Math.Min(255, ((c >> 16) & 255) * 255 / a), Math.Min(255, ((c >> 8) & 255) * 255 / a), Math.Min(255, (c & 255) * 255 / a));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint Mul(uint x, uint a) { uint t = x * a + 128; return (t + (t >> 8)) >> 8; }

    /// <summary>픽셀에 0~255 배율 (미리 곱한 알파 그대로)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Scale(uint c, uint k) => k >= 255 ? c : k == 0 ? 0 : Pack(Mul(c >> 24, k), Mul((c >> 16) & 255, k), Mul((c >> 8) & 255, k), Mul(c & 255, k));

    /// <summary>표준 덮기: src 를 dst 위에 (미리 곱한 알파)</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Over(uint s, uint d)
    {
        uint sa = s >> 24;
        if (sa == 255) return s;
        if (sa == 0) return d;
        uint ia = 255 - sa;
        return Pack(sa + Mul(d >> 24, ia), ((s >> 16) & 255) + Mul((d >> 16) & 255, ia), ((s >> 8) & 255) + Mul((d >> 8) & 255, ia), (s & 255) + Mul(d & 255, ia));
    }

    /// <summary>src(불투명도 op 0~1 적용)를 dst 에 mode 로 합성</summary>
    public static uint Composite(uint s, uint d, Blend mode, float op = 1)
    {
        if (op < 1) s = Scale(s, (uint)(op * 255 + 0.5f));
        switch (mode)
        {
            case Blend.Normal: return Over(s, d);
            case Blend.Copy: return s;
            case Blend.Atop:
            {
                uint da = d >> 24; if (da == 0) return d;
                uint sa = s >> 24, ia = 255 - sa;
                return Pack(da, Mul((s >> 16) & 255, da) + Mul((d >> 16) & 255, ia), Mul((s >> 8) & 255, da) + Mul((d >> 8) & 255, ia), Mul(s & 255, da) + Mul(d & 255, ia));
            }
            case Blend.DestIn: return Scale(d, s >> 24);
            case Blend.DestOut: return Scale(d, 255 - (s >> 24));
            case Blend.Add:
                return Pack(Math.Min(255, (s >> 24) + (d >> 24)), Math.Min(255, ((s >> 16) & 255) + ((d >> 16) & 255)), Math.Min(255, ((s >> 8) & 255) + ((d >> 8) & 255)), Math.Min(255, (s & 255) + (d & 255)));
        }
        uint sA = s >> 24, dA = d >> 24;
        if (sA == 0) return d;
        if (dA == 0) return s;
        // 미리 곱하지 않은 0~1 색
        float as_ = sA / 255f, ab = dA / 255f;
        float sr = ((s >> 16) & 255) / 255f / as_, sg = ((s >> 8) & 255) / 255f / as_, sb = (s & 255) / 255f / as_;
        float br = ((d >> 16) & 255) / 255f / ab, bg = ((d >> 8) & 255) / 255f / ab, bb = (d & 255) / 255f / ab;
        float rr, rg, rb;
        if (mode >= Blend.Hue && mode <= Blend.Luminosity)
        {
            (rr, rg, rb) = NonSep(mode, br, bg, bb, sr, sg, sb);
        }
        else { rr = Sep(mode, br, sr); rg = Sep(mode, bg, sg); rb = Sep(mode, bb, sb); }
        float oa = as_ + ab - as_ * ab;
        float Or(float cs, float cb, float bm) => as_ * (1 - ab) * cs + ab * (1 - as_) * cb + as_ * ab * bm;
        uint P(float v) => (uint)Math.Clamp((int)(v * 255 + 0.5f), 0, 255);
        return Pack(P(oa), P(Or(sr, br, rr)), P(Or(sg, bg, rg)), P(Or(sb, bb, rb)));
    }

    static float Sep(Blend m, float b, float s)
    {
        switch (m)
        {
            case Blend.Multiply: return b * s;
            case Blend.Screen: return b + s - b * s;
            case Blend.Overlay: return HardL(s, b);
            case Blend.Darken: return Math.Min(b, s);
            case Blend.Lighten: return Math.Max(b, s);
            case Blend.ColorDodge: return b <= 0 ? 0 : s >= 1 ? 1 : Math.Min(1, b / (1 - s));
            case Blend.ColorBurn: return b >= 1 ? 1 : s <= 0 ? 0 : 1 - Math.Min(1, (1 - b) / s);
            case Blend.HardLight: return HardL(b, s);
            case Blend.SoftLight:
            {
                if (s <= 0.5f) return b - (1 - 2 * s) * b * (1 - b);
                float dd = b <= 0.25f ? ((16 * b - 12) * b + 4) * b : MathF.Sqrt(b);
                return b + (2 * s - 1) * (dd - b);
            }
            case Blend.Difference: return Math.Abs(b - s);
            default: return s;
        }
    }
    static float HardL(float b, float s) => s <= 0.5f ? b * 2 * s : 1 - (1 - b) * (1 - (2 * s - 1));

    static float Lum(float r, float g, float b) => 0.3f * r + 0.59f * g + 0.11f * b;
    static (float, float, float) ClipC(float r, float g, float b)
    {
        float l = Lum(r, g, b), n = Math.Min(r, Math.Min(g, b)), x = Math.Max(r, Math.Max(g, b));
        if (n < 0) { r = l + (r - l) * l / (l - n); g = l + (g - l) * l / (l - n); b = l + (b - l) * l / (l - n); }
        if (x > 1) { r = l + (r - l) * (1 - l) / (x - l); g = l + (g - l) * (1 - l) / (x - l); b = l + (b - l) * (1 - l) / (x - l); }
        return (r, g, b);
    }
    static (float, float, float) SetLum(float r, float g, float b, float l) { float d = l - Lum(r, g, b); return ClipC(r + d, g + d, b + d); }
    static float Sat(float r, float g, float b) => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
    static (float, float, float) SetSat(float r, float g, float b, float s)
    {
        float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        if (mx <= mn) return (0, 0, 0);
        float F(float c) => (c - mn) * s / (mx - mn);
        return (F(r), F(g), F(b));
    }
    static (float, float, float) NonSep(Blend m, float br, float bg, float bb, float sr, float sg, float sb)
    {
        switch (m)
        {
            case Blend.Hue: { var (r, g, b) = SetSat(sr, sg, sb, Sat(br, bg, bb)); return SetLum(r, g, b, Lum(br, bg, bb)); }
            case Blend.Saturation: { var (r, g, b) = SetSat(br, bg, bb, Sat(sr, sg, sb)); return SetLum(r, g, b, Lum(br, bg, bb)); }
            case Blend.Color: return SetLum(sr, sg, sb, Lum(br, bg, bb));
            default: return SetLum(br, bg, bb, Lum(sr, sg, sb));
        }
    }

    /// <summary>줄 단위 합성 (빠른 길: 표준 덮기)</summary>
    public static void CompositeRow(uint[] dst, int di, uint[] src, int si, int n, Blend mode, float op)
    {
        if (mode == Blend.Normal && op >= 1)
        {
            for (int k = 0; k < n; k++) { uint s = src[si + k]; if (s != 0) dst[di + k] = Over(s, dst[di + k]); }
            return;
        }
        for (int k = 0; k < n; k++)
        {
            uint s = src[si + k];
            if (s == 0 && mode != Blend.Copy && mode != Blend.DestIn) continue;
            dst[di + k] = Composite(s, dst[di + k], mode, op);
        }
    }

    // ---- HSV ----------------------------------------------------------------
    public static (double h, double s, double v) RgbToHsv(double r, double g, double b)
    {
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn, h = 0;
        if (d > 0) h = mx == r ? ((g - b) / d + 6) % 6 : mx == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, mx <= 0 ? 0 : d / mx, mx);
    }
    public static (double r, double g, double b) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360 / 60; int i = (int)h; double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }
}
