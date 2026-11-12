// 웹 .modal 과 같은 대화상자: 어두운 판(--panel) + 테두리 #55555f + 둥근 14px, 머리(굵은 15px 제목 + × 단추, 끌어서 옮기기),
// 내용(여백 12·16, 넘치면 굴림), 아래 단추 줄(오른쪽 정렬, primary = 주황). 뒤의 메인 창은 어둡게 (.modal-backdrop).
//   ModalForm      — 새로 만드는 대화상자
//   Modals.Adopt(f) — 이미 있는 Form 을 같은 모양으로 (모든 모듈의 옛 창)
//   Modal.Confirm / Prompt / Info — 웹 confirmDialog · prompt
using System.Runtime.InteropServices;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.UI.Dialogs;

/// <summary>대화상자 머리 (.modal-head): 제목 + × 단추, 끌면 창이 움직임</summary>
public sealed class ModalHead : Control
{
    public readonly WebButton Close;
    public ModalHead(string title, Action onClose, bool closeButton = true)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = title; Font = Web.F(15, true); BackColor = Theme.Panel;
        Height = 8 + Theme.Btn + 8 + 1; Dock = DockStyle.Top;
        Close = new WebButton("", BtnKind.Icon, "x") { IconSize = 20, TabStop = false, AccessibleName = "닫기" };
        Close.Click += (_, _) => onClose();
        Close.Visible = closeButton;
        Controls.Add(Close);
    }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Close?.SetBounds(Width - 8 - Theme.Btn, 8, Theme.Btn, Theme.Btn); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        Web.Text(g, Text, Font, new Rectangle(16, 0, Width - 16 - (Close.Visible ? Theme.Btn + 16 : 16), Height - 1), Theme.Text, Web.Left);
        using var p = new Pen(Web.ModalSep);
        g.DrawLine(p, 0, Height - 1, Width, Height - 1);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || FindForm() is not Form f || f.WindowState == FormWindowState.Maximized) return;
        Modals.ReleaseCapture();
        Modals.SendMessage(f.Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }
    protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); }
}

/// <summary>아래 단추 줄 (.modal-actions)</summary>
public sealed class ModalActions : Panel
{
    readonly WebRow row = new() { Margin = Padding.Empty, Gap = 8 };
    public ModalActions()
    {
        BackColor = Theme.Panel; Dock = DockStyle.Bottom; Padding = new Padding(16, 10 + 1, 16, 10);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        row.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(row);
        Height = 40 + 21 + 1;
    }
    public WebRow Row => row;
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var ps = row.GetPreferredSize(new Size(Math.Max(10, Width - Padding.Horizontal), 0));
        row.SetBounds(Width - Padding.Right - ps.Width, Padding.Top, ps.Width, ps.Height);
        int h = ps.Height + Padding.Vertical;
        if (Height != h) Height = h;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(Web.ModalSep);
        e.Graphics.DrawLine(p, 0, 0, Width, 0);
    }
}

/// <summary>웹 .modal 대화상자</summary>
public class ModalForm : Form
{
    public readonly ModalHead Head;
    public readonly Panel Body;
    public readonly ModalActions Actions;
    Control content;
    /// <summary>true = 정해진 크기 (내용이 꽉 참), false = 내용 높이에 맞춤 (화면보다 크면 굴림)</summary>
    public bool FixedSize;
    public bool Resizable;
    public int ContentWidth;
    public Action Primary;
    public object Result;
    Modals.Backdrop backdrop;

