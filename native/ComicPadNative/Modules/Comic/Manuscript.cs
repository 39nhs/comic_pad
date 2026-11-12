// 원고 설정·인쇄 가이드 (웹 ui/manuscript.js), 인쇄·투고 전 점검 (ui/preflight.js), 메모 핀, 웹툰 미리보기.
// 원고 설정은 문서 Extra["print"] = { size, mmW, mmH, bleedMm, safeMm, trimW, trimH, bleed, safe } (웹 doc.print 와 같음),
// 메모는 Extra["notes"] = [{ id, x, y, text, color, done, author }].
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

public static class Manuscript
{
    public static readonly (string id, string label, double w, double h)[] Sizes =
    {
        ("b5", "B5 단행본·동인지 (182×257mm)", 182, 257), ("a5", "A5 (148×210mm)", 148, 210), ("b6", "B6 소형 단행본 (128×182mm)", 128, 182),
        ("a4", "A4 (210×297mm)", 210, 297), ("b4", "투고 원고 B4 (257×364mm)", 257, 364), ("custom", "직접 입력", 0, 0),
    };
    public static int Mm2Px(double mm, double dpi) => (int)Math.Round(mm / 25.4 * dpi, MidpointRounding.AwayFromZero);

    public readonly record struct Rects((double X, double Y, double W, double H) trim, (double X, double Y, double W, double H) bleed, (double X, double Y, double W, double H) safe);

    /// <summary>재단/도련/안전 영역 (문서 px). 캔버스 가운데 기준</summary>
    public static Rects? PrintRects(Document d)
    {
        if (d?.Extra["print"] is not JsonObject p) return null;
        double tw = Math.Min(J.Num(p, "trimW"), d.W), th = Math.Min(J.Num(p, "trimH"), d.H), bl = J.Num(p, "bleed"), sf = J.Num(p, "safe");
        var trim = (Bubbles.JsRound((d.W - tw) / 2), Bubbles.JsRound((d.H - th) / 2), tw, th);
        var bleed = (trim.Item1 - bl, trim.Item2 - bl, tw + bl * 2, th + bl * 2);
        var safe = (trim.Item1 + sf, trim.Item2 + sf, Math.Max(0, tw - sf * 2), Math.Max(0, th - sf * 2));
        return new Rects(trim, bleed, safe);
    }

    public static void SetupDialog()
    {
        var d = App.Doc; if (d == null) return;
        var p = d.Extra["print"] as JsonObject;
        var v = CUI.Form("원고 설정 (인쇄용 가이드)", new[]
        {
            CUI.Sel("size", "완성(재단) 크기", Sizes.Select(s => (s.id, s.label)).ToArray(), J.Str(p, "size", "b5")),
            CUI.Num("w", "직접 입력: 가로 (mm)", J.Num(p, "mmW", 182), 10, 2000), CUI.Num("h", "직접 입력: 세로 (mm)", J.Num(p, "mmH", 257), 10, 2000),
            CUI.Num("dpi", "해상도 (dpi)", d.Dpi, 72, 1200),
            CUI.Num("bleed", "도련 (재단선 바깥, mm)", J.Num(p, "bleedMm", 3), 0, 50, 1), CUI.Num("safe", "안전선 (재단선 안쪽, mm)", J.Num(p, "safeMm", 5), 0, 100, 1),
            CUI.Chk("fit", "캔버스 크기를 도련 크기에 맞추기", p == null),
            CUI.Info("빨간 점선(도련)까지 그림을 채우고, 대사·중요한 그림은 파란 점선(안전선) 안에 두세요. 재단 후에는 검은 선(재단선) 안쪽만 남습니다."),
        }, "적용", p != null ? new[] { ("가이드 없애기", "remove") } : null);
        if (v == null) return;
        if (CUI.S(v, "$extra") == "remove") { SetPrint(null, d.Dpi, "원고 가이드 제거"); return; }
        var preset = Sizes.FirstOrDefault(s => s.id == CUI.S(v, "size"));
        double mmW = preset.w > 0 ? preset.w : CUI.D(v, "w"), mmH = preset.h > 0 ? preset.h : CUI.D(v, "h");
        int dpi = (int)Math.Clamp(CUI.D(v, "dpi") > 0 ? CUI.D(v, "dpi") : 350, 72, 1200);
        var next = new JsonObject
        {
            ["size"] = CUI.S(v, "size"), ["mmW"] = mmW, ["mmH"] = mmH, ["bleedMm"] = CUI.D(v, "bleed"), ["safeMm"] = CUI.D(v, "safe"),
            ["trimW"] = Mm2Px(mmW, dpi), ["trimH"] = Mm2Px(mmH, dpi), ["bleed"] = Mm2Px(CUI.D(v, "bleed"), dpi), ["safe"] = Mm2Px(CUI.D(v, "safe"), dpi),
        };
        if (CUI.B(v, "fit"))
        {
            int W = (int)J.Num(next, "trimW") + (int)J.Num(next, "bleed") * 2, H = (int)J.Num(next, "trimH") + (int)J.Num(next, "bleed") * 2;
            if ((long)W * H > 12000L * 12000) { App.Toast("너무 큽니다. 해상도를 낮춰 주세요"); return; }
            if (W != d.W || H != d.H) { CanvasOps.ResizeCanvas(W, H, 0.5, 0.5, "원고 설정", doc => { doc.Dpi = dpi; doc.Extra["print"] = next; }); return; }
        }
        SetPrint(next, dpi, "원고 가이드");
    }

