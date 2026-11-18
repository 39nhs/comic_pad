// .clip 가져오기 자체 시험 (창 없이): --selftest clip 결과폴더  → 끝 코드 = 실패 수
// 진짜 클립 스튜디오 견본은 재배포가 막혀 있어서, 공개 문서대로 합성 .clip(CSFCHUNK + SQLite + zlib 블록)을 직접 만들어 확인한다.
// COMICPAD_CLIP_SAMPLES=폴더 를 주면 그 안의 진짜 .clip 들도 열어 보고 요약을 report.txt 에 적는다 (커밋하지 않는 개인 견본용).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using ComicPadNative.Core;
using K = ComicPadNative.Modules.ClipImport.ClipWriter.Kind;

namespace ComicPadNative.Modules.ClipImport;

public static class ClipSelfTest
{
    static readonly StringBuilder log = new();
    static int pass, fail;
    static void Ok(bool c, string name) { if (c) pass++; else fail++; log.AppendLine((c ? "PASS " : "FAIL ") + name); }

    // ---------------------------------------------------------------- 합성 파일 만들기
    sealed class Node
    {
        public readonly Dictionary<string, object> P = new();
        public readonly List<Node> Kids = new();       // 아래 → 위
        public long Id;
        public Node(string name, long type = 1) { P["LayerName"] = name; P["LayerType"] = type; P["LayerVisibility"] = 1L; P["LayerOpacity"] = 256L; }
        public Node Set(string k, object v) { P[k] = v; return this; }
    }

    static readonly string[] LayerCols =
    {
        "MainId", "CanvasId", "LayerName", "LayerType", "LayerFolder", "LayerFirstChildIndex", "LayerNextIndex", "LayerVisibility", "LayerOpacity",
        "LayerComposite", "LayerClip", "LayerLock", "LayerOffsetX", "LayerOffsetY", "LayerRenderOffscrOffsetX", "LayerRenderOffscrOffsetY",
        "LayerMaskOffsetX", "LayerMaskOffsetY", "LayerMaskOffscrOffsetX", "LayerMaskOffscrOffsetY", "LayerRenderMipmap", "LayerLayerMaskMipmap",
        "DrawColorEnable", "DrawColorMainRed", "DrawColorMainGreen", "DrawColorMainBlue", "TextLayerString", "TextLayerAttributes",
        "FilterLayerInfo", "GradationFillInfo", "DraftLayer",
    };

    sealed class Synth
    {
        public readonly ClipWriter Cw = new();
        readonly List<object[]> offs = new(), mips = new(), infos = new(), vecs = new();
        long next = 1000;
        public int PageSize = 4096;
        public List<(string t, string l)> Scheme = new() { ("Layer", "Has1_0_19"), ("Layer", "LayerName") };

        public long Offscreen(uint[] argb, int w, int h, K k, bool doInit = false, uint initA = 0, bool store = true)
        {
            var (data, sizes) = ClipWriter.Blocks(ClipWriter.Raw(argb, w, h, k));
            string id = store ? Cw.AddExta(data) : Cw.NewId();
            long mid = next++;
            offs.Add(new object[] { null, mid, 0L, ClipWriter.Attribute(w, h, k, sizes, doInit, initA), Encoding.ASCII.GetBytes(id) });
            return mid;
        }
        public long Mip(long off)
        {
            long half = next++, info = next++, mm = next++;
            infos.Add(new object[] { null, half, 50.0, 0L, 0L });                 // 50% 단계 (오프스크린 없음)
            infos.Add(new object[] { null, info, 100.0, off, half });
            mips.Add(new object[] { null, mm, 2L, info });
            return mm;
        }
        public long AddOff(byte[] attr, string id) { long mid = next++; offs.Add(new object[] { null, mid, 0L, attr, Encoding.ASCII.GetBytes(id) }); return mid; }
        public void Vector(long layerId, byte[] objs) { string id = Cw.AddExta(objs); vecs.Add(new object[] { null, next++, layerId, Encoding.ASCII.GetBytes(id) }); }

