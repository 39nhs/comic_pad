// 이동/변형 (웹 tools/transform.js): 선택 영역(없으면 레이어 전체)을 떼어 이동·확대·회전·기울이기·자유 변형(원근)·반전.
// 끄는 동안은 레이어 그림 면을 미리보기 면으로 잠시 바꿔 끼워 보여 주고, 확정할 때 고품질 보간(바이큐빅 + 축소 시 여러 점 평균)으로 한 번 그린다.
// 글자·컷 레이어는 정보(글자 행렬 mat, 컷 꼭짓점)를 변형해서 계속 편집할 수 있게 둔다 (웹 판과 같은 meta).
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

/// <summary>보간해서 다시 그리기</summary>
public static class Resample
{
    public const int Nearest = 0, Bilinear = 1, Bicubic = 2;

    /// <summary>src(sw×sh, 미리 곱한 알파)를 문서 영역 dst 로. inv(문서 x,y) → src 좌표 (픽셀 가운데 = +0.5). ss = 한 축 여러 점 평균 수</summary>
    public static uint[] Warp(uint[] src, int sw, int sh, Rect dst, Func<double, double, P2> inv, int mode, int ss = 1)
    {
        var o = new uint[dst.W * dst.H];
        ss = Math.Clamp(ss, 1, 6);
        Parallel.For(0, dst.H, y =>
        {
            for (int x = 0; x < dst.W; x++)
            {
                if (ss == 1)
                {
                    var p = inv(dst.X + x + 0.5, dst.Y + y + 0.5);
                    o[y * dst.W + x] = Sample(src, sw, sh, p.X - 0.5, p.Y - 0.5, mode);
                }
                else
                {
                    double a = 0, r = 0, g = 0, b = 0; int n = ss * ss;
                    for (int j = 0; j < ss; j++)
                        for (int i = 0; i < ss; i++)
                        {
                            var p = inv(dst.X + x + (i + 0.5) / ss, dst.Y + y + (j + 0.5) / ss);
                            uint c = Sample(src, sw, sh, p.X - 0.5, p.Y - 0.5, mode == Bicubic ? Bilinear : mode);
                            a += c >> 24; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255;
                        }
                    o[y * dst.W + x] = Px.Pack((uint)(a / n + 0.5), (uint)(r / n + 0.5), (uint)(g / n + 0.5), (uint)(b / n + 0.5));
                }
            }
        });
        return o;
    }

    static uint At(uint[] s, int w, int h, int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? s[y * w + x] : 0;

    public static uint Sample(uint[] s, int w, int h, double fx, double fy, int mode)
    {
        if (fx < -2 || fy < -2 || fx > w + 1 || fy > h + 1) return 0;
        if (mode == Nearest) return At(s, w, h, (int)Math.Floor(fx + 0.5), (int)Math.Floor(fy + 0.5));
        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
        double tx = fx - ix, ty = fy - iy;
        if (mode == Bilinear)
        {
            uint c00 = At(s, w, h, ix, iy), c10 = At(s, w, h, ix + 1, iy), c01 = At(s, w, h, ix, iy + 1), c11 = At(s, w, h, ix + 1, iy + 1);
            if ((c00 | c10 | c01 | c11) == 0) return 0;
            double w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
            uint Ch(int sh_) => (uint)Math.Clamp((int)(((c00 >> sh_) & 255) * w00 + ((c10 >> sh_) & 255) * w10 + ((c01 >> sh_) & 255) * w01 + ((c11 >> sh_) & 255) * w11 + 0.5), 0, 255);
            return Px.Pack(Ch(24), Ch(16), Ch(8), Ch(0));
        }
        // 바이큐빅 (Catmull-Rom)
        Span<double> wx = stackalloc double[4], wy = stackalloc double[4];
        Cub(tx, wx); Cub(ty, wy);
        double A = 0, R = 0, G = 0, B = 0; bool any = false;
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                uint c = At(s, w, h, ix - 1 + i, iy - 1 + j);
                if (c == 0) continue;
                any = true;
                double k = wx[i] * wy[j];
                A += (c >> 24) * k; R += ((c >> 16) & 255) * k; G += ((c >> 8) & 255) * k; B += (c & 255) * k;
            }
        if (!any) return 0;
        int a = Math.Clamp((int)(A + 0.5), 0, 255);
        if (a == 0) return 0;
        return Px.Pack((uint)a, (uint)Math.Clamp((int)(R + 0.5), 0, a), (uint)Math.Clamp((int)(G + 0.5), 0, a), (uint)Math.Clamp((int)(B + 0.5), 0, a));
    }
    static void Cub(double t, Span<double> w)
    {
        double t2 = t * t, t3 = t2 * t;
        w[0] = -0.5 * t3 + t2 - 0.5 * t; w[1] = 1.5 * t3 - 2.5 * t2 + 1; w[2] = -1.5 * t3 + 2 * t2 + 0.5 * t; w[3] = 0.5 * t3 - 0.5 * t2;
    }

