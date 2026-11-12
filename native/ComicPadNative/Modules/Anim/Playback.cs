// 재생 (타이머) — 웹 판 anim.js play/stop. 재생 중에는 바뀐 셀이 있는 곳만 다시 합성해서 빠르게.
using System.Diagnostics;
using System.Runtime.InteropServices;
using ComicPadNative.Core;

namespace ComicPadNative.Modules.Anim;

public static class Playback
{
    static System.Windows.Forms.Timer timer;
    static List<int> order;
    static int k;
    static long last;
    static double[] times;
    static Document doc;

    public static void Toggle()
    {
        var d = App.Doc;
        if (d == null) return;
        if (AnimCore.Playing) { Stop(); return; }
        if (AnimCore.FrameCount(d) < 2) { App.Toast("프레임이 2개 이상이어야 재생됩니다"); return; }
        doc = d;
        AnimCore.Playing = true;
        order = AnimCore.PlayOrder(d);
        if (order.Count == 0) { AnimCore.Playing = false; return; }
        k = Math.Max(0, order.IndexOf(AnimCore.Frame(d)));
        times = AnimCore.Timeline(d);
        AnimCore.A(d)["frame"] = order[k];
        StartSound();
        last = Stopwatch.GetTimestamp();
        timeBeginPeriod(1);
        timer ??= new System.Windows.Forms.Timer { Interval = 1 };
        timer.Tick -= Tick; timer.Tick += Tick;
        timer.Start();
        App.InvalidateAll();   // 어니언·라이트 테이블 끄기
        AnimCore.Emit();
    }

    static void StartSound() { if (Sound.HasAudio(doc)) Sound.Play(doc, times[order[k]]); }

    static void Tick(object s, EventArgs e)
    {
        if (!AnimCore.Playing || App.Doc != doc) { Stop(); return; }
        long now = Stopwatch.GetTimestamp();
        double ms = (now - last) * 1000.0 / Stopwatch.Frequency;
        if (ms < AnimCore.FrameMs(order[k], doc)) return;
        last = now;
        if (k >= order.Count - 1 && !AnimCore.Loop(doc)) { Stop(); return; }
        k = (k + 1) % order.Count;
        AnimCore.SetFrame(order[k], select: false);
        if (k == 0) StartSound();
    }

    public static void Stop()
    {
        timer?.Stop();
        if (AnimCore.Playing) timeEndPeriod(1);
        Sound.Stop();
        bool was = AnimCore.Playing;
        AnimCore.Playing = false;
        if (App.Doc == null) return;
        if (was) AnimCore.SetFrame(AnimCore.Frame(App.Doc));
    }

    // ---- 재생 중 다시 합성할 곳: 셀이 바뀐 트랙의 앞뒤 그림 범위만 ----
    static readonly Dictionary<Layer, (int ver, Rect b)> bounds = new();
    static Rect BoundsOf(Layer l)
    {
        if (l?.Px == null) return Rect.Empty;
        if (bounds.TryGetValue(l, out var v) && v.ver == l.Version) return v.b;
        var b = l.Px.Bounds();
        bounds[l] = (l.Version, b);
        return b;
    }

    public static void InvalidateFrameChange(Document d, int prev, int next)
    {
        if (prev == next) return;
        var dirty = Rect.Empty;
        foreach (var t in AnimCore.Tracks(d))
        {
            if (!t.Visible) continue;
            var xs = AnimCore.XsOf(t, d);
            string a = prev < xs.Count ? xs[prev] : null, b = next < xs.Count ? xs[next] : null;
            var ma = Motion.At(t, prev, d); var mb = Motion.At(t, next, d);
            if (a == b && ma == null && mb == null) continue;
            foreach (var (id, mo) in new[] { (a, ma), (b, mb) })
            {
                var l = d.ById(id);
                if (l == null) continue;
                // 폴더 셀·효과·마스크가 있으면 범위를 짐작하기 어려우니 전체
                if (l.IsFolder || l.Fx != null || t.Fx != null || l.Mask != null) { App.InvalidateAll(); return; }
                var r = BoundsOf(l);
                if (mo != null) r = mo.Value.T.Bounds(r, 2);
                dirty = dirty.Union(r);
            }
        }
        if (!dirty.IsEmpty) App.Invalidate(dirty.Inflate(2).Clip(d.W, d.H));
        else App.Invalidate(Rect.Empty);
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
}
