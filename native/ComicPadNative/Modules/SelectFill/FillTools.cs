// 채우기 도구 (웹 tools/basic.js fillTool): 참조(모든 레이어/참조 레이어/현재 레이어)·허용 오차·틈 막기(경쟁 되돌려 채우기)·영역 확장·디더 무늬.
// + 네이티브: 틈 닫기 (끊긴 선 끝 잇기, 분홍 미리 보기)·덜 칠한 곳 메우기·여러 칸 칠하기 (GapClose.cs).
// 그라데이션 (웹 tools/paint2.js gradientTool): 직선/원형, 전경색→투명 / 전경색→배경색, 불투명도, 선택 영역 안에만, Shift 45°.
// 올가미 채우기 (웹 tools/paint2.js lassoFillTool): 둘러싼 곳을 전경색으로 칠하기/지우기 (Alt = 지우기).
using System.Diagnostics;
using System.Drawing.Drawing2D;
using Blend = ComicPadNative.Core.Blend;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.SelectFill;

public sealed class FillTool : ToolBase
{
    public override string Id => "fill";
    public override string Name => "채우기";
    public override string Icon => "🪣";
    public override Keys Key => Keys.G;

    public string Source = SelUtil.GetS("fill.source", "all");
    public double Tolerance = SelUtil.GetD("fill.tolerance", 12), Gap = SelUtil.GetD("fill.gap", 4), Expand = SelUtil.GetD("fill.expand", 2);
    public double Dither = SelUtil.GetD("fill.dither", 0);
    public int DitherSize = (int)SelUtil.GetD("fill.ditherSize", 4);
    /// <summary>틈 닫기 (선 끝 잇기, px — 0 = 끔), 덜 칠한 곳 메우기 (px — 0 = 끔)</summary>
    public double Close = SelUtil.GetD("fill.close", 0), Leftover = SelUtil.GetD("fill.leftover", 0);
    /// <summary>여러 칸 칠하기 (끌어서), 닫는 선 미리 보기</summary>
    public bool Multi = SelUtil.GetB("fill.multi", false), ShowClose = SelUtil.GetB("fill.closePreview", true);
    bool working;
    public override bool Busy => working || multi != null;

    public Raster.FillOpt Options() => new() { Tolerance = Tolerance, Gap = Gap, Expand = Expand, Close = Close, Leftover = Leftover };

    public override async void Down(PointerArgs e)
    {
        var doc = App.Doc;
        var owner = doc?.Active;
        if (!App.CheckEditable(owner)) return;
        var target = owner.Target;
        int x = (int)Math.Floor(e.X), y = (int)Math.Floor(e.Y);
        if (x < 0 || y < 0 || x >= doc.W || y >= doc.H) return;
        if (working || multi != null) return;
        if (Multi) { StartMulti(doc, owner, x, y); return; }
        working = true;
        (byte[] mask, Rect bounds) res;
        var canvas = (App.Main as MainForm)?.Canvas;
        try
        {
            var img = SelUtil.ReferenceImage(Source);
            var fo = Options();
            var task = Task.Run(() => Raster.FillMask(img, doc.W, doc.H, x, y, fo));
            if (await Task.WhenAny(task, Task.Delay(250)) != task) { App.Toast("채우는 중…"); if (canvas != null) canvas.Cursor = Cursors.WaitCursor; }
            res = await task;
        }
        catch (Exception ex) { App.Toast("채우기: " + ex.Message); return; }
        finally { working = false; if (canvas != null) canvas.Cursor = App.Tool?.Cursor ?? Cursors.Cross; }
        if (res.bounds.IsEmpty || App.Doc != doc || !doc.Layers.Contains(owner)) return;
        uint col = SelUtil.PaintColor(owner, App.Fg);
        var r = Apply(doc, owner, target, res.mask, res.bounds, col, Dither, DitherSize);
        SelUtil.Done(r);
    }

    /// <summary>채울 마스크를 레이어에 (색 → 디더 → 선택 영역으로 자르기 → 표준/투명 픽셀 잠금이면 source-atop)</summary>
    public static Rect Apply(Document doc, Layer owner, Surface target, byte[] mask, Rect b, uint col, double dither, int ditherSize)
    {
        var pe = new PixelEdit(App.History, owner, target, "채우기");
        ApplyInto(pe, doc, owner, target, mask, b, col, dither, ditherSize);
        pe.Commit("채우기");
        return b;
    }

