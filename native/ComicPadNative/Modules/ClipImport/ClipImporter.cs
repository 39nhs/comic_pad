// 클립 스튜디오 파일(.clip) → ComicPad 문서.
// 형식 출처는 ClipContainer.cs 머리말 (nanikasheila/csp-format docs/spec, dobrokot/clip_to_psd — 둘 다 MIT, 설명만 보고 직접 구현).
// 데이터베이스 표:
//   Canvas  : CanvasWidth/Height(CanvasUnit 단위), CanvasResolution(dpi), CanvasRootFolder, CanvasCurrentLayer
//   Layer   : MainId, LayerName, LayerType(256 뿌리·1584 용지·800 작품 정보, 비트0 그림 있음, 비트1 마스크 있음, 4096 색조 보정),
//             LayerFolder(비트0 폴더, 16 펼침), LayerFirstChildIndex/LayerNextIndex(형제는 아래→위),
//             LayerVisibility(비트0 보임, 비트1 마스크 사용), LayerOpacity(0~256), LayerComposite(합성 번호), LayerClip, LayerLock,
//             LayerOffsetX/Y + LayerRenderOffscrOffsetX/Y(그림 왼쪽 위), LayerMaskOffsetX/Y + LayerMaskOffscrOffsetX/Y(마스크),
//             LayerRenderMipmap / LayerLayerMaskMipmap → Mipmap.BaseMipmapInfo → MipmapInfo(ThisScale, Offscreen, NextIndex) → Offscreen
//             DrawColorEnable/DrawColorMain*(레이어 컬러·용지 색, 32비트 값의 위 8비트)
//   Offscreen: Attribute(크기·형식), BlockData(외부 ID)
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.ClipImport;

public sealed class ClipImportResult
{
    public Document Doc;
    public readonly List<string> Missing = new();     // 가져오지 못한 것 (레이어별)
    public readonly List<string> Notes = new();       // 근사·주의
    public string Version = "";                        // 저장한 클립 스튜디오 세대 (추정)
    public int LayerCount, FolderCount;
    public TimeSpan Elapsed;
}

public static class ClipImporter
{
    // ---- 합성 번호 → ComicPad 이름 (정확히 같은 것 / 비슷한 것으로 대신) ----------------------------------------
    static readonly Dictionary<long, string> Exact = new()
    {
        [0] = "source-over", [1] = "darken", [2] = "multiply", [3] = "color-burn", [7] = "lighten", [8] = "screen", [9] = "color-dodge",
        [11] = "lighter", [14] = "overlay", [15] = "soft-light", [16] = "hard-light", [21] = "difference",
        [23] = "hue", [24] = "saturation", [25] = "color", [26] = "luminosity",
    };
    static readonly Dictionary<long, (string mode, string csp)> Approx = new()
    {
        [4] = ("color-burn", "선형 번"), [5] = ("difference", "감산"), [6] = ("darken", "어두운 색상"), [10] = ("color-dodge", "닷지(발광)"),
        [12] = ("lighter", "더하기(발광)"), [13] = ("lighten", "밝은 색상"), [17] = ("hard-light", "선명한 라이트"), [18] = ("hard-light", "선형 라이트"),
        [19] = ("hard-light", "핀 라이트"), [20] = ("hard-light", "하드 혼합"), [22] = ("difference", "제외"), [36] = ("color-dodge", "나누기"),
    };
    public const long PassThrough = 30;

    /// <summary>합성 번호 → (ComicPad 이름, 근사 설명 또는 null)</summary>
    public static (string mode, string note) MapBlend(long n)
    {
        if (Exact.TryGetValue(n, out var m)) return (m, null);
        if (Approx.TryGetValue(n, out var a)) return (a.mode, $"합성 모드 \"{a.csp}\" → 가장 비슷한 \"{Px.Modes.FirstOrDefault(x => x.id == a.mode).label ?? a.mode}\"(으)로");
        if (n == PassThrough) return ("source-over", null);
        return ("source-over", $"모르는 합성 모드 번호 {n} → 표준으로");
    }

