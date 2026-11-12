// 애니 패널 (오른쪽 탭) + 타임라인 창 — 웹 판 ui/anim.js timelinePanel
// 위: 재생 막대 · 타임라인 격자(트랙 × 프레임, 썸네일), 아래: 지금 칸 · 태그 · 재생 · 어니언 스킨 · 라이트 테이블 · 모션 · 소리 · 립싱크 · 카메라 · 내보내기
// (MainForm 은 오른쪽 탭만 두므로 아래쪽 넓은 타임라인은 따로 뜨는 "타임라인 창"으로)
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Anim;

public static class AnimBars
{
    /// <summary>재생 막대: ◀ ▶/■ ▶ 위치 +빈 +복제 +연결 삭제 어니언 라이트</summary>
    public static Control Controls(Document d)
    {
        var a = d.Anim; int n = AnimCore.FrameCount(d), f0 = AnimCore.Frame(d);
        var cur = AnimCore.AnimFolder(d);
        Button Tog(string text, string tip, Action run, bool on) { var b = Ui.Btn(text, run, on, tip); return b; }
        var pos = new Label { Text = $"{f0 + 1} / {n}", AutoSize = true, ForeColor = Ui.Fg, Margin = new Padding(4, 6, 6, 0), Font = new Font("Malgun Gothic", 9.5f, FontStyle.Bold) };
        bool lit = cur != null && AnimCore.LightIds(d).Contains(AnimCore.CelAt(cur, f0, d)?.Id ?? "\0");
        var row = Ui.Row(
            Ui.Btn("◀", () => AnimCore.SetFrame(AnimCore.Frame(App.Doc) - 1), tip: "이전 (,)"),
            Ui.Btn(AnimCore.Playing ? "■" : "▶", Playback.Toggle, AnimCore.Playing, "재생/정지 (Shift+Enter)"),
            Ui.Btn("▶|", () => AnimCore.SetFrame(AnimCore.Frame(App.Doc) + 1), tip: "다음 (.)"),
            pos,
            Ui.Btn("+빈", () => AnimCore.AddFrame("blank"), tip: "빈 프레임 추가 (지금 트랙에 새 그림)"),
            Ui.Btn("+복제", () => AnimCore.AddFrame("copy"), tip: "지금 그림을 복제해서 다음 프레임에"),
            Ui.Btn("+연결", () => AnimCore.AddFrame("link"), tip: "같은 그림을 다음 프레임에 이어 쓰기 (하나 고치면 같이 바뀜)"),
            Ui.Btn("삭제", AnimCore.DeleteFrame, tip: "프레임 삭제 (모든 트랙)"),
            Tog("어니언", "어니언 스킨", () => { a["onion"] = !J.T(a["onion"]); App.Doc.Modified = true; AnimCore.Refresh(); }, J.T(a["onion"])),
            Tog("라이트", "지금 칸을 라이트 테이블에 올리기/내리기", AnimCore.ToggleLight, lit));
        row.MaximumSize = new Size(0, 0);
        return row;
    }
}

