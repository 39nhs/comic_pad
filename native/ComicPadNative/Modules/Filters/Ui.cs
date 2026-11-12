// 이 모듈의 대화상자 부품: 웹 form() 같은 값 입력 창(숫자·켜기·색·고르기·안내), 끌어서 바꾸는 막대, 색 단추.
// 값이 바뀔 때마다 onChange 를 불러서 미리보기에 쓸 수 있다.
using System.Globalization;
using ComicPadNative.Core;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;
using ComicPadNative.UI.Dialogs;

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
    /// <summary>빈 대화상자 (웹 .modal 모양 — 머리·테두리·어두운 뒤판). 내용은 Column() 등으로 채움</summary>
    public static Form Dialog(string title)
    {
        var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 12, 16, 12), ShowInTaskbar = false,
            Font = Web.Body,
        };
        return UI.Dialogs.Modals.Adopt(f);
    }

    public static FlowLayoutPanel Column() => new WebStack { FillParent = false, MinimumSize = new Size(408, 0) };
    public static FlowLayoutPanel RowP() => new WebRow { Margin = new Padding(0, 4, 0, 4) };
    public static Label Lbl(string t, bool muted = false, int max = 360) => new() { Text = t, AutoSize = true, MaximumSize = new Size(max, 0), ForeColor = muted ? Opt.Muted : Opt.Muted, Font = muted ? Web.Small : Web.Small, Margin = new Padding(0, 6, 0, 4), UseMnemonic = false };
    public static Label Section(string t) => new SectionLabel(t);

    public static Button Btn(string text, Action run, bool primary = false)
    {
        var b = new WebButton(text, primary ? BtnKind.Primary : BtnKind.Normal);
        if (run != null) b.Click += (_, _) => run();
        return b;
    }

    /// <summary>이름 + 막대 + 값 (웹 slider). 값을 두 번 눌러 직접 입력. set 은 값이 바뀔 때마다</summary>
    public static Control Slider(string label, double min, double max, double step, Func<double> get, Action<double> set, bool log = false, Func<double, string> fmt = null)
    {
        var s = new WebSlider(label, min, max, get, set, step, log, fmt ?? (v => step >= 1 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture)));
        s.Tag = (Action)s.Sync;
        return s;
    }

    /// <summary>슬라이더 값 다시 읽기 (자동 버튼 등)</summary>
    public static void SyncAll(Control root)
    {
        foreach (Control c in root.Controls) { if (c.Tag is Action a) a(); else if (c is ISyncable sy) sy.Sync(); SyncAll(c); }
    }

    public static Button ColorBtn(string label, Func<string> get, Action<string> set)
    {
        var b = new WebColorButton(label, get, set, showHex: true);
        b.Tag = (Action)b.Sync;
        return b;
    }

    public static Control Combo((string id, string text)[] items, Func<string> get, Action<string> set, int width = 250) => new WebSelect(items, get, set) { Width = width };

    public static CheckBox Check(string label, Func<bool> get, Action<bool> set)
    {
        var c = new WebToggle(label) { Checked = get() };
        c.CheckedChanged += (_, _) => set(c.Checked);
        return c;
    }

    /// <summary>웹 form(): 값 입력 창 (모양은 UI.Dialogs.WebForm). 확인하면 값, 취소면 null</summary>
    public static Values Ask(string title, Field[] fields, string okLabel = "확인", Action<Values> onChange = null)
    {
        var vals = new Values();
        var spec = new List<FormField>();
        foreach (var fd in fields)
        {
            switch (fd.Type)
            {
                case "number": vals.Map[fd.Key] = fd.Num; spec.Add(new FormField { Key = fd.Key, Label = fd.Label, Type = "number", Value = fd.Num, Min = fd.Min, Max = fd.Max, Step = fd.Step, Decimals = fd.Step >= 1 && fd.Num % 1 == 0 ? 0 : fd.Step >= 0.1 ? 2 : 3 }); break;
                case "check": vals.Map[fd.Key] = fd.Bool; spec.Add(new FormField { Key = fd.Key, Label = fd.Label, Type = "checkbox", Value = fd.Bool }); break;
                case "color": vals.Map[fd.Key] = fd.Text; spec.Add(new FormField { Key = fd.Key, Label = fd.Label, Type = "color", Value = fd.Text }); break;
                case "choice": vals.Map[fd.Key] = fd.Text; spec.Add(new FormField { Key = fd.Key, Label = fd.Label, Type = "select", Value = fd.Text, Options = fd.Options }); break;
                default: spec.Add(new FormField { Type = "info", Label = fd.Label }); break;
            }
        }
        FormHandle handle = null;
        foreach (var fd in fields.Where(x => x.Type == "number")) { var k = fd.Key; vals.SetNum[k] = v => handle?.Set(k, v); }
        var r = WebForm.Show(title, spec, okLabel, onChange: onChange == null ? null : (m, h) =>
        {
            handle = h;
            foreach (var (k, v) in m) vals.Map[k] = v;
            try { onChange(vals); } catch (Exception ex) { App.Toast(ex.Message); }
        });
        if (r == null) return null;
        foreach (var (k, v) in r) vals.Map[k] = v;
        return vals;
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
