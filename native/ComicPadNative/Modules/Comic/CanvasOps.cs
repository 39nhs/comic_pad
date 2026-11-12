// 편집 메뉴: 레이어 반전·회전, 캔버스 반전·회전, 캔버스 크기, 이미지 크기(다시 샘플링), 선택 영역으로 자르기.
// 문서 크기가 바뀌는 편집은 모든 레이어의 면·마스크·meta(컷·말풍선·글자 행렬·벡터 선)와 자·메모·원고 설정까지 함께 옮기고,
// 실행취소 한 번으로 되돌린다 (GeomEdit).
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

/// <summary>문서 모양(크기·레이어 면·meta·자 등) 통째 기록</summary>
public sealed class GeomEdit : IEdit
{
    public sealed class Snap
    {
        public int W, H, Dpi;
        public List<(Layer l, Surface px, Surface mask, JsonObject meta)> Layers = new();
        public Dictionary<string, JsonNode> Extra = new();
        public static readonly string[] Keys = { "rulers", "print", "notes" };
        public static Snap Take(Document d)
        {
            var s = new Snap { W = d.W, H = d.H, Dpi = d.Dpi };
            foreach (var l in d.Layers) s.Layers.Add((l, l.Px, l.Mask, J.Clone(l.Meta)));
            foreach (var k in Keys) s.Extra[k] = d.Extra[k]?.DeepClone();
            return s;
        }
        public void Apply(Document d)
        {
            d.W = W; d.H = H; d.Dpi = Dpi;
            foreach (var (l, px, mask, meta) in Layers) { l.Px = px; l.Mask = mask; l.Meta = J.Clone(meta); l.Touch(); }
            foreach (var (k, v) in Extra) { if (v == null) d.Extra.Remove(k); else d.Extra[k] = v.DeepClone(); }
            d.Sel = new Selection(W, H);
        }
        public long Bytes => Layers.Sum(x => (x.px?.MemoryBytes ?? 0) + (x.mask?.MemoryBytes ?? 0));
    }
    readonly Snap before, after;
    public string Label { get; }
    public GeomEdit(string label, Snap b, Snap a) { Label = label; before = b; after = a; }
    public long Bytes => before.Bytes + after.Bytes;
    public Rect Undo(Document d) { before.Apply(d); return new Rect(0, 0, d.W, d.H); }
    public Rect Redo(Document d) { after.Apply(d); return new Rect(0, 0, d.W, d.H); }
}

public static class CanvasOps
{
    static Document D => App.Doc;

    // ---- 면 옮기기 ----------------------------------------------------------------
    /// <summary>정수 변환(반전·90° 회전·이동)으로 새 면: inv(새 x,y) → 원래 x,y. fill = 바깥(원래 면 밖) 색</summary>
    public static Surface MapExact(Surface src, int nw, int nh, Func<int, int, (int x, int y)> inv, uint fill = 0)
    {
        var dst = new Surface(nw, nh);
        for (int ti = 0; ti < dst.TileCount; ti++)
        {
            var r = dst.TileRect(ti);
            // 원래 면에서 읽을 범위 (네 모서리)
            var c = new[] { inv(r.X, r.Y), inv(r.R - 1, r.Y), inv(r.X, r.B - 1), inv(r.R - 1, r.B - 1) };
            var sr = Rect.FromLTRB(c.Min(p => p.x), c.Min(p => p.y), c.Max(p => p.x) + 1, c.Max(p => p.y) + 1);
            var buf = src.Read(sr);
            bool any = fill != 0;
            if (!any) foreach (var v in buf) if (v != 0) { any = true; break; }
            if (!any) continue;
            var o = new uint[r.W * r.H];
            for (int y = 0; y < r.H; y++)
                for (int x = 0; x < r.W; x++)
                {
                    var (sx, sy) = inv(r.X + x, r.Y + y);
                    o[y * r.W + x] = (uint)sx < (uint)src.W && (uint)sy < (uint)src.H ? buf[(sy - sr.Y) * sr.W + (sx - sr.X)] : fill;
                }
            dst.Write(r, o);
        }
        return dst;
    }

