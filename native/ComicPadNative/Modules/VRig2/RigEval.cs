// 리그 계산: 파라미터 값 → 디포머 모양 → 부위 꼭짓점 위치(작품 좌표)·불투명도·그리기 순서 → 렌더러로 그리기.
// 점 변형 = 자기 키폼(오프셋) → 부모 디포머 → 그 부모 … (각 디포머는 자기 쉼 좌표 공간의 점을 받아 부모 공간의 점으로 보냄)
//   warp: 격자 점 오프셋을 칸 안에서 보간 (부드럽게 = 캣멀-롬, 아니면 쌍선형). 격자 밖 점은 가장자리 오프셋.
//   rot : 중심 둘레로 각도·크기, 중심 이동.
using ComicPadNative.Core;

namespace ComicPadNative.Modules.VRig2;

public sealed class RigRuntime
{
    public Rig2 Rig;
    public readonly Dictionary<string, RigTexture> Tex = new();       // 레이어 id → 텍스처
    public readonly Dictionary<string, double> Values = new();        // 파라미터 값
    public readonly Dictionary<string, RigPhysics.State> Phys = new();
    public int Version;                                               // 구조가 바뀔 때마다 (편집기 캐시용)

    // 컴파일 결과
    RDeformer[] defs = Array.Empty<RDeformer>();
    int[] defParent = Array.Empty<int>();
    int[] partParent = Array.Empty<int>();
    float[][] partUv = Array.Empty<float[]>();
    readonly Dictionary<string, int> defIndex = new(), partIndex = new();
    // 계산 결과
    public sealed class DefState { public double[] Disp; public double A, S = 1, Tx, Ty, Op = 1, Cos = 1, Sin; }
    public sealed class PartState { public double[] Pos = Array.Empty<double>(); public double Op; public double Ord; }
    public DefState[] DS = Array.Empty<DefState>();
    public PartState[] PS = Array.Empty<PartState>();
    public int[] DrawOrder = Array.Empty<int>();
    readonly List<(int i, double w)> tmpW = new();
    readonly KForm tmpForm = new();

    public RigRuntime(Rig2 rig) { Rig = rig; Reset(); Compile(); }

    public double Val(string id) => id != null && Values.TryGetValue(id, out var v) ? v : (Rig.Param(id)?.Def ?? 0);
    public void Reset() { Values.Clear(); foreach (var p in Rig.Params) Values[p.Id] = p.Def; }
    public void Set(string id, double v) { var p = Rig.Param(id); if (p == null) return; Values[id] = Math.Clamp(v, p.Min, p.Max); }

    public int DefIndex(string id) => id != null && defIndex.TryGetValue(id, out var i) ? i : -1;
    public int PartIndex(string id) => id != null && partIndex.TryGetValue(id, out var i) ? i : -1;

    /// <summary>텍스처 넣기 (작품 레이어에서)</summary>
    public void LoadTextures(Document d, bool onlyMissing = false)
    {
        foreach (var p in Rig.Parts)
        {
            if (p.LayerId == null || (onlyMissing && Tex.ContainsKey(p.LayerId))) continue;
            var t = TextureFor(d, p.LayerId);
            if (t != null) Tex[p.LayerId] = t;
        }
    }
    public static RigTexture TextureFor(Document d, string layerId)
    {
        var l = d?.ById(layerId);
        if (l == null) return null;
        if (l.IsFolder) return RigTexture.FromFolder(d, l);
        return l.Px != null ? RigTexture.FromSurface(l.Px, l.Id) : null;
    }

