// 번역·식자 화면: 대사 표 내보내기/가져오기, 번역 표(바로 고치기·빠짐 검사), 찾아 바꾸기, 말풍선 맞추기, 보기 언어·언어별 글꼴, 언어별 내보내기,
// 번역 보기 중 글자를 누르면 번역 편집 창.
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.Filters;
using ComicPadNative.UI;
using Field = ComicPadNative.Modules.Extras.Field;

namespace ComicPadNative.Modules.Lettering;

public static class LetteringUi
{
    static bool IsStory => Work.StoryOf(App.Doc) != null;
    static (string, string)[] Scope => new[] { ("page", "지금 페이지"), ("work", "작품 전체") };
    static string DefaultLang => I18n.LangOf(App.Doc) ?? App.Get("letteringLang", "en");
    static (string, string)[] LangOptions(bool withOrig)
    {
        var langs = I18n.LangsIn(new[] { App.Doc }).Concat(I18n.Common.Select(c => c.code)).Distinct();
        var o = langs.Select(c => (c, I18n.Label(c))).ToList();
        if (withOrig) o.Insert(0, ("", "원문"));
        return o.ToArray();
    }
    static string Code(string s) => (s ?? "").Trim().Replace('_', '-');

    // ---------------------------------------------------------------- 표 내보내기·가져오기
    public static void ExportTable()
    {
        var d = App.Doc; if (d == null) return;
        var v = Dlg.Form("대사 표 내보내기 (번역·식자용)", new[]
        {
            IsStory ? Field.Segment("scope", "범위", "work", Scope) : null,
            Field.Text("lang", "번역할 언어 코드 (en, ja, zh, zh-TW, th …)", DefaultLang),
            Field.Select("fmt", "형식", App.Get("letteringFmt", "excel"), ("excel", "엑셀용 CSV (UTF-8 BOM)"), ("xlsx", "엑셀 파일 (.xlsx)"), ("csv", "CSV (UTF-8)")),
            Field.Select("dir", "읽는 방향", App.Get("letteringRtl", false) ? "rtl" : "ltr", ("ltr", "왼→오 (웹툰·한국식)"), ("rtl", "오→왼 (일본식)")),
            Field.Info("칸: 페이지, 순서, 레이어ID, 종류, 말풍선, 원문, 번역, 글꼴, 크기, 메모. 번역가는 \"번역\" 칸만 채우면 됩니다 (레이어ID 는 고치지 마세요). 이미 넣은 번역이 있으면 함께 적힙니다."),
        }, "내보내기");
        if (v == null) return;
        string lang = Code((string)v["lang"]), fmt = (string)v["fmt"]; bool rtl = (string)v["dir"] == "rtl";
        App.Set("letteringFmt", fmt); App.Set("letteringRtl", rtl); if (lang.Length > 0) App.Set("letteringLang", lang);
        var pages = Work.Open(d, (string)v["scope"] != "page");
        var table = LetterTable.Table(Work.Rows(pages, rtl), lang);
        string ext = fmt == "xlsx" ? "xlsx" : "csv";
        string name = $"{ImageIO.SafeName(Work.StoryOf(d) ?? d.Name)}_대사표{(lang.Length > 0 ? "_" + lang : "")}.{ext}";
        using var sd = new SaveFileDialog { Filter = ext == "xlsx" ? "엑셀 (*.xlsx)|*.xlsx" : "CSV (*.csv)|*.csv", FileName = name, InitialDirectory = ImageIO.ExportsDir };
        if (sd.ShowDialog(App.Main) != DialogResult.OK) return;
        File.WriteAllBytes(sd.FileName, fmt == "xlsx" ? LetterTable.Xlsx(table) : LetterTable.Csv(table, fmt == "excel"));
        App.Toast($"대사 {table.Count - 1}줄 ({pages.Count}쪽) → {Path.GetFileName(sd.FileName)}");
    }

