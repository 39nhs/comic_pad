// 하루 단위 작업 시간 + 모든 작품 색인 (달력·대시보드용).
//  - 작업 시간은 진행표(Extras/Progress)가 재는 값을 그대로 씀: 그 값이 늘어난 만큼을 오늘 날짜 칸에 더함 (2분 넘게 쉬면 안 세는 규칙도 같음)
//  - 문서마다: doc.Extra["workLog"] = { "YYYY-MM-DD": 초 }  (.cpad 에 저장, 웹 판은 모르는 값이라 무시)
//  - 모든 작품: data/workflow/index.json = { days: { 날짜: { 페이지열쇠: 초 } }, works: { 작품열쇠: { title, deadline, dues[], pages, done, updated } } }
//    페이지열쇠 = projects 기준 상대 경로("작품/p001.cpad"), projects 밖이면 전체 경로, 저장 안 한 그림은 "unsaved:이름"
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;

namespace ComicPadNative.Modules.Workflow;

public static class WorkLog
{
    public static string PageKey(Document d)
    {
        if (d == null) return "unsaved:";
        return Extras.Story.Rel(d.FilePath) ?? d.FilePath ?? "unsaved:" + d.Name;
    }
    /// <summary>페이지열쇠 → 작품열쇠 ("story:작품" 또는 "file:페이지열쇠")</summary>
    public static string WorkKeyOf(string pageKey)
    {
        if (pageKey != null && !pageKey.StartsWith("unsaved:") && !Path.IsPathRooted(pageKey) && pageKey.Contains('/')) return "story:" + pageKey.Split('/')[0];
        return "file:" + pageKey;
    }

    /// <summary>end 에 끝난 seconds 길이의 작업을 날짜 칸으로 나눔 (자정을 넘으면 두 날로)</summary>
    public static void Split(IDictionary<DateTime, double> buckets, DateTime end, double seconds)
    {
        if (seconds <= 0) return;
        DateTime t = end;
        double left = seconds;
        while (left > 1e-9)
        {
            DateTime dayStart = t.Date;
            double inDay = (t - dayStart).TotalSeconds;
            if (inDay <= 1e-9) { t = dayStart.AddTicks(-1); dayStart = t.Date; inDay = (t - dayStart).TotalSeconds; }
            double take = Math.Min(left, inDay);
            buckets[dayStart] = (buckets.TryGetValue(dayStart, out var v) ? v : 0) + take;
            left -= take; t = dayStart;
        }
    }
    public static Dictionary<DateTime, double> Buckets(IEnumerable<(DateTime end, double sec)> spans)
    {
        var b = new Dictionary<DateTime, double>();
        foreach (var (end, sec) in spans) Split(b, end, sec);
        return b;
    }

    // ---- 기록 --------------------------------------------------------------------------------
    static Document lastDoc; static double lastVal;
    static System.Windows.Forms.Timer flushTimer;

    public static void Init()
    {
        // Extras 의 진행표가 먼저 시간을 더한 뒤 (모듈 순서: Extras → Workflow) 늘어난 만큼을 날짜 칸에
        App.History.Changed += Tick;
        App.DocChanged += () => { WorkIndex.Flush(); lastDoc = App.Doc; lastVal = Progress.WorkTime(App.Doc); };
        ExtrasModule.Saved += () => { WorkIndex.Flush(); try { var ws = WorkSchedule.Current; if (ws != null) WorkIndex.UpdateWork(ws); } catch { } };
        flushTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        flushTimer.Tick += (_, _) => WorkIndex.Flush();
        flushTimer.Start();
        Application.ApplicationExit += (_, _) => WorkIndex.Flush();
    }

    static void Tick()
    {
        var d = App.Doc; if (d == null) return;
        double cur = Progress.WorkTime(d);
        if (d != lastDoc) { lastDoc = d; lastVal = cur; return; }
        double delta = cur - lastVal;
        lastVal = cur;
        if (delta > 0 && delta < 600) Record(d, DateTime.Now, delta);
    }

