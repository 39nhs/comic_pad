// 작업 흐름 모듈 시험 (창 없이): ComicPadNative.exe --selftest workflow 결과폴더  (COMICPAD_DATA·COMICPAD_SETTINGS 는 반드시 임시로)
//  일정: 저장·다시 읽기(story.json·.cpad), 웹 판 status 와 맞추기, 단계 진행, 속도 계산, 날짜 칸(자정 넘김), 마감 지남
//  참고 자료 보드: 넣기·옮기기·크기·회전·자르기·저장·읽기·중복 없애기·정리, 레이어로 넣기, 스포이트
//  A/B 비교: 밀어서 비교·겹쳐 보기·어니언·달라진 곳·나란히 픽셀, 예전 판 목록(버전·자동 저장·백업)
//  피드백 보기: 겹침 켜기/끄기, 빨간펜, 메모 넣기·해결 표시
//  창 그림: 결과폴더/*.png (DrawToBitmap)
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;

namespace ComicPadNative.Modules.Workflow;

public static class WorkflowTest
{
    static readonly StringBuilder log = new();
    static int fail, pass;
    static void Ok(bool cond, string name) { log.AppendLine((cond ? "통과  " : "실패  ") + name); if (cond) pass++; else fail++; }
    static readonly DateTime Today = new(2026, 11, 19);

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        string data = Environment.GetEnvironmentVariable("COMICPAD_DATA");
        if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMICPAD_SETTINGS")))
        { File.WriteAllText(Path.Combine(outDir, "result.txt"), "COMICPAD_DATA 와 COMICPAD_SETTINGS 를 임시로 정해 주세요"); return 2; }
        App.Settings = new JsonObject();
        ScheduleUi.Today = () => Today;
        void T(string n, Action a) { try { a(); } catch (Exception ex) { Ok(false, $"{n}: 예외 {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}"); } }
        T("일정 저장·읽기", TestScheduleRoundTrip);
        T("한 장 문서 일정", TestDocSchedule);
        T("단계 진행", TestPipeline);
        T("속도 계산", TestPace);
        T("날짜 칸", TestBuckets);
        T("마감 지남", TestOverdue);
        T("보드", () => TestBoard(outDir));
        T("레이어로 넣기", TestToLayer);
        T("A/B 합치기", TestCompose);
        T("예전 판 목록", TestSources);
        T("피드백 보기", TestFeedback);
        T("창 그림", () => Shots(outDir));
        log.AppendLine(fail == 0 ? $"모두 통과 ({pass}개)" : $"통과 {pass}개, 실패 {fail}개");
        File.WriteAllText(Path.Combine(outDir, "result.txt"), log.ToString());
        return fail;
    }

    // ---- 일정 ------------------------------------------------------------------------------
    const string Story0 = "일정시험";
    static List<string> MakeStory(string story, int n, int w = 400, int h = 560)
    {
        for (int i = 1; i <= n; i++) Story.SavePageDoc(Story.BlankPage(w, h, $"{story} {i}쪽", i % 2 == 0), $"{story}/{Story.PageName(i)}.cpad");
        return Story.ListPages(story);
    }

    static void TestScheduleRoundTrip()
    {
        var pages = MakeStory(Story0, 4);
        var ws = WorkSchedule.ForStory(Story0);
        Ok(ws.Pages.Count == 4 && ws.Plan.Stages.Select(s => s.Label).SequenceEqual(new[] { "콘티", "밑그림", "펜선", "채색", "식자", "완료" }), "기본 단계 6개 (콘티→밑그림→펜선→채색→식자→완료)");
        ws.Plan.SetStage(pages[0], "done", Today.AddDays(-2));
        ws.Plan.SetStage(pages[1], "ink", Today.AddDays(-1));
        ws.Plan.SetStage(pages[2], "letter", Today);
        ws.Plan.SetDue(pages[3], Today.AddDays(3));
        ws.Plan.Deadline = Today.AddDays(9);
        ws.Plan.Episodes = new List<Episode> { new() { Name = "1화", Due = Today.AddDays(5), Pages = pages.Take(2).ToList() }, new() { Name = "2화", Pages = pages.Skip(2).ToList() } };
        ws.Save();
        var info = Story.ReadInfo(Story0);
        Ok((string)info["status"]?[pages[0]] == "done" && (string)info["status"]?[pages[1]] == "line" && (string)info["status"]?[pages[2]] == "color" && info["status"]?[pages[3]] == null, "웹 판 status 로 맞춰 씀 (완료→done, 펜선→line, 식자→color)");
        Ok((string)info["deadline"] == "2026-11-28", "웹 판 deadline 같은 값");
        var re = WorkSchedule.ForStory(Story0);
        Ok(re.Plan.StageOf(pages[1]) == "ink" && re.Plan.StageOf(pages[2]) == "letter" && re.Plan.DueOf(pages[3]) == Today.AddDays(3) && re.Plan.Deadline == Today.AddDays(9), "다시 읽으면 단계·페이지 마감·작품 마감 그대로");
        var eps = re.Plan.Episodes;
        Ok(eps.Count == 2 && eps[0].Name == "1화" && eps[0].Due == Today.AddDays(5) && eps[0].Pages.SequenceEqual(pages.Take(2)) && eps[1].Due == null, "회차 이름·마감·페이지 그대로");
        Ok(re.Plan.EffectiveDue(pages[0]) == Today.AddDays(5) && re.Plan.EffectiveDue(pages[3]) == Today.AddDays(3) && re.Plan.EffectiveDue(pages[2]) == Today.AddDays(9), "실제 마감: 페이지 → 회차 → 작품 순서");
        // 웹 판(또는 예전 진행표)이 status 를 바꾸면 그 값을 따름
        info["status"][pages[1]] = "done";
        ((JsonObject)info["status"]).Remove(pages[2]);
        info["deadline"] = "2026-12-01";
        Story.WriteInfo(Story0, info);
        var w2 = WorkSchedule.ForStory(Story0);
        Ok(w2.Plan.StageOf(pages[1]) == "done" && w2.Plan.StageOf(pages[2]) == null && w2.Plan.StageOf(pages[0]) == "done" && w2.Plan.Deadline == new DateTime(2026, 12, 1), "웹 판에서 바꾼 status·deadline 을 따름");
        // 같은 웹 값이면 세부 단계 유지 (식자 → color 그대로면 식자)
        w2.Plan.SetStage(pages[2], "letter", Today); w2.Save();
        var info2 = Story.ReadInfo(Story0);
        Ok((string)info2["status"][pages[2]] == "color" && WorkSchedule.ForStory(Story0).Plan.StageOf(pages[2]) == "letter", "웹 값이 같으면 세부 단계(식자) 유지");
        Ok(info2["time"] == null || info2["time"] is JsonObject, "story.json 다른 값 보존");
        // 색인
        var idx = WorkIndex.Read();
        Ok(idx["works"]?["story:" + Story0]?["pages"]?.GetValue<int>() == 4 && (idx["works"]["story:" + Story0]["dues"] as JsonArray)?.Count >= 3, "모든 작품 색인에 마감 기록 (data/workflow/index.json)");
    }

    static void TestDocSchedule()
    {
        var d = Document.New(300, 200, "한장");
        var ws = WorkSchedule.For(d);
        Ok(!ws.IsStory && ws.Pages.SequenceEqual(new[] { WorkSchedule.SinglePage }), "한 장 문서 = 페이지 하나");
        ws.Plan.SetStage(WorkSchedule.SinglePage, "color", Today);
        ws.Plan.Deadline = Today.AddDays(2);
        ws.Save();
        string path = Path.Combine(App.DataDir, "한장.cpad");
        CpadIO.Save(d, path);
        var raw = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        Ok(raw["format"]?.ToString() == "comicpad" && raw["schedule"] is JsonObject && raw["layers"] is JsonArray, ".cpad 에 schedule 저장 (웹 판 형식 그대로 + 모르는 값 하나)");
        var d2 = CpadIO.Load(path);
        var w2 = WorkSchedule.For(d2);
        Ok(w2.Plan.StageOf(WorkSchedule.SinglePage) == "color" && w2.Plan.Deadline == Today.AddDays(2), ".cpad 다시 읽기 → 단계·마감 그대로");
        var d3 = CpadIO.FromJson(CpadIO.ToJson(d2));
        Ok(U.Same(d3.Extra["schedule"], d2.Extra["schedule"]), "왕복 저장해도 같은 값");
    }

    static void TestPipeline()
    {
        var p = new Plan();
        string pg = "x";
        Ok(p.StageOf(pg) == null && p.Progress(pg) == 0, "처음엔 단계 없음");
        var seen = new List<string>();
        for (int i = 0; i < 7; i++) seen.Add(p.Advance(pg, Today.AddDays(i)));
        Ok(seen.SequenceEqual(new[] { "conti", "sketch", "ink", "color", "letter", "done", "done" }), "다음 단계로: 콘티→밑그림→펜선→채색→식자→완료 (완료에서 멈춤)");
        Ok(p.Reached(pg, "conti") == Today && p.Reached(pg, "done") == Today.AddDays(5) && p.IsDone(pg) && Math.Abs(p.Progress(pg) - 1) < 1e-9, "단계마다 닿은 날 기록");
        p.SetStage(pg, "ink", Today.AddDays(9));
        Ok(p.Reached(pg, "color") == null && p.Reached(pg, "ink") == Today.AddDays(2) && Math.Abs(p.Progress(pg) - 0.4) < 1e-9, "되돌리면 뒤 단계 기록 지움, 앞 기록 유지");
        // 사용자 단계
        var custom = ScheduleUi.ParseStages("러프 #ff0000\n선화\n완성 #00aa00", p.Stages);
        Ok(custom.Count == 3 && custom[0].Color == "#ff0000" && custom.Select(s => s.Web).SequenceEqual(new[] { "conti", "line", "done" }), "단계 바꾸기 (3단계, 웹 판 값 conti·line·done)");
        p.SetStages(custom);
        Ok(p.Stages.Count == 3 && p.StageOf(pg) == null && p.LastId == custom[2].Id, "없어진 단계에 있던 페이지는 단계 지움");
        var same = ScheduleUi.ParseStages("콘티\n펜선\n완료", Plan.DefaultStages.ToList());
        Ok(same[0].Id == "conti" && same[1].Id == "ink" && same[2].Id == "done", "이름이 같은 단계는 id 유지");
        Ok(ScheduleUi.ParseRange("1-3, 5", new[] { "a", "b", "c", "d", "e" }).SequenceEqual(new[] { "a", "b", "c", "e" }) && ScheduleUi.RangeText(new[] { "a", "b", "c", "e" }, new[] { "a", "b", "c", "d", "e" }) == "1-3, 5", "페이지 범위 글 ↔ 목록");
        var spread = ScheduleWindow.Spread(new[] { "a", "b", "c", "d" }, Today, Today.AddDays(7));
        Ok(spread[^1].due == Today.AddDays(7) && spread[0].due == Today.AddDays(1) && spread.Select(x => x.due).SequenceEqual(spread.Select(x => x.due).OrderBy(x => x)), "마감 고르게 나누기 (마지막 = 작품 마감)");
    }

    static void TestPace()
    {
        var p = new Plan();
        var pages = Enumerable.Range(1, 10).Select(i => "p" + i).ToList();
        // 기간(7일: 11/13~11/19) 안에 완료 2쪽, 기간 전에 완료 2쪽, 펜선까지 2쪽, 나머지 4쪽 미정
        foreach (var x in pages.Take(2)) { p.SetStage(x, "conti", new DateTime(2026, 11, 1)); p.SetStage(x, "done", new DateTime(2026, 11, 15)); }
        foreach (var x in pages.Skip(2).Take(2)) { p.SetStage(x, "done", new DateTime(2026, 11, 10)); }
        foreach (var x in pages.Skip(4).Take(2)) { p.SetStage(x, "conti", new DateTime(2026, 11, 1)); p.SetStage(x, "ink", new DateTime(2026, 11, 18)); }
        var pace = Sched.Pace(p, pages, Today, new DateTime(2026, 11, 28));
        Ok(Math.Abs(pace.RemainingUnits - 5.2) < 1e-9 && pace.RemainingPages == 6, $"남은 일 5.2쪽분 ({pace.RemainingUnits})");
        Ok(pace.DaysLeft == 10 && Math.Abs(pace.NeededPerDay.Value - 0.52) < 1e-9, $"남은 10일 (오늘·마감일 포함), 필요 0.52쪽/일 ({pace.NeededPerDay})");
        Ok(Math.Abs(pace.ActualPerDay - 0.4) < 1e-9, $"실제 0.4쪽/일 = (완료 2 + 펜선 0.4×2) ÷ 7 ({pace.ActualPerDay})");
        Ok(pace.Verdict == "조금 늦음" && pace.Behind, $"판정: 조금 늦음 ({pace.Verdict})");
        Ok(pace.EstimatedFinish == new DateTime(2026, 12, 1), $"이 속도면 12/1 끝 ({pace.EstimatedFinish:yyyy-MM-dd})");
        var late = Sched.Pace(p, pages, Today, new DateTime(2026, 11, 18));
        Ok(late.Verdict == "마감 지남" && late.DaysLeft == 0, "마감 지나면 판정 \"마감 지남\"");
        var easy = Sched.Pace(p, pages, Today, new DateTime(2026, 12, 31));
        Ok(easy.Verdict == "순조로움" && !easy.Behind, "여유 있으면 \"순조로움\"");
        var none = Sched.Pace(p, pages, Today, null);
        Ok(none.Verdict == "마감 없음" && none.NeededPerDay == null, "마감 없으면 필요 속도 없음");
        var all = new Plan(); foreach (var x in pages) all.SetStage(x, "done", Today);
        Ok(Sched.Pace(all, pages, Today, Today).Verdict == "완료", "모두 완료");
    }

    static void TestBuckets()
    {
        var b = WorkLog.Buckets(new[]
        {
            (new DateTime(2026, 11, 19, 0, 1, 0), 180.0),    // 11/18 23:58 ~ 11/19 00:01 → 120 + 60
            (new DateTime(2026, 11, 19, 10, 0, 0), 3600.0),
            (new DateTime(2026, 11, 20, 0, 0, 0), 30.0),     // 자정에 딱 끝남 → 11/19
            (new DateTime(2026, 11, 17, 12, 0, 0), 90000.0), // 25시간 → 11/17 12시간 + 11/16 13시간
        });
        Ok(Math.Abs(b[new DateTime(2026, 11, 18)] - 120) < 1e-6 && Math.Abs(b[new DateTime(2026, 11, 19)] - (60 + 3600 + 30)) < 1e-6, "자정 넘는 작업을 두 날로 나눔");
        Ok(Math.Abs(b[new DateTime(2026, 11, 17)] - 43200) < 1e-6 && Math.Abs(b[new DateTime(2026, 11, 16)] - 46800) < 1e-6 && !b.ContainsKey(new DateTime(2026, 11, 15)), "하루보다 긴 작업도 날마다");
        // 문서 기록 → doc.Extra · 색인 → 달력
        var d = CpadIO.Load(Story.Full(Story0 + "/p001.cpad")); d.FilePath = Story.Full(Story0 + "/p001.cpad");
        WorkLog.Record(d, new DateTime(2026, 11, 18, 0, 0, 30), 90);   // 11/17 60초 + 11/18 30초
        WorkLog.Record(d, new DateTime(2026, 11, 18, 15, 0, 0), 600);
        var dd = WorkLog.DocDays(d);
        Ok(Math.Abs(dd[new DateTime(2026, 11, 17)] - 60) < 0.2 && Math.Abs(dd[new DateTime(2026, 11, 18)] - 630) < 0.2, "문서 workLog: 날짜별 초");
        var other = Document.New(10, 10, "다른그림");
        WorkLog.Record(other, new DateTime(2026, 11, 18, 9, 0, 0), 300);
        WorkIndex.Flush();
        var days = WorkIndex.Days();
        Ok(Math.Abs(days[new DateTime(2026, 11, 18)][Story0 + "/p001.cpad"] - 630) < 0.2 && Math.Abs(days[new DateTime(2026, 11, 18)]["unsaved:다른그림"] - 300) < 0.2, "data/workflow/index.json 날짜·페이지별 시간");
        var ws = WorkSchedule.ForStory(Story0);
        ws.Plan.SetDue(ws.Pages[1], new DateTime(2026, 11, 10)); ws.Plan.SetStage(ws.Pages[1], "ink", Today); ws.Save();
        var mine = Calendar.Month(2026, 11, ws, false, Today);
        var all = Calendar.Month(2026, 11, ws, true, Today);
        Ok(Math.Abs(mine[new DateTime(2026, 11, 18)].Seconds - 630) < 0.2 && Math.Abs(all[new DateTime(2026, 11, 18)].Seconds - 930) < 0.2, "달력 칸: 이 작품 630초 / 모든 작품 930초");
        Ok(mine[new DateTime(2026, 11, 10)].Dues.Any(x => x.overdue) && mine.Count == 30, "달력: 지난 페이지 마감은 빨강 표시 (11월 30칸)");
    }

    static void TestOverdue()
    {
        var p = new Plan();
        var pages = new List<string> { "a", "b", "c", "d" };
        p.SetDue("a", new DateTime(2026, 11, 15));                                     // 4일 지남
        p.SetDue("b", new DateTime(2026, 11, 15)); p.SetStage("b", "done", Today);     // 끝냄
        p.SetDue("c", new DateTime(2026, 11, 19));                                     // 오늘 = 아직 아님
        p.Episodes = new List<Episode> { new() { Name = "1화", Due = new DateTime(2026, 11, 17), Pages = new() { "b", "d" } }, new() { Name = "2화", Due = new DateTime(2026, 11, 10), Pages = new() { "b" } } };
        p.Deadline = new DateTime(2026, 11, 28);
        var o = Sched.Overdue(p, pages, Today);
        Ok(o.Count == 2 && o[0].Kind == "page" && o[0].Page == "a" && o[0].DaysLate == 4 && o[1].Kind == "episode" && o[1].Label == "1화" && o[1].DaysLate == 2, $"마감 지남: a쪽 4일, 1화 2일 (끝낸 b·오늘 마감 c·끝난 2화 제외) [{string.Join(", ", o.Select(x => x.Label + x.DaysLate))}]");
        p.Deadline = new DateTime(2026, 11, 1);
        Ok(Sched.Overdue(p, pages, Today).Any(x => x.Kind == "work" && x.DaysLate == 18), "작품 마감 지남 18일");
        var up = Sched.Upcoming(p, pages, Today);
        Ok(up.Count == 1 && up[0].label == "c", "다가오는 마감: 오늘 마감 c");
    }

    // ---- 보드 ------------------------------------------------------------------------------
    static Bitmap Solid(int w, int h, Color c, Action<Graphics> more = null)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b); g.Clear(c); more?.Invoke(g);
        return b;
    }

    static void TestBoard(string outDir)
    {
        int n0 = RefStore.ImageCount;
        using var red = Solid(100, 50, Color.Red, g => g.FillRectangle(Brushes.Black, 0, 0, 10, 50));
        using var red2 = new Bitmap(red);
        using var blue = Solid(80, 80, Color.Blue);
        var b = new RefBoard { Key = "시험보드", Name = "시험" };
        var a = RefOps.FromBitmap(red, "빨강", 0, 0); a.Scale = 1;
        var a2 = RefOps.FromBitmap(red2, "빨강2", 300, 0); a2.Scale = 1;
        var c = RefOps.FromBitmap(blue, "파랑", 0, 200); c.Scale = 1;
        b.Items.AddRange(new[] { a, a2, c });
        Ok(a.Hash == a2.Hash && a.Hash != c.Hash && RefStore.ImageCount - n0 == 2, "같은 그림은 파일 하나 (해시로 중복 없앰)");
        // 같은 그림을 JPG→다른 경로로 넣어도 픽셀이 같으면 같은 해시 (PNG 파일로)
        string tmp = Path.Combine(App.DataDir, "같은그림.png"); red.Save(tmp, ImageFormat.Png);
        var a3 = RefOps.FromFile(tmp, 0, 0);
        Ok(a3 != null && a3.Hash == a.Hash && RefStore.ImageCount - n0 == 2, "파일에서 넣어도 같은 해시");
        var bb = RefOps.Bounds(a);
        Ok(Math.Abs(bb.Width - 100) < 0.01 && Math.Abs(bb.Height - 50) < 0.01 && Math.Abs(bb.X + 50) < 0.01, "크기·가운데 기준 자리");
        a.X = 10; a.Y = 20; a.Scale = 2; a.Rot = 90;
        bb = RefOps.Bounds(a);
        Ok(Math.Abs(bb.Width - 100) < 0.01 && Math.Abs(bb.Height - 200) < 0.01 && Math.Abs(bb.X - (10 - 50)) < 0.01 && Math.Abs(bb.Y - (20 - 100)) < 0.01, "옮기기·2배·90° 회전 → 테두리 100×200");
        Ok(RefOps.Hit(a, new PointF(10, 110)) && !RefOps.Hit(a, new PointF(70, 20)) && RefOps.HitTest(b, new PointF(10, 110)) == a, "회전한 그림 누르기 판정");
        // 자르기: 오른쪽 절반만 → 남은 부분이 화면에서 제자리
        a.Rot = 0; a.Scale = 1; a.X = 0; a.Y = 0;
        RefOps.SetCrop(a, new RectangleF(50, 0, 50, 50));
        Ok(a.Crop == new RectangleF(50, 0, 50, 50) && Math.Abs(a.X - 25) < 0.01 && Math.Abs(RefOps.LocalSize(a).Width - 50) < 0.01, "자르기: 오른쪽 절반, 가운데는 +25 (제자리)");
        a.FlipH = true;
        // 묶기
        string grp = RefOps.GroupItems(new[] { a, a2 });
        Ok(grp != null && RefOps.Expand(b, new[] { a }).Count == 2, "묶기: 하나를 고르면 묶음 전체");
        var note = new RefItem { Kind = "note", X = 200, Y = 150, Text = "눈 모양 참고", Scale = 1 };
        b.Items.Add(note);
        RefStore.Save(b);
        var b2 = RefStore.Load("시험보드");
        Ok(b2.Items.Count == 4 && U.Same(b2.ToJson()["items"], b.ToJson()["items"]), "보드 저장·읽기 (data/refboard/boards/시험보드.json)");
        var a_ = b2.Find(a.Id);
        Ok(a_.Crop == a.Crop && a_.FlipH && a_.Group == grp && b2.Find(note.Id).Text == "눈 모양 참고", "자르기·반전·묶음·메모 그대로");
        // 스포이트 (보이는 그대로)
        b.Zoom = 1; b.PanX = 200; b.PanY = 200; b.ViewFlip = false; b.ViewGray = false;
        var pc = RefOps.Sample(b, 600, 500, new Point(200 + 0, 200 + 200));   // 파랑 가운데
        Ok(pc.B > 200 && pc.R < 30, $"스포이트: 파랑 ({pc.R},{pc.G},{pc.B})");
        b.ViewGray = true;
        var pg = RefOps.Sample(b, 600, 500, new Point(200, 400));
        Ok(Math.Abs(pg.R - pg.B) < 3 && pg.R > 20 && pg.R < 40, $"흑백 보기에서 스포이트 = 회색 ({pg.R},{pg.G},{pg.B})");
        b.ViewGray = false; b.ViewFlip = true;
        var pf = RefOps.Sample(b, 600, 500, new Point(600 - 200, 400));
        Ok(pf.B > 200, "좌우 반전 보기: 거울 자리에서 같은 색");
        b.ViewFlip = false;
        using (var shot = RefOps.Render(b, 700, 520)) shot.Save(Path.Combine(outDir, "refboard_render.png"));
        // 정리: 파랑을 빼면 그림 파일 하나 지움
        b.Items.Remove(c); RefStore.Save(b);
        int del = RefStore.Cleanup();
        Ok(del == 1 && !File.Exists(RefStore.PathOf(c.Hash)) && File.Exists(RefStore.PathOf(a.Hash)), "안 쓰는 그림 정리 (1개 지움, 쓰는 것은 남김)");
        // 큰 그림은 4096 으로
        using var big = new Bitmap(5000, 100);
        var bigItem = RefOps.FromBitmap(big, "큰그림", 0, 0);
        var bi = RefStore.Image(bigItem.Hash);
        Ok(bi.Width == 4096 && bi.Height == 81, $"큰 그림은 긴 쪽 4096 ({bi.Width}×{bi.Height})");
        var c2 = RefOps.FromBitmap(blue, "파랑", 0, 200); c2.Scale = 1;
        Ok(File.Exists(RefStore.PathOf(c2.Hash)), "지운 그림을 다시 넣으면 파일 다시 만듦");
        bigItem.Scale = 0.05; bigItem.Y = -120;
        b.Items.Add(c2); b.Items.Add(bigItem); RefStore.Save(b);   // 창 그림용
    }

    static void TestToLayer()
    {
        var d = Document.New(400, 300, "넣기시험");
        App.SetDoc(d);
        using var img = Solid(100, 50, Color.FromArgb(255, 0, 200, 0));
        var it = RefOps.FromBitmap(img, "초록", 0, 0);
        RefOps.SetCrop(it, new RectangleF(0, 0, 50, 50));
        int n = d.Layers.Count;
        var l = RefOps.ToLayer(d, it, 200, 150);
        Ok(d.Layers.Count == n + 1 && l != null && l.Name == "참고: 초록" && d.ActiveId == l.Id, "새 레이어 (이름 \"참고: 초록\", 지금 레이어)");
        uint px = l.Px.Get(200, 150), out1 = l.Px.Get(170, 150), edge = l.Px.Get(176, 150);
        Ok(Px.Unpremul(px) == 0xFF00C800 && out1 == 0 && (edge >> 24) > 200, $"놓은 자리 가운데 50×50 초록 ({px:x8} / 밖 {out1:x8})");
        var b = l.Px.Bounds();
        Ok(b.W == 50 && b.H == 50 && b.X == 175 && b.Y == 125, $"그림 자리 {b}");
        // 회전한 그림은 돌아간 모양으로
        it.Crop = null; it.Rot = 90;
        var l2 = RefOps.ToLayer(d, it, 100, 100);
        var b2 = l2.Px.Bounds();
        Ok(Math.Abs(b2.W - 50) <= 1 && Math.Abs(b2.H - 100) <= 1, $"90° 돌린 그림 → 50×100 ({b2.W}×{b2.H})");
        // 문서보다 크면 90% 로
        using var huge = Solid(1000, 1000, Color.Gray);
        var hi = RefOps.FromBitmap(huge, "큼", 0, 0);
        var l3 = RefOps.ToLayer(d, hi, 200, 150);
        var b3 = l3.Px.Bounds();
        Ok(b3.H == 270 && b3.W == 270, $"문서보다 크면 90% 로 ({b3.W}×{b3.H})");
        App.History.Undo(d);
        Ok(d.Layers.Count == n + 2 && d.ById(l3.Id) == null, "실행취소 한 번에 레이어 빠짐");
    }

    // ---- A/B ------------------------------------------------------------------------------
    static Bitmap Line(int x) => Solid(100, 60, Color.White, g => g.FillRectangle(Brushes.Black, x, 0, 4, 60));

    static void TestCompose()
    {
        using var a0 = Line(20); using var b0 = Line(70);
        using var small = Solid(50, 30, Color.White);
        var (na, nb) = AbRender.Normalize(small, b0);
        Ok(na.Width == 100 && na.Height == 60 && na.GetPixel(80, 50).R == 255, "크기가 다르면 큰 쪽에 맞추고 흰 바탕");
        na.Dispose(); nb.Dispose();
        using var split = AbRender.Compose("split", a0, b0, 0.5, out _);
        Ok(split.GetPixel(21, 30).R == 0 && split.GetPixel(71, 30).R == 0 && split.GetPixel(49, 30).R == 255 && split.GetPixel(50, 30).R == 255, "밀어서 비교 50%: 왼쪽 A 의 선, 오른쪽 B 의 선");
        using var split2 = AbRender.Compose("split", a0, b0, 0.1, out _);
        Ok(split2.GetPixel(21, 30).R == 255 && split2.GetPixel(71, 30).R == 0, "칸막이 10%: A 선이 안 보임");
        using var ov = AbRender.Compose("overlay", a0, b0, 0.25, out _);
        int va = ov.GetPixel(21, 30).R, vb = ov.GetPixel(71, 30).R, vw = ov.GetPixel(50, 30).R;
        Ok(Math.Abs(va - 191) <= 1 && Math.Abs(vb - 64) <= 1 && vw == 255, $"겹쳐 보기 A 25%: A선 {va}≈191, B선 {vb}≈64");
        using var on = AbRender.Compose("onion", a0, b0, 0, out _);
        var oa = on.GetPixel(21, 30); var ob = on.GetPixel(71, 30); var ow = on.GetPixel(50, 30);
        Ok(oa.R == 255 && oa.G == 0 && oa.B == 0 && ob.R == 0 && ob.G == 255 && ob.B == 255 && ow.R == 255 && ow.G == 255 && ow.B == 255, "어니언: A 에만 = 빨강, B 에만 = 청록, 같은 곳 = 흰색");
        using var df = AbRender.Compose("diff", a0, b0, 0, out double ch);
        Ok(df.GetPixel(21, 30).R == 235 && df.GetPixel(21, 30).G == 40 && df.GetPixel(71, 30).R == 235 && df.GetPixel(50, 30).R > 240 && Math.Abs(ch - 0.08) < 1e-9, $"달라진 곳: 두 선 빨강, 바뀐 비율 8% ({ch * 100:0.##}%)");
        using var same = AbRender.Compose("diff", a0, a0, 0, out double ch0);
        Ok(ch0 == 0, "같은 그림이면 바뀐 곳 0");
        using var side = AbRender.Compose("side", a0, b0, 0, out _);
        Ok(side.Width == 216 && side.GetPixel(21, 30).R == 0 && side.GetPixel(116 + 71, 30).R == 0 && side.GetPixel(116 + 21, 30).R == 255, "나란히: 폭 100+16+100, 왼쪽 A 오른쪽 B");
    }

    static void TestSources()
    {
        // 지금 그림 (작품 페이지) + 버전 기록 2개 + 자동 저장 + 백업 zip
        string rel = Story0 + "/p001.cpad";
        var d = CpadIO.Load(Story.Full(rel)); d.FilePath = Story.Full(rel); d.Name = "p001";
        string vdir = Path.Combine(App.DataDir, "versions", Story0, "p001"); Directory.CreateDirectory(vdir);
        var old = Story.BlankPage(400, 560, "예전", false);
        CpadIO.Save(old, Path.Combine(vdir, "20261110-090000.cpad"));
        CpadIO.Save(old, Path.Combine(vdir, "20261112-101500.cpad"));
        string adir = Path.Combine(App.DataDir, "autosave"); Directory.CreateDirectory(adir);
        CpadIO.Save(old, Path.Combine(adir, "p001.cpad"));
        string bdir = Path.Combine(App.DataDir, "backups"); Directory.CreateDirectory(bdir);
        string zip = Path.Combine(bdir, "ComicPad_백업_2026-11-11_120000.zip");
        if (File.Exists(zip)) File.Delete(zip);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("projects/" + rel);
            using var w = new StreamWriter(e.Open());
            w.Write(CpadIO.ToJson(old).ToJsonString());
        }
        var list = VersionSources.For(d);
        var kinds = list.Select(s => s.Kind).ToList();
        Ok(kinds.Take(4).SequenceEqual(new[] { "file", "version", "version", "autosave" }) && list.Count(x => x.Kind == "backup" && x.Label.Contains("2026-11-11_120000")) == 1, $"예전 판: 마지막 저장·버전 2·자동 저장·백업 ({string.Join(",", kinds)})");
        Ok(list[1].Label.Contains("2026-11-12 10:15:00"), "버전은 새 것부터");
        var fromZip = list[^1].Load();
        Ok(fromZip.W == 400 && fromZip.Layers.Count == 1, "백업 zip 안의 판 읽기");
        // 창: 합친 그림 (A = 예전, B = 지금 → 컷 틀 차이)
        var win = new AbCompareWindow();
        try
        {
            App.SetDoc(d);
            win.SetSources(list, 1, -1);
            win.SetMode("diff", 0.5);
            Ok(win.Composed != null && win.Composed.Width > 0, "A/B 비교 창: 달라진 곳 그림");
        }
        finally { win.Dispose(); }
    }

    // ---- 피드백 ------------------------------------------------------------------------------
    static string MakeFeedback(Document mine)
    {
        var ed = new Document(mine.W, mine.H) { Name = mine.Name };
        var l = new Layer(ed, "빨간펜") { Rough = true };
        var buf = new uint[20 * 10]; Array.Fill(buf, 0xFF2040C0);   // 파란 선 (빨간펜 보기에서 빨강)
        l.Px.Write(new Rect(100, 100, 20, 10), buf);
        ed.Extra["notes"] = new JsonArray(
            new JsonObject { ["id"] = "n1", ["x"] = 110, ["y"] = 105, ["text"] = "손 모양 다시", ["color"] = "#e5484d" },
            new JsonObject { ["id"] = "n2", ["x"] = 300, ["y"] = 200, ["text"] = "대사 줄이기", ["color"] = "#e5484d" });
        return Feedback.Build(ed, new[] { l }, true, "편집자").ToJsonString();
    }

    static void TestFeedback()
    {
        var doc = Document.New(400, 300, "피드백시험");
        App.SetDoc(doc);
        string text = MakeFeedback(doc);
        var s = FeedbackView.Load(doc, text, null);
        Ok(s.From == "편집자" && s.LayerCount == 1 && s.Notes.Count == 2 && s.Overlay.Width == 400, "피드백 파일 읽기 (레이어 1·메모 2, 원고 크기)");
        var po = s.Overlay.GetPixel(110, 105); var pr = s.Red.GetPixel(110, 105); var pe = s.Overlay.GetPixel(50, 50);
        Ok(po.A == 255 && Math.Abs(po.B - 0xC0) <= 2 && pr.R == 229 && pr.G == 57 && pr.A == 255 && pe.A == 0, "겹침 그림: 원래 색 / 빨간펜 / 빈 곳 투명");
        Ok(doc.Layers.Count == 1 && Feedback.Notes(doc).Count == 0, "겹쳐 보기만 — 문서는 그대로");
        var v = new ViewXf { Zoom = 1 };
        Color DrawAt(int x, int y)
        {
            using var bmp = new Bitmap(400, 300); using var g = Graphics.FromImage(bmp); g.Clear(Color.White);
            FeedbackView.Draw(g, v);
            return bmp.GetPixel(x, y);
        }
        FeedbackView.Set(s, true); FeedbackView.RedPen = true; FeedbackView.Opacity = 1;
        var on = DrawAt(110, 105);
        FeedbackView.Toggle();
        var off = DrawAt(110, 105);
        Ok(on.R > 200 && on.G < 80 && off.R == 255 && off.G == 255 && !FeedbackView.On, $"켜기 = 빨간 선 ({on.R},{on.G},{on.B}), 끄기 = 그대로");
        Color part;
        using (var bmp = new Bitmap(400, 300))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White); g.SetClip(new Rectangle(104, 100, 12, 12));
            FeedbackView.Draw(g, v);
            part = bmp.GetPixel(110, 105);
        }
        Ok(part.R == 255 && part.G == 255, "꺼져 있으면 일부 다시 그리기에도 안 나옴");
        FeedbackView.Toggle();
        using (var bmp = new Bitmap(400, 300))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White); g.SetClip(new Rectangle(104, 100, 12, 12));
            FeedbackView.Draw(g, v);
            part = bmp.GetPixel(110, 105);
        }
        Ok(part.R > 200 && part.G < 80 && Math.Abs(part.G - on.G) <= 2, $"일부만 다시 그려도(clip) 같은 색 ({part.R},{part.G},{part.B})");
        FeedbackView.RedPen = false;
        var orig = DrawAt(110, 105);
        Ok(orig.B > 150 && orig.R < 60, "빨간펜 끄면 원래 색");
        v.Zoom = 0.5; v.PanX = 10;
        var z = DrawAt(10 + 55, 52);
        Ok(z.B > 150, "확대·이동한 화면에서도 같은 자리");
        int k = FeedbackView.MergeNotes(doc, s);
        int k2 = FeedbackView.MergeNotes(doc, s);
        var notes = Feedback.Notes(doc);
        Ok(k == 2 && k2 == 0 && notes.Count == 2 && (string)notes[0]["author"] == "편집자", "메모를 작품에 넣기 (두 번 넣어도 한 번)");
        string id = (string)notes[1]["id"];
        FeedbackView.SetDone(doc, id, true);
        Ok((bool?)Feedback.Notes(doc)[1]["done"] == true && (bool?)Feedback.Notes(doc)[0]["done"] != true, "메모 해결 표시 (done = true, 웹 판과 같은 값)");
        App.History.Undo(doc);
        Ok((bool?)Feedback.Notes(doc)[1]["done"] != true, "해결 표시 실행취소");
        // 받아 둔 피드백 폴더 보이기/숨기기
        Feedback.Import(doc, text);
        var folders = FeedbackView.Folders(doc);
        Ok(folders.Count == 1, "받은 피드백 폴더 찾기");
        FeedbackView.SetFolderVisible(doc, folders[0], false);
        Ok(!folders[0].Visible, "피드백 폴더 숨기기");
        FeedbackView.SetFolderVisible(doc, folders[0], true);
        FeedbackView.RedPen = true; v.Zoom = 1; v.PanX = 0;
    }

    // ---- 창 그림 ------------------------------------------------------------------------------
    static void Save(Bitmap b, string dir, string name) { using (b) b.Save(Path.Combine(dir, name)); }

    static void Shots(string dir)
    {
        // 일정: 작품 페이지를 열어 둔 상태로
        var pages = Story.ListPages(Story0);
        var ws = WorkSchedule.ForStory(Story0);
        ws.Plan.SetStage(pages[0], "done", new DateTime(2026, 11, 15));
        ws.Plan.SetStage(pages[1], "ink", new DateTime(2026, 11, 18));
        ws.Plan.SetStage(pages[2], "color", new DateTime(2026, 11, 17));
        ws.Plan.SetDue(pages[1], new DateTime(2026, 11, 16));
        ws.Plan.SetDue(pages[3], new DateTime(2026, 11, 24));
        ws.Plan.Deadline = new DateTime(2026, 11, 28);
        ws.Save();
        var page = CpadIO.Load(Story.Full(pages[1])); page.FilePath = Story.Full(pages[1]);
        App.SetDoc(page);
        var rnd = new Random(3);
        var plog = new Dictionary<DateTime, double>();
        for (int i = 0; i < 19; i++) { var day = new DateTime(2026, 11, 1).AddDays(i); if (rnd.NextDouble() < 0.75) WorkLog.Record(page, day.AddHours(20), rnd.Next(600, 14000)); }
        WorkIndex.Flush();
        foreach (var tab in new[] { "dash", "cal", "pages", "plan" })
        {
            var w = new ScheduleWindow(tab);
            try { Save(WfForm.Shot(w, 860, 760), dir, $"schedule_{tab}.png"); } finally { w.Close(); w.Dispose(); }
        }
        Ok(File.Exists(Path.Combine(dir, "schedule_cal.png")), "일정 창 그림 (대시보드·달력·페이지·단계)");
        using (var host = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-8000, 0), ClientSize = new Size(300, 1100), BackColor = UI.Theme.Panel })
        {
            var p = new SchedulePanel { Dock = DockStyle.Fill };
            host.Controls.Add(p); host.Show();
            for (int i = 0; i < 8; i++) { Application.DoEvents(); host.PerformLayout(); Thread.Sleep(10); }
            using var bmp = new Bitmap(300, 1100); host.DrawToBitmap(bmp, new Rectangle(0, 0, 300, 1100)); bmp.Save(Path.Combine(dir, "schedule_panel.png"));
            // 페이지 패널 (단계 표시가 작업 일정 단계로)
            host.Controls.Clear();
            var pp = new PagesPanel { Dock = DockStyle.Fill };
            host.Controls.Add(pp);
            for (int i = 0; i < 8; i++) { Application.DoEvents(); host.PerformLayout(); Thread.Sleep(10); }
            using var bmp2 = new Bitmap(300, 700); host.DrawToBitmap(bmp2, new Rectangle(0, 0, 300, 700)); bmp2.Save(Path.Combine(dir, "pages_panel.png"));
        }
        Ok(PagesPanel.Badge?.Invoke(Story0, pages[1]) is { } badge && badge.label == "펜선", "페이지 패널 카드: 작업 일정 단계(펜선) 표시");
        // 참고 자료 보드 창
        var board = RefStore.Load("시험보드");
        var rw = new RefBoardWindow(true) { ShowInTaskbar = false };
        try
        {
            rw.View.Board = board;
            rw.Location = new Point(-8000, 0); rw.Size = new Size(900, 640);
            rw.Show(); rw.Location = new Point(-8000, 0);
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(10); }
            rw.View.FitAll();
            rw.View.sel.Add(board.Items[0]);
            rw.View.Picked = Color.FromArgb(0, 0, 255);
            for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(10); }
            using var b = new Bitmap(rw.Width, rw.Height); rw.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); b.Save(Path.Combine(dir, "refboard_window.png"));
        }
        finally { rw.Close(); rw.Dispose(); }
        Ok(File.Exists(Path.Combine(dir, "refboard_window.png")), "참고 자료 보드 창 그림");
        // A/B 비교 창: 지금 그림에 저장 안 한 선을 더해서 (마지막 저장과 다르게)
        var vlist = VersionSources.For(page);
        var lay = page.Layers[0];
        var ink = new uint[160 * 14]; Array.Fill(ink, 0xFF000000);
        lay.Px.Write(new Rect(120, 200, 160, 14), ink); lay.Px.Write(new Rect(120, 320, 160, 14), ink);
        var paper = new uint[200 * 6]; Array.Fill(paper, 0xFF000000);
        lay.Px.Write(new Rect(100, 420, 200, 6), paper);
        foreach (var (m, t) in new[] { ("split", 0.45), ("onion", 0.5), ("diff", 0.5), ("side", 0.5) })
        {
            var aw = new AbCompareWindow();
            try
            {
                aw.SetSources(vlist, Math.Min(1, vlist.Count - 1), -1);
                aw.SetMode(m, t);
                Save(WfForm.Shot(aw, 1000, 700), dir, $"ab_{m}.png");
            }
            finally { aw.Close(); aw.Dispose(); }
        }
        Ok(File.Exists(Path.Combine(dir, "ab_split.png")), "A/B 비교 창 그림 (밀어서·어니언·달라진 곳·나란히)");
        // 피드백 창
        var fdoc = App.Doc;
        FeedbackView.Set(FeedbackView.Load(fdoc, MakeFeedback(fdoc)), true);
        FeedbackView.MergeNotes(fdoc, FeedbackView.Current);
        FeedbackView.SetDone(fdoc, (string)Feedback.Notes(fdoc)[0]["id"], true);
        var fw = new FeedbackWindow();
        try { Save(WfForm.Shot(fw, 420, 640), dir, "feedback_window.png"); } finally { fw.Close(); fw.Dispose(); }
        Ok(File.Exists(Path.Combine(dir, "feedback_window.png")), "피드백 창 그림");
    }
}
