// 브러시 엔진 — 웹 판 core/brush.js 의 Stroke 를 그대로 옮김.
// 한 획은 문서 크기 "획 버퍼"(Surface, 미리 곱한 BGRA)에 찍히고, 레이어에는 StrokeApply 가 (그리기 전 + 버퍼) 로 합성한다.
//  - 손떨림 보정: 1€(euro) · 평균(avg) · 끈 당기기(lazy) · 무거운 펜(spring) + 펜을 떼면 펜 끝까지 따라잡기
//  - 필압: 크기·불투명도·색, 필압 다듬기·시작 약하게, 펜별 곡선
//  - 끝 모양: 원(딱딱함/부드러움) · 연필결 · 스프레이 · 납작 펜(기울기 각도) · 이미지 · 픽셀(픽셀 퍼펙트·디더·명암 잉크)
//  - 흐름·누적, 크기·회전·흩뿌림·색상/채도/명도 흔들림, 속도·기울기, 혼색, 듀얼 브러시, 리본, 입출 보정, 대칭(좌우·상하·방사)
//  - 딱딱한 불투명 원은 찍은 원들을 하나의 모양으로 (덮인 정도 최댓값) → 겹친 가장자리가 진해지지 않음 (선뭉침 없음)
using ComicPadNative.Core;

namespace ComicPadNative.Modules.BrushEngine;

public struct BPt
{
    public double X, Y, P, T;   // T = ms (없으면 NaN)
    public BPt(double x, double y, double p, double t = double.NaN) { X = x; Y = y; P = p; T = t; }
    public bool HasT => !double.IsNaN(T);
}

public sealed class StrokeSetup
{
    public int W, H;
    public BrushPreset Preset;
    public uint Color = 0xFF000000;          // 불투명 BGRA
    public uint BgColor = 0xFFFFFFFF;        // 필압으로 색 바뀜 (약하게 = 배경색)
    public string Symmetry = "none";         // none | v | h | vh | radial
    public int SymN = 6;
    public bool SymMirror;
    public Func<int, int, uint> Sample;      // 혼색·명암 잉크: 그리기 전 레이어 픽셀 (미리 곱한 BGRA)
    public List<int[]> Palette;              // 명암 잉크
    public double Zoom = 1;
    public PenFeel Feel;
    public int Seed = -1;                    // 흔들림 난수 (시험용으로 고정할 때)
}

public sealed class BrushStroke
{
    public readonly Surface Buf;
    public BrushPreset P;
    readonly int W, H;
    readonly uint color;
    readonly StrokeSetup o;
    readonly PenFeel feel;
    readonly Random rnd;
    readonly string sym;

    public double? TiltAngle;
    public double TiltMag;
    public bool ShadeFlip, NoTaper;
    public List<double> Rec;                 // 벡터 레이어: 보정이 끝난 점 (x, y, p, t) 기록

    // 상태
    readonly List<BPt> raw = new(), pts = new();
    BPt lastMid; bool hasLastMid;
    double next, travel, lastDir, lastD, vel;
    readonly List<(double x, double y, double pr, double d, double k)> queue = new();
    public Rect Total = Rect.Empty;
    Rect batch = Rect.Empty;
    int np; double? ps;
    bool euOn; double eux, euy, euv; double? et;
    public (double x, double y)? Lz; public BPt? LzPen;
    bool spOn; double spx, spy, spvx, spvy, sptx, spty, spt;
    (double x, double y)? ribPrev; double ribU;
    (int x, int y)? lastPix; readonly List<(int x, int y)> ppHist = new();
    HashSet<long> shadeSeen;
    double mr, mg, mb; bool mixOn;
    uint fill; uint? dabColor;
    // 끝 모양
    bool hardRound, isVec, pixel, union;
    readonly List<TipImage> tips = new(), maskTips = new();
    bool imageTip;
    double overlap;
    TipImage[] dualTips;
    // 작업 배열
    float[] cov = new float[1024];
    uint[] ccol = new uint[1024];

    public BrushStroke(StrokeSetup s)
    {
        o = s; P = s.Preset; W = s.W; H = s.H; color = s.Color | 0xFF000000; fill = color;
        feel = s.Feel ?? PenFeel.Current();
        rnd = s.Seed >= 0 ? new Random(s.Seed) : new Random();
        sym = s.Symmetry ?? "none";
        Buf = new Surface(W, H);
        mixOn = P.Mix > 0;
        if (mixOn) { mr = (color >> 16) & 255; mg = (color >> 8) & 255; mb = color & 255; }
        PrepareTips();
    }

    public string StabModeName => P.StabMode != null && P.StabMode != "auto" ? P.StabMode : feel.Filter ?? "euro";
    public bool IsLazy => StabModeName == "lazy";

    void PrepareTips()
    {
        var p = P;
        hardRound = p.Tip == "round" && p.Hardness >= 0.98;
        pixel = p.Tip == "pixel";
        if (pixel) { hardRound = false; isVec = false; overlap = 1; return; }
        if (p.Tip == "spray" || p.Tip == "flat") { hardRound = false; isVec = true; }
        else if (p.Tip == "image" && Tips.LoadImage(p.TipImage) is TipImage img) { tips.Add(img); imageTip = true; hardRound = false; }
        else if (!hardRound)
        {
            int variants = p.Tip == "grain" ? 4 : 1;
            for (int v = 0; v < variants; v++) { var mk = Tips.MaskTip(p.Hardness, p.Tip == "grain" ? p.Grain : 0, v); maskTips.Add(mk); tips.Add(mk); }
        }
        // 리본·듀얼은 끝 모양 그림이 있어야 함 (딱딱한 원이라도)
        if ((p.Ribbon || p.HasDual) && tips.Count == 0 && !isVec)
        {
            hardRound = false;
            var mk = Tips.MaskTip(p.Hardness, p.Tip == "grain" ? p.Grain : 0, 0);
            maskTips.Add(mk); tips.Add(mk);
        }
        // 선뭉침 방지: 딱딱한 원 + 늘 불투명이면 찍은 원들을 하나의 모양으로 (가장자리 부드러움이 겹쳐 쌓이지 않게)
        union = hardRound && !isVec && !pixel && p.Flow >= 1 && !p.PressureOpacity && p.SpeedOpacity == 0 && p.TiltOpacity == 0 && !(p.Mix > 0)
            && !p.PressureColor && p.HueJitter == 0 && p.SatJitter == 0 && p.ValJitter == 0 && !p.Shading && !p.HasDual && !p.Ribbon && feel.UnionDabs;
        overlap = Math.Max(1, 1 / Math.Max(0.02, p.Spacing));
    }

