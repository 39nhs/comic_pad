// 버튜버 리그 (웹 판 app/js/vtuber/rig.js 이식): 레이어(부위)마다 역할과 깊이를 정하고, 표정·고개 값으로 그림을 움직여 그린다.
// 2D 시차(깊이가 클수록 고개 방향으로 더 움직임) + 고개 기울기 회전 + 머리카락 용수철 + 숨쉬기. 그리기는 GDI+ (캔버스 2D 와 같은 변환 순서).
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Scene3D;

public sealed record VRole(string Name, double Depth, bool Head, bool Sway = false);

public sealed class VPart
{
    public string Id, Name, Role; public double Depth; public int Key, Order;
    public Bitmap Img; public double X, Y, W, H;
}

public sealed class VRigData
{
    public int W, H; public List<VPart> Parts = new();
    public double NeckX, NeckY, FaceH; public bool Png;
    public JsonObject Opts = new();
    public double Motion { get => J.Num(Opts["motion"], 1); set => Opts["motion"] = value; }
    public double MouthGain { get => J.Num(Opts["mouthGain"], 1); set => Opts["mouthGain"] = value; }
    public double Smooth { get => J.Num(Opts["smooth"], 0.5); set => Opts["smooth"] = value; }
    public bool Mirror { get => J.Bool(Opts["mirror"], true); set => Opts["mirror"] = value; }
    public string Bg { get => J.Str(Opts["bg"], "green"); set => Opts["bg"] = value; }
}

/// <summary>표정·고개 값: 고개 yaw/pitch/roll -1~1, 눈 감음 0~1, 입 0~1, 웃음 0~1, 눈썹 -1~1, 눈동자 -1~1</summary>
public sealed class VState
{
    public double Yaw, Pitch, Roll, BlinkL, BlinkR, Mouth, Smile, Funnel, Brow, LookX, LookY, Talk, T;
    public int Expr;
    public static VState Neutral() => new();
    public VState Clone() => (VState)MemberwiseClone();
}

/// <summary>머리카락 용수철 상태</summary>
public sealed class VPhysics { public double A, V, LastYaw, LastRoll; }

public static class VRig
{
    public static readonly (string id, VRole r)[] Roles =
    {
        ("body", new("몸", 0, false)),
        ("hairBack", new("뒷머리", 0.15, true, true)),
        ("head", new("얼굴(바탕)", 0.3, true)),
        ("blush", new("볼터치", 0.45, true)),
        ("eyesOpen", new("눈 (뜸)", 0.5, true)),
        ("pupils", new("눈동자", 0.52, true)),
        ("eyesClosed", new("눈 (감음)", 0.5, true)),
        ("brows", new("눈썹", 0.55, true)),
        ("mouthClosed", new("입 (닫힘)", 0.5, true)),
        ("mouthOpen", new("입 (벌림)", 0.5, true)),
        ("mouthA", new("입 ㅏ", 0.5, true)),
        ("mouthI", new("입 ㅣ", 0.5, true)),
        ("mouthU", new("입 ㅜ", 0.5, true)),
        ("mouthE", new("입 ㅔ", 0.5, true)),
        ("mouthO", new("입 ㅗ", 0.5, true)),
        ("hairFront", new("앞머리", 0.65, true, true)),
        ("headOther", new("머리에 붙은 것 (모자·액세서리)", 0.4, true)),
        ("bodyOther", new("몸에 붙은 것", 0.05, false)),
        ("expr", new("표정 (단축키로 켜기)", 0.5, true)),
        ("pngIdle", new("PNG: 가만히", 0, false)),
        ("pngTalk", new("PNG: 말할 때", 0, false)),
        ("pngBlink", new("PNG: 눈 감음", 0, false)),
        ("pngTalkBlink", new("PNG: 말하며 눈 감음", 0, false)),
        ("none", new("(쓰지 않음)", 0, false)),
    };
    static readonly Dictionary<string, VRole> roleMap = Roles.ToDictionary(x => x.id, x => x.r);
    public static VRole Role(string id) => id != null && roleMap.TryGetValue(id, out var r) ? r : null;

