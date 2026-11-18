// 펜 필기감 진단 계산 (웹 판 ui/pendiag.js 의 analyze): 네 단계로 모은 획 → 필압 범위·보정값 추천 (settings.penFeel).
// 창은 Modules/PenDiag/PenDiagWindow (실시간 값·그래프·지연·필압 곡선 도구) 로 바뀜. 이 계산은 웹 판과 맞춰 보는 시험(ExtrasTest)용으로 남김.
using System.Text.Json.Nodes;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public sealed record PenPt(double T, double X, double Y, double P, double Tx, double Ty);

public static class PenDiag
{
    public static readonly (string id, string title, string help)[] Steps =
    {
        ("free", "1. 자유롭게 그어 보세요", "평소처럼 빠르게·느리게 여러 번 그어 주세요 (5초 이상)."),
        ("light", "2. 가장 약하게", "선이 겨우 그어질 만큼 가장 약하게 몇 번 그어 주세요."),
        ("strong", "3. 가장 세게", "평소 가장 세게 누를 때만큼 눌러서 몇 번 그어 주세요."),
        ("hold", "4. 가만히 대고 있기", "펜을 화면에 댄 채 2초 동안 움직이지 마세요."),
    };

    public static double Pct(IEnumerable<double> arr, double q) { var a = arr.OrderBy(x => x).ToList(); return a.Count == 0 ? 0 : a[Math.Min(a.Count - 1, (int)Math.Floor(q * a.Count))]; }
    public static double Median(IEnumerable<double> a) => Pct(a, 0.5);
    public static double Std(IList<double> a) { if (a.Count < 2) return 0; double m = a.Average(); return Math.Sqrt(a.Sum(v => (v - m) * (v - m)) / a.Count); }

    public sealed class Meta { public int Events, Coalesced; public bool Predicted, Tilt; public List<double> Lat = new(); public string Type = ""; }

    public sealed record Result(double Rate, int Levels, double Light, double Strong, double Jitter, double PNoise, double Lat, bool PressureOK, JsonObject Rec);

    /// <summary>모은 획으로 결과·추천값 계산 (웹 판 analyze 와 같은 식)</summary>
    public static Result Analyze(Dictionary<string, List<List<PenPt>>> data, Meta meta, double scale = 1)
    {
        var all = data["free"].SelectMany(s => s).ToList();
        var rates = data["free"].Where(s => s.Count > 10).Select(s => (s.Count - 1) / ((s[^1].T - s[0].T) / 1000)).Where(double.IsFinite).ToList();
        double rate = Median(rates);
        var ps = all.Select(q => q.P).ToList();
        int levels = ps.Select(p => p.ToString("0.0000")).Distinct().Count();
        var lightP = data["light"].SelectMany(s => s).Select(q => q.P).Where(p => p > 0).ToList();
        var strongP = data["strong"].SelectMany(s => s).Select(q => q.P).Where(p => p > 0).ToList();
        // 대는 순간(처음 0.3초)은 빼고 (웹 판: 모든 가만히 획을 이어 붙인 첫 점 기준)
        var holdAll = data["hold"].SelectMany(s => s).ToList();
        var hold = holdAll.Where(q => holdAll.Count > 0 && q.T - holdAll[0].T > 300).ToList();
        double jitter = Math.Sqrt(Math.Pow(Std(hold.Select(q => q.X).ToList()), 2) + Math.Pow(Std(hold.Select(q => q.Y).ToList()), 2)) / scale;
        double pNoise = Std(hold.Skip(1).Select((q, i) => q.P - hold[i].P).ToList());
        double lat = Median(meta.Lat);
        bool pressureOK = levels > 20 && Pct(ps, 0.9) - Pct(ps, 0.1) > 0.05;
        double pmin = pressureOK ? Math.Max(0, Math.Min(0.4, Pct(lightP, 0.25) * 0.8)) : 0;
        double pmax = pressureOK ? Math.Min(1, Math.Max(pmin + 0.3, Pct(strongP, 0.9))) : 1;
        if (pmax - pmin < 0.3) pmax = Math.Min(1, pmin + 0.3);
        double euroK = jitter < 0.25 ? 1.4 : jitter < 0.6 ? 1.1 : jitter < 1.2 ? 0.8 : 0.55;
        double pressureSmooth = Math.Max(0.15, Math.Min(0.65, 0.2 + pNoise * 12));
        var rec = new JsonObject { ["pmin"] = Math.Round(pmin, 3), ["pmax"] = Math.Round(pmax, 3), ["euroK"] = euroK, ["pressureSmooth"] = Math.Round(pressureSmooth, 2), ["predict"] = meta.Predicted, ["startRamp"] = true };
        return new Result(rate, levels, Median(lightP), Pct(strongP, 0.9), jitter, pNoise, lat, pressureOK, rec);
    }

    public static void ApplyRec(JsonObject rec)
    {
        var f = U.Obj("penFeel");
        foreach (var (k, v) in rec) f[k] = v?.DeepClone();
        App.NotifySettings();
        App.Toast("이 펜에 맞춘 값으로 바꿨습니다. 그려 보고 어색하면 \"기본값으로\"를 누르세요");
    }
    public static void ResetFeel()
    {
        var f = U.Obj("penFeel");
        foreach (var k in new[] { "pmin", "pmax", "euroK" }) f.Remove(k);
        f["pressureSmooth"] = 0.35; f["startRamp"] = true; f["predict"] = true;
        App.NotifySettings();
        App.Toast("필기감을 기본값으로 되돌렸습니다");
    }

    /// <summary>예전 4단계 진단 창 → 새 펜 진단 창 (Modules/PenDiag)</summary>
    public static void Dialog() => PenDiagnosis.PenDiagWindow.Show();
}
