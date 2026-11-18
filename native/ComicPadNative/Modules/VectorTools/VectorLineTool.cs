// 벡터 선 수정 도구 (클립스튜디오 "선 수정" 묶음처럼): 벡터 레이어의 선을 붓으로 문질러 고친다.
//  - 굵기: 문지른 곳의 선을 굵게/가늘게 (가운데일수록 많이, 필압으로 기록)
//  - 부드럽게: 문지른 곳의 떨림을 펴기 (끝점은 그대로)
//  - 단순화: 문지른 구간의 점을 줄이기 (허용 오차 안에서)
//  - 선 끝 연결: 한 선의 끝에서 다른 선의 끝까지 끌기 → 하나로 이음 (같은 선의 양 끝이면 닫기)
//  - 교차점까지 지우기: 닿은 선을 양옆의 가장 가까운 교차점까지만 지움 (벡터 지우개 "교차점까지" 와 같음)
//  - 선 끝 다듬기: 선을 탭하면 입·출 (시작·끝을 가늘게)
// 한 번 끌기 = 실행취소 한 번. 선 목록은 VectorLayer 가 다시 그림.
using System.Diagnostics;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.VectorTools;

public sealed class VectorLineTool : ToolBase
{
    public override string Id => "vlinefix";
    public override string Name => "벡터 선 수정";
    public override string Icon => "〰";
    public override bool AllowAltPick => false;
    public override Cursor Cursor => Cursors.Cross;

    public static readonly (string id, string text)[] Modes =
    {
        ("width", "굵기"), ("smooth", "부드럽게"), ("simplify", "단순화"), ("connect", "선 끝 연결"), ("cross", "교차점까지 지우기"), ("taper", "선 끝 다듬기"),
    };
    public string Mode = App.Get("vt.mode", "width");
    public double Size = App.Get("vt.size", 30.0), Strength = App.Get("vt.strength", 0.5);
    public bool Thin = App.Get("vt.thin", false);
    public static double SimplifyTol { get => App.Get("vt.simplifyTol", 1.5); set => App.Set("vt.simplifyTol", value); }
    public static double ConnectDist { get => App.Get("vt.connectDist", 12.0); set => App.Set("vt.connectDist", value); }
    public static double TaperIn { get => App.Get("vt.taperIn", 40.0); set => App.Set("vt.taperIn", value); }
    public static double TaperOut { get => App.Get("vt.taperOut", 40.0); set => App.Set("vt.taperOut", value); }

    bool Brushy => Mode is "width" or "smooth" or "simplify" or "cross";
    public override bool DrawsSizeCircle => Brushy;
    public override double CircleSize => Size;

    sealed class St { public Layer L; public JsonArray Before; public double LX, LY; public bool Changed; public PenFeel Feel; public (int s, int e, double x, double y)? From; public double CX, CY; }
    St st;
    readonly Stopwatch clock = Stopwatch.StartNew();
    long lastRender;
    public override bool Busy => st != null;

    static double Zoom => (App.Main as MainForm)?.Canvas?.V.Zoom ?? 1;

    public override void Down(PointerArgs e)
    {
        var l = VecEdit.VLayer();
        if (l == null) { App.Toast("벡터 레이어를 고르세요 (레이어 > 새 벡터 레이어)"); return; }
        if (!App.CheckEditable(l)) return;
        st = new St { L = l, Before = (JsonArray)VectorLayer.Strokes(l).DeepClone(), LX = e.X, LY = e.Y, Feel = PenFeel.Current(), CX = e.X, CY = e.Y };
        if (Mode == "taper")
        {
            var hit = VectorLayer.StrokeAt(l, e.X, e.Y, 8 / Zoom);
            if (hit == null) { App.Toast("선을 탭하세요"); st = null; return; }
            var list = VecEdit.Copy(l);
            var t = VecOps.Taper(list[hit.Value.index], TaperIn, TaperOut, st.Feel);
            st = null;
            if (t == null) return;
            list[hit.Value.index] = t;
            VecEdit.Commit(l, list, "선 끝 다듬기");
            return;
        }
        if (Mode == "connect")
        {
            st.From = NearestEnd(l, e.X, e.Y, Math.Max(ConnectDist, 16 / Zoom), null);
            if (st.From == null) { App.Toast("선 끝에서 시작해 다른 선 끝까지 끌어 주세요"); st = null; return; }
            Redraw();
            return;
        }
        Step(e.X, e.Y);
    }

