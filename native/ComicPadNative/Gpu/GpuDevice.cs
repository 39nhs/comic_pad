// 그래픽 가속(GPU): Direct3D 11 장치 하나를 앱 전체가 함께 씀 (그림판 화면·합성·시험).
//  - 설정 gpu = auto | on | off (환경 변수 COMICPAD_GPU 가 있으면 그것이 먼저, warp = 소프트웨어 D3D 로 시험)
//    auto: 기능 수준 11_0 이상 그래픽 카드가 있으면 GPU, 없으면 CPU / on: 카드가 없으면 WARP(소프트웨어 D3D)
//  - 셰이더는 빌드할 때 fxc 로 컴파일해 넣은 것(.cso)을 쓰고, 없으면 HLSL 원본을 윈도우의 d3dcompiler_47 로 컴파일
//  - 장치가 빠지면(드라이버 재시작 등) Lost → 그림판이 다시 만들거나 CPU 로 돌아감
using System.Reflection;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ComicPadNative.Gpu;

public sealed class GpuDevice : IDisposable
{
    public ID3D11Device Dev;
    public ID3D11DeviceContext Ctx;
    public IDXGIFactory2 Factory;
    public string AdapterName = "?";
    public bool Warp;
    public FeatureLevel Level;
    public long DedicatedMB, SharedMB;
    public ID3D11ComputeShader CsComposite;
    public ID3D11VertexShader VsView;
    public ID3D11PixelShader PsCanvas;
    public string ShaderSource = "";            // "미리 컴파일" | "실행 중 컴파일"

    /// <summary>설정에서 고른 방식: auto | on | off | warp</summary>
    public static string Mode
    {
        get
        {
            string env = Environment.GetEnvironmentVariable("COMICPAD_GPU");
            if (!string.IsNullOrEmpty(env)) return env.Trim().ToLowerInvariant();
            // 안전 모드(시작 중 꺼짐이 되풀이됨)·COMICPAD_NO_GPU 면 GPU 를 쓰지 않음
            if (Environment.GetEnvironmentVariable("COMICPAD_NO_GPU") == "1" || Environment.GetEnvironmentVariable("COMICPAD_SAFE_MODE") == "1") return "off";
            // 설정 값: "auto"|"on"|"off"|"warp", 예전·다른 모듈이 쓴 true/false 도 받음
            var n = App.Settings["gpu"];
            if (n is System.Text.Json.Nodes.JsonValue v)
            {
                if (v.TryGetValue(out bool b)) return b ? "on" : "off";
                if (v.TryGetValue(out string str) && !string.IsNullOrWhiteSpace(str)) return str.Trim().ToLowerInvariant();
            }
            return "auto";
        }
    }
    public static bool Wanted => Mode != "off";

    /// <summary>마지막으로 만든 장치 상태 (설정 화면에 보임)</summary>
    public static string Status = "GPU 아직 안 씀";

    static GpuDevice shared;
    static bool sharedFailed;
    static string sharedMode;
    /// <summary>앱이 함께 쓰는 장치 (없거나 끔이면 null)</summary>
    public static GpuDevice Shared
    {
        get
        {
            string mode = Mode;
            if (mode != sharedMode) { try { shared?.Dispose(); } catch { } shared = null; sharedFailed = false; sharedMode = mode; }
            if (shared != null && shared.Lost) { try { shared.Dispose(); } catch { } shared = null; }
            if (!Wanted) { Status = "CPU (GPU 끔)"; return null; }
            if (shared == null && !sharedFailed)
            {
                shared = Create(Mode, out string err);
                if (shared == null) { sharedFailed = true; Status = "CPU (GPU 못 씀: " + err + ")"; }
            }
            return shared;
        }
    }
    /// <summary>장치를 버림 (설정 바꿈·장치 빠짐). allowRetry = 다음에 다시 만들어 보기</summary>
    public static void ResetShared(bool allowRetry)
    {
        try { shared?.Dispose(); } catch { }
        shared = null; sharedFailed = !allowRetry;
        if (!Wanted) Status = "CPU (GPU 끔)";
    }

    public bool Lost
    {
        get
        {
            try { return Dev == null || Dev.DeviceRemovedReason.Failure; } catch { return true; }
        }
    }

