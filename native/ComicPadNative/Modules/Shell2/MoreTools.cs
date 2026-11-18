// 웹 판에 있고 네이티브에 없던 도구: 복제 도장·픽셀 유동화 (웹 tools/retouch.js), 벡터 선 고르기 (웹 tools/vselect.js — Modules/VectorTools/VectorSelectTool.cs 로 옮김).
// 복제·유동화는 브러시 모듈의 DabToolBase(펜 경로를 따라 간격마다 찍기, 실행취소·선택 영역·투명 잠금 처리)를 그대로 씀.
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public sealed class CloneTool : DabToolBase
{
    public override string Id => "clone";
    public override string Name => "복제 도장";
    public override string Icon => "⎘";
    public override bool AllowAltPick => false;
    protected override double SpacingK => 0.1;
    protected override string Label => "복제 도장";
    protected override double MaxHard => 0.97;
    public double Opacity = 1; public bool Aligned = true, SampleAll;
    public (double x, double y)? Source; (double x, double y)? offset;
    Surface src; double hx, hy;

    public CloneTool() { Size = 60; Hardness = 0.5; }

    protected override void Begin(PointerArgs e)
    {
        if (e.Alt)
        {
            Source = (e.X, e.Y); offset = null;
            App.Toast("복제할 원본 위치를 정했습니다");
            edit.Cancel(); edit = null; return;
        }
        if (Source == null) { App.Toast("먼저 Alt+클릭으로 복제할 원본을 찍으세요"); edit.Cancel(); edit = null; return; }
        if (offset == null || !Aligned) offset = (Source.Value.x - e.X, Source.Value.y - e.Y);
        // 원본: 시작할 때 모습으로 고정 (방금 찍은 걸 또 복제하지 않도록)
        src = SampleAll ? App.Comp.Flatten(App.Doc, new RenderOptions { Paper = true }) : target.Clone();
        Dab(e.X, e.Y, e.Pressure > 0 ? e.Pressure : 1);
    }

    protected override void Dab(double x, double y, double p)
    {
        if (src == null || offset == null) return;
        double r = D / 2.0;
        int X = (int)Math.Floor(x - r + 0.5), Y = (int)Math.Floor(y - r + 0.5);
        int SX = (int)Math.Round(x + offset.Value.x - r), SY = (int)Math.Round(y + offset.Value.y - r);
        var tmp = new uint[D * D];
        for (int j = 0; j < D; j++) for (int i = 0; i < D; i++)
        {
            int sx = SX + i, sy = SY + j;
            if ((uint)sx < (uint)src.W && (uint)sy < (uint)src.H) tmp[j * D + i] = src.Get(sx, sy);
        }
        Stamp(tmp, X, Y, Opacity * Math.Max(0.2, p));
    }

    public override void Up(PointerArgs e) { base.Up(e); src = null; }
    public override void Hover(PointerArgs e) { hx = e.X; hy = e.Y; }

    public override Rectangle OverlayBounds(ViewXf v)
    {
        if (Source == null) return Rectangle.Empty;
        var (sx, sy) = Mark(v); int rad = (int)(Size / 2 * v.Zoom) + 12;
        return new Rectangle((int)sx - rad, (int)sy - rad, rad * 2, rad * 2);
    }
    (double, double) Mark(ViewXf v) => offset != null ? v.ToScreen(hx + offset.Value.x, hy + offset.Value.y) : v.ToScreen(Source.Value.x, Source.Value.y);
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (Source == null) return;
        var (x, y) = Mark(v);
        using var p = new Pen(Color.FromArgb(47, 124, 246), 2);
        g.DrawLine(p, (float)x - 8, (float)y, (float)x + 8, (float)y); g.DrawLine(p, (float)x, (float)y - 8, (float)x, (float)y + 8);
        float rad = (float)(Size / 2 * v.Zoom); g.DrawEllipse(p, (float)x - rad, (float)y - rad, rad * 2, rad * 2);
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("복제 도장"),
        BUI.Slider("크기", 4, 400, () => Size, v => Size = v, step: 1, log: true),
        BUI.Slider("경도", 0, 0.97, () => Hardness, v => Hardness = v, fmt: BUI.Pct),
        BUI.Slider("불투명도", 0.05, 1, () => Opacity, v => Opacity = v, fmt: BUI.Pct),
        BUI.Toggle("정렬 (원본 위치를 계속 따라감)", () => Aligned, v => { Aligned = v; offset = null; }),
        BUI.Toggle("보이는 그림 전체에서 복제", () => SampleAll, v => SampleAll = v),
        BUI.Help("Alt+클릭으로 원본 지정 → 고칠 곳을 칠하면 원본이 복사됩니다."));
}

