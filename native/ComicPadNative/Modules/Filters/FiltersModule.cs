// 필터·보정·효과·파일 입출력 모듈: 합성기 확장(Hooks) 연결 + 메뉴 등록.
// 웹 판 commands.js / commands2.js / ui/adjust.js / ui/aitools.js(선 정리) / ui/viewaids.js(용지 질감) 의 명령을 옮김.
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Filters;

public sealed class FiltersModule : IModule
{
    // 메뉴 경로에 '/' 를 쓸 수 없어서 비슷한 글자로 (웹 이름 "색조/채도/명도" 등)
    public static string M(string s) => s.Replace("/", "∕");
    static Document D => App.Doc;
    static bool HasDoc() => App.Doc != null;

    public void Init()
    {
        // ---- 합성기 확장 ----
        Hooks.Adjust = LayerEffects.Adjust;
        var baseHas = Hooks.HasFx;
        Hooks.HasFx = l => !LayerEffects.Suppressed(l) && baseHas(l);
        Hooks.Fx = LayerEffects.Fx;
        Hooks.Tone = LayerEffects.Tone;
        Hooks.Paper = Paper.Draw;
        Hooks.PaperOver = Paper.Over;
        App.Invalidated += LayerEffects.MarkDirty;      // 메인 창보다 먼저 등록됨 → 화면을 다시 그리기 전에 망점 타일 표시
        App.DocChanged += LayerEffects.ClearToneCache;

        // ---- 파일 형식 ----
        FileCommands.Openers[".psd"] = PsdReader.Open;
        FileCommands.Openers[".psb"] = PsdReader.Open;

        RegisterAdjust();
        RegisterFilters();
        RegisterFileAndEdit();

        // 시험 (창 없이): COMICPAD_FILTERS_SELFTEST=결과폴더 로 --render 실행
        var st = Environment.GetEnvironmentVariable("COMICPAD_FILTERS_SELFTEST");
        if (!string.IsNullOrEmpty(st)) Environment.Exit(SelfTest.Run(st));
    }

