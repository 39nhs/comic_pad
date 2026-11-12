// 이 모듈의 대화상자 부품: 웹 form() 같은 값 입력 창(숫자·켜기·색·고르기·안내), 끌어서 바꾸는 막대, 색 단추.
// 값이 바뀔 때마다 onChange 를 불러서 미리보기에 쓸 수 있다.
using System.Globalization;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Filters;

public sealed class Field
{
    public string Key, Label, Type = "number";   // number | check | color | choice | info
    public double Num, Min = -1e9, Max = 1e9, Step = 1;
    public bool Bool;
    public string Text;                          // color(#rrggbb) / choice(id)
    public (string id, string text)[] Options;
    public static Field N(string key, string label, double v, double min = -1e9, double max = 1e9, double step = 1) => new() { Key = key, Label = label, Num = v, Min = min, Max = max, Step = step };
    public static Field C(string key, string label, bool v) => new() { Key = key, Label = label, Type = "check", Bool = v };
    public static Field Col(string key, string label, string v) => new() { Key = key, Label = label, Type = "color", Text = v };
    public static Field Pick(string key, string label, (string, string)[] opts, string v) => new() { Key = key, Label = label, Type = "choice", Options = opts, Text = v };
    public static Field Info(string text) => new() { Key = "info", Label = text, Type = "info" };
}

public sealed class Values
{
    public readonly Dictionary<string, object> Map = new();
    public double N(string k) => Map.TryGetValue(k, out var v) && v is double d ? d : 0;
    public bool B(string k) => Map.TryGetValue(k, out var v) && v is bool b && b;
    public string S(string k) => Map.TryGetValue(k, out var v) ? v as string : null;
    /// <summary>창에 보이는 숫자 칸 바꾸기 (onChange 안에서 다른 칸 채우기)</summary>
    public readonly Dictionary<string, Action<double>> SetNum = new();
}

