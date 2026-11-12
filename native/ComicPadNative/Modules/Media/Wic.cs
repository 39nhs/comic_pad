// WIC (Windows Imaging Component) 로 그림 풀기 — WebP(손실 포함)·HEIC 등 GDI+ 가 못 여는 형식.
// 외부 패키지 없이 COM 가상 함수표를 직접 부름 (슬롯 번호는 wincodec.h 순서).
using System.Runtime.InteropServices;

namespace ComicPadNative.Modules.Media;

public static unsafe class Wic
{
    static readonly Guid CLSID_Factory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    static readonly Guid IID_Factory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    static readonly Guid BGRA32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");   // GUID_WICPixelFormat32bppBGRA (미리 곱하지 않음)

    [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint ctx, in Guid iid, out IntPtr obj);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    static void* Fn(IntPtr p, int slot) => (*(void***)p)[slot];
    internal static void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(p, 2))(p); }
    static void Ck(int hr, string what) { if (hr < 0) throw new COMException($"WIC {what} 실패 (0x{hr:X8})", hr); }

    /// <summary>파일의 frame 번째 그림 (없거나 못 풀면 예외). 반환: 미리 곱하지 않은 ARGB</summary>
    public static Argb Decode(string path, int frame = 0)
    {
        CoInitializeEx(IntPtr.Zero, 0);   // 이미 됐으면 그대로 (결과 무시)
        IntPtr fac = IntPtr.Zero, dec = IntPtr.Zero, frm = IntPtr.Zero, conv = IntPtr.Zero;
        try
        {
            Ck(CoCreateInstance(CLSID_Factory, IntPtr.Zero, 1, IID_Factory, out fac), "만들기");
            fixed (char* p = path)
                Ck(((delegate* unmanaged[Stdcall]<IntPtr, char*, Guid*, uint, int, IntPtr*, int>)Fn(fac, 3))(fac, p, null, 0x80000000, 0, &dec), "파일 열기");
            uint count = 0;
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Fn(dec, 12))(dec, &count), "장 수");
            if (frame >= count) throw new ArgumentOutOfRangeException(nameof(frame));
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Fn(dec, 13))(dec, (uint)frame, &frm), "장 읽기");
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Fn(fac, 10))(fac, &conv), "변환기");
            Guid fmt = BGRA32;
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)Fn(conv, 8))(conv, frm, &fmt, 0, IntPtr.Zero, 0.0, 0), "형식 바꾸기");
            uint w = 0, h = 0;
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)Fn(conv, 3))(conv, &w, &h), "크기");
            if (w == 0 || h == 0 || (long)w * h > 400_000_000) throw new InvalidDataException("그림 크기가 이상합니다");
            var o = new Argb((int)w, (int)h);
            fixed (uint* dst = o.P)
                Ck(((delegate* unmanaged[Stdcall]<IntPtr, void*, uint, uint, byte*, int>)Fn(conv, 7))(conv, null, w * 4, w * h * 4, (byte*)dst), "픽셀 복사");
            return o;   // BGRA 바이트 = 리틀 엔디언 ARGB 정수
        }
        finally { Release(conv); Release(frm); Release(dec); Release(fac); }
    }

    /// <summary>WIC 로 이 파일을 풀 수 있는지 (WebP 확장 설치 여부 확인용)</summary>
    public static bool CanDecode(string path) { try { Decode(path); return true; } catch { return false; } }
}
