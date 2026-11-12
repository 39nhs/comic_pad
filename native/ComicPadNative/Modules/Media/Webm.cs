// WebM 동영상 (웹 판 MediaRecorder 의 WebM 대신) — Matroska/EBML 을 직접 씀: EBML 머리 · Segment(SeekHead·Info·Tracks·Cluster·Cues).
//  - 영상: VP8 (Vp8.cs, 장마다 키 프레임 — 어느 장에서나 바로 넘겨 볼 수 있고 여러 장을 동시에 만들 수 있음)
//  - 소리: Vorbis (Vorbis.cs) — 소리 묶음은 시각 순서로 영상 사이에 끼워 넣음
// 파일에 바로 흘려 쓰고 (긴 녹화도 메모리에 다 들지 않음), 끝에 Segment 크기·길이·Cues 위치를 고쳐 씀.
// WebmProbe: 시험용 읽기 (트랙·블록 목록).
namespace ComicPadNative.Modules.Media;

/// <summary>EBML 요소 쓰기 도우미</summary>
static class Ebml
{
    public static void Id(Stream s, uint id)
    {
        if (id >= 0x1000000) s.WriteByte((byte)(id >> 24));
        if (id >= 0x10000) s.WriteByte((byte)(id >> 16));
        if (id >= 0x100) s.WriteByte((byte)(id >> 8));
        s.WriteByte((byte)id);
    }
    /// <summary>크기 (가변 길이 정수, 가장 짧게)</summary>
    public static void Size(Stream s, long n)
    {
        int len = 1;
        while (len < 8 && n >= (1L << (7 * len)) - 1) len++;
        for (int i = len - 1; i >= 0; i--)
        {
            byte b = (byte)(n >> (8 * i));
            if (i == len - 1) b |= (byte)(0x80 >> (len - 1));
            s.WriteByte(b);
        }
    }
    /// <summary>8바이트 고정 크기 (나중에 고쳐 쓸 자리)</summary>
    public static void Size8(Stream s, long n)
    {
        s.WriteByte(0x01);
        for (int i = 6; i >= 0; i--) s.WriteByte((byte)(n >> (8 * i)));
    }
    public static void UInt(Stream s, uint id, ulong v)
    {
        int len = 1; while (len < 8 && v >= 1UL << (8 * len)) len++;
        Id(s, id); Size(s, len);
        for (int i = len - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
    }
    public static void UInt8(Stream s, uint id, ulong v) { Id(s, id); Size(s, 8); for (int i = 7; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i))); }
    public static void Float(Stream s, uint id, double v)
    {
        Id(s, id); Size(s, 8);
        var b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian) Array.Reverse(b);
        s.Write(b);
    }
    public static void Str(Stream s, uint id, string v) { var b = System.Text.Encoding.UTF8.GetBytes(v); Bytes(s, id, b); }
    public static void Bytes(Stream s, uint id, byte[] b) { Id(s, id); Size(s, b.Length); s.Write(b); }
    public static void Master(Stream s, uint id, Action<MemoryStream> body)
    {
        var m = new MemoryStream(); body(m);
        Id(s, id); Size(s, m.Length); m.Position = 0; m.CopyTo(s);
    }
}

/// <summary>WebM 소리 트랙 정보</summary>
public sealed class WebmAudio
{
    public string Codec = "A_VORBIS"; public byte[] Private; public int Rate, Channels;
}

public sealed class WebmWriter : IDisposable
{
    const uint EBML = 0x1A45DFA3, Segment = 0x18538067, SeekHead = 0x114D9B74, Seek = 0x4DBB, SeekID = 0x53AB, SeekPosition = 0x53AC,
        Info = 0x1549A966, TimecodeScale = 0x2AD7B1, Duration = 0x4489, MuxingApp = 0x4D80, WritingApp = 0x5741,
        Tracks = 0x1654AE6B, TrackEntry = 0xAE, TrackNumber = 0xD7, TrackUID = 0x73C5, TrackType = 0x83, FlagLacing = 0x9C, CodecID = 0x86,
        CodecPrivate = 0x63A2, DefaultDuration = 0x23E383, Video = 0xE0, PixelWidth = 0xB0, PixelHeight = 0xBA, Audio = 0xE1,
        SamplingFrequency = 0xB5, Channels = 0x9F, Cluster = 0x1F43B675, Timecode = 0xE7, SimpleBlock = 0xA3,
        Cues = 0x1C53BB6B, CuePoint = 0xBB, CueTime = 0xB3, CueTrackPositions = 0xB7, CueTrack = 0xF7, CueClusterPosition = 0xF1;

