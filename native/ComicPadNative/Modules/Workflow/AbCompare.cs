// A/B 비교: 지금 그림과 예전 판(저장할 때 보관된 data/versions · 작업물 백업 zip · 자동 저장 · 마지막 저장 파일 · 다른 파일)을
// 나란히 / 겹쳐 보기(투명도) / 어니언(A 빨강·B 파랑) / 밀어서 비교(끄는 칸막이) / 달라진 곳(빨강) 으로 본다.
// 합치는 계산(AbRender.Compose)은 같은 크기 그림 두 장 → 결과 그림 (시험에서 픽셀로 확인).
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;
using ComicPadNative.UI.Controls;

namespace ComicPadNative.Modules.Workflow;

/// <summary>비교할 판 하나</summary>
public sealed record VersionSource(string Label, string Kind, Func<Document> Load, DateTime? When = null);

public static class VersionSources
{
    /// <summary>지금 문서의 예전 판들 (새 것부터): 마지막 저장 파일 · 버전 기록 · 자동 저장 · 백업</summary>
    public static List<VersionSource> For(Document doc)
    {
        var list = new List<VersionSource>();
        if (doc == null) return list;
        if (doc.FilePath != null && File.Exists(doc.FilePath) && doc.FilePath.EndsWith(".cpad", StringComparison.OrdinalIgnoreCase))
        {
            string fp = doc.FilePath;
            list.Add(new VersionSource($"마지막 저장 ({File.GetLastWriteTime(fp):M/d HH:mm})", "file", () => CpadIO.Load(fp), File.GetLastWriteTime(fp)));
        }
        string rel = Extras.Story.Rel(doc.FilePath);
        if (rel != null && rel.EndsWith(".cpad", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Path.Combine(App.DataDir, "versions", rel[..^5].Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.cpad").OrderByDescending(x => x, StringComparer.Ordinal))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    DateTime? t = DateTime.TryParseExact(n, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var tt) ? tt : null;
                    string path = f;
                    list.Add(new VersionSource($"버전 기록 {(t is DateTime x ? x.ToString("yyyy-MM-dd HH:mm:ss") : n)}", "version", () => CpadIO.Load(path), t));
                }
        }
        string auto = Path.Combine(App.DataDir, "autosave", SafeFile(doc.Name) + ".cpad");
        if (File.Exists(auto)) list.Add(new VersionSource($"자동 저장 ({File.GetLastWriteTime(auto):M/d HH:mm})", "autosave", () => CpadIO.Load(auto), File.GetLastWriteTime(auto)));
        if (rel != null)
        {
            string bdir = Path.Combine(App.DataDir, "backups");
            if (Directory.Exists(bdir))
                foreach (var z in Directory.GetFiles(bdir, "ComicPad_백업_*.zip").OrderByDescending(x => x).Take(20))
                {
                    try
                    {
                        using var zip = ZipFile.OpenRead(z);
                        var e = zip.GetEntry("projects/" + rel);
                        if (e == null) continue;
                        string zp = z, entry = "projects/" + rel;
                        string label = Path.GetFileNameWithoutExtension(z).Replace("ComicPad_백업_", "");
                        list.Add(new VersionSource($"백업 {label}", "backup", () => FromZip(zp, entry), File.GetLastWriteTime(z)));
                    }
                    catch { }
                }
        }
        return list;
    }
    static string SafeFile(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = (n ?? "").Replace(c, '_'); return n; }

    public static Document FromZip(string zip, string entry)
    {
        using var z = ZipFile.OpenRead(zip);
        var e = z.GetEntry(entry) ?? throw new FileNotFoundException(entry);
        using var s = new StreamReader(e.Open());
        return CpadIO.FromJson(JsonNode.Parse(s.ReadToEnd()) as JsonObject);
    }

    public static Document FromAnyFile(string path) => path.EndsWith(".cpad", StringComparison.OrdinalIgnoreCase) ? CpadIO.Load(path)
        : FileCommands.Openers.TryGetValue(Path.GetExtension(path), out var op) ? op(path) : FileCommands.FromImage(path);
}

public static class AbRender
{
    public static readonly (string id, string label)[] Modes = { ("side", "나란히"), ("overlay", "겹쳐 보기"), ("onion", "어니언"), ("split", "밀어서 비교"), ("diff", "달라진 곳") };

