// JSON 값 꺼내기 도우미 (웹 판 JS 값처럼 느슨하게: 없거나 형이 다르면 기본값)
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.Anim;

public static class J
{
    /// <summary>숫자 (true/false 는 1/0, 문자열 숫자도 읽음)</summary>
    public static double D(JsonNode n, double def = 0)
    {
        if (n is not JsonValue v) return def;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        if (v.TryGetValue<JsonElement>(out var e))
        {
            if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
            if (e.ValueKind == JsonValueKind.True) return 1;
            if (e.ValueKind == JsonValueKind.False) return 0;
            if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
            return def;
        }
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var q)) return q;
        return def;
    }

    /// <summary>숫자가 있는지 (null·없음이면 null)</summary>
    public static double? N(JsonNode n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetValue<string>(out _)) return null;
        if (v.TryGetValue<bool>(out _)) return null;
        return D(n, 0);
    }

    public static int I(JsonNode n, int def = 0) => (int)Math.Round(D(n, def));

    /// <summary>JS 의 참/거짓 (없음·null·false·0·"" = 거짓)</summary>
    public static bool T(JsonNode n)
    {
        if (n == null) return false;
        if (n is JsonObject || n is JsonArray) return true;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return s.Length > 0;
            if (v.TryGetValue<JsonElement>(out var e))
            {
                return e.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
                    JsonValueKind.String => e.GetString().Length > 0,
                    JsonValueKind.Number => e.GetDouble() != 0,
                    _ => true,
                };
            }
            return D(n, 0) != 0;
        }
        return true;
    }

    public static bool B(JsonNode n, bool def) => n == null ? def : T(n);

    public static string S(JsonNode n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.String) return e.GetString();
        return null;
    }

    public static JsonNode Num(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15 ? JsonValue.Create((long)Math.Round(v)) : JsonValue.Create(v);

    public static JsonObject Obj(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject o) return o;
        o = new JsonObject(); parent[key] = o; return o;
    }

    public static JsonArray Arr(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray a) return a;
        a = new JsonArray(); parent[key] = a; return a;
    }

    public static JsonArray StrArray(IEnumerable<string> items) => new(items.Select(s => s == null ? null : (JsonNode)JsonValue.Create(s)).ToArray());

    public static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    /// <summary>파일 이름에 못 쓰는 글자를 _ 로 (웹 safeName)</summary>
    public static string SafeName(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s ?? "", "[\\\\/:*?\"<>|]+", "_").Trim();
        return s.Length == 0 ? "제목없음" : s;
    }

    public static string F(double v, string fmt = "0.##") => v.ToString(fmt, CultureInfo.InvariantCulture);
}
