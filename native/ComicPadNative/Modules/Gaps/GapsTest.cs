// 남은 기능(손실 WebP·WebM·버튜버 녹화·눈동자 추적·소리 형식·펜 예측·두 번째 측면 버튼·출시 꾸러미) 시험 — 창 없이, 정해진 결과.
// 실행: COMICPAD_DATA=임시 폴더 로 --selftest gaps 결과폴더 → 결과폴더/report.txt, 끝 코드 = 실패 수.
// 사용자 설정·작업물 폴더·웹캠·마이크는 쓰지 않는다.
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Modules.Media;

namespace ComicPadNative.Modules.Gaps;

public static partial class GapsTest
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
            App.Settings = new JsonObject();
            void T(string n, Action a)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { a(); } catch (Exception ex) { Ok(false, $"{n}: 예외 {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}"); }
                log.AppendLine($"      ({n} {sw.ElapsedMilliseconds}ms)");
            }
            string only = Environment.GetEnvironmentVariable("COMICPAD_GAPS_ONLY");
            foreach (var (name, act) in Tests())
                if (string.IsNullOrEmpty(only) || only.Split(',').Any(o => name.Contains(o))) T(name, act);
        }
        catch (Exception ex) { Ok(false, "시험 실행 오류: " + ex); }
        log.AppendLine(fail == 0 ? "모두 통과" : $"실패 {fail}개");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        return fail;
    }

    static IEnumerable<(string, Action)> Tests()
    {
        yield return ("VP8 손실 WebP", TestVp8);
        yield return ("VP8 libwebp 참고 파일 (내장)", TestVp8Embedded);
        yield return ("VP8 참고 파일 (libwebp 가 만든 것)", TestVp8Reference);
        yield return ("WebM 기본", TestWebmBasic);
        yield return ("WebM 사이 프레임", TestWebmInter);
    }

    // ------------------------------------------------------------------ 시험 그림
    /// <summary>사진 비슷한 그림: 부드러운 그러데이션 + 원 + 선 + 약한 잡음 (+ 투명 테두리)</summary>
    internal static Argb Natural(int w, int h, int seed, bool alpha = false)
    {
        var rnd = new Random(seed);
        var im = new Argb(w, h);
        double cx = w * 0.6, cy = h * 0.45, rr = Math.Min(w, h) * 0.3;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double r = 128 + 100 * Math.Sin(x * 0.02 + seed), g = 128 + 90 * Math.Cos(y * 0.025), b = 60 + 150.0 * x / w;
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d < rr) { r = 230 - d * 0.3; g = 180 - d * 0.5; b = 90; }
                if (Math.Abs(x - y * 0.7 - 20) < 1.5 || (y % 37) < 2) { r = g = b = 20; }   // 선
                r += rnd.NextDouble() * 6 - 3; g += rnd.NextDouble() * 6 - 3; b += rnd.NextDouble() * 6 - 3;
                uint a = 255;
                if (alpha) { double e = Math.Min(Math.Min(x, w - 1 - x), Math.Min(y, h - 1 - y)); a = e < 6 ? 0 : e < 14 ? (uint)((e - 6) * 255 / 8) : 255; }
                im.P[y * w + x] = (a << 24) | ((uint)Math.Clamp((int)r, 0, 255) << 16) | ((uint)Math.Clamp((int)g, 0, 255) << 8) | (uint)Math.Clamp((int)b, 0, 255);
            }
        return im;
    }

    /// <summary>보이는(알파 &gt; 0) 픽셀만 PSNR (투명한 곳 색은 따지지 않음)</summary>
    internal static double PsnrVisible(Argb a, Argb b)
    {
        double s = 0; long n = 0;
        for (int i = 0; i < a.P.Length; i++)
        {
            uint x = a.P[i], y = b.P[i];
            if ((x >> 24) == 0) continue;
            for (int sh = 0; sh < 24; sh += 8) { int d = (int)((x >> sh) & 255) - (int)((y >> sh) & 255); s += d * d; n++; }
        }
        return s == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * n / s);
    }
}
