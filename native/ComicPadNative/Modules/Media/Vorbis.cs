// Vorbis 소리 부호기 (WebM 소리용, 웹 판 MediaRecorder 의 Opus 대신) — Vorbis I 규격대로 직접 짬, 외부 코덱 없음.
// 단순하지만 규격에 맞는 구성:
//  - 블록: 긴 블록(2048)만, 창은 Vorbis 창 (짧은 블록 256 은 머리에만 있고 쓰지 않음)
//  - MDCT: 2N 길이 FFT 로 (정확한 식), 디코더 IMDCT 는 곱 없음 → 앞쪽에서 2/M (Princen-Bradley 창 + 겹쳐 더하기로 원래 크기)
//  - 바닥(floor 1): 30점, 그 칸의 가장 큰 값 ÷ K 를 덮는 높이 (= 양자화 단계), 디코더의 예측·선 긋기를 그대로 따라 계산
//  - 나머지(residue 1): 16칸 묶음, 묶음이 모두 0 이면 0 부류(아무것도 안 씀), 아니면 2차원 격자 책(-K..K)
//  - 부호책 길이는 실제 통계로 (전부 부호화한 뒤 허프만) — 그래서 소리 전체를 한 번에 받음 (녹화도 끝에 한꺼번에)
// 화질 → K(나머지 범위)·바닥 잡음·고음 자르기.
namespace ComicPadNative.Modules.Media;

public sealed class VorbisStream
{
    public byte[][] Headers;                       // 식별·주석·설정
    public List<(long ms, byte[] pkt)> Packets = new();
    public int Rate, Channels; public long Samples;
}

public static class Vorbis
{
    const int N = 2048, M = 1024, Mult = 2, Range = 128, PartSize = 16;
    static readonly int[] Posts = { 0, 1024, 2, 4, 6, 8, 12, 16, 20, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 640, 768, 896 };
    const int ClassDim = 4;
    static readonly double[] InvDb = Enumerable.Range(0, 256).Select(i => Math.Pow(1.0649863, i - 255)).ToArray();   // 규격 표와 같은 값 (등비)
    static readonly double[] Window = Enumerable.Range(0, N).Select(i => Math.Sin(Math.PI / 2 * Math.Pow(Math.Sin((i + 0.5) / N * Math.PI), 2))).ToArray();
    static readonly int[] Low = new int[Posts.Length], High = new int[Posts.Length];
    static readonly int[] Sorted;

    static Vorbis()
    {
        for (int j = 2; j < Posts.Length; j++)
        {
            int lo = 0, hi = 1;
            for (int k = 0; k < j; k++)
            {
                if (Posts[k] < Posts[j] && Posts[k] > Posts[lo]) lo = k;
                if (Posts[k] > Posts[j] && Posts[k] < Posts[hi]) hi = k;
            }
            Low[j] = lo; High[j] = hi;
        }
        Sorted = Enumerable.Range(0, Posts.Length).OrderBy(i => Posts[i]).ToArray();
    }

