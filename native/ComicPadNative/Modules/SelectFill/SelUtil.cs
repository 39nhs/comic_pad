// 선택·채우기 모듈 공용: 선택 영역 합치기(웹 core/selection.js 의 apply 와 같은 뜻), 모양 그리기, 참조 그림, 설정 저장, 새 레이어.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.SelectFill;

public static class SelUtil
{
    static Document D => App.Doc;

    // ---- 설정 (도구 옵션을 다음 실행에도) ----------------------------------------------------
    public static double GetD(string key, double def) => App.Get("sf." + key, def);
    public static string GetS(string key, string def) => App.Get("sf." + key, def);
    public static bool GetB(string key, bool def) => App.Get("sf." + key, def);
    public static void Put(string key, JsonNode v) => App.Set("sf." + key, v);

    // ---- 선택 영역 -------------------------------------------------------------------------
    /// <summary>웹의 selection.active: 128 이상인 픽셀이 하나라도 있는지</summary>
    public static bool Active(Selection s) => s.Mask != null && Array.Exists(s.Mask, v => v >= 128);

    /// <summary>선택 영역 범위 (128 이상 기준, 웹 bounds). 없으면 Empty</summary>
    public static Rect Bounds(Selection s)
    {
        var m = s.Mask;
        if (m == null) return Rect.Empty;
        int W = s.W, H = s.H, x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int y = 0; y < H; y++)
        {
            int row = y * W, a = -1, b = -1;
            for (int x = 0; x < W; x++) if (m[row + x] >= 128) { a = x; break; }
            if (a < 0) continue;
            for (int x = W - 1; x >= a; x--) if (m[row + x] >= 128) { b = x; break; }
            if (a < x0) x0 = a; if (b > x1) x1 = b; if (y < y0) y0 = y; y1 = y;
        }
        return x1 < 0 ? Rect.Empty : new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>128 이상인 곳이 없으면 선택 없음으로 (웹: active = false)</summary>
    public static void Normalize(Selection s) { if (s.Mask != null && !Array.Exists(s.Mask, v => v >= 128)) s.Mask = null; }

    /// <summary>
    /// 흰 모양(알파 0~255, 영역 r 크기)을 op 로 합침 — 웹 Selection.apply 와 같은 규칙:
    /// replace 또는 (선택 없음 + subtract 아님) → 비우고 add, 선택 없을 때 subtract 는 아무것도 안 함,
    /// add = source-over, subtract = destination-out, intersect = destination-in (모양 밖은 모두 지움)
    /// </summary>
    public static void ApplyShape(Selection sel, Rect r, byte[] shape, string op)
    {
        bool was = Active(sel);
        if (op == "subtract" && !was) return;
        int W = sel.W, H = sel.H;
        byte[] m;
        if (op == "replace" || !was) { m = new byte[W * H]; op = "add"; }
        else m = sel.Mask;
        var c = r.Clip(W, H);
        if (op == "intersect")
        {
            // 모양 범위 밖은 0
            for (int y = 0; y < H; y++)
            {
                int row = y * W;
                if (y < c.Y || y >= c.B || c.IsEmpty) { Array.Clear(m, row, W); continue; }
                Array.Clear(m, row, c.X); Array.Clear(m, row + c.R, W - c.R);
            }
        }
        for (int y = c.Y; y < c.B; y++)
        {
            int row = y * W, srow = (y - r.Y) * r.W - r.X;
            for (int x = c.X; x < c.R; x++)
            {
                uint s = shape[srow + x], d = m[row + x];
                m[row + x] = op switch
                {
                    "add" => (byte)(s + Px.Mul(d, 255 - s)),
                    "subtract" => (byte)Px.Mul(d, 255 - s),
                    "intersect" => (byte)Px.Mul(d, s),
                    _ => (byte)s,
                };
            }
        }
        sel.Mask = m;
        Normalize(sel);
    }

    /// <summary>0/1 마스크(문서 크기)로 (fromMask)</summary>
    public static void FromMask(Selection sel, byte[] mask, Rect b, string op)
    {
        if (b.IsEmpty) return;
        var shape = new byte[b.W * b.H];
        for (int y = 0; y < b.H; y++) { int srow = (y + b.Y) * sel.W + b.X; for (int x = 0; x < b.W; x++) if (mask[srow + x] != 0) shape[y * b.W + x] = 255; }
        ApplyShape(sel, b, shape, op);
    }

