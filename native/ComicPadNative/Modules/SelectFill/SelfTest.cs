// 선택·채우기 자체 시험 (창 없이): 환경 변수 COMICPAD_SELFTEST_SELECTFILL=결과파일 로 실행하면 시험 후 바로 끝남.
// 메뉴 도움말 > 선택·채우기 자체 시험 으로도 돌릴 수 있다. 정해진 입력으로만 시험 (난수 씨앗 고정).
using System.Diagnostics;
using System.Text;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.SelectFill;

public static class SelfTest
{
    static readonly StringBuilder log = new();
    static int pass, fail;
    static void Ok(bool c, string name) { if (c) pass++; else fail++; log.AppendLine((c ? "PASS " : "FAIL ") + name); }

    public static (bool ok, string report) Run(bool perf = true, bool withApp = true)
    {
        log.Clear(); pass = fail = 0;
        void T(string name, Action a) { try { a(); } catch (Exception ex) { Ok(false, name + " 예외: " + ex.Message); } }
        T("웹 판과 같은 결과", WebParity);
        T("팽창·침식", DilateErode);
        T("플러드", FloodTest);
        T("채우기 허용 오차", FillTolerance);
        T("채우기 틈 막기", FillGap);
        T("자동 밑색 라벨", Flats);
        T("색 점 칠하기", Hints);
        T("선택 합치기", SelApply);
        T("선택 확장·축소·흐리기", GrowFeather);
        T("디더", DitherTest);
        T("선택 다듬기", RefineTest);
        if (withApp) T("채우기 적용·실행취소", FillApplyUndo);
        if (perf) T("큰 원고 속도", Perf);
        log.Insert(0, $"선택·채우기 자체 시험: 통과 {pass}, 실패 {fail}\r\n");
        return (fail == 0, log.ToString());
    }

