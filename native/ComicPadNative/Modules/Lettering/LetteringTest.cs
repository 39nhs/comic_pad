// 번역·식자 시험 (창 없이): ComicPadNative.exe --selftest lettering 결과폴더   (끝 코드 = 실패 수)
//  - 3쪽 작품(컷·말풍선·대사·효과음·메모) → 대사 표: 읽는 순서·칸·BOM·CRLF, CSV/XLSX 다시 읽기
//  - 번역 채워 가져오기 → 원문 그대로, 새 언어(en) 번역본, 다른 페이지 저장, 실행취소
//  - 보기 언어 바꾸기: 그림이 글자 레이어 자리에서만 달라지고, 원문으로 돌리면 처음과 똑같음
//  - .cpad 저장·열기로 i18n 유지, 말풍선 맞추기(긴 글, 모양 4가지 + 세로쓰기: 글자 상자 ⊂ 안쪽 타원)
//  - 찾아 바꾸기 개수, 번역 빠짐 검사, 폰 미리보기 띠 높이·작은 글자 표시, 언어별 일괄 내보내기
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;
using ComicPadNative.UI;
using StoryM = ComicPadNative.Modules.Extras.Story;

namespace ComicPadNative.Modules.Lettering;

public static class LetteringTest
{
    static readonly Dictionary<string, string> En = new()
    {
        ["안녕하세요 오늘 날씨가 정말 좋네요"] = "Hello! The weather is really lovely today, isn't it?",
        ["배가 고프다"] = "I'm so hungry... when is lunch?",
        ["멈춰!"] = "STOP RIGHT THERE!",
        ["그날 밤, 모든 것이 바뀌었다"] = "That night, everything changed.",
        ["쾅!"] = "BOOM!",
        ["작은 글씨"] = "tiny print",
    };

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        // 사용자 작업물·설정은 건드리지 않게
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMICPAD_DATA"))) Environment.SetEnvironmentVariable("COMICPAD_DATA", Path.Combine(outDir, "data"));
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMICPAD_SETTINGS"))) Environment.SetEnvironmentVariable("COMICPAD_SETTINGS", Path.Combine(outDir, "settings.json"));
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { log.Add((ok ? "OK   " : "FAIL ") + what); if (!ok) fails++; }
        void Save(Document d, string name) { try { var flat = App.Comp.Flatten(d); using var b = CpadIO.ToBitmap(flat, flat.Full, 0xFFFFFFFF); b.Save(Path.Combine(outDir, name)); } catch (Exception ex) { Check(false, name + ": " + ex.Message); } }
        try
        {
            // ---------------------------------------------------------------- 1) 작품 만들기
            string story = "번역시험";
            string sdir = Path.Combine(StoryM.ProjectsDir, story);
            if (Directory.Exists(sdir)) Directory.Delete(sdir, true);
            for (int i = 1; i <= 3; i++) StoryM.SavePageDoc(MakePage(i), $"{story}/{StoryM.PageName(i)}.cpad");
            var cur = CpadIO.Load(StoryM.Full($"{story}/p001.cpad"));
            App.SetDoc(cur);
            var pages = Work.Open(cur, true);
            Check(pages.Count == 3 && pages[0].Current && pages[0].Doc == cur && !pages[1].Current, $"작품 3쪽 열기 ({pages.Count})");

            // ---------------------------------------------------------------- 2) 대사 표
            var rows = Work.Rows(pages);
            var expectOrder = new[] { "메모: 배경 확인", "안녕하세요 오늘 날씨가 정말 좋네요", "배가 고프다", "쾅!", "멈춰!", "그날 밤, 모든 것이 바뀌었다", "작은 글씨" };
            Check(rows.Count == 21, $"대사 21줄 ({rows.Count})");
            bool orderOk = true;
            for (int p = 0; p < 3; p++)
                for (int k = 0; k < 7; k++)
                {
                    var r = rows.ElementAtOrDefault(p * 7 + k);
                    if (r == null || r.Page.No != p + 1 || r.Order != k + 1 || Flat(r.Original) != expectOrder[k]) { orderOk = false; log.Add($"     순서 {p + 1}쪽 {k + 1}: {(r == null ? "없음" : $"{r.Page.No}쪽 {r.Order} {Flat(r.Original)}")}"); }
                }
            Check(orderOk, "읽는 순서: 페이지 → 컷 → 위에서 아래 (컷 밖 메모는 첫 컷 앞)");
            Check(rows.Take(7).Select(r => r.Kind).SequenceEqual(new[] { "메모", "대사", "대사", "효과음", "대사", "나레이션", "나레이션" }), "종류: 메모·대사·효과음·나레이션 (" + string.Join(",", rows.Take(7).Select(r => r.Kind)) + ")");
            Check(rows.Take(7).Select(r => r.Shape).SequenceEqual(new[] { "", "타원", "생각", "", "외침", "사각", "" }), "말풍선 모양 칸 (" + string.Join(",", rows.Take(7).Select(r => r.Shape)) + ")");
            var table = LetterTable.Table(rows, "en");
            Check(string.Join(",", table[0]) == "페이지,순서,레이어ID,종류,말풍선,원문,번역(en),글꼴,크기,메모" && table.All(t => t.Length == 10), "칸 10개 (머리: " + string.Join(",", table[0]) + ")");
            var xl = LetterTable.Csv(table, true);
            File.WriteAllBytes(Path.Combine(outDir, "대사표_엑셀.csv"), xl);
            Check(xl.Length > 3 && xl[0] == 0xEF && xl[1] == 0xBB && xl[2] == 0xBF, "엑셀용 CSV: UTF-8 BOM");
            string xs = Encoding.UTF8.GetString(xl, 3, xl.Length - 3);
            Check(xs.StartsWith("페이지,순서,레이어ID,종류,말풍선,원문,번역(en),글꼴,크기,메모\r\n") && xs.Contains("\r\n2,"), "엑셀용 CSV: CRLF 줄 끝");
            var plain = LetterTable.Csv(table, false);
            Check(plain[0] != 0xEF && !Encoding.UTF8.GetString(plain).Contains('\r'), "보통 CSV: BOM·CR 없음");
            Check(rows.Any(r => r.Original.Contains('\n')), "여러 줄 대사가 있음 (따옴표 시험)");
            Check(Same(LetterTable.ParseCsv(LetterTable.Decode(xl)), table), "엑셀용 CSV 다시 읽기 = 표 (따옴표·칸 안 줄바꿈)");
            Check(Same(LetterTable.ParseCsv(LetterTable.Decode(plain)), table), "보통 CSV 다시 읽기 = 표");
            var xlsx = LetterTable.Xlsx(table);
            File.WriteAllBytes(Path.Combine(outDir, "대사표.xlsx"), xlsx);
            Check(Same(LetterTable.ParseXlsx(xlsx), table), "XLSX 다시 읽기 = 표");
            // CP949 로 저장한 CSV (엑셀 "CSV (쉼표로 분리)")
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var cp = Encoding.GetEncoding(949).GetBytes(Encoding.UTF8.GetString(plain));
                Check(Same(LetterTable.ParseCsv(LetterTable.Decode(cp)), table), "CP949 CSV 도 읽음");
            }
            catch (Exception ex) { Check(false, "CP949: " + ex.Message); }

