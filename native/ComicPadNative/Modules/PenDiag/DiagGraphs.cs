// 펜 진단 그래프·실시간 값 (직접 그리는 작은 칸들): 지금 값 표, 시간에 따른 필압 (장치 값 vs 최종), 점 간격 분포, 가만히 있을 때 떨림, 판정 목록.
using System.Drawing.Drawing2D;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;
using ComicPadNative.Modules.BrushEngine;

namespace ComicPadNative.Modules.PenDiagnosis;

/// <summary>그래프 칸 공통: 제목 + 테두리, 어두운 바탕</summary>
public abstract class GraphBox : Control, IWebBlock
{
    public override Size GetPreferredSize(Size proposed) => new(proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : Width, Height);
    protected GraphBox(string title, int h)
    {
        Title = title; Height = h; Margin = new Padding(0, 0, 0, 8);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Web.Body;
    }
    public string Title;
    public string Note = "";
    protected static readonly Color Grid = Color.FromArgb(70, 128, 128, 140), Raw = Color.FromArgb(150, 150, 160), Hi = Color.FromArgb(61, 123, 224);
    protected Rectangle Plot => new(34, 22, Math.Max(10, Width - 42), Math.Max(10, Height - 40));
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel2);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, Title, Web.Tiny, new Point(6, 4), Theme.Muted);
        if (Note != "") TextRenderer.DrawText(g, Note, Web.Tiny, new Rectangle(0, 4, Width - 6, 16), Theme.Text, TextFormatFlags.Right);
        DrawPlot(g, Plot);
    }
    protected abstract void DrawPlot(Graphics g, Rectangle r);
    protected static void Axis(Graphics g, Rectangle r, string bottomLeft, string bottomRight, string topLeft, string lowLeft)
    {
        using var p = new Pen(Grid);
        g.DrawRectangle(p, r);
        TextRenderer.DrawText(g, bottomLeft, Web.Tiny, new Point(r.Left - 2, r.Bottom + 1), Theme.Muted);
        TextRenderer.DrawText(g, bottomRight, Web.Tiny, new Rectangle(r.Left, r.Bottom + 1, r.Width, 14), Theme.Muted, TextFormatFlags.Right);
        TextRenderer.DrawText(g, topLeft, Web.Tiny, new Rectangle(0, r.Top - 2, r.Left - 3, 14), Theme.Muted, TextFormatFlags.Right);
        TextRenderer.DrawText(g, lowLeft, Web.Tiny, new Rectangle(0, r.Bottom - 12, r.Left - 3, 14), Theme.Muted, TextFormatFlags.Right);
    }
}

