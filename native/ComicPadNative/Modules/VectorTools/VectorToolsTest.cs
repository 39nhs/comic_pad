// 벡터 선 정리 + 틈 닫기 채우기 자체 시험 (창 없이): ComicPadNative --selftest vectortools 결과폴더
//  벡터: 단순화(점 수·허용 오차) · 부드럽게(끝점 고정) · 굵기 붓(부분만 필압) · 선 끝 다듬기 · 선 끝 연결 · 고르기(올가미·크기·회전·삭제·색) · 교차점까지 지우기 · 실행취소/다시실행
//  채우기: 3/6/10px 틈 (네모·동그라미) — 틈 닫기 0 이면 샘, 8 이면 3·6 은 안만, 10 은 샘 · 참조 레이어 · 여러 칸 칠하기 · 덜 칠한 곳 메우기
//  전·후 그림 (*.png) 과 report.txt 를 결과폴더에. 끝 코드 = 실패 수.
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.SelectFill;
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.Modules.VectorTools;

public static class VectorToolsTest
{
    static readonly StringBuilder log = new();
    static int pass, fail;
    static string dir;
    static void Ok(bool c, string name, string detail = "") { if (c) pass++; else fail++; log.AppendLine((c ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  — " + detail : "")); }

    public static int Run(string outDir)
    {
        dir = outDir; log.Clear(); pass = fail = 0;
        void T(string name, Action a) { try { a(); } catch (Exception ex) { Ok(false, name + " 예외: " + ex); } }
        App.Settings = new JsonObject();
        T("단순화", TestSimplify);
        T("부드럽게", TestSmooth);
        T("굵기 붓", TestWidth);
        T("선 끝 다듬기", TestTaper);
        T("선 끝 연결", TestConnect);
        T("벡터 선 고르기", TestSelect);
        T("교차점까지 지우기", TestCross);
        T("벡터 선 수정 도구 (끌기·실행취소)", TestLineTool);
        T("틈 닫기 채우기", TestGapFill);
        T("참조 레이어 틈 닫기", TestRefFill);
        T("여러 칸 칠하기", TestMulti);
        T("덜 칠한 곳 메우기", TestLeftover);
        T("큰 원고 속도", TestPerf);
        log.Insert(0, $"벡터 선 정리 · 틈 닫기 채우기 시험: 통과 {pass}, 실패 {fail}\r\n");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        Console.WriteLine(log.ToString());
        return fail;
    }

    // ---- 도우미 -----------------------------------------------------------------------------------------
    static JsonObject Stroke(IEnumerable<(double x, double y, double p)> pts, double size = 8, bool pSize = true, double min = 0.2, string color = "#000000")
    {
        var a = new JsonArray(); int t = 0;
        foreach (var (x, y, p) in pts) { a.Add(Math.Round(x, 3)); a.Add(Math.Round(y, 3)); a.Add(Math.Round(p, 3)); a.Add(t); t += 8; }
        return new JsonObject { ["pts"] = a, ["preset"] = new JsonObject { ["size"] = size, ["pressureSize"] = pSize, ["minSize"] = min, ["hardness"] = 1, ["spacing"] = 0.05 }, ["color"] = color };
    }
    static IEnumerable<(double, double, double)> Line(double x0, double y0, double x1, double y1, int n, double p = 1, double wob = 0)
    {
        for (int i = 0; i <= n; i++) { double t = (double)i / n; yield return (x0 + (x1 - x0) * t, y0 + (y1 - y0) * t + wob * Math.Sin(i * 1.7), p); }
    }
    static (Document d, Layer l) VDoc(int w, int h, params JsonObject[] strokes)
    {
        var d = Document.New(w, h, "벡터 시험");
        var l = new Layer(d, "벡터", "vector") { Meta = new JsonObject { ["strokes"] = new JsonArray(strokes.Select(s => (JsonNode)s).ToArray()) } };
        d.Layers.Add(l); d.ActiveId = l.Id;
        App.SetDoc(d);
        VectorLayer.Render(l, d);
        return (d, l);
    }
    /// <summary>면 → 그림 (흰 바탕, transparent 면 투명 바탕)</summary>
    static Bitmap Bmp(Surface s, Rect r, bool transparent = false)
    {
        var bmp = new Bitmap(r.W, r.H, PixelFormat.Format32bppArgb);
        for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
            {
                uint c = s.Get(r.X + x, r.Y + y);
                if (!transparent) c = Px.Over(c, 0xFFFFFFFF);
                else c = Px.Unpremul(c);
                bmp.SetPixel(x, y, Color.FromArgb((int)c));
            }
        return bmp;
    }

    static List<JsonObject> S(Layer l) => VectorLayer.Strokes(l).Select(n => (JsonObject)n).ToList();
    static string J(Layer l) => VectorLayer.Strokes(l).ToJsonString();
    /// <summary>실행취소 → 원래대로, 다시실행 → 바뀐 대로</summary>
    static void UndoRedo(Document d, Layer l, string before, string name)
    {
        string after = J(l);
        App.History.Undo(d); bool u = J(l) == before;
        App.History.Redo(d); bool r = J(l) == after;
        Ok(u && r && before != after, name + ": 실행취소/다시실행", $"취소={u} 다시={r}");
    }
    /// <summary>x 열에서 알파 128 이상인 픽셀 수 (그린 선의 굵기)</summary>
    static int Thick(Surface s, int x, int y0, int y1) { int c = 0; for (int y = y0; y <= y1; y++) if ((s.Get(x, y) >> 24) >= 128) c++; return c; }

    static void Save(Layer l, Document d, string name, Action<Graphics> extra = null, int scale = 2)
    {
        using var b = Bmp(l.Px, new Rect(0, 0, d.W, d.H));
        using var big = new Bitmap(d.W * scale, d.H * scale);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(b, 0, 0, d.W * scale, d.H * scale);
            g.ScaleTransform(scale, scale);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            extra?.Invoke(g);
        }
        big.Save(Path.Combine(dir, name), ImageFormat.Png);
    }
    static void Dots(Graphics g, JsonObject s, Color c)
    {
        var p = VecOps.Pts(s);
        using var br = new SolidBrush(c);
        for (int i = 0; i + 1 < p.Length; i += 4) g.FillEllipse(br, (float)p[i] - 1.2f, (float)p[i + 1] - 1.2f, 2.4f, 2.4f);
    }

    // ---- 벡터 -------------------------------------------------------------------------------------------
    static void TestSimplify()
    {
        // 촘촘하고 살짝 떨리는 선 + 꺾인 곳
        var pts = Line(20, 60, 200, 60, 180, 1, 0.3).Concat(Line(200, 60, 300, 140, 120, 1, 0.3).Skip(1)).ToList();
        var s0 = Stroke(pts, 6, false);
        var (d, l) = VDoc(320, 160, s0);
        Save(l, d, "vt_simplify_before.png", g => Dots(g, s0, Color.Red));
        var p0 = VecOps.Pts(s0);
        string before = J(l);
        int n = VecEdit.Simplify(l, 1.0);
        var s1 = S(l)[0]; var p1 = VecOps.Pts(s1);
        double maxDev = 0;
        for (int i = 0; i + 1 < p0.Length; i += 4) maxDev = Math.Max(maxDev, VecOps.DistToPolyline(p1, p0[i], p0[i + 1]));
        Ok(n == 1 && VecOps.Count(p1) < VecOps.Count(p0) / 10, "단순화: 점 수가 크게 줄어듦", $"{VecOps.Count(p0)} → {VecOps.Count(p1)}점");
        Ok(maxDev <= 1.0 + 1e-6, "단순화: 원래 점이 모두 허용 오차(1px) 안", $"최대 {maxDev:0.000}px");
        Ok(p1[0] == p0[0] && p1[1] == p0[1] && p1[^4] == p0[^4] && p1[^3] == p0[^3], "단순화: 양 끝점 그대로");
        Ok(Math.Abs(VecOps.Pts(s1).Where((_, i) => i % 4 == 0).Max() - 300) < 0.01, "단순화: 꺾인 모양 유지 (끝 x=300)");
        // 다시 그린 선이 원래 점을 모두 덮음 (브러시의 곡선 이음으로 꺾인 곳이 깎이지 않음)
        int miss = 0;
        for (int i = 0; i + 1 < p0.Length; i += 4) if ((l.Px.Get((int)Math.Round(p0[i]), (int)Math.Round(p0[i + 1])) >> 24) < 128) miss++;
        Ok(miss == 0, "단순화: 다시 그린 선이 원래 선 자리를 모두 덮음 (꺾인 곳 포함)", $"빠진 점 {miss}");
        Save(l, d, "vt_simplify_after.png", g => { Dots(g, s0, Color.FromArgb(120, 0, 160, 255)); Dots(g, s1, Color.Red); });
        UndoRedo(d, l, before, "단순화");
        // 허용 오차가 작으면 덜 줄어듦
        var q = VecOps.Simplify(p0, 0.1);
        Ok(VecOps.Count(q) > VecOps.Count(p1), "단순화: 허용 오차 작게 → 점이 더 남음", $"0.1px → {VecOps.Count(q)}점");
    }

    static void TestSmooth()
    {
        var zig = Enumerable.Range(0, 41).Select(i => (20.0 + i * 6, 60.0 + (i % 2 == 0 ? -4 : 4), 1.0)).ToList();
        var s0 = Stroke(zig, 4, false);
        var (d, l) = VDoc(280, 120, s0);
        Save(l, d, "vt_smooth_before.png");
        string before = J(l);
        double Rough(double[] p) { double r = 0; for (int i = 4; i + 4 < p.Length; i += 4) r += Math.Abs(p[i - 3] - 2 * p[i + 1] + p[i + 5]); return r; }
        var p0 = VecOps.Pts(s0);
        VecEdit.Smooth(l, 2);
        var p1 = VecOps.Pts(S(l)[0]);
        Ok(p1[0] == p0[0] && p1[1] == p0[1] && p1[^4] == p0[^4] && p1[^3] == p0[^3], "부드럽게: 양 끝점 그대로", $"시작 ({p1[0]},{p1[1]}) 끝 ({p1[^4]},{p1[^3]})");
        Ok(Rough(p1) < Rough(p0) * 0.3, "부드럽게: 떨림이 줄어듦", $"거칠기 {Rough(p0):0.0} → {Rough(p1):0.0}");
        Save(l, d, "vt_smooth_after.png");
        UndoRedo(d, l, before, "부드럽게");
        // 듬성한 선은 Chaikin (점이 늘고 끝점 그대로)
        var sp = new double[] { 0, 0, 1, 0, 50, 50, 1, 8, 100, 0, 1, 16 };
        var c = VecOps.SmoothAuto(sp, 1);
        Ok(VecOps.Count(c) == 4 && c[0] == 0 && c[^4] == 100 && c[^3] == 0, "부드럽게 (Chaikin): 듬성한 선 모서리 깎기, 끝점 그대로", $"{VecOps.Count(c)}점");
    }

    static void TestWidth()
    {
        App.Settings = new JsonObject();
        var f = PenFeel.Current();
        var s0 = Stroke(Line(20, 50, 300, 50, 70, 0.5), 16, true, 0.2);
        var (d, l) = VDoc(320, 100, s0);
        int tc0 = Thick(l.Px, 160, 20, 80), tf0 = Thick(l.Px, 40, 20, 80);
        Save(l, d, "vt_width_before.png");
        string before = J(l);
        var w0 = VecOps.Widths(s0, f);
        var list = S(l).Select(x => (JsonObject)x.DeepClone()).ToList();
        var s1 = VecOps.WidthBrush(list[0], 160, 50, 40, 0.5, f);
        VecEdit.Commit(l, new List<JsonObject> { s1 }, "선 굵기 수정");
        var w1 = VecOps.Widths(s1, f); var p0 = VecOps.Pts(s0); var p1 = VecOps.Pts(s1);
        int ci = Enumerable.Range(0, VecOps.Count(p1)).OrderBy(i => Math.Abs(p1[i * 4] - 160)).First();
        int fi = Enumerable.Range(0, VecOps.Count(p1)).OrderBy(i => Math.Abs(p1[i * 4] - 40)).First();
        Ok(Math.Abs(w1[ci] / w0[0] - 1.5) < 0.03, "굵기 붓: 붓 가운데 굵기 ×1.5", $"{w0[0]:0.00} → {w1[ci]:0.00}px");
        Ok(Math.Abs(w1[fi] - w0[0]) < 1e-6 && p1[fi * 4 + 2] == p0[2], "굵기 붓: 붓 밖은 필압 그대로", $"먼 곳 필압 {p1[fi * 4 + 2]}");
        Ok(p1[ci * 4 + 2] > p0[2] + 0.1, "굵기 붓: 가운데 필압이 커짐", $"{p0[2]} → {p1[ci * 4 + 2]}");
        int tc1 = Thick(l.Px, 160, 20, 80), tf1 = Thick(l.Px, 40, 20, 80);
        Ok(tc1 >= tc0 + 3 && Math.Abs(tf1 - tf0) <= 0, "굵기 붓: 다시 그린 그림도 가운데만 굵어짐", $"가운데 {tc0}→{tc1}px, 먼 곳 {tf0}→{tf1}px");
        Save(l, d, "vt_width_after.png");
        UndoRedo(d, l, before, "굵기 붓");
        // 가늘게
        var s2 = VecOps.WidthBrush((JsonObject)s0.DeepClone(), 160, 50, 40, -0.5, f);
        var w2 = VecOps.Widths(s2, f);
        int c2 = Enumerable.Range(0, w2.Length).OrderBy(i => Math.Abs(VecOps.Pts(s2)[i * 4] - 160)).First();
        Ok(Math.Abs(w2[c2] / w0[0] - 0.5) < 0.03, "굵기 붓 (가늘게): 가운데 ×0.5", $"{w2[c2]:0.00}px");
        // 필압을 안 쓰는 선 + 휘어진 필압 곡선 (γ=2) 에서도
        App.Settings = new JsonObject { ["pressureGamma"] = 2.0 };
        var fg = PenFeel.Current();
        var s3 = Stroke(Line(20, 50, 300, 50, 70, 1), 10, false);
        var r3 = VecOps.WidthBrush(s3, 160, 50, 40, 0.6, fg);
        var w3 = VecOps.Widths(r3, fg); var q3 = VecOps.Pts(r3);
        int c3 = Enumerable.Range(0, w3.Length).OrderBy(i => Math.Abs(q3[i * 4] - 160)).First(), f3 = Enumerable.Range(0, w3.Length).OrderBy(i => Math.Abs(q3[i * 4] - 40)).First();
        Ok(Math.Abs(w3[c3] - 16) < 0.2 && Math.Abs(w3[f3] - 10) < 0.2, "굵기 붓: 필압 안 쓰던 선·γ2 곡선도 (필압으로 바꿔 기록)", $"가운데 {w3[c3]:0.00}, 먼 곳 {w3[f3]:0.00}px (원래 10)");
        App.Settings = new JsonObject();
        // 고른 선 굵기 배율 (메뉴)
        var (d4, l4) = VDoc(200, 100, Stroke(Line(20, 30, 180, 30, 20, 0.6), 8), Stroke(Line(20, 70, 180, 70, 20, 0.6), 8));
        var vs = SelTool(); vs.SetSelection(l4, new[] { 1 });
        string b4 = J(l4);
        int n4 = VecEdit.ScaleWidth(l4, 2);
        var ww = S(l4).Select(x => VecOps.Widths(x, f)[5]).ToArray();
        Ok(n4 == 1 && Math.Abs(ww[1] / ww[0] - 2) < 0.02, "고른 선만 굵기 ×2 (메뉴)", $"안 고른 선 {ww[0]:0.00}, 고른 선 {ww[1]:0.00}px");
        UndoRedo(d4, l4, b4, "고른 선 굵기");
        vs.Sel.Clear();
    }

    static void TestTaper()
    {
        var f = PenFeel.Current();
        var s0 = Stroke(Line(20, 50, 300, 50, 7, 1), 14, false);   // 점이 듬성 (끼워 넣어야 함)
        var (d, l) = VDoc(320, 100, s0);
        Save(l, d, "vt_taper_before.png");
        int e0 = Thick(l.Px, 26, 20, 80), m0 = Thick(l.Px, 160, 20, 80);
        string before = J(l);
        VecEdit.Taper(l, 60, 60);
        var s1 = S(l)[0]; var w = VecOps.Widths(s1, f);
        Ok(w[0] < 14 * 0.15 && w[^1] < 14 * 0.15, "선 끝 다듬기: 시작·끝 굵기가 가늘어짐", $"시작 {w[0]:0.00}, 끝 {w[^1]:0.00}px (원래 14)");
        Ok(Math.Abs(w[w.Length / 2] - 14) < 0.1, "선 끝 다듬기: 가운데 굵기 그대로", $"{w[w.Length / 2]:0.00}px");
        Ok(VecOps.Count(VecOps.Pts(s1)) > 8, "선 끝 다듬기: 듬성한 선에 점을 끼워 넣음", $"{VecOps.Count(VecOps.Pts(s1))}점");
        int e1 = Thick(l.Px, 26, 20, 80), m1 = Thick(l.Px, 160, 20, 80);
        Ok(e1 < e0 - 4 && Math.Abs(m1 - m0) <= 1, "선 끝 다듬기: 그림에서도 끝만 가늘어짐", $"끝 쪽 {e0}→{e1}px, 가운데 {m0}→{m1}px");
        Save(l, d, "vt_taper_after.png");
        UndoRedo(d, l, before, "선 끝 다듬기");
    }

    static void TestConnect()
    {
        // 6px 떨어진 두 선 + 8px 틈이 있는 동그라미 + 멀리 있는 선
        var a = Stroke(Line(20, 40, 120, 40, 20, 1), 4, false);
        var b = Stroke(Line(126, 40, 220, 40, 20, 1), 4, false, color: "#ff0000");
        var circ = Enumerable.Range(0, 60).Select(i => { double t = 0.08 + (2 * Math.PI - 0.16) * i / 59; return (160 + 50 * Math.Cos(t), 130 + 50 * Math.Sin(t), 1.0); });
        var c = Stroke(circ, 4, false);
        var far = Stroke(Line(20, 200, 60, 200, 5, 1), 4, false);
        var (d, l) = VDoc(260, 220, a, b, c, far);
        Save(l, d, "vt_connect_before.png", g => { foreach (var s in S(l)) { var p = VecOps.Pts(s); g.DrawEllipse(Pens.Orange, (float)p[0] - 3, (float)p[1] - 3, 6, 6); g.DrawEllipse(Pens.Orange, (float)p[^4] - 3, (float)p[^3] - 3, 6, 6); } });
        string before = J(l);
        int j = VecEdit.Connect(l, 12);
        var st = S(l);
        Ok(j == 2 && st.Count == 3, "선 끝 연결 (자동 12px): 두 선을 잇고 동그라미를 닫음", $"이음 {j}, 선 {st.Count}개");
        var m = VecOps.Pts(st[0]);
        Ok(Math.Abs(m[0] - 20) < 0.01 && Math.Abs(m[^4] - 220) < 0.01 && (string)st[0]["color"] == "#000000", "선 끝 연결: 이은 선은 먼 두 끝 + 앞 선의 색", $"({m[0]}, … {m[^4]})");
        var cp = VecOps.Pts(st[1]);
        Ok(Math.Abs(cp[0] - cp[^4]) < 0.01 && Math.Abs(cp[1] - cp[^3]) < 0.01, "선 끝 연결: 동그라미가 닫힘 (끝 = 시작)");
        Ok(VecOps.Count(VecOps.Pts(st[2])) == 6, "선 끝 연결: 먼 선은 그대로");
        Save(l, d, "vt_connect_after.png");
        UndoRedo(d, l, before, "선 끝 연결");
        // 끌어서 잇기 (도구): 원래 상태에서 선 0 의 끝 → 선 1 의 시작
        App.History.Undo(d);
        var tool = new VectorLineTool { Mode = "connect" };
        tool.Down(PA(121, 40)); tool.Move(PA(124, 41)); tool.Up(PA(125, 40));
        Ok(S(l).Count == 3 && Math.Abs(VecOps.Pts(S(l)[0])[^4] - 220) < 0.01, "선 끝 연결 도구: 끝에서 끝으로 끌기", $"선 {S(l).Count}개");
        UndoRedo(d, l, before, "선 끝 연결 도구");
    }

    static VectorSelectTool SelTool() => (VectorSelectTool)Registry.Tools.Find(t => t.Id == "vselect");
    static PointerArgs PA(double x, double y, bool shift = false) => new() { X = x, Y = y, SX = x, SY = y, Pressure = 1, Buttons = 1, Shift = shift };

    static void TestSelect()
    {
        var s1 = Stroke(Line(40, 40, 100, 40, 12, 1), 4, false);
        var s2 = Stroke(Line(40, 80, 100, 80, 12, 1), 4, false);
        var s3 = Stroke(Line(200, 40, 260, 120, 12, 1), 4, false);
        var (d, l) = VDoc(300, 160, s1, s2, s3);
        var vs = SelTool(); vs.Activate();
        Save(l, d, "vt_select_before.png");
        // 올가미 (빈 곳에서 끌기): 위 두 선 둘레
        var poly = new[] { (20.0, 20.0), (130.0, 20.0), (130.0, 100.0), (20.0, 100.0), (20.0, 21.0) };
        vs.Down(PA(poly[0].Item1, poly[0].Item2));
        foreach (var (x, y) in poly.Skip(1)) vs.Move(PA(x, y));
        vs.Up(PA(20, 21));
        Ok(vs.Sel.SetEquals(new[] { 0, 1 }), "올가미로 고르기 (두 선)", string.Join(",", vs.Sel));
        // 모서리 끌기 = 크기 (오른쪽 아래 모서리: 점 범위 + 여유 8 + 굵기/2)
        double pad = 8 + 2;
        string b0 = J(l);
        // 고정점 = 왼쪽 위 모서리 (40-pad, 40-pad), 모서리 (100+pad, 80+pad) 를 대각선 방향으로 1.75배 자리까지
        double k = 1.75, ax = 40 - pad, ay = 40 - pad, tx = ax + (100 + pad - ax) * k, ty = ay + (80 + pad - ay) * k;
        vs.Down(PA(100 + pad, 80 + pad)); vs.Move(PA(130, 110)); vs.Move(PA(tx, ty)); vs.Up(PA(tx, ty));
        var bb = VecOps.Bounds(S(l).Take(2));
        double ex0 = (40 - pad) + (40 - (40 - pad)) * k;
        Ok(Math.Abs(bb.x0 - ex0) < 0.05 && Math.Abs(bb.x1 - ((40 - pad) + (100 - (40 - pad)) * k)) < 0.05, "모서리 끌기: 크기 바꾸기 (같은 비율)", $"x {bb.x0:0.0}~{bb.x1:0.0}, 배율 {k:0.000}");
        Ok(Math.Abs(VecOps.SizeOf(S(l)[0]) - 4 * k) < 0.01 && VecOps.SizeOf(S(l)[2]) == 4, "크기 바꿀 때 선 굵기도 (고른 선만)", $"{VecOps.SizeOf(S(l)[0]):0.00}px");
        UndoRedo(d, l, b0, "크기 바꾸기");
        // 회전 90° (단추와 같은 계산)
        string b1 = J(l);
        var c = vs.SelCenter().Value;
        vs.TransformSelected(0, 1, -1, 0, c.x + c.y, c.y - c.x, 1, "벡터 선 회전");
        var r = VecOps.Bounds(S(l).Take(2));
        var r0 = VecOps.Bounds(JsonNode.Parse(b1).AsArray().Take(2).Select(n => (JsonObject)n));
        Ok(Math.Abs((r.x1 - r.x0) - (r0.y1 - r0.y0)) < 0.05 && Math.Abs((r.y1 - r.y0) - (r0.x1 - r0.x0)) < 0.05, "회전 90°: 가로·세로가 바뀜", $"{r0.x1 - r0.x0:0.0}×{r0.y1 - r0.y0:0.0} → {r.x1 - r.x0:0.0}×{r.y1 - r.y0:0.0}");
        UndoRedo(d, l, b1, "회전");
        // 상자 안을 끌기 = 이동
        string b2 = J(l);
        var c2 = vs.SelCenter().Value;
        vs.Down(PA(c2.x + 1, c2.y + 1)); vs.Move(PA(c2.x + 11, c2.y + 6)); vs.Up(PA(c2.x + 21, c2.y + 11));
        var c3 = vs.SelCenter().Value;
        Ok(Math.Abs(c3.x - c2.x - 20) < 0.05 && Math.Abs(c3.y - c2.y - 10) < 0.05, "상자 안 끌기: 이동", $"({c2.x:0.0},{c2.y:0.0}) → ({c3.x:0.0},{c3.y:0.0})");
        UndoRedo(d, l, b2, "이동");
        // 색 바꾸기 · 삭제
        string b3 = J(l);
        int n = VecEdit.Recolor(l, "#ff0000");
        Ok(n == 2 && (string)S(l)[0]["color"] == "#ff0000" && (string)S(l)[2]["color"] == "#000000", "고른 선 색 바꾸기 (고른 선만)");
        UndoRedo(d, l, b3, "색 바꾸기");
        Save(l, d, "vt_select_after.png");
        string b4 = J(l);
        vs.HandleKey(Keys.Delete);
        Ok(S(l).Count == 1 && vs.Sel.Count == 0, "Delete: 고른 선 삭제", $"남은 선 {S(l).Count}");
        UndoRedo(d, l, b4, "삭제");
        // 탭으로 고르기 · Shift+탭 (삭제를 되돌려 선 3개로)
        App.History.Undo(d);
        vs.Sel.Clear();
        vs.Down(PA(230, 80)); vs.Up(PA(230, 80));
        Ok(vs.Sel.SetEquals(new[] { 2 }), "탭으로 고르기", string.Join(",", vs.Sel));
        var p0 = VecOps.Pts(S(l)[0]);
        vs.Down(PA(p0[8], p0[9], true)); vs.Up(PA(p0[8], p0[9], true));
        Ok(vs.Sel.Count == 2, "Shift+탭으로 더 고르기", string.Join(",", vs.Sel));
        // 다른 곳에서 선 목록이 바뀌면 고른 것을 풂 (번호가 어긋나지 않게)
        VectorLayer.SetStrokes(l, new JsonArray(), "다 지우기");
        Ok(vs.SelectionFor(l).Count == 0, "선 목록이 바뀌면 고른 것을 풂");
        vs.Deactivate();
    }

    static void TestCross()
    {
        JsonObject L(double x0, double y0, double x1, double y1) => Stroke(Line(x0, y0, x1, y1, 20, 1), 4, false);
        var (d, l) = VDoc(200, 100, L(20, 50, 180, 50), L(60, 10, 60, 90), L(140, 10, 140, 90));
        Save(l, d, "vt_cross_before.png");
        string before = J(l);
        var tool = new VectorLineTool { Mode = "cross", Size = 6 };
        tool.Down(PA(100, 50)); tool.Up(PA(100, 50));
        var st = S(l);
        var hz = st.Where(s => VecOps.Pts(s).Where((_, i) => i % 4 == 1).All(y => Math.Abs(y - 50) < 0.01)).Select(VecOps.Pts).ToList();
        Ok(st.Count == 4 && hz.Count == 2 && hz.Any(p => Math.Abs(p[^4] - 60) < 0.5) && hz.Any(p => Math.Abs(p[0] - 140) < 0.5), "교차점까지 지우기: 가운데 토막만 지워짐", $"선 {st.Count}개, 가로 토막 {hz.Count}");
        Ok((l.Px.Get(100, 50) >> 24) == 0 && (l.Px.Get(40, 50) >> 24) > 0, "교차점까지 지우기: 그림에서도");
        Save(l, d, "vt_cross_after.png");
        UndoRedo(d, l, before, "교차점까지 지우기");
        // 교차점이 없으면 선 전체
        var (d2, l2) = VDoc(200, 100, L(20, 50, 180, 50));
        new VectorLineTool { Mode = "cross", Size = 6 }.Also(t => { t.Down(PA(100, 50)); t.Up(PA(100, 50)); });
        Ok(S(l2).Count == 0, "교차점이 없으면 선 전체를 지움");
    }

    static T Also<T>(this T t, Action<T> a) { a(t); return t; }

    static void TestLineTool()
    {
        App.Settings = new JsonObject();
        var f = PenFeel.Current();
        var (d, l) = VDoc(320, 100, Stroke(Line(20, 50, 300, 50, 70, 0.5), 12, true, 0.2));
        string before = J(l);
        var tool = new VectorLineTool { Mode = "width", Size = 60, Strength = 1, Thin = false };
        tool.Down(PA(140, 50));
        for (int r = 0; r < 3; r++) { for (int x = 141; x <= 180; x++) tool.Move(PA(x, 50)); for (int x = 179; x >= 140; x--) tool.Move(PA(x, 50)); }
        tool.Up(PA(140, 50));
        var w = VecOps.Widths(S(l)[0], f); var p = VecOps.Pts(S(l)[0]);
        int ci = Enumerable.Range(0, w.Length).OrderBy(i => Math.Abs(p[i * 4] - 160)).First();
        int fi = Enumerable.Range(0, w.Length).OrderBy(i => Math.Abs(p[i * 4] - 30)).First();
        Ok(w[ci] > w[fi] * 1.3 && Math.Abs(w[fi] - 12 * (0.2 + 0.8 * 0.5)) < 0.05, "굵기 도구: 문지른 곳만 굵어짐", $"가운데 {w[ci]:0.00}, 먼 곳 {w[fi]:0.00}px");
        Ok(App.History.UndoLabel == "선 굵기 수정", "굵기 도구: 한 번 끌기 = 실행취소 하나", App.History.UndoLabel);
        UndoRedo(d, l, before, "굵기 도구");
        // 부드럽게 붓
        var (d2, l2) = VDoc(320, 100, Stroke(Enumerable.Range(0, 60).Select(i => (20.0 + i * 4.5, 50.0 + (i % 2 == 0 ? -3 : 3), 1.0)), 4, false));
        string b2 = J(l2);
        var sm = new VectorLineTool { Mode = "smooth", Size = 80, Strength = 1 };
        sm.Down(PA(150, 50)); for (int x = 150; x <= 180; x += 2) sm.Move(PA(x, 50)); sm.Up(PA(180, 50));
        var q = VecOps.Pts(S(l2)[0]);
        double devIn = Enumerable.Range(0, VecOps.Count(q)).Where(i => Math.Abs(q[i * 4] - 165) < 15).Max(i => Math.Abs(q[i * 4 + 1] - 50));
        double devOut = Enumerable.Range(0, VecOps.Count(q)).Where(i => q[i * 4] < 60).Max(i => Math.Abs(q[i * 4 + 1] - 50));
        Ok(devIn < 1.5 && devOut > 2.9, "부드럽게 붓: 문지른 곳만 펴짐", $"붓 안 떨림 {devIn:0.00}, 밖 {devOut:0.00}px");
        UndoRedo(d2, l2, b2, "부드럽게 붓");
        // 단순화 붓
        var (d3, l3) = VDoc(320, 100, Stroke(Line(20, 50, 300, 50, 140, 1, 0.2), 4, false));
        string b3 = J(l3);
        int n0 = VecOps.Count(VecOps.Pts(S(l3)[0]));
        var si = new VectorLineTool { Mode = "simplify", Size = 60, Strength = 0.5 };
        si.Down(PA(150, 50)); si.Move(PA(160, 50)); si.Up(PA(160, 50));
        var q3 = VecOps.Pts(S(l3)[0]);
        int inside = Enumerable.Range(0, VecOps.Count(q3)).Count(i => Math.Abs(q3[i * 4] - 155) < 25), outside = Enumerable.Range(0, VecOps.Count(q3)).Count(i => q3[i * 4] < 60);
        Ok(VecOps.Count(q3) < n0 && inside <= 3 && outside >= 19, "단순화 붓: 문지른 구간만 점이 줄어듦", $"{n0} → {VecOps.Count(q3)}점 (붓 안 {inside}, 왼쪽 {outside})");
        UndoRedo(d3, l3, b3, "단순화 붓");
        // 선 끝 다듬기 (탭)
        var (d4, l4) = VDoc(320, 100, Stroke(Line(20, 50, 300, 50, 30, 1), 10, false));
        string b4 = J(l4);
        var tp = new VectorLineTool { Mode = "taper" };
        VectorLineTool.TaperIn = 50; VectorLineTool.TaperOut = 80;
        tp.Down(PA(150, 51)); tp.Up(PA(150, 51));
        var w4 = VecOps.Widths(S(l4)[0], f);
        Ok(w4[0] < 1.5 && w4[^1] < 1.5, "선 끝 다듬기 도구 (탭)", $"시작 {w4[0]:0.00}, 끝 {w4[^1]:0.00}px");
        UndoRedo(d4, l4, b4, "선 끝 다듬기 도구");
    }

    // ---- 채우기 -----------------------------------------------------------------------------------------
    const int FW = 400, FH = 260;
    static readonly int[] Gaps = { 3, 6, 10 };

    /// <summary>투명 바탕에 검은 선 (3px): 위 줄 = 네모 3개 (오른쪽 변에 3/6/10px 틈), 아래 줄 = 동그라미 3개 (같은 틈)</summary>
    static byte[] GapImage(out List<(int x, int y)> inside)
    {
        var img = new byte[FW * FH * 4];
        void P(int x, int y) { if (x < 0 || y < 0 || x >= FW || y >= FH) return; int i = (y * FW + x) * 4; img[i] = img[i + 1] = img[i + 2] = 0; img[i + 3] = 255; }
        inside = new();
        for (int k = 0; k < 3; k++)
        {
            int x0 = 20 + k * 130, y0 = 15, s = 100, g = Gaps[k];
            int gy0 = y0 + s / 2 - g / 2, gy1 = gy0 + g - 1;          // 틈 (빈 줄)
            for (int t = 0; t < 3; t++)
                for (int i = 0; i <= s; i++)
                {
                    P(x0 + i, y0 + t); P(x0 + i, y0 + s - t); P(x0 + t, y0 + i);
                    int yy = y0 + i; if (yy < gy0 || yy > gy1) P(x0 + s - t, yy);
                }
            inside.Add((x0 + s / 2, y0 + s / 2));
            // 동그라미: 오른쪽에 같은 너비(현의 길이)의 틈
            int cx = 70 + k * 130, cy = 190, r = 50;
            double half = Math.Asin((g + 3) / 2.0 / r);   // 틈 g + 선 끝 반올림 여유
            for (int y = cy - r - 3; y <= cy + r + 3; y++)
                for (int x = cx - r - 3; x <= cx + r + 3; x++)
                {
                    double dd = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (dd < r - 1.5 || dd > r + 1.5) continue;
                    if (x > cx && Math.Abs(y - cy) <= g / 2.0) continue;    // 오른쪽 가운데 틈
                    P(x, y);
                }
            inside.Add((cx, cy));
            _ = half;
        }
        return img;
    }

    static Bitmap MaskBitmap(byte[] img, IList<byte[]> masks, IList<Color> colors, IEnumerable<GapClose.Seg> segs = null, int scale = 2)
    {
        var bmp = new Bitmap(FW * scale, FH * scale);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var raw = new Bitmap(FW, FH);
            for (int y = 0; y < FH; y++)
                for (int x = 0; x < FW; x++)
                {
                    int i = y * FW + x;
                    Color c = Color.White;
                    for (int m = 0; m < masks.Count; m++) if (masks[m] != null && masks[m][i] != 0) c = colors[m];
                    int a = img[i * 4 + 3];
                    if (a > 0) c = Color.FromArgb(255, (c.R * (255 - a) + img[i * 4] * a) / 255, (c.G * (255 - a) + img[i * 4 + 1] * a) / 255, (c.B * (255 - a) + img[i * 4 + 2] * a) / 255);
                    raw.SetPixel(x, y, c);
                }
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(raw, 0, 0, FW * scale, FH * scale);
            if (segs != null)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pk = new Pen(Color.FromArgb(235, 40, 160), 2.5f);
                foreach (var s in segs) g.DrawLine(pk, (s.X0 + 0.5f) * scale, (s.Y0 + 0.5f) * scale, (s.X1 + 0.5f) * scale, (s.Y1 + 0.5f) * scale);
            }
        }
        return bmp;
    }