    /// <summary>Apply 의 그리기 부분 (기록은 pe 에 — 여러 칸 칠하기는 끝날 때 한 번에 확정)</summary>
    public static void ApplyInto(PixelEdit pe, Document doc, Layer owner, Surface target, byte[] mask, Rect b, uint col, double dither, int ditherSize)
    {
        var sel = doc.Sel;
        bool useSel = SelUtil.Active(sel);
        bool atop = owner.AlphaLock && !owner.EditMask;
        bool dith = dither > 0 && dither < 1;
        pe.Touch(b);
        var buf = target.Read(b);
        int W = doc.W;
        Parallel.For(0, b.H, yy =>
        {
            int y = b.Y + yy;
            for (int xx = 0; xx < b.W; xx++)
            {
                int x = b.X + xx;
                if (mask[y * W + x] == 0) continue;
                uint a = 255;
                if (dith && !Raster.DitherOn(dither, ditherSize, x, y)) continue;
                if (useSel) a = sel.Mask[y * W + x];
                if (a == 0) continue;
                int i = yy * b.W + xx;
                uint src = Px.Scale(col, a);
                buf[i] = atop ? Px.Composite(src, buf[i], Blend.Atop) : Px.Over(src, buf[i]);
            }
        });
        target.Write(b, buf);
    }

    // ---- 여러 칸 칠하기 (끌면서 지나간 칸마다) ---------------------------------------------------------------
    sealed class MultiSt
    {
        public Document Doc; public Layer Owner; public Surface Target; public byte[] Img; public PixelEdit Edit; public byte[] Done;
        public readonly Queue<(int x, int y)> Q = new(); public (int x, int y) First; public Raster.FillOpt Fo; public uint Col;
        public Rect Dirty = Rect.Empty; public int Regions; public bool Ended, Cancelled, Pumping; public double LX, LY;
        public (int x, int y) LastQ = (-1, -1);
    }
    MultiSt multi;
    /// <summary>시험용: 여러 칸 칠하기가 끝났는지</summary>
    public bool MultiIdle => multi == null;

    void StartMulti(Document doc, Layer owner, int x, int y)
    {
        var m = new MultiSt
        {
            Doc = doc, Owner = owner, Target = owner.Target, Img = SelUtil.ReferenceImage(Source), Done = new byte[doc.W * doc.H], First = (x, y), Fo = Options(),
            Col = SelUtil.PaintColor(owner, App.Fg), LX = x, LY = y,
        };
        m.Edit = new PixelEdit(App.History, owner, m.Target, "여러 칸 칠하기");
        multi = m;
        m.Q.Enqueue((x, y)); m.LastQ = (x, y);
        Pump(m);
    }

    void MultiMove(PointerArgs e)
    {
        var m = multi; if (m == null || m.Ended) return;
        double d = Math.Sqrt((e.X - m.LX) * (e.X - m.LX) + (e.Y - m.LY) * (e.Y - m.LY));
        int n = Math.Max(1, (int)Math.Ceiling(d / 2));
        for (int i = 1; i <= n; i++)
        {
            int x = (int)Math.Floor(m.LX + (e.X - m.LX) * i / n), y = (int)Math.Floor(m.LY + (e.Y - m.LY) * i / n);
            if (x < 0 || y < 0 || x >= m.Doc.W || y >= m.Doc.H || m.Done[y * m.Doc.W + x] != 0 || m.LastQ == (x, y)) continue;
            m.Q.Enqueue((x, y)); m.LastQ = (x, y);
        }
        m.LX = e.X; m.LY = e.Y;
        Pump(m);
    }

    async void Pump(MultiSt m)
    {
        if (m.Pumping) return;
        m.Pumping = true;
        try
        {
            while (m.Q.Count > 0 && !m.Cancelled)
            {
                var (x, y) = m.Q.Dequeue();
                int W = m.Doc.W, H = m.Doc.H;
                if (m.Done[y * W + x] != 0 || !GapClose.SameColor(m.Img, W, m.First.x, m.First.y, x, y, m.Fo.Tolerance)) continue;
                var (mask, b) = await Task.Run(() => Raster.FillMask(m.Img, W, H, x, y, m.Fo));
                if (m.Cancelled || b.IsEmpty || App.Doc != m.Doc || !m.Doc.Layers.Contains(m.Owner)) continue;
                for (int yy = b.Y; yy < b.B; yy++) for (int i = yy * W + b.X, e = i + b.W; i < e; i++) if (mask[i] != 0) m.Done[i] = 1;
                ApplyInto(m.Edit, m.Doc, m.Owner, m.Target, mask, b, m.Col, Dither, DitherSize);
                m.Dirty = m.Dirty.Union(b); m.Regions++;
                App.Invalidate(b);
            }
        }
        catch (Exception ex) { App.Toast("채우기: " + ex.Message); }
        finally { m.Pumping = false; }
        if (m.Ended) FinishMulti(m);
    }

