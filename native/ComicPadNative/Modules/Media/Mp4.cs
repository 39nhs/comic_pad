// MP4 동영상 (H.264 + AAC 소리) — Windows Media Foundation 의 싱크 라이터로 만든다 (ffmpeg 없이).
// 웹 판은 브라우저 MediaRecorder 로 실시간 녹화(MP4/WebM)했지만, 네이티브 판은 프레임마다 정확한 시각으로 바로 인코딩.
// COM 가상 함수표를 직접 부름 (슬롯 번호는 mfobjects.h / mfreadwrite.h 순서).
using System.Runtime.InteropServices;

namespace ComicPadNative.Modules.Media;

static unsafe class Mf
{
    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IntPtr t);
    [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IntPtr a, uint n);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IntPtr s);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint len, out IntPtr b);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] public static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attrs, out IntPtr writer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] public static extern int MFCreateSourceReaderFromURL(string url, IntPtr attrs, out IntPtr reader);
    [DllImport("ole32.dll")] public static extern int PropVariantClear(void* pv);

    public static readonly Guid MT_MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), MT_INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        MT_PAR = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"),
        MT_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036"),
        MT_A_CH = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), MT_A_RATE = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"),
        MT_A_BITS = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), MT_A_BYTES = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"),
        MT_A_ALIGN = new("322de230-9eeb-43bd-ab7a-ff412251541d"),
        Video = new("73646976-0000-0010-8000-00aa00389b71"), Audio = new("73647561-0000-0010-8000-00aa00389b71"),
        H264 = new("34363248-0000-0010-8000-00aa00389b71"), RGB32 = new("00000016-0000-0010-8000-00aa00389b71"),
        PCM = new("00000001-0000-0010-8000-00aa00389b71"), AAC = new("00001610-0000-0010-8000-00aa00389b71"),
        HW = new("a634a91c-822b-41b9-a494-4de4643612b0"), NO_THROTTLE = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f"),
        VIDEO_PROC = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"), PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    public static void* Fn(IntPtr p, int slot) => (*(void***)p)[slot];
    public static void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(p, 2))(p); }
    public static void Ck(int hr, string what) { if (hr < 0) throw new COMException($"Media Foundation {what} 실패 (0x{hr:X8})", hr); }

    // IMFAttributes
    public static void SetU32(IntPtr a, Guid k, uint v) => Ck(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Fn(a, 21))(a, &k, v), "속성");
    public static void SetU64(IntPtr a, Guid k, ulong v) => Ck(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Fn(a, 22))(a, &k, v), "속성");
    public static void SetGuid(IntPtr a, Guid k, Guid v) => Ck(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Fn(a, 24))(a, &k, &v), "속성");
    public static uint? GetU32(IntPtr a, Guid k) { uint v; return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Fn(a, 7))(a, &k, &v) < 0 ? null : v; }
    public static ulong? GetU64(IntPtr a, Guid k) { ulong v; return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Fn(a, 8))(a, &k, &v) < 0 ? null : v; }
    public static Guid? GetGuid(IntPtr a, Guid k) { Guid v; return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Fn(a, 10))(a, &k, &v) < 0 ? null : v; }

    public static IntPtr NewType(Guid major, Guid sub)
    {
        Ck(MFCreateMediaType(out var t), "형식 만들기");
        SetGuid(t, MT_MAJOR, major); SetGuid(t, MT_SUBTYPE, sub);
        return t;
    }

    /// <summary>바이트를 담은 샘플 (시각·길이는 100ns 단위)</summary>
    public static IntPtr Sample(ReadOnlySpan<byte> data, long time, long dur)
    {
        Ck(MFCreateMemoryBuffer((uint)data.Length, out var buf), "버퍼");
        try
        {
            byte* p; uint max, cur;
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Fn(buf, 3))(buf, &p, &max, &cur), "버퍼 잠그기");
            data.CopyTo(new Span<byte>(p, data.Length));
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(buf, 4))(buf);
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Fn(buf, 6))(buf, (uint)data.Length), "버퍼 길이");
            Ck(MFCreateSample(out var s), "샘플");
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(s, 42))(s, buf), "샘플 버퍼");
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Fn(s, 36))(s, time), "샘플 시각");
            Ck(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Fn(s, 38))(s, dur), "샘플 길이");
            return s;
        }
        finally { Release(buf); }
    }

    static int users;
    public static void Start() { lock (typeof(Mf)) { if (users++ == 0) Ck(MFStartup(0x00020070, 0), "시작"); } }
    public static void Stop() { lock (typeof(Mf)) { if (--users == 0) MFShutdown(); } }
}