    /// <summary>레이어 이름으로 역할 짐작 (한국어·영어·일본어)</summary>
    public static string GuessRole(string name)
    {
        string n = Regex.Replace((name ?? "").ToLowerInvariant(), @"\s+", "");
        bool Has(string re) => Regex.IsMatch(n, re);
        if (Has("(idle|대기|기본)") && Has("png")) return "pngIdle";
        if (Has("png") && Has("(talk|말)")) return Has("(blink|감)") ? "pngTalkBlink" : "pngTalk";
        if (Has("png") && Has("(blink|감)")) return "pngBlink";
        if (Has(@"(표정|expr|expression|exp\d)")) return "expr";
        if (Has("(눈썹|brow|眉)")) return "brows";
        if (Has("(눈동자|동공|pupil|iris|瞳)")) return "pupils";
        if (Has("((감은|감음|감김|closed|close|閉).*(눈|eye|目)|(눈|eye|目).*(감|closed|close|閉))")) return "eyesClosed";
        if (Has("(눈|eye|目)")) return "eyesOpen";
        if (Has("(입|mouth|口)"))
        {
            if (Has("(벌|열|open|開)")) return "mouthOpen";
            if (Has("(닫|다문|close|閉)")) return "mouthClosed";
            if (Has("[_-]?(ㅏ|아|a)$")) return "mouthA";
            if (Has("[_-]?(ㅣ|이|i)$")) return "mouthI";
            if (Has("[_-]?(ㅜ|우|u)$")) return "mouthU";
            if (Has("[_-]?(ㅔ|에|e)$")) return "mouthE";
            if (Has("[_-]?(ㅗ|오|o)$")) return "mouthO";
            return "mouthClosed";
        }
        if (Has("(앞머리|frontha|bang|前髪)")) return "hairFront";
        if (Has("(뒷머리|backha|hairback|後ろ髪|後髪)")) return "hairBack";
        if (Has("(머리카락|hair|髪)")) return "hairFront";
        if (Has("(볼|홍조|blush|頬|チーク)")) return "blush";
        if (Has("(얼굴|face|head|顔|頭)")) return "head";
        if (Has("(몸|body|torso|옷|cloth|体|服|목|neck)")) return "body";
        if (Has("(모자|hat|리본|ribbon|귀|ear|액세서리|acc)")) return "headOther";
        if (Has("(배경|background|bg|paper|용지)")) return "none";
        return "headOther";
    }

    // ---------------------------------------------------------------------- 부위 그림 준비
    /// <summary>알파 > 8 인 영역</summary>
    public static Rect OpaqueRect(Surface s)
    {
        var b = s.Bounds();
        if (b.IsEmpty) return Rect.Empty;
        var buf = s.Read(b);
        int x0 = b.W, y0 = b.H, x1 = -1, y1 = -1;
        for (int y = 0; y < b.H; y++) for (int x = 0; x < b.W; x++) if ((buf[y * b.W + x] >> 24) > 8) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        return x1 < 0 ? Rect.Empty : new Rect(b.X + x0, b.Y + y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>레이어 그림 → 부위 (잘라 낸 그림 + 위치, scale 배)</summary>
    public static VPart CutPart(Surface src, double scale = 1)
    {
        var r = OpaqueRect(src);
        if (r.IsEmpty) return null;
        int w = Math.Max(1, (int)Math.Round(r.W * scale)), h = Math.Max(1, (int)Math.Round(r.H * scale));
        using var full = CpadIO.ToBitmap(src, r);
        var c = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(c))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.CompositingMode = CompositingMode.SourceCopy;
            using var ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(full, new Rectangle(0, 0, w, h), 0, 0, r.W, r.H, GraphicsUnit.Pixel, ia);
        }
        return new VPart { Img = c, X = r.X * scale, Y = r.Y * scale, W = w, H = h };
    }

