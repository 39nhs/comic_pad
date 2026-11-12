// 도형 도구 (웹 tools/shape.js): 직선 / 사각형 / 타원 / 꺾은선 — 현재 브러시(펜) 굵기·딱딱함·불투명도와 전경색으로.
// 선은 브러시 엔진(Engine.Stroke)으로 따로 면에 "덮인 정도" 만 그리고, 채우기(GDI+ 안티앨리어싱)와 합쳐 한 번에 얹는다 → 겹친 곳이 진해지지 않음.
// 유선(평행 효과선, 웹 tools/retouch.js)과 플래시(우니/베타)도 여기.
using System.Drawing.Drawing2D;
using ComicPadNative.Core;
using ComicPadNative.Engine;
using ComicPadNative.Tools;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

/// <summary>선·채우기를 덮인 정도로 그려 레이어에 얹는 도우미 (도형·다른 모듈에서도 씀)</summary>
public sealed class ShapePainter
{
    readonly Document doc;
    readonly Layer layer;
    readonly Surface scratch;
    readonly Document dummy;
    PixelEdit pe;
    Rect last = Rect.Empty, scratchUsed = Rect.Empty;
    public BrushParams P;
    public uint Color;
    public float Opacity = 1;

    public ShapePainter(Document d, Layer l, BrushParams brush, uint color)
    {
        doc = d; layer = l; Color = color;
        scratch = new Surface(d.W, d.H);
        dummy = new Document(d.W, d.H);
        P = new BrushParams
        {
            Size = brush?.Size ?? 6, Hardness = brush?.Hardness ?? 1, Opacity = 1, MinSize = brush?.MinSize ?? 0.04,
            PressureSize = true, PressureOpacity = false, Stabilizer = 0, Spacing = brush?.Spacing ?? 0.05, Curve = p => Math.Clamp(p, 0, 1),
        };
        Opacity = (float)(brush?.Opacity ?? 1);
    }

    /// <summary>현재 펜 설정 (Brushes 모듈이 바꿔 끼운 브러시도)</summary>
    public static BrushParams CurrentBrush() => (Registry.Tools.Find(t => t.Id == "brush") as BrushTool)?.Params;

