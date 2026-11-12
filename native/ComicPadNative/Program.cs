// ComicPad 네이티브 판: 웹 화면 없이 .NET 창 + 직접 짠 그리기 엔진. 웹 판과 같은 .cpad 파일을 쓴다.
// 시작: 설정 읽기 → 모든 IModule 자동 Init (도구·메뉴·패널 등록) → 메인 창 → 마지막 작품 또는 새 작품.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.UI;

namespace ComicPadNative;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 시험용 (창 없이): --roundtrip 입력.cpad 출력.cpad / --render 입력.cpad 출력.png
        if (args.Length == 3 && args[0] == "--roundtrip") { var d = CpadIO.Load(args[1]); CpadIO.Save(d, args[2]); return; }
        if (args.Length == 3 && args[0] == "--render")
        {
            InitModules();
            var d = CpadIO.Load(args[1]);
            var flat = App.Comp.Flatten(d);
            using var bmp = CpadIO.ToBitmap(flat, flat.Full);
            bmp.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        // 모듈 자체 시험 (창 없이): --selftest brushes|3d|script 결과폴더  → 끝 코드 = 실패 수
        if (args.Length == 3 && args[0] == "--selftest")
        {
            InitModules();
            Directory.CreateDirectory(args[2]);
            if (args[1] == "script") { Environment.ExitCode = Modules.Comic.ScriptTest.Run(args[2]); return; }
            if (args[1] == "3d") { Environment.ExitCode = Modules.Scene3D.SelfTest3D.Run(args[2]); return; }
            if (args[1] == "brushes")
            {
                var res = Modules.BrushEngine.SelfTest.Run(args[2]);
                File.WriteAllLines(Path.Combine(args[2], "report.txt"), res.Select(r => (r.Ok ? "PASS " : "FAIL ") + r.Name + " " + r.Detail));
                Environment.ExitCode = res.Count(r => !r.Ok); return;
            }
            Environment.ExitCode = 99; return;
        }
        if (args.Length == 5 && args[0] == "--ai-test") { Environment.ExitCode = Modules.AI.AiTest.Run(args[1], args[2], double.Parse(args[3]), double.Parse(args[4])); return; }
        EnableMouseInPointer(true);            // 마우스도 펜·터치와 같은 포인터 메시지로
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);

        App.LoadSettings();
        InitModules();

        var form = new MainForm();
        form.Shown += (_, _) =>
        {
            App.SetTool("brush");
            string open = args.FirstOrDefault(File.Exists);
            if (open != null) FileCommands.OpenPath(open);
            else App.SetDoc(Document.New(App.Get("newW", 2480), App.Get("newH", 3508)));
        };
        var auto = new System.Windows.Forms.Timer { Interval = Math.Max(1, App.Get("autosaveMin", 3)) * 60_000 };
        auto.Tick += (_, _) => FileCommands.Autosave();
        auto.Start();
        Application.Run(form);
    }

    /// <summary>모듈 자동 등록 (Core 먼저, 기본 도구, 나머지는 이름 순 — 창·시험 모드 모두 같은 순서)</summary>
    static void InitModules()
    {
        var mods = typeof(Program).Assembly.GetTypes().Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .OrderBy(t => t == typeof(CoreModule) ? 0 : t.Name == "BasicToolsModule" ? 1 : 2).ThenBy(t => t.FullName);
        foreach (var t in mods)
        {
            try { ((IModule)Activator.CreateInstance(t)).Init(); }
            catch (Exception ex) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "comicpad-native-error.log"), $"{DateTime.Now} 모듈 {t.Name}: {ex}\r\n"); } catch { } }
        }
    }

    static void Report(Exception e)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "comicpad-native-error.log"), DateTime.Now + " " + e + "\r\n\r\n"); } catch { }
        App.Toast("오류: " + e?.Message);
    }

    [DllImport("user32.dll")] static extern bool EnableMouseInPointer(bool enable);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
}
