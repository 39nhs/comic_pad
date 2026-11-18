// 클립 스튜디오 텍스트 레이어: 글자 내용·크기·글꼴·위치를 PSD 텍스트와 같은 meta.psdText 로 보관 →
// 텍스트 도구로 탭하면 기존 "PSD 텍스트 변환"(Comic/Text.cs ConvertPsdText)으로 다시 편집할 글자가 된다.
// 형식 출처: nanikasheila/csp-format (MIT) docs/spec/06-text.md — TextLayerAttributes 는 리틀엔디언 기록 (u32 ID, u32 길이, 내용):
//   32 글자 크기(pt×100), 42 외접 사각형 i32×4(레이어 픽셀), 45 말풍선 번호(0=없음), 50 CSP 가 그린 글자 그림의 Offscreen.MainId,
//   57 글꼴(u16 ?, u16 길이 + UTF-8 이름, …). 글자색·세로쓰기·정렬 위치는 문서에 없어 기본값(검정·가로·왼쪽).
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace ComicPadNative.Modules.ClipImport;

public static class ClipText
{
    /// <summary>기록들 (ID → 내용). 머리 구조가 문서에 없어서 0·4·8 바이트 뒤에서 시작해 끝까지 딱 맞게 읽히는 쪽을 씀</summary>
    public static Dictionary<int, byte[]> Records(byte[] a)
    {
        if (a == null) return new();
        foreach (int start in new[] { 0, 4, 8 })
        {
            var d = new Dictionary<int, byte[]>();
            int p = start; bool ok = true;
            while (p + 8 <= a.Length)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(p)), len = BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(p + 4));
                if (id < 0 || id > 4096 || len < 0 || p + 8 + len > a.Length) { ok = false; break; }
                d.TryAdd(id, a.AsSpan(p + 8, len).ToArray());
                p += 8 + len;
            }
            if (ok && p == a.Length && d.Count > 0) return d;
        }
        return new();
    }

    /// <summary>meta.psdText + CSP 가 그려 둔 글자 그림(있으면)의 위치</summary>
    public static (JsonObject json, long imageOff, int imgX, int imgY) Meta(byte[] attrs, string text, int offX, int offY, double dpi)
    {
        var rec = Records(attrs);
        double sizePx = 24;
        if (rec.TryGetValue(32, out var s) && s.Length >= 4)
        {
            double pt = BinaryPrimitives.ReadUInt32LittleEndian(s) / 100.0;
            if (pt > 0 && pt < 2000) sizePx = Math.Round(pt * dpi / 72.0, 1);
        }
        int x0 = 0, y0 = 0; bool hasBox = false;
        if (rec.TryGetValue(42, out var bx) && bx.Length >= 16)
        {
            x0 = BinaryPrimitives.ReadInt32LittleEndian(bx); y0 = BinaryPrimitives.ReadInt32LittleEndian(bx.AsSpan(4));
            hasBox = true;
        }
        bool inBalloon = rec.TryGetValue(45, out var bn) && bn.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bn) != 0;
        string font = null;
        if (rec.TryGetValue(57, out var fn) && fn.Length >= 4)
        {
            int n = BinaryPrimitives.ReadUInt16LittleEndian(fn.AsSpan(2));
            if (n > 0 && 4 + n <= fn.Length) font = Encoding.UTF8.GetString(fn, 4, n).TrimEnd('\0');
        }
        long img = rec.TryGetValue(50, out var im) && im.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(im) : 0;
        // 말풍선 안 글자는 말풍선 중심 기준이라 위치를 모름 → 그림은 쓰지 않음 (글자 내용은 남김)
        if (inBalloon) img = 0;
        int left = x0 + offX, top = y0 + offY;
        var json = new JsonObject
        {
            ["text"] = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n"),
            ["size"] = sizePx, ["color"] = "#000000", ["font"] = font, ["align"] = "left", ["vertical"] = false,
            ["mat"] = new JsonArray(1.0, 0.0, 0.0, 1.0, (double)left, Math.Round(top + sizePx * 0.82, 1)),
            ["source"] = "clip",
        };
        if (!hasBox || inBalloon) json.Remove("mat");
        return (json, img, left, top);
    }
}
