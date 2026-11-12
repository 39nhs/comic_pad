// ComicPad 전용 창: 이 PC 의 ComicPad 서버(server.py)를 켜고, Windows 에 들어 있는 WebView2 로 앱 화면을 띄운다.
// Edge 창이 아니라 ComicPad 만의 창 (자기 아이콘·작업 표시줄, 주소창·브라우저 알림 없음).
// 빌드: launcher\build.ps1 (C# 5 / .NET Framework 4.x 의 csc.exe 로)
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    public static string Root;
    public static int Port = 8787;
    public static string Url;
    public static CoreWebView2Environment Env;
    public static string DataDir;

    [STAThread]
    static void Main(string[] args)
    {
        Root = AppDomain.CurrentDomain.BaseDirectory;
        string envPort = Environment.GetEnvironmentVariable("COMICPAD_PORT");
        int p;
        if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out p)) Port = p;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--port" && int.TryParse(args[i + 1], out p)) Port = p;
        Url = "http://127.0.0.1:" + Port + "/";
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicPad");
        Directory.CreateDirectory(DataDir);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // 예상 못 한 오류도 .NET 기본 오류 창 대신 알기 쉬운 안내로
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);

        if (!Ping())
        {
            string err = StartServer();
            if (err != null) { MessageBox.Show(err, "ComicPad", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            var sw = Stopwatch.StartNew();
            while (!Ping() && sw.ElapsedMilliseconds < 25000) Thread.Sleep(200);
            if (!Ping()) { MessageBox.Show("ComicPad 서버가 켜지지 않았습니다.\n설치 폴더의 \"ComicPad 실행.bat\" 로 켜 보면 오류 내용을 볼 수 있습니다.", "ComicPad", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        }
        Application.Run(new AppWindow(Url, null, true));
        StopServerIfLast();
    }

    // 창을 닫으면 서버(python)도 끈다: 같은 ComicPad.exe 창이 더 없을 때만 (서버가 휴대폰 연결 중이면 서버가 알아서 남음)
    static void StopServerIfLast()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            string me = self.MainModule.FileName;
            foreach (var pr in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(me)))
            {
                try { if (pr.Id != self.Id && string.Equals(pr.MainModule.FileName, me, StringComparison.OrdinalIgnoreCase)) return; }
                catch { }
            }
            var req = (HttpWebRequest)WebRequest.Create(Url + "api/shutdown");
            req.Method = "POST"; req.Timeout = 2000; req.Proxy = null; req.ContentLength = 0;
            using (req.GetResponse()) { }
        }
        catch { }
    }

    public static void ShowError(Exception e)
    {
        try { File.AppendAllText(Path.Combine(DataDir, "error.log"), DateTime.Now + " " + e + "\r\n\r\n"); } catch { }
        MessageBox.Show("ComicPad 창에서 문제가 생겼습니다.\n" + (e != null ? e.Message : "") + "\n\n작업물은 data 폴더에 그대로 있습니다. 창을 다시 열어 주세요.\n(자세한 내용: " + Path.Combine(DataDir, "error.log") + ")", "ComicPad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /** WebView2 저장 폴더 (시험용으로 COMICPAD_WEBVIEW_DATA 로 바꿀 수 있음) */
    public static string WebDataDir()
    {
        string o = Environment.GetEnvironmentVariable("COMICPAD_WEBVIEW_DATA");
        return string.IsNullOrEmpty(o) ? Path.Combine(DataDir, "WebView2") : o;
    }

    public static bool Ping()
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(Url + "api/ping");
            req.Timeout = 800; req.Proxy = null;
            using (var r = (HttpWebResponse)req.GetResponse()) return r.StatusCode == HttpStatusCode.OK;
        }
        catch { return false; }
    }

    static string FindPython()
    {
        // 1) 설치할 때 적어 둔 Python  2) PATH 의 pythonw / python  3) py 런처
        string saved = Path.Combine(Root, "python_path.txt");
        if (File.Exists(saved))
        {
            string s = File.ReadAllText(saved).Trim().Trim('"');
            if (File.Exists(s)) return s;
        }
        string own = Path.Combine(Root, "python", "pythonw.exe");      // 설치 파일에 들어 있는 ComicPad 전용 Python
        if (File.Exists(own)) return own;
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string name in new[] { "pythonw.exe", "python.exe" })
            foreach (string dir in path.Split(';'))
            {
                try { string f = Path.Combine(dir.Trim().Trim('"'), name); if (File.Exists(f) && !f.Contains("WindowsApps")) return f; } catch { }
            }
        string py = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "pyw.exe");
        if (File.Exists(py)) return py;
        return null;
    }

    static string StartServer()
    {
        string py = FindPython();
        if (py == null) return "Python 을 찾지 못했습니다.\nhttps://www.python.org 에서 Python 을 설치한 뒤 다시 실행하세요.";
        try
        {
            var psi = new ProcessStartInfo(py, "\"" + Path.Combine(Root, "server.py") + "\" --no-browser --port " + Port);
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WorkingDirectory = Root;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
            return null;
        }
        catch (Exception e) { return "ComicPad 서버를 켜지 못했습니다: " + e.Message; }
    }

    public static bool IsLocal(string uri)
    {
        Uri u;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out u)) return false;
        if (u.Scheme == "about" || u.Scheme == "data") return true;          // 빈 창, 내보내기 파일
        if (u.Scheme == "blob") return IsApp(uri.Substring(5));            // blob:http://127.0.0.1:8787/... (이 앱이 만든 것만)
        return IsApp(uri);
    }

    // 이 PC 의 ComicPad 화면 (http://127.0.0.1:포트 또는 localhost:포트) 인가
    public static bool IsApp(string uri)
    {
        Uri u;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out u)) return false;
        return u.Scheme == "http" && (u.Host == "127.0.0.1" || u.Host == "localhost") && u.Port == Port;
    }

    public static void OpenOutside(string uri)
    {
        // 웹 주소(https/http)와 메일 주소만 기본 브라우저로. file:, 다른 프로그램을 여는 주소 등은 열지 않음 (프로그램 실행 방지)
        Uri u;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out u)) return;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeMailto) return;
        if (u.IsUnc || u.IsFile) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    /** WebView2 가 없을 때 (예전 Windows 10): Edge 앱 창으로 대신 */
    public static void FallbackEdge()
    {
        string[] edges = {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
        };
        foreach (string e in edges) if (File.Exists(e)) { Process.Start(e, "--app=" + Url + " --start-maximized"); return; }
        OpenOutside(Url);
    }
}