            // ---------------------------------------------------------------- 3) 번역 채워 가져오기
            var origText = pages.SelectMany(p => p.Doc.Layers.Where(I18n.IsText)).ToDictionary(l => l.Id, l => J.Str(l.Meta, "text"));
            var filled = new List<string[]> { table[0] };
            foreach (var t in table.Skip(1))
            {
                var c = (string[])t.Clone();
                string o = Flat(c[5]);
                if (c[3] == "메모") c[6] = "";
                else if (c[0] == "3" && o == "배가 고프다") c[6] = "";                           // 빠뜨린 번역
                else if (c[0] == "2" && o == "그날 밤, 모든 것이 바뀌었다") c[6] = c[5];          // 원문 그대로 둠
                else c[6] = En[o];
                filled.Add(c);
            }
            string filledPath = Path.Combine(outDir, "대사표_번역됨.csv");
            File.WriteAllBytes(filledPath, LetterTable.Csv(filled, true));
            var (recs, headLang) = LetterTable.Records(LetterTable.ReadFile(filledPath));
            Check(headLang == "en" && recs.Count == 21, $"머리에서 언어 읽기 ({headLang}, {recs.Count}줄)");
            var opts = new LetterTable.ImportOpts { Fit = true, Switch = true, FitOpts = new AutoFit.Opts { MinCss = 8 } };
            var res = LetterTable.Import(pages, recs, "en", opts);
            Check(res.Updated == 17 && res.Empty == 4 && res.NotFound == 0, $"가져오기: 바뀜 {res.Updated}·빈 칸 {res.Empty}·못 찾음 {res.NotFound}");
            Check(res.Fitted == 11, $"말풍선 대사 맞춤 {res.Fitted}개 (번역이 빈 1개 빼고, 최소 크기로도 여유가 모자람 {res.Overflow})");
            var all = pages.SelectMany(p => p.Doc.Layers.Where(I18n.IsText)).ToList();
            Check(all.All(l => J.Str(l.Meta, "text") == origText[l.Id]), "원문(meta.text)은 그대로");
            Check(all.Count(l => I18n.HasTr(l.Meta, "en")) == 17, "en 번역본 17개");
            Check(I18n.LangOf(cur) == "en" && cur.Layers.Where(I18n.IsText).All(l => (J.Str(l.Meta, "i18nShown") == "en") == I18n.HasTr(l.Meta, "en")), "가져온 언어로 보기 (지금 페이지)");
            var p2 = CpadIO.Load(StoryM.Full($"{story}/p002.cpad"));
            Check(I18n.LangOf(p2) == "en" && p2.Layers.Where(I18n.IsText).Count(l => I18n.HasTr(l.Meta, "en")) == 6, "다른 페이지는 파일에 저장됨 (번역 6개·보기 언어)");
            Check((string)StoryM.ReadInfo(story)["lang"] == "en", "작품 보기 언어 story.json");
            // 실행취소: 지금 페이지만 처음대로, 그림도 원문으로 (Heal)
            App.History.Undo(cur);
            Check(cur.Layers.Where(I18n.IsText).All(l => !I18n.HasTr(l.Meta, "en") && l.Meta["i18nShown"] == null), "실행취소 → 번역 없음, 원문으로 다시 그림");
            App.History.Redo(cur);
            Check(cur.Layers.Where(I18n.IsText).Count(l => J.Str(l.Meta, "i18nShown") == "en") == 6, "다시 실행 → en 으로 그림");

