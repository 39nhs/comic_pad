// BVH 모션 캡처 파일 → 데생 인형 포즈 (웹 판 app/js/three/bvh.js 이식)
// 관절 이름이 달라도(CMU·Mixamo·Biovision 등) 맞춰 보고, 뼈 방향을 따라 인형 관절 각도를 구한다(비틀림은 일부만).
// 사진 포즈 추정 결과(랜드마크) → 포즈 변환(웹 판 ai/pose.js 의 poseFromLandmarks·handFromLandmarks)도 여기에 둔다.
using System.Globalization;
using Pose = ComicPadNative.Modules.Scene3D.Engine3D.Pose;

namespace ComicPadNative.Modules.Scene3D;

public sealed class BvhJoint
{
    public string Name; public int Parent; public double[] Offset = new double[3];
    public List<string> Channels = new(); public int Ch0; public bool End;
}

public sealed class BvhData { public List<BvhJoint> Joints = new(); public List<float[]> Frames = new(); public double FrameTime; }

public static class Bvh
{
    const double D = Math.PI / 180;

    public static BvhData Parse(string text)
    {
        var tok = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        string Next() => i < tok.Length ? tok[i++] : null;
        double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var b = new BvhData();
        var stack = new List<int>();
        if (Next()?.ToUpperInvariant() != "HIERARCHY") throw new InvalidDataException("BVH 파일이 아닙니다");
        int channelCount = 0;
        while (i < tok.Length)
        {
            string t = Next(), u = t.ToUpperInvariant();
            if (u == "ROOT" || u == "JOINT")
            {
                b.Joints.Add(new BvhJoint { Name = Next(), Parent = stack.Count > 0 ? stack[^1] : -1 });
                stack.Add(b.Joints.Count - 1);
            }
            else if (u == "END")
            {
                Next();   // Site
                b.Joints.Add(new BvhJoint { Name = b.Joints[stack[^1]].Name + "_end", Parent = stack[^1], End = true });
                stack.Add(b.Joints.Count - 1);
            }
            else if (u == "OFFSET") { if (stack.Count > 0) b.Joints[stack[^1]].Offset = new[] { Num(Next()), Num(Next()), Num(Next()) }; }
            else if (u == "CHANNELS")
            {
                var j = b.Joints[stack[^1]]; int n = (int)Num(Next());
                j.Ch0 = channelCount;
                for (int k = 0; k < n; k++) j.Channels.Add(Next().ToLowerInvariant());
                channelCount += n;
            }
            else if (t == "}") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else if (u == "MOTION") break;
        }
        Next();                         // Frames:
        int frames = (int)Num(Next());
        Next(); Next();                 // Frame Time:
        double frameTime = Num(Next());
        for (int f = 0; f < frames && i < tok.Length; f++)
        {
            var row = new float[channelCount];
            for (int k = 0; k < channelCount; k++) row[k] = i < tok.Length ? (float)Num(tok[i++]) : 0;
            b.Frames.Add(row);
        }
        b.FrameTime = frameTime > 0 ? frameTime : 1.0 / 30;
        return b;
    }

    /// <summary>한 프레임의 관절별 월드 행렬</summary>
    static double[][] Fk(BvhData b, int f)
    {
        var row = b.Frames[f]; var outW = new double[b.Joints.Count][];
        for (int k = 0; k < b.Joints.Count; k++)
        {
            var j = b.Joints[k];
            var t = (double[])j.Offset.Clone(); var R = M3.Ident();
            for (int n = 0; n < j.Channels.Count; n++)
            {
                double v = row[j.Ch0 + n];
                switch (j.Channels[n])
                {
                    case "xposition": t[0] += v; break;
                    case "yposition": t[1] += v; break;
                    case "zposition": t[2] += v; break;
                    case "xrotation": R = M3.Mul(R, M3.RotX(v * D)); break;
                    case "yrotation": R = M3.Mul(R, M3.RotY(v * D)); break;
                    case "zrotation": R = M3.Mul(R, M3.RotZ(v * D)); break;
                }
            }
            var local = M3.Mul(M3.Translate(t), R);
            outW[k] = j.Parent < 0 ? local : M3.Mul(outW[j.Parent], local);
        }
        return outW;
    }

