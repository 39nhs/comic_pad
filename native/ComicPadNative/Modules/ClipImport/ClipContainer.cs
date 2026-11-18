// 클립 스튜디오 파일(.clip) 그릇 읽기 + 픽셀(Offscreen) 풀기.
// .clip 형식은 공개 문서가 없다. 아래 공개 역분석 자료를 읽고 설명대로 직접 짰다 (코드 복사 없음):
//  - nanikasheila/csp-format (MIT) docs/spec 01-container · 03-canvas-and-layers · 04-raster
//      https://github.com/nanikasheila/csp-format
//  - dobrokot/clip_to_psd (MIT) clip_to_psd.py — 블록 머리·Offscreen.Attribute 읽는 순서 교차 확인
//      https://github.com/dobrokot/clip_to_psd
// 구조 요약 (모든 정수는 빅엔디언, 이름은 UTF-16BE):
//   파일 머리 24B: "CSFCHUNK", u64 파일 크기, u64 첫 덩어리 위치(24)
//   덩어리: 8B 태그("CHNKHead"/"CHNKExta"/"CHNKSQLi"/"CHNKFoot"), u64 길이, 본문
//   CHNKExta 본문: u64 40, 외부 ID 40B("extrnlid"+16진 32자), u64 나머지 길이, 블록 기록들
//   블록 기록: u32 기록 길이, u32 19 "BlockDataBeginChunk", u32 번호, u32 푼 크기, u32 256, u32 256, u32 데이터 있음,
//             [u32 4+n, u32(LE) n, zlib n바이트], u32 17 "BlockDataEndChunk"
//   그 뒤 "BlockStatus" / "BlockCheckSum" 표: u32 이름 길이, 이름, u32 12, u32 개수, u32 4, u32×개수
//   Offscreen.Attribute: u32 16, u32×3 (Parameter·InitColor·BlockSize 길이), 각 부분 = 이름 + u32 값들
//   256×256 블록, 알파 면이 먼저, 그다음 색 면. 색(0x21)은 BGRx, 알파를 곱하지 않은 값.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.ClipImport;

/// <summary>.clip 파일 하나: 덩어리 목록 + SQLite + 외부 데이터 찾기. Dispose 로 파일 닫기</summary>
public sealed class ClipContainer : IDisposable
{
    readonly FileStream fs;
    readonly SafeFileHandle h;
    public readonly long FileLength;
    public byte[] Sql;                                            // CHNKSQLi 본문
    public readonly Dictionary<string, (long off, long len)> Exta = new(StringComparer.Ordinal);   // 외부 ID → 블록 기록 위치
    public readonly List<string> Warnings = new();

    public ClipContainer(string path)
    {
        fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
        h = fs.SafeFileHandle;
        FileLength = fs.Length;
        try { Parse(); }
        catch { fs.Dispose(); throw; }
    }

    public void Dispose() => fs.Dispose();

