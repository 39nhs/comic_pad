// 원근·격자 왜곡 (여러 면) / 퍼펫 왜곡 (핀을 끌어 휘기) — 웹 tools/warp.js.
// 삼각형 그물: 대상 삼각형마다 픽셀 가운데를 원본으로 거꾸로 옮겨 보간 (이음매 없음). 끄는 동안 미리보기 면, 적용 때 고품질로 한 번.
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

public static class MeshWarp
{
    /// <summary>삼각형 (원본 s → 대상 d) 들로 다시 그린 결과. src = 원본 영역 sb 의 픽셀</summary>
    public static (Rect r, uint[] px) Render(uint[] src, Rect sb, List<(P2[] s, P2[] d)> tris, int W, int H, int mode)
    {
        var r = Geo.BoundsRect(tris.SelectMany(t => t.d), 1).Clip(W, H);
        if (r.IsEmpty) return (r, Array.Empty<uint>());
        var o = new uint[r.W * r.H];
        var done = new bool[r.W * r.H];
        foreach (var (s, d) in tris)
        {
            // 대상 → 원본 아핀
            double den = (d[1].X - d[0].X) * (d[2].Y - d[0].Y) - (d[2].X - d[0].X) * (d[1].Y - d[0].Y);
            if (Math.Abs(den) < 1e-9) continue;
            var tb = Geo.BoundsRect(d, 1).Intersect(r);
            if (tb.IsEmpty) continue;
            for (int y = tb.Y; y < tb.B; y++)
                for (int x = tb.X; x < tb.R; x++)
                {
                    int oi = (y - r.Y) * r.W + (x - r.X);
                    if (done[oi]) continue;
                    double px = x + 0.5, py = y + 0.5;
                    // 무게중심 좌표
                    double l1 = ((d[1].Y - d[2].Y) * (px - d[2].X) + (d[2].X - d[1].X) * (py - d[2].Y)) / ((d[1].Y - d[2].Y) * (d[0].X - d[2].X) + (d[2].X - d[1].X) * (d[0].Y - d[2].Y));
                    double l2 = ((d[2].Y - d[0].Y) * (px - d[2].X) + (d[0].X - d[2].X) * (py - d[2].Y)) / ((d[1].Y - d[2].Y) * (d[0].X - d[2].X) + (d[2].X - d[1].X) * (d[0].Y - d[2].Y));
                    double l3 = 1 - l1 - l2;
                    const double eps = -1e-7;
                    if (l1 < eps || l2 < eps || l3 < eps) continue;
                    double sx = l1 * s[0].X + l2 * s[1].X + l3 * s[2].X, sy = l1 * s[0].Y + l2 * s[1].Y + l3 * s[2].Y;
                    o[oi] = Resample.Sample(src, sb.W, sb.H, sx - sb.X - 0.5, sy - sb.Y - 0.5, mode);
                    done[oi] = true;
                }
        }
        return (r, o);
    }

    /// <summary>MLS 강체 변형 (Schaefer 외 2006, 웹 mlsRigid 와 같음)</summary>
    public static P2 MlsRigid(List<(P2 p0, P2 p)> pins, double x, double y)
    {
        if (pins.Count == 0) return new P2(x, y);
        double sw = 0, px = 0, py = 0, qx = 0, qy = 0;
        var w = pins.Select(p => { double d2 = (p.p0.X - x) * (p.p0.X - x) + (p.p0.Y - y) * (p.p0.Y - y); return d2 < 1e-6 ? 1e6 : 1 / d2; }).ToArray();
        for (int i = 0; i < pins.Count; i++) { sw += w[i]; px += w[i] * pins[i].p0.X; py += w[i] * pins[i].p0.Y; qx += w[i] * pins[i].p.X; qy += w[i] * pins[i].p.Y; }
        px /= sw; py /= sw; qx /= sw; qy /= sw;
        double dx = x - px, dy = y - py;
        if (pins.Count == 1) return new P2(dx + qx, dy + qy);
        double fr = 0, fi = 0;
        for (int i = 0; i < pins.Count; i++)
        {
            double ax = pins[i].p0.X - px, ay = pins[i].p0.Y - py, bx = pins[i].p.X - qx, by = pins[i].p.Y - qy;
            double cr = bx * ax + by * ay, ci = by * ax - bx * ay;
            fr += w[i] * (cr * dx - ci * dy); fi += w[i] * (cr * dy + ci * dx);
        }
        double L = Math.Sqrt(fr * fr + fi * fi), dl = Math.Sqrt(dx * dx + dy * dy);
        if (L < 1e-9) return new P2(dx + qx, dy + qy);
        return new P2(fr / L * dl + qx, fi / L * dl + qy);
    }
}

