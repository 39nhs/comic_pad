// 시험: 버튜버 녹화 (MP4·WebM, 소리는 만든 소리로 — 실제 마이크는 열지 않음), 스튜디오 창의 녹화 단추
using System.Diagnostics;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Media;
using ComicPadNative.Modules.Scene3D;

namespace ComicPadNative.Modules.Gaps;

public static partial class GapsTest
{
    /// <summary>마이크 대신: 흐른 시간만큼 440Hz 소리를 내줌 (48kHz 모노)</summary>
    sealed class FakeMic : IVtAudio
    {
        readonly Stopwatch sw = Stopwatch.StartNew(); long given;
        public int Rate => 48000; public int Channels => 1;
        public short[] Drain()
        {
            long now = (long)(sw.Elapsed.TotalSeconds * Rate);
            var o = new short[Math.Max(0, now - given)];
            for (int i = 0; i < o.Length; i++) o[i] = (short)(Math.Sin(2 * Math.PI * 440 * (given + i) / Rate) * 8000);
            given = now; return o;
        }
        public void Dispose() { }
    }

    static void TestVtRecorder()
    {
        foreach (var fmt in new[] { "mp4", "webm" })
        {
            string path = Path.Combine(outDir, $"vt_rec.{fmt}");
            var rec = new VtRecorder(fmt, path, 640, 360, new FakeMic());
            var sw = Stopwatch.StartNew();
            int pushed = 0;
            while (sw.ElapsedMilliseconds < 2000)
            {
                if (rec.Due())
                {
                    using var bmp = new Bitmap(640, 360);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.FromArgb(0, 255, 0));
                        float x = 50 + sw.ElapsedMilliseconds / 5f;
                        g.FillEllipse(Brushes.Bisque, x, 80, 160, 200); g.FillEllipse(Brushes.Black, x + 50, 150, 14, 18); g.FillEllipse(Brushes.Black, x + 100, 150, 14, 18);
                    }
                    rec.Push(bmp); pushed++;
                }
                Thread.Sleep(5);
            }
            var stopSw = Stopwatch.StartNew();
            rec.Stop();
            long stopMs = stopSw.ElapsedMilliseconds;
            try
            {
                var mf = Mp4Probe.Read(path, new[] { 10 });
                Ok(mf.W == 640 && mf.H == 360 && Math.Abs(mf.Duration - 2) < 0.3 && mf.Frames >= (fmt == "mp4" ? 55 : 40) && mf.HasAudio && Math.Abs(mf.AudioSeconds - 2) < 0.4,
                    $"버튜버 녹화 {fmt.ToUpperInvariant()}: {mf.W}×{mf.H}, 영상 {mf.Duration:0.00}초 {mf.Frames}장 (넣은 장 {pushed}, 건너뜀 {rec.Dropped}), 소리 {mf.AudioSeconds:0.00}초, {new FileInfo(path).Length / 1024}KB, 멈추고 저장 {stopMs}ms");
                var (rate, ch, data) = MfAudio.Decode(path);
                double rms = Math.Sqrt(data[0].Skip(rate / 2).Take(rate / 2).Average(v => (double)v * v));
                Ok(rate == 48000 && Math.Abs(rms - 8000 / 32768.0 / Math.Sqrt(2)) < 0.05, $"  녹화 안 소리 (만든 440Hz): {rate}Hz, 크기 {rms:0.000} (원래 {8000 / 32768.0 / Math.Sqrt(2):0.000})");
            }
            catch (Exception ex) { Ok(false, $"버튜버 녹화 {fmt}: Media Foundation 으로 열기 실패 {ex.Message}"); }
        }
    }

    /// <summary>실시간 녹화 속도 참고: 1280×720 VP8 빠른 모드 (키 1장 + 움직이는 사이 프레임)</summary>
    static void TestVp8Speed()
    {
        var bg = Natural(1280, 720, 4);
        var enc = new Vp8Video(0.75, int.MaxValue, fast: true);
        var sw = Stopwatch.StartNew();
        enc.Encode(bg);
        long keyMs = sw.ElapsedMilliseconds; sw.Restart();
        for (int i = 0; i < 10; i++)
        {
            var f = new Argb(1280, 720, (uint[])bg.P.Clone());
            for (int y = 200; y < 500; y++) for (int x = 100 + i * 30; x < 400 + i * 30; x++) f.P[y * 1280 + x] = 0xffe0b090;
            enc.Encode(f);
        }
        double interMs = sw.ElapsedMilliseconds / 10.0;
        Ok(interMs < 1000.0 / 30 * 3, $"VP8 빠른 모드 1280×720: 키 프레임 {keyMs}ms, 일부만 움직이는 사이 프레임 평균 {interMs:0}ms (30fps 실시간은 {1000.0 / 30:0}ms — 밀리면 녹화가 장을 건너뜀)");
    }

    static void TestVtStudioRec()
    {
        var vd = new Document(200, 240);
        var face = new Layer(vd, "얼굴"); var eye = new Layer(vd, "눈");
        using (var b = new Bitmap(200, 240)) { using (var g = Graphics.FromImage(b)) g.FillEllipse(Brushes.Bisque, 50, 50, 100, 120); CpadIO.DrawBitmap(face.Px, b, 0, 0); }
        using (var b = new Bitmap(200, 240)) { using (var g = Graphics.FromImage(b)) { g.FillEllipse(Brushes.Black, 75, 100, 12, 16); g.FillEllipse(Brushes.Black, 113, 100, 12, 16); } CpadIO.DrawBitmap(eye.Px, b, 0, 0); }
        vd.Layers.AddRange(new[] { face, eye });
        vd.Extra["vtuber"] = new JsonObject { ["parts"] = new JsonObject { [face.Id] = new JsonObject { ["role"] = "head" }, [eye.Id] = new JsonObject { ["role"] = "eyesOpen" } } };
        var rig = VRig.FromDoc(vd);
        App.Settings["vtRecFmt"] = "webm";
        using var st = Vtuber.TestStudio(rig, vd);
        st.Show(); Application.DoEvents();
        st.TestToggleRec();
        var rec = st.TestRec;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000) { st.TestManual.Yaw = Math.Sin(sw.ElapsedMilliseconds / 200.0); st.TestFrame(); Application.DoEvents(); Thread.Sleep(10); }
        st.TestToggleRec();
        st.Close();
        string path = rec?.Path;
        bool ok = path != null && File.Exists(path);
        var r = ok ? WebmProbe.Read(File.ReadAllBytes(path)) : null;
        Ok(ok && r.Tracks[0].W == 200 && r.Tracks[0].H == 240 && r.Blocks.Count >= 20 && Math.Abs(r.Duration - 1000) < 200,
            $"스튜디오 녹화 단추 (WebM, 마이크 없이): {Path.GetFileName(path)} {r?.Tracks[0].W}×{r?.Tracks[0].H}, 블록 {r?.Blocks.Count}, {r?.Duration:0}ms");
    }
}
