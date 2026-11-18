// 판 고르기: 웹 판(이 창 + server.py) 또는 네이티브 판 베타(native\ComicPadNative\ComicPadNative.exe).
//  - 스위치: data\config\app.json 의 {"app":"native"} (웹 판 도움말 > 새 네이티브 판 써 보기 / 네이티브 판 도움말 > 웹 판으로 돌아가기 가 씀)
//  - 비상구: 켤 때 Shift 를 누르고 있거나 --web 을 붙이면 이번만 웹 판. --native 를 붙이면 이번만 네이티브 판 (파일이 있을 때)
//  - 네이티브 판 파일이 없거나 켜자마자 오류로 꺼지면 웹 판으로 (작업물은 같은 data 폴더)
// C# 5 (.NET Framework csc) 로 빌드되므로 $"" · ?. 같은 새 문법은 쓰지 않음
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static class AppSwitch
{
    public static string NativeExe { get { return Path.Combine(Program.Root, "native", "ComicPadNative", "ComicPadNative.exe"); } }
    public static string SwitchFile { get { return Path.Combine(Program.Root, "data", "config", "app.json"); } }

    /// <summary>app.json 의 "app" 값 (없거나 읽지 못하면 "web")</summary>
    public static string ReadSwitch()
    {
        try
        {
            if (!File.Exists(SwitchFile)) return "web";
            var m = Regex.Match(File.ReadAllText(SwitchFile, Encoding.UTF8), "\"app\"\\s*:\\s*\"(\\w+)\"");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "web";
        }
        catch { return "web"; }
    }

    static bool Has(string[] args, string flag) { return Array.IndexOf(args, flag) >= 0; }

    /// <summary>"native" 또는 "web". why = 고른 까닭 (flag·shift·switch·missing·default)</summary>
    public static string Choose(string[] args, out string why)
    {
        if (Has(args, "--web")) { why = "flag"; return "web"; }
        if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift) { why = "shift"; return "web"; }
        bool force = Has(args, "--native");
        string sw = ReadSwitch();
        if (!force && sw != "native") { why = sw == "web" && File.Exists(SwitchFile) ? "switch" : "default"; return "web"; }
        if (!File.Exists(NativeExe)) { why = "missing"; return "web"; }
        why = force ? "flag" : "switch";
        return "native";
    }

    public static void WriteWhich(string file, string choice, string why)
    {
        try { File.WriteAllText(file, choice + "\n" + why + "\n" + (choice == "native" ? NativeExe : "") + "\n", new UTF8Encoding(false)); } catch { }
    }

    /// <summary>네이티브 판 켜기 (열 파일만 넘김). 켜자마자 오류 코드로 꺼지면 알리고 false → 웹 판으로</summary>
    public static bool StartNative(string[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--port" || a == "--which") { i++; continue; }
            if (a.StartsWith("--")) continue;
            try { if (File.Exists(a)) sb.Append(sb.Length > 0 ? " " : "").Append('"').Append(Path.GetFullPath(a)).Append('"'); } catch { }
        }
        Process p;
        try
        {
            var psi = new ProcessStartInfo(NativeExe, sb.ToString());
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetDirectoryName(NativeExe);
            p = Process.Start(psi);
        }
        catch (Exception e) { Fail("네이티브 판을 켜지 못했습니다: " + e.Message); return false; }
        if (p == null) return true;
        try
        {
            // 런타임 파일이 빠졌거나 하는 문제로 바로 꺼지는지 잠깐 지켜봄 (창은 그 사이 이미 떠 있음)
            if (p.WaitForExit(4000) && p.ExitCode != 0)
            {
                Fail("네이티브 판이 켜지자마자 꺼졌습니다 (코드 " + p.ExitCode + ").");
                return false;
            }
        }
        catch { }
        return true;
    }

    static void Fail(string msg)
    {
        try { File.AppendAllText(Path.Combine(Program.DataDir, "error.log"), DateTime.Now + " " + msg + "\r\n"); } catch { }
        MessageBox.Show(msg + "\n\n이번에는 웹 판으로 엽니다. 작업물은 그대로입니다.\n(웹 판 도움말 > 새 네이티브 판 써 보기 에서 다시 바꾸거나, 그대로 웹 판을 쓰면 됩니다)", "ComicPad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
