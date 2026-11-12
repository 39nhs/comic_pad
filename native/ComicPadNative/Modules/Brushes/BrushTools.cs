// 브러시 / 지우개 도구 (웹 tools/brush.js 의 BrushTool)
//  - 프리셋(분류 brush / eraser), 대칭, 선택 영역, 투명 픽셀 잠금, 마스크 편집, 팔레트 고정 작품
//  - Shift 를 누르고 시작 = 직선 (Ctrl = 15도 단위), 선 끝에서 펜을 멈추고 있으면 직선으로 (설정 holdLine)
//  - 벡터 레이어: 그린 선을 meta.strokes 에 기록, 지우개는 선을 지움 (닿은 부분만 / 교차점까지 / 선 전체)
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.BrushEngine;

public class PaintTool : ToolBase
{
    readonly string id, name, icon, cat;
    readonly Keys key;
    public readonly bool EraseMode;
    public override string Id => id;
    public override string Name => name;
    public override string Icon => icon;
    public override Keys Key => key;
    public string Category => cat;
    public override bool DrawsSizeCircle => true;
    public override double CircleSize => Preset?.Size ?? 0;
    public override bool Busy => stroke != null || vErase != null;

    public PaintTool(string id, string name, string icon, Keys key, string category, bool erase)
    { this.id = id; this.name = name; this.icon = icon; this.key = key; cat = category; EraseMode = erase; }

    public BrushPreset Preset => PresetStore.Active(cat);

    BrushStroke stroke;
    StrokeApply apply;
    PixelEdit edit;
    Layer owner;
    bool vector; string vColor;
    BPt start, lineEnd; double startSX, startSY, lastP;
    bool lineMode;
    System.Windows.Forms.Timer holdTimer;
    (double sx, double sy, double x, double y)? holdAt;
    VErase vErase;
    // 자 맞추기 (만화 모듈의 자): 화면에서 8px 움직일 때까지 점을 모았다가 방향이 정해지면 자 위로
    Modules.Comic.RulerSnap rsnap; readonly List<BPt> rpend = new(); bool rDecided, rPass;

    sealed class VErase { public Layer Layer; public JsonArray Before; public double R; public bool Whole, Cross; public double LX, LY; }

    static double Zoom => (App.Main as UI.MainForm)?.Canvas.V.Zoom ?? 1;

    public override void Activate() { var p = Preset; if (p?.Tip == "image") Tips.LoadImage(p.TipImage); }

    double PressureOf(PointerArgs e, PenFeel f) => e.Type == "pen" && f.NoPressure ? 1 : e.Pressure;

