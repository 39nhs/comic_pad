// Shell2 모듈: 웹 판에만 있던 화면·명령 기능을 네이티브로 (퀵바·원형 메뉴·아래 막대·간단히/모든 기능·화면 꾸미기·
// 패널 꺼내기·기록/내비/에셋 패널·점검·문제 보내기·기능 안내·업데이트·설정 창 보강·빠진 레이어/보기/파일 명령).
// 시험 (창 없이): COMICPAD_SHELL2_SELFTEST=결과폴더, COMICPAD_DATA=임시 폴더 로 --render 실행 → 결과폴더/result.txt
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public sealed class Shell2Module : IModule
{
    static bool HasDoc() => App.Doc != null;
    static bool HasMask() => App.Doc?.Active?.Mask != null;

    static void M(string path, Action run, Keys key = Keys.None, Func<bool> enabled = null, Func<bool> check = null, int order = 500)
        => Registry.Menu(path, run, key, enabled, check, order);

    public void Init()
    {
        string test = Environment.GetEnvironmentVariable("COMICPAD_SHELL2_SELFTEST");
        if (!string.IsNullOrEmpty(test)) { Environment.Exit(Shell2Test.Run(test)); return; }
        RegisterAll();
        // 메인 창 연결
        CanvasView.TempAction = (act, a) =>
        {
            if (act != "radial") return null;
            var c = Shell2Ui.Canvas; if (c == null) return null;
            return Radial.Open(c.PointToScreen(new Point((int)a.SX, (int)a.SY)));
        };
        CanvasView.GestureCommand = id => WebIds.Run(id);
        CursorLook.Install();

        bool done = false;
        void FirstIdle(object s, EventArgs e)
        {
            if (done || App.Main == null || !App.Main.IsHandleCreated || !App.Main.Visible || App.Doc == null) return;
            done = true;
            Application.Idle -= FirstIdle;
            // 설정 창을 이 모듈 것으로 (기본 Shell 모듈이 나중에 등록하므로 여기서 바꿈)
            Registry.Menu("파일/설정…", Shell2Settings.Dialog, Keys.Control | Keys.Oemcomma, order: 80);
            Shell2Ui.Main?.BuildMenus();
            UiMode.ApplyLarge();
            Bars.Attach();
            CursorLook.Apply();
            WatchSettings();
            Updater.CleanupOld(Updater.InstallRoot);
            if (GuiTest.On)
            {
                var gt = new System.Windows.Forms.Timer { Interval = 1500 };
                gt.Tick += (_, _) => { gt.Stop(); gt.Dispose(); GuiTest.Run(); };
                gt.Start();
                return;   // 시험 중에는 업데이트 확인(네트워크) 안 함
            }
            App.Main.BeginInvoke((Action)UpdateUi.WhatsNew);
            var t = new System.Windows.Forms.Timer { Interval = 8000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); UpdateUi.AutoCheck(); };
            t.Start();
        }
        Application.Idle += FirstIdle;
    }

    /// <summary>프리셋·작업 화면·세팅 가져오기로 화면 설정이 바뀌면 다시 그림</summary>
    static void WatchSettings()
    {
        string Sig() => string.Join("|", new[] { "ui", "largeUI", "quickbar", "bottombar" }.Select(k => App.Settings[k]?.ToJsonString() ?? ""));
        string last = Sig();
        App.SettingsChanged += () =>
        {
            string now = Sig();
            if (now == last) return;
            last = now;
            App.Main?.BeginInvoke((Action)(() => { UiMode.Refresh(); }));
        };
    }

    public static void RegisterAll()
    {
        WebIds.Install();
        QuickActions.Init();

        // ---- 파일 ----
        M("파일/PNG 빠른 내보내기", () => { var p = MoreCommands.ExportQuick(); if (p != null) App.Toast("내보냄: " + Path.GetRelativePath(App.DataDir, p)); }, Keys.None, HasDoc, order: 51);
        M("파일/글꼴 파일 추가 (.ttf∕.otf)…", MoreCommands.AddFonts, order: 65);
        M("파일/에셋/현재 레이어를 에셋으로 저장…", () => MoreCommands.AssetSave(false, "레이어 → 에셋"), Keys.None, HasDoc, order: 66);
        M("파일/에셋/선택 영역을 에셋으로 저장…", () => MoreCommands.AssetSave(false, "선택 영역 → 에셋"), Keys.None, () => App.Doc?.Sel.Active == true, order: 67);
        M("파일/에셋/보이는 그림 전체를 에셋으로 저장…", () => MoreCommands.AssetSave(true, "보이는 그림 → 에셋"), Keys.None, HasDoc, order: 68);
        M("파일/에셋/에셋 폴더 열기", () => Shell.OpenFolder(MoreCommands.AssetsDir), order: 69);
        M("파일/작업물 백업 (자동·지금·꺼내기)…", BackupUi.Dialog, order: 84);
        M("파일/작업물 폴더 열기", () => Shell.OpenFolder(App.DataDir), order: 86);
        M("파일/내보낸 이미지 폴더 열기", () => Shell.OpenFolder(MoreCommands.ExportsDir), order: 87);

        // ---- 편집 ----
        M("편집/전경∕배경색 바꾸기", MoreCommands.SwapColors, Keys.X, order: 45);

        // ---- 레이어 ----
        M("레이어/새 러프 레이어 (의견 색, 밑그림)", () => MoreCommands.NewRough(), Keys.None, HasDoc, order: 16);
        M("레이어/새 펜선 레이어", () => MoreCommands.NewLine(), Keys.None, HasDoc, order: 17);
        M("레이어/새 채색 레이어 (아래 레이어에 클리핑)", () => MoreCommands.NewColor(), Keys.None, HasDoc, order: 18);
        M("레이어/러프 전용 레이어로 지정∕해제", () => MoreCommands.SetRough(App.Active, App.Active?.Rough != true), Keys.None, HasDoc, () => App.Active?.Rough == true, order: 19);
        M("레이어/러프 색 바꾸기…", MoreCommands.RoughColorDialog, Keys.None, HasDoc, order: 19);
        M("레이어/폴더로 묶기", MoreCommands.GroupIntoFolder, Keys.None, HasDoc, order: 21);
        M("레이어/폴더를 한 레이어로 합치기", MoreCommands.MergeFolder, Keys.None, () => App.Active?.IsFolder == true, order: 22);
        M("레이어/보이는 레이어 결합", MoreCommands.FlattenVisible, Keys.None, HasDoc, order: 52);
        M("레이어/레이어 내용 지우기", MoreCommands.ClearLayer, Keys.None, HasDoc, order: 53);
        M("레이어/레이어 컬러 (파란 선으로 보기)", MoreCommands.ToggleTint, Keys.None, HasDoc, () => App.Active?.Tint != null && App.Active?.Rough != true, order: 87);
        M("레이어/마스크/마스크 편집 ↔ 그림 편집", MoreCommands.EditMask, Keys.None, HasMask, () => App.Active?.EditMask == true, order: 91);
        M("레이어/마스크/마스크 켜기∕끄기", MoreCommands.MaskOn, Keys.None, HasMask, () => App.Active?.MaskOn == true && HasMask(), order: 92);
        M("레이어/마스크/마스크 적용 (그림에 반영)", MoreCommands.ApplyMask, Keys.None, HasMask, order: 93);
        M("레이어/마스크/마스크 삭제", MoreCommands.DeleteMask, Keys.None, HasMask, order: 94);

        // ---- 도구: 복제 도장·픽셀 유동화·벡터 선 고르기 ----
        Registry.Tool(new CloneTool());
        Registry.Tool(new LiquifyTool());
        Registry.Tool(new VectorSelectTool());

        // ---- 선택: 펜 도구 (베지어 패스) ----
        Registry.Tool(new PathPenTool());
        M("선택/-path", null, order: 200);
        M("선택/펜 도구 (베지어 패스)", () => App.SetTool("pathpen"), order: 201);
        M("선택/패스/패스 → 선택 영역", () => Paths.ToSelection(Paths.CurrentOrLast()), Keys.None, HasDoc, order: 202);
        M("선택/패스/선택 영역 → 패스", () => { Paths.FromSelection(); Shell2Ui.Canvas?.RedrawAll(); }, Keys.None, () => App.Doc?.Sel.Active == true, order: 203);
        M("선택/패스/패스 따라 선 그리기 (지금 브러시)", () => Paths.Stroke(Paths.CurrentOrLast()), Keys.None, HasDoc, order: 204);
        M("선택/패스/패스 칠하기 (전경색)", () => Paths.Fill(Paths.CurrentOrLast()), Keys.None, HasDoc, order: 205);

        // ---- 보기 ----
        M("보기/가로 폭에 맞춤", MoreCommands.FitWidth, Keys.None, HasDoc, order: 11);
        M("보기/격자 보기", MoreCommands.ToggleGrid, Keys.None, HasDoc, () => App.Get("showGrid", false), order: 82);
        M("보기/-shell2a", null, order: 88);
        M("보기/패널 모두 숨기기∕보이기", UiMode.ToggleUI, Keys.Tab, null, () => UiMode.ZenOn, order: 89);
        M("보기/오른쪽 패널 보이기∕숨기기", UiMode.ToggleSide, Keys.None, null, () => Shell2Ui.Side?.Visible == true, order: 90);
        M("보기/패널 열기", UiMode.ShowSide, order: 91);
        M("보기/전체 화면", UiMode.ToggleFullscreen, Keys.F11, null, () => UiMode.FullOn, order: 92);
        M("보기/퀵바 보이기∕숨기기", Bars.ToggleQuick, Keys.None, null, () => Bars.QuickVisible, order: 96);
        M("보기/아래 막대 보이기∕숨기기", Bars.ToggleBottom, Keys.None, null, () => Bars.BottomVisible, order: 97);
        M("보기/아래 막대 버튼 고르기…", Bars.EditBottom, order: 98);
        M("보기/-shell2b", null, order: 830);
        M("보기/화면 꾸미기 (도구·메뉴·패널)…", UiMode.Customize, order: 831);
        M("보기/간단히 ↔ 모든 기능 보기", () => UiMode.SetMode(UiMode.IsSimple ? "full" : "simple"), Keys.None, null, () => !UiMode.IsSimple, order: 832);
        M("보기/원형 메뉴 열기", () => { var c = Shell2Ui.Canvas; if (c != null) Radial.Open(Cursor.Position); }, order: 833);
        M("보기/원형 메뉴 편집…", Radial.EditDialog, order: 834);
        M("보기/패널을 앱 밖 창으로…", Popout.Chooser, order: 835);
        M("보기/환경 설정을 앱 밖 창으로", () => Popout.Settings(), order: 836);
        M("보기/손가락으로 그리기 켜기∕끄기", MoreCommands.ToggleFingerDraw, Keys.None, null, () => App.Get("fingerDraw", false), order: 837);
        M("보기/참조 이미지 창 열기…", MoreCommands.Reference, order: 838);

        // ---- 도움말 ----
        M("도움말/기능 안내 (무엇을 어디서)…", HelpUi.FeatureGuide, Keys.F2, order: 15);
        M("도움말/단축키 ∕ 제스처 안내", HelpUi.KeysHelp, order: 25);
        M("도움말/-shell2", null, order: 88);
        M("도움말/문제 보내기 (오류 보고)…", HelpUi.Report, order: 89);
        M("도움말/업데이트 확인…", () => UpdateUi.Dialog(), order: 90);
        M("도움말/자동 점검 (주요 기능 시험)", HelpUi.SelfTestRun, order: 91);
        M("도움말/ComicPad 정보", HelpUi.About, order: 99);

        // ---- 패널·겹침 그림 ----
        Registry.Panel("기록", () => new HistoryPanel(), order: 700);
        Registry.Panel("내비", () => new NavigatorPanel(), order: 710);
        Registry.Panel("에셋", () => new AssetsPanel(), order: 720);
        Registry.Overlay(MoreCommands.DrawGrid);
    }
}
