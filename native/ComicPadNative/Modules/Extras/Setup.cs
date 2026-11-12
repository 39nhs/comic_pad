// 작업 화면 (웹 판 ui/workspace.js) + 초기 세팅 프리셋 (웹 판 ui/setuppresets.js).
//  - 작업 화면: 오른쪽 패널 탭·위아래 비율·패널 보이기·왼손잡이·도구·브러시를 이름 붙여 저장 (settings.workspaces, Ctrl+Alt+1~4)
//  - 세팅 프리셋: 클립스튜디오식·간단히·포토샵식·웹툰·태블릿. 바뀌는 설정만 바꾸고 전 상태를 setupBackup 에 (되돌리기 한 번)
//  - 처음 켰을 때 "처음 세팅 고르기" (settings.setupDone), 내 세팅 내보내기/가져오기 (.cpset)
// 설정 값은 웹 판과 같은 키·모양 (도구 id 도 웹 판 것: 화면 이동 = 'view' → 네이티브 'hand')
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public static class Workspaces
{
    static readonly string[] Keys_ = { "dock_top", "dock_bottom", "dockSplit", "sideVisible", "leftHanded", "quickbar", "bottombar" };
    static JsonArray List => U.Arr("workspaces");

    static MainForm Main => App.Main as MainForm;

    /// <summary>지금 화면 상태를 설정에 반영 (저장하기 전에)</summary>
    static void CaptureLayout()
    {
        var s = App.Settings; var m = Main;
        if (m == null) return;
        if (m.Side.Selected("top") is string t) s["dock_top"] = t;          // 웹 패널 id
        if (m.Side.Selected("bottom") is string b) s["dock_bottom"] = b;
        s["dockSplit"] = Math.Round(m.Side.SplitRatio, 3);
        s["sideVisible"] = App.Get("sideVisible", true);
    }

    public static JsonObject Snapshot(string name)
    {
        if (App.Main != null) CaptureLayout();
        var s = App.Settings;
        var o = new JsonObject { ["name"] = name, ["tool"] = App.Tool == null ? null : KeyMap.WebTool(App.Tool.Id), ["preset"] = new JsonObject { ["brush"] = s["activePreset"]?["brush"]?.DeepClone(), ["eraser"] = s["activePreset"]?["eraser"]?.DeepClone() } };
        var def = new Dictionary<string, JsonNode> { ["sideVisible"] = true, ["leftHanded"] = false, ["dockSplit"] = 0.48 };
        foreach (var k in Keys_) { var v = s[k]?.DeepClone() ?? (def.TryGetValue(k, out var d) ? d : null); if (v != null) o[k] = v; }
        return o;
    }

    public static void ApplyLayout()
    {
        var m = Main; if (m == null) return;
        m.ApplyLayout();          // 왼손잡이·패널 보이기·위아래 비율
        m.Side.SelectSaved();     // 설정의 dock_top / dock_bottom 탭
    }

    public static void Apply(JsonObject w)
    {
        var s = App.Settings;
        foreach (var k in Keys_) if (w[k] != null) s[k] = w[k].DeepClone();
        if (w["preset"] is JsonObject pr)
        {
            var ap = U.Obj("activePreset");
            if (pr["brush"] != null) ap["brush"] = pr["brush"].DeepClone();
            if (pr["eraser"] != null) ap["eraser"] = pr["eraser"].DeepClone();
        }
        ApplyLayout();
        if ((string)w["tool"] is string t) App.SetTool(KeyMap.NativeTool(t));
        App.NotifySettings();
        U.Canvas?.Fit();
        App.Toast($"작업 화면: {(string)w["name"]}");
    }

    public static void SaveDialog()
    {
        var v = Dlg.Form("작업 화면 저장", new[]
        {
            Field.Text("name", "이름 (예: 펜선, 채색, 보정)", ""),
            Field.Info("지금의 패널 탭·위아래 비율·패널 보이기·왼손잡이·도구·브러시를 저장합니다. 앞의 네 개는 Ctrl+Alt+1~4 로 바로 바꿀 수 있습니다."),
        }, "저장");
        string name = ((string)v?["name"])?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var arr = List;
        int i = arr.Select((x, k) => (x, k)).FirstOrDefault(t => (string)t.x?["name"] == name, (null, -1)).Item2;
        if (i >= 0) arr[i] = Snapshot(name); else arr.Add(Snapshot(name));
        App.SaveSettingsSoon();
        App.Toast($"\"{name}\" 작업 화면을 저장했습니다");
    }

    public static void Switch(int i)
    {
        if (i < List.Count && List[i] is JsonObject w) Apply(w);
        else App.Toast($"{i + 1}번 작업 화면이 없습니다 (보기 > 작업 화면 저장)");
    }

    public static void ManageDialog()
    {
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(8) };
        using var f = Dlg.Modal("작업 화면", body, 560, 480);
        JsonObject pick = null;
        void Render()
        {
            body.SuspendLayout();
            foreach (Control c in body.Controls.Cast<Control>().ToList()) c.Dispose();
            body.Controls.Add(Opt.Row(Opt.Button("지금 화면 저장…", () => { SaveDialog(); Render(); })));
            var arr = List;
            if (arr.Count == 0) body.Controls.Add(Opt.Help("저장한 작업 화면이 없습니다. \"지금 화면 저장\"을 눌러 보세요."));
            for (int i = 0; i < arr.Count; i++)
            {
                int k = i; var w = (JsonObject)arr[i];
                body.Controls.Add(new Label { Text = (string)w["name"] + (i < 4 ? $" · Ctrl+Alt+{i + 1}" : ""), AutoSize = true, Font = new Font("Malgun Gothic", 9.5f, FontStyle.Bold), Margin = new Padding(0, 8, 0, 0) });
                body.Controls.Add(Opt.Row(
                    Opt.Button("바꾸기", () => { pick = w; f.Close(); }, true),
                    Opt.Button("지금 화면으로 덮어쓰기", () => { arr[k] = Snapshot((string)w["name"]); App.SaveSettingsSoon(); App.Toast("지금 화면으로 덮어썼습니다"); Render(); }),
                    k > 0 ? Opt.Button("▲", () => { var a = arr[k - 1].DeepClone(); var b = arr[k].DeepClone(); arr[k - 1] = b; arr[k] = a; App.SaveSettingsSoon(); Render(); }) : null,
                    Opt.Button("지우기", () => { if (Dlg.Confirm($"\"{(string)w["name"]}\" 을(를) 지울까요?", "지우기")) { arr.RemoveAt(k); App.SaveSettingsSoon(); Render(); } })));
            }
            body.ResumeLayout();
        }
        Render();
        f.ShowDialog(App.Main);
        if (pick != null) Apply(pick);
    }
}

