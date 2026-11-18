// 참고 자료 보드 창 (항상 위로 둘 수 있음): 그림 끌어 놓기·붙여넣기·화면 캡처, 자유 배치(옮기기·크기·회전·자르기), 묶기, 메모,
// 스포이트로 색 따기, 보드 좌우 반전·흑백 보기, 그림을 그림판(캔버스)으로 끌어 놓으면 새 레이어.
//  조작: 끌기 = 옮기기 · 모서리 = 크기 · 위 동그라미 = 회전(Shift 15°) · 빈 곳 끌기 = 여러 개 고르기 · 가운데/오른쪽 끌기·Space = 화면 이동 · 휠 = 확대
//        Alt+누르기 또는 스포이트(I) = 색 따기 · C = 자르기 · Ctrl+G/Ctrl+Shift+G = 묶기/풀기 · Del = 지우기 · Ctrl+V = 붙여넣기 · F = 맞춤
//        그림을 창 밖(그림판)으로 끌고 나가면 그 자리에 새 레이어
using System.Drawing.Drawing2D;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.Modules.Workflow;

public sealed class RefBoardWindow : WfForm
{
    static RefBoardWindow inst;
    public static RefBoardWindow Instance => inst != null && !inst.IsDisposed ? inst : null;
    public readonly RefCanvas View;
    readonly WebRow bar = new() { Dock = DockStyle.Top, Padding = new Padding(8, 6, 8, 4), BackColor = Theme.Panel };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = Theme.Muted, BackColor = Theme.Panel, Padding = new Padding(8, 4, 0, 0), Font = Web.Small };
    bool global;
    WebToggle onTop;

    public static RefBoardWindow Open(bool? globalBoard = null)
    {
        if (Instance == null) { inst = new RefBoardWindow(globalBoard ?? false); inst.Show(App.Main); }
        else { if (globalBoard != null && globalBoard != inst.global) inst.SwitchBoard(globalBoard.Value); inst.Activate(); }
        return inst;
    }

    public RefBoardWindow(bool globalBoard = false) : base("참고 자료 보드 — ComicPad", 820, 640)
    {
        global = globalBoard || RefStore.KeyFor(App.Doc) == "_global";
        View = new RefCanvas { Dock = DockStyle.Fill };
        View.Board = LoadBoard();
        View.Status = s => status.Text = s;
        TopMost = App.Get("refBoardOnTop", true);
        if (App.Main != null && App.Main.Visible) { var r = App.Main.Bounds; Location = new Point(Math.Max(r.Left, r.Right - Width - 340), r.Top + 90); }
        BuildBar();
        Controls.Add(View); Controls.Add(bar); Controls.Add(status);
        status.Text = "그림을 끌어다 놓거나 Ctrl+V 로 붙여넣으세요. 그림을 그림판으로 끌고 나가면 새 레이어가 됩니다.";
        App.DocChanged += OnDoc;
        App.ColorChanged += OnColor;
    }

    RefBoard LoadBoard()
    {
        string key = global ? "_global" : RefStore.KeyFor(App.Doc);
        return RefStore.Load(key, key == "_global" ? "공용 보드" : RefStore.TitleFor(App.Doc));
    }
    void UpdateTitle() => Text = $"참고 자료 보드: {View.Board.Name} — ComicPad";

    public void SwitchBoard(bool toGlobal)
    {
        View.SaveNow();
        global = toGlobal;
        View.Board = LoadBoard();
        View.FitIfEmptyView();
        UpdateTitle(); BuildBar();
    }
    void OnDoc()
    {
        if (IsDisposed || global) return;
        string key = RefStore.KeyFor(App.Doc);
        if (key == View.Board.Key) return;
        View.SaveNow();
        View.Board = LoadBoard();
        UpdateTitle(); BuildBar();
    }
    void OnColor() { if (!IsDisposed) View.Invalidate(new Rectangle(0, 0, 1, 1)); }

    void BuildBar()
    {
        bar.SuspendLayout();
        foreach (Control c in bar.Controls.Cast<Control>().ToList()) c.Dispose();
        bar.Controls.Clear();
        bool hasWork = RefStore.KeyFor(App.Doc) != "_global";
        var seg = Opt.Seg(new[] { (false, hasWork ? "이 작품" : "이 작품 (없음)"), (true, "공용") }, () => global, v => { if (v || hasWork) SwitchBoard(v); });
        seg.Width = 170; seg.Margin = new Padding(0, 0, 6, 0);
        bar.Controls.Add(seg);
        WebButton B(string text, string icon, string tip, Action a, Func<bool> on = null)
        {
            var b = new WebButton(text, BtnKind.Normal, icon) { IconSize = 16, Margin = new Padding(0, 0, 4, 0) };
            if (tip != null) new ToolTip().SetToolTip(b, tip);
            b.Click += (_, _) => { a(); BuildBar(); };
            if (on?.Invoke() == true) b.BackColor = Theme.AccentDimOnPanel;
            return b;
        }
        bar.Controls.Add(B("추가", "image", "그림 파일 추가 (끌어 놓아도 됨)", View.AddFiles));
        bar.Controls.Add(B("붙여넣기", null, "Ctrl+V: 클립보드 그림·파일·글", View.Paste));
        bar.Controls.Add(B("캡처", null, "화면 일부를 잘라 넣기", () => View.CaptureScreen(this)));
        bar.Controls.Add(B("메모", "memo", "메모 추가", View.AddNote));
        bar.Controls.Add(B("스포이트", "eyedropper", "I: 누른 곳의 색을 전경색으로 (Alt+누르기도)", () => View.Mode = View.Mode == "pick" ? "" : "pick", () => View.Mode == "pick"));
        bar.Controls.Add(B("자르기", null, "C: 고른 그림의 가장자리를 끌어 자르기", () => View.Mode = View.Mode == "crop" ? "" : "crop", () => View.Mode == "crop"));
        bar.Controls.Add(B("반전", "flip", "보드 전체 좌우 반전 보기", () => { View.Board.ViewFlip = !View.Board.ViewFlip; View.Changed(); }, () => View.Board.ViewFlip));
        bar.Controls.Add(B("흑백", null, "보드 전체 흑백 보기 (명암 확인)", () => { View.Board.ViewGray = !View.Board.ViewGray; View.Changed(); }, () => View.Board.ViewGray));
        bar.Controls.Add(B("맞춤", "fit", "F: 모두 보이게", View.FitAll));
        bar.Controls.Add(B("레이어로", "layers", "고른 그림을 지금 그림의 새 레이어로", () => View.SelectedToCanvas(null)));
        onTop = new WebToggle("항상 위") { Checked = TopMost, Margin = new Padding(6, 6, 0, 0) };
        onTop.CheckedChanged += (_, _) => { TopMost = onTop.Checked; App.Set("refBoardOnTop", onTop.Checked); };
        bar.Controls.Add(onTop);
        bar.ResumeLayout();
    }

    protected override bool HandleKey(Keys k) => View.Key(k);

    protected override void OnShown(EventArgs e) { base.OnShown(e); View.FitIfEmptyView(); UpdateTitle(); }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        View.SaveNow();
        App.DocChanged -= OnDoc; App.ColorChanged -= OnColor;
        if (inst == this) inst = null;
        base.OnFormClosed(e);
    }
}

