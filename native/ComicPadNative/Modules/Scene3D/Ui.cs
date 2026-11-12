// 3D·버튜버 창에서 쓰는 작은 조절 부품 (웹 판 ui/controls.js 의 slider·toggle·segmented·colorInput·select·button·row·section 과 같은 역할).
// UI.Opt 와 달리 앱 설정 저장을 건드리지 않고, Sync() 로 값만 다시 읽는다.
using System.Globalization;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Scene3D;

public sealed class Ui3
{
    public const int W = 320;
    public readonly List<Action> Syncs = new();
    public void SyncAll() { foreach (var s in Syncs.ToList()) s(); }

    public static Font Bold => new(SystemFonts.MessageBoxFont.FontFamily, 9.5f, FontStyle.Bold);

    public Control Section(string title, params Control[] items)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Opt.Bg, Margin = new Padding(0, 0, 0, 6) };
        p.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Opt.Accent, Font = Bold, Margin = new Padding(0, 8, 0, 2) });
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    public static Label Help(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = Opt.Muted, Margin = new Padding(0, 2, 0, 4) };

    public static System.Windows.Forms.Button Button(string text, Action run, bool primary = false)
    {
        var b = new System.Windows.Forms.Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = primary ? Opt.Accent : Opt.Bg2, ForeColor = primary ? Color.Black : Opt.Fg, Margin = new Padding(0, 2, 4, 2), MaximumSize = new Size(W, 0) };
        b.FlatAppearance.BorderColor = Color.FromArgb(74, 74, 84);
        b.Click += (_, _) => MainFormRun(run, text);
        return b;
    }
    static void MainFormRun(Action a, string name) => UI.MainForm.Run(a, name);

    public static FlowLayoutPanel Row(params Control[] items)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(W + 4, 0), BackColor = Opt.Bg, Margin = new Padding(0) };
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    /// <summary>이름 + 막대 + 값. step 단위로 맞춤</summary>
    public Control Slider(string label, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt = null, bool log = false)
    {
        var box = new Panel { Width = W, Height = 42, BackColor = Opt.Bg, Margin = new Padding(0, 1, 0, 1) };
        var name = new Label { Text = label, ForeColor = Opt.Fg, AutoSize = true, Location = new Point(0, 2) };
        var val = new Label { ForeColor = Opt.Muted, AutoSize = false, Width = 80, TextAlign = ContentAlignment.TopRight, Location = new Point(W - 82, 2) };
        var tb = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Width = W, Location = new Point(0, 17), AutoSize = false, Height = 24 };
        fmt ??= v => Math.Abs(v) >= 100 || step >= 1 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
        double ToT(double v) => log && min > 0 ? Math.Log(Math.Max(min, v) / min) / Math.Log(max / min) : (v - min) / (max - min);
        double FromT(double t) { double v = log && min > 0 ? min * Math.Pow(max / min, t) : min + (max - min) * t; if (step > 0) v = Math.Round(v / step) * step; return Math.Clamp(v, min, max); }
        bool busy = false;
        void Sync() { if (box.IsDisposed) return; busy = true; double v = get(); tb.Value = (int)Math.Clamp(Math.Round(ToT(v) * 1000), 0, 1000); val.Text = fmt(v); busy = false; }
        tb.ValueChanged += (_, _) => { if (busy) return; double v = FromT(tb.Value / 1000.0); set(v); val.Text = fmt(v); };
        tb.MouseWheel += (_, e) => { if (e is HandledMouseEventArgs h) h.Handled = true; };   // 패널 스크롤을 막대가 먹지 않게
        box.Controls.AddRange(new Control[] { name, val, tb });
        Sync();
        Syncs.Add(Sync);
        box.Disposed += (_, _) => Syncs.Remove(Sync);
        return box;
    }

    public Control Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = label, Checked = get(), ForeColor = Opt.Fg, AutoSize = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, 2, 8, 2) };
        bool busy = false;
        c.CheckedChanged += (_, _) => { if (!busy) set(c.Checked); };
        void Sync() { if (c.IsDisposed) return; busy = true; c.Checked = get(); busy = false; }
        Syncs.Add(Sync); c.Disposed += (_, _) => Syncs.Remove(Sync);
        return c;
    }

    /// <summary>여러 개 중 하나 (단추 모양)</summary>
    public Control Segmented((string id, string text)[] items, Func<string> get, Action<string> set)
    {
        var p = Row();
        var btns = new List<(string id, System.Windows.Forms.Button b)>();
        void Sync() { string v = get(); foreach (var (id, b) in btns) { bool on = id == v; b.BackColor = on ? Opt.Accent : Opt.Bg2; b.ForeColor = on ? Color.Black : Opt.Fg; } }
        foreach (var (id, text) in items)
        {
            var b = new System.Windows.Forms.Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 2, 2, 2) };
            b.FlatAppearance.BorderColor = Color.FromArgb(74, 74, 84);
            var k = id;
            b.Click += (_, _) => { set(k); Sync(); };
            btns.Add((id, b)); p.Controls.Add(b);
        }
        Sync();
        Syncs.Add(Sync); p.Disposed += (_, _) => Syncs.Remove(Sync);
        return p;
    }

    public (Control box, ComboBox combo) Select(string label, (string id, string text)[] items, Func<string> get, Action<string> set)
    {
        var box = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, BackColor = Opt.Bg, Margin = new Padding(0, 2, 0, 2) };
        if (label != null) box.Controls.Add(new Label { Text = label, ForeColor = Opt.Fg, AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = label != null ? 220 : W, BackColor = Opt.Bg2, ForeColor = Opt.Fg, FlatStyle = FlatStyle.Flat, MaxDropDownItems = 20 };
        foreach (var (_, t) in items) cb.Items.Add(t);
        bool busy = false;
        void Sync() { if (cb.IsDisposed) return; busy = true; cb.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.id == get())); busy = false; }
        Sync();
        cb.SelectedIndexChanged += (_, _) => { if (!busy && cb.SelectedIndex >= 0) set(items[cb.SelectedIndex].id); };
        cb.MouseWheel += (_, e) => { if (!cb.DroppedDown && e is HandledMouseEventArgs h) h.Handled = true; };
        Syncs.Add(Sync); cb.Disposed += (_, _) => Syncs.Remove(Sync);
        box.Controls.Add(cb);
        return (box, cb);
    }

    public Control ColorInput(string label, Func<string> get, Action<string> set)
    {
        var b = new System.Windows.Forms.Button { AutoSize = false, Width = 150, Height = 26, FlatStyle = FlatStyle.Flat, ForeColor = Opt.Fg, Margin = new Padding(0, 2, 6, 2), TextAlign = ContentAlignment.MiddleRight };
        void Sync()
        {
            if (b.IsDisposed) return;
            var c = Core.Px.Unpremul(Core.Px.FromHex(get()));
            b.BackColor = Color.FromArgb(255, (int)(c >> 16) & 255, (int)(c >> 8) & 255, (int)c & 255);
            b.ForeColor = ((c >> 16 & 255) + (c >> 8 & 255) + (c & 255)) > 380 ? Color.Black : Color.White;
            b.Text = label + "  " + get();
        }
        b.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { FullOpen = true, Color = b.BackColor };
            if (dlg.ShowDialog(b.FindForm()) != DialogResult.OK) return;
            set($"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}");
            Sync();
        };
        Sync();
        Syncs.Add(Sync); b.Disposed += (_, _) => Syncs.Remove(Sync);
        return b;
    }

    /// <summary>모든 부품을 어두운 판에 세로로 (스크롤)</summary>
    public static Panel Scroller(out FlowLayoutPanel inner)
    {
        var host = new Panel { AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(8, 4, 4, 8) };
        inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Opt.Bg, Location = new Point(8, 4) };
        host.Controls.Add(inner);
        return host;
    }

    /// <summary>값 입력 대화상자 (숫자·체크·고르기)</summary>
    public sealed record Field(string Key, string Label, string Type, object Value, double Min = 0, double Max = 0, (string id, string text)[] Options = null);
    public static Dictionary<string, object> AskForm(IWin32Window owner, string title, string okLabel, params Field[] fields)
    {
        using var f = new System.Windows.Forms.Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Font = new Font("Malgun Gothic", 9f) };
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        var getters = new Dictionary<string, Func<object>>();
        foreach (var fd in fields)
        {
            switch (fd.Type)
            {
                case "number":
                {
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, MaximumSize = new Size(360, 0) });
                    double v = Convert.ToDouble(fd.Value, CultureInfo.InvariantCulture);
                    var n = new NumericUpDown { Minimum = (decimal)fd.Min, Maximum = (decimal)fd.Max, DecimalPlaces = 0, Value = (decimal)Math.Clamp(v, fd.Min, fd.Max), Width = 200, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
                    p.Controls.Add(n); getters[fd.Key] = () => (double)n.Value;
                    break;
                }
                case "checkbox":
                {
                    var c = new CheckBox { Text = fd.Label, Checked = (bool)fd.Value, AutoSize = true, MaximumSize = new Size(360, 0) };
                    p.Controls.Add(c); getters[fd.Key] = () => c.Checked;
                    break;
                }
                case "select":
                {
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true });
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
                    foreach (var (_, t) in fd.Options) cb.Items.Add(t);
                    cb.SelectedIndex = Math.Max(0, Array.FindIndex(fd.Options, o => o.id == (string)fd.Value));
                    p.Controls.Add(cb); getters[fd.Key] = () => cb.SelectedIndex >= 0 ? fd.Options[cb.SelectedIndex].id : null;
                    break;
                }
                default:
                    p.Controls.Add(new Label { Text = fd.Label, AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Opt.Muted });
                    break;
            }
        }
        var ok = new System.Windows.Forms.Button { Text = okLabel ?? "확인", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new System.Windows.Forms.Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        p.Controls.Add(Row(ok, cancel));
        f.Controls.Add(p); f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        return getters.ToDictionary(k => k.Key, k => k.Value());
    }
}

