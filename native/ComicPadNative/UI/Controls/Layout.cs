// 웹식 배치: 세로 쌓기(WebStack, CSS 블록 흐름 — 막대·고르기·구역은 폭을 꽉, 단추·켜기는 옆으로 이어 놓고 넘치면 다음 줄)
// 와 줄(WebRow, 웹 .row = flex-wrap, gap 6px, 세로 가운데). 둘 다 FlowLayoutPanel 을 이어받아 기존 코드(Controls.Add 등)가 그대로 돈다.
using System.Windows.Forms.Layout;

namespace ComicPadNative.UI.Controls;

/// <summary>둥근 판 (웹 .setup-detail · .local-row: panel2 바탕, 둥근 10px) — 세로 쌓기</summary>
public class WebBox : WebStack
{
    public Color Fill = Theme.Panel2;
    public int Radius = 10;
    public WebBox() { FillParent = false; BackColor = Theme.Panel2; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Web.ParentBack(this));
        Web.Box(e.Graphics, ClientRectangle, Radius, Fill);
    }
}

/// <summary>세로 쌓기 (웹 div/section 의 블록 흐름)</summary>
public class WebStack : FlowLayoutPanel
{
    /// <summary>부모 폭을 꽉 채움 (부모가 일반 패널일 때: 도구 옵션 칸 등)</summary>
    public bool FillParent = true;
    Control hooked;

    public WebStack()
    {
        BackColor = Theme.Panel; ForeColor = Theme.Text;
        Margin = new Padding(0); Padding = new Padding(0);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.TopDown; WrapContents = false;
        DoubleBuffered = true;
        this.DarkScroll();
    }

    public override LayoutEngine LayoutEngine => BlockLayout.Instance;

    bool ParentIsLayout => Parent is WebStack || Parent is WebRow || Parent is FlowLayoutPanel || Parent is TableLayoutPanel;

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (hooked != null) { hooked.ClientSizeChanged -= ParentResized; hooked = null; }
        if (Parent != null && !ParentIsLayout) { hooked = Parent; hooked.ClientSizeChanged += ParentResized; }
    }
    void ParentResized(object s, EventArgs e) { if (Dock == DockStyle.None && FillParent) { Parent?.PerformLayout(this, "Bounds"); PerformLayout(); } }

    protected override void OnDockChanged(EventArgs e)
    {
        base.OnDockChanged(e);
        if (Dock == DockStyle.Fill) AutoScroll = true;
    }

    int FillWidth() => Parent == null ? 0 : Math.Max(40, Parent.ClientSize.Width - Left * 2 - Margin.Horizontal + (Parent is ScrollableControl sc && sc.AutoScroll ? 0 : 0));

    public override Size GetPreferredSize(Size proposed)
    {
        int w;
        if (Dock == DockStyle.None && FillParent && Parent != null && !ParentIsLayout) w = FillWidth();
        else if (proposed.Width > 1 && proposed.Width < 100000) w = proposed.Width;
        else w = BlockLayout.NaturalWidth(this);
        if (MaximumSize.Width > 0) w = Math.Min(w, MaximumSize.Width);
        int h = BlockLayout.Measure(this, w - Padding.Horizontal) + Padding.Vertical;
        return new Size(Math.Max(w, MinimumSize.Width), Math.Max(h, MinimumSize.Height));
    }

    /// <summary>모든 부품 값 다시 읽기</summary>
    public void SyncAll() => SyncTree(this);
    public static void SyncTree(Control root) { foreach (Control c in root.Controls) { if (c is ISyncable s) s.Sync(); SyncTree(c); } }
}

