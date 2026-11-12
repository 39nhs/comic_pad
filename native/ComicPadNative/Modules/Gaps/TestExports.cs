// 시험: 애니 → WebM (붙인 MP3 소리 → Vorbis), 타임랩스 → WebM, 붙인 소리 MP3 풀기 (Sound.ExtraDecoder)
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Anim;
using ComicPadNative.Modules.Media;

namespace ComicPadNative.Modules.Gaps;

public static partial class GapsTest
{
    static Document AnimDoc(int frames, double fps)
    {
        var d = new Document(120, 90) { Name = "애니 시험" };
        d.Layers.Add(new Layer(d, "배경"));
        var track = AnimCore.NewTrackRaw(d, "애니메이션");
        var ids = new List<string>();
        for (int i = 0; i < frames; i++)
        {
            var cel = AnimCore.NewCel(track, d);
            var px = Enumerable.Repeat(0u, 30 * 30).ToArray();
            for (int k = 0; k < px.Length; k++) px[k] = 0xFF000000u | (uint)(40 + i * 60) << 16;
            cel.Px.Write(new Rect(10 + i * 25, 30, 30, 30), px);
            ids.Add(cel.Id);
        }
        track.Meta["xs"] = J.StrArray(ids);
        d.Anim["fps"] = fps;
        return d;
    }

    static void TestAnimWebm()
    {
        var d = AnimDoc(3, 10);
        d.Anim["ms"] = new JsonArray(100, 300, 100);
        // 붙인 소리: MP3 (웹 판처럼 data URL) — Media Foundation 으로 풀려야 함
        var tone = TestTone(44100, 1, 0.6);
        string mp3 = Path.Combine(outDir, "anim_snd.mp3");
        bool haveMp3 = true;
        try { MfAudio.Encode(tone, mp3, MfAudio.MP3); } catch (Exception ex) { haveMp3 = false; Note("MP3 를 만들 수 없어 WAV 로 시험: " + ex.Message); }
        if (haveMp3)
        {
            d.Anim["audio"] = new JsonObject { ["name"] = "snd.mp3", ["data"] = "data:audio/mpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(mp3)), ["offset"] = 0, ["volume"] = 1 };
            var pcm = Sound.Decode(d);
            Ok(pcm != null && pcm.Rate == 44100 && Math.Abs(pcm.Duration - 0.6) < 0.1, $"붙인 MP3 소리 풀기 (Sound.ExtraDecoder → Media Foundation): {pcm?.Rate}Hz {pcm?.Duration:0.00}초");
        }
        string path = Path.Combine(outDir, "anim.webm");
        AnimMedia.ExportWebm(d, 2, null, 0.85, path);
        var r = WebmProbe.Read(File.ReadAllBytes(path));
        var vb = r.Blocks.Where(b => b.track == 1).ToList();
        var at = r.Tracks.FirstOrDefault(t => t.Type == 2);
        Ok(vb.Count == 3 && vb.Select(b => b.t).SequenceEqual(new long[] { 0, 100, 400 }) && r.Tracks[0].W == 240 && r.Tracks[0].H == 180 && Math.Abs(r.Duration - 500) < 1
            && (!haveMp3 || (at?.Codec == "A_VORBIS" && r.Blocks.Count(b => b.track == 2) > 10)),
            $"애니 → WebM: 영상 블록 {vb.Count} (시각 {string.Join(",", vb.Select(b => b.t))}ms), {r.Tracks[0].W}×{r.Tracks[0].H}, 길이 {r.Duration}ms, 소리 {at?.Codec ?? "없음"} 블록 {r.Blocks.Count(b => b.track == 2)}");
        try
        {
            var mf = Mp4Probe.Read(path, new[] { 1 });
            Ok(mf.Frames == 3 && (!haveMp3 || mf.HasAudio), $"  Media Foundation 으로 열기: {mf.Frames}장, 소리 {(mf.HasAudio ? $"{mf.AudioSeconds:0.00}초" : "없음")}");
            if (haveMp3)
            {
                var (rate, ch, data) = MfAudio.Decode(path);
                var (snr, gain, lag) = CompareAudio(tone, data);
                Ok(rate == 44100 && snr > 12 && Math.Abs(gain - 1) < 0.15, $"  WebM 안 소리 (MP3 → Vorbis) 다시 풀기: SNR {snr:0.0}dB (MP3 손실 포함), 크기 비 {gain:0.00}, 어긋남 {lag}");
            }
        }
        catch (Exception ex) { Note("Media Foundation 으로 애니 WebM 확인 못 함: " + ex.Message); }
        // 소리 없는 짧은 애니는 2초 이상으로 반복
        d.Anim["audio"] = null;
        string p2 = Path.Combine(outDir, "anim_loop.webm");
        AnimMedia.ExportWebm(d, 1, null, 0.85, p2);
        var r2 = WebmProbe.Read(File.ReadAllBytes(p2));
        Ok(r2.Blocks.Count == 12 && Math.Abs(r2.Duration - 2000) < 1, $"소리 없는 0.5초 애니 → 4번 반복 ({r2.Blocks.Count}블록, {r2.Duration}ms)");
    }

    static void TestTimelapseWebm()
    {
        string dir = Path.Combine(outDir, "tl"); Directory.CreateDirectory(dir);
        var keys = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var im = Natural(200, 150, 20 + i);
            for (int y = 10; y < 40 + i * 30; y++) for (int x = 10; x < 60; x++) im.P[y * 200 + x] = 0xff000000;
            string f = Path.Combine(dir, $"{i:D4}.png"); File.WriteAllBytes(f, Png(im)); keys.Add(f);
        }
        string path = Path.Combine(outDir, "timelapse.webm");
        TimelapseMedia.MakeWebm(keys, path, 2, 1);
        var r = WebmProbe.Read(File.ReadAllBytes(path));
        Ok(r.Blocks.Count == 3 && Math.Abs(r.Duration - 3000) < 1 && r.Tracks[0].W == 200, $"타임랩스 → WebM: 장면 3개 = 블록 {r.Blocks.Count}, 길이 {r.Duration}ms (30fps 90장 분량, 같은 장면은 다시 넣지 않음), {new FileInfo(path).Length}B");
        try { var mf = Mp4Probe.Read(path); Ok(mf.Frames == 3 && mf.Duration > 2.5, $"  Media Foundation 으로 열기: {mf.Frames}장, {mf.Duration:0.00}초"); }
        catch (Exception ex) { Note("Media Foundation 으로 타임랩스 WebM 확인 못 함: " + ex.Message); }
    }
}
