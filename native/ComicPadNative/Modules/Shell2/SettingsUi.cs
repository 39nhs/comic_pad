// 환경 설정 (웹 ui/settings.js 와 같은 항목·같은 설정 키) + 작업물 백업 창 (웹 ui/backup.js: 자동·지금·꺼내기).
using System.IO.Compression;
using System.Text.Json.Nodes;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class Shell2Settings
{
    static Control W(Label l) { l.MaximumSize = new Size(440, 0); return l; }

    /// <summary>설정 화면 내용 (대화상자 또는 앱 밖 창에 넣음)</summary>
    public static Control Body()
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(10) };
        void Add(params Control[] cs) { foreach (var c in cs) if (c != null) p.Controls.Add(c); }
        Add(Opt.Title("펜 (S펜·와콤·휴이온·XP-Pen·서피스 펜)"),
            Opt.Row(Opt.Button("필압 곡선·흔들림 보정…", PenSettings.Show), Opt.Button("펜 필기감 진단…", () => WebIds.Run("pen.diagnose"))),
            Opt.Choice("브러시 커서 모양", new[] { ("nib", "펜촉 + 크기 원 (클립스튜디오식)"), ("circle", "크기 원 + 십자"), ("cross", "십자만"), ("dot", "점만") }, () => App.Get("brushCursor", "nib"), v => { App.Set("brushCursor", v); CursorLook.Apply(); }),
            Opt.Choice("측면 버튼 누르고 그리기", new[] { ("eyedropper", "스포이트"), ("eraser", "지우개"), ("pan", "화면 이동"), ("radial", "원형 메뉴"), ("none", "사용 안 함") }, () => App.Get("penButton", "eyedropper"), v => App.Set("penButton", v)),
            Opt.Choice("두 번째 측면 버튼 (와콤 등)", new[] { ("pan", "화면 이동"), ("eyedropper", "스포이트"), ("eraser", "지우개"), ("radial", "원형 메뉴"), ("none", "사용 안 함") }, () => App.Get("penButton2", "pan"), v => App.Set("penButton2", v)),
            Opt.Check("펜 위치 예측 (선보다 조금 앞을 흐리게 미리 보여 줌)", () => Modules.Gaps.PenPredict.Enabled(), v => { if (App.Settings["penFeel"] is not System.Text.Json.Nodes.JsonObject f) App.Settings["penFeel"] = f = new System.Text.Json.Nodes.JsonObject(); f["predict"] = v; App.SaveSettingsSoon(); }),
            Opt.Check("마우스 오른쪽 버튼 = 원형 메뉴", () => App.Get("mouseRadial", true), v => App.Set("mouseRadial", v)),
            Opt.Slider("펜을 뗀 뒤 터치 무시 (ms)", 0, 1500, () => App.Get("palmGuardMs", 350.0), v => App.Set("palmGuardMs", Math.Round(v / 50) * 50), step: 50),
            Opt.Slider("마우스 필압", 0.05, 1, () => App.Get("mousePressure", 1.0), v => App.Set("mousePressure", Math.Round(v, 2)), pct: true),
            W(Opt.Help("와콤·휴이온·XP-Pen 등 PC 용 태블릿은 드라이버 설정에서 \"Windows Ink\" 를 켜야 필압·기울기가 옵니다. 펜 뒤쪽 지우개는 자동으로 지우개가 됩니다.")));
        Add(Opt.Title("터치"),
            Opt.Check("손가락으로 그리기 (끄면 손가락은 화면 조작 전용)", () => App.Get("fingerDraw", false), v => App.Set("fingerDraw", v)),
            Opt.Check("두 손가락으로 회전", () => App.Get("touchRotate", true), v => App.Set("touchRotate", v)),
            Opt.Check("두 손가락 탭 = 실행취소", () => App.Get("twoFingerUndo", true), v => App.Set("twoFingerUndo", v)),
            Opt.Check("세 손가락 탭 = 다시실행", () => App.Get("threeFingerRedo", true), v => App.Set("threeFingerRedo", v)),
            Opt.Check("한 손가락 길게 누르기 = 스포이트", () => App.Get("longPressEyedropper", true), v => App.Set("longPressEyedropper", v)));
        Add(Opt.Title("화면"),
            Opt.Choice("마우스 휠", new[] { ("zoom", "확대/축소"), ("scroll", "스크롤 (웹툰)") }, () => App.Get("wheelMode", "zoom"), v => App.Set("wheelMode", v)),
            Opt.Check("큰 버튼 (터치용)", () => App.Get("largeUI", false), v => { App.Set("largeUI", v); UiMode.ApplyLarge(); }),
            Opt.Check("왼손잡이 배치 (패널을 왼쪽에)", () => App.Get("leftHanded", false), v => { App.Set("leftHanded", v); Workspaces.ApplyLayout(); }),
            Opt.Check("간단히 보기 (자주 쓰는 도구·메뉴만)", () => UiMode.IsSimple, v => UiMode.SetMode(v ? "simple" : "full")),
            Opt.Slider("격자 간격 (px)", 10, 500, () => App.Get("gridSize", 100.0), v => { App.Set("gridSize", Math.Round(v / 10) * 10); Shell2Ui.Canvas?.RedrawAll(); }, step: 10),
            Opt.Row(Opt.Button("화면 꾸미기…", UiMode.Customize), Opt.Button("퀵바 버튼…", Bars.EditQuick), Opt.Button("아래 막대 버튼…", Bars.EditBottom)));
        Add(Opt.Title("저장"),
            Opt.Slider("자동 저장 간격 (분, 다시 켜면 적용)", 1, 30, () => App.Get("autosaveMin", 3.0), v => App.Set("autosaveMin", Math.Round(v)), step: 1),
            W(Opt.Help($"작업물 폴더: {App.DataDir}")),
            Opt.Row(Opt.Button("작업물 폴더 열기", () => Shell.OpenFolder(App.DataDir)), Opt.Button("설정 파일 폴더", () => Shell.OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ComicPad"))), Opt.Button("작업물 백업…", BackupUi.Dialog)),
            Opt.Slider("실행취소 기억 메모리 (MB)", 200, 6000, () => App.History.MaxBytes / 1_000_000.0, v => { App.History.MaxBytes = (long)(v * 1_000_000); App.Set("undoMB", Math.Round(v)); }, step: 50));
        Add(Opt.Title("업데이트"),
            Opt.Check("켤 때 새 버전 확인 (하루 한 번)", () => Updater.Conf()["auto"]?.GetValue<bool>() != false, v => { Updater.Conf()["auto"] = v; App.SaveSettingsSoon(); }),
            Opt.Button("업데이트 확인…", () => UpdateUi.Dialog()));
        return p;
    }

    public static void Dialog()
    {
        var body = Body();
        using var f = Shell2Ui.Modal("환경 설정", body, 500, 760);
        var pop = new Button { Text = "앱 밖 창으로", Dock = DockStyle.Bottom, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
        bool popOut = false;
        pop.Click += (_, _) => { popOut = true; f.Close(); };
        f.Controls.Add(pop);
        f.ShowDialog(App.Main);
        App.NotifySettings();
        App.SaveSettings();
        if (popOut) Popout.Settings();
    }
}

public static class BackupUi
{
    static JsonObject Conf() => Shell2Ui.Obj(App.Settings, "backup");
    static string Mb(long b) => b < 10_000_000 ? $"{b / 1e6:0.0}MB" : $"{b / 1e6:0}MB";

    /// <summary>백업 zip 안의 작품 파일 목록 (projects/…)</summary>
    public static List<(string name, long size)> Contents(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        return z.Entries.Where(e => e.FullName.StartsWith("projects/") && !e.FullName.EndsWith("/")).Select(e => (e.FullName, e.Length)).ToList();
    }

    /// <summary>백업에서 파일 하나 꺼내기: 지금 파일을 덮어쓰지 않고 "(복원 날짜)" 를 붙인 새 파일로. 만든 경로</summary>
    public static string Restore(string zip, string entry, string dataDir = null)
    {
        dataDir ??= App.DataDir;
        if (!entry.StartsWith("projects/") || entry.Split('/').Contains("..") || entry.Contains(':')) throw new InvalidDataException("꺼낼 수 없는 경로입니다");
        using var z = ZipFile.OpenRead(zip);
        var e = z.GetEntry(entry) ?? throw new FileNotFoundException(entry);
        string rel = entry["projects/".Length..].Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(dataDir, "projects", rel));
        if (!full.StartsWith(Path.GetFullPath(Path.Combine(dataDir, "projects")), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("꺼낼 수 없는 경로입니다");
        string dir = Path.GetDirectoryName(full), name = Path.GetFileNameWithoutExtension(full), ext = Path.GetExtension(full);
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd");
        string target = Path.Combine(dir, $"{name} (복원 {stamp}){ext}");
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(dir, $"{name} (복원 {stamp} {i}){ext}");
        e.ExtractToFile(target);
        return target;
    }

    public static void Dialog()
    {
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(10) };
        using var f = Shell2Ui.Modal("작업물 백업", body, 640, 620);
        bool running = false;
        void Render()
        {
            if (f.IsDisposed) return;
            body.SuspendLayout();
            foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
            var c0 = Conf();
            body.Controls.Add(new Label { Text = $"작품·에셋·글꼴·설정을 zip 하나로 묶어 {Shell.BackupDir} 에 둡니다. AI 모델은 넣지 않습니다.", AutoSize = true, MaximumSize = new Size(580, 0), ForeColor = Opt.Muted });
            body.Controls.Add(Opt.Title("자동 백업"));
            body.Controls.Add(Opt.Check("하루 한 번 자동으로 백업", () => c0["on"]?.GetValue<bool>() != false, v => { c0["on"] = v; App.SaveSettingsSoon(); }));
            body.Controls.Add(Opt.Slider("보관 개수", 1, 60, () => U.Num(c0["keep"], 14), v => { c0["keep"] = Math.Round(v); App.SaveSettingsSoon(); }, step: 1));
            body.Controls.Add(Opt.Row(
                Opt.Button(running ? "백업하는 중…" : "지금 백업", async () =>
                {
                    if (running) return; running = true; Render();
                    var r = await Task.Run(Shell.MakeBackup);
                    running = false; App.Toast(r != null ? "백업함: " + Path.GetFileName(r) : "백업하지 못했습니다"); Render();
                }, true),
                Opt.Button("백업 폴더 열기", () => Shell.OpenFolder(Shell.BackupDir))));
            body.Controls.Add(Opt.Title("백업 목록 (작품 꺼내기)"));
            var list = Directory.Exists(Shell.BackupDir) ? Directory.GetFiles(Shell.BackupDir, "ComicPad_백업_*.zip").OrderByDescending(x => x).ToList() : new();
            if (list.Count == 0) body.Controls.Add(Opt.Help("아직 백업이 없습니다."));
            foreach (var zip in list)
            {
                var fi = new FileInfo(zip);
                var head = Opt.Button($"▸ {fi.Name} — {Mb(fi.Length)} · {fi.LastWriteTime:yyyy-MM-dd HH:mm}", () => { });
                var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(18, 0, 0, 6), BackColor = Opt.Bg };
                bool loaded = false;
                head.Click += (_, _) =>
                {
                    inner.Visible = !inner.Visible;
                    if (!inner.Visible || loaded) return;
                    loaded = true;
                    try
                    {
                        var files = Contents(zip);
                        if (files.Count == 0) inner.Controls.Add(Opt.Help("작품 파일이 없습니다."));
                        foreach (var (name, size) in files)
                        {
                            string shown = name["projects/".Length..];
                            inner.Controls.Add(Opt.Row(new Label { Text = $"{shown}  ({Mb(size)})", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, Opt.Button("꺼내기", () =>
                            {
                                if (!Shell2Ui.Confirm($"\"{shown}\" 을(를) 백업에서 꺼낼까요?\n지금 파일은 덮어쓰지 않고 \"(복원 날짜)\" 를 붙인 새 파일로 만듭니다.", "꺼내기")) return;
                                try { var t = Restore(zip, name); App.Toast($"꺼냈습니다: {Path.GetFileName(t)} (파일 > 열기)"); }
                                catch (Exception ex) { App.Toast("꺼내지 못했습니다: " + ex.Message); }
                            })));
                        }
                    }
                    catch (Exception ex) { inner.Controls.Add(Opt.Help("읽지 못했습니다: " + ex.Message)); }
                };
                body.Controls.Add(head); body.Controls.Add(inner);
            }
            body.ResumeLayout();
        }
        Render();
        f.ShowDialog(App.Main);
    }
}
