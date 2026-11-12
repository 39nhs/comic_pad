// 위 메뉴 구성 (웹 판과 같은 메뉴·순서·이름·구분선). 웹 판 app.menus 를 그대로 옮긴 표 + 웹 명령 id → 네이티브 메뉴 경로 연결.
//  - 항목 글자: "웹id|웹 이름", "@이름|이름" (웹의 이름 항목: 도구 바꾸기 등), "-" = 구분선
//  - 웹 판 플러그인(집중선·망점 톤)이 넣는 항목과 "플러그인" 메뉴는 네이티브에 없어 뺐다
//  - 네이티브에만 있는 메뉴 항목은 알맞은 메뉴 맨 끝에 (간단히 모드에서는 "모든 항목 보기" 안에)
//  - --selftest menus 결과폴더 → menus.txt (네이티브 대상이 없는 웹 항목·끝에 붙은 네이티브 항목 목록)
using ComicPadNative.Modules.Extras;
using ComicPadNative.Modules.Shell2;

namespace ComicPadNative.UI.Chrome;

public sealed class MenuNode
{
    public string Key;          // 간단히 모드에서 고를 때 쓰는 키: 웹 id / "@이름" / 네이티브 메뉴 경로 / "sub:경로"
    public string Label;
    public string Path;         // 실행할 네이티브 메뉴 경로 (없으면 Run)
    public Action Run;
    public bool Sep;
    public bool Native;         // 웹 판에 없는 네이티브 항목
    public List<MenuNode> Sub;  // 하위 메뉴
    public MenuItemDef Def => Path == null ? null : Registry.Menus.Find(m => m.Path == Path);
    public bool Enabled => Sub != null || (Run != null ? RunEnabled?.Invoke() ?? true : Path != null && (Def?.Enabled?.Invoke() ?? true) && Def?.Run != null);
    public Func<bool> RunEnabled;
    public bool Checked => Path != null && Def?.Checked?.Invoke() == true;
    public Keys Shortcut
    {
        get
        {
            if (Path == null) return Keys.None;
            // 사용자가 바꾼 단축키 (설정 keys: 웹 id 또는 메뉴 경로) 가 먼저
            if (App.Settings["keys"] is System.Text.Json.Nodes.JsonObject ks)
                foreach (var (id, v) in ks)
                    if ((id == Key || id == Path) && v is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue(out string s)) return KeyMap.Parse(s);
            return Def?.Shortcut ?? Keys.None;
        }
    }
}

public sealed record MenuTop(string Id, string Label, List<MenuNode> Items);

