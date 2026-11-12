// 자(룰러): 평행선 / 방사선(1점 투시) / 2·3점 투시 / 동심원 — 브러시 선을 자에 맞춰 준다 (웹 tools/ruler.js).
// 자 목록은 문서 Extra["rulers"] (웹 doc.rulers 와 같은 JSON) → .cpad 에 그대로 저장되어 웹 판과 오간다.
// 대칭 그리기(좌우·상하·상하좌우·방사)도 여기: 설정 symmetry / symN / symMirror (웹 app.settings 와 같은 이름).
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Engine;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

/// <summary>
/// 브러시가 쓰는 자 맞추기 (웹 RulerSnap 과 같음). 처음 움직인 방향에 가장 가까운 자를 골라 그 뒤 점을 자 위로 옮긴다.
/// 쓰는 법: var s = new RulerSnap(시작x, 시작y); 화면에서 8px 쯤 움직였을 때 s.Decide(x,y) (false = 맞출 자 없음) → 이후 s.Project(x,y).
/// 브러시 모듈은 보통 RulerStrokeFilter 를 쓰면 된다.
/// </summary>
public sealed class RulerSnap
{
    enum Kind { Line, Circle }
    readonly record struct Cand(Kind K, P2 D, P2 C, double R);
    readonly P2 s;
    Cand? c;
    public RulerSnap(double startX, double startY) { s = new P2(startX, startY); }
    public bool Decided => c != null;

    /// <summary>설정(rulerSnap)이 켜져 있고 켜진 자가 하나라도 있는가</summary>
    public static bool Active(Document d = null)
    {
        d ??= App.Doc;
        if (d == null || !App.Get("rulerSnap", true)) return false;
        return Rulers.List(d).Any(r => !J.Bool(r, "off"));
    }

    static List<Cand> Candidates(Document d, P2 s)
    {
        var out_ = new List<Cand>();
        foreach (var r in Rulers.List(d))
        {
            if (J.Bool(r, "off")) continue;
            string t = J.Str(r, "type");
            if (t == "parallel") out_.Add(new(Kind.Line, new P2(J.Num(r, "x2") - J.Num(r, "x1"), J.Num(r, "y2") - J.Num(r, "y1")).Norm(), default, 0));
            else if (t == "radial") out_.Add(new(Kind.Line, new P2(J.Num(r, "cx") - s.X, J.Num(r, "cy") - s.Y).Norm(), default, 0));
            else if (t == "concentric") { var cc = new P2(J.Num(r, "cx"), J.Num(r, "cy")); out_.Add(new(Kind.Circle, default, cc, (s - cc).Len)); }
            else if (t == "perspective")
            {
                var vps = Rulers.Vps(r);
                foreach (var vp in vps) out_.Add(new(Kind.Line, (vp - s).Norm(), default, 0));
                bool vert = !(r["vertical"] is JsonValue && !J.Bool(r, "vertical", true));
                if (vps.Count < 3 && vert) out_.Add(new(Kind.Line, new P2(0, 1), default, 0));
                if (vps.Count == 1 && vert) out_.Add(new(Kind.Line, new P2(1, 0), default, 0));
            }
        }
        return out_;
    }

    /// <summary>시작점에서 (x,y) 쪽으로 움직인 방향에 가장 가까운 자를 고름. 없으면 false</summary>
    public bool Decide(double x, double y, Document d = null)
    {
        d ??= App.Doc;
        var v = new P2(x - s.X, y - s.Y).Norm();
        Cand? best = null; double bestScore = double.MaxValue;
        foreach (var cd in Candidates(d, s))
        {
            var dd = cd.D;
            if (cd.K == Kind.Circle) dd = new P2(-(s.Y - cd.C.Y), s.X - cd.C.X).Norm();
            double score = Math.Abs(v.X * dd.Y - v.Y * dd.X);
            if (score < bestScore) { bestScore = score; best = cd; }
        }
        c = best;
        return best != null;
    }

