// 소리 (배경음·대사) — 웹 판 animx.js 의 소리 부분. doc.anim.audio = { name, data: "data:audio/...;base64,...", offset, volume }
// 네이티브 판은 따로 코덱 없이 WAV(PCM 8/16/24/32비트, 32비트 실수)만 읽고 재생한다 (Windows winmm PlaySound).
// mp3·ogg·m4a 가 든 작품은 소리 데이터를 그대로 보관만 하고 재생·분석은 하지 않는다.
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public sealed class Pcm
{
    public int Rate, Channels;
    public float[] Mono;          // 첫 채널 (분석용, -1~1)
    public float[][] Ch;          // 채널별 (재생용)
    public double Duration => Mono.Length / (double)Rate;
}

public static class Sound
{
    static string cacheKey; static Pcm cache;

    public static JsonObject Audio(Document d) => d == null ? null : AnimCore.A(d)["audio"] as JsonObject;
    public static bool HasAudio(Document d) => Audio(d)?["data"] != null;

    /// <summary>붙인 소리를 PCM 으로 (WAV 가 아니면 null)</summary>
    public static Pcm Decode(Document d)
    {
        var a = Audio(d);
        string data = J.S(a?["data"]);
        if (data == null) return null;
        string key = data.Length + ":" + J.S(a["name"]);
        if (key == cacheKey) return cache;
        cacheKey = key; cache = null;
        try
        {
            int comma = data.IndexOf(',');
            cache = ParseWav(Convert.FromBase64String(comma >= 0 ? data[(comma + 1)..] : data));
        }
        catch { cache = null; }
        return cache;
    }

