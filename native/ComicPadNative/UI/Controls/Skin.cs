// 옛 WinForms 부품(모듈 대화상자 안의 Button·CheckBox·RadioButton·TextBox·ComboBox …)을 웹 모양으로 덧칠.
// 종류(클래스)는 그대로 두고 Paint 위에 다시 그리므로 이벤트·값·시험 코드가 그대로 돈다.
//   Skin.Apply(컨테이너) — 지금 안에 있는 것 + 나중에 더해지는 것까지
using System.Runtime.CompilerServices;

namespace ComicPadNative.UI.Controls;

public static class Skin
{
    sealed class State { public bool Hover, Down; }
    static readonly ConditionalWeakTable<Control, State> done = new();

    static bool IsThemeColor(Color c) =>
        c.A == 0 || c.ToArgb() == Theme.Panel.ToArgb() || c.ToArgb() == Theme.Panel2.ToArgb() || c.ToArgb() == Theme.Panel3.ToArgb() || c.ToArgb() == Theme.Bg.ToArgb()
        || c.ToArgb() == Opt.Bg2.ToArgb() || c.ToArgb() == SystemColors.Control.ToArgb() || c.ToArgb() == SystemColors.ButtonFace.ToArgb() || c.ToArgb() == SystemColors.Window.ToArgb()
        || c.ToArgb() == Color.FromArgb(58, 58, 66).ToArgb() || c.ToArgb() == Color.FromArgb(47, 47, 53).ToArgb() || c == Color.Transparent;
    static bool IsAccent(Color c) => Math.Abs(c.R - Theme.Accent.R) < 6 && Math.Abs(c.G - Theme.Accent.G) < 6 && Math.Abs(c.B - Theme.Accent.B) < 6;

    public static void Apply(Control root)
    {
        if (root == null) return;
        foreach (Control c in root.Controls) One(c);
        if (!done.TryGetValue(root, out _))
        {
            done.Add(root, new State());
            root.ControlAdded += (_, e) => One(e.Control);
        }
    }

    static void One(Control c)
    {
        if (c == null) return;
        bool first = !done.TryGetValue(c, out _);
        if (first)
        {
            done.Add(c, new State());
            c.ControlAdded += (_, e) => One(e.Control);
            Style(c);
        }
        foreach (Control k in c.Controls) One(k);
    }

    static bool DefaultFont(Font f) => f == null || f.Equals(Control.DefaultFont) || f.Name == "Malgun Gothic" && Math.Abs(f.SizeInPoints - 9f) < 0.6f || f.Name == SystemFonts.MessageBoxFont.Name && Math.Abs(f.SizeInPoints - SystemFonts.MessageBoxFont.SizeInPoints) < 0.1f;

