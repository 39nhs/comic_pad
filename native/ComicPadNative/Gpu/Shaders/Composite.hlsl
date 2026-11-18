// GPU 레이어 합성 (계산 셰이더) — Core/Px.cs 의 정수 계산을 한 비트도 다르지 않게 옮김.
// 픽셀은 CPU 와 같은 꼴: R32_UINT 하나 = a<<24 | r<<16 | g<<8 | b (미리 곱한 알파).
//  - Tiles: 레이어·마스크 타일 저장소 (256×256 조각 배열). lut 로 "이 조각 영역의 타일 → 칸 번호"
//  - Src: 다른 작업 버퍼 (폴더·클리핑 결과 등), Dst: 지금 그리는 버퍼 (읽고 쓰기)
// 합성 모드 번호는 Core.Blend 와 같음.

#define OP_LAYER 0   // 레이어 타일을 Dst 에 mode 로 (s==0 이면 건너뜀, Copy·DestIn 제외)
#define OP_BUF   1   // Src 버퍼를 Dst 에 mode 로 (Compositor.CompositeAll)
#define OP_MASK  2   // 마스크 타일 알파로 Dst 를 줄임 (Px.Scale)
#define OP_TINT  3   // 레이어 컬러: 알파만 남기고 색 바꿈
#define OP_FILL  4   // 한 색으로 채움
#define OP_ADJUST 5  // 조정 레이어 보정 (FilterChain: 표·색 행렬·단색 입히기) — Modules/Filters/SvgFilter.cs 와 같은 계산

#define B_NORMAL 0
#define B_MULTIPLY 1
#define B_SCREEN 2
#define B_OVERLAY 3
#define B_DARKEN 4
#define B_LIGHTEN 5
#define B_DODGE 6
#define B_BURN 7
#define B_HARD 8
#define B_SOFT 9
#define B_DIFF 10
#define B_HUE 11
#define B_SAT 12
#define B_COLOR 13
#define B_LUM 14
#define B_ADD 15
#define B_ATOP 16
#define B_DESTIN 17
#define B_DESTOUT 18
#define B_COPY 19

cbuffer Params : register(b0)
{
    uint op; uint mode; uint kOp; uint color;
    int ox; int oy; uint w; uint h;
    int docW; int docH; int tx0; int ty0;
    uint lutW; uint nOps; uint pad1; uint pad2;
    int4 lut[64];
};

Texture2DArray<uint> Tiles : register(t0);
Texture2D<uint> Src : register(t1);
Buffer<uint> Prog : register(t2);         // 보정 프로그램: [종류, …] 다음에 표(256칸) 들
RWTexture2D<uint> Dst : register(u0);

uint Mul(uint x, uint a) { uint t = x * a + 128; return (t + (t >> 8)) >> 8; }
uint Pack(uint a, uint r, uint g, uint b) { return (a << 24) | (r << 16) | (g << 8) | b; }
uint Scale(uint c, uint k)
{
    if (k >= 255) return c;
    if (k == 0) return 0;
    return Pack(Mul(c >> 24, k), Mul((c >> 16) & 255, k), Mul((c >> 8) & 255, k), Mul(c & 255, k));
}
uint Over(uint s, uint d)
{
    uint sa = s >> 24;
    if (sa == 255) return s;
    if (sa == 0) return d;
    uint ia = 255 - sa;
    return Pack(sa + Mul(d >> 24, ia), ((s >> 16) & 255) + Mul((d >> 16) & 255, ia), ((s >> 8) & 255) + Mul((d >> 8) & 255, ia), (s & 255) + Mul(d & 255, ia));
}

