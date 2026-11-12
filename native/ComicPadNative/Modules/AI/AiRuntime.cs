// AI 실행기: ONNX Runtime (DirectML 그래픽 가속, 안 되면 CPU). 모델 파일은 웹 판과 같은 곳 (data/models/<id>/),
// 목록은 app/ai-models.json (주소·SHA-256 고정). 입력·출력 처리는 웹 판 app/js/ai/runtime.js·features.js 와 같게.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.AI;

public sealed class AiModel
{
    public string Id, Task, Name, License, Commercial, Note, Slow, Ep;
    public bool Manual, Convert;
    public List<(string name, long size, string url, string sha256)> Files = new();
    public string Dir => Path.Combine(App.DataDir, "models", Id);
    public bool Installed => Files.All(f => File.Exists(Path.Combine(Dir, f.name)));
    public string Badge => Commercial == "yes" ? "상업 가능" : Commercial == "check" ? "확인 필요" : "⚠ 비상업";
}

public static class AiRuntime
{
    static List<AiModel> registry;
    static readonly Dictionary<string, InferenceSession> sessions = new();
    public static string LastEp = "";

    /// <summary>app/ai-models.json 찾기 (실행 파일 위쪽 폴더들)</summary>
    static string FindUp(params string[] rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent) { string p = Path.Combine(new[] { d.FullName }.Concat(rel).ToArray()); if (File.Exists(p) || Directory.Exists(p)) return p; }
        return null;
    }

    public static List<AiModel> Registry()
    {
        if (registry != null) return registry;
        registry = new();
        string p = FindUp("app", "ai-models.json");
        if (p == null) return registry;
        var root = JsonNode.Parse(File.ReadAllText(p));
        foreach (var n in root["models"].AsArray())
        {
            var m = new AiModel
            {
                Id = (string)n["id"], Task = (string)n["task"], Name = (string)n["name"], License = (string)n["license"], Commercial = (string)n["commercial"] ?? "check",
                Note = (string)n["note"], Slow = (string)n["slow"], Ep = (string)n["ep"], Manual = (bool?)n["manual"] ?? false, Convert = n["convert"] != null,
            };
            foreach (var f in n["files"]?.AsArray() ?? new JsonArray()) m.Files.Add(((string)f["name"], (long?)f["size"] ?? 0, (string)f["url"], (string)f["sha256"]));
            registry.Add(m);
        }
        return registry;
    }

    /// <summary>설치 파일에 들어 있는 모델(models_bundled)은 data/models 로 복사해 둠</summary>
    public static void CopyBundled(AiModel m)
    {
        if (m.Installed) return;
        string b = FindUp("models_bundled", m.Id);
        if (b == null) return;
        Directory.CreateDirectory(m.Dir);
        foreach (var f in Directory.GetFiles(b)) File.Copy(f, Path.Combine(m.Dir, Path.GetFileName(f)), true);
    }

    public static List<AiModel> Installed(string task) { var l = Registry().Where(m => m.Task == task).ToList(); foreach (var m in l) CopyBundled(m); return l.Where(m => m.Installed).ToList(); }

    /// <summary>모델 받기: https 만, 크기·SHA-256 이 목록과 같아야 (웹 판 server.py fetch_model 과 같음). 변환이 필요한 모델은 웹 판에서</summary>
    public static async Task Download(AiModel m, IProgress<double> prog, CancellationToken ct)
    {
        if (m.Manual) throw new InvalidOperationException("이 모델은 약관 동의 후 직접 받아야 합니다");
        if (m.Convert) throw new InvalidOperationException("이 모델은 PyTorch 로 변환이 필요합니다. 웹 판(AI > AI 모델 관리)에서 받으면 여기서도 씁니다");
        Directory.CreateDirectory(m.Dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ComicPad");
        long total = m.Files.Sum(f => f.size), done = 0;
        foreach (var f in m.Files)
        {
            string dst = Path.Combine(m.Dir, f.name);
            if (File.Exists(dst) && (f.size == 0 || new FileInfo(dst).Length == f.size) && (f.sha256 == null || Sha(dst) == f.sha256)) { done += f.size; continue; }
            if (!f.url.StartsWith("https://")) throw new InvalidOperationException($"{f.name}: https 주소가 아니어서 받지 않습니다");
            string part = dst + ".part";
            using (var resp = await http.GetAsync(f.url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                await using var o = File.Create(part);
                using var sha = SHA256.Create();
                var buf = new byte[1 << 20]; int n; long got = 0;
                while ((n = await s.ReadAsync(buf, ct)) > 0)
                {
                    got += n;
                    if (f.size > 0 && got > f.size) throw new InvalidOperationException($"{f.name} 가 예상보다 커서 중단했습니다");
                    await o.WriteAsync(buf.AsMemory(0, n), ct);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n; prog?.Report(total > 0 ? (double)done / total : 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (f.sha256 != null && Convert.ToHexString(sha.Hash).ToLowerInvariant() != f.sha256) { o.Close(); File.Delete(part); throw new InvalidOperationException($"{f.name} 가 원본과 다릅니다 (SHA-256 불일치). 받은 파일을 지웠습니다"); }
            }
            File.Move(part, dst, true);
        }
    }
    static string Sha(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }

    public static InferenceSession Session(AiModel m, string file)
    {
        string path = Path.Combine(m.Dir, file);
        if (sessions.TryGetValue(path, out var s)) return s;
        var opt = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        bool cpuOnly = App.Get("aiCpuOnly", false) || m.Ep == "wasm";
        if (!cpuOnly)
        {
            try { opt.AppendExecutionProvider_DML(0); LastEp = "그래픽(DirectML)"; }
            catch { LastEp = "CPU"; }
        }
        else LastEp = "CPU";
        try { s = new InferenceSession(path, opt); }
        catch when (!cpuOnly)
        {
            opt.Dispose(); opt = new SessionOptions(); LastEp = "CPU";
            s = new InferenceSession(path, opt);
        }
        sessions[path] = s;
        return s;
    }
    public static void CloseAll() { foreach (var s in sessions.Values) s.Dispose(); sessions.Clear(); }

    // ---- 그림 ↔ 텐서 ---------------------------------------------------------------
    public static Bitmap Resize(Bitmap src, int w, int h, Rectangle? into = null, Color? bg = null)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.Clear(bg ?? Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingMode = CompositingMode.SourceOver;
        using var ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY);
        var r = into ?? new Rectangle(0, 0, w, h);
        g.DrawImage(src, r, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return b;
    }

    /// <summary>웹 판 toCHW 와 같음: fit stretch|contain, align center|start, mean/std, padZero</summary>
    public static (float[] data, Rectangle box) ToCHW(Bitmap src, int w, int h, float[] mean = null, float[] std = null, bool contain = false, bool alignStart = false, Color? bg = null, bool padZero = false, bool gray = false)
    {
        mean ??= new float[] { 0, 0, 0 }; std ??= new float[] { 1, 1, 1 };
        var box = new Rectangle(0, 0, w, h);
        if (contain)
        {
            double s = Math.Min((double)w / src.Width, (double)h / src.Height);
            int dw = Math.Max(1, (int)Math.Round(src.Width * s)), dh = Math.Max(1, (int)Math.Round(src.Height * s));
            box = new Rectangle(alignStart ? 0 : (w - dw) / 2, alignStart ? 0 : (h - dh) / 2, dw, dh);
        }
        using var b = Resize(src, w, h, box, bg);
        var px = ReadArgb(b);
        int n = w * h; var o = new float[(gray ? 1 : 3) * n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (padZero && !box.Contains(x, y)) continue;
                uint c = px[i]; float r = (c >> 16) & 255, g = (c >> 8) & 255, bl = c & 255;
                if (gray) o[i] = ((r * 0.299f + g * 0.587f + bl * 0.114f) / 255 - mean[0]) / std[0];
                else { o[i] = (r / 255 - mean[0]) / std[0]; o[n + i] = (g / 255 - mean[1]) / std[1]; o[2 * n + i] = (bl / 255 - mean[2]) / std[2]; }
            }
        return (o, box);
    }
    public static readonly float[] ImMean = { 0.485f, 0.456f, 0.406f }, ImStd = { 0.229f, 0.224f, 0.225f };

    public static unsafe uint[] ReadArgb(Bitmap b)
    {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var a = new uint[b.Width * b.Height];
        for (int y = 0; y < b.Height; y++) { uint* row = (uint*)((byte*)d.Scan0 + y * d.Stride); for (int x = 0; x < b.Width; x++) a[y * b.Width + x] = row[x]; }
        b.UnlockBits(d);
        return a;
    }
    public static unsafe Bitmap FromArgb(uint[] a, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) { uint* row = (uint*)((byte*)d.Scan0 + y * d.Stride); for (int x = 0; x < w; x++) row[x] = a[y * w + x]; }
        b.UnlockBits(d);
        return b;
    }

    /// <summary>한 장 마스크(0~1·로짓) → 원래 크기 0~255 (웹 판 maskToCanvas)</summary>
    public static byte[] MaskToSize(float[] arr, int offset, int mw, int mh, RectangleF? box, int outW, int outH, bool sigmoid = false, bool minmax = false)
    {
        float lo = 0, hi = 1;
        if (minmax) { lo = float.MaxValue; hi = float.MinValue; for (int i = 0; i < mw * mh; i++) { float v = arr[offset + i]; if (v < lo) lo = v; if (v > hi) hi = v; } if (hi - lo < 1e-6f) hi = lo + 1; }
        var small = new uint[mw * mh];
        for (int i = 0; i < mw * mh; i++)
        {
            float v = arr[offset + i];
            if (sigmoid) v = 1 / (1 + MathF.Exp(-v));
            if (minmax) v = (v - lo) / (hi - lo);
            uint a = (uint)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
            small[i] = (a << 24) | (a << 16) | (a << 8) | a;
        }
        using var sb = FromArgb(small, mw, mh);
        var bx = box ?? new RectangleF(0, 0, mw, mh);
        using var big = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(sb, new RectangleF(0, 0, outW, outH), bx, GraphicsUnit.Pixel);
        }
        var px = ReadArgb(big);
        var o = new byte[outW * outH];
        for (int i = 0; i < o.Length; i++) o[i] = (byte)((px[i] >> 16) & 255);
        return o;
    }

    public static float[] Run1(InferenceSession s, string input, float[] data, int[] dims, out int[] outDims)
    {
        var t = new DenseTensor<float>(data, dims);
        using var r = s.Run(new[] { NamedOnnxValue.CreateFromTensor(input, t) });
        var o = r.First().AsTensor<float>();
        outDims = o.Dimensions.ToArray();
        return o.ToArray();
    }

    /// <summary>크기와 상관없는 모델을 조각으로 (웹 판 runTiled). enc/dec 는 ARGB 픽셀 기준</summary>
    public static uint[] RunTiled(InferenceSession s, uint[] src, int W, int H, int tile, int pad, int scale, int inC, int multiple,
        Action<uint, float[], int, int> enc, Func<float[], int, int, uint> dec, IProgress<double> prog, CancellationToken ct)
    {
        string inName = s.InputMetadata.Keys.First();
        int OW = W * scale, OH = H * scale;
        var dest = new uint[OW * OH];
        var tiles = new List<(int x, int y)>();
        for (int y = 0; y < H; y += tile) for (int x = 0; x < W; x += tile) tiles.Add((x, y));
        int k = 0;
        foreach (var (x0, y0) in tiles)
        {
            ct.ThrowIfCancellationRequested();
            prog?.Report((double)k++ / tiles.Count);
            int tw = Math.Min(tile, W - x0), th = Math.Min(tile, H - y0), ix = x0 - pad, iy = y0 - pad;
            int iw = (int)Math.Ceiling((tw + 2.0 * pad) / multiple) * multiple, ih = (int)Math.Ceiling((th + 2.0 * pad) / multiple) * multiple;
            int n = iw * ih; var buf = new float[inC * n];
            for (int yy = 0; yy < ih; yy++) { int sy = Math.Clamp(iy + yy, 0, H - 1); for (int xx = 0; xx < iw; xx++) enc(src[sy * W + Math.Clamp(ix + xx, 0, W - 1)], buf, yy * iw + xx, n); }
            var od = Run1(s, inName, buf, new[] { 1, inC, ih, iw }, out var dims);
            int oh = dims[2], ow = dims[3], on = oh * ow;
            int cw = tw * scale, chh = th * scale, cx = pad * scale, cy = pad * scale;
            for (int yy = 0; yy < chh; yy++) for (int xx = 0; xx < cw; xx++) dest[(y0 * scale + yy) * OW + x0 * scale + xx] = dec(od, (cy + yy) * ow + cx + xx, on);
        }
        prog?.Report(1);
        return dest;
    }
}
