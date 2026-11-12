// Wintab: 와콤·휴이온·XP-Pen 드라이버에서 "Windows Ink" 를 꺼 둔 경우에도 필압·기울기를 받기 위한 길 (클립스튜디오의 Wintab 모드와 같은 방식).
// 태블릿 드라이버가 설치한 wintab32.dll 이 있을 때만 쓰고, 설정에서 켜야 동작한다 (기본은 Windows Ink).
// 펜이 태블릿 가까이 오면(근접) 패킷이 오고, 그 위치가 그림판 위면 덮개를 켜서 패킷을 그대로 그림판에 넘긴다.
using System;
using System.Runtime.InteropServices;

static class Wintab
{
    static bool? has;
    public static bool Available
    {
        get
        {
            if (has == null)
            {
                IntPtr h = LoadLibrary("wintab32.dll");
                has = h != IntPtr.Zero;
                if (has == true)
                {
                    try { var lc = new LOGCONTEXT(); has = WTInfoW(WTI_DEFSYSCTX, 0, ref lc) > 0; } catch { has = false; }
                }
            }
            return has == true;
        }
    }

    public const int WT_PACKET = 0x7FF0, WT_PROXIMITY = 0x7FF5;
    const uint WTI_DEFSYSCTX = 4, WTI_DEVICES = 100, DVC_NPRESSURE = 15;
    const uint CXO_SYSTEM = 1, CXO_MESSAGES = 4;
    public const uint PK_STATUS = 0x2, PK_TIME = 0x4, PK_CURSOR = 0x20, PK_BUTTONS = 0x40, PK_X = 0x80, PK_Y = 0x100, PK_NORMAL_PRESSURE = 0x400, PK_ORIENTATION = 0x1000;
    public const int SUB = 8;   // 1/8 픽셀 단위로 받기

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGCONTEXT
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)] public string lcName;
        public uint lcOptions, lcStatus, lcLocks, lcMsgBase, lcDevice, lcPktRate, lcPktData, lcPktMode, lcMoveMask, lcBtnDnMask, lcBtnUpMask;
        public int lcInOrgX, lcInOrgY, lcInOrgZ, lcInExtX, lcInExtY, lcInExtZ, lcOutOrgX, lcOutOrgY, lcOutOrgZ, lcOutExtX, lcOutExtY, lcOutExtZ;
        public int lcSensX, lcSensY, lcSensZ, lcSysMode, lcSysOrgX, lcSysOrgY, lcSysExtX, lcSysExtY, lcSysSensX, lcSysSensY;
    }
    [StructLayout(LayoutKind.Sequential)] public struct AXIS { public int axMin, axMax; public uint axUnits; public int axResolution; }
    // lcPktData 에 고른 항목의 순서(비트 순서) 그대로
    [StructLayout(LayoutKind.Sequential)]
    public struct PACKET { public uint status, time, cursor, buttons; public int x, y; public uint pressure; public int azimuth, altitude, twist; }

    public class Ctx
    {
        public IntPtr h;
        public int maxPressure = 1023;
        public int sysOrgX, sysOrgY;
    }

    /// <summary>창(hwnd)에 시스템 컨텍스트를 연다. 실패하면 null.</summary>
    public static Ctx Open(IntPtr hwnd)
    {
        if (!Available) return null;
        var lc = new LOGCONTEXT();
        if (WTInfoW(WTI_DEFSYSCTX, 0, ref lc) == 0) return null;
        lc.lcName = "ComicPad";
        lc.lcOptions |= CXO_SYSTEM | CXO_MESSAGES;
        lc.lcPktData = PK_STATUS | PK_TIME | PK_CURSOR | PK_BUTTONS | PK_X | PK_Y | PK_NORMAL_PRESSURE | PK_ORIENTATION;
        lc.lcPktMode = 0;                       // 모두 절댓값
        lc.lcMoveMask = lc.lcPktData;
        lc.lcBtnDnMask = lc.lcBtnUpMask = 0xFFFFFFFF;
        // 출력 좌표 = 화면 픽셀 x SUB (위가 0 이 되게 Y 를 뒤집음)
        lc.lcOutOrgX = 0; lc.lcOutOrgY = 0;
        lc.lcOutExtX = lc.lcSysExtX * SUB;
        lc.lcOutExtY = -lc.lcSysExtY * SUB;
        var c = new Ctx { sysOrgX = lc.lcSysOrgX, sysOrgY = lc.lcSysOrgY };
        c.h = WTOpenW(hwnd, ref lc, true);
        if (c.h == IntPtr.Zero) return null;
        var ax = new AXIS();
        try { if (WTInfoW(WTI_DEVICES, DVC_NPRESSURE, ref ax) > 0 && ax.axMax > 0) c.maxPressure = ax.axMax; } catch { }
        return c;
    }

    public static void Close(Ctx c) { if (c != null && c.h != IntPtr.Zero) { try { WTClose(c.h); } catch { } c.h = IntPtr.Zero; } }

    public static int Get(Ctx c, PACKET[] buf) { try { return WTPacketsGet(c.h, buf.Length, buf); } catch { return 0; } }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string name);
    [DllImport("wintab32.dll", CharSet = CharSet.Unicode)] static extern uint WTInfoW(uint cat, uint idx, ref LOGCONTEXT lc);
    [DllImport("wintab32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTInfoW")] static extern uint WTInfoW(uint cat, uint idx, ref AXIS ax);
    [DllImport("wintab32.dll", CharSet = CharSet.Unicode)] static extern IntPtr WTOpenW(IntPtr hwnd, ref LOGCONTEXT lc, bool enable);
    [DllImport("wintab32.dll")] static extern bool WTClose(IntPtr ctx);
    [DllImport("wintab32.dll")] static extern int WTPacketsGet(IntPtr ctx, int max, [In, Out] PACKET[] buf);
}
