// ComicPad 네이티브 앱 시제품 (단계 0): 창 + 직접 짠 그리기 엔진 + Windows 펜 입력 직접 받기.
// 목적: 같은 펜으로 클립스튜디오·웹 판(1.1.3)과 지연·선 품질·필압을 비교해서 네이티브 재작성을 계속할지 정한다.
using System.Runtime.InteropServices;

namespace ComicPadNative;

static class Program
{
    [STAThread]
    static void Main()
    {
        EnableMouseInPointer(true);            // 마우스도 펜·터치와 같은 포인터 메시지로
        ApplicationConfiguration.Initialize();  // PerMonitorV2 DPI, 비주얼 스타일
        Application.Run(new CanvasForm());
    }

    [DllImport("user32.dll")] static extern bool EnableMouseInPointer(bool enable);
}
