// 선택 다듬기 (웹 ui/refine.js): 매끄럽게·경계 이동·페더·대비를 바로 보며 조절 + 머리카락 다듬기 브러시 (간단한 색 분리 매팅).
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.SelectFill;

public static class Refine
{
    public sealed class Opt2 { public double Smooth, Feather, Contrast, Shift; }

    /// <summary>마스크 알파(0~1, w×h) 다듬기 (refineAlpha)</summary>
    public static float[] RefineAlpha(float[] a, int w, int hgt, Opt2 o)
    {
        var A = a;
        if (o.Smooth > 0) { A = Raster.BoxBlur(A, w, hgt, Raster.JsRound(o.Smooth), 2); for (int i = 0; i < A.Length; i++) A[i] = A[i] >= 0.5 ? 1 : 0; }
        if (o.Shift != 0)
        {
            int r = Math.Max(1, Raster.JsRound(Math.Abs(o.Shift)));
            A = Raster.BoxBlur(A, w, hgt, r, 1);
            double t = o.Shift > 0 ? 0.08 : 0.92;
            for (int i = 0; i < A.Length; i++) A[i] = A[i] > t ? 1 : 0;
        }
        if (o.Feather > 0) A = Raster.BoxBlur(A, w, hgt, Math.Max(1, Raster.JsRound(o.Feather / 2)), 2);
        if (o.Contrast > 0)
        {
            double k = 1 + o.Contrast * 24, lo = 1 / (1 + Math.Exp(k * 0.5)), hi = 1 / (1 + Math.Exp(-k * 0.5));
            for (int i = 0; i < A.Length; i++) A[i] = (float)((1 / (1 + Math.Exp(-k * (A[i] - 0.5))) - lo) / (hi - lo));
        }
        return A;
    }