public sealed class AnimPanel : UserControl
{
    readonly FlowLayoutPanel body = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), BackColor = Opt.Bg };
    readonly System.Windows.Forms.Timer soon = new() { Interval = 60 };
    TimelineGrid grid;
    Control bar;
    bool wasPlaying;

    public AnimPanel()
    {
        BackColor = Opt.Bg; AutoScroll = true;
        Controls.Add(body);
        soon.Tick += (_, _) => { soon.Stop(); Render(); };
        Action changed = () =>
        {
            if (IsDisposed) return;
            // 재생 중에는 막대만 (아래 칸들은 재생이 멈추면)
            if (AnimCore.Playing && wasPlaying) { RefreshBar(); return; }
            soon.Stop(); soon.Start();
        };
        AnimCore.Changed += changed;
        App.LayersChanged += changed;
        App.DocChanged += changed;
        App.History.Changed += changed;
        Disposed += (_, _) => { AnimCore.Changed -= changed; App.LayersChanged -= changed; App.DocChanged -= changed; App.History.Changed -= changed; soon.Dispose(); };
        Render();
    }

    void RefreshBar()
    {
        var d = App.Doc;
        if (d == null || bar == null) return;
        int i = body.Controls.IndexOf(bar);
        var nb = AnimBars.Controls(d);
        nb.MaximumSize = bar.MaximumSize;
        body.Controls.Add(nb); body.Controls.SetChildIndex(nb, i);
        body.Controls.Remove(bar); bar.Dispose(); bar = nb;
    }

    void Render()
    {
        var d = App.Doc;
        wasPlaying = AnimCore.Playing;
        var keep = AutoScrollPosition;
        SuspendLayout(); body.SuspendLayout();
        foreach (Control c in body.Controls.Cast<Control>().ToList()) { if (c == grid) { body.Controls.Remove(c); continue; } c.Dispose(); }
        body.Controls.Clear();
        bar = null;
        try { if (d != null) Build(d); }
        catch (Exception ex) { body.Controls.Add(Ui.Help("애니 패널 오류: " + ex.Message)); }
        body.ResumeLayout(); ResumeLayout();
        AutoScrollPosition = new Point(-keep.X, -keep.Y);
    }

    void Build(Document d)
    {
        var ts = AnimCore.Tracks(d); int n = AnimCore.FrameCount(d);
        if (ts.Count == 0 || n == 0)
        {
            body.Controls.Add(Ui.Help("애니메이션 트랙(폴더) 안의 레이어 하나하나가 그림 칸이 됩니다. 트랙을 여러 개 만들면 몸·팔·배경을 따로 움직일 수 있습니다 (Aseprite·클립스튜디오식 타임라인)."));
            body.Controls.Add(Ui.Row(Ui.Btn("애니메이션 만들기", () => AnimCore.CreateAnimation(3), true), Ui.Btn("레이어들을 프레임으로", AnimCore.LayersToAnimation)));
            body.Controls.Add(Ui.Row(Ui.Btn("스프라이트 시트 가져오기", AnimDialogs.ImportSheet), Ui.Btn("Aseprite 파일 열기", AnimDialogs.ImportAseprite)));
            body.Controls.Add(Ui.Row(Ui.Btn("픽셀 아트 새로 만들기…", PixelTools.NewPixelDoc)));
            return;
        }
        var a = d.Anim; int f0 = AnimCore.Frame(d);
        bar = AnimBars.Controls(d);
        bar.MaximumSize = new Size(Math.Max(240, ClientSize.Width - 16), 0);
        body.Controls.Add(bar);
        grid ??= new TimelineGrid { Width = 296, Margin = new Padding(0, 2, 0, 2) };
        grid.Width = Math.Max(240, ClientSize.Width - 24);
        body.Controls.Add(grid);
        grid.Sync();
        body.Controls.Add(Ui.Row(Ui.Btn("+ 트랙", () => AnimCore.AddTrack(), tip: "트랙 추가"), Ui.Btn("칸 비우기", AnimCore.ClearCell, tip: "지금 트랙의 지금 칸을 비우기"), Ui.Btn("연결 끊기", AnimCore.UnlinkCell, tip: "이 칸만 따로 고칠 수 있게 복제"),
            Ui.Btn("◀ 칸", () => AnimCore.MoveCel(-1), tip: "지금 칸을 앞으로"), Ui.Btn("칸 ▶", () => AnimCore.MoveCel(1), tip: "지금 칸을 뒤로"), Ui.Btn("타임시트", AnimDialogs.Timesheet, tip: "노출표로 보기·고치기"),
            Ui.Btn("타임라인 창", TimelineWindow.Toggle, tip: "넓은 타임라인을 따로 띄우기")));

        // ---- 지금 칸 ----
        int hold = AnimCore.HoldOf(f0, d);
        var holds = Ui.Row(new[] { 1, 2, 3, 4, 6, 8 }.Select(k => (Control)Ui.Btn(k == 1 ? "1장" : $"{k}장", () => AnimCore.SetHold(f0, k), hold == k)).ToArray());
        double ms = J.D((a["ms"] as JsonArray)?.ElementAtOrDefault(f0), 0);
        body.Controls.Add(Ui.Section($"{f0 + 1}번 프레임",
            Ui.Help("지금 트랙의 그림을 몇 장 동안 보여 줄지 (뒤 칸이 밀림)"), holds,
            Ui.Slider("이 프레임 시간 (Aseprite식, 0 = 속도 따라감)", 0, 1000, 10, () => ms, v => ms = v, v => v > 0 ? $"{v:0}ms" : $"기본 ({Math.Round(1000 / AnimCore.Fps(d))}ms)", end: () => AnimCore.SetFrameMs(f0, ms > 0 ? ms : null))));

        // ---- 태그 ----
        var tagItems = new List<Control>();
        var tags = AnimCore.Tags(d).OfType<JsonObject>().ToList();
        for (int i = 0; i < tags.Count; i++)
        {
            int ii = i; var t = tags[i];
            string dir = J.S(t["dir"]) ?? "fwd";
            var sw = new Panel { Width = 12, Height = 12, Margin = new Padding(0, 6, 4, 0), BackColor = Hex(J.S(t["color"])) };
            bool playing = AnimCore.PlayTag(d) == i;
            tagItems.Add(Ui.Row(sw, Ui.Text($"{J.S(t["name"])} · {J.I(t["from"]) + 1}~{J.I(t["to"]) + 1} · {(dir == "rev" ? "←" : dir == "ping" ? "↔" : "→")}"),
                Ui.Btn(playing ? "전체 재생" : "이 구간만 재생", () => { a["playTag"] = playing ? null : ii; App.Doc.Modified = true; AnimCore.Emit(); }, playing),
                Ui.Btn("고치기", () => AnimDialogs.Tag(ii)), Ui.Btn("×", () => AnimCore.RemoveTag(ii), tip: "지우기")));
        }
        tagItems.Add(Ui.Row(Ui.Btn("태그 만들기…", () => AnimDialogs.Tag())));
        tagItems.Add(Ui.Help("걷기·점프처럼 구간에 이름을 붙이고 정방향·역방향·핑퐁으로 반복합니다. 스프라이트 시트 JSON 에도 들어갑니다."));
        body.Controls.Add(Ui.Section("태그 (구간)", tagItems.ToArray()));

        // ---- 재생 ----
        string pdir = J.S(a["dir"]) ?? "fwd";
        body.Controls.Add(Ui.Section("재생",
            Ui.Slider("속도", 1, 60, 1, () => AnimCore.Fps(d), v => { a["fps"] = (int)Math.Round(v); d.Modified = true; }, v => $"{v:0} fps", end: () => AnimCore.Emit()),
            Ui.Row(Ui.Check("반복", () => J.T(a["loop"]), v => { a["loop"] = v; d.Modified = true; App.InvalidateAll(); }), Ui.Text("방향"),
                Ui.Btn("→", () => { a["dir"] = "fwd"; d.Modified = true; AnimCore.Emit(); }, pdir == "fwd", "정방향"),
                Ui.Btn("←", () => { a["dir"] = "rev"; d.Modified = true; AnimCore.Emit(); }, pdir == "rev", "역방향"),
                Ui.Btn("↔", () => { a["dir"] = "ping"; d.Modified = true; AnimCore.Emit(); }, pdir == "ping", "핑퐁")),
            Ui.Row(Ui.Btn("미리보기 (카메라·소리·구간)", AnimDialogs.Preview))));

        // ---- 어니언 스킨 ----
        void Set(string k, JsonNode v) { a[k] = v; d.Modified = true; App.InvalidateAll(); }
        body.Controls.Add(Ui.Section("어니언 스킨",
            Ui.Row(Ui.Check("켜기", () => J.T(a["onion"]), v => { Set("onion", v); AnimCore.Emit(); }), Ui.Check("끝과 처음 이어서", () => J.T(a["loop"]), v => Set("loop", v))),
            Ui.Slider("진하기", 0.05, 0.8, 0.05, () => J.N(a["onionOpacity"]) ?? 0.3, v => Set("onionOpacity", Math.Round(v, 2)), v => $"{Math.Round(v * 100)}%"),
            Ui.Slider("멀수록 옅게", 0, 0.9, 0.05, () => J.N(a["onionFalloff"]) ?? 0.5, v => Set("onionFalloff", Math.Round(v, 2)), v => $"{Math.Round(v * 100)}%"),
            Ui.Slider("앞 장수", 0, 5, 1, () => J.D(a["onionPrev"], 0), v => Set("onionPrev", (int)Math.Round(v)), v => $"{v:0}"),
            Ui.Slider("뒤 장수", 0, 5, 1, () => J.D(a["onionNext"], 0), v => Set("onionNext", (int)Math.Round(v)), v => $"{v:0}"),
            Ui.Row(Ui.ColorBtn("앞 색", () => J.S(a["onionPrevColor"]) ?? AnimRender.PrevColor, v => Set("onionPrevColor", v)),
                Ui.ColorBtn("뒤 색", () => J.S(a["onionNextColor"]) ?? AnimRender.NextColor, v => Set("onionNextColor", v))),
            Ui.Help("같은 그림을 길게 보여 주는 칸은 건너뛰고 \"다른 그림\"만 잔상으로 보여 줍니다.")));

        // ---- 라이트 테이블 ----
        var L = a["light"] as JsonObject;
        var lightItems = new List<Control> { Ui.Help("올려 둔 그림은 어느 프레임에서나 흐리게 보입니다 (원화·중간 그림 그릴 때 기준). 내보내기에는 안 나옵니다.") };
        foreach (var id in AnimCore.LightIds(d))
        {
            var l = d.ById(id);
            if (l == null) continue;
            var sw = new Panel { Width = 12, Height = 12, Margin = new Padding(0, 6, 4, 0), BackColor = Hex(J.S(L?["color"]) ?? AnimRender.LightColor) };
            string lid = id;
            lightItems.Add(Ui.Row(sw, Ui.Text($"{d.ById(d.ParentOf(l))?.Name ?? ""} / {l.Name}"), Ui.Btn("내리기", () =>
            {
                var ids = J.Arr(J.Obj(a, "light"), "ids");
                foreach (var x in ids.Where(x => J.S(x) == lid).ToList()) ids.Remove(x);
                d.Modified = true; AnimCore.Refresh();
            })));
        }
        lightItems.Add(Ui.Row(Ui.Btn("지금 칸 올리기/내리기", AnimCore.ToggleLight)));
        if (AnimCore.LightIds(d).Count > 0) lightItems.Add(Ui.Slider("진하기", 0.05, 0.8, 0.05, () => J.N(L?["opacity"]) ?? 0.25, v => { J.Obj(a, "light")["opacity"] = Math.Round(v, 2); d.Modified = true; App.InvalidateAll(); }, v => $"{Math.Round(v * 100)}%"));
        body.Controls.Add(Ui.Section("라이트 테이블", lightItems.ToArray()));

        // ---- 모션 ----
        var mt = AnimCore.AnimFolder(d);
        if (mt != null) body.Controls.Add(MotionTool.Section(mt, d));
        body.Controls.Add(SoundSection(d));
        var lip = LipSection(d);
        if (lip != null) body.Controls.Add(lip);
        body.Controls.Add(CameraSection(d));
        body.Controls.Add(Ui.Row(Ui.Btn("움짤 만들기 (GIF)", AnimDialogs.QuickGif, true), Ui.Btn("내보내기 (형식 고르기)…", () => AnimDialogs.Export()), Ui.Btn("스프라이트 시트", AnimDialogs.Sheet)));
        body.Controls.Add(Ui.Row(Ui.Btn("Aseprite 로 저장", () => Ui.Busy("Aseprite 저장", () => Aseprite.Export(App.Doc)))));
        body.Controls.Add(Ui.Help("칸: 그림 = 새 그림 시작, 옅은 칸 = 앞 그림 이어 보이기, 점선 테두리 = 다른 프레임과 연결된 그림, 빈 칸 = 안 보임(두 번 누르면 새 그림). 트랙 이름을 누르면 그 줄을 고릅니다 (두 번 누르면 이름 바꾸기, 칸을 오른쪽 누르면 메뉴)."));
    }

    static Color Hex(string h) { try { return ColorTranslator.FromHtml(h ?? "#3d7be0"); } catch { return Color.SteelBlue; } }

    /// <summary>소리: 넣기·빼기·시작 위치·크기, 프레임 넘길 때 잠깐 듣기 (WAV)</summary>
    static Control SoundSection(Document d)
    {
        var au = Sound.Audio(d);
        bool ok = au != null && Sound.Decode(d) != null;
        var items = new List<Control>
        {
            au != null ? Ui.Help($"♪ {J.S(au["name"])}" + (ok ? "" : " — 이 형식은 네이티브 판에서 재생할 수 없습니다 (WAV 만). 데이터는 그대로 보관됩니다.")) : Ui.Help("배경음·대사 녹음(WAV)을 넣으면 재생할 때 함께 나오고, 프레임마다 소리 크기 막대가 보여 입 모양 맞추기에 좋습니다."),
            Ui.Row(Ui.Btn(au != null ? "다른 소리로" : "소리 넣기…", () => { using var od = new OpenFileDialog { Filter = "소리|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.ogg;*.opus|모든 파일|*.*" }; if (od.ShowDialog(App.Main) == DialogResult.OK) Sound.SetAudio(od.FileName); }), au != null ? Ui.Btn("빼기", Sound.RemoveAudio) : null),
        };
        if (au != null)
        {
            items.Add(Ui.Slider("시작 위치 (초)", -10, 10, 0.05, () => J.D(au["offset"], 0), v => { au["offset"] = Math.Round(v, 2); d.Modified = true; }, v => (v > 0 ? "+" : "") + v.ToString("0.00") + "초", end: () => AnimCore.Emit()));
            items.Add(Ui.Slider("크기", 0, 2, 0.05, () => J.N(au["volume"]) ?? 1, v => { au["volume"] = Math.Round(v, 2); d.Modified = true; }, v => $"{Math.Round(v * 100)}%"));
            items.Add(Ui.Check("프레임 넘길 때 소리 잠깐 듣기", () => App.Get("animScrub", true), v => App.Set("animScrub", v)));
        }
        return Ui.Section("소리", items.ToArray());
    }

    /// <summary>립싱크: 입 트랙과 모양별 그림을 고르고 자동 배치</summary>
    static Control LipSection(Document d)
    {
        var a = d.Anim; var ts = AnimCore.Tracks(d);
        if (!Sound.HasAudio(d) || ts.Count == 0) return null;
        var t = ts.FirstOrDefault(x => x.Id == J.S(a["lipTrack"])) ?? ts.FirstOrDefault(x => System.Text.RegularExpressions.Regex.IsMatch(x.Name, "입|mouth|lip", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) ?? AnimCore.AnimFolder(d);
        var kids = d.ChildrenOf(t);
        var map = new Dictionary<string, string>();
        if (t.Meta?["lipMap"] is JsonObject lm) foreach (var (k, v) in lm) map[k] = J.S(v);
        var items = new List<Control> { Ui.Help("입 모양 그림을 트랙 하나에 그려 두고(닫힘·반·ㅏ·ㅣ·ㅜ), 어느 그림이 어느 모양인지 고르면 소리에 맞춰 그 트랙의 칸을 자동으로 채웁니다. 소리 크기와 높낮이로 대략 고르는 것이라, 끝나고 타임라인에서 다듬으세요.") };
        var tsel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, BackColor = Ui.Bg2, ForeColor = Ui.Fg };
        foreach (var x in ts) tsel.Items.Add(x.Name);
        tsel.SelectedIndex = ts.IndexOf(t);
        tsel.SelectedIndexChanged += (_, _) => { a["lipTrack"] = ts[tsel.SelectedIndex].Id; d.Modified = true; AnimCore.Emit(); };
        items.Add(Ui.Row(Ui.Text("입 트랙"), tsel));
        foreach (var (shape, label) in LipSync.Shapes)
        {
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, BackColor = Ui.Bg2, ForeColor = Ui.Fg };
            cb.Items.Add("(없음)");
            for (int k = 0; k < kids.Count; k++) cb.Items.Add($"{k + 1}번 · {kids[k].Name}");
            int sel = map.TryGetValue(shape, out var cid) ? kids.FindIndex(c => c.Id == cid) + 1 : 0;
            cb.SelectedIndex = Math.Max(0, sel);
            string sh = shape;
            cb.SelectedIndexChanged += (_, _) => map[sh] = cb.SelectedIndex > 0 ? kids[cb.SelectedIndex - 1].Id : null;
            items.Add(Ui.Row(new Label { Text = label, Width = 120, ForeColor = Ui.Fg, Margin = new Padding(0, 6, 0, 0) }, cb));
        }
        bool extend = false;
        items.Add(Ui.Check("소리 길이만큼 프레임 늘리기", () => extend, v => extend = v));
        items.Add(Ui.Row(Ui.Btn("자동 배치", () =>
        {
            if (!map.Values.Any(x => x != null)) { App.Toast("모양마다 그림을 하나 이상 골라 주세요"); return; }
            var r = Ui.Busy<(int frames, Dictionary<string, int> count)?>("립싱크 분석", () => LipSync.Apply(t, map, extend));
            if (r is { } rr) App.Toast($"립싱크: {rr.frames}프레임 배치 ({string.Join(", ", LipSync.Shapes.Where(s => rr.count.GetValueOrDefault(s.id) > 0).Select(s => s.label.Split(' ')[0] + " " + rr.count[s.id]))})");
        }, true)));
        return Ui.Section("립싱크 (입 모양 자동)", items.ToArray());
    }

    /// <summary>카메라: 프레임마다 보이는 영역 (팬·줌·회전), 사이 프레임은 부드럽게 이어짐</summary>
    static Control CameraSection(Document d)
    {
        var a = d.Anim; int i = AnimCore.Frame(d);
        var cur = Camera.At(d, i) ?? new Cam { X = 0.5, Y = 0.5, Z = 1, R = 0 };
        var keys = Camera.Keys(d);
        bool isKey = keys.Any(k => J.I(k["f"]) == i);
        AnimCore.Snap0 snap = null;
        Control Sl(string label, string k, double min, double max, double step, Func<double, string> fmt)
        {
            double Get() { var c = Camera.At(d, i) ?? cur; return k switch { "x" => c.X, "y" => c.Y, "z" => c.Z, _ => c.R }; }
            return Ui.Slider(label, min, max, step, Get, v => { var key = Camera.Keys(d).FirstOrDefault(q => J.I(q["f"]) == i); if (key != null) { key[k] = Math.Round(v, 4); App.Invalidate(Rect.Empty); } }, fmt,
                begin: () => snap = AnimCore.Begin(d), end: () => { if (snap != null) { AnimCore.Commit(d, "카메라 키", snap); snap = null; } });
        }
        var items = new List<Control>();
        if (!isKey) items.Add(Ui.Help(keys.Count > 0 ? "이 프레임은 앞뒤 키 사이에서 자동으로 움직입니다. 값을 바꾸려면 키를 넣으세요." : "카메라 키를 두 개 이상 넣으면 그 사이 프레임에서 화면이 부드럽게 이동·확대합니다 (내보내기에 반영). 원고 위 영역은 모션 경로 도구에서 보입니다."));
        items.Add(Ui.Row(Ui.Btn(isKey ? "키 다시 넣기" : "이 프레임에 카메라 키", () => Camera.SetKey(d, i, null)), isKey ? Ui.Btn("키 지우기", () => Camera.RemoveKey(d, i)) : null));
        if (isKey)
        {
            items.Add(Sl("가로 위치", "x", 0, 1, 0.005, v => $"{Math.Round(v * 100)}%"));
            items.Add(Sl("세로 위치", "y", 0, 1, 0.005, v => $"{Math.Round(v * 100)}%"));
            items.Add(Sl("확대", "z", 1, 6, 0.01, v => $"{Math.Round(v * 100)}%"));
            items.Add(Sl("회전", "r", -45, 45, 0.5, v => $"{v:0.#}°"));
        }
        if (keys.Count > 0)
            items.Add(Ui.Row(Ui.Check("부드럽게 출발·정지", () => J.B(a["camera"]?["ease"], true), v => { J.Obj(a, "camera")["ease"] = v; d.Modified = true; }),
                Ui.Check("원고에 카메라 영역 표시", () => App.Get("showCamera", true), v => { App.Set("showCamera", v); App.InvalidateAll(); })));
        return Ui.Section($"카메라 ({i + 1}번 프레임{(isKey ? " · 키" : "")})", items.ToArray());
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (grid != null && !grid.IsDisposed) grid.Width = Math.Max(240, ClientSize.Width - 24);
    }
}

