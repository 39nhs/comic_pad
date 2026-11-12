// 기본 명령: 파일(새로·열기·저장·내보내기), 편집(실행취소·지우기·채우기), 레이어, 선택, 보기.
// 다른 모듈은 Registry.Menu 로 같은 메뉴에 더 붙인다.
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative.UI;

public static class FileCommands
{
    public static void New()
    {
        if (!ConfirmDiscard()) return;
        var v = Opt.AskNumbers("새 작품", ("가로 (px)", App.Get("newW", 2480.0), 16, 20000), ("세로 (px)", App.Get("newH", 3508.0), 16, 20000), ("해상도 (dpi)", App.Get("newDpi", 350.0), 36, 1200));
        if (v == null) return;
        App.Set("newW", v[0]); App.Set("newH", v[1]); App.Set("newDpi", v[2]);
        var d = Document.New((int)v[0], (int)v[1]);
        d.Dpi = (int)v[2];
        App.SetDoc(d);
    }

    public static bool ConfirmDiscard()
    {
        if (App.Doc?.Modified != true) return true;
        var r = MessageBox.Show(App.Main, "저장하지 않은 변경이 있습니다. 저장할까요?", "ComicPad", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return r == DialogResult.No || (r == DialogResult.Yes && Save(false));
    }

    public static string ProjectsDir { get { var p = Path.Combine(App.DataDir, "projects"); Directory.CreateDirectory(p); return p; } }

    public static void Open()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog { Filter = "ComicPad·이미지|*.cpad;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.psd|ComicPad (*.cpad)|*.cpad|모든 파일|*.*", InitialDirectory = ProjectsDir };
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        OpenPath(dlg.FileName);
    }

    /// <summary>열기 담당: 확장자별 (PSD 등은 다른 모듈이 Openers 에 등록)</summary>
    public static readonly Dictionary<string, Func<string, Document>> Openers = new(StringComparer.OrdinalIgnoreCase);

    public static void OpenPath(string path)
    {
        try
        {
            string ext = Path.GetExtension(path);
            Document d;
            if (ext.Equals(".cpad", StringComparison.OrdinalIgnoreCase)) d = CpadIO.Load(path);
            else if (Openers.TryGetValue(ext, out var op)) d = op(path);
            else d = FromImage(path);
            App.SetDoc(d);
            AddRecent(path);
        }
        catch (Exception ex) { MessageBox.Show(App.Main, "열지 못했습니다: " + ex.Message, "ComicPad"); }
    }

    public static Document FromImage(string path)
    {
        using var bmp = new Bitmap(path);
        var d = new Document(bmp.Width, bmp.Height) { Name = Path.GetFileNameWithoutExtension(path) };
        var l = new Layer(d, Path.GetFileName(path));
        CpadIO.DrawBitmap(l.Px, bmp, 0, 0);
        d.Layers.Add(l); d.ActiveId = l.Id;
        return d;
    }

    public static bool Save(bool saveAs)
    {
        var d = App.Doc;
        if (d == null) return false;
        string path = d.FilePath;
        if (saveAs || path == null || !path.EndsWith(".cpad", StringComparison.OrdinalIgnoreCase))
        {
            using var dlg = new SaveFileDialog { Filter = "ComicPad (*.cpad)|*.cpad", InitialDirectory = ProjectsDir, FileName = d.Name + ".cpad" };
            if (dlg.ShowDialog(App.Main) != DialogResult.OK) return false;
            path = dlg.FileName;
            d.Name = Path.GetFileNameWithoutExtension(path);
        }
        try
        {
            // 덮어쓰기 전에 이전 판을 versions 폴더에 (웹 판과 같이 작품마다 최근 30개)
            if (File.Exists(path) && path.StartsWith(ProjectsDir, StringComparison.OrdinalIgnoreCase)) BackupVersion(path);
            CpadIO.Save(d, path);
            AddRecent(path);
            App.Toast("저장함: " + path);
            (App.Main as MainForm)?.UpdateTitle();
            return true;
        }
        catch (Exception ex) { MessageBox.Show(App.Main, "저장하지 못했습니다: " + ex.Message, "ComicPad"); return false; }
    }