    public override void Down(PointerArgs e)
    {
        var doc = App.Doc;
        owner = doc?.Active;
        var p = Preset;
        if (p == null || doc == null) return;
        if (p.RoughOnly && !EraseMode && owner != null && !owner.Rough) { App.Toast("러프 연필은 러프 전용 레이어에서만 쓸 수 있습니다. 레이어를 바꿔 주세요."); owner = null; return; }
        if (!App.CheckEditable(owner)) { owner = null; return; }
        var feel = PenFeel.Current();
        bool onMask = owner.EditMask && owner.Mask != null;
        var target = owner.Target;
        vector = owner.Kind == "vector" && !onMask;
        if (vector && EraseMode)
        {
            // 벡터 지우개: 픽셀이 아니라 선을 지운다
            string mode = App.Get("vectorErase", "part");
            vErase = new VErase { Layer = owner, Before = (JsonArray)VectorLayer.Strokes(owner).DeepClone(), R = p.Size / 2, Whole = mode == "whole", Cross = mode == "cross", LX = e.X, LY = e.Y };
            VEraseStep(e);
            return;
        }
        if (p.Tip == "image") Tips.LoadImage(p.TipImage);
        uint col;
        if (onMask) col = EraseMode ? 0xFF000000 : 0xFFFFFFFF;          // 마스크: 칠하면 보이게, 지우면 가리기
        else if (EraseMode) col = 0xFF000000;
        else if (p.RoughOnly) col = Px.FromHex(owner.Tint ?? DefaultRoughColor());
        else col = p.Color != null ? Px.FromHex(p.Color, App.Fg) : (App.Fg | 0xFF000000);
        bool palLock = PixelArt.PaletteLocked(doc);
        List<int[]> pal = palLock || p.Shading ? PixelArt.PaletteOf(doc) : null;
        if (palLock && !EraseMode && !onMask)
        {
            var u = Px.Unpremul(col); var c = pal[PixelArt.NearestIndex((int)((u >> 16) & 255), (int)((u >> 8) & 255), (int)(u & 255), pal)];
            col = Px.Pack(255, (uint)c[0], (uint)c[1], (uint)c[2]);
        }
        edit = App.History.BeginPixels(owner, Name);
        var ed = edit; var tgt = target;
        Func<int, int, uint> sample = (p.Mix > 0 || p.Shading) && !EraseMode ? (x, y) =>
        {
            if ((uint)x >= (uint)tgt.W || (uint)y >= (uint)tgt.H) return 0;
            int ti = (y / Surface.TS) * tgt.TX + x / Surface.TS;
            var t = ed.BeforeTile(ti);
            return t == null ? 0 : t[(y % Surface.TS) * Surface.TS + x % Surface.TS];
        } : null;
        stroke = new BrushStroke(new StrokeSetup
        {
            W = doc.W, H = doc.H, Preset = p.Clone(), Color = col, BgColor = App.Bg,
            Symmetry = vector ? "none" : App.Get("symmetry", "none"), SymN = App.Get("symN", 6), SymMirror = App.Get("symMirror", false),
            Sample = sample, Palette = pal, Zoom = Zoom, Feel = feel,
        });
        if (vector) { stroke.Rec = new List<double>(); vColor = Px.ToHex(col); }
        stroke.ShadeFlip = e.Alt;                                    // 명암 잉크: Alt 를 누르고 그리면 반대로
        Tilt(e);
        apply = new StrokeApply
        {
            Buf = stroke.Buf, Target = target, Edit = edit, Erase = EraseMode, AlphaLock = owner.AlphaLock && !onMask,
            Mode = EraseMode || onMask ? Blend.Normal : Px.Parse(p.Blend), Opacity = p.Opacity,
            Sel = vector || !doc.Sel.Active ? null : doc.Sel, Post = EraseMode || !p.HasPost ? null : p, Dpi = doc.Dpi,
            SnapPalette = palLock && !EraseMode && !onMask ? pal : null,
        };
        double pr = PressureOf(e, feel);
        start = new BPt(e.X, e.Y, pr, e.Time); startSX = e.SX; startSY = e.SY; lastP = pr;
        lineMode = e.Shift; lineEnd = start;
        if (lineMode) stroke.Polyline(new[] { start, start });
        else stroke.Begin(new BPt(e.X, e.Y, pr, e.Time));
        rsnap = !lineMode && Modules.Comic.RulerSnap.Active(doc) ? new Modules.Comic.RulerSnap(e.X, e.Y) : null;
        rpend.Clear(); rDecided = rPass = false;
        if (!EraseMode && p.Color == null && !p.RoughOnly) AddRecentColor(Px.ToHex(App.Fg));
        Flush();
    }

    void Tilt(PointerArgs e)
    {
        if (stroke == null) return;
        if (e.TiltX != 0 || e.TiltY != 0)
        {
            stroke.TiltAngle = Math.Atan2(e.TiltY, e.TiltX) + Math.PI / 2;
            stroke.TiltMag = Math.Min(1, Math.Sqrt(e.TiltX * e.TiltX + e.TiltY * e.TiltY) / 60);   // 0 = 세움, 1 = 많이 눕힘
        }
        if (e.Twist != 0) stroke.TiltAngle = e.Twist * Math.PI / 180;           // 펜 돌림이 오면 그것을 우선
    }

    void Flush()
    {
        if (stroke == null) return;
        var r = stroke.TakeDirty();
        if (r.IsEmpty) return;
        edit.Touch(r);
        apply.Apply(r);
        App.Invalidate(r);
    }

    public override void Move(PointerArgs e)
    {
        if (vErase != null) { VEraseStep(e); return; }
        if (stroke == null) return;
        Tilt(e);
        var feel = PenFeel.Current();
        double pr = PressureOf(e, feel);
        if (pr > 0) lastP = pr;
        if (lineMode)
        {
            double x = e.X, y = e.Y;
            if (e.Ctrl)
            {
                // 15도 단위 스냅
                double a = Math.Atan2(y - start.Y, x - start.X), s = Math.Round(a / (Math.PI / 12)) * (Math.PI / 12), d = Math.Sqrt((x - start.X) * (x - start.X) + (y - start.Y) * (y - start.Y));
                x = start.X + Math.Cos(s) * d; y = start.Y + Math.Sin(s) * d;
            }
            stroke.Reset();
            double lp = Preset.PressureSize ? 1 : lastP;
            lineEnd = new BPt(x, y, lp);
            stroke.Polyline(new[] { new BPt(start.X, start.Y, lp), lineEnd });
        }
        else
        {
            var q = new BPt(e.X, e.Y, pr > 0 ? pr : lastP, e.Time);
            if (rsnap == null || rPass) { stroke.Add(q); HoldCheck(e); }
            else if (rDecided) stroke.Add(RulerProj(q));
            else
            {
                rpend.Add(q);
                if (Math.Sqrt((e.SX - startSX) * (e.SX - startSX) + (e.SY - startSY) * (e.SY - startSY)) >= 8)
                {
                    rDecided = true;
                    if (!rsnap.Decide(e.X, e.Y)) rPass = true;
                    foreach (var pq in rpend) stroke.Add(rPass ? pq : RulerProj(pq));
                    rpend.Clear();
                }
            }
        }
        Flush();
    }

