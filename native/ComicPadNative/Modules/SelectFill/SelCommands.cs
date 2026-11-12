// 선택 메뉴 명령 (웹 commands.js select.*, ui/adjust.js select.feather/colorRange, layer.selectAlpha)과
// 편집 메뉴 (잘라내기·복사·붙여넣기, 배경색으로 채우기).
using System.Drawing.Imaging;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.SelectFill;

public static class SelCommands
{
    static Document D => App.Doc;

    /// <summary>선택 반전 (웹: 선택이 없으면 전체 선택)</summary>
    public static void Invert() => SelUtil.SelHistory("선택 영역", () =>
    {
        var s = D.Sel;
        if (!SelUtil.Active(s)) { s.All(); return; }
        for (int i = 0; i < s.Mask.Length; i++) s.Mask[i] = (byte)(255 - s.Mask[i]);
        SelUtil.Normalize(s);
    });

    /// <summary>px 만큼 확장(+)/축소(-) (selection.grow): 128 기준으로 0/1 로 만든 뒤 팽창/침식</summary>
    public static void Grow(Selection sel, int px)
    {
        if (!SelUtil.Active(sel) || px == 0) return;
        int W = sel.W, H = sel.H;
        var region = SelUtil.Bounds(sel).Inflate(Math.Abs(px) + 2).Clip(W, H);
        if (region.IsEmpty) return;
        int n = region.W * region.H;
        var m = new byte[n];
        for (int y = 0; y < region.H; y++) for (int x = 0; x < region.W; x++) m[y * region.W + x] = sel.Mask[(region.Y + y) * W + region.X + x] >= 128 ? (byte)1 : (byte)0;
        m = px > 0 ? Raster.Dilate(m, region.W, region.H, px) : Raster.Erode(m, region.W, region.H, -px);
        for (int y = 0; y < region.H; y++) for (int x = 0; x < region.W; x++) sel.Mask[(region.Y + y) * W + region.X + x] = m[y * region.W + x] != 0 ? (byte)255 : (byte)0;
        SelUtil.Normalize(sel);
    }

    public static void GrowBy()
    {
        var v = Ui.Form("선택 영역 확장/축소", new[] { Ui.Num("px", "픽셀 (음수 = 축소)", 4, -1000, 1000) });
        if (v != null) SelUtil.SelHistory("선택 영역", () => Grow(D.Sel, Raster.JsRound((double)v["px"])));
    }

    /// <summary>경계 흐리기 (캔버스 filter: blur(r px) = 가우스 σ=r, 바깥은 투명)</summary>
    public static void Feather(Selection sel, double r)
    {
        if (!SelUtil.Active(sel) || r <= 0) return;
        sel.Mask = Raster.GaussBlurZero(sel.Mask, sel.W, sel.H, r);
        SelUtil.Normalize(sel);
    }

    public static void FeatherDialog()
    {
        if (!SelUtil.Active(D.Sel)) { App.Toast("선택 영역이 없습니다"); return; }
        var v = Ui.Form("경계 흐리기", new[] { Ui.Num("r", "반경(px)", 8, 0, 500) });
        if (v == null) return;
        SelUtil.SelHistory("경계 흐리기", () => Feather(D.Sel, (double)v["r"]));
    }