/// <summary>소리 (16비트 PCM, 채널 섞어 담음)</summary>
public sealed class PcmAudio
{
    public int Rate, Channels;
    public short[] Data;   // 프레임 × 채널
    public int Frames => Data.Length / Channels;
}

public sealed unsafe class Mp4Writer : IDisposable
{
    IntPtr writer;
    readonly uint vs, aus;
    readonly int w, h;
    readonly long frameDur;          // 100ns
    readonly PcmAudio audio;
    long frames, audioDone;          // 쓴 장 수, 쓴 소리 프레임 수
    readonly string path, tmp;
    bool started, finished;

    public int Width => w; public int Height => h;

    /// <summary>fpsNum/fpsDen 고정 속도. 크기는 짝수여야 함. audio 가 있으면 AAC 소리 줄도</summary>
    public Mp4Writer(string path, int width, int height, int fpsNum, int fpsDen, int bitrate = 8_000_000, PcmAudio audio = null)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("동영상 크기는 짝수여야 합니다");
        w = width; h = height; this.path = path; this.audio = audio;
        tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "~" + Path.GetFileNameWithoutExtension(path) + ".part.mp4");
        frameDur = (long)Math.Round(10_000_000.0 * fpsDen / fpsNum);
        Mf.Start(); started = true;
        IntPtr attrs = IntPtr.Zero, t = IntPtr.Zero;
        try
        {
            Mf.Ck(Mf.MFCreateAttributes(out attrs, 2), "속성");
            Mf.SetU32(attrs, Mf.NO_THROTTLE, 1);
            Mf.SetU32(attrs, Mf.HW, 0);    // 소프트웨어 인코더 (어느 PC 에서나 같은 결과)
            if (File.Exists(tmp)) File.Delete(tmp);
            Mf.Ck(Mf.MFCreateSinkWriterFromURL(tmp, IntPtr.Zero, attrs, out writer), "파일 만들기");
            // 영상: H.264 출력 ← RGB32 입력
            t = Mf.NewType(Mf.Video, Mf.H264);
            Mf.SetU32(t, Mf.MT_AVG_BITRATE, (uint)bitrate);
            Mf.SetU32(t, Mf.MT_INTERLACE, 2);
            Mf.SetU64(t, Mf.MT_FRAME_SIZE, ((ulong)w << 32) | (uint)h);
            Mf.SetU64(t, Mf.MT_FRAME_RATE, ((ulong)fpsNum << 32) | (uint)fpsDen);
            Mf.SetU64(t, Mf.MT_PAR, (1UL << 32) | 1);
            Mf.SetU32(t, Mf.MT_PROFILE, 77);   // Main
            uint idx;
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Mf.Fn(writer, 3))(writer, t, &idx), "영상 줄 추가");
            vs = idx; Mf.Release(t); t = IntPtr.Zero;
            t = Mf.NewType(Mf.Video, Mf.RGB32);
            Mf.SetU32(t, Mf.MT_INTERLACE, 2);
            Mf.SetU64(t, Mf.MT_FRAME_SIZE, ((ulong)w << 32) | (uint)h);
            Mf.SetU64(t, Mf.MT_FRAME_RATE, ((ulong)fpsNum << 32) | (uint)fpsDen);
            Mf.SetU64(t, Mf.MT_PAR, (1UL << 32) | 1);
            Mf.SetU32(t, Mf.MT_STRIDE, (uint)(w * 4));   // 양수 = 위에서 아래로
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Mf.Fn(writer, 4))(writer, vs, t, IntPtr.Zero), "영상 입력 형식 (이 크기를 인코더가 못 받을 수 있음)");
            Mf.Release(t); t = IntPtr.Zero;
            if (audio != null)
            {
                t = Mf.NewType(Mf.Audio, Mf.AAC);
                Mf.SetU32(t, Mf.MT_A_BITS, 16); Mf.SetU32(t, Mf.MT_A_RATE, (uint)audio.Rate); Mf.SetU32(t, Mf.MT_A_CH, (uint)audio.Channels);
                Mf.SetU32(t, Mf.MT_A_BYTES, 16000);   // 128kbps
                Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Mf.Fn(writer, 3))(writer, t, &idx), "소리 줄 추가");
                aus = idx; Mf.Release(t); t = IntPtr.Zero;
                t = Mf.NewType(Mf.Audio, Mf.PCM);
                Mf.SetU32(t, Mf.MT_A_BITS, 16); Mf.SetU32(t, Mf.MT_A_RATE, (uint)audio.Rate); Mf.SetU32(t, Mf.MT_A_CH, (uint)audio.Channels);
                Mf.SetU32(t, Mf.MT_A_ALIGN, (uint)(audio.Channels * 2)); Mf.SetU32(t, Mf.MT_A_BYTES, (uint)(audio.Rate * audio.Channels * 2));
                Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Mf.Fn(writer, 4))(writer, aus, t, IntPtr.Zero), "소리 입력 형식");
                Mf.Release(t); t = IntPtr.Zero;
            }
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Fn(writer, 5))(writer), "쓰기 시작");
        }
        catch { Mf.Release(t); Dispose(); throw; }
        finally { Mf.Release(attrs); }
    }

    /// <summary>한 장 (미리 곱한 ARGB 든 불투명 ARGB 든, 알파는 무시 — 흰 바탕에 합친 그림을 주세요)</summary>
    public void AddFrame(uint[] px)
    {
        if (px.Length != w * h) throw new ArgumentException("프레임 크기가 다릅니다");
        long t0 = frames * frameDur;
        fixed (uint* p = px)
        {
            var s = Mf.Sample(new ReadOnlySpan<byte>(p, px.Length * 4), t0, frameDur);
            try { Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Mf.Fn(writer, 6))(writer, vs, s), "영상 쓰기"); }
            finally { Mf.Release(s); }
        }
        frames++;
        WriteAudioUntil(frames * frameDur);
    }

    /// <summary>소리를 time(100ns) 까지 (영상과 번갈아 넣어야 파일 안에서 고르게 섞임)</summary>
    void WriteAudioUntil(long time)
    {
        if (audio == null) return;
        long until = Math.Min(audio.Frames, time * audio.Rate / 10_000_000);
        const int Chunk = 4096;
        while (audioDone < until)
        {
            int n = (int)Math.Min(Chunk, until - audioDone);
            long t0 = audioDone * 10_000_000 / audio.Rate, t1 = (audioDone + n) * 10_000_000 / audio.Rate;
            fixed (short* p = &audio.Data[audioDone * audio.Channels])
            {
                var s = Mf.Sample(new ReadOnlySpan<byte>(p, n * audio.Channels * 2), t0, t1 - t0);
                try { Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Mf.Fn(writer, 6))(writer, aus, s), "소리 쓰기"); }
                finally { Mf.Release(s); }
            }
            audioDone += n;
        }
    }

    public long Frames => frames;

    /// <summary>마무리하고 파일 이름을 제자리로</summary>
    public void Finish()
    {
        if (finished) return;
        WriteAudioUntil(frames * frameDur);   // 영상 길이만큼만 (소리가 더 길면 자름)
        Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Fn(writer, 11))(writer), "마무리");
        Mf.Release(writer); writer = IntPtr.Zero;
        finished = true;
        File.Move(tmp, path, true);
    }

    public void Dispose()
    {
        if (writer != IntPtr.Zero) { Mf.Release(writer); writer = IntPtr.Zero; }
        if (!finished) try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        if (started) { started = false; Mf.Stop(); }
    }

    /// <summary>H.264 가 받는 크기로: 짝수, 긴 쪽 4096 이하, 넓이 4096×2304 이하</summary>
    public static (int w, int h) FitSize(double w, double h)
    {
        double k = Math.Min(1, Math.Min(4096 / Math.Max(w, h), Math.Sqrt(4096.0 * 2304 / (w * h))));
        // 줄일 때는 내림 (반올림하면 한도를 살짝 넘을 수 있음)
        int W = Math.Max(2, (int)(k < 1 ? Math.Floor(w * k / 2) : Math.Round(w / 2)) * 2), H = Math.Max(2, (int)(k < 1 ? Math.Floor(h * k / 2) : Math.Round(h / 2)) * 2);
        return (W, H);
    }
}

