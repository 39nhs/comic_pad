// 아래 큰 버튼 막대 (웹 #bottombar, 높이 62): 색 동그라미 · 아이콘+이름 단추 · 크기/농도 채움 막대 · (빈칸) · 편집.
//  - 단추 목록은 settings.bottombar.items (웹 판과 같은 퀵 동작 id), 보이기는 settings.bottombar.visible
//  - 켜진 단추 = 주황 바탕, 실행 취소/다시 실행은 할 것이 없으면 흐리게
//  - 막대: 마우스·펜은 누른 곳으로 바로, 끌면 따라감 (웹 slider 와 같음). 크기는 로그 눈금
//  - 넘치면 마우스 휠로 옆으로 밀기 (웹 overflow-x)
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.UI.Chrome;

public sealed class BottomBar : PaintedBar
{
    /// <summary>퀵 동작 id → 웹 아이콘 이름</summary>
    public static readonly Dictionary<string, string> QuickIcon = new()
    {
        ["rulerSnap"] = "ruler", ["maskEdit"] = "mask", ["reference"] = "refimg", ["smudge"] = "smudge", ["lassofill"] = "lassofill", ["luma"] = "eye", ["radial"] = "dots", ["webtoonView"] = "phone",
        ["extendDown"] = "down", ["animPrev"] = "prev", ["animNext"] = "next", ["animPlay"] = "play", ["palette"] = "search", ["3d"] = "cube", ["undo"] = "undo", ["redo"] = "redo",
        ["penEraser"] = "eraser", ["eyedropper"] = "eyedropper", ["line"] = "straight", ["snap"] = "line", ["fit"] = "fit", ["flip"] = "flip", ["rotL"] = "rotateL", ["rotR"] = "rotate",
        ["deselect"] = "deselect", ["transform"] = "transform", ["finger"] = "finger", ["newLayer"] = "plus", ["rough"] = "rough", ["save"] = "save", ["zen"] = "side", ["sym"] = "sym",
        ["swap"] = "swap", ["brushTool"] = "pen", ["hand"] = "hand", ["panels"] = "side", ["layersTab"] = "layers", ["colorTab"] = "palette", ["fill"] = "fill", ["selectTool"] = "select",
        ["lassoFillQ"] = "lassofill", ["connect"] = "phone", ["tone"] = "tone",
    };

