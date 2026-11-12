// 브러시 옵션 패널 (웹 tools/brush.js buildBrushOptions): 프리셋 목록(미리보기) · 만들기/복제/이름/기본값/삭제/가져오기/내보내기 · 시험 그리기 · 모든 설정
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.BrushEngine;

public sealed class BrushOptions
{
    public static readonly (string hex, string name)[] RoughColors =
    {
        ("#3d7be0", "파랑"), ("#e5484d", "빨강"), ("#f08c2e", "주황"), ("#d9b310", "노랑"),
        ("#7fbf2f", "연두"), ("#2fa35b", "초록"), ("#1fb5a8", "청록"), ("#4fb3e8", "하늘"),
        ("#3b4cc0", "남색"), ("#8e5bd6", "보라"), ("#e05fa8", "분홍"), ("#9a6a3a", "갈색"),
        ("#7d7d86", "회색"), ("#2b2b2b", "검정"),
    };

    public readonly FlowLayoutPanel Root;
    readonly PaintTool tool;
    readonly string cat;
    readonly bool erase;
    readonly ListBox list;
    readonly FlowLayoutPanel body;
    BrushTestPad testPad;
    bool showScatter;
    readonly System.Windows.Forms.Timer refreshSoon = new() { Interval = 150 };

    BrushPreset P => PresetStore.Active(cat);

