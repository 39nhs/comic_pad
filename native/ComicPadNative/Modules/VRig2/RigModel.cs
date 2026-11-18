// 버튜버 리깅 (Live2D 식) 자료 구조: 파라미터 · 아트메쉬(부위) · 디포머(워프·회전) · 키폼 · 클리핑 · 물리.
//  - 작품 저장: doc.Extra["vtRig"] (JSON, .cpad 의 맨 위 키 — 웹 판은 모르는 키라 그대로 둠). 그림은 레이어에서 가져오므로 리그에는 좌표만.
//  - 좌표는 모두 "작품 좌표"(문서 픽셀). 부위 메쉬의 쉼 위치 = 텍스처 좌표(UV)이고, 키폼은 쉼 위치에서 얼마나 옮기는지(오프셋)를 적는다.
//  - 디포머는 부모 디포머 공간(=부모의 쉼 좌표) 안에서 정의되고, 점은 자식 → 부모 순서로 변형된다 (합성 함수).
//  - 키폼: 파라미터 1~4 개에 키 값들을 두고 각 키 조합마다 모양(오프셋·불투명도·그리기 순서·각도)을 저장, 사이는 다중 선형 보간.
//  - 왼/오른쪽: Live2D 와 같이 L = 캐릭터의 왼쪽 = 화면 오른쪽.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.VRig2;

/// <summary>파라미터 (고개 각도·눈 뜨기·입 벌림 …)</summary>
public sealed class RParam
{
    public string Id, Name, Group = "기타";
    public double Min, Max, Def;
    public bool User;              // 사용자가 만든 파라미터
    public RParam Clone() => (RParam)MemberwiseClone();
}

/// <summary>키 축: 파라미터 하나 + 키 값들 (오름차순)</summary>
public sealed class KeyAxis
{
    public string Param;
    public List<double> Keys = new();
    public KeyAxis Clone() => new() { Param = Param, Keys = new List<double>(Keys) };
}

/// <summary>키폼 하나 (모든 종류 공용: 쓰지 않는 값은 기본값 그대로)</summary>
public sealed class KForm
{
    public double[] D;             // 점마다 (dx,dy) 오프셋 — 메쉬 꼭짓점 / 워프 격자점. null = 모두 0
    public double Tx, Ty;          // 메쉬 전체 이동 (시차 등, 점마다 오프셋에 더함)
    public double Op = 1;          // 불투명도 (0~1)
    public double Ord;             // 그리기 순서 (메쉬) — 보간 뒤 반올림
    public double A, S = 1;        // 회전 디포머: 각도(도), 크기
    public KForm Clone() { var f = (KForm)MemberwiseClone(); f.D = (double[])D?.Clone(); return f; }
}

/// <summary>키폼 격자: 축(파라미터) 0~4 개, 모양 = 키 조합 수 (축 0 이 가장 빠르게 바뀜)</summary>
public sealed class KeyGrid
{
    public const int MaxAxes = 4;
    public List<KeyAxis> Axes = new();
    public List<KForm> Forms = new() { new KForm() };

    public int Count { get { int n = 1; foreach (var a in Axes) n *= Math.Max(1, a.Keys.Count); return n; } }
    public KeyGrid Clone() => new() { Axes = Axes.Select(a => a.Clone()).ToList(), Forms = Forms.Select(f => f.Clone()).ToList() };
    public KeyAxis Axis(string p) => Axes.FirstOrDefault(a => a.Param == p);

    public int Index(int[] idx) { int i = 0, stride = 1; for (int a = 0; a < Axes.Count; a++) { i += idx[a] * stride; stride *= Axes[a].Keys.Count; } return i; }
    public int[] Unindex(int i) { var r = new int[Axes.Count]; for (int a = 0; a < Axes.Count; a++) { int n = Axes[a].Keys.Count; r[a] = i % n; i /= n; } return r; }

