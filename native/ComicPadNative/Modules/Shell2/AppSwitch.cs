// 웹 판 ↔ 네이티브 판(베타) 오가기 + 웹 판 설정 가져오기.
//  - 스위치: 설치 폴더 data\config\app.json 의 {"app":"native"|"web"}. 창 프로그램(ComicPad.exe, launcher\AppSwitch.cs)이 켤 때 읽음
//    (웹 판 도움말 > 새 네이티브 판 써 보기 가 "native" 로, 여기 도움말 > 웹 판으로 돌아가기 가 "web" 으로 바꿈. 켤 때 Shift = 이번만 웹 판)
//  - 설정 가져오기: 웹 판 설정은 data\config\settings.json (서버가 저장, WebView 의 localStorage 는 그 사본일 뿐).
//    처음 켤 때 한 번 저절로, 또는 도움말 > 웹 판 설정 가져오기… 로. 키 이름이 같아 값을 그대로 옮긴다 (아래 Groups 표).
//    네이티브에서 바꾼 값은 덮어쓰지 않음: 가져온 값의 지문(webImport.keys)을 남겨, 지금 값이 그 지문과 다르면 "직접 바꾼 것"으로 보고 둠
//    (가져오기 창에서 "덮어쓰기"를 고르면 바꿈, 세팅 되돌리기로 가져오기 전으로 되돌릴 수 있음)
//  - 시험 (창 없이): --selftest switch 결과폴더 → AppSwitchTest
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class AppSwitch
{
    public const string BackMenu = "도움말/웹 판으로 돌아가기…";
    public const string ImportMenu = "도움말/웹 판 설정 가져오기…";

    /// <summary>설치 폴더 (ComicPad.exe + server.py + native\ComicPadNative\ComicPadNative.exe 가 있는 곳, 실행 파일 폴더에서 위로 찾음). 없으면 null (개발 폴더의 bin 에서 켠 것 등)</summary>
    public static string FindInstall(string start = null)
    {
        var d = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ComicPad.exe")) && File.Exists(Path.Combine(d.FullName, "server.py"))
                && File.Exists(Path.Combine(d.FullName, "native", "ComicPadNative", "ComicPadNative.exe"))) return d.FullName;
        return null;
    }

    public static string SwitchPath(string dataDir = null) => Path.Combine(dataDir ?? App.DataDir, "config", "app.json");

    public static string Read(string dataDir = null)
    {
        try { return JsonNode.Parse(File.ReadAllText(SwitchPath(dataDir)))?["app"]?.GetValue<string>() is "native" ? "native" : "web"; }
        catch { return "web"; }
    }

    public static void Write(string which, string dataDir = null)
    {
        string p = SwitchPath(dataDir), tmp = p + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(p));
        File.WriteAllText(tmp, new JsonObject { ["app"] = which, ["by"] = "native", ["at"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss") }.ToJsonString(), new UTF8Encoding(false));
        File.Move(tmp, p, true);
    }

    /// <summary>도움말 > 웹 판으로 돌아가기: 스위치를 web 으로, 창 프로그램(ComicPad.exe)을 웹 판으로 켜고 이 창을 닫음</summary>
    public static void BackToWeb()
    {
        string root = FindInstall();
        if (root == null) { Dlg.Confirm("웹 판(ComicPad.exe)을 찾지 못했습니다.\n설치 폴더(native\\ComicPadNative)에서 켠 네이티브 판만 웹 판으로 돌아갈 수 있습니다.", "확인"); return; }
        if (!Dlg.Confirm("웹 판으로 돌아갈까요?\n\n이 창을 닫고 웹 판(ComicPad 창)을 엽니다. 다음부터 ComicPad 아이콘도 웹 판으로 열립니다.\n작품(data 폴더)은 그대로이고, 웹 판 도움말 > 새 네이티브 판 써 보기 로 언제든 다시 올 수 있습니다.", "웹 판으로 돌아가기")) return;
        if (!FileCommands.ConfirmDiscard()) return;
        try { Write("web"); }
        catch (Exception e) { App.Toast("스위치를 바꾸지 못했습니다 (이번만 웹 판으로 엽니다): " + e.Message); }
        App.SaveSettings();
        try { Process.Start(new ProcessStartInfo(Path.Combine(root, "ComicPad.exe"), "--web") { UseShellExecute = false, WorkingDirectory = root }); }
        catch (Exception e) { App.Toast("웹 판을 켜지 못했습니다: " + e.Message); return; }
        if (App.Doc != null) App.Doc.Modified = false;   // 저장·버리기는 위에서 정함
        App.Main?.Close();
    }

    /// <summary>처음 켰을 때 한 번: 베타 안내 + 가져온 설정</summary>
    public static void Welcome()
    {
        if (App.Settings["nativeWelcome"] != null) return;
        if (FindInstall() == null && WebImport.LastAuto == null) return;   // 개발 폴더에서 켠 것
        App.Settings["nativeWelcome"] = DateTime.Now.ToString("yyyy-MM-dd");
        App.SaveSettings();
        var r = WebImport.LastAuto;
        string got = r != null && r.Imported.Count > 0 ? $" 웹 판 설정 {r.Imported.Count}가지를 가져왔습니다." : "";
        Shell2Ui.Notify("ComicPad 네이티브 판 (베타)입니다." + got, "안내", WelcomeDialog);
    }

    public static void WelcomeDialog()
    {
        var r = WebImport.LastAuto;
        var fields = new List<Field>
        {
            Field.Info("네이티브 판(베타)은 웹 화면 없이 Windows 에서 바로 도는 ComicPad 입니다. 작품(data 폴더)·.cpad 파일은 웹 판과 같이 씁니다."),
            Field.Info(r == null ? "웹 판 설정은 도움말 > 웹 판 설정 가져오기 로 가져올 수 있습니다."
                : r.Imported.Count == 0 ? "웹 판에서 가져올 설정이 없었습니다 (이미 같거나 웹 판을 쓴 적이 없음)."
                : "가져온 설정: " + string.Join(", ", WebImport.GroupNames(r.Imported)) + (r.Kept.Count > 0 ? $"\n(네이티브 판에서 이미 바꾼 {r.Kept.Count}가지는 그대로 둠)" : "")),
            Field.Info("웹 판으로 돌아가려면: 도움말 > 웹 판으로 돌아가기. ComicPad 를 켤 때 Shift 를 누르고 있으면 이번만 웹 판으로 열립니다."),
            Field.Info("자동 저장은 \"이름 (네이티브).cpad\" 로 따로 남고, 백업·버전 기록은 웹 판과 같은 곳에 쌓입니다."),
        };
        var v = Dlg.Form("네이티브 판 (베타)", fields, "확인", new[] { ("웹 판으로 돌아가기…", "back") }, 520);
        if ((string)v?["__action"] == "back") BackToWeb();
    }
}

/// <summary>웹 판 설정(data\config\settings.json) → 네이티브 설정(native-settings.json)</summary>
public static class WebImport
{
    /// <summary>옮기는 설정 (키 이름은 두 판이 같음). 그룹 이름은 알림·가져오기 창에 보임</summary>
    public static readonly (string name, string[] keys)[] Groups =
    {
        ("펜·필기감", new[] { "pressureGamma", "pressurePts", "penFeel", "mousePressure", "penNoPressure", "penButton", "penButton2", "holdLine", "brushCursor",
                              "fingerDraw", "palmGuardMs", "touchRotate", "twoFingerUndo", "threeFingerRedo", "longPressEyedropper", "wheelMode", "mouseRadial" }),
        ("브러시", new[] { "brushes", "activePreset", "vectorErase" }),
        ("단축키", new[] { "keys", "toolKeys" }),
        ("화면 구성", new[] { "ui", "quickbar", "bottombar", "bottomMigrated", "radialItems", "largeUI", "leftHanded", "sideVisible", "dockSplit", "dock_top", "dock_bottom", "workspaces", "popoutOnTop" }),
        ("색·팔레트", new[] { "palette", "recentColors", "roughColor" }),
        ("보기·자·대칭", new[] { "showGrid", "gridSize", "symmetry", "symMirror", "symN", "rulerSnap", "pixelGrid", "showRulers", "showNotes", "showCamera", "hintGap" }),
        ("처음 세팅·새 작품", new[] { "setupDone", "startDoc", "lastNew", "panelTemplates" }),
        ("작업·내보내기 기본값", new[] { "autosaveMin", "animExport", "animScrub", "webtoonExport", "webtoonPreviewW", "sliceW", "sliceH", "printGuides", "printIntent", "printProfile",
                                    "bookRtl", "ebookRtl", "ebookAuthor", "userName", "scriptDraft", "macros", "lastAssetFolder", "last3D", "cafSrc", "cafOut" }),
        ("AI", new[] { "aiUrl", "aiPrompt", "aiNeg", "aiDen", "aiPick", "aiCpuOnly" }),
    };
    /// <summary>웹 판에만 뜻이 있어 옮기지 않는 설정 (웹 창 펜 입력 경로·웹 메모리 절약·웹 내장 AI 서버·웹 명령 기록·되돌리기 백업)</summary>
    public static readonly string[] NotMoved = { "nativePen", "penApi", "lowLatency", "memSave", "feelV", "aiFeatures", "aiIdleSec", "aiTimeoutSec", "recentCommands", "setupBackup" };

    public static IEnumerable<string> Keys => Groups.SelectMany(g => g.keys);
    public static IEnumerable<string> GroupNames(IEnumerable<string> keys) { var ks = keys.ToHashSet(); return Groups.Where(g => g.keys.Any(ks.Contains)).Select(g => g.name); }

    public sealed class Result
    {
        public List<string> Imported = new(), Kept = new(), Same = new();
        public string From;
    }
    /// <summary>이번에 켤 때 저절로 가져온 결과 (없으면 null)</summary>
    public static Result LastAuto;

    public static string WebSettingsPath(string dataDir = null) => Path.Combine(dataDir ?? App.DataDir, "config", "settings.json");
    static string ServerConfPath(string dataDir = null) => Path.Combine(dataDir ?? App.DataDir, "config", "server.json");

    public static JsonObject ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; } catch { return null; }
    }

    static string Print(JsonNode n) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(n?.ToJsonString() ?? "null")))[..16].ToLowerInvariant();

    /// <summary>웹 설정을 native 에 합친다 (native 를 바로 바꿈). overwrite=false 면 네이티브에서 직접 바꾼 값은 그대로</summary>
    public static Result Merge(JsonObject native, JsonObject web, bool overwrite, string from = null, JsonObject serverConf = null)
    {
        var r = new Result { From = from };
        var rec = native["webImport"] as JsonObject ?? new JsonObject();
        var prints = rec["keys"] as JsonObject ?? new JsonObject();
        foreach (var key in Keys)
        {
            if (web == null || !web.TryGetPropertyValue(key, out var wv) || wv == null) continue;
            var nv = native[key];
            if (nv == null) { native[key] = wv.DeepClone(); r.Imported.Add(key); }
            else if (U.Same(nv, wv)) { r.Same.Add(key); }
            else if (overwrite || (string)prints[key] == Print(nv)) { native[key] = wv.DeepClone(); r.Imported.Add(key); }
            else { r.Kept.Add(key); continue; }
            prints[key] = Print(wv);
        }
        // 서버 설정(server.json)의 자동 백업 켜기·보관 개수: 네이티브에 아직 없을 때만
        if (serverConf?["backup"] is JsonObject sb && native["backup"] == null)
        {
            var b = new JsonObject();
            if (sb["on"] != null) b["on"] = sb["on"].DeepClone();
            if (sb["keep"] != null) b["keep"] = sb["keep"].DeepClone();
            if (b.Count > 0) { native["backup"] = b; r.Imported.Add("backup"); }
        }
        rec["at"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss");
        rec["from"] = from;
        rec["keys"] = prints;
        native["webImport"] = rec;
        return r;
    }

    /// <summary>처음 켤 때 한 번 (모듈·화면을 만들기 전에): 웹 판 설정이 있으면 가져와서 바로 저장</summary>
    public static Result ImportOnStart()
    {
        try
        {
            if (App.Settings["webImport"] != null) return null;
            string path = WebSettingsPath();
            var web = ReadJson(path);
            var r = Merge(App.Settings, web, false, web != null ? path : null, ReadJson(ServerConfPath()));
            App.SaveSettings();
            return LastAuto = web != null ? r : null;
        }
        catch (Exception e) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "comicpad-native-error.log"), $"{DateTime.Now} 웹 판 설정 가져오기: {e}\r\n"); } catch { } return null; }
    }

    /// <summary>도움말 > 웹 판 설정 가져오기…</summary>
    public static void Dialog()
    {
        string path = WebSettingsPath();
        var web = ReadJson(path);
        if (web == null) { Dlg.Confirm($"웹 판 설정 파일이 없습니다.\n({path})\n\n웹 판을 이 설치 폴더에서 한 번이라도 썼다면 생깁니다.", "확인"); return; }
        // 미리 보기: 사본에 합쳐 봄
        var probe = Merge((JsonObject)App.Settings.DeepClone(), web, false, path, ReadJson(ServerConfPath()));
        var fields = new List<Field>
        {
            Field.Info($"웹 판 설정: {path}"),
            Field.Info(probe.Imported.Count > 0 ? "가져올 것: " + string.Join(", ", GroupNames(probe.Imported)) + $" ({probe.Imported.Count}가지)" : "새로 가져올 것이 없습니다 (같거나 네이티브에서 바꾼 것뿐)."),
            probe.Kept.Count > 0 ? Field.Info($"네이티브 판에서 직접 바꾼 {probe.Kept.Count}가지 ({string.Join(", ", GroupNames(probe.Kept))})는 그대로 둡니다.") : null,
            probe.Kept.Count > 0 ? Field.Check("overwrite", "네이티브에서 바꾼 것도 웹 판 값으로 덮어쓰기", false) : null,
            Field.Info("가져오기 전 상태는 백업되어 도움말 > 세팅 되돌리기 로 되돌릴 수 있습니다."),
        };
        var v = Dlg.Form("웹 판 설정 가져오기", fields, "가져오기", width: 520);
        if (v == null) return;
        bool over = (bool?)v["overwrite"] == true;
        var next = (JsonObject)App.Settings.DeepClone();
        var r = Merge(next, web, over, path, ReadJson(ServerConfPath()));
        var changed = Setup.Diff(App.Settings, next);
        if (r.Imported.Count == 0) { App.Settings["webImport"] = next["webImport"]?.DeepClone(); App.SaveSettingsSoon(); App.Toast("바뀐 설정이 없습니다"); return; }
        Setup.Commit(App.Settings, changed, next, "웹 판 설정 가져오기");
        if (r.Imported.Contains("setupDone")) App.Settings["setupDone"] = next["setupDone"]?.DeepClone();   // Diff 는 setupDone 을 세지 않음
        Setup.RefreshAfterSettings();
        UiMode.Refresh();
        App.Toast($"웹 판 설정을 가져왔습니다: {string.Join(", ", GroupNames(r.Imported))}");
    }
}

public sealed class AppSwitchModule : IModule
{
    public void Init()
    {
        Registry.Menu(AppSwitch.BackMenu, AppSwitch.BackToWeb, order: 94);
        Registry.Menu(AppSwitch.ImportMenu, WebImport.Dialog, order: 95);
        bool done = false;
        void FirstIdle(object s, EventArgs e)
        {
            if (done || App.Main == null || !App.Main.IsHandleCreated || !App.Main.Visible || App.Doc == null) return;
            done = true;
            Application.Idle -= FirstIdle;
            if (GuiTest.On || UI.Chrome.UiShot.Active) return;
            // (창 타이머는 그리기가 바쁘면 늦게 오므로 Task.Delay 로)
            App.Main.BeginInvoke(async () => { await Task.Delay(1500); AppSwitch.Welcome(); });
        }
        Application.Idle += FirstIdle;
    }
}
