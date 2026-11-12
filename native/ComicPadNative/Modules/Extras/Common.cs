// Extras 모듈 공통: 웹 판 form() 과 같은 값 입력 창, 그림 만들기(합치기·줄이기·저장), 문서 Extra 값 실행취소, 작은 도우미.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

/// <summary>입력 칸 하나 (웹 form() 의 필드와 같은 뜻)</summary>
public sealed class Field
{
    public string Key, Label, Type = "text";      // text | textarea | number | checkbox | select | segment | info | date
    public JsonNode Value;
    public (string id, string text)[] Options;
    public double Min = -1e9, Max = 1e9, Step = 1;
    public static Field Info(string text) => new() { Type = "info", Label = text };
    public static Field Text(string key, string label, string value) => new() { Key = key, Label = label, Type = "text", Value = value ?? "" };
    public static Field Area(string key, string label, string value) => new() { Key = key, Label = label, Type = "textarea", Value = value ?? "" };
    public static Field Num(string key, string label, double value, double min = -1e9, double max = 1e9, double step = 1) => new() { Key = key, Label = label, Type = "number", Value = value, Min = min, Max = max, Step = step };
    public static Field Check(string key, string label, bool value) => new() { Key = key, Label = label, Type = "checkbox", Value = value };
    public static Field Select(string key, string label, string value, params (string, string)[] opts) => new() { Key = key, Label = label, Type = "select", Value = value, Options = opts };
    public static Field Segment(string key, string label, string value, params (string, string)[] opts) => new() { Key = key, Label = label, Type = "segment", Value = value, Options = opts };
}

public static class Dlg
{
    /// <summary>자동 액션 재생: 제목이 같은 창이면 기록한 값으로 바로 확인 (null = 보통처럼 창을 띄움)</summary>
    public static Func<string, JsonObject> FormAuto;
    /// <summary>확인한 창의 값 (자동 액션 기록용)</summary>
    public static event Action<string, JsonObject> FormSubmitted;

    /// <summary>값 입력 창 (웹 form(), 모양은 UI.Dialogs.WebForm 하나로). 확인하면 {키: 값}, 취소면 null, 추가 버튼이면 {"__action": 값}</summary>
    public static JsonObject Form(string title, IEnumerable<Field> fieldList, string okLabel = "확인", (string label, string value)[] extra = null, int width = 460)
    {
        var fields = fieldList.Where(f => f != null).ToList();
        var auto = FormAuto?.Invoke(title);
        if (auto != null)
        {
            var r = Defaults(fields);
            foreach (var (k, v) in auto) r[k] = v?.DeepClone();
            return r;
        }
        var spec = fields.Select(fd => new UI.Dialogs.FormField
        {
            Key = fd.Key, Label = fd.Label, Options = fd.Options, Min = fd.Min, Max = fd.Max, Step = fd.Step,
            Type = fd.Type,
            Value = fd.Type switch
            {
                "checkbox" => fd.Value is JsonValue b && b.TryGetValue(out bool bb) && bb,
                "number" => fd.Value is JsonValue n && n.TryGetValue(out double dv) ? dv : 0.0,
                _ => fd.Value is JsonValue sv && sv.TryGetValue(out string ss) ? ss : fd.Value?.ToString(),
            },
        }).ToList();
        var res = UI.Dialogs.WebForm.Show(title, spec, okLabel, "취소", extra, Math.Max(440, width));
        if (res == null) return null;
        if (res.TryGetValue("__extra", out var ex)) return new JsonObject { ["__action"] = ex as string };
        var outp = new JsonObject();
        foreach (var fd in fields)
        {
            if (fd.Key == null || fd.Type == "info" || !res.TryGetValue(fd.Key, out var v)) continue;
            outp[fd.Key] = v switch { bool b => JsonValue.Create(b), double d => JsonValue.Create(d), string s => JsonValue.Create(s), null => null, _ => JsonValue.Create(v.ToString()) };
        }
        FormSubmitted?.Invoke(title, (JsonObject)outp.DeepClone());
        return outp;
    }

    static JsonObject Defaults(List<Field> fields)
    {
        var r = new JsonObject();
        foreach (var f in fields) if (f.Key != null && f.Type != "info") r[f.Key] = f.Value?.DeepClone();
        return r;
    }

    /// <summary>확인 창 (웹 confirmDialog)</summary>
    public static bool Confirm(string msg, string okLabel = "확인") => UI.Dialogs.Modals.Confirm(msg, okLabel);

