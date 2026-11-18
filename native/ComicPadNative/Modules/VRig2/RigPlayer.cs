// 버튜버 스튜디오에서 메쉬 리그 쓰기 (Scene3D.IVAltRig): 추적 상태(VState) → 표준 파라미터 → 물리 → 메쉬 렌더러.
// .cpvt 저장: 리그 JSON + 부위 텍스처(PNG) — 예전 방식 부위도 함께 넣어 웹 판·예전 스튜디오에서도 열린다.
// 파라미터 애니메이션 내보내기: 정해 둔 움직임(고개 돌리기·깜빡임·말하기·숨쉬기)을 GIF / MP4 / WebM 으로.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Scene3D;

namespace ComicPadNative.Modules.VRig2;

/// <summary>추적 상태 → 파라미터 (스튜디오·편집기 공용)</summary>
public static class RigDrive
{
    /// <summary>VState(고개 -1~1, 눈 감음 0~1 …) → 표준 파라미터. motion: 움직임 크기 (스튜디오 "움직임 크기")</summary>
    public static void Apply(RigRuntime rt, VState st, double motion = 1)
    {
        void S(string id, double v) => rt.Set(id, v);
        S("ParamAngleX", st.Yaw * 30 * motion);
        S("ParamAngleY", -st.Pitch * 30 * motion);
        S("ParamAngleZ", st.Roll * 30 * motion);
        S("ParamBodyAngleX", st.Yaw * 10 * 0.45 * motion);
        S("ParamBodyAngleY", -st.Pitch * 10 * 0.3 * motion);
        S("ParamBodyAngleZ", st.Roll * 10 * 0.35 * motion);
        // VState 의 L = 화면 왼쪽 눈 = 캐릭터의 오른눈 (Live2D EyeR)
        S("ParamEyeROpen", 1 - Math.Clamp(st.BlinkL, 0, 1));
        S("ParamEyeLOpen", 1 - Math.Clamp(st.BlinkR, 0, 1));
        S("ParamEyeLSmile", st.Smile * 0.5); S("ParamEyeRSmile", st.Smile * 0.5);
        S("ParamEyeBallX", st.LookX); S("ParamEyeBallY", -st.LookY);
        S("ParamBrowLY", st.Brow); S("ParamBrowRY", st.Brow);
        S("ParamMouthOpenY", Math.Clamp(st.Mouth, 0, 1));
        S("ParamMouthForm", Math.Clamp(st.Smile * 1.3 - st.Funnel * 0.8, -1, 1));
        S("ParamCheek", st.Smile);
        S("ParamBreath", 0.5 - 0.5 * Math.Cos(st.T * Math.PI * 2 / 3.6));
    }

    /// <summary>내보내기용 정해 둔 움직임 (t 초): 좌우로 돌아보기, 끄덕, 깜빡임, 말하기, 눈동자</summary>
    public static VState Demo(double t)
    {
        var st = VState.Neutral();
        st.T = t;
        st.Yaw = Math.Sin(t * Math.PI * 2 / 4) * 0.9;
        st.Pitch = Math.Sin(t * Math.PI * 2 / 3) * 0.35;
        st.Roll = Math.Sin(t * Math.PI * 2 / 5 + 1) * 0.4;
        double blink = (t % 2.5) < 0.16 ? Math.Sin((t % 2.5) / 0.16 * Math.PI) : 0;
        st.BlinkL = st.BlinkR = blink;
        st.Mouth = t % 4 > 2 ? Math.Abs(Math.Sin(t * 9)) * 0.9 : 0;
        st.Smile = Math.Max(0, Math.Sin(t * Math.PI * 2 / 6)) * 0.7;
        st.LookX = Math.Sin(t * Math.PI * 2 / 3.3) * 0.8; st.LookY = Math.Sin(t * Math.PI * 2 / 4.7) * 0.5;
        st.Brow = Math.Sin(t * Math.PI * 2 / 2.7) * 0.5;
        return st;
    }
}

/// <summary>스튜디오용 메쉬 리그</summary>
public sealed class RigPlayer : IVAltRig
{
    public readonly RigRuntime RT;
    readonly CpuRigRenderer ren = new();
    Bitmap frame;
    readonly double k;          // 스튜디오 크기 / 작품 크기
    public int W { get; }
    public int H { get; }
    public RigPlayer(RigRuntime rt)
    {
        RT = rt;
        int dw = Math.Max(1, rt.Rig.W), dh = Math.Max(1, rt.Rig.H);
        k = Math.Min(1, (double)VRig.MaxSide / Math.Max(dw, dh));
        W = Math.Max(2, (int)Math.Round(dw * k)); H = Math.Max(2, (int)Math.Round(dh * k));
        RT.Evaluate();
    }

