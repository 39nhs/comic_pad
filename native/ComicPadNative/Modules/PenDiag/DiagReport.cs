// 펜 진단 결과: 모은 것을 한 번에 계산(DiagSummary) → 알기 쉬운 판정(Check: 좋음·주의·문제·참고·기다림 + 고치는 법) → 보낼 수 있는 보고서(글·JSON).
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.PenDiagnosis;

public enum Verdict { Ok, Warn, Bad, Info, Wait }

public sealed record Check(string Id, Verdict V, string Title, string Value, string Hint);

public sealed class DiagSummary
{
    public string Type = "";
    public int Strokes, Points, ContactPoints, Messages, MaxBatch;
    public double AvgBatch, RateHz, RateMean;
    public List<double> Intervals = new();
    public int[] Hist = new int[24];
    public double HistBinMs = 1;
    public PenStats.HoldResult Hold = PenStats.HoldResult.None;
    public PenStats.LatResult Lat = PenStats.LatResult.None, LatPredOn = PenStats.LatResult.None, LatPredOff = PenStats.LatResult.None, Canvas = PenStats.LatResult.None;
    public PenStats.PredResult Pred = PenStats.PredResult.None;
    public PenStats.PressureResult Pressure = PenStats.PressureResult.None;
    public bool PressureMask, TiltMask, TiltValue, TwistMask, Himetric, Qpc, Pen;
    public PenBtn Buttons;

    /// <summary>모은 것 → 계산 (pipe = 사용자 필압 설정, canvasLat = 그림판에서 잰 지연 ms 목록)</summary>
    public static DiagSummary Of(DiagSession s, Func<FeelPipe> pipe = null, IList<double> canvasLat = null)
    {
        var strokes = s.AllStrokes().ToList();
        var d = new DiagSummary
        {
            Type = s.SawPen ? "pen" : s.Types.Count == 0 ? "" : string.Join("+", s.Types.OrderBy(x => x)),
            Strokes = strokes.Count, Points = s.Points, ContactPoints = s.ContactPoints, Messages = s.Messages, MaxBatch = s.MaxBatch,
            AvgBatch = s.Messages == 0 ? 0 : s.Points / (double)s.Messages,
            PressureMask = s.SawPressureMask, TiltMask = s.SawTiltMask, TiltValue = s.SawTiltValue, TwistMask = s.SawTwistMask, Himetric = s.SawHimetric, Qpc = s.AllQpc, Pen = s.SawPen,
            Buttons = s.SeenButtons,
        };
        d.Intervals = PenStats.Intervals(strokes);
        d.RateHz = PenStats.RateHz(d.Intervals);
        var means = strokes.Where(x => x.Count > 10 && x[^1].T > x[0].T).Select(x => (x.Count - 1) * 1000 / (x[^1].T - x[0].T)).ToList();
        d.RateMean = means.Count == 0 ? 0 : PenStats.Median(means);
        d.Hist = PenStats.Histogram(d.Intervals, d.HistBinMs, 24);
        d.Hold = PenStats.Hold(strokes);
        d.Lat = PenStats.Latency(s.Frames);
        d.LatPredOn = PenStats.Latency(s.Frames, true);
        d.LatPredOff = PenStats.Latency(s.Frames, false);
        d.Pred = PenStats.Prediction(strokes);
        d.Pressure = PenStats.PressureRange(strokes, pipe);
        if (canvasLat != null) d.Canvas = PenStats.FromValues(canvasLat);
        return d;
    }

    static string F(double v, string fmt = "0.0") => double.IsNaN(v) ? "-" : v.ToString(fmt, CultureInfo.InvariantCulture);

    // ---- 판정 --------------------------------------------------------------------
    public const string InkHint = "와콤·휴이온·XP-Pen 등 PC 용 태블릿은 드라이버 설정에서 \"Windows Ink\" 를 켜야 필압·기울기가 옵니다.";

