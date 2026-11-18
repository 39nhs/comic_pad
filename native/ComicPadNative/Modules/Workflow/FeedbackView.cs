// 피드백 보기: 편집자·동료가 보낸 피드백 파일(.cpfb, 러프 레이어 + 메모 핀)을 내 원고 위에 "얹어 보기만" 한다 (문서는 그대로).
//  - 빨간펜으로 보기 (피드백 선을 빨갛게), 투명도, 켜기/끄기 (보기 메뉴·이 창)
//  - 파일의 메모 핀은 "내 작품에 넣기" 로 doc.Extra["notes"] 에 (웹 판과 같은 값) → 하나씩 "해결" 표시
//  - 이미 받아 둔 피드백 폴더(파일 > 피드백 파일 받기)는 이 창에서 보이기/숨기기
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.Modules.Workflow;

public static class FeedbackView
{
    public sealed class Source
    {
        public string Path, From, When;
        public int W, H;                  // 내 원고 크기 (겹침 그림은 이 크기에 맞춰 늘림)
        public Bitmap Overlay;            // 원래 색 (투명 바탕)
        public Bitmap Red;                // 빨간펜
        public JsonArray Notes = new();
        public int LayerCount;
    }

    public static Source Current;
    public static bool On;
    public static double Opacity = 1;
    public static bool RedPen = true;
    public static event Action Changed;
    public const int MaxSide = 2400;
    static readonly Color Pen = Color.FromArgb(229, 57, 53);

    /// <summary>.cpfb 글 → 겹쳐 볼 것 (doc 크기에 맞춤). 형식이 아니면 예외</summary>
    public static Source Load(Document doc, string text, string path = null)
    {
        var fb = JsonNode.Parse(text) as JsonObject;
        if ((string)fb?["format"] != "comicpad-feedback") throw new InvalidDataException("ComicPad 피드백 파일이 아닙니다");
        int fw = U.Int(fb["doc"]?["width"], doc.W), fh = U.Int(fb["doc"]?["height"], doc.H);
        var src = new Source
        {
            Path = path, From = string.IsNullOrEmpty((string)fb["from"]) ? "이름 없음" : (string)fb["from"],
            When = DateTime.TryParse((string)fb["date"], out var dt) ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "", W = doc.W, H = doc.H,
        };
        var lj = fb["layers"] as JsonArray ?? new JsonArray();
        src.LayerCount = lj.Count;
        double s = Math.Min(1, MaxSide / (double)Math.Max(doc.W, doc.H));
        int ow = Math.Max(1, (int)Math.Round(doc.W * s)), oh = Math.Max(1, (int)Math.Round(doc.H * s));
        if (lj.Count > 0)
        {
            var fd = CpadIO.FromJson(new JsonObject { ["format"] = "comicpad", ["width"] = fw, ["height"] = fh, ["layers"] = lj.DeepClone() });
            fd.PaperVisible = false;
            foreach (var l in fd.Layers) { l.Visible = true; l.Draft = false; }
            var flat = App.Comp.Flatten(fd, new RenderOptions { Paper = false });
            using var full = CpadIO.ToBitmap(flat, flat.Full, 0);
            src.Overlay = full.Width == ow && full.Height == oh ? new Bitmap(full) : Img.Resize(full, ow, oh);
        }
        else src.Overlay = new Bitmap(ow, oh, PixelFormat.Format32bppArgb);
        src.Red = Recolor(src.Overlay, Pen);
        double sx = doc.W / (double)fw, sy = doc.H / (double)fh;
        foreach (var n in fb["notes"] as JsonArray ?? new JsonArray())
        {
            if (n is not JsonObject o) continue;
            var c = (JsonObject)o.DeepClone();
            c["x"] = U.Num(c["x"]) * sx; c["y"] = U.Num(c["y"]) * sy;
            if (string.IsNullOrEmpty((string)c["author"])) c["author"] = src.From;
            src.Notes.Add(c);
        }
        return src;
    }

