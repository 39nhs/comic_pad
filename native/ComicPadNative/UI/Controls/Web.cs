// 웹 판 app/css/style.css 의 부품 모양을 그리는 공용 도구 (색·글꼴·둥근 칸·글자). 모든 웹식 컨트롤이 이것으로 그린다.
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace ComicPadNative.UI.Controls;

public static class Web
{
    static Color H(string hex) => ColorTranslator.FromHtml(hex);

    // ---- CSS 에 직접 쓰인 색 (Theme 에 없는 것) -------------------------------------------------
    public static readonly Color Field = H("#222227");        // select, input 바탕
    public static readonly Color Sep = H("#38383f");          // .section 아래 줄, .layer-toolbar 아래 줄
    public static readonly Color ModalLine = H("#55555f");    // .modal 테두리, .menu 테두리
    public static readonly Color ModalSep = H("#3c3c44");     // .modal-head 아래·.modal-actions 위 줄
    public static readonly Color SegBg = H("#26262b");        // .seg 바탕
    public static readonly Color ToggleOff = H("#555555");    // .tg-box 꺼짐
    public static readonly Color KnobOff = H("#dddddd");
    public static readonly Color OnDark = H("#1d1d1d");       // 주황 위 글자 (.btn.primary)
    public static readonly Color ActiveRow = H("#4a4030");    // .layer.on, .preset.on 바탕
    public static readonly Color Danger = H("#ff8a8a");       // .btn.danger 글자
    public static readonly Color MenuBg = H("#34343b");       // .menu 바탕
    public static readonly Color PaperRow = H("#2a2a2f");     // .layer.paper
    public static readonly Color FolderRow = H("#34343c");    // .layer.folder
    public static readonly Color MultiRow = H("#2e3a52"), MultiRowOn = H("#3d4660"), MultiBar = H("#26324a");
    public static readonly Color DockTabs = H("#29292e");

    // ---- 글꼴 (px) --------------------------------------------------------------------------------
    static readonly Dictionary<(float, bool), Font> fonts = new();
    public static Font F(float px, bool bold = false)
    {
        lock (fonts)
        {
            if (!fonts.TryGetValue((px, bold), out var f)) fonts[(px, bold)] = f = new Font(Theme.FontName, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            return f;
        }
    }
    public static Font Body => F(13);
    public static Font BodyBold => F(13, true);
    public static Font Small => F(12);
    public static Font Tiny => F(11);

    // ---- 그리기 ----------------------------------------------------------------------------------
    public static void Smooth(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; }

    /// <summary>둥근 칸 칠하기 (+ 테두리 1px, 웹 border 처럼 안쪽에)</summary>
    public static void Box(Graphics g, Rectangle r, float radius, Color? fill, Color? border = null, float borderW = 1f)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var st = g.Save();
        Smooth(g);
        if (fill is Color fc && fc.A > 0)
        {
            using var p = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), radius);
            using var b = new SolidBrush(fc);
            g.FillPath(b, p);
        }
        if (border is Color bc && bc.A > 0)
        {
            float h = borderW / 2;
            using var p = Theme.Round(new RectangleF(r.X + h, r.Y + h, r.Width - borderW, r.Height - borderW), Math.Max(0, radius - h));
            using var pen = new Pen(bc, borderW);
            g.DrawPath(pen, p);
        }
        g.Restore(st);
    }

    public const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    public const TextFormatFlags Right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    public const TextFormatFlags Wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

    public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags = Left) => TextRenderer.DrawText(g, s ?? "", f, r, c, flags);

    public static Size Measure(string s, Font f) => string.IsNullOrEmpty(s) ? new Size(0, f.Height) : TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    public static Size MeasureWrap(string s, Font f, int width) => string.IsNullOrEmpty(s) ? new Size(0, f.Height) : TextRenderer.MeasureText(s, f, new Size(Math.Max(1, width), int.MaxValue), Wrap);

    /// <summary>체크무늬 (투명 표시) — 웹 repeating-conic-gradient(#555 0 25%, #666 0 50%) 0 0/10px 10px</summary>
    public static void Checker(Graphics g, Rectangle r, int cell = 5, Color? a = null, Color? b = null)
    {
        using var ba = new SolidBrush(a ?? H("#555555"));
        using var bb = new SolidBrush(b ?? H("#666666"));
        var st = g.Save();
        g.SetClip(r);
        for (int y = r.Y; y < r.Bottom; y += cell)
            for (int x = r.X; x < r.Right; x += cell)
                g.FillRectangle(((x - r.X) / cell + (y - r.Y) / cell) % 2 == 0 ? ba : bb, x, y, cell, cell);
        g.Restore(st);
    }

    public static Color Mix(Color a, Color b, double t) => Theme.Blend(a, b, t);
    /// <summary>반투명 색을 바탕 위에 깐 결과</summary>
    public static Color Over(Color bg, Color fg) => Mix(bg, Color.FromArgb(255, fg), fg.A / 255.0);

    /// <summary>컨트롤 바탕을 부모 바탕색으로 (둥근 모서리 바깥)</summary>
    public static Color ParentBack(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent) if (p.BackColor.A == 255) return p.BackColor;
        return Theme.Panel;
    }

    // ---- 배치용 붙임 속성 ---------------------------------------------------------------------------
    sealed class Props { public bool? Inline; public bool Flex; public bool? Stretch; }
    static readonly ConditionalWeakTable<Control, Props> props = new();
    static Props P(Control c) => props.GetValue(c, _ => new Props());
    /// <summary>세로 쌓기 안에서 옆으로 이어 놓을지 (웹 inline-flex). 지정 안 하면 단추·켜기 = 이어 놓기</summary>
    public static T Inline<T>(this T c, bool v = true) where T : Control { P(c).Inline = v; return c; }
    /// <summary>줄(row) 안에서 남은 너비를 차지 (웹 flex: 1)</summary>
    public static T Flex<T>(this T c, bool v = true) where T : Control { P(c).Flex = v; return c; }
    /// <summary>세로 쌓기 안에서 폭을 꽉 채울지</summary>
    public static T Stretch<T>(this T c, bool v = true) where T : Control { P(c).Stretch = v; return c; }
    internal static bool? IsInline(Control c) => props.TryGetValue(c, out var p) ? p.Inline : null;
    internal static bool IsFlex(Control c) => props.TryGetValue(c, out var p) && p.Flex;
    internal static bool? IsStretch(Control c) => props.TryGetValue(c, out var p) ? p.Stretch : null;

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr h, string app, string idList);
    /// <summary>스크롤 막대를 어둡게 (Windows 10/11 DarkMode_Explorer)</summary>
    public static T DarkScroll<T>(this T c) where T : Control
    {
        void Apply() { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
        if (c.IsHandleCreated) Apply();
        c.HandleCreated += (_, _) => Apply();
        return c;
    }

    /// <summary>웹 명령 id (layer.new 등) 또는 메뉴 경로 실행</summary>
    public static void Run(string idOrPath)
    {
        string path = idOrPath;
        if (Modules.Shell2.WebIds.Map.TryGetValue(idOrPath, out var p)) path = p;
        if (Registry.Commands.TryGetValue(path, out var a) && a != null) MainForm.Run(a, path);
        else App.Toast("아직 없는 기능입니다: " + idOrPath);
    }
    public static bool Has(string idOrPath)
    {
        string path = Modules.Shell2.WebIds.Map.TryGetValue(idOrPath, out var p) ? p : idOrPath;
        return Registry.Commands.TryGetValue(path, out var a) && a != null;
    }
}