    static void SetPrint(JsonObject next, int dpi, string label)
    {
        var d = App.Doc;
        App.History.Push(new PrintEdit(label, d.Dpi, d.Extra["print"], dpi, next));
        d.Dpi = dpi;
        if (next == null) d.Extra.Remove("print"); else d.Extra["print"] = next.DeepClone();
        d.Modified = true;
        MetaLayers.Redraw();
        (App.Main as MainForm)?.UpdateTitle();
    }

    sealed class PrintEdit : IEdit
    {
        readonly int dpi0, dpi1; readonly JsonNode p0, p1;
        public string Label { get; }
        public PrintEdit(string label, int d0, JsonNode a, int d1, JsonNode b) { Label = label; dpi0 = d0; dpi1 = d1; p0 = a?.DeepClone(); p1 = b?.DeepClone(); }
        public long Bytes => 512;
        Rect Put(Document d, int dpi, JsonNode p) { d.Dpi = dpi; if (p == null) d.Extra.Remove("print"); else d.Extra["print"] = p.DeepClone(); return Rect.Empty; }
        public Rect Undo(Document d) => Put(d, dpi0, p0);
        public Rect Redo(Document d) => Put(d, dpi1, p1);
    }

    /// <summary>원고 가이드 (늘 그림)</summary>
    public static void DrawGuides(Graphics g, ViewXf v)
    {
        var d = App.Doc;
        var r = PrintRects(d);
        if (r == null || !App.Get("printGuides", true)) return;
        PointF[] Q((double X, double Y, double W, double H) q) => new[] { Gfx.S(v, q.X, q.Y), Gfx.S(v, q.X + q.W, q.Y), Gfx.S(v, q.X + q.W, q.Y + q.H), Gfx.S(v, q.X, q.Y + q.H) };
        // 도련 바깥은 살짝 어둡게
        using (var path = new System.Drawing.Drawing2D.GraphicsPath(System.Drawing.Drawing2D.FillMode.Alternate))
        {
            path.AddPolygon(Q((0, 0, d.W, d.H))); path.AddPolygon(Q(r.Value.bleed));
            using var br = new SolidBrush(Color.FromArgb(46, 0, 0, 0));
            g.FillPath(br, path);
        }
        using (var red = CUI.Dash(Color.FromArgb(229, 72, 77), 1, 6, 4)) g.DrawPolygon(red, Q(r.Value.bleed));
        using (var blue = CUI.Dash(Color.FromArgb(47, 124, 246), 1, 6, 4)) g.DrawPolygon(blue, Q(r.Value.safe));
        using var blk = new Pen(Color.FromArgb(17, 17, 17), 1.2f);
        var t = Q(r.Value.trim);
        g.DrawPolygon(blk, t);
        // 모서리 재단 표시(돔보) — 화면 기준
        var b = Gfx.ToScreen(v, r.Value.trim.X, r.Value.trim.Y, r.Value.trim.W, r.Value.trim.H);
        const float L = 18;
        foreach (var (x, y, sx, sy) in new[] { (b.Left, b.Top, -1, -1), (b.Right, b.Top, 1, -1), (b.Left, b.Bottom, -1, 1), (b.Right, b.Bottom, 1, 1) })
        {
            g.DrawLine(blk, x, y + sy * 4, x, y + sy * L);
            g.DrawLine(blk, x + sx * 4, y, x + sx * L, y);
        }
    }