    static void Style(Control c)
    {
        // 웹식 부품은 그대로
        if (c is WebButton || c is WebToggle || c is WebSlider || c is WebSeg || c is WebSelect || c is WebInput || c is WebSwatch || c is WebSectionTitle || c is WebList || c is WebSelectRow || c is WebPanelHead || c is Dialogs.ModalHead) return;
        if (c is ScrollableControl || c is ListBox || c is ListView || c is TreeView || c is TextBoxBase || c is ScrollBar || c is DataGridView) c.DarkScroll();
        if (c.BackColor.ToArgb() == SystemColors.Control.ToArgb() && c is not ButtonBase) c.BackColor = Theme.Panel;
        if (c.ForeColor.ToArgb() == SystemColors.ControlText.ToArgb() || c.ForeColor.ToArgb() == SystemColors.WindowText.ToArgb()) c.ForeColor = Theme.Text;
        switch (c)
        {
            case Button b:
                if (DefaultFont(b.Font)) b.Font = Web.Body;
                b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
                b.UseVisualStyleBackColor = false;
                if (b.AutoSize) { b.MinimumSize = new Size(Math.Max(b.MinimumSize.Width, 0), Math.Max(b.MinimumSize.Height, Theme.Large ? 40 : 34)); if (b.Padding.Left < 6) b.Padding = new Padding(6, 0, 6, 0); }
                Track(b);
                b.Paint += PaintButton;
                break;
            case CheckBox cb when cb.Appearance == Appearance.Normal:
                if (DefaultFont(cb.Font)) cb.Font = Web.Body;
                if (cb.AutoSize) { cb.Padding = new Padding(40, 7, 10, 7); if (cb.Margin.Top < 3) cb.Margin = new Padding(cb.Margin.Left, 3, cb.Margin.Right, 3); }
                else if (cb.Height < 30 && cb.Dock == DockStyle.None) cb.Height = 30;
                Track(cb);
                cb.Paint += PaintToggle;
                break;
            case CheckBox cb2:
                if (DefaultFont(cb2.Font)) cb2.Font = Web.Body;
                cb2.FlatStyle = FlatStyle.Flat; cb2.FlatAppearance.BorderSize = 0;
                if (cb2.AutoSize) cb2.MinimumSize = new Size(0, 32);
                Track(cb2);
                cb2.Paint += PaintSegLike;
                break;
            case RadioButton rb when rb.Appearance == Appearance.Button:
                if (DefaultFont(rb.Font)) rb.Font = Web.Body;
                rb.FlatStyle = FlatStyle.Flat; rb.FlatAppearance.BorderSize = 0;
                if (rb.AutoSize) { rb.MinimumSize = new Size(0, 32); if (rb.Padding.Left < 6) rb.Padding = new Padding(6, 0, 6, 0); }
                Track(rb);
                rb.Paint += PaintSegLike;
                break;
            case RadioButton rb2:
                if (DefaultFont(rb2.Font)) rb2.Font = Web.Body;
                Track(rb2);
                rb2.Paint += PaintRadio;
                break;
            case ComboBox cmb:
                cmb.FlatStyle = FlatStyle.Flat; cmb.BackColor = Web.Field; cmb.ForeColor = Theme.Text;
                if (DefaultFont(cmb.Font)) cmb.Font = Web.Body;
                break;
            case TextBoxBase tb:
                tb.BackColor = tb.ReadOnly && tb is TextBox ? Theme.Panel2 : Web.Field; tb.ForeColor = Theme.Text;
                if (tb.BorderStyle == BorderStyle.Fixed3D) tb.BorderStyle = BorderStyle.FixedSingle;
                if (DefaultFont(tb.Font)) tb.Font = Web.Body;
                break;
            case NumericUpDown n:
                n.BackColor = Web.Field; n.ForeColor = Theme.Text; n.BorderStyle = BorderStyle.FixedSingle;
                if (DefaultFont(n.Font)) n.Font = Web.Body;
                break;
            case DomainUpDown du:
                du.BackColor = Web.Field; du.ForeColor = Theme.Text; du.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ListBox lb:
                if (lb.BackColor.ToArgb() == SystemColors.Window.ToArgb()) lb.BackColor = Web.Field;
                lb.ForeColor = lb.ForeColor.ToArgb() == SystemColors.WindowText.ToArgb() ? Theme.Text : lb.ForeColor;
                if (lb.BorderStyle == BorderStyle.Fixed3D) lb.BorderStyle = BorderStyle.None;
                if (DefaultFont(lb.Font)) lb.Font = Web.Body;
                break;
            case ListView lv:
                if (lv.BackColor.ToArgb() == SystemColors.Window.ToArgb()) lv.BackColor = Web.Field;
                if (lv.ForeColor.ToArgb() == SystemColors.WindowText.ToArgb()) lv.ForeColor = Theme.Text;
                if (DefaultFont(lv.Font)) lv.Font = Web.Body;
                break;
            case TreeView tv:
                if (tv.BackColor.ToArgb() == SystemColors.Window.ToArgb()) tv.BackColor = Web.Field;
                if (tv.ForeColor.ToArgb() == SystemColors.WindowText.ToArgb()) tv.ForeColor = Theme.Text;
                tv.LineColor = Theme.Line;
                break;
            case LinkLabel ll:
                ll.LinkColor = Theme.Accent; ll.ActiveLinkColor = Theme.Accent; ll.VisitedLinkColor = Theme.Accent;
                break;
            case Label l:
                if (DefaultFont(l.Font)) l.Font = Web.Body;
                break;
            case TrackBar tr:
                tr.BackColor = Web.ParentBack(tr);
                break;
            case GroupBox gb:
                gb.ForeColor = Theme.Muted;
                break;
            case DataGridView dg:
                dg.BackgroundColor = Web.Field; dg.GridColor = Theme.Line; dg.BorderStyle = BorderStyle.None;
                dg.DefaultCellStyle.BackColor = Web.Field; dg.DefaultCellStyle.ForeColor = Theme.Text; dg.DefaultCellStyle.SelectionBackColor = Theme.AccentDimOnPanel; dg.DefaultCellStyle.SelectionForeColor = Theme.Text;
                dg.ColumnHeadersDefaultCellStyle.BackColor = Theme.Panel2; dg.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text; dg.EnableHeadersVisualStyles = false;
                dg.RowHeadersDefaultCellStyle.BackColor = Theme.Panel2;
                break;
        }
    }

