// 리깅 편집기 창: 왼쪽 = 부위·디포머 나무 + 고른 것의 속성, 가운데 = 미리보기 (메쉬·격자·손잡이), 오른쪽 = 파라미터·키폼·물리·움직이기.
//  모드: 보기(눌러서 부위 고르기) · 메쉬 편집(쉼 자세에서 점 옮기기 / Shift+누르기 점 넣기 / 오른쪽 누르기·Delete 점 빼기)
//        · 변형 편집(지금 파라미터 값이 키 위일 때 점·격자·회전 손잡이를 끌어 그 키폼을 고침, 끌기 반경으로 부드럽게)
//  실행취소·다시하기는 편집기 안에서 (리그 JSON 스냅숏), 닫거나 "작품에 저장" 하면 작품 기록(Ctrl+Z)에도 한 번에 들어간다.
//  카메라 얼굴 추적은 단추를 눌렀을 때만 켠다 (Vtuber.FaceSourceFactory).
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Scene3D;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.VRig2;

public sealed class RigEditorForm : Form
{
    static RigEditorForm live;
    readonly Document doc;
    Rig2 rig;
    readonly RigRuntime rt;
    readonly CpuRigRenderer ren = new();
    readonly RigCanvas canvas;
    readonly TreeView tree;
    readonly Panel propHost, rightHost;
    FlowLayoutPanel propBox, rightBox;
    readonly Ui3 ui = new();
    Ui3 propUi = new();
    readonly Label status;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly List<string> undo = new(), redo = new();
    string lastUndoLabel; long lastUndoMs;
    bool dirty, physics = true, idle, showMesh = true;
    string mode = "view";                 // view | mesh | deform
    string selPart, selDef;
    string keyParam = "ParamAngleX";
    double brush = 40;                    // 끌기 반경 (화면 px)
    IVFaceSource face;
    VState faceState;
    double lastT, renderMs;
    int frames; double fpsT; double fps;

    public static void Open()
    {
        if (App.Doc == null) return;
        if (live != null && !live.IsDisposed) { live.Activate(); return; }
        try { live = new RigEditorForm(App.Doc); live.FormClosed += (_, _) => live = null; live.Show(App.Main); }
        catch (Exception e) { App.Toast("리깅 편집기를 열지 못했습니다: " + e.Message); }
    }

    public RigEditorForm(Document d)
    {
        doc = d;
        rig = Rig2.FromDoc(d);
        bool fresh = rig == null;
        if (fresh) { rig = new Rig2 { W = d.W, H = d.H }; rig.EnsureStdParams(); }
        rig.EnsureStdParams();
        rt = new RigRuntime(rig);
        rt.LoadTextures(d);
        rt.Compile();
        Text = "버튜버 리깅 편집기 — " + d.Name;
        BackColor = Opt.Bg; ForeColor = Opt.Fg; Font = new Font("Malgun Gothic", 9f);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1500, 900); MinimumSize = new Size(1100, 640);
        KeyPreview = true;