    static Font small, bold;
    static Font Small => small ??= new Font(Theme.FontName, 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    static Font Bold => bold ??= new Font(Theme.FontName, Theme.FontPx, FontStyle.Bold, GraphicsUnit.Pixel);
    int scroll;

    sealed record Slot(string Kind, QuickAction A);   // Kind: chip · btn · size · opacity · edit

    public BottomBar()
    {
        Dock = DockStyle.Bottom; Height = 62;
        App.ToolChanged += Invalidate; App.ColorChanged += Invalidate; App.History.Changed += Invalidate; App.SettingsChanged += Invalidate; App.DocChanged += Invalidate;
        Ready();
    }

    public void Rebuild() { Visible = Bars.BottomVisible && !(App.Main as MainForm)?.Zen == true; Relayout(); }

    protected override Cursor DragCursor => Cursors.SizeWE;

    protected override void LayoutHits()
    {
        int x = 8 - scroll, cy = Height / 2;
        // 색 동그라미 (웹 .bb-chip 44, 오른쪽 4px 더)
        Hits.Add(new Hit { R = new Rectangle(x, cy - 22, 44, 44), Data = new Slot("chip", null), Tip = "색 (탭: 색 패널)", Click = () => QuickActions.ShowTab("color") });
        x += 44 + 4 + 4;
        foreach (var id in Bars.BottomItems())
        {
            var a = QuickActions.Get(id); if (a == null) continue;
            if (a.Type is "size" or "opacity")
            {
                bool sz = a.Type == "size";
                int w = sz ? 190 : 140;
                var r = new Rectangle(x, cy - 22, w, 44);
                Hits.Add(new Hit { R = r, Data = new Slot(a.Type, a), Drag = (p, end) => DragSlider(r, sz, p, end) });
                x += w + 4;
                continue;
            }
            int bw = Math.Max(60, TextW(a.Label, Small) + 12);
            var act = a;
            Hits.Add(new Hit
            {
                R = new Rectangle(x, cy - 27, bw, 54), Data = new Slot("btn", a), Tip = a.Label, Click = () => act.Run(),
                Enabled = () => act.Id switch { "undo" => App.History.CanUndo, "redo" => App.History.CanRedo, _ => true },
            });
            x += bw + 4;
        }
        // 편집 (맨 오른쪽, 사이는 빈칸)
        int ew = Math.Max(48, TextW("편집", Small) + 12);
        int ex = Math.Max(x, Width - 8 - ew);
        Hits.Add(new Hit { R = new Rectangle(ex, cy - 27, ew, 54), Data = new Slot("edit", null), Tip = "버튼 고르기", Click = Bars.EditBottom });
        contentW = ex + ew + 8 + scroll;
    }
    int contentW;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, contentW - Width);
        int ns = Math.Clamp(scroll - e.Delta / 2, 0, max);
        if (ns != scroll) { scroll = ns; Relayout(); }
    }

    void DragSlider(Rectangle r, bool sz, Point p, bool end)
    {
        var (pr, _) = Shell2Ui.ActiveBrush(); if (pr == null) return;
        if (end) { PresetStore.Save(); App.SaveSettingsSoon(); return; }
        double t = Math.Clamp((p.X - r.X) / (double)r.Width, 0, 1);
        if (sz) { double v = 0.5 * Math.Pow(600 / 0.5, t); pr.Size = Math.Clamp(Math.Round(v / 0.5) * 0.5, 0.5, 600); }
        else { double v = 0.01 + (1 - 0.01) * t; pr.Opacity = Math.Clamp(Math.Round(v / 0.01) * 0.01, 0.01, 1); }
        App.NotifySettings();
    }

    protected override void PaintBar(Graphics g)
    {
        g.Clear(Theme.Panel);
        Line(g, Theme.Border, 0, 0, Width, 1);
        var (pr, _) = Shell2Ui.ActiveBrush();
        foreach (var h in Hits)
        {
            var s = (Slot)h.Data;
            switch (s.Kind)
            {
                case "chip":
                {
                    // 3px #ddd 테두리 + 바깥 1px 검정 동그라미
                    var r = h.R;
                    using (var b = new SolidBrush(Color.Black)) g.FillEllipse(b, r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2);
                    using (var b = new SolidBrush(ColorTranslator.FromHtml("#dddddd"))) g.FillEllipse(b, r);
                    using (var b = new SolidBrush(Color.FromArgb((int)(App.Fg | 0xFF000000)))) g.FillEllipse(b, Rectangle.Inflate(r, -3, -3));
                    break;
                }
                case "size":
                case "opacity":
                {
                    bool sz = s.Kind == "size";
                    double v = pr == null ? (sz ? 1 : 1) : sz ? pr.Size : pr.Opacity;
                    double t = sz ? Math.Log(Math.Max(0.5, v) / 0.5) / Math.Log(600 / 0.5) : (v - 0.01) / 0.99;
                    t = Math.Clamp(t, 0, 1);
                    using (var path = Theme.Round(h.R, 7))
                    {
                        using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, path);
                        var st = g.Save();
                        g.SetClip(path);
                        int fw = (int)Math.Round(h.R.Width * t);
                        if (fw > 0)
                        {
                            var fr = new Rectangle(h.R.X, h.R.Y, fw, h.R.Height);
                            using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(h.R.X - 1, h.R.Y, Math.Max(2, fw + 1), h.R.Height),
                                Theme.Blend(Theme.Panel2, Theme.Accent, 0.35), Theme.Blend(Theme.Panel2, Theme.Accent, 0.55), 0f);
                            g.FillRectangle(lg, fr);
                        }
                        g.Restore(st);
                    }
                    string val = sz ? (v < 10 ? v.ToString("0.0") : Math.Round(v).ToString()) : $"{Math.Round(v * 100)}%";
                    DrawText(g, sz ? "크기" : "농도", Theme.Font, new Rectangle(h.R.X + 10, h.R.Y, h.R.Width / 2, h.R.Height), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    DrawText(g, val, Bold, new Rectangle(h.R.X, h.R.Y, h.R.Width - 10, h.R.Height), Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                    break;
                }
                case "btn":
                case "edit":
                {
                    bool edit = s.Kind == "edit";
                    bool on = !edit && s.A.Active?.Invoke() == true, dis = !h.IsEnabled;
                    if (!edit) Fill(g, h.R, on ? Theme.Accent : h == Down ? Theme.Panel3 : Theme.Panel2, 12);
                    Color c = edit ? Theme.Muted : on ? Theme.OnAccent : Theme.Text;
                    if (dis) c = Theme.Blend(Theme.Panel2, c, 0.35);
                    int isz = edit ? 20 : 24;
                    int lh = 15, total = isz + 2 + lh, y0 = h.R.Y + (h.R.Height - total) / 2;
                    string icon = edit ? "gear" : QuickIcon.TryGetValue(s.A.Id, out var ic) ? ic : "dots";
                    Icons.Draw(g, icon, new RectangleF(h.R.X + (h.R.Width - isz) / 2f, y0, isz, isz), c);
                    DrawText(g, edit ? "편집" : s.A.Label, Small, new Rectangle(h.R.X, y0 + isz + 2, h.R.Width, lh), c);
                    break;
                }
            }
        }
    }
}
