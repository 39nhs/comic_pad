// 시험: 눈동자 추적 (MediaPipe Iris ONNX) — 모델 등록, 계산식(눈동자 → lookX/lookY), 사진이 있으면 실제 추론 (좌우 뒤집으면 값도 뒤집힘)
// 사진: COMICPAD_GAPS_FACE_IMG (없으면 건너뜀). 모델은 COMICPAD_DATA/models 에 있을 때만 (시험은 받지 않음).
using ComicPadNative.Modules.Vision;

namespace ComicPadNative.Modules.Gaps;

public static partial class GapsTest
{
    static void TestIris()
    {
        var m = VisionModels.Get(VisionModels.Iris);
        Ok(m != null && m.License == "Apache-2.0" && m.Files.Count == 1 && m.Files[0].size == 2645189 && m.Files[0].sha256 == "3eb985960b80e6069812c2f20682b7a4194d80cf5a3a918ddb8d0b3ca72a6432" && m.Files[0].url.Contains("/e1372d7285e060a10c233e0e88ca5a97ccaee99c/"),
            $"눈동자 모델 등록: {m?.Name}, {m?.Files[0].size}B, 커밋 고정 주소");
        // 계산식: 기준(카메라 보기)에서 눈 너비 12% 오른쪽 → lookX 끝까지, 거울이면 반대
        var c = new FaceCalib();
        var baseM = new FaceMeasure(0.3, 0.3, 0.01, 0.4, 0.11, 0.11, 0, 0, 0) { GazeU = 0.52, GazeV = 0.01, HasGaze = true };
        for (int i = 0; i < c.Frames; i++) c.Feed(baseM);
        var right = baseM with { GazeU = 0.52 + 0.12 };
        var down = baseM with { GazeV = 0.01 + 0.035 };
        var s1 = c.State(right, false); var s2 = c.State(right, true); var s3 = c.State(down, false); var s0 = c.State(baseM, false);
        Ok(Math.Abs(s1.LookX - 1) < 1e-6 && Math.Abs(s2.LookX + 1) < 1e-6 && Math.Abs(s3.LookY - 0.5) < 1e-6 && Math.Abs(s0.LookX) < 1e-9 && Math.Abs(s0.LookY) < 1e-9,
            $"눈동자 → 상태: 오른쪽 {s1.LookX:0.00} (거울 {s2.LookX:0.00}), 아래 반 {s3.LookY:0.00}, 기준 {s0.LookX:0.00}/{s0.LookY:0.00}");

        var face = VisionModels.Get(VisionModels.Face);
        if (!m.Installed || face == null || !face.Installed) { Note("얼굴·눈동자 모델이 COMICPAD_DATA 에 없어 실제 추론 시험은 건너뜀"); return; }
        var it = new IrisTracker(m);
        string imgPath = Environment.GetEnvironmentVariable("COMICPAD_GAPS_FACE_IMG");
        if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath)) { Note("COMICPAD_GAPS_FACE_IMG 가 없어 사진 추론은 건너뜀"); return; }
        var img0 = VImg.FromFile(imgPath);
        // 얼굴 하나만 남게 잘라냄 (여러 얼굴이면 뒤집었을 때 다른 얼굴을 고를 수 있음)
        var tr0 = new FaceTracker(face);
        var f0 = tr0.Process(img0, 2);
        if (f0 == null) { Ok(false, "사진에서 얼굴을 못 찾음"); return; }
        double fx0 = f0.P.Min(p => p.x), fx1 = f0.P.Max(p => p.x), fy0 = f0.P.Min(p => p.y), fy1 = f0.P.Max(p => p.y), fs = Math.Max(fx1 - fx0, fy1 - fy0);
        int cx0 = Math.Max(0, (int)((fx0 + fx1) / 2 - fs)), cy0 = Math.Max(0, (int)((fy0 + fy1) / 2 - fs));
        int cw = Math.Min(img0.W - cx0, (int)(fs * 2)), ch = Math.Min(img0.H - cy0, (int)(fs * 2));
        var cp = new uint[cw * ch];
        for (int y = 0; y < ch; y++) Array.Copy(img0.Px, (cy0 + y) * img0.W + cx0, cp, y * cw, cw);
        var img = new VImg(cp, cw, ch);
        var flipPx = new uint[img.Px.Length];
        for (int y = 0; y < img.H; y++) for (int x = 0; x < img.W; x++) flipPx[y * img.W + x] = img.Px[y * img.W + img.W - 1 - x];
        var flip = new VImg(flipPx, img.W, img.H);
        var tr = new FaceTracker(face);
        var f = tr.Process(img, 2);
        tr.Lost();
        var ff = tr.Process(flip, 2);
        if (f == null || ff == null) { Ok(false, "사진에서 얼굴을 못 찾음"); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var g = it.Gaze(img, f);
        long ms = sw.ElapsedMilliseconds;
        var gf = it.Gaze(flip, ff);
        if (g is not { } a || gf is not { } b) { Ok(false, "눈동자를 못 찾음"); return; }
        bool inside = a.a.U > 0.15 && a.a.U < 0.85 && a.b.U > 0.15 && a.b.U < 0.85 && Math.Abs(a.v) < 0.25;
        Ok(inside, $"사진 눈동자: 그림 왼쪽 눈 U {a.a.U:0.00} V {a.a.V:0.00}, 오른쪽 눈 U {a.b.U:0.00} V {a.b.V:0.00} (눈꼬리 사이 안쪽), 두 눈 {ms}ms");
        // 좌우 뒤집은 사진: 평균 U 는 1 − U, V 는 그대로 (오른쪽 눈 뒤집기 처리가 맞는지)
        Ok(Math.Abs(b.u - (1 - a.u)) < 0.06 && Math.Abs(b.v - a.v) < 0.06, $"좌우 뒤집은 사진: U {b.u:0.000} (기대 {1 - a.u:0.000}), V {b.v:0.000} (기대 {a.v:0.000})");
        // 눈동자를 잘라 옆으로 옮긴 사진: 그 방향으로 U 가 움직임 (두 눈 모두 눈 너비의 15% 오른쪽으로)
        var moved = (uint[])img.Px.Clone();
        foreach (var e in new[] { a.a, a.b })
        {
            double w = Math.Sqrt(Math.Pow(e.C1.x - e.C0.x, 2) + Math.Pow(e.C1.y - e.C0.y, 2));
            int r = (int)Math.Round(w * 0.18), sh = (int)Math.Round(w * 0.15);
            int ix = (int)Math.Round(e.Iris.x), iy = (int)Math.Round(e.Iris.y);
            // 눈동자 둘레 원을 sh 만큼 오른쪽으로 (남는 왼쪽은 흰자 색 = 눈동자 왼쪽 바깥 픽셀)
            uint sclera = img.Px[iy * img.W + Math.Max(0, ix - r - 2)];
            for (int y = iy - r; y <= iy + r; y++)
                for (int x = ix + r + sh; x >= ix - r; x--)
                {
                    if (y < 0 || y >= img.H || x < 0 || x >= img.W) continue;
                    int sx = x - sh;
                    bool inIris = (sx - ix) * (sx - ix) + (y - iy) * (y - iy) <= r * r;
                    bool wasIris = (x - ix) * (x - ix) + (y - iy) * (y - iy) <= r * r;
                    if (inIris) moved[y * img.W + x] = img.Px[y * img.W + sx];
                    else if (wasIris) moved[y * img.W + x] = sclera;
                }
        }
        int dbg = 0;
        it.Debug = (inp, iris) =>
        {
            var im = new Media.Argb(64, 64);
            for (int i = 0; i < 64 * 64; i++) im.P[i] = 0xff000000u | ((uint)(inp[i] * 255) << 16) | ((uint)(inp[4096 + i] * 255) << 8) | (uint)(inp[8192 + i] * 255);
            for (int k = 0; k < 5; k++) { int x = (int)iris[k * 3], y = (int)iris[k * 3 + 1]; if (x >= 0 && y >= 0 && x < 64 && y < 64) im.P[y * 64 + x] = k == 0 ? 0xffff0000u : 0xffffff00u; }
            File.WriteAllBytes(Path.Combine(outDir, $"iris_in{dbg++}.png"), Png(im));
        };
        it.Gaze(img, f);
        var gm = it.Gaze(new VImg(moved, img.W, img.H), f);
        it.Debug = null;
        File.WriteAllBytes(Path.Combine(outDir, "iris_moved.png"), Png(new Media.Argb(img.W, img.H, moved.Select(x => x | 0xff000000u).ToArray())));
        File.WriteAllBytes(Path.Combine(outDir, "iris_orig.png"), Png(new Media.Argb(img.W, img.H, img.Px.Select(x => x | 0xff000000u).ToArray())));
        Note($"눈동자를 눈 너비 15% 오른쪽으로 옮겨 그린 사진: U {a.u:0.000} → {gm?.u:0.000} (오른쪽으로 움직임. 시험 사진은 눈동자 위에 웹 판 표시 점이 있어 크기는 덜 따라감 — iris_in*.png 에 모델 입력·결과)");
    }
}