    /// <summary>모양 다시 그리기 (지난번 것은 되돌리고). strokes = (점들(압력 p 포함), 닫힘), fills = 채울 다각형</summary>
    public Rect Draw(IEnumerable<(IList<(P2 p, double pr)> pts, bool closed)> strokes, IEnumerable<IList<P2>> fills)
    {
        pe?.Cancel();
        if (!scratchUsed.IsEmpty) scratch.Clear(scratchUsed);
        scratchUsed = Rect.Empty;
        pe = App.History.BeginPixels(layer, "도형");
        var r = Rect.Empty;
        var hist = new History(); var dummyLayer = new Layer();
        foreach (var (pts, closed) in strokes)
        {
            if (pts.Count == 0) continue;
            // 모양마다 따로 기록 → 앞 모양 위에 겹쳐 그림 (대칭 사본끼리 지우지 않게)
            var cover = new PixelEdit(hist, dummyLayer, scratch, "");
            var st = new Stroke(dummy, cover, P, 0xFFFFFFFF, false, false);
            var seq = new List<(P2 p, double pr)>(pts);
            if (closed && seq.Count > 2) seq.Add(seq[0]);
            st.Begin(new Pt(seq[0].p.X, seq[0].p.Y, seq[0].pr, 0));
            for (int i = 1; i < seq.Count; i++)
            {
                var q = new Pt(seq[i].p.X, seq[i].p.Y, seq[i].pr, i);
                st.Add(q);
                if (i < seq.Count - 1) st.Add(q);   // 꼭짓점을 두 번 → 곡선으로 깎이지 않고 곧게
            }
            st.End(new Pt(seq[^1].p.X, seq[^1].p.Y, seq[^1].pr, seq.Count));
            r = r.Union(cover.Dirty);
        }
        scratchUsed = r;
        byte[] fillCov = null; Rect fr = Rect.Empty;
        var fl = fills?.Where(f => f.Count >= 3).ToList() ?? new();
        if (fl.Count > 0)
        {
            fr = Geo.BoundsRect(fl.SelectMany(f => f), 2).Clip(doc.W, doc.H);
            if (!fr.IsEmpty) fillCov = Gfx.Coverage(fr, (g, br) => { foreach (var f in fl) { using var p = Geo.Poly(f.ToList()); g.FillPath(br, p); } });
            r = r.Union(fr);
        }
        r = r.Clip(doc.W, doc.H);
        if (!r.IsEmpty)
        {
            pe.Touch(r);
            var T = layer.Target;
            var buf = T.Read(r);
            var sc = scratch.Read(r);
            var sel = doc.Sel.Mask;
            bool alphaLock = layer.AlphaLock && !layer.EditMask;
            uint col = layer.EditMask ? 0xFFFFFFFF : Color;
            uint k = (uint)Math.Clamp((int)(Opacity * 255 + 0.5), 0, 255);
            for (int y = 0; y < r.H; y++)
                for (int x = 0; x < r.W; x++)
                {
                    int i = y * r.W + x;
                    uint c = sc[i] >> 24;
                    if (fillCov != null && fr.Contains(r.X + x, r.Y + y)) c = Math.Max(c, fillCov[(r.Y + y - fr.Y) * fr.W + (r.X + x - fr.X)]);
                    if (c == 0) continue;
                    c = c * k / 255;
                    if (sel != null) c = c * sel[(r.Y + y) * doc.W + r.X + x] / 255;
                    if (c == 0) continue;
                    var s = Px.Scale(col, c);
                    buf[i] = alphaLock ? Px.Composite(s, buf[i], Core.Blend.Atop) : Px.Over(s, buf[i]);
                }
            T.Write(r, buf);
        }
        var inv = last.Union(r);
        last = r;
        if (!inv.IsEmpty) App.Invalidate(inv);
        return r;
    }

    public void Commit(string label = "도형")
    {
        if (pe == null) return;
        if (!scratchUsed.IsEmpty) scratch.Clear(scratchUsed);
        pe.Commit(label);
        pe = null;
        doc.Modified = true;
        App.NotifyLayers();
    }
    public void Cancel()
    {
        pe?.Cancel(); pe = null;
        if (!last.IsEmpty) App.Invalidate(last);
        last = Rect.Empty;
    }
}

public sealed class ShapeTool : ToolBase
{
    public override string Id => "shape";
    public override string Name => "도형";
    public override string Icon => "▭";
    public override Keys Key => Keys.U;
    public override bool DrawsSizeCircle => true;
    public override double CircleSize => ShapePainter.CurrentBrush()?.Size ?? 6;

    public string Shape = "line", Paint = "stroke";
    public bool KeepRatio, FromCenter; public double Taper;
    ShapePainter sp;
    double x0, y0;
    List<P2> poly; P2 cursorPt;
    CUI.Host host;
    public override bool Busy => sp != null && poly == null;