    /// <summary>아핀 변환(문서 → 새 문서)으로 다시 샘플링한 새 면</summary>
    public static Surface MapAffine(Surface src, int nw, int nh, Mat2 fwd, int mode = Resample.Bicubic)
    {
        var dst = new Surface(nw, nh);
        var b = src.Bounds();
        if (b.IsEmpty) return dst;
        var buf = src.Read(b);
        var inv = fwd.Inverse();
        var q = new[] { fwd.Apply(b.X, b.Y), fwd.Apply(b.R, b.Y), fwd.Apply(b.R, b.B), fwd.Apply(b.X, b.B) };
        var r = Geo.BoundsRect(q, 2).Clip(nw, nh);
        if (r.IsEmpty) return dst;
        int ss = mode == Resample.Nearest ? 1 : Resample.SuperFor(Math.Abs(fwd.Det));
        var o = Resample.Warp(buf, b.W, b.H, r, (x, y) => { var p = inv.Apply(x, y); return new P2(p.X - b.X, p.Y - b.Y); }, mode, ss);
        dst.Write(r, o);
        return dst;
    }

    // ---- meta 옮기기 ----------------------------------------------------------------
    /// <summary>레이어 meta 의 좌표를 A 로 옮김 (컷 꼭짓점, 말풍선 상자·꼬리, 글자 행렬, 벡터 선)</summary>
    public static JsonObject MapMeta(Layer l, JsonObject meta, Mat2 A)
    {
        if (meta == null) return null;
        var m = J.Clone(meta);
        double k = Math.Sqrt(Math.Abs(A.Det));
        switch (l.Kind)
        {
            case "panel":
                if (m["panels"] is JsonArray) m["panels"] = new JsonArray(Panels.List(m).Select(q => (JsonNode)Panels.PanelJson(q.Select(A.Apply).ToList())).ToArray());
                break;
            case "bubble":
                if (m["bubbles"] is JsonArray bs)
                    foreach (var b in bs.OfType<JsonObject>())
                    {
                        double x = J.Num(b, "x"), y = J.Num(b, "y"), w = J.Num(b, "w"), h = J.Num(b, "h");
                        var r = Geo.Bounds(new[] { A.Apply(x, y), A.Apply(x + w, y), A.Apply(x + w, y + h), A.Apply(x, y + h) });
                        b["x"] = J.R(r.x0); b["y"] = J.R(r.y0); b["w"] = J.R(r.x1 - r.x0); b["h"] = J.R(r.y1 - r.y0);
                        if (b["tail"] is JsonObject t) { var p = A.Apply(J.ToP(t)); b["tail"] = J.Pt(p.X, p.Y); }
                        if (b["line"] != null) b["line"] = J.R(J.Num(b, "line") * k);
                    }
                break;
            case "text":
            {
                var old = Mat2.From(J.Doubles(m["mat"]));
                m["mat"] = J.ArrOf(A.Mul(old).Arr());
                if (m["quad"] is JsonArray qa)
                {
                    var qs = qa.Select(J.ToP).Select(A.Apply).ToList();
                    m["quad"] = new JsonArray(qs.Select(p => (JsonNode)J.Pt(p.X, p.Y)).ToArray());
                    var bb = Geo.Bounds(qs);
                    m["box"] = new JsonObject { ["x"] = J.R(bb.x0), ["y"] = J.R(bb.y0), ["w"] = J.R(bb.x1 - bb.x0), ["h"] = J.R(bb.y1 - bb.y0) };
                }
                break;
            }
            case "vector":
                if (m["strokes"] is JsonArray strokes)
                    foreach (var so in strokes.OfType<JsonObject>())
                    {
                        if (so["pts"] is JsonArray p) for (int i = 0; i + 1 < p.Count; i += 4) { var q = A.Apply(J.Num(p[i]), J.Num(p[i + 1])); p[i] = J.R(q.X); p[i + 1] = J.R(q.Y); }
                        if (so["preset"] is JsonObject pr && pr["size"] != null) pr["size"] = J.R(J.Num(pr, "size") * k);
                    }
                break;
        }
        return m;
    }