    /// <summary>점을 고른 자 위로 (고르기 전이면 그대로)</summary>
    public (double x, double y) Project(double x, double y)
    {
        if (c is not Cand cd) return (x, y);
        if (cd.K == Kind.Circle)
        {
            var dd = new P2(x - cd.C.X, y - cd.C.Y).Norm();
            return (cd.C.X + dd.X * cd.R, cd.C.Y + dd.Y * cd.R);
        }
        double t = (x - s.X) * cd.D.X + (y - s.Y) * cd.D.Y;
        return (s.X + cd.D.X * t, s.Y + cd.D.Y * t);
    }
}

/// <summary>
/// 브러시 획에 자 맞추기를 끼우는 도우미 (웹 tools/brush.js 의 pending 처리와 같음).
///   var f = RulerStrokeFilter.TryBegin(pt, sx, sy);   // null = 자 없음 → 평소대로 stroke.Begin(pt)
///   움직일 때: foreach (var q in f.Add(pt, sx, sy)) { if (!begun) { stroke.Begin(q); begun = true; } else stroke.Add(q); }
///   끝낼 때: foreach (var q in f.Finish()) (같은 방식) → stroke.End(...)
/// 화면에서 8px 움직이기 전까지 점을 모아 두었다가, 방향이 정해지면 모은 점을 자 위로 옮겨 한꺼번에 돌려준다.
/// </summary>
public sealed class RulerStrokeFilter
{
    readonly RulerSnap snap;
    readonly List<Pt> pending = new();
    readonly double sx0, sy0;
    bool decided, passthrough;
    RulerStrokeFilter(Pt start, double sx, double sy) { snap = new RulerSnap(start.X, start.Y); pending.Add(start); sx0 = sx; sy0 = sy; }

    public static RulerStrokeFilter TryBegin(Pt start, double sx, double sy) => RulerSnap.Active() ? new RulerStrokeFilter(start, sx, sy) : null;
    public RulerSnap Snap => snap;

    public IReadOnlyList<Pt> Add(Pt p, double sx, double sy)
    {
        if (passthrough) return new[] { p };
        if (decided) return new[] { Proj(p) };
        pending.Add(p);
        if (Math.Sqrt((sx - sx0) * (sx - sx0) + (sy - sy0) * (sy - sy0)) < 8) return Array.Empty<Pt>();
        decided = true;
        if (!snap.Decide(p.X, p.Y)) passthrough = true;
        var outp = pending.Select(q => passthrough ? q : Proj(q)).ToList();
        pending.Clear();
        return outp;
    }

    /// <summary>획을 끝낼 때: 아직 방향을 못 정했으면 모은 점 그대로</summary>
    public IReadOnlyList<Pt> Finish() { var o = pending.ToList(); pending.Clear(); return o; }

    Pt Proj(Pt q) { var (x, y) = snap.Project(q.X, q.Y); return new Pt(x, y, q.P, q.T); }
}

/// <summary>대칭 그리기 (웹 core/brush.js 의 대칭 찍기와 같은 규칙). 브러시 모듈은 Copies 로 추가로 찍을 자리를 얻는다</summary>
public static class Symmetry
{
    public static string Mode => App.Get("symmetry", "none");
    public static int N => Math.Max(2, App.Get("symN", 6));
    public static bool Mirror => App.Get("symMirror", false);
    public static bool On => Mode != "none";

    /// <summary>원래 점(x,y, 붓 각도 rot 라디안) 말고 추가로 찍을 점들</summary>
    public static List<(double x, double y, double rot)> Copies(int W, int H, double x, double y, double rot = 0)
    {
        var o = new List<(double, double, double)>();
        string m = Mode;
        if (m == "v" || m == "vh") o.Add((W - x, y, -rot));
        if (m == "h" || m == "vh") o.Add((x, H - y, -rot));
        if (m == "vh") o.Add((W - x, H - y, rot));
        if (m == "radial")
        {
            int n = N; double cx = W / 2.0, cy = H / 2.0, dx = x - cx, dy = y - cy;
            for (int i = 0; i < n; i++)
            {
                double a = (double)i / n * Math.PI * 2, c = Math.Cos(a), s = Math.Sin(a);
                if (i > 0) o.Add((cx + dx * c - dy * s, cy + dx * s + dy * c, rot + a));
                if (Mirror) o.Add((cx - (dx * c - dy * s), cy + dx * s + dy * c, -rot - a));
            }
        }
        return o;
    }