    /// <summary>파라미터 값 → (모양 번호, 무게) 목록. 축마다 둘러싼 두 키 사이 선형 (범위 밖은 끝 키)</summary>
    public void Weights(Func<string, double> val, List<(int i, double w)> o)
    {
        o.Clear();
        int n = Axes.Count;
        if (n == 0) { o.Add((0, 1)); return; }
        Span<int> lo = stackalloc int[MaxAxes]; Span<double> t = stackalloc double[MaxAxes]; Span<int> stride = stackalloc int[MaxAxes];
        int s = 1;
        for (int a = 0; a < n; a++)
        {
            var k = Axes[a].Keys; double v = val(Axes[a].Param);
            stride[a] = s; s *= k.Count;
            if (k.Count == 1 || v <= k[0]) { lo[a] = 0; t[a] = 0; continue; }
            if (v >= k[^1]) { lo[a] = k.Count - 2; t[a] = 1; continue; }
            int j = 0; while (j < k.Count - 2 && v > k[j + 1]) j++;
            lo[a] = j; t[a] = (v - k[j]) / (k[j + 1] - k[j]);
        }
        int corners = 1 << n;
        for (int c = 0; c < corners; c++)
        {
            double w = 1; int idx = 0; bool skip = false;
            for (int a = 0; a < n; a++)
            {
                bool hi = (c >> a & 1) == 1;
                if (Axes[a].Keys.Count == 1) { if (hi) { skip = true; break; } idx += 0; continue; }
                w *= hi ? t[a] : 1 - t[a];
                idx += (lo[a] + (hi ? 1 : 0)) * stride[a];
            }
            if (skip || w <= 1e-12) continue;
            o.Add((idx, w));
        }
        if (o.Count == 0) o.Add((0, 1));
    }

    /// <summary>모든 축 값이 정확히 키 위면 그 모양 번호, 아니면 -1 (편집은 키 위에서만)</summary>
    public int ExactIndex(Func<string, double> val, double eps = 1e-6)
    {
        var idx = new int[Axes.Count];
        for (int a = 0; a < Axes.Count; a++)
        {
            double v = val(Axes[a].Param);
            int j = Axes[a].Keys.FindIndex(k => Math.Abs(k - v) <= eps);
            if (j < 0) return -1;
            idx[a] = j;
        }
        return Index(idx);
    }

    /// <summary>보간한 모양 (pointCount: D 길이 = 점 수 × 2)</summary>
    public KForm Blend(Func<string, double> val, int pointCount, List<(int i, double w)> tmp = null, KForm into = null)
    {
        tmp ??= new List<(int, double)>();
        Weights(val, tmp);
        var r = into ?? new KForm();
        r.Tx = r.Ty = r.Ord = r.A = 0; r.Op = 0; r.S = 0;
        bool anyD = false;
        foreach (var (i, _) in tmp) if (Forms[i].D != null) { anyD = true; break; }
        if (anyD) { if (r.D == null || r.D.Length != pointCount * 2) r.D = new double[pointCount * 2]; else Array.Clear(r.D); }
        else r.D = null;
        foreach (var (i, w) in tmp)
        {
            var f = Forms[i];
            r.Tx += f.Tx * w; r.Ty += f.Ty * w; r.Op += f.Op * w; r.Ord += f.Ord * w; r.A += f.A * w; r.S += f.S * w;
            if (f.D != null && r.D != null) { var d = f.D; var rd = r.D; int m = Math.Min(d.Length, rd.Length); for (int k = 0; k < m; k++) rd[k] += d[k] * w; }
        }
        return r;
    }

    /// <summary>파라미터 축 넣기 (키 하나: 지금 모양 그대로)</summary>
    public bool AddAxis(string param, double key)
    {
        if (Axis(param) != null || Axes.Count >= MaxAxes) return false;
        Axes.Add(new KeyAxis { Param = param, Keys = new List<double> { key } });
        return true;                                  // 키 1개짜리 축은 모양 수가 그대로
    }

