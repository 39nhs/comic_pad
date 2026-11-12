// 브러시 프리셋: 웹 판 core/brush.js 의 BRUSH_DEFAULTS · DEFAULT_PRESETS 와 tools/brush.js 의 presets()/activePreset() 를 옮김.
// 설정 키도 웹과 같다: brushes { brush:[…], eraser:[…] }, activePreset { brush, eraser }, seenPresets.
// JSON 키 = 필드 이름의 첫 글자를 소문자로 (Size ↔ size). 이 앱이 모르는 키는 Extra 에 두었다가 그대로 다시 씀.
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.Brushes;

public sealed class BrushPreset
{
    public string Id = "", Name = "";
    // ---- BRUSH_DEFAULTS (순서도 웹과 같게) ----
    public double Size = 10, Opacity = 1, Flow = 1, Hardness = 1, Spacing = 0.08;
    public bool PressureSize = true; public double MinSize = 0.2; public bool PressureOpacity; public double MinOpacity = 0.2;
    public double Stabilizer = 3, Grain = 0; public bool Buildup; public string Color;
    public string Tip = "round";            // round | grain | spray | flat | image | pixel
    public string TipImage;                 // 에셋 경로 (tip:'image')
    public bool TintTip = true; public double RotJitter, Scatter, SizeJitter; public bool FollowDir;
    public string Texture = "none"; public double TextureStrength = 0.6, TextureScale = 1;   // 종이 결
    public double TiltSize, TiltOpacity, SpeedOpacity, HueJitter, SatJitter, ValJitter; public bool PressureColor;
    public double TaperIn, TaperOut;        // 입출 보정 (문서 px 길이)
    public string Blend = "source-over";
    public double Mix;                      // 혼색
    public double Density = 24, Particle = 1.2;          // 스프레이
    public double FlatRatio = 0.3, Angle = 45; public bool UseTilt;   // 납작 펜
    public bool RoughOnly;
    public double SpeedThin;
    public string StabMode = "auto";        // auto | euro | avg | lazy | spring
    public bool CatchUp = true;
    public double[][] PressurePts;          // 이 브러시만의 필압 곡선
    public bool PixelPerfect = true, Shading; public double ShadeDir = -1;
    public double Dither, DitherSize = 4;
    public string DualTip = "none"; public double DualSize = 0.35, DualDensity = 6, DualScatter = 1;
    public bool Ribbon;
    public string TextureMode = "multiply";
    /// <summary>이 앱이 모르는 키 (abr 등) — 그대로 보관</summary>
    public JsonObject Extra = new();

    static readonly FieldInfo[] fields = typeof(BrushPreset).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.Name != nameof(Extra)).ToArray();
    static readonly Dictionary<string, FieldInfo> byKey = fields.ToDictionary(f => Key(f.Name));
    static string Key(string n) => char.ToLowerInvariant(n[0]) + n[1..];
    public static IEnumerable<string> Keys => fields.Select(f => Key(f.Name));

    /// <summary>makePreset: 기본값 위에 JSON 값을 얹음</summary>
    public static BrushPreset FromJson(JsonObject o)
    {
        var p = new BrushPreset();
        if (o == null) return p;
        foreach (var (k, v) in o)
        {
            if (byKey.TryGetValue(k, out var f)) { try { Assign(p, f, v); } catch { } }
            else p.Extra[k] = v?.DeepClone();
        }
        return p;
    }

    static void Assign(BrushPreset p, FieldInfo f, JsonNode v)
    {
        var t = f.FieldType;
        if (t == typeof(double)) { double? d = Num(v); if (d.HasValue) f.SetValue(p, d.Value); }
        else if (t == typeof(bool)) f.SetValue(p, Bool(v));
        else if (t == typeof(string)) f.SetValue(p, v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v == null ? null : v.ToJsonString().Trim('"'));
        else if (t == typeof(double[][]))
        {
            if (v is JsonArray arr)
            {
                var list = new List<double[]>();
                foreach (var it in arr) if (it is JsonArray pt && pt.Count >= 2) list.Add(new[] { Num(pt[0]) ?? 0, Num(pt[1]) ?? 0 });
                f.SetValue(p, list.Count >= 2 ? list.ToArray() : null);
            }
            else f.SetValue(p, null);
        }
    }

    public static double? Num(JsonNode v)
    {
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        if (jv.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }
    public static bool Bool(JsonNode v)
    {
        if (v is not JsonValue jv) return false;
        if (jv.TryGetValue<bool>(out var b)) return b;
        var d = Num(v); if (d.HasValue) return d.Value != 0;
        return jv.TryGetValue<string>(out var s) && s.Length > 0;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        foreach (var f in fields)
        {
            var v = f.GetValue(this);
            string k = Key(f.Name);
            o[k] = v switch
            {
                null => null,
                double d => JsonValue.Create(Clean(d)),
                bool b => JsonValue.Create(b),
                string s => JsonValue.Create(s),
                double[][] pts => new JsonArray(pts.Select(pt => (JsonNode)new JsonArray(JsonValue.Create(Clean(pt[0])), JsonValue.Create(Clean(pt[1])))).ToArray()),
                _ => null,
            };
        }
        foreach (var (k, v) in Extra) if (!o.ContainsKey(k)) o[k] = v?.DeepClone();
        return o;
    }
    static double Clean(double d) => double.IsFinite(d) ? Math.Round(d, 6) : 0;

    public BrushPreset Clone() => FromJson(ToJson());
    public void CopyFrom(BrushPreset o) { foreach (var f in fields) f.SetValue(this, f.GetValue(o.Clone())); Extra = (JsonObject)o.Extra.DeepClone(); }

    public bool HasDual => DualTip != null && DualTip != "none";
    /// <summary>획 뒤처리(종이 결·디더)가 있는지 (paper.js hasBrushPost)</summary>
    public bool HasPost => (Texture != null && Texture != "none" && TextureStrength > 0) || (Dither > 0 && Dither < 1 && Tip != "pixel");
}

