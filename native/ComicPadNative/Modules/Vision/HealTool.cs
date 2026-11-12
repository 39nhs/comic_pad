// 복구 브러시 (웹 판 tools/heal.js 이식, 포토샵식): 원본의 결(무늬)을 가져오고, 색·밝기는 고칠 곳 주변에 맞춘다.
// 스팟 복구: 원본을 찍지 않아도 주변에서 테두리가 가장 비슷한 곳을 골라 씀. 계산은 획을 시작할 때의 그림 기준.
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Vision;

public sealed class HealTool : DabToolBase
{
    public override string Id => "heal";
    public override string Name => "복구 브러시";
    public override string Icon => "✚";
    public override bool AllowAltPick => false;
    protected override double SpacingK => 0.25;
    protected override string Label => Name;
    protected override double MaxHard => 0.97;

    public string Mode = "spot";             // spot | source
    public bool Aligned = true;
    (double x, double y)? source;
    (double x, double y)? offset;
    double hx, hy;                            // 마지막 커서 위치 (원본 표시용)

    public HealTool() { Size = 50; Hardness = 0.55; }

    public override void Down(PointerArgs e)
    {
        if (Mode == "source" && e.Alt) { source = (e.X, e.Y); offset = null; App.Toast("원본 위치를 정했습니다"); App.Invalidate(Rect.Empty); return; }
        if (Mode == "source" && source == null) { App.Toast("먼저 Alt+클릭으로 결을 가져올 원본을 찍으세요"); return; }
        base.Down(e);
    }

    protected override void Begin(PointerArgs e)
    {
        if (Mode == "source" && (offset == null || !Aligned)) offset = (source.Value.x - e.X, source.Value.y - e.Y);
        Dab(e.X, e.Y, 1);
    }

    public override void Hover(PointerArgs e) { hx = e.X; hy = e.Y; if (Mode == "source" && source != null) App.Invalidate(Rect.Empty); }
    public override void Move(PointerArgs e) { hx = e.X; hy = e.Y; base.Move(e); }