    /// <summary>리그 만들기. parts 순서 = 아래→위. 목 위치(고개 회전 중심)를 얼굴 부위로 정한다</summary>
    public static VRigData BuildRig(List<VPart> parts, int w, int h, JsonObject opts)
    {
        var ps = parts.Where(p => p.Role != null && p.Role != "none" && p.Img != null).ToList();
        for (int i = 0; i < ps.Count; i++) ps[i].Order = i;
        var headParts = ps.Where(p => Role(p.Role)?.Head == true).ToList();
        var face = ps.FirstOrDefault(p => p.Role == "head") ?? headParts.FirstOrDefault();
        var rig = new VRigData { W = w, H = h, Parts = ps };
        // 고개를 돌리는 중심: 얼굴 아래 가운데 (목)
        if (face != null) { rig.NeckX = face.X + face.W / 2; rig.NeckY = face.Y + face.H * 0.95; } else { rig.NeckX = w / 2.0; rig.NeckY = h * 0.6; }
        rig.FaceH = face?.H ?? h * 0.4;
        rig.Png = ps.Any(p => p.Role.StartsWith("png"));
        var o = new JsonObject { ["motion"] = 1, ["mouthGain"] = 1, ["smooth"] = 0.5, ["mirror"] = true };
        if (opts != null) foreach (var (k, v) in opts) o[k] = v?.DeepClone();
        rig.Opts = o;
        return rig;
    }

    public const int MaxSide = 1600;                 // 리그 그림 최대 크기 (방송용으로 충분, 메모리 아낌)

    /// <summary>문서의 그림 레이어들 (아래 → 위)</summary>
    public static List<Layer> DrawableLayers(Document d) => d.Layers.Where(l => !l.IsFolder && !l.IsAdjust && l.Px != null && l.Px.W > 1).ToList();

    /// <summary>문서 + 부위 설정(doc.Extra["vtuber"].parts) → 리그</summary>
    public static VRigData FromDoc(Document d)
    {
        var vt = d.Extra["vtuber"] as JsonObject;
        var cfg = vt?["parts"] as JsonObject ?? new JsonObject();
        double s = Math.Min(1, (double)MaxSide / Math.Max(d.W, d.H));
        var parts = new List<VPart>();
        foreach (var l in DrawableLayers(d))
        {
            var c = cfg[l.Id] as JsonObject;
            string role = J.Str(c?["role"]);
            if (role == null || role == "none") continue;
            var cut = CutPart(l.Px, s);
            if (cut == null) continue;
            cut.Id = l.Id; cut.Name = l.Name; cut.Role = role;
            cut.Depth = c["depth"] is JsonValue ? J.Num(c["depth"], 0.3) : Role(role)?.Depth ?? 0.3;
            cut.Key = (int)J.Num(c["key"], 0);
            parts.Add(cut);
        }
        return BuildRig(parts, (int)Math.Round(d.W * s), (int)Math.Round(d.H * s), vt?["opts"] as JsonObject);
    }

    // ---------------------------------------------------------------------- 상태 → 그리기
    /// <summary>머리카락 용수철 (고개가 움직이면 반대로 살짝 흔들렸다가 돌아옴)</summary>
    public static double StepPhysics(VPhysics ph, VState st, double dt)
    {
        dt = Math.Min(0.05, Math.Max(0.001, dt));
        double push = ((st.Yaw - ph.LastYaw) + (st.Roll - ph.LastRoll) * 1.5) / dt;
        ph.LastYaw = st.Yaw; ph.LastRoll = st.Roll;
        const double k = 90, c = 9;                       // 단단함·감쇠
        ph.V += (-k * ph.A - c * ph.V - push * 0.6) * dt;
        ph.A += ph.V * dt;
        ph.A = Math.Clamp(ph.A, -0.35, 0.35);
        return ph.A;
    }

    /// <summary>입 모양 고르기: 벌림 정도와 모양 값 → 역할 이름</summary>
    public static string MouthRole(VRigData rig, VState st)
    {
        bool Has(string r) => rig.Parts.Any(p => p.Role == r);
        double open = st.Mouth;
        if (open < 0.12) return Has("mouthClosed") ? "mouthClosed" : null;
        var vowels = new[] { "mouthA", "mouthI", "mouthU", "mouthE", "mouthO" }.Where(Has).ToList();
        if (vowels.Count > 0)
        {
            if (st.Funnel > 0.35 && Has("mouthO") && open > 0.35) return "mouthO";
            if (st.Funnel > 0.35 && Has("mouthU")) return "mouthU";
            if (st.Smile > 0.45 && open < 0.4 && Has("mouthI")) return "mouthI";
            if (st.Smile > 0.35 && Has("mouthE")) return "mouthE";
            if (Has("mouthA")) return "mouthA";
            return vowels[0];
        }
        return Has("mouthOpen") ? "mouthOpen" : Has("mouthClosed") ? "mouthClosed" : null;
    }