    /// <summary>축에 키 값 넣기: 새 키의 모양 = 그 값에서 보간한 모양 (모양이 바뀌지 않음)</summary>
    public void AddKey(string param, double v, int pointCount)
    {
        var ax = Axis(param);
        if (ax == null) { AddAxis(param, v); return; }
        if (ax.Keys.Any(k => Math.Abs(k - v) < 1e-9)) return;
        int a = Axes.IndexOf(ax);
        var oldAxes = Axes.Select(x => x.Clone()).ToList();
        var old = new KeyGrid { Axes = oldAxes, Forms = Forms };
        int pos = ax.Keys.FindIndex(k => k > v); if (pos < 0) pos = ax.Keys.Count;
        ax.Keys.Insert(pos, v);
        var nf = new List<KForm>(Count);
        var tmp = new List<(int, double)>();
        for (int i = 0; i < Count; i++)
        {
            var idx = Unindex(i);
            if (idx[a] != pos)
            {
                var oi = (int[])idx.Clone(); if (oi[a] > pos) oi[a]--;
                nf.Add(Forms[old.Index(oi)]);
            }
            else
            {
                // 다른 축은 그 키 값 그대로, 이 축은 v 에서 보간
                var vals = new Dictionary<string, double>();
                for (int b = 0; b < Axes.Count; b++) vals[Axes[b].Param] = Axes[b].Keys[idx[b]];
                nf.Add(old.Blend(p => vals.TryGetValue(p, out var x) ? x : 0, pointCount, tmp));
            }
        }
        Forms = nf;
    }

    /// <summary>축에서 키 값 지우기 (마지막 키면 축째로 지움 — 남는 모양은 그 키의 모양)</summary>
    public bool RemoveKey(string param, double v)
    {
        var ax = Axis(param);
        if (ax == null) return false;
        int j = ax.Keys.FindIndex(k => Math.Abs(k - v) < 1e-9);
        if (j < 0) return false;
        int a = Axes.IndexOf(ax);
        var nf = new List<KForm>();
        for (int i = 0; i < Count; i++) if (Unindex(i)[a] != j) nf.Add(Forms[i]);
        if (ax.Keys.Count == 1) { nf = Forms.ToList(); Axes.RemoveAt(a); Forms = nf; return true; }
        ax.Keys.RemoveAt(j);
        Forms = nf;
        return true;
    }

    /// <summary>모든 모양의 점 수를 바꿈 (메쉬 편집 뒤): map(옛 D) → 새 D</summary>
    public void RemapPoints(Func<double[], double[]> map) { foreach (var f in Forms) if (f.D != null) f.D = map(f.D); }
}

/// <summary>아트메쉬 (쉼 위치 = 작품 좌표, 삼각형 번호)</summary>
public sealed class RMesh
{
    public double[] V = Array.Empty<double>();
    public int[] T = Array.Empty<int>();
    public int N => V.Length / 2;
    public RMesh Clone() => new() { V = (double[])V.Clone(), T = (int[])T.Clone() };
}

/// <summary>부위 (레이어 하나 또는 레이어 폴더 하나 = 텍스처, 그 위의 메쉬)</summary>
public sealed class RPart
{
    public string Id, LayerId, Name, Role = "headAcc", Side = "", Parent;
    public double Order;                       // 기본 그리기 순서 (클수록 위). 키폼 Ord 가 있으면 그 값
    public double Depth;                        // 고개를 돌릴 때 앞뒤 (자동 리그 시차, + 앞)
    public double Density = 4;                  // 자동 메쉬 촘촘함 (1~10)
    public double[] Region;                     // 메쉬를 만들 영역 [x,y,w,h] (좌우로 나눈 눈 등) — null = 레이어 전체
    public List<string> Clip = new();           // 이 부위들이 칠해진 곳에만 그림 (클리핑 마스크)
    public bool ClipInvert;                     // 반대로: 마스크 바깥에만
    public RMesh Mesh = new();
    public KeyGrid Grid = new();
    public bool Hide;                           // 편집기에서 잠시 숨김 (저장)
    public RPart Clone() { var p = (RPart)MemberwiseClone(); p.Region = (double[])Region?.Clone(); p.Clip = new List<string>(Clip); p.Mesh = Mesh.Clone(); p.Grid = Grid.Clone(); return p; }
}

/// <summary>디포머: warp(격자, Cols×Rows 칸) 또는 rot(회전 중심)</summary>
public sealed class RDeformer
{
    public string Id, Name, Kind = "warp", Parent;
    public double X, Y, W, H;                   // warp: 쉼 사각형
    public int Cols = 2, Rows = 2;
    public bool Smooth = true;                  // warp: 부드러운 곡선 보간 (끄면 칸마다 직선)
    public double Px, Py;                       // rot: 회전 중심 (쉼)
    public double Len = 60;                     // rot: 화면 손잡이 길이
    public KeyGrid Grid = new();
    public bool IsWarp => Kind == "warp";
    public int Nodes => (Cols + 1) * (Rows + 1);
    public int PointCount => IsWarp ? Nodes : 0;
    public RDeformer Clone() { var d = (RDeformer)MemberwiseClone(); d.Grid = Grid.Clone(); return d; }
    public (double x, double y) RestNode(int i) { int c = i % (Cols + 1), r = i / (Cols + 1); return (X + W * c / Cols, Y + H * r / Rows); }
}

