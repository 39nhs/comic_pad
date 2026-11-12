// 화면 구성 (웹 ui/layout.js): 기본은 "간단히" (자주 쓰는 도구·메뉴만), 나머지는 "모든 항목 보기"·기능 검색(Ctrl+K)으로.
// 화면 꾸미기 창에서 도구 막대·메뉴 항목·패널 탭을 직접 고른다. 설정은 웹 판과 같은 settings.ui = { mode, tools, menus, menuPick, hiddenPanels }
//  - 메뉴·메뉴 항목·도구·패널 모두 웹 판 id 로 저장 (네이티브에만 있는 메뉴 항목은 메뉴 경로로)
//  - 메뉴 구성은 UI/Chrome/WebMenus (웹 판과 같은 메뉴·순서), 그리기는 위 막대(TopBar)·도구 막대(ToolBox)·오른쪽 패널(SidePanel)
//  - 큰 버튼(largeUI)·패널 숨기기·전체 화면도 여기서
using System.Text.Json.Nodes;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;
using ComicPadNative.UI.Chrome;

namespace ComicPadNative.Modules.Shell2;

public static class UiMode
{
    public static readonly string[] SimpleTools = { "brush", "eraser", "fill", "eyedropper", "select", "transform", "shape", "ruler", "panel", "bubble", "text", "view" };
    static readonly string[] SimpleMenus = { "file", "edit", "layer", "select", "comic", "adjust", "filter", "ai", "view", "help" };
    public static readonly Dictionary<string, string[]> Frequent = new()
    {
        ["file"] = new[] { "file.new", "file.open", "file.save", "file.saveAs", "file.import", "file.export", "file.versions", "file.storage", "file.printExport", "file.webtoonExport", "file.backup", "pixel.newDoc", "file.ebook" },
        ["edit"] = new[] { "edit.undo", "edit.redo", "edit.cut", "edit.copy", "edit.paste", "edit.clear", "edit.fill", "edit.transform", "edit.flipH", "edit.canvasSize", "edit.contentAwareFill", "edit.puppet", "macro.open" },
        ["layer"] = new[] { "layer.new", "layer.newVector", "layer.newRough", "layer.newFolder", "layer.group", "layer.duplicate", "layer.delete", "layer.mergeDown", "layer.clip", "layer.alphaLock", "layer.lock", "layer.addMask" },
        ["select"] = new[] { "select.all", "select.none", "select.invert", "select.grow", "select.shrink", "select.toLayer", "select.refine", "path.tool" },
        ["comic"] = new[] { "panel.frame", "panel.templates", "@컷 나누기 도구", "sfx.tool", "print.preflight", "@말풍선 도구", "@텍스트 도구", "color.autoFlat", "color.hintLayer", "color.hintFill", "progress.open", "print.setup", "story.new", "story.prev", "story.next", "story.add", "3d.open" },
        ["adjust"] = new[] { "adjust.levels", "adjust.curves", "adjust.hsl", "adjust.bc", "adjust.gradmap", "adjust.bw" },
        ["filter"] = new[] { "filter.lineExtract", "filter.lineClean", "filter.mono", "filter.blur", "filter.outline", "filter.shadow", "ai.img2img", "pixel.quantize" },
        ["view"] = new[] { "view.fit", "view.actual", "view.zoomIn", "view.zoomOut", "view.rotL", "view.rotR", "view.resetRotation", "view.flip", "view.luma", "view.toggleUI", "view.fullscreen", "workspace.manage", "ui.customize", "ui.mode", "pixel.grid" },
        ["ai"] = new[] { "ai.lineart", "ai.cutout", "ai.select", "ai.depth", "ai.upscale", "ai.pose", "filter.lineClean", "ai.img2img", "ai.setup", "ai.models", "vt.studio", "vt.setup" },
        ["help"] = new[] { "settings.open", "help.manual", "setup.choose", "palette.open", "help.features", "help.keys", "pen.diagnose", "mobile.connect", "selftest.run", "help.update", "help.report", "help.about" },
    };
    /// <summary>웹 판 패널 id ↔ 탭 제목</summary>
    public static readonly Dictionary<string, string> PanelTitle = new() { ["tool"] = "도구", ["color"] = "색", ["nav"] = "내비", ["pages"] = "페이지", ["layers"] = "레이어", ["assets"] = "에셋", ["history"] = "기록", ["anim"] = "애니" };
    public static string PanelId(string title) => PanelTitle.FirstOrDefault(kv => kv.Value == title).Key ?? (title == "애니 타임라인" ? "anim" : title);

