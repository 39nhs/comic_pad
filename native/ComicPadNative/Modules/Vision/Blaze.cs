// 사진·영상에서 사람 찾기 공용 부품 (MediaPipe Blaze 계열 ONNX 모델용):
//  - 그림(ARGB) → 회전·크기 맞춘 텐서 (쌍선형), 2×3 아핀 변환
//  - SSD 앵커 + 상자·점 풀기 + 겹친 것 빼기
// 모델 입력 규칙은 MediaPipe 그래프와 같게: 사람·얼굴 찾기는 [-1,1], 손바닥 찾기·랜드마크는 [0,1].
using System.Drawing;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ComicPadNative.Modules.AI;

namespace ComicPadNative.Modules.Vision;

/// <summary>ARGB 그림 (알파는 무시, 바탕은 그대로)</summary>
public sealed class VImg
{
    public readonly uint[] Px; public readonly int W, H;
    public VImg(uint[] px, int w, int h) { Px = px; W = w; H = h; }
    public static VImg FromBitmap(Bitmap b) => new(AiRuntime.ReadArgb(b), b.Width, b.Height);
    public static VImg FromFile(string path)
    {
        using var raw = new Bitmap(path);
        using var b = new Bitmap(raw.Width, raw.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b)) { g.Clear(Color.White); g.DrawImage(raw, 0, 0, raw.Width, raw.Height); }
        return FromBitmap(b);
    }
}

/// <summary>2×3 아핀: x' = A x + B y + C, y' = D x + E y + F</summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public (double x, double y) Apply(double x, double y) => (A * x + B * y + C, D * x + E * y + F);
    public double Scale => Math.Sqrt(Math.Abs(A * E - B * D));

    /// <summary>텐서(n×n) → 그림: 긴 쪽에 맞추고 가운데 (남는 곳은 바깥 = 0)</summary>
    public static Affine Letterbox(int W, int H, int n)
    {
        double m = Math.Max(W, H), s = m / n;
        return new(s, 0, (W - m) / 2, 0, s, (H - m) / 2);
    }

    /// <summary>텐서(n×n) → 그림: 중심 (cx,cy), 한 변 size 픽셀, rot 라디안(그림 좌표에서 시계 방향) 돌린 정사각형</summary>
    public static Affine Roi(double cx, double cy, double size, double rot, int n)
    {
        double s = size / n, c = Math.Cos(rot) * s, si = Math.Sin(rot) * s, h = n / 2.0;
        return new(c, -si, cx - (c * h - si * h), si, c, cy - (si * h + c * h));
    }
}

public sealed class Detection
{
    public double Score, Cx, Cy, W, H;
    public (double x, double y)[] Kp;
}

public static class Blaze
{
    /// <summary>그림을 아핀으로 n×n 텐서에 옮김. 값 = 채널/255 * mul + add. nchw=false 면 [1,n,n,3]</summary>
    public static float[] Sample(VImg img, Affine M, int n, bool nchw, float mul = 1, float add = 0)
    {
        var o = new float[3 * n * n];
        int W = img.W, H = img.H; var px = img.Px; int nn = n * n;
        float k = mul / 255f;
        Parallel.For(0, n, ty =>
        {
            for (int tx = 0; tx < n; tx++)
            {
                double sx = M.A * (tx + 0.5) + M.B * (ty + 0.5) + M.C - 0.5, sy = M.D * (tx + 0.5) + M.E * (ty + 0.5) + M.F - 0.5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                float fx = (float)(sx - x0), fy = (float)(sy - y0);
                float r = 0, g = 0, b = 0;
                void Acc(int x, int y, float w)
                {
                    if (w == 0 || x < 0 || y < 0 || x >= W || y >= H) return;
                    uint c = px[y * W + x];
                    r += ((c >> 16) & 255) * w; g += ((c >> 8) & 255) * w; b += (c & 255) * w;
                }
                Acc(x0, y0, (1 - fx) * (1 - fy)); Acc(x0 + 1, y0, fx * (1 - fy)); Acc(x0, y0 + 1, (1 - fx) * fy); Acc(x0 + 1, y0 + 1, fx * fy);
                // 바깥은 검정(0) — MediaPipe 와 같음 (값으로는 add)
                int i = ty * n + tx;
                if (nchw) { o[i] = r * k + add; o[nn + i] = g * k + add; o[2 * nn + i] = b * k + add; }
                else { o[i * 3] = r * k + add; o[i * 3 + 1] = g * k + add; o[i * 3 + 2] = b * k + add; }
            }
        });
        return o;
    }

