// 소리 파일 풀기·만들기 — Windows Media Foundation (외부 코덱 없이 Windows 에 있는 것).
//  - 풀기: MP3·AAC(.m4a/.aac/.mp4)·WMA·FLAC·ALAC, 그리고 Windows 에 "웹 미디어 확장"이 있으면 OGG(Vorbis)·Opus·WebM 소리
//    → 소스 리더가 32비트 실수 PCM 으로 내줌 (빠르기·채널은 원래대로)
//  - 만들기 (시험·소리만 내보내기): AAC(.m4a) · WMA · MP3 (Windows 의 MF 인코더가 있으면)
using System.Runtime.InteropServices;

namespace ComicPadNative.Modules.Media;

public static unsafe class MfAudio
{
    const uint FirstAudio = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE;
    static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid MP3 = new("00000055-0000-0010-8000-00aa00389b71"), WMA2 = new("00000161-0000-0010-8000-00aa00389b71");

    /// <summary>파일 → 채널별 실수 PCM (rate, ch, 채널별 샘플). 못 풀면 예외</summary>
    public static (int rate, int ch, float[][] data) Decode(string path)
    {
        Mf.Start();
        IntPtr r = IntPtr.Zero, t = IntPtr.Zero;
        try
        {
            Mf.Ck(Mf.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out r), "소리 파일 열기");
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Mf.Fn(r, 4))(r, AllStreams, 0);
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Mf.Fn(r, 4))(r, FirstAudio, 1), "소리 줄 고르기 (소리가 없는 파일?)");
            t = Mf.NewType(Mf.Audio, Float);
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)Mf.Fn(r, 7))(r, FirstAudio, null, t), "실수 PCM 으로 받기 (이 형식의 디코더가 Windows 에 없음?)");
            Mf.Release(t); t = IntPtr.Zero;
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Mf.Fn(r, 6))(r, FirstAudio, &t), "형식");
            int rate = (int)(Mf.GetU32(t, Mf.MT_A_RATE) ?? 0), ch = (int)(Mf.GetU32(t, Mf.MT_A_CH) ?? 0);
            Mf.Release(t); t = IntPtr.Zero;
            if (rate <= 0 || ch <= 0) throw new InvalidDataException("소리 형식을 알 수 없습니다");
            var all = new List<float>(rate * ch * 10);
            while (true)
            {
                uint actual, flags; long ts; IntPtr s = IntPtr.Zero;
                Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Mf.Fn(r, 9))(r, FirstAudio, 0, &actual, &flags, &ts, &s), "소리 읽기");
                if (s != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr buf;
                        Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Mf.Fn(s, 41))(s, &buf), "버퍼 합치기");
                        try
                        {
                            byte* p; uint max, cur;
                            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Mf.Fn(buf, 3))(buf, &p, &max, &cur), "잠그기");
                            var f = new ReadOnlySpan<float>(p, (int)(cur / 4));
                            foreach (var v in f) all.Add(v);
                            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Fn(buf, 4))(buf);
                        }
                        finally { Mf.Release(buf); }
                    }
                    finally { Mf.Release(s); }
                }
                if ((flags & 2) != 0 || (flags & 1) != 0) break;   // 끝 / 오류
                if (all.Count > (long)rate * ch * 60 * 30) throw new InvalidDataException("소리가 너무 깁니다 (30분까지)");
            }
            int n = all.Count / ch;
            var o = new float[ch][];
            for (int c = 0; c < ch; c++) { o[c] = new float[n]; for (int i = 0; i < n; i++) o[c][i] = all[i * ch + c]; }
            return (rate, ch, o);
        }
        finally { Mf.Release(t); Mf.Release(r); Mf.Stop(); }
    }

    /// <summary>바이트(소리 파일 내용) → PCM. 확장자는 MF 가 형식을 고르는 데 씀</summary>
    public static (int rate, int ch, float[][] data) DecodeBytes(byte[] bytes, string ext)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"comicpad-snd-{Environment.ProcessId}-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(tmp, bytes);
        try { return Decode(tmp); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [DllImport("mf.dll")] static extern int MFTranscodeGetAudioOutputAvailableTypes(in Guid subtype, uint flags, IntPtr config, out IntPtr collection);
    static readonly Guid IID_MediaType = new("44ae0fa8-ea31-4109-8d2e-4cae4997c555");

    /// <summary>인코더가 받는 출력 형식 중 빠르기·채널이 같고 초당 바이트가 가장 가까운 것 (없으면 0)</summary>
    static IntPtr PickType(Guid subtype, int rate, int ch, int bytesPerSec)
    {
        if (MFTranscodeGetAudioOutputAvailableTypes(subtype, 0x3F, IntPtr.Zero, out var coll) < 0 || coll == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            uint n;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Mf.Fn(coll, 3))(coll, &n) < 0) return IntPtr.Zero;
            IntPtr best = IntPtr.Zero; long bestD = long.MaxValue;
            for (uint i = 0; i < n; i++)
            {
                IntPtr unk;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Mf.Fn(coll, 4))(coll, i, &unk) < 0 || unk == IntPtr.Zero) continue;
                IntPtr mt = IntPtr.Zero; Guid iid = IID_MediaType;
                ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Mf.Fn(unk, 0))(unk, &iid, &mt);
                Mf.Release(unk);
                if (mt == IntPtr.Zero) continue;
                long d = Mf.GetU32(mt, Mf.MT_A_RATE) == rate && Mf.GetU32(mt, Mf.MT_A_CH) == ch ? Math.Abs((long)(Mf.GetU32(mt, Mf.MT_A_BYTES) ?? 0) - bytesPerSec) : long.MaxValue;
                if (d < bestD) { Mf.Release(best); best = mt; bestD = d; } else Mf.Release(mt);
            }
            return best;
        }
        finally { Mf.Release(coll); }
    }

    /// <summary>소리만 파일로 (subtype: Mf.AAC → .m4a, WMA2 → .wma, MP3 → .mp3). 이 PC 에 인코더가 없으면 예외</summary>
    public static void Encode(PcmAudio pcm, string path, Guid subtype, int bytesPerSec = 16000)
    {
        Mf.Start();
        IntPtr w = IntPtr.Zero, t = IntPtr.Zero;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            Mf.Ck(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out w), "파일 만들기");
            t = subtype == Mf.AAC ? IntPtr.Zero : PickType(subtype, pcm.Rate, pcm.Channels, bytesPerSec);   // MP3·WMA 는 인코더가 내놓는 형식 중에서
            if (t == IntPtr.Zero)
            {
                t = Mf.NewType(Mf.Audio, subtype);
                Mf.SetU32(t, Mf.MT_A_BITS, 16); Mf.SetU32(t, Mf.MT_A_RATE, (uint)pcm.Rate); Mf.SetU32(t, Mf.MT_A_CH, (uint)pcm.Channels);
                Mf.SetU32(t, Mf.MT_A_BYTES, (uint)bytesPerSec);
            }
            uint idx;
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Mf.Fn(w, 3))(w, t, &idx), "소리 줄 (이 인코더가 이 빠르기·채널을 못 받음?)");
            Mf.Release(t); t = IntPtr.Zero;
            t = Mf.NewType(Mf.Audio, Mf.PCM);
            Mf.SetU32(t, Mf.MT_A_BITS, 16); Mf.SetU32(t, Mf.MT_A_RATE, (uint)pcm.Rate); Mf.SetU32(t, Mf.MT_A_CH, (uint)pcm.Channels);
            Mf.SetU32(t, Mf.MT_A_ALIGN, (uint)(pcm.Channels * 2)); Mf.SetU32(t, Mf.MT_A_BYTES, (uint)(pcm.Rate * pcm.Channels * 2));
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Mf.Fn(w, 4))(w, idx, t, IntPtr.Zero), "소리 입력 형식");
            Mf.Release(t); t = IntPtr.Zero;
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Fn(w, 5))(w), "쓰기 시작");
            long done = 0; const int Chunk = 4096;
            while (done < pcm.Frames)
            {
                int n = (int)Math.Min(Chunk, pcm.Frames - done);
                long t0 = done * 10_000_000 / pcm.Rate, t1 = (done + n) * 10_000_000 / pcm.Rate;
                fixed (short* p = &pcm.Data[done * pcm.Channels])
                {
                    var s = Mf.Sample(new ReadOnlySpan<byte>(p, n * pcm.Channels * 2), t0, t1 - t0);
                    try { Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Mf.Fn(w, 6))(w, idx, s), "소리 쓰기"); }
                    finally { Mf.Release(s); }
                }
                done += n;
            }
            Mf.Ck(((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Fn(w, 11))(w), "마무리");
        }
        finally { Mf.Release(t); Mf.Release(w); Mf.Stop(); }
    }
}