/// <summary>시간에 따른 필압: 회색 = 장치가 보낸 값, 파랑 = 다듬기·곡선을 거친 최종 값 (최근 한 획, 길면 마지막 3초)</summary>
public sealed class PressureGraph : GraphBox
{
    public List<PenSample> Stroke;
    public Func<FeelPipe> Pipe;
    public PressureGraph() : base("필압 — 회색: 장치 값 · 파랑: 최종 (곡선·다듬기 후)", 130) { }
    protected override void DrawPlot(Graphics g, Rectangle r)
    {
        Axis(g, r, "", "", "1", "0");
        var s = Stroke;
        if (s == null || s.Count < 2) { TextRenderer.DrawText(g, "그으면 여기에 나옵니다", Web.Tiny, r, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        int i0 = 0; double tEnd = s[^1].T;
        while (i0 < s.Count - 2 && tEnd - s[i0].T > 3000) i0++;
        double t0 = s[i0].T, span = Math.Max(50, tEnd - t0);
        var pipe = Pipe?.Invoke();
        // 파이프는 획 처음부터 돌려야 같은 값 (다듬기·시작 줄이기)
        var fin = new double[s.Count];
        for (int i = 0; i < s.Count; i++) fin[i] = pipe?.Final(s[i].P) ?? s[i].P;
        PointF P(int i, double v) => new((float)(r.Left + (s[i].T - t0) / span * r.Width), (float)(r.Bottom - Math.Clamp(v, 0, 1) * r.Height));
        var raw = new PointF[s.Count - i0]; var fl = new PointF[s.Count - i0];
        for (int i = i0; i < s.Count; i++) { raw[i - i0] = P(i, s[i].P); fl[i - i0] = P(i, fin[i]); }
        using (var p = new Pen(Raw, 1.5f)) g.DrawLines(p, raw);
        using (var p = new Pen(Hi, 2f)) g.DrawLines(p, fl);
        TextRenderer.DrawText(g, $"{span / 1000:0.0}초", Web.Tiny, new Rectangle(r.Left, r.Bottom + 1, r.Width, 14), Theme.Muted, TextFormatFlags.Right);
    }
}

/// <summary>점 간격 분포 (1ms 칸), 세로 줄 = 8.3ms(120Hz)</summary>
public sealed class IntervalGraph : GraphBox
{
    public int[] Hist; public double BinMs = 1; public double RateHz;
    public IntervalGraph() : base("점 간격 분포 (ms) — 고르게 한 곳에 몰려 있으면 좋음", 120) { }
    protected override void DrawPlot(Graphics g, Rectangle r)
    {
        int n = Hist?.Length ?? 24;
        Axis(g, r, "0", $"{n * BinMs:0}+ms", "", "");
        if (Hist == null || Hist.Sum() == 0) { TextRenderer.DrawText(g, "그으면 여기에 나옵니다", Web.Tiny, r, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        int max = Math.Max(1, Hist.Max());
        float bwid = r.Width / (float)n;
        using var b = new SolidBrush(Hi);
        for (int i = 0; i < n; i++)
        {
            float h = Hist[i] / (float)max * (r.Height - 2);
            g.FillRectangle(b, r.Left + i * bwid + 1, r.Bottom - h, Math.Max(1, bwid - 2), h);
        }
        // 120Hz 기준선
        float x120 = r.Left + (float)(1000 / 120.0 / BinMs / n * r.Width);
        using var dp = new Pen(Color.FromArgb(200, 224, 96, 42)) { DashPattern = new[] { 3f, 3f } };
        g.DrawLine(dp, x120, r.Top, x120, r.Bottom);
        TextRenderer.DrawText(g, "120Hz", Web.Tiny, new Point((int)x120 + 2, r.Top + 1), Color.FromArgb(224, 96, 42));
    }
}

/// <summary>가만히 있을 때 펜 끝 위치 (가장 긴 구간, 가운데 기준 ±3px), 원 = 떨림(RMS)</summary>
public sealed class JitterGraph : GraphBox
{
    public PenStats.HoldResult Hold = PenStats.HoldResult.None;
    public JitterGraph() : base("가만히 있을 때 떨림 (±3px) — 펜을 대고 1초 멈춰 보세요", 150) { }
    protected override void DrawPlot(Graphics g, Rectangle r)
    {
        int side = Math.Min(r.Width, r.Height);
        var sq = new Rectangle(r.Left + (r.Width - side) / 2, r.Top, side, side);
        using (var p = new Pen(Grid)) { g.DrawRectangle(p, sq); g.DrawLine(p, sq.Left + side / 2, sq.Top, sq.Left + side / 2, sq.Bottom); g.DrawLine(p, sq.Left, sq.Top + side / 2, sq.Right, sq.Top + side / 2); }
        if (!Hold.Found) { TextRenderer.DrawText(g, "아직 없음", Web.Tiny, sq, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        float k = side / 6f, cx = sq.Left + side / 2f, cy = sq.Top + side / 2f;
        using var b = new SolidBrush(Color.FromArgb(120, 61, 123, 224));
        foreach (var (dx, dy) in Hold.Offsets) g.FillEllipse(b, cx + (float)Math.Clamp(dx, -3, 3) * k - 1.5f, cy + (float)Math.Clamp(dy, -3, 3) * k - 1.5f, 3, 3);
        float rr = (float)Math.Min(3, Hold.JitterPx) * k;
        using var cp = new Pen(Color.FromArgb(224, 96, 42), 1.5f);
        g.DrawEllipse(cp, cx - rr, cy - rr, rr * 2, rr * 2);
        TextRenderer.DrawText(g, $"{Hold.JitterPx:0.00}px", Web.Tiny, new Point(sq.Right + 6, sq.Top), Theme.Text);
        TextRenderer.DrawText(g, $"최대 {Hold.MaxDevPx:0.0}px", Web.Tiny, new Point(sq.Right + 6, sq.Top + 16), Theme.Muted);
        TextRenderer.DrawText(g, $"{Hold.DurationMs / 1000:0.0}초", Web.Tiny, new Point(sq.Right + 6, sq.Top + 32), Theme.Muted);
    }
}

/// <summary>지금 값 표 (이름: 값 두 줄씩), 버튼 불빛</summary>
public sealed class LiveReadout : Control
{
    public readonly List<(string k, string v)> Rows = new();
    public PenBtn Buttons; public bool Contact; public bool IsPen;
    public LiveReadout()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Web.Body; Height = 128;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.Panel2);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 버튼 불빛: 펜 끝 · 측면 1 · 측면 2 · 지우개
        var lamps = new (string n, bool on)[] { ("펜 끝", Contact && (Buttons & PenBtn.Eraser) == 0), ("측면 1", (Buttons & PenBtn.Side1) != 0), ("측면 2", (Buttons & PenBtn.Side2) != 0), ("지우개", (Buttons & PenBtn.Eraser) != 0) };
        int x = 8;
        foreach (var (n, on) in lamps)
        {
            using (var b = new SolidBrush(on ? Color.FromArgb(70, 190, 110) : Color.FromArgb(70, 128, 128, 140))) g.FillEllipse(b, x, 8, 10, 10);
            var sz = TextRenderer.MeasureText(n, Web.Tiny);
            TextRenderer.DrawText(g, n, Web.Tiny, new Point(x + 13, 5), on ? Theme.Text : Theme.Muted);
            x += 13 + sz.Width + 12;
        }
        int cols = Width >= 900 ? 3 : 2, colW = (Width - 8) / cols, lh = 19, y0 = 26;
        for (int i = 0; i < Rows.Count; i++)
        {
            int c = i % cols, r = i / cols;
            var (k, v) = Rows[i];
            int xx = 8 + c * colW, yy = y0 + r * lh;
            TextRenderer.DrawText(g, k, Web.Tiny, new Rectangle(xx, yy + 2, colW / 2, lh), Theme.Muted, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, v, Web.Body, new Rectangle(xx + colW / 2 - 6, yy, colW / 2 + 2, lh), Theme.Text, TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>판정 목록: 색 점 + 제목 + 값 + 고치는 법 (줄바꿈)</summary>
public sealed class CheckList : Control, IWebBlock
{
    public override Size GetPreferredSize(Size proposed) { int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : Width; return new(w, Measure(w)); }
    List<Check> items = new();
    public CheckList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Web.Body; Margin = new Padding(0, 0, 0, 8);
    }
    public void Set(List<Check> c) { items = c; int h = Measure(Width); if (h != Height) { Height = h; Parent?.PerformLayout(); } Invalidate(); }
    static Color ColorOf(Verdict v) => v switch { Verdict.Ok => Color.FromArgb(70, 190, 110), Verdict.Warn => Color.FromArgb(230, 170, 40), Verdict.Bad => Color.FromArgb(225, 75, 70), Verdict.Wait => Color.FromArgb(120, 128, 128, 140), _ => Color.FromArgb(61, 123, 224) };
    static string Word(Verdict v) => v switch { Verdict.Ok => "좋음", Verdict.Warn => "주의", Verdict.Bad => "문제", Verdict.Wait => "확인 중", _ => "참고" };
    const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
    int Measure(int w)
    {
        int y = 2, tw = Math.Max(50, w - 30);
        foreach (var c in items)
        {
            y += TextRenderer.MeasureText(c.Title + (c.Value != "" ? "  " + c.Value : ""), Web.Body, new Size(tw, 0), Wrap).Height + 2;
            if (c.Hint != "") y += TextRenderer.MeasureText(c.Hint, Web.Tiny, new Size(tw, 0), Wrap).Height;
            y += 8;
        }
        return Math.Max(30, y);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); int h = Measure(Width); if (h != Height) Height = h; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Web.ParentBack(this)); g.SmoothingMode = SmoothingMode.AntiAlias;
        int y = 2, tw = Math.Max(50, Width - 30);
        foreach (var c in items)
        {
            using (var b = new SolidBrush(ColorOf(c.V))) g.FillEllipse(b, 4, y + 4, 12, 12);
            string head = c.Title + (c.Value != "" ? "  " + c.Value : "");
            var hs = TextRenderer.MeasureText(head, Web.Body, new Size(tw, 0), Wrap);
            TextRenderer.DrawText(g, head, Web.Body, new Rectangle(24, y, tw, hs.Height), c.V == Verdict.Wait ? Theme.Muted : Theme.Text, Wrap);
            y += hs.Height + 2;
            if (c.Hint != "")
            {
                var ns = TextRenderer.MeasureText(c.Hint, Web.Tiny, new Size(tw, 0), Wrap);
                TextRenderer.DrawText(g, c.Hint, Web.Tiny, new Rectangle(24, y, tw, ns.Height), Theme.Muted, Wrap);
                y += ns.Height;
            }
            y += 8;
        }
    }
    public static string WordOf(Verdict v) => Word(v);
}

/// <summary>필압 곡선 미리 보기: 지금(또는 고르는 중인) 필기감으로 마지막 획(없으면 약→강→약 시험 획)을 실제 브러시로 다시 그림</summary>
public sealed class CurvePreview : Control, IWebBlock
{
    public override Size GetPreferredSize(Size proposed) => new(proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : Width, Height);
    public List<PenSample> Stroke;
    public Func<PenFeel> Feel;
    Bitmap img;
    public CurvePreview() { Height = 96; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); Margin = new Padding(0, 4, 0, 8); }
    public void Redraw() { img?.Dispose(); img = null; Invalidate(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Redraw(); }
    protected override void Dispose(bool disposing) { img?.Dispose(); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (img == null && Width > 4 && Height > 4) img = Render(Stroke, Feel?.Invoke(), Width, Height);
        if (img != null) e.Graphics.DrawImageUnscaled(img, 0, 0); else e.Graphics.Clear(Color.White);
        TextRenderer.DrawText(e.Graphics, Stroke != null ? "마지막 획을 이 곡선으로 다시 그림" : "시험 획 (약 → 강 → 약)", Web.Tiny, new Point(4, 2), Color.FromArgb(140, 140, 150));
    }

    /// <summary>획(펜판 좌표, 장치 필압)을 칸에 맞게 줄여 실제 브러시로 그림</summary>
    public static Bitmap Render(List<PenSample> stroke, PenFeel feel, int w, int h)
    {
        var pts = new List<BrushEngine.BPt>();
        if (stroke != null && stroke.Count >= 2)
        {
            double x0 = stroke.Min(p => p.X), x1 = stroke.Max(p => p.X), y0 = stroke.Min(p => p.Y), y1 = stroke.Max(p => p.Y);
            double k = Math.Min((w - 24) / Math.Max(1, x1 - x0), (h - 24) / Math.Max(1, y1 - y0)); k = Math.Min(k, 1.5);
            double ox = (w - (x1 - x0) * k) / 2 - x0 * k, oy = (h - (y1 - y0) * k) / 2 - y0 * k;
            foreach (var p in stroke) if (p.Contact) pts.Add(new BrushEngine.BPt(p.X * k + ox, p.Y * k + oy, p.P, p.T));
        }
        else
            for (int i = 0; i <= 80; i++) { double t = i / 80.0; pts.Add(new BrushEngine.BPt(14 + (w - 28) * t, h / 2.0 - Math.Sin(t * Math.PI * 2) * h * 0.25, Math.Max(0.02, Math.Sin(Math.PI * t)), i * 8)); }
        var bmp = new Bitmap(w, h);
        try
        {
            var preset = (BrushEngine.PresetStore.Active("brush") ?? BrushEngine.DefaultPresets.Of("brush")[0]).Clone();
            preset.Size = Math.Clamp(preset.Size, 3, 18);
            var st = new BrushEngine.BrushStroke(new BrushEngine.StrokeSetup { W = w, H = h, Preset = preset, Color = 0xFF1E1E22, Feel = feel, Seed = 1 });
            for (int i = 0; i < pts.Count; i++) { if (i == 0) st.Begin(pts[i]); else st.Add(pts[i]); }
            var r = st.End(pts.Count > 0 ? pts[^1] : (BrushEngine.BPt?)null).Clip(w, h);
            var surf = new Core.Surface(w, h);
            if (!r.IsEmpty) new BrushEngine.StrokeApply { Buf = st.Buf, Target = surf, Opacity = preset.Opacity }.Apply(r);
            using var flat = Core.CpadIO.ToBitmap(surf, surf.Full, 0xFFFFFFFF);
            using var g = Graphics.FromImage(bmp); g.DrawImageUnscaled(flat, 0, 0);
        }
        catch { using var g = Graphics.FromImage(bmp); g.Clear(Color.White); }
        return bmp;
    }
}

/// <summary>줄바꿈 글 (재는 것과 그리는 것이 같은 방법이라 한글 줄바꿈에서도 잘리지 않음)</summary>
public sealed class TextBlock : Control, IWebBlock
{
    const TextFormatFlags Fl = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
    public TextBlock(string text, bool muted = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Text = text; Font = muted ? Web.Small : Web.Body; ForeColor = muted ? Theme.Muted : Theme.Text; Margin = new Padding(0, 2, 0, 8);
    }
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : Math.Max(50, Width);
        return new Size(w, string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font, new Size(w, 0), Fl).Height + 2);
    }
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        int h = GetPreferredSize(new Size(Width, 0)).Height;
        if (h != Height) { Height = h; Parent?.PerformLayout(); }
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Web.ParentBack(this));
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Fl);
    }
}
