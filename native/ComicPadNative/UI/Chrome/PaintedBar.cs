// 직접 그리는 막대의 공통 부분: 누를 수 있는 칸(Hit) 목록·올림(hover)·누름·말풍선 도움말·끌기.
// 위 막대 오른쪽 단추·도구 막대·아래 막대·패널 탭이 이것을 씀 (웹 판 button 들처럼 그리기만 다르게).
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ComicPadNative.UI.Chrome;

public sealed class Hit
{
    public Rectangle R;
    public string Tip;
    public Action Click;
    public Func<bool> Enabled;
    public object Data;
    /// <summary>끌기 (막대 조절): 누른 점·움직인 점·뗐는지</summary>
    public Action<Point, bool> Drag;
    public bool IsEnabled => Enabled?.Invoke() ?? true;
}

public abstract class PaintedBar : Control
{
    protected readonly List<Hit> Hits = new();
    protected Hit Hover, Down;
    readonly ToolTip tip = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 8000 };
    string tipText;

    protected PaintedBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Font = Theme.Font;
        BackColor = Theme.Panel; ForeColor = Theme.Text;
    }

    /// <summary>칸 다시 계산 (크기·내용이 바뀌면)</summary>
    protected abstract void LayoutHits();
    protected abstract void PaintBar(Graphics g);

    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Relayout(); }
    bool ready;
    /// <summary>하위 클래스 생성자 끝에서 부름 (그 전에는 칸 계산 안 함)</summary>
    protected void Ready() { ready = true; Relayout(); }
    public void Relayout() { if (!ready) return; Hits.Clear(); LayoutHits(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        PaintBar(g);
    }

    protected Hit HitAt(Point p) { for (int i = Hits.Count - 1; i >= 0; i--) if (Hits[i].R.Contains(p)) return Hits[i]; return null; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Down?.Drag != null && Capture) { Down.Drag(e.Location, false); Invalidate(); return; }
        var h = HitAt(e.Location);
        if (h != Hover) { Hover = h; Invalidate(); SetTip(h?.Tip); }
        Cursor = h?.Drag != null && h.IsEnabled ? DragCursor : Cursors.Default;
    }
    protected virtual Cursor DragCursor => Cursors.SizeWE;

    void SetTip(string t)
    {
        if (t == tipText) return;
        tipText = t;
        tip.Hide(this);
        tip.SetToolTip(this, t ?? "");
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (Hover != null) { Hover = null; Invalidate(); } SetTip(null); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Down = HitAt(e.Location);
        if (Down != null && !Down.IsEnabled) Down = null;
        if (Down?.Drag != null) { Capture = true; Down.Drag(e.Location, false); }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var d = Down; Down = null;
        if (d?.Drag != null) { Capture = false; d.Drag(e.Location, true); Invalidate(); return; }
        Invalidate();
        if (d != null && e.Button == MouseButtons.Left && d.R.Contains(e.Location) && d.IsEnabled && d.Click != null)
        {
            var c = d.Click;
            MainForm.Run(c, d.Tip ?? "단추");
            (App.Main as MainForm)?.Canvas.Focus();
        }
    }

    // ---- 그리기 도우미 ----
    protected static void Fill(Graphics g, Rectangle r, Color c, float rad)
    {
        using var b = new SolidBrush(c);
        if (rad <= 0) { g.FillRectangle(b, r); return; }
        using var p = Theme.Round(r, rad); g.FillPath(b, p);
    }
    protected static void Line(Graphics g, Color c, int x, int y, int w, int h) { using var b = new SolidBrush(c); g.FillRectangle(b, x, y, w, h); }
    protected static void DrawText(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter)
        => TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    protected static int TextW(string s, Font f) => TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    protected static Color Dim(Color c, double a) => Color.FromArgb((int)(255 * a), c);
}
