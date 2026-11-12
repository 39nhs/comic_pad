// 피드백 (웹 판 ui/feedback.js + 메모 핀): 러프(의견) 레이어와 메모 핀만 작은 파일(.cpfb)로 주고받고, 받은 파일을 내 원고 위에 얹어 본다.
// 메모 핀은 doc.Extra["notes"] = [{ id, x, y, text, color, author, done }] (웹 판과 같은 값). 내보낸 그림에는 나오지 않음.
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public static class Feedback
{
    public static JsonArray Notes(Document d) => d?.Extra["notes"] as JsonArray ?? new JsonArray();

    public static void SetNotes(Document d, JsonArray next, string label)
    {
        ExtraEdit.Set(d, "notes", next, label);
        App.Invalidate(Rect.Empty);
        U.Canvas?.Present();
    }

    /// <summary>.cpfb 내용 만들기 (웹 판과 같은 형식)</summary>
    public static JsonObject Build(Document doc, IEnumerable<Layer> layers, bool withNotes, string from)
    {
        var tmp = new Document(doc.W, doc.H);
        foreach (var l in layers) tmp.Layers.Add(l);
        var lj = CpadIO.ToJson(tmp)["layers"]?.DeepClone() as JsonArray ?? new JsonArray();
        var notes = new JsonArray();
        if (withNotes)
            foreach (var n in Notes(doc))
            {
                var o = (JsonObject)n.DeepClone();
                if (string.IsNullOrEmpty((string)o["author"])) o["author"] = from ?? "";
                notes.Add(o);
            }
        return new JsonObject
        {
            ["format"] = "comicpad-feedback", ["version"] = 1, ["from"] = from ?? "", ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["doc"] = new JsonObject { ["name"] = doc.Name, ["width"] = doc.W, ["height"] = doc.H },
            ["notes"] = notes, ["layers"] = lj,
        };
    }

    public static void ExportDialog()
    {
        var doc = App.Doc; if (doc == null) return;
        var rough = doc.Layers.Where(l => l.Rough && !l.IsFolder).ToList();
        var v = Dlg.Form("피드백 파일 보내기", new[]
        {
            Field.Text("name", "보내는 사람", App.Get("userName", "")),
            Field.Segment("what", "넣을 레이어", rough.Count > 0 ? "rough" : "none", ("rough", $"러프 레이어 ({rough.Count})"), ("sel", "고른 레이어 (1)"), ("none", "메모만")),
            Field.Check("notes", $"메모 핀 넣기 ({Notes(doc).Count}개)", true),
            Field.Info("그림 전체가 아니라 의견만 담긴 작은 파일입니다. 받는 사람은 파일 > 피드백 파일 받기 로 자기 원고 위에 얹어 봅니다."),
        }, "만들기");
        if (v == null) return;
        string name = (string)v["name"] ?? "";
        if (name.Length > 0) { App.Settings["userName"] = name; App.SaveSettingsSoon(); }
        string what = (string)v["what"];
        var layers = what == "rough" ? rough : what == "sel" && doc.Active != null && !doc.Active.IsFolder ? new List<Layer> { doc.Active } : new List<Layer>();
        var json = Build(doc, layers, (bool)v["notes"], name);
        string file = $"{U.SafeName(doc.Name)}_피드백_{U.SafeName(name.Length > 0 ? name : "_")}_{U.FileStamp()}.cpfb".Replace("__", "_").Replace("__", "_");
        string path = Path.Combine(U.ExportsDir, file);
        File.WriteAllText(path, json.ToJsonString(U.Compact));
        App.Toast($"피드백 파일: {path} (이 파일을 메신저 등으로 보내세요)");
    }

    public static void ImportDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "ComicPad 피드백 (*.cpfb)|*.cpfb;*.json|모든 파일|*.*", InitialDirectory = U.ExportsDir };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        string msg = Import(App.Doc, File.ReadAllText(dlg.FileName));
        App.NotifyLayers(); App.InvalidateAll();
        App.Toast(msg);
    }

    /// <summary>받은 피드백을 문서에 얹기 (실행취소 한 번으로 되돌림). 알림 글을 돌려줌</summary>
    public static string Import(Document doc, string text)
    {
        JsonObject fb;
        try { fb = JsonNode.Parse(text) as JsonObject; } catch { return "피드백 파일을 읽지 못했습니다"; }
        if ((string)fb?["format"] != "comicpad-feedback") return "ComicPad 피드백 파일이 아닙니다";
        int fw = U.Int(fb["doc"]?["width"], doc.W), fh = U.Int(fb["doc"]?["height"], doc.H);
        double sx = doc.W / (double)fw, sy = doc.H / (double)fh;
        bool same = Math.Abs(sx - 1) < 0.001 && Math.Abs(sy - 1) < 0.001;
        string who = string.IsNullOrEmpty((string)fb["from"]) ? "이름 없음" : (string)fb["from"];
        string when = DateTime.TryParse((string)fb["date"], out var dt) ? dt.ToLocalTime().ToString("yyyy. M. d. tt h:mm:ss") : "";
        var lj = fb["layers"] as JsonArray ?? new JsonArray();
        var notesIn = fb["notes"] as JsonArray ?? new JsonArray();
        var edits = new List<IEdit>();
        if (lj.Count > 0)
        {
            // 보낸 쪽 크기로 읽고, 크기가 다르면 내 원고 크기로 늘림
            var src = CpadIO.FromJson(new JsonObject { ["format"] = "comicpad", ["width"] = fw, ["height"] = fh, ["layers"] = lj.DeepClone() });
            var before = StructSnap.Take(doc);
            var folder = new Layer(doc, $"피드백: {who} ({when})", "folder") { Expanded = true };
            doc.Layers.Add(folder);
            foreach (var l in src.Layers)
            {
                var nl = new Layer
                {
                    Name = l.Name, Kind = l.Kind == "folder" ? "raster" : l.Kind, Visible = l.Visible, Opacity = l.Opacity, Blend = l.Blend, Tint = l.Tint, Draft = l.Draft,
                    Meta = l.Meta, Tone = l.Tone, Fx = l.Fx, Extra = l.Extra, Parent = folder.Id, Rough = true, Locked = false,
                    Px = l.Px == null ? new Surface(doc.W, doc.H) : same ? l.Px : Resample(l.Px, doc.W, doc.H),
                };
                doc.Layers.Insert(doc.Layers.IndexOf(folder), nl);
            }
            doc.ActiveId = folder.Id;
            edits.Add(new StructEdit("피드백 받기", before, StructSnap.Take(doc)));
        }
        if (notesIn.Count > 0)
        {
            var before = Notes(doc).DeepClone() as JsonArray;
            var after = (JsonArray)before.DeepClone();
            foreach (var n in notesIn)
            {
                var o = (JsonObject)n.DeepClone();
                o["id"] = "n" + Guid.NewGuid().ToString("N")[..7];
                o["x"] = (U.Num(o["x"])) * sx; o["y"] = (U.Num(o["y"])) * sy;
                if (string.IsNullOrEmpty((string)o["author"])) o["author"] = who;
                after.Add(o);
            }
            doc.Extra["notes"] = after.DeepClone();
            edits.Add(new ExtraEdit(doc, "notes", before, after, "피드백 메모"));
        }
        if (edits.Count > 0) { App.History.PushGroup("피드백 받기", edits.ToArray()); doc.Modified = true; }
        return $"{who} 님의 피드백: 레이어 {lj.Count}개, 메모 {notesIn.Count}개{(same ? "" : " (원고 크기가 달라 맞춰 늘림)")}. 다 보면 폴더째 숨기거나 지우세요";
    }

    static Surface Resample(Surface s, int w, int h)
    {
        using var b = CpadIO.ToBitmap(s, s.Full);
        using var r = Img.Resize(b, w, h);
        var o = new Surface(w, h);
        CpadIO.DrawBitmap(o, r, 0, 0);
        return o;
    }
}

