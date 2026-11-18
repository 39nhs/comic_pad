// 그림판 화면을 GPU 로: DXGI 뒤집기(flip) 방식 스왑 체인을 그림판 창(HWND)에 붙이고
//  - 합성 결과(문서 크기 R32_UINT 텍스처, GPU 합성이 바로 씀 / CPU 합성이면 바뀐 타일만 올림)를
//    보기 변환(확대·이동·회전·반전)으로 그림. 축소 = k×k 상자 평균(최대 8×8), 픽셀 아트 보기·확대 = 가장 가까운 점
//  - 선택 영역 테두리(점선)도 셰이더에서, 문서 밖 바탕색·투명 = 흰 바탕은 CPU 화면과 같은 규칙
//  - 겹침 그림(도구 표시·자·원고 가이드·크기 원·펜 예측)은 지금 그대로 GDI+ 로: 창 크기 투명 비트맵(미리 곱한 알파)에
//    그린 뒤 바뀐 부분만 텍스처로 올려 같은 셰이더에서 얹음.
//    (Direct2D 로 바꾸지 않은 까닭: 겹침 그림은 여러 모듈이 Graphics 로 그리는 확장 지점이라 모두 고쳐야 함. GDI+ 그대로면 모양도 같음)
//  - 한 장 그린 결과(frame)를 따로 두어 화면 찍기(DrawToBitmap)·시험이 GPU 화면을 그대로 읽어 감
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using ComicPadNative.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Rect = ComicPadNative.Core.Rect;

namespace ComicPadNative.Gpu;

public sealed class GpuView : IDisposable
{
    public readonly GpuDevice G;
    public GpuCompositor Compositor;
    readonly IntPtr hwnd;
    IDXGISwapChain1 sc;
    public int W, H;
    ID3D11Texture2D frame, frameStaging;
    ID3D11RenderTargetView frameRtv;
    // 합성 결과
    ID3D11Texture2D comp; ID3D11ShaderResourceView compSrv;
    public int CompW, CompH;
    public bool CompReady;
    // 선택 영역
    ID3D11Texture2D sel; ID3D11ShaderResourceView selSrv;
    byte[] selRef; Vector256<uint> selHash; bool selCheck; Rect selDirty = Rect.Empty;
    // 겹침 그림
    ID3D11Texture2D ov; ID3D11ShaderResourceView ovSrv;
    uint[] ovPx; GCHandle ovPin; Bitmap ovBmp;
    Rectangle ovDirty = Rectangle.Empty;
    // 기타
    ID3D11Texture2D dummyU32, dummyU8; ID3D11ShaderResourceView dummyU32Srv, dummyU8Srv;
    ID3D11Buffer viewCb;
    ID3D11RasterizerState scissorState;
    (double, double, double, double, int, int, int) tabKey;
    ID3D11Buffer tab; ID3D11ShaderResourceView tabSrv; int tabLen; int[] tabData = System.Array.Empty<int>();
    bool needFull = true; ViewParams lastParams;
    static readonly uint[] zeroTile = new uint[Surface.TS * Surface.TS];

    public static int MaxTaps = 8;              // 축소 평균 최대 k (CPU 화면은 4)
    public string Error;
    public bool AdapterChanged;
    public double LastRenderMs, LastPresentMs;
    public long Frames;

    [StructLayout(LayoutKind.Sequential)]
    struct ViewParams
    {
        public float Ox, Oy, Ax, Ay, Bx, By; public int K, HasSel;
        public int DocW, DocH; public uint Bg; public int HasDoc;
        public float InvK; public int Axis, RowBase, Stride;
    }

