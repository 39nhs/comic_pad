// 비전 모듈: 웹 판에는 있고 네이티브 판에 없던 AI·영상 기능을 모음.
//  - 편집 > 내용 인식 채우기 (웹 ui/pstools.js), 복구 브러시 도구 (웹 tools/heal.js)
//  - AI > 이미지 AI 로 그리기 (이 PC 의 SD WebUI) · 연결 확인 (웹 ui/aitools.js)
//  - AI > 사진에서 인형 자세 (3D 소재 창의 "사진에서 자세/손 모양" 단추가 이 모듈의 BlazePose·BlazeHand ONNX 를 씀)
//  - 버튜버 스튜디오의 카메라 얼굴 추적 (Media Foundation 웹캠 + BlazeFace·FaceMesh ONNX)
//  - 자체 점검: 환경 변수 COMICPAD_VISION_SELFTEST=<폴더>
using ComicPadNative.Core;
using ComicPadNative.Modules.Scene3D;

namespace ComicPadNative.Modules.Vision;

public sealed class VisionModule : IModule
{
    static bool HasDoc() => App.Doc != null;

    public void Init()
    {
        VisionModels.RegisterAll();
        Registry.Tool(new HealTool());
        Registry.Menu("편집/-vision", null, order: 880);
        Registry.Menu("편집/내용 인식 채우기 (선택한 곳을 주변으로 메우기)…", ContentAwareFillDialog, enabled: () => App.Doc?.Sel.Active == true, order: 881);
        Registry.Menu("편집/복구 브러시 (잡티 지우기)", () => App.SetTool("heal"), enabled: HasDoc, order: 882);

        Registry.Menu("AI/사진에서 인형 자세 (3D 소재 창)…", () =>
        {
            Scene3DWindow.OpenFromMenu();
            App.Toast("3D 소재 창에서 인형을 고르고 \"사진에서 자세 (AI)\" 를 누르세요");
        }, enabled: HasDoc, order: 60);
        Registry.Menu("AI/-img", null, order: 100);
        Registry.Menu("AI/선 정리 (잡티·끊김·울퉁불퉁)…", () => { if (Registry.Commands.TryGetValue("필터/선 정리 (잡티·끊김·울퉁불퉁)…", out var run)) run(); }, enabled: HasDoc, order: 101);
        Registry.Menu("AI/이미지 AI 로 그리기 (이 PC 의 SD WebUI)…", ImageAi.Img2Img, enabled: HasDoc, order: 102);
        Registry.Menu("AI/이미지 AI 연결 확인", () => ImageAi.Check(), order: 103);
        Registry.Menu("AI/사진·배경에서 퍼스 자 만들기 (소실점 자동)", () => { if (Registry.Commands.TryGetValue("만화/사진·배경에서 퍼스 자 만들기 (소실점 자동)", out var run)) run(); }, enabled: HasDoc, order: 65);
        Registry.Menu("AI/AI 기능 고르기·받기…", AiLicenseExtras.SetupDialog, order: 94);

        AiLicenseExtras.Hook();

        // 다른 모듈의 빈자리 채우기
        Scene3DWindow.PhotoPoseProvider = PosePhoto.FromPhoto;
        Vtuber.FaceSourceFactory = CamFaceSource.Create;

        string st = Environment.GetEnvironmentVariable("COMICPAD_VISION_SELFTEST");
        if (!string.IsNullOrEmpty(st)) Environment.Exit(VisionSelfTest.Run(st));
    }

    // ---------------------------------------------------------------- 내용 인식 채우기
    public static void ContentAwareFillDialog()
    {
        var d = App.Doc;
        if (d == null) return;
        if (!d.Sel.Active) { App.Toast("채울 곳을 먼저 선택하세요 (지울 물체를 대충 둘러싸면 됩니다)"); return; }
        var layer = d.Active;
        if (!App.CheckEditable(layer)) return;
        var v = Filters.Ui.Ask("내용 인식 채우기", new[]
        {
            Filters.Field.Pick("src", "보고 채울 것", new[] { ("layer", "지금 레이어"), ("all", "보이는 그림 전체") }, App.Get("cafSrc", "layer")),
            Filters.Field.Pick("margin", "주변을 얼마나 넓게 볼지", new[] { ("0.6", "가까이"), ("1", "보통"), ("2", "넓게") }, "1"),
            Filters.Field.Pick("out", "결과", new[] { ("layer", "이 레이어에"), ("new", "새 레이어로") }, App.Get("cafOut", "layer")),
            Filters.Field.Info("선택한 곳을 주변 무늬로 자연스럽게 메웁니다 (배경의 물체·글자·잡티 지우기). 큰 영역은 몇십 초 걸릴 수 있습니다."),
        }, "채우기");
        if (v == null) return;
        App.Set("cafSrc", v.S("src")); App.Set("cafOut", v.S("out"));
        var rect = Inpaint.FillRegion(d, double.Parse(v.S("margin"), System.Globalization.CultureInfo.InvariantCulture));
        if (rect.IsEmpty) return;
        uint[] full = v.S("src") == "all" ? App.Comp.Render(d, rect, new RenderOptions { Paper = false, ExcludeDraft = true }) : layer.Target.Read(rect);
        uint[] filled = null; Exception err = null;
        VisionUi.RunProgress(App.Main, "내용 인식 채우기", (prog, ct) => Task.Run(() => { filled = Inpaint.ContentAwareFill(d, rect, full, 1200, prog, ct); }, ct), e => err = e);
        if (err is OperationCanceledException) return;
        if (err != null) { App.Toast("내용 인식 채우기: " + err.Message); return; }
        if (filled == null) return;
        Commit(d, layer, filled, rect, v.S("out") == "new");
        App.Toast("채웠습니다 (마음에 안 들면 실행취소 후 범위를 바꿔 다시)");
    }

    /// <summary>결과를 이 레이어(선택 모양만 바꿈) 또는 새 레이어로. 실행취소 한 번</summary>
    public static void Commit(Document d, Layer layer, uint[] filled, Rect rect, bool newLayer)
    {
        if (newLayer)
        {
            App.History.Struct(d, "내용 인식 채우기", () =>
            {
                var l = new Layer(d, "내용 인식 채우기") { Parent = layer?.Parent };
                d.Layers.Insert(layer == null ? d.Layers.Count : d.Layers.IndexOf(layer) + 1, l);
                d.ActiveId = l.Id;
                var tmp = new Surface(d.W, d.H);
                var e = new PixelEdit(App.History, l, tmp, "내용 인식 채우기");
                Inpaint.Apply(d, tmp, e, filled, rect, false);
                l.Px = tmp;
            });
            App.NotifyLayers();
        }
        else
        {
            var e = App.History.BeginPixels(layer, "내용 인식 채우기");
            Inpaint.Apply(d, layer.Target, e, filled, rect, true);
            e.Commit();
            layer.Touch();
        }
        d.Modified = true;
        App.Invalidate(rect);
    }
}
