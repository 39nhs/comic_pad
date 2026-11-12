// 사진 → 데생 인형 자세 / 손 모양 (웹 판 app/js/ai/pose.js 와 같은 일, 모델은 BlazePose·BlazeHand ONNX).
// 사진은 이 PC 밖으로 나가지 않는다. 결과 랜드마크는 MediaPipe 와 같은 형식이라 Bvh.PoseFromLandmarks·HandFromLandmarks 로 그대로 넘긴다.
using ComicPadNative.Modules.AI;
using ComicPadNative.Modules.Scene3D;
using Pose = ComicPadNative.Modules.Scene3D.Engine3D.Pose;

namespace ComicPadNative.Modules.Vision;

public sealed class BodyResult
{
    public double Score, Flag;                     // 사람 찾기 점수, 랜드마크 "사람 있음" 점수 (0~1)
    public (double x, double y, double vis)[] Image;  // 그림 좌표 33점
    public List<Bvh.Landmark> World;               // 미터, 엉덩이 중심 기준 (y 아래, MediaPipe 와 같음)
}

public sealed class HandResult
{
    public double Score, Flag, Right;              // Right: 오른손일 확률
    public (double x, double y)[] Image;           // 21점
    public List<Bvh.Landmark> World;
}

public static class PosePhoto
{
    // ---------------------------------------------------------------- 몸
    public static BodyResult DetectBody(AiModel m, VImg img)
    {
        var det = Blaze.Session(m, "pose_detection.onnx");
        var lm = Blaze.Session(m, "pose_landmarks_detector_full.onnx");
        var anchors = Blaze.LoadAnchors(Path.Combine(m.Dir, "anchors.csv"));
        var M = Affine.Letterbox(img.W, img.H, 224);
        var x = Blaze.Sample(img, M, 224, false, 2, -1);
        var o = Blaze.Run(det, x, new[] { 1, 224, 224, 3 });
        var (boxes, scores) = Pick(o, 12);
        var ds = Blaze.Decode(boxes, scores, 12, anchors, 224, M, 4, 0.3, 3);
        if (ds.Count == 0) return null;
        BodyResult best = null;
        foreach (var d in ds)
        {
            // 0 = 엉덩이 가운데, 1 = 온몸 크기·방향 점 (MediaPipe AlignmentPointsRects: 1.25배 정사각형, 엉덩이→머리 위가 위쪽)
            var r = BodyAt(lm, img, d.Kp[0], d.Kp[1]);
            if (r == null) continue;
            r.Score = d.Score;
            // 사진은 MediaPipe IMAGE 모드처럼 한 번만 (랜드마크로 다시 자르면 오히려 흔들림). 사람 있음 점수가 낮을 때만 정렬 점(33·34)으로 다시
            if (r.Flag < 0.5)
            {
                var r2 = BodyAt(lm, img, (r.Image[33].x, r.Image[33].y), (r.Image[34].x, r.Image[34].y));
                if (r2 != null && r2.Flag > r.Flag) { r2.Score = d.Score; r = r2; }
            }
            if (best == null || r.Flag > best.Flag) best = r;
        }
        if (best != null) best.Image = best.Image.Take(33).ToArray();
        return best;
    }

    static (float[] boxes, float[] scores) Pick(Dictionary<string, float[]> o, int stride)
    {
        var a = o.Values.ToList();
        return a[0].Length > a[1].Length ? (a[0], a[1]) : (a[1], a[0]);
    }

    static BodyResult BodyAt(Microsoft.ML.OnnxRuntime.InferenceSession lm, VImg img, (double x, double y) k0, (double x, double y) k1)
    {
        double dx = k1.x - k0.x, dy = k1.y - k0.y, r = Math.Sqrt(dx * dx + dy * dy);
        if (r < 4) return null;
        double rot = Math.Atan2(dy, dx) + Math.PI / 2, size = 2 * r * 1.25;
        var M2 = Affine.Roi(k0.x, k0.y, size, rot, 256);
        var o = Blaze.Run(lm, Blaze.Sample(img, M2, 256, false), new[] { 1, 256, 256, 3 });
        var L = o["Identity"]; var flagRaw = o["Identity_1"][0]; var Wl = o["Identity_4"];
        var res = new BodyResult { Flag = Clamp01(flagRaw), Image = new (double, double, double)[39], World = new() };
        for (int j = 0; j < 39; j++)
        {
            var (ix, iy) = M2.Apply(L[j * 5], L[j * 5 + 1]);
            res.Image[j] = (ix, iy, Blaze.Sigmoid(L[j * 5 + 3]));
        }
        // 세계 좌표는 잘라 낸 상자 방향 기준 → 그림 방향으로 되돌림 (MediaPipe WorldLandmarkProjection 과 같음)
        double c = Math.Cos(rot), s = Math.Sin(rot);
        for (int j = 0; j < 33; j++)
        {
            double wx = Wl[j * 3], wy = Wl[j * 3 + 1], wz = Wl[j * 3 + 2];
            res.World.Add(new Bvh.Landmark(c * wx - s * wy, s * wx + c * wy, wz, res.Image[j].vis));
        }
        return res;
    }

