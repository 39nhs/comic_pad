// 웹 판 명령 id → 네이티브 메뉴 경로. 세팅 프리셋·.cpset·원형 메뉴·아래 막대·간단히 모드 메뉴 고르기·자동 액션에서
// 웹 판 id 를 그대로 쓸 수 있게 KeyMap.CmdAlias 에 합친다 (기능 대조표 native/기능_대조표.md 의 근거이기도 함).
using ComicPadNative.Modules.Extras;
using ComicPadNative.UI;

namespace ComicPadNative.Modules.Shell2;

public static class WebIds
{
    /// <summary>웹 id → 네이티브 메뉴 경로 (이 모듈이 새로 만든 메뉴 포함)</summary>
    public static readonly Dictionary<string, string> Map = new()
    {
        // 파일
        ["file.new"] = "파일/새로 만들기…", ["file.open"] = "파일/열기…", ["file.openLocal"] = "파일/열기…", ["file.save"] = "파일/저장", ["file.saveAs"] = "파일/다른 이름으로 저장…",
        ["file.import"] = "파일/이미지 가져오기 (새 레이어)…", ["file.export"] = "파일/그림으로 내보내기 (PNG·JPG)…", ["file.exportQuick"] = "파일/PNG 빠른 내보내기",
        ["file.recover"] = "파일/최근 작품·자동 저장…", ["file.versions"] = "파일/버전 비교 (이전 저장본과)…", ["file.folder"] = "파일/작업물 폴더 열기", ["file.exportsFolder"] = "파일/내보낸 이미지 폴더 열기",
        ["file.exportPSD"] = "파일/PSD로 저장 (레이어 유지)…", ["file.exportSlices"] = "파일/웹툰 분할 내보내기…", ["file.exportLayers"] = "파일/레이어별 PNG 내보내기…",
        ["file.backup"] = "파일/작업물 백업 (자동·지금·꺼내기)…", ["file.ebook"] = "파일/전자책 내보내기 (EPUB / CBZ)…", ["file.printExport"] = "파일/인쇄용 내보내기 (PDF ∕ 1비트 TIFF)…",
        ["file.webtoonExport"] = "파일/웹툰 플랫폼 규격으로 내보내기…", ["feedback.export"] = "파일/피드백 파일 보내기 (러프·메모만)…", ["feedback.import"] = "파일/피드백 파일 받기…",
        ["brush.importAbr"] = "파일/포토샵 브러시(.abr) 가져오기…", ["pixel.newDoc"] = "파일/픽셀 아트 새로 만들기…", ["font.add"] = "파일/글꼴 파일 추가 (.ttf∕.otf)…",
        ["asset.saveLayer"] = "파일/에셋/현재 레이어를 에셋으로 저장…", ["asset.saveSelection"] = "파일/에셋/선택 영역을 에셋으로 저장…", ["asset.saveMerged"] = "파일/에셋/보이는 그림 전체를 에셋으로 저장…", ["asset.folder"] = "파일/에셋/에셋 폴더 열기",
        ["settings.open"] = "파일/설정…",
        // 편집
        ["edit.undo"] = "편집/실행취소", ["edit.redo"] = "편집/다시실행", ["edit.redo2"] = "편집/다시실행", ["edit.copy"] = "편집/복사", ["edit.cut"] = "편집/잘라내기", ["edit.paste"] = "편집/붙여넣기",
        ["edit.clear"] = "편집/지우기 (선택 영역)", ["edit.clear2"] = "편집/지우기 (선택 영역)", ["edit.fill"] = "편집/전경색으로 채우기", ["edit.transform"] = "편집/이동·변형",
        ["edit.flipH"] = "편집/레이어 좌우 반전", ["edit.flipV"] = "편집/레이어 상하 반전", ["edit.canvasSize"] = "편집/캔버스 크기…", ["edit.extendDown"] = "편집/웹툰: 아래로 캔버스 늘리기",
        ["edit.meshWarp"] = "편집/원근·격자 왜곡 (여러 면)", ["edit.puppet"] = "편집/퍼펫 왜곡 (핀으로 휘기)", ["macro.open"] = "편집/자동 액션 (기록·실행·일괄)…", ["macro.record"] = "편집/자동 액션 기록 시작/멈추기",
        ["color.swap"] = "편집/전경∕배경색 바꾸기",
        // 다른 모듈(Vision·Media)이 만든 메뉴 — 웹 id 로도 부를 수 있게 (Gaps 작업에서 더함)
        ["edit.contentAwareFill"] = "편집/내용 인식 채우기 (선택한 곳을 주변으로 메우기)…", ["edit.heal"] = "편집/복구 브러시 (잡티 지우기)",
        ["ai.pose"] = "AI/사진에서 인형 자세 (3D 소재 창)…", ["ai.img2img"] = "AI/이미지 AI 로 그리기 (이 PC 의 SD WebUI)…", ["ai.check"] = "AI/이미지 AI 연결 확인",
        ["ai.setup"] = "AI/AI 기능 고르기·받기…", ["view.spread"] = "보기/펼침면 미리보기 (책처럼 보기)",
        // 레이어
        ["layer.new"] = "레이어/새 레이어", ["layer.newRough"] = "레이어/새 러프 레이어 (의견 색, 밑그림)", ["layer.newLine"] = "레이어/새 펜선 레이어", ["layer.newColor"] = "레이어/새 채색 레이어 (아래 레이어에 클리핑)",
        ["layer.newVector"] = "레이어/새 벡터 레이어 (선 굵기·지우기 나중에 수정)", ["vector.width"] = "레이어/벡터: 선 굵기 일괄 조정…", ["vector.color"] = "레이어/벡터: 선 색을 지금 색으로", ["vector.toRaster"] = "레이어/벡터를 일반 레이어로",
        ["bubble.toRaster"] = "레이어/말풍선을 그림으로 (직접 그리기 가능, 다시 편집 불가)", ["sfx.toPixels"] = "레이어/효과음을 그림으로 바꾸기",
        ["layer.rough"] = "레이어/러프 전용 레이어로 지정∕해제", ["layer.roughColor"] = "레이어/러프 색 바꾸기…", ["layer.duplicate"] = "레이어/복제", ["layer.delete"] = "레이어/삭제",
        ["layer.mergeDown"] = "레이어/아래로 병합", ["layer.flatten"] = "레이어/보이는 레이어 결합", ["layer.clear"] = "레이어/레이어 내용 지우기", ["layer.up"] = "레이어/위로", ["layer.down"] = "레이어/아래로",
        ["layer.newFolder"] = "레이어/새 폴더", ["layer.group"] = "레이어/폴더로 묶기", ["layer.mergeFolder"] = "레이어/폴더를 한 레이어로 합치기", ["layer.clip"] = "레이어/클리핑",
        ["layer.alphaLock"] = "레이어/투명 픽셀 잠금", ["layer.lock"] = "레이어/잠금", ["layer.draft"] = "레이어/밑그림 (내보내기·채우기에서 빼기)", ["layer.tint"] = "레이어/레이어 컬러 (파란 선으로 보기)",
        ["layer.selectAlpha"] = "레이어/레이어의 그림 부분을 선택", ["layer.addMask"] = "레이어/레이어 마스크 추가·켜기/끄기", ["layer.editMask"] = "레이어/마스크/마스크 편집 ↔ 그림 편집",
        ["layer.maskOn"] = "레이어/마스크/마스크 켜기∕끄기", ["layer.applyMask"] = "레이어/마스크/마스크 적용 (그림에 반영)", ["layer.deleteMask"] = "레이어/마스크/마스크 삭제", ["layer.ref"] = "레이어/참조 레이어 (채우기 기준)",
        ["layer.fx"] = "레이어/레이어 효과 (테두리∕그림자∕광선)…", ["layer.tone"] = "레이어/톤화 (망점으로 보이기)…", ["layer.editAdjust"] = "레이어/조정 레이어 값 수정",
        // 선택
        ["select.all"] = "선택/전체 선택", ["select.none"] = "선택/선택 해제", ["select.invert"] = "선택/선택 반전", ["select.grow"] = "선택/선택 확장 (+4px)", ["select.shrink"] = "선택/선택 축소 (-4px)",
        ["select.growBy"] = "선택/선택 확장∕축소…", ["select.toLayer"] = "선택/선택 영역을 새 레이어로 복사", ["select.refine"] = "선택/선택 다듬기 (매끄럽게·페더·대비·경계 이동)…",
        ["select.refineBrush"] = "선택/머리카락 다듬기 브러시", ["select.feather"] = "선택/선택 경계 흐리기…", ["select.colorRange"] = "선택/색역 선택 (같은 색 전부)…",
        ["path.tool"] = "선택/펜 도구 (베지어 패스)", ["path.toSelection"] = "선택/패스/패스 → 선택 영역", ["path.fromSelection"] = "선택/패스/선택 영역 → 패스", ["path.stroke"] = "선택/패스/패스 따라 선 그리기 (지금 브러시)", ["path.fill"] = "선택/패스/패스 칠하기 (전경색)",
        // 보기
        ["view.fit"] = "보기/화면에 맞추기", ["view.fitWidth"] = "보기/가로 폭에 맞춤", ["view.actual"] = "보기/100%", ["view.zoomIn"] = "보기/확대", ["view.zoomOut"] = "보기/축소", ["view.zoomIn2"] = "보기/확대", ["view.zoomOut2"] = "보기/축소",
        ["view.rotL"] = "보기/왼쪽으로 돌리기", ["view.rotR"] = "보기/오른쪽으로 돌리기", ["view.resetRotation"] = "보기/회전 초기화", ["view.flip"] = "보기/좌우 반전",
        ["view.toggleUI"] = "보기/패널 모두 숨기기∕보이기", ["view.showSide"] = "보기/패널 열기", ["view.toggleSide"] = "보기/오른쪽 패널 보이기∕숨기기", ["view.fullscreen"] = "보기/전체 화면", ["view.grid"] = "보기/격자 보기",
        ["view.symmetry"] = "보기/대칭 그리기 (좌우)", ["view.symmetryRadial"] = "보기/방사 대칭 (만다라)…", ["view.symmetryOff"] = "보기/대칭 끄기", ["view.quickbar"] = "보기/퀵바 보이기∕숨기기",
        ["view.bottombar"] = "보기/아래 막대 보이기∕숨기기", ["view.bottombarEdit"] = "보기/아래 막대 버튼 고르기…", ["view.reference"] = "보기/참조 이미지 창 열기…", ["view.luma"] = "보기/명도 확인 보기 (끔 → 흑백 → 5단계)",
        ["view.cmykProof"] = "보기/인쇄 색 미리보기 (CMYK)…", ["view.webtoon"] = "만화/웹툰 미리보기 (휴대폰 폭)", ["paper.texture"] = "보기/용지 질감 (종이 결)…",
        ["pixel.grid"] = "보기/픽셀 격자 (많이 확대했을 때)", ["pixel.crisp"] = "보기/픽셀아트로 보기 (축소해도 또렷하게, 이 작품)",
        ["ruler.snap"] = "보기/자/자에 맞추기", ["ruler.p1"] = "보기/자/1점 투시 자", ["ruler.p2"] = "보기/자/2점 투시 자", ["ruler.p3"] = "보기/자/3점 투시 자", ["ruler.parallel"] = "보기/자/평행선 자",
        ["ruler.radial"] = "보기/자/방사선 자 (집중선용)", ["ruler.concentric"] = "보기/자/동심원 자", ["ruler.fromPhoto"] = "만화/사진·배경에서 퍼스 자 만들기 (소실점 자동)",
        ["timelapse.toggle"] = "보기/타임랩스 기록 켜기/끄기", ["timelapse.export"] = "보기/타임랩스 동영상 만들기…", ["timelapse.clear"] = "보기/타임랩스 장면 지우기…",
        ["workspace.save"] = "보기/작업 화면 저장…", ["workspace.manage"] = "보기/작업 화면 바꾸기·관리…",
        ["ui.customize"] = "보기/화면 꾸미기 (도구·메뉴·패널)…", ["ui.mode"] = "보기/간단히 ↔ 모든 기능 보기", ["ui.radial"] = "보기/원형 메뉴 열기", ["ui.radialEdit"] = "보기/원형 메뉴 편집…",
        ["popout.panel"] = "보기/패널을 앱 밖 창으로…", ["popout.settings"] = "보기/환경 설정을 앱 밖 창으로", ["settings.fingerDraw"] = "보기/손가락으로 그리기 켜기∕끄기",
        // 필터·보정
        ["filter.lineExtract"] = "필터/선화 추출 (흰 배경 → 투명)", ["filter.gray"] = "필터/흑백", ["filter.invert"] = "필터/색 반전", ["filter.mono"] = "필터/2값화 (흑백 선화 정리)…", ["filter.blur"] = "필터/흐리게…",
        ["filter.adjust"] = "필터/밝기∕대비∕채도…", ["filter.outline"] = "필터/외곽선 두르기 (스티커∕에셋)…", ["filter.shadow"] = "필터/그림자 넣기…", ["filter.colorize"] = "필터/단색으로 바꾸기 (선 색 변경)",
        ["filter.unsharp"] = "필터/언샤프 마스크 (선명하게)…", ["filter.noise"] = "필터/노이즈 추가…", ["filter.mosaic"] = "필터/모자이크…", ["filter.motion"] = "필터/방향 흐림 (모션 블러)…", ["filter.zoomblur"] = "필터/방사형 흐림 (줌)…",
        ["filter.glowBlur"] = "필터/빛 번짐 (블룸)…", ["filter.lineClean"] = "필터/선 정리 (잡티·끊김·울퉁불퉁)…", ["pixel.quantize"] = "필터/색 수 줄이기 (팔레트·디더링)…", ["pixel.replaceColor"] = "필터/팔레트 색 바꾸기 (같은 색 한꺼번에)…",
        ["pixel.lock"] = "필터/팔레트 색으로만 칠하기 (이 작품)", ["pixel.paletteImport"] = "필터/팔레트 가져오기 (.gpl∕.hex)…", ["pixel.paletteExport"] = "필터/팔레트 내보내기 (.gpl∕.hex)…",
        ["adjust.levels"] = "보정/레벨 (밝기 범위)…", ["adjust.curves"] = "보정/톤 커브 (곡선)…", ["adjust.hsl"] = "보정/색조∕채도∕명도 (HSL)…", ["adjust.balance"] = "보정/색상 균형…", ["adjust.bc"] = "보정/밝기∕대비…",
        ["adjust.vibrance"] = "보정/자연 채도…", ["adjust.gradmap"] = "보정/그라데이션 맵…", ["adjust.colorize"] = "보정/단색 입히기 (세피아 등)…", ["adjust.bw"] = "보정/흑백…", ["adjust.posterize"] = "보정/포스터화…",
        ["adjust.threshold"] = "보정/2값화 (한계값)…", ["adjust.invert"] = "보정/반전…",
        // 만화
        ["panel.frame"] = "만화/페이지 컷 만들기…", ["panel.templates"] = "만화/컷 나누기 템플릿…", ["sfx.tool"] = "만화/효과음 글자 도구", ["memo.tool"] = "만화/메모 핀 도구", ["effect.flash"] = "만화/플래시 효과 (우니∕베타)…",
        ["print.setup"] = "만화/원고 설정 (재단선·도련·안전선)…", ["print.toggle"] = "만화/원고 가이드 보이기/숨기기", ["print.preflight"] = "만화/인쇄·투고 전 점검…",
        ["color.autoFlat"] = "만화/밑색 한 번에 칠하기 (선화 영역마다)…", ["color.hintLayer"] = "만화/색 힌트 레이어 만들기 (색 점 찍기)", ["color.hintFill"] = "만화/색 점으로 칠하기 (힌트대로 밑색)…",
        ["story.new"] = "만화/새 작품 (여러 페이지)…", ["story.next"] = "만화/다음 페이지", ["story.prev"] = "만화/이전 페이지", ["story.add"] = "만화/페이지 추가", ["story.dup"] = "만화/현재 페이지 복제", ["story.export"] = "만화/작품 전체 내보내기…",
        ["progress.open"] = "만화/진행표 (단계·작업 시간·마감)…", ["3d.open"] = "만화/3D 소재 (데생 인형·모델 합성)…",
        // 애니
        ["anim.create"] = "애니/애니메이션 만들기 (빈 프레임 3장)", ["anim.track"] = "애니/트랙 추가 (레이어처럼 따로 움직이는 줄)", ["anim.fromLayers"] = "애니/최상위 레이어들을 프레임으로 바꾸기",
        ["anim.add"] = "애니/빈 프레임 추가", ["anim.dup"] = "애니/현재 프레임 복제", ["anim.link"] = "애니/연결된 프레임 추가 (같은 그림 이어 쓰기)", ["anim.unlink"] = "애니/칸 연결 끊기 (따로 고치기)",
        ["anim.clearCell"] = "애니/칸 비우기", ["anim.delete"] = "애니/프레임 삭제", ["anim.prev"] = "애니/이전 프레임", ["anim.next"] = "애니/다음 프레임", ["anim.play"] = "애니/재생 / 정지",
        ["anim.onion"] = "애니/어니언 스킨 켜기/끄기", ["anim.light"] = "애니/라이트 테이블에 올리기/내리기 (지금 칸)", ["anim.tag"] = "애니/태그(구간) 만들기…", ["anim.timesheet"] = "애니/타임시트 (노출표) 보기·고치기…",
        ["anim.export"] = "애니/애니메이션 내보내기 (GIF·WebP·APNG·MP4·PNG)…", ["anim.gifQuick"] = "애니/움짤 만들기 (바로, GIF)", ["anim.motion"] = "애니/모션 경로 도구 (트윈)", ["anim.bake"] = "애니/모션을 그림으로 굳히기",
        ["anim.sheet"] = "애니/스프라이트 시트 내보내기 (PNG + JSON)…", ["anim.importSheet"] = "애니/스프라이트 시트 가져오기…", ["anim.importAse"] = "애니/Aseprite 파일 열기 (.aseprite/.ase)…", ["anim.exportAse"] = "애니/Aseprite 파일로 저장 (.aseprite)",
        // AI
        ["ai.lineart"] = "AI/AI 선화 추출…", ["ai.cutout"] = "AI/AI 캐릭터·배경 따기…", ["ai.select"] = "AI/AI 고르기 (탭)", ["ai.depth"] = "AI/AI 깊이 추정…", ["ai.upscale"] = "AI/AI 크기 키우기…", ["ai.models"] = "AI/AI 모델 관리…",
        ["vt.studio"] = "AI/버튜버 스튜디오 (웹캠·마이크로 캐릭터 움직이기)…", ["vt.setup"] = "AI/버튜버 아바타 만들기 (레이어를 부위로)…", ["vt.open"] = "AI/버튜버 아바타 파일 열기 (.cpvt)…",
        // 도움말
        ["palette.open"] = "도움말/기능 검색…", ["help.manual"] = "도움말/ComicPad 설명서", ["help.about"] = "도움말/ComicPad 정보", ["help.features"] = "도움말/기능 안내 (무엇을 어디서)…",
        ["help.keys"] = "도움말/단축키 ∕ 제스처 안내", ["help.report"] = "도움말/문제 보내기 (오류 보고)…", ["help.update"] = "도움말/업데이트 확인…", ["settings.keys"] = "도움말/단축키·도구 키 바꾸기…",
        ["pen.diagnose"] = "도움말/펜 필기감 진단 (이 기기·이 펜에 맞추기)…", ["setup.choose"] = "도움말/초기 세팅 프리셋 고르기 (클립스튜디오식·포토샵식·웹툰·태블릿)…", ["setup.undo"] = "도움말/세팅 되돌리기 (마지막 프리셋·가져오기 전으로)",
        ["setup.export"] = "도움말/내 세팅 내보내기 (.cpset)…", ["setup.import"] = "도움말/세팅 가져오기 (.cpset)…", ["selftest.run"] = "도움말/자동 점검 (주요 기능 시험)",
    };