public static class WebMenus
{
    // ---- 웹 판 메뉴 (app.menuOrder · app.menus, 기본 설정·플러그인 없음 기준) ----
    static readonly (string id, string label, string[] items)[] Web =
    {
        new("file", "파일", new[]
        {
            "file.new|새 캔버스…", "file.open|프로젝트 열기…", "file.openLocal|파일에서 열기 (.cpad / .psd / 이미지)…", "-",
            "file.save|저장", "file.saveAs|다른 이름으로 저장…", "file.download|프로젝트 파일 다운로드", "-",
            "file.import|이미지 가져오기 (새 레이어)…", "file.export|이미지 내보내기…", "file.exportQuick|PNG 빠른 내보내기", "-",
            "file.recover|자동 저장 복구…", "file.versions|버전 기록 (이전 저장본)…", "file.storage|저장 위치 · 구글 드라이브 (서버 / 폴더 / 이 기기)…", "file.drivePull|구글 드라이브에서 가져오기…",
            "file.folder|data 폴더 열기", "file.exportsFolder|내보낸 이미지 폴더 열기", "-", "file.exportPSD|PSD로 내보내기 (레이어 유지)…",
            "file.exportSlices|웹툰 분할 내보내기…", "file.exportLayers|레이어별 PNG 내보내기…", "file.printExport|인쇄용 내보내기 (PDF / 1비트 TIFF)…", "brush.importAbr|포토샵 브러시(.abr) 가져오기…",
            "file.webtoonExport|웹툰 플랫폼 규격으로 내보내기…", "-", "feedback.export|피드백 파일 보내기 (러프·메모만)…", "feedback.import|피드백 파일 받기…",
            "file.backup|작업물 백업 (자동·지금·꺼내기)…", "file.ebook|전자책 내보내기 (EPUB / CBZ)…", "print.preflight|인쇄·투고 전 점검…", "pixel.newDoc|픽셀 아트 새로 만들기…",
            "file.local|기기 임시 저장함 (연결 끊겼을 때 저장된 것)…", "font.add|글꼴 파일 추가 (.ttf/.otf/.woff)…",
        }),
        new("edit", "편집", new[]
        {
            "edit.undo|실행 취소", "edit.redo|다시 실행", "-", "edit.cut|잘라내기",
            "edit.copy|복사", "edit.paste|붙여넣기", "edit.clear|지우기", "edit.fill|선택 영역 채우기 (전경색)",
            "-", "edit.transform|이동/변형", "edit.flipH|레이어 좌우 반전", "edit.flipV|레이어 상하 반전",
            "-", "edit.canvasSize|캔버스 크기…", "edit.extendDown|웹툰: 아래로 캔버스 늘리기", "-",
            "edit.contentAwareFill|내용 인식 채우기 (선택한 곳을 주변으로 메우기)…", "edit.heal|복구 브러시 (잡티 지우기)", "edit.meshWarp|원근·격자 왜곡 (여러 면)", "edit.puppet|퍼펫 왜곡 (핀으로 휘기)",
            "-", "macro.open|자동 액션 (기록·실행·일괄)…", "macro.record|자동 액션 기록 시작/멈추기",
        }),
        new("layer", "레이어", new[]
        {
            "layer.new|새 레이어", "layer.newVector|새 벡터 레이어 (선 굵기·지우기 나중에 수정)", "vector.width|벡터: 선 굵기 일괄 조정…", "vector.color|벡터: 선 색을 지금 색으로",
            "vector.toRaster|벡터를 일반 레이어로", "bubble.toRaster|말풍선을 그림으로 (직접 그리기 가능, 다시 편집 불가)", "layer.newFolder|새 폴더", "layer.group|폴더로 묶기 (여러 개 고르면 함께)",
            "layer.mergeFolder|폴더를 한 레이어로 합치기", "layer.newRough|새 러프 레이어 (의견 색, 밑그림)", "layer.rough|러프 전용 레이어로 지정/해제", "layer.roughColor|러프 색 바꾸기…",
            "layer.newLine|새 펜선 레이어", "layer.newColor|새 채색 레이어 (아래 레이어에 클리핑)", "-", "layer.duplicate|레이어 복제",
            "layer.delete|레이어 삭제", "layer.mergeDown|아래 레이어와 병합 (여러 개 고르면 고른 것끼리)", "layer.flatten|보이는 레이어 결합", "layer.clear|레이어 내용 지우기",
            "-", "layer.clip|아래 레이어에서 클리핑", "layer.alphaLock|투명 픽셀 잠금", "layer.lock|레이어 잠금",
            "layer.draft|밑그림 레이어 (내보내기 제외)", "layer.tint|레이어 컬러 (파란 선으로 보기)", "layer.selectAlpha|레이어의 그림 부분을 선택", "-",
            "layer.addMask|레이어 마스크 만들기 (선택 영역 → 보이는 부분)", "layer.editMask|마스크 편집 ↔ 그림 편집", "layer.maskOn|마스크 켜기/끄기", "layer.applyMask|마스크 적용 (그림에 반영)",
            "layer.deleteMask|마스크 삭제", "-", "layer.ref|참조 레이어로 지정 (채우기 기준)", "layer.fx|레이어 효과 (테두리/그림자/광선)…",
            "layer.tone|톤화 (망점으로 보이기)…",
        }),
        new("select", "선택", new[]
        {
            "select.all|전체 선택", "select.none|선택 해제", "select.invert|선택 반전", "-",
            "select.grow|선택 확장 (+4px)", "select.shrink|선택 축소 (-4px)", "select.growBy|선택 확장/축소…", "-",
            "select.toLayer|선택 영역을 새 레이어로 복사", "layer.selectAlpha|레이어의 그림 부분을 선택", "-", "select.refine|선택 다듬기 (매끄럽게·페더·대비·경계 이동)…",
            "select.refineBrush|머리카락 다듬기 브러시", "-", "path.tool|펜 도구 (베지어 패스)", "path.fromSelection|선택 영역 → 패스",
            "path.toSelection|패스 → 선택 영역", "select.feather|선택 경계 흐리기…", "select.colorRange|색역 선택 (같은 색 전부)…",
        }),
        new("comic", "만화", new[]
        {
            "panel.frame|페이지 컷 만들기…", "@컷 나누기 도구|컷 나누기 도구", "@말풍선 도구|말풍선 도구", "@텍스트 도구|텍스트 도구",
            "-", "layer.newRough|새 러프 레이어 (의견 색, 밑그림)", "layer.newLine|새 펜선 레이어", "layer.newColor|새 채색 레이어 (아래 레이어에 클리핑)",
            "-", "edit.extendDown|웹툰: 아래로 캔버스 늘리기", "-", "ruler.radial|방사선 자 (집중선용)",
            "file.exportSlices|웹툰 분할 내보내기…", "-", "color.autoFlat|밑색 한 번에 칠하기 (선화 영역마다)…", "color.hintLayer|색 힌트 레이어 만들기 (색 점 찍기)",
            "color.hintFill|색 점으로 칠하기 (힌트대로 밑색)…", "progress.open|진행표 (단계·작업 시간·마감)…", "sfx.tool|효과음 글자 도구", "print.preflight|인쇄·투고 전 점검…",
            "panel.templates|컷 나누기 템플릿…", "-", "print.setup|원고 설정 (재단선·도련·안전선)…", "print.toggle|원고 가이드 보이기/숨기기",
            "memo.tool|메모 핀 도구", "-", "story.new|새 작품 (여러 페이지)…", "story.prev|이전 페이지",
            "story.next|다음 페이지", "story.add|페이지 추가", "story.dup|현재 페이지 복제", "story.export|작품 전체 내보내기…",
            "-", "3d.open|3D 소재 (데생 인형·모델 합성)…", "-", "@유선 도구|유선 도구",
            "effect.flash|플래시 효과 (우니/베타)…",
        }),
        new("asset", "에셋", new[]
        {
            "asset.saveLayer|현재 레이어를 에셋으로 저장…", "asset.saveSelection|선택 영역을 에셋으로 저장…", "asset.saveMerged|보이는 그림 전체를 에셋으로 저장…", "-",
            "asset.folder|에셋 폴더 열기", "-", "filter.outline|외곽선 두르기 (스티커/에셋)…", "filter.shadow|그림자 넣기…",
            "file.exportLayers|레이어별 PNG 내보내기…", "3d.open|3D 소재 (데생 인형·모델 합성)…",
        }),
        new("filter", "필터", new[]
        {
            "filter.lineExtract|선화 추출 (흰 배경 → 투명)", "filter.mono|2값화 (흑백 선화 정리)", "filter.gray|흑백", "filter.invert|색 반전",
            "filter.blur|흐리게…", "filter.adjust|밝기/대비/채도…", "-", "filter.outline|외곽선 두르기 (스티커/에셋)…",
            "filter.shadow|그림자 넣기…", "filter.colorize|단색으로 바꾸기 (선 색 변경)", "-", "filter.lineClean|선 정리 (잡티·끊김·울퉁불퉁)…",
            "ai.img2img|이미지 AI 로 그리기 (이 PC 의 SD WebUI)…", "ai.check|이미지 AI 연결 확인", "-", "pixel.quantize|색 수 줄이기 (팔레트·디더링)…",
            "pixel.replaceColor|팔레트 색 바꾸기 (같은 색 한꺼번에)…", "pixel.lock|팔레트 색으로만 칠하기 (이 작품)", "pixel.paletteImport|팔레트 가져오기 (.gpl/.hex)…", "pixel.paletteExport|팔레트 내보내기 (.gpl/.hex)…",
            "filter.unsharp|언샤프 마스크 (선명하게)…", "filter.noise|노이즈 추가…", "filter.mosaic|모자이크…", "filter.motion|방향 흐림 (모션 블러)…",
            "filter.zoomblur|방사형 흐림 (줌)…", "filter.glowBlur|빛 번짐 (블룸)…",
        }),
        new("view", "보기", new[]
        {
            "view.fit|화면에 맞춤", "view.fitWidth|가로 폭에 맞춤", "view.actual|100%", "view.zoomIn|확대",
            "view.zoomOut|축소", "-", "view.rotL|왼쪽으로 15° 회전", "view.rotR|오른쪽으로 15° 회전",
            "view.resetRotation|회전 초기화", "view.flip|화면 좌우 반전 (데생 확인)", "-", "view.grid|격자 보기",
            "view.symmetry|대칭 그리기 (좌우)", "-", "view.toggleSide|오른쪽 패널 보이기/숨기기", "view.quickbar|퀵바 보이기/숨기기",
            "view.toggleUI|패널 모두 숨기기/보이기", "view.fullscreen|전체 화면", "-", "view.symmetryRadial|방사 대칭 (만다라)…",
            "view.symmetryOff|대칭 끄기", "view.reference|참조 이미지 창 열기…", "view.cmykProof|인쇄 색 미리보기 (CMYK)…", "view.spread|펼침면 미리보기 (책처럼 보기)",
            "-", "timelapse.toggle|타임랩스 기록 켜기/끄기", "timelapse.export|타임랩스 동영상 만들기…", "timelapse.clear|타임랩스 장면 지우기…",
            "-", "workspace.save|작업 화면 저장…", "workspace.manage|작업 화면 바꾸기·관리…", "-",
            "view.luma|명도 확인 보기 (끔 → 흑백 → 5단계)", "paper.texture|용지 질감 (종이 결)…", "ui.radialEdit|원형 메뉴 편집…", "pixel.grid|픽셀 격자 (많이 확대했을 때)",
            "pixel.crisp|픽셀아트로 보기 (축소해도 또렷하게, 이 작품)", "mem.freezeNow|메모리 절약: 지금 안 쓰는 레이어 압축", "-", "ui.customize|화면 꾸미기 (도구·메뉴·패널)…",
            "ui.mode|간단히 ↔ 모든 기능 보기", "popout.panel|패널을 앱 밖 창으로…", "-", "view.webtoon|웹툰 미리보기 (휴대폰 폭)",
            "view.bottombar|아래 막대 보이기/숨기기", "view.bottombarEdit|아래 막대 버튼 고르기…",
        }),
        new("ai", "AI", new[]
        {
            "ai.lineart|AI 선화 추출…", "ai.cutout|AI 캐릭터·배경 따기…", "ai.select|AI 고르기 (탭한 물체 선택)", "ai.depth|AI 깊이 추정…",
            "ai.upscale|AI 크기 키우기 (새 작품으로)…", "ai.pose|사진에서 인형 자세 (3D 소재 창)…", "ruler.fromPhoto|사진·배경에서 퍼스 자 만들기 (소실점 자동)", "-",
            "filter.lineClean|선 정리 (잡티·끊김·울퉁불퉁)…", "ai.img2img|이미지 AI 로 그리기 (이 PC 의 SD WebUI)…", "ai.check|이미지 AI 연결 확인", "-",
            "ai.setup|AI 기능 고르기·받기…", "ai.models|AI 모델 관리 (받기·라이선스)…", "-", "vt.studio|버튜버 스튜디오 (웹캠·마이크로 캐릭터 움직이기)…",
            "vt.setup|버튜버 아바타 만들기 (레이어를 부위로)…", "vt.open|버튜버 아바타 파일 열기 (.cpvt)…",
        }),
        new("help", "도움말", new[]
        {
            "settings.open|환경 설정…", "help.keys|단축키 / 제스처 안내", "help.about|ComicPad 정보 · 라이선스", "pen.diagnose|펜 필기감 진단 (이 기기·이 펜에 맞추기)…",
            "help.update|업데이트 확인…", "help.report|문제 보내기 (오류 보고)…", "selftest.run|자동 점검 (주요 기능 시험)", "help.manual|ComicPad 설명서 (전체 기능)",
            "help.features|기능 안내 (무엇을 어디서)…", "-", "setup.choose|초기 세팅 프리셋 고르기 (클립스튜디오식·포토샵식·웹툰·태블릿)…", "setup.export|내 세팅 내보내기 (.cpset)…",
            "setup.import|세팅 가져오기 (.cpset)…", "setup.undo|세팅 되돌리기 (마지막 프리셋·가져오기 전으로)", "palette.open|기능 검색…", "settings.keys|단축키 편집…",
            "mobile.connect|휴대폰·태블릿 연결 (QR)…", "mobile.install|앱으로 설치 (홈 화면/시작 메뉴)",
        }),
        new("ruler", "자", new[]
        {
            "@자 도구|자 도구", "ruler.snap|자에 맞추기 켜기/끄기", "-", "ruler.p1|1점 투시 자",
            "ruler.p2|2점 투시 자", "ruler.p3|3점 투시 자", "-", "ruler.parallel|평행선 자",
            "ruler.radial|방사선 자 (집중선용)", "ruler.concentric|동심원 자", "ruler.fromPhoto|사진·배경에서 퍼스 자 만들기 (소실점 자동)",
        }),
        new("anim", "애니", new[]
        {
            "anim.create|애니메이션 만들기 (빈 프레임 3장)", "anim.track|트랙 추가 (레이어처럼 따로 움직이는 줄)", "anim.fromLayers|최상위 레이어들을 프레임으로 바꾸기", "-",
            "anim.add|빈 프레임 추가", "anim.dup|현재 프레임 복제", "anim.link|연결된 프레임 추가 (같은 그림 이어 쓰기)", "anim.unlink|칸 연결 끊기 (따로 고치기)",
            "anim.clearCell|칸 비우기", "anim.delete|프레임 삭제", "-", "anim.prev|이전 프레임",
            "anim.next|다음 프레임", "anim.play|재생 / 정지", "anim.onion|어니언 스킨 켜기/끄기", "anim.light|라이트 테이블에 올리기/내리기 (지금 칸)",
            "anim.tag|태그(구간) 만들기…", "anim.timesheet|타임시트 (노출표) 보기·고치기…", "-", "anim.motion|모션 경로 도구 (트윈)",
            "anim.bake|모션을 그림으로 굳히기", "-", "anim.gifQuick|움짤 만들기 (바로, GIF)", "anim.export|애니메이션 내보내기 (GIF·WebP·APNG·MP4·WebM·PNG)…",
            "anim.sheet|스프라이트 시트 내보내기 (PNG + JSON)…", "anim.importSheet|스프라이트 시트 가져오기…", "anim.importAse|Aseprite 파일 열기 (.aseprite/.ase)…", "anim.exportAse|Aseprite 파일로 저장 (.aseprite)",
        }),
        new("adjust", "보정", new[]
        {
            "adjust.levels|레벨 (밝기 범위)…", "adjust.curves|톤 커브 (곡선)…", "adjust.hsl|색조/채도/명도 (HSL)…", "adjust.balance|색상 균형…",
            "adjust.bc|밝기/대비…", "adjust.vibrance|자연 채도…", "-", "adjust.gradmap|그라데이션 맵…",
            "adjust.colorize|단색 입히기 (세피아 등)…", "adjust.bw|흑백…", "adjust.posterize|포스터화…", "adjust.threshold|2값화 (한계값)…",
            "adjust.invert|반전…", "-", "@— 조정 레이어 만들기 (원본 보존) —|— 조정 레이어 만들기 (원본 보존) —", "adjlayer.levels|조정 레이어: 레벨 (밝기 범위)",
            "adjlayer.curves|조정 레이어: 톤 커브 (곡선)", "adjlayer.hsl|조정 레이어: 색조/채도/명도 (HSL)", "adjlayer.balance|조정 레이어: 색상 균형", "adjlayer.bc|조정 레이어: 밝기/대비",
            "adjlayer.gradmap|조정 레이어: 그라데이션 맵", "adjlayer.posterize|조정 레이어: 포스터화", "adjlayer.threshold|조정 레이어: 2값화 (한계값)", "adjlayer.invert|조정 레이어: 반전",
            "adjlayer.bw|조정 레이어: 흑백", "adjlayer.colorize|조정 레이어: 단색 입히기 (세피아 등)", "adjlayer.vibrance|조정 레이어: 자연 채도", "-",
            "layer.fx|레이어 효과 (테두리/그림자/광선)…", "layer.tone|톤화 (망점으로 보이기)…",
        }),
    };

