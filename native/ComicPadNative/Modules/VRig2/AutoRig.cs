// 자동 리그: 역할이 정해진 레이어들로 Live2D 식 리그를 한 번에 만든다 (사용자가 다듬는 출발점).
//   몸 회전(BodyAngleZ) → 몸 워프(BodyAngleX/Y·숨쉬기) → 고개 회전(AngleZ) → 고개 워프(AngleX×AngleY, 구 투영 원근)
//     ├ 눈 워프(눈 뜨기×웃음: 흰자·눈동자를 감는 선으로 눌러 모음) ─ 눈알 워프(EyeBallX×Y) ─ 눈동자 (흰자로 클리핑)
//     ├ 속눈썹: 감으면 감는 선으로 내려오며 뒤집힘(‿) / 감은 눈 그림이 있으면 바꿔 보임
//     ├ 눈썹 워프(위아래×모양) · 입 워프(벌림×모양, 입 안은 벌릴 때 펼쳐짐) · 머리카락 워프(흔들림, 물리로 움직임)
//     └ 부위마다 깊이 시차(AngleX·Y 키폼: 앞머리는 더, 뒷머리는 반대로)
// 눈·눈썹처럼 양쪽이 한 레이어에 그려져 있으면 가운데 빈 틈에서 나눠 두 부위(같은 텍스처, 다른 메쉬)로 만든다.
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.VRig2;

