// 브러시 화면 부품: 값 막대(표시 형식 지정), 여러 칸 고르기, 펜 입력 칸, 필압 곡선 편집기, 브러시 미리보기 (웹 ui/brushpreview.js)
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.BrushEngine;

public static class BUI
{
    public const int Width = 270;
    public static string Pct(double v) => $"{Math.Round(v * 100)}%";
    public static string OffOr(double v, Func<double, string> f) => v != 0 ? f(v) : "끔";

    /// <summary>이름 + 끌어서 바꾸는 막대 + 값 (fmt 로 표시 형식)</summary>
    public static Control Slider(string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> fmt = null, bool log = false, double step = 0)
    {
        var box = new Panel { Width = Width, Height = 44, BackColor = Opt.Bg, Margin = new Padding(0, 2, 0, 2) };
        var name = new Label { Text = label, ForeColor = Opt.Fg, AutoSize = false, Width = Width - 72, Height = 16, Location = new Point(0, 2), AutoEllipsis = true };
        var val = new Label { ForeColor = Opt.Muted, AutoSize = false, Width = 72, TextAlign = ContentAlignment.TopRight, Location = new Point(Width - 72, 2) };
        var tb = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Width = Width, Location = new Point(0, 18), AutoSize = false, Height = 24 };
        new ToolTip().SetToolTip(name, label);
        bool lg = log && min > 0;
        double ToT(double v) => lg ? Math.Log(Math.Max(min, v) / min) / Math.Log(max / min) : (v - min) / (max - min);
        double FromT(double t) { double v = lg ? min * Math.Pow(max / min, t) : min + (max - min) * t; if (step > 0) v = Math.Round(v / step) * step; return Math.Clamp(v, min, max); }
        fmt ??= v => Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
        bool busy = false;
        void Sync() { if (box.IsDisposed) return; busy = true; double v = get(); tb.Value = (int)Math.Clamp(ToT(v) * 1000, 0, 1000); val.Text = fmt(v); busy = false; }
        tb.Scroll += (_, _) => { if (busy) return; double v = FromT(tb.Value / 1000.0); set(v); val.Text = fmt(v); };
        tb.MouseWheel += (_, e) => { if (e is HandledMouseEventArgs h) h.Handled = true; };   // 패널 굴림에 값이 바뀌지 않게
        box.Controls.AddRange(new Control[] { name, val, tb });
        Sync();
        Action onS = () => { if (!tb.Capture) Sync(); };
        App.SettingsChanged += onS; PresetStore.Changed += onS;
        box.Disposed += (_, _) => { App.SettingsChanged -= onS; PresetStore.Changed -= onS; };
        return box;
    }

    public static CheckBox Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = label, Checked = get(), ForeColor = Opt.Fg, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        // 긴 글은 줄을 바꿔 패널 너비 안에
        var sz = TextRenderer.MeasureText(label, SystemFonts.MessageBoxFont);
        if (sz.Width > Width - 24)
        {
            c.AutoSize = false; c.Width = Width;
            c.Height = TextRenderer.MeasureText(label, SystemFonts.MessageBoxFont, new Size(Width - 24, 0), TextFormatFlags.WordBreak).Height + 6;
            c.CheckAlign = ContentAlignment.TopLeft; c.TextAlign = ContentAlignment.TopLeft;
        }
        c.CheckedChanged += (_, _) => set(c.Checked);
        return c;
    }

    /// <summary>여러 칸 중 하나 고르기 (웹 segmented)</summary>
    public static Control Seg<T>((T id, string text)[] items, Func<T> get, Action<T> set)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(Width, 0), AutoSize = true, BackColor = Opt.Bg, Margin = new Padding(0, 2, 0, 2) };
        var btns = new List<RadioButton>();
        foreach (var (id, text) in items)
        {
            var b = new RadioButton { Text = text, Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Opt.Fg, BackColor = Opt.Bg2, Margin = new Padding(0, 0, 3, 3), Tag = id, TextAlign = ContentAlignment.MiddleCenter };
            b.FlatAppearance.CheckedBackColor = Opt.Accent; b.FlatAppearance.BorderColor = Color.FromArgb(74, 74, 84);
            b.Checked = Equals(get(), id);
            b.ForeColor = b.Checked ? Color.Black : Opt.Fg;
            b.CheckedChanged += (_, _) =>
            {
                b.ForeColor = b.Checked ? Color.Black : Opt.Fg;
                if (b.Checked && !Equals(get(), (T)b.Tag)) set((T)b.Tag);
            };
            btns.Add(b); p.Controls.Add(b);
        }
        return p;
    }

    public static Control Select(string label, (string id, string text)[] items, Func<string> get, Action<string> set)
    {
        var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Opt.Bg, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
        if (label != null) box.Controls.Add(new Label { Text = label, ForeColor = Opt.Fg, AutoSize = true });
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Width - 10, BackColor = Opt.Bg2, ForeColor = Opt.Fg, FlatStyle = FlatStyle.Flat };
        foreach (var (_, t) in items) cb.Items.Add(t);
        cb.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.id == get()));
        cb.SelectedIndexChanged += (_, _) => { if (cb.SelectedIndex >= 0) set(items[cb.SelectedIndex].id); };
        cb.MouseWheel += (_, e) => { if (!cb.DroppedDown && e is HandledMouseEventArgs h) h.Handled = true; };
        box.Controls.Add(cb);
        return box;
    }

    public static Control Section(string title, params Control[] items)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Opt.Bg, Margin = new Padding(0, 4, 0, 4) };
        p.Controls.Add(Opt.Title(title));
        foreach (var c in items) if (c != null) p.Controls.Add(c);
        return p;
    }

    public static Label Help(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(Width, 0), ForeColor = Opt.Muted, Margin = new Padding(0, 0, 0, 4) };

    /// <summary>한 줄 글 입력 (웹 prompt)</summary>
    public static string Prompt(string title, string value)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), ShowInTaskbar = false };
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        p.Controls.Add(new Label { Text = title, AutoSize = true });
        var tb = new TextBox { Text = value ?? "", Width = 280, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
        p.Controls.Add(tb);
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        p.Controls.Add(Opt.Row(ok, cancel));
        f.Controls.Add(p); f.AcceptButton = ok; f.CancelButton = cancel;
        f.Shown += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return f.ShowDialog(App.Main) == DialogResult.OK ? tb.Text.Trim() : null;
    }

    /// <summary>미리 곱한 면의 영역 → 흰 바탕 위 그림</summary>
    public static Bitmap ToBitmap(Surface s, Rect r, Color? bg = null)
    {
        var bmp = new Bitmap(r.W, r.H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var px = s.Read(r);
        uint b = bg.HasValue ? (uint)bg.Value.ToArgb() : 0xFFFFFFFF;
        var data = bmp.LockBits(new Rectangle(0, 0, r.W, r.H), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var row = new int[r.W];
        for (int y = 0; y < r.H; y++)
        {
            for (int x = 0; x < r.W; x++) row[x] = (int)(Px.Over(px[y * r.W + x], b) | 0xFF000000);
            Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, r.W);
        }
        bmp.UnlockBits(data);
        return bmp;
    }
}