    /// <summary>넓이 비율(문서 1px 당 원본 px)로 여러 점 평균 수</summary>
    public static int SuperFor(double areaRatio) => areaRatio >= 0.9 ? 1 : Math.Clamp((int)Math.Ceiling(1 / Math.Sqrt(Math.Max(1e-6, areaRatio))), 1, 4);

    /// <summary>buf 를 dst 위에 덮어 그림 (미리 곱한 알파)</summary>
    public static void OverInto(uint[] dst, uint[] src) { for (int i = 0; i < dst.Length; i++) { uint c = src[i]; if (c != 0) dst[i] = Px.Over(c, dst[i]); } }
}

public sealed class TransformTool : ToolBase
{
    public override string Id => "transform";
    public override string Name => "이동/변형";
    public override string Icon => "⤧";
    public override Keys Key => Keys.V;
    public override Cursor Cursor => Cursors.SizeAll;

    public bool KeepAspect = true;
    public string Mode = "affine";          // affine | distort
    public int Interp = Resample.Bicubic;

    // [이름, 손잡이(로컬 0~1), 기준점]
    static readonly (string n, double hx, double hy, double ax, double ay)[] HANDLES =
    {
        ("nw", 0, 0, 1, 1), ("ne", 1, 0, 0, 1), ("se", 1, 1, 0, 0), ("sw", 0, 1, 1, 0),
        ("n", 0.5, 0, 0.5, 1), ("e", 1, 0.5, 0, 0.5), ("s", 0.5, 1, 0.5, 0), ("w", 0, 0.5, 1, 0.5),
    };

    sealed class State
    {
        public Layer Layer; public bool OnMask;
        public Surface Orig, Base, Prev;          // 원래 면, 떼어 낸 뒤 면, 미리보기 면
        public uint[] Float; public int W, H; public Rect O;   // 떼어 낸 그림, 원래 자리
        public double Cx, Cy, Sx = 1, Sy = 1, Rot;
        public P2[] Corners;                       // 자유 변형 (왼위, 오른위, 오른아래, 왼아래)
        public byte[] SelSnap;
        public bool MetaMode; public JsonObject PrevMeta;
        public Rect Last = Rect.Empty;
    }
    State st;
    sealed class Drag { public string Mode; public string H; public int I; public P2 Start; public P2[] Orig; public double Cx, Cy, Sx, Sy, Rot; public Mat2 M0; public bool Skew; }
    Drag drag;
    bool selfPush;
    CUI.Host host;

    public TransformTool()
    {
        App.DocChanged += () => { st = null; drag = null; };
        App.History.Changed += () =>
        {
            // 변형 중에 실행취소 등으로 기록이 바뀌면 변형을 취소 (원래 면을 돌려놓음)
            if (st != null && !selfPush) { var s = st; st = null; SetTarget(s, s.Orig); App.InvalidateAll(); }
        };
    }

    public override bool Busy => drag != null;

    public override void Activate() { Start(); }
    public override void Deactivate() { Commit(); }

    static void SetTarget(State s, Surface v) { if (s.OnMask) s.Layer.Mask = v; else s.Layer.Px = v; s.Layer.Touch(); }

