// 이 모듈에서 쓰는 작은 UI 부품: 웹 segmented(여러 개 중 하나) 단추 줄, 웹 form() 같은 입력 대화상자.
using System.Globalization;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.SelectFill;

public static class Ui
{
    /// <summary>여러 개 중 하나 고르는 단추 줄 (웹 segmented)</summary>
    public static Control Segmented<T>((T id, string text)[] items, Func<T> get, Action<T> set) => UI.Controls.WebSeg.Of(items, get, set);

    // ---- 입력 대화상자 (웹 ui/dialogs.js form — 모양은 UI.Dialogs.WebForm) -------------------------------------------------
    public sealed class Field
    {
        public string Key, Label, Type;          // segment | number | checkbox | color | info
        public (string id, string text)[] Options;
        public object Value;
        public double Min = double.MinValue, Max = double.MaxValue, Step = 1;
    }
    public static Field Seg(string key, string label, (string, string)[] opts, string value) => new() { Key = key, Label = label, Type = "segment", Options = opts, Value = value };
    public static Field Num(string key, string label, double value, double min = -1e6, double max = 1e6, double step = 1) => new() { Key = key, Label = label, Type = "number", Value = value, Min = min, Max = max, Step = step };
    public static Field Check(string key, string label, bool value) => new() { Key = key, Label = label, Type = "checkbox", Value = value };
    public static Field ColorF(string key, string label, string hex) => new() { Key = key, Label = label, Type = "color", Value = hex };
    public static Field Info(string label) => new() { Key = "info", Label = label, Type = "info" };

    /// <summary>값 입력 대화상자 → 확인하면 key → 값 (string / double / bool), 취소하면 null</summary>
    public static Dictionary<string, object> Form(string title, Field[] fields, string okLabel = "확인")
    {
        var spec = fields.Select(fd => new UI.Dialogs.FormField
        {
            Key = fd.Type == "info" ? null : fd.Key, Label = fd.Label, Type = fd.Type, Options = fd.Options,
            Min = Math.Max(-1e9, fd.Min), Max = Math.Min(1e9, fd.Max), Step = fd.Step,
            Value = fd.Type == "number" ? Convert.ToDouble(fd.Value, CultureInfo.InvariantCulture) : fd.Value,
            Decimals = fd.Type == "number" ? (fd.Step < 1 ? 2 : 0) : -1,
        }).ToList();
        return UI.Dialogs.WebForm.Show(title, spec, okLabel);
    }

    public static Color HexColor(string hex) { var (r, g, b) = AutoFlat.Hex(hex); return Color.FromArgb(r, g, b); }
    public static string ToHex(uint premulColor) => Core.Px.ToHex(premulColor);
}
