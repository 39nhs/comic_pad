// 얼굴 추적 (웹 판 vtuber/track.js 의 FaceLandmarker 대신): BlazeFace 로 얼굴을 찾고 FaceMesh(468점)로 얼굴 그물을 얻는다.
// 다음 장부터는 앞 장 그물로 상자를 잡아 (MediaPipe 추적 방식) 얼굴 찾기를 건너뛴다.
// 표정(깜빡임·입·웃음·오므림·눈썹)과 고개 방향은 그물 모양에서 계산 → MediaPipe blendshape 이름으로 VRig.FaceToState 에 넘긴다.
// 처음 몇 장으로 "평소 얼굴"을 맞춰 두고 그 차이로 움직인다 (사람마다 눈·입 크기가 달라서).
using ComicPadNative.Modules.AI;
using ComicPadNative.Modules.Scene3D;

namespace ComicPadNative.Modules.Vision;

public sealed class FaceMesh
{
    public double Score;                    // 0~1
    public (double x, double y, double z)[] P;   // 그림 좌표 (z 는 그림 픽셀 단위, 작을수록 카메라 쪽)
}

/// <summary>그물에서 잰 값 (사람·거리와 상관없게 얼굴 크기로 나눔)</summary>
public readonly record struct FaceMeasure(double EarL, double EarR, double MouthOpen, double MouthWidth, double BrowL, double BrowR, double Yaw, double Pitch, double Roll)
{
    /// <summary>눈동자 위치 (Iris.cs): U = 눈꼬리→눈머리 축 위 0~1 (0.5 가운데, 그림 오른쪽이 +), V = 그 축에 수직(아래 +), 눈 너비로 나눔. HasGaze 가 거짓이면 없음</summary>
    public double GazeU { get; init; }
    public double GazeV { get; init; }
    public bool HasGaze { get; init; }

    // 점 번호 (MediaPipe FaceMesh 468, "오른쪽" = 그 사람의 오른쪽 = 그림 왼쪽)
    public static FaceMeasure From(FaceMesh f)
    {
        var P = f.P;
        double D(int a, int b) { var p = P[a]; var q = P[b]; return Math.Sqrt((p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y) + (p.z - q.z) * (p.z - q.z)); }
        double faceW = D(234, 454), faceH = D(10, 152);
        if (faceW < 1e-6 || faceH < 1e-6) return default;
        double earR = (D(159, 145) + D(158, 153) + D(160, 144)) / 3 / Math.Max(1e-6, D(33, 133));
        double earL = (D(386, 374) + D(385, 380) + D(387, 373)) / 3 / Math.Max(1e-6, D(362, 263));
        double open = D(13, 14) / faceH;
        double width = D(61, 291) / faceW;
        double browR = (D(105, 159) + D(66, 158)) / 2 / faceH, browL = (D(334, 386) + D(296, 385)) / 2 / faceH;
        // 고개: 얼굴 가로축(234→454)·세로축(152→10) 으로 회전 틀 (카메라 공간: x 오른쪽, y 위, z 카메라 쪽)
        double[] V(int a, int b) => new[] { P[b].x - P[a].x, -(P[b].y - P[a].y), -(P[b].z - P[a].z) };
        var X = Norm(V(234, 454)); var Y = V(152, 10);
        double d = Dot(Y, X); Y = Norm(new[] { Y[0] - d * X[0], Y[1] - d * X[1], Y[2] - d * X[2] });
        var Z = new[] { X[1] * Y[2] - X[2] * Y[1], X[2] * Y[0] - X[0] * Y[2], X[0] * Y[1] - X[1] * Y[0] };
        double yaw = Math.Atan2(Z[0], Z[2]);                 // + = 얼굴이 그림 오른쪽을 봄
        double pitch = Math.Asin(Math.Clamp(Z[1], -1, 1));    // + = 위를 봄
        double roll = Math.Atan2(-X[1], X[0]);               // + = 그림에서 시계 방향으로 기욺 (y 아래 기준)
        return new FaceMeasure(earL, earR, open, width, browL, browR, yaw, pitch, roll);
    }
    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    static double[] Norm(double[] a) { double l = Math.Sqrt(Dot(a, a)); return l < 1e-9 ? new double[] { 1, 0, 0 } : new[] { a[0] / l, a[1] / l, a[2] / l }; }
}

