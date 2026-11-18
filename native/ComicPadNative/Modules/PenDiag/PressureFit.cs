// 필압 곡선 도구: 사용자 곡선·다듬기를 거친 필압 (브러시와 같은 계산), 자동 맞추기 (약하게·보통·세게 그은 획 → pmin·pmax·곡선),
// 클립스튜디오 필압 곡선 값 넣기 ((입력, 출력) 목록 → 곡선). 결과는 브러시가 읽는 설정 키 그대로 (penFeel.pmin/pmax, pressurePts, pressureGamma).
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Modules.BrushEngine;

namespace ComicPadNative.Modules.PenDiagnosis;

/// <summary>
/// 장치 필압 → 브러시가 쓰는 값. BrushStroke.PressureOf (펜을 대는 순간 줄이기 + 필압 다듬기) 다음 PenFeel.Curve (pmin~pmax 늘이기 + 곡선) 와 같은 식.
/// 획마다 새로 만들거나 Reset.
/// </summary>
public sealed class FeelPipe
{
    readonly PenFeel feel; readonly BrushPreset preset;
    int np; double? ps;
    public FeelPipe(PenFeel feel = null, BrushPreset preset = null) { this.feel = feel ?? PenFeel.Current(); this.preset = preset; }
    public PenFeel Feel => feel;
    public void Reset() { np = 0; ps = null; }
    /// <summary>다듬은 필압 (곡선 전)</summary>
    public double Smooth(double p)
    {
        np++;
        if (feel.StartRamp && np <= 4) p = Math.Min(p, 0.15 + np * 0.2);
        double k = 1 - Math.Min(0.9, feel.PressureSmooth);
        ps = ps == null ? p : ps + (p - ps) * k;
        return ps.Value;
    }
    /// <summary>최종 필압 (곡선까지). "필압 없이 쓰기" 면 1</summary>
    public double Final(double raw) => feel.NoPressure ? 1 : feel.Curve(Smooth(raw), preset);
}

public static class PressureFit
{
    // ---- 자동 맞추기 -------------------------------------------------------------
    public sealed record FitResult(double PMin, double PMax, double[][] Pts, double Light, double Medium, double Strong, string Note)
    {
        /// <summary>이 결과로 만든 필기감 (설정에 쓰기 전 미리 보기)</summary>
        public PenFeel Feel(PenFeel baseFeel = null)
        {
            var b = baseFeel ?? PenFeel.Current();
            var f = new PenFeel { PMin = PMin, PMax = PMax, Gamma = 1, PressureSmooth = b.PressureSmooth, EuroK = b.EuroK, StartRamp = b.StartRamp, UnionDabs = b.UnionDabs, Filter = b.Filter, Pts = Pts };
            return f;
        }
    }

    /// <summary>목표: 약하게 → 15%, 보통 → 50%, 세게 → 100%</summary>
    public const double TargetLight = 0.15, TargetMedium = 0.5;

    /// <summary>획 가운데 부분의 필압 (처음·끝 15% 는 대고 떼는 순간이라 뺌)</summary>
    public static List<double> Core(IEnumerable<List<PenSample>> strokes)
    {
        var r = new List<double>();
        foreach (var s in strokes)
        {
            var c = s.Where(p => p.Contact && p.P > 0).ToList();
            if (c.Count == 0) continue;
            int cut = c.Count >= 8 ? (int)Math.Floor(c.Count * 0.15) : 0;
            for (int i = cut; i < c.Count - cut; i++) r.Add(c[i].P);
        }
        return r;
    }

