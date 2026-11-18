// 읽기 전용 SQLite 3 파일 읽기 — 클립 스튜디오 파일(.clip) 안의 CHNKSQLi 덩어리를 읽는 데 쓴다.
// 공개 문서 "Database File Format" (https://www.sqlite.org/fileformat2.html, 퍼블릭 도메인) 를 보고 직접 짰다.
// Microsoft.Data.Sqlite 를 쓰지 않은 까닭: 그 패키지는 e_sqlite3 네이티브 DLL(플랫폼마다 1~2MB)을 끌고 오고,
// 이 앱은 ONNX 말고는 외부 패키지 없이 가는 방침. 여기서는 표(table) B-트리를 처음부터 끝까지 훑기만 하면 되므로
// (색인·쓰기·트랜잭션·WAL 불필요) 300줄 안쪽으로 충분하다.
// 지원: 페이지 크기 512~65536, 넘침(overflow) 페이지, 내부/잎 표 페이지, INTEGER PRIMARY KEY(rowid 별칭),
//       ALTER TABLE 로 뒤에 붙은 열(옛 행에는 없음 → NULL), UTF-8/UTF-16 글자 인코딩.
// 미지원: WITHOUT ROWID 표(색인 B-트리 형식) — 클립 스튜디오 파일에는 없다. 만나면 빈 표로 취급.
using System.Buffers.Binary;
using System.Text;

namespace ComicPadNative.Modules.ClipImport;

public sealed class SqliteTable
{
    public string Name;
    public string[] Columns;
    public readonly List<object[]> Rows = new();     // 값: null · long · double · string · byte[]
    readonly Dictionary<string, int> idx = new(StringComparer.OrdinalIgnoreCase);
    public SqliteTable(string name, string[] cols)
    {
        Name = name; Columns = cols;
        for (int i = 0; i < cols.Length; i++) idx.TryAdd(cols[i], i);
    }
    /// <summary>열 번호 (없으면 -1)</summary>
    public int Col(string name) => idx.TryGetValue(name, out int i) ? i : -1;
    public bool Has(string name) => idx.ContainsKey(name);
}

public sealed class SqliteReader
{
    readonly byte[] b;
    readonly int baseOff, len;
    public readonly int PageSize, Usable, Encoding_;
    public readonly int PageCount;
    readonly Dictionary<string, (int root, string sql)> tables = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SqliteTable> cache = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Warnings = new();

