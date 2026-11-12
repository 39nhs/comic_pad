// 내장 AI 기능 (웹 판 app/js/ai/features.js·aiselect.js·license.js 와 같은 동작):
// 선화 추출 · 캐릭터/배경 따기 · 깊이 · 크기 키우기 · AI 고르기(탭) · AI 모델 관리.
// 비상업 모델은 쓸 때 확인 + 작품에 기록(doc.Extra["aiLog"]) + 결과 레이어 이름에 "(비상업 AI)".
using System.Drawing;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.AI;

public static class AiFeatures
{
    static Document D => App.Doc;
    static Rect Region() => D.Sel.Active ? D.Sel.Bounds() : new Rect(0, 0, D.W, D.H);

    // ---- 라이선스 --------------------------------------------------------------------
    static readonly HashSet<string> warned = new();
    public static bool LicenseGate(AiModel m)
    {
        if (m.Commercial == "yes") return true;
        if (m.Commercial == "check")
        {
            if (warned.Add(m.Id)) App.Toast($"{m.Name}: {m.Note ?? "학습된 모델 파일의 라이선스 표기가 따로 없습니다."} 상업용으로 쓰기 전에 출처를 확인하세요.");
            return true;
        }
        var log = D.Extra["aiLog"] as JsonArray;
        if (log?.Any(x => (string)x?["id"] == m.Id && (bool?)x?["nc"] == true) == true) return true;
        var r = MessageBox.Show(App.Main, $"\"{m.Name}\" 은(는) 비상업용 라이선스({m.License})입니다.\n\n판매·연재·외주·광고·후원 수익·회사 업무처럼 돈을 벌거나 상업적으로 쓰는 작품에는 쓸 수 없습니다. 개인 공부·취미·연구용으로만 쓰세요.\n\n이 작품에는 \"비상업 AI 사용\" 기록이 남고, 결과 레이어 이름에 (비상업 AI)가 붙습니다.\n\n이 작품은 상업용이 아닙니다 (개인 공부·취미·연구) — 계속할까요?", "⚠ 비상업용 AI 모델", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r != DialogResult.Yes) return false;
        if (log == null) { log = new JsonArray(); D.Extra["aiLog"] = log; }
        log.Add(new JsonObject { ["id"] = m.Id, ["name"] = m.Name, ["license"] = m.License, ["nc"] = true, ["at"] = DateTime.UtcNow.ToString("o") });
        D.Modified = true;
        return true;
    }
    static string NcName(string b, AiModel m) => m.Commercial == "no" ? b + " (비상업 AI)" : b;

    // ---- 모델 고르기 대화상자 -----------------------------------------------------------
    static (AiModel m, Dictionary<string, string> v)? Choose(string task, string title, params (string key, string label, (string id, string text)[] opts, string def)[] extra)
    {
        var list = AiRuntime.Installed(task);
        if (list.Count == 0) { App.Toast("이 기능에 쓸 AI 모델이 아직 없습니다. AI > AI 모델 관리에서 받으세요"); ModelManager(); return null; }
        using var f = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedToolWindow, BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        UI.Dialogs.Modals.Adopt(f);
        var p = Opt.Panel();
        string last = (string)(App.Settings["aiPick"] as JsonObject)?[task];
        string model = list.Any(x => x.Id == last) ? last : list[0].Id;
        p.Controls.Add(Opt.Choice("모델", list.Select(x => (x.Id, $"{x.Name} · {x.Badge}")).ToArray(), () => model, v => model = v));
        var vals = new Dictionary<string, string>();
        foreach (var (key, label, opts, def) in extra)
        {
            vals[key] = def;
            if (opts == null) { p.Controls.Add(Opt.Help(label)); continue; }
            var k = key;
            p.Controls.Add(Opt.Choice(label, opts, () => vals[k], v => vals[k] = v));
        }
        bool ok = false;
        p.Controls.Add(Opt.Row(Opt.Button("실행", () => { ok = true; f.Close(); }, true), Opt.Button("취소", f.Close)));
        f.Controls.Add(p);
        f.ShowDialog(App.Main);
        if (!ok) return null;
        if (App.Settings["aiPick"] is not JsonObject ap) { ap = new JsonObject(); App.Settings["aiPick"] = ap; }
        ap[task] = model; App.SaveSettingsSoon();
        var m = list.First(x => x.Id == model);
        if (!LicenseGate(m)) return null;
        if (m.Slow != null) App.Toast($"{m.Name}: {m.Slow}");
        return (m, vals);
    }
    static readonly (string, string)[] SrcOpts = { ("all", "보이는 그림 전체"), ("layer", "지금 레이어") };