/// <summary>한 레이어를 고치는 동안의 공통 상태 (웹 session)</summary>
public sealed class WarpSession
{
    public Layer L; public bool OnMask; public Surface Orig, Base, Prev; public uint[] Src; public Rect B; Rect last = Rect.Empty;

    public static WarpSession Begin()
    {
        var d = App.Doc; var l = d?.Active;
        if (!App.CheckEditable(l)) return null;
        if (MetaLayers.IsMeta(l) && !l.EditMask) { App.Toast("이 레이어는 해당 도구로 고치세요 (그림으로 바꾼 뒤 왜곡)"); return null; }
        var T = l.Target;
        var b = T.Bounds();
        if (b.IsEmpty) { App.Toast("레이어에 그림이 없습니다"); return null; }
        var s = new WarpSession { L = l, OnMask = l.EditMask && l.Mask != null, Orig = T, Src = T.Read(b), B = b, Base = T.Clone() };
        s.Base.Clear(b);
        s.Prev = s.Base.Clone();
        s.Set(s.Prev);
        return s;
    }
    void Set(Surface v) { if (OnMask) L.Mask = v; else L.Px = v; L.Touch(); }

    public void Show(List<(P2[] s, P2[] d)> tris)
    {
        var d = App.Doc;
        if (!last.IsEmpty) Prev.Write(last, Base.Read(last));
        var (r, px) = MeshWarp.Render(Src, B, tris, d.W, d.H, Resample.Bilinear);
        if (!r.IsEmpty) { var under = Base.Read(r); Resample.OverInto(under, px); Prev.Write(r, under); }
        L.Touch();
        var inv = last.Union(r).Union(B);
        last = r;
        App.Invalidate(inv.Inflate(2));
    }

    public void Finish(bool apply, string label, List<(P2[] s, P2[] d)> tris)
    {
        Set(Orig);
        var d = App.Doc;
        if (apply && tris != null)
        {
            var (r, px) = MeshWarp.Render(Src, B, tris, d.W, d.H, Resample.Bicubic);
            var pe = App.History.BeginPixels(L, label);
            pe.Touch(B.Union(r));
            Orig.Clear(B);
            if (!r.IsEmpty) { var under = Orig.Read(r); Resample.OverInto(under, px); Orig.Write(r, under); }
            pe.Commit(label);
            d.Modified = true;
            App.NotifyLayers();
        }
        App.Invalidate(B.Union(last).Inflate(2));
    }
}

/// <summary>원근·격자 왜곡</summary>
public sealed class MeshWarpTool : ToolBase
{
    public override string Id => "meshwarp";
    public override string Name => "원근·격자 왜곡";
    public override string Icon => "⧉";
    public override Cursor Cursor => Cursors.Default;
    static readonly Dictionary<string, (int c, int r)> GRIDS = new() { ["1x1"] = (1, 1), ["2x1"] = (2, 1), ["1x2"] = (1, 2), ["3x3"] = (3, 3), ["4x4"] = (4, 4) };
    public string Grid = "2x1"; public bool ShowGrid = true;
    WarpSession S; P2[] Pts; int C, R;
    (int i, double x0, double y0, P2 p0, P2[] all)? drag;
    public override bool Busy => drag != null;

    bool Begin()
    {
        S = WarpSession.Begin();
        if (S == null) return false;
        (C, R) = GRIDS[Grid];
        var b = S.B;
        Pts = new P2[(C + 1) * (R + 1)];
        for (int j = 0; j <= R; j++) for (int i = 0; i <= C; i++) Pts[j * (C + 1) + i] = new P2(b.X + b.W * (double)i / C, b.Y + b.H * (double)j / R);
        return true;
    }

