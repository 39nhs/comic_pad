// 대본으로 대사 넣기 (웹 tools/script.js): 대사를 한꺼번에 써 두고, 탭할 때마다 한 줄씩 말풍선+글자로 놓거나 컷 순서대로 자동 배치.
//   (생각) … → 구름 풍선   ! 로 시작 / (외침) → 외침 풍선   # … / [나레이션] … → 사각 상자   "이름: 대사" → 이름은 빼고 대사만
using System.Drawing.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

public sealed record ScriptItem(string Text, string Shape);

public static class Script
{
    static readonly Regex Think = new(@"^\((생각|속마음)\)\s*"), Shout = new(@"^\((외침|고함)\)\s*"), Bang = new(@"^!\s*"),
        Narr = new(@"^(#|\[나레이션\]|\(나레이션\))\s*"), Name = new(@"^[^\s:：]{1,12}[:：]\s*");

    /// <summary>대본 한 줄 → 대사 (빈 줄이면 null)</summary>
    public static ScriptItem ParseLine(string raw)
    {
        string t = (raw ?? "").Trim(), shape = "ellipse";
        if (t.Length == 0) return null;
        bool Tag(Regex re, string s) { if (!re.IsMatch(t)) return false; t = re.Replace(t, "", 1).Trim(); shape = s; return true; }
        _ = Tag(Think, "cloud") || Tag(Shout, "shout") || Tag(Bang, "shout") || Tag(Narr, "rect");
        t = Name.Replace(t, "", 1);
        return t.Length > 0 ? new ScriptItem(t, shape) : null;
    }

    public static List<ScriptItem> Parse(string text) => (text ?? "").Split('\n').Select(ParseLine).Where(x => x != null).ToList();

    /// <summary>글자 폭에 맞춰 줄바꿈 (공백 기준, 너무 긴 단어는 글자 단위). 줄 안의 "\n" 글자도 줄바꿈</summary>
    public static string Wrap(string text, string font, double size, bool bold, double maxW)
    {
        var fam = Fonts.Family(font);
        using var f = new Font(fam, (float)Math.Max(1, size), Fonts.Style(fam, bold, false), GraphicsUnit.Pixel);
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        var fmt = StringFormat.GenericTypographic;
        double W(string s) => s.Length == 0 ? 0 : g.MeasureString(s, f, int.MaxValue, fmt).Width;
        var outp = new List<string>();
        foreach (var para in text.Replace("\\n", "\n").Split('\n'))
        {
            string line = "";
            foreach (var word in Regex.Split(para, @"(\s+)"))
            {
                if (word.Length == 0) continue;
                string tryLine = line + word;
                if (line.Length > 0 && W(tryLine.Trim()) > maxW) { outp.Add(line.Trim()); line = word.TrimStart(); }
                else line = tryLine;
                while (W(line) > maxW * 1.3 && line.Length > 1)
                {
                    int k = line.Length - 1;
                    while (k > 1 && W(line[..k]) > maxW) k--;
                    outp.Add(line[..k]); line = line[k..];
                }
            }
            if (line.Trim().Length > 0) outp.Add(line.Trim());
        }
        return string.Join("\n", outp);
    }

    public sealed class Opts { public bool Tail = true, Vertical; public int Chars = 9; public bool Rtl; }

