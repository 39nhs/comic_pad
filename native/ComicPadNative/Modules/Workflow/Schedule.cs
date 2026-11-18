// 작업 일정 (제작 일정표): 페이지마다 단계(콘티 → 밑그림 → 펜선 → 채색 → 식자 → 완료, 바꿀 수 있음)·마감일·회차(에피소드) 마감.
// 저장 위치
//  - 여러 페이지 작품: data/projects/<작품>/story.json 의 "schedule" (웹 판이 쓰는 status·deadline 도 함께 맞춰 씀 → 웹 판 진행표와 같은 값)
//  - 한 장 문서: doc.Extra["schedule"] (.cpad 에 그대로, 웹 판은 모르는 값이라 무시하고 열림)
//  schedule = { stages: [{id,label,color,web}], stage: {페이지: 단계id}, reached: {페이지: {단계id: 'YYYY-MM-DD'}}, due: {페이지: 'YYYY-MM-DD'},
//               deadline: 'YYYY-MM-DD', episodes: [{id,name,due,pages:[페이지]}] }
// 계산(남은 일수·필요 속도·실제 속도·마감 지남)은 날짜를 받아서 하는 순수 함수 → 시험에서 정해진 날짜로 확인.
using System.Globalization;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;

namespace ComicPadNative.Modules.Workflow;

/// <summary>단계 하나. Web = 웹 판 진행표 값 (conti|line|color|done) — 웹 판과 같이 쓰려고</summary>
public sealed record Stage(string Id, string Label, string Color, string Web);

public sealed class Episode
{
    public string Id, Name;
    public DateTime? Due;
    public List<string> Pages = new();
}

