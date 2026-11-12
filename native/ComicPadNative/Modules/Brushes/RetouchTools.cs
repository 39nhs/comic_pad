// 손끝(문지르기) — 웹 tools/paint2.js smudgeTool, 흐림/선명 브러시 — 웹 tools/retouch.js blurTool
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Brushes;

/// <summary>펜 경로를 따라 간격마다 찍는 보정 도구 공통 (레이어 픽셀을 직접 고침)</summary>
public abstract class DabToolBase : ToolBase
{
    public double Size = 40, Hardness = 0.3, Strength = 0.5;
    public bool UsePressure = true;
    protected abstract double SpacingK { get; }
    protected abstract string Label { get; }
    public override bool DrawsSizeCircle => true;
    public override double CircleSize => Size;
    public override bool Busy => edit != null;

    protected PixelEdit edit;
    protected Layer owner;
    protected Surface target;
    protected int D;
    protected float[] tip;
    double lx, ly, next;

    /// <summary>부드러운 원 (웹 softTip: 0 → hardness 까지 불투명, 가장자리 0)</summary>
    protected static float[] SoftTip(int d, double hardness, double maxHard)
    {
        var a = new float[d * d]; double r = d / 2.0, h = Math.Min(maxHard, hardness);
        for (int y = 0; y < d; y++)
            for (int x = 0; x < d; x++)
            {
                double t = Math.Sqrt((x + 0.5 - r) * (x + 0.5 - r) + (y + 0.5 - r) * (y + 0.5 - r)) / r;
                a[y * d + x] = (float)(t <= h ? 1 : t >= 1 ? 0 : 1 - (t - h) / (1 - h));
            }
        return a;
    }
    protected abstract double MaxHard { get; }

    public override void Down(PointerArgs e)
    {
        owner = App.Active;
        if (!App.CheckEditable(owner)) { owner = null; return; }
        target = owner.Target;
        D = Math.Max(2, (int)Math.Round(Size));
        tip = SoftTip(D, Hardness, MaxHard);
        edit = App.History.BeginPixels(owner, Label);
        lx = e.X; ly = e.Y; next = 0;
        Begin(e);
    }
    protected virtual void Begin(PointerArgs e) { }

    public override void Move(PointerArgs e)
    {
        if (edit == null) return;
        double dx = e.X - lx, dy = e.Y - ly, len = Math.Sqrt(dx * dx + dy * dy);
        double step = Math.Max(1, D * SpacingK);
        double t = next;
        while (t <= len) { Dab(lx + dx * t / len, ly + dy * t / len, e.Pressure > 0 ? e.Pressure : 1); t += step; }
        next = t - len;
        lx = e.X; ly = e.Y;
    }

    public override void Up(PointerArgs e)
    {
        if (edit == null) return;
        edit.Commit(Label);
        App.Doc.Modified = true;
        edit = null;
        App.NotifyLayers();
    }

    public override void Cancel()
    {
        if (edit == null) return;
        var r = edit.Dirty; edit.Cancel(); edit = null; App.Invalidate(r);
    }

    protected abstract void Dab(double x, double y, double p);

    /// <summary>tmp(D×D, 미리 곱한 색)를 부드러운 원 모양·선택 영역으로 잘라 (X,Y) 에 alpha 로 얹음</summary>
    protected void Stamp(uint[] tmp, int X, int Y, double alpha)
    {
        var r = new Rect(X, Y, D, D).Clip(target.W, target.H);
        if (r.IsEmpty) return;
        edit.Touch(r);
        var sel = App.Doc.Sel;
        bool atop = owner.AlphaLock && !owner.EditMask;
        uint a8 = (uint)Math.Clamp(alpha * 255 + 0.5, 0, 255);
        for (int y = r.Y; y < r.B; y++)
            for (int x = r.X; x < r.R; x++)
            {
                int i = (y - Y) * D + (x - X);
                uint s = tmp[i];
                if (s == 0) continue;
                uint k = (uint)(tip[i] * 255 + 0.5f);
                if (sel.Mask != null) k = k * sel.Mask[y * sel.W + x] / 255;
                k = k * a8 / 255;
                if (k == 0) continue;
                uint src = Px.Scale(s, k), d = target.Get(x, y);
                target.Set(x, y, atop ? Px.Composite(src, d, Blend.Atop) : Px.Over(src, d));
            }
        App.Invalidate(r);
    }
}

public sealed class SmudgeTool : DabToolBase
{
    public override string Id => "smudge";
    public override string Name => "손끝 (문지르기)";
    public override string Icon => "☝";
    public override Keys Key => Keys.W;
    protected override double SpacingK => 0.12;
    protected override string Label => "문지르기";
    protected override double MaxHard => 0.95;
    uint[] carry;

    public SmudgeTool() { Size = 40; Strength = 0.65; Hardness = 0.3; UsePressure = true; }

    protected override void Begin(PointerArgs e) => Grab(e.X, e.Y);

    void Grab(double x, double y)
    {
        double r = D / 2.0;
        int X = (int)Math.Floor(x - r + 0.5), Y = (int)Math.Floor(y - r + 0.5);
        carry = target.Read(new Rect(X, Y, D, D));
    }

    protected override void Dab(double x, double y, double p)
    {
        double r = D / 2.0;
        int X = (int)Math.Floor(x - r + 0.5), Y = (int)Math.Floor(y - r + 0.5);
        Stamp(carry, X, Y, Strength * (UsePressure ? Math.Max(0.1, p) : 1));
        Grab(x, y);
    }

