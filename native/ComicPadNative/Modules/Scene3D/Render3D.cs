// 소프트웨어 3D 렌더러 (웹 판 app/js/three/render.js 의 WebGL 셰이더를 CPU 로 옮김):
// 셀(만화식) 음영 + 법선/깊이 기반 선화 추출. 결과는 미리 곱한 BGRA (Surface 와 같은 형식).
//  - what: preview(미리보기: 격자+음영+선) | color(색) | shade(음영: 그림자 색 + 알파) | line(선화)
//  - 가로줄 띠(band)로 나눠 여러 코어로 그리고, ss 배 초과 표본(슈퍼샘플링)으로 가장자리를 부드럽게.
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Scene3D;

public sealed class DrawItem { public Mesh Mesh; public double[] Model; public double[] Color; public Tex Image; }

public static class Render3D
{
    const int PREVIEW = 0, COLOR = 1, SHADE = 2, GEO = 3;

    public static double[] Hex3(string hex)
    {
        uint c = Px.Unpremul(Px.FromHex(hex, 0xFFD8D8D8));
        return new[] { ((c >> 16) & 255) / 255.0, ((c >> 8) & 255) / 255.0, (c & 255) / 255.0 };
    }

    /// <summary>장면을 그릴 목록으로 펼친다</summary>
    public static List<DrawItem> DrawList(Scene3 scene)
    {
        var o = new List<DrawItem>();
        foreach (var n in scene.Nodes)
        {
            if (!n.Visible) continue;
            var bas = M3.Trs(n.Pos, n.Rot, n.Scale);
            var col = Hex3(n.Color);
            if (n.Kind == "hand")
            {
                int side = n.Side ?? 1;
                var parts = Engine3D.HandParts(side); var mats = Engine3D.HandMatrices(n.Pose, side);
                foreach (var (k, m) in parts) o.Add(new DrawItem { Mesh = m, Model = M3.Mul(bas, mats[k]), Color = col });
            }
            else if (n.Kind == "mannequin")
            {
                var parts = Engine3D.MannequinParts(); var mats = Engine3D.MannequinMatrices(n.Pose, n.Body);
                foreach (var (k, m) in parts) o.Add(new DrawItem { Mesh = m, Model = M3.Mul(bas, mats[k]), Color = col });
            }
            else foreach (var p in n.Parts) o.Add(new DrawItem { Mesh = p.Mesh, Model = M3.Mul(bas, p.Matrix), Color = n.Tint == true ? col : (p.Color ?? col), Image = p.Image });
        }
        return o;
    }

    public sealed record CamMats(double[] View, double[] Proj, double[] Eye, double Far);

    public static CamMats Camera(Camera3 cam, double aspect)
    {
        const double d = Math.PI / 180;
        double ce = Math.Cos(cam.Pitch * d);
        double[] eye = { cam.Target[0] + cam.Dist * ce * Math.Sin(cam.Yaw * d), cam.Target[1] + cam.Dist * Math.Sin(cam.Pitch * d), cam.Target[2] + cam.Dist * ce * Math.Cos(cam.Yaw * d) };
        var view = M3.LookAt(eye, cam.Target, new double[] { 0, 1, 0 });
        double near = Math.Max(0.01, cam.Dist * 0.01), far = cam.Dist * 20 + 50;
        // 화각이 작을수록 원근감이 약해짐 (망원)
        var proj = M3.Persp(cam.Fov * d, aspect, near, far);
        return new CamMats(view, proj, eye, far);
    }

    /// <summary>월드 점 → 화면 좌표 (0..w, 0..h). 카메라 뒤면 null</summary>
    public static (double x, double y)? Project(CamMats c, double[] p, int w, int h)
    {
        var VP = M3.Mul(c.Proj, c.View);
        double x = VP[0] * p[0] + VP[4] * p[1] + VP[8] * p[2] + VP[12], y = VP[1] * p[0] + VP[5] * p[1] + VP[9] * p[2] + VP[13], ww = VP[3] * p[0] + VP[7] * p[1] + VP[11] * p[2] + VP[15];
        if (ww <= 1e-6) return null;
        return ((x / ww * 0.5 + 0.5) * w, (0.5 - y / ww * 0.5) * h);
    }

