// 버전 비교 (웹 판 ui/compare.js): 이전 저장본과 지금을 밀어서 비교 / 겹쳐 보기 / 달라진 곳 빨갛게 / 나란히
using System.Drawing.Imaging;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public static class Compare
{
    static Bitmap Fit(Document d, int max) => Img.Flatten(d, Math.Min(1, max / (double)Math.Max(d.W, d.H)), true);

    /// <summary>달라진 곳: 밝기 차이가 있는 픽셀을 빨갛게, 나머지는 흐리게. (그림, 바뀐 비율)</summary>
    public static unsafe (Bitmap img, double ratio) Diff(Bitmap a, Bitmap b, int W, int H)
    {
        using var ca = Pad(a, W, H); using var cb = Pad(b, W, H);
        var outB = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        var r = new Rectangle(0, 0, W, H);
        var da = ca.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = cb.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dout = outB.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        long changed = 0;
        try
        {
            for (int y = 0; y < H; y++)
            {
                byte* pa = (byte*)da.Scan0 + y * da.Stride, pb = (byte*)db.Scan0 + y * db.Stride, po = (byte*)dout.Scan0 + y * dout.Stride;
                for (int x = 0; x < W; x++, pa += 4, pb += 4, po += 4)
                {
                    int dd = Math.Abs(pa[0] - pb[0]) + Math.Abs(pa[1] - pb[1]) + Math.Abs(pa[2] - pb[2]);
                    if (dd > 24) { po[2] = 235; po[1] = 40; po[0] = 40; changed++; }
                    else { double g = (pb[0] + pb[1] + pb[2]) / 3.0; byte v = (byte)Math.Round(255 - (255 - g) * 0.25); po[0] = po[1] = po[2] = v; }
                    po[3] = 255;
                }
            }
        }
        finally { ca.UnlockBits(da); cb.UnlockBits(db); outB.UnlockBits(dout); }
        return (outB, changed / (double)(W * H));
    }
    static Bitmap Pad(Bitmap s, int W, int H)
    {
        var b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b); g.Clear(Color.White); g.DrawImageUnscaled(s, 0, 0);
        return b;
    }

    /// <summary>a: 이전 문서, b: 지금 문서</summary>
    public static void Show(Document a, Document b, string labelA = "이전", string labelB = "지금")
    {
        int max = Math.Min(1400, Math.Max(Screen.PrimaryScreen.WorkingArea.Width, Screen.PrimaryScreen.WorkingArea.Height));
        Bitmap ca = null, cb = null;
        U.Task("비교 준비", () => { ca = Fit(a, max); cb = Fit(b, max); });
        int W = Math.Max(ca.Width, cb.Width), H = Math.Max(ca.Height, cb.Height);
        var diff = Diff(ca, cb, W, H);
        string mode = "wipe"; double pos = 0.5;
        var root = new Panel { BackColor = Opt.Bg };
        var stage = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(60, 60, 66) };
        var range = new TrackBar { Minimum = 0, Maximum = 100, Value = 50, TickStyle = TickStyle.None, Dock = DockStyle.Top, Height = 28 };
        var info = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Opt.Muted, Padding = new Padding(6, 4, 0, 0) };
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, BackColor = Opt.Bg };
        var btns = new List<Button>();
        void Sync()
        {
            foreach (var x in btns) x.BackColor = (string)x.Tag == mode ? Opt.Accent : Opt.Bg2;
            foreach (var x in btns) x.ForeColor = (string)x.Tag == mode ? Color.Black : Opt.Fg;
            range.Visible = mode == "wipe" || mode == "fade";
            info.Text = mode == "wipe" ? $"왼쪽: {labelA} · 오른쪽: {labelB} (그림 위를 끌거나 슬라이더)" : mode == "fade" ? $"슬라이더를 오른쪽으로 갈수록 {labelA}" : mode == "diff" ? $"빨간 곳이 달라진 부분 (전체의 {diff.ratio * 100:0.0}%)" : "";
            stage.Invalidate();
        }
        foreach (var (k, t) in new[] { ("wipe", "밀어서 비교"), ("fade", "겹쳐 보기"), ("diff", "달라진 곳"), ("side", "나란히") })
        {
            var bt = Opt.Button(t, () => { mode = k; Sync(); }); bt.Tag = k; btns.Add(bt); tabs.Controls.Add(bt);
        }
        range.Scroll += (_, _) => { pos = range.Value / 100.0; stage.Invalidate(); };
        RectangleF FitRect(int w, int h, Rectangle box)
        {
            float s = Math.Min(box.Width / (float)w, box.Height / (float)h);
            return new RectangleF(box.X + (box.Width - w * s) / 2, box.Y + (box.Height - h * s) / 2, w * s, h * s);
        }
        stage.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            var box = Rectangle.Inflate(stage.ClientRectangle, -8, -8);
            if (mode == "side")
            {
                var l = new Rectangle(box.X, box.Y, box.Width / 2 - 4, box.Height - 20);
                var r = new Rectangle(box.X + box.Width / 2 + 4, box.Y, box.Width / 2 - 4, box.Height - 20);
                g.DrawImage(ca, FitRect(ca.Width, ca.Height, l)); g.DrawImage(cb, FitRect(cb.Width, cb.Height, r));
                using var br = new SolidBrush(Opt.Fg);
                g.DrawString(labelA, stage.Font, br, l.X + l.Width / 2f - 20, l.Bottom + 2); g.DrawString(labelB, stage.Font, br, r.X + r.Width / 2f - 20, r.Bottom + 2);
                return;
            }
            var rr = FitRect(W, H, box);
            g.FillRectangle(Brushes.White, rr);
            float k = rr.Width / W;
            if (mode == "diff") { g.DrawImage(diff.img, rr); return; }
            if (mode == "wipe")
            {
                g.DrawImage(cb, rr.X, rr.Y, cb.Width * k, cb.Height * k);
                var clip = new RectangleF(rr.X, rr.Y, (float)(rr.Width * pos), rr.Height);
                var st = g.Save(); g.SetClip(clip); g.FillRectangle(Brushes.White, clip); g.DrawImage(ca, rr.X, rr.Y, ca.Width * k, ca.Height * k); g.Restore(st);
                using var red = new SolidBrush(ColorTranslator.FromHtml("#e5484d"));
                g.FillRectangle(red, (float)(rr.X + rr.Width * pos - 1), rr.Y, 3, rr.Height);
                return;
            }
            g.DrawImage(cb, rr.X, rr.Y, cb.Width * k, cb.Height * k);
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = (float)pos });
            g.DrawImage(ca, Rectangle.Round(new RectangleF(rr.X, rr.Y, ca.Width * k, ca.Height * k)), 0, 0, ca.Width, ca.Height, GraphicsUnit.Pixel, ia);
        };
        void DragTo(MouseEventArgs e)
        {
            if (mode != "wipe" || e.Button != MouseButtons.Left) return;
            var rr = FitRect(W, H, Rectangle.Inflate(stage.ClientRectangle, -8, -8));
            pos = Math.Clamp((e.X - rr.X) / rr.Width, 0, 1); range.Value = (int)Math.Round(pos * 100); stage.Invalidate();
        }
        stage.MouseDown += (_, e) => DragTo(e); stage.MouseMove += (_, e) => DragTo(e);
        stage.Resize += (_, _) => stage.Invalidate();
        root.Controls.Add(stage); root.Controls.Add(range); root.Controls.Add(info); root.Controls.Add(tabs);
        using var f = Dlg.Modal($"버전 비교: {labelA} ↔ {labelB}", root, 1100, 820);
        Sync();
        f.ShowDialog(App.Main);
        ca.Dispose(); cb.Dispose(); diff.img.Dispose();
    }

    /// <summary>지금 작품의 이전 저장본(data/versions) 또는 다른 파일과 비교</summary>
    public static void VersionsDialog()
    {
        var doc = App.Doc; if (doc == null) return;
        var list = new List<(string path, string label)>();
        string rel = Story.Rel(doc.FilePath);
        if (rel != null && rel.EndsWith(".cpad", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Path.Combine(App.DataDir, "versions", rel[..^5].Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.cpad").OrderByDescending(x => x, StringComparer.Ordinal))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    string when = DateTime.TryParseExact(n, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var t) ? t.ToString("yyyy-MM-dd HH:mm:ss") : n;
                    list.Add((f, when));
                }
        }
        if (doc.FilePath != null && File.Exists(doc.FilePath) && doc.Modified) list.Insert(0, (doc.FilePath, "마지막 저장"));
        var opts = list.Select((x, i) => (i.ToString(), x.label)).Append(("file", "다른 파일 고르기…")).ToArray();
        var v = Dlg.Form("버전 비교", new[]
        {
            Field.Select("which", "비교할 이전 판", opts[0].Item1, opts),
            Field.Info(list.Count == 0 ? "이 작품의 이전 저장본이 없습니다 (저장할 때마다 data/versions 에 보관됩니다). 다른 파일을 골라 비교할 수 있습니다." : "저장할 때마다 이전 판이 data/versions 에 보관됩니다."),
        }, "비교");
        if (v == null) return;
        string which = (string)v["which"], path, label;
        if (which == "file")
        {
            using var dlg = new OpenFileDialog { Filter = "ComicPad·이미지|*.cpad;*.png;*.jpg;*.jpeg;*.bmp|모든 파일|*.*", InitialDirectory = Story.ProjectsDir };
            if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
            path = dlg.FileName; label = Path.GetFileName(path);
        }
        else { var it = list[int.Parse(which)]; path = it.path; label = it.label; }
        Document other;
        try { other = path.EndsWith(".cpad", StringComparison.OrdinalIgnoreCase) ? CpadIO.Load(path) : FileCommands.FromImage(path); }
        catch (Exception ex) { App.Toast("열지 못했습니다: " + ex.Message); return; }
        Show(other, doc, label, "지금");
    }
}

public sealed class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
}