// ---- CPU(C#) float 와 같은 결과: GPU 의 나눗셈(최대 2.5ULP)·제곱근을 IEEE 정확 반올림으로 바로잡음 ----
// 곱의 정확한 오차 (Dekker): a*b = p + e
void TwoProd(float a, float b, out float p, out float e)
{
    precise float pp = a * b;
    precise float ca = 4097.0f * a; precise float ah = ca - (ca - a); precise float al = a - ah;
    precise float cb = 4097.0f * b; precise float bh = cb - (cb - b); precise float bl = b - bh;
    precise float ee = ((ah * bh - pp) + ah * bl + al * bh) + al * bl;
    p = pp; e = ee;
}
// a - x*b (거의 정확)
float Resid(float a, float x, float b) { float p, e; TwoProd(x, b, p, e); precise float r = (a - p) - e; return r; }
// 정확히 반올림한 a/b: 근삿값 → 뉴턴 한 번 → 이웃 세 후보 중 나머지가 가장 작은 것
float Div(float a, float b)
{
    precise float q = a / b;
    if (b == 0 || q == 0 || isinf(q) || isnan(q)) return q;
    precise float q1 = q + Resid(a, q, b) / b;
    if (!isinf(q1) && !isnan(q1) && q1 != 0) q = q1;
    float qm = asfloat(asuint(q) - 1), qp = asfloat(asuint(q) + 1);
    float r0 = abs(Resid(a, q, b)), rm = abs(Resid(a, qm, b)), rp = abs(Resid(a, qp, b));
    if (rm < r0) { q = qm; r0 = rm; }
    if (rp < r0) q = qp;
    return q;
}
// 정확히 반올림한 제곱근
float Sqrt(float x)
{
    precise float s = sqrt(x);
    if (!(x > 0) || isinf(x) || s == 0) return s;
    float sm = asfloat(asuint(s) - 1), sp = asfloat(asuint(s) + 1);
    float r0 = abs(Resid(x, s, s)), rm = abs(Resid(x, sm, sm)), rp = abs(Resid(x, sp, sp));
    if (rm < r0) { s = sm; r0 = rm; }
    if (rp < r0) s = sp;
    return s;
}

float HardL(float b, float s) { precise float r = s <= 0.5f ? b * 2 * s : 1 - (1 - b) * (1 - (2 * s - 1)); return r; }

float Sep(uint m, float b, float s)
{
    precise float r = s;
    switch (m)
    {
        case B_MULTIPLY: r = b * s; break;
        case B_SCREEN: r = b + s - b * s; break;
        case B_OVERLAY: r = HardL(s, b); break;
        case B_DARKEN: r = min(b, s); break;
        case B_LIGHTEN: r = max(b, s); break;
        case B_DODGE: r = b <= 0 ? 0 : s >= 1 ? 1 : min(1, Div(b, 1 - s)); break;
        case B_BURN: r = b >= 1 ? 1 : s <= 0 ? 0 : 1 - min(1, Div(1 - b, s)); break;
        case B_HARD: r = HardL(b, s); break;
        case B_SOFT:
        {
            if (s <= 0.5f) r = b - (1 - 2 * s) * b * (1 - b);
            else
            {
                precise float dd = b <= 0.25f ? ((16 * b - 12) * b + 4) * b : Sqrt(b);
                r = b + (2 * s - 1) * (dd - b);
            }
            break;
        }
        case B_DIFF: r = abs(b - s); break;
    }
    return r;
}

float Lum(float3 c) { precise float l = 0.3f * c.r + 0.59f * c.g + 0.11f * c.b; return l; }
float3 ClipC(float3 c)
{
    precise float l = Lum(c);
    precise float n = min(c.r, min(c.g, c.b));
    precise float x = max(c.r, max(c.g, c.b));
    precise float3 o = c;
    if (n < 0) { o.r = l + Div((o.r - l) * l, l - n); o.g = l + Div((o.g - l) * l, l - n); o.b = l + Div((o.b - l) * l, l - n); }
    if (x > 1) { o.r = l + Div((o.r - l) * (1 - l), x - l); o.g = l + Div((o.g - l) * (1 - l), x - l); o.b = l + Div((o.b - l) * (1 - l), x - l); }
    return o;
}
float3 SetLum(float3 c, float l) { precise float d = l - Lum(c); return ClipC(float3(c.r + d, c.g + d, c.b + d)); }
float Sat(float3 c) { precise float s = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b)); return s; }
float3 SetSat(float3 c, float s)
{
    precise float mx = max(c.r, max(c.g, c.b)), mn = min(c.r, min(c.g, c.b));
    precise float3 o = float3(0, 0, 0);
    if (mx > mn) { o.r = Div((c.r - mn) * s, mx - mn); o.g = Div((c.g - mn) * s, mx - mn); o.b = Div((c.b - mn) * s, mx - mn); }
    return o;
}
float3 NonSep(uint m, float3 b, float3 s)
{
    if (m == B_HUE) return SetLum(SetSat(s, Sat(b)), Lum(b));
    if (m == B_SAT) return SetLum(SetSat(b, Sat(s)), Lum(b));
    if (m == B_COLOR) return SetLum(s, Lum(b));
    return SetLum(b, Lum(s));
}
uint P(float v) { precise float t = v * 255 + 0.5f; return (uint)clamp((int)t, 0, 255); }