    public static void ToggleGuides() { App.Set("printGuides", !App.Get("printGuides", true)); MetaLayers.Redraw(); }

    // ---- 웹툰 미리보기 ----
    public static void WebtoonPreview()
    {
        var d = App.Doc; if (d == null) return;
        int width = App.Get("webtoonPreviewW", 390);
        using var f = new Form { Text = "웹툰 미리보기", StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, Width = 700, Height = 900, Font = new Font("Malgun Gothic", 9f) };
        UI.Dialogs.Modals.Adopt(f);
        var flat = App.Comp.Flatten(d, new RenderOptions { ExcludeDraft = true });
        using var full = CpadIO.ToBitmap(flat, flat.Full, 0xFFFFFFFF);
        var pic = new PictureBox { SizeMode = PictureBoxSizeMode.StretchImage };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(30, 30, 34) };
        scroll.Controls.Add(pic);
        var info = new Label { AutoSize = true, ForeColor = Opt.Muted, Margin = new Padding(8, 6, 0, 0) };
        void Draw()
        {
            double k = (double)width / d.W;
            pic.Image?.Dispose();
            var bmp = new Bitmap(width, Math.Max(1, (int)(d.H * k)));
            using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(full, 0, 0, bmp.Width, bmp.Height); }
            pic.Image = bmp; pic.Size = bmp.Size;
            info.Text = $"{width}px 폭 기준, 화면 약 {d.H * k / (width * 2.1):0.0}장 분량 (위아래로 스크롤해 보세요)";
        }
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Opt.Bg };
        foreach (var (w, label) in new[] { (360, "작은 폰 360"), (390, "보통 폰 390"), (430, "큰 폰 430"), (600, "태블릿 600") })
        { int ww = w; top.Controls.Add(Opt.Button(label, () => { width = ww; App.Set("webtoonPreviewW", ww); Draw(); })); }
        top.Controls.Add(info);
        f.Controls.Add(scroll); f.Controls.Add(top);
        Draw();
        f.ShowDialog(App.Main);
        pic.Image?.Dispose();
    }

    /// <summary>화면을 그 영역으로</summary>
    public static void GoTo(double x, double y, double w, double h)
    {
        var c = MetaLayers.Canvas; if (c == null) return;
        var v = c.V;
        double z = Math.Min(Math.Min(c.Width / (w * 1.4 + 40), c.Height / (h * 1.4 + 40)), 4);
        v.Zoom = Math.Max(0.02, z);
        var (sx, sy) = v.ToScreen(x + w / 2, y + h / 2);
        v.PanX += c.Width / 2.0 - sx; v.PanY += c.Height / 2.0 - sy;
        c.RedrawAll();
    }
}

// =====================================================================
// 인쇄·투고 전 점검
// =====================================================================
public static class Preflight
{
    public sealed record Item(string Sev, string Msg, string LayerId = null, (double X, double Y, double W, double H)? Rect = null, string FixLabel = null, Action Fix = null);
    static bool Inside((double X, double Y, double W, double H) b, (double X, double Y, double W, double H) r) => b.X >= r.X - 0.5 && b.Y >= r.Y - 0.5 && b.X + b.W <= r.X + r.W + 0.5 && b.Y + b.H <= r.Y + r.H + 0.5;

    sealed class Stats { public double Grey, Color, Ink; public (double, double, double, double)? GreyBox, ColorBox; public Surface Flat; }