class AppWindow : Form
{
    WebView2 web = new WebView2();
    PenOverlay pen;                       // 네이티브 펜 입력 (NativePen.cs)
    readonly bool main;
    bool closeOk;
    CoreWebView2NewWindowRequestedEventArgs pending;
    CoreWebView2Deferral pendingDeferral;
    FormWindowState beforeFull = FormWindowState.Maximized;

    public AppWindow(string url, CoreWebView2NewWindowRequestedEventArgs fromPopup, bool isMain)
    {
        main = isMain;
        Text = "ComicPad";
        try { Icon = new Icon(Path.Combine(Program.Root, "ComicPad.ico")); } catch { }
        BackColor = Color.FromArgb(30, 30, 34);
        StartPosition = FormStartPosition.CenterScreen;
        if (main) { Size = new Size(1400, 900); RestoreBounds_(); }
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = Color.FromArgb(30, 30, 34);
        Controls.Add(web);
        pending = fromPopup;
        if (pending != null)
        {
            pendingDeferral = pending.GetDeferral();
            var f = pending.WindowFeatures;
            if (f.HasSize) ClientSize = new Size((int)Math.Max(240, f.Width), (int)Math.Max(160, f.Height));
            if (f.HasPosition) { StartPosition = FormStartPosition.Manual; Location = new Point((int)f.Left, (int)f.Top); }
        }
        Load += async (s, e) => await Init(url);
        FormClosing += OnClosing;
    }

    static Task<CoreWebView2Environment> MakeEnv(string dir)
    {
        var opt = new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection --autoplay-policy=no-user-gesture-required");
        return CoreWebView2Environment.CreateAsync(null, dir, opt);
    }