    static void BackupVersion(string full)
    {
        try
        {
            string rel = Path.GetRelativePath(ProjectsDir, full)[..^5];
            string dir = Path.Combine(App.DataDir, "versions", rel);
            Directory.CreateDirectory(dir);
            File.Copy(full, Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".cpad"), true);
            foreach (var old in Directory.GetFiles(dir, "*.cpad").OrderByDescending(f => f).Skip(30)) File.Delete(old);
        }
        catch { }
    }

    public static void ExportImage()
    {
        var d = App.Doc;
        using var dlg = new SaveFileDialog { Filter = "PNG|*.png|JPEG|*.jpg|BMP|*.bmp", FileName = d.Name, InitialDirectory = Path.Combine(App.DataDir, "exports") };
        Directory.CreateDirectory(dlg.InitialDirectory);
        if (dlg.ShowDialog(App.Main) != DialogResult.OK) return;
        var flat = App.Comp.Flatten(d);
        bool jpg = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
        using var bmp = CpadIO.ToBitmap(flat, flat.Full, jpg ? 0xFFFFFFFF : 0);
        bmp.Save(dlg.FileName, jpg ? ImageFormat.Jpeg : dlg.FileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Bmp : ImageFormat.Png);
        App.Toast("내보냄: " + dlg.FileName);
    }

    static void AddRecent(string path)
    {
        if (App.Settings["recentFiles"] is not JsonArray a) { a = new JsonArray(); App.Settings["recentFiles"] = a; }
        var ex = a.FirstOrDefault(x => (string)x == path);
        if (ex != null) a.Remove(ex);
        a.Insert(0, path);
        while (a.Count > 12) a.RemoveAt(a.Count - 1);
        App.SaveSettingsSoon();
    }

    /// <summary>자동 저장: data/autosave 에 3분마다 (바뀐 게 있을 때)</summary>
    public static void Autosave()
    {
        var d = App.Doc;
        if (d == null || !d.Modified) return;
        try
        {
            string dir = Path.Combine(App.DataDir, "autosave"); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MakeSafe(d.Name) + ".cpad"), CpadIO.ToJson(d).ToJsonString());
        }
        catch { }
    }
    static string MakeSafe(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n; }
}

public static class LayerCommands
{
    static Document D => App.Doc;
    static void Done() { App.NotifyLayers(); App.InvalidateAll(); }

    public static Layer NewLayer(string name = null, string kind = "raster")
    {
        var d = D; Layer l = null;
        App.History.Struct(d, "새 레이어", () =>
        {
            var a = d.Active;
            l = new Layer(d, name ?? $"레이어 {d.Layers.Count + 1}", kind) { Parent = a?.IsFolder == true && a.Expanded ? a.Id : a?.Parent };
            int i = a == null ? d.Layers.Count : d.Layers.IndexOf(a) + 1;
            d.Layers.Insert(i, l); d.ActiveId = l.Id;
        });
        Done();
        return l;
    }

    public static void NewFolder() => NewLayer($"폴더 {D.Layers.Count(l => l.IsFolder) + 1}", "folder");

    public static void Duplicate()
    {
        var d = D; var a = d.Active; if (a == null) return;
        App.History.Struct(d, "레이어 복제", () =>
        {
            var c = a.Clone(d);
            d.Layers.Insert(d.Layers.IndexOf(a) + 1, c); d.ActiveId = c.Id;
            if (a.IsFolder)
            {
                // 폴더 안의 것도 함께 (새 폴더 안으로)
                var map = new Dictionary<string, string> { [a.Id] = c.Id };
                int at = d.Layers.IndexOf(a);
                foreach (var k in d.Layers.Where(x => d.IsInside(x, a)).ToList())
                {
                    var kc = k.Clone(d, k.Name); map[k.Id] = kc.Id; kc.Parent = map.GetValueOrDefault(k.Parent, k.Parent);
                    d.Layers.Insert(d.Layers.IndexOf(c), kc);
                }
            }
        });
        Done();
    }

    public static void Delete()
    {
        var d = D; var a = d.Active; if (a == null) return;
        App.History.Struct(d, "레이어 삭제", () =>
        {
            int i = d.Layers.IndexOf(a);
            var gone = new List<Layer> { a };
            if (a.IsFolder) gone.AddRange(d.Layers.Where(x => d.IsInside(x, a)));
            foreach (var g in gone) d.Layers.Remove(g);
            d.ActiveId = d.Layers.Count == 0 ? null : d.Layers[Math.Clamp(i - 1, 0, d.Layers.Count - 1)].Id;
        });
        Done();
    }