/// <summary>값을 다시 읽을 수 있는 부품 (웹 el.sync)</summary>
public interface ISyncable { void Sync(); }

/// <summary>진행 막대 (웹 .progress-bar: 높이 10, 둥근, #38383f 바탕, 주황 채움). Value &lt; 0 = 계속 도는 표시 (웹 .spinner 대신)</summary>
public class WebProgress : Control, IWebBlock
{
    double value = -1;
    int phase;
    readonly System.Windows.Forms.Timer anim = new() { Interval = 30 };
    public WebProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 10; Margin = new Padding(0, 6, 0, 6);
        anim.Tick += (_, _) => { phase = (phase + 4) % 1000; Invalidate(); };
        anim.Start();
    }
    /// <summary>0~1, 음수면 끝을 모름</summary>
    public double Value { get => value; set { this.value = value; if (value >= 0) anim.Stop(); else anim.Start(); Invalidate(); } }
    public override Size GetPreferredSize(Size proposed) => new(proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 200, 10);
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Web.ParentBack(this));
        var r = ClientRectangle;
        Web.Box(g, r, r.Height / 2f, Web.Sep);
        var st = g.Save();
        using (var clip = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), r.Height / 2f)) g.SetClip(clip);
        using var b = new SolidBrush(Theme.Accent);
        if (value >= 0) g.FillRectangle(b, 0, 0, (float)(r.Width * Math.Clamp(value, 0, 1)), r.Height);
        else { float w = r.Width * 0.3f, x = (float)((phase / 1000.0) * (r.Width + w)) - w; g.FillRectangle(b, x, 0, w, r.Height); }
        g.Restore(st);
    }
    protected override void Dispose(bool disposing) { if (disposing) anim.Dispose(); base.Dispose(disposing); }
}

/// <summary>직접 그리는 칸 (깜빡임 없이) — 웹 canvas 자리</summary>
public class WebCanvas : Control
{
    public WebCanvas() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
}
