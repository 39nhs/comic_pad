// 대사 번역본(언어 변형): 글자 레이어 meta.i18n = { "en": { text, size?, font?, x?, y?, vertical?, lineHeight?, … }, "ja": {…} }.
// 원문은 meta.text 에 그대로 둔다 (웹 판은 모르는 키를 그대로 들고 있고 원문으로 다시 그림 → .cpad 호환).
// 문서 Extra.lettering = { lang: "en", fonts: { en: "글꼴" } } 이 "보기 언어": 그 언어 번역이 있는 글자 레이어만 번역으로 다시 그린다.
// 어떤 언어로 그렸는지 meta.i18nShown 에 적어 두어, 실행취소로 옛 그림이 돌아오면 지금 보기 언어로 다시 그린다 (Heal).
using System.Text.Json.Nodes;
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;

namespace ComicPadNative.Modules.Lettering;

public static class I18n
{
    /// <summary>메뉴·표에 바로 보이는 언어 (코드, 이름)</summary>
    public static readonly (string code, string label)[] Common =
    {
        ("en", "English"), ("ja", "日本語"), ("zh", "中文 (简体)"), ("zh-TW", "中文 (繁體)"), ("th", "ไทย"),
        ("id", "Bahasa Indonesia"), ("vi", "Tiếng Việt"), ("es", "Español"), ("fr", "Français"), ("de", "Deutsch"),
    };
    public static string Label(string code) => string.IsNullOrEmpty(code) ? "원문" : Common.FirstOrDefault(c => c.code == code).label is { } l ? $"{l} ({code})" : code;

    /// <summary>번역본이 바꿀 수 있는 글자 속성 (나머지 색·외곽선 등은 원문을 따름)</summary>
    public static readonly HashSet<string> OverKeys = new() { "text", "size", "font", "lineHeight", "x", "y", "vertical", "tracking", "bold", "italic", "align", "kern" };

    static readonly HashSet<string> Latin = new(StringComparer.OrdinalIgnoreCase) { "en", "es", "fr", "de", "it", "pt", "pt-BR", "id", "ms", "vi", "tl", "nl", "pl", "tr", "sv", "da", "no", "fi", "cs", "ro", "hu" };
    public static bool IsLatin(string code) => code != null && (Latin.Contains(code) || Latin.Contains(code.Split('-')[0]));