    public void Start()
    {
        if (st != null) return;
        var d = App.Doc;
        var layer = d?.Active;
        if (layer == null || !App.CheckEditable(layer)) return;
        if (layer.Kind == "bubble" && layer.Meta?["bubbles"] is JsonArray) { App.Toast("말풍선 레이어는 말풍선 도구로 고치세요 (직접 그리려면 레이어 메뉴 > 말풍선을 그림으로)"); return; }
        var sel = d.Sel;
        var T = layer.Target;
        var b = sel.Active ? sel.Bounds().Intersect(T.Bounds()) : T.Bounds();
        if (b.IsEmpty) { App.Toast("변형할 그림이 없습니다"); return; }
        bool metaKind = layer.Kind is "text" or "panel" or "vector" && layer.Meta != null && !layer.EditMask;
        if (metaKind && sel.Active)
        {
            App.History.Struct(d, "일반 레이어로 변환", () => { layer.Kind = "raster"; layer.Meta = null; });
            App.Toast("선택 영역만 변형하므로 일반 그림 레이어로 바꿨습니다");
            App.NotifyLayers();
            metaKind = false;
        }
        var fl = T.Read(b);
        var bas = T.Clone();
        if (sel.Active)
        {
            var rest = bas.Read(b);
            for (int y = 0; y < b.H; y++)
                for (int x = 0; x < b.W; x++)
                {
                    int i = y * b.W + x; byte m = sel.At(b.X + x, b.Y + y);
                    fl[i] = Px.Scale(fl[i], m); rest[i] = Px.Scale(rest[i], 255u - m);
                }
            bas.Write(b, rest);
        }
        else bas.Clear(b);
        st = new State
        {
            Layer = layer, OnMask = layer.EditMask && layer.Mask != null, Orig = T, Base = bas, Prev = bas.Clone(), Float = fl, W = b.W, H = b.H, O = b,
            Cx = b.X + b.W / 2.0, Cy = b.Y + b.H / 2.0, SelSnap = sel.Snapshot(), MetaMode = metaKind, PrevMeta = J.Clone(layer.Meta),
        };
        if (sel.Active) { sel.None(); MetaLayers.Redraw(); }
        SetTarget(st, st.Prev);
        if (Mode == "distort") st.Corners = Corners();
        Update();
        host?.Rebuild();
    }

    Mat2 Matrix() { var s = st; return Mat2.Translate(s.Cx, s.Cy).Mul(Mat2.Rotate(s.Rot)).Mul(Mat2.Scale(s.Sx, s.Sy)).Mul(Mat2.Translate(-s.W / 2.0, -s.H / 2.0)); }

    P2[] Corners()
    {
        if (st.Corners != null) return st.Corners;
        var m = Matrix();
        return new[] { m.Apply(0, 0), m.Apply(st.W, 0), m.Apply(st.W, st.H), m.Apply(0, st.H) };
    }

    /// <summary>변형한 그림 (문서 영역 r)</summary>
    (Rect r, uint[] px) Render(bool hq)
    {
        var s = st; var q = Corners();
        var r = Geo.BoundsRect(q, 2).Intersect(new Rect(-Gfx.MaxSide, -Gfx.MaxSide, Gfx.MaxSide * 3, Gfx.MaxSide * 3)).Clip(App.Doc.W, App.Doc.H);
        if (r.IsEmpty) return (r, Array.Empty<uint>());
        Func<double, double, P2> inv; double area;
        if (s.Corners != null)
        {
            var qm = new QuadMap(q[0], q[1], q[2], q[3]);
            inv = (x, y) => { var uv = qm.Inverse(x, y); return new P2(uv.X * s.W, uv.Y * s.H); };
            area = Geo.Area(q) / Math.Max(1, (double)s.W * s.H);
        }
        else
        {
            var m = Matrix().Inverse();
            inv = (x, y) => m.Apply(x, y);
            area = Math.Abs(Matrix().Det);
        }
        int mode = hq ? Interp : Math.Min(Interp, Resample.Bilinear);
        int ss = Interp == Resample.Nearest ? 1 : Resample.SuperFor(area);
        if (!hq) ss = Math.Min(ss, 2);
        return (r, Resample.Warp(s.Float, s.W, s.H, r, inv, mode, ss));
    }