    readonly FileStream fs;
    readonly string path, tmp;
    readonly long segSizeAt, segStart, durAt, cuesPosAt;
    readonly List<(long t, long pos)> cues = new();
    readonly Queue<(long t, byte[] data)> pendingAudio = new();
    MemoryStream cluster; long clusterT = -1, clusterPos;
    long lastT; bool finished;
    readonly bool hasAudio;
    public int Width { get; } public int Height { get; }

    public WebmWriter(string path, int width, int height, double fps, WebmAudio audio = null)
    {
        this.path = path; Width = width; Height = height; hasAudio = audio != null;
        tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "~" + Path.GetFileNameWithoutExtension(path) + ".part.webm");
        fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        Ebml.Master(fs, EBML, m =>
        {
            Ebml.UInt(m, 0x4286, 1); Ebml.UInt(m, 0x42F7, 1); Ebml.UInt(m, 0x42F2, 4); Ebml.UInt(m, 0x42F3, 8);
            Ebml.Str(m, 0x4282, "webm"); Ebml.UInt(m, 0x4287, 4); Ebml.UInt(m, 0x4285, 2);
        });
        Ebml.Id(fs, Segment); segSizeAt = fs.Position; Ebml.Size8(fs, 0); segStart = fs.Position;
        // SeekHead: Info·Tracks·Cues (Cues 위치는 끝에 고쳐 씀 — 8바이트 고정)
        long infoPos, tracksPos;
        var sh = new MemoryStream();
        void SeekEntry(MemoryStream m, uint id, out long valueAt)
        {
            var e = new MemoryStream();
            Ebml.Bytes(e, SeekID, new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id });
            long at = e.Position; Ebml.UInt8(e, SeekPosition, 0);
            Ebml.Id(m, Seek); Ebml.Size(m, e.Length);
            valueAt = m.Position + at + 2 + 1;   // SeekPosition id(2) + 크기(1) 뒤 값
            e.Position = 0; e.CopyTo(m);
        }
        SeekEntry(sh, Info, out var infoAt); SeekEntry(sh, Tracks, out var tracksAt); SeekEntry(sh, Cues, out var cuesAt);
        Ebml.Id(fs, SeekHead); Ebml.Size(fs, sh.Length);
        long shBody = fs.Position;
        sh.Position = 0; sh.CopyTo(fs);
        // Info
        infoPos = fs.Position - segStart;
        long durRel = 0;
        var info = new MemoryStream();
        Ebml.UInt(info, TimecodeScale, 1_000_000);   // 1ms
        durRel = info.Position; Ebml.Float(info, Duration, 0);
        Ebml.Str(info, MuxingApp, "ComicPad"); Ebml.Str(info, WritingApp, "ComicPad");
        Ebml.Id(fs, Info); Ebml.Size(fs, info.Length);
        durAt = fs.Position + durRel + 2 + 1;
        info.Position = 0; info.CopyTo(fs);
        // Tracks
        tracksPos = fs.Position - segStart;
        Ebml.Master(fs, Tracks, m =>
        {
            Ebml.Master(m, TrackEntry, t =>
            {
                Ebml.UInt(t, TrackNumber, 1); Ebml.UInt(t, TrackUID, 1); Ebml.UInt(t, TrackType, 1); Ebml.UInt(t, FlagLacing, 0);
                Ebml.Str(t, CodecID, "V_VP8");
                if (fps > 0) Ebml.UInt(t, DefaultDuration, (ulong)Math.Round(1e9 / fps));
                Ebml.Master(t, Video, v => { Ebml.UInt(v, PixelWidth, (ulong)width); Ebml.UInt(v, PixelHeight, (ulong)height); });
            });
            if (audio != null)
                Ebml.Master(m, TrackEntry, t =>
                {
                    Ebml.UInt(t, TrackNumber, 2); Ebml.UInt(t, TrackUID, 2); Ebml.UInt(t, TrackType, 2); Ebml.UInt(t, FlagLacing, 0);
                    Ebml.Str(t, CodecID, audio.Codec);
                    if (audio.Private != null) Ebml.Bytes(t, CodecPrivate, audio.Private);
                    Ebml.Master(t, Audio, a => { Ebml.Float(a, SamplingFrequency, audio.Rate); Ebml.UInt(a, Channels, (ulong)audio.Channels); });
                });
        });
        // SeekHead 값 채우기
        long end = fs.Position;
        PatchU64(shBody + infoAt, (ulong)infoPos); PatchU64(shBody + tracksAt, (ulong)tracksPos);
        cuesPosAt = shBody + cuesAt;
        fs.Position = end;
    }

    void PatchU64(long at, ulong v)
    {
        long keep = fs.Position; fs.Position = at;
        for (int i = 7; i >= 0; i--) fs.WriteByte((byte)(v >> (8 * i)));
        fs.Position = keep;
    }

    void CloseCluster()
    {
        if (cluster == null) return;
        Ebml.Id(fs, Cluster); Ebml.Size(fs, cluster.Length);
        cluster.Position = 0; cluster.CopyTo(fs);
        cluster = null;
    }

    void Block(int track, long t, byte[] data, bool key)
    {
        bool newCluster = cluster == null || t - clusterT > 30000 || (key && track == 1 && (t - clusterT >= 1000 || cluster.Length > 4 << 20));
        if (newCluster)
        {
            CloseCluster();
            clusterPos = fs.Position - segStart; clusterT = t;
            cluster = new MemoryStream();
            Ebml.UInt(cluster, Timecode, (ulong)t);
            if (track == 1 && key) cues.Add((t, clusterPos));
        }
        Ebml.Id(cluster, SimpleBlock); Ebml.Size(cluster, data.Length + 4);
        cluster.WriteByte((byte)(0x80 | track));
        short rel = (short)(t - clusterT);
        cluster.WriteByte((byte)(rel >> 8)); cluster.WriteByte((byte)rel);
        cluster.WriteByte((byte)(key ? 0x80 : 0));
        cluster.Write(data);
        lastT = Math.Max(lastT, t);
    }

    /// <summary>영상 한 장 (VP8 프레임, ms). 그 시각까지의 소리 묶음을 먼저 넣음</summary>
    public void AddVideo(byte[] vp8, long timeMs, bool key = true)
    {
        FlushAudio(timeMs);
        Block(1, timeMs, vp8, key);
    }

    /// <summary>소리 묶음 (시각 순서대로). 영상 시각에 맞춰 끼워 넣음</summary>
    public void AddAudio(byte[] packet, long timeMs)
    {
        if (!hasAudio) throw new InvalidOperationException("소리 트랙이 없습니다");
        pendingAudio.Enqueue((timeMs, packet));
    }

    void FlushAudio(long untilMs)
    {
        while (pendingAudio.Count > 0 && pendingAudio.Peek().t <= untilMs) { var (t, d) = pendingAudio.Dequeue(); Block(2, t, d, true); }
    }

    /// <summary>마무리: 남은 소리·Cues·길이·크기를 쓰고 파일 이름을 제자리로</summary>
    public void Finish(long durationMs)
    {
        if (finished) return;
        FlushAudio(long.MaxValue);
        CloseCluster();
        long cuesPos = fs.Position - segStart;
        Ebml.Master(fs, Cues, m =>
        {
            foreach (var (t, pos) in cues)
                Ebml.Master(m, CuePoint, c =>
                {
                    Ebml.UInt(c, CueTime, (ulong)t);
                    Ebml.Master(c, CueTrackPositions, p => { Ebml.UInt(p, CueTrack, 1); Ebml.UInt(p, CueClusterPosition, (ulong)pos); });
                });
        });
        long end = fs.Position;
        PatchU64(cuesPosAt, (ulong)cuesPos);
        fs.Position = durAt;
        var b = BitConverter.GetBytes((double)Math.Max(durationMs, lastT)); if (BitConverter.IsLittleEndian) Array.Reverse(b);
        fs.Write(b);
        fs.Position = segSizeAt; Ebml.Size8(fs, end - segStart);
        fs.Position = end;
        fs.Dispose();
        finished = true;
        File.Move(tmp, path, true);
    }

    public void Dispose()
    {
        if (!finished) { try { fs.Dispose(); File.Delete(tmp); } catch { } finished = true; }
    }

    /// <summary>Vorbis 머리 세 개 → Matroska CodecPrivate (Xiph 묶음)</summary>
    public static byte[] XiphLace(params byte[][] packets)
    {
        var m = new MemoryStream();
        m.WriteByte((byte)(packets.Length - 1));
        for (int i = 0; i < packets.Length - 1; i++) { int n = packets[i].Length; while (n >= 255) { m.WriteByte(255); n -= 255; } m.WriteByte((byte)n); }
        foreach (var p in packets) m.Write(p);
        return m.ToArray();
    }
}

