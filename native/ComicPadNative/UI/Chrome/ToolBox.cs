// 왼쪽 도구 막대 (웹 #toolbar): 46px 둥근 도구 단추를 세로로 채우고 넘치면 옆 열로 (웹 grid-auto-flow: column).
//  - 켜진 도구 = 주황 바탕 + 어두운 아이콘, 올리면 --panel3
//  - 간단히 모드: 고른 도구만 (settings.ui.tools, 웹 도구 id), 나머지는 "…" (다른 도구 N개) 팝업
//  - 맨 끝: 전경·배경색 칩 (전경 = 색 패널, 배경 = 색 바꾸기)
using System.Text.Json.Nodes;
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.UI.Chrome;

public sealed class ToolBox : PaintedBar
{
    /// <summary>웹 판 도구 막대 순서 (TOOL_ORDER) 와 등록 순서</summary>
    static readonly string[] WebOrder =
    {
        "brush", "eraser", "smudge", "blurbrush", "fill", "lassofill", "gradient", "eyedropper", "select", "selpen", "transform", "liquify", "clone", "shape", "ruler", "panel", "bubble", "text", "speedlines", "view",
    };
    static readonly string[] WebRegOrder =
    {
        "brush", "eraser", "fill", "eyedropper", "select", "transform", "shape", "panel", "bubble", "text", "view", "gradient", "lassofill", "smudge", "selpen", "ruler", "clone", "blurbrush", "liquify",
        "speedlines", "vselect", "script", "memo", "aiselect", "heal", "pathpen", "meshwarp", "puppet", "refinebrush", "sfx", "motion", "focuslines",
    };
    /// <summary>웹 도구 id → 아이콘 이름</summary>
    public static readonly Dictionary<string, string> WebIcon = new()
    {
        ["brush"] = "pen", ["eraser"] = "eraser", ["fill"] = "fill", ["eyedropper"] = "eyedropper", ["select"] = "select", ["transform"] = "transform", ["shape"] = "shape", ["panel"] = "panel",
        ["bubble"] = "bubble", ["text"] = "text", ["view"] = "hand", ["gradient"] = "gradient", ["lassofill"] = "lassofill", ["smudge"] = "smudge", ["selpen"] = "selpen", ["ruler"] = "ruler",
        ["clone"] = "clone", ["blurbrush"] = "blurbrush", ["liquify"] = "liquify", ["speedlines"] = "speedlines", ["vselect"] = "vsel", ["script"] = "script", ["memo"] = "memo", ["aiselect"] = "aisel",
        ["heal"] = "heal", ["pathpen"] = "pathpen", ["meshwarp"] = "meshwarp", ["puppet"] = "puppet", ["refinebrush"] = "refine", ["sfx"] = "sfx", ["motion"] = "motion", ["focuslines"] = "focus",
    };

    public static string IconOf(ToolBase t)
    {
        string w = KeyMap.WebTool(t.Id);
        if (WebIcon.TryGetValue(w, out var n)) return n;
        if (t.Id == "pixel") return "pencil";   // 네이티브에만 있는 픽셀 연필
        return Icons.Has(t.Id) ? t.Id : null;
    }

    /// <summary>도구의 지금 키 (설정 toolKeys 가 먼저) — 말풍선 도움말·더 보기 팝업용</summary>
    public static Keys KeyOf(ToolBase t)
    {
        if (App.Settings["toolKeys"] is JsonObject tk)
        {
            string w = KeyMap.WebTool(t.Id);
            var node = tk.ContainsKey(w) ? tk[w] : tk.ContainsKey(t.Id) ? tk[t.Id] : null;
            if (tk.ContainsKey(w) || tk.ContainsKey(t.Id)) { var ks = KeyMap.ToolKeysOf(node); return ks.Count > 0 ? ks[0] : Keys.None; }
        }
        return t.Key;
    }
    public static string KeyText(ToolBase t) { var k = KeyOf(t); return k == Keys.None ? "" : KeyMap.Format(k).ToUpperInvariant(); }

    /// <summary>모든 도구 (웹 판 순서)</summary>
    public static List<ToolBase> Ordered()
    {
        int Rank(ToolBase t)
        {
            string w = KeyMap.WebTool(t.Id);
            int i = Array.IndexOf(WebOrder, w); if (i >= 0) return i;
            int j = Array.IndexOf(WebRegOrder, w); if (j >= 0) return 100 + j;
            return 1000 + Registry.Tools.IndexOf(t);
        }
        return Registry.Tools.OrderBy(Rank).ToList();
    }

    enum Kind { Tool, More, Chips }
    sealed record Cell(Kind K, ToolBase Tool);
    readonly List<Cell> cells = new();
    List<ToolBase> more = new();

    public ToolBox()
    {
        Dock = DockStyle.Left;
        App.ToolChanged += () => { if (UiMode.IsSimple && App.Tool != null && !cells.Any(c => c.Tool == App.Tool)) Rebuild(); else Invalidate(); };
        App.ColorChanged += Invalidate;
        App.SettingsChanged += Invalidate;
        Ready();
    }

    static Font emoji;
    static Font Emoji => emoji ??= new Font("Segoe UI Emoji", 16f, GraphicsUnit.Pixel);

    public static int T =>(App.Main?.ClientSize.Height ?? 900) <= 780 ? (Theme.Large ? 46 : 40) : Theme.Tool;   // 웹 @media (max-height: 780px)
    static int Rad => (App.Main?.ClientSize.Height ?? 900) <= 780 ? 8 : 10;

    public void Rebuild()
    {
        cells.Clear();
        var all = Ordered();
        var (shown, hidden) = UiMode.ToolLists(all);
        foreach (var t in shown) cells.Add(new Cell(Kind.Tool, t));
        more = hidden;
        if (more.Count > 0) cells.Add(new Cell(Kind.More, null));
        cells.Add(new Cell(Kind.Chips, null));
        UpdateWidth();
        Relayout();
    }

