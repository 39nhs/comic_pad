# ComicPad Native (시제품 · 단계 0)

웹 화면을 띄우지 않는 **별개의 네이티브 앱** 시제품. 계획은 `../네이티브_재작성_계획.md`.

## 들어 있는 것
- .NET 8 + WinForms 창 (외부 패키지 없음), 모니터마다 DPI 맞춤
- **펜 입력을 Windows 에서 직접** (WM_POINTER): 지난번 이후 모든 점, 1픽셀보다 작은 위치(HIMETRIC), 필압 0~1024, 지우개 끝.
  펜 길게 눌러 오른쪽 클릭·탭 물결 꺼짐. 마우스도 같은 길로, 손가락 1개 = 화면 이동, 2개 = 확대
- **직접 짠 그리기 엔진**: 웹 판의 선 만들기(1€ 손떨림 보정·2차 곡선·간격 찍기)를 옮기고,
  원을 픽셀마다 "덮인 정도" 로 계산해 한 획 안에서는 최댓값만 남김 → 선뭉침이 구조적으로 없음
- 그린 부분만 다시 그려 바로 화면에 (GDI), 64px 타일 실행취소
- 제목 표시줄: 펜 점/초 · 필압 · **입력→화면 지연(ms)** — 클립스튜디오·웹 판과 비교용

## 단축키
`[` `]` 크기 · `E` 지우개 · `B` 펜 · `Ctrl+Z` / `Ctrl+Y` · `Ctrl+0` 맞춤 · `Ctrl+1` 100% · 휠 확대 · `Space`+끌기 / 가운데 버튼 이동 · `Ctrl+S` PNG 저장(사진 폴더)

## 만들기 · 실행
```
cd native/ComicPadNative
dotnet build -c Release
bin/Release/net8.0-windows/ComicPadNative.exe
```
(.NET 8 데스크톱 런타임 필요. 배포할 때는 self-contained 로 묶거나 런타임 설치를 설치 파일에 넣기)

## 첫 측정 (2026-11-12, Windows 합성 펜 입력)
- 펜 점 120개 모두 그려짐, 필압 끝으로 갈수록 가늘게, 뭉침 없음
- 입력(펜 점 시각) → 화면 올리기까지 앱 안 처리 **약 0.6ms** (모니터 새로 고침 시간은 모든 앱이 같음)

## 다음 (단계 0 마무리)
- 같은 S펜으로 클립스튜디오·웹 판 1.1.3 과 직접 비교 (느낌·지연·필압 곡선)
- 큰 원고(B4 600dpi)에서 그리기·확대 속도
- 계속하기로 하면 단계 1 (레이어·합성·브러시 엔진 전부·.cpad 읽기/쓰기)

## 화면·명령 (Shell2 모듈) 시험
웹 판과의 기능 대조는 `기능_대조표.md`. 시험은 늘 임시 폴더로 (사용자 작업물·설정을 건드리지 않음):
```
set COMICPAD_DATA=%TEMP%\cps & set COMICPAD_SHELL2_SELFTEST=%TEMP%\cps-out
ComicPadNative.exe --render x y    → cps-out\result.txt (업데이트 서명·꾸러미, 웹 id, 자동 액션, 작업 화면, 레이어 명령, 패스 …)
set COMICPAD_SETTINGS=%TEMP%\cps\settings.json & set COMICPAD_SHELL2_GUITEST=%TEMP%\cps-gui
ComicPadNative.exe                  → 창을 띄워 퀵바·원형 메뉴·간단히 모드·패널 꺼내기 등을 해 보고 닫음 (gui-result.txt)
```
GUI 시험의 설정 파일에는 `{"setupDone":"csp"}` 를 미리 넣어 둔다 (처음 세팅 창이 뜨지 않게).

## 웹 판 ↔ 네이티브 판 (베타로 옮기기)
쓰던 사람은 보통 자동 업데이트로 옮긴다. 업데이트 꾸러미(`tools/make_release.py --native`)에 네이티브 판(`native/ComicPadNative/`, 자체 포함)과
스위치를 읽는 새 창 프로그램(`ComicPad.exe`)이 들어 있어, 예전(1.1.3) 업데이트 코드로도 그대로 깔린다. 깔린 뒤에도 바꾸기 전까지는 지금과 똑같이 웹 판.

- **스위치**: 설치 폴더 `data\config\app.json` = `{"app":"native"|"web", "by", "at"}`. 창 프로그램이 켤 때 읽음 (`launcher\AppSwitch.cs`).
  - `native` 이고 `native\ComicPadNative\ComicPadNative.exe` 가 있으면 서버·웹 창 없이 네이티브 판을 켜고 끝 (열 파일은 넘김)
  - 켤 때 **Shift** 를 누르고 있거나 `--web` → 이번만 웹 판. `--native` → 이번만 네이티브 판. 파일이 없거나 켜자마자 오류로 꺼지면 알리고 웹 판
  - `ComicPad.exe --which 결과.txt` → 고르기만 해서 적고 끝 (시험용)
