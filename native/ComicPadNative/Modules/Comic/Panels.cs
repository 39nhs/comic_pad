// 컷(칸) 나누기 (웹 tools/comic.js 컷 부분 + ui/paneltemplates.js).
// 컷 레이어: kind 'panel', meta { panels:[{pts:[{x,y}…]}], line, color, maskOutside } — 웹 판과 같은 형식.
// 픽셀은 meta 로 다시 그림: 컷 바깥은 용지색(덮인 정도로 매끈하게), 테두리는 컷 안쪽으로만 (2배 굵기로 긋고 컷 모양으로 자름).
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Comic;

public static class Panels
{
    /// <summary>meta.panels → 다각형 목록 (예전 형식 {x,y,w,h} 도)</summary>
    public static List<List<P2>> List(JsonObject meta)
    {
        var o = new List<List<P2>>();
        if (meta?["panels"] is not JsonArray a) return o;
        foreach (var n in a.OfType<JsonObject>())
        {
            if (n["pts"] is JsonArray pts) o.Add(pts.Select(J.ToP).ToList());
            else { double x = J.Num(n, "x"), y = J.Num(n, "y"), w = J.Num(n, "w"), h = J.Num(n, "h"); o.Add(RectPts(x, y, w, h)); }
        }
        return o;
    }
    public static List<P2> RectPts(double x, double y, double w, double h) => new() { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) };
    static double R1(double v) => Math.Round(v * 10) / 10;
    public static JsonObject PanelJson(IEnumerable<P2> q) => new() { ["pts"] = new JsonArray(q.Select(p => (JsonNode)new JsonObject { ["x"] = R1(p.X), ["y"] = R1(p.Y) }).ToArray()) };
    public static JsonArray PanelsJson(IEnumerable<List<P2>> list) => new(list.Select(q => (JsonNode)PanelJson(q)).ToArray());

    /// <summary>컷 레이어 (없으면 create 일 때 만듦: 맨 위)</summary>
    public static Layer PanelLayer(bool create = true)
    {
        var d = App.Doc; if (d == null) return null;
        var l = d.Layers.Find(x => x.Kind == "panel");
        if (l == null && create)
            l = MetaLayers.Add("컷", "panel", NewMeta(d), -1, "컷");
        return l;
    }
    public static JsonObject NewMeta(Document d) => new() { ["panels"] = new JsonArray(), ["line"] = Math.Max(3, (int)Math.Round(d.W / 400.0)), ["color"] = "#000000", ["maskOutside"] = true };

    /// <summary>컷 정보 바꾸기 (실행취소 가능). 컷 레이어가 없으면 함께 만든다 (실행취소 한 번)</summary>
    public static void Set(Func<JsonObject, JsonObject> change, string label = "컷 편집")
    {
        var d = App.Doc; if (d == null) return;
        var l = d.Layers.Find(x => x.Kind == "panel");
        if (l == null)
        {
            MetaLayers.Add("컷", "panel", change(NewMeta(d)), -1, label);
            return;
        }
        if (l.Meta == null) l.Meta = NewMeta(d);
        MetaLayers.Commit(l, label, change(J.Clone(l.Meta)), MetaLayers.Render);
    }

    // ---- 그리기 ----
    public static (Surface s, Rect r) Render(Document d, Layer l)
    {
        var m = l.Meta;
        var panels = List(m).Where(q => q.Count >= 3).ToList();
        double line = J.Num(m, "line", 6);
        bool mask = J.Bool(m, "maskOutside") && panels.Count > 0;
        var surf = new Surface(d.W, d.H);
        if (panels.Count == 0) return (surf, Rect.Empty);
        var r = mask ? new Rect(0, 0, d.W, d.H) : Geo.BoundsRect(panels.SelectMany(q => q), line * 2 + 4).Clip(d.W, d.H);
        if (r.IsEmpty) return (surf, Rect.Empty);
        uint paper = Px.FromHex(d.PaperColor, 0xFFFFFFFF);
        uint col = Px.FromHex(J.Str(m, "color", "#000000"));
        var boxes = panels.Select(q => Geo.BoundsRect(q, line + 2)).ToList();
        // 큰 원고도 메모리를 적게: 가로 띠(512줄)씩
        const int BAND = 512;
        for (int by = r.Y; by < r.B; by += BAND)
        {
            var band = Rect.FromLTRB(r.X, by, r.R, Math.Min(r.B, by + BAND));
            var outb = new uint[band.W * band.H];
            if (mask)
            {
                // 컷 바깥 = 용지색 (컷 안쪽 덮인 정도만큼 비움 → 가장자리도 매끈)
                var cov = Gfx.Coverage(band, (g, br) => { foreach (var q in panels) { using var p = Geo.Poly(q); g.FillPath(br, p); } });
                for (int i = 0; i < outb.Length; i++) { uint k = 255u - cov[i]; if (k > 0) outb[i] = Px.Scale(paper, k); }
            }
            // 테두리: 굵기 2배 선 ∩ 컷 안쪽
            if (line > 0)
                for (int k = 0; k < panels.Count; k++)
                {
                    var pr = boxes[k].Intersect(band);
                    if (pr.IsEmpty) continue;
                    using var path = Geo.Poly(panels[k]);
                    var inside = Gfx.Coverage(pr, (g, br) => g.FillPath(br, path));
                    var stroke = Gfx.Coverage(pr, (g, br) =>
                    {
                        using var pen = new Pen(br, (float)(line * 2)) { LineJoin = LineJoin.Miter, MiterLimit = 20 };
                        g.DrawPath(pen, path);
                    });
                    for (int y = 0; y < pr.H; y++)
                        for (int x = 0; x < pr.W; x++)
                        {
                            int i = y * pr.W + x;
                            uint c = (uint)(inside[i] * stroke[i] + 127) / 255;
                            if (c == 0) continue;
                            int oi = (pr.Y - band.Y + y) * band.W + (pr.X - band.X + x);
                            outb[oi] = Px.Over(Px.Scale(col, c), outb[oi]);
                        }
                }
            surf.Write(band, outb);
        }
        return (surf, r);
    }

    // ---- 다각형 계산 ----
    public static int PanelAt(Layer l, double x, double y)
    {
        var ps = List(l?.Meta);
        for (int i = ps.Count - 1; i >= 0; i--) if (Geo.InPoly(ps[i], x, y)) return i;
        return -1;
    }

    /// <summary>다각형을 직선 a→b 로 자르고 양쪽을 간격 g 만큼 띄운다</summary>
    public static (List<P2>, List<P2>) SplitPoly(List<P2> q, P2 a, P2 b, double gap)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, L = Math.Sqrt(dx * dx + dy * dy); if (L < 1e-9) L = 1;
        double nx = -dy / L, ny = dx / L;
        double Side(P2 p) => (p.X - a.X) * nx + (p.Y - a.Y) * ny;
        List<P2> Clip(int k)
        {
            var o = new List<P2>();
            double F(P2 p) => k * Side(p) - gap / 2;
            for (int i = 0; i < q.Count; i++)
            {
                var P = q[i]; var Q = q[(i + 1) % q.Count]; double fp = F(P), fq = F(Q);
                if (fp >= 0) o.Add(P);
                if ((fp >= 0) != (fq >= 0)) { double t = fp / (fp - fq); o.Add(new P2(P.X + (Q.X - P.X) * t, P.Y + (Q.Y - P.Y) * t)); }
            }
            return o;
        }
        return (Clip(1), Clip(-1));
    }

    public static bool SegCross(P2 a, P2 b, P2 c, P2 d)
    {
        int O(P2 p, P2 q, P2 r) => Math.Sign((q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X));
        return O(a, b, c) != O(a, b, d) && O(c, d, a) != O(c, d, b);
    }

    /// <summary>선택 영역 마스크를 r 만큼 줄이기 (사각 침식, 웹 sel.grow(-r))</summary>
    public static void Erode(byte[] m, int W, int H, Rect region, int r)
    {
        if (r <= 0) return;
        region = region.Inflate(r + 1).Clip(W, H);
        var tmp = new byte[m.Length];
        Array.Copy(m, tmp, m.Length);
        // 가로
        for (int y = region.Y; y < region.B; y++)
            for (int x = region.X; x < region.R; x++)
            {
                byte mn = 255;
                for (int k = -r; k <= r && mn > 0; k++) { int xx = x + k; byte v = (uint)xx < (uint)W ? m[y * W + xx] : (byte)0; if (v < mn) mn = v; }
                tmp[y * W + x] = mn;
            }
        // 세로
        for (int y = region.Y; y < region.B; y++)
            for (int x = region.X; x < region.R; x++)
            {
                byte mn = 255;
                for (int k = -r; k <= r && mn > 0; k++) { int yy = y + k; byte v = (uint)yy < (uint)H ? tmp[yy * W + x] : (byte)0; if (v < mn) mn = v; }
                m[y * W + x] = mn;
            }
    }

    /// <summary>다각형 모양 선택 (안쪽으로 shrink 만큼 줄임)</summary>
    public static byte[] PolySelection(Document d, List<P2> q, int shrink)
    {
        var r = Geo.BoundsRect(q, 2).Clip(d.W, d.H);
        var m = new byte[d.W * d.H];
        if (r.IsEmpty) return m;
        var cov = Gfx.Coverage(r, (g, br) => { using var p = Geo.Poly(q); g.FillPath(br, p); });
        for (int y = 0; y < r.H; y++) Array.Copy(cov, y * r.W, m, (r.Y + y) * d.W + r.X, r.W);
        Erode(m, d.W, d.H, r, shrink);
        return m;
    }

    // ---- 템플릿 (ui/paneltemplates.js) ----
    public sealed record Template(string Name, List<List<(double x, double y)>> Polys, bool Webtoon = false, bool Raw = false);
    static List<(double, double)> R(double x, double y, double w, double h) => new() { (x, y), (x + w, y), (x + w, y + h), (x, y + h) };
    const double T3 = 1.0 / 3;
    public static readonly Template[] Builtin =
    {
        new("1컷 (한 장 전체)", new() { R(0, 0, 1, 1) }),
        new("2단", new() { R(0, 0, 1, 0.5), R(0, 0.5, 1, 0.5) }),
        new("3단", new() { R(0, 0, 1, T3), R(0, T3, 1, T3), R(0, 2 * T3, 1, T3) }),
        new("4컷 (세로 4단)", new() { R(0, 0, 1, 0.25), R(0, 0.25, 1, 0.25), R(0, 0.5, 1, 0.25), R(0, 0.75, 1, 0.25) }),
        new("2×2", new() { R(0, 0, 0.5, 0.5), R(0.5, 0, 0.5, 0.5), R(0, 0.5, 0.5, 0.5), R(0.5, 0.5, 0.5, 0.5) }),
        new("3단 + 가운데 2칸", new() { R(0, 0, 1, T3), R(0, T3, 0.5, T3), R(0.5, T3, 0.5, T3), R(0, 2 * T3, 1, T3) }),
        new("계단 (2-1-2)", new() { R(0, 0, 0.6, T3), R(0.6, 0, 0.4, T3), R(0, T3, 1, T3), R(0, 2 * T3, 0.4, T3), R(0.4, 2 * T3, 0.6, T3) }),
        new("큰 컷 + 아래 2칸", new() { R(0, 0, 1, 0.62), R(0, 0.62, 0.5, 0.38), R(0.5, 0.62, 0.5, 0.38) }),
        new("위 2칸 + 큰 컷", new() { R(0, 0, 0.5, 0.38), R(0.5, 0, 0.5, 0.38), R(0, 0.38, 1, 0.62) }),
        new("세로 2칸 + 오른쪽 긴 컷", new() { R(0, 0, 0.55, 0.5), R(0, 0.5, 0.55, 0.5), R(0.55, 0, 0.45, 1) }),
        new("대각선 2컷", new() { new() { (0, 0), (1, 0), (1, 0.4), (0, 0.6) }, new() { (0, 0.6), (1, 0.4), (1, 1), (0, 1) } }),
        new("대각선 3단", new() { new() { (0, 0), (1, 0), (1, 0.28), (0, 0.38) }, new() { (0, 0.38), (1, 0.28), (1, 0.68), (0, 0.6) }, new() { (0, 0.6), (1, 0.68), (1, 1), (0, 1) } }),
        new("가운데 사선 2칸", new() { R(0, 0, 1, T3), new() { (0, T3), (0.62, T3), (0.38, 2 * T3), (0, 2 * T3) }, new() { (0.62, T3), (1, T3), (1, 2 * T3), (0.38, 2 * T3) }, R(0, 2 * T3, 1, T3) }),
        new("웹툰 세로 3컷", new() { R(0, 0, 1, 0.28), R(0, 0.36, 1, 0.28), R(0, 0.72, 1, 0.28) }, true),
        new("웹툰 큰 컷 + 작은 컷", new() { R(0, 0, 1, 0.55), R(0.15, 0.68, 0.7, 0.32) }, true),
    };

    static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    /// <summary>다각형 안쪽으로 변마다 다른 거리만큼 줄이기 (볼록 다각형)</summary>
    static List<(double x, double y)> InsetPoly(List<(double x, double y)> pts, Func<(double x, double y), (double x, double y), double, double, double> distOf)
    {
        int n = pts.Count;
        double area = 0;
        for (int i = 0; i < n; i++) { var (x1, y1) = pts[i]; var (x2, y2) = pts[(i + 1) % n]; area += x1 * y2 - x2 * y1; }
        int sgn = area > 0 ? 1 : -1;
        var lines = new List<((double x, double y) p, (double x, double y) v)>();
        for (int i = 0; i < n; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % n];
            double dx = b.x - a.x, dy = b.y - a.y, L = Math.Sqrt(dx * dx + dy * dy); if (L < 1e-12) L = 1;
            double nx = -dy / L * sgn, ny = dx / L * sgn;
            double dd = distOf(a, b, nx, ny);
            lines.Add(((a.x + nx * dd, a.y + ny * dd), (dx, dy)));
        }
        var o = new List<(double, double)>();
        for (int i = 0; i < n; i++)
        {
            var A = lines[(i - 1 + n) % n]; var B = lines[i];
            double den = A.v.x * B.v.y - A.v.y * B.v.x;
            if (Math.Abs(den) < 1e-9) { o.Add(B.p); continue; }
            double t = ((B.p.x - A.p.x) * B.v.y - (B.p.y - A.p.y) * B.v.x) / den;
            o.Add((A.p.x + A.v.x * t, A.p.y + A.v.y * t));
        }
        return o;
    }

    /// <summary>템플릿 → 문서 좌표 컷 목록 (area = x,y,w,h)</summary>
    public static List<List<P2>> Layout(Template t, (double X, double Y, double W, double H) area, double gx, double gy)
    {
        return t.Polys.Select(poly =>
        {
            var P = t.Raw ? poly : InsetPoly(poly.Select(p => (p.x * area.W, p.y * area.H)).ToList(), (a, b, nx, ny) =>
            {
                double ax = a.x / area.W, ay = a.y / area.H, bx = b.x / area.W, by = b.y / area.H;
                bool onEdge = (Near(ax, 0) && Near(bx, 0)) || (Near(ax, 1) && Near(bx, 1)) || (Near(ay, 0) && Near(by, 0)) || (Near(ay, 1) && Near(by, 1));
                return onEdge ? 0 : (Math.Abs(nx) * gx + Math.Abs(ny) * gy) / 2;
            }).Select(p => (p.x / area.W, p.y / area.H)).ToList();
            return P.Select(p => new P2(R1(area.X + p.Item1 * area.W), R1(area.Y + p.Item2 * area.H))).ToList();
        }).ToList();
    }

    /// <summary>컷이 들어갈 영역: 원고 설정이 있으면 안전선 안(또는 재단선 안), 없으면 가장자리 여백</summary>
    public static (double X, double Y, double W, double H) FrameArea(Document d, string mode = "safe", double? margin = null)
    {
        var r = Manuscript.PrintRects(d);
        if (r != null && mode == "safe") return r.Value.safe;
        if (r != null && mode == "trim") return r.Value.trim;
        double m = margin ?? Math.Round(d.W * 0.07);
        return (m, m, d.W - m * 2, d.H - m * 2);
    }

    public static void ApplyTemplate(Template t, string mode, double gx, double gy)
    {
        var d = App.Doc;
        var panels = Layout(t, FrameArea(d, mode), gx, gy);
        PanelTool.GutterX = gx; PanelTool.GutterY = gy;
        Set(m => { m["panels"] = PanelsJson(panels); return m; }, "컷 템플릿");
    }

    static JsonArray Saved()
    {
        if (App.Settings["panelTemplates"] is not JsonArray a) { a = new JsonArray(); App.Settings["panelTemplates"] = a; }
        return a;
    }
    static Template FromJson(JsonObject o) => new(J.Str(o, "name", "내 템플릿"), (o["panels"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().Select(poly => poly.OfType<JsonArray>().Select(p => (J.Num(p[0]), J.Num(p[1]))).ToList()).ToList(), false, J.Bool(o, "raw"));

    public static Template FromCurrent(string name, string mode)
    {
        var d = App.Doc; var l = PanelLayer(false);
        var ps = List(l?.Meta);
        if (ps.Count == 0) return null;
        var a = FrameArea(d, mode);
        return new Template(name, ps.Select(q => q.Select(p => ((p.X - a.X) / a.W, (p.Y - a.Y) / a.H)).ToList()).ToList(), false, true);
    }

    static Bitmap Thumb(Template t, int w = 54, int h = 76)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.White);
        using var pen = new Pen(Color.FromArgb(17, 17, 17), 1.5f);
        foreach (var q in Layout(t, (4, 4, w - 8, h - 8), 3, 4)) if (q.Count >= 2) g.DrawPolygon(pen, q.Select(p => p.F).ToArray());
        return bmp;
    }

    public static void TemplatesDialog()
    {
        var d = App.Doc; if (d == null) return;
        bool hasPrint = Manuscript.PrintRects(d) != null;
        string mode = hasPrint ? "safe" : "margin";
        double gx = PanelTool.GutterX > 0 ? PanelTool.GutterX : Math.Round(d.W * 0.02), gy = PanelTool.GutterY > 0 ? PanelTool.GutterY : Math.Round(d.W * 0.035);
        using var f = new Form { Text = "컷 나누기 템플릿", StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, Width = 720, Height = 600, Font = new Font("Malgun Gothic", 9f), MinimizeBox = false };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Opt.Bg, Padding = new Padding(6) };
        var grid = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(6) };
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        var modes = new[] { ("safe", "안전선 안"), ("trim", "재단선까지"), ("margin", "가장자리 여백") }.Where(x => hasPrint || x.Item1 == "margin").ToArray();
        foreach (var (_, t) in modes) cb.Items.Add(t);
        cb.SelectedIndex = Math.Max(0, Array.FindIndex(modes, x => x.Item1 == mode));
        cb.SelectedIndexChanged += (_, _) => mode = modes[cb.SelectedIndex].Item1;
        NumericUpDown Num(double v, Action<double> set) { var n = new NumericUpDown { Minimum = 0, Maximum = 2000, Value = (decimal)Math.Clamp(v, 0, 2000), Width = 70 }; n.ValueChanged += (_, _) => set((double)n.Value); return n; }
        top.Controls.Add(cb);
        top.Controls.Add(new Label { Text = "좌우 간격", AutoSize = true, Margin = new Padding(8, 6, 2, 0) }); top.Controls.Add(Num(gx, v => gx = v));
        top.Controls.Add(new Label { Text = "상하 간격", AutoSize = true, Margin = new Padding(8, 6, 2, 0) }); top.Controls.Add(Num(gy, v => gy = v));
        top.Controls.Add(new Label { Text = "누르면 지금 페이지의 컷이 바뀝니다 (실행취소 가능). 원고 설정이 있으면 안전선(또는 재단선)에 맞춰 넣습니다.", AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Opt.Muted });
        void Fill()
        {
            foreach (Control c in grid.Controls.Cast<Control>().ToList()) c.Dispose();
            grid.Controls.Clear();
            bool webtoon = d.H > d.W * 2.2;
            var saved = Saved().OfType<JsonObject>().ToList();
            var list = saved.Select((o, i) => (t: FromJson(o), mine: i)).Concat(Builtin.Where(t => t.Webtoon == webtoon || !webtoon).Select(t => (t, mine: -1))).ToList();
            foreach (var (t, mine) in list)
            {
                var card = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(4), BackColor = Opt.Bg2 };
                var b = new Button { Image = Thumb(t), Width = 64, Height = 86, FlatStyle = FlatStyle.Flat };
                var tt = t;
                b.Click += (_, _) => { ApplyTemplate(tt, mode, gx, gy); App.Toast($"\"{tt.Name}\" 컷을 넣었습니다 (실행취소 가능, 컷 도구로 다듬기)"); App.SetTool("panel"); f.Close(); };
                card.Controls.Add(b);
                card.Controls.Add(new Label { Text = t.Name, AutoSize = true, MaximumSize = new Size(100, 0), ForeColor = Opt.Fg });
                if (mine >= 0)
                {
                    int idx = mine;
                    card.Controls.Add(Opt.Row(
                        Opt.Button("✎", () => { var v = CUI.Form("템플릿 이름", new[] { CUI.Txt("n", "이름", tt.Name) }); if (!string.IsNullOrWhiteSpace(CUI.S(v, "n"))) { ((JsonObject)Saved()[idx])["name"] = CUI.S(v, "n"); App.SaveSettingsSoon(); Fill(); } }),
                        Opt.Button("×", () => { Saved().RemoveAt(idx); App.SaveSettingsSoon(); Fill(); })));
                }
                grid.Controls.Add(card);
            }
        }
        Fill();
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Opt.Bg, Padding = new Padding(6) };
        bottom.Controls.Add(Opt.Button("지금 컷을 템플릿으로 저장…", () =>
        {
            if (List(PanelLayer(false)?.Meta).Count == 0) { App.Toast("지금 페이지에 컷이 없습니다"); return; }
            var v = CUI.Form("지금 컷을 템플릿으로 저장", new[] { CUI.Txt("n", "이름", $"내 템플릿 {Saved().Count + 1}") });
            string n = CUI.S(v, "n"); if (string.IsNullOrWhiteSpace(n)) return;
            var t = FromCurrent(n, mode);
            if (t == null) return;
            Saved().Add(new JsonObject { ["name"] = n, ["raw"] = true, ["panels"] = new JsonArray(t.Polys.Select(poly => (JsonNode)new JsonArray(poly.Select(p => (JsonNode)new JsonArray(J.R(p.x), J.R(p.y))).ToArray())).ToArray()) });
            App.SaveSettingsSoon(); Fill();
            App.Toast($"\"{n}\" 템플릿을 저장했습니다");
        }));
        bottom.Controls.Add(Opt.Button("닫기", () => f.Close(), true));
        f.Controls.Add(grid); f.Controls.Add(top); f.Controls.Add(bottom);
        f.ShowDialog(App.Main);
    }

    /// <summary>페이지 컷 만들기 (웹 pageFrameDialog)</summary>
    public static void PageFrameDialog()
    {
        var d = App.Doc; if (d == null) return;
        var v = CUI.Form("페이지 컷 만들기", new[]
        {
            CUI.Num("margin", "바깥 여백(px)", Math.Round(d.W * 0.07), 0, 10000),
            CUI.Num("rows", "세로 칸 수", 1, 1, 12), CUI.Num("cols", "가로 칸 수", 1, 1, 6),
            CUI.Num("gx", "좌우 간격(px)", Math.Round(d.W * 0.02), 0, 5000), CUI.Num("gy", "상하 간격(px)", Math.Round(d.W * 0.035), 0, 5000),
            CUI.Num("line", "테두리 굵기(px)", Math.Max(3, Math.Round(d.W / 400.0)), 0, 200),
            CUI.Info("기존 컷은 대체됩니다. 이후 \"자르기\" 모드로 선을 그어 더 나눌 수 있습니다 (비스듬히 그으면 사선 컷)."),
        }, "만들기");
        if (v == null) return;
        double m = CUI.D(v, "margin"), W = d.W - m * 2, H = d.H - m * 2, gx = CUI.D(v, "gx"), gy = CUI.D(v, "gy");
        int rows = (int)CUI.D(v, "rows"), cols = (int)CUI.D(v, "cols");
        double cw = (W - gx * (cols - 1)) / cols, ch = (H - gy * (rows - 1)) / rows;
        var panels = new List<List<P2>>();
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) panels.Add(RectPts(m + c * (cw + gx), m + r * (ch + gy), cw, ch));
        PanelTool.GutterX = gx; PanelTool.GutterY = gy;
        Set(meta => { meta["panels"] = PanelsJson(panels); meta["line"] = CUI.D(v, "line"); return meta; }, "페이지 컷 만들기");
        App.SetTool("panel");
    }
}

