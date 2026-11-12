// 합성기 확장 (Core/Compositor.cs Hooks) 구현:
//  - Hooks.Adjust : 조정 레이어 보정 (웹 filters.js adjustFilter 와 같은 계산)
//  - Hooks.Fx     : 레이어 효과 (웹 fxFilter: 그림자 = 알파 흐림+이동, 외부 광선 = 팽창+흐림, 테두리 = 팽창) — 바깥 여백까지 계산
//  - Hooks.Tone   : 톤화(망점, 웹 raster.js halftone) — 타일 단위 캐시, 그리는 중에도 바뀐 타일만 다시
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Filters;

public static class LayerEffects
{
    // ---- 조정 레이어 ----------------------------------------------------------------
    /// <summary>미리보기 중인 조정 레이어 (대화상자에서 값 바꾸는 중) → 그 값으로</summary>
    public static Layer PreviewLayer;
    public static FilterChain PreviewChain;

    public static void Adjust(uint[] buf, Rect r, Layer l, Document d)
    {
        FilterChain f;
        if (PreviewLayer == l && PreviewChain != null) f = PreviewChain;
        else
        {
            string type = (string)l.Meta?["type"];
            if (type == null) return;
            f = Svg.AdjustCached(type, l.Meta?["params"] as JsonObject);
        }
        f.Apply(buf);
    }

    // ---- 레이어 효과 ------------------------------------------------------------------
    [ThreadStatic] static HashSet<Layer> suppress;
    public static bool Suppressed(Layer l) => suppress != null && suppress.Contains(l);

    static bool On(JsonObject fx, string k, out JsonObject o) { o = fx?[k] as JsonObject; return o != null && (bool?)o["on"] == true; }
    static double K(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);   // 웹: (v*s).toFixed(2), s = 1

    /// <summary>효과가 닿는 여백(px)</summary>
    public static int Padding(JsonObject fx)
    {
        double p = 0;
        if (On(fx, "shadow", out var s)) p = Math.Max(p, Math.Max(Math.Abs(Svg.Num(s, "dx")), Math.Abs(Svg.Num(s, "dy"))) + 3 * K(Svg.Num(s, "blur", 10) / 2) + 2);
        if (On(fx, "glow", out var g)) { double sz = Svg.Num(g, "size", 16); p = Math.Max(p, Math.Round(K(sz / 3)) + 3 * K(sz / 2) + 2); }
        if (On(fx, "stroke", out var k)) p = Math.Max(p, Math.Round(K(Svg.Num(k, "size", 6))) + 1);
        return (int)Math.Ceiling(p);
    }

    public static void Fx(uint[] buf, Rect r, Layer l, Document d)
    {
        var fx = l.Fx;
        bool sh = On(fx, "shadow", out var so), gl = On(fx, "glow", out var go), st = On(fx, "stroke", out var sto);
        if (!sh && !gl && !st) return;
        int pad = Padding(fx);
        var R = r.Inflate(pad);
        float[] A = AlphaOf(l, d, R, r, buf);
        int W = R.W, H = R.H;
        float[] shadow = null, glow = null, stroke = null;
        int sdx = 0, sdy = 0;
        if (sh)
        {
            shadow = (float[])A.Clone();
            double sg = K(Svg.Num(so, "blur", 10) / 2);
            Raster.Blur(shadow, W, H, sg, sg);
            sdx = (int)Math.Round(K(Svg.Num(so, "dx", 8))); sdy = (int)Math.Round(K(Svg.Num(so, "dy", 8)));
        }
        if (gl)
        {
            double sz = Svg.Num(go, "size", 16);
            glow = (float[])A.Clone();
            Raster.Dilate(glow, W, H, (int)Math.Round(K(sz / 3)));
            double sg = K(sz / 2);
            Raster.Blur(glow, W, H, sg, sg);
        }
        if (st)
        {
            stroke = (float[])A.Clone();
            Raster.Dilate(stroke, W, H, (int)Math.Round(K(Svg.Num(sto, "size", 6))));
        }
        // feFlood (flood-opacity) — 미리 곱한 색
        static uint Flood(JsonObject o, bool useOpacity)
        {
            uint c = Px.FromHex((string)o["color"], 0xFF000000);
            return useOpacity ? Px.Scale(c, (uint)Math.Clamp(Svg.Num(o, "opacity", 1) * 255 + 0.5, 0, 255)) : c;
        }
        uint cs = sh ? Flood(so, true) : 0, cg = gl ? Flood(go, true) : 0, ck = st ? Flood(sto, false) : 0;
        for (int y = 0; y < r.H; y++)
        {
            int Y = y + pad;
            for (int x = 0; x < r.W; x++)
            {
                int X = x + pad, i = y * r.W + x;
                uint res = 0;
                if (shadow != null)
                {
                    int sx = X - sdx, sy = Y - sdy;
                    if (sx >= 0 && sy >= 0 && sx < W && sy < H) { float v = shadow[sy * W + sx]; if (v > 0) res = Px.Scale(cs, Raster.B(v * 255)); }
                }
                if (glow != null) { float v = glow[Y * W + X]; if (v > 0) res = Px.Over(Px.Scale(cg, Raster.B(v * 255)), res); }
                if (stroke != null) { float v = stroke[Y * W + X]; if (v > 0) res = Px.Over(Px.Scale(ck, Raster.B(v * 255)), res); }
                buf[i] = Px.Over(buf[i], res);
            }
        }
    }