    /// <param name="width">웹 .modal 440, .wide 820, 기능 검색 560</param>
    public ModalForm(string title, int width = 440, int height = 0, bool closeButton = true)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; ShowInTaskbar = false;
        BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Web.Body; KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Padding = new Padding(1);
        ContentWidth = width;
        Head = new ModalHead(title, () => { DialogResult = DialogResult.Cancel; Close(); }, closeButton);
        Body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 12, 16, 12), BackColor = Theme.Panel }.DarkScroll();
        // 가로 굴림은 쓰지 않음 (웹 .modal-body: overflow-y 만)
        Body.AutoScroll = false; Body.HorizontalScroll.Maximum = 0; Body.HorizontalScroll.Enabled = false; Body.HorizontalScroll.Visible = false; Body.AutoScroll = true;
        Actions = new ModalActions { Visible = false };
        Controls.Add(Body); Controls.Add(Actions); Controls.Add(Head);
        FixedSize = height > 0;
        ClientSize = new Size(width + 2, height > 0 ? height : 200);
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; /* CS_DROPSHADOW */ return cp; } }

    /// <summary>내용 넣기 (WebStack 이면 폭에 맞게 늘어남)</summary>
    public T SetContent<T>(T c) where T : Control
    {
        Body.SuspendLayout();
        if (content != null) { Body.Controls.Remove(content); }
        content = c;
        if (FixedSize && c is not WebStack) c.Dock = DockStyle.Fill;
        if (c is WebStack ws) { ws.FillParent = true; ws.Dock = DockStyle.None; ws.Location = new Point(Body.Padding.Left, Body.Padding.Top); }
        Body.Controls.Add(c);
        Body.ResumeLayout();
        return c;
    }
    public Control Content => content;

    /// <summary>아래 단추 더하기. run 이 false 를 돌려주면 닫지 않음</summary>
    public WebButton AddAction(string label, Func<bool> run = null, bool primary = false, bool danger = false, DialogResult result = DialogResult.None)
    {
        var b = new WebButton(label, primary ? BtnKind.ModalPrimary : BtnKind.Modal) { Margin = Padding.Empty };
        if (danger) b.Kind = BtnKind.Modal;
        if (danger) b.ForeColor = Web.Danger;
        void Go()
        {
            bool close = true;
            try { if (run != null) close = run(); } catch (Exception ex) { App.Toast(ex.Message); close = false; }
            if (close) { DialogResult = result != DialogResult.None ? result : primary ? DialogResult.OK : DialogResult.Cancel; Close(); }
        }
        b.Click += (_, _) => Go();
        if (primary) { Primary = Go; AcceptButton = null; }
        Actions.Row.Controls.Add(b);
        Actions.Visible = true;
        return b;
    }
    public WebButton AddAction(string label, Action run, bool primary = false) => AddAction(label, () => { run?.Invoke(); return true; }, primary);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter && !e.Shift && Primary != null && !(ActiveControl is TextBox tb && tb.Multiline) && !(ActiveControlDeep() is TextBox t2 && t2.Multiline) && ActiveControlDeep() is not WebList && ActiveControlDeep() is not ButtonBase)
        { e.Handled = true; e.SuppressKeyPress = true; Primary(); }
    }
    Control ActiveControlDeep() { Control c = ActiveControl; while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl; return c; }

    /// <summary>내용 크기가 바뀌었을 때 창 높이 다시 맞춤</summary>
    public void Fit()
    {
        if (FixedSize || content == null) return;
        var wa = Screen.FromPoint(Owner?.Bounds.Location ?? Cursor.Position).WorkingArea;
        int innerW = ContentWidth - Body.Padding.Horizontal;
        var ps = content.GetPreferredSize(new Size(innerW, 0));
        int h = Head.Height + Body.Padding.Vertical + ps.Height + (Actions.Visible ? Actions.Height : 0) + 2;
        h = Math.Min(h, wa.Height - 32);
        var ow = Owner ?? App.Main;
        if (ow != null && ow.Visible && ow.WindowState != FormWindowState.Minimized) h = Math.Min(h, ow.ClientSize.Height - 32);   // 웹: max-height calc(100vh - 32px)
        ClientSize = new Size(ContentWidth + 2, Math.Max(120, h));
    }

    protected override void OnLoad(EventArgs e)
    {
        Actions.PerformLayout();
        Fit();
        Modals.CenterOn(this, Owner ?? App.Main);
        base.OnLoad(e);
        backdrop = Modals.Backdrop.For(this);
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Modals.ShotHook(this);
        // 처음 입력 칸에 포커스 (웹: input, textarea, select)
        var first = Modals.FirstInput(Body);
        if (first != null) { if (first is WebInput wi) wi.SelectAll(); else first.Focus(); }
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); backdrop?.Close(); backdrop = null; }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Modals.Round(Handle); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(Web.ModalLine);
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (Resizable && m.Msg == 0x84 /* WM_NCHITTEST */ && (int)m.Result == 1) m.Result = (IntPtr)Modals.EdgeHit(this, m.LParam);
    }
}

