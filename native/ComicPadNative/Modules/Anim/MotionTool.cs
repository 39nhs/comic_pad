// 모션 경로 도구 — 웹 판 tools/motiontool.js
// 지금 트랙의 그림을 끌어 이 프레임의 위치 키를 찍는다 (Shift = 회전, Alt = 크기). 경로와 키 점이 원고 위에 보이고, 키 점을 끌면 그 키를 옮긴다.
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public sealed class MotionTool : ToolBase
{
    public override string Id => "motion";
    public override string Name => "모션 경로 (애니)";
    public override string Icon => "⤳";
    public override Cursor Cursor => Cursors.SizeAll;

    sealed class Drag { public Layer T; public int Kf; public StructSnap Before; public MoValues Start; public double E0x, E0y, A0, D0, Cx, Cy; public string Mode; }
    Drag drag;
    public override bool Busy => drag != null;

    static ViewXf View => (App.Main as UI.MainForm)?.Canvas.V;

    public override void Down(PointerArgs e)
    {
        var d = App.Doc;
        var t = AnimCore.AnimFolder(d);
        if (t == null) { App.Toast("애니메이션 트랙을 먼저 고르세요 (애니 패널)"); return; }
        int f = AnimCore.Frame(d);
        var (ax, ay) = Motion.Anchor(t, d);
        var before = StructSnap.Take(d);
        // 키 점을 잡았는지 (화면 12px 안)
        int? hit = null;
        var v = View;
        if (v != null)
            foreach (var k in Motion.Keys(t) ?? new())
            {
                var (sx, sy) = v.ToScreen(ax + J.D(k["x"]), ay + J.D(k["y"]));
                if (Math.Sqrt((sx - e.SX) * (sx - e.SX) + (sy - e.SY) * (sy - e.SY)) < 12) { hit = J.I(k["f"]); break; }
            }
        int kf = hit ?? f;
        var start = Motion.ValuesAt(t, kf) ?? MoValues.Id;
        // 이 프레임에 키가 없으면 지금 값으로 하나 만들어 두고 끌기
        var m = (JsonObject)Motion.Of(t)?.DeepClone() ?? new JsonObject { ["keys"] = new JsonArray() };
        var keys = J.Arr(m, "keys");
        if (!keys.OfType<JsonObject>().Any(k => J.I(k["f"]) == kf)) { keys.Add(Motion.NewKey(kf, start)); Motion.Sort(keys); }
        t.Meta["motion"] = m;
        double cx = ax + start.X, cy = ay + start.Y;
        drag = new Drag
        {
            T = t, Kf = kf, Before = before, Start = start, E0x = e.X, E0y = e.Y, Cx = cx, Cy = cy,
            A0 = Math.Atan2(e.Y - cy, e.X - cx), D0 = Math.Max(4, Math.Sqrt((e.X - cx) * (e.X - cx) + (e.Y - cy) * (e.Y - cy))),
            Mode = e.Shift ? "rot" : e.Alt ? "scale" : "move",
        };
    }

    public override void Move(PointerArgs e)
    {
        var dr = drag;
        if (dr == null) return;
        var k = Motion.Keys(dr.T)?.FirstOrDefault(q => J.I(q["f"]) == dr.Kf);
        if (k == null) return;
        if (dr.Mode == "move") { k["x"] = Math.Round(dr.Start.X + (e.X - dr.E0x), 2); k["y"] = Math.Round(dr.Start.Y + (e.Y - dr.E0y), 2); }
        else if (dr.Mode == "rot")
        {
            double r = dr.Start.R + (Math.Atan2(e.Y - dr.Cy, e.X - dr.Cx) - dr.A0) * 180 / Math.PI;
            if (e.Ctrl) r = Math.Round(r / 15) * 15;
            k["r"] = Math.Round(r, 2);
        }
        else k["s"] = Math.Round(Math.Max(0.05, dr.Start.S * (Math.Sqrt((e.X - dr.Cx) * (e.X - dr.Cx) + (e.Y - dr.Cy) * (e.Y - dr.Cy)) / dr.D0)), 4);
        App.InvalidateAll();
    }

    public override void Up(PointerArgs e)
    {
        var dr = drag;
        drag = null;
        if (dr == null) return;
        var d = App.Doc;
        App.History.Push(new StructEdit("모션 키", dr.Before, StructSnap.Take(d)));
        d.Modified = true;
        AnimCore.Refresh(true);
    }

    public override void Cancel()
    {
        var dr = drag; drag = null;
        if (dr == null) return;
        dr.Before.Apply(App.Doc);
        App.InvalidateAll();
    }

    List<PointF> PathPoints(ViewXf v, Layer t, Document d)
    {
        int n = AnimCore.FrameCount(d);
        var (ax, ay) = Motion.Anchor(t, d);
        var pts = new List<PointF>();
        for (int f = 0; f < n; f++) { var vv = Motion.ValuesAt(t, f) ?? MoValues.Id; var (sx, sy) = v.ToScreen(ax + vv.X, ay + vv.Y); pts.Add(new PointF((float)sx, (float)sy)); }
        return pts;
    }

    public override Rectangle OverlayBounds(ViewXf v)
    {
        var d = App.Doc;
        var r = PixelTools.GridBounds(v);
        if (d == null) return r;
        var t = AnimCore.AnimFolder(d);
        if (t == null) return r;
        var pts = PathPoints(v, t, d);
        var (ax, ay) = Motion.Anchor(t, d);
        var (asx, asy) = v.ToScreen(ax, ay);
        pts.Add(new PointF((float)asx, (float)asy));
        foreach (var k in Motion.Keys(t) ?? new()) { var (sx, sy) = v.ToScreen(ax + J.D(k["x"]), ay + J.D(k["y"])); pts.Add(new PointF((float)sx, (float)sy)); }
        if (Camera.Keys(d).Count > 0)
        {
            // 카메라 영역도 (원고 전체 범위로 넉넉히)
            foreach (var (px, py) in new[] { (0.0, 0.0), (d.W, 0.0), (0.0, d.H), (d.W, (double)d.H) }) { var (sx, sy) = v.ToScreen(px, py); pts.Add(new PointF((float)sx, (float)sy)); }
        }
        float x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y), x1 = pts.Max(p => p.X), y1 = pts.Max(p => p.Y);
        var b = Rectangle.FromLTRB((int)x0 - 24, (int)y0 - 24, (int)x1 + 40, (int)y1 + 24);
        return r.IsEmpty ? b : Rectangle.Union(r, b);
    }

    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var d = App.Doc;
        if (d == null) return;
        PixelTools.DrawGrid(g, v);
        Camera.DrawOverlay(g, v, d);
        var t = AnimCore.AnimFolder(d);
        if (t == null) return;
        int f0 = AnimCore.Frame(d);
        var pts = PathPoints(v, t, d);
        var (ax, ay) = Motion.Anchor(t, d);
        if (pts.Count > 1)
        {
            using var pen = new Pen(Color.FromArgb(217, 232, 163, 61), 2) { DashPattern = new[] { 2.5f, 2f } };
            g.DrawLines(pen, pts.ToArray());
        }
        using (var dot = new SolidBrush(Color.FromArgb(178, 232, 163, 61)))
            for (int f = 0; f < pts.Count; f++)
            {
                float rr = f == f0 ? 4 : 2;
                g.FillEllipse(f == f0 ? Brushes.White : dot, pts[f].X - rr, pts[f].Y - rr, rr * 2, rr * 2);
            }
        using var keyPen = new Pen(Color.FromArgb(0xe8, 0xa3, 0x3d), 2);
        using var keyFill = new SolidBrush(Color.FromArgb(0xe8, 0xa3, 0x3d));
        using var font = new Font("Malgun Gothic", 7.5f);
        foreach (var k in Motion.Keys(t) ?? new())
        {
            var (sx, sy) = v.ToScreen(ax + J.D(k["x"]), ay + J.D(k["y"]));
            var rc = new RectangleF((float)sx - 6, (float)sy - 6, 12, 12);
            g.FillRectangle(J.I(k["f"]) == f0 ? keyFill : Brushes.White, rc);
            g.DrawRectangle(keyPen, rc.X, rc.Y, rc.Width, rc.Height);
            g.DrawString((J.I(k["f"]) + 1).ToString(), font, Brushes.Black, (float)sx + 8, (float)sy - 16);
        }
        // 기준점 (회전·크기의 중심)
        var (cx, cy) = v.ToScreen(ax, ay);
        using var ap = new Pen(Color.FromArgb(230, 61, 123, 224), 1.5f);
        g.DrawLine(ap, (float)cx - 8, (float)cy, (float)cx + 8, (float)cy);
        g.DrawLine(ap, (float)cx, (float)cy - 8, (float)cx, (float)cy + 8);
    }

    public override Control BuildOptions() => Live(() =>
    {
        var d = App.Doc; var t = AnimCore.AnimFolder(d);
        if (t == null) return Ui.Section("모션 경로", Ui.Help("애니메이션 트랙을 고른 뒤 쓰세요 (애니 패널)."));
        return Section(t, d);
    });

    /// <summary>애니가 바뀔 때마다 다시 만드는 옵션 칸</summary>
    public static Control Live(Func<Control> build)
    {
        var host = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, BackColor = Ui.Bg, Padding = new Padding(6) };
        var timer = new System.Windows.Forms.Timer { Interval = 60 };
        void Rebuild() { timer.Stop(); if (host.IsDisposed) return; host.SuspendLayout(); foreach (Control c in host.Controls) c.Dispose(); host.Controls.Clear(); try { host.Controls.Add(build()); } catch (Exception ex) { host.Controls.Add(Ui.Help("오류: " + ex.Message)); } host.ResumeLayout(); }
        timer.Tick += (_, _) => Rebuild();
        Action soon = () => { if (!host.IsDisposed && !AnimCore.Playing) { timer.Stop(); timer.Start(); } };
        AnimCore.Changed += soon; App.LayersChanged += soon;
        host.Disposed += (_, _) => { AnimCore.Changed -= soon; App.LayersChanged -= soon; timer.Dispose(); };
        Rebuild();
        return host;
    }

    /// <summary>옵션·애니 패널 공용: 지금 프레임의 키 값과 버튼</summary>
    public static Control Section(Layer t, Document d)
    {
        int f = AnimCore.Frame(d);
        var m = Motion.Of(t);
        var keys = Motion.Keys(t) ?? new();
        var key = keys.FirstOrDefault(k => J.I(k["f"]) == f);
        StructSnap snap = null;
        Control Sl(string label, string prop, double min, double max, double step, Func<double, string> fmt)
        {
            double Get() { var v = Motion.ValuesAt(t, f) ?? MoValues.Id; return prop switch { "x" => v.X, "y" => v.Y, "s" => v.S, "r" => v.R, _ => v.O }; }
            return Ui.Slider(label, min, max, step, Get, val => { var k = Motion.Keys(t)?.FirstOrDefault(q => J.I(q["f"]) == f); if (k != null) { k[prop] = val; App.InvalidateAll(); } }, fmt,
                begin: () => snap = StructSnap.Take(d),
                end: () => { if (snap != null) { App.History.Push(new StructEdit("모션 키", snap, StructSnap.Take(d))); d.Modified = true; snap = null; AnimCore.Refresh(true); } });
        }
        var items = new List<Control>
        {
            Ui.Help(keys.Count > 0 ? "키 사이 프레임은 이징을 따라 자동으로 움직입니다. 원고 위 경로의 네모 = 키 (끌어서 옮기기)." : "모션 경로 도구로 그림을 끌면 이 프레임에 위치 키가 생깁니다 (Shift = 회전, Alt = 크기). 키를 두 개 이상 찍으면 사이가 자동으로 움직입니다."),
            Ui.Row(Ui.Btn(key != null ? "키 다시 넣기" : "이 프레임에 키", () => Motion.SetKey(t, f)), key != null ? Ui.Btn("키 지우기", () => Motion.RemoveKey(t, f)) : null, Ui.Btn("모션 경로 도구", () => App.SetTool("motion"))),
        };
        if (key != null)
        {
            items.Add(Sl("가로 이동", "x", -d.W, d.W, 1, x => Math.Round(x) + "px"));
            items.Add(Sl("세로 이동", "y", -d.H, d.H, 1, x => Math.Round(x) + "px"));
            items.Add(Sl("크기", "s", 0.05, 4, 0.01, x => Math.Round(x * 100) + "%"));
            items.Add(Sl("회전", "r", -360, 360, 1, x => Math.Round(x) + "°"));
            items.Add(Sl("불투명도", "o", 0, 1, 0.01, x => Math.Round(x * 100) + "%"));
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, BackColor = Ui.Bg2, ForeColor = Ui.Fg };
            foreach (var (_, label) in Motion.Eases) cb.Items.Add(label);
            string cur = J.S(key["ease"]) ?? "inout";
            cb.SelectedIndex = Math.Max(0, Array.FindIndex(Motion.Eases, x => x.id == cur));
            cb.SelectedIndexChanged += (_, _) => Motion.SetKey(t, f, new JsonObject { ["ease"] = Motion.Eases[cb.SelectedIndex].id });
            items.Add(Ui.Text("다음 키까지 움직임"));
            items.Add(cb);
            if (cur == "bezier")
            {
                var bz = key["bz"] is JsonArray ba && ba.Count >= 4 ? ba.Select(n => J.D(n)).ToArray() : new[] { 0.42, 0, 0.58, 1 };
                var row = Ui.Row();
                for (int i = 0; i < 4; i++)
                {
                    int ii = i;
                    var nu = new NumericUpDown { Width = 60, DecimalPlaces = 2, Increment = 0.05m, Minimum = -2, Maximum = 3, Value = (decimal)Math.Clamp(bz[i], -2, 3), BackColor = Ui.Bg2, ForeColor = Ui.Fg };
                    new ToolTip().SetToolTip(nu, new[] { "x1", "y1", "x2", "y2" }[i]);
                    nu.ValueChanged += (_, _) => { var nb = (double[])bz.Clone(); nb[ii] = (double)nu.Value; Motion.SetKey(t, f, new JsonObject { ["bz"] = new JsonArray(nb.Select(x => (JsonNode)x).ToArray()) }); };
                    row.Controls.Add(nu);
                }
                items.Add(row);
            }
        }
        if (m != null)
        {
            items.Add(Ui.Row(Ui.Btn("기준점 = 원고 가운데", () => { var nm = (JsonObject)m.DeepClone(); nm["ax"] = d.W / 2.0; nm["ay"] = d.H / 2.0; Motion.SetMotion(t, nm, "모션 기준점"); }),
                Ui.Btn("기준점 = 이 그림 가운데", () => { var c = AnimCore.CelAt(t, f, d); var b = Motion.OpaqueCenter(c); if (b != null) { var nm = (JsonObject)m.DeepClone(); nm["ax"] = b.Value.x; nm["ay"] = b.Value.y; Motion.SetMotion(t, nm, "모션 기준점"); } })));
            items.Add(Ui.Row(Ui.Btn("모든 키 지우기", () => Motion.SetMotion(t, null, "모션 지우기")), Ui.Btn("그림으로 굳히기", () => { int k = AnimCore.BakeMotion(t); if (k > 0) App.Toast($"움직인 프레임 {k}장을 그림으로 만들었습니다"); })));
        }
        return Ui.Section($"모션 경로 · \"{t.Name}\" {f + 1}번 프레임{(key != null ? " (키)" : "")}", items.ToArray());
    }
}
