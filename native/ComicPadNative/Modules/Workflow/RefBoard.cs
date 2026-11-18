// 참고 자료 보드 (자료·모델 사진·설정화를 한곳에 모아 두는 판): 값·저장·그리기·레이어로 넣기.
//  - 그림은 data/refboard/images/<해시>.png 에 한 번만 (같은 그림을 여러 번 넣거나 여러 보드에 넣어도 파일 하나). 해시 = 픽셀 SHA-1
//  - 보드는 data/refboard/boards/<열쇠>.json : 작품마다 하나 ("story_작품", "file_이름_경로해시") + 공용 보드 "_global"
//  - 항목: 그림(옮기기·크기·회전·자르기·좌우 반전·흑백) / 메모(글), 묶음(group id 같은 것끼리 같이 움직임)
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;

namespace ComicPadNative.Modules.Workflow;

public sealed class RefItem
{
    public string Id = U.Uid("r");
    public string Kind = "image";          // image | note
    public string Hash, Name;
    public double X, Y;                    // 가운데 (보드 좌표)
    public double Scale = 1, Rot;          // 배율, 회전(도)
    public RectangleF? Crop;               // 원본 픽셀 기준 자르기 (null = 전체)
    public bool FlipH, Gray;
    public string Group;                   // 묶음 id (같은 값끼리 같이 고르고 움직임)
    public string Text = "", Color = "#fff3a6";
    public double W = 220, H = 120;        // 메모 크기 (보드 좌표, 배율 전)

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["id"] = Id, ["kind"] = Kind, ["x"] = Math.Round(X, 2), ["y"] = Math.Round(Y, 2), ["scale"] = Math.Round(Scale, 5), ["rot"] = Math.Round(Rot, 3) };
        if (Kind == "image") { o["hash"] = Hash; o["name"] = Name; }
        else { o["text"] = Text; o["color"] = Color; o["w"] = W; o["h"] = H; }
        if (Crop is RectangleF c) o["crop"] = new JsonArray(c.X, c.Y, c.Width, c.Height);
        if (FlipH) o["flipH"] = true;
        if (Gray) o["gray"] = true;
        if (Group != null) o["group"] = Group;
        return o;
    }
    public static RefItem FromJson(JsonObject o)
    {
        var it = new RefItem
        {
            Id = (string)o["id"] ?? U.Uid("r"), Kind = (string)o["kind"] ?? "image", Hash = (string)o["hash"], Name = (string)o["name"],
            X = U.Num(o["x"]), Y = U.Num(o["y"]), Scale = U.Num(o["scale"], 1), Rot = U.Num(o["rot"]),
            FlipH = (bool?)o["flipH"] ?? false, Gray = (bool?)o["gray"] ?? false, Group = (string)o["group"],
            Text = (string)o["text"] ?? "", Color = (string)o["color"] ?? "#fff3a6", W = U.Num(o["w"], 220), H = U.Num(o["h"], 120),
        };
        if (o["crop"] is JsonArray c && c.Count == 4) it.Crop = new RectangleF((float)U.Num(c[0]), (float)U.Num(c[1]), (float)U.Num(c[2]), (float)U.Num(c[3]));
        if (it.Scale <= 0) it.Scale = 1;
        return it;
    }
    public RefItem Copy() => FromJson(ToJson());
}

public sealed class RefBoard
{
    public string Key = "_global", Name = "공용 보드";
    public readonly List<RefItem> Items = new();   // 아래 → 위
    public double PanX = 40, PanY = 40, Zoom = 0.5;
    public bool ViewFlip, ViewGray;