/// <summary>평소 얼굴 기준값 + 잰 값 → 표정 (MediaPipe blendshape 이름) + 고개</summary>
public sealed class FaceCalib
{
    public FaceMeasure Base = new(0.28, 0.28, 0.0, 0.40, 0.11, 0.11, 0, 0, 0);
    int n; FaceMeasure sum;
    public bool Ready => n >= Frames;
    public int Frames = 12;
    public void Reset() { n = 0; sum = default; gn = 0; gsu = gsv = 0; }
    // 눈동자 기준 (앞 몇 장 = 카메라를 본다고 봄)
    public double GazeU0 = 0.5, GazeV0 = 0.0;
    int gn; double gsu, gsv;
    /// <summary>앞 몇 장은 평균을 기준으로 (입은 다물고 앞을 본다고 봄)</summary>
    public void Feed(FaceMeasure m)
    {
        if (m.HasGaze && gn < Frames)
        {
            gsu += m.GazeU; gsv += m.GazeV; gn++;
            if (gn == Frames) { GazeU0 = gsu / gn; GazeV0 = gsv / gn; }
        }
        if (Ready) return;
        sum = new(sum.EarL + m.EarL, sum.EarR + m.EarR, sum.MouthOpen + m.MouthOpen, sum.MouthWidth + m.MouthWidth, sum.BrowL + m.BrowL, sum.BrowR + m.BrowR, sum.Yaw + m.Yaw, sum.Pitch + m.Pitch, sum.Roll + m.Roll);
        n++;
        if (Ready) Base = new(sum.EarL / n, sum.EarR / n, sum.MouthOpen / n, sum.MouthWidth / n, sum.BrowL / n, sum.BrowR / n, sum.Yaw / n, sum.Pitch / n, sum.Roll / n);
    }

    static double C01(double v) => Math.Clamp(v, 0, 1);

    /// <summary>blendshape 값 (그 사람 기준 왼쪽·오른쪽)</summary>
    public Dictionary<string, double> Shapes(FaceMeasure m)
    {
        var b = Base;
        double blink(double ear, double e0) => C01((e0 * 0.8 - ear) / Math.Max(1e-4, e0 * 0.55));
        double open = C01((m.MouthOpen - b.MouthOpen - 0.012) / 0.16);
        double wid = (m.MouthWidth - b.MouthWidth) / Math.Max(1e-4, b.MouthWidth);
        double smile = C01(wid / 0.16), funnel = C01(-wid / 0.16) * (open > 0.05 ? 1 : 0.7);
        double browR = (m.BrowR - b.BrowR) / Math.Max(1e-4, b.BrowR), browL = (m.BrowL - b.BrowL) / Math.Max(1e-4, b.BrowL);
        double up = C01((browL + browR) / 2 / 0.18), downL = C01(-browL / 0.15), downR = C01(-browR / 0.15);
        var o = new Dictionary<string, double>
        {
            ["eyeBlinkLeft"] = blink(m.EarL, b.EarL), ["eyeBlinkRight"] = blink(m.EarR, b.EarR),
            ["jawOpen"] = open, ["mouthSmileLeft"] = smile, ["mouthSmileRight"] = smile,
            ["mouthFunnel"] = funnel, ["mouthPucker"] = funnel,
            ["browInnerUp"] = up, ["browDownLeft"] = downL, ["browDownRight"] = downR,
        };
        if (m.HasGaze)
        {
            // 눈동자: 눈 너비의 ±12% 가로·±7% 세로면 끝까지. MediaPipe 이름으로 (그림 오른쪽 = 그 사람 왼쪽 = LookOutLeft·LookInRight)
            double gx = Math.Clamp((m.GazeU - GazeU0) / 0.12, -1, 1), gy = Math.Clamp((m.GazeV - GazeV0) / 0.07, -1, 1);
            o["eyeLookOutLeft"] = o["eyeLookInRight"] = Math.Max(gx, 0); o["eyeLookInLeft"] = o["eyeLookOutRight"] = Math.Max(-gx, 0);
            o["eyeLookDownLeft"] = o["eyeLookDownRight"] = Math.Max(gy, 0); o["eyeLookUpLeft"] = o["eyeLookUpRight"] = Math.Max(-gy, 0);
        }
        return o;
    }

    /// <summary>표정 + 고개 → 버튜버 상태. 고개 30° = 끝까지 (웹 판과 같음). mirror: 거울처럼</summary>
    public VState State(FaceMeasure m, bool mirror)
    {
        var st = VRig.FaceToState(Shapes(m), null, mirror);
        double D = Math.PI / 6;
        double yaw = m.Yaw - Base.Yaw, pitch = m.Pitch - Base.Pitch, roll = m.Roll - Base.Roll;
        // 카메라 그림은 거울이 아님: 거울이면 좌우를 바꿔 "내가 오른쪽을 보면 캐릭터도 화면 오른쪽"
        st.Yaw = Math.Clamp((mirror ? -yaw : yaw) / D, -1, 1);
        st.Pitch = Math.Clamp(-pitch / D, -1, 1);       // 상태: - 가 위
        st.Roll = Math.Clamp((mirror ? -roll : roll) / D, -1, 1);
        return st;
    }
}

