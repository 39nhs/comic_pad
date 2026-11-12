// 타임랩스 (웹 판 ui/timelapse.js): 그리다가 잠깐 쉴 때마다 그림을 한 장씩 찍어 두었다가 동영상으로.
//  - 켜짐 상태는 작품 파일에 (doc.Extra["timelapse"] = { id, on } — 웹 판과 같은 값)
//  - 찍은 장면: data/timelapse/<id>/000000.jpg … (웹 판은 브라우저 IndexedDB 에 보관)
//  - 동영상: 외부 패키지 없이 만들 수 있는 Motion-JPEG AVI (Windows 기본 동영상 앱·VLC 등에서 재생)
using System.Text;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Extras;

public static class Timelapse
{
    const int MaxSide = 1280;
    static System.Windows.Forms.Timer timer;
    static bool busy;

    static JsonObject Tl => App.Doc?.Extra["timelapse"] as JsonObject;
    public static bool On => (bool?)Tl?["on"] == true;
    public static string FramesDir(string id) => Path.Combine(App.DataDir, "timelapse", U.SafeName(id));
    public static List<string> Frames(string id) { var d = FramesDir(id); return Directory.Exists(d) ? Directory.GetFiles(d, "*.jpg").OrderBy(x => x, StringComparer.Ordinal).ToList() : new(); }

    public static void Init()
    {
        App.History.Changed += Schedule;
    }

    /// <summary>그린 뒤 1.2초 쉬면 한 장 (펜이 움직이는 동안은 찍지 않음)</summary>
    static void Schedule()
    {
        if (!On) return;
        timer ??= new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick -= Tick; timer.Tick += Tick;
        timer.Stop(); timer.Start();
    }
    static void Tick(object s, EventArgs e)
    {
        timer.Stop();
        if (App.Tool?.Busy == true || Control.MouseButtons != MouseButtons.None) { timer.Start(); return; }
        Capture();
    }

    public static void Capture()
    {
        var doc = App.Doc; var t = Tl;
        if (doc == null || t == null || (bool?)t["on"] != true || busy) return;
        busy = true;
        try
        {
            double s = Math.Min(1, MaxSide / (double)Math.Max(doc.W, doc.H));
            using var b = Img.Flatten(doc, s, true);
            var data = Img.Encode(b, "jpg", 0.82);
            string dir = FramesDir((string)t["id"]);
            Directory.CreateDirectory(dir);
            int n = Directory.GetFiles(dir, "*.jpg").Length;
            File.WriteAllBytes(Path.Combine(dir, n.ToString().PadLeft(6, '0') + ".jpg"), data);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("타임랩스 저장 실패 " + ex.Message); }
        busy = false;
    }

    public static void Toggle()
    {
        var doc = App.Doc; if (doc == null) return;
        if (doc.Extra["timelapse"] is not JsonObject t) { t = new JsonObject { ["id"] = U.Uid("tl"), ["on"] = false }; doc.Extra["timelapse"] = t; }
        t["on"] = !((bool?)t["on"] ?? false);
        doc.Modified = true;
        (App.Main as UI.MainForm)?.UpdateTitle();
        if ((bool)t["on"]) { Capture(); App.Toast("타임랩스 기록 시작: 그릴 때마다 장면을 찍어 둡니다 (작품 파일에 켜짐 상태 저장)"); }
        else App.Toast($"타임랩스 기록을 멈췄습니다 ({Frames((string)t["id"]).Count}장)");
    }

    public static void Export()
    {
        var t = Tl;
        var keys = t != null ? Frames((string)t["id"]) : new();
        if (keys.Count < 2) { App.Toast("찍힌 장면이 아직 없습니다. 보기 > 타임랩스 기록을 켜고 그려 보세요"); return; }
        var v = Dlg.Form("타임랩스 동영상 만들기", new[]
        {
            Field.Num("sec", "동영상 길이 (초)", Math.Min(30, Math.Max(5, Math.Round(keys.Count / 10.0))), 1, 600),
            Field.Num("hold", "마지막 완성 그림 보여 주기 (초)", 2, 0, 30),
            Field.Info($"찍힌 장면 {keys.Count}장. Motion-JPEG AVI 동영상으로 만듭니다."),
        }, "만들기");
        if (v == null) return;
        string name = $"{U.SafeName(App.Doc.Name)}_타임랩스_{U.FileStamp()}.avi";
        string path = Path.Combine(U.ExportsDir, name);
        U.Task("타임랩스 만드는 중", () => MakeVideo(keys, path, U.Num(v["sec"], 10), U.Num(v["hold"], 2)));
        App.Toast($"타임랩스 저장: {path}");
    }

