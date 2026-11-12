// 기본 도구: 펜(G펜), 지우개, 스포이트, 화면 이동. (브러시 종류·프리셋 전부는 Brushes 모듈이 이 도구들을 바꿔 끼움)
using ComicPadNative.Core;
using ComicPadNative.Engine;

namespace ComicPadNative.Tools;

public class BrushTool : ToolBase
{
    public override string Id => "brush";
    public override string Name => "펜·브러시";
    public override string Icon => "✎";
    public override Keys Key => Keys.B;
    public override bool DrawsSizeCircle => true;
    public override double CircleSize => Params.Size;
    public virtual bool Erase => false;
    public BrushParams Params = new() { Size = 6, MinSize = 0.04, Stabilizer = 4 };

    protected Stroke stroke;
    protected PixelEdit edit;
    protected Layer layer;
    protected Pt last;
    public override bool Busy => stroke != null;

    /// <summary>필압 곡선 (설정 pressureGamma·pressurePts 반영은 Brushes 모듈이 바꿔 끼움)</summary>
    public static Func<double, double> GlobalCurve = p => Math.Clamp(p, 0, 1);

    public override void Down(PointerArgs e)
    {
        layer = App.Active;
        if (!App.CheckEditable(layer)) { layer = null; return; }
        edit = App.History.BeginPixels(layer, Erase ? "지우기" : "그리기");
        Params.Curve = GlobalCurve;
        uint col = layer.EditMask ? 0xFFFFFFFF : App.Fg;
        stroke = new Stroke(App.Doc, edit, Params, col, Erase, layer.AlphaLock && !layer.EditMask) { Zoom = (App.Main as UI.MainForm)?.Canvas.V.Zoom ?? 1 };
        last = new Pt(e.X, e.Y, e.Pressure, e.Time);
        stroke.Begin(last);
        Flush();
    }

    public override void Move(PointerArgs e)
    {
        if (stroke == null) return;
        last = new Pt(e.X, e.Y, e.Pressure, e.Time);
        stroke.Add(last);
        Flush();
    }

    public override void Up(PointerArgs e)
    {
        if (stroke == null) return;
        stroke.End(new Pt(e.X, e.Y, e.Pressure, e.Time));
        Flush();
        edit.Commit(Erase ? "지우기" : "그리기");
        App.Doc.Modified = true;
        stroke = null; edit = null;
        App.NotifyLayers();   // 썸네일
    }

    public override void Cancel()
    {
        if (stroke == null) return;
        var r = edit.Dirty;
        edit.Cancel();
        stroke = null; edit = null;
        App.Invalidate(r);
    }

    protected void Flush() { var r = stroke.TakeDirty(); if (!r.IsEmpty) App.Invalidate(r); }

    public override bool HandleKey(Keys k)
    {
        if (k == Keys.OemOpenBrackets) { Params.Size = Math.Max(0.5, Params.Size / 1.15); App.NotifySettings(); return true; }
        if (k == Keys.OemCloseBrackets) { Params.Size = Math.Min(2000, Params.Size * 1.15); App.NotifySettings(); return true; }
        return false;
    }

    public override Control BuildOptions() => UI.Opt.Panel(
        UI.Opt.Slider("크기", 0.5, 500, () => Params.Size, v => Params.Size = v, log: true),
        UI.Opt.Slider("불투명도", 0, 1, () => Params.Opacity, v => Params.Opacity = v, pct: true),
        UI.Opt.Slider("딱딱함", 0, 1, () => Params.Hardness, v => Params.Hardness = v, pct: true),
        UI.Opt.Check("필압으로 크기", () => Params.PressureSize, v => Params.PressureSize = v),
        UI.Opt.Slider("최소 크기", 0, 1, () => Params.MinSize, v => Params.MinSize = v, pct: true),
        UI.Opt.Check("필압으로 농도", () => Params.PressureOpacity, v => Params.PressureOpacity = v),
        UI.Opt.Slider("손떨림 보정", 0, 20, () => Params.Stabilizer, v => Params.Stabilizer = v));
}

public sealed class EraserTool : BrushTool
{
    public override string Id => "eraser";
    public override string Name => "지우개";
    public override string Icon => "⌫";
    public override Keys Key => Keys.E;
    public override bool Erase => true;
    public EraserTool() { Params = new BrushParams { Size = 24, PressureSize = false, Stabilizer = 0 }; }
}

public sealed class EyedropperTool : ToolBase
{
    public override string Id => "eyedropper";
    public override string Name => "스포이트";
    public override string Icon => "💧";
    public override Keys Key => Keys.I;
    public bool LayerOnly;
    public override void Down(PointerArgs e) => Pick(e);
    public override void Move(PointerArgs e) => Pick(e);
    void Pick(PointerArgs e)
    {
        var d = App.Doc;
        int x = (int)Math.Floor(e.X), y = (int)Math.Floor(e.Y);
        if (d == null || x < 0 || y < 0 || x >= d.W || y >= d.H) return;
        uint c = LayerOnly ? App.Active?.Px?.Get(x, y) ?? 0 : App.Comp.Sample(d, x, y);
        if ((c >> 24) == 0) return;
        App.SetColor(Px.Unpremul(c) | 0xFF000000);   // 불투명 색 (알파 255 면 미리 곱한 값과 같음)
    }
    public override Control BuildOptions() => UI.Opt.Panel(UI.Opt.Check("현재 레이어에서만", () => LayerOnly, v => LayerOnly = v));
}

public sealed class HandTool : ToolBase
{
    public override string Id => "hand";
    public override string Name => "화면 이동";
    public override string Icon => "✋";
    public override Keys Key => Keys.H;
    public override Cursor Cursor => Cursors.Hand;
}

/// <summary>기본 도구 등록</summary>
public sealed class BasicToolsModule : IModule
{
    public void Init()
    {
        Registry.Tool(new BrushTool());
        Registry.Tool(new EraserTool());
        Registry.Tool(new EyedropperTool());
        Registry.Tool(new HandTool());
    }
}