    /// <summary>선 끝에서 펜을 잠시 멈추고 있으면 지금까지 그린 선을 시작점→끝점 직선으로 (계속 움직이면 끝점 조절)</summary>
    void HoldCheck(PointerArgs e)
    {
        if (!App.Get("holdLine", false) || vector) return;
        holdTimer ??= new System.Windows.Forms.Timer { Interval = 650 };
        holdTimer.Stop();
        holdTimer.Tick -= OnHold; holdTimer.Tick += OnHold;
        if (holdAt == null || Math.Sqrt((e.SX - holdAt.Value.sx) * (e.SX - holdAt.Value.sx) + (e.SY - holdAt.Value.sy) * (e.SY - holdAt.Value.sy)) > 4) holdAt = (e.SX, e.SY, e.X, e.Y);
        holdTimer.Start();
    }

    void OnHold(object s, EventArgs e)
    {
        holdTimer.Stop();
        if (stroke == null || lineMode || holdAt == null) return;
        var end = holdAt.Value;
        if (Math.Sqrt((end.x - start.X) * (end.x - start.X) + (end.y - start.Y) * (end.y - start.Y)) * Zoom < 30) return;   // 너무 짧으면 그대로
        stroke.Reset();
        lineMode = true;
        double pr = Preset.PressureSize ? 1 : lastP;
        lineEnd = new BPt(end.x, end.y, pr);
        stroke.Polyline(new[] { new BPt(start.X, start.Y, pr), lineEnd });
        Flush();
        (App.Main as UI.MainForm)?.Canvas.Present();
        App.Toast("직선으로 바꿨습니다 (그대로 움직이면 끝점 조절, Ctrl = 15도 단위)");
    }

