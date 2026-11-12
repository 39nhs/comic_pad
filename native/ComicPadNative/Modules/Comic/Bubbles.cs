// 말풍선 (웹 tools/comic.js 말풍선 부분). 말풍선 레이어: kind 'bubble', meta { bubbles:[{ id, x, y, w, h, shape, line, fill, stroke, tailWidth, seed, tail:{x,y}|null, textId?, group? }] }.
// 모양 4가지(타원·사각·외침·생각), 꼬리, 이어 붙이기(같은 group 은 테두리를 먼저 모두 긋고 안을 한꺼번에 채워 하나의 외곽선).
// 만든 뒤에도 탭해서 고르고 이동·크기·꼬리·모양·색을 바꿀 수 있고, 연결된 대사(글자 레이어)가 함께 움직인다.
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

public static class Bubbles
{
    /// <summary>웹 Math.round 와 같은 반올림</summary>
    public static double JsRound(double v) => Math.Floor(v + 0.5);

    /// <summary>웹 seeded(): 파크-밀러 난수 (같은 seed 면 같은 모양)</summary>
    public static Func<double> Seeded(double seed)
    {
        long s = (long)seed % 2147483647; if (s <= 0) s += 2147483646;
        return () => (s = s * 16807 % 2147483647) / 2147483647.0;
    }

    public static (GraphicsPath body, GraphicsPath tail) Paths(JsonObject b)
    {
        double x = J.Num(b, "x"), y = J.Num(b, "y"), w = J.Num(b, "w"), h = J.Num(b, "h");
        double cx = x + w / 2, cy = y + h / 2, rx = w / 2, ry = h / 2;
        string shape = J.Str(b, "shape", "ellipse");
        var seedN = J.Num(b, "seed", 0);
        var rnd = Seeded(seedN == 0 ? 7 : seedN);
        var body = new GraphicsPath(FillMode.Winding);
        if (shape == "rect")
        {
            double r = Math.Min(Math.Min(w, h) * 0.18, Math.Min(w, h) / 2);
            if (r <= 0.01) body.AddRectangle(new RectangleF((float)x, (float)y, (float)w, (float)h));
            else
            {
                float d = (float)(r * 2), X = (float)x, Y = (float)y, W = (float)w, H = (float)h;
                body.AddArc(X, Y, d, d, 180, 90); body.AddArc(X + W - d, Y, d, d, 270, 90);
                body.AddArc(X + W - d, Y + H - d, d, d, 0, 90); body.AddArc(X, Y + H - d, d, d, 90, 90);
                body.CloseFigure();
            }
        }
        else if (shape == "shout")
        {
            int spikes = (int)Math.Max(10, JsRound((rx + ry) / 18));
            var pts = new PointF[spikes * 2];
            for (int i = 0; i < spikes * 2; i++)
            {
                double a = (double)i / (spikes * 2) * Math.PI * 2;
                bool outer = i % 2 == 0;
                double k = outer ? 1.12 + rnd() * 0.18 : 0.86 + rnd() * 0.04;
                pts[i] = new PointF((float)(cx + Math.Cos(a) * rx * k), (float)(cy + Math.Sin(a) * ry * k));
            }
            body.AddPolygon(pts);
        }
        else if (shape == "cloud")
        {
            int n = (int)Math.Max(8, JsRound((rx + ry) / 22));
            body.AddEllipse((float)(cx - rx * 0.86), (float)(cy - ry * 0.86), (float)(rx * 1.72), (float)(ry * 1.72));
            for (int i = 0; i < n; i++)
            {
                double a = (double)i / n * Math.PI * 2;
                double r = Math.PI * (rx + ry) / n * (0.55 + rnd() * 0.12);
                double px = cx + Math.Cos(a) * rx * 0.9, py = cy + Math.Sin(a) * ry * 0.9;
                body.AddEllipse((float)(px - r), (float)(py - r), (float)(r * 2), (float)(r * 2));
            }
        }
        else body.AddEllipse((float)x, (float)y, (float)w, (float)h);

        GraphicsPath tail = null;
        if (b["tail"] is JsonObject t)
        {
            tail = new GraphicsPath(FillMode.Winding);
            double tx = J.Num(t, "x"), ty = J.Num(t, "y");
            double dx = tx - cx, dy = ty - cy, L = Math.Sqrt(dx * dx + dy * dy); if (L < 1e-9) L = 1;
            double nx = -dy / L, ny = dx / L;
            if (shape == "cloud")
            {
                for (int i = 1; i <= 3; i++)
                {
                    double tt = 0.62 + i * 0.12, px = cx + dx * tt, py = cy + dy * tt;
                    double r = Math.Min(rx, ry) * (0.16 - i * 0.035);
                    if (r <= 0) continue;
                    tail.AddEllipse((float)(px - r * 1.3), (float)(py - r), (float)(r * 2.6), (float)(r * 2));
                }
            }
            else
            {
                double bw = Math.Min(rx, ry) * J.Num(b, "tailWidth", 0.45);
                double bx = cx + dx * 0.25, by = cy + dy * 0.25;
                var b1 = new P2(bx + nx * bw / 2, by + ny * bw / 2); var b2 = new P2(bx - nx * bw / 2, by - ny * bw / 2);
                double bend = shape == "shout" ? 0 : L * 0.08;
                var tip = new P2(tx, ty);
                Geo.Quad(tail, b1, new P2((b1.X + tx) / 2 + nx * bend, (b1.Y + ty) / 2 + ny * bend), tip);
                Geo.Quad(tail, tip, new P2((b2.X + tx) / 2 + nx * bend * 0.4, (b2.Y + ty) / 2 + ny * bend * 0.4), b2);
                tail.CloseFigure();
            }
        }
        return (body, tail);
    }