    /// <summary>
    /// 머리카락 다듬기 (refinePixels): 브러시 둘레에서 확실한 안(전경)·밖(배경) 색 평균을 찾아,
    /// 각 픽셀이 둘 중 어디에 가까운지로 선택 정도를 다시 정한다. img: RGBA, mask: 0~1 (w 폭)
    /// </summary>
    public static bool RefinePixels(byte[] img, float[] mask, int w, int cx, int cy, int r)
    {
        double fr = 0, fg = 0, fb = 0, br = 0, bg = 0, bb = 0; int fn = 0, bn = 0;
        double R2 = (r * 2.0) * (r * 2.0);
        int hgt = mask.Length / w;
        for (int y = Math.Max(0, cy - 2 * r); y < Math.Min(hgt, cy + 2 * r); y++)
            for (int x = Math.Max(0, cx - 2 * r); x < Math.Min(w, cx + 2 * r); x++)
            {
                if ((double)(x - cx) * (x - cx) + (double)(y - cy) * (y - cy) > R2) continue;
                int i = y * w + x; float m = mask[i];
                if (m > 0.95) { fr += img[i * 4]; fg += img[i * 4 + 1]; fb += img[i * 4 + 2]; fn++; }
                else if (m < 0.05) { br += img[i * 4]; bg += img[i * 4 + 1]; bb += img[i * 4 + 2]; bn++; }
            }
        if (fn == 0 || bn == 0) return false;
        fr /= fn; fg /= fn; fb /= fn; br /= bn; bg /= bn; bb /= bn;
        double dr = fr - br, dg = fg - bg, db = fb - bb, L2 = dr * dr + dg * dg + db * db;
        if (L2 < 300) return false;                        // 안팎 색이 거의 같으면 판단 불가
        for (int y = Math.Max(0, cy - r); y < Math.Min(hgt, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x < Math.Min(w, cx + r); x++)
            {
                double d2 = (double)(x - cx) * (x - cx) + (double)(y - cy) * (y - cy);
                if (d2 > (double)r * r) continue;
                int i = y * w + x;
                double t = ((img[i * 4] - br) * dr + (img[i * 4 + 1] - bg) * dg + (img[i * 4 + 2] - bb) * db) / L2;
                double a = Math.Max(0, Math.Min(1, (t - 0.1) / 0.8));
                double f = 1 - Math.Sqrt(d2) / r, k = Math.Min(1, f * 2);   // 브러시 가장자리는 조금만
                mask[i] = (float)(mask[i] * (1 - k) + a * k);
            }
        return true;
    }

    /// <summary>선택 다듬기 대화상자 (바로 보기, 취소하면 되돌림)</summary>
    public static void Dialog()
    {
        var doc = App.Doc;
        var sel = doc?.Sel;
        if (sel == null || !SelUtil.Active(sel)) { App.Toast("선택 영역이 없습니다"); return; }
        var orig = sel.Snapshot();
        var o = new Opt2
        {
            Smooth = SelUtil.GetD("refine.smooth", 0), Feather = SelUtil.GetD("refine.feather", 0),
            Contrast = SelUtil.GetD("refine.contrast", 0), Shift = SelUtil.GetD("refine.shift", 0),
        };
        const int pad = 60;
        var b = SelUtil.Bounds(sel);
        var R = new Rect(b.X - pad, b.Y - pad, b.W + pad * 2, b.H + pad * 2);
        var basev = new float[R.W * R.H];
        for (int y = 0; y < R.H; y++)
            for (int x = 0; x < R.W; x++)
            {
                int gx = R.X + x, gy = R.Y + y;
                if (gx >= 0 && gy >= 0 && gx < doc.W && gy < doc.H) basev[y * R.W + x] = orig[gy * doc.W + gx] / 255f;
            }
        byte[] Compute()
        {
            var A = RefineAlpha((float[])basev.Clone(), R.W, R.H, o);
            var s = new byte[A.Length];
            for (int i = 0; i < A.Length; i++) s[i] = (byte)Raster.JsRound(Math.Max(0, Math.Min(1, A[i])) * 255);
            return s;
        }
        void Show(byte[] shape) { sel.Restore(orig); SelUtil.ApplyShape(sel, R, shape, "replace"); SelUtil.RedrawSel(); }

        using var f = new Form { Text = "선택 다듬기", FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, BackColor = UI.Opt.Bg, ForeColor = UI.Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Font = new Font("Malgun Gothic", 9f) };
        var timer = new System.Windows.Forms.Timer { Interval = 90 };
        timer.Tick += (_, _) => { timer.Stop(); Show(Compute()); };
        void Preview() { timer.Stop(); timer.Start(); }
        var ok = UI.Opt.Button("적용", () => { f.DialogResult = DialogResult.OK; }, true);
        var cancel = UI.Opt.Button("취소", () => { f.DialogResult = DialogResult.Cancel; });
        var panel = UI.Opt.Panel(
            UI.Opt.Title("선택 다듬기"),
            UI.Opt.Slider("매끄럽게 (울퉁불퉁 정리)", 0, 30, () => o.Smooth, v => { o.Smooth = v; Preview(); }, step: 1),
            UI.Opt.Slider("경계 이동 (− 안쪽 / + 바깥쪽)", -30, 30, () => o.Shift, v => { o.Shift = v; Preview(); }, step: 1),
            UI.Opt.Slider("페더 (가장자리 흐리게)", 0, 60, () => o.Feather, v => { o.Feather = v; Preview(); }, step: 1),
            UI.Opt.Slider("대비 (가장자리 또렷하게)", 0, 1, () => o.Contrast, v => { o.Contrast = v; Preview(); }, pct: true, step: 0.05),
            UI.Opt.Help("그림 위 선택 영역에서 바로 확인됩니다. 머리카락·털 가장자리는 \"머리카락 다듬기\" 브러시(선택 메뉴)로 문지르세요."),
            UI.Opt.Row(cancel, ok));
        f.Controls.Add(panel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Shown += (_, _) => Preview();
        var res = f.ShowDialog(App.Main);
        timer.Stop(); timer.Dispose();
        sel.Restore(orig);
        if (res == DialogResult.OK)
        {
            var shape = Compute();
            SelUtil.SelHistory("선택 다듬기", () => SelUtil.ApplyShape(sel, R, shape, "replace"));
            SelUtil.Put("refine.smooth", o.Smooth); SelUtil.Put("refine.feather", o.Feather); SelUtil.Put("refine.contrast", o.Contrast); SelUtil.Put("refine.shift", o.Shift);
        }
        else SelUtil.RedrawSel();
    }
}

/// <summary>머리카락 다듬기 브러시</summary>
public sealed class RefineBrushTool : ToolBase
{
    public override string Id => "refinebrush";
    public override string Name => "머리카락 다듬기";
    public override string Icon => "〰";
    public double Size = SelUtil.GetD("refinebrush.size", 40);
    byte[] before;
    double hx = -1, hy = -1;
    readonly System.Diagnostics.Stopwatch redraw = new();
    public override bool Busy => before != null;

    public override void Down(PointerArgs e)
    {
        var sel = App.Doc.Sel;
        hx = e.SX; hy = e.SY;
        if (!SelUtil.Active(sel)) { App.Toast("먼저 대략 선택한 뒤, 가장자리(머리카락·털)를 문지르세요"); return; }
        before = sel.Snapshot();
        redraw.Restart();
        Dab(e);
    }
    public override void Move(PointerArgs e) { hx = e.SX; hy = e.SY; if (before != null) Dab(e); }
    public override void Hover(PointerArgs e) { hx = e.SX; hy = e.SY; }
    public override void Up(PointerArgs e)
    {
        if (before == null) return;
        var b = before; before = null;
        var sel = App.Doc.Sel;
        SelUtil.Normalize(sel);
        App.History.Push(new SelEdit("머리카락 다듬기", b, sel.Snapshot()));
        SelUtil.RedrawSel();
    }
    public override void Cancel() { if (before != null) { App.Doc.Sel.Restore(before); before = null; SelUtil.RedrawSel(); } }

    void Dab(PointerArgs e)
    {
        var doc = App.Doc; var sel = doc.Sel;
        if (sel.Mask == null) return;
        int r = Math.Max(3, Raster.JsRound(Size / 2)), R = r * 2;
        int ex = Raster.JsRound(e.X), ey = Raster.JsRound(e.Y);
        int x0 = Math.Max(0, ex - R), y0 = Math.Max(0, ey - R), x1 = Math.Min(doc.W, ex + R), y1 = Math.Min(doc.H, ey + R);
        int w = x1 - x0, hh = y1 - y0;
        if (w <= 0 || hh <= 0) return;
        // 보이는 그림 (흰 바탕 위)
        var area = new Rect(x0, y0, w, hh);
        var px = App.Comp.Render(doc, area, new RenderOptions { Paper = true, ExcludeDraft = true });
        var img = new byte[w * hh * 4];
        for (int i = 0; i < px.Length; i++) Raster.ToRgba(Px.Over(px[i], 0xFFFFFFFF), img, i * 4);
        var m = new float[w * hh];
        for (int y = 0; y < hh; y++) for (int x = 0; x < w; x++) m[y * w + x] = sel.Mask[(y0 + y) * doc.W + x0 + x] / 255f;
        if (!Refine.RefinePixels(img, m, w, ex - x0, ey - y0, r)) return;
        for (int y = 0; y < hh; y++) for (int x = 0; x < w; x++) sel.Mask[(y0 + y) * doc.W + x0 + x] = (byte)Raster.JsRound(m[y * w + x] * 255);
        if (redraw.ElapsedMilliseconds > 60) { SelUtil.RedrawSel(); redraw.Restart(); }
    }

    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (hx < 0) return;
        float r = (float)Math.Max(2, Size / 2 * v.Zoom);
        using var p = new Pen(Color.FromArgb(230, 232, 163, 61), 1);
        g.DrawEllipse(p, (float)hx - r, (float)hy - r, r * 2, r * 2);
        p.DashPattern = new[] { 3f, 3f };
        g.DrawEllipse(p, (float)hx - r * 2, (float)hy - r * 2, r * 4, r * 4);
    }
    public override Rectangle OverlayBounds(ViewXf v)
    {
        if (hx < 0) return Rectangle.Empty;
        int r = (int)Math.Ceiling(Math.Max(2, Size / 2 * v.Zoom) * 2) + 3;
        return new Rectangle((int)hx - r, (int)hy - r, r * 2, r * 2);
    }
    public override bool HandleKey(Keys k)
    {
        if (k == Keys.OemOpenBrackets) { Size = Math.Max(6, Size / 1.15); App.NotifySettings(); return true; }
        if (k == Keys.OemCloseBrackets) { Size = Math.Min(300, Size * 1.15); App.NotifySettings(); return true; }
        return false;
    }
    public override Control BuildOptions() => UI.Opt.Panel(
        UI.Opt.Title("머리카락 다듬기"),
        UI.Opt.Slider("크기", 6, 300, () => Size, v => { Size = v; SelUtil.Put("refinebrush.size", v); }, log: true, step: 1),
        UI.Opt.Help("대략 선택한 뒤 머리카락·털·흐린 가장자리를 따라 문지르세요. 브러시 둘레(점선 원)에서 확실한 안쪽 색과 바깥 색을 찾아, 그 사이 픽셀을 얼마나 선택할지 다시 정합니다."));
}
