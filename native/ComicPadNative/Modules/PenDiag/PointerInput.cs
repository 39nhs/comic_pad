// 펜 진단의 입력: 그림판(UI/CanvasView.OnPointer·Args·ScreenPos)과 같은 방법으로 Windows 포인터 메시지를 읽는다.
//  - 펜: GetPointerPenInfoHistory (64칸) 로 지난 메시지 이후 묶여 온 점 전부, 실패하면 GetPointerPenInfo 한 점
//  - 마우스·터치: GetPointerInfo 한 점 (그림판과 같음)
//  - 위치: HIMETRIC 위치를 장치 사각형으로 화면 픽셀에 옮겨 1픽셀보다 작은 값까지 (정수 위치와 1.5px 넘게 다르면 정수 위치)
//  - 시각: 점마다 PerformanceCount (QPC). 없으면 dwTime
// Decode 는 Win32 를 부르지 않아서, 시험에서 만든 POINTER_PEN_INFO 배열을 그대로 넣어 볼 수 있다.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ComicPadNative.Modules.PenDiagnosis;

public static class PointerInput
{
    public const int WM_POINTERUPDATE = 0x245, WM_POINTERDOWN = 0x246, WM_POINTERUP = 0x247, WM_POINTERLEAVE = 0x24A, WM_POINTERACTIVATE = 0x24B, WM_POINTERCAPTURECHANGED = 0x24C;
    public const int PT_TOUCH = 2, PT_PEN = 3, PT_MOUSE = 4;
    public const int POINTER_FLAG_INCONTACT = 0x4, POINTER_FLAG_FIRSTBUTTON = 0x10, POINTER_FLAG_SECONDBUTTON = 0x20, POINTER_FLAG_THIRDBUTTON = 0x40;
    public const int PEN_FLAG_BARREL = 1, PEN_FLAG_INVERTED = 2, PEN_FLAG_ERASER = 4;
    public const int PEN_MASK_PRESSURE = 1, PEN_MASK_ROTATION = 2, PEN_MASK_TILT_X = 4, PEN_MASK_TILT_Y = 8;

    public static double QpcMs(long qpc) => qpc * 1000.0 / Stopwatch.Frequency;
    public static double NowMs() => QpcMs(Stopwatch.GetTimestamp());

    static readonly POINTER_PEN_INFO[] hist = new POINTER_PEN_INFO[64];   // 그림판과 같은 크기
    static readonly Dictionary<IntPtr, RectangleF[]> devRects = new();

    /// <summary>메시지의 포인터 id 로 점들을 읽음 (못 읽으면 null)</summary>
    public static PenFrame Read(uint id, int msg, Point origin, double mousePressure)
    {
        long recv = Stopwatch.GetTimestamp();
        if (!GetPointerType(id, out int type)) return null;
        uint n = 1;
        if (type == PT_PEN)
        {
            n = (uint)hist.Length;
            if (!GetPointerPenInfoHistory(id, ref n, hist) || n == 0) { n = 1; if (!GetPointerPenInfo(id, out hist[0])) return null; }
            n = Math.Min(n, (uint)hist.Length);
        }
        else
        {
            if (!GetPointerInfo(id, out var pi)) return null;
            hist[0] = new POINTER_PEN_INFO { pointerInfo = pi };
        }
        return Decode(hist, (int)n, type, msg, origin, DeviceRects, mousePressure, recv);
    }

    /// <summary>장치 사각형 (HIMETRIC, 화면) — 장치마다 한 번 물어봄</summary>
    static RectangleF[] DeviceRects(IntPtr device)
    {
        if (!devRects.TryGetValue(device, out var r))
        {
            r = GetPointerDeviceRects(device, out RECT dv, out RECT ds) && dv.Right > dv.Left && dv.Bottom > dv.Top
                ? new[] { RectangleF.FromLTRB(dv.Left, dv.Top, dv.Right, dv.Bottom), RectangleF.FromLTRB(ds.Left, ds.Top, ds.Right, ds.Bottom) } : null;
            devRects[device] = r;
        }
        return r;
    }