    public RefItem Find(string id) => Items.Find(i => i.Id == id);
    public JsonObject ToJson() => new()
    {
        ["format"] = "comicpad-refboard", ["version"] = 1, ["name"] = Name,
        ["view"] = new JsonObject { ["panX"] = Math.Round(PanX, 1), ["panY"] = Math.Round(PanY, 1), ["zoom"] = Math.Round(Zoom, 5), ["flip"] = ViewFlip, ["gray"] = ViewGray },
        ["items"] = new JsonArray(Items.Select(i => (JsonNode)i.ToJson()).ToArray()),
    };
    public static RefBoard FromJson(JsonObject o, string key)
    {
        var b = new RefBoard { Key = key, Name = (string)o?["name"] ?? key };
        if (o?["view"] is JsonObject v) { b.PanX = U.Num(v["panX"], 40); b.PanY = U.Num(v["panY"], 40); b.Zoom = Math.Clamp(U.Num(v["zoom"], 0.5), 0.02, 20); b.ViewFlip = (bool?)v["flip"] ?? false; b.ViewGray = (bool?)v["gray"] ?? false; }
        if (o?["items"] is JsonArray a) foreach (var n in a.OfType<JsonObject>()) b.Items.Add(RefItem.FromJson(n));
        return b;
    }
}

/// <summary>그림 저장소 (해시로 중복 없이) + 보드 파일</summary>
public static class RefStore
{
    public const int MaxSide = 4096;
    public static string Root { get { var p = Path.Combine(App.DataDir, "refboard"); Directory.CreateDirectory(p); return p; } }
    public static string ImagesDir { get { var p = Path.Combine(Root, "images"); Directory.CreateDirectory(p); return p; } }
    public static string BoardsDir { get { var p = Path.Combine(Root, "boards"); Directory.CreateDirectory(p); return p; } }
    static readonly Dictionary<string, Bitmap> cache = new();

    /// <summary>픽셀 해시 (가로·세로 + ARGB) — 같은 그림이면 파일 형식이 달라도 같은 값</summary>
    public static unsafe string Hash(Bitmap b)
    {
        using var sha = SHA1.Create();
        var r = new Rectangle(0, 0, b.Width, b.Height);
        var d = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            sha.TransformBlock(BitConverter.GetBytes(b.Width), 0, 4, null, 0);
            sha.TransformBlock(BitConverter.GetBytes(b.Height), 0, 4, null, 0);
            var row = new byte[b.Width * 4];
            for (int y = 0; y < b.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, row.Length);
                sha.TransformBlock(row, 0, row.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }
        finally { b.UnlockBits(d); }
        return Convert.ToHexString(sha.Hash).ToLowerInvariant();
    }

    public static string PathOf(string hash) => Path.Combine(ImagesDir, hash + ".png");