    /// <summary>구조가 바뀐 뒤 (부위·디포머·메쉬·부모)</summary>
    public void Compile()
    {
        Version++;
        defs = Rig.Deformers.ToArray();
        defIndex.Clear(); partIndex.Clear();
        for (int i = 0; i < defs.Length; i++) defIndex[defs[i].Id] = i;
        defParent = new int[defs.Length];
        for (int i = 0; i < defs.Length; i++)
        {
            int p = DefIndex(defs[i].Parent);
            // 고리 막기
            for (int k = p, n = 0; k >= 0 && n < 64; k = DefIndex(defs[k].Parent), n++) if (k == i) { p = -1; break; }
            defParent[i] = p;
        }
        var parts = Rig.Parts;
        partParent = new int[parts.Count];
        partUv = new float[parts.Count][];
        for (int i = 0; i < parts.Count; i++)
        {
            partIndex[parts[i].Id] = i;
            partParent[i] = DefIndex(parts[i].Parent);
            partUv[i] = Uv(parts[i]);
        }
        if (DS.Length != defs.Length) DS = defs.Select(_ => new DefState()).ToArray();
        if (PS.Length != parts.Count) PS = parts.Select(_ => new PartState()).ToArray();
    }

    float[] Uv(RPart p)
    {
        var uv = new float[p.Mesh.V.Length];
        Tex.TryGetValue(p.LayerId ?? "", out var t);
        double ox = t?.X ?? 0, oy = t?.Y ?? 0;
        for (int k = 0; k + 1 < uv.Length; k += 2) { uv[k] = (float)(p.Mesh.V[k] - ox); uv[k + 1] = (float)(p.Mesh.V[k + 1] - oy); }
        return uv;
    }
    public void RefreshUv(int partIdx) => partUv[partIdx] = Uv(Rig.Parts[partIdx]);