    (List<(P2, double)> pts, bool closed) Points(double ax, double ay, double bx, double by)
    {
        if (Shape == "line")
        {
            int n = 24; var pts = new List<(P2, double)>();
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                // 입출 보정: 양끝을 가늘게
                double p = Taper > 0 ? Math.Min(1, Math.Min(t, 1 - t) / (Taper / 2) + 0.05) : 1;
                pts.Add((new P2(ax + (bx - ax) * t, ay + (by - ay) * t), p));
            }
            return (pts, false);
        }
        var r = Geo.FromPoints(ax, ay, bx, by);
        if (KeepRatio) { double s = Math.Max(r.w, r.h); r = (bx < ax ? ax - s : ax, by < ay ? ay - s : ay, s, s); }
        if (FromCenter) r = (ax - Math.Abs(bx - ax), ay - Math.Abs(by - ay), Math.Abs(bx - ax) * 2, Math.Abs(by - ay) * 2);
        if (Shape == "rect") return (Panels.RectPts(r.x, r.y, r.w, r.h).Select(p => (p, 1.0)).ToList(), true);
        double per = Math.PI * (r.w + r.h) / 2;
        int m = (int)Math.Max(24, Math.Min(720, Math.Round(per / 3)));
        var e = new List<(P2, double)>();
        for (int i = 0; i < m; i++) { double a = (double)i / m * Math.PI * 2; e.Add((new P2(r.x + r.w / 2 + Math.Cos(a) * r.w / 2, r.y + r.h / 2 + Math.Sin(a) * r.h / 2), 1.0)); }
        return (e, true);
    }

    void Draw(List<(P2 p, double pr)> pts, bool closed)
    {
        var d = App.Doc;
        var maps = new List<Func<P2, P2>> { p => p };
        maps.AddRange(Symmetry.Maps(d.W, d.H));
        var strokes = new List<(IList<(P2, double)>, bool)>();
        var fills = new List<IList<P2>>();
        foreach (var f in maps)
        {
            var q = pts.Select(x => (f(x.p), x.pr)).ToList();
            if (!closed || Paint != "fill") strokes.Add((q, closed));
            if (closed && Paint != "stroke") fills.Add(q.Select(x => x.Item1).ToList());
        }
        sp.Draw(strokes, fills);
    }

    bool Begin()
    {
        var l = App.Active;
        if (!App.CheckEditable(l)) return false;
        if (l.Kind is "bubble" or "text" or "panel" && l.Meta != null && !l.EditMask) { App.Toast("이 레이어는 해당 도구로 고치세요 (일반 레이어에 그리세요)"); return false; }
        sp = new ShapePainter(App.Doc, l, ShapePainter.CurrentBrush(), App.Fg);
        return true;
    }

    public override void Down(PointerArgs e)
    {
        if (Shape == "polyline")
        {
            if (sp == null && !Begin()) return;
            poly ??= new();
            poly.Add(new P2(e.X, e.Y));
            cursorPt = new P2(e.X, e.Y);
            DrawPoly();
            return;
        }
        if (!Begin()) return;
        x0 = e.X; y0 = e.Y;
    }
    void DrawPoly() => Draw(poly.Append(cursorPt).Select(p => (p, 1.0)).ToList(), false);

    public override void Move(PointerArgs e)
    {
        if (sp == null) return;
        if (Shape == "polyline") { if (poly != null) { cursorPt = new P2(e.X, e.Y); DrawPoly(); } return; }
        double x = e.X, y = e.Y;
        if (Shape == "line" && e.Shift)
        {
            double a = Math.Atan2(y - y0, x - x0), s = Math.Round(a / (Math.PI / 4)) * (Math.PI / 4), dd = Math.Sqrt((x - x0) * (x - x0) + (y - y0) * (y - y0));
            x = x0 + Math.Cos(s) * dd; y = y0 + Math.Sin(s) * dd;
        }
        var (pts, closed) = Points(x0, y0, x, y);
        Draw(pts, closed);
    }

    public override void Hover(PointerArgs e) { if (Shape == "polyline" && poly != null && sp != null) { cursorPt = new P2(e.X, e.Y); DrawPoly(); } }
    public override void Up(PointerArgs e) { if (Shape == "polyline") return; Finish(); }

    public void FinishPolyline(bool close = false)
    {
        if (poly == null || sp == null) return;
        if (poly.Count > 1) Draw(poly.Select(p => (p, 1.0)).ToList(), close);
        else sp.Draw(Array.Empty<(IList<(P2, double)>, bool)>(), null);
        poly = null;
        Finish();
    }
    void Finish() { sp?.Commit("도형"); sp = null; }

    public override void Deactivate() { if (poly != null) FinishPolyline(); else if (sp != null) Finish(); }
    public override void Cancel() { sp?.Cancel(); sp = null; poly = null; }
    public override bool HandleKey(Keys k)
    {
        if (poly == null) return false;
        if ((k & Keys.KeyCode) == Keys.Enter) { FinishPolyline(false); return true; }
        if ((k & Keys.KeyCode) == Keys.Escape) { sp?.Cancel(); sp = null; poly = null; return true; }
        return false;
    }

    public override Control BuildOptions() { host = new CUI.Host(Items); return host.Panel; }
    IEnumerable<Control> Items()
    {
        yield return Opt.Title("도형");
        yield return CUI.Seg(new[] { ("line", "직선"), ("rect", "사각형"), ("ellipse", "타원"), ("polyline", "꺾은선") }, () => Shape, v => { if (poly != null) FinishPolyline(); Shape = v; host.Rebuild(); });
        if (Shape is "rect" or "ellipse")
        {
            yield return CUI.Seg(new[] { ("stroke", "선"), ("fill", "채우기"), ("both", "선+채우기") }, () => Paint, v => Paint = v);
            yield return Opt.Check("정비율", () => KeepRatio, v => KeepRatio = v);
            yield return Opt.Check("중심에서 그리기", () => FromCenter, v => FromCenter = v);
        }
        if (Shape == "line") yield return Opt.Slider("입출 보정(양끝 가늘게)", 0, 1, () => Taper, v => Taper = v, pct: true, step: 0.05);
        if (Shape == "polyline")
        {
            yield return Opt.Help("탭으로 점 추가 · Enter 또는 \"꺾은선 끝내기\"로 마무리");
            yield return Opt.Row(Opt.Button("꺾은선 끝내기", () => FinishPolyline(false)), Opt.Button("닫고 끝내기", () => FinishPolyline(true)));
        }
        var b = ShapePainter.CurrentBrush();
        if (b != null) yield return Opt.Slider("선 굵기 (펜과 같음)", 0.5, 500, () => b.Size, v => b.Size = v, log: true);
        yield return Opt.Help("선 굵기/색은 현재 브러시 설정을 따릅니다. Shift: 직선 45° 단위.");
    }
}

