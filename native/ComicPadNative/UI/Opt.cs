// 도구 옵션·대화상자에서 쓰는 작은 조절 부품 (모든 모듈이 같은 모양으로)
using System.Globalization;

namespace ComicPadNative.UI;

public static class Opt
{
    public static readonly Color Bg = Color.FromArgb(47, 47, 53), Bg2 = Color.FromArgb(58, 58, 66), Fg = Color.FromArgb(232, 232, 236), Muted = Color.FromArgb(156, 156, 168), Accent = Color.FromArgb(232, 163, 61);

    public static FlowLayoutPanel Panel(params Control[] items)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Bg, Padding = new Padding(6) };
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    public static Label Title(string t) => new() { Text = t, AutoSize = true, ForeColor = Accent, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.5f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2) };
    public static Label Help(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = Muted, Margin = new Padding(0, 0, 0, 4) };

    /// <summary>이름 + 끌어서 바꾸는 막대 + 숫자</summary>
    public static Control Slider(string label, double min, double max, Func<double> get, Action<double> set, bool pct = false, bool log = false, double step = 0, Action onEnd = null)
    {
        var box = new Panel { Width = 260, Height = 44, BackColor = Bg, Margin = new Padding(0, 2, 0, 2) };
        var name = new Label { Text = label, ForeColor = Fg, AutoSize = true, Location = new Point(0, 2) };
        var val = new Label { ForeColor = Muted, AutoSize = false, Width = 70, TextAlign = ContentAlignment.TopRight, Location = new Point(190, 2) };
        var tb = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Width = 260, Location = new Point(0, 18), AutoSize = false, Height = 24 };
        double ToT(double v) => log && min > 0 ? Math.Log(v / min) / Math.Log(max / min) : (v - min) / (max - min);
        double FromT(double t) { double v = log && min > 0 ? min * Math.Pow(max / min, t) : min + (max - min) * t; return step > 0 ? Math.Round(v / step) * step : v; }
        string Fmt(double v) => pct ? $"{v * 100:0}%" : Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);
        void Sync() { double v = get(); tb.Value = (int)Math.Clamp(ToT(v) * 1000, 0, 1000); val.Text = Fmt(v); }
        tb.Scroll += (_, _) => { double v = FromT(tb.Value / 1000.0); set(v); val.Text = Fmt(v); App.NotifySettings(); };
        tb.MouseUp += (_, _) => onEnd?.Invoke();
        box.Controls.AddRange(new Control[] { name, val, tb });
        Sync();
        App.SettingsChanged += () => { if (!box.IsDisposed && !tb.Focused) Sync(); };
        return box;
    }

    public static CheckBox Check(string label, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = label, Checked = get(), ForeColor = Fg, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        c.CheckedChanged += (_, _) => { set(c.Checked); App.NotifySettings(); };
        return c;
    }

    public static Control Choice(string label, (string id, string text)[] items, Func<string> get, Action<string> set)
    {
        var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Bg, Margin = new Padding(0, 2, 0, 2) };
        if (label != null) box.Controls.Add(new Label { Text = label, ForeColor = Fg, AutoSize = true });
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, BackColor = Bg2, ForeColor = Fg, FlatStyle = FlatStyle.Flat };
        foreach (var (_, t) in items) cb.Items.Add(t);
        cb.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.id == get()));
        cb.SelectedIndexChanged += (_, _) => { set(items[cb.SelectedIndex].id); App.NotifySettings(); };
        box.Controls.Add(cb);
        return box;
    }

    public static Button Button(string text, Action run, bool primary = false)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Bg2, ForeColor = primary ? Color.Black : Fg, Margin = new Padding(0, 2, 6, 2) };
        b.FlatAppearance.BorderColor = Color.FromArgb(74, 74, 84);
        b.Click += (_, _) => run();
        return b;
    }

    public static FlowLayoutPanel Row(params Control[] items)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, BackColor = Bg, Margin = new Padding(0) };
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    /// <summary>값 입력 대화상자: fields = (이름, 기본값) → 확인하면 값 목록</summary>
    public static double[] AskNumbers(string title, params (string label, double value, double min, double max)[] fields)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, BackColor = Bg, ForeColor = Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10) };
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        var boxes = new List<NumericUpDown>();
        foreach (var (label, value, min, max) in fields)
        {
            p.Controls.Add(new Label { Text = label, AutoSize = true });
            var n = new NumericUpDown { Minimum = (decimal)min, Maximum = (decimal)max, DecimalPlaces = value % 1 == 0 && min % 1 == 0 ? 0 : 2, Value = (decimal)Math.Clamp(value, min, max), Width = 200, BackColor = Bg2, ForeColor = Fg };
            boxes.Add(n); p.Controls.Add(n);
        }
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        p.Controls.Add(Row(ok, cancel));
        f.Controls.Add(p); f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(App.Main) == DialogResult.OK ? boxes.Select(b => (double)b.Value).ToArray() : null;
    }
}
