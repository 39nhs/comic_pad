// 사진·배경에서 소실점 찾기 → 퍼스 자 (웹 판 ai/vpdetect.js 를 그대로 옮김: AI 모델 없이 계산만 — 선분 검출 + RANSAC)
// 결과 자는 doc.Extra["rulers"] 에 웹 판과 같은 모양으로: { type: 'perspective', vps: [{x, y}…], fromPhoto: true }
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public sealed class Seg { public double X1, Y1, X2, Y2, Len, Ux, Uy, Mx, My; }
public sealed class VP { public double X, Y, Support; public List<Seg> Segs; }

public static class VpDetect
{
    /// <summary>그림(RGBA 바이트, 행 우선)에서 직선 조각 찾기 (기울기 방향이 비슷한 가장자리 점을 이어 붙여 직선으로)</summary>
    public static List<Seg> DetectSegments(byte[] d, int W, int H)
    {
        var g = new float[W * H];
        for (int i = 0; i < W * H; i++) g[i] = d[i * 4] * 0.299f + d[i * 4 + 1] * 0.587f + d[i * 4 + 2] * 0.114f;
        // 살짝 흐리게 (잡음 줄이기)
        var b = new float[W * H];
        for (int y = 1; y < H - 1; y++) for (int x = 1; x < W - 1; x++)
        {
            int i = y * W + x;
            b[i] = (g[i] * 4 + g[i - 1] * 2 + g[i + 1] * 2 + g[i - W] * 2 + g[i + W] * 2 + g[i - W - 1] + g[i - W + 1] + g[i + W - 1] + g[i + W + 1]) / 16;
        }
        var mag = new float[W * H]; var ang = new float[W * H];
        double sum = 0; long cnt = 0;
        // 그림 가장자리 2px 은 빼기 (흐림 계산이 안 된 테두리가 선으로 잡히지 않게)
        for (int y = 2; y < H - 2; y++) for (int x = 2; x < W - 2; x++)
        {
            int i = y * W + x;
            float gx = b[i - W + 1] + 2 * b[i + 1] + b[i + W + 1] - b[i - W - 1] - 2 * b[i - 1] - b[i + W - 1];
            float gy = b[i + W - 1] + 2 * b[i + W] + b[i + W + 1] - b[i - W - 1] - 2 * b[i - W] - b[i - W + 1];
            mag[i] = MathF.Sqrt(gx * gx + gy * gy); ang[i] = MathF.Atan2(gy, gx);
            sum += mag[i]; cnt++;
        }
        double thr = Math.Max(40, cnt == 0 ? 0 : sum / cnt * 2.2);
        var used = new byte[W * H];
        var segs = new List<Seg>();
        double tol = Math.PI / 10;
        bool Same(double a, double c) { double dd = Math.Abs(a - c) % Math.PI; if (dd > Math.PI / 2) dd = Math.PI - dd; return dd < tol; }
        var stack = new Stack<int>(); var pts = new List<int>();
        for (int s = 0; s < W * H; s++)
        {
            if (used[s] != 0 || mag[s] < thr) continue;
            // 같은 방향의 가장자리 점 묶기
            double a0 = ang[s]; pts.Clear();
            stack.Clear(); stack.Push(s); used[s] = 1;
            while (stack.Count > 0 && pts.Count < 4000)
            {
                int i = stack.Pop(); pts.Add(i);
                int x = i % W, y = i / W;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 1 || yy < 1 || xx >= W - 1 || yy >= H - 1) continue;
                    int j = yy * W + xx;
                    if (used[j] == 0 && mag[j] >= thr * 0.6 && Same(ang[j], a0)) { used[j] = 1; stack.Push(j); }
                }
            }
            if (pts.Count < 18) continue;
            // 주성분으로 직선 맞추기
            double mx = 0, my = 0;
            foreach (int i in pts) { mx += i % W; my += i / W; }
            mx /= pts.Count; my /= pts.Count;
            double sxx = 0, syy = 0, sxy = 0;
            foreach (int i in pts) { double dx = i % W - mx, dy = i / W - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
            double tr = sxx + syy, det = sxx * syy - sxy * sxy, l1 = tr / 2 + Math.Sqrt(Math.Max(0, tr * tr / 4 - det)), l2 = tr - l1;
            if (l2 / Math.Max(1e-6, l1) > 0.02) continue;                      // 곧지 않음
            double t = Math.Atan2(l1 - sxx, sxy != 0 ? sxy : 1e-9), ux = Math.Cos(t), uy = Math.Sin(t);
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (int i in pts) { double p = (i % W - mx) * ux + (i / W - my) * uy; if (p < lo) lo = p; if (p > hi) hi = p; }
            double len = hi - lo;
            if (len < Math.Max(20, Math.Min(W, H) * 0.04)) continue;
            segs.Add(new Seg { X1 = mx + ux * lo, Y1 = my + uy * lo, X2 = mx + ux * hi, Y2 = my + uy * hi, Len = len, Ux = ux, Uy = uy, Mx = mx, My = my });
        }
        return segs;
    }

    /// <summary>선분 → 소실점들 (RANSAC, 웹 판과 같은 난수열). 거의 평행(무한대)한 무리는 빼고</summary>
    public static List<VP> FindVanishingPoints(List<Seg> segs, int W, int H, int maxVP = 3)
    {
        var outp = new List<VP>();
        var rest = segs.ToList();
        double angTol = 2.2 * Math.PI / 180;
        double far = Math.Max(W, H) * 25;
        static double[] Line(Seg s) => new[] { s.Y1 - s.Y2, s.X2 - s.X1, s.X1 * s.Y2 - s.X2 * s.Y1 };   // 동차 좌표 직선
        bool Supports(Seg s, double vx, double vy)
        {
            // 선분 가운데에서 소실점 쪽 방향과 선분 방향의 각도
            double dx = vx - s.Mx, dy = vy - s.My, l = Math.Sqrt(dx * dx + dy * dy); if (l == 0) l = 1;
            double c = Math.Abs((dx * s.Ux + dy * s.Uy) / l);
            if (Math.Acos(Math.Min(1, c)) >= angTol) return false;
            // 선 끝이 그 점에 닿아 있으면 소실점이 아니라 모서리(선이 만나는 곳)
            double e = Math.Min(Hyp(vx - s.X1, vy - s.Y1), Hyp(vx - s.X2, vy - s.Y2));
            return e > Math.Max(12, s.Len * 0.25);
        }
        long seed = 12345;
        double Rnd() { seed = seed * 16807 % 2147483647; return seed / 2147483647.0; }
        for (int k = 0; k < maxVP && rest.Count >= 3; k++)
        {
            double total = rest.Sum(s => s.Len);
            (double sc, double vx, double vy)? best = null;
            int tries = Math.Min(1500, rest.Count * rest.Count);
            for (int t = 0; t < tries; t++)
            {
                var a = rest[(int)(Rnd() * rest.Count)]; var c = rest[(int)(Rnd() * rest.Count)];
                if (a == c) continue;
                var L1 = Line(a); var L2 = Line(c);
                double p0 = L1[1] * L2[2] - L1[2] * L2[1], p1 = L1[2] * L2[0] - L1[0] * L2[2], p2 = L1[0] * L2[1] - L1[1] * L2[0];
                if (Math.Abs(p2) < 1e-9) continue;
                double vx = p0 / p2, vy = p1 / p2;
                if (Hyp(vx - W / 2.0, vy - H / 2.0) > far) continue;          // 사실상 평행
                // 선분 위(그림 안 가운데)에 걸린 교점은 소실점이 아님
                if (vx > 0 && vx < W && vy > 0 && vy < H && Hyp(vx - a.Mx, vy - a.My) < a.Len && Hyp(vx - c.Mx, vy - c.My) < c.Len) continue;
                double sc = 0;
                foreach (var s in rest) if (Supports(s, vx, vy)) sc += s.Len;
                if (best == null || sc > best.Value.sc) best = (sc, vx, vy);
            }
            if (best == null || best.Value.sc < total * 0.12 || best.Value.sc < Math.Min(W, H) * 0.6) break;
            if (outp.Count > 0 && best.Value.sc < outp[0].Support * 0.3) break;             // 첫 소실점보다 훨씬 약하면 우연
            var bv = best.Value;
            // 받쳐 주는 선분들로 다시 맞추기 (최소제곱)
            var inl = rest.Where(s => Supports(s, bv.vx, bv.vy)).ToList();
            // 서로 다른 선 3개 이상이 모여야 소실점 (한 직선이 끊겨 여러 조각인 것은 하나로 침)
            int dirs = inl.Select(q => (int)JsRound(Math.Atan2(q.My - bv.vy, q.Mx - bv.vx) * 180 / Math.PI)).Distinct().Count();
            // 그림 안쪽 점은 우연히 몇 선이 만나기 쉬우니 더 많은 선이 모여야 인정 (1점 투시는 선이 많이 모임)
            bool inside = bv.vx > 0 && bv.vx < W && bv.vy > 0 && bv.vy < H;
            if (inl.Count < 4 || dirs < (inside ? 5 : 3)) { rest = rest.Where(q => !inl.Contains(q)).ToList(); k--; if (rest.Count < 3) break; continue; }
            double A = 0, B = 0, C = 0, D = 0, E = 0;
            foreach (var s in inl)
            {
                var L = Line(s); double n = Hyp(L[0], L[1]); if (n == 0) n = 1; double w = s.Len;
                double na = L[0] / n, nb = L[1] / n, nc = L[2] / n;
                A += w * na * na; B += w * na * nb; C += w * nb * nb; D += -w * na * nc; E += -w * nb * nc;
            }
            double det = A * C - B * B;
            double fx = bv.vx, fy = bv.vy;
            if (Math.Abs(det) > 1e-9) { fx = (D * C - B * E) / det; fy = (A * E - B * D) / det; }
            outp.Add(new VP { X = fx, Y = fy, Support = bv.sc, Segs = inl });
            rest = rest.Where(s => !inl.Contains(s)).ToList();
        }
        return outp;
    }
    static double Hyp(double a, double b) => Math.Sqrt(a * a + b * b);
    static double JsRound(double v) => Math.Floor(v + 0.5);   // 자바스크립트 Math.round 와 같게

    /// <summary>문서 → (줄인 그림 RGBA, 크기, 배율). 긴 쪽 900px</summary>
    public static unsafe (byte[] rgba, int w, int h, double s) Prepare(Document d, Compositor comp = null)
    {
        double s = Math.Min(1, 900.0 / Math.Max(d.W, d.H));
        using var b = Img.Flatten(d, s, true, comp);
        int w = b.Width, h = b.Height;
        var rgba = new byte[w * h * 4];
        var bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)bd.Scan0 + y * bd.Stride;
                for (int x = 0; x < w; x++) { int o = (y * w + x) * 4; rgba[o] = row[x * 4 + 2]; rgba[o + 1] = row[x * 4 + 1]; rgba[o + 2] = row[x * 4]; rgba[o + 3] = row[x * 4 + 3]; }
            }
        }
        finally { b.UnlockBits(bd); }
        return (rgba, w, h, s);
    }

    /// <summary>찾기 → 문서 좌표의 소실점들 (선분도 문서 좌표로)</summary>
    public static List<VP> Detect(Document d, Compositor comp = null)
    {
        var (rgba, w, h, s) = Prepare(d, comp);
        var segs = DetectSegments(rgba, w, h);
        return FindVanishingPoints(segs, w, h, 3).Select(v => new VP
        {
            X = v.X / s, Y = v.Y / s, Support = v.Support,
            Segs = v.Segs.Select(q => new Seg { X1 = q.X1 / s, Y1 = q.Y1 / s, X2 = q.X2 / s, Y2 = q.Y2 / s, Len = q.Len / s }).ToList(),
        }).ToList();
    }

    /// <summary>자 하나를 rulers 에 더함 (실행취소 가능). 웹 판과 같은 JSON</summary>
    public static JsonObject AddRule(Document d, List<VP> vps)
    {
        var rule = new JsonObject { ["type"] = "perspective", ["vps"] = new JsonArray(vps.Select(v => (JsonNode)new JsonObject { ["x"] = v.X, ["y"] = v.Y }).ToArray()), ["fromPhoto"] = true };
        var next = d.Extra["rulers"] is JsonArray a ? (JsonArray)a.DeepClone() : new JsonArray();
        next.Add(rule.DeepClone());
        ExtraEdit.Set(d, "rulers", next, "사진에서 퍼스 자");
        return rule;
    }

    public static void RulerFromPhoto()
    {
        var d = App.Doc; if (d == null) return;
        List<VP> vps = null;
        U.Task("소실점 찾는 중", () => vps = Detect(d));
        if (vps == null || vps.Count == 0) { App.Toast("곧은 선(건물·가구·바닥 무늬 등)이 충분하지 않아 소실점을 찾지 못했습니다"); return; }
        AddRule(d, vps);
        App.Invalidate(Rect.Empty);
        App.Toast($"{vps.Count}점 투시 자를 만들었습니다 (빨강·파랑·초록 선 = 찾은 소실선). 자 도구로 점을 옮겨 다듬을 수 있습니다");
        ShowPreview(d, vps);
    }

    /// <summary>찾은 소실선을 그림 위에 보여 주는 창 (그림판 겹침 그림 대신)</summary>
    static void ShowPreview(Document d, List<VP> vps)
    {
        double s = Math.Min(1, 900.0 / Math.Max(d.W, d.H));
        var img = Img.Flatten(d, s, true);
        var colors = new[] { ColorTranslator.FromHtml("#e5484d"), ColorTranslator.FromHtml("#3d7be0"), ColorTranslator.FromHtml("#2fa866") };
        using (var g = Graphics.FromImage(img))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int k = 0; k < vps.Count; k++)
            {
                using var pen = new Pen(colors[k % 3], 2);
                foreach (var q in vps[k].Segs.Take(80)) g.DrawLine(pen, (float)(q.X1 * s), (float)(q.Y1 * s), (float)(q.X2 * s), (float)(q.Y2 * s));
            }
        }
        var pb = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(60, 60, 66) };
        var info = string.Join("   ", vps.Select((v, i) => $"{new[] { "빨강", "파랑", "초록" }[i % 3]}: ({v.X:0}, {v.Y:0})"));
        var root = new Panel(); var lab = new Label { Text = "찾은 소실점 " + info, Dock = DockStyle.Top, Height = 24, ForeColor = Opt.Fg, Padding = new Padding(6, 4, 0, 0) };
        pb.Dock = DockStyle.Fill; root.Controls.Add(pb); root.Controls.Add(lab);
        var f = Dlg.Modal("찾은 소실선", root, 760, 760);
        f.FormClosed += (_, _) => img.Dispose();
        var close = new System.Windows.Forms.Timer { Interval = 7000 };
        close.Tick += (_, _) => { close.Stop(); close.Dispose(); if (!f.IsDisposed) f.Close(); };
        close.Start();
        f.Show(App.Main);
    }
}