    // ---- 값 읽기 도우미 ----
    sealed class T
    {
        public readonly SqliteTable Tab;
        public readonly Dictionary<long, object[]> ById = new();
        public T(SqliteTable t)
        {
            Tab = t;
            int c = t?.Col("MainId") ?? -1;
            if (t != null && c >= 0) foreach (var r in t.Rows) if (r[c] is long id) ById[id] = r;
        }
        public object V(object[] r, string col) { int c = Tab?.Col(col) ?? -1; return r == null || c < 0 ? null : r[c]; }
        public long L(object[] r, string col, long def = 0) => V(r, col) switch { long l => l, double d => (long)d, string s when long.TryParse(s, out var x) => x, _ => def };
        public double D(object[] r, string col, double def = 0) => V(r, col) switch { long l => l, double d => d, string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) => x, _ => def };
        public string S(object[] r, string col) => V(r, col) switch { string s => s, byte[] b => Encoding.UTF8.GetString(b).TrimEnd('\0'), long l => l.ToString(), _ => null };
        public byte[] B(object[] r, string col) => V(r, col) switch { byte[] b => b, string s => Encoding.UTF8.GetBytes(s), _ => null };
        public bool Present(object[] r, string col) => V(r, col) switch { null => false, long l => l != 0, byte[] b => b.Length > 0, string s => s.Length > 0, _ => true };
        public object[] Get(long id) => id != 0 && ById.TryGetValue(id, out var r) ? r : null;
    }

    static uint Col8(long v) => (uint)((ulong)v >> 24) & 255;     // 32비트 색 값의 위 8비트

    /// <summary>파일 → 문서. prog 0..1, 취소하면 OperationCanceledException</summary>
    public static ClipImportResult Import(string path, IProgress<double> prog = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new ClipImportResult();
        void Note(string s) { if (!res.Notes.Contains(s)) res.Notes.Add(s); }
        using var c = new ClipContainer(path);
        foreach (var w in c.Warnings) Note(w);
        SqliteReader db;
        try { db = new SqliteReader(c.Sql, 0, c.Sql.Length); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        { throw new InvalidDataException("레이어 정보(데이터베이스)를 읽지 못했습니다: " + ex.Message); }
        prog?.Report(0.02);

        var canvasT = new T(db.Table("Canvas"));
        var layerT = new T(db.Table("Layer"));
        if (canvasT.Tab == null || layerT.Tab == null || canvasT.Tab.Rows.Count == 0 || !layerT.Tab.Has("MainId"))
            throw new InvalidDataException("캔버스·레이어 정보를 찾지 못했습니다 — 지원하지 않는 클립 스튜디오 버전이거나 여러 쪽 관리 파일(.cmc)일 수 있습니다");
        var mipT = new T(db.Table("Mipmap"));
        var mipInfoT = new T(db.Table("MipmapInfo"));
        var offT = new T(db.Table("Offscreen"));
        if (offT.Tab == null) Note("픽셀 표(Offscreen)가 없습니다 — 그림 없이 레이어 구조만 들어옵니다");
        res.Version = DetectVersion(db, Note);
        var vecLayers = new HashSet<long>();
        var vecT = db.Table("VectorObjectList");
        if (vecT != null && vecT.Col("LayerId") >= 0) foreach (var r in vecT.Rows) if (r[vecT.Col("LayerId")] is long lid) vecLayers.Add(lid);

        // ---- 캔버스 ----
        object[] cv = canvasT.Tab.Rows[0];
        var proj = db.Table("Project");
        if (proj != null && proj.Col("ProjectCanvas") >= 0 && proj.Rows.Count > 0 && proj.Rows[0][proj.Col("ProjectCanvas")] is long pc && canvasT.Get(pc) is object[] pcv) cv = pcv;
        double dpi = canvasT.D(cv, "CanvasResolution", 350);
        if (dpi < 1 || dpi > 10000) dpi = 350;
        int unit = (int)canvasT.L(cv, "CanvasUnit", 0);
        double perUnit = unit switch { 1 => dpi / 2.54, 2 => dpi / 25.4, 3 => dpi, 5 => dpi / 72, _ => 1 };
        int W = (int)Math.Round(canvasT.D(cv, "CanvasWidth") * perUnit), H = (int)Math.Round(canvasT.D(cv, "CanvasHeight") * perUnit);

        // 오프스크린 정보 (MainId → Attribute)
        var offInfo = new Dictionary<long, OffscreenInfo>();
        OffscreenInfo Info(long offId)
        {
            if (offId == 0) return null;
            if (offInfo.TryGetValue(offId, out var oi)) return oi;
            var row = offT.Get(offId);
            oi = row == null ? null : OffscreenInfo.Parse(offT.B(row, "Attribute"));
            offInfo[offId] = oi;
            return oi;
        }
        long BaseOff(long mipmapId)
        {
            var m = mipT.Get(mipmapId);
            if (m == null) return 0;
            long infoId = mipT.L(m, "BaseMipmapInfo");
            long best = 0; double bestScale = -1;
            var seen = new HashSet<long>();
            while (infoId != 0 && seen.Add(infoId) && seen.Count < 64)
            {
                var mi = mipInfoT.Get(infoId);
                if (mi == null) break;
                double sc = mipInfoT.D(mi, "ThisScale", 100);
                long off = mipInfoT.L(mi, "Offscreen");
                if (off != 0 && (bestScale < 0 || Math.Abs(sc - 100) < Math.Abs(bestScale - 100))) { best = off; bestScale = sc; }
                if (Math.Abs(sc - 100) < 0.01) break;
                infoId = mipInfoT.L(mi, "NextIndex");
            }
            return best;
        }

        // 단위 환산 값이 픽셀 면 크기와 조금 다르면 (짝수 맞춤 등) 픽셀 면 크기를 믿음
        {
            var votes = new Dictionary<(int, int), int>();
            foreach (var r in layerT.Tab.Rows)
            {
                long ox = layerT.L(r, "LayerOffsetX") + layerT.L(r, "LayerRenderOffscrOffsetX"), oy = layerT.L(r, "LayerOffsetY") + layerT.L(r, "LayerRenderOffscrOffsetY");
                if (ox != 0 || oy != 0) continue;
                var oi = Info(BaseOff(layerT.L(r, "LayerRenderMipmap")));
                if (oi?.Valid == true) votes[(oi.W, oi.H)] = votes.GetValueOrDefault((oi.W, oi.H)) + 1;
            }
            if (votes.Count > 0)
            {
                var (vw, vh) = votes.OrderByDescending(kv => kv.Value).First().Key;
                if (W <= 0 || H <= 0 || (Math.Abs(vw - W) <= 4 && Math.Abs(vh - H) <= 4)) { W = vw; H = vh; }
            }
        }
        if (W <= 0 || H <= 0 || W > 60000 || H > 60000 || (long)W * H > 400_000_000L)
            throw new InvalidDataException($"캔버스 크기를 알 수 없거나 너무 큽니다 ({W}×{H})");

        var doc = new Document(W, H) { Name = Path.GetFileNameWithoutExtension(path), Dpi = (int)Math.Round(dpi) };
        res.Doc = doc;

        // ---- 레이어 나무 (뿌리부터, 형제는 아래 → 위) ----
        long root = canvasT.L(cv, "CanvasRootFolder");
        var order = new List<(object[] row, string parentId, int depth)>();     // 그릴 순서 (폴더는 자식 뒤에)
        var idOf = new Dictionary<long, string>();
        var visited = new HashSet<long>();
        void Walk(long first, string parentId, int depth)
        {
            long id = first;
            int guard = 0;
            while (id != 0 && guard++ < 100000)
            {
                var row = layerT.Get(id);
                if (row == null || !visited.Add(id)) { if (row == null) Note("레이어 연결이 끊긴 곳이 있습니다 (일부 레이어 빠짐)"); break; }
                bool folder = (layerT.L(row, "LayerFolder") & 1) != 0;
                if (folder)
                {
                    string fid = Layer.NewId("L"); idOf[id] = fid;
                    if (depth < 60) Walk(layerT.L(row, "LayerFirstChildIndex"), fid, depth + 1);
                    order.Add((row, parentId, depth));
                }
                else { idOf[id] = Layer.NewId("L"); order.Add((row, parentId, depth)); }
                id = layerT.L(row, "LayerNextIndex");
            }
        }
        var rootRow = layerT.Get(root);
        if (rootRow != null) Walk(layerT.L(rootRow, "LayerFirstChildIndex"), null, 0);
        else
        {
            // 뿌리를 못 찾으면 표 순서대로 (구조 없이)
            Note("레이어 나무의 뿌리를 찾지 못해 폴더 구조 없이 표 순서대로 넣었습니다");
            foreach (var r in layerT.Tab.Rows) { long id = layerT.L(r, "MainId"); if (layerT.L(r, "LayerType") == 256) continue; idOf[id] = Layer.NewId("L"); order.Add((r, null, 0)); }
        }
        // 나무에 없는 레이어 (드물게)
        int orphan = layerT.Tab.Rows.Count(r => { long id = layerT.L(r, "MainId"); return id != root && !visited.Contains(id) && layerT.L(r, "LayerType") != 256; });
        if (rootRow != null && orphan > 0) Note($"어느 폴더에도 붙지 않은 레이어 {orphan}개는 건너뛰었습니다");

        // ---- 진행률: 읽을 외부 데이터 바이트 합 ----
        long totalBytes = 1, doneBytes = 0;
        long ExtBytes(long offId) { var row = offT.Get(offId); var id = row == null ? null : ExtId(offT.V(row, "BlockData")); return id != null && c.Exta.TryGetValue(id, out var e) ? e.len : 0; }
        foreach (var (row, _, _) in order)
        {
            totalBytes += ExtBytes(BaseOff(layerT.L(row, "LayerRenderMipmap")));
            totalBytes += ExtBytes(BaseOff(layerT.L(row, "LayerLayerMaskMipmap")));
        }
        void Advance(long n) { doneBytes += n; prog?.Report(0.05 + 0.93 * Math.Min(1, doneBytes / (double)totalBytes)); }

        // ---- 레이어 만들기 ----
        bool paperDone = false;
        long current = canvasT.L(cv, "CanvasCurrentLayer");
        var passFolders = new List<Layer>();
        foreach (var (row, parentId, depth) in order)
        {
            ct.ThrowIfCancellationRequested();
            long mid = layerT.L(row, "MainId");
            long type = layerT.L(row, "LayerType");
            string name = layerT.S(row, "LayerName");
            if (string.IsNullOrWhiteSpace(name)) name = "레이어";
            string label = $"\"{name}\"";
            bool visible = (layerT.L(row, "LayerVisibility", 1) & 1) != 0;
            float opacity = (float)Math.Clamp(layerT.L(row, "LayerOpacity", 256) / 256.0, 0, 1);
            long comp = layerT.L(row, "LayerComposite");
            int offX = (int)layerT.L(row, "LayerOffsetX"), offY = (int)layerT.L(row, "LayerOffsetY");

            // 용지
            if (type == 1584)
            {
                if (!paperDone)
                {
                    paperDone = true;
                    uint r = Col8(layerT.L(row, "DrawColorMainRed", 0xFFFFFFFF)), g = Col8(layerT.L(row, "DrawColorMainGreen", 0xFFFFFFFF)), b = Col8(layerT.L(row, "DrawColorMainBlue", 0xFFFFFFFF));
                    doc.PaperColor = $"#{r:x2}{g:x2}{b:x2}";
                    doc.PaperVisible = visible;
                }
                else Note("용지 레이어가 여럿이라 첫 번째 색만 용지 색으로 썼습니다");
                continue;
            }
            if (type == 256) continue;
            if (type == 800) { res.Missing.Add($"{label} (작품 정보: 작품명·쪽 번호) — ComicPad 에 해당 기능이 없어 건너뜀"); continue; }
            if (type == 10016 || layerT.L(row, "LightTableReferCell") != 0) { res.Missing.Add($"{label} (라이트 테이블 참조) — 건너뜀"); continue; }

            var l = new Layer
            {
                Id = idOf.TryGetValue(mid, out var lid) ? lid : Layer.NewId("L"),
                Name = name, Parent = parentId, Visible = visible, Opacity = opacity,
                Clip = layerT.L(row, "LayerClip") != 0,
            };
            long lockv = layerT.L(row, "LayerLock");
            l.Locked = (lockv & ~16L) != 0;
            l.AlphaLock = (lockv & 16) != 0;
            if (layerT.L(row, "DraftLayer") != 0) l.Draft = true;
            if (layerT.Tab.Has("ReferLayer") && layerT.L(row, "ReferLayer") != 0) l.Ref = true;
            l.Extra["clipStudio"] = new JsonObject { ["id"] = mid, ["type"] = type, ["composite"] = comp };
            var (mode, bnote) = MapBlend(comp);
            l.Blend = mode;
            if (bnote != null) Note(bnote);

            // 폴더
            if ((layerT.L(row, "LayerFolder") & 1) != 0)
            {
                l.Kind = "folder";
                l.Expanded = (layerT.L(row, "LayerFolder") & 16) != 0;
                if (comp == PassThrough) passFolders.Add(l);
                if (layerT.Present(row, "ComicFrameLineMipmap"))
                    res.Missing.Add($"{label} (컷 테두리 폴더) — 컷 선과 컷 밖 가리기는 들어오지 않음, 안의 그림만");
                MaskInto(l, row);
                doc.Layers.Add(l); res.FolderCount++;
                continue;
            }

            // 종류 판정 (csp-format 03장 classify 순서)
            string kind;
            if (layerT.Present(row, "TextLayerString")) kind = "text";
            else if (layerT.Present(row, "GradationFillInfo")) kind = "gradient";
            else if (layerT.Present(row, "FilterLayerInfo") || (type & 4096) != 0) kind = "adjust";
            else if (layerT.Present(row, "FirstLayerObject")) kind = "3d";
            else if (layerT.Present(row, "ResizableImageInfo")) kind = "image";
            else if (vecLayers.Contains(mid)) kind = "vector";
            else if ((type & 1) != 0) kind = "raster";
            else if (type == 0 && layerT.Present(row, "VectorNormalStrokeIndex")) kind = "vector";
            else kind = "other";
            string kindName = kind switch { "text" => "텍스트", "gradient" => "그라데이션·단색 칠", "adjust" => "색조 보정", "3d" => "3D", "image" => "이미지 소재", "vector" => "벡터", "other" => "알 수 없는 종류", _ => "래스터" };
            l.Extra["clipStudio"]["kind"] = kind;

            l.Px = new Surface(W, H);
            bool hasPx = false;
            // 색조 보정은 저장된 그림이 보정 결과(오래됐을 수 있음)라 쓰지 않음
            if (kind != "adjust")
            {
                long off = BaseOff(layerT.L(row, "LayerRenderMipmap"));
                int px0 = offX + (int)layerT.L(row, "LayerRenderOffscrOffsetX"), py0 = offY + (int)layerT.L(row, "LayerRenderOffscrOffsetY");
                hasPx = DecodeOffscreen(off, l.Px, px0, py0, false);
            }
            if (layerT.L(row, "DrawColorEnable") != 0)
            {
                uint r = Col8(layerT.L(row, "DrawColorMainRed")), g = Col8(layerT.L(row, "DrawColorMainGreen")), b = Col8(layerT.L(row, "DrawColorMainBlue"));
                l.Tint = $"#{r:x2}{g:x2}{b:x2}";
                Note("레이어 컬러는 ComicPad 레이어 색(모양만 남기고 한 색)으로 들어왔습니다 — 밝은 부분의 보조색은 빠짐");
            }

            switch (kind)
            {
                case "raster": break;
                case "text":
                    {
                        string text = layerT.S(row, "TextLayerString") ?? "";
                        var meta = ClipText.Meta(layerT.B(row, "TextLayerAttributes"), text, offX, offY, dpi);
                        if (!hasPx && meta.imageOff != 0) hasPx = DecodeOffscreen(meta.imageOff, l.Px, meta.imgX, meta.imgY, false);
                        l.Meta = new JsonObject { ["psdText"] = meta.json };
                        string shortText = text.Replace("\r", " ").Replace("\n", " ");
                        if (shortText.Length > 30) shortText = shortText[..30] + "…";
                        res.Missing.Add(hasPx
                            ? $"{label} (텍스트 \"{shortText}\") — 그림으로 들어옴. 텍스트 도구로 탭하면 다시 편집할 글자로 바꿀 수 있음"
                            : $"{label} (텍스트 \"{shortText}\") — 저장된 그림이 없어 빈 레이어. 텍스트 도구로 탭하면 글자로 다시 만들 수 있음");
                        break;
                    }
                case "vector":
                    if (!hasPx)
                    {
                        int n = ClipVector.Render(db, c, layerT.L(row, "MainId"), l.Px, offX, offY, Note);
                        if (n > 0) { hasPx = true; res.Missing.Add($"{label} (벡터) — 선 {n}개를 근사해서 그림으로 다시 그림 (브러시 모양·질감은 다를 수 있음, 벡터 편집 불가)"); }
                        else res.Missing.Add($"{label} (벡터) — 저장된 그림도 읽을 선도 없어 빈 레이어");
                    }
                    else res.Missing.Add($"{label} (벡터) — 그림으로 들어옴 (벡터 편집 불가)");
                    break;
                case "adjust":
                    res.Missing.Add($"{label} (색조 보정 레이어) — 설정 형식을 몰라 효과 없는 빈 레이어로");
                    l.Name = name + " [색조 보정]";
                    break;
                default:
                    if (hasPx) res.Missing.Add($"{label} ({kindName}) — 그림으로 들어옴 (원래 기능은 없어짐)");
                    else { res.Missing.Add($"{label} ({kindName}) — 저장된 그림이 없어 빈 레이어"); l.Name = name + $" [{kindName}]"; }
                    break;
            }
            if (!hasPx && kind != "text" && kind != "vector" && kind != "adjust" && kind != "raster" && !l.Name.EndsWith("]")) l.Name = name + $" [{kindName}]";
            if (!hasPx && kind == "vector") l.Name = name + " [벡터]";
            MaskInto(l, row);
            doc.Layers.Add(l); res.LayerCount++;
            if (mid == current) doc.ActiveId = l.Id;
        }

        // 통과 폴더: ComicPad 폴더는 늘 따로 합성 → 안쪽에 표준이 아닌 합성·보정 레이어가 있으면 달라 보일 수 있음
        foreach (var f in passFolders)
        {
            f.Extra["clipStudio"]["passThrough"] = true;
            bool risky = doc.Layers.Any(x => doc.IsInside(x, f) && (x.Blend != "source-over" || x.Extra["clipStudio"]?["kind"]?.ToString() == "adjust"));
            if (risky) Note($"통과 폴더 \"{f.Name}\" 는 일반 폴더로 들어왔습니다 — 안쪽 레이어의 합성 모드가 폴더 밖 그림과 섞이지 않아 조금 달라 보일 수 있음");
        }
        if (doc.Layers.Count(x => !x.IsFolder) == 0)
        {
            var empty = new Layer(doc, "레이어 1");
            doc.Layers.Add(empty);
            Note("그림 레이어가 하나도 없어 빈 레이어를 하나 넣었습니다");
        }
        if (doc.ActiveId == null || doc.ById(doc.ActiveId)?.IsFolder != false) doc.ActiveId = doc.Layers.LastOrDefault(x => !x.IsFolder)?.Id;
        doc.Extra["clipImport"] = new JsonObject { ["file"] = Path.GetFileName(path), ["version"] = res.Version };
        prog?.Report(1);
        res.Elapsed = sw.Elapsed;
        return res;

        // ---- 오프스크린 하나를 면에 풀기 (블록을 여러 코어로) ----
        bool DecodeOffscreen(long offId, Surface s, int ox, int oy, bool asMask, int fillOutside = -1)
        {
            if (offId == 0) return false;
            var row = offT.Get(offId);
            if (row == null) return false;
            var info = Info(offId);
            string ext = ExtId(offT.V(row, "BlockData"));
            byte[] data;
            try { data = c.ExtaData(ext); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { Note("일부 픽셀 데이터를 읽지 못했습니다: " + ex.Message); return false; }
            if (info == null || !info.Valid)
            {
                if (data != null) Note("픽셀 형식 정보(Attribute)를 모르는 레이어가 있어 그림을 넣지 못했습니다");
                return false;
            }
            // 마스크: 바깥/빈 블록 채우기
            if (asMask)
            {
                int outside = info.DoInit ? info.InitAlpha : 255;
                int inside = info.DoInit ? info.InitAlpha : 0;
                FillMask(s, outside);
                var r = new Rect(ox, oy, info.W, info.H).Clip(s.W, s.H);
                if (!r.IsEmpty && inside != outside) FillRect(s, r, inside);
            }
            else if (info.DoInit && info.InitAlpha > 0)
            {
                var r = new Rect(ox, oy, info.W, info.H).Clip(s.W, s.H);
                if (!r.IsEmpty) { uint v = Px.Pack((uint)info.InitAlpha, (uint)info.InitAlpha, (uint)info.InitAlpha, (uint)info.InitAlpha); var buf = new uint[r.W * r.H]; Array.Fill(buf, v); s.Write(r, buf); }
            }
            if (data == null) return false;
            var warn = new List<string>();
            var blocks = ClipBlocks.List(data, warn);
            foreach (var w in warn) Note("픽셀 데이터 일부가 깨져 있습니다: " + w);
            var outPx = new uint[blocks.Count][];
            int bad = 0, unknownFmt = 0;
            Parallel.For(0, blocks.Count, new ParallelOptions { CancellationToken = ct }, k =>
            {
                try
                {
                    var raw = ClipBlocks.Inflate(data, blocks[k]);
                    var f = ClipBlocks.FormatOf(info, raw.Length);
                    if (f == ClipBlocks.Fmt.Unknown) { Interlocked.Increment(ref unknownFmt); return; }
                    outPx[k] = ClipBlocks.ToPixels(raw, f, asMask, info.ImageOffset) ?? (asMask ? new uint[65536] : null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Interlocked.Increment(ref bad); }
            });
            if (bad > 0) Note($"압축이 깨진 블록 {bad}개는 비워 두었습니다");
            if (unknownFmt > 0) Note($"모르는 픽셀 형식 블록 {unknownFmt}개는 비워 두었습니다");
            bool any = false, clipped = false;
            for (int k = 0; k < blocks.Count; k++)
            {
                var px = outPx[k];
                if (px == null) continue;
                int bi = blocks[k].Index, bx = bi % info.BX, by = bi / info.BX;
                if (by >= info.BY) continue;
                int bw = Math.Min(256, info.W - bx * 256), bh = Math.Min(256, info.H - by * 256);
                if (bw <= 0 || bh <= 0) continue;
                if (bw < 256 || bh < 256)
                {
                    var crop = new uint[bw * bh];
                    for (int y = 0; y < bh; y++) Array.Copy(px, y * 256, crop, y * bw, bw);
                    px = crop;
                }
                var rect = new Rect(ox + bx * 256, oy + by * 256, bw, bh);
                if (rect.X < 0 || rect.Y < 0 || rect.R > s.W || rect.B > s.H) clipped = true;
                s.Write(rect, px);
                any = true;
            }
            if (clipped && !asMask) Note("캔버스 밖으로 나간 그림은 잘렸습니다 (ComicPad 레이어는 캔버스 크기)");
            Advance(data.Length);
            return any;
        }

        void MaskInto(Layer l, object[] row)
        {
            long mm = layerT.L(row, "LayerLayerMaskMipmap");
            if (mm == 0) return;
            long off = BaseOff(mm);
            if (off == 0) return;
            var ms = new Surface(W, H);
            int mx = (int)(layerT.L(row, "LayerOffsetX") + layerT.L(row, "LayerMaskOffsetX") + layerT.L(row, "LayerMaskOffscrOffsetX"));
            int my = (int)(layerT.L(row, "LayerOffsetY") + layerT.L(row, "LayerMaskOffsetY") + layerT.L(row, "LayerMaskOffscrOffsetY"));
            if (Info(off)?.Valid != true) return;
            DecodeOffscreen(off, ms, mx, my, true);
            l.Mask = ms;
            l.MaskOn = (layerT.L(row, "LayerVisibility", 1) & 2) != 0;
        }
    }

    static string ExtId(object v) => v switch { string s => s.TrimEnd('\0'), byte[] b => Encoding.ASCII.GetString(b).TrimEnd('\0'), _ => null };

    static void FillMask(Surface s, int a)
    {
        if (a <= 0) { s.Clear(); return; }
        uint v = Px.Pack((uint)a, (uint)a, (uint)a, (uint)a);
        for (int ty = 0; ty < s.TY; ty++)
            for (int tx = 0; tx < s.TX; tx++) { var t = new uint[Surface.TS * Surface.TS]; Array.Fill(t, v); s.SetTile(tx, ty, t); }
    }

    static void FillRect(Surface s, Rect r, int a)
    {
        uint v = a <= 0 ? 0 : Px.Pack((uint)a, (uint)a, (uint)a, (uint)a);
        var buf = new uint[r.W * r.H];
        if (v != 0) Array.Fill(buf, v);
        s.Write(r, buf);
    }

    /// <summary>ParamScheme 에 등록된 표시 열로 저장한 세대 추정 (csp-format 09장)</summary>
    static string DetectVersion(SqliteReader db, Action<string> note)
    {
        var ps = db.Table("ParamScheme");
        if (ps == null || ps.Col("TableName") < 0 || ps.Col("LabelName") < 0) return "알 수 없음";
        var set = new HashSet<string>(StringComparer.Ordinal);
        int tn = ps.Col("TableName"), ln = ps.Col("LabelName");
        int maxHas = 0;
        foreach (var r in ps.Rows)
        {
            string t = r[tn] as string, l = r[ln] as string;
            if (t == null || l == null) continue;
            set.Add(t + "." + l);
            if (t == "Layer" && l.StartsWith("Has1_0_") && int.TryParse(l[7..], out int n)) maxHas = Math.Max(maxHas, n);
        }
        if (maxHas > 19) note($"이 가져오기가 아는 것보다 새 클립 스튜디오(형식 {maxHas})에서 저장된 파일입니다 — 새 기능은 빠질 수 있음");
        if (maxHas >= 19) return "5.1 이후";
        if (maxHas >= 18) return "5.0";
        if (maxHas >= 15) return "4.2";
        if (maxHas >= 12) return "3.0~4.1";
        if (maxHas >= 8) return "2.1~2.x";
        if (set.Contains("Layer.WebCooperationLayerEditable")) return "2.0";
        if (maxHas >= 7) return "1.12~1.13";
        if (set.Contains("Layer.HasPrimitive")) return "1.11~1.12";
        if (set.Contains("BrushStyle.DualStyleIndex")) return "1.10.10~1.11";
        if (set.Contains("Canvas.TimeLapseManagerIndex")) return "1.10.5~1.10.9";
        if (set.Contains("Layer.TextLayerVersion")) return "1.8~1.10.3";
        return "1.7 이전";
    }
}
