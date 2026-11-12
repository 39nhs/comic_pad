// 레이어 패널: 위가 맨 위 레이어. 눈(보이기)·썸네일·이름·폴더 들여쓰기, 끌어서 순서·폴더에 넣기, 두 번 눌러 이름 바꾸기.
// 위쪽: 합성 모드·불투명도·클리핑·투명 픽셀 잠금·잠금·마스크, 아래 버튼: 새 레이어·폴더·복제·삭제·아래로 병합.
using ComicPadNative.Core;

namespace ComicPadNative.UI;

public sealed class LayersPanel : UserControl
{
    readonly ListBox list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46, BackColor = Opt.Bg, ForeColor = Opt.Fg, BorderStyle = BorderStyle.None, IntegralHeight = false };
    readonly ComboBox blend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, BackColor = Opt.Bg2, ForeColor = Opt.Fg, FlatStyle = FlatStyle.Flat };
    readonly TrackBar opacity = new() { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, Width = 120, AutoSize = false, Height = 24 };
    readonly Label opLabel = new() { ForeColor = Opt.Muted, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly CheckBox clip = Chk("클리핑"), alock = Chk("투명 잠금"), lck = Chk("잠금"), maskEdit = Chk("마스크 편집");
    readonly Dictionary<string, (int ver, Bitmap bmp)> thumbs = new();
    List<Layer> rows = new();
    bool syncing;
    int dragFrom = -1;

    static CheckBox Chk(string t) => new() { Text = t, ForeColor = Opt.Fg, AutoSize = true, Margin = new Padding(0, 2, 6, 0) };

    public LayersPanel()
    {
        BackColor = Opt.Bg;
        foreach (var (_, label) in Px.Modes) blend.Items.Add(label);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Opt.Bg, Padding = new Padding(4) };
        top.Controls.Add(blend); top.Controls.Add(opacity); top.Controls.Add(opLabel);
        top.Controls.Add(clip); top.Controls.Add(alock); top.Controls.Add(lck); top.Controls.Add(maskEdit);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Opt.Bg, Padding = new Padding(4) };
        bottom.Controls.Add(Opt.Button("＋ 레이어", () => LayerCommands.NewLayer()));
        bottom.Controls.Add(Opt.Button("📁 폴더", () => LayerCommands.NewFolder()));
        bottom.Controls.Add(Opt.Button("복제", LayerCommands.Duplicate));
        bottom.Controls.Add(Opt.Button("병합↓", LayerCommands.MergeDown));
        bottom.Controls.Add(Opt.Button("마스크", LayerCommands.AddMask));
        bottom.Controls.Add(Opt.Button("🗑", LayerCommands.Delete));
        Controls.Add(list); Controls.Add(top); Controls.Add(bottom);

        list.DrawItem += DrawRow;
        list.MouseDown += OnMouseDown;
        list.MouseMove += (_, e) => { if (dragFrom >= 0 && e.Button == MouseButtons.Left && Math.Abs(e.Y - dragStartY) > 6) list.DoDragDrop(dragFrom, DragDropEffects.Move); };
        list.AllowDrop = true;
        list.DragOver += (_, e) => e.Effect = DragDropEffects.Move;
        list.DragDrop += OnDrop;
        list.DoubleClick += (_, _) => { if (App.Active?.IsAdjust == true && Registry.Commands.TryGetValue("레이어/조정 레이어 값 수정", out var edit) && edit != null) MainForm.Run(edit, "조정 레이어 값 수정"); else Rename(); };
        blend.SelectedIndexChanged += (_, _) => { if (!syncing) Prop("합성 모드", l => l.Blend = Px.Modes[blend.SelectedIndex].id); };
        opacity.Scroll += (_, _) => { opLabel.Text = opacity.Value + "%"; if (!syncing) { var l = App.Active; if (l != null) { l.Opacity = opacity.Value / 100f; App.InvalidateAll(); } } };
        opacity.MouseUp += (_, _) => { var l = App.Active; if (l != null) { float v = l.Opacity; l.Opacity = opBefore; Prop("불투명도", x => x.Opacity = v); } };
        opacity.MouseDown += (_, _) => opBefore = App.Active?.Opacity ?? 1;
        clip.CheckedChanged += (_, _) => { if (!syncing) Prop("클리핑", l => l.Clip = clip.Checked); };
        alock.CheckedChanged += (_, _) => { if (!syncing) Prop("투명 픽셀 잠금", l => l.AlphaLock = alock.Checked); };
        lck.CheckedChanged += (_, _) => { if (!syncing) Prop("잠금", l => l.Locked = lck.Checked); };
        maskEdit.CheckedChanged += (_, _) => { if (!syncing && App.Active != null) { App.Active.EditMask = maskEdit.Checked && App.Active.Mask != null; App.NotifyLayers(); } };

        App.LayersChanged += Rebuild;
        App.DocChanged += () => { thumbs.Clear(); Rebuild(); };
        App.History.Changed += Rebuild;
    }
    float opBefore;
    int dragStartY;

    static void Prop(string label, Action<Layer> change)
    {
        var l = App.Active;
        if (l == null) return;
        App.History.Struct(App.Doc, label, () => change(l));
        App.NotifyLayers(); App.InvalidateAll();
    }

    /// <summary>화면에 보일 순서 (위 = 맨 위 레이어, 접힌 폴더 안은 숨김)</summary>
    void Rebuild()
    {
        if (InvokeRequired) { BeginInvoke(Rebuild); return; }
        var d = App.Doc;
        rows = new();
        if (d != null)
        {
            void Walk(Layer folder)
            {
                var kids = d.ChildrenOf(folder);
                for (int i = kids.Count - 1; i >= 0; i--)
                {
                    rows.Add(kids[i]);
                    if (kids[i].IsFolder && kids[i].Expanded) Walk(kids[i]);
                }
            }
            Walk(null);
        }
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var l in rows) list.Items.Add(l.Name);
        int ai = rows.FindIndex(l => l.Id == d?.ActiveId);
        if (ai >= 0) list.SelectedIndex = ai;
        list.EndUpdate();
        SyncProps();
    }

    void SyncProps()
    {
        var l = App.Active;
        syncing = true;
        blend.Enabled = opacity.Enabled = l != null;
        if (l != null)
        {
            blend.SelectedIndex = Math.Max(0, Array.FindIndex(Px.Modes, m => m.id == l.Blend));
            opacity.Value = (int)Math.Round(l.Opacity * 100); opLabel.Text = opacity.Value + "%";
            clip.Checked = l.Clip; alock.Checked = l.AlphaLock; lck.Checked = l.Locked;
            maskEdit.Enabled = l.Mask != null; maskEdit.Checked = l.EditMask;
        }
        syncing = false;
    }

    void DrawRow(object s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= rows.Count) return;
        var l = rows[e.Index];
        var d = App.Doc;
        bool sel = l.Id == d.ActiveId;
        var g = e.Graphics;
        using (var bg = new SolidBrush(sel ? Color.FromArgb(80, 232, 163, 61) : Opt.Bg)) g.FillRectangle(bg, e.Bounds);
        int indent = d.DepthOf(l) * 14;
        var r = e.Bounds;
        // 눈
        using var f = new Font("Segoe UI Emoji", 11f);
        TextRenderer.DrawText(g, l.Visible ? "👁" : "—", f, new Rectangle(r.X + 2, r.Y, 26, r.Height), l.Visible ? Opt.Fg : Opt.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        // 클리핑 표시
        int x = r.X + 30 + indent;
        if (l.Clip) { TextRenderer.DrawText(g, "↳", Font, new Rectangle(x - 12, r.Y, 12, r.Height), Opt.Accent, TextFormatFlags.VerticalCenter); }
        // 썸네일
        var th = new Rectangle(x, r.Y + 4, 52, r.Height - 8);
        if (l.IsFolder) TextRenderer.DrawText(g, l.Expanded ? "📂" : "📁", f, th, Opt.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        else if (l.IsAdjust) TextRenderer.DrawText(g, "◐", f, th, Opt.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        else { var bmp = Thumb(l, th.Size); if (bmp != null) g.DrawImage(bmp, th.X, th.Y); using var p = new Pen(Color.FromArgb(80, 80, 90)); g.DrawRectangle(p, th); }
        if (l.Mask != null) { var mr = new Rectangle(th.Right + 3, th.Y + 6, 22, th.Height - 12); using var mb = new SolidBrush(l.MaskOn ? Color.White : Color.Gray); g.FillRectangle(mb, mr); if (l.EditMask) using (var p = new Pen(Opt.Accent, 2)) g.DrawRectangle(p, mr); }
        // 이름 + 정보
        int tx = th.Right + (l.Mask != null ? 30 : 6);
        string info = (l.Blend != "source-over" ? Px.Modes.FirstOrDefault(m => m.id == l.Blend).label + " " : "") + (l.Opacity < 1 ? $"{l.Opacity * 100:0}%" : "") + (l.Locked ? " 🔒" : "") + (l.AlphaLock ? " ▦" : "") + (l.Draft ? " 밑그림" : "") + (l.Rough ? " 러프" : "");
        TextRenderer.DrawText(g, l.Name, Font, new Rectangle(tx, r.Y + 4, r.Right - tx - 4, 18), Opt.Fg, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, info, Font, new Rectangle(tx, r.Y + 22, r.Right - tx - 4, 18), Opt.Muted, TextFormatFlags.EndEllipsis);
    }

    Bitmap Thumb(Layer l, Size sz)
    {
        if (l.Px == null) return null;
        if (thumbs.TryGetValue(l.Id, out var t) && t.ver == l.Version && t.bmp.Width == sz.Width) return t.bmp;
        var d = App.Doc;
        double k = Math.Min((double)sz.Width / d.W, (double)sz.Height / d.H);
        int w = Math.Max(1, (int)(d.W * k)), h = Math.Max(1, (int)(d.H * k));
        var bmp = new Bitmap(sz.Width, sz.Height);
        int ox = (sz.Width - w) / 2, oy = (sz.Height - h) / 2;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint c = l.Px.Get((int)((x + 0.5) / k), (int)((y + 0.5) / k));
                // 체크무늬 위
                uint bg = ((x / 4 + y / 4) % 2 == 0) ? 0xFFFFFFFF : 0xFFDDDDDD;
                uint o = Px.Over(c, bg);
                bmp.SetPixel(ox + x, oy + y, Color.FromArgb((int)(o | 0xFF000000)));
            }
        if (thumbs.TryGetValue(l.Id, out var old)) old.bmp.Dispose();
        thumbs[l.Id] = (l.Version, bmp);
        return bmp;
    }

    void OnMouseDown(object s, MouseEventArgs e)
    {
        int i = list.IndexFromPoint(e.Location);
        if (i < 0 || i >= rows.Count) return;
        var l = rows[i];
        dragFrom = -1;
        if (e.X < 30) { App.History.Struct(App.Doc, l.Visible ? "숨기기" : "보이기", () => l.Visible = !l.Visible); App.InvalidateAll(); list.Invalidate(); return; }
        int indent = App.Doc.DepthOf(l) * 14;
        if (l.IsFolder && e.X >= 30 + indent && e.X < 30 + indent + 52) { l.Expanded = !l.Expanded; Rebuild(); return; }
        if (l.Mask != null && e.X >= 30 + indent + 55 && e.X < 30 + indent + 80) { l.EditMask = !l.EditMask; App.NotifyLayers(); }
        App.Doc.ActiveId = l.Id;
        App.NotifyLayers();
        dragFrom = i; dragStartY = e.Y;
        if (e.Button == MouseButtons.Right) LayerCommands.ContextMenu(list, e.Location);
    }

    void OnDrop(object s, DragEventArgs e)
    {
        if (dragFrom < 0) return;
        var p = list.PointToClient(new Point(e.X, e.Y));
        int to = list.IndexFromPoint(p);
        var src = rows[dragFrom];
        dragFrom = -1;
        if (to < 0) to = rows.Count - 1;
        var dst = rows[to];
        if (dst == src) return;
        var d = App.Doc;
        // 대상이 폴더이고 그 행의 오른쪽 절반에 놓으면 폴더 안으로
        var rr = list.GetItemRectangle(to);
        bool into = dst.IsFolder && p.X > rr.Width / 2 && !(src.IsFolder && (dst == src || d.IsInside(dst, src)));
        if (src.IsFolder && d.IsInside(dst, src)) return;
        App.History.Struct(d, "레이어 순서", () =>
        {
            var moving = new List<Layer> { src };
            if (src.IsFolder) moving.AddRange(d.Layers.Where(l => d.IsInside(l, src)));
            foreach (var m in moving) d.Layers.Remove(m);
            int di = d.Layers.IndexOf(dst);
            if (into) { src.Parent = dst.Id; d.Layers.InsertRange(di, moving.OrderBy(m => m == src ? 1 : 0)); }
            else
            {
                src.Parent = dst.Parent;
                // 화면에서 아래로 끌면 대상 아래(배열에서 앞), 위로 끌면 대상 위(배열에서 뒤)
                bool below = to > rows.IndexOf(src);
                int ins = below ? di : di + 1;
                if (!below && dst.IsFolder) ins = di + 1;
                d.Layers.InsertRange(Math.Clamp(ins, 0, d.Layers.Count), moving.OrderBy(m => m == src ? 1 : 0));
            }
        });
        App.NotifyLayers(); App.InvalidateAll();
    }

    void Rename()
    {
        var l = App.Active;
        if (l == null) return;
        string name = Microsoft.VisualBasic.Interaction.InputBox("레이어 이름", "이름 바꾸기", l.Name);
        if (!string.IsNullOrWhiteSpace(name) && name != l.Name) { App.History.Struct(App.Doc, "이름 바꾸기", () => l.Name = name); App.NotifyLayers(); }
    }
}