/// <summary>기본 프리셋 (웹 DEFAULT_PRESETS 그대로)</summary>
public static class DefaultPresets
{
    const string BrushJson = """
    [
      { "id": "notepen", "name": "필기 펜 (노트 느낌)", "size": 4, "hardness": 1, "minSize": 0.22, "stabilizer": 3, "speedThin": 0.35, "spacing": 0.05 },
      { "id": "gpen", "name": "G펜", "size": 6, "hardness": 1, "minSize": 0.04, "stabilizer": 4 },
      { "id": "maru", "name": "마루펜", "size": 3, "hardness": 1, "minSize": 0.45, "stabilizer": 3 },
      { "id": "pencil", "name": "연필", "size": 7, "hardness": 0.55, "tip": "grain", "grain": 0.65, "pressureOpacity": true, "minOpacity": 0.15, "minSize": 0.55, "stabilizer": 1, "spacing": 0.1 },
      { "id": "rough", "name": "러프 연필", "size": 9, "hardness": 0.5, "tip": "grain", "grain": 0.55, "pressureOpacity": true, "minOpacity": 0.2, "minSize": 0.6, "stabilizer": 1, "opacity": 0.85, "roughOnly": true },
      { "id": "pencilgrain", "name": "연필 (종이 결)", "size": 9, "hardness": 0.6, "tip": "grain", "grain": 0.4, "pressureOpacity": true, "minOpacity": 0.2, "minSize": 0.5, "stabilizer": 1, "spacing": 0.08, "texture": "rough", "textureStrength": 0.75 },
      { "id": "watergrain", "name": "수채 (종이 결)", "size": 60, "hardness": 0.3, "flow": 0.4, "opacity": 0.8, "mix": 0.4, "pressureOpacity": true, "minOpacity": 0.15, "stabilizer": 1, "spacing": 0.12, "texture": "rough", "textureStrength": 0.55 },
      { "id": "marker", "name": "마커", "size": 24, "hardness": 0.95, "pressureSize": false, "stabilizer": 1 },
      { "id": "soft", "name": "부드러운 브러시", "size": 60, "hardness": 0.1, "flow": 0.35, "pressureSize": false, "pressureOpacity": true, "minOpacity": 0, "stabilizer": 0 },
      { "id": "air", "name": "에어브러시", "size": 120, "hardness": 0, "flow": 0.06, "spacing": 0.12, "pressureSize": false, "pressureOpacity": true, "minOpacity": 0, "buildup": true, "stabilizer": 0 },
      { "id": "fillpen", "name": "채색 펜", "size": 30, "hardness": 0.98, "pressureSize": true, "minSize": 0.7, "stabilizer": 0 },
      { "id": "water", "name": "수채 (혼색)", "size": 50, "hardness": 0.35, "flow": 0.45, "opacity": 0.85, "mix": 0.55, "pressureSize": true, "minSize": 0.5, "pressureOpacity": true, "minOpacity": 0.2, "stabilizer": 1, "spacing": 0.12 },
      { "id": "oil", "name": "유화 (혼색)", "size": 36, "hardness": 0.9, "mix": 0.45, "pressureSize": true, "minSize": 0.6, "stabilizer": 1, "spacing": 0.06 },
      { "id": "blendbrush", "name": "색 섞기 (혼색만)", "size": 40, "hardness": 0.5, "flow": 0.5, "mix": 1, "pressureSize": false, "stabilizer": 0, "spacing": 0.1 },
      { "id": "dodge", "name": "발광 닷지", "size": 80, "hardness": 0.1, "flow": 0.15, "blend": "color-dodge", "pressureSize": false, "pressureOpacity": true, "minOpacity": 0, "stabilizer": 0, "buildup": true },
      { "id": "mulshadow", "name": "곱하기 그림자", "size": 40, "hardness": 0.6, "opacity": 0.6, "blend": "multiply", "pressureSize": true, "minSize": 0.5, "stabilizer": 1, "color": "#8a7aa8" },
      { "id": "overlaylight", "name": "오버레이 빛", "size": 90, "hardness": 0.1, "flow": 0.2, "blend": "overlay", "pressureSize": false, "pressureOpacity": true, "minOpacity": 0, "stabilizer": 0, "buildup": true },
      { "id": "spray", "name": "스프레이", "size": 70, "tip": "spray", "density": 26, "particle": 1.3, "pressureSize": false, "pressureOpacity": true, "minOpacity": 0.2, "stabilizer": 0, "spacing": 0.25 },
      { "id": "calli", "name": "캘리그래피 펜", "size": 18, "tip": "flat", "flatRatio": 0.22, "angle": 40, "useTilt": true, "hardness": 1, "minSize": 0.5, "stabilizer": 3, "spacing": 0.04 },
      { "id": "pixel1", "name": "픽셀 연필 (1px)", "size": 1, "tip": "pixel", "pixelPerfect": true, "pressureSize": false, "stabilizer": 0, "spacing": 0.1 },
      { "id": "pixelshade", "name": "픽셀 명암 잉크", "size": 1, "tip": "pixel", "shading": true, "shadeDir": -1, "pressureSize": false, "stabilizer": 0, "spacing": 0.1 },
      { "id": "pixeldither", "name": "픽셀 디더 (50%)", "size": 4, "tip": "pixel", "pixelPerfect": false, "dither": 0.5, "ditherSize": 2, "pressureSize": false, "stabilizer": 0, "spacing": 0.1 },
      { "id": "dualdry", "name": "마른 붓 (듀얼)", "size": 34, "hardness": 0.8, "tip": "round", "dualTip": "grain", "dualSize": 0.4, "dualDensity": 7, "dualScatter": 1, "pressureSize": true, "minSize": 0.5, "stabilizer": 1, "spacing": 0.08 },
      { "id": "ribbonbrush", "name": "리본 붓", "size": 26, "hardness": 0.7, "tip": "grain", "grain": 0.6, "ribbon": true, "pressureSize": true, "minSize": 0.4, "stabilizer": 2, "spacing": 0.05 }
    ]
    """;
    const string EraserJson = """
    [
      { "id": "er-hard", "name": "딱딱한 지우개", "size": 24, "hardness": 1, "pressureSize": false, "stabilizer": 0 },
      { "id": "er-soft", "name": "부드러운 지우개", "size": 80, "hardness": 0.1, "flow": 0.4, "pressureSize": false, "pressureOpacity": true, "minOpacity": 0, "stabilizer": 0 },
      { "id": "er-fine", "name": "세밀 지우개", "size": 4, "hardness": 1, "minSize": 0.3, "stabilizer": 2 },
      { "id": "er-pixel", "name": "픽셀 지우개", "size": 1, "tip": "pixel", "pixelPerfect": false, "pressureSize": false, "stabilizer": 0, "spacing": 0.1 }
    ]
    """;

