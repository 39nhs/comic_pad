// 오류 보고서 만들기: 환경·설정·모듈·오류·최근 명령·최근 기록을 글로 모음 (이 PC 에서만, 어디에도 보내지 않음).
//  - 문서 내용(그림·글자)·파일 이름은 넣지 않음: 경로는 "%USERPROFILE%\…\*.cpad" 처럼 확장자만 남기고,
//    문서 이름·레이어 이름·글 레이어 글자는 보고서 어디에 나오든 "<문서 글>" 로 바꿈.
//  - 사용자 폴더 경로는 %USERPROFILE%, 사용자 이름·컴퓨터 이름은 %USERNAME%·%COMPUTERNAME% 으로.
//  - 긴급 저장: data/autosave 에 "이름 (긴급 저장 날짜).cpad" 로 새로 씀 (사용자 파일은 절대 덮어쓰지 않음).
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Crash;

/// <summary>앱 안 기록 (최근 명령·알림·도구 바꾸기). 메모리에만, 보고서를 만들 때 가려서 씀</summary>
public static class CrashLog
{
    static readonly object gate = new();
    static readonly LinkedList<(DateTime t, string kind, string text)> items = new();
    const int Max = 400;

    public static void Add(string kind, string text)
    {
        lock (gate)
        {
            items.AddLast((DateTime.Now, kind, text ?? ""));
            while (items.Count > Max) items.RemoveFirst();
        }
    }
    public static void Command(string name) => Add("명령", name);
    public static void Clear() { lock (gate) items.Clear(); }

    public static List<(DateTime t, string kind, string text)> Snapshot() { lock (gate) return items.ToList(); }
}

/// <summary>긴급 저장 결과: Path = 저장한 파일 (못 했으면 null), Note = 사용자에게 보일 설명</summary>
public sealed record SaveResult(string Path, string Note);