/// <summary>만든 MP4 확인 (Media Foundation 소스 리더로 다시 열기): 크기·장 수·길이·소리 유무, 원하는 장의 픽셀</summary>
public static unsafe class Mp4Probe
{
    public sealed class Result { public int W, H, Frames; public double Duration; public bool HasAudio; public long AudioSamples; public double AudioSeconds; public Dictionary<int, uint[]> Pixels = new(); }

    const uint FirstVideo = 0xFFFFFFFC, FirstAudio = 0xFFFFFFFD, MediaSource = 0xFFFFFFFF;

    public static Result Read(string path, ICollection<int> wantFrames = null)
    {
        Mf.Start();
        IntPtr attrs = IntPtr.Zero, r = IntPtr.Zero, t = IntPtr.Zero;
        try
        {
            Mf.Ck(Mf.MFCreateAttributes(out attrs, 1), "속성");
            Mf.SetU32(attrs, Mf.VIDEO_PROC, 1);
            Mf.Ck(Mf.MFCreateSourceReaderFromURL(path, attrs, out r), "파일 열기");
            var res = new Result();
            // 길이
            var pv = stackalloc byte[32];
            Guid k = Mf.PD_DURATION;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, void*, int>)Mf.Fn(r, 12))(r, MediaSource, &k, pv) >= 0)
            {
                if (*(ushort*)pv == 21) res.Duration = *(ulong*)(pv + 8) / 1e7;   // VT_UI8
                Mf.PropVariantClear(pv);
            }
            // 소리 줄이 있는지
            IntPtr at;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Mf.Fn(r, 6))(r, FirstAudio, &at) >= 0) { res.HasAudio = true; Mf.Release(at); }
            // 영상은 RGB32 로 받기
            t = Mf.NewType(Mf.Video, Mf.RGB32);
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)Mf.Fn(r, 7))(r, FirstVideo, null, t), "RGB 로 받기");
            Mf.Release(t); t = IntPtr.Zero;
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Mf.Fn(r, 6))(r, FirstVideo, &t), "형식");
            ulong fs = Mf.GetU64(t, Mf.MT_FRAME_SIZE) ?? 0;
            res.W = (int)(fs >> 32); res.H = (int)(fs & 0xffffffff);
            int stride = (int)(Mf.GetU32(t, Mf.MT_STRIDE) ?? (uint)(res.W * 4));
            Mf.Release(t); t = IntPtr.Zero;
            // 소리는 16비트 PCM 으로 받아 길이(샘플 시간 합)를 잼
            if (res.HasAudio)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Mf.Fn(r, 4))(r, FirstAudio, 1);
                t = Mf.NewType(Mf.Audio, Mf.PCM);
                Mf.SetU32(t, Mf.MT_A_BITS, 16);
                ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)Mf.Fn(r, 7))(r, FirstAudio, null, t);
                Mf.Release(t); t = IntPtr.Zero;
            }
            // 줄 번호 → 영상/소리
            var isVideo = new Dictionary<uint, bool>();
            bool Kind(uint idx)
            {
                if (isVideo.TryGetValue(idx, out var v)) return v;
                IntPtr mt;
                v = false;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Mf.Fn(r, 6))(r, idx, &mt) >= 0) { v = Mf.GetGuid(mt, Mf.MT_MAJOR) == Mf.Video; Mf.Release(mt); }
                return isVideo[idx] = v;
            }
            int streams = res.HasAudio ? 2 : 1, ended = 0;
            const uint AnyStream = 0xFFFFFFFE;
            while (ended < streams)
            {
                uint actual, flags; long ts; IntPtr s = IntPtr.Zero;
                Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Mf.Fn(r, 9))(r, AnyStream, 0, &actual, &flags, &ts, &s), "읽기");
                if ((flags & 0x20) != 0 && Kind(actual))
                {
                    // 디코더가 실제 형식을 알려 줌 (16 배수로 늘린 크기·줄 간격) → 그 기준으로 읽고 보이는 크기만 잘라 씀
                    IntPtr nt;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Mf.Fn(r, 6))(r, actual, &nt) >= 0)
                    {
                        ulong nfs = Mf.GetU64(nt, Mf.MT_FRAME_SIZE) ?? fs;
                        int bw = (int)(nfs >> 32);
                        stride = (int)(Mf.GetU32(nt, Mf.MT_STRIDE) ?? (uint)(bw * 4));
                        Mf.Release(nt);
                    }
                }
                if (s != IntPtr.Zero)
                {
                    try
                    {
                        if (Kind(actual))
                        {
                            if (wantFrames != null && wantFrames.Contains(res.Frames)) res.Pixels[res.Frames] = Copy(s, res.W, res.H, stride);
                            res.Frames++;
                        }
                        else
                        {
                            long dur;
                            if (((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Mf.Fn(s, 37))(s, &dur) >= 0) res.AudioSeconds += dur / 1e7;
                            res.AudioSamples++;
                        }
                    }
                    finally { Mf.Release(s); }
                }
                if ((flags & 2) != 0) ended++;   // 이 줄 끝
                if ((flags & 1) != 0) break;     // 오류
            }
            return res;
        }
        finally { Mf.Release(t); Mf.Release(r); Mf.Release(attrs); Mf.Stop(); }
    }

    static readonly Guid IID_2D = new("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB");

    /// <summary>샘플 → 픽셀 (IMF2DBuffer 가 있으면 실제 줄 간격으로: 디코더는 16 배수로 늘린 줄 간격을 쓰기도 함)</summary>
    static uint[] Copy(IntPtr sample, int w, int h, int stride)
    {
        IntPtr b;
        Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Mf.Fn(sample, 40))(sample, 0, &b), "버퍼");
        IntPtr b2 = IntPtr.Zero;
        try
        {
            var o = new uint[w * h];
            Guid iid = IID_2D;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Mf.Fn(b, 0))(b, &iid, &b2) >= 0 && b2 != IntPtr.Zero)
            {
                byte* top; int pitch;
                Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, byte**, int*, int>)Mf.Fn(b2, 3))(b2, &top, &pitch), "2D 잠그기");
                for (int y = 0; y < h; y++) new ReadOnlySpan<uint>(top + (long)y * pitch, w).CopyTo(o.AsSpan(y * w, w));   // pitch 가 음수면 아래에서 위로
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Fn(b2, 4))(b2);
                return o;
            }
            byte* p; uint max, cur;
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Mf.Fn(b, 3))(b, &p, &max, &cur), "잠그기");
            int st = Math.Abs(stride);
            for (int y = 0; y < h; y++)
            {
                int sy = stride < 0 ? h - 1 - y : y;
                if ((long)sy * st + w * 4 > cur) break;
                new ReadOnlySpan<uint>(p + (long)sy * st, w).CopyTo(o.AsSpan(y * w, w));
            }
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Fn(b, 4))(b);
            return o;
        }
        finally { Mf.Release(b2); Mf.Release(b); }
    }
}
