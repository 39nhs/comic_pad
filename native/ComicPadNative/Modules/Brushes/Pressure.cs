// 필압 곡선 · 펜 필기감 설정 (웹 core/brush.js 의 curveLut · PRESSURE_PRESETS · pressureCurve, app.settings.penFeel)
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.BrushEngine;

public static class Pressure
{
    /// <summary>필압 곡선 모음 (설정·브러시 옵션에서 고르기)</summary>
    public static readonly (string name, double[][] pts)[] Presets =
    {
        ("기본 (곧게)", new[] { new[] { 0.0, 0 }, new[] { 1.0, 1 } }),
        ("부드럽게 (살짝만 눌러도 진하게)", new[] { new[] { 0.0, 0 }, new[] { 0.25, 0.5 }, new[] { 1.0, 1 } }),
        ("딱딱하게 (세게 눌러야 굵게)", new[] { new[] { 0.0, 0 }, new[] { 0.6, 0.3 }, new[] { 1.0, 1 } }),
        ("S자 (가운데에서 크게 변함)", new[] { new[] { 0.0, 0 }, new[] { 0.3, 0.12 }, new[] { 0.7, 0.88 }, new[] { 1.0, 1 } }),
        ("약한 손힘 (힘 덜 들게)", new[] { new[] { 0.0, 0.05 }, new[] { 0.15, 0.45 }, new[] { 0.5, 0.85 }, new[] { 1.0, 1 } }),
        ("처음부터 조금 굵게", new[] { new[] { 0.0, 0.25 }, new[] { 1.0, 1 } }),
        ("세게 눌러도 덜 굵게", new[] { new[] { 0.0, 0 }, new[] { 0.5, 0.55 }, new[] { 1.0, 0.8 } }),
    };

    static readonly Dictionary<string, float[]> lutCache = new();
    static string KeyOf(double[][] pts) => string.Join(";", pts.Select(p => $"{p[0]:R},{p[1]:R}"));

    /// <summary>점 [[입력, 출력], ...] (0~1) 을 지나는 매끄러운 단조 곡선 (Fritsch–Carlson). 256칸 표</summary>
    public static float[] CurveLut(double[][] pts)
    {
        string key = KeyOf(pts);
        lock (lutCache) if (lutCache.TryGetValue(key, out var hit)) return hit;
        var P = pts.Select(p => new[] { Math.Clamp(p[0], 0, 1), Math.Clamp(p[1], 0, 1) }).OrderBy(p => p[0]).ToList();
        if (P[0][0] > 0) P.Insert(0, new[] { 0, P[0][1] });
        if (P[^1][0] < 1) P.Add(new[] { 1, P[^1][1] });
        int n = P.Count;
        var d = new double[Math.Max(1, n - 1)]; var m = new double[n];
        for (int i = 0; i < n - 1; i++) d[i] = (P[i + 1][1] - P[i][1]) / Math.Max(1e-6, P[i + 1][0] - P[i][0]);
        m[0] = d[0]; m[n - 1] = d[Math.Max(0, n - 2)];
        for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = m[i + 1] = 0; continue; }
            double a = m[i] / d[i], b = m[i + 1] / d[i], h2 = a * a + b * b;
            if (h2 > 9) { double t = 3 / Math.Sqrt(h2); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
        }
        var lut = new float[256];
        int k = 0;
        for (int j = 0; j < 256; j++)
        {
            double x = j / 255.0;
            while (k < n - 2 && x > P[k + 1][0]) k++;
            double x0 = P[k][0], x1 = P[k + 1][0], hgt = Math.Max(1e-6, x1 - x0), t = Math.Clamp((x - x0) / hgt, 0, 1);
            double t2 = t * t, t3 = t2 * t;
            lut[j] = (float)Math.Clamp((2 * t3 - 3 * t2 + 1) * P[k][1] + (t3 - 2 * t2 + t) * hgt * m[k] + (-2 * t3 + 3 * t2) * P[k + 1][1] + (t3 - t2) * hgt * m[k + 1], 0, 1);
        }
        lock (lutCache) { if (lutCache.Count > 50) lutCache.Clear(); lutCache[key] = lut; }
        return lut;
    }

    public static double[][] ParsePts(JsonNode n)
    {
        if (n is not JsonArray a) return null;
        var l = new List<double[]>();
        foreach (var it in a) if (it is JsonArray p && p.Count >= 2) l.Add(new[] { BrushPreset.Num(p[0]) ?? 0, BrushPreset.Num(p[1]) ?? 0 });
        return l.Count >= 2 ? l.ToArray() : null;
    }
    public static JsonArray ToJson(double[][] pts) => pts == null ? null : new JsonArray(pts.Select(p => (JsonNode)new JsonArray(JsonValue.Create(Math.Round(p[0], 3)), JsonValue.Create(Math.Round(p[1], 3)))).ToArray());

    /// <summary>pressureCurve(p, preset): 지금 설정으로 (펜마다 한 번씩 부르는 곳은 PenFeel.Current() 를 잡아 두고 쓰기)</summary>
    public static double Curve(double p, BrushPreset preset = null) => PenFeel.Current().Curve(p, preset);
}

/// <summary>펜 필기감 설정 한 벌 (획을 시작할 때 읽어 둠)</summary>
public sealed class PenFeel
{
    public double PMin, PMax = 1, Gamma = 1, PressureSmooth, EuroK = 1;
    public bool StartRamp, UnionDabs = true, NoPressure;
    public string Filter = "euro";
    public double[][] Pts;
    float[] lut;

    public static PenFeel Current()
    {
        var s = App.Settings;
        var f = s["penFeel"] as JsonObject;
        var r = new PenFeel
        {
            PMin = BrushPreset.Num(f?["pmin"]) ?? 0,
            PMax = BrushPreset.Num(f?["pmax"]) ?? 1,
            PressureSmooth = BrushPreset.Num(f?["pressureSmooth"]) ?? 0,
            EuroK = BrushPreset.Num(f?["euroK"]) ?? 1,
            StartRamp = f?["startRamp"] is JsonNode sr && BrushPreset.Bool(sr),
            UnionDabs = !(f?["unionDabs"] is JsonValue uv && uv.TryGetValue<bool>(out var ub) && !ub),
            Filter = (f?["filter"] as JsonValue)?.TryGetValue<string>(out var fs) == true ? fs : "euro",
            Gamma = BrushPreset.Num(s["pressureGamma"]) ?? 1,
            NoPressure = s["penNoPressure"] is JsonNode np && BrushPreset.Bool(np),
            Pts = Pressure.ParsePts(s["pressurePts"]),
        };
        if (r.Gamma == 0) r.Gamma = 1;
        if (r.Pts != null) r.lut = Pressure.CurveLut(r.Pts);
        return r;
    }

    /// <summary>펜 필압(0~1) → 곡선을 거친 값. preset 에 따로 정한 곡선이 있으면 그것을</summary>
    public double Curve(double p, BrushPreset preset = null)
    {
        double x = Math.Clamp((p - PMin) / Math.Max(0.05, PMax - PMin), 0, 1);
        var pl = preset?.PressurePts != null && preset.PressurePts.Length >= 2 ? Pressure.CurveLut(preset.PressurePts) : lut;
        if (pl != null) return pl[(int)Math.Floor(x * 255 + 0.5)];
        return Math.Pow(x, Gamma);
    }
}