    /// <summary>hwnd = 0 이면 창 없이(시험·찍기) 그리기만</summary>
    public GpuView(GpuDevice g, IntPtr hwnd, int w, int h)
    {
        G = g; this.hwnd = hwnd;
        W = Math.Max(1, w); H = Math.Max(1, h);
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                var desc = new SwapChainDescription1
                {
                    Width = (uint)W, Height = (uint)H, Format = Format.B8G8R8A8_UNorm, Stereo = false,
                    SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
                    Scaling = Scaling.None, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore, Flags = SwapChainFlags.None,
                };
                sc = g.Factory.CreateSwapChainForHwnd(g.Dev, hwnd, desc);
                g.Factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
            }
            CreateSized();
            dummyU32 = g.Tex(1, 1, Format.R32_UInt, BindFlags.ShaderResource); dummyU32Srv = g.Dev.CreateShaderResourceView(dummyU32);
            dummyU8 = g.Tex(1, 1, Format.R8_UInt, BindFlags.ShaderResource); dummyU8Srv = g.Dev.CreateShaderResourceView(dummyU8);
            viewCb = g.Dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<ViewParams>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            scissorState = g.Dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { ScissorEnable = true });
            Compositor = new GpuCompositor(g);
        }
        catch { Dispose(); throw; }
    }

    unsafe void CreateSized()
    {
        frame = G.Tex(W, H, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
        frameRtv = G.Dev.CreateRenderTargetView(frame);
        ov = G.Tex(W, H, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource);
        ovSrv = G.Dev.CreateShaderResourceView(ov);
        ovPx = new uint[W * H];
        ovPin = GCHandle.Alloc(ovPx, GCHandleType.Pinned);
        ovBmp = new Bitmap(W, H, W * 4, PixelFormat.Format32bppPArgb, ovPin.AddrOfPinnedObject());
        ovDirty = new Rectangle(0, 0, W, H);
        needFull = true;
    }
    void ReleaseSized()
    {
        ovBmp?.Dispose(); ovBmp = null;
        if (ovPin.IsAllocated) ovPin.Free();
        ovPx = null;
        ovSrv?.Dispose(); ov?.Dispose(); frameRtv?.Dispose(); frame?.Dispose(); frameStaging?.Dispose();
        ovSrv = null; ov = null; frameRtv = null; frame = null; frameStaging = null;
    }

    /// <summary>창 크기 바뀜 (최소화로 0 이면 그대로 둠)</summary>
    public void Resize(int w, int h)
    {
        if (w <= 0 || h <= 0 || (w == W && h == H)) return;
        G.Ctx.ClearState(); G.Ctx.Flush();
        ReleaseSized();
        W = w; H = h;
        sc?.ResizeBuffers(0, (uint)W, (uint)H, Format.Unknown, SwapChainFlags.None).CheckError();
        CreateSized();
    }

    // ---- 합성 결과 -----------------------------------------------------------------------
    /// <summary>문서 크기에 맞는 합성 텍스처 (새로 만들었으면 true → 전체를 다시 합성해야 함)</summary>
    public bool EnsureComp(int w, int h)
    {
        if (comp != null && CompW == w && CompH == h) return false;
        compSrv?.Dispose(); comp?.Dispose();
        CompW = w; CompH = h; CompReady = false;
        comp = G.Tex(w, h, Format.R32_UInt, BindFlags.ShaderResource);
        compSrv = G.Dev.CreateShaderResourceView(comp);
        return true;
    }
    public void ResetComp() { CompReady = false; }

    /// <summary>GPU 로 합성 (r 이 문서 전체가 아니면 이미 합성된 상태여야 함)</summary>
    public void Composite(Document d, Rect r, RenderOptions o)
    {
        if (EnsureComp(d.W, d.H) || !CompReady) r = new Rect(0, 0, d.W, d.H);
        Compositor.Render(d, r, o, comp);
        CompReady = true;
    }

    /// <summary>CPU 합성 캐시의 영역 r 을 올림 (바뀐 타일만)</summary>
    public unsafe void UploadComp(Surface s, Rect r)
    {
        if (EnsureComp(s.W, s.H) || !CompReady) r = s.Full;
        foreach (int ti in s.TilesIn(r))
        {
            var tr = s.TileRect(ti);
            var t = s.TileAt(ti) ?? zeroTile;
            fixed (uint* p = t)
                G.Ctx.UpdateSubresource(comp, 0, new Box(tr.X, tr.Y, 0, tr.R, tr.B, 1), (IntPtr)p, Surface.TS * 4, Surface.TS * Surface.TS * 4);
        }
        CompReady = true;
    }

    /// <summary>CPU 로 합성한 영역 r (r.W*r.H 배열) 을 올림</summary>
    public unsafe void UploadRect(Rect r, uint[] px)
    {
        fixed (uint* p = px)
            G.Ctx.UpdateSubresource(comp, 0, new Box(r.X, r.Y, 0, r.R, r.B, 1), (IntPtr)p, (uint)(r.W * 4), (uint)(r.W * r.H * 4));
    }

    /// <summary>합성 결과 일부를 CPU 로 (시험용)</summary>
    public unsafe uint[] ReadComp(Rect r)
    {
        using var st = G.Tex(r.W, r.H, Format.R32_UInt, BindFlags.None, 1, ResourceUsage.Staging, CpuAccessFlags.Read);
        G.Ctx.CopySubresourceRegion(st, 0, 0, 0, 0, comp, 0, new Box(r.X, r.Y, 0, r.R, r.B, 1));
        var m = G.Ctx.Map(st, 0, MapMode.Read);
        var a = new uint[r.W * r.H];
        fixed (uint* d = a)
            for (int y = 0; y < r.H; y++) Buffer.MemoryCopy((byte*)m.DataPointer + (long)y * m.RowPitch, d + y * r.W, r.W * 4, r.W * 4);
        G.Ctx.Unmap(st, 0);
        return a;
    }

    // ---- 선택 영역 -----------------------------------------------------------------------
    /// <summary>문서 영역 r 이 바뀜 (빈 영역 = 화면만 → 선택이 바뀌었을 수 있으니 전체 확인)</summary>
    public void NoteSelection(Rect r) { if (r.IsEmpty) selCheck = true; else selDirty = selDirty.Union(r); }

    unsafe void SyncSelection(Document d)
    {
        var m = d.Sel?.Mask;
        if (m == null || m.Length != d.W * d.H) { selRef = null; selDirty = Rect.Empty; selCheck = false; return; }
        bool full = false;
        if (sel == null || !ReferenceEquals(m, selRef) || CompW != d.W || CompH != d.H)
        {
            if (sel == null || SelW != d.W || SelH != d.H)
            {
                selSrv?.Dispose(); sel?.Dispose();
                sel = G.Tex(d.W, d.H, Format.R8_UInt, BindFlags.ShaderResource); selSrv = G.Dev.CreateShaderResourceView(sel);
                SelW = d.W; SelH = d.H;
            }
            full = true;
        }
        else if (selCheck)
        {
            var h = HashBytes(m);
            if (h != selHash) full = true;
        }
        fixed (byte* p = m)
        {
            if (full)
            {
                G.Ctx.UpdateSubresource(sel, 0, null, (IntPtr)p, (uint)d.W, (uint)(d.W * d.H));
                selHash = HashBytes(m);
                needFull = true;   // 선택이 통째로 바뀜 → 테두리가 어디든 바뀔 수 있음
            }
            else if (!selDirty.IsEmpty)
            {
                var r = selDirty.Clip(d.W, d.H);
                if (!r.IsEmpty)
                {
                    G.Ctx.UpdateSubresource(sel, 0, new Box(r.X, r.Y, 0, r.R, r.B, 1), (IntPtr)(p + (long)r.Y * d.W + r.X), (uint)d.W, (uint)(d.W * r.H));
                    selHash = HashBytes(m);
                }
            }
        }
        selRef = m; selDirty = Rect.Empty; selCheck = false;
    }
    int SelW, SelH;

    static Vector256<uint> HashBytes(byte[] b)
    {
        int n = b.Length / 32 * 32;
        var v = MemoryMarshal.Cast<byte, Vector256<uint>>(b.AsSpan(0, n));
        var k = Vector256.Create(0x9E3779B1u);
        Vector256<uint> h = Vector256.Create(0x85EBCA77u);
        foreach (var x in v) h = (h ^ x) * k;
        uint tail = 0;
        for (int i = n; i < b.Length; i++) tail = tail * 31 + b[i];
        return h ^ Vector256.Create(tail);
    }

    // ---- 겹침 그림 -------------------------------------------------------------------------
    /// <summary>겹침 비트맵의 영역을 투명으로 지움</summary>
    public void ClearOverlay(Rectangle r)
    {
        r.Intersect(new Rectangle(0, 0, W, H));
        if (r.Width <= 0 || r.Height <= 0) return;
        for (int y = r.Top; y < r.Bottom; y++) System.Array.Clear(ovPx, y * W + r.Left, r.Width);
        MarkOverlay(r);
    }
    /// <summary>겹침 비트맵에 그릴 Graphics (다 쓰면 Dispose)</summary>
    public Graphics OverlayGraphics() => Graphics.FromImage(ovBmp);
    public void MarkOverlay(Rectangle r)
    {
        r.Intersect(new Rectangle(0, 0, W, H));
        if (r.Width <= 0 || r.Height <= 0) return;
        ovDirty = ovDirty.IsEmpty ? r : Rectangle.Union(ovDirty, r);
    }

    unsafe void SyncOverlay()
    {
        var r = ovDirty; ovDirty = Rectangle.Empty;
        r.Intersect(new Rectangle(0, 0, W, H));
        if (r.Width <= 0 || r.Height <= 0) return;
        fixed (uint* p = ovPx)
            G.Ctx.UpdateSubresource(ov, 0, new Box(r.X, r.Y, 0, r.Right, r.Bottom, 1), (IntPtr)(p + (long)r.Y * W + r.X), (uint)(W * 4), (uint)(W * 4 * r.Height));
    }

    // ---- 한 장 그리기 ----------------------------------------------------------------------
    /// <summary>보기 변환으로 화면(frame)을 그림. d == null 이면 바탕색만.
    /// dirty = 다시 그릴 화면 영역 (null = 전체). 보기가 바뀌었거나 처음이면 늘 전체. 그릴 것이 없으면 false</summary>
    public unsafe bool Render(ViewXf v, Document d, bool crisp, Rectangle? dirty = null)
    {
        long t0 = Stopwatch.GetTimestamp();
        var ctx = G.Ctx;
        bool hasDoc = d != null && comp != null && CompReady && CompW == d.W && CompH == d.H;
        if (hasDoc) SyncSelection(d);
        SyncOverlay();
        double z = v.Zoom;
        int k = z >= 1 || crisp ? 1 : Math.Min(MaxTaps, (int)Math.Ceiling(1 / z));
        var (ox, oy) = v.ToDoc(0, 0); var (xx, xy) = v.ToDoc(1, 0); var (yx, yy) = v.ToDoc(0, 1);
        bool hasSel = hasDoc && selRef != null && sel != null;
        var p = new ViewParams
        {
            Ox = (float)ox, Oy = (float)oy, Ax = (float)(xx - ox), Ay = (float)(xy - oy), Bx = (float)(yx - ox), By = (float)(yy - oy),
            K = k, HasSel = hasSel ? 1 : 0, DocW = hasDoc ? d.W : 0, DocH = hasDoc ? d.H : 0, Bg = 0xFF4B4B52, HasDoc = hasDoc ? 1 : 0, InvK = 1f / k,
        };
        // 회전이 없으면 줄·칸 표 (CanvasView.RenderScreen 과 같은 double 계산 → 경계도 똑같음)
        if (hasDoc && v.Angle == 0)
        {
            p.Axis = 1; p.Stride = k + 2; p.RowBase = W * (k + 2);
            var key = (ox, oy, xx - ox, yy - oy, k, W, H);
            if (!key.Equals(tabKey) || tab == null) { FillTab(ox, oy, xx - ox, xy - oy, yx - ox, yy - oy, k); tabKey = key; needFull = true; }
        }
        // 보기가 바뀌면 전체, 아니면 바뀐 부분만 (나머지는 frame 에 남아 있음)
        var area = new Rectangle(0, 0, W, H);
        if (dirty is Rectangle dr && !needFull && p.Equals(lastParams))
        {
            dr.Intersect(area);
            if (dr.Width <= 0 || dr.Height <= 0) { LastRenderMs = 0; return false; }
            area = dr;
        }
        needFull = false; lastParams = p;
        var m = ctx.Map(viewCb, 0, MapMode.WriteDiscard);
        *(ViewParams*)m.DataPointer = p;
        ctx.Unmap(viewCb, 0);
        ctx.CSSetUnorderedAccessView(0, null);
        ctx.OMSetRenderTargets(frameRtv);
        ctx.RSSetViewport(new Viewport(0, 0, W, H));
        ctx.RSSetState(scissorState);
        ctx.RSSetScissorRect(area.X, area.Y, area.Width, area.Height);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(null);
        ctx.VSSetShader(G.VsView);
        ctx.PSSetShader(G.PsCanvas);
        ctx.PSSetConstantBuffer(0, viewCb);
        ctx.PSSetShaderResource(0, hasDoc ? compSrv : dummyU32Srv);
        ctx.PSSetShaderResource(1, hasSel ? selSrv : dummyU8Srv);
        ctx.PSSetShaderResource(2, ovSrv);
        ctx.PSSetShaderResource(3, p.Axis != 0 ? tabSrv : null);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, null); ctx.PSSetShaderResource(1, null); ctx.PSSetShaderResource(2, null); ctx.PSSetShaderResource(3, null);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null);
        ctx.RSSetState(null);
        Frames++; LastArea = area.Width * area.Height;
        LastRenderMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        return true;
    }
    public long LastArea;

    /// <summary>줄·칸 표: CPU 화면과 똑같은 식(double)으로 문서 점을 버림한 정수</summary>
    unsafe void FillTab(double ox, double oy, double ax, double ay, double bxv, double byv, int k)
    {
        int st = k + 2, n = (W + H) * st;
        if (tabData.Length < n) tabData = new int[n];
        for (int x = 0; x < W; x++)
        {
            double dx = ox + ax * (x + 0.5) + bxv * (0 + 0.5);   // 회전이 없으면 bxv = 0 → y 와 상관없음
            int b = x * st;
            if (k == 1) tabData[b] = (int)Math.Floor(dx);
            else for (int i = 0; i < k; i++) tabData[b + i] = (int)Math.Floor(dx + ax * ((i + 0.5) / k - 0.5) + bxv * 0.0);
            tabData[b + k] = (int)Math.Floor(dx); tabData[b + k + 1] = (int)Math.Floor(dx + ax);
        }
        for (int y = 0; y < H; y++)
        {
            double dy = oy + ay * (0 + 0.5) + byv * (y + 0.5);   // ay = 0
            int b = (W + y) * st;
            if (k == 1) tabData[b] = (int)Math.Floor(dy);
            else for (int j = 0; j < k; j++) tabData[b + j] = (int)Math.Floor(dy + ay * 0.0 + byv * ((j + 0.5) / k - 0.5));
            tabData[b + k] = (int)Math.Floor(dy); tabData[b + k + 1] = (int)Math.Floor(dy + byv);
        }
        if (tab == null || tabLen < n)
        {
            tabSrv?.Dispose(); tab?.Dispose();
            tabLen = n;
            tab = G.Dev.CreateBuffer(new BufferDescription((uint)(n * 4), BindFlags.ShaderResource, ResourceUsage.Default));
            tabSrv = G.Dev.CreateShaderResourceView(tab, new ShaderResourceViewDescription(tab, Format.R32_SInt, 0, (uint)n));
        }
        fixed (int* q = tabData) G.Ctx.UpdateSubresource(tab, 0, new Box(0, 0, 0, n * 4, 1, 1), (IntPtr)q, (uint)(n * 4), 0);
    }

    /// <summary>그린 화면을 창에 올림. 장치가 빠지면 false</summary>
    public bool Present()
    {
        if (sc == null) { G.Ctx.Flush(); return true; }
        long t0 = Stopwatch.GetTimestamp();
        using (var bb = sc.GetBuffer<ID3D11Texture2D>(0))
            G.Ctx.CopyResource(bb, frame);
        var hr = sc.Present(0, PresentFlags.None);
        LastPresentMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (hr.Failure) { Error = $"Present 실패 0x{hr.Code:X8}"; return false; }
        if (!G.Factory.IsCurrent) { Error = "그래픽 카드 구성이 바뀜"; AdapterChanged = true; return false; }
        return true;
    }

    /// <summary>GPU 가 다 그릴 때까지 기다림 (시간 재기)</summary>
    public void Finish()
    {
        using var q = G.Dev.CreateQuery(new QueryDescription(QueryType.Event));
        G.Ctx.End(q);
        G.Ctx.Flush();
        while (!G.Ctx.IsDataAvailable(q)) Thread.SpinWait(50);
    }

    /// <summary>마지막으로 그린 화면을 비트맵으로 (화면 찍기·시험)</summary>
    public unsafe Bitmap Snapshot()
    {
        frameStaging ??= G.Tex(W, H, Format.B8G8R8A8_UNorm, BindFlags.None, 1, ResourceUsage.Staging, CpuAccessFlags.Read);
        G.Ctx.CopyResource(frameStaging, frame);
        var m = G.Ctx.Map(frameStaging, 0, MapMode.Read);
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < H; y++)
            {
                uint* s = (uint*)((byte*)m.DataPointer + (long)y * m.RowPitch), dd = (uint*)((byte*)bd.Scan0 + (long)y * bd.Stride);
                for (int x = 0; x < W; x++) dd[x] = s[x] | 0xFF000000;
            }
        }
        finally { bmp.UnlockBits(bd); G.Ctx.Unmap(frameStaging, 0); }
        return bmp;
    }

    public void Dispose()
    {
        try { G.Ctx?.ClearState(); } catch { }
        ReleaseSized();
        Compositor?.Dispose(); Compositor = null;
        compSrv?.Dispose(); comp?.Dispose(); selSrv?.Dispose(); sel?.Dispose();
        dummyU32Srv?.Dispose(); dummyU32?.Dispose(); dummyU8Srv?.Dispose(); dummyU8?.Dispose();
        viewCb?.Dispose(); scissorState?.Dispose(); tabSrv?.Dispose(); tab?.Dispose(); sc?.Dispose();
        compSrv = null; comp = null; selSrv = null; sel = null; sc = null; viewCb = null;
        try { G.Ctx?.Flush(); } catch { }
    }
}
