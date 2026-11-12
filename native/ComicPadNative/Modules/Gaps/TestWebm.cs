// 시험: WebM (EBML 구조·VP8 블록 되풀기·Windows Media Foundation 으로 열기)
using ComicPadNative.Modules.Media;

namespace ComicPadNative.Modules.Gaps;

public static partial class GapsTest
{
    static void TestWebmBasic()
    {
        int w = 320, h = 240, n = 12;
        string p = Path.Combine(outDir, "basic.webm");
        var src = new List<Argb>();
        using (var wm = new WebmWriter(p, w, h, 12))
        {
            for (int i = 0; i < n; i++)
            {
                var f = Natural(w, h, 5);
                for (int y = 60; y < 120; y++) for (int x = 10 + i * 20; x < 60 + i * 20; x++) f.P[y * w + x] = 0xff303030;
                Gray(f);   // 회색만: 디코더마다 다른 색 행렬 영향 없이 밝기로 비교
                src.Add(f);
                wm.AddVideo(Vp8.Encode(f, 0.85), (long)Math.Round(i * 1000 / 12.0));
            }
            wm.Finish((long)Math.Round(n * 1000 / 12.0));
        }
        var bytes = File.ReadAllBytes(p);
        var r = WebmProbe.Read(bytes);
        var vt = r.Tracks.FirstOrDefault();
        Ok(r.DocType == "webm" && vt?.Codec == "V_VP8" && vt.W == w && vt.H == h && r.Blocks.Count == n && r.Blocks.All(b => b.key) && r.CuePoints >= 1 && Math.Abs(r.Duration - 1000) < 1,
            $"WebM 구조: DocType {r.DocType}, {vt?.Codec} {vt?.W}×{vt?.H}, 블록 {r.Blocks.Count}, 클러스터 {r.Clusters}, Cues {r.CuePoints}, 길이 {r.Duration}ms, {bytes.Length}B");
        double minPs = 99;
        for (int i = 0; i < n; i++) minPs = Math.Min(minPs, PsnrY(src[i], Vp8.Decode(r.Blocks[i].data, 0, r.Blocks[i].data.Length)));
        Ok(minPs > 38, $"WebM 안 VP8 블록을 직접 풀기: 가장 나쁜 장 밝기 PSNR {minPs:0.0}dB");
        try
        {
            var mf = Mp4Probe.Read(p, new[] { 0, n - 1 });
            var me0 = Vp8.Decode(r.Blocks[0].data, 0, r.Blocks[0].data.Length);
            var mfl = mf.Pixels.TryGetValue(n - 1, out var pxl) ? new Argb(w, h, pxl.Select(c => c | 0xff000000u).ToArray()) : null;
            var mel = Vp8.Decode(r.Blocks[n - 1].data, 0, r.Blocks[n - 1].data.Length);
            double ps0 = mf.Pixels.TryGetValue(0, out var px0) ? FitPsnrY(me0, new Argb(w, h, px0.Select(c => c | 0xff000000u).ToArray())) : 0;
            double psl = mfl != null ? FitPsnrY(mel, mfl) : 0;
            Ok(mf.W == w && mf.H == h && mf.Frames == n && ps0 > 40 && psl > 40, $"Windows Media Foundation 으로 WebM 열기: {mf.W}×{mf.H}, {mf.Frames}장, 길이 {mf.Duration:0.00}초, 직접 푼 것과 PSNR 첫 장 {ps0:0.0}dB·끝 장 {psl:0.0}dB (MF 의 YUV→RGB 색 행렬 차이는 맞춘 뒤)");
        }
        catch (Exception ex) { Note("Media Foundation 이 이 PC 에서 WebM(VP8)을 못 엶 (웹 미디어 확장 없음?): " + ex.Message); }
    }

