// 브러시 커서 모양 (웹 ui/cursor.js, 설정 brushCursor = nib | circle | cross | dot):
// 크기 원을 그리는 도구(브러시·지우개·손끝 등)의 가운데 표시를 실제 Windows 커서로. 펜촉은 왼쪽 아래 끝(1,31)이 그리는 점.
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class CursorLook
{
    static Cursor nib, cross, dot;

    public static Cursor For(ToolBase t)
    {
        if (t == null || !t.DrawsSizeCircle) return null;
        string look = App.Get("brushCursor", "nib");
        if (look == "dot") return dot ??= Make(9, 4, 4, DrawDot);
        if (look == "nib" && t.Id != "eraser") return nib ??= Make(32, 1, 31, DrawNib);
        return cross ??= Make(23, 11, 11, DrawCross);
    }

    public static void Install() => CanvasView.CursorOverride = For;
    public static void Apply() { var c = Shell2Ui.Canvas; if (c != null) c.Cursor = CanvasView.CursorOf(App.Tool); }

    static void DrawNib(Graphics g)
    {
        // 웹 판 NIB_PATH: M1.5 30.5 L4 21.5 L22 3.5 Q25 0.5 28.5 3.5 Q31.5 7 28.5 10 L10.5 28 Z
        using var path = new GraphicsPath();
        path.AddLine(1.5f, 30.5f, 4f, 21.5f); path.AddLine(4f, 21.5f, 22f, 3.5f);
        path.AddBezier(22f, 3.5f, 24f, 1.5f, 26.5f, 1.5f, 28.5f, 3.5f);
        path.AddBezier(28.5f, 3.5f, 30.5f, 5.8f, 30.5f, 8f, 28.5f, 10f);
        path.AddLine(28.5f, 10f, 10.5f, 28f); path.CloseFigure();
        using (var w = new Pen(Color.White, 3) { LineJoin = LineJoin.Round }) g.DrawPath(w, path);
        g.FillPath(Brushes.White, path);
        using (var b = new Pen(Color.Black, 1.2f) { LineJoin = LineJoin.Round }) g.DrawPath(b, path);
        g.FillPolygon(Brushes.Black, new[] { new PointF(1.5f, 30.5f), new PointF(4f, 21.5f), new PointF(10.5f, 28f) });
        using (var w1 = new Pen(Color.White, 1)) g.DrawLine(w1, 1.5f, 30.5f, 7.5f, 24.5f);
        g.FillEllipse(Brushes.White, 6.3f, 23.1f, 2.6f, 2.6f);
        using (var b1 = new Pen(Color.Black, 1)) g.DrawLine(b1, 17, 9, 23, 15);
    }
    static void DrawCross(Graphics g)
    {
        void Lines(Pen p) { g.DrawLine(p, 11.5f, 2, 11.5f, 9); g.DrawLine(p, 11.5f, 14, 11.5f, 21); g.DrawLine(p, 2, 11.5f, 9, 11.5f); g.DrawLine(p, 14, 11.5f, 21, 11.5f); }
        using (var w = new Pen(Color.White, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round }) Lines(w);
        using (var b = new Pen(Color.Black, 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) Lines(b);
        g.FillEllipse(Brushes.Black, 10.5f, 10.5f, 2, 2);
    }
    static void DrawDot(Graphics g)
    {
        g.FillEllipse(Brushes.White, 1, 1, 7, 7);
        g.FillEllipse(Brushes.Black, 2.3f, 2.3f, 4.4f, 4.4f);
    }

    static Cursor Make(int size, int hx, int hy, Action<Graphics> draw)
    {
        try
        {
            using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent); draw(g); }
            IntPtr hIcon = bmp.GetHicon();
            if (!GetIconInfo(hIcon, out var ii)) return null;
            ii.fIcon = false; ii.xHotspot = hx; ii.yHotspot = hy;
            IntPtr hCur = CreateIconIndirect(ref ii);
            DeleteObject(ii.hbmColor); DeleteObject(ii.hbmMask); DestroyIcon(hIcon);
            return hCur == IntPtr.Zero ? null : new Cursor(hCur);
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO info);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
}
