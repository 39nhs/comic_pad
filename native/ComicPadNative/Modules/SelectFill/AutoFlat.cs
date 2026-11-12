// 밑색 한 번에 칠하기 · 색 점으로 칠하기 계산 — 웹 판 app/js/core/autoflat.js 를 그대로 옮김.
// 선화로 닫힌 영역마다 번호(라벨)를 매기고, 번호 → 색 (영역마다 다른 색 / 한 색 / 색 힌트 투표).
namespace ComicPadNative.Modules.SelectFill;

public static class AutoFlat
{
    public sealed class Opt
    {
        public double Alpha = 80;      // 선으로 볼 불투명도 (0~255)
        public double Gap = 2;         // 틈 막기 (px)
        public double MinArea = 30;    // 이보다 작은 영역은 이웃 색으로
        public bool Outside = true;    // 바깥(테두리에 닿는 영역) 제외
        public double Under = 3;       // 선 아래로 번지게 (px)
    }

    public sealed class Labels
    {
        public int[] L;      // 0 = 비움, 1.. = 영역 번호
        public int Count;
        public bool[] Edge;  // 번호마다 그림 테두리에 닿는지
    }

    /// <summary>rgba: 미리 곱하지 않은 RGBA (w*h*4) → 영역 라벨 (autoflat.js flatLabels)</summary>
    public static Labels FlatLabels(byte[] rgba, int w, int hgt, Opt o, CancellationToken ct = default)
    {
        int n = w * hgt;
        double thr = o.Alpha; int gap = Raster.JsRound(o.Gap); double minArea = o.MinArea; int under = Raster.JsRound(o.Under);
        var line = new byte[n];
        for (int i = 0, p = 3; i < n; i++, p += 4) line[i] = rgba[p] > thr ? (byte)1 : (byte)0;
        // gap = 이 너비(px) 이하의 빈틈은 선이 이어진 것으로 → 선을 gap/2 만큼 두껍게
        int rad = (int)Math.Ceiling(gap / 2.0);
        var wall = rad > 0 ? Raster.Dilate(line, w, hgt, rad) : line;
        var lab = new int[n];
        var stack = new int[n];
        var sizes = new List<int> { 0 };
        var touches = new List<bool> { false };
        int next = 1;
        for (int s = 0; s < n; s++)
        {
            if (wall[s] != 0 || lab[s] != 0) continue;
            if ((next & 1023) == 0) ct.ThrowIfCancellationRequested();
            int id = next++;
            int sp = 0, size = 0; bool edge = false;
            stack[sp++] = s; lab[s] = id;
            while (sp > 0)
            {
                int i = stack[--sp];
                size++;
                int x = i % w, y = (i - x) / w;
                if (x == 0 || y == 0 || x == w - 1 || y == hgt - 1) edge = true;
                if (x > 0 && wall[i - 1] == 0 && lab[i - 1] == 0) { lab[i - 1] = id; stack[sp++] = i - 1; }
                if (x < w - 1 && wall[i + 1] == 0 && lab[i + 1] == 0) { lab[i + 1] = id; stack[sp++] = i + 1; }
                if (y > 0 && wall[i - w] == 0 && lab[i - w] == 0) { lab[i - w] = id; stack[sp++] = i - w; }
                if (y < hgt - 1 && wall[i + w] == 0 && lab[i + w] == 0) { lab[i + w] = id; stack[sp++] = i + w; }
            }
            sizes.Add(size);
            touches.Add(edge);
        }
        // 바깥 영역은 -1 (번지지도 않음), 너무 작은 영역은 0 (이웃 색이 번져 들어감)
        var map = new int[next];
        int k = 0;
        var edgeOf = new List<bool> { false };
        for (int id = 1; id < next; id++)
        {
            map[id] = o.Outside && touches[id] ? -1 : sizes[id] < minArea ? 0 : ++k;
            if (map[id] > 0) { while (edgeOf.Count <= map[id]) edgeOf.Add(false); edgeOf[map[id]] = touches[id]; }
        }
        for (int i = 0; i < n; i++) if (lab[i] != 0) lab[i] = map[lab[i]];
        // 틈 닫기로 깎인 부분 + 선 아래까지 이웃 영역 번호로 채우기 (한 칸씩 넓히기)
        int steps = rad + under + 1;
        var front = new List<int>();
        // 바깥(-1) 영역도 함께 자라야 안쪽 색이 틈을 지나 바깥으로 새지 않음
        for (int i = 0; i < n; i++) if (lab[i] != 0) front.Add(i);
        Span<int> nb = stackalloc int[4];
        for (int st = 0; st < steps && front.Count > 0; st++)
        {
            ct.ThrowIfCancellationRequested();
            var nf = new List<int>();
            foreach (int i in front)
            {
                int id = lab[i], x = i % w;
                nb[0] = x > 0 ? i - 1 : -1; nb[1] = x < w - 1 ? i + 1 : -1; nb[2] = i - w; nb[3] = i + w;
                for (int q = 0; q < 4; q++)
                {
                    int j = nb[q];
                    if (j < 0 || j >= n || lab[j] != 0) continue;
                    lab[j] = id; nf.Add(j);
                }
            }
            front = nf;
        }
        for (int i = 0; i < n; i++) if (lab[i] < 0) lab[i] = 0;
        var edgeArr = new bool[Math.Max(edgeOf.Count, 1)];
        for (int i = 0; i < edgeOf.Count; i++) edgeArr[i] = edgeOf[i];
        return new Labels { L = lab, Count = k, Edge = edgeArr };
    }

