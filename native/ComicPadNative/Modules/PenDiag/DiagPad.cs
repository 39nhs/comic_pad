// 펜 진단 펜판: 그림판과 같은 길로 포인터 메시지를 받아 (PointerInput) 실제 브러시 엔진(BrushStroke, 지금 브러시·지금 필압 설정)으로 그리고,
// 그림판처럼 받은 즉시 화면 버퍼(DIB)에 그려 창에 바로 올린다 (BitBlt + GdiFlush). 올린 시각을 메시지마다 적어서 입력→화면 지연을 잰다.
// 펜 위치 예측(Gaps.PenPredict)도 그림판과 같이 그리며, "번갈아" 를 켜면 획마다 예측 켬/끔을 바꿔 지연을 비교한다.
using System.Runtime.InteropServices;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Gaps;

namespace ComicPadNative.Modules.PenDiagnosis;

public sealed class DiagPad : Control
{
    public DiagSession Session = new();
    /// <summary>메시지 하나를 그려서 창에 올린 뒤</summary>
    public event Action<PenFrame> FrameShown;
    public bool PredictionOn = true;          // 예측 선 그리기 (그림판 설정과 따로)
    public bool AlternatePrediction;          // 획마다 예측 켬/끔 번갈아 (지연 비교)
    public Func<PenFeel> FeelFor;             // 그릴 때 쓸 필기감 (null = 지금 설정)
    public string Caption = "여기에 펜으로 그려 보세요";
    public PenSample? Hover;                  // 펜을 띄운 채 (닿지 않은) 마지막 점
    public bool StrokePredicted => strokePred;
    public uint InkColor = 0xFF1E1E22;
    /// <summary>그린 것 (시험용)</summary>
    public Surface Paper => paper;

    readonly PenPredict predict = new();
    bool drawing, strokePred; int strokeNo; string strokeType = "pen";
    BrushStroke st; BrushPreset preset; PenFeel feel;
    Surface paper, live;
    IntPtr memDC, dib, oldBmp; unsafe uint* bits; int bw, bh;
    Rectangle lastOv = Rectangle.Empty;
    bool empty = true;