public readonly record struct Box(double X, double Y, double W, double H)
{
    public static readonly Box Empty = new(0, 0, 0, 0);
    public bool IsEmpty => W <= 0 || H <= 0;
    public double R => X + W; public double B => Y + H; public double Cx => X + W / 2; public double Cy => Y + H / 2;
    public Box Union(Box o) => IsEmpty ? o : o.IsEmpty ? this : FromLTRB(Math.Min(X, o.X), Math.Min(Y, o.Y), Math.Max(R, o.R), Math.Max(B, o.B));
    public Box Inflate(double dx, double dy) => new(X - dx, Y - dy, W + 2 * dx, H + 2 * dy);
    public static Box FromLTRB(double l, double t, double r, double b) => new(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
    public bool Contains(double x, double y) => x >= X && y >= Y && x < R && y < B;
}

public static class AutoRig
{
    /// <summary>부위 후보 (레이어 또는 폴더)</summary>
    public sealed class Cand
    {
        public string Id, Name, Role;
        public bool Folder, Visible, Guessed;
        public int Index;
        public RigTexture Tex;
    }

    static bool EffVisible(Document d, Layer l) { for (var p = l; p != null; p = d.ById(d.ParentOf(p))) if (!p.Visible) return false; return true; }

    /// <summary>후보 고르기 (아래 → 위). roles: 사용자가 정한 역할 (레이어 id → 역할)</summary>
    public static List<Cand> Candidates(Document d, IDictionary<string, string> roles = null)
    {
        var old = (d.Extra["vtuber"] as JsonObject)?["parts"] as JsonObject;
        string RoleOf(Layer l, out bool guessed)
        {
            guessed = false;
            if (roles != null && roles.TryGetValue(l.Id, out var r)) return r;
            if (old?[l.Id]?["role"] is JsonValue ov && ov.TryGetValue<string>(out var os)) { var m = RigRoles.FromOld(os); if (m != "none" || os == "none") return m; }
            guessed = true;
            return RigRoles.Guess(l.Name);
        }
        var list = new List<Cand>();
        var consumed = new HashSet<string>();
        // 1) 폴더 하나를 부위로 (폴더 이름이 역할이고 안 레이어들이 따로 역할을 갖지 않을 때, 또는 사용자가 정했을 때)
        foreach (var f in d.Layers.Where(l => l.IsFolder))
        {
            if (consumed.Contains(f.Id)) continue;
            var kids = d.Layers.Where(k => !k.IsFolder && !k.IsAdjust && k.Px != null && d.IsInside(k, f)).ToList();
            if (kids.Count == 0) continue;
            string g = RoleOf(f, out bool guessed);
            bool explicitRole = roles != null && roles.ContainsKey(f.Id);
            bool kidsOwn = kids.Any(k => { var kr = RigRoles.Guess(k.Name); return kr != null && kr != g; }) || kids.Any(k => roles != null && roles.ContainsKey(k.Id));
            if (!(explicitRole || (g != null && g != "none" && !kidsOwn))) continue;
            foreach (var k in d.Layers.Where(k => d.IsInside(k, f))) consumed.Add(k.Id);
            consumed.Add(f.Id);
            if (g == "none") continue;
            var tex = RigTexture.FromFolder(d, f);
            if (tex.W <= 2) continue;
            list.Add(new Cand { Id = f.Id, Name = f.Name, Role = g, Folder = true, Visible = EffVisible(d, f), Guessed = guessed, Index = kids.Max(k => d.IndexOf(k)), Tex = tex });
        }
        // 2) 그림 레이어
        for (int i = 0; i < d.Layers.Count; i++)
        {
            var l = d.Layers[i];
            if (l.IsFolder || l.IsAdjust || l.Px == null || consumed.Contains(l.Id)) continue;
            string role = RoleOf(l, out bool guessed);
            if (role == "none") continue;
            bool vis = EffVisible(d, l);
            bool explicitRole = roles != null && roles.ContainsKey(l.Id);
            if (!vis && !explicitRole && role is not ("eyeClosed" or "mouthInner")) continue;   // 숨긴 레이어는 "다른 모양" 역할만
            var tex = RigTexture.FromSurface(l.Px, l.Id);
            if (tex.W <= 2) continue;
            // 화면을 거의 다 덮는 이름 모를 레이어 = 배경
            if (role == null && (tex.W - 6) * (double)(tex.H - 6) >= 0.85 * d.W * d.H) continue;
            list.Add(new Cand { Id = l.Id, Name = l.Name, Role = role, Folder = false, Visible = vis, Guessed = guessed, Index = i, Tex = tex });
        }
        return list.OrderBy(c => c.Index).ToList();
    }

    /// <summary>텍스처에서 그림이 있는 범위 (region 안)</summary>
    public static Box Bounds(RigTexture t, double[] region = null, int thr = 8)
    {
        var p = t.Mip[0];
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < t.H; y++)
            for (int x = 0; x < t.W; x++)
            {
                if (p[y * t.W + x] >> 24 <= thr) continue;
                if (region != null) { double dx = t.X + x + 0.5, dy = t.Y + y + 0.5; if (dx < region[0] || dy < region[1] || dx >= region[0] + region[2] || dy >= region[1] + region[3]) continue; }
                if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
        return x1 < 0 ? Box.Empty : new Box(t.X + x0, t.Y + y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>양쪽에 그림이 있으면 cx 근처 빈 세로줄 (작품 x), 없으면 null. side: 한쪽에만 있으면 그쪽</summary>
    static double? SplitGap(RigTexture t, double cx, out string side)
    {
        side = "";
        var p = t.Mip[0];
        var col = new int[t.W];
        for (int y = 0; y < t.H; y++) for (int x = 0; x < t.W; x++) if (p[y * t.W + x] >> 24 > 0) col[x]++;
        int c0 = Array.FindIndex(col, v => v > 0), c1 = Array.FindLastIndex(col, v => v > 0);
        if (c0 < 0) return null;
        double l = t.X + c0, r = t.X + c1 + 1;
        if (r <= cx) { side = "R"; return null; }
        if (l >= cx) { side = "L"; return null; }
        int cc = (int)Math.Round(cx - t.X);
        for (int o = 0; o < t.W; o++)
        {
            foreach (int x in new[] { cc + o, cc - o })
                if (x > c0 && x < c1 && col[x] == 0)
                {
                    // 빈 줄 묶음의 가운데
                    int a = x, b = x;
                    while (a - 1 > c0 && col[a - 1] == 0) a--;
                    while (b + 1 < c1 && col[b + 1] == 0) b++;
                    return t.X + (a + b + 1) / 2.0;
                }
            if (cc + o >= c1 && cc - o <= c0) break;
        }
        return null;
    }

    static KeyGrid Grid((string p, double[] keys)[] axes, Func<Func<string, double>, KForm> make)
    {
        var g = new KeyGrid();
        foreach (var (p, k) in axes) g.Axes.Add(new KeyAxis { Param = p, Keys = k.ToList() });
        g.Forms = new List<KForm>();
        for (int i = 0; i < g.Count; i++)
        {
            var idx = g.Unindex(i);
            var vals = new Dictionary<string, double>();
            for (int a = 0; a < g.Axes.Count; a++) vals[g.Axes[a].Param] = g.Axes[a].Keys[idx[a]];
            g.Forms.Add(make(p => vals.TryGetValue(p, out var v) ? v : 0));
        }
        return g;
    }
    static double[] WarpD(RDeformer d, Func<double, double, int, int, (double dx, double dy)> f)
    {
        var o = new double[d.Nodes * 2];
        for (int k = 0; k < d.Nodes; k++) { var (x, y) = d.RestNode(k); var (dx, dy) = f(x, y, k % (d.Cols + 1), k / (d.Cols + 1)); o[k * 2] = dx; o[k * 2 + 1] = dy; }
        return o.Any(v => v != 0) ? o : null;
    }
    static double[] MeshD(RMesh m, Func<double, double, (double dx, double dy)> f)
    {
        var o = new double[m.V.Length];
        for (int k = 0; k < m.N; k++) { var (dx, dy) = f(m.V[k * 2], m.V[k * 2 + 1]); o[k * 2] = dx; o[k * 2 + 1] = dy; }
        return o.Any(v => v != 0) ? o : null;
    }

    static readonly double[] K3 = { -30, 0, 30 }, K01 = { 0, 1 }, K11 = { -1, 0, 1 }, K10 = { -10, 0, 10 };

    /// <summary>역할별 기본 메쉬 촘촘함</summary>
    static double DensityFor(string role) => role switch { "face" or "hairFront" or "hairBack" or "hairSide" => 5, "body" => 4, "mouth" or "mouthInner" => 5, "eye" or "eyeWhite" or "eyelid" or "eyeClosed" => 4, _ => 3 };

    /// <summary>자동 리그 만들기 → 리그 + 텍스처 (레이어 id → 텍스처)</summary>
    public static (Rig2 rig, Dictionary<string, RigTexture> tex) Build(Document d, IDictionary<string, string> roles, List<string> notes = null)
    {
        notes ??= new List<string>();
        var cands = Candidates(d, roles);
        var tex = cands.ToDictionary(c => c.Id, c => c.Tex);
        var rig = new Rig2 { W = d.W, H = d.H };
        rig.EnsureStdParams();
        if (cands.Count == 0) { notes.Add("부위로 쓸 그림 레이어가 없습니다"); return (rig, tex); }
        var bounds = cands.ToDictionary(c => c.Id, c => Bounds(c.Tex));
        // ---- 얼굴·머리·몸 범위
        Box U(Func<Cand, bool> f) { var b = Box.Empty; foreach (var c in cands) if (f(c) && !bounds[c.Id].IsEmpty) b = b.Union(bounds[c.Id]); return b; }
        var all = U(_ => true);
        var faceB = U(c => c.Role == "face");
        var featB = U(c => c.Role is "eye" or "eyeWhite" or "iris" or "eyelid" or "brow" or "mouth" or "nose");
        if (faceB.IsEmpty) faceB = !featB.IsEmpty ? featB.Inflate(featB.W * 0.35, featB.H * 0.6) : new Box(all.X + all.W * 0.25, all.Y + all.H * 0.05, all.W * 0.5, all.H * 0.4);
        // 역할 모름 → 머리 근처면 머리에 붙은 것, 아니면 몸에 붙은 것
        var headGuess = faceB.Inflate(faceB.W * 0.6, faceB.H * 0.5);
        foreach (var c in cands) if (c.Role == null) c.Role = headGuess.Contains(bounds[c.Id].Cx, bounds[c.Id].Cy) ? "headAcc" : "bodyAcc";
        double cx = faceB.Cx, cy = faceB.Cy, faceW = faceB.W, faceH = faceB.H;
        var headB = U(c => RigRoles.Get(c.Role).Head).Union(faceB);
        var bodyB = U(c => !RigRoles.Get(c.Role).Head);
        if (bodyB.IsEmpty) bodyB = new Box(faceB.X, faceB.B, faceB.W, Math.Max(1, all.B - faceB.B));
        notes.Add($"얼굴 {faceW:0}×{faceH:0}px, 부위 후보 {cands.Count}개");

        // ---- 부위 (양쪽 역할은 나눔)
        var parts = rig.Parts;
        foreach (var c in cands)
        {
            var role = RigRoles.Get(c.Role);
            var list = new List<(string side, double[] region)>();
            if (role.Sided)
            {
                double? gap = SplitGap(c.Tex, cx, out string side);
                if (gap != null)
                {
                    double g = gap.Value, big = 1e6;
                    list.Add(("R", new[] { -big, -big, g + big, 2 * big }));
                    list.Add(("L", new[] { g, -big, big, 2 * big }));
                }
                else list.Add((side, null));
            }
            else list.Add(("", null));
            foreach (var (side, region) in list)
            {
                var p = new RPart
                {
                    Id = rig.NewId("P"), LayerId = c.Id, Role = c.Role, Side = side, Region = region,
                    Name = c.Name + (side == "L" ? " (왼·화면 오른쪽)" : side == "R" ? " (오른·화면 왼쪽)" : ""),
                    Order = c.Index * 10, Depth = role.Depth, Density = DensityFor(c.Role),
                };
                p.Mesh = MeshGen.Auto(c.Tex, p.Density, region);
                if (Environment.GetEnvironmentVariable("COMICPAD_VRIG_DEBUG") == "1") notes.Add(p.Name + ": " + MeshGen.LastStats);
                if (p.Mesh.T.Length == 0) continue;
                parts.Add(p);
            }
        }
        Box PB(RPart p) { var t = tex[p.LayerId]; return Bounds(t, p.Region); }
        var pb = parts.ToDictionary(p => p.Id, PB);
        Box UP(IEnumerable<RPart> ps) { var b = Box.Empty; foreach (var p in ps) b = b.Union(pb[p.Id]); return b; }

        // ---- 디포머: 몸
        var defs = rig.Deformers;
        double bodyBottom = Math.Max(bodyB.B, all.B);
        var bodyRot = new RDeformer { Id = "D_BodyRot", Name = "몸 회전", Kind = "rot", Px = bodyB.Cx, Py = bodyBottom, Len = Math.Max(40, faceH * 0.5) };
        bodyRot.Grid = Grid(new[] { ("ParamBodyAngleZ", K10) }, v => new KForm { A = v("ParamBodyAngleZ") * 0.3 });
        defs.Add(bodyRot);
        var allB = all.Inflate(all.W * 0.05, all.H * 0.03);
        var bodyWarp = new RDeformer { Id = "D_BodyWarp", Name = "몸 워프", Kind = "warp", Parent = bodyRot.Id, X = allB.X, Y = allB.Y, W = allB.W, H = allB.H, Cols = 3, Rows = 3, Smooth = true };
        double shoulderY = Math.Min(bodyB.Y + bodyB.H * 0.25, bodyBottom);
        bodyWarp.Grid = Grid(new[] { ("ParamBodyAngleX", K10), ("ParamBodyAngleY", K10), ("ParamBreath", K01) }, v =>
        {
            double bx = v("ParamBodyAngleX") / 10, by = v("ParamBodyAngleY") / 10, br = v("ParamBreath");
            return new KForm
            {
                D = WarpD(bodyWarp, (x, y, c, r) =>
                {
                    double t = Math.Clamp((bodyBottom - y) / Math.Max(1, bodyBottom - allB.Y), 0, 1);   // 0 = 아래, 1 = 위
                    double lean = Math.Pow(t, 1.2);
                    double dx = bx * bodyB.W * 0.06 * lean;
                    double dy = -by * allB.H * 0.012 * lean;
                    // 숨: 어깨 위(가슴부터)는 살짝 올라가고 어깨가 벌어짐
                    double up = y < shoulderY + bodyB.H * 0.2 ? 1 : Math.Clamp(1 - (y - shoulderY - bodyB.H * 0.2) / Math.Max(1, bodyB.H * 0.4), 0, 1);
                    dy -= br * allB.H * 0.007 * up * Math.Min(1, t * 2);
                    dx += br * (x - bodyB.Cx) * 0.012 * up * Math.Min(1, t * 2);
                    return (dx, dy);
                }),
            };
        });
        defs.Add(bodyWarp);
        // ---- 고개
        var headRot = new RDeformer { Id = "D_HeadRot", Name = "고개 회전", Kind = "rot", Parent = bodyWarp.Id, Px = cx, Py = faceB.B - faceH * 0.06, Len = faceH * 0.6 };
        headRot.Grid = Grid(new[] { ("ParamAngleZ", K3) }, v => new KForm { A = v("ParamAngleZ") / 3 });
        defs.Add(headRot);
        var hb = headB.Inflate(headB.W * 0.08, headB.H * 0.06);
        var headWarp = new RDeformer { Id = "D_HeadWarp", Name = "고개 워프 (좌우·위아래)", Kind = "warp", Parent = headRot.Id, X = hb.X, Y = hb.Y, W = hb.W, H = hb.H, Cols = 4, Rows = 4, Smooth = true };
        double Rx = faceW * 0.55, Ry = faceH * 0.6;
        headWarp.Grid = Grid(new[] { ("ParamAngleX", K3), ("ParamAngleY", K3) }, v =>
        {
            double th = v("ParamAngleX") / 30 * 15 * Math.PI / 180, ph = v("ParamAngleY") / 30 * 12 * Math.PI / 180;
            return new KForm
            {
                D = WarpD(headWarp, (x, y, c, r) =>
                {
                    // 머리 = 공 (얼굴 가운데가 가장 앞): 돌리면 앞쪽 점이 많이, 가장자리는 조금 움직임
                    double nx = (x - cx) / Rx, ny = (y - cy) / Ry;
                    double z = Math.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny));
                    double x1 = nx * Math.Cos(th) + z * Math.Sin(th), z1 = -nx * Math.Sin(th) + z * Math.Cos(th);
                    double y2 = ny * Math.Cos(ph) - z1 * Math.Sin(ph);
                    return ((x1 - nx) * Rx, (y2 - ny) * Ry);
                }),
            };
        });
        defs.Add(headWarp);

        double P = faceW * 0.12;                                     // 깊이 1 일 때 끝까지 돌리면 옮기는 거리
        string EyeOpenP(string s) => s == "R" ? "ParamEyeROpen" : "ParamEyeLOpen";
        string EyeSmileP(string s) => s == "R" ? "ParamEyeRSmile" : "ParamEyeLSmile";
        double Closed(Func<string, double> v, string s) => s == "" ? 1 - (v("ParamEyeLOpen") + v("ParamEyeROpen")) / 2 : 1 - v(EyeOpenP(s));
        (string, double[])[] EyeAxes(string s) => s == "" ? new[] { ("ParamEyeLOpen", K01), ("ParamEyeROpen", K01) } : new[] { (EyeOpenP(s), K01) };

        // 부위 키폼: 깊이 시차 (+ 덧붙인 축)
        void Parallax(RPart p, (string, double[])[] extra = null, Func<Func<string, double>, KForm, KForm> more = null)
        {
            var axes = new List<(string, double[])>();
            bool par = Math.Abs(p.Depth) > 1e-6 && RigRoles.Get(p.Role).Head;
            if (par) { axes.Add(("ParamAngleX", K3)); axes.Add(("ParamAngleY", K3)); }
            if (extra != null) axes.AddRange(extra);
            if (axes.Count == 0) return;
            p.Grid = Grid(axes.ToArray(), v =>
            {
                var f = new KForm();
                if (par) { f.Tx = p.Depth * P * v("ParamAngleX") / 30; f.Ty = -p.Depth * P * 0.7 * v("ParamAngleY") / 30; }
                return more != null ? more(v, f) : f;
            });
        }

        // ---- 눈 (쪽마다)
        var eyeRoles = new[] { "eye", "eyeWhite", "iris", "eyelid", "eyeClosed" };
        foreach (var side in parts.Where(p => eyeRoles.Contains(p.Role)).Select(p => p.Side).Distinct().ToList())
        {
            var eps = parts.Where(p => eyeRoles.Contains(p.Role) && p.Side == side).ToList();
            var whites = eps.Where(p => p.Role is "eyeWhite" or "eye").ToList();
            var eyeB = UP(whites.Count > 0 ? whites : eps);
            if (eyeB.IsEmpty) continue;
            string sn = side == "L" ? "왼눈" : side == "R" ? "오른눈" : "눈";
            bool hasLid = eps.Any(p => p.Role is "eyelid" or "eyeClosed");
            double yc = eyeB.Y + eyeB.H * 0.66;                              // 감는 선
            var eb = eyeB.Inflate(eyeB.W * 0.15, eyeB.H * 0.3);
            var ew = new RDeformer { Id = rig.NewId("D_Eye"), Name = sn + " 워프 (뜨기·웃음)", Kind = "warp", Parent = headWarp.Id, X = eb.X, Y = eb.Y, W = eb.W, H = eb.H, Cols = 2, Rows = 2, Smooth = false };
            double keep = hasLid ? 0 : 0.12;                                 // 눈을 통째로 그렸으면 얇은 선으로 남김
            ew.Grid = Grid(EyeAxes(side).Append((EyeSmileP(side == "" ? "L" : side), K01)).ToArray(), v =>
            {
                double cl = Closed(v, side), sm = v(EyeSmileP(side == "" ? "L" : side));
                return new KForm
                {
                    D = WarpD(ew, (x, y, c, r) =>
                    {
                        double dy = (yc + (y - yc) * keep - y) * cl;         // 감기: 감는 선으로
                        double bell = 1 - Math.Pow((x - eyeB.Cx) / (eb.W / 2), 2);
                        // 웃음: 아래가 올라옴 (∩), 감았으면 선이 위로 휨
                        if (r == ew.Rows) dy -= sm * eyeB.H * (0.22 + 0.15 * bell) * (1 - cl);
                        else if (r > 0) dy -= sm * eyeB.H * 0.1 * bell * (1 - cl);
                        dy -= sm * cl * eyeB.H * 0.15 * Math.Max(0, bell);
                        return (0, dy);
                    }),
                };
            });
            defs.Add(ew);
            var irises = eps.Where(p => p.Role == "iris").ToList();
            RDeformer ball = null;
            if (irises.Count > 0)
            {
                var ib = UP(irises).Inflate(2, 2);
                var wb = whites.Count > 0 ? UP(whites.Where(w => w.Role == "eyeWhite")) : Box.Empty;
                double rx = !wb.IsEmpty ? Math.Max(2, (wb.W - ib.W) / 2 * 0.9 + 1) : eyeB.W * 0.15;
                double ry = !wb.IsEmpty ? Math.Max(1, (wb.H - ib.H) / 2 * 0.9 + eyeB.H * 0.06) : eyeB.H * 0.12;
                ball = new RDeformer { Id = rig.NewId("D_Ball"), Name = sn + "알 (시선)", Kind = "warp", Parent = ew.Id, X = ib.X, Y = ib.Y, W = ib.W, H = ib.H, Cols = 1, Rows = 1, Smooth = false };
                ball.Grid = Grid(new[] { ("ParamEyeBallX", K11), ("ParamEyeBallY", K11) }, v => new KForm { D = WarpD(ball, (x, y, c, r) => (v("ParamEyeBallX") * rx, -v("ParamEyeBallY") * ry)) });
                defs.Add(ball);
            }
            bool hasClosedArt = eps.Any(p => p.Role == "eyeClosed");
            foreach (var p in eps)
            {
                switch (p.Role)
                {
                    case "eye": case "eyeWhite": p.Parent = ew.Id; Parallax(p); break;
                    case "iris":
                        p.Parent = ball?.Id ?? ew.Id;
                        p.Clip = parts.Where(w => w.Role == "eyeWhite" && (w.Side == p.Side || w.Side == "" || p.Side == "")).Select(w => w.Id).ToList();
                        Parallax(p);
                        break;
                    case "eyelid":
                    {
                        p.Parent = headWarp.Id;
                        var lb = pb[p.Id];
                        var m = p.Mesh;
                        double flip = lb.Cy;
                        Parallax(p, EyeAxes(side), (v, f) =>
                        {
                            double cl = Closed(v, side);
                            if (cl > 0)
                            {
                                // 감는 선으로 내려오면서 위아래 뒤집기 (속눈썹이 아래로 향한 ‿ 모양)
                                f.D = MeshD(m, (x, y) => (0, ((yc + (flip - y) * 0.55 - lb.H * 0.1) - y) * cl));
                                if (hasClosedArt) f.Op = 1 - cl;
                            }
                            return f;
                        });
                        break;
                    }
                    case "eyeClosed":
                        p.Parent = headWarp.Id;
                        Parallax(p, EyeAxes(side), (v, f) => { f.Op = Closed(v, side); return f; });
                        break;
                }
            }
        }
        // ---- 눈썹
        foreach (var p in parts.Where(p => p.Role == "brow"))
        {
            var b = pb[p.Id];
            var bb = b.Inflate(b.W * 0.1, b.H * 0.4);
            string s = p.Side == "R" ? "R" : "L";
            var w = new RDeformer { Id = rig.NewId("D_Brow"), Name = (p.Side == "L" ? "왼" : p.Side == "R" ? "오른" : "") + "눈썹 워프", Kind = "warp", Parent = headWarp.Id, X = bb.X, Y = bb.Y, W = bb.W, H = bb.H, Cols = 2, Rows = 1, Smooth = false };
            w.Grid = Grid(new[] { ($"ParamBrow{s}Y", K11), ($"ParamBrow{s}Form", K11) }, v =>
            {
                double by = v($"ParamBrow{s}Y"), fm = v($"ParamBrow{s}Form");
                return new KForm
                {
                    D = WarpD(w, (x, y, c, r) =>
                    {
                        // 안쪽 끝 = 얼굴 가운데 쪽
                        double tIn = p.Side == "" ? 0.5 : (x < cx ? (x - bb.X) / bb.W : 1 - (x - bb.X) / bb.W);
                        if (p.Side == "") tIn = 1 - Math.Abs(x - cx) / (bb.W / 2);
                        double dy = -by * faceH * 0.04 - fm * Math.Max(3, b.H * 1.1) * (tIn - 0.5) * 2;
                        return (0, dy);
                    }),
                };
            });
            defs.Add(w);
            p.Parent = w.Id;
            Parallax(p);
        }
        // ---- 입
        var mouths = parts.Where(p => p.Role == "mouth").ToList();
        var inners = parts.Where(p => p.Role == "mouthInner").ToList();
        if (mouths.Count > 0 || inners.Count > 0)
        {
            var mb = UP(mouths.Count > 0 ? mouths : inners);
            var ib = UP(inners);
            double ym = mb.Cy;
            double open = !ib.IsEmpty ? Math.Max(4, ib.B - ym) : Math.Clamp(mb.W * 0.45, 6, faceH * 0.12);
            double pad = Math.Max(1, mb.H * 0.15);
            var mr = Box.FromLTRB(mb.X - mb.W * 0.2, mb.Y - pad, mb.R + mb.W * 0.2, mb.B + pad);
            var w = new RDeformer { Id = rig.NewId("D_Mouth"), Name = "입 워프 (벌림·모양)", Kind = "warp", Parent = headWarp.Id, X = mr.X, Y = mr.Y, W = mr.W, H = mr.H, Cols = 2, Rows = 2, Smooth = false };
            w.Grid = Grid(new[] { ("ParamMouthOpenY", K01), ("ParamMouthForm", K11) }, v =>
            {
                double o = v("ParamMouthOpenY"), fm = v("ParamMouthForm");
                return new KForm
                {
                    D = WarpD(w, (x, y, c, r) =>
                    {
                        bool mid = c == 1;
                        // 입 안 그림이 있으면 입 선(윗입술)은 조금만 늘이고 입 안이 아래로 펼쳐짐, 없으면 선을 늘여 벌린 입으로
                        double dy = o * (inners.Count > 0 ? open * 0.18 : open) * (r / 2.0) * (mid ? 1 : 0.3);
                        double dx = o * (c == 0 ? 1 : c == 2 ? -1 : 0) * mb.W * 0.06;
                        dy += -fm * mb.W * 0.12 * (mid ? -0.3 : 1);
                        return (dx, dy);
                    }),
                };
            });
            defs.Add(w);
            foreach (var p in mouths) { p.Parent = w.Id; Parallax(p); }
            foreach (var p in inners)
            {
                p.Parent = headWarp.Id;
                var m = p.Mesh;
                Parallax(p, new[] { ("ParamMouthOpenY", new[] { 0, 0.25, 1 }) }, (v, f) =>
                {
                    double o = v("ParamMouthOpenY");
                    if (o < 1) { f.D = MeshD(m, (x, y) => (0, (ym - y) * (1 - o))); f.Op = Math.Min(1, o * 4); }
                    return f;
                });
            }
        }
        // ---- 머리카락 (부위마다 흔들림 워프)
        foreach (var p in parts.Where(p => p.Role is "hairFront" or "hairSide" or "hairBack"))
        {
            var b = pb[p.Id];
            var bb = b.Inflate(b.W * 0.1, b.H * 0.05);
            string param = p.Role == "hairFront" ? "ParamHairFront" : p.Role == "hairSide" ? "ParamHairSide" : "ParamHairBack";
            double swing = b.H * (p.Role == "hairFront" ? 0.12 : p.Role == "hairSide" ? 0.16 : 0.1);
            var w = new RDeformer { Id = rig.NewId("D_Hair"), Name = p.Name + " 흔들림", Kind = "warp", Parent = headWarp.Id, X = bb.X, Y = bb.Y, W = bb.W, H = bb.H, Cols = 2, Rows = 3, Smooth = true };
            w.Grid = Grid(new[] { (param, K11) }, v =>
            {
                double s = v(param);
                return new KForm { D = WarpD(w, (x, y, c, r) => { double t = Math.Pow((double)r / w.Rows, 1.6); return (s * swing * t, -Math.Abs(s) * swing * 0.18 * t * t); }) };
            });
            defs.Add(w);
            p.Parent = w.Id;
            Parallax(p);
        }
        // ---- 나머지
        foreach (var p in parts.Where(p => p.Parent == null))
        {
            if (RigRoles.Get(p.Role).Head) { p.Parent = headWarp.Id; Parallax(p); }
            else p.Parent = bodyWarp.Id;
        }
        // ---- 물리
        void Phys(string role, string name, string output, double len, double damp, double stiff)
        {
            if (!parts.Any(p => p.Role == role)) return;
            rig.Physics.Add(new RPhys
            {
                Id = "PH_" + output.Replace("Param", ""), Name = name, Output = output, Segments = 2, Length = len, Damping = damp, Stiffness = stiff, Gravity = 1, OutScale = 1,
                Inputs = new List<PhysIn> { new() { Param = "ParamAngleX", Weight = 0.6, Kind = "x" }, new() { Param = "ParamAngleZ", Weight = 0.6, Kind = "angle" }, new() { Param = "ParamBodyAngleX", Weight = 0.4, Kind = "x" }, new() { Param = "ParamBodyAngleZ", Weight = 0.3, Kind = "angle" } },
            });
        }
        Phys("hairFront", "앞머리", "ParamHairFront", 1, 4.5, 0.3);
        Phys("hairSide", "옆머리", "ParamHairSide", 1.3, 4.0, 0.2);
        Phys("hairBack", "뒷머리", "ParamHairBack", 1.6, 3.5, 0.15);
        rig.Opts["face"] = new JsonArray(faceB.X, faceB.Y, faceB.W, faceB.H);
        notes.Add($"부위 {parts.Count}개, 디포머 {defs.Count}개, 물리 {rig.Physics.Count}개");
        return (rig, tex);
    }
}
