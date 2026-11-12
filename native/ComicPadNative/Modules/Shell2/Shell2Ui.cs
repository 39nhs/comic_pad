// Shell2 공통: 메인 창 부품(위 막대·도구 막대·오른쪽 패널), 작은 창·알림, 퀵 동작 목록(퀵바·아래 막대·원형 메뉴가 같이 씀).
using System.Text.Json.Nodes;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class Shell2Ui
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    public static MainForm Main => App.Main as MainForm;
    public static CanvasView Canvas => Main?.Canvas;
    public static UI.Chrome.SidePanel Side => Main?.Side;
    public static UI.Chrome.ToolBox ToolBar => Main?.Tools;
    public static MenuStrip MenuBar => Main?.MenuBar;

    public static Form Modal(string title, Control body, int w = 640, int h = 520) => Dlg.Modal(title, body, w, h);
    public static bool Confirm(string msg, string ok = "확인") => Dlg.Confirm(msg, ok);
    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://")) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>오른쪽 아래 작은 알림 (버튼 하나). 15초 뒤 저절로 닫힘</summary>
    public static void Notify(string text, string button, Action run)
    {
        if (App.Main == null || App.Main.IsDisposed) return;
        // 웹 .toast.has-actions: 어두운 둥근 알림 + [단추] [×]
        var f = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, BackColor = Color.FromArgb(30, 30, 34), ForeColor = Opt.Fg, Size = new Size(380, 72), Owner = App.Main, Font = UI.Controls.Web.Body };
        var l = new Label { Text = text, AutoSize = false, Location = new Point(16, 10), Size = new Size(230, 52), ForeColor = Opt.Fg, TextAlign = ContentAlignment.MiddleLeft };
        var b = Opt.Button(button, () => { f.Close(); run(); }, true); b.Margin = Padding.Empty; b.Size = b.GetPreferredSize(Size.Empty); b.Location = new Point(f.Width - 54 - b.Width, (f.Height - b.Height) / 2);
        var x = new UI.Controls.WebButton("×", UI.Controls.BtnKind.Normal) { Size = new Size(38, 38), AutoSize = false, Location = new Point(f.Width - 48, (f.Height - 38) / 2) };
        x.Click += (_, _) => f.Close();
        f.Paint += (_, e) => UI.Controls.Web.Box(e.Graphics, new Rectangle(0, 0, f.Width, f.Height), 0, null, UI.Controls.Web.ModalLine);
        f.HandleCreated += (_, _) => { try { int round = 2; DwmSetWindowAttribute(f.Handle, 33, ref round, 4); } catch { } };
        f.Controls.AddRange(new Control[] { l, b, x });
        var r = App.Main.RectangleToScreen(App.Main.ClientRectangle);
        f.Location = new Point(r.Right - f.Width - 16, r.Bottom - f.Height - 40);
        var t = new System.Windows.Forms.Timer { Interval = 15000 }; t.Tick += (_, _) => { t.Dispose(); if (!f.IsDisposed) f.Close(); };
        f.Show(); t.Start();
    }

    public static JsonObject Obj(JsonObject parent, string key) { if (parent[key] is not JsonObject o) { o = new JsonObject(); parent[key] = o; } return o; }

    /// <summary>지금 브러시(지우개 도구면 지우개) 프리셋</summary>
    public static (BrushPreset p, string cat) ActiveBrush()
    {
        string cat = App.Tool?.Id == "eraser" ? "eraser" : "brush";
        return (PresetStore.Active(cat), cat);
    }

    public static Bitmap Glyph(string text, int size, Color fg)
    {
        var b = new Bitmap(size, size);
        using var g = Graphics.FromImage(b);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI Emoji", size * 0.5f);
        using var br = new SolidBrush(fg);
        var sz = g.MeasureString(text, font);
        g.DrawString(text, font, br, (size - sz.Width) / 2, (size - sz.Height) / 2);
        return b;
    }
}

/// <summary>퀵 동작 하나 (웹 판 registerQuickAction 과 같은 id)</summary>
public sealed record QuickAction(string Id, string Label, string Icon, Action Run, Func<bool> Active = null, string Type = null);

public static class QuickActions
{
    public static readonly List<QuickAction> All = new();
    public static QuickAction Get(string id) => All.Find(a => a.Id == id);
    static void Q(string id, string label, string icon, Action run, Func<bool> active = null, string type = null) { All.RemoveAll(a => a.Id == id); All.Add(new QuickAction(id, label, icon, run, active, type)); }
    static void Cmd(string webId) { if (!WebIds.Run(webId)) App.Toast("없는 명령: " + webId); }

