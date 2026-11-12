// 밑색 한 번에 칠하기 · 색 힌트 레이어 · 색 점으로 칠하기 (웹 ui/flats.js). 계산은 백그라운드에서.
// 레이어 meta 는 웹과 같음: 힌트 레이어 {"hint": true}, 결과 레이어 {"hintResult": true}
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.SelectFill;

public static class Flats
{
    static Document D => App.Doc;
    static bool Meta(Layer l, string k) => l.Meta?[k] is JsonNode n && n.GetValueKind() == System.Text.Json.JsonValueKind.True;
    public static Layer HintLayerOf(Document d) => d.Layers.Find(l => Meta(l, "hint"));

    static bool busy;
    static async Task<T> Busy<T>(string label, Func<T> work)
    {
        busy = true;
        App.Toast(label + "…");
        var cv = (App.Main as UI.MainForm)?.Canvas;
        if (cv != null) cv.Cursor = Cursors.WaitCursor;
        try { return await Task.Run(work); }
        finally { busy = false; if (cv != null) cv.Cursor = App.Tool?.Cursor ?? Cursors.Cross; }
    }

    public static async void AutoFlatDialog()
    {
        var doc = D;
        if (doc == null || busy) return;
        bool hasRef = doc.Layers.Any(l => l.Ref);
        var v = Ui.Form("밑색 한 번에 칠하기", new[]
        {
            Ui.Seg("source", "선화로 볼 것", new[] { ("ref", "참조 레이어"), ("layer", "지금 레이어"), ("all", "보이는 레이어 전체") }, hasRef ? "ref" : "layer"),
            Ui.Seg("mode", "색", new[] { ("colors", "영역마다 다른 색"), ("one", "한 가지 색") }, "colors"),
            Ui.ColorF("color", "한 가지 색일 때", "#d9d9d9"),
            Ui.Num("gap", "틈 막기: 이 너비(px)까지 빈틈은 선이 이어진 것으로 (0 = 끔, 러프는 10~20)", 4, 0, 50),
            Ui.Num("under", "선 아래로 번지게 (px, 선 굵기만큼)", Math.Max(2, Raster.JsRound(doc.W / 800.0)), 0, 30),
            Ui.Num("minArea", "이보다 작은 조각은 옆 색으로 (픽셀 수)", 40, 0, 1e9),
            Ui.Check("outside", "바깥(테두리에 닿는 곳)은 칠하지 않기", true),
            Ui.Info("선화 레이어 아래에 \"밑색\" 레이어가 새로 생깁니다. 이후 채우기 도구(지금 레이어, 허용 0)로 영역 색을 바꾸거나, 자동선택으로 한 영역씩 고를 수 있습니다."),
        }, "칠하기");
        if (v == null) return;
        var line = doc.Active;
        var img = SelUtil.ReferenceImage((string)v["source"]);
        var opt = new AutoFlat.Opt { Gap = (double)v["gap"], Under = (double)v["under"], MinArea = (double)v["minArea"], Outside = (bool)v["outside"] };
        string mode = (string)v["mode"], color = (string)v["color"];
        int W = doc.W, H = doc.H;
        AutoFlat.Labels res; byte[] rgba;
        try
        {
            (res, rgba) = await Busy("밑색 영역 나누는 중", () => { var r = AutoFlat.FlatLabels(img, W, H, opt); return (r, r.Count > 0 ? AutoFlat.Colorize(r.L, r.Count, W, H, mode, color) : null); });
        }
        catch (Exception ex) { App.Toast("밑색 칠하기: " + ex.Message); return; }
        if (App.Doc != doc) return;
        if (res.Count == 0) { App.Toast("닫힌 영역을 찾지 못했습니다. 틈 닫기를 늘리거나 \"바깥 제외\"를 꺼 보세요"); return; }
        int at = line != null && doc.Layers.Contains(line) ? doc.IndexOf(line) : 0;
        string parent = line != null && doc.Layers.Contains(line) ? doc.ParentOf(line) : null;
        SelUtil.AddLayer("밑색 칠하기", "밑색", l => SelUtil.RgbaToSurface(rgba, l.Px), at, parent, true);
        SelUtil.Done(new Rect(0, 0, W, H));
        App.Toast($"영역 {res.Count}개를 칠했습니다");
    }

    /// <summary>색 힌트 레이어 만들기 (있으면 그 레이어로)</summary>
    public static void MakeHintLayer()
    {
        var doc = D;
        if (doc == null) return;
        var h = HintLayerOf(doc);
        if (h == null)
        {
            var line = doc.Active;
            h = SelUtil.AddLayer("색 힌트 레이어", "색 힌트 (색 점 찍기)", null, line != null ? doc.IndexOf(line) + 1 : doc.Layers.Count, null, true,
                l => { l.Meta = new JsonObject { ["hint"] = true }; l.Opacity = 0.75f; l.Draft = true; });
        }
        doc.ActiveId = h.Id;
        App.SetTool("brush");
        App.Toast("\"색 힌트\" 레이어에 칠할 색으로 영역마다 점이나 짧은 선을 찍으세요. 다 찍으면 만화 > 색 점으로 칠하기");
        App.NotifyLayers();
        App.InvalidateAll();
    }

