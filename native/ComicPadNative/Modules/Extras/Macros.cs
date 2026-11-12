// 자동 액션 (웹 판 ui/macros.js): 메뉴 명령과 대화상자 값을 기록했다가 다시 실행. 여러 쪽·여러 작품에 일괄 적용.
//  - 명령은 Registry.Commands 의 이름(메뉴 경로)으로 기록 → 재생할 때 같은 이름으로 실행
//  - 대화상자 값: 이 모듈의 값 입력 창(Dlg.Form)은 키 이름으로, 다른 모듈의 창(Opt.AskNumbers 등)은 입력 칸 순서로 기록
//  - 설정 settings.macros = [{ id, name, steps: [{ cmd, args: [], forms: [{ title, values }] }] }] (웹 판과 같은 모양)
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public static class Macros
{
    // 기록하지 않는 명령: 화면 보기·도움말·파일 열기/저장·기록 자체 등 (그림을 바꾸지 않는 것)
    static readonly Regex Skip = new(@"^(보기/|도움말/|파일/(새로 만들기|열기|저장|다른 이름으로|최근|설정|끝내기|작업물 백업)|편집/(실행취소|다시실행|자동 액션)|만화/(이전 페이지|다음 페이지))");

    sealed class Rec { public JsonArray Steps = new(); public JsonObject Current; }
    static Rec rec;
    static int depth;
    static bool wrapped;
    public static bool Recording => rec != null;
    public static event Action Changed;

    static JsonArray List => U.Arr("macros");

    /// <summary>모든 메뉴 명령을 감싸서 기록 (메인 창이 만들어진 뒤 한 번)</summary>
    public static void WrapCommands()
    {
        if (wrapped) return;
        wrapped = true;
        for (int i = 0; i < Registry.Menus.Count; i++)
        {
            var m = Registry.Menus[i];
            if (m.Run == null) continue;
            var w = Wrap(m.Path, m.Run);
            Registry.Menus[i] = m with { Run = w };
            Registry.Commands[m.Path] = w;
        }
        foreach (var k in Registry.Commands.Keys.ToList())
            if (!Registry.Menus.Any(m => m.Path == k) && Registry.Commands[k] is Action a) Registry.Commands[k] = Wrap(k, a);
        Dlg.FormSubmitted += (title, values) => { if (rec?.Current != null) ((JsonArray)rec.Current["forms"]).Add(new JsonObject { ["title"] = title, ["values"] = values }); };
        Application.Idle += WatchForeignDialogs;
    }

    static Action Wrap(string path, Action run) => () =>
    {
        bool top = rec != null && depth == 0 && !Skip.IsMatch(path);
        if (top)
        {
            var step = new JsonObject { ["cmd"] = path, ["args"] = new JsonArray(), ["forms"] = new JsonArray() };
            rec.Steps.Add(step); rec.Current = step;
            Changed?.Invoke();
        }
        depth++;
        try { run(); } finally { depth--; }
    };

    // ---- 다른 모듈의 대화상자 (Dlg.Form 이 아닌 창): 열리면 지켜보다가 확인할 때 값 기록 / 재생 때 값 넣고 확인 ----
    static readonly HashSet<Form> seen = new();
    static Func<string, JsonObject> foreignAuto;
    static void WatchForeignDialogs(object s, EventArgs e)
    {
        if (rec == null && foreignAuto == null) { seen.Clear(); return; }
        foreach (Form f in Application.OpenForms)
        {
            if (f == App.Main || !f.Modal || (f.Tag as string) == "cp-dlg" || !seen.Add(f)) continue;
            if (foreignAuto != null)
            {
                var vals = foreignAuto(f.Text);
                if (vals != null) { SetValues(f, vals); f.DialogResult = DialogResult.OK; continue; }
            }
            if (rec?.Current != null)
            {
                var step = rec.Current; var form = f;
                form.FormClosed += (_, _) => { if (form.DialogResult == DialogResult.OK) ((JsonArray)step["forms"]).Add(new JsonObject { ["title"] = form.Text, ["values"] = GetValues(form) }); seen.Remove(form); };
            }
        }
    }

    static IEnumerable<Control> Inputs(Form f) => U.All(f).Where(c => c is NumericUpDown || c is CheckBox || c is ComboBox || c is TextBox || c is TrackBar || c is RadioButton);
    static string KeyOf(Control c, int i) => c.Tag is string t && t.Length > 0 ? t : "#" + i;
    static JsonObject GetValues(Form f)
    {
        var o = new JsonObject(); int i = 0;
        foreach (var c in Inputs(f))
        {
            string k = KeyOf(c, i++);
            o[k] = c switch
            {
                NumericUpDown n => (double)n.Value, CheckBox cb => cb.Checked, RadioButton rb => rb.Checked, ComboBox co => co.SelectedIndex, TrackBar tb => tb.Value, TextBox t => t.Text, _ => null,
            };
        }
        return o;
    }
    static void SetValues(Form f, JsonObject vals)
    {
        int i = 0;
        foreach (var c in Inputs(f))
        {
            var v = vals[KeyOf(c, i++)];
            if (v == null) continue;
            try
            {
                switch (c)
                {
                    case NumericUpDown n: n.Value = Math.Clamp((decimal)(double)v, n.Minimum, n.Maximum); break;
                    case CheckBox cb: cb.Checked = (bool)v; break;
                    case RadioButton rb: rb.Checked = (bool)v; break;
                    case ComboBox co: co.SelectedIndex = Math.Clamp((int)v, -1, co.Items.Count - 1); break;
                    case TrackBar tb: tb.Value = Math.Clamp((int)v, tb.Minimum, tb.Maximum); break;
                    case TextBox t: t.Text = (string)v; break;
                }
            }
            catch { }
        }
    }

    // ---- 기록 ------------------------------------------------------------------------
    public static void StartRecord()
    {
        rec = new Rec();
        App.Toast("자동 액션 기록 중… 메뉴 명령·필터·보정을 실행하세요. 끝나면 \"기록 멈추기\"");
        Changed?.Invoke();
    }

    public static void StopRecord()
    {
        var r = rec; rec = null;
        Changed?.Invoke();
        if (r == null || r.Steps.Count == 0) { App.Toast("기록된 명령이 없습니다"); return; }
        var v = Dlg.Form("자동 액션 저장", new[]
        {
            Field.Text("name", "이름", $"액션 {List.Count + 1}"),
            Field.Info(string.Join("\n", r.Steps.Select((s, i) => $"{i + 1}. {Label((string)s["cmd"])}{(((JsonArray)s["forms"]).Count > 0 ? " (설정값 포함)" : "")}"))),
        }, "저장");
        if (v == null) return;
        string name = string.IsNullOrEmpty((string)v["name"]) ? "액션" : (string)v["name"];
        List.Add(new JsonObject { ["id"] = U.Uid("mc"), ["name"] = name, ["steps"] = r.Steps.DeepClone() });
        App.SaveSettingsSoon();
        App.Toast($"\"{name}\" 자동 액션을 저장했습니다 (편집 > 자동 액션)");
    }

    public static string Label(string cmd) => cmd?.Replace("/", " › ").TrimEnd('…') ?? "";

    /// <summary>한 번 실행 (대화상자는 기록한 값으로 자동 확인)</summary>
    public static void Play(JsonObject m)
    {
        foreach (var stepNode in (m["steps"] as JsonArray) ?? new JsonArray())
        {
            var step = (JsonObject)stepNode;
            string cmd = (string)step["cmd"];
            if (!Registry.Commands.TryGetValue(cmd, out var run) || run == null) { App.Toast($"없는 명령이라 건너뜀: {cmd}"); continue; }
            var queue = ((step["forms"] as JsonArray) ?? new JsonArray()).Select(x => (JsonObject)x.DeepClone()).ToList();
            JsonObject Take(string title)
            {
                int i = queue.FindIndex(f => (string)f["title"] == title);
                if (i < 0) return null;
                var v = queue[i]["values"] as JsonObject; queue.RemoveAt(i);
                return v ?? new JsonObject();
            }
            Dlg.FormAuto = Take; foreignAuto = Take;
            try { run(); }
            catch (Exception ex) { App.Toast($"{cmd}: {ex.Message}"); }
            finally { Dlg.FormAuto = null; foreignAuto = null; seen.Clear(); }
            Application.DoEvents();
        }
    }

    static void Batch(JsonObject m)
    {
        string name = (string)m["name"];
        string story = Story.StoryOf(App.Doc);
        string proj = Story.ProjectsDir;
        var folders = Directory.EnumerateFiles(proj, "*.cpad", SearchOption.AllDirectories).Select(f => Story.Rel(f)).Where(r => r != null)
            .Select(r => r.Contains('/') ? r.Split('/')[0] : "").Distinct().ToList();
        var opts = new List<(string, string)>();
        if (story != null) opts.Add(($"story:{story}", $"지금 작품의 모든 쪽 ({story})"));
        opts.AddRange(folders.Select(f => ($"folder:{f}", f.Length > 0 ? $"작품 폴더: {f}" : "맨 위 폴더의 그림들")));
        if (opts.Count == 0) { App.Toast("대상 파일이 없습니다"); return; }
        var v = Dlg.Form($"일괄 적용: {name}", new[]
        {
            Field.Select("where", "대상", opts[0].Item1, opts.ToArray()),
            Field.Segment("out", "결과", "export", ("export", "PNG 로 내보내기 (원본 그대로)"), ("save", "작품에 저장 (덮어쓰기, 버전 기록 남음)")),
        }, "실행");
        if (v == null) return;
        string where = (string)v["where"]; int c = where.IndexOf(':');
        string kind = where[..c], target = where[(c + 1)..];
        var files = kind == "story" ? Story.ListPages(target)
            : Directory.EnumerateFiles(target.Length > 0 ? Path.Combine(proj, target) : proj, "*.cpad", SearchOption.TopDirectoryOnly).Select(f => Story.Rel(f)).Where(r => r != null).OrderBy(r => r, StringComparer.Ordinal).ToList();
        if (files.Count == 0) { App.Toast("대상 파일이 없습니다"); return; }
        bool save = (string)v["out"] == "save";
        if (save && !Dlg.Confirm($"{files.Count}개 파일에 \"{name}\" 을(를) 적용하고 저장할까요? (이전 판은 버전 기록에 남습니다)", "적용하고 저장")) return;
        if (App.Doc?.Modified == true && App.Doc.FilePath != null && !Story.SaveCurrent()) return;
        if (App.Doc?.Modified == true && !FileCommands.ConfirmDiscard()) return;
        string back = App.Doc?.FilePath;
        int n = 0;
        string outDir = Path.Combine(U.ExportsDir, "액션_" + U.SafeName(name));
        U.Task($"일괄 적용 ({files.Count}개)", () =>
        {
            foreach (var f in files)
            {
                FileCommands.OpenPath(Story.Full(f));
                if (App.Doc?.FilePath == null || Story.Rel(App.Doc.FilePath) != f) continue;
                Play(m);
                if (save) Story.SaveCurrent();
                else
                {
                    Directory.CreateDirectory(outDir);
                    using var b = Img.Flatten(App.Doc, 1, true);
                    File.WriteAllBytes(Path.Combine(outDir, Regex.Replace(f, @"\.cpad$", "").Replace('/', '_') + ".png"), Img.Encode(b, "png"));
                    App.Doc.Modified = false;
                }
                n++;
            }
        });
        if (back != null && File.Exists(back)) FileCommands.OpenPath(back);
        App.Toast($"{n}개 파일에 적용했습니다{(save ? "" : $" (data/exports/액션_{name})")}");
    }

    public static void Dialog()
    {
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(8) };
        using var f = Dlg.Modal("자동 액션", body, 620, 520);
        Action after = null;
        void Render()
        {
            body.SuspendLayout();
            foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
            var help = Opt.Help("메뉴 명령·필터·보정을 순서대로 기록했다가 한 번에 다시 실행합니다. 대화상자에 넣은 값도 함께 기록됩니다. 그리기(펜 획)는 기록되지 않습니다.");
            help.MaximumSize = new Size(560, 0);
            body.Controls.Add(help);
            body.Controls.Add(rec != null
                ? Opt.Button($"기록 멈추기 ({rec.Steps.Count}개)", () => { after = StopRecord; f.Close(); }, true)
                : Opt.Button("● 새로 기록", () => { after = StartRecord; f.Close(); }, true));
            if (List.Count == 0) body.Controls.Add(Opt.Help("저장된 자동 액션이 없습니다."));
            foreach (var node in List.ToList())
            {
                var m = (JsonObject)node;
                var steps = string.Join(" → ", ((m["steps"] as JsonArray) ?? new JsonArray()).Select(s => Label((string)s["cmd"])));
                body.Controls.Add(new Label { Text = (string)m["name"], AutoSize = true, Font = new Font("Malgun Gothic", 9.5f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 0) });
                var sl = Opt.Help(steps); sl.MaximumSize = new Size(560, 0); body.Controls.Add(sl);
                body.Controls.Add(Opt.Row(
                    Opt.Button("실행", () => { after = () => { U.Task($"자동 액션: {(string)m["name"]}", () => Play(m)); App.Toast($"\"{(string)m["name"]}\" 실행 완료"); }; f.Close(); }),
                    Opt.Button("일괄…", () => { after = () => Batch(m); f.Close(); }),
                    Opt.Button("이름", () => { var v = Dlg.Form("이름 바꾸기", new[] { Field.Text("n", "이름", (string)m["name"]) }); if (!string.IsNullOrEmpty((string)v?["n"])) { m["name"] = (string)v["n"]; App.SaveSettingsSoon(); } Render(); }),
                    Opt.Button("지우기", () => { if (Dlg.Confirm($"\"{(string)m["name"]}\" 을(를) 지울까요?", "지우기")) { List.Remove(m); App.SaveSettingsSoon(); Render(); } })));
            }
            body.ResumeLayout();
        }
        Render();
        f.ShowDialog(App.Main);
        after?.Invoke();
    }

    /// <summary>기록 중 표시: 상태 줄에 빨간 버튼 (누르면 멈춤)</summary>
    public static void AttachIndicator()
    {
        var strip = U.All(App.Main).OfType<StatusStrip>().FirstOrDefault();
        if (strip == null) return;
        var b = new ToolStripStatusLabel { Text = "● 기록 중 (누르면 멈춤)", ForeColor = Color.White, BackColor = Color.FromArgb(229, 72, 77), Visible = false };
        b.Click += (_, _) => StopRecord();
        strip.Items.Add(b);
        Changed += () => b.Visible = rec != null;
    }
}