    // ---- 이름 맞추기 ----
    static string Norm(string s) { s = s.ToLowerInvariant(); int c = s.LastIndexOf(':'); if (c >= 0) s = s[(c + 1)..]; return new string(s.Where(ch => !char.IsWhiteSpace(ch) && ch != '_' && ch != '.' && ch != '-').ToArray()); }
    static IEnumerable<string> Side(string side, string[] names) => names.Select(n =>
    {
        if (n.StartsWith("left")) n = (side == "L" ? "left" : "right") + n[4..];
        else if (n.Length > 1 && n[0] == 'l' && char.IsAsciiLetterLower(n[1])) n = (side == "L" ? "l" : "r") + n[1..];
        return n;
    });
    static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["hips"] = new[] { "hips", "pelvis", "hip", "root" },
        ["spine"] = new[] { "spine", "lowerback", "abdomen", "spine01" },
        ["chest"] = new[] { "spine2", "chest", "upperchest", "spine1", "spine02", "chest2" },
        ["neck"] = new[] { "neck", "neck1", "neck01" },
        ["head"] = new[] { "head" },
        ["clav"] = new[] { "leftshoulder", "lcollar", "leftcollar", "lclavicle", "leftclavicle" },
        ["upper"] = new[] { "leftarm", "lshldr", "leftuparm", "lupperarm", "leftupperarm", "lshoulder" },
        ["fore"] = new[] { "leftforearm", "lforearm", "leftlowarm", "llowerarm", "leftelbow" },
        ["hand"] = new[] { "lefthand", "lhand", "leftwrist" },
        ["thigh"] = new[] { "leftupleg", "lthigh", "lefthip", "lhip", "leftthigh", "lupperleg" },
        ["shin"] = new[] { "leftleg", "lshin", "leftknee", "lknee", "leftlowleg", "llowerleg" },
        ["foot"] = new[] { "leftfoot", "lfoot", "leftankle" },
        ["toe"] = new[] { "lefttoebase", "ltoe", "lefttoe", "lefttoes", "lefttoebaseend" },
    };

    public static Dictionary<string, int> FindJoints(BvhData b)
    {
        var by = new Dictionary<string, int>();
        for (int k = 0; k < b.Joints.Count; k++) by[Norm(b.Joints[k].Name)] = k;     // 같은 이름이면 나중 것 (웹 판 Map 과 같음)
        int Pick(IEnumerable<string> names) { foreach (var n in names) if (by.TryGetValue(n, out int v)) return v; return -1; }
        var m = new Dictionary<string, int>();
        foreach (var k in new[] { "hips", "spine", "chest", "neck", "head" }) m[k] = Pick(Aliases[k]);
        if (m["chest"] == m["spine"]) m["chest"] = -1;
        foreach (var s in new[] { "L", "R" })
            foreach (var k in new[] { "clav", "upper", "fore", "hand", "thigh", "shin", "foot", "toe" }) m[k + s] = Pick(Side(s, Aliases[k]));
        return m;
    }

    // ---- 3x3 회전 도우미 (4x4 열 우선 행렬의 회전 부분) ----
    static double[] Rot3(double[] m) => new[] { m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10] };
    static double[] Apply3(double[] r, double[] v) => new[] { r[0] * v[0] + r[3] * v[1] + r[6] * v[2], r[1] * v[0] + r[4] * v[1] + r[7] * v[2], r[2] * v[0] + r[5] * v[1] + r[8] * v[2] };
    static double[] Tr3(double[] r) => new[] { r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8] };
    static double[] Mul3(double[] a, double[] b)
    {
        var o = new double[9];
        for (int c = 0; c < 3; c++) for (int rr = 0; rr < 3; rr++) o[c * 3 + rr] = a[rr] * b[c * 3] + a[3 + rr] * b[c * 3 + 1] + a[6 + rr] * b[c * 3 + 2];
        return o;
    }
    static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    static double[] Nrm(double[] v) { double l = M3.Hyp(v[0], v[1], v[2]); if (l == 0) l = 1; return new[] { v[0] / l, v[1] / l, v[2] / l }; }
    static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    static double[] Pos(double[] m) => new[] { m[12], m[13], m[14] };

    /// <summary>a 방향을 b 방향으로 돌리는 가장 짧은 회전 (3x3 열 우선)</summary>
    static double[] Arc(double[] a, double[] b)
    {
        a = Nrm(a); b = Nrm(b);
        var v = Cross(a, b); double c = Dot(a, b);
        if (c < -0.9999) { var ax = Nrm(Math.Abs(a[0]) < 0.9 ? Cross(a, new double[] { 1, 0, 0 }) : Cross(a, new double[] { 0, 1, 0 })); return AxisAngle(ax, Math.PI); }
        double k = 1 / (1 + c);
        return new[]
        {
            v[0] * v[0] * k + c, v[1] * v[0] * k + v[2], v[2] * v[0] * k - v[1],
            v[0] * v[1] * k - v[2], v[1] * v[1] * k + c, v[2] * v[1] * k + v[0],
            v[0] * v[2] * k + v[1], v[1] * v[2] * k - v[0], v[2] * v[2] * k + c,
        };
    }
    static double[] AxisAngle(double[] a, double t)
    {
        double x = a[0], y = a[1], z = a[2], c = Math.Cos(t), s = Math.Sin(t), C = 1 - c;
        return new[] { x * x * C + c, y * x * C + z * s, z * x * C - y * s, x * y * C - z * s, y * y * C + c, z * y * C + x * s, x * z * C + y * s, y * z * C - x * s, z * z * C + c };
    }
    /// <summary>두 방향(위·옆)으로 정한 좌표계</summary>
    static double[] Frame3(double[] up, double[] side)
    {
        var y = Nrm(up); var z = Nrm(Cross(side, y)); var x = Cross(y, z);
        return new[] { x[0], x[1], x[2], y[0], y[1], y[2], z[0], z[1], z[2] };
    }
    /// <summary>R = Rz·Ry·Rx 의 각도(도) — M3.Euler 와 같은 순서</summary>
    static double[] Euler(double[] r)
    {
        double r00 = r[0], r10 = r[1], r20 = r[2], r21 = r[5], r22 = r[8];
        double b = Math.Asin(Math.Clamp(-r20, -1, 1)), a, c;
        if (Math.Abs(r20) < 0.9999) { a = Math.Atan2(r21, r22); c = Math.Atan2(r10, r00); } else { a = Math.Atan2(-r[7], r[4]); c = 0; }
        return new[] { a / D, b / D, c / D }.Select(v => Math.Floor(v * 10 + 0.5) / 10).ToArray();
    }

    /// <summary>BVH 한 프레임 → 인형 포즈 { 관절: [x, y, z] }</summary>
    public static Pose Retarget(BvhData b, int f, Dictionary<string, int> map = null)
    {
        map ??= FindJoints(b);
        var W = Fk(b, f);
        double[] Q(string key) => map.TryGetValue(key, out int i) && i >= 0 ? Pos(W[i]) : null;
        return RetargetPoints(Q, map["hips"] >= 0 ? Rot3(W[map["hips"]]) : null);
    }

    /// <summary>
    /// 관절 위치들 → 인형 포즈. Q(이름) = [x, y, z] 또는 null (y 위, 사람은 +z 를 봄, 왼쪽 = +x).
    /// 이름: hips spine chest neck head / clavL upperL foreL handL thighL shinL footL (R 도 같음)
    /// hipsR 가 없으면 골반 회전을 엉덩이·어깨 위치로 계산 (사진 포즈 추정용)
    /// </summary>
    public static Pose RetargetPoints(Func<string, double[]> Q, double[] hipsR = null)
    {
        var pose = new Pose();
        var rest = Engine3D.MannequinMatrices(null, null);   // 인형의 차렷 자세
        double[] RestDir(string j, string child) => Nrm(Sub(Pos(rest[child]), Pos(rest[j])));
        var world = new Dictionary<string, double[]>();
        void SetRot(string j, double[] parentRot, double[] want) { var E = Mul3(Tr3(parentRot), want); pose[j] = Euler(E); world[j] = want; }
        void BoneTo(string j, string parent, string child, double[] a, double[] b)
        {
            if (a == null || b == null) { world[j] = world[parent]; return; }
            var d0 = Apply3(world[parent], RestDir(j, child));            // 지금 부모 방향 그대로일 때 뼈 방향
            var want = Mul3(Arc(d0, Sub(b, a)), world[parent]);
            SetRot(j, world[parent], want);
        }
        if (hipsR == null)
        {
            var hp = Q("hips"); var up = Q("chest") ?? Q("neck"); var tl = Q("thighL"); var tr = Q("thighR");
            if (hp != null && up != null && tl != null && tr != null)
            {
                var restF = Frame3(Sub(Pos(rest["chest"]), Pos(rest["hips"])), Sub(Pos(rest["thighL"]), Pos(rest["thighR"])));
                hipsR = Mul3(Frame3(Sub(up, hp), Sub(tl, tr)), Tr3(restF));
            }
            else hipsR = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        }
        pose["hips"] = Euler(hipsR); world["hips"] = hipsR;
        BoneTo("spine", "hips", "chest", Q("spine"), Q("chest") ?? Q("neck"));
        // 가슴: 위(목 쪽)와 옆(어깨 선) 두 방향으로 비틀림까지
        var chA = Q("chest") ?? Q("spine"); var nk = Q("neck"); var lA = Q("upperL"); var rA = Q("upperR");
        if (chA != null && nk != null && lA != null && rA != null)
        {
            var restF = Frame3(Sub(Pos(rest["neck"]), Pos(rest["chest"])), Sub(Pos(rest["upperArmL"]), Pos(rest["upperArmR"])));
            var wantF = Frame3(Sub(nk, chA), Sub(lA, rA));
            SetRot("chest", world["spine"], Mul3(wantF, Tr3(restF)));
        }
        else world["chest"] = world["spine"];
        BoneTo("neck", "chest", "head", Q("neck"), Q("head"));
        world["head"] = world["neck"];
        foreach (var s in new[] { "L", "R" })
        {
            BoneTo("shoulder" + s, "chest", "upperArm" + s, Q("clav" + s) ?? Q("chest"), Q("upper" + s));
            BoneTo("upperArm" + s, "shoulder" + s, "foreArm" + s, Q("upper" + s), Q("fore" + s));
            BoneTo("foreArm" + s, "upperArm" + s, "hand" + s, Q("fore" + s), Q("hand" + s));
            BoneTo("thigh" + s, "hips", "shin" + s, Q("thigh" + s), Q("shin" + s));
            BoneTo("shin" + s, "thigh" + s, "foot" + s, Q("shin" + s), Q("foot" + s));
        }
        foreach (var k in pose.Keys.ToList()) if (pose[k].All(v => Math.Abs(v) < 0.05)) pose.Remove(k);
        return pose;
    }

    public sealed record Result(List<Pose> Poses, double Fps, int Frames, int Matched);

    /// <summary>파일 전체 → 포즈 목록 (너무 길면 골고루 추림)</summary>
    public static Result Poses(string text, int max = 300)
    {
        var b = Parse(text);
        var map = FindJoints(b);
        int found = map.Values.Count(v => v >= 0);
        if (map["hips"] < 0 || found < 8) throw new InvalidDataException("관절 이름을 알아보지 못했습니다 (사람 모양 BVH 가 아닌 것 같습니다)");
        int n = b.Frames.Count, step = Math.Max(1, (int)Math.Ceiling((double)n / max));
        var poses = new List<Pose>();
        for (int f = 0; f < n; f += step) poses.Add(Retarget(b, f, map));
        if (poses.Count == 0) throw new InvalidDataException("BVH 에 프레임이 없습니다");
        return new Result(poses, 1 / (b.FrameTime * step), n, found);
    }

    // ---- 사진 포즈 추정 결과 → 포즈 (MediaPipe 랜드마크 형식; 추정기 자체는 네이티브에 아직 없음) ----
    public sealed record Landmark(double X, double Y, double Z, double Visibility = 1);

    /// <summary>MediaPipe 몸 랜드마크(월드 좌표, 미터) → 인형 포즈</summary>
    public static Pose PoseFromLandmarks(IReadOnlyList<Landmark> L)
    {
        // MediaPipe: x 오른쪽(사람의 왼쪽), y 아래, z 카메라 쪽이 작음 → 인형: y 위, 사람은 +z 를 봄
        double[] P(int i) => i < L.Count && L[i] != null && L[i].Visibility > 0.3 ? new[] { L[i].X, -L[i].Y, -L[i].Z } : null;
        double[] Raw(int i) => new[] { L[i].X, -L[i].Y, -L[i].Z };
        double[] Mid(double[] a, double[] b) => a != null && b != null ? new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 } : null;
        double[] Lerp(double[] a, double[] b, double t) => a != null && b != null ? new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t } : null;
        var hips = Mid(Raw(23), Raw(24)); var sh = Mid(Raw(11), Raw(12));
        var head = Mid(P(7), P(8)) ?? P(0);
        var Q = new Dictionary<string, double[]>
        {
            ["hips"] = hips, ["spine"] = Lerp(hips, sh, 0.35), ["chest"] = Lerp(hips, sh, 0.7), ["neck"] = sh, ["head"] = head,
            ["clavL"] = sh, ["clavR"] = sh, ["upperL"] = P(11), ["upperR"] = P(12), ["foreL"] = P(13), ["foreR"] = P(14), ["handL"] = P(15), ["handR"] = P(16),
            ["thighL"] = P(23), ["thighR"] = P(24), ["shinL"] = P(25), ["shinR"] = P(26), ["footL"] = P(27), ["footR"] = P(28),
        };
        return RetargetPoints(k => Q.GetValueOrDefault(k));
    }

    /// <summary>MediaPipe 손 랜드마크 21개 → 손 모델 포즈 (손가락 굽힘)</summary>
    public static Pose HandFromLandmarks(IReadOnlyList<Landmark> L)
    {
        double[] Vv(int a, int b) => new[] { L[b].X - L[a].X, L[b].Y - L[a].Y, L[b].Z - L[a].Z };
        double Ang(double[] u, double[] v) { double lu = M3.Hyp(u[0], u[1], u[2]), lv = M3.Hyp(v[0], v[1], v[2]); if (lu == 0) lu = 1; if (lv == 0) lv = 1; return Math.Acos(Math.Clamp(Dot(u, v) / (lu * lv), -1, 1)) * 180 / Math.PI; }
        double Lim(double d) => Math.Floor(Math.Clamp(d, 0, 110) + 0.5);
        var pose = new Pose();
        var F = new (string n, int a, int b, int c, int d)[] { ("index", 5, 6, 7, 8), ("middle", 9, 10, 11, 12), ("ring", 13, 14, 15, 16), ("pinky", 17, 18, 19, 20) };
        foreach (var (n, a, b, c, d) in F)
        {
            pose[n + "1"] = new[] { -Lim(Ang(Vv(0, a), Vv(a, b))), 0, 0 };
            pose[n + "2"] = new[] { -Lim(Ang(Vv(a, b), Vv(b, c))), 0, 0 };
            pose[n + "3"] = new[] { -Lim(Ang(Vv(b, c), Vv(c, d))), 0, 0 };
        }
        double t1 = Lim(Ang(Vv(0, 1), Vv(1, 2))), t2 = Lim(Ang(Vv(1, 2), Vv(2, 3))), t3 = Lim(Ang(Vv(2, 3), Vv(3, 4)));
        pose["thumb1"] = new[] { -Math.Floor(t1 * 0.6 + 0.5), 0, -Math.Floor(t1 * 0.8 + 0.5) };
        pose["thumb2"] = new[] { -t2, 0, -Math.Floor(t2 * 0.5 + 0.5) };
        pose["thumb3"] = new[] { -t3, 0, 0 };
        return pose;
    }
}
