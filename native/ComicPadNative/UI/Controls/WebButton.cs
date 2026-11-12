// 웹 단추들: .btn (보통·primary·danger), .icon-btn (아이콘·글자, .on), 레이어 막대 단추, .flag (레이어 속성 칩),
// .toggle (켜기/끄기 스위치 칩, CheckBox 를 이어받아 Checked 그대로), 세그먼트 단추.
namespace ComicPadNative.UI.Controls;

public enum BtnKind { Normal, Primary, Danger, Icon, IconText, LayerTool, LayerToolText, Flag, Modal, ModalPrimary }

public class WebButton : Button
{
    BtnKind kind;
    public BtnKind Kind { get => kind; set { kind = value; Font = FontFor(value); Invalidate(); } }
    public string IconName;
    public int IconSize = 16;
    bool on;
    /// <summary>켜짐 표시 (.on)</summary>
    public bool On { get => on; set { if (on != value) { on = value; Invalidate(); } } }
    protected bool hover, pressed;

    public WebButton(string text = "", BtnKind kind = BtnKind.Normal, string icon = null)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Text = text; IconName = icon; Kind = kind;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        UseMnemonic = false;
        ForeColor = Theme.Text; BackColor = Color.Transparent;
        Margin = kind is BtnKind.Icon or BtnKind.IconText or BtnKind.LayerTool or BtnKind.LayerToolText ? new Padding(0) : new Padding(0, 4, 6, 4);
        if (kind is BtnKind.Icon or BtnKind.IconText) IconSize = 18;
        if (kind is BtnKind.LayerTool) IconSize = 18;
        Cursor = Cursors.Hand;
    }

    static Font FontFor(BtnKind k) => k switch
    {
        BtnKind.Primary or BtnKind.ModalPrimary => Web.BodyBold,
        BtnKind.IconText or BtnKind.LayerToolText => Web.F(12, true),
        BtnKind.Flag => Web.Small,
        _ => Web.Body,
    };

    int MinH => kind switch
    {
        BtnKind.Icon or BtnKind.IconText => Theme.Btn,
        BtnKind.LayerTool or BtnKind.LayerToolText => 36,
        BtnKind.Flag => 28,
        BtnKind.Modal or BtnKind.ModalPrimary => 40,
        _ => Theme.Large ? 40 : 34,
    };
    int PadX => kind switch
    {
        BtnKind.Icon or BtnKind.IconText or BtnKind.LayerTool or BtnKind.LayerToolText => 6,
        BtnKind.Flag => 8,
        _ => 12,
    };

    public override Size GetPreferredSize(Size proposed)
    {
        var ts = string.IsNullOrEmpty(Text) ? Size.Empty : Web.Measure(Text, Font);
        int iw = IconName != null ? IconSize : 0;
        int w = PadX * 2 + ts.Width + iw + (iw > 0 && ts.Width > 0 ? 6 : 0) + 2;
        int minW = kind switch { BtnKind.Icon or BtnKind.IconText => Theme.Btn, BtnKind.LayerTool or BtnKind.LayerToolText => 36, BtnKind.Modal or BtnKind.ModalPrimary => 90, _ => 0 };
        w = Math.Max(w, minW);
        if (MinimumSize.Width > 0) w = Math.Max(w, MinimumSize.Width);
        int h = Math.Max(MinH, ts.Height + 8);
        if (MinimumSize.Height > 0) h = Math.Max(h, MinimumSize.Height);
        if (MaximumSize.Width > 0) w = Math.Min(w, MaximumSize.Width);
        return new Size(w, h);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        var r = ClientRectangle;
        Color? fill = null, border = null; Color fg = Theme.Text; float rad = 7;
        switch (kind)
        {
            case BtnKind.Primary: case BtnKind.ModalPrimary:
                fill = hover ? Web.Mix(Theme.Accent, Color.White, 0.08) : Theme.Accent; border = fill; fg = Web.OnDark; break;
            case BtnKind.Icon: case BtnKind.IconText:
                rad = Theme.Radius;
                if (on) { fill = Theme.AccentDimOnPanel; fg = Theme.Accent; }
                else if (hover) fill = Theme.Panel3;
                break;
            case BtnKind.LayerTool: case BtnKind.LayerToolText:
                rad = Theme.Radius;
                if (on) { fill = Theme.Accent; fg = Color.Black; }
                else if (hover) fill = Theme.Panel3;
                break;
            case BtnKind.Flag:
                rad = r.Height / 2f;
                if (on) { fill = Web.Over(Web.ParentBack(this), Theme.AccentDim); border = Theme.Accent; fg = Theme.Accent; }
                else { border = Theme.Line; fg = Theme.Muted; if (hover) fill = Theme.Panel3; }
                break;
            default:
                fill = hover || on ? Theme.Panel3 : Theme.Panel2; border = Theme.Line;
                if (kind == BtnKind.Danger) fg = Web.Danger;
                break;
        }
        if (pressed && kind is not (BtnKind.Primary or BtnKind.ModalPrimary)) fill = Theme.Panel3;
        if (!Enabled) { fg = Color.FromArgb(90, fg); if (fill is Color fc) fill = Color.FromArgb(140, fc); }
        Web.Box(g, r, rad, fill, border);
        if (Focused && ShowFocusCues && kind is BtnKind.Normal or BtnKind.Primary or BtnKind.Modal or BtnKind.ModalPrimary or BtnKind.Danger)
            Web.Box(g, Rectangle.Inflate(r, -1, -1), rad, null, Color.FromArgb(150, Theme.Accent));
        var ts = string.IsNullOrEmpty(Text) ? Size.Empty : Web.Measure(Text, Font);
        int iw = IconName != null ? IconSize : 0;
        int total = ts.Width + iw + (iw > 0 && ts.Width > 0 ? 6 : 0);
        bool left = TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft;
        int x = left ? PadX : (r.Width - total) / 2;
        if (iw > 0)
        {
            Icons.Draw(g, IconName, new RectangleF(x, (r.Height - iw) / 2f, iw, iw), fg);
            x += iw + 6;
        }
        if (ts.Width > 0) Web.Text(g, Text, Font, new Rectangle(x, 0, Math.Max(1, r.Width - x - (left ? PadX : 0) + 2), r.Height), fg, Web.Left);
    }
}

