// 작업 일정 화면: 오른쪽 패널 "일정" (요약 대시보드 + 작은 달력 + 지금 페이지 단계), 일정 창 (대시보드 · 달력 · 페이지 · 단계·회차).
// 단계는 페이지 패널 카드의 오른쪽 클릭 메뉴에서도 바꿀 수 있다 (PagesPanel.CardMenu).
using System.Text.Json.Nodes;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.Modules.Workflow;

public static class ScheduleUi
{
    /// <summary>오늘 (시험에서 날짜를 고정할 때 바꿈)</summary>
    public static Func<DateTime> Today = () => DateTime.Today;

    // ---- 바꾸기 명령 ------------------------------------------------------------------------
    public static void SetStage(WorkSchedule ws, string page, string id)
    {
        if (ws == null) return;
        ws.Plan.SetStage(page, id, DateTime.Now);
        ws.Save();
        var st = ws.Plan.StageById(id);
        App.Toast(st == null ? $"{ws.PageLabel(page)}: 단계 지움" : $"{ws.PageLabel(page)}: {st.Label}");
    }

    public static void AdvanceCurrent()
    {
        var ws = WorkSchedule.Current; if (ws == null) return;
        string p = CurrentPage(ws);
        if (p == null) { App.Toast("이 작품의 페이지가 아닙니다"); return; }
        var id = ws.Plan.Advance(p, DateTime.Now);
        ws.Save();
        App.Toast($"{ws.PageLabel(p)}: {ws.Plan.StageById(id)?.Label}");
    }

    public static string CurrentPage(WorkSchedule ws) => ws == null ? null : ws.IsStory ? (ws.Pages.Contains(Extras.Story.CurrentRel) ? Extras.Story.CurrentRel : null) : WorkSchedule.SinglePage;

    static Field DateField(string key, string label, DateTime? v) => new() { Key = key, Label = label, Type = "date", Value = v == null ? null : Plan.D(v.Value) };

    public static void EditDue(WorkSchedule ws, string page)
    {
        var v = Dlg.Form($"{ws.PageLabel(page)} 마감일", new[] { DateField("due", "마감일", ws.Plan.DueOf(page)), Field.Info("체크를 끄면 페이지 마감이 없어지고 회차·작품 마감을 따릅니다.") }, "저장");
        if (v == null) return;
        ws.Plan.SetDue(page, Plan.P(v["due"]));
        ws.Save();
    }

    public static void EditDeadline(WorkSchedule ws)
    {
        var v = Dlg.Form("작품 마감일", new[] { DateField("due", "마감일", ws.Plan.Deadline), Field.Info(ws.IsStory ? "웹 판 진행표의 마감일과 같은 값입니다." : "이 그림(.cpad)에 함께 저장됩니다.") }, "저장");
        if (v == null) return;
        ws.Plan.Deadline = Plan.P(v["due"]);
        ws.Save();
    }