/// <summary>물리 입력: 파라미터 → 뿌리 이동(x) 또는 기울기(angle)</summary>
public sealed class PhysIn { public string Param; public double Weight = 0.5; public string Kind = "x"; public PhysIn Clone() => (PhysIn)MemberwiseClone(); }

/// <summary>물리 (진자 사슬): 고개·몸 움직임 → 머리카락·장식 파라미터</summary>
public sealed class RPhys
{
    public string Id, Name, Output;
    public List<PhysIn> Inputs = new();
    public double OutScale = 1;                 // 출력 크기
    public bool Reverse;
    public int Segments = 2;
    public double Length = 1;                   // 마디 길이 (상대)
    public double Damping = 2.5;                // 감쇠 (1/초)
    public double Gravity = 1;                  // 중력 (상대)
    public double Stiffness = 0.25;             // 제자리로 돌아오려는 힘
    public double Wind;                         // 바람 (옆으로 미는 힘)
    public bool On = true;
    public RPhys Clone() { var p = (RPhys)MemberwiseClone(); p.Inputs = Inputs.Select(i => i.Clone()).ToList(); return p; }
}

/// <summary>리그 전체</summary>
public sealed class Rig2
{
    public const string Format = "comicpad-vrig2";
    public int W, H;                            // 만든 작품 크기
    public List<RParam> Params = new();
    public List<RDeformer> Deformers = new();
    public List<RPart> Parts = new();
    public List<RPhys> Physics = new();
    public JsonObject Opts = new();

    public RParam Param(string id) => Params.FirstOrDefault(p => p.Id == id);
    public RPart Part(string id) => Parts.FirstOrDefault(p => p.Id == id);
    public RDeformer Def(string id) => id == null ? null : Deformers.FirstOrDefault(d => d.Id == id);

    public Rig2 Clone() => FromJson(ToJson());

    public Dictionary<string, double> Defaults() { var d = new Dictionary<string, double>(); foreach (var p in Params) d[p.Id] = p.Def; return d; }

    static int seq;
    public string NewId(string prefix)
    {
        string id;
        do id = prefix + (++seq).ToString("x") + Random.Shared.Next(0x100, 0xfff).ToString("x"); while (Parts.Any(p => p.Id == id) || Deformers.Any(d => d.Id == id) || Physics.Any(p => p.Id == id) || Params.Any(p => p.Id == id));
        return id;
    }

    /// <summary>표준 파라미터가 빠져 있으면 넣음</summary>
    public void EnsureStdParams()
    {
        foreach (var p in StdParams.All) if (Param(p.Id) == null) Params.Add(p.Clone());
    }

    /// <summary>부모를 따라가면 자기 자신이 나오는지 (디포머 고리 막기)</summary>
    public bool WouldCycle(string defId, string newParent)
    {
        for (var p = Def(newParent); p != null; p = Def(p.Parent)) if (p.Id == defId) return true;
        return false;
    }

    // ------------------------------------------------------------------ JSON
    static double R(double v) { if (!double.IsFinite(v)) return 0; double r = Math.Round(v, 3); return r == 0 ? 0 : r; }
    static JsonArray Arr(IEnumerable<double> v) { var a = new JsonArray(); foreach (var x in v) a.Add(JsonValue.Create(R(x))); return a; }
    static JsonArray IArr(IEnumerable<int> v) { var a = new JsonArray(); foreach (var x in v) a.Add(JsonValue.Create(x)); return a; }
    static double Num(JsonNode n, double d) { if (n is JsonValue v) { if (v.TryGetValue<double>(out var x)) return x; if (v.TryGetValue<int>(out var i)) return i; if (v.TryGetValue<long>(out var l)) return l; } return d; }
    static string Str(JsonNode n, string d = null) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : d;
    static bool Bool(JsonNode n, bool d) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
    static double[] Dbl(JsonNode n) => n is JsonArray a ? a.Select(x => Num(x, 0)).ToArray() : null;
    static int[] Int(JsonNode n) => n is JsonArray a ? a.Select(x => (int)Num(x, 0)).ToArray() : Array.Empty<int>();