    /// <summary>분류별 기본 프리셋 (웹처럼 바뀐 값만 들어 있는 JSON)</summary>
    public static JsonArray Raw(string cat) => (JsonArray)JsonNode.Parse(cat == "eraser" ? EraserJson : cat == "brush" ? BrushJson : "[]");
    public static List<BrushPreset> Of(string cat) => Raw(cat).Select(n => BrushPreset.FromJson((JsonObject)n)).ToList();
    public static BrushPreset Find(string cat, string id) => Of(cat).Find(p => p.Id == id);
}

/// <summary>프리셋 목록 (설정 brushes/activePreset/seenPresets 와 같은 내용)</summary>
public static class PresetStore
{
    public static readonly string[] Cats = { "brush", "eraser" };
    static readonly Dictionary<string, List<BrushPreset>> lists = new();
    static JsonNode loadedFrom;
    public static event Action Changed;          // 목록·값이 바뀜 (미리보기 다시 그리기)

    static JsonObject Obj(string key)
    {
        if (App.Settings[key] is JsonObject o) return o;
        var n = new JsonObject(); App.Settings[key] = n; return n;
    }

    /// <summary>presets(cat): 없으면 기본값, 새 버전 기본 프리셋은 한 번씩 넣어 줌 (지운 건 다시 안 넣음)</summary>
    public static List<BrushPreset> List(string cat)
    {
        if (!ReferenceEquals(loadedFrom, App.Settings["brushes"]) || App.Settings["brushes"] == null) { lists.Clear(); }
        if (lists.TryGetValue(cat, out var l)) return l;
        var brushes = App.Settings["brushes"] as JsonObject;
        if (brushes == null)
        {
            brushes = new JsonObject();
            foreach (var c in Cats) brushes[c] = DefaultPresets.Raw(c);
            App.Settings["brushes"] = brushes;
        }
        loadedFrom = brushes;
        if (brushes[cat] is not JsonArray arr) brushes[cat] = arr = DefaultPresets.Raw(cat);
        l = arr.OfType<JsonObject>().Select(BrushPreset.FromJson).ToList();
        var seenObj = Obj("seenPresets");
        var seen = new HashSet<string>(seenObj[cat] is JsonArray sa ? sa.Select(x => (string)x).Where(x => x != null) : l.Select(p => p.Id));
        foreach (var d in DefaultPresets.Of(cat))
        {
            if (!seen.Contains(d.Id) && !l.Any(p => p.Id == d.Id)) l.Add(d);
            seen.Add(d.Id);
        }
        seenObj[cat] = new JsonArray(seen.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
        // 예전 '러프 연필(파랑)' → 러프 레이어 전용, 색은 레이어를 따름
        foreach (var p in l)
            if (p.Id == "rough" && arr.OfType<JsonObject>().FirstOrDefault(o => (string)o["id"] == "rough") is JsonObject ro && !ro.ContainsKey("roughOnly"))
            { p.RoughOnly = true; p.Color = null; if (p.Name == "러프 연필(파랑)") p.Name = "러프 연필"; }
        if (l.Count == 0) l.AddRange(DefaultPresets.Of(cat));
        lists[cat] = l;
        Write(cat);
        return l;
    }

    static void Write(string cat)
    {
        if (!lists.TryGetValue(cat, out var l)) return;
        var brushes = App.Settings["brushes"] as JsonObject;
        if (brushes == null) { brushes = new JsonObject(); App.Settings["brushes"] = brushes; }
        brushes[cat] = new JsonArray(l.Select(p => (JsonNode)p.ToJson()).ToArray());
        loadedFrom = brushes;
    }

    /// <summary>값을 바꾼 뒤 부름: 설정에 쓰고 곧 저장</summary>
    public static void Save(string cat = null)
    {
        foreach (var c in cat == null ? lists.Keys.ToList() : new List<string> { cat }) Write(c);
        App.SaveSettingsSoon();
        Changed?.Invoke();
    }

    public static string ActiveId(string cat) => (string)(App.Settings["activePreset"] as JsonObject)?[cat];

    public static BrushPreset Active(string cat)
    {
        var l = List(cat);
        var p = l.Find(x => x.Id == ActiveId(cat));
        if (p == null) { p = l.FirstOrDefault(); Obj("activePreset")[cat] = p?.Id; }
        return p;
    }

    public static void SetActive(string cat, string id)
    {
        Obj("activePreset")[cat] = id;
        App.SaveSettingsSoon();
        Changed?.Invoke();
    }

    public static string NewId(string prefix = "bp") => prefix + DateTime.Now.Ticks.ToString("x")[^8..] + Interlocked.Increment(ref seq).ToString("x");
    static int seq;

    /// <summary>설정이 통째로 바뀌었을 때 (세팅 가져오기 등) 다시 읽기</summary>
    public static void Reload() { lists.Clear(); loadedFrom = null; Changed?.Invoke(); }
}
