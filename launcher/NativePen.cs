// 네이티브 펜 입력: 펜이 그림판 위에 있을 때만, 그림판 영역을 거의 투명한(1/255) 자식 창으로 덮어
// Windows Ink 펜 입력(WM_POINTER)을 직접 받는다. 지난 메시지 이후의 모든 점(기록), 1픽셀보다 작은 위치,
// 필압(0~1024)·기울기·회전·측면 버튼·지우개를 그대로 화면(페이지)에 넘긴다.
// 펜이 떠나거나 마우스·손가락을 쓰면 바로 숨어서, 메뉴·패널·마우스·터치는 지금처럼 웹 화면이 직접 받는다.
// 펜을 길게 눌러 오른쪽 클릭·탭 물결 같은 Windows 펜 동작은 이 창에서 끈다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

class PenOverlay : NativeWindow
{
    readonly Form form;
    readonly WebView2 web;
    RectangleF stageCss = RectangleF.Empty;          // 그림판 영역 (페이지 CSS 픽셀, 웹 화면 기준)
    List<RectangleF> excl = new List<RectangleF>();  // 그림판 위에 떠 있는 버튼 등 (덮지 않음)
    bool wanted;                                     // 페이지가 켜 달라고 한 상태
    bool shown;
    Point shownAt;                                   // 덮개를 켤 때의 커서 위치 (Windows 가 보내는 가짜 이동 걸러내기)
    IntPtr cursor = IntPtr.Zero;
    bool ownCursor;
    readonly Dictionary<IntPtr, RectangleF[]> devRects = new Dictionary<IntPtr, RectangleF[]>();
    readonly POINTER_PEN_INFO[] hist = new POINTER_PEN_INFO[64];
    // Wintab (설정에서 켰을 때만)
    Wintab.Ctx wt;
    bool wtMode, wtDown, wtProx;
    readonly Wintab.PACKET[] wtBuf = new Wintab.PACKET[128];
    public static bool Disabled = Environment.GetEnvironmentVariable("COMICPAD_NO_NATIVE_PEN") == "1";

