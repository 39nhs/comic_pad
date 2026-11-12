// 획 버퍼 → 레이어 합성 (웹 commitBuffer + 그리는 동안의 app.live 미리보기와 같은 계산).
// 결과 = 그리기 전 픽셀 위에 (버퍼 × 종이 결·디더 × 팔레트 맞춤 × 불투명도 × 선택 영역) 을
//        지우개 = destination-out, 투명 픽셀 잠금 = source-atop, 아니면 브러시 합성 모드로.
// 그리는 동안에도 매번 "그리기 전" 에서 다시 계산하므로 미리보기와 확정 결과가 같다.
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Brushes;

public sealed class StrokeApply
{
    public Surface Buf;              // 획 버퍼
    public Surface Target;           // 레이어 픽셀 (또는 마스크)
    public PixelEdit Edit;           // 있으면 "그리기 전" 은 Edit.BeforeTile (그리는 중), 없으면 Target 자체 (한 번에 확정)
    public bool Erase, AlphaLock;
    public Blend Mode = Blend.Normal;
    public double Opacity = 1;
    public Selection Sel;            // null = 자르지 않음
    public BrushPreset Post;         // 종이 결·디더 (null = 없음)
    public double Dpi = 350;
    public List<int[]> SnapPalette;  // 팔레트 고정 작품 (null = 끔)

    byte[] grain; double grainScale; string grainMode; double hardCut;
    bool ditherOn; double ditherLevel; int ditherN;
    readonly Dictionary<uint, uint> snapCache = new();

    void Prepare()
    {
        grain = null; ditherOn = false;
        var p = Post;
        if (p == null) return;
        if (p.Dither > 0 && p.Dither < 1 && p.Tip != "pixel") { ditherOn = true; ditherLevel = p.Dither; ditherN = p.DitherSize == 2 ? 2 : 4; }
        if (p.Texture != null && p.Texture != "none" && p.TextureStrength > 0)
        {
            grain = PaperGrain.Mask(p.Texture, p.TextureStrength);
            grainScale = (Dpi > 0 ? Dpi : 350) / 350 * (p.TextureScale > 0 ? p.TextureScale : 1);
            grainMode = p.TextureMode ?? "multiply";
            hardCut = 200 - p.TextureStrength * 60;
        }
    }

    /// <summary>버퍼 픽셀 한 칸의 뒤처리 (종이 결·디더·팔레트)</summary>
    uint PostPx(uint b, int x, int y)
    {
        if (ditherOn && !(ditherLevel > PixelArt.BayerAt(x, y, ditherN) - 1e-6)) return 0;
        if (grain != null)
        {
            if (grainMode == "subtract")
            {
                uint a = b >> 24; double gv = PaperGrain.Nearest(grain, grainScale, x, y);
                double na = Math.Max(0, a - (255 - gv) * 1.4);
                b = na <= 0 ? 0 : Px.Scale(b, (uint)Math.Round(na * 255 / a));
            }
            else if (grainMode == "hard") { if (PaperGrain.Nearest(grain, grainScale, x, y) < hardCut) return 0; }
            else b = Px.Scale(b, (uint)Math.Round(PaperGrain.Bilinear(grain, grainScale, x, y)));
            if (b == 0) return 0;
        }
        if (SnapPalette != null && SnapPalette.Count > 0)
        {
            if ((b >> 24) < 128) return 0;
            uint u = Px.Unpremul(b) & 0xFFFFFF;
            if (!snapCache.TryGetValue(u, out var s))
            {
                var c = SnapPalette[PixelArt.NearestIndex((int)(u >> 16), (int)((u >> 8) & 255), (int)(u & 255), SnapPalette)];
                snapCache[u] = s = Px.Pack(255, (uint)c[0], (uint)c[1], (uint)c[2]);
            }
            b = s;
        }
        return b;
    }

    /// <summary>한 픽셀의 결과</summary>
    uint Final(uint before, uint b, int x, int y, int W)
    {
        if (b == 0) return before;
        if (Post != null || SnapPalette != null) { b = PostPx(b, x, y); if (b == 0) return before; }
        double k = Opacity;
        if (Sel?.Mask != null) k *= Sel.Mask[y * W + x] / 255.0;
        if (k <= 0) return before;
        uint src = k >= 1 ? b : Px.Scale(b, (uint)(k * 255 + 0.5));
        if (src == 0) return before;
        if (Erase) return Px.Scale(before, 255 - (src >> 24));
        if (AlphaLock) return Px.Composite(src, before, Blend.Atop);
        return Mode == Blend.Normal ? Px.Over(src, before) : Px.Composite(src, before, Mode);
    }

    /// <summary>영역 r 을 다시 합성. Edit 가 있으면 그 전에 Touch 해 둬야 함</summary>
    public void Apply(Rect r)
    {
        r = r.Clip(Target.W, Target.H);
        if (r.IsEmpty) return;
        Prepare();
        const int TS = Surface.TS;
        int W = Target.W;
        foreach (int ti in Target.TilesIn(r))
        {
            int tx = ti % Target.TX, ty = ti / Target.TX;
            var tr = Target.TileRect(ti).Intersect(r);
            var bt = Edit != null ? Edit.BeforeTile(ti) : Target.TileAt(ti);
            var bb = Buf.TileAt(ti);
            var cur = Target.TileAt(ti);
            if (bb == null)
            {
                if (Edit == null) continue;
                // 버퍼가 비었음 = 그리기 전 그대로 (직선 다시 그리기 등으로 지운 곳)
                if (cur == null || ReferenceEquals(cur, bt)) continue;
                for (int y = tr.Y; y < tr.B; y++)
                {
                    int li = (y - ty * TS) * TS + tr.X - tx * TS;
                    if (bt == null) Array.Clear(cur, li, tr.W); else Array.Copy(bt, li, cur, li, tr.W);
                }
                continue;
            }
            if (bt == null && (Erase || AlphaLock) && cur == null) continue;      // 투명한 곳: 지울 것·칠할 것 없음
            var t = cur ?? Target.Tile(tx, ty, true);
            for (int y = tr.Y; y < tr.B; y++)
            {
                int row = (y - ty * TS) * TS - tx * TS;
                for (int x = tr.X; x < tr.R; x++)
                {
                    int li = row + x;
                    uint before = bt == null ? 0 : bt[li];
                    t[li] = Final(before, bb[li], x, y, W);
                }
            }
        }
    }
}
