// 업데이트 (웹 판 ui/update.js + server.py 의 update_* 와 같은 방식, 같은 공개키):
//  1) GitHub 최신 릴리스의 update.json + update.json.sig 를 받아 RSA-3072 (PKCS#1 v1.5, SHA-256) 서명을 release_pubkey.json 공개키로 확인
//  2) 서명된 내용(제품·버전·파일 이름·크기·SHA-256)을 검사
//  3) 설치파일이 서명된 정보에 있으면(installer) 그것을 받아 SHA-256 확인 후 실행하고 앱을 닫음,
//     없으면 업데이트 꾸러미(zip)를 받아 SHA-256 확인 후 설치 폴더에 적용 (바꾸기 전 판은 data/updates 에 zip 으로, 되돌리기 가능)
//  - 작업물(data)·python·redist·dist 는 절대 건드리지 않음, 이상한 경로(.., 절대 경로)는 건너뜀
//  - 하루 한 번 켤 때 조용히 확인 (settings.update.auto), 인터넷이 없으면 아무 일도 안 함
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public sealed record UpdateManifest(string Version, string File, long Size, string Sha256, string Notes, string Date,
    string InstallerFile, long InstallerSize, string InstallerSha256);

public sealed class UpdateInfo
{
    public string Current, Latest, Notes, Date, Page;
    public bool Available;
    public long Size;
    public UpdateManifest Man;
    public string ZipUrl, InstallerUrl;
}

public static class Updater
{
    public const string Repo = "39nhs/comic_pad";
    const string Api = "https://api.github.com";
    public const long MaxPackage = 600L * 1000 * 1000;
    static readonly string[] Keep = { "data/", "python/", "redist/", "dist/", ".git/" };
    const string OldSuffix = ".cpold";

    // release_pubkey.json 과 같은 공개키 (설치 폴더에 파일이 없을 때 씀). 비밀키는 만드는 사람 PC 에만 있음.
    const string EmbeddedN = "ce60522b5c4a8f2762e9e0e4db4e8112c50384810ae3e9e8027228eba89312cbc65dfd58f794863884c69bcabf6c17553570c16846181ffd8e81a565c13c09c078f352021f7c54a9bf55deefdc3faafcfe3f0aebb708f163e9e430d36497963bf27b72122a14be1b19a58e71238848e06c3e2c7c6205c3cc59822dcf4c5f1db0c14cf1bf46dfd7d960557229717ce4f7630c347ceb5bc5f7bb4a2ef100c565da3966829aba144b455657e072ccd57f9c83b68b9cec66eb3922ef453cd811826f06fadb7d37e3e7a0d4dc5339874679d3f614167a5d15267a9d2e98711dc26516d00ecebfaec05c79ee572eebcd11860b7e3fe95d9fa9d0345bd8b8ae5e02eb044e0ce1c1026b8800e2406466f8c0f644c4bcb46ee5adbf40ecbe18dc5c3d448f28c08529c37d8fa1efdc45c6bbcb028c8359c272aca3c6b4e07a82a0296a1554d712da176437096e36a93b04f05b452687d2d7743b7052c76df3c3ca04127d0bf5520c3f27404ed792debc923ac650e4cf410ea3ac1273ba7d7cd81d5e9af851";

