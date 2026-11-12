// 오른쪽 패널 탭 (웹 ui/extra.js historyPanel · navigatorPanel, ui/panels.js assetsPanel 과 같은 모양).
//  - 기록: 실행취소 목록 (칸 34px, 지금 단계 = 주황, 다시실행할 것 = 흐리게), 누르면 그 단계까지 되돌리기/다시하기
//  - 내비: 그림 전체 축소판 (#1e1e22 둥근 칸) + 지금 보이는 곳 주황 네모, 눌러서·끌어서 이동, 확대·회전 막대, 맞춤·가로 맞춤·100%·좌우 반전
//  - 에셋: [레이어→에셋] [보이는 그림→에셋] / [폴더] [새로고침] [검색…] / 격자 (체크무늬 칸 + 이름), 누르면 그림에 넣기
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.Filters;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.Modules.Shell2;

/// <summary>패널 공통: 굴리는 판 + 안의 세로 쌓기 (웹 .panel-scroll 여백 4·10·12)</summary>
public class ScrollPanel : Panel
{
    public readonly WebStack Stack = new() { Padding = new Padding(10, 4, 10, 12) };
    public ScrollPanel()
    {
        BackColor = Theme.Panel; AutoScroll = true; Font = Web.Body;
        this.DarkScroll();
        AutoScroll = false; HorizontalScroll.Maximum = 0; HorizontalScroll.Visible = false; AutoScroll = true;
        Controls.Add(Stack);
    }
}

public sealed class HistoryPanel : ScrollPanel
{
    readonly HistList list;

    public HistoryPanel()
    {
        Stack.Controls.Add(new WebHelp("항목을 탭하면 그 시점으로 돌아갑니다 (다시 앞으로도 갈 수 있음)."));
        list = new HistList();
        Stack.Controls.Add(list);
        App.History.Changed += () => { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Fill); };
        VisibleChanged += (_, _) => { if (Visible) Fill(); };
        Fill();
    }

    void Fill()
    {
        if (IsDisposed) return;
        var u = App.History.UndoItems; var r = App.History.RedoItems;
        var items = new List<string> { "(시작)" };
        items.AddRange(u.Select(e => e.Label ?? "작업"));
        for (int i = r.Count - 1; i >= 0; i--) items.Add(r[i].Label ?? "작업");
        list.Set(items, u.Count);
        Stack.PerformLayout();
        // 지금 단계가 보이게
        int y = list.Top + list.ItemTop(u.Count);
        if (y < -AutoScrollPosition.Y || y > -AutoScrollPosition.Y + ClientSize.Height - 40) AutoScrollPosition = new Point(0, Math.Max(0, y - ClientSize.Height / 2));
    }

    /// <summary>index 단계 = 실행취소 목록이 index 개 남은 상태</summary>
    public static void JumpTo(int index)
    {
        var h = App.History; var d = App.Doc; if (d == null) return;
        var r = Rect.Empty;
        while (h.UndoItems.Count > index && h.CanUndo) r = r.Union(h.Undo(d));
        while (h.UndoItems.Count < index && h.CanRedo) r = r.Union(h.Redo(d));
        App.InvalidateAll(); App.NotifyLayers(); SelectCommands.RedrawView();
    }

    /// <summary>.hist-list (.hist 칸 34px, 간격 2, .on 주황, .redo 흐리게)</summary>
    sealed class HistList : Control, IWebBlock
    {
        List<string> items = new(); int cur, hot = -1;
        const int H = 34, Gap = 2;
        public HistList() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Font = Web.Body; Cursor = Cursors.Hand; }
        public void Set(List<string> it, int current) { items = it; cur = current; Invalidate(); }
        public int ItemTop(int i) => i * (H + Gap);
        public override Size GetPreferredSize(Size proposed) => new(proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 280, Math.Max(0, items.Count * (H + Gap) - Gap));
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Web.ParentBack(this));
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rectangle(0, ItemTop(i), Width, H);
                if (!e.ClipRectangle.IntersectsWith(r)) continue;
                bool on = i == cur, redo = i > cur;
                Color bg = on ? Theme.Accent : i == hot ? Theme.Panel3 : Theme.Panel2;
                Color fg = on ? Web.OnDark : Theme.Text;
                if (redo) { bg = Web.Mix(Theme.Panel, bg, 0.45); fg = Web.Mix(Theme.Panel, fg, 0.45); }
                Web.Box(g, r, 6, bg);
                Web.Text(g, items[i], on ? Web.BodyBold : Font, new Rectangle(10, r.Y, r.Width - 20, H), fg, Web.Left);
            }
        }
        int Hit(Point p) { int i = p.Y / (H + Gap); return i >= 0 && i < items.Count ? i : -1; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != hot) { hot = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); int h = Hit(e.Location); if (h >= 0 && e.Button == MouseButtons.Left && h != cur) JumpTo(h); }
    }
}