    /// <summary>언어별 기본 글꼴 후보 (설치된 첫 글꼴, 없으면 원문 글꼴)</summary>
    static readonly Dictionary<string, string[]> DefaultFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["latin"] = new[] { "CC Wild Words", "Wild Words", "Anime Ace 2.0 BB", "Anime Ace", "Komika Text", "Komika Axis", "Comic Neue", "Comic Sans MS" },
        ["ja"] = new[] { "Yu Gothic", "Meiryo", "MS Gothic" },
        ["zh"] = new[] { "Microsoft YaHei", "SimHei", "DengXian" },
        ["zh-TW"] = new[] { "Microsoft JhengHei", "PMingLiU" },
        ["th"] = new[] { "Leelawadee UI", "Tahoma" },
    };

    public static JsonObject Settings(Document d, bool create = false)
    {
        if (d == null) return null;
        if (d.Extra["lettering"] is JsonObject o) return o;
        if (!create) return null;
        o = new JsonObject(); d.Extra["lettering"] = o; return o;
    }
    public static string LangOf(Document d) => J.Str(Settings(d), "lang") is { Length: > 0 } s ? s : null;

    /// <summary>이 언어의 글꼴: 작품에서 정한 것 → 언어 기본 후보 중 설치된 것 → null (원문 글꼴)</summary>
    public static string FontFor(Document d, string lang)
    {
        if (string.IsNullOrEmpty(lang)) return null;
        string set = J.Str(J.Obj(Settings(d), "fonts"), lang);
        if (!string.IsNullOrEmpty(set) && Fonts.Has(set)) return set;
        string key = DefaultFonts.ContainsKey(lang) ? lang : IsLatin(lang) ? "latin" : lang.Split('-')[0];
        if (DefaultFonts.TryGetValue(key, out var list)) foreach (var f in list) if (Fonts.Has(f)) return f;
        return null;
    }
    public static string FontSetting(Document d, string lang) => J.Str(J.Obj(Settings(d), "fonts"), lang);

    public static bool IsText(Layer l) => l?.Kind == "text" && l.Meta != null;

    public static JsonObject Tr(JsonObject meta, string lang, bool create = false)
    {
        if (meta == null || string.IsNullOrEmpty(lang)) return null;
        if (meta["i18n"] is not JsonObject all)
        {
            if (!create) return null;
            all = new JsonObject(); meta["i18n"] = all;
        }
        if (all[lang] is JsonObject t) return t;
        if (!create) return null;
        t = new JsonObject(); all[lang] = t; return t;
    }
    public static string TrText(JsonObject meta, string lang) => J.Str(Tr(meta, lang), "text") ?? "";
    public static bool HasTr(JsonObject meta, string lang) => TrText(meta, lang).Trim().Length > 0;

    /// <summary>이 언어로 그릴 meta (번역이 없으면 null = 원문 그대로)</summary>
    public static JsonObject Effective(Document d, JsonObject meta, string lang)
    {
        var tr = Tr(meta, lang);
        if (tr == null || J.Str(tr, "text", "").Trim().Length == 0) return null;
        var e = J.Clone(meta);
        e.Remove("i18n"); e.Remove("i18nShown");
        if (tr["font"] == null && FontFor(d, lang) is { } f) e["font"] = f;
        // 세로쓰기 원문을 라틴 문자로 옮기면 가로로
        if (tr["vertical"] == null && J.Bool(meta, "vertical") && IsLatin(lang)) e["vertical"] = false;
        foreach (var (k, v) in tr) if (OverKeys.Contains(k) && v != null) e[k] = v.DeepClone();
        return e;
    }

    /// <summary>원래 그리는 길 (글자·효과음)</summary>
    static (Surface s, Rect r) Base(Document d, Layer l) => J.Bool(l.Meta, "sfx") ? Sfx.Render(d, l) : TextRender.Render(d, l);

    /// <summary>글자 레이어를 lang 으로 그림 (null = 원문). meta 의 lbox·box·quad 는 그린 모양으로, i18nShown 에 언어를 적음</summary>
    public static (Surface s, Rect r) Render(Document d, Layer l, string lang)
    {
        var orig = l.Meta;
        var eff = Effective(d, orig, lang);
        if (eff == null) { orig.Remove("i18nShown"); return Base(d, l); }
        l.Meta = eff;
        try
        {
            var res = Base(d, l);
            foreach (var k in new[] { "lbox", "box", "quad" }) orig[k] = eff[k]?.DeepClone();
            orig["i18nShown"] = lang;
            return res;
        }
        finally { l.Meta = orig; }
    }
    public static (Surface s, Rect r) Render(Document d, Layer l) => Render(d, l, LangOf(d));

    /// <summary>MetaLayers.Render 의 글자 갈래 (보기 언어도, 번역 흔적도 없으면 null → 원래 길)</summary>
    public static (Surface s, Rect r)? Hook(Document d, Layer l)
    {
        if (d == null || l?.Meta == null) return null;
        if (LangOf(d) == null && l.Meta["i18nShown"] == null) return null;
        return Render(d, l);
    }

    public static void RenderInPlace(Document d, Layer l, string lang)
    {
        var (s, _) = Render(d, l, lang);
        l.Px = s; l.Touch();
    }

    /// <summary>이 레이어가 지금 그려져 있어야 할 언어</summary>
    static string Want(JsonObject meta, string lang) => lang != null && HasTr(meta, lang) ? lang : null;

    /// <summary>작품에 들어 있는 번역 언어들</summary>
    public static List<string> LangsIn(IEnumerable<Document> docs)
    {
        var set = new List<string>();
        foreach (var d in docs)
            foreach (var l in d.Layers.Where(IsText))
                if (l.Meta["i18n"] is JsonObject all)
                    foreach (var (k, v) in all) if (v is JsonObject && !set.Contains(k)) set.Add(k);
        return set;
    }

    /// <summary>보기 언어 바꾸기 (기록 없이): 글자 레이어를 모두 다시 그림. 바뀐 레이어 수</summary>
    public static int SetLangRaw(Document d, string lang)
    {
        if (string.IsNullOrEmpty(lang)) lang = null;
        var st = Settings(d, lang != null);
        if (st != null) { if (lang == null) st.Remove("lang"); else st["lang"] = lang; }
        int n = 0;
        foreach (var l in d.Layers.Where(IsText))
        {
            string want = Want(l.Meta, lang), shown = J.Str(l.Meta, "i18nShown");
            if (want == shown && want == null) continue;   // 원문 그대로
            RenderInPlace(d, l, lang); n++;
        }
        return n;
    }

    /// <summary>실행취소·다시 실행 뒤: 그려진 언어가 보기 언어와 다른 글자 레이어만 다시 그림</summary>
    public static bool Heal(Document d)
    {
        if (d == null) return false;
        string lang = LangOf(d);
        bool any = false;
        foreach (var l in d.Layers)
        {
            if (!IsText(l)) continue;
            string shown = J.Str(l.Meta, "i18nShown");
            if (lang == null && shown == null) continue;
            if (Want(l.Meta, lang) == shown) continue;
            RenderInPlace(d, l, lang); any = true;
        }
        return any;
    }

    /// <summary>잠깐 다른 언어로 (내보내기용): 끝나면 레이어 그림·meta·보기 언어를 그대로 되돌림</summary>
    public static IDisposable Scope(Document d, string lang) => new LangScope(d, lang);
    sealed class LangScope : IDisposable
    {
        readonly Document d; readonly JsonNode before; readonly List<(Layer l, Surface px, JsonObject meta)> saved = new();
        public LangScope(Document d, string lang)
        {
            this.d = d; before = d.Extra["lettering"]?.DeepClone();
            foreach (var l in d.Layers.Where(IsText)) saved.Add((l, l.Px, J.Clone(l.Meta)));
            SetLangRaw(d, lang);
        }
        public void Dispose()
        {
            foreach (var (l, px, meta) in saved) { l.Px = px; l.Meta = meta; l.Touch(); }
            if (before == null) d.Extra.Remove("lettering"); else d.Extra["lettering"] = before.DeepClone();
        }
    }

    /// <summary>번역 레이어를 원문 상자 가운데에 다시 놓기 (세로↔가로가 바뀐 말풍선 밖 글자)</summary>
    public static void Recenter(Document d, JsonObject meta, string lang)
    {
        var eff = Effective(d, meta, lang); if (eff == null) return;
        var tr = Tr(meta, lang);
        if (tr["x"] != null || tr["y"] != null) return;
        var (ocx, ocy) = Center(meta);
        eff["x"] = 0; eff["y"] = 0;
        var (ecx, ecy) = Center(eff);
        tr["x"] = J.R(ocx - ecx); tr["y"] = J.R(ocy - ecy);
    }
    /// <summary>글자 배치 상자의 가운데 (변형 전 좌표)</summary>
    public static (double x, double y) Center(JsonObject m)
    {
        var fam = Fonts.Family(J.Str(m, "font"));
        var st = Fonts.Style(fam, J.Bool(m, "bold", true), J.Bool(m, "italic"));
        var (_, b) = TextRender.Layout(m, fam, st);
        return (b.x + b.w / 2, b.y + b.h / 2);
    }
}
