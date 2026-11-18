// 벡터 선 편집 명령 (메뉴·도구 옵션·시험이 같이 씀): 고른 선(벡터 선 고르기)이 있으면 그 선만, 없으면 레이어의 모든 선.
// 모두 VectorLayer.SetStrokes 로 바꿔서 실행취소 한 번에 되돌아가고, 선 목록으로 다시 그림.
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.Modules.VectorTools;

public static class VecEdit
{
    public static Layer VLayer() => App.Doc?.Active is { Kind: "vector" } l ? l : null;
    public static VectorSelectTool SelTool => Registry.Tools.OfType<VectorSelectTool>().FirstOrDefault();

    /// <summary>편집할 선 번호: 벡터 선 고르기로 고른 선 (같은 레이어, 그 뒤로 바뀌지 않았으면), 없으면 전부</summary>
    public static List<int> Targets(Layer l, out bool selected)
    {
        var s = SelTool?.SelectionFor(l);
        selected = s != null && s.Count > 0;
        return selected ? s.OrderBy(i => i).ToList() : Enumerable.Range(0, VectorLayer.Strokes(l).Count).ToList();
    }

    /// <summary>선 목록만 바꿔 끼움 (기록·다시 그리기 없이 — 끄는 중 미리 보기용). 다른 meta 값은 그대로</summary>
    public static void PutStrokes(Layer l, JsonArray strokes)
    {
        var m = new JsonObject();
        if (l.Meta != null) foreach (var (k, v) in l.Meta) if (k != "strokes") m[k] = v?.DeepClone();
        m["strokes"] = strokes;
        l.Meta = m;
    }

    public static List<JsonObject> Copy(Layer l) => VectorLayer.Strokes(l).Select(n => (JsonObject)n?.DeepClone()).Where(n => n != null).ToList();

    /// <summary>고른 선마다 fn (null = 그대로). 바뀐 선 수</summary>
    public static int Map(Layer l, string label, Func<JsonObject, JsonObject> fn, IList<int> idx = null)
    {
        if (l == null) return 0;
        idx ??= Targets(l, out _);
        var list = Copy(l); int n = 0;
        foreach (int i in idx)
        {
            if (i < 0 || i >= list.Count) continue;
            var r = fn((JsonObject)list[i].DeepClone());
            if (r == null || r.ToJsonString() == list[i].ToJsonString()) continue;
            list[i] = r; n++;
        }
        if (n > 0) Commit(l, list, label, idx);
        return n;
    }

    /// <summary>새 선 목록으로 (실행취소 가능). keepSel: 고른 선을 이 번호들로</summary>
    public static void Commit(Layer l, List<JsonObject> list, string label, IEnumerable<int> keepSel = null)
    {
        bool had = SelTool?.SelectionFor(l)?.Count > 0;
        VectorLayer.SetStrokes(l, new JsonArray(list.Select(s => (JsonNode)s.DeepClone()).ToArray()), label);
        if (had && keepSel != null) SelTool.SetSelection(l, keepSel);
        else SelTool?.Restamp(l);
    }

    // ---- 명령 -------------------------------------------------------------------------------------------
    public static int Simplify(Layer l, double tol) => Map(l, "선 단순화", s => { var p = VecOps.Pts(s); var q = VecOps.Simplify(p, tol, VecOps.SizeOf(s) * 0.5); return q.Length < p.Length ? VecOps.With(s, q) : null; });
    public static int Smooth(Layer l, double strength) => Map(l, "선 부드럽게", s => VecOps.With(s, VecOps.SmoothAuto(VecOps.Pts(s), strength)));
    public static int Taper(Layer l, double inLen, double outLen) { var f = PenFeel.Current(); return Map(l, "선 끝 다듬기", s => VecOps.Taper(s, inLen, outLen, f)); }
    public static int ScaleWidth(Layer l, double k) { var f = PenFeel.Current(); return Map(l, "선 굵기 수정", s => VecOps.ScaleWidthAt(s, _ => k, f)); }
    public static int Recolor(Layer l, string hex) => Map(l, "선 색", s => { s["color"] = hex; return s; });

    /// <summary>선 끝 자동 연결 (dist px 안의 끝끼리). 이은 횟수</summary>
    public static int Connect(Layer l, double dist)
    {
        if (l == null) return 0;
        var idx = Targets(l, out bool selected);
        var list = Copy(l);
        var (o, joins, origin) = VecOps.ConnectEnds(list, idx, dist);
        if (joins == 0) return 0;
        var set = idx.ToHashSet();
        var keep = Enumerable.Range(0, o.Count).Where(j => set.Contains(origin[j])).ToList();
        Commit(l, o, "선 끝 연결", selected ? keep : null);
        return joins;
    }
}