    // ---------------------------------------------------------------- 삼각형 준비
    const int VS = 13;   // 정점: sx sy z iw | nW(3)*iw | nV(3)*iw | vd*iw | u*iw v*iw
    sealed class Prepared
    {
        public float[] V = new float[3 * VS * 1024];
        public int[] Item = new int[1024];
        public float[] Y0 = new float[1024], Y1 = new float[1024];
        public int Count;
        public List<DrawItem> Items;
        public void Add(float[] a, float[] b, float[] c, int item)
        {
            if (Count >= Item.Length)
            {
                int n = Item.Length * 2;
                Array.Resize(ref V, n * 3 * VS); Array.Resize(ref Item, n); Array.Resize(ref Y0, n); Array.Resize(ref Y1, n);
            }
            int o = Count * 3 * VS;
            Array.Copy(a, 0, V, o, VS); Array.Copy(b, 0, V, o + VS, VS); Array.Copy(c, 0, V, o + 2 * VS, VS);
            Item[Count] = item;
            Y0[Count] = Math.Min(a[1], Math.Min(b[1], c[1])); Y1[Count] = Math.Max(a[1], Math.Max(b[1], c[1]));
            Count++;
        }
    }

    // 자르기용 정점: cx cy cz cw | nW(3) | nV(3) | vd | u v  (14)
    static Prepared Prepare(List<DrawItem> list, CamMats cm, int W, int H)
    {
        var P = new Prepared { Items = list };
        var view = cm.View;
        for (int it = 0; it < list.Count; it++)
        {
            var d = list[it]; var m = d.Mesh;
            if (m.Lines || m.Idx == null) continue;
            var mvp = M3.Mul(cm.Proj, M3.Mul(view, d.Model));
            var N = M3.Normal3(d.Model);
            int nv = m.Pos.Length / 3;
            var cv = new float[nv * 14];
            for (int i = 0; i < nv; i++)
            {
                double x = m.Pos[i * 3], y = m.Pos[i * 3 + 1], z = m.Pos[i * 3 + 2];
                int o = i * 14;
                cv[o] = (float)(mvp[0] * x + mvp[4] * y + mvp[8] * z + mvp[12]);
                cv[o + 1] = (float)(mvp[1] * x + mvp[5] * y + mvp[9] * z + mvp[13]);
                cv[o + 2] = (float)(mvp[2] * x + mvp[6] * y + mvp[10] * z + mvp[14]);
                cv[o + 3] = (float)(mvp[3] * x + mvp[7] * y + mvp[11] * z + mvp[15]);
                double ax = i * 3 < m.Nrm.Length ? m.Nrm[i * 3] : 0, ay = i * 3 + 1 < m.Nrm.Length ? m.Nrm[i * 3 + 1] : 1, az = i * 3 + 2 < m.Nrm.Length ? m.Nrm[i * 3 + 2] : 0;
                double nx = N[0] * ax + N[3] * ay + N[6] * az, ny = N[1] * ax + N[4] * ay + N[7] * az, nz = N[2] * ax + N[5] * ay + N[8] * az;
                double l = M3.Hyp(nx, ny, nz); if (l == 0) l = 1; nx /= l; ny /= l; nz /= l;
                double vx = view[0] * nx + view[4] * ny + view[8] * nz, vy = view[1] * nx + view[5] * ny + view[9] * nz, vz = view[2] * nx + view[6] * ny + view[10] * nz;
                l = M3.Hyp(vx, vy, vz); if (l == 0) l = 1;
                cv[o + 4] = (float)nx; cv[o + 5] = (float)ny; cv[o + 6] = (float)nz;
                cv[o + 7] = (float)(vx / l); cv[o + 8] = (float)(vy / l); cv[o + 9] = (float)(vz / l);
                cv[o + 10] = cv[o + 3];                // 시점 깊이 (-view.z) = 투영 w
                if (m.Uv != null && i * 2 + 1 < m.Uv.Length) { cv[o + 11] = m.Uv[i * 2]; cv[o + 12] = m.Uv[i * 2 + 1]; }
            }
            var poly = new List<float[]>(4); var tmp = new List<float[]>(4);
            for (int t = 0; t + 2 < m.Idx.Length; t += 3)
            {
                int a = m.Idx[t], b = m.Idx[t + 1], c = m.Idx[t + 2];
                if ((uint)a >= nv || (uint)b >= nv || (uint)c >= nv) continue;
                float wa = cv[a * 14 + 3] + cv[a * 14 + 2], wb = cv[b * 14 + 3] + cv[b * 14 + 2], wc = cv[c * 14 + 3] + cv[c * 14 + 2];
                if (wa < 0 && wb < 0 && wc < 0) continue;           // 전부 가까운 면 앞
                poly.Clear();
                poly.Add(Slice(cv, a)); poly.Add(Slice(cv, b)); poly.Add(Slice(cv, c));
                if (wa < 0 || wb < 0 || wc < 0)
                {
                    // 가까운 면(z = -w)으로 자르기 (Sutherland–Hodgman)
                    tmp.Clear();
                    for (int k = 0; k < poly.Count; k++)
                    {
                        var p0 = poly[k]; var p1 = poly[(k + 1) % poly.Count];
                        float d0 = p0[2] + p0[3], d1 = p1[2] + p1[3];
                        if (d0 >= 0) tmp.Add(p0);
                        if ((d0 >= 0) != (d1 >= 0))
                        {
                            float s = d0 / (d0 - d1); var q = new float[14];
                            for (int j = 0; j < 14; j++) q[j] = p0[j] + (p1[j] - p0[j]) * s;
                            tmp.Add(q);
                        }
                    }
                    (poly, tmp) = (tmp, poly);
                    if (poly.Count < 3) continue;
                }
                var sv = poly.Select(p => ToScreen(p, W, H)).ToList();
                for (int k = 1; k + 1 < sv.Count; k++) P.Add(sv[0], sv[k], sv[k + 1], it);
            }
        }
        return P;
    }
    static float[] Slice(float[] cv, int i) { var o = new float[14]; Array.Copy(cv, i * 14, o, 0, 14); return o; }
    static float[] ToScreen(float[] c, int W, int H)
    {
        float w = Math.Max(c[3], 1e-7f), iw = 1 / w;
        var o = new float[VS];
        o[0] = (c[0] * iw * 0.5f + 0.5f) * W; o[1] = (0.5f - c[1] * iw * 0.5f) * H; o[2] = c[2] * iw; o[3] = iw;
        for (int j = 0; j < 9; j++) o[4 + j] = c[4 + j] * iw;
        return o;
    }

