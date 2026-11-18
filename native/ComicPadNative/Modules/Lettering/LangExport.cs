// 언어별 그림 내보내기 (일괄): 페이지 × 고른 언어마다 그 언어로 글자 레이어를 잠깐 다시 그려
// 기존 내보내기 길(페이지 그림 = Img.Flatten/Encode, 웹툰 규격 = ExportCommands.WebtoonSlicesOf)로 저장. 원래 그림·보기 언어는 그대로 돌려 놓음.
using ComicPadNative.Core;
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.Filters;

namespace ComicPadNative.Modules.Lettering;

public static class LangExport
{
    public sealed class Opts
    {
        public List<string> Langs = new() { null };   // null = 원문
        public string Mode = "png";                    // png | jpg | webtoon
        public double Scale = 1;
        public MobilePreview.Spec Spec = MobilePreview.Spec.FromSettings();
    }

    public static string Folder(string lang) => lang == null ? "원문" : lang;

    /// <summary>pages × langs 를 outDir/언어/ 에 저장. 만든 파일 수</summary>
    public static int Run(List<WorkPage> pages, Opts o, string outDir)
    {
        int files = 0;
        foreach (var lang in o.Langs)
        {
            string dir = Path.Combine(outDir, Folder(lang)); Directory.CreateDirectory(dir);
            int n = 0;
            foreach (var p in pages)
            {
                using (I18n.Scope(p.Doc, lang))
                {
                    if (o.Mode == "webtoon")
                    {
                        long maxBytes = o.Spec.MaxMB > 0 ? (long)(o.Spec.MaxMB * 1048576) : 0;
                        foreach (var sl in ExportCommands.WebtoonSlicesOf(p.Doc, o.Spec.W, o.Spec.H, o.Spec.Fmt, maxBytes, o.Spec.Smart))
                        { File.WriteAllBytes(Path.Combine(dir, $"{++n:000}.{o.Spec.Fmt}"), sl.Bytes); files++; }
                    }
                    else
                    {
                        using var b = Img.Flatten(p.Doc, o.Scale, true);
                        File.WriteAllBytes(Path.Combine(dir, $"{p.No:000}.{o.Mode}"), Img.Encode(b, o.Mode, 0.92));
                        files++;
                    }
                }
                if (!p.Current) p.Doc.Modified = false;
            }
        }
        return files;
    }
}