    /// <summary>그림 넣기 → 해시 (이미 있으면 파일을 다시 쓰지 않음). 너무 크면 긴 쪽 4096 으로</summary>
    public static string AddImage(Bitmap src)
    {
        Bitmap b = src;
        bool own = false;
        if (Math.Max(src.Width, src.Height) > MaxSide) { double s = MaxSide / (double)Math.Max(src.Width, src.Height); b = Img.Resize(src, Math.Max(1, (int)(src.Width * s)), Math.Max(1, (int)(src.Height * s))); own = true; }
        else if (src.PixelFormat != PixelFormat.Format32bppArgb) { b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb); using (var g = Graphics.FromImage(b)) g.DrawImage(src, 0, 0, src.Width, src.Height); own = true; }
        string h = Hash(b);
        string p = PathOf(h);
        if (!File.Exists(p)) { string tmp = p + ".tmp"; b.Save(tmp, ImageFormat.Png); File.Move(tmp, p, true); }
        lock (cache) if (!cache.ContainsKey(h)) cache[h] = own ? b : new Bitmap(b);
        if (own && cache[h] != b) b.Dispose();
        return h;
    }

    public static Bitmap Image(string hash)
    {
        if (hash == null) return null;
        lock (cache)
        {
            if (cache.TryGetValue(hash, out var b)) return b;
            var p = PathOf(hash);
            if (!File.Exists(p)) return null;
            b = Img.Load(p);
            if (b != null) cache[hash] = b;
            return b;
        }
    }
    public static int ImageCount => Directory.Exists(ImagesDir) ? Directory.GetFiles(ImagesDir, "*.png").Length : 0;

    /// <summary>문서의 보드 열쇠: 여러 페이지 작품 → "story_작품", 저장한 그림 → "file_이름_경로해시", 아니면 공용</summary>
    public static string KeyFor(Document d)
    {
        string story = null; try { story = Extras.Story.StoryOf(d); } catch { }
        if (story != null) return "story_" + U.SafeName(story);
        if (d?.FilePath != null)
        {
            var h = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(d.FilePath).ToLowerInvariant())))[..8].ToLowerInvariant();
            return "file_" + U.SafeName(Path.GetFileNameWithoutExtension(d.FilePath)) + "_" + h;
        }
        return "_global";
    }
    public static string TitleFor(Document d)
    {
        string story = null; try { story = Extras.Story.StoryOf(d); } catch { }
        return story ?? (d?.FilePath != null ? Path.GetFileNameWithoutExtension(d.FilePath) : "공용");
    }
    public static string BoardPath(string key) => Path.Combine(BoardsDir, key + ".json");

    public static RefBoard Load(string key, string name = null)
    {
        RefBoard b;
        try { b = File.Exists(BoardPath(key)) ? RefBoard.FromJson(JsonNode.Parse(File.ReadAllText(BoardPath(key))) as JsonObject, key) : new RefBoard { Key = key }; }
        catch { b = new RefBoard { Key = key }; }
        if (name != null) b.Name = name;
        else if (key == "_global") b.Name = "공용 보드";
        return b;
    }
    public static void Save(RefBoard b)
    {
        string p = BoardPath(b.Key), tmp = p + ".tmp";
        File.WriteAllText(tmp, b.ToJson().ToJsonString(U.Indent1));
        File.Move(tmp, p, true);
    }

    /// <summary>어느 보드도 쓰지 않는 그림 파일 지우기. 지운 수</summary>
    public static int Cleanup(IEnumerable<RefBoard> openBoards = null)
    {
        var used = new HashSet<string>();
        foreach (var f in Directory.GetFiles(BoardsDir, "*.json"))
        {
            try { if (JsonNode.Parse(File.ReadAllText(f))?["items"] is JsonArray a) foreach (var n in a) if ((string)n?["hash"] is string h) used.Add(h); } catch { return 0; }   // 못 읽는 보드가 있으면 아무것도 지우지 않음
        }
        if (openBoards != null) foreach (var b in openBoards) foreach (var i in b.Items) if (i.Hash != null) used.Add(i.Hash);
        int n0 = 0;
        foreach (var f in Directory.GetFiles(ImagesDir, "*.png"))
        {
            string h = Path.GetFileNameWithoutExtension(f);
            if (used.Contains(h)) continue;
            try { File.Delete(f); n0++; lock (cache) if (cache.Remove(h, out var bm)) bm.Dispose(); } catch { }
        }
        return n0;
    }
}

/// <summary>보드 계산·그리기</summary>
public static class RefOps
{
    /// <summary>항목 원래 크기 (보드 좌표, 배율 전): 그림은 자른 크기, 메모는 W×H</summary>
    public static SizeF LocalSize(RefItem it)
    {
        if (it.Kind != "image") return new SizeF((float)it.W, (float)it.H);
        if (it.Crop is RectangleF c) return c.Size;
        var b = RefStore.Image(it.Hash);
        return b == null ? new SizeF(200, 150) : new SizeF(b.Width, b.Height);
    }
    public static RectangleF SourceRect(RefItem it)
    {
        if (it.Crop is RectangleF c) return c;
        var b = RefStore.Image(it.Hash);
        return b == null ? new RectangleF(0, 0, 200, 150) : new RectangleF(0, 0, b.Width, b.Height);
    }

