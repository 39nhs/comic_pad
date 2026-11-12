// 그림 주고받기: 복사·잘라내기·병합 복사(시스템 클립보드 PNG), 붙여넣기 → 새 레이어, 이미지 파일 가져오기 → 새 레이어,
// 내보내기 공통(합성·크기 바꾸기·PNG/JPG 저장). 웹 commands.js copySelection/paste, ops.placeImage, io.extractContent 와 같은 동작.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Filters;

public static class ImageIO
{
    static Document D => App.Doc;

    // ---- 앱 안 클립보드 (원래 위치에 붙이기) ----
    static uint[] clipPx; static Rect clipRect;

    /// <summary>레이어(또는 보이는 그림 전체) 내용을 선택 영역 안만, 빈 여백을 잘라서</summary>
    public static (uint[] px, Rect r)? Extract(Layer layer, bool merged)
    {
        var d = D;
        var full = new Rect(0, 0, d.W, d.H);
        uint[] buf;
        if (merged) buf = App.Comp.Flatten(d, new RenderOptions { Paper = false, ExcludeDraft = true }).Read(full);
        else if (layer?.Px != null) buf = layer.Px.Read(full);
        else return null;
        if (d.Sel.Active) for (int i = 0; i < buf.Length; i++) if (buf[i] != 0) buf[i] = Px.Scale(buf[i], d.Sel.Mask[i]);
        var b = Raster.AlphaBounds(buf, d.W, d.H);
        if (b.IsEmpty) return null;
        var o = new uint[b.W * b.H];
        for (int y = 0; y < b.H; y++) Array.Copy(buf, (y + b.Y) * d.W + b.X, o, y * b.W, b.W);
        return (o, b);
    }

    public static void Copy(bool merged, bool cut)
    {
        var d = D;
        var r = Extract(d.Active, merged);
        if (r == null) { App.Toast("복사할 그림이 없습니다"); return; }
        clipPx = r.Value.px; clipRect = r.Value.r;
        try { using var bmp = ToBitmap(clipPx, clipRect.W, clipRect.H); SetClipboard(bmp); } catch { /* 시스템 클립보드 실패해도 앱 안 복사는 됨 */ }
        if (cut && !merged) SelectCommands.FillSelection(true);
        App.Toast(cut ? "잘라냈습니다" : "복사했습니다");
    }

    /// <summary>클립보드에 PNG(투명 유지) + 보통 그림 둘 다</summary>
    public static void SetClipboard(Bitmap bmp)
    {
        var data = new DataObject();
        var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        data.SetData("PNG", false, ms);
        data.SetImage(bmp);
        Clipboard.SetDataObject(data, true);
    }