    void FinishMulti(MultiSt m)
    {
        if (multi != m) return;
        multi = null;
        if (m.Cancelled) { m.Edit.Cancel(); if (!m.Dirty.IsEmpty) App.Invalidate(m.Dirty); return; }
        m.Edit.Commit(m.Regions > 1 ? $"여러 칸 칠하기 ({m.Regions}칸)" : "채우기");
        if (!m.Dirty.IsEmpty) SelUtil.Done(m.Dirty);
    }

    public override void Move(PointerArgs e) { if (multi != null) MultiMove(e); }
    public override void Up(PointerArgs e)
    {
        var m = multi; if (m == null) return;
        MultiMove(e);
        m.Ended = true;
        if (!m.Pumping) FinishMulti(m);
    }
    public override void Cancel()
    {
        var m = multi; if (m == null) return;
        m.Cancelled = true; m.Ended = true; m.Q.Clear();
        if (!m.Pumping) FinishMulti(m);
    }

    // ---- 틈 닫기 미리 보기 (분홍 선) ---------------------------------------------------------------------------
    List<GapClose.Seg> preview;
    int previewGen;
    System.Windows.Forms.Timer previewTimer;
    bool active;

    public override void Activate() { active = true; App.Invalidated -= OnInvalidated; App.Invalidated += OnInvalidated; RefreshPreview(); }
    public override void Deactivate() { active = false; App.Invalidated -= OnInvalidated; previewTimer?.Stop(); previewGen++; preview = null; }

    void OnInvalidated(Rect r)
    {
        if (r.IsEmpty || !active || Close <= 0 || !ShowClose || multi != null) return;
        if (previewTimer == null) { previewTimer = new System.Windows.Forms.Timer { Interval = 700 }; previewTimer.Tick += (_, _) => { previewTimer.Stop(); RefreshPreview(); }; }
        previewTimer.Stop(); previewTimer.Start();
    }

    /// <summary>닫는 선을 다시 찾음 (빈 곳 = 투명·흰색 기준. 실제 채우기는 누른 곳의 색 기준이라 조금 다를 수 있음)</summary>
    public async void RefreshPreview()
    {
        int gen = ++previewGen;
        var doc = App.Doc;
        if (!active || doc == null || Close <= 0 || !ShowClose) { if (preview != null) { preview = null; Redraw(); } return; }
        try
        {
            var img = SelUtil.ReferenceImage(Source);
            double tol = Tolerance, close = Close; int w = doc.W, h = doc.H;
            var segs = await Task.Run(() => GapClose.Find(GapClose.PreviewWall(img, w, h, tol), w, h, close));
            if (gen != previewGen || App.Doc != doc) return;
            preview = segs;
            Redraw();
        }
        catch { }
    }
    static void Redraw() => (App.Main as MainForm)?.Canvas?.RedrawAll();

    public override Rectangle OverlayBounds(ViewXf v)
    {
        var c = (App.Main as MainForm)?.Canvas;
        return preview == null || preview.Count == 0 || c == null || Close <= 0 ? Rectangle.Empty : new Rectangle(0, 0, c.Width, c.Height);
    }
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var p = preview;
        if (p == null || p.Count == 0 || Close <= 0) return;
        using var w = new Pen(Color.FromArgb(200, 255, 255, 255), 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var pk = new Pen(Color.FromArgb(235, 40, 160), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var s in p.Take(20000))
        {
            var (ax, ay) = v.ToScreen(s.X0 + 0.5, s.Y0 + 0.5); var (bx, by) = v.ToScreen(s.X1 + 0.5, s.Y1 + 0.5);
            g.DrawLine(w, (float)ax, (float)ay, (float)bx, (float)by);
            g.DrawLine(pk, (float)ax, (float)ay, (float)bx, (float)by);
        }
    }

