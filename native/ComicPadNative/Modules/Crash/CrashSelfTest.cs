// 오류 보고 시험 (창 없이): --selftest crash 결과폴더 → 결과폴더/result.txt, 끝 코드 = 실패 수
//  ① 명령에서 오류 → data/autosave 에 긴급 저장 (올바른 .cpad, 사용자 파일은 그대로) ② 보고서에 사용자 폴더·파일 이름·문서 글이 없음
//  ③ 오류 창이 뜸 (화면 밖) ④ 같은 오류 되풀이 → 창 한 번 ⑤ 설정 끄면 보고서 없음 ⑥ GitHub 주소 길이 제한·가림
//  ⑦ 시작 꺼짐 반복 막기: 다른 프로세스로 두 번 "창 전에 꺼짐" → 세 번째는 안전 모드 (GPU 끔·기본 화면·꺼진 모듈 건너뜀) → 정상 시작 뒤 보통 + 설정 되돌림
//  브라우저·클립보드·네트워크는 쓰지 않음 (바꿔 끼움). 모든 파일은 결과폴더 안에만.
using System.Diagnostics;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Crash;

public static class CrashSelfTest
{
    public static int Run(string outDir)
    {
        var lines = new List<string>();
        int fail = 0;
        void Ok(bool c, string name, string detail = "") { if (!c) fail++; lines.Add((c ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? " — " + detail : "")); }

        string root = Path.GetFullPath(outDir);
        Directory.CreateDirectory(root);
        string data = Path.Combine(root, "data");
        Environment.SetEnvironmentVariable("COMICPAD_DATA", data);
        Environment.SetEnvironmentVariable("COMICPAD_SETTINGS", Path.Combine(root, "settings.json"));
        App.Settings = new JsonObject();
        CrashGuard.StatePath = Path.Combine(root, "startup_inproc.json");

        string rnd = Guid.NewGuid().ToString("N")[..6];
        string userDirName = "CrashUser_비밀폴더_" + rnd, docName = "비밀원고_" + rnd, layerName = "비밀레이어_" + rnd, speech = "대사비밀내용_" + rnd;
        string userDir = Path.Combine(root, userDirName);
        Directory.CreateDirectory(userDir);
        string userFile = Path.Combine(userDir, docName + ".cpad");

        string opened = null, copied = null;
        var oldOpen = CrashUi.OpenUrl;
        CrashUi.OpenUrl = u => opened = u;
        CrashUi.CopyText = s => copied = s;
        try
        {
            // ---- 문서 준비: 이름·레이어·글 레이어에 비밀 글, 사용자 폴더에 저장한 파일 ----
            var d = Document.New(320, 240, docName);
            var l = new Layer(d, layerName);
            for (int y = 20; y < 120; y++) for (int x = 30; x < 200; x++) l.Px.Set(x, y, 0xFF2040E0);
            d.Layers.Add(l);
            var t = new Layer(d, "말풍선 글", "text") { Meta = new JsonObject { ["text"] = speech, ["font"] = "Malgun Gothic", ["size"] = 32 } };
            d.Layers.Add(t);
            CpadIO.Save(d, userFile);
            d.FilePath = userFile;
            var before = File.ReadAllBytes(userFile);
            App.SetDoc(d);
            d.Modified = true;

            CrashHandler.Active = true; CrashHandler.Headless = true; CrashHandler.ResetRepeat();
            CrashLog.Clear();
            MainForm.Run(() => { }, "편집/시험 명령 하나");
            App.SetTool("eraser"); App.SetTool("brush");
            Registry.Menu("시험/오류 명령", () => throw new IOException($"'{userFile}' 처리 실패 — 문서 {docName}, 글 {speech}"));
            CrashUi.LastShown = default;
            MainForm.Run(Registry.Commands["시험/오류 명령"], "시험/오류 명령");

            // ① 긴급 저장
            var save = CrashHandler.LastSave;
            Ok(save?.Path != null && File.Exists(save.Path), "긴급 저장 파일이 생김", save?.Note ?? "없음");
            if (save?.Path != null)
            {
                Ok(Path.GetDirectoryName(save.Path)!.Equals(Path.Combine(data, "autosave"), StringComparison.OrdinalIgnoreCase), "긴급 저장은 data/autosave 에", save.Path);
                Ok(save.Path.EndsWith(".cpad") && Path.GetFileName(save.Path).Contains("긴급 저장"), "이름에 \"긴급 저장\" + .cpad");
                try
                {
                    var back = CpadIO.Load(save.Path);
                    Ok(back.W == 320 && back.H == 240 && back.Layers.Count == d.Layers.Count, "긴급 저장본이 올바른 .cpad (다시 열림)", $"{back.W}×{back.H}, 레이어 {back.Layers.Count}");
                    var bl = back.Layers.FirstOrDefault(x => x.Name == layerName);
                    Ok(bl?.Px != null && bl.Px.Get(100, 50) == 0xFF2040E0, "긴급 저장본에 그림이 그대로");
                }
                catch (Exception e) { Ok(false, "긴급 저장본이 올바른 .cpad (다시 열림)", e.Message); }
            }
            Ok(File.ReadAllBytes(userFile).SequenceEqual(before), "사용자 파일은 덮어쓰지 않음");

            // ② 보고서 가리기
            string rep = CrashHandler.LastReport ?? "";
            Ok(rep.Length > 0 && CrashHandler.LastReportPath != null && File.Exists(CrashHandler.LastReportPath), "보고서가 data/reports 에 저장됨", CrashHandler.LastReportPath ?? "");
            Ok(CrashHandler.LastReportPath?.StartsWith(Path.Combine(data, "reports"), StringComparison.OrdinalIgnoreCase) == true, "보고서 위치 = data/reports");
            var secrets = new List<(string what, string s)> { ("임시 사용자 폴더 이름", userDirName), ("문서 이름", docName), ("레이어 이름", layerName), ("글 레이어 글자", speech), ("결과 폴더 경로", root) };
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 3) secrets.Add(("사용자 폴더 경로", home));
            if (Environment.UserName.Length >= 3) secrets.Add(("사용자 이름", Environment.UserName));
            if (Environment.MachineName.Length >= 3) secrets.Add(("컴퓨터 이름", Environment.MachineName));
            string fileText = CrashHandler.LastReportPath != null && File.Exists(CrashHandler.LastReportPath) ? File.ReadAllText(CrashHandler.LastReportPath) : "";
            foreach (var (what, s) in secrets)
                Ok(!rep.Contains(s, StringComparison.OrdinalIgnoreCase) && !fileText.Contains(s, StringComparison.OrdinalIgnoreCase), "보고서에 " + what + " 없음");
            if (root.StartsWith(home, StringComparison.OrdinalIgnoreCase)) Ok(rep.Contains("%USERPROFILE%"), "사용자 폴더는 %USERPROFILE% 로");
            Ok(rep.Contains("*.cpad"), "파일은 확장자만 (*.cpad)");
            foreach (var need in new[] { "## 환경", "ComicPad:", "Windows:", ".NET:", "그래픽 카드:", "## 관련 설정", "## 모듈", "CrashModule", "IOException", "## 최근 명령", "시험/오류 명령", "편집/시험 명령 하나", "도구: eraser", "## 최근 기록", "긴급 저장: 함" })
                Ok(rep.Contains(need), "보고서에 \"" + need + "\"");
            Ok(rep.Contains("   at ") || rep.Contains(" at "), "보고서에 스택 추적");

            // ③ 오류 창
            var (title, texts, buttons) = CrashUi.LastShown;
            Ok(title == "오류가 났습니다", "오류 창이 뜸 (화면 밖)", title ?? "안 뜸");
            Ok(buttons != null && buttons.SequenceEqual(new[] { "보고서 보기", "GitHub 에 보고하기", "닫기" }), "단추 [보고서 보기] [GitHub 에 보고하기] [닫기]", buttons == null ? "" : string.Join("|", buttons));
            Ok(texts != null && save?.Path != null && texts.Any(x => x == "작업은 여기 저장했습니다: " + save.Path), "창에 \"작업은 여기 저장했습니다: …\"");
            Ok(texts != null && texts.Any(x => x.Contains("아무 데도 보내지 않았습니다")), "창에 \"보내지 않았음\" 안내");
            Ok(opened == null, "저절로 브라우저를 열지 않음");

            // 보고서 보기 창 (보내기 전 전체 보기)
            CrashUi.LastShown = default;
            CrashUi.Viewer(rep, CrashHandler.LastReportPath, true);
            Ok(CrashUi.LastShown.buttons?.SequenceEqual(new[] { "닫기", "복사", "GitHub 에 보고하기" }) == true, "보고서 보기 창 단추 [닫기] [복사] [GitHub 에 보고하기]", CrashUi.LastShown.buttons == null ? "안 뜸" : string.Join("|", CrashUi.LastShown.buttons));

            // ⑥ GitHub 주소 (사용자가 누를 때만): 길이 제한·가림·전체는 클립보드
            string url = CrashUi.SendToGitHub(rep, CrashHandler.LastReportPath);
            Ok(opened == url && url.StartsWith(CrashReport.RepoUrl + "/issues/new?"), "GitHub 이슈 작성 주소 (누를 때만)");
            Ok(url.Length <= 7500, "주소 길이 ≤ 7500", url.Length.ToString());
            string dec = Uri.UnescapeDataString(url);
            Ok(dec.Contains("title=[오류] IOException"), "이슈 제목 = [오류] 종류: 메시지");
            Ok(secrets.All(s => !dec.Contains(s.s, StringComparison.OrdinalIgnoreCase)), "이슈 주소에도 비밀 글 없음");
            Ok(copied != null && copied.Replace("\r\n", "\n").TrimEnd() == rep.Replace("\r\n", "\n").TrimEnd(), "전체 보고서는 클립보드로");
            string big = rep + new string('가', 30000);
            string bu = CrashReport.IssueUrl(big);
            Ok(bu.Length <= 7500 && Uri.UnescapeDataString(bu).Contains("길어서 잘렸습니다"), "긴 보고서는 잘리고 안내", bu.Length.ToString());

            // ④ 같은 오류 되풀이 → 창·저장 한 번
            int autos = Directory.GetFiles(Path.Combine(data, "autosave"), "*.cpad").Length;
            CrashUi.LastShown = default;
            MainForm.Run(Registry.Commands["시험/오류 명령"], "시험/오류 명령");
            Ok(CrashUi.LastShown.title == null, "같은 오류 되풀이 → 창을 또 띄우지 않음");
            Ok(Directory.GetFiles(Path.Combine(data, "autosave"), "*.cpad").Length == autos, "되풀이 때는 긴급 저장도 한 번만");

            // ⑤ 설정 끄면 보고서·창 없음 (긴급 저장은 함)
            App.Settings["crashReport"] = false;
            CrashHandler.ResetRepeat();
            int reps = CrashReport.RecentReports().Count;
            CrashUi.LastShown = default;
            Ok(CrashHandler.Handle(Thrown(new InvalidOperationException("설정 끔 시험")), "시험/둘", false), "설정 끔: 처리함");
            Ok(CrashReport.RecentReports().Count == reps && CrashUi.LastShown.title == null, "설정 끔: 보고서·창 없음");
            Ok(Directory.GetFiles(Path.Combine(data, "autosave"), "*.cpad").Length == autos + 1, "설정 끔: 긴급 저장은 함");
            App.Settings.Remove("crashReport");

            // 화면 처리 중 오류 (ThreadException 경로) → 창
            CrashHandler.ResetRepeat(); CrashUi.LastShown = default;
            CrashHandler.Handle(Thrown(new NullReferenceException("그리기 중")), null, false);
            Ok(CrashUi.LastShown.title == "오류가 났습니다", "명령 밖 오류도 창");

            // 앱이 닫히는 오류 → 창 없이 저장, 다음 시작 때 보여 줄 보고서로 기록
            CrashHandler.ResetRepeat(); CrashUi.LastShown = default;
            CrashHandler.Handle(Thrown(new ApplicationException("닫히는 오류")), null, true);
            var (ur, us) = CrashGuard.TakeUnseen();
            Ok(CrashUi.LastShown.title == null && ur != null && File.Exists(ur), "닫히는 오류: 창 없이 보고서 → 다음 시작 때 알림");
            if (ur != null) { CrashUi.Show(new CrashUi.Info("x", null, new SaveResult(us, "작업은 여기 저장했습니다: " + us), File.ReadAllText(ur), ur, true)); Ok(CrashUi.LastShown.title == "지난번에 오류로 닫혔습니다", "다음 시작 때 \"지난번에 오류로 닫혔습니다\" 창"); }

            // 최근 오류 보고서 창
            CrashUi.LastShown = default;
            CrashUi.Recent();
            Ok(CrashUi.LastShown.title == "최근 오류 보고서", "도움말 › 최근 오류 보고서… 창");
            Ok(Registry.Menus.Any(m => m.Path == "도움말/최근 오류 보고서…"), "메뉴 도움말/최근 오류 보고서…");
            Ok(Shell2.Shell2Settings.Extra.Count > 0 && Shell2.Shell2Settings.Extra.Any(f => f().OfType<CheckBox>().Any(c => c.Text.Contains("오류가 나면 보고서 만들기"))), "설정 \"오류가 나면 보고서 만들기\"");
            Ok(App.Get("crashReport", true), "설정 기본값 = 켬");

            File.WriteAllText(Path.Combine(root, "report_sample.txt"), rep);
        }
        catch (Exception e) { Ok(false, "시험 중 예외", e.ToString()); }
        finally
        {
            CrashHandler.Active = false; CrashHandler.Headless = false;
            CrashUi.OpenUrl = oldOpen;
        }