    public static void MergeDown()
    {
        var d = D; var a = d.Active; if (a == null || a.Px == null) return;
        int i = d.Layers.IndexOf(a);
        var below = d.Layers.Take(i).LastOrDefault(l => d.ParentOf(l) == d.ParentOf(a));
        if (below?.Px == null) { App.Toast("아래에 합칠 그림 레이어가 없습니다"); return; }
        var pe = App.History.BeginPixels(below, "아래로 병합");
        pe.Touch(a.Px.Bounds().Union(below.Px.Bounds()));
        // 두 레이어만 따로 합성 (클리핑·마스크·모드 반영)
        var only = new RenderOptions { Paper = false, Only = new HashSet<Layer> { below, a } };
        var b = a.Px.Bounds().Union(below.Px.Bounds());
        for (int y = b.Y; y < b.B; y += Surface.TS)
            for (int x = b.X; x < b.R; x += Surface.TS)
            {
                var part = Rect.FromLTRB(x, y, Math.Min(b.R, x + Surface.TS), Math.Min(b.B, y + Surface.TS));
                below.Px.Write(part, App.Comp.Render(d, part, only));
            }
        var px = pe.Finish("아래로 병합");
        var before = StructSnap.Take(d);
        below.Opacity = 1; below.Blend = "source-over";
        d.Layers.Remove(a); d.ActiveId = below.Id;
        App.History.PushGroup("아래로 병합", px, new StructEdit("아래로 병합", before, StructSnap.Take(d)));
        d.Modified = true;
        Done();
    }

    public static void AddMask()
    {
        var a = D.Active; if (a == null) return;
        if (a.Mask != null) { App.History.Struct(D, "마스크 끄기/켜기", () => a.MaskOn = !a.MaskOn); Done(); return; }
        App.History.Struct(D, "레이어 마스크", () =>
        {
            a.Mask = new Surface(D.W, D.H);
            // 선택 영역이 있으면 그 모양으로, 없으면 전부 보이게
            for (int ty = 0; ty < a.Mask.TY; ty++) for (int tx = 0; tx < a.Mask.TX; tx++)
            {
                var r = a.Mask.TileRect(ty * a.Mask.TX + tx);
                var buf = new uint[r.W * r.H];
                for (int y = 0; y < r.H; y++) for (int x = 0; x < r.W; x++) { byte s = D.Sel.At(r.X + x, r.Y + y); buf[y * r.W + x] = Px.Pack(s, s, s, s); }
                a.Mask.Write(r, buf);
            }
            a.MaskOn = true;
        });
        Done();
    }

    public static void ToggleProp(string label, Func<Layer, bool> get, Action<Layer, bool> set)
    {
        var a = D?.Active; if (a == null) return;
        App.History.Struct(D, label, () => set(a, !get(a)));
        Done();
    }

    public static void Move(int dir)
    {
        var d = D; var a = d.Active; if (a == null) return;
        var sib = d.Layers.Where(l => d.ParentOf(l) == d.ParentOf(a)).ToList();
        int si = sib.IndexOf(a), ti = si + dir;
        if (ti < 0 || ti >= sib.Count) return;
        App.History.Struct(d, "레이어 순서", () =>
        {
            var other = sib[ti];
            int ia = d.Layers.IndexOf(a), io = d.Layers.IndexOf(other);
            d.Layers[ia] = other; d.Layers[io] = a;
        });
        Done();
    }

    public static void ContextMenu(Control owner, Point at)
    {
        var m = new ContextMenuStrip();
        foreach (var item in Registry.Menus.Where(x => x.Path.StartsWith("레이어/")).OrderBy(x => x.Order))
        {
            string leaf = item.Path[(item.Path.LastIndexOf('/') + 1)..];
            if (leaf.StartsWith("-")) { m.Items.Add(new ToolStripSeparator()); continue; }
            var def = item;
            var mi = new ToolStripMenuItem(leaf) { Enabled = def.Enabled?.Invoke() ?? true, Checked = def.Checked?.Invoke() ?? false };
            mi.Click += (_, _) => MainForm.Run(def.Run, def.Path);
            m.Items.Add(mi);
        }
        m.Show(owner, at);
    }
}

