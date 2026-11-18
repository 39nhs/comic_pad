// 펜 진단 시험 (창 없이): --selftest pendiag 결과폴더 → 결과폴더/report.txt, 끝 코드 = 실패 수.
// 진짜 펜 대신 POINTER_PEN_INFO 배열(새것이 0번, Windows 가 주는 모양 그대로)을 만들어 PointerInput.Decode → DiagSession/DiagPad 에 넣고
// 보고율·간격 분포·떨림·지연 계산·예측 효과·필압 범위·판정·자동 맞추기·클립스튜디오 곡선·설정 저장/읽기·보고서를 확인한다.
// COMICPAD_DATA 를 임시 폴더로 정해야 하고, 설정 파일은 결과폴더의 settings-test.json 으로만 쓴다.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Modules.BrushEngine;
using static ComicPadNative.Modules.PenDiagnosis.PointerInput;

namespace ComicPadNative.Modules.PenDiagnosis;

public static class PenDiagTest
{
    static readonly StringBuilder log = new();
    static int fail;
    static string outDir;
    static void Ok(bool cond, string name) { log.AppendLine((cond ? "통과  " : "실패  ") + name); if (!cond) fail++; }
    static void Note(string s) => log.AppendLine("참고  " + s);
    static string F(double v) => v.ToString("0.###");

