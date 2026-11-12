// 선택·채우기 모듈: 도구(선택·선택 펜·채우기·그라데이션·올가미 채우기·머리카락 다듬기)와 선택·편집·만화 메뉴를 등록.
// 웹 판 tools/select.js · tools/basic.js · tools/paint2.js · core/raster.js · core/autoflat.js · ui/flats.js · ui/refine.js 를 옮김.
// 시험: 환경 변수 COMICPAD_SELFTEST_SELECTFILL=결과파일 이면 시작할 때 자체 시험만 하고 끝남 (창 없음).
using ComicPadNative.Core;

namespace ComicPadNative.Modules.SelectFill;

public sealed class SelectFillModule : IModule
{
    public void Init()
    {
        string testOut = Environment.GetEnvironmentVariable("COMICPAD_SELFTEST_SELECTFILL");
        if (!string.IsNullOrEmpty(testOut))
        {
            var (ok, report) = SelfTest.Run(Environment.GetEnvironmentVariable("COMICPAD_SELFTEST_NOPERF") == null, true);
            File.WriteAllText(testOut, report);
            Environment.Exit(ok ? 0 : 1);
        }

        string parityOut = Environment.GetEnvironmentVariable("COMICPAD_PARITY_SELECTFILL");
        if (!string.IsNullOrEmpty(parityOut)) { File.WriteAllLines(parityOut, SelfTest.ParityLines()); Environment.Exit(0); }

        Registry.Tool(new SelectTool());
        Registry.Tool(new SelPenTool());
        Registry.Tool(new FillTool());
        Registry.Tool(new GradientTool());
        Registry.Tool(new LassoFillTool());
        Registry.Tool(new RefineBrushTool());

        bool HasDoc() => App.Doc != null;
        bool HasSel() => App.Doc != null && SelUtil.Active(App.Doc.Sel);

        // ---- 선택 ----
        Registry.Menu("선택/선택 반전", SelCommands.Invert, Keys.Control | Keys.Shift | Keys.I, HasDoc, order: 30);
        Registry.Menu("선택/-sf1", null, order: 35);
        Registry.Menu("선택/선택 확장 (+4px)", () => SelUtil.SelHistory("선택 영역", () => SelCommands.Grow(App.Doc.Sel, 4)), Keys.None, HasSel, order: 40);
        Registry.Menu("선택/선택 축소 (-4px)", () => SelUtil.SelHistory("선택 영역", () => SelCommands.Grow(App.Doc.Sel, -4)), Keys.None, HasSel, order: 41);
        Registry.Menu("선택/선택 확장∕축소…", SelCommands.GrowBy, Keys.None, HasSel, order: 42);   // 메뉴 경로 구분자와 겹치지 않게 ∕
        Registry.Menu("선택/-sf2", null, order: 45);
        Registry.Menu("선택/선택 영역을 새 레이어로 복사", SelCommands.ToLayer, Keys.None, HasDoc, order: 50);
        Registry.Menu("선택/레이어의 그림 부분을 선택", SelCommands.SelectAlpha, Keys.None, HasDoc, order: 51);
        Registry.Menu("선택/선택 경계 흐리기…", SelCommands.FeatherDialog, Keys.None, HasSel, order: 52);
        Registry.Menu("선택/색역 선택 (같은 색 전부)…", SelCommands.ColorRange, Keys.None, HasDoc, order: 53);
        Registry.Menu("선택/-sf3", null, order: 60);
        Registry.Menu("선택/선택 다듬기 (매끄럽게·페더·대비·경계 이동)…", Refine.Dialog, Keys.None, HasSel, order: 61);
        Registry.Menu("선택/머리카락 다듬기 브러시", () => App.SetTool("refinebrush"), Keys.None, HasDoc, order: 62);
        Registry.Menu("선택/-sf4", null, order: 70);
        Registry.Menu("선택/선택 도구", () => App.SetTool("select"), Keys.None, HasDoc, order: 71);
        Registry.Menu("선택/선택 펜", () => App.SetTool("selpen"), Keys.None, HasDoc, order: 72);
        Registry.Menu("레이어/레이어의 그림 부분을 선택", SelCommands.SelectAlpha, Keys.None, HasDoc, order: 120);

        // ---- 편집 ----
        Registry.Menu("편집/잘라내기", () => SelCommands.Copy(true), Keys.Control | Keys.X, HasDoc, order: 26);
        Registry.Menu("편집/복사", () => SelCommands.Copy(false), Keys.Control | Keys.C, HasDoc, order: 27);
        Registry.Menu("편집/붙여넣기", SelCommands.Paste, Keys.Control | Keys.V, HasDoc, order: 28);
        Registry.Menu("편집/배경색으로 채우기", () => SelCommands.FillSelection(App.Bg), Keys.None, HasDoc, order: 41);

        // ---- 만화 (밑색) ----
        Registry.Menu("만화/-sf", null, order: 300);
        Registry.Menu("만화/밑색 한 번에 칠하기 (선화 영역마다)…", Flats.AutoFlatDialog, Keys.None, HasDoc, order: 301);
        Registry.Menu("만화/색 힌트 레이어 만들기 (색 점 찍기)", Flats.MakeHintLayer, Keys.None, HasDoc, order: 302);
        Registry.Menu("만화/색 점으로 칠하기 (힌트대로 밑색)…", Flats.HintFillDialog, Keys.None, HasDoc, order: 303);

        // ---- 시험 ----
        Registry.Menu("도움말/선택·채우기 자체 시험", () =>
        {
            var (ok, report) = SelfTest.Run(false, false);   // 작업 중인 문서·실행취소 기록은 건드리지 않음
            MessageBox.Show(App.Main, report, ok ? "선택·채우기 시험 통과" : "선택·채우기 시험 실패");
        }, Keys.None, order: 90);
    }
}
