// 아래 상태 줄 (웹 #statusbar, 높이 28, 12px 흐린 글자): [다른 모듈이 붙인 것: 작업 시간 등] 확대% · 각도 · 대칭 … 레이어 이름 · 크기 · 저장 상태.
// StatusStrip 그대로 (모듈이 ToolStripStatusLabel 을 붙일 수 있게) + 웹 모양으로 그리기. 모듈이 붙인 칸은 웹 판처럼 왼쪽 앞에 둔다.
using System.Drawing.Drawing2D;
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.UI.Chrome;

public sealed class StatusBar : StatusStrip
{
    static Font font;
    public static Font F => font ??= new Font(Theme.FontName, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    readonly ToolStripStatusLabel zoom, angle, sym, layer, size, saved, srv;
    readonly List<ToolStripItem> mine = new();
    bool adding;

    public StatusBar()
    {
        Dock = DockStyle.Bottom; AutoSize = false; Height = Theme.StatusH; SizingGrip = false; Stretch = true;
        Padding = new Padding(6, 0, 6, 0); BackColor = Theme.Panel; ForeColor = Theme.Muted; Font = F;
        Renderer = new Look(); ShowItemToolTips = true;
        LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;   // 빈칸(Spring) 대신 오른쪽 붙임 (숨은 칸이 있어도 어긋나지 않게)
        zoom = Btn("탭: 화면 맞춤", "view.fit");
        angle = Btn("탭: 회전 초기화", "view.resetRotation");
        sym = Btn("대칭 그리기", "view.symmetry");
        layer = Lbl(); size = Lbl(); saved = Lbl(); saved.ForeColor = Theme.Accent;
        srv = Lbl();   // 웹: 저장소 상태 (네이티브는 늘 이 PC 폴더라 비어 있음 — 자리만 같게)
        foreach (var r in new[] { layer, size, saved, srv }) r.Alignment = ToolStripItemAlignment.Right;
        adding = true;
        // 오른쪽 붙임 칸은 오른쪽 끝부터 놓이므로 거꾸로 넣음 → 화면에는 레이어 · 크기 · 저장 상태
        foreach (var it in new ToolStripItem[] { zoom, angle, sym, srv, saved, size, layer }) { Items.Add(it); mine.Add(it); }
        adding = false;
        App.DocChanged += SyncDoc; App.LayersChanged += SyncDoc; App.History.Changed += SyncDoc; App.SettingsChanged += SyncDoc;
    }

    // 웹 .st { padding: 0 8px } — 라벨 자체가 글자 양옆에 3px 쯤 두므로 5px 만 더함
    ToolStripStatusLabel Lbl() => new() { Margin = new Padding(1, 0, 1, 0), Padding = new Padding(5, 0, 5, 0), AutoSize = true, Font = F, ForeColor = Theme.Muted };
    ToolStripStatusLabel Btn(string tip, string cmd)
    {
        var l = Lbl(); l.ToolTipText = tip; l.Tag = "btn";
        l.Click += (_, _) => WebIds.Run(cmd);
        return l;
    }

    /// <summary>다른 모듈이 붙인 칸 (작업 시간·기록 중 등) 은 앞쪽에 (웹 판 상태 줄 순서)</summary>
    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        if (adding || mine.Contains(e.Item)) return;
        e.Item.Font = F;
        if (e.Item.ForeColor == SystemColors.ControlText || e.Item.ForeColor == Color.Empty) e.Item.ForeColor = Theme.Muted;
        // 넣는 도중에는 순서를 바꿀 수 없어 다음 차례에
        if (IsHandleCreated) BeginInvoke((Action)Reorder); else HandleCreated += (_, _) => BeginInvoke((Action)Reorder);
    }

    void Reorder()
    {
        if (IsDisposed || adding) return;
        var foreign = Items.Cast<ToolStripItem>().Where(i => !mine.Contains(i)).ToList();
        if (foreign.Count == 0 || Items.IndexOf(foreign[^1]) < Items.IndexOf(zoom)) return;
        adding = true;
        SuspendLayout();
        try
        {
            foreach (var it in foreign) Items.Remove(it);
            for (int i = 0; i < foreign.Count; i++) Items.Insert(i, foreign[i]);
        }
        finally { adding = false; ResumeLayout(true); }
    }

    public void SyncView(ViewXf v)
    {
        zoom.Text = $"{Math.Round(v.Zoom * 1000) / 10}%";
        int a = (int)Math.Round(((v.Angle % 360) + 360) % 360);
        angle.Text = $"{(a > 180 ? a - 360 : a)}°{(v.Flip ? " ⇋" : "")}";
    }

    public void SyncDoc()
    {
        var d = App.Doc;
        string s = App.Get("symmetry", "none");
        sym.Text = "대칭: " + (s switch { "none" => "끔", "v" => "좌우", "h" => "상하", "vh" => "상하좌우", _ => $"방사 {App.Get("symN", 6)}" });
        if (d == null) { layer.Text = size.Text = saved.Text = ""; return; }
        size.Text = $"{d.W}×{d.H}";
        layer.Text = d.Active != null ? "▣ " + d.Active.Name : "";
        saved.Text = d.Modified ? "● 수정됨" : d.FilePath != null ? "저장됨" : "";
    }

    sealed class Look : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Theme.Panel);
            using var b = new SolidBrush(Theme.Border);
            e.Graphics.FillRectangle(b, 0, 0, e.ToolStrip.Width, 1);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        protected override void OnRenderLabelBackground(ToolStripItemRenderEventArgs e)
        {
            var it = e.Item;
            if (it.BackColor != Color.Empty && it.BackColor != it.Owner.BackColor && it.BackColor != SystemColors.Control)
            {
                using var b = new SolidBrush(it.BackColor); e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, it.Size));
            }
            if ((string)it.Tag == "btn" && it.Selected)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var b = new SolidBrush(Theme.Panel3);
                using var p = Theme.Round(new RectangleF(0, (it.Height - 26) / 2f, it.Width, 26), 5);
                e.Graphics.FillPath(b, p);
            }
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var it = e.Item;
            Color c = (string)it.Tag == "btn" && it.Selected ? Theme.Text : it.ForeColor;
            TextRenderer.DrawText(e.Graphics, e.Text, it.Font, new Rectangle(Point.Empty, it.Size), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
}