/// <summary>일정 값 (JSON 그대로 들고 있다가 저장)</summary>
public sealed class Plan
{
    public static readonly Stage[] DefaultStages =
    {
        new("conti", "콘티", "#8e5bd6", "conti"), new("sketch", "밑그림", "#b07cc6", "conti"), new("ink", "펜선", "#3d7be0", "line"),
        new("color", "채색", "#f08c2e", "color"), new("letter", "식자", "#d4a72c", "color"), new("done", "완료", "#2fa35b", "done"),
    };
    public const string DateFmt = "yyyy-MM-dd";
    public static string D(DateTime d) => d.ToString(DateFmt, CultureInfo.InvariantCulture);
    public static DateTime? P(JsonNode n) => n is JsonValue v && v.TryGetValue(out string s) && DateTime.TryParseExact(s, DateFmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;

    public readonly JsonObject Root;
    public Plan(JsonObject root = null) { Root = root ?? new JsonObject(); }

    JsonObject Obj(string k) { if (Root[k] is not JsonObject o) { o = new JsonObject(); Root[k] = o; } return o; }

    // ---- 단계 --------------------------------------------------------------------------------
    public List<Stage> Stages
    {
        get
        {
            if (Root["stages"] is JsonArray a && a.Count >= 2)
            {
                var list = new List<Stage>();
                for (int i = 0; i < a.Count; i++)
                {
                    var o = a[i] as JsonObject; if (o == null) continue;
                    string id = (string)o["id"]; if (string.IsNullOrEmpty(id) || list.Any(s => s.Id == id)) continue;
                    list.Add(new Stage(id, (string)o["label"] ?? id, (string)o["color"] ?? "#888888", (string)o["web"] ?? WebFor(i, a.Count)));
                }
                if (list.Count >= 2) return list;
            }
            return DefaultStages.ToList();
        }
    }
    /// <summary>사용자 단계 목록의 웹 판 값: 마지막 = 완료, 나머지는 위치에 따라 콘티·선화·채색</summary>
    public static string WebFor(int i, int n) => i >= n - 1 ? "done" : (i * 3 / Math.Max(1, n - 1)) switch { 0 => "conti", 1 => "line", _ => "color" };

    public void SetStages(IEnumerable<Stage> stages)
    {
        var list = stages.Where(s => !string.IsNullOrWhiteSpace(s.Id)).GroupBy(s => s.Id).Select(g => g.First()).ToList();
        if (list.Count < 2) throw new ArgumentException("단계는 2개 이상이어야 합니다");
        Root["stages"] = new JsonArray(list.Select((s, i) => (JsonNode)new JsonObject { ["id"] = s.Id, ["label"] = s.Label, ["color"] = s.Color, ["web"] = s.Web ?? WebFor(i, list.Count) }).ToArray());
        // 없어진 단계에 있던 페이지는 단계 지움
        var ids = list.Select(s => s.Id).ToHashSet();
        foreach (var (p, v) in Obj("stage").ToList()) if (!ids.Contains((string)v)) Obj("stage").Remove(p);
    }

    public int IndexOf(string id) => id == null ? -1 : Stages.FindIndex(s => s.Id == id);
    public Stage StageById(string id) => Stages.Find(s => s.Id == id);
    public string StageOf(string page) => Root["stage"]?[page] is JsonValue v && v.TryGetValue(out string s) && IndexOf(s) >= 0 ? s : null;
    public string LastId => Stages[^1].Id;
    public bool IsDone(string page) => StageOf(page) == LastId;
    /// <summary>페이지 진행 정도 0~1 (단계 없음 = 0, 완료 = 1)</summary>
    public double Progress(string page) { int i = IndexOf(StageOf(page)); return i < 0 ? 0 : i / (double)(Stages.Count - 1); }

    /// <summary>단계 바꾸기. 처음 그 단계에 닿은 날을 기록 (되돌리면 뒤 단계 기록은 지움)</summary>
    public void SetStage(string page, string id, DateTime when)
    {
        var st = Obj("stage"); var reached = Obj("reached");
        int idx = IndexOf(id);
        if (idx < 0) { st.Remove(page); reached.Remove(page); return; }
        st[page] = id;
        if (reached[page] is not JsonObject r) { r = new JsonObject(); reached[page] = r; }
        var stages = Stages;
        for (int i = 0; i < stages.Count; i++)
        {
            if (i <= idx) { if (r[stages[i].Id] == null) r[stages[i].Id] = D(when); }
            else r.Remove(stages[i].Id);
        }
    }
    /// <summary>다음 단계로 (완료면 그대로). 바뀐 단계 id</summary>
    public string Advance(string page, DateTime when)
    {
        int i = IndexOf(StageOf(page));
        string next = Stages[Math.Min(Stages.Count - 1, i + 1)].Id;
        SetStage(page, next, when);
        return next;
    }
    public DateTime? Reached(string page, string stageId) => P(Root["reached"]?[page]?[stageId]);

    // ---- 마감 --------------------------------------------------------------------------------
    public DateTime? Deadline { get => P(Root["deadline"]); set { if (value == null) Root.Remove("deadline"); else Root["deadline"] = D(value.Value); } }
    public DateTime? DueOf(string page) => P(Root["due"]?[page]);
    public void SetDue(string page, DateTime? d) { if (d == null) Obj("due").Remove(page); else Obj("due")[page] = D(d.Value); }
    /// <summary>지금 페이지의 실제 마감: 페이지 마감 → 회차 마감 → 작품 마감</summary>
    public DateTime? EffectiveDue(string page) => DueOf(page) ?? Episodes.Where(e => e.Pages.Contains(page) && e.Due != null).Select(e => e.Due).Min() ?? Deadline;

    public List<Episode> Episodes
    {
        get
        {
            var list = new List<Episode>();
            if (Root["episodes"] is JsonArray a)
                foreach (var n in a.OfType<JsonObject>())
                    list.Add(new Episode { Id = (string)n["id"] ?? U.Uid("ep"), Name = (string)n["name"] ?? "회차", Due = P(n["due"]), Pages = (n["pages"] as JsonArray)?.Select(x => (string)x).Where(x => x != null).ToList() ?? new() });
            return list;
        }
        set => Root["episodes"] = new JsonArray(value.Select(e => (JsonNode)new JsonObject
        {
            ["id"] = e.Id ?? U.Uid("ep"), ["name"] = e.Name, ["due"] = e.Due == null ? null : D(e.Due.Value),
            ["pages"] = new JsonArray(e.Pages.Select(p => (JsonNode)p).ToArray()),
        }).ToArray());
    }

    // ---- 웹 판 진행표 값과 맞추기 ---------------------------------------------------------------
    public string WebOf(string stageId) => StageById(stageId)?.Web;
    public string FromWeb(string web) => web == null ? null : Stages.Find(s => s.Web == web)?.Id;
}

/// <summary>남은 일수·속도 계산 결과</summary>
public sealed record PaceInfo(double RemainingUnits, int RemainingPages, int TotalPages, int? DaysLeft, double? NeededPerDay, double ActualPerDay, int WindowDays, DateTime? EstimatedFinish, string Verdict, bool Behind);

/// <summary>마감 지난 항목</summary>
public sealed record OverdueItem(string Kind, string Label, DateTime Due, int DaysLate, string Page);

/// <summary>순수 계산 (날짜를 받아서)</summary>
public static class Sched
{
    /// <summary>단계별 페이지 수 (단계 없음은 null 칸)</summary>
    public static List<(Stage stage, int count)> Counts(Plan plan, IList<string> pages)
    {
        var res = plan.Stages.Select(s => (s, pages.Count(p => plan.StageOf(p) == s.Id))).ToList();
        res.Insert(0, (null, pages.Count(p => plan.StageOf(p) == null)));
        return res;
    }

