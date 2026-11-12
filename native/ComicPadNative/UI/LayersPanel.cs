// 레이어 패널 (웹 ui/panels.js layersPanel 과 같은 모양·동작):
//  - 위 아이콘 막대: + 폴더 벡터 러프 복제 병합 위 아래 삭제 여러 개
//  - 여러 개를 골랐을 때 일괄 막대 (보이기/숨기기·폴더로 묶기·삭제·선택 해제)
//  - 속성: 불투명도 막대, 합성, 칩 (클리핑·투명잠금·잠금·밑그림·파란선·참조·+마스크·효과·톤·러프·러프 색)
//  - 목록: 눈·썸네일(폴더는 펼침 꺾쇠+폴더)·이름·"100% 표준"·표시·끌기 손잡이, 고른 줄은 주황 테두리. 손잡이를 끌어 순서·폴더에 넣기,
//    두 번 눌러 이름 바꾸기, 오른쪽 클릭 메뉴, 맨 아래 용지 줄 (보이기·질감·색)
using ComicPadNative.Core;
using ComicPadNative.UI.Controls;
using ComicPadNative.UI.Dialogs;

namespace ComicPadNative.UI;

public sealed class LayersPanel : UserControl
{
    readonly WebRow toolbar = new() { Gap = 1, Margin = Padding.Empty, Padding = new Padding(4), CenterY = true };
    readonly WebRow multiBar = new() { Gap = 4, Margin = Padding.Empty, Padding = new Padding(6), BackColor = Web.MultiBar, Visible = false };
    readonly WebStack props = new() { Padding = new Padding(8, 4, 8, 4), FillParent = false };
    readonly LayerList list;
    readonly WebSlider opacity;
    readonly WebSelectRow blend;
    readonly WebRow flags = new() { Gap = 4, Margin = new Padding(0, 4, 0, 0) };
    readonly List<(WebButton b, Func<Layer, bool> on)> flagBtns = new();
    WebButton maskBtn, multiBtn;
    readonly Panel top = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Panel };
    float opBefore;
    bool editingOpacity;

    /// <summary>여러 개 고르기 (웹 doc.multi) — 레이어 id</summary>
    public static readonly HashSet<string> Multi = new();
    internal static bool MultiMode;

    public LayersPanel()
    {
        BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Web.Body;
        DoubleBuffered = true;

        // ---- 아이콘 막대 ----
        WebButton T(string icon, string text, string tip, string cmd)
        {
            var b = new WebButton(text ?? "", text != null ? BtnKind.LayerToolText : BtnKind.LayerTool, icon) { Margin = Padding.Empty };
            new ToolTip().SetToolTip(b, tip);
            b.Click += (_, _) => Web.Run(cmd);
            return b;
        }
        toolbar.Controls.AddRange(new Control[]
        {
            T("plus", null, "새 레이어", "layer.new"), T("folder", null, "새 폴더", "layer.newFolder"),
            T(null, "벡터", "새 벡터 레이어", "layer.newVector"), T(null, "러프", "새 러프 레이어", "layer.newRough"),
            T("copy", null, "복제", "layer.duplicate"), T("merge", null, "아래로 병합", "layer.mergeDown"),
            T("up", null, "위로", "layer.up"), T("down", null, "아래로", "layer.down"), T("trash", null, "삭제", "layer.delete"),
        });
        multiBtn = new WebButton("여러 개", BtnKind.LayerToolText) { Margin = Padding.Empty };
        new ToolTip().SetToolTip(multiBtn, "여러 레이어 선택 (Ctrl/Shift+클릭과 같음)");
        multiBtn.Click += (_, _) =>
        {
            MultiMode = !MultiMode; multiBtn.On = MultiMode;
            Multi.Clear();
            if (MultiMode && App.Active != null) Multi.Add(App.Active.Id);
            Rebuild();
        };
        toolbar.Controls.Add(multiBtn);
        var tbWrap = new SepPanel(toolbar);

        // ---- 속성 ----
        opacity = new WebSlider("불투명도", 0, 1, () => App.Active?.Opacity ?? 1, v =>
        {
            var l = App.Active; if (l == null) return;
            if (!editingOpacity) { editingOpacity = true; opBefore = l.Opacity; }
            l.Opacity = (float)v; App.InvalidateAll();
        }, 0.01, false, v => $"{Math.Round(v * 100)}%") { Margin = new Padding(0, 4, 0, 4) };
        opacity.OnBegin = () => { var l = App.Active; if (l != null) { opBefore = l.Opacity; editingOpacity = true; } };
        opacity.OnEnd = () =>
        {
            var l = App.Active; if (l == null || !editingOpacity) return;
            editingOpacity = false;
            float v = l.Opacity; l.Opacity = opBefore;
            if (Math.Abs(v - opBefore) > 0.0001f) Prop("불투명도", x => x.Opacity = v);
        };
        blend = new WebSelectRow("합성", Px.Modes, () => App.Active?.Blend ?? "source-over", v => Prop("합성 모드", l => l.Blend = v)) { Margin = new Padding(0, 4, 0, 4) };
        WebButton Flag(string label, string tip, Func<Layer, bool> on, Action click)
        {
            var b = new WebButton(label, BtnKind.Flag) { Margin = Padding.Empty };
            new ToolTip().SetToolTip(b, tip);
            b.Click += (_, _) => { if (App.Active != null) click(); };
            flagBtns.Add((b, on));
            flags.Controls.Add(b);
            return b;
        }
        Flag("클리핑", "아래 레이어에서 클리핑", l => l.Clip, () => Prop("클리핑", l => l.Clip = !l.Clip));
        Flag("투명잠금", "투명 픽셀 잠금", l => l.AlphaLock, () => Prop("투명 픽셀 잠금", l => l.AlphaLock = !l.AlphaLock));
        Flag("잠금", "레이어 잠금", l => l.Locked, () => Prop("잠금", l => l.Locked = !l.Locked));
        Flag("밑그림", "밑그림 (내보내기·채우기 참조 제외)", l => l.Draft, () => Prop("밑그림", l => l.Draft = !l.Draft));
        Flag("파란선", "레이어 컬러", l => !string.IsNullOrEmpty(l.Tint), () => Prop("레이어 컬러", l => l.Tint = string.IsNullOrEmpty(l.Tint) ? "#3d7be0" : null));
        Flag("참조", "참조 레이어 (채우기·자동선택 기준)", l => l.Ref, () => Prop("참조 레이어", l => l.Ref = !l.Ref));
        maskBtn = Flag("+마스크", "레이어 마스크: 없으면 만들고, 있으면 마스크 편집 전환", l => l.EditMask && l.Mask != null, () =>
        {
            var l = App.Active;
            if (l.Mask == null) LayerCommands.AddMask();
            else { l.EditMask = !l.EditMask; App.NotifyLayers(); }
        });
        Flag("효과", "레이어 효과 (테두리/그림자/광선)", l => l.Fx != null, () => Web.Run("layer.fx"));
        Flag("톤", "톤화 (망점)", l => l.Tone != null, () => Web.Run("layer.tone"));
        Flag("러프", "러프 전용 레이어 (러프 연필은 여기서만)", l => l.Rough, () => Web.Run("layer.rough"));
        Flag("러프 색", "러프 색 바꾸기", _ => false, () => Web.Run("layer.roughColor"));
        props.Controls.Add(opacity); props.Controls.Add(blend); props.Controls.Add(flags);
        var propsWrap = new SepPanel(props);

        list = new LayerList(this) { Dock = DockStyle.Fill };
        top.Controls.Add(propsWrap); propsWrap.Dock = DockStyle.Top;
        top.Controls.Add(multiBar); multiBar.Dock = DockStyle.Top;
        top.Controls.Add(tbWrap); tbWrap.Dock = DockStyle.Top;
        Controls.Add(list);
        Controls.Add(top);

        App.LayersChanged += Rebuild;
        App.DocChanged += () => { list.ClearThumbs(); Multi.Clear(); Rebuild(); };
        App.History.Changed += Rebuild;
        App.Invalidated += _ => list.PixelsMaybeChanged();
        Rebuild();
    }

    /// <summary>아래에 줄 긋는 칸 (.layer-toolbar / .layer-props 의 border-bottom)</summary>
    sealed class SepPanel : Panel
    {
        readonly Control inner;
        public SepPanel(Control c)
        {
            inner = c; BackColor = Theme.Panel; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(0, 0, 0, 1);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            Controls.Add(c);
        }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = Parent?.ClientSize.Width ?? Math.Max(proposed.Width, 300);
            return new Size(w, inner.GetPreferredSize(new Size(w, 0)).Height + 1);
        }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); inner.SetBounds(0, 0, Width, Math.Max(1, Height - 1)); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p = new Pen(Web.Sep); e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1); }
    }

    internal static void Prop(string label, Action<Layer> change)
    {
        var l = App.Active;
        if (l == null) return;
        App.History.Struct(App.Doc, label, () => change(l));
        App.NotifyLayers(); App.InvalidateAll();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); top?.PerformLayout(); }

    void Rebuild()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(Rebuild); return; }
        var d = App.Doc;
        if (d != null) Multi.RemoveWhere(id => d.ById(id) == null);
        BuildMultiBar();
        SyncProps();
        list.Rebuild();
    }

    void BuildMultiBar()
    {
        int n = Multi.Count;
        bool show = n > 1;
        if (show)
        {
            multiBar.SuspendLayout();
            foreach (Control c in multiBar.Controls.Cast<Control>().ToList()) c.Dispose();
            multiBar.Controls.Add(new Label { Text = $"{n}개 선택", AutoSize = true, Font = Web.F(12, true), ForeColor = Theme.Text, Margin = new Padding(0, 0, 4, 0), BackColor = Color.Transparent });
            WebButton B(string t, Action a) { var b = new WebButton(t) { Margin = Padding.Empty, Font = Web.Small, MinimumSize = new Size(0, 28) }; b.Click += (_, _) => a(); multiBar.Controls.Add(b); return b; }
            B("보이기/숨기기", MultiVisible);
            B("폴더로 묶기", MultiGroup);
            B("삭제", MultiDelete);
            B("선택 해제", () => { MultiMode = false; multiBtn.On = false; Multi.Clear(); Rebuild(); });
            multiBar.ResumeLayout();
        }
        if (multiBar.Visible != show) { multiBar.Visible = show; top.PerformLayout(); }
    }

    List<Layer> MultiLayers() { var d = App.Doc; return d == null ? new() : d.Layers.Where(l => Multi.Contains(l.Id)).ToList(); }
    void MultiVisible()
    {
        var ls = MultiLayers(); if (ls.Count == 0) return;
        bool show = ls.Any(l => !l.Visible);
        App.History.Struct(App.Doc, show ? "보이기" : "숨기기", () => { foreach (var l in ls) l.Visible = show; });
        App.NotifyLayers(); App.InvalidateAll();
    }
    void MultiDelete()
    {
        var d = App.Doc; var ls = MultiLayers(); if (ls.Count == 0) return;
        if (ls.Count >= d.Layers.Count) { App.Toast("레이어를 모두 지울 수는 없습니다"); return; }
        App.History.Struct(d, "레이어 삭제", () =>
        {
            var gone = new HashSet<Layer>(ls);
            foreach (var l in ls.Where(x => x.IsFolder)) foreach (var k in d.Layers.Where(x => d.IsInside(x, l))) gone.Add(k);
            foreach (var g in gone) d.Layers.Remove(g);
            if (d.ById(d.ActiveId) == null) d.ActiveId = d.Layers.Count > 0 ? d.Layers[^1].Id : null;
        });
        Multi.Clear();
        App.NotifyLayers(); App.InvalidateAll();
    }
    void MultiGroup()
    {
        var d = App.Doc; var ls = MultiLayers(); if (ls.Count == 0) return;
        App.History.Struct(d, "폴더로 묶기", () =>
        {
            var topL = ls[^1];
            var f = new Layer(d, $"폴더 {d.Layers.Count(l => l.IsFolder) + 1}", "folder") { Parent = topL.Parent };
            d.Layers.Insert(d.Layers.IndexOf(topL) + 1, f);
            foreach (var l in ls) if (!ls.Any(o => o != l && o.IsFolder && d.IsInside(l, o))) l.Parent = f.Id;
            d.ActiveId = f.Id;
        });
        Multi.Clear(); MultiMode = false; multiBtn.On = false;
        App.NotifyLayers(); App.InvalidateAll();
    }

    void SyncProps()
    {
        var l = App.Active;
        opacity.Enabled = blend.Enabled = l != null;
        if (!editingOpacity) opacity.Sync();
        blend.Sync();
        foreach (var (b, on) in flagBtns) { b.Enabled = l != null; b.On = l != null && on(l); }
        maskBtn.Text = l?.Mask != null ? (l.EditMask ? "마스크 편집 중" : "마스크") : "+마스크";
        flags.PerformLayout();
        top.PerformLayout();
    }

    internal void Rename(Layer l)
    {
        if (l == null) return;
        string name = Modals.Prompt("이름 바꾸기", l.Name, "레이어 이름");
        if (!string.IsNullOrWhiteSpace(name) && name != l.Name) { App.History.Struct(App.Doc, "이름 바꾸기", () => l.Name = name); App.NotifyLayers(); }
    }
}

