// 웹 slider (controls.js): 34px 높이 둥근 막대, 값만큼 주황 채움, 왼쪽 이름·오른쪽 굵은 값.
// 아무 곳이나 눌러 끌기 (누른 위치로 바로), 포커스가 있을 때 휠·화살표 키, 값 두 번 눌러 직접 입력.
using System.Drawing.Drawing2D;
using System.Globalization;

namespace ComicPadNative.UI.Controls;

public class WebSlider : Control, IWebBlock, ISyncable
{
    public string Label;
    public double Min, Max, Step;
    public bool Log;
    readonly Func<double> get;
    readonly Action<double> set;
    public Func<double, string> Format;
    public Action OnBegin, OnEnd;
    /// <summary>막대 높이 (웹 34, 큰 버튼 40, 색 슬라이더 28, 아래 막대 44)</summary>
    public int TrackHeight = 34;
    bool dragging, hover;
    double shown = double.NaN;
    TextBox editor;

    public WebSlider(string label, double min, double max, Func<double> get, Action<double> set, double step = 0, bool log = false, Func<double, string> format = null)
    {
        Label = label; Min = min; Max = max; Step = step; Log = log && min > 0; this.get = get; this.set = set;
        Format = format ?? DefaultFormat;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Margin = new Padding(0, 4, 0, 4);
        Font = Web.Body; ForeColor = Theme.Text;
        if (Theme.Large) TrackHeight = 40;
        Height = TrackHeight; Width = 200;
        Cursor = Cursors.SizeWE;
        TabStop = true;
        AccessibleName = label;
    }

