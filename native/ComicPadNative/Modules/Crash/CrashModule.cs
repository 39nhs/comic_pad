// 오류 보고 모듈: 오류가 나면 긴급 저장 + 보고서(보내기 전 전체 보기, 사용자가 누를 때만 GitHub 작성 화면) + 시작 꺼짐 반복 막기(안전 모드).
//  메뉴: 도움말 › 최근 오류 보고서…   설정: 오류 보고 › "오류가 나면 보고서 만들기" (기본 켬, 키 crashReport)
//  시험 (창 없이): --selftest crash 결과폴더 → 결과폴더/result.txt, 끝 코드 = 실패 수
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Crash;

public sealed class CrashModule : IModule
{
    public void Init()
    {
        Registry.Menu("도움말/최근 오류 보고서…", CrashUi.Recent, order: 90);

        // 설정 창에 한 구역
        Shell2.Shell2Settings.Extra.Add(() => new Control[]
        {
            Opt.Title("오류 보고"),
            Opt.Check("오류가 나면 보고서 만들기", () => App.Get("crashReport", true), v => App.Set("crashReport", v)),
            Opt.Help("오류가 나면 먼저 작업을 data\\autosave 에 긴급 저장하고, 보고서를 data\\reports 에 만듭니다. 보고서에는 그림·글 내용과 파일 이름이 들어가지 않으며, 저절로 보내지 않습니다 (GitHub 에 올리기는 직접 보고 누를 때만)."),
            Opt.Row(Opt.Button("최근 오류 보고서…", CrashUi.Recent)),
        });

        // 최근 명령·도구·알림 기록 (메모리에만, 보고서를 만들 때 가려서 씀)
        MainForm.Ran += CrashLog.Command;
        MainForm.Failed = (ex, name) => CrashHandler.Handle(ex, name, false);
        App.ToolChanged += () => CrashLog.Add("도구", App.Tool?.Id);
        App.Toasted += m => CrashLog.Add("알림", m);

        bool done = false;
        void FirstIdle(object s, EventArgs e)
        {
            if (done || App.Main == null || !App.Main.IsHandleCreated || !App.Main.Visible || App.Doc == null) return;
            done = true;
            Application.Idle -= FirstIdle;
            if (!CrashGuard.Started || UI.Chrome.UiShot.Active || Shell2.GuiTest.On) return;
            App.Main.BeginInvoke((Action)CrashUi.AfterShown);
        }
        Application.Idle += FirstIdle;
    }
}