public sealed class LiquifyTool : DabToolBase
{
    public override string Id => "liquify";
    public override string Name => "픽셀 유동화";
    public override string Icon => "〰";
    protected override double SpacingK => 0.12;
    protected override string Label => "픽셀 유동화";
    protected override double MaxHard => 1;
    public string Mode = "push";
    double px, py; bool first;

    public LiquifyTool() { Size = 120; Strength = 0.6; }

    protected override void Begin(PointerArgs e) { px = e.X; py = e.Y; first = true; }

    protected override void Dab(double x, double y, double p)
    {
        double ddx = x - px, ddy = y - py, len = Math.Sqrt(ddx * ddx + ddy * ddy);
        double ux = len > 0 ? ddx / len : 0, uy = len > 0 ? ddy / len : 0;
        px = x; py = y;
        if (first && Mode == "push") { first = false; return; }
        first = false;
        int R = Math.Max(1, (int)Math.Round(D / 2.0)), Dd = R * 2;
        int X = (int)Math.Round(x) - R, Y = (int)Math.Round(y) - R;
        var r = new Rect(X, Y, Dd, Dd).Clip(target.W, target.H);
        if (r.IsEmpty) return;
        edit.Touch(r);
        var srcBuf = target.Read(r); var o = (uint[])srcBuf.Clone();
        int sw = r.W, sh = r.H;
        double st = Strength * Math.Max(0.3, p), step = D * SpacingK;
        var sel = App.Doc.Sel;
        for (int j = 0; j < sh; j++)
            for (int i = 0; i < sw; i++)
            {
                double dx = r.X + i - x, dy = r.Y + j - y, d2 = dx * dx + dy * dy;
                if (d2 >= R * R) continue;
                if (sel.Mask != null && sel.Mask[(r.Y + j) * sel.W + r.X + i] < 128) continue;
                double f = 1 - Math.Sqrt(d2) / R, w = f * f * (3 - 2 * f) * st;
                double sx, sy;
                if (Mode == "push") { sx = dx - ux * step * w * 1.6; sy = dy - uy * step * w * 1.6; }
                else if (Mode == "bloat") { sx = dx * (1 - w * 0.25); sy = dy * (1 - w * 0.25); }
                else if (Mode == "pinch") { sx = dx * (1 + w * 0.25); sy = dy * (1 + w * 0.25); }
                else { double a = w * 0.25 * (Mode == "twirlL" ? -1 : 1), c = Math.Cos(a), s = Math.Sin(a); sx = dx * c - dy * s; sy = dx * s + dy * c; }
                double fx = x + sx - r.X, fy = y + sy - r.Y;
                int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                if (ix < 0 || iy < 0 || ix >= sw - 1 || iy >= sh - 1) continue;
                double tx = fx - ix, ty = fy - iy;
                uint a00 = srcBuf[iy * sw + ix], a10 = srcBuf[iy * sw + ix + 1], a01 = srcBuf[(iy + 1) * sw + ix], a11 = srcBuf[(iy + 1) * sw + ix + 1];
                uint res = 0;
                for (int sh8 = 0; sh8 < 32; sh8 += 8)
                {
                    double v = (((a00 >> sh8) & 255) * (1 - tx) + ((a10 >> sh8) & 255) * tx) * (1 - ty) + (((a01 >> sh8) & 255) * (1 - tx) + ((a11 >> sh8) & 255) * tx) * ty;
                    res |= (uint)Math.Clamp((int)Math.Round(v), 0, 255) << sh8;
                }
                o[j * sw + i] = res;
            }
        target.Write(r, o);
        App.Invalidate(r);
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("픽셀 유동화"),
        BUI.Seg(new[] { ("push", "밀기"), ("bloat", "부풀리기"), ("pinch", "오므리기"), ("twirlR", "오른쪽 회전"), ("twirlL", "왼쪽 회전") }, () => Mode, v => Mode = v),
        BUI.Slider("크기", 10, 400, () => Size, v => Size = v, step: 1, log: true),
        BUI.Slider("세기", 0.05, 1, () => Strength, v => Strength = v, fmt: BUI.Pct),
        BUI.Help("얼굴형·눈 크기·옷 주름 등을 손으로 밀어서 고칩니다. 부풀리기/오므리기/회전은 제자리에서 문지르세요."));
}
