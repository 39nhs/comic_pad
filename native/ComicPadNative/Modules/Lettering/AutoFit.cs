// 대사를 말풍선에 맞추기: 연결된 말풍선(textId)의 안쪽 영역(타원이면 타원, 사각이면 사각, 외침·생각은 안쪽 타원)에
// 글자 상자가 들어가도록 줄을 다시 나누고(낱말 단위, 한중일 글자는 글자 단위 + 줄머리 금칙) 글자 크기를 줄인다 (최소 크기까지).
// 세로쓰기는 한 줄(세로 열) 글자 수를 바꿔 맞춘다. 보기 언어가 있으면 그 언어 번역본(meta.i18n[lang])만 바꾸고 원문은 그대로.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;

namespace ComicPadNative.Modules.Lettering;

public static class AutoFit
{
    public sealed class Opts
    {
        public double MinCss = 11;      // 최소 글자 크기: 휴대폰 화면 px (PhoneW 폭 기준)
        public double PhoneW = 390;
        public bool KeepBreaks;         // 지금 줄바꿈 그대로 (크기만 줄임)
        public double MaxSize;          // 이 크기에서 시작 (0 = 원문 크기)
        public double MinDocPx(Document d) => MinCss * d.W / PhoneW;
    }
    public sealed record Result(bool Fits, double Size, int Lines, double Metric);

    /// <summary>말풍선 안쪽 영역: 가운데, 반지름(타원) 또는 반폭(사각)</summary>
    public static (double cx, double cy, double a, double b, bool rect) Inner(JsonObject bub)
    {
        double x = J.Num(bub, "x"), y = J.Num(bub, "y"), w = J.Num(bub, "w"), h = J.Num(bub, "h"), line = Math.Max(0, J.Num(bub, "line", 4));
        string shape = J.Str(bub, "shape", "ellipse");
        double k = InnerK(shape);
        double margin = Math.Max(4, Math.Min(w, h) * 0.06);
        bool rect = shape == "rect";
        double cr = rect ? Math.Min(w, h) * 0.18 * 0.3 : 0;   // 둥근 모서리만큼 조금 더
        double a = Math.Max(4, w / 2 * k - line - margin - cr), b = Math.Max(4, h / 2 * k - line - margin - cr);
        return (x + w / 2, y + h / 2, a, b, rect);
    }
    /// <summary>모양별 안쪽 비율 (외침: 안쪽 꼭지 0.86, 생각: 가운데 타원 0.86)</summary>
    public static double InnerK(string shape) => shape switch { "shout" => 0.84, "cloud" => 0.84, _ => 1.0 };

    /// <summary>가운데에 놓은 bw×bh 상자가 안쪽 영역을 얼마나 채우는지 (≤ 1 이면 들어감)</summary>
    public static double Metric(double bw, double bh, double a, double b, bool rect)
    {
        double u = bw / 2 / a, v = bh / 2 / b;
        return rect ? Math.Max(u * u, v * v) : u * u + v * v;
    }

    // ---------------------------------------------------------------- 줄 나누기
    const string NoStart = "、。，．・：；！？!?…‥」』）)〉》”’ー～ゃゅょっぁぃぅぇぉャュョッァィゥェォ.,:;%";
    const string NoEnd = "「『（(〈《“‘";
    static readonly Regex RubyTok = new(@"\{[^{}|]+\|[^{}]+\}");

    static bool IsCjk(string ch)
    {
        if (string.IsNullOrEmpty(ch)) return false;
        int c = char.ConvertToUtf32(ch, 0);
        return c is >= 0x3000 and <= 0x30FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF;
    }

    /// <summary>글자 단위 (루비 {漢字|かんじ} 는 한 덩어리)</summary>
    static List<string> Units(string s)
    {
        var o = new List<string>(); int last = 0;
        foreach (Match m in RubyTok.Matches(s))
        {
            o.AddRange(TextRender.Chars(s[last..m.Index]));
            o.Add(m.Value); last = m.Index + m.Length;
        }
        o.AddRange(TextRender.Chars(s[last..]));
        return o;
    }