public sealed class NavigatorPanel : ScrollPanel
{
    readonly WebCanvas view = new() { Cursor = Cursors.Hand, Margin = new Padding(0, 6, 0, 6) };
    readonly WebSlider zoom, rot;
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 500 };
    Bitmap thumb; double scale; PointF off;
    bool dirty = true, hooked;

    public NavigatorPanel()
    {
        view.Stretch();
        zoom = new WebSlider("확대", 0.02, 16, () => Shell2Ui.Canvas?.V.Zoom ?? 1, v => Shell2Ui.Canvas?.SetZoom(v), 0.001, true, v => $"{Math.Round(v * 1000) / 10}%");
        rot = new WebSlider("회전", -180, 180, () => { var a = Shell2Ui.Canvas?.V.Angle ?? 0; return ((a % 360) + 540) % 360 - 180; }, v =>
        {
            var c = Shell2Ui.Canvas; if (c == null) return;
            c.RotateBy(v - c.V.Angle, c.Width / 2.0, c.Height / 2.0);
        }, 1, false, v => $"{Math.Round(v)}°");
        Stack.Controls.Add(view); Stack.Controls.Add(zoom); Stack.Controls.Add(rot);
        Stack.Controls.Add(Opt.Row(Opt.Button("맞춤", () => Web.Run("view.fit")), Opt.Button("가로 맞춤", () => Web.Run("view.fitWidth")), Opt.Button("100%", () => Web.Run("view.actual")), Opt.Button("좌우 반전", () => Web.Run("view.flip"))));
        view.Paint += (_, e) => Draw(e.Graphics);
        view.Resize += (_, _) => { dirty = true; view.Invalidate(); };
        view.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { view.Capture = true; MoveTo(e.Location); } };
        view.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) MoveTo(e.Location); };
        refresh.Tick += (_, _) => { refresh.Stop(); dirty = true; if (Visible) view.Invalidate(); };
        App.Invalidated += _ => { if (!refresh.Enabled && !IsDisposed) refresh.Start(); };
        App.DocChanged += () => { dirty = true; FitHeight(); view.Invalidate(); };
        Stack.SizeChanged += (_, _) => FitHeight();
        VisibleChanged += (_, _) =>
        {
            if (!hooked && Shell2Ui.Canvas is CanvasView cv) { hooked = true; cv.ViewChanged += () => { if (Visible && !IsDisposed) { zoom.Sync(); rot.Sync(); view.Invalidate(); } }; }
            if (Visible) { FitHeight(); zoom.Sync(); rot.Sync(); view.Invalidate(); }
        };
        FitHeight();
    }

    /// <summary>웹: 높이 = min(260, max(120, 폭 × 그림 비율))</summary>
    void FitHeight()
    {
        var d = App.Doc;
        int w = Math.Max(100, view.Width > 10 ? view.Width : Stack.Width - Stack.Padding.Horizontal);
        int h = d == null ? 160 : (int)Math.Min(260, Math.Max(120, w * (double)d.H / d.W));
        if (view.Height != h) { view.Height = h; dirty = true; Stack.PerformLayout(); }
    }

    void Draw(Graphics g)
    {
        g.Clear(Web.ParentBack(view));
        var r = new Rectangle(0, 0, view.Width, view.Height);
        Web.Box(g, r, 8, Color.FromArgb(0x1e, 0x1e, 0x22));
        var d = App.Doc; var c = Shell2Ui.Canvas;
        if (d == null) return;
        double s = Math.Min(view.Width / (double)d.W, view.Height / (double)d.H);
        if (dirty || thumb == null || Math.Abs(s - scale) > 1e-6)
        {
            dirty = false; scale = s;
            if (scale <= 0) return;
            thumb?.Dispose();
            try { thumb = Img.FlattenScaled(d, scale); } catch { thumb = null; }
        }
        off = new PointF((float)((view.Width - d.W * scale) / 2), (float)((view.Height - d.H * scale) / 2));
        var st = g.Save();
        using (var clip = Theme.Round(r, 8)) g.SetClip(clip);
        if (thumb != null) g.DrawImage(thumb, off.X, off.Y, (float)(d.W * scale), (float)(d.H * scale));
        g.Restore(st);
        if (c == null) return;
        // 화면 네 귀퉁이 → 문서 → 축소판 (주황 #e8a33d, 2px)
        var pts = new[] { c.V.ToDoc(0, 0), c.V.ToDoc(c.Width, 0), c.V.ToDoc(c.Width, c.Height), c.V.ToDoc(0, c.Height) }
            .Select(p => new PointF((float)(off.X + p.x * scale), (float)(off.Y + p.y * scale))).ToArray();
        Web.Smooth(g);
        using var pen = new Pen(Theme.Accent, 2);
        g.DrawPolygon(pen, pts);
    }

    void MoveTo(Point p)
    {
        var d = App.Doc; var c = Shell2Ui.Canvas; if (d == null || c == null || scale <= 0) return;
        double dx = (p.X - off.X) / scale, dy = (p.Y - off.Y) / scale;
        var (sx, sy) = c.V.ToScreen(dx, dy);
        c.V.PanX += c.Width / 2.0 - sx; c.V.PanY += c.Height / 2.0 - sy;
        c.RedrawAll();
        view.Invalidate();
    }

    protected override void Dispose(bool disposing) { if (disposing) { refresh.Dispose(); thumb?.Dispose(); } base.Dispose(disposing); }
}

