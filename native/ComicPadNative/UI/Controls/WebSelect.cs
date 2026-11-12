// 웹 select: 어두운 칸(#222227) + 테두리(--line) + 둥근 6px + 오른쪽 꺾쇠. 누르면 어두운 목록이 펼쳐짐 (마우스 휠로 값이 바뀌지 않음 — 패널 굴리기).
// WebSelectRow = 웹 label.ctl.select (왼쪽 흐린 이름 + 오른쪽 고르기 칸).
namespace ComicPadNative.UI.Controls;

public class WebSelect : Control, IWebBlock, ISyncable
{
    List<(string id, string text)> items;
    readonly Func<string> get;
    readonly Action<string> set;
    bool hover, open;
    int index = -1;
    public event EventHandler SelectedIndexChanged;

    public WebSelect(IEnumerable<(string id, string text)> items, Func<string> get = null, Action<string> set = null)
    {
        this.items = items.ToList(); this.get = get; this.set = set;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Web.Body; ForeColor = Theme.Text;
        Margin = new Padding(0, 4, 0, 4);
        Height = 34; Width = 200; TabStop = true; Cursor = Cursors.Hand;
        Sync();
    }

    public IReadOnlyList<(string id, string text)> Items => items;
    public void SetItems(IEnumerable<(string id, string text)> list) { items = list.ToList(); Sync(); }
    public int SelectedIndex
    {
        get => index;
        set { int v = Math.Clamp(value, -1, items.Count - 1); if (v == index) return; index = v; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }
    public string SelectedId { get => index >= 0 && index < items.Count ? items[index].id : null; set => SelectedIndex = items.FindIndex(i => i.id == value); }
    public override string Text { get => index >= 0 && index < items.Count ? items[index].text : ""; set { } }

    public void Sync()
    {
        if (get == null) { if (index < 0 && items.Count > 0) index = 0; Invalidate(); return; }
        string cur; try { cur = get(); } catch { cur = null; }
        int i = items.FindIndex(x => x.id == cur);
        index = i >= 0 ? i : items.Count > 0 ? 0 : -1;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : Math.Min(320, (items.Count == 0 ? 60 : items.Max(i => Web.Measure(i.text, Font).Width)) + 44);
        return new Size(w, Theme.Large ? 40 : 34);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        var r = ClientRectangle;
        Web.Box(g, r, 6, Web.Field, open || Focused ? Web.Mix(Theme.Line, Theme.Accent, 0.6) : hover ? Web.Mix(Theme.Line, Theme.Text, 0.25) : Theme.Line);
        var fg = Enabled ? Theme.Text : Theme.Muted;
        Web.Text(g, Text, Font, new Rectangle(9, 0, r.Width - 34, r.Height), fg, Web.Left);
        // 꺾쇠 (크롬 어두운 select)
        Web.Smooth(g);
        float cx = r.Right - 16, cy = r.Height / 2f;
        using var pen = new Pen(fg, 1.8f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        ShowList();
    }
    protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Down && e.Alt || e.KeyCode is Keys.Space or Keys.F4) { ShowList(); e.Handled = true; }
        else if (e.KeyCode == Keys.Down && index < items.Count - 1) { Choose(index + 1); e.Handled = true; }
        else if (e.KeyCode == Keys.Up && index > 0) { Choose(index - 1); e.Handled = true; }
    }

    void Choose(int i)
    {
        if (i < 0 || i >= items.Count) return;
        bool changed = i != index;
        index = i; Invalidate();
        if (!changed) return;
        try { set?.Invoke(items[i].id); } catch (Exception ex) { App.Toast(ex.Message); }
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        App.NotifySettings();
    }

    void ShowList()
    {
        if (items.Count == 0) return;
        var lb = new WebList { Font = Font };
        lb.Items = items.Select(i => i.text).ToList();
        lb.Selected = index;
        int rows = Math.Min(items.Count, 14);
        lb.Size = new Size(Math.Max(Width, Math.Min(420, items.Max(i => Web.Measure(i.text, Font).Width) + 40)), rows * lb.RowH + 10);
        var host = new ToolStripControlHost(lb) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = lb.Size };
        var dd = new ToolStripDropDown { Padding = Padding.Empty, BackColor = Web.MenuBg, Renderer = new FlatRenderer(), DropShadowEnabled = true };
        dd.Items.Add(host);
        lb.Picked += i => { dd.Close(); Choose(i); };
        dd.Closed += (_, _) => { open = false; Invalidate(); BeginInvoke(() => { dd.Dispose(); }); };
        open = true; Invalidate();
        var below = PointToScreen(new Point(0, Height + 2));
        var wa = Screen.FromControl(this).WorkingArea;
        if (below.Y + lb.Height > wa.Bottom) below = PointToScreen(new Point(0, -lb.Height - 4));
        dd.Show(below);
        lb.Focus();
        lb.EnsureVisible(index);
    }

    sealed class FlatRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using var b = new SolidBrush(Web.MenuBg); e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using var p = new Pen(Web.ModalLine); e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
    }
}

