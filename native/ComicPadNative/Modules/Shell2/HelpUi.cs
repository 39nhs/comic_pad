// 도움말 창들: 기능 안내(F2, 웹 ui/guide.js), ComicPad 정보·라이선스, 단축키·제스처 안내(웹 settings.js showHelp),
// 문제 보내기(웹 ui/report.js — 이 PC 에서만 모으고, 사용자가 누를 때만 GitHub 작성 화면을 엶), 자동 점검(시험을 따로 실행).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class HelpUi
{
    // [제목, 설명, 명령 또는 tool:id] — 웹 판 GUIDE 와 같은 내용 (네이티브에 없는 것은 "열기" 버튼 없이 보임)
    static readonly (string sec, (string t, string d, string cmd)[] rows)[] Guide =
    {
        ("시작", new[] {
            ("새 그림 / 열기 / 저장", "Ctrl+N · Ctrl+O · Ctrl+S. 작업물 폴더는 파일 > 작업물 폴더 열기", "file.new"),
            ("여러 페이지 작품", "만화 > 새 작품. 페이지 패널에서 넘기고 순서를 바꿈", "story.new"),
            ("기능 찾기", "Ctrl+K 를 누르고 이름 일부를 치면 메뉴에 없는 기능도 바로 실행", "palette.open"),
            ("설명서", "전체 기능을 정리한 설명서 (찾기 가능, 인쇄 가능)", "help.manual"),
            ("화면 꾸미기", "자주 쓰는 도구·메뉴만 꺼내 두기, 패널 탭 고르기, 모든 기능 보기로 전환", "ui.customize"),
        }),
        ("그리기", new[] {
            ("펜·브러시", "B. 필압·손떨림 보정은 설정 > 펜, 이 펜에 맞추려면 펜 필기감 진단", "tool:brush"),
            ("펜 필기감 진단", "네 단계로 그어 보면 이 기기에 맞는 필압 범위·보정값을 적용", "pen.diagnose"),
            ("러프", "러프 연필은 러프 레이어에서만. 레이어 > 새 러프 레이어", "layer.newRough"),
            ("벡터 레이어", "그린 뒤에도 굵기·색 바꾸기, 교차점까지 지우기", "layer.newVector"),
            ("원형 메뉴", "마우스 오른쪽 버튼 또는 펜 측면 버튼(설정). 밀고 떼면 선택", "ui.radialEdit"),
            ("퀵바", "왼손으로 누르기 좋은 큰 버튼 + 크기 막대 (보기 > 퀵바)", "view.quickbar"),
            ("자 (투시·평행·방사)", "자 도구로 추가하고 끌어서 조정", "tool:ruler"),
        }),
        ("만화", new[] {
            ("컷 나누기", "컷 도구(K)로 칸 나누기, 대각선·다각형 컷", "tool:panel"),
            ("말풍선", "F. 만든 뒤 탭해서 옮기기·크기·꼬리·모양 수정", "tool:bubble"),
            ("밑색 한 번에", "선화로 닫힌 영역마다 색을 채운 밑색 레이어", "color.autoFlat"),
            ("메모 핀", "Y. 그림 위에 피드백 메모, 내보내기에는 안 나옴", "tool:memo"),
            ("진행표", "페이지마다 단계·작업 시간·마감일", "progress.open"),
            ("3D 소재", "데생 인형(체형·포즈), 손, 소품, 3D 모델 → 레이어로", "3d.open"),
        }),
        ("채색·보정", new[] {
            ("명도 확인", "Shift+Y. 흑백 → 5단계 → 끔", "view.luma"),
            ("용지 질감", "종이 결을 용지(와 그림 위)에", "paper.texture"),
            ("보정·조정 레이어", "레벨·곡선·색조 등, 원본을 지키려면 조정 레이어", "adjust.levels"),
        }),
        ("내보내기·인쇄", new[] {
            ("이미지 내보내기", "PNG/JPG", "file.export"),
            ("인쇄용 (PDF·1비트 TIFF)", "재단선 안·도련 포함", "file.printExport"),
            ("원고 설정", "B5 등 판형 → 재단선·도련·안전선 가이드", "print.setup"),
            ("인쇄·투고 전 점검", "해상도·도련·글자 안전선 등을 한 번에 점검", "print.preflight"),
            ("웹툰 플랫폼 규격", "가로폭 맞춤 → 나누기 → 장당 용량 맞춤", "file.webtoonExport"),
        }),
        ("저장·협업", new[] {
            ("버전 기록·비교", "저장할 때마다 이전 판 보관, 비교", "file.versions"),
            ("작업물 백업", "하루 한 번 zip 으로, 백업에서 작품 꺼내기", "file.backup"),
            ("피드백 파일", "러프·메모만 작은 파일로 주고받기", "feedback.export"),
        }),
        ("화면·도움", new[] {
            ("앱 밖 창으로", "보기 > 패널을 앱 밖 창으로 (다른 모니터·항상 위)", "popout.panel"),
            ("작업 화면 저장", "패널 배치·도구를 저장해 Ctrl+Alt+1~4 로 바꾸기", "workspace.manage"),
            ("단축키 / 제스처", "두 손가락 탭 = 실행취소 등", "help.keys"),
            ("업데이트", "새 버전 확인·적용 (서명 확인)", "help.update"),
            ("자동 점검", "주요 기능이 제대로 되는지 한 번에 시험 (작업 중인 그림·작업물은 그대로)", "selftest.run"),
        }),
    };

    static bool Has(string cmd) => cmd.StartsWith("tool:") ? Registry.Tools.Any(t => t.Id == KeyMap.NativeTool(cmd[5..])) : WebIds.Resolve(cmd) != null;
    static void RunCmd(string cmd) { if (cmd.StartsWith("tool:")) App.SetTool(KeyMap.NativeTool(cmd[5..])); else WebIds.Run(cmd); }

    public static void FeatureGuide()
    {
        var root = new Panel { BackColor = Opt.Bg };
        var q = new TextBox { Dock = DockStyle.Top, BackColor = Opt.Bg2, ForeColor = Opt.Fg, Font = new Font("Malgun Gothic", 11f), PlaceholderText = "찾을 기능 (예: 말풍선, 인쇄, 필압)" };
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(8) };
        root.Controls.Add(list); root.Controls.Add(q);
        using var f = Shell2Ui.Modal("기능 안내", root, 640, 640);
        string chosen = null;
        void Render()
        {
            list.SuspendLayout();
            foreach (Control c in list.Controls.Cast<Control>().ToList()) c.Dispose();
            string k = q.Text.Trim().ToLowerInvariant();
            foreach (var (sec, rows) in Guide)
            {
                var hit = rows.Where(r => k.Length == 0 || (r.t + r.d + sec).ToLowerInvariant().Contains(k)).ToList();
                if (hit.Count == 0) continue;
                list.Controls.Add(Opt.Title(sec));
                foreach (var (t, d, cmd) in hit)
                {
                    var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Opt.Bg, Margin = new Padding(0, 2, 0, 2) };
                    row.Controls.Add(new Label { Text = t, AutoSize = false, Width = 150, Font = new Font("Malgun Gothic", 9f, FontStyle.Bold), Margin = new Padding(0, 6, 4, 0) });
                    row.Controls.Add(new Label { Text = d, AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Opt.Muted, Margin = new Padding(0, 6, 4, 0) });
                    if (Has(cmd)) { var c = cmd; row.Controls.Add(Opt.Button("열기", () => { chosen = c; f.Close(); })); }
                    list.Controls.Add(row);
                }
            }
            if (list.Controls.Count == 0) list.Controls.Add(Opt.Help("찾는 기능이 없습니다. Ctrl+K 기능 검색도 써 보세요."));
            list.ResumeLayout();
        }
        q.TextChanged += (_, _) => Render();
        Render();
        f.Shown += (_, _) => q.Focus();
        f.ShowDialog(App.Main);
        if (chosen != null) RunCmd(chosen);
    }

    // ---- 정보·라이선스 --------------------------------------------------------------------------
    static string FindUp(params string[] names)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && d != null; i++, d = d.Parent)
            foreach (var n in names) { var p = Path.Combine(d.FullName, n); if (File.Exists(p)) return p; }
        return null;
    }

    public static void About()
    {
        var sb = new StringBuilder();
        foreach (var lf in new[] { FindUp("app/LICENSES.txt", "LICENSES.txt"), FindUp("LICENSE"), FindUp("NOTICE") })
            if (lf != null) try { sb.AppendLine("==== " + Path.GetFileName(lf) + " ====").AppendLine(File.ReadAllText(lf)).AppendLine(); } catch { }
        if (sb.Length == 0) sb.Append("라이선스 파일(LICENSES.txt)을 읽지 못했습니다. 설치 폴더의 LICENSE·NOTICE 파일을 확인하세요.");
        var body = new Panel { BackColor = Opt.Bg };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Opt.Bg2, ForeColor = Opt.Fg, Font = new Font("Consolas", 9f), Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n") };
        var top = new Label
        {
            Dock = DockStyle.Top, Height = 120, ForeColor = Opt.Fg, Padding = new Padding(4),
            Text = $"ComicPad 네이티브 판 (버전 {Updater.CurrentVersion}) — 내가 필요한 기능만 넣어 쓰는 만화 그림판.\n" +
                   $"작업물 위치: {App.DataDir}\n웹 판과 같은 .cpad 파일·세팅(.cpset)을 씁니다.\n" +
                   "ComicPad 는 Apache License 2.0 으로 배포하는 프로그램입니다. 이 프로그램으로 그린 그림의 권리는 그린 사람에게 있습니다.\n" +
                   "아래는 ComicPad 와 함께 들어 있는 라이브러리·AI 모델의 라이선스 전문입니다.",
        };
        body.Controls.Add(text); body.Controls.Add(top);
        using var f = Shell2Ui.Modal("ComicPad 정보 · 라이선스", body, 760, 600);
        f.ShowDialog(App.Main);
    }

    // ---- 단축키·제스처 ---------------------------------------------------------------------------
    public static string KeysText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("■ 터치 / 펜");
        sb.AppendLine("  두 손가락 끌기/벌리기/돌리기: 이동·확대·회전");
        sb.AppendLine("  두 손가락 탭: 실행취소 · 세 손가락 탭: 다시실행 · 네 손가락 탭: 패널 숨기기/보이기");
        sb.AppendLine("  한 손가락 끌기: 화면 이동 (손가락 그리기 꺼짐일 때) · 한 손가락 길게: 스포이트");
        sb.AppendLine("  펜 측면 버튼 + 그리기: 설정대로 (스포이트·지우개·화면 이동·원형 메뉴), 펜 뒤쪽 지우개: 지우개");
        sb.AppendLine("  마우스 오른쪽 버튼: 원형 메뉴 (밀고 떼면 선택) · 퀵바 손잡이 탭: 버튼 편집, 끌기: 이동");
        sb.AppendLine();
        sb.AppendLine("■ 키보드 보조");
        sb.AppendLine("  Space + 끌기: 화면 이동, Ctrl+Space: 확대, Shift+Space: 회전");
        sb.AppendLine("  Alt + 클릭: 스포이트 · 휠: 확대/축소, Alt+휠: 회전, Shift+휠: 좌우 이동");
        sb.AppendLine("  [ ]: 브러시 크기 · X: 전경/배경색 바꾸기 · Tab: 패널 숨기기 · F11: 전체 화면");
        sb.AppendLine();
        sb.AppendLine("■ 도구");
        foreach (var t in Registry.Tools.Where(t => t.Key != Keys.None)) sb.AppendLine($"  {t.Name}: {KeyMap.Format(t.Key)}");
        sb.AppendLine();
        sb.AppendLine("■ 명령");
        foreach (var m in Registry.Menus.Where(m => m.Run != null && m.Shortcut != Keys.None).OrderBy(m => m.Path)) sb.AppendLine($"  {m.Path.Replace("/", " › ")}: {KeyMap.Format(m.Shortcut)}");
        return sb.ToString();
    }

    public static void KeysHelp()
    {
        var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Opt.Bg2, ForeColor = Opt.Fg, Font = new Font("Malgun Gothic", 9.5f), Text = KeysText().Replace("\n", "\r\n").Replace("\r\r", "\r") };
        using var f = Shell2Ui.Modal("단축키 · 제스처", text, 640, 640);
        f.ShowDialog(App.Main);
    }

    // ---- 문제 보내기 ------------------------------------------------------------------------------
    public const string RepoUrl = "https://github.com/39nhs/comic_pad";

    /// <summary>보고서 글 (사용자 폴더 경로는 가림). 그림·작품 파일은 들어가지 않음</summary>
    public static string Collect()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Hide(string t) => string.IsNullOrEmpty(home) || home.Length <= 3 ? t : t.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine("## 무슨 일이 있었나요?").AppendLine("(여기에 어떤 작업을 하다가 어떤 문제가 생겼는지 적어 주세요)").AppendLine();
        sb.AppendLine("## 환경");
        sb.AppendLine($"- ComicPad: {Updater.CurrentVersion} (네이티브 판, .NET {Environment.Version})");
        sb.AppendLine($"- Windows: {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}");
        var scr = Screen.PrimaryScreen;
        sb.AppendLine($"- 화면: {scr?.Bounds.Width}×{scr?.Bounds.Height}, 펜 입력: {(Shell2Ui.Canvas?.PenRate > 0 ? $"있음 ({Shell2Ui.Canvas.PenRate}점/초)" : "?")}");
        sb.AppendLine($"- 메모리: {Environment.WorkingSet / 1_000_000}MB 사용 중");
        sb.AppendLine().AppendLine("## 최근 오류");
        string log = Path.Combine(Path.GetTempPath(), "comicpad-native-error.log");
        string tail = "";
        try { if (File.Exists(log)) { var all = File.ReadAllText(log); tail = all.Length > 6000 ? all[^6000..] : all; } } catch { }
        if (tail.Trim().Length == 0) sb.AppendLine("- 없음"); else sb.AppendLine("```").AppendLine(Hide(tail.Trim())).AppendLine("```");
        return Hide(sb.ToString());
    }

    /// <summary>GitHub 새 이슈 주소 (길면 앞부분만)</summary>
    public static string IssueUrl(string body)
    {
        if (body.Length > 6000) body = body[..6000] + "\n\n(길어서 잘림 — 나머지는 \"복사\" 해서 붙여 넣어 주세요)";
        string first = body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("(")) ?? "문제 보고";
        if (first.Length > 80) first = first[..80];
        return $"{RepoUrl}/issues/new?title={Uri.EscapeDataString("[문제] " + first)}&body={Uri.EscapeDataString(body)}";
    }

    public static void Report()
    {
        var body = new Panel { BackColor = Opt.Bg };
        var help = new Label { Dock = DockStyle.Top, Height = 48, ForeColor = Opt.Muted, Text = "아래 내용은 이 PC 에서만 모았고, 아직 어디에도 보내지 않았습니다. 사용자 폴더 경로는 가렸습니다. 확인하고 필요하면 고친 뒤 복사하거나 GitHub 에 올리세요. 그림·작품 파일은 들어가지 않습니다." };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = Opt.Bg2, ForeColor = Opt.Fg, Font = new Font("Consolas", 9.5f), Text = Collect().Replace("\r\n", "\n").Replace("\n", "\r\n") };
        var row = Opt.Row(Opt.Button("복사", () => { try { Clipboard.SetText(text.Text); App.Toast("복사했습니다"); } catch { } }), Opt.Button("GitHub 에 쓰기", () => Shell2Ui.OpenUrl(IssueUrl(text.Text.Replace("\r\n", "\n"))), true));
        row.Dock = DockStyle.Bottom;
        body.Controls.Add(text); body.Controls.Add(row); body.Controls.Add(help);
        using var f = Shell2Ui.Modal("문제 보내기", body, 720, 600);
        f.ShowDialog(App.Main);
    }

    // ---- 자동 점검: 이 프로그램의 시험들을 따로(창 없이, 임시 작업물 폴더로) 돌림 ---------------------------
    public static void SelfTestRun()
    {
        string exe = Environment.ProcessPath;
        if (exe == null) return;
        if (!Shell2Ui.Confirm("주요 기능 시험을 따로 실행합니다 (1~3분). 작업 중인 그림·작업물·설정은 건드리지 않습니다 (임시 폴더 사용). 시작할까요?", "시작")) return;
        string tmp = Path.Combine(Path.GetTempPath(), "ComicPad_점검_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(tmp);
        var tests = new (string name, string env, string[] args)[]
        {
            ("화면·명령 (Shell2)", "COMICPAD_SHELL2_SELFTEST", null),
            ("부가 기능 (Extras)", "COMICPAD_EXTRAS_TEST", null),
            ("필터·보정", "COMICPAD_FILTERS_SELFTEST", null),
            ("브러시", null, new[] { "--selftest", "brushes" }),
        };
        App.Toast("자동 점검 중…");
        Task.Run(() =>
        {
            var sb = new StringBuilder();
            int bad = 0;
            foreach (var (name, env, args) in tests)
            {
                string outDir = Path.Combine(tmp, name.Split(' ')[0]);
                Directory.CreateDirectory(outDir);
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                psi.Environment["COMICPAD_DATA"] = Path.Combine(tmp, "data");
                psi.Environment["COMICPAD_SETTINGS"] = Path.Combine(tmp, "settings.json");   // 사용자 설정 파일은 건드리지 않게
                if (env != null) { psi.Environment[env] = outDir; psi.ArgumentList.Add("--render"); psi.ArgumentList.Add(Path.Combine(tmp, "없음.cpad")); psi.ArgumentList.Add(Path.Combine(tmp, "x.png")); }
                else { foreach (var a in args) psi.ArgumentList.Add(a); psi.ArgumentList.Add(outDir); }
                try
                {
                    using var p = Process.Start(psi);
                    if (!p.WaitForExit(300_000)) { try { p.Kill(); } catch { } sb.AppendLine($"✗ {name}: 시간 초과"); bad++; continue; }
                    bool ok = p.ExitCode == 0; if (!ok) bad++;
                    sb.AppendLine($"{(ok ? "✓" : "✗")} {name}{(ok ? "" : $" (끝 코드 {p.ExitCode})")}");
                }
                catch (Exception e) { sb.AppendLine($"✗ {name}: {e.Message}"); bad++; }
            }
            sb.AppendLine().AppendLine($"결과 폴더: {tmp}");
            App.Main?.BeginInvoke((Action)(() =>
            {
                MessageBox.Show(App.Main, sb.ToString(), bad == 0 ? "자동 점검: 모두 통과" : $"자동 점검: {bad}개 실패");
            }));
        });
    }
}
