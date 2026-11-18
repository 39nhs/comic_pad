// 물리: 진자 사슬 (뿌리 = 고개·몸 파라미터로 옆으로 움직이고 기울어짐, 마디는 중력·제자리 스프링·감쇠로 따라옴).
// 사슬 끝이 뿌리 기준 "아래" 에서 얼마나 기울었는지(각도)를 출력 파라미터(머리카락 흔들림 등)로.
// 고정 1/120 초 걸음 + 베를레 적분 + 마디 길이 맞추기(앞에서부터) → 큰 dt 나 급한 움직임에도 터지지 않는다.
namespace ComicPadNative.Modules.VRig2;

public static class RigPhysics
{
    public sealed class State
    {
        public double[] X, Y, PX, PY;
        public double RootX, RootA;
        public bool Init;
        public double Out;            // 마지막 출력 (-1~1)
    }

    const double H = 1.0 / 120;
    const double MaxTilt = 0.5;       // 이 각도(라디안)면 출력 끝까지

    static double Norm(RParam p, double v)
    {
        if (p == null) return 0;
        double d = v - p.Def;
        double span = d >= 0 ? p.Max - p.Def : p.Def - p.Min;
        return span <= 1e-9 ? 0 : Math.Clamp(d / span, -1, 1);
    }

    /// <summary>입력 → 뿌리 이동·기울기</summary>
    static (double x, double a) Root(Rig2 rig, RPhys ph, Func<string, double> val)
    {
        double x = 0, a = 0;
        foreach (var i in ph.Inputs)
        {
            double n = Norm(rig.Param(i.Param), val(i.Param)) * i.Weight;
            if (i.Kind == "angle") a += n * Math.PI / 6; else x += n;
        }
        return (x, a);
    }

    /// <summary>dt 초만큼 진행하고 출력 파라미터를 values 에 씀</summary>
    public static void Step(Rig2 rig, Dictionary<string, State> states, Dictionary<string, double> values, double dt)
    {
        double V(string id) => values.TryGetValue(id ?? "", out var v) ? v : rig.Param(id)?.Def ?? 0;
        dt = Math.Clamp(dt, 0, 0.1);
        foreach (var ph in rig.Physics)
        {
            var outP = rig.Param(ph.Output);
            if (outP == null) continue;
            if (!states.TryGetValue(ph.Id, out var s)) states[ph.Id] = s = new State();
            int n = Math.Clamp(ph.Segments, 1, 8);
            double L = Math.Max(0.05, ph.Length);
            var (rx, ra) = Root(rig, ph, V);
            if (!s.Init || s.X == null || s.X.Length != n + 1)
            {
                s.X = new double[n + 1]; s.Y = new double[n + 1]; s.PX = new double[n + 1]; s.PY = new double[n + 1];
                for (int i = 0; i <= n; i++) { s.X[i] = s.PX[i] = rx - Math.Sin(ra) * i * L; s.Y[i] = s.PY[i] = Math.Cos(ra) * i * L; }
                s.RootX = rx; s.RootA = ra; s.Init = true;
            }
            if (!ph.On) { s.Out = 0; values[ph.Output] = outP.Def; continue; }
            int steps = (int)Math.Ceiling(dt / H);
            double g = 40 * ph.Gravity, k = 40 * ph.Stiffness, wind = 20 * ph.Wind;
            double damp = Math.Exp(-Math.Max(0, ph.Damping) * H);
            double x0 = s.RootX, a0 = s.RootA;
            for (int st = 1; st <= steps; st++)
            {
                double t = (double)st / steps;
                double rootX = x0 + (rx - x0) * t, rootA = a0 + (ra - a0) * t;
                s.PX[0] = s.X[0]; s.PY[0] = s.Y[0];
                s.X[0] = rootX; s.Y[0] = 0;
                double sn = Math.Sin(rootA), cs = Math.Cos(rootA);
                for (int i = 1; i <= n; i++)
                {
                    double restX = rootX - sn * i * L, restY = cs * i * L;
                    double vx = (s.X[i] - s.PX[i]) * damp, vy = (s.Y[i] - s.PY[i]) * damp;
                    double ax = k * (restX - s.X[i]) + wind, ay = g + k * (restY - s.Y[i]);
                    s.PX[i] = s.X[i]; s.PY[i] = s.Y[i];
                    s.X[i] += vx + ax * H * H; s.Y[i] += vy + ay * H * H;
                }
                // 마디 길이 (뿌리부터)
                for (int i = 1; i <= n; i++)
                {
                    double dx = s.X[i] - s.X[i - 1], dy = s.Y[i] - s.Y[i - 1];
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1e-9) { dx = 0; dy = 1; d = 1; }
                    double nx = s.X[i - 1] + dx / d * L, ny = s.Y[i - 1] + dy / d * L;
                    s.X[i] = nx; s.Y[i] = ny;
                }
                // 혹시 모를 숫자 문제
                for (int i = 0; i <= n; i++) if (!double.IsFinite(s.X[i]) || !double.IsFinite(s.Y[i])) { s.Init = false; break; }
                if (!s.Init) break;
            }
            s.RootX = rx; s.RootA = ra;
            if (!s.Init) { values[ph.Output] = outP.Def; continue; }
            double ang = Math.Atan2(s.X[n] - s.X[0], s.Y[n] - s.Y[0]) + ra;   // 뿌리 기준 "아래" 에서 기운 각
            double o = Math.Clamp(ang / MaxTilt * ph.OutScale, -1, 1) * (ph.Reverse ? -1 : 1);
            s.Out = o;
            values[ph.Output] = o >= 0 ? outP.Def + o * (outP.Max - outP.Def) : outP.Def + o * (outP.Def - outP.Min);
        }
    }
}
