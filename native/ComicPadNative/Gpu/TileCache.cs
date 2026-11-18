// GPU 타일 저장소: 레이어·마스크 면(Surface)의 256×256 타일을 그래픽 메모리의 조각 배열(Texture2DArray, R32_UINT)에 올려 둠.
//  - 바뀐 타일만 다시 올림: 합성할 영역에 걸친 타일의 내용 지문(해시)을 CPU 에서 빠르게 계산해 지난번과 다르면 올림
//    (붓질은 타일 배열을 제자리에서 고치므로 "배열이 같은가" 로는 알 수 없음)
//    지문은 타일을 16줄씩 16 띠로 나눠 띠마다: 다시 합성할 영역(=바뀐 영역, App.Invalidate 규칙)에 걸친 띠만 다시 계산하면 됨.
//    올릴 때는 16 띠를 모두 다시 계산 → 저장한 지문은 늘 GPU 에 있는 내용과 같음
//  - 칸이 모자라면 가장 오래 안 쓴 칸을 비움 (지금 조각에서 쓰는 칸은 빼고)
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using ComicPadNative.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ComicPadNative.Gpu;

public sealed class TileCache : IDisposable
{
    public const int TS = Surface.TS;
    readonly GpuDevice g;
    public readonly int Capacity;
    public ID3D11Texture2D Array;
    public ID3D11ShaderResourceView Srv;

    sealed class Entry
    {
        public int[] Slot;
        public Vector256<uint>[] Hash;      // 타일 i 의 띠 j = [i * Bands + j]
        public long[] Verified;     // 이 번호의 합성에서 지문을 확인함
        public long[] Full;         // 이 번호의 합성에서 16 띠 모두 계산함 (올릴 때 다시 계산 안 함)
        public bool[] Stale;        // 칸 내용이 CPU 와 다름 → 쓸 때 올림
        public Entry(int n) { Slot = new int[n]; System.Array.Fill(Slot, -1); Hash = new Vector256<uint>[n * Bands]; Verified = new long[n]; Full = new long[n]; Stale = new bool[n]; }
    }
    readonly ConditionalWeakTable<Surface, Entry> map = new();
    readonly Entry[] owner;
    readonly int[] ownerTile;
    readonly long[] lastUse;
    readonly Stack<int> free = new();

    public long Pass;                     // 합성 한 번 = 번호 하나
    public long Uploads, Hashed, UploadBytes;

    public TileCache(GpuDevice g, int capacity)
    {
        this.g = g; Capacity = capacity;
        Array = g.Tex(TS, TS, Format.R32_UInt, BindFlags.ShaderResource, capacity);
        Srv = g.Dev.CreateShaderResourceView(Array);
        owner = new Entry[capacity]; ownerTile = new int[capacity]; lastUse = new long[capacity];
        for (int i = capacity - 1; i >= 0; i--) free.Push(i);
    }

    Entry E(Surface s) => map.GetValue(s, x => new Entry(x.TileCount));

    public const int Bands = 16, BandRows = TS / Bands;

    /// <summary>타일 띠 하나의 내용 지문 (8갈래 곱하기·섞기, 한 값만 바뀌어도 반드시 달라짐)</summary>
    public static Vector256<uint> HashBand(uint[] t, int band)
    {
        var v = MemoryMarshal.Cast<uint, Vector256<uint>>(t.AsSpan(band * BandRows * TS, BandRows * TS));
        var k = Vector256.Create(0x9E3779B1u);
        Vector256<uint> h0 = Vector256.Create(0x85EBCA77u), h1 = Vector256.Create(0xC2B2AE3Du);
        int i = 0;
        for (; i + 1 < v.Length; i += 2) { h0 = (h0 ^ v[i]) * k; h1 = (h1 ^ v[i + 1]) * k; }
        for (; i < v.Length; i++) h0 = (h0 ^ v[i]) * k;
        return h0 ^ (h1 * Vector256.Create(0x27D4EB2Fu));
    }

