// 립싱크 보조 — 웹 판 app/js/core/lipsync.js
// 붙인 소리를 프레임마다 분석해 입 모양(닫힘·반·ㅏ·ㅣ·ㅜ)을 고르고, 입 트랙의 노출표에 자동으로 배치한다.
// 소리 크기 + 대역별 에너지(낮음·가운데·높음)로 모음을 대략 나눈다 (정확한 음성 인식은 아님). 네이티브 판은 WAV 소리만.
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public static class LipSync
{
    public static readonly (string id, string label)[] Shapes =
    {
        ("closed", "닫힘 (소리 없음)"), ("half", "반쯤 (작은 소리·자음)"), ("a", "ㅏ (크게 벌림)"), ("i", "ㅣ·ㅔ (옆으로)"), ("u", "ㅜ·ㅗ (둥글게)"),
    };
    // 그림이 없는 모양은 가까운 모양으로 대신
    static readonly Dictionary<string, string[]> Fallback = new()
    {
        ["half"] = new[] { "closed", "a" }, ["a"] = new[] { "half", "i", "u" }, ["i"] = new[] { "a", "half" }, ["u"] = new[] { "a", "half" }, ["closed"] = new[] { "half" },
    };

    public struct Feat { public double Vol, Low, Mid, High; }

    /// <summary>실수 FFT (크기만), n 은 2의 거듭제곱</summary>
    static float[] FftMag(float[] re)
    {
        int n = re.Length;
        var im = new float[n];
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (re[i], re[j]) = (re[j], re[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len; double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = (float)(re[a] - tr); im[b] = (float)(im[a] - ti); re[a] += (float)tr; im[a] += (float)ti;
                    double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
        var o = new float[n / 2];
        for (int i = 0; i < n / 2; i++) o[i] = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
        return o;
    }

    /// <summary>프레임마다 { vol, low, mid, high } (vol 은 0~1 로 맞춤)</summary>
    public static List<Feat> Analyze(Document d, int nFrames)
    {
        var pcm = Sound.Decode(d) ?? throw new InvalidOperationException("붙인 소리가 없거나 읽을 수 없습니다 (애니 패널 > 소리 넣기, WAV)");
        int sr = pcm.Rate; var ch = pcm.Mono; double off = J.D(Sound.Audio(d)["offset"], 0);
        const int N = 2048;
        var win = new float[N];
        for (int i = 0; i < N; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)));
        int Bin(double hz) => Math.Max(1, Math.Min(N / 2 - 1, (int)Math.Round(hz * N / sr)));
        (int, int) bl = (Bin(150), Bin(700)), bm = (Bin(700), Bin(1800)), bh = (Bin(1800), Bin(5000));
        double t = 0;
        var o = new List<Feat>();
        for (int f = 0; f < nFrames; f++)
        {
            double dur = AnimCore.FrameMs(f, d) / 1000;
            int a = (int)Math.Floor((t - off) * sr), b = (int)Math.Floor((t + dur - off) * sr);
            t += dur;
            double rms = 0; int cnt = 0;
            for (int k = Math.Max(0, a); k < Math.Min(ch.Length, b); k++) { rms += ch[k] * ch[k]; cnt++; }
            rms = cnt > 0 ? Math.Sqrt(rms / cnt) : 0;
            // 프레임 가운데 둘레로 FFT
            int c = (int)Math.Floor((a + b) / 2.0) - N / 2;
            var seg = new float[N];
            for (int i = 0; i < N; i++) { int k = c + i; seg[i] = k >= 0 && k < ch.Length ? ch[k] * win[i] : 0; }
            var mag = FftMag(seg);
            double Band((int lo, int hi) r) { double s = 0; for (int i = r.lo; i < r.hi; i++) s += mag[i] * (double)mag[i]; return s; }
            o.Add(new Feat { Vol = rms, Low = Band(bl), Mid = Band(bm), High = Band(bh) });
        }
        // 소리 크기는 큰 쪽 95% 값을 1 로
        var sorted = o.Select(x => x.Vol).OrderBy(x => x).ToList();
        double rf = sorted.Count > 0 ? sorted[(int)Math.Floor(sorted.Count * 0.95)] : 0;
        if (rf == 0) rf = 1e-6;
        for (int i = 0; i < o.Count; i++) { var x = o[i]; x.Vol = Math.Min(1, x.Vol / Math.Max(1e-6, rf)); o[i] = x; }
        return o;
    }

    /// <summary>특징 → 입 모양</summary>
    public static List<string> Classify(List<Feat> feats, double quiet = 0.12, double small = 0.32) => feats.Select(x =>
    {
        if (x.Vol < quiet) return "closed";
        if (x.Vol < small) return "half";
        double sum = x.Low + x.Mid + x.High; if (sum == 0) sum = 1;
        double l = x.Low / sum, h = x.High / sum, m = x.Mid / sum;
        if (h + m * 0.5 > 0.55 && h > 0.18) return "i";         // 높은 소리가 많으면 옆으로 (ㅣ·ㅔ)
        if (l > 0.72 && h < 0.08) return "u";                   // 낮은 소리만 크면 둥글게 (ㅜ·ㅗ)
        return "a";
    }).ToList();

    /// <summary>너무 짧게 바뀌면 깜빡여 보이므로, minHold 장보다 짧은 모양은 앞 모양에 붙인다</summary>
    public static List<string> Smooth(List<string> shapes, int minHold = 2)
    {
        var s = shapes.ToList();
        int i = 0;
        while (i < s.Count)
        {
            int j = i;
            while (j < s.Count && s[j] == s[i]) j++;
            if (j - i < minHold && i > 0) for (int k = i; k < j; k++) s[k] = s[i - 1];
            i = j;
        }
        return s;
    }

    /// <summary>모양 → 셀 id (그림이 없는 모양은 가까운 모양으로)</summary>
    public static string Resolve(Dictionary<string, string> map, string shape)
    {
        if (map.TryGetValue(shape, out var v) && v != null) return v;
        foreach (var alt in Fallback.GetValueOrDefault(shape) ?? Array.Empty<string>()) if (map.TryGetValue(alt, out var w) && w != null) return w;
        return map.Values.FirstOrDefault(x => x != null);
    }

    /// <summary>입 트랙에 자동 배치. extend = 소리 길이만큼 프레임 늘리기</summary>
    public static (int frames, Dictionary<string, int> count) Apply(Layer track, Dictionary<string, string> map, bool extend = false, int minHold = 2)
    {
        var d = App.Doc;
        int n = AnimCore.FrameCount(d);
        if (extend)
        {
            var pcm = Sound.Decode(d) ?? throw new InvalidOperationException("소리를 읽을 수 없습니다");
            double total = (pcm.Duration + J.D(Sound.Audio(d)["offset"], 0)) * 1000, t = 0;
            int k = 0;
            while (t < total && k < 10000) { t += AnimCore.FrameMs(k, d); k++; }
            n = Math.Max(n, k);
        }
        var shapes = Smooth(Classify(Analyze(d, n)), minHold);
        AnimCore.Edit("립싱크 자동 배치", tx =>
        {
            // 늘어난 프레임에서 다른 트랙은 마지막 그림을 이어서 보여 줌
            if (n > AnimCore.FrameCount(d))
            {
                foreach (var t in AnimCore.Tracks(d)) { var xs = tx.Xs(t); string lastId = xs.Count > 0 ? xs[^1] : null; while (xs.Count < n) xs.Add(t == track ? null : lastId); }
                AnimCore.A(d)["length"] = n;
            }
            var lm = new JsonObject();
            foreach (var (kk, vv) in map) lm[kk] = vv;
            track.Meta["lipMap"] = lm;
            var x2 = tx.Xs(track);
            while (x2.Count < n) x2.Add(null);
            for (int f = 0; f < n; f++) x2[f] = Resolve(map, shapes[f]);
        });
        var count = new Dictionary<string, int>();
        foreach (var s in shapes) count[s] = count.GetValueOrDefault(s) + 1;
        return (n, count);
    }
}
