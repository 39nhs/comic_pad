// 만화 모듈 등록: 변형·도형·자·컷·말풍선·텍스트·효과음·유선·메모 도구, 편집/만화/보기 메뉴, 늘 그리는 겹침 그림(자·대칭·원고 가이드·메모).
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Comic;

public sealed class ComicModule : IModule
{
    public void Init()
    {
        Fonts.LoadDataFonts();
        var transform = new TransformTool();
        Registry.Tool(transform);
        Registry.Tool(new ShapeTool());
        Registry.Tool(new RulerTool());
        Registry.Tool(new PanelTool());
        Registry.Tool(new BubbleTool());
        Registry.Tool(new TextTool());
        Registry.Tool(new SfxTool());
        Registry.Tool(new SpeedLinesTool());
        Registry.Tool(new MemoTool());

        Registry.Overlay(Manuscript.DrawGuides);
        Registry.Overlay(Rulers.Draw);
        Registry.Overlay(MemoTool.Draw);

        bool HasDoc() => App.Doc != null;
        bool HasSel() => App.Doc?.Sel.Active == true;
        void Run(Action a) { if (App.Doc != null) a(); }

        // ---- 편집 ----
        Registry.Menu("편집/-변형", null, order: 50);
        Registry.Menu("편집/이동·변형", () => { if (App.Tool == transform) transform.Start(); else App.SetTool("transform"); }, Keys.Control | Keys.T, HasDoc, order: 51);
        Registry.Menu("편집/레이어 좌우 반전", () => Run(() => CanvasOps.FlipLayer('h')), Keys.None, HasDoc, order: 52);
        Registry.Menu("편집/레이어 상하 반전", () => Run(() => CanvasOps.FlipLayer('v')), Keys.None, HasDoc, order: 53);
        Registry.Menu("편집/레이어 회전/오른쪽으로 90°", () => Run(() => CanvasOps.RotateLayer(90)), Keys.None, HasDoc, order: 54);
        Registry.Menu("편집/레이어 회전/왼쪽으로 90°", () => Run(() => CanvasOps.RotateLayer(-90)), Keys.None, HasDoc, order: 55);
        Registry.Menu("편집/레이어 회전/180°", () => Run(() => CanvasOps.RotateLayer(180)), Keys.None, HasDoc, order: 56);
        Registry.Menu("편집/-캔버스", null, order: 60);
        Registry.Menu("편집/캔버스 크기…", () => Run(CanvasOps.CanvasSizeDialog), Keys.Control | Keys.Alt | Keys.C, HasDoc, order: 61);
        Registry.Menu("편집/이미지 크기…", () => Run(CanvasOps.ImageSize), Keys.Control | Keys.Alt | Keys.I, HasDoc, order: 62);
        Registry.Menu("편집/선택 영역으로 자르기", () => Run(CanvasOps.CropToSelection), Keys.None, HasSel, order: 63);
        Registry.Menu("편집/캔버스 회전·반전/오른쪽으로 90°", () => Run(() => CanvasOps.RotateCanvas(90)), Keys.None, HasDoc, order: 64);
        Registry.Menu("편집/캔버스 회전·반전/왼쪽으로 90°", () => Run(() => CanvasOps.RotateCanvas(-90)), Keys.None, HasDoc, order: 65);
        Registry.Menu("편집/캔버스 회전·반전/180°", () => Run(() => CanvasOps.RotateCanvas(180)), Keys.None, HasDoc, order: 66);
        Registry.Menu("편집/캔버스 회전·반전/좌우 반전", () => Run(() => CanvasOps.FlipCanvas('h')), Keys.None, HasDoc, order: 67);
        Registry.Menu("편집/캔버스 회전·반전/상하 반전", () => Run(() => CanvasOps.FlipCanvas('v')), Keys.None, HasDoc, order: 68);
        Registry.Menu("편집/웹툰: 아래로 캔버스 늘리기", () => Run(CanvasOps.ExtendDown), Keys.None, HasDoc, order: 69);

        // ---- 레이어 ----
        Registry.Menu("레이어/-만화", null, order: 120);
        Registry.Menu("레이어/말풍선을 그림으로 (직접 그리기 가능, 다시 편집 불가)", () => Run(Bubbles.ToRaster), Keys.None, () => App.Active?.Kind == "bubble" && App.Active.Meta != null, order: 121);
        Registry.Menu("레이어/효과음을 그림으로 바꾸기", () => Run(() => Sfx.ToPixels(App.Active)), Keys.None, () => App.Active?.Kind == "text" && J.Bool(App.Active.Meta, "sfx"), order: 122);

        // ---- 만화 ----
        Registry.Menu("만화/페이지 컷 만들기…", () => Run(Panels.PageFrameDialog), Keys.None, HasDoc, order: 10);
        Registry.Menu("만화/컷 나누기 템플릿…", () => Run(Panels.TemplatesDialog), Keys.None, HasDoc, order: 11);
        Registry.Menu("만화/컷 나누기 도구", () => App.SetTool("panel"), order: 12);
        Registry.Menu("만화/말풍선 도구", () => App.SetTool("bubble"), order: 13);
        Registry.Menu("만화/텍스트 도구", () => App.SetTool("text"), order: 14);
        Registry.Menu("만화/효과음 글자 도구", () => App.SetTool("sfx"), order: 15);
        Registry.Menu("만화/-1", null, order: 20);
        Registry.Menu("만화/웹툰: 아래로 캔버스 늘리기", () => Run(CanvasOps.ExtendDown), Keys.None, HasDoc, order: 21);
        Registry.Menu("만화/-2", null, order: 30);
        Registry.Menu("만화/원고 설정 (재단선·도련·안전선)…", () => Run(Manuscript.SetupDialog), Keys.None, HasDoc, order: 31);
        Registry.Menu("만화/원고 가이드 보이기/숨기기", Manuscript.ToggleGuides, Keys.None, null, () => App.Get("printGuides", true), order: 32);
        Registry.Menu("만화/메모 핀 도구", () => App.SetTool("memo"), order: 33);
        Registry.Menu("만화/인쇄·투고 전 점검…", () => Run(Preflight.Dialog), Keys.None, HasDoc, order: 34);
        Registry.Menu("만화/웹툰 미리보기 (휴대폰 폭)", () => Run(Manuscript.WebtoonPreview), Keys.None, HasDoc, order: 35);
        Registry.Menu("만화/-3", null, order: 40);
        Registry.Menu("만화/유선 도구", () => App.SetTool("speedlines"), order: 41);
        Registry.Menu("만화/플래시 효과 (우니/베타)…", () => Run(Flash.Dialog), Keys.None, HasDoc, order: 42);
        Registry.Menu("만화/방사선 자 (집중선용)", () => Run(() => Rulers.Add("radial")), Keys.None, HasDoc, order: 43);
        Registry.Menu("파일/인쇄·투고 전 점검…", () => Run(Preflight.Dialog), Keys.None, HasDoc, order: 60);

        // ---- 보기: 자·대칭 ----
        Registry.Menu("보기/-자", null, order: 85);
        Registry.Menu("보기/자/1점 투시 자", () => Run(() => Rulers.Add("p1")), Keys.None, HasDoc, order: 1);
        Registry.Menu("보기/자/2점 투시 자", () => Run(() => Rulers.Add("p2")), Keys.None, HasDoc, order: 2);
        Registry.Menu("보기/자/3점 투시 자", () => Run(() => Rulers.Add("p3")), Keys.None, HasDoc, order: 3);
        Registry.Menu("보기/자/평행선 자", () => Run(() => Rulers.Add("parallel")), Keys.None, HasDoc, order: 4);
        Registry.Menu("보기/자/방사선 자 (집중선용)", () => Run(() => Rulers.Add("radial")), Keys.None, HasDoc, order: 5);
        Registry.Menu("보기/자/동심원 자", () => Run(() => Rulers.Add("concentric")), Keys.None, HasDoc, order: 6);
        Registry.Menu("보기/자/자에 맞추기", () => { App.Set("rulerSnap", !App.Get("rulerSnap", true)); App.NotifySettings(); }, Keys.None, null, () => App.Get("rulerSnap", true), order: 7);
        Registry.Menu("보기/자/자 보이기", () => { App.Set("showRulers", !App.Get("showRulers", true)); MetaLayers.Redraw(); }, Keys.None, null, () => App.Get("showRulers", true), order: 8);
        Registry.Menu("보기/대칭 그리기 (좌우)", Symmetry.Cycle, Keys.None, null, () => Symmetry.On, order: 86);
        Registry.Menu("보기/방사 대칭 (만다라)…", Symmetry.RadialDialog, Keys.None, order: 87);
        Registry.Menu("보기/대칭 끄기", () => { App.Set("symmetry", "none"); App.NotifySettings(); MetaLayers.Redraw(); }, Keys.None, () => Symmetry.On, order: 88);
    }
}
