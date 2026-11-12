// 색 패널 (웹 ui/panels.js colorPanel 과 같은 모양): 전경/배경 칩 + 바꾸기 + HEX, 채도·명도 사각형, 색상 막대,
// H S V R G B 막대, 최근 색, 팔레트(설정 palette, 오른쪽 클릭·길게 누르기 = 빼기, + = 지금 색 더하기).
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI.Controls;
using ComicPadNative.UI.Dialogs;

namespace ComicPadNative.UI;

public sealed class ColorPanel : UserControl
{
    double h, s, v;
    readonly WebStack root = new() { Padding = new Padding(10, 4, 10, 12) };
    readonly Chips chips;
    readonly WebInput hex = new("") { Width = 92 };
    readonly WebCanvas sv = new() { Height = 150, Cursor = Cursors.Cross, Margin = Padding.Empty };
    readonly WebCanvas hue = new() { Height = 26, Cursor = Cursors.Hand, Margin = new Padding(0, 8, 0, 0) };
    readonly List<WebSlider> sliders = new();
    readonly SwatchGrid recent, palette;
    bool dragging;

    static readonly string[] DefaultPalette = { "#000000", "#ffffff", "#3a3a3a", "#7a7a7a", "#bdbdbd", "#3a7bd5", "#e04646", "#f2a93b", "#f7e15a", "#4caf50", "#8e5bd6", "#f4c7b8" };

