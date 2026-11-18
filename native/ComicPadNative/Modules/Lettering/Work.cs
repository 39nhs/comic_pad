// 작품 단위로 다루기: 여러 페이지 작품(data/projects/작품/p001.cpad …)이면 모든 페이지를 읽는 순서로, 아니면 지금 문서 하나.
// 지금 열린 페이지는 메모리의 문서(저장 안 한 변경 포함)를 쓰고 실행취소 한 번으로 바꾸며, 다른 페이지는 파일을 읽어 바꾼 뒤 바로 저장한다.
// 대사 읽는 순서: 페이지 → 컷 순서(Script.PanelsInOrder: 위 줄부터, 줄 안 왼→오) → 위에서 아래.
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;
using StoryM = ComicPadNative.Modules.Extras.Story;

namespace ComicPadNative.Modules.Lettering;

public sealed class WorkPage
{
    public int No;              // 1부터
    public string Rel;          // 작품 안 페이지 ("작품/p001.cpad"), 작품이 아니면 null
    public Document Doc;
    public bool Current;        // 지금 열린 문서
}

/// <summary>대사 한 줄 (번역 표의 한 행)</summary>
public sealed class LetterRow
{
    public WorkPage Page; public Layer L;
    public int Order;           // 페이지 안 순서 (1부터)
    public string Kind;         // 대사 / 나레이션 / 효과음 / 메모
    public string Shape;        // 말풍선 모양 (타원·사각·외침·생각, 없음 = "")
    public JsonObject Bubble;   // 연결된 말풍선 (없으면 null)
    public string Original => J.Str(L.Meta, "text", "") ?? "";
    public string Font => J.Str(L.Meta, "font", "") ?? "";
    public double Size => J.Num(L.Meta, "size", 42);
    public string Note => J.Str(L.Meta, "letterNote", "") ?? "";
    public string Tr(string lang) => I18n.TrText(L.Meta, lang);
}

public static class Work
{
    public static readonly string[] Kinds = { "대사", "나레이션", "효과음", "메모" };
    public static readonly Dictionary<string, string> ShapeNames = new() { ["ellipse"] = "타원", ["rect"] = "사각", ["shout"] = "외침", ["cloud"] = "생각" };

    public static string StoryOf(Document d) => d?.FilePath == null ? null : StoryM.StoryOf(d);

    /// <summary>작품 페이지들 (whole=false 거나 작품이 아니면 지금 문서 하나)</summary>
    public static List<WorkPage> Open(Document cur, bool whole)
    {
        var res = new List<WorkPage>();
        string story = whole ? StoryOf(cur) : null;
        if (story == null) { res.Add(new WorkPage { No = 1, Rel = StoryM.Rel(cur?.FilePath), Doc = cur, Current = true }); return res; }
        string curRel = StoryM.Rel(cur.FilePath);
        var pages = StoryM.ListPages(story);
        for (int i = 0; i < pages.Count; i++)
        {
            Document d;
            if (pages[i] == curRel) d = cur;
            else
            {
                try { d = CpadIO.Load(StoryM.Full(pages[i])); } catch { continue; }
                // 다른 페이지도 지금 보기 언어로 (표·미리보기·내보내기가 같은 언어를 보도록)
                if (I18n.LangOf(d) != I18n.LangOf(cur)) I18n.SetLangRaw(d, I18n.LangOf(cur));
            }
            res.Add(new WorkPage { No = i + 1, Rel = pages[i], Doc = d, Current = d == cur });
        }
        if (!res.Any(p => p.Current)) res.Insert(0, new WorkPage { No = 0, Rel = curRel, Doc = cur, Current = true });
        return res;
    }

    /// <summary>글자 레이어 → 연결된 말풍선</summary>
    public static Dictionary<string, JsonObject> BubbleLinks(Document d)
    {
        var map = new Dictionary<string, JsonObject>();
        foreach (var bl in d.Layers.Where(Bubbles.IsEditable))
            foreach (var b in Bubbles.List(bl.Meta))
                if (J.Str(b, "textId") is { } tid && !map.ContainsKey(tid)) map[tid] = b;
        return map;
    }

    public static string KindOf(Layer l, JsonObject bubble)
    {
        string k = J.Str(l.Meta, "letterKind");
        if (k != null && Kinds.Contains(k)) return k;
        if (J.Bool(l.Meta, "sfx")) return "효과음";
        if (!l.Visible || l.Draft) return "메모";
        if (bubble != null) return J.Str(bubble, "shape", "ellipse") == "rect" ? "나레이션" : "대사";
        return "나레이션";
    }

