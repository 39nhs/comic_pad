// 전자책 내보내기 (웹 판 ui/ebook.js): EPUB 3 (고정 레이아웃, 한 쪽 = 그림 한 장, 만화는 오른쪽→왼쪽) / CBZ (그림 + ComicInfo.xml)
// ZIP 은 System.IO.Compression. EPUB 규칙대로 mimetype 을 맨 앞에 압축 없이.
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Extras;

public sealed record EbookPage(byte[] Data, int W, int H, string Type);   // Type: image/jpeg | image/png

public static class Ebook
{
    static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    static void Add(ZipArchive z, string name, byte[] data, bool store)
    {
        var e = z.CreateEntry(name, store ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(data, 0, data.Length);
    }
    static void Add(ZipArchive z, string name, string text, bool store = false) => Add(z, name, Encoding.UTF8.GetBytes(text), store);

    public static byte[] BuildEPUB(IList<EbookPage> pages, string title = "만화", string author = "", bool rtl = true, string lang = "ko", string id = null)
    {
        string uuid = id ?? "urn:uuid:" + Guid.NewGuid();
        int W = pages.Count > 0 ? pages[0].W : 1000, H = pages.Count > 0 ? pages[0].H : 1414;
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(z, "mimetype", "application/epub+zip", true);
            Add(z, "META-INF/container.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            var manifest = new StringBuilder(); var spine = new StringBuilder(); var nav = new StringBuilder();
            for (int i = 0; i < pages.Count; i++)
            {
                var p = pages[i];
                string n = (i + 1).ToString().PadLeft(3, '0'), ext = p.Type == "image/png" ? "png" : "jpg";
                Add(z, $"OEBPS/images/p{n}.{ext}", p.Data, true);
                Add(z, $"OEBPS/p{n}.xhtml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"{lang}\"><head><meta charset=\"UTF-8\"/><title>{i + 1}</title><meta name=\"viewport\" content=\"width={p.W}, height={p.H}\"/><style>html,body{{margin:0;padding:0}}img{{display:block;width:{p.W}px;height:{p.H}px}}</style></head><body><img src=\"images/p{n}.{ext}\" alt=\"{i + 1}\"/></body></html>");
                manifest.Append($"<item id=\"i{n}\" href=\"images/p{n}.{ext}\" media-type=\"{p.Type}\"{(i == 0 ? " properties=\"cover-image\"" : "")}/>").Append($"<item id=\"p{n}\" href=\"p{n}.xhtml\" media-type=\"application/xhtml+xml\"/>");
                // 펼침면: 첫 쪽은 한쪽, 그다음부터 좌우 번갈아
                string side = i == 0 ? "page-spread-center" : ((i % 2 == 1) == rtl ? "page-spread-right" : "page-spread-left");
                spine.Append($"<itemref idref=\"p{n}\" properties=\"{side}\"/>");
                nav.Append($"<li><a href=\"p{n}.xhtml\">{i + 1}</a></li>");
            }
            Add(z, "OEBPS/nav.xhtml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"{lang}\"><head><meta charset=\"UTF-8\"/><title>{Esc(title)}</title></head><body><nav epub:type=\"toc\" id=\"toc\"><ol>{nav}</ol></nav></body></html>");
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            Add(z, "OEBPS/content.opf", $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<package xmlns=""http://www.idpf.org/2007/opf"" version=""3.0"" unique-identifier=""bookid"" xml:lang=""{lang}"" prefix=""rendition: http://www.idpf.org/vocab/rendition/#"">
<metadata xmlns:dc=""http://purl.org/dc/elements/1.1/"">
<dc:identifier id=""bookid"">{Esc(uuid)}</dc:identifier><dc:title>{Esc(title)}</dc:title><dc:language>{lang}</dc:language>{(string.IsNullOrEmpty(author) ? "" : $"<dc:creator>{Esc(author)}</dc:creator>")}
<meta property=""dcterms:modified"">{now}</meta>
<meta property=""rendition:layout"">pre-paginated</meta><meta property=""rendition:spread"">landscape</meta><meta property=""rendition:orientation"">auto</meta>
<meta name=""original-resolution"" content=""{W}x{H}""/><meta name=""fixed-layout"" content=""true""/><meta name=""book-type"" content=""comic""/>
</metadata>
<manifest><item id=""nav"" href=""nav.xhtml"" media-type=""application/xhtml+xml"" properties=""nav""/>{manifest}</manifest>
<spine page-progression-direction=""{(rtl ? "rtl" : "ltr")}"">{spine}</spine>
</package>".Replace("\r\n", "\n"));
        }
        return ms.ToArray();
    }