    /// <summary>포인터 정보 배열(새것이 0번) → 메시지 한 장 (점은 오래된 것부터)</summary>
    public static PenFrame Decode(POINTER_PEN_INFO[] h, int n, int type, int msg, PointF origin, Func<IntPtr, RectangleF[]> rects, double mousePressure, long recvQpc)
    {
        var f = new PenFrame { Type = type == PT_PEN ? "pen" : type == PT_TOUCH ? "touch" : "mouse", RecvT = QpcMs(recvQpc), Down = msg == WM_POINTERDOWN, Up = msg == WM_POINTERUP, Qpc = true };
        for (int i = n - 1; i >= 0; i--)
        {
            ref var pp = ref h[i];
            var info = pp.pointerInfo;
            bool himetric = ScreenPos(ref info, type == PT_PEN, rects, out double sx, out double sy);
            bool contact = (info.pointerFlags & POINTER_FLAG_INCONTACT) != 0 && !(f.Up && i == 0);
            var s = new PenSample
            {
                X = sx - origin.X, Y = sy - origin.Y, Contact = contact,
                T = info.PerformanceCount > 0 ? QpcMs((long)info.PerformanceCount) : info.dwTime,
            };
            if (info.PerformanceCount == 0) f.Qpc = false;
            if (type == PT_PEN)
            {
                bool barrel = (pp.penFlags & PEN_FLAG_BARREL) != 0 || (info.pointerFlags & POINTER_FLAG_SECONDBUTTON) != 0;
                bool eraser = (pp.penFlags & (PEN_FLAG_ERASER | PEN_FLAG_INVERTED)) != 0;
                s.Buttons = (contact ? PenBtn.Tip : 0) | (barrel ? PenBtn.Side1 : 0) | ((info.pointerFlags & POINTER_FLAG_THIRDBUTTON) != 0 ? PenBtn.Side2 : 0) | (eraser ? PenBtn.Eraser : 0);
                s.P = (pp.penMask & PEN_MASK_PRESSURE) != 0 ? pp.pressure / 1024.0 : contact ? 0.5 : 0;
                s.TiltX = (pp.penMask & PEN_MASK_TILT_X) != 0 ? pp.tiltX : 0;
                s.TiltY = (pp.penMask & PEN_MASK_TILT_Y) != 0 ? pp.tiltY : 0;
                s.Twist = (pp.penMask & PEN_MASK_ROTATION) != 0 ? pp.rotation : 0;
                f.HasPressure |= (pp.penMask & PEN_MASK_PRESSURE) != 0;
                f.HasTilt |= (pp.penMask & (PEN_MASK_TILT_X | PEN_MASK_TILT_Y)) != 0;
                f.HasTwist |= (pp.penMask & PEN_MASK_ROTATION) != 0;
                f.Himetric |= himetric;
            }
            else
            {
                s.Buttons = ((info.pointerFlags & POINTER_FLAG_FIRSTBUTTON) != 0 ? PenBtn.Tip : 0) | ((info.pointerFlags & POINTER_FLAG_SECONDBUTTON) != 0 ? PenBtn.Side1 : 0) | ((info.pointerFlags & POINTER_FLAG_THIRDBUTTON) != 0 ? PenBtn.Side2 : 0);
                s.P = contact ? (type == PT_MOUSE ? mousePressure : 1) : 0;
            }
            f.Pts.Add(s);
        }
        return f;
    }

    /// <summary>그림판 ScreenPos 와 같은 규칙. HIMETRIC 위치를 썼으면 true</summary>
    static bool ScreenPos(ref POINTER_INFO info, bool pen, Func<IntPtr, RectangleF[]> rects, out double x, out double y)
    {
        x = info.ptPixelLocation.X; y = info.ptPixelLocation.Y;
        if (!pen) return false;
        var r = rects?.Invoke(info.sourceDevice);
        if (r == null || r[0].Width <= 0 || r[0].Height <= 0) return false;
        double hx = r[1].Left + (info.ptHimetricLocation.X - r[0].Left) * r[1].Width / r[0].Width;
        double hy = r[1].Top + (info.ptHimetricLocation.Y - r[0].Top) * r[1].Height / r[0].Height;
        if (Math.Abs(hx - x) <= 1.5 && Math.Abs(hy - y) <= 1.5) { x = hx; y = hy; return true; }
        return false;
    }

    /// <summary>펜 길게 누르기 오른쪽 클릭·물결 표시 끔 (그림판과 같음)</summary>
    public static void NoFeedback(IntPtr hwnd) { foreach (int k in new[] { 2, 3, 4, 5, 6 }) { int no = 0; SetWindowFeedbackSetting(hwnd, k, 0, 4, ref no); } }

    // ---- Win32 (그림판과 같은 모양) ----
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public int pointerType; public uint pointerId; public uint frameId; public int pointerFlags;
        public IntPtr sourceDevice; public IntPtr hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount; public int InputData; public uint dwKeyStates; public ulong PerformanceCount; public int ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_PEN_INFO { public POINTER_INFO pointerInfo; public int penFlags, penMask; public uint pressure, rotation; public int tiltX, tiltY; }
    [DllImport("user32.dll")] static extern bool GetPointerType(uint id, out int type);
    [DllImport("user32.dll")] static extern bool GetPointerInfo(uint id, out POINTER_INFO info);
    [DllImport("user32.dll")] static extern bool GetPointerPenInfo(uint id, out POINTER_PEN_INFO info);
    [DllImport("user32.dll")] static extern bool GetPointerPenInfoHistory(uint id, ref uint count, [In, Out] POINTER_PEN_INFO[] info);
    [DllImport("user32.dll")] static extern bool GetPointerDeviceRects(IntPtr device, out RECT pointerDeviceRect, out RECT displayRect);
    [DllImport("user32.dll")] static extern bool SetWindowFeedbackSetting(IntPtr h, int feedback, int flags, int size, ref int config);
}