    void Update()
    {
        var s = st; if (s == null) return;
        // 지난번 자리를 원래(떼어 낸 뒤) 그림으로 되돌리고 새 자리에 그림
        if (!s.Last.IsEmpty) s.Prev.Write(s.Last, s.Base.Read(s.Last));
        var (r, px) = Render(false);
        if (!r.IsEmpty) { var under = s.Base.Read(r); Resample.OverInto(under, px); s.Prev.Write(r, under); }
        s.Layer.Touch();
        var inv = s.Last.Union(r).Union(s.O);
        s.Last = r;
        App.Invalidate(inv.IsEmpty ? new Rect(0, 0, 1, 1) : inv.Inflate(2));
    }

    public void SetMode(string mode)
    {
        Mode = mode;
        var s = st;
        if (s != null)
        {
            if (mode == "distort" && s.Corners == null) s.Corners = Corners().ToArray();
            else if (mode == "affine" && s.Corners != null) { Commit(); Start(); return; }
            Update();
        }
        host?.Rebuild();
    }

    public void Flip(char axis)
    {
        var s = st; if (s == null) return;
        if (s.Corners != null) { var c = s.Corners; s.Corners = axis == 'h' ? new[] { c[1], c[0], c[3], c[2] } : new[] { c[3], c[2], c[1], c[0] }; }
        else if (axis == 'h') s.Sx *= -1; else s.Sy *= -1;
        Update();
    }
    public void Rotate90()
    {
        var s = st; if (s == null) return;
        if (s.Corners != null) { var c = s.Corners; s.Corners = new[] { c[3], c[0], c[1], c[2] }; }
        else s.Rot = (s.Rot + 90) % 360;
        Update();
    }

    /// <summary>문서 점 변환 (원래 자리 → 변형 뒤)</summary>
    Func<P2, P2> ToDoc()
    {
        var s = st;
        if (s.Corners != null) { var q = s.Corners; var qm = new QuadMap(q[0], q[1], q[2], q[3]); return p => qm.Map((p.X - s.O.X) / s.W, (p.Y - s.O.Y) / s.H); }
        var M = Matrix().Mul(Mat2.Translate(-s.O.X, -s.O.Y));
        return p => M.Apply(p);
    }

    public void Commit()
    {
        var s = st; if (s == null) return;
        drag = null;
        selfPush = true;
        try
        {
            var d = App.Doc;
            SetTarget(s, s.Orig);
            var l = s.Layer;
            if (s.MetaMode && l.Kind == "text" && s.Corners == null)
            {
                var A = Matrix().Mul(Mat2.Translate(-s.O.X, -s.O.Y));
                var meta = J.Clone(s.PrevMeta);
                var old = Mat2.From(J.Doubles(meta["mat"]));
                meta["mat"] = J.ArrOf(A.Mul(old).Arr());
                st = null;
                MetaLayers.Commit(l, "변형", meta, MetaLayers.Render);
                End(s); return;
            }
            if (s.MetaMode && l.Kind == "panel")
            {
                var f = ToDoc();
                var meta = J.Clone(s.PrevMeta);
                meta["panels"] = new JsonArray(Panels.List(meta).Select(q => (JsonNode)Panels.PanelJson(q.Select(f).ToList())).ToArray());
                st = null;
                MetaLayers.Commit(l, "변형", meta, MetaLayers.Render);
                End(s); return;
            }
            // 그림으로 확정
            var pe = App.History.BeginPixels(l, "변형");
            var (r, px) = Render(true);
            pe.Touch(s.O.Union(r));
            s.Orig.Write(s.O, s.Base.Read(s.O));
            if (!r.IsEmpty) { var under = s.Orig.Read(r); Resample.OverInto(under, px); s.Orig.Write(r, under); }
            var pix = pe.Finish("변형");
            var edits = new List<IEdit> { pix };
            if (s.SelSnap != null) edits.Add(new SelEdit("변형", s.SelSnap, null));
            if (s.MetaMode && l.Kind == "vector")
            {
                // 벡터 선 정보도 같이 옮김 (웹 transformStrokes 와 같음: pts 는 [x,y,p,t] 넷씩)
                var before = StructSnap.Take(d);
                var meta = J.Clone(s.PrevMeta);
                var f = ToDoc(); double k = s.Corners != null ? 1 : Math.Sqrt(Math.Abs(Matrix().Det));
                if (meta?["strokes"] is JsonArray strokes)
                    foreach (var so in strokes.OfType<JsonObject>())
                    {
                        if (so["pts"] is JsonArray p) for (int i = 0; i + 1 < p.Count; i += 4) { var q = f(new P2(J.Num(p[i]), J.Num(p[i + 1]))); p[i] = J.R(q.X); p[i + 1] = J.R(q.Y); }
                        if (so["preset"] is JsonObject pr && pr["size"] != null) pr["size"] = J.R(J.Num(pr, "size") * k);
                    }
                l.Meta = meta;
                edits.Add(new StructEdit("변형", before, StructSnap.Take(d)));
            }
            else if (s.MetaMode && l.Kind == "text")
            {
                // 글자 원근 변형: 일반 그림 레이어로
                var before = StructSnap.Take(d);
                l.Kind = "raster"; l.Meta = null;
                edits.Add(new StructEdit("변형", before, StructSnap.Take(d)));
                App.Toast("글자를 원근 변형해서 일반 그림 레이어가 되었습니다");
            }
            App.History.PushGroup("변형", edits.ToArray());
            d.Modified = true;
            st = null;
            App.Invalidate(s.O.Union(r).Union(s.Last).Inflate(2));
            App.NotifyLayers();
            End(s);
        }
        finally { selfPush = false; }
    }

