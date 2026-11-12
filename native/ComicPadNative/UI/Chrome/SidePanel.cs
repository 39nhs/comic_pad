// 오른쪽 패널 (웹 #sidepanel, 폭 300): 위·아래 두 칸(#dock-top · #dock-bottom), 그 사이 끌어서 비율을 바꾸는 손잡이.
//  - 기본 탭: 위 = 도구·색·내비·페이지, 아래 = 레이어·에셋·기록·애니 (웹 registerPanel 순서)
//  - 탭 머리: 아이콘 + 이름(넘치면 …), 켜진 탭은 밝은 글자 + 주황 밑줄, 맨 오른쪽 ⧉ = 지금 탭을 앱 밖 창으로
//  - 고른 탭은 settings.dock_top / dock_bottom (웹 패널 id), 비율은 settings.dockSplit (0.15~0.85)
//  - 패널 내용은 처음 고를 때 만듦 (웹 판처럼). 숨긴 탭(ui.hiddenPanels)·꺼낸 탭은 머리에서 빠짐
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.UI.Chrome;

public sealed class SideDef
{
    public string Id, Title, Icon, Dock;
    public Func<Control> Create;
    public Control Ctl;
    public bool Popped;
}

public sealed class SidePanel : Control
{
    public readonly List<SideDef> Defs = new();
    readonly DockArea top, bottom;
    readonly SplitHandle split;
    bool leftSide;

    /// <summary>네이티브 패널 제목 → 웹 패널 id·아이콘</summary>
    static readonly Dictionary<string, (string id, string icon, string title)> Known = new()
    {
        ["내비"] = ("nav", "nav", "내비"), ["페이지"] = ("pages", "pages", "페이지"), ["에셋"] = ("assets", "star", "에셋"), ["기록"] = ("history", "history", "기록"), ["애니 타임라인"] = ("anim", "film", "애니"),
    };
    static readonly string[] TopOrder = { "tool", "color", "nav", "pages" }, BottomOrder = { "layers", "assets", "history", "anim" };

    public SidePanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Theme.Font;
        Dock = DockStyle.Right;
        Defs.Add(new SideDef { Id = "tool", Title = "도구", Icon = "brush", Dock = "top", Create = () => new ToolOptionsPanel() });
        Defs.Add(new SideDef { Id = "color", Title = "색", Icon = "palette", Dock = "top", Create = () => new ColorPanel() });
        Defs.Add(new SideDef { Id = "layers", Title = "레이어", Icon = "layers", Dock = "bottom", Create = () => new LayersPanel() });
        foreach (var p in Registry.Panels.OrderBy(p => p.Order))
        {
            var (id, icon, title) = Known.TryGetValue(p.Title, out var k) ? k : (p.Title, "dots", p.Title);
            string dock = id == "anim" || p.Dock == "bottom" ? "bottom" : id is "nav" or "pages" ? "top" : id is "assets" or "history" ? "bottom" : "top";
            var def = p;
            Defs.Add(new SideDef { Id = id, Title = title, Icon = icon, Dock = dock, Create = def.Create });
        }
        // 웹 판 순서로 (모르는 패널은 각 칸 끝에)
        int Rank(SideDef d) { int i = Array.IndexOf(d.Dock == "top" ? TopOrder : BottomOrder, d.Id); return i < 0 ? 100 + Defs.IndexOf(d) : i; }
        var sorted = Defs.OrderBy(d => d.Dock == "top" ? 0 : 1).ThenBy(Rank).ToList();
        Defs.Clear(); Defs.AddRange(sorted);

