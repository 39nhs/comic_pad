// 3D 소재 창 (웹 판 ui/scene3d.js 이식): 데생 인형/도형/3D 모델을 배치하고 포즈를 잡아 만화 레이어(색·음영·선화)로 합성.
//  - 시점: 왼쪽 끌기 회전, 오른쪽·가운데 끌기(또는 Shift) 이동, 휠 확대
//  - 포즈: "포즈" 모드에서 관절 점을 눌러 고르고 끌면 그 관절이 돌아감, "손발 끌기"(IK)는 손·발을 끌면 팔꿈치·무릎이 따라 굽혀짐
//  - 만화에 넣기: 원고 크기 "3D" 폴더(meta.scene3d = 장면 JSON)에 색 · 음영(곱하기) · 선화 레이어 (실행취소 가능)
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;
using Pose = ComicPadNative.Modules.Scene3D.Engine3D.Pose;

namespace ComicPadNative.Modules.Scene3D;

public sealed class Scene3DWindow : Form
{
    static Scene3DWindow current;
    static readonly Camera3 DefaultCamera = new();

    readonly Document doc;
    readonly Layer folder;
    readonly Scene3 scene;
    string sel;
    string joint = "upperArmR";
    string mode = "orbit";
    bool done;

    readonly ViewBox view;
    readonly Panel sideHost;
    FlowLayoutPanel side;
    readonly Ui3 ui = new();
    readonly Label statusLbl;
    ComboBox jointCombo; (string id, string text)[] jointItems;
    Bitmap bgBmp;

    Camera3 Cam => scene.Camera;
    SceneNode SelNode => scene.Nodes.FirstOrDefault(n => n.Id == sel);

    // ---------------------------------------------------------------- 열기
    /// <summary>메뉴: 지금 레이어(또는 그 부모 폴더)에 3D 장면이 있으면 이어서 고치기</summary>
    public static void OpenFromMenu()
    {
        var d = App.Doc;
        if (d == null) return;
        if (current != null && !current.IsDisposed) { current.Activate(); return; }
        var a = d.Active;
        Layer f = a?.Meta?["scene3d"] is JsonObject ? a : null;
        if (f == null && a != null) { var p = d.ById(d.ParentOf(a)); if (p?.Meta?["scene3d"] is JsonObject) f = p; }
        JsonNode json = f?.Meta?["scene3d"] ?? App.Settings["last3D"];
        var scene = Scene3.FromJson(json, App.Toast);
        if (json == null) scene = new Scene3();
        if (scene.Nodes.Count == 0) scene.Nodes.Add(new SceneNode { Kind = "mannequin", Name = "데생 인형", Color = "#e9dccf" });
        using var w = new Scene3DWindow(d, f, scene);
        current = w;
        try { w.ShowDialog(App.Main); }
        finally { current = null; }
    }

    internal Scene3DWindow(Document d, Layer f, Scene3 s)
    {
        doc = d; folder = f; scene = s;
        sel = scene.Nodes.FirstOrDefault()?.Id;
        Text = "3D 소재";
        BackColor = Opt.Bg; ForeColor = Opt.Fg;
        Font = new Font("Malgun Gothic", 9f);
        StartPosition = FormStartPosition.CenterParent;
        var wa = Screen.FromControl(App.Main ?? this).WorkingArea;
        Size = new Size(Math.Min(1500, wa.Width - 40), Math.Min(980, wa.Height - 40));
        MinimumSize = new Size(800, 560);
        KeyPreview = true;
        AllowDrop = true;

        var head = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Opt.Bg2, Padding = new Padding(8, 6, 8, 4), WrapContents = false };
        head.Controls.Add(new Label { Text = "3D 소재", AutoSize = true, Font = Ui3.Bold, ForeColor = Opt.Fg, Margin = new Padding(0, 5, 12, 0) });
        head.Controls.Add(new Label { Text = "드래그: 시점 회전 · 오른쪽/가운데 드래그·Shift: 이동 · 휠: 확대", AutoSize = true, ForeColor = Opt.Muted, Margin = new Padding(0, 6, 16, 0) });
        head.Controls.Add(Ui3.Button("만화에 넣기", Insert, true));
        head.Controls.Add(Ui3.Button("닫기", Close));
        statusLbl = new Label { AutoSize = true, ForeColor = Opt.Accent, Margin = new Padding(12, 6, 0, 0) };
        head.Controls.Add(statusLbl);

        sideHost = Ui3.Scroller(out side);
        sideHost.Dock = DockStyle.Right; sideHost.Width = Ui3.W + 60;
        view = new ViewBox(this) { Dock = DockStyle.Fill };
        Controls.Add(view);
        Controls.Add(sideHost);
        Controls.Add(head);

