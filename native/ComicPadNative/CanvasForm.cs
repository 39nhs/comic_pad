// 시제품 창: 펜·마우스·터치를 Windows 포인터 메시지로 직접 받고, 그린 부분만 다시 그려 바로 화면에 올린다(GDI BitBlt).
// 제목 표시줄에 펜 점 개수(초당)·필압·입력→화면 지연(ms)을 보여 줌 — 클립스튜디오·웹 판과 비교용.
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ComicPadNative;

public sealed class CanvasForm : Form
{
    Doc doc;
    readonly BrushSettings brush = new();
    bool eraser;
    Stroke stroke;
    double zoom = 1, panX, panY;
    // 화면 버퍼 (DIB)
    IntPtr memDC, dib, oldBmp; unsafe uint* bits; int bw, bh;
    // 상태
    double hoverX = -1, hoverY = -1; Rectangle lastCircle = Rectangle.Empty;
    bool spaceDown, panning; Point panStart; double panX0, panY0;
    readonly Dictionary<uint, PointF> touches = new();
    float pinchD0; double pinchZoom0; PointF pinchC0; double pinchPanX0, pinchPanY0;
    // 측정
    readonly long qpf = Stopwatch.Frequency;
    double latencyMs; int samplesThisSec, rate; long secStart = Stopwatch.GetTimestamp(); double lastPressure;
    readonly POINTER_PEN_INFO[] hist = new POINTER_PEN_INFO[64];
    readonly Dictionary<IntPtr, RectangleF[]> devRects = new();

