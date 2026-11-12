// 색 관리 (ICC): 화면 색(sRGB) ↔ 인쇄 색(CMYK). 프로파일의 표(lut8/lut16)를 직접 읽어 계산 (웹 core/icc.js 그대로).
// 지원: v2/v4 프로파일의 mft1·mft2 표, Lab/XYZ 연결 공간. 큰 그림은 33×33×33 미리 계산한 표로 빠르게.
using System.Buffers.Binary;
using System.Text;

namespace ComicPadNative.Modules.Filters;

public sealed class IccProfile
{
    public byte[] Bytes;
    public string Space, Pcs, Cls, Name;
    public readonly Dictionary<string, (int off, int len, string type)> Tags = new();

    uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(o));
    ushort U16(int o) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan(o));
    string Str(int o, int n = 4) => Encoding.Latin1.GetString(Bytes, o, n);

    public static IccProfile Parse(byte[] b)
    {
        var p = new IccProfile { Bytes = b };
        p.Cls = p.Str(12); p.Space = p.Str(16).Trim(); p.Pcs = p.Str(20).Trim();
        uint n = p.U32(128);
        for (int i = 0; i < n; i++)
        {
            int o = 132 + i * 12;
            int off = (int)p.U32(o + 4), len = (int)p.U32(o + 8);
            p.Tags[p.Str(o)] = (off, len, off + 4 <= b.Length ? p.Str(off) : "");
        }
        p.Name = (p.ReadDesc() is var s && s.Length > 0 ? s : "이름 없는 프로파일").Replace(" ", "").Trim();
        return p;
    }

    string ReadDesc()
    {
        if (!Tags.TryGetValue("desc", out var t)) return "";
        if (t.type == "desc") { int n = (int)U32(t.off + 8); return Encoding.Latin1.GetString(Bytes, t.off + 12, Math.Max(0, n - 1)); }
        if (t.type == "mluc") { int off = (int)U32(t.off + 20), n = (int)U32(t.off + 24) / 2; var sb = new StringBuilder(); for (int i = 0; i < n; i++) sb.Append((char)U16(t.off + off + i * 2)); return sb.ToString(); }
        return "";
    }

    public sealed class Lut { public int Ic, Oc, G; public float[][] InT, OutT; public float[] Clut; public bool B16; }

    public Lut ReadLut(string sig)
    {
        if (!Tags.TryGetValue(sig, out var t) || (t.type != "mft2" && t.type != "mft1")) return null;
        int o = t.off; bool b16 = t.type == "mft2";
        int ic = Bytes[o + 8], oc = Bytes[o + 9], g = Bytes[o + 10];
        int nIn = b16 ? U16(o + 48) : 256, nOut = b16 ? U16(o + 50) : 256;
        int q = o + (b16 ? 52 : 48);
        float Rd() { float x = b16 ? U16(q) / 65535f : Bytes[q] / 255f; q += b16 ? 2 : 1; return x; }
        var inT = new float[ic][];
        for (int c = 0; c < ic; c++) { inT[c] = new float[nIn]; for (int k = 0; k < nIn; k++) inT[c][k] = Rd(); }
        var clut = new float[(int)Math.Pow(g, ic) * oc];
        for (int k = 0; k < clut.Length; k++) clut[k] = Rd();
        var outT = new float[oc][];
        for (int c = 0; c < oc; c++) { outT[c] = new float[nOut]; for (int k = 0; k < nOut; k++) outT[c][k] = Rd(); }
        return new Lut { Ic = ic, Oc = oc, G = g, InT = inT, Clut = clut, OutT = outT, B16 = b16 };
    }
}

public sealed class IccTransform
{
    readonly IccProfile p; readonly IccProfile.Lut b2a, a2b;
    public string Name => p.Name;

    static float Curve(float[] tab, double x) { int n = tab.Length - 1; double f = Math.Min(n, Math.Max(0, x * n)); int i = Math.Min(n - 1, (int)Math.Floor(f)); double t = f - i; return (float)(tab[i] + (tab[i + 1] - tab[i]) * t); }