    public static IEnumerable<string> Ids => Web.Select(w => w.id);
    public static string LabelOf(string id) => Web.FirstOrDefault(w => w.id == id).label ?? id;

    /// <summary>웹 이름 항목("@…") → 도구</summary>
    static readonly Dictionary<string, string> NameTools = new() { ["@컷 나누기 도구"] = "panel", ["@말풍선 도구"] = "bubble", ["@텍스트 도구"] = "text", ["@유선 도구"] = "speedlines", ["@자 도구"] = "ruler" };

    /// <summary>웹 id → 네이티브 메뉴 경로 (WebIds·KeyMap 별칭에 없는 것만, 이 메뉴 표에서 씀)</summary>
    public static readonly Dictionary<string, string> Extra = new()
    {
        ["edit.contentAwareFill"] = "편집/내용 인식 채우기 (선택한 곳을 주변으로 메우기)…", ["edit.heal"] = "편집/복구 브러시 (잡티 지우기)",
        ["ai.img2img"] = "AI/이미지 AI 로 그리기 (이 PC 의 SD WebUI)…", ["ai.check"] = "AI/이미지 AI 연결 확인", ["ai.pose"] = "AI/사진에서 인형 자세 (3D 소재 창)…",
        ["ai.setup"] = "AI/AI 기능 고르기·받기…", ["view.spread"] = "보기/펼침면 미리보기 (책처럼 보기)", ["anim.export"] = "애니/애니메이션 내보내기 (GIF·WebP·APNG·MP4·PNG)…",
        ["adjlayer.levels"] = "보정/조정 레이어: 레벨 (밝기 범위)", ["adjlayer.curves"] = "보정/조정 레이어: 톤 커브 (곡선)", ["adjlayer.hsl"] = "보정/조정 레이어: 색조∕채도∕명도 (HSL)",
        ["adjlayer.balance"] = "보정/조정 레이어: 색상 균형", ["adjlayer.bc"] = "보정/조정 레이어: 밝기∕대비", ["adjlayer.gradmap"] = "보정/조정 레이어: 그라데이션 맵",
        ["adjlayer.posterize"] = "보정/조정 레이어: 포스터화", ["adjlayer.threshold"] = "보정/조정 레이어: 2값화 (한계값)", ["adjlayer.invert"] = "보정/조정 레이어: 반전",
        ["adjlayer.bw"] = "보정/조정 레이어: 흑백", ["adjlayer.colorize"] = "보정/조정 레이어: 단색 입히기 (세피아 등)", ["adjlayer.vibrance"] = "보정/조정 레이어: 자연 채도",
    };

