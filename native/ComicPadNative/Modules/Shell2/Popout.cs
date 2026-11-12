// 패널·설정 창을 앱 밖 별도 창으로 (웹 ui/popout.js): 두 번째 모니터, 또는 "항상 위" 작은 창.
// 오른쪽 패널 탭의 내용을 그대로 옮기는 방식이라 꺼낸 창에서 바꾸면 바로 반영되고, 창을 닫으면 탭으로 돌아온다.
// 꺼낸 창에 손이 가 있어도 단축키는 메인 창으로 넘김.
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class Popout
{
    static readonly Dictionary<string, Form> popped = new();
    static Form settingsWin;

    static Form MakeWindow(string title, int w, int h, bool onTop)
    {
        var f = new Form
        {
            Text = $"{title} — ComicPad", Width = w, Height = h, StartPosition = FormStartPosition.Manual, BackColor = Opt.Bg, ForeColor = Opt.Fg,
            FormBorderStyle = FormBorderStyle.SizableToolWindow, TopMost = onTop, ShowInTaskbar = !onTop, KeyPreview = true, Font = new Font("Malgun Gothic", 9f),
        };
        if (App.Main != null) { var r = App.Main.Bounds; f.Location = new Point(Math.Max(0, r.Right - w - 40), r.Top + 80); }
        // 단축키는 메인 창으로 (글 입력 칸이 아닐 때)
        f.KeyDown += (_, e) =>
        {
            if (f.ActiveControl is TextBoxBase or NumericUpDown or ComboBox) return;
            if (KeyMap.Handle(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; return; }
            var m = Registry.Menus.Find(x => x.Shortcut == e.KeyData && x.Run != null);
            if (m != null && (m.Enabled == null || m.Enabled())) { MainForm.Run(m.Run, m.Path); e.Handled = true; e.SuppressKeyPress = true; }
        };
        return f;
    }

    public static void Panel(string title, bool onTop)
    {
        if (popped.TryGetValue(title, out var ex) && !ex.IsDisposed) { ex.Activate(); return; }
        var tabs = Shell2Ui.SideTabs;
        var page = tabs?.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Text == title);
        if (page == null || page.Controls.Count == 0) { App.Toast("패널을 찾지 못했습니다"); return; }
        var content = page.Controls[0];
        int index = tabs.TabPages.IndexOf(page);
        var f = MakeWindow(title, 360, 600, onTop);
        page.Controls.Remove(content);
        tabs.TabPages.Remove(page);
        var back = new Button { Text = "앱으로 되돌리기", Dock = DockStyle.Top, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
        back.Click += (_, _) => f.Close();
        content.Dock = DockStyle.Fill;
        f.Controls.Add(content); f.Controls.Add(back);
        f.FormClosed += (_, _) =>
        {
            popped.Remove(title);
            if (content.IsDisposed || tabs.IsDisposed) return;
            f.Controls.Remove(content);
            page.Controls.Add(content);
            tabs.TabPages.Insert(Math.Min(index, tabs.TabPages.Count), page);
            tabs.SelectedTab = page;
        };
        popped[title] = f;
        f.Show(App.Main);
    }

    public static void Settings() => Settings(App.Get("popoutOnTop", false));
    public static void Settings(bool onTop)
    {
        if (settingsWin != null && !settingsWin.IsDisposed) { settingsWin.Activate(); return; }
        var f = MakeWindow("환경 설정", 520, 760, onTop);
        var body = Shell2Settings.Body();
        body.Dock = DockStyle.Fill;
        f.Controls.Add(body);
        f.FormClosed += (_, _) => { settingsWin = null; App.SaveSettings(); };
        settingsWin = f;
        f.Show(App.Main);
    }

    public static void Chooser()
    {
        var titles = Shell2Ui.SideTabs?.TabPages.Cast<TabPage>().Select(p => p.Text).ToList() ?? new();
        var opts = new List<(string, string)> { ("__settings", "환경 설정") };
        opts.AddRange(titles.Select(t => (t, t + " 패널")));
        var v = Dlg.Form("앱 밖 창으로 꺼내기", new[]
        {
            Field.Select("what", "꺼낼 것", titles.Contains("색") ? "색" : opts[0].Item1, opts.ToArray()),
            Field.Check("top", "항상 위에 작은 창으로 (꺼내면 화면 위에 떠 있음)", App.Get("popoutOnTop", false)),
            Field.Info("보통 창은 다른 모니터로 끌어다 놓기 좋고, 여러 개를 꺼낼 수 있습니다. 창을 닫으면 패널은 오른쪽으로 돌아옵니다."),
        }, "꺼내기");
        if (v == null) return;
        bool top = (bool?)v["top"] == true;
        App.Set("popoutOnTop", top);
        if ((string)v["what"] == "__settings") Settings(top); else Panel((string)v["what"], top);
    }
}
