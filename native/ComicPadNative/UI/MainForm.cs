// 메인 창 (웹 판 index.html 과 같은 배치): 위 막대(이름·메뉴·단추) / 왼쪽 도구 막대 · 가운데 그림판 · 오른쪽 패널(위·아래 탭) / 아래 큰 버튼 막대 / 상태 줄.
//  - 부품은 UI/Chrome 아래: TopBar · ToolBox · SidePanel · BottomBar · StatusBar · Toasts (웹 모양 그대로 직접 그림)
//  - 왼손잡이(leftHanded) = 도구 막대와 패널 자리 바꿈, 패널 숨기기(Tab) = 위·도구·패널·아래 막대·상태 줄 모두 숨김
//  - 메뉴 단축키는 메뉴 단추가 아니라 여기서 (같은 기능이 여러 메뉴에 있어도 한 번만)
using ComicPadNative.Core;
using ComicPadNative.UI.Chrome;

namespace ComicPadNative.UI;

public sealed class MainForm : Form
{
    public readonly CanvasView Canvas = new() { Dock = DockStyle.Fill };
    public readonly TopBar TopBar = new();
    public readonly ToolBox Tools = new();
    public readonly SidePanel Side;
    public readonly BottomBar BottomBar = new();
    public readonly StatusBar Status = new();
    readonly Panel middle = new() { Dock = DockStyle.Fill, BackColor = Theme.StageBg };
    readonly Toasts toasts;
    public MenuStrip MenuBar => TopBar.Menu;
    public Toasts ToastList => toasts;

