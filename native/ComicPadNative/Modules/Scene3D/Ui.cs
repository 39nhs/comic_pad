// 3D·버튜버 창에서 쓰는 작은 조절 부품 (웹 판 ui/controls.js 의 slider·toggle·segmented·colorInput·select·button·row·section 과 같은 역할).
// UI.Opt 와 달리 앱 설정 저장을 건드리지 않고, Sync() 로 값만 다시 읽는다.
using System.Globalization;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;
using ComicPadNative.UI.Dialogs;

namespace ComicPadNative.Modules.Scene3D;

public sealed class Ui3
{
    public const int W = 300;
    public readonly List<Action> Syncs = new();
    public void SyncAll() { foreach (var s in Syncs.ToList()) s(); }

    public static Font Bold => Web.BodyBold;

    public Control Section(string title, params Control[] items) => new WebSection(title, items);

    public static Label Help(string t) => new WebHelp(t);

    public static System.Windows.Forms.Button Button(string text, Action run, bool primary = false)
    {
        var b = new WebButton(text, primary ? BtnKind.Primary : BtnKind.Normal) { Margin = Padding.Empty };
        b.Click += (_, _) => MainFormRun(run, text);
        return b;
    }
    static void MainFormRun(Action a, string name) => UI.MainForm.Run(a, name);

    public static FlowLayoutPanel Row(params Control[] items)
    {
        var p = new WebRow { Gap = 4 };
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    /// <summary>이름 + 막대 + 값 (웹 slider). step 단위로 맞춤</summary>
    public Control Slider(string label, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt = null, bool log = false)
    {
        var s = new WebSlider(label, min, max, get, set, step, log, fmt ?? (v => Math.Abs(v) >= 100 || step >= 1 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture)));
        Action sync = s.Sync;
        Syncs.Add(sync); s.Disposed += (_, _) => Syncs.Remove(sync);
        return s;
    }

    public Control Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var c = new WebToggle(label) { Checked = get() };
        bool busy = false;
        c.CheckedChanged += (_, _) => { if (!busy) set(c.Checked); };
        void Sync() { if (c.IsDisposed) return; busy = true; c.Checked = get(); busy = false; }
        Syncs.Add(Sync); c.Disposed += (_, _) => Syncs.Remove(Sync);
        return c;
    }

    /// <summary>여러 개 중 하나 (웹 segmented)</summary>
    public Control Segmented((string id, string text)[] items, Func<string> get, Action<string> set)
    {
        var s = WebSeg.Of(items, get, set);
        Action sync = s.Sync;
        Syncs.Add(sync); s.Disposed += (_, _) => Syncs.Remove(sync);
        return s;
    }

    public (Control box, ComboBox combo) Select(string label, (string id, string text)[] items, Func<string> get, Action<string> set)
    {
        var box = new WebRow { Gap = 8 };
        if (label != null) box.Controls.Add(new Label { Text = label, ForeColor = Opt.Muted, AutoSize = true, Font = Web.Body });
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = label != null ? 220 : W, BackColor = Web.Field, ForeColor = Opt.Fg, FlatStyle = FlatStyle.Flat, MaxDropDownItems = 20, Font = Web.Body };
        foreach (var (_, t) in items) cb.Items.Add(t);
        bool busy = false;
        void Sync() { if (cb.IsDisposed) return; busy = true; cb.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.id == get())); busy = false; }
        Sync();
        cb.SelectedIndexChanged += (_, _) => { if (!busy && cb.SelectedIndex >= 0) set(items[cb.SelectedIndex].id); };
        cb.MouseWheel += (_, e) => { if (!cb.DroppedDown && e is HandledMouseEventArgs h) h.Handled = true; };
        Syncs.Add(Sync); cb.Disposed += (_, _) => Syncs.Remove(Sync);
        box.Controls.Add(cb.Flex());
        return (box, cb);
    }

    public Control ColorInput(string label, Func<string> get, Action<string> set)
    {
        var b = new WebColorButton(label, get, set, showHex: true) { Margin = Padding.Empty };
        Action sync = b.Sync;
        Syncs.Add(sync); b.Disposed += (_, _) => Syncs.Remove(sync);
        return b;
    }

    /// <summary>모든 부품을 어두운 판에 세로로 (스크롤, 웹 .s3d-side: 여백 4·10·20)</summary>
    public static Panel Scroller(out FlowLayoutPanel inner)
    {
        var host = new Panel { AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(10, 4, 10, 20) };
        inner = new WebStack { Location = new Point(10, 4) };
        host.Controls.Add(inner);
        return host;
    }

    /// <summary>값 입력 대화상자 (숫자·체크·고르기) — 모양은 UI.Dialogs.WebForm</summary>
    public sealed record Field(string Key, string Label, string Type, object Value, double Min = 0, double Max = 0, (string id, string text)[] Options = null);
    public static Dictionary<string, object> AskForm(IWin32Window owner, string title, string okLabel, params Field[] fields)
    {
        var spec = fields.Select(fd => new FormField
        {
            Key = fd.Type is "number" or "checkbox" or "select" ? fd.Key : null, Label = fd.Label, Type = fd.Type is "number" or "checkbox" or "select" ? fd.Type : "info",
            Value = fd.Type == "number" ? Convert.ToDouble(fd.Value, CultureInfo.InvariantCulture) : fd.Value, Min = fd.Min, Max = fd.Max, Options = fd.Options, Decimals = 0,
        }).ToList();
        return WebForm.Show(title, spec, okLabel ?? "확인", owner: owner);
    }
}

// <summary>문서 Extra 의 한 값 바꾸기 (실행취소 가능)</summary>
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
