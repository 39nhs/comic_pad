// 여러 페이지 작품 관리 (웹 판 ui/story.js 와 같은 폴더 구조): data/projects/<작품>/p001.cpad, p002.cpad … + 미리보기 p001.png
// 작품 정보 story.json: { order: [파일], status: {파일: conti|line|color|done}, time: {파일: 초}, deadline: 'YYYY-MM-DD' }
// 파일 이름은 웹 판처럼 projects 기준 상대 경로 + '/' ("작품/p001.cpad")
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public static class Story
{
    public static event Action Changed;
    public static void RaiseChanged() => Changed?.Invoke();

    public static string ProjectsDir => FileCommands.ProjectsDir;

    /// <summary>projects 기준 상대 경로 ("작품/p001.cpad"), projects 밖이면 null</summary>
    public static string Rel(string full)
    {
        if (string.IsNullOrEmpty(full)) return null;
        try
        {
            string rel = Path.GetRelativePath(ProjectsDir, Path.GetFullPath(full));
            if (rel.StartsWith("..") || Path.IsPathRooted(rel)) return null;
            return rel.Replace('\\', '/');
        }
        catch { return null; }
    }
    public static string Full(string rel) => Path.Combine(ProjectsDir, rel.Replace('/', Path.DirectorySeparatorChar));

    public static string StoryOfRel(string rel) => rel != null && rel.Contains('/') ? rel.Split('/')[0] : null;
    public static string StoryOf(Document d) => StoryOfRel(Rel(d?.FilePath));
    public static string CurrentRel => Rel(App.Doc?.FilePath);

    public static List<string> ListPages(string story)
    {
        string dir = Path.Combine(ProjectsDir, story);
        if (!Directory.Exists(dir)) return new();
        var pages = Directory.GetFiles(dir, "*.cpad").Select(f => story + "/" + Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var order = (ReadInfo(story)["order"] as JsonArray)?.Select(x => (string)x).ToList() ?? new();
        int At(string n) { int i = order.IndexOf(n); return i < 0 ? 1_000_000 : i; }
        return pages.OrderBy(At).ThenBy(n => n, StringComparer.Ordinal).ToList();
    }

    public static JsonObject ReadInfo(string story)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectsDir, story, "story.json"))) as JsonObject ?? new(); } catch { return new JsonObject(); }
    }
    public static void WriteInfo(string story, JsonObject info)
    {
        string dir = Path.Combine(ProjectsDir, story); Directory.CreateDirectory(dir);
        // 웹 판 JSON.stringify(info, null, 1) 과 같은 모양 (들여쓰기는 읽는 데 상관없음)
        File.WriteAllText(Path.Combine(dir, "story.json"), info.ToJsonString(U.Indent1));
        Changed?.Invoke();
    }

    public static void MovePage(string story, int from, int to)
    {
        var names = ListPages(story);
        if (to < 0 || to >= names.Count || from < 0 || from >= names.Count || from == to) return;
        var x = names[from]; names.RemoveAt(from); names.Insert(to, x);
        var info = ReadInfo(story);
        info["order"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray());
        WriteInfo(story, info);
    }

    public static string PageName(int n) => "p" + n.ToString().PadLeft(3, '0');

    /// <summary>빈 페이지 문서 (기본 컷 틀 포함 가능) — 웹 blankPage 와 같은 레이어·메타</summary>
    public static Document BlankPage(int w, int h, string name, bool withFrame)
    {
        var d = new Document(w, h) { Name = name };
        var l = new Layer(d, "레이어 1");
        d.Layers.Add(l);
        if (withFrame)
        {
            int m = (int)Math.Round(w * 0.07, MidpointRounding.AwayFromZero);
            int line = Math.Max(3, (int)Math.Round(w / 400.0, MidpointRounding.AwayFromZero));
            var pts = new JsonArray(Pt(m, m), Pt(w - m, m), Pt(w - m, h - m), Pt(m, h - m));
            var p = new Layer(d, "컷", "panel")
            {
                Meta = new JsonObject { ["panels"] = new JsonArray(new JsonObject { ["pts"] = pts }), ["line"] = line, ["color"] = "#000000", ["maskOutside"] = true },
            };
            // 컷 바깥 흰색 + 테두리 선 (웹 판 strokeRect 와 같은 자리: m ~ m+line)
            Fill(p.Px, new Rect(0, 0, w, h), 0xFFFFFFFF);
            Fill(p.Px, Rect.FromLTRB(m, m, w - m, h - m), 0);
            foreach (var r in new[] { Rect.FromLTRB(m, m, w - m, m + line), Rect.FromLTRB(m, h - m - line, w - m, h - m), Rect.FromLTRB(m, m, m + line, h - m), Rect.FromLTRB(w - m - line, m, w - m, h - m) })
                Fill(p.Px, r, 0xFF000000);
            d.Layers.Add(p);
        }
        d.ActiveId = l.Id;
        return d;
    }
    static JsonObject Pt(int x, int y) => new() { ["x"] = x, ["y"] = y };
    static void Fill(Surface s, Rect r, uint c)
    {
        r = r.Clip(s.W, s.H);
        for (int y = r.Y; y < r.B; y += Surface.TS)
        {
            var part = Rect.FromLTRB(r.X, y, r.R, Math.Min(r.B, y + Surface.TS));
            var buf = new uint[part.W * part.H];
            if (c != 0) Array.Fill(buf, c);
            s.Write(part, buf);
        }
    }

    /// <summary>페이지 저장 + 미리보기 그림 (긴 쪽 320px)</summary>
    public static void SavePageDoc(Document d, string rel)
    {
        string full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        CpadIO.Save(d, full);
        WriteThumb(d, rel);
    }
    public static void WriteThumb(Document d, string rel)
    {
        try
        {
            double s = Math.Min(1, 320.0 / Math.Max(d.W, d.H));
            using var b = Img.Flatten(d, s, true);
            File.WriteAllBytes(Path.ChangeExtension(Full(rel), ".png"), Img.Encode(b, "png"));
        }
        catch { }
    }

    // ---- 명령 ------------------------------------------------------------------------
    static readonly (string name, int w, int h, bool transparent)[] SizePresets =
    {
        ("웹툰 (세로 스크롤)", 800, 1280, false), ("웹툰 긴 캔버스", 800, 6400, false), ("만화 원고 A4 300dpi", 2480, 3508, false), ("만화 원고 B5 350dpi", 2508, 3541, false),
        ("일러스트 FHD", 1920, 1080, false), ("정사각 2048", 2048, 2048, false), ("러프/아이디어 스케치", 1600, 1200, false),
    };

    public static void NewStoryDialog()
    {
        if (!FileCommands.ConfirmDiscard()) return;
        var v = Dlg.Form("새 작품 (여러 페이지)", new[]
        {
            Field.Text("name", "작품 이름", "새 작품"),
            Field.Num("pages", "페이지 수", 8, 1, 200),
            Field.Select("size", "크기", "2", SizePresets.Select((p, i) => (i.ToString(), $"{p.name} ({p.w}×{p.h})")).ToArray()),
            Field.Check("frame", "기본 컷 틀 넣기", true),
            Field.Info("data/projects/작품이름/ 폴더에 p001, p002… 로 만들어집니다."),
        }, "만들기");
        if (v == null) return;
        string story = U.SafeName((string)v["name"]);
        var preset = SizePresets[int.Parse((string)v["size"] ?? "2")];
        int n = Math.Clamp(U.Int(v["pages"], 8), 1, 200);
        bool frame = (bool)v["frame"];
        if (Directory.Exists(Path.Combine(ProjectsDir, story)) && Directory.GetFiles(Path.Combine(ProjectsDir, story), "*.cpad").Length > 0
            && !Dlg.Confirm($"\"{story}\" 작품 폴더가 이미 있습니다. 같은 이름의 페이지는 덮어씁니다. 계속할까요?")) return;
        U.Task("작품 만들기", () =>
        {
            for (int i = 1; i <= n; i++) SavePageDoc(BlankPage(preset.w, preset.h, $"{story} {i}쪽", frame), $"{story}/{PageName(i)}.cpad");
        });
        FileCommands.OpenPath(Full($"{story}/{PageName(1)}.cpad"));
        Changed?.Invoke();
    }

    /// <summary>현재 페이지를 저장하고 다른 페이지 열기 (넘길 때 자동 저장)</summary>
    public static bool GotoPage(string rel)
    {
        var d = App.Doc;
        if (d?.Modified == true && d.FilePath != null && d.FilePath.EndsWith(".cpad", StringComparison.OrdinalIgnoreCase)) { if (!SaveCurrent()) return false; }
        else if (d?.Modified == true && !Dlg.Confirm("현재 그림이 저장되지 않았습니다. 버리고 이동할까요?", "이동")) return false;
        if (d != null) d.Modified = false;
        FileCommands.OpenPath(Full(rel));
        Changed?.Invoke();
        return true;
    }

    /// <summary>지금 문서를 그 자리에 저장 (+ 미리보기·작업 시간)</summary>
    public static bool SaveCurrent()
    {
        var d = App.Doc;
        if (d == null) return false;
        if (!FileCommands.Save(false)) return false;
        ExtrasModule.OnSaved(d);
        return true;
    }

    public static void StepPage(int dir)
    {
        string story = StoryOf(App.Doc);
        if (story == null) { App.Toast("여러 페이지 작품이 아닙니다 (만화 > 새 작품)"); return; }
        var pages = ListPages(story);
        int i = pages.IndexOf(CurrentRel);
        int t = i + dir;
        if (t >= 0 && t < pages.Count) GotoPage(pages[t]); else App.Toast(dir > 0 ? "마지막 페이지입니다" : "첫 페이지입니다");
    }

    public static void AddPage(bool dup)
    {
        var doc = App.Doc; string story = StoryOf(doc);
        if (story == null) { App.Toast("여러 페이지 작품을 먼저 여세요"); return; }
        if (doc.Modified && !SaveCurrent()) return;
        var pages = ListPages(story);
        int max = pages.Select(p => Regex.Match(p, @"p(\d+)\.cpad$")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
        string rel = $"{story}/{PageName(max + 1)}.cpad";
        U.Task("페이지 추가", () =>
        {
            var nd = dup ? CpadIO.FromJson(CpadIO.ToJson(doc)) : BlankPage(doc.W, doc.H, story, doc.Layers.Any(l => l.Kind == "panel"));
            SavePageDoc(nd, rel);
        });
        doc.Modified = false;
        FileCommands.OpenPath(Full(rel));
        Changed?.Invoke();
    }

    public static void ExportStory()
    {
        string story = StoryOf(App.Doc);
        if (story == null) { App.Toast("여러 페이지 작품을 먼저 여세요"); return; }
        var v = Dlg.Form("작품 전체 내보내기", new[]
        {
            Field.Segment("fmt", "형식", "png", ("png", "PNG"), ("jpg", "JPG")),
            Field.Segment("scale", "배율", "1", ("0.5", "50%"), ("1", "100%")),
        }, "내보내기");
        if (v == null) return;
        if (App.Doc.Modified && !SaveCurrent()) return;
        var pages = ListPages(story);
        string fmt = (string)v["fmt"]; double scale = double.Parse((string)v["scale"], System.Globalization.CultureInfo.InvariantCulture);
        string outDir = Path.Combine(U.ExportsDir, story + "_내보내기");
        U.Task($"{pages.Count}쪽 내보내기", () => ExportPages(pages, outDir, fmt, scale));
        App.Toast($"data/exports/{story}_내보내기 에 저장했습니다");
    }

    public static void ExportPages(List<string> pages, string outDir, string fmt, double scale)
    {
        Directory.CreateDirectory(outDir);
        for (int i = 0; i < pages.Count; i++)
        {
            var d = CpadIO.Load(Full(pages[i]));
            using var b = Img.Flatten(d, scale, true);
            File.WriteAllBytes(Path.Combine(outDir, (i + 1).ToString().PadLeft(3, '0') + "." + fmt), Img.Encode(b, fmt, 0.92));
        }
    }
}

