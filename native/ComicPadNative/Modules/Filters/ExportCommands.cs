// 내보내기: 웹툰 분할(commands2.js file.exportSlices), 웹툰 플랫폼 규격(ui/webtoonexport.js), 레이어별 PNG(file.exportLayers)
// 저장 위치는 웹 판과 같은 data/exports (에셋은 data/assets)
using System.Drawing.Imaging;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

public static class ExportCommands
{
    static Document D => App.Doc;

    // ---------------------------------------------------------------- 웹툰 분할
    public static void Slices()
    {
        var d = D;
        double sh0 = App.Get("sliceH", 1280.0);
        var v = Ui.Ask("웹툰 분할 내보내기", new[]
        {
            Field.N("h", "한 장 높이 (px)", sh0, 16, 100000),
            Field.N("width", "가로 크기 (px, 비우면 원본)", App.Get("sliceW", (double)d.W), 0, 100000),
            Field.Pick("format", "형식", new[] { ("jpg", "JPG"), ("png", "PNG") }, "jpg"),
            Field.N("quality", "JPG 품질 (0.5~1)", 0.92, 0.5, 1, 0.01),
            Field.Info($"세로 {d.H}px → 약 {Math.Ceiling(d.H / sh0)}장. data/exports/작품명_분할 폴더에 01, 02… 순서로 저장됩니다."),
        }, "내보내기");
        if (v == null) return;
        App.Set("sliceH", v.N("h")); App.Set("sliceW", v.N("width"));
        double scale = (v.N("width") > 0 ? v.N("width") : d.W) / d.W;
        string folder = $"{ImageIO.SafeName(d.Name)}_분할_{ImageIO.Stamp()}";
        string dir = Path.Combine(ImageIO.ExportsDir, folder); Directory.CreateDirectory(dir);
        int n = 0;
        Ui.Busy(() =>
        {
            using var flat = ImageIO.FlattenBitmap(d, scale, 0xFFFFFFFF);
            int sh = (int)Math.Round(v.N("h"));
            n = (int)Math.Ceiling(flat.Height / (double)sh);
            for (int i = 0; i < n; i++)
            {
                int hh = Math.Min(sh, flat.Height - i * sh);
                using var c = flat.Clone(new Rectangle(0, i * sh, flat.Width, hh), PixelFormat.Format32bppArgb);
                var bytes = v.S("format") == "jpg" ? ImageIO.EncodeJpeg(c, v.N("quality")) : ImageIO.EncodePng(c);
                File.WriteAllBytes(Path.Combine(dir, $"{i + 1:00}.{v.S("format")}"), bytes);
            }
        });
        App.Toast($"{n}장 저장: data/exports/{folder}");
    }

    // ---------------------------------------------------------------- 웹툰 플랫폼 규격
    public sealed record Preset(string Id, string Name, int W, int H, string Fmt, double MaxMB, double TotalMB);
    // 플랫폼 규격은 바뀔 수 있음: 업로드 전에 각 플랫폼 공지로 확인 (아래는 흔히 쓰는 값)
    public static readonly Preset[] Presets =
    {
        new("canvas", "WEBTOON CANVAS (글로벌)", 800, 1280, "jpg", 2, 20),
        new("naver", "네이버 도전·베스트도전 (흔히 쓰는 값)", 690, 1600, "jpg", 2, 0),
        new("w720", "가로 720 (카카오 등 투고용으로 흔함)", 720, 1600, "jpg", 0, 0),
        new("custom", "직접 입력", 800, 1280, "jpg", 0, 0),
    };

    /// <summary>cut 근처(±range)에서 가장 "빈" 줄(색 변화가 가장 적은 줄): 컷 사이 여백에서 자르기</summary>
    public static int BestCut(Func<int, int, (int r, int g, int b)> px, int w, int cut, int range, int maxY)
    {
        int y0 = Math.Max(1, cut - range), y1 = Math.Min(maxY - 1, cut);
        if (y1 <= y0) return cut;
        int best = cut; double bestScore = double.MaxValue;
        for (int y = y1; y >= y0; y--)
        {
            double mn = 255, mx = 0;
            for (int x = 0; x < w; x += 2) { var (r, g, b) = px(x, y); double v = (r + g + b) / 3.0; if (v < mn) mn = v; if (v > mx) mx = v; }
            double score = mx - mn + (cut - y) * 0.02;
            if (score < bestScore) { bestScore = score; best = y; if (score < 1) break; }
        }
        return bestScore < 8 ? best : cut;
    }