    /// <summary>
    /// 약하게·보통·세게 그은 획으로 맞추기: pmin = 약하게의 10% 값 × 0.7 (그보다 약하면 0), pmax = 세게의 90% 값,
    /// 곡선 = (0,0) (약하게, 15%) (보통, 50%) (1,1). 거의 곧으면 곡선 없음. medium 은 없어도 됨.
    /// </summary>
    public static FitResult AutoFit(IEnumerable<List<PenSample>> light, IEnumerable<List<PenSample>> medium, IEnumerable<List<PenSample>> strong)
    {
        var L = Core(light); var M = medium == null ? new List<double>() : Core(medium); var S = Core(strong);
        if (L.Count < 5 || S.Count < 5) return null;
        double l10 = PenStats.Pct(L, 0.10), lMed = PenStats.Median(L), s90 = PenStats.Pct(S, 0.90);
        double mMed = M.Count >= 5 ? PenStats.Median(M) : double.NaN;
        var notes = new List<string>();
        double pmin = Math.Clamp(l10 * 0.7, 0, 0.35);
        double pmax = Math.Clamp(s90, pmin + 0.25, 1);
        if (s90 < pmin + 0.25) notes.Add("세게와 약하게의 차이가 작습니다 — 세게를 더 세게 눌러 보세요");
        double X(double p) => Math.Clamp((p - pmin) / Math.Max(0.05, pmax - pmin), 0, 1);
        var pts = new List<double[]> { new[] { 0.0, 0.0 } };
        double xl = X(lMed), xm = double.IsNaN(mMed) ? double.NaN : X(mMed);
        bool lOk = xl > 0.02 && xl < 0.9;
        if (lOk) pts.Add(new[] { Math.Round(xl, 3), TargetLight });
        if (!double.IsNaN(xm))
        {
            if (xm > (lOk ? xl + 0.05 : 0.05) && xm < 0.95) pts.Add(new[] { Math.Round(xm, 3), TargetMedium });
            else notes.Add("보통 세기가 약하게·세게와 잘 구분되지 않아 곡선 가운데 점은 뺐습니다");
        }
        pts.Add(new[] { 1.0, 1.0 });
        // 거의 곧으면 곡선 없이 (pmin~pmax 늘이기만)
        bool straight = pts.All(p => Math.Abs(p[1] - p[0]) < 0.04);
        return new FitResult(Math.Round(pmin, 3), Math.Round(pmax, 3), straight ? null : pts.ToArray(), lMed, mMed, s90, string.Join(" · ", notes));
    }