    public BrushOptions(PaintTool t)
    {
        tool = t; cat = t.Category; erase = t.EraseMode;
        Root = Opt.Panel();
        list = new ListBox { Width = BUI.Width, Height = 34 * 7 + 4, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 34, BackColor = Opt.Bg2, ForeColor = Opt.Fg, BorderStyle = BorderStyle.None, IntegralHeight = false };
        list.DrawItem += DrawItem;
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || listSync) return;
            var l = PresetStore.List(cat);
            if (list.SelectedIndex < l.Count && l[list.SelectedIndex].Id != P?.Id) { PresetStore.SetActive(cat, l[list.SelectedIndex].Id); tool.Activate(); Rebuild(); }
        };
        Root.Controls.Add(list);
        var btns = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(BUI.Width, 0), AutoSize = true, BackColor = Opt.Bg };
        btns.Controls.AddRange(new Control[]
        {
            Opt.Button("새로", NewPreset), Opt.Button("복제", Duplicate), Opt.Button("이름", Rename), Opt.Button("기본값", ResetDefault), Opt.Button("삭제", Delete),
            Opt.Button("가져오기", Import), Opt.Button("내보내기", Export),
        });
        if (!erase) btns.Controls.Add(Opt.Button("ABR 가져오기", () => BrushesModule.ImportAbr()));
        Root.Controls.Add(btns);
        body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Opt.Bg, Margin = new Padding(0) };
        Root.Controls.Add(body);
        refreshSoon.Tick += (_, _) => { refreshSoon.Stop(); if (Root.IsDisposed) return; testPad?.Refresh2(); RenderList(); };
        Action onChange = () => { if (!Root.IsDisposed) { refreshSoon.Stop(); refreshSoon.Start(); } };
        PresetStore.Changed += onChange;
        Action onColor = () => onChange();
        App.ColorChanged += onColor;
        Root.Disposed += (_, _) => { PresetStore.Changed -= onChange; App.ColorChanged -= onColor; refreshSoon.Dispose(); };
        RenderList();
        Rebuild();
    }

    bool listSync;
    void RenderList()
    {
        var l = PresetStore.List(cat);
        listSync = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var p in l) list.Items.Add(p.Name);
        list.SelectedIndex = l.FindIndex(x => x.Id == P?.Id);
        list.EndUpdate();
        listSync = false;
        list.Invalidate();
    }

    void DrawItem(object s, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var l = PresetStore.List(cat);
        if (e.Index >= l.Count) return;
        var p = l[e.Index];
        bool on = p.Id == P?.Id;
        var g = e.Graphics;
        using (var bg = new SolidBrush(on ? Color.FromArgb(78, 66, 48) : Opt.Bg2)) g.FillRectangle(bg, e.Bounds);
        uint col = erase ? 0xFF888888 : p.Color != null ? Px.FromHex(p.Color) : App.Fg | 0xFF000000;
        var th = BrushPreview.Thumb(p, col, 96, 26);
        g.DrawImage(th, e.Bounds.Right - 100, e.Bounds.Y + 4);
        using var fb = new SolidBrush(on ? Opt.Accent : Opt.Fg);
        var tr = new RectangleF(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 112, e.Bounds.Height);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(p.Name, list.Font, fb, tr, sf);
        if (on) using (var pen = new Pen(Opt.Accent)) g.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
    }

    // ---- 프리셋 단추 -----------------------------------------------------------------
    void NewPreset()
    {
        string name = BUI.Prompt("새 프리셋 이름", erase ? "새 지우개" : "새 브러시");
        if (string.IsNullOrEmpty(name)) return;
        var n = new BrushPreset { Id = PresetStore.NewId(), Name = name };
        if (erase) { n.Size = 24; n.PressureSize = false; n.Stabilizer = 0; }
        PresetStore.List(cat).Add(n);
        PresetStore.SetActive(cat, n.Id); PresetStore.Save(cat);
        RenderList(); Rebuild();
    }

    void Duplicate()
    {
        var cur = P;
        string name = BUI.Prompt("새 프리셋 이름", cur.Name + " 2");
        if (string.IsNullOrEmpty(name)) return;
        var n = cur.Clone(); n.Id = PresetStore.NewId(); n.Name = name;
        var l = PresetStore.List(cat); l.Add(n);
        PresetStore.SetActive(cat, n.Id); PresetStore.Save(cat);
        RenderList(); Rebuild();
    }

    void Rename()
    {
        string name = BUI.Prompt("프리셋 이름", P.Name);
        if (string.IsNullOrEmpty(name)) return;
        P.Name = name; PresetStore.Save(cat); RenderList();
    }

    void ResetDefault()
    {
        var def = DefaultPresets.Find(cat, P.Id);
        if (def == null) { App.Toast("사용자 프리셋은 기본값이 없습니다"); return; }
        P.CopyFrom(def);
        PresetStore.Save(cat); tool.Activate();
        Rebuild();
    }

    void Delete()
    {
        var l = PresetStore.List(cat);
        if (l.Count <= 1) return;
        if (MessageBox.Show(App.Main, $"'{P.Name}' 프리셋을 삭제할까요?", "ComicPad", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        l.Remove(P);
        PresetStore.SetActive(cat, l[0].Id); PresetStore.Save(cat);
        RenderList(); Rebuild();
    }

    /// <summary>내보내기: 웹 "내 세팅 내보내기"와 같은 .cpset (브러시 부분만) — 웹 판 "세팅 가져오기"로도 읽힘</summary>
    void Export()
    {
        var r = MessageBox.Show(App.Main, "지금 프리셋만 내보낼까요?\n(아니요 = 이 목록의 프리셋 전부)", "브러시 내보내기", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return;
        var items = r == DialogResult.Yes ? new List<BrushPreset> { P } : PresetStore.List(cat);
        using var dlg = new SaveFileDialog { Filter = "ComicPad 세팅 (*.cpset)|*.cpset|JSON (*.json)|*.json", FileName = (r == DialogResult.Yes ? P.Name : (erase ? "지우개" : "브러시")) + ".cpset" };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, BrushesModule.BuildSetFile(cat, items).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        App.Toast($"프리셋 {items.Count}개를 내보냈습니다");
    }

    void Import()
    {
        using var dlg = new OpenFileDialog { Filter = "ComicPad 세팅·브러시 (*.cpset;*.json)|*.cpset;*.json|모든 파일|*.*" };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        try
        {
            int n = BrushesModule.ImportSetFile(File.ReadAllText(dlg.FileName), cat);
            App.Toast(n > 0 ? $"프리셋 {n}개를 가져왔습니다" : "가져올 브러시가 없습니다");
            RenderList(); Rebuild();
        }
        catch (Exception ex) { App.Toast("가져오지 못했습니다: " + ex.Message); }
    }

    // ---- 설정 ------------------------------------------------------------------------
    void Changed() => PresetStore.Save(cat);
    Action<double> SetD(Action<BrushPreset, double> f) => v => { f(P, v); Changed(); };
    Action<bool> SetB(Action<BrushPreset, bool> f, bool rebuild = false) => v => { f(P, v); Changed(); if (rebuild) Root.BeginInvoke(Rebuild); };

    void Rebuild()
    {
        if (Root.IsDisposed) return;
        body.SuspendLayout();
        foreach (Control c in body.Controls.Cast<Control>().ToList()) { body.Controls.Remove(c); c.Dispose(); }
        testPad = null;
        var cur = P;
        if (cur == null) { body.ResumeLayout(); return; }
        var add = new List<Control>();
        Func<double, string> px = v => v < 10 ? v.ToString("0.0") : Math.Round(v).ToString();
        add.Add(BUI.Section("기본",
            BUI.Slider("크기", 0.5, 600, () => P.Size, SetD((p, v) => p.Size = v), px, log: true, step: 0.5),
            BUI.Slider("불투명도", 0, 1, () => P.Opacity, SetD((p, v) => p.Opacity = v), BUI.Pct, step: 0.01),
            BUI.Slider("빠를수록 가늘게", 0, 1, () => P.SpeedThin, SetD((p, v) => p.SpeedThin = v), v => BUI.OffOr(v, BUI.Pct), step: 0.05)));
        testPad = new BrushTestPad(() => P, erase);
        add.Add(BUI.Section("미리보기 · 시험 그리기", testPad));
        string sm = cur.StabMode ?? "auto";
        add.Add(BUI.Section("흔들림 보정",
            BUI.Seg(new[] { ("auto", "설정대로"), ("euro", "자동"), ("avg", "평균"), ("lazy", "끈 당기기"), ("spring", "무거운 펜") }, () => P.StabMode ?? "auto", v => { P.StabMode = v; Changed(); Root.BeginInvoke(Rebuild); }),
            BUI.Slider(sm == "lazy" ? "끈 길이 (클수록 떨림 적고 늦게 따라옴)" : sm == "spring" ? "펜 무게 (클수록 느리게·부드럽게)" : "보정 세기", 0, 20, () => P.Stabilizer, SetD((p, v) => p.Stabilizer = v), v => v != 0 ? v.ToString("0") : "끔", step: 1),
            BUI.Toggle("펜을 떼면 펜 끝까지 이어 그리기", () => P.CatchUp, SetB((p, v) => p.CatchUp = v)),
            BUI.Toggle("선 끝에서 펜을 멈추고 있으면 직선으로 (모든 브러시)", () => App.Get("holdLine", false), v => { App.Set("holdLine", v); App.NotifySettings(); }),
            BUI.Help(sm switch
            {
                "euro" => "느리게 그리면 떨림을 많이, 빠르게 그리면 지연을 적게 잡습니다.",
                "avg" => "최근 점들의 평균으로 고르게 (예전 방식).",
                "lazy" => "펜과 선 끝 사이에 보이지 않는 끈이 있어, 끈이 팽팽해질 때만 선이 따라옵니다. 떨림이 거의 없어지고 곡선이 매끈해집니다 (그리는 동안 끈이 보임).",
                "spring" => "선 끝이 용수철에 매달린 무거운 펜처럼 살짝 늦게, 부드럽게 따라옵니다. 긴 곡선·머리카락에 좋습니다.",
                _ => "설정 > 펜 의 보정 방식을 따릅니다.",
            })));
        add.Add(BUI.Section("입출 보정 (선 끝 가늘게)",
            BUI.Slider("시작", 0, 400, () => P.TaperIn, SetD((p, v) => p.TaperIn = v), v => v != 0 ? $"{Math.Round(v)}px" : "끔", step: 1),
            BUI.Slider("끝", 0, 400, () => P.TaperOut, SetD((p, v) => p.TaperOut = v), v => v != 0 ? $"{Math.Round(v)}px" : "끔", step: 1)));
        add.Add(BUI.Section("필압",
            Opt.Row(BUI.Toggle("크기", () => P.PressureSize, SetB((p, v) => p.PressureSize = v, true)), BUI.Toggle("불투명도", () => P.PressureOpacity, SetB((p, v) => p.PressureOpacity = v, true))),
            cur.PressureSize ? BUI.Slider("최소 크기", 0, 1, () => P.MinSize, SetD((p, v) => p.MinSize = v), BUI.Pct, step: 0.01) : null,
            cur.PressureOpacity ? BUI.Slider("최소 농도", 0, 1, () => P.MinOpacity, SetD((p, v) => p.MinOpacity = v), BUI.Pct, step: 0.01) : null,
            BUI.Toggle("이 브러시만 필압 곡선 따로", () => P.PressurePts != null, v =>
            {
                P.PressurePts = v ? (Pressure.ParsePts(App.Settings["pressurePts"]) ?? new[] { new[] { 0.0, 0 }, new[] { 1.0, 1 } }) : null;
                Changed(); Root.BeginInvoke(Rebuild);
            }),
            cur.PressurePts != null
                ? new CurveEditor("이 브러시의 필압 곡선", () => P.PressurePts, pts => { P.PressurePts = pts ?? new[] { new[] { 0.0, 0 }, new[] { 1.0, 1 } }; Changed(); })
                : BUI.Help("모든 브러시 공통 곡선은 설정 > 펜 에서 바꿉니다.")));
        bool isPixel = cur.Tip == "pixel", sprayFlat = cur.Tip == "spray" || cur.Tip == "flat";
        add.Add(BUI.Section("브러시 끝",
            BUI.Seg(new[] { ("round", "원형"), ("grain", "연필결"), ("spray", "스프레이"), ("flat", "납작 펜"), ("image", "이미지"), ("pixel", "픽셀") }, () => P.Tip, v =>
            {
                P.Tip = v; if (v == "pixel") { P.PressureSize = false; P.Stabilizer = 0; }
                Changed(); tool.Activate(); Root.BeginInvoke(Rebuild);
            }),
            isPixel ? BUI.Toggle("픽셀 퍼펙트 (1px 선의 ㄴ자 겹친 모서리 지우기)", () => P.PixelPerfect, SetB((p, v) => p.PixelPerfect = v)) : null,
            isPixel && !erase ? BUI.Toggle("명암 잉크 (밑 색을 팔레트에서 한 칸씩 어둡게/밝게)", () => P.Shading, SetB((p, v) => p.Shading = v, true)) : null,
            cur.Shading ? BUI.Seg(new[] { (-1.0, "어둡게"), (1.0, "밝게") }, () => P.ShadeDir < 0 ? -1.0 : 1.0, v => { P.ShadeDir = v; Changed(); }) : null,
            cur.Shading ? BUI.Help("팔레트에서 같은 계열 색(비슷한 색상, 회색은 회색끼리)을 밝기 순으로 세운 사다리에서 한 칸씩 옮깁니다 (한 획에 한 번). Alt 를 누르고 그리면 반대로. 팔레트에 없는 색은 밝기만 조금 바뀝니다.") : null,
            !isPixel && !sprayFlat ? BUI.Toggle("리본 (끝 모양을 획 방향으로 이어 붙여 늘리기)", () => P.Ribbon, SetB((p, v) => p.Ribbon = v)) : null,
            cur.Tip == "spray" ? BUI.Slider("입자 수", 2, 120, () => P.Density, SetD((p, v) => p.Density = v), v => v.ToString("0"), step: 1) : null,
            cur.Tip == "spray" ? BUI.Slider("입자 크기", 0.3, 8, () => P.Particle, SetD((p, v) => p.Particle = v), v => v.ToString("0.0"), step: 0.1) : null,
            cur.Tip == "flat" ? BUI.Slider("납작한 정도", 0.05, 1, () => P.FlatRatio, SetD((p, v) => p.FlatRatio = v), BUI.Pct, step: 0.01) : null,
            cur.Tip == "flat" ? BUI.Slider("펜 각도", -90, 90, () => P.Angle, SetD((p, v) => p.Angle = v), v => $"{v:0}°", step: 1) : null,
            cur.Tip == "flat" ? BUI.Toggle("펜 기울기·돌림으로 각도 정하기", () => P.UseTilt, SetB((p, v) => p.UseTilt = v)) : null,
            !(cur.Tip is "image" or "spray" or "flat" or "pixel") ? BUI.Slider("경도", 0, 1, () => P.Hardness, SetD((p, v) => p.Hardness = v), BUI.Pct, step: 0.01) : null,
            cur.Tip == "grain" ? BUI.Slider("입자", 0, 1, () => P.Grain, SetD((p, v) => p.Grain = v), BUI.Pct, step: 0.01) : null,
            cur.Tip == "image" ? TipImagePicker() : null,
            BUI.Slider("흐름", 0.01, 1, () => P.Flow, SetD((p, v) => p.Flow = v), BUI.Pct, step: 0.01),
            BUI.Slider("간격", 0.02, 2, () => P.Spacing, SetD((p, v) => p.Spacing = v), BUI.Pct, log: true, step: 0.01),
            BUI.Toggle("덧칠 누적 (에어브러시)", () => P.Buildup, SetB((p, v) => p.Buildup = v))));
        if (!erase)
        {
            add.Add(BUI.Section("동작 (기울기·속도·색)",
                BUI.Slider("펜을 눕히면 굵게", 0, 1, () => P.TiltSize, SetD((p, v) => p.TiltSize = v), v => BUI.OffOr(v, BUI.Pct), step: 0.01),
                BUI.Slider("펜을 눕히면 옅게", 0, 1, () => P.TiltOpacity, SetD((p, v) => p.TiltOpacity = v), v => BUI.OffOr(v, BUI.Pct), step: 0.01),
                BUI.Slider("빠를수록 옅게", 0, 1, () => P.SpeedOpacity, SetD((p, v) => p.SpeedOpacity = v), v => BUI.OffOr(v, BUI.Pct), step: 0.01),
                BUI.Toggle("필압으로 색 바뀜 (약하게 = 배경색, 세게 = 전경색)", () => P.PressureColor, SetB((p, v) => p.PressureColor = v)),
                BUI.Slider("색상 흔들림", 0, 0.5, () => P.HueJitter, SetD((p, v) => p.HueJitter = v), v => BUI.OffOr(v, x => $"{Math.Round(x * 360)}°"), step: 0.01),
                BUI.Slider("채도 흔들림", 0, 1, () => P.SatJitter, SetD((p, v) => p.SatJitter = v), v => BUI.OffOr(v, BUI.Pct), step: 0.01),
                BUI.Slider("명도 흔들림", 0, 1, () => P.ValJitter, SetD((p, v) => p.ValJitter = v), v => BUI.OffOr(v, BUI.Pct), step: 0.01)));
            bool tex = cur.Texture != null && cur.Texture != "none";
            add.Add(BUI.Section("종이 결",
                BUI.Seg(PaperGrain.Types, () => P.Texture ?? "none", v => { P.Texture = v; Changed(); Root.BeginInvoke(Rebuild); }),
                tex ? BUI.Slider("결 세기", 0.05, 1, () => P.TextureStrength, SetD((p, v) => p.TextureStrength = v), BUI.Pct, step: 0.01) : null,
                tex ? BUI.Slider("결 크기", 0.25, 4, () => P.TextureScale, SetD((p, v) => p.TextureScale = v), v => $"{v:0.00}배", step: 0.05) : null,
                tex ? BUI.Seg(new[] { ("multiply", "곱하기"), ("subtract", "빼기"), ("hard", "문턱 (또렷)") }, () => P.TextureMode ?? "multiply", v => { P.TextureMode = v; Changed(); }) : null));
        }
        if (!isPixel && !sprayFlat)
        {
            bool dual = cur.HasDual;
            add.Add(BUI.Section("듀얼 브러시 (두 번째 끝 모양으로 가리기)",
                BUI.Seg(new[] { ("none", "끔"), ("grain", "거친 결"), ("round", "원"), ("spray", "점") }, () => P.DualTip ?? "none", v => { P.DualTip = v; Changed(); Root.BeginInvoke(Rebuild); }),
                dual ? BUI.Slider("두 번째 크기", 0.05, 1, () => P.DualSize, SetD((p, v) => p.DualSize = v), BUI.Pct, step: 0.01) : null,
                dual ? BUI.Slider("찍는 수", 1, 30, () => P.DualDensity, SetD((p, v) => p.DualDensity = v), v => v.ToString("0"), step: 1) : null,
                dual ? BUI.Slider("퍼짐", 0, 1.5, () => P.DualScatter, SetD((p, v) => p.DualScatter = v), BUI.Pct, step: 0.05) : null));
        }
        add.Add(BUI.Section("디더 무늬 (점 무늬로 칠하기)",
            BUI.Slider("칠하는 칸 비율", 0, 1, () => P.Dither, v => { P.Dither = v >= 1 ? 0 : v; Changed(); }, v => v > 0 && v < 1 ? BUI.Pct(v) : "끔", step: 0.0625),
            BUI.Seg(new[] { (2.0, "2×2"), (4.0, "4×4") }, () => P.DitherSize == 2 ? 2.0 : 4.0, v => { P.DitherSize = v; Changed(); })));
        if (cur.Tip == "image" || cur.Scatter != 0 || cur.RotJitter != 0 || cur.SizeJitter != 0 || showScatter)
            add.Add(BUI.Section("흩뿌리기",
                BUI.Slider("흩뿌림", 0, 3, () => P.Scatter, SetD((p, v) => p.Scatter = v), v => v.ToString("0.00"), step: 0.05),
                BUI.Slider("회전 랜덤", 0, 1, () => P.RotJitter, SetD((p, v) => p.RotJitter = v), v => $"{Math.Round(v * 360)}°", step: 0.01),
                BUI.Slider("크기 랜덤", 0, 1, () => P.SizeJitter, SetD((p, v) => p.SizeJitter = v), BUI.Pct, step: 0.01),
                BUI.Toggle("진행 방향으로 회전", () => P.FollowDir, SetB((p, v) => p.FollowDir = v))));
        else
            add.Add(Opt.Button("흩뿌리기 설정 보기", () => { showScatter = true; Root.BeginInvoke(Rebuild); }));
        if (erase && App.Doc?.Active?.Kind == "vector")
            add.Add(BUI.Section("벡터 지우개",
                BUI.Seg(new[] { ("part", "닿은 부분만"), ("cross", "교차점까지"), ("whole", "선 전체") }, () => App.Get("vectorErase", "part"), v => { App.Set("vectorErase", v); })));
        if (!erase && cur.RoughOnly) add.Add(RoughSection());
        if (!erase) add.Add(BUI.Section("러프", BUI.Toggle("러프 레이어 전용으로 쓰기", () => P.RoughOnly, SetB((p, v) => p.RoughOnly = v, true))));
        if (!erase)
        {
            add.Add(BUI.Section("합성 · 혼색",
                BUI.Select("합성 모드", new[] { ("source-over", "표준"), ("multiply", "곱하기"), ("screen", "스크린"), ("overlay", "오버레이"), ("color-dodge", "닷지 (발광)"), ("lighter", "더하기 (발광)"), ("color-burn", "번"), ("soft-light", "소프트 라이트"), ("hard-light", "하드 라이트"), ("lighten", "밝게"), ("darken", "어둡게"), ("color", "컬러"), ("hue", "색조"), ("saturation", "채도"), ("luminosity", "광도") }, () => P.Blend ?? "source-over", v => { P.Blend = v; Changed(); }),
                BUI.Slider("혼색 (밑색과 섞기)", 0, 1, () => P.Mix, SetD((p, v) => p.Mix = v), v => BUI.OffOr(v, BUI.Pct), step: 0.01)));
        }
        if (!erase && !cur.RoughOnly)
        {
            var colorBtn = cur.Color != null ? ColorButton() : null;
            add.Add(BUI.Section("색", Opt.Row(BUI.Toggle("브러시 전용 색 사용", () => P.Color != null, v => { P.Color = v ? (P.Color ?? "#3d7be0") : null; Changed(); Root.BeginInvoke(Rebuild); }), colorBtn)));
        }
        body.Controls.AddRange(add.Where(c => c != null).ToArray());
        body.ResumeLayout();
        RenderList();
    }

    Control ColorButton()
    {
        var b = new Button { Width = 40, Height = 22, FlatStyle = FlatStyle.Flat, BackColor = ColorTranslator.FromHtml(P.Color ?? "#3d7be0"), Margin = new Padding(6, 2, 0, 2) };
        b.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = b.BackColor, FullOpen = true };
            if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
            P.Color = $"#{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}"; b.BackColor = dlg.Color; Changed();
        };
        return b;
    }

    /// <summary>러프 색 (의견 색) 14가지</summary>
    Control RoughSection()
    {
        var act = App.Doc?.Active;
        string cur = act?.Rough == true ? act.Tint : PaintTool.DefaultRoughColor();
        var grid = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(BUI.Width, 0), AutoSize = true, BackColor = Opt.Bg };
        foreach (var (hex, name) in RoughColors)
        {
            var b = new Button { Text = name, Width = 62, Height = 24, FlatStyle = FlatStyle.Flat, BackColor = ColorTranslator.FromHtml(hex), ForeColor = Color.White, Margin = new Padding(0, 0, 3, 3), Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8f, hex == cur ? FontStyle.Bold : FontStyle.Regular) };
            b.FlatAppearance.BorderColor = hex == cur ? Color.White : Color.FromArgb(74, 74, 84);
            b.FlatAppearance.BorderSize = hex == cur ? 2 : 1;
            b.Click += (_, _) => { SetRoughColor(App.Doc?.Active, hex); Rebuild(); };
            grid.Controls.Add(b);
        }
        return BUI.Section("러프 색 (의견 색)", grid,
            BUI.Help(act?.Rough == true ? "지금 러프 레이어의 색을 바꿉니다. 협업할 때 사람·의견마다 색을 나눠 쓰세요." : "러프 레이어를 고르면 그 레이어 색이 바뀝니다. 지금 고른 색은 새 러프 레이어에 쓰입니다."));
    }

    static void SetRoughColor(Layer layer, string color)
    {
        App.Set("roughColor", color);
        if (layer?.Rough != true || App.Doc == null) return;
        string cname = RoughColors.FirstOrDefault(c => c.hex == color).name ?? color;
        App.History.Struct(App.Doc, "러프 색", () =>
        {
            layer.Tint = color;
            if (System.Text.RegularExpressions.Regex.IsMatch(layer.Name, @"^러프 \(.+\)$") || layer.Name == "러프") layer.Name = $"러프 ({cname})";
        });
        App.NotifyLayers(); App.InvalidateAll();
    }

    Control TipImagePicker()
    {
        var items = new List<(string id, string text)> { ("", "(에셋 선택)") };
        try
        {
            string dir = Tips.AssetsDir;
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories).Take(2000))
                {
                    string rel = Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/');
                    items.Add((rel, rel));
                }
        }
        catch { }
        if (P.TipImage != null && !items.Any(i => i.id == P.TipImage)) items.Add((P.TipImage, P.TipImage));
        var sel = BUI.Select("이미지", items.ToArray(), () => P.TipImage ?? "", v =>
        {
            P.TipImage = string.IsNullOrEmpty(v) ? null : v;
            if (P.TipImage != null && Tips.LoadImage(P.TipImage) == null) App.Toast("이미지를 불러오지 못했습니다");
            Changed();
        });
        var pick = Opt.Button("PNG 파일 고르기…", () =>
        {
            using var dlg = new OpenFileDialog { Filter = "PNG 그림 (*.png)|*.png" };
            if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
            try
            {
                string rel = "brushes/user/" + Path.GetFileName(dlg.FileName);
                string full = Path.Combine(Tips.AssetsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                if (!string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase)) File.Copy(dlg.FileName, full, true);
                Tips.ForgetImage(rel);
                P.TipImage = rel; Changed(); Rebuild();
            }
            catch (Exception ex) { App.Toast("이미지를 불러오지 못했습니다: " + ex.Message); }
        });
        var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Opt.Bg };
        box.Controls.Add(sel); box.Controls.Add(pick);
        box.Controls.Add(BUI.Toggle("현재 색으로 칠하기", () => P.TintTip, SetB((p, v) => p.TintTip = v)));
        return box;
    }
}
