// 펜 진단 재료: 포인터 메시지 한 개(PenFrame)에 묶여 온 점들(PenSample)과, 진단 한 번 동안 모은 것(DiagSession).
// 점은 그림판(CanvasView)과 같은 길로 읽는다 (PointerInput): 묶여 온 점(history) 전부, HIMETRIC 으로 1픽셀보다 작은 위치, 입력 시각(QPC).
namespace ComicPadNative.Modules.PenDiagnosis;

/// <summary>펜 버튼 (펜 끝·측면 1·측면 2·지우개 끝)</summary>
[Flags]
public enum PenBtn { None = 0, Tip = 1, Side1 = 2, Side2 = 4, Eraser = 8 }

/// <summary>펜 점 하나</summary>
public struct PenSample
{
    public double T;              // ms (QPC 기준. 장치가 QPC 를 안 주면 메시지 시각 dwTime)
    public double X, Y;           // 펜판 안 좌표 (화면 픽셀, 1픽셀보다 작은 값까지)
    public double P;              // 장치가 보낸 필압 0~1 (필압이 없으면 펜 0.5 · 마우스 = 설정 마우스 필압)
    public double TiltX, TiltY;   // 도 (-90~90)
    public double Twist;          // 펜 돌림 (0~359도)
    public bool Contact;          // 화면에 닿았는지
    public PenBtn Buttons;
    public PenSample(double t, double x, double y, double p, bool contact = true) { T = t; X = x; Y = y; P = p; TiltX = TiltY = Twist = 0; Contact = contact; Buttons = contact ? PenBtn.Tip : PenBtn.None; }
}

/// <summary>포인터 메시지 하나 (그 사이에 묶여 온 점 전부, 오래된 것 → 새것)</summary>
public sealed class PenFrame
{
    public string Type = "pen";                    // pen | mouse | touch
    public readonly List<PenSample> Pts = new();
    public double RecvT;                           // 메시지를 받은 시각 (ms, QPC)
    public double PresentT = double.NaN;           // 그린 것을 창에 올린 시각 (ms, QPC)
    public bool Qpc;                               // 점 시각이 QPC (정확) 인지
    public bool HasPressure, HasTilt, HasTwist;    // 장치가 알려 준 항목 (penMask)
    public bool Himetric;                          // 1픽셀보다 작은 위치를 썼는지
    public bool Down, Up;                          // WM_POINTERDOWN / UP
    public bool Predicted;                         // 이 장에 예측 선을 함께 그렸는지
    public PenSample Last => Pts[^1];
    public bool AnyContact { get { foreach (var p in Pts) if (p.Contact) return true; return false; } }
}

/// <summary>진단 한 번 동안 모은 것: 획(닿아 있던 점들), 메시지, 지원 항목, 본 버튼</summary>
public sealed class DiagSession
{
    public const int MaxFrames = 30000, MaxStrokes = 600;
    public readonly List<List<PenSample>> Strokes = new();
    public readonly List<PenFrame> Frames = new();
    public List<PenSample> Cur;                     // 그리는 중인 획
    public int Messages, Points, MaxBatch, ContactMessages, ContactPoints;
    public readonly HashSet<string> Types = new();
    public bool SawPressureMask, SawTiltMask, SawTwistMask, SawTiltValue, SawTwistValue, SawHimetric, SawPen;
    public bool AllQpc = true;
    public PenBtn SeenButtons;
    public PenSample? LastSample;
    public string LastType = "";
    public int LastBatch;
    public event Action<List<PenSample>> StrokeEnded;

    public void Clear()
    {
        Strokes.Clear(); Frames.Clear(); Cur = null;
        Messages = Points = MaxBatch = ContactMessages = ContactPoints = LastBatch = 0;
        Types.Clear(); SawPressureMask = SawTiltMask = SawTwistMask = SawTiltValue = SawTwistValue = SawHimetric = SawPen = false;
        AllQpc = true; SeenButtons = PenBtn.None; LastSample = null; LastType = "";
    }

    /// <summary>메시지 하나를 더함: 닿은 점은 지금 획에, 떨어지면 획을 끝냄</summary>
    public void Add(PenFrame f)
    {
        if (f == null || f.Pts.Count == 0) return;
        Messages++; Points += f.Pts.Count; LastBatch = f.Pts.Count; MaxBatch = Math.Max(MaxBatch, f.Pts.Count);
        Types.Add(f.Type); LastType = f.Type;
        if (f.Type == "pen")
        {
            SawPen = true;
            SawPressureMask |= f.HasPressure; SawTiltMask |= f.HasTilt; SawTwistMask |= f.HasTwist;
        }
        SawHimetric |= f.Himetric;
        if (!f.Qpc) AllQpc = false;
        if (f.AnyContact) { ContactMessages++; }
        Frames.Add(f);
        if (Frames.Count > MaxFrames) Frames.RemoveRange(0, Frames.Count - MaxFrames);
        foreach (var s in f.Pts)
        {
            SeenButtons |= s.Buttons;
            if (s.TiltX != 0 || s.TiltY != 0) SawTiltValue = true;
            if (s.Twist != 0) SawTwistValue = true;
            LastSample = s;
            if (s.Contact)
            {
                ContactPoints++;
                Cur ??= new List<PenSample>();
                Cur.Add(s);
            }
            else EndStroke();
        }
        if (f.Up) EndStroke();
    }

    public void EndStroke()
    {
        var s = Cur; Cur = null;
        if (s == null || s.Count < 2) return;
        Strokes.Add(s);
        if (Strokes.Count > MaxStrokes) Strokes.RemoveRange(0, Strokes.Count - MaxStrokes);
        StrokeEnded?.Invoke(s);
    }

    /// <summary>끝난 획 + 그리는 중인 획</summary>
    public IEnumerable<List<PenSample>> AllStrokes() { foreach (var s in Strokes) yield return s; if (Cur != null && Cur.Count >= 2) yield return Cur; }
}
