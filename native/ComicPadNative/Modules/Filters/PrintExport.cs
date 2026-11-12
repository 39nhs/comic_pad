// 인쇄용 내보내기 (웹 ui/printout.js): PDF 한 파일 / TIFF(1비트·그레이·RGB·CMYK) / PNG, 재단선 기준 범위,
// CMYK 는 ICC 프로파일(Windows 색 폴더 + data/assets/profiles)로 변환해 PDF 출력 의도(OutputIntent)·TIFF 에 프로파일을 넣음.
// 여러 페이지 작품(스토리)은 아직 지금 페이지만.
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

public static class PrintExport
{
    static Document D => App.Doc;

    // ---------------------------------------------------------------- 페이지 그림
    /// <summary>웹 printRects: 재단선·도련 (doc.print 가 없으면 null)</summary>
    public static (Rect trim, Rect bleed)? PrintRects(Document d)
    {
        if (d.Extra["print"] is not JsonObject p) return null;
        int tw = (int)Math.Min(Svg.Num(p, "trimW", d.W), d.W), th = (int)Math.Min(Svg.Num(p, "trimH", d.H), d.H);
        int bl = (int)Svg.Num(p, "bleed");
        var trim = new Rect((int)Math.Round((d.W - tw) / 2.0, MidpointRounding.AwayFromZero), (int)Math.Round((d.H - th) / 2.0, MidpointRounding.AwayFromZero), tw, th);
        return (trim, new Rect(trim.X - bl, trim.Y - bl, tw + bl * 2, th + bl * 2));
    }
    static Rect AreaOf(Document d, string area)
    {
        var r = PrintRects(d);
        if (r == null || area == "all") return new Rect(0, 0, d.W, d.H);
        return area == "bleed" ? r.Value.bleed : r.Value.trim;
    }

    /// <summary>문서 → 잘라낸 페이지 (흰 바탕, 불투명 RGB 바이트)</summary>
    public static (byte[] rgb, int w, int h) PageRgb(Document d, string area)
    {
        var flat = App.Comp.Flatten(d, new RenderOptions { ExcludeDraft = true, Paper = true });
        var q = AreaOf(d, area);
        var rgb = new byte[q.W * q.H * 3];
        var px = flat.Read(q);   // 문서 밖은 0 (투명 → 흰색)
        for (int i = 0; i < px.Length; i++)
        {
            uint c = Px.Over(px[i], 0xFFFFFFFF);
            rgb[i * 3] = (byte)((c >> 16) & 255); rgb[i * 3 + 1] = (byte)((c >> 8) & 255); rgb[i * 3 + 2] = (byte)(c & 255);
        }
        return (rgb, q.W, q.H);
    }

    public static byte[] GrayOf(byte[] rgb)
    {
        var g = new byte[rgb.Length / 3];
        for (int j = 0; j < g.Length; j++) g[j] = (byte)Math.Round(0.299 * rgb[j * 3] + 0.587 * rgb[j * 3 + 1] + 0.114 * rgb[j * 3 + 2], MidpointRounding.AwayFromZero);
        return g;
    }