    // ---- 설치 폴더·버전 ------------------------------------------------------------------
    /// <summary>VERSION 파일이 있는 설치 폴더 (실행 파일 폴더에서 위로 찾음), 없으면 실행 파일 폴더</summary>
    public static string InstallRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 7 && d != null; i++, d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "VERSION")) && (File.Exists(Path.Combine(d.FullName, "release_pubkey.json")) || File.Exists(Path.Combine(d.FullName, "server.py")))) return d.FullName;
            return AppContext.BaseDirectory.TrimEnd('\\', '/');
        }
    }

    public static string VersionIn(string root)
    {
        try { var v = File.ReadAllText(Path.Combine(root, "VERSION")).Trim(); return v.Length > 0 ? v : "0.0.0"; } catch { return "0.0.0"; }
    }
    public static string CurrentVersion => VersionIn(InstallRoot);

    public static (int, int, int) VTuple(string v)
    {
        var n = Regex.Matches(v ?? "", @"\d+").Select(m => int.TryParse(m.Value, out var x) ? x : 0).Take(3).ToList();
        while (n.Count < 3) n.Add(0);
        return (n[0], n[1], n[2]);
    }
    public static bool Newer(string a, string b) => VTuple(a).CompareTo(VTuple(b)) > 0;

    // ---- 서명 ------------------------------------------------------------------------------
    public static byte[] Hex(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0 || s.Length % 2 != 0 || !Regex.IsMatch(s, "^[0-9a-fA-F]+$")) return Array.Empty<byte>();
        return Convert.FromHexString(s);
    }

    static RSAParameters Key(string nHex, long e)
    {
        var n = Hex(nHex.Length % 2 == 1 ? "0" + nHex : nHex);
        int i = 0; while (i < n.Length - 1 && n[i] == 0) i++;
        var eb = BitConverter.GetBytes((uint)e); if (BitConverter.IsLittleEndian) Array.Reverse(eb);
        int j = 0; while (j < eb.Length - 1 && eb[j] == 0) j++;
        return new RSAParameters { Modulus = n[i..], Exponent = eb[j..] };
    }

    public static int Bits(RSAParameters k) { var m = k.Modulus; if (m == null || m.Length == 0) return 0; int b = m.Length * 8, top = m[0]; while (top < 128 && b > 0) { top <<= 1; b--; } return b; }

    /// <summary>믿는 공개키: 설치 폴더의 release_pubkey.json ("keys" 여러 개 가능), 없으면 내장 키. 3072비트 미만은 버림</summary>
    public static List<RSAParameters> TrustedKeys(string root = null)
    {
        var list = new List<RSAParameters>();
        try
        {
            string p = Path.Combine(root ?? InstallRoot, "release_pubkey.json");
            if (File.Exists(p) && JsonNode.Parse(File.ReadAllText(p)) is JsonObject k)
            {
                var keys = k["keys"] is JsonArray a ? a.OfType<JsonObject>() : new[] { k };
                foreach (var x in keys) list.Add(Key((string)x["n"], (long)x["e"]));
            }
        }
        catch { list.Clear(); }
        if (list.Count == 0) list.Add(Key(EmbeddedN, 65537));
        return list.Where(x => Bits(x) >= 3072).ToList();
    }

    /// <summary>RSASSA-PKCS1-v1_5 + SHA-256 (server.py rsa_verify 와 같은 검사)</summary>
    public static bool Verify(byte[] data, byte[] sig, RSAParameters key)
    {
        try
        {
            if (sig == null || key.Modulus == null || sig.Length != key.Modulus.Length) return false;
            using var rsa = RSA.Create();
            rsa.ImportParameters(key);
            return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }
    public static bool VerifyAny(byte[] data, byte[] sig, IEnumerable<RSAParameters> keys) => keys.Any(k => Bits(k) >= 3072 && Verify(data, sig, k));

    /// <summary>서명이 맞은 update.json 의 내용 검사 (server.py update_check 와 같은 규칙). 틀리면 예외</summary>
    public static UpdateManifest ParseManifest(byte[] manBytes, ICollection<string> assets)
    {
        var m = JsonNode.Parse(Encoding.UTF8.GetString(manBytes)) as JsonObject ?? throw new InvalidDataException("업데이트 정보가 올바르지 않습니다");
        string product = (string)m["product"];
        if (product != null && product != "ComicPad") throw new InvalidDataException("다른 프로그램용 업데이트입니다");
        string file = (string)m["file"], sha = (string)m["sha256"], ver = m["version"]?.ToString();
        long size = m["size"] is JsonValue sv && sv.TryGetValue<long>(out var sz) ? sz : 0;
        if (file == null || (assets != null && !assets.Contains(file)) || !Regex.IsMatch(file, @"^ComicPad_update_[\w.-]+\.zip$")
            || sha == null || !Regex.IsMatch(sha, "^[0-9a-f]{64}$") || size <= 0 || size > MaxPackage || ver == null || !Regex.IsMatch(ver, @"^\d+\.\d+\.\d+$"))
            throw new InvalidDataException("업데이트 정보가 올바르지 않습니다");
        string iFile = null, iSha = null; long iSize = 0;
        if (m["installer"] is JsonObject ins)
        {
            iFile = (string)ins["file"]; iSha = (string)ins["sha256"];
            iSize = ins["size"] is JsonValue iv && iv.TryGetValue<long>(out var isz) ? isz : 0;
            if (iFile == null || (assets != null && !assets.Contains(iFile)) || !Regex.IsMatch(iFile, @"^ComicPad_Setup_[\w.-]+\.exe$")
                || iSha == null || !Regex.IsMatch(iSha, "^[0-9a-f]{64}$") || iSize <= 0 || iSize > MaxPackage)
                throw new InvalidDataException("설치파일 정보가 올바르지 않습니다");
        }
        string notes = ((string)m["notes"] ?? "").Trim();
        if (notes.Length > 4000) notes = notes[..4000];
        return new UpdateManifest(ver, file, size, sha, notes, (string)m["date"] ?? "", iFile, iSize, iSha);
    }

    // ---- 받기 ------------------------------------------------------------------------------
    static HttpClient http;
    static HttpClient Http
    {
        get
        {
            if (http != null) return http;
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ComicPad-Updater");
            return http;
        }
    }

    static void HttpsOnly(Uri u) { if (u == null || u.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("업데이트 주소가 https 가 아니어서 받지 않습니다"); }

    static async Task<byte[]> Get(string url, long limit = 2_000_000, string accept = null)
    {
        HttpsOnly(new Uri(url));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept != null) req.Headers.Accept.ParseAdd(accept);
        using var r = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        HttpsOnly(r.RequestMessage?.RequestUri);
        if ((int)r.StatusCode == 404) throw new InvalidDataException("아직 올라온 업데이트가 없습니다");
        if (!r.IsSuccessStatusCode) throw new InvalidDataException($"GitHub 에 연결하지 못했습니다 ({(int)r.StatusCode})");
        using var s = await r.Content.ReadAsStreamAsync();
        var ms = new MemoryStream();
        var buf = new byte[1 << 16];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0) { ms.Write(buf, 0, n); if (ms.Length > limit) throw new InvalidDataException("응답이 너무 큽니다"); }
        return ms.ToArray();
    }

    /// <summary>최신 릴리스 확인. 서명이 맞는 update.json 만 믿는다</summary>
    public static async Task<UpdateInfo> Check()
    {
        var keys = TrustedKeys();
        if (keys.Count == 0) throw new InvalidDataException("업데이트 서명 키가 없어 업데이트를 확인할 수 없습니다");
        string repo = (string)(App.Settings["update"] as JsonObject)?["repo"] ?? Repo;
        if (!Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) throw new InvalidDataException("저장소 이름이 이상합니다");
        byte[] manBytes; string sigText; Dictionary<string, string> assets;
        try
        {
            var rel = JsonNode.Parse(await Get($"{Api}/repos/{repo}/releases/latest", accept: "application/vnd.github+json")) as JsonObject;
            assets = new();
            foreach (var a in (rel?["assets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if ((string)a["name"] is string nm && (string)a["browser_download_url"] is string dl) assets[nm] = dl;
            if (!assets.ContainsKey("update.json") || !assets.ContainsKey("update.json.sig")) throw new InvalidDataException($"최신 릴리스({(string)rel?["tag_name"]})에 업데이트 꾸러미가 없습니다");
            manBytes = await Get(assets["update.json"]);
            sigText = Encoding.ASCII.GetString(await Get(assets["update.json.sig"])).Trim();
        }
        catch (HttpRequestException e) { throw new InvalidDataException($"인터넷에 연결되어 있지 않거나 GitHub 에 연결하지 못했습니다 ({e.Message})"); }
        catch (TaskCanceledException) { throw new InvalidDataException("GitHub 응답이 없습니다 (시간 초과)"); }
        if (!VerifyAny(manBytes, Hex(sigText), keys)) throw new InvalidDataException("업데이트 서명이 맞지 않아 받지 않습니다 (만든 사람이 올린 파일이 아닐 수 있음)");
        var man = ParseManifest(manBytes, assets.Keys);
        string cur = CurrentVersion;
        var info = new UpdateInfo
        {
            Current = cur, Latest = man.Version, Available = Newer(man.Version, cur), Size = man.InstallerFile != null ? man.InstallerSize : man.Size,
            Notes = man.Notes, Date = man.Date, Page = $"https://github.com/{repo}/releases/tag/v{man.Version}", Man = man,
            ZipUrl = assets[man.File], InstallerUrl = man.InstallerFile != null ? assets[man.InstallerFile] : null,
        };
        var u = Conf(); u["checked"] = DateTimeOffset.Now.ToUnixTimeSeconds(); App.SaveSettingsSoon();
        return info;
    }

    /// <summary>받아서 크기·SHA-256 확인 (틀리면 지우고 예외)</summary>
    public static async Task Download(string url, string dest, long size, string sha, Action<long, long> progress, CancellationToken ct)
    {
        HttpsOnly(new Uri(url));
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        string part = dest + ".part";
        using (var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            HttpsOnly(r.RequestMessage?.RequestUri);
            if (!r.IsSuccessStatusCode) throw new InvalidDataException($"업데이트를 받지 못했습니다 ({(int)r.StatusCode})");
            using var s = await r.Content.ReadAsStreamAsync(ct);
            using var f = File.Create(part);
            var buf = new byte[1 << 18]; long done = 0; int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                done += n;
                if (done > size) throw new InvalidDataException("받는 파일이 예상보다 커서 중단했습니다");
                f.Write(buf, 0, n);
                progress?.Invoke(done, size);
            }
        }
        if (!CheckFile(part, size, sha)) { try { File.Delete(part); } catch { } throw new InvalidDataException("받은 파일이 손상되었습니다 (SHA-256 이 다름). 다시 시도하세요"); }
        File.Move(part, dest, true);
    }

    public static string Sha256File(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(); }
    public static bool CheckFile(string path, long size, string sha) => File.Exists(path) && new FileInfo(path).Length == size && Sha256File(path) == sha;

    // ---- 꾸러미 적용 (server.py apply_zip 과 같은 규칙) -----------------------------------------
    public static List<(ZipArchiveEntry e, string rel)> PackageFiles(ZipArchive z)
    {
        var o = new List<(ZipArchiveEntry, string)>();
        foreach (var e in z.Entries)
        {
            string rel = e.FullName.Replace('\\', '/');
            if (rel.EndsWith("/") || rel.StartsWith("/") || rel.Split('/').Contains("..") || rel.Contains(':')) continue;
            if (Keep.Any(k => rel.StartsWith(k, StringComparison.OrdinalIgnoreCase)) || rel == "python_path.txt") continue;
            o.Add((e, rel));
        }
        return o;
    }

    static string SafeJoin(string root, string rel)
    {
        string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        string b = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(b, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("bad path");
        return full;
    }

    /// <summary>dst 를 src 로. 실행 중이라 잠긴 파일(exe·dll)은 이름을 바꿔 비키게 하고 넣음</summary>
    static void Put(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        try { File.Move(src, dst, true); return; } catch (UnauthorizedAccessException) { } catch (IOException) { }
        string old = dst + OldSuffix; int i = 0;
        while (File.Exists(old)) { try { File.Delete(old); } catch { i++; old = $"{dst}.{i}{OldSuffix}"; } }
        File.Move(dst, old);
        File.Move(src, dst, true);
    }

    public static string UpdatesDir(string dataDir = null) => Path.Combine(dataDir ?? App.DataDir, "updates");

    /// <summary>zip 안의 프로그램 파일로 바꾼다. 바꾸기 전 판은 updates/ComicPad_이전판_<지금 버전>.zip. (이전 버전, 바꾼 파일 수)</summary>
    public static (string from, int files) ApplyZip(string zipPath, string root, string updatesDir, Action<int, int> progress = null)
    {
        Directory.CreateDirectory(updatesDir);
        string stage = Path.Combine(updatesDir, "staging");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        List<string> rels;
        using (var z = ZipFile.OpenRead(zipPath))
        {
            var files = PackageFiles(z);
            foreach (var (e, rel) in files) { var o = SafeJoin(stage, rel); Directory.CreateDirectory(Path.GetDirectoryName(o)); e.ExtractToFile(o, true); }
            rels = files.Select(f => f.rel).OrderBy(r => r == "VERSION" ? 1 : 0).ToList();   // VERSION 은 맨 끝에
        }
        string cur = VersionIn(root);
        string keep = Path.Combine(updatesDir, $"ComicPad_이전판_{cur}.zip"), tmp = keep + ".part";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var kz = ZipFile.Open(tmp, ZipArchiveMode.Create))
            foreach (var rel in rels) { var full = SafeJoin(root, rel); if (File.Exists(full)) kz.CreateEntryFromFile(full, rel); }
        File.Move(tmp, keep, true);
        var done = new List<string>();
        try
        {
            foreach (var rel in rels) { Put(SafeJoin(stage, rel), SafeJoin(root, rel)); done.Add(rel); progress?.Invoke(done.Count, rels.Count); }
        }
        catch
        {
            // 중간에 실패하면 바꾼 것을 되돌림
            using var kz = ZipFile.OpenRead(keep);
            foreach (var rel in done)
            {
                var e = kz.GetEntry(rel); if (e == null) continue;
                var t = SafeJoin(stage, rel + ".restore"); e.ExtractToFile(t, true); Put(t, SafeJoin(root, rel));
            }
            throw;
        }
        try { Directory.Delete(stage, true); } catch { }
        foreach (var old in Directory.GetFiles(updatesDir, "ComicPad_이전판_*.zip").OrderByDescending(File.GetLastWriteTime).Skip(3)) try { File.Delete(old); } catch { }
        return (cur, rels.Count);
    }

    /// <summary>지난 업데이트 때 비켜 둔 옛 파일(.cpold) 지우기</summary>
    public static void CleanupOld(string root)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*" + OldSuffix, SearchOption.TopDirectoryOnly)) try { File.Delete(f); } catch { }
            foreach (var dir in Directory.GetDirectories(root).Where(d => !Keep.Any(k => Path.GetFileName(d).Equals(k.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))))
                foreach (var f in Directory.EnumerateFiles(dir, "*" + OldSuffix, SearchOption.AllDirectories)) try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // ---- 설정 (settings.update = { auto, checked, skip, just, last }) ------------------------
    public static JsonObject Conf() { if (App.Settings["update"] is not JsonObject u) { u = new JsonObject { ["auto"] = true }; App.Settings["update"] = u; } return u; }

    public static (string from, string to, string zip)? RollbackInfo(string root, string updatesDir)
    {
        var last = Conf()["last"] as JsonObject;
        if (last == null || (string)last["to"] != VersionIn(root)) return null;
        string z = Path.Combine(updatesDir, $"ComicPad_이전판_{(string)last["from"]}.zip");
        return File.Exists(z) ? ((string)last["to"], (string)last["from"], z) : null;
    }
}

/// <summary>업데이트 창 + 자동 확인</summary>
public static class UpdateUi
{
    static string Mb(long b) => $"{b / 1e6:0.0}MB";

    public static void Dialog(UpdateInfo pre = null)
    {
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(10) };
        using var f = Shell2Ui.Modal("업데이트", body, 600, 520);
        UpdateInfo info = pre; bool busy = false; string status = "";
        CancellationTokenSource cts = null;
        f.FormClosing += (_, _) => cts?.Cancel();
        string root = Updater.InstallRoot, upd = Updater.UpdatesDir();
        void Render()
        {
            if (f.IsDisposed) return;
            body.SuspendLayout();
            foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
            body.Controls.Add(new Label { Text = $"지금 버전: {Updater.CurrentVersion}  (설치 폴더: {root})", AutoSize = true, MaximumSize = new Size(540, 0) });
            body.Controls.Add(Opt.Title("자동 업데이트"));
            body.Controls.Add(Opt.Check("켤 때 새 버전 확인 (하루 한 번, 인터넷 연결 시)", () => Updater.Conf()["auto"]?.GetValue<bool>() != false, v => { Updater.Conf()["auto"] = v; App.SaveSettingsSoon(); }));
            body.Controls.Add(Wide(Opt.Help($"GitHub ({Updater.Repo}) 의 최신 릴리스를 봅니다. 만든 사람의 서명(RSA-3072)이 맞는 파일만 받고, 받은 파일의 SHA-256 도 확인합니다. 작업물(data)은 건드리지 않습니다.")));
            if (info != null)
            {
                body.Controls.Add(Opt.Title(info.Available ? $"새 버전 {info.Latest}" : "최신 버전입니다"));
                if (info.Available) body.Controls.Add(Wide(Opt.Help($"받을 크기 {Mb(info.Size)}{(info.Date.Length >= 10 ? " · " + info.Date[..10] : "")} · {(info.InstallerUrl != null ? "설치파일을 받아 실행합니다 (앱이 닫힘)" : "프로그램 파일을 바꿉니다 (이전 판은 data\\updates 에 보관)")}")));
                if (!string.IsNullOrEmpty(info.Notes)) body.Controls.Add(new TextBox { Text = info.Notes.Replace("\n", "\r\n"), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 540, Height = 120, BackColor = Opt.Bg2, ForeColor = Opt.Fg });
            }
            var rb = Updater.RollbackInfo(root, upd);
            if (rb != null)
            {
                body.Controls.Add(Opt.Title("되돌리기"));
                body.Controls.Add(Wide(Opt.Help($"지금 {rb.Value.from} 에 문제가 있으면 업데이트 전 {rb.Value.to} 로 되돌릴 수 있습니다. 작업물(data)은 그대로입니다.")));
                body.Controls.Add(Opt.Button($"{rb.Value.to} 로 되돌리기", () => Rollback(rb.Value)));
            }
            if (status.Length > 0) body.Controls.Add(Wide(new Label { Text = status, AutoSize = true, ForeColor = status.StartsWith("⚠") ? Color.FromArgb(240, 120, 100) : Opt.Fg }));
            var row = Opt.Row(Opt.Button("지금 확인", CheckNow), info?.Available == true ? Opt.Button("받아서 적용", Install, true) : null,
                info?.Page?.StartsWith("https://github.com/") == true ? Opt.Button("릴리스 페이지", () => Shell2Ui.OpenUrl(info.Page)) : null);
            foreach (Control c in row.Controls) c.Enabled = !busy;
            body.Controls.Add(row);
            body.ResumeLayout();
        }
        void SetStatus(string s) { status = s; if (!f.IsDisposed) f.BeginInvoke((Action)Render); }
        async void CheckNow()
        {
            busy = true; status = "확인하는 중…"; Render();
            try { info = await Updater.Check(); status = ""; }
            catch (Exception e) { status = "⚠ " + e.Message; }
            busy = false; if (!f.IsDisposed) Render();
        }
        async void Install()
        {
            if (info?.Man == null) return;
            if (!Updater.Newer(info.Man.Version, Updater.CurrentVersion)) { status = "⚠ 이미 최신판입니다"; Render(); return; }
            if (info.InstallerUrl == null && Directory.Exists(Path.Combine(root, ".git"))) { status = "⚠ 개발 폴더(.git 이 있는 곳)에는 업데이트를 적용하지 않습니다"; Render(); return; }
            if (!SaveFirst()) return;
            busy = true; cts = new CancellationTokenSource(); Render();
            try
            {
                var m = info.Man;
                if (info.InstallerUrl != null)
                {
                    string dest = Path.Combine(upd, m.InstallerFile);
                    await Updater.Download(info.InstallerUrl, dest, m.InstallerSize, m.InstallerSha256, (d, t) => SetStatus($"받는 중… {d * 100 / Math.Max(1, t)}%"), cts.Token);
                    RecordJust(Updater.CurrentVersion, m);
                    SetStatus("설치파일을 실행합니다. ComicPad 를 닫습니다…");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dest) { UseShellExecute = true, WorkingDirectory = upd });
                    f.Close();
                    App.Main?.Close();
                    return;
                }
                string zip = Path.Combine(upd, m.File);
                await Updater.Download(info.ZipUrl, zip, m.Size, m.Sha256, (d, t) => SetStatus($"받는 중… {d * 100 / Math.Max(1, t)}%"), cts.Token);
                SetStatus("적용하는 중…");
                var (from, n) = await Task.Run(() => Updater.ApplyZip(zip, root, upd, (d, t) => SetStatus($"적용하는 중… {d}/{t}")));
                try { File.Delete(zip); } catch { }
                RecordJust(from, m);
                info = null; busy = false;
                SetStatus($"{from} → {m.Version} 적용 완료 (파일 {n}개). 이전 판은 data\\updates 에 남겨 두었습니다. ComicPad 를 다시 시작하면 반영됩니다.");
                if (MessageBox.Show(f, "업데이트를 적용했습니다. 지금 ComicPad 를 다시 시작할까요?", "업데이트", MessageBoxButtons.YesNo) == DialogResult.Yes) Restart();
            }
            catch (OperationCanceledException) { busy = false; SetStatus("⚠ 취소했습니다"); }
            catch (Exception e) { busy = false; SetStatus("⚠ " + e.Message); }
        }
        async void Rollback((string from, string to, string zip) rb)
        {
            if (!Shell2Ui.Confirm($"{rb.to} 로 되돌릴까요? 프로그램 파일만 바뀌고 작업물은 그대로입니다.", "되돌리기")) return;
            if (!SaveFirst()) return;
            busy = true; Render();
            try
            {
                var (cur, n) = await Task.Run(() => Updater.ApplyZip(rb.zip, root, upd));
                try { File.Delete(rb.zip); } catch { }
                var u = Updater.Conf(); u["last"] = new JsonObject(); u["just"] = new JsonObject(); u["skip"] = cur; App.SaveSettingsSoon();
                busy = false; SetStatus($"{cur} → {rb.to} 로 되돌렸습니다 (파일 {n}개). 다시 시작하면 반영됩니다.");
            }
            catch (Exception e) { busy = false; SetStatus("⚠ 되돌리기 실패: " + e.Message); }
        }
        Render();
        f.ShowDialog(App.Main);
    }

    static Control Wide(Label l) { l.MaximumSize = new Size(540, 0); return l; }

    static void RecordJust(string from, UpdateManifest m)
    {
        var u = Updater.Conf();
        u["just"] = new JsonObject { ["from"] = from, ["to"] = m.Version, ["notes"] = m.Notes };
        u["last"] = new JsonObject { ["from"] = from, ["to"] = m.Version };
        u["skip"] = "";
        App.SaveSettings();
    }

    static bool SaveFirst()
    {
        if (App.Doc?.Modified != true) return true;
        var r = MessageBox.Show(App.Main, "저장하지 않은 작업이 있습니다. 저장하고 업데이트할까요?", "업데이트", MessageBoxButtons.YesNoCancel);
        if (r == DialogResult.Cancel) return false;
        return r == DialogResult.No || FileCommands.Save(false);
    }

    static void Restart()
    {
        try
        {
            App.SaveSettings();
            string exe = Environment.ProcessPath;
            if (App.Doc?.FilePath != null && !App.Doc.Modified) System.Diagnostics.Process.Start(exe, $"\"{App.Doc.FilePath}\"");
            else System.Diagnostics.Process.Start(exe);
            if (App.Doc != null) App.Doc.Modified = false;
            App.Main?.Close();
        }
        catch (Exception e) { App.Toast("다시 시작하지 못했습니다: " + e.Message); }
    }

    /// <summary>업데이트 뒤 처음 켤 때 한 번: 바뀐 점</summary>
    public static void WhatsNew()
    {
        var u = Updater.Conf();
        if (u["just"] is not JsonObject j || (string)j["to"] == null || (string)j["to"] != Updater.CurrentVersion) return;
        u.Remove("just"); App.SaveSettingsSoon();
        MessageBox.Show(App.Main, $"{(string)j["from"]} → {(string)j["to"]}\n\n{((string)j["notes"] is { Length: > 0 } n ? n : "바뀐 점 설명이 없습니다.")}\n\n문제가 있으면 도움말 > 업데이트 확인 에서 이전 버전으로 되돌릴 수 있습니다.", $"ComicPad {(string)j["to"]} 로 업데이트되었습니다");
    }

    /// <summary>켤 때 하루 한 번 조용히 확인</summary>
    public static async void AutoCheck()
    {
        try
        {
            var u = Updater.Conf();
            if (u["auto"]?.GetValue<bool>() == false) return;
            long checkedAt = u["checked"] is JsonValue cv && cv.TryGetValue<long>(out var c) ? c : 0;
            if (DateTimeOffset.Now.ToUnixTimeSeconds() - checkedAt < 20 * 3600) return;
            var info = await Updater.Check();
            if (!info.Available || info.Latest == (string)u["skip"]) return;
            App.Toast($"ComicPad 새 버전 {info.Latest} 이 나왔습니다 — 도움말 > 업데이트 확인");
            Shell2Ui.Notify($"ComicPad 새 버전 {info.Latest}", "업데이트", () => Dialog(info));
        }
        catch { /* 인터넷 없음·릴리스 없음 → 조용히 */ }
    }
}