public static class Ui
{
    public static Form Dialog(string title)
    {
        var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), ShowInTaskbar = false,
            Font = new Font("Malgun Gothic", 9f),
        };
        return f;
    }

    public static FlowLayoutPanel Column() => new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Opt.Bg, Margin = new Padding(0) };
    public static FlowLayoutPanel RowP() => new() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Opt.Bg, Margin = new Padding(0, 2, 0, 2) };
    public static Label Lbl(string t, bool muted = false, int max = 360) => new() { Text = t, AutoSize = true, MaximumSize = new Size(max, 0), ForeColor = muted ? Opt.Muted : Opt.Fg, Margin = new Padding(0, 4, 0, 2) };
    public static Label Section(string t) => new() { Text = t, AutoSize = true, ForeColor = Opt.Accent, Font = new Font("Malgun Gothic", 9.5f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2) };

    public static Button Btn(string text, Action run, bool primary = false)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = primary ? Opt.Accent : Opt.Bg2, ForeColor = primary ? Color.Black : Opt.Fg, Margin = new Padding(0, 4, 6, 2) };
        b.FlatAppearance.BorderColor = Color.FromArgb(74, 74, 84);
        if (run != null) b.Click += (_, _) => run();
        return b;
    }

    /// <summary>이름 + 막대 + 숫자 칸. set 은 값이 바뀔 때마다</summary>
    public static Control Slider(string label, double min, double max, double step, Func<double> get, Action<double> set, bool log = false, Func<double, string> fmt = null)
    {
        var box = new Panel { Width = 360, Height = 46, BackColor = Opt.Bg, Margin = new Padding(0, 1, 0, 1) };
        var name = new Label { Text = label, ForeColor = Opt.Fg, AutoSize = true, Location = new Point(0, 2) };
        var val = new Label { ForeColor = Opt.Muted, AutoSize = false, Width = 90, TextAlign = ContentAlignment.TopRight, Location = new Point(270, 2) };
        var tb = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Width = 360, Location = new Point(0, 18), AutoSize = false, Height = 26 };
        double ToT(double v) => log && min > 0 ? Math.Log(v / min) / Math.Log(max / min) : (v - min) / (max - min);
        double FromT(double t) { double v = log && min > 0 ? min * Math.Pow(max / min, t) : min + (max - min) * t; if (step > 0) v = Math.Round(v / step) * step; return Math.Clamp(v, min, max); }
        string F(double v) => fmt != null ? fmt(v) : (step >= 1 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture));
        bool busy = false;
        void Sync() { busy = true; double v = get(); tb.Value = (int)Math.Clamp(Math.Round(ToT(v) * 1000), 0, 1000); val.Text = F(v); busy = false; }
        tb.ValueChanged += (_, _) => { if (busy) return; double v = FromT(tb.Value / 1000.0); set(v); val.Text = F(v); };
        // 숫자 칸을 눌러 직접 입력
        val.Cursor = Cursors.IBeam;
        val.Click += (_, _) =>
        {
            var tx = new TextBox { Text = get().ToString(CultureInfo.InvariantCulture), Width = 90, Location = val.Location, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
            box.Controls.Add(tx); tx.BringToFront(); tx.Focus(); tx.SelectAll();
            void Done(bool ok)
            {
                if (tx.IsDisposed) return;
                if (ok && double.TryParse(tx.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { set(Math.Clamp(v, min, max)); }
                tx.Dispose(); Sync();
            }
            tx.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Done(true); e.SuppressKeyPress = true; } else if (e.KeyCode == Keys.Escape) { Done(false); e.SuppressKeyPress = true; } };
            tx.LostFocus += (_, _) => Done(true);
        };
        box.Controls.AddRange(new Control[] { name, val, tb });
        box.Tag = (Action)Sync;
        Sync();
        return box;
    }

    /// <summary>슬라이더 값 다시 읽기 (자동 버튼 등)</summary>
    public static void SyncAll(Control root)
    {
        foreach (Control c in root.Controls) { if (c.Tag is Action a) a(); SyncAll(c); }
    }

    public static Button ColorBtn(string label, Func<string> get, Action<string> set)
    {
        var b = new Button { AutoSize = false, Width = 160, Height = 28, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 6, 2), TextAlign = ContentAlignment.MiddleRight };
        void Paint() { var c = Px.Unpremul(Px.FromHex(get())); b.BackColor = Color.FromArgb((int)((c >> 16) & 255), (int)((c >> 8) & 255), (int)(c & 255)); b.ForeColor = ((c >> 16) & 255) * 0.299 + ((c >> 8) & 255) * 0.587 + (c & 255) * 0.114 > 140 ? Color.Black : Color.White; b.Text = (label ?? "") + "  " + get(); }
        b.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { FullOpen = true, Color = b.BackColor };
            if (dlg.ShowDialog(b.FindForm()) != DialogResult.OK) return;
            set($"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}");
            Paint();
        };
        b.Tag = (Action)Paint;
        Paint();
        return b;
    }

    public static ComboBox Combo((string id, string text)[] items, Func<string> get, Action<string> set, int width = 250)
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, BackColor = Opt.Bg2, ForeColor = Opt.Fg, FlatStyle = FlatStyle.Flat };
        foreach (var (_, t) in items) cb.Items.Add(t);
        cb.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.id == get()));
        cb.SelectedIndexChanged += (_, _) => set(items[cb.SelectedIndex].id);
        return cb;
    }

    public static CheckBox Check(string label, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = label, Checked = get(), ForeColor = Opt.Fg, AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 3, 0, 3) };
        c.CheckedChanged += (_, _) => set(c.Checked);
        return c;
    }

    /// <summary>웹 form(): 값 입력 창. 확인하면 값, 취소면 null</summary>
    public static Values Ask(string title, Field[] fields, string okLabel = "확인", Action<Values> onChange = null)
    {
        using var f = Dialog(title);
        var col = Column();
        var vals = new Values();
        void Changed() { try { onChange?.Invoke(vals); } catch (Exception ex) { App.Toast(ex.Message); } }
        foreach (var fd in fields)
        {
            switch (fd.Type)
            {
                case "number":
                {
                    vals.Map[fd.Key] = fd.Num;
                    col.Controls.Add(Lbl(fd.Label));
                    int dec = fd.Step >= 1 && fd.Num % 1 == 0 ? 0 : fd.Step >= 0.1 ? 2 : 3;
                    var n = new NumericUpDown { Minimum = (decimal)Math.Max(-1e9, fd.Min), Maximum = (decimal)Math.Min(1e9, fd.Max), DecimalPlaces = dec, Increment = (decimal)fd.Step, Width = 200, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
                    n.Value = (decimal)Math.Clamp(fd.Num, fd.Min, fd.Max);
                    var key = fd.Key;
                    n.ValueChanged += (_, _) => { vals.Map[key] = (double)n.Value; Changed(); };
                    vals.SetNum[key] = v => n.Value = (decimal)Math.Clamp(v, (double)n.Minimum, (double)n.Maximum);
                    col.Controls.Add(n);
                    break;
                }
                case "check":
                {
                    vals.Map[fd.Key] = fd.Bool;
                    var key = fd.Key;
                    col.Controls.Add(Check(fd.Label, () => (bool)vals.Map[key], v => { vals.Map[key] = v; Changed(); }));
                    break;
                }
                case "color":
                {
                    vals.Map[fd.Key] = fd.Text;
                    var key = fd.Key;
                    col.Controls.Add(Lbl(fd.Label));
                    col.Controls.Add(ColorBtn("", () => (string)vals.Map[key], v => { vals.Map[key] = v; Changed(); }));
                    break;
                }
                case "choice":
                {
                    vals.Map[fd.Key] = fd.Text;
                    var key = fd.Key;
                    col.Controls.Add(Lbl(fd.Label));
                    col.Controls.Add(Combo(fd.Options, () => (string)vals.Map[key], v => { vals.Map[key] = v; Changed(); }, 340));
                    break;
                }
                default:
                    col.Controls.Add(Lbl(fd.Label, true));
                    break;
            }
        }
        var ok = Btn(okLabel, null, true); ok.DialogResult = DialogResult.OK;
        var cancel = Btn("취소", null); cancel.DialogResult = DialogResult.Cancel;
        var row = RowP(); row.Controls.Add(ok); row.Controls.Add(cancel);
        col.Controls.Add(row);
        f.Controls.Add(col);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(App.Main) == DialogResult.OK ? vals : null;
    }

    /// <summary>메인 창이 있는 화면의 작업 영역 (창 없이 시험할 때는 주 화면)</summary>
    public static Rectangle WorkArea() => (App.Main != null ? Screen.FromControl(App.Main) : Screen.PrimaryScreen)?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);

    /// <summary>오래 걸리는 일: 모래시계 커서 + 오류 알림</summary>
    public static T Busy<T>(Func<T> work)
    {
        var old = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;
        try { return work(); }
        finally { Cursor.Current = old; }
    }
    public static void Busy(Action work) => Busy(() => { work(); return 0; });
}