/// <summary>레이어 줄 목록 (직접 그림, 굴림)</summary>
sealed class LayerList : Control
{
    readonly LayersPanel owner;
    readonly VScrollBar bar = new() { Dock = DockStyle.Right, Width = 10, Visible = false };
    readonly Dictionary<string, (int ver, Bitmap bmp)> thumbs = new();
    readonly System.Windows.Forms.Timer thumbTimer = new() { Interval = 400 };
    readonly ToolTip tip = new();
    List<(Layer l, int depth)> rows = new();
    int scroll, hotRow = -1;
    string hotPart;
    // 끌기
    Layer dragL; int dragY; bool dragging; int dropRow = -1; string dropPos;

    const int Pad = 4, RowGap = 3, RowH = 56, FolderH = 48, PaperH = 44, EyeW = 34, ThumbW = 64, ThumbH = 48;

    public LayerList(LayersPanel o)
    {
        owner = o;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Web.Body; BackColor = Theme.Panel;
        Controls.Add(bar); bar.DarkScroll();
        bar.Scroll += (_, _) => { scroll = bar.Value; Invalidate(); };
        thumbTimer.Tick += (_, _) => { thumbTimer.Stop(); Invalidate(); };
    }

    public void ClearThumbs() { foreach (var t in thumbs.Values) t.bmp.Dispose(); thumbs.Clear(); }
    public void PixelsMaybeChanged() { if (!thumbTimer.Enabled && !IsDisposed && Visible) thumbTimer.Start(); }

