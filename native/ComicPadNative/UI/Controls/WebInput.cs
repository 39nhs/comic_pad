// 웹 input[type=text|number|search] · textarea: 어두운 칸(#222227) + 테두리(--line) + 둥근 6px, 최소 높이 34px.
// 색 칸(input[type=color], 42×32), 도움말(.help), 구역 제목(.section-title), 구역(.section), 패널 머리(.panel-head).
using System.Globalization;

namespace ComicPadNative.UI.Controls;

public class WebInput : Control, IWebBlock
{
    public readonly TextBox Box;
    /// <summary>흐린 안내 글 (웹 placeholder)</summary>
    public string Placeholder { get => Box?.PlaceholderText; set { if (Box != null) Box.PlaceholderText = value ?? ""; } }
    bool hover;
    public WebInput(string text = "", bool multiline = false)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Web.Body;
        Margin = new Padding(0, 4, 0, 4);
        Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Web.Field, ForeColor = Theme.Text, Font = Web.Body, Text = text ?? "", Multiline = multiline };
        if (multiline) { Box.ScrollBars = ScrollBars.Vertical; Box.AcceptsReturn = true; Box.WordWrap = true; }
        Controls.Add(Box);
        Box.TextChanged += (_, _) => { OnTextChanged(EventArgs.Empty); Invalidate(); };
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Box.MouseEnter += (_, _) => { hover = true; Invalidate(); };
        Box.MouseLeave += (_, _) => { hover = false; Invalidate(); };
        Box.KeyDown += (_, e) => OnKeyDown(e);
        Height = multiline ? 96 : 34; Width = 200;
        Cursor = Cursors.IBeam;
    }
    public override string Text { get => Box?.Text ?? ""; set { if (Box != null) Box.Text = value ?? ""; } }
    public bool Multiline => Box.Multiline;
    public bool ReadOnly { get => Box.ReadOnly; set => Box.ReadOnly = value; }
    public int Rows = 4;
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : Width;
        int h = Box.Multiline ? Math.Max(60, Rows * (Box.Font.Height + 2) + 14) : Math.Max(Theme.Large ? 40 : 34, Box.PreferredHeight + 14);
        return new Size(w, h);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Box == null) return;
        if (Box.Multiline) Box.SetBounds(9, 7, Math.Max(10, Width - 14), Math.Max(10, Height - 14));
        else { int bh = Box.PreferredHeight; Box.SetBounds(9, (Height - bh) / 2, Math.Max(10, Width - 18), bh); }
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Box.Focus(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Box.Focus(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (Box != null) Box.Enabled = Enabled; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        Web.Box(g, ClientRectangle, 6, Web.Field, Box.Focused ? Web.Mix(Theme.Line, Theme.Accent, 0.7) : hover ? Web.Mix(Theme.Line, Theme.Text, 0.2) : Theme.Line);
        if (Box.Text.Length == 0 && !string.IsNullOrEmpty(Placeholder))
            Web.Text(g, Placeholder, Box.Font, new Rectangle(10, Box.Multiline ? 7 : 0, Width - 20, Box.Multiline ? Box.Font.Height + 4 : Height), Theme.Muted, Box.Multiline ? Web.Wrap : Web.Left);
    }
    public void SelectAll() { Box.Focus(); Box.SelectAll(); }
}

/// <summary>숫자 칸 (웹 input[type=number]): 위·아래 키·휠(포커스 있을 때)로 step 만큼, 범위 밖이면 맞춤</summary>
public class WebNumber : WebInput, ISyncable
{
    public double Min = -1e9, Max = 1e9, Step = 1;
    public int Decimals = -1;
    public event Action<double> ValueChanged;
    double value;
    bool setting;

    public WebNumber(double v, double min = -1e9, double max = 1e9, double step = 1, int decimals = -1)
    {
        Min = min; Max = max; Step = step <= 0 ? 1 : step; Decimals = decimals;
        value = Math.Clamp(v, min, max);
        setting = true; Text = Fmt(value); setting = false;
        Box.TextChanged += (_, _) => { if (setting) return; if (TryParse(Box.Text, out var d)) { value = Math.Clamp(d, Min, Max); ValueChanged?.Invoke(value); } };
        Box.LostFocus += (_, _) => { setting = true; Text = Fmt(value); setting = false; };
        Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Up or Keys.Down) { Value = value + (e.KeyCode == Keys.Up ? 1 : -1) * Step * (e.Shift ? 10 : 1); e.SuppressKeyPress = true; e.Handled = true; }
        };
        Box.MouseWheel += (_, e) => { if (!Box.Focused) return; if (e is HandledMouseEventArgs h) h.Handled = true; Value = value + Math.Sign(e.Delta) * Step; };
        Width = 160;
    }
    int Dec => Decimals >= 0 ? Decimals : Step % 1 == 0 && value % 1 == 0 ? 0 : Step >= 0.1 ? 2 : 3;
    string Fmt(double v) => Math.Round(v, Dec).ToString("0." + new string('#', Math.Max(0, Dec)), CultureInfo.InvariantCulture).TrimEnd('.');
    static bool TryParse(string s, out double d) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out d);
    public double Value
    {
        get => value;
        set
        {
            double v = Math.Clamp(value, Min, Max);
            if (Step > 0 && Step % 1 == 0 && Decimals <= 0) v = Math.Round(v);
            this.value = v;
            setting = true; Text = Fmt(v); setting = false;
            ValueChanged?.Invoke(v);
        }
    }
    public void Sync() { setting = true; Text = Fmt(value); setting = false; }
}