    /// <summary>(cx, cy) 를 가운데로 대사 하나를 말풍선 + 글자 레이어로 (기록 없이 — 부르는 쪽이 History.Struct 로 감쌈)</summary>
    static Layer PlaceRaw(Document d, double cx, double cy, ScriptItem item, Opts o, Random rng)
    {
        // 말풍선 레이어를 먼저 (글자가 그 위에 오도록): 지금 것 → 있는 것 → 새로 (맨 위)
        var bl = Bubbles.IsEditable(d.Active) ? d.Active : d.Layers.LastOrDefault(Bubbles.IsEditable);
        if (bl == null)
        {
            bl = new Layer(d, "말풍선", "bubble") { Meta = new JsonObject { ["bubbles"] = new JsonArray() } };
            d.Layers.Add(bl);
        }
        var meta = TextRender.Defaults();
        meta["font"] = TextTool.Font; meta["size"] = TextTool.Size; meta["color"] = TextTool.Color_; meta["bold"] = TextTool.Bold;
        meta["align"] = "center"; meta["vertical"] = o.Vertical; meta["strokeWidth"] = TextTool.StrokeWidth; meta["strokeColor"] = TextTool.StrokeColor; meta["lineHeight"] = TextTool.LineHeight;
        meta["x"] = J.R(cx); meta["y"] = J.R(cy);
        meta["text"] = o.Vertical ? item.Text : Wrap(item.Text, TextTool.Font, TextTool.Size, TextTool.Bold, TextTool.Size * o.Chars);
        var tl = new Layer(d, item.Text.Length > 12 ? item.Text[..12] : item.Text, "text") { Meta = meta };
        d.Layers.Add(tl);
        TextRender.Render(d, tl);                      // 크기 재기 (meta.box)
        // 가운데 맞추기: 그려 본 크기만큼 위로 / (세로쓰기면) 오른쪽으로
        var b0 = (JsonObject)meta["box"];
        double bx = J.Num(b0, "x"), by = J.Num(b0, "y"), bw0 = J.Num(b0, "w"), bh0 = J.Num(b0, "h");
        meta["y"] = J.R(cy - bh0 / 2 + (J.Num(meta, "y") - by));
        if (o.Vertical) meta["x"] = J.R(cx + bw0 / 2 - (bx + bw0 - J.Num(meta, "x")));
        tl.Px = MetaLayers.Render(tl).s;
        var b = (JsonObject)meta["box"];
        double x = J.Num(b, "x"), y = J.Num(b, "y"), w = J.Num(b, "w"), h = J.Num(b, "h");
        double k = item.Shape == "rect" ? 1.15 : item.Shape == "cloud" ? 1.55 : 1.45;
        double pad = TextTool.Size * 0.6, bw = w * k + pad, bh = h * k + pad;
        var bt = Registry.Tools.OfType<BubbleTool>().FirstOrDefault();
        var bub = new JsonObject
        {
            ["id"] = "b" + DateTime.Now.Ticks.ToString("x") + rng.Next(10000),
            ["x"] = J.R(x + w / 2 - bw / 2), ["y"] = J.R(y + h / 2 - bh / 2), ["w"] = J.R(bw), ["h"] = J.R(bh),
            ["shape"] = item.Shape, ["line"] = bt?.Line ?? 4, ["fill"] = bt?.FillC ?? "#ffffff", ["stroke"] = bt?.StrokeC ?? "#000000",
            ["tailWidth"] = bt?.TailWidth ?? 0.45, ["seed"] = rng.Next(1_000_000),
            ["tail"] = item.Shape == "rect" || !o.Tail ? null : J.Pt(x + w / 2 + bw * 0.15, y + h / 2 + bh * 0.9),
            ["textId"] = tl.Id,
        };
        var bm = J.Clone(bl.Meta);
        if (bm["bubbles"] is not JsonArray arr) bm["bubbles"] = arr = new JsonArray();
        arr.Add(bub);
        bl.Meta = bm; bl.Px = MetaLayers.Render(bl).s;
        d.ActiveId = tl.Id;
        return tl;
    }

    static void Done(Document d) { d.Modified = true; App.InvalidateAll(); App.NotifyLayers(); }

    /// <summary>대사 하나 놓기 (실행취소 한 번)</summary>
    public static Layer PlaceLine(double cx, double cy, ScriptItem item, Opts o, int seed = -1)
    {
        var d = App.Doc; if (d == null) return null;
        var rng = seed >= 0 ? new Random(seed) : new Random();
        Layer tl = null;
        App.History.Struct(d, "대사 배치", () => tl = PlaceRaw(d, cx, cy, item, o, rng));
        Done(d);
        return tl;
    }

    /// <summary>컷을 읽는 순서로: 위 줄부터, 줄 안에서는 왼→오(웹툰·한국식) 또는 오→왼(일본식)</summary>
    public static List<(double x, double y, double w, double h)> PanelsInOrder(Document d, bool rtl)
    {
        var l = d.Layers.Find(x => x.Kind == "panel");   // 웹 panelLayer(false) 와 같은 레이어
        var list = (l == null ? new List<List<P2>>() : Panels.List(l.Meta)).Where(q => q.Count > 0).Select(q =>
        {
            double x0 = q.Min(p => p.X), y0 = q.Min(p => p.Y);
            return (x: x0, y: y0, w: q.Max(p => p.X) - x0, h: q.Max(p => p.Y) - y0);
        }).OrderBy(p => p.y).ToList();
        var rows = new List<List<(double x, double y, double w, double h)>>();
        foreach (var p in list)
        {
            var r = rows.FirstOrDefault(r => Math.Abs(r[0].y - p.y) < Math.Min(r[0].h, p.h) * 0.4);
            if (r != null) r.Add(p); else rows.Add(new() { p });
        }
        return rows.SelectMany(r => rtl ? r.OrderByDescending(a => a.x) : r.OrderBy(a => a.x)).ToList();
    }