    static readonly Color[] Pal = { Color.FromArgb(120, 190, 255), Color.FromArgb(255, 200, 90), Color.FromArgb(140, 220, 140), Color.FromArgb(240, 150, 200), Color.FromArgb(180, 160, 255), Color.FromArgb(250, 240, 120) };

    static void TestGapFill()
    {
        var img = GapImage(out var inside);
        using (var b0 = MaskBitmap(img, new byte[0][], Array.Empty<Color>())) b0.Save(Path.Combine(dir, "fill_gaps_before.png"));
        (int x, int y) outside = (5, 130);
        // 틈 닫기 0: 모든 도형이 샘
        var m0 = new List<byte[]>();
        for (int k = 0; k < inside.Count; k++)
        {
            var (m, _) = Raster.FillMask(img, FW, FH, inside[k].x, inside[k].y, new Raster.FillOpt { Tolerance = 12, Gap = 0, Expand = 0 });
            m0.Add(m);
            string shape = k % 2 == 0 ? "네모" : "동그라미";
            Ok(m[outside.y * FW + outside.x] != 0, $"틈 닫기 0: {shape} {Gaps[k / 2]}px 틈으로 샘");
        }
        using (var b1 = MaskBitmap(img, new[] { m0[0] }, new[] { Pal[0] })) b1.Save(Path.Combine(dir, "fill_gaps_close0.png"));
        // 틈 닫기 8: 3·6px 은 안만, 10px 은 샘
        var m8 = new List<byte[]>(); List<GapClose.Seg> segs = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int k = 0; k < inside.Count; k++)
        {
            var fo = new Raster.FillOpt { Tolerance = 12, Gap = 0, Expand = 0, Close = 8 };
            var (m, b) = Raster.FillMask(img, FW, FH, inside[k].x, inside[k].y, fo);
            segs ??= fo.Closings;
            int g = Gaps[k / 2]; string shape = k % 2 == 0 ? "네모" : "동그라미";
            bool leak = m[outside.y * FW + outside.x] != 0;
            if (g <= 6)
            {
                // 안쪽 전체(선에서 4px 떨어진 곳까지)는 칠하고 바깥은 안 칠함
                var (cx, cy) = inside[k]; int filledIn = 0, totalIn = 0;
                for (int y = cy - 40; y <= cy + 40; y++) for (int x = cx - 40; x <= cx + 40; x++)
                {
                    if (k % 2 == 1 && (x - cx) * (x - cx) + (y - cy) * (y - cy) > 44 * 44) continue;
                    totalIn++; if (m[y * FW + x] != 0) filledIn++;
                }
                Ok(!leak && filledIn == totalIn && b.W <= 100 && b.H <= 100, $"틈 닫기 8: {shape} {g}px 틈은 안만 칠함", $"안쪽 {filledIn}/{totalIn}, 범위 {b.W}×{b.H}");
                // 닫는 선 자리(틈)까지 칠함
                int gx = k % 2 == 0 ? 20 + (k / 2) * 130 + 100 - 1 : 70 + (k / 2) * 130 + 50;
                Ok(m[cy * FW + gx] != 0 || m[cy * FW + gx - 1] != 0, $"틈 닫기 8: {shape} {g}px 틈 자리까지 칠함");
            }
            else Ok(leak, $"틈 닫기 8: {shape} {g}px 틈은 너무 넓어 샘");
            m8.Add(m);
        }
        long ms = sw.ElapsedMilliseconds;
        Ok(segs != null && segs.Count >= 4, "틈 닫기 선 찾기", $"{segs?.Count}개, 6번 채우기 {ms}ms");
        // 평행선·좁은 칸은 막지 않음: 4px 간격 두 줄 사이 통로로 칠하기가 지나감 (기존 "틈 막기" 와 다른 점)
        var lanes = new byte[120 * 60 * 4];
        for (int x = 10; x < 110; x++) foreach (int y in new[] { 20, 21, 26, 27 }) { int i = (y * 120 + x) * 4; lanes[i + 3] = 255; }
        var (ml, _) = Raster.FillMask(lanes, 120, 60, 50, 23, new Raster.FillOpt { Tolerance = 12, Close = 8 });
        Ok(ml[23 * 120 + 5] != 0, "틈 닫기: 4px 통로는 막지 않음 (선 끝이 아닌 곳)");
        using (var b2 = MaskBitmap(img, new[] { m8[0], m8[1], m8[2], m8[3] }, new[] { Pal[0], Pal[1], Pal[2], Pal[3] }, segs)) b2.Save(Path.Combine(dir, "fill_gaps_close8.png"));
        using (var b3 = MaskBitmap(img, new[] { m8[4] }, new[] { Pal[4] }, segs)) b3.Save(Path.Combine(dir, "fill_gaps_close8_10px_leak.png"));
        // 미리 보기 벽 (투명·흰 바탕 기준) 도 같은 닫는 선
        var pv = GapClose.Find(GapClose.PreviewWall(img, FW, FH, 12), FW, FH, 8);
        Ok(pv.Count == segs.Count, "미리 보기 닫는 선 = 채우기 닫는 선", $"{pv.Count} / {segs.Count}");
        // 틈 닫기 0 이면 예전과 똑같은 결과
        var (ma, ba) = Raster.FillMask(img, FW, FH, inside[0].x, inside[0].y, new Raster.FillOpt { Tolerance = 12, Gap = 4, Expand = 2 });
        var (mb, bb) = Raster.FillMask(img, FW, FH, inside[0].x, inside[0].y, new Raster.FillOpt { Tolerance = 12, Gap = 4, Expand = 2, Close = 0, Leftover = 0 });
        Ok(ma.AsSpan().SequenceEqual(mb) && ba == bb, "틈 닫기 0·메우기 0 이면 예전 채우기와 같음");
    }

    static void TestRefFill()
    {
        var img = GapImage(out var inside);
        var d = Document.New(FW, FH, "참조 시험");
        var lines = d.Layers[0]; lines.Name = "선화"; lines.Ref = true;
        for (int y = 0; y < FH; y++) for (int x = 0; x < FW; x++) if (img[(y * FW + x) * 4 + 3] != 0) lines.Px.Set(x, y, 0xFF000000);
        var color = new Layer(d, "밑색"); d.Layers.Add(color); d.ActiveId = color.Id;
        App.SetDoc(d);
        var refImg = SelUtil.ReferenceImage("ref");
        var fo = new Raster.FillOpt { Tolerance = 12, Gap = 0, Expand = 1, Close = 8 };
        var (m, b) = Raster.FillMask(refImg, FW, FH, inside[2].x, inside[2].y, fo);    // 6px 틈 네모
        FillTool.Apply(d, color, color.Px, m, b, 0xFF3399FF, 0, 4);
        var (cx, cy) = inside[2];
        Ok(color.Px.Get(cx, cy) == 0xFF3399FF && color.Px.Get(5, 130) == 0, "참조 레이어 모드 틈 닫기: 밑색 레이어에 안만 칠함");
        Ok(lines.Px.Get(cx, 15) == 0xFF000000 && lines.Px.Get(cx, cy) == 0, "참조 레이어 모드: 선화 레이어는 그대로");
        App.History.Undo(d);
        Ok(color.Px.Get(cx, cy) == 0, "참조 레이어 채우기 실행취소");
        App.History.Redo(d);
        using var bmp = Bmp(color.Px, new Rect(0, 0, FW, FH));
        using (var g = Graphics.FromImage(bmp)) using (var lb = Bmp(lines.Px, new Rect(0, 0, FW, FH), transparent: true)) g.DrawImage(lb, 0, 0);
        bmp.Save(Path.Combine(dir, "fill_ref_close8.png"));
    }

    static void TestMulti()
    {
        // 칸 3개 (닫힌 네모) + 그 사이 선 — 끌어서 지나간 칸만
        int w = 360, h = 120;
        var d = Document.New(w, h, "여러 칸");
        var l = d.Active;
        // 벽을 같이 쓰는 칸 3개 (세로선 x=10·110·210·310, 가로선 y=10·110, 2px)
        for (int t = 0; t < 2; t++)
        {
            foreach (int vx in new[] { 10, 110, 210, 310 }) for (int y = 10; y <= 111; y++) l.Px.Set(vx + t, y, 0xFF000000);
            foreach (int hy in new[] { 10, 110 }) for (int x = 10; x <= 311; x++) l.Px.Set(x, hy + t, 0xFF000000);
        }
        var paint = new Layer(d, "칠"); d.Layers.Add(paint); d.ActiveId = paint.Id;
        App.SetDoc(d);
        var img = SelUtil.ReferenceImage("all");
        var seeds = Enumerable.Range(0, 60).Select(i => (30 + i * 4, 60)).ToList();     // 30 → 266: 칸 3개를 지나감 (선 위 점도 포함)
        var (mask, bb, regions) = GapClose.FillMulti(img, w, h, seeds, new Raster.FillOpt { Tolerance = 12, Gap = 0, Expand = 0 });
        Ok(regions == 3 && mask[60 * w + 60] != 0 && mask[60 * w + 170] != 0 && mask[60 * w + 260] != 0 && mask[60 * w + 110] == 0 && mask[60 * w + 340] == 0 && mask[5 * w + 60] == 0,
            "여러 칸 칠하기: 지나간 칸 3개만 (선·바깥은 안 칠함)", $"칸 {regions}개");
        // 도구로 끌기 (실행취소 한 번)
        var tool = (FillTool)Registry.Tools.Find(t => t.Id == "fill");
        tool.Multi = true; tool.Source = "all"; tool.Gap = 0; tool.Expand = 0; tool.Close = 0; tool.Leftover = 0; tool.Dither = 0; tool.Tolerance = 12;
        App.Fg = 0xFFFF8800;
        int undo0 = App.History.UndoItems.Count;
        tool.Down(PA(30, 60)); for (int x = 34; x <= 170; x += 4) tool.Move(PA(x, 60)); tool.Up(PA(172, 60));
        for (int i = 0; i < 400 && !tool.MultiIdle; i++) Thread.Sleep(10);
        Ok(tool.MultiIdle && paint.Px.Get(60, 60) == 0xFFFF8800 && paint.Px.Get(170, 60) == 0xFFFF8800 && paint.Px.Get(260, 60) == 0 && paint.Px.Get(110, 60) == 0,
            "여러 칸 칠하기 도구: 끈 곳의 칸 2개", $"끝남={tool.MultiIdle}");
        Ok(App.History.UndoItems.Count == undo0 + 1 && (App.History.UndoLabel ?? "").StartsWith("여러 칸 칠하기"), "여러 칸 칠하기: 실행취소 하나", App.History.UndoLabel);
        using (var bmp = Bmp(paint.Px, new Rect(0, 0, w, h)))
        {
            using (var g = Graphics.FromImage(bmp)) using (var lb = Bmp(l.Px, new Rect(0, 0, w, h), transparent: true)) { g.DrawImage(lb, 0, 0); g.DrawLine(Pens.Red, 30, 60, 172, 60); }
            bmp.Save(Path.Combine(dir, "fill_multi_drag.png"));
        }
        App.History.Undo(d);
        Ok(paint.Px.Get(60, 60) == 0 && paint.Px.Get(170, 60) == 0, "여러 칸 칠하기 실행취소");
        tool.Multi = false;
    }

    static void TestLeftover()
    {
        // 닫힌 네모 안에 작은 점(1·2·3px)과 큰 네모(14px) — 메우기 5px 이면 작은 점만 함께 칠함
        int w = 160, h = 120;
        var img = new byte[w * h * 4];
        void P(int x, int y) { int i = (y * w + x) * 4; img[i + 3] = 255; }
        for (int t = 0; t < 2; t++) { for (int x = 10; x <= 150; x++) { P(x, 10 + t); P(x, 110 - t); } for (int y = 10; y <= 110; y++) { P(10 + t, y); P(150 - t, y); } }
        var dots = new[] { (40, 40, 1), (60, 40, 2), (80, 40, 3), (100, 70, 2), (50, 80, 3) };
        foreach (var (x, y, s) in dots) for (int yy = 0; yy < s; yy++) for (int xx = 0; xx < s; xx++) P(x + xx, y + yy);
        for (int yy = 60; yy < 74; yy++) for (int xx = 110; xx < 124; xx++) P(xx, yy);
        var (m0, _) = Raster.FillMask(img, w, h, 25, 25, new Raster.FillOpt { Tolerance = 12 });
        var (m1, _) = Raster.FillMask(img, w, h, 25, 25, new Raster.FillOpt { Tolerance = 12, Leftover = 5 });
        int holes0 = dots.Count(q => m0[q.Item2 * w + q.Item1] == 0), holes1 = dots.Count(q => m1[q.Item2 * w + q.Item1] == 0);
        Ok(holes0 == dots.Length && holes1 == 0, "덜 칠한 곳 메우기: 작은 점(3px 이하)이 함께 칠해짐", $"빈 점 {holes0} → {holes1}");
        Ok(m1[66 * w + 116] == 0 && m1[5 * w + 5] == 0 && m1[10 * w + 60] == 0, "덜 칠한 곳 메우기: 큰 그림(14px)·바깥·테두리 선은 그대로");
        using (var b0 = MaskBitmapSmall(img, w, h, m0)) b0.Save(Path.Combine(dir, "fill_leftover_before.png"));
        using (var b1 = MaskBitmapSmall(img, w, h, m1)) b1.Save(Path.Combine(dir, "fill_leftover_after.png"));
    }

    static void TestPerf()
    {
        // A4 350dpi (2480×3508) 에 3px 선 400개 (군데군데 끊김) — 틈 닫기 채우기 한 번이 너무 느리지 않은지
        int w = 2480, h = 3508;
        var img = new byte[w * h * 4];
        var rnd = new Random(7);
        for (int k = 0; k < 400; k++)
        {
            double x0 = rnd.Next(w), y0 = rnd.Next(h), a = rnd.NextDouble() * Math.PI, len = 200 + rnd.Next(900);
            int gapAt = rnd.Next(40, 160), gap = rnd.Next(2, 9);
            for (int t = 0; t < len; t++)
            {
                if (t >= gapAt && t < gapAt + gap) continue;
                int cx = (int)(x0 + Math.Cos(a) * t), cy = (int)(y0 + Math.Sin(a) * t);
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int x = cx + dx, y = cy + dy; if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    img[(y * w + x) * 4 + 3] = 255;
                }
            }
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var segs = GapClose.Find(GapClose.PreviewWall(img, w, h, 12), w, h, 8);
        long find = sw.ElapsedMilliseconds; sw.Restart();
        var (m, b) = Raster.FillMask(img, w, h, 5, 5, new Raster.FillOpt { Tolerance = 12, Gap = 0, Expand = 2, Close = 8, Leftover = 4 });
        long fill = sw.ElapsedMilliseconds;
        Ok(find < 4000 && fill < 6000, "A4 원고 틈 닫기 속도", $"닫는 선 찾기 {find}ms ({segs.Count}개), 틈 닫기 채우기 {fill}ms");
    }

    static Bitmap MaskBitmapSmall(byte[] img, int w, int h, byte[] m, int scale = 3)
    {
        var raw = new Bitmap(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            var c = m[i] != 0 ? Color.FromArgb(120, 190, 255) : Color.White;
            if (img[i * 4 + 3] > 0 && m[i] == 0) c = Color.Black;
            else if (img[i * 4 + 3] > 0) c = Color.FromArgb(40, 90, 160);    // 칠해진 점 (원래 검은 점)
            raw.SetPixel(x, y, c);
        }
        var big = new Bitmap(w * scale, h * scale);
        using (var g = Graphics.FromImage(big)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half; g.DrawImage(raw, 0, 0, w * scale, h * scale); }
        raw.Dispose();
        return big;
    }
}