/// <summary>줄 (웹 .row: 옆으로, 넘치면 다음 줄, 간격 6px, 세로 가운데)</summary>
public class WebRow : FlowLayoutPanel
{
    public int Gap = 6;
    public bool CenterY = true;
    public WebRow()
    {
        BackColor = Color.Transparent; ForeColor = Theme.Text;
        Margin = new Padding(0, 4, 0, 4); Padding = new Padding(0);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.LeftToRight; WrapContents = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
    }
    public override LayoutEngine LayoutEngine => RowLayout.Instance;
    /// <summary>줄 안의 단추는 간격(gap)만으로 떨어짐 (웹 .row .btn 은 margin 없음)</summary>
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is WebButton b && b.Margin == new Padding(0, 4, 6, 4)) b.Margin = Padding.Empty;
    }
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : int.MaxValue / 4;
        if (MaximumSize.Width > 0) w = Math.Min(w, MaximumSize.Width);
        var (used, h) = RowLayout.Arrange(this, w - Padding.Horizontal, false);
        int outW = proposed.Width > 1 && proposed.Width < 100000 ? Math.Min(w, used + Padding.Horizontal) : used + Padding.Horizontal;
        return new Size(outW, h + Padding.Vertical);
    }
}

/// <summary>세로 쌓기 배치 엔진</summary>
public sealed class BlockLayout : LayoutEngine
{
    public static readonly BlockLayout Instance = new();

    static bool Inline(Control c)
    {
        var v = Web.IsInline(c);
        if (v.HasValue) return v.Value;
        return c is ButtonBase && c is not WebSegButton || c is WebSwatch;
    }
    static bool StretchOf(Control c)
    {
        var v = Web.IsStretch(c);
        if (v.HasValue) return v.Value;
        if (c is WebStack || c is WebRow || c is IWebBlock) return true;
        if (c is Label) return true;
        if (c is ComboBox || c is TextBoxBase && ((TextBoxBase)c).Multiline) return true;
        return false;
    }

    /// <summary>폭 w 일 때 자식 크기</summary>
    internal static Size ChildSize(Control c, int w)
    {
        if (c is WebStack || c is WebRow || c is IWebBlock || Web.IsStretch(c) == true)
        {
            bool st = StretchOf(c);
            if (st)
            {
                int ww = c.MaximumSize.Width > 0 ? Math.Min(w, c.MaximumSize.Width) : w;
                var p = c.GetPreferredSize(new Size(ww, 0));
                return new Size(ww, p.Height);
            }
        }
        if (c is Label l)
        {
            if (!l.AutoSize) return new Size(StretchOf(c) ? w : Math.Min(w, l.Width), l.Height);
            int mw = l.MaximumSize.Width > 0 ? Math.Min(w, l.MaximumSize.Width) : w;
            var sz = Web.MeasureWrap(l.Text, l.Font, Math.Max(1, mw - l.Padding.Horizontal));
            return new Size(mw, sz.Height + l.Padding.Vertical);   // 블록: 폭 꽉 (글자가 잘리지 않게)
        }
        if (c.AutoSize)
        {
            var p = c.GetPreferredSize(new Size(w, 0));
            if (c.MaximumSize.Width > 0) p.Width = Math.Min(p.Width, c.MaximumSize.Width);
            return new Size(Math.Min(w, p.Width), p.Height);
        }
        if (StretchOf(c)) return new Size(w, c.Height);
        return new Size(Math.Min(w, c.Width), c.Height);
    }

    internal static int NaturalWidth(Control p)
    {
        int w = 0;
        foreach (Control c in p.Controls)
        {
            if (!Shown(c) || c.Dock != DockStyle.None) continue;
            int cw = c.AutoSize || c is WebStack || c is WebRow ? c.GetPreferredSize(Size.Empty).Width : c.Width;
            if (c is Label lb && lb.AutoSize) cw = Math.Min(lb.MaximumSize.Width > 0 ? lb.MaximumSize.Width : 280, Web.Measure(lb.Text, lb.Font).Width + 1);
            w = Math.Max(w, cw + c.Margin.Horizontal);
        }
        return Math.Max(w, 40) + p.Padding.Horizontal;
    }

    /// <summary>폭 w 안에 놓았을 때 높이</summary>
    internal static int Measure(Control p, int w) => Arrange(p, w, false, Point.Empty);

