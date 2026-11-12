// 선택 영역: 문서 크기 0~255 마스크 (없으면 전체). 웹 판 core/selection.js 와 같은 뜻.
namespace ComicPadNative.Core;

public sealed class Selection
{
    public readonly int W, H;
    public byte[] Mask;                    // null = 선택 없음 (전체에 그림)
    public Selection(int w, int h) { W = w; H = h; }
    public bool Active => Mask != null;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public byte At(int x, int y) => Mask == null ? (byte)255 : (uint)x < (uint)W && (uint)y < (uint)H ? Mask[y * W + x] : (byte)0;

    public void None() => Mask = null;
    public void All() { Mask = new byte[W * H]; Array.Fill(Mask, (byte)255); }
    public void Invert() { if (Mask == null) { Mask = new byte[W * H]; return; } for (int i = 0; i < Mask.Length; i++) Mask[i] = (byte)(255 - Mask[i]); }
    public byte[] Snapshot() => (byte[])Mask?.Clone();
    public void Restore(byte[] m) => Mask = (byte[])m?.Clone();

    /// <summary>모드: replace | add | subtract | intersect</summary>
    public void Apply(byte[] shape, string mode)
    {
        if (mode == "replace" || Mask == null && mode != "subtract" && mode != "intersect") { Mask = shape; return; }
        if (Mask == null) { if (mode == "subtract") { All(); } else { Mask = shape; return; } }
        for (int i = 0; i < Mask.Length; i++)
            Mask[i] = mode switch
            {
                "add" => Math.Max(Mask[i], shape[i]),
                "subtract" => (byte)(Mask[i] * (255 - shape[i]) / 255),
                "intersect" => (byte)(Mask[i] * shape[i] / 255),
                _ => shape[i],
            };
    }

    public Rect Bounds()
    {
        if (Mask == null) return new Rect(0, 0, W, H);
        int x0 = W, y0 = H, x1 = -1, y1 = -1;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (Mask[y * W + x] != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; y1 = y; }
        return x1 < 0 ? Rect.Empty : Rect.FromLTRB(x0, y0, x1 + 1, y1 + 1);
    }
}
