// 버튜버 녹화 (웹 판 ui/vtuber.js 의 MediaRecorder 녹화 대신): 스튜디오 화면의 캐릭터를 30fps 로 받아
//  - MP4: H.264 (Media Foundation) + 마이크를 켜 두었으면 AAC 소리를 실시간으로
//  - WebM: VP8 (직접 짠 인코더, 2초마다 키 프레임 + 사이 프레임) + 마이크 소리는 끝날 때 Vorbis 로 만들어 섞음
// 화면 타이머는 그림만 넘기고 인코딩은 뒤 스레드에서. 인코더가 밀리면 장을 건너뜀 (WebM 은 시각이 따로라 길이는 그대로, MP4 는 앞 장을 되풀이).
// 소리는 IVtAudio 로 받음 (실제는 MicRecorder, 시험은 만든 소리) — 녹화 파일에만 쓰고 다른 곳으로 보내지 않는다.
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ComicPadNative.Modules.Media;

/// <summary>녹화용 소리 공급 (48kHz 등 고정 빠르기, 16비트 모노/스테레오)</summary>
public interface IVtAudio : IDisposable
{
    int Rate { get; }
    int Channels { get; }
    /// <summary>지금까지 모인 소리를 꺼냄 (interleaved, 없으면 빈 배열)</summary>
    short[] Drain();
}

public sealed class VtRecorder : IDisposable
{
    public const int Fps = 30;
    readonly string fmt, path;
    readonly int w, h;
    readonly IVtAudio audio;
    readonly Stopwatch clock = new();
    readonly Thread worker;
    readonly Queue<(uint[] px, long ms)> queue = new();
    readonly object gate = new();
    bool stopping;
    Exception error;
    long nextDueMs, dropped, frames;
    // MP4
    Mp4Writer mp4; long mp4Frames;
    // WebM
    Vp8Video vp8; readonly List<(long t, byte[] data, bool key)> blocks = new(); readonly List<short> pcm = new(); long lastKey = long.MinValue / 2;

    public string Path => path;
    public long Frames => frames;
    public long Dropped => dropped;
    public double Seconds => clock.Elapsed.TotalSeconds;
    public bool HasAudio => audio != null;

    /// <summary>녹화 시작. w·h 는 짝수로 맞춤 (MP4 는 H.264 한도 안으로)</summary>
    public VtRecorder(string fmt, string path, int width, int height, IVtAudio audio = null)
    {
        this.fmt = fmt == "webm" ? "webm" : "mp4"; this.path = path; this.audio = audio;
        if (this.fmt == "mp4") (w, h) = Mp4Writer.FitSize(Math.Round(width / 2.0) * 2, Math.Round(height / 2.0) * 2);
        else (w, h) = WebmExport.FitSize(Math.Round(width / 2.0) * 2, Math.Round(height / 2.0) * 2, 1920);
        if (this.fmt == "mp4")
            mp4 = new Mp4Writer(path, w, h, Fps, 1, 8_000_000, audio != null ? new PcmAudio { Rate = audio.Rate, Channels = audio.Channels, Data = Array.Empty<short>() } : null);
        else vp8 = new Vp8Video(0.75, int.MaxValue, fast: true);
        worker = new Thread(Work) { IsBackground = true, Name = "vtuber-rec" };
        worker.Start();
        audio?.Drain();   // 인코더 준비하는 동안 모인 소리는 버림 (영상 0초와 맞춤)
        clock.Start();
    }

    public int Width => w; public int Height => h;

    /// <summary>지금 한 장을 받아야 하는지 (30fps 박자)</summary>
    public bool Due() => clock.ElapsedMilliseconds >= nextDueMs;