    /// <summary>보이는 픽셀을 한 색으로 (알파는 그대로)</summary>
    public static unsafe Bitmap Recolor(Bitmap src, Color c)
    {
        var o = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var r = new Rectangle(0, 0, src.Width, src.Height);
        var ds = src.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dd = o.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < src.Height; y++)
            {
                byte* ps = (byte*)ds.Scan0 + y * ds.Stride, pd = (byte*)dd.Scan0 + y * dd.Stride;
                for (int x = 0; x < src.Width; x++, ps += 4, pd += 4) { pd[0] = c.B; pd[1] = c.G; pd[2] = c.R; pd[3] = ps[3]; }
            }
        }
        finally { src.UnlockBits(ds); o.UnlockBits(dd); }
        return o;
    }

    public static void Set(Source s, bool on = true)
    {
        if (Current != null && Current != s) { Current.Overlay?.Dispose(); Current.Red?.Dispose(); }
        Current = s; On = on && s != null;
        Refresh();
    }
    public static void Toggle() { if (Current == null) { OpenFile(); return; } On = !On; Refresh(); }
    public static void Refresh() { Changed?.Invoke(); U.Canvas?.RedrawAll(); }

    public static Matrix ViewMatrix(ViewXf v)
    {
        var m = new Matrix();
        m.Translate((float)v.PanX, (float)v.PanY);
        m.Scale((float)v.Zoom, (float)v.Zoom);
        if (v.Angle != 0) m.Rotate((float)v.Angle);
        if (v.Flip) m.Scale(-1, 1);
        return m;
    }

    /// <summary>그림판 겹침 그림 (Registry.Overlay): 켜져 있으면 피드백을 원고 위에</summary>
    public static void Draw(Graphics g, ViewXf v)
    {
        var s = Current;
        if (!On || s == null || App.Doc == null) return;
        var img = RedPen ? s.Red : s.Overlay;
        if (img == null) return;
        int dw = App.Doc.W, dh = App.Doc.H;
        // 다시 그리는 부분(clip)에 해당하는 문서 범위만 그림 (붓질 중 작은 부분만 다시 그릴 때 빠르게)
        var doc = new RectangleF(0, 0, dw, dh);
        var cb = g.VisibleClipBounds;
        using (var inv = ViewMatrix(v))
        {
            inv.Invert();
            var pts = new[] { new PointF(cb.Left, cb.Top), new PointF(cb.Right, cb.Top), new PointF(cb.Right, cb.Bottom), new PointF(cb.Left, cb.Bottom) };
            inv.TransformPoints(pts);
            var r = RectangleF.FromLTRB(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
            r.Inflate(4, 4);
            doc = RectangleF.Intersect(doc, r);
        }
        if (doc.Width <= 0 || doc.Height <= 0) return;
        float kx = img.Width / (float)dw, ky = img.Height / (float)dh;
        var src = new RectangleF(doc.X * kx, doc.Y * ky, doc.Width * kx, doc.Height * ky);
        var st = g.Save();
        using (var m = ViewMatrix(v)) g.MultiplyTransform(m);
        g.InterpolationMode = v.Zoom < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix { Matrix33 = (float)Math.Clamp(Opacity, 0, 1) });
        ia.SetWrapMode(WrapMode.TileFlipXY);
        var dst = new[] { new PointF(doc.Left, doc.Top), new PointF(doc.Right, doc.Top), new PointF(doc.Left, doc.Bottom) };
        g.DrawImage(img, dst, src, GraphicsUnit.Pixel, ia);
        // 파일에만 있는 메모(아직 작품에 넣지 않은 것)는 점선 동그라미로
        var mine = Feedback.Notes(App.Doc);
        using var pen = new System.Drawing.Pen(Pen, 2f / (float)v.Zoom) { DashStyle = DashStyle.Dash };
        foreach (var n in s.Notes.OfType<JsonObject>())
        {
            if (FindSame(mine, n) != null) continue;
            float x = (float)U.Num(n["x"]), y = (float)U.Num(n["y"]), r = 18f / (float)v.Zoom;
            g.DrawEllipse(pen, x - r, y - r, r * 2, r * 2);
        }
        g.Restore(st);
    }

    /// <summary>같은 메모 (같은 사람·같은 글·거의 같은 자리)</summary>
    public static JsonObject FindSame(JsonArray notes, JsonObject n)
    {
        foreach (var m in notes.OfType<JsonObject>())
            if ((string)m["text"] == (string)n["text"] && ((string)m["author"] ?? "") == ((string)n["author"] ?? "") && Math.Abs(U.Num(m["x"]) - U.Num(n["x"])) < 2 && Math.Abs(U.Num(m["y"]) - U.Num(n["y"])) < 2) return m;
        return null;
    }

    /// <summary>파일의 메모 핀을 내 작품 메모에 넣기 (이미 있는 것은 빼고). 넣은 수. 실행취소 한 번</summary>
    public static int MergeNotes(Document doc, Source s)
    {
        if (doc == null || s == null) return 0;
        var notes = (JsonArray)Feedback.Notes(doc).DeepClone();
        int n0 = 0;
        foreach (var n in s.Notes.OfType<JsonObject>())
        {
            if (FindSame(notes, n) != null) continue;
            var o = (JsonObject)n.DeepClone();
            o["id"] = "n" + Guid.NewGuid().ToString("N")[..7];
            if (o["color"] == null) o["color"] = "#e5484d";
            notes.Add(o); n0++;
        }
        if (n0 > 0) Feedback.SetNotes(doc, notes, "피드백 메모 넣기");
        return n0;
    }

    /// <summary>메모 해결 표시 바꾸기 (실행취소 가능, 웹 판과 같은 done 값)</summary>
    public static void SetDone(Document doc, string id, bool done)
    {
        var notes = (JsonArray)Feedback.Notes(doc).DeepClone();
        var n = notes.OfType<JsonObject>().FirstOrDefault(x => (string)x["id"] == id);
        if (n == null) return;
        n["done"] = done;
        Feedback.SetNotes(doc, notes, done ? "메모 해결" : "메모 해결 취소");
    }

    /// <summary>문서 안의 받은 피드백 폴더 (파일 > 피드백 파일 받기 로 만든 것)</summary>
    public static List<Layer> Folders(Document d) => d?.Layers.Where(l => l.IsFolder && l.Name.StartsWith("피드백")).ToList() ?? new();

    public static void SetFolderVisible(Document d, Layer f, bool vis)
    {
        if (f.Visible == vis) return;
        App.History.Struct(d, vis ? "피드백 보이기" : "피드백 숨기기", () => f.Visible = vis);
        App.NotifyLayers(); App.InvalidateAll();
    }

    public static void OpenFile()
    {
        if (App.Doc == null) return;
        using var dlg = new OpenFileDialog { Filter = "ComicPad 피드백 (*.cpfb)|*.cpfb;*.json|모든 파일|*.*", InitialDirectory = U.ExportsDir };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        try
        {
            var s = Load(App.Doc, File.ReadAllText(dlg.FileName), dlg.FileName);
            Set(s, true);
            App.Toast($"{s.From} 님의 피드백을 겹쳐 봅니다 (레이어 {s.LayerCount}개 · 메모 {s.Notes.Count}개). 문서는 바뀌지 않습니다");
        }
        catch (Exception ex) { App.Toast("피드백 파일을 읽지 못했습니다: " + ex.Message); }
        FeedbackWindow.Open();
    }
}