    public bool Union => union;

    double Curve(double pr) => feel.Curve(pr, P);

    double SizeAt(double pr)
    {
        var p = P;
        double v = p.SpeedThin != 0 ? 1 - p.SpeedThin * 0.7 * Math.Min(1, vel / 2.5) : 1;
        if (p.TiltSize != 0 && TiltMag != 0) v *= 1 + p.TiltSize * 2.5 * TiltMag;   // 펜을 눕힐수록 굵게
        if (!p.PressureSize) return p.Size * v;
        return p.Size * (p.MinSize + (1 - p.MinSize) * Curve(pr)) * v;
    }

    double AlphaAt(double pr)
    {
        var p = P;
        double a = p.Flow;
        if (p.PressureOpacity)
        {
            double target = p.MinOpacity + (1 - p.MinOpacity) * Curve(pr);
            a *= p.Buildup ? target : 1 - Math.Pow(1 - Math.Clamp(target, 0, 0.999), 1 / overlap);
        }
        if (p.TiltOpacity != 0 && TiltMag != 0) a *= 1 - p.TiltOpacity * 0.85 * TiltMag;
        if (p.SpeedOpacity != 0) a *= 1 - p.SpeedOpacity * 0.85 * Math.Min(1, vel / 2.5);
        return a;
    }

    /// <summary>한 번 찍을 때의 색: 필압으로 배경색↔전경색, 색상·채도·명도 흔들림. 없으면 null</summary>
    uint? DabColorAt(double pr)
    {
        var p = P;
        if (!p.PressureColor && p.HueJitter == 0 && p.SatJitter == 0 && p.ValJitter == 0) return null;
        double r = (color >> 16) & 255, g = (color >> 8) & 255, b = color & 255;
        if (p.PressureColor)
        {
            double t = Curve(pr); uint bg = o.BgColor;
            double br = (bg >> 16) & 255, bgg = (bg >> 8) & 255, bb = bg & 255;
            r = br + (r - br) * t; g = bgg + (g - bgg) * t; b = bb + (b - bb) * t;
        }
        if (p.HueJitter != 0 || p.SatJitter != 0 || p.ValJitter != 0)
        {
            var (hh, ss, vv) = Px.RgbToHsv(r / 255, g / 255, b / 255);
            hh = (hh + (rnd.NextDouble() - 0.5) * p.HueJitter * 360 + 360) % 360;
            ss = Math.Clamp(ss + (rnd.NextDouble() - 0.5) * p.SatJitter, 0, 1);
            vv = Math.Clamp(vv + (rnd.NextDouble() - 0.5) * p.ValJitter, 0, 1);
            (r, g, b) = Px.HsvToRgb(hh, ss, vv); r *= 255; g *= 255; b *= 255;
        }
        uint R(double v) => (uint)Math.Clamp((int)Math.Floor(v + 0.5), 0, 255);
        return Px.Pack(255, R(r), R(g), R(b));
    }

    double SpacingAt(double pr) => Math.Max(0.35, SizeAt(pr) * P.Spacing);

    void Mark(double x0, double y0, double x1, double y1)
    {
        var r = Rect.FromLTRB((int)Math.Floor(x0), (int)Math.Floor(y0), (int)Math.Ceiling(x1), (int)Math.Ceiling(y1));
        batch = batch.Union(r);
        Total = Total.Union(r);
    }

    /// <summary>지난번 이후 바뀐 버퍼 영역 (문서 안으로 자름)</summary>
    public Rect TakeDirty() { var r = batch.Clip(W, H); batch = Rect.Empty; return r; }

    /// <summary>혼색: 밑 색을 집어 현재 붓 색과 섞는다</summary>
    uint? MixAt(double x, double y)
    {
        if (o.Sample == null || !mixOn) return null;
        int ix = (int)Math.Floor(x + 0.5), iy = (int)Math.Floor(y + 0.5);
        double r = 0, g = 0, b = 0, a = 0;
        for (int dy = -2; dy <= 2; dy += 2)
            for (int dx = -2; dx <= 2; dx += 2)
            {
                int px = ix + dx, py = iy + dy;
                if (px < 0 || py < 0 || px >= W || py >= H) continue;
                uint c = Px.Unpremul(o.Sample(px, py)); double al = c >> 24;
                r += ((c >> 16) & 255) * al; g += ((c >> 8) & 255) * al; b += (c & 255) * al; a += al;
            }
        if (a > 0)
        {
            double k = P.Mix * Math.Min(1, a / (9 * 255) * 1.5);
            mr += (r / a - mr) * k; mg += (g / a - mg) * k; mb += (b / a - mb) * k;
        }
        return Px.Pack(255, (uint)Math.Clamp((int)mr, 0, 255), (uint)Math.Clamp((int)mg, 0, 255), (uint)Math.Clamp((int)mb, 0, 255));
    }

    // ---- 찍기 --------------------------------------------------------------------
    float[] Cov(int n) { if (cov.Length < n) cov = new float[Math.Max(n, cov.Length * 2)]; Array.Clear(cov, 0, n); return cov; }
    uint[] CCol(int n) { if (ccol.Length < n) ccol = new uint[Math.Max(n, ccol.Length * 2)]; return ccol; }

    Rect Box(double x0, double y0, double x1, double y1) => Rect.FromLTRB((int)Math.Floor(x0), (int)Math.Floor(y0), (int)Math.Ceiling(x1), (int)Math.Ceiling(y1)).Clip(W, H);

