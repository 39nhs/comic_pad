// 애니메이션·픽셀아트 모듈 등록: 합성 훅(트랙 폴더), 도구(모션 경로·픽셀 연필), 메뉴(애니·픽셀), 패널(애니 탭), .aseprite 열기, 단축키(, . Shift+Enter)
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Anim;

public sealed class AnimModule : IModule
{
    public void Init()
    {
        Hooks.AnimFolder = AnimRender.RenderFolder;
        Registry.Tool(new MotionTool());
        Registry.Tool(new PixelPencilTool());
        Registry.Panel("애니 타임라인", () => new AnimPanel(), "bottom", 300);
        FileCommands.Openers[".aseprite"] = Aseprite.Open;
        FileCommands.Openers[".ase"] = Aseprite.Open;

        bool HasDoc() => App.Doc != null;
        bool HasFrames() => App.Doc != null && AnimCore.FrameCount(App.Doc) > 0;
        void M(string path, Action run, int order, Keys key = Keys.None, Func<bool> enabled = null, Func<bool> check = null) => Registry.Menu("애니/" + path, run, key, enabled ?? HasDoc, check, order);
        M("애니메이션 만들기 (빈 프레임 3장)", () => AnimCore.CreateAnimation(3), 10);
        M("트랙 추가 (레이어처럼 따로 움직이는 줄)", () => AnimCore.AddTrack(), 20);
        M("최상위 레이어들을 프레임으로 바꾸기", AnimCore.LayersToAnimation, 30);
        Registry.Menu("애니/-1", null, order: 35);
        M("빈 프레임 추가", () => AnimCore.AddFrame("blank"), 40);
        M("현재 프레임 복제", () => AnimCore.AddFrame("copy"), 50);
        M("연결된 프레임 추가 (같은 그림 이어 쓰기)", () => AnimCore.AddFrame("link"), 60);
        M("칸 연결 끊기 (따로 고치기)", AnimCore.UnlinkCell, 70, enabled: HasFrames);
        M("칸 비우기", AnimCore.ClearCell, 80, enabled: HasFrames);
        M("프레임 삭제", AnimCore.DeleteFrame, 90, enabled: HasFrames);
        Registry.Menu("애니/-2", null, order: 95);
        M("이전 프레임", () => AnimCore.SetFrame(AnimCore.Frame(App.Doc) - 1), 100, Keys.Oemcomma, HasFrames);
        M("다음 프레임", () => AnimCore.SetFrame(AnimCore.Frame(App.Doc) + 1), 110, Keys.OemPeriod, HasFrames);
        M("재생 / 정지", Playback.Toggle, 120, Keys.Shift | Keys.Enter, HasFrames, () => AnimCore.Playing);
        M("어니언 스킨 켜기/끄기", () => { var a = App.Doc.Anim; a["onion"] = !J.T(a["onion"]); App.Doc.Modified = true; AnimCore.Refresh(); }, 130, check: () => App.Doc != null && J.T(App.Doc.Anim["onion"]));
        M("라이트 테이블에 올리기/내리기 (지금 칸)", AnimCore.ToggleLight, 140, enabled: HasFrames);
        M("태그(구간) 만들기…", () => AnimDialogs.Tag(), 150, enabled: HasFrames);
        M("타임시트 (노출표) 보기·고치기…", AnimDialogs.Timesheet, 160, enabled: HasFrames);
        M("타임라인 창", TimelineWindow.Toggle, 165);
        Registry.Menu("애니/-3", null, order: 170);
        M("모션 경로 도구 (트윈)", () => App.SetTool("motion"), 180);
        M("모션을 그림으로 굳히기", () => { int k = AnimCore.BakeMotion(); if (k > 0) App.Toast($"움직인 프레임 {k}장을 그림으로 만들었습니다"); }, 190, enabled: HasFrames);
        Registry.Menu("애니/-4", null, order: 195);
        M("움짤 만들기 (바로, GIF)", AnimDialogs.QuickGif, 200, enabled: HasFrames);
        M("애니메이션 내보내기 (GIF·APNG·PNG)…", () => AnimDialogs.Export(), 210, enabled: HasFrames);
        M("스프라이트 시트 내보내기 (PNG + JSON)…", AnimDialogs.Sheet, 220, enabled: HasFrames);
        M("스프라이트 시트 가져오기…", AnimDialogs.ImportSheet, 230, enabled: () => true);
        M("Aseprite 파일 열기 (.aseprite/.ase)…", AnimDialogs.ImportAseprite, 240, enabled: () => true);
        M("Aseprite 파일로 저장 (.aseprite)", () => Ui.Busy("Aseprite 저장", () => Aseprite.Export(App.Doc)), 250);

        // 픽셀아트 (웹: 파일·보기·필터 메뉴)
        Registry.Menu("파일/픽셀 아트 새로 만들기…", PixelTools.NewPixelDoc, order: 15);
        Registry.Menu("보기/픽셀 격자 (많이 확대했을 때)", PixelTools.ToggleGrid, Keys.None, null, () => PixelTools.GridOn, order: 90);
        Registry.Menu("보기/픽셀아트로 보기 (축소해도 또렷하게, 이 작품)", PixelTools.ToggleCrisp, Keys.None, HasDoc, () => PixelArt.IsArt(App.Doc), order: 91);
        Registry.Menu("필터/-픽셀", null, order: 900);
        Registry.Menu("필터/색 수 줄이기 (팔레트·디더링)…", PixelTools.Quantize, Keys.None, HasDoc, order: 910);
        Registry.Menu("필터/팔레트 색 바꾸기 (같은 색 한꺼번에)…", PixelTools.ReplaceColor, Keys.None, HasDoc, order: 920);
        Registry.Menu("필터/팔레트 색으로만 칠하기 (이 작품)", PixelTools.ToggleLock, Keys.None, HasDoc, () => PixelArt.IsLocked(App.Doc), order: 930);
        Registry.Menu("필터/팔레트 가져오기 (.gpl∕.hex)…", PixelTools.ImportPalette, Keys.None, HasDoc, order: 940);
        Registry.Menu("필터/팔레트 내보내기 (.gpl∕.hex)…", PixelTools.ExportPalette, Keys.None, null, order: 950);
        Registry.Menu("필터/가져온 팔레트를 앱 팔레트로 쓰기 (Aseprite)", PixelTools.UseImportedPalette, Keys.None, () => Aseprite.PaletteOf(App.Doc)?.Count > 0, order: 960);

        // 이벤트
        string lastActive = null;
        // 아래 타임라인: 평소에는 숨기고, 애니메이션 트랙이 생기거나 그런 작품을 열면 보이게
        bool hadTracks = false;
        void ShowTimelineIfAnim()
        {
            bool has = AnimCore.Tracks(App.Doc).Count > 0;
            if (has != hadTracks && App.Main is UI.MainForm f && !App.Get("bottomDock", false)) f.PeekBottomDock(has);   // 늘 보이기로 정했으면 그대로
            hadTracks = has;
        }
        App.DocChanged += () =>
        {
            if (AnimCore.Playing) Playback.Stop();
            AnimCore.Migrate(App.Doc);
            lastActive = App.Doc?.ActiveId;
            AnimCore.Emit();
            hadTracks = false; ShowTimelineIfAnim();
        };
        App.LayersChanged += ShowTimelineIfAnim;
        // 레이어 패널에서 셀을 고르면 그 셀이 보이는 프레임으로
        App.LayersChanged += () =>
        {
            var d = App.Doc;
            if (d == null || d.ActiveId == lastActive) return;
            lastActive = d.ActiveId;
            var l = d.Active;
            if (l == null || AnimCore.Playing) return;
            var t = d.ById(d.ParentOf(l));
            if (!AnimCore.IsTrack(t)) return;
            var xs = AnimCore.XsOf(t, d);
            int f0 = AnimCore.Frame(d);
            if (f0 < xs.Count && xs[f0] == l.Id) return;
            int f = xs.IndexOf(l.Id);
            if (f >= 0) { d.Anim["frame"] = f; App.InvalidateAll(); AnimCore.Emit(); }
        };
        App.ColorChanged += PixelTools.OnColorChanged;
        PixelEdit.Finishing += PixelTools.OnPixelsFinishing;
        UI.CanvasView.CrispView = PixelArt.IsArt;
        Application.AddMessageFilter(new AnimKeys());
    }
}