    string DefaultFormat(double v) => Math.Abs(v) >= 100 || Step >= 1 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);

    public double Value => SafeGet();
    double SafeGet() { try { return get(); } catch { return Min; } }

    public double ToT(double v) => Max <= Min ? 0 : Log ? Math.Log(Math.Max(Min, v) / Min) / Math.Log(Max / Min) : (v - Min) / (Max - Min);
    public double FromT(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double v = Log ? Min * Math.Pow(Max / Min, t) : Min + (Max - Min) * t;
        if (Step > 0) v = Math.Round(v / Step) * Step;
        return Math.Clamp(v, Min, Max);
    }

    public override Size GetPreferredSize(Size proposed) => new(proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : Width, TrackHeight);

    public void Sync() { if (!IsDisposed) { shown = double.NaN; Invalidate(); } }

    void Apply(double v)
    {
        try { set(v); } catch (Exception e) { App.Toast(e.Message); }
        shown = v;
        Invalidate(); Update();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        double v = SafeGet();
        var r = new Rectangle(0, (Height - TrackHeight) / 2, Width, TrackHeight);
        Web.Box(g, r, 7, Theme.Panel2);
        float t = (float)Math.Clamp(ToT(v), 0, 1);
        int fw = (int)Math.Round(r.Width * t);
        if (fw > 0)
        {
            // 웹: linear-gradient(90deg, rgba(232,163,61,.35), rgba(232,163,61,.55)) — 채운 폭 안에서
            var st = g.Save();
            using (var clip = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), 7)) g.SetClip(clip);
            var fr = new Rectangle(r.X, r.Y, fw, r.Height);
            using var br = new LinearGradientBrush(new Rectangle(fr.X - 1, fr.Y, fr.Width + 2, fr.Height), Color.FromArgb(89, 232, 163, 61), Color.FromArgb(140, 232, 163, 61), LinearGradientMode.Horizontal);
            g.FillRectangle(br, fr);
            g.Restore(st);
        }
        if (Focused && !dragging)
            Web.Box(g, r, 7, null, Color.FromArgb(120, 232, 163, 61));
        string val = Format(v);
        var vs = Web.Measure(val, Web.BodyBold);
        var vr = new Rectangle(r.Right - 10 - vs.Width - 2, r.Y, vs.Width + 2, r.Height);
        Web.Text(g, val, Web.BodyBold, vr, Theme.Text, Web.Right);
        Web.Text(g, Label, Font, new Rectangle(r.X + 10, r.Y, Math.Max(0, vr.X - r.X - 16), r.Height), ForeColor, Web.Left);
    }

    double ValueAt(int x) => FromT((x - 0.0) / Math.Max(1, Width));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks >= 2 && ValueArea().Contains(e.Location)) { BeginEdit(); return; }
        Focus();
        dragging = true; Capture = true;
        OnBegin?.Invoke();
        Apply(ValueAt(e.X));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) Apply(ValueAt(e.X));
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false; Capture = false;
        OnEnd?.Invoke();
        App.NotifySettings();
        Invalidate();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (dragging && !Capture) { dragging = false; OnEnd?.Invoke(); }
    }

    Rectangle ValueArea() { var vs = Web.Measure(Format(SafeGet()), Web.BodyBold); return new Rectangle(Width - 14 - vs.Width, 0, vs.Width + 14, Height); }

    double Nudge(int dir, bool big)
    {
        double v = SafeGet();
        if (Step > 0 && !Log) return Math.Clamp(v + dir * Step * (big ? 10 : 1), Min, Max);
        return FromT(ToT(v) + dir * (big ? 0.1 : 0.01));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // 포커스가 있을 때만 값 바꾸기 (아니면 패널 굴리기)
        if (!Focused) { base.OnMouseWheel(e); return; }
        if (e is HandledMouseEventArgs h) h.Handled = true;
        OnBegin?.Invoke();
        Apply(Nudge(Math.Sign(e.Delta), ModifierKeys.HasFlag(Keys.Shift)));
        OnEnd?.Invoke();
    }

    protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int dir = e.KeyCode is Keys.Right or Keys.Up ? 1 : e.KeyCode is Keys.Left or Keys.Down ? -1 : 0;
        if (dir != 0) { OnBegin?.Invoke(); Apply(Nudge(dir, e.Shift)); OnEnd?.Invoke(); e.Handled = true; }
        else if (e.KeyCode is Keys.Home or Keys.End) { OnBegin?.Invoke(); Apply(e.KeyCode == Keys.Home ? Min : Max); OnEnd?.Invoke(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.F2) { BeginEdit(); e.Handled = true; }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; }

    /// <summary>값 직접 입력 (웹: 값 두 번 탭 → prompt)</summary>
    public void BeginEdit()
    {
        if (editor != null) return;
        var va = ValueArea();
        editor = new TextBox { Text = SafeGet().ToString("0.###", CultureInfo.InvariantCulture), BorderStyle = BorderStyle.None, BackColor = Web.Field, ForeColor = Theme.Text, Font = Web.BodyBold, TextAlign = HorizontalAlignment.Right };
        int w = Math.Max(70, va.Width);
        editor.SetBounds(Width - w - 8, (Height - editor.PreferredHeight) / 2, w, editor.PreferredHeight);
        Controls.Add(editor);
        editor.Focus(); editor.SelectAll();
        bool done = false;
        void Done(bool ok)
        {
            if (done) return; done = true;
            var ed = editor; editor = null;
            if (ok && double.TryParse(ed.Text.Replace('%', ' ').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                // 퍼센트 표시 막대(0~1)에 "50" 을 넣으면 50% 로
                if (Max <= 1 && Min >= 0 && v > 1 && Format(1).EndsWith("%")) v /= 100;
                OnBegin?.Invoke(); Apply(Math.Clamp(v, Min, Max)); OnEnd?.Invoke(); App.NotifySettings();
            }
            BeginInvoke(() => { ed.Dispose(); Invalidate(); });
        }
        editor.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Done(true); e.SuppressKeyPress = true; } else if (e.KeyCode == Keys.Escape) { Done(false); e.SuppressKeyPress = true; } };
        editor.LostFocus += (_, _) => Done(true);
    }
}
