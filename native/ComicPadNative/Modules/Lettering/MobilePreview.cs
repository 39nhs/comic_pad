// 웹툰 폰 미리보기: "웹툰 플랫폼 규격 내보내기"가 실제로 만드는 장들(ExportCommands.WebtoonSlicesOf — 같은 합성·같은 자르기·같은 JPG 압축)을
// 휴대폰 화면 폭(360/390/412 CSS px, 기기 픽셀 배율)으로 세로로 이어 보여 준다. 여러 페이지 작품이면 페이지를 읽는 순서로 이어 붙임.
// 휠·끌기로 넘기기, 읽는 속도 자동 넘김, 자른 곳(장 경계)·틈 표시, 노치·안전 영역, "글자 크기 확인"(폰에서 N px 보다 작은 글자 표시).
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;
using ComicPadNative.Modules.Filters;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Lettering;

public static class MobilePreview
{
    public sealed record Device(int CssW, int CssH, double Dpr, string Name, bool Ios);
    public static readonly Device[] Devices =
    {
        new(360, 780, 3, "360 작은 폰 (갤럭시 S 등)", false),
        new(390, 844, 3, "390 보통 폰 (아이폰 13·14 등)", true),
        new(412, 915, 2.625, "412 큰 폰 (픽셀 등)", false),
    };

    /// <summary>내보내기 규격 (마지막으로 쓴 "웹툰 플랫폼 규격" 값, 없으면 WEBTOON CANVAS)</summary>
    public sealed class Spec
    {
        public int W = 800, H = 1280; public string Fmt = "jpg"; public double MaxMB = 2; public bool Smart = true;
        public static Spec FromSettings()
        {
            var s = new Spec();
            if (App.Settings["webtoonExport"] is JsonObject o)
            {
                s.W = Math.Max(100, (int)Math.Round(J.Num(o, "w", 800))); s.H = Math.Max(200, (int)Math.Round(J.Num(o, "h", 1280)));
                s.Fmt = J.Str(o, "fmt", "jpg") == "png" ? "png" : "jpg"; s.MaxMB = J.Num(o, "maxMB", 2); s.Smart = J.Bool(o, "smart", true);
            }
            return s;
        }
    }

    /// <summary>글자 표시 (합친 띠 좌표)</summary>
    public sealed class Mark { public int Page; public RectangleF R; public double DocSize, PxPerDoc; public string Text, LayerId; public (double x, double y, double w, double h) DocBox; }

    /// <summary>내보낸 장들을 이어 붙인 띠 (가로 = 내보내기 폭 px)</summary>
    public sealed class Strip : IDisposable
    {
        public int W, H, SliceGap, PageGap;
        public long Bytes;
        public readonly List<(Bitmap bmp, int y, int page)> Slices = new();
        public readonly List<(int top, int h, int page)> Pages = new();
        public readonly List<Mark> Marks = new();
        public double CssH(int cssW) => H * (double)cssW / W;
        public double CssOf(Mark m, int cssW) => m.DocSize * m.PxPerDoc * cssW / W;
        public void Dispose() { foreach (var s in Slices) s.bmp.Dispose(); Slices.Clear(); }
    }

