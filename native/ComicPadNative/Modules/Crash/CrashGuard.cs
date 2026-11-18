// 시작 때 꺼짐 반복 막기 (crash loop guard).
//  시작할 때 "시작 중" 표시를 남기고, 창이 뜬 뒤 3초가 지나면(또는 정상으로 닫으면) 지움. 다음에 켤 때 표시가 남아 있으면
//  "창이 뜨기 전에 꺼진 것" 으로 셈. 최근 두 번 연달아 그랬으면 안전 모드로 시작:
//   - GPU 끔 (설정 gpu=false + 환경 변수 COMICPAD_NO_GPU=1), 기본 화면 방식 (ui·largeUI 를 기본값으로)
//   - 시작하다 실패한(또는 시작 중 그 안에서 꺼진) 부가 모듈은 건너뜀 (기본 모듈은 늘 켬)
//  안전 모드에서 바꾼 설정은 원래 값을 기록해 두었다가, 다음 보통 시작 때 되돌림 (그동안 사용자가 직접 바꿨으면 그대로).
//  상태 파일: 설정 파일 옆 native-settings.startup.json (COMICPAD_SETTINGS 를 주면 그 옆)
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ComicPadNative.Modules.Crash;

public static class CrashGuard
{
    /// <summary>창 모드에서 시작 기록을 하는 중인지 (시험·--render 등에서는 false)</summary>
    public static bool Started { get; private set; }
    /// <summary>창이 떴는지</summary>
    public static bool Shown { get; private set; }
    public static bool SafeMode { get; private set; }
    /// <summary>이번 시작 전에 연달아 꺼진 횟수</summary>
    public static int EarlyCrashes { get; private set; }

    static readonly List<string> loaded = new(), failed = new(), skipped = new();
    static readonly HashSet<string> suspects = new(StringComparer.Ordinal);
    static JsonObject state = new();
    static readonly object gate = new();

    /// <summary>안전 모드에서도 늘 켜는 기본 모듈</summary>
    static readonly HashSet<string> Essential = new() { "CoreModule", "BasicToolsModule", "ShellModule", "CrashModule" };

