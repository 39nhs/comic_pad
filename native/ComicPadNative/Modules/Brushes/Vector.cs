// 벡터 레이어 (웹 core/vector.js): 펜으로 그린 선을 점 목록으로 기억해 두고 언제든 다시 그린다.
// meta = { strokes: [ { pts:[x,y,p,t, x,y,p,t, …], preset:{size,…}, color:"#rrggbb" } ] } — 웹과 같은 모양 (.cpad 오가기)
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.BrushEngine;

public static class VectorLayer
{
    static readonly string[] SnapKeys = { "size", "opacity", "flow", "hardness", "spacing", "pressureSize", "minSize", "pressureOpacity", "minOpacity", "grain", "buildup", "tip", "taperIn", "taperOut", "blend", "speedThin", "density", "particle", "flatRatio", "angle", "texture", "textureStrength", "textureScale" };

    /// <summary>브러시 설정 중 다시 그릴 때 필요한 것만</summary>
    public static JsonObject PresetSnapshot(BrushPreset p)
    {
        var all = p.ToJson(); var o = new JsonObject();
        foreach (var k in SnapKeys) if (all.TryGetPropertyValue(k, out var v)) o[k] = v?.DeepClone();
        if ((string)o["tip"] == "image") o["tip"] = "round";
        return o;
    }

    public static JsonArray Strokes(Layer l) => l?.Meta?["strokes"] as JsonArray ?? new JsonArray();

    /// <summary>선 하나를 레이어에 그리기 (보정 없이, 대칭·선택 영역 없이)</summary>
    public static Rect DrawStroke(Layer layer, Document doc, JsonObject s, PenFeel feel = null)
    {
        var pre = s["preset"] as JsonObject ?? new JsonObject();
        var p = BrushPreset.FromJson(pre); p.Stabilizer = 0;
        var st = new BrushStroke(new StrokeSetup { W = doc.W, H = doc.H, Preset = p, Color = Px.FromHex((string)s["color"], 0xFF000000), Feel = feel, Seed = 1 });
        var list = new List<BPt>();
        if (s["pts"] is JsonArray a)
            for (int i = 0; i + 3 < a.Count; i += 4)
                list.Add(new BPt(BrushPreset.Num(a[i]) ?? 0, BrushPreset.Num(a[i + 1]) ?? 0, BrushPreset.Num(a[i + 2]) ?? 1, BrushPreset.Num(a[i + 3]) ?? 0));
        if (list.Count == 0) return Rect.Empty;
        var r = st.Replay(list).Clip(doc.W, doc.H);
        if (r.IsEmpty) return r;
        new StrokeApply
        {
            Buf = st.Buf, Target = layer.Px, AlphaLock = layer.AlphaLock, Mode = Px.Parse(pre["blend"] is JsonNode b ? (string)b : "source-over"),
            Opacity = BrushPreset.Num(pre["opacity"]) ?? 1, Post = p.HasPost ? p : null, Dpi = doc.Dpi,
        }.Apply(r);
        return r;
    }

    /// <summary>레이어 전체를 선 목록으로부터 다시 그림</summary>
    public static void Render(Layer layer, Document doc)
    {
        if (layer?.Px == null) return;
        layer.Px.Clear();
        var feel = PenFeel.Current();
        foreach (var n in Strokes(layer)) if (n is JsonObject s) DrawStroke(layer, doc, s, feel);
        layer.Touch();
    }

    static void SetMeta(Layer l, JsonArray strokes)
    {
        var m = l.Meta != null ? (JsonObject)l.Meta.DeepClone() : new JsonObject();
        m["strokes"] = strokes;
        l.Meta = m;
    }

    /// <summary>선 목록 바꾸기 (실행취소 가능)</summary>
    public static void SetStrokes(Layer layer, JsonArray strokes, string label = "벡터 편집", JsonArray before = null)
    {
        var doc = App.Doc;
        var prev = (JsonArray)(before ?? Strokes(layer)).DeepClone();
        var next = (JsonArray)strokes.DeepClone();
        SetMeta(layer, (JsonArray)next.DeepClone());
        Render(layer, doc);
        App.History.Push(new VectorEdit(label, layer, prev, next, true));
        doc.Modified = true;
        App.Invalidate(new Rect(0, 0, doc.W, doc.H));
        App.NotifyLayers();
    }

    /// <summary>새 선 추가 (이미 화면에 그려져 있으므로 다시 그리지 않고 기록만)</summary>
    public static void AddStroke(Layer layer, JsonObject stroke)
    {
        var prev = (JsonArray)Strokes(layer).DeepClone();
        var next = (JsonArray)prev.DeepClone(); next.Add(stroke);
        SetMeta(layer, (JsonArray)next.DeepClone());
        App.History.Push(new VectorEdit("벡터 선", layer, prev, next, true));
    }