    /// <summary>점 목록 전체를 옮기는 함수들 (도형처럼 모양 전체를 대칭으로 그릴 때). 원래 모양은 빠짐</summary>
    public static List<Func<P2, P2>> Maps(int W, int H)
    {
        var o = new List<Func<P2, P2>>();
        string m = Mode;
        if (m == "v" || m == "vh") o.Add(p => new P2(W - p.X, p.Y));
        if (m == "h" || m == "vh") o.Add(p => new P2(p.X, H - p.Y));
        if (m == "vh") o.Add(p => new P2(W - p.X, H - p.Y));
        if (m == "radial")
        {
            int n = N; double cx = W / 2.0, cy = H / 2.0; bool mir = Mirror;
            for (int i = 0; i < n; i++)
            {
                double a = (double)i / n * Math.PI * 2, c = Math.Cos(a), s = Math.Sin(a);
                if (i > 0) o.Add(p => { double dx = p.X - cx, dy = p.Y - cy; return new P2(cx + dx * c - dy * s, cy + dx * s + dy * c); });
                if (mir) o.Add(p => { double dx = p.X - cx, dy = p.Y - cy; return new P2(cx - (dx * c - dy * s), cy + dx * s + dy * c); });
            }
        }
        return o;
    }

    public static void Cycle()
    {
        string s = Mode;
        string next = s == "none" ? "v" : s == "v" ? "h" : s == "h" ? "vh" : s == "vh" ? "radial" : "none";
        App.Set("symmetry", next); App.NotifySettings();
        App.Toast("대칭: " + (next switch { "none" => "끔", "v" => "좌우", "h" => "상하", "vh" => "상하좌우", _ => $"방사 {N}조각" }));
        MetaLayers.Redraw();
    }

    public static void RadialDialog()
    {
        var v = CUI.Form("방사 대칭 (만다라)", new[] { CUI.Num("n", "조각 수", N, 2, 64), CUI.Chk("mirror", "거울 대칭도 함께", Mirror) });
        if (v == null) return;
        App.Set("symmetry", "radial"); App.Set("symN", (int)CUI.D(v, "n")); App.Set("symMirror", CUI.B(v, "mirror"));
        App.NotifySettings(); MetaLayers.Redraw();
    }

    /// <summary>대칭 축 (분홍 점선)</summary>
    public static void Draw(Graphics g, ViewXf v)
    {
        var d = App.Doc; string m = Mode;
        if (d == null || m == "none") return;
        using var pen = CUI.Dash(Color.FromArgb(204, 230, 80, 160), 1.5f, 8, 6);
        void Seg(double x0, double y0, double x1, double y1) => g.DrawLine(pen, Gfx.S(v, x0, y0), Gfx.S(v, x1, y1));
        if (m.Contains('v') && m != "radial") Seg(d.W / 2.0, 0, d.W / 2.0, d.H);
        if (m == "radial")
        {
            int n = N; double R = Math.Sqrt((double)d.W * d.W + (double)d.H * d.H) / 2;
            for (int i = 0; i < n; i++) { double a = (double)i / n * Math.PI * 2 - Math.PI / 2; Seg(d.W / 2.0, d.H / 2.0, d.W / 2.0 + Math.Cos(a) * R, d.H / 2.0 + Math.Sin(a) * R); }
        }
        else if (m.Contains('h')) Seg(0, d.H / 2.0, d.W, d.H / 2.0);
    }
}

/// <summary>자 목록 다루기 + 화면 표시 + 자 도구</summary>
public static class Rulers
{
    public static JsonArray Arr(Document d) => d?.Extra["rulers"] as JsonArray;
    public static IEnumerable<JsonObject> List(Document d) => Arr(d)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
    public static List<P2> Vps(JsonObject r) => (r["vps"] as JsonArray)?.Select(J.ToP).ToList() ?? new();
    public static JsonArray Snapshot(Document d) => (JsonArray)(Arr(d)?.DeepClone() ?? new JsonArray());
    public static event Action Changed;

