// 펜 필기감 진단 (웹 판 ui/pendiag.js): 이 기기에서 펜 신호가 얼마나 자주·얼마나 고르게 오는지 재고, 보정값(settings.penFeel)을 맞춰 준다.
// 펜 점은 Windows 포인터 메시지(WM_POINTER)에서 직접: 묶여 온 점(history) 전부, 1픽셀보다 작은 위치(HIMETRIC), 필압·기울기, 입력 시각(QPC).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Extras;

public sealed record PenPt(double T, double X, double Y, double P, double Tx, double Ty);

public static class PenDiag
{
    public static readonly (string id, string title, string help)[] Steps =
    {
        ("free", "1. 자유롭게 그어 보세요", "평소처럼 빠르게·느리게 여러 번 그어 주세요 (5초 이상)."),
        ("light", "2. 가장 약하게", "선이 겨우 그어질 만큼 가장 약하게 몇 번 그어 주세요."),
        ("strong", "3. 가장 세게", "평소 가장 세게 누를 때만큼 눌러서 몇 번 그어 주세요."),
        ("hold", "4. 가만히 대고 있기", "펜을 화면에 댄 채 2초 동안 움직이지 마세요."),
    };

    public static double Pct(IEnumerable<double> arr, double q) { var a = arr.OrderBy(x => x).ToList(); return a.Count == 0 ? 0 : a[Math.Min(a.Count - 1, (int)Math.Floor(q * a.Count))]; }
    public static double Median(IEnumerable<double> a) => Pct(a, 0.5);
    public static double Std(IList<double> a) { if (a.Count < 2) return 0; double m = a.Average(); return Math.Sqrt(a.Sum(v => (v - m) * (v - m)) / a.Count); }

    public sealed class Meta { public int Events, Coalesced; public bool Predicted, Tilt; public List<double> Lat = new(); public string Type = ""; }

    public sealed record Result(double Rate, int Levels, double Light, double Strong, double Jitter, double PNoise, double Lat, bool PressureOK, JsonObject Rec);

    /// <summary>모은 획으로 결과·추천값 계산 (웹 판 analyze 와 같은 식)</summary>
    public static Result Analyze(Dictionary<string, List<List<PenPt>>> data, Meta meta, double scale = 1)
    {
        var all = data["free"].SelectMany(s => s).ToList();
        var rates = data["free"].Where(s => s.Count > 10).Select(s => (s.Count - 1) / ((s[^1].T - s[0].T) / 1000)).Where(double.IsFinite).ToList();
        double rate = Median(rates);
        var ps = all.Select(q => q.P).ToList();
        int levels = ps.Select(p => p.ToString("0.0000")).Distinct().Count();
        var lightP = data["light"].SelectMany(s => s).Select(q => q.P).Where(p => p > 0).ToList();
        var strongP = data["strong"].SelectMany(s => s).Select(q => q.P).Where(p => p > 0).ToList();
        // 대는 순간(처음 0.3초)은 빼고 (웹 판: 모든 가만히 획을 이어 붙인 첫 점 기준)
        var holdAll = data["hold"].SelectMany(s => s).ToList();
        var hold = holdAll.Where(q => holdAll.Count > 0 && q.T - holdAll[0].T > 300).ToList();
        double jitter = Math.Sqrt(Math.Pow(Std(hold.Select(q => q.X).ToList()), 2) + Math.Pow(Std(hold.Select(q => q.Y).ToList()), 2)) / scale;
        double pNoise = Std(hold.Skip(1).Select((q, i) => q.P - hold[i].P).ToList());
        double lat = Median(meta.Lat);
        bool pressureOK = levels > 20 && Pct(ps, 0.9) - Pct(ps, 0.1) > 0.05;
        double pmin = pressureOK ? Math.Max(0, Math.Min(0.4, Pct(lightP, 0.25) * 0.8)) : 0;
        double pmax = pressureOK ? Math.Min(1, Math.Max(pmin + 0.3, Pct(strongP, 0.9))) : 1;
        if (pmax - pmin < 0.3) pmax = Math.Min(1, pmin + 0.3);
        double euroK = jitter < 0.25 ? 1.4 : jitter < 0.6 ? 1.1 : jitter < 1.2 ? 0.8 : 0.55;
        double pressureSmooth = Math.Max(0.15, Math.Min(0.65, 0.2 + pNoise * 12));
        var rec = new JsonObject { ["pmin"] = Math.Round(pmin, 3), ["pmax"] = Math.Round(pmax, 3), ["euroK"] = euroK, ["pressureSmooth"] = Math.Round(pressureSmooth, 2), ["predict"] = meta.Predicted, ["startRamp"] = true };
        return new Result(rate, levels, Median(lightP), Pct(strongP, 0.9), jitter, pNoise, lat, pressureOK, rec);
    }

