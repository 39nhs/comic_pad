// 문서: 용지 + 잉크 레이어 1장 (시제품). 픽셀은 BGRA 미리 곱한 알파(premultiplied) uint.
// 실행취소는 64x64 타일 단위로 "그리기 전" 을 보관.
namespace ComicPadNative;

public sealed class Doc
{
    public const int T = 64;                       // 타일 크기
    public readonly int W, H, TW, TH;
    public readonly uint[] Ink;                    // 잉크 (투명 바탕)
    public uint Paper = 0xFFFFFFFF;                // 흰 종이

    readonly List<Dictionary<int, uint[]>> undo = new(), redo = new();

    public Doc(int w, int h)
    {
        W = w; H = h; TW = (w + T - 1) / T; TH = (h + T - 1) / T;
        Ink = new uint[w * h];
    }

    public uint[] CopyTile(int ti)
    {
        int tx = ti % TW * T, ty = ti / TW * T, w = Math.Min(T, W - tx), h = Math.Min(T, H - ty);
        var t = new uint[T * T];
        for (int y = 0; y < h; y++) Array.Copy(Ink, (ty + y) * W + tx, t, y * T, w);
        return t;
    }

    void PutTile(int ti, uint[] t)
    {
        int tx = ti % TW * T, ty = ti / TW * T, w = Math.Min(T, W - tx), h = Math.Min(T, H - ty);
        for (int y = 0; y < h; y++) Array.Copy(t, y * T, Ink, (ty + y) * W + tx, w);
    }

    public Rect TileRect(int ti) { int tx = ti % TW * T, ty = ti / TW * T; return new Rect(tx, ty, Math.Min(T, W - tx), Math.Min(T, H - ty)); }

    public void PushUndo(Dictionary<int, uint[]> before)
    {
        if (before.Count == 0) return;
        undo.Add(before); redo.Clear();
        if (undo.Count > 200) undo.RemoveAt(0);
    }

    /// <returns>바뀐 영역 (없으면 Empty)</returns>
    public Rect Undo() => Swap(undo, redo);
    public Rect Redo() => Swap(redo, undo);

    Rect Swap(List<Dictionary<int, uint[]>> from, List<Dictionary<int, uint[]>> to)
    {
        if (from.Count == 0) return Rect.Empty;
        var rec = from[^1]; from.RemoveAt(from.Count - 1);
        var back = new Dictionary<int, uint[]>();
        Rect dirty = Rect.Empty;
        foreach (var (ti, t) in rec) { back[ti] = CopyTile(ti); PutTile(ti, t); dirty = dirty.Union(TileRect(ti)); }
        to.Add(back);
        return dirty;
    }

    /// <summary>용지 위에 잉크를 얹은 색 (불투명 BGRA)</summary>
    public uint Composite(int i)
    {
        uint c = Ink[i];
        uint a = c >> 24;
        if (a == 255) return c;
        if (a == 0) return Paper;
        uint ia = 255 - a, p = Paper;
        uint b = (c & 0xFF) + ((p & 0xFF) * ia + 127) / 255;
        uint g = ((c >> 8) & 0xFF) + (((p >> 8) & 0xFF) * ia + 127) / 255;
        uint r = ((c >> 16) & 0xFF) + (((p >> 16) & 0xFF) * ia + 127) / 255;
        return 0xFF000000 | (r << 16) | (g << 8) | b;
    }
}

public readonly record struct Rect(int X, int Y, int W, int H)
{
    public static readonly Rect Empty = new(0, 0, 0, 0);
    public bool IsEmpty => W <= 0 || H <= 0;
    public int R => X + W;
    public int B => Y + H;
    public Rect Union(Rect o) => IsEmpty ? o : o.IsEmpty ? this : FromLTRB(Math.Min(X, o.X), Math.Min(Y, o.Y), Math.Max(R, o.R), Math.Max(B, o.B));
    public Rect Clip(int w, int h) => FromLTRB(Math.Max(0, X), Math.Max(0, Y), Math.Min(w, R), Math.Min(h, B));
    public Rect Inflate(int d) => IsEmpty ? this : new(X - d, Y - d, W + 2 * d, H + 2 * d);
    public static Rect FromLTRB(int l, int t, int r, int b) => r <= l || b <= t ? Empty : new(l, t, r - l, b - t);
}
