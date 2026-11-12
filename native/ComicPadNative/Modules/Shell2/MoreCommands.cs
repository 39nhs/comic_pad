// 웹 판에 있고 네이티브에 없던 명령들 (웹 commands.js / commands2.js / main.js 와 같은 동작):
// 러프·펜선·채색 레이어, 보이는 레이어 결합, 레이어 지우기, 폴더로 묶기·합치기, 레이어 컬러, 마스크 편집·켜기·적용·삭제,
// 가로 폭 맞춤, 격자, 패널 숨기기·전체 화면, PNG 빠른 내보내기, 폴더 열기, 에셋 저장, 글꼴 추가, 전경/배경색 바꾸기, 참조 이미지 창.
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.BrushEngine;
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.Filters;
using ComicPadNative.UI;
using Field = ComicPadNative.Modules.Extras.Field;

namespace ComicPadNative.Modules.Shell2;

public static class MoreCommands
{
    static Document D => App.Doc;
    static void Done() { App.NotifyLayers(); App.InvalidateAll(); }

    /// <summary>새 레이어 (지금 레이어 위, 속성까지 실행취소 한 번)</summary>
    public static Layer AddLayer(string name, Action<Layer> init, string kind = "raster")
    {
        var d = D; Layer l = null;
        App.History.Struct(d, "새 레이어", () =>
        {
            var a = d.Active;
            l = new Layer(d, name, kind) { Parent = a?.IsFolder == true && a.Expanded ? a.Id : a?.Parent };
            init?.Invoke(l);
            int i = a == null ? d.Layers.Count : d.Layers.IndexOf(a) + 1;
            d.Layers.Insert(i, l); d.ActiveId = l.Id;
        });
        d.Modified = true;
        Done();
        return l;
    }

    // ---- 러프 (웹 core/rough.js) ----------------------------------------------------------------
    public static string ColorName(string hex) => BrushOptions.RoughColors.FirstOrDefault(c => c.hex == hex).name ?? hex;

    public static Layer NewRough()
    {
        string c = PaintTool.DefaultRoughColor();
        var l = AddLayer($"러프 ({ColorName(c)})", x => { x.Rough = true; x.Tint = c; x.Opacity = 0.6f; x.Draft = true; });
        UseBrush("rough");
        return l;
    }
    public static Layer NewLine() { var l = AddLayer("펜선", null); UseBrush("gpen"); return l; }
    public static Layer NewColor() => AddLayer("채색", x => x.Clip = true);

    static void UseBrush(string id)
    {
        if (PresetStore.List("brush").Any(p => p.Id == id)) PresetStore.SetActive("brush", id);
        App.SetTool("brush");
        App.NotifySettings();
    }

    public static void SetRough(Layer l, bool on)
    {
        if (l == null || l.IsFolder || l.IsAdjust) return;
        string c = PaintTool.DefaultRoughColor();
        App.History.Struct(D, on ? "러프 레이어로" : "일반 레이어로", () =>
        {
            if (on) { l.Rough = true; l.Tint = c; l.Draft = true; if (l.Opacity == 1) l.Opacity = 0.6f; }
            else { l.Rough = false; l.Tint = null; l.Draft = false; }
        });
        Done();
    }

    public static void RoughColorDialog()
    {
        var l = D?.Active;
        var v = Dlg.Form(l?.Rough == true ? $"러프 색: {l.Name}" : "새 러프 레이어 색", new[]
        {
            Field.Segment("c", "색", l?.Rough == true && l.Tint != null ? l.Tint : PaintTool.DefaultRoughColor(), BrushOptions.RoughColors.Select(x => (x.hex, x.name)).ToArray()),
        }, "적용");
        if (v == null) return;
        string color = (string)v["c"];
        App.Set("roughColor", color);
        if (l?.Rough != true) return;
        App.History.Struct(D, "러프 색", () =>
        {
            l.Tint = color;
            if (System.Text.RegularExpressions.Regex.IsMatch(l.Name, @"^러프 \(.+\)$") || l.Name == "러프") l.Name = $"러프 ({ColorName(color)})";
        });
        Done();
    }

