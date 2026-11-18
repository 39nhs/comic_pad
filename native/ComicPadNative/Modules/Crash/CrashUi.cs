// 오류 알림 창 (웹 .modal 모양): 무슨 일이 있었나 (짧게) · "작업은 여기 저장했습니다: …" · [보고서 보기] [GitHub 에 보고하기] [닫기]
// 보고서 보기 창: 보고서 전체를 보여 주고 (고칠 수 있음) [복사] [GitHub 에 보고하기]. GitHub 은 이 창에서 내용을 본 뒤 누를 때만,
// 브라우저에 새 이슈 작성 화면(제목·본문 채움)을 엶 — 보내기는 GitHub 화면에서 사용자가 직접. 전체 보고서는 클립보드 + data/reports.
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;
using ComicPadNative.UI.Dialogs;

namespace ComicPadNative.Modules.Crash;

public static class CrashUi
{
    public sealed record Info(string What, string Command, SaveResult Save, string Report, string ReportPath, bool FromLastRun);

    /// <summary>창이 뜨기 전에 난 오류 (창이 뜨면 보여 줌)</summary>
    public static Info Pending;
    /// <summary>브라우저 열기 (시험에서는 바꿔 끼워 아무것도 열지 않음)</summary>
    public static Action<string> OpenUrl = url => Shell2.Shell2Ui.OpenUrl(url);
    /// <summary>시험용: 마지막으로 띄운 창의 제목·글·단추</summary>
    public static (string title, List<string> texts, List<string> buttons) LastShown;

    static bool showing;

    public static void Show(Info info)
    {
        if (info == null) return;
        if (showing) { App.Toast("오류가 또 났습니다: " + info.What); return; }
        showing = true;
        try
        {
            using var f = new ModalForm(info.FromLastRun ? "지난번에 오류로 닫혔습니다" : "오류가 났습니다", 520);
            var st = new WebStack();
            Label L(string t, Font font, Color c) => new Label { Text = t, AutoSize = true, MaximumSize = new Size(488, 0), Font = font, ForeColor = c, Margin = new Padding(0, 4, 0, 4), UseMnemonic = false };
            st.Controls.Add(L(info.FromLastRun ? "지난번에 ComicPad 가 예상하지 못한 오류로 닫혔습니다." : "ComicPad 에서 예상하지 못한 오류가 났습니다.", Web.F(14, true), Theme.Text));
            st.Controls.Add(L((info.Command != null ? $"\"{info.Command}\" 실행 중 — " : "") + info.What, Web.Small, Theme.Muted));
            if (info.Save?.Path != null)
            {
                st.Controls.Add(L("작업은 여기 저장했습니다: " + info.Save.Path, Web.F(13), Theme.Accent));
                var dir = Path.GetDirectoryName(info.Save.Path);
                st.Controls.Add(Opt.Row(Opt.Button("저장한 폴더 열기", () => Shell.OpenFolder(dir))));
            }
            else st.Controls.Add(L(info.Save?.Note ?? "저장할 작품이 없습니다.", Web.F(13), Theme.Text));
            st.Controls.Add(L(info.FromLastRun ? "보고서는 이 PC 에만 만들었고 아무 데도 보내지 않았습니다."
                : "보고서는 이 PC 에만 만들었고 아무 데도 보내지 않았습니다. 프로그램은 계속 쓸 수 있습니다 (이상하면 저장한 뒤 다시 켜 주세요).", Web.Small, Theme.Muted));
            f.SetContent(st);
            f.AddAction("보고서 보기", () => { Viewer(info.Report, info.ReportPath, false); return false; });
            f.AddAction("GitHub 에 보고하기", () => { Viewer(info.Report, info.ReportPath, true); return false; });
            f.AddAction("닫기", () => true, primary: true);
            Present(f);
        }
        finally { showing = false; }
    }

