// WebP 만들기·읽기 (웹 판 animexport.js 의 움직이는 WebP, 정지 그림 WebP).
// Windows 에는 WebP 인코더가 없어서 무손실(VP8L) 인코더를 직접 짬: 초록 빼기 + 예측 변환 + LZ77(해시 사슬) + 허프만.
//  - 움직이는 WebP: VP8X + ANIM + ANMF (앞 프레임과 섞지 않고 바뀐 영역만 통째로 바꿈 — 웹 판과 같은 0x02)
//  - "화질" 1 미만이면 손실(VP8, 직접 짠 인코더 Vp8.cs) — 투명하면 알파는 ALPH 덩이(VP8L 무손실 압축)로 따로
// 읽기: 먼저 WIC (Windows 의 WebP 확장) → 안 되면 직접 짠 디코더 (무손실 VP8L·손실 VP8 + ALPH 모두, 웹 판 브라우저가 만든 파일도).
// 픽셀은 미리 곱하지 않은 ARGB (A<<24 | R<<16 | G<<8 | B).
namespace ComicPadNative.Modules.Media;

/// <summary>미리 곱하지 않은 ARGB 그림 한 장</summary>
public sealed class Argb
{
    public int W, H; public uint[] P;
    public Argb(int w, int h, uint[] p = null) { W = w; H = h; P = p ?? new uint[w * h]; }
    public bool HasAlpha() { foreach (var c in P) if ((c >> 24) != 255) return true; return false; }
    public static Argb FromPremul(uint[] premul, int w, int h)
    {
        var o = new Argb(w, h);
        for (int i = 0; i < o.P.Length; i++) o.P[i] = Core.Px.Unpremul(premul[i]);
        return o;
    }
    public uint[] ToPremul() { var o = new uint[P.Length]; for (int i = 0; i < P.Length; i++) o[i] = Core.Px.Premul(P[i]); return o; }
}

/// <summary>비트 쓰기 (LSB 먼저, VP8L 규칙)</summary>
sealed class BitW
{
    byte[] buf = new byte[1 << 16]; int len; ulong acc; int n;
    public void Put(uint v, int bits)
    {
        if (bits == 0) return;
        acc |= (ulong)(v & (uint)((1UL << bits) - 1)) << n; n += bits;
        while (n >= 8) { if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2); buf[len++] = (byte)acc; acc >>= 8; n -= 8; }
    }
    public byte[] ToArray()
    {
        var o = new byte[len + (n > 0 ? 1 : 0)];
        Array.Copy(buf, o, len);
        if (n > 0) o[len] = (byte)acc;
        return o;
    }
}

/// <summary>비트 읽기 (LSB 먼저)</summary>
sealed class BitR
{
    readonly byte[] b; readonly int start, end; long pos;   // pos = 비트 위치
    public BitR(byte[] data, int off, int count) { b = data; start = off; end = off + count; pos = (long)off * 8; }
    public uint Peek(int n)
    {
        if (n == 0) return 0;
        long bi = pos >> 3; int sh = (int)(pos & 7);
        ulong v = 0;
        for (int k = 0; k < 8; k++) { long i = bi + k; if (i < end) v |= (ulong)b[i] << (8 * k); }
        return (uint)((v >> sh) & ((1UL << n) - 1));
    }
    public void Skip(int n) { pos += n; if ((pos + 7) >> 3 > end + 8) throw new InvalidDataException("WebP: 데이터가 모자랍니다"); }
    public uint Read(int n) { uint v = Peek(n); Skip(n); return v; }
}

/// <summary>허프만 길이·부호 (인코더용)</summary>
static class Huff
{
    /// <summary>쓰인 기호의 길이 (최대 limit). 쓰인 기호가 하나면 길이 1 (읽는 쪽은 0비트로 처리)</summary>
    public static int[] Lengths(int[] counts, int limit)
    {
        int n = counts.Length;
        var len = new int[n];
        var used = new List<int>();
        for (int i = 0; i < n; i++) if (counts[i] > 0) used.Add(i);
        if (used.Count == 0) return len;
        if (used.Count == 1) { len[used[0]] = 1; return len; }
        var w = used.Select(i => (long)counts[i]).ToArray();
        while (true)
        {
            int m = used.Count;
            var parent = new int[m * 2];
            var pq = new PriorityQueue<int, (long, int)>();
            var wt = new long[m * 2];
            for (int i = 0; i < m; i++) { wt[i] = w[i]; pq.Enqueue(i, (w[i], i)); }
            int next = m;
            while (pq.Count > 1)
            {
                int a = pq.Dequeue(), b = pq.Dequeue();
                wt[next] = wt[a] + wt[b]; parent[a] = next; parent[b] = next;
                pq.Enqueue(next, (wt[next], next)); next++;
            }
            int root = next - 1, max = 0;
            var depth = new int[m];
            for (int i = 0; i < m; i++) { int d = 0, k = i; while (k != root) { k = parent[k]; d++; } depth[i] = d; if (d > max) max = d; }
            if (max <= limit) { for (int i = 0; i < m; i++) len[used[i]] = depth[i]; return len; }
            for (int i = 0; i < m; i++) w[i] = Math.Max(1, w[i] >> 1);   // 너무 길면 차이를 줄여 다시
        }
    }

    public static int Reverse(int code, int len) { int r = 0; for (int i = 0; i < len; i++) { r = (r << 1) | (code & 1); code >>= 1; } return r; }

    /// <summary>정규 부호 (비트 순서를 뒤집어 둠: 쓰기 그대로)</summary>
    public static uint[] Codes(int[] len)
    {
        var bl = new int[16];
        foreach (int l in len) if (l > 0) bl[l]++;
        var next = new int[16]; int code = 0;
        for (int b = 1; b < 16; b++) { code = (code + bl[b - 1]) << 1; next[b] = code; }
        var o = new uint[len.Length];
        for (int s = 0; s < len.Length; s++) if (len[s] > 0) o[s] = (uint)Reverse(next[len[s]]++, len[s]);
        return o;
    }
}

