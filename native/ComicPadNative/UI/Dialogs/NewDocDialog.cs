// 새 캔버스 / ComicPad 시작 (웹 ui/files.js newDocDialog): 넓은 창(820), 최근 프로젝트 카드(썸네일), "새로 만들기" 크기 카드 격자
// (왼쪽 52px 비율 그림 + 굵은 이름 + 흐린 크기·설명), 아래 [크기 직접 지정…] [프로젝트 열기…].
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.UI.Dialogs;

public static class NewDocDialog
{
    public sealed record SizePreset(string Name, int W, int H, string Note, bool Transparent = false, bool Rough = false, int Dpi = 0, JsonObject Start = null);

    /// <summary>웹 SIZE_PRESETS 와 같은 목록</summary>
    public static readonly SizePreset[] Presets =
    {
        new("웹툰 (세로 스크롤)", 800, 1280, "800×1280, 아래로 늘려 쓰기"),
        new("웹툰 긴 캔버스", 800, 6400, "800×6400"),
        new("만화 원고 A4 300dpi", 2480, 3508, "흑백/컬러 원고"),
        new("만화 원고 B5 350dpi", 2508, 3541, ""),
        new("일러스트 FHD", 1920, 1080, "가로"),
        new("정사각 2048", 2048, 2048, "SNS/표지"),
        new("에셋 (투명) 1024", 1024, 1024, "배경 투명", Transparent: true),
        new("에셋 (투명) 2048", 2048, 2048, "배경 투명", Transparent: true),
        new("러프/아이디어 스케치", 1600, 1200, "러프 레이어 포함", Rough: true),
    };

    /// <summary>startup = 앱을 켰을 때 ("ComicPad 시작", 저장 안 한 변경 확인 없음)</summary>
    public static void Show(bool startup = false)
    {
        if (!startup && !FileCommands.ConfirmDiscard()) return;
        using var f = new ModalForm(startup ? "ComicPad 시작" : "새 캔버스", 820);
        var body = new WebStack();
        // 최근 프로젝트
        var recent = RecentProjects().Take(6).ToList();
        if (recent.Count > 0)
        {
            body.Controls.Add(new SectionLabel("최근 프로젝트"));
            var pg = new CardGrid(150, 8) { Margin = new Padding(0, 8, 0, 12) };
            foreach (var p in recent)
            {
                var fi = new FileInfo(p);
                string png = Path.ChangeExtension(p, ".png");
                Image thumb = null;
                try { if (File.Exists(png)) using (var im = Image.FromFile(png)) thumb = new Bitmap(im); } catch { }
                string path = p;
                pg.Add(new Card { Title = Path.GetFileNameWithoutExtension(p), Sub = $"{fi.LastWriteTime:yyyy-MM-dd HH:mm} · {fi.Length / 1048576.0:0.0}MB", Image = thumb, Project = true, Run = () => { f.Close(); FileCommands.OpenPath(path); } });
            }
            body.Controls.Add(pg);
        }
        body.Controls.Add(new SectionLabel("새로 만들기"));
        var grid = new CardGrid(180, 8) { Margin = new Padding(0, 8, 0, 12) };
        var list = new List<SizePreset>();
        if (App.Settings["startDoc"] is JsonObject sd)
            list.Add(new SizePreset("추천: " + ((string)sd["name"] ?? "추천 크기"), (int)(sd["w"]?.GetValue<double>() ?? 2480), (int)(sd["h"]?.GetValue<double>() ?? 3508),
                (sd["dpi"] != null ? sd["dpi"] + "dpi" : "") + ((string)sd["note"] is string nt && nt.Length > 0 ? " · " + nt : ""), Start: sd));
        list.AddRange(Presets);
        foreach (var p in list)
        {
            var pp = p;
            grid.Add(new Card { Title = p.Name, Sub = $"{p.W}×{p.H}{(p.Note.Length > 0 ? " · " + p.Note : "")}", RatioW = p.W, RatioH = p.H, Transparent = p.Transparent, Run = () => { f.Close(); Create(pp); } });
        }
        body.Controls.Add(grid);
        var custom = Opt.Button("크기 직접 지정…", () => { f.Close(); Custom(); });
        var open = Opt.Button("프로젝트 열기…", () => { f.Close(); Web.Run("file.open"); });
        body.Controls.Add(Opt.Row(custom, open));
        f.SetContent(body);
        f.ShowDialog(App.Main);
    }

