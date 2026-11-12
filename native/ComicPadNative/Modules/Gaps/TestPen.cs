// 시험: 펜 위치 예측 (등속·멈춤·꺾임), 설정 penFeel.predict·penButton2
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.Gaps;

public static partial class GapsTest
{
    static void TestPenPredict()
    {
        long f = Stopwatch.Frequency;
        var p = new PenPredict();
        // 초당 1000px 로 오른쪽, 5ms 마다 (200Hz 펜)
        for (int i = 0; i < 8; i++) p.Add(100 + i * 5, 50, f + i * f / 200);
        var pts = p.Points;
        Ok(pts != null && Math.Abs(pts[^1].x - (135 + 16)) < 2 && Math.Abs(pts[^1].y - 50) < 0.5, $"등속 1000px/s: 16ms 앞 예측 ({pts?[^1].x:0.0}, {pts?[^1].y:0.0}), 기대 (151, 50)");
        var b = p.Bounds(10);
        Ok(b.Left <= 130 && b.Right >= 151, $"다시 그릴 범위 {b}");
        p.Reset();
        for (int i = 0; i < 8; i++) p.Add(100, 50, f + i * f / 200);
        Ok(p.Points == null, "멈춘 펜 → 예측 없음");
        p.Reset();
        // 아래로 가속 (y = 0.5·a·t², a = 20000px/s²)
        for (int i = 0; i < 8; i++) { double t = i / 200.0; p.Add(100 + 1000 * t, 50 + 0.5 * 20000 * t * t, f + i * f / 200); }
        var acc = p.Points;
        Ok(acc != null && acc[^1].y > 50 + 0.5 * 20000 * Math.Pow(7 / 200.0, 2) + 3, $"가속하면 그 방향으로 더 (y {acc?[^1].y:0.0})");
        // 아주 빠르게 움직여도 80px 넘게 내다보지 않음
        p.Reset();
        for (int i = 0; i < 8; i++) p.Add(i * 100, 0, f + i * f / 200);
        Ok(p.Points != null && p.Points[^1].x - 700 <= 80.01, $"최대 거리 제한 ({p.Points?[^1].x - 700:0}px)");
        // 설정
        App.Settings["penFeel"] = new JsonObject { ["predict"] = false };
        bool off = PenPredict.Enabled();
        App.Settings["penFeel"] = new JsonObject { ["filter"] = "euro" };
        bool def = PenPredict.Enabled();
        App.Settings.Remove("penButton2");
        string b2 = PenPredict.Button2Action();
        App.Settings["penButton2"] = "eyedropper";
        Ok(!off && def && b2 == "pan" && PenPredict.Button2Action() == "eyedropper" && PenPredict.ForTool(Registry.Tools.Find(t => t.Id == "brush")) && !PenPredict.ForTool(Registry.Tools.Find(t => t.Id == "eraser")),
            "설정: 예측 끄기/기본 켬, 두 번째 측면 버튼 기본 화면 이동·바꾸기, 브러시만 (지우개는 아님)");
        App.Settings.Remove("penButton2"); App.Settings.Remove("penFeel");
    }
}