public sealed class AssetsPanel : ScrollPanel
{
    static readonly List<AssetsPanel> live = new();
    public static void RefreshAll() { foreach (var p in live.ToList()) if (!p.IsDisposed) p.Fill(); }

    readonly WebInput filter = new("") { Placeholder = "검색…", Width = 80 };
    readonly WebSelect folder = new(new[] { ("", "(모든 폴더)") }) { Margin = Padding.Empty };
    readonly AssetGrid grid = new();
    readonly WebHelp empty = new("에셋이 없습니다.");
    bool loaded;

    public AssetsPanel()
    {
        Stack.Controls.Add(Opt.Row(Opt.Button("레이어→에셋", () => Web.Run("asset.saveLayer")), Opt.Button("보이는 그림→에셋", () => Web.Run("asset.saveMerged"))));
        var fb = new WebButton("폴더", BtnKind.Normal, "folder") { Margin = Padding.Empty };
        fb.Click += (_, _) => UI.Shell.OpenFolder(MoreCommands.AssetsDir);
        var rb = new WebButton("새로고침") { Margin = Padding.Empty };
        rb.Click += (_, _) => Fill();
        filter.Margin = Padding.Empty; filter.Flex();
        Stack.Controls.Add(Opt.Row(fb, rb, filter));
        folder.Visible = false;
        Stack.Controls.Add(folder);
        Stack.Controls.Add(grid);
        Stack.Controls.Add(empty);
        Stack.Controls.Add(new WebHelp("누르기: 캔버스에 배치 · 오른쪽 클릭: 파일 위치. data/assets 폴더에 PNG를 넣어도 됩니다."));
        filter.TextChanged += (_, _) => FillFiles();
        folder.SelectedIndexChanged += (_, _) => FillFiles();
        grid.Picked += Insert;
        live.Add(this);
        Disposed += (_, _) => { live.Remove(this); grid.Clear(); };
        VisibleChanged += (_, _) => { if (Visible && !loaded) Fill(); };
    }

