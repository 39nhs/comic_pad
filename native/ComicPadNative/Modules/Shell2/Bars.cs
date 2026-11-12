// 떠 있는 퀵바 (웹 ui/quickbar.js) + 아래 큰 버튼 막대 (웹 ui/bottombar.js).
//  - settings.quickbar = { visible, x, y, items }, settings.bottombar = { visible, items } (웹 판과 같은 키·모양)
//  - 퀵바: 그림판 위에 떠 있음, 손잡이를 끌어 옮기고 탭하면 버튼 고르기. 크기·불투명도는 세로 막대
using System.Text.Json.Nodes;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

/// <summary>세로 막대 (위로 갈수록 큼). 로그 눈금 가능</summary>
public sealed class VSlider : Control
{
    readonly string label; readonly Func<double> get; readonly Action<double> set; readonly double min, max; readonly bool log; readonly Func<double, string> fmt;
    public VSlider(string label, Func<double> get, Action<double> set, double min, double max, bool log, Func<double, string> fmt)
    {
        this.label = label; this.get = get; this.set = set; this.min = min; this.max = max; this.log = log; this.fmt = fmt;
        DoubleBuffered = true; Size = new Size(40, 130); Margin = new Padding(2, 4, 2, 4); Cursor = Cursors.SizeNS;
        new ToolTip().SetToolTip(this, label);
    }
    double ToT(double v) => log ? Math.Log(v / min) / Math.Log(max / min) : (v - min) / (max - min);
    double FromT(double t) => log ? min * Math.Pow(max / min, t) : min + (max - min) * t;
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Opt.Bg);
        var track = new Rectangle(Width / 2 - 8, 18, 16, Height - 22);
        using (var b = new SolidBrush(Opt.Bg2)) g.FillRectangle(b, track);
        double t = Math.Clamp(ToT(get()), 0, 1);
        int fh = (int)(track.Height * t);
        using (var b = new SolidBrush(Opt.Accent)) g.FillRectangle(b, track.X, track.Bottom - fh, track.Width, fh);
        TextRenderer.DrawText(g, fmt(get()), Font, new Rectangle(0, 0, Width, 16), Opt.Fg, TextFormatFlags.HorizontalCenter);
    }
    bool down; int y0; double t0;
    protected override void OnMouseDown(MouseEventArgs e) { down = true; y0 = e.Y; t0 = ToT(get()); Apply(e.Y, true); Capture = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (down) Apply(e.Y, false); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Capture = false; PresetStore.Save(); }
    void Apply(int y, bool jump)
    {
        int top = 18, h = Height - 22;
        double t = Math.Clamp(1 - (y - top) / (double)h, 0, 1);
        set(FromT(t)); Invalidate(); App.NotifySettings();
    }
}

public static class Bars
{
    public static readonly string[] DefaultQuick = { "undo", "redo", "penEraser", "eyedropper", "size", "opacity", "fit", "flip", "deselect" };
    public static readonly string[] DefaultBottom = { "undo", "redo", "brushTool", "penEraser", "eyedropper", "hand", "size", "opacity", "deselect", "transform", "newLayer", "save" };

    static JsonObject QConf() { var q = Shell2Ui.Obj(App.Settings, "quickbar"); if (q["x"] == null) { q["x"] = 12; q["y"] = 80; } if (q["visible"] == null) q["visible"] = true; return q; }   // 웹 판 기본값과 같이 보임
    static JsonObject BConf() { var c = Shell2Ui.Obj(App.Settings, "bottombar"); if (c["visible"] == null) c["visible"] = true; return c; }
    static string[] Items(JsonObject c, string[] def) => c["items"] is JsonArray a ? a.Select(x => (string)x).Where(x => x != null).ToArray() : def;
    static bool Large => App.Get("largeUI", false);

    static Panel quick; static FlowLayoutPanel bottom; static bool placeHooked;
    static readonly List<(Button b, QuickAction a)> buttons = new();
    static readonly List<Control> sliders = new();

