// 앱 전체 상태와 확장 지점.
//  - 모듈: IModule 을 구현한 클래스는 시작할 때 자동으로 Init() — 도구·메뉴·패널을 Registry 에 등록 (다른 파일을 고칠 필요 없음)
//  - 도구: ITool (펜·마우스·터치를 PointerArgs 로 받음)
//  - 메뉴: Registry.Menu("편집/실행취소", 동작, 단축키)
//  - 그림이 바뀌면 App.Invalidate(문서 영역) → 화면이 그 부분만 다시 합성
using System.Text.Json;
using System.Text.Json.Nodes;
using ComicPadNative.Core;

namespace ComicPadNative;

public interface IModule { void Init(); }

public sealed class PointerArgs
{
    public double X, Y;            // 문서 좌표
    public double SX, SY;          // 화면(그림판) 좌표
    public double Pressure = 1;    // 0~1
    public double TiltX, TiltY, Twist;
    public double Time;            // ms
    public string Type = "mouse";  // pen | mouse | touch
    public int Buttons;            // 1 왼쪽/펜 끝, 2 오른쪽/측면, 4 가운데, 32 지우개 끝
    public bool Shift, Ctrl, Alt;
    public bool EraserEnd => (Buttons & 32) != 0;
}

public abstract class ToolBase
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string Icon => "●";          // 도구 막대 글자 아이콘 (그림 아이콘은 나중에)
    public virtual Keys Key => Keys.None;
    public virtual Cursor Cursor => Cursors.Cross;
    public virtual bool DrawsSizeCircle => false;   // 브러시류: 크기 원 표시
    public virtual double CircleSize => 0;          // 문서 px 지름
    public virtual void Activate() { }
    public virtual void Deactivate() { }
    public virtual void Down(PointerArgs e) { }
    public virtual void Move(PointerArgs e) { }
    public virtual void Up(PointerArgs e) { }
    public virtual void Hover(PointerArgs e) { }
    public virtual void Cancel() { }
    public virtual bool Busy => false;
    /// <summary>그림판 위에 그릴 것 (화면 좌표, GDI+). 그 범위는 OverlayBounds</summary>
    public virtual void DrawOverlay(Graphics g, ViewXf v) { }
    public virtual Rectangle OverlayBounds(ViewXf v) => Rectangle.Empty;
    /// <summary>도구 옵션 패널</summary>
    public virtual Control BuildOptions() => null;
    public virtual bool HandleKey(Keys k) => false;
}

public sealed record MenuItemDef(string Path, Action Run, Keys Shortcut = Keys.None, Func<bool> Enabled = null, Func<bool> Checked = null, int Order = 500);
public sealed record PanelDef(string Title, Func<Control> Create, string Dock = "right", int Order = 500);

public static class Registry
{
    public static readonly List<ToolBase> Tools = new();
    public static readonly List<MenuItemDef> Menus = new();
    public static readonly List<PanelDef> Panels = new();
    public static readonly Dictionary<string, Action> Commands = new();   // 기능 검색·단축키용 (이름 → 동작)

    public static void Tool(ToolBase t) { Tools.RemoveAll(x => x.Id == t.Id); Tools.Add(t); }
    public static void Menu(string path, Action run, Keys key = Keys.None, Func<bool> enabled = null, Func<bool> check = null, int order = 500)
    {
        Menus.RemoveAll(m => m.Path == path);
        Menus.Add(new MenuItemDef(path, run, key, enabled, check, order));
        Commands[path] = run;
    }
    public static void Panel(string title, Func<Control> create, string dock = "right", int order = 500) { Panels.RemoveAll(p => p.Title == title); Panels.Add(new PanelDef(title, create, dock, order)); }

    /// <summary>도구와 상관없이 늘 그리는 화면 겹침 그림 (자·원고 가이드 등, 화면 좌표). 모양이 바뀌면 그림판 RedrawAll</summary>
    public static readonly List<Action<Graphics, ViewXf>> Overlays = new();
    public static void Overlay(Action<Graphics, ViewXf> draw) => Overlays.Add(draw);
}