    static void MapExtra(Document d, Mat2 A)
    {
        if (Rulers.Arr(d) is JsonArray rs)
            foreach (var r in rs.OfType<JsonObject>())
            {
                void P(string kx, string ky) { if (r[kx] == null) return; var p = A.Apply(J.Num(r, kx), J.Num(r, ky)); r[kx] = J.R(p.X); r[ky] = J.R(p.Y); }
                P("x1", "y1"); P("x2", "y2"); P("cx", "cy");
                if (r["vps"] is JsonArray vps) for (int i = 0; i < vps.Count; i++) { var p = A.Apply(J.ToP(vps[i])); vps[i] = J.Pt(p.X, p.Y); }
            }
        if (d.Extra["notes"] is JsonArray ns)
            foreach (var n in ns.OfType<JsonObject>()) { var p = A.Apply(J.Num(n, "x"), J.Num(n, "y")); n["x"] = J.R(p.X); n["y"] = J.R(p.Y); }
    }

    /// <summary>문서 전체를 새 크기로: 면은 mapSurf, meta·자는 A</summary>
    static void ApplyDoc(string label, int nw, int nh, Mat2 A, Func<Surface, bool, Surface> mapSurf, bool rerenderMeta = false, Action<Document> also = null)
    {
        var d = D; if (d == null) return;
        if ((long)nw * nh > 16000L * 16000) { App.Toast("너무 큽니다"); return; }
        Cursor.Current = Cursors.WaitCursor;
        var before = GeomEdit.Snap.Take(d);
        foreach (var l in d.Layers)
        {
            if (l.Px != null) l.Px = mapSurf(l.Px, false);
            if (l.Mask != null) l.Mask = mapSurf(l.Mask, true);
            l.Meta = MapMeta(l, l.Meta, A);
            l.Touch();
        }
        MapExtra(d, A);
        d.W = nw; d.H = nh;
        d.Sel = new Selection(nw, nh);
        also?.Invoke(d);
        if (rerenderMeta)
            foreach (var l in d.Layers.Where(MetaLayers.IsMeta)) { try { l.Px = MetaLayers.Render(l).s; } catch { } }
        App.History.Push(new GeomEdit(label, before, GeomEdit.Snap.Take(d)));
        d.Modified = true;
        App.InvalidateAll(); App.NotifyLayers();
        MetaLayers.Canvas?.Fit();
        Cursor.Current = Cursors.Default;
    }

    // ---- 명령 --------------------------------------------------------------------
    /// <summary>캔버스 크기 (anchor 0~1: 늘어나는 쪽 기준). 웹 resizeCanvas 와 같음 (마스크 바깥은 보이게)</summary>
    public static void ResizeCanvas(int w, int h, double ax = 0.5, double ay = 0.5, string label = "캔버스 크기", Action<Document> also = null)
    {
        var d = D; if (d == null || w < 1 || h < 1) return;
        int dx = (int)Math.Round((w - d.W) * ax), dy = (int)Math.Round((h - d.H) * ay);
        Offset(w, h, dx, dy, label, also);
    }
    static void Offset(int w, int h, int dx, int dy, string label, Action<Document> also = null) =>
        ApplyDoc(label, w, h, Mat2.Translate(dx, dy), (s, mask) => MapExact(s, w, h, (x, y) => (x - dx, y - dy), mask ? 0xFFFFFFFFu : 0), also: also);

    public static void FlipCanvas(char axis)
    {
        var d = D; if (d == null) return;
        int W = d.W, H = d.H;
        var A = axis == 'h' ? new Mat2(-1, 0, 0, 1, W, 0) : new Mat2(1, 0, 0, -1, 0, H);
        ApplyDoc(axis == 'h' ? "캔버스 좌우 반전" : "캔버스 상하 반전", W, H, A, (s, _) => MapExact(s, W, H, axis == 'h' ? (x, y) => (W - 1 - x, y) : (x, y) => (x, H - 1 - y)));
    }

