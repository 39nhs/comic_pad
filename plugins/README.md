# ComicPad 플러그인 만들기

이 폴더의 `.js` 파일은 앱이 켜질 때 자동으로 불러옵니다 (이름순).
- 파일 이름이 `_` 로 시작하면 **불러오지 않습니다** → 잠깐 끄고 싶을 때 이름 앞에 `_` 붙이기.
- 고친 뒤에는 메뉴 **플러그인 → 플러그인 다시 불러오기** (= 새로고침).
- 오류가 나면 화면 아래에 빨간 알림이 뜨고, 나머지 앱은 그대로 동작합니다.

## 기본 모양

```js
export default function setup(app, kit) {
  // 1) 명령 + 메뉴
  app.registerCommand('my.hello', { label: '인사하기', run: () => app.toast('안녕!') });
  app.addMenuItem('plugin', { command: 'my.hello' });

  // 2) 단축키 (설정 파일 data/config/settings.json 의 "keys" 로 덮어쓸 수도 있음)
  app.registerCommand('my.clearBlue', { label: '파란색 지우기', key: 'ctrl+alt+b', run: () => {} });

  // 3) 퀵바 버튼 (퀵바 손잡이를 탭해서 켜기)
  app.registerQuickAction('my.hello', { icon: 'star', label: '인사', run: () => app.run('my.hello') });

  // 4) 선택 영역 메뉴 버튼
  kit.registerSelectionAction({ icon: 'star', label: '인사', run: () => app.run('my.hello') });

  return { name: '내 플러그인' };   // 플러그인 메뉴에 표시될 이름
}
```

## 도구 추가

```js
app.registerTool({
  id: 'mytool', name: '내 도구', icon: 'star', key: 'y', cursor: 'crosshair',
  down(e) { /* e.x, e.y (문서 좌표), e.sx, e.sy (화면), e.pressure, e.type('pen'|'mouse'|'touch'), e.shift */ },
  move(e) {},
  up(e) {},
  cancel() {},                         // 두 번째 손가락이 닿는 등 취소될 때
  overlay(ctx, view) {},               // 화면 위에 미리보기 그리기 (화면 좌표, view.toScreen(x,y))
  options(el) { el.append(kit.controls.slider('세기', { min: 0, max: 10, get: () => v, set: x => v = x })); },
});
```

## 그림 바꾸기 (실행취소 되게)

```js
const layer = app.doc.active;
if (!kit.ops.checkEditable(layer)) return;          // 잠김/숨김 확인
app.history.pixels(layer, '내 효과', (ctx) => {
  ctx.fillStyle = app.color.fg;
  ctx.fillRect(0, 0, 100, 100);
  return { x: 0, y: 0, w: 100, h: 100 };           // 바뀐 영역 (모르면 생략 = 레이어 전체)
});

// 픽셀 필터 (선택 영역 자동 반영)
kit.ops.applyPixelFilter('빨강 빼기', (img) => { for (let i = 0; i < img.data.length; i += 4) img.data[i] = 0; });

// 여러 작업을 한 번의 실행취소로
app.history.batch('묶음', () => { kit.ops.addLayer({ name: '새 레이어' }); /* ... */ });
```

## 자주 쓰는 것

| 이름 | 설명 |
|---|---|
| `app.doc` | 현재 문서. `width`, `height`, `layers`, `active`, `selection` |
| `layer.canvas / layer.ctx` | 레이어 픽셀 (문서 크기 캔버스) |
| `app.doc.selection` | `active`, `bounds`, `mask`(캔버스), `rect()`, `polygon()`, `clear()` |
| `app.color.fg / bg` | 현재 색 |
| `app.view` | `zoom`, `toDoc(sx,sy)`, `toScreen(x,y)`, `fit(doc)` |
| `app.bus.on(이벤트, fn)` | `tool:changed`, `layers:changed`, `color:changed`, `doc:changed`, `selection:changed` … |
| `app.ui.form(제목, 필드)` | 입력 대화상자 → `await` 하면 값 객체 (취소 시 null) |
| `app.toast(메시지)` | 짧은 알림 |
| `kit.ops` | `addLayer`, `deleteLayer`, `mergeDown`, `fillSelection`, `placeImage`, `applyCanvasFilter` … |
| `kit.raster` | `fillMask`(채우기 영역 계산), `dilate`, `alphaBounds`, `trimCanvas` |
| `kit.brush.Stroke` | 브러시 엔진 (필압/보정) |
| `kit.saveAsset(canvas, 이름, 폴더)` | data/assets 에 PNG 저장 |
| `kit.controls` | `slider`, `toggle`, `segmented`, `select`, `colorInput`, `button`, `row`, `section` |
| `kit.addIcon(이름, svgPath)` | 아이콘 추가 (24×24 선 아이콘) |

예제: `focuslines.js`(도구 추가), `screentone.js`(명령 + 선택 메뉴 + 퀵바).
전체 코드는 `app/js` 에 있습니다. 앱 자체를 고쳐도 됩니다.