    /// <summary>합친 그림을 듬성듬성 훑어 픽셀 통계 (흰색·검정·회색·컬러)</summary>
    static Stats PixelStats(Document d)
    {
        var flat = App.Comp.Flatten(d, new RenderOptions { ExcludeDraft = true });
        int step = Math.Max(1, (int)Math.Ceiling(Math.Max(d.W, d.H) / 1600.0));
        long grey = 0, color = 0, ink = 0, n = 0;
        int gx0 = int.MaxValue, gy0 = int.MaxValue, gx1 = -1, gy1 = -1, cx0 = int.MaxValue, cy0 = int.MaxValue, cx1 = -1, cy1 = -1;
        for (int y = 0; y < d.H; y += step)
            for (int x = 0; x < d.W; x += step)
            {
                uint c = Px.Over(flat.Get(x, y), 0xFFFFFFFF);
                int r = (int)((c >> 16) & 255), g = (int)((c >> 8) & 255), b = (int)(c & 255);
                n++;
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx < 250) ink++;
                if (mx - mn > 28) { color++; cx0 = Math.Min(cx0, x); cy0 = Math.Min(cy0, y); cx1 = Math.Max(cx1, x); cy1 = Math.Max(cy1, y); }
                else if (mx > 40 && mx < 215) { grey++; gx0 = Math.Min(gx0, x); gy0 = Math.Min(gy0, y); gx1 = Math.Max(gx1, x); gy1 = Math.Max(gy1, y); }
            }
        return new Stats
        {
            Grey = (double)grey / n, Color = (double)color / n, Ink = (double)ink / n, Flat = flat,
            GreyBox = gx1 < 0 ? null : (gx0, gy0, gx1 - gx0 + step, gy1 - gy0 + step), ColorBox = cx1 < 0 ? null : (cx0, cy0, cx1 - cx0 + step, cy1 - cy0 + step),
        };
    }

    /// <summary>재단선에 닿은 그림이 도련까지 이어지는지 (네 변을 훑어봄)</summary>
    static int BleedGaps(Stats st, Manuscript.Rects r)
    {
        var T = r.trim; var B = r.bleed;
        bool Ink(double px, double py) { int x = (int)px, y = (int)py; if (x < 0 || y < 0 || x >= st.Flat.W || y >= st.Flat.H) return false; uint c = Px.Over(st.Flat.Get(x, y), 0xFFFFFFFF); return Math.Max((c >> 16) & 255, Math.Max((c >> 8) & 255, c & 255)) < 240; }
        int gaps = 0, total = 0;
        for (int k = 0; k <= 40; k++)
        {
            double u = k / 40.0;
            var pairs = new[]
            {
                ((T.X + 1, T.Y + u * T.H), (B.X + 1, T.Y + u * T.H)), ((T.X + T.W - 2, T.Y + u * T.H), (B.X + B.W - 2, T.Y + u * T.H)),
                ((T.X + u * T.W, T.Y + 1), (T.X + u * T.W, B.Y + 1)), ((T.X + u * T.W, T.Y + T.H - 2), (T.X + u * T.W, B.Y + B.H - 2)),
            };
            foreach (var (a, b) in pairs) if (Ink(a.Item1, a.Item2)) { total++; if (!Ink(b.Item1, b.Item2)) gaps++; }
        }
        return total >= 4 && (double)gaps / total > 0.3 ? gaps : 0;
    }

    static readonly System.Text.RegularExpressions.Regex RoughName = new("러프|rough|밑그림|스케치|sketch|콘티", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static string Cut(string s, int n) => s == null ? "" : s.Length > n ? s[..n] : s;

    /// <summary>작품 하나 점검</summary>
    public static List<Item> Check(Document d)
    {
        var o = new List<Item>();
        var r = Manuscript.PrintRects(d);
        int dpi = d.Dpi;
        var st = PixelStats(d);
        bool mono = st.Color < 0.002;
        // 1) 해상도
        if (r != null)
        {
            if (dpi <= 0) o.Add(new("warn", "해상도(dpi)가 정해져 있지 않습니다. 원고 설정에서 정하세요."));
            else if (mono && dpi < 600) o.Add(new("warn", $"흑백 원고인데 해상도가 {dpi}dpi 입니다. 흑백(1비트) 인쇄는 600dpi 이상을 권합니다."));
            else if (!mono && dpi < 300) o.Add(new("error", $"컬러 원고 해상도가 {dpi}dpi 입니다. 인쇄는 300dpi 이상이 필요합니다."));
            if (d.W < r.Value.bleed.W - 1 || d.H < r.Value.bleed.H - 1) o.Add(new("error", "캔버스가 도련(재단선 바깥 여유)보다 작습니다. 원고 설정에서 \"캔버스 크기를 도련에 맞추기\"를 하세요."));
            if (BleedGaps(st, r.Value) > 0) o.Add(new("warn", "재단선에 닿은 그림이 도련(빨간 점선)까지 이어지지 않은 곳이 있습니다. 재단 때 흰 틈이 생길 수 있습니다.", Rect: r.Value.bleed));
        }
        else o.Add(new("info", "원고 설정(재단선·도련·안전선)이 없습니다. 인쇄할 원고면 만화 > 원고 설정을 하세요."));
        // 2) 글자·말풍선이 안전선 밖
        var safe = r?.safe;
        foreach (var l in d.Layers)
        {
            if (!l.Visible) continue;
            if (l.Kind == "text" && l.Meta?["box"] is JsonObject bx)
            {
                var box = (J.Num(bx, "x"), J.Num(bx, "y"), J.Num(bx, "w"), J.Num(bx, "h"));
                string txt = Cut(J.Str(l.Meta, "text", ""), 14);
                if (safe != null && !Inside(box, safe.Value)) o.Add(new("warn", $"글자 \"{txt}\" 가 안전선(파란 점선) 밖에 있어 잘릴 수 있습니다.", l.Id, box));
                if (dpi > 0 && !J.Bool(l.Meta, "sfx"))
                {
                    var mat = J.Doubles(l.Meta["mat"]);
                    double pt = J.Num(l.Meta, "size", 42) / dpi * 72 * (mat != null && mat.Length >= 2 ? Math.Sqrt(mat[0] * mat[0] + mat[1] * mat[1]) : 1);
                    if (pt < 7) o.Add(new("warn", $"글자 \"{txt}\" 가 {pt:0.0}pt 로 작습니다 (인쇄에서는 7pt 이상 권장).", l.Id, box));
                }
                string f = J.Str(l.Meta, "font");
                if (f != null && !Fonts.Sys.Any(x => x.id == f) && !Fonts.Has(f)) o.Add(new("warn", $"글꼴 \"{f}\" 이(가) 이 PC 에 없습니다 (다른 글꼴로 보임).", l.Id, box));
            }
            if (l.Kind == "bubble" && safe != null)
                foreach (var b in Bubbles.List(l.Meta))
                {
                    var bb = (J.Num(b, "x"), J.Num(b, "y"), J.Num(b, "w"), J.Num(b, "h"));
                    if (!Inside(bb, safe.Value)) o.Add(new("warn", "말풍선이 안전선 밖으로 나가 있습니다.", l.Id, bb));
                }
            // 3) 보이는 러프·밑그림 이름의 레이어가 내보내기에 들어감
            if (!l.Draft && !l.IsFolder && (l.Rough || RoughName.IsMatch(l.Name ?? "")))
            {
                var ll = l;
                o.Add(new("warn", $"\"{l.Name}\" 레이어가 보이는 상태라 인쇄·내보내기에 들어갑니다.", l.Id, FixLabel: "밑그림으로 (내보내기 제외)", Fix: () => { App.History.Struct(App.Doc, "밑그림으로", () => ll.Draft = true); App.NotifyLayers(); }));
            }
        }
        // 4) 흑백 원고의 회색·컬러
        if (r != null && mono && st.Grey > 0.003)
            o.Add(new("warn", $"흑백 원고에 망점이 아닌 회색이 {st.Grey * 100:0.0}% 있습니다. 흑백(1비트) 인쇄에서 뭉개지거나 사라질 수 있습니다 (톤·망점으로 바꾸거나 2값화).", Rect: st.GreyBox, FixLabel: "맨 위에 2값화 조정 레이어", Fix: () =>
                MetaLayers.Add("2값화 (인쇄 점검)", "adjust", new JsonObject { ["type"] = "threshold", ["params"] = new JsonObject { ["t"] = 128 } }, -1, "2값화 조정 레이어")));
        if (r != null && !mono && st.Color < 0.02 && st.Grey + st.Color > 0) o.Add(new("info", $"컬러가 조금({st.Color * 100:0.00}%)만 있습니다. 흑백 인쇄라면 섞인 색이 회색으로 바뀝니다.", Rect: st.ColorBox));
        // 5) 비상업 AI
        if (d.Extra["aiLog"] is JsonArray ai)
        {
            var nc = ai.OfType<JsonObject>().Where(x => J.Bool(x, "nc")).Select(x => J.Str(x, "name", "?")).Distinct().ToList();
            if (nc.Count > 0) o.Add(new("error", $"비상업용 AI 로 만든 부분이 있습니다 ({string.Join(", ", nc)}). 판매·연재·투고에는 쓸 수 없습니다."));
        }
        // 6) 빈 페이지
        if (st.Ink < 0.001) o.Add(new("warn", "빈 페이지로 보입니다 (그린 것이 거의 없음)."));
        var order = new Dictionary<string, int> { ["error"] = 0, ["warn"] = 1, ["info"] = 2 };
        return o.OrderBy(x => order[x.Sev]).ToList();
    }

    public static void Dialog()
    {
        var d = App.Doc; if (d == null) return;
        Cursor.Current = Cursors.WaitCursor;
        var list = Check(d);
        Cursor.Current = Cursors.Default;
        using var f = new Form { Text = "인쇄·투고 전 점검", StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, Width = 760, Height = 520, Font = new Font("Malgun Gothic", 9f) };
        UI.Dialogs.Modals.Adopt(f);
        var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(8) };
        int ce = list.Count(x => x.Sev == "error"), cw = list.Count(x => x.Sev == "warn"), ci = list.Count(x => x.Sev == "info");
        p.Controls.Add(Opt.Help(list.Count > 0 ? $"고쳐야 함 {ce} · 확인 {cw} · 참고 {ci}" : "문제를 찾지 못했습니다. 인쇄·투고해도 좋습니다."));
        foreach (var it in list)
        {
            string ic = it.Sev == "error" ? "❌" : it.Sev == "warn" ? "⚠" : "ℹ";
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Opt.Bg2, Margin = new Padding(0, 2, 0, 2), Padding = new Padding(4) };
            row.Controls.Add(new Label { Text = ic + " " + it.Msg, AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = it.Sev == "error" ? Color.FromArgb(255, 120, 120) : Opt.Fg, Margin = new Padding(0, 4, 6, 0) });
            if (it.LayerId != null || it.Rect != null)
            {
                var item = it;
                row.Controls.Add(Opt.Button("가서 보기", () =>
                {
                    if (item.LayerId != null && d.ById(item.LayerId) != null) { d.ActiveId = item.LayerId; App.NotifyLayers(); }
                    if (item.Rect is { } rr) Manuscript.GoTo(rr.X, rr.Y, rr.W, rr.H);
                }));
            }
            if (it.Fix != null)
            {
                Button b = null; var item = it;
                b = Opt.Button(it.FixLabel, () => { item.Fix(); b.Enabled = false; b.Text = "고쳤음"; App.InvalidateAll(); }, true);
                row.Controls.Add(b);
            }
            p.Controls.Add(row);
        }
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, BackColor = Opt.Bg, Padding = new Padding(6) };
        bool again = false;
        bottom.Controls.Add(Opt.Button("닫기", () => f.Close(), true));
        bottom.Controls.Add(Opt.Button("다시 점검", () => { again = true; f.Close(); }));
        f.Controls.Add(p); f.Controls.Add(bottom);
        f.ShowDialog(App.Main);
        if (again) Dialog();
    }
}