/// <summary>넓은 타임라인 창 (아래쪽 타임라인 대신 따로 뜨는 창)</summary>
public sealed class TimelineWindow : Form
{
    static TimelineWindow inst;
    readonly Panel top = new() { Dock = DockStyle.Top, Height = 34, BackColor = Opt.Bg };
    readonly TimelineGrid grid = new() { Dock = DockStyle.Fill };

    public static void Toggle()
    {
        if (inst != null && !inst.IsDisposed) { inst.Close(); return; }
        inst = new TimelineWindow();
        inst.Show(App.Main);
    }

    TimelineWindow()
    {
        Text = "타임라인"; BackColor = Opt.Bg; ForeColor = Opt.Fg; Font = new Font("Malgun Gothic", 9f);
        FormBorderStyle = FormBorderStyle.SizableToolWindow; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        var m = App.Main;
        var scr = m != null ? Screen.FromControl(m).WorkingArea : Screen.PrimaryScreen.WorkingArea;
        Bounds = new Rectangle(scr.Left + 80, scr.Bottom - 260, Math.Min(1300, scr.Width - 160), 230);
        Controls.Add(grid); Controls.Add(top);
        Action changed = () => { if (!IsDisposed) BeginInvoke(new Action(RefreshTop)); };
        AnimCore.Changed += changed; App.LayersChanged += changed; App.DocChanged += changed;
        FormClosed += (_, _) => { AnimCore.Changed -= changed; App.LayersChanged -= changed; App.DocChanged -= changed; };
        KeyPreview = true;
        RefreshTop();
    }

    void RefreshTop()
    {
        if (IsDisposed) return;
        foreach (Control c in top.Controls.Cast<Control>().ToList()) c.Dispose();
        top.Controls.Clear();
        var d = App.Doc;
        if (d == null) return;
        var bar = AnimBars.Controls(d);
        bar.Dock = DockStyle.Fill;
        top.Controls.Add(bar);
        grid.Sync();
    }


}