    static Bitmap GetClipboardImage()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return null;
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream ms) return new Bitmap(ms);
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var f in Clipboard.GetFileDropList())
                    if (f != null && new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(Path.GetExtension(f).ToLowerInvariant())) return LoadBitmap(f);
            }
            if (Clipboard.ContainsImage()) return Clipboard.GetImage() as Bitmap;
        }
        catch { }
        return null;
    }

    public static void Paste()
    {
        var d = D;
        using var img = GetClipboardImage();
        // 방금 앱에서 복사한 그림이면 원래 위치에
        if (img != null && !(clipPx != null && img.Width == clipRect.W && img.Height == clipRect.H)) { PlaceImage(img, "붙여넣기"); return; }
        if (clipPx == null) { App.Toast("붙여넣을 내용이 없습니다"); return; }
        var px = clipPx; var r = clipRect;
        AddLayer("붙여넣기", l => l.Px.Write(r, px));
        StartTransform();
    }

    public static void ImportImage()
    {
        using var dlg = new OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|모든 파일|*.*" };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        try { using var bmp = LoadBitmap(dlg.FileName); PlaceImage(bmp, Path.GetFileNameWithoutExtension(dlg.FileName)); }
        catch (Exception ex) { App.Toast("이미지를 열지 못했습니다: " + ex.Message); }
    }

    /// <summary>파일을 잠그지 않고 읽기</summary>
    public static Bitmap LoadBitmap(string path)
    {
        using var fs = File.OpenRead(path);
        using var tmp = new Bitmap(fs);
        return new Bitmap(tmp);
    }

    /// <summary>그림을 화면 가운데에 새 레이어로 (문서보다 크면 90% 로 줄임)</summary>
    public static Layer PlaceImage(Bitmap img, string name, bool fit = true, bool transform = true)
    {
        var d = D;
        double w = img.Width, h = img.Height;
        if (fit && (w > d.W || h > d.H)) { double s = Math.Min(d.W / w, d.H / h) * 0.9; w *= s; h *= s; }
        double cx = d.W / 2.0, cy = d.H / 2.0;
        if (App.Main is MainForm mf) { var c = mf.Canvas.V.ToDoc(mf.Canvas.Width / 2.0, mf.Canvas.Height / 2.0); cx = c.x; cy = c.y; }
        int x = (int)Math.Round(Math.Max(0, Math.Min(d.W - w, cx - w / 2)));
        int y = (int)Math.Round(Math.Max(0, Math.Min(d.H - h, cy - h / 2)));
        int iw = Math.Max(1, (int)Math.Round(w)), ih = Math.Max(1, (int)Math.Round(h));
        using var scaled = iw == img.Width && ih == img.Height ? new Bitmap(img) : Resize(img, iw, ih);
        var l = AddLayer(name, ly => CpadIO.DrawBitmap(ly.Px, scaled, x, y));
        if (transform) StartTransform();
        return l;
    }

    static void StartTransform() { if (Registry.Tools.Any(t => t.Id == "transform")) App.SetTool("transform"); }

    /// <summary>새 그림 레이어 (지금 레이어 위, 실행취소 한 번)</summary>
    public static Layer AddLayer(string name, Action<Layer> fill)
    {
        var d = D; Layer l = null;
        App.History.Struct(d, "레이어 추가", () =>
        {
            var a = d.Active;
            l = new Layer(d, name) { Parent = a?.IsFolder == true && a.Expanded ? a.Id : a?.Parent };
            fill?.Invoke(l);
            l.Touch();
            int i = a == null ? d.Layers.Count : d.Layers.IndexOf(a) + 1;
            d.Layers.Insert(i, l); d.ActiveId = l.Id;
        });
        App.NotifyLayers(); App.InvalidateAll();
        return l;
    }

    // ---- 그림 ↔ Bitmap ----
    public static unsafe Bitmap ToBitmap(uint[] px, int w, int h, uint background = 0)
    {
        var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride);
                for (int x = 0; x < w; x++) { uint c = px[y * w + x]; if (background != 0) c = Px.Over(c, background); row[x] = Px.Unpremul(c); }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    public static Bitmap Resize(Image src, int w, int h)
    {
        var b = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceCopy;
        using var ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(src, new Rectangle(0, 0, b.Width, b.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return b;
    }

    /// <summary>문서 합성 (내보내기용, 밑그림 빼고) → Bitmap. scale 배율, background 0 = 투명</summary>
    public static Bitmap FlattenBitmap(Document d, double scale = 1, uint background = 0, bool paper = true)
    {
        var flat = App.Comp.Flatten(d, new RenderOptions { ExcludeDraft = true, Paper = paper });
        var full = flat.Read(flat.Full);
        var bmp = ToBitmap(full, d.W, d.H, background);
        if (Math.Abs(scale - 1) < 1e-9) return bmp;
        var r = Resize(bmp, (int)Math.Round(d.W * scale), (int)Math.Round(d.H * scale));
        bmp.Dispose();
        return r;
    }

    public static byte[] EncodePng(Bitmap b) { using var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    public static byte[] EncodeJpeg(Bitmap b, double quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(Math.Round(quality * 100), 1, 100));
        // JPG 는 투명이 없으므로 흰 바탕에
        using var rgb = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(rgb)) { g.Clear(Color.White); g.DrawImageUnscaled(b, 0, 0); }
        using var ms = new MemoryStream();
        rgb.Save(ms, codec, ep);
        return ms.ToArray();
    }

    public static string SafeName(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s ?? "", "[\\\\/:*?\"<>|]+", "_").Trim();
        return s.Length == 0 ? "제목없음" : s;
    }
    public static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public static string ExportsDir { get { var p = Path.Combine(App.DataDir, "exports"); Directory.CreateDirectory(p); return p; } }
}
