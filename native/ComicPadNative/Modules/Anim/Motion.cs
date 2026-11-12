// 모션 경로 (트윈) — 웹 판 app/js/core/motion.js
// 트랙마다 위치·크기·회전·불투명도 키를 찍으면 사이 프레임은 이징을 따라 자동으로 움직인다.
// 그림은 그대로 두고 그릴 때만 움직인다 (비파괴). "그림으로 굳히기" 로 프레임마다 그림을 만들 수 있다.
// track.meta.motion = { ax, ay, keys: [{ f, x, y, s, r, o, ease, bz }] }
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public struct MoValues { public double X, Y, S, R, O; public static MoValues Id => new() { S = 1, O = 1 }; }

public static class Motion
{
    public static readonly (string id, string label)[] Eases =
    {
        ("linear", "일정하게"), ("inout", "천천히 출발·정지"), ("in", "천천히 출발"), ("out", "천천히 정지"), ("smooth", "부드럽게 (smoothstep)"),
        ("back", "살짝 넘쳤다 돌아옴"), ("bounce", "통통 튐"), ("hold", "멈춰 있다 뚝 바뀜"), ("bezier", "직접 곡선 (베지어)"),
    };

    /// <summary>0~1 → 0~1 이징</summary>
    public static double Ease(string name, double t, double[] bz = null)
    {
        t = Math.Clamp(t, 0, 1);
        switch (name)
        {
            case "in": return t * t * t;
            case "out": return 1 - Math.Pow(1 - t, 3);
            case "inout": return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            case "smooth": return t * t * (3 - 2 * t);
            case "back": { const double c = 1.70158, c3 = c + 1; return 1 + c3 * Math.Pow(t - 1, 3) + c * Math.Pow(t - 1, 2); }
            case "bounce":
            {
                const double n = 7.5625, d = 2.75;
                if (t < 1 / d) return n * t * t;
                if (t < 2 / d) { t -= 1.5 / d; return n * t * t + 0.75; }
                if (t < 2.5 / d) { t -= 2.25 / d; return n * t * t + 0.9375; }
                t -= 2.625 / d; return n * t * t + 0.984375;
            }
            case "hold": return t < 1 ? 0 : 1;
            case "bezier": return CubicBezier(bz ?? new[] { 0.42, 0, 0.58, 1 }, t);
            default: return t;
        }
    }

    /// <summary>CSS cubic-bezier(x1,y1,x2,y2) 의 x → y</summary>
    public static double CubicBezier(double[] p, double x)
    {
        double x1 = p[0], y1 = p[1], x2 = p[2], y2 = p[3];
        double Bx(double u) => 3 * x1 * u * (1 - u) * (1 - u) + 3 * x2 * u * u * (1 - u) + u * u * u;
        double By(double u) => 3 * y1 * u * (1 - u) * (1 - u) + 3 * y2 * u * u * (1 - u) + u * u * u;
        double lo = 0, hi = 1, uu = x;
        for (int i = 0; i < 30; i++) { uu = (lo + hi) / 2; if (Bx(uu) < x) lo = uu; else hi = uu; }
        return By(uu);
    }

    public static JsonObject Of(Layer track) => track?.Meta?["motion"] as JsonObject;
    public static List<JsonObject> Keys(Layer track) => (Of(track)?["keys"] as JsonArray)?.OfType<JsonObject>().ToList();
    public static (double x, double y) Anchor(Layer track, Document d)
    {
        var m = Of(track);
        return (J.N(m?["ax"]) ?? d.W / 2.0, J.N(m?["ay"]) ?? d.H / 2.0);
    }

    static MoValues Pick(JsonObject q) => new() { X = J.D(q["x"], 0), Y = J.D(q["y"], 0), S = J.D(q["s"], 1), R = J.D(q["r"], 0), O = J.D(q["o"], 1) };
    static double[] Bz(JsonObject q) => q["bz"] is JsonArray a && a.Count >= 4 ? a.Take(4).Select(n => J.D(n)).ToArray() : null;

    /// <summary>프레임 f 의 값 (키가 없으면 null)</summary>
    public static MoValues? ValuesAt(Layer track, int f)
    {
        var keys = Keys(track);
        if (keys == null || keys.Count == 0) return null;
        var k = keys.OrderBy(q => J.D(q["f"])).ToList();
        if (f <= J.D(k[0]["f"])) return Pick(k[0]);
        if (f >= J.D(k[^1]["f"])) return Pick(k[^1]);
        int i = k.FindIndex(q => J.D(q["f"]) > f);
        var A = k[i - 1]; var B = k[i];
        double af = J.D(A["f"]), bf = J.D(B["f"]);
        double t = Ease(J.S(A["ease"]) ?? "inout", (f - af) / (bf - af), Bz(A));
        var a = Pick(A); var b = Pick(B);
        return new MoValues { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t, S = a.S + (b.S - a.S) * t, R = a.R + (b.R - a.R) * t, O = a.O + (b.O - a.O) * t };
    }