    // ------------------------------------------------------------------ MDCT (2N 길이 FFT 로 정확히)
    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }

    /// <summary>창 씌운 N 개 → M 개 계수 (디코더 IMDCT 와 짝이 맞게 2/M)</summary>
    internal static double[] Mdct(double[] x)
    {
        // (a,b,c,d) → DCT-IV 입력 (−c_r − d, a − b_r)
        var v = new double[M];
        int h = M / 2;
        for (int n = 0; n < h; n++)
        {
            v[n] = -x[3 * h - 1 - n] - x[3 * h + n];          // −c_r − d
            v[h + n] = x[n] - x[M - 1 - n];                   // a − b_r
        }
        // DCT-IV: X_k = Re( e^{−iπ(2k+1)/(4M)} · FFT_2M( v_n e^{−iπ n/(2M)} )_k )
        var re = new double[2 * M]; var im = new double[2 * M];
        for (int n = 0; n < M; n++) { double a = -Math.PI * n / (2 * M); re[n] = v[n] * Math.Cos(a); im[n] = v[n] * Math.Sin(a); }
        Fft(re, im);
        var o = new double[M];
        for (int k = 0; k < M; k++)
        {
            double a = -Math.PI * (2 * k + 1) / (4 * M);
            o[k] = (re[k] * Math.Cos(a) - im[k] * Math.Sin(a)) * 2 / M;
        }
        return o;
    }

    /// <summary>디코더 쪽 IMDCT (시험용, 곱 없음)</summary>
    internal static double[] ImdctSlow(double[] X)
    {
        var y = new double[N];
        for (int i = 0; i < N; i++)
        {
            double acc = 0;
            for (int j = 0; j < M; j++) acc += X[j] * Math.Cos(Math.PI / 2 / N * (2 * i + 1 + N / 2.0) * (2 * j + 1));
            y[i] = acc;
        }
        return y;
    }
    internal static double[] Win => Window;

    // ------------------------------------------------------------------ 바닥 (floor 1) — 디코더와 같은 계산
    static int Predict(int x, int x0, int x1, int y0, int y1)
    {
        int dy = y1 - y0, adx = x1 - x0, err = Math.Abs(dy) * (x - x0), off = err / adx;
        return dy < 0 ? y0 - off : y0 + off;
    }

    static void DrawLine(int[] o, int x0, int y0, int x1, int y1, int n)
    {
        int dy = y1 - y0, adx = x1 - x0, ady = Math.Abs(dy), b = dy / adx, sy = dy < 0 ? b - 1 : b + 1, x = x0, y = y0, err = 0;
        ady -= Math.Abs(b) * adx;
        if (x1 > n) x1 = n;
        if (x < x1)
        {
            o[x] = y;
            for (++x; x < x1; ++x)
            {
                err += ady;
                if (err >= adx) { err -= adx; y += sy; } else y += b;
                o[x] = y;
            }
        }
    }

    /// <summary>원하는 높이 → (부호화할 값들, 디코더가 얻는 최종 높이·쓰임 표시)</summary>
    static (int[] vals, int[] finalY, bool[] used) EncodeFloor(int[] target)
    {
        int n = Posts.Length;
        var fy = new int[n]; var used = new bool[n]; var vals = new int[n];
        fy[0] = target[0]; fy[1] = target[1]; vals[0] = target[0]; vals[1] = target[1]; used[0] = used[1] = true;
        for (int j = 2; j < n; j++)
        {
            int lo = Low[j], hi = High[j];
            int pred = Predict(Posts[j], Posts[lo], Posts[hi], fy[lo], fy[hi]);
            int hiroom = Range - pred, loroom = pred, room = (hiroom < loroom ? hiroom : loroom) * 2;
            int t = target[j];
            if (t == pred && t < Range - 1) t++;   // 0 은 "안 씀"이 되어 선이 건너뛰므로 피함
            int val;
            if (t == pred) val = 0;
            else if (t > pred) { int d = t - pred; val = 2 * d < room ? 2 * d : d + loroom; }
            else { int d = pred - t; val = 2 * d - 1 < room ? 2 * d - 1 : d + hiroom - 1; }
            // 디코더 규칙으로 다시 풀어 확인
            int got;
            if (val == 0) got = pred;
            else if (val >= room) got = hiroom > loroom ? val - loroom + pred : pred - val + hiroom - 1;
            else got = (val & 1) != 0 ? pred - ((val + 1) >> 1) : pred + (val >> 1);
            vals[j] = val; fy[j] = got;
            if (val != 0) { used[lo] = used[hi] = used[j] = true; }
        }
        return (vals, fy, used);
    }

    /// <summary>디코더가 그리는 바닥 곡선 (0~255 높이 번호)</summary>
    static int[] RenderFloor(int[] fy, bool[] used)
    {
        var o = new int[M];
        int lx = 0, ly = fy[0] * Mult;
        for (int q = 1; q < Posts.Length; q++)
        {
            int j = Sorted[q];
            if (!used[j]) continue;
            int hy = fy[j] * Mult, hx = Posts[j];
            if (lx != hx) DrawLine(o, lx, ly, hx, hy, M);
            lx = hx; ly = hy;
        }
        for (int j = lx; j < M; j++) o[j] = ly;
        return o;
    }

    // ------------------------------------------------------------------ 부호책
    sealed class Book
    {
        public int Dim, Entries; public int[] Len; public uint[] Code;
        public int LookupType; public int K;   // 격자: -K..K
        public void Build(int[] counts, int limit = 24)
        {
            var c = (int[])counts.Clone();
            int used = c.Count(x => x > 0);
            for (int i = 0; used < 2 && i < c.Length; i++) if (c[i] == 0) { c[i] = 1; used++; }   // 쓰인 것이 하나뿐이면 디코더가 헷갈림
            Len = Huff.Lengths(c, limit);
            Code = Assign(Len);
        }
        /// <summary>규격의 부호 배정 (차례대로, 그 길이에서 가장 낮은 빈 자리)</summary>
        static uint[] Assign(int[] len)
        {
            var code = new uint[len.Length];
            var avail = new uint[33];
            bool first = true;
            for (int i = 0; i < len.Length; i++)
            {
                int z = len[i];
                if (z == 0) continue;
                if (first)
                {
                    code[i] = 0; first = false;
                    for (int k = 1; k <= z; k++) avail[k] = 1u << (32 - k);
                    continue;
                }
                int y = z;
                while (y > 0 && avail[y] == 0) y--;
                if (y == 0) throw new InvalidOperationException("Vorbis 부호책 길이가 맞지 않습니다");
                uint res = avail[y]; avail[y] = 0;
                code[i] = res >> (32 - z);
                for (int k = z; k > y; k--) avail[k] = res + (1u << (32 - k));
            }
            return code;
        }
        public void Put(BitW bw, int sym)
        {
            int l = Len[sym];
            if (l == 0) throw new InvalidOperationException("Vorbis: 부호책에 없는 값");
            for (int b = l - 1; b >= 0; b--) bw.Put((Code[sym] >> b) & 1, 1);
        }
        public void WriteHeader(BitW bw)
        {
            bw.Put(0x564342, 24); bw.Put((uint)Dim, 16); bw.Put((uint)Entries, 24);
            bw.Put(0, 1);   // 순서 없음
            bool sparse = Len.Any(l => l == 0);
            bw.Put(sparse ? 1u : 0, 1);
            for (int i = 0; i < Entries; i++)
            {
                if (sparse) { bw.Put(Len[i] > 0 ? 1u : 0, 1); if (Len[i] == 0) continue; }
                bw.Put((uint)(Len[i] - 1), 5);
            }
            bw.Put((uint)LookupType, 4);
            if (LookupType == 1)
            {
                int vals = 2 * K + 1, bits = 1; while ((1 << bits) < vals) bits++;
                bw.Put(FloatPack(-K), 32); bw.Put(FloatPack(1), 32);
                bw.Put((uint)(bits - 1), 4); bw.Put(0, 1);
                for (int i = 0; i < vals; i++) bw.Put((uint)i, bits);
            }
        }
    }
    /// <summary>정수 → Vorbis 32비트 실수 (가수 21비트·지수 10비트)</summary>
    static uint FloatPack(int v) => (v < 0 ? 0x80000000u : 0) | (788u << 21) | (uint)Math.Abs(v);

    // ------------------------------------------------------------------ 부호화
    sealed class Block { public bool[] On; public int[][] Vals; public sbyte[][] Res; public bool[][] Cls; }

    /// <summary>PCM → Vorbis 묶음들 (WebM·Ogg 에 넣을 머리 3개 + 소리 묶음)</summary>
    public static VorbisStream Encode(PcmAudio pcm, double quality = 0.8)
    {
        int ch = pcm.Channels, rate = pcm.Rate, frames = pcm.Frames;
        quality = Math.Clamp(quality, 0, 1);
        int K = (int)Math.Round(4 + 11 * quality);                                 // 나머지 범위 (클수록 정확)
        double noise = Math.Pow(10, (-62 - 30 * quality) / 20);                   // 이 아래는 0
        int cutoff = (int)Math.Min(M, Math.Round(M * Math.Min(20000.0, 11000 + 9000 * quality) / (rate / 2.0)));
        int nBlocks = (frames + M - 1) / M + 1;
        var blocks = new Block[nBlocks];
        int parts = M / PartSize;
        Parallel.For(0, nBlocks, b =>
        {
            var bl = new Block { On = new bool[ch], Vals = new int[ch][], Res = new sbyte[ch][], Cls = new bool[ch][] };
            var seg = new double[N];
            for (int c = 0; c < ch; c++)
            {
                // 블록 b = 샘플 [(b−1)M, (b+1)M)
                for (int i = 0; i < N; i++)
                {
                    long s = (long)(b - 1) * M + i;
                    seg[i] = s >= 0 && s < frames ? pcm.Data[s * ch + c] / 32768.0 * Window[i] : 0;
                }
                var X = Mdct(seg);
                // 칸마다 필요한 바닥 높이 번호: |X|/K 이상, 잡음 바닥 이상
                var need = new int[M];
                double peak = 0;
                for (int k = 0; k < M; k++)
                {
                    double a = Math.Abs(X[k]); if (k < cutoff && a > peak) peak = a;
                    double want = Math.Max(k < cutoff ? a / K : 0, noise);
                    int lo = 0, hi = 255;
                    while (lo < hi) { int mid = (lo + hi) >> 1; if (InvDb[mid] >= want) hi = mid; else lo = mid + 1; }
                    need[k] = lo;
                }
                if (peak < noise) continue;   // 이 블록 이 채널은 조용함 → 0 채널
                var target = new int[Posts.Length];
                for (int q = 0; q < Sorted.Length; q++)
                {
                    int j = Sorted[q];
                    int from = q > 0 ? Posts[Sorted[q - 1]] : 0, to = q + 1 < Sorted.Length ? Posts[Sorted[q + 1]] : M;
                    int mx = 0; for (int k = from; k < Math.Min(to, M); k++) mx = Math.Max(mx, need[k]);
                    target[j] = Math.Clamp((mx + Mult - 1) / Mult, 0, Range - 1);
                }
                var (vals, fy, used) = EncodeFloor(target);
                var curve = RenderFloor(fy, used);
                var res = new sbyte[M]; var cls = new bool[parts];
                for (int k = 0; k < cutoff; k++)
                {
                    int r = (int)Math.Round(X[k] / InvDb[curve[k] & 255]);
                    res[k] = (sbyte)Math.Clamp(r, -K, K);
                    if (res[k] != 0) cls[k / PartSize] = true;
                }
                bl.On[c] = true; bl.Vals[c] = vals; bl.Res[c] = res; bl.Cls[c] = cls;
            }
            blocks[b] = bl;
        });

        // ---- 부호책: 바닥 값(0~127), 나머지 부류(2), 나머지 격자 (2K+1)²
        var floorBook = new Book { Dim = 1, Entries = Range };
        var classBook = new Book { Dim = 1, Entries = 2 };
        int L = 2 * K + 1;
        var resBook = new Book { Dim = 2, Entries = L * L, LookupType = 1, K = K };
        var fc = new int[Range]; var rc = new int[L * L];
        foreach (var bl in blocks)
            for (int c = 0; c < ch; c++)
            {
                if (!bl.On[c]) continue;
                for (int j = 2; j < Posts.Length; j++) fc[bl.Vals[c][j]]++;
                for (int p = 0; p < parts; p++)
                    if (bl.Cls[c][p])
                        for (int i = 0; i < PartSize; i += 2) rc[(bl.Res[c][p * PartSize + i] + K) + (bl.Res[c][p * PartSize + i + 1] + K) * L]++;
            }
        floorBook.Build(fc); classBook.Build(new[] { 1, 1 }); resBook.Build(rc);

        var vs = new VorbisStream { Rate = rate, Channels = ch, Samples = frames };
        // ---- 식별 머리
        {
            var bw = new BitW();
            bw.Put(1, 8); foreach (char cc in "vorbis") bw.Put(cc, 8);
            bw.Put(0, 32); bw.Put((uint)ch, 8); bw.Put((uint)rate, 32);
            bw.Put(0, 32); bw.Put((uint)(ch * (64000 + 96000 * quality)), 32); bw.Put(0, 32);
            bw.Put(8, 4); bw.Put(11, 4);   // 짧은 256, 긴 2048
            bw.Put(1, 1);
            vs.Headers = new byte[3][];
            vs.Headers[0] = bw.ToArray();
        }
        // ---- 주석 머리
        {
            var bw = new BitW();
            bw.Put(3, 8); foreach (char cc in "vorbis") bw.Put(cc, 8);
            var vendor = System.Text.Encoding.UTF8.GetBytes("ComicPad Vorbis");
            bw.Put((uint)vendor.Length, 32); foreach (var b in vendor) bw.Put(b, 8);
            bw.Put(0, 32); bw.Put(1, 1);
            vs.Headers[1] = bw.ToArray();
        }
        // ---- 설정 머리
        {
            var bw = new BitW();
            bw.Put(5, 8); foreach (char cc in "vorbis") bw.Put(cc, 8);
            bw.Put(2, 8);   // 부호책 3개
            floorBook.WriteHeader(bw); classBook.WriteHeader(bw); resBook.WriteHeader(bw);
            bw.Put(0, 6); bw.Put(0, 16);   // 시간 영역 (자리만)
            // 바닥 1
            bw.Put(0, 6); bw.Put(1, 16);
            int nParts = (Posts.Length - 2) / ClassDim;
            bw.Put((uint)nParts, 5);
            for (int i = 0; i < nParts; i++) bw.Put(0, 4);       // 모두 부류 0
            bw.Put(ClassDim - 1, 3); bw.Put(0, 2);                 // 부류 0: 4차원, 하위 부류 없음
            bw.Put(0 + 1, 8);                                      // 하위 책 = 0번 (+1)
            bw.Put(Mult - 1, 2); bw.Put(10, 4);                    // 곱 2, X 는 10비트 (M = 1024)
            for (int j = 2; j < Posts.Length; j++) bw.Put((uint)Posts[j], 10);
            // 나머지 (형식 1)
            bw.Put(0, 6); bw.Put(1, 16);
            bw.Put(0, 24); bw.Put((uint)M, 24); bw.Put(PartSize - 1, 24);
            bw.Put(2 - 1, 6); bw.Put(1, 8);                        // 부류 2개, 부류 책 1번
            bw.Put(0, 3); bw.Put(0, 1);                            // 부류 0: 쓰는 책 없음
            bw.Put(1, 3); bw.Put(0, 1);                            // 부류 1: 첫 차례만
            bw.Put(2, 8);                                          // 부류 1 첫 차례 책 = 2번
            // 매핑
            bw.Put(0, 6); bw.Put(0, 16);
            bw.Put(0, 1); bw.Put(0, 1); bw.Put(0, 2);              // 하위 맵 1개, 결합 없음, 예약
            bw.Put(0, 8); bw.Put(0, 8); bw.Put(0, 8);              // 시간·바닥 0·나머지 0
            // 모드
            bw.Put(0, 6); bw.Put(1, 1); bw.Put(0, 16); bw.Put(0, 16); bw.Put(0, 8);
            bw.Put(1, 1);
            vs.Headers[2] = bw.ToArray();
        }
        // ---- 소리 묶음
        for (int b = 0; b < nBlocks; b++)
        {
            var bl = blocks[b];
            var bw = new BitW();
            bw.Put(0, 1);                  // 소리 묶음 (모드 번호 0비트)
            bw.Put(1, 1); bw.Put(1, 1);    // 앞·뒤 창 모두 긴 것
            for (int c = 0; c < ch; c++)
            {
                if (!bl.On[c]) { bw.Put(0, 1); continue; }
                bw.Put(1, 1);
                bw.Put((uint)bl.Vals[c][0], 7); bw.Put((uint)bl.Vals[c][1], 7);
                for (int j = 2; j < Posts.Length; j++) floorBook.Put(bw, bl.Vals[c][j]);
            }
            // 나머지: 묶음마다 [채널별 부류] 다음 [채널별 값]
            for (int p = 0; p < parts; p++)
            {
                for (int c = 0; c < ch; c++) if (bl.On[c]) classBook.Put(bw, bl.Cls[c][p] ? 1 : 0);
                for (int c = 0; c < ch; c++)
                {
                    if (!bl.On[c] || !bl.Cls[c][p]) continue;
                    for (int i = 0; i < PartSize; i += 2) resBook.Put(bw, (bl.Res[c][p * PartSize + i] + K) + (bl.Res[c][p * PartSize + i + 1] + K) * L);
                }
            }
            long start = Math.Max(0, (long)(b - 1) * M);
            vs.Packets.Add((start * 1000 / rate, bw.ToArray()));
        }
        return vs;
    }

    /// <summary>Ogg 파일로 (시험·내보내기용): 머리 쪽 2쪽 + 소리 쪽들, 그래뉼 위치 = 끝난 샘플 수</summary>
    public static byte[] ToOgg(VorbisStream vs)
    {
        var o = new MemoryStream();
        uint serial = 0x436f6d69; int seq = 0;
        void Page(List<byte[]> pkts, long granule, int flags)
        {
            var lacing = new List<byte>();
            foreach (var p in pkts) { int n = p.Length; while (n >= 255) { lacing.Add(255); n -= 255; } lacing.Add((byte)n); }
            var h = new byte[27 + lacing.Count];
            "OggS"u8.CopyTo(h); h[4] = 0; h[5] = (byte)flags;
            BitConverter.GetBytes(granule).CopyTo(h, 6); BitConverter.GetBytes(serial).CopyTo(h, 14); BitConverter.GetBytes(seq++).CopyTo(h, 18);
            h[26] = (byte)lacing.Count; lacing.CopyTo(h, 27);
            var body = pkts.SelectMany(x => x).ToArray();
            uint crc = OggCrc(h, 0); crc = OggCrc(body, crc);
            BitConverter.GetBytes(crc).CopyTo(h, 22);
            o.Write(h); o.Write(body);
        }
        Page(new List<byte[]> { vs.Headers[0] }, 0, 2);
        Page(new List<byte[]> { vs.Headers[1], vs.Headers[2] }, 0, 0);
        var cur = new List<byte[]>(); int segs = 0;
        for (int i = 0; i < vs.Packets.Count; i++)
        {
            var p = vs.Packets[i].pkt;
            int need = p.Length / 255 + 1;
            if (segs + need > 255 && cur.Count > 0) { Page(cur, Math.Min(vs.Samples, (long)(i - 1) * M), 0); cur = new(); segs = 0; }
            cur.Add(p); segs += need;
        }
        if (cur.Count > 0) Page(cur, vs.Samples, 4);
        return o.ToArray();
    }

    static uint[] crcTable;
    static uint OggCrc(byte[] d, uint crc)
    {
        if (crcTable == null)
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint r = i << 24; for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1; t[i] = r; }
            crcTable = t;
        }
        foreach (var b in d) crc = (crc << 8) ^ crcTable[((crc >> 24) ^ b) & 0xff];
        return crc;
    }
}
