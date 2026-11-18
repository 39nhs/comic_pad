// GPU 레이어 합성 — Core/Compositor.cs 와 같은 규칙을 그래픽 카드에서 (계산 셰이더 Composite.hlsl).
//  - 폴더(따로 합성 후 폴더 모드·불투명도로), 클리핑(표준 = source-atop / 다른 모드 = 기준 모양으로 자른 뒤 그 모드),
//    레이어 마스크, 레이어 컬러, 조정 레이어(같은 단계 아래 전체에, 클리핑이면 기준에만), 보이기, 초안 빼기, 16가지 합성 모드
//  - GPU 가 아직 못 하는 것은 그 부분만 CPU 로 (결과는 같음):
//      레이어 효과(테두리·그림자·광선), 애니 트랙 폴더(어니언 포함) → 그 레이어만 CPU Compositor.RenderLayer 후 올림
//      조정 레이어 보정은 GPU (필터 묶음 = 표·색 행렬·단색 입히기를 같은 정수·float 계산으로). 모르는 보정 함수면
//      용지 질감(결)처럼 그 조각을 내려받아 CPU 로 계산 후 다시 올림
//      Only(채우기 참조) 처럼 구조를 무시하는 합성 → 전체 CPU (Supports 가 false)
//  - 큰 영역은 조각(최대 2048×2048)으로 나눠 그림 → 조각마다 필요한 타일만 GPU 에 있으면 됨
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Blend = ComicPadNative.Core.Blend;
using Rect = ComicPadNative.Core.Rect;

namespace ComicPadNative.Gpu;

public sealed class GpuCompositor : IDisposable
{
    public const int MaxChunk = 2048;
    readonly GpuDevice g;
    public readonly TileCache Cache;
    readonly ID3D11Buffer cb;
    readonly List<Buf> freeBufs = new();
    readonly List<Buf> allBufs = new();
    ID3D11Texture2D staging;
    readonly Compositor cpu = new();

    // 이번 합성
    Document doc; RenderOptions o; Dictionary<string, List<Layer>> kids;
    Rect C;                          // 지금 조각 (문서 좌표)
    long stamp;
    // 통계 (시험·화면 표시)
    public int CpuLayers, CpuHooks, GpuAdjusts, Dispatches, Chunks;
    ID3D11ShaderResourceView progSrv; uint progOps;
    public string LastNote = "";

    sealed class Buf { public ID3D11Texture2D Tex; public ID3D11ShaderResourceView Srv; public ID3D11UnorderedAccessView Uav; }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct Params
    {
        public uint Op, Mode, KOp, Color;
        public int Ox, Oy; public uint W, H;
        public int DocW, DocH, Tx0, Ty0;
        public uint LutW, NOps, P1, P2;
        public fixed int Lut[256];
    }
    const uint OP_LAYER = 0, OP_BUF = 1, OP_MASK = 2, OP_TINT = 3, OP_FILL = 4, OP_ADJUST = 5;

