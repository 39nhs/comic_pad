// Media 모듈 시험 (창 없이, 정해진 결과): COMICPAD_MEDIA_SELFTEST=결과 폴더, COMICPAD_DATA=임시 작업물 폴더 로 --render 실행.
// 만든 파일을 다시 풀어서 확인: WebP(직접 디코더 + WIC 가 있으면 WIC 로도) · MP4(Media Foundation 소스 리더) · PDF 쪽 수 · EPUB 구조.
// 결과: 결과 폴더/report.txt (통과/실패 줄), 끝 코드 = 실패 수. 사용자 설정 파일은 건드리지 않음.
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Anim;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Media;

public static class MediaTest
{
    static readonly StringBuilder log = new();
    static int fail;
    static string outDir;
    static void Ok(bool cond, string name) { log.AppendLine((cond ? "통과  " : "실패  ") + name); if (!cond) fail++; }
    static void Note(string s) => log.AppendLine("참고  " + s);

    public static int Run(string dir)
    {
        outDir = dir; Directory.CreateDirectory(dir);
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("COMICPAD_DATA"))) { File.WriteAllText(Path.Combine(dir, "report.txt"), "COMICPAD_DATA 를 임시 폴더로 정해 주세요"); return 2; }
            App.Settings = new JsonObject();   // 사용자 설정과 무관
            MediaModule.RegisterMenus();       // 창 모드처럼 다른 모듈 뒤에 메뉴를 대신 (--render 는 모듈 순서가 이름순이 아님)
            void T(string n, Action a)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { a(); } catch (Exception ex) { Ok(false, $"{n}: 예외 {ex.GetType().Name} {ex.Message}"); }
                log.AppendLine($"      ({n} {sw.ElapsedMilliseconds}ms)");
            }
            T("VP8L 부품", TestBits);
            T("WebP 정지 그림", TestWebPStill);
            T("WebP 움직이는 그림", TestWebPAnim);
            T("MP4", TestMp4);
            T("MP4 소리", TestMp4Audio);
            T("애니 내보내기", TestAnimExport);
            T("타임랩스", TestTimelapse);
            T("인쇄 (작품 전체)", TestPrint);
            T("펼침면", TestSpread);
            T("이미지 내보내기·WebP 열기", TestStill);
            T("전자책 (작품 전체)", TestEbook);
            T("메뉴", TestMenus);
        }
        catch (Exception ex) { Ok(false, "시험 실행 오류: " + ex); }
        log.AppendLine(fail == 0 ? "모두 통과" : $"실패 {fail}개");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        return fail;
    }

    // ------------------------------------------------------------------ 시험 그림
    /// <summary>그러데이션·잡음·평평한 곳·반투명·완전 투명(색 있음)이 섞인 그림</summary>
    static Argb Pattern(int w, int h, int seed, bool alpha)
    {
        var rnd = new Random(seed);
        var im = new Argb(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint a = 255, r, g, b;
                if (x < w / 3) { r = (uint)(x * 255 / Math.Max(1, w)); g = (uint)(y * 255 / Math.Max(1, h)); b = (uint)((x + y + seed * 13) & 255); }
                else if (x < 2 * w / 3) { r = 250; g = 240; b = (uint)(seed * 40 & 255); }   // 평평
                else { r = (uint)rnd.Next(256); g = (uint)rnd.Next(256); b = (uint)rnd.Next(256); }
                if (alpha && y < h / 4) a = (uint)(x * 255 / Math.Max(1, w - 1));
                if (alpha && y >= h - 3) { a = 0; }   // 완전 투명이지만 색은 남겨 둠 (무손실이면 그대로여야)
                im.P[y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        return im;
    }
    static bool Same(Argb a, Argb b) => a.W == b.W && a.H == b.H && a.P.AsSpan().SequenceEqual(b.P);
    static int Diffs(Argb a, Argb b) { int n = 0; for (int i = 0; i < a.P.Length; i++) if (a.P[i] != b.P[i]) n++; return n; }

    // ------------------------------------------------------------------ VP8L 부품
    static void TestBits()
    {
        // 허프만 길이 제한·완전성 (Kraft 합 = 1)
        var rnd = new Random(1);
        var counts = Enumerable.Range(0, 280).Select(i => i < 200 ? (int)Math.Pow(2, rnd.Next(0, 24)) : 0).ToArray();
        var len = Huff.Lengths(counts, 15);
        double kraft = len.Where(l => l > 0).Sum(l => Math.Pow(2, -l));
        Ok(len.Max() <= 15 && Math.Abs(kraft - 1) < 1e-12, $"허프만 길이 ≤15·완전한 부호 (최대 {len.Max()})");
        // 직접 짠 비트 쓰기/읽기
        var bw = new BitW();
        uint V(int i) => (uint)i * 2654435761u >> (32 - (i % 24 + 1));
        for (int i = 0; i < 1000; i++) bw.Put(V(i), i % 24 + 1);
        var br = new BitR(bw.ToArray(), 0, bw.ToArray().Length);
        bool okBits = true;
        for (int i = 0; i < 1000; i++) okBits &= br.Read(i % 24 + 1) == V(i);
        Ok(okBits, "비트 쓰기·읽기 (LSB 먼저)");
    }

    // ------------------------------------------------------------------ WebP
    static void TestWebPStill()
    {
        foreach (var (w, h, alpha, name) in new[] { (157, 93, true, "그러데이션·잡음·투명"), (1, 1, false, "1×1"), (64, 64, false, "한 가지 색"), (300, 7, true, "가로로 긴") })
        {
            var im = name == "한 가지 색" ? new Argb(w, h, Enumerable.Repeat(0xFF3366CCu, w * h).ToArray()) : Pattern(w, h, w, alpha);
            var bytes = WebP.EncodeStill(im);
            var back = WebP.DecodeFrames(bytes)[0].img;
            Ok(Same(im, back), $"무손실 WebP 되풀기 {name} {w}×{h} ({bytes.Length}B, 다른 픽셀 {Diffs(im, back)})");
            var info = WebP.Probe(bytes);
            Ok(info.W == w && info.H == h && !info.Animated && !info.Lossy && info.Alpha == im.HasAlpha(), $"WebP 머리 정보 {name}");
        }
        // 두 색·긴 반복 (거리 부호·반복 길이)
        var two = new Argb(500, 300);
        for (int i = 0; i < two.P.Length; i++) two.P[i] = (i / 37 % 2 == 0) ? 0xFF000000u : 0xFFFFFFFFu;
        var tb = WebP.EncodeStill(two);
        Ok(Same(two, WebP.DecodeFrames(tb)[0].img) && tb.Length < 4000, $"두 색 줄무늬 (LZ77 압축 {tb.Length}B)");
        // 거의 무손실: 버린 비트만큼만 다름
        var src = Pattern(120, 80, 5, false);
        var nl = WebP.DecodeFrames(WebP.EncodeStill(src, 0.8))[0].img;
        int maxD = 0;
        for (int i = 0; i < src.P.Length; i++) for (int sh = 0; sh < 24; sh += 8) maxD = Math.Max(maxD, Math.Abs((int)((src.P[i] >> sh) & 255) - (int)((nl.P[i] >> sh) & 255)));
        Ok(maxD <= 2 && WebP.EncodeStill(src, 0.8).Length < WebP.EncodeStill(src).Length, $"거의 무손실 화질 0.8 (최대 차이 {maxD})");
        // WIC (Windows 의 WebP 확장) 로도 풀리는지
        var test = Pattern(157, 93, 3, false);
        string p = Path.Combine(outDir, "still.webp");
        File.WriteAllBytes(p, WebP.EncodeStill(test));
        Argb wic = null;
        try { wic = Wic.Decode(p); } catch (Exception ex) { Note("WIC 로 WebP 를 풀 수 없음 (WebP 확장 없음?): " + ex.Message); }
        if (wic != null) Ok(Same(test, wic), $"WIC(Windows WebP 디코더)로 풀어도 같은 픽셀 (다른 픽셀 {Diffs(test, wic)})");
        var pa = Pattern(157, 93, 4, true);
        File.WriteAllBytes(Path.Combine(outDir, "still_alpha.webp"), WebP.EncodeStill(pa));
        if (wic != null)
        {
            var wa = Wic.Decode(Path.Combine(outDir, "still_alpha.webp"));
            int bad = 0; for (int i = 0; i < pa.P.Length; i++) if ((pa.P[i] >> 24) != 0 && pa.P[i] != wa.P[i]) bad++;
            Ok(bad == 0, $"WIC 로 푼 투명 WebP (보이는 픽셀 다름 {bad})");
        }
    }

    static void TestWebPAnim()
    {
        int w = 96, h = 64;
        var f0 = Pattern(w, h, 1, true);
        var f1 = new Argb(w, h, (uint[])f0.P.Clone()); for (int y = 20; y < 30; y++) for (int x = 41; x < 77; x++) f1.P[y * w + x] = 0x80FF0000u;   // 일부만 바뀜 (홀수 x)
        var f2 = new Argb(w, h, (uint[])f1.P.Clone());   // 같은 그림 → 앞 프레임 시간에 합침
        var f3 = Pattern(w, h, 9, true);
        var frames = new List<(Argb, int)> { (f0, 100), (f1, 80), (f2, 120), (f3, 50) };
        var bytes = WebP.EncodeAnimated(frames, 3, true);
        File.WriteAllBytes(Path.Combine(outDir, "anim.webp"), bytes);
        var info = WebP.Probe(bytes);
        Ok(info.Animated && info.W == w && info.H == h && info.Loop == 3 && info.Frames == 3 && info.Alpha, $"움직이는 WebP 머리 (VP8X·ANIM 반복 3·ANMF {info.Frames}개)");
        var back = WebP.DecodeFrames(bytes);
        Ok(back.Count == 3 && Same(back[0].img, f0) && Same(back[1].img, f1) && Same(back[2].img, f3), "프레임마다 합친 화면이 원본과 같음 (바뀐 영역만 넣어도)");
        Ok(back.Select(b => b.ms).SequenceEqual(new[] { 100, 200, 50 }), $"프레임 시간 (같은 그림은 합침: {string.Join(",", back.Select(b => b.ms))})");
        // 웹 판 구조 검사 (selftest.js 와 같은 조건)
        string head = Encoding.ASCII.GetString(bytes, 0, 64);
        Ok(head.StartsWith("RIFF") && head.Contains("WEBPVP8X") && head.Contains("ANIM") && BitConverter.ToInt32(bytes, 4) == bytes.Length - 8, "RIFF 구조 (웹 판 시험과 같은 조건)");
        try
        {
            var wf = Wic.Decode(Path.Combine(outDir, "anim.webp"), 0);
            int bad = 0; for (int i = 0; i < f0.P.Length; i++) if ((f0.P[i] >> 24) != 0 && f0.P[i] != wf.P[i]) bad++;
            Ok(bad == 0, $"WIC 로 움직이는 WebP 첫 장 (다른 픽셀 {bad})");
        }
        catch (Exception ex) { Note("WIC 로 움직이는 WebP 를 풀 수 없음: " + ex.Message); }
    }

    // ------------------------------------------------------------------ MP4
    static uint[] TwoTone(int w, int h, uint top, uint bottom) { var o = new uint[w * h]; for (int i = 0; i < o.Length; i++) o[i] = i / w < h / 2 ? top : bottom; return o; }
    static bool Near(uint a, uint b, int tol) { for (int sh = 0; sh < 24; sh += 8) if (Math.Abs((int)((a >> sh) & 255) - (int)((b >> sh) & 255)) > tol) return false; return true; }
    static uint Avg(uint[] px, int w, int x0, int y0, int x1, int y1)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { uint c = px[y * w + x]; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; n++; }
        return 0xFF000000u | ((uint)(r / n) << 16) | ((uint)(g / n) << 8) | (uint)(b / n);
    }

    static void TestMp4()
    {
        int w = 160, h = 128;
        string path = Path.Combine(outDir, "test.mp4");
        var cols = new[] { (0xFFE03030u, 0xFF3050E0u), (0xFF30C040u, 0xFFF0F0F0u), (0xFF202020u, 0xFFF0C020u) };
        using (var mp = new Mp4Writer(path, w, h, 12, 1, 2_000_000))
        {
            for (int i = 0; i < 12; i++) { var (t, b) = cols[i / 4]; mp.AddFrame(TwoTone(w, h, t, b)); }
            mp.Finish();
        }
        var r = Mp4Probe.Read(path, new[] { 1, 6, 11 });
        Ok(r.W == w && r.H == h, $"MP4 크기 {r.W}×{r.H}");
        Ok(r.Frames == 12, $"MP4 장 수 {r.Frames}");
        Ok(Math.Abs(r.Duration - 1.0) < 0.05, $"MP4 길이 {r.Duration:0.000}초 (12장 ÷ 12fps)");
        Ok(!r.HasAudio, "소리 없는 MP4 에 소리 줄 없음");
        bool colors = true; var msg = new StringBuilder();
        foreach (var (k, (t, b)) in new[] { (1, cols[0]), (6, cols[1]), (11, cols[2]) })
        {
            if (!r.Pixels.TryGetValue(k, out var px)) { colors = false; msg.Append($" {k}장 없음"); continue; }
            uint at = Avg(px, w, 20, 10, w - 20, h / 2 - 10), ab = Avg(px, w, 20, h / 2 + 10, w - 20, h - 10);
            colors &= Near(at, t, 24) && Near(ab, b, 24);
            msg.Append($" {k}장 위 #{at & 0xffffff:x6}/아래 #{ab & 0xffffff:x6}");
        }
        Ok(colors, "MP4 색·위아래 방향이 맞음:" + msg);
        // 16 의 배수가 아닌 크기
        foreach (var (sw, shh) in new[] { (120, 90), (200, 150), (122, 92) })
        {
            string p2 = Path.Combine(outDir, $"test_{sw}x{shh}.mp4");
            // 위 빨강·아래 파랑, 오른쪽 위 1/4 은 노랑 (줄 간격이 틀리면 비스듬히 밀려서 드러남)
            var q = TwoTone(sw, shh, 0xFFE03030u, 0xFF3050E0u);
            for (int y = 0; y < shh / 2; y++) for (int x = sw / 2; x < sw; x++) q[y * sw + x] = 0xFFF0D020u;
            using (var mp = new Mp4Writer(p2, sw, shh, 10, 1, 2_000_000)) { for (int i = 0; i < 5; i++) mp.AddFrame(q); mp.Finish(); }
            var r2 = Mp4Probe.Read(p2, new[] { 2 });
            uint at2 = Avg(r2.Pixels[2], sw, 6, 6, sw / 2 - 6, shh / 2 - 6), ay2 = Avg(r2.Pixels[2], sw, sw / 2 + 6, 6, sw - 6, shh / 2 - 6), ab2 = Avg(r2.Pixels[2], sw, 6, shh / 2 + 6, sw - 6, shh - 6);
            Ok(r2.W == sw && r2.H == shh && r2.Frames == 5 && Near(at2, 0xFFE03030u, 24) && Near(ay2, 0xFFF0D020u, 24) && Near(ab2, 0xFF3050E0u, 24), $"MP4 {sw}×{shh} (16 배수 아님) #{at2 & 0xffffff:x6} #{ay2 & 0xffffff:x6} #{ab2 & 0xffffff:x6}");
        }
        Ok(Mp4Writer.FitSize(2480, 3508) == (2480, 3508) && Mp4Writer.FitSize(6000, 8000) is var (fw, fh) && fw % 2 == 0 && fh <= 4096 && (long)fw * fh <= 4096L * 2304, "H.264 크기 맞추기 (짝수·4096 이하)");
    }

    static void TestMp4Audio()
    {
        int rate = 44100, n = rate * 3 / 2;
        var pcm = new PcmAudio { Rate = rate, Channels = 2, Data = new short[n * 2] };
        for (int i = 0; i < n; i++) { short v = (short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 12000); pcm.Data[i * 2] = v; pcm.Data[i * 2 + 1] = (short)-v; }
        string path = Path.Combine(outDir, "audio.mp4");
        using (var mp = new Mp4Writer(path, 64, 48, 10, 1, 1_000_000, pcm))
        {
            for (int i = 0; i < 10; i++) mp.AddFrame(TwoTone(64, 48, 0xFF000000u + (uint)(i * 20 << 8), 0xFFFFFFFF));
            mp.Finish();
        }
        var r = Mp4Probe.Read(path);
        Ok(r.HasAudio && r.Frames == 10, $"MP4 소리 줄 (AAC) + 영상 {r.Frames}장");
        double sec = r.AudioSeconds;
        Ok(sec > 0.8 && sec < 1.2, $"소리 길이는 영상 길이(1초)만큼 ({sec:0.00}초)");
    }

    // ------------------------------------------------------------------ 애니 (문서 → 내보내기)
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

    static void TestAnimExport()
    {
        var d = AnimDoc(3, 10);
        d.Anim["ms"] = new JsonArray(100, 300, 100);   // 둘째 프레임은 3배 길게
        var o = new ExportOpts { Fmt = "webp", Scale = 1, Loop = 0 };
        var webp = AnimMedia.EncodeWebP(d, o, 1);
        var back = WebP.DecodeFrames(webp);
        var expect = AnimExport.Frames(d, o);
        bool same = back.Count == 3;
        for (int i = 0; same && i < 3; i++) same &= Same(back[i].img, Argb.FromPremul(expect[i].img.P, expect[i].img.W, expect[i].img.H));
        Ok(same && back.Select(b => b.ms).SequenceEqual(new[] { 100, 300, 100 }), $"애니 → WebP 프레임·시간 ({string.Join(",", back.Select(b => b.ms))})");
        var tr = AnimMedia.EncodeWebP(d, new ExportOpts { Fmt = "webp", Scale = 1, Transparent = true }, 1);
        var tb = WebP.DecodeFrames(tr);
        Ok(WebP.Probe(tr).Alpha && (tb[0].img.P[0] >> 24) == 0 && (tb[0].img.P[45 * 120 + 20] >> 24) == 255, "배경 투명 WebP (종이 없이)");
        // MP4: 10fps, 1+3+1 = 5장, 0.5초 < 2초 → 4번 반복 = 20장
        string path = AnimMedia.ExportMp4(d, 1, null, Path.Combine(outDir, "anim.mp4"));
        var r = Mp4Probe.Read(path, new[] { 0, 2 });
        Ok(r.W == 120 && r.H == 90 && r.Frames == 20 && Math.Abs(r.Duration - 2.0) < 0.06, $"애니 → MP4 (짧으면 2초 이상 반복: {r.Frames}장 {r.Duration:0.00}초)");
        if (r.Pixels.TryGetValue(2, out var p2))
        {
            uint c = Avg(p2, 120, 37, 32, 63, 58);   // 둘째 프레임 셀 (35~65, 30~60), 색 (100,0,0)
            Ok(Near(c, 0xFF640000u, 24), $"MP4 둘째 프레임 색 #{c & 0xffffff:x6}");
        }
        else Ok(false, "MP4 둘째 프레임을 읽지 못함");
        // 소리 (WAV, 0.2초 늦게 시작) 가 붙은 애니
        var wav = new MemoryStream();
        using (var bw = new BinaryWriter(wav, Encoding.ASCII, true))
        {
            int rate = 22050, n = rate;   // 1초, 22.05kHz → 48kHz 로 바꿔 넣음
            bw.Write("RIFF"u8.ToArray()); bw.Write(36 + n * 2); bw.Write("WAVE"u8.ToArray()); bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
            bw.Write("data"u8.ToArray()); bw.Write(n * 2);
            for (int i = 0; i < n; i++) bw.Write((short)(Math.Sin(i * 2 * Math.PI * 300 / rate) * 8000));
        }
        d.Anim["audio"] = new JsonObject { ["name"] = "t.wav", ["data"] = "data:audio/wav;base64," + Convert.ToBase64String(wav.ToArray()), ["offset"] = 0.2, ["volume"] = 1 };
        var a = AnimMedia.AudioFor(d, 0.5);
        Ok(a != null && a.Rate == 48000 && a.Channels == 1 && a.Frames == 24000 && a.Data.Take(9000).All(s => s == 0) && a.Data.Skip(10000).Take(5000).Any(s => Math.Abs((int)s) > 4000), "소리 → 48kHz PCM (시작 위치 0.2초 반영)");
        string pa = AnimMedia.ExportMp4(d, 1, null, Path.Combine(outDir, "anim_audio.mp4"));
        var ra = Mp4Probe.Read(pa);
        Ok(ra.HasAudio && ra.Frames == 5, $"소리 붙은 애니 → MP4 (소리 있으면 반복 안 함: {ra.Frames}장, 소리 {ra.HasAudio})");
        string pt = AnimMedia.ExportMp4(d, 1, null, Path.Combine(outDir, "anim_noaudio.mp4"), withAudio: false);
        Ok(!Mp4Probe.Read(pt).HasAudio, "소리 빼고 만들기");
        Ok(AnimMedia.Rate(12) == (12, 1) && AnimMedia.Rate(12.5) == (12500, 1000), "fps → 분자/분모");
    }

    // ------------------------------------------------------------------ 타임랩스
    static void TestTimelapse()
    {
        var plan = TimelapseMedia.Plan(7, 2, 1);
        Ok(plan.Count == 90 && plan[0] == 0 && plan[59] == 6 && plan.Skip(60).All(i => i == 6) && plan.Take(60).Distinct().Count() == 7, "장면 순서 (웹 판: 30fps × 길이 + 마지막 그림 hold)");
        string dir = Path.Combine(outDir, "tl"); Directory.CreateDirectory(dir);
        var keys = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            using var b = new Bitmap(200, 150);
            using (var g = Graphics.FromImage(b)) { g.Clear(Color.White); g.FillRectangle(Brushes.Black, 0, 0, 40 * (i + 1), 150); }
            string p = Path.Combine(dir, $"{i:000000}.jpg"); b.Save(p, System.Drawing.Imaging.ImageFormat.Jpeg); keys.Add(p);
        }
        string mp4 = Path.Combine(outDir, "timelapse.mp4");
        TimelapseMedia.MakeMp4(keys, mp4, 1, 0.5);
        var r = Mp4Probe.Read(mp4, new[] { 44 });
        Ok(r.W == 200 && r.H == 150 && r.Frames == 45 && Math.Abs(r.Duration - 1.5) < 0.05, $"타임랩스 MP4 ({r.Frames}장 {r.Duration:0.00}초)");
        if (r.Pixels.TryGetValue(44, out var last)) Ok(Near(Avg(last, 200, 170, 60, 190, 90), 0xFF000000u, 30), "마지막 장 = 완성 그림");
        string gif = Path.Combine(outDir, "timelapse.gif");
        TimelapseMedia.MakeGif(keys, gif, 1, 0.5);
        using (var gi = Image.FromFile(gif))
        {
            int n = gi.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time);
            Ok(gi.Width == 200 && n == 5, $"타임랩스 GIF (같은 장면은 합침: {n}장)");
        }
    }

    // ------------------------------------------------------------------ 인쇄 (작품 전체)
    static void TestPrint()
    {
        string story = "인쇄시험";
        for (int i = 1; i <= 3; i++)
        {
            var p = Story.BlankPage(300, 420, $"{story} {i}쪽", true);
            p.Dpi = 300;
            Story.SavePageDoc(p, $"{story}/{Story.PageName(i)}.cpad");
        }
        var cur = CpadIO.Load(Story.Full($"{story}/p002.cpad"));
        cur.FilePath = Story.Full($"{story}/p002.cpad"); cur.Modified = false;
        App.SetDoc(cur);
        var list = PrintBook.PageDocs(true);
        Ok(list.Count == 3, $"작품 페이지 목록 ({list.Count}쪽)");
        foreach (var mode in new[] { "mono", "gray", "color" })
        {
            string pdf = PrintBook.ExportPages(list.Select(x => x.load).ToList(), "시험_" + mode, new PrintOpts { Fmt = "pdf", Mode = mode }, outDir);
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(pdf));
            int pages = System.Text.RegularExpressions.Regex.Matches(text, @"/Type /Page /Parent").Count;
            Ok(pages == 3 && text.Contains("/Count 3") && text.Contains("/MediaBox [0 0 72.00 100.80]") && text.TrimEnd().EndsWith("%%EOF"), $"PDF {mode} 3쪽 (300dpi 300×420px = 72×100.8pt)");
            // xref 위치가 맞는지
            int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
            int xref = int.Parse(text[(sx + 10)..].Split('\n')[0]);
            Ok(text.Substring(xref, 4) == "xref", $"PDF {mode} xref 위치");
        }
        string tif = PrintBook.ExportPages(list.Select(x => x.load).ToList(), "시험_tif", new PrintOpts { Fmt = "tif", Mode = "mono" }, outDir);
        Ok(Directory.GetFiles(tif, "*.tif").Length == 3 && File.ReadAllBytes(Path.Combine(tif, "003.tif"))[0] == 0x49, "TIFF 쪽마다 3장");
        Ok(PrintBook.PageDocs(false).Count == 1, "지금 페이지만");
    }

    static void TestSpread()
    {
        string S(List<int[]> l) => string.Join("|", l.Select(a => string.Join(",", a)));
        Ok(S(PrintBook.Spreads(5)) == "0|1,2|3,4" && S(PrintBook.Spreads(4)) == "0|1,2|3" && S(PrintBook.Spreads(1)) == "0", "펼침 묶음 (1쪽 혼자, 이후 2·3 …)");
        string L(int?[] a) => string.Join(",", a.Select(x => x?.ToString() ?? "_"));
        Ok(L(PrintBook.Layout(new[] { 0 }, false)) == "_,0" && L(PrintBook.Layout(new[] { 0 }, true)) == "0,_" && L(PrintBook.Layout(new[] { 1, 2 }, false)) == "1,2" && L(PrintBook.Layout(new[] { 1, 2 }, true)) == "2,1", "넘김 방향 (한국식 첫 쪽은 오른쪽, 일본식은 반대)");
        var d = Story.BlankPage(300, 420, "펼침", true);
        d.Extra["print"] = new JsonObject { ["trimW"] = 280, ["trimH"] = 400, ["bleed"] = 10, ["safe"] = 20 };
        using var pg = PrintBook.PageImage(d, 200);
        Ok(pg.Height == 200 && pg.Width == 140, $"쪽 그림 (재단선 안, 높이 200: {pg.Width}×{pg.Height})");
        using var sp = PrintBook.Compose(new[] { pg, pg }, new int?[] { null, 0 }, 200);
        Ok(sp.Width == 282 && sp.GetPixel(10, 100).R == 48 && sp.GetPixel(200, 100).R > 200, "펼침면 그림 (빈 쪽 + 첫 쪽)");
    }

    // ------------------------------------------------------------------ 정지 그림
    static void TestStill()
    {
        var d = new Document(200, 100) { Name = "정지" };
        var l = new Layer(d, "그림"); d.Layers.Add(l);
        l.Px.Write(new Rect(50, 25, 100, 50), Enumerable.Repeat(0xFF2040C0u, 5000).ToArray());
        var draft = new Layer(d, "러프") { Draft = true }; d.Layers.Add(draft);
        draft.Px.Write(new Rect(0, 0, 10, 10), Enumerable.Repeat(0xFFFF0000u, 100).ToArray());
        var t = StillExport.Render(d, 0.5, true);
        Ok(t.W == 100 && t.H == 50 && (t.P[0] >> 24) == 0 && t.P[25 * 100 + 50] == 0xFF2040C0u, "투명 50% (용지·밑그림 빼고)");
        var j = StillExport.Render(d, 2, false, "all", 0xFFFFFFFF);
        Ok(j.W == 400 && j.H == 200 && j.P[0] == 0xFFFFFFFFu && Near(j.P[100 * 400 + 200], 0xFF2040C0u, 2), "흰 바탕 200%");
        d.Extra["print"] = new JsonObject { ["trimW"] = 160, ["trimH"] = 80, ["bleed"] = 5, ["safe"] = 5 };
        Ok(StillExport.Render(d, 1, false, "trim").W == 160 && StillExport.Render(d, 1, false, "bleed").H == 90, "재단선·도련 범위");
        string p = Path.Combine(outDir, "still_export.webp");
        File.WriteAllBytes(p, StillExport.Encode(t, "webp"));
        var o = StillExport.OpenWebP(p);
        var px = o.Layers[0].Px.Read(new Rect(0, 0, 100, 50));
        Ok(o.W == 100 && o.H == 50 && o.Name == "still_export" && px[25 * 100 + 50] == 0xFF2040C0u && px[0] == 0, "WebP 열기 → 새 작품 (투명 유지)");
        Ok(FileCommands.Openers.TryGetValue(".webp", out var op) && op == StillExport.OpenWebP, ".webp 열기 등록");
        // 웹 판이 열 수 있게 .cpad 로 저장했다가 다시 열기
        string cp = Path.Combine(outDir, "from_webp.cpad");
        CpadIO.Save(o, cp);
        Ok(CpadIO.Load(cp).Layers[0].Px.Get(50, 25) == 0xFF2040C0u, ".cpad 저장·열기");
        // 작품 전체 내보내기 PSD·WebP
        string story = "인쇄시험";
        var files = Story.ListPages(story).Select(Story.Full).ToList();
        string od = Path.Combine(outDir, "story_psd");
        StillExport.ExportPages(files, od, "psd", 1);
        var psd = File.ReadAllBytes(Path.Combine(od, "001.psd"));
        Ok(Directory.GetFiles(od, "*.psd").Length == 3 && Encoding.ASCII.GetString(psd, 0, 4) == "8BPS", "작품 전체 → PSD 3개");
        string ow = Path.Combine(outDir, "story_webp");
        StillExport.ExportPages(files, ow, "webp", 0.5);
        var wi = WebP.Probe(File.ReadAllBytes(Path.Combine(ow, "002.webp")));
        Ok(wi.W == 150 && wi.H == 210, "작품 전체 → WebP 50%");
    }

    // ------------------------------------------------------------------ 전자책
    static void TestEbook()
    {
        var pages = Story.ListPages("인쇄시험").Select(p => Ebook.PageImage(CpadIO.Load(Story.Full(p)), 2400, "jpg")).ToList();
        var epub = Ebook.BuildEPUB(pages, "인쇄시험", "", false);
        File.WriteAllBytes(Path.Combine(outDir, "story.epub"), epub);
        using var z = new ZipArchive(new MemoryStream(epub));
        var names = z.Entries.Select(e => e.FullName).ToList();
        Ok(names[0] == "mimetype" && z.Entries[0].CompressedLength == z.Entries[0].Length && names.Contains("META-INF/container.xml") && names.Count(n => n.EndsWith(".xhtml")) == 4, $"EPUB 구조 (mimetype 맨 앞·압축 없음, 쪽 3 + 목차)");
        string opf = new StreamReader(z.GetEntry("OEBPS/content.opf").Open()).ReadToEnd();
        Ok(System.Text.RegularExpressions.Regex.Matches(opf, "<itemref ").Count == 3 && opf.Contains("page-progression-direction=\"ltr\""), "EPUB spine 3쪽");
    }

    // ------------------------------------------------------------------ 메뉴
    static void TestMenus()
    {
        string[] must =
        {
            "애니/애니메이션 내보내기 (GIF·WebP·APNG·MP4·PNG)…", "애니/동영상으로 내보내기 (MP4)…", "보기/펼침면 미리보기 (책처럼 보기)",
            "파일/인쇄용 내보내기 (PDF ∕ 1비트 TIFF)…", "보기/타임랩스 동영상 만들기…", "파일/그림으로 내보내기 (PNG·JPG)…", "만화/작품 전체 내보내기…",
        };
        var missing = must.Where(m => !Registry.Menus.Any(x => x.Path == m)).ToList();
        Ok(missing.Count == 0, "메뉴 등록" + (missing.Count > 0 ? " (없음: " + string.Join(", ", missing) + ")" : ""));
        Ok(!Registry.Menus.Any(x => x.Path == "애니/애니메이션 내보내기 (GIF·APNG·PNG)…"), "예전 애니 내보내기 메뉴는 새 창으로 바뀜");
        Ok(Registry.Menus.Find(x => x.Path == "파일/그림으로 내보내기 (PNG·JPG)…")?.Shortcut == (Keys.Control | Keys.Shift | Keys.E), "이미지 내보내기 단축키 유지");
        Ok(must.All(m => m.Count(c => c == '/') == 1), "메뉴 이름에 '/' 없음 (∕ 사용)");
    }
}
