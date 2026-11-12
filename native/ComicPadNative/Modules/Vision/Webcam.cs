// 웹캠 (웹 판 getUserMedia 대신): Windows Media Foundation 소스 리더를 COM 가상 함수표로 직접 부른다 (외부 패키지·WinRT 없이).
// 영상은 RGB32 로 받아 메모리에서만 쓰고 저장·전송하지 않는다.
using System.Runtime.InteropServices;

namespace ComicPadNative.Modules.Vision;

public sealed unsafe class Webcam : IDisposable
{
    // ---- Media Foundation ----
    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IntPtr attr, uint size);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mf.dll")] static extern int MFEnumDeviceSources(IntPtr attr, out IntPtr activates, out uint count);
    [DllImport("mfreadwrite.dll")] static extern int MFCreateSourceReaderFromMediaSource(IntPtr source, IntPtr attr, out IntPtr reader);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr r, uint flags);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

    const uint MF_VERSION = 0x00020070;
    const uint FIRST_VIDEO = 0xFFFFFFFC, ALL_STREAMS = 0xFFFFFFFE;
    static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3"), VidCap = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f"),
        FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01"), AdvancedVP = new("0f81da2c-b537-4672-a8b2-a681b17307a3"),
        MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"), DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"),
        Video = new("73646976-0000-0010-8000-00aa00389b71"), Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");

    // ---- 가상 함수 부르기 (IUnknown 0~2, IMFAttributes 3~32, 그 뒤는 인터페이스마다) ----
    static void* Fn(IntPtr p, int slot) => (*(void***)p)[slot];
    static void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(p, 2))(p); }
    static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} (0x{hr:X8})"); }
    static int SetGuid(IntPtr a, Guid k, Guid v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Fn(a, 24))(a, &k, &v);
    static int SetU32(IntPtr a, Guid k, uint v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Fn(a, 21))(a, &k, v);
    static int SetU64(IntPtr a, Guid k, ulong v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Fn(a, 22))(a, &k, v);
    static int GetU32(IntPtr a, Guid k, out uint v) { uint t; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Fn(a, 7))(a, &k, &t); v = t; return hr; }
    static int GetU64(IntPtr a, Guid k, out ulong v) { ulong t; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Fn(a, 8))(a, &k, &t); v = t; return hr; }
    static string GetString(IntPtr a, Guid k)
    {
        IntPtr s; uint len;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint*, int>)Fn(a, 13))(a, &k, &s, &len) < 0) return null;
        var r = Marshal.PtrToStringUni(s, (int)len); CoTaskMemFree(s); return r;
    }

    static readonly object mfGate = new();
    static int mfUsers;
    static void Startup()
    {
        CoInitializeEx(IntPtr.Zero, 0);          // 멀티스레드 (이미 되어 있으면 무시)
        lock (mfGate) { if (mfUsers++ == 0) Check(MFStartup(MF_VERSION, 0), "Media Foundation 을 시작하지 못했습니다"); }
    }
    static void Shutdown() { lock (mfGate) { if (--mfUsers == 0) MFShutdown(); } }

    /// <summary>카메라 목록 (열지는 않음)</summary>
    public static List<string> List()
    {
        Startup();
        try
        {
            var (arr, n) = Enum();
            var names = new List<string>();
            for (int i = 0; i < n; i++) { var a = ((IntPtr*)arr)[i]; names.Add(GetString(a, FriendlyName) ?? $"카메라 {i + 1}"); Release(a); }
            if (arr != IntPtr.Zero) CoTaskMemFree(arr);
            return names;
        }
        finally { Shutdown(); }
    }

    static (IntPtr arr, int n) Enum()
    {
        Check(MFCreateAttributes(out var attr, 1), "카메라 목록을 만들지 못했습니다");
        try
        {
            Check(SetGuid(attr, SourceType, VidCap), "카메라 목록");
            Check(MFEnumDeviceSources(attr, out var arr, out uint n), "카메라를 찾지 못했습니다");
            return (arr, (int)n);
        }
        finally { Release(attr); }
    }

    IntPtr activate, source, reader;
    bool started;
    public readonly string Name;
    public int Width { get; private set; }
    public int Height { get; private set; }
    int stride;

    /// <summary>index 번째 카메라 열기 (없으면 InvalidOperationException, 메시지는 사용자에게 보여 줄 말)</summary>
    public Webcam(int index = 0, int wantW = 640)
    {
        Startup(); started = true;
        try
        {
            var (arr, n) = Enum();
            try
            {
                if (n == 0) throw new InvalidOperationException("카메라를 찾지 못했습니다 (연결·Windows 설정 > 개인 정보 > 카메라 권한을 확인하세요)");
                if (index < 0 || index >= n) throw new InvalidOperationException($"카메라를 찾지 못했습니다 ({index + 1}번째 카메라가 없습니다, 모두 {n}대)");
                activate = ((IntPtr*)arr)[index];
                for (int i = 0; i < n; i++) if (i != index) Release(((IntPtr*)arr)[i]);
            }
            finally { if (arr != IntPtr.Zero) CoTaskMemFree(arr); }
            Name = GetString(activate, FriendlyName) ?? "카메라";
            // IMFActivate::ActivateObject(riid, ppv) = 33, IMFMediaSource
            var iid = new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66"); IntPtr src;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Fn(activate, 33))(activate, &iid, &src);
            if (hr < 0) throw new InvalidOperationException($"카메라를 열지 못했습니다 (다른 프로그램이 쓰고 있거나 권한이 없습니다, 0x{hr:X8})");
            source = src;
            Check(MFCreateAttributes(out var ra, 1), "카메라 읽기 준비");
            SetU32(ra, AdvancedVP, 1);                // RGB32 로 바꿔 주기
            hr = MFCreateSourceReaderFromMediaSource(source, ra, out reader);
            Release(ra);
            Check(hr, "카메라 읽기를 시작하지 못했습니다");
            // IMFSourceReader: SetStreamSelection 4, GetNativeMediaType 5, GetCurrentMediaType 6, SetCurrentMediaType 7, ReadSample 9
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Fn(reader, 4))(reader, ALL_STREAMS, 0);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Fn(reader, 4))(reader, FIRST_VIDEO, 1);
            // 원래 형식 중 너비가 wantW 에 가장 가까운 것 (클수록 계산이 무거움)
            IntPtr bestNative = IntPtr.Zero; long bestD = long.MaxValue;
            for (uint i = 0; i < 200; i++)
            {
                IntPtr t;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Fn(reader, 5))(reader, FIRST_VIDEO, i, &t) < 0) break;
                if (GetU64(t, FrameSize, out ulong fs) >= 0)
                {
                    long w = (long)(fs >> 32), d = Math.Abs(w - wantW) * 4 + (w < wantW ? 1 : 0);
                    if (d < bestD) { Release(bestNative); bestNative = t; bestD = d; continue; }
                }
                Release(t);
            }
            if (bestNative != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Fn(reader, 7))(reader, FIRST_VIDEO, IntPtr.Zero, bestNative);
                Release(bestNative);
            }
            Check(MFCreateMediaType(out var mt), "영상 형식");
            SetGuid(mt, MajorType, Video); SetGuid(mt, SubType, Rgb32);
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Fn(reader, 7))(reader, FIRST_VIDEO, IntPtr.Zero, mt);
            Release(mt);
            Check(hr, "카메라 영상을 RGB 로 받을 수 없습니다");
            IntPtr cur;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(reader, 6))(reader, FIRST_VIDEO, &cur), "영상 형식");
            GetU64(cur, FrameSize, out ulong size);
            Width = (int)(size >> 32); Height = (int)(size & 0xFFFFFFFF);
            stride = GetU32(cur, DefaultStride, out uint st) >= 0 ? (int)st : Width * 4;
            Release(cur);
            if (Width <= 0 || Height <= 0) throw new InvalidOperationException("카메라 영상 크기를 알 수 없습니다");
        }
        catch { Dispose(); throw; }
    }

    /// <summary>다음 한 장 (막힘, 보통 1/30초). 실패·끝이면 null</summary>
    public VImg Read()
    {
        if (reader == IntPtr.Zero) return null;
        for (int tries = 0; tries < 50; tries++)
        {
            uint actual = 0, flags = 0; long ts = 0; IntPtr sample = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Fn(reader, 9))(reader, FIRST_VIDEO, 0, &actual, &flags, &ts, &sample);
            if (hr < 0 || (flags & 3) != 0) { Release(sample); return null; }   // 오류·끝
            if (sample == IntPtr.Zero) continue;                                  // 빈 틱
            try
            {
                // IMFSample::ConvertToContiguousBuffer = 41, IMFMediaBuffer::Lock 3 / Unlock 4
                IntPtr buf;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Fn(sample, 41))(sample, &buf) < 0) return null;
                try
                {
                    byte* p; uint max, len;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Fn(buf, 3))(buf, &p, &max, &len) < 0) return null;
                    try
                    {
                        int W = Width, H = Height, s = Math.Abs(stride);
                        if (len < (uint)(s * (H - 1) + W * 4)) return null;
                        var px = new uint[W * H];
                        for (int y = 0; y < H; y++)
                        {
                            uint* row = (uint*)(p + (stride < 0 ? (H - 1 - y) : y) * s);
                            for (int x = 0; x < W; x++) px[y * W + x] = row[x] | 0xFF000000u;
                        }
                        return new VImg(px, W, H);
                    }
                    finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(buf, 4))(buf); }
                }
                finally { Release(buf); }
            }
            finally { Release(sample); }
        }
        return null;
    }

    public void Dispose()
    {
        Release(reader); reader = IntPtr.Zero;
        if (source != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(source, 12))(source); Release(source); source = IntPtr.Zero; }   // IMFMediaSource::Shutdown
        if (activate != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(activate, 34))(activate); Release(activate); activate = IntPtr.Zero; }   // ShutdownObject
        if (started) { started = false; Shutdown(); }
    }
}
