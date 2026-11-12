// 진행표 (웹 판 ui/progress.js): 페이지마다 단계(콘티→선화→채색→완료)·작업 시간·마감일.
// 작업 시간은 그리는 동안 자동으로 잰다 (doc.Extra["workTime"], 초) — 2분 넘게 손을 놓으면 쉬는 시간으로 보고 세지 않음.
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public static class Progress
{
    public static readonly (string id, string label)[] Stages = { ("", "-"), ("conti", "콘티"), ("line", "선화"), ("color", "채색"), ("done", "완료") };
    static readonly Dictionary<string, Color> Colors = new()
    {
        ["conti"] = ColorTranslator.FromHtml("#8e5bd6"), ["line"] = ColorTranslator.FromHtml("#3d7be0"), ["color"] = ColorTranslator.FromHtml("#f08c2e"), ["done"] = ColorTranslator.FromHtml("#2fa35b"),
    };
    const double Idle = 120;
    static double last;
    static double acc; static Document accDoc;
    static ToolStripStatusLabel status;

    public static double WorkTime(Document d)
    {
        if (d == null) return 0;
        if (d == accDoc) return acc;
        return U.Num(d.Extra["workTime"]);
    }

    public static void Init()
    {
        App.History.Changed += Tick;
        App.DocChanged += () => { last = 0; accDoc = App.Doc; acc = WorkTime0(App.Doc); Sync(); };
    }
    static double WorkTime0(Document d) { return U.Num(d?.Extra["workTime"]); }

    static void Tick()
    {
        var d = App.Doc; if (d == null) return;
        if (accDoc != d) { accDoc = d; acc = WorkTime0(d); }
        double now = Environment.TickCount64 / 1000.0;
        if (last > 0 && now - last < Idle) { acc += now - last; d.Extra["workTime"] = (long)Math.Round(acc); }   // 웹 판처럼 초 단위 정수로 저장
        last = now;
        Sync();
    }

    /// <summary>상태 줄에 작업 시간 표시 (누르면 진행표)</summary>
    public static void AttachStatus()
    {
        var strip = U.All(App.Main).OfType<StatusStrip>().FirstOrDefault();
        if (strip == null || status != null) return;
        status = new ToolStripStatusLabel { ToolTipText = "이 그림에 들인 작업 시간 (2분 넘게 쉬면 멈춤)", IsLink = false, Margin = new Padding(8, 3, 8, 2) };
        status.Click += (_, _) => Dialog();
        strip.Items.Add(status);
        Sync();
    }
    static void Sync() { if (status != null) status.Text = "⏱ " + U.FmtTime(WorkTime(App.Doc)); }

    /// <summary>저장한 뒤: 작품 정보에 이 쪽의 작업 시간 기록</summary>
    public static void OnSaved(Document d)
    {
        string rel = Story.Rel(d?.FilePath), story = Story.StoryOfRel(rel);
        if (story == null) return;
        var info = Story.ReadInfo(story);
        if (info["time"] is not JsonObject t) { t = new JsonObject(); info["time"] = t; }
        t[rel] = (long)Math.Round(WorkTime(d));
        Story.WriteInfo(story, info);
    }

    public static void Dialog()
    {
        var doc = App.Doc;
        string story = Story.StoryOf(doc);
        if (story == null)
        {
            var help = Opt.Panel(Opt.Title("진행표"), Opt.Help($"이 그림의 작업 시간: {U.FmtTime(WorkTime(doc))}"),
                Opt.Help("여러 페이지 작품(만화 메뉴 > 새 작품)을 만들면 페이지마다 단계·시간·마감일을 표로 관리할 수 있습니다."));
            using var f0 = Dlg.Modal("진행표", help, 420, 220); f0.ShowDialog(App.Main);
            return;
        }
        var pages = Story.ListPages(story);
        var info = Story.ReadInfo(story);
        if (info["status"] is not JsonObject stObj) { stObj = new JsonObject(); info["status"] = stObj; }
        if (info["time"] is not JsonObject tmObj) { tmObj = new JsonObject(); info["time"] = tmObj; }
        string cur = Story.CurrentRel;
        if (cur != null) tmObj[cur] = (long)Math.Round(WorkTime(doc));
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(8) };
        using var f = Dlg.Modal("진행표", body, 620, 640);
        string open = null;
        void Save() { Story.WriteInfo(story, info); Render(); }
        double T(string p) { return U.Num(tmObj[p]); }
        string S(string p) => (string)stObj[p] ?? "";
        void Render()
        {
            body.SuspendLayout();
            foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
            int n = pages.Count;
            int Count(string k) => pages.Count(p => S(p) == k);
            int done = Count("done");
            double total = pages.Sum(T), doneTime = pages.Where(p => S(p) == "done").Sum(T), per = done > 0 ? doneTime / done : 0;
            body.Controls.Add(new Label { Text = $"{story} · {n}쪽 · 완료 {done}쪽 ({(n > 0 ? Math.Round(done * 100.0 / n) : 0)}%)", AutoSize = true, Font = new Font("Malgun Gothic", 10f, FontStyle.Bold) });
            var bar = new Panel { Width = 560, Height = 14, BackColor = Opt.Bg2, Margin = new Padding(0, 4, 0, 6) };
            bar.Paint += (_, e) =>
            {
                float x = 0;
                foreach (var k in new[] { "done", "color", "line", "conti" })
                {
                    float w = n > 0 ? Count(k) * bar.Width / (float)n : 0;
                    using var br = new SolidBrush(Colors[k]); e.Graphics.FillRectangle(br, x, 0, w, bar.Height); x += w;
                }
            };
            body.Controls.Add(bar);
            string dday = "";
            if ((string)info["deadline"] is string dl && DateTime.TryParse(dl, out var dd))
            {
                int days = (int)Math.Ceiling((dd.Date.AddDays(1).AddSeconds(-1) - DateTime.Now).TotalDays);
                dday = days >= 0 ? $"마감까지 {days}일" : $"마감 {-days}일 지남";
                if (days > 0 && n - done > 0) dday += $" · 하루 {(n - done) / (double)days:0.0}쪽씩";
            }
            var picker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", ShowCheckBox = true, Width = 140 };
            if ((string)info["deadline"] is string d0 && DateTime.TryParse(d0, out var dv)) { picker.Value = dv; picker.Checked = true; } else picker.Checked = false;
            picker.CloseUp += (_, _) => { info["deadline"] = picker.Checked ? picker.Value.ToString("yyyy-MM-dd") : null; Save(); };
            picker.Leave += (_, _) => { string nv = picker.Checked ? picker.Value.ToString("yyyy-MM-dd") : null; if (nv != (string)info["deadline"]) { info["deadline"] = nv; Save(); } };
            body.Controls.Add(Opt.Row(new Label { Text = "마감일 ", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }, picker, new Label { Text = " " + dday, AutoSize = true, Margin = new Padding(0, 6, 0, 0) }));
            body.Controls.Add(Opt.Help($"작업 시간 합계 {U.FmtTime(total)}{(per > 0 ? $" · 완료한 쪽 평균 {U.FmtTime(per)} → 남은 쪽 예상 {U.FmtTime(per * (n - done))}" : "")}"));
            var table = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, BackColor = Opt.Bg, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
            foreach (var hdr in new[] { "쪽", "단계", "작업 시간", "" }) table.Controls.Add(new Label { Text = hdr, AutoSize = true, ForeColor = Opt.Accent });
            for (int i = 0; i < pages.Count; i++)
            {
                string p = pages[i];
                table.Controls.Add(new Label { Text = $"{i + 1}{(p == cur ? " (지금)" : "")}", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
                foreach (var (_, t) in Stages) cb.Items.Add(t);
                cb.SelectedIndex = Math.Max(0, Array.FindIndex(Stages, s => s.id == S(p)));
                cb.SelectedIndexChanged += (_, _) => { string v = Stages[cb.SelectedIndex].id; if (v == "") stObj.Remove(p); else stObj[p] = v; Save(); };
                table.Controls.Add(cb);
                table.Controls.Add(new Label { Text = U.FmtTime(T(p)), AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
                table.Controls.Add(p == cur ? new Label { AutoSize = true } : Opt.Button("열기", () => { open = p; f.Close(); }));
            }
            body.Controls.Add(table);
            body.Controls.Add(Opt.Row(
                Opt.Button("빈 칸 모두 \"콘티\"로", () => { foreach (var p in pages) if (S(p) == "") stObj[p] = "conti"; Save(); }),
                cur != null ? Opt.Button("지금 쪽 완료", () => { stObj[cur] = "done"; Save(); }) : null));
            body.ResumeLayout();
        }
        Render();
        f.ShowDialog(App.Main);
        if (open != null) Story.GotoPage(open);
    }
}
