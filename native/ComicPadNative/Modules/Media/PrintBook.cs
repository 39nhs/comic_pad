// 인쇄용 내보내기 — 여러 페이지 작품 전체 (웹 판 ui/printout.js 의 printExportDialog 의 "작품의 모든 페이지") + 책 펼침면 미리보기.
// PDF·TIFF·PNG 만들기 부품(BuildPdf·Tiff8·Tiff1Bit·ToBits·ICC)은 Filters 모듈의 PrintExport 를 그대로 쓰고,
// 여기서는 페이지 목록(스토리)을 돌며 한 PDF 로 묶는 일과 대화상자, 펼침면 보기만 한다.
using System.Drawing.Imaging;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.Filters;
using FField = ComicPadNative.Modules.Filters.Field;
using FUi = ComicPadNative.Modules.Filters.Ui;
using Opt = ComicPadNative.UI.Opt;

namespace ComicPadNative.Modules.Media;

public sealed class PrintOpts
{
    public string Fmt = "pdf", Mode = "mono", Area = "all";
    public double Threshold = 128; public bool Dither;
    public IccCube Cube; public byte[] Icc; public string IccName;
}

public static class PrintBook
{
    /// <summary>범위 (재단선 안·도련 포함·캔버스 전체) — 원고 설정이 없으면 캔버스 전체</summary>
    public static Rect AreaOf(Document d, string area)
    {
        var r = PrintExport.PrintRects(d);
        if (r == null || area == "all") return new Rect(0, 0, d.W, d.H);
        return area == "bleed" ? r.Value.bleed : r.Value.trim;
    }

    /// <summary>문서 → 잘라낸 페이지 (흰 바탕 RGB 바이트). 페이지마다 새 합성기 (다른 작품의 캐시와 섞이지 않게)</summary>
    public static (byte[] rgb, int w, int h) PageRgb(Document d, string area)
    {
        var flat = new Compositor().Flatten(d, new RenderOptions { ExcludeDraft = true, Paper = true });
        var q = AreaOf(d, area);
        var px = flat.Read(q);
        var rgb = new byte[q.W * q.H * 3];
        for (int i = 0; i < px.Length; i++)
        {
            uint c = Px.Over(px[i], 0xFFFFFFFF);
            rgb[i * 3] = (byte)(c >> 16); rgb[i * 3 + 1] = (byte)(c >> 8); rgb[i * 3 + 2] = (byte)c;
        }
        return (rgb, q.W, q.H);
    }