    /// <summary>"1-8, 10" → 페이지 목록</summary>
    public static List<string> ParseRange(string text, IList<string> pages)
    {
        var res = new List<string>();
        foreach (var part0 in (text ?? "").Split(',', ' ', ';'))
        {
            var part = part0.Trim(); if (part.Length == 0) continue;
            var ab = part.Split('-', '~');
            if (int.TryParse(ab[0], out int a))
            {
                int b = ab.Length > 1 && int.TryParse(ab[1], out int bb) ? bb : a;
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) if (i >= 1 && i <= pages.Count && !res.Contains(pages[i - 1])) res.Add(pages[i - 1]);
            }
        }
        return res;
    }
    public static string RangeText(IList<string> sel, IList<string> pages)
    {
        var idx = sel.Select(p => pages.IndexOf(p) + 1).Where(i => i > 0).OrderBy(i => i).ToList();
        var parts = new List<string>();
        for (int i = 0; i < idx.Count;)
        {
            int j = i; while (j + 1 < idx.Count && idx[j + 1] == idx[j] + 1) j++;
            parts.Add(i == j ? idx[i].ToString() : $"{idx[i]}-{idx[j]}"); i = j + 1;
        }
        return string.Join(", ", parts);
    }

    public static void EditEpisodes(WorkSchedule ws)
    {
        var eps = ws.Plan.Episodes;
        if (eps.Count == 0) eps.Add(new Episode { Name = "1화", Pages = ws.Pages.ToList() });
        var fields = new List<Field> { Field.Info("회차마다 이름 · 마감일 · 페이지 범위(예: 1-8, 10). 이름을 비우면 그 회차를 지웁니다.") };
        for (int i = 0; i < eps.Count + 1; i++)
        {
            var e = i < eps.Count ? eps[i] : new Episode { Name = "" };
            fields.Add(Field.Text("n" + i, i < eps.Count ? $"회차 {i + 1}" : "새 회차", e.Name));
            fields.Add(DateField("d" + i, "  마감일", e.Due));
            fields.Add(Field.Text("p" + i, "  페이지", RangeText(e.Pages, ws.Pages)));
        }
        var v = Dlg.Form("회차(에피소드) 마감", fields, "저장", width: 520);
        if (v == null) return;
        var list = new List<Episode>();
        for (int i = 0; i < eps.Count + 1; i++)
        {
            string name = ((string)v["n" + i] ?? "").Trim();
            if (name.Length == 0) continue;
            list.Add(new Episode { Id = i < eps.Count ? eps[i].Id : null, Name = name, Due = Plan.P(v["d" + i]), Pages = ParseRange((string)v["p" + i], ws.Pages) });
        }
        ws.Plan.Episodes = list;
        ws.Save();
    }

    /// <summary>단계 목록 고치기: 한 줄에 "이름 #색" (순서대로, 마지막 줄 = 완료)</summary>
    public static void EditStages(WorkSchedule ws)
    {
        var cur = ws.Plan.Stages;
        string text = string.Join("\n", cur.Select(s => $"{s.Label} {s.Color}"));
        var v = Dlg.Form("작업 단계 바꾸기", new[]
        {
            Field.Area("lines", "단계 (한 줄에 하나: 이름 #색)", text),
            Field.Info("위에서 아래로 진행합니다. 마지막 단계가 \"완료\"입니다. 웹 판 진행표에는 가까운 단계(콘티·선화·채색·완료)로 보입니다."),
        }, "저장", new[] { ("기본값으로", "reset") });
        if (v == null) return;
        if ((string)v["__action"] == "reset") { ws.Plan.Root.Remove("stages"); ws.Save(); return; }
        var stages = ParseStages((string)v["lines"], cur);
        if (stages.Count < 2) { App.Toast("단계는 2개 이상이어야 합니다"); return; }
        ws.Plan.SetStages(stages);
        ws.Save();
    }

    /// <summary>"이름 #색" 줄들 → 단계 (이름이 같은 기존 단계는 id 를 그대로)</summary>
    public static List<Stage> ParseStages(string text, List<Stage> old)
    {
        var res = new List<Stage>();
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string[] palette = { "#8e5bd6", "#b07cc6", "#3d7be0", "#26a3a3", "#f08c2e", "#d4a72c", "#e5484d", "#2fa35b" };
        for (int i = 0; i < lines.Count; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"^(.*?)\s*(#[0-9a-fA-F]{6})?$");
            string label = m.Groups[1].Value.Trim(); if (label.Length == 0) continue;
            string color = m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : old.Find(s => s.Label == label)?.Color ?? palette[i % palette.Length];
            string id = old.Find(s => s.Label == label)?.Id ?? "s" + Convert.ToString(label.GetHashCode() & 0x7fffffff, 16);
            while (res.Any(s => s.Id == id)) id += "x";
            res.Add(new Stage(id, label, color, null));
        }
        return res.Select((s, i) => s with { Web = Plan.WebFor(i, res.Count) }).ToList();
    }

    /// <summary>페이지 하나의 단계 메뉴 (페이지 패널 카드·일정 표에서)</summary>
    public static void StageMenu(WorkSchedule ws, string page, Control at, Point p)
    {
        if (ws == null || page == null) return;
        var m = WebMenu.Style(new ContextMenuStrip());
        m.Items.Add(new ToolStripLabel($"{ws.PageLabel(page)} 단계") { ForeColor = Theme.Muted });
        string cur = ws.Plan.StageOf(page);
        foreach (var s in ws.Plan.Stages)
        {
            var it = new ToolStripMenuItem(s.Label) { Checked = s.Id == cur, Image = Swatch(Wf.C(s.Color)) };
            string id = s.Id;
            it.Click += (_, _) => SetStage(WorkSchedule.ForPage(ws, page), page, id);
            m.Items.Add(it);
        }
        m.Items.Add("단계 지우기", null, (_, _) => SetStage(WorkSchedule.ForPage(ws, page), page, null));
        m.Items.Add(new ToolStripSeparator());
        var due = ws.Plan.DueOf(page);
        m.Items.Add(due == null ? "마감일 정하기…" : $"마감일 바꾸기… ({due:M/d})", null, (_, _) => EditDue(WorkSchedule.ForPage(ws, page), page));
        m.Items.Add("일정 창 열기…", null, (_, _) => ScheduleWindow.Open("pages"));
        m.Show(at, p);
    }
    static Bitmap Swatch(Color c) { var b = new Bitmap(12, 12); using var g = Graphics.FromImage(b); using var br = new SolidBrush(c); g.FillRectangle(br, 1, 1, 10, 10); return b; }

    // ---- 대시보드 부품 ---------------------------------------------------------------------------
    /// <summary>단계별 쌓인 막대 + 이름·수</summary>
    public static Control StageBar(WorkSchedule ws)
    {
        var counts = ws.Counts();
        int n = ws.Pages.Count;
        int ChipsH(int w)
        {
            int x = 0, rows = 1;
            foreach (var (s, c) in counts) { int cw = Web.Measure($"{s?.Label ?? "미정"} {c}", Web.Tiny).Width + 22; if (x + cw > w && x > 0) { rows++; x = 0; } x += cw; }
            return rows * 18;
        }
        return new Wf.Painted(w => 16 + 6 + ChipsH(w), (g, r) =>
        {
            var bar = new Rectangle(r.X, r.Y, r.Width - 1, 14);
            Web.Box(g, bar, 4, Theme.Panel2);
            float x = bar.X;
            // 완료부터 (진행된 것이 왼쪽)
            foreach (var (s, c) in counts.Where(c => c.stage != null).Reverse())
            {
                float w = n > 0 ? c * bar.Width / (float)n : 0;
                if (w <= 0) continue;
                using var br = new SolidBrush(Wf.C(s.Color)); g.FillRectangle(br, x, bar.Y, w, bar.Height); x += w;
            }
            int cx = r.X, cy = r.Y + 22;
            foreach (var (s, c) in counts)
            {
                string t = $"{s?.Label ?? "미정"} {c}";
                int cw = Web.Measure(t, Web.Tiny).Width + 22;
                if (cx + cw > r.Right && cx > r.X) { cx = r.X; cy += 18; }
                using (var br = new SolidBrush(s == null ? Theme.Panel3 : Wf.C(s.Color))) g.FillEllipse(br, cx, cy + 4, 9, 9);
                Web.Text(g, t, Web.Tiny, new Rectangle(cx + 12, cy, cw - 12, 17), c > 0 ? Theme.Text : Theme.Muted);
                cx += cw;
            }
        });
    }

    /// <summary>남은 일수 · 필요 속도 · 실제 속도 카드</summary>
    public static Control PaceCard(WorkSchedule ws, DateTime today)
    {
        var pace = ws.Pace(today);
        var dl = ws.NearestDeadline(today);
        return new Wf.Painted(w => 92, (g, r) =>
        {
            var box = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            Color vc = pace.Verdict switch { "순조로움" or "완료" => Wf.C("#2fa35b"), "조금 늦음" => Wf.C("#e8a33d"), "마감 없음" => Theme.Muted, _ => Theme.Danger };
            Web.Box(g, box, 8, Theme.Panel2, pace.Behind ? vc : null, 1.5f);
            int cw = box.Width / 3;
            void Cell(int i, string big, string small, Color c)
            {
                var cr = new Rectangle(box.X + i * cw, box.Y + 8, cw, 30);
                Web.Text(g, big, Web.F(19, true), cr, c, Web.Center);
                Web.Text(g, small, Web.Tiny, new Rectangle(cr.X, cr.Bottom + 2, cw, 16), Theme.Muted, Web.Center);
            }
            string dday = pace.DaysLeft == null ? "—" : pace.DaysLeft > 0 ? $"D-{pace.DaysLeft - 1}" : $"D+{1 - pace.DaysLeft}";
            if (pace.DaysLeft == 1) dday = "D-day";
            Cell(0, dday, dl == null ? "마감 없음" : $"마감 {dl:M/d}", pace.DaysLeft is <= 0 ? Theme.Danger : Theme.Text);
            Cell(1, pace.NeededPerDay == null ? "—" : $"{pace.NeededPerDay:0.0}", "필요 쪽/일", Theme.Text);
            Cell(2, $"{pace.ActualPerDay:0.0}", $"실제 쪽/일 ({pace.WindowDays}일)", pace.Behind ? vc : Theme.Text);
            string est = pace.EstimatedFinish is DateTime ef ? $" · 이 속도면 {ef:M/d} 끝" : pace.RemainingUnits > 0 ? " · 최근 진행 없음" : "";
            Web.Text(g, $"{pace.Verdict} · 남은 일 {pace.RemainingUnits:0.#}쪽분{est}", Web.Small, new Rectangle(box.X + 8, box.Bottom - 26, box.Width - 16, 20), vc, Web.Center);
        });
    }

    /// <summary>최근 14일 작업 시간 막대</summary>
    public static Control RecentTime(WorkSchedule ws, DateTime today)
    {
        var days = WorkIndex.Days(k => k == ws.Key);
        double Sec(DateTime d) => days.TryGetValue(d, out var m) ? m.Values.Sum() : 0;
        var list = Enumerable.Range(0, 14).Select(i => today.AddDays(i - 13)).Select(d => (d, s: Sec(d))).ToList();
        double max = Math.Max(1800, list.Max(x => x.s));
        double week = list.Skip(7).Sum(x => x.s);
        return new Wf.Painted(w => 74, (g, r) =>
        {
            Web.Text(g, $"오늘 {U.FmtTime(list[^1].s)} · 최근 7일 {U.FmtTime(week)}", Web.Small, new Rectangle(r.X, r.Y, r.Width, 18), Theme.Text);
            int bw = (r.Width - 13 * 3) / 14, top = r.Y + 22, h = 36;
            for (int i = 0; i < 14; i++)
            {
                var (d, s) = list[i];
                int bh = (int)Math.Round(h * s / max);
                var br0 = new Rectangle(r.X + i * (bw + 3), top, bw, h);
                using (var bg = new SolidBrush(Theme.Panel2)) g.FillRectangle(bg, br0);
                if (bh > 0) using (var br = new SolidBrush(i == 13 ? Theme.Accent : Wf.C("#3d7be0"))) g.FillRectangle(br, br0.X, br0.Bottom - bh, bw, bh);
                if (i % 7 == 6 || i == 0) Web.Text(g, $"{d:M/d}", Web.F(9), new Rectangle(br0.X - 10, br0.Bottom + 1, bw + 20, 13), Theme.Muted, Web.Center);
            }
        });
    }

    /// <summary>대시보드 내용 (compact = 오른쪽 패널)</summary>
    public static List<Control> Dashboard(WorkSchedule ws, DateTime today, bool compact)
    {
        var list = new List<Control>();
        if (ws == null) { list.Add(Opt.Help("열린 그림이 없습니다.")); return list; }
        int n = ws.Pages.Count, done = ws.Pages.Count(ws.Plan.IsDone);
        list.Add(Opt.Title($"{ws.Title} — {(ws.IsStory ? $"{n}쪽 · " : "")}완료 {done}/{n} ({(n > 0 ? Math.Round(done * 100.0 / n) : 0)}%)"));
        list.Add(StageBar(ws));
        list.Add(PaceCard(ws, today));
        var over = ws.Overdue(today);
        foreach (var o in over.Take(compact ? 4 : 20))
            list.Add(new WebHelp($"⚠ {o.Label} 마감 {o.Due:M/d} — {o.DaysLate}일 지남") { ForeColor = Theme.Danger });
        if (over.Count > (compact ? 4 : 20)) list.Add(new WebHelp($"⚠ 그 밖에 {over.Count - (compact ? 4 : 20)}개 더 지남") { ForeColor = Theme.Danger });
        var up = Sched.Upcoming(ws.Plan, ws.Pages, today, ws.PageLabel);
        if (up.Count > 0) list.Add(Opt.Help("다가오는 마감: " + string.Join(" · ", up.Take(compact ? 3 : 6).Select(x => $"{x.label} {x.due:M/d}{((x.due - today).TotalDays is var dd && dd <= 2 ? $" (D-{dd})" : "")}"))));
        // 지금 페이지
        string cur = CurrentPage(ws);
        if (cur != null)
        {
            list.Add(Opt.Title(ws.IsStory ? $"지금 페이지 ({ws.PageLabel(cur)})" : "이 그림의 단계"));
            var items = new List<(string, string)> { ("", "미정") };
            items.AddRange(ws.Plan.Stages.Select(s => (s.Id, s.Label)));
            list.Add(Opt.Choice("단계", items.ToArray(), () => ws.Plan.StageOf(cur) ?? "", v => SetStage(WorkSchedule.ForPage(ws, cur), cur, v == "" ? null : v)));
            var due = ws.Plan.DueOf(cur);
            list.Add(Opt.Row(Opt.Button("다음 단계 ▶", AdvanceCurrent, true), Opt.Button(due == null ? "마감일…" : $"마감 {due:M/d}…", () => EditDue(WorkSchedule.ForPage(ws, cur), cur))));
        }
        list.Add(Opt.Title("작업 시간"));
        list.Add(RecentTime(ws, today));
        if (compact)
        {
            list.Add(Opt.Title("달력"));
            list.Add(new CalendarView(ws, today, compact: true));
            list.Add(Opt.Row(Opt.Button("일정 창 열기…", () => ScheduleWindow.Open("dash"), true), Opt.Button("작품 마감…", () => EditDeadline(WorkSchedule.ForPage(ws, null)))));
        }
        else
        {
            list.Add(Opt.Title("마감"));
            list.Add(Opt.Row(Opt.Button(ws.Plan.Deadline is DateTime dl ? $"작품 마감 {dl:yyyy-MM-dd}…" : "작품 마감 정하기…", () => EditDeadline(ws)), ws.IsStory ? Opt.Button("회차(에피소드) 마감…", () => EditEpisodes(ws)) : null, Opt.Button("단계 바꾸기…", () => EditStages(ws))));
            foreach (var e in ws.Plan.Episodes)
            {
                int ed = e.Pages.Count(ws.Plan.IsDone);
                list.Add(Opt.Help($"{e.Name}: {RangeText(e.Pages, ws.Pages)}쪽 · 완료 {ed}/{e.Pages.Count}{(e.Due is DateTime d ? $" · 마감 {d:yyyy-MM-dd}" : "")}"));
            }
        }
        return list;
    }
}

