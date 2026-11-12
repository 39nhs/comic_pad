// 대본 배치 시험 (창 없이): ComicPadNative.exe --selftest script 결과폴더
//  - 대본 줄 읽기 (구름·외침·사각·이름 빼기), 줄바꿈
//  - 한 줄 놓기: 글자 레이어 + 말풍선(textId 연결), 탭한 곳이 가운데, 실행취소 한 번
//  - 자동 배치: 컷 읽는 순서 (왼→오 / 오→왼), 컷마다 나눠 놓기, 실행취소 한 번
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Comic;

public static class ScriptTest
{
    public static int Run(string outDir)
    {
        var log = new List<string>(); int fails = 0;
        void Check(bool ok, string what) { log.Add((ok ? "OK   " : "FAIL ") + what); if (!ok) fails++; }

        // 1) 줄 읽기
        Check(Script.ParseLine("철수: 안녕") is { Text: "안녕", Shape: "ellipse" }, "이름: 대사 → 대사만");
        Check(Script.ParseLine("(생각) 배고파") is { Text: "배고파", Shape: "cloud" }, "(생각) → 구름");
        Check(Script.ParseLine("! 멈춰") is { Text: "멈춰", Shape: "shout" }, "! → 외침");
        Check(Script.ParseLine("(외침) 야") is { Shape: "shout" }, "(외침) → 외침");
        Check(Script.ParseLine("# 그날 밤") is { Text: "그날 밤", Shape: "rect" }, "# → 사각 상자");
        Check(Script.ParseLine("[나레이션] 다음 날") is { Text: "다음 날", Shape: "rect" }, "[나레이션] → 사각 상자");
        Check(Script.ParseLine("   ") == null && Script.ParseLine("철수:") == null, "빈 줄·이름만 → 건너뜀");
        Check(Script.Parse("a\n\nb\r\nc").Count == 3, "여러 줄 → 3개");
        string w = Script.Wrap("가나다 라마바 사아자 차카타 파하", "Malgun Gothic", 40, true, 40 * 4);
        Check(w.Contains('\n') && w.Split('\n').All(l => l.Length <= 8), $"줄바꿈 ({w.Replace("\n", " | ")})");
        Check(Script.Wrap("한줄\\n두줄", "Malgun Gothic", 40, true, 1000) == "한줄\n두줄", "\\n → 줄바꿈");

        // 2) 한 줄 놓기
        var d = Document.New(1200, 1600, "대본 시험");
        App.SetDoc(d);
        int n0 = d.Layers.Count;
        var o = new Script.Opts();
        var tl = Script.PlaceLine(600, 400, new ScriptItem("안녕하세요 반갑습니다", "ellipse"), o, seed: 1);
        var bl = d.Layers.FirstOrDefault(l => l.Kind == "bubble");
        Check(tl != null && tl.Kind == "text" && bl != null && d.Layers.Count == n0 + 2, "글자 + 말풍선 레이어 생김");
        Check(bl != null && tl != null && d.IndexOf(tl) > d.IndexOf(bl), "글자가 말풍선 위");
        var bub = (bl?.Meta?["bubbles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        Check(bub != null && J.Str(bub, "textId") == tl?.Id, "말풍선 ↔ 글자 연결 (textId)");
        if (tl?.Meta?["box"] is JsonObject box)
        {
            double cx = J.Num(box, "x") + J.Num(box, "w") / 2, cy = J.Num(box, "y") + J.Num(box, "h") / 2;
            Check(Math.Abs(cx - 600) < 6 && Math.Abs(cy - 400) < 6, $"탭한 곳이 글자 가운데 ({cx:0},{cy:0})");
            double bx = J.Num(bub, "x"), by = J.Num(bub, "y"), bw = J.Num(bub, "w"), bh = J.Num(bub, "h");
            Check(bx < J.Num(box, "x") && by < J.Num(box, "y") && bx + bw > J.Num(box, "x") + J.Num(box, "w") && by + bh > J.Num(box, "y") + J.Num(box, "h"), "말풍선이 글자를 감쌈");
            Check(bub["tail"] is JsonObject, "꼬리 있음");
        }
        else Check(false, "글자 상자(meta.box) 있음");
        Check(tl?.Px?.Bounds().IsEmpty == false && bl?.Px?.Bounds().IsEmpty == false, "글자·말풍선이 그려짐");
        App.History.Undo(d);
        Check(d.Layers.Count == n0, "실행취소 한 번 → 처음대로");
        App.History.Redo(d);
        Check(d.Layers.Count == n0 + 2, "다시 실행");

        // 3) 자동 배치 (컷 3개: 위 줄 왼·오, 아래 줄 하나)
        d = Document.New(1200, 1600, "자동 배치");
        App.SetDoc(d);
        var pm = Panels.NewMeta(d);
        pm["panels"] = Panels.PanelsJson(new[] { Panels.RectPts(50, 50, 500, 600), Panels.RectPts(650, 60, 500, 600), Panels.RectPts(50, 800, 1100, 700) });
        var pl = new Layer(d, "컷", "panel") { Meta = pm }; d.Layers.Add(pl);
        var ord = Script.PanelsInOrder(d, false);
        Check(ord.Count == 3 && ord[0].x < ord[1].x && ord[2].y > 700, "컷 순서 왼→오, 위→아래");
        var ordR = Script.PanelsInOrder(d, true);
        Check(ordR[0].x > ordR[1].x && ordR[2].y > 700, "컷 순서 오→왼");
        n0 = d.Layers.Count;
        var items = Script.Parse("철수: 하나\n영희: 둘\n(생각) 셋\n! 넷\n# 다섯\n여섯");
        var (cnt, areas, usedPanels) = Script.AutoPlace(items, o, seed: 2);
        var texts = d.Layers.Where(l => l.Kind == "text").ToList();
        Check(cnt == 6 && areas == 3 && usedPanels && texts.Count == 6, $"6줄을 컷 3개에 ({texts.Count})");
        var bubs = d.Layers.Where(l => l.Kind == "bubble").SelectMany(l => (l.Meta["bubbles"] as JsonArray).OfType<JsonObject>()).ToList();
        Check(bubs.Count == 6 && bubs.Select(b => J.Str(b, "shape")).SequenceEqual(new[] { "ellipse", "ellipse", "cloud", "shout", "rect", "ellipse" }), "말풍선 모양이 대본대로");
        Check(bubs.Count(b => b["tail"] is JsonObject) == 5, "사각 상자만 꼬리 없음");
        bool InPanel(Layer t, (double x, double y, double w, double h) a)
        { var b = (JsonObject)t.Meta["box"]; double cx = J.Num(b, "x") + J.Num(b, "w") / 2, cy = J.Num(b, "y") + J.Num(b, "h") / 2; return cx > a.x && cx < a.x + a.w && cy > a.y && cy < a.y + a.h; }
        Check(texts.Count == 6 && InPanel(texts[0], ord[0]) && InPanel(texts[1], ord[0]) && InPanel(texts[2], ord[1]) && InPanel(texts[3], ord[1]) && InPanel(texts[4], ord[2]) && InPanel(texts[5], ord[2]), "대사 2줄씩 컷 순서대로");
        Check(texts.Count == 6 && J.Num((JsonObject)texts[0].Meta["box"], "y") < J.Num((JsonObject)texts[1].Meta["box"], "y"), "컷 안에서 첫 대사가 위");
        Directory.CreateDirectory(outDir);
        try { var flat = App.Comp.Flatten(d); using var bmp = CpadIO.ToBitmap(flat, flat.Full); bmp.Save(Path.Combine(outDir, "script_auto.png")); } catch (Exception ex) { Check(false, "그림 저장: " + ex.Message); }
        App.History.Undo(d);
        Check(d.Layers.Count == n0, "자동 배치도 실행취소 한 번");

        // 4) .cpad 왕복 (웹과 같은 meta 로 저장되는지)
        App.History.Redo(d);
        var p = Path.Combine(outDir, "script.cpad");
        CpadIO.Save(d, p);
        var d2 = CpadIO.Load(p);
        var bubs2 = d2.Layers.Where(l => l.Kind == "bubble").SelectMany(l => (l.Meta["bubbles"] as JsonArray).OfType<JsonObject>()).ToList();
        Check(bubs2.Count == 6 && bubs2.All(b => d2.ById(J.Str(b, "textId"))?.Kind == "text"), ".cpad 저장 후에도 연결 그대로");

        log.Add(fails == 0 ? "대본 배치 시험 통과" : $"대본 배치 시험 실패 {fails}개");
        File.WriteAllLines(Path.Combine(outDir, "report.txt"), log);
        return fails;
    }
}
