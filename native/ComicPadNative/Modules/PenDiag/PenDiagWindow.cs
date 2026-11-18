// 펜 진단 창: 왼쪽 = 펜판(그림판과 같은 입력·실제 브러시) + 지금 값, 오른쪽 = 점검(판정·추천·보고서) / 그래프 / 필압 곡선(편집·자동 맞추기·클립스튜디오 값) / 지연.
// 몇 분 그려 보면 필압·보고율·지연·떨림·기울기·버튼이 제대로 오는지 알 수 있고, 필압 곡선을 이 펜·이 손에 맞출 수 있다.
using System.Text.Json.Nodes;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;
using ComicPadNative.UI.Dialogs;

namespace ComicPadNative.Modules.PenDiagnosis;

public sealed class PenDiagWindow : IDisposable
{
    public static void Show()
    {
        using var w = new PenDiagWindow();
        w.Form.ShowDialog(App.Main);
        App.SaveSettings();
    }

    public readonly ModalForm Form;
    public readonly DiagPad Pad = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    readonly LiveReadout readout = new() { Dock = DockStyle.Bottom };
    readonly Label hint = new() { Dock = DockStyle.Top, Height = 46, AutoSize = false, ForeColor = Theme.Text, BackColor = Theme.Panel, Padding = new Padding(0, 0, 0, 6) };
    readonly CheckList checks = new();
    readonly PressureGraph pGraph = new(); readonly IntervalGraph iGraph = new(); readonly JitterGraph jGraph = new();
    readonly TextBlock latText = new("");
    readonly CurvePreview preview = new();
    readonly TextBlock fitText = new("");
    Control fitBox; CurveEditor curve;
    readonly Dictionary<string, Control> pages = new();
    readonly Panel host;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    readonly JsonObject before = PressureFit.Snapshot();
    DiagSession S => Pad.Session;
    public DiagSummary Summary { get; private set; } = new();
    int summaryAt = -1; long summaryTick;
    string page = "checks";

    // 자동 맞추기: -1 = 자유롭게, 0 약하게, 1 보통, 2 세게
    int fitStep = -1;
    readonly List<List<PenSample>>[] fitData = { new(), new(), new() };
    const int FitStrokes = 3;
    PressureFit.FitResult pendingFit;          // 적용 전 미리 보기
    public PressureFit.FitResult PendingFit => pendingFit;
    public string HintText => hint.Text;
    public string Page => page;
    double[][] pendingCsp;
    PressureFit.FitResult lastFit;

    static readonly string[] FitTitle = { "가장 약하게", "보통 힘으로", "가장 세게" };
    static readonly string[] FitHelp = { "선이 겨우 그어질 만큼 가장 약하게", "평소 선을 그을 때 힘으로", "평소 가장 세게 누를 때만큼" };