    /// <summary>컷 순서대로 자동 배치: 대사를 컷 수만큼 나눠 각 컷 위쪽부터 (실행취소 한 번). 놓은 대사 수, 컷 수</summary>
    public static (int lines, int areas, bool panels) AutoPlace(List<ScriptItem> items, Opts o, int seed = -1)
    {
        var d = App.Doc;
        if (d == null || items.Count == 0) return (0, 0, false);
        var panels = PanelsInOrder(d, o.Rtl);
        var areas = panels.Count > 0 ? panels : new() { (d.W * 0.05, d.H * 0.05, d.W * 0.9, d.H * 0.9) };
        int per = (int)Math.Ceiling(items.Count / (double)areas.Count);
        var rng = seed >= 0 ? new Random(seed) : new Random();
        App.History.Struct(d, "대본 자동 배치", () =>
        {
            for (int i = 0; i < areas.Count; i++)
            {
                var a = areas[i];
                var mine = items.Skip(i * per).Take(per).ToList();
                for (int j = 0; j < mine.Count; j++)
                {
                    // 읽는 방향으로 지그재그: 첫 대사는 읽기 시작하는 쪽 위
                    double fx = mine.Count == 1 ? 0.5 : (o.Rtl ? 0.75 - 0.5 * j / (mine.Count - 1) : 0.25 + 0.5 * j / (mine.Count - 1));
                    double fy = 0.2 + 0.6 * j / Math.Max(1, mine.Count);
                    PlaceRaw(d, a.x + a.w * fx, a.y + a.h * fy, mine[j], o, rng);
                }
            }
        });
        Done(d);
        return (items.Count, areas.Count, panels.Count > 0);
    }
}

/// <summary>대본 배치 도구: 탭할 때마다 다음 대사를 그 자리에</summary>
public sealed class ScriptTool : ToolBase
{
    public override string Id => "script";
    public override string Name => "대본 배치";
    public override string Icon => "📜";
    public readonly List<ScriptItem> Queue = new();
    public readonly Script.Opts O = new();
    CUI.Host host;

    public override void Up(PointerArgs e)
    {
        if (Queue.Count == 0) { App.Toast("남은 대사가 없습니다. 옵션에서 대본을 넣으세요"); return; }
        if (App.Doc == null) return;
        Script.PlaceLine(e.X, e.Y, Queue[0], O);
        Queue.RemoveAt(0);
        host?.Rebuild();
        if (Queue.Count == 0) App.Toast("대사를 모두 놓았습니다");
    }

    public void Load()
    {
        var v = CUI.Form("대본 넣기", new[]
        {
            CUI.Txt("text", "대사 (한 줄 = 말풍선 하나)", App.Get("scriptDraft", ""), true),
            CUI.Info("\"이름: 대사\" 는 이름을 빼고 넣습니다. (생각) → 구름, ! 또는 (외침) → 외침, # 또는 [나레이션] → 사각 상자. 줄 안에서 \\n 은 줄바꿈."),
        }, "넣기");
        if (v == null || v.ContainsKey("__action")) return;
        string text = ((v["text"] as string) ?? "").Replace("\r\n", "\n");
        App.Set("scriptDraft", text);
        Queue.Clear(); Queue.AddRange(Script.Parse(text));
        App.Toast($"대사 {Queue.Count}줄. 놓을 곳을 차례로 탭하세요");
        host?.Rebuild();
    }

    public void AutoPlace()
    {
        if (Queue.Count == 0) { App.Toast("먼저 대본을 넣으세요"); return; }
        var items = Queue.ToList(); Queue.Clear();
        var (n, areas, panels) = Script.AutoPlace(items, O);
        App.Toast($"{n}줄을 {(panels ? $"컷 {areas}개에" : "페이지에")} 나눠 놓았습니다. 말풍선 도구로 위치를 다듬으세요");
        host?.Rebuild();
    }

    public override Control BuildOptions() { host = new CUI.Host(Items); return host.Panel; }
    IEnumerable<Control> Items()
    {
        yield return Opt.Title("대본");
        yield return Opt.Row(Opt.Button("대본 넣기…", Load, true), Opt.Button("컷 순서대로 자동 배치", AutoPlace));
        if (Queue.Count == 0) yield return Opt.Help("남은 대사가 없습니다");
        else
        {
            for (int i = 0; i < Math.Min(8, Queue.Count); i++)
                yield return new Label { Text = (i == 0 ? "▶ " : "   ") + Queue[i].Text, AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = i == 0 ? Opt.Accent : Opt.Fg };
            if (Queue.Count > 8) yield return Opt.Help($"… 외 {Queue.Count - 8}줄");
            yield return Opt.Row(Opt.Button("이 대사 건너뛰기", () => { Queue.RemoveAt(0); host?.Rebuild(); }), Opt.Button("비우기", () => { Queue.Clear(); host?.Rebuild(); }));
        }
        yield return Opt.Check("꼬리 달기", () => O.Tail, v => O.Tail = v);
        yield return Opt.Check("세로쓰기", () => O.Vertical, v => O.Vertical = v);
        yield return Opt.Slider("한 줄 글자 수", 4, 20, () => O.Chars, v => O.Chars = (int)Math.Round(v), step: 1);
        yield return Opt.Choice("읽는 방향", new[] { ("ltr", "왼→오 (웹툰·한국식)"), ("rtl", "오→왼 (일본식)") }, () => O.Rtl ? "rtl" : "ltr", v => O.Rtl = v == "rtl");
        yield return Opt.Help("글자 크기·글꼴은 텍스트 도구의 기본값을 따릅니다. 놓은 뒤에는 말풍선 도구로 옮기고 크기를 바꿀 수 있습니다 (대사도 같이 움직임).");
    }
}