    /// <summary>창 띄우기 (시험 중에는 화면 밖에 띄우고 내용만 기록한 뒤 바로 닫음)</summary>
    static void Present(ModalForm f)
    {
        if (CrashHandler.Headless)
        {
            f.Load += (_, _) => { f.Location = new Point(-32000, -32000); f.Opacity = 0; };
            f.Shown += (_, _) =>
            {
                LastShown = (f.Text, All(f).OfType<Label>().Select(l => l.Text).ToList(), f.Actions.Row.Controls.Cast<Control>().Select(c => c.Text).ToList());
                f.BeginInvoke((Action)f.Close);
            };
            f.ShowDialog();
            return;
        }
        f.ShowDialog(App.Main);
    }
    static IEnumerable<Control> All(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (var k in All(c)) yield return k; } }

    /// <summary>보고서 전체 보기 (보내기 전에 꼭 이 창을 거침)</summary>
    public static void Viewer(string report, string path, bool forSend)
    {
        var body = new Panel { BackColor = Theme.Panel };
        var help = new Label
        {
            Dock = DockStyle.Top, Height = 56, ForeColor = forSend ? Theme.Text : Theme.Muted, Font = Web.Small, UseMnemonic = false,
            Text = forSend
                ? "보낼 내용을 확인하세요 (고쳐도 됩니다). 아래 \"GitHub 에 보고하기\" 를 누르면 브라우저에 GitHub 새 이슈 작성 화면이 이 내용으로 채워져 열립니다. 실제로 올리는 것은 GitHub 화면에서 직접 누를 때만입니다."
                : "이 PC 에서만 만든 보고서입니다 (아직 아무 데도 보내지 않았음). 그림·글 내용, 파일 이름은 넣지 않았고 사용자 폴더는 %USERPROFILE% 로 가렸습니다.",
        };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Theme.Panel2, ForeColor = Theme.Text, Font = new Font("Consolas", 9.5f), Text = (report ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n") };
        body.Controls.Add(text); body.Controls.Add(help);
        using var f = new ModalForm("오류 보고서", 780, 600) { Resizable = true };
        f.SetContent(body);
        f.AddAction("닫기", () => true);
        f.AddAction("복사", () => { Copy(text.Text); App.Toast("보고서를 복사했습니다"); return false; });
        f.AddAction("GitHub 에 보고하기", () => { SendToGitHub(text.Text.Replace("\r\n", "\n"), path); return true; }, primary: true);
        Present(f);
    }

    /// <summary>클립보드에 쓰기 (시험에서는 바꿔 끼워 사용자 클립보드를 건드리지 않음)</summary>
    public static Action<string> CopyText = s => Clipboard.SetText(s);
    static void Copy(string s) { try { CopyText(s); } catch { } }

    /// <summary>GitHub 새 이슈 작성 화면 열기 (제목·본문 채움, 길면 잘림) + 전체는 클립보드·data/reports 에</summary>
    public static string SendToGitHub(string report, string path)
    {
        Copy(report.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        CrashReport.SaveReport(report, path);
        string url = CrashReport.IssueUrl(report);
        OpenUrl(url);
        App.Toast("브라우저에 GitHub 이슈 작성 화면을 열었습니다 (전체 보고서는 클립보드에 있음)");
        return url;
    }

    /// <summary>도움말 › 최근 오류 보고서…</summary>
    public static void Recent()
    {
        var files = CrashReport.RecentReports();
        if (files.Count == 0) { Modals.Info("아직 오류 보고서가 없습니다.\n오류가 나면 data\\reports 에 보고서가 생깁니다 (설정 › 오류 보고).", "최근 오류 보고서"); return; }
        var body = new Panel { BackColor = Theme.Panel };
        var list = new ListBox { Dock = DockStyle.Top, Height = 120, BackColor = Theme.Panel2, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Web.Body, IntegralHeight = false };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Theme.Panel2, ForeColor = Theme.Text, Font = new Font("Consolas", 9.5f) };
        string Read(string p) { try { return File.ReadAllText(p); } catch (Exception e) { return "읽지 못했습니다: " + e.Message; } }
        foreach (var p in files)
        {
            string first = Read(p).Split('\n').FirstOrDefault()?.Trim() ?? "";
            list.Items.Add($"{File.GetLastWriteTime(p):yyyy-MM-dd HH:mm}  {first}");
        }
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedIndex >= 0) text.Text = Read(files[list.SelectedIndex]).Replace("\r\n", "\n").Replace("\n", "\r\n"); };
        var gap = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.Panel };
        body.Controls.Add(text); body.Controls.Add(gap); body.Controls.Add(list);
        list.SelectedIndex = 0;
        using var f = new ModalForm("최근 오류 보고서", 780, 620) { Resizable = true };
        f.SetContent(body);
        f.AddAction("보고서 폴더 열기", () => { Shell.OpenFolder(CrashReport.ReportsDir); return false; });
        f.AddAction("닫기", () => true);
        f.AddAction("GitHub 에 보고하기…", () =>
        {
            int i = list.SelectedIndex;
            if (i < 0) return false;
            Viewer(Read(files[i]), files[i], true);
            return false;
        }, primary: true);
        Present(f);
    }

    /// <summary>창이 뜬 뒤: 안전 모드 안내, 지난번 닫힌 오류, 시작 중 난 오류</summary>
    public static void AfterShown()
    {
        if (CrashGuard.SafeMode)
        {
            var (_, _, skipped) = CrashGuard.Modules();
            string msg = (CrashGuard.EarlyCrashes >= 2 ? $"최근 {CrashGuard.EarlyCrashes}번 연달아 창이 뜨기 전에 꺼져서 안전 모드로 시작했습니다.\n" : "안전 모드로 시작했습니다.\n")
                + "· GPU 가속 끔\n· 화면 방식은 기본값 (간단히 보기, 보통 크기 버튼)\n"
                + (skipped.Count > 0 ? "· 시작하다 문제가 난 기능 건너뜀: " + string.Join(", ", skipped) + "\n" : "")
                + "\n다음에 켤 때는 보통대로 시작하고, 바꾼 설정도 원래대로 돌아갑니다. 같은 문제가 되풀이되면 도움말 › 최근 오류 보고서 로 알려 주세요.";
            App.Toast("안전 모드로 시작했습니다");
            Modals.Info(msg, "안전 모드");
        }
        var (rep, save) = CrashGuard.TakeUnseen();
        if (rep != null && File.Exists(rep))
        {
            string text = File.ReadAllText(rep);
            string what = text.Split('\n').FirstOrDefault()?.Replace("[오류]", "").Trim() ?? "";
            var sr = save != null && File.Exists(save) ? new SaveResult(save, "작업은 여기 저장했습니다: " + save) : new SaveResult(null, "그때 열려 있던 작품은 저장하지 못했거나 바뀐 것이 없었습니다. data\\autosave 의 자동 저장본을 확인해 보세요.");
            Show(new Info(what, null, sr, text, rep, true));
        }
        if (Pending != null) { var p = Pending; Pending = null; Show(p); }
    }
}
