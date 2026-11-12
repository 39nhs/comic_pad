// 애니메이션 내보내기 — 웹 판 animexport.js(움직이는 WebP·MP4) + ui/anim.js 의 내보내기 창.
// GIF·APNG·PNG 연번은 Anim 모듈(AnimExport)을 그대로 쓰고, 여기서는 WebP·MP4 를 더해 한 창에서 고른다.
//  - WebP: 직접 짠 무손실 인코더 (화질 < 최고면 거의 무손실)
//  - MP4: Media Foundation H.264, 소리(WAV)가 붙어 있고 구간을 고르지 않았으면 AAC 로 함께 (웹 판처럼)
//  - 짧은 애니(2초 미만)는 동영상에서 2초 이상 되도록 반복 (웹 판 exportWebM 과 같음)
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Anim;
using AUi = ComicPadNative.Modules.Anim.Ui;

namespace ComicPadNative.Modules.Media;

public static class AnimMedia
{
    public static readonly (string id, string label)[] Formats =
    {
        ("gif", "GIF (어디서나 됨)"), ("webp", "WebP (작고 선명, 투명 가능)"), ("apng", "APNG (색 그대로·투명, 큼)"),
        ("mp4", "MP4 동영상 (SNS 용)"), ("png", "PNG 연번 (프레임마다 파일)"),
    };
    static readonly Dictionary<string, string> Tips = new()
    {
        ["gif"] = " · 커뮤니티·메신저 어디나", ["webp"] = " · GIF 보다 훨씬 작고 색이 정확", ["apng"] = " · 색 손실 없음", ["mp4"] = " · 인스타·카톡 등 동영상만 올리는 곳용",
    };
    static bool Still(string f) => f is "gif" or "webp" or "apng";

    // ------------------------------------------------------------------ WebP
    public static List<(Argb img, int ms)> FramesArgb(Document d, ExportOpts o) =>
        AnimExport.Frames(d, o).Select(f => (Argb.FromPremul(f.img.P, f.img.W, f.img.H), f.ms)).ToList();

    public static byte[] EncodeWebP(Document d, ExportOpts o, double quality) => WebP.EncodeAnimated(FramesArgb(d, o), o.Loop, o.Transparent, quality);

    public static long? Estimate(Document d, ExportOpts o, double quality)
    {
        if (o.Fmt != "webp") return AnimExport.Estimate(d, o);
        var order = AnimCore.ExportOrder(d, o.Tag);
        int k = Math.Min(order.Count, 3);
        if (k == 0) return null;
        var oo = new ExportOpts { Fmt = o.Fmt, Scale = o.Scale, Tag = o.Tag, Loop = o.Loop, Transparent = o.Transparent, Frames = k };
        return (long)Math.Round((double)EncodeWebP(d, oo, quality).Length / k * order.Count);
    }

    // ------------------------------------------------------------------ MP4
    /// <summary>동영상 한 장: 흰 바탕, 카메라 적용, 지정 크기</summary>
    static uint[] VideoFrame(Document d, int f, int w, int h)
    {
        var s = new Compositor().Flatten(d, new RenderOptions { ExcludeDraft = true, Frame = f, Paper = true });
        var buf = s.Read(s.Full);
        for (int i = 0; i < buf.Length; i++) buf[i] = Px.Over(buf[i], 0xFFFFFFFF);
        buf = Camera.Apply(d, f, buf, d.W, d.H);
        return w == d.W && h == d.H ? buf : Xform.Resize(buf, d.W, d.H, w, h);
    }