    /// <summary>선택 변경을 실행취소 가능하게 + 화면 (점선) 다시 그림</summary>
    public static void SelHistory(string label, Action change)
    {
        if (D == null) return;
        App.History.SelectionChange(D, label, change);
        RedrawSel();
    }
    public static void RedrawSel() { App.Invalidate(Rect.Empty); (App.Main as UI.MainForm)?.Canvas.RedrawAll(); }

    // ---- 모양 그리기 (캔버스 fill 처럼 가장자리 부드럽게, nonzero 규칙) -------------------------
    /// <summary>다각형/타원을 영역 r 크기 알파 배열로. fill = 그릴 동작 (문서 좌표)</summary>
    public static unsafe byte[] RasterShape(Rect r, Action<Graphics> fill)
    {
        var a = new byte[r.W * r.H];
        if (r.IsEmpty) return a;
        using var bmp = new Bitmap(r.W, r.H, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;   // 픽셀 (i,j) = [i,i+1] (캔버스와 같음)
            g.TranslateTransform(-r.X, -r.Y);
            fill(g);
        }
        var bd = bmp.LockBits(new Rectangle(0, 0, r.W, r.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < r.H; y++)
            {
                uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride);
                for (int x = 0; x < r.W; x++) a[y * r.W + x] = (byte)(row[x] >> 24);
            }
        }
        finally { bmp.UnlockBits(bd); }
        return a;
    }

    public static byte[] PolygonShape(Rect r, IList<PointF> pts) => RasterShape(r, g =>
    {
        using var path = new GraphicsPath(FillMode.Winding);
        path.AddPolygon(pts.ToArray());
        g.FillPath(System.Drawing.Brushes.White, path);
    });

    public static byte[] EllipseShape(Rect r, double x, double y, double w, double h) => RasterShape(r, g => g.FillEllipse(System.Drawing.Brushes.White, (float)x, (float)y, (float)w, (float)h));

    public static Rect PointsBounds(IEnumerable<PointF> pts, int pad = 0)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        if (x1 < x0) return Rect.Empty;
        return Rect.FromLTRB((int)Math.Floor(x0) - pad, (int)Math.Floor(y0) - pad, (int)Math.Ceiling(x1) + pad, (int)Math.Ceiling(y1) + pad);
    }

    // ---- 참조 그림 (tools/basic.js referenceImage) ----------------------------------------------
    /// <summary>채우기·자동 선택이 볼 그림 (미리 곱하지 않은 RGBA, 문서 크기). all | ref | layer</summary>
    public static byte[] ReferenceImage(string source)
    {
        var d = D;
        int W = d.W, H = d.H;
        var o = new byte[W * H * 4];
        if (source == "layer")
        {
            var px = d.Active?.Px;
            if (px != null) SurfaceToRgba(px, o);
            return o;
        }
        var refs = d.Layers.Where(l => l.Ref).ToList();
        if (source == "ref" && refs.Count == 0) App.Toast("참조 레이어가 없어 모든 레이어를 참조합니다 (레이어 패널 \"참조\")");
        // 밑그림 레이어는 참조에서 제외, 용지는 투명 취급
        var opt = new RenderOptions { Paper = false, ExcludeDraft = true, Only = source == "ref" && refs.Count > 0 ? refs.ToHashSet() : null };
        RenderRgba(d, opt, o);
        return o;
    }

    /// <summary>문서를 합성해 RGBA 로 (타일 단위)</summary>
    public static void RenderRgba(Document d, RenderOptions opt, byte[] o, uint background = 0)
    {
        var comp = new Compositor();
        int W = d.W, H = d.H;
        for (int y = 0; y < H; y += Surface.TS)
            for (int x = 0; x < W; x += Surface.TS)
            {
                var part = Rect.FromLTRB(x, y, Math.Min(W, x + Surface.TS), Math.Min(H, y + Surface.TS));
                var buf = comp.Render(d, part, opt);
                for (int yy = 0; yy < part.H; yy++)
                    for (int xx = 0; xx < part.W; xx++)
                    {
                        uint c = buf[yy * part.W + xx];
                        if (background != 0) c = Px.Over(c, background);
                        Raster.ToRgba(c, o, ((part.Y + yy) * W + part.X + xx) * 4);
                    }
            }
    }

    public static void SurfaceToRgba(Surface s, byte[] o)
    {
        int W = s.W;
        for (int ty = 0; ty < s.TY; ty++)
            for (int tx = 0; tx < s.TX; tx++)
            {
                var t = s.Tile(tx, ty, false);
                if (t == null) continue;
                var tr = s.TileRect(ty * s.TX + tx);
                for (int y = 0; y < tr.H; y++)
                    for (int x = 0; x < tr.W; x++)
                        Raster.ToRgba(t[y * Surface.TS + x], o, ((tr.Y + y) * W + tr.X + x) * 4);
            }
    }

    /// <summary>RGBA(미리 곱하지 않음, 문서 크기) 를 면에 그대로 씀 (putImageData)</summary>
    public static void RgbaToSurface(byte[] rgba, Surface s)
    {
        int W = s.W;
        for (int ty = 0; ty < s.TY; ty++)
            for (int tx = 0; tx < s.TX; tx++)
            {
                var tr = s.TileRect(ty * s.TX + tx);
                var buf = new uint[tr.W * tr.H];
                for (int y = 0; y < tr.H; y++)
                    for (int x = 0; x < tr.W; x++)
                    {
                        int p = ((tr.Y + y) * W + tr.X + x) * 4;
                        buf[y * tr.W + x] = Raster.FromRgba(rgba[p], rgba[p + 1], rgba[p + 2], rgba[p + 3]);
                    }
                s.Write(tr, buf);
            }
    }

    // ---- 레이어 ---------------------------------------------------------------------------
    /// <summary>웹 ops.addLayer 와 같은 자리: index 가 없으면 현재 레이어 바로 위(같은 폴더), 펼친 폴더면 그 안 맨 위</summary>
    public static (int index, string parent) AddPos(Document d, int? index = null, string parent = null, bool parentGiven = false)
    {
        var act = d.Active;
        if (index != null) return (index.Value, parentGiven ? parent : null);
        if (act != null && act.IsFolder && act.Expanded) return (d.IndexOf(act), parentGiven ? parent : act.Id);
        return (act != null ? d.IndexOf(act) + 1 : d.Layers.Count, parentGiven ? parent : act != null ? d.ParentOf(act) : null);
    }

    /// <summary>새 레이어를 만들어 넣음 (실행취소 가능, 픽셀은 fill 이 미리 채움)</summary>
    public static Layer AddLayer(string label, string name, Action<Layer> fill = null, int? index = null, string parent = null, bool parentGiven = false, Action<Layer> props = null)
    {
        var d = D; Layer l = null;
        App.History.Struct(d, label, () =>
        {
            var (i, p) = AddPos(d, index, parent, parentGiven);
            l = new Layer(d, name) { Parent = p };
            props?.Invoke(l);
            fill?.Invoke(l);
            d.Layers.Insert(Math.Clamp(i, 0, d.Layers.Count), l);
            d.ActiveId = l.Id;
        });
        return l;
    }

    public static string NextLayerName(Document d)
    {
        int n = d.Layers.Count + 1;
        var names = d.Layers.Select(l => l.Name).ToHashSet();
        while (names.Contains($"레이어 {n}")) n++;
        return $"레이어 {n}";
    }

    public static void Done(Rect r)
    {
        App.Doc.Modified = true;
        App.Invalidate(r);
        App.NotifyLayers();
    }

    // ---- 색 ------------------------------------------------------------------------------
    /// <summary>칠할 색: 마스크 편집 중이면 흰색 (마스크는 알파만 봄), 팔레트 고정 작품이면 가까운 팔레트 색</summary>
    public static uint PaintColor(Layer l, uint c)
    {
        if (l.EditMask) return 0xFFFFFFFF;
        return PaletteLock(c);
    }

    /// <summary>팔레트 고정(doc.pixel.lock): 가까운 팔레트 색 (pixel.js nearestHex)</summary>
    public static uint PaletteLock(uint c)
    {
        try
        {
            if (D?.Extra["pixel"] is not JsonObject px || (bool?)px["lock"] != true) return c;
            var arr = (px["palette"] as JsonArray)?.Count > 0 ? px["palette"] as JsonArray : App.Settings["palette"] as JsonArray;
            if (arr == null || arr.Count == 0) return c;
            uint u = Px.Unpremul(c);
            int r = (int)((u >> 16) & 255), g = (int)((u >> 8) & 255), b = (int)(u & 255);
            double bd = double.MaxValue; uint best = c;
            foreach (var n in arr)
            {
                var (pr, pg, pb) = AutoFlat.Hex((string)n);
                double rm = (r + pr) / 2.0, dr = r - pr, dg = g - pg, db = b - pb;
                double dd = (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
                if (dd < bd) { bd = dd; best = Px.Pack(255, pr, pg, pb); }
            }
            return best;
        }
        catch { return c; }
    }
}