    /// <summary>레이어(효과 없이)의 알파를 영역 R 에서 (0~1). r 안은 이미 받은 buf 를 그대로</summary>
    static float[] AlphaOf(Layer l, Document d, Rect R, Rect r, uint[] buf)
    {
        uint[] px;
        if (l.IsFolder)
        {
            // 폴더: 새 합성기로 폴더만 다시 그림 (그 동안 이 레이어 효과는 끔)
            suppress ??= new HashSet<Layer>();
            suppress.Add(l);
            try
            {
                var c = new Compositor();
                c.Render(d, new Rect(0, 0, 1, 1), new RenderOptions { Paper = false });
                px = c.RenderLayer(l, R, 0);
            }
            finally { suppress.Remove(l); }
        }
        else
        {
            Surface src = l.Tone != null ? Hooks.Tone(l, d) ?? l.Px : l.Px;
            px = src?.Read(R) ?? new uint[R.W * R.H];
            if (l.Mask != null && l.MaskOn)
            {
                var m = l.Mask.Read(R);
                for (int k = 0; k < px.Length; k++) if (px[k] != 0) px[k] = Px.Scale(px[k], m[k] >> 24);
            }
        }
        var A = new float[R.W * R.H];
        for (int k = 0; k < A.Length; k++) A[k] = (px[k] >> 24) / 255f;
        // r 부분은 실제로 그려진 것(변형 미리보기 등 반영)으로
        int ox = r.X - R.X, oy = r.Y - R.Y;
        for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++) A[(y + oy) * R.W + x + ox] = (buf[y * r.W + x] >> 24) / 255f;
        return A;
    }

    // ---- 톤화 (망점) --------------------------------------------------------------------
    sealed class ToneCache { public string Key; public Surface Src; public int Version; public Surface Out; public readonly HashSet<int> Dirty = new(); }
    static readonly Dictionary<Layer, ToneCache> tones = new();

    /// <summary>문서 영역이 바뀜 → 그 타일의 망점을 다음에 다시 계산</summary>
    public static void MarkDirty(Rect r)
    {
        lock (tones)
            foreach (var c in tones.Values)
                if (c.Src != null) foreach (int ti in c.Src.TilesIn(r)) c.Dirty.Add(ti);
    }
    public static void ClearToneCache() { lock (tones) tones.Clear(); }

    public static Surface Tone(Layer l, Document d)
    {
        if (l.Tone == null || l.Px == null) return l.Px;
        string key = l.Tone.ToJsonString();
        lock (tones)
        {
            if (!tones.TryGetValue(l, out var c) || c.Key != key || c.Src != l.Px || c.Version != l.Version || c.Out.W != l.Px.W || c.Out.H != l.Px.H)
            {
                c = new ToneCache { Key = key, Src = l.Px, Version = l.Version, Out = new Surface(l.Px.W, l.Px.H) };
                tones[l] = c;
                for (int ti = 0; ti < l.Px.TileCount; ti++) if (l.Px.TileAt(ti) != null) ToneTile(l.Px, c.Out, ti, l.Tone);
                return c.Out;
            }
            if (c.Dirty.Count > 0)
            {
                foreach (int ti in c.Dirty) ToneTile(l.Px, c.Out, ti, l.Tone);
                c.Dirty.Clear();
            }
            return c.Out;
        }
    }

    static void ToneTile(Surface src, Surface dst, int ti, JsonObject o)
    {
        var r = src.TileRect(ti);
        var t = src.TileAt(ti);
        if (t == null) { dst.Clear(r); return; }
        var outp = new uint[r.W * r.H];
        HalftoneRows(src.Read(r), outp, r, o);
        dst.Write(r, outp);
    }

    /// <summary>웹 halftone(): 어두운 정도만큼 점/선 망점 (문서 좌표 기준)</summary>
    public static void HalftoneRows(uint[] sd, uint[] outp, Rect r, JsonObject o)
    {
        uint col = Px.FromHex((string)o["color"] ?? "#000000", 0xFF000000) | 0xFF000000;
        double sp = Math.Max(2, Svg.Num(o, "spacing", 8)); if (Svg.Num(o, "spacing", 0) == 0) sp = 8;
        double a = Svg.Num(o, "angle", 0) * Math.PI / 180;
        double ca = Math.Cos(a) / sp, sa = Math.Sin(a) / sp;
        bool line = (string)o["shape"] == "line";
        for (int y = 0; y < r.H; y++)
        {
            int gy = r.Y + y;
            for (int x = 0; x < r.W; x++)
            {
                int i = y * r.W + x;
                uint c = sd[i];
                if (c == 0) { outp[i] = 0; continue; }
                Raster.Unpack(c, out int R, out int G, out int B, out int al);
                double dark = (1 - (0.299 * R + 0.587 * G + 0.114 * B) / 255) * al / 255;
                if (dark <= 0.01) { outp[i] = 0; continue; }
                int gx = r.X + x;
                double u = gx * ca + gy * sa, v = -gx * sa + gy * ca;
                double fu = u - Math.Floor(u) - 0.5, fv = v - Math.Floor(v) - 0.5;
                double tt = line ? Math.Abs(fv) * 2 : (fu * fu + fv * fv) * 2;
                outp[i] = dark >= 0.98 || dark > tt * (line ? 1 : 1.5708) ? col : 0;
            }
        }
    }
}