    public static int Run(string dir)
    {
        outDir = dir; Directory.CreateDirectory(dir);
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMICPAD_DATA"))) { File.WriteAllText(Path.Combine(dir, "report.txt"), "COMICPAD_DATA 를 임시 폴더로 정해 주세요"); return 2; }
            Environment.SetEnvironmentVariable("COMICPAD_SETTINGS", Path.Combine(dir, "settings-test.json"));   // 사용자 설정 파일은 절대 건드리지 않게
            App.Settings = new JsonObject();
            void T(string n, Action a)
            {
                var sw = Stopwatch.StartNew();
                try { a(); } catch (Exception ex) { Ok(false, $"{n}: 예외 {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}"); }
                log.AppendLine($"      ({n} {sw.ElapsedMilliseconds}ms)");
            }
            T("포인터 정보 풀기", TestDecode);
            T("보고율·간격 분포", TestRate);
            T("가만히 떨림", TestHold);
            T("지연 계산", TestLatency);
            T("예측 효과", TestPrediction);
            T("필압 범위·판정", TestChecks);
            T("필압 다듬기 = 브러시", TestPipeMatchesBrush);
            T("자동 맞추기", TestAutoFit);
            T("클립스튜디오 곡선", TestCsp);
            T("설정 저장·읽기", TestRoundTrip);
            T("펜판 (실제 브러시로 그리기)", TestPad);
            T("창 흐름 (자동 맞추기 → 적용)", TestWindowFlow);
            T("보고서", TestReport);
            T("창 그림", TestShots);
            T("가짜 펜 넣기 (Windows 포인터 메시지)", TestInject);
        }
        catch (Exception ex) { Ok(false, "시험 실행 오류: " + ex); }
        log.AppendLine(fail == 0 ? "모두 통과" : $"실패 {fail}개");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        return fail;
    }

    // ---- 만든 펜 점 --------------------------------------------------------------
    /// <summary>장치 사각형: HIMETRIC 0~100000 ↔ 화면 0~10000 px (HIMETRIC 10 = 1px)</summary>
    static RectangleF[] Rects(IntPtr dev) => dev == (IntPtr)1 ? new[] { new RectangleF(0, 0, 100000, 100000), new RectangleF(0, 0, 10000, 10000) } : null;
    static long Qpc(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000.0);

    public record struct Raw(double X, double Y, double P, double T, bool Contact = true, int TiltX = 0, int TiltY = 0, uint Rot = 0, int PenFlags = 0, int PtrFlags = 0, int Mask = 15);

    static POINTER_PEN_INFO Info(Raw r) => new()
    {
        pointerInfo = new POINTER_INFO
        {
            pointerType = PT_PEN, pointerId = 1, sourceDevice = (IntPtr)1,
            pointerFlags = (r.Contact ? POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON : 0) | r.PtrFlags,
            ptPixelLocation = new POINT { X = (int)Math.Floor(r.X), Y = (int)Math.Floor(r.Y) },
            ptHimetricLocation = new POINT { X = (int)Math.Round(r.X * 10), Y = (int)Math.Round(r.Y * 10) },
            PerformanceCount = (ulong)Qpc(r.T), dwTime = (uint)r.T,
        },
        penMask = r.Mask, pressure = (uint)Math.Round(r.P * 1024), tiltX = r.TiltX, tiltY = r.TiltY, rotation = r.Rot, penFlags = r.PenFlags,
    };

    /// <summary>시간 순 점들 → 메시지 한 장 (Windows 처럼 새것을 0번에)</summary>
    static PenFrame Frame(IList<Raw> pts, int msg = WM_POINTERUPDATE, int type = PT_PEN, double recvAfter = 2)
    {
        var arr = pts.Reverse().Select(Info).ToArray();
        if (type != PT_PEN) for (int i = 0; i < arr.Length; i++) arr[i].pointerInfo.pointerType = type;
        return Decode(arr, arr.Length, type, msg, PointF.Empty, Rects, 1.0, Qpc(pts[^1].T + recvAfter));
    }

    /// <summary>한 획을 메시지들로: 점 목록을 batch 개씩 묶고, 첫 장은 DOWN, 끝에 UP(떨어진 점)</summary>
    static List<PenFrame> StrokeFrames(List<Raw> pts, int batch = 2, int type = PT_PEN)
    {
        var fr = new List<PenFrame> { Frame(new[] { pts[0] }, WM_POINTERDOWN, type) };
        for (int i = 1; i < pts.Count; i += batch) fr.Add(Frame(pts.Skip(i).Take(batch).ToList(), WM_POINTERUPDATE, type));
        var last = pts[^1];
        fr.Add(Frame(new[] { last with { T = last.T + 4, Contact = false, P = 0 } }, WM_POINTERUP, type));
        return fr;
    }

    /// <summary>곧은 선: from → to, 초당 hz 점, 필압 p0 → p1</summary>
    static List<Raw> Line(double x0, double y0, double x1, double y1, double ms, double hz, double p0, double p1, double t0, int tiltX = 0, int tiltY = 0)
    {
        int n = (int)Math.Round(ms * hz / 1000);
        return Enumerable.Range(0, n + 1).Select(i => { double u = i / (double)n; return new Raw(x0 + (x1 - x0) * u, y0 + (y1 - y0) * u, p0 + (p1 - p0) * u, t0 + i * 1000 / hz, true, tiltX, tiltY); }).ToList();
    }

    /// <summary>가만히: 가운데 둘레 반지름 r 의 8 방향을 돌며</summary>
    static List<Raw> HoldPts(double cx, double cy, double r, double ms, double hz, double p, double t0)
    {
        int n = (int)Math.Round(ms * hz / 1000);
        return Enumerable.Range(0, n).Select(i => { double a = i % 8 * Math.PI / 4; return new Raw(cx + r * Math.Cos(a), cy + r * Math.Sin(a), p + (i % 2 == 0 ? 0.002 : -0.002), t0 + i * 1000 / hz); }).ToList();
    }

    static DiagSession Session(IEnumerable<PenFrame> frames) { var s = new DiagSession(); foreach (var f in frames) s.Add(f); return s; }

    /// <summary>시험용 펜 세션: 240Hz 선 4개 (필압 0.05→1, 기울기), 가만히 1초 (떨림 0.3px)</summary>
    static (DiagSession s, List<PenFrame> frames) PenSession()
    {
        var frames = new List<PenFrame>();
        double t = 1000;
        for (int k = 0; k < 4; k++) { frames.AddRange(StrokeFrames(Line(100, 100 + k * 30, 400, 250 + k * 30, 1500, 240, 0.05, 1.0, t, 20, -10))); t += 2000; }
        frames.AddRange(StrokeFrames(HoldPts(300, 300, 0.3, 1000, 240, 0.5, t)));
        return (Session(frames), frames);
    }

    // ---- 시험 --------------------------------------------------------------------
    static void TestDecode()
    {
        // 새것이 0번: 세 점 (시각 10, 14, 18ms), 마지막 점은 측면 버튼 + 지우개 표시
        var pts = new[]
        {
            new Raw(123.4, 50.2, 0.25, 10, true, 30, -12, 90),
            new Raw(124.6, 51.0, 0.5, 14, true, 31, -11, 91, PEN_FLAG_BARREL),
            new Raw(125.8, 51.9, 0.75, 18, true, 32, -10, 92, PEN_FLAG_ERASER, POINTER_FLAG_THIRDBUTTON),
        };
        var f = Frame(pts);
        Ok(f.Pts.Count == 3 && f.Pts[0].T < f.Pts[1].T && f.Pts[1].T < f.Pts[2].T, $"묶여 온 점 3개를 오래된 것부터 ({F(f.Pts[0].T)}, {F(f.Pts[1].T)}, {F(f.Pts[2].T)}ms)");
        Ok(Math.Abs(f.Pts[0].X - 123.4) < 0.051 && Math.Abs(f.Pts[0].Y - 50.2) < 0.051 && f.Himetric, $"HIMETRIC → 1픽셀보다 작은 위치 ({F(f.Pts[0].X)}, {F(f.Pts[0].Y)})");
        Ok(Math.Abs(f.Pts[1].P - 0.5) < 1e-9 && f.Pts[2].TiltX == 32 && f.Pts[2].TiltY == -10 && f.Pts[2].Twist == 92 && f.HasPressure && f.HasTilt && f.HasTwist, "필압 1024단계·기울기·돌림");
        Ok(f.Pts[0].Buttons == PenBtn.Tip && f.Pts[1].Buttons == (PenBtn.Tip | PenBtn.Side1) && f.Pts[2].Buttons == (PenBtn.Tip | PenBtn.Side2 | PenBtn.Eraser), $"버튼: 펜 끝·측면 1·측면 2·지우개 ({f.Pts[2].Buttons})");
        Ok(f.Qpc && Math.Abs(f.Pts[2].T - 18) < 0.01 && Math.Abs(f.RecvT - 20) < 0.01, "점 시각 = QPC (ms)");
        // UP 메시지: 새 점은 떨어진 것으로
        var up = Frame(new[] { pts[0] with { T = 30 } }, WM_POINTERUP);
        Ok(up.Up && !up.Pts[0].Contact, "UP 메시지 = 펜 뗌");
        // 장치 좌표가 1.5px 넘게 어긋나면 정수 위치 (그림판과 같은 규칙)
        var bad = Info(new Raw(10.5, 10.5, 0.5, 40)); bad.pointerInfo.ptHimetricLocation = new POINT { X = 500, Y = 500 };
        var fb = Decode(new[] { bad }, 1, PT_PEN, WM_POINTERUPDATE, PointF.Empty, Rects, 1, Qpc(41));
        Ok(fb.Pts[0].X == 10 && fb.Pts[0].Y == 10 && !fb.Himetric, "어긋난 HIMETRIC → 정수 위치");
        // 필압 표시 없는 펜: 닿으면 0.5 / 마우스: 설정 마우스 필압
        var np = Frame(new[] { new Raw(5, 5, 0.9, 50, Mask: 0) });
        Ok(np.Pts[0].P == 0.5 && !np.HasPressure, "필압 없는 펜 = 0.5");
        var ms = Decode(new[] { Info(new Raw(5, 5, 0, 60)) }, 1, PT_MOUSE, WM_POINTERUPDATE, PointF.Empty, Rects, 0.7, Qpc(61));
        Ok(ms.Type == "mouse" && ms.Pts[0].P == 0.7 && !ms.Himetric, "마우스 = 마우스 필압, 정수 위치");
        // QPC 없음 → dwTime
        var nq = Info(new Raw(5, 5, 0.5, 70)); nq.pointerInfo.PerformanceCount = 0;
        var fq = Decode(new[] { nq }, 1, PT_PEN, WM_POINTERUPDATE, PointF.Empty, Rects, 1, Qpc(71));
        Ok(!fq.Qpc && fq.Pts[0].T == 70, "QPC 가 없으면 메시지 시각");
    }

    static void TestRate()
    {
        var (s, _) = PenSession();
        var sum = DiagSummary.Of(s);
        Ok(Math.Abs(sum.RateHz - 240) < 0.5 && Math.Abs(sum.RateMean - 240) < 0.5, $"보고율 {F(sum.RateHz)}Hz (평균 {F(sum.RateMean)}Hz) = 240Hz");
        Ok(sum.Hist[4] == sum.Intervals.Count && sum.Intervals.Count > 1000, $"간격 4.17ms → 4ms 칸에 {sum.Hist[4]}/{sum.Intervals.Count}");
        Ok(sum.Strokes == 5 && Math.Abs(sum.AvgBatch - s.Points / (double)s.Messages) < 1e-9 && sum.MaxBatch == 2, $"획 {sum.Strokes}개, 메시지당 {F(sum.AvgBatch)}점, 최대 {sum.MaxBatch}");
        // 100Hz 에 가끔 빠짐 (20ms) → 가운데값은 그대로 100Hz, 분포에 20ms 칸
        var pts = Line(0, 0, 300, 0, 2000, 100, 0.5, 0.5, 0);
        for (int i = 10; i < pts.Count; i += 10) for (int j = i; j < pts.Count; j++) pts[j] = pts[j] with { T = pts[j].T + 10 };
        var s2 = Session(StrokeFrames(pts, 1));
        var sm2 = DiagSummary.Of(s2);
        Ok(Math.Abs(sm2.RateHz - 100) < 0.5 && sm2.Hist[20] > 10 && sm2.Hist[10] > 100, $"100Hz + 가끔 빠짐: {F(sm2.RateHz)}Hz, 10ms 칸 {sm2.Hist[10]}, 20ms 칸 {sm2.Hist[20]}");
        Ok(PenStats.RecentRate(s2) > 80 && PenStats.RecentRate(s2) < 101, $"최근 1초 보고율 {F(PenStats.RecentRate(s2))}Hz");
    }

    static void TestHold()
    {
        var (s, _) = PenSession();
        var h = DiagSummary.Of(s).Hold;
        Ok(h.Found && Math.Abs(h.JitterPx - 0.3) < 0.02 && h.MaxDevPx < 0.4, $"떨림 {F(h.JitterPx)}px (만든 값 0.3), 최대 {F(h.MaxDevPx)}");
        Ok(h.DurationMs > 850 && h.DurationMs < 1000, $"가만히 구간 {F(h.DurationMs)}ms (대는 순간 100ms 뺌)");
        Ok(Math.Abs(h.PNoise - 0.004) < 0.0015, $"필압 흔들림 {h.PNoise:0.00000} (만든 값 ±0.002 → 차이 0.004)");
        // 움직이는 선만 있으면 가만히 구간 없음
        var s2 = Session(StrokeFrames(Line(0, 0, 500, 0, 1000, 200, 0.5, 0.5, 0)));
        Ok(!DiagSummary.Of(s2).Hold.Found, "움직이는 선 = 가만히 구간 없음");
    }

    static void TestLatency()
    {
        // 100장: 90장은 7ms, 10장은 22ms → 평균 8.5, 95% 22, 가운데 7 / Windows 전달 2ms, 그리기 나머지
        var frames = new List<PenFrame>();
        for (int i = 0; i < 100; i++)
        {
            var f = Frame(new[] { new Raw(i, 0, 0.5, 1000 + i * 8.0), new Raw(i + 0.5, 0, 0.5, 1004 + i * 8.0) });
            f.PresentT = f.Last.T + (i % 10 == 0 ? 22 : 7);
            f.Predicted = i % 2 == 0;
            frames.Add(f);
        }
        var l = PenStats.Latency(frames);
        Ok(l.N == 100 && Math.Abs(l.Avg - 8.5) < 1e-6 && l.P95 == 22 && l.Median == 7, $"평균 {F(l.Avg)} · 95% {F(l.P95)} · 가운데 {F(l.Median)}ms");
        Ok(Math.Abs(l.Queue - 2) < 1e-6 && Math.Abs(l.Draw - 6.5) < 1e-6, $"Windows 전달 {F(l.Queue)}ms + 그리기 {F(l.Draw)}ms");
        var on = PenStats.Latency(frames, true); var off = PenStats.Latency(frames, false);
        Ok(on.N == 50 && off.N == 50 && Math.Abs(on.Avg - 10) < 1e-6 && Math.Abs(off.Avg - 7) < 1e-6, $"예측 켬 {F(on.Avg)}ms · 끔 {F(off.Avg)}ms (짝수 장에 22ms 가 몰림)");
        // 떨어진 장·QPC 없는 장·말이 안 되는 값은 뺌
        var upF = Frame(new[] { new Raw(0, 0, 0, 5000, false) }, WM_POINTERUP); upF.PresentT = 5010;
        var huge = Frame(new[] { new Raw(0, 0, 0.5, 6000) }); huge.PresentT = 6000 + 900;
        Ok(PenStats.Latency(frames.Append(upF).Append(huge)).N == 100, "펜을 뗀 장·500ms 넘는 값은 뺌");
        var cv = PenStats.FromValues(new List<double> { 5, 6, 7, 8, 30 });
        Ok(cv.N == 5 && Math.Abs(cv.Avg - 11.2) < 1e-9 && cv.P95 == 30, "그림판 실측 값 평균·95%");
        CanvasLatency.Clear();
        CanvasLatency.Add(0, Qpc(9), true); CanvasLatency.Add(0, Qpc(11), true); CanvasLatency.Add(0, Qpc(50), false);
        var cl = CanvasLatency.Values();
        Ok(cl.Count == 2 && Math.Abs(cl.Average() - 10) < 0.01, "그림판 지연 모음 (그리는 중만)");
        CanvasLatency.Clear();
    }

    static void TestPrediction()
    {
        // 등속 직선: 예측이 거의 정확 → 체감 지연 16ms 줄임
        var line = Session(StrokeFrames(Line(0, 0, 1000, 500, 1000, 240, 0.5, 0.5, 0))).Strokes;
        var pl = PenStats.Prediction(line);
        Ok(pl.N > 150 && pl.GainMs > 15.5 && pl.ErrPx < 0.3 && Math.Abs(pl.LagPx - 1118 * 0.016) < 0.5, $"등속 직선: {F(pl.GainMs)}ms 줄임, 오차 {F(pl.ErrPx)}px, 뒤처짐 {F(pl.LagPx)}px");
        // 원 (초당 한 바퀴, 반지름 100): 조금 빗나감
        var circ = Enumerable.Range(0, 480).Select(i => { double a = i / 240.0 * Math.PI * 2; return new Raw(300 + 100 * Math.Cos(a), 300 + 100 * Math.Sin(a), 0.5, i * 1000 / 240.0); }).ToList();
        var pc = PenStats.Prediction(Session(StrokeFrames(circ)).Strokes);
        Ok(pc.GainMs > 8 && pc.GainMs < 16 && pc.ErrPx > 0.05, $"원: {F(pc.GainMs)}ms 줄임, 오차 {F(pc.ErrPx)}px");
        // 지그재그 (20ms 마다 방향을 뒤집음): 효과가 거의 없거나 손해
        var zig = Enumerable.Range(0, 480).Select(i => { double t = i * 1000 / 240.0, ph = t % 40; double y = ph < 20 ? ph * 5 : (40 - ph) * 5; return new Raw(100 + i * 0.5, 100 + y, 0.5, t); }).ToList();
        var pz = PenStats.Prediction(Session(StrokeFrames(zig)).Strokes);
        Ok(pz.GainMs < pc.GainMs, $"지그재그: {F(pz.GainMs)}ms (원보다 작음)");
    }

    static void TestChecks()
    {
        var (s, frames) = PenSession();
        foreach (var f in frames) if (f.AnyContact) f.PresentT = f.Last.T + (frames.IndexOf(f) % 10 == 0 ? 22 : 7);
        var sum = DiagSummary.Of(s, () => new FeelPipe());
        var pr = sum.Pressure;
        Ok(pr.Levels > 300 && pr.Lo < 0.1 && pr.Hi > 0.95 && Math.Abs(pr.FinalLo - PenStats.Pct(s.Strokes.SelectMany(x => x).Select(x => x.P), 0.05)) < 0.02, $"필압 {pr.Levels}단계, {F(pr.Lo)}~{F(pr.Hi)}, 최종 {F(pr.FinalLo)}~{F(pr.FinalHi)}");
        var c = sum.Checks().ToDictionary(x => x.Id);
        string Show(string id) => c.TryGetValue(id, out var x) ? $"{id}={x.V}" : $"{id}=없음";
        Ok(c["type"].V == Verdict.Ok && c["pressure"].V == Verdict.Ok && c["range"].V == Verdict.Ok && c["rate"].V == Verdict.Ok && c["tilt"].V == Verdict.Ok && c["jitter"].V == Verdict.Ok && c["himetric"].V == Verdict.Ok,
            "좋은 펜: " + string.Join(", ", new[] { "type", "pressure", "range", "rate", "tilt", "jitter", "himetric" }.Select(Show)));
        Ok(c["latency"].V == Verdict.Warn && c["latency"].Title.StartsWith("지연 ~"), $"지연 95% 22ms → 주의 ({c["latency"].Title} / {c["latency"].Value})");
        Ok(c.ContainsKey("predict"), "예측 효과 항목");
        // 마우스로 들어오는 펜 (Windows Ink 꺼짐)
        var m = Session(StrokeFrames(Line(0, 0, 300, 0, 1000, 125, 0.5, 0.5, 0), 1, PT_MOUSE));
        var cm = DiagSummary.Of(m).Checks().ToDictionary(x => x.Id);
        Ok(cm["type"].V == Verdict.Bad && cm["type"].Hint.Contains("Windows Ink") && !cm.ContainsKey("pressure"), "마우스로 들어옴 → 문제 + Windows Ink 안내");
        // 필압 표시 없는 펜
        var np = Session(StrokeFrames(Line(0, 0, 300, 0, 1000, 200, 0.5, 0.5, 0).Select(r => r with { Mask = 4 | 8 }).ToList()));
        var cn = DiagSummary.Of(np).Checks().ToDictionary(x => x.Id);
        Ok(cn["pressure"].V == Verdict.Bad && cn["pressure"].Hint.Contains("Windows Ink"), "필압이 안 옴 → 문제 + 안내");
        // 보고율 50Hz
        var slow = Session(StrokeFrames(Line(0, 0, 300, 0, 3000, 50, 0.2, 0.8, 0)));
        var cs = DiagSummary.Of(slow).Checks().ToDictionary(x => x.Id);
        Ok(cs["rate"].V == Verdict.Bad, $"50Hz → 문제 ({cs["rate"].Value})");
        // 기울기 없음
        Ok(cn["tilt"].V == Verdict.Info, "기울기 값 0 → 참고");
        // 세게 눌러도 0.6 까지만 → 범위 주의
        var weak = new List<PenFrame>(); double t = 0;
        for (int k = 0; k < 4; k++) { weak.AddRange(StrokeFrames(Line(0, k * 20, 300, k * 20, 800, 200, 0.05, 0.6, t))); t += 1000; }
        var cw = DiagSummary.Of(Session(weak), () => new FeelPipe(new PenFeel())).Checks().ToDictionary(x => x.Id);
        Ok(cw["range"].V == Verdict.Warn && cw["range"].Hint.Contains("자동 맞추기"), $"세게 눌러도 60% → 주의 ({cw["range"].Value})");
        // 아무것도 없음
        var c0 = DiagSummary.Of(new DiagSession()).Checks();
        Ok(c0[0].V == Verdict.Wait && c0.All(x => x.V == Verdict.Wait), "아직 안 그림 = 확인 중");
    }

    static void TestPipeMatchesBrush()
    {
        App.Settings = new JsonObject { ["penFeel"] = new JsonObject { ["pressureSmooth"] = 0.4, ["startRamp"] = true } };
        var preset = DefaultPresets.Of("brush")[0].Clone(); preset.Stabilizer = 0;
        var st = new BrushStroke(new StrokeSetup { W = 200, H = 100, Preset = preset, Seed = 1 }) { Rec = new List<double>() };
        var pipe = new FeelPipe();
        var rnd = new Random(3); double maxD = 0; int n = 40;
        for (int i = 0; i < n; i++)
        {
            double p = 0.2 + 0.6 * rnd.NextDouble();
            var pt = new BPt(10 + i * 4, 50, p, i * 5);
            if (i == 0) st.Begin(pt); else st.Add(pt);
            maxD = Math.Max(maxD, Math.Abs(st.Rec[i * 4 + 2] - Math.Round(pipe.Smooth(p), 3, MidpointRounding.AwayFromZero)));
        }
        Ok(maxD < 1e-9, $"펜 진단의 필압 다듬기 = BrushStroke.PressureOf (점 {n}개, 최대 차이 {maxD})");
        var feel = PenFeel.Current();
        var f2 = new FeelPipe(feel); double a = f2.Final(0.9);
        Ok(Math.Abs(a - feel.Curve(Math.Min(0.9, 0.35))) < 1e-9, "첫 점은 시작 줄이기 (0.35 로 자름) 후 곡선");
        App.Settings = new JsonObject();
    }

    /// <summary>약하게/보통/세게 획: 처음·끝 6점은 0 에서 올라가고 내려감, 가운데는 p ± 0.01</summary>
    static List<PenSample> FitStroke(double p, int seed, double t0)
    {
        var r = new Random(seed); var l = new List<PenSample>();
        for (int i = 0; i < 60; i++)
        {
            double v = i < 6 ? p * i / 6 : i >= 54 ? p * (60 - i) / 6 : p + (r.NextDouble() - 0.5) * 0.02;
            l.Add(new PenSample(t0 + i * 5, 50 + i * 4, 50 + seed, Math.Max(0.001, v)));
        }
        return l;
    }

    static void TestAutoFit()
    {
        var light = Enumerable.Range(0, 3).Select(i => FitStroke(0.2, i, 0)).ToList();
        var med = Enumerable.Range(0, 3).Select(i => FitStroke(0.45, 10 + i, 0)).ToList();
        var strong = Enumerable.Range(0, 3).Select(i => FitStroke(0.8, 20 + i, 0)).ToList();
        var r = PressureFit.AutoFit(light, med, strong);
        Ok(r != null && Math.Abs(r.PMin - 0.133) < 0.01 && Math.Abs(r.PMax - 0.81) < 0.015 && r.Pts?.Length == 4, $"범위 {r?.PMin}~{r?.PMax}, 곡선 {PressureFit.ToCspText(r?.Pts)}");
        var fe = r.Feel(new PenFeel());
        double cl = fe.Curve(0.2), cm = fe.Curve(0.45), cs = fe.Curve(0.8), c0 = fe.Curve(0.1);
        Ok(Math.Abs(cl - 0.15) < 0.03 && Math.Abs(cm - 0.5) < 0.03 && cs > 0.97 && c0 == 0, $"약하게 0.2 → {F(cl)}, 보통 0.45 → {F(cm)}, 세게 0.8 → {F(cs)}, 0.1 → {F(c0)}");
        // 단조 증가
        bool mono = true; double prev = -1; for (int i = 0; i <= 100; i++) { double v = fe.Curve(i / 100.0); if (v < prev - 1e-6) mono = false; prev = v; }
        Ok(mono, "맞춘 곡선은 늘 증가");
        // 보통 없이 / 세기 차이가 작을 때 / 모자랄 때
        var r2 = PressureFit.AutoFit(light, null, strong);
        Ok(r2 != null && r2.Pts?.Length == 3 && double.IsNaN(r2.Medium), "보통 없이도 맞춤");
        var r3 = PressureFit.AutoFit(light, null, Enumerable.Range(0, 3).Select(i => FitStroke(0.3, 30 + i, 0)).ToList());
        Ok(r3 != null && r3.PMax - r3.PMin >= 0.25 - 1e-9 && r3.Note.Contains("세게"), $"세기 차이가 작음 → 범위 넓힘 + 안내 ({r3?.Note})");
        Ok(PressureFit.AutoFit(new List<List<PenSample>>(), med, strong) == null, "약하게 획이 없으면 맞추지 않음");
        // 곧은 경우 (약하게가 이미 15% 근처, 보통이 50% 근처) → 곡선 없음
        var lin = PressureFit.AutoFit(Enumerable.Range(0, 3).Select(i => FitStroke(0.37, 40 + i, 0)).ToList(), Enumerable.Range(0, 3).Select(i => FitStroke(0.63, 50 + i, 0)).ToList(), Enumerable.Range(0, 3).Select(i => FitStroke(1.0, 60 + i, 0)).ToList());
        Ok(lin != null && lin.Pts == null, $"거의 곧으면 곡선 없이 범위만 ({lin?.PMin}~{lin?.PMax}, {PressureFit.ToCspText(lin?.Pts)})");
    }

    static void TestCsp()
    {
        var a = PressureFit.ParseCsp("0,0  25,40  60,75  100,100", out var e1);
        Ok(a != null && a.Length == 4 && a[1][0] == 0.25 && a[1][1] == 0.4 && a[3][1] == 1, $"0~100 값 → {PressureFit.ToCspText(a)} {e1}");
        var b = PressureFit.ParseCsp("(0.0, 0.1)\n(0.5, 0.3)\n(1, 0.9)", out _);
        Ok(b != null && b[0][1] == 0.1 && b[2][1] == 0.9 && b[1][0] == 0.5, "0~1 값, 괄호·줄바꿈");
        var c = PressureFit.ParseCsp("70 90\n30 20", out _);
        Ok(c != null && c.Length == 4 && c[0][0] == 0 && c[0][1] == 0.2 && c[1][0] == 0.3 && c[3][0] == 1 && c[3][1] == 0.9, $"순서 정렬 + 양 끝 채움 → {PressureFit.ToCspText(c)}");
        Ok(PressureFit.ParseCsp("0,0 50", out var e2) == null && e2 != null, "짝이 안 맞음 → 오류: " + e2);
        Ok(PressureFit.ParseCsp("0,0 150,100", out var e3) == null && e3 != null, "범위 밖 → 오류: " + e3);
        Ok(PressureFit.ParseCsp("곡선", out var e4) == null && e4 != null, "숫자 없음 → 오류");
        // 적용 → 브러시가 읽는 설정 (pmin 0, pmax 1, pressurePts)
        App.Settings = new JsonObject { ["penFeel"] = new JsonObject { ["pmin"] = 0.2, ["pmax"] = 0.7 }, ["pressureGamma"] = 2.2 };
        PressureFit.ApplyCsp(a);
        var fe = PenFeel.Current();
        Ok(fe.PMin == 0 && fe.PMax == 1 && fe.Gamma == 1 && Math.Abs(fe.Curve(0.25) - 0.4) < 0.01 && Math.Abs(fe.Curve(0.6) - 0.75) < 0.01, $"적용: 25% → {F(fe.Curve(0.25))}, 60% → {F(fe.Curve(0.6))} (pmin {fe.PMin}, pmax {fe.PMax}, 감마 {fe.Gamma})");
        var d = PressureFit.ParseCsp("0,0 100,100", out _); PressureFit.ApplyCsp(d);
        Ok(App.Settings["pressurePts"] == null, "곧은 곡선 = 곡선 설정 지움");
        App.Settings = new JsonObject();
    }

    static void TestRoundTrip()
    {
        App.Settings = new JsonObject { ["brushCursor"] = "nib", ["penFeel"] = new JsonObject { ["filter"] = "euro", ["predict"] = true } };
        var light = Enumerable.Range(0, 3).Select(i => FitStroke(0.2, i, 0)).ToList();
        var strong = Enumerable.Range(0, 3).Select(i => FitStroke(0.8, 20 + i, 0)).ToList();
        var r = PressureFit.AutoFit(light, Enumerable.Range(0, 3).Select(i => FitStroke(0.45, 10 + i, 0)).ToList(), strong);
        var snap = PressureFit.Snapshot();
        PressureFit.Apply(r);
        var want = Enumerable.Range(0, 101).Select(i => PenFeel.Current().Curve(i / 100.0)).ToArray();
        App.SaveSettings();
        string path = Environment.GetEnvironmentVariable("COMICPAD_SETTINGS");
        Ok(path.StartsWith(outDir) && File.Exists(path), "설정은 시험 폴더 파일에만 저장");
        App.Settings = new JsonObject();
        App.LoadSettings();
        var fe = PenFeel.Current();
        double maxD = Enumerable.Range(0, 101).Max(i => Math.Abs(fe.Curve(i / 100.0) - want[i]));
        Ok(maxD < 1e-9 && fe.PMin == r.PMin && fe.PMax == r.PMax && (string)App.Settings["brushCursor"] == "nib" && (bool)App.Settings["penFeel"]["predict"], $"저장 → 다시 읽기: 곡선 같음 (최대 차이 {maxD}), 다른 설정 그대로");
        // 브러시도 같은 곡선을 씀 (StrokeSetup.Feel 없이 = 설정)
        var preset = DefaultPresets.Of("brush")[0].Clone(); preset.Stabilizer = 0; preset.PressurePts = null;
        var st = new BrushStroke(new StrokeSetup { W = 10, H = 10, Preset = preset });
        Ok(Math.Abs(PenFeel.Current().Curve(0.45, preset) - fe.Curve(0.45)) < 1e-9, "브러시가 쓰는 곡선 = 맞춘 곡선");
        // 되돌리기
        PressureFit.Restore(snap);
        var back = PenFeel.Current();
        Ok(back.PMin == 0 && back.PMax == 1 && back.Pts == null, "창을 열 때 값으로 되돌리기");
        // 추천 보정값
        var rec = PressureFit.Recommend(new PenStats.HoldResult(0.3, 0.5, 900, 200, 0.004, Array.Empty<(double, double)>()), new PenStats.PredResult(100, 10, 0.5, 5, 1));
        PressureFit.ApplyRecommend(rec);
        Ok((double)App.Settings["penFeel"]["euroK"] == 1.1 && (double)App.Settings["penFeel"]["pressureSmooth"] == 0.25 && (bool)App.Settings["penFeel"]["predict"], $"추천 보정값 → penFeel ({rec.ToJsonString()})");
        App.Settings = new JsonObject();
    }

    static void TestPad()
    {
        App.Settings = new JsonObject();
        using var pad = new DiagPad { Width = 500, Height = 400, AlternatePrediction = true };
        foreach (var f in StrokeFrames(Line(50, 50, 450, 300, 600, 240, 0.1, 1, 1000))) pad.OnFrame(f);
        foreach (var f in StrokeFrames(Line(50, 350, 450, 350, 600, 240, 0.8, 0.2, 3000))) pad.OnFrame(f);
        var s = pad.Session;
        Ok(s.Strokes.Count == 2 && s.Frames.All(f => !double.IsNaN(f.PresentT)), $"획 {s.Strokes.Count}개, 메시지 {s.Messages}개 모두 그려 올린 시각 있음");
        var p1 = s.Frames.Where(f => f.AnyContact && f.Pts[0].T < 2900).ToList(); var p2 = s.Frames.Where(f => f.AnyContact && f.Pts[0].T >= 2900).ToList();
        Ok(p1.All(f => f.Predicted) && p2.All(f => !f.Predicted), "번갈아: 첫 획 예측 켬, 둘째 획 끔");
        int ink = 0; var paper = pad.Paper;
        for (int y = 0; y < paper.H; y += 2) for (int x = 0; x < paper.W; x += 2) if ((paper.Get(x, y) >> 24) > 128) ink++;
        // 첫 획: 굵기가 필압 따라 → 왼쪽 위보다 오른쪽 아래가 굵음
        int Thick(double x, double y) { int n = 0; for (int dy = -20; dy <= 20; dy++) if ((paper.Get((int)x, (int)(y + dy)) >> 24) > 128) n++; return n; }
        int tA = Thick(90, 50 + 250 * 40 / 400.0), tB = Thick(410, 50 + 250 * 360 / 400.0);
        Ok(ink > 300 && tB > tA, $"실제 브러시로 그려짐 (잉크 {ink}점), 약할 때 굵기 {tA} < 셀 때 {tB}");
        var hv = pad.Hover; Ok(hv == null || !hv.Value.Contact, "펜을 떼면 띄운 상태");
    }

    static void TestWindowFlow()
    {
        App.Settings = new JsonObject();
        var sw = Stopwatch.StartNew();
        using var w = new PenDiagWindow();
        Note($"창 만들기 {sw.ElapsedMilliseconds}ms"); sw.Restart();
        w.Pad.Width = 600; w.Pad.Height = 400;
        w.StartFit();
        Ok(w.HintText.Contains("1/3") && w.HintText.Contains("약하게"), "자동 맞추기 1단계 안내: " + w.HintText);
        double t = 1000; int seed = 0;
        foreach (var p in new[] { 0.2, 0.45, 0.8 })
            for (int k = 0; k < 3; k++)
            {
                var raws = FitStroke(p, seed++, t).Select(s => new Raw(s.X, s.Y, s.P, s.T)).ToList();
                foreach (var f in StrokeFrames(raws)) w.Pad.OnFrame(f);
                t += 1000;
                if (p == 0.2 && k == 0) Ok(w.HintText.Contains("(1/3)"), "한 획 그으면 1/3");
            }
        Note($"아홉 획 {sw.ElapsedMilliseconds}ms");
        var pf = w.PendingFit;
        Ok(pf != null && w.Page == "curve" && Math.Abs(pf.PMin - 0.133) < 0.01, $"세 단계 끝 → 결과 미리 보기 (범위 {pf?.PMin}~{pf?.PMax}), 필압 곡선 탭");
        Ok(App.Settings["pressurePts"] == null, "적용 전에는 설정 그대로");
        w.ApplyPending();
        var fe = PenFeel.Current();
        Ok(Math.Abs(fe.Curve(0.45) - 0.5) < 0.03 && Math.Abs(fe.PMin - pf.PMin) < 1e-9 && App.Settings["pressurePts"] is JsonArray, $"적용 → 설정 (보통 0.45 → {F(fe.Curve(0.45))})");
        App.Settings = new JsonObject();
    }

    static void TestReport()
    {
        App.Settings = new JsonObject { ["penFeel"] = new JsonObject { ["pmin"] = 0.1 } };
        var (s, frames) = PenSession();
        foreach (var f in frames) if (f.AnyContact) f.PresentT = f.Last.T + 7;
        var sum = DiagSummary.Of(s, () => new FeelPipe());
        string txt = sum.ToText();
        var json = sum.ToJson();
        File.WriteAllText(Path.Combine(outDir, "pendiag_report.txt"), txt);
        File.WriteAllText(Path.Combine(outDir, "pendiag_report.json"), json.ToJsonString(Extras.U.Indent1));
        var back = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "pendiag_report.json")));
        Ok((string)back["format"] == "comicpad-pendiag" && Math.Abs((double)back["rate"]["medianHz"] - 240) < 0.5 && back["checks"].AsArray().Count == sum.Checks().Count
           && (double)back["settings"]["penFeel"]["pmin"] == 0.1 && (double)back["latency"]["avgMs"] == 7, "JSON 보고서 (보고율·판정·설정·지연)");
        Ok(txt.Contains("펜 진단 보고서") && txt.Contains("[좋음] 보고율 정상") && txt.Contains("---- JSON ----"), "글 보고서");
        App.Settings = new JsonObject();
    }

    static void TestShots()
    {
        App.Settings = new JsonObject();
        var sw = Stopwatch.StartNew();
        using var w = new PenDiagWindow();
        Note($"두 번째 창 만들기 {sw.ElapsedMilliseconds}ms");
        var (s, frames) = PenSession();
        int i = 0;
        foreach (var f in frames) { w.Pad.OnFrame(f); }
        foreach (var f in w.Pad.Session.Frames) if (f.AnyContact) f.PresentT = f.Last.T + (i++ % 10 == 0 ? 14 : 6);
        CanvasLatency.Clear(); for (int k = 0; k < 50; k++) CanvasLatency.Add(0, Qpc(8 + k % 5), true);
        // 창 내용을 화면 밖 보통 창에 옮겨 그림으로 (모달 창·배경 막 없이)
        var content = w.Form.Content;
        w.Form.Body.Controls.Remove(content);
        using var host = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ClientSize = new Size(1100, 800), ShowInTaskbar = false, BackColor = UI.Theme.Panel };
        content.Dock = DockStyle.Fill; host.Controls.Add(content);
        sw.Restart();
        host.Show();
        for (int k = 0; k < 5; k++) Application.DoEvents();
        Note($"창 띄우기 {sw.ElapsedMilliseconds}ms");
        int ups = 0; foreach (var f in frames) { w.Pad.OnFrame(f); if (f.Up && ++ups == 2) break; }   // 손잡이(창)가 생긴 뒤 다시 그려 화면 버퍼에도
        foreach (var page in new[] { "checks", "graphs", "curve", "latency" })
        {
            sw.Restart();
            w.ShowPage(page); w.Tick(true);
            for (int k = 0; k < 5; k++) Application.DoEvents();
            Note($"{page} 탭 {sw.ElapsedMilliseconds}ms");
            using var bmp = new Bitmap(host.ClientSize.Width, host.ClientSize.Height);
            host.DrawToBitmap(bmp, new Rectangle(Point.Empty, host.ClientSize));
            bmp.Save(Path.Combine(outDir, $"pendiag_{page}.png"));
        }
        host.Close();
        CanvasLatency.Clear();
        Ok(File.Exists(Path.Combine(outDir, "pendiag_latency.png")), "창 그림 4장 (pendiag_*.png)");
        App.Settings = new JsonObject();
    }

    // ---- 가짜 펜 넣기 (선택): Windows 의 합성 펜 장치로 진짜 WM_POINTER 메시지를 만들어 펜판에 그음 ----
    // COMICPAD_PENDIAG_INJECT=1 일 때만. 화면 왼쪽 위에 잠깐 맨 위 창을 띄우고, 그 자리가 정말 이 창일 때만 넣음 (다른 창에 들어가지 않게).
    static void TestInject()
    {
        if (Environment.GetEnvironmentVariable("COMICPAD_PENDIAG_INJECT") != "1") { Note("가짜 펜 넣기는 COMICPAD_PENDIAG_INJECT=1 일 때만 (화면에 잠깐 창이 뜸)"); return; }
        App.Settings = new JsonObject();
        var wa = Screen.PrimaryScreen.WorkingArea;
        using var host = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(wa.Left + 60, wa.Top + 60), ClientSize = new Size(520, 360), TopMost = true, ShowInTaskbar = false, BackColor = Color.White };
        var pad = new DiagPad { Dock = DockStyle.Fill };
        host.Controls.Add(pad);
        host.Show(); host.Activate();
        for (int k = 0; k < 20; k++) { Application.DoEvents(); Thread.Sleep(10); }
        var org = pad.PointToScreen(Point.Empty);
        Point P(int i) => new(org.X + 40 + i * 6, org.Y + 60 + i * 3);
        if (WindowFromPoint(P(0)) != pad.Handle || WindowFromPoint(P(60)) != pad.Handle) { Note("다른 창이 위에 있어 건너뜀"); host.Close(); return; }
        Note($"구조 크기: POINTER_INFO {Marshal.SizeOf<POINTER_INFO>()}, POINTER_TYPE_INFO {Marshal.SizeOf<POINTER_TYPE_INFO>()} (64비트 Windows: 96, 152)");
        IntPtr dev = CreateSyntheticPointerDevice(PT_PEN, 1, 1);
        if (dev == IntPtr.Zero) { Note($"합성 펜 장치를 만들 수 없어 건너뜀 (오류 {Marshal.GetLastWin32Error()}, Windows 10 1809 이상 필요)"); host.Close(); return; }
        int injected = 0, errors = 0; int lastErr = 0;
        try
        {
            void Send(Point pt, int flags, uint pressure, int tx, int ty)
            {
                var ti = new POINTER_TYPE_INFO { type = PT_PEN };
                ti.pen.pointerInfo.pointerType = PT_PEN;
                ti.pen.pointerInfo.pointerFlags = flags;
                ti.pen.pointerInfo.ptPixelLocation = new POINT { X = pt.X, Y = pt.Y };
                ti.pen.penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y;
                ti.pen.pressure = pressure; ti.pen.tiltX = tx; ti.pen.tiltY = ty;
                if (InjectSyntheticPointerInput(dev, new[] { ti }, 1)) injected++; else { errors++; lastErr = Marshal.GetLastWin32Error(); }
                Application.DoEvents(); Thread.Sleep(5); Application.DoEvents();
            }
            const int INRANGE = 0x2, INCONTACT = 0x4, DOWN = 0x10000, UPDATE = 0x20000, UP = 0x40000;
            Send(P(0), INRANGE | UPDATE, 0, 0, 0);
            Send(P(0), INRANGE | INCONTACT | DOWN, 100, 10, -5);
            for (int i = 1; i <= 60; i++) Send(P(i), INRANGE | INCONTACT | UPDATE, (uint)(100 + i * 15), 10 + i / 6, -5);
            Send(P(60), INRANGE | UP, 0, 0, 0);
            Send(P(60), UPDATE, 0, 0, 0);
            for (int k = 0; k < 20; k++) { Application.DoEvents(); Thread.Sleep(5); }
        }
        finally { DestroySyntheticPointerDevice(dev); host.Close(); }
        var s = pad.Session;
        var st = s.Strokes.Count > 0 ? s.Strokes[0] : new List<PenSample>();
        Note($"넣은 입력 {injected}개 (실패 {errors}, 오류 {lastErr}), 받은 메시지 {s.Messages}, 점 {s.Points}, 획 {s.Strokes.Count}, 종류 {string.Join("+", s.Types)}");
        if (injected == 0) { Note("입력을 넣지 못해 건너뜀 (화면이 잠겨 있거나 권한 없음)"); return; }
        Ok(s.SawPen && s.Strokes.Count == 1 && st.Count >= 30, $"진짜 포인터 메시지로 펜판에 한 획 ({st.Count}점)");
        double p0 = st.Count > 0 ? st.Min(x => x.P) : 0, p1 = st.Count > 0 ? st.Max(x => x.P) : 0;
        Ok(s.SawPressureMask && p0 < 0.2 && p1 > 0.9, $"필압 {p0:0.000} → {p1:0.000} (넣은 값 100/1024 → 1000/1024)");
        Ok(s.SawTiltValue && st.Any(x => x.TiltX >= 10), "기울기 받음");
        var fr = s.Frames.Where(f => f.AnyContact).ToList();
        Ok(fr.Count > 0 && fr.All(f => !double.IsNaN(f.PresentT)), $"메시지마다 그려 올림 ({fr.Count}장), QPC {(s.AllQpc ? "있음" : "없음")}");
        var lat = PenStats.Latency(s.Frames);
        Note($"이 기계에서 잰 입력→창 지연 (가짜 펜): 평균 {lat.Avg:0.00}ms, 95% {lat.P95:0.00}ms ({lat.N}장)");
    }

    [StructLayout(LayoutKind.Explicit)]
    struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public POINTER_PEN_INFO pen;
        [FieldOffset(8 + 96 + 47)] public byte pad;   // 합집합 크기 = POINTER_TOUCH_INFO (POINTER_INFO + 48)
    }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateSyntheticPointerDevice(int pointerType, uint maxCount, int mode);
    [DllImport("user32.dll", SetLastError = true)] static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] info, uint count);
    [DllImport("user32.dll")] static extern void DestroySyntheticPointerDevice(IntPtr device);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
}
