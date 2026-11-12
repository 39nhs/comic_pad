// 시험: Vorbis 부호기 (MDCT 짝 맞음, WebM·Ogg 안 Vorbis 를 Media Foundation 이 풀어 원래 소리와 비교) · MF 소리 풀기 (MP3·AAC·WMA·OGG)
using ComicPadNative.Modules.Media;

namespace ComicPadNative.Modules.Gaps;

public static partial class GapsTest
{
    /// <summary>시험 소리: 두 음 + 미끄러지는 음 + 약한 잡음 (stereo 면 오른쪽은 다른 음)</summary>
    internal static PcmAudio TestTone(int rate, int ch, double sec)
    {
        int n = (int)(rate * sec);
        var o = new PcmAudio { Rate = rate, Channels = ch, Data = new short[n * ch] };
        var rnd = new Random(1);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate, env = Math.Min(1, Math.Min(t * 20, (sec - t) * 20));
            for (int c = 0; c < ch; c++)
            {
                double f0 = c == 0 ? 440 : 660;
                double v = 0.3 * Math.Sin(2 * Math.PI * f0 * t) + 0.15 * Math.Sin(2 * Math.PI * (2500 + 1500 * t) * t) + 0.1 * Math.Sin(2 * Math.PI * 97 * t) + 0.01 * (rnd.NextDouble() - 0.5);
                o.Data[i * ch + c] = (short)Math.Round(v * env * 32767);
            }
        }
        return o;
    }

    /// <summary>원래 소리와 디코더 출력 비교: 앞뒤 어긋남(±delay)을 찾아 맞춘 뒤 SNR(dB)·크기 비(출력/원래)</summary>
    internal static (double snr, double gain, int lag) CompareAudio(PcmAudio src, float[][] dec, int maxLag = 3000)
    {
        int ch = src.Channels, n = src.Frames;
        double[] a = new double[n]; for (int i = 0; i < n; i++) a[i] = src.Data[i * ch] / 32768.0;
        var d = dec[0];
        int bestLag = 0; double bestC = double.MinValue;
        int from = Math.Min(n / 4, 4000), to = Math.Min(n - maxLag - 1, from + 20000);
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double c = 0;
            for (int i = from; i < to; i += 3) { int j = i + lag; if (j >= 0 && j < d.Length) c += a[i] * d[j]; }
            if (c > bestC) { bestC = c; bestLag = lag; }
        }
        double se = 0, ss = 0, sd = 0;
        int cnt = 0;
        for (int c = 0; c < Math.Min(ch, dec.Length); c++)
            for (int i = rate0(n) ; i < n - rate0(n); i++)
            {
                int j = i + bestLag; if (j < 0 || j >= dec[c].Length) continue;
                double x = src.Data[i * ch + c] / 32768.0, y = dec[c][j];
                se += (x - y) * (x - y); ss += x * x; sd += y * y; cnt++;
            }
        return (10 * Math.Log10(ss / Math.Max(1e-12, se)), Math.Sqrt(sd / Math.Max(1e-12, ss)), bestLag);
        static int rate0(int n) => Math.Min(n / 10, 4800);
    }

    static void TestVorbis()
    {
        // 1) MDCT ↔ 디코더 IMDCT (곱 없음) + 창 + 겹쳐 더하기 = 원래 신호
        {
            int M = 1024, N = 2048;
            var x = new double[M * 4];
            var rnd = new Random(3);
            for (int i = 0; i < x.Length; i++) x[i] = Math.Sin(i * 0.05) * 0.5 + (rnd.NextDouble() - 0.5) * 0.1;
            var outp = new double[x.Length];
            var w = Vorbis.Win;
            for (int b = 0; b + N <= x.Length; b += M)
            {
                var seg = new double[N]; for (int i = 0; i < N; i++) seg[i] = x[b + i] * w[i];
                var y = Vorbis.ImdctSlow(Vorbis.Mdct(seg));
                for (int i = 0; i < N; i++) outp[b + i] += y[i] * w[i];
            }
            double err = 0; for (int i = M; i < x.Length - M; i++) err = Math.Max(err, Math.Abs(outp[i] - x[i]));
            Ok(err < 1e-6, $"Vorbis MDCT ↔ 디코더 IMDCT 겹쳐 더하기로 원래 신호 (최대 오차 {err:E1})");
        }
        // 2) WebM(Vorbis 소리만 넣은 영상) → Media Foundation 으로 풀어 비교
        foreach (var (rate, ch, q) in new[] { (48000, 2, 0.8), (44100, 1, 0.5), (48000, 2, 1.0) })
        {
            var src = TestTone(rate, ch, 3.0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var vs = Vorbis.Encode(src, q);
            long encMs = sw.ElapsedMilliseconds;
            long bytes = vs.Packets.Sum(p => (long)p.pkt.Length);
            double kbps = bytes * 8 / 3.0 / 1000;
            string ogg = Path.Combine(outDir, $"tone_{rate}_{ch}_{(int)(q * 100)}.ogg");
            File.WriteAllBytes(ogg, Vorbis.ToOgg(vs));
            string webm = Path.Combine(outDir, $"tone_{rate}_{ch}_{(int)(q * 100)}.webm");
            using (var wm = new WebmWriter(webm, 64, 48, 10, new WebmAudio { Private = WebmWriter.XiphLace(vs.Headers), Rate = rate, Channels = ch }))
            {
                foreach (var (t, p) in vs.Packets) wm.AddAudio(p, t);
                var blank = new Argb(64, 48); Array.Fill(blank.P, 0xff808080u);
                var enc = new Vp8Video(0.5, 100);
                for (int i = 0; i < 30; i++) { var (d, k) = enc.Encode(blank); wm.AddVideo(d, i * 100, k); }
                wm.Finish(3000);
            }
            Note($"Vorbis {rate}Hz {ch}ch 화질 {q}: {bytes}B ≈ {kbps:0}kbps, 만들기 {encMs}ms");
            foreach (var file in new[] { webm, ogg })
            {
                try
                {
                    var (r, c, data) = MfAudio.Decode(file);
                    var (snr, gain, lag) = CompareAudio(src, data);
                    double minSnr = q >= 0.99 ? 25 : q >= 0.7 ? 18 : 12;
                    Ok(r == rate && c == ch && snr > minSnr && Math.Abs(gain - 1) < 0.1 && Math.Abs(data[0].Length - src.Frames) < 3000,
                        $"  Media Foundation 이 푼 {Path.GetExtension(file)} Vorbis: {r}Hz {c}ch, {data[0].Length}샘플 (원래 {src.Frames}), SNR {snr:0.0}dB (하한 {minSnr}), 크기 비 {gain:0.000}, 어긋남 {lag}");
                }
                catch (Exception ex) { Note($"  Media Foundation 으로 {Path.GetExtension(file)} Vorbis 를 못 풂 (웹 미디어 확장 없음?): {ex.Message}"); }
            }
        }
    }

    static void TestMfAudio()
    {
        var src = TestTone(44100, 2, 2.0);
        foreach (var (name, ext, sub, bps) in new[] { ("AAC", ".m4a", Mf.AAC, 16000), ("MP3", ".mp3", MfAudio.MP3, 16000), ("WMA", ".wma", MfAudio.WMA2, 16000) })
        {
            string p = Path.Combine(outDir, "snd" + ext);
            try { MfAudio.Encode(src, p, sub, bps); }
            catch (Exception ex) { Note($"{name} 시험 파일을 이 PC 에서 만들 수 없음 (Windows 인코더 없음): {ex.Message}"); continue; }
            try
            {
                var bytes = File.ReadAllBytes(p);
                var (r, c, data) = MfAudio.DecodeBytes(bytes, ext);
                var (snr, gain, lag) = CompareAudio(src, data);
                Ok(r == 44100 && c == 2 && snr > 15 && Math.Abs(gain - 1) < 0.15, $"{name} 풀기 (Media Foundation): {r}Hz {c}ch {data[0].Length}샘플, SNR {snr:0.0}dB, 크기 비 {gain:0.00}, 어긋남 {lag}");
            }
            catch (Exception ex) { Ok(false, $"{name} 풀기: {ex.Message}"); }
        }
    }
}
