// 비전 모듈 공용 창: 모델 받기 진행률, 오래 걸리는 일 (뒤에서 돌리고 진행률·취소)
using ComicPadNative.Modules.AI;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Vision;

public static class VisionUi
{
    /// <summary>모델 받기 (목록의 주소·크기·SHA-256 검사는 AiRuntime.Download). 성공하면 true</summary>
    public static bool DownloadWithProgress(AiModel m, IWin32Window owner)
    {
        bool ok = false; string err = null;
        RunProgress(owner, $"{m.Name} 받는 중", async (prog, ct) => { await AiRuntime.Download(m, prog, ct); ok = true; }, e => err = e.Message);
        if (err != null) UI.Dialogs.Modals.Info(err, "모델 받기");
        return ok && m.Installed;
    }

    /// <summary>뒤에서 돌리고 끝날 때까지 기다림 (예외는 다시 던짐)</summary>
    public static T RunBusy<T>(IWin32Window owner, string label, Func<T> work)
    {
        T result = default; Exception err = null;
        RunProgress(owner, label, (prog, ct) => Task.Run(() => { result = work(); }, ct), e => err = e);
        if (err is OperationCanceledException) return default;
        if (err != null) throw err;
        return result;
    }

    /// <summary>진행률 창 (취소 단추). work 가 끝나면 닫힘. 창이 없으면(시험) 그냥 기다림</summary>
    public static void RunProgress(IWin32Window owner, string label, Func<IProgress<double>, CancellationToken, Task> work, Action<Exception> onError)
    {
        var cts = new CancellationTokenSource();
        if (owner == null && App.Main == null)
        {
            try { work(null, cts.Token).GetAwaiter().GetResult(); } catch (Exception e) { onError(e); }
            return;
        }
        // 웹 .modal 모양: 글 + 진행 막대 + [취소]
        using var f = new UI.Dialogs.ModalForm(label, 420, 0, closeButton: false);
        var lab = new Label { Text = label + "… (이 PC 안에서)", AutoSize = true, Font = UI.Controls.Web.Body, ForeColor = Opt.Fg };
        var bar = new UI.Controls.WebProgress();
        f.SetContent(new UI.Controls.WebStack { Controls = { lab, bar } });
        f.AddAction("취소", () => { cts.Cancel(); return false; });
        f.FormClosing += (_, e) => { if (!cts.IsCancellationRequested && e.CloseReason == CloseReason.UserClosing && f.Tag == null) { cts.Cancel(); e.Cancel = true; } };
        var prog = new Progress<double>(v => { if (bar.IsDisposed) return; bar.Value = Math.Clamp(v, 0, 1); });
        f.Shown += async (_, _) =>
        {
            try { await work(prog, cts.Token); }
            catch (Exception e) { onError(e); }
            f.Tag = "done"; f.Close();
        };
        f.ShowDialog(owner ?? App.Main);
    }

    /// <summary>모델이 없으면 받을지 묻고 받음. 있거나 받았으면 true</summary>
    public static bool EnsureModel(AiModel m, IWin32Window owner, string what)
    {
        if (m == null) return false;
        if (m.Installed) return true;
        var q = UI.Dialogs.Modals.Confirm($"{what}에 쓸 \"{m.Name}\" 모델이 아직 없습니다 ({m.Files.Sum(f => f.size) / 1e6:0.0}MB, {m.License} · {m.Badge}).\n\n지금 받을까요? 받은 뒤에는 인터넷 없이 이 PC 안에서만 돌아갑니다.\n(AI > AI 모델 관리에서도 받을 수 있습니다)", "받기", false, what);
        return q && DownloadWithProgress(m, owner);
    }
}