    public void Fill()
    {
        loaded = true;
        string cur = folder.SelectedId;
        var dirs = new List<(string, string)> { ("", "(모든 폴더)") };
        try
        {
            string root = MoreCommands.AssetsDir;
            foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderBy(x => x)) { var rel = Path.GetRelativePath(root, d).Replace('\\', '/'); dirs.Add((rel, rel)); }
        }
        catch { }
        folder.SetItems(dirs);
        folder.SelectedId = dirs.Any(x => x.Item1 == cur) ? cur : "";
        folder.Visible = dirs.Count > 1;
        FillFiles();
    }

    void FillFiles()
    {
        grid.Clear();
        try
        {
            string root = MoreCommands.AssetsDir;
            string sel = folder.SelectedId;
            string dir = string.IsNullOrEmpty(sel) ? root : Path.Combine(root, sel);
            string q = filter.Text.Trim().ToLowerInvariant();
            var files = Directory.GetFiles(dir, "*.*", string.IsNullOrEmpty(sel) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => q.Length == 0 || Path.GetFileName(f).ToLowerInvariant().Contains(q)).Take(400).ToList();
            foreach (var f in files)
            {
                try
                {
                    using var b = ImageIO.LoadBitmap(f);
                    var t = new Bitmap(96, 96);
                    using (var g = Graphics.FromImage(t)) { double s = Math.Min(96.0 / b.Width, 96.0 / b.Height); int w = Math.Max(1, (int)(b.Width * s)), h = Math.Max(1, (int)(b.Height * s)); g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(b, (96 - w) / 2, (96 - h) / 2, w, h); }
                    grid.Add(f, Path.GetFileNameWithoutExtension(f), t);
                }
                catch { }
            }
        }
        catch { }
        empty.Visible = grid.Count == 0;
        Stack.PerformLayout(); grid.Invalidate();
    }

    void Insert(string f)
    {
        if (App.Doc == null) return;
        try { using var b = ImageIO.LoadBitmap(f); ImageIO.PlaceImage(b, Path.GetFileNameWithoutExtension(f)); }
        catch (Exception e) { App.Toast("넣지 못했습니다: " + e.Message); }
    }

    /// <summary>.asset-grid (칸 최소 80, 간격 6) / .asset (테두리 --line, 둥근 8, 체크무늬 바탕, 정사각 그림 + 이름 11px 어두운 띠)</summary>
    sealed class AssetGrid : Control, IWebBlock
    {
        readonly List<(string path, string name, Bitmap thumb)> items = new();
        public event Action<string> Picked;
        int hot = -1;
        readonly ToolTip tip = new();
        public AssetGrid() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Margin = new Padding(0, 8, 0, 0); Cursor = Cursors.Hand; }
        public int Count => items.Count;
        public void Add(string p, string n, Bitmap t) => items.Add((p, n, t));
        public void Clear() { foreach (var i in items) i.thumb.Dispose(); items.Clear(); Invalidate(); }
        int Cols(int w) => Math.Max(1, (w + 6) / (80 + 6));
        int Cw(int w) => (w - (Cols(w) - 1) * 6) / Cols(w);
        int Ch(int w) => Cw(w) + 4 + 16;
        Rectangle R(int i) { int n = Cols(Width), cw = Cw(Width), ch = Ch(Width); return new Rectangle(i % n * (cw + 6), i / n * (ch + 6), cw, ch); }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 280;
            int rows = (items.Count + Cols(w) - 1) / Cols(w);
            return new Size(w, rows == 0 ? 0 : rows * (Ch(w) + 6) - 6);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Web.ParentBack(this));
            for (int i = 0; i < items.Count; i++)
            {
                var r = R(i); if (!e.ClipRectangle.IntersectsWith(r)) continue;
                var st = g.Save();
                using (var clip = Theme.Round(r, 8)) g.SetClip(clip);
                Web.Checker(g, r, 6, Color.FromArgb(0x3b, 0x3b, 0x42), Color.FromArgb(0x45, 0x45, 0x4d));
                int s = r.Width - 8;
                g.DrawImage(items[i].thumb, r.X + 4, r.Y + 4, s, s);
                g.Restore(st);
                Web.Box(g, r, 8, null, i == hot ? Theme.Accent : Theme.Line);
                var ns = Web.Measure(items[i].name, Web.Tiny);
                int nw = Math.Min(r.Width - 8, ns.Width + 8);
                var nr = new Rectangle(r.X + (r.Width - nw) / 2, r.Bottom - 18, nw, 15);
                Web.Box(g, nr, 3, Color.FromArgb(128, 0, 0, 0));
                Web.Text(g, items[i].name, Web.Tiny, nr, Theme.Text, Web.Center);
            }
        }
        int Hit(Point p) { for (int i = 0; i < items.Count; i++) if (R(i).Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != hot) { hot = h; Invalidate(); tip.SetToolTip(this, h >= 0 ? Path.GetFileName(items[h].path) : ""); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = Hit(e.Location); if (h < 0) return;
            if (e.Button == MouseButtons.Left) Picked?.Invoke(items[h].path);
            else if (e.Button == MouseButtons.Right)
            {
                var path = items[h].path;
                var m = WebMenu.Style(new ContextMenuStrip());
                m.Items.Add("캔버스에 넣기", null, (_, _) => Picked?.Invoke(path));
                m.Items.Add("파일 위치 열기", null, (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } });
                m.Show(this, e.Location);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    }
}