/// <summary>피드백 보기 창: 겹쳐 보기 켜기/끄기·빨간펜·투명도, 받은 폴더 보이기, 메모 해결 표시</summary>
public sealed class FeedbackWindow : WfForm
{
    static FeedbackWindow inst;
    readonly WfScroll body = new() { Dock = DockStyle.Fill };
    bool pending;

    public static FeedbackWindow Open()
    {
        if (inst == null || inst.IsDisposed) { inst = new FeedbackWindow(); inst.Show(App.Main); }
        else { inst.Render(); inst.Activate(); }
        return inst;
    }

    public FeedbackWindow() : base("피드백 보기 — ComicPad", 420, 640)
    {
        TopMost = true;
        if (App.Main != null && App.Main.Visible) { var r = App.Main.Bounds; Location = new Point(Math.Max(r.Left, r.Right - Width - 330), r.Top + 90); }
        Controls.Add(body);
        FeedbackView.Changed += Soon; ExtrasModule.ExtraChanged += OnExtra; App.DocChanged += Soon; App.LayersChanged += Soon;
        Render();
    }
    void OnExtra(string k) { if (k == "notes") Soon(); }
    void Soon()
    {
        if (pending || IsDisposed) return;
        pending = true;
        void Go() { pending = false; if (!IsDisposed) Render(); }
        if (IsHandleCreated) BeginInvoke((Action)Go); else Go();
    }

