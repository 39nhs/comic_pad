// 원형 메뉴 (웹 ui/radial.js): 펜 측면 버튼(설정 "원형 메뉴") 또는 마우스 오른쪽 버튼을 누르면 펜 주위에 자주 쓰는 도구가 원형으로.
// 누른 채 원하는 방향으로 밀고 떼면 선택, 짧게 눌렀다 떼면 열어 둔 채 탭으로 고르기. settings.radialItems = ['tool:brush', 'cmd:edit.undo', …]
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class Radial
{
    public static readonly string[] DefaultItems = { "tool:brush", "tool:eraser", "tool:eyedropper", "tool:fill", "tool:select", "tool:transform", "cmd:edit.undo", "cmd:edit.redo" };
    const int R = 92;

    public static List<string> Items() => App.Settings["radialItems"] is JsonArray a && a.Count > 0 ? a.Select(x => (string)x).Where(x => x != null).ToList() : DefaultItems.ToList();

    public sealed record Item(string Label, string Icon, Action Run, bool On);

    public static Item Describe(string id)
    {
        int i = id.IndexOf(':'); if (i < 0) return null;
        string kind = id[..i], key = id[(i + 1)..];
        if (kind == "tool")
        {
            var t = Registry.Tools.Find(x => x.Id == KeyMap.NativeTool(key));
            return t == null ? null : new Item(t.Name, t.Icon, () => App.SetTool(t.Id), App.Tool == t);
        }
        string path = WebIds.Resolve(key);
        if (path == null) return null;
        string icon = key switch { "edit.undo" => "↶", "edit.redo" => "↷", "view.fit" => "⛶", "view.flip" => "⇋", "layer.new" => "＋", "select.none" => "⬚", "color.swap" => "⇄", "view.luma" => "◐", "edit.transform" => "⤧", "file.save" => "💾", _ => "•" };
        return new Item(WebIds.Label(path), icon, () => WebIds.Run(key), false);
    }

    static RadialForm open;
    public static bool IsOpen => open != null && !open.IsDisposed;

    /// <summary>화면 좌표 가운데에 연다. 돌려주는 것: 펜/마우스를 움직이고 뗄 때 부를 것</summary>
    public static RadialForm Open(Point screen)
    {
        Close();
        var list = Items().Select(Describe).Where(x => x != null).ToList();
        if (list.Count == 0) return null;
        open = new RadialForm(list, screen);
        open.Show(App.Main);
        return open;
    }
    public static void Close() { if (open != null && !open.IsDisposed) open.Close(); open = null; }

    public static void EditDialog()
    {
        var opts = new List<(string, string)> { ("", "(비움)") };
        opts.AddRange(Registry.Tools.Select(t => ($"tool:{KeyMap.WebTool(t.Id)}", $"도구: {t.Name}")));
        foreach (var k in new[] { "edit.undo", "edit.redo", "view.fit", "view.flip", "layer.new", "select.none", "color.swap", "view.luma", "edit.transform", "layer.duplicate", "view.toggleUI", "file.save" })
            if (WebIds.Resolve(k) is string p) opts.Add(($"cmd:{k}", $"명령: {WebIds.Label(p)}"));
        var cur = Items();
        string[] dirs = { "위", "오른쪽 위", "오른쪽", "오른쪽 아래", "아래", "왼쪽 아래", "왼쪽", "왼쪽 위" };
        var fields = Enumerable.Range(0, 8).Select(i => Field.Select("s" + i, dirs[i], i < cur.Count ? cur[i] : "", opts.ToArray())).ToList();
        fields.Add(Field.Info("설정 > 펜 측면 버튼 누르고 그리기를 \"원형 메뉴\"로 하면 펜 측면 버튼으로 열립니다. 마우스는 오른쪽 버튼."));
        var v = Dlg.Form("원형 메뉴 편집", fields, "저장");
        if (v == null) return;
        App.Settings["radialItems"] = new JsonArray(Enumerable.Range(0, 8).Select(i => (string)v["s" + i]).Where(s => !string.IsNullOrEmpty(s)).Select(s => (JsonNode)s).ToArray());
        App.SaveSettingsSoon();
    }

    /// <summary>가운데에서 (dx,dy) 만큼 떨어진 곳이 몇 번째 항목인지 (34px 안쪽이면 -1) — 웹 판과 같은 계산</summary>
    public static int HotIndex(double dx, double dy, int n)
    {
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d <= 34 || n <= 0) return -1;
        double a = (Math.Atan2(dy, dx) + Math.PI / 2 + Math.PI * 2) % (Math.PI * 2);
        return (int)Math.Round(a / (Math.PI * 2) * n) % n;
    }

    public sealed class RadialForm : Form, CanvasView.IPointerGesture
    {
        readonly List<Item> items; readonly Point center; int hot = -1; bool moved;
        const int Pad = 56;
        public RadialForm(List<Item> items, Point screenCenter)
        {
            this.items = items; center = screenCenter;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
            BackColor = Color.Magenta; TransparencyKey = Color.Magenta; DoubleBuffered = true; KeyPreview = true;
            int s = (R + Pad) * 2;
            Bounds = new Rectangle(center.X - s / 2, center.Y - s / 2, s, s);
            Font = new Font("Malgun Gothic", 8.5f);
            Deactivate += (_, _) => BeginInvoke((Action)(() => { if (!IsDisposed) Close(); }));
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }
        protected override bool ShowWithoutActivation => true;
        PointF Pos(int i) { double a = -Math.PI / 2 + i / (double)items.Count * Math.PI * 2; return new PointF(Width / 2f + (float)(Math.Cos(a) * R), Height / 2f + (float)(Math.Sin(a) * R)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var ring = new Pen(Color.FromArgb(90, 90, 100), 30);
            g.DrawEllipse(ring, Width / 2f - R, Height / 2f - R, R * 2, R * 2);
            using var cb = new SolidBrush(Opt.Bg2);
            g.FillEllipse(cb, Width / 2f - 18, Height / 2f - 18, 36, 36);
            TextRenderer.DrawText(g, "×", Font, new Rectangle(Width / 2 - 18, Height / 2 - 18, 36, 36), Opt.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using var iconFont = new Font("Segoe UI Emoji", 14f);
            for (int i = 0; i < items.Count; i++)
            {
                var p = Pos(i); var it = items[i];
                var rc = new RectangleF(p.X - 27, p.Y - 27, 54, 54);
                using var b = new SolidBrush(i == hot ? Opt.Accent : it.On ? Color.FromArgb(120, 90, 40) : Opt.Bg);
                g.FillEllipse(b, rc);
                using var bp = new Pen(Color.FromArgb(120, 120, 130)); g.DrawEllipse(bp, rc);
                var fg = i == hot ? Color.Black : Opt.Fg;
                TextRenderer.DrawText(g, it.Icon, iconFont, new Rectangle((int)rc.X, (int)rc.Y + 2, 54, 30), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                string label = it.Label.Length > 7 ? it.Label[..7] : it.Label;
                TextRenderer.DrawText(g, label, Font, new Rectangle((int)rc.X - 10, (int)rc.Y + 30, 74, 18), fg, TextFormatFlags.HorizontalCenter);
            }
        }

        int IndexAt(Point client) => HotIndex(client.X - Width / 2.0, client.Y - Height / 2.0, items.Count);

        protected override void OnMouseMove(MouseEventArgs e) { int h = IndexAt(e.Location); if (h != hot) { hot = h; Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            double d = Math.Sqrt(Math.Pow(e.X - Width / 2.0, 2) + Math.Pow(e.Y - Height / 2.0, 2));
            if (i >= 0 && d < R + 34) Pick(i); else Close();
        }

        void Pick(int i)
        {
            var it = i >= 0 && i < items.Count ? items[i] : null;
            Close();
            if (it != null) MainForm.Run(it.Run, it.Label);
        }

        // 그림판에서 넘겨받는 펜·마우스 (누른 채 밀기)
        public new void Move(PointerArgs a)
        {
            var c = Shell2Ui.Canvas; if (c == null || IsDisposed) return;
            var sp = c.PointToScreen(new Point((int)a.SX, (int)a.SY));
            double dx = sp.X - center.X, dy = sp.Y - center.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > 12) moved = true;
            int h = HotIndex(dx, dy, items.Count);
            if (h != hot) { hot = h; Invalidate(); }
        }
        public void Up(PointerArgs a)
        {
            if (IsDisposed) return;
            if (a != null) Move(a);
            if (moved && hot >= 0) Pick(hot);   // 밀고 뗐으면 바로 선택, 아니면 열어 둔 채 탭 기다림
            else Activate();
        }
    }
}