/// <summary>허프만 읽기 (짧은 부호는 표, 긴 부호는 한 비트씩)</summary>
sealed class HDec
{
    const int FAST = 10;
    int single = -1;
    int[] fast; int[] count = new int[16]; int[] sym;
    public static HDec Build(int[] len)
    {
        var h = new HDec();
        int nz = 0, last = 0;
        for (int i = 0; i < len.Length; i++) if (len[i] > 0) { nz++; last = i; }
        if (nz == 0) throw new InvalidDataException("WebP: 빈 허프만 부호");
        if (nz == 1) { h.single = last; return h; }
        foreach (int l in len) if (l > 0) h.count[l]++;
        // 완전한 부호인지 (넘치거나 모자라면 잘못된 파일)
        int left = 1;
        for (int l = 1; l < 16; l++) { left <<= 1; left -= h.count[l]; if (left < 0) throw new InvalidDataException("WebP: 허프만 부호가 넘칩니다"); }
        if (left != 0) throw new InvalidDataException("WebP: 허프만 부호가 모자랍니다");
        h.sym = Enumerable.Range(0, len.Length).Where(i => len[i] > 0).OrderBy(i => len[i]).ThenBy(i => i).ToArray();
        var codes = Huff.Codes(len);
        h.fast = new int[1 << FAST];
        for (int s = 0; s < len.Length; s++)
        {
            int l = len[s];
            if (l == 0 || l > FAST) continue;
            for (int k = (int)codes[s]; k < (1 << FAST); k += 1 << l) h.fast[k] = (s << 4) | l;
        }
        return h;
    }
    public int Read(BitR br)
    {
        if (single >= 0) return single;
        int e = fast[br.Peek(FAST)];
        if (e != 0) { br.Skip(e & 15); return e >> 4; }
        int code = 0, first = 0, index = 0;
        for (int l = 1; l < 16; l++)
        {
            code |= (int)br.Read(1);
            int c = count[l];
            if (code - c < first) return sym[index + (code - first)];
            index += c; first += c; first <<= 1; code <<= 1;
        }
        throw new InvalidDataException("WebP: 잘못된 허프만 부호");
    }
}

public static class WebP
{
    // ------------------------------------------------------------------ 공통 표
    static readonly int[] CodeLengthOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
    // 거리 부호 1~120 → (xi, yi): 거리 = yi * 폭 + xi (작은 거리는 2차원 이웃으로)
    static readonly sbyte[] PlaneXY =
    {
        0,1, 1,0, 1,1, -1,1, 0,2, 2,0, 1,2, -1,2, 2,1, -2,1, 2,2, -2,2, 0,3, 3,0, 1,3, -1,3,
        3,1, -3,1, 2,3, -2,3, 3,2, -3,2, 0,4, 4,0, 1,4, -1,4, 4,1, -4,1, 3,3, -3,3, 2,4, -2,4,
        4,2, -4,2, 0,5, 3,4, -3,4, 4,3, -4,3, 5,0, 1,5, -1,5, 5,1, -5,1, 2,5, -2,5, 5,2, -5,2,
        4,4, -4,4, 3,5, -3,5, 5,3, -5,3, 0,6, 6,0, 1,6, -1,6, 6,1, -6,1, 2,6, -2,6, 6,2, -6,2,
        4,5, -4,5, 5,4, -5,4, 3,6, -3,6, 6,3, -6,3, 0,7, 7,0, 1,7, -1,7, 5,5, -5,5, 7,1, -7,1,
        4,6, -4,6, 6,4, -6,4, 2,7, -2,7, 7,2, -7,2, 3,7, -3,7, 7,3, -7,3, 5,6, -5,6, 6,5, -6,5,
        8,0, 4,7, -4,7, 7,4, -7,4, 8,1, 8,2, 6,6, -6,6, 8,3, 5,7, -5,7, 7,5, -7,5, 8,4, 6,7,
        -6,7, 7,6, -7,6, 8,5, 7,7, -7,7, 8,6, 8,7,
    };
    const int NumLenCodes = 24, NumDistCodes = 40, MaxLen = 4096, MaxDist = (1 << 20) - 120;

    static int DistToPlane(int xsize, int dist)
    {
        for (int i = 0; i < 120; i++)
        {
            int d = PlaneXY[i * 2 + 1] * xsize + PlaneXY[i * 2];
            if (d == dist) return i + 1;
        }
        return dist + 120;
    }
    static int PlaneToDist(int xsize, int code)
    {
        if (code > 120) return code - 120;
        int d = PlaneXY[(code - 1) * 2 + 1] * xsize + PlaneXY[(code - 1) * 2];
        return d >= 1 ? d : 1;
    }

