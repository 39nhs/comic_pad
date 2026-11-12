// 메뉴 모양 (웹 판 #menubar button · .menu · .menu-item · .menu-sep · kbd). 위 메뉴 막대·펼침 메뉴·도구 "더 보기" 팝업이 같이 씀.
//  - 메뉴 막대 단추: 높이 34, 좌우 12px, 둥근 6px, 올리거나 열면 --panel3
//  - 펼침 메뉴: #34343b 바탕, #55555f 테두리, 둥근 10px, 안쪽 5px, 최소 폭 250 · 항목 높이 38, 좌우 12px, 올리면 주황 바탕에 #1d1d1d 글자
//  - 단축키: 오른쪽에 kbd 칩 (11px Consolas, #222 바탕, #555 테두리)
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ComicPadNative.UI.Chrome;

/// <summary>펼침 메뉴 항목에 붙이는 정보 (아이콘·단축키 글자·"모든 항목 보기")</summary>
public sealed class MenuTag
{
    public string Key;          // 메뉴 키 (웹 id 등)
    public string Kbd = "";     // 오른쪽 단축키 글자
    public string Icon;         // 왼쪽 아이콘 (도구 더 보기 팝업)
    public bool More;           // "모든 항목 보기 (N개 더)"
}

public sealed class MenuLook : ToolStripRenderer
{
    public static readonly MenuLook Instance = new();
    public static readonly Color MenuBg = ColorTranslator.FromHtml("#34343b"), MenuBorder = ColorTranslator.FromHtml("#55555f"), SepColor = ColorTranslator.FromHtml("#4a4a54");
    public static readonly Color KbdBg = ColorTranslator.FromHtml("#222222"), KbdBorder = ColorTranslator.FromHtml("#555555"), MoreColor = ColorTranslator.FromHtml("#99aabb"), HotKbd = ColorTranslator.FromHtml("#333333");
    public const int ItemH = 38, Pad = 5, MinW = 250, Radius = 10;
    static Font kbdFont;
    public static Font KbdFont => kbdFont ??= new Font("Consolas", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

    static void Smooth(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; }

    // ---- 바탕·테두리 ----
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        if (e.ToolStrip is ToolStripDropDown)
        {
            g.Clear(MenuBg);
            return;
        }
        using var b = new SolidBrush(e.ToolStrip.Parent?.BackColor ?? Theme.Panel);
        g.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown dd) return;
        var g = e.Graphics; Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, dd.Width - 1f, dd.Height - 1f);
        using var path = Theme.Round(r, Radius);
        using var p = new Pen(MenuBorder);
        g.DrawPath(p, path);
    }

    /// <summary>펼침 메뉴 창을 둥글게 (크기가 바뀔 때마다)</summary>
    public static void RoundWindow(ToolStripDropDown dd)
    {
        void Apply()
        {
            if (dd.Width <= 0 || dd.Height <= 0) return;
            using var path = Theme.Round(new RectangleF(0, 0, dd.Width + 0.5f, dd.Height + 0.5f), Radius);
            var old = dd.Region; dd.Region = new Region(path); old?.Dispose();
        }
        dd.SizeChanged += (_, _) => Apply();
        dd.Opening += (_, _) => Apply();
        Apply();
    }

    // ---- 항목 바탕 ----
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics; Smooth(g);
        var it = e.Item;
        var r = new Rectangle(Point.Empty, it.Size);
        if (!it.IsOnDropDown)
        {
            // 위 메뉴 막대 단추
            bool on = it.Selected || it.Pressed || (it is ToolStripMenuItem mi && mi.DropDown.Visible);
            if (on) { using var b = new SolidBrush(Theme.Panel3); using var p = Theme.Round(r, 6); g.FillPath(b, p); }
            return;
        }
        if (it.Selected && it.Enabled)
        {
            using var b = new SolidBrush(Theme.Accent); using var p = Theme.Round(r, 6); g.FillPath(b, p);
        }
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed) return;
        var g = e.Graphics; Smooth(g);
        using var b = new SolidBrush(Theme.Panel3); using var p = Theme.Round(new Rectangle(Point.Empty, e.Item.Size), 6); g.FillPath(b, p);
    }

    // ---- 글자 (이름 + 오른쪽 단축키 칩) ----
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var g = e.Graphics; Smooth(g);
        var it = e.Item;
        if (!it.IsOnDropDown)
        {
            if (e.Text != it.Text) return;
            TextRenderer.DrawText(g, it.Text, Theme.Font, new Rectangle(Point.Empty, it.Size), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            return;
        }
        if (it is ToolStripMenuItem mi && e.Text != it.Text) return;   // 기본 단축키 글자는 그리지 않음 (아래에서 칩으로)
        var tag = it.Tag as MenuTag;
        bool hot = it.Selected && it.Enabled;
        Color fg = !it.Enabled ? Theme.Blend(MenuBg, Theme.Text, 0.4) : hot ? Theme.OnAccent : tag?.More == true ? MoreColor : Theme.Text;
        int x = 12;
        if (tag?.Icon != null)
        {
            Icons.Draw(g, tag.Icon, new RectangleF(x, (it.Height - 18) / 2f, 18, 18), fg);
            x += 18 + 6;
        }
        var bounds = new Rectangle(x, 0, it.Width - x - 12, it.Height);
        // 오른쪽: 단축키 칩 · 체크 · 하위 메뉴 화살표
        int right = it.Width - 12;
        if (it is ToolStripMenuItem m2 && m2.HasDropDownItems) right -= 14;
        string kbd = tag?.Kbd ?? "";
        if (kbd.Length > 0)
        {
            var ks = TextRenderer.MeasureText(g, kbd, KbdFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            var kr = new Rectangle(right - ks.Width - 12, (it.Height - 18) / 2, ks.Width + 12, 18);
            using (var kb = new SolidBrush(KbdBg)) using (var kp = Theme.Round(kr, 4)) g.FillPath(kb, kp);
            using (var kpen = new Pen(KbdBorder)) using (var kp = Theme.Round(new RectangleF(kr.X + 0.5f, kr.Y + 0.5f, kr.Width - 1, kr.Height - 1), 4)) g.DrawPath(kpen, kp);
            TextRenderer.DrawText(g, kbd, KbdFont, kr, hot ? HotKbd : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            right = kr.X - 8;
        }
        if (it is ToolStripMenuItem m3 && m3.Checked)
        {
            Icons.Draw(g, "check", new RectangleF(right - 16, (it.Height - 16) / 2f, 16, 16), hot ? Theme.OnAccent : Theme.Accent, 2.2f);
            right -= 22;
        }
        bounds.Width = Math.Max(10, right - x);
        TextRenderer.DrawText(g, it.Text, Theme.Font, bounds, fg, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if (e.Item == null || !e.Item.IsOnDropDown) return;
        var g = e.Graphics; Smooth(g);
        var it = e.Item;
        float cx = it.Width - 16, cy = it.Height / 2f;
        using var p = new Pen(it.Selected ? Theme.OnAccent : Theme.Muted, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(p, new[] { new PointF(cx - 2, cy - 4.5f), new PointF(cx + 2.5f, cy), new PointF(cx - 2, cy + 4.5f) });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var b = new SolidBrush(SepColor);
        e.Graphics.FillRectangle(b, 6, e.Item.Height / 2, e.Item.Width - 12, 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

    // ---- 펼침 메뉴 만들기 도우미 ----
    /// <summary>펼침 메뉴 모양 맞추기 (여백·둥근 창·그림자)</summary>
    public static void Style(ToolStripDropDown dd)
    {
        dd.Renderer = Instance;
        dd.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
        dd.AutoSize = true;
        dd.Padding = new Padding(Pad + 1, Pad + 1, Pad + 1, Pad + 1);   // 웹: 안쪽 5px + 테두리 1px
        dd.BackColor = MenuBg; dd.ForeColor = Theme.Text;
        dd.Font = Theme.Font;
        dd.DropShadowEnabled = true;
        dd.ShowItemToolTips = true;
        if (dd.Tag as string != "cp-round") { dd.Tag = "cp-round"; RoundWindow(dd); }
    }

    /// <summary>웹 .menu-item 크기의 항목 (글자 폭 + 단축키 칩 폭 + 여백, 최소 250)</summary>
    public static ToolStripMenuItem Item(string text, MenuTag tag, EventHandler click = null)
    {
        var mi = new ToolStripMenuItem(text) { Tag = tag ?? new MenuTag(), AutoSize = false, Font = Theme.Font, ForeColor = Theme.Text };
        if (click != null) mi.Click += click;
        Fit(mi);
        return mi;
    }

    public static void Fit(ToolStripMenuItem mi)
    {
        var tag = mi.Tag as MenuTag ?? new MenuTag();
        int w = 12 + TextRenderer.MeasureText(mi.Text, Theme.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + 4 + 12;
        if (tag.Icon != null) w += 24;
        if (tag.Kbd.Length > 0) w += 20 + TextRenderer.MeasureText(tag.Kbd, KbdFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + 12;
        if (mi.Checked || mi.CheckOnClick) w += 22;
        if (mi.HasDropDownItems) w += 14;
        mi.Size = new Size(Math.Max(MinW - 2 * Pad, w), ItemH);
    }

    public static ToolStripSeparator Sep() => new() { AutoSize = false, Height = 9 };

    /// <summary>한 펼침 메뉴의 항목 폭을 가장 넓은 것에 맞춤 (단축키 칩이 오른쪽 끝에 줄 맞게)</summary>
    public static void Equalize(ToolStripDropDown dd)
    {
        int w = MinW - 2 * Pad;
        foreach (ToolStripItem it in dd.Items) if (it is ToolStripMenuItem) w = Math.Max(w, it.Width);
        foreach (ToolStripItem it in dd.Items) { it.Width = w; it.Margin = Padding.Empty; }
        dd.PerformLayout();
    }

    /// <summary>웹 .menu 모양의 펼침 창 (항목은 세로로 쌓고 폭은 Equalize 로 맞춤)</summary>
    public static ToolStripDropDown NewDropDown() { var dd = new ToolStripDropDown(); Style(dd); return dd; }
}