/// <summary>펜·마우스를 받는 칸 (필압 포함). Windows 포인터 메시지를 직접 읽음</summary>
public class PenInputControl : Control
{
    public event Action<double, double, double, bool> PenDown, PenMove, PenUp;   // x, y, 필압, 펜인지
    public event Action<double> Hover;                                          // 펜을 띄운 채 / 누른 채 지금 필압 (곡선 편집기)
    bool down;

    public PenInputControl() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

    protected override void WndProc(ref Message m)
    {
        const int WM_POINTERUPDATE = 0x245, WM_POINTERDOWN = 0x246, WM_POINTERUP = 0x247;
        if (m.Msg == WM_POINTERDOWN || m.Msg == WM_POINTERUPDATE || m.Msg == WM_POINTERUP)
        {
            uint id = (uint)((long)m.WParam & 0xFFFF);
            if (GetPointerType(id, out int type) && GetPointerInfo(id, out var info))
            {
                var c = PointToClient(new Point(info.ptPixelLocation.X, info.ptPixelLocation.Y));
                bool pen = type == 3;
                double pr = 0.6;
                if (pen && GetPointerPenInfo(id, out var pp)) pr = (pp.penMask & 1) != 0 ? pp.pressure / 1024.0 : 0.5;
                bool contact = (info.pointerFlags & 4) != 0;
                if (pen) Hover?.Invoke(contact || m.Msg != WM_POINTERUP ? pr : 0);
                if (m.Msg == WM_POINTERDOWN) { down = true; Capture = true; PenDown?.Invoke(c.X, c.Y, pr, pen); }
                else if (m.Msg == WM_POINTERUP) { if (down) { down = false; Capture = false; PenUp?.Invoke(c.X, c.Y, pr, pen); } }
                else if (down && contact) PenMove?.Invoke(c.X, c.Y, pr, pen);
                m.Result = IntPtr.Zero;
                return;
            }
        }
        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public int pointerType; public uint pointerId; public uint frameId; public int pointerFlags;
        public IntPtr sourceDevice; public IntPtr hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount; public int InputData; public uint dwKeyStates; public ulong PerformanceCount; public int ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)] struct POINTER_PEN_INFO { public POINTER_INFO pointerInfo; public int penFlags, penMask; public uint pressure, rotation; public int tiltX, tiltY; }
    [DllImport("user32.dll")] static extern bool GetPointerType(uint id, out int type);
    [DllImport("user32.dll")] static extern bool GetPointerInfo(uint id, out POINTER_INFO info);
    [DllImport("user32.dll")] static extern bool GetPointerPenInfo(uint id, out POINTER_PEN_INFO info);
}

