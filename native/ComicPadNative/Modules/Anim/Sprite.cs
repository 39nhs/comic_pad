// 스프라이트 시트 내보내기·가져오기 (Aseprite·TexturePacker 형식 JSON) — 웹 판 app/js/core/sprite.js
using System.Text.Json;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public sealed class SheetOpts
{
    public double Scale = 1;
    public string Layout = "grid";   // grid | h | v
    public int Cols;                 // 0 = 자동
    public int Pad;
    public bool Trim;
    public int? Tag;
    public uint? Bg;                 // null = 투명
    public string Json = "hash";     // hash | array
    public bool Camera;
}

public sealed class SheetFrame { public Img Img; public int? Duration; }

public static class Sprite
{
    static readonly Dictionary<string, string> Dirs = new() { ["fwd"] = "forward", ["rev"] = "reverse", ["ping"] = "pingpong" };
    static readonly Dictionary<string, string> DirsBack = new() { ["forward"] = "fwd", ["reverse"] = "rev", ["pingpong"] = "ping", ["pingpong_reverse"] = "ping" };

    /// <summary>시트 만들기 → (그림, JSON)</summary>
    public static (Img sheet, JsonObject json, int cols, int rows) Build(Document d, SheetOpts o)
    {
        var order = AnimCore.ExportOrder(d, o.Tag);
        // 같은 프레임 번호가 핑퐁으로 또 나오면 그림은 한 번만
        var uniq = order.Distinct().ToList();
        var imgs = uniq.Select(f =>
        {
            var c = o.Camera ? AnimExport.Frame(d, f, o.Scale, o.Bg, true) : AnimExport.Frame(d, f, o.Scale, o.Bg, false);   // 투명이면 종이도 빼고
            var full = new Rect(0, 0, c.W, c.H);
            var r = o.Trim ? c.OpaqueBounds() : full;
            return (f, c, r, full);
        }).ToList();
        int n = imgs.Count;
        int cw = imgs.Count > 0 ? imgs.Max(i => i.r.W) : 1, ch = imgs.Count > 0 ? imgs.Max(i => i.r.H) : 1;
        int cols = o.Layout == "h" ? n : o.Layout == "v" ? 1 : Math.Max(1, o.Cols > 0 ? o.Cols : (int)Math.Ceiling(Math.Sqrt(n)));
        cols = Math.Max(1, cols);
        int rows = Math.Max(1, (int)Math.Ceiling(n / (double)cols));
        var sheet = new Img(cols * cw + (cols + 1) * o.Pad, rows * ch + (rows + 1) * o.Pad);
        if (o.Bg is uint bg) Array.Fill(sheet.P, bg);
        string bas = J.SafeName(d.Name);
        var frames = new List<JsonObject>();
        for (int k = 0; k < imgs.Count; k++)
        {
            var im = imgs[k];
            int x = o.Pad + (k % cols) * (cw + o.Pad), y = o.Pad + (k / cols) * (ch + o.Pad);
            for (int yy = 0; yy < im.r.H; yy++)
                for (int xx = 0; xx < im.r.W; xx++)
                {
                    uint c = im.c.P[(im.r.Y + yy) * im.c.W + im.r.X + xx];
                    int di = (y + yy) * sheet.W + x + xx;
                    sheet.P[di] = Px.Over(c, sheet.P[di]);
                }
            frames.Add(new JsonObject
            {
                ["filename"] = $"{bas} {im.f}.png",
                ["frame"] = new JsonObject { ["x"] = x, ["y"] = y, ["w"] = im.r.W, ["h"] = im.r.H },
                ["rotated"] = false, ["trimmed"] = o.Trim,
                ["spriteSourceSize"] = new JsonObject { ["x"] = im.r.X, ["y"] = im.r.Y, ["w"] = im.r.W, ["h"] = im.r.H },
                ["sourceSize"] = new JsonObject { ["w"] = im.full.W, ["h"] = im.full.H },
                ["duration"] = (int)Math.Round(AnimCore.FrameMs(im.f, d)),
            });
        }
        // 태그는 시트 안 순서로 (태그 하나만 내보내면 0..끝)
        var pos = uniq.Select((f, k) => (f, k)).ToDictionary(p => p.f, p => p.k);
        var allTags = AnimCore.Tags(d).OfType<JsonObject>().ToList();
        var srcTags = o.Tag is int ti ? (ti < allTags.Count ? new List<JsonObject> { allTags[ti] } : new()) : allTags;
        var tags = new JsonArray();
        foreach (var t in srcTags)
        {
            int from = J.I(t["from"]), to = J.I(t["to"]);
            var inside = uniq.Where(f => f >= from && f <= to).ToList();
            if (inside.Count == 0) continue;
            tags.Add(new JsonObject { ["name"] = J.S(t["name"]), ["from"] = pos[inside[0]], ["to"] = pos[inside[^1]], ["direction"] = Dirs.GetValueOrDefault(J.S(t["dir"]) ?? "fwd", "forward"), ["color"] = J.S(t["color"]) });
        }
        var meta = new JsonObject
        {
            ["app"] = "ComicPad", ["version"] = "1", ["image"] = $"{bas}.png", ["format"] = "RGBA8888",
            ["size"] = new JsonObject { ["w"] = sheet.W, ["h"] = sheet.H }, ["scale"] = J.F(o.Scale, "0.########"), ["frameTags"] = tags,
            ["layers"] = new JsonArray(AnimCore.Tracks(d).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["opacity"] = (int)Math.Round(t.Opacity * 255), ["blendMode"] = "normal" }).ToArray()),
        };
        JsonObject json;
        if (o.Json == "array") json = new JsonObject { ["frames"] = new JsonArray(frames.Select(f => (JsonNode)f).ToArray()), ["meta"] = meta };
        else
        {
            var hash = new JsonObject();
            foreach (var f in frames) { string name = J.S(f["filename"]); f.Remove("filename"); hash[name] = f; }
            json = new JsonObject { ["frames"] = hash, ["meta"] = meta };
        }
        return (sheet, json, cols, rows);
    }

    public static string Export(Document d, SheetOpts o, string dir = null)
    {
        var (sheet, json, _, _) = Build(d, o);
        string bas = $"{J.SafeName(d.Name)}_시트_{J.Stamp()}";
        json["meta"]["image"] = $"{bas}.png";
        dir ??= AnimExport.ExportsDir;
        string png = Path.Combine(dir, bas + ".png");
        File.WriteAllBytes(png, Png.Encode(sheet));
        File.WriteAllText(Path.Combine(dir, bas + ".json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        App.Toast($"스프라이트 시트 저장: {png} (+ .json, {sheet.W}×{sheet.H})");
        return png;
    }

    /// <summary>시트 그림(+ 있으면 JSON) → 프레임 목록과 태그</summary>
    public static (List<SheetFrame> frames, JsonArray tags) Slice(Img img, JsonObject json = null, int cols = 1, int rows = 1, int pad = 0, int margin = 0, int fw = 0, int fh = 0, bool skipEmpty = false)
    {
        var outp = new List<SheetFrame>();
        var tags = new JsonArray();
        if (json != null)
        {
            var fr = json["frames"] is JsonArray arr ? arr.OfType<JsonObject>().ToList() : (json["frames"] as JsonObject)?.Select(kv => kv.Value as JsonObject).Where(x => x != null).ToList() ?? new();
            foreach (var f in fr)
            {
                var frame = f["frame"] as JsonObject;
                if (frame == null) continue;
                int fx = J.I(frame["x"]), fy = J.I(frame["y"]), w = J.I(frame["w"]), h = J.I(frame["h"]);
                int sw = J.I(f["sourceSize"]?["w"], w), sh = J.I(f["sourceSize"]?["h"], h), ssx = J.I(f["spriteSourceSize"]?["x"], 0), ssy = J.I(f["spriteSourceSize"]?["y"], 0);
                var c = new Img(Math.Max(1, sw), Math.Max(1, sh));
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int sx = fx + x, sy = fy + y, dx = ssx + x, dy = ssy + y;
                        if ((uint)sx < (uint)img.W && (uint)sy < (uint)img.H && (uint)dx < (uint)c.W && (uint)dy < (uint)c.H) c.P[dy * c.W + dx] = img.P[sy * img.W + sx];
                    }
                int du = J.I(f["duration"], 0);
                outp.Add(new SheetFrame { Img = c, Duration = du > 0 ? du : null });
            }
            if (json["meta"]?["frameTags"] is JsonArray ft)
                foreach (var t in ft.OfType<JsonObject>())
                    tags.Add(new JsonObject { ["name"] = J.S(t["name"]), ["from"] = J.I(t["from"]), ["to"] = J.I(t["to"]), ["dir"] = DirsBack.GetValueOrDefault(J.S(t["direction"]) ?? "", "fwd"), ["color"] = J.S(t["color"]) ?? "#3d7be0" });
        }
        else
        {
            cols = Math.Max(1, cols); rows = Math.Max(1, rows);
            if (fw <= 0) fw = (img.W - 2 * margin - (cols - 1) * pad) / cols;
            if (fh <= 0) fh = (img.H - 2 * margin - (rows - 1) * pad) / rows;
            fw = Math.Max(1, fw); fh = Math.Max(1, fh);
            for (int r = 0; r < rows; r++)
                for (int k = 0; k < cols; k++)
                {
                    var c = img.Crop(new Rect(margin + k * (fw + pad), margin + r * (fh + pad), fw, fh));
                    if (skipEmpty && c.P.All(v => (v >> 24) == 0)) continue;
                    outp.Add(new SheetFrame { Img = c });
                }
        }
        return (outp, tags);
    }

    /// <summary>프레임 그림들 → 새 작품 (트랙 하나). into 를 주면 그 작품의 새 트랙으로 (실행취소 가능)</summary>
    public static Document FramesToDoc(List<SheetFrame> frames, string name = "스프라이트", JsonArray tags = null, Document into = null, double fps = 12)
    {
        int w = frames.Max(f => f.Img.W), h = frames.Max(f => f.Img.H);
        tags ??= new JsonArray();
        if (into == null)
        {
            var d = new Document(w, h) { Name = name };
            var a = d.Anim;
            a["fps"] = fps;
            var track = new Layer(d, "애니메이션", "folder") { Meta = new JsonObject { ["anim"] = true } };
            d.Layers.Add(track);
            var xs = new List<string>();
            for (int i = 0; i < frames.Count; i++)
            {
                var l = new Layer(d, (i + 1).ToString()) { Parent = track.Id };
                Blit(l.Px, frames[i].Img);
                d.Layers.Insert(d.Layers.IndexOf(track), l);
                xs.Add(l.Id);
            }
            track.Meta["xs"] = J.StrArray(xs);
            a["length"] = frames.Count;
            if (frames.Any(f => f.Duration != null)) a["ms"] = new JsonArray(frames.Select(f => f.Duration is int v ? (JsonNode)v : null).ToArray());
            a["tags"] = tags.DeepClone();
            a["v"] = 2;
            d.ActiveId = xs.FirstOrDefault();
            return d;
        }
        // 지금 작품에 새 트랙으로
        AnimCore.Edit("스프라이트 시트 가져오기", tx =>
        {
            var track = AnimCore.NewTrackRaw(into, name, AnimCore.FrameCount(into));
            var xs = tx.Xs(track);
            for (int i = 0; i < frames.Count; i++)
            {
                var l = AnimCore.NewCel(track, into);
                Blit(l.Px, frames[i].Img);
                l.Touch();
                while (xs.Count <= i) xs.Add(null);
                xs[i] = l.Id;
            }
            if (tags.Count > 0) { var tg = J.Arr(into.Anim, "tags"); foreach (var t in tags) tg.Add(t.DeepClone()); }
        });
        return into;
    }

    public static void Blit(Surface s, Img im, int ox = 0, int oy = 0)
    {
        var r = new Rect(ox, oy, im.W, im.H).Clip(s.W, s.H);
        if (r.IsEmpty) return;
        var buf = new uint[r.W * r.H];
        for (int y = 0; y < r.H; y++) Array.Copy(im.P, (r.Y - oy + y) * im.W + (r.X - ox), buf, y * r.W, r.W);
        s.Write(r, buf);
    }
}
