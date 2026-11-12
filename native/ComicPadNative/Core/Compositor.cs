// 레이어 합성 — 웹 판(app/js/core/compositor.js)과 같은 규칙:
//  - 폴더: 자식들을 따로 합성한 뒤 폴더의 불투명도·합성 모드로 얹음 (애니 트랙 폴더는 Hooks.AnimFolder)
//  - 클리핑: 기준 레이어(바로 아래) 위에만, 표준이면 source-atop, 다른 모드면 기준 모양으로 자른 뒤 그 모드로
//  - 조정 레이어: 같은 단계에서 아래에 쌓인 그림 전체에 (클리핑이면 기준 레이어에만), 마스크·불투명도 반영
//  - 레이어 마스크(destination-in), 레이어 컬러(tint: 모양만 남기고 색 바꿈), 톤화·효과는 Hooks
// 영역(Rect) 단위로 그려서 바뀐 부분만 빨리 다시 합성할 수 있다.
using System.Text.Json.Nodes;

namespace ComicPadNative.Core;

public sealed class RenderOptions
{
    public bool Paper = true;
    public bool ExcludeDraft;
    public HashSet<Layer> Only;                       // 이 레이어들만 (구조 무시) — 채우기 참조 등
    public Dictionary<Layer, Surface> Override;       // 이 레이어는 이 면으로 대신 그림 (변형 미리보기 등)
    public int? Frame;                                // 애니 프레임 (없으면 문서의 현재 프레임)
    public bool Onion;                                // 어니언 스킨 (화면 표시용)
}

/// <summary>다른 모듈이 채우는 확장 지점 (없으면 기본 동작)</summary>
public static class Hooks
{
    /// <summary>조정 레이어 보정을 buf(영역 r) 에 적용. 기본: 아무것도 안 함</summary>
    public static Action<uint[], Rect, Layer, Document> Adjust = (_, _, _, _) => { };
    /// <summary>레이어 효과(테두리·그림자·광선)를 buf 에. 기본: 없음</summary>
    public static Action<uint[], Rect, Layer, Document> Fx = (_, _, _, _) => { };
    public static Func<Layer, bool> HasFx = l => l.Fx != null && (On(l.Fx, "stroke") || On(l.Fx, "shadow") || On(l.Fx, "glow"));
    /// <summary>톤화(망점): 레이어 그림 대신 쓸 면. 기본: 원본</summary>
    public static Func<Layer, Document, Surface> Tone = (l, _) => l.Px;
    /// <summary>용지를 buf 에 (색·질감). 기본: 단색</summary>
    public static Action<uint[], Rect, Document> Paper = (buf, r, d) => Array.Fill(buf, Px.FromHex(d.PaperColor, 0xFFFFFFFF));
    /// <summary>애니 트랙 폴더(meta.anim) 그리기: true 를 돌려주면 처리한 것. 기본: 보통 폴더처럼</summary>
    public static Func<Compositor, uint[], Rect, Layer, Document, RenderOptions, int, bool> AnimFolder = (_, _, _, _, _, _, _) => false;

    static bool On(JsonObject fx, string k) => fx[k] is JsonObject o && (bool?)o["on"] == true;
}

public sealed class Compositor
{
    Dictionary<string, List<Layer>> kids;
    Document doc;
    RenderOptions o;

    /// <summary>문서의 영역 r 을 합성해서 r.W*r.H 배열로 (미리 곱한 알파)</summary>
    public uint[] Render(Document d, Rect r, RenderOptions opt = null)
    {
        doc = d; o = opt ?? new RenderOptions();
        var buf = new uint[r.W * r.H];
        kids = new();
        foreach (var l in d.Layers) { var p = d.ParentOf(l) ?? ""; if (!kids.TryGetValue(p, out var list)) kids[p] = list = new(); list.Add(l); }
        List<Layer> roots = o.Only != null ? d.Layers.Where(l => o.Only.Contains(l) && !l.IsFolder).ToList() : kids.GetValueOrDefault("") ?? new();
        bool paper = o.Paper && d.PaperVisible;
        bool paperInLevel = paper && roots.Any(l => l.IsAdjust && !l.Clip && l.Visible);
        if (paper && !paperInLevel) Hooks.Paper(buf, r, d);
        RenderList(buf, r, roots, 0, paperInLevel);
        return buf;
    }

    public List<Layer> KidsOf(Layer f) => kids.GetValueOrDefault(f?.Id ?? "") ?? new();

    bool VisibleL(Layer l) => l.Visible && !(o.ExcludeDraft && l.Draft);

    bool Special(Layer l) => l.IsFolder || l.Tint != null || l.Tone != null || Hooks.HasFx(l) || (l.Mask != null && l.MaskOn) || (o.Override?.ContainsKey(l) ?? false);