    public override void Move(PointerArgs e)
    {
        var s = st; if (s == null) return;
        if (Mode == "connect") { s.CX = e.X; s.CY = e.Y; Redraw(); return; }
        // 빠르게 움직여도 빠지지 않게 지난 위치부터 촘촘히
        double d = Math.Sqrt((e.X - s.LX) * (e.X - s.LX) + (e.Y - s.LY) * (e.Y - s.LY));
        double gap = Math.Max(1, Size * 0.2);
        if (d < gap) return;
        int n = (int)Math.Ceiling(d / gap);
        double x0 = s.LX, y0 = s.LY;
        for (int i = 1; i <= n; i++) Step(x0 + (e.X - x0) * i / n, y0 + (e.Y - y0) * i / n);
    }

    /// <summary>붓 한 번 (반지름 Size/2)</summary>
    public void Step(double x, double y)
    {
        var s = st; if (s == null) return;
        s.LX = x; s.LY = y;
        double r = Size / 2;
        bool changed = false;
        if (Mode == "cross") changed = VectorLayer.EraseToCross(s.L, x, y, r);
        else
        {
            // 계산은 원래 선을 읽기만 하고 바뀐 선만 새로 만듦 (선이 많은 레이어에서도 가볍게)
            var cur = VectorLayer.Strokes(s.L);
            JsonObject[] res = null;
            for (int i = 0; i < cur.Count; i++)
            {
                if (cur[i] is not JsonObject one) continue;
                var o = Mode switch
                {
                    "width" => VecOps.WidthBrush(one, x, y, r, (Thin ? -1 : 1) * Strength * 0.06, s.Feel),
                    "smooth" => VecOps.SmoothBrush(one, x, y, r, Strength),
                    "simplify" => VecOps.SimplifyBrush(one, x, y, r, SimplifyTol * (0.5 + Strength)),
                    _ => null,
                };
                if (o != null) { res ??= new JsonObject[cur.Count]; res[i] = o; changed = true; }
            }
            if (changed)
            {
                var arr = new JsonArray();
                for (int i = 0; i < cur.Count; i++) arr.Add(res[i] != null ? res[i] : cur[i]?.DeepClone());
                VecEdit.PutStrokes(s.L, arr);
            }
        }
        if (!changed) return;
        s.Changed = true;
        // 다시 그리기는 무거우니 30ms 마다
        long now = clock.ElapsedMilliseconds;
        if (now - lastRender >= 30) { VectorLayer.Render(s.L, App.Doc); App.InvalidateAll(); lastRender = clock.ElapsedMilliseconds; }
    }

    public override void Up(PointerArgs e)
    {
        var s = st; st = null;
        if (s == null) return;
        if (Mode == "connect")
        {
            Redraw();
            var to = NearestEnd(s.L, e.X, e.Y, Math.Max(ConnectDist, 16 / Zoom), s.From);
            if (to == null) { App.Toast("다른 선 끝에서 손을 떼세요"); return; }
            ConnectPair(s.L, s.From.Value.s, s.From.Value.e, to.Value.s, to.Value.e);
            return;
        }
        if (!s.Changed) return;
        VectorLayer.SetStrokes(s.L, VectorLayer.Strokes(s.L), Label, s.Before);
        VecEdit.SelTool?.Restamp(s.L);
    }

    string Label => Mode switch { "width" => "선 굵기 수정", "smooth" => "선 부드럽게", "simplify" => "선 단순화", "cross" => "교차점까지 지우기", _ => "벡터 선 수정" };

    public override void Cancel()
    {
        var s = st; st = null; if (s == null) return;
        var m = s.L.Meta != null ? (JsonObject)s.L.Meta.DeepClone() : new JsonObject(); m["strokes"] = s.Before; s.L.Meta = m;
        VectorLayer.Render(s.L, App.Doc); App.InvalidateAll();
    }