    static IEnumerable<string> RecentProjects()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var res = new List<(string p, DateTime t)>();
        try
        {
            if (App.Settings["recentFiles"] is JsonArray ra) foreach (var n in ra) { string p = (string)n; if (p != null && p.EndsWith(".cpad", StringComparison.OrdinalIgnoreCase) && File.Exists(p) && seen.Add(Path.GetFullPath(p))) res.Add((p, File.GetLastWriteTime(p))); }
            string dir = Path.Combine(App.DataDir, "projects");
            if (Directory.Exists(dir)) foreach (var p in Directory.EnumerateFiles(dir, "*.cpad", SearchOption.AllDirectories)) if (seen.Add(Path.GetFullPath(p))) res.Add((p, File.GetLastWriteTime(p)));
        }
        catch { }
        return res.OrderByDescending(x => x.t).Select(x => x.p);
    }

    /// <summary>웹 createFromPreset</summary>
    public static void Create(SizePreset p)
    {
        Document d;
        if (p.Start != null) d = Modules.Extras.Setup.StartDocument(p.Start);
        else
        {
            d = Document.New(p.W, p.H, System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(p.Name, @"^추천:\s*", ""), @"\s*\(.*\)", ""));
            if (p.Dpi > 0) d.Dpi = p.Dpi;
            if (p.Transparent) d.PaperVisible = false;
            if (p.Rough && d.Layers.Count > 0)
            {
                var l = d.Layers[0];
                l.Name = "러프 (파랑)"; l.Rough = true; l.Tint = "#3d7be0"; l.Opacity = 0.6f; l.Draft = true;
            }
        }
        App.SetDoc(d);
        App.Settings["lastNew"] = new JsonObject { ["w"] = p.W, ["h"] = p.H, ["transparent"] = p.Transparent };
        App.Set("newW", p.W); App.Set("newH", p.H);
        App.SaveSettingsSoon();
    }

    static void Custom()
    {
        var last = App.Settings["lastNew"] as JsonObject;
        double lw = last?["w"]?.GetValue<double>() ?? App.Get("newW", 2000.0), lh = last?["h"]?.GetValue<double>() ?? App.Get("newH", 2000.0);
        var v = WebForm.Show("직접 지정", new[]
        {
            new FormField { Key = "name", Label = "이름", Value = "새 작품" },
            new FormField { Key = "w", Label = "가로(px)", Type = "number", Value = lw, Min = 16, Max = 16000 },
            new FormField { Key = "h", Label = "세로(px)", Type = "number", Value = lh, Min = 16, Max = 30000 },
            new FormField { Key = "dpi", Label = "해상도 (dpi)", Type = "number", Value = App.Get("newDpi", 350.0), Min = 36, Max = 1200 },
            new FormField { Key = "transparent", Label = "배경 투명", Type = "checkbox", Value = false },
        }, "만들기");
        if (v == null) return;
        int dpi = (int)Math.Round((double)v["dpi"]);
        App.Set("newDpi", dpi);
        Create(new SizePreset((string)v["name"] is { Length: > 0 } n ? n : "새 작품", (int)Math.Round((double)v["w"]), (int)Math.Round((double)v["h"]), "", (bool)v["transparent"], Dpi: dpi));
    }

    // ---- 카드 격자 (.preset-grid / .project-grid) ----------------------------------------------------
    sealed class Card
    {
        public string Title, Sub; public Image Image; public bool Project, Transparent; public int RatioW, RatioH; public Action Run;
    }

    sealed class CardGrid : Control, IWebBlock
    {
        readonly List<Card> cards = new();
        readonly int minW, gap;
        int hot = -1;
        public CardGrid(int minW, int gap)
        {
            this.minW = minW; this.gap = gap;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = Web.Body; Cursor = Cursors.Hand; TabStop = true;
        }
        public void Add(Card c) => cards.Add(c);
        int Cols(int w) => Math.Max(1, (w + gap) / (minW + gap));
        int CardW(int w) => (w - (Cols(w) - 1) * gap) / Cols(w);
        int CardH(Card c, int cw)
        {
            if (c.Project) return 6 + 120 + 4 + Web.BodyBold.Height + 2 + Web.Tiny.Height + 6;
            int tw = cw - 16 - 52 - 8;
            int th = Web.MeasureWrap(c.Title, Web.BodyBold, tw).Height + 2 + Web.MeasureWrap(c.Sub, Web.Tiny, tw).Height;
            return Math.Max(64, th + 16);
        }
        List<Rectangle> Lay(int w)
        {
            var res = new List<Rectangle>(); int n = Cols(w), cw = CardW(w), y = 0;
            for (int i = 0; i < cards.Count; i += n)
            {
                int rh = cards.Skip(i).Take(n).Max(c => CardH(c, cw));
                for (int k = 0; k < n && i + k < cards.Count; k++) res.Add(new Rectangle(k * (cw + gap), y, cw, rh));
                y += rh + gap;
            }
            return res;
        }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 788;
            var r = Lay(w);
            return new Size(w, r.Count == 0 ? 0 : r.Max(x => x.Bottom));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Web.ParentBack(this));
            var rs = Lay(Width);
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i]; var r = rs[i];
                Web.Box(g, r, 10, Theme.Panel2, i == hot ? Theme.Accent : Theme.Line);
                if (c.Project)
                {
                    var ir = new Rectangle(r.X + 6, r.Y + 6, r.Width - 12, 120);
                    Web.Box(g, ir, 6, Color.FromArgb(0x22, 0x22, 0x22));
                    if (c.Image != null)
                    {
                        double k = Math.Min((double)ir.Width / c.Image.Width, (double)ir.Height / c.Image.Height);
                        int w = (int)(c.Image.Width * k), h = (int)(c.Image.Height * k);
                        g.DrawImage(c.Image, ir.X + (ir.Width - w) / 2, ir.Y + (ir.Height - h) / 2, w, h);
                    }
                    else Icons.Draw(g, "image", new RectangleF(ir.X + ir.Width / 2f - 16, ir.Y + ir.Height / 2f - 16, 32, 32), Theme.Muted);
                    int ty = ir.Bottom + 4;
                    Web.Text(g, c.Title, Web.BodyBold, new Rectangle(r.X + 6, ty, r.Width - 12, Web.BodyBold.Height + 2), Theme.Text, Web.Left);
                    Web.Text(g, c.Sub, Web.Tiny, new Rectangle(r.X + 6, ty + Web.BodyBold.Height + 2, r.Width - 12, Web.Tiny.Height + 2), Theme.Muted, Web.Left);
                    continue;
                }
                // 크기 비율 그림 (.size-thumb 52×52, 안의 그림은 최대 44)
                var tr = new Rectangle(r.X + 8, r.Y + (r.Height - 52) / 2, 52, 52);
                Web.Box(g, tr, 6, Web.SegBg);
                double ratio = c.RatioW / (double)Math.Max(1, c.RatioH);
                int bw = (int)Math.Max(6, ratio > 1 ? 44 : 44 * ratio), bh = (int)Math.Max(6, ratio > 1 ? 44 / ratio : 44);
                var pr = new Rectangle(tr.X + (52 - bw) / 2, tr.Y + (52 - bh) / 2, bw, bh);
                using (var sh = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(sh, pr.X, pr.Y + 1, pr.Width, pr.Height + 1);
                if (c.Transparent) Web.Checker(g, pr, 4, Color.FromArgb(0xcc, 0xcc, 0xcc), Color.White);
                else g.FillRectangle(Brushes.White, pr);
                int tx = tr.Right + 8, tw = r.Right - 8 - tx;
                var ts = Web.MeasureWrap(c.Title, Web.BodyBold, tw); var ss = Web.MeasureWrap(c.Sub, Web.Tiny, tw);
                int y0 = r.Y + (r.Height - ts.Height - 2 - ss.Height) / 2;
                Web.Text(g, c.Title, Web.BodyBold, new Rectangle(tx, y0, tw, ts.Height + 2), Theme.Text, Web.Wrap);
                Web.Text(g, c.Sub, Web.Tiny, new Rectangle(tx, y0 + ts.Height + 2, tw, ss.Height + 2), Theme.Muted, Web.Wrap);
            }
        }
        int Hit(Point p) { var rs = Lay(Width); for (int i = 0; i < rs.Count; i++) if (rs[i].Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != hot) { hot = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); int h = Hit(e.Location); if (h >= 0 && e.Button == MouseButtons.Left) cards[h].Run(); }
        protected override void Dispose(bool disposing) { if (disposing) foreach (var c in cards) c.Image?.Dispose(); base.Dispose(disposing); }
    }
}