    // 웹 basic.js fillTool.options 와 같은 순서·글 (위), 네이티브에만 있는 것은 아래 "더 똑똑하게 칠하기" 구역
    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("채우기"),
        Ui.Segmented(new[] { ("all", "모든 레이어"), ("ref", "참조 레이어"), ("layer", "현재 레이어") }, () => Source, v => { Source = v; SelUtil.Put("fill.source", v); RefreshPreview(); }),
        Opt.Slider("색 허용 오차", 0, 255, () => Tolerance, v => { Tolerance = v; SelUtil.Put("fill.tolerance", v); }, step: 1, onEnd: RefreshPreview),
        Opt.Slider("틈 막기 (이 너비까지 빈틈은 선으로)", 0, 50, () => Gap, v => { Gap = v; SelUtil.Put("fill.gap", v); }, step: 1, fmt: v => v > 0 ? $"{v:0}px" : "끔"),
        Opt.Help("선이 살짝 끊겨 있어도 이 너비 이하의 빈틈은 막힌 것으로 보고, 틈 너머로는 칠하지 않습니다. 선이 많이 끊긴 러프는 10~20px."),
        Opt.Slider("영역 확장", 0, 10, () => Expand, v => { Expand = v; SelUtil.Put("fill.expand", v); }, step: 1, fmt: v => $"{v:0}px"),
        Opt.Help("선 아래까지 칠하려면 \"영역 확장\"을 1~3px로. 선이 살짝 끊겨 있으면 \"틈 닫기\"를 올리세요."),
        Opt.Slider("디더 무늬 (칠하는 칸 비율)", 0, 1, () => Dither, v => { Dither = v >= 1 ? 0 : v; SelUtil.Put("fill.dither", Dither); }, pct: true, step: 0.0625, fmt: v => v > 0 && v < 1 ? $"{v * 100:0}%" : "끔"),
        Ui.Segmented(new[] { (2, "2×2"), (4, "4×4") }, () => DitherSize, v => { DitherSize = v; SelUtil.Put("fill.ditherSize", v); }),
        Opt.Title("더 똑똑하게 칠하기"),
        Opt.Slider("틈 닫기 (끊긴 선 끝 잇기)", 0, 50, () => Close, v => { Close = v; SelUtil.Put("fill.close", v); }, step: 1, fmt: v => v > 0 ? $"{v:0}px" : "끔", onEnd: RefreshPreview),
        Opt.Check("닫는 선 미리 보기 (분홍 선)", () => ShowClose, v => { ShowClose = v; SelUtil.Put("fill.closePreview", v); RefreshPreview(); }),
        Opt.Help("선 끝이 이 칸 수 이하로 다른 선과 떨어져 있으면 보이지 않는 선(분홍)으로 이어 막고 칠합니다. 좁은 칸·평행선은 막지 않습니다."),
        Opt.Slider("덜 칠한 곳 메우기", 0, 40, () => Leftover, v => { Leftover = v; SelUtil.Put("fill.leftover", v); }, step: 1, fmt: v => v > 0 ? $"{v:0}px 이하" : "끔"),
        Opt.Help("칠한 곳 안에 남은 작은 빈틈(잡티·점)을 함께 칠합니다."),
        Opt.Check("여러 칸 칠하기 (끌어서)", () => Multi, v => { Multi = v; SelUtil.Put("fill.multi", v); }),
        Opt.Help("켜면 끌면서 지나간 칸을 모두 칠합니다 (실행취소 한 번). 처음 누른 곳과 색이 다른 곳(선 위)은 건너뜁니다."));
}

public sealed class GradientTool : ToolBase
{
    public override string Id => "gradient";
    public override string Name => "그라데이션";
    public override string Icon => "▤";
    public override Keys Key => Keys.J;

    public string Type = SelUtil.GetS("gradient.type", "linear");        // linear | radial
    public string Colors = SelUtil.GetS("gradient.colors", "fg-clear");  // fg-clear | fg-bg
    public double Opacity = SelUtil.GetD("gradient.opacity", 1);

    sealed class St { public Layer Owner; public Surface Target; public Rect Region; public double X0, Y0, X1, Y1; public PixelEdit Edit; public bool Dirty; }
    St st;
    readonly Stopwatch clock = new();
    long lastMs, costMs;
    System.Windows.Forms.Timer timer;
    public override bool Busy => st != null;

