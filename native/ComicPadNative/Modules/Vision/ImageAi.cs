// 이 PC 의 이미지 AI (Stable Diffusion WebUI / Forge 의 API) 연결 (웹 판 ui/aitools.js aiImg2Img·aiCheck 이식).
// 웹 판처럼 이 PC 주소(127.0.0.1·localhost)만 허용 — 그림은 이 PC 밖으로 나가지 않는다. 사용자가 켜 둔 서버만 쓰고(opt-in), ComicPad 가 모델을 받지 않는다.
using System.Drawing.Imaging;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;
using ComicPadNative.Modules.AI;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Vision;

public static class ImageAi
{
    static string Url => App.Get("aiUrl", "http://127.0.0.1:7860");
    static readonly Regex LocalOnly = new(@"^https?://(127\.0\.0\.1|localhost)(:\d+)?/?$", RegexOptions.IgnoreCase);
    public static bool IsLocal(string url) => url != null && LocalOnly.IsMatch(url.Trim());

    static async Task<JsonNode> Call(string baseUrl, string path, JsonNode body, CancellationToken ct)
    {
        if (!IsLocal(baseUrl)) throw new InvalidOperationException("이 PC 의 주소(127.0.0.1 또는 localhost)만 쓸 수 있습니다");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var uri = baseUrl.TrimEnd('/') + path;
        using var r = body == null ? await http.GetAsync(uri, ct) : await http.PostAsync(uri, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        string t = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(t.Length > 0 ? t[..Math.Min(200, t.Length)] : $"AI 서버 오류 ({(int)r.StatusCode})");
        return JsonNode.Parse(t);
    }

    public static bool Check(bool quiet = false)
    {
        try
        {
            var models = Task.Run(() => Call(Url, "/sdapi/v1/sd-models", null, CancellationToken.None)).GetAwaiter().GetResult() as JsonArray;
            string first = (string)models?.FirstOrDefault()?["model_name"] ?? "";
            if (!quiet) App.Toast($"이미지 AI 연결됨: 모델 {models?.Count ?? 0}개 ({first[..Math.Min(30, first.Length)]})");
            return true;
        }
        catch (Exception e)
        {
            string msg = e is HttpRequestException ? "서버가 꺼져 있습니다" : e.Message;
            App.Toast($"이미지 AI 에 연결하지 못했습니다: {msg}\n\nStable Diffusion WebUI(또는 Forge)를 --api 옵션으로 켜 두면 연결됩니다. 주소: {Url}");
            return false;
        }
    }

    public static void Img2Img()
    {
        var d = App.Doc;
        if (d == null) return;
        string url = Url, prompt = App.Get("aiPrompt", "anime style, flat colors, clean lineart, comic"), neg = App.Get("aiNeg", "blurry, low quality, text, watermark"), src = "all";
        double den = App.Get("aiDen", 0.45), steps = 25;
        using (var f = new Form { Text = "이미지 AI 로 그리기 (img2img)", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedToolWindow, BackColor = Opt.Bg, ForeColor = Opt.Fg, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink })
        {
            UI.Dialogs.Modals.Adopt(f);
            var p = Opt.Panel();
            TextBox Box(string label, string v, bool multi = false)
            {
                p.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Opt.Fg, MaximumSize = new Size(360, 0) });
                var t = new TextBox { Text = v, Width = 360, Multiline = multi, Height = multi ? 64 : 24, BackColor = Opt.Bg2, ForeColor = Opt.Fg };
                p.Controls.Add(t); return t;
            }
            var tUrl = Box("AI 서버 주소 (이 PC)", url);
            p.Controls.Add(Opt.Choice("바탕으로 쓸 것", new[] { ("all", "보이는 그림 전체"), ("layer", "지금 레이어") }, () => src, v => src = v));
            var tPrompt = Box("원하는 결과 (영어가 잘 됨, 예: anime style, flat color, clean lineart)", prompt, true);
            var tNeg = Box("빼고 싶은 것", neg);
            p.Controls.Add(Opt.Slider("바뀌는 정도 (0.2 = 조금, 0.7 = 많이)", 0.05, 1, () => den, v => den = Math.Round(v, 2)));
            p.Controls.Add(Opt.Slider("단계 수", 5, 80, () => steps, v => steps = Math.Round(v), step: 1));
            p.Controls.Add(Opt.Help($"결과는 \"AI 결과\" 새 레이어로 들어갑니다{(d.Sel.Active ? " (선택 영역 부분만)" : "")}. 그림은 이 PC 밖으로 나가지 않습니다 (이 PC 의 AI 서버로만 보냄)."));
            bool ok = false;
            p.Controls.Add(Opt.Row(Opt.Button("그리기", () => { ok = true; f.Close(); }, true), Opt.Button("취소", f.Close)));
            f.Controls.Add(p);
            f.ShowDialog(App.Main);
            if (!ok) return;
            url = tUrl.Text.Trim().TrimEnd('/'); prompt = tPrompt.Text; neg = tNeg.Text;
        }
        if (!IsLocal(url)) { App.Toast("이 PC 의 주소(127.0.0.1 또는 localhost)만 쓸 수 있습니다"); return; }
        App.Set("aiUrl", url); App.Set("aiPrompt", prompt); App.Set("aiNeg", neg); App.Set("aiDen", den);