    // ---- 도우미 ----------------------------------------------------------------------------
    static byte[] RandMask(Random r, int w, int h, double p) { var m = new byte[w * h]; for (int i = 0; i < m.Length; i++) m[i] = r.NextDouble() < p ? (byte)1 : (byte)0; return m; }
    static byte[] DilateRef(byte[] s, int w, int h, int r)
    {
        var o = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            bool any = false;
            for (int yy = Math.Max(0, y - r); yy <= Math.Min(h - 1, y + r) && !any; yy++) for (int xx = Math.Max(0, x - r); xx <= Math.Min(w - 1, x + r); xx++) if (s[yy * w + xx] != 0) { any = true; break; }
            o[y * w + x] = any ? (byte)1 : (byte)0;
        }
        return o;
    }
    static byte[] BfsRef(byte[] pass, int w, int h, int sx, int sy)
    {
        var o = new byte[w * h];
        if (pass[sy * w + sx] == 0) return o;
        var q = new Queue<int>(); q.Enqueue(sy * w + sx); o[sy * w + sx] = 1;
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % w, y = i / w;
            foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = ny * w + nx;
                if (pass[j] != 0 && o[j] == 0) { o[j] = 1; q.Enqueue(j); }
            }
        }
        return o;
    }
    static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    static byte[] Img(int w, int h, uint rgba) { var d = new byte[w * h * 4]; for (int i = 0; i < w * h; i++) { d[i * 4] = (byte)(rgba >> 24); d[i * 4 + 1] = (byte)(rgba >> 16); d[i * 4 + 2] = (byte)(rgba >> 8); d[i * 4 + 3] = (byte)rgba; } return d; }
    static void Put(byte[] d, int w, int x, int y, uint rgba) { int p = (y * w + x) * 4; d[p] = (byte)(rgba >> 24); d[p + 1] = (byte)(rgba >> 16); d[p + 2] = (byte)(rgba >> 8); d[p + 3] = (byte)rgba; }

    // ---- 시험 --------------------------------------------------------------------------------
    static void DilateErode()
    {
        var rnd = new Random(1234);
        bool ok = true, ok2 = true;
        foreach (var (w, h) in new[] { (1, 1), (3, 2), (17, 9), (40, 33) })
            for (int r = 0; r <= 5; r++)
            {
                var m = RandMask(rnd, w, h, 0.08);
                var d = Raster.Dilate(m, w, h, r);
                if (!Same(d, r == 0 ? m : DilateRef(m, w, h, r))) ok = false;
                var inv = m.Select(v => v != 0 ? (byte)0 : (byte)1).ToArray();
                var e = Raster.Erode(m, w, h, r);
                var eref = r == 0 ? m : DilateRef(inv, w, h, r).Select(v => v != 0 ? (byte)0 : (byte)1).ToArray();
                if (!Same(e, eref)) ok2 = false;
            }
        Ok(ok, "팽창 = 정사각 커널 직접 계산과 같음");
        Ok(ok2, "침식 = 반전·팽창·반전과 같음 (문서 밖은 영향 없음)");
        Ok(Raster.Dilate(new byte[] { 0, 1, 0, 0, 0 }, 5, 1, 1.5).SequenceEqual(new byte[] { 1, 1, 1, 1, 0 }), "팽창 반경 반올림 (1.5 → 2, JS Math.round)");
    }

    static void FloodTest()
    {
        var rnd = new Random(99);
        bool ok = true, okb = true;
        for (int k = 0; k < 30; k++)
        {
            int w = 5 + rnd.Next(40), h = 5 + rnd.Next(40);
            var p = RandMask(rnd, w, h, 0.62);
            int sx = rnd.Next(w), sy = rnd.Next(h);
            var (m, b) = Raster.Flood(p, w, h, sx, sy);
            var rf = BfsRef(p, w, h, sx, sy);
            if (!Same(m, rf)) ok = false;
            var rb = Raster.MaskBounds(rf, w, h);
            if (b != rb) okb = false;
        }
        Ok(ok, "스캔라인 플러드 = 4방향 BFS");
        Ok(okb, "플러드 범위 = 마스크 범위");
    }

    static void FillTolerance()
    {
        int w = 20, h = 10;
        var d = Img(w, h, 0xFFFFFFFF);
        for (int y = 0; y < h; y++) Put(d, w, 10, y, 0xF0F0F0FF);      // 15 차이 나는 회색 세로줄
        var (m1, _) = Raster.FillMask(d, w, h, 2, 2, new Raster.FillOpt { Tolerance = 12 });
        var (m2, _) = Raster.FillMask(d, w, h, 2, 2, new Raster.FillOpt { Tolerance = 15 });
        Ok(m1[2 * w + 15] == 0 && m1[2 * w + 9] == 1, "허용 오차 12: 15 차이 줄에서 멈춤");
        Ok(m2[2 * w + 15] == 1, "허용 오차 15: 같은 색으로 봄 (d <= tol)");
        // 투명 위: 반투명 픽셀은 미리 곱한 값으로 비교 (rgb*a/255 와 a 차이)
        var t = new byte[w * h * 4];
        Put(t, w, 5, 5, 0xFF000008);   // 빨강 알파 8 → 미리 곱한 빨강 8, 알파 차이 8
        var (m3, _) = Raster.FillMask(t, w, h, 0, 0, new Raster.FillOpt { Tolerance = 7 });
        var (m4, _) = Raster.FillMask(t, w, h, 0, 0, new Raster.FillOpt { Tolerance = 8 });
        Ok(m3[5 * w + 5] == 0 && m4[5 * w + 5] == 1, "반투명 픽셀은 미리 곱한 색·알파 최대 차이로 비교");
        var (m5, b5) = Raster.FillMask(d, w, h, 2, 2, new Raster.FillOpt { Tolerance = 12, Expand = 2 });
        Ok(m5[2 * w + 11] == 1 && m5[2 * w + 12] == 0 && b5 == new Rect(0, 0, 12, 10), "영역 확장 2px = 정사각 팽창");
    }

    static void FillGap()
    {
        // 흰 바탕에 2px 검은 세로선 (x=30,31), y=18~20 에 3px 틈
        int w = 60, h = 40;
        var d = Img(w, h, 0xFFFFFFFF);
        for (int y = 0; y < h; y++) if (y < 18 || y > 20) { Put(d, w, 30, y, 0x000000FF); Put(d, w, 31, y, 0x000000FF); }
        var (m0, _) = Raster.FillMask(d, w, h, 10, 10, new Raster.FillOpt { Tolerance = 12, Gap = 0 });
        Ok(m0[10 * w + 50] == 1, "틈 막기 끔: 틈으로 새어 오른쪽도 칠함");
        var (m, b) = Raster.FillMask(d, w, h, 10, 10, new Raster.FillOpt { Tolerance = 12, Gap = 4 });
        bool leftFull = true, rightNone = true;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            if (x < 30 && m[y * w + x] == 0) leftFull = false;
            if (x >= 32 && m[y * w + x] != 0) rightNone = false;
        }
        Ok(rightNone, "틈 막기 4px: 3px 틈 너머로 새지 않음");
        Ok(leftFull, "틈 막기 4px: 두껍게 한 만큼 선 바로 앞까지 되돌려 채움");
        Ok(m[19 * w + 30] == 1 && m[19 * w + 31] == 0, "경쟁 되돌려 채우기: 틈 한가운데를 양쪽이 반씩 차지");
        Ok(m[10 * w + 30] == 0, "선 자체는 칠하지 않음");
        Ok(b == new Rect(0, 0, 31, 40), "틈 막기 결과 범위");
        // 시작점이 두껍게 한 벽 안이면 틈 막기 없이 (웹과 같음)
        var (m2, _) = Raster.FillMask(d, w, h, 29, 10, new Raster.FillOpt { Tolerance = 12, Gap = 4 });
        Ok(m2[10 * w + 50] == 1, "시작점이 선 가까이(벽 안)면 틈 막기를 쓰지 않음");
        // 영역 확장은 되돌려 채운 뒤에
        var (m3, _) = Raster.FillMask(d, w, h, 10, 10, new Raster.FillOpt { Tolerance = 12, Gap = 4, Expand = 1 });
        Ok(m3[10 * w + 30] == 1 && m3[10 * w + 31] == 0 && m3[19 * w + 31] == 1, "틈 막기 + 영역 확장 1px");
        // 큰 틈 막기 (50px) 도 터지지 않고 계산
        var (m4, b4) = Raster.FillMask(d, w, h, 5, 5, new Raster.FillOpt { Tolerance = 12, Gap = 50 });
        Ok(b4.IsEmpty || m4[5 * w + 5] == 1, "틈 막기 50px 계산");
    }

    static void Flats()
    {
        // 투명 40x40 에 닫힌 사각 테두리 (10,10)-(29,29) 1px
        int w = 40, h = 40;
        var d = new byte[w * h * 4];
        for (int i = 10; i <= 29; i++) { Put(d, w, i, 10, 0x000000FF); Put(d, w, i, 29, 0x000000FF); Put(d, w, 10, i, 0x000000FF); Put(d, w, 29, i, 0x000000FF); }
        var r = AutoFlat.FlatLabels(d, w, h, new AutoFlat.Opt { Gap = 0, Under = 1, MinArea = 30, Outside = true });
        Ok(r.Count == 1, $"닫힌 영역 1개 (바깥 제외) — {r.Count}");
        Ok(r.L[20 * w + 20] == 1 && r.L[0] == 0, "안쪽 = 1, 바깥 = 0");
        // 선 아래로 번지기: 같은 단계에서는 번호 순(위·왼쪽 먼저) — 왼쪽 선은 바깥(-1)이, 오른쪽 선은 안쪽이 차지
        Ok(r.L[20 * w + 10] == 0 && r.L[20 * w + 29] == 1, "선 아래 번지기 경쟁 순서 (웹과 같음)");
        var r2 = AutoFlat.FlatLabels(d, w, h, new AutoFlat.Opt { Gap = 0, Under = 0, MinArea = 30, Outside = false });
        Ok(r2.Count == 2 && r2.Edge[1] && !r2.Edge[2], "바깥 포함이면 2개, 테두리 닿음 표시");
        var c = AutoFlat.Colorize(r.L, r.Count, w, h);
        var (cr, cg, cb) = AutoFlat.RegionColor(1);
        Ok(c[(20 * w + 20) * 4] == cr && c[(20 * w + 20) * 4 + 1] == cg && c[(20 * w + 20) * 4 + 2] == cb && c[(20 * w + 20) * 4 + 3] == 255 && c[3] == 0, "영역 색 칠하기");
        Ok(AutoFlat.RegionColor(1) == (175, 233, 192) && AutoFlat.RegionColor(2) == (192, 134, 233), $"황금각 파스텔 색 (웹 값과 같음) {AutoFlat.RegionColor(1)} {AutoFlat.RegionColor(2)}");
        // 작은 조각은 0 → 이웃이 번져 들어옴
        var r3 = AutoFlat.FlatLabels(d, w, h, new AutoFlat.Opt { Gap = 0, Under = 0, MinArea = 1000, Outside = true });
        Ok(r3.Count == 0, "minArea 보다 작은 영역은 번호 없음");
        // 틈 막기: 테두리에 2px 틈 → gap 0 이면 안팎이 이어져 바깥(0개), gap 2 면 닫힘
        var g = (byte[])d.Clone();
        Put(g, w, 29, 19, 0); Put(g, w, 29, 20, 0);
        Ok(AutoFlat.FlatLabels(g, w, h, new AutoFlat.Opt { Gap = 0, Under = 0, MinArea = 30 }).Count == 0, "틈 있으면 바깥과 이어짐");
        Ok(AutoFlat.FlatLabels(g, w, h, new AutoFlat.Opt { Gap = 2, Under = 0, MinArea = 30 }).Count == 1, "틈 막기 2px 로 닫힘");
    }

    static void Hints()
    {
        // 두 영역: 안쪽 사각(테두리 안 닿음) + 바깥(테두리 닿음)
        int w = 40, h = 40;
        var d = new byte[w * h * 4];
        for (int i = 10; i <= 29; i++) { Put(d, w, i, 10, 0x000000FF); Put(d, w, i, 29, 0x000000FF); Put(d, w, 10, i, 0x000000FF); Put(d, w, 29, i, 0x000000FF); }
        var r = AutoFlat.FlatLabels(d, w, h, new AutoFlat.Opt { Gap = 0, Under = 2, MinArea = 20, Outside = false });
        int outside = r.L[0], inside = r.L[20 * w + 20];
        var hint = new byte[w * h * 4];
        Put(hint, w, 2, 2, 0xFF0000FF); Put(hint, w, 3, 2, 0xFF0000FF); Put(hint, w, 4, 2, 0x00FF00FF);   // 바깥: 빨강 2, 초록 1
        var o = AutoFlat.ColorizeHints(r.L, r.Count, r.Edge, hint, w, h, true);
        Ok(o.Hinted == 1 && o.Filled == 2, $"힌트 1곳, 이웃 따라 2개 칠함 ({o.Hinted}, {o.Filled})");
        Ok(o.Rgba[0] == 255 && o.Rgba[1] == 0 && o.Rgba[(20 * w + 20) * 4] == 255, "가장 많이 찍힌 색 + 안쪽은 이웃 색으로");
        var o2 = AutoFlat.ColorizeHints(r.L, r.Count, r.Edge, hint, w, h, false);
        Ok(o2.Filled == 1 && o2.Rgba[(20 * w + 20) * 4 + 3] == 0, "spread 끄면 힌트 없는 영역은 비움");
        // 안쪽에만 힌트 → 바깥(테두리 닿음)은 따라가지 않음
        var hint2 = new byte[w * h * 4];
        Put(hint2, w, 20, 20, 0x0000FFFF);
        var o3 = AutoFlat.ColorizeHints(r.L, r.Count, r.Edge, hint2, w, h, true);
        Ok(o3.Filled == 1 && o3.Rgba[3] == 0, "테두리 닿는 영역은 이웃 색을 따라가지 않음");
        Ok(outside != inside, "안·밖 번호 다름");
    }

    static void SelApply()
    {
        var s = new Selection(10, 10);
        var full = new Rect(0, 0, 10, 10);
        byte[] Shape(Rect r, byte v) { var a = new byte[r.W * r.H]; Array.Fill(a, v); return a; }
        SelUtil.ApplyShape(s, new Rect(2, 2, 3, 3), Shape(new Rect(2, 2, 3, 3), 255), "subtract");
        Ok(s.Mask == null, "선택 없을 때 빼기는 아무것도 안 함");
        SelUtil.ApplyShape(s, new Rect(2, 2, 3, 3), Shape(new Rect(2, 2, 3, 3), 255), "intersect");
        Ok(s.Mask != null && s.Mask[3 * 10 + 3] == 255 && s.Mask[0] == 0, "선택 없을 때 교차 = 새로");
        SelUtil.ApplyShape(s, new Rect(4, 4, 4, 4), Shape(new Rect(4, 4, 4, 4), 128), "add");
        Ok(s.Mask[4 * 10 + 4] == 255 && s.Mask[6 * 10 + 6] == 128, "추가 = source-over (s + d(1-s))");
        SelUtil.ApplyShape(s, new Rect(4, 4, 4, 4), Shape(new Rect(4, 4, 4, 4), 128), "add");
        Ok(s.Mask[6 * 10 + 6] == 128 + Px.Mul(128, 127), "반투명 두 번 추가");
        SelUtil.ApplyShape(s, new Rect(0, 0, 5, 5), Shape(new Rect(0, 0, 5, 5), 255), "intersect");
        Ok(s.Mask[6 * 10 + 6] == 0 && s.Mask[3 * 10 + 3] == 255, "교차: 모양 밖은 지움");
        SelUtil.ApplyShape(s, full, Shape(full, 255), "subtract");
        Ok(s.Mask == null, "전부 빼면 선택 없음");
        // 모양이 문서 밖으로 나가도 잘림
        SelUtil.ApplyShape(s, new Rect(-3, -3, 5, 5), Shape(new Rect(-3, -3, 5, 5), 255), "replace");
        Ok(s.Mask != null && s.Mask[1 * 10 + 1] == 255 && s.Mask[2 * 10 + 2] == 0 && SelUtil.Bounds(s) == new Rect(0, 0, 2, 2), "문서 밖 모양 잘림");
        // 다각형·타원 모양 (가장자리 부드럽게)
        var pr = new Rect(0, 0, 10, 10);
        var poly = SelUtil.PolygonShape(pr, new List<PointF> { new(1, 1), new(9, 1), new(9, 9), new(1, 9) });
        Ok(poly[5 * 10 + 5] == 255 && poly[0] == 0 && poly[1 * 10 + 1] == 255 && poly[9 * 10 + 9] == 0, "다각형: 픽셀 (i,j) = [i,i+1] 영역");
        var el = SelUtil.EllipseShape(pr, 0, 0, 10, 10);
        Ok(el[5 * 10 + 5] == 255 && el[0] < 64 && el[0 * 10 + 5] > 64, "타원 모양");
    }

    static void GrowFeather()
    {
        var s = new Selection(30, 30);
        s.Mask = new byte[900];
        for (int y = 10; y < 20; y++) for (int x = 10; x < 20; x++) s.Mask[y * 30 + x] = 255;
        SelCommands.Grow(s, 2);
        Ok(SelUtil.Bounds(s) == new Rect(8, 8, 14, 14) && s.Mask[8 * 30 + 8] == 255, "확장 +2px = 정사각 팽창");
        SelCommands.Grow(s, -4);
        Ok(SelUtil.Bounds(s) == new Rect(12, 12, 6, 6), "축소 -4px");
        SelCommands.Grow(s, -10);
        Ok(s.Mask == null, "너무 줄이면 선택 없음");
        s.Mask = new byte[900];
        for (int y = 10; y < 20; y++) for (int x = 10; x < 20; x++) s.Mask[y * 30 + x] = 255;
        long before = s.Mask.Sum(v => (long)v);
        SelCommands.Feather(s, 2);
        long after = s.Mask.Sum(v => (long)v);
        Ok(Math.Abs(after - before) < before * 0.02 && s.Mask[15 * 30 + 15] == 255 && s.Mask[15 * 30 + 9] > 0 && s.Mask[15 * 30 + 9] < 128, $"경계 흐리기: 양 보존, 가장자리 반투명 ({before}→{after})");
        // 색역 선택
        var img = Img(4, 1, 0x101010FF); Put(img, 4, 2, 0, 0x404040FF); Put(img, 4, 3, 0, 0x10101008);
        var (m, b) = SelCommands.ColorRangeMask(img, 4, 1, "#141414", 4);
        Ok(m.SequenceEqual(new byte[] { 1, 1, 0, 0 }) && b == new Rect(0, 0, 2, 1), "색역 선택 (알파 16 미만 제외)");
    }

    static void DitherTest()
    {
        int on = 0; for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) if (Raster.DitherOn(0.5, 4, x, y)) on++;
        Ok(on == 8, "디더 50% 4×4 = 16칸 중 8칸");
        Ok(Raster.DitherOn(0.25, 2, 0, 0) && !Raster.DitherOn(0.25, 2, 1, 0) && Raster.DitherOn(0.25, 2, 2, 2), "디더 2×2 문서 좌표 기준 반복");
        Ok(Math.Abs(Raster.BayerAt(-1, -1, 4) - (5 + 0.5) / 16) < 1e-12, "음수 좌표 bayer");
    }

    static void RefineTest()
    {
        int w = 30, h = 30;
        var a = new float[w * h];
        for (int y = 8; y < 22; y++) for (int x = 8; x < 22; x++) a[y * w + x] = 1;
        var same = Refine.RefineAlpha((float[])a.Clone(), w, h, new Refine.Opt2());
        Ok(same.SequenceEqual(a), "다듬기 값 0 = 그대로");
        var grow = Refine.RefineAlpha((float[])a.Clone(), w, h, new Refine.Opt2 { Shift = 3 });
        var shrink = Refine.RefineAlpha((float[])a.Clone(), w, h, new Refine.Opt2 { Shift = -3 });
        Ok(grow.Sum() > a.Sum() && shrink.Sum() < a.Sum() && grow.All(v => v == 0 || v == 1), "경계 이동 +/−");
        var fea = Refine.RefineAlpha((float[])a.Clone(), w, h, new Refine.Opt2 { Feather = 6 });
        Ok(fea[15 * w + 15] > 0.99f && fea[15 * w + 8] > 0.2f && fea[15 * w + 8] < 0.8f, "페더");
        // 머리카락 다듬기: 왼쪽 검정(안) / 오른쪽 흰색(밖), 가운데 회색 → 중간값
        int W = 40, H = 20;
        var img = new byte[W * H * 4]; var m = new float[W * H];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { byte v = x < 18 ? (byte)0 : x > 21 ? (byte)255 : (byte)128; int p = (y * W + x) * 4; img[p] = img[p + 1] = img[p + 2] = v; img[p + 3] = 255; m[y * W + x] = x < 20 ? 1 : 0; }
        bool done = Refine.RefinePixels(img, m, W, 20, 10, 6);
        Ok(done && m[10 * W + 20] > 0.2f && m[10 * W + 20] < 0.8f && m[10 * W + 23] < 0.05f, "머리카락 다듬기 색 분리");
    }

    static void FillApplyUndo()
    {
        var keep = App.Doc;
        try
        {
            var d = Document.New(64, 64);
            App.Doc = d;
            var l = d.Active;
            for (int y = 0; y < 64; y++) l.Px.Set(32, y, 0xFF000000);   // 검은 세로선
            var img = SelUtil.ReferenceImage("layer");
            var (mask, b) = Raster.FillMask(img, 64, 64, 5, 5, new Raster.FillOpt { Tolerance = 12, Gap = 0, Expand = 0 });
            Ok(b == new Rect(0, 0, 32, 64), "현재 레이어 참조로 투명 영역 채우기 범위");
            FillTool.Apply(d, l, l.Px, mask, b, 0xFFFF0000, 0, 4);
            Ok(l.Px.Get(5, 5) == 0xFFFF0000 && l.Px.Get(40, 5) == 0 && l.Px.Get(32, 5) == 0xFF000000, "칠해짐 (선·반대쪽은 그대로)");
            App.History.Undo(d);
            Ok(l.Px.Get(5, 5) == 0, "실행취소");
            App.History.Redo(d);
            Ok(l.Px.Get(5, 5) == 0xFFFF0000, "다시실행");
            // 선택 영역 안에만 + 디더
            d.Sel.Mask = new byte[64 * 64];
            for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++) d.Sel.Mask[y * 64 + x] = 255;
            App.History.Undo(d);
            FillTool.Apply(d, l, l.Px, mask, b, 0xFF00FF00, 0.5, 2);
            Ok(l.Px.Get(0, 0) == 0xFF00FF00 && l.Px.Get(1, 0) == 0 && l.Px.Get(20, 20) == 0, "선택 영역·디더 반영");
            // 올가미 채우기 / 지우기
            d.Sel.Mask = null;
            var r = new Rect(40, 40, 10, 10);
            LassoFillTool.Apply(d, l, r, Enumerable.Repeat((byte)255, 100).ToArray(), false, 0xFF0000FF);
            Ok(l.Px.Get(45, 45) == 0xFF0000FF, "올가미 채우기");
            LassoFillTool.Apply(d, l, r, Enumerable.Repeat((byte)255, 100).ToArray(), true, 0);
            Ok(l.Px.Get(45, 45) == 0, "올가미 지우기");
            // 그라데이션 (전경색 → 투명, 왼쪽 → 오른쪽)
            var l2 = new Layer(d, "g"); d.Layers.Add(l2); d.ActiveId = l2.Id;
            var pe = App.History.BeginPixels(l2, "그라데이션"); pe.Touch(new Rect(0, 0, 64, 64));
            GradientTool.Fill(d, l2, l2.Px, new Rect(0, 0, 64, 64), pe, "linear", 0, 0, 64, 0, 0xFF000000, null, 1);
            pe.Commit();
            uint a0 = l2.Px.Get(0, 10) >> 24, a1 = l2.Px.Get(32, 10) >> 24, a2 = l2.Px.Get(63, 10) >> 24;
            Ok(a0 > 250 && Math.Abs((int)a1 - 125) <= 3 && a2 < 5, $"그라데이션 알파 {a0}/{a1}/{a2}");
            // 선택 → 새 레이어로 복사
            d.ActiveId = l.Id;
            d.Sel.Mask = new byte[64 * 64]; for (int y = 0; y < 64; y++) for (int x = 30; x < 34; x++) d.Sel.Mask[y * 64 + x] = 255;
            var c = SelCommands.ExtractContent(l);
            Ok(c != null && c.Bounds == new Rect(30, 0, 3, 64) || c != null && c.Bounds.X >= 30 && c.Bounds.R <= 34, "선택 영역 꺼내기 (남는 곳 자름)");
        }
        finally { App.Doc = keep; App.History.Clear(); }
    }

    static void Perf()
    {
        // B5 350dpi 정도 원고: 선이 엉성한 큰 그림에서 틈 막기 10px
        int w = 2480, h = 3508;
        var d = Img(w, h, 0xFFFFFFFF);
        var rnd = new Random(7);
        for (int k = 0; k < 300; k++)
        {
            int x0 = rnd.Next(w), y0 = rnd.Next(h), len = 200 + rnd.Next(800); bool hor = rnd.Next(2) == 0;
            for (int t = 0; t < len; t++) { if (t % 97 < 4) continue; int x = hor ? x0 + t : x0, y = hor ? y0 : y0 + t; if (x < w && y < h) { Put(d, w, x, y, 0x000000FF); if (x + 1 < w) Put(d, w, x + 1, y, 0x000000FF); } }
        }
        var sw = Stopwatch.StartNew();
        var (m, b) = Raster.FillMask(d, w, h, 10, 10, new Raster.FillOpt { Tolerance = 12, Gap = 10, Expand = 2 });
        long ms1 = sw.ElapsedMilliseconds; sw.Restart();
        var r = AutoFlat.FlatLabels(d, w, h, new AutoFlat.Opt { Gap = 4, Under = 3, MinArea = 40 });
        long ms2 = sw.ElapsedMilliseconds;
        log.AppendLine($"INFO 2480×3508: 채우기(틈 10px) {ms1}ms, 밑색 라벨 {ms2}ms (영역 {r.Count}개)");
        Ok(!b.IsEmpty && ms1 < 20000, "큰 원고 채우기 끝남");
    }

    // ---- 웹 판과 같은지 (같은 입력을 웹 raster.js/autoflat.js 에 넣은 결과 해시와 비교) ----------------
    static int Lcg(ref int x) { x = unchecked(x * 1103515245 + 12345) & 0x7fffffff; return x; }
    static uint Fnv(ReadOnlySpan<byte> b) { uint h = 2166136261; foreach (var v in b) { h ^= v; h = unchecked(h * 16777619); } return h; }
    const int PW = 300, PH = 200;
    static byte[] ParityLines(bool opaqueBg)
    {
        var d = new byte[PW * PH * 4];
        if (opaqueBg) Array.Fill(d, (byte)255);
        int x = 42;
        void Set(int px, int py, byte v, byte a) { if (px < 0 || py < 0 || px >= PW || py >= PH) return; int p = (py * PW + px) * 4; d[p] = v; d[p + 1] = v; d[p + 2] = v; d[p + 3] = a; }
        for (int k = 0; k < 60; k++)
        {
            int x0 = Lcg(ref x) % PW, y0 = Lcg(ref x) % PH, len = 20 + Lcg(ref x) % 150, hor = Lcg(ref x) % 2, ge = 15 + Lcg(ref x) % 40;
            for (int t = 0; t < len; t++)
            {
                if (t % ge < 3) continue;
                int px = hor != 0 ? x0 + t : x0, py = hor != 0 ? y0 : y0 + t;
                if (px < PW && py < PH) { Set(px, py, 0, 255); if (hor != 0) Set(px, py + 1, 40, (byte)(opaqueBg ? 160 : 100)); else Set(px + 1, py, 40, (byte)(opaqueBg ? 160 : 100)); }
            }
        }
        return d;
    }

    /// <summary>웹 판 하네스(parity.html)와 같은 줄을 만든다</summary>
    public static List<string> ParityLines()
    {
        var o = new List<string>();
        var img = ParityLines(true);
        var starts = new[] { (5, 5), (150, 100), (299, 199), (77, 123) };
        var opts = new[] { (12, 0, 0), (12, 4, 2), (30, 9, 0), (0, 15, 1) };
        foreach (var (t, g, e) in opts) foreach (var (x, y) in starts)
        {
            var (mask, b) = Raster.FillMask(img, PW, PH, x, y, new Raster.FillOpt { Tolerance = t, Gap = g, Expand = e });
            int n = mask.Sum(v => v);
            o.Add($"fill t{t} g{g} e{e} @{x},{y}: n={n} h={Fnv(mask)} b={(b.IsEmpty ? "none" : $"{b.X},{b.Y},{b.W},{b.H}")}");
        }
        var fl = ParityLines(false);
        var hint = new byte[PW * PH * 4];
        {
            int x = 7; var pal = new[] { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 204, 0) };
            for (int k = 0; k < 25; k++)
            {
                int hx = Lcg(ref x) % PW, hy = Lcg(ref x) % PH; var c = pal[Lcg(ref x) % 4];
                for (int dy = 0; dy < 3; dy++) for (int dx = 0; dx < 3; dx++) { int X = hx + dx, Y = hy + dy; if (X < PW && Y < PH) { int p = (Y * PW + X) * 4; hint[p] = (byte)c.Item1; hint[p + 1] = (byte)c.Item2; hint[p + 2] = (byte)c.Item3; hint[p + 3] = 255; } }
            }
        }
        var fopts = new[] { (4, 2, 30, true), (0, 3, 10, false), (10, 1, 5, false) };
        foreach (var (g, u, m, outside) in fopts)
        {
            var r = AutoFlat.FlatLabels(fl, PW, PH, new AutoFlat.Opt { Gap = g, Under = u, MinArea = m, Outside = outside });
            var lb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(r.L.AsSpan());
            var eb = r.Edge.Select(v => v ? (byte)1 : (byte)0).ToArray();
            o.Add($"flat g{g} u{u} m{m} o{(outside ? "true" : "false")}: count={r.Count} h={Fnv(lb)} edge={Fnv(eb)}");
            foreach (bool sp in new[] { true, false })
            {
                var hr = AutoFlat.ColorizeHints(r.L, r.Count, r.Edge, hint, PW, PH, sp);
                o.Add($"  hints spread={(sp ? "true" : "false")}: hinted={hr.Hinted} filled={hr.Filled} h={Fnv(hr.Rgba)}");
            }
            o.Add($"  colorize h={Fnv(AutoFlat.Colorize(r.L, r.Count, PW, PH, "colors"))}");
        }
        return o;
    }

    /// <summary>웹 판(브라우저에서 app/js/core/raster.js·autoflat.js 를 그대로 실행)이 낸 결과 — 2026-11-12 확인</summary>
    static readonly string[] WebGolden =
    {
        "fill t12 g0 e0 @5,5: n=52972 h=1568830061 b=0,0,300,200",
        "fill t12 g0 e0 @150,100: n=52972 h=1568830061 b=0,0,300,200",
        "fill t12 g0 e0 @299,199: n=52972 h=1568830061 b=0,0,300,200",
        "fill t12 g0 e0 @77,123: n=52972 h=1568830061 b=0,0,300,200",
        "fill t12 g4 e2 @5,5: n=20285 h=879626236 b=0,0,300,200",
        "fill t12 g4 e2 @150,100: n=59684 h=4292465535 b=0,0,300,200",
        "fill t12 g4 e2 @299,199: n=175 h=1234801824 b=287,186,13,14",
        "fill t12 g4 e2 @77,123: n=13710 h=3169396417 b=27,56,185,86",
        "fill t30 g9 e0 @5,5: n=11452 h=358966745 b=0,0,218,200",
        "fill t30 g9 e0 @150,100: n=52972 h=1568830061 b=0,0,300,200",
        "fill t30 g9 e0 @299,199: n=114 h=689095657 b=289,188,11,12",
        "fill t30 g9 e0 @77,123: n=9740 h=447432905 b=29,72,181,67",
        "fill t0 g15 e1 @5,5: n=11700 h=1530869999 b=0,0,218,200",
        "fill t0 g15 e1 @150,100: n=59268 h=3316573795 b=0,0,300,200",
        "fill t0 g15 e1 @299,199: n=119 h=1228931502 b=288,187,12,13",
        "fill t0 g15 e1 @77,123: n=2709 h=2854357444 b=59,79,50,60",
        "flat g4 u2 m30 otrue: count=14 h=3121988390 edge=89356807",
        "  hints spread=true: hinted=7 filled=14 h=3702204353",
        "  hints spread=false: hinted=7 filled=7 h=1220950117",
        "  colorize h=3088976887",
        "flat g0 u3 m10 ofalse: count=2 h=4179587141 edge=632192737",
        "  hints spread=true: hinted=1 filled=1 h=2080211269",
        "  hints spread=false: hinted=1 filled=1 h=2080211269",
        "  colorize h=162165509",
        "flat g10 u1 m5 ofalse: count=27 h=550996828 edge=3499314984",
        "  hints spread=true: hinted=10 filled=17 h=2605453899",
        "  hints spread=false: hinted=10 filled=10 h=235192333",
        "  colorize h=2285651014",
    };
    static void WebParity()
    {
        var mine = ParityLines();
        int same = 0;
        for (int i = 0; i < Math.Min(mine.Count, WebGolden.Length); i++) if (mine[i] == WebGolden[i]) same++; else log.AppendLine("  다름: " + mine[i] + "  ≠ 웹: " + WebGolden[i]);
        Ok(same == WebGolden.Length && mine.Count == WebGolden.Length, $"웹 판과 비트 단위로 같음 (채우기 16가지·밑색 라벨/힌트/색 12가지: {same}/{WebGolden.Length})");
    }
}