/// <summary>메모 핀 도구 (웹 판 memoTool): 빈 곳 탭 = 새 메모, 핀 탭 = 보기/수정/삭제, 핀 끌기 = 이동</summary>
public sealed class MemoTool : ToolBase
{
    public override string Id => "memo";
    public override string Name => "메모";
    public override string Icon => "📝";
    public override Keys Key => Keys.Y;
    const int PIN = 13;
    JsonObject dragN; bool create, moved; double x0, y0, sx0, sy0, ox, oy, cx, cy; JsonArray before;
    public override bool Busy => dragN != null || create;

    static ViewXf V => U.Canvas?.V;

    JsonObject NoteAt(double sx, double sy)
    {
        var notes = Feedback.Notes(App.Doc); var v = V; if (v == null) return null;
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            var (px, py) = v.ToScreen(U.Num(notes[i]["x"]), U.Num(notes[i]["y"]));
            if (Math.Sqrt((px - sx) * (px - sx) + (py - sy) * (py - sy)) <= PIN + 4) return (JsonObject)notes[i];
            // 핀 머리도
            if (Math.Sqrt((px - sx) * (px - sx) + (py - PIN - 4 - sy) * (py - PIN - 4 - sy)) <= PIN + 2) return (JsonObject)notes[i];
        }
        return null;
    }

    public override void Down(PointerArgs e)
    {
        var d = App.Doc; if (d == null) return;
        var n = NoteAt(e.SX, e.SY);
        if (n != null) { dragN = n; moved = false; x0 = e.X; y0 = e.Y; sx0 = e.SX; sy0 = e.SY; ox = U.Num(n["x"]); oy = U.Num(n["y"]); before = (JsonArray)Feedback.Notes(d).DeepClone(); }
        else { create = true; cx = e.X; cy = e.Y; }
    }
    public override void Move(PointerArgs e)
    {
        if (dragN == null) return;
        if (Math.Abs(e.SX - sx0) + Math.Abs(e.SY - sy0) > 5) moved = true;
        if (moved) { dragN["x"] = ox + e.X - x0; dragN["y"] = oy + e.Y - y0; U.Canvas?.Present(); }
    }
    public override void Up(PointerArgs e)
    {
        var d = App.Doc;
        if (create) { create = false; Edit(new JsonObject { ["id"] = "n" + Convert.ToString(DateTimeOffset.Now.ToUnixTimeMilliseconds(), 16), ["x"] = cx, ["y"] = cy, ["text"] = "", ["color"] = "#f5c518", ["author"] = App.Get("userName", "") }, true); return; }
        var n = dragN; dragN = null;
        if (n == null || d == null) return;
        if (!moved) { Edit(n, false); return; }
        var after = (JsonArray)Feedback.Notes(d).DeepClone();
        d.Extra["notes"] = before;
        Feedback.SetNotes(d, after, "메모 이동");
    }
    public override void Cancel() { if (dragN != null) { dragN["x"] = ox; dragN["y"] = oy; } dragN = null; create = false; U.Canvas?.Present(); }

    public static void Edit(JsonObject n, bool isNew)
    {
        var d = App.Doc; if (d == null) return;
        var v = Dlg.Form(isNew ? "새 메모" : "메모", new[]
        {
            Field.Area("text", "내용", (string)n["text"] ?? ""),
            Field.Select("color", "색", (string)n["color"] ?? "#f5c518", ("#f5c518", "노랑"), ("#e5484d", "빨강 (수정 요청)"), ("#2fa35b", "초록 (확인)"), ("#2f7cf6", "파랑 (참고)")),
            Field.Check("done", "해결됨", (bool?)n["done"] ?? false),
        }, "저장", isNew ? null : new[] { ("삭제", "delete") });
        var notes = (JsonArray)Feedback.Notes(d).DeepClone();
        int i = notes.Select((x, k) => (x, k)).FirstOrDefault(t => (string)t.x?["id"] == (string)n["id"], (null, -1)).Item2;
        if ((string)v?["__action"] == "delete") { if (i >= 0) notes.RemoveAt(i); Feedback.SetNotes(d, notes, "메모 삭제"); return; }
        if (v == null) return;
        var item = (JsonObject)n.DeepClone();
        item["text"] = (string)v["text"]; item["color"] = (string)v["color"]; item["done"] = (bool)v["done"];
        if (i >= 0) notes[i] = item; else notes.Add(item);
        Feedback.SetNotes(d, notes, isNew ? "메모 추가" : "메모 수정");
    }

    public override Rectangle OverlayBounds(ViewXf v)
    {
        var notes = Feedback.Notes(App.Doc);
        if (notes.Count == 0 || !App.Get("showNotes", true)) return Rectangle.Empty;
        var r = Rectangle.Empty;
        foreach (var n in notes)
        {
            var (px, py) = v.ToScreen(U.Num(n["x"]), U.Num(n["y"]));
            var nr = new Rectangle((int)px - PIN - 4, (int)py - PIN * 2 - 10, 200 + PIN * 2, PIN * 2 + 16);
            r = r.IsEmpty ? nr : Rectangle.Union(r, nr);
        }
        return r;
    }

    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var notes = Feedback.Notes(App.Doc);
        if (notes.Count == 0 || !App.Get("showNotes", true)) return;
        using var font = new Font("Malgun Gothic", 9f);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            var (px, py) = v.ToScreen(U.Num(n["x"]), U.Num(n["y"]));
            bool done = (bool?)n["done"] ?? false;
            int alpha = done ? 115 : 255;
            Color c; try { c = ColorTranslator.FromHtml((string)n["color"] ?? "#f5c518"); } catch { c = Color.Gold; }
            float cx = (float)px, cy = (float)py - PIN - 4;
            using var path = new GraphicsPath();
            path.AddArc(cx - PIN, cy - PIN, PIN * 2, PIN * 2, 112, 316);
            path.AddLine(cx + PIN * (float)Math.Cos(68 * Math.PI / 180), cy + PIN * (float)Math.Sin(68 * Math.PI / 180), cx, (float)py);
            path.CloseFigure();
            using (var br = new SolidBrush(Color.FromArgb(alpha, c))) g.FillPath(br, path);
            using (var pen = new Pen(Color.FromArgb(alpha * 6 / 10, 0, 0, 0), 1.5f)) g.DrawPath(pen, path);
            using (var tb = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0))) g.DrawString(done ? "✓" : (i + 1).ToString(), font, tb, cx, cy, fmt);
            string t = ((string)n["text"] ?? "").Split('\n')[0];
            if (t.Length > 18) t = t[..18];
            if (t.Length > 0)
            {
                var sz = g.MeasureString(t, font);
                using var bg = new SolidBrush(Color.FromArgb(242, 255, 255, 240));
                g.FillRectangle(bg, cx + PIN + 2, cy - 10, sz.Width + 10, 20);
                using var fg = new SolidBrush(Color.FromArgb(34, 34, 34));
                g.DrawString(t, font, fg, cx + PIN + 7, cy - sz.Height / 2);
            }
        }
    }

    public override Control BuildOptions()
    {
        var p = Opt.Panel();
        void Fill()
        {
            if (p.IsDisposed) return;
            p.SuspendLayout();
            foreach (Control c in p.Controls.Cast<Control>().ToList()) c.Dispose();
            var notes = Feedback.Notes(App.Doc);
            p.Controls.Add(Opt.Title("메모"));
            p.Controls.Add(Opt.Check("메모 핀 보이기", () => App.Get("showNotes", true), v => { App.Set("showNotes", v); U.Canvas?.Present(); }));
            p.Controls.Add(Opt.Help("빈 곳 탭: 새 메모 · 핀 탭: 보기/수정/삭제 · 핀 드래그: 이동. 메모는 작품 파일에 저장되고 내보낸 그림에는 나오지 않습니다."));
            for (int i = 0; i < notes.Count; i++)
            {
                var n = (JsonObject)notes[i];
                string t = ((string)n["text"] ?? "").Split('\n')[0];
                p.Controls.Add(Opt.Button($"{((bool?)n["done"] == true ? "✓" : (i + 1).ToString())}  {(t.Length > 0 ? t : "(빈 메모)")}", () =>
                {
                    var cv = U.Canvas;
                    if (cv != null) { var (px, py) = cv.V.ToScreen(U.Num(n["x"]), U.Num(n["y"])); cv.V.PanX += cv.Width / 2.0 - px; cv.V.PanY += cv.Height / 2.0 - py; cv.RedrawAll(); }
                    Edit(n, false);
                }));
            }
            if (notes.Count > 0) p.Controls.Add(Opt.Button("해결된 메모 지우기", () => Feedback.SetNotes(App.Doc, new JsonArray(Feedback.Notes(App.Doc).Where(n => (bool?)n?["done"] != true).Select(n => n.DeepClone()).ToArray()), "해결된 메모 정리")));
            p.ResumeLayout();
        }
        void OnChange(string k) { if (k != "notes" || p.IsDisposed) return; if (p.IsHandleCreated) p.BeginInvoke((Action)Fill); else Fill(); }
        ExtrasModule.ExtraChanged += OnChange;
        p.Disposed += (_, _) => ExtrasModule.ExtraChanged -= OnChange;
        Fill();
        return p;
    }
}