    /// <summary>항목 안 좌표(가운데 0,0) → 보드 좌표</summary>
    public static Matrix ItemMatrix(RefItem it)
    {
        var m = new Matrix();
        m.Translate((float)it.X, (float)it.Y);
        m.Rotate((float)it.Rot);
        m.Scale((float)(it.Scale * (it.FlipH ? -1 : 1)), (float)it.Scale);
        return m;
    }
    /// <summary>보드 → 화면 (보드 전체 좌우 반전 보기 포함)</summary>
    public static Matrix ViewMatrix(RefBoard b, int viewW)
    {
        var m = new Matrix();
        if (b.ViewFlip) { m.Translate(viewW, 0); m.Scale(-1, 1); }
        m.Translate((float)b.PanX, (float)b.PanY);
        m.Scale((float)b.Zoom, (float)b.Zoom);
        return m;
    }
    public static PointF[] Corners(RefItem it, Matrix outer = null)
    {
        var s = LocalSize(it);
        var pts = new[] { new PointF(-s.Width / 2, -s.Height / 2), new PointF(s.Width / 2, -s.Height / 2), new PointF(s.Width / 2, s.Height / 2), new PointF(-s.Width / 2, s.Height / 2) };
        using var m = ItemMatrix(it);
        if (outer != null) m.Multiply(outer, MatrixOrder.Append);
        m.TransformPoints(pts);
        return pts;
    }
    public static RectangleF Bounds(RefItem it, Matrix outer = null)
    {
        var p = Corners(it, outer);
        float x0 = p.Min(q => q.X), y0 = p.Min(q => q.Y), x1 = p.Max(q => q.X), y1 = p.Max(q => q.Y);
        return RectangleF.FromLTRB(x0, y0, x1, y1);
    }
    /// <summary>보드 좌표 → 항목 안 좌표</summary>
    public static PointF ToLocal(RefItem it, PointF boardPt)
    {
        using var m = ItemMatrix(it);
        m.Invert();
        var a = new[] { boardPt };
        m.TransformPoints(a);
        return a[0];
    }
    public static bool Hit(RefItem it, PointF boardPt)
    {
        var l = ToLocal(it, boardPt); var s = LocalSize(it);
        return Math.Abs(l.X) <= s.Width / 2 && Math.Abs(l.Y) <= s.Height / 2;
    }
    public static RefItem HitTest(RefBoard b, PointF boardPt)
    {
        for (int i = b.Items.Count - 1; i >= 0; i--) if (Hit(b.Items[i], boardPt)) return b.Items[i];
        return null;
    }

    static readonly ColorMatrix GrayCm = new(new[]
    {
        new[] { 0.299f, 0.299f, 0.299f, 0, 0 }, new[] { 0.587f, 0.587f, 0.587f, 0, 0 }, new[] { 0.114f, 0.114f, 0.114f, 0, 0 }, new[] { 0f, 0, 0, 1, 0 }, new[] { 0f, 0, 0, 0, 1 },
    });

