// 떠 있는 퀵바 (웹 ui/quickbar.js) + 아래 큰 버튼 막대 설정 (웹 ui/bottombar.js).
//  - settings.quickbar = { visible, x, y, items }, settings.bottombar = { visible, items } (웹 판과 같은 키·모양)
//  - 퀵바: 그림판 위에 떠 있음, 손잡이를 끌어 옮기고 탭하면 버튼 고르기. 크기·불투명도는 세로 막대.
//    웹 판처럼 처음에는 숨김 (아래 막대가 생긴 뒤 기본으로 끔 — 보기 메뉴에서 켬)
//  - 아래 막대 그리기는 UI/Chrome/BottomBar (메인 창 부품), 여기는 단추 목록·고르기 창
using System.Text.Json.Nodes;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class Bars
{
    public static readonly string[] DefaultQuick = { "undo", "redo", "penEraser", "eyedropper", "size", "opacity", "fit", "flip", "deselect" };
    public static readonly string[] DefaultBottom = { "undo", "redo", "brushTool", "penEraser", "eyedropper", "hand", "size", "opacity", "deselect", "transform", "newLayer", "save" };

    static JsonObject QConf() { var q = Shell2Ui.Obj(App.Settings, "quickbar"); if (q["x"] == null) { q["x"] = 12; q["y"] = 80; } if (q["visible"] == null) q["visible"] = false; return q; }   // 웹 판 기본값 (bottomMigrated 뒤: 숨김)
    static JsonObject BConf() { var c = Shell2Ui.Obj(App.Settings, "bottombar"); if (c["visible"] == null) c["visible"] = true; return c; }
    static string[] Items(JsonObject c, string[] def) => c["items"] is JsonArray a ? a.Select(x => (string)x).Where(x => x != null).ToArray() : def;
    static bool Large => App.Get("largeUI", false);

    static UI.Chrome.QuickBar quick; static bool placeHooked;

    public static void Attach()
    {
        App.ToolChanged += Sync; App.ColorChanged += Sync; App.History.Changed += Sync; App.SettingsChanged += Sync;
        RenderQuick(); RenderBottom();
    }

    // ---- 퀵바 (그리기: UI/Chrome/QuickBar) ---------------------------------------------------------
    public static void RenderQuick()
    {
        var canvas = Shell2Ui.Canvas; if (canvas == null) return;
        var q = QConf();
        if (quick != null) { quick.Dispose(); quick = null; }
        if (q["visible"]?.GetValue<bool>() != true) return;   // 웹: 패널 숨기기(zen) 에도 퀵바는 그대로
        int ox = 0, oy = 0;
        quick = new UI.Chrome.QuickBar(
            (start, now) =>
            {
                if (ox == int.MinValue) { ox = U.Int(q["x"]); oy = U.Int(q["y"]); }
                q["x"] = ox + now.X - start.X; q["y"] = oy + now.Y - start.Y; Place();
            },
            EditQuick,
            () => { ox = int.MinValue; App.SaveSettingsSoon(); });
        ox = int.MinValue;
        quick.MaxH = Math.Max(120, canvas.Height - 16);
        quick.SetItems(Items(q, DefaultQuick).Select(QuickActions.Get).Where(a => a != null));
        canvas.Controls.Add(quick);
        Place();
        if (!placeHooked) { placeHooked = true; canvas.Resize += (_, _) => { if (quick != null) { quick.Measure(Math.Max(120, canvas.Height - 16)); quick.Relayout(); } Place(); }; }
    }

    static void Place()
    {
        var canvas = Shell2Ui.Canvas; if (quick == null || canvas == null) return;
        var q = QConf();
        int x = Math.Clamp(U.Int(q["x"]), 0, Math.Max(0, canvas.Width - quick.Width)), y = Math.Clamp(U.Int(q["y"]), 0, Math.Max(0, canvas.Height - quick.Height));
        q["x"] = x; q["y"] = y;
        quick.Location = new Point(x, y);
        quick.BringToFront();
    }

    public static void EditQuick() => EditItems("퀵바 버튼 고르기", QConf(), DefaultQuick, "체크한 버튼이 퀵바에 나타납니다. 손잡이를 끌어 위치를 옮길 수 있어요.", false, RenderQuick);

    public static void ToggleQuick() { var q = QConf(); q["visible"] = q["visible"]?.GetValue<bool>() != true; App.SaveSettingsSoon(); RenderQuick(); }
    public static bool QuickVisible => QConf()["visible"]?.GetValue<bool>() == true;

    // ---- 아래 막대 (그리기: UI/Chrome/BottomBar) -----------------------------------------------------
    public static void RenderBottom() { Shell2Ui.Main?.BottomBar.Rebuild(); Shell2Ui.Main?.PerformLayout(); }
    /// <summary>아래 막대 단추 id 목록 (설정 또는 기본)</summary>
    public static string[] BottomItems() => Items(BConf(), DefaultBottom);

    public static void EditBottom()
    {
        var c = BConf();
        EditItems("아래 막대 버튼 고르기", c, DefaultBottom, "체크한 버튼이 아래 막대에 나옵니다 (순서는 체크한 순서대로 뒤에 붙음).", true, RenderBottom);
    }
    public static void ToggleBottom() { var c = BConf(); c["visible"] = c["visible"]?.GetValue<bool>() == false; App.SaveSettingsSoon(); RenderBottom(); }
    public static bool BottomVisible => BConf()["visible"]?.GetValue<bool>() != false;

    /// <summary>버튼 고르기 창 (퀵바·아래 막대 공통). 고른 순서: 원래 있던 것 순서 + 새로 고른 것 뒤에</summary>
    static void EditItems(string title, JsonObject conf, string[] def, string info, bool withVisible, Action render)
    {
        var cur = Items(conf, def);
        var fields = new List<Field>();
        if (withVisible) fields.Add(Field.Check("_visible", title.StartsWith("아래") ? "아래 막대 보이기" : "보이기", conf["visible"]?.GetValue<bool>() != false));
        fields.Add(Field.Info(info));
        fields.AddRange(QuickActions.All.Select(a => Field.Check(a.Id, a.Label, cur.Contains(a.Id))));
        var v = Dlg.Form(title, fields, "적용");
        if (v == null) return;
        if (withVisible) conf["visible"] = (bool?)v["_visible"] == true;
        conf["items"] = new JsonArray(cur.Where(id => (bool?)v[id] == true).Concat(QuickActions.All.Select(a => a.Id).Where(id => (bool?)v[id] == true && !cur.Contains(id))).Select(x => (JsonNode)x).ToArray());
        App.SaveSettingsSoon();
        render();
    }

    /// <summary>켜짐 표시·실행취소 가능 여부·색 칩·막대 값 (다시 그리기)</summary>
    public static void Sync()
    {
        quick?.Invalidate();
        Shell2Ui.Main?.BottomBar.Invalidate();
    }

    /// <summary>큰 버튼 바꾸면 다시 그림</summary>
    public static void RenderAll() { RenderQuick(); RenderBottom(); }
}
