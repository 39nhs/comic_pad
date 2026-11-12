// PSD 내보내기 (레이어 유지, RLE 압축) — 웹 core/psd.js 와 같은 구조. 포토샵/클립스튜디오/크리타에서 열 수 있다.
//  - 레이어 이름(한글은 luni)·불투명도·합성 모드·클리핑·숨김·투명 잠금·폴더·레이어 마스크
//  - 글자 레이어는 포토샵 글자(TySh)로도, 말풍선·컷·벡터 레이어는 셰이프 레이어(SoCo+vmsk+vstk)로도 (그림도 함께)
//  - 조정 레이어는 웹 판처럼 빈 레이어로 (합성 이미지에는 반영됨)
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

public static class PsdWriter
{
    public static readonly Dictionary<string, string> Blend = new()
    {
        ["source-over"] = "norm", ["multiply"] = "mul ", ["screen"] = "scrn", ["overlay"] = "over", ["darken"] = "dark", ["lighten"] = "lite",
        ["color-dodge"] = "div ", ["color-burn"] = "idiv", ["hard-light"] = "hLit", ["soft-light"] = "sLit", ["difference"] = "diff",
        ["hue"] = "hue ", ["saturation"] = "sat ", ["color"] = "colr", ["luminosity"] = "lum ", ["lighter"] = "lddg",
    };