    /// <summary>합성 시작: 영역 r 에 걸친 (면, 타일) 의 지문(r 이 걸친 띠만)을 한꺼번에(여러 코어) 확인</summary>
    public void BeginPass(IEnumerable<Surface> surfaces, Rect r)
    {
        Pass++;
        var work = new List<(Entry e, uint[] t, int ti, int b0, int b1)>();
        foreach (var s in surfaces)
        {
            if (s == null) continue;
            var e = E(s);
            foreach (int ti in s.TilesIn(r))
            {
                var t = s.TileAt(ti);
                if (t == null) { Release(e, ti); continue; }
                if (e.Verified[ti] == Pass) continue;
                e.Verified[ti] = Pass;
                if (e.Slot[ti] < 0) { e.Stale[ti] = true; e.Full[ti] = Pass; work.Add((e, t, ti, 0, Bands - 1)); continue; }   // GPU 에 없음 → 올릴 것: 지문 전부 미리 (여러 코어)
                var tr = s.TileRect(ti);
                int y0 = Math.Max(r.Y, tr.Y) - tr.Y, y1 = Math.Min(r.B, tr.B) - tr.Y;
                work.Add((e, t, ti, y0 / BandRows, (y1 - 1) / BandRows));
            }
        }
        void One(int i)
        {
            var (e, t, ti, b0, b1) = work[i];
            bool changed = false;
            for (int b = b0; b <= b1; b++)
            {
                var h = HashBand(t, b);
                if (h != e.Hash[ti * Bands + b]) { e.Hash[ti * Bands + b] = h; changed = true; }
            }
            if (changed) e.Stale[ti] = true;
        }
        foreach (var w in work) Hashed += w.b1 - w.b0 + 1;
        if (work.Count >= 8) Parallel.For(0, work.Count, One);
        else for (int i = 0; i < work.Count; i++) One(i);
    }

    void Release(Entry e, int ti)
    {
        int s = e.Slot[ti];
        if (s < 0) return;
        e.Slot[ti] = -1; owner[s] = null; free.Push(s);
    }

    /// <summary>타일의 칸 번호 (없으면 -1). stamp = 지금 조각 번호 (이 조각에서 쓰는 칸은 비우지 않음)</summary>
    public unsafe int Lookup(Surface s, int ti, long stamp)
    {
        var t = s.TileAt(ti);
        var e = E(s);
        if (t == null) { Release(e, ti); return -1; }
        if (e.Verified[ti] != Pass)
        {
            // BeginPass 목록에 없던 면: 타일 전체 지문으로 확인
            e.Verified[ti] = Pass;
            if (e.Slot[ti] < 0) e.Stale[ti] = true;
            else for (int b = 0; b < Bands; b++) { var h = HashBand(t, b); Hashed++; if (h != e.Hash[ti * Bands + b]) { e.Hash[ti * Bands + b] = h; e.Stale[ti] = true; } }
        }
        int slot = e.Slot[ti];
        if (slot >= 0 && !e.Stale[ti]) { lastUse[slot] = stamp; return slot; }
        if (slot < 0)
        {
            slot = Alloc(stamp);
            if (slot < 0) throw new GpuFullException();
            e.Slot[ti] = slot; owner[slot] = e; ownerTile[slot] = ti;
        }
        lastUse[slot] = stamp;
        // 올리는 내용의 지문 (BeginPass 가 이번에 16 띠를 다 계산했으면 그대로)
        if (e.Full[ti] != Pass) for (int b = 0; b < Bands; b++) e.Hash[ti * Bands + b] = HashBand(t, b);
        e.Full[ti] = 0;
        fixed (uint* p = t)
            g.Ctx.UpdateSubresource(Array, (uint)slot, null, (IntPtr)p, TS * 4, TS * TS * 4);
        e.Stale[ti] = false;
        Uploads++; UploadBytes += TS * TS * 4;
        return slot;
    }

    int Alloc(long stamp)
    {
        if (free.Count > 0) return free.Pop();
        int best = -1; long bu = long.MaxValue;
        for (int i = 0; i < Capacity; i++) if (lastUse[i] != stamp && lastUse[i] < bu) { bu = lastUse[i]; best = i; }
        if (best < 0) return -1;
        var o = owner[best];
        if (o != null) { o.Slot[ownerTile[best]] = -1; o.Stale[ownerTile[best]] = true; }
        owner[best] = null;
        return best;
    }

    /// <summary>모든 칸 비우기 (장치 다시 만들 때 등)</summary>
    public void Clear()
    {
        for (int i = 0; i < Capacity; i++) { var o = owner[i]; if (o != null) { o.Slot[ownerTile[i]] = -1; o.Stale[ownerTile[i]] = true; } owner[i] = null; }
        free.Clear(); for (int i = Capacity - 1; i >= 0; i--) free.Push(i);
    }

    public int Used => Capacity - free.Count;

    public void Dispose() { Srv?.Dispose(); Array?.Dispose(); Srv = null; Array = null; }
}

/// <summary>한 조각에 필요한 타일이 저장소보다 많음 → 조각을 줄여 다시</summary>
public sealed class GpuFullException : Exception { public GpuFullException() : base("GPU 타일 칸 모자람") { } }