    byte[] ReadAt(long off, int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = RandomAccess.Read(h, buf.AsSpan(got), off + got);
            if (r <= 0) throw new EndOfStreamException("파일이 중간에 끊겼습니다");
            got += r;
        }
        return buf;
    }

    static long U64(byte[] b, int o) => (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));

    void Parse()
    {
        if (FileLength < 40) throw new InvalidDataException("클립 스튜디오 파일이 아닙니다 (너무 작음)");
        var head = ReadAt(0, 24);
        if (Encoding.ASCII.GetString(head, 0, 8) != "CSFCHUNK") throw new InvalidDataException("클립 스튜디오 파일(.clip)이 아닙니다 (CSFCHUNK 머리 없음)");
        long declared = U64(head, 8), first = U64(head, 16);
        if (declared != FileLength) Warnings.Add($"파일 크기가 머리에 적힌 값과 다릅니다 ({FileLength:N0} / {declared:N0}) — 잘린 파일일 수 있음");
        long pos = first >= 24 && first < FileLength ? first : 24;
        bool foot = false;
        while (pos + 16 <= FileLength)
        {
            var ch = ReadAt(pos, 16);
            string tag = Encoding.ASCII.GetString(ch, 0, 8);
            long len = U64(ch, 8);
            long body = pos + 16;
            if (!tag.StartsWith("CHNK") || len < 0 || body + len > FileLength)
            {
                Warnings.Add($"덩어리 목록이 {pos:N0} 바이트에서 깨졌습니다 ({tag.Replace('\0', ' ')}) — 읽은 데까지만 씁니다");
                break;
            }
            switch (tag)
            {
                case "CHNKSQLi":
                    if (len > int.MaxValue) throw new InvalidDataException("데이터베이스 덩어리가 너무 큽니다");
                    Sql = ReadAt(body, (int)len);
                    break;
                case "CHNKExta":
                    if (len >= 16)
                    {
                        var a = ReadAt(body, (int)Math.Min(len, 8 + 256 + 8));
                        long idLen = U64(a, 0);
                        if (idLen > 0 && idLen <= 256 && 8 + idLen + 8 <= len)
                        {
                            string id = Encoding.ASCII.GetString(a, 8, (int)idLen).TrimEnd('\0');
                            long dataLen = U64(a, 8 + (int)idLen);
                            long dataOff = body + 8 + idLen + 8;
                            long avail = body + len - dataOff;
                            if (dataLen < 0 || dataLen > avail) dataLen = avail;
                            Exta[id] = (dataOff, dataLen);
                        }
                    }
                    break;
                case "CHNKFoot": foot = true; break;
            }
            if (foot) break;
            pos = body + len;
        }
        if (Sql == null) throw new InvalidDataException("레이어 정보(데이터베이스)가 없습니다 — 지원하지 않는 버전이거나 깨진 파일입니다");
        if (!foot) Warnings.Add("파일 끝 표시(CHNKFoot)가 없습니다 — 잘린 파일일 수 있음");
    }

    /// <summary>외부 데이터 본문 (없으면 null)</summary>
    public byte[] ExtaData(string id)
    {
        if (id == null || !Exta.TryGetValue(id, out var e)) return null;
        if (e.len > int.MaxValue) throw new InvalidDataException("외부 데이터가 너무 큽니다");
        return ReadAt(e.off, (int)e.len);
    }
}

/// <summary>Offscreen.Attribute: 픽셀 면의 크기·형식·초기색</summary>
public sealed class OffscreenInfo
{
    public int W, H, BX, BY;
    public int ChannelOrder = -1, ImageChannels = -1, PixelBytes = -1, ImageOffset = -1, AlphaRowBytes = -1;
    public bool DoInit; public int InitAlpha;
    public bool Valid => W > 0 && H > 0 && BX > 0 && BY > 0;

    public static OffscreenInfo Parse(byte[] a)
    {
        var o = new OffscreenInfo();
        if (a == null || a.Length < 16) return o;
        try
        {
            int p = 0;
            uint U() { uint v = BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(p)); p += 4; return v; }
            uint hdr = U();
            if (hdr < 16 || hdr > a.Length) return o;
            var lens = new List<uint>();
            while (p < hdr) lens.Add(U());
            p = (int)hdr;
            foreach (uint sl in lens)
            {
                int start = p;
                if (sl < 4 || start + sl > a.Length) break;
                int nl = (int)U();
                if (nl < 0 || p + nl * 2 > start + sl) break;
                string name = Encoding.BigEndianUnicode.GetString(a, p, nl * 2); p += nl * 2;
                var vals = new List<uint>();
                while (p + 4 <= start + sl) vals.Add(U());
                p = start + (int)sl;
                if (name == "Parameter" && vals.Count >= 4)
                {
                    o.W = (int)vals[0]; o.H = (int)vals[1]; o.BX = (int)vals[2]; o.BY = (int)vals[3];
                    if (vals.Count >= 13)
                    {
                        o.ChannelOrder = (int)vals[4]; o.ImageChannels = (int)vals[6]; o.PixelBytes = (int)vals[7];
                        o.ImageOffset = (int)vals[8]; o.AlphaRowBytes = (int)vals[12];
                    }
                }
                else if (name == "InitColor" && vals.Count >= 3)
                {
                    o.DoInit = vals[1] != 0;
                    uint v = vals[2];
                    o.InitAlpha = (int)((v & 0xFF) != 0 || v == 0 ? v & 0xFF : v >> 24);
                }
            }
        }
        catch (ArgumentOutOfRangeException) { }
        // 블록 개수가 이상하면 크기로 다시 셈
        if (o.W > 0 && o.H > 0 && (o.BX != (o.W + 255) / 256 || o.BY != (o.H + 255) / 256)) { o.BX = (o.W + 255) / 256; o.BY = (o.H + 255) / 256; }
        if (o.W > 1 << 20 || o.H > 1 << 20) { o.W = o.H = 0; }
        return o;
    }
}

