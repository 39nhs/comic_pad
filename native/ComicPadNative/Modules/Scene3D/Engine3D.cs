// 작은 3D 엔진 (웹 판 app/js/three/engine.js 이식): 행렬 수학, 기본 도형, 데생 인형, 손, 포즈 모음, GLB/GLTF/OBJ 불러오기.
// 행렬은 웹 판과 같은 열 우선 4x4 (double[16]).
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.Scene3D;

/// <summary>4x4 행렬 (열 우선) — 웹 판 M 과 같은 함수들</summary>
public static class M3
{
    public static double[] Ident() { var m = new double[16]; m[0] = m[5] = m[10] = m[15] = 1; return m; }
    public static double[] Mul(double[] a, double[] b)
    {
        var o = new double[16];
        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
                o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3];
        return o;
    }
    public static double[] Mul(params double[][] ms) { var o = ms[0]; for (int i = 1; i < ms.Length; i++) o = Mul(o, ms[i]); return o; }
    public static double[] Translate(double x, double y, double z) { var m = Ident(); m[12] = x; m[13] = y; m[14] = z; return m; }
    public static double[] Translate(double[] v) => Translate(v[0], v[1], v[2]);
    public static double[] Scale(double x, double y, double z) { var m = Ident(); m[0] = x; m[5] = y; m[10] = z; return m; }
    public static double[] Scale(double s) => Scale(s, s, s);
    public static double[] RotX(double a) { var m = Ident(); double c = Math.Cos(a), s = Math.Sin(a); m[5] = c; m[6] = s; m[9] = -s; m[10] = c; return m; }
    public static double[] RotY(double a) { var m = Ident(); double c = Math.Cos(a), s = Math.Sin(a); m[0] = c; m[2] = -s; m[8] = s; m[10] = c; return m; }
    public static double[] RotZ(double a) { var m = Ident(); double c = Math.Cos(a), s = Math.Sin(a); m[0] = c; m[1] = s; m[4] = -s; m[5] = c; return m; }
    /// <summary>회전 각(도) [x,y,z] → Rz·Ry·Rx (x 먼저)</summary>
    public static double[] Euler(double[] r) { const double d = Math.PI / 180; return Mul(RotZ(r[2] * d), Mul(RotY(r[1] * d), RotX(r[0] * d))); }
    public static double[] Trs(double[] t, double[] r, double[] s) => Mul(Translate(t), Mul(Euler(r), Scale(s[0], s[1], s[2])));
    public static double[] Quat(double[] q)
    {
        double x = q[0], y = q[1], z = q[2], w = q[3];
        var m = Ident();
        m[0] = 1 - 2 * (y * y + z * z); m[1] = 2 * (x * y + z * w); m[2] = 2 * (x * z - y * w);
        m[4] = 2 * (x * y - z * w); m[5] = 1 - 2 * (x * x + z * z); m[6] = 2 * (y * z + x * w);
        m[8] = 2 * (x * z + y * w); m[9] = 2 * (y * z - x * w); m[10] = 1 - 2 * (x * x + y * y);
        return m;
    }
    public static double[] Persp(double fovy, double aspect, double near, double far)
    {
        double f = 1 / Math.Tan(fovy / 2); var m = new double[16];
        m[0] = f / aspect; m[5] = f; m[10] = (far + near) / (near - far); m[11] = -1; m[14] = 2 * far * near / (near - far);
        return m;
    }
    public static double[] LookAt(double[] e, double[] t, double[] up)
    {
        double zx = e[0] - t[0], zy = e[1] - t[1], zz = e[2] - t[2];
        double l = Hyp(zx, zy, zz); if (l == 0) l = 1; zx /= l; zy /= l; zz /= l;
        double xx = up[1] * zz - up[2] * zy, xy = up[2] * zx - up[0] * zz, xz = up[0] * zy - up[1] * zx;
        l = Hyp(xx, xy, xz); if (l == 0) l = 1; xx /= l; xy /= l; xz /= l;
        double yx = zy * xz - zz * xy, yy = zz * xx - zx * xz, yz = zx * xy - zy * xx;
        var m = Ident();
        m[0] = xx; m[4] = xy; m[8] = xz; m[1] = yx; m[5] = yy; m[9] = yz; m[2] = zx; m[6] = zy; m[10] = zz;
        m[12] = -(xx * e[0] + xy * e[1] + xz * e[2]); m[13] = -(yx * e[0] + yy * e[1] + yz * e[2]); m[14] = -(zx * e[0] + zy * e[1] + zz * e[2]);
        return m;
    }
    /// <summary>법선 행렬 (왼쪽 위 3x3 의 역전치)</summary>
    public static double[] Normal3(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double det = a * A + b * B + c * C; if (det == 0) det = 1e-9;
        return new[] { A / det, B / det, C / det, -(b * i - c * h) / det, (a * i - c * g) / det, -(a * h - b * g) / det, (b * f - c * e) / det, -(a * f - c * d) / det, (a * e - b * d) / det };
    }
    public static double[] Point(double[] m, double x, double y, double z) =>
        new[] { m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14] };
    public static double Hyp(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);
}

/// <summary>삼각형 메시 (lines = 선분 목록)</summary>
public sealed class Mesh
{
    public float[] Pos, Nrm, Uv;
    public int[] Idx;
    public bool Lines;
    public int VertexCount => Pos.Length / 3;
}

/// <summary>모델 텍스처 (미리 곱하지 않은 ARGB)</summary>
public sealed class Tex { public int W, H; public uint[] Px; }

/// <summary>모델 한 조각: 메시 + 행렬 + 색 (+ 텍스처)</summary>
public sealed class Part { public Mesh Mesh; public double[] Matrix; public double[] Color; public Tex Image; }