    /// <summary>문서(페이지)들을 내보내기 길로 만들어 이어 붙임</summary>
    public static Strip Build(IList<Document> docs, Spec spec, int sliceGap = 0, int pageGap = 0)
    {
        var s = new Strip { W = spec.W, SliceGap = sliceGap, PageGap = pageGap };
        long maxBytes = spec.MaxMB > 0 ? (long)(spec.MaxMB * 1048576) : 0;
        int y = 0;
        for (int pi = 0; pi < docs.Count; pi++)
        {
            var d = docs[pi];
            var slices = ExportCommands.WebtoonSlicesOf(d, spec.W, spec.H, spec.Fmt, maxBytes, spec.Smart);
            int top = y;
            var map = new List<(int ey, int eh, int sy)>();
            for (int i = 0; i < slices.Count; i++)
            {
                var sl = slices[i];
                using var ms = new MemoryStream(sl.Bytes);
                using var img = Image.FromStream(ms);
                var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, img.Width, img.Height);
                s.Slices.Add((bmp, y, pi));
                map.Add((sl.Y, sl.H, y));
                s.Bytes += sl.Bytes.Length;
                y += sl.H;
                if (i < slices.Count - 1) y += sliceGap;
            }
            s.Pages.Add((top, y - top, pi));
            // 글자 자리: 문서 → 내보내기 px → 띠 (장 사이 틈 반영)
            double kx = spec.W / (double)d.W, ky = Math.Round(d.H * kx) / d.H;
            double ToStrip(double ey)
            {
                foreach (var (sy0, sh, sy) in map) if (ey < sy0 + sh || (sy0, sh, sy) == map[^1]) return sy + (ey - sy0);
                return top + ey;
            }
            string lang = I18n.LangOf(d);
            foreach (var l in d.Layers)
            {
                if (!I18n.IsText(l) || !l.Visible || l.Draft || l.Meta["box"] is not JsonObject) continue;
                var eff = I18n.Effective(d, l.Meta, lang) ?? l.Meta;
                double size = J.Num(eff, "size", 42);
                if (eff["mat"] is JsonArray) size *= Math.Sqrt(Math.Abs(Mat2.From(J.Doubles(eff["mat"])).Det));
                var b = Work.BoxOf(l);
                double y0 = ToStrip(b.y * ky), y1 = ToStrip((b.y + b.h) * ky);
                s.Marks.Add(new Mark
                {
                    Page = pi, R = new RectangleF((float)(b.x * kx), (float)y0, (float)(b.w * kx), (float)(y1 - y0)),
                    DocSize = size, PxPerDoc = kx, Text = J.Str(eff, "text", ""), LayerId = l.Id, DocBox = b,
                });
            }
            if (pi < docs.Count - 1) y += pageGap;
        }
        s.H = y;
        return s;
    }

    // ---------------------------------------------------------------- 그리기
    public sealed class View
    {
        public Device Dev = Devices[1];
        public double Dpr = 3;
        public double Scroll;           // CSS px
        public double Zoom = 1;         // 화면 px / CSS px
        public bool Notch = true, TextCheck = true, Cuts = true, Bezel = true;
        public double MinCss = 11;
        public int Hover = -1;
    }

    public static int Bez(View v) => v.Bezel ? (int)Math.Round(14 * v.Zoom) : 0;
    public static Size FrameSize(View v) => new((int)Math.Ceiling(v.Dev.CssW * v.Zoom) + Bez(v) * 2, (int)Math.Ceiling(v.Dev.CssH * v.Zoom) + Bez(v) * 2);
    public static double MaxScroll(Strip s, View v) => Math.Max(0, s.CssH(v.Dev.CssW) - v.Dev.CssH);

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    /// <summary>안전 영역 (CSS px): 위·아래</summary>
    public static (double top, double bottom) Safe(Device dv) => dv.Ios ? (47, 34) : (28, 20);

    /// <summary>폰 하나를 (origin 왼쪽 위에) 그림</summary>
    public static void Paint(Graphics g, Strip s, View v, PointF origin)
    {
        var dev = v.Dev; float z = (float)v.Zoom, bez = Bez(v);
        var outer = new RectangleF(origin.X, origin.Y, dev.CssW * z + bez * 2, dev.CssH * z + bez * 2);
        var vp = new RectangleF(origin.X + bez, origin.Y + bez, dev.CssW * z, dev.CssH * z);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (v.Bezel) using (var op = Round(outer, 46 * z)) using (var br = new SolidBrush(Color.FromArgb(18, 18, 20))) g.FillPath(br, op);
        var state = g.Save();
        using var clip = Round(vp, v.Bezel ? 34 * z : 1);
        g.SetClip(clip);
        using (var bg = new SolidBrush(Color.FromArgb(44, 44, 50))) g.FillRectangle(bg, vp);   // 장 사이 틈·끝 뒤
        double k = vp.Width / s.W;                      // 화면 px / 띠 px
        double top = vp.Y - v.Scroll * z;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY);
        foreach (var (bmp, y, _) in s.Slices)
        {
            double sy0 = top + y * k, sy1 = top + (y + bmp.Height) * k;
            if (sy1 < vp.Top || sy0 > vp.Bottom) continue;
            // 보이는 부분만 (긴 장도 빠르게)
            double a = Math.Max(0, (vp.Top - sy0) / k - 1), b = Math.Min(bmp.Height, (vp.Bottom - sy0) / k + 1);
            var src = new RectangleF(0, (float)a, bmp.Width, (float)(b - a));
            var dst = new RectangleF(vp.X, (float)(sy0 + a * k), vp.Width, (float)((b - a) * k));
            g.DrawImage(bmp, Rectangle.Round(dst), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        // 자른 곳 (장 경계)·페이지 이음
        using var fnt = new Font("Malgun Gothic", Math.Max(7f, 9f * z), FontStyle.Bold, GraphicsUnit.Pixel);
        if (v.Cuts)
        {
            using var cut = new Pen(Color.FromArgb(200, 0, 170, 210), 1) { DashPattern = new[] { 5f, 4f } };
            using var pg = new Pen(Color.FromArgb(230, 240, 140, 46), 1.5f) { DashPattern = new[] { 8f, 4f } };
            int n = 0;
            foreach (var (bmp, y, page) in s.Slices)
            {
                n++;
                bool pageStart = s.Pages.Any(p => p.top == y && p.page == page);
                if (y == 0) continue;
                float sy = (float)(top + y * k);
                if (sy < vp.Top - 20 || sy > vp.Bottom + 20) continue;
                g.DrawLine(pageStart ? pg : cut, vp.X, sy, vp.Right, sy);
                string lab = pageStart ? $"{page + 1}쪽 · {n}장" : $"{n}장";
                var sz = g.MeasureString(lab, fnt);
                using var lb = new SolidBrush(pageStart ? Color.FromArgb(220, 240, 140, 46) : Color.FromArgb(200, 0, 150, 190));
                g.FillRectangle(lb, vp.Right - sz.Width - 6, sy + 2, sz.Width + 4, sz.Height);
                g.DrawString(lab, fnt, Brushes.White, vp.Right - sz.Width - 4, sy + 2);
            }
        }
        // 글자 크기 확인
        if (v.TextCheck)
        {
            using var bad = new Pen(Color.FromArgb(235, 229, 57, 53), 2f);
            using var ok = new Pen(Color.FromArgb(110, 46, 160, 90), 1f);
            using var badFill = new SolidBrush(Color.FromArgb(40, 229, 57, 53));
            for (int i = 0; i < s.Marks.Count; i++)
            {
                var m = s.Marks[i];
                double css = s.CssOf(m, dev.CssW);
                var r = new RectangleF(vp.X + (float)(m.R.X * k), (float)(top + m.R.Y * k), (float)(m.R.Width * k), (float)(m.R.Height * k));
                if (r.Bottom < vp.Top || r.Top > vp.Bottom) continue;
                bool small = css < v.MinCss;
                if (small) { g.FillRectangle(badFill, r); g.DrawRectangle(bad, r.X, r.Y, r.Width, r.Height); }
                else if (i == v.Hover) g.DrawRectangle(ok, r.X, r.Y, r.Width, r.Height);
                if (small || i == v.Hover)
                {
                    string lab = $"{css:0.#}px";
                    var sz = g.MeasureString(lab, fnt);
                    using var lb = new SolidBrush(small ? Color.FromArgb(229, 57, 53) : Color.FromArgb(46, 160, 90));
                    g.FillRectangle(lb, r.X, r.Y - sz.Height - 1, sz.Width + 2, sz.Height);
                    g.DrawString(lab, fnt, Brushes.White, r.X + 1, r.Y - sz.Height - 1);
                }
            }
        }
        // 노치·안전 영역
        if (v.Notch)
        {
            var (st, sb) = Safe(dev);
            using var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            g.FillRectangle(shade, vp.X, vp.Y, vp.Width, (float)(st * z));
            g.FillRectangle(shade, vp.X, (float)(vp.Bottom - sb * z), vp.Width, (float)(sb * z));
            using var blk = new SolidBrush(Color.Black);
            if (dev.Ios) using (var island = Round(new RectangleF(vp.X + vp.Width / 2 - 62 * z, vp.Y + 11 * z, 124 * z, 34 * z), 17 * z)) g.FillPath(blk, island);
            else g.FillEllipse(blk, vp.X + vp.Width / 2 - 6 * z, vp.Y + 8 * z, 12 * z, 12 * z);
            using var sf = new Font("Segoe UI", Math.Max(7f, 13f * z), FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("9:41", sf, Brushes.White, vp.X + (dev.Ios ? 30 : 16) * z, vp.Y + (dev.Ios ? 16 : 6) * z);
            g.FillRectangle(Brushes.White, vp.Right - 36 * z, vp.Y + (dev.Ios ? 21 : 10) * z, 22 * z, 10 * z);
            using var pill = Round(new RectangleF(vp.X + vp.Width / 2 - 67 * z, (float)(vp.Bottom - 9 * z), 134 * z, 5 * z), 2.5f * z);
            g.FillPath(Brushes.White, pill);
        }
        // 스크롤 막대
        double total = s.CssH(dev.CssW);
        if (total > dev.CssH)
        {
            float bh = (float)Math.Max(24, vp.Height * dev.CssH / total);
            float by = (float)(vp.Y + (vp.Height - bh) * (v.Scroll / Math.Max(1, total - dev.CssH)));
            using var sbB = new SolidBrush(Color.FromArgb(140, 128, 128, 128));
            g.FillRectangle(sbB, vp.Right - 5, by, 3, bh);
        }
        g.Restore(state);
    }

    /// <summary>폰 화면 한 장 (시험·저장용)</summary>
    public static Bitmap Shot(Strip s, View v, int margin = 16)
    {
        var fs = FrameSize(v);
        var bmp = new Bitmap(fs.Width + margin * 2, fs.Height + margin * 2, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(32, 32, 36));
        Paint(g, s, v, new PointF(margin, margin));
        return bmp;
    }

    public static string Summary(Strip s, View v, Spec spec)
    {
        double cssH = s.CssH(v.Dev.CssW);
        int small = s.Marks.Count(m => s.CssOf(m, v.Dev.CssW) < v.MinCss);
        double devW = v.Dev.CssW * v.Dpr;
        string sharp = spec.W >= devW - 0.5 ? "기기 폭보다 넓음 → 선명" : $"기기 폭 {devW:0}px 에 {devW / spec.W * 100:0}% 확대 → 조금 흐릿";
        return $"{s.Slices.Count}장 ({s.Bytes / 1048576.0:0.0}MB) · 내보내기 {spec.W}×{spec.H} · 길이 {s.H}px → 폰 {cssH:0}px (화면 {cssH / v.Dev.CssH:0.0}개) · {sharp} · 작은 글자 {small}개";
    }

    // ---------------------------------------------------------------- 창
    static Form open;
    public static void Open()
    {
        if (App.Doc == null) return;
        if (open != null && !open.IsDisposed) { open.Activate(); return; }
        open = new PreviewForm();
        open.Show(App.Main);
    }

    /// <summary>시험용: 창을 화면 밖에 띄워 그대로 그림으로</summary>
    internal static Bitmap WindowShot(Action<View> setup = null)
    {
        using var f = new PreviewForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false, Width = 1040, Height = 1000 };
        f.Show(); Application.DoEvents();
        setup?.Invoke(f.v); f.phone.Invalidate(); Application.DoEvents();
        var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        f.Close();
        return bmp;
    }

    sealed class PreviewForm : Form
    {
        internal readonly View v = new();
        Strip strip; Spec spec;
        internal readonly PhoneView phone;
        readonly Label status = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 40, ForeColor = Opt.Muted, BackColor = Opt.Bg, Padding = new Padding(10, 4, 10, 4) };
        readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
        readonly Stopwatch sw = new();
        double speed = App.Get("mobileSpeed", 140.0);
        bool whole = App.Get("mobileWhole", true), gaps = App.Get("mobileGaps", false), devicePx;
        int pageGap = App.Get("mobilePageGap", 0);
        CUI.Host host;

        public PreviewForm()
        {
            Text = "웹툰 폰 미리보기"; BackColor = Opt.Bg; ForeColor = Opt.Fg; Font = new Font("Malgun Gothic", 9f);
            Width = 1040; Height = 1000; StartPosition = FormStartPosition.CenterParent; KeyPreview = true;
            v.Dev = Devices.FirstOrDefault(d => d.CssW == App.Get("mobileW", 390)) ?? Devices[1];
            v.Dpr = v.Dev.Dpr; v.MinCss = App.Get("mobileMinCss", 11.0);
            v.Notch = App.Get("mobileNotch", true); v.TextCheck = App.Get("mobileTextCheck", true); v.Cuts = App.Get("mobileCuts", true);
            phone = new PhoneView(this) { Dock = DockStyle.Fill };
            host = new CUI.Host(Items);
            host.Panel.Dock = DockStyle.Left; host.Panel.Width = 300; host.Panel.AutoScroll = true;
            Controls.Add(phone); Controls.Add(host.Panel); Controls.Add(status);
            timer.Tick += (_, _) =>
            {
                double dt = sw.Elapsed.TotalSeconds; sw.Restart();
                if (strip == null) return;
                v.Scroll = Math.Min(MaxScroll(strip, v), v.Scroll + speed * dt);
                if (v.Scroll >= MaxScroll(strip, v)) StopAuto();
                phone.Invalidate();
            };
            Shown += (_, _) => Rebuild();
            FormClosed += (_, _) => { timer.Stop(); strip?.Dispose(); };
        }

        IEnumerable<Control> Items()
        {
            yield return Opt.Title("휴대폰");
            yield return CUI.Seg(Devices.Select(d => (d.CssW.ToString(), d.CssW + "px")).ToArray(), () => v.Dev.CssW.ToString(), x =>
            {
                v.Dev = Devices.First(d => d.CssW.ToString() == x); v.Dpr = v.Dev.Dpr; App.Set("mobileW", v.Dev.CssW);
                Clamp(); host.Rebuild(); UpdStatus(); phone.Invalidate();
            });
            yield return Opt.Help(v.Dev.Name + $" · 화면 {v.Dev.CssW}×{v.Dev.CssH}");
            yield return Opt.Choice("기기 픽셀 배율 (DPR)", new[] { ("1", "1"), ("2", "2"), ("2.625", "2.625"), ("3", "3"), ("3.5", "3.5") }, () => v.Dpr.ToString(System.Globalization.CultureInfo.InvariantCulture),
                x => { v.Dpr = double.Parse(x, System.Globalization.CultureInfo.InvariantCulture); UpdStatus(); phone.Invalidate(); });
            yield return Opt.Check("기기 픽셀 1:1 로 크게 보기 (선명도 확인)", () => devicePx, x => { devicePx = x; phone.Invalidate(); });
            yield return Opt.Title("범위");
            yield return CUI.Seg(new[] { ("page", "지금 페이지"), ("work", "작품 전체") }, () => whole ? "work" : "page", x => { whole = x == "work"; App.Set("mobileWhole", whole); Rebuild(); });
            yield return Opt.Choice("페이지 사이", new[] { ("0", "바로 이어 붙임"), ("40", "40px 띄움"), ("120", "120px 띄움") }, () => pageGap.ToString(), x => { pageGap = int.Parse(x); App.Set("mobilePageGap", pageGap); Rebuild(); });
            yield return Opt.Check("자른 곳에 틈 (장마다 따로 올린 그림처럼)", () => gaps, x => { gaps = x; App.Set("mobileGaps", x); Rebuild(); });
            yield return Opt.Button("다시 만들기 (고친 그림 반영)", Rebuild, true);
            yield return Opt.Title("겹쳐 보기");
            yield return Opt.Check("자른 곳·페이지 이음 표시", () => v.Cuts, x => { v.Cuts = x; App.Set("mobileCuts", x); phone.Invalidate(); });
            yield return Opt.Check("노치·안전 영역", () => v.Notch, x => { v.Notch = x; App.Set("mobileNotch", x); phone.Invalidate(); });
            yield return Opt.Check("글자 크기 확인 (작은 글자 빨갛게)", () => v.TextCheck, x => { v.TextCheck = x; App.Set("mobileTextCheck", x); phone.Invalidate(); });
            yield return Opt.Slider("이보다 작으면 표시 (폰 px)", 6, 20, () => v.MinCss, x => { v.MinCss = Math.Round(x * 2) / 2; App.Set("mobileMinCss", v.MinCss); UpdStatus(); phone.Invalidate(); }, step: 0.5);
            yield return Opt.Title("넘기기");
            yield return Opt.Row(Opt.Button(timer.Enabled ? "■ 멈춤" : "▶ 자동 넘김", ToggleAuto, true), Opt.Button("맨 위로", () => { v.Scroll = 0; phone.Invalidate(); }));
            yield return Opt.Slider("읽는 속도 (폰 px/초)", 30, 500, () => speed, x => { speed = Math.Round(x); App.Set("mobileSpeed", speed); }, step: 5);
            yield return Opt.Help("휠·끌기·↑↓·PgUp/PgDn·스페이스로 넘깁니다. 빨간 상자를 누르면 그 대사로 갑니다 (지금 페이지). 내보내기 규격은 파일 > 웹툰 플랫폼 규격으로 내보내기 의 마지막 값.");
        }

        void ToggleAuto() { if (timer.Enabled) StopAuto(); else { if (strip != null && v.Scroll >= MaxScroll(strip, v)) v.Scroll = 0; sw.Restart(); timer.Start(); host.Rebuild(); } }
        void StopAuto() { timer.Stop(); host.Rebuild(); }
        void Clamp() { if (strip != null) v.Scroll = Math.Clamp(v.Scroll, 0, MaxScroll(strip, v)); }

        public void Rebuild()
        {
            if (App.Doc == null) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                spec = Spec.FromSettings();
                double keep = strip == null ? 0 : v.Scroll / Math.Max(1, strip.CssH(v.Dev.CssW));
                strip?.Dispose();
                var pages = Work.Open(App.Doc, whole);
                strip = Build(pages.Select(p => p.Doc).ToList(), spec, gaps ? 6 : 0, pageGap);
                pageOf = pages;
                v.Scroll = keep * strip.CssH(v.Dev.CssW);
                Clamp(); UpdStatus();
            }
            catch (Exception ex) { status.Text = "만들기 실패: " + ex.Message; }
            finally { Cursor = Cursors.Default; }
            phone.Invalidate();
        }
        List<WorkPage> pageOf;

        void UpdStatus() { if (strip != null) status.Text = Summary(strip, v, spec); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (strip == null) return base.ProcessCmdKey(ref msg, keyData);
            double page = v.Dev.CssH * 0.85;
            double d = keyData switch { Keys.Down => 60, Keys.Up => -60, Keys.PageDown or Keys.Space => page, Keys.PageUp => -page, Keys.End => 1e9, Keys.Home => -1e9, _ => 0 };
            if (d == 0) return base.ProcessCmdKey(ref msg, keyData);
            v.Scroll += d; Clamp(); phone.Invalidate();
            return true;
        }

        internal sealed class PhoneView : Control
        {
            readonly PreviewForm f; Point down; double scroll0; bool dragging;
            public PhoneView(PreviewForm f) { this.f = f; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = Color.FromArgb(32, 32, 36); }
            View V => f.v;
            void Layout1(out PointF origin)
            {
                var v = V;
                if (f.devicePx) { v.Bezel = false; v.Zoom = v.Dpr; }
                else { v.Bezel = true; v.Zoom = 1; var fs = FrameSize(v); if (fs.Height > Height - 16) v.Zoom = Math.Max(0.3, (Height - 16.0) / fs.Height); }
                var s = FrameSize(v);
                origin = new PointF(Math.Max(8, (Width - s.Width) / 2f), f.devicePx ? 0 : Math.Max(8, (Height - s.Height) / 2f));
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                if (f.strip == null) return;
                Layout1(out var o);
                MobilePreview.Paint(e.Graphics, f.strip, V, o);
                if (V.Zoom < 0.999 && !f.devicePx) TextRenderer.DrawText(e.Graphics, $"창에 맞춰 {V.Zoom * 100:0}% 로 줄여 보임", Font, new Point(8, 8), Color.Gray);
            }
            protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (f.strip == null) return; V.Scroll -= e.Delta / 120.0 * 120; f.Clamp(); Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); down = e.Location; scroll0 = V.Scroll; dragging = false; }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (f.strip == null) return;
                if (e.Button == MouseButtons.Left)
                {
                    if (Math.Abs(e.Y - down.Y) > 4) dragging = true;
                    if (dragging) { V.Scroll = scroll0 - (e.Y - down.Y) / V.Zoom; f.Clamp(); Invalidate(); }
                    return;
                }
                int h = HitMark(e.Location);
                if (h != V.Hover) { V.Hover = h; Invalidate(); }
            }
            int HitMark(Point p)
            {
                Layout1(out var o);
                float bez = Bez(V);
                double k = V.Dev.CssW * V.Zoom / f.strip.W, top = o.Y + bez - V.Scroll * V.Zoom;
                for (int i = 0; i < f.strip.Marks.Count; i++)
                {
                    var r = f.strip.Marks[i].R;
                    var sr = new RectangleF(o.X + bez + (float)(r.X * k), (float)(top + r.Y * k), (float)(r.Width * k), (float)(r.Height * k));
                    if (sr.Contains(p)) return i;
                }
                return -1;
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (dragging || f.strip == null || e.Button != MouseButtons.Left) return;
                int i = HitMark(e.Location); if (i < 0) return;
                var m = f.strip.Marks[i];
                var page = f.pageOf != null && m.Page < f.pageOf.Count ? f.pageOf[m.Page] : null;
                if (page?.Current != true || App.Doc?.ById(m.LayerId) == null) { App.Toast($"{m.Page + 1}쪽의 대사입니다 (그 페이지를 열어 고치세요)"); return; }
                App.Doc.ActiveId = m.LayerId; App.NotifyLayers();
                Manuscript.GoTo(m.DocBox.x, m.DocBox.y, m.DocBox.w, m.DocBox.h);
            }
        }
    }
}
