// 정지 그림 내보내기·열기 — 웹 판 ui/files.js exportDialog + core/io.js exportImage (형식·배율·배경 빼기·재단선 범위),
// WebP 저장(무손실 직접 인코더)·열기(.webp: WIC → 안 되면 직접 디코더), 작품 전체 내보내기에 PSD (웹 판 story.js exportStory).
using System.Drawing.Imaging;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.Filters;
using Field = ComicPadNative.Modules.Extras.Field;
using ComicPadNative.UI;
using AXform = ComicPadNative.Modules.Anim.Xform;

namespace ComicPadNative.Modules.Media;

public static class StillExport
{
    // ------------------------------------------------------------------ 그리기
    /// <summary>
    /// 내보낼 그림 (웹 exportImage): 밑그림 빼고, transparent 면 용지 없이, bg 를 주면 그 색 바탕, area = trim|bleed|all, scale 배율.
    /// 반환: 미리 곱하지 않은 ARGB
    /// </summary>
    public static Argb Render(Document d, double scale = 1, bool transparent = false, string area = "all", uint? bg = null)
    {
        var flat = new Compositor().Flatten(d, new RenderOptions { ExcludeDraft = true, Paper = !transparent });
        var q = PrintBook.AreaOf(d, area);
        var px = flat.Read(q);
        if (bg is uint b) for (int i = 0; i < px.Length; i++) px[i] = Px.Over(px[i], b);
        int w = Math.Max(1, (int)Math.Round(q.W * scale)), h = Math.Max(1, (int)Math.Round(q.H * scale));
        if (w != q.W || h != q.H) px = scale < 1 ? AXform.Resize(px, q.W, q.H, w, h) : Upscale(px, q.W, q.H, w, h);
        return Argb.FromPremul(px, w, h);
    }

    /// <summary>키우기: 쌍선형 (웹 캔버스의 부드러운 확대와 비슷하게)</summary>
    static uint[] Upscale(uint[] src, int sw, int sh, int dw, int dh)
    {
        var o = new uint[dw * dh];
        double kx = (double)sw / dw, ky = (double)sh / dh;
        Parallel.For(0, dh, y =>
        {
            double sy = Math.Clamp((y + 0.5) * ky, 0.5, sh - 0.5);   // 픽셀 가운데 = +0.5 (가장자리는 안쪽 픽셀로)
            for (int x = 0; x < dw; x++) o[y * dw + x] = AXform.Bilinear(src, sw, sh, Math.Clamp((x + 0.5) * kx, 0.5, sw - 0.5), sy);
        });
        return o;
    }

    public static unsafe Bitmap ToBitmap(Argb im)
    {
        var bmp = new Bitmap(im.W, im.H, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, im.W, im.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < im.H; y++) new ReadOnlySpan<uint>(im.P, y * im.W, im.W).CopyTo(new Span<uint>((byte*)bd.Scan0 + y * bd.Stride, im.W)); }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    public static byte[] Encode(Argb im, string fmt, double quality = 0.92)
    {
        if (fmt == "webp") return WebP.EncodeStill(im, quality);
        using var bmp = ToBitmap(im);
        return fmt == "jpg" ? ImageIO.EncodeJpeg(bmp, quality) : ImageIO.EncodePng(bmp);
    }

    // ------------------------------------------------------------------ 이미지 내보내기 창
    public static void Dialog()
    {
        var d = App.Doc; if (d == null) return;
        bool print = d.Extra["print"] != null;
        var v = Dlg.Form("이미지 내보내기", new[]
        {
            Field.Segment("format", "형식", App.Get("exportFmt", "png"), ("png", "PNG"), ("jpg", "JPG"), ("webp", "WebP")),
            Field.Segment("scale", "배율", "1", ("0.25", "25%"), ("0.5", "50%"), ("1", "100%"), ("2", "200%")),
            Field.Check("transparent", "배경(용지) 빼기 — PNG·WebP 투명", !d.PaperVisible),
            Field.Segment("quality", "WebP 화질", "1", ("1", "무손실"), ("0.9", "좋게"), ("0.8", "보통")),
            Field.Check("pick", "저장할 곳 직접 고르기 (끄면 data/exports 에 저장)", false),
            print ? Field.Segment("area", "범위", "bleed", ("trim", "재단선 안"), ("bleed", "도련 포함"), ("all", "캔버스 전체")) : null,
            Field.Info("밑그림(러프) 레이어와 숨긴 레이어, 메모 핀은 내보내지 않습니다. WebP 는 무손실(또는 거의 무손실)로 만듭니다."),
        }, "내보내기");
        if (v == null) return;
        string fmt = (string)v["format"];
        App.Settings["exportFmt"] = fmt; App.SaveSettingsSoon();
        double scale = double.Parse((string)v["scale"], System.Globalization.CultureInfo.InvariantCulture);
        double quality = fmt == "webp" ? double.Parse((string)v["quality"], System.Globalization.CultureInfo.InvariantCulture) : 0.92;
        string name = $"{U.SafeName(d.Name)}_{U.FileStamp()}.{fmt}", path = Path.Combine(U.ExportsDir, name);
        if ((bool)v["pick"])
        {
            using var sd = new SaveFileDialog { Filter = fmt == "jpg" ? "JPEG|*.jpg" : fmt == "webp" ? "WebP|*.webp" : "PNG|*.png", FileName = name, InitialDirectory = U.ExportsDir };
            if (sd.ShowDialog(App.Main) != DialogResult.OK) return;
            path = sd.FileName;
        }
        try
        {
            uint? bg = fmt == "jpg" ? (d.PaperVisible ? Px.FromHex(d.PaperColor, 0xFFFFFFFF) : 0xFFFFFFFF) : null;   // JPG 는 용지 색 바탕
            var bytes = FUiBusy(() => Encode(Render(d, scale, (bool)v["transparent"] && fmt != "jpg", (string)v["area"] ?? "all", bg), fmt, quality));
            File.WriteAllBytes(path, bytes);
            App.Toast($"내보냄: {path} ({bytes.Length / 1024}KB)");
        }
        catch (Exception ex) { App.Toast("내보내지 못했습니다: " + ex.Message); }
    }
    static T FUiBusy<T>(Func<T> f) => Filters.Ui.Busy(f);