    public List<(P2[] s, P2[] d)> Tris()
    {
        var b = S.B; int N = Math.Max(4, (int)Math.Round(24.0 / Math.Max(C, R)));
        var tris = new List<(P2[], P2[])>();
        for (int j = 0; j < R; j++)
            for (int i = 0; i < C; i++)
            {
                P2 K(int jj, int ii) => Pts[jj * (C + 1) + ii];
                var H = new QuadMap(K(j, i), K(j, i + 1), K(j + 1, i + 1), K(j + 1, i));
                double sx0 = b.X + b.W * (double)i / C, sy0 = b.Y + b.H * (double)j / R, cw = (double)b.W / C, ch = (double)b.H / R;
                for (int v = 0; v < N; v++)
                    for (int u = 0; u < N; u++)
                    {
                        P2 Sp(int uu, int vv) => new(sx0 + cw * uu / N, sy0 + ch * vv / N);
                        P2 Dp(int uu, int vv) => H.Map((double)uu / N, (double)vv / N);
                        tris.Add((new[] { Sp(u, v), Sp(u + 1, v), Sp(u + 1, v + 1) }, new[] { Dp(u, v), Dp(u + 1, v), Dp(u + 1, v + 1) }));
                        tris.Add((new[] { Sp(u, v), Sp(u + 1, v + 1), Sp(u, v + 1) }, new[] { Dp(u, v), Dp(u + 1, v + 1), Dp(u, v + 1) }));
                    }
            }
        return tris;
    }

    /// <summary>점 옮기기 (시험·다른 모듈용)</summary>
    public void SetPoint(int i, P2 p) { if (S == null && !Begin()) return; Pts[i] = p; S.Show(Tris()); }
    public bool Start() => S != null || Begin();

    public void Apply() { if (S == null) return; S.Finish(true, "원근·격자 왜곡", Tris()); S = null; Pts = null; }
    public void CancelWarp() { if (S == null) return; S.Finish(false, null, null); S = null; Pts = null; }
    public override void Deactivate() => Apply();
    public override void Activate() { App.DocChanged -= Drop; App.DocChanged += Drop; }
    void Drop() { S = null; Pts = null; drag = null; }

    public override void Down(PointerArgs e)
    {
        if (S == null && !Begin()) return;
        var v = MetaLayers.Canvas?.V; if (v == null) return;
        int best = -1; double bd = 14;
        for (int i = 0; i < Pts.Length; i++) { var s = Gfx.S(v, Pts[i]); double dd = Math.Sqrt((s.X - e.SX) * (s.X - e.SX) + (s.Y - e.SY) * (s.Y - e.SY)); if (dd < bd) { bd = dd; best = i; } }
        if (best < 0) return;
        drag = (best, e.X, e.Y, Pts[best], e.Shift ? Pts.ToArray() : null);
    }
    public override void Move(PointerArgs e)
    {
        if (drag is not { } d) return;
        double dx = e.X - d.x0, dy = e.Y - d.y0;
        if (d.all != null) Pts = d.all.Select(p => new P2(p.X + dx, p.Y + dy)).ToArray();
        else Pts[d.i] = new P2(d.p0.X + dx, d.p0.Y + dy);
        S.Show(Tris());
    }
    public override void Up(PointerArgs e) => drag = null;
    public override void Cancel() => drag = null;
    public override bool HandleKey(Keys k)
    {
        if (S == null) return false;
        if ((k & Keys.KeyCode) == Keys.Enter) { Apply(); return true; }
        if ((k & Keys.KeyCode) == Keys.Escape) { CancelWarp(); return true; }
        return false;
    }

