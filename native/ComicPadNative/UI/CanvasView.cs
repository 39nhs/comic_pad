// 그림판: 펜·마우스·터치를 Windows 포인터 메시지로 직접 받아 도구로, 문서를 합성해 화면에 바로 올림(GDI).
//  - 합성 결과는 문서 크기 캐시(comp)에 두고, 바뀐 영역만 다시 합성 → 화면은 캐시에서 확대·회전해 그림
//  - 펜: 지난번 이후 모든 점, 1픽셀보다 작은 위치, 필압·기울기, 측면 버튼(스포이트), 지우개 끝(지우개)
//  - 손가락: 1개 이동, 2개 확대·회전 / 펜이 가까이 있으면 손바닥으로 보고 무시
//  - 스페이스+끌기·가운데 버튼 = 이동, Ctrl+스페이스 = 확대, Shift+스페이스 = 회전, Alt = 스포이트
using System.Diagnostics;
using System.Runtime.InteropServices;
using ComicPadNative.Core;

namespace ComicPadNative.UI;

public sealed class CanvasView : Control
{
    public readonly ViewXf V = new();
    Surface comp;                                   // 합성 캐시 (문서 크기)
    IntPtr memDC, dib, oldBmp; unsafe uint* bits; int bw, bh;
    Rectangle lastOverlay = Rectangle.Empty;
    double hoverSX = -1, hoverSY = -1;
    public bool SpaceDown, CtrlDown, ShiftDown;
    // 입력 상태
    bool drawing; string tempTool; bool panning; double panSX, panSY, panX0, panY0, rotA0, zoomS0;
    string panMode;
    readonly Dictionary<uint, PointF> touches = new();
    float pinchD0, pinchA0; double pinchZoom0, pinchAngle0, pinchPanX0, pinchPanY0; PointF pinchC0;
    long lastPenTick;
    readonly POINTER_PEN_INFO[] hist = new POINTER_PEN_INFO[64];
    readonly Dictionary<IntPtr, RectangleF[]> devRects = new();
    // 측정
    public double LatencyMs; public int PenRate; int samples; long secStart = Stopwatch.GetTimestamp();
    public event Action ViewChanged;

