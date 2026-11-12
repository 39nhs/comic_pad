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
    readonly Modules.Gaps.PenPredict predict = new();   // 펜 위치 예측 (penFeel.predict, 화면 표시만)
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

    // ---- 확장 지점 (Shell2 모듈: 원형 메뉴·브러시 커서 모양 등) ----------------------------------
    /// <summary>도구가 아닌 측면 버튼·오른쪽 버튼 동작(예: "radial")을 받아 처리하면, 그 접촉의 이동·뗌을 넘겨받을 것을 돌려줌</summary>
    public interface IPointerGesture { void Move(PointerArgs a); void Up(PointerArgs a); }
    public static Func<string, PointerArgs, IPointerGesture> TempAction;
    /// <summary>도구 커서 바꾸기 (설정 brushCursor). null 이면 도구가 정한 커서</summary>
    public static Func<ToolBase, Cursor> CursorOverride;
    public static Cursor CursorOf(ToolBase t) => (t == null ? null : CursorOverride?.Invoke(t)) ?? t?.Cursor ?? Cursors.Cross;
    IPointerGesture gesture;

    public CanvasView()
    {
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        App.Invalidated += r => { Recomposite(r); Present(); };
        App.ToolChanged += () => { Cursor = CursorOf(App.Tool); Present(); };
        App.SettingsChanged += () => { if (!SpaceDown && !panning) Cursor = CursorOf(App.Tool); };
        longPress.Tick += (_, _) => LongPressTick();
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

    /// <summary>true 면 축소해도 평균 내지 않고 가장 가까운 점 (픽셀 아트 보기)</summary>
    public static Func<Document, bool> CrispView;

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
        int k = z >= 1 || CrispView?.Invoke(d) == true ? 1 : Math.Min(4, (int)Math.Ceiling(1 / z));
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
        var pb = drawing ? predict.Bounds(t.CircleSize * V.Zoom * 0.6) : Rectangle.Empty;
        if (!pb.IsEmpty) r = r.IsEmpty ? pb : Rectangle.Union(r, pb);
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
        if (drawing) predict.Draw(g, t.CircleSize * V.Zoom * 0.6, App.Fg);
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
                else if ((ModifierKeys & Keys.Control) == 0 && App.Get("wheelMode", "zoom") == "scroll") { V.PanY += delta / 2.0; RedrawAll(); }   // 웹툰: 휠 = 위아래 스크롤 (Ctrl+휠 = 확대)
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
        if (type == PT_PEN) buttons = (contact ? (eraser ? 32 : 1) : 0) | (barrel ? 2 : 0) | ((info.pointerFlags & POINTER_FLAG_THIRDBUTTON) != 0 ? 4 : 0);   // 4 = 두 번째 측면 버튼 (드라이버가 알려 줄 때)
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
        if (type == PT_PEN && touchGesture != null) { touches.Clear(); EndTouchGesture(false); }   // 펜이 닿으면 손가락 제스처는 끝
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
                else if (gesture != null) gesture.Move(a);
                else
                {
                    App.Tool?.Move(a);
                    if (type == PT_PEN && Modules.Gaps.PenPredict.ForTool(App.Tool) && Modules.Gaps.PenPredict.Enabled()) predict.Add(a.SX, a.SY, (long)hist[i].pointerInfo.PerformanceCount);
                }
            }
            else
            {
                if (panning) { panning = false; Cursor = CursorOf(App.Tool); }
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
        predict.Reset();
        // 펜 두 번째 측면 버튼 (설정 penButton2, 웹 판과 같음: 기본 화면 이동)
        string b2 = a.Type == "pen" && (a.Buttons & 6) == 4 ? Modules.Gaps.PenPredict.Button2Action() : null;
        if (b2 == "none") return;
        // 화면 조작: 스페이스(+Ctrl 확대 / +Shift 회전), 가운데 버튼, 화면 이동 도구
        if (SpaceDown || ((a.Buttons & 4) != 0 && (b2 == null || b2 == "pan")) || App.Tool?.Id == "hand")
        {
            panning = true; panSX = a.SX; panSY = a.SY; panX0 = V.PanX; panY0 = V.PanY; rotA0 = V.Angle; zoomS0 = V.Zoom;
            panMode = SpaceDown && CtrlDown ? "zoom" : SpaceDown && ShiftDown ? "rotate" : "pan";
            Cursor = Cursors.SizeAll;
            return;
        }
        // 마우스 오른쪽 = 스포이트 / 펜 측면 버튼 = 설정(스포이트) / 지우개 끝 = 지우개 / Alt = 스포이트
        string temp = null;
        if (b2 != null) temp = b2;
        else if (a.EraserEnd) temp = "eraser";
        else if ((a.Buttons & 2) != 0 && a.Type == "mouse") temp = App.Get("mouseRadial", true) && TempAction != null ? "radial" : App.Get("penButton", "eyedropper");   // 마우스 오른쪽 = 원형 메뉴 (웹 판과 같음)
        else if ((a.Buttons & 2) != 0) temp = App.Get("penButton", "eyedropper");
        else if (a.Alt && App.Tool?.Id != "eyedropper" && App.Tool?.AllowAltPick != false) temp = "eyedropper";
        if (temp == "none" && (a.Buttons & 2) != 0) return;
        if (temp == "pan") { panning = true; panSX = a.SX; panSY = a.SY; panX0 = V.PanX; panY0 = V.PanY; panMode = "pan"; return; }
        if (temp != null && !Registry.Tools.Any(t => t.Id == temp) && TempAction?.Invoke(temp, a) is IPointerGesture g) { gesture = g; drawing = true; return; }
        if (temp != null && Registry.Tools.Any(t => t.Id == temp)) { tempTool = App.Tool?.Id; App.SetTool(temp); }
        drawing = true;
        App.Tool?.Down(a);
    }

    void EndDraw(PointerArgs a)
    {
        drawing = false;
        predict.Reset();
        if (gesture != null) { var g = gesture; gesture = null; g.Up(a); return; }
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

    // ---- 손가락 제스처 (웹 core/input.js 와 같은 규칙): 두 손가락 탭 = 실행취소, 세 손가락 탭 = 다시실행, 네 손가락 탭 = 패널 숨기기,
    //      한 손가락 길게 = 스포이트, 손가락으로 그리기(설정 fingerDraw) 켜면 한 손가락은 그리기
    sealed class TouchGesture { public long T0; public int Max; public bool Moved, Used; public readonly Dictionary<uint, PointF> Start = new(); }
    TouchGesture touchGesture;
    uint fingerDrawId; bool fingerDrawing;
    readonly System.Windows.Forms.Timer longPress = new() { Interval = 550 };
    /// <summary>제스처로 실행할 명령 (Shell2 가 연결, 없으면 메뉴 경로로)</summary>
    public static Action<string> GestureCommand;

    void RunGesture(string webId, string path)
    {
        if (GestureCommand != null) { GestureCommand(webId); return; }
        if (Registry.Commands.TryGetValue(path, out var run) && run != null) MainForm.Run(run, path);
    }

    void LongPressTick()
    {
        longPress.Stop();
        var g = touchGesture;
        if (g == null || g.Moved || touches.Count != 1 || fingerDrawing) return;
        var p = touches.Values.First();
        var t = Registry.Tools.Find(x => x.Id == "eyedropper");
        if (t == null) return;
        var (dx, dy) = V.ToDoc(p.X, p.Y);
        var a = new PointerArgs { X = dx, Y = dy, SX = p.X, SY = p.Y, Type = "touch", Buttons = 1, Pressure = 1 };
        try { t.Down(a); t.Up(a); } catch { }
        g.Used = true;
        Present();
    }

    void EndTouchGesture(bool allowTap)
    {
        var g = touchGesture; touchGesture = null; longPress.Stop();
        if (g == null || !allowTap) return;
        long dt = Environment.TickCount64 - g.T0;
        if (g.Moved || g.Used || dt >= 320) return;
        if (g.Max == 2 && App.Get("twoFingerUndo", true)) RunGesture("edit.undo", "편집/실행취소");
        else if (g.Max == 3 && App.Get("threeFingerRedo", true)) RunGesture("edit.redo", "편집/다시실행");
        else if (g.Max == 4) RunGesture("view.toggleUI", "보기/패널 모두 숨기기∕보이기");
    }

    PointerArgs TouchArgs(Point c, int buttons) { var (dx, dy) = V.ToDoc(c.X, c.Y); var mods = ModifierKeys; return new PointerArgs { X = dx, Y = dy, SX = c.X, SY = c.Y, Pressure = 1, Type = "touch", Buttons = buttons, Time = Environment.TickCount64, Shift = (mods & Keys.Shift) != 0, Ctrl = (mods & Keys.Control) != 0, Alt = (mods & Keys.Alt) != 0 }; }

    void OnTouch(ref Message m, uint id)
    {
        if (!GetPointerInfo(id, out var pi)) return;
        var c = PointToClient(new Point(pi.ptPixelLocation.X, pi.ptPixelLocation.Y));
        bool up = m.Msg == WM_POINTERUP || m.Msg == WM_POINTERLEAVE || m.Msg == WM_POINTERCAPTURECHANGED;
        // 손가락으로 그리는 중
        if (fingerDrawing && id == fingerDrawId)
        {
            if (up) { fingerDrawing = false; EndDraw(TouchArgs(c, 0)); Present(); }
            else { App.Tool?.Move(TouchArgs(c, 1)); Present(); }
            return;
        }
        if (up)
        {
            touches.Remove(id); ResetPinch();
            if (touches.Count == 0) EndTouchGesture(true);
            return;
        }
        bool isNew = !touches.ContainsKey(id);
        if (isNew && fingerDrawing)
        {
            // 손가락으로 그리던 중 두 번째 손가락 → 그리기 취소하고 제스처로
            fingerDrawing = false; drawing = false; App.Tool?.Cancel();
            if (tempTool != null) { App.SetTool(tempTool); tempTool = null; }
            touches[fingerDrawId] = touches.TryGetValue(fingerDrawId, out var fp) ? fp : c;
        }
        else if (isNew && touches.Count == 0 && App.Get("fingerDraw", false) && !drawing && !panning)
        {
            fingerDrawing = true; fingerDrawId = id;
            BeginInput(TouchArgs(c, 1));
            if (panning) { fingerDrawing = false; panning = false; } else { Capture = true; Present(); return; }
        }
        touches[id] = c;
        if (isNew)
        {
            if (touchGesture == null) { touchGesture = new TouchGesture { T0 = Environment.TickCount64 }; if (touches.Count == 1 && App.Get("longPressEyedropper", true) && !App.Get("fingerDraw", false)) { longPress.Stop(); longPress.Start(); } }
            touchGesture.Start[id] = c;
            touchGesture.Max = Math.Max(touchGesture.Max, touches.Count);
            if (touches.Count > 1) longPress.Stop();
            ResetPinch(); return;
        }
        if (touchGesture != null && touchGesture.Start.TryGetValue(id, out var s0) && Math.Abs(c.X - s0.X) + Math.Abs(c.Y - s0.Y) > 12) { touchGesture.Moved = true; longPress.Stop(); }
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
