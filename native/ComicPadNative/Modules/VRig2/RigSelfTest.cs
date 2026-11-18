// 시험 (창 없이): --selftest vrig2 결과폴더 → 결과폴더/report.txt + 미리보기 PNG·GIF, 끝 코드 = 실패 수.
// 코드로 그린 캐릭터(부위마다 레이어) → 자동 리그 → 메쉬 덮음·기본 자세 = 원래 그림·고개 돌리기 시차·눈 감기·입 벌리기·키폼 보간·물리·클리핑·
// .cpad/.cpvt 왕복·같은 결과·속도(1280×720)를 확인한다. 웹캠·마이크·사용자 폴더는 쓰지 않는다.
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Scene3D;

namespace ComicPadNative.Modules.VRig2;

public static class RigSelfTest
{
    static readonly StringBuilder log = new();
    static int fail;
    static string outDir;
    static void Ok(bool c, string name) { log.AppendLine((c ? "통과  " : "실패  ") + name); if (!c) fail++; }
    static void Info(string s) => log.AppendLine("      " + s);

    public static int Run(string dir)
    {
        outDir = dir; log.Clear(); fail = 0;
        try { RunAll(); }
        catch (Exception e) { Ok(false, "예외: " + e); }
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        return fail;
    }

    // ------------------------------------------------------------------ 시험용 캐릭터
    static readonly Color Skin = Color.FromArgb(255, 226, 204), Hair = Color.FromArgb(120, 72, 52), HairDark = Color.FromArgb(70, 40, 30), Line = Color.FromArgb(60, 36, 30), IrisC = Color.FromArgb(40, 70, 170), Shirt = Color.FromArgb(70, 110, 190);

