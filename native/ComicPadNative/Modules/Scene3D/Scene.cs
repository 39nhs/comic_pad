// 3D 장면 (웹 판 ui/scene3d.js 의 scene 객체와 같은 JSON): { camera, style, nodes:[…] }
// 폴더 레이어 meta.scene3d 와 설정 last3D 에 이 JSON 그대로 저장한다 (웹 판과 서로 열 수 있게).
using System.Globalization;
using System.Text.Json.Nodes;
using Pose = ComicPadNative.Modules.Scene3D.Engine3D.Pose;

namespace ComicPadNative.Modules.Scene3D;

/// <summary>JSON 값 읽기 도우미 (숫자 형식이 달라도 안전하게)</summary>
static class J
{
    public static double Num(JsonNode n, double def)
    {
        if (n is not JsonValue v) return def;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : def;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return def;
    }
    public static bool Bool(JsonNode n, bool def) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
    public static string Str(JsonNode n, string def = null) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;
    public static double[] Arr(JsonNode n, params double[] def)
    {
        if (n is not JsonArray a) return (double[])def.Clone();
        var o = (double[])def.Clone();
        for (int i = 0; i < Math.Min(a.Count, o.Length); i++) o[i] = Num(a[i], o[i]);
        return o;
    }
    public static JsonArray ToArr(double[] v) { var a = new JsonArray(); foreach (var x in v) a.Add(JsonValue.Create(x)); return a; }

    public static Pose PoseFrom(JsonNode n)
    {
        var p = new Pose();
        if (n is JsonObject o) foreach (var (k, v) in o) if (v is JsonArray) p[k] = Arr(v, 0, 0, 0);
        return p;
    }
    public static JsonObject PoseTo(Dictionary<string, double[]> p) { var o = new JsonObject(); foreach (var (k, v) in p) o[k] = ToArr(v); return o; }
}

public sealed class Camera3
{
    public double Yaw = 25, Pitch = 10, Dist = 4.2, Fov = 40;
    public double[] Target = { 0, 0.95, 0 };
    public Camera3 Clone() => new() { Yaw = Yaw, Pitch = Pitch, Dist = Dist, Fov = Fov, Target = (double[])Target.Clone() };
    public void Set(Camera3 c) { Yaw = c.Yaw; Pitch = c.Pitch; Dist = c.Dist; Fov = c.Fov; Target = (double[])c.Target.Clone(); }
    public JsonObject ToJson() => new() { ["yaw"] = Yaw, ["pitch"] = Pitch, ["dist"] = Dist, ["target"] = J.ToArr(Target), ["fov"] = Fov };
    public static Camera3 FromJson(JsonNode n)
    {
        var c = new Camera3();
        if (n is not JsonObject) return c;
        c.Yaw = J.Num(n["yaw"], c.Yaw); c.Pitch = J.Num(n["pitch"], c.Pitch); c.Dist = J.Num(n["dist"], c.Dist); c.Fov = J.Num(n["fov"], c.Fov);
        c.Target = J.Arr(n["target"], c.Target);
        return c;
    }
}

/// <summary>만화 스타일 (웹 판 DEFAULT_STYLE)</summary>
public sealed class Style3
{
    public double Levels = 2, Soft = 0.04, Ambient = 0.35, LightAz = 40, LightEl = 45, LineWidth = 3, NThr = 0.75, DThr = 0.08;
    public string ShadowColor = "#9a93b0", LineColor = "#111111";
    public bool Line = true, BgDoc = true, Grid = true;
    public bool OutColor = true, OutShade = true, OutLine = true;
    public bool? OutRulers;                     // 없으면 켬 (웹 판 out.rulers !== false)
    public JsonObject Extra = new(), OutExtra = new();

    public Style3 Clone() { var s = (Style3)MemberwiseClone(); s.Extra = (JsonObject)Extra.DeepClone(); s.OutExtra = (JsonObject)OutExtra.DeepClone(); return s; }