    /// <summary>
    /// 속도: 남은 일(페이지 × 남은 단계 비율) ÷ 남은 일수(오늘·마감일 포함) = 필요 속도,
    /// 최근 window 일 동안 나아간 단계(페이지 단위) ÷ window = 실제 속도
    /// </summary>
    public static PaceInfo Pace(Plan plan, IList<string> pages, DateTime today, DateTime? deadline, int window = 7)
    {
        today = today.Date;
        int n = plan.Stages.Count;
        double remaining = pages.Sum(p => 1 - plan.Progress(p));
        int remPages = pages.Count(p => !plan.IsDone(p));
        DateTime start = today.AddDays(-(window - 1));
        double units = 0;
        foreach (var p in pages)
        {
            int before = -1, upto = -1;
            for (int i = 0; i < n; i++)
            {
                var r = plan.Reached(p, plan.Stages[i].Id);
                if (r == null || r > today) continue;
                if (r < start) before = Math.Max(before, i);
                upto = Math.Max(upto, i);
            }
            // 진행 정도는 Progress 와 같이 (첫 단계 = 0, 완료 = 1): 기간 안에 늘어난 만큼
            double Un(int i) => i < 0 ? 0 : i / (double)(n - 1);
            if (upto > before) units += Un(upto) - Un(before);
        }
        double actual = units / window;
        int? daysLeft = deadline == null ? null : (int)(deadline.Value.Date - today).TotalDays + 1;
        double? needed = daysLeft == null ? null : daysLeft <= 0 ? remaining : remaining / daysLeft.Value;
        DateTime? est = remaining <= 1e-9 ? today : actual > 1e-9 ? today.AddDays(Math.Ceiling(remaining / actual - 1e-9) - 1) : null;
        string verdict; bool behind = false;
        if (remaining <= 1e-9) verdict = "완료";
        else if (deadline == null) verdict = "마감 없음";
        else if (daysLeft <= 0) { verdict = "마감 지남"; behind = true; }
        else if (actual >= needed * 0.95) verdict = "순조로움";
        else if (actual >= needed * 0.6) { verdict = "조금 늦음"; behind = true; }
        else { verdict = "많이 늦음"; behind = true; }
        return new PaceInfo(remaining, remPages, pages.Count, daysLeft, needed, actual, window, est, verdict, behind);
    }

