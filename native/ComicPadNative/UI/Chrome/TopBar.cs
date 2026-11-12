// 위 막대 (웹 #topbar, 높이 44): "ComicPad" 글자 · 메뉴 막대(#menubar) · 오른쪽 단추(#top-actions).
//  - 메뉴는 웹 판과 같은 메뉴·항목·순서 (WebMenus), 간단히 모드면 자주 쓰는 것만 + "모든 항목 보기 (N개 더)"
//  - 오른쪽 단추: 기능 검색 · 실행 취소 · 다시 실행 | 저장 · 내보내기 | 손가락 그리기 · 패널 · 전체 화면 · 설정 (웹 initTopActions 순서)
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.UI.Chrome;

public sealed class TopBar : PaintedBar
{
    public readonly MenuStrip Menu = new()
    {
        Dock = DockStyle.None, AutoSize = false, GripStyle = ToolStripGripStyle.Hidden, Padding = Padding.Empty, Stretch = false, CanOverflow = false,
        BackColor = Theme.Panel, ForeColor = Theme.Text, Renderer = MenuLook.Instance, ShowItemToolTips = false, LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow,
    };

    sealed record Act(string Icon, string Tip, string Cmd, Func<bool> Disabled = null, Func<bool> On = null, Func<bool> Dirty = null);
    static readonly Act[] Actions =
    {
        new("search", "기능 검색 (Ctrl+K)", "palette.open"),
        new("undo", "실행 취소 (Ctrl+Z / 두 손가락 탭)", "edit.undo", () => !App.History.CanUndo),
        new("redo", "다시 실행 (Ctrl+Y / 세 손가락 탭)", "edit.redo", () => !App.History.CanRedo),
        null,
        new("save", "저장 (Ctrl+S)", "file.save", Dirty: () => App.Doc?.Modified == true),
        new("export", "내보내기", "file.export"),
        null,
        new("finger", "손가락으로 그리기 켜기/끄기", "settings.fingerDraw", On: () => App.Get("fingerDraw", false)),
        new("side", "패널 보이기/숨기기", "view.toggleSide"),
        new("fullscreen", "전체 화면", "view.fullscreen"),
        new("gear", "설정", "settings.open"),
    };

    const string Brand = "ComicPad";
    static Font brandFont;
    static Font BrandFont => brandFont ??= new Font(Theme.FontName, Theme.FontPx, FontStyle.Bold, GraphicsUnit.Pixel);
    int menuX;

    public TopBar()
    {
        Dock = DockStyle.Top; Height = Theme.TopBarH;
        Controls.Add(Menu);
        Menu.Font = Theme.Font;
        App.History.Changed += Invalidate;
        App.DocChanged += Invalidate;
        App.SettingsChanged += Invalidate;
        App.LayersChanged += Invalidate;
        Ready();
    }

    protected override void LayoutHits()
    {
        int bw = Math.Min(Theme.Btn, Height), x = Width - 6, y = (Height - 1 - bw) / 2;
        for (int i = Actions.Length - 1; i >= 0; i--)
        {
            var a = Actions[i];
            if (a == null) { x -= 9 + 2; continue; }
            x -= bw;
            var act = a;
            Hits.Add(new Hit { R = new Rectangle(x, y, bw, bw), Tip = a.Tip, Data = a, Click = () => WebIds.Run(act.Cmd), Enabled = () => act.Disabled?.Invoke() != true });
            x -= 2;
        }
        int bwid = TextW(Brand, BrandFont) + 3;
        menuX = 6 + 6 + bwid + 10 + 4;
        int mw = Math.Max(0, Math.Min(MenuWidth(), x - 8 - menuX));
        Menu.Bounds = new Rectangle(menuX, (Height - 34 + 1) / 2, mw, 34);
    }

    int MenuWidth() { int w = 0; foreach (ToolStripItem it in Menu.Items) if (it.Available) w += it.Width + it.Margin.Horizontal; return w; }