    public PenDiagWindow()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Form = new ModalForm("펜 진단 — 필압·지연·필기감", Math.Min(1140, wa.Width - 40), Math.Min(860, wa.Height - 40)) { Resizable = true };
        var content = new Panel { BackColor = Theme.Panel };
        // 왼쪽: 안내 + 펜판 + 단추 줄 + 지금 값
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        var bar = new WebRow { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Theme.Panel, Padding = new Padding(0, 4, 0, 2) };
        bar.Controls.Add(Opt.Button("그린 것 지우기", () => Pad.ClearInk()));
        bar.Controls.Add(Opt.Button("처음부터 다시 재기", Restart));
        bar.Controls.Add(Toggle("예측 선 보이기", Pad.PredictionOn, v => Pad.PredictionOn = v));
        bar.Controls.Add(Toggle("예측 켬/끔 번갈아 (지연 비교)", false, v => Pad.AlternatePrediction = v));
        left.Controls.Add(Pad); left.Controls.Add(hint); left.Controls.Add(bar); left.Controls.Add(readout);
        // 오른쪽: 탭 + 내용 (탭 내용은 처음 볼 때 만듦 — 창이 빨리 열리게)
        var right = new Panel { Dock = DockStyle.Right, Width = 450, BackColor = Theme.Panel, Padding = new Padding(14, 0, 0, 0) };
        host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Panel }.DarkScroll();
        var seg = WebSeg.Of(new[] { ("checks", "점검"), ("graphs", "그래프"), ("curve", "필압 곡선"), ("latency", "지연") }, () => page, v => ShowPage(v));
        seg.Dock = DockStyle.Top;
        right.Controls.Add(host); right.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Panel }); right.Controls.Add(seg);
        content.Controls.Add(left); content.Controls.Add(right);
        Form.SetContent(content);
        Form.AddAction("닫기", () => true, primary: true);
        Form.Primary = null;
        Form.PerformLayout();
        ShowPage("checks");

        Pad.FeelFor = () => CandidateFeel();
        S.StrokeEnded += OnStrokeEnded;
        timer.Tick += (_, _) => Tick();
        Form.Shown += (_, _) => { timer.Start(); SetHint(); Tick(true); };
        SetHint();
        Form.FormClosed += (_, _) => timer.Stop();
    }

    public void Dispose() { timer.Dispose(); Form.Dispose(); }

    static TextBlock Help(string t) => new(t, true);

    static WebToggle Toggle(string label, bool on, Action<bool> set)
    {
        var t = new WebToggle(label) { Checked = on };
        t.CheckedChanged += (_, _) => set(t.Checked);
        return t;
    }

    public void ShowPage(string id)
    {
        page = id;
        if (!pages.ContainsKey(id))
        {
            var p = id switch { "graphs" => BuildGraphs(), "curve" => BuildCurve(), "latency" => BuildLatency(), _ => BuildChecks() };
            p.Visible = false; pages[id] = p;
            host.SuspendLayout(); host.Controls.Add(p); host.ResumeLayout(false);
        }
        foreach (var (k, p) in pages) p.Visible = k == id;
        host.PerformLayout();
        if (id == "curve") { preview.Stroke = LastStroke(); preview.Redraw(); }
        Tick(true);
    }

    // ---- 페이지 ------------------------------------------------------------------
    Control BuildChecks()
    {
        var p = Opt.Panel();
        p.Controls.Add(Opt.Title("점검 결과 (그리는 동안 바로 바뀜)"));
        p.Controls.Add(checks);
        p.Controls.Add(Opt.Row(Opt.Button("추천 보정값 적용", ApplyRecommend, true), Opt.Button("보고서 저장…", SaveReport), Opt.Button("보고서 복사", CopyReport)));
        p.Controls.Add(Help("추천 보정값: 가만히 있을 때 떨림·필압 흔들림으로 흔들림 보정과 필압 다듬기를 맞춥니다. 보고서(글 + JSON)는 문제가 있을 때 그대로 보내 주세요."));
        p.Controls.Add(Opt.Title("이렇게 그려 보세요 (2~3분)"));
        p.Controls.Add(Help("① 빠르게·느리게 긴 선 여러 개  ② 가장 약하게, 가장 세게 몇 번  ③ 펜을 대고 1초 가만히  ④ 펜을 기울여서  ⑤ 측면 버튼을 누른 채 펜을 띄우기 · 펜을 뒤집어 지우개 끝으로"));
        return p;
    }

    Control BuildGraphs()
    {
        var p = Opt.Panel();
        pGraph.Pipe = () => new FeelPipe(CandidateFeel());
        p.Controls.Add(pGraph); p.Controls.Add(iGraph); p.Controls.Add(jGraph);
        p.Controls.Add(Help("필압: 파랑 선이 0 에서 1 까지 고르게 오르내리면 좋음 (세게 눌러도 위에 닿지 않으면 \"필압 곡선\" 탭에서 자동 맞추기). " +
            "점 간격: 120Hz 줄 왼쪽에 좁게 모여 있으면 펜 신호가 촘촘하고 고름. 떨림: 점들이 작게 모일수록 좋음."));
        return p;
    }

    Control BuildCurve()
    {
        var p = Opt.Panel();
        var s = App.Settings;
        p.Controls.Add(Opt.Title("필압 곡선 — 모든 브러시에 적용"));
        curve = new CurveEditor("곡선",
            () => pendingFit != null ? pendingFit.Pts : pendingCsp ?? Pressure.ParsePts(App.Settings["pressurePts"]) ?? (BrushPreset.Num(App.Settings["pressureGamma"]) is double gm && gm != 1 && gm > 0 ? new[] { 0, 0.25, 0.5, 0.75, 1 }.Select(x => new[] { x, Math.Round(Math.Pow(x, gm), 3) }).ToArray() : null),
            pts => { DiscardPending(false); App.Settings["pressurePts"] = Pressure.ToJson(pts); App.Settings["pressureGamma"] = 1; App.NotifySettings(); preview.Redraw(); });
        p.Controls.Add(curve);
        preview.Feel = CandidateFeel;
        p.Controls.Add(preview);
        p.Controls.Add(Opt.Slider("가장 약한 힘 (이보다 약하면 0)", 0, 0.5, () => BrushPreset.Num((App.Settings["penFeel"] as JsonObject)?["pmin"]) ?? 0, v => { FeelObj()["pmin"] = Math.Round(v, 2); preview.Redraw(); }, pct: true, step: 0.01));
        p.Controls.Add(Opt.Slider("가장 센 힘 (이만큼만 눌러도 최대)", 0.4, 1, () => BrushPreset.Num((App.Settings["penFeel"] as JsonObject)?["pmax"]) ?? 1, v => { FeelObj()["pmax"] = Math.Round(v, 2); preview.Redraw(); }, pct: true, step: 0.01));
        p.Controls.Add(Opt.Row(Opt.Button("자동 맞추기 시작", StartFit, true), Opt.Button("클립스튜디오 곡선 값 넣기…", CspDialog), Opt.Button("창을 열 때 값으로", RevertAll)));
        p.Controls.Add(Help("자동 맞추기: 가장 약하게·보통·가장 세게 세 번씩 그으면, 내 손의 약한 힘 → 가는 선, 센 힘 → 가장 굵은 선이 되도록 범위와 곡선을 정합니다. " +
            "클립스튜디오: [파일 > 필압 검출 레벨 조절] 또는 브러시 필압 설정의 곡선 점을 (입력, 출력) 숫자로 넣으면 같은 느낌이 됩니다."));
        var box = new WebBox { Padding = new Padding(10, 8, 10, 8), Margin = new Padding(0, 6, 0, 6) };
        box.Controls.Add(fitText);
        box.Controls.Add(Opt.Row(Opt.Button("적용", ApplyPending, true), Opt.Button("버리기", () => DiscardPending(true))));
        box.Visible = false;
        fitBox = box;
        p.Controls.Add(box);
        return p;
    }

    Control BuildLatency()
    {
        var p = Opt.Panel();
        p.Controls.Add(Opt.Title("입력 → 화면 지연"));
        p.Controls.Add(latText);
        p.Controls.Add(Opt.Check("펜 위치 예측 (그림판 설정)", () => Gaps.PenPredict.Enabled(), v => FeelObj()["predict"] = v));
        p.Controls.Add(Opt.Row(Opt.Button("그림판 기록 지우기", () => { CanvasLatency.Clear(); Tick(true); })));
        p.Controls.Add(Help("재는 법: 펜 점마다 Windows 가 붙여 주는 입력 시각(QPC)과, 그 점을 그려 창에 올린 시각의 차이. " +
            "\"획마다 예측 켬/끔 번갈아\" 를 켜고 그으면 예측 선을 그릴 때·안 그릴 때를 나눠 잽니다. 예측 효과는 모은 획에 그림판과 같은 예측을 돌려서 예측 끝이 실제 펜과 얼마나 맞았는지로 셈. " +
            "그림판 기록은 진단 창을 열기 전에 그림판에서 그린 것 (최근 2000장)."));
        return p;
    }

    static JsonObject FeelObj()
    {
        if (App.Settings["penFeel"] is JsonObject f) return f;
        var n = new JsonObject { ["filter"] = "euro", ["pressureSmooth"] = 0, ["startRamp"] = false, ["predict"] = true };
        App.Settings["penFeel"] = n; return n;
    }

    // ---- 필기감 (적용 전 미리 보기 포함) ----------------------------------------
    PenFeel CandidateFeel()
    {
        if (pendingFit != null) return pendingFit.Feel();
        if (pendingCsp != null) return new PressureFit.FitResult(0, 1, pendingCsp, 0, 0, 0, "").Feel();
        return PenFeel.Current();
    }

    public void ApplyPending()
    {
        if (pendingFit != null) { PressureFit.Apply(pendingFit); App.Toast($"필압 범위 {pendingFit.PMin:0.00}~{pendingFit.PMax:0.00} 과 곡선을 모든 브러시에 적용했습니다"); }
        else if (pendingCsp != null) { PressureFit.ApplyCsp(pendingCsp); App.Toast("클립스튜디오 곡선을 모든 브러시에 적용했습니다"); }
        pendingFit = null; pendingCsp = null; if (fitBox != null) fitBox.Visible = false;
        curve?.Redraw(); preview.Redraw(); SetHint(); Tick(true);
    }
    void DiscardPending(bool redraw)
    {
        if (pendingFit == null && pendingCsp == null) return;
        pendingFit = null; pendingCsp = null; if (fitBox != null) fitBox.Visible = false;
        if (redraw) { curve?.Redraw(); preview.Redraw(); SetHint(); Tick(true); }
    }
    void RevertAll()
    {
        DiscardPending(false);
        PressureFit.Restore(before);
        curve?.Redraw(); preview.Redraw(); Tick(true);
        App.Toast("필압 설정을 이 창을 열 때 값으로 되돌렸습니다");
    }

    // ---- 자동 맞추기 -------------------------------------------------------------
    public void StartFit()
    {
        DiscardPending(false);
        foreach (var l in fitData) l.Clear();
        fitStep = 0;
        Pad.ClearInk();
        SetHint();
    }

    void OnStrokeEnded(List<PenSample> s)
    {
        if (fitStep < 0) return;
        if (s.Count(p => p.Contact) < 8) { SetHint("너무 짧습니다 — 조금 더 길게 그어 주세요. "); return; }
        fitData[fitStep].Add(s);
        if (fitData[fitStep].Count >= FitStrokes)
        {
            fitStep++;
            Pad.ClearInk();
            if (fitStep > 2) { FinishFit(); return; }
        }
        SetHint();
    }

    void FinishFit()
    {
        fitStep = -1;
        var r = PressureFit.AutoFit(fitData[0], fitData[1], fitData[2]);
        if (r == null) { SetHint("자동 맞추기에 필요한 점이 모자랍니다. 다시 해 주세요."); return; }
        pendingFit = r; lastFit = r; pendingCsp = null;
        ShowPage("curve");
        var fe = r.Feel();
        fitText.Text = $"자동 맞추기 결과 (아직 적용 안 함 — 지금 펜판·미리 보기에는 이 값으로 그려짐)\n" +
            $"약하게 {r.Light:0.00} → {fe.Curve(r.Light) * 100:0}%  ·  보통 {(double.IsNaN(r.Medium) ? "-" : r.Medium.ToString("0.00"))} → {(double.IsNaN(r.Medium) ? "-" : (fe.Curve(r.Medium) * 100).ToString("0"))}%  ·  세게 {r.Strong:0.00} → {fe.Curve(r.Strong) * 100:0}%\n" +
            $"범위 {r.PMin:0.00}~{r.PMax:0.00}, 곡선 {(r.Pts == null ? "곧게" : PressureFit.ToCspText(r.Pts))}" + (r.Note != "" ? "\n" + r.Note : "");
        fitBox.Visible = true;
        curve.Redraw(); preview.Redraw();
        SetHint("자동 맞추기 끝 — 펜판에 그어 보고 마음에 들면 오른쪽 [적용], 아니면 [버리기].");
    }

    // ---- 클립스튜디오 곡선 -------------------------------------------------------
    void CspDialog()
    {
        var cur = pendingCsp ?? Pressure.ParsePts(App.Settings["pressurePts"]);
        var r = Dlg.Form("클립스튜디오 필압 곡선 값 넣기", new[]
        {
            Field.Info("클립스튜디오 [파일 > 필압 검출 레벨 조절…] 의 \"필압 조절\" 그래프나, 브러시 [필압 설정] 그래프의 점을 왼쪽부터 (입력, 출력) 으로 적어 주세요. " +
                       "그래프 칸 하나 = 10 (0~100). 예: 0,0  25,40  60,75  100,100 — 0~1 값으로 적어도 됩니다."),
            Field.Area("pts", "(입력, 출력) 점", PressureFit.ToCspText(cur)),
        }, "미리 보기");
        if (r == null) return;
        var pts = PressureFit.ParseCsp((string)r["pts"], out var err);
        if (pts == null) { Modals.Info(err, "클립스튜디오 곡선"); return; }
        pendingCsp = pts; pendingFit = null;
        var fe = CandidateFeel();
        fitText.Text = $"클립스튜디오 곡선 (아직 적용 안 함 — 지금 펜판·미리 보기에는 이 곡선으로 그려짐)\n점 {PressureFit.ToCspText(pts)}\n" +
            $"필압 25% → {fe.Curve(0.25) * 100:0}%, 50% → {fe.Curve(0.5) * 100:0}%, 75% → {fe.Curve(0.75) * 100:0}%  (적용하면 가장 약한/센 힘은 0%/100% 로)";
        fitBox.Visible = true;
        curve.Redraw(); preview.Redraw();
        SetHint("클립스튜디오 곡선 미리 보기 — 펜판에 그어 보고 [적용] 또는 [버리기].");
    }

    // ---- 보고서·추천 -------------------------------------------------------------
    void ApplyRecommend()
    {
        var sum = Compute();
        var rec = PressureFit.Recommend(sum.Hold, sum.Pred);
        if (!sum.Hold.Found) { Modals.Info("먼저 펜을 화면에 댄 채 1초쯤 가만히 있어 보세요 (떨림을 재야 보정값을 고를 수 있습니다).", "추천 보정값"); return; }
        PressureFit.ApplyRecommend(rec);
        App.Toast($"흔들림 보정 배율 {rec["euroK"]}, 필압 다듬기 {Math.Round(U.Num(rec["pressureSmooth"]) * 100)}% 로 바꿨습니다");
        Tick(true);
    }

    void SaveReport()
    {
        var sum = Compute();
        using var d = new SaveFileDialog { Filter = "글 (*.txt)|*.txt|JSON (*.json)|*.json", FileName = $"펜진단_{U.FileStamp()}.txt" };
        if (d.ShowDialog(Form) != DialogResult.OK) return;
        File.WriteAllText(d.FileName, d.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? sum.ToJson(null, lastFit).ToJsonString(U.Indent1) : sum.ToText(null, lastFit));
        App.Toast("보고서를 저장했습니다: " + Path.GetFileName(d.FileName));
    }
    void CopyReport()
    {
        try { Clipboard.SetText(Compute().ToText(null, lastFit)); App.Toast("보고서를 복사했습니다 (메일·메신저에 붙여 넣기)"); } catch (Exception ex) { App.Toast("복사 실패: " + ex.Message); }
    }

    void Restart()
    {
        S.Clear(); fitStep = -1; foreach (var l in fitData) l.Clear();
        Pad.ClearInk(); summaryAt = -1; SetHint(); Tick(true);
    }

    // ---- 안내 글 -----------------------------------------------------------------
    void SetHint(string prefix = "")
    {
        string t;
        if (fitStep >= 0)
            t = $"자동 맞추기 {fitStep + 1}/3 — {FitTitle[fitStep]}: {FitHelp[fitStep]} {FitStrokes}번 그어 주세요 ({fitData[fitStep].Count}/{FitStrokes})";
        else if (pendingFit != null || pendingCsp != null) t = "새 필압 곡선을 미리 보는 중 — 그어 보고 오른쪽 \"필압 곡선\" 에서 [적용] 또는 [버리기].";
        else t = "펜으로 자유롭게 그어 보세요: 빠르게·느리게, 약하게·세게, 한 번은 펜을 대고 1초 가만히. 오른쪽에 판정이 바로 나옵니다.";
        hint.Text = prefix + t;
        Pad.SetCaption(fitStep >= 0 ? $"{FitTitle[fitStep]} 그어 주세요" : "여기에 펜으로 그려 보세요");
    }

    // ---- 갱신 --------------------------------------------------------------------
    DiagSummary Compute()
    {
        if (summaryAt != S.Messages)
        {
            Summary = DiagSummary.Of(S, () => new FeelPipe(CandidateFeel()), CanvasLatency.Values());
            summaryAt = S.Messages;
        }
        return Summary;
    }

    public void Tick(bool force = false)
    {
        UpdateReadout();
        if (page == "graphs" && S.Cur != null) { pGraph.Stroke = S.Cur; pGraph.Invalidate(); }
        long now = Environment.TickCount64;
        // 펜이 닿아 있는 동안은 큰 계산을 미룸 (그 사이 들어온 펜 메시지가 기다리면 지연을 잘못 재게 되니까)
        if (!force && (now - summaryTick < 350 || summaryAt == S.Messages || S.Cur != null)) return;
        summaryTick = now;
        if (force) summaryAt = -1;
        var sum = Compute();
        if (page == "checks") checks.Set(sum.Checks());
        if (page == "graphs")
        {
            pGraph.Stroke = S.Cur ?? LastStroke(); pGraph.Invalidate();
            iGraph.Hist = sum.Hist; iGraph.BinMs = sum.HistBinMs; iGraph.RateHz = sum.RateHz; iGraph.Note = sum.RateHz > 0 ? $"{sum.RateHz:0}Hz" : ""; iGraph.Invalidate();
            jGraph.Hold = sum.Hold; jGraph.Invalidate();
        }
        if (page == "latency") latText.Text = LatencyText(sum);
        if (page == "curve" && S.Cur == null && preview.Stroke != LastStroke()) { preview.Stroke = LastStroke(); preview.Redraw(); }
    }

    List<PenSample> LastStroke() => S.Strokes.Count > 0 ? S.Strokes[^1] : null;

    static string F(double v, string fmt = "0.0") => double.IsNaN(v) ? "-" : v.ToString(fmt);

    public static string LatencyText(DiagSummary s)
    {
        var l = s.Lat;
        string t = $"이 창 펜판 (입력 → 창에 올림)\n  평균 {F(l.Avg)}ms · 95% {F(l.P95)}ms · 가운데 {F(l.Median)}ms  ({l.N}장)\n" +
                   $"  └ Windows 가 넘겨줄 때까지 {F(l.Queue)}ms + 그리기·올리기 {F(l.Draw)}ms\n" +
                   $"  예측 선 그림: 평균 {F(s.LatPredOn.Avg)}ms ({s.LatPredOn.N}장) · 안 그림: 평균 {F(s.LatPredOff.Avg)}ms ({s.LatPredOff.N}장)\n\n";
        if (s.Pred.N > 0)
            t += $"펜 위치 예측 (그림판과 같은 예측, 16ms 앞)\n  체감 지연 약 {F(s.Pred.GainMs)}ms 줄임 (화면 표시까지 합친 전체 지연에서)\n" +
                 $"  예측 끝 오차 {F(s.Pred.ErrPx)}px (예측 없을 때 선 끝이 펜보다 {F(s.Pred.LagPx)}px 뒤처짐), 예측이 나온 비율 {s.Pred.Coverage * 100:0}%\n\n";
        else t += "펜 위치 예측: 몇 번 빠르게 그어 보면 효과를 계산합니다\n\n";
        t += s.Canvas.N > 0 ? $"그림판에서 잰 지연 (그릴 때)\n  평균 {F(s.Canvas.Avg)}ms · 95% {F(s.Canvas.P95)}ms ({s.Canvas.N}장)\n\n" : "그림판에서 잰 지연: 아직 없음 (그림판에서 그린 뒤 이 창을 열면 나옴)\n\n";
        t += "화면 합성(DWM)과 모니터가 실제로 빛을 내기까지(보통 +1~2 화면 장, 60Hz 면 17~33ms)는 소프트웨어로 잴 수 없어 빠져 있습니다.";
        return t;
    }

    void UpdateReadout()
    {
        var rows = readout.Rows; rows.Clear();
        var last = S.LastSample;
        bool pen = S.LastType == "pen";
        string type = S.LastType == "" ? "-" : S.LastType == "pen" ? "펜" : S.LastType == "mouse" ? "마우스" : "터치";
        if (last is PenSample q)
        {
            double fin = double.NaN;
            if (S.Cur != null && S.Cur.Count > 0) { var fp = new FeelPipe(CandidateFeel()); foreach (var p in S.Cur) fin = fp.Final(p.P); }
            rows.Add(("입력", type + (q.Contact ? " · 닿음" : " · 떠 있음")));
            rows.Add(("필압 (장치)", q.Contact ? $"{q.P:0.000}  ({Math.Round(q.P * 1024)}/1024)" : "-"));
            rows.Add(("필압 (최종)", q.Contact && !double.IsNaN(fin) ? $"{fin:0.000}" : "-"));
            rows.Add(("기울기 X / Y", pen ? $"{q.TiltX:0}° / {q.TiltY:0}°" : "-"));
            rows.Add(("돌림", pen && S.SawTwistMask ? $"{q.Twist:0}°" : "-"));
            rows.Add(("위치", $"{q.X:0.00}, {q.Y:0.00}"));
            readout.Buttons = q.Buttons; readout.Contact = q.Contact; readout.IsPen = pen;
        }
        else { rows.Add(("입력", "-")); readout.Buttons = PenBtn.None; readout.Contact = false; }
        double rate = PenStats.RecentRate(S);
        rows.Add(("보고율 (최근 1초)", rate > 0 ? $"{rate:0}Hz" : "-"));
        rows.Add(("이번 메시지 점 수", S.LastBatch > 0 ? $"{S.LastBatch}개 (최대 {S.MaxBatch})" : "-"));
        rows.Add(("묶여 온 점 (모두)", S.Messages > 0 ? $"{S.Points - S.Messages}개 / 메시지 {S.Messages}" : "-"));
        // 최근 120장의 지연
        var recent = S.Frames.Skip(Math.Max(0, S.Frames.Count - 120));
        var lr = PenStats.Latency(recent);
        rows.Add(("지연 (최근)", lr.N > 0 ? $"평균 {lr.Avg:0.0}ms · 95% {lr.P95:0.0}ms" : "-"));
        readout.Invalidate();
    }
}