public sealed class PanelTool : ToolBase
{
    public override string Id => "panel";
    public override string Name => "컷 나누기";
    public override string Icon => "▦";
    public override Keys Key => Keys.K;

    public static double GutterX, GutterY;
    public string Mode = "split";
    public bool Snap = true;
    sealed class DragSt { public double X0, Y0, X1, Y1, SX0, SY0; public bool Moved; public int Panel = -1; public List<P2> Orig; public MetaPreview Prev; }
    DragSt drag;
    List<P2> poly; P2? cursor;
    CUI.Host host;
    MetaPreview linePrev;

    (double gx, double gy) Gutters() { var d = App.Doc; return (GutterX > 0 ? GutterX : Math.Round(d.W * 0.02), GutterY > 0 ? GutterY : Math.Round(d.W * 0.035)); }

    (P2 a, P2 b) SnapLine(DragSt d)
    {
        P2 a = new(d.X0, d.Y0), b = new(d.X1, d.Y1);
        if (!Snap) return (a, b);
        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
        double s = Math.Round(ang / 90) * 90;
        if (Math.Abs(ang - s) < 6)
        {
            if (Math.Abs(s) % 180 == 0) { double y = (a.Y + b.Y) / 2; a = new(a.X, y); b = new(b.X, y); }
            else { double x = (a.X + b.X) / 2; a = new(x, a.Y); b = new(x, b.Y); }
        }
        return (a, b);
    }