    /// <summary>영역 번호 k 의 색 (영역마다 다른 색: 황금각 파스텔). 결과 RGB</summary>
    public static (byte r, byte g, byte b) RegionColor(int k)
    {
        double hh = (k * 137.508) % 360, s = 0.45 + (k % 3) * 0.12, l = 0.72 + (k % 2) * 0.08;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(((hh / 60) % 2) - 1)), m = l - c / 2;
        double r, g, b;
        if (hh < 60) (r, g, b) = (c, x, 0); else if (hh < 120) (r, g, b) = (x, c, 0); else if (hh < 180) (r, g, b) = (0, c, x);
        else if (hh < 240) (r, g, b) = (0, x, c); else if (hh < 300) (r, g, b) = (x, 0, c); else (r, g, b) = (c, 0, x);
        return ((byte)Raster.JsRound((r + m) * 255), (byte)Raster.JsRound((g + m) * 255), (byte)Raster.JsRound((b + m) * 255));
    }

    /// <summary>영역 번호 → 색 (autoflat.js colorize). mode: colors | one. 결과 RGBA (불투명)</summary>
    public static byte[] Colorize(int[] labels, int count, int w, int hgt, string mode = "colors", string one = "#d9d9d9")
    {
        var cols = new byte[(count + 1) * 3];
        for (int k = 1; k <= count; k++)
        {
            byte r, g, b;
            if (mode == "one") (r, g, b) = Hex(one);
            else (r, g, b) = RegionColor(k);
            cols[k * 3] = r; cols[k * 3 + 1] = g; cols[k * 3 + 2] = b;
        }
        var d = new byte[w * hgt * 4];
        for (int i = 0, p = 0; i < labels.Length; i++, p += 4)
        {
            int k = labels[i];
            if (k == 0) continue;
            d[p] = cols[k * 3]; d[p + 1] = cols[k * 3 + 1]; d[p + 2] = cols[k * 3 + 2]; d[p + 3] = 255;
        }
        return d;
    }

    public static (byte, byte, byte) Hex(string s)
    {
        s = (s ?? "").Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        byte P(int i) { try { return Convert.ToByte(s.Substring(i, 2), 16); } catch { return 0; } }
        return (P(1), P(3), P(5));
    }

    public sealed class HintResult { public byte[] Rgba; public int Hinted, Filled; }

    /// <summary>
    /// 색 점(힌트)으로 영역 칠하기 (autoflat.js colorizeHints): 영역 안에 찍힌 색 중 가장 많은 색으로.
    /// 힌트가 없는 영역은 spread 면 가장 길게 맞닿은 이웃 영역 색 (테두리에 닿는 바깥 영역은 따라가지 않음).
    /// hint: 힌트 레이어 RGBA (미리 곱하지 않음). Map 의 넣은 순서까지 웹과 같게.
    /// </summary>
    public static HintResult ColorizeHints(int[] labels, int count, bool[] edge, byte[] hint, int w, int hgt, bool spread = true)
    {
        var votes = new Dictionary<int, int>[count + 1];
        var voteOrder = new List<int>[count + 1];
        for (int i = 0, p = 3; i < labels.Length; i++, p += 4)
        {
            int k = labels[i];
            if (k == 0 || hint[p] < 128) continue;
            int key = (hint[p - 3] << 16) | (hint[p - 2] << 8) | hint[p - 1];
            var m = votes[k] ??= new Dictionary<int, int>();
            if (m.TryGetValue(key, out int c)) m[key] = c + 1;
            else { m[key] = 1; (voteOrder[k] ??= new List<int>()).Add(key); }
        }
        var color = new int[count + 1];
        Array.Fill(color, -1);
        for (int k = 1; k <= count; k++)
        {
            var m = votes[k];
            if (m == null) continue;
            int best = -1, bc = 0;
            foreach (int key in voteOrder[k]) { int c = m[key]; if (c > bc) { bc = c; best = key; } }
            color[k] = best;
        }
        var hinted = (int[])color.Clone();
        if (spread)
        {
            // 이웃 관계: 선을 사이에 둔 영역끼리 (몇 px 건너서) 맞닿은 길이
            var adj = new Dictionary<long, int>();
            var adjOrder = new List<long>();
            void Add(int a, int b)
            {
                if (a == b || a == 0 || b == 0) return;
                long key = a < b ? a * 1_000_000L + b : b * 1_000_000L + a;
                if (adj.TryGetValue(key, out int c)) adj[key] = c + 1; else { adj[key] = 1; adjOrder.Add(key); }
            }
            const int R = 4;
            for (int y = 0; y < hgt; y += 2) for (int x = 0; x < w; x += 2)
            {
                int a = labels[y * w + x];
                if (a == 0) continue;
                if (x + R < w) Add(a, labels[y * w + x + R]);
                if (y + R < hgt) Add(a, labels[(y + R) * w + x]);
            }
            var nb = new List<(int j, int c)>[count + 1];
            for (int i = 0; i <= count; i++) nb[i] = new();
            foreach (long key in adjOrder) { int c = adj[key]; int a = (int)(key / 1_000_000L), b = (int)(key % 1_000_000L); nb[a].Add((b, c)); nb[b].Add((a, c)); }
            bool changed = true;
            for (int round = 0; changed && round < 50; round++)
            {
                changed = false;
                for (int k = 1; k <= count; k++)
                {
                    if (color[k] >= 0 || (edge != null && k < edge.Length && edge[k])) continue;
                    int best = -1, bc = 0;
                    foreach (var (j, c) in nb[k]) if (color[j] >= 0 && c > bc) { bc = c; best = color[j]; }
                    if (best >= 0) { color[k] = best; changed = true; }
                }
            }
        }
        var d = new byte[w * hgt * 4];
        for (int i = 0, p = 0; i < labels.Length; i++, p += 4)
        {
            int c = color[labels[i]];
            if (labels[i] == 0 || c < 0) continue;
            d[p] = (byte)((c >> 16) & 255); d[p + 1] = (byte)((c >> 8) & 255); d[p + 2] = (byte)(c & 255); d[p + 3] = 255;
        }
        int nh = 0; for (int i = 0; i < hinted.Length; i++) if (hinted[i] >= 0) nh++;
        if (hinted[0] >= 0) nh--;
        int filled = 0; for (int i = 1; i < color.Length; i++) if (color[i] >= 0) filled++;
        return new HintResult { Rgba = d, Hinted = nh, Filled = filled };
    }
}