    static string statePath;
    public static string StatePath
    {
        get
        {
            if (statePath != null) return statePath;
            string sp = Environment.GetEnvironmentVariable("COMICPAD_SETTINGS") is { Length: > 0 } s ? s
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ComicPad", "native-settings.json");
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sp)) ?? ".", Path.GetFileNameWithoutExtension(sp) + ".startup.json");
        }
        set => statePath = value;
    }

    /// <summary>안전 모드에서 바꾸는 설정과 그 안전한 값 (null = 설정 지움 → 기본값)</summary>
    static readonly (string key, Func<JsonNode> safe)[] SafeSettings =
    {
        ("gpu", () => JsonValue.Create(false)),
        ("ui", () => null),
        ("largeUI", () => null),
    };

    // ---- 시작 -------------------------------------------------------------------------------------
    /// <summary>창 모드 시작 (설정을 읽은 뒤, 모듈을 켜기 전에)</summary>
    public static void BeginStart()
    {
        lock (gate)
        {
            Started = true; Shown = false;
            loaded.Clear(); failed.Clear(); skipped.Clear(); suspects.Clear();
            state = Load();
            int crashes = state["earlyCrashes"]?.GetValue<int>() ?? 0;
            if (state["pending"]?.GetValue<bool>() == true)
            {
                crashes++;
                if (state["initing"]?.GetValue<string>() is { Length: > 0 } m) AddSuspect(m);
            }
            if (state["suspects"] is JsonArray sa) foreach (var s in sa) if (s?.GetValue<string>() is { Length: > 0 } n) suspects.Add(n);
            EarlyCrashes = crashes;
            SafeMode = crashes >= 2 || Environment.GetEnvironmentVariable("COMICPAD_SAFE_MODE") == "1";
            if (SafeMode) ApplySafeSettings();
            else RestoreSettings();
            if (SafeMode) { Environment.SetEnvironmentVariable("COMICPAD_SAFE_MODE", "1"); Environment.SetEnvironmentVariable("COMICPAD_NO_GPU", "1"); }
            state["earlyCrashes"] = crashes;
            state["pending"] = true;
            state["initing"] = null;
            state["suspects"] = new JsonArray(suspects.Select(s => (JsonNode)s).ToArray());
            state["lastStart"] = DateTime.Now.ToString("s");
            state["safeMode"] = SafeMode;
            Save();
        }
        CrashHandler.Active = true;
    }

    /// <summary>모듈 켜기 직전 (null = 모두 켬). 시작 중 꺼지면 어느 모듈이었는지 남음</summary>
    public static void BeginModule(string name)
    {
        if (name != null && !loaded.Contains(name)) loaded.Add(name);
        if (!Started || Shown) return;
        lock (gate) { state["initing"] = name; Save(); }
        if (name != null && Environment.GetEnvironmentVariable("COMICPAD_CRASHGUARD_PROBE") == "crash:" + name) Environment.Exit(3);   // 시험: 이 모듈을 켜다 꺼진 것처럼
    }

    public static void ModuleFailed(string name, Exception ex)
    {
        loaded.Remove(name);
        if (!failed.Contains(name)) failed.Add(name);
        CrashLog.Add("모듈", name + " 시작 실패: " + ex?.GetType().Name + ": " + ex?.Message);
        if (!Started) return;
        lock (gate) { AddSuspect(name); state["suspects"] = new JsonArray(suspects.Select(s => (JsonNode)s).ToArray()); Save(); }
    }

    /// <summary>안전 모드에서 건너뛸 모듈인지 (지난 시작 때 실패했거나 그 안에서 꺼진 부가 모듈)</summary>
    public static bool Skip(Type t)
    {
        if (!SafeMode || Essential.Contains(t.Name) || !suspects.Contains(t.Name)) return false;
        if (!skipped.Contains(t.Name)) skipped.Add(t.Name);
        return true;
    }

    public static (List<string> loaded, List<string> failed, List<string> skipped) Modules()
    {
        if (loaded.Count == 0 && failed.Count == 0)
        {
            var all = typeof(CrashGuard).Assembly.GetTypes().Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            return (all, new(), new());
        }
        return (loaded.ToList(), failed.ToList(), skipped.ToList());
    }

    static void AddSuspect(string typeName)
    {
        if (string.IsNullOrEmpty(typeName) || Essential.Contains(typeName)) return;
        suspects.Add(typeName);
    }

    /// <summary>창이 뜨기 전에 난 오류: 스택에 나오는 모듈을 의심 목록에 (다음 안전 모드 때 건너뜀)</summary>
    public static void NoteEarlyCrash(Exception ex)
    {
        if (!Started || Shown || ex == null) return;
        var mods = typeof(CrashGuard).Assembly.GetTypes().Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract && t.Namespace != null && t.Namespace.StartsWith("ComicPadNative.Modules.")).ToList();
        var text = ex.ToString();
        lock (gate)
        {
            foreach (Match m in Regex.Matches(text, @"ComicPadNative\.Modules\.([A-Za-z0-9_]+)\."))
                foreach (var t in mods.Where(t => t.Namespace == "ComicPadNative.Modules." + m.Groups[1].Value)) AddSuspect(t.Name);
            state["suspects"] = new JsonArray(suspects.Select(s => (JsonNode)s).ToArray());
            Save();
        }
    }

    /// <summary>창이 뜸: 3초 뒤 "정상 시작" 으로 기록 (첫 화면 그리기 중 꺼지는 것도 시작 실패로 셈)</summary>
    public static void WindowShown()
    {
        if (!Started) return;
        Shown = true;
        var t = new System.Windows.Forms.Timer { Interval = 3000 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); CleanStart(); };
        t.Start();
        Application.ApplicationExit += (_, _) => CleanStart();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanStart();   // 창이 뜬 뒤 Environment.Exit (화면 찍기 등) 도 정상으로
    }

    /// <summary>정상 시작: 횟수·의심 모듈 지움 (안전 모드에서 바꾼 설정 되돌리기 정보는 다음 보통 시작까지 둠)</summary>
    public static void CleanStart()
    {
        if (!Started) return;
        lock (gate)
        {
            if (state["pending"]?.GetValue<bool>() != true && (state["earlyCrashes"]?.GetValue<int>() ?? 0) == 0) return;
            Shown = true;
            state["pending"] = false; state["earlyCrashes"] = 0; state["initing"] = null; state["suspects"] = new JsonArray();
            Save();
        }
    }

    // ---- 지난번 오류로 닫혔을 때 보여 줄 보고서 -----------------------------------------------------------
    public static void SetUnseen(string reportPath, string savedPath)
    {
        lock (gate)
        {
            if (!Started) state = Load();
            state["unseenReport"] = reportPath; state["unseenSave"] = savedPath;
            Save();
        }
    }
    public static (string report, string save) TakeUnseen()
    {
        lock (gate)
        {
            string r = state["unseenReport"]?.GetValue<string>(), s = state["unseenSave"]?.GetValue<string>();
            if (r == null) return (null, null);
            state.Remove("unseenReport"); state.Remove("unseenSave");
            Save();
            return (r, s);
        }
    }

    // ---- 안전 모드 설정 --------------------------------------------------------------------------------
    static void ApplySafeSettings()
    {
        // 원래 값은 처음 안전 모드로 들어갈 때 한 번만 기록 (연달아 안전 모드여도 안전 값으로 덮지 않음)
        if (state["restore"] is not JsonObject rest)
        {
            rest = new JsonObject();
            foreach (var (k, _) in SafeSettings) rest[k] = App.Settings.ContainsKey(k) ? (App.Settings[k]?.DeepClone() ?? JsonValue.Create("__null__")) : JsonValue.Create("__absent__");
            state["restore"] = rest;
        }
        foreach (var (k, safe) in SafeSettings)
        {
            var v = safe();
            if (v == null) App.Settings.Remove(k); else App.Settings[k] = v;
        }
    }

    static void RestoreSettings()
    {
        if (state["restore"] is not JsonObject rest) return;
        foreach (var (k, safe) in SafeSettings)
        {
            if (!rest.ContainsKey(k)) continue;
            var sv = safe();
            var cur = App.Settings.ContainsKey(k) ? App.Settings[k] : null;
            bool untouched = sv == null ? !App.Settings.ContainsKey(k) : cur != null && JsonNode.DeepEquals(cur, sv);
            if (!untouched) continue;   // 안전 모드 동안 사용자가 직접 바꿈 → 그대로
            var orig = rest[k];
            if (orig is JsonValue ov && ov.TryGetValue<string>(out var os) && os == "__absent__") App.Settings.Remove(k);
            else if (orig is JsonValue nv && nv.TryGetValue<string>(out var ns) && ns == "__null__") App.Settings[k] = null;
            else App.Settings[k] = orig?.DeepClone();
        }
        state.Remove("restore");
        App.SaveSettings();
    }

    // ---- 상태 파일 ---------------------------------------------------------------------------------
    static JsonObject Load()
    {
        try { if (File.Exists(StatePath)) return JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject ?? new(); } catch { }
        return new();
    }
    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            string tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StatePath, true);
        }
        catch { }
    }

    /// <summary>시험용 (창 없이 다른 프로세스로): COMICPAD_CRASHGUARD_PROBE = crash (창 전에 꺼짐) · crash:모듈 (그 모듈을 켜다 꺼짐) · probe:파일 (상태를 파일로 쓰고 정상 시작으로 기록)</summary>
    public static void Probe()
    {
        string p = Environment.GetEnvironmentVariable("COMICPAD_CRASHGUARD_PROBE");
        if (string.IsNullOrEmpty(p)) return;
        if (p == "crash") Environment.Exit(3);
        if (p.StartsWith("probe:"))
        {
            var o = new JsonObject
            {
                ["safeMode"] = SafeMode, ["earlyCrashes"] = EarlyCrashes,
                ["skipped"] = new JsonArray(skipped.Select(s => (JsonNode)s).ToArray()),
                ["gpu"] = App.Settings["gpu"]?.DeepClone(), ["ui"] = App.Settings["ui"]?.DeepClone(), ["hasUi"] = App.Settings.ContainsKey("ui"),
                ["noGpuEnv"] = Environment.GetEnvironmentVariable("COMICPAD_NO_GPU"),
            };
            File.WriteAllText(p[6..], o.ToJsonString());
            CleanStart();
            App.SaveSettings();
            Environment.Exit(0);
        }
    }
}