            // ---------------------------------------------------------------- 4) 보기 언어: 글자 레이어만 달라짐
            var nonText = cur.Layers.Where(l => !I18n.IsText(l)).ToDictionary(l => l, l => l.Px);
            I18n.SetLangRaw(cur, null);
            var boundsOrig = TextBounds(cur);
            var A = Pixels(cur);
            Save(cur, "page1_원문.png");
            I18n.SetLangRaw(cur, "en");
            var boundsEn = TextBounds(cur);
            var B = Pixels(cur);
            Save(cur, "page1_en.png");
            Check(nonText.All(kv => kv.Key.Px == kv.Value), "언어를 바꿔도 다른 레이어 그림은 그대로");
            var region = boundsOrig.Union(boundsEn);
            int diff = 0, outside = 0;
            for (int y = 0; y < cur.H; y++)
                for (int x = 0; x < cur.W; x++)
                {
                    int i = y * cur.W + x;
                    if (A[i] == B[i]) continue;
                    diff++;
                    if (!region.Contains(x, y)) outside++;
                }
            Check(diff > 1000 && outside == 0, $"원문↔en: 다른 픽셀 {diff}개, 글자 레이어 밖 {outside}개");
            I18n.SetLangRaw(cur, null);
            var C = Pixels(cur);
            Check(A.SequenceEqual(C), "원문으로 돌리면 처음과 똑같음");
            I18n.SetLangRaw(cur, "en");
            Check(cur.Layers.Where(I18n.IsText).All(l => J.Str(l.Meta, "text") == origText[l.Id]), "언어를 바꿔도 원문 그대로");

            // ---------------------------------------------------------------- 5) .cpad 왕복
            string rt = Path.Combine(outDir, "lettering_rt.cpad");
            string keepPath = cur.FilePath;
            CpadIO.Save(cur, rt);
            cur.FilePath = keepPath;                       // 작품 페이지로 계속 (Save 는 경로를 바꿈)
            var back = CpadIO.Load(rt);
            bool rtOk = cur.Layers.Where(I18n.IsText).All(l => back.ById(l.Id) is { } bl && bl.Meta["i18n"]?.ToJsonString() == l.Meta["i18n"]?.ToJsonString() && J.Str(bl.Meta, "text") == J.Str(l.Meta, "text"));
            Check(rtOk && I18n.LangOf(back) == "en", ".cpad 저장·열기: i18n·원문·보기 언어 유지");
            Check(back.Layers.Where(I18n.IsText).All(l => l.Meta["text"] is JsonValue), "웹 판 호환: meta.text 는 늘 원문 문자열");

