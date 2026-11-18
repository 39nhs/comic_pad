// 아트메쉬 자동 만들기: 레이어 알파 → (조금 넓힌) 윤곽선 점 + 안쪽 점(육각 격자) → 들로네 삼각형 → 그림이 있는 삼각형만.
// 윤곽은 알파를 2px 넓힌 모양을 따라가므로 가장자리의 반투명 픽셀까지 메쉬 안에 들어간다 (쌍선형 샘플이 가장자리에서 흐려지지 않게).
// 손으로 고친 메쉬는 Retriangulate 로 다시 삼각형을 만들고, 키폼 오프셋은 TransferOffsets 로 새 꼭짓점에 옮긴다.
namespace ComicPadNative.Modules.VRig2;

public static class MeshGen
{
    /// <summary>알파 판 (작품 좌표 rect 안, 0~255)</summary>
    public sealed class AlphaMap
    {
        public int X, Y, W, H; public byte[] A;
        public byte At(int x, int y) { x -= X; y -= Y; return (uint)x < (uint)W && (uint)y < (uint)H ? A[y * W + x] : (byte)0; }
    }

    /// <summary>텍스처 알파 (region 이 있으면 그 밖은 0)</summary>
    public static AlphaMap Alpha(RigTexture tex, double[] region)
    {
        var m = new AlphaMap { X = tex.X, Y = tex.Y, W = tex.W, H = tex.H, A = new byte[tex.W * tex.H] };
        var p = tex.Mip[0];
        for (int y = 0; y < tex.H; y++)
            for (int x = 0; x < tex.W; x++)
            {
                if (region != null)
                {
                    double dx = tex.X + x + 0.5, dy = tex.Y + y + 0.5;
                    if (dx < region[0] || dy < region[1] || dx >= region[0] + region[2] || dy >= region[1] + region[3]) continue;
                }
                m.A[y * tex.W + x] = (byte)(p[y * tex.W + x] >> 24);
            }
        return m;
    }

    public static string LastStats = "";
    /// <summary>density(1~10) → 점 간격 (px)</summary>
    public static double Spacing(int w, int h, double density) => Math.Max(4, Math.Sqrt((double)w * h) / (3 + 2 * Math.Clamp(density, 0.5, 12)));

