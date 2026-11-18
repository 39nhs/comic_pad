// PenDiag 모듈: 펜 진단 창 (도움말 > 펜 필기감 진단, 설정 > 펜 > 펜 진단) — 실제 펜으로 몇 분 그려 보며 필압·보고율·지연·떨림·기울기·버튼을 확인하고,
// 필압 곡선을 맞춘다 (곡선 편집·자동 맞추기·클립스튜디오 곡선 값). 그림판에서 그릴 때의 입력→화면 지연도 늘 조금씩 모아 둔다 (CanvasView.InputPresented).
// 시험: --selftest pendiag 결과폴더 (창 없이, 만든 포인터 점으로)
using ComicPadNative.UI;

namespace ComicPadNative.Modules.PenDiagnosis;

public sealed class PenDiagModule : IModule
{
    public const string MenuPath = "도움말/펜 필기감 진단 (이 기기·이 펜에 맞추기)…";   // 웹 id pen.diagnose 가 가리키는 경로 (Shell2.WebIds)

    public void Init()
    {
        Registry.Menu(MenuPath, PenDiagWindow.Show, order: 51);
        CanvasView.InputPresented += CanvasLatency.Add;
    }
}

/// <summary>그림판에서 그리는 동안 잰 입력→화면 지연 (최근 2000장, ms)</summary>
public static class CanvasLatency
{
    static readonly double[] ring = new double[2000];
    static int count, head;
    public static void Add(long inputQpc, long presentQpc, bool drawing)
    {
        if (!drawing) return;
        double ms = (presentQpc - inputQpc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms < 0 || ms > 500) return;
        lock (ring) { ring[head] = ms; head = (head + 1) % ring.Length; count = Math.Min(count + 1, ring.Length); }
    }
    public static List<double> Values() { lock (ring) { var l = new List<double>(count); for (int i = 0; i < count; i++) l.Add(ring[(head - count + i + ring.Length) % ring.Length]); return l; } }
    public static void Clear() { lock (ring) { count = head = 0; } }
}
