// 화면 비교용 찍기 (COMICPAD_UI_SHOT=결과.png): 창 안쪽을 1600×900 으로 맞추고 1.5초 뒤 안쪽만 그림으로 저장하고 끝냄.
// 웹 판 스크린샷(1600×900 보기 영역)과 같은 크기. 반드시 COMICPAD_SETTINGS·COMICPAD_DATA 를 임시로.
//  COMICPAD_UI_SHOT_RUN = "menu:file" (메뉴 펼치기, "menu:file:all" = 모든 항목) · "more" (도구 더 보기) · "tool:select" · 메뉴 경로 · "web:웹 명령 id"
//  열린 펼침 메뉴·팝업도 제자리에 겹쳐 그림.
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.UI.Chrome;

public static class UiShot
{
    /// <summary>화면 찍기 중 (시작 창 등을 띄우지 않게)</summary>
    public static bool Active;
    /// <summary>찍을 파일 (환경 변수는 지우므로 대화상자 찍기가 여기서 읽음)</summary>
    public static string ShotPath;

    public static void Hook(MainForm f)
    {
        string shot = Environment.GetEnvironmentVariable("COMICPAD_UI_SHOT");
        if (string.IsNullOrEmpty(shot)) return;
        Active = true; ShotPath = shot;
        // Program.cs 의 예전 찍기(창 전체, 2초)는 쓰지 않게: 이 과정 안에서만 변수를 지움
        Environment.SetEnvironmentVariable("COMICPAD_UI_SHOT", null);
        f.Shown += (_, _) =>
        {
            f.WindowState = FormWindowState.Normal; f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0); f.ClientSize = new Size(1600, 900);
            // 모듈들이 "창이 뜬 뒤 첫 Idle" 에 붙이는 것(상태 줄 작업 시간 등)이 끝난 뒤에 찍음
            void Idle(object s, EventArgs e)
            {
                Application.Idle -= Idle;
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (_, _) => { t.Stop(); Take(f, shot); };
                t.Start();
            }
            Application.Idle += Idle;
        };
    }

    static void Pump(int n) { for (int i = 0; i < n; i++) { Application.DoEvents(); Thread.Sleep(30); } }

    static void Take(MainForm f, string shot)
    {
        try
        {
            f.ClientSize = new Size(1600, 900);
            Pump(5);
            string run = Environment.GetEnvironmentVariable("COMICPAD_UI_SHOT_RUN") ?? "";
            var popups = new List<ToolStripDropDown>();
            if (run.StartsWith("menu:"))
            {
                var parts = run[5..].Split(':');
                var ti = f.TopBar.Open(parts[0], parts.Length > 1 && parts[1] == "all");
                Pump(8);
                if (ti != null) popups.Add(ti.DropDown);
            }
            else if (run == "more") { f.Tools.OpenMore(); Pump(8); if (f.Tools.LastPopup != null) popups.Add(f.Tools.LastPopup); }
            else if (run.StartsWith("tool:")) { App.SetTool(run[5..]); Pump(10); }
            else if (run.StartsWith("web:")) { f.BeginInvoke((Action)(() => WebIds.Run(run[4..]))); Pump(30); }
            else if (run.Length > 0 && Registry.Commands.TryGetValue(run, out var cmd)) { f.BeginInvoke(cmd); Pump(30); }
            Save(f, shot, popups);
            // 배치 기록 (웹 판 layout.json 과 맞대 보기)
            Rectangle B(Control c) => c.Visible ? f.RectangleToClient(c.RectangleToScreen(c.ClientRectangle)) : Rectangle.Empty;
            File.WriteAllLines(shot + ".txt", new[]
            {
                $"topbar {B(f.TopBar)}", $"menubar {B(f.MenuBar)}", $"toolbar {B(f.Tools)}", $"stage {B(f.Canvas)}", $"sidepanel {B(f.Side)}", $"bottombar {B(f.BottomBar)}", $"statusbar {B(f.Status)}",
                "status: " + string.Join(" | ", f.Status.Items.Cast<ToolStripItem>().Select(i => $"{i.Text}@{i.Bounds.X}")),
                "menus: " + string.Join(" ", f.MenuBar.Items.Cast<ToolStripItem>().Where(i => i.Visible).Select(i => $"{i.Text}@{i.Bounds.X}")),
                "tools: " + string.Join(" ", f.Tools.Shown.Select(t => t.Id)),
                "popups: " + string.Join(" ; ", popups.Where(p => p != null).Select(p => $"{p.Bounds} pad={p.Padding} items=" + string.Join(",", p.Items.Cast<ToolStripItem>().Take(3).Select(i => i.Bounds.ToString())))),
            });
        }
        catch (Exception ex) { File.WriteAllText(shot + ".error.txt", ex.ToString()); }
        Environment.Exit(0);
    }

    static IEnumerable<Control> All(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (var k in All(c)) yield return k; } }

    /// <summary>창 안쪽 + 열린 팝업을 그림으로</summary>
    public static void Save(Form f, string path, IEnumerable<ToolStripDropDown> popups = null)
    {
        var target = Form.ActiveForm != null && Form.ActiveForm != f && Form.ActiveForm.Modal ? Form.ActiveForm : f;
        using var full = new Bitmap(target.Width, target.Height);
        target.DrawToBitmap(full, new Rectangle(0, 0, full.Width, full.Height));
        Bitmap outp;
        if (target == f)
        {
            var org = f.PointToScreen(Point.Empty);
            int ox = org.X - f.Left, oy = org.Y - f.Top;
            outp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
            using var g = Graphics.FromImage(outp);
            g.DrawImage(full, new Rectangle(0, 0, outp.Width, outp.Height), new Rectangle(ox, oy, outp.Width, outp.Height), GraphicsUnit.Pixel);
            // 창 위에 떠 있는 알림은 DrawToBitmap 의 겹침 순서가 거꾸로라 따로 맨 위에
            foreach (Control c in f.Controls)
            {
                if (c.GetType().Name != "Pill" || !c.Visible) continue;
                using var pb = new Bitmap(c.Width, c.Height);
                c.DrawToBitmap(pb, new Rectangle(0, 0, c.Width, c.Height));
                if (c.Region != null) { using var rg = c.Region.Clone(); rg.Translate(c.Left, c.Top); g.SetClip(rg, System.Drawing.Drawing2D.CombineMode.Replace); }
                g.DrawImage(pb, c.Location);
                g.ResetClip();
            }
            foreach (var dd in popups ?? Enumerable.Empty<ToolStripDropDown>())
            {
                if (dd == null || !dd.Visible) continue;
                using var pb = new Bitmap(dd.Width, dd.Height);
                dd.DrawToBitmap(pb, new Rectangle(0, 0, pb.Width, pb.Height));
                var p = f.PointToClient(dd.Bounds.Location);
                if (dd.Region != null) { using var rg = dd.Region.Clone(); rg.Translate(p.X, p.Y); g.SetClip(rg, System.Drawing.Drawing2D.CombineMode.Replace); }
                g.DrawImage(pb, p);
                g.ResetClip();
            }
        }
        else outp = (Bitmap)full.Clone();
        using (outp) outp.Save(path);
    }
}