/// <summary>유선 (평행 효과선)</summary>
public sealed class SpeedLinesTool : ToolBase
{
    public override string Id => "speedlines";
    public override string Name => "유선";
    public override string Icon => "≋";
    public double Gap = 14, Width = 3, Jitter = 0.6, LengthMin = 0.4; public string ColorHex = "#000000"; public bool NewLayer = true;
    (double x0, double y0, double x1, double y1)? drag;
    public override bool Busy => drag != null;

    public override void Down(PointerArgs e) => drag = (e.X, e.Y, e.X, e.Y);
    public override void Move(PointerArgs e) { if (drag is { } d) drag = (d.x0, d.y0, e.X, e.Y); }
    public override void Cancel() => drag = null;
    public override void Up(PointerArgs e)
    {
        if (drag is not { } d) return;
        drag = null;
        double L = Math.Sqrt((d.x1 - d.x0) * (d.x1 - d.x0) + (d.y1 - d.y0) * (d.y1 - d.y0));
        if (L < 20) { App.Toast("선 방향으로 길게 드래그하세요"); return; }
        Draw(d.x0, d.y0, d.x1, d.y1, Random.Shared);
    }

    /// <summary>유선 그리기 (새 레이어 + 그림을 실행취소 한 번으로)</summary>
    public void Draw(double ax, double ay, double bx, double by, Random rnd)
    {
        var doc = App.Doc;
        double L = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        double ux = (bx - ax) / L, uy = (by - ay) / L, nx = -uy, ny = ux;
        var sel = doc.Sel;
        var area = sel.Active ? sel.Bounds() : new Rect(0, 0, doc.W, doc.H);
        double cx = area.X + area.W / 2.0, cy = area.Y + area.H / 2.0, span = Math.Sqrt((double)area.W * area.W + (double)area.H * area.H);
        var polys = new List<PointF[]>();
        for (double k = -span / 2; k < span / 2; k += Gap * (0.6 + rnd.NextDouble() * 0.8))
        {
            double len = span * (LengthMin + rnd.NextDouble() * (1 - LengthMin)) * 0.6;
            double along = (rnd.NextDouble() - 0.5) * span * Jitter;
            double mx = cx + nx * k + ux * along, my = cy + ny * k + uy * along;
            double w = Width * (0.4 + rnd.NextDouble() * 0.9);
            // 양끝이 뾰족한 가는 마름모
            polys.Add(new[] { new PointF((float)(mx - ux * len / 2), (float)(my - uy * len / 2)), new PointF((float)(mx + nx * w / 2), (float)(my + ny * w / 2)), new PointF((float)(mx + ux * len / 2), (float)(my + uy * len / 2)), new PointF((float)(mx - nx * w / 2), (float)(my - ny * w / 2)) });
        }
        var col = Gfx.Col(ColorHex);
        PaintNewOrActive("유선", NewLayer, new Rect(0, 0, doc.W, doc.H), g => { using var br = new SolidBrush(col); foreach (var p in polys) g.FillPolygon(br, p); }, true);
    }