public sealed record SetupPreset(string Id, string Name, string Desc, string[] Changes, Action<JsonObject> Apply);

public static class Setup
{
    public const string DefaultSetup = "csp";
    static readonly string[] SimpleTools = { "brush", "eraser", "fill", "eyedropper", "select", "transform", "shape", "ruler", "panel", "bubble", "text", "view" };
    static readonly Dictionary<string, string[]> Frequent = new()
    {
        ["file"] = new[] { "file.new", "file.open", "file.save", "file.saveAs", "file.import", "file.export", "file.versions", "file.storage", "file.printExport", "file.webtoonExport", "file.backup", "pixel.newDoc", "file.ebook" },
        ["view"] = new[] { "view.fit", "view.actual", "view.zoomIn", "view.zoomOut", "view.rotL", "view.rotR", "view.resetRotation", "view.flip", "view.luma", "view.toggleUI", "view.fullscreen", "workspace.manage", "ui.customize", "ui.mode", "pixel.grid" },
    };
    static readonly string[] DefaultBottom = { "undo", "redo", "brushTool", "penEraser", "eyedropper", "hand", "size", "opacity", "deselect", "transform", "newLayer", "save" };

    static JsonObject B5_600 => new() { ["name"] = "흑백 만화 원고 B5 600dpi", ["w"] = 4441, ["h"] = 6213, ["dpi"] = 600, ["print"] = new JsonObject { ["size"] = "b5", ["mmW"] = 182, ["mmH"] = 257, ["bleedMm"] = 3, ["safeMm"] = 5 }, ["note"] = "재단선·도련·안전선 가이드 포함" };
    static JsonObject Webtoon => new() { ["name"] = "웹툰 (세로 긴 캔버스)", ["w"] = 800, ["h"] = 6400, ["dpi"] = 72, ["note"] = "800px 폭, 아래로 더 늘려 쓰기 가능" };
    static JsonObject PenDefaults => new() { ["penButton"] = "eyedropper", ["fingerDraw"] = false, ["touchRotate"] = true, ["twoFingerUndo"] = true, ["threeFingerRedo"] = true, ["longPressEyedropper"] = true, ["palmGuardMs"] = 350 };