            // ---------------------------------------------------------------- 6) 말풍선 맞추기
            var fd = FitDoc(out var fitTexts);
            App.SetDoc(fd);
            var fitPages = Work.Open(fd, false);
            var (fn, fover, _) = AutoFit.FitAll(fitPages, new AutoFit.Opts { MinCss = 4 });
            Check(fn == fitTexts.Count && fover == 0, $"긴 대사 {fn}개 맞춤 (넘침 {fover})");
            var links = Work.BubbleLinks(fd);
            foreach (var t in fitTexts)
            {
                var l = fd.ById(t); var bub = links[t];
                var (inside, worst) = BoxInBubble(l.Meta, bub);
                Check(inside, $"글자 상자 ⊂ 말풍선 안쪽 ({J.Str(bub, "shape")}{(J.Bool(l.Meta, "vertical") ? ", 세로" : "")}, 크기 {J.Num(l.Meta, "size"):0.#}, 줄 {J.Str(l.Meta, "text").Split('\n').Length}, 값 {worst:0.00})");
            }
            Check(J.Num(fd.ById(fitTexts[0]).Meta, "size") < 42, "긴 글은 글자 크기가 줄어듦");
            Save(fd, "fit_sample.png");
            App.History.Undo(fd);
            Check(fitTexts.All(t => J.Num(fd.ById(t).Meta, "size") == 42), "맞추기 실행취소 한 번");
            App.History.Redo(fd);
            // 번역본(en)도 말풍선 안 (가져오며 맞춘 것)
            App.SetDoc(cur);
            var links1 = Work.BubbleLinks(cur);
            int trIn = 0, trAll = 0;
            foreach (var l in cur.Layers.Where(l => I18n.IsText(l) && I18n.HasTr(l.Meta, "en") && links1.ContainsKey(l.Id)))
            { trAll++; if (BoxInBubble(l.Meta, links1[l.Id]).inside) trIn++; }
            Check(trAll == 4 && trIn == trAll, $"en 번역도 말풍선 테두리 안 ({trIn}/{trAll})");

            // ---------------------------------------------------------------- 7) 찾아 바꾸기·빠짐 검사
            pages = Work.Open(cur, true);
            var q = new FindReplace.Query { Find = "Hello", Lang = "en" };
            Check(FindReplace.Run(pages, q, false).Sum(h => h.Count) == 3, "찾기 (작품 전체, en): Hello 3곳");
            Check(FindReplace.Run(Work.Open(cur, false), q, false).Sum(h => h.Count) == 1, "찾기 (지금 페이지): 1곳");
            Check(FindReplace.Run(pages, new FindReplace.Query { Find = "hello", Lang = "en", MatchCase = true }, false).Count == 0, "대소문자 구분");
            var rx = new FindReplace.Query { Find = @"(\w+)!$", Replace = "$1!!", Regex = true, Lang = "en" };
            int rxn = FindReplace.Run(pages, rx, false).Sum(h => h.Count);
            Check(rxn == 6, $"정규식 찾기 ({rxn}곳: STOP RIGHT THERE!, BOOM! × 3쪽)");
            var hits = FindReplace.Run(pages, new FindReplace.Query { Find = "안녕하세요", Replace = "반가워요" }, true);
            Check(hits.Sum(h => h.Count) == 3, "원문 바꾸기 (작품 전체) 3곳");
            var p3 = CpadIO.Load(StoryM.Full($"{story}/p003.cpad"));
            Check(cur.Layers.Concat(p3.Layers).Count(l => I18n.IsText(l) && (J.Str(l.Meta, "text") ?? "").StartsWith("반가워요")) == 2
                  && p3.Layers.Where(I18n.IsText).Count(l => I18n.TrText(l.Meta, "en").StartsWith("Hello")) == 1, "바꾼 원문은 저장되고 번역본은 그대로");
            FindReplace.Run(pages, rx, true);
            Check(cur.Layers.Any(l => I18n.IsText(l) && I18n.TrText(l.Meta, "en") == "BOOM!!"), "정규식 바꾸기 ($1)");
            var probs = FindReplace.Untranslated(Work.Open(cur, true), "en");
            Check(probs.Count == 2 && probs.Any(p => p.Why == "번역 없음" && p.Row.Page.No == 3) && probs.Any(p => p.Why == "원문과 같음" && p.Row.Page.No == 2),
                $"번역 빠짐 검사: {string.Join(" · ", probs.Select(p => $"{p.Row.Page.No}쪽 {p.Why}"))}");