    static void DrawImg(Graphics g, Bitmap img, double x, double y, double w, double h, float alpha)
    {
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY);
        if (alpha < 1) ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
        g.DrawImage(img, new[] { new PointF((float)x, (float)y), new PointF((float)(x + w), (float)y), new PointF((float)x, (float)(y + h)) }, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
    }

    /// <summary>한 장 그리기. g 는 rig.W×rig.H 기준 (scale 로 맞춤). sway: 머리카락 값(-0.35~0.35), bg: 배경 (null = 투명)</summary>
    public static void Render(Graphics g, VRigData rig, VState st, double sway = 0, double scale = 1, Color? bg = null, bool fast = false)
    {
        double M = rig.Motion;
        var saved = g.Save();
        g.ResetTransform();
        g.ScaleTransform((float)scale, (float)scale);
        g.InterpolationMode = fast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceOver;
        if (bg != null) g.Clear(bg.Value); else g.Clear(Color.Transparent);
        double breath = Math.Sin(st.T * Math.PI * 2 / 3.6);
        double P = rig.FaceH * 0.12 * M;                     // 고개를 돌렸을 때 가장 앞 부위가 움직이는 거리
        double bounce = rig.Png ? -Math.Abs(st.Talk) * rig.H * 0.02 : 0;
        // PNG 버튜버: 상태에 맞는 그림 한 장 (말하면 살짝 튐)
        if (rig.Png)
        {
            bool blink = Math.Max(st.BlinkL, st.BlinkR) > 0.5, talk = st.Talk > 0.5;
            VPart Pick(string r) => rig.Parts.FirstOrDefault(p => p.Role == r);
            var p = (talk && blink ? Pick("pngTalkBlink") : null) ?? (talk ? Pick("pngTalk") : null) ?? (blink ? Pick("pngBlink") : null) ?? Pick("pngIdle") ?? rig.Parts.FirstOrDefault();
            if (p != null) DrawImg(g, p.Img, p.X, p.Y + bounce + breath * rig.H * 0.003, p.W, p.H, 1);
            foreach (var q in rig.Parts) if (q.Role == "expr" && st.Expr != 0 && q.Key == st.Expr) DrawImg(g, q.Img, q.X, q.Y + bounce, q.W, q.H, 1);
            g.Restore(saved);
            return;
        }
        string mouth = MouthRole(rig, st);
        double blinkBoth = (st.BlinkL + st.BlinkR) / 2;
        double rollDeg = st.Roll * 0.35 * M * 180 / Math.PI;      // 최대 약 20°
        bool hasClosed = rig.Parts.Any(q => q.Role == "eyesClosed");
        foreach (var p in rig.Parts)
        {
            var R = Role(p.Role) ?? new VRole("", 0, false);
            // 보일지
            if ((p.Role == "eyesOpen" || p.Role == "pupils") && blinkBoth > 0.55 && hasClosed) continue;
            if (p.Role == "eyesClosed" && blinkBoth <= 0.55) continue;
            if (p.Role.StartsWith("mouth") && p.Role != mouth) continue;
            if (p.Role == "expr" && !(st.Expr != 0 && p.Key == st.Expr)) continue;
            double alpha = 1;
            if (p.Role == "blush") alpha = Math.Min(1, 0.25 + st.Smile * 0.9 + (st.Expr != 0 && p.Key == st.Expr ? 1 : 0));
            var s1 = g.Save();
            double breathY = breath * rig.H * (R.Head ? 0.004 : 0.0025);
            if (R.Head)
            {
                // 목을 중심으로 기울이고, 깊이만큼 고개 방향으로 옮김
                g.TranslateTransform((float)rig.NeckX, (float)(rig.NeckY + breathY));
                g.RotateTransform((float)rollDeg);
                g.TranslateTransform((float)-rig.NeckX, (float)-rig.NeckY);
                g.TranslateTransform((float)(st.Yaw * P * p.Depth * 2), (float)(st.Pitch * P * p.Depth * 1.4));
                // 고개를 돌리면 얼굴이 살짝 좁아 보이게 (가로 눌림)
                double squash = 1 - Math.Abs(st.Yaw) * 0.06 * M;
                g.TranslateTransform((float)rig.NeckX, 0); g.ScaleTransform((float)squash, 1); g.TranslateTransform((float)-rig.NeckX, 0);
            }
            else
            {
                g.TranslateTransform(0, (float)breathY);
                // 숨쉴 때 몸이 아주 조금 커졌다 작아졌다
                g.TranslateTransform((float)(p.X + p.W / 2), (float)(p.Y + p.H)); g.ScaleTransform(1, (float)(1 + breath * 0.004)); g.TranslateTransform((float)-(p.X + p.W / 2), (float)-(p.Y + p.H));
            }
            double dx = 0, dy = 0;
            if (p.Role == "pupils") { dx = st.LookX * p.W * 0.08; dy = st.LookY * p.H * 0.15; }
            if (p.Role == "brows") dy = -st.Brow * rig.FaceH * 0.03;
            if (R.Sway && sway != 0)
            {
                // 머리카락: 윗부분을 중심으로 흔들림 (뒷머리는 반대로 조금 더)
                double px = p.X + p.W / 2, py = p.Y + p.H * 0.1;
                g.TranslateTransform((float)px, (float)py); g.RotateTransform((float)(sway * (p.Role == "hairBack" ? -0.6 : 0.35) * 180 / Math.PI)); g.TranslateTransform((float)-px, (float)-py);
            }
            DrawImg(g, p.Img, p.X + dx, p.Y + dy, p.W, p.H, (float)Math.Clamp(alpha, 0, 1));
            g.Restore(s1);
        }
        g.Restore(saved);
    }