    void EndLine()
    {
        if (linePrev == null) return;
        var m = J.Clone(linePrev.L.Meta); var l = linePrev.L; linePrev.Restore(); linePrev = null;
        MetaLayers.Commit(l, "컷 테두리", m, MetaLayers.Render);
    }

    public override void Down(PointerArgs e)
    {
        EndLine();   // 키보드로 바꾼 슬라이더 값도 기록
        if (Mode == "poly")
        {
            poly ??= new();
            if (poly.Count >= 3)
            {
                var s = Gfx.S(MetaLayers.Canvas.V, poly[0]);
                if (Math.Sqrt((s.X - e.SX) * (s.X - e.SX) + (s.Y - e.SY) * (s.Y - e.SY)) < (e.Type == "touch" ? 26 : 16)) { FinishPoly(); return; }
            }
            poly.Add(new P2(e.X, e.Y));
            return;
        }
        var layer = Panels.PanelLayer(false);
        drag = new DragSt { X0 = e.X, Y0 = e.Y, X1 = e.X, Y1 = e.Y, SX0 = e.SX, SY0 = e.SY };
        if (Mode == "move" && layer != null)
        {
            if (!App.CheckEditable(layer)) { drag = null; return; }
            int i = Panels.PanelAt(layer, e.X, e.Y);
            if (i >= 0) { drag.Panel = i; drag.Orig = Panels.List(layer.Meta)[i]; drag.Prev = new MetaPreview(layer); }
        }
    }

