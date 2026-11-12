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
        using var f = new Dialogs.ModalForm("설정", 500, 0);
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
        f.SetContent(p);
        f.AddAction("닫기", () => true, primary: true);
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

    // ---- 기능 검색 (Ctrl+K): 메뉴·도구 이름으로 찾아 실행 (웹 .palette-modal) -----------------------------------------
    public static void CommandPalette() => Dialogs.CommandPalette.Show();

    // ---- 최근 파일·자동 저장 (웹 자동 저장 복구 / 프로젝트 열기 카드) ----------------------------------------------
    public static void RecentDialog()
    {
        var files = (App.Settings["recentFiles"] as JsonArray)?.Select(x => (string)x).Where(File.Exists).ToList() ?? new();
        string auto = Path.Combine(App.DataDir, "autosave");
        if (Directory.Exists(auto)) files.AddRange(Directory.GetFiles(auto, "*.cpad").OrderByDescending(File.GetLastWriteTime).Take(8).Select(p => p));
        if (files.Count == 0) { App.Toast("최근 파일이 없습니다"); return; }
        using var f = new Dialogs.ModalForm("최근 작품 · 자동 저장", 640, 440) { Resizable = true };
        var list = new Controls.WebList { Bg = Theme.Panel, RowH = 38, Dock = DockStyle.Fill };
        list.Items = files.Select(p => (p.Contains(Path.DirectorySeparatorChar + "autosave" + Path.DirectorySeparatorChar) ? "[자동 저장] " : "") + Path.GetFileName(p)).ToList();
        list.Sub = files.Select(p => $"{File.GetLastWriteTime(p):yyyy-MM-dd HH:mm}").ToList();
        list.Selected = 0;
        f.SetContent(list);
        string pick = null;
        list.Picked += i => { pick = files[i]; f.DialogResult = DialogResult.OK; f.Close(); };
        f.AddAction("작업물 폴더 열기", () => { OpenFolder(App.DataDir); return false; });
        f.AddAction("취소", () => true);
        f.AddAction("열기", () => { if (list.Selected >= 0) pick = files[list.Selected]; return pick != null; }, primary: true);
        f.Shown += (_, _) => list.Focus();
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
        // 새 캔버스 (웹 newDocDialog: 크기 카드 격자)
        Registry.Menu("파일/새로 만들기…", () => Dialogs.NewDocDialog.Show(), Keys.Control | Keys.N, order: 10);
        App.History.MaxBytes = (long)(App.Get("undoMB", 1500.0) * 1_000_000);
        Shell.BackupIfDue();
        var t = new System.Windows.Forms.Timer { Interval = 3600_000 };
        t.Tick += (_, _) => Shell.BackupIfDue();
        t.Start();
        // 메인 창이 뜬 뒤 한 번 (화면 비교 찍기)
        void FirstIdle(object s, EventArgs e)
        {
            if (App.Main == null || !App.Main.Visible || App.Doc == null) return;
            Application.Idle -= FirstIdle;
            if (!string.IsNullOrEmpty(PanelShots.Dir)) { var pt = new System.Windows.Forms.Timer { Interval = 1500 }; pt.Tick += (_, _) => { pt.Stop(); pt.Dispose(); PanelShots.Run(); }; pt.Start(); return; }
            if (WantStartDialog()) StartDialogWhenFree();
        }
        Application.Idle += FirstIdle;
    }

    /// <summary>웹처럼 켤 때 "ComicPad 시작" 창 (최근 프로젝트 + 크기 카드). 파일을 열며 켰거나 시험·화면 찍기 중이면 안 띄움</summary>
    static bool WantStartDialog()
    {
        if (!App.Get("startDialog", true)) return false;
        if (Chrome.UiShot.Active) return false;
        if (Environment.GetCommandLineArgs().Skip(1).Any(File.Exists)) return false;
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string k = e.Key.ToString().ToUpperInvariant();
            if (k.StartsWith("COMICPAD_") && (k.Contains("TEST") || k.Contains("SHOT") || k.Contains("PANELS")) && !string.IsNullOrEmpty(e.Value?.ToString())) return false;
        }
        return App.Doc != null && App.Doc.FilePath == null && !App.Doc.Modified;
    }

    /// <summary>처음 세팅 창 같은 다른 창이 닫힌 뒤에 띄움</summary>
    static void StartDialogWhenFree()
    {
        var t = new System.Windows.Forms.Timer { Interval = 300 };
        int tries = 0;
        t.Tick += (_, _) =>
        {
            if (Application.OpenForms.Cast<Form>().Any(f => f.Modal && f.Visible) && ++tries < 600) return;
            t.Stop(); t.Dispose();
            if (App.Doc == null || App.Doc.FilePath != null || App.Doc.Modified || App.History.CanUndo) return;
            MainForm.Run(() => Dialogs.NewDocDialog.Show(startup: true), "ComicPad 시작");
        };
        t.Start();
    }
}