    public override void Up(PointerArgs e)
    {
        if (vErase != null)
        {
            var v = vErase; vErase = null;
            var now = VectorLayer.Strokes(v.Layer);
            if (now.ToJsonString() != v.Before.ToJsonString())
            {
                App.History.Push(new VectorEdit("벡터 지우기", v.Layer, v.Before, (JsonArray)now.DeepClone(), true));
                App.Doc.Modified = true;
                App.NotifyLayers();
            }
            return;
        }
        if (stroke == null) return;
        var last = new BPt(e.X, e.Y, lastP, e.Time);
        if (rsnap != null && !rPass)
        {
            if (rDecided) last = RulerProj(last);
            else { foreach (var pq in rpend) stroke.Add(pq); rpend.Clear(); }
        }
        var rect = lineMode ? stroke.Total : stroke.End(last);
        Flush();
        if (!rect.IsEmpty)
        {
            if (vector && (stroke.Rec?.Count >= 4 || lineMode))
            {
                edit.Finish();                                       // 픽셀 기록 대신 선 기록 (되돌리면 선 목록으로 다시 그림)
                var pts = stroke.Rec;
                if (lineMode) { pts = new List<double>(); foreach (var q in new[] { new BPt(start.X, start.Y, lineEnd.P), lineEnd }) pts.AddRange(new[] { Math.Round(q.X, 2), Math.Round(q.Y, 2), Math.Round(q.P, 3), 0 }); }
                VectorLayer.AddStroke(owner, new JsonObject
                {
                    ["pts"] = new JsonArray(pts.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
                    ["preset"] = VectorLayer.PresetSnapshot(stroke.P),
                    ["color"] = vColor,
                });
            }
            else edit.Commit(Name);
            App.Doc.Modified = true;
        }
        else edit.Finish();
        ResetState();
        App.NotifyLayers();   // 썸네일
    }

    public override void Cancel()
    {
        if (vErase != null)
        {
            var v = vErase; vErase = null;
            var m = v.Layer.Meta != null ? (JsonObject)v.Layer.Meta.DeepClone() : new JsonObject();
            m["strokes"] = v.Before.DeepClone(); v.Layer.Meta = m;
            VectorLayer.Render(v.Layer, App.Doc);
            App.InvalidateAll();
            return;
        }
        if (stroke == null) return;
        var r = edit.Dirty;
        edit.Cancel();
        ResetState();
        App.Invalidate(r);
    }

    BPt RulerProj(BPt q) { var (x, y) = rsnap.Project(q.X, q.Y); return new BPt(x, y, q.P, q.T); }

    void ResetState()
    {
        rsnap = null; rpend.Clear();
        holdTimer?.Stop(); holdAt = null;
        stroke = null; apply = null; edit = null; lineMode = false;
    }

    void VEraseStep(PointerArgs e)
    {
        var v = vErase;
        // 빠르게 움직여도 빠지지 않도록 지난 위치부터 촘촘히
        double d = Math.Sqrt((e.X - v.LX) * (e.X - v.LX) + (e.Y - v.LY) * (e.Y - v.LY));
        int n = Math.Max(1, (int)Math.Ceiling(d / Math.Max(1, v.R * 0.5)));
        bool changed = false;
        for (int i = 1; i <= n; i++)
        {
            double x = v.LX + (e.X - v.LX) * i / n, y = v.LY + (e.Y - v.LY) * i / n;
            changed = (v.Cross ? VectorLayer.EraseToCross(v.Layer, x, y, v.R) : VectorLayer.EraseAt(v.Layer, x, y, v.R, v.Whole)) || changed;
        }
        v.LX = e.X; v.LY = e.Y;
        if (changed) { VectorLayer.Render(v.Layer, App.Doc); App.InvalidateAll(); }
    }

    public static string DefaultRoughColor()
    {
        string c = App.Get<string>("roughColor", null);
        return BrushOptions.RoughColors.Any(x => x.hex == c) ? c : BrushOptions.RoughColors[0].hex;
    }

    public static void AddRecentColor(string hex)
    {
        var r = App.Settings["recentColors"] as JsonArray;
        if (r == null) { r = new JsonArray(); App.Settings["recentColors"] = r; }
        var list = r.Select(x => (string)x).ToList();
        int i = list.IndexOf(hex);
        if (i == 0) return;
        if (i > 0) list.RemoveAt(i);
        list.Insert(0, hex);
        if (list.Count > 18) list.RemoveRange(18, list.Count - 18);
        App.Settings["recentColors"] = new JsonArray(list.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
        App.SaveSettingsSoon();
    }

    // ---- 겹침 그림: 끈 당기기·무거운 펜의 끈 -------------------------------------
    bool RopeVisible => stroke?.Lz != null && stroke.LzPen != null && !lineMode;

    public override Rectangle OverlayBounds(ViewXf v)
    {
        if (!RopeVisible) return Rectangle.Empty;
        var (ax, ay) = v.ToScreen(stroke.Lz.Value.x, stroke.Lz.Value.y); var (bx, by) = v.ToScreen(stroke.LzPen.Value.X, stroke.LzPen.Value.Y);
        double pad = stroke.P.Stabilizer * 2.5 + 8;
        return Rectangle.FromLTRB((int)(Math.Min(ax, bx) - pad), (int)(Math.Min(ay, by) - pad), (int)(Math.Max(ax, bx) + pad), (int)(Math.Max(ay, by) + pad));
    }

    public override void DrawOverlay(Graphics g, ViewXf v)
    {
        if (!RopeVisible) return;
        var (ax, ay) = v.ToScreen(stroke.Lz.Value.x, stroke.Lz.Value.y); var (bx, by) = v.ToScreen(stroke.LzPen.Value.X, stroke.LzPen.Value.Y);
        using var pen = new Pen(Color.FromArgb(230, 232, 163, 61), 1.5f) { DashPattern = new[] { 4f, 3f } };
        g.DrawLine(pen, (float)ax, (float)ay, (float)bx, (float)by);
        if (stroke.IsLazy)
        {
            float r = (float)(stroke.P.Stabilizer * 2.5);
            using var p2 = new Pen(Color.FromArgb(90, 232, 163, 61));
            g.DrawEllipse(p2, (float)bx - r, (float)by - r, r * 2, r * 2);
        }
        using var br = new SolidBrush(Color.FromArgb(232, 163, 61));
        g.FillEllipse(br, (float)ax - 3, (float)ay - 3, 6, 6);
    }

    public override bool HandleKey(Keys k)
    {
        if (k != Keys.OemOpenBrackets && k != Keys.OemCloseBrackets) return false;
        var p = Preset; if (p == null) return false;
        double f = k == Keys.OemCloseBrackets ? 1.15 : 1 / 1.15;
        double v = Math.Round(p.Size * f * 2) / 2; if (v == 0) v = 1;
        p.Size = Math.Max(0.5, Math.Min(600, v));
        PresetStore.Save(cat);
        App.NotifySettings();
        return true;
    }

    public override Control BuildOptions() => new BrushOptions(this).Root;
}