    static JsonNode Keys(params string[] ks) => ks.Length == 1 ? ks[0] : new JsonArray(ks.Select(k => (JsonNode)k).ToArray());
    static JsonArray Arr(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode)x).ToArray());
    static JsonObject Spread(JsonNode o) => o is JsonObject j ? (JsonObject)j.DeepClone() : new JsonObject();
    static IEnumerable<string> Strs(JsonNode n) => (n as JsonArray)?.Select(x => (string)x) ?? Enumerable.Empty<string>();
    static void Assign(JsonObject s, JsonObject src) { foreach (var (k, v) in src) s[k] = v?.DeepClone(); }

    public static readonly SetupPreset[] Presets =
    {
        new("csp", "클립스튜디오식 (기본 · 흑백 만화)", "클립스튜디오에 익숙한 사람용. 도구 키 배치를 비슷하게, 펜 중심 도구 막대, 새 작품은 흑백 B5 600dpi 원고.", new[]
        {
            "도구 키: P·B 펜/브러시, E 지우개, G 채우기→그라데이션→올가미 채우기(누를 때마다), I 스포이트, M 선택→선택 펜, J 손끝→흐림, U 도형→컷 나누기→자, T 텍스트→말풍선, H 화면 이동, O 벡터 선 고르기",
            "도구 막대: 펜·컷·말풍선·자 중심 (그라데이션·올가미 채우기·벡터 선 고르기·유선 추가)",
            "브러시: G펜으로 시작",
            "펜: 필압 그대로(필압 다듬기·시작 굵기 줄이기 끔), 필압 곡선 직선, 커서는 펜촉 + 크기 원",
            "새 작품 추천: 흑백 만화 원고 B5 600dpi (재단선·도련 가이드)",
            "Ctrl+Shift+N 새 레이어, Ctrl+E 아래로 병합, Ctrl+Alt+G 클리핑, Ctrl+T 변형 등은 그대로 (이미 같음)",
        }, s =>
        {
            s["keys"] = new JsonObject();
            s["toolKeys"] = new JsonObject
            {
                ["brush"] = Keys("b", "p"), ["eraser"] = "e", ["fill"] = "g", ["gradient"] = "g", ["lassofill"] = "g", ["eyedropper"] = "i", ["select"] = "m", ["selpen"] = "m",
                ["smudge"] = "j", ["blur"] = "j", ["shape"] = "u", ["panel"] = "u", ["ruler"] = "u", ["text"] = "t", ["bubble"] = "t", ["view"] = "h", ["vselect"] = "o",
            };
            var ui = Spread(s["ui"]); ui["mode"] = "simple"; ui["tools"] = Arr(SimpleTools.Concat(new[] { "gradient", "lassofill", "vselect", "speedlines", "selpen" }).Distinct());
            s["ui"] = ui;
            var ap = Spread(s["activePreset"]); ap["brush"] = "gpen"; s["activePreset"] = ap;
            s["startDoc"] = B5_600;
            var pf = Spread(s["penFeel"]); pf["pressureSmooth"] = 0; pf["startRamp"] = false; s["penFeel"] = pf;
            s["pressurePts"] = null; s["pressureGamma"] = 1;
            s["brushCursor"] = "nib";
        }),
        new("comicpad", "간단히 (ComicPad 원래 화면)", "처음 설치했을 때와 같은 상태. 자주 쓰는 도구·메뉴만 보이고, 나머지는 \"모든 항목 보기\"·Ctrl+K 로.",
            new[] { "단축키·도구 키를 기본으로", "화면: 간단히 (자주 쓰는 것만)", "아래 큰 버튼 막대: 기본 버튼", "보통 크기 버튼, 펜 측면 버튼 = 스포이트", "새 작품 추천 크기 없음" }, s =>
        {
            s["keys"] = new JsonObject(); s["toolKeys"] = new JsonObject();
            s["ui"] = new JsonObject { ["mode"] = "simple" };
            s["bottombar"] = new JsonObject { ["visible"] = true, ["items"] = null };
            s["largeUI"] = false; s["leftHanded"] = false; s["sideVisible"] = true;
            Assign(s, PenDefaults);
            s["startDoc"] = null;
        }),
        new("ps", "포토샵식 (일러스트)", "포토샵에 익숙한 사람용. 도구 키·단축키를 비슷하게, 모든 메뉴를 보여 줌.", new[]
        {
            "도구 키: B 브러시, E 지우개, G 채우기→그라데이션, I 스포이트, M·L·W 선택, Q 선택 펜(빠른 마스크 느낌), V 이동/변형, T 텍스트, U 도형, H 화면 이동, S 복제 도장, A 벡터 선 고르기",
            "Ctrl+1 100% 보기, Ctrl+Shift+E 보이는 레이어 결합, Ctrl+Alt+Shift+W 이미지 내보내기",
            "Ctrl+J 복제, Ctrl+E 아래로 병합, Ctrl+G 폴더로 묶기, Ctrl+Alt+G 클리핑, Ctrl+T 변형, Alt+Backspace 채우기 (이미 같음)",
            "화면: 모든 기능 보기 (메뉴 전부)",
            "브러시: 부드러운 브러시로 시작",
        }, s =>
        {
            s["keys"] = new JsonObject { ["view.actual"] = "ctrl+1", ["layer.flatten"] = "ctrl+shift+e", ["file.export"] = "ctrl+alt+shift+w" };
            s["toolKeys"] = new JsonObject
            {
                ["brush"] = Keys("b", "p"), ["eraser"] = "e", ["fill"] = "g", ["gradient"] = "g", ["lassofill"] = "", ["eyedropper"] = "i", ["select"] = Keys("m", "l", "w"), ["selpen"] = "q", ["smudge"] = "",
                ["transform"] = "v", ["text"] = "t", ["shape"] = "u", ["view"] = "h", ["clone"] = "s", ["vselect"] = "a",
            };
            var ui = Spread(s["ui"]); ui["mode"] = "full"; s["ui"] = ui;
            var ap = Spread(s["activePreset"]); ap["brush"] = "soft"; s["activePreset"] = ap;
            s["startDoc"] = null;
        }),
        new("webtoon", "웹툰 (컬러, 세로 스크롤)", "컬러 웹툰용. 세로로 긴 캔버스, 웹툰 미리보기·플랫폼 규격 내보내기·아래로 늘리기를 가까이.", new[]
        {
            "새 작품 추천: 800px 폭 세로 긴 캔버스",
            "아래 막대에 \"웹툰 미리보기\", \"아래로 늘리기\", \"레이어\", \"색\" 버튼 추가",
            "보기 메뉴에 웹툰 미리보기, 파일 메뉴에 웹툰 플랫폼 규격 내보내기",
            "브러시: G펜으로 시작",
        }, s =>
        {
            s["startDoc"] = Webtoon;
            var items = s["bottombar"]?["items"] is JsonArray ia ? Strs(ia) : DefaultBottom;
            s["bottombar"] = new JsonObject { ["visible"] = true, ["items"] = Arr(items.Concat(new[] { "webtoonView", "extendDown", "layersTab", "colorTab" }).Distinct()) };
            var pick = Spread(s["ui"]?["menuPick"]);
            pick["view"] = Arr((pick["view"] is JsonArray pv ? Strs(pv) : Frequent["view"]).Concat(new[] { "view.webtoon", "edit.extendDown" }).Distinct());
            pick["file"] = Arr((pick["file"] is JsonArray pf ? Strs(pf) : Frequent["file"]).Concat(new[] { "file.webtoonExport" }).Distinct());
            var ui = Spread(s["ui"]); ui["menuPick"] = pick; s["ui"] = ui;
            var ap = Spread(s["activePreset"]); ap["brush"] = "gpen"; s["activePreset"] = ap;
        }),
        new("tablet", "펜·터치 태블릿 (S펜·아이패드 등)", "키보드 없이 펜과 손가락으로. 큰 버튼, 아래 막대 넓게, 펜 측면 버튼으로 원형 메뉴.", new[]
        {
            "큰 버튼 (터치용)",
            "아래 큰 버튼 막대: 실행취소·다시실행·브러시/지우개·스포이트·크기·농도 + 레이어·색·화면 맞춤·회전",
            "펜 측면 버튼 = 원형 메뉴",
            "손가락: 화면 이동·확대·회전, 두 손가락 탭 실행취소, 세 손가락 탭 다시실행, 길게 눌러 스포이트 (손가락으로 그리기는 끔)",
        }, s =>
        {
            s["largeUI"] = true;
            s["bottombar"] = new JsonObject { ["visible"] = true, ["items"] = Arr(DefaultBottom.Concat(new[] { "layersTab", "colorTab", "fit", "rotL", "rotR" }).Distinct()) };
            Assign(s, PenDefaults); s["penButton"] = "radial";
            s["sideVisible"] = true;
        }),
    };

    static readonly Dictionary<string, string> KeyLabel = new()
    {
        ["keys"] = "단축키", ["toolKeys"] = "도구 키", ["ui"] = "화면 구성(도구 막대·메뉴)", ["bottombar"] = "아래 큰 버튼 막대", ["largeUI"] = "큰 버튼",
        ["leftHanded"] = "왼손잡이 배치", ["sideVisible"] = "오른쪽 패널", ["penButton"] = "펜 측면 버튼", ["penButton2"] = "두 번째 측면 버튼", ["penNoPressure"] = "필압 없이 쓰기", ["fingerDraw"] = "손가락 그리기",
        ["touchRotate"] = "두 손가락 회전", ["twoFingerUndo"] = "두 손가락 실행취소", ["threeFingerRedo"] = "세 손가락 다시실행", ["longPressEyedropper"] = "길게 눌러 스포이트",
        ["palmGuardMs"] = "손바닥 무시 시간", ["startDoc"] = "새 작품 추천 크기", ["activePreset"] = "시작 브러시", ["brushes"] = "브러시", ["seenPresets"] = "브러시 목록 기록",
        ["workspaces"] = "작업 화면", ["palette"] = "팔레트", ["roughColor"] = "러프 색", ["radialItems"] = "원형 메뉴", ["quickbar"] = "떠 있는 퀵바",
        ["dock_top"] = "위 패널 탭", ["dock_bottom"] = "아래 패널 탭", ["dockSplit"] = "패널 위아래 비율", ["pressureGamma"] = "필압 곡선", ["penFeel"] = "필기감 보정", ["mousePressure"] = "마우스 필압",
    };
    static readonly HashSet<string> NotSettings = new() { "setupBackup", "setupDone" };

    /// <summary>바뀐 키 목록 (값이 다른 것만; 없음과 null 은 같게 봄)</summary>
    public static List<string> Diff(JsonObject a, JsonObject b) =>
        a.Select(kv => kv.Key).Concat(b.Select(kv => kv.Key)).Distinct().Where(k => !NotSettings.Contains(k) && !U.Same(a[k], b[k])).ToList();

    /// <summary>설정 사본에 프리셋을 적용해 본다 (원래 설정은 그대로)</summary>
    public static (JsonObject next, List<string> changed) ApplyPresetTo(JsonObject settings, string id)
    {
        var p = Presets.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException("없는 프리셋: " + id);
        var next = (JsonObject)settings.DeepClone();
        p.Apply(next);
        return (next, Diff(settings, next));
    }

    /// <summary>설정이 바뀐 뒤 화면 다시</summary>
    public static void RefreshAfterSettings()
    {
        if (App.Main != null) Workspaces.ApplyLayout();
        App.NotifySettings();
    }

    /// <summary>바뀔 키들의 지금 값을 백업하고, 새 값으로 바꾼다</summary>
    public static void Commit(JsonObject s, List<string> changed, JsonObject next, string label)
    {
        var values = new JsonObject(); var absent = new JsonArray();
        foreach (var k in changed) { if (s.ContainsKey(k)) values[k] = s[k]?.DeepClone(); else absent.Add(k); }
        s["setupBackup"] = new JsonObject { ["label"] = label, ["at"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(), ["values"] = values, ["absent"] = absent };
        foreach (var k in changed) { if (next.ContainsKey(k)) s[k] = next[k]?.DeepClone(); else s.Remove(k); }
    }

    public static bool UndoSetup(JsonObject s, bool ui = true)
    {
        if (s["setupBackup"] is not JsonObject b) { if (ui) App.Toast("되돌릴 세팅 변경이 없습니다"); return false; }
        foreach (var (k, v) in (b["values"] as JsonObject) ?? new JsonObject()) s[k] = v?.DeepClone();
        foreach (var k in Strs(b["absent"])) s.Remove(k);
        s.Remove("setupBackup");
        if (ui) { RefreshAfterSettings(); App.Toast($"\"{(string)b["label"]}\" 적용 전으로 되돌렸습니다"); }
        return true;
    }

    public static void ApplySetupPreset(string id, bool quiet = false)
    {
        var p = Presets.First(x => x.Id == id);
        var s = App.Settings;
        var (next, changed) = ApplyPresetTo(s, id);
        s["setupDone"] = id;
        if (changed.Count == 0) { if (!quiet) App.Toast($"이미 \"{p.Name}\" 세팅입니다"); App.SaveSettingsSoon(); return; }
        Commit(s, changed, next, p.Name);
        RefreshAfterSettings();
        OfferStartDoc();
        if (!quiet) App.Toast($"\"{p.Name}\" 세팅을 적용했습니다 (되돌리기: 도움말 > 세팅 되돌리기)");
    }

    // ---- 추천 새 작품 (startDoc) -----------------------------------------------------------
    public static Document StartDocument(JsonObject sd)
    {
        int w = U.Int(sd["w"]), h = U.Int(sd["h"]);
        string name = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace((string)sd["name"] ?? "제목 없음", @"^추천:\s*", ""), @"\s*\(.*\)", "");
        var d = Document.New(w, h, name);
        if (sd["dpi"] != null) d.Dpi = U.Int(sd["dpi"], 350);
        if (sd["print"] is JsonObject p)
        {
            int dpi = d.Dpi; int Mm(double v) => (int)Math.Round(v / 25.4 * dpi, MidpointRounding.AwayFromZero);
            d.Extra["print"] = new JsonObject
            {
                ["size"] = p["size"]?.DeepClone(), ["mmW"] = p["mmW"]?.DeepClone(), ["mmH"] = p["mmH"]?.DeepClone(), ["bleedMm"] = p["bleedMm"]?.DeepClone(), ["safeMm"] = p["safeMm"]?.DeepClone(),
                ["trimW"] = Mm(U.Num(p["mmW"])), ["trimH"] = Mm(U.Num(p["mmH"])), ["bleed"] = Mm(U.Num(p["bleedMm"])), ["safe"] = Mm(U.Num(p["safeMm"])),
            };
        }
        return d;
    }

    public static void NewFromStartDoc()
    {
        if (App.Settings["startDoc"] is not JsonObject sd) { App.Toast("추천 크기가 없습니다 (도움말 > 초기 세팅 프리셋 고르기)"); return; }
        if (!FileCommands.ConfirmDiscard()) return;
        App.SetDoc(StartDocument(sd));
        App.Settings["lastNew"] = new JsonObject { ["w"] = U.Int(sd["w"]), ["h"] = U.Int(sd["h"]), ["transparent"] = false };
        App.SaveSettingsSoon();
    }

    /// <summary>세팅 적용 직후: 지금 문서가 손대지 않은 새 문서면 추천 크기로 바꿔 줌</summary>
    static void OfferStartDoc()
    {
        var d = App.Doc;
        if (App.Settings["startDoc"] is not JsonObject sd || d == null || d.Modified || d.FilePath != null || App.History.CanUndo) return;
        if (d.W == U.Int(sd["w"]) && d.H == U.Int(sd["h"])) return;
        App.SetDoc(StartDocument(sd));
    }

    // ---- 고르기 창 ---------------------------------------------------------------------
    public static void ChooseDialog(bool firstRun = false)
    {
        var s = App.Settings;
        var root = new Panel { BackColor = Opt.Bg };
        var cards = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 300, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(6) };
        var detail = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(10) };
        SetupPreset chosen = null;
        var cardBtns = new List<Button>();
        void Show(SetupPreset p)
        {
            chosen = p;
            foreach (var b in cardBtns) { bool on = (string)b.Tag == p.Id; b.BackColor = on ? Opt.Accent : Opt.Bg2; b.ForeColor = on ? Color.Black : Opt.Fg; }
            var (_, changed) = ApplyPresetTo(s, p.Id);
            detail.SuspendLayout();
            foreach (Control c in detail.Controls.Cast<Control>().ToList()) c.Dispose();
            detail.Controls.Add(new Label { Text = p.Name, AutoSize = true, Font = new Font("Malgun Gothic", 11f, FontStyle.Bold) });
            detail.Controls.Add(new Label { Text = p.Desc, AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Opt.Muted, Margin = new Padding(0, 4, 0, 8) });
            foreach (var t in p.Changes) detail.Controls.Add(new Label { Text = "• " + t, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 2, 0, 2) });
            detail.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Opt.Muted, Margin = new Padding(0, 10, 0, 0),
                Text = firstRun ? "나중에 도움말 > 초기 세팅 프리셋 고르기 에서 언제든 바꿀 수 있습니다."
                    : changed.Count > 0 ? $"바뀌는 설정: {string.Join(", ", changed.Select(k => KeyLabel.GetValueOrDefault(k, k)))}. 적용 전 상태는 백업되어 \"되돌리기\" 한 번으로 돌아갑니다. 내가 만든 브러시·작업 화면·팔레트는 지우지 않습니다."
                    : "지금 설정과 같아서 바뀌는 것이 없습니다.",
            });
            var nh = Opt.Help("네이티브 판에서도 모두 쓰입니다: 도구 키·단축키, 화면 구성(간단히/모든 기능)·아래 막대·퀵바·원형 메뉴·큰 버튼, 펜 측면 버튼·손가락 제스처·브러시 커서, 오른쪽 패널, 새 작품 추천 크기.");
            nh.MaximumSize = new Size(440, 0); nh.Margin = new Padding(0, 10, 0, 0);
            detail.Controls.Add(nh);
            detail.ResumeLayout();
        }
        if (firstRun) cards.Controls.Add(new Label { Text = "ComicPad 에 오신 것을 환영합니다. 기본은 클립스튜디오식입니다. 다른 방식이 익숙하면 골라 주세요 (단축키·화면·새 작품 크기를 맞춰 드립니다).", AutoSize = true, MaximumSize = new Size(280, 0), ForeColor = Opt.Muted, Margin = new Padding(0, 0, 0, 8) });
        foreach (var p in Presets)
        {
            var b = new Button { Text = p.Name + "\n" + p.Desc, Tag = p.Id, Width = 280, Height = 78, TextAlign = ContentAlignment.TopLeft, FlatStyle = FlatStyle.Flat, BackColor = Opt.Bg2, ForeColor = Opt.Fg, Margin = new Padding(0, 0, 0, 6) };
            var pp = p; b.Click += (_, _) => Show(pp);
            cardBtns.Add(b); cards.Controls.Add(b);
        }
        string result = null;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, BackColor = Opt.Bg, Padding = new Padding(4) };
        Form f = null;
        bar.Controls.Add(Opt.Button("이 세팅으로 시작", () => { result = "apply"; f.Close(); }, true));
        bar.Controls.Add(Opt.Button(firstRun ? "기본(클립스튜디오식)으로 시작" : "닫기", () => { result = "skip"; f.Close(); }));
        if (!firstRun && s["setupBackup"] is JsonObject bk) bar.Controls.Add(Opt.Button($"되돌리기 (\"{(string)bk["label"]}\" 전으로)", () => { result = "undo"; f.Close(); }));
        root.Controls.Add(detail); root.Controls.Add(cards); root.Controls.Add(bar);
        f = new Form { Text = firstRun ? "처음 세팅 고르기" : "초기 세팅 프리셋", Width = 800, Height = 560, StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = new Font("Malgun Gothic", 9f) };
        root.Dock = DockStyle.Fill; f.Controls.Add(root);
        Show(Presets.FirstOrDefault(p => p.Id == (string)s["setupDone"]) ?? Presets.First(p => p.Id == DefaultSetup));
        using (f) f.ShowDialog(App.Main);
        if (result == "apply" && chosen != null) ApplySetupPreset(chosen.Id);
        else if (result == "undo") UndoSetup(s);
        else if (firstRun) ApplySetupPreset(DefaultSetup, true);   // 창을 그냥 닫아도 기본은 클립스튜디오식
        App.SaveSettingsSoon();
    }

    /// <summary>처음 켰을 때: 이미 쓰던 설정이 있으면 조용히 "완료"로, 아니면 고르기 창</summary>
    public static void FirstRunCheck()
    {
        var s = App.Settings;
        if (s["setupDone"] != null) return;
        bool used = s["lastNew"] != null || s["bottomMigrated"] != null || (s["keys"] is JsonObject k && k.Count > 0) || (s["workspaces"] is JsonArray w && w.Count > 0)
            || (s["recentFiles"] is JsonArray r && r.Count > 0) || s["newW"] != null;
        if (used) { s["setupDone"] = "existing"; App.SaveSettingsSoon(); return; }
        ChooseDialog(true);
    }

    // ---- .cpset 내보내기·가져오기 -----------------------------------------------------------
    public static readonly (string id, string label, string[] keys)[] SetParts =
    {
        ("keys", "단축키·도구 키", new[] { "keys", "toolKeys" }),
        ("layout", "화면 구성 (도구 막대·메뉴·패널·아래 막대·원형 메뉴·큰 버튼)", new[] { "ui", "dock_top", "dock_bottom", "dockSplit", "sideVisible", "leftHanded", "quickbar", "bottombar", "radialItems", "largeUI" }),
        ("brushes", "브러시 (내가 만든 것 포함)", new[] { "brushes", "activePreset" }),
        ("workspaces", "작업 화면", new[] { "workspaces" }),
        ("palette", "팔레트·러프 색", new[] { "palette", "roughColor" }),
        ("pen", "펜·터치 설정 (필압·필기감·제스처·측면 버튼)", new[] { "pressureGamma", "pressurePts", "brushCursor", "holdLine", "penButton2", "penNoPressure", "penFeel", "mousePressure", "fingerDraw", "palmGuardMs", "touchRotate", "twoFingerUndo", "threeFingerRedo", "longPressEyedropper", "penButton" }),
        ("startDoc", "새 작품 추천 크기", new[] { "startDoc" }),
        ("panelTemplates", "내 컷 나누기 템플릿", new[] { "panelTemplates" }),
    };

    public static JsonObject BuildSetFile(JsonObject settings, IEnumerable<string> parts)
    {
        var ps = parts.ToHashSet();
        var outp = new JsonObject();
        foreach (var (id, _, keys) in SetParts)
        {
            if (!ps.Contains(id)) continue;
            var o = new JsonObject();
            foreach (var k in keys) if (settings.ContainsKey(k)) o[k] = settings[k]?.DeepClone();
            outp[id] = o;
        }
        return new JsonObject { ["format"] = "comicpad-settings", ["version"] = 1, ["created"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["parts"] = outp };
    }

    /// <summary>받은 세팅을 사본에 합치기. 브러시·작업 화면은 합치고(같은 것은 바꿈), 나머지는 바꿈</summary>
    public static (JsonObject next, List<string> changed) MergeSetFile(JsonObject settings, JsonObject file, IEnumerable<string> parts)
    {
        var next = (JsonObject)settings.DeepClone();
        foreach (var id in parts)
        {
            if (file["parts"]?[id] is not JsonObject o) continue;
            foreach (var (k, v) in o)
            {
                if (k == "brushes" && v is JsonObject bv)
                {
                    var nb = Spread(next["brushes"]);
                    foreach (var (cat, list) in bv)
                    {
                        if (list is not JsonArray la) continue;
                        if (nb[cat] is not JsonArray cur) { cur = new JsonArray(); nb[cat] = cur; }
                        foreach (var b in la)
                        {
                            int i = cur.Select((x, n) => (x, n)).FirstOrDefault(t => (string)t.x?["id"] == (string)b?["id"], (null, -1)).Item2;
                            if (i >= 0) cur[i] = b?.DeepClone(); else cur.Add(b?.DeepClone());
                        }
                    }
                    next["brushes"] = nb;
                }
                else if (k == "workspaces" && v is JsonArray wv)
                {
                    var cur = next["workspaces"] is JsonArray wa ? (JsonArray)wa.DeepClone() : new JsonArray();
                    foreach (var w in wv)
                    {
                        int i = cur.Select((x, n) => (x, n)).FirstOrDefault(t => (string)t.x?["name"] == (string)w?["name"], (null, -1)).Item2;
                        if (i >= 0) cur[i] = w?.DeepClone(); else cur.Add(w?.DeepClone());
                    }
                    next["workspaces"] = cur;
                }
                else if (k == "activePreset" && v is JsonObject av)
                {
                    var ap = Spread(next["activePreset"]);
                    foreach (var (ak, aval) in av) ap[ak] = aval?.DeepClone();
                    next["activePreset"] = ap;
                }
                else next[k] = v?.DeepClone();
            }
        }
        return (next, Diff(settings, next));
    }

    public static void ExportSetDialog()
    {
        var fields = new List<Field> { Field.Info("고른 부분만 작은 파일 하나로 저장합니다. 다른 사람에게 건네면 \"세팅 가져오기\"로 똑같이 쓸 수 있습니다. 그림·작품은 들어가지 않습니다.") };
        fields.AddRange(SetParts.Select(p => Field.Check(p.id, p.label, p.id != "startDoc" || App.Settings["startDoc"] != null)));
        fields.Add(Field.Text("name", "파일 이름", "내 세팅"));
        var v = Dlg.Form("내 세팅 내보내기 (.cpset)", fields, "저장");
        if (v == null) return;
        var parts = SetParts.Select(p => p.id).Where(id => (bool?)v[id] == true).ToList();
        if (parts.Count == 0) { App.Toast("내보낼 부분을 하나 이상 골라 주세요"); return; }
        var file = BuildSetFile(App.Settings, parts);
        using var dlg = new SaveFileDialog { Filter = "ComicPad 세팅 (*.cpset)|*.cpset", FileName = $"{U.SafeName((string)v["name"] ?? "세팅")}_{U.FileStamp()}.cpset", InitialDirectory = U.ExportsDir };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, file.ToJsonString(U.Indent1));
        App.Toast("세팅 파일을 저장했습니다: " + dlg.FileName);
    }

    public static void ImportSetDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "ComicPad 세팅 (*.cpset)|*.cpset;*.json|모든 파일|*.*" };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        JsonObject file;
        try { file = JsonNode.Parse(File.ReadAllText(dlg.FileName)) as JsonObject; } catch { App.Toast("세팅 파일을 읽지 못했습니다"); return; }
        if ((string)file?["format"] != "comicpad-settings" || file["parts"] is not JsonObject) { App.Toast("ComicPad 세팅 파일(.cpset)이 아닙니다"); return; }
        var have = SetParts.Where(p => file["parts"][p.id] != null).ToList();
        if (have.Count == 0) { App.Toast("파일에 가져올 내용이 없습니다"); return; }
        var fields = new List<Field> { Field.Info("가져올 부분을 고르세요. 브러시·작업 화면은 내 것에 합쳐지고(이름/번호가 같으면 바뀜), 나머지는 바뀝니다. 가져오기 전 상태는 백업되어 되돌릴 수 있습니다.") };
        fields.AddRange(have.Select(p => Field.Check(p.id, p.label, true)));
        string fname = Path.GetFileName(dlg.FileName);
        var v = Dlg.Form($"세팅 가져오기: {fname}", fields, "가져오기");
        if (v == null) return;
        var parts = have.Select(p => p.id).Where(id => (bool?)v[id] == true).ToList();
        var (next, changed) = MergeSetFile(App.Settings, file, parts);
        if (changed.Count == 0) { App.Toast("지금 설정과 같아서 바뀐 것이 없습니다"); return; }
        Commit(App.Settings, changed, next, $"세팅 가져오기 ({fname})");
        RefreshAfterSettings();
        App.Toast($"세팅을 가져왔습니다: {string.Join(", ", changed.Select(k => KeyLabel.GetValueOrDefault(k, k)))}");
    }
}
