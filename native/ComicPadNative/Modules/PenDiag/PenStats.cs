// 펜 진단 계산 (창 없이 시험할 수 있게 순수 함수로): 보고율·간격 분포·가만히 있을 때 떨림·입력→화면 지연·위치 예측 효과·필압 범위.
using System.Diagnostics;
using ComicPadNative.Modules.Gaps;

namespace ComicPadNative.Modules.PenDiagnosis;

public static class PenStats
{
    // ---- 기본 --------------------------------------------------------------------
    /// <summary>q 분위 (0~1, 가장 가까운 순위) — 정렬해서 셈</summary>
    public static double Pct(IEnumerable<double> values, double q)
    {
        var a = values as List<double> ?? values.ToList();
        if (a.Count == 0) return double.NaN;
        var s = a.OrderBy(x => x).ToList();
        return s[Math.Clamp((int)Math.Ceiling(q * s.Count) - 1, 0, s.Count - 1)];
    }
    public static double Median(IEnumerable<double> v) => Pct(v, 0.5);
    public static double Mean(IEnumerable<double> v) { double s = 0; int n = 0; foreach (var x in v) { s += x; n++; } return n == 0 ? double.NaN : s / n; }
    public static double Std(IList<double> a) { if (a.Count < 2) return 0; double m = a.Average(); return Math.Sqrt(a.Sum(v => (v - m) * (v - m)) / a.Count); }

    // ---- 보고율 ------------------------------------------------------------------
    /// <summary>획 안의 점 간격 (ms). 같은 시각으로 겹쳐 온 점(0.05ms 미만)·끊김(100ms 넘음)은 뺌</summary>
    public static List<double> Intervals(IEnumerable<List<PenSample>> strokes)
    {
        var r = new List<double>();
        foreach (var s in strokes)
            for (int i = 1; i < s.Count; i++)
            {
                double dt = s[i].T - s[i - 1].T;
                if (dt >= 0.05 && dt <= 100) r.Add(dt);
            }
        return r;
    }

    /// <summary>보고율 (Hz) = 1000 / 간격 가운데값</summary>
    public static double RateHz(IEnumerable<double> intervals) { double m = Median(intervals); return double.IsNaN(m) || m <= 0 ? 0 : 1000 / m; }

    /// <summary>간격 분포: binMs 칸 bins 개, 마지막 칸 = 그 이상</summary>
    public static int[] Histogram(IEnumerable<double> intervals, double binMs = 1, int bins = 24)
    {
        var h = new int[bins];
        foreach (var v in intervals) h[Math.Clamp((int)Math.Floor(v / binMs), 0, bins - 1)]++;
        return h;
    }

    // ---- 가만히 있을 때 떨림 -----------------------------------------------------
    public sealed record HoldResult(double JitterPx, double MaxDevPx, double DurationMs, int N, double PNoise, (double dx, double dy)[] Offsets)
    {
        public static readonly HoldResult None = new(double.NaN, double.NaN, 0, 0, double.NaN, Array.Empty<(double, double)>());
        public bool Found => N > 0;
    }