    public void CancelTransform()
    {
        var s = st; if (s == null) return;
        SetTarget(s, s.Orig);
        if (s.SelSnap != null) App.Doc.Sel.Restore(s.SelSnap);
        st = null;
        App.Invalidate(s.O.Union(s.Last).Inflate(2));
        End(s);
    }

    void End(State s) { drag = null; MetaLayers.Redraw(); host?.Rebuild(); }

    // ---- 손잡이 ----
    List<(string n, int i, PointF p)> HandlePos(ViewXf v)
    {
        var o = new List<(string, int, PointF)>();
        if (st.Corners != null)
        {
            var c = st.Corners;
            for (int i = 0; i < 4; i++) o.Add(("c", i, Gfx.S(v, c[i])));
            for (int i = 0; i < 4; i++) o.Add(("e", i, Gfx.S(v, (c[i] + c[(i + 1) % 4]) * 0.5)));
            return o;
        }
        var m = Matrix();
        foreach (var hd in HANDLES) o.Add((hd.n, 0, Gfx.S(v, m.Apply(hd.hx * st.W, hd.hy * st.H))));
        var top = o.First(x => x.Item1 == "n").Item3; var cc = Gfx.S(v, st.Cx, st.Cy);
        double dx = top.X - cc.X, dy = top.Y - cc.Y, L = Math.Sqrt(dx * dx + dy * dy); if (L < 1e-6) L = 1;
        o.Add(("rot", 0, new PointF((float)(top.X + dx / L * 36), (float)(top.Y + dy / L * 36))));
        return o;
    }

    bool Inside(double x, double y)
    {
        if (st.Corners != null) return Geo.InPoly(st.Corners, x, y);
        var p = Matrix().Inverse().Apply(x, y);
        return p.X >= 0 && p.Y >= 0 && p.X <= st.W && p.Y <= st.H;
    }