    public static RigPlayer FromDoc(Document d)
    {
        var rig = Rig2.FromDoc(d);
        if (rig == null || rig.Parts.Count == 0) return null;
        if (rig.W <= 0) { rig.W = d.W; rig.H = d.H; }
        var rt = new RigRuntime(rig);
        rt.LoadTextures(d);
        rt.Compile();
        return new RigPlayer(rt);
    }

    public void Step(VRigData rig, VState st, double dt)
    {
        RigDrive.Apply(RT, st, rig?.Motion ?? 1);
        RigPhysics.Step(RT.Rig, RT.Phys, RT.Values, dt);
        RT.Evaluate();
    }

    public void Render(Graphics g, VRigData rig, VState st, double scale, Color? bg, bool fast)
    {
        int w = Math.Max(1, (int)Math.Round(W * scale)), h = Math.Max(1, (int)Math.Round(H * scale));
        uint clear = bg is Color c ? Px.Pack(255, c.R, c.G, c.B) : 0;
        RT.Render(ren, w, h, scale * k, 0, 0, clear);
        if (frame == null || frame.Width != w || frame.Height != h) { frame?.Dispose(); frame = new Bitmap(w, h, PixelFormat.Format32bppPArgb); }
        ren.CopyTo(frame);
        var s = g.Save();
        g.ResetTransform();
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(frame, 0, 0);
        g.Restore(s);
    }

    public JsonObject ToFile() => RigFile.Pack(RT);
    public static RigPlayer FromFile(JsonNode n) => new(RigFile.Unpack(n));

    /// <summary>예전 방식 부위 (레이어마다 하나, 스튜디오 크기로 줄인 그림)</summary>
    public List<VPart> LegacyParts()
    {
        var o = new List<VPart>();
        var seen = new HashSet<string>();
        foreach (var p in RT.Rig.Parts.OrderBy(p => p.Order))
        {
            if (p.LayerId == null || !seen.Add(p.LayerId) || !RT.Tex.TryGetValue(p.LayerId, out var t)) continue;
            string role = RigRoles.ToOld(p.Role);
            if (role == "none") continue;
            using var full = RigFile.ToBitmap(t);
            int w = Math.Max(1, (int)Math.Round(t.W * k)), h = Math.Max(1, (int)Math.Round(t.H * k));
            var img = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(img)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(full, new Rectangle(0, 0, w, h)); }
            o.Add(new VPart { Id = p.LayerId, Name = p.Name, Role = role, Depth = VRig.Role(role)?.Depth ?? 0.3, Key = 1, Img = img, X = t.X * k, Y = t.Y * k, W = w, H = h });
        }
        return o;
    }

    public void Dispose() { frame?.Dispose(); frame = null; ren.Dispose(); }
}

/// <summary>.cpvt 안의 rig2 (리그 + 텍스처 PNG)</summary>
public static class RigFile
{
    public static Bitmap ToBitmap(RigTexture t)
    {
        var bmp = new Bitmap(t.W, t.H, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, t.W, t.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new int[t.W];
        for (int y = 0; y < t.H; y++) { for (int x = 0; x < t.W; x++) row[x] = (int)Px.Unpremul(t.Mip[0][y * t.W + x]); System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, t.W); }
        bmp.UnlockBits(bd);
        return bmp;
    }
    static RigTexture FromBitmap(Bitmap src, int x, int y, string key)
    {
        using var b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImageUnscaled(src, 0, 0); }
        var bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var p = new uint[b.Width * b.Height]; var row = new int[b.Width];
        for (int r = 0; r < b.Height; r++) { System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + r * bd.Stride, row, 0, b.Width); for (int c = 0; c < b.Width; c++) p[r * b.Width + c] = Px.Premul((uint)row[c]); }
        b.UnlockBits(bd);
        return RigTexture.FromPixels(p, x, y, b.Width, b.Height, key);
    }

    public static JsonObject Pack(RigRuntime rt)
    {
        var tex = new JsonObject();
        foreach (var id in rt.Rig.Parts.Select(p => p.LayerId).Where(id => id != null).Distinct())
        {
            if (!rt.Tex.TryGetValue(id, out var t)) continue;
            using var bmp = ToBitmap(t);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            tex[id] = new JsonObject { ["x"] = t.X, ["y"] = t.Y, ["png"] = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray()) };
        }
        return new JsonObject { ["rig"] = rt.Rig.ToJson(), ["tex"] = tex };
    }

    public static RigRuntime Unpack(JsonNode n)
    {
        var rig = Rig2.FromJson(n?["rig"]);
        var rt = new RigRuntime(rig);
        if (n?["tex"] is JsonObject tex)
            foreach (var (id, v) in tex)
            {
                string png = v?["png"]?.GetValue<string>() ?? "";
                var bytes = Convert.FromBase64String(png[(png.IndexOf(',') + 1)..]);
                using var ms = new MemoryStream(bytes);
                using var bmp = new Bitmap(ms);
                rt.Tex[id] = FromBitmap(bmp, (int)(v["x"]?.GetValue<double>() ?? 0), (int)(v["y"]?.GetValue<double>() ?? 0), id);
            }
        rt.Compile();
        return rt;
    }
}

