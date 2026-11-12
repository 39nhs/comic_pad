// 애니 대화상자: 태그 · 타임시트 · 내보내기 · 스프라이트 시트 · 시트 가져오기 · Aseprite 열기 · 미리보기 — 웹 판 ui/anim.js
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Anim;

public static class AnimDialogs
{
    static (string, string)[] TagOptions(Document d)
    {
        var o = new List<(string, string)> { ("", "전체") };
        int i = 0;
        foreach (var t in AnimCore.Tags(d).OfType<JsonObject>()) { o.Add((i.ToString(), $"{J.S(t["name"])} ({J.I(t["from"]) + 1}~{J.I(t["to"]) + 1})")); i++; }
        return o.ToArray();
    }

    // ---- 태그 ----
    public static void Tag(int? i = null)
    {
        var d = App.Doc;
        int n = AnimCore.FrameCount(d), f = AnimCore.Frame(d);
        if (n == 0) { App.Toast("애니메이션 프레임이 없습니다"); return; }
        var t = i is int k ? AnimCore.Tags(d)[k] as JsonObject : null;
        var v = Ui.Form(t != null ? "태그 고치기" : "태그(구간) 만들기", new[]
        {
            Ui.Field.Txt("name", "이름 (예: 걷기, 점프)", J.S(t?["name"]) ?? $"구간 {AnimCore.Tags(d).Count + 1}"),
            Ui.Field.Num("from", "시작 프레임", (t != null ? J.I(t["from"]) : f) + 1, 1, n),
            Ui.Field.Num("to", "끝 프레임", (t != null ? J.I(t["to"]) : Math.Min(n - 1, f + 3)) + 1, 1, n),
            Ui.Field.Seg("dir", "재생 방향", new[] { ("fwd", "정방향 →"), ("rev", "역방향 ←"), ("ping", "핑퐁 ↔") }, J.S(t?["dir"]) ?? "fwd"),
            Ui.Field.Clr("color", "색", J.S(t?["color"]) ?? "#3d7be0"),
        }, t != null ? "고치기" : "만들기");
        if (v == null) return;
        int a = v.I("from"), b = v.I("to");
        var tag = new JsonObject { ["name"] = string.IsNullOrEmpty(v.S("name")) ? "구간" : v.S("name"), ["from"] = Math.Max(0, Math.Min(a, b) - 1), ["to"] = Math.Min(n - 1, Math.Max(a, b) - 1), ["dir"] = v.S("dir"), ["color"] = v.S("color") };
        if (t != null) AnimCore.UpdateTag(i.Value, tag); else AnimCore.AddTag(tag);
    }