    public static void Attach()
    {
        App.ToolChanged += Sync; App.ColorChanged += Sync; App.History.Changed += Sync; App.SettingsChanged += Sync;
        RenderQuick(); RenderBottom();
    }

    static Control Slider(string type)
    {
        bool sz = type == "size";
        var s = new VSlider(sz ? "크기" : "불투명도",
            () => { var (p, _) = Shell2Ui.ActiveBrush(); return p == null ? 1 : sz ? p.Size : p.Opacity; },
            v => { var (p, _) = Shell2Ui.ActiveBrush(); if (p == null) return; if (sz) p.Size = Math.Max(0.5, Math.Round(v * 2) / 2); else p.Opacity = Math.Round(v * 100) / 100; },
            sz ? 0.5 : 0.01, sz ? 600 : 1, sz, v => sz ? (v < 10 ? v.ToString("0.0") : Math.Round(v).ToString()) : $"{Math.Round(v * 100)}%");
        sliders.Add(s);
        return s;
    }

    static Button Btn(QuickAction a, bool withLabel)
    {
        int sz = Large ? 52 : 40;
        var b = new Button { Text = withLabel ? a.Icon + "\n" + a.Label : a.Icon, Size = withLabel ? new Size(Large ? 78 : 64, sz + 8) : new Size(sz, sz), FlatStyle = FlatStyle.Flat, BackColor = Opt.Bg2, ForeColor = Opt.Fg, Margin = new Padding(2), Font = new Font("Segoe UI Emoji", withLabel ? 8.5f : 13f), TabStop = false };
        b.FlatAppearance.BorderColor = Color.FromArgb(74, 74, 84);
        if (withLabel) { b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowOnly; b.MinimumSize = b.Size; b.Padding = new Padding(2, 0, 2, 0); }
        new ToolTip().SetToolTip(b, a.Label);
        b.Click += (_, _) => { MainForm.Run(a.Run, a.Label); Sync(); App.Main?.ActiveControl?.Focus(); Shell2Ui.Canvas?.Focus(); };
        buttons.Add((b, a));
        return b;
    }

