// 메인 창: 위 메뉴(Registry.Menus), 왼쪽 도구 막대(Registry.Tools), 가운데 그림판, 오른쪽 패널(레이어·색·도구 옵션·Registry.Panels), 아래 상태 줄.
using ComicPadNative.Core;

namespace ComicPadNative.UI;

public sealed class MainForm : Form
{
    public readonly CanvasView Canvas = new() { Dock = DockStyle.Fill };
    readonly MenuStrip menu = new() { BackColor = Opt.Bg2, ForeColor = Opt.Fg, RenderMode = ToolStripRenderMode.System };
    readonly ToolStrip tools = new() { Dock = DockStyle.Left, LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow, BackColor = Opt.Bg, ForeColor = Opt.Fg, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, AutoSize = false, Width = 52 };
    readonly StatusStrip status = new() { BackColor = Opt.Bg2, ForeColor = Opt.Fg, SizingGrip = false };
    readonly ToolStripStatusLabel stInfo = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel stView = new();
    readonly ToolStripStatusLabel stPen = new();
    readonly Panel optionsHost = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Opt.Bg };
    readonly System.Windows.Forms.Timer toastTimer = new() { Interval = 4000 };

    static readonly string[] MenuOrder = { "파일", "편집", "레이어", "선택", "필터", "만화", "애니", "AI", "보기", "도움말" };

    public MainForm()
    {
        App.Main = this;
        Text = "ComicPad";
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "ComicPad.ico")); } catch { }
        BackColor = Opt.Bg; ForeColor = Opt.Fg;
        WindowState = FormWindowState.Maximized;
        ClientSize = new Size(1500, 950);
        KeyPreview = true;
        Font = new Font("Malgun Gothic", 9f);

        // 오른쪽: 위 = 레이어·색·추가 패널 탭, 아래 = 도구 옵션
        var right = new SplitContainer { Dock = DockStyle.Right, Orientation = Orientation.Horizontal, Width = 320, BackColor = Opt.Bg, SplitterWidth = 6 };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("레이어", new LayersPanel { Dock = DockStyle.Fill }));
        tabs.TabPages.Add(Page("색", new ColorPanel { Dock = DockStyle.Fill }));
        foreach (var p in Registry.Panels.Where(p => p.Dock == "right").OrderBy(p => p.Order)) { try { var c = p.Create(); c.Dock = DockStyle.Fill; tabs.TabPages.Add(Page(p.Title, c)); } catch (Exception ex) { App.Toast($"패널 {p.Title} 오류: {ex.Message}"); } }
        right.Panel1.Controls.Add(tabs);
        var optTitle = new Label { Text = "도구 옵션", Dock = DockStyle.Top, ForeColor = Opt.Accent, Height = 22, Padding = new Padding(6, 4, 0, 0) };
        right.Panel2.Controls.Add(optionsHost);
        right.Panel2.Controls.Add(optTitle);

        status.Items.AddRange(new ToolStripItem[] { stInfo, stPen, stView });
        Controls.Add(Canvas);
        Controls.Add(right);
        Controls.Add(tools);
        Controls.Add(menu);
        Controls.Add(status);
        MainMenuStrip = menu;
        Load += (_, _) => { right.SplitterDistance = (int)(right.Height * 0.55); };

        BuildMenus();
        BuildTools();
        App.ToolChanged += () => { SyncTools(); BuildOptions(); };
        App.LayersChanged += UpdateTitle;
        App.DocChanged += UpdateTitle;
        App.Toasted += Toast;
        App.History.Changed += () => { UpdateTitle(); };
        Canvas.ViewChanged += UpdateStatus;
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); UpdateStatus(); };
        FormClosing += OnClosingForm;
    }

    static TabPage Page(string title, Control c) { var p = new TabPage(title) { BackColor = Opt.Bg }; p.Controls.Add(c); return p; }

    // ---- 메뉴 ----------------------------------------------------------------------
    public void BuildMenus()
    {
        menu.Items.Clear();
        var tops = new Dictionary<string, ToolStripMenuItem>();
        ToolStripMenuItem Top(string name)
        {
            if (!tops.TryGetValue(name, out var t)) { t = new ToolStripMenuItem(name) { ForeColor = Opt.Fg }; tops[name] = t; }
            return t;
        }
        foreach (var m in Registry.Menus.OrderBy(m => m.Order))
        {
            var parts = m.Path.Split('/');
            var parent = Top(parts[0]);
            for (int i = 1; i < parts.Length - 1; i++)
            {
                var sub = parent.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(x => x.Text == parts[i]);
                if (sub == null) { sub = new ToolStripMenuItem(parts[i]); parent.DropDownItems.Add(sub); }
                parent = sub;
            }
            string leaf = parts[^1];
            if (leaf.StartsWith("-")) { parent.DropDownItems.Add(new ToolStripSeparator()); continue; }
            var item = new ToolStripMenuItem(leaf) { ShortcutKeys = IsValidShortcut(m.Shortcut) ? m.Shortcut : Keys.None };
            if (m.Shortcut != Keys.None && !IsValidShortcut(m.Shortcut)) item.ShortcutKeyDisplayString = new KeysConverter().ConvertToString(m.Shortcut);
            var def = m;
            item.Click += (_, _) => Run(def.Run, def.Path);
            parent.DropDownItems.Add(item);
            parent.DropDownOpening += (_, _) =>
            {
                item.Enabled = def.Enabled?.Invoke() ?? true;
                if (def.Checked != null) item.Checked = def.Checked();
            };
        }
        foreach (var name in MenuOrder.Concat(tops.Keys.Except(MenuOrder))) if (tops.TryGetValue(name, out var t)) menu.Items.Add(t);
    }
    static bool IsValidShortcut(Keys k) => k != Keys.None && (k & (Keys.Control | Keys.Alt)) != 0 || (k >= Keys.F1 && k <= Keys.F24) || k == Keys.Delete;

    public static void Run(Action a, string name)
    {
        try { a(); }
        catch (Exception ex) { App.Toast($"{name}: {ex.Message}"); File.AppendAllText(Path.Combine(Path.GetTempPath(), "comicpad-native-error.log"), DateTime.Now + " " + name + "\r\n" + ex + "\r\n\r\n"); }
    }

    // ---- 도구 막대 -------------------------------------------------------------------
    void BuildTools()
    {
        tools.Items.Clear();
        foreach (var t in Registry.Tools)
        {
            var b = new ToolStripButton(t.Icon) { ToolTipText = t.Name + (t.Key != Keys.None ? $" ({t.Key})" : ""), Tag = t.Id, AutoSize = false, Width = 48, Height = 40, Font = new Font("Segoe UI Emoji", 14f), ForeColor = Opt.Fg };
            var id = t.Id;
            b.Click += (_, _) => App.SetTool(id);
            tools.Items.Add(b);
        }
        SyncTools();
    }
    void SyncTools() { foreach (ToolStripItem i in tools.Items) if (i is ToolStripButton b) b.Checked = (string)b.Tag == App.Tool?.Id; }

    void BuildOptions()
    {
        optionsHost.SuspendLayout();
        foreach (Control c in optionsHost.Controls) c.Dispose();
        optionsHost.Controls.Clear();
        try { var c = App.Tool?.BuildOptions(); if (c != null) optionsHost.Controls.Add(c); } catch (Exception ex) { App.Toast("도구 옵션 오류: " + ex.Message); }
        optionsHost.ResumeLayout();
    }

    // ---- 키보드 ----------------------------------------------------------------------
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (ActiveControl is TextBoxBase || ActiveControl is NumericUpDown || ActiveControl is ComboBox) return base.ProcessCmdKey(ref msg, keyData);
        Keys k = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
        if (App.Tool?.HandleKey(keyData) == true) return true;
        if (mods == Keys.None)
        {
            // 도구 키: 같은 키가 여러 도구면 누를 때마다 차례로
            var list = Registry.Tools.Where(t => t.Key == k).ToList();
            if (list.Count > 0)
            {
                int i = list.FindIndex(t => t == App.Tool);
                App.SetTool(list[(i + 1) % list.Count].Id);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && !(ActiveControl is TextBoxBase)) { Canvas.SpaceDown = true; Canvas.Cursor = Cursors.Hand; e.Handled = true; e.SuppressKeyPress = true; }
        Canvas.CtrlDown = e.Control; Canvas.ShiftDown = e.Shift;
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space) { Canvas.SpaceDown = false; Canvas.Cursor = App.Tool?.Cursor ?? Cursors.Cross; }
        Canvas.CtrlDown = e.Control; Canvas.ShiftDown = e.Shift;
    }

    // ---- 상태 ----------------------------------------------------------------------
    public void UpdateTitle()
    {
        var d = App.Doc;
        Text = d == null ? "ComicPad" : $"{(d.Modified ? "● " : "")}{d.Name} — ComicPad ({d.W}×{d.H}, {d.Dpi}dpi)";
        UpdateStatus();
    }
    void UpdateStatus()
    {
        if (toastTimer.Enabled) return;
        var d = App.Doc;
        stInfo.Text = d == null ? "" : $"{App.Tool?.Name} · {d.Active?.Name}";
        stView.Text = $"{Canvas.V.Zoom * 100:0}%" + (Canvas.V.Angle != 0 ? $" · {Canvas.V.Angle:0}°" : "") + (Canvas.V.Flip ? " · 반전" : "");
        stPen.Text = Canvas.PenRate > 0 ? $"펜 {Canvas.PenRate}점/초 · 입력→화면 {Canvas.LatencyMs:0.0}ms" : "";
    }
    void Toast(string msg) { stInfo.Text = "ⓘ " + msg; toastTimer.Stop(); toastTimer.Start(); }

    void OnClosingForm(object s, FormClosingEventArgs e)
    {
        if (App.Doc?.Modified == true)
        {
            var r = MessageBox.Show(this, "저장하지 않은 변경이 있습니다. 저장할까요?", "ComicPad", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes && !FileCommands.Save(false)) { e.Cancel = true; return; }
        }
        App.SaveSettings();
    }
}