    /// <summary>그림 한 장 (크기가 다르면 맞춰 줄임). Due() 가 참일 때 부름</summary>
    public void Push(Bitmap bmp)
    {
        long now = clock.ElapsedMilliseconds;
        nextDueMs = Math.Max(nextDueMs + 1000 / Fps, now - 1000 / Fps);
        lock (gate)
        {
            if (error != null) return;
            if (queue.Count > Fps) { dropped++; return; }   // 인코더가 1초 넘게 밀리면 건너뜀
        }
        var px = Pixels(bmp, w, h);
        lock (gate) { queue.Enqueue((px, now)); frames++; Monitor.Pulse(gate); }
    }

    static uint[] Pixels(Bitmap src, int w, int h)
    {
        Bitmap b = src;
        if (src.Width != w || src.Height != h || src.PixelFormat != PixelFormat.Format32bppArgb)
        {
            b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(b);
            g.Clear(Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, new Rectangle(0, 0, w, h));
        }
        var o = new uint[w * h];
        var bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var row = new int[w];
        for (int y = 0; y < h; y++) { Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, w); for (int x = 0; x < w; x++) o[y * w + x] = (uint)row[x] | 0xff000000u; }
        b.UnlockBits(bd);
        if (!ReferenceEquals(b, src)) b.Dispose();
        return o;
    }

    void Work()
    {
        try
        {
            uint[] prev = null;
            while (true)
            {
                (uint[] px, long ms) item;
                lock (gate)
                {
                    while (queue.Count == 0 && !stopping) Monitor.Wait(gate, 50);
                    if (queue.Count == 0) { if (stopping) break; continue; }
                    item = queue.Dequeue();
                }
                if (mp4 != null)
                {
                    // 고정 30fps: 늦은 장 사이는 앞 장을 되풀이해서 시간이 맞게
                    long target = item.ms * Fps / 1000;
                    while (prev != null && mp4Frames < target) { mp4.AddFrame(prev); mp4Frames++; }
                    mp4.AddFrame(item.px); mp4Frames++;
                    if (audio != null) { var a = audio.Drain(); if (a.Length > 0) mp4.AddAudio(a, a.Length / audio.Channels); }
                }
                else
                {
                    bool key = item.ms - lastKey >= 2000;
                    if (key) vp8.ForceKey();
                    var (data, k) = vp8.Encode(new Argb(w, h, item.px));
                    if (k) lastKey = item.ms;
                    blocks.Add((item.ms, data, k));
                    if (audio != null) pcm.AddRange(audio.Drain());
                }
                prev = item.px;
            }
        }
        catch (Exception ex) { lock (gate) error = ex; }
    }

    /// <summary>멈추고 파일 마무리 (인코더가 남은 장을 다 쓸 때까지 기다림) → 저장 경로</summary>
    public string Stop()
    {
        long endMs = clock.ElapsedMilliseconds;
        clock.Stop();
        lock (gate) { stopping = true; Monitor.PulseAll(gate); }
        worker.Join();
        if (error != null) { Dispose(); throw new InvalidOperationException("녹화 중 오류: " + error.Message, error); }
        if (mp4 != null)
        {
            if (audio != null) { var a = audio.Drain(); if (a.Length > 0) mp4.AddAudio(a, a.Length / audio.Channels); }
            mp4.Finish(); mp4.Dispose(); mp4 = null;
        }
        else
        {
            if (audio != null) pcm.AddRange(audio.Drain());
            WebmAudio info = null; VorbisStream vs = null;
            if (audio != null && pcm.Count >= audio.Channels * 1024)
            {
                vs = Vorbis.Encode(new PcmAudio { Rate = audio.Rate, Channels = audio.Channels, Data = pcm.ToArray() }, 0.6);
                info = new WebmAudio { Private = WebmWriter.XiphLace(vs.Headers), Rate = audio.Rate, Channels = audio.Channels };
            }
            using var wm = new WebmWriter(path, w, h, Fps, info);
            if (vs != null) foreach (var (t, p) in vs.Packets) wm.AddAudio(p, t);
            foreach (var (t, d, k) in blocks) wm.AddVideo(d, t, k);
            wm.Finish(Math.Max(endMs, blocks.Count > 0 ? blocks[^1].t + 1000 / Fps : 0));
        }
        audio?.Dispose();
        return path;
    }

    public void Dispose()
    {
        lock (gate) { stopping = true; Monitor.PulseAll(gate); }
        if (worker.IsAlive) worker.Join(2000);
        mp4?.Dispose(); mp4 = null;
        audio?.Dispose();
    }
}