    public DiagPad()
    {
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        Cursor = Cursors.Cross;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); PointerInput.NoFeedback(Handle); }

    // ---- 화면 버퍼 ---------------------------------------------------------------
    unsafe void EnsureBuffer()
    {
        int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
        if (dib != IntPtr.Zero && w == bw && h == bh) return;
        FreeBuffer();
        var bi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        memDC = CreateCompatibleDC(IntPtr.Zero);
        dib = CreateDIBSection(memDC, ref bi, 0, out IntPtr p, IntPtr.Zero, 0);
        oldBmp = SelectObject(memDC, dib);
        bits = (uint*)p; bw = w; bh = h;
        // 크기가 바뀌어도 그린 것은 겹치는 만큼 남김
        var old = paper;
        paper = new Surface(w, h); live = new Surface(w, h);
        st = null; drawing = false;
        if (old != null && !empty)
        {
            var keep = new Rect(0, 0, Math.Min(w, old.W), Math.Min(h, old.H));
            if (!keep.IsEmpty) { var px = old.Read(keep); paper.Write(keep, px); live.Write(keep, px); }
        }
        else empty = true;
        CopyToBits(new Rect(0, 0, w, h));
        DrawCaption();
    }
    void FreeBuffer() { if (dib != IntPtr.Zero) { SelectObject(memDC, oldBmp); DeleteObject(dib); DeleteDC(memDC); dib = IntPtr.Zero; } }

    unsafe void CopyToBits(Rect r)
    {
        r = r.Clip(bw, bh);
        if (r.IsEmpty || bits == null) return;
        var px = live.Read(r);
        for (int y = 0; y < r.H; y++)
        {
            uint* row = bits + (r.Y + y) * bw + r.X;
            for (int x = 0; x < r.W; x++) { uint c = px[y * r.W + x]; row[x] = (c >> 24) == 255 ? c : Px.Over(c, 0xFFFFFFFF) | 0xFF000000; }
        }
        // 1cm 쯤 격자 (흐리게) — 떨림·크기를 눈으로 가늠
        for (int y = r.Y; y < r.B; y++)
            for (int x = r.X; x < r.R; x++)
                if ((x % 40 == 0 || y % 40 == 0) && bits[y * bw + x] == 0xFFFFFFFF) bits[y * bw + x] = 0xFFEDEFF3;
    }

    void DrawCaption()
    {
        if (!empty || memDC == IntPtr.Zero || string.IsNullOrEmpty(Caption)) return;
        using var g = Graphics.FromHdc(memDC);
        TextRenderer.DrawText(g, Caption, Font, new Rectangle(0, 0, bw, bh), Color.FromArgb(150, 150, 160), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    /// <summary>그린 것 지우기 (모은 값은 그대로)</summary>
    public void ClearInk()
    {
        if (paper == null) return;
        paper.Clear(); live.Clear(); st = null; drawing = false; empty = true; lastOv = Rectangle.Empty;
        CopyToBits(new Rect(0, 0, bw, bh)); DrawCaption();
        Invalidate();
    }
    public void SetCaption(string text) { Caption = text; if (empty && paper != null) { CopyToBits(new Rect(0, 0, bw, bh)); DrawCaption(); Invalidate(); } }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) { EnsureBuffer(); Invalidate(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureBuffer();
        IntPtr hdc = e.Graphics.GetHdc();
        BitBlt(hdc, 0, 0, bw, bh, memDC, 0, 0, SRCCOPY);
        e.Graphics.ReleaseHdc(hdc);
    }
    protected override void Dispose(bool disposing) { FreeBuffer(); base.Dispose(disposing); }

    // ---- 입력 (그림판 CanvasView.WndProc/OnPointer 와 같은 순서) ----------------
    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case PointerInput.WM_POINTERDOWN: case PointerInput.WM_POINTERUPDATE: case PointerInput.WM_POINTERUP:
            {
                uint id = (uint)((long)m.WParam & 0xFFFF);
                var f = PointerInput.Read(id, m.Msg, PointToScreen(Point.Empty), App.Get("mousePressure", 1.0));
                if (f == null) break;
                if (f.Down) { Capture = true; Focus(); }
                OnFrame(f);
                if (f.Up) Capture = false;
                m.Result = IntPtr.Zero;
                return;
            }
            case PointerInput.WM_POINTERLEAVE: Hover = null; Present(null); break;
            case PointerInput.WM_POINTERCAPTURECHANGED: if (drawing) { EndStroke(null); Session.EndStroke(); Present(null); } break;
            case PointerInput.WM_POINTERACTIVATE: m.Result = (IntPtr)1; return;
        }
        base.WndProc(ref m);
    }

    /// <summary>메시지 한 장: 점마다 그리기 → 모으기 → 창에 올리기 (시험에서도 부름)</summary>
    public void OnFrame(PenFrame f)
    {
        EnsureBufferIfReady();
        foreach (var s in f.Pts)
        {
            if (s.Contact) { if (!drawing) BeginStroke(s, f.Type); else MoveStroke(s); Hover = null; }
            else { if (drawing) EndStroke(s); Hover = s; }
        }
        if (f.Up && drawing) EndStroke(f.Last);
        Session.Add(f);
        Present(f);
    }
    void EnsureBufferIfReady() { if (IsHandleCreated) EnsureBuffer(); else if (paper == null) { paper = new Surface(Math.Max(1, Width), Math.Max(1, Height)); live = new Surface(paper.W, paper.H); bw = paper.W; bh = paper.H; } }

    double Pr(in PenSample s) => strokeType == "pen" && feel.NoPressure ? 1 : s.P;

    void BeginStroke(in PenSample s, string type)
    {
        strokeNo++;
        strokePred = AlternatePrediction ? strokeNo % 2 == 1 : PredictionOn;
        strokeType = type;
        predict.Reset();
        feel = FeelFor?.Invoke() ?? PenFeel.Current();
        drawing = true; empty = false;
        try
        {
            preset = (PresetStore.Active("brush") ?? DefaultPresets.Of("brush")[0]).Clone();
            preset.Size = Math.Clamp(preset.Size, 3, 24);
            st = new BrushStroke(new StrokeSetup { W = paper.W, H = paper.H, Preset = preset, Color = InkColor, Feel = feel, Seed = 1 });
            st.Begin(new BPt(s.X, s.Y, Pr(s), s.T));
        }
        catch { st = null; }
    }

    void MoveStroke(in PenSample s)
    {
        try { st?.Add(new BPt(s.X, s.Y, Pr(s), s.T)); } catch { st = null; }
        if (strokePred) predict.Add(s.X, s.Y, (long)Math.Round(s.T * System.Diagnostics.Stopwatch.Frequency / 1000.0));
    }

    void EndStroke(PenSample? s)
    {
        drawing = false; predict.Reset();
        if (st == null) return;
        try
        {
            var r = (s is PenSample e ? st.End(new BPt(e.X, e.Y, Pr(e), e.T)) : st.End(null)).Clip(paper.W, paper.H);
            if (!r.IsEmpty)
            {
                new StrokeApply { Buf = st.Buf, Target = paper, Opacity = preset.Opacity }.Apply(r);
                live.Write(r, paper.Read(r));
                CopyToBits(r);
                pendingBlit = pendingBlit.IsEmpty ? ToRc(r) : Rectangle.Union(pendingBlit, ToRc(r));
            }
        }
        catch { }
        st = null;
    }
    Rectangle pendingBlit = Rectangle.Empty;
    static Rectangle ToRc(Rect r) => new(r.X, r.Y, r.W, r.H);

    /// <summary>바뀐 곳을 버퍼에 그리고 창에 바로 올림 → 올린 시각을 메시지에 적음</summary>
    void Present(PenFrame f)
    {
        var all = pendingBlit; pendingBlit = Rectangle.Empty;
        if (st != null && drawing)
        {
            var d = st.TakeDirty().Clip(paper.W, paper.H);
            if (!d.IsEmpty)
            {
                live.Write(d, paper.Read(d));
                try { new StrokeApply { Buf = st.Buf, Target = live, Opacity = preset.Opacity }.Apply(d); } catch { }
                CopyToBits(d);
                all = all.IsEmpty ? ToRc(d) : Rectangle.Union(all, ToRc(d));
            }
        }
        if (memDC != IntPtr.Zero)
        {
            // 겹침 그림: 예측 선, 펜을 띄웠을 때 위치 표시
            if (!lastOv.IsEmpty) { CopyToBits(new Rect(lastOv.X, lastOv.Y, lastOv.Width, lastOv.Height)); all = all.IsEmpty ? lastOv : Rectangle.Union(all, lastOv); }
            var ov = Rectangle.Empty;
            double width = (preset?.Size ?? 8) * 0.6;
            bool predDrawn = drawing && strokePred && predict.Points != null;
            if (predDrawn || Hover != null)
            {
                using var g = Graphics.FromHdc(memDC);
                if (predDrawn) { predict.Draw(g, width, 0xFFE0602A); ov = predict.Bounds(width); }
                if (Hover is PenSample hv)
                {
                    float r = 4;
                    using var p = new Pen(Color.FromArgb(150, 61, 123, 224), 1.5f);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawEllipse(p, (float)hv.X - r, (float)hv.Y - r, r * 2, r * 2);
                    var hr = Rectangle.FromLTRB((int)(hv.X - r - 3), (int)(hv.Y - r - 3), (int)(hv.X + r + 4), (int)(hv.Y + r + 4));
                    ov = ov.IsEmpty ? hr : Rectangle.Union(ov, hr);
                }
            }
            ov.Intersect(new Rectangle(0, 0, bw, bh));
            lastOv = ov;
            if (!ov.IsEmpty) all = all.IsEmpty ? ov : Rectangle.Union(all, ov);
            all.Intersect(new Rectangle(0, 0, bw, bh));
            if (all.Width > 0 && all.Height > 0 && IsHandleCreated)
            {
                IntPtr hdc = GetDC(Handle);
                BitBlt(hdc, all.X, all.Y, all.Width, all.Height, memDC, all.X, all.Y, SRCCOPY);
                ReleaseDC(Handle, hdc);
                GdiFlush();
            }
        }
        if (f != null) f.Predicted = drawing && strokePred;
        if (f != null) { f.PresentT = PointerInput.NowMs(); FrameShown?.Invoke(f); }
    }

    // ---- Win32 ----
    const int SRCCOPY = 0x00CC0020;
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
}
