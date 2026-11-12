// 마이크 소리 크기 (웹 판 vtuber/track.js startMic 대신): Windows 기본 waveIn(winmm) 을 직접 불러 RMS 만 계산한다.
// 소리는 이 PC 안에서만 쓰고 저장·전송하지 않는다. 외부 패키지 없이 P/Invoke 로만.
using System.Runtime.InteropServices;

namespace ComicPadNative.Modules.Scene3D;

public sealed class VMic : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormatEx { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int dev, ref WaveFormatEx fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInGetNumDevs();

    const uint WHDR_DONE = 1;
    const int Samples = 1024, Rate = 16000, NBuf = 4;
    IntPtr dev;
    readonly IntPtr[] hdrs = new IntPtr[NBuf], datas = new IntPtr[NBuf];
    static readonly int HdrSize = Marshal.SizeOf<WaveHdr>();
    double level;
    readonly short[] tmp = new short[Samples];

    /// <summary>기본 마이크 열기 (없거나 막혀 있으면 예외)</summary>
    public VMic()
    {
        if (waveInGetNumDevs() <= 0) throw new InvalidOperationException("마이크를 찾지 못했습니다");
        var fmt = new WaveFormatEx { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = Rate * 2 };
        int r = waveInOpen(out dev, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0) throw new InvalidOperationException($"마이크를 열지 못했습니다 (코드 {r}). Windows 설정 > 개인 정보 > 마이크 권한을 확인하세요");
        for (int i = 0; i < NBuf; i++)
        {
            datas[i] = Marshal.AllocHGlobal(Samples * 2);
            hdrs[i] = Marshal.AllocHGlobal(HdrSize);
            Marshal.StructureToPtr(new WaveHdr { lpData = datas[i], dwBufferLength = Samples * 2 }, hdrs[i], false);
            waveInPrepareHeader(dev, hdrs[i], HdrSize);
            waveInAddBuffer(dev, hdrs[i], HdrSize);
        }
        if (waveInStart(dev) != 0) { Dispose(); throw new InvalidOperationException("마이크를 시작하지 못했습니다"); }
    }

    /// <summary>최근 소리 크기 (RMS, 0~1). 다 찬 버퍼를 읽고 다시 넣는다</summary>
    public double Level()
    {
        if (dev == IntPtr.Zero) return 0;
        for (int i = 0; i < NBuf; i++)
        {
            var h = Marshal.PtrToStructure<WaveHdr>(hdrs[i]);
            if ((h.dwFlags & WHDR_DONE) == 0) continue;
            int n = (int)Math.Min(Samples, h.dwBytesRecorded / 2);
            if (n > 0)
            {
                Marshal.Copy(datas[i], tmp, 0, n);
                double s = 0;
                for (int k = 0; k < n; k++) { double v = tmp[k] / 32768.0; s += v * v; }
                level = Math.Sqrt(s / n);
            }
            h.dwFlags &= ~WHDR_DONE; h.dwBytesRecorded = 0;
            Marshal.StructureToPtr(h, hdrs[i], false);
            waveInAddBuffer(dev, hdrs[i], HdrSize);
        }
        return level;
    }

    public void Dispose()
    {
        if (dev != IntPtr.Zero)
        {
            waveInReset(dev);
            for (int i = 0; i < NBuf; i++) if (hdrs[i] != IntPtr.Zero) waveInUnprepareHeader(dev, hdrs[i], HdrSize);
            waveInClose(dev);
            dev = IntPtr.Zero;
        }
        for (int i = 0; i < NBuf; i++)
        {
            if (hdrs[i] != IntPtr.Zero) { Marshal.FreeHGlobal(hdrs[i]); hdrs[i] = IntPtr.Zero; }
            if (datas[i] != IntPtr.Zero) { Marshal.FreeHGlobal(datas[i]); datas[i] = IntPtr.Zero; }
        }
    }
}