    /// <summary>
    /// 펜을 대고 가만히 있던 구간 찾기: 한 점에서 radius 안에 minMs 넘게 머문 구간 (대는 순간 skipMs 는 뺌).
    /// 떨림 = 구간 가운데에서 떨어진 거리의 RMS (여러 구간이면 점 개수로 가중), 필압 흔들림 = 이웃 점 필압 차이의 표준편차.
    /// </summary>
    public static HoldResult Hold(IEnumerable<List<PenSample>> strokes, double radius = 3, double minMs = 300, double skipMs = 100)
    {
        double sumSq = 0, maxDev = 0, dur = 0; int n = 0;
        var pDiffs = new List<double>();
        (double, double)[] best = null; double bestDur = 0;
        foreach (var s in strokes)
        {
            int i = 0;
            while (i < s.Count)
            {
                int j = i;
                while (j + 1 < s.Count && Dist(s[j + 1], s[i]) <= radius) j++;
                if (s[j].T - s[i].T >= minMs)
                {
                    // 획 첫머리 (대는 순간)는 skipMs 만큼 뺌
                    int a = i;
                    if (i == 0) while (a < j && s[a].T - s[0].T < skipMs) a++;
                    int cnt = j - a + 1;
                    if (cnt >= 5)
                    {
                        double cx = 0, cy = 0;
                        for (int k = a; k <= j; k++) { cx += s[k].X; cy += s[k].Y; }
                        cx /= cnt; cy /= cnt;
                        var offs = new (double, double)[cnt];
                        for (int k = a; k <= j; k++)
                        {
                            double dx = s[k].X - cx, dy = s[k].Y - cy, d2 = dx * dx + dy * dy;
                            sumSq += d2; maxDev = Math.Max(maxDev, Math.Sqrt(d2)); offs[k - a] = (dx, dy);
                            if (k > a) pDiffs.Add(s[k].P - s[k - 1].P);
                        }
                        n += cnt;
                        double d = s[j].T - s[a].T; dur += d;
                        if (d > bestDur) { bestDur = d; best = offs; }
                    }
                    i = j + 1;
                }
                else i++;
            }
        }
        if (n == 0) return HoldResult.None;
        return new HoldResult(Math.Sqrt(sumSq / n), maxDev, dur, n, Std(pDiffs), best ?? Array.Empty<(double, double)>());
    }
    static double Dist(in PenSample a, in PenSample b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---- 지연 --------------------------------------------------------------------
    public sealed record LatResult(int N, double Avg, double P95, double Median, double Queue, double Draw)
    {
        public static readonly LatResult None = new(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
    }

    /// <summary>
    /// 입력 → 화면 지연: 메시지 마지막 점의 입력 시각(QPC) → 그린 것을 창에 올린 시각. 펜이 닿아 있던 장만.
    /// Queue = 입력 시각 → 메시지를 받은 시각 (Windows 가 넘겨줄 때까지), Draw = 받은 시각 → 창에 올린 시각 (그리기).
    /// predicted 를 주면 예측 선을 그린 장만(true)/안 그린 장만(false).
    /// </summary>
    public static LatResult Latency(IEnumerable<PenFrame> frames, bool? predicted = null)
    {
        var lat = new List<double>(); var q = new List<double>(); var d = new List<double>();
        foreach (var f in frames)
        {
            if (f.Pts.Count == 0 || !f.Qpc || double.IsNaN(f.PresentT) || !f.AnyContact) continue;
            if (predicted != null && f.Predicted != predicted.Value) continue;
            double l = f.PresentT - f.Last.T;
            if (l < 0 || l > 500) continue;
            lat.Add(l); q.Add(f.RecvT - f.Last.T); d.Add(f.PresentT - f.RecvT);
        }
        if (lat.Count == 0) return LatResult.None;
        return new LatResult(lat.Count, lat.Average(), Pct(lat, 0.95), Median(lat), q.Average(), d.Average());
    }

    /// <summary>시각 목록(ms) 만으로 평균·95% (그림판 실측용)</summary>
    public static LatResult FromValues(IList<double> lat) => lat.Count == 0 ? LatResult.None : new LatResult(lat.Count, lat.Average(), Pct(lat, 0.95), Median(lat), double.NaN, double.NaN);

    // ---- 펜 위치 예측 효과 -------------------------------------------------------
    public sealed record PredResult(int N, double GainMs, double ErrPx, double LagPx, double Coverage)
    {
        public static readonly PredResult None = new(0, 0, double.NaN, double.NaN, 0);
    }

    /// <summary>
    /// 그림판과 같은 예측(Gaps.PenPredict, 한 화면 장 앞)을 모은 획에 그대로 돌려 봄:
    ///  예측 없음 = 선 끝이 leadMs 뒤의 실제 펜보다 LagPx 만큼 뒤처짐, 예측 있음 = 예측 끝이 실제 펜과 ErrPx 만큼 어긋남.
    ///  줄어든 체감 지연 GainMs = leadMs × (1 − ErrPx / LagPx)  (완벽하면 leadMs, 엉뚱하면 0 이하)
    /// </summary>
    public static PredResult Prediction(IEnumerable<List<PenSample>> strokes, double leadMs = 16)
    {
        double sumErr = 0, sumLag = 0; int n = 0, eligible = 0;
        var pp = new PenPredict { LeadMs = leadMs };
        foreach (var s in strokes)
        {
            if (s.Count < 4) continue;
            pp.Reset();
            for (int k = 0; k < s.Count; k++)
            {
                pp.Add(s[k].X, s[k].Y, (long)Math.Round(s[k].T * Stopwatch.Frequency / 1000.0));
                double tf = s[k].T + leadMs;
                if (tf > s[^1].T) break;
                if (!At(s, k, tf, out double fx, out double fy)) continue;
                double lag = Math.Sqrt((fx - s[k].X) * (fx - s[k].X) + (fy - s[k].Y) * (fy - s[k].Y));
                if (lag < 0.5) continue;   // 거의 멈춤: 예측할 것이 없음
                eligible++;
                var pr = pp.Points;
                if (pr == null) continue;
                var (px, py) = pr[^1];
                sumErr += Math.Sqrt((fx - px) * (fx - px) + (fy - py) * (fy - py));
                sumLag += lag; n++;
            }
        }
        if (n == 0) return PredResult.None with { Coverage = 0 };
        double err = sumErr / n, lagAvg = sumLag / n;
        double gain = Math.Clamp(leadMs * (1 - err / Math.Max(1e-9, lagAvg)), -leadMs, leadMs);
        return new PredResult(n, gain, err, lagAvg, eligible == 0 ? 0 : n / (double)eligible);
    }

    /// <summary>획에서 시각 t 의 위치 (k 번째 점부터 앞으로 찾아 사이를 곧게 이음)</summary>
    static bool At(List<PenSample> s, int k, double t, out double x, out double y)
    {
        for (int i = Math.Max(1, k + 1); i < s.Count; i++)
            if (s[i].T >= t)
            {
                var a = s[i - 1]; var b = s[i]; double span = b.T - a.T, u = span <= 0 ? 1 : (t - a.T) / span;
                x = a.X + (b.X - a.X) * u; y = a.Y + (b.Y - a.Y) * u; return true;
            }
        x = y = 0; return false;
    }

    // ---- 필압 --------------------------------------------------------------------
    public sealed record PressureResult(int N, int Levels, double Lo, double Hi, double Min, double Max, double FinalLo, double FinalHi)
    {
        public static readonly PressureResult None = new(0, 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
    }

    /// <summary>닿은 점의 필압 (장치 값 1/1024 단계): 단계 수, 2%·98% 범위. final = 사용자 곡선·다듬기를 거친 값 (획마다 처음부터)</summary>
    public static PressureResult PressureRange(IEnumerable<List<PenSample>> strokes, Func<FeelPipe> pipe = null)
    {
        var raw = new List<double>(); var fin = new List<double>(); var lv = new HashSet<int>();
        foreach (var s in strokes)
        {
            var fp = pipe?.Invoke();
            foreach (var p in s)
            {
                if (!p.Contact) continue;
                raw.Add(p.P); lv.Add((int)Math.Round(p.P * 1024));
                if (fp != null) fin.Add(fp.Final(p.P));
            }
        }
        if (raw.Count == 0) return PressureResult.None;
        return new PressureResult(raw.Count, lv.Count, Pct(raw, 0.02), Pct(raw, 0.98), raw.Min(), raw.Max(),
            fin.Count > 0 ? Pct(fin, 0.05) : double.NaN, fin.Count > 0 ? Pct(fin, 0.98) : double.NaN);
    }

    /// <summary>최근 windowMs 동안 닿은 점의 보고율 (실시간 표시)</summary>
    public static double RecentRate(DiagSession s, double windowMs = 1000)
    {
        var cur = s.Cur ?? (s.Strokes.Count > 0 ? s.Strokes[^1] : null);
        if (cur == null || cur.Count < 3) return 0;
        double t1 = cur[^1].T; var iv = new List<double>();
        for (int i = cur.Count - 1; i > 0 && t1 - cur[i].T < windowMs; i--) { double dt = cur[i].T - cur[i - 1].T; if (dt >= 0.05 && dt <= 100) iv.Add(dt); }
        return iv.Count < 2 ? 0 : RateHz(iv);
    }
}
