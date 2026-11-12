// 키 바꾸기 (웹 판 setuppresets.js applyToolKeys + 설정 keys): 메인 창 파일을 고치지 않고 메시지 필터(IMessageFilter)로.
//  - settings.toolKeys = { 도구id: 'k' | ['k','l'] | '' } — 같은 키를 여러 도구에 주면 누를 때마다 차례로 (설정 순서대로)
//  - settings.keys = { 명령: 'ctrl+shift+e' } — 명령은 메뉴 경로("보기/100%") 또는 웹 판 명령 id("view.actual", 아는 것만)
//  - 이 모듈이 등록한 메뉴 중 Windows 메뉴 단축키로 쓸 수 없는 키(PageUp·Shift+Y 등)도 여기서 실행
using System.Text.Json.Nodes;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public sealed class KeyMap : IMessageFilter
{
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    public static readonly KeyMap Instance = new();
    /// <summary>이 모듈의 "메뉴 단축키로는 안 되는" 키 → 명령 경로</summary>
    public static readonly Dictionary<Keys, string> ModuleKeys = new();

    // 웹 판 도구 id → 네이티브 도구 id (다른 것만)
    public static readonly Dictionary<string, string> ToolAlias = new() { ["view"] = "hand" };
    // 웹 판 명령 id → 네이티브 메뉴 경로 (프리셋·.cpset 의 keys 를 그대로 쓸 수 있게)
    public static readonly Dictionary<string, string> CmdAlias = new()
    {
        ["view.actual"] = "보기/100%", ["view.fit"] = "보기/화면에 맞추기", ["view.zoomIn"] = "보기/확대", ["view.zoomOut"] = "보기/축소", ["view.flip"] = "보기/좌우 반전",
        ["file.export"] = "파일/그림으로 내보내기 (PNG·JPG)…", ["file.save"] = "파일/저장", ["file.saveAs"] = "파일/다른 이름으로 저장…", ["file.open"] = "파일/열기…", ["file.new"] = "파일/새로 만들기…",
        ["edit.undo"] = "편집/실행취소", ["edit.redo"] = "편집/다시실행", ["layer.new"] = "레이어/새 레이어", ["layer.duplicate"] = "레이어/복제", ["layer.mergeDown"] = "레이어/아래로 병합",
        ["layer.newFolder"] = "레이어/새 폴더", ["layer.clip"] = "레이어/클리핑", ["layer.flatten"] = "레이어/보이는 레이어 결합", ["select.all"] = "선택/전체 선택", ["select.none"] = "선택/선택 해제", ["select.invert"] = "선택/선택 반전",
        ["story.next"] = "만화/다음 페이지", ["story.prev"] = "만화/이전 페이지", ["view.luma"] = "보기/명도 확인 보기 (끔 → 흑백 → 5단계)",
    };

    public static string ResolveCmd(string id) => Registry.Commands.ContainsKey(id) ? id : CmdAlias.TryGetValue(id, out var p) && Registry.Commands.ContainsKey(p) ? p : null;
    public static string NativeTool(string webId) => ToolAlias.TryGetValue(webId, out var n) ? n : webId;
    public static string WebTool(string nativeId) => ToolAlias.FirstOrDefault(kv => kv.Value == nativeId).Key ?? nativeId;

    // ---- 키 글자 ↔ Keys ("ctrl+shift+e", "pagedown", "b") ------------------------------------
    public static Keys Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Keys.None;
        Keys k = Keys.None;
        foreach (var raw in s.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw)
            {
                case "ctrl": case "control": case "cmd": case "meta": k |= Keys.Control; break;
                case "shift": k |= Keys.Shift; break;
                case "alt": k |= Keys.Alt; break;
                default: var code = Code(raw); if (code == Keys.None) return Keys.None; k |= code; break;
            }
        }
        return (k & Keys.KeyCode) == Keys.None ? Keys.None : k;
    }
    static Keys Code(string n)
    {
        if (n.Length == 1)
        {
            char c = n[0];
            if (c >= 'a' && c <= 'z') return Keys.A + (c - 'a');
            if (c >= '0' && c <= '9') return Keys.D0 + (c - '0');
            return c switch { '[' => Keys.OemOpenBrackets, ']' => Keys.OemCloseBrackets, ',' => Keys.Oemcomma, '.' => Keys.OemPeriod, '/' => Keys.OemQuestion, ';' => Keys.OemSemicolon, '\'' => Keys.OemQuotes, '-' => Keys.OemMinus, '=' => Keys.Oemplus, '`' => Keys.Oemtilde, '\\' => Keys.OemPipe, _ => Keys.None };
        }
        if (n[0] == 'f' && int.TryParse(n[1..], out int fn) && fn >= 1 && fn <= 24) return Keys.F1 + (fn - 1);
        return n switch
        {
            "pageup" => Keys.PageUp, "pagedown" => Keys.PageDown, "home" => Keys.Home, "end" => Keys.End, "delete" or "del" => Keys.Delete, "backspace" => Keys.Back,
            "insert" => Keys.Insert, "enter" => Keys.Enter, "escape" or "esc" => Keys.Escape, "tab" => Keys.Tab, "space" => Keys.Space,
            "arrowup" or "up" => Keys.Up, "arrowdown" or "down" => Keys.Down, "arrowleft" or "left" => Keys.Left, "arrowright" or "right" => Keys.Right,
            _ => Keys.None,
        };
    }
    public static string Format(Keys k)
    {
        if (k == Keys.None) return "";
        var parts = new List<string>();
        if ((k & Keys.Control) != 0) parts.Add("ctrl");
        if ((k & Keys.Alt) != 0) parts.Add("alt");
        if ((k & Keys.Shift) != 0) parts.Add("shift");
        var c = k & Keys.KeyCode;
        string n = c switch
        {
            >= Keys.A and <= Keys.Z => ((char)('a' + (c - Keys.A))).ToString(),
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (c - Keys.D0))).ToString(),
            >= Keys.F1 and <= Keys.F24 => "f" + (c - Keys.F1 + 1),
            Keys.OemOpenBrackets => "[", Keys.OemCloseBrackets => "]", Keys.Oemcomma => ",", Keys.OemPeriod => ".", Keys.OemQuestion => "/", Keys.OemSemicolon => ";",
            Keys.OemQuotes => "'", Keys.OemMinus => "-", Keys.Oemplus => "=", Keys.Oemtilde => "`", Keys.OemPipe => "\\",
            Keys.PageUp => "pageup", Keys.PageDown => "pagedown", Keys.Home => "home", Keys.End => "end", Keys.Delete => "delete", Keys.Back => "backspace",
            Keys.Insert => "insert", Keys.Enter => "enter", Keys.Escape => "escape", Keys.Tab => "tab", Keys.Space => "space",
            Keys.Up => "arrowup", Keys.Down => "arrowdown", Keys.Left => "arrowleft", Keys.Right => "arrowright",
            _ => null,
        };
        if (n == null) return "";
        parts.Add(n);
        return string.Join("+", parts);
    }

    /// <summary>도구 키 목록 (설정 값: 'b' | ['b','p'] | '')</summary>
    public static List<Keys> ToolKeysOf(JsonNode v)
    {
        var r = new List<Keys>();
        if (v is JsonArray a) { foreach (var x in a) { var k = Parse((string)x); if (k != Keys.None) r.Add(k); } }
        else if (v is JsonValue jv && jv.TryGetValue(out string s)) { var k = Parse(s); if (k != Keys.None) r.Add(k); }
        return r;
    }

    /// <summary>키 → 도구 순서 (설정 순서대로, 설정에 없는 도구는 기본 키)</summary>
    public static List<string> ToolsForKey(JsonObject toolKeys, Keys key)
    {
        var list = new List<string>();
        var ids = new HashSet<string>(Registry.Tools.Select(t => t.Id));
        var mapped = new HashSet<string>();
        if (toolKeys != null)
            foreach (var (webId, v) in toolKeys)
            {
                string id = NativeTool(webId);
                mapped.Add(id);
                if (ids.Contains(id) && ToolKeysOf(v).Contains(key) && !list.Contains(id)) list.Add(id);
            }
        foreach (var t in Registry.Tools) if (!mapped.Contains(t.Id) && t.Key == key && !list.Contains(t.Id)) list.Add(t.Id);
        return list;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_KEYDOWN && m.Msg != WM_SYSKEYDOWN) return false;
        var main = App.Main;
        if (main == null || Form.ActiveForm != main) return false;
        var target = Control.FromChildHandle(m.HWnd);
        if (target == null || target.FindForm() != main) return false;
        if (target is TextBoxBase || target is NumericUpDown || target is ComboBox || target is UpDownBase || target.GetType().Name.Contains("UpDownEdit")) return false;
        Keys code = (Keys)((int)m.WParam & 0xFFFF);
        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu) return false;
        Keys keyData = code | Control.ModifierKeys;
        bool repeat = ((long)m.LParam & (1L << 30)) != 0;
        try { return Handle(keyData, repeat); }
        catch (Exception ex) { App.Toast("키: " + ex.Message); return true; }
    }

    /// <summary>키 처리 (시험에서도 부름). 처리했으면 true</summary>
    public static bool Handle(Keys keyData, bool repeat = false)
    {
        // 1) 사용자가 바꾼 명령 단축키
        if (App.Settings["keys"] is JsonObject keys && keys.Count > 0)
            foreach (var (id, v) in keys)
            {
                if (Parse(v is JsonValue jv && jv.TryGetValue(out string s) ? s : null) != keyData) continue;
                string path = ResolveCmd(id);
                if (path == null || !Registry.Commands.TryGetValue(path, out var run) || run == null) continue;
                var def = Registry.Menus.Find(x => x.Path == path);
                if (def?.Enabled != null && !def.Enabled()) return true;
                MainForm.Run(run, path);
                return true;
            }
        // 2) 이 모듈의 특수 키 (PageUp/PageDown, Shift+Y …)
        if (ModuleKeys.TryGetValue(keyData, out var mp) && Registry.Commands.TryGetValue(mp, out var mr) && mr != null)
        {
            if (repeat && !mp.Contains("페이지")) return true;
            var def = Registry.Menus.Find(x => x.Path == mp);
            if (def?.Enabled == null || def.Enabled()) MainForm.Run(mr, mp);
            return true;
        }
        // 3) 도구 키 (설정에 toolKeys 가 있을 때만 — 없으면 메인 창 기본 처리)
        if ((keyData & Keys.Modifiers) != Keys.None) return false;
        if (App.Settings["toolKeys"] is not JsonObject tk || tk.Count == 0) return false;
        bool defaultOwner = Registry.Tools.Any(t => t.Key == keyData);
        var list = ToolsForKey(tk, keyData);
        if (list.Count == 0 && !defaultOwner) return false;
        if (App.Tool?.HandleKey(keyData) == true) return true;
        if (repeat || list.Count == 0) return true;      // 기본 키를 다른 도구로 옮긴 경우: 원래 도구로 가지 않게 먹음
        int i = list.IndexOf(App.Tool?.Id);
        App.SetTool(list[(i + 1) % list.Count]);
        return true;
    }

    // ---- 단축키 바꾸기 창 ----------------------------------------------------------------
    public static void Dialog()
    {
        var root = new Panel { BackColor = Opt.Bg };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var toolKeys = (App.Settings["toolKeys"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        var cmdKeys = (App.Settings["keys"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();

        // 도구 키
        var tp = new TabPage("도구 키") { BackColor = Opt.Bg };
        var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(6) };
        tl.Controls.Add(new Label { Text = "도구", AutoSize = true, ForeColor = Opt.Accent }); tl.Controls.Add(new Label { Text = "키 (쉼표로 여러 개, 비우면 없음)", AutoSize = true, ForeColor = Opt.Accent }); tl.Controls.Add(new Label());
        foreach (var t in Registry.Tools)
        {
            string web = WebTool(t.Id);
            var node = toolKeys.ContainsKey(web) ? toolKeys[web] : toolKeys.ContainsKey(t.Id) ? toolKeys[t.Id] : null;
            bool has = toolKeys.ContainsKey(web) || toolKeys.ContainsKey(t.Id);
            string cur = has ? string.Join(", ", ToolKeysOf(node).Select(Format)) : Format(t.Key);
            var box = new TextBox { Text = cur, Width = 160, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
            string def = Format(t.Key);
            box.Leave += (_, _) =>
            {
                var ks = box.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Format(Parse(x))).Where(x => x.Length > 0).ToList();
                toolKeys.Remove(t.Id);
                if (string.Join(",", ks) == def) toolKeys.Remove(web);
                else toolKeys[web] = ks.Count == 0 ? "" : ks.Count == 1 ? ks[0] : new JsonArray(ks.Select(x => (JsonNode)x).ToArray());
                box.Text = string.Join(", ", ks);
            };
            tl.Controls.Add(new Label { Text = t.Name, AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
            tl.Controls.Add(box);
            tl.Controls.Add(new Label { Text = def.Length > 0 ? $"기본 {def}" : "", AutoSize = true, ForeColor = Opt.Muted, Margin = new Padding(3, 6, 3, 3) });
        }
        tp.Controls.Add(tl);

        // 명령 단축키
        var cp = new TabPage("명령 단축키") { BackColor = Opt.Bg };
        var search = new TextBox { Dock = DockStyle.Top, BackColor = Opt.Bg2, ForeColor = Opt.Fg, PlaceholderText = "찾기…" };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Opt.Bg, ForeColor = Opt.Fg, HideSelection = false };
        list.Columns.Add("명령", 330); list.Columns.Add("기본", 110); list.Columns.Add("내 키", 110);
        var hint = new Label { Dock = DockStyle.Bottom, Height = 40, ForeColor = Opt.Muted, Text = "줄을 고른 뒤 새 키 조합을 누르세요. Backspace = 내 키 지우기 (기본으로), Delete = 이 명령 키 없음." };
        var menus = Registry.Menus.Where(x => x.Run != null && !x.Path.Split('/').Last().StartsWith("-")).OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
        string Mine(string path)
        {
            foreach (var (id, v) in cmdKeys) if (ResolveCmd(id) == path || id == path) return v is JsonValue jv && jv.TryGetValue(out string s) ? (s.Length == 0 ? "(없음)" : s) : "";
            return "";
        }
        void Fill()
        {
            list.BeginUpdate(); list.Items.Clear();
            string q = search.Text.Trim();
            foreach (var mm in menus)
            {
                if (q.Length > 0 && !mm.Path.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                var it = new ListViewItem(new[] { mm.Path.Replace("/", " › "), Format(mm.Shortcut), Mine(mm.Path) }) { Tag = mm.Path };
                list.Items.Add(it);
            }
            list.EndUpdate();
        }
        search.TextChanged += (_, _) => Fill();
        list.KeyDown += (_, e) =>
        {
            if (list.SelectedItems.Count == 0) return;
            string path = (string)list.SelectedItems[0].Tag;
            if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.Up or Keys.Down) return;
            foreach (var id in cmdKeys.Where(kv => ResolveCmd(kv.Key) == path || kv.Key == path).Select(kv => kv.Key).ToList()) cmdKeys.Remove(id);
            if (e.KeyData == Keys.Back) { }
            else if (e.KeyData == Keys.Delete) cmdKeys[path] = "";
            else { string k = Format(e.KeyData); if (k.Length > 0) cmdKeys[path] = k; }
            list.SelectedItems[0].SubItems[2].Text = Mine(path);
            e.Handled = true; e.SuppressKeyPress = true;
        };
        cp.Controls.Add(list); cp.Controls.Add(search); cp.Controls.Add(hint);
        tabs.TabPages.Add(tp); tabs.TabPages.Add(cp);
        root.Controls.Add(tabs);
        var bar = Opt.Row(Opt.Button("저장", () =>
        {
            App.Settings["toolKeys"] = toolKeys.DeepClone(); App.Settings["keys"] = cmdKeys.DeepClone();
            App.NotifySettings(); App.Toast("단축키를 저장했습니다");
            root.FindForm()?.Close();
        }, true), Opt.Button("모두 기본으로", () => { toolKeys.Clear(); cmdKeys.Clear(); root.FindForm()?.Close(); App.Settings["toolKeys"] = new JsonObject(); App.Settings["keys"] = new JsonObject(); App.NotifySettings(); App.Toast("단축키를 기본으로 되돌렸습니다"); }));
        bar.Dock = DockStyle.Bottom;
        root.Controls.Add(bar);
        Fill();
        using var f = Dlg.Modal("단축키·도구 키 바꾸기", root, 640, 640);
        f.ShowDialog(App.Main);
    }
}