// =====================================================================
// 메모 핀 (작업 메모·피드백을 그림 위에 꽂아 두기. 내보내기에는 안 나옴)
// =====================================================================
public sealed class MemoTool : ToolBase
{
    public override string Id => "memo";
    public override string Name => "메모";
    public override string Icon => "📌";
    public override Keys Key => Keys.Y;
    const float PIN = 13;
    sealed class DragSt { public JsonObject N; public double X0, Y0, SX0, SY0, OX, OY; public bool Moved, Create; public JsonNode Before; }
    DragSt drag;
    CUI.Host host;
    public override bool Busy => drag != null;

    static JsonArray Notes(Document d) => d?.Extra["notes"] as JsonArray;

    static JsonObject NoteAt(double sx, double sy)
    {
        var ns = Notes(App.Doc)?.OfType<JsonObject>().ToList(); var v = MetaLayers.Canvas?.V;
        if (ns == null || v == null) return null;
        for (int i = ns.Count - 1; i >= 0; i--)
        {
            var p = Gfx.S(v, J.Num(ns[i], "x"), J.Num(ns[i], "y"));
            if (Math.Sqrt((p.X - sx) * (p.X - sx) + (p.Y - sy) * (p.Y - sy)) <= PIN + 4) return ns[i];
        }
        return null;
    }

