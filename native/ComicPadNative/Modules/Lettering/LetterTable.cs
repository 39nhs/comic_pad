// 번역(식자) 표: 작품의 모든 대사를 읽는 순서로 CSV / 엑셀용 CSV(UTF-8 BOM, CRLF) / XLSX 로 내보내고,
// 번역가가 "번역" 칸을 채운 표를 다시 읽어 레이어 id 로 맞춰 새 언어 번역본(meta.i18n[lang])에 넣는다 (원문은 그대로).
// 칸: 페이지, 순서, 레이어ID, 종류(대사/나레이션/효과음/메모), 말풍선, 원문, 번역(언어), 글꼴, 크기, 메모
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;

namespace ComicPadNative.Modules.Lettering;

public static class LetterTable
{
    public static readonly string[] Keys = { "page", "order", "id", "kind", "bubble", "original", "translation", "font", "size", "notes" };
    public static string[] Header(string lang) => new[] { "페이지", "순서", "레이어ID", "종류", "말풍선", "원문", string.IsNullOrEmpty(lang) ? "번역" : $"번역({lang})", "글꼴", "크기", "메모" };

    /// <summary>머리 칸 이름 → 키 (한국어·영어 둘 다). 번역 칸의 (언어) 도 돌려줌</summary>
    public static string KeyOf(string head, out string lang)
    {
        lang = null;
        string h = (head ?? "").Trim().Trim('﻿');
        var m = Regex.Match(h, @"[\(\[]\s*([A-Za-z]{2,3}(?:-[A-Za-z]{2,4})?)\s*[\)\]]");
        if (m.Success) { lang = m.Groups[1].Value; h = h.Remove(m.Index, m.Length); }
        h = Regex.Replace(h.ToLowerInvariant(), @"[\s_\-]", "");
        return h switch
        {
            "페이지" or "page" or "쪽" or "p" => "page",
            "순서" or "order" or "no" or "번호" => "order",
            "레이어id" or "layerid" or "id" or "레이어" => "id",
            "종류" or "kind" or "type" => "kind",
            "말풍선" or "bubble" or "shape" => "bubble",
            "원문" or "original" or "source" or "text" => "original",
            "번역" or "translation" or "target" or "번역문" => "translation",
            "글꼴" or "font" => "font",
            "크기" or "size" => "size",
            "메모" or "notes" or "note" or "comment" or "비고" => "notes",
            _ => null,
        };
    }

    // ---------------------------------------------------------------- 표 만들기
    public static List<string[]> Table(List<LetterRow> rows, string lang)
    {
        var t = new List<string[]> { Header(lang) };
        foreach (var r in rows)
            t.Add(new[]
            {
                r.Page.No.ToString(CultureInfo.InvariantCulture), r.Order.ToString(CultureInfo.InvariantCulture), r.L.Id, r.Kind, r.Shape,
                r.Original, string.IsNullOrEmpty(lang) ? "" : r.Tr(lang), r.Font, r.Size.ToString("0.##", CultureInfo.InvariantCulture), r.Note,
            });
        return t;
    }