        // ⑦ 시작 꺼짐 반복 막기 (다른 프로세스, 창 없이)
        try { GuardTest(root, (c, nm, dt) => Ok(c, nm, dt)); }
        catch (Exception e) { Ok(false, "시작 꺼짐 시험 중 예외", e.ToString()); }

        lines.Add($"\n{lines.Count(x => x.StartsWith("PASS"))} 통과, {fail} 실패");
        File.WriteAllLines(Path.Combine(root, "result.txt"), lines);
        return fail;
    }

    static Exception Thrown(Exception e) { try { throw e; } catch (Exception x) { return x; } }

    delegate void OkFn(bool c, string name, string detail = "");
    static void GuardTest(string root, OkFn ok)
    {
        string exe = Environment.ProcessPath;
        string dir = Path.Combine(root, "guard");
        Directory.CreateDirectory(dir);
        string settings = Path.Combine(dir, "settings.json");
        File.WriteAllText(settings, new JsonObject { ["gpu"] = true, ["ui"] = new JsonObject { ["mode"] = "full" }, ["largeUI"] = true, ["penButton"] = "eraser" }.ToJsonString());
        int n = 0;
        (int code, JsonObject probe) Start(string probe)
        {
            n++;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("COMICPAD_", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(k);
            psi.Environment["COMICPAD_SETTINGS"] = settings;
            psi.Environment["COMICPAD_DATA"] = Path.Combine(dir, "data");
            psi.Environment["COMICPAD_CRASHGUARD_PROBE"] = probe.StartsWith("probe") ? "probe:" + Path.Combine(dir, $"probe{n}.json") : probe;
            using var p = Process.Start(psi);
            p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(180_000)) { try { p.Kill(); } catch { } return (-1, null); }   // 내가 띄운 프로세스만
            string pf = Path.Combine(dir, $"probe{n}.json");
            return (p.ExitCode, File.Exists(pf) ? JsonNode.Parse(File.ReadAllText(pf)) as JsonObject : null);
        }
        var r1 = Start("crash:VisionModule");
        ok(r1.code == 3, "시작 1: VisionModule 을 켜다 꺼짐 (창 전에)", "끝 코드 " + r1.code);
        var r2 = Start("crash");
        ok(r2.code == 3, "시작 2: 창 전에 꺼짐", "끝 코드 " + r2.code);
        var r3 = Start("probe");
        var p3 = r3.probe;
        ok(p3?["safeMode"]?.GetValue<bool>() == true, "시작 3: 두 번 꺼진 뒤 안전 모드", p3?.ToJsonString() ?? "결과 없음");
        ok(p3?["earlyCrashes"]?.GetValue<int>() == 2, "안전 모드: 꺼진 횟수 2");
        ok(p3?["gpu"]?.ToString() == "off" && p3?["noGpuEnv"]?.GetValue<string>() == "1", "안전 모드: GPU 끔 (설정 + COMICPAD_NO_GPU)");
        ok(p3?["hasUi"]?.GetValue<bool>() == false, "안전 모드: 화면 방식 기본값");
        ok(p3?["skipped"] is JsonArray sk && sk.Any(x => x?.GetValue<string>() == "VisionModule"), "안전 모드: 꺼지게 한 모듈(VisionModule) 건너뜀");
        var r4 = Start("probe");
        var p4 = r4.probe;
        ok(p4?["safeMode"]?.GetValue<bool>() == false && p4?["earlyCrashes"]?.GetValue<int>() == 0, "정상 시작 뒤 다시 보통 모드", p4?.ToJsonString() ?? "결과 없음");
        ok(p4?["gpu"]?.GetValue<bool>() == true && p4?["ui"]?["mode"]?.GetValue<string>() == "full", "보통 모드: 안전 모드에서 바꾼 설정을 되돌림");
        var saved = JsonNode.Parse(File.ReadAllText(settings)) as JsonObject;
        ok(saved?["gpu"]?.GetValue<bool>() == true && saved?["largeUI"]?.GetValue<bool>() == true && saved?["penButton"]?.GetValue<string>() == "eraser", "설정 파일도 원래대로 (다른 설정은 그대로)", saved?.ToJsonString() ?? "");
        var r5 = Start("crash");
        var r6 = Start("probe");
        ok(r5.code == 3 && r6.probe?["safeMode"]?.GetValue<bool>() == false && r6.probe?["earlyCrashes"]?.GetValue<int>() == 1, "한 번만 꺼지면 안전 모드 아님 (정상 시작 때 횟수 초기화 확인)", r6.probe?.ToJsonString() ?? "");
    }
}