    static void EvalLut(IccProfile.Lut L, double[] x, double[] outp)
    {
        int ic = L.Ic, oc = L.Oc, g = L.G;
        Span<int> idx = stackalloc int[ic]; Span<double> fr = stackalloc double[ic];
        for (int c = 0; c < ic; c++)
        {
            double f = Math.Min(g - 1, Math.Max(0, Curve(L.InT[c], x[c]) * (g - 1)));
            int i = Math.Min(g - 2, (int)Math.Floor(f));
            idx[c] = i; fr[c] = f - i;
        }
        for (int k = 0; k < oc; k++) outp[k] = 0;
        int corners = 1 << ic;
        for (int m = 0; m < corners; m++)
        {
            double w = 1; int off = 0;
            for (int c = 0; c < ic; c++)
            {
                int bit = (m >> (ic - 1 - c)) & 1;
                w *= bit != 0 ? fr[c] : 1 - fr[c];
                off = off * g + idx[c] + bit;
            }
            if (w == 0) continue;
            off *= oc;
            for (int k = 0; k < oc; k++) outp[k] += L.Clut[off + k] * w;
        }
        for (int k = 0; k < oc; k++) outp[k] = Curve(L.OutT[k], outp[k]);
    }

    // ---- sRGB ↔ Lab (D50) ----
    static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    static double Gam(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
    static readonly double[] W50 = { 0.9642, 1, 0.8249 };
    static double FLab(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
    static double FInv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27);

    public static double[] RgbToLab(double r, double g, double b)
    {
        r = Lin(r); g = Lin(g); b = Lin(b);
        double X = 0.4360747 * r + 0.3850649 * g + 0.1430804 * b, Y = 0.2225045 * r + 0.7168786 * g + 0.0606169 * b, Z = 0.0139322 * r + 0.0971045 * g + 0.7141733 * b;
        double fx = FLab(X / W50[0]), fy = FLab(Y / W50[1]), fz = FLab(Z / W50[2]);
        return new[] { 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz) };
    }
    public static double[] LabToRgb(double L, double a, double b)
    {
        double fy = (L + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        double X = FInv(fx) * W50[0], Y = FInv(fy) * W50[1], Z = FInv(fz) * W50[2];
        double r = 3.1338561 * X - 1.6168667 * Y - 0.4906146 * Z, g = -0.9787684 * X + 1.9161415 * Y + 0.0334540 * Z, bb = 0.0719453 * X - 0.2289914 * Y + 1.4052427 * Z;
        return new[] { Gam(Math.Clamp(r, 0, 1)), Gam(Math.Clamp(g, 0, 1)), Gam(Math.Clamp(bb, 0, 1)) };
    }

    static double[] LabEnc(IccProfile.Lut lut, double[] Lab) => lut.B16
        ? new[] { Lab[0] * 652.8 / 65535, (Lab[1] + 128) * 256 / 65535, (Lab[2] + 128) * 256 / 65535 }
        : new[] { Lab[0] / 100, (Lab[1] + 128) / 255, (Lab[2] + 128) / 255 };
    static double[] LabDec(IccProfile.Lut lut, double[] o) => lut.B16
        ? new[] { o[0] * 65535 / 652.8, o[1] * 65535 / 256 - 128, o[2] * 65535 / 256 - 128 }
        : new[] { o[0] * 100, o[1] * 255 - 128, o[2] * 255 - 128 };

    /// <summary>intent 0 = 지각적, 1 = 상대 색도</summary>
    public IccTransform(IccProfile prof, int intent = 0)
    {
        p = prof;
        if (p.Space != "CMYK") throw new InvalidDataException("CMYK 프로파일이 아닙니다 (" + p.Space + ")");
        b2a = p.ReadLut("B2A" + intent) ?? p.ReadLut("B2A0");
        a2b = p.ReadLut("A2B" + (intent == 0 ? 0 : 1)) ?? p.ReadLut("A2B0");
        if (b2a == null || a2b == null) throw new InvalidDataException("이 프로파일은 지원하지 않는 형식입니다 (표가 없음)");
    }

    double[] ToPcs(double r, double g, double b, IccProfile.Lut lut)
    {
        var L = RgbToLab(r, g, b);
        if (p.Pcs == "XYZ") { double fy = (L[0] + 16) / 116; return new[] { FInv(fy + L[1] / 500) * W50[0] / 1.999969, FInv(fy) * W50[1] / 1.999969, FInv(fy - L[2] / 200) * W50[2] / 1.999969 }; }
        return LabEnc(lut, L);
    }

    public double[] RgbToCmyk(double r, double g, double b) { var o = new double[4]; EvalLut(b2a, ToPcs(r, g, b, b2a), o); return o; }
    public double[] CmykToRgb(double c, double m, double y, double k)
    {
        var o = new double[3];
        EvalLut(a2b, new[] { c, m, y, k }, o);
        if (p.Pcs == "XYZ")
        {
            double X = o[0] * 1.999969, Y = o[1] * 1.999969, Z = o[2] * 1.999969;
            double fx = FLab(X / W50[0]), fy = FLab(Y / W50[1]), fz = FLab(Z / W50[2]);
            return LabToRgb(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        }
        var Lab = LabDec(a2b, o);
        return LabToRgb(Lab[0], Lab[1], Lab[2]);
    }
    /// <summary>인쇄했다 다시 본 색이 원래 색과 많이 다르면 (ΔE > 8) 인쇄로 못 내는 색</summary>
    public bool OutOfGamut(double r, double g, double b)
    {
        var c = RgbToCmyk(r, g, b); var back = CmykToRgb(c[0], c[1], c[2], c[3]);
        var L1 = RgbToLab(r, g, b); var L2 = RgbToLab(back[0], back[1], back[2]);
        return Math.Sqrt(Math.Pow(L1[0] - L2[0], 2) + Math.Pow(L1[1] - L2[1], 2) + Math.Pow(L1[2] - L2[2], 2)) > 8;
    }
}

/// <summary>33단계 표로 미리 계산 → 픽셀마다 빠르게</summary>
public sealed class IccCube
{
    public int N; public float[] Cmyk, Proof; public byte[] Gam;

    public static IccCube Build(IccTransform xf, int N = 33)
    {
        var c = new IccCube { N = N, Cmyk = new float[N * N * N * 4], Proof = new float[N * N * N * 3], Gam = new byte[N * N * N] };
        Parallel.For(0, N, ri =>
        {
            for (int gi = 0; gi < N; gi++) for (int bi = 0; bi < N; bi++)
            {
                int k = (ri * N + gi) * N + bi;
                double r = ri / (double)(N - 1), g = gi / (double)(N - 1), b = bi / (double)(N - 1);
                var cm = xf.RgbToCmyk(r, g, b);
                for (int i = 0; i < 4; i++) c.Cmyk[k * 4 + i] = (float)cm[i];
                var pr = xf.CmykToRgb(cm[0], cm[1], cm[2], cm[3]);
                for (int i = 0; i < 3; i++) c.Proof[k * 3 + i] = (float)pr[i];
                c.Gam[k] = (byte)(xf.OutOfGamut(r, g, b) ? 1 : 0);
            }
        });
        return c;
    }

    public void Lookup(float[] arr, int ch, double r, double g, double b, float[] outp)
    {
        int n1 = N - 1;
        double fr = r * n1, fg = g * n1, fb = b * n1;
        int ri = Math.Min(n1 - 1, (int)fr), gi = Math.Min(n1 - 1, (int)fg), bi = Math.Min(n1 - 1, (int)fb);
        double tr = fr - ri, tg = fg - gi, tb = fb - bi;
        for (int c = 0; c < ch; c++) outp[c] = 0;
        for (int m = 0; m < 8; m++)
        {
            int dr = (m >> 2) & 1, dg = (m >> 1) & 1, db = m & 1;
            double w = (dr != 0 ? tr : 1 - tr) * (dg != 0 ? tg : 1 - tg) * (db != 0 ? tb : 1 - tb);
            if (w == 0) continue;
            int o = (((ri + dr) * N + gi + dg) * N + bi + db) * ch;
            for (int c = 0; c < ch; c++) outp[c] += (float)(arr[o + c] * w);
        }
    }

    /// <summary>불투명 RGB 바이트(r,g,b 순 3바이트씩) → CMYK 바이트</summary>
    public byte[] ToCmyk(byte[] rgb)
    {
        int n = rgb.Length / 3;
        var o = new byte[n * 4];
        Parallel.For(0, (n + 65535) / 65536, blk =>
        {
            var tmp = new float[4];
            for (int i = blk * 65536; i < Math.Min(n, blk * 65536 + 65536); i++)
            {
                Lookup(Cmyk, 4, rgb[i * 3] / 255.0, rgb[i * 3 + 1] / 255.0, rgb[i * 3 + 2] / 255.0, tmp);
                for (int c = 0; c < 4; c++) o[i * 4 + c] = (byte)Math.Clamp(tmp[c] * 255 + 0.5f, 0, 255);
            }
        });
        return o;
    }
}
