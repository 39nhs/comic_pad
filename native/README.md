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
