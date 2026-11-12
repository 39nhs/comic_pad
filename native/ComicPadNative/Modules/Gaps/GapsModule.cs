// 웹 판과 남은 차이 메우기 (Gaps): 손실 WebP·WebM·Vorbis·버튜버 녹화·눈동자·소리 형식은 Media/Vision 쪽 파일에,
// 여기에는 펜 예측(PenPredict)·퀵바 "직선 모드"·"각도 스냅" 버튼(웹 ui/quickbar.js 의 app.modifiers.line/snap) 과 시험(GapsTest).
// 시험: COMICPAD_DATA=임시 폴더 로 --selftest gaps 결과폴더
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.Modules.Gaps;

/// <summary>화면 버튼으로 켜는 가상 수정 키 (손가락·펜만 쓰는 태블릿에서 Shift 대신)</summary>
public static class VirtualMods
{
    /// <summary>직선 모드: 브러시·지우개 선을 직선으로 (웹 brush.js lineMode = shift || modifiers.line)</summary>
    public static bool Line;
    /// <summary>각도 스냅: 모든 도구에 Shift 를 누른 것처럼 (웹 modifiers.shift = snap)</summary>
    public static bool Snap;
    /// <summary>지금 도구에 Shift 를 더할지</summary>
    public static bool ShiftFor(ToolBase t) => Snap || (Line && t != null && t.Id is "brush" or "eraser");
}

public sealed class GapsModule : IModule
{
    public void Init()
    {
        // 퀵바·아래 막대 버튼 고르기 목록에 (Shell2 의 QuickActions 는 같은 id 를 지우고 더하므로 차례와 상관없음)
        QuickActions.All.RemoveAll(a => a.Id is "line" or "snap");
        QuickActions.All.Add(new QuickAction("line", "직선 모드", "📏", () => { VirtualMods.Line = !VirtualMods.Line; App.Toast(VirtualMods.Line ? "직선 모드 켜짐" : "직선 모드 꺼짐"); }, () => VirtualMods.Line));
        QuickActions.All.Add(new QuickAction("snap", "각도 스냅 (Shift 대용)", "∠", () => { VirtualMods.Snap = !VirtualMods.Snap; App.Toast(VirtualMods.Snap ? "각도 스냅 켜짐 (Shift 를 누른 것처럼)" : "각도 스냅 꺼짐"); }, () => VirtualMods.Snap));
    }
}
