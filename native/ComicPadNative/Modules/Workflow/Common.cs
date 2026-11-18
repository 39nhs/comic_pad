// 작업 흐름(Workflow) 모듈 공통: 어두운 별도 창, 색 읽기, 굴리는 판, 작은 그리기 도우미.
using System.Runtime.InteropServices;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.Modules.Workflow;

/// <summary>앱 밖 별도 창 (어두운 제목 줄, 단축키는 메인 창으로 넘김). 일정·참고 자료 보드·A/B 비교·피드백 보기</summary>
public class WfForm : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    public WfForm(string title, int w, int h)
    {
        Text = title; Width = w; Height = h;
        StartPosition = FormStartPosition.Manual; BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Web.Body; KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        if (App.Main != null && App.Main.Visible)
        {
            var r = App.Main.Bounds;
            Location = new Point(Math.Max(r.Left, r.Left + (r.Width - w) / 2), Math.Max(r.Top, r.Top + (r.Height - h) / 2));
        }
        else Location = new Point(80, 60);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }
    }
    /// <summary>글 입력 칸이 아닐 때 단축키는 메인 창으로 (이 창이 쓰는 키는 HandleKey 에서 먼저)</summary>
    protected virtual bool HandleKey(Keys k) => false;
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (ActiveControl is TextBoxBase or NumericUpDown or ComboBox or WebInput) return;
        if (HandleKey(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.KeyData == Keys.Escape) return;
        if (KeyMap.Handle(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; return; }
        var m = Registry.Menus.Find(x => x.Shortcut == e.KeyData && x.Run != null);
        if (m != null && (m.Enabled == null || m.Enabled())) { MainForm.Run(m.Run, m.Path); e.Handled = true; e.SuppressKeyPress = true; }
    }

    /// <summary>창을 화면 밖에 띄우고 그림으로 (시험·스크린숏용)</summary>
    public static Bitmap Shot(Form f, int w, int h)
    {
        f.StartPosition = FormStartPosition.Manual; f.ShowInTaskbar = false;
        f.Location = new Point(-8000, 0); f.Size = new Size(w, h);
        f.Show();
        for (int i = 0; i < 8; i++) { Application.DoEvents(); f.PerformLayout(); Thread.Sleep(10); }
        var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }
}

/// <summary>굴리는 판 + 세로 쌓기 (오른쪽 패널·창 안 내용)</summary>
public class WfScroll : Panel
{
    public readonly WebStack Stack = new() { Padding = new Padding(10, 4, 10, 12) };
    public WfScroll()
    {
        BackColor = Theme.Panel; Font = Web.Body;
        AutoScroll = false; HorizontalScroll.Maximum = 0; HorizontalScroll.Visible = false; AutoScroll = true;
        this.DarkScroll();
        Controls.Add(Stack);
    }
    /// <summary>글 줄바꿈 높이를 폭에 맞춰 다시 (Label 자동 크기가 한 줄로 되돌리는 것 막기)</summary>
    public void Relayout()
    {
        Stack.PerformLayout();
        if (IsHandleCreated) BeginInvoke((Action)(() => { if (!IsDisposed) Stack.PerformLayout(); }));
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Stack?.PerformLayout(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    /// <summary>내용 다시 채우기 (기존 것은 버림)</summary>
    public void Fill(IEnumerable<Control> items)
    {
        int y = VerticalScroll.Value;
        Stack.SuspendLayout();
        foreach (Control c in Stack.Controls.Cast<Control>().ToList()) c.Dispose();
        Stack.Controls.Clear();
        foreach (var c in items) if (c != null) Stack.Controls.Add(c);
        Stack.ResumeLayout();
        Relayout();
        try { if (y > 0) AutoScrollPosition = new Point(0, y); } catch { }
    }
}

public static class Wf
{
    public static Color C(string hex, Color? fb = null) { try { return string.IsNullOrEmpty(hex) ? fb ?? Color.Gray : ColorTranslator.FromHtml(hex); } catch { return fb ?? Color.Gray; } }
    public static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
    /// <summary>짧은 시간 글 (1:05, 45분)</summary>
    public static string Short(double sec) { long m = (long)Math.Round(sec / 60); return m >= 60 ? $"{m / 60}:{m % 60:00}" : m > 0 ? $"{m}분" : sec > 0 ? "<1분" : ""; }
    /// <summary>글자 색 (밝은 바탕이면 검정)</summary>
    public static Color On(Color bg) => bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114 > 150 ? Color.FromArgb(30, 30, 30) : Color.White;

    /// <summary>스스로 그리는 칸 (높이는 Measure 가 정함)</summary>
    public sealed class Painted : Control, IWebBlock
    {
        public Func<int, int> Measure;
        public Action<Graphics, Rectangle> Draw;
        public Painted(Func<int, int> measure, Action<Graphics, Rectangle> draw)
        {
            Measure = measure; Draw = draw;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Margin = new Padding(0, 4, 0, 4); Font = Web.Body;
        }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 1 && proposed.Width < 100000 ? proposed.Width : 280;
            return new Size(w, Measure(w));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Web.ParentBack(this));
            Web.Smooth(e.Graphics);
            try { Draw(e.Graphics, ClientRectangle); } catch { }
        }
    }
}
