// 실행취소/다시실행.
//  - 픽셀: 그리기 전에 Touch(영역) 로 걸치는 타일의 "전" 을 보관 → Commit 때 "후" 를 보관 (바뀐 타일만)
//  - 구조: Struct(이름, 바꾸기) — 레이어 목록·속성 전후를 통째로 기억 (픽셀은 참조만)
//  - 선택 영역: Selection(이름, 바꾸기)
namespace ComicPadNative.Core;

public interface IEdit
{
    string Label { get; }
    long Bytes { get; }
    Rect Undo(Document d);   // 바뀐 문서 영역 (전체면 Full)
    Rect Redo(Document d);
}

public sealed class History
{
    readonly List<IEdit> undo = new(), redo = new();
    public long MaxBytes = 1_500_000_000;   // 약 1.5GB 넘으면 오래된 것부터 버림
    public event Action Changed;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string UndoLabel => undo.Count > 0 ? undo[^1].Label : null;
    public string RedoLabel => redo.Count > 0 ? redo[^1].Label : null;

    public void Push(IEdit e)
    {
        if (e == null) return;
        undo.Add(e); redo.Clear();
        long total = undo.Sum(x => x.Bytes);
        while (undo.Count > 1 && (total > MaxBytes || undo.Count > 300)) { total -= undo[0].Bytes; undo.RemoveAt(0); }
        Changed?.Invoke();
    }
    public Rect Undo(Document d) { if (undo.Count == 0) return Rect.Empty; var e = undo[^1]; undo.RemoveAt(undo.Count - 1); var r = e.Undo(d); redo.Add(e); d.Modified = true; Changed?.Invoke(); return r; }
    public Rect Redo(Document d) { if (redo.Count == 0) return Rect.Empty; var e = redo[^1]; redo.RemoveAt(redo.Count - 1); var r = e.Redo(d); undo.Add(e); d.Modified = true; Changed?.Invoke(); return r; }
    public void Clear() { undo.Clear(); redo.Clear(); Changed?.Invoke(); }

    // ---- 편하게 쓰는 길 -------------------------------------------------------------
    public PixelEdit BeginPixels(Layer l, string label = "그리기") => new(this, l, l.Target, label);

    /// <summary>레이어 목록·속성 바꾸기를 실행취소 가능하게</summary>
    public void Struct(Document d, string label, Action change)
    {
        var before = StructSnap.Take(d);
        change();
        d.Modified = true;
        Push(new StructEdit(label, before, StructSnap.Take(d)));
    }

    public void SelectionChange(Document d, string label, Action change)
    {
        var before = d.Sel.Snapshot();
        change();
        Push(new SelEdit(label, before, d.Sel.Snapshot()));
    }

    /// <summary>여러 편집을 하나로 (병합 = 픽셀 + 구조)</summary>
    public void PushGroup(string label, params IEdit[] edits) => Push(new GroupEdit(label, edits.Where(e => e != null).ToArray()));
}

/// <summary>픽셀 편집 기록. 그리기 전에 Touch, 끝나면 Commit</summary>
public sealed class PixelEdit : IEdit
{
    readonly History h;
    readonly Layer layer;
    readonly Surface s;
    readonly Dictionary<int, uint[]> before = new(), after = new();
    public string Label { get; private set; }
    public Rect Dirty = Rect.Empty;
    bool done;

    public PixelEdit(History h, Layer l, Surface s, string label) { this.h = h; layer = l; this.s = s; Label = label; }

    /// <summary>이 영역을 바꾸기 전에 부름 (처음 걸치는 타일만 복사)</summary>
    public void Touch(Rect r)
    {
        r = r.Clip(s.W, s.H);
        if (r.IsEmpty) return;
        Dirty = Dirty.Union(r);
        foreach (int ti in s.TilesIn(r))
            if (!before.ContainsKey(ti)) before[ti] = (uint[])s.TileAt(ti)?.Clone();
    }

    /// <summary>"전" 타일 (그리기 전 상태, null = 투명). 브러시가 전 상태를 기준으로 계산할 때</summary>
    public uint[] BeforeTile(int ti) => before.TryGetValue(ti, out var t) ? t : s.TileAt(ti);

    public Surface Target => s;

    public void Commit(string label = null) { if (Finish(label) != null) h.Push(this); }

    /// <summary>기록을 마치되 쌓지는 않음 (여러 편집을 GroupEdit 로 묶을 때). 바뀐 게 없으면 null</summary>
    public PixelEdit Finish(string label = null)
    {
        if (done) return null;
        done = true;
        if (label != null) Label = label;
        foreach (var ti in before.Keys.ToList())
        {
            var now = s.TileAt(ti);
            if (Same(before[ti], now)) { before.Remove(ti); continue; }
            after[ti] = (uint[])now?.Clone();
        }
        if (before.Count == 0) return null;
        layer.Touch();
        return this;
    }