    static void Track(Control c)
    {
        var st = done.GetValue(c, _ => new State());
        c.MouseEnter += (_, _) => { st.Hover = true; c.Invalidate(); };
        c.MouseLeave += (_, _) => { st.Hover = false; st.Down = false; c.Invalidate(); };
        c.MouseDown += (_, _) => { st.Down = true; c.Invalidate(); };
        c.MouseUp += (_, _) => { st.Down = false; c.Invalidate(); };
        c.EnabledChanged += (_, _) => c.Invalidate();
    }
    static State S(Control c) => done.GetValue(c, _ => new State());

    static TextFormatFlags Align(ContentAlignment a, bool multi)
    {
        var f = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | (multi ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        f |= a switch
        {
            ContentAlignment.TopLeft => TextFormatFlags.Left | TextFormatFlags.Top,
            ContentAlignment.TopCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Top,
            ContentAlignment.TopRight => TextFormatFlags.Right | TextFormatFlags.Top,
            ContentAlignment.MiddleLeft => TextFormatFlags.Left | TextFormatFlags.VerticalCenter,
            ContentAlignment.MiddleRight => TextFormatFlags.Right | TextFormatFlags.VerticalCenter,
            ContentAlignment.BottomLeft => TextFormatFlags.Left | TextFormatFlags.Bottom,
            ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom,
            ContentAlignment.BottomRight => TextFormatFlags.Right | TextFormatFlags.Bottom,
            _ => TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter,
        };
        return f;
    }

    /// <summary>.btn (주황 바탕이면 .btn.primary, 확인 단추도 primary, 그 밖의 색이면 색 칸)</summary>
    static void PaintButton(object sender, PaintEventArgs e)
    {
        var b = (Button)sender; var g = e.Graphics; var st = S(b);
        g.Clear(Web.ParentBack(b));
        var r = b.ClientRectangle;
        var form = b.FindForm();
        bool primary = IsAccent(b.BackColor) || form != null && form.AcceptButton == b && IsThemeColor(b.BackColor);
        bool swatch = !primary && !IsThemeColor(b.BackColor);
        Color fill, border, fg;
        if (primary) { fill = st.Hover ? Web.Mix(Theme.Accent, Color.White, 0.08) : Theme.Accent; border = fill; fg = Web.OnDark; }
        else if (swatch) { fill = b.BackColor; border = st.Hover ? Theme.Accent : Theme.Line; fg = b.ForeColor; }
        else { fill = st.Hover || st.Down ? Theme.Panel3 : Theme.Panel2; border = Theme.Line; fg = b.ForeColor.ToArgb() == Color.Black.ToArgb() ? Theme.Text : b.ForeColor; }
        if (!b.Enabled) { fg = Color.FromArgb(100, fg); fill = Color.FromArgb(150, fill); }
        Web.Box(g, r, 7, fill, border);
        var font = primary && b.Font.Equals(Web.Body) ? Web.BodyBold : b.Font;
        var inner = Rectangle.Inflate(r, -Math.Max(8, b.Padding.Left), -3);
        int iw = 0;
        if (b.Image != null)
        {
            var im = b.Image;
            if (string.IsNullOrEmpty(b.Text)) { g.DrawImage(im, r.X + (r.Width - im.Width) / 2, r.Y + (r.Height - im.Height) / 2, im.Width, im.Height); return; }
            g.DrawImage(im, inner.X, r.Y + (r.Height - im.Height) / 2, im.Width, im.Height);
            iw = im.Width + 6;
        }
        inner = new Rectangle(inner.X + iw, inner.Y, inner.Width - iw, inner.Height);
        bool multi = b.Text.Contains('\n') || !b.AutoSize && Web.Measure(b.Text, font).Width > inner.Width;
        TextRenderer.DrawText(g, b.Text, font, inner, fg, Align(b.TextAlign, multi));
        if (b.Focused) Web.Box(g, Rectangle.Inflate(r, -1, -1), 7, null, Color.FromArgb(150, Theme.Accent));
    }

    /// <summary>.toggle (스위치 칩)</summary>
    static void PaintToggle(object sender, PaintEventArgs e)
    {
        var cb = (CheckBox)sender; var g = e.Graphics; var st = S(cb);
        g.Clear(Web.ParentBack(cb));
        var r = cb.ClientRectangle;
        Web.Box(g, r, 7, st.Hover ? Web.Mix(Theme.Panel2, Theme.Panel3, 0.5) : Theme.Panel2);
        var bx = new Rectangle(6, r.Height >= 26 ? (r.Height - 18) / 2 : 4, 30, 18);
        if (r.Height > 46) bx.Y = 8;
        Web.Box(g, bx, 9, cb.Checked ? Theme.Accent : Web.ToggleOff);
        Web.Smooth(g);
        using (var k = new SolidBrush(cb.Checked ? Color.White : Web.KnobOff)) g.FillEllipse(k, bx.X + (cb.Checked ? 14 : 2), bx.Y + 2, 14, 14);
        var tr = new Rectangle(bx.Right + 8, 0, Math.Max(1, r.Width - bx.Right - 8 - 6), r.Height);
        var fg = cb.Enabled ? (cb.ForeColor.ToArgb() == Color.Black.ToArgb() ? Theme.Text : cb.ForeColor) : Theme.Muted;
        var one = Web.Measure(cb.Text, cb.Font);
        if (one.Width <= tr.Width) Web.Text(g, cb.Text, cb.Font, tr, fg, Web.Left);
        else { var ws = Web.MeasureWrap(cb.Text, cb.Font, tr.Width); Web.Text(g, cb.Text, cb.Font, new Rectangle(tr.X, Math.Max(2, (r.Height - ws.Height) / 2), tr.Width, ws.Height + 2), fg, Web.Wrap); }
    }

    /// <summary>세그먼트 단추처럼 (Appearance.Button 인 라디오·체크)</summary>
    static void PaintSegLike(object sender, PaintEventArgs e)
    {
        var c = (ButtonBase)sender; var g = e.Graphics; var st = S(c);
        bool on = c is RadioButton rb ? rb.Checked : c is CheckBox cb && cb.Checked;
        var back = Web.ParentBack(c);
        g.Clear(back);
        var r = c.ClientRectangle;
        Web.Box(g, r, 6, on ? Theme.Panel3 : st.Hover ? Web.Mix(Web.SegBg, Theme.Panel3, 0.5) : Web.SegBg);
        if (on)
        {
            var s = g.Save();
            using (var clip = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), 6)) g.SetClip(clip);
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X, r.Bottom - 2, r.Width, 2);
            g.Restore(s);
        }
        Web.Text(g, c.Text, c.Font, new Rectangle(4, 0, r.Width - 8, r.Height), on ? Theme.Text : Theme.Muted, Web.Center);
    }

    static void PaintRadio(object sender, PaintEventArgs e)
    {
        var rb = (RadioButton)sender; var g = e.Graphics; var st = S(rb);
        g.Clear(Web.ParentBack(rb));
        int y = (rb.Height - 16) / 2;
        Web.Smooth(g);
        using (var p = new Pen(rb.Checked ? Theme.Accent : st.Hover ? Theme.Text : Theme.Line, 1.5f)) g.DrawEllipse(p, 1, y, 15, 15);
        if (rb.Checked) using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, 5, y + 4, 8, 8);
        Web.Text(g, rb.Text, rb.Font, new Rectangle(22, 0, rb.Width - 22, rb.Height), rb.Enabled ? Theme.Text : Theme.Muted, Web.Left);
    }
}