    /// <summary>획을 시작할 때의 픽셀 (고친 타일은 기록의 "전" 타일에서)</summary>
    uint Snap(int x, int y)
    {
        if ((uint)x >= (uint)target.W || (uint)y >= (uint)target.H) return 0;
        var t = edit.BeforeTile((y / Surface.TS) * target.TX + x / Surface.TS);
        return t == null ? 0 : t[(y % Surface.TS) * Surface.TS + x % Surface.TS];
    }
    uint[] SnapBlock(int X, int Y, int w, int h) { var o = new uint[w * h]; for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) o[y * w + x] = Snap(X + x, Y + y); return o; }

    /// <summary>스팟: 주변 8방향 후보 중 테두리가 가장 비슷한 곳</summary>
    (double x, double y) SpotOffset(double x, double y, int D)
    {
        uint[] Ring(double cx, double cy)
        {
            var v = new uint[24];
            for (int k = 0; k < 24; k++) { double a = k / 24.0 * Math.PI * 2; v[k] = Snap((int)Math.Round(cx + Math.Cos(a) * D * 0.45), (int)Math.Round(cy + Math.Sin(a) * D * 0.45)); }
            return v;
        }
        var here = Ring(x, y);
        (double, double)? best = null; double bestE = double.MaxValue;
        foreach (double dist in new[] { 1.1, 1.6 })
            for (int k = 0; k < 8; k++)
            {
                double a = k / 8.0 * Math.PI * 2, ox = Math.Cos(a) * D * dist, oy = Math.Sin(a) * D * dist, cx = x + ox, cy = y + oy;
                if (cx < D / 2.0 || cy < D / 2.0 || cx > target.W - D / 2.0 || cy > target.H - D / 2.0) continue;
                var v = Ring(cx, cy);
                double err = 0;
                for (int i = 0; i < 24; i++)
                    for (int s = 0; s < 32; s += 8) { double dd = (double)((v[i] >> s) & 255) - ((here[i] >> s) & 255); err += dd * dd; }
                if (err < bestE) { bestE = err; best = (ox, oy); }
            }
        return best ?? (D, 0);
    }

    protected override void Dab(double x, double y, double p)
    {
        int P = (int)Math.Round(D / 2.0), B = D + P * 2;           // 둘레 여유를 두고 계산
        int X = (int)Math.Round(x - D / 2.0), Y = (int)Math.Round(y - D / 2.0);
        var off = Mode == "source" ? offset.Value : SpotOffset(x, y, D);
        var dst = target.Read(new Rect(X - P, Y - P, B, B));
        var src = SnapBlock((int)Math.Round(X - P + off.x), (int)Math.Round(Y - P + off.y), B, B);
        var res = HealPatch(src, dst, B, B, D / 3.0);
        var tmp = new uint[D * D];
        for (int yy = 0; yy < D; yy++) for (int xx = 0; xx < D; xx++) tmp[yy * D + xx] = res[(yy + P) * B + xx + P];
        Stamp(tmp, X, Y, 1);
    }

    /// <summary>
    /// 복구 계산 (웹 판 healPatch): 원본 조각 src 의 결 + (고칠 곳 dst 의 저주파 색 − 원본의 저주파 색). 미리 곱한 ARGB.
    /// 알파는 둘 중 큰 쪽, 색은 알파를 넘지 않게.
    /// </summary>
    public static uint[] HealPatch(uint[] src, uint[] dst, int w, int h, double radius)
    {
        int n = w * h, r = Math.Max(1, (int)Math.Round(radius));
        var outC = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            int sh = 16 - c * 8;
            var S = new float[n]; var Dd = new float[n];
            for (int i = 0; i < n; i++) { S[i] = (src[i] >> sh) & 255; Dd[i] = (dst[i] >> sh) & 255; }
            var lS = BoxBlur(S, w, h, r); var lD = BoxBlur(Dd, w, h, r);
            var o = new float[n];
            for (int i = 0; i < n; i++) o[i] = S[i] + (lD[i] - lS[i]);
            outC[c] = o;
        }
        var res = new uint[n];
        for (int i = 0; i < n; i++)
        {
            uint a = Math.Max(src[i] >> 24, dst[i] >> 24);
            uint R = (uint)Math.Clamp((int)(outC[0][i] + 0.5f), 0, (int)a), G = (uint)Math.Clamp((int)(outC[1][i] + 0.5f), 0, (int)a), Bl = (uint)Math.Clamp((int)(outC[2][i] + 0.5f), 0, (int)a);
            res[i] = (a << 24) | (R << 16) | (G << 8) | Bl;
        }
        return res;
    }

    /// <summary>상자 흐림 (분리형, 두 번 = 가우스 근사) — 한 채널</summary>
    public static float[] BoxBlur(float[] a, int w, int h, int r, int passes = 2)
    {
        int k = 2 * r + 1;
        for (int p = 0; p < passes; p++)
        {
            var b = new float[a.Length]; var o = new float[a.Length];
            for (int y = 0; y < h; y++)
            {
                int row = y * w; float s = 0;
                for (int x = -r; x <= r; x++) s += a[row + Math.Clamp(x, 0, w - 1)];
                for (int x = 0; x < w; x++) { b[row + x] = s / k; s += a[row + Math.Clamp(x + r + 1, 0, w - 1)] - a[row + Math.Clamp(x - r, 0, w - 1)]; }
            }
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int y = -r; y <= r; y++) s += b[Math.Clamp(y, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++) { o[y * w + x] = s / k; s += b[Math.Clamp(y + r + 1, 0, h - 1) * w + x] - b[Math.Clamp(y - r, 0, h - 1) * w + x]; }
            }
            a = o;
        }
        return a;
    }

    // ---- 화면 표시 ----
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (Mode != "source" || source == null) return;
        var s = offset != null ? (hx + offset.Value.x, hy + offset.Value.y) : source.Value;
        var (sx, sy) = v.ToScreen(s.Item1, s.Item2);
        using var pen = new Pen(Color.FromArgb(0x22, 0xa0, 0x6b), 2);
        g.DrawLine(pen, (float)sx - 8, (float)sy, (float)sx + 8, (float)sy);
        g.DrawLine(pen, (float)sx, (float)sy - 8, (float)sx, (float)sy + 8);
    }
    public override Rectangle OverlayBounds(ViewXf v)
    {
        if (Mode != "source" || source == null) return Rectangle.Empty;
        var s = offset != null ? (hx + offset.Value.x, hy + offset.Value.y) : source.Value;
        var (sx, sy) = v.ToScreen(s.Item1, s.Item2);
        return new Rectangle((int)sx - 12, (int)sy - 12, 24, 24);
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("복구 브러시"),
        Opt.Choice(null, new[] { ("spot", "스팟 복구 (자동)"), ("source", "복구 브러시 (Alt+클릭 원본)") }, () => Mode, v => { Mode = v; offset = null; }),
        Opt.Slider("크기", 4, 400, () => Size, v => Size = Math.Round(v), log: true, step: 1),
        Opt.Slider("경도", 0, 0.95, () => Hardness, v => Hardness = v, pct: true),
        Opt.Check("정렬 (원본 위치를 계속 따라감, 복구 브러시)", () => Aligned, v => { Aligned = v; offset = null; }),
        Opt.Help("잡티·먼지·흉터 위를 문지르면, 다른 곳의 결을 가져오되 색·밝기는 그 자리 주변에 맞춰 자연스럽게 지웁니다. 큰 부분은 선택 후 편집 > 내용 인식 채우기."));
}