    // ---- 원본·결과 -------------------------------------------------------------------
    /// <summary>흰 바탕에 합친 원본 (선택 영역이 있으면 그 부분만)</summary>
    static Bitmap Source(string src, Rect r, bool transparent = false)
    {
        uint[] buf;
        if (src == "layer" && D.Active?.Px != null) { buf = D.Active.Px.Read(r); if (!transparent) for (int i = 0; i < buf.Length; i++) buf[i] = Px.Over(buf[i], 0xFFFFFFFF); }
        else buf = App.Comp.Render(D, r, new RenderOptions { Paper = !transparent, ExcludeDraft = true });
        if (!transparent) for (int i = 0; i < buf.Length; i++) buf[i] = Px.Over(buf[i], 0xFFFFFFFF);
        var s = new Surface(r.W, r.H); s.Write(new Rect(0, 0, r.W, r.H), buf);
        return CpadIO.ToBitmap(s, s.Full);
    }

    /// <summary>결과를 새 레이어로 (선택 영역이 있으면 그 안만)</summary>
    static void ResultLayer(string name, Bitmap img, Rect r, AiModel m)
    {
        var d = D;
        App.History.Struct(d, name, () =>
        {
            var l = new Layer(d, NcName(name, m));
            var a = d.Active;
            l.Parent = a?.Parent;
            d.Layers.Insert(a == null ? d.Layers.Count : d.Layers.IndexOf(a) + 1, l);
            d.ActiveId = l.Id;
            var tmp = new Surface(d.W, d.H);
            CpadIO.DrawBitmap(tmp, img, r.X, r.Y);
            if (d.Sel.Active) { var buf = tmp.Read(r); for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++) buf[y * r.W + x] = Px.Scale(buf[y * r.W + x], d.Sel.At(r.X + x, r.Y + y)); tmp.Write(r, buf); }
            l.Px = tmp;
        });
        App.NotifyLayers(); App.Invalidate(r);
    }

    /// <summary>오래 걸리는 일을 뒤에서, 진행률 창과 함께</summary>
    static T Busy<T>(string label, Func<IProgress<double>, CancellationToken, T> work)
    {
        using var f = new Form { Text = label, Width = 380, Height = 130, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedToolWindow, ControlBox = false, BackColor = Opt.Bg, ForeColor = Opt.Fg };
        UI.Dialogs.Modals.Adopt(f);
        var lab = new Label { Text = label + " 중… (이 PC 안에서)", Dock = DockStyle.Top, Height = 30, Padding = new Padding(8) };
        var bar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Maximum = 1000 };
        var cts = new CancellationTokenSource();
        var cancel = new Button { Text = "취소", Dock = DockStyle.Bottom, Height = 30 };
        cancel.Click += (_, _) => cts.Cancel();
        f.Controls.Add(cancel); f.Controls.Add(bar); f.Controls.Add(lab);
        T result = default; Exception err = null;
        var prog = new Progress<double>(v => { if (!bar.IsDisposed) bar.Value = (int)Math.Clamp(v * 1000, 0, 1000); });
        f.Shown += async (_, _) =>
        {
            try { result = await Task.Run(() => work(prog, cts.Token)); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { err = ex; }
            f.Close();
        };
        f.ShowDialog(App.Main);
        if (err != null) App.Toast($"{label}: {err.Message}");
        else if (AiRuntime.LastEp != "") App.Toast($"{label}: {AiRuntime.LastEp} 로 처리");
        return result;
    }

    static uint Gray(byte v) => 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | v;
    static byte Cl(float v) => (byte)Math.Clamp((int)v, 0, 255);

    /// <summary>흑백(흰 바탕) → 투명 바탕 선 (어두운 만큼 진하게)</summary>
    static Bitmap GrayToLines(uint[] px, int w, int h, uint color)
    {
        uint c = Px.Unpremul(color);
        for (int i = 0; i < px.Length; i++)
        {
            uint v = px[i]; float lum = ((v >> 16) & 255) * 0.299f + ((v >> 8) & 255) * 0.587f + (v & 255) * 0.114f;
            uint a = (uint)Math.Clamp((int)Math.Round((250 - lum) * 1.2), 0, 255);
            px[i] = (a << 24) | (c & 0xFFFFFF);
        }
        return AiRuntime.FromArgb(px, w, h);
    }

    // ---- 선화 추출 ---------------------------------------------------------------------
    public static void Lineart()
    {
        var pick = Choose("lineart", "AI 선화 추출", ("src", "바탕으로 쓸 것", SrcOpts, "all"), ("color", "선 색", new[] { ("black", "검정"), ("fg", "지금 전경색") }, "black"),
            ("i", $"결과는 \"AI 선화\" 새 레이어(흰 바탕 없이 선만)로 들어갑니다{(D.Sel.Active ? " (선택 영역 부분만)" : "")}.", null, ""));
        if (pick == null) return;
        var (m, v) = pick.Value; var r = Region();
        using var src = Source(v["src"], r);
        var px = AiRuntime.ReadArgb(src);
        var outPx = Busy("AI 선화 추출", (prog, ct) => LineartPx(m, px, r.W, r.H, prog, ct));
        if (outPx == null) return;
        using var lines = GrayToLines(outPx, r.W, r.H, v["color"] == "fg" ? App.Fg : 0xFF000000);
        ResultLayer("AI 선화", lines, r, m);
        App.Toast("선화를 새 레이어로 넣었습니다");
    }

    static uint[] LineartPx(AiModel m, uint[] px, int W, int H, IProgress<double> prog, CancellationToken ct)
    {
        var s = AiRuntime.Session(m, m.Files[0].name);
        if (m.Id == "line-manga")
            return AiRuntime.RunTiled(s, px, W, H, 512, 32, 1, 1, 16,
                (c, b, i, n) => b[i] = ((c >> 16) & 255) * 0.299f + ((c >> 8) & 255) * 0.587f + (c & 255) * 0.114f,
                (od, q, on) => Gray(Cl(od[q])), prog, ct);
        if (m.Id == "line-informative")
            return AiRuntime.RunTiled(s, px, W, H, 512, 32, 1, 3, 8,
                (c, b, i, n) => { b[i] = ((c >> 16) & 255) / 255f; b[n + i] = ((c >> 8) & 255) / 255f; b[2 * n + i] = (c & 255) / 255f; },
                (od, q, on) => Gray(Cl(od[q] * 255)), prog, ct);
        // Anime2Sketch: 그림 전체를 한 번에 (256 의 배수, 긴 쪽 최대 1024)
        double sc = Math.Min(1, 1024.0 / Math.Max(W, H));
        int w2 = Math.Max(256, (int)Math.Round(W * sc / 256) * 256), h2 = Math.Max(256, (int)Math.Round(H * sc / 256) * 256);
        using var src = AiRuntime.FromArgb(px, W, H);
        var (data, _) = AiRuntime.ToCHW(src, w2, h2, new[] { .5f, .5f, .5f }, new[] { .5f, .5f, .5f });
        var od2 = AiRuntime.Run1(s, s.InputMetadata.Keys.First(), data, new[] { 1, 3, h2, w2 }, out _);
        var small = new uint[w2 * h2];
        for (int i = 0; i < small.Length; i++) small[i] = Gray(Cl((od2[i] + 1) * 127.5f));
        using var sb = AiRuntime.FromArgb(small, w2, h2);
        using var back = AiRuntime.Resize(sb, W, H);
        return AiRuntime.ReadArgb(back);
    }

    // ---- 캐릭터·배경 따기 ----------------------------------------------------------------
    public static void Cutout()
    {
        var pick = Choose("cutout", "AI 캐릭터·배경 따기", ("src", "바탕으로 쓸 것", SrcOpts, "all"), ("out", "결과", new[] { ("sel", "선택 영역으로"), ("layer", "오려 낸 새 레이어") }, "sel"));
        if (pick == null) return;
        var (m, v) = pick.Value; var r = Region();
        using var src = Source(v["src"], r);
        var mask = Busy("AI 따기", (prog, ct) => CutoutMask(m, src));
        if (mask == null) return;
        if (v["out"] == "sel")
        {
            var full = new byte[D.W * D.H];
            for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++) full[(r.Y + y) * D.W + r.X + x] = mask[y * r.W + x];
            App.History.SelectionChange(D, "AI 따기", () => D.Sel.Apply(full, "replace"));
            SelectCommands.RedrawView();
            App.Toast("선택 영역으로 만들었습니다 (가장자리는 부드럽게)");
        }
        else
        {
            using var t = Source(v["src"], r, transparent: true);
            var px = AiRuntime.ReadArgb(t);
            for (int i = 0; i < px.Length; i++) { uint a = (px[i] >> 24) * mask[i] / 255; px[i] = (a << 24) | (px[i] & 0xFFFFFF); }
            using var cut = AiRuntime.FromArgb(px, r.W, r.H);
            ResultLayer("AI 따기", cut, r, m);
            App.Toast("오려 낸 그림을 새 레이어로 넣었습니다");
        }
    }

    static byte[] CutoutMask(AiModel m, Bitmap src)
    {
        var s = AiRuntime.Session(m, m.Files[0].name);
        float[] inp; RectangleF? box = null; bool sig = false, mm = false;
        if (m.Id == "seg-anime") { var (d, b) = AiRuntime.ToCHW(src, 1024, 1024, contain: true, bg: Color.Black); inp = d; box = b; }
        else if (m.Id == "seg-birefnet") { inp = AiRuntime.ToCHW(src, 1024, 1024, AiRuntime.ImMean, AiRuntime.ImStd).data; sig = true; }
        else { inp = AiRuntime.ToCHW(src, 1024, 1024, new[] { .5f, .5f, .5f }, new[] { 1f, 1f, 1f }).data; mm = true; }
        var od = AiRuntime.Run1(s, s.InputMetadata.Keys.First(), inp, new[] { 1, 3, 1024, 1024 }, out _);
        return AiRuntime.MaskToSize(od, 0, 1024, 1024, box, src.Width, src.Height, sig, mm);
    }

    // ---- 깊이 (밝을수록 가까움) --------------------------------------------------------------
    public static void Depth()
    {
        var pick = Choose("depth", "AI 깊이 추정", ("src", "바탕으로 쓸 것", SrcOpts, "all"), ("res", "정밀도", new[] { ("518", "보통"), ("770", "자세히"), ("1022", "아주 자세히 (느림)") }, "518"),
            ("i", "밝을수록 가까운 곳입니다. 명암·공기 원근·3D 배경 참고용으로 쓰세요 (\"AI 깊이\" 새 레이어).", null, ""));
        if (pick == null) return;
        var (m, v) = pick.Value; var r = Region();
        using var src = Source(v["src"], r);
        int res = int.Parse(v["res"]);
        var mask = Busy("AI 깊이 추정", (prog, ct) =>
        {
            var s = AiRuntime.Session(m, m.Files[0].name);
            double sc = (double)res / Math.Max(src.Width, src.Height);
            int W = Math.Max(14 * 8, (int)Math.Round(src.Width * sc / 14) * 14), H = Math.Max(14 * 8, (int)Math.Round(src.Height * sc / 14) * 14);
            var (data, _) = AiRuntime.ToCHW(src, W, H, AiRuntime.ImMean, AiRuntime.ImStd);
            var od = AiRuntime.Run1(s, s.InputMetadata.Keys.First(), data, new[] { 1, 3, H, W }, out var dims);
            return AiRuntime.MaskToSize(od, 0, dims[^1], dims[^2], null, src.Width, src.Height, minmax: true);
        });
        if (mask == null) return;
        var px = new uint[mask.Length];
        for (int i = 0; i < px.Length; i++) px[i] = Gray(mask[i]);
        using var img = AiRuntime.FromArgb(px, r.W, r.H);
        ResultLayer("AI 깊이", img, r, m);
        App.Toast("깊이 그림을 새 레이어로 넣었습니다 (밝을수록 가까움)");
    }

    // ---- 크기 키우기 → 새 작품 ----------------------------------------------------------------
    public static void Upscale()
    {
        var r = Region();
        var pick = Choose("upscale", "AI 크기 키우기", ("scale", "배율", new[] { ("2", "2배"), ("4", "4배") }, "2"),
            ("i", $"보이는 그림{(D.Sel.Active ? "(선택 영역)" : "")}을 키워 새 작품으로 엽니다. 지금 작품은 그대로입니다.", null, ""));
        if (pick == null) return;
        var (m, v) = pick.Value; int k = int.Parse(v["scale"]);
        if ((long)r.W * k * r.H * k > 80_000_000) { App.Toast($"결과가 너무 큽니다 ({r.W * k}×{r.H * k}). 2배로 하거나 일부만 선택하세요"); return; }
        using var src = Source("all", r);
        var px = AiRuntime.ReadArgb(src);
        var big = Busy("AI 크기 키우기", (prog, ct) =>
        {
            var s = AiRuntime.Session(m, m.Files[0].name);
            return AiRuntime.RunTiled(s, px, r.W, r.H, m.Id == "up-anime6b" ? 128 : 192, 12, 4, 3, 1,
                (c, b, i, n) => { b[i] = ((c >> 16) & 255) / 255f; b[n + i] = ((c >> 8) & 255) / 255f; b[2 * n + i] = (c & 255) / 255f; },
                (od, q, on) => 0xFF000000u | ((uint)Cl(od[q] * 255) << 16) | ((uint)Cl(od[on + q] * 255) << 8) | Cl(od[2 * on + q] * 255), prog, ct);
        });
        if (big == null) return;
        using var b4 = AiRuntime.FromArgb(big, r.W * 4, r.H * 4);
        using var outBmp = k == 4 ? (Bitmap)b4.Clone() : AiRuntime.Resize(b4, r.W * k, r.H * k);
        var d = new Document(outBmp.Width, outBmp.Height) { Name = $"{D.Name} ×{k}", Dpi = D.Dpi * k };
        var l = new Layer(d, NcName("키운 그림", m));
        CpadIO.DrawBitmap(l.Px, outBmp, 0, 0);
        d.Layers.Add(l); d.ActiveId = l.Id;
        if (D.Extra["aiLog"] is JsonArray log) d.Extra["aiLog"] = log.DeepClone();
        if (!FileCommands.ConfirmDiscard()) return;
        App.SetDoc(d); d.Modified = true;
        App.Toast($"{d.W}×{d.H} 새 작품으로 열었습니다");
    }

    // ---- AI 모델 관리 -----------------------------------------------------------------------
    public static void ModelManager()
    {
        using var f = new Form { Text = "AI 모델 관리", Width = 760, Height = 560, StartPosition = FormStartPosition.CenterParent, BackColor = Opt.Bg, ForeColor = Opt.Fg, FormBorderStyle = FormBorderStyle.SizableToolWindow };
        UI.Dialogs.Modals.Adopt(f);
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Opt.Bg, ForeColor = Opt.Fg, ShowItemToolTips = true };
        list.Columns.Add("모델", 260); list.Columns.Add("기능", 70); list.Columns.Add("라이선스", 130); list.Columns.Add("크기", 80); list.Columns.Add("상태", 180);
        string TaskName(string t) => t switch { "lineart" => "선화", "cutout" => "따기", "depth" => "깊이", "upscale" => "키우기", "select" => "고르기", "pose" => "포즈", "hand" => "손", "face" => "얼굴", _ => t };
        void Fill()
        {
            list.Items.Clear();
            foreach (var m in AiRuntime.Registry())
            {
                AiRuntime.CopyBundled(m);
                string st = m.Installed ? "✔ 있음" : m.Manual ? "직접 받기 (약관)" : m.Convert ? "웹 판에서 받기 (변환 필요)" : m.Files.Any(x => x.name.EndsWith(".task")) ? "웹 판 전용 (MediaPipe)" : "받기 가능";
                var it = new ListViewItem(new[] { m.Name, TaskName(m.Task), $"{m.Badge} ({m.License})", $"{m.Files.Sum(x => x.size) / 1e6:0}MB", st }) { Tag = m, ToolTipText = m.License + (m.Note != null ? " — " + m.Note : "") + (m.Source != null ? " · 출처: " + m.Source : "") };
                list.Items.Add(it);
            }
        }
        var bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 14, Maximum = 1000 };
        var row = Opt.Row(
            Opt.Button("받기", async () =>
            {
                if (list.SelectedItems.Count == 0) return;
                var m = (AiModel)list.SelectedItems[0].Tag;
                if (m.Installed) { App.Toast("이미 있습니다"); return; }
                if (m.Commercial == "no" && MessageBox.Show(f, $"{m.Name} 은(는) 비상업용입니다. 받을까요?", "AI 모델", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                try { await AiRuntime.Download(m, new Progress<double>(v => bar.Value = (int)(v * 1000)), CancellationToken.None); App.Toast($"{m.Name} 받음"); }
                catch (Exception ex) { App.Toast(ex.Message); }
                Fill();
            }, true),
            Opt.Button("모델 폴더 열기", () => Shell.OpenFolder(Path.Combine(App.DataDir, "models"))),
            Opt.Button("출처·라이선스 보기", () =>
            {
                if (list.SelectedItems.Count == 0) return;
                var m = (AiModel)list.SelectedItems[0].Tag;
                if (m.Source?.StartsWith("https://") != true) { App.Toast(m.License); return; }
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(m.Source) { UseShellExecute = true }); } catch { App.Toast(m.Source); }
            }),
            Opt.Check("그래픽 가속 끄기 (CPU 만)", () => App.Get("aiCpuOnly", false), v => { App.Set("aiCpuOnly", v); AiRuntime.CloseAll(); }));
        row.Dock = DockStyle.Top;
        f.Controls.Add(list); f.Controls.Add(bar); f.Controls.Add(row);
        f.Controls.Add(new Label { Text = "모델 파일은 웹 판과 같은 data\\models 폴더를 씁니다. 받은 파일은 목록에 적힌 SHA-256 과 같아야 씁니다.", Dock = DockStyle.Top, Height = 34, ForeColor = Opt.Muted, Padding = new Padding(6) });
        Fill();
        f.ShowDialog(App.Main);
    }
}