    /// <summary>두 선 끝을 이음 (a 의 ea 끝 ↔ b 의 eb 끝). 같은 선의 양 끝이면 닫음</summary>
    public static bool ConnectPair(Layer l, int a, int ea, int b, int eb)
    {
        var list = VecEdit.Copy(l);
        if (a < 0 || b < 0 || a >= list.Count || b >= list.Count) return false;
        if (a == b)
        {
            if (ea == eb) return false;
            list[a] = VecOps.Close(list[a]);
        }
        else
        {
            list[Math.Min(a, b)] = a < b ? VecOps.Join(list[a], ea, list[b], eb) : VecOps.Join(list[b], eb, list[a], ea);
            list.RemoveAt(Math.Max(a, b));
        }
        VecEdit.Commit(l, list, "선 끝 연결");
        return true;
    }

    /// <summary>(x, y) 에서 tol 안의 가장 가까운 선 끝 (exclude 끝은 빼고)</summary>
    public static (int s, int e, double x, double y)? NearestEnd(Layer l, double x, double y, double tol, (int s, int e, double x, double y)? exclude)
    {
        var strokes = VectorLayer.Strokes(l);
        (int, int, double, double)? best = null; double bd = tol;
        for (int k = 0; k < strokes.Count; k++)
        {
            if (strokes[k] is not JsonObject s) continue;
            var p = VecOps.Pts(s);
            if (p.Length < 4) continue;
            for (int e = 0; e < 2; e++)
            {
                if (exclude is { } ex && ex.s == k && ex.e == e) continue;
                var (ex2, ey2) = VecOps.EndOf(p, e);
                double d = Math.Sqrt((ex2 - x) * (ex2 - x) + (ey2 - y) * (ey2 - y));
                if (d <= bd) { bd = d; best = (k, e, ex2, ey2); }
            }
        }
        return best;
    }

    static void Redraw() => (App.Main as MainForm)?.Canvas?.RedrawAll();