    public GpuCompositor(GpuDevice g, int tileCapacity = 0)
    {
        this.g = g;
        if (tileCapacity <= 0)
        {
            // 그래픽 메모리에 맞춰: 전용 2GB 이상(또는 공유 3.5GB 이상 내장 그래픽)이면 2048칸(512MB, D3D11 배열 최대), 아니면 1024칸. WARP 는 512칸
            tileCapacity = g.Warp ? 512 : g.DedicatedMB >= 1900 || g.SharedMB >= 3500 ? 2048 : 1024;
        }
        Cache = new TileCache(g, tileCapacity);
        cb = g.Dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    /// <summary>이 합성을 GPU 로 할 수 있나 (안 되면 부르는 쪽이 CPU 로)</summary>
    public static bool Supports(Document d, RenderOptions o) => d != null && (o == null || o.Only == null) && d.W <= 16384 && d.H <= 16384;

    // ---- 바깥 입구 ---------------------------------------------------------------------
    /// <summary>문서 영역 r 을 합성: target(문서 크기 R32_UINT 텍스처)의 같은 자리에 넣거나, cpuOut(r.W*r.H)에 받음</summary>
    public void Render(Document d, Rect r, RenderOptions opt, ID3D11Texture2D target, uint[] cpuOut = null)
    {
        r = r.Clip(d.W, d.H);
        if (r.IsEmpty) return;
        doc = d; o = opt ?? new RenderOptions();
        kids = new();
        foreach (var l in d.Layers) { var p = d.ParentOf(l) ?? ""; if (!kids.TryGetValue(p, out var list)) kids[p] = list = new(); list.Add(l); }
        cpu.Begin(d, o);
        CpuLayers = CpuHooks = GpuAdjusts = Dispatches = Chunks = 0;
        var surfs = SurfacesOf(d).ToList();
        Cache.BeginPass(surfs, r);
        // 조각 크기: (쓰는 면 수 × 조각 안 타일 수) 가 칸 수의 90% 를 넘지 않게
        int cs = MaxChunk;
        int nSurf = Math.Max(1, surfs.Count);
        while (cs > Surface.TS && nSurf * (cs / Surface.TS) * (cs / Surface.TS) > Cache.Capacity * 9 / 10) cs /= 2;
        RenderChunks(r, cs, target, cpuOut);
    }

    void RenderChunks(Rect r, int cs, ID3D11Texture2D target, uint[] cpuOut)
    {
        for (int cy = r.Y / cs * cs; cy < r.B; cy += cs)
            for (int cx = r.X / cs * cs; cx < r.R; cx += cs)
            {
                var part = new Rect(cx, cy, cs, cs).Intersect(r);
                if (part.IsEmpty) continue;
                try { RenderChunk(part, target, cpuOut, r); }
                catch (GpuFullException) when (cs > Surface.TS)
                {
                    ReleaseAll();
                    RenderChunks(part, cs / 2, target, cpuOut);
                }
            }
    }

    void RenderChunk(Rect part, ID3D11Texture2D target, uint[] cpuOut, Rect whole)
    {
        C = part; stamp++; Chunks++;
        var root = Get();
        Fill(root, 0);
        var roots = kids.GetValueOrDefault("") ?? new();
        bool paper = o.Paper && doc.PaperVisible;
        bool paperInLevel = paper && roots.Any(l => l.IsAdjust && !l.Clip && l.Visible);
        if (paper && !paperInLevel) Paper(root);
        RenderList(root, roots, 0, paperInLevel);
        if (paper) PaperOver(root);
        if (target != null)
            g.Ctx.CopySubresourceRegion(target, 0, (uint)C.X, (uint)C.Y, 0, root.Tex, 0, new Box(0, 0, 0, C.W, C.H, 1));
        if (cpuOut != null)
        {
            var tmp = Download(root);
            for (int y = 0; y < C.H; y++) System.Array.Copy(tmp, y * C.W, cpuOut, (C.Y - whole.Y + y) * whole.W + (C.X - whole.X), C.W);
        }
        Put(root);
    }

    // ---- Compositor.RenderList 와 같은 순서 ------------------------------------------------
    bool VisibleL(Layer l) => l.Visible && !(o.ExcludeDraft && l.Draft);
    bool Special(Layer l) => l.IsFolder || l.Tint != null || l.Tone != null || Hooks.HasFx(l) || (l.Mask != null && l.MaskOn) || (o.Override?.ContainsKey(l) ?? false);
    List<Layer> KidsOf(Layer f) => kids.GetValueOrDefault(f?.Id ?? "") ?? new();

    void RenderList(Buf buf, List<Layer> L, int depth, bool paperInLevel = false)
    {
        bool hasAdjust = L.Any(l => l.IsAdjust && !l.Clip && VisibleL(l));
        var outb = buf;
        if (hasAdjust)
        {
            outb = Get(); Fill(outb, 0);
            if (depth == 0 && paperInLevel) Paper(outb);
        }
        int i = 0;
        while (i < L.Count)
        {
            var b = L[i];
            int j = i + 1;
            while (j < L.Count && L[j].Clip) j++;
            var clips = L.GetRange(i + 1, j - i - 1).Where(VisibleL).ToList();
            i = j;
            if (!VisibleL(b) || b.Opacity <= 0) continue;
            var mode = Px.Parse(b.Blend);
            if (b.IsAdjust) { if (hasAdjust) ApplyAdjust(outb, b); continue; }
            if (clips.Count == 0 && !Special(b))
            {
                if (b.Px != null) LayerOp(outb, b.Px, OP_LAYER, mode, b.Opacity);
                continue;
            }
            var bx = RenderLayer(b, depth);
            if (clips.Count > 0)
            {
                Buf mask = clips.Any(c => c.Blend != "source-over" && !c.IsAdjust) ? Clone(bx) : null;
                foreach (var c in clips)
                {
                    if (c.IsAdjust) { ApplyAdjust(bx, c); continue; }
                    var cx = RenderLayer(c, depth);
                    if (c.Blend == "source-over") BufOp(bx, cx, Blend.Atop, c.Opacity);
                    else
                    {
                        BufOp(cx, mask, Blend.DestIn, 1);
                        BufOp(bx, cx, Px.Parse(c.Blend), c.Opacity);
                    }
                    Put(cx);
                }
                if (mask != null) Put(mask);
            }
            BufOp(outb, bx, mode, b.Opacity);
            Put(bx);
        }
        if (hasAdjust) { BufOp(buf, outb, Blend.Normal, 1); Put(outb); }
    }

    Buf RenderLayer(Layer l, int depth)
    {
        Surface src = null;
        if (!l.IsFolder)
            src = o.Override != null && o.Override.TryGetValue(l, out var ov) ? ov : l.Tone != null ? Hooks.Tone(l, doc) ?? l.Px : l.Px;
        bool anim = l.IsFolder && l.Meta?["anim"] is JsonNode;
        bool odd = (src != null && (src.W != doc.W || src.H != doc.H)) || (l.Mask != null && l.MaskOn && (l.Mask.W != doc.W || l.Mask.H != doc.H));
        if (anim || Hooks.HasFx(l) || odd)
        {
            // 이 레이어(폴더면 그 안 전부)만 CPU 로 그려 올림
            CpuLayers++;
            LastNote = anim ? "애니 폴더 CPU" : odd ? "크기 다른 면 CPU" : "레이어 효과 CPU";
            var arr = cpu.RenderLayer(l, C, depth);
            var bb = Get(); Upload(bb, arr);
            return bb;
        }
        var x = Get(); Fill(x, 0);
        if (l.IsFolder) RenderList(x, KidsOf(l), depth + 1);
        else if (src != null) LayerOp(x, src, OP_LAYER, Blend.Copy, 1);
        if (l.Mask != null && l.MaskOn) LayerOp(x, l.Mask, OP_MASK, Blend.Normal, 1);
        if (l.Tint != null) Op(x, OP_TINT, Blend.Normal, 255, Px.FromHex(l.Tint), null, null);
        return x;
    }

    void ApplyAdjust(Buf target, Layer l)
    {
        Buf tb;
        var prog = AdjustProgram(l, out uint nOps);
        if (prog != null)
        {
            // GPU: 복사본에 보정 프로그램
            GpuAdjusts++;
            tb = Clone(target);
            if (nOps > 0)
            {
                using var buf = MakeProg(prog);
                using var srv = g.Dev.CreateShaderResourceView(buf, new ShaderResourceViewDescription(buf, Format.R32_UInt, 0, (uint)prog.Length));
                progSrv = srv; progOps = nOps;
                try { Op(tb, OP_ADJUST, Blend.Normal, 255, 0, null, null); }
                finally { progSrv = null; progOps = 0; }
            }
        }
        else
        {
            // 모르는 보정 함수: 조각을 내려받아 CPU 로 보정 → 올림
            CpuHooks++;
            var t = Download(target);
            Hooks.Adjust(t, C, l, doc);
            tb = Get(); Upload(tb, t);
        }
        if (l.Mask != null && l.MaskOn) LayerOp(tb, l.Mask, OP_MASK, Blend.Normal, 1);
        BufOp(target, tb, Blend.Atop, l.Opacity);
        Put(tb);
    }

    unsafe ID3D11Buffer MakeProg(uint[] prog)
    {
        fixed (uint* p = prog)
            return g.Dev.CreateBuffer(new BufferDescription((uint)(prog.Length * 4), BindFlags.ShaderResource, ResourceUsage.Immutable), new SubresourceData((IntPtr)p));
    }

    /// <summary>조정 레이어 보정 → GPU 프로그램 (Prog 버퍼: 단계들 뒤에 표). 기본 보정 함수가 아니거나 모르는 단계면 null</summary>
    static uint[] AdjustProgram(Layer l, out uint nOps)
    {
        nOps = 0;
        var m = Hooks.Adjust.Method;
        if (m.DeclaringType != typeof(Modules.Filters.LayerEffects) || m.Name != "Adjust") return null;
        Modules.Filters.FilterChain f;
        if (Modules.Filters.LayerEffects.PreviewLayer == l && Modules.Filters.LayerEffects.PreviewChain != null) f = Modules.Filters.LayerEffects.PreviewChain;
        else
        {
            string type = (string)l.Meta?["type"];
            if (type == null) return System.Array.Empty<uint>();
            f = Modules.Filters.Svg.AdjustCached(type, l.Meta?["params"] as JsonObject);
        }
        var code = new List<uint>();
        var luts = new List<(int at, byte[] t)>();
        uint F(float v) => BitConverter.SingleToUInt32Bits(v);
        foreach (var op in f.Ops)
        {
            switch (op)
            {
                case Modules.Filters.TransferOp t:
                    code.Add(0);
                    foreach (var ch in new[] { t.R, t.G, t.B })
                    {
                        if (ch == null || ch.Length < 256) { code.Add(0xFFFFFFFF); continue; }
                        luts.Add((code.Count, ch)); code.Add(0);
                    }
                    break;
                case Modules.Filters.MatrixOp mo:
                    if (mo.M == null || mo.M.Length < 15) return null;
                    code.Add(1); for (int i = 0; i < 20; i++) code.Add(i < mo.M.Length ? F(mo.M[i]) : 0);
                    break;
                case Modules.Filters.ArithTintOp k:
                    if (k.M == null || k.M.Length < 15) return null;
                    code.Add(2); for (int i = 0; i < 20; i++) code.Add(i < k.M.Length ? F(k.M[i]) : 0);
                    code.Add(F(k.K2)); code.Add(F(k.K3));
                    break;
                default: return null;
            }
            nOps++;
        }
        foreach (var (at, t) in luts)
        {
            code[at] = (uint)code.Count;
            for (int i = 0; i < 256; i++) code.Add(t[i]);
        }
        return code.ToArray();
    }

    // ---- 용지 --------------------------------------------------------------------------
    /// <summary>용지가 한 색이면 그 색 (질감이 있거나 모르는 용지 함수면 false → CPU 로)</summary>
    public static bool PlainPaper(Document d, out uint color)
    {
        color = 0;
        var m = Hooks.Paper.Method;
        if (m.DeclaringType != typeof(Modules.Filters.Paper) || m.Name != "Draw") return false;
        if (TextureOn(d)) return false;
        color = Px.FromHex(d.PaperColor, 0xFFFFFFFF) | 0xFF000000;
        return true;
    }
    static bool TextureOn(Document d)
    {
        var t = Modules.Filters.Paper.Texture(d);
        if (t == null) return false;
        string type = (string)t["type"];
        if (type == null || type == "none" || !(Modules.Filters.Svg.Num(t, "strength") > 0)) return false;
        return Modules.Filters.Paper.Tile(type) != null;
    }
    static bool PaperOverNoop(Document d)
    {
        var m = Hooks.PaperOver.Method;
        if (m.DeclaringType != typeof(Modules.Filters.Paper) || m.Name != "Over") return false;
        var t = Modules.Filters.Paper.Texture(d);
        return t == null || (bool?)t["over"] != true || !TextureOn(d);
    }

    void Paper(Buf b)
    {
        if (PlainPaper(doc, out uint c)) { Fill(b, c); return; }
        CpuHooks++;
        var arr = new uint[C.W * C.H];
        Hooks.Paper(arr, C, doc);
        Upload(b, arr);
    }
    void PaperOver(Buf b)
    {
        if (PaperOverNoop(doc)) return;
        CpuHooks++;
        var arr = Download(b);
        Hooks.PaperOver(arr, C, doc);
        Upload(b, arr);
    }

    // ---- 면 목록 (타일 지문 미리 확인용) -------------------------------------------------
    IEnumerable<Surface> SurfacesOf(Document d)
    {
        var seen = new HashSet<Surface>();
        foreach (var l in d.Layers)
        {
            if (!VisibleL(l)) continue;
            Surface s = o.Override != null && o.Override.TryGetValue(l, out var ov) ? ov : l.Tone != null ? Hooks.Tone(l, d) ?? l.Px : l.Px;
            if (s != null && s.W == d.W && s.H == d.H && seen.Add(s)) yield return s;
            if (l.Mask != null && l.MaskOn && l.Mask.W == d.W && l.Mask.H == d.H && seen.Add(l.Mask)) yield return l.Mask;
        }
    }

    // ---- GPU 작업 ----------------------------------------------------------------------
    static uint K(float op) => op < 1 ? (uint)(op * 255 + 0.5f) : 255;

    unsafe void Op(Buf dst, uint op, Blend mode, uint k, uint color, Buf src, Surface tiles)
    {
        var p = new Params { Op = op, Mode = (uint)mode, KOp = k, Color = color, Ox = C.X, Oy = C.Y, W = (uint)C.W, H = (uint)C.H, NOps = progOps };
        if (tiles != null)
        {
            int tx0 = C.X / Surface.TS, ty0 = C.Y / Surface.TS, tx1 = (C.R - 1) / Surface.TS, ty1 = (C.B - 1) / Surface.TS;
            p.DocW = tiles.W; p.DocH = tiles.H; p.Tx0 = tx0; p.Ty0 = ty0; p.LutW = (uint)(tx1 - tx0 + 1);
            int n = 0;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                    p.Lut[n++] = tx < tiles.TX && ty < tiles.TY ? Cache.Lookup(tiles, ty * tiles.TX + tx, stamp) : -1;
        }
        var m = g.Ctx.Map(cb, 0, MapMode.WriteDiscard);
        *(Params*)m.DataPointer = p;
        g.Ctx.Unmap(cb, 0);
        var ctx = g.Ctx;
        ctx.CSSetUnorderedAccessView(0, null);
        ctx.CSSetShader(g.CsComposite);
        ctx.CSSetConstantBuffer(0, cb);
        ctx.CSSetShaderResource(0, Cache.Srv);
        ctx.CSSetShaderResource(1, src?.Srv);
        ctx.CSSetShaderResource(2, progSrv);
        ctx.CSSetUnorderedAccessView(0, dst.Uav);
        ctx.Dispatch((uint)(C.W + 15) / 16, (uint)(C.H + 15) / 16, 1);
        Dispatches++;
    }

    void LayerOp(Buf dst, Surface s, uint op, Blend mode, float opacity) => Op(dst, op, mode, K(opacity), 0, null, s);
    void BufOp(Buf dst, Buf src, Blend mode, float opacity) => Op(dst, OP_BUF, mode, K(opacity), 0, src, null);
    void Fill(Buf dst, uint color) => Op(dst, OP_FILL, Blend.Normal, 255, color, null, null);

    Buf Get()
    {
        if (freeBufs.Count > 0) { var b0 = freeBufs[^1]; freeBufs.RemoveAt(freeBufs.Count - 1); return b0; }
        var t = g.Tex(MaxChunk, MaxChunk, Format.R32_UInt, BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        var b = new Buf { Tex = t, Srv = g.Dev.CreateShaderResourceView(t), Uav = g.Dev.CreateUnorderedAccessView(t) };
        allBufs.Add(b);
        return b;
    }
    void Put(Buf b) { if (b != null && !freeBufs.Contains(b)) freeBufs.Add(b); }
    void ReleaseAll() { freeBufs.Clear(); freeBufs.AddRange(allBufs); }

    Buf Clone(Buf b)
    {
        var c = Get();
        g.Ctx.CopySubresourceRegion(c.Tex, 0, 0, 0, 0, b.Tex, 0, new Box(0, 0, 0, C.W, C.H, 1));
        return c;
    }

    unsafe void Upload(Buf b, uint[] arr)
    {
        fixed (uint* p = arr)
            g.Ctx.UpdateSubresource(b.Tex, 0, new Box(0, 0, 0, C.W, C.H, 1), (IntPtr)p, (uint)(C.W * 4), (uint)(C.W * C.H * 4));
    }

    unsafe uint[] Download(Buf b)
    {
        staging ??= g.Tex(MaxChunk, MaxChunk, Format.R32_UInt, BindFlags.None, 1, ResourceUsage.Staging, CpuAccessFlags.Read);
        g.Ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, b.Tex, 0, new Box(0, 0, 0, C.W, C.H, 1));
        var m = g.Ctx.Map(staging, 0, MapMode.Read);
        var arr = new uint[C.W * C.H];
        try
        {
            fixed (uint* d = arr)
                for (int y = 0; y < C.H; y++)
                    Buffer.MemoryCopy((byte*)m.DataPointer + (long)y * m.RowPitch, d + y * C.W, C.W * 4, C.W * 4);
        }
        finally { g.Ctx.Unmap(staging, 0); }
        return arr;
    }

    public void Dispose()
    {
        foreach (var b in allBufs) { b.Uav?.Dispose(); b.Srv?.Dispose(); b.Tex?.Dispose(); }
        allBufs.Clear(); freeBufs.Clear();
        staging?.Dispose(); cb?.Dispose(); Cache?.Dispose();
    }
}