/// <summary>필압 곡선 편집기: 점을 끌어 옮기고, 빈 곳을 두 번 탭하면 점 추가, 점을 두 번 탭하면 지우기</summary>
public sealed class CurveEditor : FlowLayoutPanel
{
    const int S = 180, PAD = 8;
    readonly Func<double[][]> get; readonly Action<double[][]> set;
    readonly PenInputControl c = new() { Width = S, Height = S, Margin = new Padding(0, 0, 6, 0) };
    List<double[]> pts;
    int drag = -1; double? live; long lastTap;

    public CurveEditor(string title, Func<double[][]> get, Action<double[][]> set)
    {
        this.get = get; this.set = set;
        FlowDirection = FlowDirection.TopDown; AutoSize = true; WrapContents = false; BackColor = Opt.Bg; Margin = new Padding(0, 4, 0, 4);
        Load();
        c.Paint += (_, e) => Draw(e.Graphics);
        c.PenDown += (x, y, pr, pen) => OnDown(x, y);
        c.PenMove += (x, y, pr, pen) => { if (pen) live = pr; OnMove(x, y); };
        c.PenUp += (x, y, pr, pen) => { if (drag >= 0) Commit(); drag = -1; };
        c.Hover += pr => { live = pr; c.Invalidate(); };
        var presetSel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = BUI.Width - 10, BackColor = Opt.Bg2, ForeColor = Opt.Fg, FlatStyle = FlatStyle.Flat };
        presetSel.Items.Add("곡선 고르기…");
        foreach (var (n, _) in Pressure.Presets) presetSel.Items.Add(n);
        presetSel.SelectedIndex = 0;
        presetSel.SelectedIndexChanged += (_, _) =>
        {
            if (presetSel.SelectedIndex <= 0) return;
            pts = Pressure.Presets[presetSel.SelectedIndex - 1].pts.Select(p => (double[])p.Clone()).ToList();
            presetSel.SelectedIndex = 0; c.Invalidate(); Commit();
        };
        Controls.Add(Opt.Title(title));
        Controls.Add(c);
        Controls.Add(presetSel);
        Controls.Add(BUI.Help("점을 끌어 모양 바꾸기 · 빈 곳 두 번 탭 = 점 추가 · 점 두 번 탭 = 지우기. 왼쪽 = 약하게 누름, 오른쪽 = 세게. 위로 갈수록 굵게·진하게."));
        Controls.Add(Opt.Button("곧게 (초기화)", () => { pts = new() { new[] { 0.0, 0 }, new[] { 1.0, 1 } }; c.Invalidate(); Commit(); }));
    }

    void Load() => pts = (get() ?? new[] { new[] { 0.0, 0 }, new[] { 1.0, 1 } }).Select(p => (double[])p.Clone()).ToList();
    public void Redraw() { Load(); c.Invalidate(); }

    static PointF ToXY(double u, double v) => new((float)(PAD + u * (S - 2 * PAD)), (float)(S - PAD - v * (S - 2 * PAD)));
    static double[] FromXY(double px, double py) => new[] { Math.Clamp((px - PAD) / (S - 2 * PAD), 0, 1), Math.Clamp((S - PAD - py) / (S - 2 * PAD), 0, 1) };
    int Hit(double x, double y) => pts.FindIndex(p => { var q = ToXY(p[0], p[1]); return Math.Sqrt((q.X - x) * (q.X - x) + (q.Y - y) * (q.Y - y)) < 12; });

    void Commit()
    {
        bool id = pts.Count == 2 && pts[0][0] == 0 && pts[0][1] == 0 && pts[1][0] == 1 && pts[1][1] == 1;
        set(id ? null : pts.Select(p => (double[])p.Clone()).ToArray());
    }

    void OnDown(double x, double y)
    {
        int i = Hit(x, y); long now = Environment.TickCount64; bool dbl = now - lastTap < 350; lastTap = now;
        if (dbl && i > 0 && i < pts.Count - 1) { pts.RemoveAt(i); drag = -1; c.Invalidate(); Commit(); return; }
        if (i >= 0) { drag = i; return; }
        if (dbl)
        {
            var p = FromXY(x, y);
            pts.Add(p); pts = pts.OrderBy(q => q[0]).ToList(); drag = pts.IndexOf(p); c.Invalidate(); Commit();
        }
    }

    void OnMove(double x, double y)
    {
        if (drag < 0) { c.Invalidate(); return; }
        var uv = FromXY(x, y); double u = uv[0], v = uv[1];
        if (drag == 0) u = 0; else if (drag == pts.Count - 1) u = 1;
        else u = Math.Min(pts[drag + 1][0] - 0.02, Math.Max(pts[drag - 1][0] + 0.02, u));
        pts[drag] = new[] { Math.Round(u, 3), Math.Round(v, 3) };
        c.Invalidate();
    }

    void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Opt.Bg2);
        using var grid = new Pen(Color.FromArgb(90, 128, 128, 128));
        for (int i = 0; i <= 4; i++) { float q = PAD + i * (S - 2 * PAD) / 4f; g.DrawLine(grid, q, PAD, q, S - PAD); g.DrawLine(grid, PAD, q, S - PAD, q); }
        using (var dash = new Pen(Color.FromArgb(90, 128, 128, 128)) { DashPattern = new[] { 3f, 3f } }) g.DrawLine(dash, ToXY(0, 0), ToXY(1, 1));
        var lut = Pressure.CurveLut(pts.ToArray());
        var line = new List<PointF>();
        for (int i = 0; i < 256; i += 3) line.Add(ToXY(i / 255.0, lut[i]));
        line.Add(ToXY(1, lut[255]));
        using (var cp = new Pen(Opt.Accent, 2.5f)) g.DrawLines(cp, line.ToArray());
        using var wb = new SolidBrush(Color.White); using var ap = new Pen(Opt.Accent, 2);
        foreach (var p in pts) { var q = ToXY(p[0], p[1]); g.FillEllipse(wb, q.X - 5, q.Y - 5, 10, 10); g.DrawEllipse(ap, q.X - 5, q.Y - 5, 10, 10); }
        if (live is double lv && lv > 0)
        {
            var q = ToXY(lv, lut[(int)Math.Floor(Math.Clamp(lv, 0, 1) * 255 + 0.5)]);
            using var bb = new SolidBrush(Color.FromArgb(61, 123, 224)); g.FillEllipse(bb, q.X - 4, q.Y - 4, 8, 8);
        }
    }
}

