// 앱 전체 기능: 설정 창, 기능 검색(Ctrl+K), 최근 파일·자동 저장 꺼내기, 작업물 자동 백업, 설명서.
using System.IO.Compression;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.UI;

public static class Shell
{
    // ---- 설정 창 ----------------------------------------------------------------------
    public static void SettingsDialog()
    {
        using var f = new Form { Text = "설정", Width = 420, Height = 640, StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, FormBorderStyle = FormBorderStyle.SizableToolWindow };
        var p = Opt.Panel(
            Opt.Title("펜·터치"),
            Opt.Choice("펜 측면 버튼 누르고 그리기", new[] { ("eyedropper", "스포이트"), ("eraser", "지우개"), ("pan", "화면 이동"), ("none", "아무것도 안 함") }, () => App.Get("penButton", "eyedropper"), v => App.Set("penButton", v)),
            Opt.Slider("마우스 필압", 0.1, 1, () => App.Get("mousePressure", 1.0), v => App.Set("mousePressure", v), pct: true),
            Opt.Slider("펜을 뗀 뒤 손바닥 무시 (ms)", 0, 1500, () => App.Get("palmGuardMs", 350.0), v => App.Set("palmGuardMs", Math.Round(v)), step: 10),
            Opt.Check("두 손가락으로 화면 돌리기", () => App.Get("touchRotate", true), v => App.Set("touchRotate", v)),
            Opt.Help("필압 곡선·손떨림 보정·브러시 커서는 펜·브러시 설정에 있습니다."),
            Opt.Title("저장"),
            Opt.Slider("자동 저장 간격 (분)", 1, 30, () => App.Get("autosaveMin", 3.0), v => App.Set("autosaveMin", Math.Round(v)), step: 1),
            Opt.Help($"작업물 폴더: {App.DataDir}"),
            Opt.Button("작업물 폴더 열기", () => OpenFolder(App.DataDir)),
            Opt.Title("자동 백업"),
            Opt.Check("하루 한 번 작품·에셋·글꼴·설정을 zip 으로", () => BackupConf("on", true), v => SetBackup("on", v)),
            Opt.Slider("보관 개수", 1, 60, () => BackupConf("keep", 14.0), v => SetBackup("keep", Math.Round(v)), step: 1),
            Opt.Row(Opt.Button("지금 백업", () => { var r = MakeBackup(); App.Toast(r != null ? "백업함: " + Path.GetFileName(r) : "백업하지 못했습니다"); }), Opt.Button("백업 폴더 열기", () => OpenFolder(BackupDir))),
            Opt.Title("실행취소"),
            Opt.Slider("기억할 메모리 (MB)", 200, 6000, () => App.History.MaxBytes / 1_000_000.0, v => { App.History.MaxBytes = (long)(v * 1_000_000); App.Set("undoMB", Math.Round(v)); }, step: 50));
        p.Dock = DockStyle.Fill; p.AutoScroll = true; p.WrapContents = false;
        f.Controls.Add(p);
        f.ShowDialog(App.Main);
        App.NotifySettings();
    }

    static T BackupConf<T>(string k, T fb) { try { var n = (App.Settings["backup"] as JsonObject)?[k]; return n == null ? fb : n.GetValue<T>(); } catch { return fb; } }
    static void SetBackup(string k, JsonNode v) { if (App.Settings["backup"] is not JsonObject b) { b = new JsonObject(); App.Settings["backup"] = b; } b[k] = v; App.SaveSettingsSoon(); }