    public static void ImportTable()
    {
        var d = App.Doc; if (d == null) return;
        using var od = new OpenFileDialog { Filter = "대사 표 (*.csv;*.xlsx;*.tsv;*.txt)|*.csv;*.xlsx;*.tsv;*.txt|모든 파일|*.*", InitialDirectory = ImageIO.ExportsDir };
        if (od.ShowDialog(App.Main) != DialogResult.OK) return;
        List<Dictionary<string, string>> recs; string headLang;
        try { (recs, headLang) = LetterTable.Records(LetterTable.ReadFile(od.FileName)); }
        catch (Exception ex) { UI.Dialogs.Modals.Info("표를 읽지 못했습니다: " + ex.Message); return; }
        int filled = recs.Count(r => r.TryGetValue("translation", out var t) && !string.IsNullOrWhiteSpace(t));
        var v = Dlg.Form("번역 가져오기", new[]
        {
            Field.Info($"{Path.GetFileName(od.FileName)}: {recs.Count}줄 (번역 있음 {filled}줄)"),
            Field.Text("lang", "번역 언어 코드 (새 언어로 넣고 원문은 그대로 둡니다)", headLang ?? DefaultLang),
            IsStory ? Field.Segment("scope", "범위", "work", Scope) : null,
            Field.Check("fit", "말풍선 대사는 말풍선에 맞추기 (글자 크기·줄바꿈)", true),
            Field.Check("switch", "가져온 언어로 보기", true),
            Field.Num("min", "맞출 때 최소 글자 크기 (폰 화면 px, 390 폭 기준)", App.Get("fitMinCss", 11.0), 4, 40),
        }, "가져오기");
        if (v == null) return;
        string lang = Code((string)v["lang"]);
        if (lang.Length == 0) { App.Toast("언어 코드를 넣으세요"); return; }
        App.Set("fitMinCss", (double)v["min"]); App.Set("letteringLang", lang);
        var pages = Work.Open(d, (string)v["scope"] != "page");
        var o = new LetterTable.ImportOpts { Fit = (bool)v["fit"], Switch = (bool)v["switch"], FitOpts = new AutoFit.Opts { MinCss = (double)v["min"] } };
        LetterTable.ImportResult r = null;
        Cursor.Current = Cursors.WaitCursor;
        try { r = LetterTable.Import(pages, recs, lang, o); }
        catch (Exception ex) { UI.Dialogs.Modals.Info("가져오지 못했습니다: " + ex.Message); return; }
        finally { Cursor.Current = Cursors.Default; }
        App.InvalidateAll(); App.NotifyLayers();
        UI.Dialogs.Modals.Info($"{I18n.Label(lang)} 번역 가져오기\n\n바뀐 대사 {r.Updated}줄 · 그대로 {r.Unchanged}줄 · 번역 빈 칸 {r.Empty}줄\n말풍선에 맞춤 {r.Fitted}개{(r.Overflow > 0 ? $" (최소 크기로도 넘침 {r.Overflow}개 — 번역 표에서 확인)" : "")}\n" +
            (r.NotFound > 0 ? $"작품에서 못 찾은 레이어ID {r.NotFound}줄: {string.Join(", ", r.Missing.Take(5))}{(r.Missing.Count > 5 ? " …" : "")}\n" : "") + $"바뀐 페이지 {r.Pages}쪽 (지금 페이지는 실행취소 한 번으로 되돌릴 수 있음, 다른 페이지는 저장됨)");
    }

    // ---------------------------------------------------------------- 보기 언어
    public static void SwitchLang(string lang)
    {
        var d = App.Doc; if (d == null) return;
        lang = string.IsNullOrWhiteSpace(lang) ? null : Code(lang);
        int n = I18n.SetLangRaw(d, lang);
        d.Modified = true;
        LetteringModule.SetStoryLang(Work.Open(d, false), lang);
        if (lang != null) App.Set("letteringLang", lang);
        App.InvalidateAll(); App.NotifyLayers(); MetaLayers.Redraw();
        int have = d.Layers.Count(l => I18n.IsText(l) && I18n.HasTr(l.Meta, lang));
        int all = d.Layers.Count(I18n.IsText);
        App.Toast(lang == null ? "보기 언어: 원문" : $"보기 언어: {I18n.Label(lang)} — 이 페이지 번역 {have}/{all}개" + (have < all ? " (번역이 없는 글자는 원문으로 보임)" : ""));
    }

    public static void OtherLang()
    {
        var v = Dlg.Form("보기 언어", new[] { Field.Text("lang", "언어 코드 (비우면 원문)", I18n.LangOf(App.Doc) ?? "") }, "바꾸기");
        if (v != null) SwitchLang((string)v["lang"]);
    }