    protected override void PaintBar(Graphics g)
    {
        g.Clear(Theme.Panel);
        Line(g, Theme.Border, 0, Height - 1, Width, 1);
        // 이름 (굵게, 주황, 글자 사이 조금 띄움)
        int x = 12;
        foreach (char c in Brand)
        {
            string s = c.ToString();
            DrawText(g, s, BrandFont, new Rectangle(x, 0, 40, Height - 1), Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            x += TextW(s, BrandFont);
        }
        // 오른쪽 단추 + 구분선
        int bw = Math.Min(Theme.Btn, Height);
        int sx = Width - 6;
        for (int i = Actions.Length - 1; i >= 0; i--)
        {
            if (Actions[i] == null) { sx -= 9 + 2; Line(g, Theme.Line, sx + 2 + 4, (Height - 1 - 22) / 2, 1, 22); continue; }
            sx -= bw + 2;
        }
        foreach (var h in Hits)
        {
            var a = (Act)h.Data;
            bool dis = !h.IsEnabled, on = a.On?.Invoke() == true;
            if (on) Fill(g, h.R, Theme.AccentDim, Theme.Radius);
            else if (h == Hover && !dis) Fill(g, h.R, Theme.Panel3, Theme.Radius);
            Color c = on || a.Dirty?.Invoke() == true ? Theme.Accent : Theme.Text;
            if (dis) c = Theme.Blend(Theme.Panel, c, 0.35);
            Icons.Draw(g, a.Icon, new RectangleF(h.R.X + (h.R.Width - 20) / 2f, h.R.Y + (h.R.Height - 20) / 2f, 20, 20), c);
        }
    }

    // ---- 메뉴 ------------------------------------------------------------------------------
    readonly HashSet<string> showAllOnce = new();

    public void BuildMenus()
    {
        Menu.SuspendLayout();
        foreach (ToolStripItem it in Menu.Items.Cast<ToolStripItem>().ToList()) { Menu.Items.Remove(it); it.Dispose(); }
        WebMenus.Invalidate();
        foreach (var top in WebMenus.Tree())
        {
            var t = top;
            var ti = new ToolStripMenuItem(top.Label) { AutoSize = false, Tag = top.Id, Margin = new Padding(0, 0, 2, 0), Padding = Padding.Empty, Font = Theme.Font, ForeColor = Theme.Text };
            ti.Size = new Size(TextW(top.Label, Theme.Font) + 24, 34);
            var dd = MenuLook.NewDropDown();
            ti.DropDown = dd;
            ti.DropDownItems.Add(new ToolStripMenuItem(" "));   // 열 수 있게 (열 때 채움)
            ti.DropDownOpening += (_, _) => Fill(ti, t);
            ti.Visible = UiMode.MenuShown(top.Id);
            Menu.Items.Add(ti);
        }
        Menu.ResumeLayout();
        Relayout();
    }

    public IEnumerable<ToolStripMenuItem> TopItems => Menu.Items.OfType<ToolStripMenuItem>();

    /// <summary>펼칠 때마다 항목을 다시 만듦 (간단히 모드 거르기·켜짐·쓸 수 있는지)</summary>
    void Fill(ToolStripMenuItem ti, MenuTop top)
    {
        bool all = showAllOnce.Remove(top.Id);
        var (items, hidden) = UiMode.FilterMenu(top.Id, top.Items, all);
        var dd = ti.DropDown;
        dd.SuspendLayout();
        foreach (ToolStripItem old in dd.Items.Cast<ToolStripItem>().ToList()) { dd.Items.Remove(old); old.Dispose(); }
        AddNodes(dd.Items, items);
        if (hidden > 0)
        {
            dd.Items.Add(MenuLook.Sep());
            var more = MenuLook.Item($"모든 항목 보기 ({hidden}개 더)", new MenuTag { More = true }, (_, _) =>
            {
                showAllOnce.Add(top.Id);
                BeginInvoke((Action)(() => ti.ShowDropDown()));
            });
            dd.Items.Add(more);
        }
        MenuLook.Equalize(dd);
        dd.ResumeLayout();
        // 화면 아래로 넘치면 높이를 줄임 (웹: max-height)
        var scr = Screen.FromControl(this).WorkingArea;
        dd.MaximumSize = new Size(0, Math.Max(200, scr.Bottom - PointToScreen(new Point(0, Height)).Y - 8));
    }

    static void AddNodes(ToolStripItemCollection into, List<MenuNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Sep) { into.Add(MenuLook.Sep()); continue; }
            if (n.Sub != null)
            {
                var si = MenuLook.Item(n.Label, new MenuTag { Key = n.Key });
                var sdd = MenuLook.NewDropDown();
                si.DropDown = sdd;
                AddNodes(si.DropDownItems, n.Sub);
                MenuLook.Equalize(sdd);
                MenuLook.Fit(si);
                into.Add(si);
                continue;
            }
            var node = n;
            var tag = new MenuTag { Key = n.Key, Kbd = WebMenus.KeyLabel(n.Shortcut) };
            var mi = MenuLook.Item(n.Label, tag, (_, _) => Exec(node));
            mi.Enabled = n.Enabled;
            mi.Checked = n.Checked;
            if (n.Path == null && n.Run == null) mi.ToolTipText = "네이티브 판에는 없는 기능입니다";
            MenuLook.Fit(mi);
            into.Add(mi);
        }
    }

    public static void Exec(MenuNode n)
    {
        if (n.Run != null) { MainForm.Run(n.Run, n.Label); return; }
        if (n.Path == null) return;
        var def = n.Def;
        if (def?.Run == null) return;
        if (def.Enabled != null && !def.Enabled()) { App.Toast("지금은 쓸 수 없습니다: " + n.Label); return; }
        MainForm.Run(def.Run, def.Path);
    }

    /// <summary>메뉴 열기 (시험·화면 찍기용). all = 모든 항목 보기</summary>
    public ToolStripMenuItem Open(string id, bool all = false)
    {
        var ti = TopItems.FirstOrDefault(t => (string)t.Tag == id || t.Text == id);
        if (ti == null) return null;
        if (all) showAllOnce.Add((string)ti.Tag);
        ti.ShowDropDown();
        return ti;
    }
}