    public override bool Layout(object container, LayoutEventArgs e)
    {
        var p = (Control)container;
        var r = p.DisplayRectangle;
        int w = r.Width;
        Arrange(p, w, true, r.Location);
        return p.AutoSize;
    }

    /// <summary>자기 Visible 값 (부모가 아직 안 보여도) — WinForms 배치 엔진과 같은 기준</summary>
    static readonly System.Reflection.PropertyInfo partProp = typeof(Control).GetInterfaces().FirstOrDefault(i => i.Name == "IArrangedElement")?.GetProperty("ParticipatesInLayout");
    internal static bool Shown(Control c)
    {
        try { if (partProp != null) return (bool)partProp.GetValue(c); } catch { }
        return c.Visible || c.Parent?.Visible != true;
    }

    static int Arrange(Control p, int w, bool apply, Point origin)
    {
        int y = 0, lineX = 0, lineH = 0, lineTop = 0;
        int prevBottomMargin = 0;
        var line = new List<(Control c, Size s)>();
        var xs = new Dictionary<Control, int>();
        void FlushLine()
        {
            if (line.Count == 0) return;
            if (apply)
                foreach (var (c, s) in line)
                {
                    int cy = lineTop + (lineH - s.Height - c.Margin.Vertical) / 2 + c.Margin.Top;
                    c.SetBounds(origin.X + xs[c], origin.Y + cy, s.Width, s.Height);
                }
            y = lineTop + lineH; prevBottomMargin = 0;
            line.Clear(); lineX = 0; lineH = 0;
        }
        foreach (Control c in p.Controls)
        {
            if (!Shown(c) || c.Dock != DockStyle.None) continue;
            var m = c.Margin;
            if (Inline(c))
            {
                var s = ChildSize(c, Math.Max(1, w - m.Horizontal));
                int need = s.Width + m.Horizontal;
                if (line.Count > 0 && lineX + need > w) FlushLine();
                if (line.Count == 0) { lineTop = y; }
                xs[c] = lineX + m.Left;
                line.Add((c, s));
                lineX += need;
                lineH = Math.Max(lineH, s.Height + m.Vertical);
            }
            else
            {
                FlushLine();
                var s = ChildSize(c, Math.Max(1, w - m.Horizontal));
                // 위아래 바깥 여백은 겹침 (CSS margin collapse)
                int top = Math.Max(0, m.Top - Math.Min(m.Top, prevBottomMargin));
                y += top;
                if (apply) c.SetBounds(origin.X + m.Left, origin.Y + y, s.Width, s.Height);
                y += s.Height + m.Bottom;
                prevBottomMargin = m.Bottom;
            }
        }
        FlushLine();
        return y;
    }
}

/// <summary>줄 배치 엔진 (flex-wrap)</summary>
public sealed class RowLayout : LayoutEngine
{
    public static readonly RowLayout Instance = new();

    public override bool Layout(object container, LayoutEventArgs e)
    {
        var p = (Control)container;
        var r = p.DisplayRectangle;
        Arrange(p, r.Width, true, r.Location);
        return p.AutoSize;
    }