    /// <summary>항목 하나 그리기 (g 의 변환 = 보드 → 화면)</summary>
    public static void DrawItem(Graphics g, RefItem it, bool gray)
    {
        var st = g.Save();
        using (var m = ItemMatrix(it)) g.MultiplyTransform(m);
        var s = LocalSize(it);
        var dst = new RectangleF(-s.Width / 2, -s.Height / 2, s.Width, s.Height);
        if (it.Kind == "image")
        {
            var bmp = RefStore.Image(it.Hash);
            if (bmp == null) { g.FillRectangle(Brushes.DimGray, dst); g.DrawString("그림 없음", SystemFonts.DefaultFont, Brushes.White, dst.X + 4, dst.Y + 4); }
            else
            {
                var src = SourceRect(it);
                var pts = new[] { new PointF(dst.Left, dst.Top), new PointF(dst.Right, dst.Top), new PointF(dst.Left, dst.Bottom) };
                if (gray || it.Gray)
                {
                    using var ia = new ImageAttributes(); ia.SetColorMatrix(GrayCm);
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(bmp, pts, src, GraphicsUnit.Pixel, ia);
                }
                else
                {
                    using var ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(bmp, pts, src, GraphicsUnit.Pixel, ia);
                }
            }
        }
        else
        {
            var c = Wf.C(it.Color, System.Drawing.Color.FromArgb(255, 243, 166));
            if (gray) { int l = (int)(c.R * 0.299 + c.G * 0.587 + c.B * 0.114); c = System.Drawing.Color.FromArgb(l, l, l); }
            using (var br = new SolidBrush(c)) g.FillRectangle(br, dst);
            using (var pen = new Pen(System.Drawing.Color.FromArgb(90, 0, 0, 0), 1 / (float)Math.Max(0.01, it.Scale))) g.DrawRectangle(pen, dst.X, dst.Y, dst.Width, dst.Height);
            if (it.FlipH) { g.ScaleTransform(-1, 1); }   // 글은 거꾸로 쓰지 않음
            using var font = new Font("Malgun Gothic", 15f, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(System.Drawing.Color.FromArgb(40, 40, 40));
            g.DrawString(string.IsNullOrEmpty(it.Text) ? "(메모 — 두 번 눌러 쓰기)" : it.Text, font, fg, new RectangleF(dst.X + 8, dst.Y + 6, dst.Width - 16, dst.Height - 12));
        }
        g.Restore(st);
    }

    /// <summary>보드 전체 그리기 (화면 크기 w×h, 바탕 포함)</summary>
    public static void DrawBoard(Graphics g, RefBoard b, int w, int h, Color? back = null)
    {
        g.Clear(back ?? System.Drawing.Color.FromArgb(0x24, 0x24, 0x28));
        g.InterpolationMode = b.Zoom < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var st = g.Save();
        using (var v = ViewMatrix(b, w)) g.MultiplyTransform(v);
        foreach (var it in b.Items) DrawItem(g, it, b.ViewGray);
        g.Restore(st);
    }

    /// <summary>보드를 그림으로 (시험·스포이트)</summary>
    public static Bitmap Render(RefBoard b, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        DrawBoard(g, b, w, h);
        return bmp;
    }

    /// <summary>화면 한 점의 색 (보이는 그대로: 흑백·반전 보기 포함)</summary>
    public static Color Sample(RefBoard b, int viewW, int viewH, Point p)
    {
        using var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TranslateTransform(-p.X, -p.Y);
            DrawBoard(g, b, viewW, viewH);
        }
        return bmp.GetPixel(0, 0);
    }

    /// <summary>항목을 그림으로 (원본 해상도: 자르기·회전·좌우 반전·흑백 적용, 보드 배율은 빼고) — 레이어로 넣을 때</summary>
    public static Bitmap RenderItem(RefItem it, double scale = 1)
    {
        var tmp = it.Copy();
        tmp.X = 0; tmp.Y = 0; tmp.Scale = scale;
        var bb = Bounds(tmp);
        int w = Math.Max(1, (int)Math.Ceiling(bb.Width - 0.01)), h = Math.Max(1, (int)Math.Ceiling(bb.Height - 0.01));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(w / 2f, h / 2f);
        DrawItem(g, tmp, false);
        return bmp;
    }

    /// <summary>항목을 문서에 새 레이어로 (cx,cy = 문서 좌표 가운데). 문서보다 크면 90% 로 줄임. 실행취소 한 번</summary>
    public static Layer ToLayer(Document d, RefItem it, double cx, double cy)
    {
        if (d == null || it == null) return null;
        using var img = RenderItem(it);
        double w = img.Width, h = img.Height;
        if (w > d.W || h > d.H) { double s = Math.Min(d.W / w, d.H / h) * 0.9; w *= s; h *= s; }
        int iw = Math.Max(1, (int)Math.Round(w)), ih = Math.Max(1, (int)Math.Round(h));
        using var scaled = iw == img.Width && ih == img.Height ? new Bitmap(img) : Img.Resize(img, iw, ih);
        int x = (int)Math.Round(cx - iw / 2.0), y = (int)Math.Round(cy - ih / 2.0);
        string name = "참고: " + (it.Kind == "image" ? (string.IsNullOrEmpty(it.Name) ? "그림" : it.Name) : "메모");
        Layer l = null;
        App.History.Struct(d, "참고 자료를 레이어로", () =>
        {
            var a = d.Active;
            l = new Layer(d, name) { Parent = a?.IsFolder == true && a.Expanded ? a.Id : a?.Parent };
            CpadIO.DrawBitmap(l.Px, scaled, x, y);
            int i = a == null ? d.Layers.Count : d.Layers.IndexOf(a) + 1;
            d.Layers.Insert(i, l); d.ActiveId = l.Id;
        });
        d.Modified = true;
        App.NotifyLayers();
        App.Invalidate(new Rect(Math.Max(0, x), Math.Max(0, y), iw, ih).Clip(d.W, d.H));
        return l;
    }