    static Layer AddLayer(Document d, string name, Action<Graphics> draw, bool visible = true)
    {
        var l = new Layer(d, name) { Visible = visible };
        using var bmp = new Bitmap(d.W, d.H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent); draw(g); }
        CpadIO.DrawBitmap(l.Px, bmp, 0, 0);
        d.Layers.Add(l);
        return l;
    }

    public static Document MakeCharacter()
    {
        var d = new Document(1024, 1024) { Name = "리깅시험" };
        using var line = new Pen(Line, 4) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        AddLayer(d, "뒷머리", g =>
        {
            using var b = new SolidBrush(HairDark);
            using var path = new GraphicsPath();
            path.AddEllipse(282, 140, 460, 470);
            path.AddRectangle(new Rectangle(300, 380, 424, 330));
            g.FillPath(b, path);
        });
        AddLayer(d, "몸", g =>
        {
            using var b = new SolidBrush(Shirt);
            using var path = new GraphicsPath();
            path.AddBezier(300, 1024, 300, 760, 360, 690, 512, 680);
            path.AddBezier(512, 680, 664, 690, 724, 760, 724, 1024);
            path.CloseFigure();
            g.FillPath(b, path);
            g.DrawPath(line, path);
        });
        AddLayer(d, "목", g => { using var b = new SolidBrush(Color.FromArgb(240, 205, 185)); g.FillRectangle(b, 470, 560, 84, 140); });
        AddLayer(d, "얼굴", g =>
        {
            using var b = new SolidBrush(Skin);
            using var path = new GraphicsPath();
            path.AddBezier(342, 380, 342, 230, 682, 230, 682, 380);
            path.AddBezier(682, 380, 682, 520, 580, 610, 512, 612);
            path.AddBezier(512, 612, 444, 610, 342, 520, 342, 380);
            path.CloseFigure();
            g.FillPath(b, path);
            g.DrawPath(line, path);
        });
        AddLayer(d, "볼터치", g => { using var b = new SolidBrush(Color.FromArgb(110, 255, 120, 140)); g.FillEllipse(b, 392, 470, 60, 26); g.FillEllipse(b, 572, 470, 60, 26); });
        AddLayer(d, "흰자", g =>
        {
            using var b = new SolidBrush(Color.White); using var p = new Pen(Color.FromArgb(150, 120, 110), 2);
            g.FillEllipse(b, 398, 382, 84, 60); g.DrawEllipse(p, 398, 382, 84, 60);
            g.FillEllipse(b, 542, 382, 84, 60); g.DrawEllipse(p, 542, 382, 84, 60);
        });
        AddLayer(d, "눈동자", g =>
        {
            using var b = new SolidBrush(IrisC); using var pu = new SolidBrush(Color.FromArgb(15, 20, 50)); using var hi = new SolidBrush(Color.White);
            foreach (int cx in new[] { 440, 584 }) { g.FillEllipse(b, cx - 20, 392, 40, 42); g.FillEllipse(pu, cx - 8, 404, 16, 18); g.FillEllipse(hi, cx - 12, 398, 8, 8); }
        });
        AddLayer(d, "속눈썹", g =>
        {
            using var p = new Pen(Color.FromArgb(40, 25, 25), 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(p, 394, 378, 92, 66, 195, 150); g.DrawArc(p, 538, 378, 92, 66, 195, 150);
            using var p2 = new Pen(Color.FromArgb(40, 25, 25), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(p2, 398, 398, 386, 388); g.DrawLine(p2, 626, 398, 638, 388);
        });
        AddLayer(d, "감은 눈", g =>
        {
            using var p = new Pen(Color.FromArgb(40, 25, 25), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(p, 400, 400, 80, 30, 10, 160); g.DrawArc(p, 544, 400, 80, 30, 10, 160);
        }, visible: false);
        AddLayer(d, "눈썹", g =>
        {
            using var p = new Pen(HairDark, 8) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(p, 400, 340, 84, 30, 200, 140); g.DrawArc(p, 540, 340, 84, 30, 200, 140);
        });
        AddLayer(d, "코", g => { using var p = new Pen(Color.FromArgb(200, 140, 120), 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawLine(p, 512, 470, 506, 492); g.DrawLine(p, 506, 492, 514, 494); });
        AddLayer(d, "입 안", g => { using var b = new SolidBrush(Color.FromArgb(150, 40, 50)); g.FillEllipse(b, 484, 532, 56, 44); using var t = new SolidBrush(Color.FromArgb(230, 110, 120)); g.FillEllipse(t, 496, 556, 32, 16); }, visible: false);
        AddLayer(d, "입", g => { using var p = new Pen(Color.FromArgb(150, 50, 50), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round }; g.DrawArc(p, 482, 510, 60, 30, 20, 140); });
        AddLayer(d, "옆머리", g =>
        {
            using var b = new SolidBrush(Hair);
            foreach (int s in new[] { -1, 1 })
            {
                using var path = new GraphicsPath();
                float x0 = 512 + s * 180, x1 = 512 + s * 140;
                path.AddBezier(x0, 300, x0 + s * 30, 420, x0 + s * 20, 560, x0 + s * 6, 680);
                path.AddLine(x0 + s * 6, 680, x1 + s * 10, 640);
                path.AddBezier(x1 + s * 10, 640, x1 + s * 4, 520, x1, 420, x1 - s * 10, 300);
                path.CloseFigure();
                g.FillPath(b, path);
                using var pl = new Pen(Line, 3); g.DrawPath(pl, path);
            }
        });
        AddLayer(d, "앞머리", g =>
        {
            using var b = new SolidBrush(Hair);
            using var path = new GraphicsPath();
            path.AddBezier(322, 400, 300, 170, 724, 170, 702, 400);
            var pts = new List<PointF>();
            float y = 400;
            for (int i = 0; i <= 8; i++) { float x = 702 - i * 47.5f; pts.Add(new PointF(x, i % 2 == 0 ? y - (i == 0 || i == 8 ? 0 : 20) : 345 - (i % 4) * 6)); }
            path.AddLines(pts.ToArray());
            path.CloseFigure();
            g.FillPath(b, path);
            using var pl = new Pen(Line, 4) { LineJoin = LineJoin.Round }; g.DrawPath(pl, path);
        });
        d.ActiveId = d.Layers[^1].Id;
        return d;
    }

    /// <summary>보이는 레이어를 표준 합성 (투명 바탕)</summary>
    static uint[] Flat(Document d)
    {
        var o = new uint[d.W * d.H];
        foreach (var l in d.Layers)
        {
            if (!l.Visible || l.Px == null) continue;
            var p = l.Px.Read(l.Px.Full);
            for (int i = 0; i < o.Length; i++) if (p[i] != 0) o[i] = RigTexture.Over(p[i], o[i]);
        }
        return o;
    }

    static void SavePng(uint[] px, int w, int h, string name, Color? bg = null)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        System.Runtime.InteropServices.Marshal.Copy((int[])(object)px, 0, bd.Scan0, w * h);
        bmp.UnlockBits(bd);
        using var outB = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(outB)) { g.Clear(bg ?? Color.FromArgb(235, 235, 240)); g.DrawImageUnscaled(bmp, 0, 0); }
        outB.Save(Path.Combine(outDir, name), ImageFormat.Png);
    }

    static uint[] Render(RigRuntime rt, int w, int h, double s, double ox = 0, double oy = 0, bool parallel = true)
    {
        var r = new CpuRigRenderer { Parallel = parallel };
        rt.Evaluate();
        rt.Render(r, w, h, s, ox, oy, 0);
        return (uint[])r.Pixels.Clone();
    }

    static (double x, double y) MeanPos(RigRuntime rt, string role)
    {
        double sx = 0, sy = 0; int n = 0;
        for (int i = 0; i < rt.Rig.Parts.Count; i++)
        {
            if (rt.Rig.Parts[i].Role != role) continue;
            var p = rt.PS[i].Pos;
            for (int k = 0; k + 1 < p.Length; k += 2) { sx += p[k]; sy += p[k + 1]; n++; }
        }
        return n == 0 ? (double.NaN, double.NaN) : (sx / n, sy / n);
    }

    static void RunAll()
    {
        var d = MakeCharacter();
        App.Doc = d;
        // ---- 자동 리그
        var notes = new List<string>();
        var sw = Stopwatch.StartNew();
        var (rig, tex) = AutoRig.Build(d, null, notes);
        long buildMs = sw.ElapsedMilliseconds;
        Info($"자동 리그 {buildMs}ms: " + string.Join(" / ", notes));
        var rt = new RigRuntime(rig);
        foreach (var (k, t) in tex) rt.Tex[k] = t;
        rt.Compile();
        Info("부위: " + string.Join(", ", rig.Parts.Select(p => $"{p.Name}[{p.Role}{(p.Side != "" ? "·" + p.Side : "")}] v{p.Mesh.N}/t{p.Mesh.T.Length / 3}")));
        Info("디포머: " + string.Join(", ", rig.Deformers.Select(x => $"{x.Name}({x.Kind}, 축 {string.Join("×", x.Grid.Axes.Select(a => a.Param.Replace("Param", "")))})")));
        Ok(rig.Parts.Count >= 18, $"자동 리그 부위 {rig.Parts.Count}개 (눈·눈썹은 좌우로 나뉨)");
        Ok(rig.Parts.Count(p => p.Role == "eyeWhite") == 2 && rig.Parts.Count(p => p.Role == "iris") == 2 && rig.Parts.Count(p => p.Role == "brow") == 2, "흰자·눈동자·눈썹이 좌우 두 부위로 나뉨");
        Ok(rig.Parts.All(p => p.Role != "eyeClosed" || p.Grid.Axes.Any(a => a.Param.Contains("EyeLOpen") || a.Param.Contains("EyeROpen"))), "감은 눈(숨긴 레이어)도 부위로 들어감");
        Ok(rig.Physics.Count == 3, $"물리 {rig.Physics.Count}개 (앞·옆·뒷머리)");

        // ---- 메쉬 덮음
        double worst = 2; string worstName = "";
        foreach (var p in rig.Parts)
        {
            var (c, t) = MeshGen.Coverage(p.Mesh, tex[p.LayerId], p.Region);
            double r = t == 0 ? 1 : (double)c / t;
            if (r < worst) { worst = r; worstName = p.Name; }
        }
        Ok(worst >= 0.98, $"모든 부위 메쉬가 그림의 98% 이상을 덮음 (가장 낮은 것 {worstName} {worst * 100:0.00}%)");

        // ---- 기본 자세 = 원래 그림
        rt.Reset();
        var flat = Flat(d);
        var def = Render(rt, d.W, d.H, 1);
        int maxDiff = 0; long big = 0;
        for (int i = 0; i < flat.Length; i++)
        {
            uint a = flat[i], b = def[i];
            for (int s = 0; s < 32; s += 8) { int dd = Math.Abs((int)(a >> s & 255) - (int)(b >> s & 255)); if (dd > maxDiff) maxDiff = dd; if (dd > 2) { big++; break; } }
        }
        Ok(maxDiff <= 3 && big < 50, $"기본 파라미터 = 원래 그림 (최대 채널 차이 {maxDiff}, 2 넘게 다른 픽셀 {big}개)");
        SavePng(flat, d.W, d.H, "flat_reference.png");
        SavePng(def, d.W, d.H, "rig_default.png");

        // ---- 고개 좌우: 시차
        double Shift(string role, double ax)
        {
            rt.Reset(); rt.Evaluate(); var a = MeanPos(rt, role);
            rt.Set("ParamAngleX", ax); rt.Evaluate(); var b = MeanPos(rt, role);
            rt.Reset();
            return b.x - a.x;
        }
        foreach (double ax in new[] { 30.0, -30.0 })
        {
            double sF = Shift("hairFront", ax), sFace = Shift("face", ax), sEye = Shift("eyeWhite", ax), sNose = Shift("nose", ax), sBack = Shift("hairBack", ax), sMouth = Shift("mouth", ax);
            double sg = Math.Sign(ax);
            Info($"AngleX {ax:+0;-0}: 이동 앞머리 {sF:0.0} · 코 {sNose:0.0} · 눈 {sEye:0.0} · 입 {sMouth:0.0} · 얼굴 {sFace:0.0} · 뒷머리 {sBack:0.0} px");
            Ok(sg * sF > 0 && sg * sNose > 0 && sg * sEye > 0 && sg * sMouth > 0 && sg * sFace > 0, $"AngleX {ax:+0;-0}: 얼굴 부위가 그쪽으로 움직임");
            Ok(sg * sF > sg * sFace && sg * sNose > sg * sFace && sg * sEye > sg * sFace && sg * sFace > sg * sBack, $"AngleX {ax:+0;-0}: 깊이 순서 (앞머리·코·눈 > 얼굴 > 뒷머리)");
        }
        rt.Reset();
        rt.Set("ParamAngleY", 30); rt.Evaluate(); var up = MeanPos(rt, "eyeWhite"); rt.Reset(); rt.Evaluate(); var mid = MeanPos(rt, "eyeWhite");
        Ok(up.y < mid.y - 2, $"AngleY +30: 눈이 위로 ({up.y - mid.y:0.0}px)");
        rt.Reset();

        // ---- 눈 감기 (왼눈 = 화면 오른쪽)
        int CountIn(uint[] px, Box b, Func<uint, bool> f)
        {
            int n = 0;
            for (int y = (int)b.Y; y < (int)b.B; y++) for (int x = (int)b.X; x < (int)b.R; x++) if ((uint)x < (uint)d.W && (uint)y < (uint)d.H && f(px[y * d.W + x])) n++;
            return n;
        }
        bool IsIris(uint c) { c = Px.Unpremul(c); int r = (int)(c >> 16 & 255), g = (int)(c >> 8 & 255), b = (int)(c & 255); return b > 120 && r < 90 && b - r > 70 && c >> 24 > 200; }
        bool IsWhite(uint c) { c = Px.Unpremul(c); return (c >> 24) > 200 && (c >> 16 & 255) > 245 && (c >> 8 & 255) > 245 && (c & 255) > 245; }
        var eyeL = new Box(530, 370, 110, 85);
        var open = Render(rt, d.W, d.H, 1);
        rt.Set("ParamEyeLOpen", 0);
        var closed = Render(rt, d.W, d.H, 1);
        int irO = CountIn(open, eyeL, IsIris), irC = CountIn(closed, eyeL, IsIris), whO = CountIn(open, eyeL, IsWhite), whC = CountIn(closed, eyeL, IsWhite);
        int irR = CountIn(closed, new Box(386, 370, 110, 85), IsIris);
        Ok(irO > 500 && irC == 0 && whO > 1000 && whC < whO * 0.02, $"EyeLOpen 0: 왼눈의 눈동자·흰자가 사라짐 (눈동자 {irO}→{irC}, 흰자 {whO}→{whC} 픽셀)");
        Ok(irR > 500, $"EyeLOpen 0: 오른눈은 그대로 ({irR} 픽셀)");
        rt.Set("ParamEyeROpen", 0);
        var bothClosed = Render(rt, d.W, d.H, 1);
        SavePng(bothClosed, d.W, d.H, "rig_eyes_closed.png");
        rt.Reset();

        // ---- 입 벌리기
        int MouthArea(double o)
        {
            rt.Reset(); rt.Set("ParamMouthOpenY", o);
            var px = Render(rt, d.W, d.H, 1);
            return CountIn(px, new Box(460, 495, 104, 120), c => { c = Px.Unpremul(c); int r = (int)(c >> 16 & 255), g = (int)(c >> 8 & 255), b = (int)(c & 255); return c >> 24 > 128 && r > 110 && g < 90 && b < 100; });
        }
        int m0 = MouthArea(0), m1 = MouthArea(1);
        Ok(m1 > m0 * 2 && m1 > 600, $"MouthOpenY 1: 입 넓이가 커짐 ({m0} → {m1} 픽셀)");
        rt.Set("ParamMouthOpenY", 1); SavePng(Render(rt, d.W, d.H, 1), d.W, d.H, "rig_mouth_open.png");
        rt.Reset();

        // ---- 키폼 보간
        TestKeyGrid();
        var hw = rig.Def("D_HeadWarp");
        double maxJump = 0; double[] prev = null; bool exact = true;
        for (double v = -30; v <= 30.0001; v += 0.25)
        {
            var f = hw.Grid.Blend(p => p == "ParamAngleX" ? v : 0, hw.Nodes);
            var fd = f.D ?? new double[hw.Nodes * 2];
            if (prev != null) for (int k = 0; k < prev.Length; k++) maxJump = Math.Max(maxJump, Math.Abs(fd[k] - prev[k]));
            prev = (double[])fd.Clone();
            if (Math.Abs(v - Math.Round(v / 30) * 30) < 1e-9)
            {
                var idx = hw.Grid.Index(new[] { hw.Grid.Axes[0].Keys.IndexOf(Math.Round(v / 30) * 30), 1 });
                var stored = hw.Grid.Forms[idx].D ?? new double[hw.Nodes * 2];
                for (int k = 0; k < stored.Length; k++) if (Math.Abs(stored[k] - fd[k]) > 1e-9) exact = false;
            }
        }
        double maxStep = hw.Grid.Forms.Where(x => x.D != null).SelectMany(x => x.D).Max(Math.Abs) / 30 * 0.25 * 1.01;
        Ok(maxJump <= maxStep + 1e-9 && exact, $"고개 워프 키폼: 키 위에서 정확, 사이는 연속 (0.25 단계 최대 변화 {maxJump:0.000}px ≤ {maxStep:0.000})");

        // ---- 물리
        TestPhysics(rt);

        // ---- 클리핑
        rt.Reset(); rt.Set("ParamEyeBallX", 1); rt.Set("ParamEyeBallY", -1);
        var clipOn = Render(rt, d.W, d.H, 1);
        var irisParts = rig.Parts.Where(p => p.Role == "iris").ToList();
        var savedClip = irisParts.Select(p => p.Clip.ToList()).ToList();
        foreach (var p in irisParts) p.Clip.Clear();
        var clipOff = Render(rt, d.W, d.H, 1);
        for (int i = 0; i < irisParts.Count; i++) irisParts[i].Clip = savedClip[i];
        // 흰자 밖 (흰자 텍스처 알파가 0 인 곳)의 눈동자 색
        var whiteTex = tex[rig.Parts.First(p => p.Role == "eyeWhite").LayerId];
        int outsideOn = 0, outsideOff = 0;
        var wb = new Box(380, 360, 270, 110);
        for (int y = (int)wb.Y; y < wb.B; y++) for (int x = (int)wb.X; x < wb.R; x++)
        {
            int tx = x - whiteTex.X, ty = y - whiteTex.Y;
            bool inWhite = (uint)tx < (uint)whiteTex.W && (uint)ty < (uint)whiteTex.H && whiteTex.Mip[0][ty * whiteTex.W + tx] >> 24 > 0;
            if (inWhite) continue;
            if (IsIris(clipOn[y * d.W + x])) outsideOn++;
            if (IsIris(clipOff[y * d.W + x])) outsideOff++;
        }
        Ok(outsideOn == 0 && outsideOff > 20, $"클리핑: 눈동자를 끝으로 돌려도 흰자 밖에는 안 그려짐 (마스크 있음 {outsideOn}, 없음 {outsideOff} 픽셀)");
        SavePng(clipOn, d.W, d.H, "rig_look_clip.png");
        rt.Reset();

        // ---- 그리기 순서 키
        TestDrawOrderKey(rt);

        // ---- 같은 결과 (여러 번·여러 코어 대 한 줄)
        rt.Reset(); foreach (var (k, v) in new[] { ("ParamAngleX", 17.0), ("ParamAngleY", -9.0), ("ParamAngleZ", 12.0), ("ParamMouthOpenY", 0.6), ("ParamEyeLOpen", 0.4) }) rt.Set(k, v);
        var r1 = Render(rt, 1280, 720, 0.7, 280, 0); var r2 = Render(rt, 1280, 720, 0.7, 280, 0); var r3 = Render(rt, 1280, 720, 0.7, 280, 0, parallel: false);
        Ok(r1.AsSpan().SequenceEqual(r2) && r1.AsSpan().SequenceEqual(r3), "같은 값이면 같은 그림 (두 번, 여러 코어 = 한 줄)");
        rt.Reset();

        // ---- 저장 왕복
        TestRoundTrips(d, rt);

        // ---- 속도
        TestSpeed(rt);

        // ---- 스튜디오 연결
        TestStudio(d, rt);

        // ---- 미리보기
        Previews(d, rt);

        // ---- 편집기 (창을 띄우지 않고 만들기만)
        TestEditor(d, rt);
    }

    static void TestKeyGrid()
    {
        var g = new KeyGrid();
        g.Axes.Add(new KeyAxis { Param = "A", Keys = new List<double> { -1, 0, 1 } });
        g.Axes.Add(new KeyAxis { Param = "B", Keys = new List<double> { 0, 2 } });
        g.Axes.Add(new KeyAxis { Param = "C", Keys = new List<double> { 0, 1 } });
        g.Forms = new List<KForm>();
        var rnd = new Random(7);
        for (int i = 0; i < g.Count; i++) g.Forms.Add(new KForm { Tx = rnd.NextDouble() * 10, Ty = rnd.NextDouble() * 10, D = new[] { rnd.NextDouble(), rnd.NextDouble() }, Op = rnd.NextDouble() });
        // 직접 계산한 3중 선형과 비교
        double maxErr = 0;
        for (int it = 0; it < 200; it++)
        {
            double a = rnd.NextDouble() * 2.4 - 1.2, b = rnd.NextDouble() * 2.4 - 0.2, c = rnd.NextDouble() * 1.2 - 0.1;
            var f = g.Blend(p => p == "A" ? a : p == "B" ? b : c, 1);
            double ac = Math.Clamp(a, -1, 1), bc = Math.Clamp(b, 0, 2), cc = Math.Clamp(c, 0, 1);
            int ia = ac >= 0 ? 1 : 0; double ta = ac - (ia - 1);
            double tb = bc / 2, tc = cc;
            double want = 0;
            for (int m = 0; m < 8; m++)
            {
                int xa = ia + (m & 1), xb = m >> 1 & 1, xc = m >> 2 & 1;
                double w = ((m & 1) == 1 ? ta : 1 - ta) * (xb == 1 ? tb : 1 - tb) * (xc == 1 ? tc : 1 - tc);
                want += w * g.Forms[g.Index(new[] { xa, xb, xc })].Tx;
            }
            maxErr = Math.Max(maxErr, Math.Abs(want - f.Tx));
        }
        Ok(maxErr < 1e-9, $"키폼 3축 다중 선형 보간 = 직접 계산 (최대 오차 {maxErr:E1})");
        // 키 넣기는 모양을 바꾸지 않음, 지우면 원래대로
        var before = Enumerable.Range(0, 50).Select(i => { double a = -1 + i * 0.04; return g.Blend(p => p == "A" ? a : p == "B" ? 0.7 : 0.3, 1).Tx; }).ToArray();
        g.AddKey("A", 0.37, 1);
        var after = Enumerable.Range(0, 50).Select(i => { double a = -1 + i * 0.04; return g.Blend(p => p == "A" ? a : p == "B" ? 0.7 : 0.3, 1).Tx; }).ToArray();
        g.RemoveKey("A", 0.37);
        var again = Enumerable.Range(0, 50).Select(i => { double a = -1 + i * 0.04; return g.Blend(p => p == "A" ? a : p == "B" ? 0.7 : 0.3, 1).Tx; }).ToArray();
        double e1 = before.Zip(after, (x, y) => Math.Abs(x - y)).Max(), e2 = before.Zip(again, (x, y) => Math.Abs(x - y)).Max();
        Ok(e1 < 1e-9 && e2 < 1e-9 && g.Count == 12, $"키 넣기·지우기가 모양을 바꾸지 않음 ({e1:E1}, {e2:E1})");
        int ex = g.ExactIndex(p => p == "A" ? 0 : p == "B" ? 2 : 1);
        Ok(ex == g.Index(new[] { 1, 1, 1 }), "키 위 판별 (편집할 키폼 찾기)");
    }

    static void TestPhysics(RigRuntime rt)
    {
        var rig = rt.Rig;
        rt.Reset(); rt.Phys.Clear();
        var outs = new List<double>();
        bool finite = true;
        double dt = 1.0 / 60;
        for (int i = 0; i < 60 * 8; i++)
        {
            double t = i * dt;
            rt.Values["ParamAngleX"] = t >= 0.5 && t < 2.5 ? 30 : 0;          // 홱 돌렸다가 돌아옴
            RigPhysics.Step(rig, rt.Phys, rt.Values, dt);
            double o = rt.Val("ParamHairFront");
            if (!double.IsFinite(o)) finite = false;
            outs.Add(o);
        }
        double peak = outs.Max(Math.Abs);
        double tailVar = outs.Skip(outs.Count - 60).Max() - outs.Skip(outs.Count - 60).Min();
        double final = outs[^1];
        double atHold = outs[(int)(2.4 / dt)];
        Ok(finite && peak > 0.15 && peak <= 1, $"물리: 고개를 돌리면 앞머리가 흔들림 (최대 {peak:0.00})");
        Ok(Math.Abs(atHold) < 0.08 && tailVar < 0.01 && Math.Abs(final) < 0.05, $"물리: 멈추면 가라앉음 (멈춘 동안 {atHold:0.000}, 끝 1초 변화 {tailVar:0.0000}, 끝 값 {final:0.000})");
        // 기울이면 아래로 늘어짐 (머리 기준으로 한쪽으로)
        rt.Reset(); rt.Phys.Clear();
        for (int i = 0; i < 300; i++) { rt.Values["ParamAngleZ"] = 30; RigPhysics.Step(rig, rt.Phys, rt.Values, dt); }
        double tilt = rt.Val("ParamHairFront");
        Ok(tilt > 0.05, $"물리: 고개를 기울이면 머리카락이 중력 쪽으로 ({tilt:0.00})");
        // 거친 입력 (큰 dt, 마구잡이) 에도 터지지 않음
        var rnd = new Random(3);
        bool ok = true;
        for (int i = 0; i < 2000; i++)
        {
            rt.Values["ParamAngleX"] = rnd.NextDouble() * 60 - 30; rt.Values["ParamAngleZ"] = rnd.NextDouble() * 60 - 30; rt.Values["ParamBodyAngleX"] = rnd.NextDouble() * 20 - 10;
            RigPhysics.Step(rig, rt.Phys, rt.Values, rnd.NextDouble() * 0.2);
            foreach (var p in rig.Physics) { double v = rt.Val(p.Output); if (!double.IsFinite(v) || Math.Abs(v) > 1.0001) ok = false; }
        }
        Ok(ok, "물리: 마구잡이 입력·큰 시간 간격에도 값이 범위 안 (터지지 않음)");
        rt.Reset(); rt.Phys.Clear();
    }

    static void TestDrawOrderKey(RigRuntime rt)
    {
        var rig = rt.Rig;
        var hair = rig.Parts.First(p => p.Role == "hairSide");
        var face = rig.Parts.First(p => p.Role == "face");
        var saved = hair.Grid.Clone();
        hair.Grid.AddKey("ParamAngleX", 30, hair.Mesh.N);
        int ex = hair.Grid.ExactIndex(p => p == "ParamAngleX" ? 30 : 0);
        hair.Grid.Forms[ex].Ord = face.Order - hair.Order - 5;          // 끝까지 돌리면 얼굴 뒤로
        rt.Reset(); rt.Evaluate();
        int hi = rig.Parts.IndexOf(hair), fi = rig.Parts.IndexOf(face);
        bool frontAt0 = Array.IndexOf(rt.DrawOrder, hi) > Array.IndexOf(rt.DrawOrder, fi);
        rt.Set("ParamAngleX", 30); rt.Evaluate();
        bool behindAt30 = Array.IndexOf(rt.DrawOrder, hi) < Array.IndexOf(rt.DrawOrder, fi);
        hair.Grid = saved; rt.Reset(); rt.Evaluate();
        Ok(frontAt0 && behindAt30, "그리기 순서 키: AngleX 30 에서 옆머리가 얼굴 뒤로");
    }

    static void TestRoundTrips(Document d, RigRuntime rt)
    {
        string json0 = rt.Rig.ToJsonString();
        Ok(Rig2.FromJson(JsonNode.Parse(json0)).ToJsonString() == json0, "리그 JSON → 읽기 → JSON 이 같음");
        d.Extra["vtRig"] = rt.Rig.ToJson();
        string path = Path.Combine(outDir, "vrig2_test.cpad");
        CpadIO.Save(d, path);
        var d2 = CpadIO.Load(path);
        var r2 = Rig2.FromDoc(d2);
        Ok(r2 != null && r2.ToJsonString() == json0 && d2.Extra["vtRig"].ToJsonString() == json0, $".cpad 왕복: 리그 JSON 이 바이트까지 같음 ({json0.Length / 1024}KB)");
        var raw = JsonNode.Parse(File.ReadAllText(path));
        Ok((string)raw["format"] == "comicpad" && raw["layers"] is JsonArray la && la.Count == d.Layers.Count && raw["vtRig"] is JsonObject, ".cpad 형식은 그대로 (웹 판이 여는 format·layers, vtRig 는 덧붙은 키)");
        // 다시 연 작품으로 그린 것 = 처음
        var rtB = new RigRuntime(r2); rtB.LoadTextures(d2); rtB.Compile();
        rt.Reset(); rtB.Reset(); rt.Set("ParamAngleX", 20); rtB.Set("ParamAngleX", 20);
        var a = Render(rt, 512, 512, 0.5); var b = Render(rtB, 512, 512, 0.5);
        int mdB = 0; for (int i = 0; i < a.Length; i++) for (int s2 = 0; s2 < 32; s2 += 8) mdB = Math.Max(mdB, Math.Abs((int)(a[i] >> s2 & 255) - (int)(b[i] >> s2 & 255)));
        Ok(mdB <= 2, $".cpad 에서 다시 읽은 리그 그림이 같음 (레이어 PNG 왕복 반올림만, 최대 차이 {mdB})");
        rt.Reset();
        // .cpvt
        var player = new RigPlayer(rt);
        var data = VRig.BuildRig(player.LegacyParts(), player.W, player.H, null);
        data.Alt = player;
        string cpvt = VRig.ToFile(data, "시험").ToJsonString();
        File.WriteAllText(Path.Combine(outDir, "vrig2_test.cpvt"), cpvt);
        var back = VRig.FromFile(JsonNode.Parse(cpvt));
        var bp = back.Alt as RigPlayer;
        Ok(bp != null && bp.RT.Rig.ToJsonString() == json0, $".cpvt 왕복: 리그 JSON 이 바이트까지 같음 (예전 부위 {back.Parts.Count}개도 함께, {cpvt.Length / 1024}KB)");
        if (bp != null)
        {
            bp.RT.Reset(); bp.RT.Set("ParamAngleX", 20); rt.Set("ParamAngleX", 20);
            var c = Render(bp.RT, 512, 512, 0.5); var e = Render(rt, 512, 512, 0.5);
            int md = 0; for (int i = 0; i < c.Length; i++) for (int s = 0; s < 32; s += 8) md = Math.Max(md, Math.Abs((int)(c[i] >> s & 255) - (int)(e[i] >> s & 255)));
            Ok(md <= 2, $".cpvt 에서 읽은 리그 그림 = 작품 리그 그림 (최대 차이 {md})");
            rt.Reset();
        }
        foreach (var p in back.Parts) p.Img?.Dispose();
        foreach (var p in data.Parts) p.Img?.Dispose();
        // Studio 가 작품에서 메쉬 리그를 찾음
        var alt = Vtuber.AltFromDoc?.Invoke(d2);
        Ok(alt != null && alt.W > 0, "스튜디오 연결: 작품의 vtRig → 메쉬 리그");
        alt?.Dispose();
    }

    static void TestSpeed(RigRuntime rt)
    {
        var rig = rt.Rig;
        double s = 720.0 / rig.H * 0.98, ox = (1280 - rig.W * s) / 2, oy = (720 - rig.H * s) / 2;
        var ren = new CpuRigRenderer();
        rt.Reset(); rt.Phys.Clear();
        for (int i = 0; i < 10; i++) { RigDrive.Apply(rt, RigDrive.Demo(i / 30.0)); rt.Evaluate(); rt.Render(ren, 1280, 720, s, ox, oy, 0xFF00FF00); }
        int n = 150;
        var sw = Stopwatch.StartNew();
        double evalMs = 0;
        for (int i = 0; i < n; i++)
        {
            var e0 = sw.Elapsed.TotalMilliseconds;
            RigDrive.Apply(rt, RigDrive.Demo(i / 30.0));
            RigPhysics.Step(rig, rt.Phys, rt.Values, 1 / 30.0);
            rt.Evaluate();
            evalMs += sw.Elapsed.TotalMilliseconds - e0;
            rt.Render(ren, 1280, 720, s, ox, oy, 0xFF00FF00);
        }
        double ms = sw.Elapsed.TotalMilliseconds / n;
        var one = new CpuRigRenderer { Parallel = false };
        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < 30; i++) { RigDrive.Apply(rt, RigDrive.Demo(i / 30.0)); rt.Evaluate(); rt.Render(one, 1280, 720, s, ox, oy, 0xFF00FF00); }
        double ms1 = sw2.Elapsed.TotalMilliseconds / 30;
        int verts = rig.Parts.Sum(p => p.Mesh.N), tris = rig.Parts.Sum(p => p.Mesh.T.Length / 3);
        Info($"속도 1280×720 (꼭짓점 {verts}, 삼각형 {tris}, 코어 {Environment.ProcessorCount}): 여러 코어 {ms:0.00}ms/장 = {1000 / ms:0}fps (그중 계산 {evalMs / n:0.00}ms), 한 줄 {ms1:0.00}ms/장 = {1000 / ms1:0}fps");
        File.WriteAllText(Path.Combine(outDir, "fps.txt"), $"1280x720 multi {1000 / ms:0.0} fps ({ms:0.00} ms), single {1000 / ms1:0.0} fps ({ms1:0.00} ms), eval {evalMs / n:0.00} ms, verts {verts}, tris {tris}, cores {Environment.ProcessorCount}\n");
        Ok(1000 / ms >= 30, $"미리보기 속도 1280×720: {1000 / ms:0}fps (30 이상)");
        rt.Reset(); rt.Phys.Clear();
    }

    static void TestStudio(Document d, RigRuntime rt)
    {
        var player = new RigPlayer(rt);
        var data = VRig.BuildRig(player.LegacyParts(), player.W, player.H, null);
        data.Alt = player;
        using var form = Vtuber.TestStudio(data, d);
        form.TestManual.Yaw = 0.8; form.TestManual.Mouth = 0.9;
        form.TestFrame(); form.TestFrame();
        using var bmp = VRig.RenderBitmap(data, form.TestState, 0, 0.5, Color.FromArgb(0, 255, 0));
        int green = 0, other = 0;
        for (int y = 0; y < bmp.Height; y += 4) for (int x = 0; x < bmp.Width; x += 4) { var c = bmp.GetPixel(x, y); if (c.G > 250 && c.R < 5 && c.B < 5) green++; else other++; }
        bmp.Save(Path.Combine(outDir, "studio_frame.png"), ImageFormat.Png);
        Ok(other > 500 && green > 500 && Math.Abs(rt.Val("ParamAngleX") - 24) < 6, $"스튜디오: 메쉬 리그로 그림 (고개 0.8 → AngleX {rt.Val("ParamAngleX"):0.0}, 캐릭터 {other}·바탕 {green} 표본)");
        foreach (var p in data.Parts) p.Img?.Dispose();
        rt.Reset(); rt.Phys.Clear();
    }

    static void Previews(Document d, RigRuntime rt)
    {
        var rig = rt.Rig;
        int cw = 300, ch = 300;
        double s = cw / 560.0, ox = -232 * s, oy = -150 * s;     // 머리 쪽 확대
        var shots = new (string label, (string p, double v)[] set)[]
        {
            ("기본", new (string, double)[0]),
            ("AngleX -30", new[] { ("ParamAngleX", -30.0) }), ("AngleX +30", new[] { ("ParamAngleX", 30.0) }),
            ("AngleY +30", new[] { ("ParamAngleY", 30.0) }), ("AngleY -30", new[] { ("ParamAngleY", -30.0) }),
            ("AngleZ +30", new[] { ("ParamAngleZ", 30.0) }),
            ("눈 감기", new[] { ("ParamEyeLOpen", 0.0), ("ParamEyeROpen", 0.0) }), ("눈 웃음", new[] { ("ParamEyeLOpen", 0.0), ("ParamEyeROpen", 0.0), ("ParamEyeLSmile", 1.0), ("ParamEyeRSmile", 1.0), ("ParamMouthForm", 1.0) }),
            ("입 벌림", new[] { ("ParamMouthOpenY", 1.0) }), ("웃으며 말하기", new[] { ("ParamMouthOpenY", 0.6), ("ParamMouthForm", 1.0) }),
            ("눈동자 →↑", new[] { ("ParamEyeBallX", 1.0), ("ParamEyeBallY", 1.0) }), ("눈썹 화남", new[] { ("ParamBrowLForm", -1.0), ("ParamBrowRForm", -1.0), ("ParamBrowLY", -0.6), ("ParamBrowRY", -0.6) }),
            ("앞머리 흔들림", new[] { ("ParamHairFront", 1.0), ("ParamHairSide", -1.0), ("ParamHairBack", 1.0) }),
            ("몸 기울기", new[] { ("ParamBodyAngleZ", 10.0), ("ParamBodyAngleX", 10.0) }),
            ("조합", new[] { ("ParamAngleX", 25.0), ("ParamAngleY", 12.0), ("ParamAngleZ", -10.0), ("ParamEyeBallX", 0.8), ("ParamMouthOpenY", 0.5), ("ParamMouthForm", 0.8) }),
            ("조합 2", new[] { ("ParamAngleX", -22.0), ("ParamAngleY", -15.0), ("ParamAngleZ", 15.0), ("ParamEyeLOpen", 0.0), ("ParamEyeBallX", -0.8), ("ParamBrowLY", 1.0), ("ParamBrowRY", 1.0) }),
        };
        int cols = 4, rows = (shots.Length + cols - 1) / cols;
        using var sheet = new Bitmap(cols * cw, rows * (ch + 22));
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(235, 235, 240));
            using var fnt = new Font("Malgun Gothic", 10f);
            for (int i = 0; i < shots.Length; i++)
            {
                rt.Reset();
                foreach (var (p, v) in shots[i].set) rt.Set(p, v);
                var px = Render(rt, cw, ch, s, ox, oy);
                using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                System.Runtime.InteropServices.Marshal.Copy((int[])(object)px, 0, bd.Scan0, cw * ch);
                bmp.UnlockBits(bd);
                int x = i % cols * cw, y = i / cols * (ch + 22);
                g.DrawImageUnscaled(bmp, x, y + 22);
                g.DrawString(shots[i].label, fnt, Brushes.Black, x + 6, y + 3);
            }
        }
        sheet.Save(Path.Combine(outDir, "sweep_sheet.png"), ImageFormat.Png);
        // 메쉬·디포머 겹쳐 그리기
        rt.Reset(); rt.Set("ParamAngleX", 20); rt.Set("ParamAngleY", 10);
        int W = 768;
        double k = W / (double)d.W;
        var full = Render(rt, W, W, k);
        using (var bmp = new Bitmap(W, W, PixelFormat.Format32bppPArgb))
        {
            var bd = bmp.LockBits(new Rectangle(0, 0, W, W), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            System.Runtime.InteropServices.Marshal.Copy((int[])(object)full, 0, bd.Scan0, W * W);
            bmp.UnlockBits(bd);
            using var o = new Bitmap(W, W);
            using (var g = Graphics.FromImage(o))
            {
                g.Clear(Color.White); g.DrawImageUnscaled(bmp, 0, 0);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(Color.FromArgb(90, 30, 120, 255), 1);
                for (int i = 0; i < rig.Parts.Count; i++)
                {
                    if (rig.Parts[i].Role is not ("face" or "eyeWhite" or "mouth" or "hairFront" or "brow")) continue;
                    var pos = rt.PS[i].Pos; var T = rig.Parts[i].Mesh.T;
                    for (int t = 0; t + 2 < T.Length; t += 3)
                        g.DrawPolygon(pen, new[] { new PointF((float)(pos[T[t] * 2] * k), (float)(pos[T[t] * 2 + 1] * k)), new PointF((float)(pos[T[t + 1] * 2] * k), (float)(pos[T[t + 1] * 2 + 1] * k)), new PointF((float)(pos[T[t + 2] * 2] * k), (float)(pos[T[t + 2] * 2 + 1] * k)) });
                }
                int hi = rt.DefIndex("D_HeadWarp");
                var n = rt.WarpNodes(hi); var hd = rig.Def("D_HeadWarp");
                using var wp = new Pen(Color.FromArgb(200, 255, 120, 0), 1.5f);
                int C = hd.Cols + 1;
                for (int r = 0; r <= hd.Rows; r++) for (int c = 0; c <= hd.Cols; c++)
                {
                    int q = r * C + c;
                    var a = new PointF((float)(n[q * 2] * k), (float)(n[q * 2 + 1] * k));
                    if (c < hd.Cols) g.DrawLine(wp, a, new PointF((float)(n[(q + 1) * 2] * k), (float)(n[(q + 1) * 2 + 1] * k)));
                    if (r < hd.Rows) g.DrawLine(wp, a, new PointF((float)(n[(q + C) * 2] * k), (float)(n[(q + C) * 2 + 1] * k)));
                }
            }
            o.Save(Path.Combine(outDir, "mesh_overlay.png"), ImageFormat.Png);
        }
        rt.Reset();
        // 움직임 GIF (내보내기 기능 그대로)
        var sw = Stopwatch.StartNew();
        string gif = Path.Combine(outDir, "motion.gif");
        RigExport.Export(rt, "gif", gif, 360, 4, Color.White);
        Ok(File.Exists(gif) && new FileInfo(gif).Length > 10_000, $"움직임 GIF 내보내기 ({new FileInfo(gif).Length / 1024}KB, {sw.ElapsedMilliseconds}ms)");
        try
        {
            string mp4 = Path.Combine(outDir, "motion.mp4");
            RigExport.Export(rt, "mp4", mp4, 480, 2, Color.White);
            var mf = Media.Mp4Probe.Read(mp4, new[] { 10 });
            Ok(mf.Frames >= 55 && Math.Abs(mf.Duration - 2) < 0.2, $"움직임 MP4 내보내기 ({mf.W}×{mf.H}, {mf.Frames}장, {mf.Duration:0.00}초)");
        }
        catch (Exception e) { Ok(false, "움직임 MP4 내보내기: " + e.Message); }
        try
        {
            string webm = Path.Combine(outDir, "motion.webm");
            RigExport.Export(rt, "webm", webm, 320, 1, Color.White);
            Ok(new FileInfo(webm).Length > 2000, $"움직임 WebM 내보내기 ({new FileInfo(webm).Length / 1024}KB)");
        }
        catch (Exception e) { Ok(false, "움직임 WebM 내보내기: " + e.Message); }
        // 이어 붙인 고개 돌리기 띠 (판단용)
        using (var strip = new Bitmap(9 * 200, 230))
        {
            using var g = Graphics.FromImage(strip);
            g.Clear(Color.FromArgb(235, 235, 240));
            for (int i = 0; i < 9; i++)
            {
                rt.Reset(); rt.Set("ParamAngleX", -30 + i * 7.5); rt.Set("ParamAngleY", Math.Sin(i * 0.8) * 10);
                double ss = 200 / 560.0;
                var px = Render(rt, 200, 230, ss, -232 * ss, -120 * ss);
                using var bmp = new Bitmap(200, 230, PixelFormat.Format32bppPArgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, 200, 230), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                System.Runtime.InteropServices.Marshal.Copy((int[])(object)px, 0, bd.Scan0, 200 * 230);
                bmp.UnlockBits(bd);
                g.DrawImageUnscaled(bmp, i * 200, 0);
            }
            strip.Save(Path.Combine(outDir, "turn_strip.png"), ImageFormat.Png);
        }
        rt.Reset();
    }

    static void TestEditor(Document d, RigRuntime rt)
    {
        d.Extra["vtRig"] = rt.Rig.ToJson();
        try
        {
            using var f = new RigEditorForm(d);
            f.Size = new Size(1500, 900);
            f.CreateControl();
            f.PerformLayout();
            var c = f.TestCanvas;
            c.Size = new Size(860, 780);
            f.TestSelect(rt.Rig.Parts.First(p => p.Role == "face").Id);
            using (var bmp = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height)); bmp.Save(Path.Combine(outDir, "editor_canvas.png"), ImageFormat.Png); }
            // 창 모습 (COMICPAD_VRIG2_GUI=1 일 때만 실제로 창을 띄움 — 평소 시험은 창 없이)
            if (Environment.GetEnvironmentVariable("COMICPAD_VRIG2_GUI") == "1")
            {
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0);
                f.Show();
                for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(25); }
                f.TestSelectDef("D_HeadWarp");
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25); }
                using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(Path.Combine(outDir, "editor_window.png"), ImageFormat.Png); }
                f.TestSelect(rt.Rig.Parts.First(p => p.Role == "mouth").Id);
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25); }
                using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(Path.Combine(outDir, "editor_window_part.png"), ImageFormat.Png); }
                f.Hide();
            }
            // 자동 리그 → 되돌리기 → 같은 JSON
            string before = f.TestRig.ToJsonString();
            f.TestAutoRig();
            f.TestUndo();
            Ok(f.TestRig.ToJsonString() == before, "편집기: 자동 리그 → 되돌리기 → 처음과 같음");
        }
        catch (Exception e) { Ok(false, "편집기 만들기: " + e.Message); }
    }
}