    internal static (int usedW, int h) Arrange(Control p, int w, bool apply, Point origin = default)
    {
        int gap = p is WebRow wr ? wr.Gap : 6;
        bool center = p is not WebRow r2 || r2.CenterY;
        var kids = p.Controls.Cast<Control>().Where(c => BlockLayout.Shown(c) && c.Dock == DockStyle.None).ToList();
        int y = 0, maxW = 0;
        int i = 0;
        while (i < kids.Count)
        {
            // 한 줄에 들어갈 만큼
            var line = new List<(Control c, Size s)>();
            int x = 0;
            while (i < kids.Count)
            {
                var c = kids[i];
                var s = Natural(c, w);
                int need = s.Width + c.Margin.Horizontal + (line.Count > 0 ? gap : 0);
                if (line.Count > 0 && x + need > w) break;
                line.Add((c, s)); x += need; i++;
            }
            // flex: 남은 폭 나누기
            int free = w - x;
            var flex = line.Where(l => Web.IsFlex(l.c)).ToList();
            if (free > 0 && flex.Count > 0 && w < int.MaxValue / 8)
            {
                int each = free / flex.Count;
                for (int k = 0; k < line.Count; k++) if (Web.IsFlex(line[k].c)) line[k] = (line[k].c, new Size(line[k].s.Width + each, line[k].s.Height));
                x = w;
            }
            // flex 부품은 높이가 폭에 따라 바뀔 수 있음
            for (int k = 0; k < line.Count; k++)
                if (Web.IsFlex(line[k].c) && (line[k].c is WebStack || line[k].c is IWebBlock)) line[k] = (line[k].c, new Size(line[k].s.Width, line[k].c.GetPreferredSize(new Size(line[k].s.Width, 0)).Height));
                else if (Web.IsFlex(line[k].c) && line[k].c is Label fl2) line[k] = (fl2, new Size(line[k].s.Width, Web.MeasureWrap(fl2.Text, fl2.Font, Math.Max(10, line[k].s.Width - fl2.Padding.Horizontal)).Height + fl2.Padding.Vertical));
            int lh = line.Count == 0 ? 0 : line.Max(l => l.s.Height + l.c.Margin.Vertical);
            if (apply)
            {
                int cx = 0;
                foreach (var (c, s) in line)
                {
                    int cy = center ? (lh - s.Height - c.Margin.Vertical) / 2 + c.Margin.Top : c.Margin.Top;
                    c.SetBounds(origin.X + cx + c.Margin.Left, origin.Y + y + cy, s.Width, s.Height);
                    cx += s.Width + c.Margin.Horizontal + gap;
                }
            }
            maxW = Math.Max(maxW, Math.Min(x, w));
            y += lh + (i < kids.Count ? gap : 0);
        }
        return (maxW, y);
    }

    static Size Natural(Control c, int w)
    {
        if (Web.IsFlex(c) && c is Label fl) return new Size(Math.Max(60, fl.MinimumSize.Width), fl.Font.Height + fl.Padding.Vertical);
        if (c is Label l && l.AutoSize)
        {
            var sz = Web.Measure(l.Text, l.Font);
            int mw = l.MaximumSize.Width > 0 ? l.MaximumSize.Width : w;
            if (sz.Width + l.Padding.Horizontal > mw) { var s2 = Web.MeasureWrap(l.Text, l.Font, mw - l.Padding.Horizontal); return new Size(mw, s2.Height + l.Padding.Vertical); }
            return new Size(sz.Width + l.Padding.Horizontal + 1, sz.Height + l.Padding.Vertical);
        }
        if (Web.IsFlex(c))
        {
            var p = c is IWebBlock || c is WebStack ? new Size(Math.Max(40, c.MinimumSize.Width), c.GetPreferredSize(new Size(Math.Max(60, c.MinimumSize.Width), 0)).Height) : new Size(Math.Max(40, c.MinimumSize.Width), c.Height);
            return p;
        }
        if (c is IWebBlock && c is not WebStack)
        {
            int nw = Math.Min(c.GetPreferredSize(Size.Empty).Width, Math.Max(1, w - c.Margin.Horizontal));
            return new Size(nw, c.GetPreferredSize(new Size(nw, 0)).Height);
        }
        if (c.AutoSize || c is WebStack)
        {
            var p = c.GetPreferredSize(new Size(Math.Max(1, w - c.Margin.Horizontal), 0));
            return new Size(Math.Min(p.Width, Math.Max(1, w - c.Margin.Horizontal)), p.Height);
        }
        return new Size(Math.Min(c.Width, Math.Max(1, w - c.Margin.Horizontal)), c.Height);
    }
}

/// <summary>세로 쌓기에서 폭을 꽉 채우는 부품 (막대·고르기·구역 등). GetPreferredSize(폭) 의 높이를 씀</summary>
public interface IWebBlock { }