    public static void Init()
    {
        // 퀵바 (웹 ui/quickbar.js)
        Q("undo", "실행취소", "↶", () => Cmd("edit.undo"));
        Q("redo", "다시실행", "↷", () => Cmd("edit.redo"));
        Q("penEraser", "펜↔지우개", "⌫", () => App.SetTool(App.Tool?.Id == "eraser" ? "brush" : "eraser"), () => App.Tool?.Id == "eraser");
        Q("eyedropper", "스포이트(한 번)", "💧", EyedropperOnce, () => App.Tool?.Id == "eyedropper");
        Q("size", "브러시 크기", "", null, null, "size");
        Q("opacity", "불투명도", "", null, null, "opacity");
        Q("fit", "화면 맞춤", "⛶", () => Cmd("view.fit"));
        Q("flip", "좌우 반전 보기", "⇋", () => Cmd("view.flip"), () => Shell2Ui.Canvas?.V.Flip == true);
        Q("rotL", "왼쪽 회전", "⟲", () => Cmd("view.rotL"));
        Q("rotR", "오른쪽 회전", "⟳", () => Cmd("view.rotR"));
        Q("deselect", "선택 해제", "⬚", () => Cmd("select.none"));
        Q("transform", "변형", "⤧", () => Cmd("edit.transform"));
        Q("finger", "손가락 그리기", "☝", () => Cmd("settings.fingerDraw"), () => App.Get("fingerDraw", false));
        Q("newLayer", "새 레이어", "＋", () => Cmd("layer.new"));
        Q("rough", "러프 레이어", "✏", () => Cmd("layer.newRough"));
        Q("save", "저장", "💾", () => Cmd("file.save"));
        Q("zen", "패널 숨기기", "▣", () => Cmd("view.toggleUI"));
        Q("sym", "대칭", "⧓", () => Cmd("view.symmetry"), () => App.Get("symmetry", "none") != "none");
        Q("swap", "색 바꾸기", "⇄", () => Cmd("color.swap"));
        // 아래 막대 (웹 ui/bottombar.js)
        Q("brushTool", "브러시", "✎", () => App.SetTool("brush"), () => App.Tool?.Id == "brush");
        Q("hand", "화면 이동", "✋", () => App.SetTool(App.Tool?.Id == "hand" ? (App.PrevTool?.Id ?? "brush") : "hand"), () => App.Tool?.Id == "hand");
        Q("panels", "패널", "▤", () => Cmd("view.toggleSide"));
        Q("layersTab", "레이어", "☰", () => ShowTab("레이어"));
        Q("colorTab", "색", "🎨", () => ShowTab("색"));
        Q("fill", "채우기", "🪣", () => App.SetTool("fill"), () => App.Tool?.Id == "fill");
        Q("selectTool", "선택", "⬚", () => App.SetTool("select"), () => App.Tool?.Id == "select");
        Q("lassoFillQ", "올가미 채우기", "◍", () => App.SetTool("lassofill"), () => App.Tool?.Id == "lassofill");
        Q("webtoonView", "웹툰 미리보기", "📱", () => Cmd("view.webtoon"));
        Q("extendDown", "아래로 늘리기", "⇩", () => Cmd("edit.extendDown"));
        Q("radial", "원형 메뉴", "◎", () => { var c = Shell2Ui.Canvas; if (c != null) Radial.Open(c.PointToScreen(new Point(c.Width / 2, c.Height / 2))); });
    }

    /// <summary>오른쪽 패널 탭 보이기 (웹 id 또는 제목). 패널이 숨어 있으면 보이게</summary>
    public static void ShowTab(string idOrTitle)
    {
        var m = Shell2Ui.Main; if (m == null) return;
        if (!m.Side.Visible) m.SetSideVisible(true);
        m.Side.ShowTab(idOrTitle);
    }

    /// <summary>스포이트 한 번: 색을 고르면 전 도구로</summary>
    static void EyedropperOnce()
    {
        string prev = App.Tool?.Id;
        if (prev == "eyedropper") return;
        App.SetTool("eyedropper");
        void Back() { App.ColorChanged -= Back; var t = new System.Windows.Forms.Timer { Interval = 120 }; t.Tick += (_, _) => { t.Dispose(); if (App.Tool?.Id == "eyedropper" && prev != null) App.SetTool(prev); }; t.Start(); }
        App.ColorChanged += Back;
    }
}