    static JsonObject GridJson(KeyGrid g)
    {
        var axes = new JsonArray();
        foreach (var a in g.Axes) axes.Add(new JsonObject { ["param"] = a.Param, ["keys"] = Arr(a.Keys) });
        var forms = new JsonArray();
        foreach (var f in g.Forms)
        {
            var o = new JsonObject();
            if (f.D != null && f.D.Any(x => R(x) != 0)) o["d"] = Arr(f.D);
            if (R(f.Tx) != 0 || R(f.Ty) != 0) o["t"] = Arr(new[] { f.Tx, f.Ty });
            if (R(f.Op) != 1) o["op"] = R(f.Op);
            if (R(f.Ord) != 0) o["ord"] = R(f.Ord);
            if (R(f.A) != 0) o["a"] = R(f.A);
            if (R(f.S) != 1) o["s"] = R(f.S);
            forms.Add(o);
        }
        return new JsonObject { ["axes"] = axes, ["forms"] = forms };
    }
    static KeyGrid GridFrom(JsonNode n)
    {
        var g = new KeyGrid();
        if (n is not JsonObject o) return g;
        foreach (var a in o["axes"] as JsonArray ?? new JsonArray())
            g.Axes.Add(new KeyAxis { Param = Str(a?["param"], ""), Keys = (Dbl(a?["keys"]) ?? new[] { 0.0 }).ToList() });
        g.Forms.Clear();
        foreach (var f in o["forms"] as JsonArray ?? new JsonArray())
        {
            var t = Dbl(f?["t"]);
            g.Forms.Add(new KForm { D = Dbl(f?["d"]), Tx = t?.Length > 0 ? t[0] : 0, Ty = t?.Length > 1 ? t[1] : 0, Op = Num(f?["op"], 1), Ord = Num(f?["ord"], 0), A = Num(f?["a"], 0), S = Num(f?["s"], 1) });
        }
        // 망가진 파일: 모양 수를 맞춤
        int need = g.Count;
        while (g.Forms.Count < need) g.Forms.Add(g.Forms.Count > 0 ? g.Forms[^1].Clone() : new KForm());
        if (g.Forms.Count > need) g.Forms.RemoveRange(need, g.Forms.Count - need);
        return g;
    }