/// <summary>페이지 패널: 작품의 페이지 카드(미리보기·단계 표시), 이전/다음, 추가·복제, 순서 바꾸기</summary>
public sealed class PagesPanel : Panel
{
    static readonly Dictionary<string, (string label, Color color)> ST = new()
    {
        ["conti"] = ("콘티", ColorTranslator.FromHtml("#8e5bd6")), ["line"] = ("선화", ColorTranslator.FromHtml("#3d7be0")),
        ["color"] = ("채색", ColorTranslator.FromHtml("#f08c2e")), ["done"] = ("완료", ColorTranslator.FromHtml("#2fa35b")),
    };
    readonly FlowLayoutPanel body = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Opt.Bg };
    bool pending;

    public PagesPanel()
    {
        BackColor = Opt.Bg;
        Controls.Add(body);
        Story.Changed += Soon;
        App.DocChanged += Soon;
        ExtrasModule.Saved += Soon;
        Soon();
    }

    void Soon()
    {
        if (pending || IsDisposed) return;
        pending = true;
        void Go() { pending = false; if (!IsDisposed) Render(); }
        if (IsHandleCreated) BeginInvoke((Action)Go); else HandleCreated += (_, _) => Go();
    }

    void Render()
    {
        body.SuspendLayout();
        foreach (var pb in U.All(body).OfType<PictureBox>()) pb.Image?.Dispose();
        foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
        body.Controls.Clear();
        string story = Story.StoryOf(App.Doc);
        if (story == null)
        {
            body.Controls.Add(Opt.Help("여러 페이지로 된 만화를 한 작품으로 관리합니다. 페이지를 넘기면 자동 저장됩니다."));
            body.Controls.Add(Opt.Button("새 작품 만들기", Story.NewStoryDialog, true));
            body.ResumeLayout();
            return;
        }
        var pages = Story.ListPages(story);
        string cur = Story.CurrentRel;
        int ci = pages.IndexOf(cur);
        body.Controls.Add(Opt.Title($"{story} — {pages.Count}쪽"));
        body.Controls.Add(Opt.Row(Opt.Button("◀ 이전", () => Story.StepPage(-1)), Opt.Button("다음 ▶", () => Story.StepPage(1)), Opt.Button("+ 빈 페이지", () => Story.AddPage(false))));
        body.Controls.Add(Opt.Row(Opt.Button("+ 복제", () => Story.AddPage(true)), Opt.Button("전체 내보내기", Story.ExportStory), Opt.Button("폴더 열기", () => Shell.OpenFolder(Path.Combine(Story.ProjectsDir, story)))));
        body.Controls.Add(Opt.Row(Opt.Button("◀ 이 쪽을 앞으로", () => Story.MovePage(story, ci, ci - 1)), Opt.Button("이 쪽을 뒤로 ▶", () => Story.MovePage(story, ci, ci + 1))));
        body.Controls.Add(Opt.Row(Opt.Button("진행표…", Progress.Dialog)));
        var info = Story.ReadInfo(story);
        var grid = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(290, 0), WrapContents = true, BackColor = Opt.Bg };
        for (int i = 0; i < pages.Count; i++)
        {
            string p = pages[i]; int idx = i;
            string st = (string)info["status"]?[p];
            var card = new Panel { Width = 86, Height = 122, Margin = new Padding(3), BackColor = p == cur ? Opt.Accent : Opt.Bg2, Cursor = Cursors.Hand, AllowDrop = true };
            var pic = new PictureBox { Location = new Point(3, 3), Size = new Size(80, 96), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(70, 70, 78), Image = Img.Load(Path.ChangeExtension(Story.Full(p), ".png")) };
            var lab = new Label { Text = (i + 1).ToString(), Location = new Point(3, 101), AutoSize = true, ForeColor = p == cur ? Color.Black : Opt.Fg };
            card.Controls.Add(pic); card.Controls.Add(lab);
            if (st != null && ST.TryGetValue(st, out var s))
                card.Controls.Add(new Label { Text = s.label, BackColor = s.color, ForeColor = Color.White, AutoSize = true, Location = new Point(40, 101) });
            void Open(object _, EventArgs __) { if (p != Story.CurrentRel) Story.GotoPage(p); }
            card.Click += Open; pic.Click += Open; lab.Click += Open;
            // 끌어서 다른 카드 위에 놓으면 그 자리로
            pic.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) card.DoDragDrop("page:" + idx, DragDropEffects.Move); };
            card.DragEnter += (_, e) => e.Effect = e.Data.GetData(typeof(string)) is string s2 && s2.StartsWith("page:") ? DragDropEffects.Move : DragDropEffects.None;
            card.DragDrop += (_, e) => { if (e.Data.GetData(typeof(string)) is string s2 && int.TryParse(s2[5..], out int from)) Story.MovePage(story, from, idx); };
            pic.AllowDrop = true;
            pic.DragEnter += (_, e) => e.Effect = DragDropEffects.Move;
            pic.DragDrop += (_, e) => { if (e.Data.GetData(typeof(string)) is string s2 && s2.StartsWith("page:") && int.TryParse(s2[5..], out int from)) Story.MovePage(story, from, idx); };
            grid.Controls.Add(card);
        }
        body.Controls.Add(grid);
        body.Controls.Add(Opt.Help("페이지 카드를 끌어다 다른 카드 위에 놓으면 순서가 바뀝니다 (펜·터치는 위 버튼으로)."));
        body.ResumeLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Story.Changed -= Soon; App.DocChanged -= Soon; ExtrasModule.Saved -= Soon; }
        base.Dispose(disposing);
    }
}