    public PenOverlay(Form f, WebView2 w)
    {
        form = f; web = w;
        var cp = new CreateParams();
        cp.Parent = form.Handle;
        cp.Style = WS_CHILD | WS_CLIPSIBLINGS;
        cp.ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE;
        cp.X = 0; cp.Y = 0; cp.Width = 1; cp.Height = 1;
        CreateHandle(cp);
        SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);   // 1/255: 눈에는 안 보이지만 입력은 받음
        // Windows 펜 동작(길게 눌러 오른쪽 클릭, 탭·더블탭 표시, 측면 버튼 표시) 끄기
        foreach (int k in new[] { 2, 3, 4, 5, 6 }) { int no = 0; SetWindowFeedbackSetting(Handle, k, 0, 4, ref no); }
        cursor = LoadCursor(IntPtr.Zero, IDC_CROSS);
        form.Resize += (s, e) => Place();
    }

    double Scale { get { return form.DeviceDpi / 96.0 * web.ZoomFactor; } }

    // ---- 페이지에서 온 메시지 -------------------------------------------------
    public void OnMessage(string m)
    {
        if (Disabled || m == null || !m.StartsWith("pen:")) return;
        try
        {
            string[] p = m.Split('|');
            switch (p[0])
            {
                case "pen:on": wanted = true; if (p.Length > 1) stageCss = ParseRect(p[1]); if (p.Length > 2) excl = ParseRects(p[2]); Place(); break;
                case "pen:off": wanted = false; Hide(false); break;
                case "pen:rect": if (p.Length > 1) stageCss = ParseRect(p[1]); if (p.Length > 2) excl = ParseRects(p[2]); if (shown) Place(); break;
                case "pen:cursor": SetPageCursor(p); break;
                case "pen:wintab":
                    if (p.Length > 1 && p[1] == "1")
                    {
                        if (p.Length > 2) stageCss = ParseRect(p[2]);
                        if (p.Length > 3) excl = ParseRects(p[3]);
                        if (wt == null) wt = Wintab.Open(Handle);
                        wtMode = wt != null;
                        if (!wtMode) Post("{\"t\":\"pen\",\"k\":\"wintab-fail\"}");
                    }
                    else { Wintab.Close(wt); wt = null; wtMode = false; Hide(true); }
                    break;
            }
        }
        catch { }
    }

    static RectangleF ParseRect(string s)
    {
        if (string.IsNullOrEmpty(s)) return RectangleF.Empty;
        string[] v = s.Split(',');
        Func<int, float> f = i => float.Parse(v[i], CultureInfo.InvariantCulture);
        return new RectangleF(f(0), f(1), f(2), f(3));
    }
    static List<RectangleF> ParseRects(string s)
    {
        var l = new List<RectangleF>();
        if (string.IsNullOrEmpty(s)) return l;
        foreach (string r in s.Split(';')) if (r.Length > 0) l.Add(ParseRect(r));
        return l;
    }

    Rectangle ToForm(RectangleF r)
    {
        double k = Scale;
        Point o = web.Location;
        int x0 = (int)Math.Floor(o.X + r.X * k), y0 = (int)Math.Floor(o.Y + r.Y * k);
        int x1 = (int)Math.Ceiling(o.X + (r.X + r.Width) * k), y1 = (int)Math.Ceiling(o.Y + (r.Y + r.Height) * k);
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    void Place()
    {
        if (!(wanted || wtMode) || stageCss.Width < 4 || stageCss.Height < 4 || form.WindowState == FormWindowState.Minimized) { Hide(false); return; }
        Rectangle r = ToForm(stageCss);
        // 떠 있는 버튼 등은 구멍을 내서 그 자리는 웹 화면이 직접 받게
        IntPtr rgn = CreateRectRgn(0, 0, r.Width, r.Height);
        foreach (var e in excl)
        {
            Rectangle q = ToForm(e);
            q.Offset(-r.X, -r.Y);
            q.Inflate(2, 2);
            IntPtr hole = CreateRectRgn(q.Left, q.Top, q.Right, q.Bottom);
            CombineRgn(rgn, rgn, hole, RGN_DIFF);
            DeleteObject(hole);
        }
        if (!shown) shownAt = Cursor.Position;
        SetWindowPos(Handle, HWND_TOP, r.X, r.Y, r.Width, r.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetWindowRgn(Handle, rgn, true);   // 창이 rgn 을 가져가므로 지우지 않음
        shown = true;
    }

    void Hide(bool tell)
    {
        if (shown) ShowWindow(Handle, SW_HIDE);
        bool was = shown;
        shown = false;
        if (tell && was) Post("{\"t\":\"pen\",\"k\":\"hidden\"}");
    }

    void Post(string json)
    {
        try { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(json); } catch { }
    }

    // ---- 커서: 페이지가 정한 펜촉·십자 그림을 실제 커서로 ------------------------
    void SetPageCursor(string[] p)
    {
        // pen:cursor|kw|crosshair   또는   pen:cursor|png|hx,hy|base64
        IntPtr next = IntPtr.Zero; bool own = false;
        if (p.Length > 2 && p[1] == "kw")
        {
            int id = IDC_ARROW;
            switch (p[2]) { case "crosshair": id = IDC_CROSS; break; case "text": id = IDC_IBEAM; break; case "grab": case "grabbing": case "move": id = IDC_SIZEALL; break; case "pointer": id = IDC_HAND; break; }
            next = LoadCursor(IntPtr.Zero, id);
        }
        else if (p.Length > 3 && p[1] == "png")
        {
            string[] hs = p[2].Split(',');
            int hx = (int)float.Parse(hs[0], CultureInfo.InvariantCulture), hy = (int)float.Parse(hs[1], CultureInfo.InvariantCulture);
            using (var ms = new MemoryStream(Convert.FromBase64String(p[3])))
            using (var bmp = new Bitmap(ms))
            {
                // 투명도를 살리려면 아이콘으로 만든 뒤 끝점(핫스폿)만 바꿔 커서로
                IntPtr hicon = bmp.GetHicon();
                ICONINFO ii;
                if (GetIconInfo(hicon, out ii))
                {
                    ii.fIcon = false; ii.xHotspot = hx; ii.yHotspot = hy;
                    next = CreateIconIndirect(ref ii);
                    DeleteObject(ii.hbmMask); DeleteObject(ii.hbmColor);
                    own = true;
                }
                DestroyIcon(hicon);
            }
        }
        if (next == IntPtr.Zero) return;
        IntPtr old = cursor; bool oldOwn = ownCursor;
        cursor = next; ownCursor = own;
        if (shown) SetCursor(cursor);
        if (oldOwn && old != IntPtr.Zero) DestroyCursor(old);
    }

    // ---- 펜 입력 ------------------------------------------------------------
    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE: m.Result = (IntPtr)MA_NOACTIVATE; return;
            case WM_POINTERACTIVATE: m.Result = (IntPtr)PA_NOACTIVATE; return;
            case WM_SETCURSOR: SetCursor(cursor); m.Result = (IntPtr)1; return;
            case WM_MOUSEMOVE: case WM_LBUTTONDOWN: case WM_RBUTTONDOWN: case WM_MBUTTONDOWN: case WM_MOUSEWHEEL:
                // 진짜 마우스일 때만 비켜 줌: 펜·터치가 흉내 낸 마우스 메시지, 창이 나타날 때 Windows 가 보내는 가짜 이동(위치 그대로)은 무시
                if (((long)GetMessageExtraInfo() & 0xFFFFFF00) == 0xFF515700) return;
                if (wtMode && (wtProx || wtDown)) return;
                if (m.Msg == WM_MOUSEMOVE && Cursor.Position == shownAt) return;
                Hide(true); return;
            case WM_POINTERDOWN: case WM_POINTERUPDATE: case WM_POINTERUP: case WM_POINTERLEAVE: case WM_POINTERCAPTURECHANGED:
                if (wtMode)
                {
                    // Wintab 으로 받는 중: Windows Ink 펜 메시지는 버림 (두 번 그려지지 않게)
                    int pt; uint pid = (uint)((long)m.WParam & 0xFFFF);
                    if (GetPointerType(pid, out pt) && pt == PT_PEN) { m.Result = IntPtr.Zero; return; }
                }
                if (HandlePointer(ref m)) return;
                break;
            case Wintab.WT_PACKET:
                if (wtMode) { WintabPackets(); m.Result = IntPtr.Zero; return; }
                break;
            case Wintab.WT_PROXIMITY:
                if (wtMode)
                {
                    wtProx = ((long)m.LParam & 0xFFFF) != 0;
                    if (!wtProx)
                    {
                        if (wtDown) { wtDown = false; ReleaseCapture(); }
                        Post("{\"t\":\"pen\",\"k\":\"leave\",\"id\":900}");
                        Hide(true);
                    }
                    m.Result = IntPtr.Zero; return;
                }
                break;
        }
        base.WndProc(ref m);
    }

    bool HandlePointer(ref Message m)
    {
        uint id = (uint)((long)m.WParam & 0xFFFF);
        int type;
        if (!GetPointerType(id, out type)) return false;
        if (type != PT_PEN) { Hide(true); m.Result = IntPtr.Zero; return true; }   // 손가락: 비켜 줌 (다음 터치부터 웹이 받음)
        if (m.Msg == WM_POINTERLEAVE || m.Msg == WM_POINTERCAPTURECHANGED)
        {
            Post("{\"t\":\"pen\",\"k\":\"leave\",\"id\":" + id + "}");
            Hide(true);
            m.Result = IntPtr.Zero; return true;
        }
        uint n = (uint)hist.Length;
        if (!GetPointerPenInfoHistory(id, ref n, hist) || n == 0)
        {
            n = 1;
            if (!GetPointerPenInfo(id, out hist[0])) return false;
        }
        if (n > hist.Length) n = (uint)hist.Length;
        string kind = m.Msg == WM_POINTERDOWN ? "down" : m.Msg == WM_POINTERUP ? "up" : "move";
        var sb = new StringBuilder(64 + (int)n * 64);
        sb.Append("{\"t\":\"pen\",\"k\":\"").Append(kind).Append("\",\"id\":").Append(id);
        var o = web.PointToScreen(Point.Empty);
        double k = Scale;
        sb.Append(",\"e\":[");
        // 기록은 최신이 앞: 오래된 것부터
        for (int i = (int)n - 1; i >= 0; i--)
        {
            var pi = hist[i];
            var info = pi.pointerInfo;
            double sx, sy;
            ScreenPos(ref info, out sx, out sy);
            double cx = (sx - o.X) / k, cy = (sy - o.Y) / k;
            bool contact = (info.pointerFlags & POINTER_FLAG_INCONTACT) != 0;
            bool barrel = (pi.penFlags & PEN_FLAG_BARREL) != 0 || (info.pointerFlags & POINTER_FLAG_SECONDBUTTON) != 0;
            bool eraser = (pi.penFlags & (PEN_FLAG_ERASER | PEN_FLAG_INVERTED)) != 0;
            int buttons = (contact ? (eraser ? 32 : 1) : 0) | (barrel ? 2 : 0);
            double pr = (pi.penMask & PEN_MASK_PRESSURE) != 0 ? pi.pressure / 1024.0 : (contact ? 0.5 : 0);
            if (kind == "up" && i == 0) { buttons = barrel ? 2 : 0; pr = 0; }
            if (i != (int)n - 1) sb.Append(',');
            sb.Append('[').Append(F(cx)).Append(',').Append(F(cy)).Append(',').Append(F(pr))
              .Append(',').Append((pi.penMask & PEN_MASK_TILT_X) != 0 ? pi.tiltX : 0)
              .Append(',').Append((pi.penMask & PEN_MASK_TILT_Y) != 0 ? pi.tiltY : 0)
              .Append(',').Append((pi.penMask & PEN_MASK_ROTATION) != 0 ? pi.rotation : 0)
              .Append(',').Append(buttons).Append(',').Append(info.dwTime).Append(']');
        }
        int button = -1;
        var last = hist[0];
        if (kind != "move") button = (last.penFlags & (PEN_FLAG_ERASER | PEN_FLAG_INVERTED)) != 0 ? 5 : ((last.pointerInfo.pointerFlags & POINTER_FLAG_SECONDBUTTON) != 0 ? 2 : 0);
        sb.Append("],\"b\":").Append(button).Append('}');
        Post(sb.ToString());
        m.Result = IntPtr.Zero;   // 처리함: 마우스 흉내·길게 눌러 오른쪽 클릭 없음
        return true;
    }

    static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

    // ---- Wintab 패킷 → 페이지 ------------------------------------------------
    void WintabPackets()
    {
        int n = Wintab.Get(wt, wtBuf);
        if (n <= 0) return;
        var o = web.PointToScreen(Point.Empty);
        double k = Scale;
        var sb = new StringBuilder();
        int count = 0;
        Action<string> flush = kind =>
        {
            if (count == 0) return;
            Post("{\"t\":\"pen\",\"k\":\"" + kind + "\",\"id\":900,\"e\":[" + sb + "],\"b\":" + (kind == "move" ? -1 : 0) + "}");
            sb.Length = 0; count = 0;
        };
        for (int i = 0; i < n; i++)
        {
            var pk = wtBuf[i];
            double sx = wt.sysOrgX + pk.x / (double)Wintab.SUB, sy = wt.sysOrgY + pk.y / (double)Wintab.SUB;
            double cx = (sx - o.X) / k, cy = (sy - o.Y) / k;
            bool tip = (pk.buttons & 1) != 0;
            bool eraser = pk.cursor % 3 == 2;
            bool barrel = (pk.buttons & 6) != 0;
            // 그림판 밖(패널 등)에서는 덮개를 치워 웹 화면이 받게, 그림판 위로 오면 다시 덮기
            bool inside = stageCss.Contains((float)cx, (float)cy);
            foreach (var e in excl) if (e.Contains((float)cx, (float)cy)) inside = false;
            if (!wtDown && !tip && !inside) { flush("move"); if (shown) Hide(true); continue; }
            if (!shown && inside) { Place(); if (!shown) continue; }
            double pr = Math.Min(1, pk.pressure / (double)Math.Max(1, wt.maxPressure));
            double alt = Math.Max(1, pk.altitude) / 10.0 * Math.PI / 180, az = pk.azimuth / 10.0 * Math.PI / 180;
            double cot = alt >= Math.PI / 2 - 0.001 ? 0 : 1 / Math.Tan(alt);
            int tx = (int)Math.Round(Math.Atan(Math.Sin(az) * cot) * 180 / Math.PI), ty = (int)Math.Round(Math.Atan(-Math.Cos(az) * cot) * 180 / Math.PI);
            int buttons = (tip ? (eraser ? 32 : 1) : 0) | (barrel ? 2 : 0);
            string smp = "[" + F(cx) + "," + F(cy) + "," + F(tip ? Math.Max(pr, 0.001) : 0) + "," + tx + "," + ty + "," + (pk.twist / 10) + "," + buttons + "," + pk.time + "]";
            if (tip && !wtDown)
            {
                flush("move");
                sb.Append(smp); count = 1; flush("down");
                wtDown = true; SetCapture(Handle);
                continue;
            }
            if (!tip && wtDown)
            {
                flush("move");
                sb.Append(smp); count = 1; flush("up");
                wtDown = false; ReleaseCapture();
                continue;
            }
            if (count > 0) sb.Append(',');
            sb.Append(smp); count++;
        }
        flush("move");
    }

    // 1픽셀보다 작은 위치: 펜 장치 좌표(HIMETRIC)를 화면 픽셀로
    void ScreenPos(ref POINTER_INFO info, out double x, out double y)
    {
        x = info.ptPixelLocation.X; y = info.ptPixelLocation.Y;
        RectangleF[] r;
        if (!devRects.TryGetValue(info.sourceDevice, out r))
        {
            RECT dev, disp;
            r = GetPointerDeviceRects(info.sourceDevice, out dev, out disp) && dev.Right > dev.Left && dev.Bottom > dev.Top
                ? new[] { RectangleF.FromLTRB(dev.Left, dev.Top, dev.Right, dev.Bottom), RectangleF.FromLTRB(disp.Left, disp.Top, disp.Right, disp.Bottom) }
                : null;
            devRects[info.sourceDevice] = r;
        }
        if (r == null) return;
        double hx = r[1].Left + (info.ptHimetricLocation.X - r[0].Left) * r[1].Width / r[0].Width;
        double hy = r[1].Top + (info.ptHimetricLocation.Y - r[0].Top) * r[1].Height / r[0].Height;
        // 장치 매핑이 이상하면(회전 화면 등) 정수 위치를 그대로
        if (Math.Abs(hx - x) <= 1.5 && Math.Abs(hy - y) <= 1.5) { x = hx; y = hy; }
    }

    // ---- Win32 --------------------------------------------------------------
    const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    const int LWA_ALPHA = 2, RGN_DIFF = 4, SW_HIDE = 0;
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    const int WM_SETCURSOR = 0x20, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_MOUSEWHEEL = 0x20A;
    const int WM_POINTERUPDATE = 0x245, WM_POINTERDOWN = 0x246, WM_POINTERUP = 0x247, WM_POINTERLEAVE = 0x24A, WM_POINTERACTIVATE = 0x24B, WM_POINTERCAPTURECHANGED = 0x24C, PA_NOACTIVATE = 3;
    const int PT_PEN = 3;
    const int POINTER_FLAG_INCONTACT = 0x4, POINTER_FLAG_SECONDBUTTON = 0x20;
    const int PEN_FLAG_BARREL = 1, PEN_FLAG_INVERTED = 2, PEN_FLAG_ERASER = 4;
    const int PEN_MASK_PRESSURE = 1, PEN_MASK_ROTATION = 2, PEN_MASK_TILT_X = 4, PEN_MASK_TILT_Y = 8;
    const int IDC_ARROW = 32512, IDC_IBEAM = 32513, IDC_CROSS = 32515, IDC_SIZEALL = 32646, IDC_HAND = 32649;

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
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, int key, byte alpha, int flags);
    [DllImport("user32.dll")] static extern bool SetWindowFeedbackSetting(IntPtr h, int feedback, int flags, int size, ref int config);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] static extern IntPtr GetMessageExtraInfo();
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DestroyCursor(IntPtr c);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr c);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, int id);
    [DllImport("user32.dll")] static extern bool GetPointerType(uint id, out int type);
    [DllImport("user32.dll")] static extern bool GetPointerPenInfo(uint id, out POINTER_PEN_INFO info);
    [DllImport("user32.dll")] static extern bool GetPointerPenInfoHistory(uint id, ref uint count, [In, Out] POINTER_PEN_INFO[] info);
    [DllImport("user32.dll")] static extern bool GetPointerDeviceRects(IntPtr device, out RECT pointerDeviceRect, out RECT displayRect);
}