    /// <summary>그림 원본: 미리 곱한 픽셀 (문서 크기면 Surface, 작은 조각이면 배열 + 위치)</summary>
    sealed class Src
    {
        public Surface S; public uint[] Arr; public int AW, AH, OX, OY;
        public Rect Bounds()
        {
            if (S != null) return S.Bounds();
            var b = Raster.AlphaBounds(Arr, AW, AH);
            return b.IsEmpty ? b : new Rect(b.X + OX, b.Y + OY, b.W, b.H);
        }
        public uint[] Read(Rect r)
        {
            if (S != null) return S.Read(r);
            var o = new uint[r.W * r.H];
            for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++)
            {
                int sx = r.X + x - OX, sy = r.Y + y - OY;
                if (sx >= 0 && sy >= 0 && sx < AW && sy < AH) o[y * r.W + x] = Arr[sy * AW + sx];
            }
            return o;
        }
    }

    sealed class Spec
    {
        public string Name; public Src Canvas; public bool Divider, Folder, Expanded;
        public string BlendMode = "source-over"; public float Opacity = 1; public bool Clip, Visible = true, AlphaLock;
        public Surface Mask; public bool MaskOn = true;
        public JsonObject Text; public List<byte[]> Blocks;
    }

    // ---- RLE ----
    static byte[] PackBits(byte[] src, int n)
    {
        var o = new byte[n + (n + 127) / 128 + 2];
        int op = 0, i = 0;
        while (i < n)
        {
            int j = i + 1;
            while (j < n && j - i < 128 && src[j] == src[i]) j++;
            if (j - i >= 3) { o[op++] = (byte)((257 - (j - i)) & 255); o[op++] = src[i]; i = j; }
            else
            {
                int k = i;
                while (k < n && k - i < 128 && !(k + 2 < n && src[k] == src[k + 1] && src[k] == src[k + 2])) k++;
                o[op++] = (byte)(k - i - 1);
                for (int m = i; m < k; m++) o[op++] = src[m];
                i = k;
            }
        }
        return o.AsSpan(0, op).ToArray();
    }

    sealed class Chan { public int Id; public Be Counts = new(); public List<byte[]> Body = new(); public long Length; }

    /// <summary>RGBA 바이트(미리 곱하지 않음)의 한 채널(ch 0~3) RLE</summary>
    static Chan RleChannel(byte[] rgba, int w, int h, int ch, int id)
    {
        var c = new Chan { Id = id };
        var row = new byte[w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0, p = y * w * 4 + ch; x < w; x++, p += 4) row[x] = rgba[p];
            var packed = PackBits(row, w);
            c.Counts.U16(packed.Length);
            c.Body.Add(packed);
        }
        c.Length = c.Counts.Length + c.Body.Sum(b => (long)b.Length);
        return c;
    }
    static Chan Empty(int id) => new() { Id = id };

    static void PascalName(Be w, string name)
    {
        var ascii = new string(name.Select(c => c < 128 ? c : '_').ToArray());
        if (ascii.Length > 255) ascii = ascii[..255];
        int len = 1 + ascii.Length, pad = (4 - len % 4) % 4;
        w.U8(ascii.Length); w.Ascii(ascii);
        for (int i = 0; i < pad; i++) w.U8(0);
    }
    static void UnicodeName(Be w, string name)
    {
        w.Ascii("8BIM"); w.Ascii("luni");
        var units = name.Length > 255 ? name[..255] : name;
        int len = 4 + units.Length * 2, pad = len % 4 != 0 ? 4 - len % 4 : 0;
        w.U32(len + pad); w.U32(units.Length);
        foreach (char u in units) w.U16(u);
        for (int i = 0; i < pad; i++) w.U8(0);
    }

    // ---- 셰이프 레이어 ----
    sealed class ShapeOut { public string Name; public Src Canvas; public List<byte[]> Blocks; }

    static byte[] AlphaPlane(uint[] px) { var v = new byte[px.Length]; for (int i = 0; i < px.Length; i++) v[i] = (byte)(px[i] >> 24); return v; }

    /// <summary>GDI+ 로 모양 칠하기 (알파 평면, box 영역)</summary>
    static unsafe uint[] Rasterize(Rect box, Action<Graphics> draw)
    {
        using var bmp = new Bitmap(box.W, box.H, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(-box.X, -box.Y);
            draw(g);
        }
        var o = new uint[box.W * box.H];
        var bd = bmp.LockBits(new Rectangle(0, 0, box.W, box.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        for (int y = 0; y < box.H; y++) { uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride); for (int x = 0; x < box.W; x++) o[y * box.W + x] = row[x]; }
        bmp.UnlockBits(bd);
        return o;
    }

    static Rect? UnitBox(List<JsonObject> g, int W, int H)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var b in g)
        {
            double bw = Svg.Num(b, "w"), bh = Svg.Num(b, "h"), bx = Svg.Num(b, "x"), by = Svg.Num(b, "y");
            double pad = Svg.Num(b, "line") * 2 + Math.Max(bw, bh) * 0.25 + 4;
            var xs = new List<double> { bx - pad, bx + bw + pad }; var ys = new List<double> { by - pad, by + bh + pad };
            if (b["tail"] is JsonObject t) { xs.Add(Svg.Num(t, "x") - pad); xs.Add(Svg.Num(t, "x") + pad); ys.Add(Svg.Num(t, "y") - pad); ys.Add(Svg.Num(t, "y") + pad); }
            x0 = Math.Min(x0, xs.Min()); y0 = Math.Min(y0, ys.Min()); x1 = Math.Max(x1, xs.Max()); y1 = Math.Max(y1, ys.Max());
        }
        int X0 = Math.Max(0, (int)Math.Floor(x0)), Y0 = Math.Max(0, (int)Math.Floor(y0)), X1 = Math.Min(W, (int)Math.Ceiling(x1)), Y1 = Math.Min(H, (int)Math.Ceiling(y1));
        return X1 > X0 && Y1 > Y0 ? new Rect(X0, Y0, X1 - X0, Y1 - Y0) : null;
    }

    /// <summary>말풍선 묶음 그리기 (같은 group 은 선을 먼저 모두 그리고 칠을 덮음)</summary>
    static void DrawBubbleSet(Graphics g, List<JsonObject> grp)
    {
        var paths = grp.Select(b => (b, p: PsdVector.BubblePaths(b))).ToList();
        foreach (var (b, p) in paths)
        {
            double line = Svg.Num(b, "line");
            if (line <= 0) continue;
            using var pen = new Pen(PsdVector.CssColor((string)b["stroke"] ?? "#000000"), (float)(line * 2)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(pen, p.body); if (p.tail != null) g.DrawPath(pen, p.tail);
        }
        foreach (var (b, p) in paths)
        {
            using var br = new SolidBrush(PsdVector.CssColor((string)b["fill"] ?? "#ffffff"));
            g.FillPath(br, p.body); if (p.tail != null) g.FillPath(br, p.tail);
        }
        foreach (var (_, p) in paths) { p.body.Dispose(); p.tail?.Dispose(); }
    }

    static List<ShapeOut> ShapeSpecs(Layer l, Document doc)
    {
        int W = doc.W, H = doc.H;
        var m = l.Meta;
        if (l.Kind == "panel" && m?["panels"] is JsonArray panels && panels.Count > 0)
        {
            // 컷: 문서 사각형에서 컷들을 뺀 모양 = "컷 바깥 용지색" 칠, 선은 바깥쪽(= 컷 안쪽)으로
            var loops = new List<Loop> { new() { Pts = new double[] { 0, 0, W, 0, W, H, 0, H }, Hole = false } };
            foreach (var pn in panels.OfType<JsonObject>())
            {
                double[] q;
                if (pn["pts"] is JsonArray pa && pa.Count > 0) q = pa.OfType<JsonObject>().SelectMany(v => new[] { Svg.Num(v, "x"), Svg.Num(v, "y") }).ToArray();
                else { double x = Svg.Num(pn, "x"), y = Svg.Num(pn, "y"), w = Svg.Num(pn, "w"), h = Svg.Num(pn, "h"); q = new[] { x, y, x + w, y, x + w, y + h, x, y + h }; }
                loops.Add(new Loop { Pts = q, Hole = true });
            }
            return new()
            {
                new ShapeOut
                {
                    Name = l.Name, Canvas = new Src { S = l.Px },
                    Blocks = new()
                    {
                        PsdVector.SocoBlock(doc.PaperColor ?? "#ffffff"), PsdVector.VmskBlock(loops, W, H),
                        PsdVector.VstkBlock(fill: (bool?)m["maskOutside"] != false, stroke: true, width: Svg.Num(m, "line"), color: (string)m["color"] ?? "#000000", align: "outside", join: "miter"),
                    },
                },
            };
        }
        if (l.Kind == "bubble" && m?["bubbles"] is JsonArray bubbles && bubbles.Count > 0)
        {
            var list = bubbles.OfType<JsonObject>().ToList();
            var units = new List<List<JsonObject>>(); var seen = new HashSet<JsonObject>();
            foreach (var b in list)
            {
                if (seen.Contains(b)) continue;
                var gid = b["group"]?.ToJsonString();
                var g = gid != null && gid != "null" ? list.Where(x => x["group"]?.ToJsonString() == gid).ToList() : new List<JsonObject> { b };
                foreach (var x in g) seen.Add(x);
                units.Add(g);
            }
            var outp = new List<ShapeOut>();
            for (int ui = 0; ui < units.Count; ui++)
            {
                var g = units[ui]; var st = g[0];
                var box = UnitBox(g, W, H);
                if (box == null) continue;
                // 칠 모양(테두리 제외)의 윤곽 → 경로, 선은 바깥쪽으로 테두리 굵기만큼
                var mask = Rasterize(box.Value, gr =>
                {
                    foreach (var b in g) { var (body, tail) = PsdVector.BubblePaths(b); gr.FillPath(Brushes.Black, body); if (tail != null) gr.FillPath(Brushes.Black, tail); body.Dispose(); tail?.Dispose(); }
                });
                var lb = Raster.AlphaBounds(mask, box.Value.W, box.Value.H);
                if (lb.IsEmpty) continue;
                var sub = new byte[lb.W * lb.H];
                for (int y = 0; y < lb.H; y++) for (int x = 0; x < lb.W; x++) sub[y * lb.W + x] = (byte)(mask[(y + lb.Y) * box.Value.W + x + lb.X] >> 24);
                var loops = PsdVector.ShapeLoops(sub, lb.W, lb.H, lb.X + box.Value.X, lb.Y + box.Value.Y);
                if (loops.Count == 0) continue;
                Src canvas = new() { S = l.Px };
                if (units.Count > 1)
                {
                    var bx = box.Value;
                    var px = Rasterize(bx, gr => DrawBubbleSet(gr, g));
                    canvas = new Src { Arr = px, AW = bx.W, AH = bx.H, OX = bx.X, OY = bx.Y };
                }
                string fill = (string)st["fill"] ?? "#ffffff";
                double line = Svg.Num(st, "line");
                outp.Add(new ShapeOut
                {
                    Name = units.Count == 1 ? l.Name : $"{l.Name} {ui + 1}", Canvas = canvas,
                    Blocks = new() { PsdVector.SocoBlock(fill), PsdVector.VmskBlock(loops, W, H), PsdVector.VstkBlock(fill: PsdVector.CssRGB(fill).a > 0, stroke: line > 0, width: line, color: (string)st["stroke"] ?? "#000000", align: "outside", join: "round") },
                });
            }
            return outp.Count > 0 ? outp : null;
        }
        if (l.Kind == "vector" && m?["strokes"] is JsonArray strokes && strokes.Count > 0 && l.Px != null)
        {
            // 색마다 셰이프 레이어 하나: 그 색 선들이 칠한 모양의 윤곽
            var b = l.Px.Bounds();
            if (b.IsEmpty) return null;
            var colors = strokes.OfType<JsonObject>().Select(s => (string)s["color"] ?? "#000000").Distinct().ToList();
            var rgbs = colors.Select(PsdVector.CssRGB).ToList();
            int Nearest(int r, int g, int bb)
            {
                int best = 0; double bd = double.MaxValue;
                for (int i = 0; i < rgbs.Count; i++) { double dd = Math.Pow(rgbs[i].r - r, 2) + Math.Pow(rgbs[i].g - g, 2) + Math.Pow(rgbs[i].b - bb, 2); if (dd < bd) { bd = dd; best = i; } }
                return best;
            }
            var px = l.Px.Read(b);
            var outp = new List<ShapeOut>();
            for (int ci = 0; ci < colors.Count; ci++)
            {
                var V = new byte[px.Length];
                var keepPx = colors.Count > 1 ? new uint[px.Length] : null;
                for (int i = 0; i < px.Length; i++)
                {
                    Raster.Unpack(px[i], out int r, out int g, out int bb, out int a);
                    bool keep = colors.Count == 1 || (a > 0 && Nearest(r, g, bb) == ci);
                    if (keep) { V[i] = (byte)a; if (keepPx != null) keepPx[i] = px[i]; }
                }
                var loops = PsdVector.ShapeLoops(V, b.W, b.H, b.X, b.Y);
                if (loops.Count == 0) continue;
                var canvas = colors.Count > 1 ? new Src { Arr = keepPx, AW = b.W, AH = b.H, OX = b.X, OY = b.Y } : new Src { S = l.Px };
                outp.Add(new ShapeOut { Name = colors.Count == 1 ? l.Name : $"{l.Name} ({colors[ci]})", Canvas = canvas, Blocks = new() { PsdVector.SocoBlock(colors[ci]), PsdVector.VmskBlock(loops, W, H) } });
            }
            return outp.Count > 0 ? outp : null;
        }
        return null;
    }

    /// <summary>문서 → PSD 바이트. flatten: 합성 이미지(문서 크기, 불투명). vector=false 면 셰이프 없이 그림만</summary>
    public static byte[] Write(Document doc, Surface flatten, bool vector = true, Action<double> progress = null)
    {
        int W = doc.W, H = doc.H;
        var specs = new List<Spec>();
        void Props(Spec s, Layer l) { s.BlendMode = l.Blend; s.Opacity = l.Opacity; s.Clip = l.Clip; s.Visible = l.Visible; s.AlphaLock = l.AlphaLock; }
        // 폴더는 PSD 규칙대로: [그룹 끝 표시] 자식들… [폴더] (아래→위)
        void Walk(Layer folder)
        {
            foreach (var l in doc.ChildrenOf(folder))
            {
                if (l.IsFolder)
                {
                    specs.Add(new Spec { Divider = true });
                    Walk(l);
                    var f = new Spec { Folder = true, Name = l.Name, Expanded = l.Expanded }; Props(f, l); specs.Add(f);
                    continue;
                }
                List<ShapeOut> shapes = null;
                if (vector) { try { shapes = ShapeSpecs(l, doc); } catch { shapes = null; } }
                if (shapes?.Count == 1)
                {
                    var s = new Spec { Name = l.Name, Canvas = shapes[0].Canvas, Mask = l.Mask, MaskOn = l.MaskOn, Blocks = shapes[0].Blocks }; Props(s, l); specs.Add(s);
                }
                else if (shapes?.Count > 1)
                {
                    specs.Add(new Spec { Divider = true });
                    foreach (var sh in shapes) specs.Add(new Spec { Name = sh.Name, Canvas = sh.Canvas, Blocks = sh.Blocks });
                    var f = new Spec { Folder = true, Name = l.Name, Expanded = false }; Props(f, l); specs.Add(f);
                }
                else
                {
                    var text = l.Kind == "text" && l.Meta?["text"] is JsonNode && (bool?)l.Meta["sfx"] != true ? l.Meta : null;
                    var s = new Spec { Name = l.Name, Canvas = l.Px != null ? new Src { S = l.Px } : null, Mask = l.Mask, MaskOn = l.MaskOn, Text = text }; Props(s, l); specs.Add(s);
                }
            }
        }
        Walk(null);

        var records = new Be(); var channelData = new Be();
        for (int idx = 0; idx < specs.Count; idx++)
        {
            var e = specs[idx];
            progress?.Invoke(idx / (double)specs.Count);
            List<Chan> chans;
            var b = new Rect(0, 0, 0, 0);
            if (e.Divider || e.Folder) chans = new() { Empty(-1), Empty(0), Empty(1), Empty(2) };
            else
            {
                var lb = e.Canvas?.Bounds() ?? Rect.Empty;
                if (!lb.IsEmpty)
                {
                    b = lb;
                    var rgba = FilterOps.ToBytes(e.Canvas.Read(lb));
                    chans = new() { RleChannel(rgba, b.W, b.H, 3, -1), RleChannel(rgba, b.W, b.H, 0, 0), RleChannel(rgba, b.W, b.H, 1, 1), RleChannel(rgba, b.W, b.H, 2, 2) };
                }
                else chans = new() { Empty(-1), Empty(0), Empty(1), Empty(2) };
                // 레이어 마스크: 문서 전체 크기의 회색 채널 (흰색 = 보임)
                if (e.Mask != null)
                {
                    var md = e.Mask.Read(new Rect(0, 0, W, H));
                    var rgba = new byte[md.Length * 4];
                    for (int i = 0; i < md.Length; i++) rgba[i * 4 + 3] = (byte)(md[i] >> 24);
                    chans.Add(RleChannel(rgba, W, H, 3, -2));
                }
            }
            records.I32(b.Y); records.I32(b.X); records.I32(b.Y + b.H); records.I32(b.X + b.W);
            records.U16(chans.Count);
            foreach (var c in chans) { records.I16(c.Id); records.U32(c.Length + 2); }
            records.Ascii("8BIM");
            records.Ascii(e.Divider ? "norm" : Blend.GetValueOrDefault(e.BlendMode ?? "source-over", "norm"));
            records.U8(e.Divider ? 255 : (int)Math.Round(e.Opacity * 255, MidpointRounding.AwayFromZero));
            records.U8(!e.Divider && !e.Folder && e.Clip ? 1 : 0);
            records.U8(e.Divider ? 0 : ((e.AlphaLock ? 1 : 0) | (!e.Visible ? 2 : 0)));
            records.U8(0);
            var extra = new Be();
            if (!e.Divider && !e.Folder && e.Mask != null)
            {
                extra.U32(20);
                extra.I32(0); extra.I32(0); extra.I32(H); extra.I32(W);
                extra.U8(0);                          // 사각형 밖 기본값: 가림
                extra.U8(e.MaskOn ? 0 : 2);           // 2 = 마스크 꺼짐
                extra.U16(0);
            }
            else extra.U32(0);
            extra.U32(0);   // 블렌딩 범위 없음
            string name = e.Divider ? "</Layer group>" : e.Name ?? "";
            PascalName(extra, name);
            UnicodeName(extra, name);
            if (e.Divider || e.Folder) { extra.Ascii("8BIM"); extra.Ascii("lsct"); extra.U32(4); extra.U32(e.Divider ? 3 : (e.Expanded ? 1 : 2)); }
            if (e.Blocks != null) foreach (var blk in e.Blocks) extra.Bytes(blk);
            if (e.Text != null) { try { extra.Bytes(PsdText.TyshBlock(e.Text, b)); } catch { /* 글자 저장 실패 → 그림만 */ } }
            records.U32(extra.Length);
            records.Writer(extra);
            foreach (var c in chans)
            {
                channelData.U16(c.Length > 0 ? 1 : 0);
                if (c.Length > 0) { channelData.Writer(c.Counts); foreach (var p in c.Body) channelData.Bytes(p); }
            }
        }

        var layerInfo = new Be();
        layerInfo.I16(specs.Count);
        layerInfo.Writer(records);
        layerInfo.Writer(channelData);
        if (layerInfo.Length % 2 != 0) layerInfo.U8(0);

        var outp = new Be();
        outp.Ascii("8BPS"); outp.U16(1); outp.Bytes(new byte[6]);
        outp.U16(3); outp.U32(H); outp.U32(W); outp.U16(8); outp.U16(3);
        outp.U32(0);   // color mode data
        outp.U32(0);   // image resources
        outp.U32(4 + layerInfo.Length + 4);
        outp.U32(layerInfo.Length);
        outp.Writer(layerInfo);
        outp.U32(0);   // global layer mask

        // 합성 이미지 (RGB, RLE)
        var comp = FilterOps.ToBytes(flatten.Read(new Rect(0, 0, W, H)));
        var rgb = new[] { RleChannel(comp, W, H, 0, 0), RleChannel(comp, W, H, 1, 1), RleChannel(comp, W, H, 2, 2) };
        outp.U16(1);
        foreach (var c in rgb) outp.Writer(c.Counts);
        foreach (var c in rgb) foreach (var p in c.Body) outp.Bytes(p);
        progress?.Invoke(1);
        return outp.ToArray();
    }
}