    static byte[] Encode(Bitmap c, string fmt, long maxBytes)
    {
        if (fmt == "png") return ImageIO.EncodePng(c);
        double q = 0.92; var b = ImageIO.EncodeJpeg(c, q);
        while (maxBytes > 0 && b.Length > maxBytes && q > 0.4) { q -= 0.08; b = ImageIO.EncodeJpeg(c, q); }
        return b;
    }

    public static unsafe void Webtoon()
    {
        var d = D;
        var last = App.Settings["webtoonExport"] as System.Text.Json.Nodes.JsonObject;
        var pre = Presets.FirstOrDefault(p => p.Id == ((string)last?["preset"] ?? "canvas")) ?? Presets[0];
        string shown = pre.Id;
        double L(string k, double f) => last?[k] is System.Text.Json.Nodes.JsonNode n ? Svg.Num(n, f) : f;
        var v = Ui.Ask("웹툰 플랫폼 규격으로 내보내기", new[]
        {
            Field.Pick("preset", "규격", Presets.Select(p => (p.Id, p.Name)).ToArray(), pre.Id),
            Field.N("w", "가로폭 (px)", L("w", pre.W), 100, 20000),
            Field.N("h", "한 장 세로 (px)", L("h", pre.H), 200, 100000),
            Field.Pick("fmt", "형식", new[] { ("jpg", "JPG"), ("png", "PNG") }, (string)last?["fmt"] ?? pre.Fmt),
            Field.N("maxMB", "한 장 최대 용량 (MB, 0 = 제한 없음 · 넘으면 JPG 화질을 낮춤)", L("maxMB", pre.MaxMB), 0, 1000, 0.5),
            Field.C("smart", "되도록 컷 사이 빈 곳에서 자르기", last?["smart"] is System.Text.Json.Nodes.JsonNode sm ? (bool)sm : true),
            Field.Info("규격을 고르면 아래 값이 바뀝니다. 플랫폼 규격은 바뀔 수 있으니 올리기 전에 공지를 확인하세요. 밑그림·메모는 빠집니다."),
        }, "내보내기", vals =>
        {
            // 규격을 바꿨을 때만 값 채우기 (직접 고친 값은 그대로)
            if (vals.S("preset") == shown) return;
            shown = vals.S("preset");
            var p = Presets.FirstOrDefault(x => x.Id == shown);
            if (p != null && p.Id != "custom") { vals.SetNum["w"](p.W); vals.SetNum["h"](p.H); vals.SetNum["maxMB"](p.MaxMB); }
        });
        if (v == null) return;
        App.Set("webtoonExport", new System.Text.Json.Nodes.JsonObject { ["preset"] = v.S("preset"), ["w"] = v.N("w"), ["h"] = v.N("h"), ["fmt"] = v.S("fmt"), ["maxMB"] = v.N("maxMB"), ["smart"] = v.B("smart") });
        int W = Math.Max(100, (int)Math.Round(v.N("w"))), H = Math.Max(200, (int)Math.Round(v.N("h")));
        var preset = Presets.FirstOrDefault(p => p.Id == v.S("preset"));
        string folder = $"{ImageIO.SafeName(d.Name)}_웹툰_{W}_{ImageIO.Stamp()}";
        string dir = Path.Combine(ImageIO.ExportsDir, folder); Directory.CreateDirectory(dir);
        var sizes = new List<long>();
        long maxBytes = v.N("maxMB") > 0 ? (long)(v.N("maxMB") * 1048576) : 0;
        Ui.Busy(() =>
        {
            using var full = ImageIO.FlattenBitmap(d, W / (double)d.W, 0xFFFFFFFF);
            var bd = full.LockBits(new Rectangle(0, 0, full.Width, full.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte* p0 = (byte*)bd.Scan0; int stride = bd.Stride;
            (int, int, int) Px(int x, int y) { uint c = *(uint*)(p0 + y * stride + x * 4); return ((int)((c >> 16) & 255), (int)((c >> 8) & 255), (int)(c & 255)); }
            var cuts = new List<int>();
            int y = 0;
            while (y < full.Height)
            {
                int cut = Math.Min(full.Height, y + H);
                if (v.B("smart") && cut < full.Height) cut = BestCut(Px, full.Width, cut, (int)Math.Round(H * 0.2), full.Height);
                if (cut <= y) cut = Math.Min(full.Height, y + H);
                cuts.Add(cut); y = cut;
            }
            full.UnlockBits(bd);
            int n = 0, prev = 0;
            foreach (int cut in cuts)
            {
                using var c = full.Clone(new Rectangle(0, prev, full.Width, cut - prev), PixelFormat.Format32bppArgb);
                var bytes = Encode(c, v.S("fmt"), maxBytes);
                File.WriteAllBytes(Path.Combine(dir, $"{++n:000}.{v.S("fmt")}"), bytes);
                sizes.Add(bytes.Length);
                prev = cut;
            }
        });
        double total = sizes.Sum() / 1048576.0;
        bool over = maxBytes > 0 && sizes.Any(s => s > maxBytes);
        string totalWarn = preset != null && preset.TotalMB > 0 && total > preset.TotalMB ? $" · ⚠ 합계가 {preset.TotalMB}MB 를 넘습니다" : "";
        App.Toast($"{sizes.Count}장 ({total:0.0}MB){(over ? " · ⚠ 화질을 낮춰도 용량을 넘는 장이 있습니다" : "")}{totalWarn} → data/exports/{folder}");
    }

    // ---------------------------------------------------------------- 레이어별 PNG
    public static void LayerPngs()
    {
        var d = D;
        var v = Ui.Ask("레이어별 PNG", new[]
        {
            Field.C("trim", "빈 여백 잘라내기 (에셋용)", true),
            Field.C("hidden", "숨긴 레이어도 포함", false),
            Field.C("toAssets", "에셋 폴더에 저장 (끄면 data/exports)", false),
        }, "내보내기");
        if (v == null) return;
        string folder = $"{ImageIO.SafeName(d.Name)}_레이어_{ImageIO.Stamp()}";
        string root = v.B("toAssets") ? Path.Combine(App.DataDir, "assets") : ImageIO.ExportsDir;
        string dir = Path.Combine(root, folder); Directory.CreateDirectory(dir);
        int count = 0;
        Ui.Busy(() =>
        {
            var full = new Rect(0, 0, d.W, d.H);
            for (int i = 0; i < d.Layers.Count; i++)
            {
                var l = d.Layers[i];
                if (!l.Visible && !v.B("hidden")) continue;
                if (l.Px == null) continue;
                var px = l.Px.Read(full);
                if (l.Mask != null && l.MaskOn) { var m = l.Mask.Read(full); for (int k = 0; k < px.Length; k++) if (px[k] != 0) px[k] = Px.Scale(px[k], m[k] >> 24); }
                Rect b = full;
                if (v.B("trim")) { b = Raster.AlphaBounds(px, d.W, d.H); if (b.IsEmpty) continue; }
                var o = new uint[b.W * b.H];
                for (int y = 0; y < b.H; y++) Array.Copy(px, (y + b.Y) * d.W + b.X, o, y * b.W, b.W);
                using var bmp = ImageIO.ToBitmap(o, b.W, b.H);
                bmp.Save(Path.Combine(dir, $"{i + 1:00}_{ImageIO.SafeName(l.Name)}.png"), ImageFormat.Png);
                count++;
            }
        });
        App.Toast($"{count}개 레이어 저장: data/{(v.B("toAssets") ? "assets" : "exports")}/{folder}");
    }
}
