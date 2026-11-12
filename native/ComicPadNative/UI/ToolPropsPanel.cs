// 도구 속성 패널 (웹 ui/panels.js toolPropsPanel): 머리(.panel-head: 도구 아이콘 + 주황 굵은 이름) + 굴리는 칸(.panel-scroll)에 지금 도구의 옵션.
// 도구가 바뀌면 다시 만듦. 메인 창의 오른쪽 도크 "도구" 탭에 Dock=Fill 로 넣어 쓰면 됨.
using ComicPadNative.UI.Controls;

namespace ComicPadNative.UI;

public sealed class ToolPropsPanel : UserControl
{
    readonly WebPanelHead head = new("", null) { Dock = DockStyle.Top };
    readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Panel };

    /// <summary>도구 id → 웹 아이콘 이름 (같은 이름이 없을 때)</summary>
    static readonly Dictionary<string, string> IconOf = new()
    {
        ["hand"] = "hand", ["view"] = "hand", ["eyedropper"] = "eyedropper", ["select"] = "select", ["lasso"] = "lasso", ["wand"] = "wand",
        ["vselect"] = "vsel", ["aiselect"] = "aisel", ["refinebrush"] = "refine", ["pixel"] = "pencil", ["motion"] = "motion", ["memo"] = "memo",
    };
    public static string IconFor(ToolBase t) => t == null ? null : IconOf.TryGetValue(t.Id, out var n) ? n : Icons.Has(t.Id) ? t.Id : "brush";

    public ToolPropsPanel()
    {
        BackColor = Theme.Panel; Font = Web.Body;
        scroll.DarkScroll();
        scroll.AutoScroll = false; scroll.HorizontalScroll.Maximum = 0; scroll.HorizontalScroll.Visible = false; scroll.AutoScroll = true;
        Controls.Add(scroll); Controls.Add(head);
        App.ToolChanged += Rebuild;
        Disposed += (_, _) => App.ToolChanged -= Rebuild;
        Rebuild();
    }

    void Rebuild()
    {
        if (IsDisposed) return;
        var t = App.Tool;
        head.Text = t?.Name ?? ""; head.IconName = IconFor(t); head.Invalidate();
        scroll.SuspendLayout();
        foreach (Control c in scroll.Controls.Cast<Control>().ToList()) c.Dispose();
        scroll.Controls.Clear();
        Control opts = null;
        try { opts = t?.BuildOptions(); } catch (Exception ex) { opts = Opt.Panel(Opt.Help("옵션 표시 오류: " + ex.Message)); }
        opts ??= Opt.Panel(Opt.Help("설정할 옵션이 없습니다."));
        scroll.Controls.Add(opts);
        scroll.ResumeLayout();
    }
}