    /// <summary>테두리 2배 굵기로 먼저 긋고 채우기로 덮어 합쳐진 외곽선 (같은 group 은 함께)</summary>
    public static void DrawGroup(Graphics g, IList<JsonObject> group)
    {
        var paths = group.Select(b => (b, p: Paths(b))).ToList();
        try
        {
            foreach (var (b, (body, tail)) in paths)
            {
                double line = J.Num(b, "line", 4);
                if (line <= 0) continue;
                using var pen = new Pen(Gfx.Col(J.Str(b, "stroke", "#000000")), (float)(line * 2)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawPath(pen, body); if (tail != null) g.DrawPath(pen, tail);
            }
            foreach (var (b, (body, tail)) in paths)
            {
                using var br = new SolidBrush(Gfx.Col(J.Str(b, "fill", "#ffffff")));
                g.FillPath(br, body); if (tail != null) g.FillPath(br, tail);
            }
        }
        finally { foreach (var (_, (body, tail)) in paths) { body.Dispose(); tail?.Dispose(); } }
    }

    /// <summary>풍선 하나가 차지하는 범위 (테두리 포함)</summary>
    public static Rect BoundsOf(JsonObject b)
    {
        var (body, tail) = Paths(b);
        using (body) using (tail)
        {
            var r = body.GetBounds();
            if (tail != null) r = RectangleF.Union(r, tail.GetBounds());
            double pad = J.Num(b, "line", 4) + 3;
            return Rect.FromPoints(r.Left, r.Top, r.Right, r.Bottom, pad);
        }
    }

    public static List<JsonObject> List(JsonObject meta) => (meta?["bubbles"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();

    public static void DrawAll(Graphics g, JsonObject meta)
    {
        var list = List(meta);
        var done = new HashSet<JsonObject>();
        foreach (var b in list)
        {
            if (done.Contains(b)) continue;
            string grp = J.Str(b, "group");
            var group = grp != null ? list.Where(x => J.Str(x, "group") == grp).ToList() : new List<JsonObject> { b };
            foreach (var x in group) done.Add(x);
            DrawGroup(g, group);
        }
    }

    public static (Surface s, Rect r) Render(Document d, Layer l)
    {
        var list = List(l.Meta);
        var r = Rect.Empty;
        foreach (var b in list) r = r.Union(BoundsOf(b));
        r = r.Clip(d.W, d.H);
        if (r.IsEmpty) return (new Surface(d.W, d.H), Rect.Empty);
        var px = Gfx.Render(r, g => DrawAll(g, l.Meta));
        return (Gfx.NewSurface(d, r, px), r);
    }

    public static bool IsEditable(Layer l) => l?.Kind == "bubble" && l.Meta?["bubbles"] is JsonArray;

    /// <summary>쓸 말풍선 레이어: 현재 것 → 있는 것 → 새로 (맨 위)</summary>
    public static Layer LayerForNew(out bool created)
    {
        var d = App.Doc; created = false;
        if (IsEditable(d.Active)) return d.Active;
        var ex = d.Layers.LastOrDefault(IsEditable);
        if (ex != null) { d.ActiveId = ex.Id; App.NotifyLayers(); return ex; }
        created = true;
        return null;
    }

    public static bool Hit(JsonObject b, double x, double y)
    {
        var (body, tail) = Paths(b);
        using (body) using (tail)
        {
            using var pen = new Pen(Color.Black, (float)Math.Max(8, J.Num(b, "line", 4) * 2));
            return body.IsVisible((float)x, (float)y) || body.IsOutlineVisible((float)x, (float)y, pen) || (tail != null && tail.IsVisible((float)x, (float)y));
        }
    }

    /// <summary>(x,y) 위의 말풍선: 현재 레이어 먼저, 위에 있는 것부터</summary>
    public static (Layer l, JsonObject b)? At(double x, double y)
    {
        var d = App.Doc;
        var layers = d.Layers.Where(l => IsEditable(l) && l.Visible && !l.Locked).OrderByDescending(l => l == d.Active).ThenByDescending(l => d.IndexOf(l));
        foreach (var l in layers)
        {
            var list = List(l.Meta);
            for (int i = list.Count - 1; i >= 0; i--) if (Hit(list[i], x, y)) return (l, list[i]);
        }
        return null;
    }

    public static (double x0, double y0, double x1, double y1) BBox(JsonObject b)
    {
        double x = J.Num(b, "x"), y = J.Num(b, "y"), w = J.Num(b, "w"), h = J.Num(b, "h");
        double x0 = x, y0 = y, x1 = x + w, y1 = y + h;
        if (b["tail"] is JsonObject t) { x0 = Math.Min(x0, J.Num(t, "x")); y0 = Math.Min(y0, J.Num(t, "y")); x1 = Math.Max(x1, J.Num(t, "x")); y1 = Math.Max(y1, J.Num(t, "y")); }
        return (x0, y0, x1, y1);
    }

    /// <summary>연결된 대사 레이어 옮기기 (base = 원래 meta)</summary>
    public static JsonObject MovedText(JsonObject baseMeta, double dx, double dy)
    {
        var m = J.Clone(baseMeta);
        if (m["mat"] is JsonArray mat && mat.Count >= 6) { mat[4] = J.R(J.Num(mat[4]) + dx); mat[5] = J.R(J.Num(mat[5]) + dy); }
        else { m["x"] = J.R(J.Num(m, "x") + dx); m["y"] = J.R(J.Num(m, "y") + dy); }
        return m;
    }

    /// <summary>말풍선 레이어(+대사 레이어) meta 를 한 번에 바꾸기 (실행취소 한 번)</summary>
    public static void Commit(Layer bl, JsonObject bmeta, string label, Layer tl = null, JsonObject tmeta = null, Action also = null)
    {
        var d = App.Doc;
        var old = (bl.Px?.Bounds() ?? Rect.Empty).Union(tl?.Px?.Bounds() ?? Rect.Empty);
        Rect r = Rect.Empty;
        App.History.Struct(d, label, () =>
        {
            bl.Meta = bmeta; var (s, rr) = MetaLayers.Render(bl); bl.Px = s; r = rr;
            if (tl != null && tmeta != null) { tl.Meta = tmeta; var (s2, r2) = MetaLayers.Render(tl); tl.Px = s2; r = r.Union(r2); }
            also?.Invoke();
        });
        d.Modified = true;
        var inv = old.Union(r);
        App.Invalidate(inv.IsEmpty ? new Rect(0, 0, d.W, d.H) : inv.Inflate(4));
        App.NotifyLayers();
    }

    public static void ToRaster()
    {
        var l = App.Active;
        if (l?.Kind != "bubble") { App.Toast("말풍선 레이어를 고르세요"); return; }
        App.History.Struct(App.Doc, "말풍선을 그림으로", () => l.Meta = null);
        App.NotifyLayers();
    }
}

public sealed class BubbleTool : ToolBase
{
    public override string Id => "bubble";
    public override string Name => "말풍선";
    public override string Icon => "💬";
    public override Keys Key => Keys.F;

    // 새 말풍선 옵션 (웹 opt 와 같음)
    public string Shape = "ellipse"; public double Line = 4; public bool Tail = true; public double TailWidth = 0.45;
    public string FillC = "#ffffff", StrokeC = "#000000"; public bool AutoText = true;

    sealed class NewSt { public string Phase = "box"; public double X0, Y0, X1, Y1; public long Seed; public P2 TailP; }
    NewSt st;
    (Layer l, string id)? sel;
    sealed class DragSt { public Layer L; public string Id, Mode; public double X0, Y0, SX0, SY0; public bool Moved; public JsonObject Orig; public MetaPreview Prev, TextPrev; public JsonObject TextBefore; }
    DragSt drag;
    CUI.Host host;
    MetaPreview livePrev;
    const double HANDLE = 12;
    static readonly Random rng = new();

    public override bool Busy => st != null || drag != null;

    JsonObject NewBubble()
    {
        var r = Geo.FromPoints(st.X0, st.Y0, st.X1, st.Y1);
        var b = new JsonObject
        {
            ["x"] = J.R(r.x), ["y"] = J.R(r.y), ["w"] = J.R(r.w), ["h"] = J.R(r.h), ["shape"] = Shape, ["line"] = Line, ["fill"] = FillC, ["stroke"] = StrokeC,
            ["tailWidth"] = TailWidth, ["seed"] = st.Seed, ["tail"] = st.Phase == "tail" && Tail ? J.Pt(st.TailP.X, st.TailP.Y) : null,
        };
        return b;
    }

    JsonObject Selected()
    {
        if (sel is not { } s || !App.Doc.Layers.Contains(s.l) || !Bubbles.IsEditable(s.l)) return null;
        return Bubbles.List(s.l.Meta).FirstOrDefault(b => J.Str(b, "id") == s.id);
    }

    List<(string k, P2 p)> Handles(JsonObject b)
    {
        double x = J.Num(b, "x"), y = J.Num(b, "y"), w = J.Num(b, "w"), h = J.Num(b, "h");
        var o = new List<(string, P2)> { ("nw", new(x, y)), ("ne", new(x + w, y)), ("se", new(x + w, y + h)), ("sw", new(x, y + h)) };
        if (b["tail"] is JsonObject t) o.Add(("tail", J.ToP(t)));
        return o;
    }

    public override void Down(PointerArgs e)
    {
        if (livePrev != null) EndLive("말풍선 수정");   // 키보드로 바꾼 슬라이더 값도 기록
        if (st?.Phase == "tail") { st.TailP = new P2(e.X, e.Y); return; }
        var cur = Selected();
        if (cur != null)
        {
            double z = MetaLayers.Zoom;
            var hd = Handles(cur).FirstOrDefault(p => (p.p - new P2(e.X, e.Y)).Len * z <= HANDLE);
            if (hd.k != null) { StartDrag(sel.Value.l, cur, hd.k, e); return; }
        }
        var hit = Bubbles.At(e.X, e.Y);
        if (hit is { } h)
        {
            sel = (h.l, J.Str(h.b, "id"));
            if (App.Doc.Active != h.l) { App.Doc.ActiveId = h.l.Id; App.NotifyLayers(); }
            StartDrag(h.l, h.b, "move", e);
            host?.Rebuild();
            return;
        }
        if (sel != null) { sel = null; host?.Rebuild(); }
        st = new NewSt { X0 = e.X, Y0 = e.Y, X1 = e.X, Y1 = e.Y, Seed = rng.Next(1_000_000) };
    }

    void StartDrag(Layer layer, JsonObject b, string mode, PointerArgs e)
    {
        if (!App.CheckEditable(layer)) return;
        string tid = J.Str(b, "textId");
        var t = tid != null ? App.Doc.ById(tid) : null;
        drag = new DragSt
        {
            L = layer, Id = J.Str(b, "id"), Mode = mode, X0 = e.X, Y0 = e.Y, SX0 = e.SX, SY0 = e.SY, Orig = J.Clone(b), Prev = new MetaPreview(layer),
            TextPrev = t?.Kind == "text" && t.Meta != null ? new MetaPreview(t) : null, TextBefore = t?.Kind == "text" ? J.Clone(t.Meta) : null,
        };
    }

    void DragMove(PointerArgs e)
    {
        var d = drag;
        if (Math.Sqrt((e.SX - d.SX0) * (e.SX - d.SX0) + (e.SY - d.SY0) * (e.SY - d.SY0)) > 4) d.Moved = true;
        if (!d.Moved) return;
        var meta = d.Prev.OrigMeta;
        var b = Bubbles.List(meta).FirstOrDefault(x => J.Str(x, "id") == d.Id);
        if (b == null) return;
        var o = d.Orig; double dx = e.X - d.X0, dy = e.Y - d.Y0;
        double ox = J.Num(o, "x"), oy = J.Num(o, "y"), ow = J.Num(o, "w"), oh = J.Num(o, "h");
        JsonObject tm = null;
        if (d.Mode == "move")
        {
            b["x"] = J.R(ox + dx); b["y"] = J.R(oy + dy);
            if (o["tail"] is JsonObject t) b["tail"] = J.Pt(J.Num(t, "x") + dx, J.Num(t, "y") + dy);
            if (d.TextBefore != null) tm = Bubbles.MovedText(d.TextBefore, dx, dy);
        }
        else if (d.Mode == "tail") b["tail"] = J.Pt(e.X, e.Y);
        else
        {
            // 모서리 크기 조절: 반대쪽 모서리 고정
            double x0 = ox, y0 = oy, x1 = ox + ow, y1 = oy + oh;
            if (d.Mode.Contains('w')) x0 = Math.Min(x1 - 10, ox + dx); else x1 = Math.Max(x0 + 10, x1 + dx);
            if (d.Mode.Contains('n')) y0 = Math.Min(y1 - 10, oy + dy); else y1 = Math.Max(y0 + 10, y1 + dy);
            b["x"] = J.R(x0); b["y"] = J.R(y0); b["w"] = J.R(x1 - x0); b["h"] = J.R(y1 - y0);
            // 대사는 풍선 가운데를 따라간다
            if (d.TextBefore != null) tm = Bubbles.MovedText(d.TextBefore, (x0 + x1 - ox * 2 - ow) / 2, (y0 + y1 - oy * 2 - oh) / 2);
        }
        d.Prev.Show(meta, MetaLayers.Render);
        if (tm != null && d.TextPrev != null) d.TextPrev.Show(tm, MetaLayers.Render);
    }

    public override void Move(PointerArgs e)
    {
        if (drag != null) { DragMove(e); return; }
        if (st == null) return;
        if (st.Phase == "box") { st.X1 = e.X; st.Y1 = e.Y; }
        else st.TailP = new P2(e.X, e.Y);
    }

    public override void Hover(PointerArgs e) { if (st?.Phase == "tail") st.TailP = new P2(e.X, e.Y); }

    public override void Up(PointerArgs e)
    {
        if (drag != null)
        {
            var d = drag; drag = null;
            if (d.Moved)
            {
                var bm = J.Clone(d.L.Meta); var tm = d.TextPrev != null ? J.Clone(d.TextPrev.L.Meta) : null;
                d.Prev.Restore(); d.TextPrev?.Restore();
                string label = d.Mode == "move" ? "말풍선 이동" : d.Mode == "tail" ? "말풍선 꼬리" : "말풍선 크기";
                Bubbles.Commit(d.L, bm, label, d.TextPrev?.L, tm);
            }
            else { d.Prev.Restore(); d.TextPrev?.Restore(); }
            return;
        }
        if (st == null) return;
        if (st.Phase == "box")
        {
            var r = Geo.FromPoints(st.X0, st.Y0, st.X1, st.Y1);
            if (r.w < 10 || r.h < 10) { st = null; return; }
            if (Tail)
            {
                st.Phase = "tail";
                st.TailP = new P2(r.x + r.w * 0.3, r.y + r.h * 1.35);
                App.Toast("꼬리 끝을 탭(또는 드래그)하세요");
                return;
            }
        }
        CommitNew();
    }

    void CommitNew()
    {
        if (st == null) return;
        var b = NewBubble();
        string id = "b" + DateTime.Now.Ticks.ToString("x")[^8..] + rng.Next(10000);
        b["id"] = id;
        st = null;
        var d = App.Doc;
        var layer = Bubbles.LayerForNew(out bool created);
        if (created)
        {
            layer = MetaLayers.Add("말풍선", "bubble", new JsonObject { ["bubbles"] = new JsonArray(b) }, -1, "말풍선");
        }
        else
        {
            if (!App.CheckEditable(layer)) return;
            var meta = J.Clone(layer.Meta);
            ((JsonArray)meta["bubbles"]).Add(b);
            Bubbles.Commit(layer, meta, "말풍선");
        }
        sel = (layer, id);
        host?.Rebuild();
        if (AutoText)
        {
            double x = J.Num(b, "x"), y = J.Num(b, "y"), w = J.Num(b, "w"), h = J.Num(b, "h");
            var bl = layer;
            TextTool.CreateAt(x + w / 2, y + h / 2, center: true, also: t => LinkText(bl, id, t));
        }
    }

    /// <summary>풍선에 대사 레이어 연결 (구조 기록 안에서 부름)</summary>
    static void LinkText(Layer bl, string id, Layer t)
    {
        var b = Bubbles.List(bl.Meta).FirstOrDefault(x => J.Str(x, "id") == id);
        if (b != null) b["textId"] = t.Id;
    }

    /// <summary>고른 말풍선 속성 바꾸기</summary>
    void Edit(Action<JsonObject> fn, string label = "말풍선 수정")
    {
        if (Selected() == null) return;
        var layer = sel.Value.l;
        if (!App.CheckEditable(layer)) return;
        var meta = J.Clone(layer.Meta);
        var b = Bubbles.List(meta).First(x => J.Str(x, "id") == sel.Value.id);
        fn(b);
        Bubbles.Commit(layer, meta, label);
    }

    /// <summary>슬라이더: 끄는 동안 보여 주고 손을 떼면 한 번 기록</summary>
    void Live(string key, double v)
    {
        if (Selected() == null) return;
        var layer = sel.Value.l;
        livePrev ??= new MetaPreview(layer);
        var meta = livePrev.OrigMeta;
        var b = Bubbles.List(meta).FirstOrDefault(x => J.Str(x, "id") == sel.Value.id);
        if (b == null) return;
        b[key] = v;
        livePrev.Show(meta, MetaLayers.Render);
    }
    void EndLive(string label)
    {
        if (livePrev == null) return;
        var p = livePrev; livePrev = null;
        var meta = J.Clone(p.L.Meta); p.Restore();
        Bubbles.Commit(p.L, meta, label);
    }

    public bool DeleteSelected()
    {
        var b = Selected();
        if (b == null) return false;
        var layer = sel.Value.l; var d = App.Doc;
        if (!App.CheckEditable(layer)) return false;
        string tid = J.Str(b, "textId");
        var t = tid != null ? d.ById(tid) : null;
        var meta = J.Clone(layer.Meta);
        var arr = (JsonArray)meta["bubbles"];
        var rm = arr.OfType<JsonObject>().First(x => J.Str(x, "id") == sel.Value.id);
        arr.Remove(rm);
        Bubbles.Commit(layer, meta, "말풍선 삭제", also: () => { if (t != null) { d.Layers.Remove(t); if (d.ActiveId == t.Id) d.ActiveId = layer.Id; } });
        sel = null;
        host?.Rebuild();
        return true;
    }

    public override bool HandleKey(Keys k)
    {
        var key = k & Keys.KeyCode;
        if ((key == Keys.Delete || key == Keys.Back) && Selected() != null) { DeleteSelected(); return true; }
        if (key == Keys.Escape)
        {
            if (st != null) { st = null; MetaLayers.Canvas?.Present(); return true; }
            if (sel != null) { sel = null; host?.Rebuild(); MetaLayers.Canvas?.Present(); return true; }
        }
        if (key == Keys.Enter && st?.Phase == "tail") { CommitNew(); return true; }
        return false;
    }

    public override void Activate() { App.DocChanged -= Reset; App.DocChanged += Reset; }
    void Reset() { st = null; sel = null; drag = null; }
    public override void Deactivate() { if (livePrev != null) EndLive("말풍선 수정"); if (st?.Phase == "tail") CommitNew(); st = null; sel = null; drag = null; }
    public override void Cancel()
    {
        if (drag != null) { drag.Prev.Restore(); drag.TextPrev?.Restore(); App.InvalidateAll(); drag = null; }
        if (st?.Phase == "box") st = null;
    }

    public override Rectangle OverlayBounds(ViewXf v)
    {
        var pts = new List<PointF>();
        var b = Selected();
        if (b != null && st == null) foreach (var (_, p) in Handles(b)) pts.Add(Gfx.S(v, p));
        if (st != null) { var r = Bubbles.BoundsOf(NewBubble()); var sr = Gfx.ToScreen(v, r.X, r.Y, r.W, r.H); pts.Add(new PointF(sr.Left, sr.Top)); pts.Add(new PointF(sr.Right, sr.Bottom)); }
        return CUI.Bounds(pts, 12);
    }
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var b = Selected();
        if (b != null && st == null)
        {
            using var pen = CUI.Dash(Color.FromArgb(47, 124, 246), 1, 5, 4);
            var r = Gfx.ToScreen(v, J.Num(b, "x"), J.Num(b, "y"), J.Num(b, "w"), J.Num(b, "h"));
            g.DrawRectangle(pen, r);
            foreach (var (k, p) in Handles(b)) CUI.Handle(g, Gfx.S(v, p), k == "tail", k == "tail" ? Color.FromArgb(240, 140, 46) : null, k == "tail" ? 6 : 6);
        }
        if (st == null) return;
        // 만드는 중: 반투명 미리보기
        var nb = NewBubble();
        var bounds = Bubbles.BoundsOf(nb);
        var sr = Gfx.ToScreen(v, bounds.X, bounds.Y, bounds.W, bounds.H);
        if (sr.Width <= 0 || sr.Height <= 0 || sr.Width > 8000 || sr.Height > 8000) return;
        var state = g.Save();
        try
        {
            using var bmp = new Bitmap(Math.Max(1, sr.Width), Math.Max(1, sr.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var bg = Graphics.FromImage(bmp))
            {
                Gfx.Setup(bg);
                bg.TranslateTransform(-sr.X, -sr.Y);
                var (ox, oy) = v.ToScreen(0, 0); var (xx, xy) = v.ToScreen(1, 0); var (yx, yy) = v.ToScreen(0, 1);
                bg.MultiplyTransform(new Matrix((float)(xx - ox), (float)(xy - oy), (float)(yx - ox), (float)(yy - oy), (float)ox, (float)oy));
                Bubbles.DrawGroup(bg, new[] { nb });
            }
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.85f });
            g.DrawImage(bmp, sr, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
        }
        finally { g.Restore(state); }
    }

    public override Control BuildOptions() { host = new CUI.Host(Items); return host.Panel; }
    IEnumerable<Control> Items()
    {
        var shapes = new[] { ("ellipse", "타원"), ("rect", "사각"), ("shout", "외침"), ("cloud", "생각") };
        var b = Selected();
        if (b != null)
        {
            yield return Opt.Title("고른 말풍선");
            yield return CUI.Seg(shapes, () => J.Str(Selected(), "shape", "ellipse"), v => Edit(x => x["shape"] = v, "말풍선 모양"));
            yield return Opt.Slider("테두리 굵기", 0, 30, () => J.Num(Selected(), "line", 4), v => Live("line", v), step: 0.5, onEnd: () => EndLive("말풍선 테두리"));
            yield return Opt.Slider("꼬리 폭", 0.1, 1, () => J.Num(Selected(), "tailWidth", 0.45), v => Live("tailWidth", v), pct: true, step: 0.05, onEnd: () => EndLive("꼬리 폭"));
            yield return Opt.Row(CUI.ColorBtn("채우기", () => J.Str(Selected(), "fill", "#ffffff"), v => Edit(x => x["fill"] = v, "말풍선 색")),
                                 CUI.ColorBtn("테두리", () => J.Str(Selected(), "stroke", "#000000"), v => Edit(x => x["stroke"] = v, "말풍선 색")));
            yield return Opt.Row(
                Opt.Button(b["tail"] is JsonObject ? "꼬리 없애기" : "꼬리 달기", () =>
                {
                    Edit(x => x["tail"] = x["tail"] is JsonObject ? null : J.Pt(J.Num(x, "x") + J.Num(x, "w") * 0.3, J.Num(x, "y") + J.Num(x, "h") * 1.35), "말풍선 꼬리");
                    host?.Rebuild();
                }),
                Opt.Button("대사 편집", () =>
                {
                    var cur = Selected(); if (cur == null) return;
                    string tid = J.Str(cur, "textId"); var t = tid != null ? App.Doc.ById(tid) : null;
                    if (t?.Kind == "text") TextTool.Edit(t);
                    else { var bl = sel.Value.l; string id = sel.Value.id; TextTool.CreateAt(J.Num(cur, "x") + J.Num(cur, "w") / 2, J.Num(cur, "y") + J.Num(cur, "h") / 2, center: true, also: tl => LinkText(bl, id, tl)); }
                }),
                Opt.Button("삭제", () => DeleteSelected()));
            string grp = J.Str(b, "group");
            if (grp != null)
                yield return Opt.Button("이어 붙인 것 떼기", () => { UnGroup(grp); host?.Rebuild(); });
            else
                yield return Opt.Button("겹친 풍선과 이어 붙이기", Group);
            yield return Opt.Help("풍선 안을 드래그: 이동(대사도 함께) · 네 모서리: 크기 · 주황 점: 꼬리 끝 · Delete: 삭제");
        }
        yield return Opt.Title("새 말풍선");
        yield return CUI.Seg(shapes, () => Shape, v => Shape = v);
        yield return Opt.Slider("테두리 굵기", 0, 30, () => Line, v => Line = v, step: 0.5);
        yield return Opt.Check("꼬리", () => Tail, v => Tail = v);
        yield return Opt.Slider("꼬리 폭", 0.1, 1, () => TailWidth, v => TailWidth = v, pct: true, step: 0.05);
        yield return Opt.Row(CUI.ColorBtn("채우기", () => FillC, v => FillC = v), CUI.ColorBtn("테두리", () => StrokeC, v => StrokeC = v));
        yield return Opt.Check("만든 뒤 바로 대사 입력", () => AutoText, v => AutoText = v);
        yield return Opt.Help("빈 곳 드래그로 풍선 크기 → 꼬리 끝 탭. 만든 풍선은 탭해서 고른 뒤 이동·크기·꼬리·모양을 언제든 바꿀 수 있습니다.");
    }

    void UnGroup(string g)
    {
        if (Selected() == null) return;
        var layer = sel.Value.l;
        var meta = J.Clone(layer.Meta);
        foreach (var x in Bubbles.List(meta)) if (J.Str(x, "group") == g) x.Remove("group");
        Bubbles.Commit(layer, meta, "말풍선 떼기");
    }

    void Group()
    {
        var b = Selected(); if (b == null) return;
        var layer = sel.Value.l;
        var list = Bubbles.List(layer.Meta);
        var bb = Bubbles.BBox(b);
        var touching = list.Where(x => J.Str(x, "id") != J.Str(b, "id")).Where(x => { var o = Bubbles.BBox(x); return o.x0 < bb.x1 && bb.x0 < o.x1 && o.y0 < bb.y1 && bb.y0 < o.y1; }).Select(x => J.Str(x, "id")).ToHashSet();
        if (touching.Count == 0) { App.Toast("겹친 말풍선이 없습니다. 풍선을 조금 겹치게 옮긴 뒤 누르세요"); return; }
        var meta = J.Clone(layer.Meta);
        var all = Bubbles.List(meta);
        string g = J.Str(b, "group") ?? all.Where(x => touching.Contains(J.Str(x, "id"))).Select(x => J.Str(x, "group")).FirstOrDefault(x => x != null) ?? "g" + DateTime.Now.Ticks.ToString("x")[^8..];
        foreach (var x in all) if (J.Str(x, "id") == J.Str(b, "id") || touching.Contains(J.Str(x, "id"))) x["group"] = g;
        Bubbles.Commit(layer, meta, "말풍선 이어 붙이기");
        App.Toast($"{touching.Count + 1}개를 하나의 외곽선으로 이었습니다");
        host?.Rebuild();
    }
}