    public static void ApplyRec(JsonObject rec)
    {
        var f = U.Obj("penFeel");
        foreach (var (k, v) in rec) f[k] = v?.DeepClone();
        App.NotifySettings();
        App.Toast("이 펜에 맞춘 값으로 바꿨습니다. 그려 보고 어색하면 \"기본값으로\"를 누르세요");
    }
    public static void ResetFeel()
    {
        var f = U.Obj("penFeel");
        foreach (var k in new[] { "pmin", "pmax", "euroK" }) f.Remove(k);
        f["pressureSmooth"] = 0.35; f["startRamp"] = true; f["predict"] = true;
        App.NotifySettings();
        App.Toast("필기감을 기본값으로 되돌렸습니다");
    }

    public static void Dialog()
    {
        var data = Steps.ToDictionary(s => s.id, _ => new List<List<PenPt>>());
        var meta = new Meta();
        int step = 0;
        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Opt.Bg, Padding = new Padding(8) };
        var title = new Label { AutoSize = true, Font = new Font("Malgun Gothic", 11f, FontStyle.Bold) };
        var help = new Label { AutoSize = true, ForeColor = Opt.Muted, MaximumSize = new Size(660, 0) };
        var pad = new PenPad { Width = 640, Height = 300 };
        var result = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Opt.Bg };
        Button next = null, redo = null;
        bool Enough()
        {
            var id = Steps[step].id; var st = data[id];
            if (id == "hold") return st.Any(s => s[^1].T - s[0].T > 1800);
            if (id == "free") return st.Sum(s => s.Count) > 300;
            return st.Count >= 2;
        }
        void Sync()
        {
            title.Text = Steps[step].title;
            help.Text = Steps[step].help + (Enough() ? " ✓ 충분합니다" : "");
            next.Text = step < Steps.Length - 1 ? "다음 단계" : "결과 보기";
            next.Enabled = Enough();
        }
        pad.Meta = meta;
        pad.StrokeDone += s => { if (s.Count > 1) data[Steps[step].id].Add(s); Sync(); };
        void Show()
        {
            result.Controls.Clear();
            var r = Analyze(data, meta, 1);
            var rows = new (string k, string v, string note)[]
            {
                ("입력 장치", meta.Type == "" ? "?" : meta.Type, meta.Type == "pen" ? "" : meta.Type == "mouse" ? "펜이 마우스로 들어옵니다 → 와콤·휴이온·XP-Pen 드라이버 설정에서 \"Windows Ink\" 를 켜야 필압·기울기가 됩니다" : "펜이 아니라 손가락(터치)으로 측정되었습니다"),
                ("초당 점 개수", $"{Math.Round(r.Rate)}개", r.Rate >= 200 ? "아주 촘촘함" : r.Rate >= 100 ? "보통" : "적음 → 보정을 조금 더"),
                ("한 번에 묶여 오는 점", (meta.Coalesced / (double)Math.Max(1, meta.Events)).ToString("0.0") + "개", "모두 빠짐없이 사용 중"),
                ("필압 단계", $"{r.Levels}단계", r.PressureOK ? "" : "필압이 거의 안 들어옵니다 (Windows Ink/드라이버 확인)"),
                ("가장 약하게 / 세게", $"{r.Light:0.00} / {r.Strong:0.00}", "이 범위를 0~100% 로 늘려 씀"),
                ("펜 끝 떨림 (가만히)", $"{r.Jitter:0.00}px", r.Jitter < 0.6 ? "안정적" : "떨림이 있어 보정을 강하게"),
                ("필압 흔들림 (가만히)", r.PNoise.ToString("0.0000"), ""),
                ("기울기 인식", meta.Tilt ? "됨" : "안 됨", ""),
                ("입력 지연 (화면 처리)", $"{r.Lat:0.0}ms", meta.Predicted ? "위치 예측 사용 가능" : "위치 예측 지원 안 함"),
            };
            var table = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Opt.Bg };
            foreach (var (k, v, note) in rows)
            {
                table.Controls.Add(new Label { Text = k, AutoSize = true });
                table.Controls.Add(new Label { Text = v, AutoSize = true, Font = new Font("Malgun Gothic", 9f, FontStyle.Bold) });
                table.Controls.Add(new Label { Text = note, AutoSize = true, ForeColor = Opt.Muted, MaximumSize = new Size(380, 0) });
            }
            result.Controls.Add(table);
            var rec = r.Rec;
            result.Controls.Add(Opt.Help($"추천: 필압 범위 {rec["pmin"]}~{rec["pmax"]}, 떨림 보정 배율 {rec["euroK"]}, 필압 다듬기 {Math.Round(U.Num(rec["pressureSmooth"]) * 100)}%, 위치 예측 {((bool)rec["predict"] ? "켬" : "끔")}"));
            result.Controls.Add(Opt.Row(Opt.Button("추천값 적용", () => ApplyRec(rec), true), Opt.Button("기본값으로", ResetFeel),
                Opt.Button("처음부터 다시", () => { step = 0; foreach (var k in data.Keys) data[k].Clear(); meta.Events = meta.Coalesced = 0; meta.Lat.Clear(); result.Controls.Clear(); pad.ClearPad(); Sync(); })));
        }
        next = Opt.Button("다음 단계", () => { if (step < Steps.Length - 1) { step++; pad.ClearPad(); Sync(); } else Show(); }, true);
        redo = Opt.Button("이 단계 다시", () => { data[Steps[step].id].Clear(); pad.ClearPad(); Sync(); });
        root.Controls.Add(title); root.Controls.Add(help); root.Controls.Add(pad); root.Controls.Add(Opt.Row(redo, next)); root.Controls.Add(result);
        using var f = Dlg.Modal("펜 필기감 진단", root, 720, 820);
        Sync();
        f.ShowDialog(App.Main);
    }
}