public static class CrashReport
{
    public const string RepoUrl = "https://github.com/39nhs/comic_pad";
    public static string ErrorLogPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "comicpad-native-error.log");
    public static string ReportsDir => System.IO.Path.Combine(App.DataDir, "reports");

    // ---- 긴급 저장 --------------------------------------------------------------------------------
    /// <summary>지금 작품을 data/autosave 에 새 파일로 (사용자 파일은 덮어쓰지 않음). 바뀐 것이 없고 파일이 있으면 저장하지 않음</summary>
    public static SaveResult EmergencySave(Document d = null)
    {
        d ??= App.Doc;
        if (d == null) return new SaveResult(null, "열린 작품이 없어 저장할 것이 없습니다.");
        if (!d.Modified && d.FilePath != null && File.Exists(d.FilePath)) return new SaveResult(null, "저장한 뒤 바뀐 것이 없어 원래 파일 그대로입니다.");
        try
        {
            string dir = System.IO.Path.Combine(App.DataDir, "autosave");
            Directory.CreateDirectory(dir);
            string name = Safe(string.IsNullOrWhiteSpace(d.Name) ? "제목 없음" : d.Name);
            if (name.Length > 60) name = name[..60];
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string p = System.IO.Path.Combine(dir, $"{name} (긴급 저장 {stamp}).cpad");
            for (int i = 2; File.Exists(p); i++) p = System.IO.Path.Combine(dir, $"{name} (긴급 저장 {stamp}-{i}).cpad");
            if (d.FilePath != null && string.Equals(System.IO.Path.GetFullPath(p), System.IO.Path.GetFullPath(d.FilePath), StringComparison.OrdinalIgnoreCase))
                return new SaveResult(null, "저장할 곳이 원래 파일과 같아 저장하지 않았습니다.");
            string tmp = p + ".tmp";
            File.WriteAllText(tmp, CpadIO.ToJson(d).ToJsonString());
            File.Move(tmp, p, false);
            return new SaveResult(p, "작업은 여기 저장했습니다: " + p);
        }
        catch (Exception e) { return new SaveResult(null, "작업을 저장하지 못했습니다: " + e.Message); }
    }
    static string Safe(string n) { foreach (var c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n.Trim(); }

    // ---- 가리기 -----------------------------------------------------------------------------------
    /// <summary>문서에서 보고서에 나오면 안 되는 글 (문서 이름·파일 이름·레이어 이름·글 레이어 글자 등)</summary>
    public static List<string> UserStrings(Document d)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (s.Length < 2 || s.StartsWith("data:") || Regex.IsMatch(s, @"^#[0-9a-fA-F]{3,8}$") || Regex.IsMatch(s, @"^[-+0-9.,eE ]+$")) return;
            // 앱이 쓰는 이름표 (source-over, raster, L12 …) 는 문서 글이 아님
            if (Regex.IsMatch(s, @"^[A-Za-z][A-Za-z0-9_.\-]*$") && s.Length < 6) return;
            if (s.Length > 400) s = s[..400];
            set.Add(s);
            foreach (var line in s.Split('\n', '\r')) if (line.Trim().Length >= 2 && line.Trim() != s) set.Add(line.Trim());
        }
        void Walk(JsonNode n, int depth)
        {
            if (n == null || depth > 8 || set.Count > 3000) return;
            if (n is JsonObject o) foreach (var (_, v) in o) Walk(v, depth + 1);
            else if (n is JsonArray a) { foreach (var v in a) Walk(v, depth + 1); }
            else if (n is JsonValue jv && jv.TryGetValue<string>(out var s) && (s.Any(ch => ch > 0x7F) || s.Contains(' ') || s.Length >= 8)) Add(s);
        }
        if (d == null) return new();
        Add(d.Name);
        if (d.FilePath != null)
        {
            Add(d.FilePath);
            Add(System.IO.Path.GetFileNameWithoutExtension(d.FilePath));
            // 바로 위 폴더 이름 (보통 작품 폴더). 경로 전체는 Sanitize 가 확장자만 남기고 접음
            string parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(d.FilePath) ?? "");
            if (parent.Length >= 3) Add(parent);
        }
        foreach (var l in d.Layers)
        {
            if (l.Name != null && l.Name.Length >= 2) Add(l.Name);
            Walk(l.Meta, 0); Walk(l.Extra, 0);
        }
        Walk(d.Extra, 0);
        // 흔한 폴더 이름은 가릴 필요 없음 (보고서의 다른 글을 망가뜨리지 않게)
        foreach (var common in new[] { "Users", "AppData", "Local", "Temp", "Roaming", "Documents", "data", "projects", "autosave", "ComicPad", "Desktop", "Program Files" }) set.Remove(common);
        return set.OrderByDescending(s => s.Length).ToList();
    }

    static readonly Regex PathRx = new(@"(?<root>[A-Za-z]:|%USERPROFILE%|%TEMP%|\\\\[A-Za-z0-9._$\-]+)(?<rest>(?:[\\/][^\\/\r\n""'<>|*?:]*)+)", RegexOptions.Compiled);
    static readonly Regex ExtRx = new(@"^(?<name>.*?)(?<ext>\.[A-Za-z0-9]{1,6})(?<tail>(?=$|[\s""'),;\]]).*)$", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".dll", ".exe" };

    /// <summary>보고서 글 가리기: 사용자 폴더 → %USERPROFILE%, 경로는 확장자만 (소스 파일 이름 .cs 는 그대로), 문서 글 → &lt;문서 글&gt;</summary>
    public static string Sanitize(string text, IEnumerable<string> userStrings = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && home.Length > 3)
        {
            text = text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(home.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        string tmp = System.IO.Path.GetTempPath().TrimEnd('\\');
        if (tmp.Length > 3 && !tmp.Contains("%USERPROFILE%")) text = text.Replace(tmp, "%TEMP%", StringComparison.OrdinalIgnoreCase);
        text = PathRx.Replace(text, m =>
        {
            string root = m.Groups["root"].Value, rest = m.Groups["rest"].Value;
            if (rest.Length > 1 && rest[1] == '…') return m.Value;   // 이미 접은 경로 (두 번 가려도 같게)
            var parts = rest.Split('\\', '/');
            string last = parts[^1];
            var em = ExtRx.Match(last);
            string sep = rest.Contains('/') && !rest.Contains('\\') ? "/" : "\\";
            string prefix = root + (parts.Length > 2 ? sep + "…" : "");
            if (!em.Success) return prefix + sep + "…";
            string ext = em.Groups["ext"].Value, tail = em.Groups["tail"].Value;
            string file = CodeExt.Contains(ext) ? em.Groups["name"].Value + ext : "*" + ext;
            return prefix + sep + file + tail;
        });
        // 경로를 접은 뒤 문서 글 가리기 (경로 안의 이름은 이미 접혔음)
        if (userStrings != null)
            foreach (var s in userStrings) if (s.Length >= 2) text = text.Replace(s, "<문서 글>", StringComparison.OrdinalIgnoreCase);
        string user = Environment.UserName, machine = Environment.MachineName;
        if (!string.IsNullOrEmpty(user) && user.Length >= 3) text = Regex.Replace(text, Regex.Escape(user), "%USERNAME%", RegexOptions.IgnoreCase);
        if (!string.IsNullOrEmpty(machine) && machine.Length >= 3) text = Regex.Replace(text, Regex.Escape(machine), "%COMPUTERNAME%", RegexOptions.IgnoreCase);
        return text;
    }

    // ---- 보고서 ----------------------------------------------------------------------------------
    /// <summary>보고서 제목 (GitHub 이슈 제목)</summary>
    public static string Title(Exception ex, IEnumerable<string> user = null)
    {
        string msg = (ex?.Message ?? "").Split('\n')[0].Trim();
        string t = $"[오류] {ex?.GetType().Name ?? "알 수 없음"}: {msg}";
        t = Sanitize(t, user);
        return t.Length > 100 ? t[..100] : t;
    }

    public static string Build(Exception ex, string where, bool fatal, SaveResult save, Document doc = null)
    {
        doc ??= App.Doc;
        var user = UserStrings(doc);
        var sb = new StringBuilder();
        sb.AppendLine(Title(ex, user)).AppendLine();
        sb.AppendLine("## 무슨 일이 있었나요?").AppendLine("(무엇을 하다가 오류가 났는지 적어 주시면 고치는 데 큰 도움이 됩니다 — 비워 둬도 됩니다)").AppendLine();

        sb.AppendLine("## 오류");
        sb.AppendLine($"- 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 어디서: {(string.IsNullOrEmpty(where) ? "명령 밖 (화면 처리 중)" : "명령 \"" + where + "\"")}{(fatal ? " — 앱이 닫힘" : " — 앱은 계속 실행")}");
        sb.AppendLine($"- 시작 중이었나: {(CrashGuard.Started && !CrashGuard.Shown ? "예 (창이 뜨기 전)" : "아니요")}");
        sb.AppendLine($"- 긴급 저장: {(save?.Path != null ? "함 (" + System.IO.Path.GetExtension(save.Path) + ", data\\autosave)" : save?.Note ?? "안 함")}");
        sb.AppendLine("```");
        sb.AppendLine(ex?.ToString() ?? "(오류 정보 없음)");
        sb.AppendLine("```").AppendLine();

        sb.AppendLine("## 환경");
        string ver = "?";
        try { ver = Shell2.Updater.CurrentVersion; } catch { }
        sb.AppendLine($"- ComicPad: {ver} (네이티브 판)");
        sb.AppendLine($"- Windows: {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version}) {RuntimeInformation.OSArchitecture}, 프로세스 {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"- .NET: {RuntimeInformation.FrameworkDescription} ({Environment.Version})");
        sb.AppendLine($"- 그래픽 카드: {string.Join(", ", Adapters().DefaultIfEmpty("알 수 없음"))}");
        try
        {
            var scr = Screen.PrimaryScreen;
            sb.AppendLine($"- 화면: {scr?.Bounds.Width}×{scr?.Bounds.Height}, 모니터 {Screen.AllScreens.Length}개, 창 DPI {App.Main?.DeviceDpi.ToString() ?? "?"}");
        }
        catch { }
        try { var c = Shell2.Shell2Ui.Canvas; sb.AppendLine($"- 펜 입력: {(c?.PenRate > 0 ? $"있음 ({c.PenRate}점/초)" : "아직 없음")}"); } catch { }
        sb.AppendLine($"- 메모리: {Environment.WorkingSet / 1_000_000}MB 사용 중, 실행 {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMinutes:0}분째");
        sb.AppendLine($"- 안전 모드: {(CrashGuard.SafeMode ? "켜짐" : "꺼짐")}");
        sb.AppendLine();

        sb.AppendLine("## 관련 설정");
        foreach (var (k, v) in ImportantSettings()) sb.AppendLine($"- {k}: {v}");
        sb.AppendLine();

        sb.AppendLine("## 모듈");
        var (loaded, failed, skipped) = CrashGuard.Modules();
        sb.AppendLine($"- 불러옴 ({loaded.Count}): {string.Join(", ", loaded)}");
        if (failed.Count > 0) sb.AppendLine($"- 시작 실패: {string.Join(", ", failed)}");
        if (skipped.Count > 0) sb.AppendLine($"- 안전 모드로 건너뜀: {string.Join(", ", skipped)}");
        sb.AppendLine();

        sb.AppendLine("## 작업 중이던 문서 (내용·이름은 넣지 않음)");
        if (doc == null) sb.AppendLine("- 없음");
        else
        {
            sb.AppendLine($"- 크기: {doc.W}×{doc.H}, {doc.Dpi}dpi, 레이어 {doc.Layers.Count}개 (종류: {string.Join(", ", doc.Layers.GroupBy(l => l.Kind).Select(g => g.Key + " " + g.Count()))})");
            sb.AppendLine($"- 파일: {(doc.FilePath == null ? "저장 안 한 새 작품" : "*" + System.IO.Path.GetExtension(doc.FilePath))}, 바뀜: {(doc.Modified ? "예" : "아니요")}");
        }
        sb.AppendLine();

        var log = CrashLog.Snapshot();
        sb.AppendLine("## 최근 명령 (오래된 것 → 최근, 최대 50개)");
        var cmds = log.Where(e => e.kind is "명령" or "도구").TakeLast(50).ToList();
        if (cmds.Count == 0) sb.AppendLine("- 없음");
        foreach (var e in cmds) sb.AppendLine($"- {e.t:HH:mm:ss} {e.kind}: {e.text}");
        sb.AppendLine();

        sb.AppendLine("## 최근 기록 (앱 알림·오류 기록 파일 끝, 최대 200줄)");
        var lines = new List<string>();
        foreach (var e in log.Where(e => e.kind is not ("명령" or "도구")).TakeLast(60)) lines.Add($"{e.t:HH:mm:ss} {e.kind}: {e.text.Replace("\r", " ").Replace("\n", " ")}");
        lines.AddRange(ErrorLogTail(200 - lines.Count));
        sb.AppendLine("```");
        foreach (var l in lines.TakeLast(200)) sb.AppendLine(l);
        sb.AppendLine("```");
        return Sanitize(sb.ToString(), user);
    }

    /// <summary>오류 기록 파일 (%TEMP%\comicpad-native-error.log) 끝 n줄</summary>
    public static List<string> ErrorLogTail(int n)
    {
        if (n <= 0) return new();
        try
        {
            var fi = new FileInfo(ErrorLogPath);
            if (!fi.Exists) return new();
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - 256_000);
            fs.Seek(start, SeekOrigin.Begin);
            using var r = new StreamReader(fs, Encoding.UTF8);
            var all = r.ReadToEnd().Replace("\r\n", "\n").Split('\n').ToList();
            if (start > 0 && all.Count > 0) all.RemoveAt(0);   // 잘린 첫 줄
            while (all.Count > 0 && all[^1].Trim().Length == 0) all.RemoveAt(all.Count - 1);
            return all.TakeLast(n).ToList();
        }
        catch { return new(); }
    }

    static readonly Regex ImportantKey = new(@"^(gpu.*|.*Gpu.*|.*GPU.*|pen.*|.*Pen.*|pressure.*|palm.*|mouse.*|finger.*|touch.*|twoFinger.*|threeFinger.*|longPress.*|wintab.*|nativePen|ui|largeUI|leftHanded|wheelMode|undoMB|autosaveMin|aiCpuOnly|memSave|crashReport|brushCursor)$", RegexOptions.Compiled);

    /// <summary>문제와 관련 있을 만한 설정 (GPU·펜·화면 방식 등). 값이 길면 줄임</summary>
    public static List<(string key, string value)> ImportantSettings()
    {
        var res = new List<(string, string)>();
        try
        {
            foreach (var (k, v) in App.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!ImportantKey.IsMatch(k) || k.Contains("Name", StringComparison.OrdinalIgnoreCase) || k.Contains("recent", StringComparison.OrdinalIgnoreCase)) continue;
                string s = v?.ToJsonString() ?? "null";
                if (s.Length > 200) s = s[..200] + "…";
                res.Add((k, s));
            }
        }
        catch { }
        if (!res.Any(r => r.Item1.Contains("gpu", StringComparison.OrdinalIgnoreCase))) res.Add(("gpu", "(설정 없음 = 기본)"));
        if (Environment.GetEnvironmentVariable("COMICPAD_NO_GPU") == "1") res.Add(("COMICPAD_NO_GPU", "1"));
        return res;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

    /// <summary>그래픽 카드 이름 (화면에 연결된 것, 주 화면 것 먼저)</summary>
    public static List<string> Adapters()
    {
        var res = new List<(bool primary, string name)>();
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & 1) == 0 || string.IsNullOrWhiteSpace(dd.DeviceString)) continue;   // 화면에 연결된 것만
                res.Add(((dd.StateFlags & 4) != 0, dd.DeviceString.Trim()));
            }
        }
        catch { }
        return res.OrderByDescending(r => r.primary).Select(r => r.name).Distinct().ToList();
    }

    /// <summary>보고서를 data/reports/오류_날짜.txt 로 (이 PC 에만)</summary>
    public static string SaveReport(string text, string path = null)
    {
        try
        {
            Directory.CreateDirectory(ReportsDir);
            path ??= System.IO.Path.Combine(ReportsDir, $"오류_{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            if (!File.Exists(path) || path.StartsWith(ReportsDir, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
                // 오래된 보고서는 50개까지만
                foreach (var old in Directory.GetFiles(ReportsDir, "오류_*.txt").OrderByDescending(f => f, StringComparer.Ordinal).Skip(50)) try { File.Delete(old); } catch { }
            }
            return path;
        }
        catch { return null; }
    }

    public static List<string> RecentReports()
    {
        try { return Directory.Exists(ReportsDir) ? Directory.GetFiles(ReportsDir, "오류_*.txt").OrderByDescending(f => f, StringComparer.Ordinal).ToList() : new(); }
        catch { return new(); }
    }

    /// <summary>GitHub 새 이슈 주소 (제목 + 본문, 주소 길이 제한 안으로 줄임 — 전체는 클립보드에)</summary>
    public static string IssueUrl(string report, int maxUrl = 7500)
    {
        report = (report ?? "").Replace("\r\n", "\n");
        string title = report.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "[오류] 보고";
        if (title.Length > 100) title = title[..100];
        string body = report.Contains('\n') ? report[(report.IndexOf('\n') + 1)..].TrimStart('\n') : report;
        string head = $"{RepoUrl}/issues/new?labels=crash&title={Uri.EscapeDataString(title)}&body=";
        string full = head + Uri.EscapeDataString(body);
        if (full.Length <= maxUrl) return full;
        const string cut = "\n\n…(길어서 잘렸습니다 — 전체 보고서는 클립보드에 복사해 두었으니 여기에 붙여 넣어 주세요)";
        int lo = 0, hi = body.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if ((head + Uri.EscapeDataString(body[..mid] + cut)).Length <= maxUrl) lo = mid; else hi = mid - 1;
        }
        if (lo > 0 && char.IsHighSurrogate(body[lo - 1])) lo--;
        return head + Uri.EscapeDataString(body[..lo] + cut);
    }
}