    public ColorPanel()
    {
        BackColor = Theme.Panel; AutoScroll = true; Font = Web.Body;
        this.DarkScroll();
        chips = new Chips();
        hex.Box.Font = new Font("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        hex.Box.MaxLength = 7;
        hex.Inline();
        var swap = Opt.IconButton("swap", "바꾸기 (X)", Swap);
        var top = new WebRow { Gap = 8, Margin = new Padding(0, 6, 0, 8) };
        top.Controls.Add(chips); top.Controls.Add(swap); top.Controls.Add(hex);
        root.Controls.Add(top);
        sv.Stretch(); hue.Stretch();
        root.Controls.Add(sv); root.Controls.Add(hue);
        // H S V R G B (웹 .color-sliders: 막대 높이 28)
        (uint r, uint g, uint b) Rgb() { uint c = Px.Unpremul(App.Fg); return ((c >> 16) & 255, (c >> 8) & 255, c & 255); }
        void SetRgb(int k, double x) { var (r, g, b) = Rgb(); uint n = (uint)Math.Round(x); if (k == 0) r = n; else if (k == 1) g = n; else b = n; App.SetColor(Px.Pack(255, r, g, b)); }
        void SetHsv(int k, double x) { if (k == 0) h = x; else if (k == 1) s = x / 100; else v = x / 100; Commit(); }
        WebSlider S(string label, double max, Func<double> get, Action<double> set, Func<double, string> fmt)
        {
            var sl = new WebSlider(label, 0, max, get, set, 1, false, fmt) { TrackHeight = 28, Height = 28 };
            sl.OnEnd = AddRecent;
            sliders.Add(sl); root.Controls.Add(sl);
            return sl;
        }
        S("H", 359, () => Math.Round(h), x => SetHsv(0, x), x => $"{Math.Round(x)}°");
        S("S", 100, () => Math.Round(s * 100), x => SetHsv(1, x), x => $"{Math.Round(x)}%");
        S("V", 100, () => Math.Round(v * 100), x => SetHsv(2, x), x => $"{Math.Round(x)}%");
        S("R", 255, () => Rgb().r, x => SetRgb(0, x), null);
        S("G", 255, () => Rgb().g, x => SetRgb(1, x), null);
        S("B", 255, () => Rgb().b, x => SetRgb(2, x), null);
        root.Controls.Add(new SectionLabel("최근 색") { });
        recent = new SwatchGrid(() => (App.Settings["recentColors"] as JsonArray)?.Select(n => (string)n).ToList() ?? new(), false);
        recent.Picked += c => { App.SetColor(Px.FromHex(c)); };
        root.Controls.Add(recent);
        root.Controls.Add(new SectionLabel("팔레트 (길게 누르면 삭제)"));
        palette = new SwatchGrid(() => Palette().Select(n => (string)n).ToList(), true);
        palette.Picked += c => { App.SetColor(Px.FromHex(c)); AddRecent(); };
        palette.Remove += i =>
        {
            var a = Palette(); if (i < 0 || i >= a.Count) return;
            if (!Modals.Confirm($"{(string)a[i]} 색을 팔레트에서 뺄까요?", "빼기")) return;
            a.RemoveAt(i); App.SaveSettingsSoon(); palette.Refresh2();
        };
        palette.Add += () => { Palette().Add(Px.ToHex(App.Fg)); App.SaveSettingsSoon(); palette.Refresh2(); };
        root.Controls.Add(palette);
        Controls.Add(root);

        sv.Paint += (_, e) => PaintSV(e.Graphics);
        hue.Paint += (_, e) => PaintHue(e.Graphics);
        sv.MouseDown += (_, e) => { dragging = true; sv.Capture = true; PickSV(e.Location); };
        sv.MouseMove += (_, e) => { if (dragging) PickSV(e.Location); };
        sv.MouseUp += (_, _) => { dragging = false; AddRecent(); };
        hue.MouseDown += (_, e) => { dragging = true; hue.Capture = true; PickHue(e.X); };
        hue.MouseMove += (_, e) => { if (dragging) PickHue(e.X); };
        hue.MouseUp += (_, _) => { dragging = false; AddRecent(); };
        chips.BgClicked += Swap;
        hex.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplyHex(); e.SuppressKeyPress = true; } };
        hex.Box.LostFocus += (_, _) => ApplyHex();
        App.ColorChanged += Sync;
        App.SettingsChanged += () => { if (!IsDisposed) { palette.Refresh2(); recent.Refresh2(); } };
        Sync();
    }

    void Swap() { (App.Fg, App.Bg) = (App.Bg, App.Fg); App.SetColor(App.Fg); }
    void ApplyHex()
    {
        string t = hex.Text.Trim(); if (!t.StartsWith('#')) t = "#" + t;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, "^#[0-9a-fA-F]{6}$") && t.ToLowerInvariant() != Px.ToHex(App.Fg)) { App.SetColor(Px.FromHex(t.ToLowerInvariant())); AddRecent(); }
    }

    JsonArray Palette()
    {
        if (App.Settings["palette"] is not JsonArray a) { a = new JsonArray(DefaultPalette.Select(x => (JsonNode)x).ToArray()); App.Settings["palette"] = a; }
        return a;
    }

    void AddRecent()
    {
        if (App.Settings["recentColors"] is not JsonArray a) { a = new JsonArray(); App.Settings["recentColors"] = a; }
        string hx = Px.ToHex(App.Fg);
        var ex = a.FirstOrDefault(x => (string)x == hx);
        if (ex != null) a.Remove(ex);
        a.Insert(0, hx);
        while (a.Count > 18) a.RemoveAt(a.Count - 1);
        App.SaveSettingsSoon(); recent.Refresh2();
    }

    void Sync()
    {
        if (IsDisposed) return;
        uint c = Px.Unpremul(App.Fg);
        double r = ((c >> 16) & 255) / 255.0, g = ((c >> 8) & 255) / 255.0, b = (c & 255) / 255.0;
        var (nh, ns, nv) = Px.RgbToHsv(r, g, b);
        if (!dragging || ns > 0.001 && nv > 0.001) { if (ns > 0.001 && nv > 0.001) h = nh; s = ns; v = nv; }
        chips.Invalidate();
        if (!hex.Box.Focused) hex.Text = Px.ToHex(App.Fg);
        sv.Invalidate(); hue.Invalidate();
        foreach (var sl in sliders) if (!sl.Capture) sl.Sync();
    }

    void Commit()
    {
        var (r, g, b) = Px.HsvToRgb(h, s, v);
        double hh = h, ss = s, vv = v;
        App.SetColor(Px.Pack(255, (uint)(r * 255 + 0.5), (uint)(g * 255 + 0.5), (uint)(b * 255 + 0.5)));
        h = hh; s = ss; v = vv;   // 무채색일 때도 고른 값 유지
        sv.Invalidate(); hue.Invalidate();
        foreach (var sl in sliders) if (!sl.Capture) sl.Sync();
    }
    void PickSV(Point p) { s = Math.Clamp(p.X / (double)Math.Max(1, sv.Width), 0, 1); v = 1 - Math.Clamp(p.Y / (double)Math.Max(1, sv.Height), 0, 1); Commit(); }
    void PickHue(int x) { h = Math.Clamp(x / (double)Math.Max(1, hue.Width), 0, 1) * 359.9; Commit(); }

    void PaintSV(Graphics g)
    {
        g.Clear(Theme.Panel);
        var r = new Rectangle(0, 0, sv.Width, sv.Height);
        if (r.Width < 2) return;
        var st = g.Save();
        using (var path = Theme.Round(r, 8)) { Web.Smooth(g); g.SetClip(path); }
        var (hr, hg, hb) = Px.HsvToRgb(h, 1, 1);
        using (var b = new SolidBrush(Color.FromArgb((int)(hr * 255), (int)(hg * 255), (int)(hb * 255)))) g.FillRectangle(b, r);
        using (var wb = new LinearGradientBrush(new Rectangle(-1, 0, r.Width + 2, r.Height), Color.White, Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Horizontal)) g.FillRectangle(wb, r);
        using (var kb = new LinearGradientBrush(new Rectangle(0, -1, r.Width, r.Height + 2), Color.FromArgb(0, 0, 0, 0), Color.Black, LinearGradientMode.Vertical)) g.FillRectangle(kb, r);
        g.Restore(st);
        Web.Smooth(g);
        float px = (float)(s * r.Width), py = (float)((1 - v) * r.Height);
        using var pen = new Pen(v > 0.5 ? Color.Black : Color.White, 2);
        g.DrawEllipse(pen, px - 7, py - 7, 14, 14);
    }
    void PaintHue(Graphics g)
    {
        g.Clear(Theme.Panel);
        var r = new Rectangle(0, 0, hue.Width, hue.Height);
        if (r.Width < 2) return;
        var st = g.Save();
        using (var path = Theme.Round(r, 6)) { Web.Smooth(g); g.SetClip(path); }
        using (var b = new LinearGradientBrush(new Rectangle(0, 0, r.Width, r.Height), Color.Red, Color.Red, LinearGradientMode.Horizontal))
        {
            var cb = new ColorBlend(7) { Colors = new[] { Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta, Color.Red }, Positions = new[] { 0f, 1 / 6f, 2 / 6f, 3 / 6f, 4 / 6f, 5 / 6f, 1f } };
            b.InterpolationColors = cb;
            g.FillRectangle(b, r);
        }
        g.Restore(st);
        float x = (float)(h / 360 * r.Width);
        using var p = new Pen(Color.White, 3);
        g.DrawRectangle(p, x - 3, 1.5f, 6, r.Height - 3);
    }

    /// <summary>전경/배경 큰 칩 (.chips-big)</summary>
    sealed class Chips : WebCanvas
    {
        public event Action BgClicked;
        public Chips() { Size = new Size(52, 44); Cursor = Cursors.Hand; Margin = Padding.Empty; Paint += (_, e) => Draw(e.Graphics); }
        static Color C(uint premul) { uint c = Px.Unpremul(premul); return Color.FromArgb(255, (int)(c >> 16) & 255, (int)(c >> 8) & 255, (int)c & 255); }
        static void Chip(Graphics g, Rectangle r, Color c)
        {
            Web.Box(g, Rectangle.Inflate(r, 1, 1), 7, Color.Black);
            Web.Box(g, r, 6, c, Color.FromArgb(0xdd, 0xdd, 0xdd), 2);
        }
        void Draw(Graphics g)
        {
            g.Clear(Web.ParentBack(this));
            Chip(g, new Rectangle(Width - 33, Height - 33, 32, 32), C(App.Bg));
            Chip(g, new Rectangle(1, 1, 32, 32), C(App.Fg));
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.X > 32 || e.Y > 32) BgClicked?.Invoke(); }
    }
}