    public CanvasView()
    {
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        App.Invalidated += r => { Recomposite(r); Present(); };
        App.ToolChanged += () => { Cursor = App.Tool?.Cursor ?? Cursors.Cross; Present(); };
        App.DocChanged += () => { comp = null; Fit(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (int k in new[] { 2, 3, 4, 5, 6 }) { int no = 0; SetWindowFeedbackSetting(Handle, k, 0, 4, ref no); }   // 펜 길게 누르기 오른쪽 클릭 등 끔
    }

    // ---- 합성 캐시 ---------------------------------------------------------------
    public void Recomposite(Rect r)
    {
        var d = App.Doc;
        if (d == null) return;
        if (comp == null || comp.W != d.W || comp.H != d.H) { comp = new Surface(d.W, d.H); r = new Rect(0, 0, d.W, d.H); }
        r = r.Clip(d.W, d.H);
        if (r.IsEmpty) return;
        var o = new RenderOptions { Onion = true };
        // 타일 크기 조각으로 (큰 영역도 메모리 적게)
        for (int y = r.Y; y < r.B; y += Surface.TS)
            for (int x = r.X; x < r.R; x += Surface.TS)
            {
                var part = Rect.FromLTRB(x, y, Math.Min(r.R, x + Surface.TS), Math.Min(r.B, y + Surface.TS));
                comp.Write(part, App.Comp.Render(d, part, o));
            }
        var sr = DocToScreen(r);
        pendingScreen = pendingScreen.IsEmpty ? sr : Rectangle.Union(pendingScreen, sr);
    }
    Rectangle pendingScreen = Rectangle.Empty;

    // ---- 화면 버퍼 -----------------------------------------------------------------
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

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) { EnsureBuffer(); RenderScreen(new Rectangle(0, 0, bw, bh)); Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureBuffer();
        IntPtr hdc = e.Graphics.GetHdc();
        BitBlt(hdc, 0, 0, bw, bh, memDC, 0, 0, SRCCOPY);
        e.Graphics.ReleaseHdc(hdc);
    }

    /// <summary>화면 영역을 합성 캐시에서 그림 (축소면 여러 점 평균, 선택 영역 테두리 포함)</summary>
    unsafe void RenderScreen(Rectangle sr)
    {
        sr.Intersect(new Rectangle(0, 0, bw, bh));
        if (sr.Width <= 0 || sr.Height <= 0 || bits == null) return;
        var d = App.Doc;
        uint bg = 0xFF4B4B52;
        if (d == null || comp == null)
        {
            for (int y = sr.Top; y < sr.Bottom; y++) for (int x = sr.Left; x < sr.Right; x++) bits[y * bw + x] = bg;
            return;
        }
        double z = V.Zoom;
        int k = z >= 1 ? 1 : Math.Min(4, (int)Math.Ceiling(1 / z));
        // 화면 → 문서 아핀 변환 계수
        var (ox, oy) = V.ToDoc(0, 0); var (xx, xy) = V.ToDoc(1, 0); var (yx, yy) = V.ToDoc(0, 1);
        double ax = xx - ox, ay = xy - oy, bxv = yx - ox, byv = yy - oy;
        var sel = d.Sel.Mask;
        int W = d.W, H = d.H;
        for (int y = sr.Top; y < sr.Bottom; y++)
        {
            uint* row = bits + y * bw;
            for (int x = sr.Left; x < sr.Right; x++)
            {
                double dx = ox + ax * (x + 0.5) + bxv * (y + 0.5), dy = oy + ay * (x + 0.5) + byv * (y + 0.5);
                uint c;
                if (k == 1)
                {
                    int ix = (int)Math.Floor(dx), iy = (int)Math.Floor(dy);
                    c = (uint)ix >= (uint)W || (uint)iy >= (uint)H ? bg : Opaque(comp.Get(ix, iy));
                }
                else
                {
                    uint sb = 0, sg = 0, srr = 0; int n = 0, outside = 0;
                    for (int j = 0; j < k; j++) for (int i = 0; i < k; i++)
                    {
                        double fx = dx + ax * ((i + 0.5) / k - 0.5) + bxv * ((j + 0.5) / k - 0.5), fy = dy + ay * ((i + 0.5) / k - 0.5) + byv * ((j + 0.5) / k - 0.5);
                        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                        if ((uint)ix >= (uint)W || (uint)iy >= (uint)H) { outside++; continue; }
                        uint v = Opaque(comp.Get(ix, iy));
                        sb += v & 255; sg += (v >> 8) & 255; srr += (v >> 16) & 255; n++;
                    }
                    c = n == 0 || outside > n ? bg : 0xFF000000 | (srr / (uint)n << 16) | (sg / (uint)n << 8) | (sb / (uint)n);
                }
                // 선택 영역 테두리 (점선)
                if (sel != null)
                {
                    int ix = (int)Math.Floor(dx), iy = (int)Math.Floor(dy);
                    bool In(double fx, double fy) { int a = (int)Math.Floor(fx), b2 = (int)Math.Floor(fy); return (uint)a < (uint)W && (uint)b2 < (uint)H && sel[b2 * W + a] > 127; }
                    bool here = In(dx, dy);
                    if (here != In(dx + ax, dy + ay) || here != In(dx + bxv, dy + byv)) c = ((x + y) / 4 % 2 == 0) ? 0xFF000000 : 0xFFFFFFFF;
                }
                row[x] = c;
            }
        }
    }

    static uint Opaque(uint c) => (c >> 24) == 255 ? c : Px.Over(c, 0xFFFFFFFF) | 0xFF000000;   // 투명은 흰 바탕 위로 (용지를 끈 경우)

    Rectangle DocToScreen(Rect r)
    {
        if (r.IsEmpty) return Rectangle.Empty;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (px, py) in new[] { (r.X, r.Y), (r.R, r.Y), (r.X, r.B), (r.R, r.B) })
        {
            var (sx, sy) = V.ToScreen(px, py);
            x0 = Math.Min(x0, sx); y0 = Math.Min(y0, sy); x1 = Math.Max(x1, sx); y1 = Math.Max(y1, sy);
        }
        return Rectangle.FromLTRB((int)Math.Floor(x0) - 2, (int)Math.Floor(y0) - 2, (int)Math.Ceiling(x1) + 2, (int)Math.Ceiling(y1) + 2);
    }

