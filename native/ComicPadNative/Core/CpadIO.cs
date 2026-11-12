// .cpad 읽기/쓰기 — 웹 판(app/js/core/doc.js serialize/deserialize)과 같은 형식:
// JSON { format:'comicpad', version:1, name, width, height, dpi, paper, activeId, layers:[{ ...속성, data:"data:image/png;base64,...", rect:{x,y,w,h}, mask }], ...그 밖 }
// 레이어 그림은 그림이 있는 부분만 PNG 로. 이 앱이 모르는 값은 Extra 에 보관했다가 그대로 다시 씀.
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComicPadNative.Core;

public static class CpadIO
{
    static readonly HashSet<string> LayerKeys = new() { "id", "name", "kind", "visible", "opacity", "blend", "locked", "alphaLock", "clip", "draft", "tint", "ref", "maskOn", "parent", "expanded", "rough", "tone", "fx", "meta", "data", "rect", "mask" };
    static readonly HashSet<string> DocKeys = new() { "format", "version", "name", "width", "height", "dpi", "paper", "activeId", "layers" };

    public static Document Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("빈 파일");
        var d = FromJson(root);
        d.FilePath = path;
        return d;
    }

    public static Document FromJson(JsonObject root)
    {
        if ((string)root["format"] != "comicpad") throw new InvalidDataException("ComicPad 프로젝트 파일이 아닙니다");
        var d = new Document((int)root["width"], (int)root["height"])
        {
            Name = (string)root["name"] ?? "제목 없음",
            Dpi = (int?)root["dpi"] ?? 350,
        };
        if (root["paper"] is JsonObject paper)
        {
            foreach (var (k, v) in paper)
            {
                if (k == "color") d.PaperColor = (string)v ?? "#ffffff";
                else if (k == "visible") d.PaperVisible = (bool?)v ?? true;
                else d.PaperExtra[k] = v?.DeepClone();
            }
        }
        foreach (var (k, v) in root) if (!DocKeys.Contains(k)) d.Extra[k] = v?.DeepClone();
        if (root["layers"] is JsonArray arr)
            foreach (var n in arr) if (n is JsonObject lo) d.Layers.Add(LayerFromJson(d, lo));
        string act = (string)root["activeId"];
        d.ActiveId = act != null && d.ById(act) != null ? act : d.Layers.LastOrDefault()?.Id;
        return d;
    }

    static Layer LayerFromJson(Document d, JsonObject o)
    {
        var l = new Layer
        {
            Id = (string)o["id"] ?? Layer.NewId("L"),
            Name = (string)o["name"] ?? "레이어",
            Kind = (string)o["kind"] ?? "raster",
            Visible = (bool?)o["visible"] ?? true,
            Opacity = (float?)(double?)o["opacity"] ?? 1,
            Blend = (string)o["blend"] ?? "source-over",
            Locked = (bool?)o["locked"] ?? false,
            AlphaLock = (bool?)o["alphaLock"] ?? false,
            Clip = (bool?)o["clip"] ?? false,
            Draft = (bool?)o["draft"] ?? false,
            Tint = (string)o["tint"],
            Ref = (bool?)o["ref"] ?? false,
            MaskOn = (bool?)o["maskOn"] ?? true,
            Parent = (string)o["parent"],
            Expanded = (bool?)o["expanded"] ?? true,
            Meta = o["meta"]?.DeepClone() as JsonObject,
            Tone = o["tone"]?.DeepClone() as JsonObject,
            Fx = o["fx"]?.DeepClone() as JsonObject,
        };
        // 예전 파일: 파란선+밑그림 레이어는 러프 레이어
        l.Rough = o["rough"] is JsonNode rn ? (bool)rn : l.Draft && l.Tint != null;
        foreach (var (k, v) in o) if (!LayerKeys.Contains(k)) l.Extra[k] = v?.DeepClone();
        if (!l.IsFolder && !l.IsAdjust)
        {
            l.Px = new Surface(d.W, d.H);
            if ((string)o["data"] is string data && data.Length > 0)
            {
                int rx = (int?)o["rect"]?["x"] ?? 0, ry = (int?)o["rect"]?["y"] ?? 0;
                DrawPng(l.Px, data, rx, ry);
            }
        }
        if ((string)o["mask"] is string mk && mk.Length > 0) { l.Mask = new Surface(d.W, d.H); DrawPng(l.Mask, mk, 0, 0); }
        return l;
    }

    public static void Save(Document d, string path)
    {
        var json = ToJson(d).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, true);
        d.FilePath = path; d.Modified = false;
    }

    public static JsonObject ToJson(Document d)
    {
        var paper = new JsonObject { ["color"] = d.PaperColor, ["visible"] = d.PaperVisible };
        foreach (var (k, v) in d.PaperExtra) paper[k] = v?.DeepClone();
        var root = new JsonObject
        {
            ["format"] = "comicpad", ["version"] = 1, ["name"] = d.Name, ["width"] = d.W, ["height"] = d.H, ["dpi"] = d.Dpi,
            ["paper"] = paper, ["activeId"] = d.ActiveId,
        };
        foreach (var (k, v) in d.Extra) root[k] = v?.DeepClone();
        var layers = new JsonArray();
        foreach (var l in d.Layers) layers.Add(LayerToJson(l));
        root["layers"] = layers;
        return root;
    }

    static JsonObject LayerToJson(Layer l)
    {
        var o = new JsonObject
        {
            ["id"] = l.Id, ["name"] = l.Name, ["kind"] = l.Kind, ["visible"] = l.Visible, ["opacity"] = Math.Round(l.Opacity, 4), ["blend"] = l.Blend,
            ["locked"] = l.Locked, ["alphaLock"] = l.AlphaLock, ["clip"] = l.Clip, ["draft"] = l.Draft, ["tint"] = l.Tint, ["ref"] = l.Ref,
            ["maskOn"] = l.MaskOn, ["parent"] = l.Parent, ["expanded"] = l.Expanded, ["rough"] = l.Rough,
            ["tone"] = l.Tone?.DeepClone(), ["fx"] = l.Fx?.DeepClone(), ["meta"] = l.Meta?.DeepClone(),
        };
        foreach (var (k, v) in l.Extra) o[k] = v?.DeepClone();
        o["data"] = null;
        if (l.Px != null)
        {
            var b = l.Px.Bounds();
            if (!b.IsEmpty)
            {
                o["data"] = ToPngDataUrl(l.Px, b);
                o["rect"] = new JsonObject { ["x"] = b.X, ["y"] = b.Y, ["w"] = b.W, ["h"] = b.H };
            }
        }
        if (l.Mask != null) o["mask"] = ToPngDataUrl(l.Mask, l.Mask.Full);
        return o;
    }

    // ---- PNG ↔ Surface -----------------------------------------------------------
    public static unsafe void DrawPng(Surface s, string dataUrl, int ox, int oy)
    {
        int comma = dataUrl.IndexOf(',');
        var bytes = Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl);
        using var ms = new MemoryStream(bytes);
        using var bmp = new Bitmap(ms);
        DrawBitmap(s, bmp, ox, oy);
    }

    public static unsafe void DrawBitmap(Surface s, Bitmap bmp, int ox, int oy)
    {
        var r = Rect.FromLTRB(ox, oy, ox + bmp.Width, oy + bmp.Height).Clip(s.W, s.H);
        if (r.IsEmpty) return;
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new uint[r.W * r.H];
            for (int y = 0; y < r.H; y++)
            {
                uint* row = (uint*)((byte*)bd.Scan0 + (y + r.Y - oy) * bd.Stride);
                for (int x = 0; x < r.W; x++) buf[y * r.W + x] = Px.Premul(row[x + r.X - ox]);
            }
            s.Write(r, buf);
        }
        finally { bmp.UnlockBits(bd); }
    }

    public static unsafe Bitmap ToBitmap(Surface s, Rect r, uint background = 0)
    {
        var bmp = new Bitmap(r.W, r.H, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, r.W, r.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = s.Read(r);
            for (int y = 0; y < r.H; y++)
            {
                uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride);
                for (int x = 0; x < r.W; x++) { uint c = buf[y * r.W + x]; if (background != 0) c = Px.Over(c, background); row[x] = Px.Unpremul(c); }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    public static string ToPngDataUrl(Surface s, Rect r)
    {
        using var bmp = ToBitmap(s, r);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }
}
