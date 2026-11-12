// 눈동자 추적 (웹 판 FaceLandmarker 의 eyeLook* blendshape 대신): 얼굴 그물의 눈꼬리 두 점으로 눈 상자를 잡고
// MediaPipe Iris Landmark(ONNX, Apache-2.0)로 눈동자 가운데를 찾는다 → 눈꼬리 축 위치(U)·수직 위치(V).
// MediaPipe 그래프 규칙: 눈꼬리 33·133 / 362·263, 상자 = 꼬리 사이 거리 × 2.3 정사각형, 꼬리 축만큼 돌림, 64×64 [0,1] RGB NCHW,
// 그림 오른쪽 눈(362·263)은 좌우를 뒤집어 넣음 (모델은 왼쪽 눈 모양으로 배움).
using ComicPadNative.Modules.AI;

namespace ComicPadNative.Modules.Vision;

public sealed class IrisTracker
{
    public const string File = "iris_landmark.onnx";
    readonly AiModel m;
    public IrisTracker(AiModel model) { m = model; Blaze.Session(m, File); }

    /// <summary>시험용: 모델에 넣은 64×64 텐서와 눈동자 출력</summary>
    public Action<float[], float[]> Debug;

    public sealed class Eye {public (double x, double y) C0, C1, Iris; public double U, V; }

    /// <summary>눈 하나: c0 = 그림 왼쪽 꼬리, c1 = 그림 오른쪽 꼬리, flip = 오른쪽 눈</summary>
    public Eye One(VImg img, (double x, double y) c0, (double x, double y) c1, bool flip)
    {
        double dx = c1.x - c0.x, dy = c1.y - c0.y, w = Math.Sqrt(dx * dx + dy * dy);
        if (w < 3) return null;
        double cx = (c0.x + c1.x) / 2, cy = (c0.y + c1.y) / 2;
        var M = Affine.Roi(cx, cy, w * 2.3, Math.Atan2(dy, dx), 64);
        if (flip) M = new Affine(-M.A, M.B, M.C + 64 * M.A, -M.D, M.E, M.F + 64 * M.D);   // 텐서 x → 64 − x
        var input = Blaze.Sample(img, M, 64, true);
        var o = Blaze.Run(Blaze.Session(m, File), input, new[] { 1, 3, 64, 64 });
        if (!o.TryGetValue("output_iris", out var iris) || iris.Length < 3) return null;
        Debug?.Invoke(input, iris);
        var p = M.Apply(iris[0], iris[1]);
        double u = ((p.x - c0.x) * dx + (p.y - c0.y) * dy) / (w * w);
        double v = ((p.x - cx) * -dy + (p.y - cy) * dx) / (w * w);
        return new Eye { C0 = c0, C1 = c1, Iris = p, U = u, V = v };
    }

    /// <summary>두 눈 평균 (U, V). 못 찾으면 null</summary>
    public (double u, double v, Eye a, Eye b)? Gaze(VImg img, FaceMesh f)
    {
        (double, double) P(int i) => (f.P[i].x, f.P[i].y);
        var a = One(img, P(33), P(133), false);
        var b = One(img, P(362), P(263), true);
        if (a == null || b == null) return null;
        return ((a.U + b.U) / 2, (a.V + b.V) / 2, a, b);
    }

    /// <summary>잰 얼굴 값에 눈동자를 더함 (모델이 없거나 실패하면 그대로)</summary>
    public FaceMeasure With(FaceMeasure me, VImg img, FaceMesh f)
    {
        var g = Gaze(img, f);
        return g is { } x && x.u > -0.3 && x.u < 1.3 ? me with { GazeU = x.u, GazeV = x.v, HasGaze = true } : me;
    }
}
