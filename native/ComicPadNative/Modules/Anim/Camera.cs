// 카메라: 프레임마다 보이는 영역 (가운데 x,y = 0~1, 확대 z ≥ 1, 회전 r°) — 웹 판 app/js/core/animx.js
// doc.anim.camera = { keys: [{ f, x, y, z, r }], ease }
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public struct Cam { public double F, X, Y, Z, R; }

public static class Camera
{
    static double Ease(double t) => t * t * (3 - 2 * t);
    static Cam Of(JsonObject k) => new() { F = J.D(k["f"]), X = J.D(k["x"], 0.5), Y = J.D(k["y"], 0.5), Z = J.D(k["z"], 1), R = J.D(k["r"], 0) };

    public static List<JsonObject> Keys(Document d) => (AnimCore.A(d)["camera"]?["keys"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();

    public static Cam? At(Document d, int i)
    {
        var keys = Keys(d).Select(Of).OrderBy(k => k.F).ToList();
        if (keys.Count == 0) return null;
        if (i <= keys[0].F) return keys[0];
        if (i >= keys[^1].F) return keys[^1];
        int k = keys.FindIndex(c => c.F > i);
        var A = keys[k - 1]; var B = keys[k];
        double t = (i - A.F) / (B.F - A.F);
        if (J.B(AnimCore.A(d)["camera"]?["ease"], true)) t = Ease(t);
        return new Cam { F = i, X = A.X + (B.X - A.X) * t, Y = A.Y + (B.Y - A.Y) * t, Z = A.Z + (B.Z - A.Z) * t, R = A.R + (B.R - A.R) * t };
    }

    /// <summary>카메라를 적용한 프레임 그림 (같은 크기, 흰 바탕). 카메라가 없으면 그대로</summary>
    public static uint[] Apply(Document d, int i, uint[] src, int w, int h)
    {
        var cc = At(d, i);
        if (cc == null) return src;
        var cam = cc.Value;
        if (cam.Z == 1 && cam.X == 0.5 && cam.Y == 0.5 && cam.R == 0) return src;
        // 화면(출력) ← 원본: translate(w/2,h/2)·scale(z)·rotate(-r)·translate(-x·w, -y·h)
        var T = Affine.Translate(w / 2.0, h / 2.0).Mul(Affine.Scale(cam.Z)).Mul(Affine.Rotate(-cam.R)).Mul(Affine.Translate(-cam.X * w, -cam.Y * h));
        var o = new uint[w * h];
        Array.Fill(o, 0xFFFFFFFFu);
        Xform.DrawTransformed(o, new Rect(0, 0, w, h), r => r == new Rect(0, 0, w, h) ? src : Crop(src, w, r), new Rect(0, 0, w, h), T, 1, false);
        return o;
    }

    static uint[] Crop(uint[] s, int w, Rect r)
    {
        var o = new uint[r.W * r.H];
        for (int y = 0; y < r.H; y++) Array.Copy(s, (r.Y + y) * w + r.X, o, y * r.W, r.W);
        return o;
    }

    public static void SetKey(Document d, int i, Dictionary<string, double> vals)
    {
        AnimCore.EditAnimOnly("카메라 키", a =>
        {
            var cam = J.Obj(a, "camera");
            if (cam["ease"] == null) cam["ease"] = true;
            var keys = J.Arr(cam, "keys");
            var k = keys.OfType<JsonObject>().FirstOrDefault(c => J.I(c["f"]) == i);
            if (k == null)
            {
                var b = At(d, i) ?? new Cam { X = 0.5, Y = 0.5, Z = 1, R = 0 };
                k = new JsonObject { ["f"] = i, ["x"] = b.X, ["y"] = b.Y, ["z"] = b.Z, ["r"] = b.R };
                keys.Add(k);
            }
            if (vals != null) foreach (var (kk, v) in vals) k[kk] = v;
        });
    }

    public static void RemoveKey(Document d, int i)
    {
        if (!Keys(d).Any(c => J.I(c["f"]) == i)) return;
        AnimCore.EditAnimOnly("카메라 키 지우기", a =>
        {
            var keys = J.Arr(J.Obj(a, "camera"), "keys");
            foreach (var k in keys.OfType<JsonObject>().Where(c => J.I(c["f"]) == i).ToList()) keys.Remove(k);
        });
    }

    /// <summary>원고 위에 지금 프레임의 카메라 영역 (모션 경로 도구 겹침 그림에서)</summary>
    public static void DrawOverlay(Graphics g, ViewXf v, Document d)
    {
        if (Keys(d).Count == 0 || !App.Get("showCamera", true)) return;
        var cc = At(d, AnimCore.Frame(d));
        if (cc == null) return;
        var cam = cc.Value;
        double w = d.W / cam.Z, hh = d.H / cam.Z, a = cam.R * Math.PI / 180;
        var pts = new[] { (-w / 2, -hh / 2), (w / 2, -hh / 2), (w / 2, hh / 2), (-w / 2, hh / 2) }.Select(p =>
        {
            double rx = p.Item1 * Math.Cos(a) - p.Item2 * Math.Sin(a), ry = p.Item1 * Math.Sin(a) + p.Item2 * Math.Cos(a);
            var (sx, sy) = v.ToScreen(cam.X * d.W + rx, cam.Y * d.H + ry);
            return new PointF((float)sx, (float)sy);
        }).ToArray();
        using var pen = new Pen(Color.FromArgb(0xe5, 0x48, 0x4d), 2) { DashPattern = new[] { 4f, 2.5f } };
        g.DrawPolygon(pen, pts);
        using var br = new SolidBrush(Color.FromArgb(0xe5, 0x48, 0x4d));
        using var f = new Font("Malgun Gothic", 9f);
        g.DrawString($"카메라 {Math.Round(cam.Z * 100)}%", f, br, pts[0].X + 4, pts[0].Y + 4);
    }
}
