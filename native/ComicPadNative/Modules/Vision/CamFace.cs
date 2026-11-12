// 버튜버 스튜디오의 "카메라 얼굴 추적": 웹캠 → 얼굴 그물 → 표정·고개 (뒤 스레드). 화면 타이머는 Poll 로 최근 값만 가져간다.
// 영상은 이 PC 안에서만 쓰고 저장·전송하지 않는다.
using System.Diagnostics;
using ComicPadNative.Modules.AI;
using ComicPadNative.Modules.Scene3D;

namespace ComicPadNative.Modules.Vision;

public sealed class CamFaceSource : IVFaceSource
{
    readonly Thread th;
    volatile bool stop;
    readonly object lk = new();
    readonly FaceCalib calib = new();
    FaceMeasure? last;
    long lastTick;
    string status;
    Webcam cam;
    readonly AiModel model;

    /// <summary>스튜디오의 카메라 단추에서 (모델 확인·받기, 카메라 고르기). 취소면 null, 실패는 예외</summary>
    public static IVFaceSource Create()
    {
        var m = VisionModels.Get(VisionModels.Face);
        if (!VisionUi.EnsureModel(m, App.Main, "카메라 얼굴 추적")) return null;
        if (!AiFeatures.LicenseGate(m)) return null;
        var cams = Webcam.List();
        if (cams.Count == 0) throw new InvalidOperationException("카메라를 찾지 못했습니다 (연결·Windows 설정 > 개인 정보 > 카메라 권한을 확인하세요)");
        int idx = Math.Clamp(App.Get("vtCamIndex", 0), 0, cams.Count - 1);
        if (cams.Count > 1)
        {
            idx = PickCamera(cams, idx);
            if (idx < 0) return null;
            App.Set("vtCamIndex", idx);
        }
        return new CamFaceSource(m, idx);
    }

    static int PickCamera(List<string> cams, int def)
    {
        var v = Filters.Ui.Ask("카메라 고르기", new[] { Filters.Field.Pick("cam", "얼굴을 비출 카메라", cams.Select((n, i) => (i.ToString(), n)).ToArray(), def.ToString()) }, "켜기");
        return v == null ? -1 : int.Parse(v.S("cam"));
    }

    public CamFaceSource(AiModel m, int camIndex)
    {
        model = m;
        cam = new Webcam(camIndex);            // 못 열면 여기서 예외 (단추 쪽에서 알림)
        status = $"카메라 추적 준비 중… ({cam.Name}, {cam.Width}×{cam.Height}) 처음 1초쯤은 평소 얼굴(입 다물고 정면)로 있어 주세요";
        th = new Thread(Loop) { IsBackground = true, Name = "버튜버 얼굴 추적" };
        th.SetApartmentState(ApartmentState.MTA);
        th.Start();
    }

    void Loop()
    {
        try { Track(); }
        finally { var c = cam; cam = null; c?.Dispose(); }     // 카메라는 이 스레드가 닫음 (읽는 중에 닫히지 않게)
    }

    void Track()
    {
        FaceTracker tr;
        try { tr = new FaceTracker(model); }
        catch (Exception e) { Set("얼굴 추적 모델을 열지 못했습니다: " + e.Message); return; }
        var sw = Stopwatch.StartNew(); int frames = 0; double lastReport = 0; bool lostShown = false;
        while (!stop)
        {
            VImg f;
            try { f = cam.Read(); } catch (Exception e) { Set("카메라 영상을 읽지 못했습니다: " + e.Message); break; }
            if (f == null) { Set("카메라 영상이 끊겼습니다 (다른 프로그램이 카메라를 가져갔을 수 있습니다)"); break; }
            if (stop) break;
            FaceMesh mesh = null;
            try { mesh = tr.Process(f); } catch (Exception e) { Set("얼굴 추적 오류: " + e.Message); }
            frames++;
            if (mesh != null)
            {
                var me = FaceMeasure.From(mesh);
                lock (lk) { calib.Feed(me); last = me; lastTick = Environment.TickCount64; }
                lostShown = false;
            }
            else if (!lostShown) { Set("얼굴을 찾는 중… (카메라에 얼굴이 잘 보이게)"); lostShown = true; }
            double t = sw.Elapsed.TotalSeconds;
            if (t - lastReport >= 1 && mesh != null)
            {
                Set($"카메라 추적 중 {frames / (t - lastReport):0}장/초 · {AiRuntime.LastEp} (영상은 저장·전송하지 않습니다)");
                frames = 0; lastReport = t;
            }
            else if (t - lastReport >= 1) { frames = 0; lastReport = t; }
        }
    }

    void Set(string s) { lock (lk) status = s; }

    public string Status { get { lock (lk) return status; } }

    /// <summary>평소 얼굴 다시 맞추기 (다음 몇 장을 기준으로)</summary>
    public void Recalibrate() { lock (lk) calib.Reset(); }

    public VState Poll(bool mirror)
    {
        lock (lk)
        {
            if (last == null || Environment.TickCount64 - lastTick > 600) return null;
            return calib.State(last.Value, mirror);
        }
    }

    public void Dispose()
    {
        stop = true;
        // Read 는 길어야 한 장 시간 → 스레드가 끝나면서 카메라를 닫는다
        if (th != null && th.IsAlive) th.Join(1500);
    }
}
