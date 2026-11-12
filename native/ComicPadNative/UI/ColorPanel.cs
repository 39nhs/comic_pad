// 색 패널: 채도·명도 사각형 + 색상 막대, 전경/배경색(X 로 바꾸기), 최근 색, 팔레트(설정 palette), HEX 입력.
using ComicPadNative.Core;
using System.Text.Json.Nodes;

namespace ComicPadNative.UI;

public sealed class ColorPanel : UserControl
{
    double h, s, v;
    readonly PictureBox sv = new() { Width = 220, Height = 160, Cursor = Cursors.Cross };
    readonly PictureBox hue = new() { Width = 220, Height = 18, Cursor = Cursors.Hand };
    readonly Panel fg = new() { Width = 40, Height = 40, BorderStyle = BorderStyle.FixedSingle }, bgc = new() { Width = 30, Height = 30, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox hex = new() { Width = 90, BackColor = Opt.Bg2, ForeColor = Opt.Fg, BorderStyle = BorderStyle.FixedSingle };
    readonly FlowLayoutPanel swatches = new() { Width = 260, AutoSize = true, MaximumSize = new Size(260, 0), BackColor = Opt.Bg };
    readonly FlowLayoutPanel recent = new() { Width = 260, AutoSize = true, MaximumSize = new Size(260, 0), BackColor = Opt.Bg };
    bool dragging;

    static readonly string[] DefaultPalette = { "#000000", "#ffffff", "#3a3a3a", "#7a7a7a", "#bdbdbd", "#3a7bd5", "#e04646", "#f2a93b", "#f7e15a", "#4caf50", "#8e5bd6", "#f4c7b8" };

    public ColorPanel()
    {
        BackColor = Opt.Bg; AutoScroll = true;
        var p = Opt.Panel();
        p.Controls.Add(sv); p.Controls.Add(hue);
        var swapBtn = Opt.Button("⇄ (X)", () => { (App.Fg, App.Bg) = (App.Bg, App.Fg); App.SetColor(App.Fg); });
        p.Controls.Add(Opt.Row(fg, bgc, swapBtn, hex));
        p.Controls.Add(Opt.Title("최근 색")); p.Controls.Add(recent);
        p.Controls.Add(Opt.Title("팔레트")); p.Controls.Add(swatches);
        p.Controls.Add(Opt.Button("＋ 지금 색을 팔레트에", () => { Palette().Add(Px.ToHex(App.Fg)); App.SaveSettingsSoon(); BuildSwatches(); }));
        Controls.Add(p);

        sv.Paint += (_, e) => PaintSV(e.Graphics);
        hue.Paint += (_, e) => PaintHue(e.Graphics);
        sv.MouseDown += (_, e) => { dragging = true; PickSV(e.Location); };
        sv.MouseMove += (_, e) => { if (dragging) PickSV(e.Location); };
        sv.MouseUp += (_, _) => { dragging = false; AddRecent(); };
        hue.MouseDown += (_, e) => { dragging = true; PickHue(e.X); };
        hue.MouseMove += (_, e) => { if (dragging) PickHue(e.X); };
        hue.MouseUp += (_, _) => { dragging = false; AddRecent(); };
        bgc.Click += (_, _) => { (App.Fg, App.Bg) = (App.Bg, App.Fg); App.SetColor(App.Fg); };
        hex.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { App.SetColor(Px.FromHex(hex.Text, App.Fg)); AddRecent(); e.SuppressKeyPress = true; } };
        App.ColorChanged += Sync;
        BuildSwatches(); BuildRecent(); Sync();
    }

    JsonArray Palette()
    {
        if (App.Settings["palette"] is not JsonArray a) { a = new JsonArray(DefaultPalette.Select(x => (JsonNode)x).ToArray()); App.Settings["palette"] = a; }
        return a;
    }

