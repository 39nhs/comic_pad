// 알림 (웹 #toast-root .toast): 아래 가운데 둥근 알약, rgba(30,30,34,.95) 바탕 · #55555f 테두리 · 위로 쌓임.
// 아래 막대가 있으면 창 아래에서 104px 위, 없으면 44px 위 (웹 판과 같은 자리). 몇 초 뒤 저절로 사라짐.
using System.Drawing.Drawing2D;

namespace ComicPadNative.UI.Chrome;

public sealed class Toasts
{
    readonly Form host;
    readonly List<(Pill pill, System.Windows.Forms.Timer timer)> live = new();
    public Toasts(Form host) { this.host = host; host.Resize += (_, _) => Place(); }

    public void Show(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg) || host.IsDisposed) return;
        var p = new Pill(msg, host.ClientSize.Width);
        host.Controls.Add(p);
        p.BringToFront();
        var t = new System.Windows.Forms.Timer { Interval = Math.Clamp(2500 + msg.Length * 45, 3000, 9000) };
        t.Tick += (_, _) => Remove(p);
        live.Add((p, t));
        while (live.Count > 4) Remove(live[0].pill);
        Place();
        t.Start();
        p.Click += (_, _) => Remove(p);
    }

    void Remove(Pill p)
    {
        int i = live.FindIndex(x => x.pill == p);
        if (i < 0) return;
        var (pill, timer) = live[i];
        live.RemoveAt(i);
        timer.Stop(); timer.Dispose();
        host.Controls.Remove(pill); pill.Dispose();
        Place();
    }

    public int Count => live.Count;
    public string LastText => live.Count > 0 ? live[^1].pill.Msg : null;

    void Place()
    {
        if (host.IsDisposed) return;
        bool bottomBar = (host as MainForm)?.BottomBar.Visible == true;
        int y = host.ClientSize.Height - (bottomBar ? 104 : 44);
        for (int i = live.Count - 1; i >= 0; i--)
        {
            var p = live[i].pill;
            y -= p.Height;
            p.Location = new Point((host.ClientSize.Width - p.Width) / 2, y);
            p.BringToFront();
            y -= 6;
        }
    }

    sealed class Pill : Control
    {
        public readonly string Msg;
        static readonly Color Bg = Color.FromArgb(30, 30, 34), BorderC = ColorTranslator.FromHtml("#55555f");
        public Pill(string msg, int maxW)
        {
            Msg = msg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Font = Theme.Font;
            int lim = Math.Max(200, (int)(maxW * 0.8) - 32);
            var sz = TextRenderer.MeasureText(msg, Font, new Size(lim, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Size = new Size(Math.Min(lim, sz.Width) + 32 + 2, Math.Max(18, sz.Height) + 18 + 2);
            using var path = Theme.Round(new RectangleF(0, 0, Width, Height), Math.Min(20, Height / 2f));
            Region = new Region(path);
            Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Math.Min(20, Height / 2f))) using (var pen = new Pen(BorderC)) g.DrawPath(pen, p);
            TextRenderer.DrawText(g, Msg, Font, new Rectangle(17, 10, Width - 34, Height - 20), Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter);
        }
    }
}