    /// <summary>네이티브 위 메뉴 이름 → 웹 메뉴 id (네이티브에만 있는 항목을 붙일 곳)</summary>
    static readonly Dictionary<string, string> NativeTop = new()
    {
        ["파일"] = "file", ["편집"] = "edit", ["레이어"] = "layer", ["선택"] = "select", ["만화"] = "comic", ["필터"] = "filter", ["보정"] = "adjust",
        ["보기"] = "view", ["AI"] = "ai", ["도움말"] = "help", ["애니"] = "anim", ["에셋"] = "asset", ["자"] = "ruler",
    };

    /// <summary>웹 id → 네이티브 메뉴 경로 (없으면 null)</summary>
    public static string Resolve(string id)
    {
        if (id == null) return null;
        if (Extra.TryGetValue(id, out var p)) return p != null && Registry.Commands.ContainsKey(p) ? p : null;
        return KeyMap.ResolveCmd(id) ?? WebIds.Resolve(id);
    }

    public static string Clean(string label) => label.Replace('∕', '/');

    static List<MenuTop> cache; static int cacheCount = -1;
    /// <summary>메뉴 표 (등록된 네이티브 메뉴가 바뀌면 다시 만듦)</summary>
    public static List<MenuTop> Tree()
    {
        if (cache != null && cacheCount == Registry.Menus.Count) return cache;
        var tops = new List<MenuTop>();
        var used = new HashSet<string>();
        foreach (var (id, label, items) in Web)
        {
            var list = new List<MenuNode>();
            foreach (var raw in items)
            {
                if (raw == "-") { list.Add(new MenuNode { Sep = true }); continue; }
                int bar = raw.IndexOf('|');
                string key = raw[..bar], lab = raw[(bar + 1)..];
                var n = new MenuNode { Key = key, Label = lab };
                if (key.StartsWith("@"))
                {
                    if (NameTools.TryGetValue(key, out var tool)) { string t = tool; n.Run = () => App.SetTool(t); n.RunEnabled = () => Registry.Tools.Any(x => x.Id == t); }
                    else
                    {
                        // "— 조정 레이어 만들기 —" 같은 제목 줄: 네이티브에 같은 이름 항목이 있으면 그것
                        n.Path = Registry.Menus.FirstOrDefault(m => m.Path.EndsWith("/" + lab))?.Path;
                    }
                }
                else n.Path = Resolve(key);
                if (n.Path != null) used.Add(n.Path);
                list.Add(n);
            }
            tops.Add(new MenuTop(id, label, list));
        }
        // 네이티브에만 있는 항목: 알맞은 메뉴 끝에 (네이티브 메뉴 경로 = 키)
        // (같은 메뉴에 같은 이름의 웹 항목이 이미 있으면 같은 기능을 다른 경로로 등록한 것이라 뺌)
        static string Norm(string s) => Clean(s).TrimEnd('…').Trim();
        var extra = Registry.Menus.Where(m => m.Run != null && !m.Path.Split('/')[^1].StartsWith("-") && !used.Contains(m.Path)).OrderBy(m => m.Order).ToList();
        foreach (var grp in extra.GroupBy(m => m.Path.Split('/')[0]))
        {
            string wid = NativeTop.TryGetValue(grp.Key, out var w) ? w : grp.Key;
            var top = tops.Find(t => t.Id == wid);
            if (top == null) { top = new MenuTop(wid, grp.Key, new List<MenuNode>()); tops.Add(top); }
            var have = top.Items.Where(n => !n.Sep).Select(n => Norm(n.Label)).ToHashSet();
            var rest = grp.Where(m => !have.Contains(Norm(m.Path.Split('/')[^1]))).ToList();
            if (rest.Count == 0) continue;
            if (top.Items.Count > 0) top.Items.Add(new MenuNode { Sep = true });
            foreach (var m in rest)
            {
                var parts = m.Path.Split('/');
                var into = top.Items;
                for (int i = 1; i < parts.Length - 1; i++)
                {
                    string subPath = string.Join("/", parts.Take(i + 1));
                    var sub = into.Find(x => x.Sub != null && x.Key == "sub:" + subPath);
                    if (sub == null) { sub = new MenuNode { Key = "sub:" + subPath, Label = Clean(parts[i]), Sub = new List<MenuNode>(), Native = true }; into.Add(sub); }
                    into = sub.Sub;
                }
                into.Add(new MenuNode { Key = m.Path, Label = Clean(parts[^1]), Path = m.Path, Native = true });
            }
        }
        cache = tops; cacheCount = Registry.Menus.Count;
        return tops;
    }