    public static Pcm ParseWav(byte[] b)
    {
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E') return null;
        int p = 12, fmt = 0, ch = 0, rate = 0, bits = 0, dataAt = -1, dataLen = 0;
        while (p + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
            int len = BitConverter.ToInt32(b, p + 4);
            if (id == "fmt ")
            {
                fmt = BitConverter.ToUInt16(b, p + 8); ch = BitConverter.ToUInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); bits = BitConverter.ToUInt16(b, p + 22);
                if (fmt == 0xFFFE && len >= 26) fmt = BitConverter.ToUInt16(b, p + 32);   // WAVE_FORMAT_EXTENSIBLE → 하위 형식
            }
            else if (id == "data") { dataAt = p + 8; dataLen = Math.Min(len < 0 ? int.MaxValue : len, b.Length - dataAt); break; }
            p += 8 + len + (len & 1);
        }
        if (dataAt < 0 || ch <= 0 || rate <= 0 || (fmt != 1 && fmt != 3)) return null;
        int bps = bits / 8, frames = dataLen / (bps * ch);
        var o = new Pcm { Rate = rate, Channels = ch, Ch = new float[ch][] };
        for (int c = 0; c < ch; c++) o.Ch[c] = new float[frames];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < ch; c++)
            {
                int q = dataAt + (i * ch + c) * bps;
                float v = fmt == 3 ? (bits == 64 ? (float)BitConverter.ToDouble(b, q) : BitConverter.ToSingle(b, q))
                    : bits == 8 ? (b[q] - 128) / 128f
                    : bits == 16 ? BitConverter.ToInt16(b, q) / 32768f
                    : bits == 24 ? ((b[q] | (b[q + 1] << 8) | ((sbyte)b[q + 2] << 16)) / 8388608f)
                    : BitConverter.ToInt32(b, q) / 2147483648f;
                o.Ch[c][i] = v;
            }
        o.Mono = o.Ch[0];
        return o;
    }

    /// <summary>소리 넣기 (WAV 파일 → 작품 안에 함께 저장)</summary>
    public static void SetAudio(string path)
    {
        var d = App.Doc;
        var fi = new FileInfo(path);
        if (fi.Length > 30 * 1048576) { App.Toast("소리 파일이 너무 큽니다 (30MB 까지)"); return; }
        var bytes = File.ReadAllBytes(path);
        var pcm = ParseWav(bytes);
        if (pcm == null) { App.Toast("이 소리 파일은 읽을 수 없습니다 (네이티브 판은 WAV 만)"); return; }
        var next = new JsonObject { ["name"] = fi.Name, ["data"] = "data:audio/wav;base64," + Convert.ToBase64String(bytes), ["offset"] = 0, ["volume"] = 1 };
        AnimCore.EditAnimOnly("소리 넣기", a => a["audio"] = next);
        App.Toast($"소리: {fi.Name} ({pcm.Duration:0.0}초)");
    }

    public static void RemoveAudio()
    {
        if (!HasAudio(App.Doc)) return;
        Stop();
        AnimCore.EditAnimOnly("소리 빼기", a => a["audio"] = null);
    }

    // ---- 재생 (winmm PlaySound, 메모리의 WAV) ----
    static IntPtr playing = IntPtr.Zero;

    /// <summary>t 초부터 재생 (dur 를 주면 그만큼만: 프레임 넘길 때 잠깐 듣기)</summary>
    public static void Play(Document d, double t, double dur = 0)
    {
        Stop();
        var a = Audio(d);
        var pcm = Decode(d);
        if (pcm == null) return;
        double at = t - J.D(a["offset"], 0), vol = J.N(a["volume"]) ?? 1;
        if (at >= pcm.Duration) return;
        int lead = at < 0 ? (int)(-at * pcm.Rate) : 0;
        int from = Math.Max(0, (int)(Math.Max(0, at) * pcm.Rate));
        int count = pcm.Mono.Length - from;
        if (dur > 0) count = Math.Min(count, (int)(Math.Max(0.02, dur) * pcm.Rate));
        if (count <= 0) return;
        var wav = BuildWav(pcm, from, count, lead, vol);
        playing = Marshal.AllocHGlobal(wav.Length);
        Marshal.Copy(wav, 0, playing, wav.Length);
        PlaySound(playing, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    public static void Stop()
    {
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (playing != IntPtr.Zero) { Marshal.FreeHGlobal(playing); playing = IntPtr.Zero; }
    }

    /// <summary>16비트 WAV 바이트 (앞에 lead 개 무음)</summary>
    public static byte[] BuildWav(Pcm p, int from, int count, int lead, double vol)
    {
        int ch = p.Channels, frames = lead + count;
        using var ms = new MemoryStream(44 + frames * ch * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + frames * ch * 2); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(p.Rate); w.Write(p.Rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(frames * ch * 2);
        for (int i = 0; i < lead * ch; i++) w.Write((short)0);
        for (int i = 0; i < count; i++)
            for (int c = 0; c < ch; c++) w.Write((short)Math.Clamp((int)(p.Ch[c][from + i] * vol * 32767), -32768, 32767));
        return ms.ToArray();
    }

    /// <summary>프레임마다 소리 크기 (입 모양 맞추기·타임라인 막대, 0~1)</summary>
    public static double[] FramePeaks(Document d, double[] times)
    {
        var pcm = Decode(d);
        if (pcm == null) return null;
        double off = J.D(Audio(d)["offset"], 0);
        var o = new double[Math.Max(0, times.Length - 1)];
        for (int i = 0; i < o.Length; i++)
        {
            int a = Math.Max(0, (int)Math.Floor((times[i] - off) * pcm.Rate)), b = Math.Min(pcm.Mono.Length, (int)Math.Floor((times[i + 1] - off) * pcm.Rate));
            float m = 0;
            for (int k = a; k < b; k += 16) { float v = Math.Abs(pcm.Mono[k]); if (v > m) m = v; }
            o[i] = m;
        }
        return o;
    }

    const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
    [DllImport("winmm.dll")] static extern bool PlaySound(IntPtr sound, IntPtr mod, uint flags);
}