    // ---------------------------------------------------------------- 조명·음영
    sealed class Shading
    {
        public double Lx, Ly, Lz, Levels, Soft, Ambient, Far;
        public double[] Shadow;
        public Shading(Style3 st, double far)
        {
            const double d = Math.PI / 180;
            Lx = Math.Cos(st.LightEl * d) * Math.Sin(st.LightAz * d); Ly = Math.Sin(st.LightEl * d); Lz = Math.Cos(st.LightEl * d) * Math.Cos(st.LightAz * d);
            double l = M3.Hyp(Lx, Ly, Lz); if (l == 0) l = 1; Lx /= l; Ly /= l; Lz /= l;
            Levels = st.Levels; Soft = st.Soft; Ambient = st.Ambient; Far = far; Shadow = Hex3(st.ShadowColor);
        }
        public double Toon(double l)
        {
            if (Levels < 0.5) return l;
            double x = l * Levels, f = Math.Floor(x), t = x - f, s = Math.Max(Soft, 0.001);
            return Math.Clamp((f + SmoothStep(0.5 - s, 0.5 + s, t)) / Levels, 0, 1);
        }
    }
    static double SmoothStep(double e0, double e1, double x) { double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }
    static uint Pack(double a, double r, double g, double b)
    {
        uint A = (uint)Math.Clamp((int)(a * 255 + 0.5), 0, 255);
        uint P(double v) => (uint)Math.Clamp((int)(v * a * 255 + 0.5), 0, (int)A);
        return (A << 24) | (P(r) << 16) | (P(g) << 8) | P(b);
    }

