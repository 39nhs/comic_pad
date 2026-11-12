// 내보내기·미디어 모듈 (웹 판 animexport.js · timelapse.js · printout.js · files.js 내보내기 · story.js 내보내기 중 네이티브에 없던 것):
//  - 애니: 움직이는 WebP·MP4(H.264+AAC) 를 더한 내보내기 창 (GIF·APNG·PNG 연번은 Anim 모듈 그대로)
//  - 타임랩스: MP4·GIF 로 (AVI 도 그대로)
//  - 인쇄용 내보내기: 여러 페이지 작품 전체를 한 PDF 로 / 쪽마다 TIFF·PNG, 책 펼침면 미리보기
//  - 이미지 내보내기 창 (형식 PNG·JPG·WebP, 배율, 배경 빼기, 재단선 범위), .webp 열기·가져오기, 작품 전체 내보내기에 PSD·WebP
// 같은 메뉴 경로를 다시 등록하면 앞 모듈의 항목을 대신함 (단축키·순서·켜짐 조건은 그대로 이어받음).
// 시험 (창 없이): COMICPAD_MEDIA_SELFTEST=결과폴더, COMICPAD_DATA=임시 폴더 로 --render 실행 → 결과폴더/report.txt, 끝 코드 = 실패 수
using ComicPadNative.Modules.Anim;
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Media;

public sealed class MediaModule : IModule
{
    /// <summary>있는 메뉴를 대신하기 (단축키·순서·켜짐 조건은 앞 것 그대로)</summary>
    static void Replace(string path, Action run, Keys key = Keys.None, Func<bool> enabled = null, int order = 500)
    {
        var old = Registry.Menus.Find(m => m.Path == path);
        Registry.Menu(path, run, old?.Shortcut ?? key, old?.Enabled ?? enabled, old?.Checked, old?.Order ?? order);
    }

    /// <summary>메뉴·열기 등록 (창 모드에서는 Anim·Extras·Filters 다음에 불려서 그 항목을 대신함)</summary>
    public static void RegisterMenus()
    {
        bool HasDoc() => App.Doc != null;
        bool HasFrames() => App.Doc != null && AnimCore.FrameCount(App.Doc) > 0;
        bool InStory() => Story.StoryOf(App.Doc) != null;

        // ---- 애니: 형식 고르는 창에 WebP·MP4 ----
        const string oldAnim = "애니/애니메이션 내보내기 (GIF·APNG·PNG)…";
        Registry.Menus.RemoveAll(m => m.Path == oldAnim); Registry.Commands.Remove(oldAnim);
        Registry.Menu("애니/애니메이션 내보내기 (GIF·WebP·APNG·MP4·PNG)…", () => AnimMedia.Dialog(), Keys.None, HasFrames, order: 210);
        Registry.Menu("애니/동영상으로 내보내기 (MP4)…", () => AnimMedia.Dialog("mp4"), Keys.None, HasFrames, order: 212);
        Replace("애니/움짤 만들기 (바로, GIF)", AnimMedia.Quick, enabled: HasFrames, order: 200);
        AnimDialogs.ExportOverride = p => AnimMedia.Dialog(p);   // 애니 패널의 "내보내기 (형식 고르기)" 버튼도 새 창으로
        AnimDialogs.QuickOverride = AnimMedia.Quick;

        // ---- 타임랩스: MP4·GIF ----
        Replace("보기/타임랩스 동영상 만들기…", TimelapseMedia.Export, enabled: HasDoc, order: 802);

        // ---- 인쇄: 작품 전체 + 펼침면 ----
        Replace("파일/" + Filters.FiltersModule.M("인쇄용 내보내기 (PDF / 1비트 TIFF)…"), PrintBook.Dialog, enabled: HasDoc, order: 56);
        Registry.Menu("보기/펼침면 미리보기 (책처럼 보기)", PrintBook.SpreadPreview, Keys.None, HasDoc, order: 205);

        // ---- 정지 그림: 내보내기 창·WebP·작품 전체 ----
        Replace("파일/그림으로 내보내기 (PNG·JPG)…", StillExport.Dialog, Keys.Control | Keys.Shift | Keys.E, HasDoc, order: 50);
        Replace("파일/이미지 가져오기 (새 레이어)…", StillExport.ImportImage, enabled: HasDoc, order: 46);
        Replace("만화/작품 전체 내보내기…", StillExport.ExportStory, enabled: InStory, order: 806);
        FileCommands.Openers[".webp"] = StillExport.OpenWebP;
    }

    public void Init()
    {
        RegisterMenus();

        // ---- 시험: 다른 모듈이 모두 등록된 뒤(--render 가 그림을 다 쓴 뒤) 돌리고 끝냄 ----
        //  (메인 스레드는 Main 이 끝나도 앞쪽 스레드를 기다리므로 Join 은 못 씀 → --render 결과 파일이 다 써질 때를 기다림)
        string test = Environment.GetEnvironmentVariable("COMICPAD_MEDIA_SELFTEST");
        if (!string.IsNullOrEmpty(test))
        {
            var args = Environment.GetCommandLineArgs();
            int ri = Array.IndexOf(args, "--render");
            string png = ri >= 0 && ri + 2 < args.Length ? Path.GetFullPath(args[ri + 2]) : null;
            try { if (png != null && File.Exists(png)) File.Delete(png); } catch { }
            var th = new Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 120_000)
                {
                    if (png == null) { Thread.Sleep(2000); break; }
                    if (File.Exists(png)) { try { using (File.Open(png, FileMode.Open, FileAccess.Read, FileShare.None)) { } break; } catch (IOException) { } }
                    Thread.Sleep(50);
                }
                Thread.Sleep(200);
                int fail;
                try { fail = MediaTest.Run(test); } catch (Exception ex) { File.WriteAllText(Path.Combine(test, "report.txt"), "시험 실행 오류: " + ex); fail = 99; }
                Environment.Exit(fail);
            }) { IsBackground = false, Name = "media-selftest" };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }
    }
}