    public override void Move(PointerArgs e)
    {
        var d = drag; if (d == null) return;
        d.X1 = e.X; d.Y1 = e.Y;
        if (Math.Sqrt((e.SX - d.SX0) * (e.SX - d.SX0) + (e.SY - d.SY0) * (e.SY - d.SY0)) > 6) d.Moved = true;
        if (Mode == "move" && d.Panel >= 0 && d.Moved)
        {
            var meta = d.Prev.OrigMeta;
            var ps = Panels.List(meta);
            ps[d.Panel] = d.Orig.Select(p => new P2(p.X + e.X - d.X0, p.Y + e.Y - d.Y0)).ToList();
            meta["panels"] = Panels.PanelsJson(ps);
            d.Prev.Show(meta, MetaLayers.Render);
        }
    }

    public override void Hover(PointerArgs e) { if (poly != null) cursor = new P2(e.X, e.Y); }

    public override void Up(PointerArgs e)
    {
        var d = drag; drag = null;
        if (d == null) return;
        if (Mode == "select" || !d.Moved)
        {
            d.Prev?.Restore();
            var layer = Panels.PanelLayer(false);
            int i = Panels.PanelAt(layer, d.X0, d.Y0);
            if (i < 0) { App.Toast("컷 안쪽을 탭하세요"); return; }
            var q = Panels.List(layer.Meta)[i]; double ln = J.Num(layer.Meta, "line", 6);
            var doc = App.Doc;
            var m = Panels.PolySelection(doc, q, (int)Math.Ceiling(ln));
            App.History.SelectionChange(doc, "컷 선택", () => doc.Sel.Mask = m);
            MetaLayers.Redraw();
            return;
        }
        if (Mode == "draw")
        {
            var r = Geo.FromPoints(d.X0, d.Y0, d.X1, d.Y1);
            if (r.w < 8 || r.h < 8) return;
            if (Panels.PanelLayer(false) is Layer pl && !App.CheckEditable(pl)) return;
            Panels.Set(m => { var ps = Panels.List(m); ps.Add(Panels.RectPts(r.x, r.y, r.w, r.h)); m["panels"] = Panels.PanelsJson(ps); return m; });
        }
        else if (Mode == "split") Split(d);
        else if (Mode == "move" && d.Panel >= 0)
        {
            var meta = J.Clone(d.Prev.L.Meta);
            d.Prev.Restore();
            MetaLayers.Commit(d.Prev.L, "컷 편집", meta, MetaLayers.Render);
        }
    }

