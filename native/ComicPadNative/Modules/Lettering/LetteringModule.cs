// 번역·식자 모듈: 대사 표(CSV·엑셀) 내보내기/가져오기, 언어 변형(meta.i18n)과 보기 언어, 언어별 글꼴, 말풍선에 대사 맞추기,
// 모든 글자 찾아 바꾸기·번역 빠짐 검사, 언어별 일괄 내보내기, 웹툰 폰 미리보기. 시험: --selftest lettering 결과폴더
using ComicPadNative.Core;
using ComicPadNative.Modules.Comic;
using ComicPadNative.Modules.Extras;

namespace ComicPadNative.Modules.Lettering;

public sealed class LetteringModule : IModule
{
    public void Init()
    {
        // 글자 레이어를 보기 언어로 그리기·번역 편집·실행취소 뒤 맞추기
        MetaLayers.TextHook = I18n.Hook;
        TextTool.EditOverride = LetteringUi.EditHook;
        App.History.Changed += () => { if (App.Doc != null && I18n.Heal(App.Doc)) App.InvalidateAll(); };
        App.DocChanged += OnDoc;

        bool HasDoc() => App.Doc != null;
        void Run(Action a) { if (App.Doc != null) a(); }
        const string M = "만화/번역·식자/";
        Registry.Menu("만화/-번역", null, order: 44);
        Registry.Menu(M + "대사 표 내보내기 (CSV·엑셀, 번역가에게)…", () => Run(LetteringUi.ExportTable), Keys.None, HasDoc, order: 1);
        Registry.Menu(M + "대사 표 가져오기 (번역 넣기)…", () => Run(LetteringUi.ImportTable), Keys.None, HasDoc, order: 2);
        Registry.Menu(M + "번역 표 보기·고치기…", () => Run(() => LetterGrid.Open(false)), Keys.None, HasDoc, order: 3);
        Registry.Menu(M + "번역 빠짐 검사…", () => Run(() => LetterGrid.Open(true)), Keys.None, HasDoc, order: 4);
        Registry.Menu(M + "-1", null, order: 10);
        Registry.Menu(M + "찾아 바꾸기 (모든 글자 레이어)…", () => Run(LetteringUi.FindReplaceDialog), Keys.Control | Keys.Shift | Keys.H, HasDoc, order: 11);
        Registry.Menu(M + "-2", null, order: 20);
        Registry.Menu(M + "고른 대사를 말풍선에 맞추기", () => Run(LetteringUi.FitSelected), Keys.None, HasDoc, order: 21);
        Registry.Menu(M + "모든 말풍선 맞추기 (이 페이지)", () => Run(() => LetteringUi.FitAll(false)), Keys.None, HasDoc, order: 22);
        Registry.Menu(M + "모든 말풍선 맞추기 (작품 전체)…", () => Run(() => LetteringUi.FitAll(true)), Keys.None, HasDoc, order: 23);
        Registry.Menu(M + "-3", null, order: 30);
        Registry.Menu(M + "언어별 글꼴…", () => Run(LetteringUi.FontsDialog), Keys.None, HasDoc, order: 31);
        Registry.Menu(M + "언어별 그림 내보내기 (일괄)…", () => Run(LetteringUi.LangExportDialog), Keys.None, HasDoc, order: 32);
        Registry.Menu("만화/웹툰 폰 미리보기 (내보내기 결과로)…", () => Run(MobilePreview.Open), Keys.None, HasDoc, order: 36);

        // 보기 > 언어
        Registry.Menu("보기/-언어", null, order: 95);
        Registry.Menu("보기/언어/원문", () => Run(() => LetteringUi.SwitchLang(null)), Keys.None, HasDoc, () => App.Doc != null && I18n.LangOf(App.Doc) == null, order: 1);
        int o = 2;
        foreach (var (code, _) in I18n.Common.Take(6))
        {
            string c = code;
            Registry.Menu("보기/언어/" + I18n.Label(c), () => Run(() => LetteringUi.SwitchLang(c)), Keys.None, HasDoc, () => App.Doc != null && I18n.LangOf(App.Doc) == c, order: o++);
        }
        Registry.Menu("보기/언어/다른 언어…", () => Run(LetteringUi.OtherLang), Keys.None, HasDoc, () => App.Doc != null && I18n.LangOf(App.Doc) is { } l && !I18n.Common.Take(6).Any(x => x.code == l), order: 20);
        Registry.Menu("보기/언어/언어별 글꼴…", () => Run(LetteringUi.FontsDialog), Keys.None, HasDoc, order: 21);
        Registry.Menu("보기/웹툰 폰 미리보기…", () => Run(MobilePreview.Open), Keys.None, HasDoc, order: 96);
    }

    /// <summary>작품(여러 페이지)의 보기 언어: story.json "lang"</summary>
    public static void SetStoryLang(List<WorkPage> pages, string lang)
    {
        string story = pages.Select(p => Story.StoryOfRel(p.Rel)).FirstOrDefault(s => s != null);
        if (story == null) return;
        var info = Story.ReadInfo(story);
        if ((string)info["lang"] == lang) return;
        if (lang == null) info.Remove("lang"); else info["lang"] = lang;
        try { Story.WriteInfo(story, info); } catch { }
    }

    /// <summary>작품 페이지를 열면 작품의 보기 언어로 (저장 안 함 표시는 그대로)</summary>
    static void OnDoc()
    {
        var d = App.Doc;
        string story = Work.StoryOf(d);
        if (story == null) return;
        string lang = (string)Story.ReadInfo(story)["lang"];
        if (lang == I18n.LangOf(d)) return;
        bool mod = d.Modified;
        I18n.SetLangRaw(d, lang);
        d.Modified = mod;
    }
}