    /// <summary>장면 목록 → AVI. 1초에 최대 30장, 장면이 적으면 장면마다 한 번씩 (속도는 길이에 맞춤)</summary>
    public static void MakeVideo(List<string> keys, string path, double sec, double hold)
    {
        sec = Math.Max(1, sec);
        int total = Math.Max(2, Math.Min(keys.Count, (int)Math.Round(sec * 30)));
        double fps = total / sec;
        int holdN = Math.Max(0, (int)Math.Round(hold * fps));
        int W = 0, H = 0;
        var frames = new List<byte[]>();
        byte[] last = null; int lastIdx = -1;
        byte[] Get(int i)
        {
            if (i == lastIdx) return last;
            var data = File.ReadAllBytes(keys[i]);
            using (var ms = new MemoryStream(data))
            using (var bmp = new Bitmap(ms))
            {
                if (W == 0) { W = bmp.Width / 2 * 2; H = bmp.Height / 2 * 2; }
                if (bmp.Width != W || bmp.Height != H) { using var r = Img.Resize(bmp, W, H); data = Img.Encode(r, "jpg", 0.85); }   // 크기가 다른 장면은 첫 장면 크기로
            }
            lastIdx = i; last = data;
            return data;
        }
        for (int f = 0; f < total; f++) frames.Add(Get(Math.Min(keys.Count - 1, (int)Math.Floor(f * (double)keys.Count / total))));
        for (int f = 0; f < holdN; f++) frames.Add(Get(keys.Count - 1));
        AviWriter.WriteMjpeg(path, frames, W, H, fps);
    }

    public static void Clear()
    {
        var t = Tl; if (t == null) return;
        if (!Dlg.Confirm("이 작품의 타임랩스 장면을 모두 지울까요? (그림은 그대로)", "지우기")) return;
        try { Directory.Delete(FramesDir((string)t["id"]), true); } catch { }
        App.Toast("타임랩스 장면을 지웠습니다");
    }
}

/// <summary>Motion-JPEG AVI (RIFF AVI 1.0 + idx1)</summary>
public static class AviWriter
{
    public static void WriteMjpeg(string path, IList<byte[]> frames, int w, int h, double fps)
    {
        // 속도: dwRate/dwScale (소수 fps 도 정확히)
        uint scale = 1000, rate = (uint)Math.Max(1, Math.Round(fps * 1000));
        int maxFrame = frames.Count == 0 ? 0 : frames.Max(f => f.Length);
        using var fs = new FileStream(path + ".part", FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        void Four(string s) => bw.Write(Encoding.ASCII.GetBytes(s));
        long Begin(string id, string type = null) { Four(id); long at = fs.Position; bw.Write(0u); if (type != null) Four(type); return at; }
        void End(long at) { long now = fs.Position; fs.Position = at; bw.Write((uint)(now - at - 4)); fs.Position = now; }

        long riff = Begin("RIFF", "AVI ");
        long hdrl = Begin("LIST", "hdrl");
        // avih (MainAVIHeader 56바이트)
        Four("avih"); bw.Write(56u);
        bw.Write((uint)Math.Round(1_000_000.0 / Math.Max(0.001, fps)));   // 한 장 시간 (µs)
        bw.Write((uint)(maxFrame * fps));                                // 최대 초당 바이트
        bw.Write(0u);                                                    // padding
        bw.Write(0x10u);                                                 // AVIF_HASINDEX
        bw.Write((uint)frames.Count);                                    // 전체 장 수
        bw.Write(0u);                                                    // initial frames
        bw.Write(1u);                                                    // 스트림 수
        bw.Write((uint)(maxFrame + 8));                                  // 권장 버퍼
        bw.Write((uint)w); bw.Write((uint)h);
        bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);          // reserved
        long strl = Begin("LIST", "strl");
        // strh (AVIStreamHeader 56바이트)
        Four("strh"); bw.Write(56u);
        Four("vids"); Four("MJPG");
        bw.Write(0u); bw.Write((ushort)0); bw.Write((ushort)0); bw.Write(0u);
        bw.Write(scale); bw.Write(rate); bw.Write(0u); bw.Write((uint)frames.Count);
        bw.Write((uint)(maxFrame + 8)); bw.Write(0xFFFFFFFFu); bw.Write(0u);
        bw.Write((short)0); bw.Write((short)0); bw.Write((short)w); bw.Write((short)h);
        // strf (BITMAPINFOHEADER 40바이트)
        Four("strf"); bw.Write(40u);
        bw.Write(40u); bw.Write(w); bw.Write(h); bw.Write((ushort)1); bw.Write((ushort)24); Four("MJPG");
        bw.Write((uint)(w * h * 3)); bw.Write(0); bw.Write(0); bw.Write(0u); bw.Write(0u);
        End(strl);
        End(hdrl);
        long movi = Begin("LIST", "movi");
        long moviData = movi + 4;    // idx1 의 위치 기준: 'movi' 글자 자리
        var index = new List<(uint off, uint size)>();
        foreach (var f in frames)
        {
            index.Add(((uint)(fs.Position - moviData), (uint)f.Length));
            Four("00dc"); bw.Write((uint)f.Length); bw.Write(f);
            if ((f.Length & 1) != 0) bw.Write((byte)0);
        }
        End(movi);
        Four("idx1"); bw.Write((uint)(index.Count * 16));
        foreach (var (off, size) in index) { Four("00dc"); bw.Write(0x10u); bw.Write(off); bw.Write(size); }   // AVIIF_KEYFRAME
        End(riff);
        bw.Flush(); fs.Close();
        File.Move(path + ".part", path, true);
    }
}