    // ---------------------------------------------------------------- 손
    public static HandResult DetectHand(AiModel m, VImg img)
    {
        var det = Blaze.Session(m, "hand_detector.onnx");
        var lm = Blaze.Session(m, "hand_landmarks_detector.onnx");
        var anchors = Blaze.LoadAnchors(Path.Combine(m.Dir, "anchors.csv"));
        var M = Affine.Letterbox(img.W, img.H, 192);
        var o = Blaze.Run(det, Blaze.Sample(img, M, 192, false), new[] { 1, 192, 192, 3 });
        var (boxes, scores) = Pick(o, 18);
        var ds = Blaze.Decode(boxes, scores, 18, anchors, 192, M, 7, 0.02, 5);
        HandResult best = null;
        foreach (var d in ds)
        {
            // 손바닥 상자 → 손 상자: 손목(0)→가운뎃손가락 뿌리(2) 가 위쪽, 2.6배, 손가락 쪽으로 0.5 칸 (MediaPipe palm→hand rect)
            double dx = d.Kp[2].x - d.Kp[0].x, dy = d.Kp[2].y - d.Kp[0].y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) continue;
            double s0 = Math.Max(d.W, d.H), rot = Math.Atan2(dy, dx) + Math.PI / 2;
            var r = HandAt(lm, img, d.Cx + dx / len * 0.5 * s0, d.Cy + dy / len * 0.5 * s0, s0 * 2.6, rot);
            if (r == null) continue;
            r.Score = d.Score;
            var r2 = Refine(lm, img, r);
            if (r2 != null && r2.Flag > r.Flag) { r2.Score = d.Score; r = r2; }
            if (best == null || r.Flag > best.Flag) best = r;
        }
        return best;
    }

    /// <summary>찾은 손 랜드마크로 상자를 다시 잡아 한 번 더 (MediaPipe 손 추적: 손목→가운뎃손가락 뿌리 방향, 2배, 0.1 위로)</summary>
    static HandResult Refine(Microsoft.ML.OnnxRuntime.InferenceSession lm, VImg img, HandResult r)
    {
        var w = r.Image[0]; var mid = r.Image[9];
        double rot = Math.Atan2(mid.y - w.y, mid.x - w.x) + Math.PI / 2, c = Math.Cos(-rot), s = Math.Sin(-rot);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (px, py) in r.Image) { double u = c * px - s * py, v = s * px + c * py; x0 = Math.Min(x0, u); x1 = Math.Max(x1, u); y0 = Math.Min(y0, v); y1 = Math.Max(y1, v); }
        double cu = (x0 + x1) / 2, cv = (y0 + y1) / 2 - 0.1 * (y1 - y0), size = Math.Max(x1 - x0, y1 - y0) * 2.0;
        double cx = Math.Cos(rot) * cu - Math.Sin(rot) * cv, cy = Math.Sin(rot) * cu + Math.Cos(rot) * cv;
        return HandAt(lm, img, cx, cy, size, rot);
    }

    static HandResult HandAt(Microsoft.ML.OnnxRuntime.InferenceSession lm, VImg img, double cx, double cy, double size, double rot)
    {
        if (size < 8) return null;
        var M2 = Affine.Roi(cx, cy, size, rot, 224);
        var o = Blaze.Run(lm, Blaze.Sample(img, M2, 224, false), new[] { 1, 224, 224, 3 });
        var L = o["Identity"]; var Wl = o["Identity_3"];
        var res = new HandResult { Flag = Clamp01(o["Identity_1"][0]), Right = Clamp01(o["Identity_2"][0]), Image = new (double, double)[21], World = new() };
        for (int j = 0; j < 21; j++) res.Image[j] = M2.Apply(L[j * 3], L[j * 3 + 1]);
        double c = Math.Cos(rot), s = Math.Sin(rot);
        for (int j = 0; j < 21; j++) { double wx = Wl[j * 3], wy = Wl[j * 3 + 1]; res.World.Add(new Bvh.Landmark(c * wx - s * wy, s * wx + c * wy, Wl[j * 3 + 2])); }
        return res;
    }
    // 몸·손 랜드마크 모델은 "있음" 출력에 이미 시그모이드가 들어 있음 (0~1). 아니면 로짓으로 보고 바꿈
    static double Clamp01(double v) => v < 0 || v > 1 ? Blaze.Sigmoid(v) : v;

    // ---------------------------------------------------------------- 3D 소재 창에서 부르기
    /// <summary>사진 고르기 → 포즈 (kind "pose" | "hand"). 모델이 없으면 받을지 묻는다</summary>
    public static Pose FromPhoto(string kind, IWin32Window owner)
    {
        var m = VisionModels.Get(kind == "pose" ? VisionModels.Pose : VisionModels.Hand);
        if (!VisionUi.EnsureModel(m, owner, kind == "pose" ? "사진에서 자세" : "사진에서 손 모양")) return null;
        if (!AiFeatures.LicenseGate(m)) return null;
        using var dlg = new OpenFileDialog { Filter = "그림 (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|모든 파일|*.*" };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
        VImg img;
        try { img = VImg.FromFile(dlg.FileName); }
        catch { MessageBox.Show(owner, "그림 파일을 열지 못했습니다", "사진에서 자세"); return null; }
        try
        {
            var pose = VisionUi.RunBusy(owner, kind == "pose" ? "사진에서 자세 읽는 중" : "사진에서 손 모양 읽는 중", () =>
            {
                if (kind == "pose")
                {
                    var r = DetectBody(m, img);
                    if (r == null || r.Flag < 0.3) throw new InvalidOperationException("사진에서 사람을 찾지 못했습니다 (몸 전체가 보이는 사진이 좋습니다)");
                    return Bvh.PoseFromLandmarks(r.World);
                }
                var h = DetectHand(m, img);
                if (h == null || h.Flag < 0.3) throw new InvalidOperationException("사진에서 손을 찾지 못했습니다 (손이 크게 보이는 사진이 좋습니다)");
                return Bvh.HandFromLandmarks(h.World);
            });
            return pose;
        }
        catch (Exception e) { MessageBox.Show(owner, e.Message, "사진에서 " + (kind == "pose" ? "자세" : "손 모양")); return null; }
    }
}