    /// <summary>마감이 지났는데 끝나지 않은 것 (페이지·회차·작품)</summary>
    public static List<OverdueItem> Overdue(Plan plan, IList<string> pages, DateTime today, Func<string, string> pageLabel = null)
    {
        today = today.Date;
        pageLabel ??= p => p;
        var list = new List<OverdueItem>();
        foreach (var p in pages)
            if (plan.DueOf(p) is DateTime d && d < today && !plan.IsDone(p)) list.Add(new OverdueItem("page", pageLabel(p), d, (int)(today - d).TotalDays, p));
        foreach (var e in plan.Episodes)
            if (e.Due is DateTime d && d < today && e.Pages.Any(p => pages.Contains(p) && !plan.IsDone(p))) list.Add(new OverdueItem("episode", e.Name, d, (int)(today - d).TotalDays, null));
        if (plan.Deadline is DateTime dl && dl < today && pages.Any(p => !plan.IsDone(p))) list.Add(new OverdueItem("work", "작품 마감", dl, (int)(today - dl).TotalDays, null));
        return list.OrderByDescending(o => o.DaysLate).ToList();
    }

    /// <summary>앞으로 다가오는 마감 (오늘 포함, 끝나지 않은 것)</summary>
    public static List<(DateTime due, string label, string kind)> Upcoming(Plan plan, IList<string> pages, DateTime today, Func<string, string> pageLabel = null)
    {
        today = today.Date; pageLabel ??= p => p;
        var list = new List<(DateTime, string, string)>();
        foreach (var p in pages) if (plan.DueOf(p) is DateTime d && d >= today && !plan.IsDone(p)) list.Add((d, pageLabel(p), "page"));
        foreach (var e in plan.Episodes) if (e.Due is DateTime d && d >= today && e.Pages.Any(p => !plan.IsDone(p))) list.Add((d, e.Name, "episode"));
        if (plan.Deadline is DateTime dl && dl >= today && pages.Any(p => !plan.IsDone(p))) list.Add((dl, "작품 마감", "work"));
        return list.OrderBy(x => x.Item1).ToList();
    }

    /// <summary>모든 마감 (달력 표시용, 끝난 것도)</summary>
    public static List<(DateTime due, string label, string kind, bool done)> AllDues(Plan plan, IList<string> pages, Func<string, string> pageLabel = null)
    {
        pageLabel ??= p => p;
        var list = new List<(DateTime, string, string, bool)>();
        foreach (var p in pages) if (plan.DueOf(p) is DateTime d) list.Add((d, pageLabel(p), "page", plan.IsDone(p)));
        foreach (var e in plan.Episodes) if (e.Due is DateTime d) list.Add((d, e.Name, "episode", e.Pages.Count > 0 && e.Pages.All(plan.IsDone)));
        if (plan.Deadline is DateTime dl) list.Add((dl, "작품 마감", "work", pages.Count > 0 && pages.All(plan.IsDone)));
        return list;
    }
}

/// <summary>지금 작품(여러 페이지 작품 또는 한 장 문서)의 일정 — 읽고, 웹 판 값과 맞추고, 저장</summary>
public sealed class WorkSchedule
{
    public const string SinglePage = "_";
    public string Story;            // 여러 페이지 작품 이름 (null = 한 장 문서)
    public Document Doc;            // 한 장 문서
    public List<string> Pages = new();
    public Plan Plan;
    JsonObject info;                // story.json

    public static event Action Changed;
    public static void RaiseChanged() => Changed?.Invoke();

    public string Title => Story ?? Doc?.Name ?? "작품";
    /// <summary>모든 작품 색인 열쇠</summary>
    public string Key => Story != null ? "story:" + Story : "file:" + WorkLog.PageKey(Doc);
    public bool IsStory => Story != null;
    public JsonObject Info => info;

    public string PageLabel(string p)
    {
        if (!IsStory) return Doc?.Name ?? "이 그림";
        int i = Pages.IndexOf(p);
        return i >= 0 ? $"{i + 1}쪽" : Path.GetFileNameWithoutExtension(p);
    }

    /// <summary>페이지마다 작업 시간 (초): 작품이면 story.json time, 한 장이면 문서 workTime</summary>
    public double TimeOf(string p) => IsStory ? U.Num(info?["time"]?[p]) : Progress.WorkTime(Doc);

