// 번역 표 창: 작품의 대사를 읽는 순서로 표로 보고 "번역" 칸을 바로 고쳐 넣기 (표 가져오기와 같은 길).
// "문제 있는 줄만" 을 켜면 번역 빠짐 검사 (번역 없음 / 원문과 같음 / 한글이 남음). 줄을 두 번 누르면 그 페이지·대사로.
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Lettering;

public sealed class LetterGrid : Form
{
    static LetterGrid open;
    public static void Open(bool problemsOnly)
    {
        if (App.Doc == null) return;
        if (open != null && !open.IsDisposed) { open.problems.Checked = problemsOnly; open.Reload(); open.Activate(); return; }
        open = new LetterGrid(problemsOnly);
        open.Show(App.Main);
    }

    /// <summary>시험용: 창을 화면 밖에 띄워 그대로 그림으로</summary>
    internal static Bitmap WindowShot(bool problemsOnly)
    {
        using var f = new LetterGrid(problemsOnly) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
        f.Show(); Application.DoEvents();
        var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        f.Close();
        return bmp;
    }

    readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        SelectionMode = DataGridViewSelectionMode.CellSelect, BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
    };
    readonly ComboBox lang = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDown };
    readonly CheckBox problems = new() { Text = "문제 있는 줄만 (번역 빠짐 검사)", AutoSize = true, ForeColor = Opt.Fg };
    readonly CheckBox whole = new() { Text = "작품 전체", AutoSize = true, ForeColor = Opt.Fg, Checked = true };
    readonly Label info = new() { AutoSize = true, ForeColor = Opt.Muted, Margin = new Padding(8, 7, 0, 0) };
    List<LetterRow> rows = new();
    List<WorkPage> pages;
    readonly Dictionary<int, string> edited = new();

    LetterGrid(bool problemsOnly)
    {
        Text = "번역 표"; BackColor = Opt.Bg; ForeColor = Opt.Fg; Font = new Font("Malgun Gothic", 9.5f); Width = 1100; Height = 760; StartPosition = FormStartPosition.CenterParent;
        problems.Checked = problemsOnly;
        whole.Visible = Work.StoryOf(App.Doc) != null;
        foreach (var c in I18n.LangsIn(new[] { App.Doc }).Concat(I18n.Common.Select(x => x.code)).Distinct()) lang.Items.Add(c);
        lang.Text = I18n.LangOf(App.Doc) ?? App.Get("letteringLang", "en");
        grid.BackgroundColor = Opt.Bg; grid.GridColor = Color.FromArgb(60, 60, 66);
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Opt.Bg2, ForeColor = Opt.Fg, SelectionBackColor = Color.FromArgb(70, 90, 130), SelectionForeColor = Color.White, WrapMode = DataGridViewTriState.True };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Opt.Bg, ForeColor = Opt.Muted, Font = new Font("Malgun Gothic", 9f, FontStyle.Bold) };
        void Col(string name, string head, int w, bool ro = true, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = head, Width = w, ReadOnly = ro, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (fill) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.FillWeight = 50; }
            grid.Columns.Add(c);
        }
        Col("page", "쪽", 40); Col("order", "순서", 44); Col("kind", "종류", 64); Col("shape", "말풍선", 56);
        Col("orig", "원문", 300, true, true); Col("tr", "번역", 300, false, true); Col("why", "확인", 110);
        grid.CellEndEdit += (_, e) => { if (e.ColumnIndex == grid.Columns["tr"].Index && e.RowIndex >= 0) { edited[(int)grid.Rows[e.RowIndex].Tag] = (grid.Rows[e.RowIndex].Cells["tr"].Value as string ?? "").Replace("\r\n", "\n"); UpdInfo(); } };
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != grid.Columns["tr"].Index) Go(rows[(int)grid.Rows[e.RowIndex].Tag]); };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Opt.Bg, Padding = new Padding(8, 6, 8, 6) };
        top.Controls.Add(new Label { Text = "번역 언어", AutoSize = true, ForeColor = Opt.Muted, Margin = new Padding(0, 7, 4, 0) });
        top.Controls.Add(lang); top.Controls.Add(problems); top.Controls.Add(whole);
        top.Controls.Add(Opt.Button("새로 읽기", Reload));
        top.Controls.Add(Opt.Button("번역 넣기 (고친 칸)", Apply, true));
        top.Controls.Add(Opt.Button("고른 줄로 가기", () => { if (grid.CurrentCell != null) Go(rows[(int)grid.Rows[grid.CurrentCell.RowIndex].Tag]); }));
        top.Controls.Add(info);
        lang.SelectedIndexChanged += (_, _) => Reload(); lang.Leave += (_, _) => Reload();
        problems.CheckedChanged += (_, _) => Fill(); whole.CheckedChanged += (_, _) => Reload();
        Controls.Add(grid); Controls.Add(top);
        Shown += (_, _) => Reload();
    }

    string Lang => (lang.Text ?? "").Trim();

    void Reload()
    {
        if (App.Doc == null) return;
        if (edited.Count > 0 && !UI.Dialogs.Modals.Confirm("고친 번역 칸을 아직 넣지 않았습니다. 버리고 다시 읽을까요?", "버리기")) return;
        edited.Clear();
        pages = Work.Open(App.Doc, whole.Visible && whole.Checked);
        rows = Work.Rows(pages, App.Get("letteringRtl", false));
        Fill();
    }

    string Why(LetterRow r)
    {
        string l = Lang; if (l.Length == 0 || r.Kind == "메모" || r.Original.Trim().Length == 0) return "";
        string t = edited.TryGetValue(rows.IndexOf(r), out var e) ? e : r.Tr(l);
        if (t.Trim().Length == 0) return "번역 없음";
        if (System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim() == System.Text.RegularExpressions.Regex.Replace(r.Original, @"\s+", " ").Trim()) return "원문과 같음";
        if (l != "ko" && t.Any(c => c >= 0xAC00 && c <= 0xD7A3)) return "한글이 남아 있음";
        return "";
    }

    void Fill()
    {
        grid.SuspendLayout();
        grid.Rows.Clear();
        grid.Columns["tr"].HeaderText = Lang.Length > 0 ? $"번역 ({Lang})" : "번역";
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; string why = Why(r);
            if (problems.Checked && why.Length == 0) continue;
            string tr = edited.TryGetValue(i, out var e) ? e : Lang.Length > 0 ? r.Tr(Lang) : "";
            int k = grid.Rows.Add(r.Page.No, r.Order, r.Kind, r.Shape, r.Original, tr, why);
            grid.Rows[k].Tag = i;
            if (why.Length > 0) grid.Rows[k].Cells["why"].Style.ForeColor = Color.FromArgb(240, 120, 110);
        }
        grid.ResumeLayout();
        UpdInfo();
    }

    void UpdInfo()
    {
        int bad = rows.Count(r => Why(r).Length > 0);
        info.Text = $"대사 {rows.Count}줄 · 확인할 줄 {bad}" + (edited.Count > 0 ? $" · 고친 칸 {edited.Count}" : "");
    }

    void Apply()
    {
        grid.EndEdit();
        if (Lang.Length == 0) { App.Toast("번역 언어를 정하세요"); return; }
        if (edited.Count == 0) { App.Toast("고친 번역 칸이 없습니다"); return; }
        var recs = edited.Select(kv => new Dictionary<string, string> { ["id"] = rows[kv.Key].L.Id, ["page"] = rows[kv.Key].Page.No.ToString(), ["translation"] = kv.Value }).ToList();
        // 지운 칸 = 번역 지우기
        var cleared = edited.Where(kv => kv.Value.Trim().Length == 0).Select(kv => rows[kv.Key]).ToList();
        var r = LetterTable.Import(pages, recs.Where(x => x["translation"].Trim().Length > 0).ToList(), Lang, new LetterTable.ImportOpts { FitOpts = new AutoFit.Opts { MinCss = App.Get("fitMinCss", 11.0) } });
        if (cleared.Count > 0)
        {
            var plan = new Dictionary<WorkPage, Dictionary<Layer, System.Text.Json.Nodes.JsonObject>>();
            foreach (var c in cleared)
            {
                var nm = J.Clone(c.L.Meta);
                if (nm["i18n"] is System.Text.Json.Nodes.JsonObject all) { all.Remove(Lang); if (all.Count == 0) nm.Remove("i18n"); Work.PlanOf(plan, c.Page)[c.L] = nm; }
            }
            Work.Apply(plan, "번역 지우기");
        }
        edited.Clear();
        App.InvalidateAll(); App.NotifyLayers();
        App.Toast($"번역 {r.Updated + cleared.Count}줄을 넣었습니다{(r.Overflow > 0 ? $" (말풍선을 넘친 것 {r.Overflow}개)" : "")}");
        Reload();
    }

    void Go(LetterRow r)
    {
        if (!r.Page.Current)
        {
            if (r.Page.Rel == null || !Story.GotoPage(r.Page.Rel)) return;
            edited.Clear(); Reload();
        }
        var l = App.Doc?.ById(r.L.Id); if (l == null) return;
        App.Doc.ActiveId = l.Id; App.NotifyLayers();
        var b = Work.BoxOf(l);
        Manuscript.GoTo(b.x, b.y, Math.Max(40, b.w), Math.Max(40, b.h));
    }
}