    /// <summary>새 레이어(또는 현재 레이어)에 GDI+ 그림을 얹음 — 레이어 추가와 그림이 실행취소 한 번</summary>
    public static Layer PaintNewOrActive(string label, bool newLayer, Rect r, Action<Graphics> draw, bool useSel, string layerName = null)
    {
        var d = App.Doc;
        Layer l; IEdit structEdit = null;
        if (newLayer)
        {
            var before = StructSnap.Take(d);
            var a = d.Active;
            l = new Layer(d, layerName ?? label) { Parent = a?.IsFolder == true && a.Expanded ? a.Id : a?.Parent };
            int i = a == null ? d.Layers.Count : d.Layers.IndexOf(a) + 1;
            d.Layers.Insert(i, l); d.ActiveId = l.Id;
            structEdit = new StructEdit(label, before, StructSnap.Take(d));
        }
        else
        {
            l = d.Active;
            if (!App.CheckEditable(l)) return null;
        }
        r = r.Clip(d.W, d.H);
        var src = Gfx.Render(r, draw);
        var pe = App.History.BeginPixels(l, label);
        pe.Touch(r);
        Gfx.Paint(l.Target, r, src, d, useSel, l.AlphaLock && !l.EditMask);
        var px = pe.Finish(label);
        App.History.PushGroup(label, structEdit, px);
        d.Modified = true;
        App.Invalidate(r); App.NotifyLayers();
        return l;
    }

    public override Rectangle OverlayBounds(ViewXf v) => drag is { } d ? CUI.Bounds(new[] { Gfx.S(v, d.x0, d.y0), Gfx.S(v, d.x1, d.y1) }, 6) : Rectangle.Empty;
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (drag is not { } d) return;
        using var pen = new Pen(Color.FromArgb(232, 72, 74), 2);
        g.DrawLine(pen, Gfx.S(v, d.x0, d.y0), Gfx.S(v, d.x1, d.y1));
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("유선"),
        Opt.Slider("간격", 3, 80, () => Gap, v => Gap = v, step: 1),
        Opt.Slider("굵기", 0.5, 20, () => Width, v => Width = v, step: 0.5),
        Opt.Slider("위치 흩어짐", 0, 1, () => Jitter, v => Jitter = v, pct: true, step: 0.01),
        Opt.Slider("최소 길이", 0.05, 1, () => LengthMin, v => LengthMin = v, pct: true, step: 0.01),
        CUI.ColorBtn("색", () => ColorHex, v => ColorHex = v),
        Opt.Check("새 레이어에 그리기", () => NewLayer, v => NewLayer = v),
        Opt.Help("선이 향할 방향으로 드래그. 선택 영역이 있으면 그 안에만 그립니다 (속도감·배경 효과)."));
}