    public override void Down(PointerArgs e)
    {
        var doc = App.Doc;
        var owner = doc?.Active;
        if (!App.CheckEditable(owner)) return;
        var sel = doc.Sel;
        var region = SelUtil.Active(sel) ? SelUtil.Bounds(sel) : new Rect(0, 0, doc.W, doc.H);
        var edit = App.History.BeginPixels(owner, "그라데이션");
        edit.Touch(region);
        st = new St { Owner = owner, Target = owner.Target, Region = region, X0 = e.X, Y0 = e.Y, X1 = e.X, Y1 = e.Y, Edit = edit };
        clock.Restart(); lastMs = -1000;
        if (timer == null) { timer = new System.Windows.Forms.Timer { Interval = 30 }; timer.Tick += (_, _) => Pending(); }
        timer.Start();
    }

    /// <summary>쉬는 동안 미뤄 둔 미리 보기</summary>
    void Pending()
    {
        var s = st;
        if (s == null) { timer.Stop(); return; }
        if (!s.Dirty) return;
        long now = clock.ElapsedMilliseconds;
        if (now - lastMs >= Math.Max(16, costMs)) { Render(s); lastMs = clock.ElapsedMilliseconds; costMs = lastMs - now; }
    }

    public override void Move(PointerArgs e)
    {
        var s = st;
        if (s == null) return;
        double x = e.X, y = e.Y;
        if (e.Shift)
        {
            double a = Math.Round(Math.Atan2(y - s.Y0, x - s.X0) / (Math.PI / 4)) * (Math.PI / 4), d = Math.Sqrt((x - s.X0) * (x - s.X0) + (y - s.Y0) * (y - s.Y0));
            x = s.X0 + Math.Cos(a) * d; y = s.Y0 + Math.Sin(a) * d;
        }
        s.X1 = x; s.Y1 = y;
        // 미리 보기: 큰 영역은 계산·합성이 무거우니 걸린 시간만큼 쉬었다가
        long now = clock.ElapsedMilliseconds;
        if (now - lastMs >= Math.Max(16, costMs)) { Render(s); lastMs = clock.ElapsedMilliseconds; costMs = lastMs - now; }
        else s.Dirty = true;
    }

    public override void Up(PointerArgs e)
    {
        var s = st;
        if (s == null) return;
        st = null; timer?.Stop();
        if (Math.Sqrt((s.X1 - s.X0) * (s.X1 - s.X0) + (s.Y1 - s.Y0) * (s.Y1 - s.Y0)) < 1) { s.Edit.Cancel(); App.Invalidate(s.Region); return; }
        Render(s, false);
        s.Edit.Commit("그라데이션");
        SelUtil.Done(s.Region);
    }

    public override void Cancel()
    {
        var s = st;
        if (s == null) return;
        st = null; timer?.Stop();
        s.Edit.Cancel();
        App.Invalidate(s.Region);
    }

    /// <summary>그리기 전 상태 위에 그라데이션을 다시 계산해 넣음</summary>
    void Render(St s, bool show = true)
    {
        s.Dirty = false;
        var doc = App.Doc;
        Fill(doc, s.Owner, s.Target, s.Region, s.Edit, Type, s.X0, s.Y0, s.X1, s.Y1, SelUtil.PaintColor(s.Owner, App.Fg), Colors == "fg-bg" ? SelUtil.PaintColor(s.Owner, App.Bg) : (uint?)null, Opacity);
        if (show) App.Invalidate(s.Region);
    }