    public List<Check> Checks()
    {
        var r = new List<Check>();
        bool any = Messages > 0;
        // 입력 장치
        if (!any) r.Add(new("type", Verdict.Wait, "펜으로 그려 보세요", "", "왼쪽 칸에 펜으로 여러 번 그어 보면 아래 항목이 채워집니다."));
        else if (Pen) r.Add(new("type", Verdict.Ok, "펜으로 들어옵니다", Type, ""));
        else if (Type.Contains("touch")) r.Add(new("type", Verdict.Warn, "펜이 아니라 손가락(터치)으로 들어옵니다", Type, "펜으로 그려 보세요. 펜인데도 이렇게 나오면 " + InkHint));
        else r.Add(new("type", Verdict.Bad, "펜이 마우스로 들어옵니다", Type, "펜이 마우스로 들어옵니다 → " + InkHint));

        // 필압이 들어오는지
        var pr = Pressure;
        if (Pen)
        {
            if (!PressureMask) r.Add(new("pressure", Verdict.Bad, "필압이 들어오지 않습니다", "드라이버가 필압을 보내지 않음", InkHint + " 그래도 안 되면 펜 드라이버를 다시 설치하거나, 설정 > 펜 > \"필압 없이 쓰기\" 를 켜세요."));
            else if (pr.N < 30) r.Add(new("pressure", Verdict.Wait, "필압 확인 중", $"{pr.N}점", "약하게·세게 섞어서 몇 번 더 그어 보세요."));
            else if (pr.Levels < 20 || pr.Hi - pr.Lo < 0.15) r.Add(new("pressure", Verdict.Warn, "필압 변화가 거의 없습니다", $"{pr.Levels}단계, {F(pr.Lo, "0.00")}~{F(pr.Hi, "0.00")}", "약하게·세게 번갈아 그어 보세요. 그래도 같으면 필압이 거의 안 들어오는 것입니다 (Windows Ink/드라이버 확인)."));
            else r.Add(new("pressure", Verdict.Ok, "필압이 들어옵니다", $"{pr.Levels}단계, 장치 값 {F(pr.Lo, "0.00")}~{F(pr.Hi, "0.00")}", ""));
        }
        // 0~1 전체를 쓰는지 (사용자 곡선을 거친 값)
        if (Pen && PressureMask)
        {
            if (Strokes < 3 || pr.N < 150) r.Add(new("range", Verdict.Wait, "필압 범위 확인 중", "", "가장 약하게, 가장 세게 그은 획이 몇 개 있어야 압니다."));
            else
            {
                string v = $"약하게 {F(pr.FinalLo * 100, "0")}% ~ 세게 {F(pr.FinalHi * 100, "0")}%";
                if (pr.FinalHi < 0.9) r.Add(new("range", Verdict.Warn, "세게 눌러도 굵기가 끝까지 가지 않습니다", v, "\"필압 곡선\" 탭의 자동 맞추기를 해 보세요 (또는 \"가장 센 힘\" 을 낮추기)."));
                else if (pr.FinalLo > 0.25) r.Add(new("range", Verdict.Warn, "약하게 그어도 처음부터 굵습니다", v, "\"필압 곡선\" 탭의 자동 맞추기를 해 보세요 (또는 \"가장 약한 힘\" 을 높이기)."));
                else r.Add(new("range", Verdict.Ok, "필압이 0~1 전체를 씁니다", v, ""));
            }
        }
        // 보고율
        if (Intervals.Count < 50) r.Add(new("rate", Verdict.Wait, "보고율 확인 중", "", "몇 번 더 길게 그어 보세요."));
        else
        {
            string v = $"{F(RateHz, "0")}Hz (평균 {F(RateMean, "0")}Hz)";
            if (!Pen) r.Add(new("rate", Verdict.Info, "보고율 (펜이 아님)", v, "마우스·터치의 값이라 펜과 다를 수 있습니다."));
            else if (RateHz >= 120) r.Add(new("rate", Verdict.Ok, "보고율 정상 (≥120Hz)", v, ""));
            else if (RateHz >= 60) r.Add(new("rate", Verdict.Warn, "보고율이 낮습니다 (120Hz 미만)", v, "블루투스 연결이면 유선(USB)으로, 노트북은 전원 연결·고성능 모드로. 드라이버에 보고율(report rate) 설정이 있으면 높이세요. 선이 각지면 흔들림 보정을 조금 더."));
            else r.Add(new("rate", Verdict.Bad, "보고율이 매우 낮습니다 (60Hz 미만)", v, "펜 신호가 띄엄띄엄 옵니다. 유선 연결·드라이버 재설치·" + InkHint));
        }
        // 지연
        if (!Qpc && Messages > 0) r.Add(new("qpc", Verdict.Warn, "입력 시각(QPC)이 오지 않습니다", "", "이 장치는 점마다 정확한 시각을 주지 않아 지연·보고율이 대략값입니다."));
        if (Lat.N < 30) r.Add(new("latency", Verdict.Wait, "지연 확인 중", "", "몇 번 더 그어 보세요."));
        else
        {
            string v = $"가운데 {F(Lat.Median)}ms · 평균 {F(Lat.Avg)}ms · 95% {F(Lat.P95)}ms";
            if (Canvas.N >= 30) v += $" (그림판 평균 {F(Canvas.Avg)}ms · 95% {F(Canvas.P95)}ms)";
            const string note = " (화면 합성·모니터 표시 시간은 빠진 값, 보통 여기에 +1~2 화면 장)";
            if (Lat.P95 <= 16) r.Add(new("latency", Verdict.Ok, $"지연 ~{F(Lat.Median, "0")}ms", v, "앱 안에서 걸리는 시간이 짧습니다." + note));
            else if (Lat.P95 <= 33) r.Add(new("latency", Verdict.Warn, $"지연 ~{F(Lat.Median, "0")}ms (가끔 늦음)", v, "노트북은 전원 연결·고성능 모드로, 그래픽 드라이버를 최신으로. 큰 브러시·큰 화면에서 늘어날 수 있습니다." + note));
            else r.Add(new("latency", Verdict.Bad, $"지연 ~{F(Lat.Median, "0")}ms (늦음)", v, "다른 무거운 프로그램을 닫고, 전원 관리 모드를 고성능으로. 그래도 늦으면 보고서를 보내 주세요." + note));
        }
        // 예측
        if (Pred.N >= 30)
        {
            string v = $"체감 지연 {F(Pred.GainMs, "0.0")}ms 줄임, 예측 오차 {F(Pred.ErrPx, "0.0")}px (예측 없을 때 뒤처짐 {F(Pred.LagPx, "0.0")}px)";
            r.Add(new("predict", Pred.GainMs > 2 ? Verdict.Ok : Verdict.Info, Pred.GainMs > 2 ? "펜 위치 예측이 도움이 됩니다" : "펜 위치 예측 효과가 적습니다", v,
                Pred.GainMs > 2 ? "설정 > 펜 > \"펜 위치 예측\" 을 켜 두세요." : "손이 자주 방향을 바꾸면 예측이 빗나갑니다. 예측 선이 거슬리면 꺼도 됩니다."));
        }
        // 기울기
        if (Pen)
        {
            if (TiltMask && TiltValue) r.Add(new("tilt", Verdict.Ok, "기울기 지원", TwistMask ? "기울기 + 펜 돌림" : "기울기", ""));
            else if (TiltMask) r.Add(new("tilt", Verdict.Info, "기울기 신호는 있으나 값이 0", "", "펜을 기울여서 그어 보세요."));
            else r.Add(new("tilt", Verdict.Info, "기울기 없음 (이 펜·드라이버가 보내지 않음)", "", "납작 펜 각도 등은 획 방향으로 대신합니다. " + InkHint));
        }
        // 가만히 떨림
        if (!Hold.Found) r.Add(new("jitter", Verdict.Wait, "가만히 떨림 확인 중", "", "펜을 화면에 댄 채 1초쯤 움직이지 말아 보세요."));
        else
        {
            string v = $"{F(Hold.JitterPx, "0.00")}px (최대 {F(Hold.MaxDevPx, "0.0")}px, 필압 흔들림 {F(Hold.PNoise, "0.0000")})";
            if (Hold.JitterPx < 0.6) r.Add(new("jitter", Verdict.Ok, "가만히 있을 때 안정적", v, ""));
            else if (Hold.JitterPx < 1.5) r.Add(new("jitter", Verdict.Warn, "가만히 있을 때 조금 떨림", v, "\"추천 보정값 적용\" 으로 흔들림 보정을 조금 강하게."));
            else r.Add(new("jitter", Verdict.Bad, "가만히 있을 때 많이 떨림", v, "\"추천 보정값 적용\" 을 누르세요. 화면 보호 필름·전자기 간섭(충전기, 모니터)도 원인일 수 있습니다."));
        }
        // 묶여 오는 점, 1픽셀보다 작은 위치, 버튼
        if (any)
        {
            r.Add(new("batch", Verdict.Info, "묶여 오는 점도 빠짐없이 씁니다", $"메시지당 평균 {F(AvgBatch, "0.0")}점 (최대 {MaxBatch}점)", ""));
            if (Pen) r.Add(new("himetric", Himetric ? Verdict.Ok : Verdict.Info, Himetric ? "1픽셀보다 작은 위치까지 받습니다" : "위치는 1픽셀 단위", "", Himetric ? "" : "장치가 정밀 좌표를 주지 않거나 화면 배율이 맞지 않습니다. 선이 계단처럼 보이면 흔들림 보정을 조금 더."));
            var b = new List<string>();
            if ((Buttons & PenBtn.Side1) != 0) b.Add("측면 1");
            if ((Buttons & PenBtn.Side2) != 0) b.Add("측면 2");
            if ((Buttons & PenBtn.Eraser) != 0) b.Add("지우개 끝");
            if (Pen) r.Add(new("buttons", Verdict.Info, b.Count > 0 ? "인식된 버튼: " + string.Join("·", b) : "측면 버튼·지우개 끝은 아직 안 눌림", "", b.Count > 0 ? "" : "측면 버튼을 누른 채 펜을 띄우거나 그어 보면 여기에 나옵니다. 안 나오면 드라이버에서 그 버튼을 \"클릭\"·\"오른쪽 클릭\" 등으로 두세요."));
        }
        return r;
    }