/// <summary>녹화용 마이크 (winmm waveIn, 48kHz 모노 16비트): 뒤 스레드가 다 찬 버퍼를 모아 둠. 소리는 녹화 파일에만 씀</summary>
public sealed class MicRecorder : IVtAudio
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

    const int Samples = 2400, NBuf = 10;   // 50ms × 10
    const uint WHDR_DONE = 1;
    static readonly int HdrSize = Marshal.SizeOf<WaveHdr>();
    IntPtr dev;
    readonly IntPtr[] hdrs = new IntPtr[NBuf], datas = new IntPtr[NBuf];
    readonly List<short> got = new();
    readonly Thread th; volatile bool run = true;
    public int Rate => 48000;
    public int Channels => 1;

    public MicRecorder()
    {
        if (waveInGetNumDevs() <= 0) throw new InvalidOperationException("마이크를 찾지 못했습니다");
        var fmt = new WaveFormatEx { wFormatTag = 1, nChannels = 1, nSamplesPerSec = 48000, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = 96000 };
        if (waveInOpen(out dev, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) throw new InvalidOperationException("마이크를 열지 못했습니다 (Windows 설정 > 개인 정보 > 마이크)");
        for (int i = 0; i < NBuf; i++)
        {
            datas[i] = Marshal.AllocHGlobal(Samples * 2); hdrs[i] = Marshal.AllocHGlobal(HdrSize);
            Marshal.StructureToPtr(new WaveHdr { lpData = datas[i], dwBufferLength = Samples * 2 }, hdrs[i], false);
            waveInPrepareHeader(dev, hdrs[i], HdrSize); waveInAddBuffer(dev, hdrs[i], HdrSize);
        }
        waveInStart(dev);
        th = new Thread(Loop) { IsBackground = true, Name = "vtuber-mic" };
        th.Start();
    }

    void Loop()
    {
        var tmp = new short[Samples];
        int i = 0;
        while (run)
        {
            var h = Marshal.PtrToStructure<WaveHdr>(hdrs[i]);
            if ((h.dwFlags & WHDR_DONE) == 0) { Thread.Sleep(5); continue; }
            int n = (int)Math.Min(Samples, h.dwBytesRecorded / 2);
            Marshal.Copy(datas[i], tmp, 0, n);
            lock (got) for (int k = 0; k < n; k++) got.Add(tmp[k]);
            h.dwFlags &= ~WHDR_DONE; h.dwBytesRecorded = 0;
            Marshal.StructureToPtr(h, hdrs[i], false);
            if (run) waveInAddBuffer(dev, hdrs[i], HdrSize);
            i = (i + 1) % NBuf;   // 버퍼는 넣은 차례대로 참
        }
    }

    public short[] Drain() { lock (got) { var o = got.ToArray(); got.Clear(); return o; } }

    public void Dispose()
    {
        run = false;
        th?.Join(500);
        if (dev != IntPtr.Zero)
        {
            waveInReset(dev);
            for (int i = 0; i < NBuf; i++) if (hdrs[i] != IntPtr.Zero) waveInUnprepareHeader(dev, hdrs[i], HdrSize);
            waveInClose(dev); dev = IntPtr.Zero;
        }
        for (int i = 0; i < NBuf; i++)
        {
            if (hdrs[i] != IntPtr.Zero) { Marshal.FreeHGlobal(hdrs[i]); hdrs[i] = IntPtr.Zero; }
            if (datas[i] != IntPtr.Zero) { Marshal.FreeHGlobal(datas[i]); datas[i] = IntPtr.Zero; }
        }
    }
}