/// <summary>브러시 미리보기 (웹 previewStroke · presetThumb)</summary>
public static class BrushPreview
{
    static readonly Dictionary<string, Bitmap> thumbs = new();

    /// <summary>브러시 한 획을 그려 본 면 (필압 0 → 1 → 0, S자). 크기는 칸에 맞게 줄여서</summary>
    public static Surface Stroke(BrushPreset preset, int w, int h, uint? color = null, bool erase = false, bool fit = true)
    {
        if (preset.Tip == "pixel" && fit && w >= 32)
        {
            // 픽셀 끝: 작게 그려서 4배로 (픽셀이 보이게)
            const int k = 4;
            var sp = preset.Clone(); sp.Size = Math.Min(preset.Size, 6);
            var small = Stroke(sp, (int)Math.Round(w / (double)k), (int)Math.Round(h / (double)k), color, erase, false);
            var big = new Surface(w, h);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) big.Set(x, y, small.Get(x / k, y / k));
            return big;
        }
        var outS = new Surface(w, h);
        double s = fit ? Math.Min(1, h * 0.42 / Math.Max(1, preset.Size)) : 1;
        var p = preset.Clone();
        p.Size = Math.Max(0.6, preset.Size * s); p.TaperIn *= s; p.TaperOut *= s; p.Stabilizer = 0; p.Mix = 0;
        Func<int, int, uint> sample = null;
        if (preset.Shading)
        {
            // 명암 잉크: 팔레트 가운데 밝기 색 띠 위에 그려 봄 (밑 색이 있어야 보임)
            var pal = PixelArt.PaletteOf(null).OrderBy(PixelArt.Lum).ToList();
            var mid = pal.Count > 0 ? pal[pal.Count / 2] : new[] { 136, 136, 136 };
            uint mc = Px.Pack(255, (uint)mid[0], (uint)mid[1], (uint)mid[2]);
            for (int y = (int)Math.Round(h * 0.2); y < (int)Math.Round(h * 0.8); y++) for (int x = 0; x < w; x++) outS.Set(x, y, mc);
            var baseS = outS.Clone();
            sample = (x, y) => baseS.Get(x, y);
        }
        try
        {
            uint col = erase ? 0xFF777777 : color ?? (p.Color != null ? Px.FromHex(p.Color) : App.Fg | 0xFF000000);
            var st = new BrushStroke(new StrokeSetup { W = w, H = h, Preset = p, Color = col, Sample = sample, Seed = 7, Palette = PixelArt.PaletteOf(null) });
            const int n = 70; double pad = Math.Min(16, w * 0.08);
            BPt last = default;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                var pt = new BPt(pad + (w - 2 * pad) * t, h / 2.0 - Math.Sin(t * Math.PI * 2) * h * 0.22, Math.Max(0.04, Math.Sin(Math.PI * t)), i * 9);
                if (i == 0) st.Begin(pt); else st.Add(pt);
                last = pt;
            }
            var r = st.End(last).Clip(w, h);
            if (!r.IsEmpty) new StrokeApply { Buf = st.Buf, Target = outS, Opacity = preset.Opacity, Post = p.HasPost ? p : null }.Apply(r);
        }
        catch { /* 미리보기 실패: 빈 칸 */ }
        return outS;
    }

    public static Bitmap Bitmap(BrushPreset preset, int w, int h, uint? color = null, bool erase = false)
        => BUI.ToBitmap(Stroke(preset, w, h, color, erase), new Rect(0, 0, w, h));

    /// <summary>목록용 작은 미리보기 (같은 설정이면 다시 그리지 않음)</summary>
    public static Bitmap Thumb(BrushPreset preset, uint color, int w = 96, int h = 26, bool erase = false)
    {
        string key = preset.ToJson().ToJsonString() + color + w + "x" + h + erase + App.Settings["pressurePts"]?.ToJsonString() + App.Settings["pressureGamma"]?.ToJsonString();
        if (thumbs.TryGetValue(key, out var b)) return b;
        if (thumbs.Count > 120) { foreach (var v in thumbs.Values) v.Dispose(); thumbs.Clear(); }
        return thumbs[key] = Bitmap(preset, w, h, color, erase);
    }
}