/// <summary>한 달 달력: 날마다 작업 시간(진할수록 많이)·마감 표시. 마감 지난 날은 빨강, 오늘은 주황 테두리</summary>
public sealed class CalendarView : Control, IWebBlock
{
    public int Year, Month;
    public bool AllWorks;
    public DateTime Today, Selected;
    readonly bool compact;
    WorkSchedule ws;
    Dictionary<DateTime, DayInfo> data = new();
    public event Action<DateTime> DayPicked;
    Rectangle prevR, nextR;

    public CalendarView(WorkSchedule ws, DateTime today, bool compact = false, bool allWorks = false)
    {
        this.ws = ws; Today = today.Date; Selected = Today; this.compact = compact; AllWorks = allWorks;
        Year = Today.Year; Month = Today.Month;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Margin = new Padding(0, 4, 0, 4); Font = Web.Body; Cursor = Cursors.Hand;
        Reload();
    }
    public Dictionary<DateTime, DayInfo> Data => data;
    public void SetWork(WorkSchedule w) { ws = w; Reload(); }
    public void Reload() { try { data = Calendar.Month(Year, Month, ws, AllWorks, Today); } catch { data = new(); } Invalidate(); }
    public void Step(int dm) { var d = new DateTime(Year, Month, 1).AddMonths(dm); Year = d.Year; Month = d.Month; Reload(); }