    /// <summary>미뤄 둔 화면 영역 + 도구 겹침 그림을 버퍼에 그리고 바로 창에 올림</summary>
    public void Present(long inputQpc = 0)
    {
        if (!IsHandleCreated) return;
        EnsureBuffer();
        var dirty = pendingScreen;
        pendingScreen = Rectangle.Empty;
        if (!dirty.IsEmpty) RenderScreen(dirty);
        // 겹침 그림 (크기 원·도구 표시): 지난번 자리를 지우고 새로 그림
        var ov = OverlayRect();
        if (!lastOverlay.IsEmpty) RenderScreen(lastOverlay);
        // 늘 그리는 겹침 그림(Registry.Overlays)은 다시 그린 부분에만 다시 얹음
        var re = Rectangle.Union(Nz(dirty), Nz(lastOverlay));
        if (!re.IsEmpty && Registry.Overlays.Count > 0) { using var g0 = Graphics.FromHdc(memDC); g0.SetClip(re); DrawGlobalOverlays(g0); }
        if (!ov.IsEmpty)
        {
            using var g = Graphics.FromHdc(memDC);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            DrawOverlay(g);
        }
        var all = Rectangle.Union(Rectangle.Union(Nz(dirty), Nz(lastOverlay)), Nz(ov));
        if (all.IsEmpty) all = dirty.IsEmpty ? (lastOverlay.IsEmpty ? ov : lastOverlay) : dirty;
        lastOverlay = ov;
        all.Intersect(new Rectangle(0, 0, bw, bh));
        if (all.Width <= 0 || all.Height <= 0) return;
        IntPtr hdc = GetDC(Handle);
        BitBlt(hdc, all.X, all.Y, all.Width, all.Height, memDC, all.X, all.Y, SRCCOPY);
        ReleaseDC(Handle, hdc);
        GdiFlush();
        if (inputQpc > 0)
        {
            double ms = (Stopwatch.GetTimestamp() - inputQpc) * 1000.0 / Stopwatch.Frequency;
            if (ms >= 0 && ms < 500) LatencyMs = LatencyMs == 0 ? ms : LatencyMs * 0.9 + ms * 0.1;
        }
    }
    static Rectangle Nz(Rectangle r) => r.IsEmpty ? Rectangle.Empty : r;

    Rectangle OverlayRect()
    {
        var r = Rectangle.Empty;
        var t = App.Tool;
        if (t == null) return r;
        if (t.DrawsSizeCircle && hoverSX >= 0)
        {
            double rad = t.CircleSize / 2 * V.Zoom + 2;
            r = Rectangle.FromLTRB((int)(hoverSX - rad - 2), (int)(hoverSY - rad - 2), (int)(hoverSX + rad + 3), (int)(hoverSY + rad + 3));
        }
        var tb = t.OverlayBounds(V);
        if (!tb.IsEmpty) r = r.IsEmpty ? tb : Rectangle.Union(r, tb);
        return r;
    }

    void DrawOverlay(Graphics g)
    {
        var t = App.Tool;
        if (t.DrawsSizeCircle && hoverSX >= 0)
        {
            float rad = (float)(t.CircleSize / 2 * V.Zoom);
            if (rad > 2)
            {
                using var p1 = new Pen(Color.FromArgb(160, 0, 0, 0)); using var p2 = new Pen(Color.FromArgb(200, 255, 255, 255));
                g.DrawEllipse(p1, (float)hoverSX - rad, (float)hoverSY - rad, rad * 2, rad * 2);
                g.DrawEllipse(p2, (float)hoverSX - rad - 1, (float)hoverSY - rad - 1, rad * 2 + 2, rad * 2 + 2);
            }
        }
        t.DrawOverlay(g, V);
    }