    void SetNotes(JsonArray next, string label)
    {
        ExtraEdit.Set("notes", label, next, () => { MetaLayers.Redraw(); host?.Rebuild(); });
    }

    void EditNote(JsonObject n, bool isNew)
    {
        var v = CUI.Form(isNew ? "새 메모" : "메모", new[]
        {
            CUI.Txt("text", "내용", J.Str(n, "text", ""), true),
            CUI.Sel("color", "색", new[] { ("#f5c518", "노랑"), ("#e5484d", "빨강 (수정 요청)"), ("#2fa35b", "초록 (확인)"), ("#2f7cf6", "파랑 (참고)") }, J.Str(n, "color", "#f5c518")),
            CUI.Chk("done", "해결됨", J.Bool(n, "done")),
        }, "저장", isNew ? null : new[] { ("삭제", "delete") });
        var notes = (JsonArray)(Notes(App.Doc)?.DeepClone() ?? new JsonArray());
        string id = J.Str(n, "id");
        if (CUI.S(v, "$extra") == "delete") { SetNotes(new JsonArray(notes.OfType<JsonObject>().Where(x => J.Str(x, "id") != id).Select(x => (JsonNode)x.DeepClone()).ToArray()), "메모 삭제"); return; }
        if (v == null) return;
        var item = J.Clone(n); item["text"] = CUI.S(v, "text"); item["color"] = CUI.S(v, "color"); item["done"] = CUI.B(v, "done");
        int i = notes.OfType<JsonObject>().ToList().FindIndex(x => J.Str(x, "id") == id);
        if (i >= 0) notes[i] = item; else notes.Add(item);
        SetNotes(notes, isNew ? "메모 추가" : "메모 수정");
    }

