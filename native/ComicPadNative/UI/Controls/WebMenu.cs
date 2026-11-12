// 웹 .menu 모양의 펼침 메뉴 (오른쪽 클릭 메뉴 등): 바탕 #34343b, 테두리 #55555f 둥근 10px, 칸 높이 38, 올리면 주황 + 어두운 글자, 단축키는 흐린 11px.
//   WebMenu.Style(contextMenu) 로 꾸미기
namespace ComicPadNative.UI.Controls;

public static class WebMenu
{
    public static readonly ToolStripRenderer Renderer = new WebMenuRenderer();

    public static T Style<T>(T m) where T : ToolStripDropDown
    {
        m.Renderer = Renderer;
        m.BackColor = Web.MenuBg; m.ForeColor = Theme.Text; m.Font = Web.Body;
        m.Padding = new Padding(5);
        foreach (ToolStripItem it in m.Items) StyleItem(it);
        m.ItemAdded += (_, e) => StyleItem(e.Item);
        m.Opening += (_, _) => { foreach (ToolStripItem it in m.Items) StyleItem(it); };
        return m;
    }

    static void StyleItem(ToolStripItem it)
    {
        it.ForeColor = Theme.Text; it.Font = Web.Body;
        if (it is ToolStripMenuItem mi)
        {
            mi.AutoSize = true;
            mi.Padding = new Padding(4, 0, 4, 0);
            if (mi.Height < 34) mi.Height = 34;
            if (mi.HasDropDownItems && mi.DropDown is ToolStripDropDownMenu dd && dd.Renderer != Renderer) Style(dd);
            mi.DropDownOpening += (_, _) => { if (mi.DropDown.Renderer != Renderer) Style(mi.DropDown); };
        }
    }

    sealed class WebMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Web.MenuBg);
            e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is not ToolStripDropDown) return;
            using var p = new Pen(Web.ModalLine);
            e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var it = e.Item;
            if (!(it.Selected || it.Pressed) || !it.Enabled) return;
            var r = new Rectangle(2, 1, it.Width - 4, it.Height - 2);
            Web.Box(e.Graphics, r, 6, Theme.Accent);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var it = e.Item;
            bool hot = (it.Selected || it.Pressed) && it.Enabled;
            Color c = !it.Enabled ? Color.FromArgb(110, Theme.Text) : hot ? Web.OnDark : Theme.Text;
            if (e.Text != null && it is ToolStripMenuItem && e.TextRectangle.X > it.Width / 2)
            {
                // 단축키 글자
                TextRenderer.DrawText(e.Graphics, e.Text, Web.Tiny, e.TextRectangle, hot ? Color.FromArgb(0x33, 0x33, 0x33) : Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }
            e.TextColor = c;
            e.TextFont = Web.Body;
            e.TextFormat |= TextFormatFlags.NoPrefix;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Theme.Line);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 6, y, e.Item.Width - 6, y);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item != null && e.Item.Selected ? Web.OnDark : Theme.Muted;
            base.OnRenderArrow(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            Icons.Draw(e.Graphics, "check", new RectangleF(r.X, r.Y, r.Width, r.Height), e.Item.Selected ? Web.OnDark : Theme.Accent, 2.2f);
        }
    }
}