        top = new DockArea(this, "top");
        bottom = new DockArea(this, "bottom");
        split = new SplitHandle(this);
        Controls.Add(top); Controls.Add(split); Controls.Add(bottom);
    }

    public int SideWidth => Theme.SideW;   // 웹: 폭 300 (왼쪽 1px 경계 포함)
    public bool LeftSide { get => leftSide; set { leftSide = value; Dock = value ? DockStyle.Left : DockStyle.Right; PerformLayout(); Invalidate(); } }

    public double SplitRatio
    {
        get => Math.Clamp(App.Get("dockSplit", 0.48), 0.15, 0.85);
        set { App.Settings["dockSplit"] = Math.Round(Math.Clamp(value, 0.15, 0.85), 3); PerformLayout(); }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (Width != SideWidth) { Width = SideWidth; return; }
        int x = leftSide ? 0 : 1, w = Width - 1, h = Height;
        int th = (int)Math.Round(h * SplitRatio);
        top.Bounds = new Rectangle(x, 0, w, th);
        split.Bounds = new Rectangle(x, th, w, 8);
        bottom.Bounds = new Rectangle(x, th + 8, w, Math.Max(0, h - th - 8));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Panel);
        using var b = new SolidBrush(Theme.Border);
        e.Graphics.FillRectangle(b, leftSide ? Width - 1 : 0, 0, 1, Height);
    }

    // ---- 탭 ------------------------------------------------------------------------------
    public SideDef Find(string idOrTitle) => idOrTitle == null ? null : Defs.Find(d => d.Id == idOrTitle) ?? Defs.Find(d => d.Title == idOrTitle) ?? Defs.Find(d => Known.TryGetValue(idOrTitle, out var k) && k.id == d.Id);
    public static bool Shown(SideDef d) => UiMode.PanelShown(d.Id) && !d.Popped;
    DockArea AreaOf(SideDef d) => d.Dock == "top" ? top : bottom;

    /// <summary>탭 고르기 (웹 showDockTab)</summary>
    public void ShowTab(string idOrTitle)
    {
        var d = Find(idOrTitle);
        if (d == null || d.Popped) return;
        AreaOf(d).Select(d);
    }

    public string Selected(string dock) => (dock == "top" ? top : bottom).Current?.Id;
    public bool IsSelected(string id) { var d = Find(id); return d != null && AreaOf(d).Current == d; }

    /// <summary>탭 목록 다시 (숨긴 탭 설정이 바뀌면)</summary>
    public void Rerender() { top.Render(); bottom.Render(); }

    /// <summary>설정의 dock_top / dock_bottom 탭으로 (작업 화면 바꾸기)</summary>
    public void SelectSaved()
    {
        foreach (var a in new[] { top, bottom })
        {
            a.Render();
            var d = Find((string)App.Settings["dock_" + a.DockName]);
            if (d != null && a.List.Contains(d)) a.Select(d, false);
        }
        PerformLayout();
    }

    public Control ControlOf(string id) { var d = Find(id); if (d == null) return null; Ensure(d); return d.Ctl; }

    internal static void Ensure(SideDef d)
    {
        if (d.Ctl != null && !d.Ctl.IsDisposed) return;
        try { d.Ctl = d.Create(); }
        catch (Exception ex) { d.Ctl = new Label { Text = "패널 오류: " + ex.Message, ForeColor = Theme.Muted, Dock = DockStyle.Fill }; App.Toast($"패널 {d.Title} 오류: {ex.Message}"); }
        d.Ctl.Dock = DockStyle.Fill;
    }

    /// <summary>앱 밖 창으로 꺼내기: 패널 내용을 떼어 돌려줌 (탭에서 빠짐)</summary>
    public Control Detach(string idOrTitle)
    {
        var d = Find(idOrTitle);
        if (d == null || d.Popped) return null;
        Ensure(d);
        var area = AreaOf(d);
        d.Popped = true;
        d.Ctl.Parent?.Controls.Remove(d.Ctl);
        area.Render();
        return d.Ctl;
    }

    /// <summary>꺼낸 패널을 탭으로 되돌리기</summary>
    public void Attach(string idOrTitle)
    {
        var d = Find(idOrTitle);
        if (d == null || !d.Popped) return;
        d.Popped = false;
        if (d.Ctl != null && !d.Ctl.IsDisposed) { d.Ctl.Parent?.Controls.Remove(d.Ctl); d.Ctl.Dock = DockStyle.Fill; }
        var area = AreaOf(d);
        area.Render();
        area.Select(d);
    }

    // ================================================================= 한 칸 (탭 머리 + 내용)
    sealed class DockArea : Control
    {
        readonly SidePanel side; readonly string name;
        readonly Tabs tabs; readonly Panel body = new() { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        public SideDef Current;
        public List<SideDef> List = new();

        public DockArea(SidePanel side, string name)
        {
            this.side = side; this.name = name;
            BackColor = Theme.Panel;
            tabs = new Tabs(this);
            Controls.Add(body); Controls.Add(tabs);
        }

        public void Render()
        {
            List = side.Defs.Where(d => d.Dock == name && Shown(d)).ToList();
            string want = (string)App.Settings["dock_" + name];
            var first = (Current != null && List.Contains(Current) ? Current : null) ?? List.Find(d => d.Id == want || d.Title == want) ?? List.FirstOrDefault();
            if (first != null) Select(first, save: false); else { Current = null; foreach (Control c in body.Controls) c.Visible = false; }
            tabs.Relayout();
        }

        public void Select(SideDef d, bool save = true)
        {
            if (!List.Contains(d)) { List = side.Defs.Where(x => x.Dock == name && Shown(x)).ToList(); if (!List.Contains(d)) return; }
            Current = d;
            if (save) { App.Settings["dock_" + name] = d.Id; App.SaveSettingsSoon(); }
            Ensure(d);
            if (d.Ctl.Parent != body) body.Controls.Add(d.Ctl);
            body.SuspendLayout();
            foreach (Control c in body.Controls) c.Visible = c == d.Ctl;
            body.ResumeLayout();
            tabs.Invalidate();
        }

        public string DockName => name;
    }

    // ================================================================= 탭 머리 (웹 .dock-tabs)
    sealed class Tabs : PaintedBar
    {
        readonly DockArea area;
        static Font small, sym;
        static Font Small => small ??= new Font(Theme.FontName, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        static Font Sym => sym ??= new Font("Segoe UI Symbol", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Color Strip = ColorTranslator.FromHtml("#29292e");

        public Tabs(DockArea area) { this.area = area; Dock = DockStyle.Top; Height = 36; Ready(); }

        protected override void LayoutHits()
        {
            var list = area.List;
            int n = list.Count + 1;
            for (int i = 0; i < n; i++)
            {
                int x0 = Width * i / n, x1 = Width * (i + 1) / n;
                var r = new Rectangle(x0, 0, x1 - x0, Height);
                if (i < list.Count) { var d = list[i]; Hits.Add(new Hit { R = r, Data = d, Tip = d.Title, Click = () => area.Select(d) }); }
                else Hits.Add(new Hit { R = r, Data = "pop", Tip = "지금 탭을 앱 밖 창으로 꺼내기 (다른 모니터로 옮기거나 항상 위에)", Click = () => { if (area.Current != null) Popout.Panel(area.Current.Title, App.Get("popoutOnTop", false)); } });
            }
        }

        protected override void PaintBar(Graphics g)
        {
            g.Clear(Strip);
            foreach (var h in Hits)
            {
                if (h.Data is SideDef d)
                {
                    bool on = area.Current == d;
                    if (on) { Fill(g, h.R, Theme.Panel, 0); Line(g, Theme.Accent, h.R.X, h.R.Bottom - 2, h.R.Width, 2); }
                    var col = on ? Theme.Text : Theme.Muted;
                    int avail = h.R.Width - 8;
                    int tw = TextW(d.Title, Small), cw = Math.Min(avail, 16 + 5 + tw);
                    int x = h.R.X + 4 + (avail - cw) / 2;
                    Icons.Draw(g, d.Icon, new RectangleF(x, (Height - 2 - 16) / 2f + 1, 16, 16), col);
                    var tr = new Rectangle(x + 21, 0, Math.Max(0, cw - 21), Height - 1);
                    TextRenderer.DrawText(g, d.Title, Small, tr, col, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
                else
                {
                    var col = Theme.Blend(Strip, Theme.Muted, 0.7);   // 웹 opacity .7
                    DrawText(g, "⧉", Sym, h.R, h == Hover ? Theme.Text : col);
                }
            }
        }
    }

    // ================================================================= 위·아래 비율 손잡이 (웹 .dock-split)
    sealed class SplitHandle : Control
    {
        readonly SidePanel side;
        static readonly Color Bg = ColorTranslator.FromHtml("#1f1f23");
        bool drag;
        public SplitHandle(SidePanel side)
        {
            this.side = side;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Cursor = Cursors.HSplit;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Bg);
            using var b = new SolidBrush(Theme.Line);
            e.Graphics.FillRectangle(b, Width / 2 - 18, 3, 36, 2);
        }
        protected override void OnMouseDown(MouseEventArgs e) { drag = true; Capture = true; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!drag) return;
            var p = side.PointToClient(PointToScreen(e.Location));
            side.SplitRatio = p.Y / (double)Math.Max(1, side.Height);
        }
        protected override void OnMouseUp(MouseEventArgs e) { if (!drag) return; drag = false; Capture = false; App.SaveSettingsSoon(); }
    }
}

/// <summary>"도구" 탭 (웹 toolPropsPanel): 머리 = 도구 아이콘 + 이름 (주황), 아래 = 도구 옵션</summary>
public sealed class ToolOptionsPanel : Panel
{
    readonly Panel body = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Panel };
    readonly Head head = new();

    public ToolOptionsPanel()
    {
        BackColor = Theme.Panel;
        Controls.Add(body); Controls.Add(head);
        App.ToolChanged += Build;
        Disposed += (_, _) => App.ToolChanged -= Build;
        Build();
    }

    void Build()
    {
        if (IsDisposed) return;
        head.Invalidate();
        body.SuspendLayout();
        foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
        body.Controls.Clear();
        Control opt = null;
        try { opt = App.Tool?.BuildOptions(); } catch (Exception ex) { opt = new Label { Text = "옵션 표시 오류: " + ex.Message, AutoSize = true, ForeColor = Theme.Muted }; }
        if (opt == null && App.Tool != null) opt = new Label { Text = "설정할 옵션이 없습니다.", AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small, Margin = new Padding(0, 6, 0, 6) };
        if (opt != null) { opt.Location = new Point(4, 0); body.Controls.Add(opt); }
        body.ResumeLayout();
        // 가로 스크롤 막대는 숨김 (웹 .panel-scroll 은 세로만)
        body.AutoScroll = false; body.HorizontalScroll.Maximum = 0; body.HorizontalScroll.Visible = false; body.AutoScroll = true;
    }

    sealed class Head : Control
    {
        static Font bold;
        static Font Bold => bold ??= new Font(Theme.FontName, Theme.FontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        public Head() { Dock = DockStyle.Top; Height = 30; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Panel);
            var t = App.Tool; if (t == null) return;
            Icons.Draw(g, ToolBox.IconOf(t) ?? "dots", new RectangleF(10, 8, 18, 18), Theme.Accent);
            TextRenderer.DrawText(g, t.Name, Bold, new Rectangle(36, 8, Width - 46, 18), Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }
}