    public static async void HintFillDialog()
    {
        var doc = D;
        if (doc == null || busy) return;
        var hint = HintLayerOf(doc);
        if (hint == null) { MakeHintLayer(); return; }
        bool hasRef = doc.Layers.Any(l => l.Ref);
        var lineGuess = doc.Layers.Find(l => !Meta(l, "hint") && !Meta(l, "hintResult") && !l.IsFolder && !l.IsAdjust && l.Visible);
        var v = Ui.Form("색 점으로 칠하기", new[]
        {
            Ui.Seg("source", "선화로 볼 것", new[] { ("ref", "참조 레이어"), ("all", "보이는 레이어 전체 (힌트·결과 제외)") }, hasRef ? "ref" : "all"),
            Ui.Num("gap", "틈 막기: 이 너비(px)까지 빈틈은 선이 이어진 것으로 (0 = 끔, 러프는 10~20)", App.Get("hintGap", 4.0), 0, 50),
            Ui.Num("under", "선 아래로 번지게 (px)", Math.Max(2, Raster.JsRound(doc.W / 800.0)), 0, 30),
            Ui.Check("spread", "점이 없는 영역은 가장 많이 맞닿은 이웃 색으로", true),
            Ui.Check("hideHint", "칠한 뒤 힌트 레이어 숨기기", true),
            Ui.Info("힌트 레이어의 색 점을 보고, 선화로 막힌 영역마다 가장 많이 찍힌 색으로 칠합니다. 결과는 \"밑색 (색 점)\" 레이어(다시 하면 바뀜)이고, 점을 고쳐 다시 실행하면 됩니다."),
        }, "칠하기");
        if (v == null) return;
        // 선화: 힌트·이전 결과 레이어는 빼고 합치기
        byte[] img;
        if ((string)v["source"] == "ref") img = SelUtil.ReferenceImage("ref");
        else
        {
            var hidden = doc.Layers.Where(l => (Meta(l, "hint") || Meta(l, "hintResult")) && l.Visible).ToList();
            hidden.ForEach(l => l.Visible = false);
            img = new byte[doc.W * doc.H * 4];
            try { SelUtil.RenderRgba(doc, new RenderOptions { Paper = false, ExcludeDraft = true }, img); }
            finally { hidden.ForEach(l => l.Visible = true); }
        }
        App.Set("hintGap", (double)v["gap"]);
        var opt = new AutoFlat.Opt { Gap = (double)v["gap"], Under = (double)v["under"], MinArea = 20, Outside = false };
        var hd = new byte[doc.W * doc.H * 4];
        if (hint.Px != null) SelUtil.SurfaceToRgba(hint.Px, hd);
        bool spread = (bool)v["spread"];
        int W = doc.W, H = doc.H;
        AutoFlat.Labels res; AutoFlat.HintResult o;
        try
        {
            (res, o) = await Busy("영역 나누는 중", () => { var r = AutoFlat.FlatLabels(img, W, H, opt); return (r, r.Count > 0 ? AutoFlat.ColorizeHints(r.L, r.Count, r.Edge, hd, W, H, spread) : null); });
        }
        catch (Exception ex) { App.Toast("색 점으로 칠하기: " + ex.Message); return; }
        if (App.Doc != doc) return;
        if (res.Count == 0) { App.Toast("선화로 막힌 영역을 찾지 못했습니다"); return; }
        if (o.Hinted == 0) { App.Toast("힌트 레이어에 찍힌 색이 없습니다"); return; }
        var full = new Rect(0, 0, W, H);
        var r0 = doc.Layers.Find(l => Meta(l, "hintResult"));
        if (r0 == null || r0.Px == null)
        {
            // 새 결과 레이어 + 힌트 숨기기를 한 번에 되돌리도록 구조 편집 하나로
            int below = lineGuess != null && !Meta(lineGuess, "hint") ? doc.IndexOf(lineGuess) : 0;
            App.History.Struct(doc, "색 점으로 칠하기", () =>
            {
                var l = new Layer(doc, "밑색 (색 점)") { Meta = new JsonObject { ["hintResult"] = true } };
                SelUtil.RgbaToSurface(o.Rgba, l.Px);
                doc.Layers.Insert(Math.Clamp(Math.Max(0, below), 0, doc.Layers.Count), l);
                doc.ActiveId = l.Id;
                if ((bool)v["hideHint"]) hint.Visible = false;
            });
        }
        else
        {
            var pe = new PixelEdit(App.History, r0, r0.Px, "색 점으로 칠하기");
            pe.Touch(full);
            SelUtil.RgbaToSurface(o.Rgba, r0.Px);
            var px = pe.Finish("색 점으로 칠하기");
            var before = StructSnap.Take(doc);
            if ((bool)v["hideHint"]) hint.Visible = false;
            App.History.PushGroup("색 점으로 칠하기", px, new StructEdit("색 점으로 칠하기", before, StructSnap.Take(doc)));
        }
        SelUtil.Done(full);
        App.Toast($"색 점 {o.Hinted}곳 → 영역 {o.Filled}개를 칠했습니다");
    }
}
