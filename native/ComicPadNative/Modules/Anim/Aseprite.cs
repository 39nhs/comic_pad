// .aseprite / .ase 읽기·쓰기 — 웹 판 app/js/core/sprite.js (공개된 파일 형식 문서 기준: 레이어·셀·연결 셀·태그·프레임 시간·팔레트)
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public static class Aseprite
{
    static readonly string[] Blend = { "source-over", "multiply", "screen", "overlay", "darken", "lighten", "color-dodge", "color-burn", "hard-light", "soft-light", "difference", "exclusion", "hue", "saturation", "color", "luminosity", "lighter", "source-over", "source-over" };

    public sealed class AseLayer { public bool Visible; public int Type, Level, Blend; public double Opacity; public string Name; }
    public sealed class AseCel { public int Layer, X, Y, Type, W, H, Link = -1; public double Opacity; public byte[] Px; }
    public sealed class AseFrame { public int Dur; public List<AseCel> Cels = new(); }
    public sealed class AseFile
    {
        public int W, H, Depth, TransIdx;
        public List<int[]> Pal = new();
        public List<AseLayer> Layers = new();
        public List<AseFrame> Frames = new();
        public JsonArray Tags = new();
    }

    static byte[] Inflate(byte[] b, int off, int len)
    {
        using var ms = new MemoryStream(b, off, len);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    /// <summary>파일에서 가져온 팔레트 (작품과 함께 저장하지 않음)</summary>
    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document, List<string>> Imported = new();
    public static List<string> PaletteOf(Document d) => d != null && Imported.TryGetValue(d, out var p) ? p : null;

    public static AseFile Read(byte[] u8)
    {
        ushort U16(int p) => BitConverter.ToUInt16(u8, p);
        short I16(int p) => BitConverter.ToInt16(u8, p);
        uint U32(int p) => BitConverter.ToUInt32(u8, p);
        (string s, int end) Str(int p) { int n = U16(p); return (Encoding.UTF8.GetString(u8, p + 2, n), p + 2 + n); }
        if (u8.Length < 128 || U16(4) != 0xA5E0) throw new InvalidDataException("Aseprite 파일이 아닙니다");
        var f = new AseFile { W = U16(8), H = U16(10), Depth = U16(12), TransIdx = u8[28] };
        int nFrames = U16(6);
        int p = 128;
        for (int fi = 0; fi < nFrames; fi++)
        {
            int fBytes = (int)U32(p);
            if (U16(p + 4) != 0xF1FA) throw new InvalidDataException("프레임 머리 이상");
            int oldN = U16(p + 6), dur = U16(p + 8), newN = (int)U32(p + 12);
            int nChunks = newN != 0 ? newN : oldN;
            var frame = new AseFrame { Dur = dur };
            int q = p + 16;
            for (int c = 0; c < nChunks; c++)
            {
                int size = (int)U32(q), type = U16(q + 4), d = q + 6;
                if (type == 0x2004)
                {
                    int flags = U16(d), lt = U16(d + 2), level = U16(d + 4), blend = U16(d + 10), op = u8[d + 12];
                    f.Layers.Add(new AseLayer { Visible = (flags & 1) != 0, Type = lt, Level = level, Blend = blend, Opacity = op / 255.0, Name = Str(d + 16).s });
                }
                else if (type == 0x2005)
                {
                    var cel = new AseCel { Layer = U16(d), X = I16(d + 2), Y = I16(d + 4), Opacity = u8[d + 6] / 255.0, Type = U16(d + 7) };
                    if (cel.Type == 1) cel.Link = U16(d + 16);
                    else if (cel.Type == 0 || cel.Type == 2)
                    {
                        cel.W = U16(d + 16); cel.H = U16(d + 18);
                        int rawAt = d + 20, rawLen = q + size - rawAt;
                        cel.Px = cel.Type == 2 ? Inflate(u8, rawAt, rawLen) : u8.AsSpan(rawAt, rawLen).ToArray();
                    }
                    frame.Cels.Add(cel);
                }
                else if (type == 0x2019)
                {
                    int first = (int)U32(d + 4), last = (int)U32(d + 8), e = d + 20;
                    for (int i = first; i <= last; i++)
                    {
                        int fl = U16(e);
                        while (f.Pal.Count <= i) f.Pal.Add(null);
                        f.Pal[i] = new int[] { u8[e + 2], u8[e + 3], u8[e + 4], u8[e + 5] };
                        e += 6;
                        if ((fl & 1) != 0) e = Str(e).end;
                    }
                }
                else if ((type == 0x0004 || type == 0x0011) && f.Pal.Count == 0)
                {
                    int packets = U16(d), e = d + 2, idx = 0;
                    var pal = new List<int[]>();
                    for (int k = 0; k < packets; k++)
                    {
                        idx += u8[e]; int cnt = u8[e + 1] == 0 ? 256 : u8[e + 1]; e += 2;
                        for (int i = 0; i < cnt; i++, idx++, e += 3)
                        {
                            while (pal.Count <= idx) pal.Add(null);
                            pal[idx] = type == 0x0011 ? new[] { u8[e] * 4, u8[e + 1] * 4, u8[e + 2] * 4, 255 } : new[] { (int)u8[e], u8[e + 1], u8[e + 2], 255 };
                        }
                    }
                    f.Pal = pal;
                }
                else if (type == 0x2018)
                {
                    int n = U16(d), e = d + 10;
                    for (int k = 0; k < n; k++)
                    {
                        int from = U16(e), to = U16(e + 2), dir = u8[e + 4];
                        string col = $"#{u8[e + 13]:x2}{u8[e + 14]:x2}{u8[e + 15]:x2}";
                        var (name, end) = Str(e + 17);
                        f.Tags.Add(new JsonObject { ["name"] = name, ["from"] = from, ["to"] = to, ["dir"] = dir == 1 ? "rev" : dir >= 2 ? "ping" : "fwd", ["color"] = col });
                        e = end;
                    }
                }
                q += size;
            }
            f.Frames.Add(frame);
            p += fBytes;
        }
        return f;
    }

    /// <summary>셀 픽셀 → 그림 (RGBA / 회색 / 팔레트)</summary>
    static Img CelImg(AseFile f, AseCel cel, bool isBg)
    {
        var im = new Img(Math.Max(1, cel.W), Math.Max(1, cel.H));
        var s = cel.Px;
        for (int i = 0; i < cel.W * cel.H; i++)
        {
            uint r, g, b, a;
            if (f.Depth == 32) { if (i * 4 + 3 >= s.Length) break; r = s[i * 4]; g = s[i * 4 + 1]; b = s[i * 4 + 2]; a = s[i * 4 + 3]; }
            else if (f.Depth == 16) { if (i * 2 + 1 >= s.Length) break; r = g = b = s[i * 2]; a = s[i * 2 + 1]; }
            else
            {
                if (i >= s.Length) break;
                int k = s[i];
                var col = k < f.Pal.Count && f.Pal[k] != null ? f.Pal[k] : new[] { 0, 0, 0, 255 };
                r = (uint)col[0]; g = (uint)col[1]; b = (uint)col[2]; a = k == f.TransIdx && !isBg ? 0 : (uint)col[3];
            }
            im.P[i] = Px.Premul(Px.Pack(a, r, g, b));
        }
        return im;
    }

    /// <summary>.aseprite → ComicPad 작품 (레이어마다 트랙, 연결 셀은 같은 셀로)</summary>
    public static Document ToDoc(byte[] buf, string name = "Aseprite")
    {
        var f = Read(buf);
        var d = new Document(Math.Max(1, f.W), Math.Max(1, f.H)) { Name = name };
        var a = d.Anim;
        var durs = f.Frames.Select(fr => fr.Dur > 0 ? fr.Dur : 100).ToList();
        int common = durs.Count == 0 ? 100 : durs.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        double fps = Math.Max(1, Math.Min(60, Math.Round(1000.0 / common)));
        a["fps"] = fps;
        if (durs.Any(x => Math.Abs(x - 1000 / fps) > 1)) a["ms"] = new JsonArray(durs.Select(x => (JsonNode)x).ToArray());
        a["length"] = f.Frames.Count;
        a["tags"] = f.Tags.DeepClone();
        a["v"] = 2;
        // 그룹 이름은 앞에 붙여서 (그룹/레이어)
        var path = new List<string>();
        var tracks = new List<(Layer t, AseLayer L, int li, Layer[] byFrame, string[] xs)>();
        var trackOf = new Dictionary<int, int>();
        for (int li = 0; li < f.Layers.Count; li++)
        {
            var L = f.Layers[li];
            while (path.Count <= L.Level) path.Add(null);
            path[L.Level] = L.Name;
            path.RemoveRange(L.Level + 1, path.Count - L.Level - 1);
            if (L.Type != 0) continue;
            var t = new Layer(d, string.Join("/", path), "folder") { Visible = L.Visible, Opacity = (float)L.Opacity, Meta = new JsonObject { ["anim"] = true } };
            d.Layers.Add(t);
            trackOf[li] = tracks.Count;
            tracks.Add((t, L, li, new Layer[f.Frames.Count], new string[f.Frames.Count]));
        }
        for (int fi = 0; fi < f.Frames.Count; fi++)
            foreach (var cel in f.Frames[fi].Cels)
            {
                if (!trackOf.TryGetValue(cel.Layer, out int ti)) continue;
                var T = tracks[ti];
                if (cel.Type == 1)
                {
                    var src = cel.Link >= 0 && cel.Link < T.byFrame.Length ? T.byFrame[cel.Link] : null;
                    T.byFrame[fi] = src; T.xs[fi] = src?.Id;
                    continue;
                }
                if (cel.Px == null) continue;
                var l = new Layer(d, (fi + 1).ToString()) { Parent = T.t.Id, Opacity = (float)cel.Opacity, Blend = T.L.Blend < Blend.Length ? Blend[T.L.Blend] : "source-over" };
                bool isBg = T.li == 0 && System.Text.RegularExpressions.Regex.IsMatch(T.L.Name ?? "", "background|배경", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                Sprite.Blit(l.Px, CelImg(f, cel, isBg), cel.X, cel.Y);
                d.Layers.Insert(d.Layers.IndexOf(T.t), l);
                T.byFrame[fi] = l; T.xs[fi] = l.Id;
            }
        foreach (var T in tracks) T.t.Meta["xs"] = J.StrArray(T.xs);
        var pal = f.Pal.Where(c => c != null).Select(c => PixelArt.ToHex(c[0], c[1], c[2])).ToList();
        if (pal.Count > 0) Imported.AddOrUpdate(d, pal);   // 저장하지 않는 값 (웹 doc.importedPalette)
        // 픽셀아트 설정(가까운 픽셀 보기·팔레트)과 이어지게
        d.Extra["pixel"] = new JsonObject { ["art"] = f.W <= 512 && f.H <= 512, ["lock"] = false, ["palette"] = pal.Count > 0 ? J.StrArray(pal.Take(256)) : null };
        var first = tracks.Count > 0 ? tracks[0] : default;
        d.ActiveId = first.xs?.FirstOrDefault(x => x != null) ?? first.t?.Id ?? d.Layers.FirstOrDefault()?.Id;
        return d;
    }

    // ---------------------------------------------------------------------
    // .aseprite 쓰기: 위쪽 레이어(폴더 밖 그림 레이어) + 트랙 → Aseprite 레이어, 같은 셀은 연결 셀로
    // ---------------------------------------------------------------------
    sealed class Buf
    {
        public readonly List<byte> A = new();
        public void U8(params int[] v) { foreach (var x in v) A.Add((byte)x); }
        public void U16(int v) { A.Add((byte)(v & 255)); A.Add((byte)((v >> 8) & 255)); }
        public void I16(int v) => U16(v & 0xffff);
        public void U32(long v) { U16((int)(v & 0xffff)); U16((int)((v >> 16) & 0xffff)); }
        public void Str(string s) { var b = Encoding.UTF8.GetBytes(s); U16(b.Length); A.AddRange(b); }
        public void Bytes(byte[] b) => A.AddRange(b);
    }
    static List<byte> Chunk(int type, Action<Buf> fill) { var b = new Buf(); fill(b); var o = new Buf(); o.U32(b.A.Count + 6); o.U16(type); o.A.AddRange(b.A); return o.A; }

    static byte[] Deflate(byte[] b)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(b);
        return ms.ToArray();
    }

    public static byte[] Write(Document d)
    {
        int W = d.W, H = d.H;
        var top = d.Layers.Where(l => d.ParentOf(l) == null && (AnimCore.IsTrack(l) || (!l.IsFolder && l.Kind != "adjust" && !l.Draft))).ToList();
        int n = Math.Max(1, AnimCore.FrameCount(d));
        var blendId = new Dictionary<string, int>();
        for (int i = Blend.Length - 1; i >= 0; i--) blendId[Blend[i]] = i;
        List<byte> ImgCel(int li, Layer layer, double opacity)
        {
            var b = layer.Px?.Bounds() ?? Rect.Empty;
            if (b.IsEmpty) b = new Rect(0, 0, 1, 1);
            var px = layer.Px?.Read(b) ?? new uint[1];
            var raw = new byte[b.W * b.H * 4];
            for (int i = 0; i < px.Length; i++) { uint u = Px.Unpremul(px[i]); raw[i * 4] = (byte)(u >> 16); raw[i * 4 + 1] = (byte)(u >> 8); raw[i * 4 + 2] = (byte)u; raw[i * 4 + 3] = (byte)(u >> 24); }
            var z = Deflate(raw);
            return Chunk(0x2005, c => { c.U16(li); c.I16(b.X); c.I16(b.Y); c.U8((int)Math.Round(opacity * 255)); c.U16(2); c.I16(0); c.U8(0, 0, 0, 0, 0); c.U16(b.W); c.U16(b.H); c.Bytes(z); });
        }
        List<byte> LinkCel(int li, int f) => Chunk(0x2005, c => { c.U16(li); c.I16(0); c.I16(0); c.U8(255); c.U16(1); c.I16(0); c.U8(0, 0, 0, 0, 0); c.U16(f); });
        var frames = new List<(List<List<byte>> chunks, int dur)>();
        var first = top.Select(_ => new Dictionary<string, int>()).ToList();   // 레이어마다 셀 id → 처음 나온 프레임
        for (int f = 0; f < n; f++)
        {
            var chunks = new List<List<byte>>();
            if (f == 0)
            {
                foreach (var l in top)
                    chunks.Add(Chunk(0x2004, b => { b.U16(l.Visible ? 1 : 0); b.U16(0); b.U16(0); b.U16(0); b.U16(0); b.U16(AnimCore.IsTrack(l) ? 0 : blendId.GetValueOrDefault(l.Blend ?? "source-over", 0)); b.U8((int)Math.Round(l.Opacity * 255)); b.U8(0, 0, 0); b.Str(string.IsNullOrEmpty(l.Name) ? "레이어" : l.Name); }));
                var tags = AnimCore.Tags(d).OfType<JsonObject>().ToList();
                if (tags.Count > 0) chunks.Add(Chunk(0x2018, b =>
                {
                    b.U16(tags.Count); b.U8(0, 0, 0, 0, 0, 0, 0, 0);
                    foreach (var t in tags)
                    {
                        var rgb = PixelArt.ParseHex(J.S(t["color"]) ?? "#3d7be0");
                        string dir = J.S(t["dir"]) ?? "fwd";
                        b.U16(J.I(t["from"])); b.U16(J.I(t["to"])); b.U8(dir == "rev" ? 1 : dir == "ping" ? 2 : 0); b.U16(0); b.U8(0, 0, 0, 0, 0, 0); b.U8(rgb[0], rgb[1], rgb[2]); b.U8(0);
                        string nm = J.S(t["name"]); b.Str(string.IsNullOrEmpty(nm) ? "tag" : nm);
                    }
                }));
            }
            for (int li = 0; li < top.Count; li++)
            {
                var l = top[li];
                var cel = AnimCore.IsTrack(l) ? AnimCore.CelAt(l, f, d) : l;
                if (cel == null) continue;
                if (first[li].TryGetValue(cel.Id, out int seen)) { chunks.Add(LinkCel(li, seen)); continue; }
                first[li][cel.Id] = f;
                chunks.Add(ImgCel(li, cel, AnimCore.IsTrack(l) ? cel.Opacity : 1));
            }
            frames.Add((chunks, (int)Math.Round(AnimCore.FrameMs(f, d))));
        }
        var body = new Buf();
        foreach (var (chunks, dur) in frames)
        {
            int len = chunks.Sum(c => c.Count);
            body.U32(16 + len); body.U16(0xF1FA); body.U16(Math.Min(0xffff, chunks.Count)); body.U16(dur); body.U8(0, 0); body.U32(chunks.Count);
            foreach (var c in chunks) body.A.AddRange(c);
        }
        var head = new Buf();
        head.U32(128 + body.A.Count); head.U16(0xA5E0); head.U16(n); head.U16(W); head.U16(H); head.U16(32); head.U32(1); head.U16(100); head.U32(0); head.U32(0);
        head.U8(0, 0, 0, 0); head.U16(0); head.U8(1, 1); head.I16(0); head.I16(0); head.U16(16); head.U16(16);
        for (int i = 0; i < 84; i++) head.U8(0);
        return head.A.Concat(body.A).ToArray();
    }

    public static string Export(Document d, string dir = null)
    {
        var bytes = Write(d);
        string path = Path.Combine(dir ?? AnimExport.ExportsDir, $"{J.SafeName(d.Name)}_{J.Stamp()}.aseprite");
        File.WriteAllBytes(path, bytes);
        App.Toast($"Aseprite 파일 저장: {path} ({bytes.Length / 1024}KB)");
        return path;
    }

    /// <summary>파일 열기 담당 (FileCommands.Openers)</summary>
    public static Document Open(string path)
    {
        var d = ToDoc(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        d.Modified = true;
        var a = d.Anim;
        int nt = AnimCore.Tracks(d).Count, nf = AnimCore.FrameCount(d), tg = AnimCore.Tags(d).Count;
        int pc = PaletteOf(d)?.Count ?? 0;
        App.Toast($"Aseprite: 트랙 {nt}개 · {nf}프레임{(tg > 0 ? $" · 태그 {tg}개" : "")}{(pc > 0 ? $" · 팔레트 {pc}색" : "")}");
        return d;
    }
}
