// 창을 띄운 채로 하는 시험 (COMICPAD_SHELL2_GUITEST=결과폴더). COMICPAD_SETTINGS·COMICPAD_DATA 를 임시로 정해서 실행해야 함.
// 메인 창이 뜬 뒤 화면 부품(위 메뉴·도구 막대·퀵바·아래 막대·원형 메뉴·패널 탭·간단히 모드·왼손잡이·꺼낸 창·설정 내용)을 만들고 닫아 보며 예외가 없는지, 결과를 쓰고 끝냄.
using System.Text;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class GuiTest
{
    public static string Dir => Environment.GetEnvironmentVariable("COMICPAD_SHELL2_GUITEST");
    public static bool On => !string.IsNullOrEmpty(Dir);

    public static void Run()
    {
        Directory.CreateDirectory(Dir);
        var log = new StringBuilder(); int fail = 0;
        void T(string name, Action a)
        {
            try { a(); Application.DoEvents(); log.AppendLine("통과  " + name); }
            catch (Exception e) { fail++; log.AppendLine($"실패  {name}: {e.GetType().Name} {e.Message}\n{e.StackTrace}"); }
        }
        void Ok(bool c, string name) { if (c) log.AppendLine("통과  " + name); else { fail++; log.AppendLine("실패  " + name); } }
        bool safe = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMICPAD_SETTINGS")) == false && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMICPAD_DATA")) == false;
        Ok(safe, "임시 설정·작업물 폴더로 실행");
        if (safe)
        {
            var ms = Shell2Ui.MenuBar;
            var main = Shell2Ui.Main;
            T("간단히 모드 메뉴 거르기", () =>
            {
                UiMode.SetMode("simple");
                foreach (var top in ms.Items.OfType<ToolStripMenuItem>().Where(t => t.Visible)) { top.ShowDropDown(); Application.DoEvents(); top.HideDropDown(); }
            });
            Ok(ms.Items.OfType<ToolStripMenuItem>().Select(t => t.Text).SequenceEqual(UI.Chrome.WebMenus.Tree().Select(t => t.Label)), "위 메뉴: 웹 판과 같은 메뉴·순서");
            Ok(string.Join(",", ms.Items.OfType<ToolStripMenuItem>().Where(t => t.Visible).Select(t => t.Text)) == "파일,편집,레이어,선택,만화,필터,보기,AI,도움말,보정", "간단히: 보이는 메뉴 (웹 판과 같음)");
            var file = ms.Items.OfType<ToolStripMenuItem>().First(t => t.Text == "파일");
            file.ShowDropDown(); Application.DoEvents();
            var fileItems = file.DropDownItems.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();
            Ok(fileItems.Any(t => t.StartsWith("모든 항목 보기")) && !fileItems.Contains("PNG 빠른 내보내기") && fileItems.FirstOrDefault() == "새 캔버스…", "간단히: 파일 메뉴 (웹 이름) + \"모든 항목 보기\", 덜 쓰는 항목 숨김");
            Ok(file.DropDownItems.OfType<ToolStripMenuItem>().First(i => i.Text == "저장").Tag is UI.Chrome.MenuTag { Kbd: "Ctrl+S" }, "메뉴 단축키 칩 (Ctrl+S)");
            file.HideDropDown();
            T("모든 항목 보기", () => { main.TopBar.Open("file", true); Application.DoEvents(); });
            Ok(file.DropDownItems.OfType<ToolStripMenuItem>().Any(i => i.Text == "PNG 빠른 내보내기") && !file.DropDownItems.OfType<ToolStripMenuItem>().Any(i => i.Text.StartsWith("모든 항목 보기")), "모든 항목 보기: 숨긴 항목까지");
            file.HideDropDown();
            Ok(!ms.Items.OfType<ToolStripMenuItem>().First(t => t.Text == "애니").Visible, "간단히: 애니 메뉴 숨김");
            var tb = Shell2Ui.ToolBar;
            Ok(tb.More.Count > 0 && tb.Shown.Count() < Registry.Tools.Count, "간단히: 도구 막대 \"…\"");
            T("도구 더 보기 팝업", () => { tb.OpenMore(); Application.DoEvents(); tb.LastPopup?.Close(); });
            T("모든 기능 보기", () => UiMode.SetMode("full"));
            Ok(ms.Items.OfType<ToolStripMenuItem>().First(t => t.Text == "애니").Visible && tb.More.Count == 0, "모든 기능: 애니 메뉴·모든 도구 보임");
            T("큰 버튼", () => { App.Set("largeUI", true); UiMode.ApplyLarge(); Ok(main.Side.Width == 330 && main.TopBar.Height == 44, "큰 버튼: 패널 폭 330"); App.Set("largeUI", false); UiMode.ApplyLarge(); });
            Ok(main.Side.Width == 300 && main.BottomBar.Height == 62 && main.Status.Height == 28 && main.TopBar.Height == 44, "화면 크기: 위 44 · 패널 300 · 아래 막대 62 · 상태 줄 28 (웹 판과 같음)");
            T("왼손잡이 배치", () =>
            {
                App.Set("leftHanded", true); Workspaces.ApplyLayout(); Application.DoEvents();
                Ok(main.Side.Dock == DockStyle.Left && main.Tools.Dock == DockStyle.Right, "왼손잡이: 패널 왼쪽 · 도구 막대 오른쪽");
                App.Set("leftHanded", false); Workspaces.ApplyLayout();
            });
            T("퀵바 켜기·끄기", () => { Bars.ToggleQuick(); Application.DoEvents(); Bars.ToggleQuick(); });
            Ok(!Bars.QuickVisible, "퀵바: 기본은 숨김 (웹 판과 같음)");
            T("아래 막대", () => { Bars.RenderBottom(); Bars.Sync(); });
            T("원형 메뉴 열고 닫기", () => { var f = Radial.Open(new Point(400, 400)); Application.DoEvents(); f.Move(new PointerArgs { SX = 10, SY = 10 }); Radial.Close(); });
            T("패널 숨기기·전체 화면", () =>
            {
                UiMode.ToggleUI(); Application.DoEvents();
                Ok(!main.TopBar.Visible && !main.Side.Visible && !main.Tools.Visible && !main.BottomBar.Visible && !main.Status.Visible, "패널 모두 숨기기 (Tab): 위·도구·패널·아래 막대·상태 줄");
                UiMode.ToggleUI(); UiMode.ToggleFullscreen(); Application.DoEvents(); UiMode.ToggleFullscreen();
            });
            Ok(main.TopBar.Visible && main.Side.Visible, "패널 다시 보이기");
            T("패널 탭 숨기기·되돌리기", () => { UiMode.Ui()["hiddenPanels"] = new System.Text.Json.Nodes.JsonArray("history"); UiMode.ApplyPanels(); UiMode.Ui().Remove("hiddenPanels"); UiMode.ApplyPanels(); });
            Ok(main.Side.Selected("top") == "tool" && main.Side.Selected("bottom") == "layers", "패널 기본 탭: 위 = 도구, 아래 = 레이어");
            Ok(string.Join(",", main.Side.Defs.Select(d => d.Dock + ":" + d.Id)) == "top:tool,top:color,top:nav,top:pages,bottom:layers,bottom:assets,bottom:history,bottom:anim", "패널 탭 배치 (웹 판과 같음)");
            T("패널 앱 밖으로 꺼냈다 되돌리기", () =>
            {
                Popout.Panel("내비", false); Application.DoEvents();
                Ok(main.Side.Find("nav").Popped, "꺼낸 패널은 탭에서 빠짐");
                var w = Application.OpenForms.OfType<Form>().FirstOrDefault(x => x.Text.StartsWith("내비")); w?.Close(); Application.DoEvents();
            });
            Ok(!main.Side.Find("nav").Popped, "꺼낸 패널이 탭으로 돌아옴");
            T("설정 창 내용 만들기", () => { using var f = new Form(); var b = Shell2Settings.Body(); f.Controls.Add(b); f.Show(); Application.DoEvents(); f.Close(); });
            T("기록·내비 패널 그리기", () =>
            {
                QuickActions.ShowTab("기록"); Application.DoEvents(); WebIds.Run("layer.new"); Application.DoEvents();
                QuickActions.ShowTab("내비"); Application.DoEvents(); QuickActions.ShowTab("에셋"); Application.DoEvents(); QuickActions.ShowTab("애니"); Application.DoEvents();
                QuickActions.ShowTab("페이지"); Application.DoEvents(); QuickActions.ShowTab("색"); Application.DoEvents(); QuickActions.ShowTab("도구"); QuickActions.ShowTab("레이어");
            });
            T("격자·가로 폭 맞춤", () => { MoreCommands.ToggleGrid(); MoreCommands.FitWidth(); MoreCommands.ToggleGrid(); });
            T("펜 도구 옵션", () => { App.SetTool("pathpen"); Application.DoEvents(); App.SetTool("brush"); });
            Ok(Shell2Ui.Canvas.Cursor != Cursors.Cross, "브러시 커서 = 펜촉");
            T("화면 찍기", () =>
            {
                UiMode.SetMode("simple"); Bars.ToggleQuick(); App.Main.Activate();
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(20); }
                // 화면을 찍지 않고 창 부품만 그려서 (다른 창이 위에 있어도 같은 결과)
                UI.Chrome.UiShot.Save(App.Main, Path.Combine(Dir, "window.png"));
                Bars.ToggleQuick();
            });
        }
        log.AppendLine(fail == 0 ? "모두 통과" : $"실패 {fail}개");
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "gui-result.txt"), log.ToString());
        if (App.Doc != null) App.Doc.Modified = false;
        Environment.ExitCode = fail == 0 ? 0 : 1;
        App.Main.Close();
    }
}