    /// <summary>"보정" 메뉴를 "필터" 다음으로 (메인 창은 모르는 메뉴를 맨 뒤에 둠)</summary>
    internal static void MoveAdjustMenu(object s, EventArgs e)
    {
        if (App.Main?.MainMenuStrip is not MenuStrip ms) return;
        Application.Idle -= MoveAdjustMenu;
        var adj = ms.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "보정");
        var flt = ms.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "필터");
        if (adj == null || flt == null) return;
        ms.Items.Remove(adj);
        ms.Items.Insert(ms.Items.IndexOf(flt) + 1, adj);
    }

    // ================================================================= 보정
    static void RegisterAdjust()
    {
        int o = 10;
        void A(string type) => Registry.Menu("보정/" + M(AdjustTypes.Name(type)) + "…", () => AdjustDialogs.AdjustDialog(type), Keys.None, HasDoc, order: o += 2);
        foreach (var t in new[] { "levels", "curves", "hsl", "balance", "bc", "vibrance" }) A(t);
        Registry.Menu("보정/-1", null, order: o += 2);
        foreach (var t in new[] { "gradmap", "colorize", "bw", "posterize", "threshold", "invert" }) A(t);
        Registry.Menu("보정/-2", null, order: o += 2);
        Registry.Menu("보정/— 조정 레이어 만들기 (원본 보존) —", () => { }, Keys.None, () => false, order: o += 2);
        foreach (var (type, name) in AdjustTypes.List)
        {
            var tp = type;
            Registry.Menu("보정/조정 레이어: " + M(name), () => AdjustDialogs.AddAdjustLayer(tp), Keys.None, HasDoc, order: o += 2);
        }
        Registry.Menu("보정/-3", null, order: o += 2);
        Registry.Menu("보정/" + M("레이어 효과 (테두리/그림자/광선)…"), AdjustDialogs.FxDialog, Keys.None, HasDoc, order: o += 2);
        Registry.Menu("보정/톤화 (망점으로 보이기)…", AdjustDialogs.ToneDialog, Keys.None, HasDoc, order: o += 2);

        Registry.Menu("레이어/-fx", null, order: 300);
        Registry.Menu("레이어/" + M("레이어 효과 (테두리/그림자/광선)…"), AdjustDialogs.FxDialog, Keys.None, HasDoc, order: 301);
        Registry.Menu("레이어/톤화 (망점으로 보이기)…", AdjustDialogs.ToneDialog, Keys.None, HasDoc, order: 302);
        Registry.Menu("레이어/조정 레이어 값 수정", EditAdjust, Keys.None, () => App.Active?.IsAdjust == true, order: 303);

        Registry.Menu("보기/-paper", null, order: 200);
        Registry.Menu("보기/용지 질감 (종이 결)…", AdjustDialogs.PaperTextureDialog, Keys.None, HasDoc, order: 201);
        Registry.Menu("보기/용지 색…", AdjustDialogs.PaperColorDialog, Keys.None, HasDoc, order: 202);
        Registry.Menu("보기/용지 보이기", AdjustDialogs.TogglePaper, Keys.None, HasDoc, () => App.Doc?.PaperVisible == true, order: 203);
    }

    static void EditAdjust()
    {
        var l = App.Active;
        if (l?.IsAdjust == true && (string)l.Meta?["type"] is string t) AdjustDialogs.AdjustDialog(t, l);
    }

    // ================================================================= 필터
    static void RegisterFilters()
    {
        int o = 10;
        void F(string label, Action run) => Registry.Menu("필터/" + label, run, Keys.None, HasDoc, order: o += 2);

        F("선화 추출 (흰 배경 → 투명)", () => FilterOps.PixelFilter("선화 추출", FilterOps.LineExtract));
        F("2값화 (흑백 선화 정리)…", () =>
        {
            var v = Ui.Ask("2값화", new[] { Field.N("t", "경계값 (0~255)", 128, 0, 255) });
            if (v != null) FilterOps.PixelFilter("2값화", d => FilterOps.Mono(d, v.N("t")));
        });
        F("흑백", () => FilterOps.PixelFilter("흑백", FilterOps.Gray));
        F("색 반전", () => FilterOps.PixelFilter("색 반전", FilterOps.Invert));
        F("흐리게…", () =>
        {
            var v = Ui.Ask("흐리게", new[] { Field.N("r", "반경(px)", 4, 0, 500, 0.5) });
            if (v != null) FilterOps.CanvasOp("흐리게", (s, w, h) => Raster.BlurRgba(s, w, h, v.N("r")));
        });
        F(M("밝기/대비/채도…"), () =>
        {
            var v = Ui.Ask("색조 보정", new[] { Field.N("b", "밝기 %", 100, 0, 1000), Field.N("c", "대비 %", 100, 0, 1000), Field.N("s", "채도 %", 100, 0, 1000), Field.N("hue", "색조 회전 (도)", 0, -360, 360) });
            if (v == null) return;
            var chain = Svg.CssColor(v.N("b"), v.N("c"), v.N("s"), v.N("hue"));
            FilterOps.CanvasOp("색조 보정", (s, w, h) => { var r = (uint[])s.Clone(); chain.Apply(r); return r; });
        });
        Registry.Menu("필터/-1", null, order: o += 2);
        F(M("외곽선 두르기 (스티커/에셋)…"), () =>
        {
            var v = Ui.Ask("외곽선", new[] { Field.N("w", "두께 (px)", 8, 0, 500), Field.Col("color", "색", "#ffffff"), Field.Info("그림 바깥쪽에 테두리를 두릅니다 (선택 영역이 있으면 그 안만).") }, "적용");
            if (v != null) DirectOp("외곽선", (s, w, h) => FilterOps.Outline(s, w, h, v.N("w"), v.S("color"), D.Sel));
        });
        F("그림자 넣기…", () =>
        {
            var v = Ui.Ask("그림자", new[] { Field.N("dx", "가로 이동", 12, -2000, 2000), Field.N("dy", "세로 이동", 12, -2000, 2000), Field.N("blur", "흐림", 10, 0, 500), Field.Col("color", "색", "#000000"), Field.N("alpha", "진하기 (0~1)", 0.45, 0, 1, 0.05) }, "적용");
            if (v != null) DirectOp("그림자", (s, w, h) => FilterOps.DropShadow(s, w, h, v.N("dx"), v.N("dy"), v.N("blur"), v.S("color"), v.N("alpha")));
        });
        F("단색으로 바꾸기 (선 색 변경)", () => DirectOp("단색 변경", (s, w, h) => FilterOps.Colorize(s, w, h, App.Fg, D.Sel)));
        F("언샤프 마스크 (선명하게)…", () =>
        {
            var v = Ui.Ask("언샤프 마스크", new[] { Field.N("r", "반경(px)", 2, 0, 200, 0.5), Field.N("a", "양(%)", 80, 0, 1000) });
            if (v != null) FilterOps.CanvasOp("언샤프 마스크", (s, w, h) => FilterOps.Unsharp(s, w, h, v.N("r"), v.N("a") / 100));
        });
        F("노이즈 추가…", () =>
        {
            var v = Ui.Ask("노이즈", new[] { Field.N("a", "양(%)", 20, 0, 100), Field.C("mono", "흑백 노이즈", true) });
            if (v != null) { int seed = Random.Shared.Next(1000); FilterOps.CanvasOp("노이즈", (s, w, h) => FilterOps.Noise(s, w, h, v.N("a"), v.B("mono"), seed)); }
        });
        F("모자이크…", () =>
        {
            var v = Ui.Ask("모자이크", new[] { Field.N("s", "칸 크기(px)", Math.Max(8, Math.Round(D.W / 80.0)), 1, 2000), Field.Info("선택 영역이 있으면 그 안만 (가리기용).") });
            if (v == null) return;
            int s0 = Math.Max(2, (int)Math.Round(v.N("s")));
            FilterOps.CanvasOp("모자이크", (s, w, h) => FilterOps.Mosaic(s, w, h, s0));
        });
        F("방향 흐림 (모션 블러)…", () =>
        {
            var v = Ui.Ask("방향 흐림", new[] { Field.N("a", "각도(도)", 0, -360, 360), Field.N("d", "거리(px)", 30, 0, 2000) });
            if (v != null) FilterOps.CanvasOp("방향 흐림", (s, w, h) => FilterOps.Motion(s, w, h, v.N("a"), v.N("d")));
        });
        F("방사형 흐림 (줌)…", () =>
        {
            var d = D;
            var v = Ui.Ask("방사형 흐림", new[] { Field.N("amt", "세기(%)", 15, 0, 500), Field.Info("중심은 선택 영역 가운데(없으면 캔버스 가운데). 집중·속도감 연출.") });
            if (v == null) return;
            var b = d.Sel.Active ? d.Sel.Bounds() : new Rect(0, 0, d.W, d.H);
            double cx = b.X + b.W / 2.0, cy = b.Y + b.H / 2.0;
            FilterOps.CanvasOp("방사형 흐림", (s, w, h) => FilterOps.ZoomBlur(s, w, h, v.N("amt"), cx, cy));
        });
        F("빛 번짐 (블룸)…", () =>
        {
            var v = Ui.Ask("빛 번짐", new[] { Field.N("r", "번짐 반경(px)", 20, 0, 500), Field.N("a", "세기(%)", 50, 0, 100) });
            if (v != null) FilterOps.CanvasOp("빛 번짐", (s, w, h) => FilterOps.GlowBlur(s, w, h, v.N("r"), v.N("a")));
        });
        Registry.Menu("필터/-2", null, order: o += 2);
        F("선 정리 (잡티·끊김·울퉁불퉁)…", LineCleanDialog);

        Registry.Menu("선택/-flt", null, order: 200);
        Registry.Menu("선택/선택 경계 흐리기…", Feather, Keys.None, HasDoc, order: 201);
        Registry.Menu("선택/색역 선택 (같은 색 전부)…", ColorRange, Keys.None, HasDoc, order: 202);
    }

    /// <summary>선택 영역 섞기 없이 결과를 그대로 (외곽선·그림자·단색: 선택 영역은 계산 안에서 씀)</summary>
    static void DirectOp(string label, Func<uint[], int, int, uint[]> op)
    {
        var s = FilterOps.Target(out var l); if (s == null) return;
        Ui.Busy(() => { var full = s.Full; FilterOps.Commit(l, s, full, op(s.Read(full), s.W, s.H), label); });
    }

    static void LineCleanDialog()
    {
        var d = D; var s = FilterOps.Target(out var layer); if (s == null) return;
        int k = Math.Max(1, (int)Math.Round(d.W / 1200.0));
        var v = Ui.Ask("선 정리", new[]
        {
            Field.N("specks", "잡티 지우기: 이보다 작은 점은 지움 (픽셀 수, 0 = 끔)", 12 * k * k, 0, 1e6),
            Field.N("close", "끊긴 선 잇기: 이 정도 틈은 메움 (px, 0 = 끔)", k, 0, 20),
            Field.N("smooth", "가장자리 다듬기: 울퉁불퉁·계단 줄이기 (px, 0 = 끔)", k, 0, 10),
            Field.N("weight", "선 굵기 (+ 굵게 / − 가늘게, px)", 0, -5, 5),
            Field.C("darken", "선 색을 한 가지로 (지금 전경색)", false),
            Field.C("fromScan", "스캔한 그림(흰 바탕 종이)에서 선만 뽑기", false),
            Field.Info($"\"{d.Active.Name}\" 레이어에 적용합니다{(d.Sel.Active ? " (선택 영역 안만)" : "")}. 마음에 안 들면 실행취소(Ctrl+Z)."),
        }, "정리하기");
        if (v == null) return;
        var r = (d.Sel.Active ? d.Sel.Bounds() : s.Full).Clip(s.W, s.H);
        if (r.IsEmpty) return;
        int removed = 0;
        Ui.Busy(() =>
        {
            var img = FilterOps.ToBytes(s.Read(r));
            var before = d.Sel.Active ? (byte[])img.Clone() : null;
            removed = FilterOps.CleanLines(img, r.W, r.H, new FilterOps.CleanOpt
            {
                Specks = (int)v.N("specks"), Close = (int)v.N("close"), Smooth = (int)v.N("smooth"), Weight = (int)Math.Round(v.N("weight")),
                Darken = v.B("darken"), FromScan = v.B("fromScan"), Color = Px.ToHex(App.Fg),
            });
            if (before != null)
                for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++)
                    if (d.Sel.At(r.X + x, r.Y + y) == 0) { int p = (y * r.W + x) * 4; for (int c = 0; c < 4; c++) img[p + c] = before[p + c]; }
            FilterOps.Commit(layer, s, r, FilterOps.FromBytes(img), "선 정리");
        });
        App.Toast("선을 정리했습니다" + (removed > 0 ? $" (잡티 {removed}px 지움)" : ""));
    }

    static void Feather()
    {
        var d = D; var sel = d.Sel;
        if (!sel.Active) { App.Toast("선택 영역이 없습니다"); return; }
        var v = Ui.Ask("경계 흐리기", new[] { Field.N("r", "반경(px)", 8, 0, 500) });
        if (v == null) return;
        App.History.SelectionChange(d, "경계 흐리기", () =>
        {
            var p = new float[sel.Mask.Length];
            for (int i = 0; i < p.Length; i++) p[i] = sel.Mask[i];
            Raster.Blur(p, d.W, d.H, v.N("r"), v.N("r"));
            var m = new byte[p.Length];
            for (int i = 0; i < p.Length; i++) m[i] = (byte)Raster.B(p[i]);
            sel.Mask = m;
        });
        SelectCommands.RedrawView();
    }

    static void ColorRange()
    {
        var d = D;
        var v = Ui.Ask("색역 선택", new[]
        {
            Field.Col("color", "고를 색", Px.ToHex(App.Fg)),
            Field.N("tol", "허용 범위 (0~255)", 32, 0, 255),
            Field.Pick("src", "기준", new[] { ("all", "보이는 그림"), ("layer", "현재 레이어") }, "all"),
            Field.Pick("op", "방식", new[] { ("replace", "새로"), ("add", "추가"), ("subtract", "빼기") }, "replace"),
        }, "선택");
        if (v == null) return;
        Ui.Busy(() =>
        {
            Surface img = v.S("src") == "layer" && d.Active?.Px != null ? d.Active.Px : App.Comp.Flatten(d, new RenderOptions { Paper = false, ExcludeDraft = true });
            uint c0 = Px.Unpremul(Px.FromHex(v.S("color")));
            int r0 = (int)((c0 >> 16) & 255), g0 = (int)((c0 >> 8) & 255), b0 = (int)(c0 & 255);
            double tol = v.N("tol");
            var mask = new byte[d.W * d.H]; bool any = false;
            var full = img.Read(new Rect(0, 0, d.W, d.H));
            for (int i = 0; i < full.Length; i++)
            {
                Raster.Unpack(full[i], out int r, out int g, out int b, out int a);
                if (a < 16) continue;
                if (Math.Max(Math.Abs(r - r0), Math.Max(Math.Abs(g - g0), Math.Abs(b - b0))) <= tol) { mask[i] = 255; any = true; }
            }
            if (!any) { App.Toast("그 색을 찾지 못했습니다"); return; }
            App.History.SelectionChange(d, "색역 선택", () => d.Sel.Apply(mask, v.S("op")));
        });
        SelectCommands.RedrawView();
    }

    // ================================================================= 파일 · 편집
    static void RegisterFileAndEdit()
    {
        Registry.Menu("파일/이미지 가져오기 (새 레이어)…", ImageIO.ImportImage, Keys.None, HasDoc, order: 46);
        Registry.Menu("파일/PSD로 저장 (레이어 유지)…", PsdCommands.SavePsd, Keys.None, HasDoc, order: 52);
        Registry.Menu("파일/웹툰 분할 내보내기…", ExportCommands.Slices, Keys.None, HasDoc, order: 53);
        Registry.Menu("파일/웹툰 플랫폼 규격으로 내보내기…", ExportCommands.Webtoon, Keys.None, HasDoc, order: 54);
        Registry.Menu("파일/레이어별 PNG 내보내기…", ExportCommands.LayerPngs, Keys.None, HasDoc, order: 55);
        Registry.Menu("파일/" + M("인쇄용 내보내기 (PDF / 1비트 TIFF)…"), PrintExport.Dialog, Keys.None, HasDoc, order: 56);
        Registry.Menu("보기/인쇄 색 미리보기 (CMYK)…", PrintExport.ProofDialog, Keys.None, HasDoc, order: 204);

        Registry.Menu("편집/잘라내기", () => ImageIO.Copy(false, true), Keys.Control | Keys.X, HasDoc, order: 26);
        Registry.Menu("편집/복사", () => ImageIO.Copy(false, false), Keys.Control | Keys.C, HasDoc, order: 27);
        Registry.Menu("편집/병합 복사 (보이는 그림)", () => ImageIO.Copy(true, false), Keys.Control | Keys.Shift | Keys.C, HasDoc, order: 28);
        Registry.Menu("편집/붙여넣기", ImageIO.Paste, Keys.Control | Keys.V, HasDoc, order: 29);
    }
}