    public JsonObject ToJson()
    {
        var ps = new JsonArray();
        foreach (var p in Params) ps.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name, ["group"] = p.Group, ["min"] = R(p.Min), ["max"] = R(p.Max), ["def"] = R(p.Def), ["user"] = p.User });
        var ds = new JsonArray();
        foreach (var d in Deformers)
        {
            var o = new JsonObject { ["id"] = d.Id, ["name"] = d.Name, ["kind"] = d.Kind, ["parent"] = d.Parent };
            if (d.IsWarp) { o["rect"] = Arr(new[] { d.X, d.Y, d.W, d.H }); o["cols"] = d.Cols; o["rows"] = d.Rows; o["smooth"] = d.Smooth; }
            else { o["pivot"] = Arr(new[] { d.Px, d.Py }); o["len"] = R(d.Len); }
            o["grid"] = GridJson(d.Grid);
            ds.Add(o);
        }
        var parts = new JsonArray();
        foreach (var p in Parts)
        {
            parts.Add(new JsonObject
            {
                ["id"] = p.Id, ["layer"] = p.LayerId, ["name"] = p.Name, ["role"] = p.Role, ["side"] = p.Side, ["parent"] = p.Parent,
                ["order"] = R(p.Order), ["depth"] = R(p.Depth), ["density"] = R(p.Density), ["region"] = p.Region != null ? Arr(p.Region) : null,
                ["clip"] = new JsonArray(p.Clip.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()), ["clipInvert"] = p.ClipInvert, ["hide"] = p.Hide,
                ["mesh"] = new JsonObject { ["v"] = Arr(p.Mesh.V), ["t"] = IArr(p.Mesh.T) },
                ["grid"] = GridJson(p.Grid),
            });
        }
        var phys = new JsonArray();
        foreach (var p in Physics)
        {
            var ins = new JsonArray();
            foreach (var i in p.Inputs) ins.Add(new JsonObject { ["param"] = i.Param, ["weight"] = R(i.Weight), ["kind"] = i.Kind });
            phys.Add(new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["output"] = p.Output, ["inputs"] = ins, ["outScale"] = R(p.OutScale), ["reverse"] = p.Reverse,
                ["segments"] = p.Segments, ["length"] = R(p.Length), ["damping"] = R(p.Damping), ["gravity"] = R(p.Gravity), ["stiffness"] = R(p.Stiffness), ["wind"] = R(p.Wind), ["on"] = p.On,
            });
        }
        return new JsonObject
        {
            ["format"] = Format, ["version"] = 1, ["w"] = W, ["h"] = H,
            ["params"] = ps, ["deformers"] = ds, ["parts"] = parts, ["physics"] = phys, ["opts"] = Opts.DeepClone(),
        };
    }

    public static Rig2 FromJson(JsonNode n)
    {
        if (n is not JsonObject o || Str(o["format"]) != Format) throw new InvalidDataException("ComicPad 버튜버 리그가 아닙니다");
        var r = new Rig2 { W = (int)Num(o["w"], 0), H = (int)Num(o["h"], 0), Opts = o["opts"]?.DeepClone() as JsonObject ?? new JsonObject() };
        foreach (var p in o["params"] as JsonArray ?? new JsonArray())
            r.Params.Add(new RParam { Id = Str(p?["id"], "Param"), Name = Str(p?["name"], ""), Group = Str(p?["group"], "기타"), Min = Num(p?["min"], 0), Max = Num(p?["max"], 1), Def = Num(p?["def"], 0), User = Bool(p?["user"], false) });
        foreach (var d in o["deformers"] as JsonArray ?? new JsonArray())
        {
            var df = new RDeformer { Id = Str(d?["id"], "D"), Name = Str(d?["name"], ""), Kind = Str(d?["kind"], "warp") == "rot" ? "rot" : "warp", Parent = Str(d?["parent"]), Grid = GridFrom(d?["grid"]) };
            if (df.IsWarp)
            {
                var rc = Dbl(d?["rect"]) ?? new double[] { 0, 0, 1, 1 };
                df.X = rc[0]; df.Y = rc[1]; df.W = Math.Max(1e-3, rc[2]); df.H = Math.Max(1e-3, rc[3]);
                df.Cols = Math.Clamp((int)Num(d?["cols"], 2), 1, 16); df.Rows = Math.Clamp((int)Num(d?["rows"], 2), 1, 16); df.Smooth = Bool(d?["smooth"], true);
            }
            else { var pv = Dbl(d?["pivot"]) ?? new double[] { 0, 0 }; df.Px = pv[0]; df.Py = pv[1]; df.Len = Num(d?["len"], 60); }
            r.Deformers.Add(df);
        }
        foreach (var p in o["parts"] as JsonArray ?? new JsonArray())
        {
            var part = new RPart
            {
                Id = Str(p?["id"], "P"), LayerId = Str(p?["layer"]), Name = Str(p?["name"], ""), Role = Str(p?["role"], "headAcc"), Side = Str(p?["side"], ""), Parent = Str(p?["parent"]),
                Order = Num(p?["order"], 0), Depth = Num(p?["depth"], 0), Density = Num(p?["density"], 4), Region = Dbl(p?["region"]),
                Clip = (p?["clip"] as JsonArray ?? new JsonArray()).Select(x => Str(x, "")).Where(x => x.Length > 0).ToList(), ClipInvert = Bool(p?["clipInvert"], false), Hide = Bool(p?["hide"], false),
                Mesh = new RMesh { V = Dbl(p?["mesh"]?["v"]) ?? Array.Empty<double>(), T = Int(p?["mesh"]?["t"]) },
                Grid = GridFrom(p?["grid"]),
            };
            r.Parts.Add(part);
        }
        foreach (var p in o["physics"] as JsonArray ?? new JsonArray())
        {
            r.Physics.Add(new RPhys
            {
                Id = Str(p?["id"], "PH"), Name = Str(p?["name"], ""), Output = Str(p?["output"]),
                Inputs = (p?["inputs"] as JsonArray ?? new JsonArray()).Select(i => new PhysIn { Param = Str(i?["param"], ""), Weight = Num(i?["weight"], 0.5), Kind = Str(i?["kind"], "x") }).ToList(),
                OutScale = Num(p?["outScale"], 1), Reverse = Bool(p?["reverse"], false), Segments = Math.Clamp((int)Num(p?["segments"], 2), 1, 8), Length = Num(p?["length"], 1),
                Damping = Num(p?["damping"], 2.5), Gravity = Num(p?["gravity"], 1), Stiffness = Num(p?["stiffness"], 0.25), Wind = Num(p?["wind"], 0), On = Bool(p?["on"], true),
            });
        }
        return r;
    }

    public string ToJsonString() => ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>작품에 저장된 리그 (없으면 null)</summary>
    public static Rig2 FromDoc(Core.Document d)
    {
        if (d?.Extra["vtRig"] is not JsonObject o) return null;
        try { return FromJson(o); } catch { return null; }
    }
}