    // ---- 화면 ------------------------------------------------------------------------------------------------
    public override Rectangle OverlayBounds(ViewXf v)
    {
        var c = (App.Main as MainForm)?.Canvas;
        return Mode == "connect" && c != null ? new Rectangle(0, 0, c.Width, c.Height) : Rectangle.Empty;
    }
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (Mode != "connect") return;
        var l = VecEdit.VLayer(); if (l == null) return;
        // 선 끝 표시 (많으면 화면 안 것만)
        var strokes = VectorLayer.Strokes(l);
        using var ring = new Pen(Color.FromArgb(230, 90, 40), 1.5f);
        int shown = 0;
        foreach (var n in strokes)
        {
            if (n is not JsonObject s) continue;
            var p = VecOps.Pts(s); if (p.Length < 4) continue;
            for (int e = 0; e < 2 && shown < 4000; e++)
            {
                var (x, y) = VecOps.EndOf(p, e); var q = v.ToScreen(x, y);
                if (q.x < -10 || q.y < -10 || q.x > 20000 || q.y > 20000) continue;
                g.FillEllipse(Brushes.White, (float)q.x - 3.5f, (float)q.y - 3.5f, 7, 7); g.DrawEllipse(ring, (float)q.x - 3.5f, (float)q.y - 3.5f, 7, 7); shown++;
            }
        }
        var s0 = st;
        if (s0?.From is { } f)
        {
            var a = v.ToScreen(f.x, f.y); var b = v.ToScreen(s0.CX, s0.CY);
            using var w = new Pen(Color.White, 3); using var bl = new Pen(Color.FromArgb(230, 90, 40), 1.5f) { DashPattern = new[] { 4f, 3f } };
            g.DrawLine(w, (float)a.x, (float)a.y, (float)b.x, (float)b.y); g.DrawLine(bl, (float)a.x, (float)a.y, (float)b.x, (float)b.y);
        }
    }

    public override Control BuildOptions()
    {
        var box = (FlowLayoutPanel)Opt.Panel();
        void Render()
        {
            if (box.IsDisposed) return;
            box.SuspendLayout();
            foreach (Control c in box.Controls.Cast<Control>().ToList()) c.Dispose();
            box.Controls.Add(Opt.Title("벡터 선 수정"));
            box.Controls.Add(Opt.Seg(Modes.Take(3).ToArray(), () => Mode, v => { Mode = v; App.Set("vt.mode", v); box.BeginInvoke((Action)Render); }));
            box.Controls.Add(Opt.Seg(Modes.Skip(3).ToArray(), () => Mode, v => { Mode = v; App.Set("vt.mode", v); box.BeginInvoke((Action)Render); }));
            if (Brushy) box.Controls.Add(Opt.Slider("붓 크기", 2, 400, () => Size, v => { Size = v; App.Set("vt.size", v); }, log: true, step: 1, fmt: v => $"{v:0}px"));
            switch (Mode)
            {
                case "width":
                    box.Controls.Add(Opt.Seg(new[] { (false, "굵게"), (true, "가늘게") }, () => Thin, v => { Thin = v; App.Set("vt.thin", v); }));
                    box.Controls.Add(Opt.Slider("세기", 0.05, 1, () => Strength, v => { Strength = v; App.Set("vt.strength", v); }, pct: true, step: 0.01));
                    box.Controls.Add(Opt.Help("문지른 곳의 선이 조금씩 굵어지거나 가늘어집니다 (붓 가운데일수록 많이). 여러 번 문지르면 더 바뀝니다."));
                    break;
                case "smooth":
                    box.Controls.Add(Opt.Slider("세기", 0.05, 1, () => Strength, v => { Strength = v; App.Set("vt.strength", v); }, pct: true, step: 0.01));
                    box.Controls.Add(Opt.Help("문지른 곳의 떨린 선을 매끄럽게 폅니다. 선의 양 끝은 움직이지 않습니다."));
                    break;
                case "simplify":
                    box.Controls.Add(Opt.Slider("허용 오차", 0.2, 10, () => SimplifyTol, v => SimplifyTol = v, step: 0.1, fmt: v => $"{v:0.0}px"));
                    box.Controls.Add(Opt.Help("문지른 구간에서 모양에 꼭 필요한 점만 남깁니다 (선이 허용 오차 이상 바뀌지 않음)."));
                    break;
                case "connect":
                    box.Controls.Add(Opt.Slider("자동 연결 거리", 1, 60, () => ConnectDist, v => ConnectDist = v, step: 1, fmt: v => $"{v:0}px"));
                    box.Controls.Add(Opt.Button("가까운 선 끝 모두 잇기 (고른 선 / 전부)", () => { int j = VecEdit.Connect(VecEdit.VLayer(), ConnectDist); App.Toast(j > 0 ? $"선 끝 {j}곳을 이었습니다" : "가까운 선 끝이 없습니다"); }));
                    box.Controls.Add(Opt.Help("주황 동그라미(선 끝)에서 다른 선 끝까지 끌면 두 선이 하나로 이어집니다. 같은 선의 반대쪽 끝이면 닫힌 선이 됩니다."));
                    break;
                case "cross":
                    box.Controls.Add(Opt.Help("닿은 선을 양옆의 가장 가까운 교차점까지만 지웁니다 (삐져나온 선 정리). 지우개 도구의 벡터 지우개 \"교차점까지\" 와 같습니다."));
                    break;
                case "taper":
                    box.Controls.Add(Opt.Slider("입 (시작 가늘게)", 0, 300, () => TaperIn, v => TaperIn = v, step: 1, fmt: v => v > 0 ? $"{v:0}px" : "끔"));
                    box.Controls.Add(Opt.Slider("출 (끝 가늘게)", 0, 300, () => TaperOut, v => TaperOut = v, step: 1, fmt: v => v > 0 ? $"{v:0}px" : "끔"));
                    box.Controls.Add(Opt.Button("고른 선 / 전부에 적용", () => VecEdit.Taper(VecEdit.VLayer(), TaperIn, TaperOut)));
                    box.Controls.Add(Opt.Help("선을 탭하면 시작·끝이 붓처럼 가늘어집니다. 다 그린 뒤에 입·출을 넣을 때."));
                    break;
            }
            box.ResumeLayout();
        }
        Render();
        return box;
    }
}