    public void Render()
    {
        var doc = App.Doc;
        var s = FeedbackView.Current;
        var items = new List<Control> { Opt.Title("피드백 파일 겹쳐 보기") };
        if (s == null) items.Add(Opt.Help("받은 피드백 파일(.cpfb)을 원고 위에 겹쳐 봅니다. 문서에는 아무것도 더하지 않습니다."));
        else
        {
            items.Add(Opt.Help($"{s.From} · {s.When} · 레이어 {s.LayerCount}개 · 메모 {s.Notes.Count}개{(s.Path != null ? "\n" + Path.GetFileName(s.Path) : "")}"));
            items.Add(Opt.Check("겹쳐 보기 켜기", () => FeedbackView.On, v => { FeedbackView.On = v; FeedbackView.Refresh(); }));
            items.Add(Opt.Check("빨간펜으로 보기 (선을 빨갛게)", () => FeedbackView.RedPen, v => { FeedbackView.RedPen = v; FeedbackView.Refresh(); }));
            items.Add(Opt.Slider("투명도", 0.1, 1, () => FeedbackView.Opacity, v => { FeedbackView.Opacity = v; U.Canvas?.RedrawAll(); }, pct: true));
            int notIn = doc == null ? 0 : s.Notes.OfType<JsonObject>().Count(n => FeedbackView.FindSame(Feedback.Notes(doc), n) == null);
            items.Add(Opt.Row(
                notIn > 0 ? Opt.Button($"메모 {notIn}개를 내 작품에 넣기", () => { int k = FeedbackView.MergeNotes(App.Doc, s); App.Toast($"메모 {k}개를 넣었습니다 (메모 핀)"); }, true) : null,
                Opt.Button("레이어로 받기…", () => { if (s.Path != null && App.Doc != null) { App.Toast(Feedback.Import(App.Doc, File.ReadAllText(s.Path))); App.NotifyLayers(); App.InvalidateAll(); } }),
                Opt.Button("닫기", () => FeedbackView.Set(null, false))));
        }
        items.Add(Opt.Row(Opt.Button(s == null ? "피드백 파일 열기…" : "다른 파일 열기…", FeedbackView.OpenFile, s == null)));
        var folders = FeedbackView.Folders(doc);
        if (folders.Count > 0)
        {
            items.Add(Opt.Title("받아 둔 피드백 레이어"));
            foreach (var f in folders) { var ff = f; items.Add(Opt.Check(f.Name, () => ff.Visible, v => FeedbackView.SetFolderVisible(App.Doc, ff, v))); }
        }
        var notes = Feedback.Notes(doc);
        items.Add(Opt.Title($"메모 핀 ({notes.Count(n => (bool?)n?["done"] != true)}개 남음 / {notes.Count}개)"));
        if (notes.Count == 0) items.Add(Opt.Help("메모가 없습니다. 메모 핀 도구(Y)로 그림 위에 메모를 남기거나, 피드백 파일의 메모를 넣으세요."));
        for (int i = 0; i < notes.Count; i++)
        {
            if (notes[i] is not JsonObject n) continue;
            string id = (string)n["id"], text = ((string)n["text"] ?? "").Split('\n')[0];
            bool done = (bool?)n["done"] ?? false;
            string who = (string)n["author"];
            var row = Opt.Check($"{i + 1}. {(text.Length > 0 ? text : "(빈 메모)")}{(string.IsNullOrEmpty(who) ? "" : $" — {who}")}", () => done, v => FeedbackView.SetDone(App.Doc, id, v));
            var go = Opt.Button("보기", () => Jump(n));
            items.Add(Opt.Row(row, go));
        }
        if (notes.Any(n => (bool?)n?["done"] == true))
            items.Add(Opt.Row(Opt.Button("해결된 메모 지우기", () => Feedback.SetNotes(App.Doc, new JsonArray(Feedback.Notes(App.Doc).Where(n => (bool?)n?["done"] != true).Select(n => n.DeepClone()).ToArray()), "해결된 메모 정리"))));
        items.Add(Opt.Help("체크 = 해결. 해결한 메모는 핀이 흐려지고 ✓ 로 바뀝니다 (웹 판에서도 같게 보임)."));
        body.Fill(items);
    }

    static void Jump(JsonObject n)
    {
        var cv = U.Canvas; if (cv == null) return;
        var (px, py) = cv.V.ToScreen(U.Num(n["x"]), U.Num(n["y"]));
        cv.V.PanX += cv.Width / 2.0 - px; cv.V.PanY += cv.Height / 2.0 - py;
        cv.RedrawAll();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        FeedbackView.Changed -= Soon; ExtrasModule.ExtraChanged -= OnExtra; App.DocChanged -= Soon; App.LayersChanged -= Soon;
        if (inst == this) inst = null;
        base.OnFormClosed(e);
    }
}
