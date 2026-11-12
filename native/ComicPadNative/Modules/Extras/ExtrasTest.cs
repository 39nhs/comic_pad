// Extras 모듈 시험 (창 없이, 정해진 결과): 환경 변수 COMICPAD_EXTRAS_TEST=결과 폴더, COMICPAD_DATA=임시 작업물 폴더 로 실행.
//   예) COMICPAD_DATA=%TEMP%\cpx COMICPAD_EXTRAS_TEST=%TEMP%\cpx-out ComicPadNative.exe --render x y
// 결과: 결과 폴더/result.txt (통과/실패 줄), 끝 코드 0 = 모두 통과. 사용자 설정 파일은 건드리지 않음.
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Extras;

public static class ExtrasTest
{
    static readonly StringBuilder log = new();
    static int fail;
    static void Ok(bool cond, string name) { log.AppendLine((cond ? "통과  " : "실패  ") + name); if (!cond) fail++; }

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMICPAD_DATA"))) { File.WriteAllText(Path.Combine(outDir, "result.txt"), "COMICPAD_DATA 를 임시 폴더로 정해 주세요"); return 2; }
            App.Settings = new JsonObject();   // 사용자 설정과 무관
            // 다른 기본 모듈(메뉴·도구)만 등록 (자동 백업 등 Shell 은 빼고)
            foreach (var t in typeof(App).Assembly.GetTypes().Where(t => t.Name is "CoreModule" or "BasicToolsModule"))
                ((IModule)Activator.CreateInstance(t)).Init();
            Registry.Tool(new MemoTool());
            void T(string n, Action a) { try { a(); } catch (Exception ex) { Ok(false, $"{n}: 예외 {ex.GetType().Name} {ex.Message}"); } }
            T("여러 페이지 작품", TestStory);
            T("전자책", () => TestEbook(outDir));
            T("타임랩스", () => TestTimelapse(outDir));
            T("소실점", TestVP);
            T("세팅 프리셋", TestSetup);
            T("키 바꾸기", TestKeys);
            T("피드백", TestFeedback);
            T("자동 액션", TestMacros);
            T("명도 보기", TestLuma);
            T("버전 비교", TestCompare);
            T("펜 진단", TestPen);
        }
        catch (Exception ex) { Ok(false, "시험 실행 오류: " + ex); }
        log.AppendLine(fail == 0 ? "모두 통과" : $"실패 {fail}개");
        File.WriteAllText(Path.Combine(outDir, "result.txt"), log.ToString());
        return fail == 0 ? 0 : 1;
    }

    static void TestStory()
    {
        string story = "시험작품";
        for (int i = 1; i <= 3; i++) Story.SavePageDoc(Story.BlankPage(800, 1000, $"{story} {i}쪽", true), $"{story}/{Story.PageName(i)}.cpad");
        var pages = Story.ListPages(story);
        Ok(pages.SequenceEqual(new[] { "시험작품/p001.cpad", "시험작품/p002.cpad", "시험작품/p003.cpad" }), "페이지 목록 p001~p003");
        Ok(File.Exists(Story.Full("시험작품/p001.png")), "미리보기 그림 p001.png");
        Story.MovePage(story, 2, 0);
        pages = Story.ListPages(story);
        Ok(pages[0] == "시험작품/p003.cpad" && (Story.ReadInfo(story)["order"] as JsonArray)?.Count == 3, "순서 바꾸기 → story.json order");
        var d = CpadIO.Load(Story.Full("시험작품/p001.cpad"));
        var panel = d.Layers.FirstOrDefault(l => l.Kind == "panel");
        int m = (int)Math.Round(800 * 0.07), line = Math.Max(3, (int)Math.Round(800 / 400.0));
        Ok(d.Layers.Count == 2 && panel != null && (panel.Meta?["panels"]?[0]?["pts"] as JsonArray)?.Count == 4 && (int)panel.Meta["line"] == line && (bool)panel.Meta["maskOutside"], "컷 틀 레이어 메타 (웹 판과 같은 모양)");
        Ok(panel.Px.Get(2, 2) == 0xFFFFFFFF && panel.Px.Get(m + 1, 500) == 0xFF000000 && panel.Px.Get(400, 500) == 0, "컷 틀 그림 (바깥 흰색·테두리 검정·안쪽 투명)");
        Ok(Story.StoryOfRel("시험작품/p001.cpad") == story && Story.StoryOfRel("한장.cpad") == null, "작품 이름 알아내기");
        // 작업 시간 → story.json time
        d.Extra["workTime"] = 125;
        Progress.OnSaved(d);
        Ok((long?)Story.ReadInfo(story)["time"]?["시험작품/p001.cpad"] == 125, "저장 때 작업 시간 기록");
        string outDir = Path.Combine(U.ExportsDir, story + "_내보내기");
        Story.ExportPages(Story.ListPages(story), outDir, "png", 0.5);
        Ok(File.Exists(Path.Combine(outDir, "003.png")) && Image.FromFile(Path.Combine(outDir, "001.png")) is var im && im.Width == 400, "작품 전체 내보내기 (50%)");
    }

    static void TestEbook(string outDir)
    {
        var d1 = Story.BlankPage(600, 900, "a", true); var d2 = Story.BlankPage(600, 900, "b", false);
        var pages = new List<EbookPage> { Ebook.PageImage(d1, 2400, "jpg"), Ebook.PageImage(d2, 300, "png") };
        Ok(pages[1].H == 300 && pages[1].W == 200 && pages[1].Type == "image/png", "쪽 그림 크기 줄이기 (긴 쪽 300)");
        var epub = Ebook.BuildEPUB(pages, "시험 <책>", "나", true);
        File.WriteAllBytes(Path.Combine(outDir, "test.epub"), epub);
        // mimetype: 맨 앞, 압축 없음, 추가 필드 없음
        bool mt = epub[0] == 'P' && epub[1] == 'K' && BitConverter.ToUInt16(epub, 8) == 0 && BitConverter.ToUInt16(epub, 26) == 8 && BitConverter.ToUInt16(epub, 28) == 0
            && Encoding.ASCII.GetString(epub, 30, 8) == "mimetype" && Encoding.ASCII.GetString(epub, 38, 20) == "application/epub+zip";
        Ok(mt, "EPUB mimetype 맨 앞·압축 없음");
        using (var z = new ZipArchive(new MemoryStream(epub)))
        {
            var names = z.Entries.Select(e => e.FullName).ToList();
            Ok(names.SequenceEqual(new[] { "mimetype", "META-INF/container.xml", "OEBPS/images/p001.jpg", "OEBPS/p001.xhtml", "OEBPS/images/p002.png", "OEBPS/p002.xhtml", "OEBPS/nav.xhtml", "OEBPS/content.opf" }), "EPUB 파일 목록 (웹 판과 같은 순서)");
            string opf = new StreamReader(z.GetEntry("OEBPS/content.opf").Open()).ReadToEnd();
            Ok(opf.Contains("page-progression-direction=\"rtl\"") && opf.Contains("<dc:title>시험 &lt;책&gt;</dc:title>") && opf.Contains("properties=\"page-spread-right\"") && opf.Contains("original-resolution\" content=\"600x900\""), "EPUB content.opf (오른쪽→왼쪽·제목·펼침면)");
        }
        var cbz = Ebook.BuildCBZ(pages, "시험", "", false);
        File.WriteAllBytes(Path.Combine(outDir, "test.cbz"), cbz);
        using (var z = new ZipArchive(new MemoryStream(cbz)))
        {
            Ok(z.Entries.Select(e => e.FullName).SequenceEqual(new[] { "001.jpg", "002.png", "ComicInfo.xml" }), "CBZ 파일 목록");
            Ok(new StreamReader(z.GetEntry("ComicInfo.xml").Open()).ReadToEnd().Contains("<Manga>No</Manga><LanguageISO>ko</LanguageISO>"), "CBZ ComicInfo.xml");
        }
        var pd = Setup.StartDocument(new JsonObject { ["name"] = "흑백 만화 원고 B5 600dpi", ["w"] = 4441, ["h"] = 6213, ["dpi"] = 600, ["print"] = new JsonObject { ["size"] = "b5", ["mmW"] = 182, ["mmH"] = 257, ["bleedMm"] = 3, ["safeMm"] = 5 } });
        var pr = Ebook.PrintRects(pd);
        Ok(pd.Name == "흑백 만화 원고 B5 600dpi" && (int)pd.Extra["print"]["trimW"] == 4299 && (int)pd.Extra["print"]["bleed"] == 71 && pr?.trim.Width == 4299 && pr?.trim.X == 71, "원고 설정(재단선) 계산·영역");
    }

    static void TestTimelapse(string outDir)
    {
        var d = Document.New(400, 300, "타임랩스");
        App.SetDoc(d);
        Timelapse.Toggle();
        string id = (string)d.Extra["timelapse"]["id"];
        Ok(Timelapse.On && id.StartsWith("tl"), "타임랩스 켜기 (doc.timelapse {id,on})");
        // 그림을 조금씩 바꾸며 찍기
        for (int i = 0; i < 6; i++) { d.Layers[0].Px.Write(new Rect(i * 50, 0, 50, 300), Enumerable.Repeat(Px.Pack(255, (uint)(i * 40), 0, 0), 50 * 300).ToArray()); Timelapse.Capture(); }
        var frames = Timelapse.Frames(id);
        Ok(frames.Count == 7, $"장면 저장 ({frames.Count}장)");
        string avi = Path.Combine(outDir, "test.avi");
        Timelapse.MakeVideo(frames, avi, 2, 1);
        var b = File.ReadAllBytes(avi);
        uint riff = BitConverter.ToUInt32(b, 4);
        int nFrames = (int)BitConverter.ToUInt32(b, 48);   // avih.dwTotalFrames
        string all = Encoding.ASCII.GetString(b);
        int idx = all.LastIndexOf("idx1", StringComparison.Ordinal);
        Ok(Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "AVI " && riff == b.Length - 8, "AVI 머리 (RIFF 크기)");
        Ok(nFrames == 7 + (int)Math.Round(1 * 3.5) && idx > 0 && BitConverter.ToUInt32(b, idx + 4) == nFrames * 16, $"AVI 장 수·색인 ({nFrames})");
        Timelapse.Toggle();
        Ok(!Timelapse.On, "타임랩스 끄기");
    }

    /// <summary>2점 투시 그림을 그려서 소실점을 찾아 봄</summary>
    static unsafe void TestVP()
    {
        int W = 1200, H = 800;
        var vp1 = (x: -400.0, y: 380.0); var vp2 = (x: 1700.0, y: 400.0);
        var d = Document.New(W, H, "투시");
        using (var bmp = new Bitmap(W, H))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Pen(Color.Black, 3);
                // 건물 모서리 (세로선) 와 두 소실점으로 모이는 선들
                foreach (var (vp, x0, x1) in new[] { (vp1, 300.0, 600.0), (vp2, 600.0, 900.0) })
                    for (int k = 0; k < 9; k++)
                    {
                        double yy = 120 + k * 70;   // 가운데 모서리(x=600)에서의 높이
                        double ax = x0, bx = x1;
                        double ay = vp.y + (yy - vp.y) * (ax - vp.x) / (600 - vp.x), by = vp.y + (yy - vp.y) * (bx - vp.x) / (600 - vp.x);
                        g.DrawLine(pen, (float)ax, (float)ay, (float)bx, (float)by);
                    }
                for (int x = 300; x <= 900; x += 100) g.DrawLine(pen, x, 60, x, 740);
            }
            CpadIO.DrawBitmap(d.Layers[0].Px, bmp, 0, 0);
        }
        var vps = VpDetect.Detect(d, new Compositor());
        bool Near((double x, double y) t) => vps.Any(v => Math.Sqrt((v.X - t.x) * (v.X - t.x) + (v.Y - t.y) * (v.Y - t.y)) < Math.Max(40, 0.05 * Math.Abs(t.x - 600)));
        Ok(vps.Count >= 2 && Near(vp1) && Near(vp2), $"2점 투시 소실점 찾기 ({string.Join(", ", vps.Select(v => $"({v.X:0},{v.Y:0})"))})");
        App.SetDoc(d);
        var rule = VpDetect.AddRule(d, vps);
        var rulers = d.Extra["rulers"] as JsonArray;
        Ok(rulers?.Count == 1 && (string)rulers[0]["type"] == "perspective" && (bool)rulers[0]["fromPhoto"] && (rulers[0]["vps"] as JsonArray)?.Count == vps.Count
            && rulers[0]["vps"][0] is JsonObject p0 && p0.Count == 2 && p0.ContainsKey("x") && p0.ContainsKey("y"), "퍼스 자 JSON (웹 판 rulers 와 같은 모양)");
        App.History.Undo(d);
        Ok(d.Extra["rulers"] == null, "퍼스 자 실행취소");
        App.History.Redo(d);
        Ok((d.Extra["rulers"] as JsonArray)?.Count == 1, "퍼스 자 다시실행");
        var json = CpadIO.ToJson(d);
        Ok(json["rulers"] is JsonArray, ".cpad 에 rulers 로 저장");
        // 직선이 없는 그림: 못 찾음
        var empty = Document.New(300, 300);
        Ok(VpDetect.Detect(empty, new Compositor()).Count == 0, "곧은 선 없는 그림 → 소실점 없음");
    }

    static void TestSetup()
    {
        var s0 = new JsonObject { ["penButton"] = "eraser", ["palette"] = new JsonArray("#000000") };
        var (next, changed) = Setup.ApplyPresetTo(s0, "csp");
        var exp = new[] { "keys", "toolKeys", "ui", "activePreset", "startDoc", "penFeel", "pressureGamma", "brushCursor" };
        Ok(changed.OrderBy(x => x).SequenceEqual(exp.OrderBy(x => x)), "클립스튜디오식: 바뀌는 설정 (" + string.Join(",", changed) + ")");
        Ok(U.Same(next["toolKeys"]["brush"], new JsonArray("b", "p")) && (string)next["toolKeys"]["view"] == "h" && (int)next["startDoc"]["dpi"] == 600 && (string)next["activePreset"]["brush"] == "gpen", "클립스튜디오식 값");
        var s = (JsonObject)s0.DeepClone();
        Setup.Commit(s, changed, next, "시험");
        Ok((string)s["setupBackup"]["label"] == "시험" && (s["setupBackup"]["absent"] as JsonArray).Count == changed.Count, "적용 전 백업");
        Setup.UndoSetup(s, false);
        Ok(U.Same(s, s0), "세팅 되돌리기 → 원래 설정 그대로");
        var (n2, c2) = Setup.ApplyPresetTo(s0, "tablet");
        Ok(c2.Contains("penButton") && (string)n2["penButton"] == "radial" && (bool)n2["largeUI"] && !c2.Contains("palmGuardMs") == false, "태블릿: 펜 측면 버튼 = 원형 메뉴");
        var (n3, _) = Setup.ApplyPresetTo(s0, "webtoon");
        Ok((int)n3["startDoc"]["w"] == 800 && (n3["bottombar"]["items"] as JsonArray).Select(x => (string)x).Contains("webtoonView"), "웹툰: 추천 크기·아래 막대");
        foreach (var p in Setup.Presets) Setup.ApplyPresetTo(new JsonObject(), p.Id);
        Ok(true, "다섯 프리셋 모두 적용해 봄");
        // .cpset
        var file = Setup.BuildSetFile(next, new[] { "keys", "pen" });
        Ok((string)file["format"] == "comicpad-settings" && file["parts"]["keys"]["toolKeys"] != null && file["parts"]["pen"]["penFeel"] != null && file["parts"]["layout"] == null, ".cpset 만들기");
        var (m, mc) = Setup.MergeSetFile(s0, file, new[] { "keys" });
        Ok(mc.OrderBy(x => x).SequenceEqual(new[] { "keys", "toolKeys" }) && m["penFeel"] == null, ".cpset 고른 부분만 가져오기");
        var ws = new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["name"] = "펜선", ["tool"] = "brush" }) };
        var (m2, _) = Setup.MergeSetFile(new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["name"] = "채색" }, new JsonObject { ["name"] = "펜선", ["tool"] = "eraser" }) },
            new JsonObject { ["parts"] = new JsonObject { ["workspaces"] = ws } }, new[] { "workspaces" });
        Ok((m2["workspaces"] as JsonArray).Count == 2 && (string)m2["workspaces"][1]["tool"] == "brush", ".cpset 작업 화면 합치기 (이름이 같으면 바꿈)");
        var w = Workspaces.Snapshot("시험");
        Ok((string)w["name"] == "시험" && (double)w["dockSplit"] == 0.48 && (bool)w["sideVisible"], "작업 화면 저장 값 (기본값 포함)");
    }

    static void TestKeys()
    {
        Ok(KeyMap.Parse("ctrl+shift+e") == (Keys.Control | Keys.Shift | Keys.E) && KeyMap.Parse("pagedown") == Keys.PageDown && KeyMap.Parse("b") == Keys.B && KeyMap.Parse("ctrl+alt+shift+w") == (Keys.Control | Keys.Alt | Keys.Shift | Keys.W), "키 글자 읽기");
        Ok(KeyMap.Format(Keys.Control | Keys.Shift | Keys.E) == "ctrl+shift+e" && KeyMap.Format(Keys.Control | Keys.D1) == "ctrl+1" && KeyMap.Format(Keys.OemOpenBrackets) == "[", "키 글자 쓰기");
        int hits = 0;
        Registry.Menu("시험/명령", () => hits++);
        App.Settings["keys"] = new JsonObject { ["시험/명령"] = "ctrl+shift+q", ["view.actual"] = "ctrl+1" };
        Ok(KeyMap.Handle(Keys.Control | Keys.Shift | Keys.Q) && hits == 1, "바꾼 명령 단축키 실행");
        Ok(KeyMap.ResolveCmd("view.actual") == "보기/100%", "웹 판 명령 id → 메뉴 경로");
        App.Settings["keys"] = new JsonObject();
        App.Settings["toolKeys"] = new JsonObject { ["brush"] = new JsonArray("b", "p"), ["eraser"] = "b", ["view"] = "h", ["eyedropper"] = "" };
        Ok(KeyMap.ToolsForKey((JsonObject)App.Settings["toolKeys"], Keys.B).SequenceEqual(new[] { "brush", "eraser" }), "같은 키 여러 도구 (설정 순서)");
        App.SetTool("hand");
        KeyMap.Handle(Keys.B); string t1 = App.Tool?.Id;
        KeyMap.Handle(Keys.B); string t2 = App.Tool?.Id;
        KeyMap.Handle(Keys.B); string t3 = App.Tool?.Id;
        Ok(t1 == "brush" && t2 == "eraser" && t3 == "brush", $"누를 때마다 차례로 ({t1}→{t2}→{t3})");
        KeyMap.Handle(Keys.P); Ok(App.Tool?.Id == "brush", "두 번째 키 P");
        App.SetTool("hand");
        Ok(KeyMap.Handle(Keys.I) && App.Tool?.Id == "hand", "키를 뺀 도구(스포이트 I)는 안 바뀜");
        KeyMap.Handle(Keys.H); Ok(App.Tool?.Id == "hand", "웹 판 'view' = 화면 이동(hand)");
        App.Settings["toolKeys"] = new JsonObject();
        Ok(!KeyMap.Handle(Keys.B), "toolKeys 가 없으면 메인 창 기본 처리에 맡김");
        App.Settings.Remove("toolKeys"); App.Settings.Remove("keys");
    }

    static void TestFeedback()
    {
        var a = Document.New(200, 100, "보내는 쪽");
        var r = new Layer(a, "러프") { Rough = true, Tint = "#3d7be0" };
        r.Px.Set(10, 20, 0xFFFF0000);
        a.Layers.Add(r);
        a.Extra["notes"] = new JsonArray(new JsonObject { ["id"] = "n1", ["x"] = 50, ["y"] = 40, ["text"] = "여기 고쳐 주세요", ["color"] = "#e5484d" });
        var fb = Feedback.Build(a, new[] { r }, true, "편집자");
        Ok((string)fb["format"] == "comicpad-feedback" && (fb["layers"] as JsonArray).Count == 1 && (string)fb["notes"][0]["author"] == "편집자" && (int)fb["doc"]["width"] == 200, ".cpfb 만들기");
        var b = Document.New(400, 200, "받는 쪽");
        App.SetDoc(b);
        int before = b.Layers.Count;
        string msg = Feedback.Import(b, fb.ToJsonString());
        var folder = b.Layers.FirstOrDefault(l => l.IsFolder);
        var got = b.Layers.FirstOrDefault(l => l.Parent == folder?.Id);
        Ok(folder != null && folder.Name.StartsWith("피드백: 편집자") && got != null && got.Rough && b.Layers.IndexOf(got) < b.Layers.IndexOf(folder), "피드백 레이어 폴더로 받기");
        Ok(got != null && (got.Px.Get(20, 40) >> 24) > 0 && got.Px.W == 400, "원고 크기가 다르면 늘림");
        var notes = b.Extra["notes"] as JsonArray;
        Ok(notes?.Count == 1 && Math.Abs((double)notes[0]["x"] - 100) < 1e-9 && (string)notes[0]["id"] != "n1" && msg.Contains("크기가 달라"), "메모 핀 받기 (위치 맞춤·새 id)");
        App.History.Undo(b);
        Ok(b.Layers.Count == before && (b.Extra["notes"] as JsonArray)?.Count == 0, "피드백 받기 실행취소 한 번");
    }

    static void TestMacros()
    {
        int a = 0; string got = null;
        Registry.Menu("시험/하나", () => a++);
        Registry.Menu("시험/값", () => { var v = Dlg.Form("시험 창", new[] { Field.Num("n", "수", 1), Field.Text("t", "글", "x") }); got = v == null ? null : $"{(double)v["n"]}:{(string)v["t"]}"; });
        Registry.Menu("보기/시험 보기", () => a += 100);
        Macros.WrapCommands();
        Macros.StartRecord();
        Registry.Commands["시험/하나"](); Registry.Commands["보기/시험 보기"](); Registry.Commands["시험/하나"]();
        Dlg.FormAuto = t => t == "자동 액션 저장" ? new JsonObject { ["name"] = "시험 액션" } : null;
        Macros.StopRecord();
        Dlg.FormAuto = null;
        var list = App.Settings["macros"] as JsonArray;
        var m = list?.LastOrDefault() as JsonObject;
        Ok(m != null && (string)m["name"] == "시험 액션" && (m["steps"] as JsonArray).Select(x => (string)x["cmd"]).SequenceEqual(new[] { "시험/하나", "시험/하나" }), "기록 (보기 명령은 빼고)");
        a = 0;
        Macros.Play(m);
        Ok(a == 2, "재생");
        var m2 = new JsonObject { ["name"] = "값", ["steps"] = new JsonArray(new JsonObject { ["cmd"] = "시험/값", ["args"] = new JsonArray(), ["forms"] = new JsonArray(new JsonObject { ["title"] = "시험 창", ["values"] = new JsonObject { ["n"] = 7.0 } }) }) };
        Macros.Play(m2);
        Ok(got == "7:x", $"대화상자 값 자동 입력 ({got})");
    }

    static void TestLuma()
    {
        var d = Document.New(100, 100);
        d.Layers[0].Px.Write(new Rect(0, 0, 50, 100), Enumerable.Repeat(0xFFFF0000u, 5000).ToArray());
        using var b = new Bitmap(50, 50);
        ViewAids.RenderInto(b, d, new Rect(0, 0, 100, 100), 0.5, "gray", new Compositor());
        var red = b.GetPixel(5, 5); var white = b.GetPixel(40, 40);
        Ok(red.R == 76 && red.G == 76 && white.R == 255, $"흑백: 빨강 → {red.R}, 흰색 → {white.R}");
        ViewAids.RenderInto(b, d, new Rect(0, 0, 100, 100), 0.5, "steps", new Compositor());
        Ok(b.GetPixel(5, 5).R == ViewAids.Steps(76) && b.GetPixel(40, 40).R == (byte)Math.Round(0.96 * 255), "5단계 명암");
        using var fs = Img.FlattenScaled(d, 0.25, true, new Compositor());
        var c1 = fs.GetPixel(3, 10); var c2 = fs.GetPixel(20, 10);
        Ok(fs.Width == 25 && fs.Height == 25 && c1.R == 255 && c1.G == 0 && c2.R == 255 && c2.G == 255, "줄여서 합치기 (띠 단위)");
    }

    static void TestCompare()
    {
        using var a = new Bitmap(10, 10); using var b = new Bitmap(10, 10);
        using (var g = Graphics.FromImage(a)) g.Clear(Color.White);
        using (var g = Graphics.FromImage(b)) { g.Clear(Color.White); g.FillRectangle(Brushes.Black, 0, 0, 5, 2); }
        var (img, ratio) = Compare.Diff(a, b, 10, 10);
        Ok(Math.Abs(ratio - 0.1) < 1e-9 && img.GetPixel(1, 1).R == 235 && img.GetPixel(8, 8).R == 255, $"달라진 곳 비율 {ratio:0.00}");
        img.Dispose();
    }

    static void TestPen()
    {
        var data = PenDiag.Steps.ToDictionary(s => s.id, _ => new List<List<PenPt>>());
        var rnd = new Random(1);
        for (int s = 0; s < 4; s++) data["free"].Add(Enumerable.Range(0, 120).Select(i => new PenPt(i * 5.0, i * 2, 50 + s * 10, 0.1 + 0.8 * i / 120.0, 0, 0)).ToList());
        data["light"].Add(Enumerable.Range(0, 30).Select(i => new PenPt(i * 5.0, i, 0, 0.1, 0, 0)).ToList());
        data["light"].Add(Enumerable.Range(0, 30).Select(i => new PenPt(i * 5.0, i, 0, 0.12, 0, 0)).ToList());
        data["strong"].Add(Enumerable.Range(0, 30).Select(i => new PenPt(i * 5.0, i, 0, 0.9, 0, 0)).ToList());
        data["hold"].Add(Enumerable.Range(0, 500).Select(i => new PenPt(i * 5.0, 100, 100, 0.5, 0, 0)).ToList());
        var meta = new PenDiag.Meta { Type = "pen", Events = 100, Coalesced = 300 };
        meta.Lat.AddRange(new[] { 1.0, 2.0, 3.0 });
        var r = PenDiag.Analyze(data, meta);
        Ok(Math.Abs(r.Rate - 200) < 1e-6 && r.PressureOK && r.Jitter == 0 && (double)r.Rec["euroK"] == 1.4 && (double)r.Rec["pmax"] == 0.9 && Math.Abs((double)r.Rec["pmin"] - 0.08) < 1e-9 && r.Lat == 2,
            $"펜 진단 계산 (초당 {r.Rate:0}점, 추천 {r.Rec.ToJsonString()})");
        PenDiag.ApplyRec(r.Rec);
        Ok((double)App.Settings["penFeel"]["euroK"] == 1.4, "추천값 → settings.penFeel");
    }
}