    public override void Down(PointerArgs e)
    {
        var n = NoteAt(e.SX, e.SY);
        drag = n != null ? new DragSt { N = n, X0 = e.X, Y0 = e.Y, SX0 = e.SX, SY0 = e.SY, OX = J.Num(n, "x"), OY = J.Num(n, "y"), Before = Notes(App.Doc).DeepClone() } : new DragSt { Create = true, X0 = e.X, Y0 = e.Y };
    }
    public override void Move(PointerArgs e)
    {
        var d = drag; if (d?.N == null) return;
        if (Math.Sqrt((e.SX - d.SX0) * (e.SX - d.SX0) + (e.SY - d.SY0) * (e.SY - d.SY0)) > 5) d.Moved = true;
        if (d.Moved) { d.N["x"] = J.R(d.OX + e.X - d.X0); d.N["y"] = J.R(d.OY + e.Y - d.Y0); MetaLayers.Redraw(); }
    }
    public override void Up(PointerArgs e)
    {
        var d = drag; drag = null;
        if (d == null) return;
        if (d.Create) { EditNote(new JsonObject { ["id"] = "n" + DateTime.Now.Ticks.ToString("x")[^8..], ["x"] = J.R(d.X0), ["y"] = J.R(d.Y0), ["text"] = "", ["color"] = "#f5c518", ["author"] = App.Get("userName", "") }, true); return; }
        if (!d.Moved) { EditNote(d.N, false); return; }
        var after = Notes(App.Doc).DeepClone();
        App.Doc.Extra["notes"] = d.Before;
        SetNotes((JsonArray)after, "메모 이동");
    }
    public override void Cancel()
    {
        if (drag?.N != null) { drag.N["x"] = drag.OX; drag.N["y"] = drag.OY; MetaLayers.Redraw(); }
        drag = null;
    }
    public override void Activate() => MetaLayers.Redraw();
    public override void Deactivate() => MetaLayers.Redraw();

