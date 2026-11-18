// 벡터 선 정리 모듈 (클립스튜디오식): 벡터 선 수정 도구 + 레이어 메뉴의 벡터 명령 (고른 선이 있으면 그 선만, 없으면 전부).
// 벡터 선 고르기 도구(VectorSelectTool)는 Shell2 모듈이 등록하고, 코드는 이 폴더에 있음.
// 시험: ComicPadNative --selftest vectortools 결과폴더
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.VectorTools;

public sealed class VectorToolsModule : IModule
{
    public void Init()
    {
        Registry.Tool(new VectorLineTool());
        bool IsVector() => App.Doc?.Active?.Kind == "vector";

        Registry.Menu("레이어/-벡터선", null, order: 16);
        Registry.Menu("레이어/벡터 선 수정 도구 (굵기·부드럽게·단순화·선 끝 연결)", () => App.SetTool("vlinefix"), Keys.None, IsVector, order: 17);
        Registry.Menu("레이어/벡터: 교차점까지 지우기 도구", () => SetMode("cross"), Keys.None, IsVector, order: 18);
        Registry.Menu("레이어/벡터: 선 단순화…", () => Ask("선 단순화", ("허용 오차 (px) — 선 모양이 이만큼까지만 바뀌게 점을 줄입니다", VectorLineTool.SimplifyTol, 0.1, 50), v => Report(VecEdit.Simplify(L(), v[0]), "선")), Keys.None, IsVector, order: 19);
        Registry.Menu("레이어/벡터: 선 부드럽게", () => Report(VecEdit.Smooth(L(), 2), "선"), Keys.None, IsVector, order: 20);
        Registry.Menu("레이어/벡터: 선 끝 연결…", () => Ask("선 끝 연결", ("이 거리(px) 안의 선 끝끼리 잇기", VectorLineTool.ConnectDist, 1, 500), v => { VectorLineTool.ConnectDist = v[0]; int j = VecEdit.Connect(L(), v[0]); App.Toast(j > 0 ? $"선 끝 {j}곳을 이었습니다" : "가까운 선 끝이 없습니다"); }), Keys.None, IsVector, order: 21);
        Registry.Menu("레이어/벡터: 선 끝 다듬기 (입·출)…", () =>
        {
            var v = Opt.AskNumbers("선 끝 다듬기", ("입: 시작을 가늘게 (px, 0 = 그대로)", VectorLineTool.TaperIn, 0, 2000), ("출: 끝을 가늘게 (px, 0 = 그대로)", VectorLineTool.TaperOut, 0, 2000));
            if (v == null) return;
            VectorLineTool.TaperIn = v[0]; VectorLineTool.TaperOut = v[1];
            Report(VecEdit.Taper(L(), v[0], v[1]), "선");
        }, Keys.None, IsVector, order: 22);
        Registry.Menu("레이어/벡터: 고른 선 굵기 조정…", () => Ask("고른 선 굵기 조정", ("배율 (%) — 고른 선(없으면 전부)의 굵기. 100보다 작으면 가늘게", 120, 1, 1000), v => Report(VecEdit.ScaleWidth(L(), v[0] / 100), "선")), Keys.None, IsVector, order: 23);
        Registry.Menu("레이어/벡터: 고른 선 색을 지금 색으로", () => Report(VecEdit.Recolor(L(), Px.ToHex(App.Fg)), "선"), Keys.None, IsVector, order: 24);
    }

    static Layer L() => VecEdit.VLayer();

    static void SetMode(string mode)
    {
        App.Set("vt.mode", mode);
        if (Registry.Tools.Find(t => t.Id == "vlinefix") is VectorLineTool t) t.Mode = mode;
        if (App.Tool?.Id == "vlinefix") { App.SetTool("brush"); }
        App.SetTool("vlinefix");
    }

    static void Ask(string title, (string label, double value, double min, double max) f, Action<double[]> run)
    {
        var v = Opt.AskNumbers(title, f);
        if (v != null) run(v);
    }

    static void Report(int n, string what)
    {
        VecEdit.Targets(L(), out bool sel);
        App.Toast(n > 0 ? $"{(sel ? "고른 " : "")}{what} {n}개를 고쳤습니다" : "바뀐 선이 없습니다");
    }
}