    public static JsonObject Ui() { if (App.Settings["ui"] is not JsonObject u) { u = new JsonObject { ["mode"] = "simple" }; App.Settings["ui"] = u; } return u; }
    public static bool IsSimple => (string)Ui()["mode"] != "full";
    static HashSet<string> Strs(JsonNode n) => (n as JsonArray)?.Select(x => (string)x).Where(x => x != null).ToHashSet() ?? new();

    // ---- 메뉴 ------------------------------------------------------------------------------------
    public static string ItemKey(string path) => WebIds.WebIdOf(path) ?? path;
    /// <summary>웹 메뉴 id (네이티브 메뉴 이름을 주어도 됨: "파일" → "file")</summary>
    public static string MenuIdOf(string idOrName) => WebMenus.Ids.Contains(idOrName) ? idOrName : WebIds.MenuId(idOrName);

    /// <summary>메뉴 경로가 고른 키에 드는지 (웹 id·"@이름"·경로 어느 것으로든)</summary>
    public static bool KeyMatches(HashSet<string> want, string path)
    {
        if (want == null) return true;
        if (want.Contains(path)) return true;
        string id = WebIds.WebIdOf(path); if (id != null && want.Contains(id)) return true;
        foreach (var kv in WebIds.Map) if (kv.Value == path && want.Contains(kv.Key)) return true;   // 같은 메뉴를 가리키는 다른 웹 id
        foreach (var kv in WebMenus.Extra) if (kv.Value == path && want.Contains(kv.Key)) return true;
        foreach (var w in want) if (!w.StartsWith("@") && WebMenus.Resolve(w) == path) return true;
        string leaf = path[(path.LastIndexOf('/') + 1)..];
        return want.Contains("@" + leaf);
    }