    int HeadH => 26;
    int WeekH => 18;
    int Rows => (int)Math.Ceiling(((int)new DateTime(Year, Month, 1).DayOfWeek + DateTime.DaysInMonth(Year, Month)) / 7.0);
    int CellH(int w) => compact ? Math.Max(30, (w - 6) / 7) : Math.Max(64, (w - 6) / 7 * 3 / 4);
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 280;
        return new Size(w, HeadH + WeekH + Rows * CellH(w) + 2);
    }
    Rectangle CellRect(int i) { int cw = (Width - 1) / 7, ch = CellH(Width); return new Rectangle(i % 7 * cw, HeadH + WeekH + i / 7 * ch, cw, ch); }
    int Offset => (int)new DateTime(Year, Month, 1).DayOfWeek;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Web.ParentBack(this)); Web.Smooth(g);
        prevR = new Rectangle(0, 0, 28, HeadH - 4); nextR = new Rectangle(Width - 28, 0, 28, HeadH - 4);
        Web.Box(g, prevR, 4, Theme.Panel2); Web.Box(g, nextR, 4, Theme.Panel2);
        Web.Text(g, "◀", Web.Small, prevR, Theme.Text, Web.Center); Web.Text(g, "▶", Web.Small, nextR, Theme.Text, Web.Center);
        double total = data.Values.Sum(d => d.Seconds);
        Web.Text(g, $"{Year}년 {Month}월{(total > 0 ? $" · {U.FmtTime(total)}" : "")}", compact ? Web.Small : Web.BodyBold, new Rectangle(30, 0, Width - 60, HeadH - 4), Theme.Text, Web.Center);
        string[] wd = { "일", "월", "화", "수", "목", "금", "토" };
        int cw = (Width - 1) / 7;
        for (int i = 0; i < 7; i++) Web.Text(g, wd[i], Web.Tiny, new Rectangle(i * cw, HeadH, cw, WeekH), i == 0 ? Wf.C("#e57373") : Theme.Muted, Web.Center);
        double max = Math.Max(3600, data.Values.Select(d => d.Seconds).DefaultIfEmpty(0).Max());
        int days = DateTime.DaysInMonth(Year, Month);
        for (int day = 1; day <= days; day++)
        {
            var date = new DateTime(Year, Month, day);
            var r = CellRect(Offset + day - 1); r.Inflate(-1, -1);
            data.TryGetValue(date, out var di);
            double k = di == null ? 0 : Math.Min(1, di.Seconds / max);
            var fill = k > 0 ? Web.Mix(Theme.Panel2, Wf.C("#3d7be0"), 0.15 + 0.6 * k) : Theme.Panel2;
            bool over = di?.Dues.Any(x => x.overdue) == true;
            bool hasDue = di?.Dues.Count > 0;
            Color? border = date == Today ? Theme.Accent : date == Selected && !compact ? Theme.Text : over ? Theme.Danger : null;
            Web.Box(g, r, 4, fill, border, date == Today ? 2 : 1.2f);
            var numR = new Rectangle(r.X + 3, r.Y + 1, r.Width - 6, 15);
            Web.Text(g, day.ToString(), Web.Tiny, numR, date.DayOfWeek == DayOfWeek.Sunday ? Wf.C("#e57373") : Theme.Text, compact ? Web.Center : Web.Left);
            if (di == null) continue;
            if (compact)
            {
                if (hasDue) { using var br = new SolidBrush(over ? Theme.Danger : di.Dues.All(x => x.done) ? Wf.C("#2fa35b") : Theme.Accent); g.FillEllipse(br, r.X + r.Width / 2 - 3, r.Bottom - 8, 6, 6); }
                continue;
            }
            if (di.Seconds > 0) Web.Text(g, Wf.Short(di.Seconds), Web.Tiny, new Rectangle(r.X + 3, r.Y + 1, r.Width - 6, 15), Theme.Text, Web.Right);
            int y = r.Y + 18;
            foreach (var (label, kind, done, od, _) in di.Dues)
            {
                if (y + 14 > r.Bottom) { Web.Text(g, "…", Web.Tiny, new Rectangle(r.X + 3, y - 4, r.Width - 6, 12), Theme.Muted); break; }
                var lr = new Rectangle(r.X + 2, y, r.Width - 4, 14);
                Web.Box(g, lr, 3, od ? Theme.Danger : done ? Wf.C("#2fa35b") : kind == "work" ? Theme.Accent : kind == "episode" ? Wf.C("#8e5bd6") : Wf.C("#5a5a66"));
                Web.Text(g, (done ? "✓ " : "") + label, Web.F(10), new Rectangle(lr.X + 3, lr.Y, lr.Width - 5, lr.Height), Color.White);
                y += 16;
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (prevR.Contains(e.Location)) { Step(-1); return; }
        if (nextR.Contains(e.Location)) { Step(1); return; }
        int days = DateTime.DaysInMonth(Year, Month);
        for (int day = 1; day <= days; day++)
            if (CellRect(Offset + day - 1).Contains(e.Location)) { Selected = new DateTime(Year, Month, day); Invalidate(); DayPicked?.Invoke(Selected); return; }
    }
}