    void BuildSwatches()
    {
        swatches.Controls.Clear();
        foreach (var n in Palette())
        {
            string hx = (string)n;
            var b = new Panel { Width = 22, Height = 22, Margin = new Padding(1), BackColor = Color.FromArgb((int)(Px.Unpremul(Px.FromHex(hx)) | 0xFF000000)), Cursor = Cursors.Hand };
            b.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Right) { Palette().Remove(Palette().FirstOrDefault(x => (string)x == hx)); App.SaveSettingsSoon(); BuildSwatches(); return; }
                App.SetColor(Px.FromHex(hx));
            };
            swatches.Controls.Add(b);
        }
    }

    void BuildRecent()
    {
        recent.Controls.Clear();
        if (App.Settings["recentColors"] is not JsonArray a) return;
        foreach (var n in a)
        {
            string hx = (string)n;
            var b = new Panel { Width = 18, Height = 18, Margin = new Padding(1), BackColor = Color.FromArgb((int)(Px.FromHex(hx) | 0xFF000000)), Cursor = Cursors.Hand };
            b.Click += (_, _) => App.SetColor(Px.FromHex(hx));
            recent.Controls.Add(b);
        }
    }

    void AddRecent()
    {
        if (App.Settings["recentColors"] is not JsonArray a) { a = new JsonArray(); App.Settings["recentColors"] = a; }
        string hx = Px.ToHex(App.Fg);
        var ex = a.FirstOrDefault(x => (string)x == hx);
        if (ex != null) a.Remove(ex);
        a.Insert(0, hx);
        while (a.Count > 18) a.RemoveAt(a.Count - 1);
        App.SaveSettingsSoon(); BuildRecent();
    }

    void Sync()
    {
        uint c = Px.Unpremul(App.Fg);
        double r = ((c >> 16) & 255) / 255.0, g = ((c >> 8) & 255) / 255.0, b = (c & 255) / 255.0;
        var (nh, ns, nv) = Px.RgbToHsv(r, g, b);
        if (ns > 0 && nv > 0) h = nh;
        s = ns; v = nv;
        fg.BackColor = Color.FromArgb((int)(c | 0xFF000000));
        bgc.BackColor = Color.FromArgb((int)(Px.Unpremul(App.Bg) | 0xFF000000));
        if (!hex.Focused) hex.Text = Px.ToHex(App.Fg);
        sv.Invalidate(); hue.Invalidate();
    }

    void Set()
    {
        var (r, g, b) = Px.HsvToRgb(h, s, v);
        App.SetColor(Px.Pack(255, (uint)(r * 255 + 0.5), (uint)(g * 255 + 0.5), (uint)(b * 255 + 0.5)));
    }
    void PickSV(Point p) { s = Math.Clamp(p.X / (double)(sv.Width - 1), 0, 1); v = 1 - Math.Clamp(p.Y / (double)(sv.Height - 1), 0, 1); Set(); }
    void PickHue(int x) { h = Math.Clamp(x / (double)(hue.Width - 1), 0, 1) * 360; Set(); }

    void PaintSV(Graphics g)
    {
        using var bmp = new Bitmap(sv.Width, sv.Height);
        for (int y = 0; y < sv.Height; y++)
            for (int x = 0; x < sv.Width; x++)
            {
                var (r, gg, b) = Px.HsvToRgb(h, x / (double)(sv.Width - 1), 1 - y / (double)(sv.Height - 1));
                bmp.SetPixel(x, y, Color.FromArgb((int)(r * 255), (int)(gg * 255), (int)(b * 255)));
            }
        g.DrawImage(bmp, 0, 0);
        int cx = (int)(s * (sv.Width - 1)), cy = (int)((1 - v) * (sv.Height - 1));
        g.DrawEllipse(Pens.White, cx - 5, cy - 5, 10, 10); g.DrawEllipse(Pens.Black, cx - 6, cy - 6, 12, 12);
    }
    void PaintHue(Graphics g)
    {
        for (int x = 0; x < hue.Width; x++)
        {
            var (r, gg, b) = Px.HsvToRgb(x / (double)(hue.Width - 1) * 360, 1, 1);
            using var p = new Pen(Color.FromArgb((int)(r * 255), (int)(gg * 255), (int)(b * 255)));
            g.DrawLine(p, x, 0, x, hue.Height);
        }
        int hx = (int)(h / 360 * (hue.Width - 1));
        g.DrawRectangle(Pens.White, hx - 2, 0, 4, hue.Height - 1);
    }
}