    /// <summary>문서에 seconds 만큼 작업했음을 기록 (end 시각 기준으로 날짜 칸에)</summary>
    public static void Record(Document d, DateTime end, double seconds)
    {
        if (d == null || seconds <= 0) return;
        var b = new Dictionary<DateTime, double>();
        Split(b, end, seconds);
        if (d.Extra["workLog"] is not JsonObject log) { log = new JsonObject(); d.Extra["workLog"] = log; }
        foreach (var (day, sec) in b)
        {
            string k = Plan.D(day);
            log[k] = Math.Round(U.Num(log[k]) + sec, 1);
        }
        WorkIndex.AddPending(PageKey(d), b);
    }

    /// <summary>이 문서의 날짜별 작업 시간 (초)</summary>
    public static Dictionary<DateTime, double> DocDays(Document d)
    {
        var res = new Dictionary<DateTime, double>();
        if (d?.Extra["workLog"] is JsonObject log)
            foreach (var (k, v) in log) if (Plan.P(JsonValue.Create(k)) is DateTime day) res[day] = U.Num(v);
        return res;
    }
}

/// <summary>data/workflow/index.json (모든 작품 달력)</summary>
public static class WorkIndex
{
    public static string Dir { get { var p = Path.Combine(App.DataDir, "workflow"); Directory.CreateDirectory(p); return p; } }
    public static string PathOf => Path.Combine(Dir, "index.json");
    static readonly Dictionary<string, Dictionary<DateTime, double>> pending = new();
    static readonly object gate = new();

    public static JsonObject Read()
    {
        try { if (File.Exists(PathOf)) return JsonNode.Parse(File.ReadAllText(PathOf)) as JsonObject ?? new JsonObject(); } catch { }
        return new JsonObject();
    }
    static void Write(JsonObject o)
    {
        string tmp = PathOf + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString(U.Indent1));
        File.Move(tmp, PathOf, true);
    }
    static JsonObject Obj(JsonObject p, string k) { if (p[k] is not JsonObject o) { o = new JsonObject(); p[k] = o; } return o; }

    public static void AddPending(string pageKey, Dictionary<DateTime, double> b)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(pageKey, out var m)) pending[pageKey] = m = new();
            foreach (var (d, s) in b) m[d] = (m.TryGetValue(d, out var v) ? v : 0) + s;
        }
    }

    /// <summary>모아 둔 시간을 파일에 (1분마다·저장·문서 바꿀 때)</summary>
    public static void Flush()
    {
        lock (gate)
        {
            if (pending.Count == 0) return;
            try
            {
                var idx = Read();
                var days = Obj(idx, "days");
                foreach (var (key, m) in pending)
                    foreach (var (day, sec) in m)
                    {
                        var dd = Obj(days, Plan.D(day));
                        dd[key] = Math.Round(U.Num(dd[key]) + sec, 1);
                    }
                Write(idx);
                pending.Clear();
            }
            catch { }
        }
    }

    /// <summary>날짜별 · 페이지열쇠별 시간 (파일 + 아직 안 쓴 것). filter = 작품열쇠 고르기 (null = 모두)</summary>
    public static Dictionary<DateTime, Dictionary<string, double>> Days(Func<string, bool> workFilter = null)
    {
        var res = new Dictionary<DateTime, Dictionary<string, double>>();
        void Add(DateTime day, string key, double sec)
        {
            if (workFilter != null && !workFilter(WorkLog.WorkKeyOf(key))) return;
            if (!res.TryGetValue(day, out var m)) res[day] = m = new();
            m[key] = (m.TryGetValue(key, out var v) ? v : 0) + sec;
        }
        if (Read()["days"] is JsonObject days)
            foreach (var (k, v) in days)
                if (Plan.P(JsonValue.Create(k)) is DateTime day && v is JsonObject o)
                    foreach (var (pk, s) in o) Add(day, pk, U.Num(s));
        lock (gate) foreach (var (key, m) in pending) foreach (var (day, sec) in m) Add(day, key, sec);
        return res;
    }

    /// <summary>작품 하나의 마감·진행 요약을 색인에 (달력의 "모든 작품" 보기)</summary>
    public static void UpdateWork(WorkSchedule ws)
    {
        lock (gate)
        {
            var idx = Read();
            var works = Obj(idx, "works");
            var plan = ws.Plan;
            var dues = new JsonArray(Sched.AllDues(plan, ws.Pages, ws.PageLabel).Select(x => (JsonNode)new JsonObject { ["date"] = Plan.D(x.due), ["label"] = x.label, ["kind"] = x.kind, ["done"] = x.done }).ToArray());
            works[ws.Key] = new JsonObject
            {
                ["title"] = ws.Title, ["story"] = ws.Story, ["file"] = ws.IsStory ? null : ws.Doc?.FilePath,
                ["deadline"] = plan.Deadline == null ? null : Plan.D(plan.Deadline.Value),
                ["dues"] = dues, ["pages"] = ws.Pages.Count, ["done"] = ws.Pages.Count(plan.IsDone), ["updated"] = Plan.D(DateTime.Now),
            };
            Write(idx);
        }
    }

    public static List<(string key, string title, DateTime due, string label, string kind, bool done)> AllDues()
    {
        var list = new List<(string, string, DateTime, string, string, bool)>();
        if (Read()["works"] is JsonObject works)
            foreach (var (k, v) in works)
                if (v?["dues"] is JsonArray a)
                    foreach (var o in a.OfType<JsonObject>())
                        if (Plan.P(o["date"]) is DateTime d) list.Add((k, (string)v["title"] ?? k, d, (string)o["label"] ?? "", (string)o["kind"] ?? "", (bool?)o["done"] ?? false));
        return list;
    }

    public static string TitleOf(string workKey)
    {
        var t = (string)Read()["works"]?[workKey]?["title"];
        if (t != null) return t;
        return workKey.StartsWith("story:") ? workKey[6..] : Path.GetFileNameWithoutExtension(workKey.StartsWith("file:") ? workKey[5..] : workKey);
    }
}