/// <summary>페이지 표: 번호 · 단계(누르면 고르기) · 마감(누르면 바꾸기) · 작업 시간 · 완료한 날. 두 번 누르면 그 페이지 열기</summary>
public sealed class PageTable : Control, IWebBlock
{
    readonly WorkSchedule ws;
    readonly DateTime today;
    const int RowH = 28, HeadH = 22;
    int hot = -1;
    public PageTable(WorkSchedule ws, DateTime today)
    {
        this.ws = ws; this.today = today.Date;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Margin = new Padding(0, 4, 0, 4); Font = Web.Body; Cursor = Cursors.Hand;
    }
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 500;
        return new Size(w, HeadH + ws.Pages.Count * RowH + 2);
    }
    (Rectangle no, Rectangle stage, Rectangle due, Rectangle time, Rectangle done) Cols(int y)
    {
        int w = Width;
        int a = 56, b = Math.Max(96, w * 30 / 100), c = Math.Max(90, w * 22 / 100), d = Math.Max(70, w * 18 / 100);
        return (new Rectangle(0, y, a, RowH), new Rectangle(a, y, b, RowH), new Rectangle(a + b, y, c, RowH), new Rectangle(a + b + c, y, d, RowH), new Rectangle(a + b + c + d, y, Math.Max(10, w - a - b - c - d), RowH));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Web.ParentBack(this)); Web.Smooth(g);
        var hc = Cols(0);
        foreach (var (r, t) in new[] { (hc.no, "쪽"), (hc.stage, "단계"), (hc.due, "마감"), (hc.time, "작업 시간"), (hc.done, "완료한 날") })
            Web.Text(g, t, Web.Tiny, new Rectangle(r.X + 6, 0, r.Width - 6, HeadH), Theme.Accent);
        string cur = ScheduleUi.CurrentPage(ws);
        for (int i = 0; i < ws.Pages.Count; i++)
        {
            string p = ws.Pages[i];
            int y = HeadH + i * RowH;
            var c = Cols(y);
            if (i == hot || p == cur) Web.Box(g, new Rectangle(0, y, Width - 1, RowH - 1), 4, p == cur ? Web.ActiveRow : Theme.Panel2);
            Web.Text(g, ws.PageLabel(p) + (p == cur ? " ●" : ""), Web.Small, new Rectangle(c.no.X + 6, y, c.no.Width - 6, RowH), Theme.Text);
            var st = ws.Plan.StageById(ws.Plan.StageOf(p));
            var pill = new Rectangle(c.stage.X + 4, y + 4, c.stage.Width - 10, RowH - 8);
            Web.Box(g, pill, 9, st == null ? Theme.Panel3 : Wf.C(st.Color));
            Web.Text(g, st?.Label ?? "미정 ▾", Web.Small, pill, st == null ? Theme.Muted : Wf.On(Wf.C(st.Color)), Web.Center);
            var own = ws.Plan.DueOf(p); var eff = ws.Plan.EffectiveDue(p);
            bool late = eff != null && eff < today && !ws.Plan.IsDone(p);
            string dueT = own != null ? $"{own:yyyy-MM-dd}" : eff != null ? $"({eff:M/d})" : "—";
            Web.Text(g, dueT + (late ? " ⚠" : ""), Web.Small, new Rectangle(c.due.X + 6, y, c.due.Width - 6, RowH), late ? Theme.Danger : own != null ? Theme.Text : Theme.Muted);
            Web.Text(g, U.FmtTime(ws.TimeOf(p)), Web.Small, new Rectangle(c.time.X + 6, y, c.time.Width - 6, RowH), Theme.Muted);
            var dd = ws.Plan.Reached(p, ws.Plan.LastId);
            Web.Text(g, dd == null ? "" : $"{dd:M/d}", Web.Small, new Rectangle(c.done.X + 6, y, c.done.Width - 6, RowH), Theme.Muted);
        }
    }
    int Hit(Point p) { int i = (p.Y - HeadH) / RowH; return p.Y >= HeadH && i >= 0 && i < ws.Pages.Count ? i : -1; }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != hot) { hot = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = Hit(e.Location); if (i < 0) return;
        string p = ws.Pages[i];
        var c = Cols(HeadH + i * RowH);
        if (e.Button == MouseButtons.Right || c.stage.Contains(e.Location)) ScheduleUi.StageMenu(ws, p, this, e.Location);
        else if (c.due.Contains(e.Location)) ScheduleUi.EditDue(WorkSchedule.ForPage(ws, p), p);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = Hit(e.Location); if (i < 0 || !ws.IsStory) return;
        var c = Cols(HeadH + i * RowH);
        if (c.no.Contains(e.Location) || c.time.Contains(e.Location) || c.done.Contains(e.Location)) Extras.Story.GotoPage(ws.Pages[i]);
    }
}