    /// <summary>자 바꾸기를 실행취소 가능하게 (before = 바꾸기 전 Snapshot)</summary>
    public static void Push(string label, JsonArray before)
    {
        var d = App.Doc;
        var after = Snapshot(d);
        App.History.Push(new ExtraEdit("rulers", label, before, after, () => { Changed?.Invoke(); MetaLayers.Redraw(); }));
        d.Modified = true;
        Changed?.Invoke();
        MetaLayers.Redraw();
    }

    public static void Add(string type)
    {
        var d = App.Doc; if (d == null) return;
        var before = Snapshot(d);
        if (Arr(d) == null) d.Extra["rulers"] = new JsonArray();
        var cv = MetaLayers.Canvas;
        var (cx, cy) = cv != null ? cv.V.ToDoc(cv.Width / 2.0, cv.Height / 2.0) : (d.W / 2.0, d.H / 2.0);
        double W = d.W, H = d.H;
        JsonObject r = type switch
        {
            "parallel" => new() { ["type"] = type, ["x1"] = J.R(cx - W * 0.2), ["y1"] = J.R(cy), ["x2"] = J.R(cx + W * 0.2), ["y2"] = J.R(cy) },
            "radial" or "concentric" => new() { ["type"] = type, ["cx"] = J.R(cx), ["cy"] = J.R(cy) },
            "p1" => new() { ["type"] = "perspective", ["vps"] = new JsonArray(J.Pt(cx, H * 0.4)) },
            "p2" => new() { ["type"] = "perspective", ["vps"] = new JsonArray(J.Pt(-W * 0.3, H * 0.4), J.Pt(W * 1.3, H * 0.4)) },
            _ => new() { ["type"] = "perspective", ["vps"] = new JsonArray(J.Pt(-W * 0.3, H * 0.3), J.Pt(W * 1.3, H * 0.3), J.Pt(W / 2, H * 2.2)) },
        };
        Arr(d).Add(r);
        App.Set("showRulers", true);
        Push("자 추가", before);
        if (App.Tool?.Id != "ruler") App.SetTool("ruler");
        App.Toast("보라색 손잡이를 끌어 위치를 맞추세요. 화면을 축소하면 소실점이 보입니다.");
    }

    /// <summary>손잡이: (자, 읽기, 쓰기)</summary>
    public static List<(JsonObject r, Func<P2> get, Action<P2> set)> Handles(Document d)
    {
        var o = new List<(JsonObject, Func<P2>, Action<P2>)>();
        foreach (var r in List(d))
        {
            string t = J.Str(r, "type");
            if (t == "parallel")
            {
                o.Add((r, () => new P2(J.Num(r, "x1"), J.Num(r, "y1")), p => { r["x1"] = J.R(p.X); r["y1"] = J.R(p.Y); }));
                o.Add((r, () => new P2(J.Num(r, "x2"), J.Num(r, "y2")), p => { r["x2"] = J.R(p.X); r["y2"] = J.R(p.Y); }));
            }
            else if (t == "radial" || t == "concentric") o.Add((r, () => new P2(J.Num(r, "cx"), J.Num(r, "cy")), p => { r["cx"] = J.R(p.X); r["cy"] = J.R(p.Y); }));
            else if (t == "perspective" && r["vps"] is JsonArray vps)
                for (int i = 0; i < vps.Count; i++) { int k = i; o.Add((r, () => J.ToP(vps[k]), p => vps[k] = J.Pt(p.X, p.Y))); }
        }
        return o;
    }

