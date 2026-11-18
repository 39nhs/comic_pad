// 처리되지 않은 오류가 나면: ① 지금 작품을 data/autosave 에 긴급 저장 ② (설정 "오류가 나면 보고서 만들기" 가 켜져 있으면)
// 보고서를 data/reports 에 만들고 ③ 웹식 창으로 알림 [보고서 보기] [GitHub 에 보고하기] [닫기]. 앱은 되도록 계속 실행.
// 아무것도 저절로 보내지 않음 — GitHub 은 사용자가 보고서를 다 본 뒤 버튼을 누를 때만 브라우저로 작성 화면을 엶.
// 앱이 닫히는 오류(다른 스레드)는 창을 띄울 수 없으므로 저장·보고서만 하고, 다음에 켤 때 알림.
namespace ComicPadNative.Modules.Crash;

public static class CrashHandler
{
    /// <summary>창 모드(또는 시험)에서만 켬. 다른 시험·--render 중에는 예전처럼 알림만</summary>
    public static bool Active;
    /// <summary>시험: 창을 화면 밖에 띄우고 바로 닫음 (CrashUi.LastShown 에 내용 기록)</summary>
    public static bool Headless;

    static readonly object gate = new();
    static bool busy;
    static string lastSig;
    static DateTime lastTime;
    static SaveResult lastSave;

    /// <summary>마지막으로 만든 보고서 (시험·메뉴용)</summary>
    public static string LastReport, LastReportPath;
    public static SaveResult LastSave => lastSave;

    public static bool ReportEnabled => App.Get("crashReport", true);

    /// <summary>오류 처리. true = 여기서 알렸음 (예전 "오류:" 알림은 띄우지 않음)</summary>
    public static bool Handle(Exception ex, string command, bool fatal)
    {
        if (!Active || ex == null) return false;
        if (!Headless && (Shell2.GuiTest.On || UI.Chrome.UiShot.Active)) return false;   // 화면 자동 시험·화면 찍기 중에는 예전처럼 (창을 띄우지 않음)
        lock (gate)
        {
            if (busy) return false;   // 처리 중에 또 오류 (저장·보고서 만들기 안에서) → 예전처럼
            busy = true;
        }
        try
        {
            CrashLog.Add("오류", (command != null ? command + ": " : "") + ex.GetType().Name + ": " + ex.Message);
            CrashGuard.NoteEarlyCrash(ex);
            // 같은 오류가 짧은 사이에 되풀이 (그리기 중 오류 등) → 저장·창은 한 번만
            string sig = ex.GetType().FullName + "|" + (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim();
            bool repeat = sig == lastSig && (DateTime.Now - lastTime).TotalSeconds < 30;
            lastSig = sig; lastTime = DateTime.Now;
            if (repeat)
            {
                if (!fatal) App.Toast("오류가 또 났습니다: " + Short(ex));
                return true;
            }
            var save = CrashReport.EmergencySave();
            lastSave = save;
            if (save.Path != null) CrashLog.Add("저장", "긴급 저장함 (*.cpad)");
            if (!ReportEnabled)
            {
                if (!fatal) App.Toast("오류: " + Short(ex) + (save.Path != null ? " — 작업은 data\\autosave 에 저장했습니다" : ""));
                return true;
            }
            string report = CrashReport.Build(ex, command, fatal, save);
            string file = CrashReport.SaveReport(report);
            LastReport = report; LastReportPath = file;
            if (fatal) { CrashGuard.SetUnseen(file, save.Path); return true; }
            var info = new CrashUi.Info(Short(ex), command, save, report, file, false);
            if (Headless) { CrashUi.Show(info); return true; }
            var main = App.Main;
            if (main != null && !main.IsDisposed && main.IsHandleCreated && main.Visible)
            {
                main.BeginInvoke((Action)(() => CrashUi.Show(info)));
                return true;
            }
            if (main != null && !main.IsDisposed) { CrashUi.Pending = info; return true; }   // 창이 뜨면 보여 줌
            CrashGuard.SetUnseen(file, save.Path);   // 창이 없음 → 다음에 켤 때
            return true;
        }
        catch { return false; }
        finally { lock (gate) busy = false; }
    }

    /// <summary>사용자에게 보일 짧은 설명 (가린 것)</summary>
    public static string Short(Exception ex)
    {
        string m = (ex.Message ?? "").Split('\n')[0].Trim();
        if (m.Length > 160) m = m[..160] + "…";
        return CrashReport.Sanitize($"{ex.GetType().Name}: {m}", CrashReport.UserStrings(App.Doc));
    }

    /// <summary>시험용: 되풀이 판단 초기화</summary>
    public static void ResetRepeat() { lastSig = null; lastTime = DateTime.MinValue; }
}
