// 시험용: 합성 .clip 파일 만들기 (진짜 클립 스튜디오 견본은 재배포가 막혀 있어 공개 문서대로 직접 씀).
//  - SqliteWriter: 최소 SQLite 3 파일 쓰기 (표 B-트리, 잎/내부 쪽, 넘침 쪽). sqlite.org 파일 형식 문서대로.
//    개발 중 파이썬 sqlite3 의 PRAGMA integrity_check 로 결과가 올바른 SQLite 파일인지 확인함.
//  - ClipWriter: CSFCHUNK 그릇 + CHNKExta 블록 기록 + Offscreen.Attribute (ClipContainer.cs 머리말의 구조)
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ComicPadNative.Modules.ClipImport;

public sealed class SqliteWriter
{
    readonly int ps;
    readonly List<byte[]> pages = new();
    readonly List<(string name, string sql, List<object[]> rows, int rowidCol)> tables = new();

    public SqliteWriter(int pageSize = 4096) { ps = pageSize; }

    /// <summary>열 정의 예: "_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT", "MainId INTEGER", "LayerName TEXT"</summary>
    public void AddTable(string name, string[] colDefs, IEnumerable<object[]> rows)
    {
        string sql = $"CREATE TABLE {name} ({string.Join(", ", colDefs)})";
        var (_, rowid, _) = SqliteReader.ParseColumns(sql);
        tables.Add((name, sql, rows.ToList(), rowid));
    }

    int Alloc() { pages.Add(new byte[ps]); return pages.Count; }