    public static GpuDevice Create(string mode, out string error)
    {
        error = null;
        if (mode == "off") { error = "꺼 둠"; return null; }
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var g = new GpuDevice();
        try
        {
            g.Factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            bool hw = false;
            if (mode != "warp")
            {
                for (uint i = 0; g.Factory.EnumAdapters1(i, out IDXGIAdapter1 ad).Success; i++)
                {
                    using (ad)
                    {
                        var desc = ad.Description1;
                        if ((desc.Flags & AdapterFlags.Software) != 0) continue;
                        if (D3D11.D3D11CreateDevice(ad, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device dev, out FeatureLevel fl, out ID3D11DeviceContext ctx).Success)
                        {
                            g.Dev = dev; g.Ctx = ctx; g.Level = fl;
                            g.AdapterName = desc.Description?.Trim();
                            g.DedicatedMB = (long)desc.DedicatedVideoMemory / (1024 * 1024);
                            g.SharedMB = (long)desc.SharedSystemMemory / (1024 * 1024);
                            hw = true;
                            break;
                        }
                    }
                }
            }
            if (!hw)
            {
                if (mode == "auto") { error = "기능 수준 11_0 그래픽 카드 없음"; g.Dispose(); return null; }
                if (D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device dev, out FeatureLevel fl, out ID3D11DeviceContext ctx).Failure)
                { error = "WARP 장치도 못 만듦"; g.Dispose(); return null; }
                g.Dev = dev; g.Ctx = ctx; g.Level = fl; g.Warp = true;
                g.AdapterName = "Microsoft Basic Render Driver (WARP, 소프트웨어)";
            }
            g.LoadShaders();
            Status = $"GPU: {g.AdapterName} · D3D11 {LevelName(g.Level)}" + (g.Warp ? "" : $" · 전용 메모리 {g.DedicatedMB}MB") + $" · 셰이더 {g.ShaderSource}";
            return g;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { g.Dispose(); } catch { }
            return null;
        }
    }

    public static string LevelName(FeatureLevel l) => l switch { FeatureLevel.Level_11_1 => "11_1", FeatureLevel.Level_11_0 => "11_0", FeatureLevel.Level_12_0 => "12_0", FeatureLevel.Level_12_1 => "12_1", _ => l.ToString() };

    void LoadShaders()
    {
        bool pre = true;
        byte[] Get(string file, string entry, string profile)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream($"ComicPadNative.Gpu.Shaders.{file}.{entry}.cso"))
                if (s != null) { var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
            pre = false;
            using var hs = asm.GetManifestResourceStream($"ComicPadNative.Gpu.Shaders.{file}.hlsl") ?? throw new InvalidOperationException("셰이더 원본 없음: " + file);
            string src = new StreamReader(hs).ReadToEnd();
            return Compiler.Compile(src, entry, file + ".hlsl", profile, ShaderFlags.OptimizationLevel3).ToArray();
        }
        CsComposite = Dev.CreateComputeShader(Get("Composite", "CSMain", "cs_5_0"));
        VsView = Dev.CreateVertexShader(Get("View", "VSMain", "vs_5_0"));
        PsCanvas = Dev.CreatePixelShader(Get("View", "PSCanvas", "ps_5_0"));
        ShaderSource = pre ? "미리 컴파일" : "실행 중 컴파일";
    }

    // ---- 작은 도우미 ------------------------------------------------------------------
    public ID3D11Texture2D Tex(int w, int h, Format f, BindFlags bind, int array = 1, ResourceUsage usage = ResourceUsage.Default, CpuAccessFlags cpu = CpuAccessFlags.None)
        => Dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = (uint)array, Format = f, SampleDescription = new SampleDescription(1, 0),
            Usage = usage, BindFlags = bind, CPUAccessFlags = cpu, MiscFlags = ResourceOptionFlags.None,
        });

    public void Dispose()
    {
        try { Ctx?.ClearState(); Ctx?.Flush(); } catch { }
        CsComposite?.Dispose(); VsView?.Dispose(); PsCanvas?.Dispose();
        Ctx?.Dispose(); Dev?.Dispose(); Factory?.Dispose();
        CsComposite = null; VsView = null; PsCanvas = null; Ctx = null; Dev = null; Factory = null;
    }
}