    static readonly HashSet<string> keys = new() { "levels", "soft", "ambient", "lightAz", "lightEl", "shadowColor", "line", "lineWidth", "nThr", "dThr", "lineColor", "out", "bgDoc", "grid" };
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["levels"] = Levels, ["soft"] = Soft, ["ambient"] = Ambient, ["lightAz"] = LightAz, ["lightEl"] = LightEl, ["shadowColor"] = ShadowColor,
            ["line"] = Line, ["lineWidth"] = LineWidth, ["nThr"] = NThr, ["dThr"] = DThr, ["lineColor"] = LineColor,
        };
        var outO = new JsonObject { ["color"] = OutColor, ["shade"] = OutShade, ["line"] = OutLine };
        if (OutRulers != null) outO["rulers"] = OutRulers.Value;
        foreach (var (k, v) in OutExtra) outO[k] = v?.DeepClone();
        o["out"] = outO; o["bgDoc"] = BgDoc; o["grid"] = Grid;
        foreach (var (k, v) in Extra) o[k] = v?.DeepClone();
        return o;
    }
    public static Style3 FromJson(JsonNode n)
    {
        var s = new Style3();
        if (n is not JsonObject o) return s;
        s.Levels = J.Num(o["levels"], s.Levels); s.Soft = J.Num(o["soft"], s.Soft); s.Ambient = J.Num(o["ambient"], s.Ambient);
        s.LightAz = J.Num(o["lightAz"], s.LightAz); s.LightEl = J.Num(o["lightEl"], s.LightEl); s.ShadowColor = J.Str(o["shadowColor"], s.ShadowColor);
        s.Line = J.Bool(o["line"], s.Line); s.LineWidth = J.Num(o["lineWidth"], s.LineWidth); s.NThr = J.Num(o["nThr"], s.NThr); s.DThr = J.Num(o["dThr"], s.DThr);
        s.LineColor = J.Str(o["lineColor"], s.LineColor); s.BgDoc = J.Bool(o["bgDoc"], s.BgDoc); s.Grid = J.Bool(o["grid"], s.Grid);
        if (o["out"] is JsonObject oo)
        {
            s.OutColor = J.Bool(oo["color"], true); s.OutShade = J.Bool(oo["shade"], true); s.OutLine = J.Bool(oo["line"], true);
            if (oo["rulers"] is JsonValue) s.OutRulers = J.Bool(oo["rulers"], true);
            foreach (var (k, v) in oo) if (k is not ("color" or "shade" or "line" or "rulers")) s.OutExtra[k] = v?.DeepClone();
        }
        foreach (var (k, v) in o) if (!keys.Contains(k)) s.Extra[k] = v?.DeepClone();
        return s;
    }
}

/// <summary>불러온 BVH 동작 (노드에 함께 저장)</summary>
public sealed class BvhInfo
{
    public string Name; public List<Pose> Poses = new(); public double Fps = 30; public int Matched; public int At;
    public JsonObject ToJson() => new() { ["name"] = Name, ["poses"] = new JsonArray(Poses.Select(p => (JsonNode)J.PoseTo(p)).ToArray()), ["fps"] = Fps, ["matched"] = Matched, ["at"] = At };
    public static BvhInfo FromJson(JsonNode n)
    {
        if (n is not JsonObject o) return null;
        var b = new BvhInfo { Name = J.Str(o["name"], "BVH"), Fps = J.Num(o["fps"], 30), Matched = (int)J.Num(o["matched"], 0), At = (int)J.Num(o["at"], 0) };
        if (o["poses"] is JsonArray a) foreach (var p in a) b.Poses.Add(J.PoseFrom(p));
        return b.Poses.Count > 0 ? b : null;
    }
}

/// <summary>장면의 물체: mannequin(데생 인형) | hand(손) | prim(기본 도형) | model(3D 모델 파일)</summary>
public sealed class SceneNode
{
    public string Id = Layer3D.Uid("n"), Kind = "prim", Name = "물체", Color = "#d8d8d8", Prim;
    public double[] Pos = new double[3], Rot = new double[3];
    public double[] Scale = { 1, 1, 1 };
    public bool ScaleIsArray;
    public bool Visible = true;
    public bool? Tint;
    public int? Side;
    public Pose Pose = new();
    public Dictionary<string, double> Body;
    public JsonObject Src;                   // 모델 파일 { type:'glb'|'obj', name, data(base64), height? }
    public BvhInfo Bvh;
    public JsonObject Extra = new();
    public List<Part> Parts = new();         // 그릴 조각 (저장 안 함)

    public double S => Scale[0];
    public void SetScale(double v) { Scale = new[] { v, v, v }; ScaleIsArray = false; }

    static readonly HashSet<string> keys = new() { "id", "kind", "name", "pos", "rot", "scale", "color", "visible", "pose", "prim", "side", "tint", "body", "src", "bvh", "parts" };

