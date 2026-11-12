// 웹 판 app/css/style.css 의 :root 값 그대로 (색·크기). 화면을 웹과 같게 맞출 때는 여기 값을 쓴다.
namespace ComicPadNative.UI;

public static class Theme
{
    static Color H(string hex) => ColorTranslator.FromHtml(hex);

    public static readonly Color Bg = H("#26262b");        // --bg
    public static readonly Color Panel = H("#2f2f35");     // --panel
    public static readonly Color Panel2 = H("#3a3a42");    // --panel2
    public static readonly Color Panel3 = H("#45454e");    // --panel3
    public static readonly Color Line = H("#4a4a54");      // --line
    public static readonly Color Border = H("#1c1c20");    // 상단·도구 막대 경계선
    public static readonly Color Text = H("#e8e8ec");      // --text
    public static readonly Color Muted = H("#9c9ca8");     // --muted
    public static readonly Color Accent = H("#e8a33d");    // --accent
    public static readonly Color AccentDim = Color.FromArgb((int)(0.28 * 255), 232, 163, 61);   // --accent-dim (반투명)
    public static readonly Color AccentDimOnPanel = Blend(Panel, Accent, 0.28);                  // 패널 위에 깐 --accent-dim
    public static readonly Color Blue = H("#2f7cf6");      // --blue
    public static readonly Color Danger = H("#e25555");    // --danger
    public static readonly Color StageBg = H("#4b4b52");   // --stage-bg
    public static readonly Color FgColor = H("#1b1b1f");   // --fg-color (주황 위 글자)
    public static readonly Color OnAccent = H("#1d1d1d");  // .tool-btn.on 글자

    // 크기 (px, 96dpi 기준 — 화면 배율은 WinForms 가 맞춤)
    public static int Btn => Large ? 48 : 40;              // --btn
    public static int Tool => Large ? 54 : 46;             // --tool
    public static int SideW => Large ? 330 : 300;          // --side-w
    public const int Radius = 8;                           // --radius
    public const int TopBarH = 44, StatusH = 28;
    public static bool Large;                              // body.large-ui (큰 버튼)

    public const string FontName = "Malgun Gothic";
    public const float FontPx = 13f;                       // 13px
    static Font font, bold, small;
    /// <summary>13px 맑은 고딕 (웹 body)</summary>
    public static Font Font => font ??= new Font(FontName, FontPx, FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font Bold => bold ??= new Font(FontName, FontPx, FontStyle.Bold, GraphicsUnit.Pixel);
    public static Font Small => small ??= new Font(FontName, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    public static Font Px(float px, bool b = false) => new(FontName, px, b ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

    public static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>둥근 사각형 경로</summary>
    public static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        if (rad <= 0) { p.AddRectangle(r); return p; }
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