/// <summary>시험용 WebM 읽기: 트랙(번호·코덱·크기)과 블록(트랙·시각·키·데이터)</summary>
public static class WebmProbe
{
    public sealed class Track { public int Number, Type, W, H, Channels; public string Codec; public double Rate; public byte[] Private; }
    public sealed class Result
    {
        public string DocType; public double Duration; public long Scale = 1_000_000;
        public List<Track> Tracks = new(); public List<(int track, long t, bool key, byte[] data)> Blocks = new(); public int CuePoints, Clusters;
    }
    static readonly HashSet<uint> Masters = new() { 0x1A45DFA3, 0x18538067, 0x114D9B74, 0x4DBB, 0x1549A966, 0x1654AE6B, 0xAE, 0xE0, 0xE1, 0x1F43B675, 0x1C53BB6B, 0xBB, 0xB7 };

    static (uint id, int len) ReadId(byte[] b, int p)
    {
        int len = b[p] >= 0x80 ? 1 : b[p] >= 0x40 ? 2 : b[p] >= 0x20 ? 3 : 4;
        uint id = 0; for (int i = 0; i < len; i++) id = (id << 8) | b[p + i];
        return (id, len);
    }
    static (long size, int len) ReadSize(byte[] b, int p)
    {
        int len = 1; while (len <= 8 && (b[p] & (0x80 >> (len - 1))) == 0) len++;
        long v = b[p] & ((0x80 >> (len - 1)) - 1);
        for (int i = 1; i < len; i++) v = (v << 8) | b[p + i];
        return (v, len);
    }
    static ulong U(byte[] b, int p, long n) { ulong v = 0; for (int i = 0; i < n; i++) v = (v << 8) | b[p + i]; return v; }