    /// <summary>줄바꿈을 풀어 한 덩어리로 (한중일 글자 사이는 붙이고, 그 밖은 띄어 씀)</summary>
    public static string Unwrap(string text)
    {
        var lines = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                string prev = TextRender.Chars(lines[i - 1]).LastOrDefault(), next = TextRender.Chars(lines[i]).FirstOrDefault();
                if (!(IsCjk(prev) || IsCjk(next)) && !prev.EndsWith("-")) sb.Append(' ');
            }
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }

    /// <summary>나눌 수 있는 조각들 (앞에 빈칸이 있었는지). 한중일 글자는 한 글자씩, 줄머리·줄끝 금칙은 붙임</summary>
    static List<(string tok, bool sp)> Tokens(string s)
    {
        var raw = new List<(string tok, bool sp)>();
        var word = new StringBuilder(); bool sp = false;
        void Flush() { if (word.Length > 0) { raw.Add((word.ToString(), sp)); sp = false; word.Clear(); } }
        foreach (var u in Units(s))
        {
            if (u.Length > 0 && char.IsWhiteSpace(u[0])) { Flush(); sp = true; continue; }
            if (IsCjk(u) || u.StartsWith("{")) { Flush(); raw.Add((u, sp)); sp = false; continue; }
            word.Append(u);
        }
        Flush();
        var o = new List<(string tok, bool sp)>();
        foreach (var t in raw)
        {
            if (o.Count > 0 && !t.sp && (NoStart.Contains(t.tok[0]) || NoEnd.Contains(o[^1].tok[^1]))) { o[^1] = (o[^1].tok + t.tok, o[^1].sp); continue; }
            o.Add(t);
        }
        return o;
    }

    static string StripRuby(string s) => RubyTok.Replace(s, m => m.Value[1..m.Value.IndexOf('|')]);

    /// <summary>가로쓰기: maxW 폭에 맞춰 줄바꿈 (욕심쟁이 방식, 너무 긴 낱말은 글자 단위로)</summary>
    public static string Wrap(string src, double maxW, Func<string, double> width)
    {
        var lines = new List<string>(); string cur = "";
        double W(string s) => width(StripRuby(s));
        foreach (var (tok, sp) in Tokens(src))
        {
            string cand = cur.Length == 0 ? tok : cur + (sp ? " " : "") + tok;
            if (cur.Length > 0 && W(cand) > maxW) { lines.Add(cur); cur = tok; }
            else cur = cand;
            while (W(cur) > maxW)
            {
                var us = Units(cur);
                if (us.Count < 2) break;
                int k = us.Count - 1;
                while (k > 1 && W(string.Concat(us.Take(k))) > maxW) k--;
                lines.Add(string.Concat(us.Take(k))); cur = string.Concat(us.Skip(k)).TrimStart();
            }
        }
        if (cur.Length > 0) lines.Add(cur);
        return string.Join("\n", lines);
    }

    /// <summary>세로쓰기: 한 열 최대 n 글자 (빈칸은 열 머리에서 버림, 줄머리 금칙 글자는 앞 열에 매달기)</summary>
    public static string WrapV(string src, int n)
    {
        n = Math.Max(1, n);
        var cols = new List<List<string>> { new() };
        foreach (var u in Units(src))
        {
            var col = cols[^1];
            bool space = u.Length > 0 && char.IsWhiteSpace(u[0]);
            if (space && col.Count == 0) continue;
            if (col.Count >= n)
            {
                if (!space && NoStart.Contains(u[0]) && col.Count <= n) { col.Add(u); continue; }   // 매달기
                cols.Add(col = new List<string>());
                if (space) continue;
            }
            col.Add(u);
        }
        return string.Join("\n", cols.Where(c => c.Count > 0).Select(c => string.Concat(c).TrimEnd()));
    }

    // ---------------------------------------------------------------- 맞추기
    /// <summary>meta(그릴 meta, 고침) 를 말풍선 안에: text·size·x·y 를 바꿈. maxSize 부터 minSize 까지 줄여 봄</summary>
    public static Result FitMeta(JsonObject m, JsonObject bub, double maxSize, double minSize, bool keepBreaks)
    {
        var fam = Fonts.Family(J.Str(m, "font"));
        var st = Fonts.Style(fam, J.Bool(m, "bold", true), J.Bool(m, "italic"));
        bool vertical = J.Bool(m, "vertical");
        string src = keepBreaks ? (J.Str(m, "text", "") ?? "") : Unwrap(J.Str(m, "text", ""));
        var (cx, cy, a, b, rect) = Inner(bub);
        // 변형된 글자: 말풍선 가운데를 글자 좌표로, 크기는 배율만큼
        var mat = m["mat"] is JsonArray ? Mat2.From(J.Doubles(m["mat"])) : Mat2.Id;
        var lc = mat.Inverse().Apply(cx, cy);
        double sc = Math.Sqrt(Math.Abs(mat.Det)); if (sc < 1e-6) sc = 1;
        a /= sc; b /= sc;
        double pad = J.Num(m, "strokeWidth") + 4;   // 글자 상자 여백 (meta.box 와 같게)
        double track = J.Num(m, "tracking") / 100;
        maxSize = Math.Max(1, maxSize); minSize = Math.Clamp(minSize, 1, maxSize);

        (string text, double metric, int lines) Try(double size)
        {
            m["size"] = J.R(size); m["x"] = 0; m["y"] = 0;
            var cands = new List<string>();
            if (keepBreaks) cands.Add(src);
            else if (!vertical)
            {
                double Wd(string s) { var cs = TextRender.Chars(s); return Fonts.Measure(s, fam, st, size) + (cs.Count > 1 ? size * track * (cs.Count - 1) : 0); }
                double full = Wd(StripRuby(src));
                double lo = Math.Max(size * 1.2, 1), hi = Math.Max(lo, Math.Min(full + 1, 2 * a));
                for (int i = 0; i <= 12; i++) cands.Add(Wrap(src, lo + (hi - lo) * i / 12, Wd));
                cands.Add(src);
            }
            else
            {
                double step = size * (1.05 + track);
                int total = Math.Max(1, Units(src).Count);
                int lo = 1, hi = Math.Max(1, Math.Min(total, (int)Math.Floor(2 * b / step)));
                for (int k = lo; k <= hi; k++) cands.Add(WrapV(src, k));
            }
            string best = null; double bm = double.MaxValue;
            foreach (var t in cands.Distinct())
            {
                m["text"] = t;
                var (_, box) = TextRender.Layout(m, fam, st);
                double f = Metric(box.w + pad * 2, box.h + pad * 2, a, b, rect);
                if (f < bm) { bm = f; best = t; }
            }
            return (best ?? src, bm, (best ?? src).Split('\n').Length);
        }

        double s = maxSize;
        (string text, double metric, int lines) r;
        while (true)
        {
            r = Try(s);
            if (r.metric <= 1 || s <= minSize) break;
            double next = Math.Max(minSize, Math.Floor(s * 0.94 * 2) / 2);
            if (next >= s) next = Math.Max(minSize, s - 0.5);
            s = next;
        }
        m["text"] = r.text; m["size"] = J.R(s); m["x"] = 0; m["y"] = 0;
        var (_, bx) = TextRender.Layout(m, fam, st);
        m["x"] = J.R(lc.X - (bx.x + bx.w / 2)); m["y"] = J.R(lc.Y - (bx.y + bx.h / 2));
        return new Result(r.metric <= 1, s, r.lines, r.metric);
    }

    /// <summary>글자 레이어 하나를 말풍선에 맞춘 새 meta (lang 이 있으면 번역본만). 번역이 없으면 null</summary>
    public static JsonObject Plan(Document d, Layer t, JsonObject bub, string lang, Opts o, out Result res)
    {
        res = null;
        var nm = J.Clone(t.Meta);
        double minSize = o.MinDocPx(d);
        if (string.IsNullOrEmpty(lang))
        {
            var work = J.Clone(nm); work.Remove("i18n"); work.Remove("i18nShown");
            double max = o.MaxSize > 0 ? o.MaxSize : J.Num(nm, "size", 42);
            res = FitMeta(work, bub, max, Math.Min(minSize, max), o.KeepBreaks);
            foreach (var k in new[] { "text", "size", "x", "y" }) nm[k] = work[k]?.DeepClone();
            return nm;
        }
        var eff = I18n.Effective(d, nm, lang);
        if (eff == null) return null;
        double maxT = J.Num(nm, "size", 42);   // 번역은 원문 크기에서 시작 (다시 맞출 때 커질 수 있게)
        if (o.MaxSize > 0) maxT = o.MaxSize;
        else if (I18n.Tr(nm, lang)["size"] != null && o.KeepBreaks) maxT = J.Num(eff, "size", maxT);
        res = FitMeta(eff, bub, maxT, Math.Min(minSize, maxT), o.KeepBreaks);
        var tr = I18n.Tr(nm, lang, true);
        foreach (var k in new[] { "text", "size", "x", "y" }) tr[k] = eff[k]?.DeepClone();
        if (J.Bool(nm, "vertical") != J.Bool(eff, "vertical")) tr["vertical"] = J.Bool(eff, "vertical");
        return nm;
    }

    /// <summary>작품(또는 페이지)의 말풍선 대사를 모두 맞춤 (보기 언어 기준). (맞춘 수, 최소 크기로도 넘친 수, 바뀐 페이지 수)</summary>
    public static (int fitted, int overflow, int pages) FitAll(List<WorkPage> pages, Opts o, string onlyLayerId = null)
    {
        var plan = new Dictionary<WorkPage, Dictionary<Layer, JsonObject>>();
        int fitted = 0, over = 0;
        foreach (var p in pages)
        {
            string lang = I18n.LangOf(p.Doc);
            var links = Work.BubbleLinks(p.Doc);
            foreach (var l in p.Doc.Layers.Where(I18n.IsText))
            {
                if (onlyLayerId != null && l.Id != onlyLayerId) continue;
                if (J.Bool(l.Meta, "sfx") || !links.TryGetValue(l.Id, out var bub)) continue;
                if (l.Locked) continue;
                var nm = Plan(p.Doc, l, bub, lang, o, out var r);
                if (nm == null) continue;
                Work.PlanOf(plan, p)[l] = nm;
                fitted++; if (!r.Fits) over++;
            }
        }
        int n = Work.Apply(plan, "말풍선에 대사 맞추기");
        return (fitted, over, n);
    }
}