    void Split(DragSt d)
    {
        var layer = Panels.PanelLayer(false);
        var list = Panels.List(layer?.Meta);
        if (layer == null || list.Count == 0) { App.Toast("먼저 \"페이지 컷 만들기\"로 기본 컷을 만드세요"); return; }
        if (!App.CheckEditable(layer)) return;
        var (a, b) = SnapLine(d);
        var (gx, gy) = Gutters();
        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
        double g = gy * Math.Abs(Math.Cos(ang)) + gx * Math.Abs(Math.Sin(ang));
        var outp = new List<List<P2>>();
        bool did = false;
        foreach (var q in list)
        {
            bool crosses = q.Select((v, i) => Panels.SegCross(a, b, v, q[(i + 1) % q.Count])).Any(x => x) || Geo.InPoly(q, a.X, a.Y) || Geo.InPoly(q, b.X, b.Y);
            if (crosses)
            {
                var (p1, p2) = Panels.SplitPoly(q, a, b, g);
                var parts = new[] { p1, p2 }.Where(p => p.Count >= 3 && Geo.Area(p) > 400).ToList();
                if (parts.Count == 2) { outp.AddRange(parts); did = true; continue; }
            }
            outp.Add(q);
        }
        if (!did) { App.Toast("컷을 가로지르도록 선을 그어 주세요"); return; }
        Panels.Set(m => { m["panels"] = Panels.PanelsJson(outp); return m; });
    }

