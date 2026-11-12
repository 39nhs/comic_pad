// 애니 트랙 폴더 합성 (Hooks.AnimFolder) — 웹 판 compositor.js _renderFolder 와 같은 규칙:
//  지금 프레임에 노출된 셀만 + (화면에서만) 라이트 테이블·어니언 스킨, 모션 경로가 있으면 옮겨서
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public static class AnimRender
{
    public const string PrevColor = "#e0455a", NextColor = "#2fa866", LightColor = "#8a63d2";

    public static bool RenderFolder(Compositor c, uint[] x, Rect r, Layer f, Document d, RenderOptions o, int depth)
    {
        var kids = c.KidsOf(f);
        if (!AnimCore.IsTrack(f) || kids.Count == 0) return false;   // 보통 폴더처럼
        var a = d.Anim;
        int n = Math.Max(1, AnimCore.FrameCount(d));
        int frame = Math.Max(0, Math.Min(n - 1, o.Frame ?? AnimCore.Frame(d)));
        var xs = AnimCore.XsOf(f, d);
        var byId = new Dictionary<string, Layer>();
        foreach (var k in kids) byId[k.Id] = k;
        string IdAt(int g) => g >= 0 && g < xs.Count ? xs[g] : null;
        string curId = IdAt(frame);
        var full = new Rect(0, 0, d.W, d.H);
        bool nearest = PixelArt.IsArt(d);

        void Ghost(Layer cel, string color, double alpha, int? g)
        {
            if (cel == null || !cel.Visible || alpha <= 0) return;
            var mo = g == null ? null : Motion.At(f, g.Value, d);
            if (mo == null)
            {
                var t = c.RenderLayer(cel, r, depth + 1, color);
                Px.CompositeRow(x, 0, t, 0, t.Length, Blend.Normal, (float)Math.Clamp(alpha, 0, 1));
                return;
            }
            var m = mo.Value;
            Xform.DrawTransformed(x, r, sr => c.RenderLayer(cel, sr, depth + 1, color), full, m.T, (float)(alpha * m.Alpha), nearest);
        }

        // 라이트 테이블: 올려 둔 셀을 늘 흐리게
        if (o.Onion && !AnimCore.Playing && a["light"]?["ids"] is JsonArray lids && lids.Count > 0)
        {
            string lc = J.S(a["light"]["color"]) ?? LightColor;
            double lo = J.N(a["light"]["opacity"]) ?? 0.25;
            foreach (var id in lids.Select(J.S))
                if (id != null && id != curId && byId.TryGetValue(id, out var lcel)) Ghost(lcel, lc, lo, null);
        }
        if (o.Onion && J.T(a["onion"]) && !AnimCore.Playing)
        {
            bool loop = J.T(a["loop"]);
            // 앞뒤로 "다른 그림"인 셀만 (같은 셀을 길게 보여 주는 칸은 건너뜀)
            List<(string id, int g)> Collect(int dir, int count)
            {
                var outp = new List<(string, int)>();
                var seen = new HashSet<string> { Motion.At(f, frame, d) != null ? curId + "@" + frame : curId ?? "\0" };
                for (int k = 1; k < n && outp.Count < count; k++)
                {
                    int g = frame + dir * k;
                    if (g < 0 || g >= n) { if (!loop) break; g = ((g % n) + n) % n; }
                    var id = IdAt(g);
                    string key = Motion.At(f, g, d) != null ? id + "@" + g : id;   // 모션이 있으면 같은 그림도 프레임마다 다르게 보임
                    if (id != null && seen.Add(key)) outp.Add((id, g));
                }
                return outp;
            }
            var prev = Collect(-1, J.I(a["onionPrev"], 0));
            var next = Collect(1, J.I(a["onionNext"], 0));
            double baseA = J.N(a["onionOpacity"]) ?? 0.3, fall = J.N(a["onionFalloff"]) ?? 0.5;
            string pc = J.S(a["onionPrevColor"]) ?? PrevColor, nc = J.S(a["onionNextColor"]) ?? NextColor;
            // 먼 것부터 (가까울수록 진하게 위에)
            for (int k = prev.Count - 1; k >= 0; k--) Ghost(byId.GetValueOrDefault(prev[k].id), pc, baseA * Math.Pow(1 - fall, k), prev[k].g);
            for (int k = next.Count - 1; k >= 0; k--) Ghost(byId.GetValueOrDefault(next[k].id), nc, baseA * Math.Pow(1 - fall, k), next[k].g);
        }
        if (curId == null || !byId.TryGetValue(curId, out var cel)) return true;
        var mo = Motion.At(f, frame, d);
        if (mo == null) { c.RenderList(x, r, new List<Layer> { cel }, depth + 1); return true; }
        // 모션이 있는 프레임: 따로 그린 뒤 옮겨서 (레이어 불투명도·합성·효과는 그대로)
        var m = mo.Value;
        Xform.DrawTransformed(x, r, sr => { var b = new uint[sr.W * sr.H]; c.RenderList(b, sr, new List<Layer> { cel }, depth + 1); return b; }, full, m.T, (float)m.Alpha, nearest);
        return true;
    }
}
