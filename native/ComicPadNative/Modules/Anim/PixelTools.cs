// 픽셀아트 모드 — 웹 판 app/js/ui/pixel.js (+ 브러시의 팔레트 고정)
//  - 새 픽셀 작품 · 픽셀 격자 · 픽셀아트 보기 · 팔레트 고정 · 색 수 줄이기 · 팔레트 파일 · 팔레트 색 바꾸기
//  - 픽셀 연필 도구: 1px(크기 조절), 픽셀 퍼펙트(L자 모서리 없애기), 디더 농도, 명암 잉크, 팔레트 고정. 많이 확대하면 픽셀 격자
//  - 팔레트 고정 작품: 전경색을 팔레트의 가까운 색으로, 다른 도구로 그린 획도 끝나면 팔레트 색 + 반투명 없애기
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Anim;

public static class PixelTools
{
    // ---------------------------------------------------------------------
    // 픽셀 격자 (6배 이상 확대했을 때) — 픽셀 연필·모션 경로 도구의 겹침 그림으로 그림
    // ---------------------------------------------------------------------
    public static bool GridOn => App.Get("pixelGrid", false);

    static (Rect doc, Rectangle screen)? Visible(ViewXf v)
    {
        var d = App.Doc;
        var cv = (App.Main as MainForm)?.Canvas;
        if (d == null || cv == null || !GridOn || v.Zoom < 6) return null;
        var sz = cv.ClientSize;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (sx, sy) in new[] { (0, 0), (sz.Width, 0), (0, sz.Height), (sz.Width, sz.Height) })
        {
            var (dx, dy) = v.ToDoc(sx, sy);
            x0 = Math.Min(x0, dx); y0 = Math.Min(y0, dy); x1 = Math.Max(x1, dx); y1 = Math.Max(y1, dy);
        }
        var r = Rect.FromPoints(x0, y0, x1, y1, 1).Clip(d.W, d.H);
        if (r.IsEmpty) return null;
        return (r, new Rectangle(0, 0, sz.Width, sz.Height));
    }

    public static Rectangle GridBounds(ViewXf v) => Visible(v)?.screen ?? Rectangle.Empty;

    public static void DrawGrid(Graphics g, ViewXf v)
    {
        var vis = Visible(v);
        if (vis == null) return;
        var r = vis.Value.doc;
        using var pen = new Pen(Color.FromArgb(46, 128, 128, 128), 1);
        var old = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        PointF S(double x, double y) { var (sx, sy) = v.ToScreen(x, y); return new PointF((float)sx, (float)sy); }
        for (int x = r.X; x <= r.R; x++) g.DrawLine(pen, S(x, r.Y), S(x, r.B));
        for (int y = r.Y; y <= r.B; y++) g.DrawLine(pen, S(r.X, y), S(r.R, y));
        g.SmoothingMode = old;
    }

    // ---------------------------------------------------------------------
    // 팔레트 고정
    // ---------------------------------------------------------------------
    static bool snapping;
    /// <summary>전경색이 바뀌면 팔레트의 가까운 색으로 (팔레트 고정 작품)</summary>
    public static void OnColorChanged()
    {
        var d = App.Doc;
        if (snapping || d == null || !PixelArt.IsLocked(d)) return;
        var pal = PixelArt.PaletteOf(d);
        if (pal.Count == 0) return;
        uint c = PixelArt.NearestColor(App.Fg | 0xFF000000, pal);
        if (c == App.Fg) return;
        snapping = true;
        try { App.SetColor(c); } finally { snapping = false; }
    }

    /// <summary>
    /// 그리기 기록을 확정하기 직전 (팔레트 고정 작품): 바뀐 픽셀을 팔레트 색 + 반투명 없애기로 맞춘다. (PixelEdit.Finishing 훅)
    /// </summary>
    public static void OnPixelsFinishing(PixelEdit pe)
    {
        var d = App.Doc;
        if (d == null || !PixelArt.IsLocked(d)) return;
        if (pe.Label == "지우기" || pe.Label == "색 수 줄이기" || pe.Label == "팔레트 색 바꾸기") return;
        if (pe.Layer == null || pe.Layer.EditMask || pe.Target != pe.Layer.Px) return;
        var pal = PixelArt.PaletteOf(d);
        if (pal.Count == 0) return;
        var s = pe.Target;
        foreach (var ti in pe.TouchedTiles.ToList())
        {
            var now = s.TileAt(ti);
            if (now == null) continue;
            var bt = pe.BeforeTile(ti);
            PixelArt.SnapToPalette(now, pal, true, i => bt == null ? now[i] != 0 : now[i] != bt[i]);
            bool any = false; foreach (var v in now) if (v != 0) { any = true; break; }
            if (!any) s.SetTile(ti % s.TX, ti / s.TX, null);
        }
        App.Invalidate(pe.Dirty);
    }

    // ---------------------------------------------------------------------
    // 메뉴 명령
    // ---------------------------------------------------------------------
    public static void NewPixelDoc()
    {
        if (!FileCommands.ConfirmDiscard()) return;
        var v = Ui.Form("픽셀 아트 새로 만들기", new[]
        {
            Ui.Field.Seg("size", "크기", new[] { ("16", "16"), ("32", "32"), ("64", "64"), ("128", "128"), ("256", "256"), ("custom", "직접") }, "64"),
            Ui.Field.Num("w", "가로 (직접일 때)", 48, 1, 4096),
            Ui.Field.Num("h", "세로 (직접일 때)", 48, 1, 4096),
            Ui.Field.Chk("clear", "투명 바탕 (스프라이트용)", true),
            Ui.Field.Chk("lock", "팔레트 색으로만 칠하기", false),
            Ui.Field.Info("픽셀 연필(1px, 픽셀 퍼펙트)로 시작하고, 확대하면 픽셀 격자가 보입니다. 애니메이션은 애니 패널에서."),
        }, "만들기");
        if (v == null) return;
        bool custom = v.S("size") == "custom";
        int w = custom ? Math.Max(1, v.I("w")) : int.Parse(v.S("size")), h = custom ? Math.Max(1, v.I("h")) : int.Parse(v.S("size"));
        var d = Document.New(w, h, $"픽셀 {w}×{h}");
        d.PaperVisible = !v.B("clear");
        d.Dpi = 72;
        d.Extra["pixel"] = new JsonObject { ["art"] = true, ["lock"] = v.B("lock"), ["palette"] = null };
        App.Set("pixelGrid", true);
        App.SetDoc(d);
        App.SetTool("pixel");
        App.InvalidateAll();
    }

    public static void ToggleGrid()
    {
        App.Set("pixelGrid", !GridOn);
        App.NotifySettings();
        (App.Main as MainForm)?.Canvas.RedrawAll();
        App.Toast(GridOn ? "픽셀 격자: 켜짐 (6배 이상 확대하면 보임 — 픽셀 연필·모션 경로 도구에서)" : "픽셀 격자: 꺼짐");
    }

    public static void ToggleCrisp()
    {
        var p = PixelArt.PixelOf(App.Doc, true);
        p["art"] = !J.T(p["art"]);
        App.Doc.Modified = true;
        App.InvalidateAll();
        App.Toast(J.T(p["art"]) ? "픽셀아트 보기: 켜짐" : "픽셀아트 보기: 꺼짐");
    }

    public static void ToggleLock()
    {
        var p = PixelArt.PixelOf(App.Doc, true);
        p["lock"] = !J.T(p["lock"]);
        App.Doc.Modified = true;
        App.Toast(J.T(p["lock"]) ? "팔레트 고정: 브러시·채우기 색이 팔레트 색으로 맞춰지고 반투명 가장자리가 없어집니다" : "팔레트 고정: 꺼짐");
        OnColorChanged();
    }

    static void SetDocPalette(List<string> pal)
    {
        var p = PixelArt.PixelOf(App.Doc, true);
        p["palette"] = pal == null ? null : J.StrArray(pal);
        App.Doc.Modified = true;
    }

    static void SetAppPalette(List<string> pal)
    {
        App.Set("palette", J.StrArray(pal));
        App.NotifySettings();
    }

    public static void Quantize()
    {
        var d = App.Doc; var layer = d?.Active;
        if (!App.CheckEditable(layer)) return;
        var v = Ui.Form("색 수 줄이기 (픽셀아트 팔레트로)", new[]
        {
            Ui.Field.Seg("mode", "색", new[] { ("auto", "그림에서 뽑기"), ("palette", "지금 팔레트 색으로") }, "auto"),
            Ui.Field.Num("n", "색 수 (그림에서 뽑을 때)", 16, 2, 256),
            Ui.Field.Seg("dither", "디더링", new[] { ("none", "없음"), ("bayer2", "격자 2×2"), ("bayer4", "격자 4×4"), ("fs", "오차 확산") }, "none"),
            Ui.Field.Num("strength", "디더 세기", 1, 0.1, 2, 0.1),
            Ui.Field.Chk("setPal", "뽑은 색을 팔레트로 쓰기 (이 작품)", true),
            Ui.Field.Info($"\"{layer.Name}\" 레이어에 적용합니다. 반투명은 반 넘으면 불투명, 아니면 투명이 됩니다. 마음에 안 들면 실행취소."),
        }, "줄이기");
        if (v == null) return;
        var t = layer.Target;
        var full = t.Full;
        var buf = t.Read(full);
        var pal = v.S("mode") == "palette" ? PixelArt.PaletteOf(d) : PixelArt.MedianCut(buf, Math.Max(2, Math.Min(256, v.I("n"))));
        if (pal.Count == 0) { App.Toast("색 수 줄이기: 칠한 픽셀이 없습니다"); return; }
        Ui.Busy("색 수 줄이기", () =>
        {
            var pe = App.History.BeginPixels(layer, "색 수 줄이기");
            pe.Touch(full);
            PixelArt.QuantizeImage(buf, full.W, full.H, pal, v.S("dither"), v.N("strength"));
            t.Write(full, buf);
            pe.Commit();
            return true;
        });
        if (v.S("mode") == "auto" && v.B("setPal"))
        {
            var hex = pal.Select(c => PixelArt.ToHex(c[0], c[1], c[2])).ToList();
            SetDocPalette(hex);
            SetAppPalette(hex);
        }
        d.Modified = true;
        App.InvalidateAll(); App.NotifyLayers();
        App.Toast($"{pal.Count}가지 색으로 줄였습니다");
    }

    public static void ImportPalette()
    {
        using var od = new OpenFileDialog { Filter = "팔레트|*.gpl;*.hex;*.txt;*.pal|모든 파일|*.*" };
        if (od.ShowDialog(App.Main) != DialogResult.OK) return;
        var colors = PixelArt.ParsePaletteFile(File.ReadAllText(od.FileName), od.FileName);
        if (colors.Count == 0) { App.Toast("팔레트 색을 찾지 못했습니다 (.gpl / .hex / .txt)"); return; }
        var v = Ui.Form("팔레트 가져오기", new[]
        {
            Ui.Field.Info($"{Path.GetFileName(od.FileName)}: {colors.Count}가지 색"),
            Ui.Field.Seg("where", "어디에", new[] { ("app", "앱 팔레트 바꾸기"), ("doc", "이 작품 팔레트로만") }, "app"),
        }, "가져오기");
        if (v == null) return;
        if (v.S("where") == "doc") SetDocPalette(colors); else SetAppPalette(colors);
        App.Toast($"팔레트 {colors.Count}색을 가져왔습니다" + (v.S("where") == "app" ? " (색 패널은 다시 열면 바뀜)" : ""));
    }

    public static void ExportPalette()
    {
        var colors = PixelArt.PaletteHex(App.Doc);
        var v = Ui.Form("팔레트 내보내기", new[]
        {
            Ui.Field.Seg("fmt", "형식", new[] { ("gpl", ".gpl (GIMP·Aseprite·Krita)"), ("hex", ".hex (Lospec)") }, "gpl"),
            Ui.Field.Txt("name", "이름", App.Doc?.Name ?? "ComicPad"),
        }, "내보내기");
        if (v == null) return;
        string fmt = v.S("fmt"), name = string.IsNullOrEmpty(v.S("name")) ? "palette" : v.S("name");
        using var sd = new SaveFileDialog { Filter = fmt == "gpl" ? "GIMP 팔레트|*.gpl" : "HEX 팔레트|*.hex", FileName = $"{J.SafeName(name)}.{fmt}", InitialDirectory = AnimExport.ExportsDir };
        if (sd.ShowDialog(App.Main) != DialogResult.OK) return;
        File.WriteAllText(sd.FileName, fmt == "gpl" ? PixelArt.PaletteToGpl(colors, name) : PixelArt.PaletteToHex(colors));
        App.Toast("팔레트 저장: " + sd.FileName);
    }

    public static void ReplaceColor()
    {
        var d = App.Doc; var layer = d?.Active;
        if (!App.CheckEditable(layer)) return;
        var pal = PixelArt.PaletteHex(d);
        string bgHex = Px.ToHex(App.Bg);
        var v = Ui.Form("팔레트 색 바꾸기", new[]
        {
            Ui.Field.Clr("from", "바꿀 색", pal.Contains(bgHex) ? bgHex : pal.FirstOrDefault() ?? "#000000"),
            Ui.Field.Clr("to", "새 색 (지금 전경색)", Px.ToHex(App.Fg)),
            Ui.Field.Num("tol", "허용 오차 (0 = 똑같은 색만)", 0, 0, 128),
            Ui.Field.Chk("pal", "팔레트의 그 색도 바꾸기", true),
            Ui.Field.Info($"\"{layer.Name}\" 레이어의 같은 색 픽셀을 한꺼번에 바꿉니다."),
        }, "바꾸기");
        if (v == null) return;
        var t = layer.Target; var full = t.Full;
        var buf = t.Read(full);
        var pe = App.History.BeginPixels(layer, "팔레트 색 바꾸기");
        pe.Touch(full);
        int n = PixelArt.ReplaceColor(buf, PixelArt.ParseHex(v.S("from")), PixelArt.ParseHex(v.S("to")), v.I("tol"));
        t.Write(full, buf);
        pe.Commit();
        if (v.B("pal"))
        {
            List<string> Swap(List<string> list) => list.Select(c => c.Equals(v.S("from"), StringComparison.OrdinalIgnoreCase) ? v.S("to") : c).ToList();
            if ((PixelArt.PixelOf(d)?["palette"] as JsonArray)?.Count > 0) SetDocPalette(Swap(PixelArt.PaletteHex(d)));
            var app = PixelArt.AppPalette();
            if (app.Count > 0) SetAppPalette(Swap(app));
        }
        d.Modified = true;
        App.InvalidateAll(); App.NotifyLayers();
        App.Toast($"{n}개 픽셀의 색을 바꿨습니다");
    }

    /// <summary>Aseprite 에서 가져온 팔레트를 앱 팔레트로</summary>
    public static void UseImportedPalette()
    {
        var p = Aseprite.PaletteOf(App.Doc);
        if (p == null || p.Count == 0) { App.Toast("가져온 팔레트가 없습니다"); return; }
        SetAppPalette(p.Take(256).ToList());
        App.Toast($"팔레트 {Math.Min(256, p.Count)}색을 앱 팔레트로 썼습니다 (색 패널은 다시 열면 바뀜)");
    }
}