    /// <summary>덮인 정도(cov) × alpha 로 색을 버퍼에 (maxMode = 덮인 정도 최댓값만 남기기)</summary>
    void Emit(Rect box, float[] c, double alpha, uint col, uint[] colors, bool maxMode)
    {
        if (box.IsEmpty) return;
        const int TS = Surface.TS;
        uint cr = (col >> 16) & 255, cg = (col >> 8) & 255, cb = col & 255;
        for (int y = box.Y; y < box.B; y++)
        {
            int ty = y / TS, row = (y - ty * TS) * TS, ci = (y - box.Y) * box.W - box.X;
            uint[] t = null; int curTx = -1;
            for (int x = box.X; x < box.R; x++)
            {
                float v = c[ci + x];
                if (v <= 0) continue;
                double a = v * alpha;
                uint a8 = a >= 1 ? 255u : (uint)(a * 255 + 0.5);
                if (a8 == 0) continue;
                int tx = x / TS;
                if (tx != curTx) { t = Buf.Tile(tx, ty, true); curTx = tx; }
                int li = row + x - tx * TS;
                uint src = colors != null ? Px.Scale(colors[ci + x], a8) : Px.Pack(a8, Px.Mul(cr, a8), Px.Mul(cg, a8), Px.Mul(cb, a8));
                if (maxMode) { if (a8 > (t[li] >> 24)) t[li] = src; }
                else t[li] = Px.Over(src, t[li]);
            }
        }
    }

    /// <summary>원 하나의 덮인 정도 (딱딱한 원: 가장자리 1px 부드럽게)</summary>
    Rect HardCircle(double x, double y, double r, float[] c0 = null)
    {
        var box = Box(x - r - 1, y - r - 1, x + r + 1, y + r + 1);
        if (box.IsEmpty) return box;
        var c = c0 ?? Cov(box.W * box.H);
        for (int py = box.Y; py < box.B; py++)
        {
            double dy = py + 0.5 - y, dy2 = dy * dy; int ri = (py - box.Y) * box.W;
            for (int px = box.X; px < box.R; px++)
            {
                double dx = px + 0.5 - x, v = r + 0.5 - Math.Sqrt(dx * dx + dy2);
                if (v > 0) c[ri + px - box.X] = (float)Math.Min(1, v);
            }
        }
        return box;
    }

    /// <summary>팁 그림 찍기 (회전·크기). colored = 이미지 팁 원래 색</summary>
    Rect TexDab(TipImage tip, double x, double y, double size, double rot, bool colored)
    {
        double asp = (double)tip.H / tip.W, hw = size / 2, hh = size * asp / 2;
        double ex = hw, ey = hh;
        if (rot != 0) { ex = ey = Math.Sqrt(hw * hw + hh * hh); }
        var box = Box(x - ex - 1, y - ey - 1, x + ex + 1, y + ey + 1);
        if (box.IsEmpty) return box;
        var c = Cov(box.W * box.H);
        var cc = colored ? CCol(box.W * box.H) : null;
        // 연필결: 웹(캔버스 drawImage)처럼 원본 결에서 바로 집어 결이 살아 있게, 그 밖은 밉맵으로 매끈하게
        int l = GrainTip(tip) ? 0 : tip.LevelFor(size);
        var (lw, lh) = tip.Sz[l];
        double cs = Math.Cos(rot), sn = Math.Sin(rot);
        bool radial = !colored && tip.W == Tips.TIP && maskTips.Count > 0 && P.Tip == "round" && tip == maskTips[0];
        for (int py = box.Y; py < box.B; py++)
        {
            double dy = py + 0.5 - y; int ri = (py - box.Y) * box.W - box.X;
            for (int px = box.X; px < box.R; px++)
            {
                double dx = px + 0.5 - x;
                if (radial)
                {
                    // 부드러운 원: 그라데이션을 바로 계산 (작게 찍어도 정확)
                    double t = Math.Sqrt(dx * dx + dy * dy) / Math.Max(0.25, hw);
                    if (t < 1) c[ri + px] = Tips.Grad(P.Hardness, t);
                    continue;
                }
                double lx = dx * cs + dy * sn, ly = -dx * sn + dy * cs;
                double u = (lx + hw) / size * lw - 0.5, v = (ly + hh) / (size * asp) * lh - 0.5;
                if (u < -1 || v < -1 || u > lw || v > lh) continue;
                float a = tip.Alpha(l, u, v);
                if (a <= 0) continue;
                c[ri + px] = a;
                if (cc != null) { uint col = tip.Color(l, u, v); uint ca = col >> 24; cc[ri + px] = ca == 0 ? 0 : Px.Unpremul(col) | 0xFF000000; }
            }
        }
        // 색이 있는 이미지 팁: 색은 "알파 255 로 펼친" 값이어야 Emit 의 Scale 이 맞음 → 위에서 펼쳐 둠
        return box;
    }

    bool GrainTip(TipImage t) => !imageTip && P.Tip == "grain" && P.Grain > 0;