public static class Engine3D
{
    // ---------------------------------------------------------------- 메시
    public static float[] ComputeNormals(float[] pos, int[] idx)
    {
        var n = new float[pos.Length];
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int a = idx[i] * 3, b = idx[i + 1] * 3, c = idx[i + 2] * 3;
            if (a + 2 >= pos.Length || b + 2 >= pos.Length || c + 2 >= pos.Length) continue;
            float ux = pos[b] - pos[a], uy = pos[b + 1] - pos[a + 1], uz = pos[b + 2] - pos[a + 2];
            float vx = pos[c] - pos[a], vy = pos[c + 1] - pos[a + 1], vz = pos[c + 2] - pos[a + 2];
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            foreach (int k in new[] { a, b, c }) { n[k] += nx; n[k + 1] += ny; n[k + 2] += nz; }
        }
        for (int i = 0; i < n.Length; i += 3) { float l = MathF.Sqrt(n[i] * n[i] + n[i + 1] * n[i + 1] + n[i + 2] * n[i + 2]); if (l == 0) l = 1; n[i] /= l; n[i + 1] /= l; n[i + 2] /= l; }
        return n;
    }

    static Mesh Make(List<float> pos, List<float> nrm, List<int> idx, List<float> uv = null) => new() { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Idx = idx.ToArray(), Uv = uv?.ToArray() };

    public static Mesh BoxMesh(double w = 1, double h = 1, double d = 1)
    {
        float x = (float)(w / 2), y = (float)(h / 2), z = (float)(d / 2);
        var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<int>();
        void Face(int[] n, int[] u, int[] v)
        {
            int b = pos.Count / 3;
            foreach (var (su, sv) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                pos.Add(n[0] * x + u[0] * su * x + v[0] * sv * x); pos.Add(n[1] * y + u[1] * su * y + v[1] * sv * y); pos.Add(n[2] * z + u[2] * su * z + v[2] * sv * z);
                nrm.Add(n[0]); nrm.Add(n[1]); nrm.Add(n[2]);
            }
            idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        Face(new[] { 1, 0, 0 }, new[] { 0, 0, -1 }, new[] { 0, 1, 0 }); Face(new[] { -1, 0, 0 }, new[] { 0, 0, 1 }, new[] { 0, 1, 0 });
        Face(new[] { 0, 1, 0 }, new[] { 1, 0, 0 }, new[] { 0, 0, -1 }); Face(new[] { 0, -1, 0 }, new[] { 1, 0, 0 }, new[] { 0, 0, 1 });
        Face(new[] { 0, 0, 1 }, new[] { 1, 0, 0 }, new[] { 0, 1, 0 }); Face(new[] { 0, 0, -1 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 });
        return Make(pos, nrm, idx);
    }

    /// <summary>캡슐: y=0 에서 y=-len 까지, 반지름 r (len=0 이면 구). 아래 반구는 r2</summary>
    public static Mesh CapsuleMesh(double r = 0.5, double len = 0, int seg = 20, int rings = 12, double? r2n = null)
    {
        double r2 = r2n ?? r;
        var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<int>();
        int rowsN = rings * 2 + 2;
        for (int i = 0; i < rowsN; i++)
        {
            bool top = i <= rings;
            double th = (top ? (double)i / rings : (double)(i - 1) / rings) * Math.PI / 2;
            double theta = top ? th : Math.PI / 2 + ((double)(i - rings - 1) / rings) * Math.PI / 2;
            double off = top ? 0 : -len;
            for (int j = 0; j <= seg; j++)
            {
                double ph = (double)j / seg * Math.PI * 2;
                double nx = Math.Sin(theta) * Math.Cos(ph), ny = Math.Cos(theta), nz = Math.Sin(theta) * Math.Sin(ph);
                double rr = top ? r : r2;
                pos.Add((float)(nx * rr)); pos.Add((float)(ny * rr + off)); pos.Add((float)(nz * rr));
                nrm.Add((float)nx); nrm.Add((float)ny); nrm.Add((float)nz);
            }
        }
        for (int i = 0; i < rowsN - 1; i++)
            for (int j = 0; j < seg; j++)
            {
                int a = i * (seg + 1) + j, b = a + 1, c = a + seg + 1, d = c + 1;
                idx.AddRange(new[] { a, b, c, b, d, c });   // 바깥에서 볼 때 반시계 (= 앞면)
            }
        return Make(pos, nrm, idx);
    }

    public static Mesh SphereMesh(double r = 0.5) => CapsuleMesh(r, 0, 28, 14);

    public static Mesh CylinderMesh(double r = 0.5, double h = 1, int seg = 28, double? rTopN = null)
    {
        double rTop = rTopN ?? r;
        var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<int>();
        double slope = (r - rTop) / h;
        for (int j = 0; j <= seg; j++)
        {
            double ph = (double)j / seg * Math.PI * 2, c = Math.Cos(ph), s = Math.Sin(ph);
            double l = Math.Sqrt(1 + slope * slope);
            pos.AddRange(new[] { (float)(c * r), 0f, (float)(s * r), (float)(c * rTop), (float)h, (float)(s * rTop) });
            nrm.AddRange(new[] { (float)(c / l), (float)(slope / l), (float)(s / l), (float)(c / l), (float)(slope / l), (float)(s / l) });
        }
        for (int j = 0; j < seg; j++) { int a = j * 2; idx.AddRange(new[] { a, a + 1, a + 2, a + 2, a + 1, a + 3 }); }
        void Cap(double y, double rad, int ny)
        {
            if (rad <= 0) return;
            int c0 = pos.Count / 3;
            pos.AddRange(new[] { 0f, (float)y, 0f }); nrm.AddRange(new[] { 0f, ny, 0f });
            for (int j = 0; j <= seg; j++) { double ph = (double)j / seg * Math.PI * 2; pos.AddRange(new[] { (float)(Math.Cos(ph) * rad), (float)y, (float)(Math.Sin(ph) * rad) }); nrm.AddRange(new[] { 0f, ny, 0f }); }
            for (int j = 0; j < seg; j++) idx.AddRange(new[] { c0, c0 + 1 + (ny > 0 ? j + 1 : j), c0 + 1 + (ny > 0 ? j : j + 1) });
        }
        Cap(0, r, -1); Cap(h, rTop, 1);
        for (int i = 1; i < pos.Count; i += 3) pos[i] -= (float)(h / 2);   // 가운데 정렬
        return Make(pos, nrm, idx);
    }

    public static Mesh PlaneMesh(double w = 4, double d = 4)
    {
        float x = (float)(w / 2), z = (float)(d / 2);
        return new Mesh { Pos = new[] { -x, 0, -z, x, 0, -z, x, 0, z, -x, 0, z }, Nrm = new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0 }, Idx = new[] { 0, 2, 1, 0, 3, 2 } };
    }

    /// <summary>메시에 행렬을 구워 넣은 사본</summary>
    public static Mesh Bake(Mesh m, double[] mat)
    {
        var pos = new float[m.Pos.Length]; var nrm = new float[m.Nrm.Length]; var N = M3.Normal3(mat);
        for (int i = 0; i < m.Pos.Length; i += 3)
        {
            var p = M3.Point(mat, m.Pos[i], m.Pos[i + 1], m.Pos[i + 2]);
            pos[i] = (float)p[0]; pos[i + 1] = (float)p[1]; pos[i + 2] = (float)p[2];
            double x = m.Nrm[i], y = m.Nrm[i + 1], z = m.Nrm[i + 2];
            double nx = N[0] * x + N[3] * y + N[6] * z, ny = N[1] * x + N[4] * y + N[7] * z, nz = N[2] * x + N[5] * y + N[8] * z;
            double l = M3.Hyp(nx, ny, nz); if (l == 0) l = 1;
            nrm[i] = (float)(nx / l); nrm[i + 1] = (float)(ny / l); nrm[i + 2] = (float)(nz / l);
        }
        return new Mesh { Pos = pos, Nrm = nrm, Idx = m.Idx, Uv = m.Uv };
    }

    static Mesh Merge(IEnumerable<Mesh> parts)
    {
        int n = 0; var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<int>();
        foreach (var p in parts) { pos.AddRange(p.Pos); nrm.AddRange(p.Nrm); foreach (var i in p.Idx) idx.Add(i + n); n += p.Pos.Length / 3; }
        return Make(pos, nrm, idx);
    }
    static Mesh At(Mesh m, double x, double y, double z) => Bake(m, M3.Translate(x, y, z));

    static Mesh StairsMesh(int steps = 8) => Merge(Enumerable.Range(0, steps).Select(i => At(BoxMesh(1.4, 0.18 * (i + 1), 0.3), 0, 0.09 * (i + 1), -i * 0.3)));
    static Mesh TableMesh()
    {
        var parts = new List<Mesh> { At(BoxMesh(1.2, 0.05, 0.7), 0, 0.72, 0) };
        foreach (var (x, z) in new[] { (-0.55, -0.3), (0.55, -0.3), (-0.55, 0.3), (0.55, 0.3) }) parts.Add(At(BoxMesh(0.05, 0.7, 0.05), x, 0.35, z));
        return Merge(parts);
    }
    static Mesh ChairMesh()
    {
        var parts = new List<Mesh> { At(BoxMesh(0.45, 0.05, 0.45), 0, 0.45, 0), At(BoxMesh(0.45, 0.5, 0.04), 0, 0.72, -0.2) };
        foreach (var (x, z) in new[] { (-0.2, -0.2), (0.2, -0.2), (-0.2, 0.2), (0.2, 0.2) }) parts.Add(At(BoxMesh(0.04, 0.45, 0.04), x, 0.225, z));
        return Merge(parts);
    }

    public sealed record Prim(string Name, Func<Mesh> MakeMesh, double Y);
    public static readonly (string id, Prim p)[] Primitives =
    {
        ("box", new("상자", () => BoxMesh(1, 1, 1), 0.5)),
        ("sphere", new("구", () => SphereMesh(0.5), 0.5)),
        ("cylinder", new("원기둥", () => CylinderMesh(0.4, 1), 0.5)),
        ("cone", new("원뿔", () => CylinderMesh(0.5, 1, 28, 0.001), 0.5)),
        ("plane", new("바닥판", () => PlaneMesh(6, 6), 0)),
        ("wall", new("벽", () => BoxMesh(6, 3, 0.1), 1.5)),
        ("stairs", new("계단", () => StairsMesh(), 0)),
        ("table", new("책상", () => TableMesh(), 0)),
        ("chair", new("의자", () => ChairMesh(), 0)),
    };
    public static Prim PrimById(string id) => Primitives.FirstOrDefault(x => x.id == id).p;

    // ---------------------------------------------------------------- 데생 인형
    // 관절: [이름, 부모, 오프셋, 모양]. 모양: cap(a→b 캡슐, r, 가로/세로 늘림) / ell(타원구 중심, 반지름)
    public sealed record Shape(double[] CapA = null, double[] CapB = null, double R = 0, double? R2 = null, double Sx = 1, double Sz = 1, double[] Ell = null, double[] Rad = null);
    public sealed record JointDef(string Name, string Parent, double[] Off, Shape Shape);

    static Shape Cap(double[] a, double[] b, double r, double? r2 = null, double sx = 1, double sz = 1) => new(a, b, r, r2, sx, sz);
    static Shape Ell(double[] c, double[] rad) => new(Ell: c, Rad: rad);
    static double[] V(double x, double y, double z) => new[] { x, y, z };

    public static readonly List<JointDef> Body = BuildBody();
    static List<JointDef> BuildBody()
    {
        var b = new List<JointDef>
        {
            new("hips", null, V(0, 0.98, 0), Cap(V(0, 0.03, 0), V(0, -0.05, 0), 0.125, 0.14, 1.25, 0.85)),
            new("spine", "hips", V(0, 0.06, 0), Cap(V(0, 0.01, 0), V(0, 0.17, 0), 0.118, 0.11, 1.15, 0.8)),
            new("chest", "spine", V(0, 0.2, 0), Cap(V(0, 0.02, 0), V(0, 0.15, 0), 0.125, 0.15, 1.3, 0.82)),
            new("neck", "chest", V(0, 0.21, 0), Cap(V(0, 0, 0), V(0, 0.08, 0), 0.045)),
            new("head", "neck", V(0, 0.09, 0), Ell(V(0, 0.11, 0.012), V(0.092, 0.118, 0.105))),
        };
        foreach (var (s, k) in new[] { ("L", 1.0), ("R", -1.0) })
        {
            b.Add(new("shoulder" + s, "chest", V(0.165 * k, 0.15, 0), Ell(V(0, 0, 0), V(0.06, 0.055, 0.06))));
            b.Add(new("upperArm" + s, "shoulder" + s, V(0.025 * k, -0.01, 0), Cap(V(0, 0, 0), V(0, -0.27, 0), 0.052, 0.039)));
            b.Add(new("foreArm" + s, "upperArm" + s, V(0, -0.29, 0), Cap(V(0, 0, 0), V(0, -0.24, 0), 0.042, 0.028)));
            b.Add(new("hand" + s, "foreArm" + s, V(0, -0.26, 0), Ell(V(0, -0.07, 0), V(0.042, 0.08, 0.022))));
            b.Add(new("thigh" + s, "hips", V(0.09 * k, -0.06, 0), Cap(V(0, 0, 0), V(0, -0.41, 0), 0.085, 0.052)));
            b.Add(new("shin" + s, "thigh" + s, V(0, -0.43, 0), Cap(V(0, 0, 0), V(0, -0.39, 0), 0.058, 0.034)));
            b.Add(new("foot" + s, "shin" + s, V(0, -0.42, 0), Ell(V(0, -0.03, 0.055), V(0.045, 0.035, 0.11))));
        }
        return b;
    }

    public static readonly (string id, string name)[] JointNames =
    {
        ("hips", "골반"), ("spine", "허리"), ("chest", "가슴"), ("neck", "목"), ("head", "머리"),
        ("shoulderL", "왼 어깨"), ("upperArmL", "왼 위팔"), ("foreArmL", "왼 아래팔"), ("handL", "왼손"), ("thighL", "왼 허벅지"), ("shinL", "왼 정강이"), ("footL", "왼발"),
        ("shoulderR", "오른 어깨"), ("upperArmR", "오른 위팔"), ("foreArmR", "오른 아래팔"), ("handR", "오른손"), ("thighR", "오른 허벅지"), ("shinR", "오른 정강이"), ("footR", "오른발"),
    };

    static Mesh PartMesh(Shape s)
    {
        if (s.CapA != null)
        {
            var a = s.CapA; var b = s.CapB;
            double len = M3.Hyp(b[0] - a[0], b[1] - a[1], b[2] - a[2]);
            var m = CapsuleMesh(s.R, len, 22, 10, s.R2 ?? s.R);
            // 캡슐은 -Y 방향 → a→b 방향으로 회전
            double[] dir = { (b[0] - a[0]) / len, (b[1] - a[1]) / len, (b[2] - a[2]) / len };
            var rot = M3.Ident();
            if (dir[1] > 0.999) rot = M3.RotX(Math.PI);
            else if (dir[1] > -0.999) { double ang = Math.Acos(-dir[1]); rot = M3.Mul(M3.RotY(Math.Atan2(dir[0], dir[2])), M3.RotX(-ang)); }
            return Bake(m, M3.Mul(M3.Translate(a), M3.Mul(rot, M3.Scale(s.Sx, 1, s.Sz))));
        }
        return Bake(SphereMesh(1), M3.Mul(M3.Translate(s.Ell), M3.Scale(s.Rad[0], s.Rad[1], s.Rad[2])));
    }

    static Dictionary<string, Mesh> bodyMeshes;
    public static Dictionary<string, Mesh> MannequinParts() => bodyMeshes ??= Body.ToDictionary(j => j.Name, j => PartMesh(j.Shape));

    /// <summary>체형: 1 = 기본 (약 7.5등신)</summary>
    public static readonly string[] BodyKeys = { "head", "torso", "shoulders", "hips", "arms", "legs", "girth" };
    public static readonly Dictionary<string, string> BodyNames = new() { ["head"] = "머리 크기", ["torso"] = "몸통 길이", ["shoulders"] = "어깨 폭", ["hips"] = "골반 폭", ["arms"] = "팔 길이", ["legs"] = "다리 길이", ["girth"] = "두께 (살집)" };
    public static readonly (string name, Dictionary<string, double> v)[] BodyPresets =
    {
        ("기본", new()),
        ("8등신 모델", new() { ["head"] = 0.92, ["legs"] = 1.12, ["arms"] = 1.05, ["girth"] = 0.9 }),
        ("만화 6등신", new() { ["head"] = 1.3, ["torso"] = 0.92, ["legs"] = 0.85, ["arms"] = 0.9 }),
        ("어린이", new() { ["head"] = 1.45, ["torso"] = 0.8, ["shoulders"] = 0.8, ["hips"] = 0.85, ["arms"] = 0.75, ["legs"] = 0.7, ["girth"] = 0.9 }),
        ("SD 3등신", new() { ["head"] = 2.1, ["torso"] = 0.7, ["shoulders"] = 0.85, ["arms"] = 0.55, ["legs"] = 0.5, ["girth"] = 1.1 }),
        ("근육질", new() { ["shoulders"] = 1.25, ["torso"] = 1.03, ["girth"] = 1.3, ["arms"] = 1.02 }),
        ("여성형", new() { ["shoulders"] = 0.88, ["hips"] = 1.15, ["girth"] = 0.88, ["head"] = 1.03 }),
        ("통통", new() { ["girth"] = 1.45, ["hips"] = 1.15, ["shoulders"] = 1.05 }),
    };

    /// <summary>관절 회전(도) + 체형 → 부위별 월드 행렬 (월드 = 부위 원점, Scale 은 out 에만)</summary>
    public static Dictionary<string, double[]> MannequinMatrices(Dictionary<string, double[]> pose, Dictionary<string, double> body, Dictionary<string, double[]> worldOut = null)
    {
        double B(string k) => body != null && body.TryGetValue(k, out var v) ? v : 1;
        double g = B("girth");
        var outM = new Dictionary<string, double[]>(); var world = worldOut ?? new Dictionary<string, double[]>();
        foreach (var j in Body)
        {
            string name = j.Name; var off = (double[])j.Off.Clone();
            double[] ps = { g, 1, g };
            bool L(string k) => name.StartsWith(k);
            if (name == "hips") { off[1] = 0.13 + 0.85 * B("legs"); ps = new[] { g * B("hips"), 1, g }; }
            else if (name == "spine" || name == "neck") { off[1] *= B("torso"); ps = new[] { g, B("torso"), g }; }
            else if (name == "chest") { off[1] *= B("torso"); ps = new[] { g * B("shoulders"), B("torso"), g }; }
            else if (name == "head") ps = new[] { B("head"), B("head"), B("head") };
            else if (L("shoulder")) { off[0] *= B("shoulders"); off[1] *= B("torso"); ps = new[] { g, g, g }; }
            else if (L("upperArm")) ps = new[] { g, B("arms"), g };
            else if (L("foreArm") || L("hand")) { off[1] *= B("arms"); ps = L("hand") ? new[] { 1, Math.Sqrt(B("arms")), 1 } : new[] { g, B("arms"), g }; }
            else if (L("thigh")) { off[0] *= B("hips"); ps = new[] { g, B("legs"), g }; }
            else if (L("shin")) { off[1] *= B("legs"); ps = new[] { g, B("legs"), g }; }
            else if (L("foot")) { off[1] *= B("legs"); ps = new double[] { 1, 1, 1 }; }
            var local = M3.Mul(M3.Translate(off), M3.Euler(pose != null && pose.TryGetValue(name, out var r) ? r : new double[3]));
            world[name] = j.Parent != null ? M3.Mul(world[j.Parent], local) : local;
            outM[name] = M3.Mul(world[name], M3.Scale(ps[0], ps[1], ps[2]));
        }
        return outM;
    }

    // ---------------------------------------------------------------- 손 (손가락 마디까지)
    // 손바닥이 +Z(정면 카메라 쪽), 손가락은 아래(-Y). side: 1 = 오른손, -1 = 왼손
    static List<JointDef> HandBody(int k)
    {
        var F = new (string name, double x, double l1, double l2, double l3)[] { ("index", 0.028, 0.04, 0.025, 0.021), ("middle", 0.009, 0.045, 0.028, 0.022), ("ring", -0.01, 0.042, 0.026, 0.021), ("pinky", -0.028, 0.034, 0.021, 0.019) };
        var arr = new List<JointDef>
        {
            new("wrist", null, V(0, 0, 0), Ell(V(0, -0.012, 0), V(0.032, 0.022, 0.02))),
            new("palm", "wrist", V(0, -0.02, 0), Ell(V(0, -0.042, 0), V(0.047, 0.05, 0.017))),
        };
        foreach (var (name, x, l1, l2, l3) in F)
        {
            double r = name == "pinky" ? 0.0075 : 0.0088;
            arr.Add(new(name + "1", "palm", V(x * k, -0.088, 0), Cap(V(0, 0, 0), V(0, -l1, 0), r)));
            arr.Add(new(name + "2", name + "1", V(0, -l1, 0), Cap(V(0, 0, 0), V(0, -l2, 0), r * 0.92)));
            arr.Add(new(name + "3", name + "2", V(0, -l2, 0), Cap(V(0, 0, 0), V(0, -l3, 0), r * 0.85)));
        }
        arr.Add(new("thumb1", "palm", V(0.036 * k, -0.018, 0.01), Cap(V(0, 0, 0), V(0.012 * k, -0.03, 0.006), 0.012)));
        arr.Add(new("thumb2", "thumb1", V(0.012 * k, -0.03, 0.006), Cap(V(0, 0, 0), V(0.006 * k, -0.028, 0.004), 0.0095)));
        arr.Add(new("thumb3", "thumb2", V(0.006 * k, -0.028, 0.004), Cap(V(0, 0, 0), V(0.003 * k, -0.024, 0.002), 0.0085)));
        return arr;
    }
    static readonly Dictionary<int, Dictionary<string, Mesh>> handCache = new();
    public static Dictionary<string, Mesh> HandParts(int side = 1)
    {
        lock (handCache)
        {
            if (!handCache.TryGetValue(side, out var p)) handCache[side] = p = HandBody(side).ToDictionary(j => j.Name, j => PartMesh(j.Shape));
            return p;
        }
    }
    /// <summary>손 관절 회전(도) → 부위별 행렬. 왼손은 오른손 포즈를 거울로</summary>
    public static Dictionary<string, double[]> HandMatrices(Dictionary<string, double[]> pose, int side = 1)
    {
        var outM = new Dictionary<string, double[]>(); var world = new Dictionary<string, double[]>();
        foreach (var j in HandBody(side))
        {
            var r = pose != null && pose.TryGetValue(j.Name, out var rr) ? rr : new double[3];
            var local = M3.Mul(M3.Translate(j.Off), M3.Euler(side < 0 ? new[] { r[0], -r[1], -r[2] } : r));
            world[j.Name] = j.Parent != null ? M3.Mul(world[j.Parent], local) : local;
            outM[j.Name] = world[j.Name];
        }
        return outM;
    }
    public static readonly (string id, string name)[] HandJointNames =
    {
        ("wrist", "손목"), ("palm", "손바닥"), ("thumb1", "엄지 뿌리"), ("thumb2", "엄지 가운데"), ("thumb3", "엄지 끝"),
        ("index1", "검지 뿌리"), ("index2", "검지 가운데"), ("index3", "검지 끝"), ("middle1", "중지 뿌리"), ("middle2", "중지 가운데"), ("middle3", "중지 끝"),
        ("ring1", "약지 뿌리"), ("ring2", "약지 가운데"), ("ring3", "약지 끝"), ("pinky1", "새끼 뿌리"), ("pinky2", "새끼 가운데"), ("pinky3", "새끼 끝"),
    };

    // ---------------------------------------------------------------- 포즈 모음
    public sealed class Pose : Dictionary<string, double[]>
    {
        public Pose() { }
        public Pose(IDictionary<string, double[]> d) { foreach (var (k, v) in d) this[k] = (double[])v.Clone(); }
        public Pose Clone() => new(this);
        public Pose With(params (string k, double x, double y, double z)[] items) { foreach (var (k, x, y, z) in items) this[k] = new[] { x, y, z }; return this; }
        public Pose Add(Pose o) { foreach (var (k, v) in o) this[k] = (double[])v.Clone(); return this; }
    }
    static Pose P(params (string k, double x, double y, double z)[] items) => new Pose().With(items);
    static Pose Curl(string[] names, double a, double b, double c) { var p = new Pose(); foreach (var n in names) p.With((n + "1", a, 0, 0), (n + "2", b, 0, 0), (n + "3", c, 0, 0)); return p; }
    static readonly string[] FOUR = { "index", "middle", "ring", "pinky" };
    static Pose ThumbIn() => P(("thumb1", -25, 0, -35), ("thumb2", -20, 0, -30), ("thumb3", -30, 0, 0));
    public static readonly (string name, Pose pose)[] HandPoses =
    {
        ("펼침", new Pose()),
        ("편하게", Curl(FOUR, -18, -20, -12).With(("thumb1", -10, 0, -10))),
        ("주먹", Curl(FOUR, -85, -95, -60).Add(ThumbIn())),
        ("가리키기", Curl(new[] { "middle", "ring", "pinky" }, -85, -95, -60).Add(ThumbIn())),
        ("브이", Curl(new[] { "ring", "pinky" }, -85, -95, -60).With(("index1", 0, 0, 9), ("middle1", 0, 0, -9)).Add(ThumbIn())),
        ("엄지척", Curl(FOUR, -85, -95, -60).With(("thumb1", 0, 0, 150), ("thumb2", 0, 0, 10))),
        ("잡기 (C자)", Curl(FOUR, -35, -35, -25).With(("thumb1", -25, 0, -15), ("thumb2", -15, 0, -10))),
        ("OK", Curl(new[] { "middle", "ring", "pinky" }, -8, -8, -5).With(("index1", -40, 0, 0), ("index2", -60, 0, 0), ("index3", -40, 0, 0), ("pinky1", -12, 0, 8), ("thumb1", -25, 0, -30), ("thumb2", -25, 0, -20))),
        ("손가락 벌리기", P(("index1", 0, 0, 12), ("middle1", 0, 0, 2), ("ring1", 0, 0, -8), ("pinky1", 0, 0, -18), ("thumb1", 0, 0, 25))),
    };

    public static readonly (string name, Pose pose)[] Poses =
    {
        ("차렷", new Pose()),
        ("A 포즈", P(("upperArmL", 0, 0, 35), ("upperArmR", 0, 0, -35))),
        ("T 포즈", P(("upperArmL", 0, 0, 88), ("upperArmR", 0, 0, -88))),
        ("걷기", P(("upperArmL", 25, 0, 6), ("upperArmR", -25, 0, -6), ("foreArmL", -20, 0, 0), ("foreArmR", -15, 0, 0), ("thighL", -25, 0, 0), ("shinL", 12, 0, 0), ("thighR", 20, 0, 0), ("shinR", 25, 0, 0), ("footR", -10, 0, 0), ("spine", 3, 0, 0))),
        ("달리기", P(("spine", 12, 0, 0), ("chest", 6, 0, 0), ("upperArmL", 55, 0, 10), ("foreArmL", -85, 0, 0), ("upperArmR", -50, 0, -10), ("foreArmR", -80, 0, 0), ("thighL", -65, 0, 0), ("shinL", 75, 0, 0), ("thighR", 30, 0, 0), ("shinR", 70, 0, 0), ("head", -8, 0, 0))),
        ("의자에 앉기", P(("hips", 0, 0, 0), ("thighL", -88, 0, 4), ("thighR", -88, 0, -4), ("shinL", 88, 0, 0), ("shinR", 88, 0, 0), ("upperArmL", -15, 0, 8), ("upperArmR", -15, 0, -8), ("foreArmL", -50, 0, 0), ("foreArmR", -50, 0, 0))),
        ("손 들기", P(("upperArmR", 0, 0, -160), ("foreArmR", 0, 0, -10), ("head", -5, -10, 0))),
        ("팔짱", P(("upperArmL", -20, 0, 15), ("upperArmR", -20, 0, -15), ("foreArmL", -100, 40, 0), ("foreArmR", -100, -40, 0))),
        ("생각하기", P(("upperArmR", -35, 0, -5), ("foreArmR", -130, 0, 0), ("handR", 0, 0, 10), ("head", 8, 0, 8), ("upperArmL", -25, 0, 20), ("foreArmL", -95, -60, 0))),
        ("점프", P(("spine", -5, 0, 0), ("upperArmL", 0, 0, 150), ("upperArmR", 0, 0, -150), ("thighL", -40, 0, 5), ("shinL", 80, 0, 0), ("thighR", 10, 0, -5), ("shinR", 60, 0, 0))),
        ("주먹 내지르기", P(("chest", 0, -25, 0), ("upperArmR", -85, 15, 0), ("foreArmR", -5, 0, 0), ("upperArmL", -60, 0, 30), ("foreArmL", -110, 0, 0), ("thighL", -30, 0, 0), ("shinL", 20, 0, 0), ("thighR", 15, 0, 0))),
    };

    /// <summary>좌우 뒤집기 (웹 판 mirrorPose)</summary>
    public static Pose MirrorPose(Dictionary<string, double[]> p)
    {
        var o = new Pose();
        foreach (var (k, v) in p)
        {
            string m = k.EndsWith("L") ? k[..^1] + "R" : k.EndsWith("R") ? k[..^1] + "L" : k;
            o[m] = new[] { v[0], -v[1], -v[2] };
        }
        return o;
    }

    // ---------------------------------------------------------------- 불러오기
    static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    public static List<Part> ParseOBJ(string text)
    {
        var v = new List<double[]>(); var vn = new List<double[]>(); var vt = new List<double[]>();
        var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
        var map = new Dictionary<string, int>();
        bool hasN = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            double G(int i) => i < p.Length ? Num(p[i]) : double.NaN;
            if (p[0] == "v") v.Add(new[] { G(1), G(2), G(3) });
            else if (p[0] == "vn") vn.Add(new[] { G(1), G(2), G(3) });
            else if (p[0] == "vt") vt.Add(new[] { G(1), G(2) });
            else if (p[0] == "f")
            {
                var ids = new List<int>();
                foreach (var tok in p.Skip(1))
                {
                    if (map.TryGetValue(tok, out int got)) { ids.Add(got); continue; }
                    var abc = tok.Split('/');
                    int ai = (int)Num(abc[0]); int vi = ai < 0 ? v.Count + ai : ai - 1;
                    var P_ = vi >= 0 && vi < v.Count ? v[vi] : new double[3];
                    pos.AddRange(P_.Select(x => (float)x));
                    if (abc.Length > 2 && abc[2] != "") { int c = (int)Num(abc[2]); int ni = c < 0 ? vn.Count + c : c - 1; var n = ni >= 0 && ni < vn.Count ? vn[ni] : new double[] { 0, 1, 0 }; nrm.AddRange(n.Select(x => (float)x)); hasN = true; }
                    else nrm.AddRange(new[] { 0f, 0f, 0f });
                    if (abc.Length > 1 && abc[1] != "") { int b = (int)Num(abc[1]); int ti = b < 0 ? vt.Count + b : b - 1; var t = ti >= 0 && ti < vt.Count ? vt[ti] : new double[2]; uv.Add((float)t[0]); uv.Add((float)t[1]); }
                    else { uv.Add(0); uv.Add(0); }
                    int id = pos.Count / 3 - 1;
                    map[tok] = id; ids.Add(id);
                }
                for (int i = 1; i < ids.Count - 1; i++) idx.AddRange(new[] { ids[0], ids[i], ids[i + 1] });
            }
        }
        var m = new Mesh { Pos = pos.ToArray(), Idx = idx.ToArray(), Uv = uv.ToArray() };
        m.Nrm = hasN ? nrm.ToArray() : ComputeNormals(m.Pos, m.Idx);
        return new List<Part> { new() { Mesh = m, Matrix = M3.Ident(), Color = new[] { 0.85, 0.85, 0.85 } } };
    }

    /// <summary>GLB 또는 GLTF(JSON, 데이터 포함) → 조각 목록</summary>
    public static List<Part> ParseGLTF(byte[] bytes)
    {
        JsonNode json = null; byte[] bin = null;
        if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == 0x46546c67)
        {
            int o = 12;
            while (o + 8 <= bytes.Length)
            {
                int len = (int)BitConverter.ToUInt32(bytes, o); uint type = BitConverter.ToUInt32(bytes, o + 4);
                var chunk = bytes.AsSpan(o + 8, Math.Min(len, bytes.Length - o - 8)).ToArray();
                if (type == 0x4e4f534a) json = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(chunk));
                else if (type == 0x004e4942) bin = chunk;
                o += 8 + len;
            }
        }
        else json = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        if (json == null) throw new InvalidDataException("GLTF 내용을 찾지 못했습니다");
        var buffers = (json["buffers"]?.AsArray() ?? new JsonArray()).Select(b =>
        {
            string uri = (string)b?["uri"];
            if (uri == null) return bin;
            if (uri.StartsWith("data:")) return Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
            throw new InvalidDataException("외부 파일을 참조하는 .gltf 는 지원하지 않습니다. .glb 로 내보내 주세요");
        }).ToList();
        (byte[] buf, int off, int len, int stride) View(int i)
        {
            var bv = json["bufferViews"][i];
            return (buffers[(int)bv["buffer"]], (int?)bv["byteOffset"] ?? 0, (int)bv["byteLength"], (int?)bv["byteStride"] ?? 0);
        }
        var COMP = new Dictionary<string, int> { ["SCALAR"] = 1, ["VEC2"] = 2, ["VEC3"] = 3, ["VEC4"] = 4, ["MAT4"] = 16 };
        float[] Accessor(int i)
        {
            var a = json["accessors"][i]; int n = COMP[(string)a["type"]], cnt = (int)a["count"];
            var outA = new float[cnt * n];
            if (a["bufferView"] == null) return outA;
            var bv = View((int)a["bufferView"]);
            int baseOff = bv.off + ((int?)a["byteOffset"] ?? 0);
            int ct = (int)a["componentType"]; bool norm = (bool?)a["normalized"] ?? false;
            int size = ct switch { 5126 => 4, 5123 => 2, 5125 => 4, 5121 => 1, 5122 => 2, _ => 1 };
            int stride = bv.stride != 0 ? bv.stride : size * n;
            var B = bv.buf;
            for (int k = 0; k < cnt; k++)
                for (int c = 0; c < n; c++)
                {
                    int p = baseOff + k * stride + c * size;
                    if (p + size > B.Length) continue;
                    float v = ct switch
                    {
                        5126 => BitConverter.ToSingle(B, p),
                        5123 => norm ? BitConverter.ToUInt16(B, p) / 65535f : BitConverter.ToUInt16(B, p),
                        5125 => BitConverter.ToUInt32(B, p),
                        5121 => norm ? B[p] / 255f : B[p],
                        5122 => norm ? Math.Max(BitConverter.ToInt16(B, p) / 32767f, -1) : BitConverter.ToInt16(B, p),
                        _ => norm ? Math.Max((sbyte)B[p] / 127f, -1) : (sbyte)B[p],
                    };
                    outA[k * n + c] = v;
                }
            return outA;
        }
        var images = (json["images"]?.AsArray() ?? new JsonArray()).Select(im =>
        {
            try
            {
                byte[] data = null;
                if (im?["bufferView"] != null) { var bv = View((int)im["bufferView"]); data = bv.buf.AsSpan(bv.off, bv.len).ToArray(); }
                else if (((string)im?["uri"])?.StartsWith("data:") == true) { string u = (string)im["uri"]; data = Convert.FromBase64String(u[(u.IndexOf(',') + 1)..]); }
                return data == null ? null : TexFromBytes(data);
            }
            catch { return null; }
        }).ToList();
        var outP = new List<Part>();
        var nodes = json["nodes"]?.AsArray() ?? new JsonArray();
        double[] Arr(JsonNode n, params double[] def) => n is JsonArray a ? a.Select(x => (double)x).ToArray() : def;
        void Walk(int ni, double[] parent, int depth)
        {
            if (depth > 64 || ni < 0 || ni >= nodes.Count) return;
            var n = nodes[ni];
            double[] local;
            if (n["matrix"] is JsonArray mat) local = mat.Select(x => (double)x).ToArray();
            else
            {
                var t = Arr(n["translation"], 0, 0, 0); var r = Arr(n["rotation"], 0, 0, 0, 1); var s = Arr(n["scale"], 1, 1, 1);
                local = M3.Mul(M3.Translate(t), M3.Mul(M3.Quat(r), M3.Scale(s[0], s[1], s[2])));
            }
            var world = M3.Mul(parent, local);
            if (n["mesh"] != null)
            {
                foreach (var pr in json["meshes"][(int)n["mesh"]]["primitives"].AsArray())
                {
                    if (pr["mode"] != null && (int)pr["mode"] != 4) continue;
                    var attr = pr["attributes"];
                    var pos = Accessor((int)attr["POSITION"]);
                    int[] idx = pr["indices"] != null ? Accessor((int)pr["indices"]).Select(x => (int)x).ToArray() : Enumerable.Range(0, pos.Length / 3).ToArray();
                    var nrm = attr["NORMAL"] != null ? Accessor((int)attr["NORMAL"]) : ComputeNormals(pos, idx);
                    var uv = attr["TEXCOORD_0"] != null ? Accessor((int)attr["TEXCOORD_0"]) : null;
                    var matl = pr["material"] != null ? json["materials"]?[(int)pr["material"]] : null;
                    var pbr = matl?["pbrMetallicRoughness"];
                    var cf = Arr(pbr?["baseColorFactor"], 0.85, 0.85, 0.85, 1);
                    Tex image = null;
                    if (pbr?["baseColorTexture"]?["index"] is JsonNode ti)
                    {
                        var src = json["textures"]?[(int)ti]?["source"];
                        if (src != null && (int)src < images.Count) image = images[(int)src];
                    }
                    outP.Add(new Part { Mesh = new Mesh { Pos = pos, Nrm = nrm, Idx = idx, Uv = uv }, Matrix = world, Color = new[] { cf[0], cf[1], cf[2] }, Image = image });
                }
            }
            foreach (var c in n["children"]?.AsArray() ?? new JsonArray()) Walk((int)c, world, depth + 1);
        }
        var scenes = json["scenes"]?.AsArray();
        var scene = scenes != null && scenes.Count > 0 ? scenes[(int?)json["scene"] ?? 0] : null;
        IEnumerable<int> roots = scene != null ? scene["nodes"].AsArray().Select(x => (int)x)
            : Enumerable.Range(0, nodes.Count).Where(i => !nodes.Any(n => n["children"]?.AsArray().Any(c => (int)c == i) == true));
        foreach (var r in roots.ToList()) Walk(r, M3.Ident(), 0);
        if (outP.Count == 0) throw new InvalidDataException("모델에서 삼각형 메시를 찾지 못했습니다");
        return outP;
    }

    public static unsafe Tex TexFromBytes(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var bmp = new Bitmap(ms);
        var t = new Tex { W = bmp.Width, H = bmp.Height, Px = new uint[bmp.Width * bmp.Height] };
        var bd = bmp.LockBits(new Rectangle(0, 0, t.W, t.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < t.H; y++) { uint* row = (uint*)((byte*)bd.Scan0 + y * bd.Stride); for (int x = 0; x < t.W; x++) t.Px[y * t.W + x] = row[x]; } }
        finally { bmp.UnlockBits(bd); }
        return t;
    }

    /// <summary>모델 크기를 사람 키(1.7) 정도로 맞추고 바닥 가운데에 세운다</summary>
    public static List<Part> NormalizeParts(List<Part> parts, double height = 1.7)
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var p in parts)
            for (int i = 0; i < p.Mesh.Pos.Length; i += 3)
            {
                var q = M3.Point(p.Matrix, p.Mesh.Pos[i], p.Mesh.Pos[i + 1], p.Mesh.Pos[i + 2]);
                x0 = Math.Min(x0, q[0]); x1 = Math.Max(x1, q[0]); y0 = Math.Min(y0, q[1]); y1 = Math.Max(y1, q[1]); z0 = Math.Min(z0, q[2]); z1 = Math.Max(z1, q[2]);
            }
        double s = height / Math.Max(1e-6, Math.Max(y1 - y0, Math.Max((x1 - x0) * 0.6, (z1 - z0) * 0.6)));
        var fix = M3.Mul(M3.Scale(s), M3.Translate(-(x0 + x1) / 2, -y0, -(z0 + z1) / 2));
        return parts.Select(p => new Part { Mesh = p.Mesh, Matrix = M3.Mul(fix, p.Matrix), Color = p.Color, Image = p.Image }).ToList();
    }
}
