// Extras 모듈: 여러 페이지 작품·전자책·타임랩스·진행표·피드백/메모·자동 액션·버전 비교·펜 진단·명도 보기·작업 화면·세팅 프리셋·키 바꾸기·사진에서 퍼스 자.
// 메뉴·패널·도구를 Registry 에 등록하고, 메인 창이 뜬 뒤(첫 Idle) 상태 줄·키 필터·명령 기록·처음 세팅 창을 붙인다.
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public sealed class ExtrasModule : IModule
{
    public static event Action<string> ExtraChanged;          // 문서 Extra 값이 실행취소 등으로 바뀜 (키)
    public static event Action Saved;                         // 지금 문서를 저장함 (어디서 저장했든)
    public static void RaiseExtraChanged(string key) => ExtraChanged?.Invoke(key);

    /// <summary>저장 직후 처리: 작품 정보에 작업 시간, 페이지 미리보기</summary>
    public static void OnSaved(Document d)
    {
        if (d == null) return;
        try
        {
            Progress.OnSaved(d);
            string rel = Story.Rel(d.FilePath);
            if (Story.StoryOfRel(rel) != null) Story.WriteThumb(d, rel);
        }
        catch { }
        Saved?.Invoke();
    }

    static void M(string path, Action run, Keys key = Keys.None, Func<bool> enabled = null, Func<bool> check = null, int order = 500)
    {
        // Windows 메뉴 단축키로 쓸 수 없는 키(PageUp, Shift+글자 …)는 키 필터가 실행
        bool valid = key != Keys.None && ((key & (Keys.Control | Keys.Alt)) != 0 || (key >= Keys.F1 && key <= Keys.F24) || key == Keys.Delete);
        if (key != Keys.None && !valid) KeyMap.ModuleKeys[key] = path;
        Registry.Menu(path, run, key, enabled, check, order);
    }

    public void Init()
    {
        // 시험 (창 없이): COMICPAD_EXTRAS_TEST=결과 폴더 → 시험만 하고 끝냄
        string test = Environment.GetEnvironmentVariable("COMICPAD_EXTRAS_TEST");
        if (!string.IsNullOrEmpty(test)) { Environment.Exit(ExtrasTest.Run(test)); return; }

        bool HasDoc() => App.Doc != null;
        bool InStory() => Story.StoryOf(App.Doc) != null;

        // 만화: 여러 페이지 작품·진행표·퍼스 자
        M("만화/-extras", null, order: 800);
        M("만화/새 작품 (여러 페이지)…", Story.NewStoryDialog, order: 801);
        M("만화/이전 페이지", () => Story.StepPage(-1), Keys.PageUp, InStory, order: 802);
        M("만화/다음 페이지", () => Story.StepPage(1), Keys.PageDown, InStory, order: 803);
        M("만화/페이지 추가", () => Story.AddPage(false), Keys.None, InStory, order: 804);
        M("만화/현재 페이지 복제", () => Story.AddPage(true), Keys.None, InStory, order: 805);
        M("만화/작품 전체 내보내기…", Story.ExportStory, Keys.None, InStory, order: 806);
        M("만화/진행표 (단계·작업 시간·마감)…", Progress.Dialog, Keys.None, HasDoc, order: 807);
        M("만화/메모 핀 도구", () => App.SetTool("memo"), order: 808);
        M("만화/사진·배경에서 퍼스 자 만들기 (소실점 자동)", VpDetect.RulerFromPhoto, Keys.None, HasDoc, order: 809);

        // 파일: 전자책·피드백·버전 비교·추천 크기
        M("파일/-extras", null, order: 60);
        M("파일/전자책 내보내기 (EPUB / CBZ)…", () => Ebook.Dialog("epub"), Keys.None, HasDoc, order: 61);
        M("파일/버전 비교 (이전 저장본과)…", Compare.VersionsDialog, Keys.None, HasDoc, order: 62);
        M("파일/피드백 파일 보내기 (러프·메모만)…", Feedback.ExportDialog, Keys.None, HasDoc, order: 63);
        M("파일/피드백 파일 받기…", Feedback.ImportDialog, Keys.None, HasDoc, order: 64);
        M("파일/새 작품 (추천 크기)", Setup.NewFromStartDoc, Keys.None, () => App.Settings["startDoc"] is System.Text.Json.Nodes.JsonObject, order: 15);

        // 편집: 자동 액션
        M("편집/-extras", null, order: 800);
        M("편집/자동 액션 (기록·실행·일괄)…", Macros.Dialog, order: 801);
        M("편집/자동 액션 기록 시작/멈추기", () => { if (Macros.Recording) Macros.StopRecord(); else Macros.StartRecord(); }, Keys.None, null, () => Macros.Recording, order: 802);

        // 보기: 타임랩스·명도 보기·작업 화면
        M("보기/-extras1", null, order: 800);
        M("보기/타임랩스 기록 켜기/끄기", Timelapse.Toggle, Keys.None, HasDoc, () => Timelapse.On, order: 801);
        M("보기/타임랩스 동영상 만들기…", Timelapse.Export, Keys.None, HasDoc, order: 802);
        M("보기/타임랩스 장면 지우기…", Timelapse.Clear, Keys.None, HasDoc, order: 803);
        M("보기/-extras2", null, order: 810);
        M("보기/명도 확인 보기 (끔 → 흑백 → 5단계)", ViewAids.Cycle, Keys.Shift | Keys.Y, HasDoc, () => ViewAids.Mode != "off", order: 811);
        M("보기/-extras3", null, order: 820);
        M("보기/작업 화면 저장…", Workspaces.SaveDialog, order: 821);
        M("보기/작업 화면 바꾸기·관리…", Workspaces.ManageDialog, order: 822);
        for (int i = 0; i < 4; i++) { int k = i; M($"보기/작업 화면 {i + 1}번으로", () => Workspaces.Switch(k), Keys.Control | Keys.Alt | (Keys.D1 + i), order: 823 + i); }

        // 도움말: 펜 진단·세팅 프리셋·키 바꾸기
        M("도움말/-extras", null, order: 50);
        M("도움말/펜 필기감 진단 (이 기기·이 펜에 맞추기)…", PenDiag.Dialog, order: 51);
        M("도움말/초기 세팅 프리셋 고르기 (클립스튜디오식·포토샵식·웹툰·태블릿)…", () => Setup.ChooseDialog(), order: 52);
        M("도움말/내 세팅 내보내기 (.cpset)…", Setup.ExportSetDialog, order: 53);
        M("도움말/세팅 가져오기 (.cpset)…", Setup.ImportSetDialog, order: 54);
        M("도움말/세팅 되돌리기 (마지막 프리셋·가져오기 전으로)", () => Setup.UndoSetup(App.Settings), order: 55);
        M("도움말/단축키·도구 키 바꾸기…", KeyMap.Dialog, order: 56);

        Registry.Tool(new MemoTool());
        Registry.Panel("페이지", () => new PagesPanel(), order: 600);

        Progress.Init();
        Timelapse.Init();
        Application.AddMessageFilter(KeyMap.Instance);

        // 메인 창이 뜬 뒤 한 번: 상태 줄·명령 기록 감싸기·화면 배치·처음 세팅 창
        bool done = false;
        void FirstIdle(object s, EventArgs e)
        {
            if (done || App.Main == null || !App.Main.IsHandleCreated || !App.Main.Visible || App.Doc == null) return;
            done = true;
            Application.Idle -= FirstIdle;
            Macros.WrapCommands();
            (App.Main as MainForm)?.BuildMenus();
            Progress.AttachStatus();
            Macros.AttachIndicator();
            try { Workspaces.ApplyLayout(); } catch { }
            App.Main.BeginInvoke((Action)Setup.FirstRunCheck);
            StartSaveWatch();
        }
        Application.Idle += FirstIdle;
    }

    /// <summary>저장 감지: 지금 문서가 "바뀜" → "저장됨" 으로 바뀌면 (Ctrl+S 등 어디서 저장했든)</summary>
    static void StartSaveWatch()
    {
        Document lastDoc = null; bool lastMod = false; string lastPath = null;
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += (_, _) =>
        {
            var d = App.Doc;
            if (d != lastDoc) { lastDoc = d; lastMod = d?.Modified ?? false; lastPath = d?.FilePath; return; }
            if (d == null) return;
            bool saved = (lastMod && !d.Modified && d.FilePath != null) || (d.FilePath != null && d.FilePath != lastPath && !d.Modified);
            lastMod = d.Modified; lastPath = d.FilePath;
            if (saved) OnSaved(d);
        };
        t.Start();
    }
}