    /// <summary>두 그림을 같은 크기(큰 쪽, 흰 바탕)로</summary>
    public static (Bitmap a, Bitmap b) Normalize(Bitmap a, Bitmap b)
    {
        int W = Math.Max(a.Width, b.Width), H = Math.Max(a.Height, b.Height);
        return (Pad(a, W, H), Pad(b, W, H));
    }
    static Bitmap Pad(Bitmap s, int W, int H)
    {
        var o = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(o);
        g.Clear(Color.White);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        g.DrawImage(s, new Rectangle(0, 0, s.Width, s.Height), 0, 0, s.Width, s.Height, GraphicsUnit.Pixel);
        return o;
    }

    /// <summary>
    /// 합친 그림. a·b 는 같은 크기. t: 겹쳐 보기 = A 의 불투명도(0~1), 밀어서 비교 = 칸막이 위치(0~1, 왼쪽이 A).
    /// 나란히 = 폭 2배 + 간격 gap. 어니언 = A 에만 있는 선은 빨강, B 에만 있는 선은 파랑(청록), 둘 다 = 검정.
    /// 달라진 곳 = 밝기 차이가 threshold 넘는 픽셀 빨강, 나머지는 B 를 흐리게. changed = 바뀐 픽셀 비율
    /// </summary>
    public static unsafe Bitmap Compose(string mode, Bitmap a, Bitmap b, double t, out double changed, int gap = 16, int threshold = 24)
    {
        changed = 0;
        int W = a.Width, H = a.Height;
        if (b.Width != W || b.Height != H) throw new ArgumentException("같은 크기여야 합니다");
        if (ReferenceEquals(a, b)) { using var copy = new Bitmap(b); return Compose(mode, a, copy, t, out changed, gap, threshold); }
        if (mode == "side")
        {
            var o = new Bitmap(W * 2 + gap, H, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(o);
            g.Clear(Color.FromArgb(0x24, 0x24, 0x28));
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(a, new Rectangle(0, 0, W, H), 0, 0, W, H, GraphicsUnit.Pixel);
            g.DrawImage(b, new Rectangle(W + gap, 0, W, H), 0, 0, W, H, GraphicsUnit.Pixel);
            return o;
        }
        var outB = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        var r = new Rectangle(0, 0, W, H);
        var da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dout = outB.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        long nChanged = 0;
        int splitX = (int)Math.Round(Math.Clamp(t, 0, 1) * W);
        int ta = (int)Math.Round(Math.Clamp(t, 0, 1) * 256);
        try
        {
            for (int y = 0; y < H; y++)
            {
                byte* pa = (byte*)da.Scan0 + y * da.Stride, pb = (byte*)db.Scan0 + y * db.Stride, po = (byte*)dout.Scan0 + y * dout.Stride;
                for (int x = 0; x < W; x++, pa += 4, pb += 4, po += 4)
                {
                    switch (mode)
                    {
                        case "split":
                        {
                            byte* s = x < splitX ? pa : pb;
                            po[0] = s[0]; po[1] = s[1]; po[2] = s[2]; po[3] = 255;
                            break;
                        }
                        case "overlay":
                            for (int c = 0; c < 3; c++) po[c] = (byte)((pb[c] * (256 - ta) + pa[c] * ta + 128) >> 8);
                            po[3] = 255;
                            break;
                        case "onion":
                        {
                            int la = (pa[2] * 77 + pa[1] * 150 + pa[0] * 29) >> 8, lb = (pb[2] * 77 + pb[1] * 150 + pb[0] * 29) >> 8;
                            // A 만 어두움 → (255, la, la) 빨강 / B 만 어두움 → (lb, 255, 255) 청록 / 둘 다 → 어두움
                            po[2] = (byte)lb; po[1] = (byte)la; po[0] = (byte)la;
                            po[3] = 255;
                            break;
                        }
                        default:   // diff
                        {
                            int dd = Math.Abs(pa[0] - pb[0]) + Math.Abs(pa[1] - pb[1]) + Math.Abs(pa[2] - pb[2]);
                            if (dd > threshold) { po[2] = 235; po[1] = 40; po[0] = 40; nChanged++; }
                            else { double gg = (pb[0] + pb[1] + pb[2]) / 3.0; byte v = (byte)Math.Round(255 - (255 - gg) * 0.25); po[0] = po[1] = po[2] = v; }
                            po[3] = 255;
                            break;
                        }
                    }
                }
            }
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); outB.UnlockBits(dout); }
        changed = nChanged / (double)Math.Max(1, W * H);
        return outB;
    }

