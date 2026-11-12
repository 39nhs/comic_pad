// WebM 만들기 공용 (애니·타임랩스): 같은 그림이 이어지면 블록을 다시 넣지 않고 시각만 (WebM 은 장마다 시각이 따로라 가변 빠르기가 됨).
// 영상 VP8 (2초마다 키 프레임, 사이는 앞 장 참조), 소리가 있으면 Vorbis (Vorbis.cs).
namespace ComicPadNative.Modules.Media;

public static class WebmExport
{
    /// <summary>VP8 이 받는 크기 (긴 쪽 maxSide 이하, 너비·높이 1 이상)</summary>
    public static (int w, int h) FitSize(double w, double h, int maxSide = 4096)
    {
        double k = Math.Min(1, maxSide / Math.Max(w, h));
        return (Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
    }

    /// <summary>frames: (불투명 ARGB, 시작 시각 ms). 같은 배열(참조)이 이어지면 건너뜀. totalMs = 전체 길이</summary>
    public static void Write(string path, int w, int h, IEnumerable<(uint[] px, long ms)> frames, long totalMs, double quality = 0.8,
        PcmAudio audio = null, double nominalFps = 30, Action<double> progress = null, int frameCount = 0)
    {
        var vorbis = audio != null ? Vorbis.Encode(audio, quality) : null;
        var info = vorbis != null ? new WebmAudio { Codec = "A_VORBIS", Private = WebmWriter.XiphLace(vorbis.Headers), Rate = audio.Rate, Channels = audio.Channels } : null;
        bool fast = (long)w * h > 640 * 480;   // 큰 그림은 4×4 모드를 빼서 빠르게
        var enc = new Vp8Video(quality, int.MaxValue, fast);
        using var wm = new WebmWriter(path, w, h, nominalFps, info);
        if (vorbis != null) foreach (var (t, pkt) in vorbis.Packets) wm.AddAudio(pkt, t);
        uint[] prev = null; long lastKey = long.MinValue / 2; int done = 0;
        foreach (var (px, ms) in frames)
        {
            done++;
            if (ReferenceEquals(px, prev)) { progress?.Invoke(frameCount > 0 ? done / (double)frameCount : 0); continue; }
            prev = px;
            var im = new Argb(w, h, px);
            bool forceKey = ms - lastKey >= 2000;
            var planes = Vp8.FromArgb(im);
            var (data, key) = forceKey ? EncodeKey(enc, planes) : enc.Encode(planes);
            if (key) lastKey = ms;
            wm.AddVideo(data, ms, key);
            progress?.Invoke(frameCount > 0 ? done / (double)frameCount : 0);
        }
        wm.Finish(totalMs);
    }

    static (byte[], bool) EncodeKey(Vp8Video enc, Vp8Planes p) { enc.ForceKey(); return enc.Encode(p); }
}