    // ---- 보고서 ------------------------------------------------------------------
    public JsonObject ToJson(List<Check> checks = null, PressureFit.FitResult fit = null)
    {
        checks ??= Checks();
        JsonNode N(double v, int dp = 2) => double.IsNaN(v) || double.IsInfinity(v) ? null : JsonValue.Create(Math.Round(v, dp));
        var o = new JsonObject
        {
            ["format"] = "comicpad-pendiag", ["version"] = 1, ["time"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            ["os"] = Environment.OSVersion.VersionString, ["dotnet"] = Environment.Version.ToString(),
            ["screens"] = new JsonArray(Screen.AllScreens.Select(sc => (JsonNode)$"{sc.Bounds.Width}x{sc.Bounds.Height}{(sc.Primary ? " (주)" : "")}").ToArray()),
            ["device"] = new JsonObject { ["type"] = Type, ["pressure"] = PressureMask, ["tilt"] = TiltMask, ["tiltValues"] = TiltValue, ["twist"] = TwistMask, ["himetric"] = Himetric, ["qpc"] = Qpc, ["buttons"] = Buttons.ToString() },
            ["counts"] = new JsonObject { ["strokes"] = Strokes, ["messages"] = Messages, ["points"] = Points, ["contactPoints"] = ContactPoints, ["avgBatch"] = N(AvgBatch), ["maxBatch"] = MaxBatch },
            ["rate"] = new JsonObject { ["medianHz"] = N(RateHz, 1), ["meanHz"] = N(RateMean, 1), ["histBinMs"] = HistBinMs, ["hist"] = new JsonArray(Hist.Select(h => (JsonNode)h).ToArray()) },
            ["pressure"] = new JsonObject { ["levels"] = Pressure.Levels, ["p02"] = N(Pressure.Lo, 3), ["p98"] = N(Pressure.Hi, 3), ["min"] = N(Pressure.Min, 3), ["max"] = N(Pressure.Max, 3), ["finalP05"] = N(Pressure.FinalLo, 3), ["finalP98"] = N(Pressure.FinalHi, 3) },
            ["hold"] = new JsonObject { ["jitterPx"] = N(Hold.JitterPx, 3), ["maxDevPx"] = N(Hold.MaxDevPx, 2), ["ms"] = N(Hold.DurationMs, 0), ["pressureNoise"] = N(Hold.PNoise, 5) },
            ["latency"] = new JsonObject
            {
                ["n"] = Lat.N, ["avgMs"] = N(Lat.Avg), ["p95Ms"] = N(Lat.P95), ["medianMs"] = N(Lat.Median), ["queueMs"] = N(Lat.Queue), ["drawMs"] = N(Lat.Draw),
                ["predictOn"] = new JsonObject { ["n"] = LatPredOn.N, ["avgMs"] = N(LatPredOn.Avg), ["p95Ms"] = N(LatPredOn.P95) },
                ["predictOff"] = new JsonObject { ["n"] = LatPredOff.N, ["avgMs"] = N(LatPredOff.Avg), ["p95Ms"] = N(LatPredOff.P95) },
                ["canvas"] = new JsonObject { ["n"] = Canvas.N, ["avgMs"] = N(Canvas.Avg), ["p95Ms"] = N(Canvas.P95) },
            },
            ["prediction"] = new JsonObject { ["n"] = Pred.N, ["gainMs"] = N(Pred.GainMs), ["errPx"] = N(Pred.ErrPx), ["lagPx"] = N(Pred.LagPx), ["coverage"] = N(Pred.Coverage) },
            ["checks"] = new JsonArray(checks.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["verdict"] = c.V.ToString(), ["title"] = c.Title, ["value"] = c.Value, ["hint"] = c.Hint }).ToArray()),
            ["settings"] = new JsonObject
            {
                ["penFeel"] = App.Settings["penFeel"]?.DeepClone(), ["pressurePts"] = App.Settings["pressurePts"]?.DeepClone(), ["pressureGamma"] = App.Settings["pressureGamma"]?.DeepClone(),
                ["penNoPressure"] = App.Settings["penNoPressure"]?.DeepClone(), ["palmGuardMs"] = App.Settings["palmGuardMs"]?.DeepClone(), ["penButton"] = App.Settings["penButton"]?.DeepClone(), ["penButton2"] = App.Settings["penButton2"]?.DeepClone(),
            },
        };
        if (fit != null) o["autoFit"] = new JsonObject { ["pmin"] = fit.PMin, ["pmax"] = fit.PMax, ["pts"] = BrushEngine.Pressure.ToJson(fit.Pts), ["light"] = N(fit.Light, 3), ["medium"] = N(fit.Medium, 3), ["strong"] = N(fit.Strong, 3) };
        return o;
    }

    static string Mark(Verdict v) => v switch { Verdict.Ok => "[좋음]", Verdict.Warn => "[주의]", Verdict.Bad => "[문제]", Verdict.Wait => "[확인 중]", _ => "[참고]" };

    public string ToText(List<Check> checks = null, PressureFit.FitResult fit = null)
    {
        checks ??= Checks();
        var sb = new StringBuilder();
        sb.AppendLine("ComicPad 펜 진단 보고서");
        sb.AppendLine($"시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   Windows: {Environment.OSVersion.VersionString}");
        sb.AppendLine($"입력: {(Type == "" ? "-" : Type)}   획 {Strokes}개 · 메시지 {Messages}개 · 점 {Points}개 (메시지당 평균 {F(AvgBatch)}, 최대 {MaxBatch})");
        sb.AppendLine();
        foreach (var c in checks) { sb.AppendLine($"{Mark(c.V)} {c.Title}{(c.Value != "" ? " — " + c.Value : "")}"); if (c.Hint != "") sb.AppendLine("        → " + c.Hint); }
        sb.AppendLine();
        sb.AppendLine($"보고율: 가운데 {F(RateHz, "0")}Hz, 평균 {F(RateMean, "0")}Hz");
        sb.AppendLine("점 간격 분포 (1ms 칸): " + string.Join(" ", Hist.Select((h, i) => $"{i}:{h}").Where((_, i) => Hist[i] > 0)));
        sb.AppendLine($"지연 (입력→창에 올림): 평균 {F(Lat.Avg)}ms, 95% {F(Lat.P95)}ms, 가운데 {F(Lat.Median)}ms (Windows 전달 {F(Lat.Queue)}ms + 그리기 {F(Lat.Draw)}ms), {Lat.N}장");
        sb.AppendLine($"  예측 선 켬 {F(LatPredOn.Avg)}ms ({LatPredOn.N}장) / 끔 {F(LatPredOff.Avg)}ms ({LatPredOff.N}장), 예측으로 줄어든 체감 지연 {F(Pred.GainMs)}ms (오차 {F(Pred.ErrPx)}px)");
        if (Canvas.N > 0) sb.AppendLine($"  그림판에서 잰 지연: 평균 {F(Canvas.Avg)}ms, 95% {F(Canvas.P95)}ms ({Canvas.N}장)");
        sb.AppendLine($"필압: {Pressure.Levels}단계, 장치 값 2~98% {F(Pressure.Lo, "0.000")}~{F(Pressure.Hi, "0.000")}, 곡선 거친 값 5~98% {F(Pressure.FinalLo, "0.00")}~{F(Pressure.FinalHi, "0.00")}");
        sb.AppendLine($"가만히 떨림: {F(Hold.JitterPx, "0.000")}px RMS (최대 {F(Hold.MaxDevPx, "0.00")}px, {F(Hold.DurationMs, "0")}ms)");
        if (fit != null) sb.AppendLine($"자동 맞추기: 약하게 {F(fit.Light, "0.00")} · 보통 {F(fit.Medium, "0.00")} · 세게 {F(fit.Strong, "0.00")} → 범위 {fit.PMin}~{fit.PMax}, 곡선 {PressureFit.ToCspText(fit.Pts)}");
        sb.AppendLine();
        sb.AppendLine("---- JSON ----");
        sb.AppendLine(ToJson(checks, fit).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return sb.ToString();
    }
}