/// <summary>픽셀 연필: 1px 픽셀 퍼펙트·디더·명암 잉크·팔레트 고정</summary>
public sealed class PixelPencilTool : ToolBase
{
    public override string Id => "pixel";
    public override string Name => "픽셀 연필";
    public override string Icon => "▦";
    public override Cursor Cursor => Cursors.Cross;
    public override bool DrawsSizeCircle => false;

    public int Size = 1;
    public bool Perfect = true, Erase;
    public double Dither = 1;          // 농도 (1 = 꽉, 0.5 = 체크무늬 …)
    public int DitherN = 4;
    public string Shade = "off";       // off | dark | light

    PixelEdit edit; Layer layer; uint color;
    int lx, ly;
    readonly List<(int x, int y)> trail = new();
    readonly Dictionary<(int, int), uint> orig = new();
    readonly HashSet<(int, int)> shaded = new();
    List<int[]> pal;
    public override bool Busy => edit != null;

    public override void Down(PointerArgs e)
    {
        layer = App.Active;
        if (!App.CheckEditable(layer)) { layer = null; return; }
        var d = App.Doc;
        bool erase = Erase || e.EraserEnd;
        edit = App.History.BeginPixels(layer, erase ? "지우기" : "그리기");
        pal = PixelArt.PaletteOf(d);
        color = layer.EditMask ? 0xFFFFFFFF : App.Fg | 0xFF000000;
        if (PixelArt.IsLocked(d) && !erase && !layer.EditMask) color = PixelArt.NearestColor(color, pal);
        trail.Clear(); orig.Clear(); shaded.Clear();
        lx = (int)Math.Floor(e.X); ly = (int)Math.Floor(e.Y);
        Plot(lx, ly, erase);
    }