uint Composite(uint s, uint d, uint m, uint k)
{
    s = Scale(s, k);
    if (m == B_NORMAL) return Over(s, d);
    if (m == B_COPY) return s;
    if (m == B_ATOP)
    {
        uint da = d >> 24; if (da == 0) return d;
        uint sa = s >> 24, ia = 255 - sa;
        return Pack(da, Mul((s >> 16) & 255, da) + Mul((d >> 16) & 255, ia), Mul((s >> 8) & 255, da) + Mul((d >> 8) & 255, ia), Mul(s & 255, da) + Mul(d & 255, ia));
    }
    if (m == B_DESTIN) return Scale(d, s >> 24);
    if (m == B_DESTOUT) return Scale(d, 255 - (s >> 24));
    if (m == B_ADD)
        return Pack(min(255u, (s >> 24) + (d >> 24)), min(255u, ((s >> 16) & 255) + ((d >> 16) & 255)), min(255u, ((s >> 8) & 255) + ((d >> 8) & 255)), min(255u, (s & 255) + (d & 255)));
    uint sA = s >> 24, dA = d >> 24;
    if (sA == 0) return d;
    if (dA == 0) return s;
    precise float as_ = Div((float)sA, 255.0f), ab = Div((float)dA, 255.0f);
    precise float3 sc = float3(Div(Div((float)((s >> 16) & 255), 255.0f), as_), Div(Div((float)((s >> 8) & 255), 255.0f), as_), Div(Div((float)(s & 255), 255.0f), as_));
    precise float3 bc = float3(Div(Div((float)((d >> 16) & 255), 255.0f), ab), Div(Div((float)((d >> 8) & 255), 255.0f), ab), Div(Div((float)(d & 255), 255.0f), ab));
    precise float3 rc = float3(0, 0, 0);
    if (m >= B_HUE && m <= B_LUM) rc = NonSep(m, bc, sc);
    else { rc.r = Sep(m, bc.r, sc.r); rc.g = Sep(m, bc.g, sc.g); rc.b = Sep(m, bc.b, sc.b); }
    precise float oa = as_ + ab - as_ * ab;
    precise float k1 = as_ * (1 - ab), k2 = ab * (1 - as_), k3 = as_ * ab;
    precise float orr = k1 * sc.r + k2 * bc.r + k3 * rc.r;
    precise float org = k1 * sc.g + k2 * bc.g + k3 * rc.g;
    precise float orb = k1 * sc.b + k2 * bc.b + k3 * rc.b;
    return Pack(P(oa), P(orr), P(org), P(orb));
}