    public static WorkSchedule Current => App.Doc == null ? null : For(App.Doc);
    /// <summary>같은 작품을 파일에서 새로 읽기 (창이 열려 있는 동안 다른 곳에서 바뀐 값 반영)</summary>
    public static WorkSchedule ForPage(WorkSchedule old, string page) => old == null ? Current : old.IsStory ? ForStory(old.Story) : old.Doc != null ? For(old.Doc) : Current;

    public static WorkSchedule For(Document d)
    {
        string story = Story0(d);
        if (story != null) return ForStory(story);
        var ws = new WorkSchedule { Doc = d, Pages = new List<string> { SinglePage } };
        ws.Plan = new Plan(d.Extra["schedule"] is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject());
        return ws;
    }
    static string Story0(Document d) { try { return Extras.Story.StoryOf(d); } catch { return null; } }

    public static WorkSchedule ForStory(string story)
    {
        var info = Extras.Story.ReadInfo(story);
        var ws = new WorkSchedule { Story = story, info = info, Pages = Extras.Story.ListPages(story) };
        ws.Plan = new Plan(info["schedule"] is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject());
        ws.Reconcile(DateTime.Now);
        return ws;
    }

    /// <summary>웹 판(또는 예전 진행표)이 status·deadline 을 바꿨으면 그 값을 따름</summary>
    public void Reconcile(DateTime now)
    {
        if (info == null) return;
        var plan = Plan;
        foreach (var p in Pages)
        {
            string web = info["status"]?[p] is JsonValue v && v.TryGetValue(out string s) ? s : null;
            string ours = plan.StageOf(p);
            if (web == null) { if (ours != null) plan.SetStage(p, null, now); }
            else if (ours == null || plan.WebOf(ours) != web) { var id = plan.FromWeb(web); if (id != null) plan.SetStage(p, id, now); }
        }
        var dl = Plan.P(info["deadline"]);
        if (dl != plan.Deadline) plan.Deadline = dl;
    }

    /// <summary>저장: 작품 → story.json (schedule + 웹 판 status·deadline), 한 장 → doc.Extra["schedule"]. 색인도 갱신</summary>
    public void Save()
    {
        if (IsStory)
        {
            var fresh = Extras.Story.ReadInfo(Story);     // 그 사이 다른 곳(진행표·시간 기록)이 쓴 값은 살림
            fresh["schedule"] = Plan.Root.DeepClone();
            if (fresh["status"] is not JsonObject st) { st = new JsonObject(); fresh["status"] = st; }
            foreach (var p in Pages)
            {
                string w = Plan.WebOf(Plan.StageOf(p));
                if (w == null) st.Remove(p); else st[p] = w;
            }
            fresh["deadline"] = Plan.Deadline == null ? null : Plan.D(Plan.Deadline.Value);
            info = fresh;
            Extras.Story.WriteInfo(Story, fresh);
        }
        else if (Doc != null)
        {
            Doc.Extra["schedule"] = Plan.Root.DeepClone();
            Doc.Modified = true;
        }
        try { WorkIndex.UpdateWork(this); } catch { }
        Changed?.Invoke();
    }

    public List<(Stage stage, int count)> Counts() => Sched.Counts(Plan, Pages);
    public PaceInfo Pace(DateTime today) => Sched.Pace(Plan, Pages, today, NearestDeadline(today));
    public List<OverdueItem> Overdue(DateTime today) => Sched.Overdue(Plan, Pages, today, PageLabel);

    /// <summary>속도 계산에 쓰는 마감: 작품 마감, 없으면 가장 늦은 회차·페이지 마감</summary>
    public DateTime? NearestDeadline(DateTime today)
    {
        if (Plan.Deadline != null) return Plan.Deadline;
        var all = Plan.Episodes.Where(e => e.Due != null).Select(e => e.Due.Value).Concat(Pages.Select(Plan.DueOf).Where(d => d != null).Select(d => d.Value)).ToList();
        return all.Count == 0 ? null : all.Max();
    }
}