    /// <summary>1비트: 비트 1 = 흰색 (한 줄은 바이트 단위). dither 면 플로이드-스타인버그</summary>
    public static byte[] ToBits(byte[] gray, int w, int h, double threshold = 128, bool dither = false)
    {
        int rowBytes = (w + 7) / 8;
        var o = new byte[rowBytes * h];
        float[] err = dither ? gray.Select(v => (float)v).ToArray() : null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double v = dither ? err[i] : gray[i];
                bool white = v >= threshold;
                if (white) o[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                if (dither)
                {
                    float e = (float)(v - (white ? 255 : 0));
                    if (x + 1 < w) err[i + 1] += e * 7 / 16;
                    if (y + 1 < h)
                    {
                        if (x > 0) err[i + w - 1] += e * 3 / 16;
                        err[i + w] += e * 5 / 16;
                        if (x + 1 < w) err[i + w + 1] += e / 16;
                    }
                }
            }
        return o;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- PDF
    public sealed record PdfPage(double WPt, double HPt, int W, int H, int Bpc, string Cs, string Filter, byte[] Data);

    public static byte[] BuildPdf(List<PdfPage> pages, (byte[] icc, string name)? intent = null)
    {
        var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();
        void Put(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b); }
        void Obj(int n, string body, byte[] stream = null)
        {
            offsets[n] = ms.Position;
            Put($"{n} 0 obj\n{body}\n");
            if (stream != null) { Put("stream\n"); ms.Write(stream); Put("\nendstream\n"); }
            Put("endobj\n");
        }
        ms.Write(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A, 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });   // %PDF-1.4 + 이진 표시
        int n = pages.Count;
        string kids = string.Join(" ", pages.Select((_, i) => $"{3 + i * 3} 0 R"));
        int iccObj = 3 + n * 3;
        string Ascii(string t) => new string((t ?? "CMYK").Where(c => c >= 0x20 && c <= 0x7e && c != '(' && c != ')' && c != '\\').ToArray());
        string F(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Obj(1, intent != null
            ? $"<< /Type /Catalog /Pages 2 0 R /OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier ({Ascii(intent.Value.name)}) /Info ({Ascii(intent.Value.name)}) /DestOutputProfile {iccObj} 0 R >>] >>"
            : "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Kids [{kids}] /Count {n} >>");
        for (int i = 0; i < n; i++)
        {
            var p = pages[i];
            int pg = 3 + i * 3, content = pg + 1, img = pg + 2;
            var cs = Encoding.Latin1.GetBytes($"q {F(p.WPt)} 0 0 {F(p.HPt)} 0 0 cm /Im0 Do Q");
            Obj(pg, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(p.WPt)} {F(p.HPt)}] /Resources << /XObject << /Im0 {img} 0 R >> >> /Contents {content} 0 R >>");
            Obj(content, $"<< /Length {cs.Length} >>", cs);
            Obj(img, $"<< /Type /XObject /Subtype /Image /Width {p.W} /Height {p.H} /ColorSpace /{p.Cs} /BitsPerComponent {p.Bpc} /Filter /{p.Filter} /Length {p.Data.Length} >>", p.Data);
        }
        if (intent != null) Obj(iccObj, $"<< /N 4 /Length {intent.Value.icc.Length} >>", intent.Value.icc);
        long xref = ms.Position;
        int total = 3 + n * 3 + (intent != null ? 1 : 0);
        var x = new StringBuilder($"xref\n0 {total}\n0000000000 65535 f \n");
        for (int i = 1; i < total; i++) x.Append($"{offsets[i]:0000000000} 00000 n \n");
        Put(x.ToString());
        Put($"trailer\n<< /Size {total} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    static PdfPage Page(byte[] rgb, int w, int h, string mode, int dpi, double threshold, bool dither, IccCube cube)
    {
        double wPt = w / (double)dpi * 72, hPt = h / (double)dpi * 72;
        if (mode == "cmyk") return new(wPt, hPt, w, h, 8, "DeviceCMYK", "FlateDecode", Deflate(cube.ToCmyk(rgb)));
        if (mode == "color")
        {
            using var bmp = RgbBitmap(rgb, w, h);
            return new(wPt, hPt, w, h, 8, "DeviceRGB", "DCTDecode", ImageIO.EncodeJpeg(bmp, 0.92));
        }
        var g = GrayOf(rgb);
        if (mode == "gray") return new(wPt, hPt, w, h, 8, "DeviceGray", "FlateDecode", Deflate(g));
        return new(wPt, hPt, w, h, 1, "DeviceGray", "FlateDecode", Deflate(ToBits(g, w, h, threshold, dither)));
    }

    static unsafe Bitmap RgbBitmap(byte[] rgb, int w, int h)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
        {
            uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride);
            for (int x = 0; x < w; x++) { int i = (y * w + x) * 3; row[x] = 0xFF000000u | ((uint)rgb[i] << 16) | ((uint)rgb[i + 1] << 8) | rgb[i + 2]; }
        }
        bmp.UnlockBits(bd);
        return bmp;
    }

    // ---------------------------------------------------------------- TIFF
    /// <summary>8비트 TIFF: 그레이(1)·RGB(3)·CMYK(4), Deflate 압축, 인쇄 프로파일 포함</summary>
    public static byte[] Tiff8(byte[] data, int w, int h, int spp, int dpi, byte[] icc = null)
    {
        var z = Deflate(data);
        int photometric = spp == 1 ? 1 : spp == 3 ? 2 : 5;
        var tags = new List<(int tag, int type, int count, object val)>
        {
            (256, 4, 1, w), (257, 4, 1, h), (258, 3, spp, spp == 1 ? 8 : "bps"), (259, 3, 1, 8), (262, 3, 1, photometric),
            (273, 4, 1, "data"), (277, 3, 1, spp), (278, 4, 1, h), (279, 4, 1, z.Length), (282, 5, 1, "xres"), (283, 5, 1, "yres"), (284, 3, 1, 1), (296, 3, 1, 2),
        };
        if (spp == 4) tags.Add((332, 3, 1, 1));
        if (icc != null) tags.Add((34675, 7, icc.Length, "icc"));
        tags.Sort((a, b) => a.tag.CompareTo(b.tag));
        int ifd = 8, ifdSize = 2 + tags.Count * 12 + 4;
        int extra = ifd + ifdSize;
        var place = new Dictionary<string, int>();
        void Reserve(string key, int n) { place[key] = extra; extra += n + (n % 2); }
        if (spp > 1) Reserve("bps", spp * 2);
        Reserve("xres", 8); Reserve("yres", 8);
        if (icc != null) Reserve("icc", icc.Length);
        Reserve("data", z.Length);
        var buf = new byte[extra];
        void U16(int o, int v) => BitConverter.TryWriteBytes(buf.AsSpan(o), (ushort)v);
        void U32(int o, uint v) => BitConverter.TryWriteBytes(buf.AsSpan(o), v);
        buf[0] = 0x49; buf[1] = 0x49; U16(2, 42); U32(4, (uint)ifd); U16(ifd, tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            var (tag, type, count, val) = tags[i];
            int o = ifd + 2 + i * 12;
            U16(o, tag); U16(o + 2, type); U32(o + 4, (uint)count);
            int value = val is string s ? place[s] : (int)val;
            if (type == 3 && count == 1) U16(o + 8, value); else U32(o + 8, (uint)value);
        }
        if (spp > 1) for (int k = 0; k < spp; k++) U16(place["bps"] + k * 2, 8);
        U32(place["xres"], (uint)dpi); U32(place["xres"] + 4, 1);
        U32(place["yres"], (uint)dpi); U32(place["yres"] + 4, 1);
        if (icc != null) Array.Copy(icc, 0, buf, place["icc"], icc.Length);
        Array.Copy(z, 0, buf, place["data"], z.Length);
        return buf;
    }

    /// <summary>1비트 TIFF (인쇄소에 흔히 내는 형식, 압축 없음)</summary>
    public static byte[] Tiff1Bit(byte[] bits, int w, int h, int dpi)
    {
        var tags = new (int tag, int type, int count, int value)[]
        {
            (256, 4, 1, w), (257, 4, 1, h), (258, 3, 1, 1), (259, 3, 1, 1), (262, 3, 1, 0),
            (273, 4, 1, 0), (277, 3, 1, 1), (278, 4, 1, h), (279, 4, 1, bits.Length),
            (282, 5, 1, 0), (283, 5, 1, 0), (296, 3, 1, 2),
        };
        int ifdSize = 2 + tags.Length * 12 + 4;
        int resOff = 8 + ifdSize, dataOff = resOff + 16;
        var buf = new byte[dataOff + bits.Length];
        void U16(int o, int v) => BitConverter.TryWriteBytes(buf.AsSpan(o), (ushort)v);
        void U32(int o, uint v) => BitConverter.TryWriteBytes(buf.AsSpan(o), v);
        buf[0] = 0x49; buf[1] = 0x49; U16(2, 42); U32(4, 8);
        U16(8, tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            var (tag, type, count, value) = tags[i];
            int p = 10 + i * 12;
            U16(p, tag); U16(p + 2, type); U32(p + 4, (uint)count);
            if (tag == 273) value = dataOff;
            if (tag == 282) value = resOff;
            if (tag == 283) value = resOff + 8;
            if (type == 3) U16(p + 8, value); else U32(p + 8, (uint)value);
        }
        U32(10 + tags.Length * 12, 0);
        U32(resOff, (uint)dpi); U32(resOff + 4, 1); U32(resOff + 8, (uint)dpi); U32(resOff + 12, 1);
        // WhiteIsZero: 우리 비트는 1 = 흰색이므로 뒤집어 넣는다
        for (int i = 0; i < bits.Length; i++) buf[dataOff + i] = (byte)~bits[i];
        return buf;
    }

    // ---------------------------------------------------------------- 인쇄 프로파일
    public sealed record ProfileRef(string Path, string Label);
    public static List<ProfileRef> ListProfiles()
    {
        var o = new List<ProfileRef>();
        try
        {
            var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
            if (Directory.Exists(win)) foreach (var f in Directory.GetFiles(win).Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                    o.Add(new(f, Path.GetFileName(f) + " (Windows)"));
        }
        catch { }
        try
        {
            var mine = Path.Combine(App.DataDir, "assets", "profiles");
            if (Directory.Exists(mine)) foreach (var f in Directory.GetFiles(mine).Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                    o.Add(new(f, Path.GetFileName(f)));
        }
        catch { }
        return o;
    }

    static ProfileRef AddProfileFile()
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

    static readonly Dictionary<string, IccCube> cubes = new();
    public static IccCube CubeFor(string path, int intent)
    {
        string key = path + ":" + intent;
        lock (cubes)
        {
            if (cubes.TryGetValue(key, out var c)) return c;
            return cubes[key] = IccCube.Build(new IccTransform(IccProfile.Parse(File.ReadAllBytes(path)), intent));
        }
    }

    // ---------------------------------------------------------------- 대화상자
    public static void Dialog()
    {
        var d = D;
        var profs = ListProfiles();
        string lastProf = App.Get<string>("printProfile", null);
        int profIdx = Math.Max(0, profs.FindIndex(p => p.Path == lastProf));
        var profOpts = profs.Select((p, i) => (i.ToString(), p.Label)).Append(("add", "+ 프로파일 파일 추가 (.icc/.icm)…")).ToArray();
        var v = Ui.Ask("인쇄용 내보내기", new[]
        {
            Field.Pick("fmt", "형식", new[] { ("pdf", "PDF 한 파일"), ("tif", "TIFF (쪽마다)"), ("png", "PNG (쪽마다)") }, "pdf"),
            Field.Pick("mode", "색", new[] { ("mono", "흑백 1비트"), ("gray", "그레이"), ("color", "RGB 컬러"), ("cmyk", "CMYK (인쇄 색)") }, "mono"),
            Field.Pick("prof", "CMYK 일 때 인쇄 프로파일 (인쇄소가 정해 주면 그것으로, 모르면 SWOP)", profOpts, profs.Count > 0 ? profIdx.ToString() : "add"),
            Field.Pick("intent", "CMYK 변환 방식", new[] { ("0", "지각적 (자연스럽게)"), ("1", "상대 색도 (정확하게)") }, App.Get("printIntent", 0.0).ToString("0")),
            Field.Pick("area", "범위", new[] { ("trim", "재단선 안"), ("bleed", "도련 포함"), ("all", "캔버스 전체") }, d.Extra["print"] != null ? "bleed" : "all"),
            Field.C("dither", "흑백일 때 회색을 점(디더링)으로 — 끄면 기준값으로 딱 나눔 (톤을 붙인 원고는 끄기)", false),
            Field.N("threshold", "흑백 기준 밝기 (0~255)", 128, 1, 254),
            Field.Info($"해상도 {(d.Dpi > 0 ? d.Dpi : 350)}dpi 기준으로 실제 크기가 정해집니다 (원고 설정에서 변경). 밑그림·러프 레이어와 메모는 빠집니다. 여러 페이지 작품은 지금 페이지만 나갑니다."),
        }, "내보내기");
        if (v == null) return;
        double threshold = Math.Clamp(v.N("threshold") > 0 ? v.N("threshold") : 128, 1, 254);
        bool dither = v.B("dither");
        string mode = v.S("mode"), fmt = v.S("fmt");
        IccCube cube = null; byte[] iccBytes = null; string iccName = null;
        if (mode == "cmyk")
        {
            var sel = v.S("prof") == "add" ? AddProfileFile() : profs.ElementAtOrDefault(int.Parse(v.S("prof")));
            if (sel == null) { App.Toast("CMYK 로 내보내려면 인쇄 프로파일이 필요합니다"); return; }
            int intent = int.Parse(v.S("intent"));
            try
            {
                cube = Ui.Busy(() => CubeFor(sel.Path, intent));
                iccBytes = File.ReadAllBytes(sel.Path); iccName = IccProfile.Parse(iccBytes).Name;
            }
            catch (Exception ex) { App.Toast(ex.Message); return; }
            App.Set("printProfile", sel.Path); App.Set("printIntent", intent);
        }
        string b = $"{ImageIO.SafeName(d.Name)}_인쇄_{ImageIO.Stamp()}";
        string outPath = null;
        Ui.Busy(() =>
        {
            int dpi = d.Dpi > 0 ? d.Dpi : 350;
            var (rgb, w, h) = PageRgb(d, v.S("area"));
            const string num = "001";
            if (fmt == "pdf")
            {
                var page = Page(rgb, w, h, mode, dpi, threshold, dither, cube);
                outPath = Path.Combine(ImageIO.ExportsDir, b + ".pdf");
                File.WriteAllBytes(outPath, BuildPdf(new() { page }, iccBytes != null ? (iccBytes, iccName) : null));
                return;
            }
            string dir = Path.Combine(ImageIO.ExportsDir, b); Directory.CreateDirectory(dir);
            outPath = dir;
            if (fmt == "tif")
            {
                byte[] data = mode switch
                {
                    "cmyk" => Tiff8(cube.ToCmyk(rgb), w, h, 4, dpi, iccBytes),
                    "gray" => Tiff8(GrayOf(rgb), w, h, 1, dpi),
                    "color" => Tiff8(rgb, w, h, 3, dpi),
                    _ => Tiff1Bit(ToBits(GrayOf(rgb), w, h, threshold, dither), w, h, dpi),
                };
                File.WriteAllBytes(Path.Combine(dir, num + ".tif"), data);
            }
            else
            {
                if (mode != "color")
                {
                    var g = GrayOf(rgb);
                    var bits = mode == "mono" ? ToBits(g, w, h, threshold, dither) : null; int rb = (w + 7) / 8;
                    for (int j = 0; j < g.Length; j++)
                    {
                        byte val = bits != null ? (byte)(((bits[j / w * rb + ((j % w) >> 3)] >> (7 - (j % w & 7))) & 1) * 255) : g[j];
                        rgb[j * 3] = rgb[j * 3 + 1] = rgb[j * 3 + 2] = val;
                    }
                }
                using var bmp = RgbBitmap(rgb, w, h);
                bmp.Save(Path.Combine(dir, num + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        });
        App.Toast("내보냄: " + outPath);
    }

    /// <summary>인쇄 색 미리보기: 원래 색 ↔ 인쇄했을 때 색, 인쇄로 못 내는 색 표시</summary>
    public static unsafe void ProofDialog()
    {
        var profs = ListProfiles().Where(p => { try { return IccProfile.Parse(File.ReadAllBytes(p.Path)).Space == "CMYK"; } catch { return false; } }).ToList();
        if (profs.Count == 0) { App.Toast("인쇄 프로파일이 없습니다. 파일 > 인쇄용 내보내기에서 프로파일을 추가하세요"); return; }
        string lastProf = App.Get<string>("printProfile", null);
        var v = Ui.Ask("인쇄 색 미리보기 (CMYK)", new[]
        {
            Field.Pick("p", "인쇄 프로파일", profs.Select((p, i) => (i.ToString(), p.Label)).ToArray(), Math.Max(0, profs.FindIndex(p => p.Path == lastProf)).ToString()),
            Field.Pick("intent", "변환 방식", new[] { ("0", "지각적 (자연스럽게)"), ("1", "상대 색도 (정확하게)") }, App.Get("printIntent", 0.0).ToString("0")),
        }, "보기");
        if (v == null) return;
        var sel = profs[int.Parse(v.S("p"))];
        int intent = int.Parse(v.S("intent"));
        IccCube cube;
        try { cube = Ui.Busy(() => CubeFor(sel.Path, intent)); } catch (Exception ex) { App.Toast(ex.Message); return; }
        App.Set("printProfile", sel.Path); App.Set("printIntent", intent);
        var d = D;
        double s = Math.Min(1, 1400.0 / Math.Max(d.W, d.H));
        using var src = ImageIO.FlattenBitmap(d, s, 0xFFFFFFFF);
        Bitmap Make(bool warn)
        {
            var o = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var a = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var b = o.LockBits(new Rectangle(0, 0, o.Width, o.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var tmp = new float[3]; int N = cube.N - 1;
            for (int y = 0; y < o.Height; y++)
            {
                uint* ra = (uint*)((byte*)a.Scan0 + y * a.Stride), rb = (uint*)((byte*)b.Scan0 + y * b.Stride);
                for (int x = 0; x < o.Width; x++)
                {
                    uint c = ra[x];
                    double r = ((c >> 16) & 255) / 255.0, g = ((c >> 8) & 255) / 255.0, bl = (c & 255) / 255.0;
                    cube.Lookup(cube.Proof, 3, r, g, bl, tmp);
                    if (warn)
                    {
                        int k = ((int)Math.Round(r * N) * cube.N + (int)Math.Round(g * N)) * cube.N + (int)Math.Round(bl * N);
                        if (cube.Gam[k] != 0 && (x + y) % 6 < 3) { rb[x] = 0xFF808080; continue; }
                    }
                    rb[x] = 0xFF000000u | ((uint)Math.Clamp(tmp[0] * 255 + 0.5f, 0, 255) << 16) | ((uint)Math.Clamp(tmp[1] * 255 + 0.5f, 0, 255) << 8) | (uint)Math.Clamp(tmp[2] * 255 + 0.5f, 0, 255);
                }
            }
            src.UnlockBits(a); o.UnlockBits(b);
            return o;
        }
        using var orig = new Bitmap(src);
        using var proof = Ui.Busy(() => Make(false));
        using var warnB = Ui.Busy(() => Make(true));
        using var f = Ui.Dialog("인쇄 색 미리보기: " + sel.Label);
        f.AutoSize = false; f.FormBorderStyle = FormBorderStyle.Sizable; f.MaximizeBox = true;
        var area = Ui.WorkArea();
        f.Size = new Size(Math.Min(area.Width - 80, 1100), Math.Min(area.Height - 80, 900));
        var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = proof, BackColor = Opt2.Back };
        var top = Ui.RowP(); top.Dock = DockStyle.Top;
        foreach (var (t, img) in new[] { ("원래 색 (화면)", (Image)orig), ("인쇄했을 때", proof), ("인쇄로 못 내는 색", warnB) }) { var im = img; top.Controls.Add(Ui.Btn(t, () => pb.Image = im)); }
        var help = Ui.Lbl("\"인쇄로 못 내는 색\"은 회색 줄무늬로 표시됩니다. 화면 색과 차이가 큰 곳은 인쇄하면 탁해질 수 있습니다.", true, 1000); help.Dock = DockStyle.Top;
        f.Controls.Add(pb); f.Controls.Add(help); f.Controls.Add(top);
        f.ShowDialog(App.Main);
        pb.Image = null;
    }
}

static class Opt2 { public static readonly Color Back = Color.FromArgb(30, 30, 34); }