    int rows = 1;
    void UpdateWidth()
    {
        int t = T, h = Math.Max(Height, t + 12);
        rows = Math.Max(1, (h - 12 + 3) / (t + 3));
        int cols = Math.Max(1, (cells.Count + rows - 1) / rows);
        int w = 10 + cols * t + (cols - 1) * 3 + 1;
        if (Width == w) return;
        // 부모가 배치하는 중에는 폭이 덮어써질 수 있어 다음 차례에 한 번 더
        Width = w;
        if (IsHandleCreated) BeginInvoke((Action)(() => { if (Width != w) Width = w; Parent?.PerformLayout(); }));
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); int r = rows; UpdateWidth(); if (r != rows) Relayout(); }

    protected override void LayoutHits()
    {
        int t = T;
        for (int i = 0; i < cells.Count; i++)
        {
            int col = i / rows, row = i % rows;
            var r = new Rectangle(5 + col * (t + 3), 6 + row * (t + 3), t, t);
            var c = cells[i];
            switch (c.K)
            {
                case Kind.Tool:
                    var tool = c.Tool;
                    string k = KeyText(tool);
                    Hits.Add(new Hit { R = r, Data = c, Tip = tool.Name + (k.Length > 0 ? $" ({k})" : ""), Click = () => { if (App.Tool == tool) (App.Main as MainForm)?.SetSideVisible(true); App.SetTool(tool.Id); } });
                    break;
                case Kind.More:
                    Hits.Add(new Hit { R = r, Data = c, Tip = $"다른 도구 {more.Count}개", Click = () => MorePopup(r) });
                    break;
                case Kind.Chips:
                    int cs = (int)Math.Round(t * 0.62);
                    Hits.Add(new Hit { R = new Rectangle(r.Right - cs, r.Bottom - cs, cs, cs), Data = "bg", Tip = "배경색 (탭: 바꾸기)", Click = () => WebIds.Run("color.swap") });
                    Hits.Add(new Hit { R = new Rectangle(r.X, r.Y, cs, cs), Data = "fg", Tip = "전경색", Click = () => (App.Main as MainForm)?.Side.ShowTab("color") });
                    break;
            }
        }
    }

    protected override void PaintBar(Graphics g)
    {
        g.Clear(Theme.Panel);
        Line(g, Theme.Border, Width - 1, 0, 1, Height);
        foreach (var h in Hits)
        {
            if (h.Data is Cell c)
            {
                bool on = c.K == Kind.Tool && App.Tool == c.Tool;
                if (on) Fill(g, h.R, Theme.Accent, Rad);
                else if (h == Hover) Fill(g, h.R, Theme.Panel3, Rad);
                var col = on ? Theme.OnAccent : Theme.Text;
                var ir = new RectangleF(h.R.X + (h.R.Width - 24) / 2f, h.R.Y + (h.R.Height - 24) / 2f, 24, 24);
                if (c.K == Kind.More) Icons.Draw(g, "dots", ir, col);
                else
                {
                    string ic = IconOf(c.Tool);
                    if (ic != null) Icons.Draw(g, ic, ir, col);
                    else DrawText(g, c.Tool.Icon, Emoji, h.R, col);
                }
            }
            else Chip(g, h.R, (string)h.Data == "fg" ? App.Fg : App.Bg);
        }
    }

    /// <summary>색 칩 (웹 .chip: 2px #ddd 테두리, 둥근 6px, 바깥 1px 검정)</summary>
    static void Chip(Graphics g, Rectangle r, uint argb)
    {
        var rf = new RectangleF(r.X - 0.5f, r.Y - 0.5f, r.Width + 1, r.Height + 1);
        using (var p = Theme.Round(rf, 7)) using (var b = new SolidBrush(Color.Black)) g.FillPath(b, p);
        using (var p = Theme.Round(r, 6)) using (var b = new SolidBrush(ColorTranslator.FromHtml("#dddddd"))) g.FillPath(b, p);
        var inner = Rectangle.Inflate(r, -2, -2);
        using (var p = Theme.Round(inner, 4)) using (var b = new SolidBrush(Color.FromArgb((int)(argb | 0xFF000000)))) g.FillPath(b, p);
    }

    /// <summary>"…" 팝업 (웹 .menu.tool-more): 아이콘 + 이름 + 키, 맨 아래 "도구 막대 꾸미기…"</summary>
    void MorePopup(Rectangle anchor)
    {
        var m = MenuLook.NewDropDown();
        foreach (var t in more)
        {
            var tool = t;
            m.Items.Add(MenuLook.Item(t.Name, new MenuTag { Icon = IconOf(t) ?? "dots", Kbd = KeyText(t) }, (_, _) => App.SetTool(tool.Id)));
        }
        m.Items.Add(MenuLook.Sep());
        m.Items.Add(MenuLook.Item("도구 막대 꾸미기…", new MenuTag(), (_, _) => UiMode.Customize("tools")));
        MenuLook.Equalize(m);
        m.Closed += (_, _) => BeginInvoke((Action)m.Dispose);
        var p = PointToScreen(new Point(anchor.Right + 6, Math.Max(4, anchor.Top - 40)));
        m.Show(p);
        LastPopup = m;
    }
    public ToolStripDropDown LastPopup;
    public void OpenMore() { var h = Hits.FirstOrDefault(x => x.Data is Cell { K: Kind.More }); if (h != null) MorePopup(h.R); }
    public IEnumerable<ToolBase> Shown => cells.Where(c => c.K == Kind.Tool).Select(c => c.Tool);
    public IReadOnlyList<ToolBase> More => more;
}