    public SceneNode CloneNode()
    {
        var c = FromJson(ToJson());
        c.Parts = Parts;
        return c;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = Id, ["kind"] = Kind, ["name"] = Name, ["pos"] = J.ToArr(Pos), ["rot"] = J.ToArr(Rot),
            ["scale"] = ScaleIsArray ? J.ToArr(Scale) : JsonValue.Create(Scale[0]), ["color"] = Color, ["visible"] = Visible, ["pose"] = J.PoseTo(Pose),
        };
        if (Prim != null) o["prim"] = Prim;
        if (Side != null) o["side"] = Side.Value;
        if (Tint != null) o["tint"] = Tint.Value;
        if (Body != null) { var b = new JsonObject(); foreach (var (k, v) in Body) b[k] = v; o["body"] = b; }
        if (Src != null) o["src"] = Src.DeepClone();
        if (Bvh != null) o["bvh"] = Bvh.ToJson();
        foreach (var (k, v) in Extra) o[k] = v?.DeepClone();
        return o;
    }

    public static SceneNode FromJson(JsonNode n)
    {
        var s = new SceneNode();
        if (n is not JsonObject o) return s;
        s.Id = J.Str(o["id"], s.Id); s.Kind = J.Str(o["kind"], s.Kind); s.Name = J.Str(o["name"], s.Name); s.Color = J.Str(o["color"], s.Color);
        s.Pos = J.Arr(o["pos"], 0, 0, 0); s.Rot = J.Arr(o["rot"], 0, 0, 0);
        if (o["scale"] is JsonArray) { s.Scale = J.Arr(o["scale"], 1, 1, 1); s.ScaleIsArray = true; } else s.SetScale(J.Num(o["scale"], 1));
        s.Visible = J.Bool(o["visible"], true);
        s.Pose = J.PoseFrom(o["pose"]);
        s.Prim = J.Str(o["prim"]);
        if (o["side"] is JsonValue) s.Side = J.Num(o["side"], 1) < 0 ? -1 : 1;
        if (o["tint"] is JsonValue) s.Tint = J.Bool(o["tint"], false);
        if (o["body"] is JsonObject b) { s.Body = new(); foreach (var (k, v) in b) s.Body[k] = J.Num(v, 1); }
        if (o["src"] is JsonObject src) s.Src = (JsonObject)src.DeepClone();
        s.Bvh = BvhInfo.FromJson(o["bvh"]);
        foreach (var (k, v) in o) if (!keys.Contains(k)) s.Extra[k] = v?.DeepClone();
        return s;
    }

    /// <summary>그릴 조각 만들기 (도형·모델 파일)</summary>
    public void BuildParts()
    {
        Parts = new();
        if (Kind == "prim")
        {
            var P = Engine3D.PrimById(Prim ?? "box");
            if (P == null) return;
            Parts.Add(new Part { Mesh = P.MakeMesh(), Matrix = M3.Translate(0, P.Y, 0) });
        }
        else if (Kind == "model" && Src != null)
        {
            var bytes = Convert.FromBase64String(J.Str(Src["data"], ""));
            var parts = J.Str(Src["type"]) == "obj" ? Engine3D.ParseOBJ(System.Text.Encoding.UTF8.GetString(bytes)) : Engine3D.ParseGLTF(bytes);
            Parts = Engine3D.NormalizeParts(parts, J.Num(Src["height"], 1.7));
        }
    }
}

public sealed class Scene3
{
    public Camera3 Camera = new();
    public Style3 Style = new();
    public List<SceneNode> Nodes = new();

    public JsonObject ToJson() => new() { ["camera"] = Camera.ToJson(), ["style"] = Style.ToJson(), ["nodes"] = new JsonArray(Nodes.Select(n => (JsonNode)n.ToJson()).ToArray()) };

    /// <summary>JSON → 장면 (모델 복원 실패한 것은 빼고 알림)</summary>
    public static Scene3 FromJson(JsonNode json, Action<string> warn = null)
    {
        var s = new Scene3 { Camera = Camera3.FromJson(json?["camera"]), Style = Style3.FromJson(json?["style"]) };
        if (json?["nodes"] is JsonArray a)
            foreach (var n in a)
            {
                try { var nd = SceneNode.FromJson(n); nd.BuildParts(); s.Nodes.Add(nd); }
                catch { warn?.Invoke($"3D 모델 복원 실패: {J.Str(n?["name"], "물체")}"); }
            }
        return s;
    }

    /// <summary>설정 파일에는 큰 모델 데이터를 넣지 않는다 (인형·도형·카메라·스타일만)</summary>
    public static JsonObject Slim(JsonObject j)
    {
        var o = (JsonObject)j.DeepClone();
        if (o["nodes"] is JsonArray a) o["nodes"] = new JsonArray(a.Where(n => J.Str(n?["kind"]) != "model").Select(n => n.DeepClone()).ToArray());
        return o;
    }
}

static class Layer3D
{
    static int seq;
    /// <summary>웹 판 uid(prefix) 와 비슷한 짧은 id</summary>
    public static string Uid(string p) => p + DateTime.Now.Ticks.ToString("x")[^7..] + Interlocked.Increment(ref seq).ToString("x");
}