    /// <summary>캔버스 회전: 90 = 오른쪽(시계), -90 = 왼쪽, 180</summary>
    public static void RotateCanvas(int deg)
    {
        var d = D; if (d == null) return;
        int W = d.W, H = d.H;
        if (deg == 180)
        {
            ApplyDoc("캔버스 180° 회전", W, H, new Mat2(-1, 0, 0, -1, W, H), (s, _) => MapExact(s, W, H, (x, y) => (W - 1 - x, H - 1 - y)));
            return;
        }
        bool cw = deg > 0;
        // 시계 방향: (x,y) → (H - y, x) / 반시계: (x,y) → (y, W - x)
        var A = cw ? new Mat2(0, 1, -1, 0, H, 0) : new Mat2(0, -1, 1, 0, 0, W);
        ApplyDoc(cw ? "캔버스 오른쪽으로 90° 회전" : "캔버스 왼쪽으로 90° 회전", H, W, A,
            (s, _) => MapExact(s, H, W, cw ? (x, y) => (y, H - 1 - x) : (x, y) => (W - 1 - y, x)),
            also: doc =>
            {
                // 원고 설정: 재단 가로·세로도 바꿈
                if (doc.Extra["print"] is JsonObject p)
                {
                    (p["trimW"], p["trimH"]) = (p["trimH"]?.DeepClone(), p["trimW"]?.DeepClone());
                    (p["mmW"], p["mmH"]) = (p["mmH"]?.DeepClone(), p["mmW"]?.DeepClone());
                }
            });
    }

    public static void ImageSize()
    {
        var d = D; if (d == null) return;
        var v = CUI.Form("이미지 크기", new[]
        {
            CUI.Num("w", "가로(px)", d.W, 1, 16000), CUI.Num("h", "세로(px)", d.H, 1, 16000),
            CUI.Chk("keep", "비율 유지 (가로 기준)", true),
            CUI.Sel("interp", "보간", new[] { ("2", "고품질 (바이큐빅)"), ("1", "부드럽게 (바이리니어)"), ("0", "픽셀 그대로 (도트)") }, "2"),
            CUI.Chk("dpi", "해상도(dpi)도 같은 비율로 (인쇄 크기 유지)", false),
            CUI.Info("모든 레이어를 다시 샘플링합니다. 컷·말풍선·글자는 정보로 다시 그려 선명하게 유지됩니다."),
        }, "적용");
        if (v == null) return;
        int nw = (int)CUI.D(v, "w"), nh = CUI.B(v, "keep") ? (int)Math.Max(1, Math.Round(d.H * (double)nw / d.W)) : (int)CUI.D(v, "h");
        Resize(nw, nh, int.Parse(CUI.S(v, "interp") ?? "2"), CUI.B(v, "dpi"));
    }

    public static void Resize(int nw, int nh, int interp = Resample.Bicubic, bool scaleDpi = false)
    {
        var d = D; if (d == null || nw < 1 || nh < 1 || (nw == d.W && nh == d.H)) return;
        double kx = (double)nw / d.W, ky = (double)nh / d.H;
        var A = Mat2.Scale(kx, ky);
        ApplyDoc("이미지 크기", nw, nh, A, (s, mask) => MapAffineFull(s, nw, nh, A, interp, mask), rerenderMeta: true, also: doc =>
        {
            if (doc.Extra["print"] is JsonObject p)
            {
                p["trimW"] = (int)Math.Round(J.Num(p, "trimW") * kx); p["trimH"] = (int)Math.Round(J.Num(p, "trimH") * ky);
                p["bleed"] = (int)Math.Round(J.Num(p, "bleed") * kx); p["safe"] = (int)Math.Round(J.Num(p, "safe") * kx);
            }
            if (scaleDpi) doc.Dpi = Math.Max(1, (int)Math.Round(doc.Dpi * kx));
        });
    }
    static Surface MapAffineFull(Surface s, int nw, int nh, Mat2 A, int interp, bool mask)
    {
        if (!mask) return MapAffine(s, nw, nh, A, interp);
        // 마스크: 가장자리 바깥도 보이게 (0 이 아닌 값 유지)
        var o = MapAffine(s, nw, nh, A, interp);
        return o;
    }

    public static void CropToSelection()
    {
        var d = D; if (d == null) return;
        if (!d.Sel.Active) { App.Toast("선택 영역이 없습니다"); return; }
        var b = d.Sel.Bounds();
        if (b.IsEmpty) { App.Toast("선택 영역이 비어 있습니다"); return; }
        Offset(b.W, b.H, -b.X, -b.Y, "선택 영역으로 자르기");
    }