/// <summary>오른쪽 패널 "일정"</summary>
public sealed class SchedulePanel : WfScroll
{
    bool pending;
    public SchedulePanel()
    {
        WorkSchedule.Changed += Soon; Extras.Story.Changed += Soon; App.DocChanged += Soon; ExtrasModule.Saved += Soon;
        VisibleChanged += (_, _) => { if (Visible) Soon(); };
        Render();
    }
    void Soon()
    {
        if (pending || IsDisposed) return;
        pending = true;
        void Go() { pending = false; if (!IsDisposed) Render(); }
        if (IsHandleCreated) BeginInvoke((Action)Go); else Go();
    }
    void Render()
    {
        var ws = WorkSchedule.Current;
        var items = ScheduleUi.Dashboard(ws, ScheduleUi.Today(), compact: true);
        if (ws != null && !ws.IsStory) items.Add(Opt.Help("여러 페이지 작품(만화 > 새 작품)이면 페이지마다 단계·마감을 관리합니다."));
        Fill(items);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { WorkSchedule.Changed -= Soon; Extras.Story.Changed -= Soon; App.DocChanged -= Soon; ExtrasModule.Saved -= Soon; }
        base.Dispose(disposing);
    }
}

/// <summary>일정 창: 대시보드 · 달력 · 페이지 · 단계·회차</summary>
public sealed class ScheduleWindow : WfForm
{
    static ScheduleWindow inst;
    string tab;
    bool allWorks;
    readonly WfScroll body = new() { Dock = DockStyle.Fill };
    readonly Panel top = new() { Dock = DockStyle.Top, Height = 48, BackColor = Theme.Panel, Padding = new Padding(10, 6, 10, 2) };
    CalendarView cal;
    int calY, calM;
    DateTime? picked;