    public override Rectangle OverlayBounds(ViewXf v)
    {
        if (Pts != null) return CUI.Bounds(Pts.Select(p => Gfx.S(v, p)), 10);
        var b = App.Active?.Target?.Bounds() ?? Rect.Empty;
        return b.IsEmpty ? Rectangle.Empty : Rectangle.Inflate(Gfx.ToScreen(v, b.X, b.Y, b.W, b.H), 3, 3);
    }
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (Pts == null)
        {
            var b = App.Active?.Target?.Bounds() ?? Rect.Empty;
            if (b.IsEmpty) return;
            using var dp = CUI.Dash(Color.FromArgb(47, 124, 246), 1, 5, 4);
            g.DrawRectangle(dp, Gfx.ToScreen(v, b.X, b.Y, b.W, b.H));
            return;
        }
        var S2 = Pts.Select(p => Gfx.S(v, p)).ToArray();
        if (ShowGrid)
        {
            using var pen = new Pen(Color.FromArgb(230, 47, 124, 246), 1.2f);
            for (int j = 0; j <= R; j++) g.DrawLines(pen, Enumerable.Range(0, C + 1).Select(i => S2[j * (C + 1) + i]).ToArray());
            for (int i = 0; i <= C; i++) g.DrawLines(pen, Enumerable.Range(0, R + 1).Select(j => S2[j * (C + 1) + i]).ToArray());
        }
        foreach (var s in S2) CUI.Handle(g, s, true, null, 5);
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("원근·격자 왜곡"),
        CUI.Seg(new[] { ("1x1", "한 면"), ("2x1", "두 면 (가로)"), ("1x2", "두 면 (세로)"), ("3x3", "격자 3×3"), ("4x4", "격자 4×4") }, () => Grid, v => { CancelWarp(); Grid = v; }),
        Opt.Check("격자선 보이기", () => ShowGrid, v => { ShowGrid = v; MetaLayers.Canvas?.Present(); }),
        Opt.Row(Opt.Button("적용 (Enter)", Apply, true), Opt.Button("취소 (Esc)", CancelWarp)),
        Opt.Help("지금 레이어의 그림 둘레에 면이 생깁니다. 점을 끌어 모양을 맞추세요 (Shift+끌기 = 전체 이동). 두 면은 건물 모서리처럼 꺾인 벽에, 격자는 천·종이 휘기에. 도구를 바꾸면 자동 적용."));
}

/// <summary>퍼펫 왜곡 (MLS 강체 변형)</summary>
public sealed class PuppetTool : ToolBase
{
    public override string Id => "puppet";
    public override string Name => "퍼펫 왜곡";
    public override string Icon => "✣";
    public int Density = 24; public bool ShowMesh = true;
    WarpSession S; readonly List<(P2 p0, P2 p)> pins = new(); List<P2> V; int cols, rows;
    int? drag;
    public override bool Busy => drag != null;

