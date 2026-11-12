// 창을 띄운 채로 하는 시험 (COMICPAD_SHELL2_GUITEST=결과폴더). COMICPAD_SETTINGS·COMICPAD_DATA 를 임시로 정해서 실행해야 함.
// 메인 창이 뜬 뒤 화면 부품(퀵바·아래 막대·원형 메뉴·패널·간단히 모드·꺼낸 창·설정 내용)을 만들고 닫아 보며 예외가 없는지, 결과를 쓰고 끝냄.
using System.Text;
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
            T("간단히 모드 메뉴 거르기", () =>
            {
                UiMode.SetMode("simple");
                foreach (var top in ms.Items.OfType<ToolStripMenuItem>().Where(t => t.Visible)) { top.ShowDropDown(); Application.DoEvents(); top.HideDropDown(); }
            });
            var file = ms.Items.OfType<ToolStripMenuItem>().First(t => t.Text == "파일");
            file.ShowDropDown(); Application.DoEvents();
            Ok(file.DropDownItems.OfType<ToolStripItem>().Any(i => i.Text.StartsWith("모든 항목 보기")) && !file.DropDownItems.OfType<ToolStripItem>().First(i => i.Text == "PNG 빠른 내보내기").Visible, "간단히: 파일 메뉴에 \"모든 항목 보기\", 덜 쓰는 항목 숨김");
            file.HideDropDown();
            Ok(!ms.Items.OfType<ToolStripMenuItem>().First(t => t.Text == "애니").Visible, "간단히: 애니 메뉴 숨김");
            var tb = Shell2Ui.ToolBar;
            Ok(tb.Items.OfType<ToolStripItem>().Any(i => i.Name == "cp-more") && tb.Items.OfType<ToolStripButton>().Count(b => b.Tag is string && b.Visible) < Registry.Tools.Count, "간단히: 도구 막대 \"…\"");
            T("모든 기능 보기", () => UiMode.SetMode("full"));
            Ok(ms.Items.OfType<ToolStripMenuItem>().First(t => t.Text == "애니").Visible, "모든 기능: 애니 메뉴 보임");
            T("큰 버튼", () => { App.Set("largeUI", true); UiMode.ApplyLarge(); App.Set("largeUI", false); UiMode.ApplyLarge(); });
            T("퀵바 켜기·끄기", () => { Bars.ToggleQuick(); Application.DoEvents(); Bars.ToggleQuick(); });
            T("아래 막대", () => { Bars.RenderBottom(); Bars.Sync(); });
            T("원형 메뉴 열고 닫기", () => { var f = Radial.Open(new Point(400, 400)); Application.DoEvents(); f.Move(new PointerArgs { SX = 10, SY = 10 }); Radial.Close(); });
            T("패널 숨기기·전체 화면", () => { UiMode.ToggleUI(); UiMode.ToggleUI(); UiMode.ToggleFullscreen(); Application.DoEvents(); UiMode.ToggleFullscreen(); });
            T("패널 탭 숨기기·되돌리기", () => { UiMode.Ui()["hiddenPanels"] = new System.Text.Json.Nodes.JsonArray("history"); UiMode.ApplyPanels(); UiMode.Ui().Remove("hiddenPanels"); UiMode.ApplyPanels(); });
            Ok(Shell2Ui.SideTabs.TabPages.Cast<TabPage>().Any(p => p.Text == "기록") && Shell2Ui.SideTabs.TabPages.Cast<TabPage>().Any(p => p.Text == "내비"), "기록·내비·에셋 패널 탭");
            T("패널 앱 밖으로 꺼냈다 되돌리기", () =>
            {
                Popout.Panel("내비", false); Application.DoEvents();
                var w = Application.OpenForms.OfType<Form>().FirstOrDefault(x => x.Text.StartsWith("내비")); w?.Close(); Application.DoEvents();
            });
            Ok(Shell2Ui.SideTabs.TabPages.Cast<TabPage>().Any(p => p.Text == "내비"), "꺼낸 패널이 탭으로 돌아옴");
            T("설정 창 내용 만들기", () => { using var f = new Form(); var b = Shell2Settings.Body(); f.Controls.Add(b); f.Show(); Application.DoEvents(); f.Close(); });
            T("기록·내비 패널 그리기", () =>
            {
                QuickActions.ShowTab("기록"); Application.DoEvents(); WebIds.Run("layer.new"); Application.DoEvents();
                QuickActions.ShowTab("내비"); Application.DoEvents(); QuickActions.ShowTab("에셋"); Application.DoEvents(); QuickActions.ShowTab("레이어");
            });
            T("격자·가로 폭 맞춤", () => { MoreCommands.ToggleGrid(); MoreCommands.FitWidth(); MoreCommands.ToggleGrid(); });
            T("펜 도구 옵션", () => { App.SetTool("pathpen"); Application.DoEvents(); App.SetTool("brush"); });
            Ok(Shell2Ui.Canvas.Cursor != Cursors.Cross, "브러시 커서 = 펜촉");
            T("화면 찍기", () =>
            {
                UiMode.SetMode("simple"); Bars.ToggleQuick(); App.Main.Activate();
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(20); }
                // 화면을 찍지 않고 창 부품만 그려서 (다른 창이 위에 있어도 같은 결과)
                using var bmp = new Bitmap(App.Main.Width, App.Main.Height);
                App.Main.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(Path.Combine(Dir, "window.png"));
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