    /// <summary>buf[off .. off+length) 가 SQLite 파일 하나</summary>
    public SqliteReader(byte[] buf, int off, int length)
    {
        b = buf; baseOff = off; len = length;
        if (length < 100 || Encoding.ASCII.GetString(buf, off, 15) != "SQLite format 3") throw new InvalidDataException("SQLite 데이터베이스가 아닙니다");
        int ps = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(off + 16));
        PageSize = ps == 1 ? 65536 : ps;
        if (PageSize < 512 || (PageSize & (PageSize - 1)) != 0) throw new InvalidDataException("SQLite 페이지 크기가 이상합니다: " + PageSize);
        Usable = PageSize - buf[off + 20];
        if (Usable < 480) throw new InvalidDataException("SQLite 예약 영역이 너무 큽니다");
        Encoding_ = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(off + 56));
        if (Encoding_ < 1 || Encoding_ > 3) Encoding_ = 1;
        PageCount = len / PageSize;
        // sqlite_master (1쪽에서 시작)
        foreach (var row in ScanTree(1))
        {
            var v = DecodeRecord(row.payload);
            if (v.Count < 5) continue;
            if (v[0] as string != "table") continue;
            string name = v[1] as string; int root = (int)(v[3] is long l ? l : 0); string sql = v[4] as string;
            if (name != null && root > 0) tables[name] = (root, sql ?? "");
        }
    }

    public IEnumerable<string> TableNames => tables.Keys;
    public bool HasTable(string name) => tables.ContainsKey(name);

    /// <summary>표 전체 (없으면 null). 한 번 읽으면 기억</summary>
    public SqliteTable Table(string name)
    {
        if (cache.TryGetValue(name, out var t)) return t;
        if (!tables.TryGetValue(name, out var info)) return null;
        var (cols, rowidCol, withoutRowid) = ParseColumns(info.sql);
        t = new SqliteTable(name, cols);
        if (withoutRowid) { Warnings.Add($"{name}: WITHOUT ROWID 표는 읽지 않음"); cache[name] = t; return t; }
        foreach (var (rowid, payload) in ScanTree(info.root))
        {
            var v = DecodeRecord(payload);
            var row = new object[cols.Length];
            for (int i = 0; i < cols.Length && i < v.Count; i++) row[i] = v[i];
            if (rowidCol >= 0) row[rowidCol] = rowid;
            t.Rows.Add(row);
        }
        cache[name] = t;
        return t;
    }

    // ---------------------------------------------------------------- B-트리 훑기
    Span<byte> Page(int n)
    {
        if (n < 1 || n > PageCount) throw new InvalidDataException("SQLite 페이지 번호가 범위 밖: " + n);
        return b.AsSpan(baseOff + (n - 1) * PageSize, PageSize);
    }

    /// <summary>표 B-트리의 모든 행 (rowid 순). 깨진 파일에서 무한 반복하지 않도록 방문한 쪽은 다시 안 감</summary>
    IEnumerable<(long rowid, byte[] payload)> ScanTree(int root)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(root);
        var outRows = new List<(long, byte[])>();
        while (stack.Count > 0)
        {
            int pn = stack.Pop();
            if (!seen.Add(pn) || pn < 1 || pn > PageCount) continue;
            var p = Page(pn);
            int h = pn == 1 ? 100 : 0;
            int type = p[h];
            int n = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(h + 3));
            if (type == 13)
            {
                for (int i = 0; i < n; i++)
                {
                    int cp = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(h + 8 + i * 2));
                    if (cp <= 0 || cp >= PageSize) continue;
                    int o = cp;
                    long plen = Varint(p, ref o);
                    long rowid = Varint(p, ref o);
                    outRows.Add((rowid, Payload(p, o, plen)));
                }
            }
            else if (type == 5)
            {
                // 내부 쪽: 왼쪽 자식들 + 맨 오른쪽. 순서를 지키려고 거꾸로 쌓는다
                int right = (int)BinaryPrimitives.ReadUInt32BigEndian(p.Slice(h + 8));
                stack.Push(right);
                for (int i = n - 1; i >= 0; i--)
                {
                    int cp = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(h + 12 + i * 2));
                    if (cp <= 0 || cp + 4 > PageSize) continue;
                    stack.Push((int)BinaryPrimitives.ReadUInt32BigEndian(p.Slice(cp)));
                }
            }
            else if (type is 2 or 10) { Warnings.Add("색인 B-트리는 건너뜀"); }
            else throw new InvalidDataException($"SQLite 페이지 {pn} 의 종류를 모름 ({type})");
        }
        return outRows;
    }

    /// <summary>잎 칸의 내용 (넘침 페이지 이어 붙임). 표 잎 공식: X = U-35</summary>
    byte[] Payload(Span<byte> p, int o, long plen)
    {
        if (plen < 0 || plen > 1L << 31) throw new InvalidDataException("SQLite 칸 크기가 이상합니다");
        int P = (int)plen, U = Usable;
        int X = U - 35;
        int local;
        if (P <= X) local = P;
        else
        {
            int M = (U - 12) * 32 / 255 - 23;
            int K = M + (P - M) % (U - 4);
            local = K <= X ? K : M;
        }
        var res = new byte[P];
        if (o + local > PageSize) throw new InvalidDataException("SQLite 칸이 쪽을 넘습니다");
        p.Slice(o, local).CopyTo(res);
        if (local < P)
        {
            int next = (int)BinaryPrimitives.ReadUInt32BigEndian(p.Slice(o + local));
            int at = local, guard = 0;
            while (at < P && next != 0)
            {
                if (++guard > PageCount + 1) throw new InvalidDataException("SQLite 넘침 페이지가 돌고 돕니다");
                var op = Page(next);
                next = (int)BinaryPrimitives.ReadUInt32BigEndian(op);
                int take = Math.Min(U - 4, P - at);
                op.Slice(4, take).CopyTo(res.AsSpan(at));
                at += take;
            }
            if (at < P) throw new InvalidDataException("SQLite 넘침 페이지가 모자랍니다");
        }
        return res;
    }

    static long Varint(ReadOnlySpan<byte> p, ref int o)
    {
        long v = 0;
        for (int i = 0; i < 8; i++)
        {
            byte c = p[o++];
            v = (v << 7) | (uint)(c & 0x7f);
            if ((c & 0x80) == 0) return v;
        }
        return (v << 8) | p[o++];
    }

    List<object> DecodeRecord(byte[] r)
    {
        var res = new List<object>();
        int o = 0;
        long hs = Varint(r, ref o);
        if (hs > r.Length || hs < 1) throw new InvalidDataException("SQLite 레코드 머리가 이상합니다");
        var types = new List<long>();
        while (o < hs) types.Add(Varint(r, ref o));
        int d = (int)hs;
        foreach (long t in types)
        {
            switch (t)
            {
                case 0: res.Add(null); break;
                case 1: res.Add((long)(sbyte)r[d]); d += 1; break;
                case 2: res.Add((long)BinaryPrimitives.ReadInt16BigEndian(r.AsSpan(d))); d += 2; break;
                case 3: res.Add((long)(((sbyte)r[d] << 16) | (r[d + 1] << 8) | r[d + 2])); d += 3; break;
                case 4: res.Add((long)BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(d))); d += 4; break;
                case 5: { long v = ((long)(short)BinaryPrimitives.ReadInt16BigEndian(r.AsSpan(d)) << 32) | BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(d + 2)); res.Add(v); d += 6; break; }
                case 6: res.Add(BinaryPrimitives.ReadInt64BigEndian(r.AsSpan(d))); d += 8; break;
                case 7: res.Add(BinaryPrimitives.ReadDoubleBigEndian(r.AsSpan(d))); d += 8; break;
                case 8: res.Add(0L); break;
                case 9: res.Add(1L); break;
                case 10: case 11: res.Add(null); break;
                default:
                    {
                        int n = (int)((t - (t % 2 == 0 ? 12 : 13)) / 2);
                        if (d + n > r.Length) throw new InvalidDataException("SQLite 값이 레코드를 넘습니다");
                        if (t % 2 == 0) res.Add(r.AsSpan(d, n).ToArray());
                        else res.Add(Encoding_ switch { 2 => Encoding.Unicode.GetString(r, d, n), 3 => Encoding.BigEndianUnicode.GetString(r, d, n), _ => Encoding.UTF8.GetString(r, d, n) });
                        d += n; break;
                    }
            }
        }
        return res;
    }

    // ---------------------------------------------------------------- CREATE TABLE 풀기
    /// <summary>열 이름들, rowid 별칭 열 번호(INTEGER PRIMARY KEY), WITHOUT ROWID 여부</summary>
    internal static (string[] cols, int rowidCol, bool withoutRowid) ParseColumns(string sql)
    {
        var cols = new List<string>(); int rowid = -1;
        int a = sql.IndexOf('('), z = sql.LastIndexOf(')');
        if (a < 0 || z <= a) return (Array.Empty<string>(), -1, false);
        bool without = sql[(z + 1)..].Replace(" ", "").Contains("WITHOUTROWID", StringComparison.OrdinalIgnoreCase);
        string body = sql[(a + 1)..z];
        var parts = new List<string>(); int depth = 0, start = 0; char q = '\0';
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (q != '\0') { if (c == q) q = '\0'; continue; }
            if (c is '"' or '\'' or '`') q = c;
            else if (c == '[') q = ']';
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { parts.Add(body[start..i]); start = i + 1; }
        }
        parts.Add(body[start..]);
        foreach (var raw in parts)
        {
            string s = raw.Trim();
            if (s.Length == 0) continue;
            string first = s.Split(' ', '\t', '\r', '\n')[0].ToUpperInvariant();
            if (first is "CONSTRAINT" or "PRIMARY" or "UNIQUE" or "CHECK" or "FOREIGN") continue;
            string name; string rest;
            if (s[0] is '"' or '`' or '[')
            {
                char end = s[0] == '[' ? ']' : s[0];
                int e = s.IndexOf(end, 1); if (e < 0) e = s.Length;
                name = s[1..e]; rest = e + 1 < s.Length ? s[(e + 1)..] : "";
            }
            else { int sp = s.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }); name = sp < 0 ? s : s[..sp]; rest = sp < 0 ? "" : s[sp..]; }
            string up = " " + string.Join(' ', rest.ToUpperInvariant().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) + " ";
            if (up.StartsWith(" INTEGER ") && up.Contains(" PRIMARY KEY") && !up.Contains(" DESC")) rowid = cols.Count;
            cols.Add(name);
        }
        return (cols.ToArray(), rowid, without);
    }
}
