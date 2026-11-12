// 타임랩스 동영상 (웹 판 ui/timelapse.js 의 exportTimelapse): 찍어 둔 장면(Extras 모듈이 data/timelapse/<id>/ 에 기록)을
// MP4(H.264) · WebM(VP8, 직접 짠 인코더) · GIF · AVI(Motion-JPEG) 로. 웹 판과 같은 규칙: 30fps, 길이(초)×30 장, 마지막 완성 그림을 hold 초 더.
// 작품 파일의 doc.timelapse = { id, on } 은 Extras 모듈이 웹 판과 같은 모양으로 읽고 씀 (여기서는 읽기만).
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using AImg = ComicPadNative.Modules.Anim.Img;

namespace ComicPadNative.Modules.Media;

public static class TimelapseMedia
{
    const int Fps = 30;

    /// <summary>장면 순서: 웹 판과 같이 f 번째 장 = 장면[floor(f × 장면수 / 전체)], 끝에 마지막 장면 hold 장</summary>
    public static List<int> Plan(int keys, double sec, double hold, int fps = Fps)
    {
        int total = Math.Max(2, (int)Math.Round(Math.Max(1, sec) * fps)), holdN = Math.Max(0, (int)Math.Round(Math.Max(0, hold) * fps));
        var o = new List<int>(total + holdN);
        for (int f = 0; f < total; f++) o.Add(Math.Min(keys - 1, (int)Math.Floor(f * (double)keys / total)));
        for (int f = 0; f < holdN; f++) o.Add(keys - 1);
        return o;
    }

    static uint[] Load(string file, int w, int h)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(file));
        using var bmp = new Bitmap(ms);
        using var fit = bmp.Width == w && bmp.Height == h ? new Bitmap(bmp) : Extras.Img.Resize(bmp, w, h);   // 크기가 다른 장면은 첫 장면 크기로
        var im = AImg.FromBitmap(fit);
        for (int i = 0; i < im.P.Length; i++) im.P[i] = Px.Over(im.P[i], 0xFFFFFFFF);
        return im.P;
    }

    static (int w, int h) FirstSize(List<string> keys)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(keys[0]));
        using var b = new Bitmap(ms);
        return (b.Width, b.Height);
    }

    /// <summary>MP4 (H.264, 30fps)</summary>
    public static void MakeMp4(List<string> keys, string path, double sec, double hold)
    {
        var (w0, h0) = FirstSize(keys);
        var (w, h) = Mp4Writer.FitSize(Math.Round(w0 / 2.0) * 2, Math.Round(h0 / 2.0) * 2);
        var plan = Plan(keys.Count, sec, hold);
        using var mp = new Mp4Writer(path, w, h, Fps, 1, 6_000_000);
        int shown = -1; uint[] px = null;
        foreach (int i in plan)
        {
            if (i != shown) { px = Load(keys[i], w, h); shown = i; }
            mp.AddFrame(px);
        }
        mp.Finish();
    }

    /// <summary>WebM (VP8, 웹 판 타임랩스와 같은 형식). 같은 장면이 이어지는 동안은 블록을 다시 넣지 않음 (가변 빠르기). 긴 쪽 1920 이하</summary>
    public static void MakeWebm(List<string> keys, string path, double sec, double hold, double quality = 0.8)
    {
        var (w0, h0) = FirstSize(keys);
        var (w, h) = WebmExport.FitSize(w0, h0, 1920);
        var plan = Plan(keys.Count, sec, hold);
        IEnumerable<(uint[], long)> Frames()
        {
            int shown = -1; uint[] px = null;
            for (int f = 0; f < plan.Count; f++)
            {
                if (plan[f] != shown) { px = Load(keys[plan[f]], w, h); shown = plan[f]; }
                yield return (px, (long)Math.Round(f * 1000.0 / Fps));
            }
        }
        WebmExport.Write(path, w, h, Frames(), (long)Math.Round(plan.Count * 1000.0 / Fps), quality, null, Fps);
    }

    /// <summary>GIF (긴 쪽 640px, 10fps — 움짤로 올리기 좋게)</summary>
    public static void MakeGif(List<string> keys, string path, double sec, double hold, int maxSide = 640, int fps = 10)
    {
        var (w0, h0) = FirstSize(keys);
        double k = Math.Min(1, maxSide / (double)Math.Max(w0, h0));
        int w = Math.Max(1, (int)Math.Round(w0 * k)), h = Math.Max(1, (int)Math.Round(h0 * k));
        var plan = Plan(keys.Count, sec, hold, fps);
        var frames = new List<(AImg img, int ms)>();
        int ms = (int)Math.Round(1000.0 / fps);
        int prev = -1;
        foreach (int i in plan)
        {
            if (i == prev) { frames[^1] = (frames[^1].img, frames[^1].ms + ms); continue; }   // 같은 장면이 이어지면 시간만 늘림
            frames.Add((new AImg(w, h, Load(keys[i], w, h)), ms)); prev = i;
        }
        File.WriteAllBytes(path, Anim.Gif.Encode(frames, 0, true, 256, false));
    }

    public static void Export()
    {
        var doc = App.Doc;
        var t = doc?.Extra["timelapse"] as JsonObject;
        var keys = t != null ? Extras.Timelapse.Frames((string)t["id"]) : new();
        if (keys.Count < 2) { App.Toast("찍힌 장면이 아직 없습니다. 보기 > 타임랩스 기록을 켜고 그려 보세요"); return; }
        var v = Dlg.Form("타임랩스 동영상 만들기", new[]
        {
            Field.Segment("fmt", "형식", App.Get("timelapseFmt", "mp4"), ("mp4", "MP4 (H.264)"), ("webm", "WebM (VP8)"), ("gif", "GIF (움짤)"), ("avi", "AVI (Motion-JPEG)")),
            Field.Num("sec", "동영상 길이 (초)", Math.Min(30, Math.Max(5, Math.Round(keys.Count / 10.0))), 1, 600),
            Field.Num("hold", "마지막 완성 그림 보여 주기 (초)", 2, 0, 30),
            Field.Info($"찍힌 장면 {keys.Count}장. MP4·WebM 은 30fps (WebM 은 긴 쪽 1920px 이하), GIF 는 긴 쪽 640px·10fps 로 만듭니다."),
        }, "만들기");
        if (v == null) return;
        string fmt = (string)v["fmt"];
        App.Settings["timelapseFmt"] = fmt; App.SaveSettingsSoon();
        string path = Path.Combine(U.ExportsDir, $"{U.SafeName(doc.Name)}_타임랩스_{U.FileStamp()}.{fmt}");
        double sec = U.Num(v["sec"], 10), hold = U.Num(v["hold"], 2);
        bool ok = false;
        U.Task("타임랩스 만드는 중", () =>
        {
            try
            {
                if (fmt == "mp4") MakeMp4(keys, path, sec, hold);
                else if (fmt == "webm") MakeWebm(keys, path, sec, hold);
                else if (fmt == "gif") MakeGif(keys, path, sec, hold);
                else Extras.Timelapse.MakeVideo(keys, path, sec, hold);
                ok = true;
            }
            catch (Exception ex) { App.Toast("타임랩스를 만들지 못했습니다: " + ex.Message); }
        });
        if (ok) App.Toast($"타임랩스 저장: {path}");
    }
}