public static class SelectCommands
{
    static Document D => App.Doc;
    public static void All() { App.History.SelectionChange(D, "전체 선택", () => D.Sel.All()); App.Invalidate(Rect.Empty); RedrawView(); }
    public static void None() { App.History.SelectionChange(D, "선택 해제", () => D.Sel.None()); RedrawView(); }
    public static void Invert() { App.History.SelectionChange(D, "선택 반전", () => D.Sel.Invert()); RedrawView(); }
    public static void RedrawView() => (App.Main as MainForm)?.Canvas.RedrawAll();

    /// <summary>선택 영역(없으면 전체)을 지우거나 색으로 채움</summary>
    public static void FillSelection(bool erase)
    {
        var d = D; var l = d.Active;
        if (!App.CheckEditable(l)) return;
        var r = d.Sel.Bounds();
        if (r.IsEmpty) return;
        var pe = App.History.BeginPixels(l, erase ? "지우기" : "채우기");
        pe.Touch(r);
        var t = l.Target;
        var buf = t.Read(r);
        uint col = l.EditMask ? 0xFFFFFFFF : App.Fg;
        for (int y = 0; y < r.H; y++)
            for (int x = 0; x < r.W; x++)
            {
                byte s = d.Sel.At(r.X + x, r.Y + y);
                if (s == 0) continue;
                int i = y * r.W + x;
                buf[i] = erase ? Px.Scale(buf[i], 255u - s) : l.AlphaLock ? Px.Composite(Px.Scale(col, s), buf[i], Blend.Atop) : Px.Over(Px.Scale(col, s), buf[i]);
            }
        t.Write(r, buf);
        pe.Commit();
        d.Modified = true;
        App.Invalidate(r); App.NotifyLayers();
    }
}