    /// <summary>색역 선택: 같은 색 전부 (알파 16 미만은 건너뜀)</summary>
    public static (byte[] mask, Rect bounds) ColorRangeMask(byte[] d, int W, int H, string hex, double tol)
    {
        var (r0, g0, b0) = AutoFlat.Hex(hex);
        int n = W * H;
        var mask = new byte[n];
        int x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int i = 0, p = 0; i < n; i++, p += 4)
        {
            if (d[p + 3] < 16) continue;
            if (Math.Max(Math.Max(Math.Abs(d[p] - r0), Math.Abs(d[p + 1] - g0)), Math.Abs(d[p + 2] - b0)) <= tol)
            {
                mask[i] = 1;
                int x = i % W, y = i / W;
                if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
        }
        return (mask, x1 < 0 ? Rect.Empty : new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
    }

    public static async void ColorRange()
    {
        var v = Ui.Form("색역 선택", new[]
        {
            Ui.ColorF("color", "고를 색", Px.ToHex(App.Fg)),
            Ui.Num("tol", "허용 범위 (0~255)", 32, 0, 255),
            Ui.Seg("src", "기준", new[] { ("all", "보이는 그림"), ("layer", "현재 레이어") }, "all"),
            Ui.Seg("op", "방식", new[] { ("replace", "새로"), ("add", "추가"), ("subtract", "빼기") }, "replace"),
        }, "선택");
        if (v == null) return;
        var doc = D;
        var img = SelUtil.ReferenceImage((string)v["src"]);
        var res = await Task.Run(() => ColorRangeMask(img, doc.W, doc.H, (string)v["color"], (double)v["tol"]));
        if (res.bounds.IsEmpty) { App.Toast("그 색을 찾지 못했습니다"); return; }
        if (App.Doc == doc) SelUtil.SelHistory("색역 선택", () => SelUtil.FromMask(doc.Sel, res.mask, res.bounds, (string)v["op"]));
    }

    /// <summary>레이어의 그림 부분을 선택 (fromAlpha, replace)</summary>
    public static void SelectAlpha()
    {
        var l = D.Active;
        if (l?.Px == null) return;
        var sel = D.Sel;
        SelUtil.SelHistory("선택 영역", () =>
        {
            var shape = new byte[D.W * D.H];
            var px = l.Px;
            for (int ty = 0; ty < px.TY; ty++)
                for (int tx = 0; tx < px.TX; tx++)
                {
                    var t = px.Tile(tx, ty, false);
                    if (t == null) continue;
                    var tr = px.TileRect(ty * px.TX + tx);
                    for (int y = 0; y < tr.H; y++) for (int x = 0; x < tr.W; x++) shape[(tr.Y + y) * D.W + tr.X + x] = (byte)(t[y * Surface.TS + x] >> 24);
                }
            SelUtil.ApplyShape(sel, new Rect(0, 0, D.W, D.H), shape, "replace");
        });
    }

    // ---- 꺼내기·복사·붙여넣기 (io.js extractContent, commands.js copySelection/paste) ----------------
    public sealed class Clip { public uint[] Px; public Rect Bounds; }
    public static Clip Clipboard;

    /// <summary>레이어 그림을 선택 영역 모양으로 꺼내 남는 곳 없이 자름. 없으면 null</summary>
    public static Clip ExtractContent(Layer l)
    {
        var d = D;
        if (l?.Px == null) return null;
        var sel = d.Sel;
        bool useSel = SelUtil.Active(sel);
        var area = useSel ? SelUtil.Bounds(sel).Union(BoundsNonZero(sel)) : l.Px.Bounds();
        area = area.Intersect(l.Px.Bounds());
        if (area.IsEmpty) return null;
        var buf = l.Px.Read(area);
        if (useSel) for (int y = 0; y < area.H; y++) for (int x = 0; x < area.W; x++) { int i = y * area.W + x; buf[i] = Px.Scale(buf[i], sel.Mask[(area.Y + y) * d.W + area.X + x]); }
        // 알파가 있는 곳만 남게 다시 자르기 (trimCanvas)
        int x0 = area.W, y0 = area.H, x1 = -1, y1 = -1;
        for (int y = 0; y < area.H; y++) for (int x = 0; x < area.W; x++) if ((buf[y * area.W + x] >> 24) != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; y1 = y; }
        if (x1 < 0) return null;
        var b = new Rect(area.X + x0, area.Y + y0, x1 - x0 + 1, y1 - y0 + 1);
        var o = new uint[b.W * b.H];
        for (int y = 0; y < b.H; y++) Array.Copy(buf, (y + y0) * area.W + x0, o, y * b.W, b.W);
        return new Clip { Px = o, Bounds = b };
    }
    static Rect BoundsNonZero(Selection s) => s.Bounds();

    public static void ToLayer()
    {
        var src = D.Active;
        var r = ExtractContent(src);
        if (r == null) { App.Toast("복사할 그림이 없습니다"); return; }
        SelUtil.AddLayer("레이어 추가", src.Name + " 조각", l => l.Px.Write(r.Bounds, r.Px));
        SelUtil.Done(r.Bounds);
    }

    public static void Copy(bool cut)
    {
        var r = ExtractContent(D.Active);
        if (r == null) { App.Toast("복사할 그림이 없습니다"); return; }
        Clipboard = r;
        try
        {
            var s = new Surface(r.Bounds.W, r.Bounds.H);
            s.Write(new Rect(0, 0, r.Bounds.W, r.Bounds.H), r.Px);
            using var bmp = CpadIO.ToBitmap(s, s.Full);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            var data = new DataObject();
            data.SetData("PNG", false, new MemoryStream(ms.ToArray()));
            data.SetImage(bmp);
            System.Windows.Forms.Clipboard.SetDataObject(data, true);
        }
        catch { /* 시스템 클립보드 실패해도 앱 내부 복사는 됨 */ }
        if (cut) ClearLayer();
        App.Toast(cut ? "잘라냈습니다" : "복사했습니다");
    }

    /// <summary>지우기 (ops.clearLayer): 선택 영역이 있으면 그 모양만큼, 없으면 전부</summary>
    public static void ClearLayer()
    {
        var d = D; var l = d.Active;
        if (!App.CheckEditable(l)) return;
        var t = l.Target;
        var sel = d.Sel;
        bool useSel = SelUtil.Active(sel);
        var r = useSel ? sel.Bounds() : t.Bounds();
        if (r.IsEmpty) return;
        var pe = App.History.BeginPixels(l, "지우기");
        pe.Touch(r);
        if (useSel)
        {
            var buf = t.Read(r);
            for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++) { int i = y * r.W + x; byte s = sel.Mask[(r.Y + y) * d.W + r.X + x]; if (s != 0) buf[i] = Px.Scale(buf[i], 255u - s); }
            t.Write(r, buf);
        }
        else t.Clear(r);
        pe.Commit("지우기");
        SelUtil.Done(r);
    }