/// <summary>달력 칸 하나</summary>
public sealed class DayInfo
{
    public DateTime Date;
    public double Seconds;
    public readonly Dictionary<string, double> ByWork = new();      // 작품열쇠 → 초
    public readonly List<(string label, string kind, bool done, bool overdue, string work)> Dues = new();
}

public static class Calendar
{
    /// <summary>한 달 칸 (그 달 날짜만). allWorks = 모든 작품, 아니면 ws 작품만</summary>
    public static Dictionary<DateTime, DayInfo> Month(int year, int month, WorkSchedule ws, bool allWorks, DateTime today,
        Dictionary<DateTime, Dictionary<string, double>> days = null, List<(string key, string title, DateTime due, string label, string kind, bool done)> otherDues = null)
    {
        today = today.Date;
        var res = new Dictionary<DateTime, DayInfo>();
        var first = new DateTime(year, month, 1);
        for (var d = first; d.Month == month; d = d.AddDays(1)) res[d] = new DayInfo { Date = d };
        string myKey = ws?.Key;
        days ??= WorkIndex.Days(allWorks ? null : k => k == myKey);
        foreach (var (day, m) in days)
        {
            if (!res.TryGetValue(day.Date, out var di)) continue;
            foreach (var (pk, s) in m)
            {
                string wk = WorkLog.WorkKeyOf(pk);
                if (!allWorks && wk != myKey) continue;
                di.Seconds += s;
                di.ByWork[wk] = (di.ByWork.TryGetValue(wk, out var v) ? v : 0) + s;
            }
        }
        if (ws != null)
            foreach (var (due, label, kind, done) in Sched.AllDues(ws.Plan, ws.Pages, ws.PageLabel))
                if (res.TryGetValue(due, out var di)) di.Dues.Add((label, kind, done, !done && due < today, ws.Key));
        if (allWorks)
        {
            otherDues ??= WorkIndex.AllDues();
            foreach (var (key, title, due, label, kind, done) in otherDues)
            {
                if (key == myKey) continue;
                if (res.TryGetValue(due, out var di)) di.Dues.Add(($"{title}: {label}", kind, done, !done && due < today, key));
            }
        }
        return res;
    }
}