            // ---------------------------------------------------------------- 8) 폰 미리보기
            pages = Work.Open(cur, true);
            var spec = new MobilePreview.Spec { W = 800, H = 1280, Fmt = "jpg", MaxMB = 2, Smart = true };
            using (var strip = MobilePreview.Build(pages.Select(p => p.Doc).ToList(), spec))
            {
                Check(strip.H == 4800 && strip.Slices.Sum(s => s.bmp.Height) == 4800 && strip.Pages.Count == 3, $"띠 높이 800 폭: {strip.H}px (3쪽 × 1600)");
                Check(Math.Abs(strip.CssH(390) - 2340) < 0.6 && Math.Abs(strip.CssH(360) - 2160) < 0.6, $"폰 높이 390: {strip.CssH(390):0}px, 360: {strip.CssH(360):0}px");
                int expectSlices = pages.Sum(p => Filters.ExportCommands.WebtoonSlicesOf(p.Doc, 800, 1280, "jpg", 2 << 20, true).Count);
                Check(strip.Slices.Count == expectSlices, $"내보내기와 같은 장 수 ({strip.Slices.Count})");
                var v = new MobilePreview.View { Dev = MobilePreview.Devices[1], MinCss = 11 };
                var tiny = strip.Marks.Where(m => m.Text.Contains("tiny") || m.Text.Contains("작은")).ToList();
                Check(tiny.Count == 3 && tiny.All(m => Math.Abs(strip.CssOf(m, 390) - 14 * 390 / 800.0) < 0.05), $"작은 글자: {string.Join(", ", tiny.Select(m => strip.CssOf(m, 390).ToString("0.0")))}px (< 11 표시)");
                var boom = strip.Marks.Where(m => m.Text.StartsWith("BOOM")).ToList();
                Check(boom.Count == 3 && boom.All(m => strip.CssOf(m, 390) >= 11), "큰 효과음은 표시 안 함");
                Check(tiny.All(m => m.R.Y >= strip.Pages[m.Page].top && m.R.Bottom <= strip.Pages[m.Page].top + strip.Pages[m.Page].h), "글자 자리가 자기 페이지 안");
                using (var shot = MobilePreview.Shot(strip, v)) { shot.Save(Path.Combine(outDir, "phone_390_top.png")); var fs = MobilePreview.FrameSize(v); Check(shot.Width == fs.Width + 32 && shot.Height == fs.Height + 32, $"폰 화면 그림 {shot.Width}×{shot.Height}"); }
                v.Scroll = 1180 - 300; v.Dev = MobilePreview.Devices[0];
                using (var shot = MobilePreview.Shot(strip, v)) shot.Save(Path.Combine(outDir, "phone_360_page2.png"));
                v.Dev = MobilePreview.Devices[2]; v.Scroll = MobilePreview.MaxScroll(strip, v);
                Check(Math.Abs(v.Scroll - (strip.CssH(412) - 915)) < 0.01, "맨 아래까지 넘기기");
                using (var shot = MobilePreview.Shot(strip, v)) shot.Save(Path.Combine(outDir, "phone_412_end.png"));
                log.Add("     " + MobilePreview.Summary(strip, v, spec));
            }
            using (var strip = MobilePreview.Build(pages.Select(p => p.Doc).ToList(), spec, sliceGap: 6, pageGap: 40))
            {
                int slices = strip.Slices.Count;
                Check(strip.H == 4800 + 6 * (slices - 3) + 40 * 2, $"틈 넣은 띠 높이 {strip.H} (장 {slices})");
                var v = new MobilePreview.View { Dev = MobilePreview.Devices[1], Scroll = 760 * 390 / 800.0 };
                using var shot = MobilePreview.Shot(strip, v); shot.Save(Path.Combine(outDir, "phone_390_gaps.png"));
            }