        MakeBg();
        RenderSide();
        FormClosing += OnClosingWin;
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) => DropFiles(e.Data?.GetData(DataFormats.FileDrop) as string[]);
        view.AllowDrop = true;
        view.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        view.DragDrop += (_, e) => DropFiles(e.Data?.GetData(DataFormats.FileDrop) as string[]);
    }

    protected override void Dispose(bool disposing) { if (disposing) bgBmp?.Dispose(); base.Dispose(disposing); }

    void Status(string t) { statusLbl.Text = t; statusLbl.Refresh(); }

    /// <summary>원고 비치기용 작은 미리보기</summary>
    void MakeBg()
    {
        try
        {
            double s = Math.Min(1, 900.0 / Math.Max(doc.W, doc.H));
            int w = Math.Max(1, (int)Math.Round(doc.W * s)), h = Math.Max(1, (int)Math.Round(doc.H * s));
            var buf = App.Comp.Render(doc, new Rect(0, 0, doc.W, doc.H), new RenderOptions { ExcludeDraft = true });
            var surf = new Surface(doc.W, doc.H); surf.Write(surf.Full, buf);
            using var full = CpadIO.ToBitmap(surf, surf.Full, 0xFFFFFFFF);
            bgBmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bgBmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, w, h);
        }
        catch { bgBmp = null; }
    }

    void Draw() => view.Redraw();

    // ---- 자체 점검용 (SelfTest3D) ----
    internal string TestMode { get => mode; set { mode = value; RenderSide(); Draw(); } }
    internal string TestJoint { get => joint; set { joint = value; ui.SyncAll(); } }
    internal Scene3 TestScene => scene;
    internal Rectangle TestFrame => view.FrameRect;
    internal IEnumerable<(string id, (double x, double y) pt)> TestJoints() => SelNode == null ? Enumerable.Empty<(string, (double, double))>() : JointScreen(SelNode);
    internal void TestDrag(Point a, Point b, MouseButtons btn = MouseButtons.Left) => view.Sim(a, b, btn);
    internal Point TestViewOrigin => view.Location;
    internal void TestLoadBvh(string path) => LoadBVHFile(SelNode, path);
    void SyncSliders() { ui.SyncAll(); Draw(); }

    // ---------------------------------------------------------------- 그림판 (미리보기 + 조작)
    sealed class ViewBox : Control
    {
        readonly Scene3DWindow w;
        Bitmap frame; bool dirty = true; Size lastSize;
        public Rectangle FrameRect;
        Point last; MouseButtons btn; bool down;
        double[] ikTarget;

        public ViewBox(Scene3DWindow w) { this.w = w; DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true); BackColor = Color.FromArgb(36, 36, 40); }
        protected override void Dispose(bool disposing) { if (disposing) frame?.Dispose(); base.Dispose(disposing); }
        public void Redraw() { dirty = true; Invalidate(); }
        /// <summary>자체 점검용: 마우스 끌기 흉내</summary>
        internal void Sim(Point a, Point b, MouseButtons button)
        {
            OnMouseDown(new MouseEventArgs(button, 1, a.X, a.Y, 0));
            for (int i = 1; i <= 8; i++) OnMouseMove(new MouseEventArgs(button, 0, a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8, 0));
            OnMouseUp(new MouseEventArgs(button, 1, b.X, b.Y, 0));
        }

        void Layout0()
        {
            double a = (double)w.doc.W / w.doc.H;
            double fw = Width - 16, fh = fw / a;
            if (fh > Height - 16) { fh = Height - 16; fw = fh * a; }
            int iw = Math.Max(50, (int)fw), ih = Math.Max(50, (int)fh);
            FrameRect = new Rectangle((Width - iw) / 2, (Height - ih) / 2, iw, ih);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Layout0();
            var fr = FrameRect;
            int pw = Math.Min(2048, fr.Width), ph = Math.Min(2048, fr.Height);
            if (dirty || frame == null || lastSize != new Size(pw, ph))
            {
                dirty = false; lastSize = new Size(pw, ph);
                try
                {
                    var buf = Render3D.Render(w.scene, pw, ph, "preview", w.scene.Style, (double)pw / w.doc.W, w.scene.Style.Grid, 1);
                    frame?.Dispose();
                    frame = ToBitmap(buf, pw, ph);
                }
                catch (Exception ex) { w.Status("3D 그리기 오류: " + ex.Message); }
            }
            if (w.scene.Style.BgDoc && w.bgBmp != null) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(w.bgBmp, fr); }
            else using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, fr);
            if (frame != null) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(frame, fr); }
            using (var pen = new Pen(Color.FromArgb(90, 90, 100))) g.DrawRectangle(pen, fr);
            // 관절 점 (포즈·손발 끌기 모드)
            var n = w.SelNode;
            if (n != null && (w.mode == "pose" || w.mode == "ik") && (n.Kind == "mannequin" || n.Kind == "hand"))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var (id, pt) in w.JointScreen(n))
                {
                    bool on = id == w.joint;
                    if (w.mode == "ik" && n.Kind == "mannequin" && ChainFor(id) == null) continue;
                    float r = on ? 6 : 4;
                    using var br = new SolidBrush(on ? Opt.Accent : Color.FromArgb(200, 70, 140, 255));
                    g.FillEllipse(br, (float)pt.x - r, (float)pt.y - r, r * 2, r * 2);
                    g.DrawEllipse(Pens.White, (float)pt.x - r, (float)pt.y - r, r * 2, r * 2);
                }
            }
        }

        static unsafe Bitmap ToBitmap(uint[] buf, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy((int[])(object)buf, y * w, bd.Scan0 + y * bd.Stride, w); }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            down = true; btn = e.Button; last = e.Location; Capture = true;
            var n = w.SelNode;
            if (e.Button == MouseButtons.Left && n != null && (w.mode == "pose" || w.mode == "ik") && (n.Kind == "mannequin" || n.Kind == "hand"))
            {
                // 관절 점을 눌렀으면 그 관절을 고름
                string best = null; double bd = 14 * 14;
                foreach (var (id, pt) in w.JointScreen(n))
                {
                    if (w.mode == "ik" && n.Kind == "mannequin" && ChainFor(id) == null) continue;
                    double d2 = (pt.x - e.X) * (pt.x - e.X) + (pt.y - e.Y) * (pt.y - e.Y);
                    if (d2 < bd) { bd = d2; best = id; }
                }
                if (best != null && best != w.joint) { w.joint = best; w.ui.SyncAll(); Invalidate(); }
            }
        }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Capture = false; ikTarget = null; }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var c = w.Cam;
            c.Dist = Math.Clamp(c.Dist * (e.Delta < 0 ? 1.1 : 1 / 1.1), 0.3, 60);
            w.SyncSliders();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!down) return;
            double dx = e.X - last.X, dy = e.Y - last.Y;
            last = e.Location;
            if (dx == 0 && dy == 0) return;
            var cam = w.Cam; var n = w.SelNode;
            bool panBtn = btn == MouseButtons.Middle || btn == MouseButtons.Right;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (w.mode == "orbit" || n == null || panBtn)
            {
                if (panBtn || shift) w.Pan(dx, dy);
                else { cam.Yaw -= dx * 0.4; cam.Pitch = Math.Clamp(cam.Pitch + dy * 0.3, -89, 89); }
            }
            else if (w.mode == "move")
            {
                double k = cam.Dist * 0.0018; var (right, fwd) = w.CamAxes();
                if (shift) n.Pos[1] -= dy * k;
                else { n.Pos[0] += (right[0] * dx - fwd[0] * dy) * k; n.Pos[2] += (right[2] * dx - fwd[2] * dy) * k; }
            }
            else if (w.mode == "turn")
            {
                n.Rot[1] += dx * 0.5; n.Rot[0] = Math.Clamp(n.Rot[0] + dy * 0.5, -180, 180);
            }
            else if (w.mode == "ik" && n.Kind == "mannequin" && ChainFor(w.joint) is Chain ch)
            {
                double k = cam.Dist * 0.0018; var (right, _) = w.CamAxes(); var up = w.CamUp();
                ikTarget ??= EndPos(n, n.Pose, ch.End);
                for (int i = 0; i < 3; i++) ikTarget[i] += right[i] * dx * k - up[i] * dy * k;
                SolveIK(n, ch, ikTarget);
            }
            else if (w.mode == "pose" && (n.Kind == "mannequin" || n.Kind == "hand"))
            {
                if (!n.Pose.TryGetValue(w.joint, out var r)) n.Pose[w.joint] = r = new double[3];
                r[2] = Math.Clamp(r[2] - dx * 0.5, -180, 180); r[0] = Math.Clamp(r[0] + dy * 0.5, -180, 180);
            }
            w.SyncSliders();
        }
    }

    /// <summary>고른 물체의 관절 화면 위치 (그림판 좌표)</summary>
    IEnumerable<(string id, (double x, double y) pt)> JointScreen(SceneNode n)
    {
        var fr = view.FrameRect;
        if (fr.Width <= 0) yield break;
        var cm = Render3D.Camera(Cam, (double)fr.Width / fr.Height);
        var bas = M3.Trs(n.Pos, n.Rot, n.Scale);
        Dictionary<string, double[]> world;
        if (n.Kind == "hand") world = Engine3D.HandMatrices(n.Pose, n.Side ?? 1);
        else { world = new(); Engine3D.MannequinMatrices(n.Pose, n.Body, world); }
        foreach (var (id, m) in world)
        {
            var p = M3.Point(bas, m[12], m[13], m[14]);
            var s = Render3D.Project(cm, p, fr.Width, fr.Height);
            if (s != null) yield return (id, (s.Value.x + fr.X, s.Value.y + fr.Y));
        }
    }

    (double[] right, double[] fwd) CamAxes()
    {
        double y = Cam.Yaw * Math.PI / 180;
        return (new[] { Math.Cos(y), 0, -Math.Sin(y) }, new[] { -Math.Sin(y), 0, -Math.Cos(y) });
    }
    double[] CamUp()
    {
        double d = Math.PI / 180, cp = Math.Cos(Cam.Pitch * d);
        double[] f = { -cp * Math.Sin(Cam.Yaw * d), -Math.Sin(Cam.Pitch * d), -cp * Math.Cos(Cam.Yaw * d) };
        var r = CamAxes().right;
        return new[] { r[1] * f[2] - r[2] * f[1], r[2] * f[0] - r[0] * f[2], r[0] * f[1] - r[1] * f[0] };
    }
    void Pan(double dx, double dy)
    {
        double k = Cam.Dist * 0.0018; var (right, _) = CamAxes();
        Cam.Target[0] -= right[0] * dx * k; Cam.Target[2] -= right[2] * dx * k;
        Cam.Target[1] += dy * k;
    }

    // ---- 손발 끌기 (IK): 손이나 발을 끌면 팔·다리 관절이 알아서 따라 굽혀짐 ----
    sealed record Chain(string End, (string j, int ax)[] Params, Dictionary<string, (double lo, double hi)> Limit);
    static Chain ChainFor(string j)
    {
        string s = j.EndsWith("L") ? "L" : j.EndsWith("R") ? "R" : null;
        if (s == null) return null;
        if (j.StartsWith("hand") || j.StartsWith("foreArm") || j.StartsWith("upperArm") || j.StartsWith("shoulder"))
            return new("hand" + s, new[] { ("upperArm" + s, 0), ("upperArm" + s, 1), ("upperArm" + s, 2), ("foreArm" + s, 0) }, new() { ["foreArm" + s] = (-160, 0) });
        if (j.StartsWith("foot") || j.StartsWith("shin") || j.StartsWith("thigh"))
            return new("foot" + s, new[] { ("thigh" + s, 0), ("thigh" + s, 1), ("thigh" + s, 2), ("shin" + s, 0) }, new() { ["shin" + s] = (0, 160) });
        return null;
    }
    static double[] EndPos(SceneNode n, Dictionary<string, double[]> pose, string end)
    {
        var W = M3.Mul(M3.Trs(n.Pos, n.Rot, n.Scale), Engine3D.MannequinMatrices(pose, n.Body)[end]);
        return new[] { W[12], W[13], W[14] };
    }
    static void SolveIK(SceneNode n, Chain ch, double[] target)
    {
        var pose = n.Pose;
        double Err() { var p = EndPos(n, pose, ch.End); return (p[0] - target[0]) * (p[0] - target[0]) + (p[1] - target[1]) * (p[1] - target[1]) + (p[2] - target[2]) * (p[2] - target[2]); }
        double best = Err();
        foreach (double step in new[] { 12, 6, 3, 1.5, 0.75 })
            for (int pass = 0; pass < 4; pass++)
            {
                bool improved = false;
                foreach (var (jn, ax) in ch.Params)
                {
                    if (!pose.TryGetValue(jn, out var r)) pose[jn] = r = new double[3];
                    var lim = ax == 0 && ch.Limit.TryGetValue(jn, out var l) ? l : (-180, 180);
                    foreach (double d in new[] { step, -step })
                    {
                        double old = r[ax];
                        r[ax] = Math.Clamp(old + d, lim.lo, lim.hi);
                        double e = Err();
                        if (e < best - 1e-9) { best = e; improved = true; } else r[ax] = old;
                    }
                }
                if (!improved) break;
            }
    }

    // ---------------------------------------------------------------- 옆 패널
    SceneNode Add(string kind, Action<SceneNode> init)
    {
        var nn = new SceneNode { Kind = kind, Name = "물체", Color = "#d8d8d8" };
        init(nn);
        scene.Nodes.Add(nn); sel = nn.Id;
        RenderSide(); Draw();
        return nn;
    }

    void AddHand(int s)
    {
        joint = "index1";
        // 손가락이 위를 향하게 (Z 축 180° 회전: 거울이 아니라 돌림이라 오른손은 그대로 오른손)
        var nn = Add("hand", x => { x.Name = s > 0 ? "오른손" : "왼손"; x.Side = s; x.Color = "#e9dccf"; x.SetScale(6); x.Rot = new double[] { 0, 0, 180 }; x.Pos = new[] { scene.Nodes.Count * 0.7, 0.5, 0 }; });
        Cam.Target = new[] { nn.Pos[0], nn.Pos[1] + 0.6, nn.Pos[2] };
        Cam.Dist = Math.Min(Cam.Dist, 3);
        RenderSide(); SyncSliders();
    }

    Control S(string label, Func<SceneNode> o, Func<SceneNode, double[]> arr, int i, double min, double max, double step, Func<double, string> fmt) =>
        ui.Slider(label, min, max, step, () => o() is SceneNode n ? arr(n)[i] : 0, v => { if (o() is SceneNode n) { arr(n)[i] = v; Draw(); } }, fmt);

    void RenderSide()
    {
        var old = side.Controls.Cast<Control>().ToList();
        side.SuspendLayout();
        side.Controls.Clear();
        foreach (var c in old) c.Dispose();
        jointCombo = null;
        var n = SelNode;
        string F2(double v) => v.ToString("0.00");
        string Deg(double v) => Math.Round(v) + "°";
        string Pct(double v) => Math.Round(v * 100) + "%";

        // 추가
        var prims = Ui3.Row(Engine3D.Primitives.Select(x => (Control)Ui3.Button(x.p.Name, () => { var nn = Add("prim", y => { y.Prim = x.id; y.Name = x.p.Name; }); nn.BuildParts(); Draw(); })).ToArray());
        side.Controls.Add(ui.Section("추가",
            Ui3.Row(Ui3.Button("★ 데생 인형", () => Add("mannequin", x => { x.Name = "데생 인형"; x.Color = "#e9dccf"; x.Pos = new[] { scene.Nodes.Count * 0.7, 0, 0 }; })),
                Ui3.Button("손 (오른손)", () => AddHand(1)), Ui3.Button("손 (왼손)", () => AddHand(-1))),
            prims,
            Ui3.Row(Ui3.Button("모델 파일 (.glb .gltf .obj)", ImportFile), Ui3.Button("에셋 폴더에서", ImportAsset))));

        // 물체 목록
        var list = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Opt.Bg };
        foreach (var x in scene.Nodes)
        {
            var item = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = x.Id == sel ? Color.FromArgb(80, 70, 50) : Opt.Bg2, Margin = new Padding(0, 1, 0, 1), Padding = new Padding(2) };
            var eye = new System.Windows.Forms.Button { Text = x.Visible ? "보임" : "숨김", Width = 44, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Opt.Fg, Margin = new Padding(0) };
            var xx = x;
            eye.Click += (_, _) => { xx.Visible = !xx.Visible; RenderSide(); Draw(); };
            var name = new Label { Text = x.Name, AutoSize = false, Width = 150, Height = 24, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Opt.Fg, Cursor = Cursors.Hand, AutoEllipsis = true };
            name.Click += (_, _) => { sel = xx.Id; RenderSide(); Draw(); };
            var copy = new System.Windows.Forms.Button { Text = "복제", Width = 50, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Opt.Fg, Margin = new Padding(0) };
            copy.Click += (_, _) => { var c = xx.CloneNode(); c.Id = Layer3D.Uid("n"); c.Name = xx.Name + " 2"; c.Pos[0] += 0.6; scene.Nodes.Add(c); sel = c.Id; RenderSide(); Draw(); };
            var del = new System.Windows.Forms.Button { Text = "삭제", Width = 50, Height = 24, FlatStyle = FlatStyle.Flat, ForeColor = Opt.Fg, Margin = new Padding(0) };
            del.Click += (_, _) => { scene.Nodes.Remove(xx); if (sel == xx.Id) sel = scene.Nodes.FirstOrDefault()?.Id; RenderSide(); Draw(); };
            item.Controls.AddRange(new Control[] { eye, name, copy, del });
            list.Controls.Add(item);
        }
        side.Controls.Add(ui.Section("물체", list));

        if (n != null)
        {
            var modes = new List<(string, string)> { ("orbit", "시점"), ("move", "이동"), ("turn", "회전") };
            if (n.Kind == "mannequin" || n.Kind == "hand") modes.Add(("pose", "포즈"));
            if (n.Kind == "mannequin") modes.Add(("ik", "손발 끌기"));
            if (!modes.Any(m => m.Item1 == mode)) mode = "orbit";
            side.Controls.Add(ui.Section($"선택: {n.Name}",
                ui.Segmented(modes.ToArray(), () => mode, v => { mode = v; if (v == "ik" && ChainFor(joint) == null) { joint = "handR"; ui.SyncAll(); } Draw(); }),
                S("좌우 (X)", () => SelNode, x => x.Pos, 0, -10, 10, 0.01, F2),
                S("높이 (Y)", () => SelNode, x => x.Pos, 1, -3, 6, 0.01, F2),
                S("앞뒤 (Z)", () => SelNode, x => x.Pos, 2, -10, 10, 0.01, F2),
                S("회전 X", () => SelNode, x => x.Rot, 0, -180, 180, 1, Deg),
                S("회전 Y", () => SelNode, x => x.Rot, 1, -180, 180, 1, Deg),
                S("회전 Z", () => SelNode, x => x.Rot, 2, -180, 180, 1, Deg),
                ui.Slider("크기", 0.05, 10, 0.01, () => SelNode?.S ?? 1, v => { SelNode?.SetScale(v); Draw(); }, F2),
                Ui3.Row(ui.ColorInput("색", () => n.Color, v => { n.Color = v; n.Tint = true; Draw(); }),
                    n.Kind == "model" ? ui.Toggle("모델 원래 색 무시", () => n.Tint == true, v => { n.Tint = v; Draw(); }) : null),
                n.Kind == "model" ? Ui3.Row(Ui3.Button("이름 바꾸기", () => { var t = Prompt("이름", n.Name); if (!string.IsNullOrEmpty(t)) { n.Name = t; RenderSide(); } })) : null));
        }

        Control JointSliders(string[] labels) => Ui3.Row(Enumerable.Range(0, 3).Select(i => ui.Slider(labels[i], -180, 180, 1,
            () => n.Pose.TryGetValue(joint, out var r) ? r[i] : 0,
            v => { if (!n.Pose.TryGetValue(joint, out var r)) n.Pose[joint] = r = new double[3]; r[i] = v; Draw(); }, Deg)).ToArray());

        if (n?.Kind == "hand")
        {
            jointItems = Engine3D.HandJointNames;
            var (jbox, jc) = ui.Select("마디", jointItems, () => { if (!jointItems.Any(x => x.id == joint)) joint = "index1"; return joint; }, v => { joint = v; ui.SyncAll(); Draw(); });
            jointCombo = jc;
            side.Controls.Add(ui.Section("손 포즈",
                jbox,
                JointSliders(new[] { "굽힘 (X)", "비틀기 (Y)", "옆으로 벌림 (Z)" }),
                Ui3.Row(Engine3D.HandPoses.Select(p => (Control)Ui3.Button(p.name, () => { n.Pose = p.pose.Clone(); SyncSliders(); })).ToArray()),
                Ui3.Row(Ui3.Button("마디 초기화", () => { n.Pose.Remove(joint); SyncSliders(); }),
                    Ui3.Button("왼손/오른손 바꾸기", () => { n.Side = -(n.Side ?? 1); n.Name = n.Side > 0 ? "오른손" : "왼손"; RenderSide(); Draw(); }),
                    Ui3.Button("사진에서 손 모양 (AI)…", () => PhotoPose("hand"))),
                Ui3.Help("\"포즈\" 모드에서 화면을 끌면 고른 마디가 굽혀집니다 (마디 점을 눌러 고를 수도 있습니다). 굽힘이 음수면 손바닥 쪽으로 접힙니다.")));
        }
        if (n?.Kind == "mannequin")
        {
            jointItems = Engine3D.JointNames;
            var (jbox, jc) = ui.Select("관절", jointItems, () => { if (!jointItems.Any(x => x.id == joint)) joint = "upperArmR"; return joint; }, v => { joint = v; ui.SyncAll(); Draw(); });
            jointCombo = jc;
            var bvhCtl = new List<Control>();
            if (n.Bvh != null)
            {
                var b = n.Bvh;
                bvhCtl.Add(ui.Slider($"BVH 프레임 ({b.Poses.Count}장)", 0, Math.Max(1, b.Poses.Count - 1), 1, () => b.At, v => { b.At = Math.Clamp((int)v, 0, b.Poses.Count - 1); n.Pose = b.Poses[b.At].Clone(); ui.SyncAll(); Draw(); }, v => $"{(int)v + 1}"));
                bvhCtl.Add(Ui3.Help($"{b.Name} · 관절 {b.Matched}개 맞춤 · {b.Fps:0.0}fps"));
            }
            side.Controls.Add(ui.Section("포즈", new Control[]
            {
                jbox,
                JointSliders(new[] { "앞뒤로 굽힘 (X)", "비틀기 (Y)", "옆으로 벌림 (Z)" }),
                Ui3.Row(Engine3D.Poses.Select(p => (Control)Ui3.Button(p.name, () => { n.Pose = p.pose.Clone(); SyncSliders(); })).ToArray()),
                Ui3.Row(Ui3.Button("관절 초기화", () => { n.Pose.Remove(joint); SyncSliders(); }), Ui3.Button("좌우 뒤집기", () => { n.Pose = Engine3D.MirrorPose(n.Pose); SyncSliders(); })),
                Ui3.Row(Ui3.Button("BVH 포즈 파일 열기…", () => LoadBVH(n)), Ui3.Button("사진에서 자세 (AI)…", () => PhotoPose("pose"))),
            }.Concat(bvhCtl).Append(Ui3.Help("\"포즈\" 모드: 관절 점을 누르거나 목록에서 고른 뒤 화면을 끌면 그 관절이 돌아갑니다. \"손발 끌기\" 모드: 손·발(또는 팔·다리 마디)을 고르고 끌면 팔꿈치·무릎까지 알아서 굽혀집니다.")).ToArray()));

            var body = new List<Control>
            {
                Ui3.Row(Engine3D.BodyPresets.Select(p => (Control)Ui3.Button(p.name, () => { n.Body = Engine3D.BodyKeys.ToDictionary(k => k, k => p.v.GetValueOrDefault(k, 1)); SyncSliders(); })).ToArray()),
            };
            foreach (var k in Engine3D.BodyKeys)
            {
                var key = k;
                body.Add(ui.Slider(Engine3D.BodyNames[k], k == "head" ? 0.6 : 0.4, k == "head" ? 2.5 : 1.8, 0.01, () => n.Body?.GetValueOrDefault(key, 1) ?? 1,
                    v => { n.Body ??= Engine3D.BodyKeys.ToDictionary(x => x, _ => 1.0); n.Body[key] = v; Draw(); }, Pct));
            }
            side.Controls.Add(ui.Section("체형", body.ToArray()));
        }

        // 카메라
        void CamSet(Action a) { a(); SyncSliders(); }
        side.Controls.Add(ui.Section("카메라",
            ui.Slider("화각 (원근감)", 8, 100, 1, () => Cam.Fov, v => { Cam.Fov = v; Draw(); }, Deg),
            ui.Slider("거리", 0.3, 40, 0.05, () => Cam.Dist, v => { Cam.Dist = v; Draw(); }, F2, log: true),
            ui.Slider("좌우 각도", -180, 180, 1, () => ((Cam.Yaw % 360) + 540) % 360 - 180, v => { Cam.Yaw = v; Draw(); }, Deg),
            ui.Slider("높낮이 (부감/앙각)", -89, 89, 1, () => Cam.Pitch, v => { Cam.Pitch = v; Draw(); }, Deg),
            Ui3.Row(
                Ui3.Button("정면", () => CamSet(() => { Cam.Yaw = 0; Cam.Pitch = 0; })),
                Ui3.Button("옆", () => CamSet(() => { Cam.Yaw = 90; Cam.Pitch = 0; })),
                Ui3.Button("부감", () => CamSet(() => Cam.Pitch = 45)),
                Ui3.Button("앙각", () => CamSet(() => Cam.Pitch = -25)),
                Ui3.Button("선택 물체 보기", () => { var x = SelNode; if (x != null) { Cam.Target = new[] { x.Pos[0], x.Pos[1] + 0.9 * x.S, x.Pos[2] }; Draw(); } }),
                Ui3.Button("초기화", () => CamSet(() => Cam.Set(DefaultCamera))))));

        var st = scene.Style;
        side.Controls.Add(ui.Section("만화 스타일",
            ui.Slider("음영 단계", 0, 4, 1, () => st.Levels, v => { st.Levels = v; Draw(); }, v => v > 0 ? v + "단" : "부드럽게"),
            ui.Slider("경계 부드러움", 0, 0.5, 0.01, () => st.Soft, v => { st.Soft = v; Draw(); }),
            ui.Slider("밝기 (환경광)", 0, 0.9, 0.01, () => st.Ambient, v => { st.Ambient = v; Draw(); }),
            ui.Slider("빛 방향 (좌우)", -180, 180, 1, () => st.LightAz, v => { st.LightAz = v; Draw(); }, Deg),
            ui.Slider("빛 높이", -30, 90, 1, () => st.LightEl, v => { st.LightEl = v; Draw(); }, Deg),
            ui.ColorInput("그림자 색", () => st.ShadowColor, v => { st.ShadowColor = v; Draw(); }),
            ui.Toggle("선화", () => st.Line, v => { st.Line = v; Draw(); }),
            ui.Slider("선 굵기 (원고 px)", 1, 20, 0.5, () => st.LineWidth, v => { st.LineWidth = v; Draw(); }),
            ui.Slider("안쪽 선 양", 0, 1, 0.01, () => 1 - st.NThr, v => { st.NThr = 1 - v; Draw(); }, Pct),
            ui.Slider("겹침 선 감도", 0.01, 0.5, 0.01, () => 0.51 - st.DThr, v => { st.DThr = 0.51 - v; Draw(); }, v => Math.Round(v * 200) + "%"),
            ui.ColorInput("선 색", () => st.LineColor, v => { st.LineColor = v; Draw(); }),
            Ui3.Row(ui.Toggle("원고 비치기", () => st.BgDoc, v => { st.BgDoc = v; Draw(); }), ui.Toggle("바닥 격자", () => st.Grid, v => { st.Grid = v; Draw(); }))));

        side.Controls.Add(ui.Section("애니메이션 프레임으로",
            Ui3.Help("지금 장면을 여러 장 그려 애니메이션 폴더로 넣습니다: BVH 동작을 따라가거나, 카메라를 빙 돌리거나."),
            Ui3.Row(Ui3.Button("애니 프레임 만들기…", ToAnimFrames))));

        side.Controls.Add(ui.Section("만화에 넣을 레이어",
            Ui3.Row(ui.Toggle("색", () => st.OutColor, v => st.OutColor = v), ui.Toggle("음영(곱하기)", () => st.OutShade, v => st.OutShade = v), ui.Toggle("선화", () => st.OutLine, v => st.OutLine = v)),
            ui.Toggle("퍼스 자도 같이 만들기", () => st.OutRulers != false, v => st.OutRulers = v),
            Ui3.Row(Ui3.Button("✓ 만화에 넣기", Insert, true), Ui3.Button("퍼스 자만 만들기", () => RulersFromCamera(false))),
            Ui3.Help("원고 크기 그대로 \"3D\" 폴더에 색 · 음영 · 선화 레이어로 들어갑니다. 폴더 안 레이어를 고르고 다시 3D 창을 열면 이어서 고칠 수 있습니다.")));

        side.ResumeLayout();
    }

    string Prompt(string title, string value)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10) };
        UI.Dialogs.Modals.Adopt(f);
        var tb = new TextBox { Text = value, Width = 280, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
        var ok = new System.Windows.Forms.Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new System.Windows.Forms.Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        p.Controls.Add(tb); p.Controls.Add(Ui3.Row(ok, cancel));
        f.Controls.Add(p); f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : null;
    }

    // ---------------------------------------------------------------- 모델 불러오기
    void ImportBytes(string name, byte[] bytes)
    {
        bool isObj = name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase);
        try
        {
            var nn = new SceneNode { Kind = "model", Name = Path.GetFileNameWithoutExtension(name), Pos = new[] { scene.Nodes.Count * 0.7, 0, 0 }, Src = new JsonObject { ["type"] = isObj ? "obj" : "glb", ["name"] = name, ["data"] = Convert.ToBase64String(bytes) } };
            Cursor = Cursors.WaitCursor;
            nn.BuildParts();
            scene.Nodes.Add(nn); sel = nn.Id;
            RenderSide(); Draw();
            if (bytes.Length > 15e6) App.Toast("큰 모델은 작품 파일 크기가 커집니다");
        }
        catch (Exception e) { MessageBox.Show(this, "모델을 읽지 못했습니다: " + e.Message, "3D 소재"); }
        finally { Cursor = Cursors.Default; }
    }
    void ImportFile()
    {
        using var dlg = new OpenFileDialog { Filter = "3D 모델 (*.glb;*.gltf;*.obj)|*.glb;*.gltf;*.obj" };
        if (dlg.ShowDialog(this) == DialogResult.OK) ImportBytes(Path.GetFileName(dlg.FileName), File.ReadAllBytes(dlg.FileName));
    }
    void ImportAsset()
    {
        string dir = Path.Combine(App.DataDir, "assets");
        var items = Directory.Exists(dir) ? Directory.GetFiles(dir).Where(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).OrderBy(x => x).ToArray() : Array.Empty<string>();
        if (items.Length == 0) { MessageBox.Show(this, "data/assets 폴더에 .glb/.gltf/.obj 파일을 넣어 주세요", "3D 소재"); return; }
        var v = Ui3.AskForm(this, "에셋 폴더의 3D 모델", "확인", new Ui3.Field("n", "파일", "select", items[0], Options: items.Select(i => (i, i)).ToArray()));
        if (v?["n"] is string name) ImportBytes(name, File.ReadAllBytes(Path.Combine(dir, name)));
    }
    void DropFiles(string[] files)
    {
        foreach (var f in files ?? Array.Empty<string>())
        {
            if (f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) ImportBytes(Path.GetFileName(f), File.ReadAllBytes(f));
            else if (f.EndsWith(".bvh", StringComparison.OrdinalIgnoreCase) && SelNode?.Kind == "mannequin") LoadBVHFile(SelNode, f);
            else Status(".glb / .gltf / .obj 파일만 넣을 수 있습니다");
        }
    }

    // ---------------------------------------------------------------- BVH · 사진
    void LoadBVH(SceneNode n)
    {
        using var dlg = new OpenFileDialog { Filter = "BVH 모션 (*.bvh)|*.bvh" };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadBVHFile(n, dlg.FileName);
    }
    void LoadBVHFile(SceneNode n, string path)
    {
        try
        {
            var r = Bvh.Poses(File.ReadAllText(path));
            n.Bvh = new BvhInfo { Name = Path.GetFileName(path), Poses = r.Poses, Fps = r.Fps, Matched = r.Matched, At = 0 };
            n.Pose = r.Poses[0].Clone();
            RenderSide(); Draw();
            Status($"BVH: {r.Frames}프레임 → {r.Poses.Count}개 포즈 (관절 {r.Matched}개 맞춤)");
        }
        catch (Exception e) { MessageBox.Show(this, "BVH 를 읽지 못했습니다: " + e.Message, "3D 소재"); }
    }

    /// <summary>사진 → 포즈 추정기 (Vision 모듈이 채움). (kind "pose"|"hand", 창) → 포즈 또는 null(취소·실패, 알림은 추정기가)</summary>
    public static Func<string, IWin32Window, Pose> PhotoPoseProvider;

    void PhotoPose(string kind)
    {
        var n = SelNode;
        if (PhotoPoseProvider != null && n != null)
        {
            var pose = PhotoPoseProvider(kind, this);
            if (pose == null) return;
            n.Pose = pose;
            RenderSide(); Draw();
            Status(kind == "pose" ? "사진의 자세를 인형에 입혔습니다 (관절 슬라이더로 다듬으세요)" : "사진의 손 모양을 입혔습니다");
            return;
        }
        // 웹 판은 MediaPipe(.task, TFLite) 로 추정한다. 네이티브 판에는 이 형식을 돌릴 실행기가 없어 아직 쓸 수 없다.
        MessageBox.Show(this, (kind == "pose" ? "사진에서 자세" : "사진에서 손 모양") + " (AI) 는 네이티브 판에서 아직 쓸 수 없습니다.\n\n이 기능의 모델(MediaPipe .task)은 웹 판에서만 돌아갑니다. 웹 판에서 자세를 입힌 뒤 3D 폴더를 저장하면 네이티브 판에서 이어서 고칠 수 있습니다.", "3D 소재");
    }

    // ---------------------------------------------------------------- 3D 카메라 → 퍼스 자
    // 물체의 세 방향(가로·깊이·세로)이 화면에서 모이는 점(소실점)을 계산해 원고에 투시 자로 넣는다
    int RulersFromCamera(bool silent)
    {
        var vps = VanishingPoints(scene, SelNode, doc.W, doc.H);
        if (vps.Count == 0) { if (!silent) Status("지금 시점에서는 소실점이 없습니다 (정면 평행 투영에 가까움)"); return 0; }
        ExtraEdit.Change(doc, "3D 퍼스 자", "rulers", () =>
        {
            var arr = doc.Extra["rulers"] as JsonArray ?? new JsonArray();
            arr.Add(new JsonObject { ["type"] = "perspective", ["vps"] = new JsonArray(vps.Select(v => (JsonNode)new JsonObject { ["x"] = v.x, ["y"] = v.y }).ToArray()), ["from3D"] = true });
            doc.Extra["rulers"] = arr;
        });
        App.Set("showRulers", true);
        App.InvalidateAll();
        var n = SelNode;
        if (!silent) Status($"{vps.Count}점 투시 자를 만들었습니다{(n != null ? $" (\"{n.Name}\" 방향 기준)" : "")}. 자 도구로 확인하세요");
        return vps.Count;
    }

    public static List<(double x, double y)> VanishingPoints(Scene3 scene, SceneNode n, int W, int H)
    {
        var cm = Render3D.Camera(scene.Camera, (double)W / H);
        var VP = M3.Mul(cm.Proj, cm.View);
        var rot = n != null ? M3.Euler(n.Rot) : M3.Ident();   // 고른 물체가 돌아가 있으면 그 방향 기준
        var vps = new List<(double, double)>();
        for (int axis = 0; axis < 3; axis++)
        {
            double[] d = { rot[axis * 4], rot[axis * 4 + 1], rot[axis * 4 + 2] };
            double x = VP[0] * d[0] + VP[4] * d[1] + VP[8] * d[2], y = VP[1] * d[0] + VP[5] * d[1] + VP[9] * d[2], w = VP[3] * d[0] + VP[7] * d[1] + VP[11] * d[2];
            if (Math.Abs(w) < 1e-4) continue;                 // 화면과 나란한 방향: 소실점 없음
            double nx = x / w, ny = y / w;
            if (Math.Sqrt(nx * nx + ny * ny) > 40) continue;  // 너무 멀면 사실상 평행
            vps.Add(((nx * 0.5 + 0.5) * W, (0.5 - ny * 0.5) * H));
        }
        return vps;
    }

    // ---------------------------------------------------------------- 3D → 애니메이션 프레임
    void ToAnimFrames()
    {
        var withBvh = scene.Nodes.Where(x => x.Bvh != null).ToList();
        var v = Ui3.AskForm(this, "애니메이션 프레임 만들기", "만들기",
            new Ui3.Field("count", "장 수", "number", withBvh.Count > 0 ? Math.Min(48, withBvh[0].Bvh.Poses.Count) : 24, 2, 240),
            new Ui3.Field("bvh", withBvh.Count > 0 ? $"BVH 동작 따라가기 ({string.Join(", ", withBvh.Select(x => x.Name))})" : "BVH 동작 (불러온 BVH 가 없음)", "checkbox", withBvh.Count > 0),
            new Ui3.Field("orbit", "카메라를 돌리는 각도 (0 = 고정)", "number", withBvh.Count > 0 ? 0 : 90, -720, 720),
            new Ui3.Field("what", "그릴 것", "select", "line", Options: new[] { ("line", "선화만"), ("color", "색+선화") }),
            new Ui3.Field("info", "프레임마다 원고 크기 그대로 한 장씩 그려 \"3D 애니\" 폴더에 넣습니다. 위에 따라 그리거나 그대로 써도 됩니다.", "info", null));
        if (v == null) return;
        int N = Math.Clamp((int)Math.Round((double)v["count"]), 2, 240);
        bool useBvh = (bool)v["bvh"]; double orbit = (double)v["orbit"]; string what = (string)v["what"];
        double s = Math.Min(1, 4096.0 / Math.Max(doc.W, doc.H));
        int W = (int)Math.Round(doc.W * s), H = (int)Math.Round(doc.H * s);
        double yaw0 = Cam.Yaw; var saved = scene.Nodes.Select(x => x.Pose.Clone()).ToList();
        var frames = new List<uint[]>();
        Cursor = Cursors.WaitCursor;
        try
        {
            for (int i = 0; i < N; i++)
            {
                Status($"3D → {N}장: {i + 1}/{N}");
                double t = N == 1 ? 0 : (double)i / (N - 1);
                if (useBvh) foreach (var x in withBvh) x.Pose = x.Bvh.Poses[Math.Min(x.Bvh.Poses.Count - 1, (int)Math.Round(t * (x.Bvh.Poses.Count - 1)))].Clone();
                Cam.Yaw = yaw0 + orbit * t;
                var st = scene.Style.Clone(); st.Line = true;
                var line = Render3D.Render(scene, W, H, "line", st, s, false, 2);
                uint[] buf;
                if (what == "color")
                {
                    buf = Render3D.Render(scene, W, H, "color", st, s, false, 2);
                    var shade = Render3D.Render(scene, W, H, "shade", st, s, false, 2);
                    for (int k = 0; k < buf.Length; k++) { if (shade[k] != 0) buf[k] = Px.Composite(shade[k], buf[k], Core.Blend.Multiply); if (line[k] != 0) buf[k] = Px.Over(line[k], buf[k]); }
                }
                else buf = line;
                frames.Add(ScaleTo(buf, W, H, doc.W, doc.H));
            }
        }
        finally
        {
            Cam.Yaw = yaw0; for (int k = 0; k < scene.Nodes.Count; k++) scene.Nodes[k].Pose = saved[k];
            Cursor = Cursors.Default; Draw();
        }
        var beforeS = StructSnap.Take(doc); var beforeAnim = doc.Extra["anim"]?.DeepClone();
        var f = new Layer(doc, "3D 애니", "folder") { Meta = new JsonObject { ["anim"] = true } };
        doc.Layers.Add(f);
        for (int i = 0; i < frames.Count; i++)
        {
            var l = new Layer(doc, (i + 1).ToString()) { Parent = f.Id };
            l.Px.Write(new Rect(0, 0, doc.W, doc.H), frames[i]);
            doc.Layers.Insert(doc.Layers.IndexOf(f), l);
        }
        double fps = useBvh && withBvh.Count > 0 ? Math.Min(24, Math.Max(6, withBvh[0].Bvh.Fps * withBvh[0].Bvh.Poses.Count / N)) : 12;
        doc.Anim["fps"] = (int)Math.Round(fps);
        doc.ActiveId = doc.ChildrenOf(f).FirstOrDefault()?.Id ?? f.Id;
        doc.Modified = true;
        App.History.PushGroup("3D 애니 프레임", new StructEdit("3D 애니 프레임", beforeS, StructSnap.Take(doc)), new ExtraEdit("3D 애니 프레임", "anim", beforeAnim, doc.Extra["anim"]));
        App.NotifyLayers(); App.InvalidateAll();
        Status($"\"3D 애니\" 폴더에 {N}장을 넣었습니다. 애니 패널에서 재생해 보세요");
        App.Toast($"\"3D 애니\" 폴더에 {N}장을 넣었습니다. 애니 패널에서 재생해 보세요");
    }

    /// <summary>미리 곱한 BGRA 이중선형 크기 바꾸기 (같으면 그대로)</summary>
    public static uint[] ScaleTo(uint[] src, int w, int h, int W, int H)
    {
        if (w == W && h == H) return src;
        var o = new uint[W * H];
        Parallel.For(0, H, y =>
        {
            double fy = Math.Clamp((y + 0.5) * h / H - 0.5, 0, h - 1); int y0 = (int)fy, y1 = Math.Min(h - 1, y0 + 1); double ay = fy - y0;
            for (int x = 0; x < W; x++)
            {
                double fx = Math.Clamp((x + 0.5) * w / W - 0.5, 0, w - 1); int x0 = (int)fx, x1 = Math.Min(w - 1, x0 + 1); double ax = fx - x0;
                uint a = src[y0 * w + x0], b = src[y0 * w + x1], c = src[y1 * w + x0], d = src[y1 * w + x1];
                if ((a | b | c | d) == 0) continue;
                uint Ch(int sh) { double v = (((a >> sh) & 255) * (1 - ax) + ((b >> sh) & 255) * ax) * (1 - ay) + (((c >> sh) & 255) * (1 - ax) + ((d >> sh) & 255) * ax) * ay; return (uint)Math.Clamp((int)(v + 0.5), 0, 255); }
                o[y * W + x] = Px.Pack(Ch(24), Ch(16), Ch(8), Ch(0));
            }
        });
        return o;
    }

    // ---------------------------------------------------------------- 만화에 넣기
    void Insert()
    {
        var st = scene.Style;
        if (!st.OutColor && !st.OutShade && !st.OutLine) { Status("넣을 레이어를 하나 이상 고르세요"); return; }
        Cursor = Cursors.WaitCursor;
        try
        {
            Status("3D 합성 중…");
            var json = InsertInto(doc, folder, scene);
            App.Set("last3D", Scene3.Slim(json));
            if (st.OutRulers != false) RulersFromCamera(true);
            App.NotifyLayers(); App.InvalidateAll();
        }
        catch (Exception e) { MessageBox.Show(this, "3D 합성 실패: " + e.Message, "3D 소재"); return; }
        finally { Cursor = Cursors.Default; }
        done = true;
        Close();
        App.Toast("3D 폴더에 넣었습니다. 선화 레이어 위에 그려서 다듬어 보세요");
    }

    /// <summary>장면을 원고 크기 레이어로 그려 "3D" 폴더에 (있던 폴더면 안을 바꿈). 실행취소 한 번으로 되돌림. 장면 JSON 을 돌려줌</summary>
    public static JsonObject InsertInto(Document doc, Layer folder, Scene3 scene)
    {
        var st = scene.Style;
        double s = Math.Min(1, 8192.0 / Math.Max(doc.W, doc.H));
        int W = (int)Math.Round(doc.W * s), H = (int)Math.Round(doc.H * s);
        int ss = (long)W * H * 4 <= 120_000_000 ? 2 : 1;               // 가장자리를 부드럽게 (너무 크면 1배)
        var st2 = st.Clone(); st2.Line = true;
        uint[] Snap(string what) => ScaleTo(Render3D.Render(scene, W, H, what, st2, s, false, ss), W, H, doc.W, doc.H);
        var imgs = new List<(string name, uint[] px, string blend)>();
        if (st.OutColor) imgs.Add(("3D 색", Snap("color"), "source-over"));
        if (st.OutShade) imgs.Add(("3D 음영", Snap("shade"), "multiply"));
        if (st.OutLine) imgs.Add(("3D 선화", Snap("line"), "source-over"));
        var json = scene.ToJson();
        App.History.Struct(doc, "3D 합성", () =>
        {
            var f = folder != null && doc.Layers.Contains(folder) ? folder : null;
            if (f != null)
            {
                foreach (var c in doc.Layers.Where(x => doc.IsInside(x, f)).ToList()) doc.Layers.Remove(c);
                var meta = (JsonObject)(f.Meta?.DeepClone() ?? new JsonObject());
                meta["scene3d"] = json.DeepClone();
                f.Meta = meta;
            }
            else
            {
                f = new Layer(doc, "3D", "folder") { Meta = new JsonObject { ["scene3d"] = json.DeepClone() } };
                doc.Layers.Add(f);
            }
            foreach (var (name, px, blend) in imgs)
            {
                var l = new Layer(doc, name) { Parent = f.Id, Blend = blend };
                l.Px.Write(new Rect(0, 0, doc.W, doc.H), px);
                doc.Layers.Insert(doc.Layers.IndexOf(f), l);
            }
            doc.ActiveId = f.Id;
        });
        return json;
    }

    internal bool Testing;            // 자체 점검: 닫을 때 묻지 않고 설정에도 쓰지 않음
    void OnClosingWin(object s, FormClosingEventArgs e)
    {
        if (Testing) return;
        if (!done && MessageBox.Show(this, "3D 창을 닫을까요? (넣지 않은 변경은 마지막 장면으로만 기억됩니다)", "닫기", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { e.Cancel = true; return; }
        App.Set("last3D", Scene3.Slim(scene.ToJson()));
    }
}