    public MainForm()
    {
        App.Main = this;
        Text = "ComicPad";
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "ComicPad.ico")); } catch { }
        BackColor = Theme.Bg; ForeColor = Theme.Text;
        WindowState = FormWindowState.Maximized;
        ClientSize = new Size(1500, 950);
        MinimumSize = new Size(640, 420);
        KeyPreview = true;
        Font = new Font("Malgun Gothic", 9f);   // 패널 안 부품들의 기본 글꼴 (위·아래 막대는 웹 13px 를 따로 씀)
        Theme.Large = App.Get("largeUI", false);

        Side = new SidePanel();
        middle.Controls.Add(Canvas);
        middle.Controls.Add(Tools);
        middle.Controls.Add(Side);
        Controls.Add(middle);
        Controls.Add(BottomBar);
        Controls.Add(Status);
        Controls.Add(TopBar);
        MainMenuStrip = TopBar.Menu;
        toasts = new Toasts(this);

        BuildMenus();
        Tools.Rebuild();
        Load += (_, _) => { ApplyLayout(); Side.Rerender(); };
        App.LayersChanged += UpdateTitle;
        App.DocChanged += UpdateTitle;
        App.Toasted += Toast;
        App.History.Changed += UpdateTitle;
        Canvas.ViewChanged += () => Status.SyncView(Canvas.V);
        FormClosing += OnClosingForm;
        Resize += (_, _) => { int t = ToolBox.T; if (t != lastTool) { lastTool = t; Tools.Rebuild(); } };
        UiShot.Hook(this);
    }
    int lastTool = ToolBox.T;

    // ---- 화면 배치 ---------------------------------------------------------------------
    bool zen;
    /// <summary>패널 모두 숨기기 (웹 body.zen): 위 막대·도구 막대·패널·아래 막대·상태 줄</summary>
    public bool Zen { get => zen; set { zen = value; ApplyLayout(); } }

    public bool SideVisible => Side.Visible;
    public void SetSideVisible(bool v) { App.Set("sideVisible", v); Side.Visible = v && !zen; }

    /// <summary>설정대로 다시 배치 (왼손잡이·패널 보이기·큰 버튼·위아래 비율·탭)</summary>
    public void ApplyLayout()
    {
        SuspendLayout();
        Theme.Large = App.Get("largeUI", false);
        bool left = App.Get("leftHanded", false);
        Tools.Dock = left ? DockStyle.Right : DockStyle.Left;
        Side.LeftSide = left;
        TopBar.Visible = Tools.Visible = Status.Visible = !zen;
        Side.Visible = !zen && App.Get("sideVisible", true);
        BottomBar.Rebuild();
        Tools.Rebuild();
        Side.Rerender();
        Side.PerformLayout();
        TopBar.Relayout();
        ResumeLayout(true);
    }

    /// <summary>예전 "아래 패널(타임라인)" = 이제 오른쪽 아래 칸의 "애니" 탭 (웹 판과 같은 자리)</summary>
    public bool BottomDockVisible
    {
        get => Side.IsSelected("anim");
        set { App.Set("bottomDock", value); if (value) { SetSideVisible(true); Side.ShowTab("anim"); } else if (Side.IsSelected("anim")) Side.ShowTab("layers"); }
    }
    /// <summary>이 작품 동안만 애니 탭 보이기 (애니 작품을 열 때 자동으로)</summary>
    public void PeekBottomDock(bool v) { if (v) Side.ShowTab("anim"); }

    // ---- 메뉴 ----------------------------------------------------------------------
    public void BuildMenus() => TopBar.BuildMenus();

    public static void Run(Action a, string name)
    {
        try { a(); }
        catch (Exception ex) { App.Toast($"{name}: {ex.Message}"); File.AppendAllText(Path.Combine(Path.GetTempPath(), "comicpad-native-error.log"), DateTime.Now + " " + name + "\r\n" + ex + "\r\n\r\n"); }
    }

    // ---- 키보드 ----------------------------------------------------------------------
    static bool IsTextInput(Control c)
    {
        while (c is ContainerControl cc && cc.ActiveControl != null && cc.ActiveControl != c) c = cc.ActiveControl;
        return c is TextBoxBase || c is NumericUpDown || c is ComboBox || c is UpDownBase;
    }

    /// <summary>메뉴 단축키 실행 (있으면 true)</summary>
    static bool RunShortcut(Keys keyData)
    {
        var m = Registry.Menus.Find(x => x.Shortcut == keyData && x.Run != null);
        if (m == null) return false;
        if (m.Enabled == null || m.Enabled()) Run(m.Run, m.Path);
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (IsTextInput(ActiveControl))
        {
            // 글 입력 중: Ctrl·Alt 조합(복사·붙여넣기 등 글자 편집 키 빼고)과 F 키만 메뉴로
            Keys code = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
            bool edit = mods == Keys.Control && code is Keys.A or Keys.C or Keys.V or Keys.X or Keys.Z or Keys.Y or Keys.Left or Keys.Right or Keys.Back or Keys.Delete;
            if (!edit && ((mods & (Keys.Control | Keys.Alt)) != 0 || code is >= Keys.F1 and <= Keys.F24) && RunShortcut(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
        Keys k = keyData & Keys.KeyCode, mod = keyData & Keys.Modifiers;
        if (App.Tool?.HandleKey(keyData) == true) return true;
        if (mod == Keys.None)
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
        if (RunShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space && !IsTextInput(ActiveControl)) { Canvas.SpaceDown = true; Canvas.Cursor = Cursors.Hand; e.Handled = true; e.SuppressKeyPress = true; }
        Canvas.CtrlDown = e.Control; Canvas.ShiftDown = e.Shift;
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space) { Canvas.SpaceDown = false; Canvas.Cursor = CanvasView.CursorOf(App.Tool); }
        Canvas.CtrlDown = e.Control; Canvas.ShiftDown = e.Shift;
    }

    // ---- 상태 ----------------------------------------------------------------------
    public void UpdateTitle()
    {
        var d = App.Doc;
        Text = d == null ? "ComicPad" : $"{(d.Modified ? "● " : "")}{d.Name} — ComicPad ({d.W}×{d.H}, {d.Dpi}dpi)";
        Status.SyncDoc();
        TopBar.Invalidate();
    }
    void Toast(string msg) { if (InvokeRequired) { BeginInvoke((Action)(() => Toast(msg))); return; } toasts.Show(msg); }

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