    public static float[,] LoadAnchors(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToArray();
        var a = new float[lines.Length, 4];
        for (int i = 0; i < lines.Length; i++)
        {
            var v = lines[i].Split(',');
            for (int j = 0; j < 4; j++) a[i, j] = float.Parse(v[j], CultureInfo.InvariantCulture);
        }
        return a;
    }

    static double Sig(double v) => 1 / (1 + Math.Exp(-Math.Clamp(v, -100, 100)));

    /// <summary>
    /// SSD 출력 풀기. raw: [N, stride] (cx, cy, w, h, 점들…, 텐서 픽셀 단위, 앵커 중심 기준), scores: [N] 로짓.
    /// 점수 높은 순으로 겹치는(IoU&gt;0.3) 것은 빼고 최대 maxOut 개, 그림 좌표로.
    /// </summary>
    public static List<Detection> Decode(float[] raw, float[] scores, int stride, float[,] anchors, int inSize, Affine M, int nKp, double minScore, int maxOut)
    {
        int N = anchors.GetLength(0);
        var idx = Enumerable.Range(0, N).Where(i => Sig(scores[i]) >= minScore).OrderByDescending(i => scores[i]).Take(200).ToList();
        var outL = new List<Detection>();
        foreach (int i in idx)
        {
            int o = i * stride;
            double ax = anchors[i, 0] * inSize, ay = anchors[i, 1] * inSize;
            var (cx, cy) = M.Apply(ax + raw[o], ay + raw[o + 1]);
            double s = M.Scale;
            var d = new Detection { Score = Sig(scores[i]), Cx = cx, Cy = cy, W = raw[o + 2] * s, H = raw[o + 3] * s, Kp = new (double, double)[nKp] };
            for (int k = 0; k < nKp; k++) d.Kp[k] = M.Apply(ax + raw[o + 4 + 2 * k], ay + raw[o + 5 + 2 * k]);
            if (d.W <= 0 || d.H <= 0) continue;
            if (outL.Any(p => IoU(p, d) > 0.3)) continue;
            outL.Add(d);
            if (outL.Count >= maxOut) break;
        }
        return outL;
    }

    static double IoU(Detection a, Detection b)
    {
        double x0 = Math.Max(a.Cx - a.W / 2, b.Cx - b.W / 2), y0 = Math.Max(a.Cy - a.H / 2, b.Cy - b.H / 2);
        double x1 = Math.Min(a.Cx + a.W / 2, b.Cx + b.W / 2), y1 = Math.Min(a.Cy + a.H / 2, b.Cy + b.H / 2);
        double inter = Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
        return inter / (a.W * a.H + b.W * b.H - inter + 1e-9);
    }

    // ---- ONNX 실행 (작은 모델들, 여러 스레드에서 부를 수 있게 세션 만들기는 잠금) ----
    static readonly object gate = new();
    public static InferenceSession Session(AiModel m, string file) { lock (gate) return AiRuntime.Session(m, file); }

    /// <summary>입력 하나(이름은 첫 입력) → 출력 이름별 float 배열</summary>
    public static Dictionary<string, float[]> Run(InferenceSession s, float[] data, int[] dims, params (string name, int[] v)[] intInputs)
    {
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(s.InputMetadata.Keys.First(), new DenseTensor<float>(data, dims)) };
        foreach (var (name, v) in intInputs) inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(v, new[] { 1, 1 })));
        using var r = s.Run(inputs);
        var o = new Dictionary<string, float[]>();
        foreach (var x in r)
        {
            if (x.Value is Tensor<float> tf) o[x.Name] = tf.ToArray();
            else if (x.Value is Tensor<int> ti) o[x.Name] = ti.ToArray().Select(v => (float)v).ToArray();
        }
        return o;
    }

    public static double Sigmoid(double v) => Sig(v);
}