    async Task Init(string url)
    {
        Exception first = null;
        try
        {
            if (Program.Env == null) Program.Env = await MakeEnv(Program.WebDataDir());
            await web.EnsureCoreWebView2Async(Program.Env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show("이 PC 에 WebView2 런타임이 없어 Edge 창으로 엽니다.\n(Windows 11 에는 기본으로 들어 있습니다. Windows 10 은 마이크로소프트 \"WebView2 Runtime\" 을 설치하면 전용 창으로 열립니다.)", "ComicPad");
            Program.FallbackEdge();
            closeOk = true; Close();
            return;
        }
        catch (Exception ex) { first = ex; }
        if (first != null)
        {
            // 다른 ComicPad 창이 다른 설정으로 이미 떠 있거나 저장 폴더가 잠겨 있을 때: 이 창만 따로 폴더를 써서 다시 시도
            try
            {
                Controls.Remove(web);
                web.Dispose();
                web = new WebView2();
                web.Dock = DockStyle.Fill;
                web.DefaultBackgroundColor = Color.FromArgb(30, 30, 34);
                Controls.Add(web);
                Program.Env = await MakeEnv(Path.Combine(Program.DataDir, "WebView2-" + Process.GetCurrentProcess().Id));
                await web.EnsureCoreWebView2Async(Program.Env);
            }
            catch (Exception ex2)
            {
                Program.ShowError(new Exception(first.Message + " / " + ex2.Message, ex2));
                Program.FallbackEdge();
                closeOk = true; Close();
                return;
            }
        }
        var c = web.CoreWebView2;
        var st = c.Settings;
        st.AreBrowserAcceleratorKeysEnabled = false;   // Ctrl+R 새로고침·Ctrl+P 인쇄 같은 브라우저 단축키 끄기 (작업 날림 방지)
        st.IsZoomControlEnabled = false;               // Ctrl+휠은 앱의 화면 확대로
        st.IsPinchZoomEnabled = false;                 // 두 손가락은 앱의 확대·회전으로
        st.IsSwipeNavigationEnabled = false;           // 옆으로 밀어 뒤로 가기 끄기 (그리다가 페이지가 넘어가지 않게)
        st.IsStatusBarEnabled = false;
        st.IsGeneralAutofillEnabled = false;
        st.IsPasswordAutosaveEnabled = false;
        st.AreDevToolsEnabled = true;
        c.DocumentTitleChanged += (s, e) => { string t = c.DocumentTitle; Text = string.IsNullOrEmpty(t) || t.StartsWith("http") ? "ComicPad" : t; };
        // 브라우저 기본 오른쪽 클릭 메뉴는 끄기 (앱의 원형 메뉴와 겹쳐 뜨지 않게). 글자 입력칸에서만 복사·붙여넣기 메뉴
        c.ContextMenuRequested += (s2, e2) => { if (!e2.ContextMenuTarget.IsEditable) e2.Handled = true; };
        c.NewWindowRequested += OnNewWindow;
        c.WindowCloseRequested += (s, e) => { closeOk = true; Close(); };
        c.ContainsFullScreenElementChanged += (s, e) => SetFull(c.ContainsFullScreenElement);
        c.NavigationStarting += (s, e) =>
        {
            // 앱 밖 주소(출처 링크 등)는 기본 브라우저로
            if (!Program.IsLocal(e.Uri)) { e.Cancel = true; Program.OpenOutside(e.Uri); }
        };
        c.PermissionRequested += (s, e) =>
        {
            // 이 PC 의 ComicPad 화면이 묻는 권한(클립보드·소리 등)은 허용
            // (data:/blob: 처럼 출처가 다른 화면에는 주지 않음)
            if ((Program.IsApp(e.Uri) || e.Uri == "about:blank") && e.PermissionKind != CoreWebView2PermissionKind.Geolocation) e.State = CoreWebView2PermissionState.Allow;
        };
        // 네이티브 펜 입력: 페이지가 "그림판 위에 펜" 을 알려 주면 그 영역만 덮어서 Windows Ink 펜 입력을 직접 받음
        if (main && !PenOverlay.Disabled)
        {
            try
            {
                pen = new PenOverlay(this, web);
                await c.AddScriptToExecuteOnDocumentCreatedAsync("window.comicpadNative={pen:1,version:1,wintab:" + (Wintab.Available ? "1" : "0") + "};");
                c.WebMessageReceived += (s3, e3) =>
                {
                    string msg = null;
                    try { msg = e3.TryGetWebMessageAsString(); } catch { }
                    if (pen != null) pen.OnMessage(msg);
                };
            }
            catch (Exception ex) { pen = null; try { File.AppendAllText(Path.Combine(Program.DataDir, "error.log"), DateTime.Now + " 네이티브 펜 입력 꺼짐: " + ex.Message + "\r\n"); } catch { } }
        }
        if (pending != null)
        {
            pending.NewWindow = c;
            pending.Handled = true;
            pendingDeferral.Complete();
            pending = null;
        }
        else web.Source = new Uri(url);
    }

    void OnNewWindow(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!Program.IsLocal(e.Uri)) { e.Handled = true; Program.OpenOutside(e.Uri); return; }
        // 앱 밖 창으로 꺼낸 패널·설명서: ComicPad 창을 하나 더 (원래 창과 이어진 채로)
        var w = new AppWindow(null, e, false);
        w.Show();
    }