    public static Result Read(byte[] b)
    {
        var r = new Result();
        Track cur = null; long clusterT = 0;
        void Walk(int p, int end)
        {
            while (p < end)
            {
                var (id, il) = ReadId(b, p); var (size, sl) = ReadSize(b, p + il);
                int body = p + il + sl; int bend = (int)Math.Min(end, body + size);
                if (Masters.Contains(id))
                {
                    if (id == 0xAE) { cur = new Track(); r.Tracks.Add(cur); }
                    if (id == 0x1F43B675) r.Clusters++;
                    if (id == 0xBB) r.CuePoints++;
                    Walk(body, bend);
                }
                else switch (id)
                {
                    case 0x4282: r.DocType = System.Text.Encoding.ASCII.GetString(b, body, (int)size); break;
                    case 0x2AD7B1: r.Scale = (long)U(b, body, size); break;
                    case 0x4489: { var x = b[body..(body + (int)size)]; Array.Reverse(x); r.Duration = size == 8 ? BitConverter.ToDouble(x) : BitConverter.ToSingle(x); break; }
                    case 0xD7: cur.Number = (int)U(b, body, size); break;
                    case 0x83: cur.Type = (int)U(b, body, size); break;
                    case 0x86: cur.Codec = System.Text.Encoding.ASCII.GetString(b, body, (int)size); break;
                    case 0x63A2: cur.Private = b[body..bend]; break;
                    case 0xB0: cur.W = (int)U(b, body, size); break;
                    case 0xBA: cur.H = (int)U(b, body, size); break;
                    case 0x9F: cur.Channels = (int)U(b, body, size); break;
                    case 0xB5: { var x = b[body..(body + (int)size)]; Array.Reverse(x); cur.Rate = size == 8 ? BitConverter.ToDouble(x) : BitConverter.ToSingle(x); break; }
                    case 0xE7: clusterT = (long)U(b, body, size); break;
                    case 0xA3:
                    {
                        int track = b[body] & 0x7f;
                        short rel = (short)((b[body + 1] << 8) | b[body + 2]);
                        r.Blocks.Add((track, clusterT + rel, (b[body + 3] & 0x80) != 0, b[(body + 4)..bend]));
                        break;
                    }
                }
                p = bend;
            }
        }
        Walk(0, b.Length);
        return r;
    }
}
