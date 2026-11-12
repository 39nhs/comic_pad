// 떠 있는 퀵바 (웹 #quickbar): 그림판 위 둥근 세로 막대 — 손잡이 · 48px 아이콘 단추 · 세로 채움 막대(크기·불투명도).
// 넘치면 옆 열로 (웹 flex-wrap). 손잡이를 끌면 옮기고, 탭하면 단추 고르기. 설정·단추 목록은 Shell2 Bars (settings.quickbar).
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.UI.Chrome;

public sealed class QuickBar : PaintedBar
{
    static readonly Color Bg = Theme.Blend(Theme.StageBg, Color.FromArgb(40, 40, 46), 0.92), BorderC = ColorTranslator.FromHtml("#55555f");
    static Font val;
    static Font Val => val ??= new Font(Theme.FontName, 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    readonly Action<Point, Point> moved;     // 손잡이로 옮김 (누른 화면 점, 지금 화면 점)
    readonly Action tapped;
    readonly Action dropped;
    int B => Theme.Large ? 56 : 48;

    sealed record Slot(string Kind, QuickAction A);   // grip · btn · size · opacity

    public QuickBar(Action<Point, Point> moved, Action tapped, Action dropped)
    {
        this.moved = moved; this.tapped = tapped; this.dropped = dropped;
        BackColor = Bg;
        App.ToolChanged += Invalidate; App.SettingsChanged += Invalidate; App.History.Changed += Invalidate;
        Disposed += (_, _) => { App.ToolChanged -= Invalidate; App.SettingsChanged -= Invalidate; App.History.Changed -= Invalidate; };
        Ready();
    }

    readonly List<Slot> slots = new();
    public void SetItems(IEnumerable<QuickAction> items)
    {
        slots.Clear();
        slots.Add(new Slot("grip", null));
        foreach (var a in items) slots.Add(new Slot(a.Type is "size" or "opacity" ? a.Type : "btn", a));
        Measure(MaxH);
        Relayout();
    }

    /// <summary>그림판 높이 - 16 (웹 max-height) 를 넘으면 옆 열로</summary>
    public int MaxH = 800;
    int SlotH(Slot s) => s.Kind switch { "grip" => 26, "btn" => B, _ => 2 + 14 + 2 + 96 + 2 };

    public void Measure(int maxH)
    {
        MaxH = maxH;
        int col = 0, y = 4, colH = 0, maxCol = 0;
        foreach (var s in slots)
        {
            int h = SlotH(s);
            if (y > 4 && y + h + 4 > maxH) { col++; y = 4; }
            y += h + 3; colH = Math.Max(colH, y - 3 + 4); maxCol = col;
        }
        var size = new Size(4 + (maxCol + 1) * B + maxCol * 3 + 4 + 2, Math.Max(colH, 40) + 2);
        if (Size != size) Size = size;
        using var p = Theme.Round(new RectangleF(0, 0, Width, Height), 14);
        var old = Region; Region = new Region(p); old?.Dispose();
    }

    protected override Cursor DragCursor => Cursors.SizeNS;

    protected override void LayoutHits()
    {
        int x = 5, y = 5;
        foreach (var s in slots)
        {
            int h = SlotH(s);
            if (y > 5 && y + h + 4 > MaxH) { x += B + 3; y = 5; }
            var r = new Rectangle(x, y, B, h);
            var slot = s;
            switch (s.Kind)
            {
                case "grip":
                    Point start = default; bool movedAny = false;
                    Hits.Add(new Hit { R = r, Data = s, Tip = "끌어서 이동 · 탭: 버튼 편집", Drag = (p, end) =>
                    {
                        var sp = Control.MousePosition;
                        if (Down != null && !end && start == default) { start = sp; movedAny = false; }
                        if (!end) { if (Math.Abs(sp.X - start.X) + Math.Abs(sp.Y - start.Y) > 5) movedAny = true; if (movedAny) moved(start, sp); return; }
                        if (!movedAny) tapped(); else dropped();
                        start = default;
                    } });
                    break;
                case "btn":
                    Hits.Add(new Hit { R = r, Data = s, Tip = s.A.Label, Click = () => slot.A.Run?.Invoke(),
                        Enabled = () => slot.A.Id switch { "undo" => App.History.CanUndo, "redo" => App.History.CanRedo, _ => true } });
                    break;
                default:
                    var track = new Rectangle(r.X + (B - 34) / 2, r.Y + 2 + 14 + 2, 34, 96);
                    Hits.Add(new Hit { R = r, Data = s, Tip = s.Kind == "size" ? "크기" : "불투명도", Drag = (p, end) => DragV(track, s.Kind == "size", p, end) });
                    break;
            }
            y += h + 3;
        }
    }

    void DragV(Rectangle track, bool sz, Point p, bool end)
    {
        var (pr, _) = Shell2Ui.ActiveBrush(); if (pr == null) return;
        if (end) { PresetStore.Save(); App.SaveSettingsSoon(); return; }
        double t = Math.Clamp(1 - (p.Y - track.Y) / (double)track.Height, 0, 1);
        if (sz) pr.Size = Math.Max(0.5, Math.Round(0.5 * Math.Pow(600 / 0.5, t) * 2) / 2);
        else pr.Opacity = Math.Clamp(Math.Round((0.01 + 0.99 * t) * 100) / 100, 0.01, 1);
        App.NotifySettings();
    }

    protected override void PaintBar(Graphics g)
    {
        g.Clear(Bg);
        using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 14)) using (var pen = new Pen(BorderC)) g.DrawPath(pen, p);
        var (pr, _) = Shell2Ui.ActiveBrush();
        foreach (var h in Hits)
        {
            var s = (Slot)h.Data;
            if (s.Kind == "grip") { Icons.Draw(g, "grip", new RectangleF(h.R.X + (h.R.Width - 18) / 2f, h.R.Y + 4, 18, 18), Theme.Muted); continue; }
            if (s.Kind == "btn")
            {
                bool on = s.A.Active?.Invoke() == true, dis = !h.IsEnabled;
                if (on) Fill(g, h.R, Theme.Accent, 10);
                else if (h == Down) Fill(g, h.R, Theme.Panel3, 10);
                var c = on ? Theme.OnAccent : Theme.Text;
                if (dis) c = Theme.Blend(Bg, c, 0.35);
                string ic = BottomBar.QuickIcon.TryGetValue(s.A.Id, out var n) ? n : "dots";
                Icons.Draw(g, ic, new RectangleF(h.R.X + (h.R.Width - 24) / 2f, h.R.Y + (h.R.Height - 24) / 2f, 24, 24), c);
                continue;
            }
            bool sz = s.Kind == "size";
            double v = pr == null ? 1 : sz ? pr.Size : pr.Opacity;
            double t = Math.Clamp(sz ? Math.Log(Math.Max(0.5, v) / 0.5) / Math.Log(600 / 0.5) : (v - 0.01) / 0.99, 0, 1);
            DrawText(g, sz ? (v < 10 ? v.ToString("0.0") : Math.Round(v).ToString()) : $"{Math.Round(v * 100)}%", Val, new Rectangle(h.R.X, h.R.Y + 2, h.R.Width, 14), Theme.Accent);
            var track = new Rectangle(h.R.X + (B - 34) / 2, h.R.Y + 18, 34, 96);
            using var path = Theme.Round(track, 8);
            using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, path);
            int fh = (int)Math.Round(track.Height * t);
            if (fh > 0)
            {
                var st = g.Save(); g.SetClip(path);
                var fr = new Rectangle(track.X, track.Bottom - fh, track.Width, fh);
                using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(fr.X, fr.Y - 1, fr.Width, fr.Height + 2),
                    Theme.Blend(Theme.Panel2, Theme.Accent, 0.3), Theme.Blend(Theme.Panel2, Theme.Accent, 0.65), 90f);
                g.FillRectangle(lg, fr);
                g.Restore(st);
            }
        }
    }
}