    void SetFull(bool full)
    {
        if (full)
        {
            beforeFull = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = beforeFull;
        }
    }

    async void OnClosing(object sender, FormClosingEventArgs e)
    {
      try
      {
        if (!main || closeOk || web.CoreWebView2 == null) { SaveBounds(); return; }
        e.Cancel = true;
        string r = "false";
        try { r = await web.CoreWebView2.ExecuteScriptAsync("(function(){try{return !!(window.comicpadUnsaved&&window.comicpadUnsaved())}catch(e){return false}})()"); } catch { }
        if (r == "true")
        {
            var ans = MessageBox.Show(this, "저장하지 않은 변경이 있습니다.\n\n저장하고 닫을까요?\n(예 = 저장 후 닫기, 아니요 = 저장하지 않고 닫기)", "ComicPad", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (ans == DialogResult.Cancel) return;
            if (ans == DialogResult.Yes)
            {
                try
                {
                    await web.CoreWebView2.ExecuteScriptAsync("window.comicpadSave&&window.comicpadSave()");
                    // 저장이 끝날 때까지 (최대 20초)
                    for (int i = 0; i < 100; i++)
                    {
                        await Task.Delay(200);
                        string still = await web.CoreWebView2.ExecuteScriptAsync("(function(){try{return !!(window.comicpadUnsaved&&window.comicpadUnsaved())}catch(e){return false}})()");
                        if (still != "true") break;
                    }
                }
                catch { }
            }
        }
        closeOk = true;
        SaveBounds();
        Close();
      }
      catch (Exception ex) { Program.ShowError(ex); closeOk = true; Close(); }
    }

    // 창 크기·위치 기억
    string BoundsFile { get { return Path.Combine(Program.DataDir, "window.txt"); } }
    void RestoreBounds_()
    {
        WindowState = FormWindowState.Maximized;
        try
        {
            string[] v = File.ReadAllText(BoundsFile).Split(',');
            if (v.Length == 5)
            {
                var r = new Rectangle(int.Parse(v[0]), int.Parse(v[1]), int.Parse(v[2]), int.Parse(v[3]));
                if (Screen.FromRectangle(r).WorkingArea.IntersectsWith(r) && r.Width > 300 && r.Height > 200)
                {
                    StartPosition = FormStartPosition.Manual; Bounds = r;
                    WindowState = v[4] == "max" ? FormWindowState.Maximized : FormWindowState.Normal;
                }
            }
        }
        catch { }
    }
    void SaveBounds()
    {
        if (!main) return;
        try
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            File.WriteAllText(BoundsFile, r.X + "," + r.Y + "," + r.Width + "," + r.Height + "," + (WindowState == FormWindowState.Maximized ? "max" : "normal"));
        }
        catch { }
    }
}