    // ------------------------------------------------------------------ 열기·가져오기
    /// <summary>.webp 열기 (새 작품)</summary>
    public static Document OpenWebP(string path)
    {
        var im = WebP.Load(path);
        var d = new Document(im.W, im.H) { Name = Path.GetFileNameWithoutExtension(path) };
        var l = new Layer(d, Path.GetFileName(path));
        l.Px.Write(new Rect(0, 0, im.W, im.H), im.ToPremul());
        l.Px.Compact();
        d.Layers.Add(l); d.ActiveId = l.Id;
        return d;
    }

    /// <summary>이미지 가져오기 (새 레이어) — WebP 도 받음</summary>
    public static void ImportImage()
    {
        using var dlg = new OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|모든 파일|*.*" };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        try
        {
            Bitmap bmp;
            if (dlg.FileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) bmp = ToBitmap(WebP.Load(dlg.FileName));
            else bmp = ImageIO.LoadBitmap(dlg.FileName);
            using (bmp) ImageIO.PlaceImage(bmp, Path.GetFileNameWithoutExtension(dlg.FileName));
        }
        catch (Exception ex) { App.Toast("이미지를 열지 못했습니다: " + ex.Message); }
    }

    // ------------------------------------------------------------------ 작품 전체 내보내기 (PNG·JPG·WebP·PSD)
    public static void ExportStory()
    {
        string story = Story.StoryOf(App.Doc);
        if (story == null) { App.Toast("여러 페이지 작품을 먼저 여세요"); return; }
        var v = Dlg.Form("작품 전체 내보내기", new[]
        {
            Field.Segment("fmt", "형식", "png", ("png", "PNG"), ("jpg", "JPG"), ("webp", "WebP"), ("psd", "PSD")),
            Field.Segment("scale", "배율", "1", ("0.5", "50%"), ("1", "100%")),
            Field.Info("PSD 는 레이어를 그대로 (배율 무시). 결과는 data/exports/<작품>_내보내기 에 저장합니다."),
        }, "내보내기");
        if (v == null) return;
        if (App.Doc.Modified && !Story.SaveCurrent()) return;
        var pages = Story.ListPages(story);
        string fmt = (string)v["fmt"]; double scale = double.Parse((string)v["scale"], System.Globalization.CultureInfo.InvariantCulture);
        string outDir = Path.Combine(U.ExportsDir, story + "_내보내기");
        bool ok = false;
        U.Task($"{pages.Count}쪽 내보내기", () =>
        {
            try { ExportPages(pages.Select(Story.Full).ToList(), outDir, fmt, scale); ok = true; }
            catch (Exception ex) { App.Toast("내보내지 못했습니다: " + ex.Message); }
        });
        if (ok) App.Toast($"data/exports/{story}_내보내기 에 저장했습니다");
    }

    /// <summary>페이지 파일들 → 001.png … (웹 판과 같은 이름). PSD 는 레이어 유지, 흰 바탕 합성 그림 포함</summary>
    public static void ExportPages(List<string> files, string outDir, string fmt, double scale)
    {
        Directory.CreateDirectory(outDir);
        for (int i = 0; i < files.Count; i++)
        {
            var d = CpadIO.Load(files[i]);
            string bas = Path.Combine(outDir, (i + 1).ToString("000"));
            if (fmt == "psd")
            {
                var flat = new Compositor().Flatten(d, new RenderOptions { ExcludeDraft = false, Paper = true });
                for (int ti = 0; ti < flat.TileCount; ti++)
                {
                    var r = flat.TileRect(ti);
                    var px = flat.Read(r);
                    for (int k = 0; k < px.Length; k++) px[k] = Px.Over(px[k], 0xFFFFFFFF);
                    flat.Write(r, px);
                }
                File.WriteAllBytes(bas + ".psd", PsdWriter.Write(d, flat));
                continue;
            }
            var im = Render(d, scale, false, "all", 0xFFFFFFFF);   // 흰 바탕 (웹: background '#ffffff')
            File.WriteAllBytes(bas + "." + fmt, Encode(im, fmt, 0.92));
        }
    }
}
