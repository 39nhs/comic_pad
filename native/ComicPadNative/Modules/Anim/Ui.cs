// 애니·픽셀 패널에서 쓰는 작은 조절 부품 + 값 입력 대화상자 (웹 판 controls.js · dialogs.js form 과 같은 쓰임)
using System.Globalization;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Anim;

public static class Ui
{
    public static Color Bg => Opt.Bg; public static Color Bg2 => Opt.Bg2; public static Color Fg => Opt.Fg; public static Color Muted => Opt.Muted; public static Color Accent => Opt.Accent;

    public static FlowLayoutPanel Col(int width, params Control[] items)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Bg, Margin = new Padding(0), Padding = new Padding(0) };
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    public static FlowLayoutPanel Row(params Control[] items)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Bg, Margin = new Padding(0, 1, 0, 1), MaximumSize = new Size(290, 0) };
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    /// <summary>제목 + 내용 (웹 section)</summary>
    public static Control Section(string title, params Control[] items)
    {
        var p = Col(290);
        p.Margin = new Padding(0, 6, 0, 2);
        p.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Accent, Font = new Font("Malgun Gothic", 9.5f, FontStyle.Bold), Margin = new Padding(0, 4, 0, 2) });
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    public static Label Help(string t, int width = 280) => new() { Text = t, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = Muted, Margin = new Padding(0, 0, 0, 4) };
    public static Label Text(string t) => new() { Text = t, AutoSize = true, ForeColor = Fg, Margin = new Padding(0, 4, 4, 0) };

    public static Button Btn(string text, Action run, bool primary = false, string tip = null)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Bg2, ForeColor = primary ? Color.Black : Fg, Margin = new Padding(0, 1, 3, 1), Padding = new Padding(2, 0, 2, 0), MinimumSize = new Size(0, 24) };
        b.FlatAppearance.BorderColor = Color.FromArgb(74, 74, 84);
        b.Click += (_, _) => UI.MainForm.Run(run, text);
        if (tip != null) new ToolTip().SetToolTip(b, tip);
        return b;
    }

    public static CheckBox Check(string label, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = label, Checked = get(), ForeColor = Fg, AutoSize = true, Margin = new Padding(0, 2, 6, 2) };
        c.CheckedChanged += (_, _) => set(c.Checked);
        return c;
    }

    /// <summary>끌어서 바꾸는 막대: begin(누를 때) → set(끄는 동안) → end(뗄 때, 실행취소 기록용)</summary>
    public static Control Slider(string label, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt = null, Action begin = null, Action end = null, int width = 280)
    {
        var box = new Panel { Width = width, Height = 42, BackColor = Bg, Margin = new Padding(0, 1, 0, 1) };
        var name = new Label { Text = label, ForeColor = Fg, AutoSize = false, AutoEllipsis = true, Width = width - 94, Height = 16, Location = new Point(0, 2) };
        var val = new Label { ForeColor = Muted, AutoSize = false, Width = 90, TextAlign = ContentAlignment.TopRight, Location = new Point(width - 92, 2) };
        int steps = (int)Math.Max(1, Math.Min(2000, Math.Round((max - min) / (step > 0 ? step : (max - min) / 1000))));
        var tb = new TrackBar { Minimum = 0, Maximum = steps, TickStyle = TickStyle.None, Width = width, Location = new Point(0, 17), AutoSize = false, Height = 24 };
        fmt ??= v => v.ToString("0.##", CultureInfo.InvariantCulture);
        double FromT(int t) => min + (max - min) * t / steps;
        int ToT(double v) => (int)Math.Clamp(Math.Round((v - min) / (max - min) * steps), 0, steps);
        tb.Value = ToT(get()); val.Text = fmt(get());
        bool down = false;
        tb.MouseDown += (_, _) => { if (!down) { down = true; begin?.Invoke(); } };
        tb.Scroll += (_, _) => { if (!down) { down = true; begin?.Invoke(); } double v = FromT(tb.Value); set(v); val.Text = fmt(v); };
        tb.MouseUp += (_, _) => { if (down) { down = false; end?.Invoke(); } };
        tb.KeyUp += (_, _) => { if (down) { down = false; end?.Invoke(); } };
        box.Controls.AddRange(new Control[] { name, val, tb });
        return box;
    }

    /// <summary>색 고르기 단추</summary>
    public static Button ColorBtn(string label, Func<string> get, Action<string> set)
    {
        var b = Btn(label, () => { });
        void Sync() { b.BackColor = ColorTranslator.FromHtml(get() ?? "#000000"); var c = b.BackColor; b.ForeColor = c.R * 0.3 + c.G * 0.59 + c.B * 0.11 > 128 ? Color.Black : Color.White; }
        Sync();
        b.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = ColorTranslator.FromHtml(get() ?? "#000000"), FullOpen = true };
            if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
            set($"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}"); Sync();
        };
        return b;
    }

    // ---------------------------------------------------------------------
    // 값 입력 대화상자 (웹 form)
    // ---------------------------------------------------------------------
    public sealed class Field
    {
        public string Key, Label, Type;   // number | text | check | seg | select | color | info
        public object Value;
        public double Min = double.MinValue, Max = double.MaxValue, Step = 1;
        public (string id, string text)[] Options;
        public static Field Num(string key, string label, double v, double min = 0, double max = 100000, double step = 1) => new() { Key = key, Label = label, Type = "number", Value = v, Min = min, Max = max, Step = step };
        public static Field Txt(string key, string label, string v) => new() { Key = key, Label = label, Type = "text", Value = v };
        public static Field Chk(string key, string label, bool v) => new() { Key = key, Label = label, Type = "check", Value = v };
        public static Field Seg(string key, string label, (string, string)[] opts, string v) => new() { Key = key, Label = label, Type = "seg", Options = opts, Value = v };
        public static Field Clr(string key, string label, string v) => new() { Key = key, Label = label, Type = "color", Value = v };
        public static Field Info(string text) => new() { Key = "", Label = text, Type = "info" };
    }

    public sealed class Values : Dictionary<string, object>
    {
        public double N(string k) => Convert.ToDouble(this[k], CultureInfo.InvariantCulture);
        public int I(string k) => (int)Math.Round(N(k));
        public string S(string k) => this[k]?.ToString();
        public bool B(string k) => this[k] is bool b && b;
    }

    public static Values Form(string title, IEnumerable<Field> fields, string okLabel = "확인", Action<Values, Label> onChange = null)
    {
        using var f = new System.Windows.Forms.Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, BackColor = Bg, ForeColor = Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Font = new Font("Malgun Gothic", 9f), ShowInTaskbar = false };
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var vals = new Values();
        var live = new Label { AutoSize = true, ForeColor = Muted, MaximumSize = new Size(420, 0) };
        void Changed() => onChange?.Invoke(vals, live);
        foreach (var fd in fields)
        {
            if (fd == null) continue;
            switch (fd.Type)
            {
                case "info": p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, ForeColor = Muted, MaximumSize = new Size(420, 0), Margin = new Padding(0, 4, 0, 4) }); break;
                case "number":
                {
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
                    double v = Convert.ToDouble(fd.Value, CultureInfo.InvariantCulture);
                    int dec = fd.Step % 1 == 0 ? 0 : fd.Step >= 0.1 ? 1 : 2;
                    var n = new NumericUpDown { Minimum = (decimal)Math.Max(-1e9, fd.Min), Maximum = (decimal)Math.Min(1e9, fd.Max), DecimalPlaces = dec, Increment = (decimal)fd.Step, Width = 200, BackColor = Bg2, ForeColor = Fg };
                    n.Value = Math.Clamp((decimal)v, n.Minimum, n.Maximum);
                    vals[fd.Key] = (double)n.Value;
                    var key = fd.Key;
                    n.ValueChanged += (_, _) => { vals[key] = (double)n.Value; Changed(); };
                    p.Controls.Add(n);
                    break;
                }
                case "text":
                {
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
                    var t = new TextBox { Text = fd.Value?.ToString() ?? "", Width = 260, BackColor = Bg2, ForeColor = Fg };
                    vals[fd.Key] = t.Text; var key = fd.Key;
                    t.TextChanged += (_, _) => { vals[key] = t.Text; Changed(); };
                    p.Controls.Add(t);
                    break;
                }
                case "check":
                {
                    var c = new CheckBox { Text = fd.Label, Checked = fd.Value is bool b && b, AutoSize = true, ForeColor = Fg, Margin = new Padding(0, 4, 0, 0) };
                    vals[fd.Key] = c.Checked; var key = fd.Key;
                    c.CheckedChanged += (_, _) => { vals[key] = c.Checked; Changed(); };
                    p.Controls.Add(c);
                    break;
                }
                case "seg":
                {
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, Margin = new Padding(0, 4, 0, 0) });
                    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(440, 0) };
                    vals[fd.Key] = fd.Value?.ToString();
                    var key = fd.Key;
                    var btns = new List<RadioButton>();
                    foreach (var (id, text) in fd.Options)
                    {
                        var rb = new RadioButton { Text = text, Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Bg2, ForeColor = Fg, Checked = id == fd.Value?.ToString(), Margin = new Padding(0, 0, 2, 2), Tag = id };
                        rb.FlatAppearance.CheckedBackColor = Accent;
                        rb.CheckedChanged += (_, _) => { rb.ForeColor = rb.Checked ? Color.Black : Fg; if (rb.Checked) { vals[key] = (string)rb.Tag; Changed(); } };
                        rb.ForeColor = rb.Checked ? Color.Black : Fg;
                        btns.Add(rb); row.Controls.Add(rb);
                    }
                    p.Controls.Add(row);
                    break;
                }
                case "color":
                {
                    vals[fd.Key] = fd.Value?.ToString() ?? "#000000"; var key = fd.Key;
                    p.Controls.Add(ColorBtn(fd.Label, () => (string)vals[key], v => { vals[key] = v; Changed(); }));
                    break;
                }
            }
        }
        p.Controls.Add(live);
        var ok = new Button { Text = okLabel, DialogResult = DialogResult.OK, AutoSize = true, BackColor = Accent, ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, BackColor = Bg2, ForeColor = Fg, FlatStyle = FlatStyle.Flat };
        p.Controls.Add(new FlowLayoutPanel { AutoSize = true, Controls = { ok, cancel }, Margin = new Padding(0, 8, 0, 0) });
        f.Controls.Add(p); f.AcceptButton = ok; f.CancelButton = cancel;
        f.Shown += (_, _) => Changed();
        return f.ShowDialog(App.Main) == DialogResult.OK ? vals : null;
    }

    /// <summary>오래 걸리는 일: 모래시계 커서로 실행하고 오류는 알림으로</summary>
    public static T Busy<T>(string name, Func<T> work)
    {
        var old = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;
        try { return work(); }
        catch (Exception ex) { App.Toast($"{name}: {ex.Message}"); return default; }
        finally { Cursor.Current = old; }
    }
}
