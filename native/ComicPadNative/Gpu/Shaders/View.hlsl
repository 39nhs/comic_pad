// 그림판 화면 그리기 (픽셀 셰이더) — UI/CanvasView.RenderScreen 과 같은 규칙:
//  - 화면 점 → 문서 점 (확대·이동·회전·반전을 아핀 계수로), 확대 ≥ 1 또는 픽셀 아트 보기 = 가장 가까운 점
//  - 축소면 k×k 점 평균 (상자 거르기), 문서 밖 = 바탕색, 투명 = 흰 바탕 위로
//  - 선택 영역 테두리 (점선), 그 위에 GDI+ 겹침 그림(미리 곱한 알파) 얹기
//  - 회전이 없으면(반전은 됨) 화면 줄·칸마다의 문서 점을 CPU 가 double 로 계산한 표(Tab)를 씀 → 경계에 딱 걸리는 점까지 CPU 화면과 똑같음
cbuffer View : register(b0)
{
    float ox, oy, ax, ay;
    float bx, by; int k; int hasSel;
    int docW, docH; uint bg; int hasDoc;
    float invK; int axis; int rowBase; int stride;
};

Texture2D<uint> Comp : register(t0);     // 합성 결과 (R32_UINT, CPU 와 같은 꼴)
Texture2D<uint> Sel : register(t1);      // 선택 마스크 (R8_UINT)
Texture2D<float4> Ov : register(t2);     // 겹침 그림 (BGRA, 미리 곱한 알파)
Buffer<int> Tab : register(t3);          // 회전 없을 때: 칸 x → [k 개 표본 열, floor(dx), floor(dx+ax)], 줄 y → [k 개 표본 행, floor(dy), floor(dy+by)]

float4 VSMain(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

uint Mul(uint x, uint a) { uint t = x * a + 128; return (t + (t >> 8)) >> 8; }
uint Opaque(uint c)
{
    uint sa = c >> 24;
    if (sa == 255) return c;
    if (sa == 0) return 0xFFFFFFFF;
    uint ia = 255 - sa;
    // Px.Over(c, 흰색) | 0xFF000000
    return 0xFF000000 | ((((c >> 16) & 255) + Mul(255, ia)) << 16) | ((((c >> 8) & 255) + Mul(255, ia)) << 8) | ((c & 255) + Mul(255, ia));
}
bool InI(int a, int b) { return (uint)a < (uint)docW && (uint)b < (uint)docH && Sel.Load(int3(a, b, 0)) > 127; }
bool In(float fx, float fy) { return InI((int)floor(fx), (int)floor(fy)); }

float4 PSCanvas(float4 pos : SV_Position) : SV_Target
{
    int x = (int)pos.x, y = (int)pos.y;
    uint c = bg;
    if (hasDoc != 0 && axis != 0)
    {
        int cb = x * stride, rb = rowBase + y * stride;
        if (k <= 1)
        {
            int ix = Tab[cb + k], iy = Tab[rb + k];
            c = (uint)ix >= (uint)docW || (uint)iy >= (uint)docH ? bg : Opaque(Comp.Load(int3(ix, iy, 0)));
        }
        else
        {
            uint sb = 0, sg = 0, sr = 0; int n = 0, outside = 0;
            [loop] for (int j = 0; j < k; j++)
            {
                int iy = Tab[rb + j];
                [loop] for (int i = 0; i < k; i++)
                {
                    int ix = Tab[cb + i];
                    if ((uint)ix >= (uint)docW || (uint)iy >= (uint)docH) { outside++; continue; }
                    uint q = Opaque(Comp.Load(int3(ix, iy, 0)));
                    sb += q & 255; sg += (q >> 8) & 255; sr += (q >> 16) & 255; n++;
                }
            }
            c = n == 0 || outside > n ? bg : 0xFF000000 | ((sr / (uint)n) << 16) | ((sg / (uint)n) << 8) | (sb / (uint)n);
        }
        if (hasSel != 0)
        {
            int x0 = Tab[cb + k], x1 = Tab[cb + k + 1], y0 = Tab[rb + k], y1 = Tab[rb + k + 1];
            bool here = InI(x0, y0);
            if (here != InI(x1, y0) || here != InI(x0, y1)) c = ((x + y) / 4 % 2 == 0) ? 0xFF000000 : 0xFFFFFFFF;
        }
    }
    else if (hasDoc != 0)
    {
        precise float dx = ox + ax * (x + 0.5f) + bx * (y + 0.5f);
        precise float dy = oy + ay * (x + 0.5f) + by * (y + 0.5f);
        if (k <= 1)
        {
            int ix = (int)floor(dx), iy = (int)floor(dy);
            c = (uint)ix >= (uint)docW || (uint)iy >= (uint)docH ? bg : Opaque(Comp.Load(int3(ix, iy, 0)));
        }
        else
        {
            uint sb = 0, sg = 0, sr = 0; int n = 0, outside = 0;
            [loop] for (int j = 0; j < k; j++)
            {
                [loop] for (int i = 0; i < k; i++)
                {
                    precise float u = (i + 0.5f) * invK - 0.5f, v = (j + 0.5f) * invK - 0.5f;
                    precise float fx = dx + ax * u + bx * v, fy = dy + ay * u + by * v;
                    int ix = (int)floor(fx), iy = (int)floor(fy);
                    if ((uint)ix >= (uint)docW || (uint)iy >= (uint)docH) { outside++; continue; }
                    uint q = Opaque(Comp.Load(int3(ix, iy, 0)));
                    sb += q & 255; sg += (q >> 8) & 255; sr += (q >> 16) & 255; n++;
                }
            }
            c = n == 0 || outside > n ? bg : 0xFF000000 | ((sr / (uint)n) << 16) | ((sg / (uint)n) << 8) | (sb / (uint)n);
        }
        if (hasSel != 0)
        {
            bool here = In(dx, dy);
            if (here != In(dx + ax, dy + ay) || here != In(dx + bx, dy + by)) c = ((x + y) / 4 % 2 == 0) ? 0xFF000000 : 0xFFFFFFFF;
        }
    }
    float3 rgb = float3((c >> 16) & 255, (c >> 8) & 255, c & 255) / 255.0f;
    float4 o = Ov.Load(int3(x, y, 0));
    rgb = o.rgb + rgb * (1 - o.a);
    return float4(rgb, 1);
}
