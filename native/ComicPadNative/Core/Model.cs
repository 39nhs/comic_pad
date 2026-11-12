// 문서·레이어: 웹 판(app/js/core/doc.js)과 같은 속성. .cpad 에 있는데 아직 쓰지 않는 값도 그대로 들고 있다가 저장할 때 돌려준다.
using System.Text.Json.Nodes;

namespace ComicPadNative.Core;

public sealed class Layer
{
    public string Id = NewId("L");
    public string Name = "레이어";
    public string Kind = "raster";        // raster | folder | adjust | text | panel | bubble | vector | ...
    public bool Visible = true;
    public float Opacity = 1;
    public string Blend = "source-over";
    public bool Locked, AlphaLock, Clip, Draft, Ref, Rough;
    public string Tint;                   // "#rrggbb" (러프 파란선 등)
    public bool MaskOn = true;
    public bool EditMask;                 // 마스크 편집 중 (저장 안 함)
    public string Parent;                 // 들어 있는 폴더 id
    public bool Expanded = true;
    public JsonObject Meta;               // 종류별 정보 (조정 값, 말풍선, 컷, 벡터 선, 애니 트랙 …)
    public JsonObject Tone;               // 톤화(망점)
    public JsonObject Fx;                 // 레이어 효과 (테두리·그림자·광선)
    public JsonObject Extra = new();      // 이 앱이 모르는 속성 (저장할 때 그대로)
    public Surface Px;                    // 픽셀 (폴더·조정 레이어는 null)
    public Surface Mask;                  // 레이어 마스크 (알파 = 보이는 정도)
    public int Version;                   // 픽셀이 바뀔 때마다 +1 (캐시용)

    public bool IsFolder => Kind == "folder";
    public bool IsAdjust => Kind == "adjust";
    public bool HasPixels => Px != null;

    public Layer() { }
    public Layer(Document d, string name = null, string kind = "raster")
    {
        Kind = kind;
        if (name != null) Name = name;
        if (kind != "folder" && kind != "adjust") Px = new Surface(d.W, d.H);
    }

    /// <summary>그리기 대상: 마스크 편집 중이면 마스크</summary>
    public Surface Target => EditMask && Mask != null ? Mask : Px;

    public void Touch() { Version++; }

    /// <summary>속성만 복사 (픽셀·마스크는 같은 면을 가리킴) — 실행취소 스냅숏용</summary>
    public Layer PropsCopy()
    {
        var l = (Layer)MemberwiseClone();
        l.Meta = (JsonObject)Meta?.DeepClone(); l.Tone = (JsonObject)Tone?.DeepClone(); l.Fx = (JsonObject)Fx?.DeepClone();
        l.Extra = (JsonObject)Extra.DeepClone();
        return l;
    }

    public Layer Clone(Document d, string name = null)
    {
        var l = (Layer)MemberwiseClone();
        l.Id = NewId("L");
        l.Name = name ?? Name + " 복사";
        l.Meta = (JsonObject)Meta?.DeepClone(); l.Tone = (JsonObject)Tone?.DeepClone(); l.Fx = (JsonObject)Fx?.DeepClone();
        l.Extra = (JsonObject)Extra.DeepClone();
        l.Px = Px?.Clone(); l.Mask = Mask?.Clone();
        l.Version = 0;
        return l;
    }

    static int seq;
    public static string NewId(string p) => p + DateTime.Now.Ticks.ToString("x")[^7..] + Interlocked.Increment(ref seq).ToString("x");
}

public sealed class Document
{
    public int W, H;
    public string Name = "제목 없음";
    public string FilePath;               // 저장한 파일 (없으면 새 작품)
    public int Dpi = 350;
    public string PaperColor = "#ffffff";
    public bool PaperVisible = true;
    public JsonObject PaperExtra = new();     // 용지 질감 등
    public readonly List<Layer> Layers = new();   // 아래 → 위
    public string ActiveId;
    public Selection Sel;
    public JsonObject Extra = new();          // rulers, anim, print, notes, timelapse, vtuber, pixel, paths, workTime …
    public bool Modified;

    public Document(int w, int h) { W = w; H = h; Sel = new Selection(w, h); }

    public Layer Active => Layers.Find(l => l.Id == ActiveId) ?? (Layers.Count > 0 ? Layers[^1] : null);
    public Layer ById(string id) => id == null ? null : Layers.Find(l => l.Id == id);
    public int IndexOf(Layer l) => Layers.IndexOf(l);

    /// <summary>실제로 있는 폴더만 부모로</summary>
    public string ParentOf(Layer l) { var p = ById(l.Parent); return p != null && p.IsFolder ? p.Id : null; }
    public List<Layer> ChildrenOf(Layer folder) { string id = folder?.Id; return Layers.Where(l => ParentOf(l) == id).ToList(); }
    public int DepthOf(Layer l) { int d = 0; var p = ParentOf(l); while (p != null && d < 32) { d++; p = ParentOf(ById(p)); } return d; }
    public bool IsInside(Layer l, Layer folder) { var p = ParentOf(l); for (int i = 0; p != null && i < 64; i++) { if (p == folder.Id) return true; p = ParentOf(ById(p)); } return false; }

    public JsonObject Anim => Extra["anim"] as JsonObject ?? (JsonObject)(Extra["anim"] = new JsonObject { ["fps"] = 12, ["frame"] = 0, ["onion"] = true, ["onionOpacity"] = 0.3, ["onionPrev"] = 1, ["onionNext"] = 1, ["loop"] = true });

    /// <summary>새 문서 + 빈 레이어 하나</summary>
    public static Document New(int w, int h, string name = null)
    {
        var d = new Document(w, h);
        if (name != null) d.Name = name;
        var l = new Layer(d, "레이어 1");
        d.Layers.Add(l); d.ActiveId = l.Id;
        return d;
    }
}