/// <summary>색 칸 (웹 input[type=color] 42×32). 누르면 색 고르기</summary>
public class WebSwatch : Control, ISyncable
{
    readonly Func<string> get; readonly Action<string> set;
    public string Label;
    bool hover;
    public WebSwatch(Func<string> get, Action<string> set, string label = null)
    {
        this.get = get; this.set = set; Label = label;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Web.Body; Margin = new Padding(0, 4, 6, 4); Cursor = Cursors.Hand;
        Size = GetPreferredSize(Size.Empty);
    }
    int LabelW => string.IsNullOrEmpty(Label) ? 0 : Web.Measure(Label, Font).Width + 6;
    public override Size GetPreferredSize(Size proposed) => new(LabelW + 42, 32);
    public string Hex { get { try { return get() ?? "#000000"; } catch { return "#000000"; } } }
    public static Color ColorOf(string hex) { try { var c = ColorTranslator.FromHtml(hex.Length == 9 ? hex[..7] : hex); return Color.FromArgb(255, c); } catch { return Color.Black; } }
    public void Sync() => Invalidate();
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        if (LabelW > 0) Web.Text(g, Label, Font, new Rectangle(0, 0, LabelW, Height), Theme.Muted, Web.Left);
        var r = new Rectangle(LabelW, 0, 42, Height);
        Web.Box(g, r, 6, null, hover ? Theme.Accent : Theme.Line);
        Web.Box(g, Rectangle.Inflate(r, -4, -4), 2, ColorOf(Hex));
    }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        using var dlg = new ColorDialog { Color = ColorOf(Hex), FullOpen = true };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        set($"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}");
        Invalidate();
        App.NotifySettings();
    }
}

/// <summary>도움말 글 (.help: 흐린 12px, 줄 높이 1.5)</summary>
public class WebHelp : Label
{
    public WebHelp(string text)
    {
        Text = text; ForeColor = Theme.Muted; Font = Web.Small; AutoSize = true; UseMnemonic = false;
        Margin = new Padding(0, 6, 0, 6); BackColor = Color.Transparent;
    }
}

/// <summary>구역 제목 (.section-title: 흐린 11px). 첫 칸이 아니면 위에 구역 나눔 줄 (.section 아래 줄)</summary>
public class WebSectionTitle : Control, IWebBlock
{
    public bool Separator = true;
    public WebSectionTitle(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Web.Tiny; Margin = Padding.Empty;
    }
    bool First => Parent == null || Parent.Controls.Cast<Control>().FirstOrDefault(BlockLayout.Shown) == this;
    int Top0 => Separator && !First ? 8 + 1 + 6 : 6;
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : Web.Measure(Text, Font).Width;
        return new Size(w, Top0 + 8 + Web.Measure("가", Font).Height + 6);
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        if (Separator && !First) using (var p = new Pen(Web.Sep)) g.DrawLine(p, 0, 8, Width, 8);
        int y = Top0 + 8, th = Web.Measure("가", Font).Height;
        Web.Text(g, Text, Font, new Rectangle(0, y, Width, th + 2), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}

/// <summary>구역 (.section: 위 6·아래 8 여백, 아래 줄 #38383f, 마지막 구역은 줄 없음) + 제목</summary>
public class WebSection : WebStack
{
    public WebSection(string title, params Control[] items)
    {
        Padding = new Padding(0, 6, 0, 8);
        if (!string.IsNullOrEmpty(title)) Controls.Add(new WebSectionTitle(title) { Separator = false });
        foreach (var c in items) if (c != null) Controls.Add(c);
        FillParent = false;
    }
    bool Last => Parent == null || Parent.Controls.Cast<Control>().LastOrDefault(c => BlockLayout.Shown(c) && c.Dock == DockStyle.None) == this;
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Last) using (var p = new Pen(Web.Sep)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
    }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Parent?.Invalidate(true); }
}

/// <summary>패널 머리 (.panel-head: 아이콘 + 주황 굵은 이름)</summary>
public class WebPanelHead : Control, IWebBlock
{
    public string IconName;
    public WebPanelHead(string text, string icon = null)
    {
        Text = text; IconName = icon;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Web.BodyBold; Height = 34; Margin = Padding.Empty;
    }
    public override Size GetPreferredSize(Size proposed) => new(proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 200, 34);
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        int x = 10, y = 8 + (Height - 12) / 2;
        if (IconName != null) { Icons.Draw(g, IconName, new RectangleF(x, y - 9, 18, 18), Theme.Accent); x += 18 + 8; }
        Web.Text(g, Text, Font, new Rectangle(x, 8, Width - x - 10, Height - 12), Theme.Accent, Web.Left);
    }
}
