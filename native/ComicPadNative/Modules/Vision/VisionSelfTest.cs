// 비전 모듈 자체 점검 (창 없이, 정해진 결과): COMICPAD_VISION_SELFTEST=결과 폴더, COMICPAD_DATA=임시 작업물 폴더.
//   예) COMICPAD_DATA=%TEMP%\cpv COMICPAD_VISION_SELFTEST=%TEMP%\cpv-out ComicPadNative.exe --render x.cpad y.png
// 선택: COMICPAD_VISION_DOWNLOAD=1 → 비전 모델을 목록 주소에서 받아(크기·SHA-256 검사) 모델 시험까지
//       COMICPAD_VISION_POSE_IMG / _HAND_IMG / _FACE_IMG = 사진 경로 → 사진 추정 시험 (없으면 건너뜀)
// 웹캠·마이크는 열지 않는다 (카메라 목록만 읽고, 없는 번호를 열어 "없음" 처리를 확인).
// 결과: 결과 폴더/report.txt, 끝 코드 = 실패 수
using System.Text;
using ComicPadNative.Core;
using ComicPadNative.Modules.AI;
using ComicPadNative.Modules.Scene3D;

namespace ComicPadNative.Modules.Vision;

public static class VisionSelfTest
{
    static readonly StringBuilder log = new();
    static int fails;
    static void Check(bool ok, string what) { log.AppendLine((ok ? "OK   " : "FAIL ") + what); Console.WriteLine((ok ? "OK   " : "FAIL ") + what); if (!ok) fails++; }
    static void Note(string s) { log.AppendLine("     " + s); Console.WriteLine("     " + s); }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Section("내용 인식 채우기", TestInpaint);
        Section("복구 브러시", TestHeal);
        Section("마이크 입 모양", TestLipSync);
        Section("얼굴 표정 계산", TestFaceCalib);
        Section("웹캠 (열지 않음)", TestWebcam);
        Section("이미지 AI 주소 제한", TestImageAi);
        Section("비상업 AI 내보내기 확인", TestNcGuard);
        Section("비전 모델 목록", TestRegistry);
        Section("사진 → 자세·손·얼굴", () => TestModels(dir));
        log.AppendLine(fails == 0 ? "비전 점검 통과" : $"비전 점검 실패 {fails}개");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        Console.WriteLine(fails == 0 ? "비전 점검 통과" : $"비전 점검 실패 {fails}개");
        return fails;
    }

    static void Section(string name, Action a)
    {
        log.AppendLine($"== {name}"); Console.WriteLine($"== {name}");
        try { a(); } catch (Exception e) { Check(false, $"{name}: 예외 {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
    }

    // ---------------------------------------------------------------- 내용 인식 채우기
    static readonly uint ColA = 0xFF3060C0, ColB = 0xFFE8D8A0;
    static uint Stripe(int x, int y) => ((x / 6) % 2 == 0) ? ColA : ColB;     // 세로 줄무늬 (폭 6)

    static (Document d, Layer l) StripeDoc(int W, int H)
    {
        var d = new Document(W, H);
        var l = new Layer(d, "바탕");
        var px = new uint[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) px[y * W + x] = Stripe(x, y);
        l.Px.Write(new Rect(0, 0, W, H), px);
        d.Layers.Add(l); d.ActiveId = l.Id;
        return (d, l);
    }

    static int Dist(uint a, uint b) => Math.Abs((int)((a >> 16) & 255) - (int)((b >> 16) & 255)) + Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255)) + Math.Abs((int)(a & 255) - (int)(b & 255)) + Math.Abs((int)(a >> 24) - (int)(b >> 24));

    static void TestInpaint()
    {
        const int W = 200, H = 140;
        var (d, l) = StripeDoc(W, H);
        App.SetDoc(d);
        // 지울 "물체": 가운데 빨간 네모
        var hole = new Rect(80, 50, 36, 30);
        var px = l.Px.Read(hole); Array.Fill(px, 0xFFFF0000u); l.Px.Write(hole, px);
        var before = l.Px.Read(new Rect(0, 0, W, H));
        // 선택 = 물체보다 2px 넓게
        var mask = new byte[W * H];
        for (int y = hole.Y - 2; y < hole.B + 2; y++) for (int x = hole.X - 2; x < hole.R + 2; x++) mask[y * W + x] = 255;
        d.Sel.Mask = mask;
        var rect = Inpaint.FillRegion(d, 1);
        Check(rect.W > 40 && rect.H > 34 && rect.X >= 0 && rect.R <= W, $"계산 범위 {rect}");
        var t0 = DateTime.Now;
        var filled = Inpaint.ContentAwareFill(d, rect, l.Px.Read(rect));
        Note($"채우기 {(DateTime.Now - t0).TotalMilliseconds:0}ms");
        // 같은 입력이면 같은 결과
        var again = Inpaint.ContentAwareFill(d, rect, l.Px.Read(rect));
        Check(filled.AsSpan().SequenceEqual(again), "같은 입력 → 같은 결과 (시드 고정)");
        VisionModule.Commit(d, l, filled, rect, false);
        var after = l.Px.Read(new Rect(0, 0, W, H));
        int outsideDiff = 0, red = 0, inside = 0, palette = 0, phase = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (mask[i] == 0) { if (after[i] != before[i]) outsideDiff++; continue; }
                inside++;
                uint c = after[i];
                if (((c >> 16) & 255) > 200 && ((c >> 8) & 255) < 80 && (c & 255) < 80) red++;
                if (Math.Min(Dist(c, ColA), Dist(c, ColB)) < 60) palette++;
                if (Dist(c, Stripe(x, y)) < 60) phase++;
            }
        Check(outsideDiff == 0, $"선택 밖 픽셀은 그대로 (바뀐 수 {outsideDiff})");
        Check(red == 0, $"빨간 물체가 지워짐 (남은 빨강 {red}/{inside})");
        Check(palette >= inside * 0.85, $"채운 색이 주변 무늬 색 ({palette}/{inside} = {100.0 * palette / inside:0}%)");
        Note($"줄무늬 위치까지 맞은 비율 {100.0 * phase / inside:0}% (참고)");
        Check(phase >= inside * 0.5, $"줄무늬가 이어짐 ({100.0 * phase / inside:0}% ≥ 50%)");
        // 실행취소·다시 실행
        App.History.Undo(d);
        Check(l.Px.Read(new Rect(0, 0, W, H)).AsSpan().SequenceEqual(before), "실행취소 → 원래 그림 (빨간 물체 다시)");
        App.History.Redo(d);
        Check(l.Px.Read(new Rect(0, 0, W, H)).AsSpan().SequenceEqual(after), "다시 실행 → 채운 그림");
        // 큰 그림 (줄여서 계산 → 다시 키움): 시간·선택 밖 보존
        {
            const int BW = 1600, BH = 1000;
            var (bd, bl) = StripeDoc(BW, BH);
            var bm = new byte[BW * BH];
            for (int y = 400; y < 640; y++) for (int x = 700; x < 980; x++) bm[y * BW + x] = 255;
            bd.Sel.Mask = bm;
            var br = Inpaint.FillRegion(bd, 2);
            var b0 = bl.Px.Read(new Rect(0, 0, BW, BH));
            var tb = DateTime.Now;
            var bf = Inpaint.ContentAwareFill(bd, br, bl.Px.Read(br));
            double sec = (DateTime.Now - tb).TotalSeconds;
            VisionModule.Commit(bd, bl, bf, br, false);
            var b1 = bl.Px.Read(new Rect(0, 0, BW, BH));
            int changedOut = 0; for (int i = 0; i < b0.Length; i++) if (bm[i] == 0 && b0[i] != b1[i]) changedOut++;
            Check(sec < 90, $"큰 선택 (280×240, 계산 범위 {br.W}×{br.H}, 긴 쪽 1200 넘으면 줄여 계산) {sec:0.0}초");
            Check(changedOut == 0, $"큰 그림도 선택 밖은 그대로 (바뀐 수 {changedOut})");
        }
        // 새 레이어로
        int n0 = d.Layers.Count;
        VisionModule.Commit(d, l, filled, rect, true);
        var nl = d.Layers.Last();
        Check(d.Layers.Count == n0 + 1 && nl.Name == "내용 인식 채우기", "새 레이어로 넣기");
        Check(nl.Px.Get(5, 5) == 0 && nl.Px.Get(hole.X + 10, hole.Y + 10) != 0, "새 레이어는 선택 안만 그림");
        App.History.Undo(d);
        Check(d.Layers.Count == n0, "새 레이어 실행취소");
        // 투명 레이어 (미리 곱한 알파 보존)
        var t = Inpaint.Fill(new uint[64 * 64], Enumerable.Range(0, 64 * 64).Select(i => (byte)(i % 64 > 20 && i % 64 < 40 && i / 64 > 20 && i / 64 < 40 ? 1 : 0)).ToArray(), 64, 64);
        Check(t.All(c => c == 0), "투명한 곳은 투명하게 채움");
    }

    // ---------------------------------------------------------------- 복구 브러시
    static void TestHeal()
    {
        // 계산: 결은 원본, 밝기는 고칠 곳 쪽으로
        int n = 32;
        var src = new uint[n * n]; var dst = new uint[n * n];
        for (int i = 0; i < n * n; i++) { uint v = (uint)(100 + ((i % n) % 2) * 20); src[i] = 0xFF000000 | v << 16 | v << 8 | v; uint w = 180; dst[i] = 0xFF000000 | w << 16 | w << 8 | w; }
        var res = HealTool.HealPatch(src, dst, n, n, 6);
        double mean = res.Average(c => (double)(c & 255));
        int lo = (int)(res[n * 16 + 16] & 255), hi = (int)(res[n * 16 + 17] & 255);
        Check(Math.Abs(mean - 180) < 4, $"밝기는 고칠 곳에 맞춤 (평균 {mean:0.0}, 목표 180)");
        Check(Math.Abs(hi - lo) >= 15, $"원본의 결(줄무늬 차이 {Math.Abs(hi - lo)})이 남음");

        // 도구: 회색 바탕의 검은 점을 스팟 복구로 문지르기 → 점이 사라지고, 실행취소로 돌아옴
        const int W = 160, H = 120;
        var d = new Document(W, H);
        var l = new Layer(d, "바탕");
        var px = new uint[W * H];
        for (int i = 0; i < px.Length; i++) { uint v = (uint)(150 + ((i % W) / 3 % 2) * 10); px[i] = 0xFF000000 | v << 16 | v << 8 | v; }
        for (int y = 56; y < 64; y++) for (int x = 76; x < 84; x++) px[y * W + x] = 0xFF101010;
        l.Px.Write(new Rect(0, 0, W, H), px);
        d.Layers.Add(l); d.ActiveId = l.Id;
        App.SetDoc(d);
        var tool = new HealTool { Size = 24, Hardness = 0.8 };
        tool.Down(new PointerArgs { X = 78, Y = 60, Pressure = 1 });
        tool.Move(new PointerArgs { X = 82, Y = 60, Pressure = 1 });
        tool.Up(new PointerArgs { X = 82, Y = 60 });
        uint c = l.Px.Get(80, 60);
        Check((c & 255) > 120, $"검은 점이 지워짐 (가운데 밝기 {c & 255}, 원래 16)");
        Check(l.Px.Get(5, 5) == px[5 * W + 5], "브러시 밖은 그대로");
        App.History.Undo(d);
        Check((l.Px.Get(80, 60) & 255) == 0x10, "실행취소 → 점 다시");
        // 원본 모드: 원본을 안 찍으면 아무것도 안 함
        var t2 = new HealTool { Mode = "source" };
        t2.Down(new PointerArgs { X = 50, Y = 50 }); t2.Up(new PointerArgs { X = 50, Y = 50 });
        Check(l.Px.Read(new Rect(0, 0, W, H)).AsSpan().SequenceEqual(px), "원본 없는 획 → 그림 그대로");
    }

    // ---------------------------------------------------------------- 마이크 입 모양
    static void TestLipSync()
    {
        const int rate = 16000, block = 1024;
        // 0.5초 조용 → 1초 "말소리"(150Hz, 4Hz 로 커졌다 작아짐) → 0.5초 조용
        int N = rate * 2; var wave = new float[N];
        for (int i = 0; i < N; i++)
        {
            double t = (double)i / rate;
            double env = t < 0.5 || t >= 1.5 ? 0.002 * Math.Sin(i * 0.7) : 0.35 * Math.Abs(Math.Sin(Math.PI * 4 * (t - 0.5)));
            wave[i] = (float)(env * Math.Sin(2 * Math.PI * 150 * t));
        }
        var mouths = new List<(double t, double m)>();
        for (int s = 0; s + block <= N; s += block)
        {
            double lv = VRig.Rms(wave.AsSpan(s, block));
            mouths.Add(((s + block / 2.0) / rate, VRig.LevelToMouth(lv, 1)));
        }
        Check(mouths.Where(x => x.t < 0.45 || x.t > 1.55).All(x => x.m == 0), "조용할 때 입 닫힘 (문턱 아래 0)");
        double maxTalk = mouths.Where(x => x.t > 0.5 && x.t < 1.5).Max(x => x.m);
        Check(maxTalk > 0.8, $"말할 때 입이 크게 열림 (최대 {maxTalk:0.00})");
        // 음절 사이(소리 작음)는 덜 열림 → 입이 여닫힘
        double valley = mouths.Where(x => Math.Abs(x.t - 1.0) < 0.04).Select(x => x.m).DefaultIfEmpty(1).Min();
        Check(valley < maxTalk * 0.6, $"음절 사이에는 덜 열림 ({valley:0.00} < {maxTalk * 0.6:0.00})");
        // 소리 크기 → 입: 단조 증가, 0~1
        double prev = -1; bool mono = true;
        for (double lv = 0; lv <= 0.3; lv += 0.005) { double m = VRig.LevelToMouth(lv, 1); if (m < prev - 1e-12 || m < 0 || m > 1) mono = false; prev = m; }
        Check(mono, "소리 크기 → 입 벌림이 단조 증가·0~1");
        Check(VRig.LevelToMouth(0.05, 2) > VRig.LevelToMouth(0.05, 1), "입 민감도(gain) 를 올리면 더 열림");
        Check(Math.Abs(VRig.Rms(new float[] { 0.5f, -0.5f, 0.5f, -0.5f }) - 0.5) < 1e-9, "RMS 계산");
        Note($"말소리 구간 입 값: {string.Join(" ", mouths.Where(x => x.t > 0.5 && x.t < 1.5).Select(x => x.m.ToString("0.0")))}");
    }

    // ---------------------------------------------------------------- 얼굴 표정
    static void TestFaceCalib()
    {
        var c = new FaceCalib();
        var neutral = new FaceMeasure(0.30, 0.30, 0.01, 0.40, 0.11, 0.11, 0.02, -0.05, 0.01);
        for (int i = 0; i < c.Frames; i++) c.Feed(neutral);
        Check(c.Ready, "처음 몇 장으로 평소 얼굴을 맞춤");
        var s0 = c.State(neutral, true);
        Check(Math.Abs(s0.Yaw) < 1e-9 && Math.Abs(s0.Pitch) < 1e-9 && s0.BlinkL < 0.01 && s0.Mouth < 0.01 && s0.Smile < 0.01, "평소 얼굴 → 가운데·눈 뜸·입 닫힘");
        var wink = neutral with { EarL = 0.06 };                     // 그 사람의 왼눈 감음
        var sm = c.State(wink, true); var sn = c.State(wink, false);
        Check(sm.BlinkR > 0.9 && sm.BlinkL < 0.1, $"거울: 왼눈 감음 → 화면 오른쪽 눈 (R {sm.BlinkR:0.00})");
        Check(sn.BlinkL > 0.9 && sn.BlinkR < 0.1, "거울 아님: 왼눈 감음 → 캐릭터 왼눈");
        var open = c.State(neutral with { MouthOpen = 0.15 }, true);
        Check(open.Mouth > 0.9, $"입 벌림 (0.15) → 입 {open.Mouth:0.00}");
        var smile = c.State(neutral with { MouthWidth = 0.48 }, true);
        Check(smile.Smile > 0.9 && smile.Funnel < 0.01, $"입이 넓어지면 웃음 ({smile.Smile:0.00})");
        var pucker = c.State(neutral with { MouthWidth = 0.33, MouthOpen = 0.04 }, true);
        Check(pucker.Funnel > 0.6, $"입이 좁아지면 오므림 ({pucker.Funnel:0.00})");
        var brow = c.State(neutral with { BrowL = 0.13, BrowR = 0.13 }, true);
        Check(brow.Brow > 0.5, $"눈썹 올림 ({brow.Brow:0.00})");
        // 고개: 카메라 그림 오른쪽을 봄(+yaw) → 거울이면 캐릭터는 화면 왼쪽(-)
        var turn = c.State(neutral with { Yaw = 0.02 + Math.PI / 12 }, true);
        Check(Math.Abs(turn.Yaw + 0.5) < 1e-6, $"고개 15° → 상태 -0.5 (거울) ({turn.Yaw:0.00})");
        Check(Math.Abs(c.State(neutral with { Yaw = 0.02 + Math.PI / 12 }, false).Yaw - 0.5) < 1e-6, "거울 아님 → +0.5");
        var up = c.State(neutral with { Pitch = -0.05 + Math.PI / 6 }, true);
        Check(up.Pitch < -0.99, "위를 보면 상태 pitch 가 - (화면 위)");
        var big = c.State(neutral with { Roll = 2 }, true);
        Check(big.Roll >= -1 && big.Roll <= 1, "값은 -1~1 로 자름");
        c.Reset();
        Check(!c.Ready, "다시 맞추기");
    }

    // ---------------------------------------------------------------- 웹캠
    static void TestWebcam()
    {
        List<string> cams = null;
        try { cams = Webcam.List(); Check(true, $"카메라 목록 읽기 ({cams.Count}대, 열지 않음)"); }
        catch (Exception e) { Check(false, "카메라 목록 읽기: " + e.Message); }
        try { using var w = new Webcam(4000); Check(false, "없는 카메라가 열림?"); }
        catch (InvalidOperationException e) { Check(e.Message.Contains("카메라"), $"없는 카메라 → 알림 문구 ({e.Message})"); }
        // 스튜디오 연결
        Check(Vtuber.FaceSourceFactory != null, "버튜버 스튜디오에 카메라 얼굴 추적 연결됨");
        Check(Scene3DWindow.PhotoPoseProvider != null, "3D 소재 창에 사진 → 자세 연결됨");
    }

    static void TestImageAi()
    {
        Check(ImageAi.IsLocal("http://127.0.0.1:7860") && ImageAi.IsLocal("http://localhost:7861/"), "이 PC 주소는 허용");
        Check(!ImageAi.IsLocal("http://192.168.0.5:7860") && !ImageAi.IsLocal("https://example.com") && !ImageAi.IsLocal("http://127.0.0.1.evil.com:7860") && !ImageAi.IsLocal("http://localhost:7860/x"), "다른 주소는 막음");
    }

    static void TestNcGuard()
    {
        var d = new Document(10, 10);
        App.SetDoc(d);
        Check(AiLicenseExtras.NcUsed(d).Count == 0 && AiLicenseExtras.ExportGuard(), "비상업 AI 기록이 없으면 묻지 않고 내보냄");
        d.Extra["aiLog"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = "depth-b", ["name"] = "깊이 B", ["nc"] = true });
        Check(AiLicenseExtras.NcUsed(d).Count == 1, "비상업 AI 기록 찾기");
        int before = Registry.Menus.Count;
        Registry.Menu("시험/그림 내보내기 시험…", () => { }, order: 1);
        Registry.Menu("시험/팔레트 내보내기 시험…", () => { }, order: 2);
        int n = AiLicenseExtras.WrapExports();
        Check(n >= 1 && AiLicenseExtras.WrapExports() == 0, $"내보내기 메뉴 감싸기 (이번에 {n}개, 두 번 감싸지 않음)");
        Registry.Menus.RemoveAll(m => m.Path.StartsWith("시험/"));
    }

    // ---------------------------------------------------------------- 모델
    static void TestRegistry()
    {
        foreach (var m in VisionModels.All)
        {
            var r = AiRuntime.Registry().FirstOrDefault(x => x.Id == m.Id);
            Check(r != null && r.Files.All(f => f.url.StartsWith("https://") && f.size > 0 && f.sha256?.Length == 64) && r.License == "Apache-2.0" && r.Commercial == "yes",
                $"{m.Id}: 목록에 있음 · https · 크기·SHA-256 · {m.License} · {m.Files.Sum(f => f.size) / 1e6:0.0}MB");
        }
        Check(AiRuntime.Registry().Any(x => x.Id == "pose-mp"), "웹 판 모델 목록도 그대로");
    }

    static void TestModels(string dir)
    {
        bool dl = Environment.GetEnvironmentVariable("COMICPAD_VISION_DOWNLOAD") == "1";
        foreach (var m in VisionModels.All.Select(x => VisionModels.Get(x.Id)))
        {
            if (!m.Installed && dl)
            {
                var t0 = DateTime.Now;
                try { AiRuntime.Download(m, null, CancellationToken.None).GetAwaiter().GetResult(); Check(m.Installed, $"{m.Id} 받기 (크기·SHA-256 확인) {(DateTime.Now - t0).TotalSeconds:0.0}초"); }
                catch (Exception e) { Check(false, $"{m.Id} 받기: {e.Message}"); }
            }
            if (!m.Installed) Note($"{m.Id}: 모델이 없어 건너뜀 (COMICPAD_VISION_DOWNLOAD=1 로 받아 시험)");
        }
        var blank = new VImg(Enumerable.Repeat(0xFF808080u, 320 * 240).ToArray(), 320, 240);
        var pose = VisionModels.Get(VisionModels.Pose);
        if (pose.Installed)
        {
            var r = PosePhoto.DetectBody(pose, blank);
            Check(r == null || r.Flag < 0.5, $"빈 회색 그림 → 사람 없음 ({(r == null ? "못 찾음" : $"flag {r.Flag:0.00}")}), {AiRuntime.LastEp}");
            var img = Img("COMICPAD_VISION_POSE_IMG");
            if (img != null)
            {
                var t0 = DateTime.Now;
                r = PosePhoto.DetectBody(pose, img);
                Check(r != null && r.Flag > 0.5, $"사진에서 사람 찾음 (점수 {r?.Score:0.00}, flag {r?.Flag:0.00}) {(DateTime.Now - t0).TotalMilliseconds:0}ms");
                if (r != null)
                {
                    Check(r.Image.All(p => p.x > -img.W * 0.2 && p.x < img.W * 1.2 && p.y > -img.H * 0.2 && p.y < img.H * 1.2), "랜드마크가 그림 안쪽");
                    double hipW = Math.Abs(r.World[23].X - r.World[24].X) + Math.Abs(r.World[23].Z - r.World[24].Z);
                    double torso = Hyp(r.World[11], r.World[23]);
                    Check(torso > 0.25 && torso < 0.9, $"세계 좌표 몸통 길이 {torso:0.00}m (사람 크기)");
                    var p = Bvh.PoseFromLandmarks(r.World);
                    Check(p.Count >= 8, $"인형 포즈로 바꿈 (관절 {p.Count}개)");
                    Note($"코 {Fmt(r.Image[0])} 왼발목 {Fmt(r.Image[27])} 오른발목 {Fmt(r.Image[28])} 엉덩이 폭 {hipW:0.00}");
                    Note("포즈: " + string.Join(" ", p.Take(6).Select(kv => $"{kv.Key}=({string.Join(",", kv.Value.Select(v => v.ToString("0")))})")));
                    File.WriteAllText(Path.Combine(dir, "pose_landmarks.txt"), string.Join("\n", r.Image.Select((q, i) => $"{i}\t{q.x:0.0}\t{q.y:0.0}\t{q.vis:0.00}\t{r.World[i].X:0.000}\t{r.World[i].Y:0.000}\t{r.World[i].Z:0.000}")));
                }
            }
            else Note("COMICPAD_VISION_POSE_IMG 가 없어 사진 자세 시험은 건너뜀");
        }
        var hand = VisionModels.Get(VisionModels.Hand);
        if (hand.Installed)
        {
            var r = PosePhoto.DetectHand(hand, blank);
            Check(r == null || r.Flag < 0.5, $"빈 회색 그림 → 손 없음 ({(r == null ? "못 찾음" : $"flag {r.Flag:0.00}")})");
            var img = Img("COMICPAD_VISION_HAND_IMG");
            if (img != null)
            {
                r = PosePhoto.DetectHand(hand, img);
                Check(r != null && r.Flag > 0.5, $"사진에서 손 찾음 (flag {r?.Flag:0.00})");
                if (r != null)
                {
                    var hp = Bvh.HandFromLandmarks(r.World);
                    Check(hp.ContainsKey("index1") && hp.ContainsKey("thumb3"), $"손 포즈로 바꿈 (마디 {hp.Count}개)");
                    Note($"손목 {Fmt(r.Image[0])} 검지 끝 {Fmt(r.Image[8])} 가운데 끝 {Fmt(r.Image[12])} 오른손 확률 {r.Right:0.00}");
                    Note("손 포즈: " + string.Join(" ", hp.Take(6).Select(kv => $"{kv.Key}={kv.Value[0]:0}")));
                }
            }
            else Note("COMICPAD_VISION_HAND_IMG 가 없어 사진 손 시험은 건너뜀");
        }
        var face = VisionModels.Get(VisionModels.Face);
        if (face.Installed)
        {
            var tr = new FaceTracker(face);
            Check(tr.Process(blank) == null, "빈 회색 그림 → 얼굴 없음");
            var img = Img("COMICPAD_VISION_FACE_IMG");
            if (img != null)
            {
                var t0 = DateTime.Now;
                var f = tr.Process(img, 2);
                Check(f != null && f.Score > 0.5, $"사진에서 얼굴 그물 (점수 {f?.Score:0.00}) {(DateTime.Now - t0).TotalMilliseconds:0}ms");
                if (f != null)
                {
                    var P = f.P;
                    Check(P[33].x < P[263].x, "오른눈 바깥(33)이 그림 왼쪽, 왼눈 바깥(263)이 그림 오른쪽");
                    double eyeY = (P[33].y + P[263].y) / 2;
                    Check(eyeY < P[1].y && P[1].y < P[13].y && P[13].y < P[152].y, "눈 → 코 → 입 → 턱 순서로 아래");
                    var me = FaceMeasure.From(f);
                    Check(me.EarL > 0.1 && me.EarR > 0.1, $"눈 뜸 (EAR {me.EarL:0.00}/{me.EarR:0.00})");
                    Check(Math.Abs(me.Yaw) < 0.6 && Math.Abs(me.Roll) < 0.6, $"고개 각도 (yaw {me.Yaw * 180 / Math.PI:0}°, pitch {me.Pitch * 180 / Math.PI:0}°, roll {me.Roll * 180 / Math.PI:0}°)");
                    Note($"입 벌림 {me.MouthOpen:0.000} 입 너비 {me.MouthWidth:0.00} 눈썹 {me.BrowL:0.000}/{me.BrowR:0.000}");
                    // 다음 장은 앞 그물로 (얼굴 찾기 없이) 추적
                    var t1 = DateTime.Now;
                    var f2 = tr.Process(img);
                    Check(f2 != null && Math.Abs(f2.P[1].x - P[1].x) < 6, $"다음 장 추적 (코 이동 {Math.Abs((f2?.P[1].x ?? 0) - P[1].x):0.0}px) {(DateTime.Now - t1).TotalMilliseconds:0}ms");
                    var st = new FaceCalib().State(me, true);
                    Check(st.BlinkL < 0.5 && st.BlinkR < 0.5, "기본 기준으로도 눈 뜬 상태");
                }
            }
            else Note("COMICPAD_VISION_FACE_IMG 가 없어 사진 얼굴 시험은 건너뜀");
        }
    }

    static VImg Img(string env)
    {
        var p = Environment.GetEnvironmentVariable(env);
        return string.IsNullOrEmpty(p) || !File.Exists(p) ? null : VImg.FromFile(p);
    }
    static double Hyp(Bvh.Landmark a, Bvh.Landmark b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    static string Fmt((double x, double y) p) => $"({p.x:0},{p.y:0})";
    static string Fmt((double x, double y, double v) p) => $"({p.x:0},{p.y:0})";
}
