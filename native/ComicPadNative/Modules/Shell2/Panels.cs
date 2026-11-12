// 오른쪽 패널 탭 (웹 ui/panels.js 의 기록·내비·에셋 패널).
//  - 기록: 실행취소 목록, 누르면 그 단계까지 되돌리기/다시하기
//  - 내비: 그림 전체 축소판 + 지금 보이는 곳 네모, 눌러서·끌어서 이동, 확대 막대
//  - 에셋: data/assets 의 그림, 두 번 누르면 새 레이어로 넣기
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.Filters;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public sealed class HistoryPanel : Panel
{
    readonly ListBox list = new() { Dock = DockStyle.Fill, BackColor = Opt.Bg, ForeColor = Opt.Fg, BorderStyle = BorderStyle.None, Font = new Font("Malgun Gothic", 9.5f), IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22 };
    int undoCount;
    bool syncing;

    public HistoryPanel()
    {
        BackColor = Opt.Bg;
        Controls.Add(list);
        Controls.Add(new Label { Text = "누르면 그 단계로 되돌립니다 (회색 = 다시실행할 수 있는 것)", Dock = DockStyle.Top, Height = 20, ForeColor = Opt.Muted });
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool redo = e.Index > undoCount;
            bool cur = e.Index == undoCount;
            using var bg = new SolidBrush(cur ? Color.FromArgb(90, 70, 40) : Opt.Bg);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, list.Items[e.Index].ToString(), list.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 2, e.Bounds.Width - 6, e.Bounds.Height), redo ? Opt.Muted : Opt.Fg, TextFormatFlags.Left);
        };
        list.SelectedIndexChanged += (_, _) => { if (!syncing && list.SelectedIndex >= 0) JumpTo(list.SelectedIndex); };
        App.History.Changed += () => { if (IsHandleCreated && Visible) BeginInvoke((Action)Fill); };
        VisibleChanged += (_, _) => { if (Visible) Fill(); };
        Fill();
    }

    void Fill()
    {
        if (IsDisposed) return;
        syncing = true;
        list.BeginUpdate();
        list.Items.Clear();
        var u = App.History.UndoItems; var r = App.History.RedoItems;
        list.Items.Add("(처음 상태)");
        foreach (var e in u) list.Items.Add(e.Label);
        for (int i = r.Count - 1; i >= 0; i--) list.Items.Add(r[i].Label);
        undoCount = u.Count;
        list.SelectedIndex = -1;
        list.EndUpdate();
        if (list.Items.Count > 0) list.TopIndex = Math.Max(0, undoCount - 8);
        syncing = false;
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
}

public sealed class NavigatorPanel : Panel
{
    readonly DoubleBufferedPanel view = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(40, 40, 44), Cursor = Cursors.Hand };
    readonly TrackBar zoom = new() { Dock = DockStyle.Bottom, Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Height = 28 };
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 400 };
    Bitmap thumb; double scale; Point off;
    bool dirty = true, hooked;

    public NavigatorPanel()
    {
        BackColor = Opt.Bg;
        Controls.Add(view); Controls.Add(zoom);
        view.Paint += (_, e) => Draw(e.Graphics);
        view.Resize += (_, _) => { dirty = true; view.Invalidate(); };
        view.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) MoveTo(e.Location); };
        view.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) MoveTo(e.Location); };
        refresh.Tick += (_, _) => { refresh.Stop(); dirty = true; if (Visible) view.Invalidate(); };
        App.Invalidated += _ => { if (!refresh.Enabled) refresh.Start(); };
        App.DocChanged += () => { dirty = true; view.Invalidate(); };
        zoom.Scroll += (_, _) =>
        {
            var c = Shell2Ui.Canvas; if (c == null) return;
            double z = 0.02 * Math.Pow(64 / 0.02, zoom.Value / 1000.0);
            c.SetZoom(z);
        };
        VisibleChanged += (_, _) =>
        {
            if (!hooked && Shell2Ui.Canvas is CanvasView cv) { hooked = true; cv.ViewChanged += () => { if (Visible) { SyncZoom(); view.Invalidate(); } }; }
            if (Visible) { SyncZoom(); view.Invalidate(); }
        };
    }

    void SyncZoom() { var c = Shell2Ui.Canvas; if (c == null) return; zoom.Value = (int)Math.Clamp(Math.Log(c.V.Zoom / 0.02) / Math.Log(64 / 0.02) * 1000, 0, 1000); }

    void Draw(Graphics g)
    {
        var d = App.Doc; var c = Shell2Ui.Canvas;
        if (d == null) return;
        if (dirty || thumb == null)
        {
            dirty = false;
            scale = Math.Min((view.Width - 8.0) / d.W, (view.Height - 8.0) / d.H);
            if (scale <= 0) return;
            thumb?.Dispose();
            try { thumb = Img.FlattenScaled(d, scale); } catch { thumb = null; }
            off = new Point((view.Width - (int)(d.W * scale)) / 2, (view.Height - (int)(d.H * scale)) / 2);
        }
        if (thumb != null) g.DrawImage(thumb, off.X, off.Y, (int)(d.W * scale), (int)(d.H * scale));
        if (c == null) return;
        // 화면 네 귀퉁이 → 문서 → 축소판
        var pts = new[] { c.V.ToDoc(0, 0), c.V.ToDoc(c.Width, 0), c.V.ToDoc(c.Width, c.Height), c.V.ToDoc(0, c.Height) }
            .Select(p => new PointF((float)(off.X + p.x * scale), (float)(off.Y + p.y * scale))).ToArray();
        using var pen = new Pen(Color.FromArgb(232, 80, 60), 2);
        g.DrawPolygon(pen, pts);
    }

    void MoveTo(Point p)
    {
        var d = App.Doc; var c = Shell2Ui.Canvas; if (d == null || c == null || scale <= 0) return;
        double dx = (p.X - off.X) / scale, dy = (p.Y - off.Y) / scale;
        var (sx, sy) = c.V.ToScreen(dx, dy);
        c.V.PanX += c.Width / 2.0 - sx; c.V.PanY += c.Height / 2.0 - sy;
        c.RedrawAll();
    }
}