    public CanvasForm()
    {
        Text = "ComicPad Native (시제품)";
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "ComicPad.ico")); } catch { }
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        KeyPreview = true;
        Cursor = Cursors.Cross;
        WindowState = FormWindowState.Maximized;
        ClientSize = new Size(1400, 900);
        doc = new Doc(2480, 3508);   // A4 300dpi
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 펜 동작(길게 눌러 오른쪽 클릭, 탭 표시) 끄기
        foreach (int k in new[] { 2, 3, 4, 5, 6 }) { int no = 0; SetWindowFeedbackSetting(Handle, k, 0, 4, ref no); }
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Fit(); }

    // ---- 화면 버퍼 -------------------------------------------------------------
    unsafe void EnsureBuffer()
    {
        int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
        if (dib != IntPtr.Zero && w == bw && h == bh) return;
        if (dib != IntPtr.Zero) { SelectObject(memDC, oldBmp); DeleteObject(dib); DeleteDC(memDC); }
        var bi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        memDC = CreateCompatibleDC(IntPtr.Zero);
        dib = CreateDIBSection(memDC, ref bi, 0, out IntPtr p, IntPtr.Zero, 0);
        oldBmp = SelectObject(memDC, dib);
        bits = (uint*)p; bw = w; bh = h;
        RenderScreen(new Rectangle(0, 0, w, h));
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) { EnsureBuffer(); Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureBuffer();
        IntPtr hdc = e.Graphics.GetHdc();
        BitBlt(hdc, 0, 0, bw, bh, memDC, 0, 0, SRCCOPY);
        e.Graphics.ReleaseHdc(hdc);
    }

    /// <summary>화면 영역을 문서에서 다시 그려 버퍼에 (확대가 1보다 작으면 여러 점 평균)</summary>
    unsafe void RenderScreen(Rectangle sr)
    {
        sr.Intersect(new Rectangle(0, 0, bw, bh));
        if (sr.Width <= 0 || sr.Height <= 0) return;
        int k = zoom >= 1 ? 1 : Math.Min(4, (int)Math.Ceiling(1 / zoom));
        double inv = 1 / zoom, step = inv / k;
        uint bg = 0xFF4B4B52;
        for (int y = sr.Top; y < sr.Bottom; y++)
        {
            uint* row = bits + y * bw;
            double dy0 = (y - panY) * inv;
            for (int x = sr.Left; x < sr.Right; x++)
            {
                double dx0 = (x - panX) * inv;
                if (k == 1)
                {
                    int ix = (int)Math.Floor(dx0 + 0.5 * inv), iy = (int)Math.Floor(dy0 + 0.5 * inv);
                    row[x] = ix < 0 || iy < 0 || ix >= doc.W || iy >= doc.H ? bg : doc.Composite(iy * doc.W + ix);
                    continue;
                }
                uint sb = 0, sg = 0, sr2 = 0; int n = 0, outside = 0;
                for (int j = 0; j < k; j++)
                {
                    int iy = (int)(dy0 + (j + 0.5) * step);
                    for (int i = 0; i < k; i++)
                    {
                        int ix = (int)(dx0 + (i + 0.5) * step);
                        if (ix < 0 || iy < 0 || ix >= doc.W || iy >= doc.H) { outside++; continue; }
                        uint c = doc.Composite(iy * doc.W + ix);
                        sb += c & 0xFF; sg += (c >> 8) & 0xFF; sr2 += (c >> 16) & 0xFF; n++;
                    }
                }
                row[x] = n == 0 || outside > n ? bg : 0xFF000000 | (sr2 / (uint)n << 16) | (sg / (uint)n << 8) | (sb / (uint)n);
            }
        }
    }

    Rectangle DocToScreen(Rect r) => Rectangle.FromLTRB((int)Math.Floor(r.X * zoom + panX) - 1, (int)Math.Floor(r.Y * zoom + panY) - 1,
        (int)Math.Ceiling(r.R * zoom + panX) + 1, (int)Math.Ceiling(r.B * zoom + panY) + 1);

    /// <summary>바뀐 문서 영역을 버퍼에 다시 그리고 바로 화면에 (가장 짧은 지연)</summary>
    void Present(Rect docDirty, long inputQpc = 0)
    {
        EnsureBuffer();
        var sr = docDirty.IsEmpty ? Rectangle.Empty : DocToScreen(docDirty);
        if (!sr.IsEmpty) RenderScreen(sr);
        // 브러시 크기 원 (펜·마우스 위치)
        var oldC = lastCircle;
        if (!oldC.IsEmpty) RenderScreen(oldC);
        lastCircle = DrawCircle();
        var all = Rectangle.Union(Rectangle.Union(sr.IsEmpty ? oldC : sr, oldC.IsEmpty ? sr : oldC), lastCircle.IsEmpty ? (sr.IsEmpty ? oldC : sr) : lastCircle);
        all.Intersect(new Rectangle(0, 0, bw, bh));
        if (all.Width <= 0 || all.Height <= 0) return;
        IntPtr hdc = GetDC(Handle);
        BitBlt(hdc, all.X, all.Y, all.Width, all.Height, memDC, all.X, all.Y, SRCCOPY);
        ReleaseDC(Handle, hdc);
        GdiFlush();
        if (inputQpc > 0)
        {
            double ms = (Stopwatch.GetTimestamp() - inputQpc) * 1000.0 / qpf;
            if (ms >= 0 && ms < 500) latencyMs = latencyMs == 0 ? ms : latencyMs * 0.9 + ms * 0.1;
        }
    }

    unsafe Rectangle DrawCircle()
    {
        if (hoverX < 0) return Rectangle.Empty;
        double r = brush.Size / 2 * zoom;
        if (r < 3) return Rectangle.Empty;
        var box = Rectangle.FromLTRB((int)(hoverX - r - 2), (int)(hoverY - r - 2), (int)(hoverX + r + 3), (int)(hoverY + r + 3));
        box.Intersect(new Rectangle(0, 0, bw, bh));
        int n = Math.Max(16, (int)(r * 6.3));
        for (int i = 0; i < n; i++)
        {
            double a = i * 2 * Math.PI / n;
            for (int ring = 0; ring < 2; ring++)
            {
                int x = (int)Math.Round(hoverX + Math.Cos(a) * (r + ring)), y = (int)Math.Round(hoverY + Math.Sin(a) * (r + ring));
                if (x >= 0 && y >= 0 && x < bw && y < bh) bits[y * bw + x] = ring == 0 ? 0xFF202020 : 0xFFF0F0F0;
            }
        }
        return box;
    }

    // ---- 보기 ------------------------------------------------------------------
    void Fit()
    {
        EnsureBuffer();
        zoom = Math.Min((bw - 40.0) / doc.W, (bh - 40.0) / doc.H);
        panX = (bw - doc.W * zoom) / 2; panY = (bh - doc.H * zoom) / 2;
        RenderScreen(new Rectangle(0, 0, bw, bh)); lastCircle = Rectangle.Empty; Invalidate(); UpdateTitle();
    }

    void ZoomAt(double f, double sx, double sy)
    {
        double nz = Math.Clamp(zoom * f, 0.05, 32);
        f = nz / zoom;
        panX = sx - (sx - panX) * f; panY = sy - (sy - panY) * f; zoom = nz;
        RenderScreen(new Rectangle(0, 0, bw, bh)); lastCircle = Rectangle.Empty; Invalidate(); UpdateTitle();
    }

    void PanTo(double px, double py) { panX = px; panY = py; RenderScreen(new Rectangle(0, 0, bw, bh)); lastCircle = Rectangle.Empty; Invalidate(); }

    void UpdateTitle()
    {
        Text = $"ComicPad Native (시제품) — {(eraser ? "지우개" : "G펜")} {brush.Size:0.#}px · {zoom * 100:0}% · 펜 {rate}점/초 · 필압 {lastPressure:0.00} · 입력→화면 {latencyMs:0.0}ms   [ ] 크기 · E 지우개 · Ctrl+Z/Y · Ctrl+0 맞춤 · Ctrl+S 저장";
    }

    // ---- 입력 ------------------------------------------------------------------
    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_POINTERDOWN: case WM_POINTERUPDATE: case WM_POINTERUP: case WM_POINTERLEAVE: case WM_POINTERCAPTURECHANGED:
                if (OnPointer(ref m)) return;
                break;
            case WM_MOUSEWHEEL:
            {
                int delta = (short)((long)m.WParam >> 16);
                var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)((long)m.LParam >> 16)));
                ZoomAt(delta > 0 ? 1.15 : 1 / 1.15, p.X, p.Y);
                return;
            }
        }
        base.WndProc(ref m);
    }

    bool OnPointer(ref Message m)
    {
        uint id = (uint)((long)m.WParam & 0xFFFF);
        if (!GetPointerType(id, out int type)) return false;
        if (type == PT_TOUCH) { OnTouch(ref m, id); return true; }
        if (m.Msg == WM_POINTERLEAVE) { hoverX = -1; Present(Rect.Empty); return true; }
        if (m.Msg == WM_POINTERCAPTURECHANGED) { if (stroke != null) FinishStroke(new Pt(0, 0, 0, 0), cancel: false); return true; }

        // 펜: 지난번 이후 모든 점(기록), 마우스: 지금 점
        uint n = 1;
        if (type == PT_PEN)
        {
            n = (uint)hist.Length;
            if (!GetPointerPenInfoHistory(id, ref n, hist) || n == 0) { n = 1; if (!GetPointerPenInfo(id, out hist[0])) return false; }
            n = Math.Min(n, (uint)hist.Length);
        }
        else
        {
            if (!GetPointerInfo(id, out var pi)) return false;
            hist[0] = new POINTER_PEN_INFO { pointerInfo = pi, pressure = 1024, penMask = 1 };
        }
        var origin = PointToScreen(Point.Empty);
        long qpc = 0;
        bool eraserEnd = (hist[0].penFlags & (PEN_FLAG_ERASER | PEN_FLAG_INVERTED)) != 0;
        for (int i = (int)n - 1; i >= 0; i--)
        {
            var pp = hist[i]; var info = pp.pointerInfo;
            ScreenPos(ref info, type == PT_PEN, out double sx, out double sy);
            double cx = sx - origin.X, cy = sy - origin.Y;
            double pr = (pp.penMask & 1) != 0 ? pp.pressure / 1024.0 : 1;
            bool contact = (info.pointerFlags & POINTER_FLAG_INCONTACT) != 0;
            hoverX = cx; hoverY = cy;
            qpc = (long)info.PerformanceCount;
            if (type == PT_PEN) { samplesThisSec++; if (contact) lastPressure = pr; }

            // 화면 이동 (스페이스 + 끌기, 가운데 버튼)
            bool middle = (info.pointerFlags & POINTER_FLAG_THIRDBUTTON) != 0;
            if ((spaceDown || middle) && contact && stroke == null)
            {
                if (!panning) { panning = true; panStart = new Point((int)cx, (int)cy); panX0 = panX; panY0 = panY; }
                else PanTo(panX0 + cx - panStart.X, panY0 + cy - panStart.Y);
                continue;
            }
            if (!contact && panning) { panning = false; }

            double dx = (cx - panX) / zoom, dy = (cy - panY) / zoom, t = info.dwTime;
            if (contact && stroke == null && m.Msg != WM_POINTERUP)
            {
                bool right = (info.pointerFlags & POINTER_FLAG_SECONDBUTTON) != 0 && type != PT_PEN;
                if (right) continue;
                stroke = new Stroke(doc, brush, eraser || eraserEnd) { Zoom = zoom };
                stroke.Begin(new Pt(dx, dy, pr, t));
                Capture = true;
            }
            else if (contact && stroke != null) stroke.Add(new Pt(dx, dy, pr, t));
            else if (!contact && stroke != null) FinishStroke(new Pt(dx, dy, pr, t), cancel: false);
        }
        if (m.Msg == WM_POINTERUP && stroke != null)
        {
            var info = hist[0].pointerInfo; ScreenPos(ref info, type == PT_PEN, out double sx, out double sy);
            FinishStroke(new Pt((sx - origin.X - panX) / zoom, (sy - origin.Y - panY) / zoom, 0, info.dwTime), cancel: false);
        }
        var dirty = stroke?.Dirty ?? Rect.Empty;
        if (stroke != null) stroke.Dirty = Rect.Empty;
        Present(dirty.Union(pendingDirty), qpc);
        pendingDirty = Rect.Empty;
        long now = Stopwatch.GetTimestamp();
        if (now - secStart > qpf) { rate = samplesThisSec; samplesThisSec = 0; secStart = now; UpdateTitle(); }
        m.Result = IntPtr.Zero;
        return true;
    }

    Rect pendingDirty = Rect.Empty;

    void FinishStroke(Pt last, bool cancel)
    {
        var s = stroke; stroke = null; Capture = false;
        if (s == null) return;
        var before = s.End(last);
        pendingDirty = pendingDirty.Union(s.Dirty);
        doc.PushUndo(before);
    }

    void OnTouch(ref Message m, uint id)
    {
        if (!GetPointerInfo(id, out var pi)) return;
        var c = PointToClient(new Point(pi.ptPixelLocation.X, pi.ptPixelLocation.Y));
        if (m.Msg == WM_POINTERUP || m.Msg == WM_POINTERLEAVE || m.Msg == WM_POINTERCAPTURECHANGED) { touches.Remove(id); ResetPinch(); return; }
        bool isNew = !touches.ContainsKey(id);
        touches[id] = c;
        if (isNew) { ResetPinch(); return; }
        if (touches.Count == 1) { PanTo(pinchPanX0 + c.X - pinchC0.X, pinchPanY0 + c.Y - pinchC0.Y); return; }
        var (cen, d) = PinchInfo();
        double nz = Math.Clamp(pinchZoom0 * d / Math.Max(1, pinchD0), 0.05, 32), f = nz / pinchZoom0;
        zoom = nz;
        panX = cen.X - (pinchC0.X - pinchPanX0) * f; panY = cen.Y - (pinchC0.Y - pinchPanY0) * f;
        RenderScreen(new Rectangle(0, 0, bw, bh)); lastCircle = Rectangle.Empty; Invalidate(); UpdateTitle();
    }

    void ResetPinch()
    {
        if (touches.Count == 0) return;
        var (cen, d) = PinchInfo();
        pinchC0 = cen; pinchD0 = d; pinchZoom0 = zoom; pinchPanX0 = panX; pinchPanY0 = panY;
    }

    (PointF, float) PinchInfo()
    {
        float sx = 0, sy = 0; foreach (var p in touches.Values) { sx += p.X; sy += p.Y; }
        var cen = new PointF(sx / touches.Count, sy / touches.Count);
        if (touches.Count < 2) return (cen, 1);
        var a = touches.Values.ElementAt(0); var b = touches.Values.ElementAt(1);
        return (cen, (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)));
    }

    // 1픽셀보다 작은 펜 위치 (HIMETRIC → 화면 픽셀)
    void ScreenPos(ref POINTER_INFO info, bool pen, out double x, out double y)
    {
        x = info.ptPixelLocation.X; y = info.ptPixelLocation.Y;
        if (!pen) return;
        if (!devRects.TryGetValue(info.sourceDevice, out var r))
        {
            r = GetPointerDeviceRects(info.sourceDevice, out RECT dv, out RECT ds) && dv.Right > dv.Left && dv.Bottom > dv.Top
                ? new[] { RectangleF.FromLTRB(dv.Left, dv.Top, dv.Right, dv.Bottom), RectangleF.FromLTRB(ds.Left, ds.Top, ds.Right, ds.Bottom) } : null;
            devRects[info.sourceDevice] = r;
        }
        if (r == null) return;
        double hx = r[1].Left + (info.ptHimetricLocation.X - r[0].Left) * r[1].Width / r[0].Width;
        double hy = r[1].Top + (info.ptHimetricLocation.Y - r[0].Top) * r[1].Height / r[0].Height;
        if (Math.Abs(hx - x) <= 1.5 && Math.Abs(hy - y) <= 1.5) { x = hx; y = hy; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = e.Control;
        switch (e.KeyCode)
        {
            case Keys.Space: spaceDown = true; Cursor = Cursors.Hand; break;
            case Keys.OemOpenBrackets: brush.Size = Math.Max(1, brush.Size / 1.15f); UpdateTitle(); Present(Rect.Empty); break;
            case Keys.OemCloseBrackets: brush.Size = Math.Min(500, brush.Size * 1.15f); UpdateTitle(); Present(Rect.Empty); break;
            case Keys.E: eraser = !eraser; UpdateTitle(); break;
            case Keys.B: eraser = false; UpdateTitle(); break;
            case Keys.Z when ctrl && !e.Shift: Present(doc.Undo()); break;
            case Keys.Z when ctrl && e.Shift: case Keys.Y when ctrl: Present(doc.Redo()); break;
            case Keys.D0 when ctrl: Fit(); break;
            case Keys.D1 when ctrl: ZoomAt(1 / zoom, bw / 2.0, bh / 2.0); break;
            case Keys.Oemplus: ZoomAt(1.25, bw / 2.0, bh / 2.0); break;
            case Keys.OemMinus: ZoomAt(0.8, bw / 2.0, bh / 2.0); break;
            case Keys.S when ctrl: SavePng(); break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) { spaceDown = false; panning = false; Cursor = Cursors.Cross; } }

    unsafe void SavePng()
    {
        using var bmp = new Bitmap(doc.W, doc.H, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, doc.W, doc.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < doc.H; y++) { uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride); for (int x = 0; x < doc.W; x++) row[x] = doc.Composite(y * doc.W + x); }
        bmp.UnlockBits(bd);
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string path = Path.Combine(dir, $"ComicPadNative_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        bmp.Save(path, ImageFormat.Png);
        Text = "저장함: " + path;
    }

    // 시험용: 지금 상태를 글자로 (자동 시험이 읽음)
    public string Stats() => $"rate={rate};pressure={lastPressure:0.000};latency={latencyMs:0.00};zoom={zoom:0.000};ink={InkPixels()}";
    int InkPixels() { int n = 0; foreach (var c in doc.Ink) if ((c >> 24) != 0) n++; return n; }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (dib != IntPtr.Zero) { SelectObject(memDC, oldBmp); DeleteObject(dib); DeleteDC(memDC); }
        base.OnFormClosed(e);
    }

    // ---- Win32 -----------------------------------------------------------------
    const int WM_POINTERUPDATE = 0x245, WM_POINTERDOWN = 0x246, WM_POINTERUP = 0x247, WM_POINTERLEAVE = 0x24A, WM_POINTERCAPTURECHANGED = 0x24C, WM_MOUSEWHEEL = 0x20A;
    const int PT_TOUCH = 2, PT_PEN = 3;
    const int POINTER_FLAG_INCONTACT = 0x4, POINTER_FLAG_SECONDBUTTON = 0x20, POINTER_FLAG_THIRDBUTTON = 0x40;
    const int PEN_FLAG_INVERTED = 2, PEN_FLAG_ERASER = 4;
    const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public int pointerType; public uint pointerId; public uint frameId; public int pointerFlags;
        public IntPtr sourceDevice; public IntPtr hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount; public int InputData; public uint dwKeyStates; public ulong PerformanceCount; public int ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_PEN_INFO { public POINTER_INFO pointerInfo; public int penFlags, penMask; public uint pressure, rotation; public int tiltX, tiltY; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [DllImport("user32.dll")] static extern bool GetPointerType(uint id, out int type);
    [DllImport("user32.dll")] static extern bool GetPointerInfo(uint id, out POINTER_INFO info);
    [DllImport("user32.dll")] static extern bool GetPointerPenInfo(uint id, out POINTER_PEN_INFO info);
    [DllImport("user32.dll")] static extern bool GetPointerPenInfoHistory(uint id, ref uint count, [In, Out] POINTER_PEN_INFO[] info);
    [DllImport("user32.dll")] static extern bool GetPointerDeviceRects(IntPtr device, out RECT pointerDeviceRect, out RECT displayRect);
    [DllImport("user32.dll")] static extern bool SetWindowFeedbackSetting(IntPtr h, int feedback, int flags, int size, ref int config);
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