    /// <summary>그라데이션 계산 (웹: 캔버스 그라데이션 → 선택 영역으로 자르기 → 불투명도로 덮기)</summary>
    public static void Fill(Document doc, Layer owner, Surface t, Rect region, PixelEdit edit, string type, double x0, double y0, double x1, double y1, uint fg, uint? bg, double opacity)
    {
        var sel = doc.Sel;
        bool useSel = SelUtil.Active(sel);
        bool atop = owner.AlphaLock && !owner.EditMask;
        uint f = Px.Unpremul(fg), e = bg.HasValue ? Px.Unpremul(bg.Value) : f;
        double fa = (f >> 24) / 255.0, ea = bg.HasValue ? (e >> 24) / 255.0 : 0;
        double fr = (f >> 16) & 255, fgc = (f >> 8) & 255, fb = f & 255, er = (e >> 16) & 255, eg = (e >> 8) & 255, eb = e & 255;
        double dx = x1 - x0, dy = y1 - y0, len2 = dx * dx + dy * dy, rad = Math.Max(1, Math.Sqrt(len2));
        bool radial = type == "radial";
        uint op = (uint)Math.Clamp((int)Math.Round(opacity * 255), 0, 255);
        int W = doc.W;
        var tiles = t.TilesIn(region).ToArray();
        Parallel.ForEach(tiles, ti =>
        {
            var tr = t.TileRect(ti).Intersect(region);
            if (tr.IsEmpty) return;
            var bt = edit.BeforeTile(ti);
            if (bt == null && atop) return;
            int tx = ti % t.TX, ty = ti / t.TX;
            var tile = t.Tile(tx, ty, true);
            for (int y = tr.Y; y < tr.B; y++)
            {
                int row = (y - ty * Surface.TS) * Surface.TS - tx * Surface.TS;
                double py = y + 0.5;
                for (int x = tr.X; x < tr.R; x++)
                {
                    double px = x + 0.5, u;
                    if (radial) u = Math.Sqrt((px - x0) * (px - x0) + (py - y0) * (py - y0)) / rad;
                    else u = len2 <= 0 ? 0 : ((px - x0) * dx + (py - y0) * dy) / len2;
                    u = u < 0 ? 0 : u > 1 ? 1 : u;
                    // 미리 곱하지 않은 색으로 보간 (8비트 캔버스 버퍼처럼 반올림)
                    double a = fa + (ea - fa) * u;
                    uint A = (uint)Math.Round(a * 255), R = (uint)Math.Round(fr + (er - fr) * u), G = (uint)Math.Round(fgc + (eg - fgc) * u), B = (uint)Math.Round(fb + (eb - fb) * u);
                    uint src = !radial && len2 <= 0 ? 0 : Px.Premul(Px.Pack(A, R, G, B));   // 시작=끝 이면 캔버스처럼 아무것도 안 칠함
                    if (useSel) src = Px.Scale(src, sel.Mask[y * W + x]);
                    src = Px.Scale(src, op);
                    int li = row + x;
                    uint before = bt == null ? 0 : bt[li];
                    tile[li] = atop ? Px.Composite(src, before, Blend.Atop) : Px.Over(src, before);
                }
            }
        });
    }

    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var s = st;
        if (s == null) return;
        var (ax, ay) = v.ToScreen(s.X0, s.Y0); var (bx, by) = v.ToScreen(s.X1, s.Y1);
        using var w = new Pen(Color.White, 3); using var bl = new Pen(Color.FromArgb(47, 124, 246), 1.5f);
        g.DrawLine(w, (float)ax, (float)ay, (float)bx, (float)by);
        g.DrawLine(bl, (float)ax, (float)ay, (float)bx, (float)by);
        foreach (var (x, y) in new[] { (ax, ay), (bx, by) }) { g.FillEllipse(System.Drawing.Brushes.White, (float)x - 5, (float)y - 5, 10, 10); g.DrawEllipse(bl, (float)x - 5, (float)y - 5, 10, 10); }
    }
    public override Rectangle OverlayBounds(ViewXf v)
    {
        var s = st;
        if (s == null) return Rectangle.Empty;
        var (ax, ay) = v.ToScreen(s.X0, s.Y0); var (bx, by) = v.ToScreen(s.X1, s.Y1);
        return Rectangle.FromLTRB((int)Math.Min(ax, bx) - 8, (int)Math.Min(ay, by) - 8, (int)Math.Max(ax, bx) + 9, (int)Math.Max(ay, by) + 9);
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("그라데이션"),
        Ui.Segmented(new[] { ("linear", "직선"), ("radial", "원형") }, () => Type, v => { Type = v; SelUtil.Put("gradient.type", v); }),
        Ui.Segmented(new[] { ("fg-clear", "전경색 → 투명"), ("fg-bg", "전경색 → 배경색") }, () => Colors, v => { Colors = v; SelUtil.Put("gradient.colors", v); }),
        Opt.Slider("불투명도", 0, 1, () => Opacity, v => { Opacity = v; SelUtil.Put("gradient.opacity", v); }, pct: true, step: 0.01),
        Opt.Help("드래그한 방향으로 칠합니다. 선택 영역이 있으면 그 안에만. Shift: 45° 단위."));
}