    public override Control BuildOptions() => UI.Opt.Panel(
        UI.Opt.Title("손끝"),
        BUI.Slider("크기", 4, 300, () => Size, v => Size = v, step: 1, log: true),
        BUI.Slider("세기", 0.05, 1, () => Strength, v => Strength = v, fmt: BUI.Pct),
        BUI.Slider("경도", 0, 0.95, () => Hardness, v => Hardness = v, fmt: BUI.Pct),
        BUI.Toggle("필압으로 세기 조절", () => UsePressure, v => UsePressure = v),
        UI.Opt.Help("색을 끌어서 섞습니다. 채색 경계를 부드럽게 할 때 사용하세요."));
}

public sealed class BlurBrushTool : DabToolBase
{
    public override string Id => "blurbrush";
    public override string Name => "흐림/선명 브러시";
    public override string Icon => "💧";
    protected override double SpacingK => 0.25;
    protected override string Label => "흐림/선명 브러시";
    protected override double MaxHard => 0.97;
    public string Mode = "blur";

    public BlurBrushTool() { Size = 50; Hardness = 0.3; Strength = 0.5; }

    protected override void Begin(PointerArgs e) { Dab(e.X, e.Y, e.Pressure > 0 ? e.Pressure : 1); }

    protected override void Dab(double x, double y, double p)
    {
        const int pad = 8;
        int B = D + pad * 2;
        int bx = (int)Math.Floor(x - B / 2.0 + 0.5), by = (int)Math.Floor(y - B / 2.0 + 0.5);
        var src = target.Read(new Rect(bx, by, B, B));
        uint[] outp;
        if (Mode == "blur") outp = Gaussian(src, B, B, Math.Max(0.5, D / 12.0 * Strength * 2));
        else
        {
            var bl = Gaussian(src, B, B, 1.2);
            double amt = Strength * 2.5;
            outp = new uint[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                uint s = src[i], b = bl[i];
                int C(int sh) => Math.Clamp((int)Math.Round((1 + amt) * ((s >> sh) & 255) - amt * ((b >> sh) & 255)), 0, 255);
                uint a = (uint)C(24);
                outp[i] = Px.Pack(a, (uint)Math.Min(a, C(16)), (uint)Math.Min(a, C(8)), (uint)Math.Min(a, C(0)));
            }
        }
        var tmp = new uint[D * D];
        for (int yy = 0; yy < D; yy++) Array.Copy(outp, (yy + pad) * B + pad, tmp, yy * D, D);
        int X = (int)Math.Floor(x - D / 2.0 + 0.5), Y = (int)Math.Floor(y - D / 2.0 + 0.5);
        Stamp(tmp, X, Y, Math.Min(1, 0.35 + Strength * 0.5) * Math.Max(0.2, p));
    }

    /// <summary>가우스 흐림 (미리 곱한 색 그대로, 밖은 투명)</summary>
    public static uint[] Gaussian(uint[] src, int w, int h, double sigma)
    {
        int rad = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var k = new double[rad * 2 + 1]; double sum = 0;
        for (int i = -rad; i <= rad; i++) { k[i + rad] = Math.Exp(-i * i / (2 * sigma * sigma)); sum += k[i + rad]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var tmp = new double[w * h * 4]; var o = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double a = 0, r = 0, g = 0, b = 0;
                for (int j = -rad; j <= rad; j++)
                {
                    int xx = x + j; if ((uint)xx >= (uint)w) continue;
                    uint c = src[y * w + xx]; double kk = k[j + rad];
                    a += (c >> 24) * kk; r += ((c >> 16) & 255) * kk; g += ((c >> 8) & 255) * kk; b += (c & 255) * kk;
                }
                int t = (y * w + x) * 4; tmp[t] = a; tmp[t + 1] = r; tmp[t + 2] = g; tmp[t + 3] = b;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double a = 0, r = 0, g = 0, b = 0;
                for (int j = -rad; j <= rad; j++)
                {
                    int yy = y + j; if ((uint)yy >= (uint)h) continue;
                    int t = (yy * w + x) * 4; double kk = k[j + rad];
                    a += tmp[t] * kk; r += tmp[t + 1] * kk; g += tmp[t + 2] * kk; b += tmp[t + 3] * kk;
                }
                uint A = (uint)Math.Clamp((int)Math.Round(a), 0, 255);
                o[y * w + x] = Px.Pack(A, (uint)Math.Min(A, Math.Clamp((int)Math.Round(r), 0, 255)), (uint)Math.Min(A, Math.Clamp((int)Math.Round(g), 0, 255)), (uint)Math.Min(A, Math.Clamp((int)Math.Round(b), 0, 255)));
            }
        return o;
    }

    public override Control BuildOptions() => UI.Opt.Panel(
        UI.Opt.Title("흐림 / 선명"),
        BUI.Seg(new[] { ("blur", "흐리게"), ("sharp", "선명하게") }, () => Mode, v => Mode = v),
        BUI.Slider("크기", 4, 400, () => Size, v => Size = v, step: 1, log: true),
        BUI.Slider("세기", 0.05, 1, () => Strength, v => Strength = v, fmt: BUI.Pct),
        UI.Opt.Help("문지른 곳만 흐리거나 선명하게. 배경 거리감, 피부결 정리, 초점 강조에."));
}