    public static byte[] BuildCBZ(IList<EbookPage> pages, string title = "만화", string author = "", bool rtl = true)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            for (int i = 0; i < pages.Count; i++) Add(z, $"{(i + 1).ToString().PadLeft(3, '0')}.{(pages[i].Type == "image/png" ? "png" : "jpg")}", pages[i].Data, true);
            Add(z, "ComicInfo.xml", $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ComicInfo><Title>{Esc(title)}</Title>{(string.IsNullOrEmpty(author) ? "" : $"<Writer>{Esc(author)}</Writer>")}<PageCount>{pages.Count}</PageCount><Manga>{(rtl ? "YesAndRightToLeft" : "No")}</Manga><LanguageISO>ko</LanguageISO></ComicInfo>");
        }
        return ms.ToArray();
    }

    /// <summary>원고 설정(재단선)이 있으면 그 영역 (웹 판 printRects)</summary>
    public static (Rectangle trim, Rectangle bleed, Rectangle safe)? PrintRects(Document d)
    {
        if (d.Extra["print"] is not JsonObject p || p["trimW"] == null) return null;
        int tw = Math.Min(U.Int(p["trimW"]), d.W), th = Math.Min(U.Int(p["trimH"]), d.H);
        int bleed = U.Int(p["bleed"]), safe = U.Int(p["safe"]);
        var trim = new Rectangle((int)Math.Round((d.W - tw) / 2.0, MidpointRounding.AwayFromZero), (int)Math.Round((d.H - th) / 2.0, MidpointRounding.AwayFromZero), tw, th);
        return (trim, Rectangle.Inflate(trim, bleed, bleed), new Rectangle(trim.X + safe, trim.Y + safe, Math.Max(0, tw - safe * 2), Math.Max(0, th - safe * 2)));
    }

    /// <summary>문서 한 장 → 쪽 그림 (긴 쪽 maxSide 이하)</summary>
    public static EbookPage PageImage(Document d, int maxSide = 2400, string fmt = "jpg", bool trimArea = true)
    {
        Bitmap c = Img.Flatten(d, 1, true);
        var pr = trimArea ? PrintRects(d) : null;
        if (pr != null) { var k = Img.Crop(c, pr.Value.trim); c.Dispose(); c = k; }
        double s = Math.Min(1, maxSide / (double)Math.Max(c.Width, c.Height));
        if (s < 1) { var k = Img.Scale(c, s); c.Dispose(); c = k; }
        using (c) return new EbookPage(Img.Encode(c, fmt, 0.9), c.Width, c.Height, fmt == "png" ? "image/png" : "image/jpeg");
    }

    public static void Dialog(string kind = "epub")
    {
        var doc = App.Doc; if (doc == null) return;
        string story = Story.StoryOf(doc);
        var v = Dlg.Form(kind == "cbz" ? "CBZ (만화책 압축 파일) 내보내기" : "전자책 (EPUB) 내보내기", new[]
        {
            Field.Segment("kind", "형식", kind, ("epub", "EPUB 3 (전자책)"), ("cbz", "CBZ (만화 뷰어)")),
            story != null ? Field.Segment("range", "범위", "story", ("story", $"작품 전체 ({story})"), ("doc", "지금 쪽만")) : Field.Segment("range", "범위", "doc", ("doc", "지금 그림")),
            Field.Text("title", "제목", story ?? doc.Name ?? "만화"),
            Field.Text("author", "지은이", App.Get("ebookAuthor", "")),
            Field.Check("rtl", "오른쪽에서 왼쪽으로 넘기기 (일본식 만화)", App.Get("ebookRtl", false)),
            Field.Num("max", "그림 긴 쪽 최대 (px)", 2400, 600, 8000),
            Field.Segment("fmt", "그림 형식", "jpg", ("jpg", "JPG (작음)"), ("png", "PNG (선명)")),
            Field.Info("고정 레이아웃 EPUB 3: 한 쪽 = 그림 한 장. 원고 설정(재단선)이 있으면 재단선 안쪽만 넣습니다. 결과는 data/exports 에 저장합니다."),
        }, "만들기");
        if (v == null) return;
        App.Settings["ebookAuthor"] = (string)v["author"]; App.Settings["ebookRtl"] = (bool)v["rtl"];
        App.SaveSettingsSoon();
        byte[] outData = null;
        int max = U.Int(v["max"], 2400); string fmt = (string)v["fmt"];
        U.Task("전자책 만드는 중", () =>
        {
            var pages = new List<EbookPage>();
            if ((string)v["range"] == "story")
            {
                if (doc.Modified && !Story.SaveCurrent()) return;
                foreach (var p in Story.ListPages(story)) pages.Add(PageImage(CpadIO.Load(Story.Full(p)), max, fmt));
            }
            else pages.Add(PageImage(doc, max, fmt));
            outData = (string)v["kind"] == "cbz" ? BuildCBZ(pages, (string)v["title"], (string)v["author"], (bool)v["rtl"]) : BuildEPUB(pages, (string)v["title"], (string)v["author"], (bool)v["rtl"]);
        });
        if (outData == null) return;
        string name = $"{U.SafeName(string.IsNullOrEmpty((string)v["title"]) ? "comic" : (string)v["title"])}.{(string)v["kind"]}";
        File.WriteAllBytes(Path.Combine(U.ExportsDir, name), outData);
        App.Toast($"{name} 을(를) 만들었습니다 (data/exports, {outData.Length / 1e6:0.0}MB)");
    }
}