    // ---- 타임시트: 프레임(행) × 트랙(열). 칸 = 셀 번호, 빈칸 = 안 보임, "|" = 위 칸 이어 보이기 ----
    public static void Timesheet()
    {
        var d = App.Doc;
        var ts = AnimCore.Tracks(d); ts.Reverse();
        int n = AnimCore.FrameCount(d);
        if (ts.Count == 0) { App.Toast("애니메이션 트랙이 없습니다"); return; }
        var nums = ts.Select(t => d.ChildrenOf(t).Select((c, k) => (c.Id, k + 1)).ToDictionary(p => p.Id, p => (p.Item2).ToString())).ToList();
        using var f = new Form { Text = "타임시트 (노출표)", StartPosition = FormStartPosition.CenterParent, Size = new Size(Math.Min(1100, 260 + ts.Count * 110), 640), BackColor = Ui.Bg, ForeColor = Ui.Fg, Font = new Font("Malgun Gothic", 9f), ShowInTaskbar = false, MinimizeBox = false };
        var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = Ui.Bg, ForeColor = Color.Black, SelectionMode = DataGridViewSelectionMode.CellSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        grid.Columns.Add("f", "프레임"); grid.Columns.Add("ms", "ms");
        grid.Columns[0].ReadOnly = true; grid.Columns[1].ReadOnly = true;
        foreach (var t in ts) grid.Columns.Add(t.Id, t.Name);
        for (int fr = 0; fr < n; fr++)
        {
            var row = new List<object> { (fr + 1).ToString(), Math.Round(AnimCore.FrameMs(fr, d)).ToString() };
            for (int k = 0; k < ts.Count; k++)
            {
                var xs = AnimCore.XsOf(ts[k], d);
                string id = fr < xs.Count ? xs[fr] : null;
                row.Add(id == null ? "" : fr > 0 && xs[fr - 1] == id ? "|" : nums[k].GetValueOrDefault(id, "?"));
            }
            int ri = grid.Rows.Add(row.ToArray());
            if (fr == AnimCore.Frame(d)) grid.Rows[ri].DefaultCellStyle.BackColor = Color.FromArgb(255, 236, 200);
        }
        var help = new Label { Dock = DockStyle.Top, Height = 40, ForeColor = Ui.Muted, Text = "칸에 셀 번호(트랙 안 몇 번째 그림)를 적습니다. \"|\" = 위 칸 그림을 이어 보이기, 빈칸 = 아무것도 안 보임. 같은 번호를 떨어진 칸에 다시 쓰면 같은 그림(연결)입니다.", Padding = new Padding(6) };
        var ok = Ui.Btn("적용", () => { }, true); ok.DialogResult = DialogResult.OK;
        var close = Ui.Btn("닫기", () => { }); close.DialogResult = DialogResult.Cancel;
        var csv = Ui.Btn("CSV 로 저장", () =>
        {
            using var sd = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"{J.SafeName(d.Name)}_타임시트.csv", InitialDirectory = AnimExport.ExportsDir };
            if (sd.ShowDialog(f) != DialogResult.OK) return;
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", new[] { "프레임", "ms" }.Concat(ts.Select(t => t.Name))));
            foreach (DataGridViewRow r in grid.Rows) sb.AppendLine(string.Join(",", r.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString() ?? "")));
            File.WriteAllText(sd.FileName, sb.ToString(), new UTF8Encoding(true));
            App.Toast("타임시트 저장: " + sd.FileName);
        });
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.RightToLeft, BackColor = Ui.Bg };
        bar.Controls.AddRange(new Control[] { ok, close, csv });
        f.Controls.Add(grid); f.Controls.Add(help); f.Controls.Add(bar);
        f.AcceptButton = null; f.CancelButton = close;
        if (f.ShowDialog(App.Main) != DialogResult.OK) return;
        grid.EndEdit();
        AnimCore.Edit("타임시트", tx =>
        {
            for (int k = 0; k < ts.Count; k++)
            {
                var kids = d.ChildrenOf(ts[k]);
                var xs = tx.Xs(ts[k]);
                while (xs.Count < n) xs.Add(null);
                string prev = null;
                for (int fr = 0; fr < n; fr++)
                {
                    string s = (grid.Rows[fr].Cells[k + 2].Value?.ToString() ?? "").Trim();
                    if (s == "|" || s == "-") { xs[fr] = prev; continue; }
                    if (s.Length == 0) { xs[fr] = null; prev = null; continue; }
                    int num = int.TryParse(s, out var q) ? q : 0;
                    var c = num >= 1 && num <= kids.Count ? kids[num - 1] : null;
                    xs[fr] = c != null ? c.Id : xs[fr];
                    prev = xs[fr];
                }
            }
        });
    }

    /// <summary>다른 모듈이 내보내기 창·바로 만들기를 대신할 때 (Media 모듈: WebP·MP4 가 든 창)</summary>
    public static Action<string> ExportOverride;
    public static Action QuickOverride;

    // ---- 내보내기 창: 형식·크기·반복·투명·색 수·디더링, 크기 미리 짐작 ----
    public static void Export(string preset = null)
    {
        if (ExportOverride != null) { ExportOverride(preset); return; }
        var d = App.Doc;
        if (AnimCore.FrameCount(d) < 1) { App.Toast("애니메이션 프레임이 없습니다"); return; }
        var o = LastOpts(d);
        if (preset != null) o.Fmt = preset;
        var tags = AnimCore.Tags(d);
        var fields = new List<Ui.Field>
        {
            Ui.Field.Seg("fmt", "형식", AnimExport.Formats.Select(x => (x.id, x.label.Split(" (")[0])).ToArray(), o.Fmt),
            Ui.Field.Info("GIF · 커뮤니티·메신저 어디나 / APNG · 색 손실 없음·투명 / PNG 연번 · 프레임마다 파일"),
            Ui.Field.Seg("scale", "크기", new[] { ("0.25", "25%"), ("0.5", "50%"), ("1", "100%"), ("2", "200%"), ("4", "400%") }, J.F(o.Scale)),
            tags.Count > 0 ? Ui.Field.Seg("tag", "구간", TagOptions(d), o.Tag?.ToString() ?? "") : null,
            Ui.Field.Seg("loop", "반복 (GIF·APNG)", new[] { ("0", "무한"), ("1", "한 번"), ("2", "2번"), ("3", "3번") }, o.Loop.ToString()),
            Ui.Field.Chk("transparent", "배경 투명 (종이·흰 바탕 없이)", o.Transparent),
            Ui.Field.Seg("colors", "색 수 (GIF, 적을수록 작음)", new[] { ("256", "256"), ("128", "128"), ("64", "64"), ("32", "32"), ("16", "16") }, o.Colors.ToString()),
            Ui.Field.Chk("dither", "디더링 (GIF, 부드러운 색 번짐, 끄면 작아짐)", o.Dither),
        };
        ExportOpts Read(Ui.Values v) => new()
        {
            Fmt = v.S("fmt"), Scale = double.Parse(v.S("scale"), System.Globalization.CultureInfo.InvariantCulture),
            Tag = v.ContainsKey("tag") && int.TryParse(v.S("tag"), out var ti) ? ti : null,
            Loop = int.Parse(v.S("loop")), Transparent = v.B("transparent"), Colors = int.Parse(v.S("colors")), Dither = v.B("dither"),
        };
        var v = Ui.Form("애니메이션 내보내기 (움짤)", fields, "내보내기", (vals, live) =>
        {
            try
            {
                var oo = Read(vals);
                int cnt = AnimCore.ExportOrder(d, oo.Tag).Count;
                string sz = $"{Math.Round(d.W * oo.Scale)}×{Math.Round(d.H * oo.Scale)} · {cnt}장";
                if (oo.Fmt == "png") { live.Text = sz + " · 프레임마다 PNG 파일로 저장합니다"; return; }
                long? b = (long)d.W * d.H <= 4_000_000 ? AnimExport.Estimate(d, oo) : null;   // 큰 원고는 짐작 생략 (느림)
                live.Text = sz + (b is long bb ? $" · 예상 크기: 약 {(bb > 1048576 ? (bb / 1048576.0).ToString("0.0") + "MB" : Math.Round(bb / 1024.0) + "KB")}{(bb > 10 * 1048576 ? " — 큰 편입니다 (크기·색 수를 줄여 보세요)" : "")}" : "");
            }
            catch (Exception ex) { live.Text = "크기를 짐작하지 못했습니다: " + ex.Message; }
        });
        if (v == null) return;
        var opts = Read(v);
        App.Set("animExport", new JsonObject { ["fmt"] = opts.Fmt, ["scale"] = opts.Scale, ["loop"] = opts.Loop, ["transparent"] = opts.Transparent, ["dither"] = opts.Dither, ["colors"] = opts.Colors });
        Ui.Busy("움짤 만들기", () => AnimExport.Export(d, opts));
    }

    static ExportOpts LastOpts(Document d)
    {
        var s = App.Settings["animExport"] as JsonObject;
        return new ExportOpts
        {
            Fmt = J.S(s?["fmt"]) is string f && AnimExport.Formats.Any(x => x.id == f) ? f : "gif",
            Scale = J.N(s?["scale"]) ?? (d.W <= 256 ? 4 : 0.5),
            Loop = J.I(s?["loop"], 0), Transparent = J.T(s?["transparent"]), Dither = J.B(s?["dither"], true), Colors = J.I(s?["colors"], 256),
            Tag = AnimCore.PlayTag(d),
        };
    }

    /// <summary>움짤 만들기: 묻지 않고 지난 설정(처음엔 GIF 50% 무한 반복)으로 바로</summary>
    public static void QuickGif()
    {
        if (QuickOverride != null) { QuickOverride(); return; }
        var d = App.Doc;
        if (AnimCore.FrameCount(d) < 2) { App.Toast("프레임이 2장 이상 있어야 움짤이 됩니다"); return; }
        var o = LastOpts(d);
        if (o.Fmt != "gif" && o.Fmt != "apng") o.Fmt = "gif";
        Ui.Busy("움짤 만들기", () => AnimExport.Export(d, o));
    }

    // ---- 스프라이트 시트 ----
    public static void Sheet()
    {
        var d = App.Doc;
        if (AnimCore.FrameCount(d) < 1) { App.Toast("애니메이션 프레임이 없습니다"); return; }
        var v = Ui.Form("스프라이트 시트 내보내기", new[]
        {
            Ui.Field.Seg("layout", "배치", new[] { ("grid", "격자"), ("h", "가로 한 줄"), ("v", "세로 한 줄") }, "grid"),
            Ui.Field.Num("cols", "격자 칸 수 (가로, 0 = 자동)", 0, 0, 64),
            Ui.Field.Num("scale", "크기 (배)", d.W <= 256 ? 1 : 0.5, 0.1, 8, 0.25),
            Ui.Field.Num("pad", "칸 사이 여백 (px)", 0, 0, 32),
            Ui.Field.Chk("trim", "투명한 가장자리 잘라내기 (JSON 에 원래 위치 기록)", false),
            Ui.Field.Chk("bg", "흰 바탕 (끄면 투명)", false),
            Ui.Field.Chk("camera", "카메라 적용", false),
            Ui.Field.Seg("json", "JSON 형식", new[] { ("hash", "Hash (Aseprite 기본)"), ("array", "Array") }, "hash"),
            AnimCore.Tags(d).Count > 0 ? Ui.Field.Seg("tag", "구간", TagOptions(d), "") : null,
            Ui.Field.Info("게임 엔진(Unity·Godot·Phaser 등)에서 쓰는 Aseprite 형식 JSON(프레임 위치·시간·태그)을 함께 저장합니다."),
        }, "내보내기");
        if (v == null) return;
        var o = new SheetOpts { Layout = v.S("layout"), Cols = v.I("cols"), Scale = v.N("scale"), Pad = v.I("pad"), Trim = v.B("trim"), Bg = v.B("bg") ? 0xFFFFFFFF : null, Camera = v.B("camera"), Json = v.S("json"), Tag = v.ContainsKey("tag") && int.TryParse(v.S("tag"), out var ti) ? ti : null };
        Ui.Busy("스프라이트 시트", () => Sprite.Export(d, o));
    }

    public static void ImportSheet()
    {
        using var od = new OpenFileDialog { Filter = "그림|*.png;*.gif;*.bmp;*.jpg;*.jpeg|모든 파일|*.*" };
        if (od.ShowDialog(App.Main) != DialogResult.OK) return;
        Img img;
        using (var bmp = new Bitmap(od.FileName)) img = Img.FromBitmap(bmp);
        int sq = Math.Min(img.W, img.H);
        var v = Ui.Form("스프라이트 시트 가져오기", new[]
        {
            Ui.Field.Info($"{Path.GetFileName(od.FileName)} · {img.W}×{img.H}"),
            Ui.Field.Seg("how", "자르는 방법", new[] { ("grid", "칸 수로"), ("size", "칸 크기로"), ("json", "JSON 파일로") }, "grid"),
            Ui.Field.Num("cols", "가로 칸 수", Math.Max(1, Math.Round(img.W / (double)sq)), 1, 1000),
            Ui.Field.Num("rows", "세로 칸 수", 1, 1, 1000),
            Ui.Field.Num("fw", "칸 너비 (칸 크기로)", sq, 1, 10000),
            Ui.Field.Num("fh", "칸 높이 (칸 크기로)", sq, 1, 10000),
            Ui.Field.Num("pad", "칸 사이 여백", 0, 0, 1000),
            Ui.Field.Chk("skip", "완전히 빈 칸은 빼기", true),
            Ui.Field.Seg("into", "넣을 곳", new[] { ("new", "새 작품"), ("here", "지금 작품의 새 트랙") }, "new"),
        }, "가져오기");
        if (v == null) return;
        List<SheetFrame> frames; JsonArray tags;
        int pad = v.I("pad");
        if (v.S("how") == "json")
        {
            using var jd = new OpenFileDialog { Filter = "JSON|*.json", InitialDirectory = Path.GetDirectoryName(od.FileName) };
            if (jd.ShowDialog(App.Main) != DialogResult.OK) return;
            (frames, tags) = Sprite.Slice(img, JsonNode.Parse(File.ReadAllText(jd.FileName)) as JsonObject);
        }
        else if (v.S("how") == "size")
        {
            int fw = v.I("fw"), fh = v.I("fh");
            (frames, tags) = Sprite.Slice(img, null, (img.W + pad) / (fw + pad), (img.H + pad) / (fh + pad), pad, 0, fw, fh, v.B("skip"));
        }
        else (frames, tags) = Sprite.Slice(img, null, v.I("cols"), v.I("rows"), pad, 0, 0, 0, v.B("skip"));
        if (frames.Count == 0) { App.Toast("잘라 낸 칸이 없습니다"); return; }
        string name = Path.GetFileNameWithoutExtension(od.FileName);
        if (v.S("into") == "here" && App.Doc != null) Sprite.FramesToDoc(frames, name, tags, App.Doc);
        else { if (!FileCommands.ConfirmDiscard()) return; var nd = Sprite.FramesToDoc(frames, name, tags); App.SetDoc(nd); nd.Modified = true; }
        App.Toast($"{frames.Count}장을 프레임으로 가져왔습니다");
    }

    public static void ImportAseprite()
    {
        if (!FileCommands.ConfirmDiscard()) return;
        using var od = new OpenFileDialog { Filter = "Aseprite (*.aseprite;*.ase)|*.aseprite;*.ase" };
        if (od.ShowDialog(App.Main) != DialogResult.OK) return;
        FileCommands.OpenPath(od.FileName);
    }

    // ---- 미리보기 (카메라·소리·구간 적용) ----
    public static void Preview()
    {
        var d = App.Doc;
        int n = AnimCore.FrameCount(d);
        if (n == 0) return;
        double s = Math.Min(1, 720.0 / Math.Max(d.W, d.H));
        var order = AnimCore.PlayOrder(d);
        var times = AnimCore.Timeline(d);
        var frames = new Dictionary<int, Bitmap>();
        var f = new Form { Text = "미리보기 (카메라·소리·구간 적용)", StartPosition = FormStartPosition.CenterParent, BackColor = Color.FromArgb(30, 30, 34), ClientSize = new Size(Math.Max(200, (int)(d.W * s)), Math.Max(150, (int)(d.H * s))), ShowInTaskbar = false };
        var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
        f.Controls.Add(pb);
        int k = 0; long last = Environment.TickCount64;
        Bitmap Frame(int fr) { if (!frames.TryGetValue(fr, out var b)) frames[fr] = b = AnimExport.Frame(d, fr, s).ToBitmap(); return b; }
        var timer = new System.Windows.Forms.Timer { Interval = 5 };
        timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            if (now - last >= AnimCore.FrameMs(order[k], d)) { last = now; k = (k + 1) % order.Count; if (k == 0) Sound.Play(d, times[order[0]]); pb.Image = Frame(order[k]); }
        };
        f.Shown += (_, _) => { pb.Image = Frame(order[0]); Sound.Play(d, times[order[0]]); last = Environment.TickCount64; timer.Start(); };
        f.FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); Sound.Stop(); pb.Image = null; foreach (var b in frames.Values) b.Dispose(); f.Dispose(); };
        f.Show(App.Main);
    }
}
