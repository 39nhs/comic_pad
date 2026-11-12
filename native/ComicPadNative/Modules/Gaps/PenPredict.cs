// 펜 위치 예측 (웹 판 core/input.js 의 getPredictedEvents + tools/brush.js overlay, 설정 penFeel.predict):
// 그리는 동안 실제 선보다 조금 앞을 흐리게(55%) 미리 그려서 펜을 따라오는 느낌을 줄인다. 그림에는 남지 않는 화면 표시만.
// Windows 포인터 메시지에는 예측 점이 없어서, 최근 펜 점들의 속도·가속도로 한 화면 장(약 16ms) 앞까지 직접 내다본다.
// 두 번째 측면 버튼(penButton2, 웹 판과 같은 선택지): 펜 포인터의 세 번째 버튼 표시가 오면 그 동작으로.
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative;

namespace ComicPadNative.Modules.Gaps;

public sealed class PenPredict
{
    readonly List<(double x, double y, double t)> pts = new();   // 화면 좌표, 초
    (double x, double y)[] pred;
    public double LeadMs = 16;

    /// <summary>설정: penFeel.predict (기본 켬, 웹 판과 같음)</summary>
    public static bool Enabled()
    {
        try { return (App.Settings["penFeel"] as JsonObject)?["predict"] is not JsonNode n || n.GetValue<bool>(); } catch { return true; }
    }

    /// <summary>예측을 그리는 도구인지 (브러시 계열, 지우개 아님 — 웹 판 brush.js 의 overlay 와 같음)</summary>
    public static bool ForTool(ToolBase t) => t != null && t.Id == "brush";

    public void Reset() { pts.Clear(); pred = null; }

    /// <summary>펜 점 하나 (화면 좌표, QPC 시각)</summary>
    public void Add(double sx, double sy, long qpc)
    {
        double t = qpc / (double)System.Diagnostics.Stopwatch.Frequency;
        if (pts.Count > 0 && t - pts[^1].t < 0.0005) { pts[^1] = (sx, sy, pts[^1].t); }
        else pts.Add((sx, sy, t));
        while (pts.Count > 6) pts.RemoveAt(0);
        pred = Compute();
    }

    /// <summary>최근 점들로 앞을 내다봄: 속도(마지막 ~20ms 평균) + 약한 가속, 너무 멀면 자름</summary>
    (double x, double y)[] Compute()
    {
        if (pts.Count < 3) return null;
        var (x2, y2, t2) = pts[^1];
        int k = pts.Count - 2;
        while (k > 0 && t2 - pts[k].t < 0.012) k--;
        var (x1, y1, t1) = pts[k];
        double dt = t2 - t1;
        if (dt <= 0.001 || dt > 0.08) return null;
        double vx = (x2 - x1) / dt, vy = (y2 - y1) / dt;
        // 앞 구간 속도와 비교해 가속 (반만 반영 — 과하게 튀지 않게)
        double ax = 0, ay = 0;
        if (k > 0)
        {
            var (x0, y0, t0) = pts[k - 1 < 0 ? 0 : k - 1];
            double dt0 = t1 - t0;
            if (dt0 > 0.001 && dt0 < 0.08) { ax = (vx - (x1 - x0) / dt0) / ((dt + dt0) / 2) * 0.5; ay = (vy - (y1 - y0) / dt0) / ((dt + dt0) / 2) * 0.5; }
        }
        double speed = Math.Sqrt(vx * vx + vy * vy);
        if (speed < 30) return null;   // 거의 멈춤 (초당 30px 미만)
        var o = new (double, double)[3];
        double maxD = Math.Min(80, speed * LeadMs / 1000 * 1.5);
        for (int i = 0; i < 3; i++)
        {
            double h = LeadMs / 1000 * (i + 1) / 3;
            double px = vx * h + 0.5 * ax * h * h, py = vy * h + 0.5 * ay * h * h;
            double d = Math.Sqrt(px * px + py * py);
            if (d > maxD) { px *= maxD / d; py *= maxD / d; }
            o[i] = (x2 + px, y2 + py);
        }
        return o;
    }

    public (double x, double y)[] Points => pred;

    /// <summary>다시 그릴 범위 (화면)</summary>
    public Rectangle Bounds(double width)
    {
        if (pred == null || pts.Count == 0) return Rectangle.Empty;
        double x0 = pts[^1].x, y0 = pts[^1].y, x1 = x0, y1 = y0;
        foreach (var (x, y) in pred) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        int pad = (int)Math.Ceiling(width / 2) + 3;
        return Rectangle.FromLTRB((int)x0 - pad, (int)y0 - pad, (int)x1 + pad + 1, (int)y1 + pad + 1);
    }

    /// <summary>예측 선 (마지막 실제 점 → 예측 점들), 붓 색 55%</summary>
    public void Draw(Graphics g, double width, uint argb)
    {
        if (pred == null || pts.Count == 0) return;
        var c = Color.FromArgb((int)(0.55 * 255), (int)((argb >> 16) & 255), (int)((argb >> 8) & 255), (int)(argb & 255));
        using var pen = new Pen(c, (float)Math.Max(1, width)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        var ps = new PointF[pred.Length + 1];
        ps[0] = new PointF((float)pts[^1].x, (float)pts[^1].y);
        for (int i = 0; i < pred.Length; i++) ps[i + 1] = new PointF((float)pred[i].x, (float)pred[i].y);
        var old = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.DrawLines(pen, ps);
        g.SmoothingMode = old;
    }

    /// <summary>두 번째 측면 버튼 동작 (웹 판 penButton2: pan·eyedropper·eraser·radial·none, 기본 pan)</summary>
    public static string Button2Action() => App.Get("penButton2", "pan");
}