    static (double x0, double y0, double x1, double y1) Bounds(JsonObject s)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        var p = Pts(s);
        for (int i = 0; i + 1 < p.Length; i += 4) { x0 = Math.Min(x0, p[i]); x1 = Math.Max(x1, p[i]); y0 = Math.Min(y0, p[i + 1]); y1 = Math.Max(y1, p[i + 1]); }
        double pad = SizeOf(s) / 2;
        return (x0 - pad, y0 - pad, x1 + pad, y1 + pad);
    }
    static bool Overlap((double x0, double y0, double x1, double y1) a, (double x0, double y0, double x1, double y1) b) => a.x0 <= b.x1 && b.x0 <= a.x1 && a.y0 <= b.y1 && b.y0 <= a.y1;

    public static double[] Pts(JsonObject s) => s["pts"] is JsonArray a ? a.Select(v => BrushPreset.Num(v) ?? 0).ToArray() : Array.Empty<double>();
    static double SizeOf(JsonObject s) { var v = BrushPreset.Num(s["preset"]?["size"]); return v is double d && d != 0 ? d : 4; }
    static JsonArray ToArr(IEnumerable<double> v) => new(v.Select(x => (JsonNode)JsonValue.Create(Math.Round(x, 3))).ToArray());
    static JsonObject With(JsonObject s, IEnumerable<double> pts) { var c = (JsonObject)s.DeepClone(); c["pts"] = ToArr(pts); return c; }

    /// <summary>벡터 지우개: 원(x, y, r) 안의 점을 지우고 선을 나눈다. whole = 닿은 선 전체. 바뀌면 true</summary>
    public static bool EraseAt(Layer layer, double x, double y, double r, bool whole)
    {
        var strokes = Strokes(layer);
        var o = new JsonArray();
        bool changed = false;
        foreach (var n in strokes)
        {
            if (n is not JsonObject s) continue;
            var p = Pts(s);
            bool pSize = s["preset"]?["pressureSize"] is JsonNode ps && BrushPreset.Bool(ps);
            double sz = SizeOf(s);
            var hit = new List<bool>(); bool any = false;
            for (int i = 0; i + 3 < p.Length; i += 4)
            {
                double pr = sz * 0.5 * (pSize ? Math.Max(0.3, p[i + 2]) : 1);
                double dx = p[i] - x, dy = p[i + 1] - y;
                bool h = dx * dx + dy * dy <= (r + pr) * (r + pr);
                hit.Add(h); if (h) any = true;
            }
            if (!any) { o.Add(s.DeepClone()); continue; }
            changed = true;
            if (whole) continue;
            var run = new List<double>();
            void Flush() { if (run.Count >= 8) o.Add(With(s, run)); run = new List<double>(); }
            for (int k = 0; k < hit.Count; k++)
            {
                if (hit[k]) Flush();
                else run.AddRange(new[] { p[k * 4], p[k * 4 + 1], p[k * 4 + 2], p[k * 4 + 3] });
            }
            Flush();
        }
        if (changed) SetMeta(layer, o);
        return changed;
    }

    /// <summary>(x, y) 에서 tol 안에 있는 선: (번호, 위치(점 번호 + 비율), 거리) — 위에 그린 선부터</summary>
    public static (int index, double seg, double d)? StrokeAt(Layer layer, double x, double y, double tol)
    {
        var list = Strokes(layer);
        for (int k = list.Count - 1; k >= 0; k--)
        {
            if (list[k] is not JsonObject s) continue;
            var b = Bounds(s); double r = tol + SizeOf(s) / 2;
            if (x < b.x0 - tol || x > b.x1 + tol || y < b.y0 - tol || y > b.y1 + tol) continue;
            var p = Pts(s);
            (int, double, double)? best = null;
            for (int i = 0; i + 4 < p.Length; i += 4)
            {
                double ax = p[i], ay = p[i + 1], bx = p[i + 4], by = p[i + 5];
                double dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
                double t = L > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / L, 0, 1) : 0;
                double d = Math.Sqrt((ax + dx * t - x) * (ax + dx * t - x) + (ay + dy * t - y) * (ay + dy * t - y));
                if (d <= r && (best == null || d < best.Value.Item3)) best = (k, i / 4 + t, d);
            }
            if (best != null) return best;
        }
        return null;
    }

    static double? SegHit(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        double rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-9) return null;
        double t = ((cx - ax) * sy - (cy - ay) * sx) / den, u = ((cx - ax) * ry - (cy - ay) * rx) / den;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? t : null;
    }

    static double[] PointAt(double[] p, double pos)
    {
        int i = Math.Min((int)Math.Floor(pos), p.Length / 4 - 2); double t = pos - i; int a = i * 4, b = a + 4;
        return new[] { p[a] + (p[b] - p[a]) * t, p[a + 1] + (p[b + 1] - p[a + 1]) * t, p[a + 2] + (p[b + 2] - p[a + 2]) * t, p[a + 3] + (p[b + 3] - p[a + 3]) * t };
    }
    static List<double> Slice(double[] p, double from, double to)
    {
        var o = new List<double>(PointAt(p, from));
        for (int i = (int)Math.Ceiling(from + 1e-6); i < to; i++) o.AddRange(new[] { p[i * 4], p[i * 4 + 1], p[i * 4 + 2], p[i * 4 + 3] });
        o.AddRange(PointAt(p, to));
        return o;
    }

    /// <summary>교차점까지 지우기: 닿은 선에서 양옆으로 가장 가까운 다른 선과의 교차점 사이만 지운다. 바뀌면 true</summary>
    public static bool EraseToCross(Layer layer, double x, double y, double r)
    {
        var list = Strokes(layer);
        var hit = StrokeAt(layer, x, y, r);
        if (hit == null) return false;
        var s = (JsonObject)list[hit.Value.index];
        var p = Pts(s); int n = p.Length / 4;
        if (n < 2) return false;
        var cuts = new List<double>();
        var sb = Bounds(s);
        foreach (var on in list)
        {
            if (on is not JsonObject o) continue;
            bool self = ReferenceEquals(o, s);
            if (self && n < 3) continue;
            if (!Overlap(sb, Bounds(o))) continue;
            var q = Pts(o);
            for (int i = 0; i + 4 < p.Length; i += 4)
                for (int j = 0; j + 4 < q.Length; j += 4)
                {
                    if (self && Math.Abs(i - j) <= 4) continue;       // 자기 자신과는 붙어 있는 선분끼리 빼고
                    var t = SegHit(p[i], p[i + 1], p[i + 4], p[i + 5], q[j], q[j + 1], q[j + 4], q[j + 5]);
                    if (t != null) cuts.Add(i / 4 + t.Value);
                }
        }
        const double eps = 0.02;
        double seg = hit.Value.seg;
        double left = cuts.Where(c => c < seg - eps).DefaultIfEmpty(-1).Max(); left = Math.Max(-1, left);
        double right = cuts.Where(c => c > seg + eps).DefaultIfEmpty(double.PositiveInfinity).Min();
        var pieces = new List<JsonObject>();
        if (left > 0) pieces.Add(With(s, Slice(p, 0, left)));
        if (right < n - 1) pieces.Add(With(s, Slice(p, right, n - 1)));
        var next = new JsonArray();
        for (int k = 0; k < list.Count; k++)
        {
            if (k == hit.Value.index) { foreach (var pc in pieces) if (((JsonArray)pc["pts"]).Count >= 8) next.Add(pc); }
            else next.Add(list[k]?.DeepClone());
        }
        SetMeta(layer, next);
        return true;
    }

    /// <summary>모든 선 굵기에 배율 곱하기</summary>
    public static void ScaleWidths(Layer layer, double k)
    {
        var next = new JsonArray();
        foreach (var n in Strokes(layer))
        {
            if (n is not JsonObject s) continue;
            var c = (JsonObject)s.DeepClone();
            var pre = c["preset"] as JsonObject ?? new JsonObject(); c["preset"] = pre;
            pre["size"] = Math.Max(0.3, (BrushPreset.Num(pre["size"]) ?? 4) * k);
            next.Add(c);
        }
        SetStrokes(layer, next, "선 굵기 일괄 조정");
    }

    /// <summary>선 색 바꾸기 (전부)</summary>
    public static void Recolor(Layer layer, string color)
    {
        var next = new JsonArray();
        foreach (var n in Strokes(layer)) if (n is JsonObject s) { var c = (JsonObject)s.DeepClone(); c["color"] = color; next.Add(c); }
        SetStrokes(layer, next, "선 색 바꾸기");
    }
}

/// <summary>벡터 선 목록 바꾸기 실행취소 (되돌리면 선 목록으로 다시 그림)</summary>
public sealed class VectorEdit : IEdit
{
    readonly Layer layer; readonly JsonArray before, after; readonly bool rerender;
    public string Label { get; }
    public VectorEdit(string label, Layer l, JsonArray b, JsonArray a, bool rerender) { Label = label; layer = l; before = b; after = a; this.rerender = rerender; }
    public long Bytes => (before.ToJsonString().Length + after.ToJsonString().Length) * 2L;
    public Rect Undo(Document d) => Put(d, before);
    public Rect Redo(Document d) => Put(d, after);
    Rect Put(Document d, JsonArray s)
    {
        var m = layer.Meta != null ? (JsonObject)layer.Meta.DeepClone() : new JsonObject();
        m["strokes"] = s.DeepClone();
        layer.Meta = m;
        if (rerender) VectorLayer.Render(layer, d);
        return new Rect(0, 0, d.W, d.H);
    }
}