/// <summary>화면 ↔ 문서 변환 (확대·이동·회전·좌우 반전)</summary>
public sealed class ViewXf
{
    public double Zoom = 1, PanX, PanY, Angle;   // 도(degree)
    public bool Flip;
    public (double x, double y) ToScreen(double dx, double dy)
    {
        double x = dx, y = dy;
        if (Flip) x = -x;
        if (Angle != 0) { double a = Angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); (x, y) = (x * c - y * s, x * s + y * c); }
        return (x * Zoom + PanX, y * Zoom + PanY);
    }
    public (double x, double y) ToDoc(double sx, double sy)
    {
        double x = (sx - PanX) / Zoom, y = (sy - PanY) / Zoom;
        if (Angle != 0) { double a = -Angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a); (x, y) = (x * c - y * s, x * s + y * c); }
        if (Flip) x = -x;
        return (x, y);
    }
    public bool Simple => Angle == 0 && !Flip;
}

public static class App
{
    public static Document Doc;
    public static readonly History History = new();
    public static readonly Compositor Comp = new();
    public static ToolBase Tool;
    public static ToolBase PrevTool;
    public static uint Fg = 0xFF000000, Bg = 0xFFFFFFFF;
    public static JsonObject Settings = new();
    public static Form Main;

    public static event Action DocChanged;        // 새 문서·열기
    public static event Action LayersChanged;     // 레이어 목록·속성
    public static event Action ToolChanged;
    public static event Action ColorChanged;
    public static event Action<Rect> Invalidated; // 문서 영역 다시 합성 필요 (Empty = 화면만)
    public static event Action SettingsChanged;

    public static Layer Active => Doc?.Active;

    public static void SetDoc(Document d)
    {
        Doc = d; History.Clear();
        DocChanged?.Invoke(); LayersChanged?.Invoke();
        Invalidate(new Rect(0, 0, d.W, d.H));
    }
    public static void Invalidate(Rect docRect) => Invalidated?.Invoke(docRect);
    public static void InvalidateAll() { if (Doc != null) Invalidate(new Rect(0, 0, Doc.W, Doc.H)); }
    public static void NotifyLayers() => LayersChanged?.Invoke();
    public static void NotifySettings() { SettingsChanged?.Invoke(); SaveSettingsSoon(); }

    public static void SetTool(string id)
    {
        var t = Registry.Tools.Find(x => x.Id == id);
        if (t == null || t == Tool) return;
        Tool?.Deactivate();
        PrevTool = Tool; Tool = t; t.Activate();
        ToolChanged?.Invoke();
    }
    public static void SetColor(uint c, bool bg = false) { if (bg) Bg = c; else Fg = c; ColorChanged?.Invoke(); }

    public static void Toast(string msg) => Toasted?.Invoke(msg);
    public static event Action<string> Toasted;

    /// <summary>편집 가능한 레이어인지 (잠금·폴더·조정 레이어 확인). 아니면 알림</summary>
    public static bool CheckEditable(Layer l)
    {
        if (l == null) { Toast("레이어가 없습니다"); return false; }
        if (l.Locked) { Toast("잠긴 레이어입니다"); return false; }
        if (l.IsFolder || l.IsAdjust) { Toast("이 레이어에는 그릴 수 없습니다 (폴더·조정 레이어)"); return false; }
        if (!l.Visible) { Toast("숨긴 레이어입니다"); return false; }
        return l.Target != null;
    }

    // ---- 설정 (%APPDATA%\ComicPad\native-settings.json) ---------------------------------
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ComicPad", "native-settings.json");
    public static void LoadSettings()
    {
        try { if (File.Exists(SettingsPath)) Settings = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new(); } catch { Settings = new(); }
    }
    static System.Windows.Forms.Timer saveTimer;
    public static void SaveSettingsSoon()
    {
        saveTimer ??= new System.Windows.Forms.Timer { Interval = 800 };
        saveTimer.Tick -= SaveTick; saveTimer.Tick += SaveTick;
        saveTimer.Stop(); saveTimer.Start();
    }
    static void SaveTick(object s, EventArgs e) { saveTimer.Stop(); SaveSettings(); }
    public static void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)); File.WriteAllText(SettingsPath, Settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
    public static T Get<T>(string key, T fallback) { try { var n = Settings[key]; return n == null ? fallback : n.GetValue<T>(); } catch { return fallback; } }
    public static void Set(string key, JsonNode v) { Settings[key] = v; SaveSettingsSoon(); }

    /// <summary>작업물 폴더: 설치 폴더의 data (웹 판과 같은 곳), 없으면 문서\ComicPad</summary>
    public static string DataDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "server.py")) && Directory.Exists(Path.Combine(d.FullName, "data"))) return Path.Combine(d.FullName, "data");
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ComicPad");
            Directory.CreateDirectory(p);
            return p;
        }
    }
}
