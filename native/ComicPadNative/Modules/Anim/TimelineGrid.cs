// 타임라인 격자: 트랙(줄) × 프레임(칸) + 태그 줄 + 프레임 번호 줄 — 웹 판 ui/anim.js 의 .tl-grid
// 칸: 그림 = 새 그림 시작(썸네일), 옅은 칸 = 앞 그림 이어 보이기, 점선 테두리 = 다른 프레임과 연결된 그림, 빈 칸 = 안 보임(두 번 누르면 새 그림)
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Anim;

public sealed class TimelineGrid : Control
{
    public const int NameW = 110, CellW = 40, TagH = 18, NumH = 20, RowH = 34;
    readonly HScrollBar hs = new() { Dock = DockStyle.Bottom, Height = 14 };
    readonly Dictionary<string, (int ver, Bitmap bmp)> thumbs = new();
    readonly ToolTip tip = new();
    string lastTip;
    int scroll;            // 왼쪽 끝 프레임 위치 (px)
    int lastFrame = -1;
    double[] peaks;

    public TimelineGrid()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Opt.Bg; ForeColor = Opt.Fg;
        Font = new Font("Malgun Gothic", 8.25f);
        Controls.Add(hs);
        hs.Scroll += (_, _) => { scroll = hs.Value; Invalidate(); };
        Action changed = () => { if (!IsDisposed) Sync(); };
        AnimCore.Changed += changed;
        App.LayersChanged += changed;
        App.DocChanged += () => { foreach (var t in thumbs.Values) t.bmp.Dispose(); thumbs.Clear(); peaks = null; if (!IsDisposed) Sync(); };
        Disposed += (_, _) => { AnimCore.Changed -= changed; App.LayersChanged -= changed; };
    }

    static List<Layer> Rows(Document d) { var ts = AnimCore.Tracks(d); ts.Reverse(); return ts; }   // 위에 있는 트랙이 위

    public int WantedHeight { get { var d = App.Doc; return TagH + NumH + Math.Max(1, Rows(d).Count) * RowH + hs.Height + 2; } }

    /// <summary>스크롤 범위 맞추고, 지금 프레임이 보이게</summary>
    public void Sync()
    {
        var d = App.Doc;
        int n = AnimCore.FrameCount(d);
        int total = n * CellW, view = Math.Max(1, Width - NameW);
        hs.Maximum = Math.Max(0, total);
        hs.LargeChange = view;
        hs.SmallChange = CellW;
        hs.Enabled = total > view;
        int f = AnimCore.Frame(d);
        if (f != lastFrame)
        {
            lastFrame = f;
            if (f * CellW < scroll) scroll = f * CellW;
            else if ((f + 1) * CellW > scroll + view) scroll = (f + 1) * CellW - view;
        }
        scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, total - view)));
        if (hs.Value != scroll) hs.Value = Math.Min(scroll, hs.Maximum);
        peaks = !AnimCore.Playing && d != null && Sound.HasAudio(d) ? Sound.FramePeaks(d, AnimCore.Timeline(d)) : null;
        if (Height != WantedHeight && Dock != DockStyle.Fill) Height = WantedHeight;
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) Sync(); }

    Bitmap Thumb(Layer l, Document d)
    {
        if (thumbs.TryGetValue(l.Id, out var t) && t.ver == l.Version) return t.bmp;
        const int TW = 36, TH = 27;
        var bmp = new Bitmap(TW, TH);
        double k = Math.Min((double)TW / d.W, (double)TH / d.H);
        int w = Math.Max(1, (int)(d.W * k)), h = Math.Max(1, (int)(d.H * k)), ox = (TW - w) / 2, oy = (TH - h) / 2;
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Transparent);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint c = l.Px?.Get((int)((x + 0.5) / k), (int)((y + 0.5) / k)) ?? 0;
                bmp.SetPixel(ox + x, oy + y, Color.FromArgb((int)(Px.Over(c, 0xFFFFFFFF) | 0xFF000000)));
            }
        if (thumbs.TryGetValue(l.Id, out var old)) old.bmp.Dispose();
        thumbs[l.Id] = (l.Version, bmp);
        return bmp;
    }

    static Color Hex(string h, Color def) { try { return h == null ? def : ColorTranslator.FromHtml(h); } catch { return def; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Opt.Bg);
        var d = App.Doc;
        if (d == null) return;
        var ts = Rows(d);
        int n = AnimCore.FrameCount(d), f0 = AnimCore.Frame(d);
        if (ts.Count == 0 || n == 0) { TextRenderer.DrawText(g, "애니메이션 트랙이 없습니다", Font, new Point(6, 6), Opt.Muted); return; }
        var cur = AnimCore.AnimFolder(d);
        var a = d.Anim;
        using var sfC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var fg = new SolidBrush(Opt.Fg); using var muted = new SolidBrush(Opt.Muted);
        int x0 = NameW - scroll;
        int f1 = Math.Max(0, scroll / CellW), f2 = Math.Min(n - 1, (scroll + Width - NameW) / CellW + 1);
        g.SetClip(new Rectangle(NameW, 0, Width - NameW, Height - hs.Height));
        // ---- 태그 줄 ----
        var tags = AnimCore.Tags(d).OfType<JsonObject>().ToList();
        var tagRow = new (JsonObject t, int i, bool start)?[n];
        for (int i = 0; i < tags.Count; i++)
        {
            int from = J.I(tags[i]["from"]), to = J.I(tags[i]["to"]);
            for (int f = Math.Max(0, from); f <= Math.Min(n - 1, to); f++) tagRow[f] ??= (tags[i], i, f == from);
        }
        for (int f = f1; f <= f2; f++)
        {
            var r = new Rectangle(x0 + f * CellW, 0, CellW, TagH - 2);
            if (tagRow[f] is { } tg)
            {
                using var b = new SolidBrush(Hex(J.S(tg.t["color"]), Color.SteelBlue));
                g.FillRectangle(b, r);
                if (tg.start) g.DrawString(J.S(tg.t["name"]) ?? "", Font, Brushes.White, r.X + 2, r.Y + 2);
            }
        }
        // ---- 프레임 번호 줄 ----
        var msArr = a["ms"] as JsonArray;
        for (int f = f1; f <= f2; f++)
        {
            var r = new Rectangle(x0 + f * CellW, TagH, CellW, NumH - 2);
            if (f == f0) { using var b = new SolidBrush(Opt.Accent); g.FillRectangle(b, r); }
            g.DrawString((f + 1).ToString() + (msArr != null && f < msArr.Count && J.T(msArr[f]) ? "•" : ""), Font, f == f0 ? Brushes.Black : fg, r, sfC);
            if (peaks != null && f < peaks.Length)
            {
                int hgt = Math.Max(1, (int)Math.Round(peaks[f] * 8));
                using var pb = new SolidBrush(Color.FromArgb(160, 90, 170, 240));
                g.FillRectangle(pb, r.X + 2, r.Bottom - hgt, 3, hgt);
            }
        }
        // ---- 트랙 줄 ----
        int y = TagH + NumH;
        foreach (var t in ts)
        {
            var xs = AnimCore.XsOf(t, d);
            var count = new Dictionary<string, int>();
            for (int f = 0; f < xs.Count; f++) if (xs[f] != null && (f == 0 || xs[f - 1] != xs[f])) count[xs[f]] = count.GetValueOrDefault(xs[f]) + 1;
            var kidNum = d.ChildrenOf(t).Select((c, k) => (c.Id, k + 1)).ToDictionary(p => p.Id, p => p.Item2);
            for (int f = f1; f <= f2; f++)
            {
                string id = f < xs.Count ? xs[f] : null;
                bool start = id != null && (f == 0 || xs[f - 1] != id), end = id != null && (f == n - 1 || f + 1 >= xs.Count || xs[f + 1] != id);
                var r = new Rectangle(x0 + f * CellW + 1, y + 1, CellW - 2, RowH - 3);
                Color bg = id == null ? Color.FromArgb(40, 40, 46) : start ? Color.FromArgb(78, 78, 90) : Color.FromArgb(62, 62, 74);
                if (f == f0 && t != cur) bg = ControlPaint.Light(bg, 0.25f);
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
                if (id != null && !start)
                {
                    // 앞 그림 이어 보이기: 가운데 선
                    using var p = new Pen(Color.FromArgb(120, 120, 135), 2);
                    g.DrawLine(p, r.X - 1, r.Y + r.Height / 2, end ? r.X + r.Width / 2 : r.Right + 1, r.Y + r.Height / 2);
                    if (end) g.FillEllipse(Brushes.Gray, r.X + r.Width / 2 - 3, r.Y + r.Height / 2 - 3, 6, 6);
                }
                if (start)
                {
                    var l = d.ById(id);
                    if (l != null) g.DrawImage(Thumb(l, d), r.X + 1, r.Y + 1);
                    g.DrawString(kidNum.TryGetValue(id, out var kn) ? kn.ToString() : "?", Font, fg, r.X + 1, r.Bottom - 13);
                }
                if (id != null && count.GetValueOrDefault(id) > 1)
                {
                    using var p = new Pen(Color.FromArgb(200, 232, 163, 61)) { DashStyle = DashStyle.Dash };
                    g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
                if (t == cur && f == f0) { using var p = new Pen(Opt.Accent, 2); g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2); }
            }
            y += RowH;
        }
        // ---- 이름 칸 (고정) ----
        g.ResetClip();
        using (var nb = new SolidBrush(Opt.Bg2)) g.FillRectangle(nb, 0, 0, NameW, Height - hs.Height);
        g.DrawString("태그", Font, muted, 4, 2);
        g.DrawString("프레임", Font, muted, 4, TagH + 3);
        y = TagH + NumH;
        foreach (var t in ts)
        {
            var r = new Rectangle(0, y + 1, NameW - 2, RowH - 3);
            if (t == cur) { using var b = new SolidBrush(Color.FromArgb(90, 232, 163, 61)); g.FillRectangle(b, r); }
            g.DrawString(t.Visible ? "●" : "○", Font, t.Visible ? fg : muted, 3, y + 9);
            var nr = new RectangleF(20, y + 1, NameW - 22, RowH - 3);
            using var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(t.Name, Font, fg, nr, sf);
            y += RowH;
        }
    }

    (string what, Layer track, int frame) Hit(Point p)
    {
        var d = App.Doc;
        if (d == null) return (null, null, -1);
        var ts = Rows(d);
        int n = AnimCore.FrameCount(d);
        int f = p.X >= NameW ? (p.X - NameW + scroll) / CellW : -1;
        if (f >= n) f = -1;
        if (p.Y < TagH) return ("tag", null, f);
        if (p.Y < TagH + NumH) return ("num", null, f);
        int row = (p.Y - TagH - NumH) / RowH;
        if (row < 0 || row >= ts.Count) return (null, null, f);
        if (p.X < NameW) return (p.X < 18 ? "eye" : "name", ts[row], f);
        return ("cell", ts[row], f);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var d = App.Doc;
        if (d == null) return;
        if (AnimCore.Playing) Playback.Stop();
        var (what, t, f) = Hit(e.Location);
        switch (what)
        {
            case "tag":
                if (f >= 0 && e.Button == MouseButtons.Left)
                {
                    var tags = AnimCore.Tags(d).OfType<JsonObject>().ToList();
                    int ti = tags.FindIndex(x => J.I(x["from"]) <= f && f <= J.I(x["to"]));
                    if (ti >= 0) AnimDialogs.Tag(ti);
                }
                break;
            case "num": if (f >= 0) AnimCore.SetFrame(f); break;
            case "eye":
                App.History.Struct(d, "트랙 보이기", () => t.Visible = !t.Visible);
                App.NotifyLayers(); App.InvalidateAll();
                break;
            case "name": AnimCore.SelectCell(t, AnimCore.Frame(d)); break;
            case "cell":
                if (f < 0) break;
                AnimCore.SelectCell(t, f);
                if (e.Button == MouseButtons.Right) CellMenu(e.Location);
                break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var (what, t, f) = Hit(e.Location);
        var d = App.Doc;
        if (what == "cell" && f >= 0)
        {
            var xs = AnimCore.XsOf(t, d);
            if (f >= xs.Count || xs[f] == null) AnimCore.NewCelHere(t, f);
        }
        else if (what == "name")
        {
            // 트랙 이름 바꾸기
            var v = Ui.Form("트랙 이름", new[] { Ui.Field.Txt("name", "이름", t.Name) });
            if (v != null && !string.IsNullOrWhiteSpace(v.S("name"))) { App.History.Struct(d, "이름 바꾸기", () => t.Name = v.S("name")); App.NotifyLayers(); }
        }
    }

    void CellMenu(Point at)
    {
        var m = new ContextMenuStrip();
        void Add(string text, Action run) { var it = new ToolStripMenuItem(text); it.Click += (_, _) => MainForm.Run(run, text); m.Items.Add(it); }
        Add("빈 프레임 추가", () => AnimCore.AddFrame("blank"));
        Add("현재 프레임 복제", () => AnimCore.AddFrame("copy"));
        Add("연결된 프레임 추가 (같은 그림 이어 쓰기)", () => AnimCore.AddFrame("link"));
        m.Items.Add(new ToolStripSeparator());
        Add("칸 비우기", AnimCore.ClearCell);
        Add("칸 연결 끊기 (따로 고치기)", AnimCore.UnlinkCell);
        Add("라이트 테이블에 올리기/내리기 (지금 칸)", AnimCore.ToggleLight);
        // 같은 트랙의 다른 그림으로 연결
        var d = App.Doc; var t = AnimCore.AnimFolder(d);
        if (t != null)
        {
            var link = new ToolStripMenuItem("이 칸을 다른 그림으로 연결");
            int k = 0;
            foreach (var c in d.ChildrenOf(t)) { var id = c.Id; var it = new ToolStripMenuItem($"{++k}번 · {c.Name}"); it.Click += (_, _) => AnimCore.LinkCellTo(id); link.DropDownItems.Add(it); }
            if (link.DropDownItems.Count > 0) m.Items.Add(link);
        }
        m.Items.Add(new ToolStripSeparator());
        Add("프레임 삭제", AnimCore.DeleteFrame);
        m.Show(this, at);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var d = App.Doc;
        var (what, t, f) = Hit(e.Location);
        string s = null;
        if (d != null && f >= 0)
        {
            if (what == "num") s = $"{Math.Round(AnimCore.FrameMs(f, d))}ms";
            else if (what == "cell")
            {
                var xs = AnimCore.XsOf(t, d);
                string id = f < xs.Count ? xs[f] : null;
                if (id == null) s = "빈 칸 (두 번 누르면 새 그림)";
                else
                {
                    int num = d.ChildrenOf(t).FindIndex(c => c.Id == id) + 1;
                    int runs = 0; for (int i = 0; i < xs.Count; i++) if (xs[i] == id && (i == 0 || xs[i - 1] != id)) runs++;
                    s = $"{(num > 0 ? num.ToString() : "?")}번 그림{(runs > 1 ? " · 다른 프레임과 연결됨" : "")}";
                }
            }
            else if (what == "tag")
            {
                var tg = AnimCore.Tags(d).OfType<JsonObject>().FirstOrDefault(x => J.I(x["from"]) <= f && f <= J.I(x["to"]));
                if (tg != null) { string dir = J.S(tg["dir"]) ?? "fwd"; s = $"{J.S(tg["name"])} ({J.I(tg["from"]) + 1}~{J.I(tg["to"]) + 1}, {(dir == "rev" ? "역방향" : dir == "ping" ? "핑퐁" : "정방향")})"; }
            }
        }
        if (what == "eye") s = "보이기/숨기기";
        if (s != lastTip) { lastTip = s; tip.SetToolTip(this, s ?? ""); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scroll = Math.Max(0, Math.Min(Math.Max(0, hs.Maximum - hs.LargeChange + 1), scroll - Math.Sign(e.Delta) * CellW * 2));
        hs.Value = Math.Max(hs.Minimum, Math.Min(scroll, hs.Maximum));
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { foreach (var t in thumbs.Values) t.bmp.Dispose(); thumbs.Clear(); }
        base.Dispose(disposing);
    }
}