    /// <summary>값(≥1) → 앞부호 + 덧붙는 비트</summary>
    static void Prefix(int v, out int code, out int nbits, out int extra)
    {
        int d = v - 1;
        if (d < 4) { code = d; nbits = 0; extra = 0; return; }
        int hb = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)d);
        int second = (d >> (hb - 1)) & 1;
        nbits = hb - 1; code = 2 * hb + second; extra = d & ((1 << nbits) - 1);
    }
    static int PrefixValue(BitR br, int code)
    {
        if (code < 4) return code + 1;
        int nb = (code - 2) >> 1, off = (2 + (code & 1)) << nb;
        return off + (int)br.Read(nb) + 1;
    }

    static uint AddPx(uint a, uint b) => (((a & 0xff00ff00u) + (b & 0xff00ff00u)) & 0xff00ff00u) | (((a & 0x00ff00ffu) + (b & 0x00ff00ffu)) & 0x00ff00ffu);
    static uint SubPx(uint a, uint b) => (((a | 0x00ff00ffu) - (b & 0xff00ff00u)) & 0xff00ff00u) | (((a | 0xff00ff00u) - (b & 0x00ff00ffu)) & 0x00ff00ffu);
    static uint Avg2(uint a, uint b) => (((a ^ b) & 0xfefefefeu) >> 1) + (a & b);
    static int Ch(uint c, int sh) => (int)((c >> sh) & 255);
    static uint Select(uint L, uint T, uint TL)
    {
        int pl = 0, pt = 0;
        for (int sh = 0; sh < 32; sh += 8)
        {
            int p = Ch(L, sh) + Ch(T, sh) - Ch(TL, sh);
            pl += Math.Abs(p - Ch(L, sh)); pt += Math.Abs(p - Ch(T, sh));
        }
        return pl < pt ? L : T;
    }
    static uint ClampAddSubFull(uint a, uint b, uint c)
    {
        uint o = 0;
        for (int sh = 0; sh < 32; sh += 8) o |= (uint)Math.Clamp(Ch(a, sh) + Ch(b, sh) - Ch(c, sh), 0, 255) << sh;
        return o;
    }
    static uint ClampAddSubHalf(uint a, uint b)
    {
        uint o = 0;
        for (int sh = 0; sh < 32; sh += 8) { int x = Ch(a, sh), y = Ch(b, sh); o |= (uint)Math.Clamp(x + (x - y) / 2, 0, 255) << sh; }
        return o;
    }
    /// <summary>예측 모드 0~13 (L 왼쪽, T 위, TR 오른쪽 위, TL 왼쪽 위)</summary>
    static uint Predict(int mode, uint L, uint T, uint TR, uint TL) => mode switch
    {
        0 => 0xff000000u,
        1 => L,
        2 => T,
        3 => TR,
        4 => TL,
        5 => Avg2(Avg2(L, TR), T),
        6 => Avg2(L, TL),
        7 => Avg2(L, T),
        8 => Avg2(TL, T),
        9 => Avg2(T, TR),
        10 => Avg2(Avg2(L, TL), Avg2(T, TR)),
        11 => Select(L, T, TL),
        12 => ClampAddSubFull(L, T, TL),
        _ => ClampAddSubHalf(Avg2(L, T), TL),
    };
    /// <summary>위치 i 의 예측값 (가장자리 규칙 포함, data 는 원래 픽셀)</summary>
    static uint PredictAt(uint[] data, int w, int x, int y, int mode)
    {
        int i = y * w + x;
        if (x == 0 && y == 0) return 0xff000000u;
        if (y == 0) return data[i - 1];
        if (x == 0) return data[i - w];
        return Predict(mode, data[i - 1], data[i - w], data[i - w + 1], data[i - w - 1]);   // 오른쪽 끝의 TR 은 이 줄 첫 픽셀 (메모리 순서 그대로)
    }

    // ================================================================== 인코더
    /// <summary>VP8L 비트스트림 (서명 0x2f 부터)</summary>
    public static byte[] EncodeVP8L(Argb im)
    {
        int w = im.W, h = im.H;
        if (w < 1 || h < 1 || w > 16384 || h > 16384) throw new InvalidOperationException($"WebP 는 가로·세로 16384px 까지입니다 ({w}×{h})");
        var bw = new BitW();
        bw.Put(0x2f, 8); bw.Put((uint)(w - 1), 14); bw.Put((uint)(h - 1), 14); bw.Put(im.HasAlpha() ? 1u : 0u, 1); bw.Put(0, 3);
        var px = (uint[])im.P.Clone();
        // 1) 초록 빼기
        bw.Put(1, 1); bw.Put(2, 2);
        for (int i = 0; i < px.Length; i++) { uint c = px[i], g = (c >> 8) & 255; px[i] = (c & 0xff00ff00u) | ((((c >> 16) & 255) - g) & 255) << 16 | (((c & 255) - g) & 255); }
        // 2) 예측 (16px 칸마다 가장 잘 맞는 모드)
        if (w * h >= 64)
        {
            const int bits = 4;
            int bwid = (w + (1 << bits) - 1) >> bits, bhei = (h + (1 << bits) - 1) >> bits;
            var modes = new int[bwid * bhei];
            Parallel.For(0, bhei, by =>
            {
                for (int bx = 0; bx < bwid; bx++)
                {
                    int x0 = bx << bits, y0 = by << bits, x1 = Math.Min(w, x0 + (1 << bits)), y1 = Math.Min(h, y0 + (1 << bits));
                    long best = long.MaxValue; int bm = 1;
                    for (int m = 0; m < 14; m++)
                    {
                        long cost = 0;
                        for (int y = Math.Max(1, y0); y < y1 && cost < best; y++)
                            for (int x = Math.Max(1, x0); x < x1; x++)
                            {
                                uint r = SubPx(px[y * w + x], Predict(m, px[y * w + x - 1], px[(y - 1) * w + x], px[(y - 1) * w + x + 1], px[(y - 1) * w + x - 1]));
                                cost += Math.Abs((int)(sbyte)r) + Math.Abs((int)(sbyte)(r >> 8)) + Math.Abs((int)(sbyte)(r >> 16)) + Math.Abs((int)(sbyte)(r >> 24));
                            }
                        if (cost < best) { best = cost; bm = m; }
                    }
                    modes[by * bwid + bx] = bm;
                }
            });
            bw.Put(1, 1); bw.Put(0, 2); bw.Put(bits - 2, 3);
            EncodeImageData(bw, modes.Select(m => 0xff000000u | ((uint)m << 8)).ToArray(), bwid, bhei, false);
            var res = new uint[px.Length];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int m = modes[(y >> bits) * bwid + (x >> bits)];
                    res[y * w + x] = SubPx(px[y * w + x], PredictAt(px, w, x, y, m));
                }
            });
            px = res;
        }
        bw.Put(0, 1);   // 변환 끝
        EncodeImageData(bw, px, w, h, true);
        return bw.ToArray();
    }

    /// <summary>엔트로피 부호 그림 (색 캐시·메타 부호 없이: 부호 한 벌)</summary>
    static void EncodeImageData(BitW bw, uint[] px, int w, int h, bool main)
    {
        bw.Put(0, 1);                 // 색 캐시 없음
        if (main) bw.Put(0, 1);       // 메타 허프만 없음
        int n = px.Length;
        // ---- LZ77 (해시 사슬, 탐욕) ----
        var kind = new List<int>(n / 2);   // ≥0: 픽셀 번호(글자), <0: -(길이)
        var dists = new List<int>();
        const int HB = 17; const int Depth = 24;
        var head = new int[1 << HB]; Array.Fill(head, -1);
        var prev = new int[n];
        uint Hash(int i) => ((px[i] * 0x1e35a7bdu) ^ (px[i + 1] * 0x9E3779B1u)) >> (32 - HB);
        void Insert(int i) { if (i + 1 >= n) return; uint hh = Hash(i); prev[i] = head[hh]; head[hh] = i; }
        int Match(int j, int i, int max) { int k = 0; while (k < max && px[j + k] == px[i + k]) k++; return k; }
        int pos = 0;
        while (pos < n)
        {
            int best = 0, bestD = 0, max = Math.Min(MaxLen, n - pos);
            if (max >= 3)
            {
                void Try(int d) { if (d < 1 || d > pos || d > MaxDist) return; int l = Match(pos - d, pos, max); if (l > best) { best = l; bestD = d; } }
                Try(1); if (best < max) Try(w);
                if (best < max)
                {
                    int dep = 0;
                    for (int j = head[Hash(pos)]; j >= 0 && dep < Depth && best < max; j = prev[j], dep++)
                    {
                        int d = pos - j;
                        if (d > MaxDist) break;
                        if (px[j + best] != px[pos + best]) continue;
                        int l = Match(j, pos, max);
                        if (l > best) { best = l; bestD = d; }
                    }
                }
            }
            if (best >= 3)
            {
                kind.Add(-best); dists.Add(bestD);
                int end = pos + best;
                // 긴 반복은 일부만 사슬에 (빠르게)
                for (int k = pos; k < end; k += best > 64 ? 4 : 1) Insert(k);
                pos = end;
            }
            else { kind.Add(pos); Insert(pos); pos++; }
        }
        // ---- 빈도 → 부호 ----
        var hg = new int[256 + NumLenCodes]; var hr = new int[256]; var hb = new int[256]; var ha = new int[256]; var hd = new int[NumDistCodes];
        var dcodes = new int[dists.Count];
        int di = 0;
        foreach (int k in kind)
        {
            if (k >= 0) { uint c = px[k]; hg[(c >> 8) & 255]++; hr[(c >> 16) & 255]++; hb[c & 255]++; ha[c >> 24]++; }
            else
            {
                Prefix(-k, out int lc, out _, out _); hg[256 + lc]++;
                dcodes[di] = DistToPlane(w, dists[di]);
                Prefix(dcodes[di], out int dc, out _, out _); hd[dc]++; di++;
            }
        }
        var lg = WriteCode(bw, hg); var lr = WriteCode(bw, hr); var lb = WriteCode(bw, hb); var la = WriteCode(bw, ha); var ld = WriteCode(bw, hd);
        di = 0;
        foreach (int k in kind)
        {
            if (k >= 0)
            {
                uint c = px[k];
                lg.Put(bw, (int)((c >> 8) & 255)); lr.Put(bw, (int)((c >> 16) & 255)); lb.Put(bw, (int)(c & 255)); la.Put(bw, (int)(c >> 24));
            }
            else
            {
                Prefix(-k, out int lc, out int ln, out int lx); lg.Put(bw, 256 + lc); bw.Put((uint)lx, ln);
                Prefix(dcodes[di], out int dc, out int dn, out int dx); ld.Put(bw, dc); bw.Put((uint)dx, dn); di++;
            }
        }
    }

    sealed class Code
    {
        public int[] Len; public uint[] Bits; public bool Zero;
        public void Put(BitW bw, int s) { if (!Zero) bw.Put(Bits[s], Len[s]); }
    }

    /// <summary>허프만 부호를 적고 (빈도 → 길이), 쓸 부호를 돌려줌</summary>
    static Code WriteCode(BitW bw, int[] hist)
    {
        var len = Huff.Lengths(hist, 15);
        int nz = 0, one = 0;
        for (int i = 0; i < len.Length; i++) if (len[i] > 0) { nz++; one = i; }
        if (nz <= 1 && one < 256)
        {
            // 단순 부호: 기호 하나 (0비트)
            bw.Put(1, 1); bw.Put(0, 1);
            if (one < 2) { bw.Put(0, 1); bw.Put((uint)one, 1); } else { bw.Put(1, 1); bw.Put((uint)one, 8); }
            return new Code { Len = len, Zero = true };
        }
        bw.Put(0, 1);
        // 길이를 반복 부호(16·17·18)로 줄임
        var toks = new List<(int s, int nb, int v)>();
        for (int i = 0; i < len.Length;)
        {
            int v = len[i], run = 1;
            while (i + run < len.Length && len[i + run] == v) run++;
            i += run;
            if (v == 0)
            {
                while (run >= 11) { int r = Math.Min(run, 138); toks.Add((18, 7, r - 11)); run -= r; }
                if (run >= 3) { toks.Add((17, 3, run - 3)); run = 0; }
                while (run-- > 0) toks.Add((0, 0, 0));
            }
            else
            {
                toks.Add((v, 0, 0)); run--;
                while (run >= 3) { int r = Math.Min(run, 6); toks.Add((16, 2, r - 3)); run -= r; }
                while (run-- > 0) toks.Add((v, 0, 0));
            }
        }
        var ch = new int[19];
        foreach (var t in toks) ch[t.s]++;
        var cl = Huff.Lengths(ch, 7);
        int num = 19;
        while (num > 4 && cl[CodeLengthOrder[num - 1]] == 0) num--;
        bw.Put((uint)(num - 4), 4);
        for (int i = 0; i < num; i++) bw.Put((uint)cl[CodeLengthOrder[i]], 3);
        bw.Put(0, 1);   // 기호 수 = 알파벳 전체
        var clc = new Code { Len = cl, Bits = Huff.Codes(cl), Zero = cl.Count(x => x > 0) <= 1 };
        foreach (var t in toks) { clc.Put(bw, t.s); bw.Put((uint)t.v, t.nb); }
        return new Code { Len = len, Bits = Huff.Codes(len), Zero = nz <= 1 };
    }

    // ================================================================== 디코더 (VP8L 전부: 변환 4가지·색 캐시·메타 허프만)
    sealed class Transform { public int Type, Bits, XSize; public uint[] Data; }

    public static Argb DecodeVP8L(byte[] data, int off, int len)
    {
        var br = new BitR(data, off, len);
        if (br.Read(8) != 0x2f) throw new InvalidDataException("VP8L 서명이 아닙니다");
        int w = (int)br.Read(14) + 1, h = (int)br.Read(14) + 1;
        br.Read(1);
        if (br.Read(3) != 0) throw new InvalidDataException("VP8L 판이 다릅니다");
        int xs = w;
        var tr = new List<Transform>();
        while (br.Read(1) == 1)
        {
            var t = new Transform { Type = (int)br.Read(2), XSize = xs };
            if (tr.Any(x => x.Type == t.Type)) throw new InvalidDataException("VP8L: 같은 변환이 두 번");
            if (t.Type == 0 || t.Type == 1)
            {
                t.Bits = (int)br.Read(3) + 2;
                t.Data = DecodeImageStream(br, Div(xs, t.Bits), Div(h, t.Bits), false);
            }
            else if (t.Type == 3)
            {
                int size = (int)br.Read(8) + 1;
                var pal = DecodeImageStream(br, size, 1, false);
                for (int i = 1; i < size; i++) pal[i] = AddPx(pal[i], pal[i - 1]);
                t.Data = pal;
                t.Bits = size <= 2 ? 3 : size <= 4 ? 2 : size <= 16 ? 1 : 0;
                xs = Div(xs, t.Bits);
            }
            tr.Add(t);
        }
        var px = DecodeImageStream(br, xs, h, true);
        for (int k = tr.Count - 1; k >= 0; k--) px = Inverse(tr[k], px, h);
        return new Argb(w, h, px);
    }
    static int Div(int v, int bits) => (v + (1 << bits) - 1) >> bits;

    static uint[] Inverse(Transform t, uint[] px, int h)
    {
        int w = t.XSize;
        switch (t.Type)
        {
            case 0:   // 예측
            {
                int bw = Div(w, t.Bits);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int m = (int)((t.Data[(y >> t.Bits) * bw + (x >> t.Bits)] >> 8) & 15);
                        int i = y * w + x;
                        px[i] = AddPx(px[i], PredictAt(px, w, x, y, m));
                    }
                return px;
            }
            case 1:   // 색 변환
            {
                int bw = Div(w, t.Bits);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        uint e = t.Data[(y >> t.Bits) * bw + (x >> t.Bits)];
                        sbyte g2r = (sbyte)(e & 255), g2b = (sbyte)((e >> 8) & 255), r2b = (sbyte)((e >> 16) & 255);
                        int i = y * w + x; uint c = px[i];
                        sbyte g = (sbyte)((c >> 8) & 255);
                        int r = (int)((c >> 16) & 255), b = (int)(c & 255);
                        r = (r + ((g2r * g) >> 5)) & 255;
                        b = (b + ((g2b * g) >> 5)) & 255;
                        b = (b + ((r2b * (sbyte)r) >> 5)) & 255;
                        px[i] = (c & 0xff00ff00u) | ((uint)r << 16) | (uint)b;
                    }
                return px;
            }
            case 2:   // 초록 더하기
                for (int i = 0; i < px.Length; i++) { uint c = px[i], g = (c >> 8) & 255; px[i] = (c & 0xff00ff00u) | ((((c >> 16) & 255) + g) & 255) << 16 | (((c & 255) + g) & 255); }
                return px;
            default:  // 색 번호 (팔레트, 작은 팔레트는 한 바이트에 여러 픽셀)
            {
                int pw = Div(w, t.Bits), per = 1 << t.Bits, bitsPer = 8 >> t.Bits, mask = (1 << bitsPer) - 1;
                var o = new uint[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        uint packed = (px[y * pw + (x >> t.Bits)] >> 8) & 255;
                        int idx = (int)(packed >> ((x & (per - 1)) * bitsPer)) & mask;
                        o[y * w + x] = idx < t.Data.Length ? t.Data[idx] : 0;
                    }
                return o;
            }
        }
    }

    static uint[] DecodeImageStream(BitR br, int xs, int ys, bool main)
    {
        int cacheBits = 0;
        if (br.Read(1) == 1) { cacheBits = (int)br.Read(4); if (cacheBits < 1 || cacheBits > 11) throw new InvalidDataException("VP8L: 색 캐시 크기"); }
        int pbits = 0, ew = 0; uint[] ent = null; int groups = 1;
        if (main && br.Read(1) == 1)
        {
            pbits = (int)br.Read(3) + 2; ew = Div(xs, pbits);
            ent = DecodeImageStream(br, ew, Div(ys, pbits), false);
            foreach (var e in ent) groups = Math.Max(groups, (int)((e >> 8) & 0xffff) + 1);
        }
        int cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
        var G = new HDec[groups][];
        for (int g = 0; g < groups; g++)
            G[g] = new[] { ReadCode(br, 256 + NumLenCodes + cacheSize), ReadCode(br, 256), ReadCode(br, 256), ReadCode(br, 256), ReadCode(br, NumDistCodes) };
        int n = xs * ys;
        var o = new uint[n];
        var cache = cacheSize > 0 ? new uint[cacheSize] : null;
        int cached = 0, i = 0;
        while (i < n)
        {
            var c = G[ent == null ? 0 : (int)((ent[((i / xs) >> pbits) * ew + ((i % xs) >> pbits)] >> 8) & 0xffff)];
            int s = c[0].Read(br);
            if (s < 256)
            {
                uint r = (uint)c[1].Read(br), b = (uint)c[2].Read(br), a = (uint)c[3].Read(br);
                o[i++] = (a << 24) | (r << 16) | ((uint)s << 8) | b;
            }
            else if (s < 256 + NumLenCodes)
            {
                int len = PrefixValue(br, s - 256);
                int dist = PlaneToDist(xs, PrefixValue(br, c[4].Read(br)));
                if (dist > i || i + len > n) throw new InvalidDataException("VP8L: 잘못된 거리");
                for (int k = 0; k < len; k++, i++) o[i] = o[i - dist];
            }
            else
            {
                if (cache == null || s - 256 - NumLenCodes >= cacheSize) throw new InvalidDataException("VP8L: 색 캐시");
                o[i++] = cache[s - 256 - NumLenCodes];
            }
            if (cache != null) for (; cached < i; cached++) cache[(int)((0x1e35a7bdu * o[cached]) >> (32 - cacheBits))] = o[cached];
        }
        return o;
    }

    static HDec ReadCode(BitR br, int alphabet)
    {
        var len = new int[alphabet];
        if (br.Read(1) == 1)
        {
            int num = (int)br.Read(1) + 1;
            int s0 = (int)br.Read(br.Read(1) == 1 ? 8 : 1);
            if (s0 >= alphabet) throw new InvalidDataException("VP8L: 기호 범위");
            len[s0] = 1;
            if (num == 2) { int s1 = (int)br.Read(8); if (s1 >= alphabet) throw new InvalidDataException("VP8L: 기호 범위"); len[s1] = 1; }
            return HDec.Build(len);
        }
        var cl = new int[19];
        int ncl = (int)br.Read(4) + 4;
        for (int i = 0; i < ncl; i++) cl[CodeLengthOrder[i]] = (int)br.Read(3);
        var clc = HDec.Build(cl);
        int max = alphabet;
        if (br.Read(1) == 1) { int nb = 2 + 2 * (int)br.Read(3); max = 2 + (int)br.Read(nb); if (max > alphabet) throw new InvalidDataException("VP8L: 기호 수"); }
        int prevLen = 8, sym = 0;
        while (sym < alphabet)
        {
            if (max-- == 0) break;
            int v = clc.Read(br);
            if (v < 16) { len[sym++] = v; if (v != 0) prevLen = v; continue; }
            int rep = v == 16 ? 3 + (int)br.Read(2) : v == 17 ? 3 + (int)br.Read(3) : 11 + (int)br.Read(7);
            int fill = v == 16 ? prevLen : 0;
            if (sym + rep > alphabet) throw new InvalidDataException("VP8L: 길이 반복이 넘칩니다");
            for (int k = 0; k < rep; k++) len[sym++] = fill;
        }
        return HDec.Build(len);
    }

    // ================================================================== RIFF 묶기
    static void Le24(List<byte> o, int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); }
    static void Le32(List<byte> o, int v) { o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 24)); }
    static void Chunk(List<byte> o, string id, IList<byte> data)
    {
        foreach (char c in id) o.Add((byte)c);
        Le32(o, data.Count);
        o.AddRange(data);
        if ((data.Count & 1) != 0) o.Add(0);
    }
    static byte[] Riff(List<byte> body)
    {
        var o = new List<byte>(body.Count + 12);
        foreach (char c in "RIFF") o.Add((byte)c);
        Le32(o, body.Count + 4);
        foreach (char c in "WEBP") o.Add((byte)c);
        o.AddRange(body);
        return o.ToArray();
    }

    /// <summary>"화질" → 버릴 아래 비트 수 (1 = 무손실, 0.9 → 1비트, 0.8 → 2비트, 그 아래 3비트)</summary>
    public static int LossyBits(double quality) => quality >= 0.999 ? 0 : quality >= 0.85 ? 1 : quality >= 0.7 ? 2 : 3;

    /// <summary>거의 무손실: RGB 아래 비트를 반올림해서 버림 (알파는 그대로)</summary>
    public static Argb NearLossless(Argb im, int bits)
    {
        if (bits <= 0) return im;
        var o = new Argb(im.W, im.H);
        int half = 1 << (bits - 1);
        uint Q(uint v) => (uint)Math.Min(255, ((int)v + half) >> bits << bits);
        for (int i = 0; i < o.P.Length; i++)
        {
            uint c = im.P[i];
            o.P[i] = (c & 0xff000000u) | (Q((c >> 16) & 255) << 16) | (Q((c >> 8) & 255) << 8) | Q(c & 255);
        }
        return o;
    }

    /// <summary>손실로 만드는지 (화질 1 = 무손실)</summary>
    public static bool IsLossy(double quality) => quality < 0.999;

    /// <summary>그림 한 장의 덩이들 (무손실: VP8L / 손실: [ALPH] + "VP8 ")</summary>
    static List<(string id, byte[] data)> EncodeImage(Argb im, double quality)
    {
        var o = new List<(string, byte[])>();
        if (!IsLossy(quality)) { o.Add(("VP8L", EncodeVP8L(im))); return o; }
        if (im.HasAlpha()) o.Add(("ALPH", EncodeAlph(im)));
        o.Add(("VP8 ", Vp8.Encode(im, quality)));
        return o;
    }

    /// <summary>ALPH 덩이: 머리 1바이트(압축 1 = VP8L, 거르기 없음) + 머리 없는 VP8L (알파는 초록 자리)</summary>
    public static byte[] EncodeAlph(Argb im)
    {
        var g = new Argb(im.W, im.H);
        for (int i = 0; i < g.P.Length; i++) g.P[i] = 0xff000000u | ((im.P[i] >> 24) << 8);
        var v = EncodeVP8L(g);   // 서명 1 + 크기 4바이트를 떼면 바로 그림 줄기
        var o = new byte[v.Length - 4];
        o[0] = 0x01;
        Array.Copy(v, 5, o, 1, v.Length - 5);
        return o;
    }

    /// <summary>ALPH 덩이 → 알파 (w×h)</summary>
    public static byte[] DecodeAlph(byte[] b, int off, int len, int w, int h)
    {
        if (len < 1) throw new InvalidDataException("ALPH 가 비었습니다");
        int flags = b[off], method = flags & 3, filter = (flags >> 2) & 3;
        var a = new byte[w * h];
        if (method == 0) Array.Copy(b, off + 1, a, 0, Math.Min(a.Length, len - 1));
        else if (method == 1)
        {
            var v = new byte[len - 1 + 5];
            v[0] = 0x2f;
            uint bits = (uint)(w - 1) | ((uint)(h - 1) << 14);
            v[1] = (byte)bits; v[2] = (byte)(bits >> 8); v[3] = (byte)(bits >> 16); v[4] = (byte)(bits >> 24);
            Array.Copy(b, off + 1, v, 5, len - 1);
            var im = DecodeVP8L(v, 0, v.Length);
            for (int i = 0; i < a.Length; i++) a[i] = (byte)(im.P[i] >> 8);
        }
        else throw new InvalidDataException("ALPH 압축 방식을 모릅니다");
        if (filter != 0)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, pred;
                    if (x == 0 && y == 0) pred = 0;
                    else if (y == 0) pred = a[i - 1];
                    else if (x == 0) pred = a[i - w];
                    else pred = filter == 1 ? a[i - 1] : filter == 2 ? a[i - w] : Math.Clamp(a[i - 1] + a[i - w] - a[i - w - 1], 0, 255);
                    a[i] = (byte)(a[i] + pred);
                }
        return a;
    }

    /// <summary>정지 그림 WebP (화질 1 = 무손실, 그 아래는 손실 VP8)</summary>
    public static byte[] EncodeStill(Argb im, double quality = 1)
    {
        var body = new List<byte>();
        var parts = EncodeImage(im, quality);
        if (parts.Count > 1)   // 알파 + VP8 → VP8X 필요
        {
            var vp8x = new List<byte> { 0x10, 0, 0, 0 };
            Le24(vp8x, im.W - 1); Le24(vp8x, im.H - 1);
            Chunk(body, "VP8X", vp8x);
        }
        foreach (var (id, data) in parts) Chunk(body, id, data);
        return Riff(body);
    }

    /// <summary>움직이는 WebP. loop 0 = 무한. 같은 그림이 이어지면 앞 프레임 시간에 합치고, 바뀐 영역만 넣음</summary>
    public static byte[] EncodeAnimated(IList<(Argb img, int ms)> frames, int loop = 0, bool transparent = false, double quality = 1)
    {
        if (frames.Count == 0) throw new InvalidOperationException("프레임이 없습니다");
        int W = frames[0].img.W, H = frames[0].img.H;
        if (frames.Any(f => f.img.W != W || f.img.H != H)) throw new ArgumentException("프레임 크기가 모두 같아야 합니다");
        var list = new List<(int x, int y, Argb part, int ms)>();
        uint[] canvas = null;
        bool alpha = transparent;
        foreach (var (src, ms0) in frames)
        {
            var im = src;
            int ms = Math.Max(1, ms0);
            if (canvas == null) { canvas = (uint[])im.P.Clone(); list.Add((0, 0, im, ms)); alpha |= im.HasAlpha(); continue; }
            int x0 = W, y0 = H, x1 = -1, y1 = -1;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (im.P[y * W + x] != canvas[y * W + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            if (x1 < 0) { var l = list[^1]; list[^1] = (l.x, l.y, l.part, Math.Min(0xffffff, l.ms + ms)); continue; }   // 바뀐 게 없음
            x0 &= ~1; y0 &= ~1;   // 위치는 짝수만
            int pw = x1 - x0 + 1, ph = y1 - y0 + 1;
            var part = new Argb(pw, ph);
            for (int y = 0; y < ph; y++) Array.Copy(im.P, (y0 + y) * W + x0, part.P, y * pw, pw);
            for (int y = 0; y < ph; y++) Array.Copy(part.P, y * pw, canvas, (y0 + y) * W + x0, pw);
            alpha |= part.HasAlpha();
            list.Add((x0, y0, part, ms));
        }
        var enc = new List<(string id, byte[] data)>[list.Count];
        Parallel.For(0, list.Count, i => enc[i] = EncodeImage(list[i].part, quality));
        var body = new List<byte>();
        var vp8x = new List<byte> { (byte)(0x02 | (alpha ? 0x10 : 0)), 0, 0, 0 };
        Le24(vp8x, W - 1); Le24(vp8x, H - 1);
        Chunk(body, "VP8X", vp8x);
        Chunk(body, "ANIM", new byte[] { 255, 255, 255, (byte)(transparent ? 0 : 255), (byte)(loop & 255), (byte)((loop >> 8) & 255) });   // 배경색(BGRA)·반복
        for (int i = 0; i < list.Count; i++)
        {
            var (x, y, part, ms) = list[i];
            var p = new List<byte>(enc[i].Sum(e => e.data.Length) + 48);
            Le24(p, x / 2); Le24(p, y / 2); Le24(p, part.W - 1); Le24(p, part.H - 1); Le24(p, Math.Min(0xffffff, ms));
            p.Add(0x02);   // 섞지 않고 바꿈, 지우지 않음
            foreach (var (id, data) in enc[i]) Chunk(p, id, data);
            Chunk(body, "ANMF", p);
        }
        return Riff(body);
    }

    // ================================================================== 읽기
    public sealed class Info { public int W, H, Loop, Frames; public bool Animated, Alpha, Lossy; }

    static IEnumerable<(string id, int off, int len)> Chunks(byte[] b, int from, int to)
    {
        int p = from;
        while (p + 8 <= to)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, p, 4);
            int len = BitConverter.ToInt32(b, p + 4);
            if (len < 0 || p + 8 + len > to) yield break;
            yield return (id, p + 8, len);
            p += 8 + len + (len & 1);
        }
    }
    static int U24(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);

    public static bool IsWebP(byte[] b) => b.Length >= 16 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

    public static Info Probe(byte[] b)
    {
        if (!IsWebP(b)) throw new InvalidDataException("WebP 파일이 아닙니다");
        var info = new Info();
        foreach (var (id, off, len) in Chunks(b, 12, b.Length))
        {
            if (id == "VP8X") { info.Animated = (b[off] & 0x02) != 0; info.Alpha = (b[off] & 0x10) != 0; info.W = U24(b, off + 4) + 1; info.H = U24(b, off + 7) + 1; }
            else if (id == "ANIM") info.Loop = b[off + 4] | (b[off + 5] << 8);
            else if (id == "ANMF") { info.Frames++; if (Chunks(b, off + 16, off + len).Any(c => c.id == "VP8 ")) info.Lossy = true; }
            else if (id == "VP8L" && info.W == 0) { info.Frames = 1; var bits = b[off + 1] | (b[off + 2] << 8) | (b[off + 3] << 16) | (b[off + 4] << 24); info.W = (bits & 0x3fff) + 1; info.H = ((bits >> 14) & 0x3fff) + 1; info.Alpha = ((bits >> 28) & 1) != 0; }
            else if (id == "VP8 ") { info.Lossy = true; info.Frames = Math.Max(1, info.Frames); if (info.W == 0) { info.W = (b[off + 6] | (b[off + 7] << 8)) & 0x3fff; info.H = (b[off + 8] | (b[off + 9] << 8)) & 0x3fff; } }
        }
        if (info.Frames == 0) info.Frames = 1;
        return info;
    }

    /// <summary>덩이 묶음(from~to)에서 그림 하나: VP8L, 또는 [ALPH] + "VP8 " (없으면 null)</summary>
    static Argb DecodeImageChunks(byte[] b, int from, int to)
    {
        (int off, int len)? alph = null;
        foreach (var (id, off, len) in Chunks(b, from, to))
        {
            if (id == "VP8L") return DecodeVP8L(b, off, len);
            if (id == "ALPH") alph = (off, len);
            if (id == "VP8 ")
            {
                var im = Vp8.Decode(b, off, len);
                if (alph is (int ao, int al))
                {
                    var a = DecodeAlph(b, ao, al, im.W, im.H);
                    for (int i = 0; i < im.P.Length; i++) im.P[i] = (im.P[i] & 0x00ffffffu) | ((uint)a[i] << 24);
                }
                return im;
            }
        }
        return null;
    }

    /// <summary>WebP 를 직접 풀기 (무손실·손실 모두): 프레임마다 합친 화면 + 시간(ms)</summary>
    public static List<(Argb img, int ms)> DecodeFrames(byte[] b)
    {
        var info = Probe(b);
        var o = new List<(Argb, int)>();
        if (!info.Animated)
        {
            var still = DecodeImageChunks(b, 12, b.Length) ?? throw new InvalidDataException("WebP 그림 데이터가 없습니다");
            o.Add((still, 0));
            return o;
        }
        var canvas = new uint[info.W * info.H];
        foreach (var (id, off, len) in Chunks(b, 12, b.Length))
        {
            if (id != "ANMF") continue;
            int fx = U24(b, off) * 2, fy = U24(b, off + 3) * 2, fw = U24(b, off + 6) + 1, fh = U24(b, off + 9) + 1, ms = U24(b, off + 12);
            bool noBlend = (b[off + 15] & 2) != 0, dispose = (b[off + 15] & 1) != 0;
            Argb part = DecodeImageChunks(b, off + 16, off + len);
            if (part == null) continue;
            for (int y = 0; y < fh && fy + y < info.H; y++)
                for (int x = 0; x < fw && fx + x < info.W; x++)
                {
                    int ci = (fy + y) * info.W + fx + x;
                    uint s = part.P[y * part.W + x];
                    canvas[ci] = noBlend ? s : BlendOver(s, canvas[ci]);
                }
            o.Add((new Argb(info.W, info.H, (uint[])canvas.Clone()), ms));
            if (dispose)
                for (int y = 0; y < fh && fy + y < info.H; y++) Array.Clear(canvas, (fy + y) * info.W + fx, Math.Min(fw, info.W - fx));
        }
        return o;
    }

    /// <summary>WebP 규칙의 알파 섞기 (미리 곱하지 않은 값)</summary>
    static uint BlendOver(uint s, uint d)
    {
        uint sa = s >> 24; if (sa == 255) return s; if (sa == 0) return d;
        uint da = d >> 24;
        uint dw = da * (255 - sa) / 255, oa = sa + dw;
        if (oa == 0) return 0;
        uint C(int sh) => (((s >> sh) & 255) * sa + ((d >> sh) & 255) * dw) / oa;
        return (oa << 24) | (C(16) << 16) | (C(8) << 8) | C(0);
    }

    /// <summary>파일 → 첫 그림 (WIC 먼저, 안 되면 직접)</summary>
    public static Argb Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { var w = Wic.Decode(path); if (w != null) return w; } catch { }
        return DecodeFrames(bytes)[0].img;
    }
}