- **옮기기**: 웹 판 도움말 > **새 네이티브 판 써 보기 (베타)…** (`app.switchNative`): 설명 → 저장 확인 → `/api/app/switch` 가 스위치를 쓰고 네이티브 판을 켬(열던 작품과 함께) → 웹 창을 닫음.
  네이티브 판으로 되어 있는데 웹 판이 열리면(Shift 등) 알림에서 "네이티브 판으로 / 앞으로 웹 판" 을 고름.
- **돌아가기**: 네이티브 판 도움말 > **웹 판으로 돌아가기…** (`app.switchWeb`, 간단히 모드에서도 늘 보임): 저장 확인 → 스위치 `web` → `ComicPad.exe --web` 을 켜고 닫음.
  업데이트 되돌리기(1.1.3 으로)를 해도 옛 창 프로그램은 스위치를 읽지 않으므로 늘 웹 판.
- **설정 가져오기** (`Modules/Shell2/AppSwitch.cs` `WebImport`): 웹 판 설정의 원본은 `data\config\settings.json` (WebView 의 localStorage 는 사본이라 읽지 않음).
  처음 켤 때 한 번 저절로(모듈·화면을 만들기 전에), 또는 도움말 > **웹 판 설정 가져오기…** (`app.importWebSettings`, 세팅 되돌리기로 되돌릴 수 있음). 키 이름이 같아 값을 그대로 옮긴다:

  | 묶음 | 키 |
  |---|---|
  | 펜·필기감 | pressureGamma, pressurePts, penFeel, mousePressure, penNoPressure, penButton, penButton2, holdLine, brushCursor, fingerDraw, palmGuardMs, touchRotate, twoFingerUndo, threeFingerRedo, longPressEyedropper, wheelMode, mouseRadial |
  | 브러시 | brushes, activePreset, vectorErase |
  | 단축키 | keys (웹 명령 id 그대로 — WebIds/KeyMap 이 풀어 줌), toolKeys |
  | 화면 구성 | ui (간단히/모든 기능·도구·메뉴 고르기), quickbar, bottombar, bottomMigrated, radialItems, largeUI, leftHanded, sideVisible, dockSplit, dock_top, dock_bottom, workspaces, popoutOnTop |
  | 색·팔레트 | palette, recentColors, roughColor |
  | 보기·자·대칭 | showGrid, gridSize, symmetry, symMirror, symN, rulerSnap, pixelGrid, showRulers, showNotes, showCamera, hintGap |
  | 처음 세팅·새 작품 | setupDone, startDoc, lastNew, panelTemplates |
  | 작업·내보내기 기본값 | autosaveMin, animExport, animScrub, webtoonExport, webtoonPreviewW, sliceW, sliceH, printGuides, printIntent, printProfile, bookRtl, ebookRtl, ebookAuthor, userName, scriptDraft, macros, lastAssetFolder, last3D, cafSrc, cafOut |
  | AI | aiUrl, aiPrompt, aiNeg, aiDen, aiPick, aiCpuOnly |
  | (server.json) | backup.on · backup.keep → 네이티브 `backup` (아직 없을 때만) |

  옮기지 않음: nativePen·penApi·lowLatency (웹 창 펜 입력 경로), memSave, feelV, aiFeatures·aiIdleSec·aiTimeoutSec (웹 내장 AI 서버), recentCommands (웹 명령 기록), setupBackup, 그리고 표에 없는 키.
  **덮어쓰지 않기**: 가져온 값마다 지문을 `webImport.keys` 에 남김. 다시 가져올 때 네이티브 값이 그 지문과 같으면(안 바꿨으면) 웹 판 새 값으로, 다르면(네이티브에서 바꿨으면) 그대로 — 가져오기 창에서 "덮어쓰기" 를 골랐을 때만 바꿈.
- **data 같이 쓰기**: 작품(`projects`)·에셋·글꼴·백업(`backups`, 같은 이름 규칙)·버전 기록(`versions/작품/저장시각.cpad`, 두 판 모두 덮어쓸 파일의 마지막 저장 시각으로) 은 같은 곳.
  자동 저장은 판마다 따로: 웹 `이름.cpad`, 네이티브 `이름 (네이티브).cpad` (둘 다 어느 판에서나 열림). 네이티브도 작품 폴더에 저장할 때 웹 판 "프로젝트 열기" 카드용 미리보기 `이름.png` (긴 변 320) 를 씀.
  최근 파일: 웹 판은 작품 폴더를 시각순으로, 네이티브는 `recentFiles`(설정) — 서로 겹치지 않음.
- **시험**: `--selftest switch 결과폴더` (스위치·설정 가져오기·자동 저장 이름·버전 기록·미리보기·.cpad 왕복),
  `python tools/make_release.py --native --dry-run [--sim-launch]` → `tools/release_sim.py` 가 1.1.3 업데이트 코드로 임시 설치 폴더에 적용해 봄.