    public readonly record struct Mo(Affine T, double Alpha, MoValues V);

    /// <summary>문서 좌표에서의 변환 (그리기용). 아무 변화가 없으면 null</summary>
    public static Mo? At(Layer track, int f, Document d)
    {
        var vv = ValuesAt(track, f);
        if (vv == null) return null;
        var v = vv.Value;
        if (Math.Abs(v.X) < 1e-3 && Math.Abs(v.Y) < 1e-3 && Math.Abs(v.S - 1) < 1e-4 && Math.Abs(v.R) < 1e-3 && Math.Abs(v.O - 1) < 1e-4) return null;
        var (ax, ay) = Anchor(track, d);
        var T = Affine.Translate(ax + v.X, ay + v.Y).Mul(Affine.Rotate(v.R)).Mul(Affine.Scale(v.S)).Mul(Affine.Translate(-ax, -ay));
        return new Mo(T, Math.Clamp(v.O, 0, 1), v);
    }

    // ---- 키 고치기 (실행취소: 트랙 meta 는 레이어 속성이라 History.Struct 로) ----
    public static void SetMotion(Layer track, JsonObject next, string label = "모션 키")
    {
        var d = App.Doc;
        App.History.Struct(d, label, () => Apply(track, next));
        AnimCore.Refresh(true);
    }

    static void Apply(Layer track, JsonObject v)
    {
        track.Meta ??= new JsonObject();
        if ((v?["keys"] is JsonArray ks && ks.Count > 0) || J.N(v?["ax"]) != null) track.Meta["motion"] = v.DeepClone();
        else track.Meta.Remove("motion");
    }

    /// <summary>프레임 f 에 키 넣기 (값을 주지 않으면 지금 보이는 값 그대로)</summary>
    public static void SetKey(Layer track, int f, JsonObject vals = null, string label = "모션 키")
    {
        var m = (JsonObject)Of(track)?.DeepClone() ?? new JsonObject { ["keys"] = new JsonArray() };
        var cur = ValuesAt(track, f) ?? MoValues.Id;
        var keys = J.Arr(m, "keys");
        var k = keys.OfType<JsonObject>().FirstOrDefault(q => J.I(q["f"]) == f);
        if (k == null)
        {
            k = NewKey(f, cur);
            keys.Add(k);
        }
        if (vals != null) foreach (var (kk, vv) in vals) k[kk] = vv?.DeepClone();
        Sort(keys);
        SetMotion(track, m, label);
    }

    public static JsonObject NewKey(int f, MoValues cur) => new()
    {
        ["f"] = f, ["x"] = Math.Round(cur.X, 3), ["y"] = Math.Round(cur.Y, 3), ["s"] = Math.Round(cur.S, 5), ["r"] = Math.Round(cur.R, 3), ["o"] = Math.Round(cur.O, 4), ["ease"] = "inout",
    };

    public static void Sort(JsonArray keys)
    {
        var list = keys.OfType<JsonObject>().OrderBy(q => J.D(q["f"])).Select(q => (JsonNode)q.DeepClone()).ToList();
        keys.Clear();
        foreach (var q in list) keys.Add(q);
    }

    public static void RemoveKey(Layer track, int f)
    {
        var m = (JsonObject)Of(track)?.DeepClone();
        if (m == null) return;
        var keys = J.Arr(m, "keys");
        foreach (var q in keys.OfType<JsonObject>().Where(q => J.I(q["f"]) == f).ToList()) keys.Remove(q);
        SetMotion(track, m, "모션 키 지우기");
    }

    /// <summary>키 프레임 번호 밀기 (프레임 넣기·지우기와 같이)</summary>
    public static void ShiftKeys(Document d, int at, int delta)
    {
        foreach (var t in d.Layers)
        {
            if (Of(t)?["keys"] is not JsonArray ks) continue;
            foreach (var k in ks.OfType<JsonObject>()) { int f = J.I(k["f"]); if (f >= at) k["f"] = Math.Max(at, f + delta); }
        }
    }

    /// <summary>그림의 불투명한 곳 가운데 (기준점용)</summary>
    public static (double x, double y)? OpaqueCenter(Layer l)
    {
        var b = l?.Px?.Bounds() ?? Rect.Empty;
        if (b.IsEmpty) return null;
        return ((b.X + b.R - 1) / 2.0, (b.Y + b.B - 1) / 2.0);
    }
}