    // ---------------------------------------------------------------- CSV
    static string Cell(string s)
    {
        s ??= "";
        bool q = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || s.Length > 0 && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]));
        return q ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
    /// <summary>excel = UTF-8 BOM + CRLF (엑셀에서 한글이 깨지지 않게). 칸 안 줄바꿈은 LF 로</summary>
    public static byte[] Csv(List<string[]> table, bool excel)
    {
        string nl = excel ? "\r\n" : "\n";
        var sb = new StringBuilder();
        foreach (var row in table) sb.Append(string.Join(",", row.Select(c => Cell((c ?? "").Replace("\r\n", "\n"))))).Append(nl);
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        return excel ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }

    /// <summary>글자 인코딩 알아내기: BOM → UTF-8 (틀리면 CP949, 엑셀 "CSV" 저장)</summary>
    public static string Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(949).GetString(b); }
            catch { return Encoding.UTF8.GetString(b); }
        }
    }

    /// <summary>CSV 읽기 (따옴표·칸 안 줄바꿈, 구분자 , ; 탭 자동)</summary>
    public static List<string[]> ParseCsv(string text)
    {
        text ??= "";
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        string first = text.Split('\n')[0];
        char sep = new[] { ',', ';', '\t' }.OrderByDescending(c => first.Count(x => x == c)).First();
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder();
        bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; }
                else cell.Append(c);
                continue;
            }
            if (c == '"' && cell.Length == 0) q = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row = new(); }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows.Where(r => r.Any(c => c.Length > 0)).Select(r => r.Select(c => c.Replace("\r\n", "\n")).ToArray()).ToList();
    }

    // ---------------------------------------------------------------- XLSX (최소 구현: 시트 하나, 글자 칸)
    static string Col(int i) { var s = ""; i++; while (i > 0) { int m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; } return s; }
    static int ColIndex(string r) { int n = 0; foreach (char c in r) { if (!char.IsLetter(c)) break; n = n * 26 + (char.ToUpperInvariant(c) - 'A' + 1); } return n - 1; }

    public static byte[] Xlsx(List<string[]> table)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string name, string xml) { using var w = new StreamWriter(z.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)); w.Write(xml); }
            Put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
            Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Put("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"대사\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            // 스타일 0 = 기본, 1 = 줄바꿈 + 위 정렬, 2 = 굵게(머리)
            Put("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Malgun Gothic\"/></font><font><b/><sz val=\"11\"/><name val=\"Malgun Gothic\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs></styleSheet>");
            var sheet = new XElement(ns + "worksheet",
                new XElement(ns + "sheetViews", new XElement(ns + "sheetView", new XAttribute("workbookViewId", 0), new XElement(ns + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
                new XElement(ns + "cols", new[] { 6, 6, 18, 9, 7, 40, 40, 16, 6, 24 }.Select((w, i) => new XElement(ns + "col", new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", w), new XAttribute("customWidth", 1)))),
                new XElement(ns + "sheetData", table.Select((row, ri) => new XElement(ns + "row", new XAttribute("r", ri + 1),
                    row.Select((c, ci) => new XElement(ns + "c", new XAttribute("r", Col(ci) + (ri + 1)), new XAttribute("t", "inlineStr"), new XAttribute("s", ri == 0 ? 2 : 1),
                        new XElement(ns + "is", new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Clean(c)))))))));
            Put("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + sheet.ToString(SaveOptions.DisableFormatting));
        }
        return ms.ToArray();
    }
    static string Clean(string s) => new string((s ?? "").Where(c => c == '\n' || c == '\t' || c >= 0x20).ToArray());

    /// <summary>XLSX 첫 시트 읽기 (공유 문자열·인라인 문자열·숫자)</summary>
    public static List<string[]> ParseXlsx(byte[] bytes)
    {
        using var z = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        string Txt(XElement e) => string.Concat(e.Descendants(ns + "t").Where(t => t.Parent?.Name.LocalName != "rPh").Select(t => t.Value));
        var shared = new List<string>();
        var se = z.GetEntry("xl/sharedStrings.xml");
        if (se != null) using (var s = se.Open()) shared = XDocument.Load(s).Root.Elements(ns + "si").Select(Txt).ToList();
        var sheetEntry = z.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml")).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.Ordinal).FirstOrDefault();
        if (sheetEntry == null) return new();
        XDocument doc; using (var s = sheetEntry.Open()) doc = XDocument.Load(s);
        var rows = new List<string[]>();
        foreach (var r in doc.Descendants(ns + "row"))
        {
            var cells = new SortedDictionary<int, string>();
            int auto = 0;
            foreach (var c in r.Elements(ns + "c"))
            {
                string rf = (string)c.Attribute("r"); int ci = rf != null ? ColIndex(rf) : auto;
                auto = ci + 1;
                string t = (string)c.Attribute("t"), v = c.Element(ns + "v")?.Value;
                string val = t switch
                {
                    "s" => int.TryParse(v, out int si) && si >= 0 && si < shared.Count ? shared[si] : "",
                    "inlineStr" => c.Element(ns + "is") is { } isE ? Txt(isE) : "",
                    _ => v ?? "",
                };
                cells[ci] = val.Replace("\r\n", "\n");
            }
            if (cells.Count == 0) continue;
            var arr = new string[cells.Keys.Max() + 1];
            for (int i = 0; i < arr.Length; i++) arr[i] = cells.TryGetValue(i, out var x) ? x : "";
            if (arr.Any(x => x.Length > 0)) rows.Add(arr);
        }
        return rows;
    }

    public static List<string[]> ReadFile(string path)
    {
        var b = File.ReadAllBytes(path);
        bool zip = b.Length > 4 && b[0] == 'P' && b[1] == 'K';
        return zip ? ParseXlsx(b) : ParseCsv(Decode(b));
    }

    /// <summary>표 → 키 사전 목록 (첫 줄 = 머리). 머리의 번역(언어)</summary>
    public static (List<Dictionary<string, string>> rows, string lang) Records(List<string[]> table)
    {
        var res = new List<Dictionary<string, string>>();
        if (table.Count == 0) return (res, null);
        string lang = null;
        var keys = table[0].Select(h => { var k = KeyOf(h, out var lg); if (k == "translation" && lg != null) lang = lg; return k; }).ToArray();
        if (!keys.Contains("id")) throw new InvalidDataException("표에 \"레이어ID\" 칸이 없습니다 (내보낸 표의 첫 줄을 지우지 마세요)");
        foreach (var row in table.Skip(1))
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < keys.Length && i < row.Length; i++) if (keys[i] != null) d[keys[i]] = row[i];
            if (d.TryGetValue("id", out var id) && !string.IsNullOrWhiteSpace(id)) res.Add(d);
        }
        return (res, lang);
    }

    // ---------------------------------------------------------------- 가져오기
    public sealed class ImportOpts { public bool Fit = true; public bool Switch = true; public AutoFit.Opts FitOpts = new(); }
    public sealed record ImportResult(int Rows, int Updated, int Unchanged, int Empty, int NotFound, int Fitted, int Overflow, int Pages, List<string> Missing);

    /// <summary>표의 번역을 lang 번역본으로 넣기. 레이어 id 로 맞추고, 같은 id 가 여러 페이지에 있으면 페이지 번호로 고름</summary>
    public static ImportResult Import(List<WorkPage> pages, List<Dictionary<string, string>> recs, string lang, ImportOpts o)
    {
        if (string.IsNullOrWhiteSpace(lang)) throw new ArgumentException("번역 언어 코드가 필요합니다 (예: en, ja)");
        var byId = new Dictionary<string, List<(WorkPage p, Layer l)>>();
        foreach (var p in pages)
            foreach (var l in p.Doc.Layers.Where(I18n.IsText))
            {
                if (!byId.TryGetValue(l.Id, out var lst)) byId[l.Id] = lst = new();
                lst.Add((p, l));
            }
        var plan = new Dictionary<WorkPage, Dictionary<Layer, JsonObject>>();
        foreach (var p in pages) Work.PlanOf(plan, p);
        int upd = 0, same = 0, empty = 0, nf = 0, fitted = 0, over = 0;
        var missing = new List<string>();
        var touchedText = new HashSet<Layer>();
        string V(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? (v ?? "").Replace("\r\n", "\n") : null;
        foreach (var r in recs)
        {
            string id = V(r, "id").Trim();
            if (!byId.TryGetValue(id, out var cands)) { nf++; missing.Add(id); continue; }
            var hit = cands[0];
            if (cands.Count > 1 && int.TryParse(V(r, "page"), out int pn)) hit = cands.FirstOrDefault(c => c.p.No == pn) is { l: not null } h ? h : cands[0];
            var (page, l) = hit;
            var changes = plan[page];
            var nm = changes.TryGetValue(l, out var pm) ? pm : J.Clone(l.Meta);
            bool changed = false;
            string tr = V(r, "translation") ?? "";
            if (tr.Trim().Length == 0) empty++;
            else
            {
                var t = I18n.Tr(nm, lang, true);
                if (J.Str(t, "text") != tr)
                {
                    t["text"] = tr;
                    t.Remove("x"); t.Remove("y"); t.Remove("size");   // 예전 배치는 새 글에 맞지 않음
                    changed = true; touchedText.Add(l);
                }
                // 글꼴·크기 칸을 원문과 다르게 고쳤으면 이 언어에만
                string f = V(r, "font");
                if (!string.IsNullOrWhiteSpace(f) && f != J.Str(nm, "font") && J.Str(t, "font") != f) { t["font"] = f; changed = true; }
                if (double.TryParse(V(r, "size"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sz) && sz > 0 && Math.Abs(sz - J.Num(nm, "size", 42)) > 0.01 && Math.Abs(sz - J.Num(t, "size", -1)) > 0.01)
                { t["size"] = sz; changed = true; }
            }
            string kind = V(r, "kind");
            if (!string.IsNullOrWhiteSpace(kind) && Work.Kinds.Contains(kind.Trim()))
            {
                var links = Work.BubbleLinks(page.Doc);
                links.TryGetValue(l.Id, out var bub);
                var tmp = new Layer { Kind = l.Kind, Meta = J.Clone(nm), Visible = l.Visible, Draft = l.Draft }; tmp.Meta.Remove("letterKind");
                string auto = Work.KindOf(tmp, bub);
                if (kind.Trim() != auto && J.Str(nm, "letterKind") != kind.Trim()) { nm["letterKind"] = kind.Trim(); changed = true; }
                else if (kind.Trim() == auto && nm["letterKind"] != null) { nm.Remove("letterKind"); changed = true; }
            }
            string note = V(r, "notes");
            if (note != null && note != (J.Str(nm, "letterNote") ?? ""))
            {
                if (note.Length == 0) nm.Remove("letterNote"); else nm["letterNote"] = note;
                changed = true;
            }
            if (changed) { changes[l] = nm; upd++; } else same++;
        }
        // 맞추기·가운데 다시 놓기 (번역이 바뀐 것만)
        foreach (var p in pages)
        {
            var links = Work.BubbleLinks(p.Doc);
            foreach (var (l, nm) in plan[p].ToList())
            {
                if (!touchedText.Contains(l) || !I18n.HasTr(nm, lang)) continue;
                if (o.Fit && !J.Bool(nm, "sfx") && links.TryGetValue(l.Id, out var bub))
                {
                    var tmp = new Layer { Id = l.Id, Kind = "text", Meta = nm };
                    var fm = AutoFit.Plan(p.Doc, tmp, bub, lang, o.FitOpts, out var res);
                    if (fm != null) { plan[p][l] = fm; fitted++; if (!res.Fits) over++; }
                }
                else
                {
                    var eff = I18n.Effective(p.Doc, nm, lang);
                    if (eff != null && J.Bool(eff, "vertical") != J.Bool(nm, "vertical"))
                    {
                        I18n.Tr(nm, lang)["vertical"] = J.Bool(eff, "vertical");
                        I18n.Recenter(p.Doc, nm, lang);
                    }
                }
            }
        }
        int pagesChanged = Work.Apply(plan, $"번역 가져오기 ({lang})", o.Switch ? lang : Work.Keep);
        if (o.Switch) LetteringModule.SetStoryLang(pages, lang);
        return new ImportResult(recs.Count, upd, same, empty, nf, fitted, over, pagesChanged, missing);
    }
}