    void DabOne(double x, double y, double size, double alpha, double rot, bool dual)
    {
        double r = size / 2;
        uint? mix = mixOn ? MixAt(x, y) : dabColor;
        if (mix != null) fill = mix.Value;
        var p = P;
        Rect box; bool maxMode = false; double a = alpha; uint col = fill; uint[] colors = null;
        if (isVec)
        {
            if (p.Tip == "spray")
            {
                int n = Math.Max(1, (int)Math.Floor(p.Density * Math.Min(4, Math.Max(0.3, size / 60)) + 0.5));
                var parts = new (double x, double y, double r)[n];
                double maxR = 0;
                for (int k = 0; k < n; k++)
                {
                    double an = rnd.NextDouble() * Math.PI * 2, d = r * Math.Sqrt(rnd.NextDouble()), pr = p.Particle * (0.5 + rnd.NextDouble());
                    parts[k] = (x + Math.Cos(an) * d, y + Math.Sin(an) * d, pr); maxR = Math.Max(maxR, pr);
                }
                box = Box(x - r - maxR - 1, y - r - maxR - 1, x + r + maxR + 1, y + r + maxR + 1);
                if (box.IsEmpty) { MarkDab(x, y, r); return; }
                var c = Cov(box.W * box.H);
                foreach (var (qx, qy, qr) in parts)
                {
                    var bb = Box(qx - qr - 1, qy - qr - 1, qx + qr + 1, qy + qr + 1).Intersect(box);
                    for (int py = bb.Y; py < bb.B; py++)
                        for (int px = bb.X; px < bb.R; px++)
                        {
                            double ddx = px + 0.5 - qx, ddy = py + 0.5 - qy, v = qr + 0.5 - Math.Sqrt(ddx * ddx + ddy * ddy);
                            if (v <= 0) continue;
                            int ci = (py - box.Y) * box.W + px - box.X;
                            if (v > c[ci]) c[ci] = (float)Math.Min(1, v);   // 한 경로로 채움 = 겹쳐도 한 번
                        }
                }
            }
            else
            {
                // 납작 펜: 펜 기울기 방향(있으면) 또는 고정 각도
                double ang = p.UseTilt && TiltAngle != null ? TiltAngle.Value : p.Angle * Math.PI / 180;
                double rx = Math.Max(r, 0.5), ry = Math.Max(r * p.FlatRatio, 0.5);
                box = Box(x - rx - 1, y - rx - 1, x + rx + 1, y + rx + 1);
                if (box.IsEmpty) { MarkDab(x, y, r); return; }
                var c = Cov(box.W * box.H);
                double cs = Math.Cos(ang), sn = Math.Sin(ang);
                for (int py = box.Y; py < box.B; py++)
                    for (int px = box.X; px < box.R; px++)
                    {
                        double dx = px + 0.5 - x, dy = py + 0.5 - y;
                        double u = dx * cs + dy * sn, v = -dx * sn + dy * cs;
                        double q = Math.Sqrt(u * u / (rx * rx) + v * v / (ry * ry));
                        if (q > 2) continue;
                        double gx = u / (rx * rx), gy = v / (ry * ry), gl = Math.Sqrt(gx * gx + gy * gy);
                        double sd = q < 1e-9 ? -ry : (q - 1) * q / Math.Max(1e-9, gl);   // 가장자리까지 대략의 거리
                        double cv = Math.Clamp(0.5 - sd, 0, 1);
                        if (cv > 0) c[(py - box.Y) * box.W + px - box.X] = (float)cv;
                    }
            }
        }
        else if (hardRound)
        {
            double rr = Math.Max(r, 0.5);
            a = size < 1 ? alpha * size : alpha;
            box = HardCircle(x, y, rr);
            if (union) { maxMode = true; col = color; }
        }
        else
        {
            TipImage tip = tips[tips.Count == 1 ? 0 : rnd.Next(tips.Count)];
            bool colored = imageTip && !p.TintTip && tip.HasColor;
            col = imageTip ? color : (mix ?? color);
            box = TexDab(tip, x, y, size, rot, colored);
            if (colored) colors = ccol;
        }
        if (!box.IsEmpty)
        {
            if (dual) ApplyDual(box, x, y, size);
            Emit(box, cov, a, col, colors, maxMode);
        }
        MarkDab(x, y, r);
    }
    void MarkDab(double x, double y, double r) { double e = r + 2; Mark(x - e, y - e, x + e, y + e); }

    /// <summary>한 번 찍기: 명암 잉크 / 듀얼 브러시 / 보통</summary>
    void Stamp(double x, double y, double size, double alpha, double rot)
    {
        if (P.Shading) { ShadeArea(x, y, size, false); return; }
        DabOne(x, y, size, alpha, rot, P.HasDual && !isVec && !mixOn);
    }

    static double Hash(long i, long j, long k)
    {
        int h0 = unchecked((int)(i * 374761393L + j * 668265263L + k * 2147483647L));
        int t = h0 ^ (int)((uint)h0 >> 13);
        h0 = unchecked((int)(long)((double)t * 1274126177.0));   // 웹: 실수 곱셈 뒤 |0
        return (uint)(h0 ^ (int)((uint)h0 >> 16)) / 4294967296.0;
    }