/// <summary>파라미터 애니메이션 내보내기 (정해 둔 움직임)</summary>
public static class RigExport
{
    /// <summary>fmt: gif | mp4 | webm. seconds 동안, 크기 w×h (작품 비율로 맞춤). bg: 바탕 (gif 는 null 이면 투명)</summary>
    public static string Export(RigRuntime rt, string fmt, string path, int maxSide, double seconds, Color? bg, Func<double, VState> motion = null, Action<double> progress = null)
    {
        motion ??= RigDrive.Demo;
        double k = (double)maxSide / Math.Max(rt.Rig.W, rt.Rig.H);
        int w = Math.Max(2, (int)Math.Round(rt.Rig.W * k / 2) * 2), h = Math.Max(2, (int)Math.Round(rt.Rig.H * k / 2) * 2);
        if (fmt == "mp4") { (w, h) = Media.Mp4Writer.FitSize(w, h); }
        else if (fmt == "webm") { (w, h) = Media.WebmExport.FitSize(w, h, 1920); }
        k = Math.Min((double)w / rt.Rig.W, (double)h / rt.Rig.H);
        double ox = (w - rt.Rig.W * k) / 2, oy = (h - rt.Rig.H * k) / 2;
        int fps = fmt == "gif" ? 15 : 30;
        int frames = Math.Max(1, (int)Math.Round(seconds * fps));
        var saved = new Dictionary<string, double>(rt.Values);
        rt.Phys.Clear();
        var ren = new CpuRigRenderer();
        uint clear = bg is Color c ? Px.Pack(255, c.R, c.G, c.B) : (fmt == "gif" ? 0u : 0xFFFFFFFFu);
        try
        {
            var gifFrames = new List<(Anim.Img, int)>();
            Media.Mp4Writer mp4 = fmt == "mp4" ? new Media.Mp4Writer(path, w, h, fps, 1, 6_000_000) : null;
            Media.Vp8Video vp8 = fmt == "webm" ? new Media.Vp8Video(0.75, fps * 2, fast: true) : null;
            var blocks = new List<(long t, byte[] d, bool k)>();
            try
            {
                for (int i = 0; i < frames; i++)
                {
                    double t = (double)i / fps;
                    RigDrive.Apply(rt, motion(t));
                    RigPhysics.Step(rt.Rig, rt.Phys, rt.Values, 1.0 / fps);
                    rt.Evaluate();
                    rt.Render(ren, w, h, k, ox, oy, clear);
                    var px = (uint[])ren.Pixels.Clone();
                    if (fmt == "gif") gifFrames.Add((new Anim.Img(w, h, px), 1000 / fps));
                    else if (mp4 != null) mp4.AddFrame(px);
                    else if (vp8 != null) { var (d, key) = vp8.Encode(Media.Argb.FromPremul(px, w, h)); blocks.Add((i * 1000L / fps, d, key)); }
                    progress?.Invoke((double)(i + 1) / frames);
                }
                if (fmt == "gif") File.WriteAllBytes(path, Anim.Gif.Encode(gifFrames, 0, true, 256, bg == null));
                else if (mp4 != null) mp4.Finish();
                else if (vp8 != null)
                {
                    using var wm = new Media.WebmWriter(path, w, h, fps);
                    foreach (var (t, d, key) in blocks) wm.AddVideo(d, t, key);
                    wm.Finish(frames * 1000L / fps);
                }
            }
            finally { mp4?.Dispose(); }
        }
        finally
        {
            ren.Dispose();
            rt.Values.Clear(); foreach (var (kk, v) in saved) rt.Values[kk] = v;
            rt.Phys.Clear();
            rt.Evaluate();
        }
        return path;
    }
}