/// <summary>어두운 목록 (펼침 목록·기능 검색 등). 위 항목에 마우스를 올리면 주황</summary>
public sealed class WebList : Control
{
    public List<string> Items = new();
    public List<string> Sub;          // 오른쪽 흐린 글 (단축키·메뉴 이름)
    public int Selected = -1, RowH = 34;
    int top, hot = -1;
    public event Action<int> Picked;
    public Color Bg = Web.MenuBg;

    public WebList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Web.Body; TabStop = true;
    }
    int Rows => Math.Max(1, (Height - 10) / RowH);
    public void EnsureVisible(int i) { if (i < 0) return; if (i < top) top = i; else if (i >= top + Rows) top = i - Rows + 1; top = Math.Clamp(top, 0, Math.Max(0, Items.Count - Rows)); Invalidate(); }
    public void Reset() { top = 0; hot = -1; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Bg);
        int y = 5;
        for (int i = top; i < Items.Count && y < Height - 4; i++, y += RowH)
        {
            var r = new Rectangle(5, y, Width - 10 - (Items.Count > Rows ? 6 : 0), RowH);
            bool on = i == hot || hot < 0 && i == Selected;
            if (on) Web.Box(g, r, 6, Theme.Accent);
            var fg = on ? Web.OnDark : Theme.Text;
            string sub = Sub != null && i < Sub.Count ? Sub[i] : null;
            int sw = string.IsNullOrEmpty(sub) ? 0 : Web.Measure(sub, Web.Tiny).Width + 8;
            Web.Text(g, Items[i], Font, new Rectangle(r.X + 12, r.Y, r.Width - 24 - sw, r.Height), fg, Web.Left);
            if (sw > 0) Web.Text(g, sub, Web.Tiny, new Rectangle(r.Right - 12 - sw, r.Y, sw, r.Height), on ? Color.FromArgb(0x33, 0x33, 0x33) : Theme.Muted, Web.Right);
        }
        if (Items.Count > Rows)
        {
            // 얇은 스크롤 표시
            int th = Math.Max(20, (Height - 10) * Rows / Items.Count);
            int ty = 5 + (Height - 10 - th) * top / Math.Max(1, Items.Count - Rows);
            Web.Box(g, new Rectangle(Width - 7, ty, 4, th), 2, Color.FromArgb(90, 255, 255, 255));
        }
    }
    int Hit(Point p) { int i = top + (p.Y - 5) / RowH; return p.Y >= 5 && i >= 0 && i < Items.Count ? i : -1; }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != hot) { hot = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); int h = Hit(e.Location); if (h >= 0 && e.Button == MouseButtons.Left) { Selected = h; Picked?.Invoke(h); } }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs he) he.Handled = true;
        top = Math.Clamp(top - Math.Sign(e.Delta) * 3, 0, Math.Max(0, Items.Count - Rows));
        hot = Hit(PointToClient(MousePosition)); Invalidate();
    }
    protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Enter or Keys.PageUp or Keys.PageDown || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Move(e.KeyCode) && e.KeyCode == Keys.Enter && Selected >= 0) Picked?.Invoke(Selected);
    }
    /// <summary>위·아래 키 처리 (검색 칸에서도 부름)</summary>
    public bool Move(Keys k)
    {
        int n = Items.Count; if (n == 0) return false;
        int d = k switch { Keys.Down => 1, Keys.Up => -1, Keys.PageDown => Rows, Keys.PageUp => -Rows, _ => 0 };
        if (d == 0) return false;
        hot = -1; Selected = Math.Clamp((Selected < 0 ? 0 : Selected) + d, 0, n - 1); EnsureVisible(Selected); Invalidate();
        return true;
    }
}

/// <summary>웹 label.ctl.select: 흐린 이름 (최소 40px) + 고르기 칸 (남은 폭)</summary>
public class WebSelectRow : Control, IWebBlock, ISyncable
{
    public readonly WebSelect Select;
    readonly string label;
    public WebSelectRow(string label, IEnumerable<(string id, string text)> items, Func<string> get, Action<string> set)
    {
        this.label = label;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent; Font = Web.Body;
        Margin = new Padding(0, 4, 0, 4);
        Select = new WebSelect(items, get, set) { Margin = Padding.Empty };
        Controls.Add(Select);
        Height = 34;
    }
    int LabelW => string.IsNullOrEmpty(label) ? 0 : Math.Max(40, Web.Measure(label, Font).Width + 1);
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : LabelW + 8 + Select.GetPreferredSize(Size.Empty).Width;
        return new Size(w, Select.GetPreferredSize(Size.Empty).Height);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Select == null) return;
        int lw = LabelW, x = lw > 0 ? lw + 8 : 0;
        Select.SetBounds(x, 0, Math.Max(40, Width - x), Height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Web.ParentBack(this));
        if (LabelW > 0) Web.Text(e.Graphics, label, Font, new Rectangle(0, 0, LabelW + 2, Height), Theme.Muted, Web.Left);
    }
    public void Sync() => Select.Sync();
}