    // ---- 클립스튜디오 필압 곡선 값 -----------------------------------------------
    /// <summary>
    /// 클립스튜디오(필압 검출 레벨 조절·브러시 필압 설정)의 곡선 점을 (입력, 출력) 목록으로 받아 곡선 점으로.
    /// 숫자만 차례로 짝지음: "0,0 30,60 100,100" · "(0, 0) (0.3, 0.6)" · 줄마다 "30 60" 모두 됨. 1보다 큰 값이 있으면 0~100 으로 보고 100 으로 나눔.
    /// 입력 순으로 정렬, 같은 입력은 나중 것, 양 끝(0·1)이 없으면 이웃 출력으로 채움.
    /// </summary>
    public static double[][] ParseCsp(string text, out string error)
    {
        error = null;
        var nums = Regex.Matches(text ?? "", @"-?\d+(?:\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count < 4) { error = "점이 두 개 이상 있어야 합니다 (예: 0,0  50,30  100,100)"; return null; }
        if (nums.Count % 2 != 0) { error = $"숫자가 {nums.Count}개라 (입력, 출력) 짝이 맞지 않습니다"; return null; }
        bool percent = nums.Any(v => v > 1.0001);
        if (nums.Any(v => v < 0 || v > (percent ? 100.0001 : 1.0001))) { error = percent ? "값은 0~100 사이여야 합니다" : "값은 0~1 사이여야 합니다"; return null; }
        var byX = new SortedDictionary<double, double>();
        for (int i = 0; i < nums.Count; i += 2)
        {
            double x = nums[i], y = nums[i + 1];
            if (percent) { x /= 100; y /= 100; }
            byX[Math.Round(Math.Clamp(x, 0, 1), 4)] = Math.Round(Math.Clamp(y, 0, 1), 4);
        }
        var pts = byX.Select(kv => new[] { kv.Key, kv.Value }).ToList();
        if (pts.Count < 2) { error = "입력 값이 서로 다른 점이 두 개 이상 있어야 합니다"; return null; }
        if (pts[0][0] > 0) pts.Insert(0, new[] { 0.0, pts[0][1] });
        if (pts[^1][0] < 1) pts.Add(new[] { 1.0, pts[^1][1] });
        return pts.ToArray();
    }

    /// <summary>곡선 점 → 글 (클립스튜디오 식 0~100)</summary>
    public static string ToCspText(double[][] pts) => pts == null ? "0,0  100,100" : string.Join("  ", pts.Select(p => $"{Math.Round(p[0] * 100, 1).ToString(CultureInfo.InvariantCulture)},{Math.Round(p[1] * 100, 1).ToString(CultureInfo.InvariantCulture)}"));

    // ---- 설정에 쓰기 -------------------------------------------------------------
    static JsonObject Feel()
    {
        if (App.Settings["penFeel"] is JsonObject f) return f;
        var n = new JsonObject { ["filter"] = "euro", ["pressureSmooth"] = 0, ["startRamp"] = false, ["predict"] = true };
        App.Settings["penFeel"] = n; return n;
    }

    /// <summary>모든 브러시가 쓰는 필압 설정을 바꿈: pmin·pmax (penFeel), 곡선 (pressurePts, 곧으면 지움), 감마 1</summary>
    public static void Apply(double pmin, double pmax, double[][] pts)
    {
        var f = Feel();
        f["pmin"] = Math.Round(pmin, 3); f["pmax"] = Math.Round(pmax, 3);
        App.Settings["pressurePts"] = pts == null || IsIdentity(pts) ? null : Pressure.ToJson(pts);
        App.Settings["pressureGamma"] = 1;
        App.NotifySettings();
    }
    public static void Apply(FitResult r) => Apply(r.PMin, r.PMax, r.Pts);
    /// <summary>클립스튜디오 곡선은 장치 필압 전체(0~1)에 거니까 pmin 0 · pmax 1 로</summary>
    public static void ApplyCsp(double[][] pts) => Apply(0, 1, pts);

    public static bool IsIdentity(double[][] p) => p.Length == 2 && p[0][0] == 0 && p[0][1] == 0 && p[1][0] == 1 && p[1][1] == 1;

    /// <summary>지금 설정 (되돌리기용 복사)</summary>
    public static JsonObject Snapshot() => new()
    {
        ["penFeel"] = App.Settings["penFeel"]?.DeepClone(), ["pressurePts"] = App.Settings["pressurePts"]?.DeepClone(), ["pressureGamma"] = App.Settings["pressureGamma"]?.DeepClone(),
    };
    public static void Restore(JsonObject snap)
    {
        foreach (var k in new[] { "penFeel", "pressurePts", "pressureGamma" })
        {
            var v = snap[k]?.DeepClone();
            if (v == null) App.Settings.Remove(k); else App.Settings[k] = v;
        }
        App.NotifySettings();
    }

    /// <summary>떨림·필압 흔들림으로 추천하는 보정값 (예전 진단과 같은 식: Extras.PenDiag.Analyze)</summary>
    public static JsonObject Recommend(PenStats.HoldResult hold, PenStats.PredResult pred)
    {
        var rec = new JsonObject();
        if (hold.Found)
        {
            double j = hold.JitterPx;
            rec["euroK"] = j < 0.25 ? 1.4 : j < 0.6 ? 1.1 : j < 1.2 ? 0.8 : 0.55;
            rec["pressureSmooth"] = Math.Round(Math.Max(0.15, Math.Min(0.65, 0.2 + (double.IsNaN(hold.PNoise) ? 0 : hold.PNoise) * 12)), 2);
        }
        rec["startRamp"] = true;
        if (pred.N > 20) rec["predict"] = pred.GainMs > 2;
        return rec;
    }
    public static void ApplyRecommend(JsonObject rec)
    {
        var f = Feel();
        foreach (var (k, v) in rec) f[k] = v?.DeepClone();
        App.NotifySettings();
    }
}