/// <summary>웹 .toggle: 둥근 칩 안에 스위치 + 글자. CheckBox 라서 Checked·CheckedChanged 를 그대로 씀</summary>
public class WebToggle : CheckBox
{
    bool hover;
    public WebToggle(string text = "")
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Text = text; Font = Web.Body; ForeColor = Theme.Text; BackColor = Color.Transparent;
        AutoSize = true; UseMnemonic = false;
        Margin = new Padding(0, 4, 0, 4);
        Cursor = Cursors.Hand;
    }

    const int PadL = 6, PadR = 10, Box = 30, Gap = 8;
    int MinH => Theme.Large ? 40 : 34;

    public override Size GetPreferredSize(Size proposed)
    {
        int maxText = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width - PadL - PadR - Box - Gap : int.MaxValue / 4;
        if (MaximumSize.Width > 0) maxText = Math.Min(maxText, MaximumSize.Width - PadL - PadR - Box - Gap);
        var one = Web.Measure(Text, Font);
        if (one.Width <= maxText) return new Size(PadL + Box + Gap + one.Width + PadR + 1, Math.Max(MinH, one.Height + 8));
        var wrap = Web.MeasureWrap(Text, Font, Math.Max(20, maxText));
        return new Size(PadL + Box + Gap + Math.Min(maxText, wrap.Width) + PadR + 1, Math.Max(MinH, wrap.Height + 12));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        var r = ClientRectangle;
        Web.Box(g, r, 7, hover ? Web.Mix(Theme.Panel2, Theme.Panel3, 0.5) : Theme.Panel2);
        var bx = new Rectangle(PadL, (r.Height - 18) / 2, Box, 18);
        Web.Box(g, bx, 9, Checked ? Theme.Accent : Web.ToggleOff);
        var knob = new Rectangle(bx.X + (Checked ? 14 : 2), bx.Y + 2, 14, 14);
        Web.Smooth(g);
        using (var b = new SolidBrush(Checked ? Color.White : Web.KnobOff)) g.FillEllipse(b, knob);
        var fg = Enabled ? ForeColor : Theme.Muted;
        var tr = new Rectangle(bx.Right + Gap, 0, Math.Max(1, r.Width - bx.Right - Gap - PadR + 2), r.Height);
        var one = Web.Measure(Text, Font);
        if (one.Width <= tr.Width) Web.Text(g, Text, Font, tr, fg, Web.Left);
        else
        {
            var wsz = Web.MeasureWrap(Text, Font, tr.Width);
            Web.Text(g, Text, Font, new Rectangle(tr.X, (r.Height - wsz.Height) / 2, tr.Width, wsz.Height + 2), fg, Web.Wrap);
        }
        if (Focused && ShowFocusCues) Web.Box(g, r, 7, null, Color.FromArgb(150, Theme.Accent));
    }
}