        public byte[] Build(int w, int h, double dpi, Node root, long current, int unit = 0, double cw = 0, double ch = 0)
        {
            var rows = new List<object[]>();
            long lid = 1;
            void Assign(Node n) { n.Id = lid++; foreach (var k in n.Kids) Assign(k); }
            Assign(root);
            void Emit(Node n)
            {
                n.P["MainId"] = n.Id; n.P["CanvasId"] = 1L;
                n.P["LayerFirstChildIndex"] = n.Kids.Count > 0 ? n.Kids[0].Id : 0L;
                for (int i = 0; i < n.Kids.Count; i++) n.Kids[i].P["LayerNextIndex"] = i + 1 < n.Kids.Count ? n.Kids[i + 1].Id : 0L;
                rows.Add(new object[] { null }.Concat(LayerCols.Select(c => n.P.GetValueOrDefault(c))).ToArray());
                foreach (var k in n.Kids) Emit(k);
            }
            Emit(root);
            var sw = new SqliteWriter(PageSize);
            sw.AddTable("Canvas", new[] { "_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT", "MainId INTEGER", "CanvasUnit INTEGER", "CanvasWidth REAL", "CanvasHeight REAL", "CanvasResolution REAL", "CanvasRootFolder INTEGER", "CanvasCurrentLayer INTEGER" },
                new[] { new object[] { null, 1L, (long)unit, unit == 0 ? (double)w : cw, unit == 0 ? (double)h : ch, dpi, root.Id, current } });
            sw.AddTable("Layer", new[] { "_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT" }.Concat(LayerCols.Select(c => $"\"{c}\" " + (c.StartsWith("Layer") && c.EndsWith("Name") || c == "TextLayerString" ? "TEXT" : c.EndsWith("Info") || c.EndsWith("Attributes") ? "BLOB" : "INTEGER"))).ToArray(), rows);
            sw.AddTable("Offscreen", new[] { "_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT", "MainId INTEGER", "LayerId INTEGER", "Attribute BLOB", "BlockData BLOB" }, offs);
            sw.AddTable("Mipmap", new[] { "_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT", "MainId INTEGER", "MipmapCount INTEGER", "BaseMipmapInfo INTEGER" }, mips);
            sw.AddTable("MipmapInfo", new[] { "_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT", "MainId INTEGER", "ThisScale REAL", "Offscreen INTEGER", "NextIndex INTEGER" }, infos);
            sw.AddTable("VectorObjectList", new[] { "_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT", "MainId INTEGER", "LayerId INTEGER", "VectorData BLOB" }, vecs);
            sw.AddTable("ParamScheme", new[] { "_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT", "TableName TEXT", "LabelName TEXT" }, Scheme.Select(s => new object[] { null, s.t, s.l }));
            return Cw.Build(sw.Build());
        }
    }