    public void Cancel()
    {
        foreach (var (ti, t) in before) s.SetTile(ti % s.TX, ti / s.TX, (uint[])t?.Clone());
        done = true;
    }

    static bool Same(uint[] a, uint[] b)
    {
        if (a == null || b == null) return a == b || IsZero(a ?? b);
        return a.AsSpan().SequenceEqual(b);
    }
    static bool IsZero(uint[] t) { foreach (var v in t) if (v != 0) return false; return true; }

    public long Bytes => (before.Count + after.Count) * Surface.TS * Surface.TS * 4L;
    public Rect Undo(Document d) => Put(before);
    public Rect Redo(Document d) => Put(after);
    Rect Put(Dictionary<int, uint[]> m)
    {
        var r = Rect.Empty;
        foreach (var (ti, t) in m) { s.SetTile(ti % s.TX, ti / s.TX, (uint[])t?.Clone()); r = r.Union(s.TileRect(ti)); }
        layer.Touch();
        return r;
    }
}

/// <summary>레이어 목록·속성 스냅숏 (픽셀은 참조만 — 픽셀이 바뀌는 편집은 PixelEdit 를 함께)</summary>
public sealed class StructSnap
{
    public List<(Layer l, Layer copy)> Items;
    public string Active;
    public static StructSnap Take(Document d) => new() { Items = d.Layers.Select(l => (l, Shallow(l))).ToList(), Active = d.ActiveId };
    static Layer Shallow(Layer l) => l.PropsCopy();
    public void Apply(Document d)
    {
        d.Layers.Clear();
        foreach (var (l, c) in Items)
        {
            // 원래 객체에 속성을 되돌려 놓음 (다른 곳에서 들고 있는 참조가 그대로 맞도록)
            l.Name = c.Name; l.Kind = c.Kind; l.Visible = c.Visible; l.Opacity = c.Opacity; l.Blend = c.Blend; l.Locked = c.Locked;
            l.AlphaLock = c.AlphaLock; l.Clip = c.Clip; l.Draft = c.Draft; l.Ref = c.Ref; l.Rough = c.Rough; l.Tint = c.Tint; l.MaskOn = c.MaskOn;
            l.Parent = c.Parent; l.Expanded = c.Expanded; l.Meta = (System.Text.Json.Nodes.JsonObject)c.Meta?.DeepClone(); l.Tone = (System.Text.Json.Nodes.JsonObject)c.Tone?.DeepClone();
            l.Fx = (System.Text.Json.Nodes.JsonObject)c.Fx?.DeepClone(); l.Extra = (System.Text.Json.Nodes.JsonObject)c.Extra.DeepClone();
            l.Px = c.Px; l.Mask = c.Mask; l.Touch();
            d.Layers.Add(l);
        }
        d.ActiveId = Active;
    }
}

public sealed class StructEdit : IEdit
{
    readonly StructSnap before, after;
    public string Label { get; }
    public StructEdit(string label, StructSnap b, StructSnap a) { Label = label; before = b; after = a; }
    public long Bytes => 4096;
    public Rect Undo(Document d) { before.Apply(d); return new Rect(0, 0, d.W, d.H); }
    public Rect Redo(Document d) { after.Apply(d); return new Rect(0, 0, d.W, d.H); }
}

public sealed class SelEdit : IEdit
{
    readonly byte[] before, after;
    public string Label { get; }
    public SelEdit(string label, byte[] b, byte[] a) { Label = label; before = b; after = a; }
    public long Bytes => (before?.Length ?? 0) + (after?.Length ?? 0);
    public Rect Undo(Document d) { d.Sel.Restore(before); return Rect.Empty; }
    public Rect Redo(Document d) { d.Sel.Restore(after); return Rect.Empty; }
}

public sealed class GroupEdit : IEdit
{
    readonly IEdit[] parts;
    public string Label { get; }
    public GroupEdit(string label, IEdit[] p) { Label = label; parts = p; }
    public long Bytes => parts.Sum(p => p.Bytes);
    public Rect Undo(Document d) { var r = Rect.Empty; for (int i = parts.Length - 1; i >= 0; i--) r = r.Union(parts[i].Undo(d)); return r; }
    public Rect Redo(Document d) { var r = Rect.Empty; foreach (var p in parts) r = r.Union(p.Redo(d)); return r; }
}