public sealed class LassoFillTool : ToolBase
{
    public override string Id => "lassofill";
    public override string Name => "올가미 채우기";
    public override string Icon => "➰";
    public override Keys Key => Keys.L;
    public override bool AllowAltPick => false;

    public string Mode = SelUtil.GetS("lassofill.mode", "fill");    // fill | erase
    List<PointF> pts;
    bool erase;
    public override bool Busy => pts != null;

    public override void Down(PointerArgs e) { pts = new() { new PointF((float)e.X, (float)e.Y) }; erase = Mode == "erase" || e.Alt; }
    public override void Move(PointerArgs e)
    {
        if (pts == null) return;
        var l = pts[^1];
        double z = (App.Main as MainForm)?.Canvas.V.Zoom ?? 1;
        if (Math.Sqrt((e.X - l.X) * (e.X - l.X) + (e.Y - l.Y) * (e.Y - l.Y)) * z > 2) pts.Add(new PointF((float)e.X, (float)e.Y));
    }
    public override void Up(PointerArgs e)
    {
        var p = pts;
        pts = null;
        if (p == null || p.Count < 3) return;
        var doc = App.Doc; var owner = doc.Active;
        if (!App.CheckEditable(owner)) return;
        var b = SelUtil.PointsBounds(p, 2).Clip(doc.W, doc.H);
        if (b.IsEmpty) return;
        var shape = SelUtil.PolygonShape(b, p);
        Apply(doc, owner, b, shape, erase, SelUtil.PaintColor(owner, App.Fg));
        SelUtil.Done(b);
    }

    /// <summary>모양(알파)을 전경색으로 칠하거나 지움 (선택 영역으로 자름)</summary>
    public static void Apply(Document doc, Layer owner, Rect b, byte[] shape, bool erase, uint col)
    {
        var sel = doc.Sel;
        bool useSel = SelUtil.Active(sel);
        bool atop = owner.AlphaLock && !owner.EditMask;
        var t = owner.Target;
        var pe = new PixelEdit(App.History, owner, t, erase ? "올가미 지우기" : "올가미 채우기");
        pe.Touch(b);
        var buf = t.Read(b);
        for (int y = 0; y < b.H; y++)
            for (int x = 0; x < b.W; x++)
            {
                int i = y * b.W + x;
                uint a = shape[i];
                if (useSel) a = Px.Mul(a, sel.Mask[(b.Y + y) * doc.W + b.X + x]);
                if (a == 0) continue;
                buf[i] = erase ? Px.Scale(buf[i], 255 - a) : atop ? Px.Composite(Px.Scale(col, a), buf[i], Blend.Atop) : Px.Over(Px.Scale(col, a), buf[i]);
            }
        t.Write(b, buf);
        pe.Commit(erase ? "올가미 지우기" : "올가미 채우기");
    }

    public override void Cancel() { pts = null; }

    List<PointF> Screen(ViewXf v) => pts == null ? new() : pts.Select(q => { var (x, y) = v.ToScreen(q.X, q.Y); return new PointF((float)x, (float)y); }).ToList();
    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        var p = Screen(v);
        if (p.Count < 2) return;
        var arr = p.ToArray();
        uint fg = Px.Unpremul(App.Fg);
        var c = erase ? Color.FromArgb(115, 255, 90, 90) : Color.FromArgb(115, (int)((fg >> 16) & 255), (int)((fg >> 8) & 255), (int)(fg & 255));
        if (p.Count >= 3) { using var br = new SolidBrush(c); g.FillPolygon(br, arr, FillMode.Winding); }
        using var pen = new Pen(Color.White, 1) { DashPattern = new[] { 4f, 3f } };
        g.DrawPolygon(pen, arr);
    }
    public override Rectangle OverlayBounds(ViewXf v) => SelectTool.Bounds(Screen(v), 3);

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("올가미 채우기"),
        Ui.Segmented(new[] { ("fill", "채우기"), ("erase", "지우기") }, () => Mode, v => { Mode = v; SelUtil.Put("lassofill.mode", v); }),
        Opt.Help("둘러싼 영역을 전경색으로 칠합니다 (밑색 깔기에 편함). Alt를 누르고 그리면 지우기."));
}