    static uint[] Img(int w, int h, Func<int, int, uint> f) { var a = new uint[w * h]; for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) a[y * w + x] = f(x, y); return a; }
    static uint C(uint a, uint r, uint g, uint b) => (a << 24) | (r << 16) | (g << 8) | b;
    static long Hi(uint v) => (long)(v * 0x01010101u);       // 8비트 색 → CSP 32비트 값 (위 8비트)

    static byte[] TextAttrs(uint sizeX100, int x0, int y0, int x1, int y1, string font, long imageOff)
    {
        var ms = new MemoryStream(); var b4 = new byte[4];
        void Rec(int id, byte[] body) { BinaryPrimitives.WriteInt32LittleEndian(b4, id); ms.Write(b4); BinaryPrimitives.WriteInt32LittleEndian(b4, body.Length); ms.Write(b4); ms.Write(body); }
        byte[] I32s(params int[] v) { var b = new byte[v.Length * 4]; for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), v[i]); return b; }
        Rec(32, I32s((int)sizeX100));
        Rec(42, I32s(x0, y0, x1, y1));
        Rec(45, I32s(0));
        var fn = Encoding.UTF8.GetBytes(font); var f = new byte[4 + fn.Length + 2]; BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(2), (ushort)fn.Length); fn.CopyTo(f, 4);
        Rec(57, f);
        Rec(50, I32s((int)imageOff));
        return ms.ToArray();
    }

    static byte[] VectorObjs(params (double x, double y, float pr)[][] strokes)
    {
        var ms = new MemoryStream();
        void U(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }
        void I(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        void D(double v) { var b = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); ms.Write(b); }
        void F(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, v); ms.Write(b); }
        foreach (var s in strokes)
        {
            U(88); U(72); U(88); U(88); U((uint)s.Length); U(0x11);
            I(0); I(0); I(0); I(0);
            U(0x20FFFFFF); U(0x40FFFFFF); U(0xE0FFFFFF);          // 주 색 (32, 64, 224)
            U(0xFFFFFFFF); U(0xFFFFFFFF); U(0xFFFFFFFF);
            D(1.0); U(1); D(4.0); U(0);
            foreach (var (x, y, pr) in s)
            {
                D(x); D(y); I(0); I(0); I(0); I(0); U(0);
                F(pr); for (int i = 1; i < 13; i++) F(0);
            }
        }
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- 시험
    public static int Run(string dir)
    {
        log.Clear(); pass = fail = 0;
        Directory.CreateDirectory(dir);
        try { TestSqlite(dir); } catch (Exception ex) { Ok(false, "SQLite 읽기 시험 예외: " + ex); }
        try { TestMain(dir); } catch (Exception ex) { Ok(false, "합성 파일 시험 예외: " + ex); }
        try { TestRobust(dir); } catch (Exception ex) { Ok(false, "깨진 파일 시험 예외: " + ex); }
        try { TestBig(dir); } catch (Exception ex) { Ok(false, "큰 파일 시험 예외: " + ex); }
        try { TestSamples(dir); } catch (Exception ex) { Ok(false, "견본 시험 예외: " + ex); }
        log.Insert(0, $".clip 가져오기 자체 시험: 통과 {pass}, 실패 {fail}\r\n");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        Console.WriteLine(log.ToString());
        return fail;
    }

    /// <summary>SQLite 읽기: 1KB 쪽·여러 단계 내부 쪽·넘침 쪽·여러 값 형식</summary>
    static void TestSqlite(string dir)
    {
        foreach (int psz in new[] { 1024, 4096, 65536 })
        {
            var rows = new List<object[]>();
            var rnd = new Random(7);
            for (int i = 0; i < 3000; i++)
            {
                byte[] blob = i % 97 == 0 ? Enumerable.Range(0, 5000 + i).Select(k => (byte)(k * 31 + i)).ToArray() : new byte[] { (byte)i, 1, 2 };
                rows.Add(new object[] { (long)(i * 3 + 1), (long)i * (i % 2 == 0 ? 1 : -1) * 1000003L, i * 0.5, "이름 名前 " + i, blob, i % 5 == 0 ? null : (object)(long)(i % 300) });
            }
            var sw = new SqliteWriter(psz);
            sw.AddTable("T", new[] { "Id INTEGER PRIMARY KEY", "Big INTEGER", "R REAL", "S TEXT", "B BLOB", "N INTEGER" }, rows);
            sw.AddTable("Empty", new[] { "A", "B" }, new List<object[]>());
            var bytes = sw.Build();
            if (psz == 1024) File.WriteAllBytes(Path.Combine(dir, "sqlite_1k.db"), bytes);
            var db = new SqliteReader(bytes, 0, bytes.Length);
            var t = db.Table("T");
            bool same = t.Rows.Count == rows.Count;
            for (int i = 0; same && i < rows.Count; i++)
            {
                var a = rows[i]; var b = t.Rows[i];
                same = (long)a[0] == (long)b[0] && (long)a[1] == (long)b[1] && (double)a[2] == (double)b[2] && (string)a[3] == (string)b[3]
                       && ((byte[])a[4]).SequenceEqual((byte[])b[4]) && Equals(a[5], b[5]);
            }
            Ok(same, $"SQLite {psz}B 쪽: 3000행(넘침·내부 쪽) 그대로 읽힘");
            Ok(db.Table("Empty")?.Rows.Count == 0 && db.Table("없는표") == null, $"SQLite {psz}B 쪽: 빈 표·없는 표");
        }
        var (cols, rid, wr) = SqliteReader.ParseColumns("CREATE TABLE \"X\" (_PW_ID INTEGER PRIMARY KEY AUTOINCREMENT, \"Name\" TEXT DEFAULT 'a,b', [Sq] BLOB, CONSTRAINT c UNIQUE (Name), Val NUMERIC(10,2))");
        Ok(cols.SequenceEqual(new[] { "_PW_ID", "Name", "Sq", "Val" }) && rid == 0 && !wr, "CREATE TABLE 열 풀기 (따옴표·기본값 쉼표·제약)");
    }

    static string Make(string dir, string name, out Dictionary<string, long> ids, Action<Synth> tweak = null)
    {
        ids = new();
        const int W = 600, H = 400;
        var s = new Synth();
        tweak?.Invoke(s);
        var root = new Node("root", 256);
        var paper = new Node("용지", 1584).Set("DrawColorEnable", 1L).Set("DrawColorMainRed", Hi(0xf0)).Set("DrawColorMainGreen", Hi(0xe8)).Set("DrawColorMainBlue", Hi(0xd8));
        root.Kids.Add(paper);
        // 1. 배경: 꽉 찬 색
        var bg = new Node("배경 背景").Set("LayerRenderMipmap", s.Mip(s.Offscreen(Img(W, H, (x, y) => C(255, 200, 100, 50)), W, H, K.Color)));
        root.Kids.Add(bg);
        // 2. 반투명: 알파 = x%256, 곱하기, 불투명도 50%, 잠금
        var semi = new Node("반투명 半透明").Set("LayerOpacity", 128L).Set("LayerComposite", 2L).Set("LayerLock", 1L)
            .Set("LayerRenderMipmap", s.Mip(s.Offscreen(Img(W, H, (x, y) => C((uint)(x % 256), 10, 200, 30)), W, H, K.Color)));
        root.Kids.Add(semi);
        // 3. 폴더(통과, 불투명도 200/256, 펼침) 안: 회색 레이어(작은 면, 어긋난 위치), 클리핑(숨김·스크린), 속 폴더(오버레이) 안 모노
        var folder = new Node("폴더 フォルダ", 0).Set("LayerFolder", 17L).Set("LayerComposite", 30L).Set("LayerOpacity", 200L);
        var gray = new Node("회색 グレー").Set("LayerOffsetX", 300L).Set("LayerRenderOffscrOffsetX", -263L).Set("LayerOffsetY", 0L).Set("LayerRenderOffscrOffsetY", 21L)
            .Set("LayerRenderMipmap", s.Mip(s.Offscreen(Img(300, 280, (x, y) => C(255, 77, 77, 77)), 300, 280, K.Gray)));
        var clip = new Node("클리핑 クリップ").Set("LayerClip", 1L).Set("LayerVisibility", 0L).Set("LayerComposite", 8L).Set("DraftLayer", 1L)
            .Set("LayerRenderMipmap", s.Mip(s.Offscreen(Img(W, H, (x, y) => x < 100 ? C(255, 0, 0, 255) : 0), W, H, K.Color)));
        var inner = new Node("속 폴더", 0).Set("LayerFolder", 1L).Set("LayerComposite", 14L);
        var mono = new Node("모노 モノクロ").Set("LayerRenderMipmap", s.Mip(s.Offscreen(Img(W, H, (x, y) => y < 50 ? ((x / 10) % 2 == 0 ? 0xFF000000 : 0xFFFFFFFF) : 0), W, H, K.Mono)));
        inner.Kids.Add(mono);
        folder.Kids.Add(gray); folder.Kids.Add(clip); folder.Kids.Add(inner);
        root.Kids.Add(folder);
        // 4. 마스크 레이어: 왼쪽 절반만 보이는 마스크 (오른쪽 블록은 저장 안 함 = 가림)
        var masked = new Node("마스크 マスク", 3).Set("LayerVisibility", 3L)
            .Set("LayerRenderMipmap", s.Mip(s.Offscreen(Img(W, H, (x, y) => C(255, 0, 128, 0)), W, H, K.Color)))
            .Set("LayerLayerMaskMipmap", s.Mip(s.Offscreen(Img(W, H, (x, y) => x < 256 ? 0xFF000000u : x < 300 ? 0x80000000u : 0), W, H, K.Alpha)));
        root.Kids.Add(masked);
        // 5. 텍스트: 그림 없음, 글자 그림(기록 50) 따로
        long textImg = s.Offscreen(Img(41, 21, (x, y) => C(255, 0, 0, 0)), 41, 21, K.Color);
        var text = new Node("글자", 1).Set("TextLayerString", "안녕 こんにちは").Set("LayerOffsetX", 5L).Set("LayerOffsetY", 7L)
            .Set("TextLayerAttributes", TextAttrs(1200, 100, 200, 140, 220, "Meiryo", textImg))
            .Set("LayerRenderMipmap", s.Mip(s.Offscreen(new uint[W * H], W, H, K.Color, store: false)));
        root.Kids.Add(text);
        // 6. 벡터: 픽셀 없음 → 선 데이터로 근사
        var vec = new Node("벡터 ベクター", 1).Set("LayerRenderMipmap", s.Mip(s.Offscreen(new uint[W * H], W, H, K.Color, store: false)));
        root.Kids.Add(vec);
        // 7. 색조 보정
        var adj = new Node("밝기 보정", 4096 + 1).Set("FilterLayerInfo", new byte[] { 0, 0, 0, 1, 0, 0, 0, 8, 1, 2, 3, 4, 5, 6, 7, 8 });
        root.Kids.Add(adj);
        // 8. 모르는 합성 번호 99, 9. 선명한 라이트(근사)
        root.Kids.Add(new Node("이상한 합성").Set("LayerComposite", 99L));
        root.Kids.Add(new Node("선명한 라이트").Set("LayerComposite", 17L));
        // 10. 작품 정보 (건너뜀)
        root.Kids.Add(new Node("작품 정보", 800).Set("TextLayerString", "제목"));
        var bytes = s.Build(W, H, 600, root, semi.Id + 0);
        // Build 가 Id 를 정한 뒤 벡터 등록 → 다시 만들기 (Id 는 같은 순서로 같게 나옴)
        s.Vector(vec.Id, VectorObjs(new[] { (50.0, 300.0, 1f), (150.0, 300.0, 1f), (250.0, 300.0, 1f) }));
        bytes = s.Build(W, H, 600, root, semi.Id);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        foreach (var (k, n) in new[] { ("semi", semi), ("vec", vec) }) ids[k] = n.Id;
        return path;
    }

    static void TestMain(string dir)
    {
        string path = Make(dir, "synthetic.clip", out _);
        var r = ClipImporter.Import(path);
        var d = r.Doc;
        File.WriteAllText(Path.Combine(dir, "synthetic_summary.txt"), ClipImportModule.SummaryText(r));
        Ok(d.W == 600 && d.H == 400 && d.Dpi == 600, $"캔버스 600×400, 600dpi ({d.W}×{d.H}, {d.Dpi})");
        Ok(d.PaperColor == "#f0e8d8" && d.PaperVisible, "용지 색 → 문서 용지 색 " + d.PaperColor);
        Ok(r.Version == "5.1 이후", "세대 추정: " + r.Version);
        string[] want = { "배경 背景", "반투명 半透明", "회색 グレー", "클리핑 クリップ", "모노 モノクロ", "속 폴더", "폴더 フォルダ", "마스크 マスク", "글자", "벡터 ベクター", "밝기 보정 [색조 보정]", "이상한 합성", "선명한 라이트" };
        var names = d.Layers.Select(l => l.Name).ToArray();
        Ok(names.SequenceEqual(want), "레이어 순서·이름(한글·일본어): " + string.Join(" | ", names));
        Layer L(string n) => d.Layers.First(l => l.Name == n);
        var folder = L("폴더 フォルダ"); var inner = L("속 폴더");
        Ok(folder.IsFolder && folder.Expanded && Math.Abs(folder.Opacity - 200 / 256f) < 1e-4 && folder.Blend == "source-over" && folder.Extra["clipStudio"]?["passThrough"]?.GetValue<bool>() == true, "폴더: 불투명도·펼침·통과→일반");
        Ok(L("회색 グレー").Parent == folder.Id && L("클리핑 クリップ").Parent == folder.Id && inner.Parent == folder.Id && L("모노 モノクロ").Parent == inner.Id && inner.IsFolder && !inner.Expanded, "폴더 계층 (2단)");
        Ok(inner.Blend == "overlay", "속 폴더 오버레이");
        Ok(L("배경 背景").Px.Get(599, 399) == Px.Pack(255, 200, 100, 50) && L("배경 背景").Px.Get(0, 0) == Px.Pack(255, 200, 100, 50), "꽉 찬 색 픽셀 (가장자리 블록 포함)");
        var semi = L("반투명 半透明");
        uint a = 130;
        Ok(semi.Px.Get(130, 10) == Px.Pack(a, Px.Mul(10, a), Px.Mul(200, a), Px.Mul(30, a)) && semi.Px.Get(256, 5) == 0 && semi.Px.Get(511, 5) >> 24 == 255, "반투명 픽셀: 알파 곱하기(premultiplied)");
        Ok(Math.Abs(semi.Opacity - 0.5f) < 1e-4 && semi.Blend == "multiply" && semi.Locked && !semi.AlphaLock, "불투명도 50%·곱하기·잠금");
        Ok(d.ActiveId == semi.Id, "선택 레이어 = CanvasCurrentLayer");
        var g = L("회색 グレー");
        Ok(g.Px.Get(37, 21) == Px.Pack(255, 77, 77, 77) && g.Px.Get(36, 21) == 0 && g.Px.Get(37, 20) == 0 && g.Px.Get(336, 300) == Px.Pack(255, 77, 77, 77) && g.Px.Get(337, 301) == 0, "회색 레이어: 위치 어긋남(37,21) 반영");
        var cl = L("클리핑 クリップ");
        Ok(cl.Clip && !cl.Visible && cl.Blend == "screen" && cl.Draft && cl.Px.Get(50, 50) == 0xFF0000FF, "클리핑·숨김·스크린·밑그림");
        var mo = L("모노 モノクロ");
        Ok(mo.Px.Get(5, 5) == 0xFF000000 && mo.Px.Get(15, 5) == 0xFFFFFFFF && mo.Px.Get(5, 60) == 0, "모노크롬 1비트 픽셀");
        var mk = L("마스크 マスク");
        Ok(mk.Mask != null && mk.MaskOn && mk.Mask.Get(10, 10) >> 24 == 255 && mk.Mask.Get(280, 10) >> 24 == 128 && mk.Mask.Get(400, 10) >> 24 == 0 && mk.Mask.Get(590, 390) >> 24 == 0, "레이어 마스크 (빈 블록 = 가림)");
        var t = L("글자");
        var pt = t.Meta?["psdText"];
        Ok(pt?["text"]?.ToString() == "안녕 こんにちは" && pt?["font"]?.ToString() == "Meiryo" && Math.Abs((double)pt["size"] - 100) < 0.01, "텍스트: 내용·글꼴·크기(12pt@600dpi=100px)");
        Ok(t.Px.Get(105, 207) >> 24 == 255 && t.Px.Get(104, 207) == 0 && t.Px.Get(145, 227) >> 24 == 255, "텍스트: CSP 글자 그림을 외접 사각형+레이어 위치에");
        var v = L("벡터 ベクター");
        uint vp = v.Px.Get(150, 300);
        Ok(vp >> 24 == 255 && (vp & 0xFF) == 224 && v.Px.Get(150, 310) == 0 && v.Px.Get(40, 300) == 0, $"벡터 선 근사 그리기 (가운데 {vp:X8})");
        Ok(r.Missing.Any(m => m.Contains("벡터") && m.Contains("근사")) && r.Missing.Any(m => m.Contains("색조 보정")) && r.Missing.Any(m => m.Contains("작품 정보")) && r.Missing.Any(m => m.Contains("텍스트")), "가져오지 못한 것 목록 (벡터·보정·작품 정보·텍스트)");
        Ok(L("이상한 합성").Blend == "source-over" && r.Notes.Any(n => n.Contains("99")), "모르는 합성 번호 → 표준 + 알림");
        Ok(L("선명한 라이트").Blend == "hard-light" && r.Notes.Any(n => n.Contains("선명한 라이트")), "근사 합성 모드 + 알림");
        Ok(r.LayerCount == 11 && r.FolderCount == 2, $"레이어 {r.LayerCount}, 폴더 {r.FolderCount}");
        // 합성·저장 되는지
        var flat = App.Comp.Flatten(d);
        using (var bmp = CpadIO.ToBitmap(flat, flat.Full)) bmp.Save(Path.Combine(dir, "synthetic_flat.png"));
        Ok(flat.Get(300, 200) >> 24 == 255, "합친 그림 만들기");
        string cp = Path.Combine(dir, "synthetic.cpad");
        CpadIO.Save(d, cp);
        var d2 = CpadIO.Load(cp);
        Ok(d2.Layers.Count == d.Layers.Count && d2.Layers.Select(l => l.Name).SequenceEqual(names) && d2.ById(d2.Layers.First(l => l.Name == "반투명 半透明").Id).Px.Get(130, 10) == semi.Px.Get(130, 10), ".cpad 로 저장 후 다시 열기");
        // 여는 길 등록
        Ok(UI.FileCommands.Openers.TryGetValue(".CLIP", out var op) && op == ClipImportModule.Open, ".clip 열기 등록");
        Ok(Registry.Menus.Any(m => m.Path == "파일/클립 스튜디오 파일 가져오기 (.clip)…"), "파일 메뉴 항목");
        var viaOpen = ClipImportModule.Open(path);
        Ok(viaOpen.Layers.Count == d.Layers.Count, "열기 담당(창 없이) 으로 열기");

        // 단위가 mm 인 캔버스 (A4 = 210×297mm, 72dpi → 595×842, 픽셀 면 크기를 믿음)
        string p2 = Path.Combine(dir, "unit_mm.clip");
        {
            var s = new Synth();
            var root = new Node("root", 256);
            root.Kids.Add(new Node("한 장").Set("LayerRenderMipmap", s.Mip(s.Offscreen(Img(596, 842, (x, y) => C(255, 1, 2, 3)), 596, 842, K.Color))));
            File.WriteAllBytes(p2, s.Build(0, 0, 72, root, 0, unit: 2, cw: 210, ch: 297));
        }
        var r2 = ClipImporter.Import(p2);
        Ok(r2.Doc.W == 596 && r2.Doc.H == 842 && r2.Doc.Dpi == 72, $"mm 단위 캔버스 + 픽셀 면 크기 맞춤 ({r2.Doc.W}×{r2.Doc.H})");

        // 새 버전 표시
        string p3 = Path.Combine(dir, "newer.clip");
        {
            var s = new Synth(); s.Scheme.Add(("Layer", "Has1_0_25"));
            var root = new Node("root", 256);
            root.Kids.Add(new Node("레이어").Set("LayerRenderMipmap", s.Mip(s.Offscreen(Img(64, 64, (x, y) => C(255, 9, 9, 9)), 64, 64, K.Color))));
            File.WriteAllBytes(p3, s.Build(64, 64, 350, root, 0));
        }
        var r3 = ClipImporter.Import(p3);
        Ok(r3.Notes.Any(n => n.Contains("새 클립 스튜디오")) && r3.Doc.Layers[0].Px.Get(1, 1) == Px.Pack(255, 9, 9, 9), "더 새 버전 파일: 알림 + 가져오기");
    }

    static void TestRobust(string dir)
    {
        string good = Make(dir, "robust_base.clip", out _);
        var bytes = File.ReadAllBytes(good);
        bool Safe(byte[] b, string name, out string msg)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, b);
            msg = "";
            try { var r = ClipImportModule.ImportSafe(p, null, default); msg = r == null ? "취소" : "성공"; return true; }
            catch (InvalidDataException ex) { msg = ex.Message; return true; }
            catch (IOException ex) { msg = ex.Message; return true; }
            catch (Exception ex) { msg = ex.GetType().Name + ": " + ex.Message; return false; }
        }
        Ok(Safe(Encoding.ASCII.GetBytes("this is not a clip file at all........................"), "notclip.clip", out var m1) && m1.Contains("CSFCHUNK"), "다른 파일: 알기 쉬운 오류 — " + m1);
        Ok(Safe(bytes.Take(bytes.Length / 2).ToArray(), "half.clip", out var m2), "반쯤 잘린 파일: 죽지 않음 — " + m2);
        Ok(Safe(bytes.Take(200).ToArray(), "tiny.clip", out var m3), "앞부분만: 죽지 않음 — " + m3);
        // Layer 표 없는 DB
        {
            var sw = new SqliteWriter();
            sw.AddTable("Canvas", new[] { "MainId INTEGER" }, new[] { new object[] { 1L } });
            var cw = new ClipWriter();
            Ok(Safe(cw.Build(sw.Build()), "nolayer.clip", out var m4) && m4.Contains("레이어 정보"), "레이어 표 없음: " + m4);
        }
        // 무작위 바이트 바꾸기 300번 → 예상한 오류 아니면 실패
        var rnd = new Random(1234);
        int crashes = 0; string firstCrash = null;
        for (int i = 0; i < 300; i++)
        {
            var b = (byte[])bytes.Clone();
            int n = 1 + rnd.Next(8);
            for (int k = 0; k < n; k++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
            if (!Safe(b, "fuzz.clip", out var msg)) { crashes++; firstCrash ??= msg; }
        }
        Ok(crashes == 0, $"무작위로 깨뜨린 파일 300개: 예상 못한 오류 {crashes}개 {firstCrash}");
        File.Delete(Path.Combine(dir, "fuzz.clip"));
    }

    /// <summary>A4 600dpi(4961×7016), 레이어 52개 — 진행률·취소·시간</summary>
    static void TestBig(string dir)
    {
        const int W = 4961, H = 7016;
        var s = new Synth();
        var root = new Node("root", 256);
        int bx = (W + 255) / 256, by = (H + 255) / 256;
        var rnd = new Random(5);
        var sw0 = Stopwatch.StartNew();
        // 블록 단위로 직접 (전체 그림 배열을 만들면 시험이 너무 느림)
        byte[] Block(byte a, byte r, byte g, byte b, int seed)
        {
            var raw = new byte[327680];
            for (int i = 0; i < 65536; i++) { raw[i] = (byte)(((i + seed) & 255) < 200 ? a : 0); raw[65536 + i * 4] = b; raw[65536 + i * 4 + 1] = g; raw[65536 + i * 4 + 2] = (byte)(r ^ (i >> 8)); }
            return raw;
        }
        var shared = Block(255, 30, 60, 90, 0);
        for (int li = 0; li < 52; li++)
        {
            var raws = new byte[bx * by][];
            bool full = li % 20 == 0;                     // 3장은 꽉 찬 레이어, 나머지는 블록 20개쯤
            var own = full ? Block(255, (byte)li, 60, 90, li) : shared;
            for (int i = 0; i < raws.Length; i++) if (full || rnd.Next(raws.Length) < 20) raws[i] = own;
            var (data, sizes) = ClipWriter.Blocks(raws);
            string id = s.Cw.AddExta(data);
            long off = s.AddRawOffscreen(W, H, sizes, id);
            var n = new Node("레이어 " + li).Set("LayerRenderMipmap", s.Mip(off));
            if (li % 17 == 3) n.Set("LayerLayerMaskMipmap", s.Mip(s.AddRawOffscreen(W, H, Enumerable.Repeat(104, bx * by).ToArray(), s.Cw.AddExta(ClipWriter.Blocks(new byte[bx * by][]).data), K.Alpha, true, 0xFFFFFFFF)));
            if (li % 5 == 0) { var f = new Node("폴더 " + li, 0).Set("LayerFolder", 1L); f.Kids.Add(n); root.Kids.Add(f); }
            else root.Kids.Add(n);
        }
        string path = Path.Combine(dir, "big_a4_600dpi.clip");
        File.WriteAllBytes(path, s.Build(W, H, 600, root, 0));
        log.AppendLine($"     큰 파일 만들기 {sw0.Elapsed.TotalSeconds:0.0}초, {new FileInfo(path).Length / 1e6:0.0}MB");
        // 진행률 + 시간
        var reports = new List<double>();
        var prog = new SyncProgress(v => { lock (reports) reports.Add(v); });
        var sw = Stopwatch.StartNew();
        var r = ClipImportModule.ImportSafe(path, prog, default);
        double sec = sw.Elapsed.TotalSeconds;
        long mem = r.Doc.Layers.Sum(l => (l.Px?.MemoryBytes ?? 0) + (l.Mask?.MemoryBytes ?? 0));
        log.AppendLine($"     큰 파일 가져오기 {sec:0.0}초, 레이어 {r.LayerCount}개, 폴더 {r.FolderCount}개, 픽셀 메모리 {mem / 1e6:0}MB");
        Ok(r.Doc.W == W && r.Doc.H == H && r.LayerCount == 52 && r.FolderCount == 11, "A4 600dpi 52레이어 가져오기");
        Ok(sec < 60, $"가져오기 시간 {sec:0.0}초 (< 60초)");
        Ok(reports.Count > 10 && reports.Max() >= 0.999 && reports.Zip(reports.Skip(1)).All(z => z.Second >= z.First - 1e-9), $"진행률 {reports.Count}번, 늘기만 함");
        var l0 = r.Doc.Layers.First(l => l.Name == "레이어 0");
        Ok(l0.Px.Get(W - 1, H - 1) >> 24 == 255 || l0.Px.Get(W - 1, H - 1) == 0, "가장자리 블록 잘라 넣기");
        Ok(r.Doc.Layers.First(l => l.Name == "레이어 3").Mask?.Get(100, 100) >> 24 == 255, "초기색 마스크 (빈 블록 = 보임)");
        r = null; GC.Collect();
        // 취소
        var cts = new CancellationTokenSource();
        var sw2 = Stopwatch.StartNew();
        var cprog = new SyncProgress(v => { if (v > 0.2) cts.Cancel(); });
        var rc = ClipImportModule.ImportSafe(path, cprog, cts.Token);
        Ok(rc == null && sw2.Elapsed.TotalSeconds < sec + 1, $"가져오는 도중 취소 → null ({sw2.Elapsed.TotalSeconds:0.0}초)");
        r = null; rc = null; GC.Collect();
        File.Delete(path);
    }

    sealed class SyncProgress : IProgress<double> { readonly Action<double> a; public SyncProgress(Action<double> a) { this.a = a; } public void Report(double v) => a(v); }

    static long AddRawOffscreen(this Synth s, int w, int h, int[] sizes, string id, K k = K.Color, bool doInit = false, uint initA = 0) => s.AddOff(ClipWriter.Attribute(w, h, k, sizes, doInit, initA), id);

    /// <summary>COMICPAD_CLIP_SAMPLES 의 진짜 .clip 들 (있으면)</summary>
    static void TestSamples(string outDir)
    {
        string sd = Environment.GetEnvironmentVariable("COMICPAD_CLIP_SAMPLES");
        if (string.IsNullOrEmpty(sd) || !Directory.Exists(sd)) { log.AppendLine("     (진짜 견본 없음: COMICPAD_CLIP_SAMPLES 미지정)"); return; }
        foreach (var f in Directory.GetFiles(sd, "*.clip"))
        {
            try
            {
                var r = ClipImportModule.ImportSafe(f, null, default);
                Ok(r != null, $"견본 {Path.GetFileName(f)}: {r?.Doc.W}×{r?.Doc.H}, 레이어 {r?.LayerCount}, 빠진 것 {r?.Missing.Count}, 참고 {r?.Notes.Count}");
                log.AppendLine(ClipImportModule.SummaryText(r));
                log.AppendLine("     레이어: " + string.Join(" | ", r.Doc.Layers.Select(l => $"{l.Name}({l.Kind},{l.Blend},{l.Opacity:0.##}{(l.Clip ? ",clip" : "")}{(l.Visible ? "" : ",숨김")}{(l.Mask != null ? ",mask" : "")})")));
                var flat = App.Comp.Flatten(r.Doc);
                using var bmp = CpadIO.ToBitmap(flat, flat.Full);
                bmp.Save(Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "_flat.png"));
            }
            catch (InvalidDataException ex) { Ok(false, $"견본 {Path.GetFileName(f)}: {ex.Message}"); }
        }
    }
}