/// <summary>세그먼트 안의 단추 하나 (WebSeg 가 씀)</summary>
public sealed class WebSegButton : Control
{
    public object Value;
    public bool On;
    bool hover;
    public WebSegButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Font = Web.Body; Cursor = Cursors.Hand; }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.SegBg);
        var r = ClientRectangle;
        if (On)
        {
            Web.Box(g, r, 6, Theme.Panel3);
            // box-shadow: inset 0 -2px 0 accent
            var st = g.Save();
            using (var clip = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), 6)) g.SetClip(clip);
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X, r.Bottom - 2, r.Width, 2);
            g.Restore(st);
        }
        else if (hover) Web.Box(g, r, 6, Color.FromArgb(40, 255, 255, 255));
        Web.Text(g, Text, Font, new Rectangle(4, 0, r.Width - 8, r.Height), On ? Theme.Text : Theme.Muted, Web.Center);
    }
}

/// <summary>색 단추: 왼쪽 색 칩 + 이름 (+ #hex). 누르면 색 고르기. Button 이라 예전 ColorBtn 자리에 그대로</summary>
public sealed class WebColorButton : WebButton, ISyncable
{
    readonly Func<string> get; readonly Action<string> set;
    readonly string label; readonly bool showHex;
    public WebColorButton(string label, Func<string> get, Action<string> set, bool showHex = false) : base(label ?? "")
    {
        this.label = label ?? ""; this.get = get; this.set = set; this.showHex = showHex;
        Sync();
        Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = WebSwatch.ColorOf(Hex), FullOpen = true };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            set($"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}");
            Sync();
        };
    }
    public string Hex { get { try { return get() ?? "#000000"; } catch { return "#000000"; } } }
    public void Sync() { Text = showHex ? (label.Length > 0 ? label + "  " : "") + Hex : label; Invalidate(); }
    public override Size GetPreferredSize(Size proposed)
    {
        var s = base.GetPreferredSize(proposed);
        return new Size(s.Width + 26 + (string.IsNullOrEmpty(Text) ? 8 : 0), s.Height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        var r = ClientRectangle;
        Web.Box(g, r, 7, hover ? Theme.Panel3 : Theme.Panel2, Theme.Line);
        var chip = new Rectangle(8, (r.Height - 20) / 2, string.IsNullOrEmpty(Text) ? r.Width - 16 : 22, 20);
        Web.Box(g, chip, 4, WebSwatch.ColorOf(Hex), Color.FromArgb(90, 0, 0, 0));
        if (!string.IsNullOrEmpty(Text)) Web.Text(g, Text, Font, new Rectangle(chip.Right + 8, 0, r.Width - chip.Right - 12, r.Height), Enabled ? Theme.Text : Theme.Muted, Web.Left);
    }
}