    // ------------------------------------------------------------------ 계산
    public void Evaluate()
    {
        Func<string, double> val = Val;
        for (int i = 0; i < defs.Length; i++)
        {
            var d = defs[i]; var s = DS[i];
            var f = d.Grid.Blend(val, d.PointCount, tmpW, tmpForm);
            s.Op = Math.Clamp(f.Op, 0, 1);
            if (d.IsWarp)
            {
                int n = d.Nodes * 2;
                if (s.Disp == null || s.Disp.Length != n) s.Disp = new double[n];
                if (f.D != null) Array.Copy(f.D, s.Disp, Math.Min(n, f.D.Length)); else Array.Clear(s.Disp);
            }
            else
            {
                s.A = f.A; s.S = f.S; s.Tx = f.Tx; s.Ty = f.Ty;
                double r = f.A * Math.PI / 180; s.Cos = Math.Cos(r) * f.S; s.Sin = Math.Sin(r) * f.S;
            }
        }
        var parts = Rig.Parts;
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i]; var s = PS[i];
            int n = p.Mesh.N;
            var f = p.Grid.Blend(val, n, tmpW, tmpForm);
            if (s.Pos.Length != n * 2) s.Pos = new double[n * 2];
            var v = p.Mesh.V; var pos = s.Pos; var dd = f.D;
            int pi = partParent[i];
            for (int k = 0; k < n; k++)
            {
                double x = v[k * 2] + f.Tx, y = v[k * 2 + 1] + f.Ty;
                if (dd != null && k * 2 + 1 < dd.Length) { x += dd[k * 2]; y += dd[k * 2 + 1]; }
                if (pi >= 0) Map(pi, ref x, ref y);
                pos[k * 2] = x; pos[k * 2 + 1] = y;
            }
            double op = Math.Clamp(f.Op, 0, 1);
            for (int k = pi, guard = 0; k >= 0 && guard < 64; k = defParent[k], guard++) op *= DS[k].Op;
            s.Op = p.Hide ? 0 : op;
            s.Ord = Math.Round(p.Order + f.Ord);
        }
        if (DrawOrder.Length != parts.Count) DrawOrder = new int[parts.Count];
        for (int i = 0; i < DrawOrder.Length; i++) DrawOrder[i] = i;
        var ps = PS;
        Array.Sort(DrawOrder, (a, b) => { int c = ps[a].Ord.CompareTo(ps[b].Ord); return c != 0 ? c : a.CompareTo(b); });
    }

    /// <summary>디포머 i 의 쉼 좌표 점 → 맨 위(작품) 좌표</summary>
    public void Map(int i, ref double x, ref double y)
    {
        for (int guard = 0; i >= 0 && guard < 64; i = defParent[i], guard++) Local(i, ref x, ref y);
    }
    public int ParentOf(int defIdx) => defIdx >= 0 && defIdx < defParent.Length ? defParent[defIdx] : -1;
    public int PartParent(int partIdx) => partIdx >= 0 && partIdx < partParent.Length ? partParent[partIdx] : -1;

    void Local(int i, ref double x, ref double y)
    {
        var d = defs[i]; var s = DS[i];
        if (!d.IsWarp)
        {
            double rx = x - d.Px, ry = y - d.Py;
            x = d.Px + s.Tx + s.Cos * rx - s.Sin * ry;
            y = d.Py + s.Ty + s.Sin * rx + s.Cos * ry;
            return;
        }
        var disp = s.Disp;
        if (disp == null) return;
        int C = d.Cols, Rr = d.Rows, stride = C + 1;
        double u = Math.Clamp((x - d.X) / d.W * C, 0, C), v = Math.Clamp((y - d.Y) / d.H * Rr, 0, Rr);
        int ci = Math.Min((int)u, C - 1), ri = Math.Min((int)v, Rr - 1);
        double fu = u - ci, fv = v - ri;
        double dx = 0, dy = 0;
        if (!d.Smooth || (C < 2 && Rr < 2))
        {
            int n00 = ri * stride + ci;
            double w00 = (1 - fu) * (1 - fv), w10 = fu * (1 - fv), w01 = (1 - fu) * fv, w11 = fu * fv;
            dx = disp[n00 * 2] * w00 + disp[(n00 + 1) * 2] * w10 + disp[(n00 + stride) * 2] * w01 + disp[(n00 + stride + 1) * 2] * w11;
            dy = disp[n00 * 2 + 1] * w00 + disp[(n00 + 1) * 2 + 1] * w10 + disp[(n00 + stride) * 2 + 1] * w01 + disp[(n00 + stride + 1) * 2 + 1] * w11;
        }
        else
        {
            Span<double> wu = stackalloc double[4], wv = stackalloc double[4];
            CR(fu, wu); CR(fv, wv);
            for (int b = 0; b < 4; b++)
            {
                int rr = Math.Clamp(ri - 1 + b, 0, Rr);
                for (int a = 0; a < 4; a++)
                {
                    int cc = Math.Clamp(ci - 1 + a, 0, C);
                    double ww = wu[a] * wv[b];
                    int n = (rr * stride + cc) * 2;
                    dx += disp[n] * ww; dy += disp[n + 1] * ww;
                }
            }
        }
        x += dx; y += dy;
    }

    /// <summary>캣멀-롬 무게 (점 i-1, i, i+1, i+2)</summary>
    static void CR(double t, Span<double> w)
    {
        double t2 = t * t, t3 = t2 * t;
        w[0] = -0.5 * t3 + t2 - 0.5 * t;
        w[1] = 1.5 * t3 - 2.5 * t2 + 1;
        w[2] = -1.5 * t3 + 2 * t2 + 0.5 * t;
        w[3] = 0.5 * t3 - 0.5 * t2;
    }

    /// <summary>점 변형의 야코비안 (부모 공간 이동 → 화면 이동). 편집할 때 거꾸로 풀기용</summary>
    public (double a, double b, double c, double d) Jacobian(int defIdx, double x, double y)
    {
        if (defIdx < 0) return (1, 0, 0, 1);
        const double h = 0.5;
        double x0 = x, y0 = y; Map(defIdx, ref x0, ref y0);
        double x1 = x + h, y1 = y; Map(defIdx, ref x1, ref y1);
        double x2 = x, y2 = y + h; Map(defIdx, ref x2, ref y2);
        return ((x1 - x0) / h, (x2 - x0) / h, (y1 - y0) / h, (y2 - y0) / h);
    }
    /// <summary>화면 이동 (sx,sy) → 부모 쉼 공간 이동</summary>
    public (double dx, double dy) Unmap(int defIdx, double x, double y, double sx, double sy)
    {
        var (a, b, c, d) = Jacobian(defIdx, x, y);
        double det = a * d - b * c;
        if (Math.Abs(det) < 1e-6) return (sx, sy);
        return ((d * sx - b * sy) / det, (-c * sx + a * sy) / det);
    }

    /// <summary>워프 격자 점들의 지금 위치 (작품 좌표) — 자기 오프셋까지 넣고 부모로</summary>
    public double[] WarpNodes(int i)
    {
        var d = defs[i]; var s = DS[i];
        var o = new double[d.Nodes * 2];
        for (int k = 0; k < d.Nodes; k++)
        {
            var (x, y) = d.RestNode(k);
            if (s.Disp != null) { x += s.Disp[k * 2]; y += s.Disp[k * 2 + 1]; }
            Map(defParent[i], ref x, ref y);
            o[k * 2] = x; o[k * 2 + 1] = y;
        }
        return o;
    }
    /// <summary>회전 디포머: 중심·손잡이 끝의 지금 위치</summary>
    public (double px, double py, double hx, double hy) RotHandle(int i)
    {
        var d = defs[i]; var s = DS[i];
        double px = d.Px + s.Tx, py = d.Py + s.Ty;
        double r = s.A * Math.PI / 180;
        double hx = px + Math.Sin(r) * d.Len * s.S, hy = py - Math.Cos(r) * d.Len * s.S;
        Map(defParent[i], ref px, ref py); Map(defParent[i], ref hx, ref hy);
        return (px, py, hx, hy);
    }

    // ------------------------------------------------------------------ 그리기
    readonly Dictionary<string, int> maskIds = new();
    float[][] xyBuf = Array.Empty<float[]>();

    /// <summary>리그 그리기: 화면 = 작품 × scale + (ox, oy). clear = 바탕 (미리 곱한 ARGB, 0 = 투명)</summary>
    public void Render(IRigRenderer r, int w, int h, double scale, double ox, double oy, uint clear)
    {
        var parts = Rig.Parts;
        if (xyBuf.Length != parts.Count) xyBuf = new float[parts.Count][];
        for (int i = 0; i < parts.Count; i++)
        {
            var pos = PS[i].Pos;
            if (xyBuf[i] == null || xyBuf[i].Length != pos.Length) xyBuf[i] = new float[pos.Length];
            var xy = xyBuf[i];
            for (int k = 0; k + 1 < pos.Length; k += 2) { xy[k] = (float)(pos[k] * scale + ox); xy[k + 1] = (float)(pos[k + 1] * scale + oy); }
        }
        r.Begin(w, h, clear);
        maskIds.Clear();
        foreach (int i in DrawOrder)
        {
            var p = parts[i]; var s = PS[i];
            if (s.Op <= 0.002 || p.Mesh.T.Length < 3 || !Tex.TryGetValue(p.LayerId ?? "", out var tex)) continue;
            if (partUv[i].Length != p.Mesh.V.Length) RefreshUv(i);
            int mask = -1;
            if (p.Clip.Count > 0)
            {
                string key = string.Join("|", p.Clip);
                if (!maskIds.TryGetValue(key, out mask))
                {
                    var ms = new List<RigMeshDraw>();
                    foreach (var cid in p.Clip)
                    {
                        int ci = PartIndex(cid);
                        if (ci < 0 || ci == i) continue;
                        var cp = parts[ci];
                        if (cp.Mesh.T.Length < 3 || !Tex.TryGetValue(cp.LayerId ?? "", out var ct)) continue;
                        if (partUv[ci].Length != cp.Mesh.V.Length) RefreshUv(ci);
                        ms.Add(new RigMeshDraw(xyBuf[ci], partUv[ci], cp.Mesh.T, ct, (float)PS[ci].Op));
                    }
                    mask = r.AddMask(ms);
                    maskIds[key] = mask;
                }
            }
            r.DrawMesh(xyBuf[i], partUv[i], p.Mesh.T, tex, (float)s.Op, mask, p.ClipInvert);
        }
        r.End();
    }

    /// <summary>새 그림으로 그리기 (32bpp PArgb)</summary>
    public Bitmap RenderBitmap(IRigRenderer r, int w, int h, double scale, double ox, double oy, uint clear)
    {
        Render(r, w, h, scale, ox, oy, clear);
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        r.CopyTo(bmp);
        return bmp;
    }
}
