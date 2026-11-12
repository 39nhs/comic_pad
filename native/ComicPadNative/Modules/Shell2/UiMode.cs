// 화면 구성 (웹 ui/layout.js): 기본은 "간단히" (자주 쓰는 도구·메뉴만), 나머지는 "모든 항목 보기"·기능 검색(Ctrl+K)으로.
// 화면 꾸미기 창에서 도구 막대·메뉴 항목·패널 탭을 직접 고른다. 설정은 웹 판과 같은 settings.ui = { mode, tools, menus, menuPick, hiddenPanels }
//  - 도구·메뉴 항목은 웹 판 id 로 저장 (네이티브에만 있는 메뉴 항목은 메뉴 경로로)
//  - 큰 버튼(largeUI)·패널 숨기기·전체 화면도 여기서
using System.Text.Json.Nodes;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

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
    /// <summary>웹 판 패널 id ↔ 네이티브 탭 제목</summary>
    public static readonly Dictionary<string, string> PanelTitle = new() { ["layers"] = "레이어", ["color"] = "색", ["pages"] = "페이지", ["history"] = "기록", ["nav"] = "내비", ["assets"] = "에셋", ["anim"] = "애니 타임라인", ["tool"] = "도구 옵션" };
    public static string PanelId(string title) => PanelTitle.FirstOrDefault(kv => kv.Value == title).Key ?? title;

    public static JsonObject Ui() { if (App.Settings["ui"] is not JsonObject u) { u = new JsonObject { ["mode"] = "simple" }; App.Settings["ui"] = u; } return u; }
    public static bool IsSimple => (string)Ui()["mode"] != "full";
    static HashSet<string> Strs(JsonNode n) => (n as JsonArray)?.Select(x => (string)x).Where(x => x != null).ToHashSet() ?? new();

    // ---- 메뉴 항목 키: 웹 id 가 있으면 그것, 없으면 "@이름"(웹의 이름 항목) 또는 메뉴 경로 -----------------------
    public static string ItemKey(string path) => WebIds.WebIdOf(path) ?? path;
    /// <summary>네이티브에서 늘 보이는 항목 (웹 판에서는 다른 자리에 있는 것)</summary>
    static readonly HashSet<string> AlwaysShow = new() { "파일/설정…", "파일/최근 작품·자동 저장…", "파일/끝내기" };
    public static bool KeyMatches(HashSet<string> want, string path)
    {
        if (want.Contains(path) || AlwaysShow.Contains(path)) return true;
        string id = WebIds.WebIdOf(path); if (id != null && want.Contains(id)) return true;
        foreach (var kv in WebIds.Map) if (kv.Value == path && want.Contains(kv.Key)) return true;   // 같은 메뉴를 가리키는 다른 웹 id
        string leaf = path[(path.LastIndexOf('/') + 1)..];
        return want.Contains("@" + leaf);
    }

    public static bool MenuShown(string top)
    {
        string id = WebIds.MenuId(top);
        if (Ui()["menus"]?[id] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return !IsSimple || SimpleMenus.Contains(id) || !WebIds.MenuName.ContainsKey(id);
    }

    /// <summary>간단히 모드에서 이 메뉴에 꺼내 둘 항목 (null = 전부)</summary>
    public static HashSet<string> Pick(string top)
    {
        string id = WebIds.MenuId(top);
        if (Ui()["menuPick"]?[id] is JsonArray a) return Strs(a);
        return Frequent.TryGetValue(id, out var f) ? f.ToHashSet() : null;
    }

    static readonly HashSet<string> showAllOnce = new();

    /// <summary>메뉴 막대가 만들어질 때: 숨길 메뉴·항목 처리 (열 때마다 다시 거름)</summary>
    public static void OnMenusBuilt(MenuStrip ms)
    {
        foreach (var top in ms.Items.OfType<ToolStripMenuItem>().ToList())
        {
            top.Visible = MenuShown(top.Text);
            var t = top;
            t.DropDownOpening += (_, _) => FilterDropDown(t);
        }
    }

    static string PathOf(ToolStripItem it)
    {
        var parts = new List<string> { it.Text };
        var p = it.OwnerItem;
        while (p != null) { parts.Insert(0, p.Text); p = p.OwnerItem; }
        return string.Join("/", parts);
    }

    static void FilterDropDown(ToolStripMenuItem top)
    {
        var items = top.DropDownItems;
        foreach (var x in items.OfType<ToolStripItem>().Where(x => (string)x.Tag == "cp-showall").ToList()) { items.Remove(x); x.Dispose(); }
        var pick = IsSimple && !showAllOnce.Contains(top.Text) ? Pick(top.Text) : null;
        showAllOnce.Remove(top.Text);
        int hidden = 0;
        foreach (ToolStripItem it in items)
        {
            if (it is ToolStripSeparator) continue;
            bool show = pick == null || KeyMatches(pick, PathOf(it)) || (it is ToolStripMenuItem mi && mi.DropDownItems.OfType<ToolStripMenuItem>().Any(c => KeyMatches(pick, PathOf(c))));
            it.Visible = show;
            if (!show) hidden++;
        }
        // 구분선 정리: 맨 앞·맨 뒤·연달아 있는 것 숨김
        ToolStripItem prev = null;
        foreach (ToolStripItem it in items)
        {
            if (it is ToolStripSeparator) { it.Visible = prev != null && prev is not ToolStripSeparator; if (it.Visible) prev = it; }
            else if (it.Visible) prev = it;
        }
        if (prev is ToolStripSeparator) prev.Visible = false;
        if (hidden > 0)
        {
            items.Add(new ToolStripSeparator { Tag = "cp-showall" });
            var all = new ToolStripMenuItem($"모든 항목 보기 ({hidden}개 더)…") { Tag = "cp-showall" };
            all.Click += (_, _) => { showAllOnce.Add(top.Text); top.ShowDropDown(); };
            items.Add(all);
        }
    }

    // ---- 도구 막대 ---------------------------------------------------------------------------
    public static void OnToolsBuilt(ToolStrip ts)
    {
        foreach (var x in ts.Items.OfType<ToolStripItem>().Where(x => (string)x.Name == "cp-more").ToList()) { ts.Items.Remove(x); x.Dispose(); }
        bool large = App.Get("largeUI", false);
        var pick = IsSimple ? (Ui()["tools"] is JsonArray a ? Strs(a) : SimpleTools.ToHashSet()) : null;
        var more = new List<ToolBase>();
        foreach (var b in ts.Items.OfType<ToolStripButton>())
        {
            if (b.Tag is not string id) continue;
            b.Width = large ? 64 : 48; b.Height = large ? 54 : 40;
            bool show = pick == null || pick.Contains(KeyMap.WebTool(id)) || pick.Contains(id) || App.Tool?.Id == id;
            b.Visible = show;
            if (!show && Registry.Tools.Find(t => t.Id == id) is ToolBase tb) more.Add(tb);
        }
        ts.Width = large ? 68 : 52;
        if (more.Count > 0)
        {
            var mb = new ToolStripButton("…") { Name = "cp-more", ToolTipText = $"도구 더 보기 ({more.Count}개)", AutoSize = false, Width = large ? 64 : 48, Height = large ? 40 : 30, Font = new Font("Segoe UI", 12f), ForeColor = Opt.Fg };
            mb.Click += (_, _) =>
            {
                var m = new ContextMenuStrip();
                foreach (var t in more) { var tt = t; m.Items.Add($"{t.Icon}  {t.Name}{(t.Key != Keys.None ? $"   ({t.Key})" : "")}", null, (_, _) => App.SetTool(tt.Id)); }
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("도구 막대 꾸미기…", null, (_, _) => Customize("tools"));
                m.Show(ts, new Point(ts.Width, mb.Bounds.Y));
            };
            ts.Items.Add(mb);
        }
    }

    // ---- 패널 탭 -----------------------------------------------------------------------------
    static readonly Dictionary<string, (TabPage page, int index)> hiddenTabs = new();
    public static bool PanelShown(string title) => !Strs(Ui()["hiddenPanels"]).Contains(PanelId(title));

    public static void ApplyPanels()
    {
        var tabs = Shell2Ui.SideTabs; if (tabs == null) return;
        // 숨겼던 것 다시 넣기 → 지금 설정대로 빼기
        foreach (var (title, (page, idx)) in hiddenTabs.ToList()) if (PanelShown(title) && !page.IsDisposed) { tabs.TabPages.Insert(Math.Min(idx, tabs.TabPages.Count), page); hiddenTabs.Remove(title); }
        foreach (TabPage p in tabs.TabPages.Cast<TabPage>().ToList())
            if (!PanelShown(p.Text) && tabs.TabPages.Count > 1) { hiddenTabs[p.Text] = (p, tabs.TabPages.IndexOf(p)); tabs.TabPages.Remove(p); }
    }

    public static IEnumerable<string> AllPanelTitles()
    {
        var tabs = Shell2Ui.SideTabs;
        var l = tabs == null ? new List<string>() : tabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList();
        l.AddRange(hiddenTabs.Keys);
        return l.Distinct();
    }

    // ---- 모두 다시 -----------------------------------------------------------------------------
    public static void Refresh()
    {
        App.SaveSettingsSoon();
        Shell2Ui.Main?.BuildMenus();
        if (Shell2Ui.ToolBar is ToolStrip ts) OnToolsBuilt(ts);
        ApplyPanels();
        ApplyLarge();
    }

    public static void SetMode(string mode)
    {
        Ui()["mode"] = mode;
        Refresh();
        App.Toast(mode == "full" ? "모든 도구·메뉴를 보여 줍니다" : "자주 쓰는 것만 보여 줍니다 (나머지는 메뉴 맨 아래 \"모든 항목 보기\" 또는 Ctrl+K)");
    }

    // ---- 큰 버튼 (터치용) -------------------------------------------------------------------------
    static float baseMenuFont = 0;
    public static void ApplyLarge()
    {
        bool large = App.Get("largeUI", false);
        if (Shell2Ui.MenuBar is MenuStrip ms)
        {
            if (baseMenuFont == 0) baseMenuFont = ms.Font.Size;
            float sz = large ? baseMenuFont * 1.3f : baseMenuFont;
            if (Math.Abs(ms.Font.Size - sz) > 0.01) ms.Font = new Font(ms.Font.FontFamily, sz);
        }
        if (Shell2Ui.ToolBar is ToolStrip ts) OnToolsBuilt(ts);
        Bars.RenderAll();
    }

    // ---- 패널 숨기기·전체 화면 ----------------------------------------------------------------
    static bool zen; static (bool side, bool tools, bool bottomBar)? zenSaved;
    public static void ToggleUI()
    {
        var side = Shell2Ui.Side; var tb = Shell2Ui.ToolBar;
        if (!zen)
        {
            zenSaved = (side?.Visible ?? true, tb?.Visible ?? true, Bars.BottomVisible);
            if (side != null) side.Visible = false; if (tb != null) tb.Visible = false;
            zen = true; App.Toast("패널을 숨겼습니다 (Tab 또는 네 손가락 탭으로 다시)");
        }
        else
        {
            var s = zenSaved ?? (true, true, true);
            if (side != null) side.Visible = s.side; if (tb != null) tb.Visible = s.tools;
            zen = false;
        }
    }
    public static bool ZenOn => zen;
    public static void ToggleSide() { var s = Shell2Ui.Side; if (s == null) return; s.Visible = !s.Visible; App.Set("sideVisible", s.Visible); }
    public static void ShowSide() { var s = Shell2Ui.Side; if (s == null) return; s.Visible = true; App.Set("sideVisible", true); }

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
            CheckBox Chk(string label, bool on, Action<bool> set)
            {
                var c = new CheckBox { Text = label, Checked = on, AutoSize = true, ForeColor = Opt.Fg, Margin = new Padding(0, 1, 0, 1) };
                c.CheckedChanged += (_, _) => { set(c.Checked); Refresh(); };
                return c;
            }
            if (tab == "tools")
            {
                content.Controls.Add(Wide(Opt.Help("간단히 모드에서 왼쪽 도구 막대에 꺼내 둘 도구. 나머지는 도구 막대의 \"…\" 버튼에 있습니다.")));
                var pick = u["tools"] is JsonArray a ? Strs(a) : SimpleTools.ToHashSet();
                foreach (var t in Registry.Tools)
                {
                    string wid = KeyMap.WebTool(t.Id);
                    content.Controls.Add(Chk($"{t.Icon}  {t.Name}", pick.Contains(wid), v => { if (v) pick.Add(wid); else pick.Remove(wid); u["tools"] = new JsonArray(Registry.Tools.Select(x => KeyMap.WebTool(x.Id)).Where(pick.Contains).Select(x => (JsonNode)x).ToArray()); }));
                }
            }
            else if (tab == "menus")
            {
                content.Controls.Add(Wide(Opt.Help("메뉴 막대에 보일 메뉴와, 간단히 모드에서 각 메뉴에 꺼내 둘 항목. 숨긴 항목은 메뉴 맨 아래 \"모든 항목 보기\"로 언제든 볼 수 있습니다.")));
                var tops = Registry.Menus.Select(m => m.Path.Split('/')[0]).Distinct().ToList();
                foreach (var top in tops)
                {
                    string id = WebIds.MenuId(top);
                    content.Controls.Add(Chk("■ " + top + " 메뉴 보이기", MenuShown(top), v => Shell2Ui.Obj(u, "menus")[id] = v));
                    var keys = Registry.Menus.Where(m => m.Path.StartsWith(top + "/") && m.Run != null && !m.Path.Split('/')[^1].StartsWith("-")).OrderBy(m => m.Order).Select(m => m.Path).ToList();
                    var cur = Pick(top) is HashSet<string> p ? keys.Where(k => KeyMatches(p, k)).Select(ItemKey).ToHashSet() : keys.Select(ItemKey).ToHashSet();
                    foreach (var k in keys)
                    {
                        string key = ItemKey(k);
                        var c = Chk("      " + k[(top.Length + 1)..].Replace("/", " › "), cur.Contains(key), v => { if (v) cur.Add(key); else cur.Remove(key); Shell2Ui.Obj(u, "menuPick")[id] = new JsonArray(keys.Select(ItemKey).Where(cur.Contains).Distinct().Select(x => (JsonNode)x).ToArray()); });
                        content.Controls.Add(c);
                    }
                }
            }
            else if (tab == "panels")
            {
                content.Controls.Add(Wide(Opt.Help("오른쪽 패널에 보일 탭. (보기 > 패널을 앱 밖 창으로 에서 따로 꺼낼 수도 있습니다)")));
                foreach (var title in AllPanelTitles())
                    content.Controls.Add(Chk(title, PanelShown(title), v =>
                    {
                        var set = Strs(u["hiddenPanels"]); string pid = PanelId(title);
                        if (v) set.Remove(pid); else set.Add(pid);
                        u["hiddenPanels"] = new JsonArray(set.Select(x => (JsonNode)x).ToArray());
                    }));
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