    /// <summary>사이 프레임: 디코더(Media Foundation)가 부호기 복원과 같은 그림을 내는지 — 틀리면 장이 지날수록 어긋남이 쌓임</summary>
    static void TestWebmInter()
    {
        int w = 256, h = 176, n = 24;
        string p = Path.Combine(outDir, "inter.webm");
        var enc = new Vp8Video(0.8, 12);
        var recs = new List<Argb>(); var srcs = new List<Argb>();
        long keyBytes = 0, interBytes = 0;
        using (var wm = new WebmWriter(p, w, h, 24))
        {
            for (int i = 0; i < n; i++)
            {
                var f = Natural(w, h, 9);
                for (int y = 40; y < 90; y++) for (int x = 5 + i * 8; x < 45 + i * 8; x++) f.P[y * w + x] = 0xff202020;   // 움직이는 상자
                if (i % 5 == 0) for (int y = 120; y < 170; y++) for (int x = 150; x < 250; x++) f.P[y * w + x] ^= 0x00ffffff;   // 가끔 바뀌는 곳
                Gray(f);
                var (data, key) = enc.Encode(f);
                if (key) keyBytes += data.Length; else interBytes += data.Length;
                recs.Add(Vp8.ToArgb(enc.Last)); srcs.Add(f);
                wm.AddVideo(data, (long)Math.Round(i * 1000 / 24.0), key);
            }
            wm.Finish(1000);
        }
        var r = WebmProbe.Read(File.ReadAllBytes(p));
        double minPs = recs.Select((x, i) => PsnrY(srcs[i], x)).Min();
        Ok(r.Blocks.Count == n && r.Blocks.Count(b => b.key) == 2 && minPs > 34 && interBytes / 22.0 < keyBytes / 2.0 / 3,
            $"사이 프레임 부호화: 키 2장 평균 {keyBytes / 2}B, 사이 22장 평균 {interBytes / 22}B, 원본 대비 가장 나쁜 장 밝기 PSNR {minPs:0.0}dB");
        try
        {
            var want = Enumerable.Range(0, n).ToArray();
            var mf = Mp4Probe.Read(p, want);
            double worst = 99; int worstAt = -1;
            for (int i = 0; i < n; i++)
            {
                if (!mf.Pixels.TryGetValue(i, out var px)) { worst = 0; worstAt = i; break; }
                double ps = FitPsnrY(recs[i], new Argb(w, h, px.Select(c => c | 0xff000000u).ToArray()));
                if (ps < worst) { worst = ps; worstAt = i; }
            }
            Ok(mf.Frames == n && worst > 45, $"Media Foundation 이 푼 사이 프레임 = 부호기 복원 ({mf.Frames}장, 가장 다른 장 {worstAt}: PSNR {worst:0.0}dB)");
        }
        catch (Exception ex) { Note("Media Foundation 으로 사이 프레임 WebM 확인 못 함: " + ex.Message); }
    }

    internal static void Gray(Argb f)
    {
        for (int k = 0; k < f.P.Length; k++) { uint c = f.P[k]; uint g = (((c >> 16) & 255) * 77 + ((c >> 8) & 255) * 150 + (c & 255) * 29) >> 8; f.P[k] = (c & 0xff000000u) | (g << 16) | (g << 8) | g; }
    }

    /// <summary>RGB PSNR, 단 b 를 a 의 3×3 색 행렬 + 치우침으로 맞춘 뒤 (디코더마다 다른 YUV→RGB 행렬·범위를 빼고 모양만 비교)</summary>
    internal static double FitPsnrY(Argb a, Argb b)
    {
        int n = a.P.Length;
        var M = new double[4, 4]; var V = new double[3, 4];
        static double C(uint c, int k) => k == 3 ? 1 : (c >> (16 - 8 * k)) & 255;
        for (int i = 0; i < n; i++)
            for (int r = 0; r < 4; r++)
            {
                double x = C(a.P[i], r);
                for (int q = 0; q < 4; q++) M[r, q] += x * C(a.P[i], q);
                for (int ch = 0; ch < 3; ch++) V[ch, r] += x * C(b.P[i], ch);
            }
        var beta = new double[3, 4];
        for (int ch = 0; ch < 3; ch++)
        {
            var A = new double[4, 5];
            for (int r = 0; r < 4; r++) { for (int q = 0; q < 4; q++) A[r, q] = M[r, q] + (r == q ? 1e-6 : 0); A[r, 4] = V[ch, r]; }
            for (int col = 0; col < 4; col++)
            {
                int piv = col; for (int r = col + 1; r < 4; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
                for (int q = 0; q < 5; q++) (A[col, q], A[piv, q]) = (A[piv, q], A[col, q]);
                for (int r = 0; r < 4; r++) if (r != col) { double f = A[r, col] / A[col, col]; for (int q = col; q < 5; q++) A[r, q] -= f * A[col, q]; }
            }
            for (int r = 0; r < 4; r++) beta[ch, r] = A[r, 4] / A[r, r];
        }
        double e = 0;
        for (int i = 0; i < n; i++)
            for (int ch = 0; ch < 3; ch++)
            {
                double p = 0; for (int r = 0; r < 4; r++) p += beta[ch, r] * C(a.P[i], r);
                double d = p - C(b.P[i], ch); e += d * d;
            }
        return e == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * n * 3 / e);
    }
}