/// <summary>
/// 글쇠 하나짜리 단축키 (, . Shift+Enter). MainForm 은 Ctrl/Alt 없는 메뉴 단축키를 실행하지 않아서 여기서 직접 받는다.
/// </summary>
public sealed class AnimKeys : IMessageFilter
{
    const int WM_KEYDOWN = 0x100;
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_KEYDOWN) return false;
        var f = Form.ActiveForm;
        if (f == null || (f != App.Main && f is not TimelineWindow)) return false;
        var c = Control.FromHandle(m.HWnd);
        if (c is TextBoxBase || c is NumericUpDown || c is ComboBox || c is DataGridView || c?.Parent is NumericUpDown || c?.Parent is DataGridView) return false;
        return Handle((Keys)(int)m.WParam | Control.ModifierKeys);
    }

    public static bool Handle(Keys k)
    {
        var d = App.Doc;
        if (d == null || AnimCore.FrameCount(d) == 0) return false;
        if (k == Keys.Oemcomma) { MainForm.Run(() => AnimCore.SetFrame(AnimCore.Frame(d) - 1), "이전 프레임"); return true; }
        if (k == Keys.OemPeriod) { MainForm.Run(() => AnimCore.SetFrame(AnimCore.Frame(d) + 1), "다음 프레임"); return true; }
        if (k == (Keys.Shift | Keys.Enter)) { MainForm.Run(Playback.Toggle, "재생 / 정지"); return true; }
        return false;
    }
}
