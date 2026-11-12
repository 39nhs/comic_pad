// 웹 form() (ui/dialogs.js) 과 같은 값 입력 창 — 모든 모듈의 Form/Ask 대화상자가 이것 하나를 쓴다.
//   필드 종류: text | textarea | number | checkbox | select | segment | color | info | date | slider
//   .form (세로, 칸 사이 10px) / .field (흐린 12px 이름 + 입력 칸) / .field.check (네모 칸 + 이름) / 아래 [extra…] [취소] [확인(primary)]
using System.Globalization;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.UI.Dialogs;

public sealed class FormField
{
    public string Key, Label, Type = "text";
    public object Value;
    public (string id, string text)[] Options;
    public double Min = -1e9, Max = 1e9, Step = 1;
    public int Decimals = -1;
    public string Hint;
    public bool Log;
    public Func<double, string> Format;
    public int Rows = 4;
}

/// <summary>창이 떠 있는 동안 값을 바꾸거나 알림 글을 쓰는 손잡이 (onChange 에서)</summary>
public sealed class FormHandle
{
    internal readonly Dictionary<string, Action<object>> setters = new();
    public readonly Label Status = new WebHelp("") { Visible = false };
    public ModalForm Form { get; internal set; }
    public void Set(string key, object v) { if (setters.TryGetValue(key, out var s)) s(v); }
    public void SetStatus(string text) { Status.Text = text ?? ""; Status.Visible = !string.IsNullOrEmpty(text); Form?.Fit(); }
}

