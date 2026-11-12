// 픽셀 면: 256x256 타일로 나눠 그림이 있는 타일만 메모리를 쓴다 (큰 원고·레이어 많은 작품에서 메모리 절약).
// 픽셀은 BGRA "미리 곱한 알파"(premultiplied) uint: a<<24 | r<<16 | g<<8 | b, 색 성분은 이미 알파를 곱한 값.
// 모든 모듈이 이 API 로 픽셀을 다룬다: Get/Set (편하지만 느림), Tile(직접 배열, 빠름), Read/Write(영역을 배열로).
using System.Runtime.CompilerServices;

namespace ComicPadNative.Core;

public sealed class Surface
{
    public const int TS = 256;                    // 타일 한 변
    public readonly int W, H, TX, TY;             // 크기, 타일 개수
    readonly uint[][] tiles;                      // null = 전부 투명

    public Surface(int w, int h)
    {
        W = Math.Max(1, w); H = Math.Max(1, h);
        TX = (W + TS - 1) / TS; TY = (H + TS - 1) / TS;
        tiles = new uint[TX * TY][];
    }

    public Rect Full => new(0, 0, W, H);

    /// <summary>타일 배열 (TS*TS, 행 우선). create=false 이고 비어 있으면 null</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint[] Tile(int tx, int ty, bool create)
    {
        int i = ty * TX + tx;
        var t = tiles[i];
        if (t == null && create) tiles[i] = t = new uint[TS * TS];
        return t;
    }
    public uint[] TileAt(int index) => tiles[index];
    public void SetTile(int tx, int ty, uint[] t) => tiles[ty * TX + tx] = t;
    public int TileCount => tiles.Length;
    public bool HasTile(int tx, int ty) => tiles[ty * TX + tx] != null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Get(int x, int y)
    {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H) return 0;
        var t = tiles[(y / TS) * TX + x / TS];
        return t == null ? 0 : t[(y % TS) * TS + x % TS];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int x, int y, uint v)
    {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
        int i = (y / TS) * TX + x / TS;
        var t = tiles[i];
        if (t == null) { if (v == 0) return; tiles[i] = t = new uint[TS * TS]; }
        t[(y % TS) * TS + x % TS] = v;
    }

    /// <summary>영역 픽셀을 배열로 (행 우선, r.W*r.H). 문서 밖은 0</summary>
    public uint[] Read(Rect r, uint[] dst = null)
    {
        dst ??= new uint[r.W * r.H];
        for (int ty = Math.Max(0, r.Y / TS); ty <= Math.Min(TY - 1, (r.B - 1) / TS); ty++)
            for (int tx = Math.Max(0, r.X / TS); tx <= Math.Min(TX - 1, (r.R - 1) / TS); tx++)
            {
                var t = tiles[ty * TX + tx];
                int x0 = Math.Max(r.X, tx * TS), x1 = Math.Min(Math.Min(r.R, W), tx * TS + TS);
                int y0 = Math.Max(r.Y, ty * TS), y1 = Math.Min(Math.Min(r.B, H), ty * TS + TS);
                for (int y = y0; y < y1; y++)
                {
                    int di = (y - r.Y) * r.W + (x0 - r.X);
                    if (t == null) Array.Clear(dst, di, x1 - x0);
                    else Array.Copy(t, (y - ty * TS) * TS + (x0 - tx * TS), dst, di, x1 - x0);
                }
            }
        return dst;
    }

    /// <summary>배열(행 우선, r.W*r.H)을 영역에 쓴다. 전부 0 인 타일은 비운다</summary>
    public void Write(Rect r, uint[] src)
    {
        for (int ty = Math.Max(0, r.Y / TS); ty <= Math.Min(TY - 1, (r.B - 1) / TS); ty++)
            for (int tx = Math.Max(0, r.X / TS); tx <= Math.Min(TX - 1, (r.R - 1) / TS); tx++)
            {
                int x0 = Math.Max(r.X, tx * TS), x1 = Math.Min(Math.Min(r.R, W), tx * TS + TS);
                int y0 = Math.Max(r.Y, ty * TS), y1 = Math.Min(Math.Min(r.B, H), ty * TS + TS);
                var t = tiles[ty * TX + tx];
                if (t == null)
                {
                    bool any = false;
                    for (int y = y0; y < y1 && !any; y++) { int si = (y - r.Y) * r.W + (x0 - r.X); for (int k = 0; k < x1 - x0; k++) if (src[si + k] != 0) { any = true; break; } }
                    if (!any) continue;
                    tiles[ty * TX + tx] = t = new uint[TS * TS];
                }
                for (int y = y0; y < y1; y++) Array.Copy(src, (y - r.Y) * r.W + (x0 - r.X), t, (y - ty * TS) * TS + (x0 - tx * TS), x1 - x0);
            }
    }

    public void Clear() => Array.Clear(tiles);
    public void Clear(Rect r) { r = r.Clip(W, H); if (r.IsEmpty) return; Write(r, new uint[r.W * r.H]); }

    public Surface Clone()
    {
        var s = new Surface(W, H);
        for (int i = 0; i < tiles.Length; i++) if (tiles[i] != null) s.tiles[i] = (uint[])tiles[i].Clone();
        return s;
    }

    public void CopyFrom(Surface o)
    {
        if (o.W != W || o.H != H) throw new ArgumentException("크기가 다른 면");
        for (int i = 0; i < tiles.Length; i++) tiles[i] = o.tiles[i] == null ? null : (uint[])o.tiles[i].Clone();
    }

    /// <summary>투명이 아닌 픽셀이 있는 범위 (없으면 Empty)</summary>
    public Rect Bounds()
    {
        int x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int ty = 0; ty < TY; ty++)
            for (int tx = 0; tx < TX; tx++)
            {
                var t = tiles[ty * TX + tx];
                if (t == null) continue;
                int bw = Math.Min(TS, W - tx * TS), bh = Math.Min(TS, H - ty * TS);
                for (int y = 0; y < bh; y++)
                {
                    int row = y * TS;
                    for (int x = 0; x < bw; x++)
                        if ((t[row + x] >> 24) != 0)
                        {
                            int gx = tx * TS + x, gy = ty * TS + y;
                            if (gx < x0) x0 = gx; if (gx > x1) x1 = gx; if (gy < y0) y0 = gy; if (gy > y1) y1 = gy;
                        }
                }
            }
        return x1 < 0 ? Rect.Empty : Rect.FromLTRB(x0, y0, x1 + 1, y1 + 1);
    }

    /// <summary>빈 타일(전부 0) 정리 — 메모리 돌려주기</summary>
    public void Compact()
    {
        for (int i = 0; i < tiles.Length; i++)
        {
            var t = tiles[i];
            if (t == null) continue;
            bool any = false;
            foreach (var v in t) if (v != 0) { any = true; break; }
            if (!any) tiles[i] = null;
        }
    }

    public long MemoryBytes { get { long n = 0; foreach (var t in tiles) if (t != null) n += t.Length * 4L; return n; } }

    /// <summary>r 이 걸치는 타일 번호들</summary>
    public IEnumerable<int> TilesIn(Rect r)
    {
        r = r.Clip(W, H);
        if (r.IsEmpty) yield break;
        for (int ty = r.Y / TS; ty <= (r.B - 1) / TS; ty++)
            for (int tx = r.X / TS; tx <= (r.R - 1) / TS; tx++) yield return ty * TX + tx;
    }
    public Rect TileRect(int index) { int tx = index % TX, ty = index / TX; return new Rect(tx * TS, ty * TS, Math.Min(TS, W - tx * TS), Math.Min(TS, H - ty * TS)); }
}