    /// <summary>글자 상자 (그려진 모양, 문서 좌표). 없으면 기준점</summary>
    public static (double x, double y, double w, double h) BoxOf(Layer l)
    {
        if (l.Meta?["box"] is JsonObject b) return (J.Num(b, "x"), J.Num(b, "y"), J.Num(b, "w"), J.Num(b, "h"));
        return (J.Num(l.Meta, "x"), J.Num(l.Meta, "y"), 0, 0);
    }

    /// <summary>한 페이지의 대사들 (읽는 순서)</summary>
    public static List<LetterRow> Rows(WorkPage p, bool rtl = false)
    {
        var d = p.Doc;
        var links = BubbleLinks(d);
        var panels = Script.PanelsInOrder(d, rtl);
        var list = new List<(LetterRow row, double rank, double y, double x)>();
        foreach (var l in d.Layers)
        {
            if (!I18n.IsText(l)) continue;
            bool anyTr = l.Meta["i18n"] is JsonObject all && all.Any(kv => kv.Value is JsonObject o && (J.Str(o, "text", "") ?? "").Trim().Length > 0);
            if ((J.Str(l.Meta, "text", "") ?? "").Trim().Length == 0 && !anyTr) continue;
            links.TryGetValue(l.Id, out var bub);
            // 자리: 말풍선이 있으면 말풍선 가운데, 없으면 글자 상자 가운데
            double cx, cy, top;
            if (bub != null) { cx = J.Num(bub, "x") + J.Num(bub, "w") / 2; cy = J.Num(bub, "y") + J.Num(bub, "h") / 2; top = J.Num(bub, "y"); }
            else { var b = BoxOf(l); cx = b.x + b.w / 2; cy = b.y + b.h / 2; top = b.y; }
            // 컷 순위: 들어 있는 컷 번호. 컷 밖이면 그 아래 첫 컷 바로 앞 (-0.5)
            double rank = -1;
            for (int i = 0; i < panels.Count; i++)
            {
                var a = panels[i];
                if (cx >= a.x && cx <= a.x + a.w && cy >= a.y && cy <= a.y + a.h) { rank = i; break; }
            }
            if (rank < 0)
            {
                int next = panels.FindIndex(a => a.y > cy);
                rank = next < 0 ? panels.Count : next - 0.5;
            }
            string shape = bub != null ? J.Str(bub, "shape", "ellipse") : null;
            list.Add((new LetterRow { Page = p, L = l, Bubble = bub, Kind = KindOf(l, bub), Shape = shape != null && ShapeNames.TryGetValue(shape, out var sn) ? sn : "" }, rank, top, rtl ? -cx : cx));
        }
        // 같은 컷 안: 위→아래, 거의 같은 높이면 읽는 방향
        var sorted = list.OrderBy(t => t.rank).ThenBy(t => Math.Round(t.y / 24)).ThenBy(t => t.x).Select(t => t.row).ToList();
        for (int i = 0; i < sorted.Count; i++) sorted[i].Order = i + 1;
        return sorted;
    }

    public static List<LetterRow> Rows(List<WorkPage> pages, bool rtl = false) => pages.SelectMany(p => Rows(p, rtl)).ToList();

    public const string Keep = "\0";

    /// <summary>바뀐 meta 들을 반영: 지금 페이지는 실행취소 한 번, 나머지 페이지는 바로 저장. setLang 을 주면 보기 언어도 바꿈 (null = 원문). 바뀐 페이지 수</summary>
    public static int Apply(Dictionary<WorkPage, Dictionary<Layer, JsonObject>> plan, string label, string setLang = Keep)
    {
        int n = 0;
        foreach (var (p, changes) in plan)
        {
            if (changes.Count == 0 && setLang == Keep) continue;
            void Do()
            {
                foreach (var (l, meta) in changes) l.Meta = meta;
                if (setLang != Keep) I18n.SetLangRaw(p.Doc, setLang);
                else foreach (var (l, _) in changes) I18n.RenderInPlace(p.Doc, l, I18n.LangOf(p.Doc));
            }
            if (p.Current && p.Doc == App.Doc)
            {
                App.History.Struct(p.Doc, label, Do);
                p.Doc.Modified = true;
                App.InvalidateAll(); App.NotifyLayers();
            }
            else
            {
                Do();
                if (p.Rel != null) { try { StoryM.SavePageDoc(p.Doc, p.Rel); } catch (Exception ex) { App.Toast($"{p.Rel} 저장 실패: {ex.Message}"); } }
                else p.Doc.Modified = true;
            }
            n++;
        }
        return n;
    }

    public static Dictionary<Layer, JsonObject> PlanOf(Dictionary<WorkPage, Dictionary<Layer, JsonObject>> plan, WorkPage p)
    {
        if (!plan.TryGetValue(p, out var m)) plan[p] = m = new Dictionary<Layer, JsonObject>();
        return m;
    }
}