/// <summary>플래시 (우니 플래시 / 베타 플래시 / 폭발)</summary>
public static class Flash
{
    public static void Dialog()
    {
        var d = App.Doc; if (d == null) return;
        var v = CUI.Form("플래시 효과", new[]
        {
            CUI.Sel("style", "종류", new[] { ("uni", "우니 플래시 (흰 가시 + 테두리)"), ("beta", "베타 플래시 (검은 가시)"), ("burst", "폭발 (안쪽 흰 + 바깥 검정)") }, "uni"),
            CUI.Num("count", "가시 개수", 90, 3, 2000), CUI.Num("len", "가시 길이 (%)", 70, 1, 500), CUI.Num("jitter", "길이 들쭉날쭉 (%)", 45, 0, 100),
            CUI.Info("선택 영역이 있으면 그 크기의 타원 둘레에, 없으면 화면 가운데에 그립니다. 새 레이어에 만들어집니다."),
        }, "그리기");
        if (v == null) return;
        Draw(CUI.S(v, "style"), (int)CUI.D(v, "count"), CUI.D(v, "len"), CUI.D(v, "jitter"), Random.Shared);
    }

    public static Layer Draw(string style, int count, double len, double jitter, Random rnd)
    {
        var d = App.Doc; var sel = d.Sel;
        double bx0, by0, bw, bh;
        if (sel.Active) { var b = sel.Bounds(); (bx0, by0, bw, bh) = (b.X, b.Y, b.W, b.H); } else (bx0, by0, bw, bh) = (d.W * 0.3, d.H * 0.3, d.W * 0.4, d.H * 0.3);
        double cx = bx0 + bw / 2, cy = by0 + bh / 2, rx = bw / 2, ry = bh / 2;
        var spikes = new List<PointF[]>();
        for (int i = 0; i < count; i++)
        {
            double a = (double)i / count * Math.PI * 2 + (rnd.NextDouble() - 0.5) * 0.04;
            double L = Math.Max(rx, ry) * (len / 100) * (1 - rnd.NextDouble() * jitter / 100);
            double w = Math.PI * 2 * Math.Max(rx, ry) / count * (0.35 + rnd.NextDouble() * 0.3);
            double sx = cx + Math.Cos(a) * rx, sy = cy + Math.Sin(a) * ry, nx = -Math.Sin(a), ny = Math.Cos(a);
            spikes.Add(new[] { new PointF((float)(sx + nx * w), (float)(sy + ny * w)), new PointF((float)(sx + Math.Cos(a) * L), (float)(sy + Math.Sin(a) * L)), new PointF((float)(sx - nx * w), (float)(sy - ny * w)) });
        }
        float lw = (float)(Math.Max(2, d.W / 600.0) * 2);
        return SpeedLinesTool.PaintNewOrActive("플래시", true, new Rect(0, 0, d.W, d.H), g =>
        {
            if (style == "beta") { foreach (var s in spikes) g.FillPolygon(Brushes.Black, s); return; }
            if (style == "burst")
            {
                double ex = Math.Max(rx, ry) * len / 100 * 1.1;
                g.FillEllipse(Brushes.Black, (float)(cx - rx - ex), (float)(cy - ry - ex), (float)((rx + ex) * 2), (float)((ry + ex) * 2));
            }
            using var pen = new Pen(Color.Black, lw) { LineJoin = LineJoin.Miter };
            foreach (var s in spikes) g.DrawPolygon(pen, s);
            foreach (var s in spikes) g.FillPolygon(Brushes.White, s);
            g.FillEllipse(Brushes.White, (float)(cx - rx * 1.02), (float)(cy - ry * 1.02), (float)(rx * 2.04), (float)(ry * 2.04));
        }, false);
    }
}