    public static ScheduleWindow Open(string tab = "dash")
    {
        if (inst == null || inst.IsDisposed) { inst = new ScheduleWindow(tab); inst.Show(App.Main); }
        else { inst.tab = tab; inst.Render(); inst.Activate(); }
        return inst;
    }

    public ScheduleWindow(string tab = "dash") : base("작업 일정 — ComicPad", 860, 760)
    {
        this.tab = tab;
        var seg = Opt.Seg(new[] { ("dash", "대시보드"), ("cal", "달력"), ("pages", "페이지"), ("plan", "단계·회차") }, () => this.tab, v => { this.tab = v; Render(); });
        seg.Dock = DockStyle.Fill;
        top.Controls.Add(seg);
        Controls.Add(body); Controls.Add(top);
        WorkSchedule.Changed += Soon; Extras.Story.Changed += Soon; App.DocChanged += Soon;
        Render();
    }
    bool pending;
    void Soon()
    {
        if (pending || IsDisposed) return;
        pending = true;
        void Go() { pending = false; if (!IsDisposed) Render(); }
        if (IsHandleCreated) BeginInvoke((Action)Go); else Go();
    }
    public string Tab { get => tab; set { tab = value; Render(); } }

    public void Render()
    {
        var ws = WorkSchedule.Current;
        var today = ScheduleUi.Today();
        var items = new List<Control>();
        if (ws == null) { body.Fill(new[] { Opt.Help("열린 그림이 없습니다.") }); return; }
        Text = $"작업 일정: {ws.Title} — ComicPad";
        switch (tab)
        {
            case "cal":
                items.Add(Opt.Seg(new[] { (false, "이 작품"), (true, "모든 작품") }, () => allWorks, v => { allWorks = v; Render(); }));
                cal = new CalendarView(ws, today, compact: false, allWorks: allWorks);
                if (calY > 0) { cal.Year = calY; cal.Month = calM; cal.Reload(); }
                if (picked != null) cal.Selected = picked.Value;
                cal.DayPicked += d => { picked = d; calY = cal.Year; calM = cal.Month; Render(); };
                items.Add(cal);
                var sel = picked ?? today;
                cal.Data.TryGetValue(sel, out var di);
                items.Add(Opt.Title($"{sel:yyyy-MM-dd} ({"일월화수목금토"[(int)sel.DayOfWeek]})"));
                if (di == null || (di.Seconds <= 0 && di.Dues.Count == 0)) items.Add(Opt.Help("이 날은 기록이 없습니다."));
                else
                {
                    if (di.Seconds > 0) items.Add(Opt.Help($"작업 시간 {U.FmtTime(di.Seconds)}: " + string.Join(" · ", di.ByWork.OrderByDescending(x => x.Value).Select(x => $"{WorkIndex.TitleOf(x.Key)} {U.FmtTime(x.Value)}"))));
                    foreach (var d in di.Dues) items.Add(new WebHelp($"{(d.done ? "✓" : d.overdue ? "⚠" : "•")} 마감: {d.label}") { ForeColor = d.overdue ? Theme.Danger : Theme.Text });
                }
                items.Add(Opt.Help("칸 색이 진할수록 그날 작업 시간이 많습니다. 주황 테두리 = 오늘, 빨강 = 마감 지남."));
                items.Add(Opt.Help("작업 시간은 그리는 동안 자동으로 날짜별로 기록됩니다 (2분 넘게 쉬면 멈춤)."));
                break;
            case "pages":
                items.Add(Opt.Title($"{ws.Title} — 페이지 {ws.Pages.Count}개"));
                items.Add(new PageTable(ws, today));
                items.Add(Opt.Help("단계 칸을 누르면 단계 고르기, 마감 칸을 누르면 마감일. (괄호) 는 회차·작품 마감을 따르는 것. 번호를 두 번 누르면 그 페이지를 엽니다."));
                if (ws.IsStory) items.Add(Opt.Row(
                    Opt.Button("미정 페이지 모두 첫 단계로", () => { foreach (var p in ws.Pages) if (ws.Plan.StageOf(p) == null) ws.Plan.SetStage(p, ws.Plan.Stages[0].Id, DateTime.Now); ws.Save(); }),
                    Opt.Button("마감일 고르게 나누기…", () => SpreadDues(ws))));
                break;
            case "plan":
                items.Add(Opt.Title("작업 단계"));
                items.Add(new Wf.Painted(w => 30, (g, r) =>
                {
                    var st = ws.Plan.Stages; int x = r.X;
                    for (int i = 0; i < st.Count; i++)
                    {
                        string t = st[i].Label; int tw = Web.Measure(t, Web.Small).Width + 18;
                        var pr = new Rectangle(x, r.Y + 3, tw, 22); Web.Box(g, pr, 11, Wf.C(st[i].Color)); Web.Text(g, t, Web.Small, pr, Wf.On(Wf.C(st[i].Color)), Web.Center);
                        x += tw; if (i < st.Count - 1) { Web.Text(g, "→", Web.Small, new Rectangle(x, r.Y + 3, 18, 22), Theme.Muted, Web.Center); x += 18; }
                    }
                }));
                items.Add(Opt.Row(Opt.Button("단계 바꾸기…", () => ScheduleUi.EditStages(ws)), Opt.Button(ws.Plan.Deadline is DateTime dl ? $"작품 마감 {dl:yyyy-MM-dd}…" : "작품 마감 정하기…", () => ScheduleUi.EditDeadline(ws))));
                if (ws.IsStory)
                {
                    items.Add(Opt.Title("회차(에피소드)"));
                    var eps = ws.Plan.Episodes;
                    if (eps.Count == 0) items.Add(Opt.Help("회차를 나누지 않았습니다. 작품 전체가 한 회차입니다."));
                    foreach (var e in eps) items.Add(Opt.Help($"{e.Name}: {ScheduleUi.RangeText(e.Pages, ws.Pages)}쪽 · 완료 {e.Pages.Count(ws.Plan.IsDone)}/{e.Pages.Count}{(e.Due is DateTime d ? $" · 마감 {d:yyyy-MM-dd}" : " · 마감 없음")}"));
                    items.Add(Opt.Row(Opt.Button("회차 마감 고치기…", () => ScheduleUi.EditEpisodes(ws))));
                }
                items.Add(Opt.Help(ws.IsStory ? $"저장 위치: data/projects/{ws.Story}/story.json (웹 판 진행표와 같은 파일, 단계는 웹 판에서 콘티·선화·채색·완료로 보입니다)" : "저장 위치: 이 그림(.cpad) 안. 여러 페이지 작품이면 story.json 에 저장됩니다."));
                break;
            default:
                items.AddRange(ScheduleUi.Dashboard(ws, today, compact: false));
                break;
        }
        body.Fill(items);
    }