/// <summary>"파일/PSD로 저장" 명령</summary>
public static class PsdCommands
{
    public static void SavePsd()
    {
        var d = App.Doc; if (d == null) return;
        using var dlg = new SaveFileDialog { Title = "PSD로 저장 (레이어 유지)", Filter = "Photoshop (*.psd)|*.psd", InitialDirectory = ImageIO.ExportsDir, FileName = $"{ImageIO.SafeName(d.Name)}_{ImageIO.Stamp()}.psd" };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        App.Toast("PSD 만드는 중…");
        try
        {
            long size = Ui.Busy(() =>
            {
                var flat = App.Comp.Flatten(d, new RenderOptions { ExcludeDraft = false, Paper = true });
                // 흰 바탕 위에 (웹: background '#ffffff')
                for (int ti = 0; ti < flat.TileCount; ti++)
                {
                    var r = flat.TileRect(ti);
                    var px = flat.Read(r);
                    for (int i = 0; i < px.Length; i++) px[i] = Px.Over(px[i], 0xFFFFFFFF);
                    flat.Write(r, px);
                }
                var bytes = PsdWriter.Write(d, flat);
                File.WriteAllBytes(dlg.FileName, bytes);
                return bytes.LongLength;
            });
            App.Toast($"PSD 저장: {dlg.FileName} ({size / 1048576.0:0.0}MB) — 레이어 이름·불투명도·합성 모드·클리핑·숨김·폴더·레이어 마스크 유지");
        }
        catch (Exception ex) { MessageBox.Show(App.Main, "PSD 로 저장하지 못했습니다: " + ex.Message, "ComicPad"); }
    }
}