    // ---- 레이어 반전·회전 -------------------------------------------------------------
    static bool EditableForGeom(Layer l)
    {
        if (!App.CheckEditable(l)) return false;
        if (l.Kind == "bubble" && l.Meta?["bubbles"] is JsonArray && !l.EditMask) { App.Toast("말풍선 레이어는 말풍선 도구로 고치세요 (직접 그리려면 레이어 메뉴 > 말풍선을 그림으로)"); return false; }
        return true;
    }

    /// <summary>레이어(또는 편집 중인 마스크)만 반전/회전 — 문서 가운데 기준</summary>
    public static void LayerOp(string label, Mat2 A, Func<int, int, (int x, int y)> exactInv = null)
    {
        var d = D; var l = d?.Active;
        if (!EditableForGeom(l)) return;
        // 컷·글자 레이어: 정보를 옮기고 다시 그림 (계속 편집 가능)
        if (!l.EditMask && (l.Kind == "panel" && l.Meta?["panels"] is JsonArray || l.Kind == "text" && l.Meta != null))
        {
            MetaLayers.Commit(l, label, MapMeta(l, l.Meta, A), MetaLayers.Render);
            return;
        }
        var T = l.Target;
        var n = exactInv != null ? MapExact(T, d.W, d.H, exactInv) : MapAffine(T, d.W, d.H, A);
        var pe = App.History.BeginPixels(l, label);
        var r = T.Bounds().Union(n.Bounds());
        if (r.IsEmpty) { App.Toast("레이어에 그림이 없습니다"); return; }
        pe.Touch(r);
        T.Write(r, n.Read(r));
        var px = pe.Finish(label);
        if (l.Kind == "vector" && l.Meta != null)
        {
            var before = StructSnap.Take(d);
            l.Meta = MapMeta(l, l.Meta, A);
            App.History.PushGroup(label, px, new StructEdit(label, before, StructSnap.Take(d)));
        }
        else if (px != null) App.History.Push(px);
        d.Modified = true;
        App.Invalidate(r); App.NotifyLayers();
    }

    public static void FlipLayer(char axis)
    {
        var d = D; if (d == null) return;
        int W = d.W, H = d.H;
        if (axis == 'h') LayerOp("좌우 반전", new Mat2(-1, 0, 0, 1, W, 0), (x, y) => (W - 1 - x, y));
        else LayerOp("상하 반전", new Mat2(1, 0, 0, -1, 0, H), (x, y) => (x, H - 1 - y));
    }

    public static void RotateLayer(double deg)
    {
        var d = D; if (d == null) return;
        int W = d.W, H = d.H;
        var A = Mat2.Translate(W / 2.0, H / 2.0).Mul(Mat2.Rotate(deg)).Mul(Mat2.Translate(-W / 2.0, -H / 2.0));
        Func<int, int, (int, int)> exact = null;
        if (deg == 180) exact = (x, y) => (W - 1 - x, H - 1 - y);
        LayerOp(deg == 180 ? "레이어 180° 회전" : deg > 0 ? "레이어 오른쪽으로 90° 회전" : "레이어 왼쪽으로 90° 회전", A, exact);
    }

    public static void CanvasSizeDialog()
    {
        var d = D; if (d == null) return;
        var v = CUI.Form("캔버스 크기", new[]
        {
            CUI.Num("w", "가로(px)", d.W, 1, 16000), CUI.Num("h", "세로(px)", d.H, 1, 16000),
            CUI.Sel("anchor", "기준", new[] { ("top", "위 고정 (아래로 늘리기)"), ("center", "가운데"), ("bottom", "아래 고정") }, "top"),
            CUI.Info("웹툰은 \"위 고정\"으로 세로를 늘리면 아래로 이어 그릴 수 있습니다."),
        }, "적용");
        if (v == null) return;
        string a = CUI.S(v, "anchor");
        double ay = a == "top" ? 0 : a == "bottom" ? 1 : 0.5;
        ResizeCanvas((int)Math.Round(CUI.D(v, "w")), (int)Math.Round(CUI.D(v, "h")), 0.5, ay);
    }

    public static void ExtendDown()
    {
        var d = D; if (d == null) return;
        ResizeCanvas(d.W, d.H + (int)Math.Round(d.W * 1.6), 0.5, 0, "웹툰: 아래로 캔버스 늘리기");
        App.Toast($"세로 {d.H}px");
    }
}