/// <summary>기본 메뉴 등록</summary>
public sealed class CoreModule : IModule
{
    public void Init()
    {
        CanvasView V() => (App.Main as MainForm)?.Canvas;
        bool HasDoc() => App.Doc != null;

        Registry.Menu("파일/새로 만들기…", FileCommands.New, Keys.Control | Keys.N, order: 10);
        Registry.Menu("파일/열기…", FileCommands.Open, Keys.Control | Keys.O, order: 20);
        Registry.Menu("파일/저장", () => FileCommands.Save(false), Keys.Control | Keys.S, HasDoc, order: 30);
        Registry.Menu("파일/다른 이름으로 저장…", () => FileCommands.Save(true), Keys.Control | Keys.Shift | Keys.S, HasDoc, order: 40);
        Registry.Menu("파일/-1", null, order: 45);
        Registry.Menu("파일/그림으로 내보내기 (PNG·JPG)…", FileCommands.ExportImage, Keys.Control | Keys.Shift | Keys.E, HasDoc, order: 50);
        Registry.Menu("파일/-2", null, order: 90);
        Registry.Menu("파일/끝내기", () => App.Main.Close(), Keys.Alt | Keys.F4, order: 99);

        Registry.Menu("편집/실행취소", () => { var r = App.History.Undo(App.Doc); App.Invalidate(r.IsEmpty ? Rect.Empty : r); App.NotifyLayers(); SelectCommands.RedrawView(); }, Keys.Control | Keys.Z, () => App.History.CanUndo, order: 10);
        Registry.Menu("편집/다시실행", () => { var r = App.History.Redo(App.Doc); App.Invalidate(r.IsEmpty ? Rect.Empty : r); App.NotifyLayers(); SelectCommands.RedrawView(); }, Keys.Control | Keys.Y, () => App.History.CanRedo, order: 20);
        Registry.Menu("편집/-1", null, order: 25);
        Registry.Menu("편집/지우기 (선택 영역)", () => SelectCommands.FillSelection(true), Keys.Delete, HasDoc, order: 30);
        Registry.Menu("편집/전경색으로 채우기", () => SelectCommands.FillSelection(false), Keys.Alt | Keys.Back, HasDoc, order: 40);

        Registry.Menu("레이어/새 레이어", () => LayerCommands.NewLayer(), Keys.Control | Keys.Shift | Keys.N, HasDoc, order: 10);
        Registry.Menu("레이어/새 폴더", LayerCommands.NewFolder, Keys.Control | Keys.G, HasDoc, order: 20);
        Registry.Menu("레이어/복제", LayerCommands.Duplicate, Keys.Control | Keys.J, HasDoc, order: 30);
        Registry.Menu("레이어/삭제", LayerCommands.Delete, Keys.None, HasDoc, order: 40);
        Registry.Menu("레이어/아래로 병합", LayerCommands.MergeDown, Keys.Control | Keys.E, HasDoc, order: 50);
        Registry.Menu("레이어/-1", null, order: 55);
        Registry.Menu("레이어/클리핑", () => LayerCommands.ToggleProp("클리핑", l => l.Clip, (l, v) => l.Clip = v), Keys.Control | Keys.Alt | Keys.G, HasDoc, () => App.Active?.Clip == true, order: 60);
        Registry.Menu("레이어/투명 픽셀 잠금", () => LayerCommands.ToggleProp("투명 픽셀 잠금", l => l.AlphaLock, (l, v) => l.AlphaLock = v), Keys.None, HasDoc, () => App.Active?.AlphaLock == true, order: 70);
        Registry.Menu("레이어/잠금", () => LayerCommands.ToggleProp("잠금", l => l.Locked, (l, v) => l.Locked = v), Keys.None, HasDoc, () => App.Active?.Locked == true, order: 80);
        Registry.Menu("레이어/밑그림 (내보내기·채우기에서 빼기)", () => LayerCommands.ToggleProp("밑그림", l => l.Draft, (l, v) => l.Draft = v), Keys.None, HasDoc, () => App.Active?.Draft == true, order: 85);
        Registry.Menu("레이어/참조 레이어 (채우기 기준)", () => LayerCommands.ToggleProp("참조 레이어", l => l.Ref, (l, v) => l.Ref = v), Keys.None, HasDoc, () => App.Active?.Ref == true, order: 86);
        Registry.Menu("레이어/레이어 마스크 추가·켜기/끄기", LayerCommands.AddMask, Keys.None, HasDoc, order: 90);
        Registry.Menu("레이어/-2", null, order: 95);
        Registry.Menu("레이어/위로", () => LayerCommands.Move(1), Keys.Control | Keys.OemCloseBrackets, HasDoc, order: 100);
        Registry.Menu("레이어/아래로", () => LayerCommands.Move(-1), Keys.Control | Keys.OemOpenBrackets, HasDoc, order: 110);

        Registry.Menu("선택/전체 선택", SelectCommands.All, Keys.Control | Keys.A, HasDoc, order: 10);
        Registry.Menu("선택/선택 해제", SelectCommands.None, Keys.Control | Keys.D, HasDoc, order: 20);
        Registry.Menu("선택/선택 반전", SelectCommands.Invert, Keys.Control | Keys.Shift | Keys.I, HasDoc, order: 30);

        Registry.Menu("보기/화면에 맞추기", () => V()?.Fit(), Keys.Control | Keys.D0, order: 10);
        Registry.Menu("보기/100%", () => V()?.SetZoom(1), Keys.Control | Keys.D1, order: 20);
        Registry.Menu("보기/확대", () => V()?.SetZoom(V().V.Zoom * 1.25), Keys.Control | Keys.Oemplus, order: 30);
        Registry.Menu("보기/축소", () => V()?.SetZoom(V().V.Zoom / 1.25), Keys.Control | Keys.OemMinus, order: 40);
        Registry.Menu("보기/왼쪽으로 돌리기", () => { var v = V(); v?.RotateBy(-15, v.Width / 2.0, v.Height / 2.0); }, Keys.None, order: 50);
        Registry.Menu("보기/오른쪽으로 돌리기", () => { var v = V(); v?.RotateBy(15, v.Width / 2.0, v.Height / 2.0); }, Keys.None, order: 60);
        Registry.Menu("보기/회전 초기화", () => { var v = V(); if (v != null) v.RotateBy(-v.V.Angle, v.Width / 2.0, v.Height / 2.0); }, Keys.None, order: 70);
        Registry.Menu("보기/좌우 반전", () => V()?.FlipView(), Keys.None, order: 80);

        Registry.Menu("도움말/ComicPad 정보", () => MessageBox.Show(App.Main, "ComicPad 네이티브 판 (개발 중)\n웹 판과 같은 .cpad 파일을 씁니다.\nApache License 2.0", "ComicPad"), order: 99);
    }
}
