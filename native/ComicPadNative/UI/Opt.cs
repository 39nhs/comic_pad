// 도구 옵션·대화상자에서 쓰는 작은 조절 부품 (모든 모듈이 같은 모양으로) — 웹 app/js/ui/controls.js 와 같은 모양·동작.
// 실제 부품은 UI/Controls (WebSlider·WebToggle·WebSeg·WebButton·WebSelect …), 이 클래스는 예전 이름 그대로 쓰는 입구.
using System.Globalization;
using ComicPadNative.UI.Controls;
using ComicPadNative.UI.Dialogs;

namespace ComicPadNative.UI;

/// <summary>도구 옵션 판 (웹 .panel-scroll: 여백 4·10·12). 다른 쌓기 안에 들어가면 여백 없음</summary>
public sealed class OptPanel : WebStack
{
    public OptPanel() { Padding = new Padding(10, 4, 10, 12); }
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent is WebStack || Parent is FlowLayoutPanel || Parent is WebRow) Padding = Padding.Empty;
        else if (Parent is Panel { Padding.Left: >= 8 }) Padding = new Padding(0, 0, 0, 12);
        else Padding = new Padding(10, 4, 10, 12);
    }
}

public static class Opt
{
    public static readonly Color Bg = Theme.Panel, Bg2 = Theme.Panel2, Fg = Theme.Text, Muted = Theme.Muted, Accent = Theme.Accent;

    /// <summary>세로 쌓기 (도구 옵션 판)</summary>
    public static FlowLayoutPanel Panel(params Control[] items)
    {
        var p = new OptPanel();
        p.SuspendLayout();
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        p.ResumeLayout();
        return p;
    }

    /// <summary>구역 제목 (웹 .section-title — 흐린 작은 글, 앞 구역과 줄로 나눔)</summary>
    public static Label Title(string t) => new SectionLabel(t);
    /// <summary>도움말 (웹 .help)</summary>
    public static Label Help(string t) => new WebHelp(t);

    /// <summary>이름 + 끌어서 바꾸는 막대 + 숫자 (웹 slider)</summary>
    public static Control Slider(string label, double min, double max, Func<double> get, Action<double> set, bool pct = false, bool log = false, double step = 0, Action onEnd = null)
    {
        Func<double, string> fmt = pct ? v => $"{v * 100:0}%" : null;
        var s = new WebSlider(label, min, max, get, v => { set(v); App.NotifySettings(); }, step, log, fmt) { OnEnd = onEnd };
        Action sync = () => { if (!s.IsDisposed && !s.Capture) s.Sync(); };
        App.SettingsChanged += sync;
        s.Disposed += (_, _) => App.SettingsChanged -= sync;
        return s;
    }

    /// <summary>켜기/끄기 (웹 toggle)</summary>
    public static CheckBox Check(string label, Func<bool> get, Action<bool> set)
    {
        var c = new WebToggle(label) { Checked = get() };
        c.CheckedChanged += (_, _) => { set(c.Checked); App.NotifySettings(); };
        return c;
    }

    /// <summary>고르기 (웹 select: 왼쪽 흐린 이름 + 칸)</summary>
    public static Control Choice(string label, (string id, string text)[] items, Func<string> get, Action<string> set)
    {
        Action<string> s2 = v => { set(v); App.NotifySettings(); };
        return label == null ? new WebSelect(items, get, s2) : new WebSelectRow(label, items, get, s2);
    }

    /// <summary>여러 개 중 하나 (웹 segmented)</summary>
    public static Control Seg<T>((T id, string text)[] items, Func<T> get, Action<T> set) => WebSeg.Of(items, get, set);

    /// <summary>단추 (웹 .btn / .btn.primary)</summary>
    public static Button Button(string text, Action run, bool primary = false)
    {
        var b = new WebButton(text, primary ? BtnKind.Primary : BtnKind.Normal);
        b.Click += (_, _) => run?.Invoke();
        return b;
    }
    /// <summary>아이콘 단추 (웹 .icon-btn)</summary>
    public static WebButton IconButton(string icon, string tip, Action run)
    {
        var b = new WebButton("", BtnKind.Icon, icon);
        if (tip != null) new ToolTip().SetToolTip(b, tip);
        b.Click += (_, _) => run?.Invoke();
        return b;
    }

    /// <summary>줄 (웹 .row: 옆으로, 넘치면 다음 줄)</summary>
    public static FlowLayoutPanel Row(params Control[] items)
    {
        var p = new WebRow();
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    /// <summary>구역 (웹 section: 제목 + 내용, 아래 줄)</summary>
    public static Control Section(string title, params Control[] items) => new WebSection(title, items);

    /// <summary>색 칸 (웹 colorInput)</summary>
    public static Control Swatch(string label, Func<string> get, Action<string> set) => new WebSwatch(get, set, label);

    /// <summary>값 입력 대화상자: fields = (이름, 기본값) → 확인하면 값 목록</summary>
    public static double[] AskNumbers(string title, params (string label, double value, double min, double max)[] fields)
    {
        var list = fields.Select((f, i) => new FormField { Key = "n" + i, Label = f.label, Type = "number", Value = f.value, Min = f.min, Max = f.max, Step = f.value % 1 == 0 && f.min % 1 == 0 ? 1 : 0.01 }).ToList();
        var r = WebForm.Show(title, list);
        return r == null ? null : list.Select(f => Convert.ToDouble(r[f.Key], CultureInfo.InvariantCulture)).ToArray();
    }
}

/// <summary>Opt.Title 이 돌려주는 구역 제목 (Label 이라 예전 코드와 맞음)</summary>
public sealed class SectionLabel : Label, IWebBlock
{
    public SectionLabel(string text)
    {
        Text = text; Font = Web.Tiny; ForeColor = Theme.Muted; AutoSize = false; UseMnemonic = false; Margin = Padding.Empty;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 37;
    }
    bool First
    {
        get
        {
            if (Parent == null) return true;
            foreach (Control c in Parent.Controls) { if (!BlockLayout.Shown(c) || c.Dock != DockStyle.None) continue; return c == this; }
            return true;
        }
    }
    int Top0 => First ? 6 : 8 + 1 + 6;
    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : Web.Measure(Text, Font).Width + 2;
        return new Size(w, Top0 + 8 + Font.Height + 6);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        if (!First) using (var p = new Pen(Web.Sep)) g.DrawLine(p, 0, 8, Width, 8);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, Top0 + 8, Width, Font.Height + 2), ForeColor, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}