    bool Begin()
    {
        S = WarpSession.Begin();
        if (S == null) return false;
        pins.Clear();
        var b = S.B; double step = Math.Max(8, Math.Max(b.W, b.H) / (double)Density);
        cols = Math.Max(1, (int)Math.Ceiling(b.W / step)); rows = Math.Max(1, (int)Math.Ceiling(b.H / step));
        V = new();
        for (int j = 0; j <= rows; j++) for (int i = 0; i <= cols; i++) V.Add(new P2(b.X + b.W * (double)i / cols, b.Y + b.H * (double)j / rows));
        return true;
    }
    List<P2> Deformed() => V.Select(p => MeshWarp.MlsRigid(pins, p.X, p.Y)).ToList();
    public List<(P2[] s, P2[] d)> Tris()
    {
        var D = Deformed(); var o = new List<(P2[], P2[])>(); int C = cols;
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < C; i++)
            {
                int a = j * (C + 1) + i, b = a + 1, c = a + C + 2, d = a + C + 1;
                o.Add((new[] { V[a], V[b], V[c] }, new[] { D[a], D[b], D[c] }));
                o.Add((new[] { V[a], V[c], V[d] }, new[] { D[a], D[c], D[d] }));
            }
        return o;
    }

    /// <summary>핀 꽂기 (시험·다른 모듈용)</summary>
    public void AddPin(P2 from, P2 to) { if (S == null && !Begin()) return; pins.Add((from, to)); S.Show(Tris()); }
    public void Apply() { if (S == null) return; S.Finish(true, "퍼펫 왜곡", Tris()); S = null; pins.Clear(); }
    public void CancelWarp() { if (S == null) return; S.Finish(false, null, null); S = null; pins.Clear(); }
    public override void Deactivate() => Apply();
    public override void Activate() { App.DocChanged -= Drop; App.DocChanged += Drop; }
    void Drop() { S = null; pins.Clear(); drag = null; }

    public override void Down(PointerArgs e)
    {
        if (S == null && !Begin()) return;
        var v = MetaLayers.Canvas?.V; if (v == null) return;
        int hit = pins.FindIndex(p => { var s = Gfx.S(v, p.p); return Math.Sqrt((s.X - e.SX) * (s.X - e.SX) + (s.Y - e.SY) * (s.Y - e.SY)) < 12; });
        if (hit >= 0 && e.Alt) { pins.RemoveAt(hit); S.Show(Tris()); return; }
        if (hit >= 0) { drag = hit; return; }
        var b = S.B;
        if (e.X < b.X || e.Y < b.Y || e.X > b.R || e.Y > b.B) { App.Toast("핀은 그림 안쪽에 꽂으세요"); return; }
        // 이미 휜 상태에서 꽂으면, 지금 보이는 자리의 원래 위치를 거꾸로 찾아 둠 (가까운 격자점 기준)
        double x0 = e.X, y0 = e.Y;
        if (pins.Count > 0)
        {
            var D = Deformed(); int bi = 0; double bd = double.MaxValue;
            for (int i = 0; i < D.Count; i++) { double dd = (D[i].X - e.X) * (D[i].X - e.X) + (D[i].Y - e.Y) * (D[i].Y - e.Y); if (dd < bd) { bd = dd; bi = i; } }
            x0 = V[bi].X + (e.X - D[bi].X); y0 = V[bi].Y + (e.Y - D[bi].Y);
        }
        pins.Add((new P2(x0, y0), new P2(e.X, e.Y)));
        drag = pins.Count - 1;
    }
    public override void Move(PointerArgs e)
    {
        if (drag is not int i) return;
        pins[i] = (pins[i].p0, new P2(e.X, e.Y));
        S.Show(Tris());
    }
    public override void Up(PointerArgs e) => drag = null;
    public override void Cancel() => drag = null;
    public override bool HandleKey(Keys k)
    {
        if (S == null) return false;
        if ((k & Keys.KeyCode) == Keys.Enter) { Apply(); return true; }
        if ((k & Keys.KeyCode) == Keys.Escape) { CancelWarp(); return true; }
        return false;
    }

    public override Rectangle OverlayBounds(ViewXf v)
    {
        if (S != null) return CUI.Bounds(Deformed().Concat(pins.Select(p => p.p)).Select(p => Gfx.S(v, p)), 12);
        var b = App.Active?.Target?.Bounds() ?? Rect.Empty;
        return b.IsEmpty ? Rectangle.Empty : Rectangle.Inflate(Gfx.ToScreen(v, b.X, b.Y, b.W, b.H), 3, 3);
    }
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (S == null)
        {
            var b = App.Active?.Target?.Bounds() ?? Rect.Empty;
            if (b.IsEmpty) return;
            using var dp = CUI.Dash(Color.FromArgb(232, 163, 61), 1, 5, 4);
            g.DrawRectangle(dp, Gfx.ToScreen(v, b.X, b.Y, b.W, b.H));
            return;
        }
        if (ShowMesh)
        {
            var D = Deformed().Select(p => Gfx.S(v, p)).ToList(); int C = cols;
            using var pen = new Pen(Color.FromArgb(89, 232, 163, 61), 1);
            for (int j = 0; j <= rows; j++)
                for (int i = 0; i <= C; i++)
                {
                    var a = D[j * (C + 1) + i];
                    if (i < C) g.DrawLine(pen, a, D[j * (C + 1) + i + 1]);
                    if (j < rows) g.DrawLine(pen, a, D[(j + 1) * (C + 1) + i]);
                }
        }
        foreach (var p in pins)
        {
            var s = Gfx.S(v, p.p);
            using var br = new SolidBrush(Color.FromArgb(232, 163, 61)); using var wp = new Pen(Color.White, 2);
            g.FillEllipse(br, s.X - 7, s.Y - 7, 14, 14); g.DrawEllipse(wp, s.X - 7, s.Y - 7, 14, 14);
        }
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("퍼펫 왜곡"),
        CUI.Seg(new[] { ("12", "거칠게 (빠름)"), ("24", "보통"), ("40", "촘촘히") }, () => Density.ToString(), v => { CancelWarp(); Density = int.Parse(v); }),
        Opt.Check("격자 보이기", () => ShowMesh, v => { ShowMesh = v; MetaLayers.Canvas?.Present(); }),
        Opt.Row(Opt.Button("적용 (Enter)", Apply, true), Opt.Button("취소 (Esc)", CancelWarp)),
        Opt.Help("그림 위를 탭해 핀을 꽂고(관절·고정할 곳), 핀을 끌면 나머지가 자연스럽게 따라 휩니다. Alt+탭 = 핀 빼기. 도구를 바꾸면 자동 적용."));
}
