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
        if (err != null) MessageBox.Show(owner, err, "모델 받기");
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
        using var f = new Form { Text = label, Width = 420, Height = 140, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedToolWindow, ControlBox = false, BackColor = Opt.Bg, ForeColor = Opt.Fg };
        var lab = new Label { Text = label + "… (이 PC 안에서)", Dock = DockStyle.Top, Height = 30, Padding = new Padding(8) };
        var bar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Maximum = 1000, Style = ProgressBarStyle.Marquee };
        var cancel = new Button { Text = "취소", Dock = DockStyle.Bottom, Height = 30 };
        cancel.Click += (_, _) => cts.Cancel();
        f.Controls.Add(cancel); f.Controls.Add(bar); f.Controls.Add(lab);
        var prog = new Progress<double>(v => { if (bar.IsDisposed) return; bar.Style = ProgressBarStyle.Continuous; bar.Value = (int)Math.Clamp(v * 1000, 0, 1000); });
        f.Shown += async (_, _) =>
        {
            try { await work(prog, cts.Token); }
            catch (Exception e) { onError(e); }
            f.Close();
        };
        f.ShowDialog(owner ?? App.Main);
    }

    /// <summary>모델이 없으면 받을지 묻고 받음. 있거나 받았으면 true</summary>
    public static bool EnsureModel(AiModel m, IWin32Window owner, string what)
    {
        if (m == null) return false;
        if (m.Installed) return true;
        var q = MessageBox.Show(owner, $"{what}에 쓸 \"{m.Name}\" 모델이 아직 없습니다 ({m.Files.Sum(f => f.size) / 1e6:0.0}MB, {m.License} · {m.Badge}).\n\n지금 받을까요? 받은 뒤에는 인터넷 없이 이 PC 안에서만 돌아갑니다.\n(AI > AI 모델 관리에서도 받을 수 있습니다)", what, MessageBoxButtons.YesNo);
        return q == DialogResult.Yes && DownloadWithProgress(m, owner);
    }
}
