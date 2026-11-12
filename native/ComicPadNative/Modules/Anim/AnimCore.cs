// 애니메이션 (타임라인) — 웹 판 app/js/core/anim.js 와 같은 규칙·같은 .cpad 형식
// - 트랙 = "애니메이션 폴더" 하나 (meta.anim = true). 폴더 안 레이어 하나하나가 그림 칸(셀).
// - 전체 프레임 0..T-1 이 공통 시간. 트랙마다 meta.xs[프레임] = 그 프레임에 보일 셀 id (null = 빈 칸).
//   같은 셀이 이어지면 "길게 보여 주기", 떨어진 프레임에 같은 셀이 다시 나오면 "연결된 칸"(하나 고치면 같이 바뀜).
// - 프레임마다 길이(ms)를 따로 줄 수 있고(doc.anim.ms), 태그(구간 이름 + 정방향/역방향/핑퐁)로 구간 재생·내보내기.
// - doc.Extra["anim"] = { fps, frame, onion, onionOpacity, onionPrev, onionNext, onionFalloff, onionPrevColor, onionNextColor,
//   loop, dir, length, ms[], tags[], playTag, light{ids,opacity,color}, camera{keys,ease}, audio{name,data,offset,volume}, lipTrack, holds, v }
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public static class AnimCore
{
    /// <summary>노출표·프레임·태그 등이 바뀜 (패널 다시 그리기)</summary>
    public static event Action Changed;
    /// <summary>재생 중 (저장 안 함 — 웹 doc.anim.playing)</summary>
    public static bool Playing;

    public static void Emit() => Changed?.Invoke();
    /// <summary>바뀐 것 알리고 화면 다시 그리기</summary>
    public static void Refresh(bool layers = false)
    {
        if (layers) App.NotifyLayers();
        App.InvalidateAll();
        Emit();
    }

    // ---------------------------------------------------------------------
    // 트랙 · 셀 · 노출표
    // ---------------------------------------------------------------------
    public static JsonObject A(Document d) => d.Anim;
    public static bool IsTrack(Layer l) => l != null && l.IsFolder && J.T(l.Meta?["anim"]);
    public static List<Layer> Tracks(Document d) => d == null ? new() : d.Layers.Where(IsTrack).ToList();

    /// <summary>지금 고른 레이어가 속한 트랙 (없으면 맨 위 트랙)</summary>
    public static Layer AnimFolder(Document d)
    {
        if (d == null) return null;
        var act = d.Active;
        if (act != null)
        {
            if (IsTrack(act)) return act;
            var f = d.ById(d.ParentOf(act));
            if (IsTrack(f)) return f;
        }
        return Tracks(d).LastOrDefault();
    }

    /// <summary>트랙의 노출표. 예전 파일(노출표 없음)은 폴더 순서 + 프레임 길이(holds)로 만들어 씀</summary>
    public static List<string> XsOf(Layer track, Document d)
    {
        var kids = d.ChildrenOf(track);
        var ids = new HashSet<string>(kids.Select(k => k.Id));
        if (track.Meta?["xs"] is JsonArray arr)
            return arr.Select(n => { var s = J.S(n); return s != null && ids.Contains(s) ? s : null; }).ToList();
        var outp = new List<string>();
        var holds = A(d)["holds"] as JsonObject;
        foreach (var k in kids) for (int r = Math.Max(1, (int)Math.Round(J.D(holds?[k.Id], 1))); r > 0; r--) outp.Add(k.Id);
        return outp;
    }

    /// <summary>전체 프레임 수</summary>
    public static int FrameCount(Document d)
    {
        if (d == null) return 0;
        int n = Math.Max(0, J.I(A(d)["length"], 0));
        foreach (var t in Tracks(d)) n = Math.Max(n, XsOf(t, d).Count);
        return n;
    }

    public static int Frame(Document d) => d == null ? 0 : Math.Max(0, J.I(A(d)["frame"], 0));
    public static double Fps(Document d) { double f = J.D(A(d)["fps"], 12); return f > 0 ? f : 12; }
    public static bool Loop(Document d) => J.T(A(d)["loop"]);

    /// <summary>프레임 f 에서 트랙에 보일 셀</summary>
    public static Layer CelAt(Layer track, int f, Document d)
    {
        var xs = XsOf(track, d);
        return f >= 0 && f < xs.Count && xs[f] != null ? d.ById(xs[f]) : null;
    }

    /// <summary>프레임 길이 (ms): 따로 정했으면 그것, 아니면 1/fps</summary>
    public static double FrameMs(int f, Document d)
    {
        var ms = A(d)["ms"] as JsonArray;
        double v = ms != null && f >= 0 && f < ms.Count ? J.D(ms[f], 0) : 0;
        return v > 0 ? v : 1000 / Fps(d);
    }

    /// <summary>각 프레임 시작 시각(초), 마지막은 전체 길이</summary>
    public static double[] Timeline(Document d)
    {
        int n = FrameCount(d);
        var o = new double[n + 1];
        double t = 0;
        for (int f = 0; f < n; f++) { o[f] = t; t += FrameMs(f, d) / 1000; }
        o[n] = t;
        return o;
    }

    public static JsonArray Tags(Document d) => A(d)["tags"] as JsonArray ?? new JsonArray();
    public static int? PlayTag(Document d) { var n = J.N(A(d)["playTag"]); return n == null ? null : (int)n; }

    // ---------------------------------------------------------------------
    // 노출표 고치기 (실행취소 포함) — 웹 editAnim
    // ---------------------------------------------------------------------
    /// <summary>편집 중 트랙별 노출표 (List 로 고친 뒤 끝에 meta.xs 로 돌려 씀)</summary>
    public sealed class Tx
    {
        public readonly Document D;
        readonly Dictionary<Layer, List<string>> map = new();
        public Tx(Document d) { D = d; }
        public List<string> Xs(Layer t)
        {
            if (!map.TryGetValue(t, out var l)) { l = XsOf(t, D); map[t] = l; }
            return l;
        }
        public string At(Layer t, int f) { var xs = Xs(t); return f >= 0 && f < xs.Count ? xs[f] : null; }
        public Layer CelAt(Layer t, int f) { var id = At(t, f); return id == null ? null : D.ById(id); }
        public JsonObject Anim => D.Anim;
    }

    internal sealed record Snap(StructSnap S, JsonObject Anim);
    static Snap Take(Document d) => new(StructSnap.Take(d), (JsonObject)d.Anim.DeepClone());

    /// <summary>노출표·태그·길이·레이어 구조를 함께 되돌리는 실행취소 기록</summary>
    public sealed class AnimEdit : IEdit
    {
        readonly Snap before, after;
        public string Label { get; }
        internal AnimEdit(string label, Snap b, Snap a) { Label = label; before = b; after = a; }
        public long Bytes => 8192;
        public Rect Undo(Document d) { Put(d, before); return new Rect(0, 0, d.W, d.H); }
        public Rect Redo(Document d) { Put(d, after); return new Rect(0, 0, d.W, d.H); }
        static void Put(Document d, Snap s)
        {
            s.S.Apply(d);
            d.Extra["anim"] = s.Anim.DeepClone();
            if (App.Main != null) App.Main.BeginInvoke(new Action(() => Emit()));
        }
    }

    /// <summary>슬라이더처럼 끄는 동안 값을 바로 바꾸고, 뗄 때 실행취소 한 번으로 남길 때: Begin → (바꾸기) → Commit</summary>
    public sealed class Snap0 { internal Snap S; }
    public static Snap0 Begin(Document d) => new() { S = Take(d) };
    public static void Commit(Document d, string label, Snap0 s)
    {
        d.Modified = true;
        App.History.Push(new AnimEdit(label, s.S, Take(d)));
        Refresh();
    }

    /// <summary>doc.anim 의 값만 바꾸는 작업을 실행취소 가능하게 (카메라 키·소리·라이트 테이블 등)</summary>
    public static void EditAnimOnly(string label, Action<JsonObject> fn)
    {
        var d = App.Doc;
        if (d == null) return;
        var before = Take(d);
        fn(d.Anim);
        d.Modified = true;
        App.History.Push(new AnimEdit(label, before, Take(d)));
        Refresh();
    }

    /// <summary>레이어 구조(셀 추가·삭제) + 노출표 + 태그·길이를 실행취소 한 번으로</summary>
    public static T Edit<T>(string label, Func<Tx, T> fn)
    {
        var d = App.Doc;
        var before = Take(d);
        var tx = new Tx(d);
        // 예전 파일이면 노출표를 지금 모양 그대로 굳혀 둔다
        foreach (var t in Tracks(d)) tx.Xs(t);
        A(d)["holds"] = new JsonObject();
        T r = fn(tx);
        Normalize(d, tx);
        d.Modified = true;
        App.History.Push(new AnimEdit(label, before, Take(d)));
        Refresh(true);
        return r;
    }
    public static void Edit(string label, Action<Tx> fn) => Edit<bool>(label, tx => { fn(tx); return true; });

    /// <summary>길이 정리: 트랙 노출표를 전체 길이에 맞추고 meta.xs 로 씀</summary>
    static void Normalize(Document d, Tx tx)
    {
        var a = A(d);
        var ts = Tracks(d);
        int n = Math.Max(0, J.I(a["length"], 0));
        foreach (var t in ts) n = Math.Max(n, tx.Xs(t).Count);
        a["length"] = n;
        foreach (var t in ts)
        {
            var xs = tx.Xs(t);
            while (xs.Count < n) xs.Add(null);
            if (xs.Count > n) xs.RemoveRange(n, xs.Count - n);
            t.Meta ??= new JsonObject();
            t.Meta["xs"] = J.StrArray(xs);
        }
        if (a["ms"] is JsonArray ms) while (ms.Count > n) ms.RemoveAt(ms.Count - 1);
        a["frame"] = Math.Max(0, Math.Min(J.I(a["frame"], 0), n - 1));
    }

    /// <summary>더 이상 어느 프레임에도 안 쓰이는 셀 레이어 지우기 (라이트 테이블에 올린 것은 남김)</summary>
    static void DropUnused(Tx tx)
    {
        var d = tx.D;
        var used = new HashSet<string>(Tracks(d).SelectMany(t => tx.Xs(t)).Where(x => x != null));
        var light = LightIds(d);
        foreach (var t in Tracks(d))
            foreach (var k in d.ChildrenOf(t))
                if (!used.Contains(k.Id) && !light.Contains(k.Id))
                {
                    var gone = new List<Layer> { k };
                    if (k.IsFolder) gone.AddRange(d.Layers.Where(x => d.IsInside(x, k)));
                    foreach (var g in gone) d.Layers.Remove(g);
                }
        if (d.ById(d.ActiveId) == null) d.ActiveId = d.Layers.LastOrDefault()?.Id;
    }

    static string CelName(Layer track, Document d) => (d.ChildrenOf(track).Count + 1).ToString();

    /// <summary>트랙 안에 새 셀 (from 을 주면 복제). 트랙의 마지막 셀 바로 위에 넣음</summary>
    public static Layer NewCel(Layer track, Document d, Layer from = null)
    {
        var kids = d.ChildrenOf(track);
        int at = kids.Count > 0 ? d.Layers.IndexOf(kids[^1]) + 1 : d.Layers.IndexOf(track);
        string name = CelName(track, d);
        var l = from != null ? from.Clone(d, name) : new Layer(d, name);
        l.Parent = track.Id;
        d.Layers.Insert(Math.Max(0, at), l);
        return l;
    }

    /// <summary>빈 트랙 (맨 위에)</summary>
    public static Layer NewTrackRaw(Document d, string name, int frames = 0)
    {
        var f = new Layer(d, name, "folder") { Parent = null, Meta = new JsonObject { ["anim"] = true, ["xs"] = J.StrArray(Enumerable.Repeat<string>(null, frames)) } };
        d.Layers.Add(f);
        return f;
    }

    // ---------------------------------------------------------------------
    // 프레임 이동 · 만들기
    // ---------------------------------------------------------------------
    public static void SetFrame(int i, bool select = true)
    {
        var d = App.Doc;
        int n = FrameCount(d);
        if (n == 0) return;
        int prev = Frame(d);
        A(d)["frame"] = ((i % n) + n) % n;
        bool layers = false;
        if (select && !Playing)
        {
            var t = AnimFolder(d);
            var c = t != null ? CelAt(t, Frame(d), d) : null;
            if (c != null && d.ActiveId != c.Id) { d.ActiveId = c.Id; layers = true; }
        }
        if (!Playing && Sound.HasAudio(d) && App.Get("animScrub", true))
        {
            var tl = Timeline(d); int f = Frame(d);
            Sound.Play(d, tl[f], tl[f + 1] - tl[f]);
        }
        if (Playing) Playback.InvalidateFrameChange(d, prev, Frame(d));
        else App.InvalidateAll();
        if (layers) App.NotifyLayers();
        Emit();
    }

    /// <summary>트랙 + 칸으로 이동 (타임라인 칸 누르기)</summary>
    public static void SelectCell(Layer track, int f)
    {
        var d = App.Doc;
        A(d)["frame"] = f;
        var c = CelAt(track, f, d);
        d.ActiveId = (c ?? track).Id;
        App.NotifyLayers(); App.InvalidateAll(); Emit();
    }

    public static Layer CreateAnimation(int frames = 1)
    {
        var d = App.Doc;
        if (d == null) return null;
        return Edit("애니메이션 만들기", tx =>
        {
            int n = Math.Max(frames, FrameCount(d));
            var ts = Tracks(d);
            var f = NewTrackRaw(d, ts.Count > 0 ? $"트랙 {ts.Count + 1}" : "애니메이션");
            var xs = tx.Xs(f);
            for (int i = 0; i < frames; i++) xs.Add(NewCel(f, d).Id);
            // 다른 트랙보다 짧으면 마지막 칸을 이어 보여 줌
            while (xs.Count < n) xs.Add(xs.Count > 0 ? xs[^1] : null);
            A(d)["frame"] = 0;
            if (xs.Count > 0 && xs[0] != null) d.ActiveId = xs[0];
            return f;
        });
    }

    /// <summary>새 트랙 (지금 길이만큼, 빈 셀 하나를 처음부터 끝까지)</summary>
    public static Layer AddTrack()
    {
        var d = App.Doc;
        if (d == null) return null;
        int n = Math.Max(1, FrameCount(d));
        return Edit("트랙 추가", tx =>
        {
            var f = NewTrackRaw(d, $"트랙 {Tracks(d).Count + 1}");
            var c = NewCel(f, d);
            var xs = tx.Xs(f);
            for (int i = 0; i < n; i++) xs.Add(c.Id);
            d.ActiveId = c.Id;
            return f;
        });
    }

    /// <summary>지금 보이는 레이어들(폴더 밖)을 각각 프레임으로</summary>
    public static void LayersToAnimation()
    {
        var d = App.Doc;
        var src = d.ChildrenOf(null).Where(l => !l.IsFolder && l.Kind == "raster").ToList();
        if (src.Count < 2) { App.Toast("프레임으로 바꿀 레이어가 2개 이상 필요합니다"); return; }
        Edit("레이어 → 프레임", tx =>
        {
            var f = NewTrackRaw(d, "애니메이션");
            var xs = tx.Xs(f);
            foreach (var l in src) { l.Parent = f.Id; l.Visible = true; xs.Add(l.Id); }
            A(d)["frame"] = 0;
        });
    }

    /// <summary>
    /// 프레임 추가 (지금 프레임 뒤). how: "blank" 빈 셀 | "copy" 복제 | "link" 같은 셀 연결
    /// 지금 트랙만 새 셀을 받고, 다른 트랙은 앞 칸을 그대로 이어 보여 준다.
    /// </summary>
    public static void AddFrame(string how = "blank")
    {
        var d = App.Doc;
        if (d == null) return;
        var track = AnimFolder(d);
        if (track == null) { CreateAnimation(1); return; }
        int f = Frame(d);
        string label = how == "copy" ? "프레임 복제" : how == "link" ? "연결된 프레임 추가" : "빈 프레임 추가";
        Edit(label, tx =>
        {
            var cur = tx.CelAt(track, f);
            foreach (var t in Tracks(d))
            {
                var xs = tx.Xs(t);
                string id = tx.At(t, f);
                if (t == track) id = how == "link" && cur != null ? cur.Id : NewCel(t, d, how == "copy" ? cur : null).Id;
                xs.Insert(Math.Min(f + 1, xs.Count), id);
            }
            if (A(d)["ms"] is JsonArray ms && ms.Count > f + 1) ms.Insert(f + 1, null);
            ShiftAfter(d, f + 1, 1);
            A(d)["frame"] = f + 1;
            var c = tx.CelAt(track, f + 1);
            if (c != null) d.ActiveId = c.Id;
        });
    }

    /// <summary>태그·카메라 키·모션 키를 프레임 삽입/삭제에 맞춰 밀기</summary>
    static void ShiftAfter(Document d, int at, int delta)
    {
        foreach (var n in Tags(d))
        {
            if (n is not JsonObject t) continue;
            int from = J.I(t["from"]), to = J.I(t["to"]);
            if (from >= at) from += delta;
            if (to >= at) to += delta;
            if (to < from) to = from;
            t["from"] = from; t["to"] = to;
        }
        if (A(d)["camera"]?["keys"] is JsonArray ck)
            foreach (var k in ck.OfType<JsonObject>()) { int f = J.I(k["f"]); if (f >= at) k["f"] = Math.Max(at, f + delta); }
        Motion.ShiftKeys(d, at, delta);
    }

    /// <summary>모션 경로를 그림으로 굳히기: 움직이는 프레임마다 옮겨 그린 새 그림을 만들고 모션 키는 지움</summary>
    public static int BakeMotion(Layer track = null)
    {
        var d = App.Doc;
        track ??= AnimFolder(d);
        if (track == null || !(Motion.Keys(track)?.Count > 0)) { App.Toast("이 트랙에 모션 키가 없습니다"); return 0; }
        int made = 0;
        Edit("모션 굳히기", tx =>
        {
            var xs = tx.Xs(track);
            var cache = new Dictionary<string, string>();
            for (int f = 0; f < xs.Count; f++)
            {
                var id = xs[f];
                var mo = Motion.At(track, f, d);
                if (id == null || mo == null) continue;
                var m = mo.Value;
                string key = id + "|" + m.T + "|" + m.Alpha.ToString("0.000");
                if (cache.TryGetValue(key, out var hit)) { xs[f] = hit; continue; }
                var src = d.ById(id);
                var l = NewCel(track, d);
                if (src?.Px != null) Xform.DrawSurface(l.Px, src.Px, m.T, (float)m.Alpha, PixelArt.IsArt(d));
                l.Touch();
                cache[key] = l.Id; xs[f] = l.Id; made++;
            }
            track.Meta.Remove("motion");
            DropUnused(tx);
        });
        return made;
    }

    /// <summary>지금 프레임을 모든 트랙에서 지우기</summary>
    public static void DeleteFrame()
    {
        var d = App.Doc;
        int n = FrameCount(d), f = Frame(d);
        if (n <= 1) { App.Toast("프레임이 하나뿐입니다"); return; }
        Edit("프레임 삭제", tx =>
        {
            foreach (var t in Tracks(d)) { var xs = tx.Xs(t); if (f < xs.Count) xs.RemoveAt(f); }
            if (A(d)["ms"] is JsonArray ms && f < ms.Count) ms.RemoveAt(f);
            if (A(d)["tags"] is JsonArray tags)
                foreach (var t in tags.OfType<JsonObject>().Where(t => J.I(t["from"]) == f && J.I(t["to"]) == f).ToList()) tags.Remove(t);
            ShiftAfter(d, f + 1, -1);
            A(d)["length"] = n - 1;
            DropUnused(tx);
        });
        SetFrame(Math.Min(f, FrameCount(d) - 1));
    }

    /// <summary>지금 트랙의 지금 칸을 비우기 (그 프레임엔 아무것도 안 보임)</summary>
    public static void ClearCell()
    {
        var d = App.Doc; var t = AnimFolder(d); int f = Frame(d);
        if (t == null) return;
        Edit("칸 비우기", tx => { var xs = tx.Xs(t); if (f < xs.Count) xs[f] = null; DropUnused(tx); d.ActiveId = t.Id; });
    }

    /// <summary>빈 칸에 새 셀 만들기</summary>
    public static void NewCelHere(Layer track = null, int f = -1)
    {
        var d = App.Doc;
        track ??= AnimFolder(d);
        if (track == null) return;
        if (f < 0) f = Frame(d);
        Edit("새 셀", tx =>
        {
            var c = NewCel(track, d);
            var xs = tx.Xs(track);
            while (xs.Count <= f) xs.Add(null);
            xs[f] = c.Id; d.ActiveId = c.Id;
        });
    }

    /// <summary>연결 끊기: 같은 셀을 쓰던 다른 칸과 따로 고칠 수 있게 복제 (이어진 구간째)</summary>
    public static void UnlinkCell()
    {
        var d = App.Doc; var t = AnimFolder(d); int f = Frame(d);
        var cur = t != null ? CelAt(t, f, d) : null;
        if (cur == null) return;
        Edit("연결 끊기", tx =>
        {
            var c = NewCel(t, d, cur);
            var xs = tx.Xs(t);
            int a = f, b = f;
            while (a > 0 && xs[a - 1] == cur.Id) a--;
            while (b < xs.Count - 1 && xs[b + 1] == cur.Id) b++;
            for (int i = a; i <= b; i++) xs[i] = c.Id;
            d.ActiveId = c.Id;
        });
    }

    /// <summary>지금 칸을 다른 셀로 연결 (그 셀과 같은 그림이 보임)</summary>
    public static void LinkCellTo(string celId)
    {
        var d = App.Doc; var t = AnimFolder(d); int f = Frame(d);
        if (t == null) return;
        Edit("칸 연결", tx => { var xs = tx.Xs(t); while (xs.Count <= f) xs.Add(null); xs[f] = celId; DropUnused(tx); d.ActiveId = celId; });
    }

    /// <summary>지금 트랙에서 셀이 이어 보이는 장수 (길게 보여 주기)</summary>
    public static int HoldOf(int i, Document d)
    {
        var t = AnimFolder(d);
        if (t == null) return 1;
        var xs = XsOf(t, d);
        if (i < 0 || i >= xs.Count || xs[i] == null) return 1;
        var id = xs[i];
        int a = i, b = i;
        while (a > 0 && xs[a - 1] == id) a--;
        while (b < xs.Count - 1 && xs[b + 1] == id) b++;
        return b - a + 1;
    }

    /// <summary>지금 칸의 셀을 n 장 동안 보이게 (뒤 칸들은 밀리거나 당겨짐 — 이 트랙만)</summary>
    public static void SetHold(int i, int n)
    {
        var d = App.Doc; var t = AnimFolder(d);
        if (t == null) return;
        Edit("프레임 길이", tx =>
        {
            var xs = tx.Xs(t);
            if (i < 0 || i >= xs.Count || xs[i] == null) return;
            var id = xs[i];
            int a = i, b = i;
            while (a > 0 && xs[a - 1] == id) a--;
            while (b < xs.Count - 1 && xs[b + 1] == id) b++;
            int len = b - a + 1;
            if (n > len) xs.InsertRange(b + 1, Enumerable.Repeat(id, n - len));
            else if (n < len) xs.RemoveRange(a + n, len - n);
            // 다른 트랙이 더 길면 그대로, 이 트랙이 짧아졌으면 끝을 빈 칸으로 (Normalize)
        });
    }

    public static void MoveCel(int dir)
    {
        var d = App.Doc; var t = AnimFolder(d); int f = Frame(d), g = f + dir;
        if (t == null || g < 0 || g >= FrameCount(d)) return;
        Edit("칸 옮기기", tx => { var xs = tx.Xs(t); while (xs.Count <= Math.Max(f, g)) xs.Add(null); (xs[f], xs[g]) = (xs[g], xs[f]); });
        SetFrame(g);
    }

    /// <summary>프레임 길이를 ms 로 (null = fps 따라감)</summary>
    public static void SetFrameMs(int f, double? ms)
    {
        var d = App.Doc;
        Edit("프레임 시간", tx =>
        {
            var a = J.Arr(A(d), "ms");
            while (a.Count <= f) a.Add(null);
            a[f] = ms is double v && v > 0 ? JsonValue.Create((int)Math.Max(10, Math.Round(v))) : null;
            if (a.All(x => !J.T(x))) A(d).Remove("ms");
        });
    }

    // ---------------------------------------------------------------------
    // 태그 (구간): { name, from, to, dir: "fwd"|"rev"|"ping", color }
    // ---------------------------------------------------------------------
    public static void AddTag(JsonObject tag)
    {
        Edit("태그 추가", tx =>
        {
            var t = new JsonObject { ["color"] = "#3d7be0", ["dir"] = "fwd" };
            foreach (var (k, v) in tag) t[k] = v?.DeepClone();
            J.Arr(A(tx.D), "tags").Add(t);
        });
    }
    public static void UpdateTag(int i, JsonObject props)
    {
        Edit("태그 고치기", tx => { if (Tags(tx.D)[i] is JsonObject t) foreach (var (k, v) in props) t[k] = v?.DeepClone(); });
    }
    public static void RemoveTag(int i)
    {
        Edit("태그 지우기", tx =>
        {
            var tags = J.Arr(A(tx.D), "tags");
            if (i < tags.Count) tags.RemoveAt(i);
            if (PlayTag(tx.D) == i) A(tx.D)["playTag"] = null;
        });
    }

    /// <summary>재생 순서 (태그를 고르면 그 구간만, 방향대로)</summary>
    public static List<int> PlayOrder(Document d, int? tagIndex = -1)
    {
        int n = FrameCount(d);
        int? ti = tagIndex == -1 ? PlayTag(d) : tagIndex;
        var tags = Tags(d);
        var tag = ti is int k && k >= 0 && k < tags.Count ? tags[k] as JsonObject : null;
        int from = tag != null ? Math.Max(0, J.I(tag["from"])) : 0, to = tag != null ? Math.Min(n - 1, J.I(tag["to"])) : n - 1;
        var fwd = new List<int>();
        for (int f = from; f <= to; f++) fwd.Add(f);
        string dir = J.S(tag?["dir"]) ?? J.S(A(d)["dir"]) ?? "fwd";
        if (dir == "rev") { fwd.Reverse(); return fwd; }
        if (dir == "ping") { if (fwd.Count <= 2) return fwd; var back = fwd.Skip(1).Take(fwd.Count - 2).Reverse(); return fwd.Concat(back).ToList(); }
        return fwd;
    }

    /// <summary>내보낼 프레임 순서 (태그 번호, null = 전체 — 방향은 문서 방향)</summary>
    public static List<int> ExportOrder(Document d, int? tagIndex) => PlayOrder(d, tagIndex);

    // ---------------------------------------------------------------------
    // 라이트 테이블
    // ---------------------------------------------------------------------
    public static HashSet<string> LightIds(Document d) => new((A(d)["light"]?["ids"] as JsonArray)?.Select(J.S).Where(s => s != null) ?? Enumerable.Empty<string>());

    public static void ToggleLight()
    {
        var d = App.Doc; var t = AnimFolder(d);
        var c = t != null ? CelAt(t, Frame(d), d) : null;
        if (c == null) { App.Toast("라이트 테이블에 올릴 그림 칸을 고르세요"); return; }
        var L = J.Obj(A(d), "light");
        if (L["opacity"] == null) L["opacity"] = 0.25;
        var ids = J.Arr(L, "ids");
        var hit = ids.FirstOrDefault(x => J.S(x) == c.Id);
        if (hit != null) ids.Remove(hit); else ids.Add(c.Id);
        d.Modified = true;
        Refresh();
        App.Toast(hit != null ? "라이트 테이블에서 내렸습니다" : $"\"{c.Name}\" 을(를) 라이트 테이블에 올렸습니다 (어느 프레임에서나 흐리게 보임)");
    }

    // ---------------------------------------------------------------------
    // 예전 파일 옮기기: 노출표가 없는 트랙 → 노출표, 카메라 키(셀 번호) → 프레임 번호
    // ---------------------------------------------------------------------
    public static void Migrate(Document d)
    {
        if (d == null) return;
        var a = A(d);
        if (J.D(a["v"], 0) >= 2) return;
        var ts = Tracks(d);
        if (ts.Count > 0 && ts.Any(t => t.Meta?["xs"] is not JsonArray))
        {
            var kids = d.ChildrenOf(ts[0]);
            var holds = a["holds"] as JsonObject;
            if (a["camera"]?["keys"] is JsonArray ck && ck.Count > 0)
            {
                var start = new List<int>(); int tt = 0;
                foreach (var k in kids) { start.Add(tt); tt += Math.Max(1, (int)Math.Round(J.D(holds?[k.Id], 1))); }
                foreach (var key in ck.OfType<JsonObject>())
                {
                    int kf = J.I(key["f"]);
                    if (start.Count > 0) key["f"] = start[Math.Max(0, Math.Min(start.Count - 1, kf))];
                }
            }
            var conv = ts.ToDictionary(t => t, t => XsOf(t, d));
            a["holds"] = new JsonObject();
            int len = conv.Values.Max(x => x.Count);
            a["length"] = len;
            foreach (var (t, xs) in conv)
            {
                while (xs.Count < len) xs.Add(xs.Count > 0 ? xs[^1] : null);
                t.Meta ??= new JsonObject();
                t.Meta["xs"] = J.StrArray(xs);
            }
        }
        a["v"] = 2;
    }
}