    public static void OpenFolder(string path) { try { Directory.CreateDirectory(path); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }

    // ---- 자동 백업 (웹 판 server.py make_backup 과 같은 묶음·이름) ------------------------------
    public static string BackupDir => Path.Combine(App.DataDir, "backups");

    public static string MakeBackup()
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            string name = DateTime.Now.ToString("'ComicPad_백업_'yyyy-MM-dd_HHmmss'.zip'");
            string final = Path.Combine(BackupDir, name), tmp = final + ".part";
            using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var kind in new[] { "projects", "assets", "fonts" })
                {
                    string b = Path.Combine(App.DataDir, kind);
                    if (!Directory.Exists(b)) continue;
                    foreach (var full in Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories))
                    {
                        if (full.EndsWith(".tmp") || full.EndsWith(".part")) continue;
                        z.CreateEntryFromFile(full, kind + "/" + Path.GetRelativePath(b, full).Replace('\\', '/'), CompressionLevel.Optimal);
                    }
                }
                string cfg = Path.Combine(App.DataDir, "config");
                if (Directory.Exists(cfg))
                    foreach (var full in Directory.EnumerateFiles(cfg, "*.json")) if (Path.GetFileName(full) != "server.json") z.CreateEntryFromFile(full, "config/" + Path.GetFileName(full));
            }
            File.Move(tmp, final, true);
            int keep = (int)BackupConf("keep", 14.0);
            foreach (var old in Directory.GetFiles(BackupDir, "ComicPad_백업_*.zip").OrderByDescending(x => x).Skip(keep)) File.Delete(old);
            SetBackup("lastAt", DateTimeOffset.Now.ToUnixTimeSeconds());
            return final;
        }
        catch { return null; }
    }

    /// <summary>켤 때·한 시간마다: 마지막 백업이 하루 넘었으면 뒤에서 백업</summary>
    public static void BackupIfDue()
    {
        if (!BackupConf("on", true)) return;
        long last = BackupConf("lastAt", 0L);
        if (DateTimeOffset.Now.ToUnixTimeSeconds() - last < 24 * 3600) return;
        Task.Run(MakeBackup);
    }

    // ---- 기능 검색 (Ctrl+K): 메뉴·도구 이름으로 찾아 실행 -----------------------------------------
    public static void CommandPalette()
    {
        using var f = new Form { Text = "기능 검색", Width = 520, Height = 460, StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, FormBorderStyle = FormBorderStyle.SizableToolWindow, KeyPreview = true };
        var q = new TextBox { Dock = DockStyle.Top, BackColor = Opt.Bg2, ForeColor = Opt.Fg, Font = new Font("Malgun Gothic", 12f) };
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Opt.Bg, ForeColor = Opt.Fg, BorderStyle = BorderStyle.None, Font = new Font("Malgun Gothic", 10.5f) };
        var all = Registry.Menus.Where(m => m.Run != null && !m.Path.EndsWith("-") && !m.Path.Split('/').Last().StartsWith("-"))
            .Select(m => (label: m.Path.Replace("/", " › ").TrimEnd('…'), run: m.Run, key: m.Shortcut))
            .Concat(Registry.Tools.Select(t => (label: "도구 › " + t.Name, run: (Action)(() => App.SetTool(t.Id)), key: t.Key))).ToList();
        List<(string label, Action run, Keys key)> shown = all;
        void Fill()
        {
            string s = q.Text.Trim();
            shown = s.Length == 0 ? all : all.Where(x => Fuzzy(x.label, s)).ToList();
            list.BeginUpdate(); list.Items.Clear();
            foreach (var x in shown) list.Items.Add(x.label + (x.key != Keys.None ? "   (" + new KeysConverter().ConvertToString(x.key) + ")" : ""));
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            list.EndUpdate();
        }
        Action chosen = null;
        void Go() { if (list.SelectedIndex >= 0) { chosen = shown[list.SelectedIndex].run; f.Close(); } }
        q.TextChanged += (_, _) => Fill();
        f.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Go(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) f.Close();
        };
        list.DoubleClick += (_, _) => Go();
        f.Controls.Add(list); f.Controls.Add(q);
        Fill();
        f.Shown += (_, _) => q.Focus();
        f.ShowDialog(App.Main);
        if (chosen != null) MainForm.Run(chosen, "기능 검색");
    }

    /// <summary>글자들이 순서대로 들어 있으면 (띄어쓰기 무시)</summary>
    static bool Fuzzy(string text, string q)
    {
        text = text.Replace(" ", "").ToLowerInvariant(); q = q.Replace(" ", "").ToLowerInvariant();
        if (text.Contains(q)) return true;
        int i = 0; foreach (char c in text) if (i < q.Length && c == q[i]) i++;
        return i == q.Length;
    }

    // ---- 최근 파일·자동 저장 ----------------------------------------------------------------
    public static void RecentDialog()
    {
        var files = (App.Settings["recentFiles"] as JsonArray)?.Select(x => (string)x).Where(File.Exists).ToList() ?? new();
        string auto = Path.Combine(App.DataDir, "autosave");
        if (Directory.Exists(auto)) files.AddRange(Directory.GetFiles(auto, "*.cpad").OrderByDescending(File.GetLastWriteTime).Take(8).Select(p => p));
        if (files.Count == 0) { App.Toast("최근 파일이 없습니다"); return; }
        using var f = new Form { Text = "최근 작품 · 자동 저장", Width = 640, Height = 420, StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, FormBorderStyle = FormBorderStyle.SizableToolWindow };
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Opt.Bg, ForeColor = Opt.Fg, Font = new Font("Malgun Gothic", 10f) };
        foreach (var p in files) list.Items.Add((p.Contains(Path.DirectorySeparatorChar + "autosave" + Path.DirectorySeparatorChar) ? "[자동 저장] " : "") + $"{Path.GetFileName(p)}   —   {File.GetLastWriteTime(p):yyyy-MM-dd HH:mm}   {Path.GetDirectoryName(p)}");
        string pick = null;
        list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) { pick = files[list.SelectedIndex]; f.Close(); } };
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && list.SelectedIndex >= 0) { pick = files[list.SelectedIndex]; f.Close(); } };
        f.Controls.Add(list);
        f.ShowDialog(App.Main);
        if (pick != null && FileCommands.ConfirmDiscard()) FileCommands.OpenPath(pick);
    }

    // ---- 설명서 -------------------------------------------------------------------------
    public static void OpenManual()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && d != null; i++, d = d.Parent)
        {
            string m = Path.Combine(d.FullName, "app", "manual.html");
            if (File.Exists(m)) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(m) { UseShellExecute = true }); return; }
        }
        App.Toast("설명서(manual.html)를 찾지 못했습니다");
    }
}

public sealed class ShellModule : IModule
{
    public void Init()
    {
        Registry.Menu("파일/최근 작품·자동 저장…", Shell.RecentDialog, Keys.Control | Keys.Shift | Keys.O, order: 25);
        Registry.Menu("파일/설정…", Shell.SettingsDialog, Keys.Control | Keys.Oemcomma, order: 80);
        Registry.Menu("파일/작업물 백업 (지금)", () => { var r = Shell.MakeBackup(); App.Toast(r != null ? "백업함: " + Path.GetFileName(r) : "백업하지 못했습니다"); }, order: 85);
        Registry.Menu("도움말/기능 검색…", Shell.CommandPalette, Keys.Control | Keys.K, order: 10);
        Registry.Menu("도움말/ComicPad 설명서", Shell.OpenManual, Keys.F1, order: 20);
        App.History.MaxBytes = (long)(App.Get("undoMB", 1500.0) * 1_000_000);
        Shell.BackupIfDue();
        var t = new System.Windows.Forms.Timer { Interval = 3600_000 };
        t.Tick += (_, _) => Shell.BackupIfDue();
        t.Start();
    }
}