    public void RenderList(uint[] buf, Rect r, List<Layer> L, int depth, bool paperInLevel = false)
    {
        bool hasAdjust = L.Any(l => l.IsAdjust && !l.Clip && VisibleL(l));
        var outb = buf;
        if (hasAdjust)
        {
            outb = new uint[buf.Length];
            if (depth == 0 && paperInLevel) Hooks.Paper(outb, r, doc);
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
            if (b.IsAdjust) { if (hasAdjust) ApplyAdjust(outb, r, b); continue; }
            if (clips.Count == 0 && !Special(b))
            {
                if (b.Px != null) Blit(outb, r, b.Px, mode, b.Opacity);
                continue;
            }
            var bx = RenderLayer(b, r, depth);
            if (clips.Count > 0)
            {
                uint[] mask = clips.Any(c => c.Blend != "source-over" && !c.IsAdjust) ? (uint[])bx.Clone() : null;
                foreach (var c in clips)
                {
                    if (c.IsAdjust) { ApplyAdjust(bx, r, c); continue; }
                    var cx = RenderLayer(c, r, depth);
                    if (c.Blend == "source-over") CompositeAll(bx, cx, Blend.Atop, c.Opacity);
                    else
                    {
                        CompositeAll(cx, mask, Blend.DestIn, 1, srcIsMask: true);
                        CompositeAll(bx, cx, Px.Parse(c.Blend), c.Opacity);
                    }
                }
            }
            CompositeAll(outb, bx, mode, b.Opacity);
        }
        if (hasAdjust) CompositeAll(buf, outb, Blend.Normal, 1);
    }

    /// <summary>레이어(또는 폴더) 하나를 영역 크기 배열로</summary>
    public uint[] RenderLayer(Layer l, Rect r, int depth, string tintOverride = null)
    {
        var x = new uint[r.W * r.H];
        if (l.IsFolder)
        {
            if (!(l.Meta?["anim"] is JsonNode && Hooks.AnimFolder(this, x, r, l, doc, o, depth)))
                RenderList(x, r, KidsOf(l), depth + 1);
        }
        else
        {
            Surface src = o.Override != null && o.Override.TryGetValue(l, out var ov) ? ov : l.Tone != null ? Hooks.Tone(l, doc) ?? l.Px : l.Px;
            src?.Read(r, x);
        }
        if (l.Mask != null && l.MaskOn)
        {
            var m = l.Mask.Read(r);
            for (int k = 0; k < x.Length; k++) if (x[k] != 0) x[k] = Px.Scale(x[k], m[k] >> 24);
        }
        string tint = tintOverride ?? l.Tint;
        if (tint != null)
        {
            uint tc = Px.FromHex(tint);
            for (int k = 0; k < x.Length; k++) { uint a = x[k] >> 24; if (a != 0) x[k] = Px.Scale(tc, a); }
        }
        if (Hooks.HasFx(l)) Hooks.Fx(x, r, l, doc);
        return x;
    }

    void ApplyAdjust(uint[] target, Rect r, Layer l)
    {
        var t = (uint[])target.Clone();
        Hooks.Adjust(t, r, l, doc);
        if (l.Mask != null && l.MaskOn)
        {
            var m = l.Mask.Read(r);
            for (int k = 0; k < t.Length; k++) t[k] = Px.Scale(t[k], m[k] >> 24);
        }
        CompositeAll(target, t, Blend.Atop, l.Opacity);
    }

    static void Blit(uint[] dst, Rect r, Surface s, Blend mode, float op)
    {
        // 타일 단위로 읽어 빈 타일은 건너뜀
        foreach (int ti in s.TilesIn(r))
        {
            var t = s.TileAt(ti);
            if (t == null) continue;
            var tr = s.TileRect(ti).Intersect(r);
            int ox = tr.X % Surface.TS, oy = tr.Y % Surface.TS;
            for (int y = 0; y < tr.H; y++)
                Px.CompositeRow(dst, (tr.Y - r.Y + y) * r.W + (tr.X - r.X), t, (oy + y) * Surface.TS + ox, tr.W, mode, op);
        }
    }

    static void CompositeAll(uint[] dst, uint[] src, Blend mode, float op, bool srcIsMask = false)
    {
        if (mode == Blend.DestIn)
        {
            for (int k = 0; k < dst.Length; k++) dst[k] = Px.Scale(dst[k], src[k] >> 24);
            return;
        }
        Px.CompositeRow(dst, 0, src, 0, dst.Length, mode, op);
    }

    /// <summary>문서 전체를 합친 그림 (내보내기용)</summary>
    public Surface Flatten(Document d, RenderOptions opt = null)
    {
        var s = new Surface(d.W, d.H);
        opt ??= new RenderOptions { ExcludeDraft = true };
        for (int ty = 0; ty < s.TY; ty++)
            for (int tx = 0; tx < s.TX; tx++)
            {
                var r = s.TileRect(ty * s.TX + tx);
                s.Write(r, Render(d, r, opt));
            }
        return s;
    }

    /// <summary>(x,y) 의 합성 색 (스포이트)</summary>
    public uint Sample(Document d, int x, int y, RenderOptions opt = null) => Render(d, new Rect(x, y, 1, 1), opt)[0];
}