    /// <summary>듀얼 브러시: 두 번째 끝 모양(문서에 고정된 격자 위의 흩뿌린 자국)이 닿은 곳만 남긴다</summary>
    void ApplyDual(Rect box, double x, double y, double size)
    {
        var p = P;
        int R = (int)Math.Ceiling(size / 2 + 2), D = R * 2;
        double ds = Math.Max(1.5, size * (p.DualSize > 0 ? p.DualSize : 0.35)), g = ds * 0.85;
        double keep = Math.Max(0.05, Math.Min(1, (p.DualDensity > 0 ? p.DualDensity : 6) / 15)), sc = p.DualScatter;
        dualTips ??= Enumerable.Range(0, 4).Select(v => Tips.MaskTip(0.6, p.DualTip == "grain" ? 0.7 : 0, v)).ToArray();
        int ox = (int)Math.Floor(x) - R, oy = (int)Math.Floor(y) - R;
        var m = new float[box.W * box.H];
        void Put(int px, int py, double a)
        {
            if (a <= 0 || !box.Contains(px, py)) return;
            int i = (py - box.Y) * box.W + px - box.X; m[i] = (float)(m[i] + a * (1 - m[i]));
        }
        for (long j = (long)Math.Floor((oy - ds) / g); j <= (long)Math.Ceiling((oy + D + ds) / g); j++)
            for (long i = (long)Math.Floor((ox - ds) / g); i <= (long)Math.Ceiling((ox + D + ds) / g); i++)
            {
                if (Hash(i, j, 1) > keep) continue;
                double cx = (i + 0.5 + (Hash(i, j, 2) - 0.5) * sc) * g, cy = (j + 0.5 + (Hash(i, j, 3) - 0.5) * sc) * g;   // 문서 좌표
                if (p.DualTip == "spray")
                {
                    for (int k = 0; k < 6; k++)
                    {
                        double b = Hash(i, j, 10 + k) * Math.PI * 2, e = Hash(i, j, 20 + k) * ds / 2;
                        double rx = cx + Math.Cos(b) * e, ry = cy + Math.Sin(b) * e;   // 1.2×1.2 칸
                        for (int py = (int)Math.Floor(ry); py <= (int)Math.Floor(ry + 1.2); py++)
                            for (int px = (int)Math.Floor(rx); px <= (int)Math.Floor(rx + 1.2); px++)
                            {
                                double ax = Math.Max(0, Math.Min(px + 1, rx + 1.2) - Math.Max(px, rx)), ay = Math.Max(0, Math.Min(py + 1, ry + 1.2) - Math.Max(py, ry));
                                Put(px, py, ax * ay);
                            }
                    }
                }
                else if (p.DualTip == "round")
                {
                    double rr = ds / 2;
                    for (int py = (int)Math.Floor(cy - rr - 1); py <= (int)Math.Ceiling(cy + rr + 1); py++)
                        for (int px = (int)Math.Floor(cx - rr - 1); px <= (int)Math.Ceiling(cx + rr + 1); px++)
                        {
                            double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                            Put(px, py, Math.Clamp(rr + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1));
                        }
                }
                else
                {
                    var tip = dualTips[(int)(Hash(i, j, 4) * 4)];
                    int l = p.DualTip == "grain" ? 0 : tip.LevelFor(ds); var (lw, lh) = tip.Sz[l];
                    for (int py = (int)Math.Floor(cy - ds / 2 - 1); py <= (int)Math.Ceiling(cy + ds / 2 + 1); py++)
                        for (int px = (int)Math.Floor(cx - ds / 2 - 1); px <= (int)Math.Ceiling(cx + ds / 2 + 1); px++)
                        {
                            double u = (px + 0.5 - (cx - ds / 2)) / ds * lw - 0.5, v = (py + 0.5 - (cy - ds / 2)) / ds * lh - 0.5;
                            Put(px, py, tip.Alpha(l, u, v));
                        }
                }
            }
        for (int k = 0; k < m.Length; k++) cov[k] *= m[k];
    }

