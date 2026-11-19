// 웹 .panel-scroll (overflow-y: auto; scrollbar-width: thin) 흉내: 세로로만 굴러가고, 스크롤 막대가 내용 폭을 줄이지 않음.
//  - 막대는 오른쪽 가장자리 6px 에 겹쳐 그림 (내용의 오른쪽 여백 10px 안) — 넘칠 때만 보임, 끌기·누르기·휠 모두 됨
//  - 안의 부품은 Content 하나 (도구 옵션 판 등). 내용 높이가 바뀌면 굴린 자리를 다시 맞춤
//  - 휠: 자식 부품이 처리하지 않은 휠은 부모로 올라오므로 여기서 받음 (한 칸 = 48px, 웹 Edge 와 비슷)
namespace ComicPadNative.UI.Controls;

public class WebScroll : Control
{
    Control content;
    int y;
    readonly Bar bar;
    public const int BarW = 6;

    public WebScroll()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ContainerControl, true);
        BackColor = Theme.Panel; ForeColor = Theme.Text;
        bar = new Bar(this);
        Controls.Add(bar);
    }

    /// <summary>안에 놓을 부품 (하나). 왼쪽 끝 0, 폭은 이 칸 폭 그대로 (WebStack 은 스스로 부모 폭을 채움)</summary>
    public Control Content
    {
        get => content;
        set
        {
            if (content == value) return;
            if (content != null) { content.SizeChanged -= ContentSized; Controls.Remove(content); }
            content = value; y = 0;
            if (content != null)
            {
                content.Location = new Point(0, 0);
                Controls.Add(content);
                content.SizeChanged += ContentSized;
                bar.BringToFront();
            }
            Sync();
        }
    }

    void ContentSized(object s, EventArgs e) => Sync();

    public int ContentHeight => content?.Height ?? 0;
    public int MaxScroll => Math.Max(0, ContentHeight - ClientSize.Height);
    public bool Overflows => MaxScroll > 0;

    /// <summary>굴린 양 (px)</summary>
    public int ScrollY
    {
        get => y;
        set { int v = Math.Clamp(value, 0, MaxScroll); if (v == y && content?.Top == -v) return; y = v; Sync(); }
    }

    /// <summary>부품이 보이도록 굴림 (탭으로 옮겨 갈 때 등)</summary>
    public void ScrollIntoView(Control c)
    {
        if (content == null || c == null || !content.Contains(c)) return;
        var r = content.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
        if (r.Top < y) ScrollY = r.Top;
        else if (r.Bottom > y + ClientSize.Height) ScrollY = r.Bottom - ClientSize.Height;
    }

    void Sync()
    {
        y = Math.Clamp(y, 0, MaxScroll);
        if (content != null && content.Top != -y) content.Top = -y;
        bar.Bounds = new Rectangle(ClientSize.Width - BarW - 1, 0, BarW, ClientSize.Height);
        bar.Visible = Overflows;
        bar.Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Sync(); }
    protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); content?.PerformLayout(); Sync(); }
    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(BackColor);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!Overflows) { base.OnMouseWheel(e); return; }
        ScrollY -= e.Delta * 48 / 120;
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); if (e.Control != bar) bar.BringToFront(); }

    /// <summary>얇은 스크롤 막대 (웹 thin: 바탕 없이 둥근 손잡이만)</summary>
    sealed class Bar : Control
    {
        readonly WebScroll owner;
        bool drag, hover; int grabY, grabScroll;
        public Bar(WebScroll owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.Panel; Cursor = Cursors.Default;
        }

        Rectangle Thumb()
        {
            int h = Height, ch = Math.Max(1, owner.ContentHeight);
            int th = Math.Max(24, h * h / ch);
            int max = owner.MaxScroll;
            int ty = max <= 0 ? 0 : (int)((h - th) * (owner.y / (double)max));
            return new Rectangle(1, ty + 2, Width - 2, Math.Max(8, th - 4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var t = Thumb();
            var col = drag || hover ? ColorTranslator.FromHtml("#8a8a96") : ColorTranslator.FromHtml("#5c5c66");
            using var b = new SolidBrush(col);
            using var p = Theme.Round(t, t.Width / 2f);
            g.FillPath(b, p);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var t = Thumb();
            if (!t.Contains(e.Location))
            {
                // 막대 빈 곳: 한 화면씩
                owner.ScrollY += e.Y < t.Top ? -owner.ClientSize.Height + 40 : owner.ClientSize.Height - 40;
                Invalidate(); return;
            }
            drag = true; Capture = true; grabY = e.Y; grabScroll = owner.y; Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!drag) return;
            var t = Thumb();
            int track = Math.Max(1, Height - t.Height - 4);
            owner.ScrollY = grabScroll + (int)((e.Y - grabY) * (owner.MaxScroll / (double)track));
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; Capture = false; Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) => owner.OnMouseWheel(e);
    }
}