/// <summary>색 칸 격자 (.swatches: 칸 최소 30px, 간격 4, 정사각형, 둥근 6px). add=true 면 끝에 + 칸</summary>
public sealed class SwatchGrid : Control, IWebBlock
{
    readonly Func<List<string>> list;
    readonly bool addButton;
    public event Action<string> Picked;
    public event Action<int> Remove;
    public event Action Add;
    int hot = -1, pressIdx = -1;
    readonly System.Windows.Forms.Timer longPress = new() { Interval = 600 };
    bool longFired;
    const int Min = 30, Gap = 4;

    public SwatchGrid(Func<List<string>> list, bool addButton)
    {
        this.list = list; this.addButton = addButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Margin = Padding.Empty; Cursor = Cursors.Hand;
        longPress.Tick += (_, _) => { longPress.Stop(); if (pressIdx >= 0 && Remove != null) { longFired = true; Remove(pressIdx); } };
    }
    int N => list().Count + (addButton ? 1 : 0);
    int Cols(int w) => Math.Max(1, (w + Gap) / (Min + Gap));
    int Cell(int w) => (w - (Cols(w) - 1) * Gap) / Cols(w);
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 280;
        int n = N, rows = (n + Cols(w) - 1) / Cols(w);
        return new Size(w, rows == 0 ? 0 : rows * Cell(w) + (rows - 1) * Gap);
    }
    public void Refresh2() { var p = Parent; if (p != null && Height != GetPreferredSize(new Size(Width, 0)).Height) p.PerformLayout(); Invalidate(); }
    Rectangle R(int i) { int c = Cell(Width), n = Cols(Width); return new Rectangle(i % n * (c + Gap), i / n * (c + Gap), c, c); }
    int Hit(Point p) { for (int i = 0; i < N; i++) if (R(i).Contains(p)) return i; return -1; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        var l = list();
        for (int i = 0; i < l.Count; i++)
        {
            var r = R(i);
            Web.Box(g, r, 6, WebSwatch.ColorOf(l[i]), Color.Black);
            Web.Box(g, Rectangle.Inflate(r, -1, -1), 5, null, i == hot ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(38, 255, 255, 255));
        }
        if (addButton)
        {
            var r = R(l.Count);
            Web.Box(g, r, 6, l.Count == hot ? Theme.Panel3 : Theme.Panel2, Color.Black);
            Icons.Draw(g, "plus", new RectangleF(r.X + (r.Width - 16) / 2f, r.Y + (r.Height - 16) / 2f, 16, 16), Theme.Text);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != hot) { hot = h; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; longPress.Stop(); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        pressIdx = Hit(e.Location); longFired = false;
        if (pressIdx >= 0 && pressIdx < list().Count && e.Button == MouseButtons.Left) longPress.Start();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        longPress.Stop();
        int i = Hit(e.Location);
        if (i < 0 || longFired) return;
        var l = list();
        if (i == l.Count && addButton) { if (e.Button == MouseButtons.Left) Add?.Invoke(); return; }
        if (i >= l.Count) return;
        if (e.Button == MouseButtons.Right) Remove?.Invoke(i);
        else if (e.Button == MouseButtons.Left) Picked?.Invoke(l[i]);
    }
    protected override void Dispose(bool disposing) { if (disposing) longPress.Dispose(); base.Dispose(disposing); }
}