    public static bool MenuShown(string idOrName)
    {
        string id = MenuIdOf(idOrName);
        if (Ui()["menus"]?[id] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return !IsSimple || SimpleMenus.Contains(id) || !WebMenus.Ids.Contains(id);
    }

    /// <summary>간단히 모드에서 이 메뉴에 꺼내 둘 항목 키 (null = 전부)</summary>
    public static HashSet<string> Pick(string idOrName)
    {
        string id = MenuIdOf(idOrName);
        if (Ui()["menuPick"]?[id] is JsonArray a) return Strs(a);
        return Frequent.TryGetValue(id, out var f) ? f.ToHashSet() : null;
    }

    /// <summary>네이티브 창에만 있어 간단히 모드에서도 늘 보이는 항목</summary>
    static readonly HashSet<string> AlwaysShow = new() { "파일/끝내기" };

    /// <summary>웹 menuItems: 간단히 모드에서는 고른 것만 (구분선 정리), 숨긴 개수</summary>
    public static (List<MenuNode> items, int hidden) FilterMenu(string id, List<MenuNode> items, bool showAll)
    {
        if (showAll || !IsSimple) return (items, 0);
        var want = Pick(id) ?? items.Where(i => !i.Sep).Select(i => i.Key).ToHashSet();
        var outp = new List<MenuNode>(); int hidden = 0;
        foreach (var it in items)
        {
            if (it.Sep) { if (outp.Count > 0 && !outp[^1].Sep) outp.Add(it); continue; }
            bool show = want.Contains(it.Key) || AlwaysShow.Contains(it.Key) || (it.Sub != null && it.Sub.Any(c => !c.Sep && want.Contains(c.Key)));
            if (show) outp.Add(it); else hidden++;
        }
        while (outp.Count > 0 && outp[^1].Sep) outp.RemoveAt(outp.Count - 1);
        return (outp, hidden);
    }

    // ---- 도구 막대 ---------------------------------------------------------------------------
    /// <summary>웹 toolLists: 간단히 모드면 고른 도구만 (+ 지금 도구), 나머지는 "…"</summary>
    public static (List<ToolBase> shown, List<ToolBase> more) ToolLists(List<ToolBase> all)
    {
        if (!IsSimple) return (all, new List<ToolBase>());
        var pick = Ui()["tools"] is JsonArray a ? Strs(a) : SimpleTools.ToHashSet();
        var shown = all.Where(t => pick.Contains(KeyMap.WebTool(t.Id)) || pick.Contains(t.Id)).ToList();
        if (App.Tool != null && !shown.Contains(App.Tool) && all.Contains(App.Tool)) shown.Add(App.Tool);
        return (shown, all.Where(t => !shown.Contains(t)).ToList());
    }

    // ---- 패널 탭 -----------------------------------------------------------------------------
    public static bool PanelShown(string idOrTitle) => !Strs(Ui()["hiddenPanels"]).Contains(PanelId(idOrTitle));
    public static void ApplyPanels() => Shell2Ui.Side?.Rerender();
    public static IEnumerable<string> AllPanelTitles() => Shell2Ui.Side?.Defs.Select(d => d.Title) ?? Enumerable.Empty<string>();

    // ---- 모두 다시 -----------------------------------------------------------------------------
    public static void Refresh()
    {
        App.SaveSettingsSoon();
        var m = Shell2Ui.Main; if (m == null) return;
        m.BuildMenus();
        m.ApplyLayout();
        Bars.RenderQuick();
    }

    public static void SetMode(string mode)
    {
        Ui()["mode"] = mode;
        Refresh();
        App.Toast(mode == "full" ? "모든 도구·메뉴를 보여 줍니다" : "자주 쓰는 것만 보여 줍니다 (나머지는 메뉴 맨 아래 \"모든 항목 보기\" 또는 Ctrl+K)");
    }

    // ---- 큰 버튼 (터치용) -------------------------------------------------------------------------
    public static void ApplyLarge()
    {
        Theme.Large = App.Get("largeUI", false);
        Shell2Ui.Main?.ApplyLayout();
        Bars.RenderQuick();
    }

    // ---- 패널 숨기기·전체 화면 ----------------------------------------------------------------
    public static void ToggleUI()
    {
        var m = Shell2Ui.Main; if (m == null) return;
        m.Zen = !m.Zen;
        if (m.Zen) App.Toast("패널을 숨겼습니다 (Tab 또는 네 손가락 탭으로 다시)");
    }
    public static bool ZenOn => Shell2Ui.Main?.Zen == true;
    public static void ToggleSide() { var m = Shell2Ui.Main; if (m == null) return; m.SetSideVisible(!App.Get("sideVisible", true) || !m.Side.Visible); }
    public static void ShowSide() => Shell2Ui.Main?.SetSideVisible(true);

    static FormWindowState savedState; static FormBorderStyle savedBorder; static bool full;
    public static void ToggleFullscreen()
    {
        var f = App.Main; if (f == null) return;
        if (!full) { savedState = f.WindowState; savedBorder = f.FormBorderStyle; f.FormBorderStyle = FormBorderStyle.None; f.WindowState = FormWindowState.Normal; f.WindowState = FormWindowState.Maximized; full = true; }
        else { f.FormBorderStyle = savedBorder; f.WindowState = savedState == FormWindowState.Minimized ? FormWindowState.Normal : savedState; full = false; }
        Shell2Ui.Canvas?.Fit();
    }
    public static bool FullOn => full;

    // ---- 화면 꾸미기 창 --------------------------------------------------------------------------
    public static void Customize() => Customize("tools");
    public static void Customize(string tab)
    {
        var u = Ui();
        var root = new Panel { BackColor = Opt.Bg };
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 74, BackColor = Opt.Bg, Padding = new Padding(6) };
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(8) };
        root.Controls.Add(content); root.Controls.Add(head);
        using var f = Shell2Ui.Modal("화면 꾸미기", root, 680, 640);
        var tabBtns = new List<Button>();
        void Render()
        {
            content.SuspendLayout();
            foreach (Control c in content.Controls.Cast<Control>().ToList()) c.Dispose();
            foreach (var b in tabBtns) { bool on = (string)b.Tag == tab; b.BackColor = on ? Opt.Accent : Opt.Bg2; b.ForeColor = on ? Color.Black : Opt.Fg; }
            content.Controls.Add(Opt.Row(Opt.Button("간단히 (자주 쓰는 것만)", () => { SetMode("simple"); Render(); }, IsSimple), Opt.Button("모든 기능 보기", () => { SetMode("full"); Render(); }, !IsSimple)));
            CheckBox Chk(string label, bool on, Action<bool> set, string icon = null)
            {
                var c = new CheckBox { Text = (icon != null ? "      " : "") + label, Checked = on, AutoSize = true, ForeColor = Opt.Fg, Margin = new Padding(0, 1, 0, 1) };
                if (icon != null) c.Paint += (_, e) => Icons.Draw(e.Graphics, icon, new RectangleF(18, (c.Height - 16) / 2f, 16, 16), Opt.Fg);
                c.CheckedChanged += (_, _) => { set(c.Checked); Refresh(); };
                return c;
            }
            if (tab == "tools")
            {
                content.Controls.Add(Wide(Opt.Help("간단히 모드에서 왼쪽 도구 막대에 꺼내 둘 도구. 나머지는 도구 막대의 \"…\" 버튼에 있습니다.")));
                var pick = u["tools"] is JsonArray a ? Strs(a) : SimpleTools.ToHashSet();
                var ordered = ToolBox.Ordered();
                foreach (var t in ordered)
                {
                    string wid = KeyMap.WebTool(t.Id);
                    content.Controls.Add(Chk(t.Name, pick.Contains(wid), v => { if (v) pick.Add(wid); else pick.Remove(wid); u["tools"] = new JsonArray(ordered.Select(x => KeyMap.WebTool(x.Id)).Where(pick.Contains).Select(x => (JsonNode)x).ToArray()); }, ToolBox.IconOf(t) ?? "dots"));
                }
            }
            else if (tab == "menus")
            {
                content.Controls.Add(Wide(Opt.Help("메뉴 막대에 보일 메뉴와, 간단히 모드에서 각 메뉴에 꺼내 둘 항목. 숨긴 항목은 메뉴 맨 아래 \"모든 항목 보기\"로 언제든 볼 수 있습니다.")));
                foreach (var top in WebMenus.Tree())
                {
                    string id = top.Id;
                    content.Controls.Add(Chk("■ " + top.Label + " 메뉴 보이기", MenuShown(id), v => Shell2Ui.Obj(u, "menus")[id] = v));
                    var keys = top.Items.Where(n => !n.Sep).Select(n => n.Key).Distinct().ToList();
                    var cur = Pick(id) is HashSet<string> p ? keys.Where(p.Contains).ToHashSet() : keys.ToHashSet();
                    foreach (var n in top.Items.Where(n => !n.Sep))
                    {
                        string key = n.Key;
                        content.Controls.Add(Chk("      " + n.Label, cur.Contains(key), v => { if (v) cur.Add(key); else cur.Remove(key); Shell2Ui.Obj(u, "menuPick")[id] = new JsonArray(keys.Where(cur.Contains).Select(x => (JsonNode)x).ToArray()); }));
                    }
                }
            }
            else if (tab == "panels")
            {
                content.Controls.Add(Wide(Opt.Help("오른쪽 패널에 보일 탭. (패널 탭 옆 ⧉ 버튼으로 앱 밖 창으로 꺼낼 수도 있습니다)")));
                foreach (var d in Shell2Ui.Side?.Defs ?? new List<SideDef>())
                {
                    string pid = d.Id;
                    content.Controls.Add(Chk(d.Title, PanelShown(pid), v =>
                    {
                        var set = Strs(u["hiddenPanels"]);
                        if (v) set.Remove(pid); else set.Add(pid);
                        u["hiddenPanels"] = new JsonArray(set.Select(x => (JsonNode)x).ToArray());
                    }, d.Icon));
                }
            }
            else
            {
                content.Controls.Add(Opt.Check("큰 버튼 (터치용)", () => App.Get("largeUI", false), v => { App.Set("largeUI", v); ApplyLarge(); }));
                content.Controls.Add(Opt.Check("왼손잡이 배치 (패널을 왼쪽에)", () => App.Get("leftHanded", false), v => { App.Set("leftHanded", v); Workspaces.ApplyLayout(); }));
                content.Controls.Add(Opt.Row(Opt.Button("아래 큰 버튼 막대 고르기…", Bars.EditBottom), Opt.Button("퀵바 버튼 고르기…", Bars.EditQuick), Opt.Button("원형 메뉴 편집…", Radial.EditDialog)));
                content.Controls.Add(Opt.Row(Opt.Button("작업 화면 저장·바꾸기…", Workspaces.ManageDialog), Opt.Button("설정 창을 앱 밖으로…", Popout.Settings)));
                content.Controls.Add(Opt.Row(Opt.Button("화면 구성 초기화", () =>
                {
                    u.Remove("tools"); u.Remove("menus"); u.Remove("menuPick"); u.Remove("hiddenPanels"); u["mode"] = "simple";
                    Refresh(); Render(); App.Toast("화면 구성을 처음 상태로 되돌렸습니다");
                })));
            }
            content.ResumeLayout();
        }
        foreach (var (k, t) in new[] { ("tools", "도구 막대"), ("menus", "메뉴"), ("panels", "패널 탭"), ("etc", "그 밖") })
        {
            var b = Opt.Button(t, () => { tab = k; Render(); }); b.Tag = k; b.MinimumSize = new Size(90, 28); tabBtns.Add(b); head.Controls.Add(b);
        }
        Render();
        f.ShowDialog(App.Main);
        Refresh();
    }

    static Control Wide(Label l) { l.MaximumSize = new Size(600, 0); return l; }
}