            // ---------------------------------------------------------------- 9) 언어별 일괄 내보내기
            var pxBefore = cur.Layers.Where(I18n.IsText).ToDictionary(l => l, l => l.Px);
            string le = Path.Combine(outDir, "언어별");
            if (Directory.Exists(le)) Directory.Delete(le, true);
            int files = LangExport.Run(pages, new LangExport.Opts { Langs = new() { null, "en" }, Mode = "png", Scale = 0.5 }, le);
            Check(files == 6 && File.Exists(Path.Combine(le, "원문", "001.png")) && File.Exists(Path.Combine(le, "en", "003.png")), $"원문·en × 3쪽 = {files}개 파일");
            Check(I18n.LangOf(cur) == "en" && pxBefore.All(kv => kv.Key.Px == kv.Value), "내보낸 뒤 보기 언어·그림 그대로");
            using (var a = new Bitmap(Path.Combine(le, "원문", "001.png"))) using (var b = new Bitmap(Path.Combine(le, "en", "001.png")))
                Check(a.Width == 400 && a.Height == 800 && !SameBmp(a, b), "언어마다 다른 그림 (50%)");
            int wt = LangExport.Run(pages.Take(1).ToList(), new LangExport.Opts { Langs = new() { "en" }, Mode = "webtoon", Spec = spec }, Path.Combine(le, "webtoon"));
            Check(wt == 2, $"웹툰 규격으로도 ({wt}장)");