    public byte[] Build()
    {
        pages.Clear();
        Alloc();                                  // 1쪽 = sqlite_master
        var master = new List<(long, byte[])>();
        long mrow = 1;
        foreach (var (name, sql, rows, rowidCol) in tables)
        {
            var recs = new List<(long rowid, byte[] payload)>();
            long auto = 1;
            foreach (var r in rows)
            {
                long rowid = rowidCol >= 0 && r[rowidCol] is long l ? l : auto;
                auto = Math.Max(auto, rowid) + 1;
                var vals = (object[])r.Clone();
                if (rowidCol >= 0) vals[rowidCol] = null;
                recs.Add((rowid, Record(vals)));
            }
            recs.Sort((a, b) => a.rowid.CompareTo(b.rowid));
            int root = BuildTree(recs);
            master.Add((mrow++, Record(new object[] { "table", name, name, (long)root, sql })));
        }
        // 1쪽에 sqlite_master 잎 (100바이트 파일 머리 뒤)
        var cells = master.Select(m => LeafCell(m.Item1, m.Item2)).ToList();
        if (!WriteLeaf(pages[0], 100, cells)) throw new InvalidOperationException("스키마가 1쪽에 다 들어가지 않음");
        var h = pages[0];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(16), (ushort)(ps == 65536 ? 1 : ps));
        h[18] = 1; h[19] = 1; h[20] = 0; h[21] = 64; h[22] = 32; h[23] = 32;
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(28), (uint)pages.Count);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(44), 4);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(92), 1);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(96), 3030001);
        var outp = new byte[pages.Count * ps];
        for (int i = 0; i < pages.Count; i++) pages[i].CopyTo(outp, i * ps);
        return outp;
    }

    int BuildTree(List<(long rowid, byte[] payload)> recs)
    {
        var level = new List<(int page, long maxKey)>();
        var cur = new List<byte[]>(); long curMax = 0; int used = 8;
        void Flush()
        {
            int pg = Alloc();
            if (!WriteLeaf(pages[pg - 1], 0, cur)) throw new InvalidOperationException("잎 쪽 넘침");
            level.Add((pg, curMax)); cur.Clear(); used = 8;
        }
        foreach (var (rowid, payload) in recs)
        {
            var cell = LeafCell(rowid, payload);
            if (cur.Count > 0 && used + 2 + cell.Length > ps) Flush();
            cur.Add(cell); used += 2 + cell.Length; curMax = rowid;
        }
        if (cur.Count > 0 || level.Count == 0) Flush();
        while (level.Count > 1)
        {
            var next = new List<(int, long)>();
            int cap = (ps - 12) / (2 + 13);
            for (int i = 0; i < level.Count; i += cap + 1)
            {
                var grp = level.GetRange(i, Math.Min(cap + 1, level.Count - i));
                int pg = Alloc();
                var p = pages[pg - 1];
                var ic = grp.Take(grp.Count - 1).Select(g => { var c = new List<byte>(); var b4 = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b4, (uint)g.page); c.AddRange(b4); c.AddRange(Varint(g.maxKey)); return c.ToArray(); }).ToList();
                int end = ps;
                p[0] = 5;
                BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(3), (ushort)ic.Count);
                for (int k = 0; k < ic.Count; k++) { end -= ic[k].Length; ic[k].CopyTo(p, end); BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(12 + k * 2), (ushort)end); }
                BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(5), (ushort)(end == 65536 ? 0 : end));
                BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), (uint)grp[^1].page);
                next.Add((pg, grp[^1].maxKey));
            }
            level = next;
        }
        return level[0].page;
    }

    bool WriteLeaf(byte[] p, int h, List<byte[]> cells)
    {
        int end = ps;
        int hdrEnd = h + 8 + cells.Count * 2;
        p[h] = 13;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(h + 3), (ushort)cells.Count);
        for (int k = 0; k < cells.Count; k++)
        {
            end -= cells[k].Length;
            if (end < hdrEnd) return false;
            cells[k].CopyTo(p, end);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(h + 8 + k * 2), (ushort)end);
        }
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(h + 5), (ushort)(end == 65536 ? 0 : end));
        return true;
    }

    byte[] LeafCell(long rowid, byte[] payload)
    {
        int P = payload.Length, U = ps, X = U - 35;
        int local;
        if (P <= X) local = P;
        else { int M = (U - 12) * 32 / 255 - 23, K = M + (P - M) % (U - 4); local = K <= X ? K : M; }
        var c = new List<byte>();
        c.AddRange(Varint(P)); c.AddRange(Varint(rowid));
        c.AddRange(payload.AsSpan(0, local).ToArray());
        if (local < P)
        {
            // 넘침 쪽 사슬
            int first = 0, prev = 0, at = local;
            while (at < P)
            {
                int pg = Alloc();
                if (prev != 0) BinaryPrimitives.WriteUInt32BigEndian(pages[prev - 1], (uint)pg); else first = pg;
                int take = Math.Min(U - 4, P - at);
                Array.Copy(payload, at, pages[pg - 1], 4, take);
                at += take; prev = pg;
            }
            var b4 = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b4, (uint)first); c.AddRange(b4);
        }
        return c.ToArray();
    }

    static byte[] Varint(long v)
    {
        ulong u = (ulong)v;
        if (u <= 0x7f) return new[] { (byte)u };
        if ((u & 0xff00000000000000UL) != 0)
        {
            var r9 = new byte[9]; r9[8] = (byte)u; u >>= 8;
            for (int i = 7; i >= 0; i--) { r9[i] = (byte)((u & 0x7f) | 0x80); u >>= 7; }
            return r9;
        }
        var t = new List<byte>();
        while (u != 0) { t.Add((byte)(u & 0x7f)); u >>= 7; }
        t.Reverse();
        for (int i = 0; i < t.Count - 1; i++) t[i] |= 0x80;
        return t.ToArray();
    }

    static byte[] Record(object[] vals)
    {
        var types = new List<long>(); var body = new List<byte>();
        foreach (var v0 in vals)
        {
            object v = v0 switch { int i => (long)i, bool b => b ? 1L : 0L, uint u => (long)u, float f => (double)f, _ => v0 };
            switch (v)
            {
                case null: types.Add(0); break;
                case long l:
                    if (l == 0) types.Add(8);
                    else if (l == 1) types.Add(9);
                    else if (l >= sbyte.MinValue && l <= sbyte.MaxValue) { types.Add(1); body.Add((byte)l); }
                    else if (l >= short.MinValue && l <= short.MaxValue) { types.Add(2); body.Add((byte)(l >> 8)); body.Add((byte)l); }
                    else if (l >= -(1 << 23) && l < (1 << 23)) { types.Add(3); body.Add((byte)(l >> 16)); body.Add((byte)(l >> 8)); body.Add((byte)l); }
                    else if (l >= int.MinValue && l <= int.MaxValue) { types.Add(4); var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, (int)l); body.AddRange(b); }
                    else if (l >= -(1L << 47) && l < (1L << 47)) { types.Add(5); for (int s = 40; s >= 0; s -= 8) body.Add((byte)(l >> s)); }
                    else { types.Add(6); var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, l); body.AddRange(b); }
                    break;
                case double d: { types.Add(7); var b = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, d); body.AddRange(b); break; }
                case string s: { var b = Encoding.UTF8.GetBytes(s); types.Add(13 + 2L * b.Length); body.AddRange(b); break; }
                case byte[] b: types.Add(12 + 2L * b.Length); body.AddRange(b); break;
                default: throw new ArgumentException("SQLite 값 형식: " + v.GetType());
            }
        }
        var hdr = new List<byte>(); foreach (var t in types) hdr.AddRange(Varint(t));
        int hs = hdr.Count + 1; if (Varint(hs).Length > 1) hs = hdr.Count + Varint(hdr.Count + 2).Length;
        var res = new List<byte>(); res.AddRange(Varint(hs)); res.AddRange(hdr); res.AddRange(body);
        return res.ToArray();
    }
}

