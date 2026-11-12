// 시험: VP8 손실 WebP (만들기·풀기·화질·크기·WIC 와 비교·알파·움직이는 그림·libwebp 가 만든 참고 파일)
using ComicPadNative.Modules.Media;

namespace ComicPadNative.Modules.Gaps;

public static partial class GapsTest
{
    static void TestVp8()
    {
        // 1) 크기 여러 가지 (16 배수 아님 포함) — 되풀어서 화질
        foreach (var (w, h) in new[] { (1, 1), (17, 9), (64, 64), (333, 217) })
        {
            var im = Natural(w, h, w + h);
            var bytes = WebP.EncodeStill(im, 0.9);
            var back = WebP.DecodeFrames(bytes)[0].img;
            double ps = PsnrY(im, back);
            var info = WebP.Probe(bytes);
            Ok(back.W == w && back.H == h && info.Lossy && !info.Alpha && ps > (w * h < 300 ? 30 : 36), $"손실 WebP {w}×{h} 화질 0.9: 밝기 PSNR {ps:0.0}dB, {bytes.Length}B");
        }
        // 2) 화질별 PSNR 하한 + 화질이 낮을수록 작아짐
        var src = Natural(400, 300, 7);
        File.WriteAllBytes(Path.Combine(outDir, "vp8_src.png"), Png(src));
        var table = new[] { (0.95, 42.0), (0.9, 41.0), (0.8, 39.5), (0.6, 36.5), (0.3, 31.0), (0.0, 25.0) };
        long prev = long.MaxValue; double prevPs = 99;
        foreach (var (q, min) in table)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bytes = WebP.EncodeStill(src, q);
            long encMs = sw.ElapsedMilliseconds; sw.Restart();
            var back = WebP.DecodeFrames(bytes)[0].img;
            long decMs = sw.ElapsedMilliseconds;
            double ps = PsnrY(src, back), psRgb = Vp8.Psnr(src, back);
            File.WriteAllBytes(Path.Combine(outDir, $"vp8_q{(int)(q * 100)}.webp"), bytes);
            Ok(ps >= min && bytes.Length < prev && ps <= prevPs + 0.01, $"화질 {q:0.00} (양자화 {Vp8.QIndex(q)}): 밝기 PSNR {ps:0.00}dB (하한 {min}), RGB {psRgb:0.00}dB, {bytes.Length}B ({bytes.Length * 8.0 / (400 * 300):0.000} bpp), 만들기 {encMs}ms·풀기 {decMs}ms");
            prev = bytes.Length; prevPs = ps;
            // 3) Windows WIC (WebP 확장) 로도 같은 그림이 나오는지
            Argb wic = null;
            try { wic = Wic.Decode(Path.Combine(outDir, $"vp8_q{(int)(q * 100)}.webp")); } catch (Exception ex) { if (q == 0.9) Note("WIC 로 손실 WebP 를 풀 수 없음 (WebP 확장 없음?): " + ex.Message); }
            if (wic != null)
            {
                double pw = Vp8.Psnr(back, wic), ps2 = PsnrY(src, wic);
                Ok(pw > 42 && Math.Abs(ps2 - ps) < 0.6, $"  WIC 로 푼 것과 직접 푼 것 PSNR {pw:0.0}dB, WIC 그림의 원본 대비 밝기 {ps2:0.00}dB");
            }
        }
        // 4) 알파: ALPH 무손실 → 알파는 똑같고, 보이는 곳 색은 손실
        var ta = Natural(150, 90, 3, alpha: true);
        var ab = WebP.EncodeStill(ta, 0.85);
        File.WriteAllBytes(Path.Combine(outDir, "vp8_alpha.webp"), ab);
        var ai = WebP.Probe(ab);
        var aback = WebP.DecodeFrames(ab)[0].img;
        bool alphaSame = true;
        for (int i = 0; i < ta.P.Length; i++) if ((ta.P[i] >> 24) != (aback.P[i] >> 24)) { alphaSame = false; break; }
        double pv = PsnrY(ta, aback, true);
        string head = System.Text.Encoding.ASCII.GetString(ab, 0, Math.Min(64, ab.Length));
        Ok(ai.Alpha && ai.Lossy && alphaSame && pv > 33 && head.Contains("VP8X") && head.Contains("ALPH"), $"투명 손실 WebP: VP8X+ALPH+VP8, 알파 그대로 {alphaSame}, 보이는 곳 밝기 PSNR {pv:0.0}dB, {ab.Length}B");
        try
        {
            var wa = Wic.Decode(Path.Combine(outDir, "vp8_alpha.webp"));
            bool wsame = true;
            for (int i = 0; i < ta.P.Length; i++) if ((ta.P[i] >> 24) != (wa.P[i] >> 24)) { wsame = false; break; }
            Ok(wsame && PsnrY(ta, wa, true) > 32, $"  WIC 로 푼 투명 손실 WebP: 알파 같음 {wsame}, 보이는 곳 밝기 PSNR {PsnrY(ta, wa, true):0.0}dB");
        }
        catch (Exception ex) { Note("WIC 투명 손실 WebP: " + ex.Message); }
        // 5) 움직이는 손실 WebP
        var frames = new List<(Argb, int)>();
        for (int k = 0; k < 4; k++) { var f = Natural(120, 80, 11); for (int y = 20; y < 40; y++) for (int x = 10 + k * 20; x < 30 + k * 20; x++) f.P[y * 120 + x] = 0xffff2020; frames.Add((f, 80 + k * 10)); }
        var anim = WebP.EncodeAnimated(frames, 0, false, 0.85);
        File.WriteAllBytes(Path.Combine(outDir, "vp8_anim.webp"), anim);
        var abk = WebP.DecodeFrames(anim);
        double minPs = abk.Select((b, i) => PsnrY(frames[i].Item1, b.img)).Min();
        var lossless = WebP.EncodeAnimated(frames, 0, false, 1);
        Ok(abk.Count == 4 && minPs > 33 && WebP.Probe(anim).Lossy && abk.Select(b => b.ms).SequenceEqual(new[] { 80, 90, 100, 110 }),
            $"움직이는 손실 WebP: 4장, 가장 나쁜 장 PSNR {minPs:0.0}dB, {anim.Length}B (무손실이면 {lossless.Length}B)");
        try
        {
            var w0 = Wic.Decode(Path.Combine(outDir, "vp8_anim.webp"), 0);
            Ok(Vp8.Psnr(abk[0].img, w0) > 40, $"  WIC 로 움직이는 손실 WebP 첫 장 (직접 푼 것과 PSNR {Vp8.Psnr(abk[0].img, w0):0.0}dB)");
        }
        catch (Exception ex) { Note("WIC 움직이는 손실 WebP: " + ex.Message); }
        // 6) 비트 단위 부품: 불 부호기 → 복호기
        var rnd = new Random(5);
        var bits = Enumerable.Range(0, 5000).Select(_ => (p: rnd.Next(1, 256), b: rnd.Next(2))).ToList();
        var enc = new Vp8BoolEnc();
        foreach (var (p, b) in bits) enc.Put(p, b);
        var data = enc.Finish();
        var dec = new Vp8BoolDec(data, 0, data.Length);
        Ok(bits.All(x => dec.Bit(x.p) == x.b), $"불 산술 부호기 → 복호기 5000비트 ({data.Length}B)");
    }

    /// <summary>libwebp(PIL 등)가 만든 손실 WebP 를 직접 풀어 PNG 로 (COMICPAD_WEBP_REF=폴더: *.webp, 옆에 *.ref.png 가 있으면 PSNR 비교)</summary>
    static void TestVp8Reference()
    {
        string dir = Environment.GetEnvironmentVariable("COMICPAD_WEBP_REF");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Note("COMICPAD_WEBP_REF 없음 — libwebp 참고 파일 비교 건너뜀"); return; }
        foreach (var f in Directory.GetFiles(dir, "*.webp").OrderBy(x => x))
        {
            try
            {
                var bytes = File.ReadAllBytes(f);
                var fr = WebP.DecodeFrames(bytes);
                File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + ".native.png"), Png(fr[0].img));
                string refPng = Path.ChangeExtension(f, ".ref.png");
                if (File.Exists(refPng))
                {
                    var r = LoadPng(refPng);
                    double ps = r.W == fr[0].img.W && r.H == fr[0].img.H ? Vp8.Psnr(r, fr[0].img) : 0;
                    bool aok = true;
                    for (int i = 0; i < r.P.Length && i < fr[0].img.P.Length; i++) if (Math.Abs((int)(r.P[i] >> 24) - (int)(fr[0].img.P[i] >> 24)) > 1) { aok = false; break; }
                    Ok(ps > 40 && aok, $"libwebp 파일 {Path.GetFileName(f)} ({fr.Count}장): libwebp 로 푼 것과 PSNR {ps:0.0}dB, 알파 같음 {aok}");
                }
                else Note($"{Path.GetFileName(f)} 풀림 ({fr.Count}장, {fr[0].img.W}×{fr[0].img.H})");
            }
            catch (Exception ex) { Ok(false, $"libwebp 파일 {Path.GetFileName(f)}: {ex.Message}"); }
        }
    }

    /// <summary>밝기(BT.601 Y)만 PSNR — 색차 절반 해상도(4:2:0) 손실과 섞지 않으려고</summary>
    internal static double PsnrY(Argb a, Argb b, bool visibleOnly = false)
    {
        double s = 0; long n = 0;
        static double Y(uint c) => 0.299 * ((c >> 16) & 255) + 0.587 * ((c >> 8) & 255) + 0.114 * (c & 255);
        for (int i = 0; i < a.P.Length; i++)
        {
            if (visibleOnly && (a.P[i] >> 24) == 0) continue;
            double d = Y(a.P[i]) - Y(b.P[i]); s += d * d; n++;
        }
        return s == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * n / s);
    }

    internal static byte[] Png(Argb im)
    {
        using var bmp = new Bitmap(im.W, im.H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, im.W, im.H), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        for (int y = 0; y < im.H; y++) System.Runtime.InteropServices.Marshal.Copy((int[])(object)im.P, y * im.W, bd.Scan0 + y * bd.Stride, im.W);
        bmp.UnlockBits(bd);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    internal static Argb LoadPng(string path)
    {
        using var raw = new Bitmap(path);
        using var bmp = raw.Clone(new Rectangle(0, 0, raw.Width, raw.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var o = new Argb(bmp.Width, bmp.Height);
        var bd = bmp.LockBits(new Rectangle(0, 0, o.W, o.H), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
        var tmp = new int[o.W];
        for (int y = 0; y < o.H; y++) { System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, tmp, 0, o.W); for (int x = 0; x < o.W; x++) o.P[y * o.W + x] = (uint)tmp[x]; }
        bmp.UnlockBits(bd);
        return o;
    }
}