    static unsafe Bitmap RgbBitmap(byte[] rgb, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
        {
            uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride);
            for (int x = 0; x < w; x++) { int i = (y * w + x) * 3; row[x] = 0xFF000000u | ((uint)rgb[i] << 16) | ((uint)rgb[i + 1] << 8) | rgb[i + 2]; }
        }
        bmp.UnlockBits(bd);
        return bmp;
    }

    static PrintExport.PdfPage PdfPage(byte[] rgb, int w, int h, int dpi, PrintOpts o)
    {
        double wPt = w / (double)dpi * 72, hPt = h / (double)dpi * 72;
        if (o.Mode == "cmyk") return new(wPt, hPt, w, h, 8, "DeviceCMYK", "FlateDecode", PrintExport.Deflate(o.Cube.ToCmyk(rgb)));
        if (o.Mode == "color") { using var bmp = RgbBitmap(rgb, w, h); return new(wPt, hPt, w, h, 8, "DeviceRGB", "DCTDecode", ImageIO.EncodeJpeg(bmp, 0.92)); }
        var g = PrintExport.GrayOf(rgb);
        if (o.Mode == "gray") return new(wPt, hPt, w, h, 8, "DeviceGray", "FlateDecode", PrintExport.Deflate(g));
        return new(wPt, hPt, w, h, 1, "DeviceGray", "FlateDecode", PrintExport.Deflate(PrintExport.ToBits(g, w, h, o.Threshold, o.Dither)));
    }

    /// <summary>
    /// 페이지들을 내보냄 → PDF 파일 경로 또는 TIFF·PNG 폴더. pages 는 문서를 하나씩 불러오는 함수 (메모리를 아끼려고)
    /// </summary>
    public static string ExportPages(IList<Func<Document>> pages, string baseName, PrintOpts o, string outDir, Action<int> progress = null)
    {
        Directory.CreateDirectory(outDir);
        var pdf = new List<PrintExport.PdfPage>();
        string folder = Path.Combine(outDir, baseName);
        if (o.Fmt != "pdf") Directory.CreateDirectory(folder);
        for (int i = 0; i < pages.Count; i++)
        {
            var d = pages[i]();
            int dpi = d.Dpi > 0 ? d.Dpi : 350;
            var (rgb, w, h) = PageRgb(d, o.Area);
            string num = (i + 1).ToString("000");
            if (o.Fmt == "pdf") pdf.Add(PdfPage(rgb, w, h, dpi, o));
            else if (o.Fmt == "tif")
            {
                byte[] data = o.Mode switch
                {
                    "cmyk" => PrintExport.Tiff8(o.Cube.ToCmyk(rgb), w, h, 4, dpi, o.Icc),
                    "gray" => PrintExport.Tiff8(PrintExport.GrayOf(rgb), w, h, 1, dpi),
                    "color" => PrintExport.Tiff8(rgb, w, h, 3, dpi),
                    _ => PrintExport.Tiff1Bit(PrintExport.ToBits(PrintExport.GrayOf(rgb), w, h, o.Threshold, o.Dither), w, h, dpi),
                };
                File.WriteAllBytes(Path.Combine(folder, num + ".tif"), data);
            }
            else
            {
                if (o.Mode != "color")
                {
                    var g = PrintExport.GrayOf(rgb);
                    var bits = o.Mode == "mono" ? PrintExport.ToBits(g, w, h, o.Threshold, o.Dither) : null; int rb = (w + 7) / 8;
                    for (int j = 0; j < g.Length; j++)
                    {
                        byte val = bits != null ? (byte)(((bits[j / w * rb + ((j % w) >> 3)] >> (7 - (j % w & 7))) & 1) * 255) : g[j];
                        rgb[j * 3] = rgb[j * 3 + 1] = rgb[j * 3 + 2] = val;
                    }
                }
                using var bmp = RgbBitmap(rgb, w, h);
                bmp.Save(Path.Combine(folder, num + ".png"), ImageFormat.Png);
            }
            progress?.Invoke(i + 1);
        }
        if (o.Fmt != "pdf") return folder;
        string path = Path.Combine(outDir, baseName + ".pdf");
        File.WriteAllBytes(path, PrintExport.BuildPdf(pdf, o.Icc != null ? (o.Icc, o.IccName) : null));
        return path;
    }

    /// <summary>내보낼 페이지: 작품 전체면 story 의 페이지 파일들 (지금 페이지는 먼저 저장), 아니면 지금 문서 하나</summary>
    public static List<(Func<Document> load, string name)> PageDocs(bool all)
    {
        var doc = App.Doc;
        string story = Story.StoryOf(doc);
        if (!all || story == null) return new() { (() => doc, doc.Name) };
        if (doc.Modified && !Story.SaveCurrent()) return null;
        return Story.ListPages(story).Select(p => ((Func<Document>)(() => CpadIO.Load(Story.Full(p))), p)).ToList();
    }

    static PrintExport.ProfileRef AddProfileFile()
    {
        using var dlg = new OpenFileDialog { Filter = "ICC 프로파일|*.icc;*.icm" };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return null;
        var p = IccProfile.Parse(File.ReadAllBytes(dlg.FileName));
        if (p.Space != "CMYK") { App.Toast($"CMYK 프로파일이 아닙니다 ({p.Space})"); return null; }
        var dir = Path.Combine(App.DataDir, "assets", "profiles"); Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, Path.GetFileName(dlg.FileName));
        File.Copy(dlg.FileName, dst, true);
        App.Toast("인쇄 프로파일 추가: " + p.Name);
        return new(dst, Path.GetFileName(dst));
    }

    /// <summary>인쇄용 내보내기 창 (작품 전체 페이지를 한 PDF 로 / 쪽마다 TIFF·PNG)</summary>
    public static void Dialog()
    {
        var d = App.Doc; if (d == null) return;
        string story = Story.StoryOf(d);
        var profs = PrintExport.ListProfiles();
        string lastProf = App.Get<string>("printProfile", null);
        int profIdx = Math.Max(0, profs.FindIndex(p => p.Path == lastProf));
        var profOpts = profs.Select((p, i) => (i.ToString(), p.Label)).Append(("add", "+ 프로파일 파일 추가 (.icc/.icm)…")).ToArray();
        var v = FUi.Ask("인쇄용 내보내기", new[]
        {
            FField.Pick("fmt", "형식", new[] { ("pdf", "PDF 한 파일"), ("tif", "TIFF (쪽마다)"), ("png", "PNG (쪽마다)") }, "pdf"),
            FField.Pick("mode", "색", new[] { ("mono", "흑백 1비트"), ("gray", "그레이"), ("color", "RGB 컬러"), ("cmyk", "CMYK (인쇄 색)") }, "mono"),
            FField.Pick("prof", "CMYK 일 때 인쇄 프로파일 (인쇄소가 정해 주면 그것으로, 모르면 SWOP)", profOpts, profs.Count > 0 ? profIdx.ToString() : "add"),
            FField.Pick("intent", "CMYK 변환 방식", new[] { ("0", "지각적 (자연스럽게)"), ("1", "상대 색도 (정확하게)") }, App.Get("printIntent", 0.0).ToString("0")),
            FField.Pick("area", "범위", new[] { ("trim", "재단선 안"), ("bleed", "도련 포함"), ("all", "캔버스 전체") }, d.Extra["print"] != null ? "bleed" : "all"),
            FField.C("dither", "흑백일 때 회색을 점(디더링)으로 — 끄면 기준값으로 딱 나눔 (톤을 붙인 원고는 끄기)", false),
            FField.N("threshold", "흑백 기준 밝기 (0~255)", 128, 1, 254),
            FField.C("all", story != null ? $"작품의 모든 페이지 ({story})" : "여러 페이지 작품이 아니라 지금 페이지만 나갑니다", story != null),
            FField.Info($"해상도 {(d.Dpi > 0 ? d.Dpi : 350)}dpi 기준으로 실제 크기가 정해집니다 (원고 설정에서 변경). 밑그림·러프 레이어와 메모는 빠집니다."),
        }, "내보내기");
        if (v == null) return;
        var o = new PrintOpts { Fmt = v.S("fmt"), Mode = v.S("mode"), Area = v.S("area"), Threshold = Math.Clamp(v.N("threshold") > 0 ? v.N("threshold") : 128, 1, 254), Dither = v.B("dither") };
        if (o.Mode == "cmyk")
        {
            var sel = v.S("prof") == "add" ? AddProfileFile() : profs.ElementAtOrDefault(int.Parse(v.S("prof")));
            if (sel == null) { App.Toast("CMYK 로 내보내려면 인쇄 프로파일이 필요합니다"); return; }
            int intent = int.Parse(v.S("intent"));
            try
            {
                o.Cube = FUi.Busy(() => PrintExport.CubeFor(sel.Path, intent));
                o.Icc = File.ReadAllBytes(sel.Path); o.IccName = IccProfile.Parse(o.Icc).Name;
            }
            catch (Exception ex) { App.Toast(ex.Message); return; }
            App.Set("printProfile", sel.Path); App.Set("printIntent", intent);
        }
        var list = PageDocs(v.B("all"));
        if (list == null) return;
        string baseName = $"{ImageIO.SafeName(story != null && v.B("all") ? story : d.Name)}_인쇄_{ImageIO.Stamp()}";
        string outPath = null;
        U.Task($"인쇄용 {list.Count}쪽", () =>
        {
            try { outPath = ExportPages(list.Select(x => x.load).ToList(), baseName, o, ImageIO.ExportsDir); }
            catch (Exception ex) { App.Toast("내보내지 못했습니다: " + ex.Message); }
        });
        if (outPath != null) App.Toast($"내보냄 ({list.Count}쪽): {outPath}");
    }

    // ================================================================== 펼침면 미리보기
    /// <summary>펼침: 1쪽은 혼자 (책의 첫 장), 이후 2·3, 4·5 …</summary>
    public static List<int[]> Spreads(int n)
    {
        var s = new List<int[]>();
        if (n <= 0) return s;
        s.Add(new[] { 0 });
        for (int i = 1; i < n; i += 2) s.Add(i + 1 < n ? new[] { i, i + 1 } : new[] { i });
        return s;
    }

    /// <summary>화면 왼쪽부터 놓을 쪽 (null = 빈 쪽). 왼쪽 넘김(한국식): 짝수쪽이 왼쪽 / 오른쪽 넘김(일본식): 반대.
    /// 첫 쪽: 한국식 책은 오른쪽, 일본식 책은 왼쪽에 혼자</summary>
    public static int?[] Layout(int[] s, bool rtl)
    {
        if (s.Length == 2) return rtl ? new int?[] { s[1], s[0] } : new int?[] { s[0], s[1] };
        if (s[0] == 0) return rtl ? new int?[] { 0, null } : new int?[] { null, 0 };
        return new int?[] { s[0] };
    }

    /// <summary>쪽 그림: 재단선 안, 높이 maxH 에 맞춰 (1배 이하)</summary>
    public static Bitmap PageImage(Document d, int maxH)
    {
        var q = AreaOf(d, "trim");
        double s = Math.Min(1, maxH / (double)q.H);
        using var full = s < 0.999 ? Extras.Img.FlattenScaled(d, s, true, new Compositor()) : Extras.Img.Flatten(d, 1, true, new Compositor());
        var r = new Rectangle((int)Math.Round(q.X * s), (int)Math.Round(q.Y * s), Math.Max(1, (int)Math.Round(q.W * s)), Math.Max(1, (int)Math.Round(q.H * s)));
        return Extras.Img.Crop(full, r);
    }

    /// <summary>펼침면 한 장 그림 (쪽 사이 가는 틈, 빈 쪽은 어둡게)</summary>
    public static Bitmap Compose(IList<Bitmap> pages, int?[] layout, int h)
    {
        int Wof(int? i) => i is int k ? pages[k].Width : pages.Where(p => p != null).Select(p => p.Width).DefaultIfEmpty(h * 7 / 10).First();
        const int gap = 2;
        int W = layout.Sum(Wof) + gap * (layout.Length - 1);
        var b = new Bitmap(Math.Max(1, W), Math.Max(1, h), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.FromArgb(24, 24, 28));
        int x = 0;
        foreach (var i in layout)
        {
            int w = Wof(i);
            if (i is int k) g.DrawImage(pages[k], new Rectangle(x, 0, w, Math.Min(h, pages[k].Height)));
            else using (var br = new SolidBrush(Color.FromArgb(48, 48, 54))) g.FillRectangle(br, x, 0, w, h);
            x += w + gap;
        }
        return b;
    }

    public static void SpreadPreview()
    {
        var doc = App.Doc; if (doc == null) return;
        string story = Story.StoryOf(doc);
        var list = PageDocs(story != null);
        if (list == null) return;
        bool rtl = App.Get("bookRtl", false);
        int at = 0;
        var area = FUi.WorkArea();
        int H = (int)Math.Min(area.Height * 0.7, 820);
        var cache = new Dictionary<int, Bitmap>();
        var spreads = Spreads(list.Count);
        var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 24, 28) };
        var info = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 24, ForeColor = Opt.Muted, TextAlign = ContentAlignment.MiddleLeft };
        Bitmap Page(int i)
        {
            if (!cache.TryGetValue(i, out var b)) cache[i] = b = PageImage(list[i].load(), H);
            return b;
        }
        void Draw()
        {
            var s = spreads[at];
            var old = pb.Cursor; pb.Cursor = Cursors.WaitCursor;
            try
            {
                var pages = new Bitmap[list.Count];
                foreach (int i in s) pages[i] = Page(i);
                var prev = pb.Image;
                pb.Image = Compose(pages, Layout(s, rtl), H);
                prev?.Dispose();
                info.Text = $"  {string.Join(" · ", s.Select(i => i + 1))}쪽 / 전체 {list.Count}쪽 · {(rtl ? "오른쪽으로 넘김 (일본식)" : "왼쪽으로 넘김 (한국식)")} · 방향키로 넘기기";
            }
            catch (Exception ex) { info.Text = "  그리지 못했습니다: " + ex.Message; }
            finally { pb.Cursor = old; }
        }
        void Go(int dir) { int n = at + (rtl ? -dir : dir); if (n >= 0 && n < spreads.Count) { at = n; Draw(); } }
        var bar = FUi.RowP(); bar.Dock = DockStyle.Top;
        bar.Controls.Add(FUi.Btn("◀", () => Go(-1)));
        bar.Controls.Add(FUi.Btn("▶", () => Go(1)));
        bar.Controls.Add(FUi.Btn("넘김 방향 바꾸기", () => { rtl = !rtl; App.Set("bookRtl", rtl); Draw(); }));
        var body = new Panel { BackColor = Opt.Bg };
        body.Controls.Add(pb); body.Controls.Add(info); body.Controls.Add(bar);
        using var f = new KeyForm
        {
            Text = story != null ? $"펼침면 미리보기: {story}" : "펼침면 미리보기 (한 페이지)", StartPosition = FormStartPosition.CenterParent,
            Width = Math.Min(area.Width - 80, 1400), Height = Math.Min(area.Height - 60, H + 140), BackColor = Opt.Bg, ForeColor = Opt.Fg,
            FormBorderStyle = FormBorderStyle.SizableToolWindow, Font = new Font("Malgun Gothic", 9f),
        };
        body.Dock = DockStyle.Fill;
        f.Controls.Add(body);
        // 방향키는 버튼 사이 이동보다 먼저 가로챔
        f.OnKey = k => { if (k == Keys.Right) { Go(1); return true; } if (k == Keys.Left) { Go(-1); return true; } if (k == Keys.Escape) { f.Close(); return true; } return false; };
        f.Shown += (_, _) => Draw();
        f.ShowDialog(App.Main);
        pb.Image?.Dispose();
        foreach (var b in cache.Values) b.Dispose();
    }
}

/// <summary>단축키를 먼저 받는 창 (방향키로 넘기기)</summary>
sealed class KeyForm : Form
{
    public Func<Keys, bool> OnKey;
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => OnKey?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);
}