public readonly record struct Rect(int X, int Y, int W, int H)
{
    public static readonly Rect Empty = new(0, 0, 0, 0);
    public bool IsEmpty => W <= 0 || H <= 0;
    public int R => X + W;
    public int B => Y + H;
    public Rect Union(Rect o) => IsEmpty ? o : o.IsEmpty ? this : FromLTRB(Math.Min(X, o.X), Math.Min(Y, o.Y), Math.Max(R, o.R), Math.Max(B, o.B));
    public Rect Intersect(Rect o) => FromLTRB(Math.Max(X, o.X), Math.Max(Y, o.Y), Math.Min(R, o.R), Math.Min(B, o.B));
    public Rect Clip(int w, int h) => FromLTRB(Math.Max(0, X), Math.Max(0, Y), Math.Min(w, R), Math.Min(h, B));
    public Rect Inflate(int d) => IsEmpty ? this : new(X - d, Y - d, W + 2 * d, H + 2 * d);
    public bool Contains(int x, int y) => x >= X && y >= Y && x < R && y < B;
    public static Rect FromLTRB(int l, int t, int r, int b) => r <= l || b <= t ? Empty : new(l, t, r - l, b - t);
    public static Rect FromPoints(double x0, double y0, double x1, double y1, double pad = 0) =>
        FromLTRB((int)Math.Floor(Math.Min(x0, x1) - pad), (int)Math.Floor(Math.Min(y0, y1) - pad), (int)Math.Ceiling(Math.Max(x0, x1) + pad), (int)Math.Ceiling(Math.Max(y0, y1) + pad));
}
