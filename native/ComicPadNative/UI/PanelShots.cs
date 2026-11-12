// 화면 비교용: COMICPAD_UI_PANELS=결과폴더 → 메인 창이 뜬 뒤 오른쪽 패널(레이어·색·내비·페이지·기록·에셋·애니)과 모든 도구의 옵션을
// 웹 패널 폭(300px)으로 한 장씩 (옵션은 끝까지 펼쳐서) PNG 로 남기고 끝냄. 반드시 COMICPAD_SETTINGS·COMICPAD_DATA 를 임시로.
namespace ComicPadNative.UI;

public static class PanelShots
{
    public static string Dir => Environment.GetEnvironmentVariable("COMICPAD_UI_PANELS");

    public static void Run()
    {
        string dir = Dir;
        Directory.CreateDirectory(dir);
        var log = new System.Text.StringBuilder();
        using var host = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-6000, 0), ClientSize = new Size(300, 900), BackColor = Theme.Panel };
        host.Show();
        void Shot(string name, Func<Control> make, int height, bool fill)
        {
            try
            {
                host.Controls.Clear();
                var c = make();
                if (fill) { c.Dock = DockStyle.Fill; host.ClientSize = new Size(300, height); host.Controls.Add(c); }
                else
                {
                    var scroll = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
                    scroll.Controls.Add(c);
                    host.Controls.Add(scroll);
                    for (int i = 0; i < 3; i++) { Application.DoEvents(); host.PerformLayout(); }
                    int h = Math.Max(120, c.GetPreferredSize(new Size(300, 0)).Height + c.Top + 4);
                    host.ClientSize = new Size(300, Math.Min(4000, h));
                }
                for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(15); }
                // 창은 화면보다 크게 못 만드니 내용 부품을 직접 그림
                var target = fill ? (Control)host : c;
                using var bmp = new Bitmap(target.Width, target.Height);
                target.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(Path.Combine(dir, name + ".png"));
                if (Environment.GetEnvironmentVariable("COMICPAD_UI_PANELS_DUMP") == name)
                {
                    var sb = new System.Text.StringBuilder();
                    void Dump(Control k, int d) { sb.AppendLine(new string(' ', d * 2) + k.GetType().Name + " " + k.Bounds + " vis=" + k.Visible + " '" + (k.Text.Length > 20 ? k.Text[..20] : k.Text) + "'"); foreach (Control q in k.Controls) Dump(q, d + 1); }
                    Dump(host, 0);
                    File.WriteAllText(Path.Combine(dir, name + ".tree.txt"), sb.ToString());
                }
                log.AppendLine("ok " + name);
                foreach (Control k in host.Controls.Cast<Control>().ToList()) k.Dispose();
            }
            catch (Exception e) { log.AppendLine("FAIL " + name + ": " + e); }
        }
        Shot("panel_layers", () => new LayersPanel(), 520, true);
        Shot("panel_color", () => new ColorPanel(), 760, true);
        foreach (var p in Registry.Panels) Shot("panel_" + Safe(p.Title), p.Create, p.Dock == "bottom" ? 300 : 620, true);
        foreach (var t in Registry.Tools.ToList())
        {
            var tt = t;
            Shot("tool_" + tt.Id, () => { App.SetTool(tt.Id); return tt.BuildOptions() ?? new Label { Text = "(옵션 없음)", ForeColor = Theme.Muted, AutoSize = true }; }, 0, false);
        }
        App.SetTool("brush");
        File.WriteAllText(Path.Combine(dir, "panels.txt"), log.ToString());
        host.Close();
        if (App.Doc != null) App.Doc.Modified = false;
        Environment.Exit(0);
    }

    static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s.Replace(' ', '_'); }
}