        var r = d.Sel.Active ? d.Sel.Bounds() : new Rect(0, 0, d.W, d.H);
        if (r.IsEmpty) return;
        // AI 는 1024 안쪽, 8의 배수 크기로
        double s = Math.Min(1, 1024.0 / Math.Max(r.W, r.H));
        int W = Math.Max(64, (int)Math.Round(r.W * s / 8) * 8), H = Math.Max(64, (int)Math.Round(r.H * s / 8) * 8);
        uint[] buf = src == "layer" && d.Active?.Px != null ? d.Active.Px.Read(r) : App.Comp.Render(d, r, new RenderOptions { Paper = true, ExcludeDraft = true });
        for (int i = 0; i < buf.Length; i++) buf[i] = Px.Unpremul(Px.Over(buf[i], 0xFFFFFFFF));
        string b64;
        using (var bmp = AiRuntime.FromArgb(buf, r.W, r.H))
        using (var small = AiRuntime.Resize(bmp, W, H))
        using (var ms = new MemoryStream()) { small.Save(ms, ImageFormat.Png); b64 = Convert.ToBase64String(ms.ToArray()); }
        var body = new JsonObject
        {
            ["init_images"] = new JsonArray(b64), ["prompt"] = prompt, ["negative_prompt"] = neg, ["denoising_strength"] = den, ["steps"] = (int)steps,
            ["width"] = W, ["height"] = H, ["cfg_scale"] = 7, ["sampler_name"] = "Euler a",
        };
        JsonNode res = null; Exception err = null;
        VisionUi.RunProgress(App.Main, "이미지 AI 그리는 중 (몇십 초 걸릴 수 있음)", async (prog, ct) => res = await Call(url, "/sdapi/v1/img2img", body, ct), e => err = e);
        if (err is OperationCanceledException) return;
        if (err != null) { App.Toast(err is HttpRequestException ? "이미지 AI 서버에 연결하지 못했습니다" : err.Message); Check(); return; }
        var outB64 = (string)res?["images"]?[0];
        if (string.IsNullOrEmpty(outB64)) { App.Toast("AI 가 그림을 돌려주지 않았습니다"); return; }
        using var outBmp = new Bitmap(new MemoryStream(Convert.FromBase64String(outB64)));
        using var fit = AiRuntime.Resize(outBmp, r.W, r.H);
        App.History.Struct(d, "AI 결과", () =>
        {
            var l = new Layer(d, "AI 결과");
            var a = d.Active;
            l.Parent = a?.Parent;
            d.Layers.Insert(a == null ? d.Layers.Count : d.Layers.IndexOf(a) + 1, l);
            d.ActiveId = l.Id;
            var tmp = new Surface(d.W, d.H);
            CpadIO.DrawBitmap(tmp, fit, r.X, r.Y);
            if (d.Sel.Active) { var px = tmp.Read(r); for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++) px[y * r.W + x] = Px.Scale(px[y * r.W + x], d.Sel.At(r.X + x, r.Y + y)); tmp.Write(r, px); }
            l.Px = tmp;
        });
        App.NotifyLayers(); App.Invalidate(r);
        App.Toast("AI 결과를 새 레이어로 넣었습니다 (불투명도·지우개로 섞어 쓰세요)");
    }
}
