// 명도 확인 보기 (웹 판 ui/viewaids.js): 그림은 그대로 두고 화면만 흑백 / 5단계 명암 덩어리로.
// 네이티브 판 그림판(CanvasView)에는 화면 후처리 연결점이 없어서, 옆에 띄우는 "명도 확인" 창에 그림 전체를 같은 방식으로 보여 준다.
// 그림이 바뀌면 바뀐 부분만 다시 줄여 그림 (App.Invalidated).
using System.Drawing.Imaging;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public static class ViewAids
{
    static readonly string[] Modes = { "off", "gray", "steps" };
    public static string Mode = "off";
    static LumaWindow win;

    // 웹 판 SVG 필터와 같은 값: 루마 0.299/0.587/0.114, 5단계 tableValues
    static readonly double[] StepTable = { 0.08, 0.3, 0.52, 0.74, 0.96 };
    public static byte Luma(uint c)
    {
        // c: 불투명 BGRA (흰 바탕 위로 합친 색)
        double v = ((c >> 16) & 255) * 0.299 + ((c >> 8) & 255) * 0.587 + (c & 255) * 0.114;
        return (byte)Math.Clamp(Math.Round(v), 0, 255);
    }
    public static byte Steps(byte l)
    {
        int k = Math.Min(StepTable.Length - 1, (int)Math.Floor(l / 255.0 * StepTable.Length));
        return (byte)Math.Round(StepTable[k] * 255);
    }

    public static void Cycle() => Set(Modes[(Array.IndexOf(Modes, Mode) + 1) % Modes.Length]);

    public static void Set(string mode)
    {
        Mode = mode;
        if (mode == "off") { win?.Close(); win = null; }
        else
        {
            if (win == null || win.IsDisposed) { win = new LumaWindow(); win.FormClosed += (_, _) => { win = null; Mode = "off"; }; win.Show(App.Main); }
            win.Refill();
        }
        App.Toast(mode switch { "gray" => "명도 보기: 흑백 (그림은 그대로)", "steps" => "명도 보기: 5단계 명암 덩어리", _ => "명도 보기 끔" });
    }

    /// <summary>문서 → 줄인 명도 그림 (시험·창 공용)</summary>
    public static unsafe void RenderInto(Bitmap dst, Document d, Rect docRect, double s, string mode, Compositor comp = null)
    {
        comp ??= App.Comp;
        // 줄인 그림의 칸 범위
        int px0 = Math.Max(0, (int)Math.Floor(docRect.X * s)), py0 = Math.Max(0, (int)Math.Floor(docRect.Y * s));
        int px1 = Math.Min(dst.Width, (int)Math.Ceiling(docRect.R * s)), py1 = Math.Min(dst.Height, (int)Math.Ceiling(docRect.B * s));
        if (px1 <= px0 || py1 <= py0) return;
        var bd = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            // 줄 단위 띠로 합성 (메모리 적게)
            int band = Math.Max(1, (int)(64 * s) + 1);
            for (int by = py0; by < py1; by += band)
            {
                int by1 = Math.Min(py1, by + band);
                int dx0 = (int)Math.Floor(px0 / s), dx1 = Math.Min(d.W, (int)Math.Ceiling(px1 / s));
                int dy0 = (int)Math.Floor(by / s), dy1 = Math.Min(d.H, (int)Math.Ceiling(by1 / s));
                var r = Rect.FromLTRB(dx0, dy0, dx1, dy1);
                if (r.IsEmpty) continue;
                var buf = comp.Render(d, r, new RenderOptions { Paper = true });
                for (int py = by; py < by1; py++)
                {
                    uint* row = (uint*)((byte*)bd.Scan0 + py * bd.Stride);
                    int sy0 = Math.Max(r.Y, (int)Math.Floor(py / s)), sy1 = Math.Min(r.B, Math.Max(sy0 + 1, (int)Math.Floor((py + 1) / s)));
                    for (int px = px0; px < px1; px++)
                    {
                        int sx0 = Math.Max(r.X, (int)Math.Floor(px / s)), sx1 = Math.Min(r.R, Math.Max(sx0 + 1, (int)Math.Floor((px + 1) / s)));
                        long acc = 0; int n = 0;
                        for (int y = sy0; y < sy1; y++)
                            for (int x = sx0; x < sx1; x++)
                            {
                                uint c = buf[(y - r.Y) * r.W + (x - r.X)];
                                if ((c >> 24) != 255) c = Px.Over(c, 0xFFFFFFFF);
                                acc += Luma(c); n++;
                            }
                        byte l = n == 0 ? (byte)255 : (byte)(acc / n);
                        if (mode == "steps") l = Steps(l);
                        row[px] = 0xFF000000u | ((uint)l << 16) | ((uint)l << 8) | l;
                    }
                }
            }
        }
        finally { dst.UnlockBits(bd); }
    }

    sealed class LumaWindow : Form
    {
        Bitmap img; double s; Document doc;
        Rect dirty = Rect.Empty;
        readonly System.Windows.Forms.Timer t = new() { Interval = 350 };
        readonly DoubleBufferedPanel view = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(60, 60, 66) };
        readonly Button modeBtn;

        public LumaWindow()
        {
            Text = "명도 확인 보기"; Width = 420; Height = 560; FormBorderStyle = FormBorderStyle.SizableToolWindow; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; BackColor = Opt.Bg; ForeColor = Opt.Fg; Font = new Font("Malgun Gothic", 9f);
            var wa = Screen.FromControl(App.Main).WorkingArea; Location = new Point(wa.Right - Width - 340, wa.Top + 80);
            modeBtn = Opt.Button("", Cycle); modeBtn.Dock = DockStyle.Top;
            Controls.Add(view); Controls.Add(modeBtn);
            view.Paint += (_, e) =>
            {
                if (img == null) return;
                var box = Rectangle.Inflate(view.ClientRectangle, -6, -6);
                float k = Math.Min(box.Width / (float)img.Width, box.Height / (float)img.Height);
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                e.Graphics.DrawImage(img, box.X + (box.Width - img.Width * k) / 2, box.Y + (box.Height - img.Height * k) / 2, img.Width * k, img.Height * k);
            };
            App.Invalidated += OnInv;
            App.DocChanged += Refill;
            t.Tick += (_, _) => { t.Stop(); Flush(); };
            FormClosed += (_, _) => { App.Invalidated -= OnInv; App.DocChanged -= Refill; t.Dispose(); img?.Dispose(); };
        }
        void OnInv(Rect r) { if (r.IsEmpty || IsDisposed) return; dirty = dirty.Union(r); t.Stop(); t.Start(); }

        public void Refill()
        {
            doc = App.Doc;
            modeBtn.Text = Mode == "steps" ? "지금: 5단계 명암 (누르면 끔)" : "지금: 흑백 (누르면 5단계)";
            if (doc == null) return;
            s = Math.Min(1, 900.0 / Math.Max(doc.W, doc.H));
            img?.Dispose();
            img = new Bitmap(Math.Max(1, (int)Math.Round(doc.W * s)), Math.Max(1, (int)Math.Round(doc.H * s)), PixelFormat.Format32bppArgb);
            dirty = new Rect(0, 0, doc.W, doc.H);
            Flush();
        }
        void Flush()
        {
            if (doc == null || doc != App.Doc || img == null) { Refill(); return; }
            var r = dirty.Clip(doc.W, doc.H); dirty = Rect.Empty;
            if (r.IsEmpty) return;
            RenderInto(img, doc, r, s, Mode);
            view.Invalidate();
        }
    }
}