    public void Rebuild()
    {
        var d = App.Doc;
        rows = new();
        if (d != null)
        {
            void Walk(Layer folder, int depth)
            {
                var kids = d.ChildrenOf(folder);
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    rows.Add((kids[i], depth));
                    if (kids[i].IsFolder && kids[i].Expanded) Walk(kids[i], depth + 1);
                }
            }
            Walk(null, 0);
        }
        UpdateBar();
        Invalidate();
    }

    int HeightOf(Layer l) => l.IsFolder || l.IsAdjust ? FolderH : RowH;
    int ContentH => Pad * 2 + rows.Sum(r => HeightOf(r.l) + RowGap) + (App.Doc != null ? PaperH : 0);
    void UpdateBar()
    {
        int max = Math.Max(0, ContentH - Height);
        bar.Visible = max > 0;
        bar.Maximum = Math.Max(0, ContentH); bar.LargeChange = Math.Max(1, Height); bar.SmallChange = 30;
        scroll = Math.Clamp(scroll, 0, max);
        if (bar.Visible) bar.Value = Math.Min(scroll, Math.Max(0, bar.Maximum - bar.LargeChange + 1));
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (bar != null) UpdateBar(); }

    /// <summary>줄 위치들 (화면 좌표)</summary>
    IEnumerable<(int i, Rectangle r)> RowRects()
    {
        int y = Pad - scroll, w = Width - Pad * 2 - (bar.Visible ? bar.Width : 0);
        for (int i = 0; i < rows.Count; i++)
        {
            var (l, depth) = rows[i];
            int h = HeightOf(l);
            int indent = depth * 14 + (l.Clip ? 16 : 0);
            yield return (i, new Rectangle(Pad + indent, y, w - indent, h));
            y += h + RowGap;
        }
    }
    Rectangle PaperRect() { int y = Pad - scroll + rows.Sum(r => HeightOf(r.l) + RowGap); return new Rectangle(Pad, y, Width - Pad * 2 - (bar.Visible ? bar.Width : 0), PaperH); }

    // 줄 안의 자리
    static Rectangle EyeRect(Rectangle r) => new(r.X + 4, r.Y + (r.Height - EyeW) / 2, EyeW, EyeW);
    static Rectangle ThumbRect(Rectangle r) => new(r.X + 4 + EyeW + 6, r.Y + (r.Height - ThumbH) / 2, ThumbW, ThumbH);
    static Rectangle FolderRect(Rectangle r) => new(r.X + 4 + EyeW + 6, r.Y + (r.Height - 40) / 2, 64, 40);
    static Rectangle GripRect(Rectangle r) => new(r.Right - 4 - 24, r.Y, 24, r.Height);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        var d = App.Doc;
        if (d == null) return;
        foreach (var (i, r) in RowRects())
        {
            if (r.Bottom < 0 || r.Top > Height) continue;
            DrawRow(g, rows[i].l, r, i);
        }
        DrawPaper(g, PaperRect());
        if (dragging && dropRow >= 0 && dropRow < rows.Count)
        {
            var r = RowRects().First(x => x.i == dropRow).r;
            using var b = new SolidBrush(Theme.Accent);
            if (dropPos == "inside") Web.Box(g, r, 8, null, Theme.Accent, 2);
            else g.FillRectangle(b, r.X, dropPos == "above" ? r.Y - 3 : r.Bottom, r.Width, 3);
        }
    }

    void DrawRow(Graphics g, Layer l, Rectangle r, int index)
    {
        var d = App.Doc;
        bool on = l.Id == d.ActiveId, multi = LayersPanel.Multi.Contains(l.Id) && LayersPanel.Multi.Count > 0;
        Color bg = l.IsFolder ? Web.FolderRow : Theme.Panel2;
        if (on) bg = Web.ActiveRow;
        if (multi) bg = on ? Web.MultiRowOn : Web.MultiRow;
        Web.Box(g, r, 8, bg, on ? Theme.Accent : null);
        if (multi)
        {
            var st0 = g.Save();
            using (var clip = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), 8)) g.SetClip(clip);
            using (var bb = new SolidBrush(Theme.Blue)) g.FillRectangle(bb, r.X, r.Y, 4, r.Height);
            g.Restore(st0);
        }
        if (l.Clip) Web.Text(g, "↳", Font, new Rectangle(r.X - 14, r.Y, 14, r.Height), Theme.Muted, Web.Center);
        float alpha = l.Visible ? 1f : 0.55f;
        Color A(Color c) => alpha < 1 ? Color.FromArgb((int)(c.A * alpha), c) : c;
        // 눈
        var er = EyeRect(r);
        if (hotRow == index && hotPart == "eye") Web.Box(g, er, 8, Theme.Panel3);
        Icons.Draw(g, l.Visible ? "eye" : "eyeOff", new RectangleF(er.X + 8, er.Y + 8, 18, 18), A(Theme.Text));
        int x;
        if (l.IsFolder || l.IsAdjust)
        {
            var fr = FolderRect(r);
            if (l.IsFolder)
            {
                Web.Text(g, l.Expanded ? "▾" : "▸", Web.Small, new Rectangle(fr.X + 2, fr.Y, 12, fr.Height), A(Theme.Muted), Web.Center);
                bool anim = l.Meta?["anim"] != null;
                Icons.Draw(g, anim ? "film" : "folder", new RectangleF(fr.X + 18, fr.Y + 9, 22, 22), A(Theme.Accent));
            }
            else Icons.Draw(g, "adjust", new RectangleF(fr.X + 20, fr.Y + 8, 24, 24), A(Theme.Accent));
            x = fr.Right + 6;
        }
        else
        {
            var tr = ThumbRect(r);
            var st = g.Save();
            using (var path = Theme.Round(new RectangleF(tr.X, tr.Y, tr.Width, tr.Height), 4)) { Web.Smooth(g); g.SetClip(path); }
            Web.Checker(g, tr);
            var bmp = Thumb(l);
            if (bmp != null)
            {
                if (alpha < 1) { using var ia = new System.Drawing.Imaging.ImageAttributes(); ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha }); g.DrawImage(bmp, tr, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia); }
                else g.DrawImageUnscaled(bmp, tr.X, tr.Y);
            }
            if (!string.IsNullOrEmpty(l.Tint)) Web.Box(g, tr, 4, null, WebSwatch.ColorOf(l.Tint), 2);
            g.Restore(st);
            x = tr.Right + 6;
            if (l.Mask != null)
            {
                var mr = new Rectangle(x, tr.Y + 6, 26, tr.Height - 12);
                Web.Box(g, mr, 3, l.MaskOn ? Color.White : Color.Gray, l.EditMask ? Theme.Accent : Theme.Line, l.EditMask ? 2 : 1);
                x = mr.Right + 6;
            }
        }
        // 표시 (badges)
        var badges = new List<(string text, Color bg, Color fg, string icon)>();
        Color bb2 = Web.SegBg;
        if (l.Locked) badges.Add(("", bb2, Theme.Muted, "lock"));
        if (l.AlphaLock) badges.Add(("α", bb2, Theme.Muted, null));
        if (l.Draft) badges.Add(("밑", bb2, Theme.Muted, null));
        if (l.Rough) badges.Add(("러프", WebSwatch.ColorOf(l.Tint ?? "#3d7be0"), Color.White, null));
        if (l.Ref) badges.Add(("참", bb2, Theme.Muted, null));
        if (l.Fx != null) badges.Add(("fx", bb2, Theme.Muted, null));
        if (l.Tone != null) badges.Add(("톤", bb2, Theme.Muted, null));
        if (l.Mask != null) badges.Add((l.MaskOn ? "M" : "m", l.EditMask ? Theme.Accent : bb2, l.EditMask ? Web.OnDark : Theme.Muted, null));
        string kindB = l.Kind switch { "text" => "T", "panel" => "컷", "bubble" => "풍", "vector" => "벡터", _ => null };
        if (kindB != null) badges.Add((kindB, bb2, Theme.Muted, null));
        var gr = GripRect(r);
        int bx = gr.X - 4;
        var bf = Web.Tiny;
        for (int k = badges.Count - 1; k >= 0; k--)
        {
            var (t, c, fc, ic) = badges[k];
            int bw = ic != null ? 16 : Web.Measure(t, bf).Width + 6;
            var br = new Rectangle(bx - bw, r.Y + (r.Height - 16) / 2, bw, 16);
            Web.Box(g, br, 4, A(c));
            if (ic != null) Icons.Draw(g, ic, new RectangleF(br.X + 2, br.Y + 2, 12, 12), A(fc));
            else Web.Text(g, t, bf, br, A(fc), Web.Center);
            bx = br.X - 2;
        }
        // 이름 + 정보
        string sub = l.IsFolder
            ? $"{d.ChildrenOf(l).Count}개{(l.Meta?["anim"] != null ? " 프레임" : "")} · {Math.Round(l.Opacity * 100)}%"
            : $"{Math.Round(l.Opacity * 100)}% {Px.Modes.FirstOrDefault(m => m.id == l.Blend).label}";
        int nw = Math.Max(10, bx - x - 2);
        int nh = Font.Height, sh = Web.Tiny.Height;
        int ny = r.Y + (r.Height - nh - sh) / 2;
        Web.Text(g, l.Name, Font, new Rectangle(x, ny, nw, nh), A(Theme.Text), Web.Left);
        Web.Text(g, sub, Web.Tiny, new Rectangle(x, ny + nh, nw, sh + 1), A(Theme.Muted), Web.Left);
        // 끌기 손잡이
        Icons.Draw(g, "grip", new RectangleF(gr.X + 4, r.Y + (r.Height - 16) / 2f, 16, 16), hotRow == index && hotPart == "grip" ? Theme.Text : Theme.Muted);
    }

    void DrawPaper(Graphics g, Rectangle r)
    {
        var d = App.Doc;
        Web.Box(g, r, 8, Web.PaperRow);
        var er = EyeRect(r);
        if (hotRow == -2 && hotPart == "eye") Web.Box(g, er, 8, Theme.Panel3);
        Icons.Draw(g, d.PaperVisible ? "eye" : "eyeOff", new RectangleF(er.X + 8, er.Y + 8, 18, 18), Theme.Text);
        Web.Text(g, "용지", Font, new Rectangle(er.Right + 6, r.Y, 80, r.Height), Theme.Text, Web.Left);
        var cr = PaperColorRect(r);
        Web.Box(g, cr, 6, null, hotRow == -2 && hotPart == "color" ? Theme.Accent : Theme.Line);
        Web.Box(g, Rectangle.Inflate(cr, -4, -4), 2, WebSwatch.ColorOf(d.PaperColor ?? "#ffffff"));
        var tr = PaperTexRect(r);
        if (hotRow == -2 && hotPart == "tex") Web.Box(g, tr, 8, Theme.Panel3);
        bool tex = d.PaperExtra?["texture"] is System.Text.Json.Nodes.JsonObject t && (string)t["type"] != "none";
        Web.Text(g, tex ? "질감 ✓" : "질감", Web.F(12, true), tr, Theme.Text, Web.Center);
    }
    static Rectangle PaperColorRect(Rectangle r) => new(r.Right - 4 - 42, r.Y + (r.Height - 32) / 2, 42, 32);
    static Rectangle PaperTexRect(Rectangle r) => new(r.Right - 4 - 42 - 6 - 54, r.Y + (r.Height - 36) / 2, 54, 36);

    Bitmap Thumb(Layer l)
    {
        if (l.Px == null) return null;
        if (thumbs.TryGetValue(l.Id, out var t) && t.ver == l.Version) return t.bmp;
        var d = App.Doc;
        double k = Math.Min((double)ThumbW / d.W, (double)ThumbH / d.H);
        int w = Math.Max(1, (int)(d.W * k)), h = Math.Max(1, (int)(d.H * k));
        int ox = (ThumbW - w) / 2, oy = (ThumbH - h) / 2;
        var px = new int[ThumbW * ThumbH];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint c = l.Px.Get(Math.Min(d.W - 1, (int)((x + 0.5) / k)), Math.Min(d.H - 1, (int)((y + 0.5) / k)));
                px[(oy + y) * ThumbW + ox + x] = (int)(Px.Over(c, 0xFFFFFFFF) | 0xFF000000);   // 흰 종이 위
            }
        var bmp = new Bitmap(ThumbW, ThumbH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, ThumbW, ThumbH), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < ThumbH; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * ThumbW, bd.Scan0 + y * bd.Stride, ThumbW);
        bmp.UnlockBits(bd);
        if (thumbs.TryGetValue(l.Id, out var old)) old.bmp.Dispose();
        thumbs[l.Id] = (l.Version, bmp);
        return bmp;
    }

    (int row, string part) HitTest(Point p)
    {
        foreach (var (i, r) in RowRects())
        {
            if (!r.Contains(p)) continue;
            var l = rows[i].l;
            if (EyeRect(r).Contains(p)) return (i, "eye");
            if (GripRect(r).Contains(p)) return (i, "grip");
            if (l.IsFolder && FolderRect(r).Contains(p)) return (i, "folder");
            if (l.IsAdjust && FolderRect(r).Contains(p)) return (i, "adjust");
            if (l.Mask != null && !l.IsFolder)
            {
                var tr = ThumbRect(r);
                if (new Rectangle(tr.Right + 6, tr.Y, 26, tr.Height).Contains(p)) return (i, "mask");
            }
            return (i, "row");
        }
        if (App.Doc != null)
        {
            var pr = PaperRect();
            if (pr.Contains(p))
            {
                if (EyeRect(pr).Contains(p)) return (-2, "eye");
                if (PaperColorRect(pr).Contains(p)) return (-2, "color");
                if (PaperTexRect(pr).Contains(p)) return (-2, "tex");
                return (-2, "row");
            }
        }
        return (-1, null);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragL != null && e.Button == MouseButtons.Left)
        {
            if (!dragging && Math.Abs(e.Y - dragY) > 4) dragging = true;
            if (dragging) { DragOver(e.Location); return; }
        }
        var (row, part) = HitTest(e.Location);
        if (row != hotRow || part != hotPart)
        {
            hotRow = row; hotPart = part; Invalidate();
            Cursor = part == "grip" ? Cursors.SizeNS : part is "eye" or "folder" or "color" or "tex" or "mask" ? Cursors.Hand : Cursors.Default;
            tip.SetToolTip(this, part switch { "eye" => "보이기/숨기기", "grip" => "끌어서 순서 변경 / 폴더에 넣기", "folder" => "펼치기/접기", "tex" => "용지 질감 (종이 결)", "color" => "용지 색", "mask" => "마스크 편집 전환", _ => "" });
        }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotRow = -1; hotPart = null; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var d = App.Doc; if (d == null) return;
        var (row, part) = HitTest(e.Location);
        if (row == -2)
        {
            if (e.Button != MouseButtons.Left) return;
            if (part == "eye") { d.PaperVisible = !d.PaperVisible; d.Modified = true; App.InvalidateAll(); Invalidate(); }
            else if (part == "tex") Web.Run("paper.texture");
            else if (part == "color")
            {
                using var dlg = new ColorDialog { Color = WebSwatch.ColorOf(d.PaperColor ?? "#ffffff"), FullOpen = true };
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) { d.PaperColor = $"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}"; d.Modified = true; App.InvalidateAll(); Invalidate(); }
            }
            return;
        }
        if (row < 0) return;
        var l = rows[row].l;
        if (e.Button == MouseButtons.Right)
        {
            if (d.ActiveId != l.Id) { d.ActiveId = l.Id; App.NotifyLayers(); }
            ShowMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        switch (part)
        {
            case "eye":
                App.History.Struct(d, l.Visible ? "숨기기" : "보이기", () => l.Visible = !l.Visible);
                App.InvalidateAll(); App.NotifyLayers(); return;
            case "folder":
                l.Expanded = !l.Expanded; Rebuild(); return;
            case "adjust":
                d.ActiveId = l.Id; App.NotifyLayers(); Web.Run("layer.editAdjust"); return;
            case "mask":
                d.ActiveId = l.Id; l.EditMask = !l.EditMask; App.NotifyLayers(); return;
            case "grip":
                dragL = l; dragY = e.Y; dragging = false; Capture = true; return;
        }
        // 여러 개 고르기: Ctrl/Shift+클릭 또는 "여러 개" 단추
        if (ModifierKeys.HasFlag(Keys.Shift) || ModifierKeys.HasFlag(Keys.Control) || LayersPanel.MultiMode)
        {
            var set = LayersPanel.Multi;
            if (set.Count == 0 && d.Active != null) set.Add(d.Active.Id);
            if (ModifierKeys.HasFlag(Keys.Shift) && d.Active != null)
            {
                int a = rows.FindIndex(x => x.l.Id == d.ActiveId);
                for (int i = Math.Min(a, row); i <= Math.Max(a, row); i++) if (i >= 0) set.Add(rows[i].l.Id);
            }
            else if (set.Contains(l.Id) && set.Count > 1) set.Remove(l.Id);
            else set.Add(l.Id);
            if (set.Contains(l.Id)) d.ActiveId = l.Id;
            App.NotifyLayers();
            return;
        }
        if (LayersPanel.Multi.Count > 0) LayersPanel.Multi.Clear();
        dragL = l; dragY = e.Y; dragging = false;
        if (d.ActiveId != l.Id) { d.ActiveId = l.Id; App.NotifyLayers(); }
        else { App.NotifyLayers(); }
    }

    void DragOver(Point p)
    {
        dropRow = -1; dropPos = null;
        foreach (var (i, r) in RowRects())
        {
            if (p.Y < r.Top - RowGap || p.Y >= r.Bottom) continue;
            double t = (p.Y - r.Top) / (double)r.Height;
            bool folder = rows[i].l.IsFolder;
            dropRow = i; dropPos = folder && t > 0.28 && t < 0.72 ? "inside" : t < 0.5 ? "above" : "below";
        }
        // 끝에서 저절로 굴리기
        if (p.Y < 24) { scroll = Math.Max(0, scroll - 12); UpdateBar(); }
        if (p.Y > Height - 24) { scroll += 12; UpdateBar(); }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var l = dragL; bool was = dragging;
        dragL = null; dragging = false; Capture = false;
        if (!was || l == null) { Invalidate(); return; }
        int row = dropRow; string pos = dropPos;
        dropRow = -1; dropPos = null;
        Invalidate();
        if (row < 0 || row >= rows.Count) return;
        var target = rows[row].l;
        if (target == l) return;
        MoveRelative(l, target, pos);
    }

    /// <summary>웹 ops.moveRelative: 대상 위/아래/폴더 안으로</summary>
    static void MoveRelative(Layer src, Layer dst, string pos)
    {
        var d = App.Doc;
        if (src.IsFolder && (dst == src || d.IsInside(dst, src))) return;
        App.History.Struct(d, "레이어 순서", () =>
        {
            var moving = new List<Layer>();
            if (src.IsFolder) moving.AddRange(d.Layers.Where(x => d.IsInside(x, src)));
            moving.Add(src);
            foreach (var m in moving) d.Layers.Remove(m);
            int di = d.Layers.IndexOf(dst);
            if (pos == "inside")
            {
                src.Parent = dst.Id; dst.Expanded = true;
                d.Layers.InsertRange(di, moving);         // 폴더 안 맨 위 = 폴더 바로 아래 (배열에서 폴더 앞)
            }
            else
            {
                src.Parent = dst.Parent;
                int at;
                if (pos == "above") at = di + 1;            // 화면에서 위 = 배열에서 뒤
                else
                {
                    // 아래: 대상이 폴더면 그 안의 것들보다 앞
                    int first = di;
                    if (dst.IsFolder) for (int i = 0; i < d.Layers.Count; i++) if (d.IsInside(d.Layers[i], dst)) { first = Math.Min(first, i); }
                    at = first;
                }
                d.Layers.InsertRange(Math.Clamp(at, 0, d.Layers.Count), moving);
            }
        });
        App.NotifyLayers(); App.InvalidateAll();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var (row, part) = HitTest(e.Location);
        if (row < 0 || part is "eye" or "grip" or "folder" || e.Button != MouseButtons.Left) return;
        var l = rows[row].l;
        if (l.IsAdjust) { Web.Run("layer.editAdjust"); return; }
        owner.Rename(l);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, ContentH - Height);
        scroll = Math.Clamp(scroll - e.Delta / 120 * 40, 0, max);
        UpdateBar(); Invalidate();
    }

    void ShowMenu(Point at)
    {
        var m = WebMenu.Style(new ContextMenuStrip());
        foreach (var item in Registry.Menus.Where(x => x.Path.StartsWith("레이어/")).OrderBy(x => x.Order))
        {
            var parts = item.Path.Split('/');
            ToolStripItemCollection items = m.Items;
            for (int i = 1; i < parts.Length - 1; i++)
            {
                var sub = items.OfType<ToolStripMenuItem>().FirstOrDefault(x => x.Text == parts[i]);
                if (sub == null) { sub = new ToolStripMenuItem(parts[i]); items.Add(sub); WebMenu.Style(sub.DropDown); }
                items = sub.DropDownItems;
            }
            string leaf = parts[^1];
            if (leaf.StartsWith("-") || leaf.Length == 0) { if (items.Count > 0 && items[^1] is not ToolStripSeparator) items.Add(new ToolStripSeparator()); continue; }
            var def = item;
            var mi = new ToolStripMenuItem(leaf) { Enabled = def.Enabled?.Invoke() ?? true, Checked = def.Checked?.Invoke() ?? false };
            mi.Click += (_, _) => MainForm.Run(def.Run, def.Path);
            items.Add(mi);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("이름 바꾸기…", null, (_, _) => owner.Rename(App.Active)));
        m.Show(this, at);
    }

    protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(k);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var d = App.Doc; if (d == null || rows.Count == 0) return;
        int i = rows.FindIndex(x => x.l.Id == d.ActiveId);
        if (e.KeyCode == Keys.Up && i > 0) { d.ActiveId = rows[i - 1].l.Id; App.NotifyLayers(); e.Handled = true; }
        else if (e.KeyCode == Keys.Down && i < rows.Count - 1) { d.ActiveId = rows[i + 1].l.Id; App.NotifyLayers(); e.Handled = true; }
        else if (e.KeyCode == Keys.F2) { owner.Rename(App.Active); e.Handled = true; }
    }

    protected override void Dispose(bool disposing) { if (disposing) { ClearThumbs(); thumbTimer.Dispose(); tip.Dispose(); } base.Dispose(disposing); }
}