public sealed class FaceTracker
{
    readonly AiModel m;
    readonly float[,] anchors;
    FaceMesh last;
    public double MinScore = 0.5;

    public FaceTracker(AiModel model)
    {
        m = model;
        anchors = Blaze.LoadAnchors(Path.Combine(m.Dir, "anchors.csv"));
        Blaze.Session(m, "blaze_face_short_range.onnx"); Blaze.Session(m, "face_mesh_Nx3x192x192_post.onnx");
    }

    public void Lost() => last = null;

    /// <summary>한 장 → 얼굴 그물 (없으면 null). refine: 찾은 그물로 상자를 다시 잡아 몇 번 더 (사진용)</summary>
    public FaceMesh Process(VImg img, int refine = 0)
    {
        FaceMesh f = null;
        if (last != null) { f = MeshFrom(img, last); if (f == null || f.Score < MinScore) f = null; }
        if (f == null)
        {
            var d = Detect(img);
            if (d == null) { last = null; return null; }
            // 눈 두 점으로 기울기, 상자 1.5배 (MediaPipe: 얼굴 상자 → 그물 상자)
            double rot = Math.Atan2(d.Kp[1].y - d.Kp[0].y, d.Kp[1].x - d.Kp[0].x);
            f = Mesh(img, d.Cx, d.Cy, Math.Max(d.W, d.H) * 1.5, rot);
            if (f == null || f.Score < MinScore) { last = null; return null; }
            refine = Math.Max(refine, 1);
        }
        for (int i = 0; i < refine; i++) { var g = MeshFrom(img, f); if (g != null && g.Score >= MinScore) f = g; }
        last = f;
        return f;
    }

    public Detection Detect(VImg img)
    {
        var M = Affine.Letterbox(img.W, img.H, 128);
        var o = Blaze.Run(Blaze.Session(m, "blaze_face_short_range.onnx"), Blaze.Sample(img, M, 128, false, 2, -1), new[] { 1, 128, 128, 3 });
        var ds = Blaze.Decode(o["regressors"], o["classificators"], 16, anchors, 128, M, 6, 0.4, 4);
        // 가장 큰 얼굴 (방송하는 사람은 화면 앞쪽)
        return ds.OrderByDescending(d => d.W * d.H * d.Score).FirstOrDefault();
    }

    FaceMesh MeshFrom(VImg img, FaceMesh f)
    {
        var a = f.P[33]; var b = f.P[263];
        double rot = Math.Atan2(b.y - a.y, b.x - a.x);
        double x0 = f.P.Min(p => p.x), x1 = f.P.Max(p => p.x), y0 = f.P.Min(p => p.y), y1 = f.P.Max(p => p.y);
        return Mesh(img, (x0 + x1) / 2, (y0 + y1) / 2, Math.Max(x1 - x0, y1 - y0) * 1.5, rot);
    }

    const int K = 16;     // 그물 모델은 정수 좌표를 돌려줘서 16배로 받아 정밀도를 지킴
    FaceMesh Mesh(VImg img, double cx, double cy, double size, double rot)
    {
        if (size < 16) return null;
        var M2 = Affine.Roi(cx, cy, size, rot, 192);
        var s = Blaze.Session(m, "face_mesh_Nx3x192x192_post.onnx");
        var names = s.InputMetadata.Keys.ToList();
        var o = Blaze.Run(s, Blaze.Sample(img, M2, 192, true), new[] { 1, 3, 192, 192 },
            (names[1], new[] { 0 }), (names[2], new[] { 0 }), (names[3], new[] { 192 * K }), (names[4], new[] { 192 * K }));
        var lm = o["final_landmarks"]; double score = Blaze.Sigmoid(o["score"][0]);
        var P = new (double, double, double)[468];
        double zs = M2.Scale / K;
        for (int j = 0; j < 468; j++) { var (x, y) = M2.Apply(lm[j * 3] / K, lm[j * 3 + 1] / K); P[j] = (x, y, lm[j * 3 + 2] * zs); }
        return new FaceMesh { Score = score, P = P };
    }
}