public sealed class AssetsPanel : Panel
{
    static readonly List<AssetsPanel> live = new();
    public static void RefreshAll() { foreach (var p in live.ToList()) if (!p.IsDisposed) p.Fill(); }

    readonly ListView list = new() { Dock = DockStyle.Fill, View = View.LargeIcon, BackColor = Opt.Bg, ForeColor = Opt.Fg, BorderStyle = BorderStyle.None, MultiSelect = false };
    readonly ImageList icons = new() { ImageSize = new Size(64, 64), ColorDepth = ColorDepth.Depth32Bit };
    readonly ComboBox folder = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Opt.Bg2, ForeColor = Opt.Fg, FlatStyle = FlatStyle.Flat };

    public AssetsPanel()
    {
        BackColor = Opt.Bg;
        list.LargeImageList = icons;
        var bar = Opt.Row(Opt.Button("넣기", Insert, true), Opt.Button("새로 고침", Fill), Opt.Button("폴더 열기", () => Shell.OpenFolder(MoreCommands.AssetsDir)), Opt.Button("레이어 저장…", () => MoreCommands.AssetSave(false, "레이어 → 에셋")));
        bar.Dock = DockStyle.Bottom;
        Controls.Add(list); Controls.Add(bar); Controls.Add(folder);
        list.ItemActivate += (_, _) => Insert();
        folder.SelectedIndexChanged += (_, _) => FillFiles();
        live.Add(this);
        Disposed += (_, _) => live.Remove(this);
        VisibleChanged += (_, _) => { if (Visible && folder.Items.Count == 0) Fill(); };
    }

    public void Fill()
    {
        string cur = folder.SelectedItem as string;
        folder.Items.Clear();
        folder.Items.Add("(모든 폴더)");
        try
        {
            string root = MoreCommands.AssetsDir;
            foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderBy(x => x)) folder.Items.Add(Path.GetRelativePath(root, d).Replace('\\', '/'));
        }
        catch { }
        folder.SelectedItem = cur != null && folder.Items.Contains(cur) ? cur : "(모든 폴더)";
        FillFiles();
    }

    void FillFiles()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (Image im in icons.Images) im.Dispose();
        icons.Images.Clear();
        try
        {
            string root = MoreCommands.AssetsDir;
            string sel = folder.SelectedItem as string;
            string dir = sel == null || sel == "(모든 폴더)" ? root : Path.Combine(root, sel);
            var files = Directory.GetFiles(dir, "*.*", sel == null || sel == "(모든 폴더)" ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" }.Contains(Path.GetExtension(f).ToLowerInvariant())).Take(400).ToList();
            foreach (var f in files)
            {
                try
                {
                    using var b = ImageIO.LoadBitmap(f);
                    var t = new Bitmap(64, 64);
                    using (var g = Graphics.FromImage(t)) { double s = Math.Min(64.0 / b.Width, 64.0 / b.Height); int w = Math.Max(1, (int)(b.Width * s)), h = Math.Max(1, (int)(b.Height * s)); g.DrawImage(b, (64 - w) / 2, (64 - h) / 2, w, h); }
                    icons.Images.Add(t);
                    list.Items.Add(new ListViewItem(Path.GetFileNameWithoutExtension(f), icons.Images.Count - 1) { Tag = f, ToolTipText = Path.GetRelativePath(root, f) });
                }
                catch { }
            }
            if (files.Count == 0) list.Items.Add(new ListViewItem("(에셋 없음 — 파일 > 에셋 > …저장)"));
        }
        catch { }
        list.EndUpdate();
    }

    void Insert()
    {
        if (App.Doc == null || list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not string f) return;
        try { using var b = ImageIO.LoadBitmap(f); ImageIO.PlaceImage(b, Path.GetFileNameWithoutExtension(f)); }
        catch (Exception e) { App.Toast("넣지 못했습니다: " + e.Message); }
    }
}
