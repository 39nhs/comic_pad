// Shell2 시험 (창 없이, 정해진 결과). 사용자 설정·작업물은 건드리지 않는다 (COMICPAD_DATA 필수, App.Settings 는 새것, 설정 파일에 쓰지 않음).
// 실제 업데이트·네트워크는 하지 않는다: 서명은 시험용 키를 여기서 만들어 확인, 꾸러미 적용은 임시 폴더에.
//   예) COMICPAD_DATA=%TEMP%\cps COMICPAD_SHELL2_SELFTEST=%TEMP%\cps-out ComicPadNative.exe --render x y
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Extras;

namespace ComicPadNative.Modules.Shell2;

public static class Shell2Test
{
    static readonly StringBuilder log = new();
    static int fail;
    static void Ok(bool cond, string name) { log.AppendLine((cond ? "통과  " : "실패  ") + name); if (!cond) fail++; }

    // 다른 작업(애니·내보내기, AI)에서 만들 메뉴라 아직 없을 수 있는 웹 id (없어도 실패로 치지 않음, 목록만 남김)
    static readonly HashSet<string> MaybeMissing = new() { "file.ebook", "file.printExport", "file.webtoonExport", "view.cmykProof", "timelapse.toggle", "timelapse.export", "timelapse.clear" };
    static bool Maybe(string id) => MaybeMissing.Contains(id) || id.StartsWith("anim.") || id.StartsWith("ai.") || id.StartsWith("vt.");

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMICPAD_DATA"))) { File.WriteAllText(Path.Combine(outDir, "result.txt"), "COMICPAD_DATA 를 임시 폴더로 정해 주세요"); return 2; }
            // 설정 파일 저장이 일어나도 (DoEvents 로 저장 타이머가 돌 수 있음) 사용자 설정 파일이 아니라 시험 폴더로
            Environment.SetEnvironmentVariable("COMICPAD_SETTINGS", Path.Combine(outDir, "settings-test.json"));
            App.Settings = new JsonObject();
            // 다른 모듈 모두 등록 (이 모듈 빼고 — 시험으로 끝나는 모듈은 그 환경 변수가 없으니 그냥 등록만 됨)
            foreach (var t in typeof(App).Assembly.GetTypes().Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface && t != typeof(Shell2Module)))
                try { ((IModule)Activator.CreateInstance(t)).Init(); } catch (Exception ex) { log.AppendLine($"(모듈 {t.Name} 등록 오류: {ex.Message})"); }
            Shell2Module.RegisterAll();
            App.Settings = new JsonObject();
            App.SetDoc(Document.New(200, 150, "시험"));
            Dump(outDir);
            void T(string n, Action a) { try { a(); } catch (Exception ex) { Ok(false, $"{n}: 예외 {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}"); } }
            T("업데이트 서명", TestSignature);
            T("업데이트 정보 검사", TestManifest);
            T("업데이트 꾸러미 적용·되돌리기", () => TestApply(outDir));
            T("웹 명령 id", () => TestWebIds(outDir));
            T("설정 왕복", TestSettings);
            T("자동 액션 (웹 id 재생)", TestMacro);
            T("작업 화면", TestWorkspace);
            T("레이어 명령", TestLayers);
            T("원형 메뉴", TestRadial);
            T("간단히 모드", TestUiMode);
            T("퀵 동작", TestQuick);
            T("백업 꺼내기", TestBackup);
            T("문제 보내기", TestReport);
            T("빠른 내보내기·에셋", () => TestExport(outDir));
            T("펜 도구 (패스)", TestPaths);
            T("복제 도장·유동화·벡터 선 고르기", TestTools);
        }
        catch (Exception ex) { Ok(false, "시험 실행 오류: " + ex); }
        log.AppendLine(fail == 0 ? "모두 통과" : $"실패 {fail}개");
        File.WriteAllText(Path.Combine(outDir, "result.txt"), log.ToString());
        return fail == 0 ? 0 : 1;
    }

    /// <summary>등록된 메뉴·도구·패널 목록 (기능 대조표 만들 때 씀)</summary>
    static void Dump(string outDir)
    {
        var sb = new StringBuilder();
        foreach (var m in Registry.Menus.OrderBy(m => m.Path.Split('/')[0]).ThenBy(m => m.Order)) if (m.Run != null) sb.AppendLine($"menu\t{m.Path}\t{(m.Shortcut != Keys.None ? KeyMap.Format(m.Shortcut) : "")}");
        foreach (var t in Registry.Tools) sb.AppendLine($"tool\t{t.Id}\t{t.Name}\t{t.Key}");
        foreach (var p in Registry.Panels) sb.AppendLine($"panel\t{p.Title}\t{p.Dock}");
        foreach (var (k, v) in WebIds.Map) sb.AppendLine($"alias\t{k}\t{v}\t{(Registry.Commands.ContainsKey(v) ? "ok" : "missing")}");
        File.WriteAllText(Path.Combine(outDir, "registry.tsv"), sb.ToString());
    }

    // ---- 업데이트 -----------------------------------------------------------------------------
    static (RSAParameters pub, RSA rsa) TestKey(int bits) { var rsa = RSA.Create(bits); return (rsa.ExportParameters(false), rsa); }
    static byte[] Sign(RSA rsa, byte[] data) => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    static void TestSignature()
    {
        var (pub, rsa) = TestKey(3072);
        var man = Encoding.UTF8.GetBytes("{\"product\":\"ComicPad\",\"version\":\"9.9.9\"}");
        var sig = Sign(rsa, man);
        string hex = Convert.ToHexString(sig).ToLowerInvariant();   // make_release.py 와 같은 모양 (소문자 16진)
        var keys = new List<RSAParameters> { pub };
        Ok(Updater.VerifyAny(man, Updater.Hex(hex), keys), "맞는 서명 받아들임 (RSA-3072, PKCS#1 v1.5, SHA-256)");
        var bad = (byte[])man.Clone(); bad[^2] ^= 1;
        Ok(!Updater.VerifyAny(bad, Updater.Hex(hex), keys), "내용을 1비트 바꾸면 거부");
        var badSig = (byte[])sig.Clone(); badSig[10] ^= 0x40;
        Ok(!Updater.VerifyAny(man, badSig, keys), "서명을 바꾸면 거부");
        Ok(!Updater.VerifyAny(man, sig[..^1], keys) && !Updater.VerifyAny(man, Updater.Hex(hex + "0"), keys) && Updater.Hex("zz").Length == 0, "길이가 틀린·16진이 아닌 서명 거부");
        var real = Updater.TrustedKeys();
        Ok(real.Count >= 1 && real.All(k => Updater.Bits(k) >= 3072), "믿는 공개키 (release_pubkey.json 또는 내장) 3072비트 이상");
        Ok(!Updater.VerifyAny(man, sig, real), "다른 키(시험 키)로 서명한 것은 진짜 키로 거부");
        var (pub2048, rsa2048) = TestKey(2048);
        Ok(!Updater.VerifyAny(man, Sign(rsa2048, man), new[] { pub2048 }), "3072비트 미만 키는 믿지 않음");
        // 설치 폴더의 release_pubkey.json 과 내장 키가 같은 키인지 (저장소에서 실행할 때만 확인)
        string pk = Path.Combine(Updater.InstallRoot, "release_pubkey.json");
        if (File.Exists(pk))
        {
            var n = (string)JsonNode.Parse(File.ReadAllText(pk))["n"];
            var embedded = typeof(Updater).GetField("EmbeddedN", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as string;
            Ok(n == embedded, "내장 공개키 = release_pubkey.json");
        }
        Ok(Updater.Newer("1.2.0", "1.1.9") && !Updater.Newer("1.1.3", "1.1.3") && Updater.Newer("1.10.0", "1.9.9") && !Updater.Newer("0.9", "1.0.0"), "버전 비교");
        rsa.Dispose(); rsa2048.Dispose();
    }

    static byte[] Man(string extra = "", string file = "ComicPad_update_2.0.0.zip", string sha = null, long size = 1234, string product = "ComicPad", string ver = "2.0.0")
        => Encoding.UTF8.GetBytes($"{{\"product\":\"{product}\",\"version\":\"{ver}\",\"file\":\"{file}\",\"size\":{size},\"sha256\":\"{sha ?? new string('a', 64)}\",\"notes\":\"고침\"{extra}}}");

    static bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }

    static void TestManifest()
    {
        var assets = new HashSet<string> { "ComicPad_update_2.0.0.zip", "update.json", "update.json.sig", "ComicPad_Setup_2.0.0.exe" };
        var m = Updater.ParseManifest(Man(), assets);
        Ok(m.Version == "2.0.0" && m.Size == 1234 && m.File == "ComicPad_update_2.0.0.zip" && m.Notes == "고침" && m.InstallerFile == null, "올바른 update.json");
        Ok(Throws(() => Updater.ParseManifest(Man(product: "Other"), assets)), "다른 프로그램용 거부");
        Ok(Throws(() => Updater.ParseManifest(Man(file: "evil.exe"), new HashSet<string> { "evil.exe" })), "이상한 파일 이름 거부");
        Ok(Throws(() => Updater.ParseManifest(Man(file: "ComicPad_update_3.0.0.zip"), assets)), "릴리스에 없는 파일 거부");
        Ok(Throws(() => Updater.ParseManifest(Man(sha: "XYZ"), assets)), "SHA-256 형식 틀림 거부");
        Ok(Throws(() => Updater.ParseManifest(Man(size: 0), assets)) && Throws(() => Updater.ParseManifest(Man(size: Updater.MaxPackage + 1), assets)), "크기 0·너무 큼 거부");
        Ok(Throws(() => Updater.ParseManifest(Man(ver: "2.0"), assets)), "버전 형식 틀림 거부");
        var mi = Updater.ParseManifest(Man($",\"installer\":{{\"file\":\"ComicPad_Setup_2.0.0.exe\",\"size\":99,\"sha256\":\"{new string('b', 64)}\"}}"), assets);
        Ok(mi.InstallerFile == "ComicPad_Setup_2.0.0.exe" && mi.InstallerSize == 99, "서명된 설치파일 정보");
        Ok(Throws(() => Updater.ParseManifest(Man($",\"installer\":{{\"file\":\"setup.bat\",\"size\":99,\"sha256\":\"{new string('b', 64)}\"}}"), new HashSet<string>(assets) { "setup.bat" })), "설치파일 이름이 이상하면 거부");
    }

    static void TestApply(string outDir)
    {
        string root = Path.Combine(outDir, "설치폴더"), upd = Path.Combine(outDir, "updates");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (Directory.Exists(upd)) Directory.Delete(upd, true);
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "VERSION"), "1.0.0");
        File.WriteAllText(Path.Combine(root, "a.txt"), "옛것");
        File.WriteAllText(Path.Combine(root, "data", "작품.txt"), "내 작품");
        string zip = Path.Combine(outDir, "ComicPad_update_1.0.1.zip");
        if (File.Exists(zip)) File.Delete(zip);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void E(string name, string text) { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write(text); }
            E("VERSION", "1.0.1"); E("a.txt", "새것"); E("sub/b.txt", "비"); E("data/작품.txt", "덮어쓰기 시도"); E("../밖.txt", "탈출"); E("python/x.py", "x");
        }
        // 받은 파일 확인 (SHA-256·크기)
        string sha = Updater.Sha256File(zip); long size = new FileInfo(zip).Length;
        Ok(Updater.CheckFile(zip, size, sha) && !Updater.CheckFile(zip, size, new string('0', 64)) && !Updater.CheckFile(zip, size + 1, sha), "받은 파일 SHA-256·크기 확인");
        var (from, n) = Updater.ApplyZip(zip, root, upd);
        Ok(from == "1.0.0" && n == 3 && Updater.VersionIn(root) == "1.0.1" && File.ReadAllText(Path.Combine(root, "a.txt")) == "새것" && File.Exists(Path.Combine(root, "sub", "b.txt")), "꾸러미 적용 (VERSION·파일)");
        Ok(File.ReadAllText(Path.Combine(root, "data", "작품.txt")) == "내 작품" && !File.Exists(Path.Combine(outDir, "밖.txt")) && !Directory.Exists(Path.Combine(root, "python")), "data·python·바깥 경로는 건드리지 않음");
        string keep = Path.Combine(upd, "ComicPad_이전판_1.0.0.zip");
        Ok(File.Exists(keep), "바꾸기 전 판 보관");
        Updater.Conf()["last"] = new JsonObject { ["from"] = "1.0.0", ["to"] = "1.0.1" };
        var rb = Updater.RollbackInfo(root, upd);
        Ok(rb != null && rb.Value.to == "1.0.0", "되돌리기 가능");
        Updater.ApplyZip(rb.Value.zip, root, upd);
        Ok(Updater.VersionIn(root) == "1.0.0" && File.ReadAllText(Path.Combine(root, "a.txt")) == "옛것", "되돌리기 (1.0.1 → 1.0.0)");
    }

    // ---- 웹 명령 id ---------------------------------------------------------------------------
    static void TestWebIds(string outDir)
    {
        var missing = WebIds.Map.Where(kv => !Registry.Commands.ContainsKey(kv.Value)).Select(kv => kv.Key).ToList();
        File.WriteAllLines(Path.Combine(outDir, "alias-missing.txt"), missing);
        var unexpected = missing.Where(m => !Maybe(m)).ToList();
        Ok(unexpected.Count == 0, "웹 명령 id → 네이티브 메뉴 (없는 것: " + (unexpected.Count == 0 ? "없음" : string.Join(", ", unexpected)) + ")");
        Ok(KeyMap.ResolveCmd("view.fitWidth") == "보기/가로 폭에 맞춤" && KeyMap.ResolveCmd("layer.flatten") == "레이어/보이는 레이어 결합", "KeyMap 별칭에 합쳐짐 (단축키·.cpset)");
        App.Settings["keys"] = new JsonObject { ["view.grid"] = "ctrl+alt+shift+g" };
        bool before = App.Get("showGrid", false);
        bool handled = KeyMap.Handle(Keys.Control | Keys.Alt | Keys.Shift | Keys.G);
        Ok(handled && App.Get("showGrid", false) != before, "웹 id 로 정한 단축키 실행 (view.grid)");
        App.Settings.Remove("keys"); App.Settings.Remove("showGrid");
    }

    // ---- 설정 ----------------------------------------------------------------------------------
    static void TestSettings()
    {
        var s = App.Settings;
        s["ui"] = new JsonObject { ["mode"] = "full", ["tools"] = new JsonArray("brush", "view"), ["menuPick"] = new JsonObject { ["view"] = new JsonArray("view.fit") }, ["hiddenPanels"] = new JsonArray("history") };
        s["quickbar"] = new JsonObject { ["visible"] = true, ["x"] = 30, ["y"] = 40, ["items"] = new JsonArray("undo", "size") };
        s["bottombar"] = new JsonObject { ["visible"] = false, ["items"] = new JsonArray("save") };
        s["radialItems"] = new JsonArray("tool:brush", "cmd:edit.undo");
        s["brushCursor"] = "dot"; s["wheelMode"] = "scroll"; s["fingerDraw"] = true; s["largeUI"] = true; s["update"] = new JsonObject { ["auto"] = false };
        string text = s.ToJsonString();
        var back = JsonNode.Parse(text) as JsonObject;
        Ok(back.ToJsonString() == text && (string)back["ui"]["mode"] == "full" && (bool)back["quickbar"]["visible"] && (int)back["quickbar"]["x"] == 30, "설정 JSON 왕복");
        // .cpset 화면 구성 부분에 웹 판과 같은 키로 들어가고, 가져오면 그대로
        var file = Setup.BuildSetFile(s, new[] { "layout" });
        var lay = file["parts"]["layout"] as JsonObject;
        Ok(lay?["quickbar"] != null && lay["bottombar"] != null && lay["radialItems"] != null && lay["ui"] != null && lay["largeUI"] != null, ".cpset 화면 구성에 퀵바·아래 막대·원형 메뉴·화면 구성·큰 버튼");
        var (next, changed) = Setup.MergeSetFile(new JsonObject(), JsonNode.Parse(file.ToJsonString()) as JsonObject, new[] { "layout" });
        Ok(changed.Contains("quickbar") && U.Same(next["radialItems"], s["radialItems"]) && U.Same(next["ui"], s["ui"]), ".cpset 가져오기 왕복");
        Ok(CursorLook.For(Registry.Tools.Find(t => t.Id == "brush")) != null && CursorLook.For(Registry.Tools.Find(t => t.Id == "hand")) == null, "브러시 커서 (점) 만들기, 브러시가 아니면 그대로");
        s["brushCursor"] = "nib";
        Ok(CursorLook.For(Registry.Tools.Find(t => t.Id == "brush")) != null, "브러시 커서 (펜촉)");
        foreach (var k in new[] { "ui", "quickbar", "bottombar", "radialItems", "brushCursor", "wheelMode", "fingerDraw", "largeUI", "update" }) s.Remove(k);
    }

    // ---- 자동 액션: 웹 판 id 로 된 단계도 재생 -------------------------------------------------
    static void TestMacro()
    {
        var d = Document.New(120, 90, "매크로"); App.SetDoc(d);
        int n0 = d.Layers.Count;
        var macro = new JsonObject { ["name"] = "웹에서 온 액션", ["steps"] = new JsonArray(
            new JsonObject { ["cmd"] = "layer.newRough", ["args"] = new JsonArray(), ["forms"] = new JsonArray() },
            new JsonObject { ["cmd"] = "layer.newColor", ["args"] = new JsonArray(), ["forms"] = new JsonArray() },
            new JsonObject { ["cmd"] = "레이어/폴더로 묶기", ["args"] = new JsonArray(), ["forms"] = new JsonArray() }) };
        Macros.Play(macro);
        Ok(d.Layers.Count == n0 + 3 && d.Layers.Any(l => l.Rough && l.Tint != null && l.Draft) && d.Layers.Any(l => l.Clip && l.Name == "채색") && d.Active.IsFolder, "웹 id·메뉴 경로가 섞인 자동 액션 재생");
        // 기록 → 재생
        Macros.WrapCommands();
        Macros.StartRecord();
        Registry.Commands["레이어/새 펜선 레이어"]();
        Registry.Commands["보기/격자 보기"]();          // 보기 명령은 기록하지 않음 (웹 판과 같음)
        Registry.Commands["레이어/레이어 컬러 (파란 선으로 보기)"]();
        Dlg.FormAuto = t => t == "자동 액션 저장" ? new JsonObject { ["name"] = "시험 기록" } : null;
        Macros.StopRecord();
        Dlg.FormAuto = null;
        var rec = (App.Settings["macros"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => (string)m["name"] == "시험 기록");
        Ok(rec != null && (rec["steps"] as JsonArray)?.Count == 2 && (string)rec["steps"][0]["cmd"] == "레이어/새 펜선 레이어", "새 명령 기록");
        int n1 = d.Layers.Count;
        Macros.Play(rec);
        Ok(d.Layers.Count == n1 + 1 && d.Active.Name == "펜선" && d.Active.Tint == "#3d7be0", "기록한 액션 다시 실행");
        App.Settings.Remove("macros"); App.Settings.Remove("showGrid");
    }

    // ---- 작업 화면 ----------------------------------------------------------------------------
    static void TestWorkspace()
    {
        var s = App.Settings;
        s["quickbar"] = new JsonObject { ["visible"] = true, ["x"] = 5, ["y"] = 6 };
        s["bottombar"] = new JsonObject { ["visible"] = false };
        s["leftHanded"] = true;
        App.SetTool("brush");
        var w = Workspaces.Snapshot("펜선");
        s["quickbar"] = new JsonObject { ["visible"] = false }; s["bottombar"] = new JsonObject { ["visible"] = true }; s["leftHanded"] = false;
        App.SetTool("eraser");
        var saved = JsonNode.Parse(w.ToJsonString()) as JsonObject;   // 설정 파일에 저장했다 읽은 것처럼
        Workspaces.Apply(saved);
        Ok((bool)s["quickbar"]["visible"] && (int)s["quickbar"]["x"] == 5 && (bool)s["bottombar"]["visible"] == false && (bool)s["leftHanded"] && App.Tool?.Id == "brush", "작업 화면 저장 → 바꾸기 (퀵바·아래 막대·왼손·도구)");
        foreach (var k in new[] { "quickbar", "bottombar", "leftHanded", "workspaces" }) s.Remove(k);
    }

    // ---- 레이어 명령 ---------------------------------------------------------------------------
    static void Paint(Layer l, int x, int y, int w, int h, uint c) { var r = new Rect(x, y, w, h); var b = new uint[w * h]; Array.Fill(b, c); l.Px.Write(r, b); l.Touch(); }

    static void TestLayers()
    {
        var d = Document.New(64, 48, "레이어"); App.SetDoc(d);
        Paint(d.Layers[0], 0, 0, 10, 10, 0xFFFF0000);
        var rough = MoreCommands.NewRough();
        Ok(rough.Rough && rough.Draft && Math.Abs(rough.Opacity - 0.6f) < 1e-6 && rough.Tint == "#3d7be0" && rough.Name == "러프 (파랑)", "새 러프 레이어 (웹 판과 같은 속성)");
        App.History.Undo(d);
        Ok(!d.Layers.Contains(rough), "새 러프 레이어 실행취소");
        var col = MoreCommands.NewColor();
        Ok(col.Clip && col.Name == "채색", "새 채색 레이어 (클리핑)");
        Paint(col, 20, 20, 5, 5, 0xFF0000FF);
        MoreCommands.ToggleTint();
        Ok(col.Tint == "#3d7be0", "레이어 컬러 켜기");
        MoreCommands.ToggleTint();
        MoreCommands.GroupIntoFolder();
        var f = d.Active;
        Ok(f.IsFolder && col.Parent == f.Id, "폴더로 묶기");
        MoreCommands.MergeFolder();
        var merged = d.Active;
        Ok(!merged.IsFolder && !d.Layers.Contains(f) && !d.Layers.Contains(col) && merged.Px.Get(0, 0) == 0, "폴더를 한 레이어로");
        App.History.Undo(d);
        Ok(d.Layers.Contains(f) && d.Layers.Contains(col), "폴더 합치기 실행취소");
        // 보이는 레이어 결합
        var d2 = Document.New(32, 32, "결합"); App.SetDoc(d2);
        Paint(d2.Layers[0], 0, 0, 4, 4, 0xFF00FF00);
        var top = MoreCommands.AddLayer("위", null); Paint(top, 2, 2, 4, 4, 0xFFFF0000);
        MoreCommands.FlattenVisible();
        Ok(d2.Layers.Count == 1 && d2.Layers[0].Name == "결합" && d2.Layers[0].Px.Get(0, 0) == 0xFF00FF00 && d2.Layers[0].Px.Get(3, 3) == 0xFFFF0000, "보이는 레이어 결합");
        // 지우기 (선택 영역 안만)
        d2.Sel.Mask = new byte[32 * 32]; d2.Sel.Mask[0] = 255;
        MoreCommands.ClearLayer();
        Ok(d2.Layers[0].Px.Get(0, 0) == 0 && d2.Layers[0].Px.Get(1, 1) == 0xFF00FF00, "레이어 지우기 (선택 영역 안만)");
        d2.Sel.None();
        // 마스크 적용
        var l = d2.Layers[0];
        l.Mask = new Surface(32, 32);
        var mb = new uint[32 * 32]; for (int i = 0; i < mb.Length; i++) mb[i] = i % 32 < 16 ? 0xFFFFFFFF : 0u;
        l.Mask.Write(new Rect(0, 0, 32, 32), mb);
        MoreCommands.ApplyMask();
        Ok(l.Mask == null && l.Px.Get(1, 1) == 0xFF00FF00 && l.Px.Get(3, 3) == 0xFFFF0000, "마스크 적용 (보이는 쪽 남김)");
        Paint(l, 20, 20, 2, 2, 0xFF123456);
        l.Mask = new Surface(32, 32);
        MoreCommands.ApplyMask();
        Ok(l.Px.Get(20, 20) == 0, "마스크 적용 (숨긴 쪽 지움)");
        App.History.Undo(d2);
        Ok(l.Mask != null && l.Px.Get(20, 20) == 0xFF123456, "마스크 적용 실행취소");
        MoreCommands.DeleteMask();
        Ok(l.Mask == null, "마스크 삭제");
    }

    static void TestRadial()
    {
        Ok(Radial.HotIndex(0, -60, 8) == 0 && Radial.HotIndex(60, 0, 8) == 2 && Radial.HotIndex(0, 60, 8) == 4 && Radial.HotIndex(-60, 0, 8) == 6 && Radial.HotIndex(10, 5, 8) == -1, "원형 메뉴 방향 → 항목 (웹 판과 같은 계산)");
        var a = Radial.Describe("tool:view"); var b = Radial.Describe("cmd:edit.undo"); var c = Radial.Describe("cmd:없는.명령");
        Ok(a?.Label == "화면 이동" && b?.Label == "실행취소" && c == null, "원형 메뉴 항목 (웹 도구 id·명령 id)");
        Ok(Radial.Items().SequenceEqual(Radial.DefaultItems), "원형 메뉴 기본 항목");
    }

    static void TestUiMode()
    {
        App.Settings.Remove("ui");
        Ok(UiMode.IsSimple && UiMode.MenuShown("파일") && !UiMode.MenuShown("애니") && UiMode.MenuShown("AI"), "간단히: 애니 메뉴 숨김, 파일·AI 보임");
        var pick = UiMode.Pick("파일");
        Ok(UiMode.KeyMatches(pick, "파일/저장") && UiMode.KeyMatches(pick, "파일/설정…") && !UiMode.KeyMatches(pick, "파일/PNG 빠른 내보내기"), "간단히: 파일 메뉴 항목 고르기 (웹 id 로)");
        Ok(UiMode.KeyMatches(UiMode.Pick("만화"), "만화/말풍선 도구"), "간단히: 이름(@말풍선 도구) 항목");
        UiMode.Ui()["mode"] = "full";
        Ok(!UiMode.IsSimple && UiMode.MenuShown("애니") && UiMode.Pick("파일") != null, "모든 기능 보기");
        UiMode.Ui()["menus"] = new JsonObject { ["anim"] = false };
        Ok(!UiMode.MenuShown("애니"), "메뉴 하나 숨기기");
        App.Settings.Remove("ui");
    }

    static void TestQuick()
    {
        var all = Bars.DefaultQuick.Concat(Bars.DefaultBottom).Distinct().ToList();
        Ok(all.All(id => QuickActions.Get(id) != null), "퀵바·아래 막대 기본 버튼이 모두 있음");
        App.SetTool("brush");
        QuickActions.Get("penEraser").Run();
        Ok(App.Tool?.Id == "eraser" && QuickActions.Get("penEraser").Active(), "펜↔지우개");
        QuickActions.Get("penEraser").Run();
        uint f = App.Fg, b = App.Bg;
        QuickActions.Get("swap").Run();
        Ok(App.Fg == b && App.Bg == f, "색 바꾸기 (X)");
        QuickActions.Get("swap").Run();
    }

    static void TestBackup()
    {
        string proj = Path.Combine(App.DataDir, "projects"); Directory.CreateDirectory(proj);
        string file = Path.Combine(proj, "백업시험.cpad");
        File.WriteAllText(file, "원래");
        App.Settings["backup"] = new JsonObject { ["keep"] = 3 };
        var zip = UI.Shell.MakeBackup();
        Ok(zip != null && BackupUi.Contents(zip).Any(x => x.name == "projects/백업시험.cpad"), "백업 zip 에 작품");
        File.WriteAllText(file, "바뀜");
        var restored = BackupUi.Restore(zip, "projects/백업시험.cpad");
        Ok(File.ReadAllText(restored) == "원래" && File.ReadAllText(file) == "바뀜" && Path.GetFileName(restored).StartsWith("백업시험 (복원 "), "꺼내기: 덮어쓰지 않고 (복원 날짜) 새 파일");
        bool rejected = false;
        try { BackupUi.Restore(zip, "config/../../밖.txt"); } catch (InvalidDataException) { rejected = true; }
        Ok(rejected, "작품 폴더 밖 경로는 꺼내지 않음");
        App.Settings.Remove("backup");
    }

    static void TestReport()
    {
        string text = HelpUi.Collect();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Ok(text.Contains("## 환경") && text.Contains("네이티브") && (home.Length <= 3 || !text.Contains(home, StringComparison.OrdinalIgnoreCase)), "문제 보고 내용 (사용자 폴더 가림)");
        string url = HelpUi.IssueUrl("## 무슨 일\n펜이 안 돼요 & 끊겨요\n" + new string('가', 7000));
        Ok(url.StartsWith(HelpUi.RepoUrl + "/issues/new?title=") && !url.Contains(' ') && url.Contains(Uri.EscapeDataString("[문제] 펜이 안 돼요 & 끊겨요")) && url.Contains(Uri.EscapeDataString("길어서 잘림")), "GitHub 이슈 주소 (인코딩·길이 제한)");
        Ok(HelpUi.KeysText().Contains("두 손가락 탭") && HelpUi.KeysText().Contains("전경/배경색"), "단축키·제스처 안내 글");
    }

    static void TestPaths()
    {
        var d = Document.New(100, 80, "패스"); App.SetDoc(d);
        var tool = Registry.Tools.Find(t => t.Id == "pathpen");
        App.SetTool("pathpen"); PathPenTool.Cur = null;
        PointerArgs P(double x, double y, bool alt = false) => new() { X = x, Y = y, SX = x, SY = y, Buttons = 1, Alt = alt };
        tool.Down(P(10, 10)); tool.Up(P(10, 10));
        tool.Down(P(90, 10)); tool.Move(P(95, 30)); tool.Up(P(95, 30));     // 끌어서 곡선 점
        tool.Down(P(50, 70)); tool.Up(P(50, 70));
        tool.Down(P(10, 10)); tool.Up(P(10, 10));                           // 첫 점 탭 → 닫힘
        var l = Paths.Read(d);
        Ok(l.Count == 1 && l[0].Pts.Count == 3 && l[0].Closed && l[0].Pts[1].OX == 95 && l[0].Pts[1].IX == 85, "점 찍기·곡선 손잡이·닫기 (Extra.paths)");
        App.History.Undo(d);
        Ok(!Paths.Read(d)[0].Closed, "패스 고치기 실행취소");
        App.History.Redo(d);
        Paths.ToSelection(Paths.CurrentOrLast());
        Ok(d.Sel.At(50, 30) == 255 && d.Sel.At(2, 70) == 0, "패스 → 선택 영역");
        Paths.Fill(Paths.CurrentOrLast());
        Ok(d.Active.Px.Get(50, 30) == App.Fg && d.Active.Px.Get(2, 70) == 0, "패스 칠하기 (전경색)");
        var back = Paths.FromSelection();
        Ok(back != null && back.Closed && back.Pts.Count >= 3 && Paths.Read(d).Count == 2, "선택 영역 → 패스");
        d.Sel.None();
        var line = MoreCommands.AddLayer("선", null);
        Paths.Stroke(Paths.Read(d)[0]);
        Ok(line.Px.Bounds().W > 50, "패스 따라 선 그리기 (지금 브러시)");
        var saved = CpadIO.ToJson(d).ToJsonString();
        Ok(saved.Contains("\"paths\"") && saved.Contains("\"ox\""), ".cpad 에 paths 저장 (웹 판과 같은 모양)");
        App.SetTool("brush");
    }

    static void TestTools()
    {
        var d = Document.New(120, 80, "도구"); App.SetDoc(d);
        var l = d.Layers[0];
        Paint(l, 10, 10, 20, 20, 0xFFFF0000);
        PointerArgs P(double x, double y, bool alt = false) => new() { X = x, Y = y, SX = x, SY = y, Buttons = 1, Pressure = 1, Alt = alt };
        var clone = (CloneTool)Registry.Tools.Find(t => t.Id == "clone");
        clone.Size = 10; clone.Hardness = 0.97;
        clone.Down(P(20, 20, true)); clone.Up(P(20, 20, true));
        Ok(clone.Source == (20, 20) && !App.History.CanUndo, "복제 도장: Alt+클릭으로 원본 (그림은 안 바뀜)");
        clone.Down(P(80, 50)); clone.Move(P(81, 50)); clone.Up(P(81, 50));
        Ok(l.Px.Get(80, 50) == 0xFFFF0000 && l.Px.Get(10, 70) == 0, "복제 도장: 원본 그림이 복사됨");
        App.History.Undo(d);
        Ok(l.Px.Get(80, 50) == 0, "복제 도장 실행취소");
        var liq = (LiquifyTool)Registry.Tools.Find(t => t.Id == "liquify");
        liq.Size = 30; liq.Strength = 1;
        uint before = l.Px.Get(31, 20);
        liq.Down(P(22, 20)); for (int x = 23; x <= 34; x++) liq.Move(P(x, 20)); liq.Up(P(34, 20));
        Ok(before == 0 && l.Px.Get(31, 20) != 0, "픽셀 유동화: 밀면 그림이 따라 움직임");
        // 벡터 선 고르기
        var vl = MoreCommands.AddLayer("벡터", x => x.Meta = new JsonObject { ["strokes"] = new JsonArray() }, "vector");
        var stroke = new JsonObject { ["preset"] = new JsonObject { ["size"] = 4 }, ["color"] = "#000000", ["pts"] = new JsonArray(10, 60, 1, 0, 60, 60, 1, 8, 110, 60, 1, 16) };
        VectorLayer.SetStrokes(vl, new JsonArray(stroke), "선");
        var vs = (VectorSelectTool)Registry.Tools.Find(t => t.Id == "vselect");
        vs.Activate();
        vs.Down(P(60, 61)); vs.Move(P(60, 70)); vs.Move(P(60, 71)); vs.Up(P(60, 71));
        var moved = VectorLayer.Pts((JsonObject)VectorLayer.Strokes(vl)[0]);
        Ok(vs.Sel.Contains(0) && Math.Abs(moved[1] - 70) < 0.01 && Math.Abs(moved[9] - 70) < 0.01, "벡터 선 고르기: 탭해서 고르고 끌어 옮기기");
        vs.Apply(s => { s["color"] = "#ff0000"; return s; }, "선 색");
        Ok((string)VectorLayer.Strokes(vl)[0]["color"] == "#ff0000", "벡터 선 색 바꾸기");
        Ok(VectorSelectTool.Smooth(new double[] { 0, 0, 1, 0, 10, 10, 1, 0, 20, 0, 1, 0 }, 1)[5] == 5, "벡터 선 다듬기 (웹 smoothPts 와 같은 계산)");
        vs.HandleKey(Keys.Delete);
        Ok(VectorLayer.Strokes(vl).Count == 0 && vs.Sel.Count == 0, "벡터 선 삭제");
        App.SetTool("brush");
    }

    static void TestExport(string outDir)
    {
        var d = Document.New(40, 30, "빠른"); App.SetDoc(d);
        Paint(d.Layers[0], 0, 0, 40, 30, 0xFF336699);
        string dir = Path.Combine(outDir, "exports"); Directory.CreateDirectory(dir);
        var p = MoreCommands.ExportQuick(d, dir);
        using (var bmp = new Bitmap(p)) Ok(bmp.Width == 40 && bmp.Height == 30 && bmp.GetPixel(5, 5).ToArgb() == unchecked((int)0xFF336699), "PNG 빠른 내보내기");
        string ap = MoreCommands.AssetPath("나무/../../나쁜", "배경/../../../밖");
        string assets = Path.GetFullPath(MoreCommands.AssetsDir);
        Ok(Path.GetFullPath(ap).StartsWith(assets + Path.DirectorySeparatorChar) && ap.EndsWith(".png"), "에셋 경로는 data/assets 안으로만");
    }
}