    /// <summary>남은 페이지 마감일을 오늘부터 작품 마감까지 고르게</summary>
    static void SpreadDues(WorkSchedule ws)
    {
        var today = ScheduleUi.Today();
        var dl = ws.NearestDeadline(today);
        if (dl == null) { App.Toast("작품 마감을 먼저 정하세요"); ScheduleUi.EditDeadline(ws); dl = ws.NearestDeadline(today); if (dl == null) return; }
        var left = ws.Pages.Where(p => !ws.Plan.IsDone(p)).ToList();
        if (left.Count == 0) return;
        if (!Dlg.Confirm($"끝나지 않은 {left.Count}쪽의 마감일을 오늘부터 {dl:yyyy-MM-dd} 까지 고르게 나눌까요? (이미 정한 페이지 마감은 바뀝니다)", "나누기")) return;
        foreach (var (p, d) in Spread(left, today, dl.Value)) ws.Plan.SetDue(p, d);
        ws.Save();
    }
    /// <summary>pages 를 from~to 에 고르게 (마지막 페이지 = to)</summary>
    public static List<(string page, DateTime due)> Spread(IList<string> pages, DateTime from, DateTime to)
    {
        int span = Math.Max(0, (int)(to.Date - from.Date).TotalDays);
        return pages.Select((p, i) => (p, from.Date.AddDays(Math.Round((i + 1) * (span + 1) / (double)pages.Count) - 1))).Select(x => (x.p, x.Item2 < from.Date ? from.Date : x.Item2)).ToList();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        WorkSchedule.Changed -= Soon; Extras.Story.Changed -= Soon; App.DocChanged -= Soon;
        if (inst == this) inst = null;
        base.OnFormClosed(e);
    }
}
