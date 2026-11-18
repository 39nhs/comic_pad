// 모든 글자 레이어에서 찾아 바꾸기 (정규식·대소문자, 지금 페이지 / 작품 전체, 원문 또는 번역본)
// + 번역 빠짐 검사: 번역 칸이 비었거나 원문과 똑같은 줄 (맞춤법 검사처럼 목록으로)
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;

namespace ComicPadNative.Modules.Lettering;

public static class FindReplace
{
    public sealed class Query
    {
        public string Find = "", Replace = "";
        public bool Regex, MatchCase, WholeWork;
        public string Lang;      // null = 원문, 아니면 그 언어 번역본
    }
    public sealed record Hit(WorkPage Page, Layer L, int Count, string Before, string After);

    static Regex Make(Query q)
    {
        var opt = (q.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant;
        string pat = q.Regex ? q.Find : System.Text.RegularExpressions.Regex.Escape(q.Find);
        return new Regex(pat, opt, TimeSpan.FromSeconds(2));
    }

    static string TextOf(JsonObject m, string lang) => lang == null ? J.Str(m, "text", "") ?? "" : I18n.TrText(m, lang);

    /// <summary>찾기 (apply 면 바꾸기까지). 찾은 레이어별 개수와 바뀐 글</summary>
    public static List<Hit> Run(List<WorkPage> pages, Query q, bool apply)
    {
        var hits = new List<Hit>();
        if (string.IsNullOrEmpty(q.Find)) return hits;
        var re = Make(q);
        string repl = q.Regex ? q.Replace ?? "" : (q.Replace ?? "").Replace("$", "$$");
        var plan = new Dictionary<WorkPage, Dictionary<Layer, JsonObject>>();
        foreach (var p in pages)
            foreach (var l in p.Doc.Layers.Where(I18n.IsText))
            {
                string s = TextOf(l.Meta, q.Lang);
                if (s.Length == 0) continue;
                int n = re.Matches(s).Count;
                if (n == 0) continue;
                string after = re.Replace(s, repl);
                hits.Add(new Hit(p, l, n, s, after));
                if (!apply || after == s || l.Locked) continue;
                var nm = J.Clone(l.Meta);
                if (q.Lang == null) nm["text"] = after;
                else I18n.Tr(nm, q.Lang, true)["text"] = after;
                Work.PlanOf(plan, p)[l] = nm;
            }
        if (apply) Work.Apply(plan, "찾아 바꾸기");
        return hits;
    }

    // ---------------------------------------------------------------- 번역 빠짐 검사
    public sealed record Problem(LetterRow Row, string Why);

    /// <summary>번역이 비었거나 원문과 같은 줄 (메모 칸은 빼고)</summary>
    public static List<Problem> Untranslated(List<WorkPage> pages, string lang)
    {
        var res = new List<Problem>();
        foreach (var r in Work.Rows(pages))
        {
            if (r.Kind == "메모" || r.Original.Trim().Length == 0) continue;
            string t = r.Tr(lang);
            if (t.Trim().Length == 0) res.Add(new Problem(r, "번역 없음"));
            else if (Norm(t) == Norm(r.Original)) res.Add(new Problem(r, "원문과 같음"));
            else if (HasHangul(t) && lang != "ko") res.Add(new Problem(r, "한글이 남아 있음"));
        }
        return res;
    }
    static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
    static bool HasHangul(string s) => s.Any(c => c >= 0xAC00 && c <= 0xD7A3);
}