/// <summary>표준 파라미터 (Live2D 이름 · 범위 · 기본값)</summary>
public static class StdParams
{
    static RParam P(string id, string name, string group, double min, double max, double def = 0) => new() { Id = id, Name = name, Group = group, Min = min, Max = max, Def = def };
    public static readonly RParam[] All =
    {
        P("ParamAngleX", "고개 좌우", "고개", -30, 30), P("ParamAngleY", "고개 위아래", "고개", -30, 30), P("ParamAngleZ", "고개 기울기", "고개", -30, 30),
        P("ParamEyeLOpen", "왼눈 뜨기 (화면 오른쪽)", "눈", 0, 1, 1), P("ParamEyeLSmile", "왼눈 웃음", "눈", 0, 1),
        P("ParamEyeROpen", "오른눈 뜨기 (화면 왼쪽)", "눈", 0, 1, 1), P("ParamEyeRSmile", "오른눈 웃음", "눈", 0, 1),
        P("ParamEyeBallX", "눈동자 좌우", "눈", -1, 1), P("ParamEyeBallY", "눈동자 위아래", "눈", -1, 1),
        P("ParamBrowLY", "왼눈썹 위아래", "눈썹", -1, 1), P("ParamBrowRY", "오른눈썹 위아래", "눈썹", -1, 1),
        P("ParamBrowLForm", "왼눈썹 모양 (화남 ↔ 곤란)", "눈썹", -1, 1), P("ParamBrowRForm", "오른눈썹 모양 (화남 ↔ 곤란)", "눈썹", -1, 1),
        P("ParamMouthForm", "입 모양 (시무룩 ↔ 웃음)", "입", -1, 1), P("ParamMouthOpenY", "입 벌림", "입", 0, 1), P("ParamCheek", "볼 부풀림·홍조", "입", 0, 1),
        P("ParamBodyAngleX", "몸 좌우", "몸", -10, 10), P("ParamBodyAngleY", "몸 앞뒤", "몸", -10, 10), P("ParamBodyAngleZ", "몸 기울기", "몸", -10, 10),
        P("ParamBreath", "숨쉬기", "몸", 0, 1),
        P("ParamHairFront", "앞머리 흔들림", "머리카락", -1, 1), P("ParamHairSide", "옆머리 흔들림", "머리카락", -1, 1), P("ParamHairBack", "뒷머리 흔들림", "머리카락", -1, 1),
    };
}

/// <summary>부위 역할 (자동 리그가 쓰는 뜻) — 예전 버튜버(VRig) 역할 이름과도 이어 줌</summary>
public static class RigRoles
{
    public sealed record Role(string Id, string Name, double Depth, bool Head, bool Sided);
    public static readonly Role[] All =
    {
        new("body", "몸", 0, false, false), new("neck", "목", 0, false, false), new("bodyAcc", "몸에 붙은 것", 0, false, false),
        new("hairBack", "뒷머리", -0.35, true, false), new("ear", "귀", -0.2, true, true),
        new("face", "얼굴 (바탕)", 0, true, false), new("blush", "볼터치", 0.05, true, true), new("nose", "코", 0.3, true, false),
        new("eye", "눈 (통째로)", 0.08, true, true), new("eyeWhite", "흰자", 0.08, true, true), new("iris", "눈동자", 0.1, true, true),
        new("eyelid", "속눈썹·눈꺼풀", 0.12, true, true), new("eyeClosed", "감은 눈", 0.12, true, true), new("brow", "눈썹", 0.15, true, true),
        new("mouth", "입", 0.12, true, false), new("mouthInner", "입 안 (벌렸을 때)", 0.11, true, false),
        new("hairSide", "옆머리", 0.2, true, true), new("hairFront", "앞머리", 0.45, true, false), new("headAcc", "머리에 붙은 것", 0.3, true, false),
        new("none", "(쓰지 않음)", 0, false, false),
    };
    static readonly Dictionary<string, Role> map = All.ToDictionary(r => r.Id);
    public static Role Get(string id) => id != null && map.TryGetValue(id, out var r) ? r : map["headAcc"];
    public static string Name(string id) => Get(id).Name;

