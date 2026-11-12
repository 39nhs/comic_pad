// 웹 segmented (.seg): 어두운 바탕(#26262b) 안에 단추들, 고른 것은 panel3 + 아래 주황 줄. 넘치면 다음 줄, 줄마다 남은 폭을 나눠 가짐 (flex 1 1 auto).
namespace ComicPadNative.UI.Controls;

public class WebSeg : Control, IWebBlock, ISyncable
{
    readonly List<(object id, string text, string tip)> items;
    readonly Func<object> get;
    readonly Action<object> set;
    readonly List<Rectangle> rects = new();
    int hot = -1;
    readonly ToolTip tips = new();
    public event Action<object> Changed;

    public WebSeg(IEnumerable<(object id, string text)> items, Func<object> get, Action<object> set)
        : this(items.Select(i => (i.id, i.text, (string)null)), get, set) { }

    public WebSeg(IEnumerable<(object id, string text, string tip)> items, Func<object> get, Action<object> set)
    {
        this.items = items.ToList(); this.get = get; this.set = set;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Web.Body; ForeColor = Theme.Text;
        Margin = new Padding(0, 4, 0, 4);
        Height = 38; Cursor = Cursors.Hand; TabStop = true;
    }

    public static WebSeg Of<T>(IEnumerable<(T id, string text)> items, Func<T> get, Action<T> set)
        => new(items.Select(i => ((object)i.id, i.text)), () => get(), v => set((T)v));

    int BtnH => Theme.Large ? 38 : 32;

    public object Current { get { try { return get(); } catch { return null; } } }
    public void Sync() => Invalidate();

    /// <summary>폭 w 에서 단추 자리 (안쪽 여백 3, 간격 3)</summary>
    List<Rectangle> Lay(int w, out int height)
    {
        var res = new List<Rectangle>();
        int inner = Math.Max(10, w - 6);
        var nat = items.Select(i => Web.Measure(i.text, Font).Width + 16).ToList();
        int y = 3, i0 = 0;
        while (i0 < items.Count)
        {
            int x = 0, i1 = i0;
            while (i1 < items.Count && (i1 == i0 || x + 3 + nat[i1] <= inner)) { x += (i1 > i0 ? 3 : 0) + nat[i1]; i1++; }
            int n = i1 - i0, free = Math.Max(0, inner - x), cx = 3;
            for (int k = i0; k < i1; k++)
            {
                int extra = free / n + (k - i0 < free % n ? 1 : 0);
                int bw = Math.Min(inner, nat[k] + extra);
                res.Add(new Rectangle(cx, y, bw, BtnH));
                cx += bw + 3;
            }
            y += BtnH + 3; i0 = i1;
        }
        height = Math.Max(BtnH + 6, y);
        return res;
    }

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : items.Sum(i => Web.Measure(i.text, Font).Width + 19) + 3;
        Lay(w, out int h);
        return new Size(w, h);
    }

    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); if (items == null) return; rects.Clear(); rects.AddRange(Lay(Width, out _)); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (items == null) return; rects.Clear(); rects.AddRange(Lay(Width, out _)); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        if (rects.Count != items.Count) { rects.Clear(); rects.AddRange(Lay(Width, out _)); }
        Web.Box(g, ClientRectangle, 8, Web.SegBg);
        var cur = Current;
        for (int i = 0; i < items.Count && i < rects.Count; i++)
        {
            var r = rects[i];
            bool on = Equals(items[i].id, cur) || cur != null && items[i].id != null && items[i].id.ToString() == cur.ToString() && items[i].id.GetType() == typeof(string);
            if (on)
            {
                Web.Box(g, r, 6, Theme.Panel3);
                var st = g.Save();
                using (var clip = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), 6)) g.SetClip(clip);
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X, r.Bottom - 2, r.Width, 2);
                g.Restore(st);
            }
            else if (i == hot) Web.Box(g, r, 6, Color.FromArgb(28, 255, 255, 255));
            Web.Text(g, items[i].text, Font, new Rectangle(r.X + 4, r.Y, r.Width - 8, r.Height), on ? Theme.Text : Theme.Muted, Web.Center);
        }
        if (Focused && ShowFocusCues) Web.Box(g, ClientRectangle, 8, null, Color.FromArgb(120, Theme.Accent));
    }

    int Hit(Point p) { for (int i = 0; i < rects.Count; i++) if (rects[i].Contains(p)) return i; return -1; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = Hit(e.Location);
        if (h != hot)
        {
            hot = h; Invalidate();
            tips.SetToolTip(this, h >= 0 ? items[h].tip ?? "" : "");
        }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int h = Hit(e.Location);
        if (h >= 0 && e.Button == MouseButtons.Left) Pick(h);
    }
    void Pick(int i)
    {
        try { set(items[i].id); } catch (Exception ex) { App.Toast(ex.Message); }
        Invalidate();
        Changed?.Invoke(items[i].id);
        App.NotifySettings();
    }
    protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int cur = items.FindIndex(i => Equals(i.id, Current));
        if (e.KeyCode == Keys.Right && cur < items.Count - 1) Pick(cur + 1);
        else if (e.KeyCode == Keys.Left && cur > 0) Pick(cur - 1);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
}