    // ---- 레이어 ----------------------------------------------------------------------------------
    /// <summary>보이는 최상위 레이어들을 하나로 (웹 flattenVisible: 밑그림도 포함, 용지 빼고)</summary>
    public static void FlattenVisible()
    {
        var d = D; if (d == null) return;
        var flat = App.Comp.Flatten(d, new RenderOptions { Paper = false, ExcludeDraft = false });
        App.History.Struct(d, "표시 레이어 결합", () =>
        {
            var l = new Layer(d, "결합");
            l.Px.Write(flat.Full, flat.Read(flat.Full));
            l.Touch();
            var gone = d.Layers.Where(x => x.Visible && d.ParentOf(x) == null).ToList();
            foreach (var g in gone.ToList()) if (g.IsFolder) gone.AddRange(d.Layers.Where(x => d.IsInside(x, g)));
            foreach (var g in gone.Distinct()) d.Layers.Remove(g);
            d.Layers.Add(l); d.ActiveId = l.Id;
        });
        d.Modified = true;
        Done();
    }

    /// <summary>레이어 내용 지우기 (선택 영역이 있으면 그 안만, 벡터는 선 모두)</summary>
    public static void ClearLayer()
    {
        var d = D; var l = d?.Active;
        if (l?.Kind == "vector") { VectorLayer.SetStrokes(l, new JsonArray(), "벡터 지우기"); Done(); return; }
        if (!App.CheckEditable(l)) return;
        var t = l.Target;
        var r = d.Sel.Active ? d.Sel.Bounds() : t.Bounds();
        if (r.IsEmpty) return;
        var pe = App.History.BeginPixels(l, "지우기");
        pe.Touch(r);
        if (d.Sel.Active)
        {
            var buf = t.Read(r);
            for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++) { byte s = d.Sel.At(r.X + x, r.Y + y); if (s > 0) buf[y * r.W + x] = Px.Scale(buf[y * r.W + x], 255u - s); }
            t.Write(r, buf);
        }
        else t.Clear(r);
        pe.Commit();
        d.Modified = true;
        App.Invalidate(r); App.NotifyLayers();
    }

    /// <summary>지금 레이어를 새 폴더에 넣기</summary>
    public static void GroupIntoFolder()
    {
        var d = D; var a = d?.Active; if (a == null) return;
        App.History.Struct(d, "폴더로 묶기", () =>
        {
            var f = new Layer(d, "폴더", "folder") { Parent = d.ParentOf(a) };
            d.Layers.Insert(d.Layers.IndexOf(a) + 1, f);
            a.Parent = f.Id;
            d.ActiveId = f.Id;
        });
        Done();
    }

    /// <summary>폴더 안을 합성해 한 레이어로</summary>
    public static void MergeFolder()
    {
        var d = D; var f = d?.Active;
        if (f?.IsFolder != true) { App.Toast("폴더를 고르세요"); return; }
        var inside = d.Layers.Where(x => d.IsInside(x, f)).ToList();
        var only = new HashSet<Layer>(inside.Where(x => x.Visible && x.Px != null || x.IsAdjust)) { f };
        var full = new Rect(0, 0, d.W, d.H);
        var px = new Surface(d.W, d.H);
        if (f.Visible)
            for (int y = 0; y < d.H; y += Surface.TS)
                for (int x = 0; x < d.W; x += Surface.TS)
                {
                    var part = Rect.FromLTRB(x, y, Math.Min(d.W, x + Surface.TS), Math.Min(d.H, y + Surface.TS));
                    px.Write(part, App.Comp.Render(d, part, new RenderOptions { Paper = false, Only = only }));
                }
        App.History.Struct(d, "폴더 합치기", () =>
        {
            var l = new Layer(d, f.Name) { Parent = d.ParentOf(f), Opacity = f.Opacity, Blend = f.Blend == "pass-through" ? "source-over" : f.Blend };
            l.Px = px; l.Touch();
            d.Layers.Insert(d.Layers.IndexOf(f) + 1, l);
            foreach (var g in inside) d.Layers.Remove(g);
            d.Layers.Remove(f);
            d.ActiveId = l.Id;
        });
        d.Modified = true;
        Done();
    }

    public static void ToggleTint()
    {
        var l = D?.Active; if (l == null) return;
        App.History.Struct(D, "레이어 컬러", () => l.Tint = l.Tint == null ? "#3d7be0" : null);
        Done();
    }

    // ---- 마스크 ----------------------------------------------------------------------------------
    public static void EditMask()
    {
        var l = D?.Active; if (l == null) return;
        if (l.Mask == null) { App.Toast("마스크가 없습니다 (레이어 > 레이어 마스크 추가)"); return; }
        l.EditMask = !l.EditMask;
        App.Toast(l.EditMask ? "마스크 편집: 검정으로 그리면 숨김, 흰색은 보임" : "그림 편집으로 돌아왔습니다");
        App.NotifyLayers();
    }
    public static void MaskOn()
    {
        var l = D?.Active; if (l?.Mask == null) return;
        App.History.Struct(D, "마스크 켜기/끄기", () => l.MaskOn = !l.MaskOn);
        Done();
    }
    public static void DeleteMask()
    {
        var l = D?.Active; if (l?.Mask == null) return;
        App.History.Struct(D, "마스크 삭제", () => { l.Mask = null; l.EditMask = false; });
        Done();
    }
    /// <summary>마스크를 그림에 적용하고 마스크 제거 (실행취소 한 번)</summary>
    public static void ApplyMask()
    {
        var d = D; var l = d?.Active; if (l?.Mask == null || l.Px == null) return;
        var mask = l.Mask;
        var pe = new PixelEdit(App.History, l, l.Px, "마스크 적용");
        var r = l.Px.Bounds();
        pe.Touch(r);
        var buf = l.Px.Read(r); var m = mask.Read(r);
        for (int i = 0; i < buf.Length; i++) buf[i] = Px.Scale(buf[i], m[i] >> 24);
        l.Px.Write(r, buf);
        var px = pe.Finish("마스크 적용");
        var before = StructSnap.Take(d);
        l.Mask = null; l.EditMask = false;
        App.History.PushGroup("마스크 적용", px, new StructEdit("마스크 적용", before, StructSnap.Take(d)));
        d.Modified = true; l.Touch();
        Done();
    }

    // ---- 보기 ------------------------------------------------------------------------------------
    public static void FitWidth()
    {
        var c = Shell2Ui.Canvas; var d = D; if (c == null || d == null) return;
        c.V.Angle = 0; c.V.Flip = false;
        c.V.Zoom = Math.Max(0.02, (c.Width - 40.0) / d.W);
        c.V.PanX = (c.Width - d.W * c.V.Zoom) / 2; c.V.PanY = 20;
        c.RedrawAll();
    }

    /// <summary>격자 (설정 showGrid, gridSize 문서 px)</summary>
    public static void DrawGrid(Graphics g, ViewXf v)
    {
        var d = D; if (d == null || !App.Get("showGrid", false)) return;
        double step = Math.Max(4, App.Get("gridSize", 100.0));
        if (step * v.Zoom < 6) return;
        using var p = new Pen(Color.FromArgb(70, 60, 120, 220));
        using var p2 = new Pen(Color.FromArgb(120, 60, 120, 220));
        int n = 0;
        for (double x = 0; x <= d.W; x += step, n++) { var a = v.ToScreen(x, 0); var b = v.ToScreen(x, d.H); g.DrawLine(n % 5 == 0 ? p2 : p, (float)a.x, (float)a.y, (float)b.x, (float)b.y); }
        n = 0;
        for (double y = 0; y <= d.H; y += step, n++) { var a = v.ToScreen(0, y); var b = v.ToScreen(d.W, y); g.DrawLine(n % 5 == 0 ? p2 : p, (float)a.x, (float)a.y, (float)b.x, (float)b.y); }
    }
    public static void ToggleGrid() { App.Set("showGrid", !App.Get("showGrid", false)); Shell2Ui.Canvas?.RedrawAll(); }

    public static void SwapColors() { uint f = App.Fg, b = App.Bg; App.SetColor(b); App.SetColor(f, true); }

    public static void ToggleFingerDraw()
    {
        bool v = !App.Get("fingerDraw", false);
        App.Set("fingerDraw", v); App.NotifySettings();
        App.Toast(v ? "손가락으로 그리기: 켜짐" : "손가락으로 그리기: 꺼짐 (손가락은 화면 이동/확대)");
    }

    // ---- 파일 ------------------------------------------------------------------------------------
    public static string ExportsDir { get { var p = Path.Combine(App.DataDir, "exports"); Directory.CreateDirectory(p); return p; } }

    /// <summary>PNG 빠른 내보내기 (대화상자 없이 data/exports 에). 만든 경로</summary>
    public static string ExportQuick(Document d = null, string dir = null)
    {
        d ??= D; if (d == null) return null;
        var flat = App.Comp.Flatten(d);
        using var bmp = CpadIO.ToBitmap(flat, flat.Full);
        string path = Path.Combine(dir ?? ExportsDir, $"{U.SafeName(d.Name)}_{U.FileStamp()}.png");
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    // ---- 에셋 (data/assets) -----------------------------------------------------------------------
    public static string AssetsDir { get { var p = Path.Combine(App.DataDir, "assets"); Directory.CreateDirectory(p); return p; } }

    /// <summary>에셋 파일 경로 (폴더 이름의 위험한 글자는 바꿈, data/assets 밖으로 못 나감)</summary>
    public static string AssetPath(string name, string folder)
    {
        string Safe(string s) => string.Join("_", (s ?? "").Split(Path.GetInvalidFileNameChars())).Trim();
        var parts = (folder ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Safe).Where(x => x.Length > 0 && x != "." && x != "..");
        string dir = Path.Combine(new[] { AssetsDir }.Concat(parts).ToArray());
        Directory.CreateDirectory(dir);
        string n = Safe(name); if (n.Length == 0) n = "에셋";
        string p = Path.Combine(dir, n + ".png");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{n} ({i}).png");
        return p;
    }

    public static void AssetSave(bool merged, string label)
    {
        var d = D; if (d == null) return;
        var r = ImageIO.Extract(d.Active, merged);
        if (r == null) { App.Toast("저장할 그림이 없습니다"); return; }
        var (px, rect) = r.Value;
        var v = Dlg.Form(label, new[]
        {
            Field.Text("name", "이름", merged ? d.Name : d.Active?.Name),
            Field.Text("folder", "폴더 (선택, 예: 배경/나무 — data/assets 아래에 만들어집니다)", App.Get("lastAssetFolder", "")),
            Field.Info($"{rect.W}×{rect.H}px 투명 PNG (빈 여백은 잘라냄)"),
        }, "저장");
        if (v == null) return;
        App.Set("lastAssetFolder", (string)v["folder"] ?? "");
        string path = AssetPath((string)v["name"], (string)v["folder"]);
        using var bmp = ImageIO.ToBitmap(px, rect.W, rect.H);
        bmp.Save(path, ImageFormat.Png);
        App.Toast("에셋 저장: " + Path.GetRelativePath(App.DataDir, path));
        AssetsPanel.RefreshAll();
    }

    // ---- 글꼴 추가 (data/fonts) -----------------------------------------------------------------
    public static void AddFonts()
    {
        using var dlg = new OpenFileDialog { Filter = "글꼴 (*.ttf;*.otf)|*.ttf;*.otf", Multiselect = true };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        string dir = Path.Combine(App.DataDir, "fonts"); Directory.CreateDirectory(dir);
        int n = 0;
        foreach (var f in dlg.FileNames) { try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); n++; } catch { } }
        Modules.Comic.Fonts.LoadDataFonts();
        App.Toast($"글꼴 {n}개를 추가했습니다 (텍스트 도구 글꼴 목록에 \"내 폰트\" 로 나옵니다)");
    }

    // ---- 참조 이미지 창 (웹 ui/extra.js openReference) ---------------------------------------------
    public static void Reference()
    {
        using var dlg = new OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|모든 파일|*.*", InitialDirectory = Directory.Exists(AssetsDir) ? AssetsDir : null };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        Bitmap img;
        try { img = ImageIO.LoadBitmap(dlg.FileName); } catch (Exception e) { App.Toast("열지 못했습니다: " + e.Message); return; }
        var f = new Form { Text = "참조: " + Path.GetFileName(dlg.FileName), Width = Math.Min(520, img.Width + 20), Height = Math.Min(620, img.Height + 60), TopMost = true, FormBorderStyle = FormBorderStyle.SizableToolWindow, BackColor = Color.FromArgb(40, 40, 44), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        if (App.Main != null) f.Location = new Point(App.Main.Right - f.Width - 360, App.Main.Top + 120);
        var pb = new PictureBox { Dock = DockStyle.Fill, Image = img, SizeMode = PictureBoxSizeMode.Zoom, BackColor = f.BackColor, Cursor = Cursors.Cross };
        var tip = new ToolTip(); tip.SetToolTip(pb, "클릭: 이 색을 전경색으로 · 창을 닫으면 사라짐");
        pb.MouseClick += (_, e) =>
        {
            // 확대 보기에서 클릭한 점의 색
            double s = Math.Min(pb.Width / (double)img.Width, pb.Height / (double)img.Height);
            int ox = (int)((pb.Width - img.Width * s) / 2), oy = (int)((pb.Height - img.Height * s) / 2);
            int x = (int)((e.X - ox) / s), y = (int)((e.Y - oy) / s);
            if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) return;
            var c = img.GetPixel(x, y);
            App.SetColor(0xFF000000u | (uint)(c.R << 16 | c.G << 8 | c.B));
        };
        f.FormClosed += (_, _) => img.Dispose();
        f.Controls.Add(pb);
        f.Show(App.Main);
    }
}