    public static void Invalidate() => cache = null;

    /// <summary>웹 판 메뉴 단축키 글자 (ctrl+shift+s → Ctrl+Shift+S, delete → delete)</summary>
    public static string KeyLabel(Keys k)
    {
        string s = KeyMap.Format(k);
        if (s.Length == 0) return "";
        s = s.Replace("ctrl", "Ctrl").Replace("shift", "Shift").Replace("alt", "Alt");
        int p = s.LastIndexOf('+');
        if (p >= 0 && p == s.Length - 2 && char.IsLetterOrDigit(s[^1])) s = s[..^1] + char.ToUpperInvariant(s[^1]);
        return s;
    }

    /// <summary>점검: 네이티브 대상이 없는 웹 항목·메뉴 끝에 붙은 네이티브 항목 (--selftest menus)</summary>
    public static int Report(string dir)
    {
        Directory.CreateDirectory(dir);
        var sb = new System.Text.StringBuilder();
        var tree = Tree();
        int missing = 0;
        sb.AppendLine("## 네이티브 대상이 없는 웹 메뉴 항목");
        foreach (var t in tree)
            foreach (var n in t.Items.Where(n => !n.Sep && !n.Native && n.Path == null && n.Run == null))
            { missing++; sb.AppendLine($"{t.Id}\t{n.Key}\t{n.Label}"); }
        sb.AppendLine();
        sb.AppendLine("## 메뉴 끝에 붙인 네이티브 전용 항목");
        void Walk(string top, List<MenuNode> items, string ind)
        {
            foreach (var n in items.Where(n => n.Native))
            {
                sb.AppendLine($"{top}\t{ind}{n.Label}\t{n.Path}");
                if (n.Sub != null) Walk(top, n.Sub, ind + "  ");
            }
        }
        foreach (var t in tree) Walk(t.Id, t.Items, "");
        sb.AppendLine();
        sb.AppendLine("## 메뉴 순서");
        sb.AppendLine(string.Join(" ", tree.Select(t => t.Label)));
        File.WriteAllText(Path.Combine(dir, "menus.txt"), sb.ToString());
        return 0;
    }
}
