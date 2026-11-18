// --selftest switch 결과폴더: 웹 판 ↔ 네이티브 판 오가기 시험 (창 없이, 임시 설치 폴더·임시 설정 파일만 씀)
//  - 설치 폴더 찾기·스위치 파일(app.json) 읽기/쓰기
//  - 웹 판 설정 가져오기: 옮기는 키·안 옮기는 키·네이티브에서 바꾼 값 지키기·덮어쓰기·처음 켤 때 한 번만
//  - 데이터 같이 쓰기: 자동 저장 이름(웹과 다름)·버전 기록 이름(server.py 와 같은 규칙)·웹 판 미리보기 PNG·.cpad 왕복
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class AppSwitchTest
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        int fail = 0;
        void Ok(bool c, string name, string detail = "") { if (!c) fail++; log.AppendLine((c ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  " + detail : "")); }
        void T(string name, Action a) { try { a(); } catch (Exception e) { fail++; log.AppendLine("FAIL " + name + " 예외: " + e.Message); } }

        string tmp = Path.Combine(outDir, "switch_tmp");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        string root = Path.Combine(tmp, "install"), data = Path.Combine(root, "data"), nat = Path.Combine(root, "native", "ComicPadNative");
        Directory.CreateDirectory(nat); Directory.CreateDirectory(Path.Combine(data, "config"));
        File.WriteAllText(Path.Combine(root, "ComicPad.exe"), "stand-in");
        File.WriteAllText(Path.Combine(root, "server.py"), "# stand-in");
        File.WriteAllText(Path.Combine(nat, "ComicPadNative.exe"), "stand-in");
        string oldData = Environment.GetEnvironmentVariable("COMICPAD_DATA"), oldSet = Environment.GetEnvironmentVariable("COMICPAD_SETTINGS");
        Environment.SetEnvironmentVariable("COMICPAD_DATA", data);
        Environment.SetEnvironmentVariable("COMICPAD_SETTINGS", Path.Combine(tmp, "native-settings.json"));
        var keepSettings = App.Settings; var keepDoc = App.Doc;
        try
        {
            T("설치 폴더 찾기", () =>
            {
                Ok(AppSwitch.FindInstall(nat) == root, "native\\ComicPadNative 에서 설치 폴더 찾기", AppSwitch.FindInstall(nat) ?? "null");
                Ok(AppSwitch.FindInstall(Path.Combine(tmp)) == null, "설치 폴더가 아니면 null");
            });

            T("스위치 파일", () =>
            {
                Ok(AppSwitch.Read() == "web", "스위치 없으면 web");
                AppSwitch.Write("native");
                var j = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "config", "app.json")));
                Ok(AppSwitch.Read() == "native" && (string)j["app"] == "native" && (string)j["by"] == "native", "native 쓰기·읽기 (창 프로그램이 읽는 \"app\" 키)");
                AppSwitch.Write("web");
                Ok(AppSwitch.Read() == "web", "web 으로 되돌리기");
                File.WriteAllText(AppSwitch.SwitchPath(), "{깨진");
                Ok(AppSwitch.Read() == "web", "깨진 파일이면 web");
                File.Delete(AppSwitch.SwitchPath());
            });

            var web = new JsonObject
            {
                ["pressureGamma"] = 1.4, ["penFeel"] = new JsonObject { ["filter"] = "euro", ["pressureSmooth"] = 0, ["startRamp"] = false, ["predict"] = true },
                ["brushCursor"] = "nib", ["keys"] = new JsonObject { ["view.grid"] = "ctrl+alt+g" }, ["toolKeys"] = new JsonObject { ["fill"] = "g" },
                ["brushes"] = new JsonObject { ["brush"] = new JsonArray(new JsonObject { ["id"] = "my-pen", ["name"] = "내 펜", ["size"] = 7 }) },
                ["activePreset"] = new JsonObject { ["brush"] = "my-pen" }, ["ui"] = new JsonObject { ["mode"] = "full" },
                ["palette"] = new JsonArray("#000000", "#ff0000"), ["recentColors"] = new JsonArray("#123456"), ["setupDone"] = "clip",
                ["nativePen"] = false, ["penApi"] = "wintab", ["memSave"] = true, ["recentCommands"] = new JsonArray("file.save"), ["somethingNew"] = 1,
            };
            var server = new JsonObject { ["backup"] = new JsonObject { ["on"] = false, ["keep"] = 5 }, ["lan"] = true };

            T("웹 판 설정 가져오기 (처음)", () =>
            {
                var n = new JsonObject { ["brushCursor"] = "cross" };    // 네이티브를 먼저 쓰며 바꿔 둔 값
                var r = WebImport.Merge(n, web, false, "settings.json", server);
                Ok((double)n["pressureGamma"] == 1.4 && (string)n["penFeel"]["filter"] == "euro" && (string)n["keys"]["view.grid"] == "ctrl+alt+g"
                   && (string)n["brushes"]["brush"][0]["id"] == "my-pen" && (string)n["ui"]["mode"] == "full" && (string)n["setupDone"] == "clip"
                   && (string)n["recentColors"][0] == "#123456" && (string)n["toolKeys"]["fill"] == "g", "펜·필기감·단축키·브러시·화면 구성·처음 세팅·최근 색 옮김");
                Ok(n["nativePen"] == null && n["penApi"] == null && n["memSave"] == null && n["recentCommands"] == null && n["somethingNew"] == null, "웹 전용·모르는 키는 안 옮김");
                Ok((string)n["brushCursor"] == "cross" && r.Kept.Contains("brushCursor"), "네이티브에 이미 있던 다른 값은 그대로", string.Join(",", r.Kept));
                Ok((bool)n["backup"]["on"] == false && (int)n["backup"]["keep"] == 5 && n["lan"] == null, "server.json 자동 백업 설정 옮김 (휴대폰 연결은 안 옮김)");
                Ok(n["webImport"]?["keys"]?["pressureGamma"] != null && n["webImport"]?["keys"]?["brushCursor"] == null, "가져온 값 지문 기록");

                // 네이티브에서 필압 곡선을 바꾸고, 웹 판에서 팔레트·필압을 바꾼 뒤 다시 가져오기
                n["pressureGamma"] = 2.0;
                var web2 = (JsonObject)web.DeepClone(); web2["palette"] = new JsonArray("#00ff00"); web2["pressureGamma"] = 1.6;
                var r2 = WebImport.Merge(n, web2, false, "settings.json");
                Ok((double)n["pressureGamma"] == 2.0 && r2.Kept.Contains("pressureGamma"), "다시 가져오기: 네이티브에서 바꾼 값은 덮어쓰지 않음");
                Ok((string)n["palette"][0] == "#00ff00" && r2.Imported.Contains("palette"), "다시 가져오기: 안 바꾼 값은 웹 판 새 값으로");
                Ok(r2.Same.Contains("keys") && !r2.Imported.Contains("keys"), "같은 값은 그대로 (가져온 것으로 세지 않음)");
                var r3 = WebImport.Merge(n, web2, true, "settings.json");
                Ok((double)n["pressureGamma"] == 1.6 && (string)n["brushCursor"] == "nib" && r3.Kept.Count == 0, "덮어쓰기를 고르면 웹 판 값으로");
                var gn = WebImport.GroupNames(r.Imported).ToList();
                Ok(gn.Contains("펜·필기감") && gn.Contains("브러시") && gn.Contains("단축키") && gn.Contains("화면 구성"), "그룹 이름", string.Join(",", gn));
            });

            T("처음 켤 때 한 번만", () =>
            {
                File.WriteAllText(WebImport.WebSettingsPath(), web.ToJsonString());
                App.Settings = new JsonObject();
                var r = WebImport.ImportOnStart();
                Ok(r != null && r.Imported.Contains("pressureGamma") && (double)App.Settings["pressureGamma"] == 1.4, "처음 켤 때 가져옴");
                var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(tmp, "native-settings.json")));
                Ok((double)saved["pressureGamma"] == 1.4 && saved["webImport"] != null, "바로 저장 (임시 설정 파일)");
                App.Settings["pressureGamma"] = 3.0;
                Ok(WebImport.ImportOnStart() == null && (double)App.Settings["pressureGamma"] == 3.0, "두 번째로 켤 때는 가져오지 않음");
                App.Settings = new JsonObject(); File.Delete(WebImport.WebSettingsPath());
                WebImport.ImportOnStart();
                Ok(App.Settings["webImport"] != null && App.Settings["pressureGamma"] == null, "웹 판 설정이 없어도 '한 번 했음' 기록");
            });

            T("자동 저장 이름", () =>
            {
                Ok(FileCommands.AutosaveName("작품1") == "작품1 (네이티브).cpad", "네이티브 자동 저장은 \"이름 (네이티브).cpad\"");
                var d = Document.New(64, 48, "작품1"); d.Name = "작품1"; d.Modified = true;
                App.Doc = d;
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(data, "autosave")).FullName, "작품1.cpad"), "{\"web\":1}");
                FileCommands.Autosave();
                Ok(File.Exists(Path.Combine(data, "autosave", "작품1 (네이티브).cpad")) && File.ReadAllText(Path.Combine(data, "autosave", "작품1.cpad")) == "{\"web\":1}", "웹 판 자동 저장본을 덮어쓰지 않음");
                var back = CpadIO.Load(Path.Combine(data, "autosave", "작품1 (네이티브).cpad"));
                Ok(back.W == 64 && back.H == 48, "자동 저장본은 .cpad 로 열림");
            });

            T("저장: 미리보기·버전 기록", () =>
            {
                var proj = FileCommands.ProjectsDir;
                var d = Document.New(400, 200, "공유"); d.Name = "공유";
                d.FilePath = Path.Combine(proj, "공유.cpad"); d.Modified = true; App.Doc = d;
                Ok(FileCommands.Save(false) && File.Exists(d.FilePath), "작품 폴더에 저장");
                string png = Path.Combine(proj, "공유.png");
                bool thumbOk = false;
                if (File.Exists(png)) using (var b = new Bitmap(png)) thumbOk = b.Width == 320 && b.Height == 160;
                Ok(thumbOk, "웹 판 미리보기 PNG (긴 변 320)");
                var stamp = File.GetLastWriteTime(d.FilePath).ToString("yyyyMMdd-HHmmss");
                d.Modified = true;
                FileCommands.Save(false);
                Ok(File.Exists(Path.Combine(data, "versions", "공유", stamp + ".cpad")), "버전 기록 이름 = 덮어쓴 파일의 저장 시각 (server.py 와 같은 규칙)", stamp);
            });

            T(".cpad 왕복 (웹 판 파일)", () =>
            {
                string src = null;
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && src == null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "native", "parity_test.cpad"))) src = Path.Combine(dir.FullName, "native", "parity_test.cpad");
                if (src == null) { log.AppendLine("SKIP .cpad 왕복 (native/parity_test.cpad 없음)"); return; }
                var a = CpadIO.Load(src);
                string outp = Path.Combine(tmp, "roundtrip.cpad");
                CpadIO.Save(a, outp);
                var b = CpadIO.Load(outp);
                var fa = App.Comp.Flatten(a); var fb = App.Comp.Flatten(b);
                using var ba = CpadIO.ToBitmap(fa, fa.Full); using var bb = CpadIO.ToBitmap(fb, fb.Full);
                int diff = 0, max = 0;
                for (int y = 0; y < ba.Height; y += 3)
                    for (int x = 0; x < ba.Width; x += 3)
                    {
                        Color p = ba.GetPixel(x, y), q = bb.GetPixel(x, y);
                        int m = Math.Max(Math.Max(Math.Abs(p.R - q.R), Math.Abs(p.G - q.G)), Math.Max(Math.Abs(p.B - q.B), Math.Abs(p.A - q.A)));
                        if (m > 0) diff++;
                        max = Math.Max(max, m);
                    }
                var j = JsonNode.Parse(File.ReadAllText(outp)) as JsonObject;
                // 반투명 픽셀은 PNG(곱하지 않은 알파) ↔ 내부(곱한 알파) 변환에서 ±1 생길 수 있음 → parity 시험과 같은 허용치 1
                Ok(max <= 1 && a.Layers.Count == b.Layers.Count, "웹 판이 만든 .cpad → 네이티브 저장 → 다시 열기 (그림 같음, 차이 ≤1)", $"다른 점 {diff}, 최대 {max}");
                Ok(j?["layers"] is JsonArray && j["width"] != null && j["height"] != null, "네이티브가 저장한 .cpad 에 웹 판이 읽는 칸(width·height·layers)");
            });
        }
        finally
        {
            App.Settings = keepSettings; App.Doc = keepDoc;
            Environment.SetEnvironmentVariable("COMICPAD_DATA", oldData);
            Environment.SetEnvironmentVariable("COMICPAD_SETTINGS", oldSet);
        }
        log.AppendLine(fail == 0 ? "모두 통과" : $"실패 {fail}");
        File.WriteAllText(Path.Combine(outDir, "switch_report.txt"), log.ToString());
        return fail;
    }
}