    // ---- 보기 ----------------------------------------------------------------------
    public void Fit()
    {
        var d = App.Doc;
        if (d == null || !IsHandleCreated) return;
        EnsureBuffer();
        V.Angle = 0; V.Flip = false;
        V.Zoom = Math.Max(0.02, Math.Min((bw - 40.0) / d.W, (bh - 40.0) / d.H));
        V.PanX = (bw - d.W * V.Zoom) / 2; V.PanY = (bh - d.H * V.Zoom) / 2;
        RedrawAll();
    }
    public void RedrawAll()
    {
        if (comp == null) App.InvalidateAll();
        RenderScreen(new Rectangle(0, 0, bw, bh)); lastOverlay = Rectangle.Empty;
        if (dib != IntPtr.Zero && App.Doc != null)
        {
            using var g = Graphics.FromHdc(memDC);
            DrawGlobalOverlays(g);
            var ov = OverlayRect();
            if (!ov.IsEmpty && App.Tool != null) { g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; DrawOverlay(g); lastOverlay = ov; }
        }
        Invalidate(); ViewChanged?.Invoke();
    }
    void DrawGlobalOverlays(Graphics g)
    {
        if (App.Doc == null) return;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        foreach (var o in Registry.Overlays) { try { o(g, V); } catch { } }
    }
    public void ZoomAt(double f, double sx, double sy)
    {
        double nz = Math.Clamp(V.Zoom * f, 0.02, 64); f = nz / V.Zoom;
        V.PanX = sx - (sx - V.PanX) * f; V.PanY = sy - (sy - V.PanY) * f; V.Zoom = nz;
        RedrawAll();
    }
    public void SetZoom(double z) => ZoomAt(z / V.Zoom, bw / 2.0, bh / 2.0);
    public void RotateBy(double deg, double cx, double cy)
    {
        var (dx, dy) = V.ToDoc(cx, cy);
        V.Angle = ((V.Angle + deg) % 360 + 360) % 360;
        var (sx, sy) = V.ToScreen(dx, dy);
        V.PanX += cx - sx; V.PanY += cy - sy;
        RedrawAll();
    }
    public void FlipView() { double cx = bw / 2.0, cy = bh / 2.0; var (dx, dy) = V.ToDoc(cx, cy); V.Flip = !V.Flip; var (sx, sy) = V.ToScreen(dx, dy); V.PanX += cx - sx; V.PanY += cy - sy; RedrawAll(); }