    /// <summary>모달 창 하나 (닫기 버튼만): 내용 컨트롤을 넣어 보여 줌 (웹 .modal, 크기 고정·가장자리 끌어 크기 바꾸기)</summary>
    public static Form Modal(string title, Control body, int w = 640, int h = 520)
    {
        var f = new UI.Dialogs.ModalForm(title, w, h) { Resizable = true };
        f.SetContent(body);
        f.AddAction("닫기", () => true, primary: true);
        f.Primary = null;   // 엔터는 내용 칸에
        return f;
    }
}
// <summary>문서 Extra 의 값 하나 바꾸기 (메모·자·타임랩스 등) — 실행취소용</summary>
public sealed class ExtraEdit : IEdit
{
    readonly Document doc; readonly string key; readonly JsonNode before, after;
    public string Label { get; }
    public ExtraEdit(Document d, string key, JsonNode before, JsonNode after, string label) { doc = d; this.key = key; this.before = before?.DeepClone(); this.after = after?.DeepClone(); Label = label; }
    public long Bytes => 1024 + (before?.ToJsonString().Length ?? 0) + (after?.ToJsonString().Length ?? 0);
    Rect Put(Document d, JsonNode v) { if (v == null) d.Extra.Remove(key); else d.Extra[key] = v.DeepClone(); ExtrasModule.RaiseExtraChanged(key); return Rect.Empty; }
    public Rect Undo(Document d) => Put(d, before);
    public Rect Redo(Document d) => Put(d, after);

    /// <summary>값을 바꾸고 실행취소에 쌓음</summary>
    public static void Set(Document d, string key, JsonNode next, string label)
    {
        var before = d.Extra[key]?.DeepClone();
        if (next == null) d.Extra.Remove(key); else d.Extra[key] = next.DeepClone();
        d.Modified = true;
        App.History.Push(new ExtraEdit(d, key, before, next, label));
        ExtrasModule.RaiseExtraChanged(key);
    }
}

public static class Img
{
    /// <summary>문서를 합친 그림 (용지 포함, 밑그림 제외). 배율 scale, 투명한 곳은 흰색(white=true)</summary>
    public static Bitmap Flatten(Document d, double scale = 1, bool white = true, Compositor comp = null)
    {
        if (scale < 0.999) return FlattenScaled(d, scale, white, comp);
        var flat = (comp ?? App.Comp).Flatten(d);
        using var full = CpadIO.ToBitmap(flat, flat.Full, white ? 0xFFFFFFFF : 0);
        return Scale(full, scale);
    }

    /// <summary>줄인 그림을 띠 단위로 합성하며 바로 만듦 (큰 원고도 문서 크기 그림을 만들지 않음). 칸마다 평균</summary>
    public static unsafe Bitmap FlattenScaled(Document d, double s, bool white = true, Compositor comp = null)
    {
        comp ??= App.Comp;
        int w = Math.Max(1, (int)Math.Round(d.W * s)), h = Math.Max(1, (int)Math.Round(d.H * s));
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var opt = new RenderOptions { ExcludeDraft = true };
        double kx = d.W / (double)w, ky = d.H / (double)h;
        try
        {
            int band = Math.Max(1, (int)(128 / ky));
            for (int py0 = 0; py0 < h; py0 += band)
            {
                int py1 = Math.Min(h, py0 + band);
                int dy0 = (int)Math.Floor(py0 * ky), dy1 = Math.Min(d.H, Math.Max(dy0 + 1, (int)Math.Ceiling(py1 * ky)));
                var r = new Rect(0, dy0, d.W, dy1 - dy0);
                var buf = comp.Render(d, r, opt);
                for (int py = py0; py < py1; py++)
                {
                    uint* row = (uint*)((byte*)bd.Scan0 + py * bd.Stride);
                    int sy0 = Math.Clamp((int)Math.Floor(py * ky), r.Y, r.B - 1), sy1 = Math.Clamp((int)Math.Floor((py + 1) * ky), sy0 + 1, r.B);
                    for (int px = 0; px < w; px++)
                    {
                        int sx0 = Math.Min(d.W - 1, (int)Math.Floor(px * kx)), sx1 = Math.Clamp((int)Math.Floor((px + 1) * kx), sx0 + 1, d.W);
                        ulong sa = 0, sr = 0, sg = 0, sb = 0; uint n = 0;
                        for (int y = sy0; y < sy1; y++)
                        {
                            int o = (y - r.Y) * r.W;
                            for (int x = sx0; x < sx1; x++) { uint c = buf[o + x]; sa += c >> 24; sr += (c >> 16) & 255; sg += (c >> 8) & 255; sb += c & 255; n++; }
                        }
                        uint pc = Px.Pack((uint)(sa / n), (uint)(sr / n), (uint)(sg / n), (uint)(sb / n));
                        if (white) pc = Px.Over(pc, 0xFFFFFFFF);
                        row[px] = Px.Unpremul(pc);
                    }
                }
            }
        }
        finally { b.UnlockBits(bd); }
        return b;
    }

