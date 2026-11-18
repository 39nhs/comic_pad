// 작업 흐름(Workflow) 모듈: 작업 일정(단계·마감·달력·대시보드) · 참고 자료 보드 · A/B 비교 · 피드백 보기.
// 메뉴·패널("일정")을 등록하고, 하루 단위 작업 시간 기록·페이지 패널 단계 표시·그림판으로 끌어 놓기·피드백 겹침 그림을 붙인다.
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Workflow;

public sealed class WorkflowModule : IModule
{
    public void Init()
    {
        bool HasDoc() => App.Doc != null;

        // 만화: 작업 일정
        Registry.Menu("만화/-workflow", null, order: 840);
        Registry.Menu("만화/작업 일정 (대시보드)…", () => ScheduleWindow.Open("dash"), Keys.None, HasDoc, order: 841);
        Registry.Menu("만화/작업 일정: 달력…", () => ScheduleWindow.Open("cal"), Keys.None, HasDoc, order: 842);
        Registry.Menu("만화/작업 일정: 페이지별 단계·마감…", () => ScheduleWindow.Open("pages"), Keys.None, HasDoc, order: 843);
        Registry.Menu("만화/지금 페이지 다음 단계로", ScheduleUi.AdvanceCurrent, Keys.None, HasDoc, order: 844);
        Registry.Menu("만화/작업 단계 바꾸기 (콘티→…→완료)…", () => { var ws = WorkSchedule.Current; if (ws != null) ScheduleUi.EditStages(ws); }, Keys.None, HasDoc, order: 845);
        Registry.Menu("만화/작품 마감일…", () => { var ws = WorkSchedule.Current; if (ws != null) ScheduleUi.EditDeadline(ws); }, Keys.None, HasDoc, order: 846);
        Registry.Menu("만화/회차(에피소드) 마감…", () => { var ws = WorkSchedule.Current; if (ws?.IsStory == true) ScheduleUi.EditEpisodes(ws); else App.Toast("여러 페이지 작품에서 쓸 수 있습니다"); }, Keys.None, () => WorkSchedule.Current?.IsStory == true, order: 847);

        // 보기: 참고 자료 보드 · 피드백 보기
        Registry.Menu("보기/-workflow", null, order: 860);
        Registry.Menu("보기/참고 자료 보드 (이 작품)…", () => RefBoardWindow.Open(false), order: 861);
        Registry.Menu("보기/참고 자료 보드 (공용)…", () => RefBoardWindow.Open(true), order: 862);
        Registry.Menu("보기/참고 자료 보드: 화면 캡처해서 넣기", () => { var w = RefBoardWindow.Open(); w.View.CaptureScreen(w); }, order: 863);
        Registry.Menu("보기/참고 자료 보드: 안 쓰는 그림 정리", () =>
        {
            var open = RefBoardWindow.Instance?.View.Board;
            open?.Let(b => { try { RefStore.Save(b); } catch { } });
            int n = RefStore.Cleanup(open == null ? null : new[] { open });
            App.Toast($"어느 보드도 쓰지 않는 그림 {n}개를 지웠습니다");
        }, order: 864);
        Registry.Menu("보기/피드백 보기 (겹쳐 보기·메모 해결)…", () => FeedbackWindow.Open(), Keys.None, HasDoc, order: 865);
        Registry.Menu("보기/피드백 겹쳐 보기 켜기/끄기", FeedbackView.Toggle, Keys.None, HasDoc, () => FeedbackView.On, order: 866);

        // 파일: A/B 비교
        Registry.Menu("파일/A∕B 비교 (예전 판·백업·자동 저장과)…", () => AbCompareWindow.Open(), Keys.None, HasDoc, order: 65);
        Registry.Menu("파일/피드백 파일 겹쳐 보기 (문서는 그대로)…", FeedbackView.OpenFile, Keys.None, HasDoc, order: 66);

        Registry.Panel("일정", () => new SchedulePanel(), order: 610);
        Registry.Overlay(FeedbackView.Draw);

        // 페이지 패널: 카드 단계 표시는 작업 일정 단계로, 오른쪽 클릭 = 단계 메뉴
        PagesPanel.Badge = (story, page) =>
        {
            var ws = BadgeCache(story);
            var st = ws?.Plan.StageById(ws.Plan.StageOf(page));
            return st == null ? null : (st.Label, Wf.C(st.Color));
        };
        PagesPanel.CardMenu = (story, page, ctl, pt) => ScheduleUi.StageMenu(WorkSchedule.ForStory(story), page, ctl, pt);
        WorkSchedule.Changed += () => cache = null;
        Extras.Story.Changed += () => cache = null;

        WorkLog.Init();

        // 메인 창이 뜬 뒤: 그림판이 참고 자료 보드에서 끌어 온 그림을 받게
        bool done = false;
        void FirstIdle(object s, EventArgs e)
        {
            if (done || App.Main == null || !App.Main.IsHandleCreated || !App.Main.Visible) return;
            var cv = U.Canvas; if (cv == null) return;
            done = true;
            Application.Idle -= FirstIdle;
            AttachDrop(cv);
        }
        Application.Idle += FirstIdle;
    }

    static (string story, WorkSchedule ws, long at)? cache;
    /// <summary>페이지 패널이 카드마다 부르니 같은 작품은 잠깐 기억</summary>
    static WorkSchedule BadgeCache(string story)
    {
        long now = Environment.TickCount64;
        if (cache is { } c && c.story == story && now - c.at < 1500) return c.ws;
        var ws = WorkSchedule.ForStory(story);
        cache = (story, ws, now);
        return ws;
    }

    /// <summary>참고 자료 보드 → 그림판 끌어 놓기 = 놓은 자리에 새 레이어</summary>
    public static void AttachDrop(Control cv)
    {
        void Hook(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(RefCanvas.DragFormat) == true) e.Effect = DragDropEffects.Copy; };
            c.DragOver += (_, e) => { if (e.Data?.GetDataPresent(RefCanvas.DragFormat) == true) e.Effect = DragDropEffects.Copy; };
            c.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(RefCanvas.DragFormat) is not string key) return;
                var parts = key.Split('|');
                if (parts.Length != 2 || App.Doc == null) return;
                var board = RefBoardWindow.Instance?.View.Board is { } open && open.Key == parts[0] ? open : RefStore.Load(parts[0]);
                var it = board.Find(parts[1]);
                if (it == null) return;
                var view = U.Canvas;
                var p = view.PointToClient(new Point(e.X, e.Y));
                var (dx, dy) = view.V.ToDoc(p.X, p.Y);
                var l = RefOps.ToLayer(App.Doc, it, dx, dy);
                if (l != null) { App.Toast($"새 레이어: {l.Name}"); App.Main?.Activate(); }
            };
            foreach (Control k in c.Controls) Hook(k);
        }
        Hook(cv);
    }
}

static class LetExt { public static void Let<T>(this T v, Action<T> a) where T : class { if (v != null) a(v); } }