/// <summary>문서 Extra 의 한 값 바꾸기 (실행취소 가능)</summary>
public sealed class ExtraEdit : Core.IEdit
{
    readonly string key; readonly System.Text.Json.Nodes.JsonNode before, after;
    public string Label { get; }
    public ExtraEdit(string label, string key, System.Text.Json.Nodes.JsonNode before, System.Text.Json.Nodes.JsonNode after) { Label = label; this.key = key; this.before = before?.DeepClone(); this.after = after?.DeepClone(); }
    public long Bytes => 1024 + (before?.ToJsonString().Length ?? 0) + (after?.ToJsonString().Length ?? 0);
    public Core.Rect Undo(Core.Document d) { Put(d, before); return Core.Rect.Empty; }
    public Core.Rect Redo(Core.Document d) { Put(d, after); return Core.Rect.Empty; }
    void Put(Core.Document d, System.Text.Json.Nodes.JsonNode v) { if (v == null) d.Extra.Remove(key); else d.Extra[key] = v.DeepClone(); }

    /// <summary>바꾸고 기록</summary>
    public static void Change(Core.Document d, string label, string key, Action change)
    {
        var b = d.Extra[key]?.DeepClone();
        change();
        d.Modified = true;
        App.History.Push(new ExtraEdit(label, key, b, d.Extra[key]));
    }
}