    public override void Down(PointerArgs e)
    {
        if (st == null) { Start(); if (st == null) return; }
        var v = MetaLayers.Canvas?.V; if (v == null) return;
        double rad = e.Type == "touch" ? 26 : 16;
        var hits = HandlePos(v).Where(h => Math.Sqrt((h.p.X - e.SX) * (h.p.X - e.SX) + (h.p.Y - e.SY) * (h.p.Y - e.SY)) <= rad).ToList();
        var hit = hits.Count > 0 ? hits[0] : default;
        bool has = hits.Count > 0;
        var s = st;
        // Ctrl + 변 가운데 손잡이 = 기울이기 (자유 변형으로 바꿔서 그 변을 변 방향으로만)
        if (has && e.Ctrl && s.Corners == null && hit.n is "n" or "e" or "s" or "w")
        {
            s.Corners = Corners().ToArray(); Mode = "distort"; host?.Rebuild();
            int edge = hit.n switch { "n" => 0, "e" => 1, "s" => 2, _ => 3 };
            drag = new Drag { Mode = "edge", I = edge, Orig = s.Corners.ToArray(), Start = new P2(e.X, e.Y), Skew = true };
            return;
        }
        if (s.Corners != null)
        {
            var orig = s.Corners.ToArray();
            if (has && hit.n == "c") drag = new Drag { Mode = "corner", I = hit.i, Orig = orig, Start = new P2(e.X, e.Y) };
            else if (has && hit.n == "e") drag = new Drag { Mode = "edge", I = hit.i, Orig = orig, Start = new P2(e.X, e.Y), Skew = e.Ctrl };
            else if (Inside(e.X, e.Y)) drag = new Drag { Mode = "quad", Orig = orig, Start = new P2(e.X, e.Y) };
            return;
        }
        var bd = new Drag { Start = new P2(e.X, e.Y), Cx = s.Cx, Cy = s.Cy, Sx = s.Sx, Sy = s.Sy, Rot = s.Rot, M0 = Matrix() };
        if (has && hit.n != "rot") { bd.Mode = "scale"; bd.H = hit.n; }
        else if (has || !Inside(e.X, e.Y)) bd.Mode = "rotate";
        else bd.Mode = "move";
        drag = bd;
    }

    public override void Move(PointerArgs e)
    {
        var d = drag; var s = st;
        if (d == null || s == null) return;
        if (d.Mode is "corner" or "quad" or "edge")
        {
            double dx = e.X - d.Start.X, dy = e.Y - d.Start.Y;
            if (d.Mode == "edge" && (d.Skew || e.Ctrl))
            {
                var a = d.Orig[d.I]; var b = d.Orig[(d.I + 1) % 4]; var u = (b - a).Norm();
                double t = dx * u.X + dy * u.Y; dx = u.X * t; dy = u.Y * t;
            }
            s.Corners = d.Orig.Select((p, i) => d.Mode == "quad" || i == d.I || (d.Mode == "edge" && i == (d.I + 1) % 4) ? new P2(p.X + dx, p.Y + dy) : p).ToArray();
            Update();
            return;
        }
        if (d.Mode == "move") { s.Cx = d.Cx + e.X - d.Start.X; s.Cy = d.Cy + e.Y - d.Start.Y; }
        else if (d.Mode == "rotate")
        {
            double a0 = Math.Atan2(d.Start.Y - d.Cy, d.Start.X - d.Cx), a1 = Math.Atan2(e.Y - d.Cy, e.X - d.Cx);
            double rot = d.Rot + (a1 - a0) * 180 / Math.PI;
            if (e.Shift) rot = Math.Round(rot / 15) * 15;
            s.Rot = rot;
        }
        else
        {
            var hd = HANDLES.First(x => x.n == d.H);
            double Hx = hd.hx * s.W, Hy = hd.hy * s.H, Ax = hd.ax * s.W, Ay = hd.ay * s.H;
            var A = d.M0.Apply(Ax, Ay);
            double rad = d.Rot * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
            double vx0 = e.X - A.X, vy0 = e.Y - A.Y;
            double vx = vx0 * cos + vy0 * sin, vy = -vx0 * sin + vy0 * cos;
            double sx = Hx != Ax ? vx / (Hx - Ax) : d.Sx;
            double sy = Hy != Ay ? vy / (Hy - Ay) : d.Sy;
            bool corner = Hx != Ax && Hy != Ay;
            bool keep = corner && (KeepAspect != e.Shift);
            if (keep)
            {
                double k = Math.Max(Math.Abs(sx / d.Sx), Math.Abs(sy / d.Sy));
                sx = Sign(sx) * Math.Abs(d.Sx) * k; sy = Sign(sy) * Math.Abs(d.Sy) * k;
            }
            if (Math.Abs(sx * s.W) < 1) sx = Sign(sx) / s.W;
            if (Math.Abs(sy * s.H) < 1) sy = Sign(sy) / s.H;
            s.Sx = sx; s.Sy = sy;
            double lx = (Ax - s.W / 2.0) * sx, ly = (Ay - s.H / 2.0) * sy;
            s.Cx = A.X - (lx * cos - ly * sin);
            s.Cy = A.Y - (lx * sin + ly * cos);
        }
        Update();
    }
    static double Sign(double v) => v < 0 ? -1 : 1;