/// <summary>펜 시험판: WM_POINTER 를 직접 받아 점을 모두 기록하고 굵기(필압)로 그림</summary>
public sealed class PenPad : Control
{
    public PenDiag.Meta Meta;
    public event Action<List<PenPt>> StrokeDone;
    Bitmap buf;
    List<PenPt> cur;
    uint curId;
    readonly double qpcMs = 1000.0 / Stopwatch.Frequency;

    public PenPad()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Color.White; Cursor = Cursors.Cross;
    }
    public void ClearPad() { buf?.Dispose(); buf = null; Invalidate(); }
    Bitmap Buf()
    {
        if (buf == null || buf.Width != Width || buf.Height != Height)
        {
            buf?.Dispose(); buf = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            using var g = Graphics.FromImage(buf); g.Clear(Color.White);
        }
        return buf;
    }
    protected override void OnPaint(PaintEventArgs e) => e.Graphics.DrawImageUnscaled(Buf(), 0, 0);
    protected override void Dispose(bool disposing) { if (disposing) buf?.Dispose(); base.Dispose(disposing); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach (int k in new[] { 2, 3, 4, 5, 6 }) { int no = 0; SetWindowFeedbackSetting(Handle, k, 0, 4, ref no); }   // 펜 길게 누르기 오른쪽 클릭·물결 끔
    }

    const int WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247, WM_POINTERCAPTURECHANGED = 0x024C;
    const int PT_TOUCH = 2, PT_PEN = 3, PT_MOUSE = 4;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POINTERDOWN || m.Msg == WM_POINTERUPDATE || m.Msg == WM_POINTERUP)
        {
            uint id = (uint)((long)m.WParam & 0xFFFF);
            if (HandlePointer(m.Msg, id)) { m.Result = IntPtr.Zero; return; }
        }
        else if (m.Msg == WM_POINTERCAPTURECHANGED && cur != null) { End(); }
        base.WndProc(ref m);
    }

    bool HandlePointer(int msg, uint id)
    {
        if (!GetPointerType(id, out int type)) return false;
        var pts = new List<(POINTER_INFO pi, double p, int tx, int ty)>();
        if (type == PT_PEN)
        {
            uint n = 1; var one = new POINTER_PEN_INFO[1];
            if (!GetPointerPenInfo(id, ref one[0])) return false;
            n = Math.Max(1, one[0].pointerInfo.historyCount);
            var hist = new POINTER_PEN_INFO[n];
            if (n > 1 && GetPointerPenInfoHistory(id, ref n, hist)) { for (int i = (int)n - 1; i >= 0; i--) pts.Add((hist[i].pointerInfo, (hist[i].penMask & 1) != 0 ? hist[i].pressure / 1024.0 : 0.5, hist[i].tiltX, hist[i].tiltY)); }
            else pts.Add((one[0].pointerInfo, (one[0].penMask & 1) != 0 ? one[0].pressure / 1024.0 : 0.5, one[0].tiltX, one[0].tiltY));
        }
        else
        {
            var one = new POINTER_INFO();
            if (!GetPointerInfo(id, ref one)) return false;
            uint n = Math.Max(1, one.historyCount);
            var hist = new POINTER_INFO[n];
            if (n > 1 && GetPointerInfoHistory(id, ref n, hist)) { for (int i = (int)n - 1; i >= 0; i--) pts.Add((hist[i], 0.5, 0, 0)); }
            else pts.Add((one, 0.5, 0, 0));
        }
        string tname = type == PT_PEN ? "pen" : type == PT_TOUCH ? "touch" : "mouse";
        if (msg == WM_POINTERDOWN)
        {
            Capture = true; Focus();
            Meta.Type = tname; curId = id;
            cur = new List<PenPt> { ToPt(pts[^1]) };
            return true;
        }
        if (cur == null || id != curId) return false;
        if (msg == WM_POINTERUPDATE)
        {
            Meta.Events++;
            Meta.Coalesced += Math.Max(1, pts.Count);
            ulong qpc = pts[^1].pi.PerformanceCount;
            if (qpc > 0) { double lat = (Stopwatch.GetTimestamp() - (long)qpc) * qpcMs; if (lat >= 0 && lat < 1000) Meta.Lat.Add(lat); }
            using var g = Graphics.FromImage(Buf());
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            foreach (var p in pts)
            {
                var q = ToPt(p);
                if (q.Tx != 0 || q.Ty != 0) Meta.Tilt = true;
                var last = cur[^1];
                using var pen = new Pen(Color.FromArgb(34, 34, 34), (float)(1 + q.P * 10)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                g.DrawLine(pen, (float)last.X, (float)last.Y, (float)q.X, (float)q.Y);
                cur.Add(q);
            }
            Invalidate();
            return true;
        }
        End();
        return true;
    }

    void End() { var s = cur; cur = null; Capture = false; if (s != null) StrokeDone?.Invoke(s); }

    PenPt ToPt((POINTER_INFO pi, double p, int tx, int ty) a)
    {
        double t = a.pi.PerformanceCount > 0 ? a.pi.PerformanceCount * qpcMs : a.pi.dwTime;
        // HIMETRIC → 화면 픽셀 (1픽셀보다 작은 위치)
        double sx = a.pi.ptPixelLocation.X, sy = a.pi.ptPixelLocation.Y;
        if (GetPointerDeviceRects(a.pi.sourceDevice, out var dr, out var disp) && dr.Right > dr.Left && dr.Bottom > dr.Top)
        {
            sx = disp.Left + (a.pi.ptHimetricLocationRaw.X - dr.Left) * (double)(disp.Right - disp.Left) / (dr.Right - dr.Left);
            sy = disp.Top + (a.pi.ptHimetricLocationRaw.Y - dr.Top) * (double)(disp.Bottom - disp.Top) / (dr.Bottom - dr.Top);
            // 장치 좌표가 맞지 않으면(회전 화면 등) 정수 위치로
            if (Math.Abs(sx - a.pi.ptPixelLocation.X) > 2 || Math.Abs(sy - a.pi.ptPixelLocation.Y) > 2) { sx = a.pi.ptPixelLocation.X; sy = a.pi.ptPixelLocation.Y; }
        }
        var o = PointToScreen(Point.Empty);
        return new PenPt(t, sx - o.X, sy - o.Y, a.p, a.tx, a.ty);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public int pointerType; public uint pointerId; public uint frameId; public int pointerFlags;
        public IntPtr sourceDevice; public IntPtr hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount; public int InputData; public uint dwKeyStates; public ulong PerformanceCount; public int ButtonChangeType;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_PEN_INFO { public POINTER_INFO pointerInfo; public int penFlags, penMask; public uint pressure, rotation; public int tiltX, tiltY; }
    [DllImport("user32.dll")] static extern bool GetPointerType(uint id, out int type);
    [DllImport("user32.dll")] static extern bool GetPointerInfo(uint id, ref POINTER_INFO info);
    [DllImport("user32.dll")] static extern bool GetPointerInfoHistory(uint id, ref uint count, [Out] POINTER_INFO[] info);
    [DllImport("user32.dll")] static extern bool GetPointerPenInfo(uint id, ref POINTER_PEN_INFO info);
    [DllImport("user32.dll")] static extern bool GetPointerPenInfoHistory(uint id, ref uint count, [Out] POINTER_PEN_INFO[] info);
    [DllImport("user32.dll")] static extern bool GetPointerDeviceRects(IntPtr device, out RECT pointerDeviceRect, out RECT displayRect);
    [DllImport("user32.dll")] static extern bool SetWindowFeedbackSetting(IntPtr hwnd, int feedback, int flags, int size, ref int config);
}