    /// <summary>새 그림에 그리기 (32bpp 미리 곱한 ARGB)</summary>
    public static Bitmap RenderBitmap(VRigData rig, VState st, double sway, double scale, Color? bg)
    {
        int w = Math.Max(1, (int)Math.Round(rig.W * scale / 2) * 2), h = Math.Max(1, (int)Math.Round(rig.H * scale / 2) * 2);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        Render(g, rig, st, sway, scale, bg);
        return bmp;
    }

    // ---------------------------------------------------------------------- 추적 값 → 상태
    /// <summary>얼굴 변환 행렬(열 우선 4x4) → yaw/pitch/roll (라디안)</summary>
    public static (double yaw, double pitch, double roll) HeadAngles(double[] m)
    {
        double R(int r, int c) => m[c * 4 + r];
        return (Math.Atan2(R(0, 2), R(2, 2)), Math.Asin(Math.Clamp(-R(1, 2), -1, 1)), Math.Atan2(R(1, 0), R(1, 1)));
    }

    /// <summary>blendshape 값 → 상태 (mirror: 거울처럼 좌우 바꿈). 얼굴 추적기가 생기면 이 함수로 넘기면 된다</summary>
    public static VState FaceToState(IDictionary<string, double> b, double[] matrix, bool mirror = true)
    {
        double G(string k) => b != null && b.TryGetValue(k, out var v) ? v : 0;
        var st = VState.Neutral();
        st.BlinkL = G(mirror ? "eyeBlinkRight" : "eyeBlinkLeft");
        st.BlinkR = G(mirror ? "eyeBlinkLeft" : "eyeBlinkRight");
        st.Mouth = Math.Min(1, G("jawOpen") * 1.6);
        st.Smile = (G("mouthSmileLeft") + G("mouthSmileRight")) / 2;
        st.Funnel = Math.Max(G("mouthFunnel"), G("mouthPucker"));
        st.Brow = G("browInnerUp") - (G("browDownLeft") + G("browDownRight")) / 2;
        double lx = (G("eyeLookOutLeft") - G("eyeLookInLeft") + G("eyeLookInRight") - G("eyeLookOutRight")) / 2;
        st.LookX = mirror ? -lx : lx;
        st.LookY = ((G("eyeLookDownLeft") + G("eyeLookDownRight")) - (G("eyeLookUpLeft") + G("eyeLookUpRight"))) / 2;
        if (matrix != null)
        {
            var a = HeadAngles(matrix); const double D = Math.PI / 6;     // 30° = 끝까지
            st.Yaw = Math.Clamp((mirror ? -a.yaw : a.yaw) / D, -1, 1);
            st.Pitch = Math.Clamp(-a.pitch / D, -1, 1);
            st.Roll = Math.Clamp((mirror ? -a.roll : a.roll) / D, -1, 1);
        }
        return st;
    }