    public static void Paste()
    {
        var d = D;
        Bitmap img = null;
        try
        {
            if (System.Windows.Forms.Clipboard.ContainsData("PNG") && System.Windows.Forms.Clipboard.GetData("PNG") is MemoryStream ms) img = new Bitmap(ms);
            else if (System.Windows.Forms.Clipboard.ContainsImage()) img = System.Windows.Forms.Clipboard.GetImage() as Bitmap;
        }
        catch { /* 내부 클립보드 사용 */ }
        var cb = Clipboard;
        // 방금 앱에서 복사한 그림이면 원래 위치에
        if (img != null && !(cb != null && cb.Bounds.W == img.Width && cb.Bounds.H == img.Height))
        {
            using (img) PlaceImage(img, "붙여넣기");
            return;
        }
        img?.Dispose();
        if (cb == null) { App.Toast("붙여넣을 내용이 없습니다"); return; }
        SelUtil.AddLayer("레이어 추가", "붙여넣기", l => l.Px.Write(cb.Bounds, cb.Px));
        SelUtil.Done(cb.Bounds);
        StartTransform();
    }

    /// <summary>그림을 새 레이어로 (ops.placeImage): 문서보다 크면 90% 로 맞춤, 화면 가운데</summary>
    public static void PlaceImage(Bitmap img, string name)
    {
        var d = D;
        double w = img.Width, h = img.Height;
        if (w > d.W || h > d.H) { double s = Math.Min(d.W / w, d.H / h) * 0.9; w *= s; h *= s; }
        var cv = (App.Main as MainForm)?.Canvas;
        var (cx, cy) = cv != null ? cv.V.ToDoc(cv.Width / 2.0, cv.Height / 2.0) : (d.W / 2.0, d.H / 2.0);
        int x = Raster.JsRound(Math.Max(0, Math.Min(d.W - w, cx - w / 2))), y = Raster.JsRound(Math.Max(0, Math.Min(d.H - h, cy - h / 2)));
        int iw = Math.Max(1, (int)Math.Round(w)), ih = Math.Max(1, (int)Math.Round(h));
        using var scaled = new Bitmap(iw, ih, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(img, 0, 0, iw, ih);
        }
        SelUtil.AddLayer("레이어 추가", name, l => CpadIO.DrawBitmap(l.Px, scaled, x, y));
        SelUtil.Done(new Rect(x, y, iw, ih));
        StartTransform();
    }

    static void StartTransform() { if (Registry.Tools.Any(t => t.Id == "transform")) App.SetTool("transform"); }

    /// <summary>선택 영역(없으면 전체)을 색으로 채움 (ops.fillSelection)</summary>
    public static void FillSelection(uint color)
    {
        var d = D; var l = d.Active;
        if (!App.CheckEditable(l)) return;
        var sel = d.Sel;
        bool useSel = SelUtil.Active(sel);
        var r = useSel ? sel.Bounds() : new Rect(0, 0, d.W, d.H);
        if (r.IsEmpty) return;
        uint col = SelUtil.PaintColor(l, color);
        bool atop = l.AlphaLock && !l.EditMask;
        var t = l.Target;
        var pe = App.History.BeginPixels(l, "채우기");
        pe.Touch(r);
        var buf = t.Read(r);
        for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
            {
                uint s = useSel ? sel.Mask[(r.Y + y) * d.W + r.X + x] : 255u;
                if (s == 0) continue;
                int i = y * r.W + x;
                var src = Px.Scale(col, s);
                buf[i] = atop ? Px.Composite(src, buf[i], Blend.Atop) : Px.Over(src, buf[i]);
            }
        t.Write(r, buf);
        pe.Commit("채우기");
        SelUtil.Done(r);
    }
}