/// <summary>보드 그림판</summary>
public sealed class RefCanvas : Control
{
    public const string DragFormat = "ComicPadRefItem";
    RefBoard board = new();
    public RefBoard Board { get => board; set { board = value ?? new RefBoard(); sel.Clear(); Invalidate(); } }
    public readonly List<RefItem> sel = new();
    public string Mode = "";              // "" | pick | crop
    public Action<string> Status;
    public Color? Picked;
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 700 };

    // 끌기 상태
    enum Drag { None, Move, Scale, Rotate, Crop, Marquee, Pan }
    Drag drag;
    Point down, last;
    PointF downBoard;
    List<(RefItem it, double x, double y, double s, double r)> orig;
    RectangleF cropOrig; int cropEdge;
    bool spaceDown, outDragged;
    Rectangle marquee;

    public RefCanvas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AllowDrop = true; TabStop = true;
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
        DragEnter += (_, e) => e.Effect = e.Data != null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) || e.Data.GetDataPresent(DataFormats.UnicodeText)) && !e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { var p = PointToClient(new Point(e.X, e.Y)); DropData(e.Data, ToBoard(p)); };
    }

    // ---- 좌표 ------------------------------------------------------------------------------
    Matrix V() => RefOps.ViewMatrix(board, Width);
    public PointF ToBoard(Point p) { using var m = V(); m.Invert(); var a = new[] { new PointF(p.X, p.Y) }; m.TransformPoints(a); return a[0]; }
    PointF ToScreen(PointF b) { using var m = V(); var a = new[] { b }; m.TransformPoints(a); return a[0]; }
    PointF CenterBoard => ToBoard(new Point(Width / 2, Height / 2));

    public void Changed() { Invalidate(); saveTimer.Stop(); saveTimer.Start(); }
    public void SaveNow() { saveTimer.Stop(); try { RefStore.Save(board); } catch (Exception ex) { App.Toast("보드를 저장하지 못했습니다: " + ex.Message); } }
    public void FitAll() { RefOps.Fit(board, Width, Height); Changed(); }
    public void FitIfEmptyView() { if (board.Items.Count > 0 && !board.Items.Any(i => RefOps.Bounds(i, V()).IntersectsWith(ClientRectangle))) FitAll(); }

    // ---- 추가 ------------------------------------------------------------------------------
    int addSeq;
    PointF NextSpot() { var c = CenterBoard; float o = (addSeq++ % 8) * 24 / (float)board.Zoom; return new PointF(c.X + o, c.Y + o); }
    public RefItem Add(RefItem it)
    {
        if (it == null) return null;
        board.Items.Add(it);
        sel.Clear(); sel.Add(it);
        Changed();
        return it;
    }
    public void AddFiles()
    {
        using var dlg = new OpenFileDialog { Filter = "그림|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|모든 파일|*.*", Multiselect = true };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) { var p = NextSpot(); Add(RefOps.FromFile(f, p.X, p.Y)); }
    }
    public void AddNote()
    {
        var p = NextSpot();
        var it = Add(new RefItem { Kind = "note", X = p.X, Y = p.Y, Scale = 1 / Math.Max(0.05, board.Zoom) * 0.8, Text = "" });
        EditNote(it);
    }
    void EditNote(RefItem it)
    {
        var v = Dlg.Form("보드 메모", new[]
        {
            Field.Area("text", "내용", it.Text),
            Field.Select("color", "색", it.Color, ("#fff3a6", "노랑"), ("#ffd1dc", "분홍"), ("#c8f0d0", "초록"), ("#cfe3ff", "파랑"), ("#ffffff", "흰색")),
        }, "저장");
        if (v == null) return;
        it.Text = (string)v["text"] ?? ""; it.Color = (string)v["color"] ?? it.Color;
        Changed();
    }

    public void Paste()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) { foreach (var f in Clipboard.GetFileDropList()) { var p = NextSpot(); Add(RefOps.FromFile(f, p.X, p.Y)); } return; }
            if (Clipboard.ContainsImage()) { using var img = new Bitmap(Clipboard.GetImage()); var p = NextSpot(); Add(RefOps.FromBitmap(img, "붙여넣기 " + DateTime.Now.ToString("HHmmss"), p.X, p.Y)); return; }
            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText().Trim();
                var p = NextSpot();
                if (File.Exists(t)) { Add(RefOps.FromFile(t, p.X, p.Y)); return; }
                Add(new RefItem { Kind = "note", X = p.X, Y = p.Y, Text = t, Scale = 1 / Math.Max(0.05, board.Zoom) * 0.8 });
                return;
            }
            App.Toast("붙여넣을 그림이 없습니다");
        }
        catch (Exception ex) { App.Toast("붙여넣지 못했습니다: " + ex.Message); }
    }

    void DropData(IDataObject data, PointF at)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            int i = 0;
            foreach (var f in files) { var it = RefOps.FromFile(f, at.X + i * 30 / board.Zoom, at.Y + i * 30 / board.Zoom); if (it != null) { Add(it); i++; } }
            if (i == 0) App.Toast("그림 파일이 아닙니다");
            return;
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is Image im) { using var b = new Bitmap(im); Add(RefOps.FromBitmap(b, "끌어 놓기", at.X, at.Y)); return; }
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string t && t.Trim().Length > 0)
            Add(new RefItem { Kind = "note", X = at.X, Y = at.Y, Text = t.Trim(), Scale = 1 / Math.Max(0.05, board.Zoom) * 0.8 });
    }

    /// <summary>화면 일부 캡처해서 넣기 (보드 창은 잠깐 숨김)</summary>
    public void CaptureScreen(Form owner)
    {
        double op = owner.Opacity;
        owner.Opacity = 0;
        Application.DoEvents(); Thread.Sleep(220);
        Bitmap shot = null;
        try { shot = ScreenCapture.Grab(); }
        finally { owner.Opacity = op; }
        if (shot == null) return;
        using (shot)
        {
            var crop = ScreenCapture.PickRegion(shot);
            if (crop == null) return;
            using (crop) { var p = NextSpot(); Add(RefOps.FromBitmap(crop, "캡처 " + DateTime.Now.ToString("HHmmss"), p.X, p.Y)); }
        }
        owner.Activate();
    }

    // ---- 명령 ------------------------------------------------------------------------------
    public void DeleteSelected() { if (sel.Count == 0) return; foreach (var i in RefOps.Expand(board, sel)) board.Items.Remove(i); sel.Clear(); Changed(); }
    public void SelectedToCanvas(Point? screenAt)
    {
        var d = App.Doc;
        if (d == null) { App.Toast("열린 그림이 없습니다"); return; }
        var it = sel.LastOrDefault(i => i.Kind == "image") ?? sel.LastOrDefault();
        if (it == null) { App.Toast("보드에서 그림을 먼저 고르세요"); return; }
        double cx = d.W / 2.0, cy = d.H / 2.0;
        var cv = U.Canvas;
        if (cv != null) { var c = screenAt is Point sp ? cv.PointToClient(sp) : new Point(cv.Width / 2, cv.Height / 2); (cx, cy) = cv.V.ToDoc(c.X, c.Y); }
        var l = RefOps.ToLayer(d, it, cx, cy);
        if (l != null) { App.Toast($"새 레이어: {l.Name}"); if (Registry.Tools.Any(t => t.Id == "transform")) App.SetTool("transform"); }
    }
    void Order(int dir)
    {
        foreach (var it in (dir > 0 ? sel.AsEnumerable().Reverse() : sel).ToList())
        {
            board.Items.Remove(it);
            if (dir > 0) board.Items.Add(it); else board.Items.Insert(0, it);
        }
        Changed();
    }

    public bool Key(Keys k)
    {
        switch (k)
        {
            case Keys.Delete: case Keys.Back: DeleteSelected(); return true;
            case Keys.Control | Keys.V: Paste(); return true;
            case Keys.Control | Keys.A: sel.Clear(); sel.AddRange(board.Items); Invalidate(); return true;
            case Keys.Control | Keys.G: if (RefOps.GroupItems(RefOps.Expand(board, sel)) != null) { Status?.Invoke("묶었습니다"); Changed(); } return true;
            case Keys.Control | Keys.Shift | Keys.G: RefOps.Ungroup(RefOps.Expand(board, sel)); Status?.Invoke("묶음을 풀었습니다"); Changed(); return true;
            case Keys.I: Mode = Mode == "pick" ? "" : "pick"; Invalidate(); Status?.Invoke(Mode == "pick" ? "스포이트: 누른 곳의 색을 전경색으로" : ""); return true;
            case Keys.C: Mode = Mode == "crop" ? "" : "crop"; Invalidate(); Status?.Invoke(Mode == "crop" ? "자르기: 고른 그림의 가장자리 네모를 끄세요 (다시 C = 끝)" : ""); return true;
            case Keys.F: FitAll(); return true;
            case Keys.H: foreach (var i in sel) i.FlipH = !i.FlipH; Changed(); return true;
            case Keys.Escape: if (Mode != "") { Mode = ""; Invalidate(); return true; } sel.Clear(); Invalidate(); return true;
            case Keys.Space: spaceDown = true; Cursor = Cursors.SizeAll; return true;
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
            case Keys.Shift | Keys.Left: case Keys.Shift | Keys.Right: case Keys.Shift | Keys.Up: case Keys.Shift | Keys.Down:
            {
                double st = ((k & Keys.Shift) != 0 ? 10 : 1) / board.Zoom;
                var kc = k & Keys.KeyCode;
                foreach (var i in RefOps.Expand(board, sel)) { i.X += kc == Keys.Left ? -st : kc == Keys.Right ? st : 0; i.Y += kc == Keys.Up ? -st : kc == Keys.Down ? st : 0; }
                Changed(); return true;
            }
        }
        return false;
    }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (e.KeyCode == Keys.Space) { spaceDown = false; Cursor = Cursors.Default; } }
    protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(k);

    // ---- 손잡이 ------------------------------------------------------------------------------
    RefItem Single => sel.Count == 1 ? sel[0] : RefOps.Expand(board, sel).Count == 1 ? sel.FirstOrDefault() : null;
    PointF[] ScreenCorners(RefItem it) { using var v = V(); return RefOps.Corners(it, v); }
    PointF RotHandle(PointF[] c)
    {
        var mid = new PointF((c[0].X + c[1].X) / 2, (c[0].Y + c[1].Y) / 2);
        var ctr = new PointF((c[0].X + c[2].X) / 2, (c[0].Y + c[2].Y) / 2);
        float dx = mid.X - ctr.X, dy = mid.Y - ctr.Y, len = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
        return new PointF(mid.X + dx / len * 26, mid.Y + dy / len * 26);
    }
    static PointF Mid(PointF a, PointF b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    static bool Near(PointF a, Point b, float r = 8) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) <= r * r;

    // ---- 그리기 ------------------------------------------------------------------------------
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        RefOps.DrawBoard(g, board, Width, Height);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (board.Items.Count == 0)
        {
            Web.Text(g, "그림 파일을 끌어다 놓거나, Ctrl+V 로 붙여넣거나, 위의 [캡처] 로 화면 일부를 넣으세요.", Web.Body, ClientRectangle, Theme.Muted, Web.Center);
        }
        // 묶음 테두리
        using (var gp = new Pen(Color.FromArgb(150, 120, 180, 255), 1) { DashStyle = DashStyle.Dash })
            foreach (var grp in board.Items.Where(i => i.Group != null).GroupBy(i => i.Group))
            {
                using var v = V();
                var r = grp.Select(i => RefOps.Bounds(i, v)).Aggregate(RectangleF.Union); r.Inflate(6, 6);
                g.DrawRectangle(gp, r.X, r.Y, r.Width, r.Height);
            }
        using var selPen = new Pen(Theme.Accent, 1.5f);
        foreach (var it in RefOps.Expand(board, sel))
        {
            var c = ScreenCorners(it);
            g.DrawPolygon(selPen, c);
        }
        var s = Single;
        if (s != null)
        {
            var c = ScreenCorners(s);
            using var hb = new SolidBrush(Color.White);
            if (Mode == "crop" && s.Kind == "image")
            {
                using var cp = new Pen(Color.FromArgb(255, 80, 200, 255), 3);
                for (int i = 0; i < 4; i++) { var m = Mid(c[i], c[(i + 1) % 4]); g.FillRectangle(hb, m.X - 6, m.Y - 6, 12, 12); g.DrawRectangle(cp, m.X - 6, m.Y - 6, 12, 12); }
            }
            else
            {
                foreach (var p in c) { g.FillRectangle(hb, p.X - 5, p.Y - 5, 10, 10); g.DrawRectangle(selPen, p.X - 5, p.Y - 5, 10, 10); }
                var rh = RotHandle(c);
                g.DrawLine(selPen, Mid(c[0], c[1]), rh);
                g.FillEllipse(hb, rh.X - 6, rh.Y - 6, 12, 12); g.DrawEllipse(selPen, rh.X - 6, rh.Y - 6, 12, 12);
            }
        }
        if (drag == Drag.Marquee) using (var mp = new Pen(Theme.Accent) { DashStyle = DashStyle.Dash }) g.DrawRectangle(mp, marquee);
        // 고른 색
        if (Mode == "pick" || Picked != null)
        {
            var sw = new Rectangle(Width - 46, Height - 46, 36, 36);
            using var br = new SolidBrush(Picked ?? Color.Transparent);
            g.FillRectangle(br, sw); g.DrawRectangle(Pens.White, sw);
            if (Mode == "pick") Web.Text(g, "스포이트", Web.Tiny, new Rectangle(Width - 110, Height - 24, 60, 16), Theme.Accent, Web.Right);
        }
        string vflags = (board.ViewFlip ? "좌우 반전 " : "") + (board.ViewGray ? "흑백 " : "");
        if (vflags.Length > 0) Web.Text(g, "보기: " + vflags, Web.Small, new Rectangle(8, 6, 300, 18), Theme.Accent);
    }

    // ---- 마우스 ------------------------------------------------------------------------------
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        down = last = e.Location; downBoard = ToBoard(e.Location); outDragged = false;
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && spaceDown) || e.Button == MouseButtons.Right) { drag = Drag.Pan; return; }
        if (e.Button != MouseButtons.Left) return;
        if (Mode == "pick" || ModifierKeys == Keys.Alt) { PickAt(e.Location); drag = Drag.None; Mode = Mode == "pick" ? "pick" : ""; return; }
        var s = Single;
        if (s != null)
        {
            var c = ScreenCorners(s);
            if (Mode == "crop" && s.Kind == "image")
            {
                for (int i = 0; i < 4; i++) if (Near(Mid(c[i], c[(i + 1) % 4]), e.Location, 10)) { drag = Drag.Crop; cropEdge = i; cropOrig = RefOps.SourceRect(s); Snapshot(new[] { s }); return; }
            }
            else
            {
                if (Near(RotHandle(c), e.Location, 9)) { drag = Drag.Rotate; Snapshot(new[] { s }); return; }
                foreach (var p in c) if (Near(p, e.Location, 8)) { drag = Drag.Scale; Snapshot(new[] { s }); return; }
            }
        }
        var hit = RefOps.HitTest(board, downBoard);
        if (hit != null)
        {
            if ((ModifierKeys & Keys.Control) != 0) { if (!sel.Remove(hit)) sel.Add(hit); }
            else if (!sel.Contains(hit) && !RefOps.Expand(board, sel).Contains(hit)) { sel.Clear(); sel.Add(hit); }
            drag = Drag.Move;
            Snapshot(RefOps.Expand(board, sel));
            Invalidate();
            Status?.Invoke(hit.Kind == "image" ? $"{hit.Name} · {RefOps.SourceRect(hit).Width:0}×{RefOps.SourceRect(hit).Height:0} · {hit.Scale * 100:0}% · {hit.Rot:0}°{(hit.Group != null ? " · 묶음" : "")}" : "메모 (두 번 눌러 고치기)");
            return;
        }
        if ((ModifierKeys & Keys.Control) == 0) sel.Clear();
        drag = Drag.Marquee; marquee = new Rectangle(e.Location, Size.Empty);
        Invalidate();
    }
    void Snapshot(IEnumerable<RefItem> items) => orig = items.Select(i => (i, i.X, i.Y, i.Scale, i.Rot)).ToList();

    void PickAt(Point p)
    {
        var c = RefOps.Sample(board, Width, Height, p);
        Picked = c;
        App.SetColor(0xFF000000u | (uint)(c.R << 16 | c.G << 8 | c.B));
        Status?.Invoke($"색: {Wf.Hex(c)} → 전경색");
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var bp = ToBoard(e.Location);
        switch (drag)
        {
            case Drag.Pan:
                board.PanX += (board.ViewFlip ? -1 : 1) * (e.X - last.X); board.PanY += e.Y - last.Y; Invalidate();
                break;
            case Drag.Move:
                // 창 밖으로 끌고 나가면 → 그림판에 놓을 수 있게 끌어 놓기 시작
                if (!ClientRectangle.Contains(e.Location) && !outDragged)
                {
                    outDragged = true;
                    foreach (var o in orig) { o.it.X = o.x; o.it.Y = o.y; }
                    Invalidate();
                    var it = sel.LastOrDefault();
                    if (it != null)
                    {
                        var data = new DataObject();
                        data.SetData(DragFormat, board.Key + "|" + it.Id);
                        drag = Drag.None;
                        DoDragDrop(data, DragDropEffects.Copy);
                    }
                    return;
                }
                foreach (var o in orig) { o.it.X = o.x + bp.X - downBoard.X; o.it.Y = o.y + bp.Y - downBoard.Y; }
                Invalidate();
                break;
            case Drag.Scale:
            {
                var o = orig[0]; var ctr = ToScreen(new PointF((float)o.x, (float)o.y));
                double d0 = Dist(ctr, down), d1 = Dist(ctr, e.Location);
                if (d0 > 2) o.it.Scale = Math.Clamp(o.s * d1 / d0, 0.01, 50);
                Status?.Invoke($"{o.it.Scale * 100:0}%");
                Invalidate();
                break;
            }
            case Drag.Rotate:
            {
                var o = orig[0]; var ctr = ToScreen(new PointF((float)o.x, (float)o.y));
                double a0 = Math.Atan2(down.Y - ctr.Y, down.X - ctr.X), a1 = Math.Atan2(e.Y - ctr.Y, e.X - ctr.X);
                double r = o.r + (a1 - a0) * 180 / Math.PI * (board.ViewFlip ? -1 : 1);
                if ((ModifierKeys & Keys.Shift) != 0) r = Math.Round(r / 15) * 15;
                o.it.Rot = ((r % 360) + 540) % 360 - 180;
                Status?.Invoke($"{o.it.Rot:0}°");
                Invalidate();
                break;
            }
            case Drag.Crop:
            {
                var it = orig[0].it;
                var bmp = RefStore.Image(it.Hash); if (bmp == null) break;
                // 원본 픽셀 좌표 = 항목 안 좌표 + 자르기 가운데
                var cur = RefOps.SourceRect(it);
                var l = RefOps.ToLocal(it, bp);
                float sx = l.X + cur.X + cur.Width / 2, sy = l.Y + cur.Y + cur.Height / 2;
                float x0 = cur.Left, y0 = cur.Top, x1 = cur.Right, y1 = cur.Bottom;
                switch (cropEdge) { case 0: y0 = Math.Clamp(sy, 0, y1 - 4); break; case 1: x1 = Math.Clamp(sx, x0 + 4, bmp.Width); break; case 2: y1 = Math.Clamp(sy, y0 + 4, bmp.Height); break; case 3: x0 = Math.Clamp(sx, 0, x1 - 4); break; }
                RefOps.SetCrop(it, RectangleF.FromLTRB(x0, y0, x1, y1));
                Status?.Invoke($"자르기 {x1 - x0:0}×{y1 - y0:0}");
                Invalidate();
                break;
            }
            case Drag.Marquee:
                marquee = Rectangle.FromLTRB(Math.Min(down.X, e.X), Math.Min(down.Y, e.Y), Math.Max(down.X, e.X), Math.Max(down.Y, e.Y));
                Invalidate();
                break;
            default:
                Cursor = Mode == "pick" || ModifierKeys == Keys.Alt ? Cursors.Cross : spaceDown ? Cursors.SizeAll : RefOps.HitTest(board, bp) != null ? Cursors.SizeAll : Cursors.Default;
                break;
        }
        last = e.Location;
    }
    static double Dist(PointF a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var was = drag; drag = Drag.None;
        if (was == Drag.Pan && e.Button == MouseButtons.Right && Dist(down, e.Location) < 4) { Menu(e.Location); return; }
        if (was == Drag.Marquee)
        {
            using var v = V();
            foreach (var it in board.Items) if (marquee.Width > 2 && marquee.IntersectsWith(Rectangle.Round(RefOps.Bounds(it, v))) && !sel.Contains(it)) sel.Add(it);
            Invalidate(); return;
        }
        if (was is Drag.Move or Drag.Scale or Drag.Rotate or Drag.Crop or Drag.Pan) Changed();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var hit = RefOps.HitTest(board, ToBoard(e.Location));
        if (hit?.Kind == "note") EditNote(hit);
        else if (hit != null) { RefOps.Fit(board, Width, Height, new[] { hit }); Changed(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var bp = ToBoard(e.Location);
        double z = Math.Clamp(board.Zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.02, 20);
        board.Zoom = z;
        // 커서 아래 점이 그대로 있게
        var after = ToScreen(bp);
        board.PanX += (board.ViewFlip ? 1 : -1) * (after.X - e.X);
        board.PanY -= after.Y - e.Y;
        Changed();
    }

    void Menu(Point p)
    {
        var hit = RefOps.HitTest(board, ToBoard(p));
        if (hit != null && !RefOps.Expand(board, sel).Contains(hit)) { sel.Clear(); sel.Add(hit); Invalidate(); }
        var m = WebMenu.Style(new ContextMenuStrip());
        if (sel.Count > 0)
        {
            m.Items.Add("그림판에 새 레이어로 넣기", null, (_, _) => SelectedToCanvas(null));
            m.Items.Add("맨 앞으로", null, (_, _) => Order(1));
            m.Items.Add("맨 뒤로", null, (_, _) => Order(-1));
            m.Items.Add("좌우 반전 (H)", null, (_, _) => { foreach (var i in sel) i.FlipH = !i.FlipH; Changed(); });
            m.Items.Add("흑백 켜기/끄기", null, (_, _) => { foreach (var i in sel) i.Gray = !i.Gray; Changed(); });
            m.Items.Add("자르기 (C)", null, (_, _) => { Mode = "crop"; Invalidate(); });
            m.Items.Add("자르기 풀기", null, (_, _) => { foreach (var i in sel.Where(i => i.Kind == "image")) RefOps.SetCrop(i, new RectangleF(0, 0, RefStore.Image(i.Hash)?.Width ?? 1, RefStore.Image(i.Hash)?.Height ?? 1)); Changed(); });
            m.Items.Add("크기·회전 처음대로", null, (_, _) => { foreach (var i in sel) { i.Rot = 0; i.Scale = i.Kind == "image" ? Math.Min(1, 900.0 / Math.Max(1, Math.Max(RefOps.LocalSize(i).Width, RefOps.LocalSize(i).Height))) : 1; } Changed(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("묶기 (Ctrl+G)", null, (_, _) => Key(Keys.Control | Keys.G));
            m.Items.Add("묶음 풀기 (Ctrl+Shift+G)", null, (_, _) => Key(Keys.Control | Keys.Shift | Keys.G));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("지우기 (Del)", null, (_, _) => DeleteSelected());
        }
        else
        {
            m.Items.Add("붙여넣기 (Ctrl+V)", null, (_, _) => Paste());
            m.Items.Add("메모 추가", null, (_, _) => AddNote());
            m.Items.Add("모두 보이게 (F)", null, (_, _) => FitAll());
        }
        m.Show(this, p);
    }

    protected override void Dispose(bool disposing) { if (disposing) saveTimer.Dispose(); base.Dispose(disposing); }
}

/// <summary>화면 캡처: 전체 화면을 찍고, 그 위에서 네모를 끌어 고름</summary>
public static class ScreenCapture
{
    public static Bitmap Grab()
    {
        try
        {
            var vs = SystemInformation.VirtualScreen;
            var b = new Bitmap(vs.Width, vs.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(b);
            g.CopyFromScreen(vs.Location, Point.Empty, vs.Size);
            return b;
        }
        catch (Exception ex) { App.Toast("화면을 찍지 못했습니다: " + ex.Message); return null; }
    }

    /// <summary>찍은 화면 위에서 네모 고르기 (Esc = 취소). 고른 부분 그림</summary>
    public static Bitmap PickRegion(Bitmap shot)
    {
        var vs = SystemInformation.VirtualScreen;
        Rectangle sel = Rectangle.Empty; Point? a = null; Bitmap result = null;
        using var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = vs, TopMost = true, ShowInTaskbar = false, Cursor = Cursors.Cross, KeyPreview = true };
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(f, true);
        f.Paint += (_, e) =>
        {
            e.Graphics.DrawImageUnscaled(shot, 0, 0);
            using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            using var rg = new Region(new Rectangle(0, 0, f.Width, f.Height));
            if (!sel.IsEmpty) rg.Exclude(sel);
            e.Graphics.FillRegion(dim, rg);
            if (!sel.IsEmpty) using (var p = new Pen(Theme.Accent, 2)) e.Graphics.DrawRectangle(p, sel);
            e.Graphics.DrawString("끌어서 넣을 부분 고르기 · Esc 취소", Web.Body, Brushes.White, 20, 20);
        };
        f.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) a = e.Location; else f.Close(); };
        f.MouseMove += (_, e) => { if (a is Point p) { sel = Rectangle.FromLTRB(Math.Min(p.X, e.X), Math.Min(p.Y, e.Y), Math.Max(p.X, e.X), Math.Max(p.Y, e.Y)); f.Invalidate(); } };
        f.MouseUp += (_, e) => { if (a != null && sel.Width > 3 && sel.Height > 3) { result = Crop(shot, sel); f.Close(); } a = null; };
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
        f.ShowDialog();
        return result;
    }
    public static Bitmap Crop(Bitmap shot, Rectangle r) => Img.Crop(shot, r);
}