    // ---- 퀵바 -----------------------------------------------------------------------------
    public static void RenderQuick()
    {
        var canvas = Shell2Ui.Canvas; if (canvas == null) return;
        var q = QConf();
        if (quick != null) { buttons.RemoveAll(x => quick.Contains(x.b)); sliders.RemoveAll(quick.Contains); quick.Dispose(); quick = null; }
        if (q["visible"]?.GetValue<bool>() != true) return;
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Opt.Bg, Padding = new Padding(3) };
        var grip = new Label { Text = "⠿", AutoSize = false, Size = new Size(Large ? 52 : 40, 18), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Opt.Muted, Cursor = Cursors.SizeAll, Margin = new Padding(2, 0, 2, 2) };
        new ToolTip().SetToolTip(grip, "끌어서 이동 · 탭: 버튼 편집");
        flow.Controls.Add(grip);
        foreach (var id in Items(q, DefaultQuick))
        {
            var a = QuickActions.Get(id); if (a == null) continue;
            if (a.Type is "size" or "opacity") flow.Controls.Add(Slider(a.Type)); else flow.Controls.Add(Btn(a, false));
        }
        quick = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Opt.Bg, BorderStyle = BorderStyle.FixedSingle };
        quick.Controls.Add(flow);
        canvas.Controls.Add(quick);
        Place();
        if (!placeHooked) { placeHooked = true; canvas.Resize += (_, _) => Place(); }
        Point s0 = default; int ox = 0, oy = 0; bool moved = false, dragging = false;
        grip.MouseDown += (_, e) => { dragging = true; moved = false; s0 = Control.MousePosition; ox = U.Int(q["x"]); oy = U.Int(q["y"]); grip.Capture = true; };
        grip.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var p = Control.MousePosition;
            if (Math.Abs(p.X - s0.X) + Math.Abs(p.Y - s0.Y) > 5) moved = true;
            q["x"] = ox + p.X - s0.X; q["y"] = oy + p.Y - s0.Y; Place();
        };
        grip.MouseUp += (_, _) => { dragging = false; grip.Capture = false; App.SaveSettingsSoon(); if (!moved) EditQuick(); };
        Sync();
    }

    static void Place()
    {
        var canvas = Shell2Ui.Canvas; if (quick == null || canvas == null) return;
        var q = QConf();
        int x = Math.Clamp(U.Int(q["x"]), 0, Math.Max(0, canvas.Width - quick.Width)), y = Math.Clamp(U.Int(q["y"]), 0, Math.Max(0, canvas.Height - quick.Height));
        quick.Location = new Point(x, y);
        quick.BringToFront();
    }

    public static void EditQuick() => EditItems("퀵바 버튼 고르기", QConf(), DefaultQuick, "체크한 버튼이 퀵바에 나타납니다. 손잡이를 끌어 위치를 옮길 수 있어요.", false, RenderQuick);

    public static void ToggleQuick() { var q = QConf(); q["visible"] = q["visible"]?.GetValue<bool>() != true; App.SaveSettingsSoon(); RenderQuick(); }
    public static bool QuickVisible => QConf()["visible"]?.GetValue<bool>() == true;

    // ---- 아래 막대 ---------------------------------------------------------------------------
    public static void RenderBottom()
    {
        var main = App.Main; var canvas = Shell2Ui.Canvas; if (main == null || canvas == null) return;
        var c = BConf();
        if (bottom != null) { buttons.RemoveAll(x => bottom.Contains(x.b)); sliders.RemoveAll(bottom.Contains); bottom.Dispose(); bottom = null; }
        if (c["visible"]?.GetValue<bool>() == false) return;
        bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Large ? 70 : 56, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg2, Padding = new Padding(4, 2, 4, 0) };
        var chip = new Button { Size = new Size(36, 36), FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 8, 6, 2), Tag = "chip", TabStop = false };
        new ToolTip().SetToolTip(chip, "색 (탭: 색 패널)");
        chip.Click += (_, _) => QuickActions.ShowTab("색");
        bottom.Controls.Add(chip);
        foreach (var id in Items(c, DefaultBottom))
        {
            var a = QuickActions.Get(id); if (a == null) continue;
            if (a.Type is "size" or "opacity") bottom.Controls.Add(HSlider(a.Type)); else bottom.Controls.Add(Btn(a, true));
        }
        var edit = Btn(new QuickAction("_edit", "편집", "⚙", EditBottom), true);
        bottom.Controls.Add(edit);
        var host = canvas.Parent;
        host.Controls.Add(bottom);
        host.Controls.SetChildIndex(bottom, host.Controls.GetChildIndex(canvas) + 1);
        Sync();
    }

    static Control HSlider(string type)
    {
        bool sz = type == "size";
        var box = new Panel { Width = sz ? 200 : 130, Height = 46, Margin = new Padding(4, 2, 4, 0), BackColor = Opt.Bg2 };
        var name = new Label { Text = sz ? "크기" : "농도", AutoSize = true, Location = new Point(0, 2), ForeColor = Opt.Fg };
        var val = new Label { AutoSize = false, Width = 60, TextAlign = ContentAlignment.TopRight, Location = new Point(box.Width - 62, 2), ForeColor = Opt.Muted };
        var tb = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, AutoSize = false, Height = 24, Width = box.Width, Location = new Point(0, 18), TabStop = false };
        double min = sz ? 0.5 : 0.01, max = sz ? 600 : 1;
        double ToT(double v) => sz ? Math.Log(v / min) / Math.Log(max / min) : (v - min) / (max - min);
        double FromT(double t) => sz ? min * Math.Pow(max / min, t) : min + (max - min) * t;
        void SyncOne() { var (p, _) = Shell2Ui.ActiveBrush(); if (p == null) return; double v = sz ? p.Size : p.Opacity; tb.Value = (int)Math.Clamp(ToT(v) * 1000, 0, 1000); val.Text = sz ? (v < 10 ? v.ToString("0.0") : Math.Round(v).ToString()) : $"{Math.Round(v * 100)}%"; }
        tb.Scroll += (_, _) =>
        {
            var (p, _) = Shell2Ui.ActiveBrush(); if (p == null) return;
            double v = FromT(tb.Value / 1000.0);
            if (sz) p.Size = Math.Max(0.5, Math.Round(v * 2) / 2); else p.Opacity = Math.Round(v * 100) / 100;
            SyncOne(); App.NotifySettings();
        };
        tb.MouseUp += (_, _) => PresetStore.Save();
        box.Controls.AddRange(new Control[] { name, val, tb });
        box.Tag = (Action)SyncOne;
        sliders.Add(box);
        SyncOne();
        return box;
    }

    public static void EditBottom()
    {
        var c = BConf();
        EditItems("아래 막대 버튼 고르기", c, DefaultBottom, "체크한 버튼이 아래 막대에 나옵니다 (순서는 체크한 순서대로 뒤에 붙음).", true, RenderBottom);
    }
    public static void ToggleBottom() { var c = BConf(); c["visible"] = c["visible"]?.GetValue<bool>() == false; App.SaveSettingsSoon(); RenderBottom(); }
    public static bool BottomVisible => BConf()["visible"]?.GetValue<bool>() != false;

    /// <summary>버튼 고르기 창 (퀵바·아래 막대 공통). 고른 순서: 원래 있던 것 순서 + 새로 고른 것 뒤에</summary>
    static void EditItems(string title, JsonObject conf, string[] def, string info, bool withVisible, Action render)
    {
        var cur = Items(conf, def);
        var fields = new List<Field>();
        if (withVisible) fields.Add(Field.Check("_visible", title.StartsWith("아래") ? "아래 막대 보이기" : "보이기", conf["visible"]?.GetValue<bool>() != false));
        fields.Add(Field.Info(info));
        fields.AddRange(QuickActions.All.Select(a => Field.Check(a.Id, a.Label, cur.Contains(a.Id))));
        var v = Dlg.Form(title, fields, "적용");
        if (v == null) return;
        if (withVisible) conf["visible"] = (bool?)v["_visible"] == true;
        conf["items"] = new JsonArray(cur.Where(id => (bool?)v[id] == true).Concat(QuickActions.All.Select(a => a.Id).Where(id => (bool?)v[id] == true && !cur.Contains(id))).Select(x => (JsonNode)x).ToArray());
        App.SaveSettingsSoon();
        render();
    }

    /// <summary>켜짐 표시·실행취소 가능 여부·색 칩·막대 값</summary>
    public static void Sync()
    {
        foreach (var (b, a) in buttons)
        {
            if (b.IsDisposed) continue;
            bool on = a.Active?.Invoke() == true;
            b.BackColor = on ? Opt.Accent : Opt.Bg2; b.ForeColor = on ? Color.Black : Opt.Fg;
            if (a.Id == "undo") b.Enabled = App.History.CanUndo;
            if (a.Id == "redo") b.Enabled = App.History.CanRedo;
        }
        foreach (var s in sliders) { if (s.IsDisposed) continue; if (s.Tag is Action sync) sync(); else s.Invalidate(); }
        if (bottom != null) foreach (Control c in bottom.Controls) if ((c.Tag as string) == "chip") c.BackColor = Color.FromArgb((int)(App.Fg | 0xFF000000));
    }

    /// <summary>큰 버튼 바꾸면 다시 그림</summary>
    public static void RenderAll() { RenderQuick(); RenderBottom(); }
}