    public override void Up(PointerArgs e) { drag = null; App.NotifySettings(); }
    public override void Cancel() { drag = null; }

    public override bool HandleKey(Keys k)
    {
        if (st == null) return false;
        var key = k & Keys.KeyCode;
        if (key == Keys.Enter) { Commit(); return true; }
        if (key == Keys.Escape) { CancelTransform(); return true; }
        if (key is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            double step = (k & Keys.Shift) != 0 ? 10 : 1;
            double dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0, dy = key == Keys.Up ? -step : key == Keys.Down ? step : 0;
            if (st.Corners != null) st.Corners = st.Corners.Select(p => new P2(p.X + dx, p.Y + dy)).ToArray();
            else { st.Cx += dx; st.Cy += dy; }
            Update(); MetaLayers.Canvas?.Present();
            return true;
        }
        return false;
    }

    // ---- 화면 ----
    public override Rectangle OverlayBounds(ViewXf v) => st == null ? Rectangle.Empty : CUI.Bounds(HandlePos(v).Select(h => h.p).Concat(Corners().Select(p => Gfx.S(v, p))), 12);
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (st == null) return;
        var pts = Corners().Select(p => Gfx.S(v, p)).ToArray();
        using var pen = new Pen(Color.FromArgb(47, 124, 246), 1.5f);
        g.DrawPolygon(pen, pts);
        var hp = HandlePos(v);
        var top = hp.FirstOrDefault(o => o.n == "n"); var rot = hp.FirstOrDefault(o => o.n == "rot");
        if (top.n != null && rot.n != null) g.DrawLine(pen, top.p, rot.p);
        foreach (var h in hp) CUI.Handle(g, h.p, h.n == "rot" || h.n == "e", h.n == "e" ? Color.FromArgb(220, 230, 240) : null, h.n == "e" ? 4 : 5);
    }

    public override Control BuildOptions()
    {
        host = new CUI.Host(Items);
        return host.Panel;
    }
    IEnumerable<Control> Items()
    {
        yield return Opt.Title("변형");
        yield return CUI.Seg(new[] { ("affine", "이동·크기·회전"), ("distort", "자유 변형 (원근)") }, () => Mode, v => SetMode(v));
        yield return Opt.Check("모서리: 비율 유지", () => KeepAspect, v => KeepAspect = v);
        yield return Opt.Choice("보간", new[] { ("2", "고품질 (바이큐빅)"), ("1", "부드럽게 (바이리니어)"), ("0", "픽셀 그대로 (도트)") }, () => Interp.ToString(), v => { Interp = int.Parse(v); if (st != null) Update(); });
        if (st != null && st.Corners == null)
        {
            yield return Opt.Slider("가로 배율", 0.01, 4, () => Math.Abs(st?.Sx ?? 1), v => { if (st != null) { st.Sx = Sign(st.Sx) * v; Update(); } }, pct: true);
            yield return Opt.Slider("세로 배율", 0.01, 4, () => Math.Abs(st?.Sy ?? 1), v => { if (st != null) { st.Sy = Sign(st.Sy) * v; Update(); } }, pct: true);
            yield return Opt.Slider("회전", -180, 180, () => st?.Rot ?? 0, v => { if (st != null) { st.Rot = v; Update(); } }, step: 1);
        }
        yield return Opt.Row(Opt.Button("✓ 확정", Commit, true), Opt.Button("취소", CancelTransform), Opt.Button("다시 시작", () => { Commit(); Start(); }));
        yield return Opt.Row(Opt.Button("좌우", () => Flip('h')), Opt.Button("상하", () => Flip('v')), Opt.Button("90°", Rotate90));
        if (st == null) yield return Opt.Button("변형 시작", Start);
        yield return Opt.Help("안쪽 드래그: 이동 · 바깥 드래그: 회전 · 모서리: 크기 · Shift: 비율/15° 스냅 · Ctrl+변 손잡이: 기울이기 · 화살표: 1px(Shift 10px) · Enter 확정 · Esc 취소");
    }
}