/// <summary>시험 그리기 칸: 미리보기 한 획 + 마음대로 그려 보는 칸 (그림에는 안 들어감)</summary>
public sealed class BrushTestPad : FlowLayoutPanel
{
    const int W = BUI.Width - 6, PH = 64, H = 120;
    readonly Func<BrushPreset> getPreset; readonly bool erase;
    readonly PictureBox big = new() { Width = W, Height = PH, Margin = new Padding(0, 0, 0, 2), BackColor = Color.White };
    readonly PenInputControl pad = new() { Width = W, Height = H, Margin = new Padding(0, 0, 0, 2), BackColor = Color.White, Cursor = Cursors.Cross };
    readonly Label info = BUI.Help("여기에 펜으로 그려 보세요 (그림에는 안 들어감)");
    Surface done = new(W, H);
    BrushStroke st; BPt last; long t0;

    public BrushTestPad(Func<BrushPreset> getPreset, bool erase)
    {
        this.getPreset = getPreset; this.erase = erase;
        FlowDirection = FlowDirection.TopDown; AutoSize = true; WrapContents = false; BackColor = Opt.Bg;
        pad.Paint += (_, e) => Show(e.Graphics);
        pad.PenDown += (x, y, pr, pen) =>
        {
            var p = getPreset().Clone();
            p.Size = Math.Min(p.Size, H * 0.6); p.Mix = 0;      // 칸이 작으니 아주 큰 브러시는 줄여서
            uint col = erase ? 0xFF888888 : p.Color != null ? Px.FromHex(p.Color) : App.Fg | 0xFF000000;
            st = new BrushStroke(new StrokeSetup { W = W, H = H, Preset = p, Color = col });
            t0 = Environment.TickCount64;
            last = new BPt(x, y, pen ? pr : 0.6, 0);
            st.Begin(last); pad.Invalidate();
        };
        pad.PenMove += (x, y, pr, pen) =>
        {
            if (st == null) return;
            last = new BPt(x, y, pen ? (pr > 0 ? pr : last.P) : 0.6, Environment.TickCount64 - t0);
            st.Add(last); pad.Invalidate();
        };
        pad.PenUp += (x, y, pr, pen) =>
        {
            if (st == null) return;
            var r = st.End(last).Clip(W, H);
            if (!r.IsEmpty) new StrokeApply { Buf = st.Buf, Target = done, Opacity = getPreset().Opacity, Post = getPreset().HasPost ? getPreset() : null }.Apply(r);
            st = null; pad.Invalidate();
            info.Text = "지우려면 \"칸 비우기\"";
        };
        Controls.Add(big); Controls.Add(pad);
        Controls.Add(info);
        Controls.Add(Opt.Button("칸 비우기", () => { done = new Surface(W, H); pad.Invalidate(); info.Text = "여기에 펜으로 그려 보세요 (그림에는 안 들어감)"; }));
        Refresh2();
    }

    void Show(Graphics g)
    {
        Surface s = done;
        if (st != null)
        {
            s = done.Clone();
            new StrokeApply { Buf = st.Buf, Target = s, Opacity = getPreset().Opacity }.Apply(new Rect(0, 0, W, H));
        }
        using var bmp = BUI.ToBitmap(s, new Rect(0, 0, W, H));
        g.DrawImageUnscaled(bmp, 0, 0);
    }

    public void Refresh2()
    {
        var old = big.Image;
        big.Image = BrushPreview.Bitmap(getPreset(), W, PH, null, erase);
        old?.Dispose();
    }
}
