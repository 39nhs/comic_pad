// 클립 스튜디오 파일(.clip) 가져오기 모듈: 열기(Openers ".clip") + "파일/클립 스튜디오 파일 가져오기 (.clip)…" 메뉴,
// 진행률·취소 창, 가져온 뒤 "가져오지 못한 것" 정리 창.
// 시험 (창 없이): --selftest clip 결과폴더  (ClipSelfTest — 직접 만든 합성 .clip 으로)
using System.Runtime.ExceptionServices;
using System.Text;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.ClipImport;

public sealed class ClipImportModule : IModule
{
    public void Init()
    {
        FileCommands.Openers[".clip"] = Open;
        Registry.Menu("파일/클립 스튜디오 파일 가져오기 (.clip)…", ImportCommand, order: 47);
    }

    static void ImportCommand()
    {
        if (!FileCommands.ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog { Title = "클립 스튜디오 파일 가져오기", Filter = "클립 스튜디오 (*.clip)|*.clip|모든 파일|*.*" };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        FileCommands.OpenPath(dlg.FileName);
    }

    /// <summary>열기 담당: 창이 있으면 진행률 창과 함께, 끝나면 정리 창</summary>
    public static Document Open(string path)
    {
        var r = Run(path);
        if (r == null) throw new OperationCanceledException("가져오기를 취소했습니다");
        if (App.Main != null && App.Main.IsHandleCreated && App.Main.Visible) App.Main.BeginInvoke(() => ShowSummary(r));
        else if (r.Notes.Count > 0 || r.Missing.Count > 0) App.Toast($"클립 스튜디오 파일: 가져오지 못한 것 {r.Missing.Count}개, 참고 {r.Notes.Count}개");
        return r.Doc;
    }

    /// <summary>가져오기 (예상 못한 오류는 알기 쉬운 말로 바꿈). 취소면 null</summary>
    public static ClipImportResult ImportSafe(string path, IProgress<double> prog, CancellationToken ct)
    {
        try { return ClipImporter.Import(path, prog, ct); }
        catch (OperationCanceledException) { return null; }
        catch (InvalidDataException) { throw; }
        catch (IOException ex) { throw new IOException("파일을 읽지 못했습니다: " + ex.Message, ex); }
        catch (OutOfMemoryException) { throw new InvalidDataException("메모리가 모자라 이 파일을 다 열지 못했습니다 (레이어가 아주 많거나 캔버스가 아주 큼)"); }
        catch (Exception ex) { throw new InvalidDataException("클립 스튜디오 파일을 읽다가 문제가 생겼습니다 (아직 모르는 버전·형식일 수 있음): " + ex.Message, ex); }
    }

    static ClipImportResult Run(string path)
    {
        var main = App.Main;
        if (main == null || !main.IsHandleCreated || !main.Visible) return ImportSafe(path, null, default);
        using var f = new Form
        {
            Text = "클립 스튜디오 파일 가져오기", Width = 420, Height = 140, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedToolWindow,
            ControlBox = false, BackColor = Opt.Bg, ForeColor = Opt.Fg, ShowInTaskbar = false,
        };
        UI.Dialogs.Modals.Adopt(f);
        var lab = new Label { Text = Path.GetFileName(path) + " 읽는 중…", Dock = DockStyle.Top, Height = 32, Padding = new Padding(8), AutoEllipsis = true };
        var bar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Maximum = 1000 };
        var cts = new CancellationTokenSource();
        var cancel = new Button { Text = "취소", Dock = DockStyle.Bottom, Height = 30 };
        cancel.Click += (_, _) => { cts.Cancel(); cancel.Enabled = false; lab.Text = "취소하는 중…"; };
        f.Controls.Add(cancel); f.Controls.Add(bar); f.Controls.Add(lab);
        ClipImportResult result = null; ExceptionDispatchInfo err = null;
        var prog = new Progress<double>(v => { if (!bar.IsDisposed) bar.Value = (int)Math.Clamp(v * 1000, 0, 1000); });
        f.Shown += async (_, _) =>
        {
            try { result = await Task.Run(() => ImportSafe(path, prog, cts.Token)); }
            catch (Exception ex) { err = ExceptionDispatchInfo.Capture(ex); }
            f.Close();
        };
        f.ShowDialog(main);
        err?.Throw();
        return result;
    }

    /// <summary>정리 글 (창·시험 공용)</summary>
    public static string SummaryText(ClipImportResult r)
    {
        var sb = new StringBuilder();
        var d = r.Doc;
        sb.AppendLine($"가져옴: {d.W}×{d.H}px, {d.Dpi}dpi, 레이어 {r.LayerCount}개, 폴더 {r.FolderCount}개 ({r.Elapsed.TotalSeconds:0.0}초)");
        sb.AppendLine($"저장한 클립 스튜디오: {r.Version} (추정) · 용지 색 {d.PaperColor}{(d.PaperVisible ? "" : " (숨김)")}");
        sb.AppendLine();
        sb.AppendLine(r.Missing.Count == 0 ? "가져오지 못한 것: 없음" : $"가져오지 못한 것 ({r.Missing.Count}):");
        foreach (var m in r.Missing) sb.AppendLine(" • " + m);
        if (r.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("참고:");
            foreach (var n in r.Notes) sb.AppendLine(" • " + n);
        }
        sb.AppendLine();
        sb.AppendLine("다른 이름으로 저장하면 .cpad 로 저장됩니다 (원래 .clip 파일은 바뀌지 않음).");
        return sb.ToString();
    }

    static void ShowSummary(ClipImportResult r)
    {
        if (r.Missing.Count == 0 && r.Notes.Count == 0) { App.Toast($"클립 스튜디오 파일을 가져왔습니다 (레이어 {r.LayerCount}개)"); return; }
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = SummaryText(r).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"),
            Dock = DockStyle.Fill, BackColor = Opt.Bg2, ForeColor = Opt.Fg, BorderStyle = BorderStyle.None, Font = new Font("Malgun Gothic", 9.5f),
        };
        using var f = Dlg.Modal("클립 스튜디오 가져오기 — 가져오지 못한 것", box, 640, 480);
        f.ShowDialog(App.Main);
    }
}