    /// <summary>화면 표시 (늘 그림)</summary>
    public static void Draw(Graphics g, ViewXf v)
    {
        var d = App.Doc;
        Symmetry.Draw(g, v);
        if (d == null || !App.Get("showRulers", true)) return;
        var list = List(d).ToList();
        if (list.Count == 0) return;
        bool editing = App.Tool?.Id == "ruler";
        double L = Math.Sqrt((double)d.W * d.W + (double)d.H * d.H) * 2;
        foreach (var r in list)
        {
            bool off = J.Bool(r, "off");
            using var pen = new Pen(off ? Color.FromArgb(64, 150, 150, 150) : editing ? Color.FromArgb(140, 140, 90, 255) : Color.FromArgb(71, 140, 90, 255), 1);
            void Seg(double ax, double ay, double bx, double by) => g.DrawLine(pen, Gfx.S(v, ax, ay), Gfx.S(v, bx, by));
            string t = J.Str(r, "type");
            if (t == "parallel")
            {
                var dd = new P2(J.Num(r, "x2") - J.Num(r, "x1"), J.Num(r, "y2") - J.Num(r, "y1")).Norm(); var n = new P2(-dd.Y, dd.X);
                double step = Math.Max(d.W, d.H) / 12.0;
                for (int i = -14; i <= 14; i++)
                {
                    double ox = J.Num(r, "x1") + n.X * step * i, oy = J.Num(r, "y1") + n.Y * step * i;
                    Seg(ox - dd.X * L, oy - dd.Y * L, ox + dd.X * L, oy + dd.Y * L);
                }
            }
            else if (t == "radial" || t == "perspective")
            {
                var pts = t == "radial" ? new List<P2> { new(J.Num(r, "cx"), J.Num(r, "cy")) } : Vps(r);
                foreach (var vp in pts)
                    for (int i = 0; i < 36; i++) { double a = i / 36.0 * Math.PI * 2; Seg(vp.X, vp.Y, vp.X + Math.Cos(a) * L, vp.Y + Math.Sin(a) * L); }
                if (t == "perspective" && pts.Count >= 2)
                {
                    var a = pts[0]; var dd = (pts[1] - a).Norm();
                    using var hp = new Pen(Color.FromArgb(153, 255, 90, 90), 1);
                    g.DrawLine(hp, Gfx.S(v, a.X - dd.X * L, a.Y - dd.Y * L), Gfx.S(v, a.X + dd.X * L, a.Y + dd.Y * L));
                }
            }
            else if (t == "concentric")
            {
                double step = Math.Max(d.W, d.H) / 16.0;
                var c = Gfx.S(v, J.Num(r, "cx"), J.Num(r, "cy"));
                for (int i = 1; i <= 20; i++) { float rr = (float)(step * i * v.Zoom); g.DrawEllipse(pen, c.X - rr, c.Y - rr, rr * 2, rr * 2); }
            }
        }
        if (!editing) return;
        foreach (var (r, get, _) in Handles(d))
        {
            var p = Gfx.S(v, get());
            using var br = new SolidBrush(J.Bool(r, "off") ? Color.FromArgb(136, 136, 136) : Color.FromArgb(140, 90, 255));
            using var wp = new Pen(Color.White, 2);
            g.FillEllipse(br, p.X - 9, p.Y - 9, 18, 18); g.DrawEllipse(wp, p.X - 9, p.Y - 9, 18, 18);
        }
    }
}

/// <summary>문서 Extra 의 한 값(rulers·print·notes …) 바꾸기 기록</summary>
public sealed class ExtraEdit : IEdit
{
    readonly string key; readonly JsonNode before, after; readonly Action changed;
    public string Label { get; }
    public ExtraEdit(string key, string label, JsonNode before, JsonNode after, Action changed = null) { this.key = key; Label = label; this.before = before?.DeepClone(); this.after = after?.DeepClone(); this.changed = changed; }
    public long Bytes => (before?.ToJsonString().Length ?? 0) * 2 + (after?.ToJsonString().Length ?? 0) * 2 + 64;
    Rect Put(Document d, JsonNode v) { if (v == null) d.Extra.Remove(key); else d.Extra[key] = v.DeepClone(); changed?.Invoke(); return Rect.Empty; }
    public Rect Undo(Document d) => Put(d, before);
    public Rect Redo(Document d) => Put(d, after);
    /// <summary>Extra[key] 를 바꾸고 기록</summary>
    public static void Set(string key, string label, JsonNode value, Action changed = null)
    {
        var d = App.Doc;
        var before = d.Extra[key]?.DeepClone();
        if (value == null) d.Extra.Remove(key); else d.Extra[key] = value.DeepClone();
        App.History.Push(new ExtraEdit(key, label, before, value, changed));
        d.Modified = true;
        changed?.Invoke();
    }
}