    /// <summary>리본: 끝 모양 그림을 획 방향으로 계속 이어 붙인다 (그림의 가로가 획을 따라 흐름)</summary>
    void RibbonTo(double x, double y, double size, double alpha)
    {
        var prev = ribPrev;
        ribPrev = (x, y);
        var tip = tips.Count > 0 ? tips[0] : null;
        if (prev == null || tip == null) return;
        var (px0, py0) = prev.Value;
        double dx = x - px0, dy = y - py0, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.01) return;
        int tw = tip.W, th = tip.H;
        double srcW = Math.Max(0.5, len / Math.Max(1, size) * tw);
        double u0 = ribU;
        double ang = Math.Atan2(dy, dx), cs = Math.Cos(ang), sn = Math.Sin(ang);
        double e = size / 2 + 2;
        var box = Box(Math.Min(px0, x) - e, Math.Min(py0, y) - e, Math.Max(px0, x) + e, Math.Max(py0, y) + e);
        bool colored = imageTip && !P.TintTip && tip.HasColor;
        uint col = imageTip ? color : (dabColor ?? color);
        if (!box.IsEmpty)
        {
            var c = Cov(box.W * box.H); var cc = colored ? CCol(box.W * box.H) : null;
            int l = GrainTip(tip) ? 0 : tip.LevelFor(size * tw / Math.Max(1, th)); var (lw, lh) = tip.Sz[l]; double ls = (double)lw / tw;
            for (int py = box.Y; py < box.B; py++)
                for (int px = box.X; px < box.R; px++)
                {
                    double qx = px + 0.5 - px0, qy = py + 0.5 - py0;
                    double along = qx * cs + qy * sn, across = -qx * sn + qy * cs;
                    if (along < -0.3 || along > len + 0.6 || across < -size / 2 - 0.5 || across > size / 2 + 0.5) continue;
                    double uu = (u0 + Math.Clamp(along, 0, len) / len * srcW) % tw;
                    double u = uu * ls - 0.5, v = (across + size / 2) / size * lh - 0.5;
                    float a = tip.Alpha(l, u, v);
                    // 가장자리 반 픽셀 부드럽게
                    double edge = Math.Min(1, Math.Min(along + 0.8, len + 0.6 - along + 0.2));
                    if (edge < 1) a *= (float)Math.Max(0, edge);
                    if (a <= 0) continue;
                    int ci = (py - box.Y) * box.W + px - box.X;
                    c[ci] = a;
                    if (cc != null) { uint cl = tip.Color(l, u, v); uint ca = cl >> 24; cc[ci] = ca == 0 ? 0 : Px.Unpremul(cl) | 0xFF000000; }
                }
            Emit(box, c, alpha, col, cc, false);
        }
        ribU = (u0 + srcW) % tw;
        Mark(Math.Min(px0, x) - e, Math.Min(py0, y) - e, Math.Max(px0, x) + e, Math.Max(py0, y) + e);
    }

    // ---- 픽셀 끝 -------------------------------------------------------------
    /// <summary>지난 픽셀부터 이번 픽셀까지 빈틈없이 (Bresenham)</summary>
    void PixelTo(double x, double y, double pr)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        int n = Math.Max(1, (int)Math.Floor(SizeAt(pr) + 0.5));
        if (lastPix == null) { Plot(ix, iy, n); lastPix = (ix, iy); return; }
        var (x0, y0) = lastPix.Value;
        if (x0 == ix && y0 == iy) return;
        int dx = Math.Abs(ix - x0), dy = -Math.Abs(iy - y0), sx = x0 < ix ? 1 : -1, sy = y0 < iy ? 1 : -1;
        int err = dx + dy;
        while (x0 != ix || y0 != iy)
        {
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
            Plot(x0, y0, n);
        }
        lastPix = (ix, iy);
    }

    /// <summary>픽셀 하나(크기 n 의 네모) 찍기 + 픽셀 퍼펙트(ㄴ자 모서리 지우기)</summary>
    void Plot(int px, int py, int n)
    {
        if (P.PixelPerfect && n == 1 && !P.Shading)
        {
            if (ppHist.Count >= 2)
            {
                var b = ppHist[^1]; var a = ppHist[^2];
                if (Math.Abs(a.x - px) == 1 && Math.Abs(a.y - py) == 1 && ((b.x == a.x && b.y == py) || (b.y == a.y && b.x == px)))
                {
                    PixelsAt(b.x, b.y, 1, true);                 // 가운데 모서리 픽셀 지우기
                    ppHist.RemoveAt(ppHist.Count - 1);
                }
            }
            ppHist.Add((px, py));
            if (ppHist.Count > 3) ppHist.RemoveAt(0);
        }
        PixelsAt(px, py, n, false);
    }

    void SetBuf(int x, int y, uint v)
    {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
        Buf.Set(x, y, v);
    }

    /// <summary>대칭까지 포함해 실제 픽셀 칠하기/지우기</summary>
    void PixelsAt(int px, int py, int n, bool erase)
    {
        int o2 = (int)Math.Floor((n - 1) / 2.0);
        var spots = new List<(int, int)> { (px, py) };
        if (sym == "v" || sym == "vh") spots.Add((W - 1 - px, py));
        if (sym == "h" || sym == "vh") spots.Add((px, H - 1 - py));
        if (sym == "vh") spots.Add((W - 1 - px, H - 1 - py));
        var p = P;
        foreach (var (qx, qy) in spots)
        {
            int x0 = qx - o2, y0 = qy - o2;
            if (erase) { for (int yy = y0; yy < y0 + n; yy++) for (int xx = x0; xx < x0 + n; xx++) SetBuf(xx, yy, 0); }
            else if (p.Shading) ShadeArea(x0 + n / 2.0, y0 + n / 2.0, n, true);
            else if (p.Dither > 0 && p.Dither < 1)
            {
                int ds = p.DitherSize == 2 ? 2 : 4;
                for (int yy = y0; yy < y0 + n; yy++) for (int xx = x0; xx < x0 + n; xx++) if (p.Dither > PixelArt.BayerAt(xx, yy, ds) - 1e-6) SetBuf(xx, yy, fill);
            }
            else for (int yy = y0; yy < y0 + n; yy++) for (int xx = x0; xx < x0 + n; xx++) SetBuf(xx, yy, fill);
            Mark(x0 - 1, y0 - 1, x0 + n + 1, y0 + n + 1);
        }
    }

    /// <summary>명암 잉크: 밑 그림 색을 팔레트 밝기 사다리에서 한 칸씩 (한 획에 한 픽셀은 한 번만)</summary>
    void ShadeArea(double cx, double cy, double size, bool square)
    {
        if (o.Sample == null) return;
        var pal = o.Palette ?? PixelArt.DefaultPalette.Select(PixelArt.ParseHex).ToList();
        shadeSeen ??= new HashSet<long>();
        int dir = (P.ShadeDir < 0 ? -1 : 1) * (ShadeFlip ? -1 : 1);
        double r = size / 2;
        int x0 = (int)Math.Floor(cx - r), y0 = (int)Math.Floor(cy - r), x1 = (int)Math.Ceiling(cx + r), y1 = (int)Math.Ceiling(cy + r);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (x < 0 || y < 0 || x >= W || y >= H) continue;
                if (!square && Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy)) > r + 0.01) continue;
                long k = (long)y * W + x;
                if (!shadeSeen.Add(k)) continue;
                uint s = Px.Unpremul(o.Sample(x, y));
                if ((s >> 24) == 0) continue;
                int sr = (int)((s >> 16) & 255), sg = (int)((s >> 8) & 255), sb = (int)(s & 255);
                var c = PixelArt.ShadeStep(sr, sg, sb, pal, dir);
                if (c == null)
                {
                    double f = dir < 0 ? 0.88 : 1.12;
                    int Adj(int v) => Math.Clamp((int)Math.Floor(v * f + (dir > 0 ? 8 : 0) + 0.5), 0, 255);
                    c = new[] { Adj(sr), Adj(sg), Adj(sb) };
                }
                Buf.Set(x, y, Px.Pack(255, (uint)c[0], (uint)c[1], (uint)c[2]));
            }
        Mark(x0 - 1, y0 - 1, x1 + 1, y1 + 1);
    }

    // ---- 선 따라 찍기 ------------------------------------------------------------
    /// <summary>입출 보정: 시작은 바로 가늘게, 끝은 taperOut 길이만큼 늦게 그려서 마지막에 가늘게</summary>
    void EmitAt(double x, double y, double pr, double d)
    {
        var p = P;
        if (NoTaper || (p.TaperIn == 0 && p.TaperOut == 0)) { Dab(x, y, pr, 1); return; }
        double k = p.TaperIn != 0 ? Math.Min(1, 0.08 + d / p.TaperIn) : 1;
        lastD = d;
        if (p.TaperOut == 0) { Dab(x, y, pr, k); return; }
        queue.Add((x, y, pr, d, k));
        while (queue.Count > 0 && d - queue[0].d > p.TaperOut)
        {
            var q = queue[0]; queue.RemoveAt(0);
            Dab(q.x, q.y, q.pr, q.k);
        }
    }

    void FlushTaper()
    {
        foreach (var q in queue) Dab(q.x, q.y, q.pr, q.k * Math.Min(1, 0.08 + (lastD - q.d) / P.TaperOut));
        queue.Clear();
    }

    void Dab(double x, double y, double pr, double k = 1)
    {
        var p = P;
        if (pixel) { PixelTo(x, y, pr); return; }
        if (p.Ribbon && !isVec) { dabColor = DabColorAt(pr); RibbonTo(x, y, SizeAt(pr) * k, AlphaAt(pr)); return; }
        double size = SizeAt(pr) * k;
        if (p.SizeJitter != 0) size *= 1 - rnd.NextDouble() * p.SizeJitter;
        double alpha = AlphaAt(pr);
        double rot = 0;
        if (p.Tip == "grain") rot = rnd.NextDouble() * Math.PI * 2;
        if (p.FollowDir) rot = lastDir;
        if (p.RotJitter != 0) rot += (rnd.NextDouble() - 0.5) * p.RotJitter * Math.PI * 2;
        if (p.Scatter != 0)
        {
            double a = rnd.NextDouble() * Math.PI * 2, d = rnd.NextDouble() * p.Scatter * size;
            x += Math.Cos(a) * d; y += Math.Sin(a) * d;
        }
        dabColor = DabColorAt(pr);
        Stamp(x, y, size, alpha, rot);
        if (sym == "v" || sym == "vh") Stamp(W - x, y, size, alpha, -rot);
        if (sym == "h" || sym == "vh") Stamp(x, H - y, size, alpha, -rot);
        if (sym == "vh") Stamp(W - x, H - y, size, alpha, rot);
        if (sym == "radial")
        {
            int n = Math.Max(2, o.SymN > 0 ? o.SymN : 6);
            double cx = W / 2.0, cy = H / 2.0, dx = x - cx, dy = y - cy;
            for (int i = 0; i < n; i++)
            {
                double a = (double)i / n * Math.PI * 2, c = Math.Cos(a), s = Math.Sin(a);
                if (i != 0) Stamp(cx + dx * c - dy * s, cy + dx * s + dy * c, size, alpha, rot + a);
                if (o.SymMirror) Stamp(cx - (dx * c - dy * s), cy + dx * s + dy * c, size, alpha, -rot - a);
            }
        }
    }

    void Line(BPt a, BPt b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return;
        lastDir = Math.Atan2(dy, dx);
        double t = next;
        while (t <= len)
        {
            double u = t / len, pr = a.P + (b.P - a.P) * u;
            EmitAt(a.X + dx * u, a.Y + dy * u, pr, travel + t);
            t += SpacingAt(pr);
        }
        next = t - len;
        travel += len;
    }

    void Quad(BPt a, BPt c, BPt b)
    {
        double len = Math.Sqrt((c.X - a.X) * (c.X - a.X) + (c.Y - a.Y) * (c.Y - a.Y)) + Math.Sqrt((b.X - c.X) * (b.X - c.X) + (b.Y - c.Y) * (b.Y - c.Y));
        int n = Math.Max(1, Math.Min(32, (int)Math.Ceiling(len / 3)));
        var prev = a;
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n, it = 1 - t;
            var q = new BPt(it * it * a.X + 2 * it * t * c.X + t * t * b.X, it * it * a.Y + 2 * it * t * c.Y + t * t * b.Y, a.P + (b.P - a.P) * t);
            Line(prev, q);
            prev = q;
        }
    }

    // ---- 손떨림 보정 ------------------------------------------------------------
    /// <summary>필압 다듬기: 펜을 대는 순간의 튀는 값과 떨림을 줄인다</summary>
    double PressureOf(double p)
    {
        np++;
        if (feel.StartRamp && np <= 4) p = Math.Min(p, 0.15 + np * 0.2);
        double k = 1 - Math.Min(0.9, feel.PressureSmooth);
        ps = ps == null ? p : ps + (p - ps) * k;
        return ps.Value;
    }

    /// <summary>1€ 필터 (Casiez 외, CHI 2012): 느릴 땐 떨림을 많이, 빠를 땐 지연을 적게</summary>
    BPt Euro(BPt pt)
    {
        double s = Math.Max(1, P.Stabilizer);
        double minCut = 4 / (1 + 0.5 * s) * feel.EuroK, beta = 0.006, dCut = 1;
        double t = pt.HasT ? pt.T : (et ?? 0) + 8;
        double dt = et == null ? 1 / 120.0 : Math.Max(1 / 1000.0, (t - et.Value) / 1000);
        et = t;
        double Alpha(double cut) { double tau = 1 / (2 * Math.PI * cut); return 1 / (1 + tau / dt); }
        if (!euOn) { euOn = true; eux = pt.X; euy = pt.Y; euv = 0; return pt; }
        double speed = Math.Sqrt((pt.X - eux) * (pt.X - eux) + (pt.Y - euy) * (pt.Y - euy)) * o.Zoom / dt;
        euv += (speed - euv) * Alpha(dCut);
        double a = Alpha(minCut + beta * euv);
        eux += (pt.X - eux) * a; euy += (pt.Y - euy) * a;
        return new BPt(eux, euy, pt.P, t);
    }

    /// <summary>끈 당기기: 펜이 끈 길이(반지름)보다 멀어질 때만 선 끝이 끌려온다</summary>
    BPt Lazy(BPt pt)
    {
        double R = P.Stabilizer * 2.5 / (o.Zoom > 0 ? o.Zoom : 1);
        LzPen = pt;
        if (Lz == null) { Lz = (pt.X, pt.Y); return pt; }
        var (lx, ly) = Lz.Value;
        double dx = pt.X - lx, dy = pt.Y - ly, d = Math.Sqrt(dx * dx + dy * dy);
        if (d > R) { double k = (d - R) / d; lx += dx * k; ly += dy * k; Lz = (lx, ly); }
        return new BPt(lx, ly, pt.P, pt.T);
    }

    /// <summary>무거운 펜: 선 끝이 용수철에 매달린 것처럼 살짝 늦게, 부드럽게 따라온다</summary>
    BPt Spring(BPt pt)
    {
        double tau = 0.006 + P.Stabilizer * 0.011, w = 1 / tau;
        if (!spOn) { spOn = true; spx = pt.X; spy = pt.Y; spvx = spvy = 0; sptx = pt.X; spty = pt.Y; spt = pt.HasT ? pt.T : 0; LzPen = pt; return pt; }
        double t = pt.HasT ? pt.T : spt + 8;
        double dt = Math.Clamp((t - spt) / 1000, 0.001, 0.05);
        int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004)); double hs = dt / n;
        for (int i = 1; i <= n; i++)
        {
            double tx = sptx + (pt.X - sptx) * i / n, ty = spty + (pt.Y - spty) * i / n;
            spvx += (w * w * (tx - spx) - 2 * w * spvx) * hs; spvy += (w * w * (ty - spy) - 2 * w * spvy) * hs;
            spx += spvx * hs; spy += spvy * hs;
        }
        sptx = pt.X; spty = pt.Y; spt = t;
        LzPen = pt; Lz = (spx, spy);
        return new BPt(spx, spy, pt.P, t);
    }

    BPt Smooth(BPt pt)
    {
        int n = (int)Math.Floor(P.Stabilizer + 0.5);
        pt.P = PressureOf(pt.P);
        raw.Add(pt);
        if (n <= 0) return pt;
        var mode = StabModeName;
        if (mode == "lazy") return Lazy(pt);
        if (mode == "spring") return Spring(pt);
        if (mode == "euro") return Euro(pt);
        int k = Math.Min(raw.Count, n * 2 + 1);
        double sx = 0, sy = 0, sp = 0, sw = 0;
        for (int i = 0; i < k; i++)
        {
            var q = raw[raw.Count - 1 - i]; double w = k - i;
            sx += q.X * w; sy += q.Y * w; sp += q.P * w; sw += w;
        }
        if (raw.Count > 64) raw.RemoveRange(0, raw.Count - 64);
        return new BPt(sx / sw, sy / sw, sp / sw, pt.T);
    }

    void Push(BPt pt)
    {
        if (Rec != null) { Rec.Add(Math.Round(pt.X, 2, MidpointRounding.AwayFromZero)); Rec.Add(Math.Round(pt.Y, 2, MidpointRounding.AwayFromZero)); Rec.Add(Math.Round(pt.P, 3, MidpointRounding.AwayFromZero)); Rec.Add(pt.HasT ? Math.Floor(pt.T + 0.5) : 0); }
        // 속도 (화면 px/ms) — 속도에 따른 굵기용
        if (pts.Count > 0)
        {
            var prev = pts[^1];
            if (pt.HasT && prev.HasT && pt.T > prev.T)
            {
                double v = Math.Sqrt((pt.X - prev.X) * (pt.X - prev.X) + (pt.Y - prev.Y) * (pt.Y - prev.Y)) * o.Zoom / (pt.T - prev.T);
                vel += (v - vel) * 0.3;
            }
        }
        pts.Add(pt);
        int n = pts.Count;
        if (n == 1)
        {
            EmitAt(pt.X, pt.Y, pt.P, 0);
            next = SpacingAt(pt.P);
            lastMid = pt; hasLastMid = true;
            return;
        }
        var a = pts[n - 2]; var b = pts[n - 1];
        var mid = new BPt((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.P + b.P) / 2);
        if (n == 2) Line(lastMid, mid); else Quad(lastMid, a, mid);
        lastMid = mid;
        if (pts.Count > 4) pts.RemoveAt(0);
    }

    // ---- 바깥에서 부르는 것 ------------------------------------------------------
    public void Begin(BPt pt) => Push(Smooth(pt));
    public void Add(BPt pt) => Push(Smooth(pt));

    /// <summary>획 끝: 보정 지연분을 마지막 지점까지 따라잡기 (필압은 지금 값 → 펜을 뗄 때 값으로 점점). 그린 전체 영역</summary>
    public Rect End(BPt? lastPt)
    {
        var p = P;
        if (lastPt is BPt lp && p.Stabilizer > 0 && p.CatchUp)
        {
            double p0 = ps ?? lp.P, p1 = Math.Min(p0, lp.P);
            double PAt(int i, int n) => p0 + (p1 - p0) * ((double)i / n);
            if (spOn)
            {
                double t0 = spt;
                for (int i = 1; i <= 12; i++) Push(Spring(new BPt(lp.X, lp.Y, PAt(i, 12), t0 + i * 16)));
                Push(new BPt(lp.X, lp.Y, p1, t0 + 220));
            }
            else if (Lz != null) Push(new BPt(lp.X, lp.Y, p1, lp.T));
            else if (euOn)
            {
                double t0 = et ?? 0;
                for (int i = 1; i <= 8; i++) Push(Euro(new BPt(lp.X, lp.Y, PAt(i, 8), t0 + i * 12)));
                Push(new BPt(lp.X, lp.Y, p1, t0 + 120));
            }
            else for (int i = 0; i < p.Stabilizer * 2 + 1; i++) Push(Smooth(lp));
        }
        if (pts.Count > 1 && hasLastMid) Line(lastMid, pts[^1]);
        if (queue.Count > 0) FlushTaper();
        return Total;
    }

    /// <summary>보정이 끝난 점들로 다시 그리기 (벡터 레이어)</summary>
    public Rect Replay(IEnumerable<BPt> points)
    {
        foreach (var pt in points) Push(pt);
        if (pts.Count > 1 && hasLastMid) Line(lastMid, pts[^1]);
        if (queue.Count > 0) FlushTaper();
        return Total;
    }

    /// <summary>버퍼를 비우고 처음 상태로 (직선 미리보기용). 지웠던 영역 (다시 합성해야 할 곳)</summary>
    public Rect Reset()
    {
        var keep = Total;
        if (!keep.IsEmpty) { Buf.Clear(keep.Inflate(1)); batch = batch.Union(keep.Inflate(1)); }
        raw.Clear(); pts.Clear(); next = 0; hasLastMid = false; travel = 0; queue.Clear();
        ribPrev = null; ribU = 0; lastPix = null; ppHist.Clear();
        Total = Rect.Empty;
        return keep;
    }

    /// <summary>점 목록을 보정 없이 그대로 그린다 (직선)</summary>
    public Rect Polyline(IList<BPt> points, bool closed = false)
    {
        NoTaper = true;
        var list = closed ? points.Concat(new[] { points[0] }).ToList() : points.ToList();
        Dab(list[0].X, list[0].Y, list[0].P);
        next = SpacingAt(list[0].P);
        for (int i = 1; i < list.Count; i++) Line(list[i - 1], list[i]);
        NoTaper = false;
        return Total;
    }
}