    public override void Move(PointerArgs e)
    {
        if (edit == null) return;
        int x = (int)Math.Floor(e.X), y = (int)Math.Floor(e.Y);
        if (x == lx && y == ly) return;
        bool erase = Erase || e.EraserEnd;
        // 브레젠험 선
        int dx = Math.Abs(x - lx), dy = -Math.Abs(y - ly), sx = lx < x ? 1 : -1, sy = ly < y ? 1 : -1, err = dx + dy;
        int cx = lx, cy = ly;
        while (cx != x || cy != y)
        {
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; cx += sx; }
            if (e2 <= dx) { err += dx; cy += sy; }
            Plot(cx, cy, erase);
        }
        lx = x; ly = y;
    }

    public override void Up(PointerArgs e)
    {
        if (edit == null) return;
        edit.Commit();
        App.Doc.Modified = true;
        edit = null; layer = null;
        App.NotifyLayers();
    }

    public override void Cancel()
    {
        if (edit == null) return;
        var r = edit.Dirty;
        edit.Cancel();
        edit = null;
        App.Invalidate(r);
    }

    void Plot(int x, int y, bool erase)
    {
        var s = edit.Target;
        // 픽셀 퍼펙트: 앞앞 점과 대각선으로 이어지면 가운데 (L자 모서리) 점을 되돌림
        if (Perfect && Size == 1 && trail.Count >= 2)
        {
            var a = trail[^2]; var b = trail[^1];
            if (Math.Abs(a.x - x) == 1 && Math.Abs(a.y - y) == 1 && (b.x == a.x || b.y == a.y) && (b.x == x || b.y == y))
            {
                if (orig.TryGetValue(b, out var ov)) { s.Set(b.x, b.y, ov); App.Invalidate(new Rect(b.x, b.y, 1, 1)); }
                trail.RemoveAt(trail.Count - 1);
            }
        }
        trail.Add((x, y));
        if (trail.Count > 8) trail.RemoveAt(0);
        int h0 = (Size - 1) / 2;
        var r = new Rect(x - h0, y - h0, Size, Size).Clip(s.W, s.H);
        if (r.IsEmpty) return;
        edit.Touch(r);
        for (int yy = r.Y; yy < r.B; yy++)
            for (int xx = r.X; xx < r.R; xx++)
            {
                if (!(Dither >= 1 || Dither > PixelArt.BayerAt(xx, yy, DitherN) - 1e-6)) continue;
                var key = (xx, yy);
                uint old = s.Get(xx, yy);
                if (!orig.ContainsKey(key)) orig[key] = old;
                uint c;
                if (erase) c = 0;
                else if (Shade != "off" && !layer.EditMask)
                {
                    if (!shaded.Add(key) || (old >> 24) == 0) continue;
                    uint u = Px.Unpremul(orig[key]);
                    var st = PixelArt.ShadeStep((int)((u >> 16) & 255), (int)((u >> 8) & 255), (int)(u & 255), pal, Shade == "dark" ? -1 : 1);
                    if (st == null) continue;
                    c = Px.Premul(Px.Pack(u >> 24, (uint)st[0], (uint)st[1], (uint)st[2]));
                }
                else c = color;
                if (layer.AlphaLock && !layer.EditMask && (old >> 24) == 0) continue;
                s.Set(xx, yy, c);
            }
        App.Invalidate(r);
    }

    public override Rectangle OverlayBounds(ViewXf v) => PixelTools.GridBounds(v);
    public override void DrawOverlay(Graphics g, ViewXf v) => PixelTools.DrawGrid(g, v);

    public override bool HandleKey(Keys k)
    {
        if (k == Keys.OemOpenBrackets) { Size = Math.Max(1, Size - 1); App.NotifySettings(); return true; }
        if (k == Keys.OemCloseBrackets) { Size = Math.Min(64, Size + 1); App.NotifySettings(); return true; }
        return false;
    }

    public override Control BuildOptions() => Opt.Panel(
        Opt.Slider("크기 (px)", 1, 32, () => Size, v => Size = (int)Math.Round(v), step: 1),
        Opt.Check("픽셀 퍼펙트 (L자 모서리 없애기, 1px)", () => Perfect, v => Perfect = v),
        Opt.Check("지우개로 쓰기", () => Erase, v => Erase = v),
        Opt.Slider("디더 농도 (1 = 꽉 채움)", 0.0625, 1, () => Dither, v => Dither = v, pct: true, step: 0.0625),
        Opt.Choice("디더 무늬", new[] { ("4", "격자 4×4"), ("2", "격자 2×2") }, () => DitherN.ToString(), v => DitherN = int.Parse(v)),
        Opt.Choice("명암 잉크 (팔레트 사다리 한 칸)", new[] { ("off", "끄기"), ("dark", "어둡게"), ("light", "밝게") }, () => Shade, v => Shade = v),
        Opt.Check("픽셀 격자 (6배 이상 확대)", () => PixelTools.GridOn, v => { App.Set("pixelGrid", v); (App.Main as MainForm)?.Canvas.RedrawAll(); }),
        Opt.Help("팔레트 고정 작품이면 색이 팔레트의 가까운 색으로 맞춰집니다 (필터 > 팔레트 색으로만 칠하기)."));
}