/// <summary>블록 기록 풀기 + 픽셀 변환</summary>
public static class ClipBlocks
{
    static readonly byte[] Begin = Encoding.BigEndianUnicode.GetBytes("BlockDataBeginChunk");
    static readonly byte[] End = Encoding.BigEndianUnicode.GetBytes("BlockDataEndChunk");
    static readonly byte[] Status = Encoding.BigEndianUnicode.GetBytes("BlockStatus");
    static readonly byte[] Check = Encoding.BigEndianUnicode.GetBytes("BlockCheckSum");

    public sealed class Block { public int Index; public int RawSize; public int ZOff, ZLen; }

    /// <summary>외부 데이터에서 블록 목록 (데이터 없는 블록은 빠짐). data 의 처음 u64 는 ExtaData 가 이미 건너뜀</summary>
    public static List<Block> List(byte[] d, List<string> warn)
    {
        var res = new List<Block>();
        int i = 0, seq = 0;
        static uint U(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
        bool At(int o, byte[] name) => o + name.Length <= d.Length && d.AsSpan(o, name.Length).SequenceEqual(name);
        while (i + 8 <= d.Length)
        {
            if (U(d, i + 4) == 19 && At(i + 8, Begin))
            {
                int recLen = (int)U(d, i);
                if (recLen < 104 || i + recLen > d.Length) { warn?.Add("블록 기록 길이가 이상합니다"); break; }
                int p = i + 8 + Begin.Length;
                int index = (int)U(d, p), raw = (int)U(d, p + 4), has = (int)U(d, p + 16);
                if (has != 0 && recLen >= 112)
                {
                    int stored = (int)U(d, p + 20);
                    int zl = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 24));
                    if (zl <= 0 || zl != stored - 4) zl = stored - 4;               // 두 길이가 다르면 앞의 것을 믿음
                    int zo = p + 28;
                    if (zl > 0 && zo + zl <= i + recLen)
                        res.Add(new Block { Index = index >= 0 ? index : seq, RawSize = raw, ZOff = zo, ZLen = zl });
                    else warn?.Add("블록 데이터 길이가 이상합니다");
                }
                seq++;
                i += recLen;
            }
            else if (U(d, i) == 11 && At(i + 4, Status) || U(d, i) == 13 && At(i + 4, Check))
            {
                int p = i + 4 + (int)U(d, i) * 2;
                if (p + 12 > d.Length) break;
                int count = (int)U(d, p + 4), es = (int)U(d, p + 8);
                if (count < 0 || es is < 0 or > 16) break;
                i = p + 12 + count * es;
            }
            else
            {
                // 모르는 기록: 앞 u32 를 길이로 보고 건너뛰기 (안 되면 그만)
                int recLen = (int)U(d, i);
                if (recLen < 8 || i + recLen > d.Length) break;
                i += recLen;
            }
        }
        return res;
    }

    public static byte[] Inflate(byte[] d, Block b)
    {
        using var ms = new MemoryStream(d, b.ZOff, b.ZLen, false);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        int cap = b.RawSize > 0 && b.RawSize <= 4 << 20 ? b.RawSize : 327680;
        var outp = new byte[cap];
        int got = 0;
        while (true)
        {
            if (got == outp.Length)
            {
                if (outp.Length >= 8 << 20) break;
                Array.Resize(ref outp, outp.Length * 2);
            }
            int r = z.Read(outp, got, outp.Length - got);
            if (r <= 0) break;
            got += r;
        }
        if (got != outp.Length) Array.Resize(ref outp, got);
        return outp;
    }

    public enum Fmt { Color, Gray, Mono, Alpha, Unknown }

    public static Fmt FormatOf(OffscreenInfo info, int rawLen)
    {
        if (info.ImageChannels == 4 || info.ChannelOrder == 0x21) return rawLen >= 327680 ? Fmt.Color : Fmt.Unknown;
        if (info.ImageChannels == 1 && info.AlphaRowBytes == 32) return rawLen >= 16384 ? Fmt.Mono : Fmt.Unknown;
        if (info.ImageChannels == 1) return rawLen >= 131072 ? Fmt.Gray : Fmt.Unknown;
        if (info.ImageChannels == 0) return rawLen >= 65536 ? Fmt.Alpha : Fmt.Unknown;
        return rawLen switch { 327680 => Fmt.Color, 131072 => Fmt.Gray, 16384 => Fmt.Mono, 65536 => Fmt.Alpha, _ => Fmt.Unknown };
    }

    /// <summary>블록 하나(256×256) → 미리 곱한 BGRA. asMask 면 알파만 (흰색 × 알파). 전부 투명이면 null</summary>
    public static uint[] ToPixels(byte[] raw, Fmt f, bool asMask, int imageOffset)
    {
        const int K = 65536;
        var px = new uint[K];
        bool any = false;
        switch (f)
        {
            case Fmt.Color:
                {
                    int io = imageOffset > 0 && imageOffset + 4 * K <= raw.Length ? imageOffset : K;
                    for (int i = 0; i < K; i++)
                    {
                        uint a = raw[i];
                        if (a == 0) continue;
                        any = true;
                        if (asMask) { px[i] = Px.Pack(a, a, a, a); continue; }
                        int c = io + i * 4;
                        px[i] = Px.Pack(a, Px.Mul(raw[c + 2], a), Px.Mul(raw[c + 1], a), Px.Mul(raw[c], a));
                    }
                    break;
                }
            case Fmt.Gray:
                {
                    int io = imageOffset > 0 && imageOffset + K <= raw.Length ? imageOffset : K;
                    for (int i = 0; i < K; i++)
                    {
                        uint a = raw[i];
                        if (a == 0) continue;
                        any = true;
                        uint g = asMask ? a : Px.Mul(raw[io + i], a);
                        px[i] = Px.Pack(a, g, g, g);
                    }
                    break;
                }
            case Fmt.Mono:
                {
                    int io = imageOffset > 0 && imageOffset + 8192 <= raw.Length ? imageOffset : 8192;
                    for (int y = 0; y < 256; y++)
                        for (int x = 0; x < 256; x++)
                        {
                            int bi = y * 32 + (x >> 3), bit = 0x80 >> (x & 7);
                            if ((raw[bi] & bit) == 0) continue;
                            any = true;
                            bool white = (raw[io + bi] & bit) != 0;
                            px[y * 256 + x] = asMask || white ? 0xFFFFFFFF : 0xFF000000;
                        }
                    break;
                }
            case Fmt.Alpha:
                for (int i = 0; i < K; i++)
                {
                    uint a = raw[i];
                    if (a == 0) continue;
                    any = true;
                    px[i] = Px.Pack(a, a, a, a);
                }
                break;
            default: return null;
        }
        return any ? px : null;
    }
}