public sealed class RulerTool : ToolBase
{
    public override string Id => "ruler";
    public override string Name => "자 (투시/평행)";
    public override string Icon => "📐";
    public override Keys Key => Keys.R;
    public override Cursor Cursor => Cursors.Default;
    (Action<P2> set, JsonArray before)? drag;
    CUI.Host host;

    public override void Activate() { MetaLayers.Redraw(); }
    public override void Deactivate() { MetaLayers.Redraw(); }

    public override void Down(PointerArgs e)
    {
        double rad = e.Type == "touch" ? 26 : 16;
        var v = MetaLayers.Canvas?.V; if (v == null) return;
        foreach (var (_, get, set) in Rulers.Handles(App.Doc))
        {
            var p = Gfx.S(v, get());
            if (Math.Sqrt((p.X - e.SX) * (p.X - e.SX) + (p.Y - e.SY) * (p.Y - e.SY)) <= rad) { drag = (set, Rulers.Snapshot(App.Doc)); return; }
        }
    }
    public override void Move(PointerArgs e) { if (drag is { } dd) { dd.set(new P2(e.X, e.Y)); MetaLayers.Redraw(); } }
    public override void Up(PointerArgs e) { if (drag is { } dd) Rulers.Push("자 이동", dd.before); drag = null; }
    public override void Cancel()
    {
        if (drag is { } dd) { App.Doc.Extra["rulers"] = dd.before.DeepClone(); MetaLayers.Redraw(); }
        drag = null;
    }
    public override bool Busy => drag != null;

    public override Control BuildOptions()
    {
        host = new CUI.Host(Items);
        Rulers.Changed -= Refresh; Rulers.Changed += Refresh;
        return host.Panel;
    }
    void Refresh() { if (host != null && !host.Panel.IsDisposed) host.Rebuild(); }

    IEnumerable<Control> Items()
    {
        yield return Opt.Title("자 추가");
        yield return Opt.Row(Opt.Button("평행선", () => Rulers.Add("parallel")), Opt.Button("방사선", () => Rulers.Add("radial")), Opt.Button("동심원", () => Rulers.Add("concentric")));
        yield return Opt.Row(Opt.Button("1점 투시", () => Rulers.Add("p1")), Opt.Button("2점 투시", () => Rulers.Add("p2")), Opt.Button("3점 투시", () => Rulers.Add("p3")));
        yield return Opt.Title("자 목록");
        var d = App.Doc;
        var list = Rulers.List(d).ToList();
        var names = new Dictionary<string, string> { ["parallel"] = "평행선", ["radial"] = "방사선", ["concentric"] = "동심원", ["perspective"] = "투시" };
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i]; int k = i;
            string t = J.Str(r, "type");
            string label = t == "perspective" ? $"{Rulers.Vps(r).Count}점 투시" : names.GetValueOrDefault(t ?? "", t);
            var chk = new CheckBox { Text = label, Checked = !J.Bool(r, "off"), ForeColor = Opt.Fg, AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
            chk.CheckedChanged += (_, _) => { var b = Rulers.Snapshot(App.Doc); r["off"] = !chk.Checked; Rulers.Push("자 켜기/끄기", b); };
            yield return Opt.Row(chk, Opt.Button("삭제", () => { var b = Rulers.Snapshot(App.Doc); Rulers.Arr(App.Doc).RemoveAt(k); Rulers.Push("자 삭제", b); }));
        }
        if (list.Count == 0) yield return Opt.Help("자가 없습니다.");
        yield return Opt.Title("설정");
        yield return Opt.Check("브러시를 자에 맞추기", () => App.Get("rulerSnap", true), v => App.Set("rulerSnap", v));
        yield return Opt.Check("자 보이기", () => App.Get("showRulers", true), v => { App.Set("showRulers", v); MetaLayers.Redraw(); });
        yield return Opt.Help("브러시로 그리기 시작한 방향에 가장 가까운 자에 선이 붙습니다. 끄려면 퀵바의 \"자\" 버튼이나 이 설정을 끄세요.");
    }
}
