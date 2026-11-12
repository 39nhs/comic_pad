// 애니·픽셀 패널에서 쓰는 작은 조절 부품 + 값 입력 대화상자 (웹 판 controls.js · dialogs.js form 과 같은 쓰임)
using System.Globalization;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;
using ComicPadNative.UI.Dialogs;

namespace ComicPadNative.Modules.Anim;

public static class Ui
{
    public static Color Bg => Opt.Bg; public static Color Bg2 => Opt.Bg2; public static Color Fg => Opt.Fg; public static Color Muted => Opt.Muted; public static Color Accent => Opt.Accent;

    public static FlowLayoutPanel Col(int width, params Control[] items)
    {
        var p = new WebStack { FillParent = false };
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    public static FlowLayoutPanel Row(params Control[] items)
    {
        var p = new WebRow();
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    /// <summary>제목 + 내용 (웹 section)</summary>
    public static Control Section(string title, params Control[] items) => new WebSection(title, items);

    public static Label Help(string t, int width = 280) => new WebHelp(t);
    public static Label Text(string t) => new() { Text = t, AutoSize = true, ForeColor = Fg, Font = Web.Body, Margin = new Padding(0, 4, 4, 0), UseMnemonic = false };

    public static Button Btn(string text, Action run, bool primary = false, string tip = null)
    {
        var b = new WebButton(text, primary ? BtnKind.Primary : BtnKind.Normal) { Margin = Padding.Empty };
        b.Click += (_, _) => UI.MainForm.Run(run, text);
        if (tip != null) new ToolTip().SetToolTip(b, tip);
        return b;
    }

    public static CheckBox Check(string label, Func<bool> get, Action<bool> set)
    {
        var c = new WebToggle(label) { Checked = get(), Margin = Padding.Empty };
        c.CheckedChanged += (_, _) => set(c.Checked);
        return c;
    }

    /// <summary>끌어서 바꾸는 막대 (웹 slider): begin(누를 때) → set(끄는 동안) → end(뗄 때, 실행취소 기록용)</summary>
    public static Control Slider(string label, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt = null, Action begin = null, Action end = null, int width = 280)
    {
        var s = new WebSlider(label, min, max, get, set, step, false, fmt ?? (v => v.ToString("0.##", CultureInfo.InvariantCulture))) { OnBegin = begin, OnEnd = end, Width = width };
        return s;
    }

    /// <summary>색 고르기 단추</summary>
    public static Button ColorBtn(string label, Func<string> get, Action<string> set) => new WebColorButton(label, get, set) { Margin = Padding.Empty };

    // ---------------------------------------------------------------------
    // 값 입력 대화상자 (웹 form — 모양은 UI.Dialogs.WebForm)
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
        var list = fields.Where(f => f != null).ToList();
        var spec = list.Select(fd => new FormField
        {
            Key = fd.Key, Label = fd.Label, Options = fd.Options, Step = fd.Step,
            Min = Math.Max(-1e9, fd.Min), Max = Math.Min(1e9, fd.Max),
            Type = fd.Type switch { "check" => "checkbox", "seg" => "segment", _ => fd.Type },
            Value = fd.Type == "number" ? Convert.ToDouble(fd.Value, CultureInfo.InvariantCulture) : fd.Value,
            Decimals = fd.Type == "number" ? (fd.Step % 1 == 0 ? 0 : fd.Step >= 0.1 ? 1 : 2) : -1,
        }).ToList();
        var vals = new Values();
        var live = new Label();
        var r = WebForm.Show(title, spec, okLabel, onChange: onChange == null ? null : (m, h) =>
        {
            foreach (var (k, v) in m) vals[k] = v;
            live.Text = "";
            onChange(vals, live);
            h.SetStatus(live.Text);
        });
        if (r == null) return null;
        var res = new Values();
        foreach (var (k, v) in r) res[k] = v;
        return res;
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