    /// <summary>이름으로 역할 짐작 (확실하지 않으면 null) — 한국어·영어·일본어</summary>
    public static string Guess(string name)
    {
        string n = System.Text.RegularExpressions.Regex.Replace((name ?? "").ToLowerInvariant(), @"\s+", "");
        bool Has(string re) => System.Text.RegularExpressions.Regex.IsMatch(n, re);
        if (Has("(배경|background|^bg|paper|용지|背景)")) return "none";
        if (Has("(흰자|white|sclera|白目)")) return "eyeWhite";
        if (Has("(눈동자|동공|홍채|iris|pupil|瞳|黒目)")) return "iris";
        if (Has("(속눈썹|눈꺼풀|윗눈|lash|eyelid|lid|まつ毛|まつげ|まぶた)")) return "eyelid";
        if (Has("(눈썹|brow|眉)")) return "brow";
        if (Has("((감은|감음|감김|closed|close|閉).*(눈|eye|目)|(눈|eye|目).*(감|closed|close|閉))")) return "eyeClosed";
        if (Has("(입안|입속|입벌|벌린입|mouthin|innermouth|mouthopen|openmouth|口内|口の中)")) return "mouthInner";
        if (Has("(입|mouth|口|唇|lip)")) return "mouth";
        if (Has("(옆머리|sidehair|hairside|横髪|もみあげ)")) return "hairSide";
        if (Has("(앞머리|fronthair|hairfront|bang|前髪)")) return "hairFront";
        if (Has("(뒷머리|backhair|hairback|後ろ髪|後髪)")) return "hairBack";
        if (Has("(머리카락|hair|髪)")) return "hairFront";
        if (Has("(눈|eye|目)")) return "eye";
        if (Has("(볼|홍조|blush|cheek|頬|チーク)")) return "blush";
        if (Has("(코|nose|鼻)")) return "nose";
        if (Has("(귀|^ear|ears?$|耳)")) return "ear";
        if (Has("(목|neck|首)")) return "neck";
        if (Has("(얼굴|face|head|顔|頭|피부|skin)")) return "face";
        if (Has("(몸|body|torso|옷|cloth|체|体|服|팔|arm|손|hand)")) return "body";
        if (Has("(모자|hat|리본|ribbon|머리띠|핀|hairpin|crown|horn|뿔)")) return "headAcc";
        return null;
    }

    /// <summary>예전 버튜버(부위 정하기) 역할 → 새 역할</summary>
    public static string FromOld(string old) => old switch
    {
        "body" => "body", "hairBack" => "hairBack", "head" => "face", "blush" => "blush", "eyesOpen" => "eye", "pupils" => "iris",
        "eyesClosed" => "eyeClosed", "brows" => "brow", "mouthClosed" => "mouth", "mouthOpen" => "mouthInner", "hairFront" => "hairFront",
        "headOther" => "headAcc", "bodyOther" => "bodyAcc", _ => "none",
    };
    /// <summary>새 역할 → 예전 역할 (.cpvt 를 웹 판·예전 스튜디오에서도 열 수 있게)</summary>
    public static string ToOld(string role) => role switch
    {
        "body" or "neck" => "body", "bodyAcc" => "bodyOther", "hairBack" => "hairBack", "face" => "head", "blush" => "blush",
        "eye" or "eyeWhite" or "eyelid" => "eyesOpen", "iris" => "pupils", "eyeClosed" => "eyesClosed", "brow" => "brows",
        "mouth" => "mouthClosed", "mouthInner" => "mouthOpen", "hairFront" or "hairSide" => "hairFront", "none" => "none", _ => "headOther",
    };

    public static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