    /// <summary>자동 메쉬</summary>
    public static RMesh Auto(RigTexture tex, double density, double[] region)
    {
        var am = Alpha(tex, region);
        // 그림이 있는 범위
        int x0 = am.W, y0 = am.H, x1 = -1, y1 = -1;
        for (int y = 0; y < am.H; y++) for (int x = 0; x < am.W; x++) if (am.A[y * am.W + x] > 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 < 0) return new RMesh();
        const int R = 2;
        // 넓힌 마스크 (가장자리 R px 여유, 판 밖으로도 R+1 px)
        int lox = x0 - R - 1, loy = y0 - R - 1, mw = x1 - x0 + 1 + 2 * (R + 1), mh = y1 - y0 + 1 + 2 * (R + 1);
        int ox = am.X + lox, oy = am.Y + loy;          // 마스크 (0,0) 의 작품 좌표
        var src = new bool[mw * mh];
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) if (am.A[y * am.W + x] > 0) src[(y - loy) * mw + (x - lox)] = true;
        var dil = Dilate(src, mw, mh, R);
        double s = Spacing(x1 - x0 + 1, y1 - y0 + 1, density);
        var pts = new List<(double x, double y)>();
        // 1) 윤곽 (덩어리마다)
        var label = new int[mw * mh];
        int nLab = 0;
        var stack = new Stack<int>();
        var contourPts = new List<(double x, double y)>();
        for (int i = 0; i < mw * mh; i++)
        {
            if (!dil[i] || label[i] != 0) continue;
            nLab++;
            int area = 0, bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
            stack.Push(i); label[i] = nLab;
            while (stack.Count > 0)
            {
                int k = stack.Pop(); area++;
                int kx = k % mw, ky = k / mw;
                if (kx < bx0) bx0 = kx; if (kx > bx1) bx1 = kx; if (ky < by0) by0 = ky; if (ky > by1) by1 = ky;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = kx + dx, ny = ky + dy;
                    if ((uint)nx >= (uint)mw || (uint)ny >= (uint)mh) continue;
                    int q = ny * mw + nx;
                    if (dil[q] && label[q] == 0) { label[q] = nLab; stack.Push(q); }
                }
            }
            if (area < s * s / 3 || bx1 - bx0 < 4 || by1 - by0 < 4)
            {
                // 작은 덩어리: 둘레 상자 네 귀만
                contourPts.Add((bx0 + ox, by0 + oy)); contourPts.Add((bx1 + 1 + ox, by0 + oy)); contourPts.Add((bx0 + ox, by1 + 1 + oy)); contourPts.Add((bx1 + 1 + ox, by1 + 1 + oy));
                continue;
            }
            var c = Trace(dil, mw, mh, i);
            var simp = Simplify(c, 0.8);
            // 간격보다 긴 변은 나눔
            for (int k = 0; k < simp.Count; k++)
            {
                var a = simp[k]; var b = simp[(k + 1) % simp.Count];
                double len = Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
                int n = Math.Max(1, (int)Math.Ceiling(len / (s * 0.75)));
                for (int t = 0; t < n; t++) contourPts.Add((a.x + (b.x - a.x) * t / n + ox + 0.5, a.y + (b.y - a.y) * t / n + oy + 0.5));
            }
        }
        // 너무 가까운 윤곽 점 합치기
        var grid = new Dictionary<(int, int), List<(double x, double y)>>();
        double cell = Math.Max(1, s * 0.3);
        bool Near(double x, double y, double r)
        {
            int cx = (int)Math.Floor(x / cell), cy = (int)Math.Floor(y / cell), rr = (int)Math.Ceiling(r / cell);
            for (int j = cy - rr; j <= cy + rr; j++) for (int i2 = cx - rr; i2 <= cx + rr; i2++)
                if (grid.TryGetValue((i2, j), out var l)) foreach (var q in l) if ((q.x - x) * (q.x - x) + (q.y - y) * (q.y - y) < r * r) return true;
            return false;
        }
        void Put(double x, double y)
        {
            var key = ((int)Math.Floor(x / cell), (int)Math.Floor(y / cell));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<(double, double)>();
            l.Add((x, y)); pts.Add((x, y));
        }
        foreach (var p in contourPts) if (!Near(p.x, p.y, Math.Min(2.5, s * 0.25))) Put(p.x, p.y);
        // 2) 안쪽 점 (육각 격자)
        double rowH = s * Math.Sqrt(3) / 2;
        int row = 0;
        for (double y = oy + s * 0.5; y < oy + mh; y += rowH, row++)
            for (double x = ox + (row % 2 == 0 ? s * 0.5 : s); x < ox + mw; x += s)
            {
                int ix = (int)(x - ox), iy = (int)(y - oy);
                if ((uint)ix >= (uint)mw || (uint)iy >= (uint)mh || !dil[iy * mw + ix]) continue;
                if (Near(x, y, s * 0.55)) continue;
                Put(x, y);
            }
        var mesh = new RMesh { V = new double[pts.Count * 2] };
        for (int i = 0; i < pts.Count; i++) { mesh.V[i * 2] = Math.Round(pts[i].x, 2); mesh.V[i * 2 + 1] = Math.Round(pts[i].y, 2); }
        var del = Delaunay(mesh.V);
        mesh.T = KeepOpaque(mesh.V, del, am);
        LastStats = $"윤곽 {contourPts.Count} 점 {pts.Count} 들로네 {del.Length / 3} 남김 {mesh.T.Length / 3} 간격 {s:0.0} 덩어리 {nLab}";
        return Compact(mesh);
    }

    /// <summary>손으로 고친 꼭짓점으로 다시 삼각형 (그림이 있는 삼각형만)</summary>
    public static int[] Retriangulate(double[] v, RigTexture tex, double[] region)
    {
        var am = Alpha(tex, region);
        return KeepOpaque(v, Delaunay(v), am);
    }

    /// <summary>쓰지 않는 꼭짓점 빼기 (자동 메쉬 전용)</summary>
    static RMesh Compact(RMesh m)
    {
        var used = new bool[m.N];
        foreach (var i in m.T) used[i] = true;
        if (used.All(u => u)) return m;
        var remap = new int[m.N]; var v = new List<double>(); int n = 0;
        for (int i = 0; i < m.N; i++) { if (!used[i]) { remap[i] = -1; continue; } remap[i] = n++; v.Add(m.V[i * 2]); v.Add(m.V[i * 2 + 1]); }
        return new RMesh { V = v.ToArray(), T = m.T.Select(i => remap[i]).ToArray() };
    }

    static bool[] Dilate(bool[] m, int w, int h, int r)
    {
        // 원 모양으로 넓힘 (r 작음 → 직접)
        var o = new bool[w * h];
        var offs = new List<(int, int)>();
        for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) if (dx * dx + dy * dy <= r * r + 1) offs.Add((dx, dy));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!m[y * w + x]) continue;
                // 안쪽 픽셀(네 이웃이 모두 참)은 건너뜀
                if (x > 0 && y > 0 && x < w - 1 && y < h - 1 && m[y * w + x - 1] && m[y * w + x + 1] && m[(y - 1) * w + x] && m[(y + 1) * w + x]) { o[y * w + x] = true; continue; }
                foreach (var (dx, dy) in offs) { int nx = x + dx, ny = y + dy; if ((uint)nx < (uint)w && (uint)ny < (uint)h) o[ny * w + nx] = true; }
            }
        return o;
    }

    /// <summary>무어 이웃 따라가기: 덩어리 바깥 경계 픽셀 (시계 방향)</summary>
    static List<(double x, double y)> Trace(bool[] m, int w, int h, int start)
    {
        bool At(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h && m[y * w + x];
        int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 }, dy = { 0, 1, 1, 1, 0, -1, -1, -1 };
        int sx = start % w, sy = start / w;
        var o = new List<(double, double)> { (sx, sy) };
        int cx = sx, cy = sy, dir = 7;          // 시작 픽셀은 맨 위 왼쪽 → 왼쪽(4)에서 들어왔다고 봄
        int limit = w * h * 4, firstDir = -1;
        for (int it = 0; it < limit; it++)
        {
            int found = -1;
            int startDir = (dir + (dir % 2 == 0 ? 7 : 6)) % 8;   // 들어온 방향의 왼쪽부터 시계 방향으로 찾음
            for (int k = 0; k < 8; k++)
            {
                int d = (startDir + k) % 8;
                if (At(cx + dx[d], cy + dy[d])) { found = d; break; }
            }
            if (found < 0) break;               // 외딴 한 픽셀
            if (cx == sx && cy == sy) { if (firstDir == found) break; if (firstDir < 0) firstDir = found; }   // 처음과 같은 걸음이면 한 바퀴 (제이컵 멈춤)
            cx += dx[found]; cy += dy[found]; dir = found;
            if (!(cx == sx && cy == sy)) o.Add((cx, cy));
        }
        return o;
    }

    /// <summary>더글러스-포커 (닫힌 선)</summary>
    static List<(double x, double y)> Simplify(List<(double x, double y)> pts, double eps)
    {
        if (pts.Count < 4) return pts;
        // 가장 먼 두 점으로 나눠 두 열린 선처럼
        int far = 0; double best = -1;
        for (int i = 1; i < pts.Count; i++) { double d = (pts[i].x - pts[0].x) * (pts[i].x - pts[0].x) + (pts[i].y - pts[0].y) * (pts[i].y - pts[0].y); if (d > best) { best = d; far = i; } }
        var keep = new bool[pts.Count]; keep[0] = keep[far] = true;
        void Rec(int a, int b)
        {
            // a → b (원형 번호)
            int n = (b - a + pts.Count) % pts.Count;
            if (n < 2) return;
            var p = pts[a]; var q = pts[b % pts.Count];
            double lx = q.x - p.x, ly = q.y - p.y, ll = Math.Sqrt(lx * lx + ly * ly);
            int bi = -1; double bd = eps;
            for (int k = 1; k < n; k++)
            {
                var r = pts[(a + k) % pts.Count];
                double d = ll < 1e-9 ? Math.Sqrt((r.x - p.x) * (r.x - p.x) + (r.y - p.y) * (r.y - p.y)) : Math.Abs((r.x - p.x) * ly - (r.y - p.y) * lx) / ll;
                if (d > bd) { bd = d; bi = (a + k) % pts.Count; }
            }
            if (bi < 0) return;
            keep[bi] = true;
            Rec(a, bi); Rec(bi, b);
        }
        Rec(0, far); Rec(far, 0 + pts.Count);
        var o = new List<(double, double)>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) o.Add(pts[i]);
        return o;
    }

    /// <summary>들로네 삼각분할 (보이어-왓슨)</summary>
    public static int[] Delaunay(double[] v)
    {
        int n = v.Length / 2;
        if (n < 3) return Array.Empty<int>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var px = new double[n + 3]; var py = new double[n + 3];
        for (int i = 0; i < n; i++)
        {
            // 같은 원 위의 점 때문에 생기는 흔들림을 막는 아주 작은 어긋남 (항상 같음)
            px[i] = v[i * 2] + ((i * 7919) % 13 - 6) * 1e-5; py[i] = v[i * 2 + 1] + ((i * 104729) % 11 - 5) * 1e-5;
            minX = Math.Min(minX, px[i]); minY = Math.Min(minY, py[i]); maxX = Math.Max(maxX, px[i]); maxY = Math.Max(maxY, py[i]);
        }
        double d = Math.Max(maxX - minX, maxY - minY) * 20 + 10, mx = (minX + maxX) / 2, my = (minY + maxY) / 2;
        px[n] = mx - d; py[n] = my - d; px[n + 1] = mx + d; py[n + 1] = my - d; px[n + 2] = mx; py[n + 2] = my + d;
        var tris = new List<(int a, int b, int c, double cx, double cy, double r2)>();
        (int, int, int, double, double, double) Make(int a, int b, int c)
        {
            double ax = px[a], ay = py[a], bx = px[b], by = py[b], cx = px[c], cy = py[c];
            double dd = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(dd) < 1e-12) return (a, b, c, ax, ay, double.MaxValue);
            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / dd, uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / dd;
            return (a, b, c, ux, uy, (ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));
        }
        tris.Add(Make(n, n + 1, n + 2));
        var edges = new Dictionary<long, int>();
        var bad = new List<int>();
        for (int i = 0; i < n; i++)
        {
            double x = px[i], y = py[i];
            bad.Clear();
            for (int t = 0; t < tris.Count; t++) { var tr = tris[t]; double ddx = x - tr.cx, ddy = y - tr.cy; if (ddx * ddx + ddy * ddy <= tr.r2 * (1 + 1e-12)) bad.Add(t); }
            edges.Clear();
            void E(int a, int b) { long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a; edges[k] = edges.TryGetValue(k, out var c) ? c + 1 : 1; }
            foreach (int t in bad) { var tr = tris[t]; E(tr.a, tr.b); E(tr.b, tr.c); E(tr.c, tr.a); }
            for (int k = bad.Count - 1; k >= 0; k--) { tris[bad[k]] = tris[^1]; tris.RemoveAt(tris.Count - 1); }
            foreach (var (k, c) in edges) if (c == 1) tris.Add(Make((int)(k >> 32), (int)(k & 0xffffffff), i));
        }
        var o = new List<int>();
        foreach (var t in tris)
        {
            if (t.a >= n || t.b >= n || t.c >= n) continue;
            // 시계 방향으로 맞춤 (화면 y 아래)
            double cr = (px[t.b] - px[t.a]) * (py[t.c] - py[t.a]) - (py[t.b] - py[t.a]) * (px[t.c] - px[t.a]);
            if (Math.Abs(cr) < 1e-9) continue;
            if (cr > 0) { o.Add(t.a); o.Add(t.b); o.Add(t.c); } else { o.Add(t.a); o.Add(t.c); o.Add(t.b); }
        }
        return o.ToArray();
    }

    /// <summary>픽셀 가운데가 삼각형 안에 있는지 따라 픽셀마다 f(x,y) 부름 (렌더러와 같은 규칙)</summary>
    public static void ForPixels(double ax, double ay, double bx, double by, double cx, double cy, Action<int, int> f)
    {
        // y 순서로
        if (ay > by || (ay == by && ax > bx)) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
        if (by > cy || (by == cy && bx > cx)) { (bx, cx) = (cx, bx); (by, cy) = (cy, by); }
        if (ay > by || (ay == by && ax > bx)) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
        int ys = (int)Math.Ceiling(ay - 0.5), ye = (int)Math.Ceiling(cy - 0.5);
        for (int y = ys; y < ye; y++)
        {
            double yc = y + 0.5;
            double xl = EdgeX(ax, ay, cx, cy, yc);
            double xr = yc < by ? EdgeX(ax, ay, bx, by, yc) : EdgeX(bx, by, cx, cy, yc);
            if (xl > xr) (xl, xr) = (xr, xl);
            int xs = (int)Math.Ceiling(xl - 0.5), xe = (int)Math.Ceiling(xr - 0.5);
            for (int x = xs; x < xe; x++) f(x, y);
        }
    }
    static double EdgeX(double x0, double y0, double x1, double y1, double y) => y1 == y0 ? x0 : x0 + (y - y0) * (x1 - x0) / (y1 - y0);

    /// <summary>그림(알파 > 0)이 있는 픽셀을 하나라도 덮는 삼각형만</summary>
    static int[] KeepOpaque(double[] v, int[] t, AlphaMap am)
    {
        var o = new List<int>();
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            int a = t[i], b = t[i + 1], c = t[i + 2];
            bool any = false;
            ForPixelsUntil(v[a * 2], v[a * 2 + 1], v[b * 2], v[b * 2 + 1], v[c * 2], v[c * 2 + 1], (x, y) => any = am.At(x, y) > 0);
            if (any) { o.Add(a); o.Add(b); o.Add(c); }
        }
        return o.ToArray();
    }
    static void ForPixelsUntil(double ax, double ay, double bx, double by, double cx, double cy, Func<int, int, bool> f)
    {
        if (ay > by || (ay == by && ax > bx)) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
        if (by > cy || (by == cy && bx > cx)) { (bx, cx) = (cx, bx); (by, cy) = (cy, by); }
        if (ay > by || (ay == by && ax > bx)) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
        int ys = (int)Math.Ceiling(ay - 0.5), ye = (int)Math.Ceiling(cy - 0.5);
        for (int y = ys; y < ye; y++)
        {
            double yc = y + 0.5;
            double xl = EdgeX(ax, ay, cx, cy, yc);
            double xr = yc < by ? EdgeX(ax, ay, bx, by, yc) : EdgeX(bx, by, cx, cy, yc);
            if (xl > xr) (xl, xr) = (xr, xl);
            int xs = (int)Math.Ceiling(xl - 0.5), xe = (int)Math.Ceiling(xr - 0.5);
            for (int x = xs; x < xe; x++) if (f(x, y)) return;
        }
    }

    /// <summary>메쉬가 덮는 불투명 픽셀 비율 (알파 > thr) → (덮음, 전체)</summary>
    public static (int covered, int total) Coverage(RMesh m, RigTexture tex, double[] region, int thr = 8)
    {
        var am = Alpha(tex, region);
        var hit = new bool[am.W * am.H];
        for (int i = 0; i + 2 < m.T.Length; i += 3)
        {
            int a = m.T[i], b = m.T[i + 1], c = m.T[i + 2];
            ForPixels(m.V[a * 2], m.V[a * 2 + 1], m.V[b * 2], m.V[b * 2 + 1], m.V[c * 2], m.V[c * 2 + 1], (x, y) =>
            {
                x -= am.X; y -= am.Y;
                if ((uint)x < (uint)am.W && (uint)y < (uint)am.H) hit[y * am.W + x] = true;
            });
        }
        int cov = 0, tot = 0;
        for (int i = 0; i < am.A.Length; i++) if (am.A[i] > thr) { tot++; if (hit[i]) cov++; }
        return (cov, tot);
    }

    /// <summary>옛 메쉬의 키폼 오프셋을 새 꼭짓점으로 (옛 삼각형 안이면 무게 중심 보간, 밖이면 가장 가까운 꼭짓점)</summary>
    public static double[] TransferOffsets(RMesh old, double[] oldD, double[] newV)
    {
        int n = newV.Length / 2;
        var o = new double[n * 2];
        if (oldD == null || old.N == 0) return o;
        for (int i = 0; i < n; i++)
        {
            double x = newV[i * 2], y = newV[i * 2 + 1];
            bool done = false;
            for (int t = 0; t + 2 < old.T.Length && !done; t += 3)
            {
                int a = old.T[t], b = old.T[t + 1], c = old.T[t + 2];
                double ax = old.V[a * 2], ay = old.V[a * 2 + 1], bx = old.V[b * 2], by = old.V[b * 2 + 1], cx = old.V[c * 2], cy = old.V[c * 2 + 1];
                double det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (Math.Abs(det) < 1e-9) continue;
                double l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / det, l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / det, l3 = 1 - l1 - l2;
                if (l1 < -1e-6 || l2 < -1e-6 || l3 < -1e-6) continue;
                o[i * 2] = l1 * oldD[a * 2] + l2 * oldD[b * 2] + l3 * oldD[c * 2];
                o[i * 2 + 1] = l1 * oldD[a * 2 + 1] + l2 * oldD[b * 2 + 1] + l3 * oldD[c * 2 + 1];
                done = true;
            }
            if (done) continue;
            int best = 0; double bd = double.MaxValue;
            for (int k = 0; k < old.N; k++) { double d = (old.V[k * 2] - x) * (old.V[k * 2] - x) + (old.V[k * 2 + 1] - y) * (old.V[k * 2 + 1] - y); if (d < bd) { bd = d; best = k; } }
            o[i * 2] = oldD[best * 2]; o[i * 2 + 1] = oldD[best * 2 + 1];
        }
        return o;
    }
}