    // ---- 입력 ----------------------------------------------------------------------
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
                if ((ModifierKeys & Keys.Alt) != 0) RotateBy(delta > 0 ? -15 : 15, p.X, p.Y);
                else if ((ModifierKeys & Keys.Shift) != 0) { V.PanX += delta / 2.0; RedrawAll(); }
                else ZoomAt(delta > 0 ? 1.15 : 1 / 1.15, p.X, p.Y);
                return;
            }
            case WM_POINTERACTIVATE: m.Result = (IntPtr)1; return;   // PA_ACTIVATE
        }
        base.WndProc(ref m);
    }

    PointerArgs Args(ref POINTER_PEN_INFO pp, int type, Point origin)
    {
        var info = pp.pointerInfo;
        ScreenPos(ref info, type == PT_PEN, out double sx, out double sy);
        sx -= origin.X; sy -= origin.Y;
        var (dx, dy) = V.ToDoc(sx, sy);
        bool contact = (info.pointerFlags & POINTER_FLAG_INCONTACT) != 0;
        bool barrel = (pp.penFlags & PEN_FLAG_BARREL) != 0 || (info.pointerFlags & POINTER_FLAG_SECONDBUTTON) != 0;
        bool eraser = (pp.penFlags & (PEN_FLAG_ERASER | PEN_FLAG_INVERTED)) != 0;
        int buttons;
        if (type == PT_PEN) buttons = (contact ? (eraser ? 32 : 1) : 0) | (barrel ? 2 : 0);
        else buttons = ((info.pointerFlags & POINTER_FLAG_FIRSTBUTTON) != 0 ? 1 : 0) | ((info.pointerFlags & POINTER_FLAG_SECONDBUTTON) != 0 ? 2 : 0) | ((info.pointerFlags & POINTER_FLAG_THIRDBUTTON) != 0 ? 4 : 0);
        double pr = type == PT_PEN ? ((pp.penMask & 1) != 0 ? pp.pressure / 1024.0 : contact ? 0.5 : 0) : App.Get("mousePressure", 1.0);
        var mods = ModifierKeys;
        return new PointerArgs
        {
            X = dx, Y = dy, SX = sx, SY = sy, Pressure = pr, Time = info.dwTime, Type = type == PT_PEN ? "pen" : type == PT_TOUCH ? "touch" : "mouse",
            Buttons = buttons, TiltX = (pp.penMask & 4) != 0 ? pp.tiltX : 0, TiltY = (pp.penMask & 8) != 0 ? pp.tiltY : 0, Twist = (pp.penMask & 2) != 0 ? pp.rotation : 0,
            Shift = (mods & Keys.Shift) != 0, Ctrl = (mods & Keys.Control) != 0, Alt = (mods & Keys.Alt) != 0,
        };
    }

    bool OnPointer(ref Message m)
    {
        uint id = (uint)((long)m.WParam & 0xFFFF);
        if (!GetPointerType(id, out int type)) return false;
        if (type == PT_TOUCH) { if (Environment.TickCount64 - lastPenTick > App.Get("palmGuardMs", 350)) OnTouch(ref m, id); return true; }
        if (m.Msg == WM_POINTERLEAVE) { hoverSX = -1; Present(); return true; }
        if (m.Msg == WM_POINTERCAPTURECHANGED) { if (drawing) EndDraw(null); return true; }
        uint n = 1;
        if (type == PT_PEN)
        {
            lastPenTick = Environment.TickCount64;
            n = (uint)hist.Length;
            if (!GetPointerPenInfoHistory(id, ref n, hist) || n == 0) { n = 1; if (!GetPointerPenInfo(id, out hist[0])) return false; }
            n = Math.Min(n, (uint)hist.Length);
        }
        else
        {
            if (!GetPointerInfo(id, out var pi)) return false;
            hist[0] = new POINTER_PEN_INFO { pointerInfo = pi };
        }
        var origin = PointToScreen(Point.Empty);
        long qpc = 0;
        for (int i = (int)n - 1; i >= 0; i--)
        {
            var a = Args(ref hist[i], type, origin);
            qpc = (long)hist[i].pointerInfo.PerformanceCount;
            if (type == PT_PEN) samples++;
            hoverSX = a.SX; hoverSY = a.SY;
            bool contact = (hist[i].pointerInfo.pointerFlags & POINTER_FLAG_INCONTACT) != 0;
            bool isUp = m.Msg == WM_POINTERUP && i == 0;
            if (contact && !isUp)
            {
                if (!drawing && !panning) BeginInput(a);
                else if (panning) PanMove(a);
                else App.Tool?.Move(a);
            }
            else
            {
                if (panning) { panning = false; Cursor = App.Tool?.Cursor ?? Cursors.Cross; }
                else if (drawing) EndDraw(a);
                else App.Tool?.Hover(a);
            }
        }
        if (m.Msg == WM_POINTERDOWN) { Capture = true; Focus(); }
        if (m.Msg == WM_POINTERUP) Capture = false;
        Present(qpc);
        long now = Stopwatch.GetTimestamp();
        if (now - secStart > Stopwatch.Frequency) { PenRate = samples; samples = 0; secStart = now; ViewChanged?.Invoke(); }
        m.Result = IntPtr.Zero;
        return true;
    }

    void BeginInput(PointerArgs a)
    {
        // 화면 조작: 스페이스(+Ctrl 확대 / +Shift 회전), 가운데 버튼, 화면 이동 도구
        if (SpaceDown || (a.Buttons & 4) != 0 || App.Tool?.Id == "hand")
        {
            panning = true; panSX = a.SX; panSY = a.SY; panX0 = V.PanX; panY0 = V.PanY; rotA0 = V.Angle; zoomS0 = V.Zoom;
            panMode = SpaceDown && CtrlDown ? "zoom" : SpaceDown && ShiftDown ? "rotate" : "pan";
            Cursor = Cursors.SizeAll;
            return;
        }
        // 마우스 오른쪽 = 스포이트 / 펜 측면 버튼 = 설정(스포이트) / 지우개 끝 = 지우개 / Alt = 스포이트
        string temp = null;
        if (a.EraserEnd) temp = "eraser";
        else if ((a.Buttons & 2) != 0) temp = App.Get("penButton", "eyedropper");
        else if (a.Alt && App.Tool?.Id != "eyedropper" && App.Tool?.AllowAltPick != false) temp = "eyedropper";
        if (temp == "pan") { panning = true; panSX = a.SX; panSY = a.SY; panX0 = V.PanX; panY0 = V.PanY; panMode = "pan"; return; }
        if (temp != null && Registry.Tools.Any(t => t.Id == temp)) { tempTool = App.Tool?.Id; App.SetTool(temp); }
        drawing = true;
        App.Tool?.Down(a);
    }

    void EndDraw(PointerArgs a)
    {
        drawing = false;
        if (a != null) App.Tool?.Up(a); else App.Tool?.Cancel();
        if (tempTool != null) { App.SetTool(tempTool); tempTool = null; }
    }

    void PanMove(PointerArgs a)
    {
        if (panMode == "zoom") { double f = Math.Exp((a.SX - panSX) * 0.01); V.Zoom = zoomS0; ZoomAt(f, panSX, panSY); return; }
        if (panMode == "rotate")
        {
            double cx = bw / 2.0, cy = bh / 2.0;
            double a0 = Math.Atan2(panSY - cy, panSX - cx), a1 = Math.Atan2(a.SY - cy, a.SX - cx);
            double target = rotA0 + (a1 - a0) * 180 / Math.PI;
            RotateBy(target - V.Angle, cx, cy);
            return;
        }
        V.PanX = panX0 + a.SX - panSX; V.PanY = panY0 + a.SY - panSY;
        RedrawAll();
    }

    void OnTouch(ref Message m, uint id)
    {
        if (!GetPointerInfo(id, out var pi)) return;
        var c = PointToClient(new Point(pi.ptPixelLocation.X, pi.ptPixelLocation.Y));
        if (m.Msg == WM_POINTERUP || m.Msg == WM_POINTERLEAVE || m.Msg == WM_POINTERCAPTURECHANGED) { touches.Remove(id); ResetPinch(); return; }
        bool isNew = !touches.ContainsKey(id);
        touches[id] = c;
        if (isNew) { ResetPinch(); return; }
        var (cen, d, ang) = PinchInfo();
        if (touches.Count == 1) { V.PanX = pinchPanX0 + cen.X - pinchC0.X; V.PanY = pinchPanY0 + cen.Y - pinchC0.Y; RedrawAll(); return; }
        double nz = Math.Clamp(pinchZoom0 * d / Math.Max(1, pinchD0), 0.02, 64);
        double rot = App.Get("touchRotate", true) ? ang - pinchA0 : 0;
        // 처음 상태에서: 두 손가락 가운데가 같은 문서 점을 가리키도록
        V.Zoom = pinchZoom0; V.Angle = pinchAngle0; V.PanX = pinchPanX0; V.PanY = pinchPanY0;
        var (dx, dy) = V.ToDoc(pinchC0.X, pinchC0.Y);
        V.Zoom = nz; V.Angle = ((pinchAngle0 + rot) % 360 + 360) % 360;
        var (sx, sy) = V.ToScreen(dx, dy);
        V.PanX += cen.X - sx; V.PanY += cen.Y - sy;
        RedrawAll();
    }
    void ResetPinch()
    {
        if (touches.Count == 0) return;
        var (cen, d, ang) = PinchInfo();
        pinchC0 = cen; pinchD0 = d; pinchA0 = ang; pinchZoom0 = V.Zoom; pinchAngle0 = V.Angle; pinchPanX0 = V.PanX; pinchPanY0 = V.PanY;
    }
    (PointF, float, float) PinchInfo()
    {
        float sx = 0, sy = 0; foreach (var p in touches.Values) { sx += p.X; sy += p.Y; }
        var cen = new PointF(sx / touches.Count, sy / touches.Count);
        if (touches.Count < 2) return (cen, 1, 0);
        var a = touches.Values.ElementAt(0); var b = touches.Values.ElementAt(1);
        return (cen, MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)), MathF.Atan2(b.Y - a.Y, b.X - a.X) * 180 / MathF.PI);
    }

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

    /// <summary>화면 점 → 문서 점 (도구가 씀)</summary>
    public (double x, double y) ScreenToDoc(double sx, double sy) => V.ToDoc(sx, sy);

    protected override void Dispose(bool disposing)
    {
        if (dib != IntPtr.Zero) { SelectObject(memDC, oldBmp); DeleteObject(dib); DeleteDC(memDC); dib = IntPtr.Zero; }
        base.Dispose(disposing);
    }

    // ---- Win32 -------------------------------------------------------------------
    const int WM_POINTERUPDATE = 0x245, WM_POINTERDOWN = 0x246, WM_POINTERUP = 0x247, WM_POINTERLEAVE = 0x24A, WM_POINTERACTIVATE = 0x24B, WM_POINTERCAPTURECHANGED = 0x24C, WM_MOUSEWHEEL = 0x20A;
    const int PT_TOUCH = 2, PT_PEN = 3;
    const int POINTER_FLAG_INCONTACT = 0x4, POINTER_FLAG_FIRSTBUTTON = 0x10, POINTER_FLAG_SECONDBUTTON = 0x20, POINTER_FLAG_THIRDBUTTON = 0x40;
    const int PEN_FLAG_BARREL = 1, PEN_FLAG_INVERTED = 2, PEN_FLAG_ERASER = 4;
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