    /// <summary>메모 핀 (늘 그림)</summary>
    public static void Draw(Graphics g, ViewXf v)
    {
        var ns = Notes(App.Doc)?.OfType<JsonObject>().ToList();
        if (ns == null || ns.Count == 0 || !App.Get("showNotes", true)) return;
        using var font = new Font("Malgun Gothic", 9f);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < ns.Count; i++)
        {
            var n = ns[i]; var p = Gfx.S(v, J.Num(n, "x"), J.Num(n, "y"));
            int alpha = J.Bool(n, "done") ? 115 : 255;
            var c = Gfx.Col(J.Str(n, "color", "#f5c518"));
            using var br = new SolidBrush(Color.FromArgb(alpha, c));
            using var pen = new Pen(Color.FromArgb(alpha * 153 / 255, 0, 0, 0), 1.5f);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            float cy = p.Y - PIN - 4;
            path.AddLine(p.X, p.Y, p.X + PIN * (float)Math.Cos(Math.PI * 0.62), cy + PIN * (float)Math.Sin(Math.PI * 0.62));
            path.AddArc(p.X - PIN, cy - PIN, PIN * 2, PIN * 2, (float)(0.62 * 180), (float)(1.76 * 180));
            path.CloseFigure();
            g.FillPath(br, path); g.DrawPath(pen, path);
            using var tb = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            g.DrawString(J.Bool(n, "done") ? "✓" : (i + 1).ToString(), font, tb, new PointF(p.X, cy), sf);
            string t = (J.Str(n, "text", "") ?? "").Split('\n')[0];
            if (t.Length > 18) t = t[..18];
            if (t.Length > 0 && App.Tool?.Id == "memo")
            {
                var sz = g.MeasureString(t, font);
                using var bg = new SolidBrush(Color.FromArgb(242, 255, 255, 240));
                g.FillRectangle(bg, p.X + PIN + 2, p.Y - PIN - 14, sz.Width + 10, 20);
                g.DrawString(t, font, Brushes.Black, p.X + PIN + 7, p.Y - PIN - 12);
            }
        }
    }

    public override Control BuildOptions() { host = new CUI.Host(Items); return host.Panel; }
    IEnumerable<Control> Items()
    {
        yield return Opt.Title("메모");
        yield return Opt.Check("메모 핀 보이기", () => App.Get("showNotes", true), v => { App.Set("showNotes", v); MetaLayers.Redraw(); });
        yield return Opt.Help("빈 곳 탭: 새 메모 · 핀 탭: 보기/수정/삭제 · 핀 드래그: 이동. 메모는 작품 파일에 저장되고 내보낸 그림에는 나오지 않습니다.");
        var ns = Notes(App.Doc)?.OfType<JsonObject>().ToList() ?? new();
        for (int i = 0; i < ns.Count; i++)
        {
            var n = ns[i];
            string t = (J.Str(n, "text", "") ?? "").Split('\n')[0];
            yield return Opt.Button($"{(J.Bool(n, "done") ? "✓" : (i + 1).ToString())}  {(t.Length > 0 ? t : "(빈 메모)")}", () =>
            {
                double x = J.Num(n, "x"), y = J.Num(n, "y");
                var c = MetaLayers.Canvas;
                if (c != null) { var (sx, sy) = c.V.ToScreen(x, y); c.V.PanX += c.Width / 2.0 - sx; c.V.PanY += c.Height / 2.0 - sy; c.RedrawAll(); }
                EditNote(n, false);
            });
        }
        if (ns.Count > 0) yield return Opt.Button("해결된 메모 지우기", () => SetNotes(new JsonArray(ns.Where(n => !J.Bool(n, "done")).Select(n => (JsonNode)n.DeepClone()).ToArray()), "해결된 메모 정리"));
    }
}