public static class Modals
{
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    /// <summary>Windows 11: 둥근 모서리 + 테두리 색</summary>
    internal static void Round(IntPtr h)
    {
        try
        {
            int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);
            int col = Web.ModalLine.R | Web.ModalLine.G << 8 | Web.ModalLine.B << 16; DwmSetWindowAttribute(h, 34, ref col, 4);
        }
        catch { }
    }

    internal static int EdgeHit(Form f, IntPtr lp)
    {
        int x = (short)((long)lp & 0xFFFF), y = (short)(((long)lp >> 16) & 0xFFFF);
        var p = f.PointToClient(new Point(x, y));
        const int b = 6;
        bool l = p.X < b, r = p.X >= f.ClientSize.Width - b, t = p.Y < b, bt = p.Y >= f.ClientSize.Height - b;
        if (t && l) return 13; if (t && r) return 14; if (bt && l) return 16; if (bt && r) return 17;
        if (l) return 10; if (r) return 11; if (t) return 12; if (bt) return 15;
        return 1;
    }

    internal static void CenterOn(Form f, Form owner)
    {
        Rectangle area = owner != null && owner.Visible && owner.WindowState != FormWindowState.Minimized ? owner.Bounds : Screen.FromPoint(Cursor.Position).WorkingArea;
        var wa = Screen.FromRectangle(area).WorkingArea;
        int x = area.X + (area.Width - f.Width) / 2, y = area.Y + (area.Height - f.Height) / 2;
        f.Location = new Point(Math.Clamp(x, wa.X, Math.Max(wa.X, wa.Right - f.Width)), Math.Clamp(y, wa.Y, Math.Max(wa.Y, wa.Bottom - f.Height)));
    }

    /// <summary>화면 비교 찍기 (COMICPAD_UI_SHOT): 대화상자가 뜨면 메인 창 + 어두운 뒤판 + 대화상자를 한 장으로 저장하고 끝냄.
    /// (Program 의 찍기는 대화상자가 떠 있는 동안 기다리지 못하므로 여기서)</summary>
    internal static void ShotHook(Form f)
    {
        string shot = Environment.GetEnvironmentVariable("COMICPAD_UI_SHOT");
        if (string.IsNullOrEmpty(shot) || App.Main == null || !f.Modal) return;
        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try
            {
                var main = App.Main;
                using var bmp = new Bitmap(main.ClientSize.Width, main.ClientSize.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    using (var mb = new Bitmap(main.Width, main.Height))
                    {
                        main.DrawToBitmap(mb, new Rectangle(0, 0, mb.Width, mb.Height));
                        var off = main.PointToScreen(Point.Empty);
                        g.DrawImage(mb, main.Left - off.X, main.Top - off.Y);
                    }
                    using (var dim = new SolidBrush(Color.FromArgb(115, 0, 0, 0))) g.FillRectangle(dim, 0, 0, bmp.Width, bmp.Height);
                    using var db = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(db, new Rectangle(0, 0, db.Width, db.Height));
                    var p = main.PointToClient(f.Location);
                    g.DrawImage(db, p.X, p.Y);
                }
                bmp.Save(shot);
            }
            catch (Exception ex) { File.WriteAllText(shot + ".error.txt", ex.ToString()); }
            Environment.Exit(0);
        };
        t.Start();
    }

    internal static Control FirstInput(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (!c.Visible || !c.Enabled) continue;
            if (c is WebInput || c is TextBox { ReadOnly: false } || c is WebSelect) return c;
            var d = FirstInput(c);
            if (d != null) return d;
        }
        return null;
    }

    /// <summary>뒤의 메인 창을 어둡게 (웹 .modal-backdrop rgba(0,0,0,.45))</summary>
    internal sealed class Backdrop : Form
    {
        static int open;
        public static Backdrop For(Form dlg)
        {
            var main = App.Main;
            if (main == null || !main.Visible || main.WindowState == FormWindowState.Minimized || open > 0) return null;
            if (dlg.Owner != null && dlg.Owner != main) return null;
            if (Environment.GetEnvironmentVariable("COMICPAD_NO_BACKDROP") == "1") return null;
            try
            {
                var b = new Backdrop { Bounds = main.RectangleToScreen(main.ClientRectangle) };
                open++;
                b.FormClosed += (_, _) => open--;
                b.Show(main);
                return b;
            }
            catch { return null; }
        }
        Backdrop()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black; Opacity = 0.45; Text = "";
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x80 /* TOOLWINDOW */; return cp; } }
    }

    // ---- 옛 Form 을 웹 모양으로 ------------------------------------------------------------------
    sealed class Adopted { public ModalHead Head; public Modals.Backdrop Backdrop; public bool Sizable; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, Adopted> adopted = new();
    public static bool IsAdopted(Form f) => adopted.TryGetValue(f, out _);

    /// <summary>이미 만든 Form (모듈의 옛 대화상자) 을 웹 .modal 모양으로: 테두리 없는 어두운 창 + 머리 + 웹식 부품 색. 만든 직후에 부름</summary>
    public static T Adopt<T>(T f, bool backdrop = true) where T : Form
    {
        if (f == null || f is ModalForm || adopted.TryGetValue(f, out _)) return f;
        var a = new Adopted { Sizable = f.FormBorderStyle is FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow };
        adopted.Add(f, a);
        bool auto = f.AutoSize;
        var client = f.ClientSize;
        f.FormBorderStyle = FormBorderStyle.None;
        if (!auto) f.ClientSize = client;
        f.BackColor = Theme.Panel; f.ForeColor = Theme.Text;
        f.ShowInTaskbar = false;
        f.KeyPreview = true;
        if (f.Font == null || f.Font.Name == "Malgun Gothic" && Math.Abs(f.Font.SizeInPoints - 9f) < 0.6f || f.Font.Equals(Control.DefaultFont)) f.Font = Web.Body;
        f.Load += (_, _) => Wrap(f, a, backdrop);
        f.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && f.CancelButton == null && !e.Handled) { f.Close(); e.Handled = true; }
        };
        f.HandleCreated += (_, _) => Round(f.Handle);
        f.Shown += (_, _) => ShotHook(f);
        return f;
    }

    static void Wrap(Form f, Adopted a, bool backdrop)
    {
        if (a.Head != null) return;
        f.SuspendLayout();
        bool auto = f.AutoSize;
        if (auto) { f.PerformLayout(); f.Size = f.GetPreferredSize(Size.Empty); }
        var client = f.ClientSize;
        var kids = f.Controls.Cast<Control>().ToList();
        var body = new Panel { BackColor = Theme.Panel, Padding = f.Padding, Margin = Padding.Empty, Dock = DockStyle.Fill };
        f.AutoSize = false;
        foreach (var c in kids) body.Controls.Add(c);
        // 위치를 직접 정한 부품이 여백을 무시하고 (0,0) 에 붙어 있으면 여백만큼 안으로
        var free = kids.Where(c => c.Dock == DockStyle.None).ToList();
        if (free.Count > 0 && free.Min(c => c.Left) < body.Padding.Left && free.Min(c => c.Top) < body.Padding.Top)
        {
            foreach (var c in free) c.Location = new Point(c.Left + body.Padding.Left, c.Top + body.Padding.Top);
            client = new Size(Math.Max(client.Width, free.Max(c => c.Right) + body.Padding.Right), Math.Max(client.Height, free.Max(c => c.Bottom) + body.Padding.Bottom));
        }
        // 맨 끝의 단추 줄 (확인·취소 …) 은 웹 .modal-actions 처럼 아래 줄로
        ModalActions actions = null;
        var stack = free.Count == 1 && free[0] is FlowLayoutPanel fp0 ? fp0 : null;
        var lastRow = stack?.Controls.Cast<Control>().LastOrDefault(c => c.Visible || !f.Visible) as FlowLayoutPanel;
        if (lastRow != null && lastRow.Controls.Count > 0 && lastRow.Controls.Cast<Control>().All(c => c is Button))
        {
            int rowH = lastRow.Height + lastRow.Margin.Vertical;
            stack.Controls.Remove(lastRow);
            actions = new ModalActions();
            foreach (var b in lastRow.Controls.Cast<Button>().ToList())
            {
                lastRow.Controls.Remove(b);
                b.Margin = Padding.Empty;
                if (b is WebButton wb) wb.Kind = wb.Kind == BtnKind.Primary ? BtnKind.ModalPrimary : BtnKind.Modal;
                else b.MinimumSize = new Size(90, 40);
                actions.Row.Controls.Add(b);
            }
            actions.Visible = true;
            client = new Size(client.Width, Math.Max(40, client.Height - rowH));
        }
        Action refit = null;
        if (auto)
        {
            // 내용 크기가 바뀌면 창도 (예전 AutoSize 처럼)
            void Refit()
            {
                if (f.IsDisposed || a.Head == null) return;
                int w = 0, h = 0;
                foreach (Control c in body.Controls) { if (!c.Visible || c.Dock != DockStyle.None) continue; var ps = c.AutoSize ? c.GetPreferredSize(Size.Empty) : c.Size; w = Math.Max(w, c.Left + Math.Max(ps.Width, c.Width) + c.Margin.Right); h = Math.Max(h, c.Top + Math.Max(ps.Height, c.Height) + c.Margin.Bottom); }
                var need = new Size(Math.Max(client.Width, w + body.Padding.Right) + 2, h + body.Padding.Bottom + a.Head.Height + (actions?.Height ?? 0) + 2);
                if (need != f.ClientSize) f.ClientSize = need;
            }
            foreach (var c in kids) c.SizeChanged += (_, _) => Refit();
            refit = Refit;
        }
        a.Head = new ModalHead(f.Text, () => f.Close(), f.ControlBox);
        f.TextChanged += (_, _) => { if (a.Head != null) a.Head.Text = f.Text; };
        f.Padding = new Padding(1);
        f.Controls.Add(body);
        if (actions != null) { f.Controls.Add(actions); actions.PerformLayout(); }
        f.Controls.Add(a.Head);
        Skin.Apply(body);
        f.Paint += (_, e) => { using var p = new Pen(Web.ModalLine); e.Graphics.DrawRectangle(p, 0, 0, f.Width - 1, f.Height - 1); };
        f.ResumeLayout(true);
        f.ClientSize = new Size(client.Width + 2, client.Height + a.Head.Height + (actions?.Height ?? 0) + 2);
        refit?.Invoke();
        if (a.Sizable) new EdgeResizer(f);
        if (f.StartPosition is FormStartPosition.CenterParent or FormStartPosition.CenterScreen or FormStartPosition.WindowsDefaultLocation)
        {
            var owner = f.Owner ?? App.Main;
            CenterOn(f, owner);
            f.StartPosition = FormStartPosition.Manual;
        }
        if (backdrop && f.Modal) a.Backdrop = Backdrop.For(f);
        f.FormClosed += (_, _) => { a.Backdrop?.Close(); a.Backdrop = null; };
    }

    /// <summary>테두리 없는 창 가장자리를 끌어 크기 바꾸기</summary>
    sealed class EdgeResizer : NativeWindow
    {
        readonly Form f;
        public EdgeResizer(Form f) { this.f = f; if (f.IsHandleCreated) AssignHandle(f.Handle); f.HandleCreated += (_, _) => AssignHandle(f.Handle); f.HandleDestroyed += (_, _) => ReleaseHandle(); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x84 && (int)m.Result == 1) m.Result = (IntPtr)EdgeHit(f, m.LParam);
        }
    }

    // ---- 자주 쓰는 작은 창 (웹 confirmDialog · prompt) -------------------------------------------------------
    /// <summary>확인 창 (웹 confirmDialog): 확인이면 true</summary>
    public static bool Confirm(string message, string okLabel = "확인", bool danger = false, string title = "확인")
    {
        if (App.Main == null || !App.Main.Visible) return MessageBox.Show(message, title, MessageBoxButtons.OKCancel) == DialogResult.OK;
        using var f = new ModalForm(title);
        var t = new Label { Text = message, AutoSize = true, Font = Web.F(14), ForeColor = Theme.Text, Margin = new Padding(0, 6, 0, 6), UseMnemonic = false };
        f.SetContent(new WebStack { Controls = { t } });
        f.AddAction("취소", () => true);
        f.AddAction(okLabel, () => true, primary: !danger, danger: danger, result: DialogResult.OK);
        if (danger) f.Primary = () => { f.DialogResult = DialogResult.OK; f.Close(); };
        return f.ShowDialog(Owner()) == DialogResult.OK;
    }

    /// <summary>예·아니요·취소 (저장할까요? 같은 것). Yes / No / Cancel</summary>
    public static DialogResult Ask3(string message, string yes = "저장", string no = "저장 안 함", string title = "ComicPad")
    {
        if (App.Main == null || !App.Main.Visible) return MessageBox.Show(message, title, MessageBoxButtons.YesNoCancel);
        using var f = new ModalForm(title);
        f.SetContent(new WebStack { Controls = { new Label { Text = message, AutoSize = true, Font = Web.F(14), ForeColor = Theme.Text, Margin = new Padding(0, 6, 0, 6), UseMnemonic = false } } });
        f.AddAction("취소", () => true, result: DialogResult.Cancel);
        f.AddAction(no, () => true, result: DialogResult.No);
        f.AddAction(yes, () => true, primary: true, result: DialogResult.Yes);
        var r = f.ShowDialog(Owner());
        return r is DialogResult.Yes or DialogResult.No ? r : DialogResult.Cancel;
    }

    /// <summary>알림 창 (닫기만)</summary>
    public static void Info(string message, string title = "ComicPad")
    {
        if (App.Main == null || !App.Main.Visible) { MessageBox.Show(message, title); return; }
        using var f = new ModalForm(title);
        f.SetContent(new WebStack { Controls = { new Label { Text = message, AutoSize = true, Font = Web.F(14), ForeColor = Theme.Text, Margin = new Padding(0, 6, 0, 6), UseMnemonic = false } } });
        f.AddAction("확인", () => true, primary: true);
        f.ShowDialog(Owner());
    }

    /// <summary>한 줄 글 입력 (웹 prompt). 취소면 null</summary>
    public static string Prompt(string title, string value, string label = null, string okLabel = "확인")
    {
        using var f = new ModalForm(title);
        var input = new WebInput(value ?? "");
        var st = new WebStack();
        if (label != null) st.Controls.Add(new Label { Text = label, AutoSize = true, Font = Web.Small, ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, 4) });
        st.Controls.Add(input);
        f.SetContent(st);
        f.AddAction("취소", () => true);
        string res = null;
        f.AddAction(okLabel, () => { res = input.Text.Trim(); return true; }, primary: true);
        return f.ShowDialog(Owner()) == DialogResult.OK ? res : null;
    }

    static IWin32Window Owner() => Form.ActiveForm is Form af && af.Visible && af.Modal ? af : App.Main;
}