    public static void FontsDialog()
    {
        var d = App.Doc; if (d == null) return;
        var langs = I18n.LangsIn(new[] { d });
        if (I18n.LangOf(d) is { } cur && !langs.Contains(cur)) langs.Insert(0, cur);
        if (langs.Count == 0) langs.Add(DefaultLang);
        var fonts = new[] { ("", "자동 (언어에 맞는 설치된 글꼴)") }.Concat(Fonts.List()).ToArray();
        var v = Dlg.Form("언어별 글꼴", langs.Select(l => Field.Select("f_" + l, $"{I18n.Label(l)} — 지금: {I18n.FontFor(d, l) ?? "원문 글꼴"}", I18n.FontSetting(d, l) ?? "", fonts))
            .Append(Field.Info("자동: 영어 등 라틴 글자는 만화 글꼴(CC Wild Words, Anime Ace, Komika, Comic Neue, Comic Sans …) 중 설치된 것, 일본어는 Yu Gothic … 을 씁니다. 번역 편집 창에서 대사마다 따로 정할 수도 있습니다.")), "적용");
        if (v == null) return;
        var map = new JsonObject();
        foreach (var l in langs) if (((string)v["f_" + l]) is { Length: > 0 } f) map[l] = f;
        var pages = Work.Open(d, true);
        foreach (var p in pages) { var st = I18n.Settings(p.Doc, true); if (map.Count == 0) st.Remove("fonts"); else st["fonts"] = map.DeepClone(); }
        var plan = new Dictionary<WorkPage, Dictionary<Layer, JsonObject>>();
        foreach (var p in pages) Work.PlanOf(plan, p);
        Work.Apply(plan.Where(kv => true).ToDictionary(kv => kv.Key, kv => kv.Value), "언어별 글꼴", I18n.LangOf(d));
        App.InvalidateAll();
        App.Toast("언어별 글꼴을 바꿨습니다");
    }

    // ---------------------------------------------------------------- 맞추기
    static AutoFit.Opts FitOpts => new() { MinCss = App.Get("fitMinCss", 11.0) };

    /// <summary>고른 대사(글자 레이어) 또는 말풍선 도구에서 고른 풍선의 대사</summary>
    public static void FitSelected()
    {
        var d = App.Doc; if (d == null) return;
        string id = null;
        var bt = Registry.Tools.OfType<BubbleTool>().FirstOrDefault();
        if (App.Tool == bt && bt?.SelectedBubble is { } b) id = J.Str(b, "textId");
        if (id == null && I18n.IsText(d.Active)) id = d.Active.Id;
        if (id == null || !Work.BubbleLinks(d).ContainsKey(id)) { App.Toast("말풍선에 연결된 대사(글자 레이어)를 고르세요"); return; }
        FitBubbleText(id);
    }
    public static void FitBubbleText(string textId)
    {
        var (n, over, _) = AutoFit.FitAll(Work.Open(App.Doc, false), FitOpts, textId);
        App.Toast(n == 0 ? "맞출 대사가 없습니다" : over > 0 ? "최소 크기로도 다 들어가지 않습니다 — 말풍선을 키우거나 대사를 줄이세요" : "말풍선에 맞췄습니다" + (I18n.LangOf(App.Doc) is { } l ? $" ({I18n.Label(l)})" : ""));
    }
    public static void FitAll(bool whole)
    {
        var d = App.Doc; if (d == null) return;
        if (whole && IsStory)
        {
            if (!Dlg.Confirm("작품의 모든 페이지에서 말풍선 대사를 맞춥니다 (보기 언어 기준). 다른 페이지는 바로 저장됩니다. 계속할까요?")) return;
        }
        var (n, over, pages) = AutoFit.FitAll(Work.Open(d, whole), FitOpts);
        App.Toast($"말풍선 대사 {n}개를 맞췄습니다 ({pages}쪽){(over > 0 ? $" · 다 안 들어간 것 {over}개" : "")}");
    }