    static (double r, double g, double b) SampleTex(Tex t, double u, double v)
    {
        // REPEAT + 이중선형. glTF 처럼 v=0 이 그림 위쪽
        double fx = (u - Math.Floor(u)) * t.W - 0.5, fy = (v - Math.Floor(v)) * t.H - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy); double ax = fx - x0, ay = fy - y0;
        double r = 0, g = 0, b = 0;
        for (int k = 0; k < 4; k++)
        {
            int x = ((x0 + (k & 1)) % t.W + t.W) % t.W, y = ((y0 + (k >> 1)) % t.H + t.H) % t.H;
            double wgt = ((k & 1) != 0 ? ax : 1 - ax) * ((k >> 1) != 0 ? ay : 1 - ay);
            uint c = t.Px[y * t.W + x];
            r += ((c >> 16) & 255) * wgt; g += ((c >> 8) & 255) * wgt; b += (c & 255) * wgt;
        }
        return (r / 255, g / 255, b / 255);
    }

    // ---------------------------------------------------------------- 띠 하나 그리기
    /// <summary>띠(표본 줄 y0..y1)에 삼각형들을 그린다. mode GEO 면 geo(4 float: 시점 법선 xyz, 깊이/far), 아니면 col(미리 곱한 BGRA)</summary>
    static void RasterBand(Prepared P, int mode, int Wd, int y0, int y1, float[] depth, uint[] col, float[] geo, Shading sh)
    {
        var V = P.V;
        for (int t = 0; t < P.Count; t++)
        {
            if (P.Y1[t] < y0 - 1 || P.Y0[t] > y1 + 1) continue;
            int o = t * 3 * VS;
            double x0 = V[o], yy0 = V[o + 1], x1 = V[o + VS], yy1 = V[o + VS + 1], x2 = V[o + 2 * VS], yy2 = V[o + 2 * VS + 1];
            double area = (x1 - x0) * (yy2 - yy0) - (yy1 - yy0) * (x2 - x0);
            if (!(Math.Abs(area) > 1e-12)) continue;
            bool front = area < 0;                     // 화면은 y 아래 → GL(반시계 = 앞면)과 부호 반대
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2)))), maxX = Math.Min(Wd - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
            int minY = Math.Max(y0, (int)Math.Floor(Math.Min(yy0, Math.Min(yy1, yy2)))), maxY = Math.Min(y1 - 1, (int)Math.Ceiling(Math.Max(yy0, Math.Max(yy1, yy2))));
            if (minX > maxX || minY > maxY) continue;
            double inv = 1 / area;
            // 무게중심 좌표 기울기
            double a0x = -(yy2 - yy1) * inv, a0y = (x2 - x1) * inv;
            double a1x = -(yy0 - yy2) * inv, a1y = (x0 - x2) * inv;
            var item = P.Items[P.Item[t]];
            double cr = item.Color[0], cg = item.Color[1], cb = item.Color[2];
            var tex = item.Image;
            float z0 = V[o + 2], z1 = V[o + VS + 2], z2 = V[o + 2 * VS + 2];
            for (int py = minY; py <= maxY; py++)
            {
                double cy = py + 0.5, cx0 = minX + 0.5;
                double l0 = ((x2 - x1) * (cy - yy1) - (yy2 - yy1) * (cx0 - x1)) * inv;
                double l1 = ((x0 - x2) * (cy - yy2) - (yy0 - yy2) * (cx0 - x2)) * inv;
                int row = (py - y0) * Wd;
                for (int px = minX; px <= maxX; px++, l0 += a0x, l1 += a1x)
                {
                    double l2 = 1 - l0 - l1;
                    if (l0 < -1e-9 || l1 < -1e-9 || l2 < -1e-9) continue;
                    double z = l0 * z0 + l1 * z1 + l2 * z2;
                    if (z < -1 || z > 1) continue;
                    int idx = row + px;
                    if (z >= depth[idx]) continue;
                    depth[idx] = (float)z;
                    double iw = l0 * V[o + 3] + l1 * V[o + VS + 3] + l2 * V[o + 2 * VS + 3];
                    double rw = 1 / iw;
                    double I(int k) => (l0 * V[o + k] + l1 * V[o + VS + k] + l2 * V[o + 2 * VS + k]) * rw;
                    if (mode == GEO)
                    {
                        double vx = I(7), vy = I(8), vz = I(9), l = Math.Sqrt(vx * vx + vy * vy + vz * vz); if (l == 0) l = 1;
                        if (!front) l = -l;
                        int g = idx * 4;
                        geo[g] = (float)(vx / l); geo[g + 1] = (float)(vy / l); geo[g + 2] = (float)(vz / l);
                        geo[g + 3] = (float)Math.Max(I(10) / sh.Far, 0.002);
                        continue;
                    }
                    double br = cr, bg = cg, bb = cb;
                    if (tex != null) { var (tr, tg, tb) = SampleTex(tex, I(11), I(12)); br *= tr; bg *= tg; bb *= tb; }
                    if (mode == COLOR) { col[idx] = Pack(1, br, bg, bb); continue; }
                    double nx = I(4), ny = I(5), nz = I(6), nl = Math.Sqrt(nx * nx + ny * ny + nz * nz); if (nl == 0) nl = 1;
                    if (!front) nl = -nl;
                    double lam = Math.Max((nx * sh.Lx + ny * sh.Ly + nz * sh.Lz) / nl, 0);
                    double lit = sh.Toon(Math.Clamp(sh.Ambient + (1 - sh.Ambient) * lam, 0, 1));
                    if (mode == PREVIEW)
                    {
                        var s = sh.Shadow;
                        col[idx] = Pack(1, br * s[0] + (br - br * s[0]) * lit, bg * s[1] + (bg - bg * s[1]) * lit, bb * s[2] + (bb - bb * s[2]) * lit);
                    }
                    else col[idx] = Pack(1 - lit, sh.Shadow[0], sh.Shadow[1], sh.Shadow[2]);
                }
            }
        }
    }

    /// <summary>바닥 격자 (1px 선, 깊이 시험)</summary>
    static void GridBand(CamMats cm, int Wd, int Hd, int y0, int y1, float[] depth, uint[] col)
    {
        var VP = M3.Mul(cm.Proj, cm.View);
        uint gc = Pack(0.5, 0.5, 0.5, 0.55);
        double[] C(double x, double y, double z) => new[] { VP[0] * x + VP[4] * y + VP[8] * z + VP[12], VP[1] * x + VP[5] * y + VP[9] * z + VP[13], VP[2] * x + VP[6] * y + VP[10] * z + VP[14], VP[3] * x + VP[7] * y + VP[11] * z + VP[15] };
        void Seg(double[] a, double[] b)
        {
            double da = a[2] + a[3], db = b[2] + b[3];
            if (da < 0 && db < 0) return;
            if (da < 0 || db < 0)
            {
                double s = da / (da - db); var q = new double[4];
                for (int j = 0; j < 4; j++) q[j] = a[j] + (b[j] - a[j]) * s;
                if (da < 0) a = q; else b = q;
            }
            double ax = (a[0] / a[3] * 0.5 + 0.5) * Wd, ay = (0.5 - a[1] / a[3] * 0.5) * Hd, az = a[2] / a[3];
            double bx = (b[0] / b[3] * 0.5 + 0.5) * Wd, by = (0.5 - b[1] / b[3] * 0.5) * Hd, bz = b[2] / b[3];
            if (Math.Max(ay, by) < y0 - 1 || Math.Min(ay, by) > y1 + 1) return;
            double len = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
            int n = (int)Math.Min(100000, Math.Ceiling(len)) + 1;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n, x = ax + (bx - ax) * t, y = ay + (by - ay) * t, z = az + (bz - az) * t;
                int px = (int)Math.Floor(x), py = (int)Math.Floor(y);
                if (px < 0 || px >= Wd || py < y0 || py >= y1 || z < -1 || z > 1) continue;
                int idx = (py - y0) * Wd + px;
                if (z >= depth[idx]) continue;
                depth[idx] = (float)z; col[idx] = gc;
            }
        }
        for (int i = -10; i <= 10; i++) { Seg(C(i, 0, -10), C(i, 0, 10)); Seg(C(-10, 0, i), C(10, 0, i)); }
    }

    /// <summary>선 찾기 (웹 판 EDGE 셰이더): 8방향 이웃과 깊이·법선을 비교해 선의 진하기(0~1)</summary>
    static void EdgeBand(float[] geo, int Wd, int gy0, int gRows, int y0, int y1, float[] eOut, double radius, double nThr, double dThr)
    {
        var dirs = new (double dx, double dy)[8];
        for (int i = 0; i < 8; i++) { double a = i * 0.785398; dirs[i] = (Math.Cos(a) * radius, Math.Sin(a) * radius); }
        for (int y = y0; y < y1; y++)
            for (int x = 0; x < Wd; x++)
            {
                int ci = ((y - gy0) * Wd + x) * 4;
                float ca = geo[ci + 3];
                double e = 0;
                for (int i = 0; i < 8; i++)
                {
                    int nx = Math.Clamp((int)Math.Floor(x + 0.5 + dirs[i].dx), 0, Wd - 1);
                    int ny = Math.Clamp((int)Math.Floor(y + 0.5 + dirs[i].dy), gy0, gy0 + gRows - 1);
                    int ni = ((ny - gy0) * Wd + nx) * 4;
                    float na = geo[ni + 3];
                    bool cbk = ca <= 0, nbk = na <= 0;
                    if (cbk && nbk) continue;
                    if (cbk != nbk) { e = 1; break; }
                    double dd = Math.Abs(ca - na) / Math.Max(Math.Min(ca, na), 1e-4);
                    if (dd > dThr) e = Math.Max(e, 1);
                    double nd = geo[ci] * geo[ni] + geo[ci + 1] * geo[ni + 1] + geo[ci + 2] * geo[ni + 2];
                    e = Math.Max(e, SmoothStep(nThr + 0.06, nThr - 0.06, nd));
                }
                eOut[(y - y0) * Wd + x] = (float)e;
            }
    }

    // ---------------------------------------------------------------- 그리기
    /// <summary>
    /// 장면을 w×h 로 그린다 (미리 곱한 BGRA, 행 우선). what: preview | color | shade | line.
    /// lineScale: 선 굵기 배율 (원고 px 기준 굵기 × 이 값 = 이 그림의 px). ss: 초과 표본 배수 (1~4)
    /// </summary>
    public static uint[] Render(Scene3 scene, int w, int h, string what, Style3 st, double lineScale = 1, bool grid = false, int ss = 1, CancellationToken ct = default)
    {
        ss = Math.Clamp(ss, 1, 4);
        int Wd = w * ss, Hd = h * ss;
        var list = DrawList(scene);
        var cm = Camera(scene.Camera, (double)w / h);
        var P = Prepare(list, cm, Wd, Hd);
        var sh = new Shading(st, cm.Far);
        var outBuf = new uint[w * h];
        bool lines = what == "line" || (what == "preview" && st.Line);
        bool colorPass = what != "line";
        int mode = what == "color" ? COLOR : what == "shade" ? SHADE : PREVIEW;
        double radius = Math.Max(1, st.LineWidth * lineScale) * 0.5 * ss;
        int pad = lines ? (int)Math.Ceiling(radius) + 2 : 0;
        uint lineC = Pack(1, 0, 0, 0); var lc = Hex3(st.LineColor);
        int bandOut = Math.Max(8, Math.Min(64, 2_000_000 / Math.Max(1, Wd * ss)));       // 띠 높이 (결과 줄)
        int bands = (h + bandOut - 1) / bandOut;
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, bands, po, b =>
        {
            int oy0 = b * bandOut, oy1 = Math.Min(h, oy0 + bandOut);
            int y0 = oy0 * ss, y1 = oy1 * ss, rows = y1 - y0;
            uint[] col = null; float[] e = null;
            if (colorPass)
            {
                col = new uint[Wd * rows];
                var depth = new float[Wd * rows]; Array.Fill(depth, 1f);
                if (what == "preview" && grid) GridBand(cm, Wd, Hd, y0, y1, depth, col);
                RasterBand(P, mode, Wd, y0, y1, depth, col, null, sh);
            }
            if (lines)
            {
                int gy0 = Math.Max(0, y0 - pad), gy1 = Math.Min(Hd, y1 + pad), gRows = gy1 - gy0;
                var geo = new float[Wd * gRows * 4];
                var depth = new float[Wd * gRows]; Array.Fill(depth, 1f);
                RasterBand(P, GEO, Wd, gy0, gy1, depth, null, geo, sh);
                e = new float[Wd * rows];
                EdgeBand(geo, Wd, gy0, gRows, y0, y1, e, radius, st.NThr, st.DThr);
            }
            // 내려 받기 (ss×ss 평균) + 선 얹기
            int n = ss * ss;
            for (int oy = oy0; oy < oy1; oy++)
                for (int ox = 0; ox < w; ox++)
                {
                    uint pix = 0;
                    if (col != null)
                    {
                        uint sa = 0, sr = 0, sg = 0, sb = 0;
                        for (int j = 0; j < ss; j++) for (int i = 0; i < ss; i++)
                        {
                            uint c = col[((oy - oy0) * ss + j) * Wd + ox * ss + i];
                            sa += c >> 24; sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255;
                        }
                        pix = n == 1 ? Px.Pack(sa, sr, sg, sb) : Px.Pack((uint)((sa + n / 2) / n), (uint)((sr + n / 2) / n), (uint)((sg + n / 2) / n), (uint)((sb + n / 2) / n));
                    }
                    if (e != null)
                    {
                        double ea = 0;
                        for (int j = 0; j < ss; j++) for (int i = 0; i < ss; i++) ea += e[((oy - oy0) * ss + j) * Wd + ox * ss + i];
                        ea /= n;
                        if (ea > 0) pix = Px.Over(Pack(ea, lc[0], lc[1], lc[2]), pix);
                    }
                    outBuf[oy * w + ox] = pix;
                }
        });
        return outBuf;
    }

    /// <summary>원고 크기 레이어용: w×h 를 그린 뒤 Surface 로 (rect 밖은 비움)</summary>
    public static Surface RenderSurface(Scene3 scene, int w, int h, string what, Style3 st, double lineScale, int ss)
    {
        var buf = Render(scene, w, h, what, st, lineScale, false, ss);
        var s = new Surface(w, h);
        s.Write(new Rect(0, 0, w, h), buf);
        return s;
    }
}