    /// <summary>소리 크기 (RMS)</summary>
    public static double Rms(ReadOnlySpan<float> buf) { double s = 0; foreach (var v in buf) s += v * v; return Math.Sqrt(s / Math.Max(1, buf.Length)); }
    /// <summary>소리 크기 → 입 벌림 0~1 (문턱 아래는 0)</summary>
    public static double LevelToMouth(double level, double gain = 1, double gate = 0.02) => Math.Clamp((level - gate) * 9 * gain, 0, 1);

    /// <summary>부드럽게: 이전 값 쪽으로 (k = 0 이면 그대로, 1 에 가까울수록 느리게)</summary>
    public static VState SmoothState(VState prev, VState next, double k)
    {
        if (prev == null) return next.Clone();
        var o = next.Clone();
        double S(double a, double b) => a + (b - a) * (1 - k);
        o.Yaw = S(prev.Yaw, next.Yaw); o.Pitch = S(prev.Pitch, next.Pitch); o.Roll = S(prev.Roll, next.Roll); o.Mouth = S(prev.Mouth, next.Mouth);
        o.Smile = S(prev.Smile, next.Smile); o.Funnel = S(prev.Funnel, next.Funnel); o.Brow = S(prev.Brow, next.Brow);
        o.LookX = S(prev.LookX, next.LookX); o.LookY = S(prev.LookY, next.LookY); o.Talk = S(prev.Talk, next.Talk);
        // 눈 깜빡임은 빠르게
        o.BlinkL = prev.BlinkL + (next.BlinkL - prev.BlinkL) * (1 - k * 0.3);
        o.BlinkR = prev.BlinkR + (next.BlinkR - prev.BlinkR) * (1 - k * 0.3);
        return o;
    }

    /// <summary>자동 깜빡임 (추적 안 할 때): 3~6초마다 0.12초</summary>
    public static double AutoBlink(double t, double seed = 0)
    {
        double period = 4.2 + Math.Sin(seed * 12.9898) * 1.2;
        double ph = (t + seed) % period;
        return ph < 0.12 ? 1 : 0;
    }

    // ---------------------------------------------------------------------- .cpvt (아바타 파일) — 부위 그림을 PNG 로
    static string PngUrl(Bitmap b)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }
    public static JsonObject ToFile(VRigData rig, string name = "아바타")
    {
        var parts = new JsonArray();
        foreach (var p in rig.Parts)
            parts.Add(new JsonObject { ["name"] = p.Name, ["role"] = p.Role, ["depth"] = p.Depth, ["key"] = p.Key, ["x"] = p.X, ["y"] = p.Y, ["w"] = p.W, ["h"] = p.H, ["png"] = PngUrl(p.Img) });
        return new JsonObject { ["format"] = "comicpad-vtuber", ["version"] = 1, ["name"] = name, ["w"] = rig.W, ["h"] = rig.H, ["opts"] = rig.Opts.DeepClone(), ["parts"] = parts };
    }
    public static VRigData FromFile(JsonNode json)
    {
        if (J.Str(json?["format"]) != "comicpad-vtuber") throw new InvalidDataException("ComicPad 아바타 파일(.cpvt)이 아닙니다");
        var parts = new List<VPart>();
        foreach (var p in json["parts"]?.AsArray() ?? new JsonArray())
        {
            string png = J.Str(p?["png"], "");
            int w = Math.Max(1, (int)Math.Round(J.Num(p["w"], 1))), h = Math.Max(1, (int)Math.Round(J.Num(p["h"], 1)));
            var img = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            try
            {
                var bytes = Convert.FromBase64String(png[(png.IndexOf(',') + 1)..]);
                using var ms = new MemoryStream(bytes);
                using var src = new Bitmap(ms);
                using var g = Graphics.FromImage(img);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            catch (Exception e) { img.Dispose(); throw new InvalidDataException("아바타 그림을 읽지 못했습니다", e); }
            parts.Add(new VPart { Name = J.Str(p["name"], ""), Role = J.Str(p["role"], "none"), Depth = J.Num(p["depth"], 0.3), Key = (int)J.Num(p["key"], 0), X = J.Num(p["x"], 0), Y = J.Num(p["y"], 0), W = w, H = h, Img = img });
        }
        return BuildRig(parts, (int)J.Num(json["w"], 512), (int)J.Num(json["h"], 512), json["opts"] as JsonObject);
    }
}