    /// <summary>웹 판 메뉴 id → 네이티브 위 메뉴 이름</summary>
    public static readonly Dictionary<string, string> MenuName = new()
    {
        ["file"] = "파일", ["edit"] = "편집", ["layer"] = "레이어", ["select"] = "선택", ["comic"] = "만화", ["adjust"] = "보정", ["filter"] = "필터", ["ai"] = "AI", ["view"] = "보기", ["help"] = "도움말", ["anim"] = "애니",
    };
    public static string MenuId(string nativeTop) => MenuName.FirstOrDefault(kv => kv.Value == nativeTop).Key ?? nativeTop;

    /// <summary>웹 id·네이티브 경로 어느 쪽이든 → 실행 (없으면 false)</summary>
    public static bool Run(string id)
    {
        string path = Resolve(id);
        if (path == null || !Registry.Commands.TryGetValue(path, out var run) || run == null) return false;
        var def = Registry.Menus.Find(m => m.Path == path);
        if (def?.Enabled != null && !def.Enabled()) { App.Toast("지금은 쓸 수 없습니다: " + Label(path)); return true; }
        MainForm.Run(run, path);
        return true;
    }

    public static string Resolve(string id) => id == null ? null : Registry.Commands.ContainsKey(id) ? id : Map.TryGetValue(id, out var p) && Registry.Commands.ContainsKey(p) ? p : null;
    public static string WebIdOf(string path) => Map.FirstOrDefault(kv => kv.Value == path).Key;
    public static string Label(string path) => path == null ? "" : System.Text.RegularExpressions.Regex.Replace(path[(path.LastIndexOf('/') + 1)..], @"\s*\(.*?\)\s*", "").TrimEnd('…');

    /// <summary>KeyMap 별칭에 합치기 (이미 있는 것은 그대로)</summary>
    public static void Install()
    {
        foreach (var (k, v) in Map) KeyMap.CmdAlias.TryAdd(k, v);
    }
}