/// <summary>form 의 네모 체크 칸 (웹 .field.check input 22×22)</summary>
public sealed class WebCheck : CheckBox
{
    bool hover;
    public WebCheck(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Text = text; Font = Web.Body; ForeColor = Theme.Text; BackColor = Color.Transparent; AutoSize = true; UseMnemonic = false;
        Margin = new Padding(0, 2, 0, 2); Cursor = Cursors.Hand;
    }
    public override Size GetPreferredSize(Size proposed)
    {
        int max = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width - 30 : int.MaxValue / 4;
        var one = Web.Measure(Text, Font);
        if (one.Width <= max) return new Size(30 + one.Width + 2, Math.Max(26, one.Height + 4));
        var w = Web.MeasureWrap(Text, Font, max);
        return new Size(30 + w.Width + 2, Math.Max(26, w.Height + 6));
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        var bx = new Rectangle(0, (Height - 22) / 2, 22, 22);
        if (Height > 30) bx.Y = 2;
        Web.Box(g, bx, 4, Checked ? Theme.Accent : Web.Field, Checked ? Theme.Accent : Focused ? Theme.Text : hover ? Web.Mix(Theme.Line, Theme.Text, 0.4) : Theme.Line, Focused && !Checked ? 2 : 1);
        if (Checked)
        {
            Web.Smooth(g);
            using var p = new Pen(Web.OnDark, 2.2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawLines(p, new[] { new PointF(bx.X + 5.5f, bx.Y + 11.5f), new PointF(bx.X + 9.5f, bx.Y + 15.5f), new PointF(bx.X + 16.5f, bx.Y + 7f) });
        }
        var tr = new Rectangle(30, 0, Width - 30, Height);
        var one = Web.Measure(Text, Font);
        if (one.Width <= tr.Width) Web.Text(g, Text, Font, tr, Enabled ? ForeColor : Theme.Muted, Web.Left);
        else Web.Text(g, Text, Font, new Rectangle(30, 3, tr.Width, Height - 3), Enabled ? ForeColor : Theme.Muted, Web.Wrap);
    }
}

public static class WebForm
{
    /// <summary>값 입력 창. 확인 → {키: 값}, 취소 → null, extra 단추 → {"__extra": 값}</summary>
    public static Dictionary<string, object> Show(string title, IEnumerable<FormField> fieldList, string okLabel = "확인", string cancelLabel = "취소",
        (string label, string value)[] extra = null, int width = 440, Action<Dictionary<string, object>, FormHandle> onChange = null, Control extraContent = null, IWin32Window owner = null)
    {
        var fields = fieldList.Where(f => f != null).ToList();
        var vals = new Dictionary<string, object>();
        var handle = new FormHandle();
        using var f = new ModalForm(title, width);
        handle.Form = f;
        var form = new WebStack();
        bool building = true;
        void Changed() { if (building || onChange == null) return; try { onChange(vals, handle); } catch (Exception ex) { App.Toast(ex.Message); } }
        Control Field(string label, Control input, string hint = null)
        {
            var fs = new WebStack { Margin = new Padding(0, 0, 0, 10) };
            if (!string.IsNullOrEmpty(label)) fs.Controls.Add(new Label { Text = label, AutoSize = true, Font = Web.Small, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 4), UseMnemonic = false });
            input.Margin = Padding.Empty;
            fs.Controls.Add(input);
            if (!string.IsNullOrEmpty(hint)) fs.Controls.Add(new Label { Text = hint, AutoSize = true, Font = Web.Small, ForeColor = Theme.Muted, Margin = new Padding(0, 4, 0, 0), UseMnemonic = false });
            return fs;
        }
        foreach (var fd in fields)
        {
            string key = fd.Key;
            switch (fd.Type)
            {
                case "info":
                    form.Controls.Add(new WebHelp(fd.Label) { Margin = new Padding(0, 0, 0, 10) });
                    break;
                case "checkbox": case "check":
                {
                    var c = new WebCheck(fd.Label) { Checked = fd.Value is bool b && b, Tag = key };
                    vals[key] = c.Checked;
                    c.CheckedChanged += (_, _) => { vals[key] = c.Checked; Changed(); };
                    handle.setters[key] = v => c.Checked = v is bool bb && bb;
                    var fs = new WebStack { Margin = new Padding(0, 0, 0, 10) }; fs.Controls.Add(c.Stretch());
                    if (!string.IsNullOrEmpty(fd.Hint)) fs.Controls.Add(new WebHelp(fd.Hint) { Margin = new Padding(30, 2, 0, 0) });
                    form.Controls.Add(fs);
                    break;
                }
                case "number":
                {
                    double v = ToD(fd.Value);
                    var n = new WebNumber(v, fd.Min, fd.Max, fd.Step, fd.Decimals) { Tag = key };
                    vals[key] = n.Value;
                    n.ValueChanged += x => { vals[key] = x; Changed(); };
                    handle.setters[key] = x => n.Value = ToD(x);
                    form.Controls.Add(Field(fd.Label, n, fd.Hint));
                    break;
                }
                case "slider":
                {
                    vals[key] = ToD(fd.Value);
                    var s = new WebSlider(fd.Label, fd.Min, fd.Max, () => (double)vals[key], x => { vals[key] = x; Changed(); }, fd.Step, fd.Log, fd.Format) { Tag = key };
                    handle.setters[key] = x => { vals[key] = ToD(x); s.Sync(); };
                    form.Controls.Add(Field(null, s, fd.Hint));
                    break;
                }
                case "select":
                {
                    var opts = fd.Options ?? Array.Empty<(string, string)>();
                    string cur = fd.Value?.ToString();
                    if (opts.Length > 0 && !opts.Any(o => o.id == cur)) cur = opts[0].id;
                    vals[key] = cur;
                    var sel = new WebSelect(opts, () => (string)vals[key], x => { vals[key] = x; Changed(); }) { Tag = key };
                    handle.setters[key] = x => { vals[key] = x?.ToString(); sel.Sync(); };
                    form.Controls.Add(Field(fd.Label, sel, fd.Hint));
                    break;
                }
                case "segment": case "seg":
                {
                    var opts = fd.Options ?? Array.Empty<(string, string)>();
                    vals[key] = fd.Value?.ToString();
                    var seg = new WebSeg(opts.Select(o => ((object)o.id, o.text)), () => vals[key], x => { vals[key] = x?.ToString(); Changed(); }) { Tag = key };
                    handle.setters[key] = x => { vals[key] = x?.ToString(); seg.Sync(); };
                    form.Controls.Add(Field(fd.Label, seg, fd.Hint));
                    break;
                }
                case "color":
                {
                    vals[key] = fd.Value?.ToString() ?? "#000000";
                    var sw = new WebSwatch(() => (string)vals[key], x => { vals[key] = x; Changed(); }) { Tag = key };
                    handle.setters[key] = x => { vals[key] = x?.ToString(); sw.Sync(); };
                    form.Controls.Add(Field(fd.Label, sw.Inline(), fd.Hint));
                    break;
                }
                case "date":
                {
                    string ds = fd.Value?.ToString();
                    var row = new WebRow { Margin = Padding.Empty };
                    var on = new WebCheck("") { Checked = DateTime.TryParse(ds, out _), Margin = new Padding(0, 0, 4, 0) };
                    var dt = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 170, CalendarMonthBackground = Web.Field, Font = Web.Body };
                    if (DateTime.TryParse(ds, out var dd)) dt.Value = dd;
                    dt.Enabled = on.Checked;
                    void Upd() { vals[key] = on.Checked ? dt.Value.ToString("yyyy-MM-dd") : null; Changed(); }
                    vals[key] = on.Checked ? dt.Value.ToString("yyyy-MM-dd") : null;
                    on.CheckedChanged += (_, _) => { dt.Enabled = on.Checked; Upd(); };
                    dt.ValueChanged += (_, _) => Upd();
                    row.Controls.Add(on); row.Controls.Add(dt);
                    form.Controls.Add(Field(fd.Label, row, fd.Hint));
                    break;
                }
                default:
                {
                    bool multi = fd.Type == "textarea";
                    var t = new WebInput(fd.Value?.ToString() ?? "", multi) { Tag = key, Rows = fd.Rows };
                    vals[key] = t.Text;
                    t.TextChanged += (_, _) => { vals[key] = t.Text; Changed(); };
                    handle.setters[key] = x => t.Text = x?.ToString() ?? "";
                    form.Controls.Add(Field(fd.Label, t, fd.Hint));
                    break;
                }
            }
        }
        if (extraContent != null) form.Controls.Add(extraContent);
        form.Controls.Add(handle.Status);
        // 마지막 칸 아래 여백은 빼기 (웹 gap)
        var last = form.Controls.Cast<Control>().LastOrDefault(c => c != handle.Status);
        if (last != null) last.Margin = new Padding(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        f.SetContent(form);
        string hit = null;
        foreach (var (label, value) in extra ?? Array.Empty<(string, string)>())
        {
            string v = value;
            f.AddAction(label, () => { hit = v; return true; }, result: DialogResult.Abort);
        }
        f.AddAction(cancelLabel, () => true, result: DialogResult.Cancel);
        f.AddAction(okLabel, () => true, primary: true, result: DialogResult.OK);
        building = false;
        f.Shown += (_, _) => Changed();
        var r = f.ShowDialog(owner ?? DefaultOwner());
        if (hit != null) return new Dictionary<string, object> { ["__extra"] = hit };
        return r == DialogResult.OK ? vals : null;
    }

    internal static IWin32Window DefaultOwner() => Form.ActiveForm is Form af && af.Visible && af.Modal ? af : App.Main;

    static double ToD(object o)
    {
        if (o == null) return 0;
        if (o is double d) return d;
        if (o is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue(out double jd)) return jd;
        try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; }
    }
}