/// <summary>AI 고르기 도구: 그림을 한 번 읽어 두고 탭한 곳의 물체를 선택 영역으로 (MobileSAM·SlimSAM)</summary>
public sealed class AiSelectTool : ToolBase
{
    public override string Id => "aiselect";
    public override string Name => "AI 고르기 (탭)";
    public override string Icon => "✦";
    readonly List<(double x, double y, int l)> pts = new();
    AiModel model; Document embDoc; float[] emb, pos; int[] embDims, posDims; Rectangle box; double scale;
    bool reading;

    public override void Activate() { pts.Clear(); if (emb == null || embDoc != App.Doc) Read(); }
    public override void Deactivate() { pts.Clear(); }

    async void Read()
    {
        if (reading) return;
        var list = AiRuntime.Installed("select");
        if (list.Count == 0) { App.Toast("AI 고르기 모델이 없습니다. AI > AI 모델 관리에서 받으세요"); return; }
        model = list[0];
        if (!AiFeatures.LicenseGate(model)) return;
        reading = true;
        App.Toast("그림 읽는 중 (AI 고르기)…");
        var d = App.Doc;
        var buf = App.Comp.Render(d, new Rect(0, 0, d.W, d.H), new RenderOptions { ExcludeDraft = true });
        var s0 = new Surface(d.W, d.H); s0.Write(s0.Full, buf.Select(c => Px.Over(c, 0xFFFFFFFF)).ToArray());
        using var bmp = CpadIO.ToBitmap(s0, s0.Full);
        try
        {
            await Task.Run(() =>
            {
                var (data, b) = AiRuntime.ToCHW(bmp, 1024, 1024, AiRuntime.ImMean, AiRuntime.ImStd, contain: true, alignStart: true, padZero: true);
                var s = AiRuntime.Session(model, "vision_encoder.onnx");
                using var r = s.Run(new[] { NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(data, new[] { 1, 3, 1024, 1024 })) });
                var e = r.First(x => x.Name == "image_embeddings").AsTensor<float>(); var p = r.First(x => x.Name == "image_positional_embeddings").AsTensor<float>();
                emb = e.ToArray(); embDims = e.Dimensions.ToArray(); pos = p.ToArray(); posDims = p.Dimensions.ToArray();
                box = b; scale = b.Width / (double)d.W;
            });
            embDoc = d;
            App.Toast($"그림을 읽었습니다 ({AiRuntime.LastEp}). 고를 물체를 탭하세요 (Shift+탭 = 더하기, Alt+탭 = 빼기)");
        }
        catch (Exception ex) { App.Toast("AI 고르기: " + ex.Message); }
        finally { reading = false; }
    }

    public override void Down(PointerArgs e)
    {
        if (emb == null || embDoc != App.Doc) { if (!reading) Read(); App.Toast("그림을 읽는 중입니다. 잠시 뒤 다시 탭하세요"); return; }
        if (!e.Shift && !e.Alt) pts.Clear();
        pts.Add((e.X, e.Y, e.Alt ? 0 : 1));
        Decode();
    }

    void Decode()
    {
        var d = App.Doc; int n = pts.Count;
        var p = new float[n * 2]; var lab = new long[n];
        for (int i = 0; i < n; i++) { p[i * 2] = (float)(pts[i].x * scale); p[i * 2 + 1] = (float)(pts[i].y * scale); lab[i] = pts[i].l; }
        var s = AiRuntime.Session(model, "prompt_encoder_mask_decoder.onnx");
        using var r = s.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_points", new DenseTensor<float>(p, new[] { 1, 1, n, 2 })),
            NamedOnnxValue.CreateFromTensor("input_labels", new DenseTensor<long>(lab, new[] { 1, 1, n })),
            NamedOnnxValue.CreateFromTensor("image_embeddings", new DenseTensor<float>(emb, embDims)),
            NamedOnnxValue.CreateFromTensor("image_positional_embeddings", new DenseTensor<float>(pos, posDims)),
        });
        var iou = r.First(x => x.Name == "iou_scores").AsTensor<float>().ToArray();
        var masks = r.First(x => x.Name == "pred_masks").AsTensor<float>().ToArray();
        int best = 0; for (int k = 1; k < 3; k++) if (iou[k] > iou[best]) best = k;
        var sharp = new float[65536];
        for (int i = 0; i < 65536; i++) sharp[i] = masks[best * 65536 + i] * 3;   // 가장자리를 조금 또렷하게
        var mask = AiRuntime.MaskToSize(sharp, 0, 256, 256, new RectangleF(0, 0, box.Width / 4f, box.Height / 4f), d.W, d.H, sigmoid: true);
        App.History.SelectionChange(d, "AI 고르기", () => d.Sel.Apply(mask, "replace"));
        SelectCommands.RedrawView();
    }

    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        foreach (var (x, y, l) in pts)
        {
            var (sx, sy) = v.ToScreen(x, y);
            using var b = new SolidBrush(l == 1 ? Color.FromArgb(34, 197, 94) : Color.FromArgb(239, 68, 68));
            g.FillEllipse(b, (float)sx - 6, (float)sy - 6, 12, 12);
            g.DrawEllipse(new Pen(Color.White, 2), (float)sx - 6, (float)sy - 6, 12, 12);
        }
    }
    public override Rectangle OverlayBounds(ViewXf v)
    {
        var r = Rectangle.Empty;
        foreach (var (x, y, _) in pts) { var (sx, sy) = v.ToScreen(x, y); var q = new Rectangle((int)sx - 9, (int)sy - 9, 18, 18); r = r.IsEmpty ? q : Rectangle.Union(r, q); }
        return r;
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Title("AI 고르기"),
        Opt.Button("처음부터 (점 지우기)", () => { pts.Clear(); SelectCommands.None(); }),
        Opt.Button("그림 다시 읽기 (그림을 고쳤으면)", () => { emb = null; Read(); }),
        Opt.Help("탭 = 고르기, Shift+탭 = 더하기, Alt+탭 = 빼기"));
}

public sealed class AiModule : IModule
{
    public void Init()
    {
        Registry.Menu("AI/AI 선화 추출…", () => AiFeatures.Lineart(), order: 10);
        Registry.Menu("AI/AI 캐릭터·배경 따기…", () => AiFeatures.Cutout(), order: 20);
        Registry.Menu("AI/AI 고르기 (탭)", () => App.SetTool("aiselect"), order: 30);
        Registry.Menu("AI/AI 깊이 추정…", () => AiFeatures.Depth(), order: 40);
        Registry.Menu("AI/AI 크기 키우기…", () => AiFeatures.Upscale(), order: 50);
        Registry.Menu("AI/-1", null, order: 90);
        Registry.Menu("AI/AI 모델 관리…", () => AiFeatures.ModelManager(), order: 95);
        Registry.Tool(new AiSelectTool());
    }
}