    // ---------------------------------------------------------------- 찾아 바꾸기
    public static void FindReplaceDialog()
    {
        var d = App.Doc; if (d == null) return;
        var q = new FindReplace.Query { Find = App.Get("frFind", ""), Replace = App.Get("frRepl", ""), Regex = App.Get("frRegex", false), MatchCase = App.Get("frCase", false), WholeWork = IsStory && App.Get("frWhole", false), Lang = I18n.LangOf(d) };
        List<WorkPage> pagesPage = null, pagesWork = null;
        List<WorkPage> Pages(bool whole) => whole ? pagesWork ??= Work.Open(d, true) : pagesPage ??= Work.Open(d, false);
        void Read(Dictionary<string, object> v)
        {
            q.Find = v["find"] as string ?? ""; q.Replace = v["repl"] as string ?? ""; q.Regex = v["regex"] is true; q.MatchCase = v["case"] is true;
            q.WholeWork = IsStory && v.TryGetValue("scope", out var sc) && sc as string == "work";
            q.Lang = v["target"] as string is { Length: > 0 } t ? t : null;
        }
        string Count()
        {
            if (q.Find.Length == 0) return "찾을 말을 넣으면 찾은 곳 수가 여기에 나옵니다";
            try
            {
                var hits = FindReplace.Run(Pages(q.WholeWork), q, false);
                int total = hits.Sum(h => h.Count);
                return total == 0 ? "찾은 곳이 없습니다" : $"{hits.Count}개 레이어에서 {total}곳 — " + string.Join(" · ", hits.Take(4).Select(h => $"{h.Page.No}쪽 \"{Short(h.Before)}\" → \"{Short(h.After)}\"")) + (hits.Count > 4 ? " …" : "");
            }
            catch (ArgumentException ex) { return "정규식 오류: " + ex.Message; }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return "정규식이 너무 오래 걸립니다"; }
        }
        var fields = new List<UI.Dialogs.FormField>
        {
            new() { Key = "find", Label = "찾을 말", Type = "text", Value = q.Find },
            new() { Key = "repl", Label = "바꿀 말 (정규식이면 $1 처럼 묶음을 쓸 수 있음)", Type = "text", Value = q.Replace },
            new() { Key = "regex", Label = "정규식", Type = "checkbox", Value = q.Regex },
            new() { Key = "case", Label = "대소문자 구분", Type = "checkbox", Value = q.MatchCase },
            IsStory ? new() { Key = "scope", Label = "범위", Type = "segment", Value = q.WholeWork ? "work" : "page", Options = Scope } : null,
            new() { Key = "target", Label = "대상", Type = "select", Value = q.Lang ?? "", Options = LangOptions(true) },
        };
        var res = UI.Dialogs.WebForm.Show("찾아 바꾸기 (모든 글자 레이어)", fields, "모두 바꾸기", "닫기", null, 480, (v, h) => { Read(v); h.SetStatus(Count()); });
        if (res == null) return;
        Read(res);
        App.Set("frFind", q.Find); App.Set("frRepl", q.Replace); App.Set("frRegex", q.Regex); App.Set("frCase", q.MatchCase); App.Set("frWhole", q.WholeWork);
        if (q.Find.Length == 0) return;
        try
        {
            var hits = FindReplace.Run(Pages(q.WholeWork), q, true);
            App.InvalidateAll();
            App.Toast($"{hits.Select(h => h.L).Distinct().Count()}개 레이어에서 {hits.Sum(h => h.Count)}곳을 바꿨습니다");
        }
        catch (Exception ex) { UI.Dialogs.Modals.Info("바꾸지 못했습니다: " + ex.Message); }
    }
    static string Short(string s) { s = (s ?? "").Replace("\n", " "); return s.Length > 18 ? s[..18] + "…" : s; }

    // ---------------------------------------------------------------- 번역 편집 (번역 보기 중 글자 누르기)
    /// <summary>TextTool.Edit 갈래: 보기 언어가 있으면 그 언어 번역을 편집 (true = 처리함)</summary>
    public static bool EditHook(Layer l)
    {
        var d = App.Doc;
        string lang = I18n.LangOf(d);
        if (lang == null || !I18n.IsText(l)) return false;
        var links = Work.BubbleLinks(d);
        links.TryGetValue(l.Id, out var bub);
        var tr = I18n.Tr(l.Meta, lang);
        var fonts = new[] { ("", $"언어 기본 ({I18n.FontFor(d, lang) ?? "원문 글꼴"})") }.Concat(Fonts.List()).ToArray();
        var v = Dlg.Form($"번역 편집 — {I18n.Label(lang)}", new[]
        {
            Field.Info("원문: " + (J.Str(l.Meta, "text", "") ?? "").Replace("\n", " / ")),
            Field.Area("text", "번역", J.Str(tr, "text", "")),
            Field.Num("size", "글자 크기 (0 = 원문 크기)", J.Num(tr, "size", 0), 0, 400),
            Field.Select("font", "글꼴", J.Str(tr, "font", ""), fonts),
            bub != null ? Field.Check("fit", "말풍선에 맞추기 (줄바꿈·크기)", true) : null,
            Field.Info("원문을 고치려면 보기 > 언어 > 원문 으로 바꾼 뒤 편집하세요."),
        }, "확인", new[] { ("이 번역 지우기", "clear") });
        if (v == null) return true;
        var nm = J.Clone(l.Meta);
        if (J.Str(v, "__action") == "clear")
        {
            if (nm["i18n"] is JsonObject all) { all.Remove(lang); if (all.Count == 0) nm.Remove("i18n"); }
            MetaLayers.Commit(l, "번역 지우기", nm, MetaLayers.Render);
            return true;
        }
        var t = I18n.Tr(nm, lang, true);
        string text = ((string)v["text"] ?? "").Replace("\r\n", "\n");
        if (J.Str(t, "text") != text) { t["text"] = text; t.Remove("x"); t.Remove("y"); }
        double size = (double)v["size"];
        if (size > 0) t["size"] = size; else t.Remove("size");
        string font = (string)v["font"];
        if (string.IsNullOrEmpty(font)) t.Remove("font"); else t["font"] = font;
        if (text.Trim().Length == 0) { if (nm["i18n"] is JsonObject all) { all.Remove(lang); if (all.Count == 0) nm.Remove("i18n"); } }
        else if (bub != null && v["fit"] is JsonValue fv && (bool)fv)
        {
            var tmp = new Layer { Id = l.Id, Kind = "text", Meta = nm };
            var o = FitOpts; o.MaxSize = size;   // 크기를 정했으면 그 크기부터 줄여 봄
            nm = AutoFit.Plan(d, tmp, bub, lang, o, out _) ?? nm;
        }
        else I18n.Recenter(d, nm, lang);
        MetaLayers.Commit(l, "번역 편집", nm, MetaLayers.Render);
        return true;
    }

    // ---------------------------------------------------------------- 언어별 내보내기
    public static void LangExportDialog()
    {
        var d = App.Doc; if (d == null) return;
        var langs = I18n.LangsIn(Work.Open(d, IsStory).Select(p => p.Doc));
        var fields = new List<Field> { Field.Check("l_", "원문", true) };
        fields.AddRange(langs.Select(l => Field.Check("l_" + l, I18n.Label(l), true)));
        if (IsStory) fields.Add(Field.Segment("scope", "범위", "work", Scope));
        fields.Add(Field.Select("mode", "형식", App.Get("langExportMode", "png"), ("png", "페이지 그림 PNG"), ("jpg", "페이지 그림 JPG"), ("webtoon", "웹툰 플랫폼 규격 (마지막 설정으로 나눠서)")));
        fields.Add(Field.Segment("scale", "배율 (페이지 그림)", "1", ("0.5", "50%"), ("1", "100%")));
        fields.Add(Field.Info("data/exports/작품_언어별_날짜/원문, en, ja … 폴더에 페이지 순서대로 저장합니다. 지금 그림·보기 언어는 바뀌지 않습니다."));
        var v = Dlg.Form("언어별 그림 내보내기 (일괄)", fields, "내보내기");
        if (v == null) return;
        var o = new LangExport.Opts { Langs = new(), Mode = (string)v["mode"], Scale = double.Parse((string)v["scale"] ?? "1", System.Globalization.CultureInfo.InvariantCulture) };
        if ((bool)v["l_"]) o.Langs.Add(null);
        foreach (var l in langs) if (v["l_" + l] is JsonValue b && (bool)b) o.Langs.Add(l);
        if (o.Langs.Count == 0) { App.Toast("언어를 하나 이상 고르세요"); return; }
        App.Set("langExportMode", o.Mode);
        var pages = Work.Open(d, (string)v["scope"] != "page");
        string folder = $"{ImageIO.SafeName(Work.StoryOf(d) ?? d.Name)}_언어별_{ImageIO.Stamp()}";
        string outDir = Path.Combine(ImageIO.ExportsDir, folder);
        int n = 0;
        Cursor.Current = Cursors.WaitCursor;
        try { n = LangExport.Run(pages, o, outDir); }
        finally { Cursor.Current = Cursors.Default; }
        App.InvalidateAll();
        App.Toast($"{o.Langs.Count}개 언어 × {pages.Count}쪽 → {n}개 파일: data/exports/{folder}");
    }
}
