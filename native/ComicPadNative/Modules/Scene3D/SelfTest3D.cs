// 3D·버튜버 자체 점검 (창 없이): 정해진 결과가 나오는지 확인하고 그림을 outDir 에 남긴다.
//  - 인형을 그려 실루엣 픽셀(몸 안은 칠해지고 바깥은 비었는지), 선화·음영 레이어, 포즈를 바꾸면 팔 자리가 바뀌는지
//  - BVH 읽기 → 포즈 (팔을 든 동작이 인형 팔 각도로 옮겨지는지), 장면 JSON 저장/복원
//  - 버튜버: 레이어 이름 → 역할 짐작, 리그 그리기 (고개를 돌리면 앞머리가 움직이는지, 눈 깜빡임)
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Scene3D;

public static class SelfTest3D
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "OK   " : "FAIL ") + what); if (!ok) fails++; }

    static void Save(uint[] buf, int w, int h, string path)
    {
        var s = new Surface(w, h); s.Write(new Rect(0, 0, w, h), buf);
        using var bmp = CpadIO.ToBitmap(s, s.Full, 0xFFFFFFFF);
        bmp.Save(path, ImageFormat.Png);
    }

    public static int Run(string outDir)
    {
        fails = 0;
        Directory.CreateDirectory(outDir);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ---- 1) 정면 인형 실루엣 ----
        var sc = new Scene3();
        sc.Nodes.Add(new SceneNode { Kind = "mannequin", Name = "데생 인형", Color = "#e9dccf" });
        sc.Camera = new Camera3 { Yaw = 0, Pitch = 0, Dist = 4.2, Target = new[] { 0, 0.95, 0 }, Fov = 40 };
        int W = 300, H = 400;
        var col = Render3D.Render(sc, W, H, "color", sc.Style, 1, false, 1);
        Save(col, W, H, Path.Combine(outDir, "mannequin_color.png"));
        uint At(uint[] b, double x, double y) => b[(int)y * W + (int)x];
        var cm = Render3D.Camera(sc.Camera, (double)W / H);
        var headP = Render3D.Project(cm, new[] { 0, 1.72, 0.0 }, W, H).Value;
        var hipP = Render3D.Project(cm, new[] { 0, 0.98, 0.0 }, W, H).Value;
        var footGap = Render3D.Project(cm, new[] { 0, 0.3, 0.0 }, W, H).Value;     // 두 다리 사이
        Check((At(col, headP.x, headP.y) >> 24) == 255, $"머리 자리 칠해짐 ({headP.x:0},{headP.y:0})");
        Check((At(col, hipP.x, hipP.y) >> 24) == 255, "골반 자리 칠해짐");
        Check((At(col, 5, 5) >> 24) == 0 && (At(col, W - 5, H / 2) >> 24) == 0, "바깥(구석) 비어 있음");
        Check((At(col, footGap.x, footGap.y) >> 24) == 0, "두 다리 사이(정강이 높이 가운데) 비어 있음");
        uint hc = Px.Unpremul(At(col, headP.x, headP.y));
        Check(Math.Abs((int)((hc >> 16) & 255) - 0xe9) <= 1 && Math.Abs((int)(hc & 255) - 0xcf) <= 1, $"색 레이어 = 인형 색 #e9dccf (얻은 값 {Px.ToHex(hc)})");
        int filled = col.Count(c => (c >> 24) != 0);
        Check(filled > W * H * 0.04 && filled < W * H * 0.3, $"실루엣 넓이 적당함 ({filled * 100.0 / (W * H):0.0}%)");

        // ---- 2) 선화·음영 ----
        var line = Render3D.Render(sc, W, H, "line", sc.Style, 1, false, 2);
        Save(line, W, H, Path.Combine(outDir, "mannequin_line.png"));
        int lineN = line.Count(c => (c >> 24) > 128);
        Check(lineN > 300 && lineN < filled, $"선화 픽셀 있음 ({lineN})");
        // 실루엣 가장자리 근처에 선이 있어야 함: 머리 위쪽 바깥 경계
        int topY = -1; for (int y = 0; y < H; y++) if ((At(col, headP.x, y) >> 24) != 0) { topY = y; break; }
        bool edgeLine = false; for (int dy = -3; dy <= 3; dy++) if (topY + dy >= 0 && (At(line, headP.x, topY + dy) >> 24) > 100) edgeLine = true;
        Check(topY >= 0 && edgeLine, "머리 윤곽에 선이 그려짐");
        var shade = Render3D.Render(sc, W, H, "shade", sc.Style, 1, false, 1);
        Save(shade, W, H, Path.Combine(outDir, "mannequin_shade.png"));
        int shadeN = shade.Count(c => (c >> 24) > 0);
        Check(shadeN > 0 && shade.All(c => (c >> 24) == 0 || col.Length > 0), $"음영 레이어 픽셀 ({shadeN})");
        var prev = Render3D.Render(sc, W, H, "preview", sc.Style, 1, true, 1);
        Save(prev, W, H, Path.Combine(outDir, "mannequin_preview.png"));
        Check(prev.Count(c => (c >> 24) == 128) > 100, "미리보기에 바닥 격자 (반투명)");

        // ---- 2b) 셀 음영 계산 (바깥으로 감긴 상자): 빛(좌우 40°, 높이 45°) 쪽 윗면은 밝고 앞면은 그늘 ----
        {
            var bx = new Scene3 { Camera = new Camera3 { Yaw = 20, Pitch = 30, Dist = 3, Target = new[] { 0, 0.5, 0.0 }, Fov = 40 } };
            bx.Nodes.Add(new SceneNode { Kind = "prim", Prim = "box" }); bx.Nodes[0].BuildParts();
            var sh = Render3D.Render(bx, 200, 200, "shade", bx.Style, 1, false, 1);
            var bcm = Render3D.Camera(bx.Camera, 1);
            var top = Render3D.Project(bcm, new[] { 0, 1.0, 0 }, 200, 200).Value; var front = Render3D.Project(bcm, new[] { 0, 0.5, 0.5 }, 200, 200).Value;
            uint ta = sh[(int)top.y * 200 + (int)top.x] >> 24, fa = sh[(int)front.y * 200 + (int)front.x] >> 24;
            Check(ta == 0 && Math.Abs((int)fa - 128) <= 2, $"상자 음영: 윗면 그늘 알파 {ta} (0), 앞면 {fa} (128)");
        }

        // ---- 3) 포즈: 오른팔 들기 → 손 자리 ----
        var mats0 = Engine3D.MannequinMatrices(sc.Nodes[0].Pose, null);
        sc.Nodes[0].Pose = Engine3D.Poses.First(p => p.name == "손 들기").pose.Clone();
        var mats1 = Engine3D.MannequinMatrices(sc.Nodes[0].Pose, null);
        Check(mats1["handR"][13] > 1.7 && mats0["handR"][13] < 1.0, $"'손 들기' 포즈: 오른손 높이 {mats0["handR"][13]:0.00} → {mats1["handR"][13]:0.00}");
        var col2 = Render3D.Render(sc, W, H, "color", sc.Style, 1, false, 1);
        Save(col2, W, H, Path.Combine(outDir, "mannequin_raise.png"));
        var handUp = Render3D.Project(cm, new[] { mats1["handR"][12], mats1["handR"][13] - 0.07, mats1["handR"][14] }, W, H).Value;
        Check((At(col2, handUp.x, handUp.y) >> 24) == 255 && (At(col, handUp.x, handUp.y) >> 24) == 0, "든 손 자리가 새로 칠해짐");

        // ---- 4) BVH ----
        string bvh = MakeTestBvh();
        var r = Bvh.Poses(bvh);
        Check(r.Poses.Count == 3 && r.Matched >= 8, $"BVH 읽기: 포즈 {r.Poses.Count}개, 관절 {r.Matched}개 맞춤, {r.Fps:0.#}fps");
        var p0 = r.Poses[0]; var p2 = r.Poses[2];
        var m0 = Engine3D.MannequinMatrices(p0, null);
        Check(Math.Abs(m0["handR"][13] - mats0["handR"][13]) < 0.03 && Math.Abs(m0["handR"][12] - mats0["handR"][12]) < 0.05,
            $"BVH 0프레임 = 차렷 (오른손 자리 {m0["handR"][12]:0.00},{m0["handR"][13]:0.00} ≈ {mats0["handR"][12]:0.00},{mats0["handR"][13]:0.00}; 포즈 {string.Join(" ", p0.Select(k => k.Key + "[" + string.Join(",", k.Value) + "]"))})");
        var m2 = Engine3D.MannequinMatrices(p2, null);
        Check(m2["handR"][13] > m2["shoulderR"][13] + 0.2, $"BVH 2프레임: 오른손이 어깨 위로 (손 {m2["handR"][13]:0.00}, 어깨 {m2["shoulderR"][13]:0.00})");
        Check(m2["handL"][13] < 1.0, "BVH 2프레임: 왼손은 그대로 아래");

        // ---- 5) 장면 JSON 저장/복원 ----
        sc.Nodes.Add(new SceneNode { Kind = "prim", Prim = "box", Name = "상자", Pos = new[] { 1.0, 0, 0 } });
        sc.Nodes[^1].BuildParts();
        sc.Nodes[0].Bvh = new BvhInfo { Name = "t.bvh", Poses = r.Poses, Fps = r.Fps, Matched = r.Matched };
        var json = sc.ToJson();
        var back = Scene3.FromJson(JsonNode.Parse(json.ToJsonString()));
        Check(back.Nodes.Count == 2 && back.Nodes[1].Parts.Count == 1 && back.Nodes[0].Bvh?.Poses.Count == 3 && back.ToJson().ToJsonString() == json.ToJsonString(), "장면 JSON 저장 → 복원 → 같은 JSON");
        Check(json["nodes"][0]["pose"]["upperArmR"] is JsonArray && json["camera"]["target"] is JsonArray && json["style"]["out"]["color"] != null, "웹 판과 같은 키 (pose·camera.target·style.out)");

        // ---- 6) 만화에 넣기 (레이어·실행취소·.cpad 왕복) ----
        {
            App.History.Clear();
            var d = Document.New(360, 480);
            int n0 = d.Layers.Count;
            var sc2 = new Scene3 { Camera = sc.Camera.Clone() };
            sc2.Nodes.Add(new SceneNode { Kind = "mannequin", Name = "데생 인형", Color = "#e9dccf" });
            Scene3DWindow.InsertInto(d, null, sc2);
            var f = d.Layers.FirstOrDefault(l => l.IsFolder && l.Name == "3D");
            var kids = f == null ? new List<Layer>() : d.ChildrenOf(f);
            Check(f != null && f.Meta?["scene3d"]?["nodes"] is JsonArray && d.Layers[^1] == f && d.ActiveId == f.Id, "\"3D\" 폴더 (맨 위, meta.scene3d)");
            Check(kids.Select(k => k.Name + ":" + k.Blend).SequenceEqual(new[] { "3D 색:source-over", "3D 음영:multiply", "3D 선화:source-over" }), "색·음영(곱하기)·선화 레이어 순서: " + string.Join(", ", kids.Select(k => k.Name)));
            Check(kids.All(k => !k.Px.Bounds().IsEmpty), "세 레이어 모두 그림 있음");
            using (var bmp = CpadIO.ToBitmap(App.Comp.Flatten(d), new Rect(0, 0, d.W, d.H))) bmp.Save(Path.Combine(outDir, "insert_flat.png"), ImageFormat.Png);
            App.History.Undo(d);
            Check(d.Layers.Count == n0, "실행취소 → 레이어 원래대로");
            App.History.Redo(d);
            Check(d.Layers.Count == n0 + 4, "다시실행 → 3D 폴더 다시");
            // 이어서 고치기: 같은 폴더 안을 바꿈
            sc2.Nodes[0].Pose = Engine3D.Poses.First(p => p.name == "T 포즈").pose.Clone();
            Scene3DWindow.InsertInto(d, f, sc2);
            Check(d.Layers.Count == n0 + 4 && d.ChildrenOf(f).Count == 3 && f.Meta["scene3d"]["nodes"][0]["pose"]["upperArmL"] != null, "같은 폴더에 다시 넣기 (안의 레이어만 바뀜)");
            var vps = Scene3DWindow.VanishingPoints(sc2, null, d.W, d.H);
            var tilted = new Scene3 { Camera = new Camera3 { Yaw = 35, Pitch = 20, Dist = 4, Fov = 40 } };
            var vps2 = Scene3DWindow.VanishingPoints(tilted, null, d.W, d.H);
            Check(vps.Count == 1 && Math.Abs(vps[0].x - d.W / 2.0) < 1 && vps2.Count == 3, $"퍼스 자 소실점: 정면 {vps.Count}점 (가운데), 비스듬히 {vps2.Count}점");
            var d2 = CpadIO.FromJson(JsonNode.Parse(CpadIO.ToJson(d).ToJsonString()).AsObject());
            var f2 = d2.ById(f.Id);
            var sc3 = Scene3.FromJson(f2?.Meta?["scene3d"]);
            Check(sc3.Nodes.Count == 1 && sc3.Nodes[0].Pose["upperArmL"][2] == 88, ".cpad 저장 → 열기 후 장면 복원");
            App.History.Clear();
        }

        // ---- 7) 원고 크기 (A4 350dpi) 합성 시간 ----
        {
            var big = Document.New(2480, 3508);
            var sc4 = new Scene3 { Camera = new Camera3 { Yaw = 30, Pitch = 10, Dist = 4.2, Fov = 40 } };
            sc4.Nodes.Add(new SceneNode { Kind = "mannequin", Color = "#e9dccf", Pose = Engine3D.Poses.First(p => p.name == "달리기").pose.Clone() });
            var t0 = sw.ElapsedMilliseconds;
            Scene3DWindow.InsertInto(big, null, sc4);
            long ms = sw.ElapsedMilliseconds - t0;
            Check(ms < 30000 && big.Layers.Count == 5, $"A4 350dpi 원고에 3D 합성 (색·음영·선화, 2배 표본) {ms}ms");
            var lineL = big.Layers.First(l => l.Name == "3D 선화");
            var lb = lineL.Px.Bounds();
            using (var bmp = CpadIO.ToBitmap(lineL.Px, lb, 0xFFFFFFFF)) bmp.Save(Path.Combine(outDir, "big_line_crop.png"), ImageFormat.Png);
            App.History.Clear();
        }

        // ---- 8) 버튜버 ----
        fails += VtuberTest(outDir);

        Console.WriteLine($"시간 {sw.ElapsedMilliseconds}ms, 실패 {fails}");
        return fails;
    }

    /// <summary>창 점검: 3D 창·버튜버 스튜디오를 잠깐 띄워 마우스 조작을 흉내 내고 화면을 그림으로 남긴다 (설정 파일은 건드리지 않음)</summary>
    public static int GuiSmoke(string outDir)
    {
        fails = 0;
        Directory.CreateDirectory(outDir);
        var d = Document.New(600, 800);
        App.Doc = d;
        var sc = new Scene3();
        sc.Nodes.Add(new SceneNode { Kind = "mannequin", Name = "데생 인형", Color = "#e9dccf" });
        using (var w = new Scene3DWindow(d, null, sc) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40), Size = new Size(1300, 900), Testing = true })
        {
            w.Show(); Application.DoEvents(); w.Refresh(); Application.DoEvents();
            var fr = w.TestFrame;
            Check(fr.Width > 100 && fr.Height > 100, $"3D 창 그림판 {fr.Width}x{fr.Height}");
            // 시점 돌리기
            double yaw0 = sc.Camera.Yaw;
            w.TestDrag(new Point(fr.X + 100, fr.Y + 100), new Point(fr.X + 200, fr.Y + 100));
            Check(Math.Abs(sc.Camera.Yaw - (yaw0 - 40)) < 0.01, $"끌기 → 시점 회전 (yaw {yaw0} → {sc.Camera.Yaw})");
            sc.Camera.Yaw = 0; sc.Camera.Pitch = 0;
            // 포즈 모드: 오른손 관절 점을 눌러 고르고 끌기
            w.TestMode = "pose"; w.TestJoint = "hips"; Application.DoEvents(); w.Refresh();
            var hand = w.TestJoints().First(j => j.id == "foreArmR").pt;
            var p0 = new Point((int)hand.x, (int)hand.y);
            w.TestDrag(p0, new Point(p0.X - 40, p0.Y - 60));
            Check(w.TestJoint == "foreArmR" && sc.Nodes[0].Pose.TryGetValue("foreArmR", out var r) && Math.Abs(r[0] + 30) < 0.01 && Math.Abs(r[2] - 20) < 0.01, $"관절 점 눌러 고르기 + 끌어 돌리기 ({w.TestJoint} = [{string.Join(",", sc.Nodes[0].Pose.GetValueOrDefault("foreArmR") ?? new double[3])}])");
            // 손발 끌기 (IK): 오른손을 위로 끌면 손이 올라감
            sc.Nodes[0].Pose = new Engine3D.Pose();
            w.TestMode = "ik"; w.TestJoint = "handR"; w.Refresh();
            double hy0 = Engine3D.MannequinMatrices(sc.Nodes[0].Pose, null)["handR"][13];
            var hp = w.TestJoints().First(j => j.id == "handR").pt;
            w.TestDrag(new Point((int)hp.x, (int)hp.y), new Point((int)hp.x, (int)hp.y - 120));
            double hy1 = Engine3D.MannequinMatrices(sc.Nodes[0].Pose, null)["handR"][13];
            Check(hy1 > hy0 + 0.2, $"손발 끌기: 오른손 높이 {hy0:0.00} → {hy1:0.00}");
            // BVH 파일 열기
            string bvhPath = Path.Combine(outDir, "test.bvh"); File.WriteAllText(bvhPath, MakeTestBvh());
            w.TestMode = "orbit";
            w.TestLoadBvh(bvhPath);
            Check(sc.Nodes[0].Bvh?.Poses.Count == 3, "창에서 BVH 열기 → 노드에 동작");
            sc.Nodes[0].Pose = Engine3D.Poses.First(p => p.name == "달리기").pose.Clone();
            sc.Camera.Yaw = 30; sc.Camera.Pitch = 10;
            w.TestMode = "pose"; w.TestJoint = "upperArmL";
            Application.DoEvents(); w.Refresh();
            using var bmp = new Bitmap(w.Width, w.Height);
            w.DrawToBitmap(bmp, new Rectangle(0, 0, w.Width, w.Height));
            bmp.Save(Path.Combine(outDir, "gui_3d.png"), ImageFormat.Png);
        }
        // 버튜버 스튜디오
        var vd = new Document(200, 240);
        var face = new Layer(vd, "얼굴"); var eye = new Layer(vd, "눈"); var expr = new Layer(vd, "표정 웃음");
        using (var b = new Bitmap(200, 240)) { using (var g = Graphics.FromImage(b)) g.FillEllipse(Brushes.Bisque, 50, 50, 100, 120); CpadIO.DrawBitmap(face.Px, b, 0, 0); }
        using (var b = new Bitmap(200, 240)) { using (var g = Graphics.FromImage(b)) { g.FillEllipse(Brushes.Black, 75, 100, 12, 16); g.FillEllipse(Brushes.Black, 113, 100, 12, 16); } CpadIO.DrawBitmap(eye.Px, b, 0, 0); }
        using (var b = new Bitmap(200, 240)) { using (var g = Graphics.FromImage(b)) g.FillEllipse(Brushes.HotPink, 60, 120, 20, 10); CpadIO.DrawBitmap(expr.Px, b, 0, 0); }
        vd.Layers.AddRange(new[] { face, eye, expr });
        vd.Extra["vtuber"] = new JsonObject { ["parts"] = new JsonObject { [face.Id] = new JsonObject { ["role"] = "head" }, [eye.Id] = new JsonObject { ["role"] = "eyesOpen" }, [expr.Id] = new JsonObject { ["role"] = "expr", ["key"] = 2 } } };
        var rig = VRig.FromDoc(vd);
        using (var st = Vtuber.TestStudio(rig, vd))
        {
            st.StartPosition = FormStartPosition.Manual; st.Location = new Point(60, 60);
            st.Show(); Application.DoEvents();
            st.TestManual.Yaw = 0.8;
            for (int i = 0; i < 20; i++) { st.TestFrame(); Application.DoEvents(); }
            Check(st.TestState.Yaw > 0.5, $"스튜디오: 고개 슬라이더 값이 부드럽게 따라감 (yaw {st.TestState.Yaw:0.00})");
            st.TestKey(Keys.D2);
            Check(st.TestExpr == 2, "스튜디오: 숫자 키 2 → 표정 2");
            st.TestKey(Keys.D5);
            Check(st.TestExpr == 2, "스튜디오: 없는 표정 키 5 는 무시");
            st.TestPopup(); for (int i = 0; i < 3; i++) { st.TestFrame(); Application.DoEvents(); }
            using var bmp = new Bitmap(st.Width, st.Height);
            st.DrawToBitmap(bmp, new Rectangle(0, 0, st.Width, st.Height));
            bmp.Save(Path.Combine(outDir, "gui_vtuber.png"), ImageFormat.Png);
            st.Close();
            Check(vd.Extra["vtuber"]?["opts"]?["motion"] != null, "스튜디오 닫으면 조절 값이 doc.vtuber.opts 에");
        }
        Console.WriteLine($"창 점검 실패 {fails}");
        return fails;
    }

    /// <summary>시험용 BVH: 0프레임 차렷, 1 반쯤, 2 오른팔을 위로</summary>
    public static string MakeTestBvh()
    {
        string J(string name, string off, string inner) => $"JOINT {name}\n{{\nOFFSET {off}\nCHANNELS 3 Zrotation Xrotation Yrotation\n{inner}}}\n";
        string End(string off) => $"End Site\n{{\nOFFSET {off}\n}}\n";
        string arm(string s, int k) => J($"{s}Shoulder", $"{0.03 * k} 0.1 0", J($"{s}Arm", $"{0.14 * k} 0 0", J($"{s}ForeArm", "0 -0.28 0", J($"{s}Hand", "0 -0.25 0", End("0 -0.08 0")))));
        string leg(string s, int k) => J($"{s}UpLeg", $"{0.09 * k} -0.05 0", J($"{s}Leg", "0 -0.43 0", J($"{s}Foot", "0 -0.42 0", End("0 -0.05 0.1"))));
        string hier = "HIERARCHY\nROOT Hips\n{\nOFFSET 0 0 0\nCHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation\n" +
            J("Spine", "0 0.1 0", J("Spine1", "0 0.2 0", J("Neck", "0 0.2 0", J("Head", "0 0.1 0", End("0 0.15 0"))) + arm("Left", 1) + arm("Right", -1))) +
            leg("Left", 1) + leg("Right", -1) + "}\n";
        int ch = 6 + 3 * (4 + 8 + 6);
        string Frame(double rz) { var v = new double[ch]; v[1] = 0.98; int rightArmZ = 6 + 3 * 9; /* Hips 6 + Spine·Spine1·Neck·Head·왼쪽 4 마디·RightShoulder 각 3 */ v[rightArmZ] = rz; return string.Join(" ", v.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
        return hier + "MOTION\nFrames: 3\nFrame Time: 0.0333333\n" + Frame(0) + "\n" + Frame(-80) + "\n" + Frame(-160) + "\n";
    }

    static int VtuberTest(string outDir)
    {
        int f0 = fails;
        Check(VRig.GuessRole("앞머리") == "hairFront" && VRig.GuessRole("눈 감음") == "eyesClosed" && VRig.GuessRole("눈") == "eyesOpen" && VRig.GuessRole("입_ㅏ") == "mouthA"
              && VRig.GuessRole("mouth open") == "mouthOpen" && VRig.GuessRole("배경") == "none" && VRig.GuessRole("얼굴") == "head" && VRig.GuessRole("PNG 말할 때") == "pngTalk", "레이어 이름 → 역할 짐작");
        // 그림: 얼굴(원) + 눈(뜸/감음) + 앞머리 막대
        int W = 200, H = 240;
        var d = new Document(W, H);
        Layer L(string name, Action<Graphics> draw)
        {
            var l = new Layer(d, name);
            using var bmp = new Bitmap(W, H);
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); draw(g); }
            CpadIO.DrawBitmap(l.Px, bmp, 0, 0);
            d.Layers.Add(l);
            return l;
        }
        L("몸", g => g.FillRectangle(Brushes.SteelBlue, 60, 170, 80, 70));
        L("얼굴", g => g.FillEllipse(Brushes.Bisque, 50, 50, 100, 120));
        L("눈", g => { g.FillEllipse(Brushes.Black, 75, 100, 12, 16); g.FillEllipse(Brushes.Black, 113, 100, 12, 16); });
        L("눈 감음", g => { g.FillRectangle(Brushes.Black, 73, 108, 16, 3); g.FillRectangle(Brushes.Black, 111, 108, 16, 3); });
        L("입 닫힘", g => g.FillRectangle(Brushes.DarkRed, 90, 140, 20, 3));
        L("입 벌림", g => g.FillRectangle(Brushes.Green, 90, 135, 20, 15));
        L("앞머리", g => g.FillRectangle(Brushes.SaddleBrown, 50, 40, 100, 30));
        var cfg = new JsonObject();
        foreach (var l in d.Layers) cfg[l.Id] = new JsonObject { ["role"] = VRig.GuessRole(l.Name) };
        d.Extra["vtuber"] = new JsonObject { ["parts"] = cfg };
        var rig = VRig.FromDoc(d);
        Check(rig.Parts.Count == 7 && rig.Parts.Any(p => p.Role == "eyesClosed"), $"문서 → 리그 (부위 {rig.Parts.Count}개)");
        uint[] Draw(VState st, double sway, string name)
        {
            using var bmp = VRig.RenderBitmap(rig, st, sway, 1, null);
            var buf = Modules.AI.AiRuntime.ReadArgb(bmp);
            using (var ms = File.Create(Path.Combine(outDir, name))) bmp.Save(ms, ImageFormat.Png);
            return buf;
        }
        var s0 = VState.Neutral();
        var a = Draw(s0, 0, "vt_neutral.png");
        uint Pix(uint[] b, int x, int y) => b[y * W + x];
        Check((Pix(a, 80, 104) >> 24) > 200 && (Pix(a, 80, 104) & 0xFFFFFF) < 0x202020, "눈 뜸: 눈 자리 검정");
        var sb = VState.Neutral(); sb.BlinkL = sb.BlinkR = 1;
        var b = Draw(sb, 0, "vt_blink.png");
        Check((Pix(b, 80, 104) & 0xFFFFFF) > 0x808080 && (Pix(b, 80, 109) & 0xFFFFFF) < 0x202020, "눈 감음: 뜬 눈 대신 감은 눈 선");
        var sy = VState.Neutral(); sy.Yaw = 1;
        var c = Draw(sy, 0, "vt_yaw.png");
        int Left(uint[] buf, int y) { for (int x = 0; x < W; x++) if ((Pix(buf, x, y) >> 24) > 128 && ((Pix(buf, x, y) >> 16) & 255) < 0xA0) return x; return -1; }
        int h0 = Left(a, 55), h1 = Left(c, 55);
        Check(h1 > h0 + 2, $"고개 오른쪽 → 앞머리가 오른쪽으로 (x {h0} → {h1})");
        var sm = VState.Neutral(); sm.Mouth = 0.8;
        var mo = Draw(sm, 0, "vt_mouth.png");
        Check(((Pix(a, 100, 141) >> 8) & 255) < 0x30 && ((Pix(a, 100, 141) >> 16) & 255) > 0x60 && ((Pix(mo, 100, 145) >> 8) & 255) > 0x60 && ((Pix(mo, 100, 145) >> 16) & 255) < 0x30, "입: 닫힘(빨강) → 벌림(초록)");
        // PNG 버튜버: 말하면 그림이 바뀜
        var pd = new Document(40, 60); d = pd;
        W = 40; H = 60;
        var li = L("png 기본 idle", g => g.FillRectangle(Brushes.Blue, 0, 0, 40, 40));
        var lt = L("png 말 talk", g => g.FillRectangle(Brushes.Red, 0, 0, 40, 40));
        pd.Extra["vtuber"] = new JsonObject { ["parts"] = new JsonObject { [li.Id] = new JsonObject { ["role"] = VRig.GuessRole(li.Name) }, [lt.Id] = new JsonObject { ["role"] = VRig.GuessRole(lt.Name) } } };
        var prig = VRig.FromDoc(pd);
        using (var b1 = VRig.RenderBitmap(prig, VState.Neutral(), 0, 1, null))
        {
            var talk = VState.Neutral(); talk.Talk = 1;
            using var b2 = VRig.RenderBitmap(prig, talk, 0, 1, null);
            Check(prig.Png && (b1.GetPixel(20, 20).B > 200) && (b2.GetPixel(20, 20).R > 200), "PNG 버튜버: 말할 때 그림이 바뀜");
        }
        W = 200; H = 240;
        // 숨쉬기: 시간에 따라 몸 높이가 바뀜
        var t1 = VState.Neutral(); t1.T = 0.9;
        var e = Draw(t1, 0, "vt_breath.png");
        Check(!a.SequenceEqual(e), "숨쉬기 (시간에 따라 그림이 바뀜)");
        // 머리카락 용수철: 고개를 휙 돌리면 흔들림 값이 생기고 결국 0 으로
        var ph = new VPhysics(); var st = VState.Neutral();
        double maxA = 0; for (int i = 0; i < 10; i++) { st.Yaw = i < 3 ? i * 0.3 : 0.6; maxA = Math.Max(maxA, Math.Abs(VRig.StepPhysics(ph, st, 1 / 60.0))); }
        for (int i = 0; i < 600; i++) VRig.StepPhysics(ph, st, 1 / 60.0);
        Check(maxA > 0.01 && Math.Abs(ph.A) < 0.01, $"머리카락 용수철 (최대 {maxA:0.000}, 나중 {ph.A:0.0000})");
        Check(VRig.AutoBlink(0.05, 0) == 1 && VRig.AutoBlink(1, 0) == 0, "자동 깜빡임");
        // .cpvt 저장 → 읽기
        var file = VRig.ToFile(rig, "시험");
        var rig2 = VRig.FromFile(JsonNode.Parse(file.ToJsonString()));
        Check(rig2.Parts.Count == rig.Parts.Count && rig2.W == rig.W && (string)file["format"] == "comicpad-vtuber", ".cpvt 저장 → 읽기");
        return fails - f0;
    }
}
