// 이 모듈에서 쓰는 작은 UI 부품: 웹 segmented(여러 개 중 하나) 단추 줄, 웹 form() 같은 입력 대화상자.
using System.Globalization;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.SelectFill;

public static class Ui
{
    /// <summary>여러 개 중 하나 고르는 단추 줄 (웹 segmented)</summary>
    public static Control Segmented<T>((T id, string text)[] items, Func<T> get, Action<T> set)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, MaximumSize = new Size(270, 0), WrapContents = true, BackColor = Opt.Bg, Margin = new Padding(0, 2, 0, 2) };
        var buttons = new List<RadioButton>();
        foreach (var (id, text) in items)
        {
            var b = new RadioButton { Text = text, Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Opt.Bg2, ForeColor = Opt.Fg, Margin = new Padding(0, 0, 2, 2), Checked = Equals(get(), id), Tag = id };
            b.FlatAppearance.CheckedBackColor = Opt.Accent;
            b.FlatAppearance.BorderColor = Color.FromArgb(74, 74, 84);
            b.CheckedChanged += (_, _) =>
            {
                b.ForeColor = b.Checked ? Color.Black : Opt.Fg;
                if (b.Checked) { set((T)b.Tag); App.NotifySettings(); }
            };
            b.ForeColor = b.Checked ? Color.Black : Opt.Fg;
            buttons.Add(b);
            p.Controls.Add(b);
        }
        return p;
    }

    // ---- 입력 대화상자 (웹 ui/dialogs.js form) -------------------------------------------------
    public sealed class Field
    {
        public string Key, Label, Type;          // segment | number | checkbox | color | info
        public (string id, string text)[] Options;
        public object Value;
        public double Min = double.MinValue, Max = double.MaxValue, Step = 1;
    }
    public static Field Seg(string key, string label, (string, string)[] opts, string value) => new() { Key = key, Label = label, Type = "segment", Options = opts, Value = value };
    public static Field Num(string key, string label, double value, double min = -1e6, double max = 1e6, double step = 1) => new() { Key = key, Label = label, Type = "number", Value = value, Min = min, Max = max, Step = step };
    public static Field Check(string key, string label, bool value) => new() { Key = key, Label = label, Type = "checkbox", Value = value };
    public static Field ColorF(string key, string label, string hex) => new() { Key = key, Label = label, Type = "color", Value = hex };
    public static Field Info(string label) => new() { Key = "info", Label = label, Type = "info" };

    /// <summary>값 입력 대화상자 → 확인하면 key → 값 (string / double / bool), 취소하면 null</summary>
    public static Dictionary<string, object> Form(string title, Field[] fields, string okLabel = "확인")
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12), Font = new Font("Malgun Gothic", 9f) };
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        var vals = new Dictionary<string, object>();
        foreach (var fd in fields)
        {
            var fl = fd;
            switch (fd.Type)
            {
                case "segment":
                    vals[fd.Key] = fd.Value;
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 6, 0, 2) });
                    var seg = Segmented(fd.Options, () => (string)vals[fl.Key], v => vals[fl.Key] = v);
                    seg.MaximumSize = new Size(440, 0);
                    p.Controls.Add(seg);
                    break;
                case "number":
                    vals[fd.Key] = Convert.ToDouble(fd.Value, CultureInfo.InvariantCulture);
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 6, 0, 2) });
                    int dec = fd.Step < 1 ? 2 : 0;
                    var n = new NumericUpDown { Minimum = (decimal)Math.Max(-1e9, fd.Min), Maximum = (decimal)Math.Min(1e9, fd.Max), DecimalPlaces = dec, Increment = (decimal)fd.Step, Width = 160, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
                    n.Value = (decimal)Math.Clamp((double)vals[fd.Key], (double)n.Minimum, (double)n.Maximum);
                    n.ValueChanged += (_, _) => vals[fl.Key] = (double)n.Value;
                    p.Controls.Add(n);
                    break;
                case "checkbox":
                    vals[fd.Key] = (bool)fd.Value;
                    var cb = new CheckBox { Text = fd.Label, Checked = (bool)fd.Value, AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Opt.Fg, Margin = new Padding(0, 6, 0, 2) };
                    cb.CheckedChanged += (_, _) => vals[fl.Key] = cb.Checked;
                    p.Controls.Add(cb);
                    break;
                case "color":
                    vals[fd.Key] = (string)fd.Value;
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, Margin = new Padding(0, 6, 0, 2) });
                    var btn = new Button { Width = 80, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = HexColor((string)fd.Value), Text = "" };
                    btn.Click += (_, _) =>
                    {
                        using var cd = new ColorDialog { Color = btn.BackColor, FullOpen = true };
                        if (cd.ShowDialog(f) == DialogResult.OK) { btn.BackColor = cd.Color; vals[fl.Key] = $"#{cd.Color.R:x2}{cd.Color.G:x2}{cd.Color.B:x2}"; }
                    };
                    p.Controls.Add(btn);
                    break;
                case "info":
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Opt.Muted, Margin = new Padding(0, 8, 0, 2) });
                    break;
            }
        }
        var ok = new Button { Text = okLabel, DialogResult = DialogResult.OK, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Opt.Accent, ForeColor = Color.Black };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
        var row = Opt.Row(ok, cancel); row.Margin = new Padding(0, 12, 0, 0);
        p.Controls.Add(row);
        f.Controls.Add(p); f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(App.Main) == DialogResult.OK ? vals : null;
    }

    public static Color HexColor(string hex) { var (r, g, b) = AutoFlat.Hex(hex); return Color.FromArgb(r, g, b); }
    public static string ToHex(uint premulColor) => Core.Px.ToHex(premulColor);
}