    /// <summary>붙은 소리 → 동영상 길이(sec)만큼의 16비트 PCM (시작 위치·음량 반영). WAV 가 아니면 null</summary>
    public static PcmAudio AudioFor(Document d, double sec)
    {
        var a = Sound.Audio(d);
        var pcm = Sound.Decode(d);
        if (a == null || pcm == null) return null;
        double offset = J.D(a["offset"], 0), vol = J.N(a["volume"]) ?? 1;
        int rate = pcm.Rate == 44100 || pcm.Rate == 48000 ? pcm.Rate : 48000;   // AAC 인코더가 받는 빠르기
        int ch = Math.Min(2, Math.Max(1, pcm.Channels));
        int n = (int)Math.Ceiling(sec * rate);
        var o = new PcmAudio { Rate = rate, Channels = ch, Data = new short[n * ch] };
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate - offset;
            if (t < 0) continue;
            double si = t * pcm.Rate;
            int i0 = (int)si;
            if (i0 >= pcm.Mono.Length) break;
            double fr = si - i0;
            int i1 = Math.Min(pcm.Mono.Length - 1, i0 + 1);
            for (int c = 0; c < ch; c++)
            {
                // 채널이 많으면 남는 채널은 섞어 넣음 (5.1 → 2)
                double v = 0; int cnt = 0;
                for (int k = c; k < pcm.Channels; k += ch) { var src = pcm.Ch[k]; v += src[i0] + (src[i1] - src[i0]) * fr; cnt++; }
                v = cnt > 0 ? v / cnt : 0;
                o.Data[i * ch + c] = (short)Math.Clamp((int)Math.Round(v * vol * 32767), -32768, 32767);
            }
        }
        return o;
    }

    /// <summary>fps → 분자/분모 (정수면 그대로)</summary>
    public static (int num, int den) Rate(double fps)
    {
        if (Math.Abs(fps - Math.Round(fps)) < 1e-6) return ((int)Math.Round(fps), 1);
        return ((int)Math.Round(fps * 1000), 1000);
    }

    /// <summary>MP4 만들기 → 저장 경로. 프레임 길이가 다르면 fps 기준 장수만큼 같은 그림을 이어서 (PNG 연번과 같은 규칙)</summary>
    public static string ExportMp4(Document d, double scale, int? tag, string outPath = null, bool withAudio = true, Action<double> progress = null)
    {
        var order = AnimCore.ExportOrder(d, tag);
        if (order.Count == 0) throw new InvalidOperationException("프레임이 없습니다");
        double fps = AnimCore.Fps(d), bas = 1000 / fps;
        var reps = order.Select(f => Math.Max(1, (int)Math.Round(AnimCore.FrameMs(f, d) / bas))).ToList();
        double total = reps.Sum() * bas;
        PcmAudio audio = null;
        if (withAudio && tag == null && Sound.HasAudio(d))
        {
            audio = AudioFor(d, total / 1000);
            if (audio == null) App.Toast("붙인 소리가 WAV 가 아니라 동영상에 넣지 못했습니다 (네이티브 판은 WAV 만 읽음)");
        }
        int loops = audio != null ? 1 : Math.Max(1, (int)Math.Ceiling(2000 / Math.Max(1, total)));   // 짧으면 2초 이상 되도록 반복
        if (audio != null) audio = AudioFor(d, total * loops / 1000);
        var (w, h) = Mp4Writer.FitSize(Math.Round(d.W * scale / 2) * 2, Math.Round(d.H * scale / 2) * 2);
        outPath ??= Path.Combine(AnimExport.ExportsDir, $"{J.SafeName(d.Name)}_{J.Stamp()}.mp4");
        var (num, den) = Rate(fps);
        int bitrate = (int)Math.Clamp(w * (long)h * fps * 0.25, 2_000_000, 16_000_000);
        var cache = new Dictionary<int, uint[]>();
        using var mp = new Mp4Writer(outPath, w, h, num, den, bitrate, audio);
        int all = reps.Sum() * loops, done = 0;
        for (int k = 0; k < loops; k++)
            for (int i = 0; i < order.Count; i++)
            {
                int f = order[i];
                if (!cache.TryGetValue(f, out var px)) { if (cache.Count > 64) cache.Clear(); cache[f] = px = VideoFrame(d, f, w, h); }
                for (int r = 0; r < reps[i]; r++) { mp.AddFrame(px); progress?.Invoke(++done / (double)all); }
            }
        mp.Finish();
        return outPath;
    }

    // ------------------------------------------------------------------ 내보내기 (형식별)
    public static string Export(Document d, ExportOpts o, double quality)
    {
        switch (o.Fmt)
        {
            case "webp":
            {
                var bytes = EncodeWebP(d, o, quality);
                string path = Path.Combine(AnimExport.ExportsDir, $"{J.SafeName(d.Name)}_{J.Stamp()}.webp");
                File.WriteAllBytes(path, bytes);
                App.Toast($"WEBP 저장: {path} ({bytes.Length / 1024}KB)");
                return path;
            }
            case "mp4":
            {
                string path = ExportMp4(d, o.Scale, o.Tag);
                App.Toast($"동영상 저장: {path} ({new FileInfo(path).Length / 1024}KB)");
                return path;
            }
            default: return AnimExport.Export(d, o);
        }
    }

    // ------------------------------------------------------------------ 창
    static (ExportOpts o, double quality) LastOpts(Document d)
    {
        var s = App.Settings["animExport"] as JsonObject;
        var o = new ExportOpts
        {
            Fmt = J.S(s?["fmt"]) is string f && Formats.Any(x => x.id == f) ? f : "gif",
            Scale = J.N(s?["scale"]) ?? (d.W <= 256 ? 4 : 0.5),
            Loop = J.I(s?["loop"], 0), Transparent = J.T(s?["transparent"]), Dither = J.B(s?["dither"], true), Colors = J.I(s?["colors"], 256),
            Tag = AnimCore.PlayTag(d),
        };
        return (o, J.N(s?["quality"]) ?? 0.9);
    }

    static (string, string)[] TagOptions(Document d)
    {
        var tags = AnimCore.Tags(d);
        var o = new List<(string, string)> { ("", "전체") };
        for (int i = 0; i < tags.Count; i++) o.Add((i.ToString(), $"{J.S(tags[i]?["name"])} ({J.I(tags[i]?["from"], 0) + 1}~{J.I(tags[i]?["to"], 0) + 1})"));
        return o.ToArray();
    }

    /// <summary>내보내기 창: 형식·크기·구간·반복·투명·색 수·디더링·WebP 화질, 크기 미리 짐작</summary>
    public static void Dialog(string preset = null)
    {
        var d = App.Doc;
        if (d == null || AnimCore.FrameCount(d) < 1) { App.Toast("애니메이션 프레임이 없습니다"); return; }
        var (o, quality) = LastOpts(d);
        if (preset != null) o.Fmt = preset;
        var fields = new List<AUi.Field>
        {
            AUi.Field.Seg("fmt", "형식", Formats.Select(x => (x.id, x.label.Split(" (")[0])).ToArray(), o.Fmt),
            AUi.Field.Seg("scale", "크기", new[] { ("0.25", "25%"), ("0.5", "50%"), ("1", "100%"), ("2", "200%"), ("4", "400%") }, J.F(o.Scale)),
            AnimCore.Tags(d).Count > 0 ? AUi.Field.Seg("tag", "구간", TagOptions(d), o.Tag?.ToString() ?? "") : null,
            AUi.Field.Seg("loop", "반복 (GIF·WebP·APNG)", new[] { ("0", "무한"), ("1", "한 번"), ("2", "2번"), ("3", "3번") }, o.Loop.ToString()),
            AUi.Field.Chk("transparent", "배경 투명 (GIF·WebP·APNG, 종이·흰 바탕 없이)", o.Transparent),
            AUi.Field.Seg("colors", "색 수 (GIF, 적을수록 작음)", new[] { ("256", "256"), ("128", "128"), ("64", "64"), ("32", "32"), ("16", "16") }, o.Colors.ToString()),
            AUi.Field.Chk("dither", "디더링 (GIF, 부드러운 색 번짐, 끄면 작아짐)", o.Dither),
            AUi.Field.Seg("quality", "화질 (WebP)", new[] { ("0.6", "작게"), ("0.8", "보통"), ("0.9", "좋게"), ("1", "최고 (무손실)") }, J.F(quality)),
        };
        (ExportOpts, double) Read(AUi.Values v) => (new ExportOpts
        {
            Fmt = v.S("fmt"), Scale = double.Parse(v.S("scale"), System.Globalization.CultureInfo.InvariantCulture),
            Tag = v.ContainsKey("tag") && int.TryParse(v.S("tag"), out var ti) ? ti : null,
            Loop = int.Parse(v.S("loop")), Transparent = v.B("transparent"), Colors = int.Parse(v.S("colors")), Dither = v.B("dither"),
        }, double.Parse(v.S("quality"), System.Globalization.CultureInfo.InvariantCulture));
        var v = AUi.Form("애니메이션 내보내기 (움짤·동영상)", fields, "내보내기", (vals, live) =>
        {
            try
            {
                var (oo, q) = Read(vals);
                int cnt = AnimCore.ExportOrder(d, oo.Tag).Count;
                var label = Formats.First(x => x.id == oo.Fmt).label + Tips.GetValueOrDefault(oo.Fmt, "");
                string sz = $"{Math.Round(d.W * oo.Scale)}×{Math.Round(d.H * oo.Scale)} · {cnt}장";
                if (oo.Fmt == "png") { live.Text = $"{label}\n{sz} · 프레임마다 PNG 파일로 저장합니다"; return; }
                if (oo.Fmt == "mp4")
                {
                    var (mw, mh) = Mp4Writer.FitSize(Math.Round(d.W * oo.Scale / 2) * 2, Math.Round(d.H * oo.Scale / 2) * 2);
                    string snd = oo.Tag == null && Sound.HasAudio(d) ? (Sound.Decode(d) != null ? " · 붙인 소리 포함" : " · 소리는 WAV 만 넣을 수 있음") : "";
                    live.Text = $"{label}\n{mw}×{mh} · {cnt}장 · H.264{snd}{(mw != Math.Round(d.W * oo.Scale / 2) * 2 ? " (동영상 크기 제한으로 줄임)" : "")}";
                    return;
                }
                long? b = (long)d.W * d.H <= 4_000_000 ? Estimate(d, oo, q) : null;   // 큰 원고는 짐작 생략 (느림)
                live.Text = $"{label}\n{sz}" + (b is long bb ? $" · 예상 크기: 약 {(bb > 1048576 ? (bb / 1048576.0).ToString("0.0") + "MB" : Math.Round(bb / 1024.0) + "KB")}{(bb > 10 * 1048576 ? " — 큰 편입니다 (크기·색 수를 줄여 보세요)" : "")}" : "");
            }
            catch (Exception ex) { live.Text = "크기를 짐작하지 못했습니다: " + ex.Message; }
        });
        if (v == null) return;
        var (opts, qual) = Read(v);
        App.Set("animExport", new JsonObject { ["fmt"] = opts.Fmt, ["scale"] = opts.Scale, ["loop"] = opts.Loop, ["transparent"] = opts.Transparent, ["dither"] = opts.Dither, ["colors"] = opts.Colors, ["quality"] = qual });
        AUi.Busy(opts.Fmt == "mp4" ? "동영상 만들기" : "움짤 만들기", () => Export(d, opts, qual));
    }

    /// <summary>움짤 만들기: 묻지 않고 지난 설정으로 바로 (GIF·WebP·APNG 만, 아니면 GIF)</summary>
    public static void Quick()
    {
        var d = App.Doc;
        if (d == null || AnimCore.FrameCount(d) < 2) { App.Toast("프레임이 2장 이상 있어야 움짤이 됩니다"); return; }
        var (o, q) = LastOpts(d);
        if (!Still(o.Fmt)) o.Fmt = "gif";
        AUi.Busy("움짤 만들기", () => Export(d, o, q));
    }
}