// ---- 조정 보정 (FilterChain.Apply 와 같음) ----
uint B8(float v) { precise float t = v + 0.5f; return v <= 0 ? 0u : v >= 255 ? 255u : (uint)t; }
void MatOp(uint o, inout uint r, inout uint g, inout uint b, uint a)
{
    precise float R = Div((float)r, 255.0f), G = Div((float)g, 255.0f), B = Div((float)b, 255.0f), A = Div((float)a, 255.0f);
    precise float nr = asfloat(Prog[o]) * R + asfloat(Prog[o + 1]) * G + asfloat(Prog[o + 2]) * B + asfloat(Prog[o + 3]) * A + asfloat(Prog[o + 4]);
    precise float ng = asfloat(Prog[o + 5]) * R + asfloat(Prog[o + 6]) * G + asfloat(Prog[o + 7]) * B + asfloat(Prog[o + 8]) * A + asfloat(Prog[o + 9]);
    precise float nb = asfloat(Prog[o + 10]) * R + asfloat(Prog[o + 11]) * G + asfloat(Prog[o + 12]) * B + asfloat(Prog[o + 13]) * A + asfloat(Prog[o + 14]);
    r = B8(nr * 255); g = B8(ng * 255); b = B8(nb * 255);
}
uint AdjustPx(uint c)
{
    uint a = c >> 24;
    if (a == 0 || nOps == 0) return c;
    uint r, g, b;
    if (a == 255) { r = (c >> 16) & 255; g = (c >> 8) & 255; b = c & 255; }
    else
    {
        r = min(255u, (((c >> 16) & 255) * 255 + a / 2) / a);
        g = min(255u, (((c >> 8) & 255) * 255 + a / 2) / a);
        b = min(255u, ((c & 255) * 255 + a / 2) / a);
    }
    uint pc = 0;
    [loop] for (uint i = 0; i < nOps; i++)
    {
        uint t = Prog[pc];
        if (t == 0)
        {
            uint lr = Prog[pc + 1], lg = Prog[pc + 2], lb = Prog[pc + 3];
            if (lr != 0xFFFFFFFF) r = Prog[lr + r];
            if (lg != 0xFFFFFFFF) g = Prog[lg + g];
            if (lb != 0xFFFFFFFF) b = Prog[lb + b];
            pc += 4;
        }
        else if (t == 1) { MatOp(pc + 1, r, g, b, a); pc += 21; }
        else
        {
            uint tr = r, tg = g, tb = b;
            MatOp(pc + 1, tr, tg, tb, a);
            precise float k2 = asfloat(Prog[pc + 21]), k3 = asfloat(Prog[pc + 22]);
            precise float vr = k2 * (float)tr + k3 * (float)r, vg = k2 * (float)tg + k3 * (float)g, vb = k2 * (float)tb + k3 * (float)b;
            r = B8(vr); g = B8(vg); b = B8(vb);
            pc += 23;
        }
    }
    return a == 255 ? Pack(255, r, g, b) : Pack(a, Mul(r, a), Mul(g, a), Mul(b, a));
}

// 이 점의 타일 값. 문서 밖이거나 빈 타일이면 false (CPU 의 Blit 도 빈 타일은 건너뜀)
bool TileAt(uint2 p, out uint v)
{
    v = 0;
    int gx = ox + (int)p.x, gy = oy + (int)p.y;
    if (gx < 0 || gy < 0 || gx >= docW || gy >= docH) return false;
    int ti = ((gy >> 8) - ty0) * (int)lutW + ((gx >> 8) - tx0);
    int4 q = lut[ti >> 2];
    int c = ti & 3;
    int slot = c == 0 ? q.x : c == 1 ? q.y : c == 2 ? q.z : q.w;
    if (slot < 0) return false;
    v = Tiles.Load(int4(gx & 255, gy & 255, slot, 0));
    return true;
}

[numthreads(16, 16, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= w || id.y >= h) return;
    uint2 p = id.xy;
    if (op == OP_FILL) { Dst[p] = color; return; }
    uint d = Dst[p];
    if (op == OP_LAYER)
    {
        uint s;
        if (!TileAt(p, s)) return;
        if (s == 0 && mode != B_COPY && mode != B_DESTIN) return;
        Dst[p] = Composite(s, d, mode, kOp);
    }
    else if (op == OP_BUF)
    {
        uint s = Src[p];
        if (mode == B_DESTIN) { Dst[p] = Scale(d, s >> 24); return; }
        if (s == 0 && mode != B_COPY) return;
        Dst[p] = Composite(s, d, mode, kOp);
    }
    else if (op == OP_MASK)
    {
        if (d == 0) return;
        uint m;
        TileAt(p, m);
        Dst[p] = Scale(d, m >> 24);
    }
    else if (op == OP_ADJUST)
    {
        Dst[p] = AdjustPx(d);
    }
    else if (op == OP_TINT)
    {
        uint a = d >> 24;
        if (a != 0) Dst[p] = Scale(color, a);
    }
}