        // ---- 위 막대
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, BackColor = Opt.Bg2, Padding = new Padding(8, 6, 8, 4), WrapContents = false };
        top.Controls.Add(Ui3.Button("자동 리그", AutoRigNow, true));
        top.Controls.Add(Ui3.Button("되돌리기", Undo));
        top.Controls.Add(Ui3.Button("다시하기", Redo));
        var modeSeg = ui.Segmented(new[] { ("view", "보기"), ("mesh", "메쉬 편집"), ("deform", "변형 편집") }, () => mode, v => { mode = v; Changed(false); SetHint(); });
        modeSeg.Width = 280; modeSeg.Height = 34;
        top.Controls.Add(modeSeg);
        top.Controls.Add(ui.Toggle("메쉬·격자 보기", () => showMesh, v => { showMesh = v; canvas.Invalidate(); }));
        top.Controls.Add(Ui3.Button("작품에 저장", () => { Apply(); SetStatus("작품에 저장했습니다 (Ctrl+Z 로 되돌릴 수 있음)"); }));
        top.Controls.Add(Ui3.Button("스튜디오에서 움직이기", OpenStudio));
        top.Controls.Add(Ui3.Button("움직임 내보내기…", ExportMotion));
        top.Controls.Add(Ui3.Button(".cpvt 저장", SaveCpvt));
        top.Controls.Add(Ui3.Button("그림 다시 읽기", ReloadTextures));
        foreach (Control c in top.Controls) c.Margin = new Padding(0, 0, 6, 0);

        // ---- 왼쪽
        var left = new Panel { Dock = DockStyle.Left, Width = 300, BackColor = Opt.Bg };
        tree = new TreeView { Dock = DockStyle.Top, Height = 330, BackColor = Theme.Panel2, ForeColor = Opt.Fg, BorderStyle = BorderStyle.None, HideSelection = false, FullRowSelect = true, ShowLines = true, Font = new Font("Malgun Gothic", 9f) };
        tree.AfterSelect += (_, e) =>
        {
            if (syncingTree) return;
            string tag = e.Node?.Tag as string;
            selPart = tag?.StartsWith("P:") == true ? tag[2..] : null;
            selDef = tag?.StartsWith("D:") == true ? tag[2..] : null;
            BuildProps(); BuildKeys(); canvas.Invalidate();
        };
        propHost = Ui3.Scroller(out propBox); propHost.Dock = DockStyle.Fill;
        left.Controls.Add(propHost); left.Controls.Add(tree);

        // ---- 오른쪽
        rightHost = Ui3.Scroller(out rightBox); rightHost.Dock = DockStyle.Right; rightHost.Width = Ui3.W + 60;

        // ---- 가운데
        canvas = new RigCanvas(this) { Dock = DockStyle.Fill };
        status = new Label { Dock = DockStyle.Bottom, Height = 28, ForeColor = Opt.Muted, Padding = new Padding(8, 6, 8, 0), BackColor = Opt.Bg2 };
        Controls.Add(canvas); Controls.Add(status); Controls.Add(rightHost); Controls.Add(left); Controls.Add(top);

        BuildTree(); BuildProps(); BuildRight();
        timer.Tick += (_, _) => Tick();
        Shown += (_, _) =>
        {
            lastT = clock.Elapsed.TotalSeconds; timer.Start();
            if (fresh) { SetStatus("아직 리그가 없습니다. \"자동 리그\"를 누르면 레이어 이름(얼굴·눈·흰자·눈동자·속눈썹·눈썹·입·앞머리·옆머리·뒷머리·몸…)으로 리그를 만듭니다."); }
            else SetHint();
        };
        FormClosing += (_, _) =>
        {
            timer.Stop();
            face?.Dispose(); face = null;
            if (dirty) Apply();
            ren.Dispose();
        };
    }

    // ---- 자체 점검용 ----
    internal Control TestCanvas => canvas;
    internal Rig2 TestRig => rig;
    internal void TestSelect(string partId) => Select(partId, null);
    internal void TestSelectDef(string defId) => Select(null, defId);
    internal void TestAutoRig() => AutoRigNow();
    internal void TestUndo() => Undo();

    // ================================================================== 상태
    void SetStatus(string t) => status.Text = t;
    void SetHint()
    {
        SetStatus(mode switch
        {
            "mesh" => "메쉬 편집: 점 끌기 = 옮기기 · Shift+누르기 = 점 넣기 · 오른쪽 누르기/Delete = 점 빼기 (쉼 자세로 보여 줍니다)",
            "deform" => "변형 편집: 파라미터를 키 값에 두고 점·격자·회전 손잡이를 끌면 그 키폼이 바뀝니다 (휠: 확대, 가운데·오른쪽 끌기: 이동)",
            _ => "보기: 그림을 눌러 부위 고르기 · 휠: 확대 · 가운데·오른쪽 끌기: 이동 · 파라미터 막대로 움직여 보기",
        });
    }

    /// <summary>바꾸기 전에 부름 (같은 이름의 연속 변경은 한 번만 기록)</summary>
    void Snapshot(string label, bool merge = false)
    {
        long now = clock.ElapsedMilliseconds;
        if (merge && label == lastUndoLabel && now - lastUndoMs < 900) { lastUndoMs = now; return; }
        undo.Add(rig.ToJsonString());
        if (undo.Count > 80) undo.RemoveAt(0);
        redo.Clear();
        lastUndoLabel = label; lastUndoMs = now;
        dirty = true;
    }
    void Restore(string json)
    {
        rig = Rig2.FromJson(JsonNode.Parse(json));
        rt.Rig = rig;
        rt.LoadTextures(doc, onlyMissing: true);
        rt.Compile();
        if (rig.Part(selPart) == null) selPart = null;
        if (rig.Def(selDef) == null) selDef = null;
        BuildTree(); BuildProps(); BuildKeys(); Changed(false);
        dirty = true;
    }
    void Undo() { if (undo.Count == 0) { SetStatus("되돌릴 것이 없습니다"); return; } redo.Add(rig.ToJsonString()); var s = undo[^1]; undo.RemoveAt(undo.Count - 1); Restore(s); lastUndoLabel = null; SetStatus("되돌렸습니다"); }
    void Redo() { if (redo.Count == 0) { SetStatus("다시 할 것이 없습니다"); return; } undo.Add(rig.ToJsonString()); var s = redo[^1]; redo.RemoveAt(redo.Count - 1); Restore(s); lastUndoLabel = null; SetStatus("다시 했습니다"); }

    /// <summary>모양이 바뀜 → 다시 계산·그리기 (structure: 부위·디포머 구성이 바뀜)</summary>
    void Changed(bool structure)
    {
        if (structure) rt.Compile();
        canvas.Invalidate();
    }

    void Apply()
    {
        if (doc == null) return;
        ExtraEdit.Change(doc, "버튜버 리그", "vtRig", () => doc.Extra["vtRig"] = rig.ToJson());
        dirty = false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (ActiveControl is TextBoxBase) return base.ProcessCmdKey(ref msg, keyData);
        if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
        if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
        if (keyData == Keys.Delete && mode == "mesh") { canvas.DeleteHover(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ================================================================== 움직이기
    void Tick()
    {
        if (IsDisposed) return;
        double now = clock.Elapsed.TotalSeconds, dt = now - lastT; lastT = now;
        bool anim = false;
        if (face != null)
        {
            var st = face.Poll(true);
            if (face.Status != null && status.Text != face.Status && st == null) SetStatus(face.Status);
            if (st != null)
            {
                st.T = now;
                faceState = VRig.SmoothState(faceState, st, 0.5);
                RigDrive.Apply(rt, faceState);
                anim = true;
            }
        }
        else if (idle)
        {
            var st = RigDrive.Demo(now);
            RigDrive.Apply(rt, st);
            anim = true;
        }
        if (physics && rig.Physics.Count > 0 && mode != "mesh")
        {
            var before = rig.Physics.Select(p => rt.Val(p.Output)).ToArray();
            RigPhysics.Step(rig, rt.Phys, rt.Values, dt);
            var after = rig.Physics.Select(p => rt.Val(p.Output)).ToArray();
            for (int i = 0; i < before.Length; i++) if (Math.Abs(before[i] - after[i]) > 1e-4) anim = true;
        }
        if (anim) { canvas.Invalidate(); if (rightBox.Visible) ui.SyncAll(); }
    }

    void ToggleCam(Control btn)
    {
        if (face != null) { face.Dispose(); face = null; faceState = null; btn.Text = "카메라 얼굴 추적 켜기"; SetStatus("카메라를 껐습니다"); return; }
        if (Vtuber.FaceSourceFactory == null) { SetStatus("카메라 얼굴 추적을 쓸 수 없습니다"); return; }
        try
        {
            face = Vtuber.FaceSourceFactory();
            if (face == null) return;
            idle = false; ui.SyncAll();
            btn.Text = "카메라 끄기";
            SetStatus("카메라 추적 준비 중… (영상은 저장·전송하지 않습니다)");
        }
        catch (Exception e) { face = null; SetStatus("카메라를 켜지 못했습니다: " + e.Message); }
    }

    // ================================================================== 나무
    bool syncingTree;
    void BuildTree()
    {
        syncingTree = true;
        tree.BeginUpdate();
        tree.Nodes.Clear();
        var nodes = new Dictionary<string, TreeNode>();
        TreeNode Def(RDeformer d)
        {
            if (nodes.TryGetValue("D:" + d.Id, out var n)) return n;
            n = new TreeNode((d.IsWarp ? "▦ " : "↻ ") + d.Name) { Tag = "D:" + d.Id, ForeColor = d.IsWarp ? Color.FromArgb(140, 190, 255) : Color.FromArgb(255, 190, 120) };
            nodes["D:" + d.Id] = n;
            var p = rig.Def(d.Parent);
            if (p != null && p.Id != d.Id && !rig.WouldCycle(d.Id, p.Id)) Def(p).Nodes.Add(n); else tree.Nodes.Add(n);
            return n;
        }
        foreach (var d in rig.Deformers) Def(d);
        foreach (var p in rig.Parts.OrderByDescending(p => p.Order))
        {
            var n = new TreeNode("◆ " + p.Name + "  · " + RigRoles.Name(p.Role)) { Tag = "P:" + p.Id, ForeColor = p.Hide ? Opt.Muted : Opt.Fg };
            nodes["P:" + p.Id] = n;
            var d = rig.Def(p.Parent);
            if (d != null) Def(d).Nodes.Add(n); else tree.Nodes.Add(n);
        }
        tree.ExpandAll();
        string want = selPart != null ? "P:" + selPart : selDef != null ? "D:" + selDef : null;
        if (want != null && nodes.TryGetValue(want, out var sel)) { tree.SelectedNode = sel; sel.EnsureVisible(); }
        else if (tree.Nodes.Count > 0) tree.Nodes[0].EnsureVisible();
        tree.EndUpdate();
        syncingTree = false;
    }
    internal void Select(string partId, string defId)
    {
        selPart = partId; selDef = defId;
        BuildTree(); BuildProps(); BuildKeys(); canvas.Invalidate();
    }

    // ================================================================== 속성 (왼쪽 아래)
    void BuildProps()
    {
        propBox.SuspendLayout();
        foreach (Control c in propBox.Controls.Cast<Control>().ToList()) c.Dispose();
        propUi = new Ui3();
        var u = propUi;
        var p = rig.Part(selPart); var d = rig.Def(selDef);
        (string id, string text)[] parentOpts = new[] { ("", "(없음 — 맨 위)") }.Concat(rig.Deformers.Where(x => x.Id != d?.Id && (d == null || !rig.WouldCycle(d.Id, x.Id))).Select(x => (x.Id, x.Name))).ToArray();
        if (p != null)
        {
            var cov = rt.Tex.TryGetValue(p.LayerId ?? "", out var tx) ? MeshGen.Coverage(p.Mesh, tx, p.Region) : (0, 0);
            propBox.Controls.Add(u.Section("부위: " + p.Name,
                Ui3.Help($"레이어: {doc.ById(p.LayerId)?.Name ?? "(없음)"} · 꼭짓점 {p.Mesh.N} · 삼각형 {p.Mesh.T.Length / 3} · 그림 덮음 {(cov.Item2 == 0 ? 100 : 100.0 * cov.Item1 / cov.Item2):0.0}%"),
                Ui3.Help("역할"), u.Select(null, RigRoles.All.Select(r => (r.Id, r.Name)).ToArray(), () => p.Role, v => { Snapshot("역할"); p.Role = v; BuildTree(); }).box,
                Ui3.Help("쪽"), u.Select(null, new[] { ("", "가운데"), ("L", "왼 (화면 오른쪽)"), ("R", "오른 (화면 왼쪽)") }, () => p.Side, v => { Snapshot("쪽"); p.Side = v; }).box,
                Ui3.Help("부모"), u.Select(null, parentOpts, () => p.Parent ?? "", v => { Snapshot("부모"); p.Parent = v == "" ? null : v; Changed(true); BuildTree(); }).box,
                u.Slider("그리기 순서", -100, Math.Max(100, rig.Parts.Max(q => q.Order) + 100), 1, () => p.Order, v => { Snapshot("순서", true); p.Order = v; BuildTree(); Changed(false); }),
                u.Slider("깊이 (자동 리그 시차)", -1, 1.5, 0.05, () => p.Depth, v => { Snapshot("깊이", true); p.Depth = v; }),
                u.Toggle("편집기에서 숨기기", () => p.Hide, v => { Snapshot("숨김"); p.Hide = v; BuildTree(); Changed(false); })));
            // 메쉬
            propBox.Controls.Add(u.Section("메쉬",
                u.Slider("촘촘함", 1, 10, 0.5, () => p.Density, v => { p.Density = v; }),
                Ui3.Row(Ui3.Button("메쉬 다시 만들기", () => RemeshPart(p)), Ui3.Button("삼각형 다시", () => Retri(p))),
                Ui3.Help("메쉬를 바꾸면 키폼의 점 오프셋은 새 점으로 옮겨집니다.")));
            // 클리핑
            var clipRows = new List<Control>();
            foreach (var cid in p.Clip.ToList())
            {
                var cp = rig.Part(cid);
                clipRows.Add(Ui3.Row(new Label { Text = cp?.Name ?? cid, AutoSize = true, ForeColor = Opt.Fg, Margin = new Padding(0, 8, 6, 0) }, Ui3.Button("빼기", () => { Snapshot("클리핑"); p.Clip.Remove(cid); BuildProps(); Changed(false); })));
            }
            string addClip = null;
            var others = rig.Parts.Where(q => q.Id != p.Id && !p.Clip.Contains(q.Id)).Select(q => (q.Id, q.Name)).ToArray();
            propBox.Controls.Add(u.Section("클리핑 마스크 (이 부위들이 칠해진 곳에만)",
                clipRows.Count > 0 ? Ui3.Row(clipRows.ToArray()) : Ui3.Help("없음"),
                others.Length > 0 ? u.Select(null, others, () => addClip ?? others[0].Id, v => addClip = v).box : null,
                Ui3.Row(Ui3.Button("마스크 넣기", () => { if (others.Length == 0) return; Snapshot("클리핑"); p.Clip.Add(addClip ?? others[0].Id); BuildProps(); Changed(false); }),
                    u.Toggle("반전 (바깥에만)", () => p.ClipInvert, v => { Snapshot("클리핑"); p.ClipInvert = v; Changed(false); }))));
            propBox.Controls.Add(u.Section(null, Ui3.Row(Ui3.Button("부위 빼기", () => { Snapshot("부위 빼기"); rig.Parts.Remove(p); foreach (var q in rig.Parts) q.Clip.Remove(p.Id); selPart = null; Changed(true); BuildTree(); BuildProps(); BuildKeys(); }),
                Ui3.Button("이 부위에 워프 씌우기", () => AddDeformer("warp", p)), Ui3.Button("회전 씌우기", () => AddDeformer("rot", p)))));
        }
        else if (d != null)
        {
            var items = new List<Control>
            {
                Ui3.Help(d.IsWarp ? $"워프 디포머 · 격자 {d.Cols}×{d.Rows} 칸" : "회전 디포머 · 중심을 끌어 옮기고, 손잡이 끝을 끌어 돌립니다 (변형 편집)"),
                Ui3.Help("부모"), u.Select(null, parentOpts, () => d.Parent ?? "", v => { if (v != "" && rig.WouldCycle(d.Id, v)) return; Snapshot("부모"); d.Parent = v == "" ? null : v; Changed(true); BuildTree(); }).box,
            };
            if (d.IsWarp)
            {
                items.Add(u.Slider("가로 칸", 1, 8, 1, () => d.Cols, v => { if ((int)v == d.Cols) return; Snapshot("격자", true); ResampleWarp(d, (int)v, d.Rows); }));
                items.Add(u.Slider("세로 칸", 1, 8, 1, () => d.Rows, v => { if ((int)v == d.Rows) return; Snapshot("격자", true); ResampleWarp(d, d.Cols, (int)v); }));
                items.Add(u.Toggle("부드러운 곡선 (베지어 식)", () => d.Smooth, v => { Snapshot("곡선"); d.Smooth = v; Changed(false); }));
            }
            else items.Add(u.Slider("손잡이 길이", 10, 600, 1, () => d.Len, v => { d.Len = v; canvas.Invalidate(); }));
            items.Add(Ui3.Row(Ui3.Button("디포머 지우기", () => DeleteDeformer(d)), Ui3.Button("워프 씌우기", () => AddDeformer("warp", null, d)), Ui3.Button("회전 씌우기", () => AddDeformer("rot", null, d))));
            propBox.Controls.Add(u.Section("디포머: " + d.Name, items.ToArray()));
        }
        else
        {
            var layers = doc.Layers.Where(l => (l.Px != null || l.IsFolder) && !rig.Parts.Any(q => q.LayerId == l.Id)).Select(l => (l.Id, (l.IsFolder ? "📁 " : "") + l.Name)).ToArray();
            string pick = null;
            propBox.Controls.Add(u.Section("리그",
                Ui3.Help($"부위 {rig.Parts.Count} · 디포머 {rig.Deformers.Count} · 파라미터 {rig.Params.Count} · 물리 {rig.Physics.Count}\n나무에서 부위나 디포머를 고르세요. 레이어 이름으로 역할을 짐작하지만, 부위의 역할을 바꾸고 \"자동 리그\"를 다시 누르면 그 역할로 다시 만듭니다."),
                layers.Length > 0 ? u.Select(null, layers, () => pick ?? layers[0].Id, v => pick = v).box : Ui3.Help("부위가 아닌 레이어가 없습니다"),
                layers.Length > 0 ? Ui3.Row(Ui3.Button("레이어를 부위로 넣기", () => AddLayerPart(pick ?? layers[0].Id))) : null,
                Ui3.Row(Ui3.Button("새 워프 디포머", () => AddDeformer("warp", null)), Ui3.Button("새 회전 디포머", () => AddDeformer("rot", null)))));
        }
        propBox.ResumeLayout();
    }

    void AddLayerPart(string layerId)
    {
        var l = doc.ById(layerId);
        if (l == null) return;
        var t = RigRuntime.TextureFor(doc, layerId);
        if (t == null || t.W <= 6) { SetStatus("그림이 없는 레이어입니다"); return; }
        Snapshot("부위 넣기");
        rt.Tex[layerId] = t;
        string role = RigRoles.Guess(l.Name) ?? "headAcc";
        var p = new RPart { Id = rig.NewId("P"), LayerId = layerId, Name = l.Name, Role = role, Order = doc.IndexOf(l) * 10, Depth = RigRoles.Get(role).Depth };
        p.Mesh = MeshGen.Auto(t, p.Density, null);
        p.Parent = rig.Def("D_HeadWarp") != null && RigRoles.Get(role).Head ? "D_HeadWarp" : rig.Def("D_BodyWarp")?.Id;
        rig.Parts.Add(p);
        selPart = p.Id; selDef = null;
        Changed(true); BuildTree(); BuildProps(); BuildKeys();
    }

    void RemeshPart(RPart p)
    {
        if (!rt.Tex.TryGetValue(p.LayerId ?? "", out var t)) return;
        Snapshot("메쉬");
        var old = p.Mesh;
        var m = MeshGen.Auto(t, p.Density, p.Region);
        p.Grid.RemapPoints(dd => MeshGen.TransferOffsets(old, dd, m.V));
        p.Mesh = m;
        Changed(true); BuildProps();
    }
    void Retri(RPart p)
    {
        if (!rt.Tex.TryGetValue(p.LayerId ?? "", out var t)) return;
        Snapshot("삼각형");
        p.Mesh.T = MeshGen.Retriangulate(p.Mesh.V, t, p.Region);
        Changed(true); BuildProps();
    }

    /// <summary>워프 격자 칸 수 바꾸기: 모든 키폼의 오프셋을 새 격자점에서 다시 읽음</summary>
    void ResampleWarp(RDeformer d, int cols, int rows)
    {
        var old = d.Clone();
        d.Cols = cols; d.Rows = rows;
        foreach (var (f, i) in d.Grid.Forms.Select((f, i) => (f, i)))
        {
            if (f.D == null) continue;
            var nd = new double[d.Nodes * 2];
            for (int k = 0; k < d.Nodes; k++)
            {
                var (x, y) = d.RestNode(k);
                var (dx, dy) = WarpDispAt(old, old.Grid.Forms[i].D, x, y);
                nd[k * 2] = dx; nd[k * 2 + 1] = dy;
            }
            f.D = nd;
        }
        Changed(true); BuildProps();
    }
    static (double, double) WarpDispAt(RDeformer d, double[] disp, double x, double y)
    {
        double u = Math.Clamp((x - d.X) / d.W * d.Cols, 0, d.Cols), v = Math.Clamp((y - d.Y) / d.H * d.Rows, 0, d.Rows);
        int ci = Math.Min((int)u, d.Cols - 1), ri = Math.Min((int)v, d.Rows - 1);
        double fu = u - ci, fv = v - ri; int s = d.Cols + 1, n = ri * s + ci;
        double W(int a, int c) => disp[(n + a) * 2 + c];
        double dx = W(0, 0) * (1 - fu) * (1 - fv) + W(1, 0) * fu * (1 - fv) + W(s, 0) * (1 - fu) * fv + W(s + 1, 0) * fu * fv;
        double dy = W(0, 1) * (1 - fu) * (1 - fv) + W(1, 1) * fu * (1 - fv) + W(s, 1) * (1 - fu) * fv + W(s + 1, 1) * fu * fv;
        return (dx, dy);
    }

    /// <summary>디포머 넣기: 부위나 디포머를 감싸서 (그 부모 아래에 끼움), 없으면 맨 위에</summary>
    void AddDeformer(string kind, RPart part, RDeformer child = null)
    {
        Snapshot("디포머 넣기");
        Box b;
        string parent;
        if (part != null) { b = PartBox(part); parent = part.Parent; }
        else if (child != null) { b = DefBox(child); parent = child.Parent; }
        else { b = new Box(rig.W * 0.3, rig.H * 0.3, rig.W * 0.4, rig.H * 0.4); parent = null; }
        if (b.IsEmpty) b = new Box(rig.W * 0.3, rig.H * 0.3, rig.W * 0.4, rig.H * 0.4);
        b = b.Inflate(b.W * 0.08 + 2, b.H * 0.08 + 2);
        var d = new RDeformer { Id = rig.NewId("D"), Kind = kind, Parent = parent, Name = (kind == "warp" ? "워프 " : "회전 ") + (part?.Name ?? child?.Name ?? (rig.Deformers.Count + 1).ToString()) };
        if (kind == "warp") { d.X = b.X; d.Y = b.Y; d.W = b.W; d.H = b.H; d.Cols = 2; d.Rows = 2; }
        else { d.Px = b.Cx; d.Py = b.B; d.Len = Math.Max(30, b.H * 0.6); }
        rig.Deformers.Add(d);
        if (part != null) part.Parent = d.Id;
        if (child != null) child.Parent = d.Id;
        selDef = d.Id; selPart = null;
        Changed(true); BuildTree(); BuildProps(); BuildKeys();
        SetStatus("디포머를 넣었습니다. 파라미터를 고르고 \"키 추가\" 한 뒤 변형 편집으로 모양을 만드세요.");
    }
    void DeleteDeformer(RDeformer d)
    {
        Snapshot("디포머 지우기");
        foreach (var x in rig.Deformers) if (x.Parent == d.Id) x.Parent = d.Parent;
        foreach (var p in rig.Parts) if (p.Parent == d.Id) p.Parent = d.Parent;
        rig.Deformers.Remove(d);
        selDef = null;
        Changed(true); BuildTree(); BuildProps(); BuildKeys();
    }

    Box PartBox(RPart p)
    {
        if (p.Mesh.N == 0) return Box.Empty;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        for (int k = 0; k < p.Mesh.N; k++) { x0 = Math.Min(x0, p.Mesh.V[k * 2]); x1 = Math.Max(x1, p.Mesh.V[k * 2]); y0 = Math.Min(y0, p.Mesh.V[k * 2 + 1]); y1 = Math.Max(y1, p.Mesh.V[k * 2 + 1]); }
        return Box.FromLTRB(x0, y0, x1, y1);
    }
    Box DefBox(RDeformer d) => d.IsWarp ? new Box(d.X, d.Y, d.W, d.H) : new Box(d.Px - d.Len / 2, d.Py - d.Len, d.Len, d.Len);

    // ================================================================== 오른쪽 (파라미터·키폼·물리)
    FlowLayoutPanel keyBox;
    Ui3 keyUi = new();
    void BuildRight()
    {
        rightBox.SuspendLayout();
        foreach (Control c in rightBox.Controls.Cast<Control>().ToList()) c.Dispose();
        ui.Syncs.Clear();
        System.Windows.Forms.Button camBtn = null;
        camBtn = Ui3.Button(face != null ? "카메라 끄기" : "카메라 얼굴 추적 켜기", () => ToggleCam(camBtn), true);
        rightBox.Controls.Add(ui.Section("움직여 보기",
            Ui3.Row(camBtn),
            Ui3.Row(ui.Toggle("물리 미리보기", () => physics, v => { physics = v; if (!v) { foreach (var ph in rig.Physics) rt.Values[ph.Output] = rig.Param(ph.Output)?.Def ?? 0; rt.Phys.Clear(); canvas.Invalidate(); ui.SyncAll(); } }),
                ui.Toggle("저절로 움직이기", () => idle, v => { idle = v; if (!v) { rt.Reset(); ui.SyncAll(); canvas.Invalidate(); } })),
            Ui3.Row(Ui3.Button("모두 기본값", () => { rt.Reset(); rt.Phys.Clear(); ui.SyncAll(); canvas.Invalidate(); BuildKeys(); })),
            Ui3.Help("카메라는 이 단추를 눌렀을 때만 켭니다. 영상은 저장·전송하지 않습니다.")));
        keyBox = new Controls_Stack();
        rightBox.Controls.Add(keyBox);
        BuildKeys();
        foreach (var grp in rig.Params.GroupBy(p => p.Group))
        {
            var items = new List<Control>();
            foreach (var p in grp)
            {
                var pp = p;
                double step = (p.Max - p.Min) >= 10 ? 0.5 : 0.01;
                items.Add(ui.Slider(p.Name, p.Min, p.Max, step, () => rt.Val(pp.Id), v => { rt.Values[pp.Id] = v; keyParam = pp.Id; canvas.Invalidate(); BuildKeysSoon(); }));
            }
            rightBox.Controls.Add(ui.Section("파라미터 · " + grp.Key, items.ToArray()));
        }
        rightBox.Controls.Add(ui.Section(null, Ui3.Row(Ui3.Button("파라미터 만들기…", AddUserParam))));
        // 물리
        var phys = new List<Control>();
        foreach (var ph in rig.Physics)
        {
            var p = ph;
            phys.Add(ui.Toggle($"{p.Name} → {rig.Param(p.Output)?.Name ?? p.Output}", () => p.On, v => { Snapshot("물리"); p.On = v; }));
            phys.Add(ui.Slider("  길이", 0.2, 4, 0.05, () => p.Length, v => { Snapshot("물리", true); p.Length = v; }));
            phys.Add(ui.Slider("  흔들림 줄이기 (감쇠)", 0, 10, 0.1, () => p.Damping, v => { Snapshot("물리", true); p.Damping = v; }));
            phys.Add(ui.Slider("  제자리로 (단단함)", 0, 2, 0.01, () => p.Stiffness, v => { Snapshot("물리", true); p.Stiffness = v; }));
            phys.Add(ui.Slider("  중력", 0, 3, 0.05, () => p.Gravity, v => { Snapshot("물리", true); p.Gravity = v; }));
            phys.Add(ui.Slider("  출력 크기", 0, 3, 0.05, () => p.OutScale, v => { Snapshot("물리", true); p.OutScale = v; }));
        }
        if (phys.Count == 0) phys.Add(Ui3.Help("물리가 없습니다. 자동 리그가 머리카락 부위가 있으면 만듭니다."));
        rightBox.Controls.Add(ui.Section("물리 (진자 사슬)", phys.ToArray()));
        rightBox.ResumeLayout();
    }
    sealed class Controls_Stack : UI.Controls.WebStack { }

    bool keysSoon;
    void BuildKeysSoon()
    {
        if (keysSoon) return;
        keysSoon = true;
        BeginInvoke(() => { keysSoon = false; if (!IsDisposed) BuildKeys(); });
    }

    KeyGrid SelGrid(out int points, out string name)
    {
        var p = rig.Part(selPart); var d = rig.Def(selDef);
        points = p != null ? p.Mesh.N : d?.PointCount ?? 0;
        name = p?.Name ?? d?.Name;
        return p?.Grid ?? d?.Grid;
    }

    void BuildKeys()
    {
        if (keyBox == null) return;
        keyBox.SuspendLayout();
        foreach (Control c in keyBox.Controls.Cast<Control>().ToList()) c.Dispose();
        keyUi = new Ui3();
        var g = SelGrid(out int pts, out string name);
        if (g == null) { keyBox.Controls.Add(keyUi.Section("키폼", Ui3.Help("부위나 디포머를 고르면 키폼(파라미터 값마다의 모양)을 보고 고칠 수 있습니다."))); keyBox.ResumeLayout(); return; }
        var items = new List<Control>();
        int exact = g.ExactIndex(rt.Val);
        foreach (var ax in g.Axes)
        {
            var prm = rig.Param(ax.Param);
            string ks = string.Join(" · ", ax.Keys.Select(k => (Math.Abs(k - rt.Val(ax.Param)) < 1e-6 ? "[" : "") + RigRoles.Fmt(k) + (Math.Abs(k - rt.Val(ax.Param)) < 1e-6 ? "]" : "")));
            items.Add(Ui3.Help($"{prm?.Name ?? ax.Param}: {ks}"));
        }
        if (g.Axes.Count == 0) items.Add(Ui3.Help("파라미터 축이 없습니다 (늘 같은 모양)."));
        items.Add(Ui3.Help(exact >= 0 ? "▶ 지금 값이 키 위에 있습니다 — 변형 편집으로 이 키폼을 고칠 수 있습니다." : "지금 값은 키 사이 (보간 중) — 고치려면 \"현재 값에 키 추가\"."));
        items.Add(Ui3.Help("키를 넣을 파라미터"));
        items.Add(keyUi.Select(null, rig.Params.Select(p => (p.Id, p.Name)).ToArray(), () => keyParam, v => { keyParam = v; }).box);
        items.Add(Ui3.Row(
            Ui3.Button("현재 값에 키 추가", () =>
            {
                double v = rt.Val(keyParam);
                if (g.Axis(keyParam) == null && g.Axes.Count >= KeyGrid.MaxAxes) { SetStatus($"파라미터 축은 {KeyGrid.MaxAxes}개까지입니다"); return; }
                Snapshot("키 추가");
                // 새 축이면 기본값에도 키를 두어 기본 모양을 지킴
                var prm = rig.Param(keyParam);
                if (g.Axis(keyParam) == null && prm != null && Math.Abs(prm.Def - v) > 1e-9) { g.AddAxis(keyParam, prm.Def); g.AddKey(keyParam, v, pts); }
                else g.AddKey(keyParam, v, pts);
                Changed(false); BuildKeys();
                SetStatus($"{prm?.Name ?? keyParam} = {RigRoles.Fmt(v)} 에 키를 넣었습니다");
            }, true),
            Ui3.Button("현재 값의 키 삭제", () =>
            {
                double v = rt.Val(keyParam);
                if (g.Axis(keyParam) == null) { SetStatus("이 파라미터 축이 없습니다"); return; }
                Snapshot("키 삭제");
                if (!g.RemoveKey(keyParam, v)) { undo.RemoveAt(undo.Count - 1); SetStatus("지금 값에 키가 없습니다"); return; }
                Changed(false); BuildKeys();
            })));
        if (exact >= 0)
        {
            var f = g.Forms[exact];
            var p = rig.Part(selPart); var d = rig.Def(selDef);
            items.Add(keyUi.Slider("이 키폼 불투명도", 0, 1, 0.01, () => f.Op, v => { Snapshot("불투명도", true); f.Op = v; canvas.Invalidate(); }));
            if (p != null) items.Add(keyUi.Slider("이 키폼 그리기 순서 (더함)", -500, 500, 1, () => f.Ord, v => { Snapshot("순서 키", true); f.Ord = v; canvas.Invalidate(); }));
            if (d != null && !d.IsWarp)
            {
                items.Add(keyUi.Slider("이 키폼 각도", -180, 180, 0.5, () => f.A, v => { Snapshot("각도", true); f.A = v; canvas.Invalidate(); }));
                items.Add(keyUi.Slider("이 키폼 크기", 0.2, 3, 0.01, () => f.S, v => { Snapshot("크기", true); f.S = v; canvas.Invalidate(); }));
            }
            items.Add(Ui3.Row(Ui3.Button("이 키폼 처음대로", () => { Snapshot("키폼 처음대로"); f.D = null; f.Tx = f.Ty = 0; f.Op = 1; f.Ord = 0; f.A = 0; f.S = 1; Changed(false); BuildKeys(); }),
                Ui3.Button("끌기 반경 −", () => { brush = Math.Max(4, brush / 1.4); SetStatus($"끌기 반경 {brush:0}px"); canvas.Invalidate(); }),
                Ui3.Button("+", () => { brush = Math.Min(600, brush * 1.4); SetStatus($"끌기 반경 {brush:0}px"); canvas.Invalidate(); })));
        }
        // 키폼 목록 (누르면 그 파라미터 값으로)
        if (g.Axes.Count > 0 && g.Count <= 81)
        {
            var row = Ui3.Row();
            for (int i = 0; i < g.Count; i++)
            {
                var idx = g.Unindex(i);
                string label = string.Join(", ", g.Axes.Select((a, k) => ShortName(a.Param) + "=" + RigRoles.Fmt(a.Keys[idx[k]])));
                var ii = idx;
                var b = Ui3.Button((i == exact ? "▶ " : "") + label, () => { for (int k = 0; k < g.Axes.Count; k++) rt.Values[g.Axes[k].Param] = g.Axes[k].Keys[ii[k]]; ui.SyncAll(); canvas.Invalidate(); BuildKeys(); });
                row.Controls.Add(b);
            }
            items.Add(Ui3.Help("키폼 목록 (누르면 그 값으로):"));
            items.Add(row);
        }
        keyBox.Controls.Add(keyUi.Section("키폼: " + name, items.ToArray()));
        keyBox.ResumeLayout();
    }
    static string ShortName(string id) => id.StartsWith("Param") ? id[5..] : id;

    void AddUserParam()
    {
        var v = Ui3.AskForm(this, "파라미터 만들기", "만들기",
            new Ui3.Field("min", "가장 작은 값", "number", -1.0, -1000, 1000), new Ui3.Field("max", "가장 큰 값", "number", 1.0, -1000, 1000), new Ui3.Field("def", "기본값", "number", 0.0, -1000, 1000));
        if (v == null) return;
        double mn = Convert.ToDouble(v["min"]), mx = Convert.ToDouble(v["max"]), df = Convert.ToDouble(v["def"]);
        if (mx <= mn) { SetStatus("가장 큰 값이 가장 작은 값보다 커야 합니다"); return; }
        Snapshot("파라미터 만들기");
        int n = rig.Params.Count(p => p.User) + 1;
        string id = "ParamUser" + n; while (rig.Param(id) != null) id = "ParamUser" + (++n);
        rig.Params.Add(new RParam { Id = id, Name = "사용자 파라미터 " + n, Group = "사용자", Min = mn, Max = mx, Def = Math.Clamp(df, mn, mx), User = true });
        rt.Values[id] = Math.Clamp(df, mn, mx);
        keyParam = id;
        BuildRight();
    }

    // ================================================================== 명령
    void AutoRigNow()
    {
        Snapshot("자동 리그");
        var roles = rig.Parts.Where(p => p.LayerId != null).GroupBy(p => p.LayerId).ToDictionary(g => g.Key, g => g.First().Role);
        var notes = new List<string>();
        var sw = Stopwatch.StartNew();
        var (nr, tex) = AutoRig.Build(doc, roles.Count > 0 ? roles : null, notes);
        // 사용자 파라미터는 남김
        foreach (var up in rig.Params.Where(p => p.User)) if (nr.Param(up.Id) == null) nr.Params.Add(up.Clone());
        rig = nr; rt.Rig = rig;
        foreach (var (k, t) in tex) rt.Tex[k] = t;
        rt.Reset(); rt.Phys.Clear();
        rt.Compile();
        selPart = selDef = null;
        BuildTree(); BuildProps(); BuildRight(); Changed(false);
        SetStatus($"자동 리그 ({sw.ElapsedMilliseconds}ms): " + string.Join(" · ", notes) + " — 되돌리기로 이전 상태로 갈 수 있습니다");
    }

    void ReloadTextures()
    {
        rt.Tex.Clear(); rt.LoadTextures(doc); rt.Compile(); canvas.Invalidate();
        SetStatus("레이어 그림을 다시 읽었습니다 (그림이 메쉬 밖으로 나갔으면 \"메쉬 다시 만들기\")");
    }

    void OpenStudio()
    {
        Apply();
        if (rig.Parts.Count == 0) { SetStatus("리그가 비어 있습니다. 먼저 \"자동 리그\"를 누르세요"); return; }
        Close();
        App.Main?.BeginInvoke(() => Vtuber.Studio());
    }

    static string ExportsDir { get { var p = Path.Combine(App.DataDir, "exports"); Directory.CreateDirectory(p); return p; } }
    static string SafeName(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n.Trim().Length == 0 ? "버튜버" : n; }

    void ExportMotion()
    {
        if (rig.Parts.Count == 0) { SetStatus("리그가 비어 있습니다"); return; }
        var v = Ui3.AskForm(this, "움직임 내보내기 (고개 돌리기·깜빡임·말하기·숨쉬기)", "내보내기",
            new Ui3.Field("fmt", "형식", "select", "gif", Options: new[] { ("gif", "GIF (투명 바탕)"), ("mp4", "MP4 (H.264)"), ("webm", "WebM (VP8)") }),
            new Ui3.Field("sec", "길이 (초)", "number", 6.0, 1, 60), new Ui3.Field("size", "긴 변 (px)", "number", 720.0, 128, 1920));
        if (v == null) return;
        string fmt = v["fmt"] as string ?? "gif";
        string path = Path.Combine(ExportsDir, $"{SafeName(doc.Name)}_리그움직임_{DateTime.Now:yyyyMMdd-HHmmss}.{fmt}");
        try
        {
            Cursor = Cursors.WaitCursor;
            SetStatus("내보내는 중…");
            RigExport.Export(rt, fmt, path, (int)Convert.ToDouble(v["size"]), Convert.ToDouble(v["sec"]), fmt == "gif" ? null : Color.White, null, f => { SetStatus($"내보내는 중… {f * 100:0}%"); Application.DoEvents(); });
            SetStatus($"저장: data/exports/{Path.GetFileName(path)}");
            App.Toast($"저장: data/exports/{Path.GetFileName(path)}");
        }
        catch (Exception e) { SetStatus("내보내지 못했습니다: " + e.Message); }
        finally { Cursor = Cursors.Default; ui.SyncAll(); canvas.Invalidate(); }
    }

    void SaveCpvt()
    {
        if (rig.Parts.Count == 0) { SetStatus("리그가 비어 있습니다"); return; }
        var player = new RigPlayer(rt);
        var data = VRig.BuildRig(player.LegacyParts(), player.W, player.H, (doc.Extra["vtuber"] as JsonObject)?["opts"] as JsonObject);
        data.Alt = player;
        var json = VRig.ToFile(data, doc.Name);
        foreach (var p in data.Parts) p.Img?.Dispose();
        string name = $"{SafeName(doc.Name)}_{DateTime.Now:yyyyMMdd-HHmmss}.cpvt";
        File.WriteAllText(Path.Combine(ExportsDir, name), json.ToJsonString());
        SetStatus($"아바타 파일 저장: data/exports/{name} (메쉬 리그 포함)");
    }

    // ================================================================== 캔버스
    internal sealed class RigCanvas : Control
    {
        readonly RigEditorForm f;
        Bitmap frame;
        double zoom = 1, panX, panY;
        Point dragStart; bool panning;
        // 편집 끌기
        bool editing; PointF last;
        int hoverVert = -1, dragVert = -1;
        List<(int idx, double w)> grabbed;
        string grabKind;            // vert | node | pivot | handle | meshvert
        double handleStartAng, formStartA;

        public RigCanvas(RigEditorForm f)
        {
            this.f = f;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.StageBg;
        }

        (double s, double ox, double oy) View()
        {
            var r = f.rig;
            double fit = Math.Min((Width - 20.0) / Math.Max(1, r.W), (Height - 20.0) / Math.Max(1, r.H));
            double s = fit * zoom;
            return (s, Width / 2.0 - r.W * s / 2 + panX, Height / 2.0 - r.H * s / 2 + panY);
        }
        PointF ToScreen(double x, double y) { var (s, ox, oy) = View(); return new PointF((float)(x * s + ox), (float)(y * s + oy)); }
        (double x, double y) ToDoc(double sx, double sy) { var (s, ox, oy) = View(); return ((sx - ox) / s, (sy - oy) / s); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Width < 4 || Height < 4) return;
            var sw = Stopwatch.StartNew();
            var rt = f.rt;
            // 메쉬 편집은 쉼 자세로
            Dictionary<string, double> saved = null;
            if (f.mode == "mesh") { saved = new Dictionary<string, double>(rt.Values); rt.Reset(); }
            rt.Evaluate();
            var (s, ox, oy) = View();
            // 작품 범위 바둑판
            var docR = new RectangleF((float)ox, (float)oy, (float)(f.rig.W * s), (float)(f.rig.H * s));
            using (var a = new SolidBrush(Color.FromArgb(205, 205, 210))) using (var b = new SolidBrush(Color.FromArgb(235, 235, 240)))
            {
                var clip = RectangleF.Intersect(docR, ClientRectangle);
                g.SetClip(clip);
                for (float y = docR.Top; y < docR.Bottom; y += 16) for (float x = docR.Left; x < docR.Right; x += 16)
                    if (x + 16 >= clip.Left && x <= clip.Right && y + 16 >= clip.Top && y <= clip.Bottom)
                        g.FillRectangle(((int)((x - docR.Left) / 16) + (int)((y - docR.Top) / 16)) % 2 == 0 ? a : b, x, y, 16, 16);
                g.ResetClip();
            }
            if (frame == null || frame.Width != Width || frame.Height != Height) { frame?.Dispose(); frame = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb); }
            rt.Render(f.ren, Width, Height, s, ox, oy, 0);
            f.ren.CopyTo(frame);
            g.DrawImageUnscaled(frame, 0, 0);
            f.renderMs = sw.Elapsed.TotalMilliseconds;
            if (f.showMesh) DrawOverlay(g);
            if (saved != null) { rt.Values.Clear(); foreach (var (k, v) in saved) rt.Values[k] = v; }
            // fps
            f.frames++;
            double now = f.clock.Elapsed.TotalSeconds;
            if (now - f.fpsT >= 1) { f.fps = f.frames / (now - f.fpsT); f.frames = 0; f.fpsT = now; }
            using var fnt = new Font("Malgun Gothic", 8.5f);
            using var br = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            g.DrawString($"{f.ren.Name} 렌더 {f.renderMs:0.0}ms · {Width}×{Height} · {f.fps:0}fps · 확대 {zoom * 100:0}%", fnt, br, 8, Height - 20);
        }

        void DrawOverlay(Graphics g)
        {
            var rt = f.rt;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = f.rig.Part(f.selPart);
            if (p != null)
            {
                int pi = rt.PartIndex(p.Id);
                if (pi >= 0)
                {
                    var pos = rt.PS[pi].Pos;
                    using var pen = new Pen(Color.FromArgb(150, 40, 140, 255), 1);
                    var T = p.Mesh.T;
                    for (int t = 0; t + 2 < T.Length; t += 3)
                    {
                        var a = ToScreen(pos[T[t] * 2], pos[T[t] * 2 + 1]); var b = ToScreen(pos[T[t + 1] * 2], pos[T[t + 1] * 2 + 1]); var c = ToScreen(pos[T[t + 2] * 2], pos[T[t + 2] * 2 + 1]);
                        g.DrawPolygon(pen, new[] { a, b, c });
                    }
                    if (f.mode != "view")
                    {
                        using var vb = new SolidBrush(Color.FromArgb(230, 255, 255, 255)); using var vh = new SolidBrush(Theme.Accent);
                        for (int k = 0; k < p.Mesh.N; k++)
                        {
                            var q = ToScreen(pos[k * 2], pos[k * 2 + 1]);
                            g.FillEllipse(k == hoverVert ? vh : vb, q.X - 2.5f, q.Y - 2.5f, 5, 5);
                        }
                    }
                }
            }
            var d = f.rig.Def(f.selDef);
            if (d != null)
            {
                int di = rt.DefIndex(d.Id);
                if (di >= 0 && d.IsWarp)
                {
                    var n = rt.WarpNodes(di);
                    using var pen = new Pen(Color.FromArgb(220, 60, 170, 255), 1.4f);
                    int C = d.Cols + 1;
                    for (int r = 0; r <= d.Rows; r++) for (int c = 0; c <= d.Cols; c++)
                    {
                        int k = r * C + c; var a = ToScreen(n[k * 2], n[k * 2 + 1]);
                        if (c < d.Cols) g.DrawLine(pen, a, ToScreen(n[(k + 1) * 2], n[(k + 1) * 2 + 1]));
                        if (r < d.Rows) g.DrawLine(pen, a, ToScreen(n[(k + C) * 2], n[(k + C) * 2 + 1]));
                    }
                    using var nb = new SolidBrush(Color.FromArgb(240, 60, 170, 255)); using var nh = new SolidBrush(Theme.Accent);
                    for (int k = 0; k < d.Nodes; k++) { var a = ToScreen(n[k * 2], n[k * 2 + 1]); g.FillRectangle(k == hoverVert ? nh : nb, a.X - 3.5f, a.Y - 3.5f, 7, 7); }
                }
                else if (di >= 0)
                {
                    var (px, py, hx, hy) = rt.RotHandle(di);
                    var a = ToScreen(px, py); var b = ToScreen(hx, hy);
                    using var pen = new Pen(Color.FromArgb(240, 255, 170, 60), 2);
                    g.DrawLine(pen, a, b);
                    g.DrawEllipse(pen, a.X - 7, a.Y - 7, 14, 14);
                    using var hb = new SolidBrush(Color.FromArgb(240, 255, 170, 60));
                    g.FillEllipse(hb, b.X - 5, b.Y - 5, 10, 10);
                }
            }
            if (f.mode == "deform" && (p != null || (d != null && d.IsWarp)))
            {
                var m = PointToClient(Cursor.Position);
                if (ClientRectangle.Contains(m)) using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255)) { DashStyle = DashStyle.Dash }) g.DrawEllipse(pen, (float)(m.X - f.brush), (float)(m.Y - f.brush), (float)(f.brush * 2), (float)(f.brush * 2));
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var (x, y) = ToDoc(e.X, e.Y);
            zoom = Math.Clamp(zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.2, 20);
            var (s, ox, oy) = View();
            // 누른 곳이 그대로 있게
            panX += e.X - (x * s + ox); panY += e.Y - (y * s + oy);
            Invalidate();
        }

        /// <summary>화면 점에 가장 가까운 점 (반경 안)</summary>
        int Nearest(double[] pos, int n, Point m, double r)
        {
            int best = -1; double bd = r * r;
            for (int k = 0; k < n; k++) { var q = ToScreen(pos[k * 2], pos[k * 2 + 1]); double dd = (q.X - m.X) * (q.X - m.X) + (q.Y - m.Y) * (q.Y - m.Y); if (dd < bd) { bd = dd; best = k; } }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            dragStart = e.Location; last = e.Location;
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Right && f.mode != "mesh")) { panning = true; return; }
            var rt = f.rt;
            if (f.mode == "mesh")
            {
                var p = f.rig.Part(f.selPart);
                if (p == null) { PickPart(e.Location); return; }
                int pi = rt.PartIndex(p.Id);
                int k = Nearest(rt.PS[pi].Pos, p.Mesh.N, e.Location, 7);
                if (e.Button == MouseButtons.Right) { if (k >= 0) { hoverVert = k; DeleteHover(); } return; }
                if (e.Button != MouseButtons.Left) return;
                if ((ModifierKeys & Keys.Shift) != 0)
                {
                    var (x, y) = ToDoc(e.X, e.Y);
                    f.Snapshot("점 넣기");
                    var old = p.Mesh.Clone();
                    var nv = p.Mesh.V.Concat(new[] { Math.Round(x, 2), Math.Round(y, 2) }).ToArray();
                    Remesh(p, old, nv);
                    return;
                }
                if (k >= 0) { f.Snapshot("점 옮기기"); dragVert = k; editing = true; grabKind = "meshvert"; }
                else PickPart(e.Location);
                return;
            }
            if (f.mode == "deform" && e.Button == MouseButtons.Left)
            {
                var g = f.SelGrid(out _, out _);
                if (g != null)
                {
                    int ex = g.ExactIndex(rt.Val);
                    if (ex < 0) { f.SetStatus("지금 파라미터 값에 키가 없습니다 — 오른쪽에서 \"현재 값에 키 추가\"를 누르세요"); return; }
                    if (StartDeform(e.Location, g.Forms[ex])) { f.Snapshot("변형"); editing = true; return; }
                }
                PickPart(e.Location);
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                if (!PickPart(e.Location)) panning = true;
            }
        }

        KForm editForm;
        bool StartDeform(Point m, KForm form)
        {
            var rt = f.rt;
            editForm = form;
            var p = f.rig.Part(f.selPart); var d = f.rig.Def(f.selDef);
            if (p != null)
            {
                int pi = rt.PartIndex(p.Id); var pos = rt.PS[pi].Pos;
                grabbed = new List<(int, double)>();
                for (int k = 0; k < p.Mesh.N; k++)
                {
                    var q = ToScreen(pos[k * 2], pos[k * 2 + 1]);
                    double dd = Math.Sqrt((q.X - m.X) * (q.X - m.X) + (q.Y - m.Y) * (q.Y - m.Y));
                    if (dd < f.brush) { double w = 1 - dd / f.brush; grabbed.Add((k, w * w * (3 - 2 * w))); }
                }
                if (grabbed.Count == 0) { int k = Nearest(pos, p.Mesh.N, m, 12); if (k >= 0) grabbed.Add((k, 1)); }
                grabKind = "vert";
                if (grabbed.Count > 0 && form.D == null) form.D = new double[p.Mesh.N * 2];
                return grabbed.Count > 0;
            }
            if (d != null && d.IsWarp)
            {
                int di = rt.DefIndex(d.Id); var n = rt.WarpNodes(di);
                grabbed = new List<(int, double)>();
                int near = Nearest(n, d.Nodes, m, 10);
                if (near >= 0) grabbed.Add((near, 1));
                else
                    for (int k = 0; k < d.Nodes; k++)
                    {
                        var q = ToScreen(n[k * 2], n[k * 2 + 1]);
                        double dd = Math.Sqrt((q.X - m.X) * (q.X - m.X) + (q.Y - m.Y) * (q.Y - m.Y));
                        if (dd < f.brush) { double w = 1 - dd / f.brush; grabbed.Add((k, w * w * (3 - 2 * w))); }
                    }
                grabKind = "node";
                if (grabbed.Count > 0 && form.D == null) form.D = new double[d.Nodes * 2];
                return grabbed.Count > 0;
            }
            if (d != null)
            {
                int di = rt.DefIndex(d.Id);
                var (px, py, hx, hy) = rt.RotHandle(di);
                var a = ToScreen(px, py); var b = ToScreen(hx, hy);
                if (Math.Abs(b.X - m.X) < 9 && Math.Abs(b.Y - m.Y) < 9) { grabKind = "handle"; handleStartAng = Math.Atan2(m.X - a.X, -(m.Y - a.Y)); formStartA = form.A; return true; }
                if (Math.Abs(a.X - m.X) < 10 && Math.Abs(a.Y - m.Y) < 10) { grabKind = "pivot"; return true; }
            }
            return false;
        }

        void Remesh(RPart p, RMesh old, double[] nv)
        {
            if (!f.rt.Tex.TryGetValue(p.LayerId ?? "", out var t)) return;
            var tris = MeshGen.Retriangulate(nv, t, p.Region);
            var m = new RMesh { V = nv, T = tris };
            p.Grid.RemapPoints(dd => MeshGen.TransferOffsets(old, dd, nv));
            p.Mesh = m;
            f.Changed(true);
            Invalidate();
        }
        internal void DeleteHover()
        {
            var p = f.rig.Part(f.selPart);
            if (p == null || hoverVert < 0 || hoverVert >= p.Mesh.N) return;
            f.Snapshot("점 빼기");
            var old = p.Mesh.Clone();
            var nv = new List<double>();
            for (int k = 0; k < p.Mesh.N; k++) if (k != hoverVert) { nv.Add(p.Mesh.V[k * 2]); nv.Add(p.Mesh.V[k * 2 + 1]); }
            hoverVert = -1;
            Remesh(p, old, nv.ToArray());
        }

        bool PickPart(Point m)
        {
            var rt = f.rt;
            var (x, y) = ToDoc(m.X, m.Y);
            for (int oi = rt.DrawOrder.Length - 1; oi >= 0; oi--)
            {
                int i = rt.DrawOrder[oi];
                var p = f.rig.Parts[i]; var s = rt.PS[i];
                if (s.Op <= 0.02 || p.Hide) continue;
                var T = p.Mesh.T; var pos = s.Pos;
                for (int t = 0; t + 2 < T.Length; t += 3)
                {
                    double ax = pos[T[t] * 2], ay = pos[T[t] * 2 + 1], bx = pos[T[t + 1] * 2], by = pos[T[t + 1] * 2 + 1], cx = pos[T[t + 2] * 2], cy = pos[T[t + 2] * 2 + 1];
                    double d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by), d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy), d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
                    bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos2 = d1 > 0 || d2 > 0 || d3 > 0;
                    if (neg && pos2) continue;
                    // 그 자리 텍스처가 비었으면 건너뜀
                    if (rt.Tex.TryGetValue(p.LayerId ?? "", out var tex))
                    {
                        // 쉼 좌표로 (무게 중심)
                        double den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                        if (Math.Abs(den) < 1e-9) continue;
                        double l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / den, l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / den, l3 = 1 - l1 - l2;
                        var V = p.Mesh.V;
                        double rx = l1 * V[T[t] * 2] + l2 * V[T[t + 1] * 2] + l3 * V[T[t + 2] * 2], ry = l1 * V[T[t] * 2 + 1] + l2 * V[T[t + 1] * 2 + 1] + l3 * V[T[t + 2] * 2 + 1];
                        int tx = (int)(rx - tex.X), ty = (int)(ry - tex.Y);
                        if ((uint)tx >= (uint)tex.W || (uint)ty >= (uint)tex.H || tex.Mip[0][ty * tex.W + tx] >> 24 < 16) continue;
                    }
                    f.Select(p.Id, null);
                    return true;
                }
            }
            return false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var rt = f.rt;
            if (panning) { panX += e.X - last.X; panY += e.Y - last.Y; last = e.Location; Invalidate(); return; }
            double sdx = e.X - last.X, sdy = e.Y - last.Y;
            last = e.Location;
            var (s, _, _) = View();
            if (editing && grabKind == "meshvert")
            {
                var p = f.rig.Part(f.selPart);
                if (p != null && dragVert >= 0 && dragVert < p.Mesh.N) { p.Mesh.V[dragVert * 2] += sdx / s; p.Mesh.V[dragVert * 2 + 1] += sdy / s; f.rt.RefreshUv(rt.PartIndex(p.Id)); Invalidate(); }
                return;
            }
            if (editing && editForm != null)
            {
                var p = f.rig.Part(f.selPart); var d = f.rig.Def(f.selDef);
                if (grabKind == "vert" && p != null)
                {
                    int par = rt.PartParent(rt.PartIndex(p.Id));
                    foreach (var (k, w) in grabbed)
                    {
                        double lx = p.Mesh.V[k * 2] + editForm.Tx + editForm.D[k * 2], ly = p.Mesh.V[k * 2 + 1] + editForm.Ty + editForm.D[k * 2 + 1];
                        var (dx, dy) = rt.Unmap(par, lx, ly, sdx / s * w, sdy / s * w);
                        editForm.D[k * 2] += dx; editForm.D[k * 2 + 1] += dy;
                    }
                }
                else if (grabKind == "node" && d != null)
                {
                    int par = rt.ParentOf(rt.DefIndex(d.Id));
                    foreach (var (k, w) in grabbed)
                    {
                        var (nx, ny) = d.RestNode(k);
                        var (dx, dy) = rt.Unmap(par, nx + editForm.D[k * 2], ny + editForm.D[k * 2 + 1], sdx / s * w, sdy / s * w);
                        editForm.D[k * 2] += dx; editForm.D[k * 2 + 1] += dy;
                    }
                }
                else if (grabKind == "pivot" && d != null)
                {
                    int par = rt.ParentOf(rt.DefIndex(d.Id));
                    var (dx, dy) = rt.Unmap(par, d.Px + editForm.Tx, d.Py + editForm.Ty, sdx / s, sdy / s);
                    editForm.Tx += dx; editForm.Ty += dy;
                }
                else if (grabKind == "handle" && d != null)
                {
                    var (px, py, _, _) = rt.RotHandle(rt.DefIndex(d.Id));
                    var a = ToScreen(px, py);
                    double ang = Math.Atan2(e.X - a.X, -(e.Y - a.Y));
                    double delta = (ang - handleStartAng) * 180 / Math.PI;
                    while (delta > 180) delta -= 360; while (delta < -180) delta += 360;
                    editForm.A = Math.Round(formStartA + delta, 2);
                }
                Invalidate();
                return;
            }
            // 가까운 점 표시
            if (f.mode != "view")
            {
                int old = hoverVert; hoverVert = -1;
                var p = f.rig.Part(f.selPart); var d = f.rig.Def(f.selDef);
                if (p != null) { int pi = rt.PartIndex(p.Id); if (pi >= 0) hoverVert = Nearest(rt.PS[pi].Pos, p.Mesh.N, e.Location, 7); }
                else if (d != null && d.IsWarp) { int di = rt.DefIndex(d.Id); if (di >= 0) hoverVert = Nearest(rt.WarpNodes(di), d.Nodes, e.Location, 10); }
                if (old != hoverVert || f.mode == "deform") Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            panning = false;
            if (editing)
            {
                editing = false; dragVert = -1; editForm = null; grabbed = null;
                f.BuildKeys();
                if (f.mode == "mesh") f.BuildProps();
            }
        }
    }
}