    /// <summary>문서 → 보기용 그림 (긴 쪽 max)</summary>
    public static Bitmap Snapshot(Document d, int max = 1600) => Img.Flatten(d, Math.Min(1, max / (double)Math.Max(d.W, d.H)), true);
}

/// <summary>A/B 비교 창</summary>
public sealed class AbCompareWindow : WfForm
{
    static AbCompareWindow inst;
    readonly WebStack head = new() { Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 4) };
    readonly DoubleBufferedPanel stage = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(0x24, 0x24, 0x28) };
    readonly Label info = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = Theme.Muted, BackColor = Theme.Panel, Padding = new Padding(10, 4, 0, 0), Font = Web.Small };
    List<VersionSource> sources = new();
    public string Mode = "split";
    public double T = 0.5;
    int ia = 0, ib = -1;           // ib = -1 → 지금 그림
    Bitmap a, b, composed; string labelA = "", labelB = "지금";
    double changed;
    float zoom = 0; PointF pan;    // zoom 0 = 맞춤

    public static AbCompareWindow Open(VersionSource preferA = null)
    {
        if (inst == null || inst.IsDisposed) { inst = new AbCompareWindow(); inst.Show(App.Main); }
        else inst.Activate();
        if (preferA != null) inst.UseA(preferA);
        return inst;
    }

    public AbCompareWindow() : base("A/B 비교 — ComicPad", 1100, 820)
    {
        Controls.Add(stage); Controls.Add(head); Controls.Add(info);
        stage.Paint += (_, e) => PaintStage(e.Graphics);
        stage.MouseDown += (_, e) => Drag(e, true);
        stage.MouseMove += (_, e) => Drag(e, false);
        stage.MouseWheel += (_, e) => { float z0 = Zoom(); zoom = Math.Clamp(z0 * (e.Delta > 0 ? 1.2f : 1 / 1.2f), 0.05f, 16); stage.Invalidate(); };
        stage.DoubleClick += (_, _) => { zoom = 0; pan = PointF.Empty; stage.Invalidate(); };
        Reload();
    }

    /// <summary>판 목록 다시 읽고 다시 그리기</summary>
    public void Reload()
    {
        sources = VersionSources.For(App.Doc);
        if (ia >= sources.Count) ia = 0;
        BuildHead();
        Recompute(true);
    }

    void UseA(VersionSource s) { sources.Insert(0, s); ia = 0; BuildHead(); Recompute(true); }

    void BuildHead()
    {
        head.SuspendLayout();
        foreach (Control c in head.Controls.Cast<Control>().ToList()) c.Dispose();
        head.Controls.Clear();
        var opts = sources.Select((s, i) => (i.ToString(), s.Label)).Append(("file", "다른 파일 고르기…")).ToArray();
        var optsB = new[] { ("now", "지금 그림 (저장 안 한 것 포함)") }.Concat(opts.Where(o => o.Item1 != "file")).Append(("file", "다른 파일 고르기…")).ToArray();
        head.Controls.Add(Opt.Row(
            Opt.Choice("A (예전)", opts, () => ia.ToString(), v => { if (v == "file") PickFile(true); else { ia = int.Parse(v); Recompute(true); } }).Flex(),
            Opt.Choice("B", optsB, () => ib < 0 ? "now" : ib.ToString(), v => { if (v == "file") PickFile(false); else { ib = v == "now" ? -1 : int.Parse(v); Recompute(true); } }).Flex()));
        var seg = Opt.Seg(AbRender.Modes, () => Mode, v => { Mode = v; Recompute(false); BuildHead(); });
        head.Controls.Add(seg);
        if (Mode is "overlay" or "split")
            head.Controls.Add(new WebSlider(Mode == "overlay" ? "A 불투명도" : "칸막이 (왼쪽 A)", 0, 1, () => T, v => { T = v; Recompute(false); }, 0.01, false, v => $"{v * 100:0}%"));
        if (sources.Count == 0) head.Controls.Add(Opt.Help("이 그림의 예전 판이 없습니다 (저장할 때마다 data/versions 에 보관됩니다). 다른 파일을 골라 비교할 수 있습니다."));
        head.ResumeLayout();
        head.Height = head.GetPreferredSize(new Size(ClientSize.Width, 0)).Height + 4;
    }

    void PickFile(bool forA)
    {
        using var dlg = new OpenFileDialog { Filter = "ComicPad·이미지|*.cpad;*.png;*.jpg;*.jpeg;*.bmp;*.psd|모든 파일|*.*", InitialDirectory = Extras.Story.ProjectsDir };
        if (dlg.ShowDialog(this) != DialogResult.OK) { BuildHead(); return; }
        string p = dlg.FileName;
        var src = new VersionSource(Path.GetFileName(p), "other", () => VersionSources.FromAnyFile(p));
        sources.Add(src);
        if (forA) ia = sources.Count - 1; else ib = sources.Count - 1;
        BuildHead(); Recompute(true);
    }

    void Recompute(bool reload)
    {
        try
        {
            if (reload || a == null || b == null)
            {
                a?.Dispose(); b?.Dispose(); a = b = null;
                Document da = null, db;
                if (ia >= 0 && ia < sources.Count) { da = sources[ia].Load(); labelA = sources[ia].Label; }
                if (ib >= 0 && ib < sources.Count) { db = sources[ib].Load(); labelB = sources[ib].Label; } else { db = App.Doc; labelB = "지금"; }
                if (db == null) { info.Text = "열린 그림이 없습니다"; return; }
                using var sa = da != null ? AbRender.Snapshot(da) : null;
                using var sb = AbRender.Snapshot(db);
                if (sa == null) { a = new Bitmap(sb); b = new Bitmap(sb); labelA = "(예전 판 없음)"; }
                else (a, b) = AbRender.Normalize(sa, sb);
            }
            composed?.Dispose();
            composed = AbRender.Compose(Mode, a, b, Mode == "overlay" ? T : Mode == "split" ? T : 0.5, out changed);
            info.Text = Mode switch
            {
                "side" => $"왼쪽 A: {labelA} · 오른쪽 B: {labelB}",
                "overlay" => $"B({labelB}) 위에 A({labelA}) 를 {T * 100:0}% 로",
                "onion" => $"빨강 = A({labelA})에만 있는 선 · 청록 = B({labelB})에만 있는 선 · 검정 = 같은 곳",
                "split" => $"왼쪽 A: {labelA} · 오른쪽 B: {labelB} — 그림 위를 끌어 칸막이 옮기기",
                _ => $"빨간 곳이 달라진 부분 (전체의 {changed * 100:0.00}%) — A: {labelA} · B: {labelB}",
            } + " · 휠 = 확대, 두 번 누르기 = 맞춤";
        }
        catch (Exception ex) { info.Text = "비교하지 못했습니다: " + ex.Message; }
        stage.Invalidate();
    }

    float FitZoom() => composed == null ? 1 : Math.Min((stage.Width - 16) / (float)composed.Width, (stage.Height - 16) / (float)composed.Height);
    float Zoom() => zoom > 0 ? zoom : FitZoom();
    RectangleF ImgRect()
    {
        if (composed == null) return RectangleF.Empty;
        float z = Zoom();
        return new RectangleF((stage.Width - composed.Width * z) / 2 + pan.X, (stage.Height - composed.Height * z) / 2 + pan.Y, composed.Width * z, composed.Height * z);
    }

    void PaintStage(Graphics g)
    {
        g.Clear(stage.BackColor);
        if (composed == null) return;
        g.InterpolationMode = Zoom() < 1 ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear : System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        var r = ImgRect();
        g.DrawImage(composed, r);
        void Tag(string t, float x, float y) { var s = Web.Measure(t, Web.Small); var tr = new Rectangle((int)x, (int)y, s.Width + 12, 20); Web.Box(g, tr, 4, Color.FromArgb(200, 20, 20, 24)); Web.Text(g, t, Web.Small, tr, Color.White, Web.Center); }
        if (Mode == "split")
        {
            float x = r.X + r.Width * (float)T;
            using var red = new SolidBrush(Color.FromArgb(0xe5, 0x48, 0x4d));
            g.FillRectangle(red, x - 1.5f, r.Y, 3, r.Height);
            g.FillEllipse(red, x - 9, r.Y + r.Height / 2 - 9, 18, 18);
            Tag("A", r.X + 6, r.Y + 6); Tag("B", r.Right - 30, r.Y + 6);
        }
        else if (Mode == "side") { Tag("A " + labelA, r.X + 6, r.Y + 6); Tag("B " + labelB, r.X + r.Width / 2 + 10, r.Y + 6); }
    }

    Point lastDrag;
    void Drag(MouseEventArgs e, bool isDown)
    {
        if (e.Button == MouseButtons.Left && Mode == "split" && composed != null)
        {
            var r = ImgRect();
            T = Math.Clamp((e.X - r.X) / r.Width, 0, 1);
            Recompute(false);
            foreach (var s in U.All(head).OfType<WebSlider>()) s.Sync();
            return;
        }
        if (e.Button == MouseButtons.None) return;
        // 끌어서 화면 옮기기 (확대했을 때)
        if (!isDown) { pan.X += e.X - lastDrag.X; pan.Y += e.Y - lastDrag.Y; stage.Invalidate(); }
        lastDrag = e.Location;
    }

    protected override bool HandleKey(Keys k)
    {
        switch (k)
        {
            case Keys.D1: Mode = "side"; break;
            case Keys.D2: Mode = "overlay"; break;
            case Keys.D3: Mode = "onion"; break;
            case Keys.D4: Mode = "split"; break;
            case Keys.D5: Mode = "diff"; break;
            case Keys.Escape: Close(); return true;
            default: return false;
        }
        Recompute(false); BuildHead(); return true;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (head != null && IsHandleCreated) { head.Height = head.GetPreferredSize(new Size(ClientSize.Width, 0)).Height + 4; stage?.Invalidate(); } }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        a?.Dispose(); b?.Dispose(); composed?.Dispose();
        if (inst == this) inst = null;
        base.OnFormClosed(e);
    }

    // 시험용
    public Bitmap Composed => composed;
    public void SetSources(List<VersionSource> list, int aIndex, int bIndex) { sources = list; ia = aIndex; ib = bIndex; BuildHead(); Recompute(true); }
    public void SetMode(string m, double t) { Mode = m; T = t; BuildHead(); Recompute(false); }
}