    /// <summary>자르기 바꾸기: 그림이 화면에서 제자리에 있도록 가운데도 옮김</summary>
    public static void SetCrop(RefItem it, RectangleF crop)
    {
        var bmp = RefStore.Image(it.Hash); if (bmp == null) return;
        crop = RectangleF.Intersect(crop, new RectangleF(0, 0, bmp.Width, bmp.Height));
        if (crop.Width < 2 || crop.Height < 2) return;
        var old = SourceRect(it);
        var dc = new PointF(crop.X + crop.Width / 2 - (old.X + old.Width / 2), crop.Y + crop.Height / 2 - (old.Y + old.Height / 2));
        using var m = ItemMatrix(it);
        var v = new[] { dc };
        m.TransformVectors(v);
        it.X += v[0].X; it.Y += v[0].Y;
        it.Crop = Math.Abs(crop.X) < 0.01 && Math.Abs(crop.Y) < 0.01 && Math.Abs(crop.Width - bmp.Width) < 0.01 && Math.Abs(crop.Height - bmp.Height) < 0.01 ? null : crop;
    }

    /// <summary>모든 항목이 보이게 화면 맞춤</summary>
    public static void Fit(RefBoard b, int w, int h, IEnumerable<RefItem> only = null)
    {
        var items = (only ?? b.Items).ToList();
        if (items.Count == 0 || w < 10 || h < 10) { b.Zoom = 0.5; b.PanX = 40; b.PanY = 40; return; }
        var r = items.Select(i => Bounds(i)).Aggregate(RectangleF.Union);
        double z = Math.Clamp(Math.Min((w - 40) / Math.Max(1, r.Width), (h - 40) / Math.Max(1, r.Height)), 0.02, 8);
        b.Zoom = z;
        b.PanX = w / 2.0 - (r.X + r.Width / 2) * z;
        b.PanY = h / 2.0 - (r.Y + r.Height / 2) * z;
    }

    /// <summary>골라 둔 것을 묶기 (같은 group id). 하나뿐이면 풀기</summary>
    public static string GroupItems(IEnumerable<RefItem> items)
    {
        var list = items.ToList();
        if (list.Count < 2) return null;
        string g = U.Uid("g");
        foreach (var i in list) i.Group = g;
        return g;
    }
    public static void Ungroup(IEnumerable<RefItem> items) { foreach (var i in items) i.Group = null; }
    /// <summary>묶음까지 넓힌 고르기</summary>
    public static List<RefItem> Expand(RefBoard b, IEnumerable<RefItem> sel)
    {
        var set = sel.ToHashSet();
        var groups = set.Where(i => i.Group != null).Select(i => i.Group).ToHashSet();
        return b.Items.Where(i => set.Contains(i) || (i.Group != null && groups.Contains(i.Group))).ToList();
    }

    /// <summary>파일 → 보드 항목 (그림 저장소에 넣고). 실패하면 null</summary>
    public static RefItem FromFile(string path, double x, double y)
    {
        try
        {
            using var b = Modules.Filters.ImageIO.LoadBitmap(path);
            return FromBitmap(b, Path.GetFileNameWithoutExtension(path), x, y);
        }
        catch { return null; }
    }
    public static RefItem FromBitmap(Bitmap b, string name, double x, double y)
    {
        string h = RefStore.AddImage(b);
        var img = RefStore.Image(h);
        double s = img == null ? 1 : Math.Min(1, 900.0 / Math.Max(img.Width, img.Height));   // 처음에는 긴 쪽 900 정도로
        return new RefItem { Kind = "image", Hash = h, Name = name, X = x, Y = y, Scale = s };
    }
}