    public static Bitmap Scale(Bitmap src, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        return Resize(src, w, h);
    }

    public static Bitmap Resize(Bitmap src, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceCopy;
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        return b;
    }

    public static Bitmap Crop(Bitmap src, Rectangle r)
    {
        r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
        var b = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.DrawImage(src, new Rectangle(0, 0, b.Width, b.Height), r, GraphicsUnit.Pixel);
        return b;
    }

    public static byte[] Encode(Bitmap b, string fmt, double quality = 0.92)
    {
        using var ms = new MemoryStream();
        if (fmt == "jpg" || fmt == "jpeg")
        {
            // JPEG 은 알파가 없으니 흰 바탕 위로
            using var rgb = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(rgb)) { g.Clear(Color.White); g.DrawImageUnscaled(b, 0, 0); }
            var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Round(quality * 100));
            rgb.Save(ms, enc, ep);
        }
        else b.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>파일을 잠그지 않고 그림 읽기</summary>
    public static Bitmap Load(string path)
    {
        try { using var ms = new MemoryStream(File.ReadAllBytes(path)); using var b = new Bitmap(ms); return new Bitmap(b); }
        catch { return null; }
    }
}

public static class U
{
    static int seq;
    public static string Uid(string p = "id") => p + Convert.ToString(DateTimeOffset.Now.ToUnixTimeMilliseconds(), 16) + Convert.ToString(Interlocked.Increment(ref seq), 16);
    public static string SafeName(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s ?? "", "[\\\\/:*?\"<>|]+", "_").Trim();
        return s.Length == 0 ? "제목없음" : s;
    }
    public static string FileStamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public static string ExportsDir { get { var p = Path.Combine(App.DataDir, "exports"); Directory.CreateDirectory(p); return p; } }
    public static string FmtTime(double sec) { long s = (long)Math.Round(sec); long hh = s / 3600, mm = s % 3600 / 60; return hh > 0 ? $"{hh}시간 {mm}분" : $"{mm}분"; }
    public static readonly JsonSerializerOptions Indent1 = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>JSON 숫자 읽기 (정수로 만든 값·실수로 만든 값·파일에서 읽은 값 모두)</summary>
    public static double Num(JsonNode n, double fb = 0)
    {
        if (n is not JsonValue v) return fb;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out float f)) return f;
        if (v.TryGetValue(out decimal m)) return (double)m;
        if (v.TryGetValue(out string s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return fb;
    }
    public static int Int(JsonNode n, int fb = 0) => n is JsonValue ? (int)Math.Round(Num(n, fb)) : fb;
    public static bool Same(JsonNode a, JsonNode b) => (a?.ToJsonString() ?? "null") == (b?.ToJsonString() ?? "null");
    public static JsonObject Obj(string key) { if (App.Settings[key] is not JsonObject o) { o = new JsonObject(); App.Settings[key] = o; } return o; }
    public static JsonArray Arr(string key) { if (App.Settings[key] is not JsonArray a) { a = new JsonArray(); App.Settings[key] = a; } return a; }
    public static void SaveSettings() { App.SaveSettingsSoon(); }

    /// <summary>메인 창의 그림판 (없으면 null)</summary>
    public static CanvasView Canvas => (App.Main as MainForm)?.Canvas;

    /// <summary>창 안의 컨트롤을 모두 (메인 창 구조를 고치지 않고 찾을 때)</summary>
    public static IEnumerable<Control> All(Control root)
    {
        if (root == null) yield break;
        foreach (Control c in root.Controls) { yield return c; foreach (var k in All(c)) yield return k; }
    }

    public static void Task(string label, Action work)
    {
        var old = App.Main?.Cursor;
        if (App.Main != null) App.Main.Cursor = Cursors.WaitCursor;
        App.Toast(label + "…");
        Application.DoEvents();
        try { work(); }
        finally { if (App.Main != null) App.Main.Cursor = old ?? Cursors.Default; }
    }
}