    void FinishPoly()
    {
        var q = poly; poly = null; cursor = null;
        if (q == null || q.Count < 3) { MetaLayers.Canvas?.Present(); return; }
        Panels.Set(m => { var ps = Panels.List(m); ps.Add(q); m["panels"] = Panels.PanelsJson(ps); return m; });
    }

    public override void Deactivate() { EndLine(); poly = null; cursor = null; }
    public override void Cancel() { drag?.Prev?.Restore(); if (drag?.Prev != null) App.InvalidateAll(); drag = null; }
    public override bool HandleKey(Keys k)
    {
        if (poly == null) return false;
        if ((k & Keys.KeyCode) == Keys.Enter) { FinishPoly(); return true; }
        if ((k & Keys.KeyCode) == Keys.Escape) { poly = null; cursor = null; MetaLayers.Redraw(); return true; }
        return false;
    }

    public override Rectangle OverlayBounds(ViewXf v) => CUI.ScreenAll();
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var layer = Panels.PanelLayer(false);
        void Outline(Pen pen, IList<P2> q, bool close = true)
        {
            if (q.Count < 2) return;
            var pts = q.Select(p => Gfx.S(v, p)).ToArray();
            if (close) g.DrawPolygon(pen, pts); else g.DrawLines(pen, pts);
        }
        if (layer != null && layer.Visible)
        {
            using var pen = CUI.Dash(Color.FromArgb(140, 47, 124, 246), 1, 5, 4);
            foreach (var q in Panels.List(layer.Meta)) Outline(pen, q);
        }
        using var red = new Pen(Color.FromArgb(232, 72, 74), 2);
        if (poly != null)
        {
            var q = cursor is P2 c ? poly.Append(c).ToList() : poly;
            Outline(red, q, false);
            for (int i = 0; i < poly.Count; i++)
            {
                var s = Gfx.S(v, poly[i]); float r = i == 0 ? 8 : 5;
                using var br = new SolidBrush(i == 0 ? Color.FromArgb(232, 72, 74) : Color.White);
                g.FillEllipse(br, s.X - r, s.Y - r, r * 2, r * 2); g.DrawEllipse(red, s.X - r, s.Y - r, r * 2, r * 2);
            }
        }
        var d = drag;
        if (d == null || !d.Moved) return;
        if (Mode == "split") { var (a, b) = SnapLine(d); Outline(red, new[] { a, b }, false); }
        else if (Mode == "draw") { var r = Geo.FromPoints(d.X0, d.Y0, d.X1, d.Y1); Outline(red, Panels.RectPts(r.x, r.y, r.w, r.h)); }
    }

    public override Control BuildOptions() { host = new CUI.Host(Items); return host.Panel; }
    IEnumerable<Control> Items()
    {
        var help = new Dictionary<string, string>
        {
            ["split"] = "컷을 가로지르는 선을 그으면 나뉩니다. 비스듬히 그으면 사선 컷 (간격 자동).",
            ["draw"] = "드래그해서 사각형 컷을 만듭니다.",
            ["poly"] = "탭으로 꼭짓점을 찍고, 첫 점(빨간 점)을 다시 탭하거나 Enter로 다각형 컷 완성.",
            ["select"] = "컷 안을 탭하면 그 컷 안쪽이 선택됩니다 → 그 안에서만 그려져요.",
            ["move"] = "컷을 끌어서 옮깁니다.",
        };
        var helpLabel = Opt.Help(help[Mode]);
        yield return Opt.Title("컷 도구");
        yield return CUI.Seg(new[] { ("split", "자르기"), ("draw", "사각형"), ("poly", "다각형"), ("select", "컷 선택"), ("move", "이동") }, () => Mode, v => { if (poly != null) FinishPoly(); Mode = v; helpLabel.Text = help[v]; });
        yield return helpLabel;
        yield return Opt.Check("거의 수평/수직이면 맞추기", () => Snap, v => Snap = v);
        yield return Opt.Row(Opt.Button("페이지 컷 만들기", Panels.PageFrameDialog), Opt.Button("마지막 컷 삭제", () =>
        {
            var l = Panels.PanelLayer(false); var ps = Panels.List(l?.Meta);
            if (l == null || ps.Count == 0 || !App.CheckEditable(l)) return;
            Panels.Set(m => { m["panels"] = Panels.PanelsJson(ps.Take(ps.Count - 1)); return m; });
        }));
        yield return Opt.Button("컷 나누기 템플릿…", Panels.TemplatesDialog);
        yield return Opt.Title("컷 설정");
        yield return Opt.Slider("테두리 굵기", 0, 40, () => J.Num(Panels.PanelLayer(false)?.Meta, "line", 6), v =>
        {
            var l = Panels.PanelLayer(false); if (l == null) return;
            linePrev ??= new MetaPreview(l);
            var m = linePrev.OrigMeta; m["line"] = Math.Round(v * 2) / 2; linePrev.Show(m, MetaLayers.Render);
        }, step: 0.5, onEnd: EndLine);
        yield return Opt.Slider("좌우 간격", 0, 200, () => Gutters().gx, v => GutterX = Math.Max(0.01, v), step: 1);
        yield return Opt.Slider("상하 간격", 0, 300, () => Gutters().gy, v => GutterY = Math.Max(0.01, v), step: 1);
        yield return Opt.Check("컷 바깥을 용지색으로 가리기", () => J.Bool(Panels.PanelLayer(false)?.Meta, "maskOutside", true), v =>
        {
            var l = Panels.PanelLayer(false); if (l == null || J.Bool(l.Meta, "maskOutside", true) == v) return;
            Panels.Set(m => { m["maskOutside"] = v; return m; }, "컷 편집");
        });
        yield return CUI.ColorBtn("테두리 색", () => J.Str(Panels.PanelLayer(false)?.Meta, "color", "#000000"), v =>
        {
            if (Panels.PanelLayer(false) == null) return;
            Panels.Set(m => { m["color"] = v; return m; }, "컷 편집");
        });
    }
}
