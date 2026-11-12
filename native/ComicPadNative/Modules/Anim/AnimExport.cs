// 애니메이션 내보내기 — 웹 판 anim.js(exportPNGs·GIF) + animexport.js(APNG)
// 프레임 순서는 태그·방향을 따름, 카메라 키가 있으면 그 영역만 확대·회전해서. 결과는 data/exports 에.
// (WebP·MP4 는 Media 모듈 — AnimDialogs 의 ExportOverride 훅으로 그쪽 창이 열림)
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public sealed class ExportOpts
{
    public string Fmt = "gif";
    public double Scale = 0.5;
    public int? Tag;
    public int Loop;                // 0 = 무한
    public bool Transparent;
    public bool Dither = true;
    public int Colors = 256;
    public int Frames;              // 미리 짐작용: 앞 몇 장만 (0 = 전부)
}

public static class AnimExport
{
    public static readonly (string id, string label)[] Formats =
    {
        ("gif", "GIF (어디서나 됨)"), ("apng", "APNG (색 그대로·투명, 큼)"), ("png", "PNG 연번 (프레임마다 파일)"),
    };

    public static string ExportsDir { get { var p = Path.Combine(App.DataDir, "exports"); Directory.CreateDirectory(p); return p; } }

    /// <summary>
    /// 프레임 그림: 크기 scale, bg = 배경색 (null 이면 투명 — 종이도 빼고), camera = 카메라 키 적용
    /// </summary>
    public static Img Frame(Document d, int f, double scale, uint? bg = 0xFFFFFFFF, bool camera = true)
    {
        var comp = new Compositor();
        var s = comp.Flatten(d, new RenderOptions { ExcludeDraft = true, Frame = f, Paper = bg != null });
        var buf = s.Read(s.Full);
        if (bg is uint b) for (int i = 0; i < buf.Length; i++) buf[i] = Px.Over(buf[i], b);
        if (camera) buf = Camera.Apply(d, f, buf, d.W, d.H);
        int w = Math.Max(1, (int)Math.Round(d.W * scale)), h = Math.Max(1, (int)Math.Round(d.H * scale));
        return new Img(w, h, Xform.Resize(buf, d.W, d.H, w, h));
    }

    public static List<(Img img, int ms)> Frames(Document d, ExportOpts o)
    {
        var order = AnimCore.ExportOrder(d, o.Tag);
        if (o.Frames > 0) order = order.Take(o.Frames).ToList();
        var cache = new Dictionary<int, Img>();
        return order.Select(f =>
        {
            if (!cache.TryGetValue(f, out var im)) cache[f] = im = Frame(d, f, o.Scale, o.Transparent ? null : 0xFFFFFFFF);
            return (im, (int)Math.Round(AnimCore.FrameMs(f, d)));
        }).ToList();
    }

    public static byte[] Encode(Document d, ExportOpts o) => o.Fmt switch
    {
        "gif" => Gif.Encode(Frames(d, o), o.Loop, o.Dither, o.Colors, o.Transparent),
        "apng" => Png.EncodeApng(Frames(d, o), o.Loop, o.Transparent),
        _ => throw new InvalidOperationException("알 수 없는 형식: " + o.Fmt),
    };

    /// <summary>크기 미리 짐작: 앞쪽 몇 장만 만들어 보고 전체 장수로 늘림</summary>
    public static long? Estimate(Document d, ExportOpts o)
    {
        var order = AnimCore.ExportOrder(d, o.Tag);
        int k = Math.Min(order.Count, 3);
        if (k == 0 || (o.Fmt != "gif" && o.Fmt != "apng")) return null;
        var oo = Clone(o); oo.Frames = k;
        var b = Encode(d, oo);
        return (long)Math.Round((double)b.Length / k * order.Count);
    }

    static ExportOpts Clone(ExportOpts o) => new() { Fmt = o.Fmt, Scale = o.Scale, Tag = o.Tag, Loop = o.Loop, Transparent = o.Transparent, Dither = o.Dither, Colors = o.Colors, Frames = o.Frames };

    /// <summary>PNG 연번: 프레임 길이(ms)가 다르면 fps 기준 장수만큼 같은 그림을 이어서 저장 (영상 편집 프로그램에서 그대로 쓰도록)</summary>
    public static (string folder, int count) ExportPngs(Document d, double scale, int? tag, string dir = null)
    {
        var order = AnimCore.ExportOrder(d, tag);
        string folder = Path.Combine(dir ?? ExportsDir, $"{J.SafeName(d.Name)}_프레임_{J.Stamp()}");
        Directory.CreateDirectory(folder);
        double bas = 1000 / AnimCore.Fps(d);
        int k = 0;
        var cache = new Dictionary<int, byte[]>();
        foreach (int f in order)
        {
            if (!cache.TryGetValue(f, out var png)) cache[f] = png = Png.Encode(Frame(d, f, scale));
            for (int r = Math.Max(1, (int)Math.Round(AnimCore.FrameMs(f, d) / bas)); r > 0; r--) File.WriteAllBytes(Path.Combine(folder, $"{++k:0000}.png"), png);
        }
        return (folder, k);
    }

    /// <summary>내보내기 (형식별) → 저장한 파일 경로</summary>
    public static string Export(Document d, ExportOpts o, string dir = null)
    {
        if (o.Fmt == "png")
        {
            var (folder, k) = ExportPngs(d, o.Scale, o.Tag, dir);
            App.Toast($"{k}장 저장: {folder}");
            return folder;
        }
        var bytes = Encode(d, o);
        string ext = o.Fmt == "gif" ? "gif" : "png";
        string path = Path.Combine(dir ?? ExportsDir, $"{J.SafeName(d.Name)}_{J.Stamp()}.{ext}");
        File.WriteAllBytes(path, bytes);
        App.Toast($"{o.Fmt.ToUpperInvariant()} 저장: {path} ({bytes.Length / 1024}KB)");
        return path;
    }
}
