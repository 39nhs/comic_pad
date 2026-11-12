// 브러시 프리셋 목록 (웹 .preset-list: 2열 격자, 칸마다 이름 + 한 획 미리보기 96×26, 고른 칸은 주황 테두리 + #4a4030)
using ComicPadNative.Core;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.Modules.BrushEngine;

public sealed class PresetGrid : Control, IWebBlock
{
    readonly Func<List<BrushPreset>> list;
    readonly Func<string> activeId;
    readonly Func<BrushPreset, uint> colorOf;
    public event Action<BrushPreset> Picked;
    public event Action<BrushPreset, Point> Menu;
    const int RowH = 36, Gap = 4;
    int hot = -1;

    public PresetGrid(Func<List<BrushPreset>> list, Func<string> activeId, Func<BrushPreset, uint> colorOf)
    {
        this.list = list; this.activeId = activeId; this.colorOf = colorOf;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Web.Body; Margin = Padding.Empty; Cursor = Cursors.Hand;
    }

    int Count => list().Count;
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 280;
        int rows = (Count + 1) / 2;
        return new Size(w, Math.Max(RowH, rows * RowH + Math.Max(0, rows - 1) * Gap));
    }
    Rectangle Cell(int i)
    {
        int cw = (Width - Gap) / 2;
        return new Rectangle(i % 2 * (cw + Gap), i / 2 * (RowH + Gap), cw, RowH);
    }
    int Hit(Point p) { for (int i = 0; i < Count; i++) if (Cell(i).Contains(p)) return i; return -1; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        var l = list(); string act = activeId();
        for (int i = 0; i < l.Count; i++)
        {
            var r = Cell(i); var p = l[i];
            if (!e.ClipRectangle.IntersectsWith(r)) continue;
            bool on = p.Id == act;
            Web.Box(g, r, 7, on ? Web.ActiveRow : i == hot ? Web.Mix(Theme.Panel2, Theme.Panel3, 0.5) : Theme.Panel2, on ? Theme.Accent : null);
            var st = g.Save();
            using (var clip = Theme.Round(new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), 6)) g.SetClip(clip);
            int nw = Web.Measure(p.Name, Font).Width;
            Web.Text(g, p.Name, Font, new Rectangle(r.X + 8, r.Y, Math.Max(10, r.Width - 16), r.Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            // 이름이 길면 미리보기가 뒤로 밀려 잘림 (웹 justify-content: space-between + overflow hidden)
            int tx = Math.Max(r.X + 8 + nw + 6, r.Right - 8 - 96);
            var th = BrushPreview.Thumb(p, colorOf(p), 96, 26);
            var tr = new RectangleF(tx, r.Y + (RowH - 26) / 2f, 96, 26);
            using (var tp = Theme.Round(tr, 4)) { Web.Smooth(g); using var wb = new SolidBrush(Color.White); g.FillPath(wb, tp); g.SetClip(tp, System.Drawing.Drawing2D.CombineMode.Intersect); }
            g.DrawImageUnscaled(th, (int)tr.X, (int)tr.Y);
            g.Restore(st);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != hot) { hot = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int h = Hit(e.Location); if (h < 0) return;
        var p = list()[h];
        if (e.Button == MouseButtons.Left) Picked?.Invoke(p);
        else if (e.Button == MouseButtons.Right) Menu?.Invoke(p, e.Location);
    }
}

/// <summary>러프 색 고르기 (웹 .rough-colors: 칸마다 색 동그라미 26 + 이름 11px, 고른 칸은 주황 테두리)</summary>
public sealed class RoughColorGrid : Control, IWebBlock
{
    readonly (string hex, string name)[] colors;
    readonly Func<string> current;
    public event Action<string> Picked;
    int hot = -1;
    const int MinW = 62, CellH = 50, Gap = 4;
    public RoughColorGrid((string hex, string name)[] colors, Func<string> current)
    {
        this.colors = colors; this.current = current;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = Web.Tiny; Margin = new Padding(0, 4, 0, 4); Cursor = Cursors.Hand;
    }
    int Cols(int w) => Math.Max(1, (w + Gap) / (MinW + Gap));
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 280;
        int rows = (colors.Length + Cols(w) - 1) / Cols(w);
        return new Size(w, rows * CellH + Math.Max(0, rows - 1) * Gap);
    }
    Rectangle Cell(int i) { int n = Cols(Width), cw = (Width - (n - 1) * Gap) / n; return new Rectangle(i % n * (cw + Gap), i / n * (CellH + Gap), cw, CellH); }
    int Hit(Point p) { for (int i = 0; i < colors.Length; i++) if (Cell(i).Contains(p)) return i; return -1; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        string cur = current();
        for (int i = 0; i < colors.Length; i++)
        {
            var r = Cell(i); bool on = colors[i].hex == cur;
            Web.Box(g, r, 8, i == hot ? Web.Mix(Theme.Panel2, Theme.Panel3, 0.5) : Theme.Panel2, on ? Theme.Accent : null, 2);
            var c = new Rectangle(r.X + (r.Width - 26) / 2, r.Y + 5, 26, 26);
            Web.Smooth(g);
            using (var ring = new SolidBrush(Color.FromArgb(51, 255, 255, 255))) g.FillEllipse(ring, Rectangle.Inflate(c, 2, 2));
            using (var b = new SolidBrush(WebSwatch.ColorOf(colors[i].hex))) g.FillEllipse(b, c);
            Web.Text(g, colors[i].name, Font, new Rectangle(r.X, c.Bottom + 1, r.Width, r.Bottom - c.Bottom - 3), Theme.Text, Web.Center);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != hot) { hot = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); int h = Hit(e.Location); if (h >= 0 && e.Button == MouseButtons.Left) Picked?.Invoke(colors[h].hex); }
}