/// <summary>합성 .clip 그릇</summary>
public sealed class ClipWriter
{
    readonly List<(string id, byte[] data)> exta = new();
    int seq;

    public string NewId() => "extrnlid" + (++seq).ToString("X32");

    /// <summary>외부 데이터 하나 (블록 기록 등) 추가 → 외부 ID</summary>
    public string AddExta(byte[] data) { var id = NewId(); exta.Add((id, data)); return id; }

    public byte[] Build(byte[] sqlite)
    {
        var ms = new MemoryStream();
        void U64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); ms.Write(b); }
        void Chunk(string tag, byte[] body) { ms.Write(Encoding.ASCII.GetBytes(tag)); U64(body.Length); ms.Write(body); }
        ms.Write(Encoding.ASCII.GetBytes("CSFCHUNK")); U64(0); U64(24);
        // 머리 (CHNKSQLi 위치는 나중에 채움)
        long headPos = ms.Position;
        var head = new byte[40]; BinaryPrimitives.WriteInt64BigEndian(head, 256); BinaryPrimitives.WriteInt64BigEndian(head.AsSpan(16), 16);
        Chunk("CHNKHead", head);
        foreach (var (id, data) in exta)
        {
            var body = new MemoryStream();
            var b8 = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b8, 40); body.Write(b8);
            body.Write(Encoding.ASCII.GetBytes(id));
            BinaryPrimitives.WriteInt64BigEndian(b8, data.Length); body.Write(b8);
            body.Write(data);
            Chunk("CHNKExta", body.ToArray());
        }
        long sqlPos = ms.Position;
        Chunk("CHNKSQLi", sqlite);
        Chunk("CHNKFoot", Array.Empty<byte>());
        var res = ms.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(res.AsSpan(8), res.Length);
        BinaryPrimitives.WriteInt64BigEndian(res.AsSpan((int)headPos + 16 + 8), sqlPos);
        return res;
    }

    // ---- 픽셀 면 ----
    public enum Kind { Color, Gray, Mono, Alpha }

    /// <summary>Offscreen.Attribute 만들기</summary>
    public static byte[] Attribute(int w, int h, Kind k, int[] blockSizes, bool doInit = false, uint initAlpha = 0)
    {
        int bx = (w + 255) / 256, by = (h + 255) / 256;
        uint[] par = k switch
        {
            Kind.Color => new uint[] { 0x21, 1, 4, 5, 65536, 4, 1024, 1, 256 },
            Kind.Gray => new uint[] { 0x11, 1, 1, 2, 65536, 1, 256, 1, 256 },
            Kind.Mono => new uint[] { 0x11, 1, 1, 1, 8192, 1, 32, 1, 32 },
            _ => new uint[] { 0x01, 1, 0, 1, 0, 0, 0, 1, 256 },
        };
        var vals = new List<uint> { (uint)w, (uint)h, (uint)bx, (uint)by };
        vals.AddRange(par); vals.AddRange(new uint[] { 65536, 256, 256, 8, 8, 0, 0 });
        byte[] Sec(string name, IEnumerable<uint> v)
        {
            var s = new MemoryStream(); var b4 = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b4, (uint)name.Length); s.Write(b4);
            s.Write(Encoding.BigEndianUnicode.GetBytes(name));
            foreach (var x in v) { BinaryPrimitives.WriteUInt32BigEndian(b4, x); s.Write(b4); }
            return s.ToArray();
        }
        var p = Sec("Parameter", vals);
        var ic = Sec("InitColor", new uint[] { 0, doInit ? 1u : 0u, initAlpha, 1, 0 });
        var bs = Sec("BlockSize", new[] { (uint)blockSizes.Length }.Concat(blockSizes.Select(x => (uint)x)));
        var o = new MemoryStream(); var hb = new byte[4];
        foreach (var x in new uint[] { 16, (uint)p.Length, (uint)ic.Length, (uint)bs.Length }) { BinaryPrimitives.WriteUInt32BigEndian(hb, x); o.Write(hb); }
        o.Write(p); o.Write(ic); o.Write(bs);
        return o.ToArray();
    }

    /// <summary>블록 기록들 (raw[i] = 블록 i 의 푼 바이트, null = 빈 블록) + 상태·검사합 표. 기록 크기들도 돌려줌</summary>
    public static (byte[] data, int[] sizes) Blocks(byte[][] raw)
    {
        var ms = new MemoryStream(); var b4 = new byte[4];
        void U(uint v) { BinaryPrimitives.WriteUInt32BigEndian(b4, v); ms.Write(b4); }
        var sizes = new int[raw.Length]; var sums = new uint[raw.Length];
        var begin = Encoding.BigEndianUnicode.GetBytes("BlockDataBeginChunk"); var end = Encoding.BigEndianUnicode.GetBytes("BlockDataEndChunk");
        for (int i = 0; i < raw.Length; i++)
        {
            byte[] z = null;
            if (raw[i] != null)
            {
                var zs = new MemoryStream();
                using (var zl = new ZLibStream(zs, CompressionLevel.Fastest, true)) zl.Write(raw[i]);
                z = zs.ToArray();
            }
            int rec = z == null ? 104 : 112 + z.Length;
            sizes[i] = rec;
            U((uint)rec); U(19); ms.Write(begin);
            U((uint)i); U((uint)(raw[i]?.Length ?? 0)); U(256); U(256); U(z == null ? 0u : 1u);
            if (z != null)
            {
                U((uint)z.Length + 4);
                BinaryPrimitives.WriteInt32LittleEndian(b4, z.Length); ms.Write(b4);
                ms.Write(z);
                var lenLe = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(lenLe, z.Length);
                sums[i] = Adler(lenLe.Concat(z).ToArray());
            }
            U(17); ms.Write(end);
        }
        U(11); ms.Write(Encoding.BigEndianUnicode.GetBytes("BlockStatus")); U(12); U((uint)raw.Length); U(4);
        for (int i = 0; i < raw.Length; i++) U(1);
        U(13); ms.Write(Encoding.BigEndianUnicode.GetBytes("BlockCheckSum")); U(12); U((uint)raw.Length); U(4);
        for (int i = 0; i < raw.Length; i++) U(sums[i]);
        return (ms.ToArray(), sizes);
    }

    static uint Adler(byte[] d) { uint a = 1, b = 0; foreach (var x in d) { a = (a + x) % 65521; b = (b + a) % 65521; } return (b << 16) | a; }

    /// <summary>곧은 알파 ARGB 그림(w×h) → 블록별 푼 바이트 (투명 블록은 null)</summary>
    public static byte[][] Raw(uint[] argb, int w, int h, Kind k)
    {
        int bx = (w + 255) / 256, by = (h + 255) / 256;
        var res = new byte[bx * by][];
        for (int j = 0; j < by; j++)
            for (int i = 0; i < bx; i++)
            {
                bool any = false;
                byte[] r = k switch { Kind.Color => new byte[327680], Kind.Gray => new byte[131072], Kind.Mono => new byte[16384], _ => new byte[65536] };
                for (int y = 0; y < 256; y++)
                    for (int x = 0; x < 256; x++)
                    {
                        int gx = i * 256 + x, gy = j * 256 + y;
                        if (gx >= w || gy >= h) continue;
                        uint c = argb[gy * w + gx]; byte a = (byte)(c >> 24);
                        if (a == 0) continue;
                        any = true;
                        int q = y * 256 + x;
                        switch (k)
                        {
                            case Kind.Color: r[q] = a; r[65536 + q * 4] = (byte)c; r[65536 + q * 4 + 1] = (byte)(c >> 8); r[65536 + q * 4 + 2] = (byte)(c >> 16); break;
                            case Kind.Gray: r[q] = a; r[65536 + q] = (byte)c; break;
                            case Kind.Mono:
                                {
                                    int bi = y * 32 + (x >> 3); byte bit = (byte)(0x80 >> (x & 7));
                                    r[bi] |= bit; if ((c & 0xFF) >= 128) r[8192 + bi] |= bit; break;
                                }
                            default: r[q] = a; break;
                        }
                    }
                res[j * bx + i] = any ? r : null;
            }
        return res;
    }
}