            // ---------------------------------------------------------------- 10) 창 그려 보기 (화면 밖)
            try
            {
                using (var b = MobilePreview.WindowShot(v => v.Scroll = 300)) b.Save(Path.Combine(outDir, "ui_phone_window.png"));
                using (var b = LetterGrid.WindowShot(false)) b.Save(Path.Combine(outDir, "ui_letter_grid.png"));
                using (var b = LetterGrid.WindowShot(true)) b.Save(Path.Combine(outDir, "ui_letter_grid_problems.png"));
                Check(true, "폰 미리보기·번역 표 창 그리기");
            }
            catch (Exception ex) { Check(false, "창 그리기: " + ex.Message); }
        }
        catch (Exception ex) { Check(false, "예외: " + ex); }
        log.Add(fails == 0 ? "번역·식자 시험 통과" : $"번역·식자 시험 실패 {fails}개");
        File.WriteAllLines(Path.Combine(outDir, "report.txt"), log);
        return fails;
    }

    static string Flat(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();

    static bool Same(List<string[]> a, List<string[]> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Length < b[i].Length && !b[i].Skip(a[i].Length).All(x => x == "")) return false;
            for (int j = 0; j < b[i].Length; j++) if ((a[i].ElementAtOrDefault(j) ?? "") != (b[i][j] ?? "")) return false;
        }
        return true;
    }

    static uint[] Pixels(Document d) { var f = App.Comp.Flatten(d); return f.Read(f.Full); }
    static Rect TextBounds(Document d) { var r = Rect.Empty; foreach (var l in d.Layers.Where(I18n.IsText)) r = r.Union(l.Px.Bounds()); return r; }
    static bool SameBmp(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return false;
        for (int y = 0; y < a.Height; y += 2) for (int x = 0; x < a.Width; x += 2) if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    /// <summary>글자 상자(meta.box) 네 모서리가 말풍선 안쪽(테두리 안, 외침·생각은 안쪽 비율)에 있는지</summary>
    static (bool inside, double worst) BoxInBubble(JsonObject m, JsonObject bub)
    {
        var b = (JsonObject)m["box"];
        double bx = J.Num(b, "x"), by = J.Num(b, "y"), bw = J.Num(b, "w"), bh = J.Num(b, "h");
        string shape = J.Str(bub, "shape", "ellipse");
        double line = J.Num(bub, "line", 4), k = AutoFit.InnerK(shape);
        double cx = J.Num(bub, "x") + J.Num(bub, "w") / 2, cy = J.Num(bub, "y") + J.Num(bub, "h") / 2;
        double a = J.Num(bub, "w") / 2 * k - line, c = J.Num(bub, "h") / 2 * k - line;
        double worst = 0;
        foreach (var (x, y) in new[] { (bx, by), (bx + bw, by), (bx, by + bh), (bx + bw, by + bh) })
        {
            double u = (x - cx) / a, v = (y - cy) / c;
            worst = Math.Max(worst, shape == "rect" ? Math.Max(u * u, v * v) : u * u + v * v);
        }
        return (worst <= 1.0001, worst);
    }

    static Layer TextLayer(Document d, string name, string text, double x, double y, double size, Action<JsonObject> more = null, JsonObject baseMeta = null)
    {
        var meta = baseMeta ?? TextRender.Defaults();
        meta["text"] = text; meta["x"] = x; meta["y"] = y; meta["size"] = size;
        more?.Invoke(meta);
        var l = new Layer(d, name, "text") { Meta = meta };
        d.Layers.Add(l);
        l.Px = MetaLayers.Render(l).s;
        return l;
    }

    /// <summary>시험 페이지: 컷 2개, 대본 4줄(말풍선), 효과음, 숨긴 메모, 작은 글씨</summary>
    static Document MakePage(int i)
    {
        var d = Document.New(800, 1600, $"번역시험 {i}쪽");
        App.SetDoc(d);
        var pm = Panels.NewMeta(d);
        pm["panels"] = Panels.PanelsJson(new[] { Panels.RectPts(40, 40, 720, 700), Panels.RectPts(40, 820, 720, 740) });
        var pl = new Layer(d, "컷", "panel") { Meta = pm }; d.Layers.Add(pl);
        pl.Px = MetaLayers.Render(pl).s;
        Script.AutoPlace(Script.Parse("철수: 안녕하세요 오늘 날씨가 정말 좋네요\n(생각) 배가 고프다\n! 멈춰!\n# 그날 밤, 모든 것이 바뀌었다"), new Script.Opts(), seed: i);
        TextLayer(d, "효과음", "쾅!", 560, 560, 140, baseMeta: Sfx.Defaults());
        TextLayer(d, "메모", "메모: 배경 확인", 400, 6, 20).Visible = false;
        TextLayer(d, "작은 글씨", "작은 글씨", 600, 1500, 14);
        d.Modified = false;
        return d;
    }

    /// <summary>맞추기 시험 문서: 모양 4가지 + 세로쓰기, 긴 대사 (크기 42)</summary>
    static Document FitDoc(out List<string> texts)
    {
        var d = Document.New(900, 1300, "맞추기 시험");
        App.SetDoc(d);
        texts = new();
        var bl = new Layer(d, "말풍선", "bubble") { Meta = new JsonObject { ["bubbles"] = new JsonArray() } };
        d.Layers.Add(bl);
        var specs = new (string shape, double x, double y, double w, double h, string text, bool vert)[]
        {
            ("ellipse", 40, 40, 380, 240, "Hello there! This is a really long line of dialogue that would never fit inside this little bubble without wrapping and shrinking.", false),
            ("rect", 470, 40, 390, 200, "그날 밤, 우리는 아무 말도 하지 않았다. 창밖에는 비가 내렸고, 시계 소리만 방 안을 채웠다.", false),
            ("shout", 40, 360, 420, 300, "WHAT DO YOU MEAN YOU ATE THE WHOLE CAKE ALREADY?!", false),
            ("cloud", 480, 330, 380, 280, "어쩌면 내일은 조금 더 나은 하루가 될지도 몰라... 그렇게 믿고 싶다.", false),
            ("ellipse", 300, 720, 260, 520, "今日はとても長い一日だった。本当に疲れたよ、もう寝よう。", true),
        };
        int n = 0;
        foreach (var s in specs)
        {
            var t = TextLayer(d, "대사" + ++n, s.text, s.x + s.w / 2, s.y + s.h / 2, 42, m => { if (s.vert) { m["vertical"] = true; m["font"] = "Yu Gothic"; } });
            ((JsonArray)bl.Meta["bubbles"]).Add(new JsonObject
            {
                ["id"] = "fb" + n, ["x"] = s.x, ["y"] = s.y, ["w"] = s.w, ["h"] = s.h, ["shape"] = s.shape, ["line"] = 4, ["fill"] = "#ffffff", ["stroke"] = "#000000",
                ["tailWidth"] = 0.45, ["seed"] = 11 * n, ["tail"] = null, ["textId"] = t.Id,
            });
            texts.Add(t.Id);
        }
        // 말풍선을 글자 아래로
        d.Layers.Remove(bl); d.Layers.Insert(1, bl);
        bl.Px = MetaLayers.Render(bl).s;
        return d;
    }
}
