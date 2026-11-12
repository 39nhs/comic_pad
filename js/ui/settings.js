// 환경 설정 대화상자, 단축키, 도움말
import { app } from '../app.js';
import { h } from '../core/util.js';
import { slider, toggle, select, section, row, button, segmented } from './controls.js';
import { openModal } from './dialogs.js';
import { pressureCurve } from '../core/brush.js';
import { curveEditor } from './brushpreview.js';
import { keys as heldKeys } from '../core/input.js';
import { server } from '../core/io.js';

export function registerSettingsCommands() {
  app.registerCommand('settings.open', { label: '환경 설정…', run: openSettings });
  app.registerCommand('settings.fingerDraw', {
    label: '손가락으로 그리기 켜기/끄기',
    run: () => {
      app.settings.fingerDraw = !app.settings.fingerDraw;
      app.toast(app.settings.fingerDraw ? '손가락으로 그리기: 켜짐' : '손가락으로 그리기: 꺼짐 (손가락은 화면 이동/확대)');
      app.bus.emit('settings:changed');
      app.saveSettingsSoon();
    },
  });
  app.registerCommand('help.keys', { label: '단축키 / 제스처 안내', run: showHelp });
  // help.about (ComicPad 정보 · 라이선스) 은 ui/guide.js 에서
}

function pressureTester() {
  const c = h('canvas.pressure-test', { width: 520, height: 140 });
  const curve = h('canvas.pressure-curve', { width: 140, height: 140 });
  const drawCurve = () => {
    const x = curve.getContext('2d');
    x.clearRect(0, 0, 140, 140);
    x.strokeStyle = '#555'; x.strokeRect(0.5, 0.5, 139, 139);
    x.strokeStyle = '#e8a33d'; x.lineWidth = 2;
    x.beginPath();
    for (let i = 0; i <= 50; i++) { const p = i / 50; const y = pressureCurve(p); x.lineTo(p * 140, 140 - y * 140); }
    x.stroke();
  };
  const ctx = c.getContext('2d');
  let last = null;
  c.addEventListener('pointerdown', (e) => { c.setPointerCapture(e.pointerId); last = null; ctx.clearRect(0, 0, c.width, c.height); });
  c.addEventListener('pointermove', (e) => {
    if (!e.buttons) return;
    const r = c.getBoundingClientRect();
    const x = (e.clientX - r.left) * c.width / r.width, y = (e.clientY - r.top) * c.height / r.height;
    const p = e.pointerType === 'pen' ? e.pressure : 1;
    const w = 1 + 14 * pressureCurve(p);
    if (last) { ctx.lineWidth = w; ctx.lineCap = 'round'; ctx.strokeStyle = '#ddd'; ctx.beginPath(); ctx.moveTo(last.x, last.y); ctx.lineTo(x, y); ctx.stroke(); }
    last = { x, y };
    info.textContent = `${e.pointerType} · 필압 ${p.toFixed(2)} → ${pressureCurve(p).toFixed(2)}`;
  });
  const info = h('div.help', null, '여기에 펜으로 그려 보세요');
  drawCurve();
  return { el: h('div.pressure-box', null, h('div.row', null, c, curve), info), drawCurve };
}

/** 설정 화면 내용 (대화상자 또는 앱 밖 창에 넣음) */
export function settingsBody() {
  const s = app.settings;
  const save = () => { app.saveSettingsSoon(); app.bus.emit('settings:changed'); };
  const tester = pressureTester();
  const body = h('div.settings',
    null,
    section('펜 (S펜·와콤·휴이온·XP-Pen·서피스 펜·애플 펜슬)',
      curveEditor({
        title: '필압 곡선 (모든 브러시)',
        // 예전 감마 값만 있으면 그 모양을 점으로 바꿔서 보여 줌
        get: () => s.pressurePts || ((s.pressureGamma || 1) !== 1 ? [0, 0.25, 0.5, 0.75, 1].map(x => [x, +Math.pow(x, s.pressureGamma).toFixed(3)]) : null),
        set: (pts) => { s.pressurePts = pts; s.pressureGamma = 1; tester.drawCurve(); save(); },
      }).el,
      slider('가장 약한 힘 (이보다 약하면 0)', { min: 0, max: 0.5, step: 0.01, get: () => s.penFeel?.pmin ?? 0, set: v => { s.penFeel = { ...s.penFeel, pmin: v }; tester.drawCurve(); save(); }, format: v => Math.round(v * 100) + '%' }),
      slider('가장 센 힘 (이만큼만 눌러도 최대)', { min: 0.4, max: 1, step: 0.01, get: () => s.penFeel?.pmax ?? 1, set: v => { s.penFeel = { ...s.penFeel, pmax: v }; tester.drawCurve(); save(); }, format: v => Math.round(v * 100) + '%' }),
      tester.el,
      select('브러시 커서 모양', [['nib', '펜촉 + 크기 원 (클립스튜디오식)'], ['circle', '크기 원 + 십자'], ['shape', '브러시 끝 모양 (납작 펜·이미지·부드러움)'], ['cross', '십자만'], ['dot', '점만']], () => s.brushCursor || 'nib', v => { s.brushCursor = v; save(); app.applyCursor?.(); app.requestRender(); }),
      button('펜 필기감 진단 (이 기기·이 펜에 맞추기)…', () => app.run('pen.diagnose')),
      toggle('필압 없이 쓰기 (필압을 못 보내는 펜: 굵기 고정)', () => !!s.penNoPressure, v => { s.penNoPressure = v; save(); }),
      h('div.help', null, '와콤·휴이온·XP-Pen 등 PC 용 태블릿은 드라이버 설정에서 "Windows Ink" 를 켜야 필압·기울기가 옵니다. 펜 뒤쪽 지우개는 자동으로 지우개가 됩니다.'),
      segmented([['euro', '자동 보정'], ['avg', '평균'], ['lazy', '끈 당기기'], ['spring', '무거운 펜']], () => s.penFeel?.filter || 'euro', v => { s.penFeel = { ...s.penFeel, filter: v }; save(); }),
      h('div.help', null, '흔들림 보정 방식 (브러시 옵션에서 브러시마다 따로 정할 수도 있음). 끈 당기기 = 펜이 끈 길이만큼 멀어질 때만 선이 따라옴, 무거운 펜 = 살짝 늦게 부드럽게 따라옴.'),
      toggle('선 끝에서 펜을 멈추고 있으면 직선으로', () => !!s.holdLine, v => { s.holdLine = v; save(); }),
      slider('필압 다듬기', { min: 0, max: 0.8, step: 0.05, get: () => s.penFeel?.pressureSmooth ?? 0, set: v => { s.penFeel = { ...s.penFeel, pressureSmooth: v }; save(); }, format: v => Math.round(v * 100) + '%' }),
      toggle('펜을 대는 순간 굵게 찍히는 것 줄이기 (선 시작이 늘 가늘어짐)', () => s.penFeel?.startRamp === true, v => { s.penFeel = { ...s.penFeel, startRamp: v }; save(); }),
      toggle('펜 위치 예측 (선이 덜 늦게 따라옴)', () => s.penFeel?.predict !== false, v => { s.penFeel = { ...s.penFeel, predict: v }; save(); }),
      window.comicpadNative?.pen ? toggle('네이티브 펜 입력 (ComicPad 창: 펜의 모든 점·필압을 Windows 에서 직접, 펜 길게 누르기 오른쪽 클릭 없음)', () => s.nativePen !== false, v => { s.nativePen = v; save(); }) : null,
      window.comicpadNative?.wintab ? segmented([['ink', 'Windows Ink (기본)'], ['wintab', 'Wintab (와콤 등, Ink 꺼 둔 경우)']], () => s.penApi || 'ink', v => { s.penApi = v; save(); }) : null,
      select('측면 버튼 누르고 그리기', [['eyedropper', '스포이트'], ['eraser', '지우개'], ['pan', '화면 이동'], ['radial', '원형 메뉴'], ['none', '사용 안 함']], () => s.penButton, v => { s.penButton = v; save(); }),
      select('두 번째 측면 버튼 (와콤 등)', [['pan', '화면 이동'], ['eyedropper', '스포이트'], ['eraser', '지우개'], ['radial', '원형 메뉴'], ['none', '사용 안 함']], () => s.penButton2 || 'pan', v => { s.penButton2 = v; save(); }),
      slider('펜을 뗀 뒤 터치 무시 시간', { min: 0, max: 1500, step: 50, get: () => s.palmGuardMs, set: v => { s.palmGuardMs = v; save(); }, format: v => v + 'ms' }),
    ),
    section('터치',
      toggle('손가락으로 그리기 (끄면 손가락은 화면 조작 전용)', () => s.fingerDraw, v => { s.fingerDraw = v; save(); }),
      toggle('두 손가락으로 회전', () => s.touchRotate, v => { s.touchRotate = v; save(); }),
      toggle('두 손가락 탭 = 실행취소', () => s.twoFingerUndo, v => { s.twoFingerUndo = v; save(); }),
      toggle('세 손가락 탭 = 다시실행', () => s.threeFingerRedo, v => { s.threeFingerRedo = v; save(); }),
      toggle('한 손가락 길게 누르기 = 스포이트', () => s.longPressEyedropper, v => { s.longPressEyedropper = v; save(); }),
    ),
    section('화면',
      select('마우스 휠', [['zoom', '확대/축소'], ['scroll', '스크롤 (웹툰)']], () => s.wheelMode || 'zoom', v => { s.wheelMode = v; save(); }),
      toggle('화면 저지연 모드 (펜 선이 덜 늦게 보임 — 화면이 깜빡이면 끄기, 앱을 다시 열면 적용)', () => s.lowLatency !== false, v => { s.lowLatency = v; save(); app.toast('앱 창을 다시 열면 적용됩니다'); }),
      toggle('큰 버튼 (터치용)', () => s.largeUI, v => { s.largeUI = v; document.body.classList.toggle('large-ui', v); save(); }),
      toggle('왼손잡이 배치 (패널을 왼쪽에)', () => s.leftHanded, v => { s.leftHanded = v; document.body.classList.toggle('left-handed', v); save(); }),
      slider('격자 간격', { min: 10, max: 500, step: 10, get: () => s.gridSize, set: v => { s.gridSize = v; save(); app.requestRender(); }, format: v => v + 'px' }),
    ),
    section('저장',
      select('큰 원고 메모리 절약', [['auto', '자동 (큰 원고일 때만)'], ['always', '항상'], ['off', '끄기']], () => s.memSave || 'auto', v => { s.memSave = v; save(); }),
      slider('자동 저장 간격', { min: 0, max: 30, step: 1, get: () => s.autosaveMin, set: v => { s.autosaveMin = v; save(); }, format: v => (v ? v + '분' : '끔') }),
      row(button('저장 위치…', () => app.run('file.storage')), button('data 폴더 열기', () => server.openFolder('projects')), button('설정 파일 폴더', () => server.openFolder('config'))),
    ),
  );
  return body;
}

export function openSettings() {
  const body = settingsBody();
  openModal({ title: '환경 설정', body, wide: true, actions: [{ label: '닫기', primary: true }, { label: '앱 밖 창으로', run: () => app.run('popout.settings') }] });
}

export function showHelp() {
  const keys = app.defaultKeys;
  const rows = Object.entries(keys).filter(([id]) => app.commands.get(id) && !app.commands.get(id).hidden)
    .map(([id, k]) => h('tr', null, h('td', null, app.commands.get(id).label), h('td', null, h('kbd', null, (app.settings.keys[id] || k).toUpperCase()))));
  const toolRows = [...app.tools.values()].filter(t => t.key).map(t => h('tr', null, h('td', null, t.name), h('td', null, h('kbd', null, t.key.toUpperCase()))));
  openModal({
    title: '단축키 · 제스처',
    wide: true,
    body: h('div.help-cols', null,
      h('div', null,
        h('h4', null, '터치 / 펜'),
        h('ul', null,
          h('li', null, '두 손가락 드래그/벌리기/돌리기: 이동·확대·회전'),
          h('li', null, '두 손가락 탭: 실행취소 · 세 손가락 탭: 다시실행'),
          h('li', null, '네 손가락 탭: 패널 숨기기/보이기'),
          h('li', null, '한 손가락 드래그: 화면 이동 (손가락 그리기 꺼짐일 때)'),
          h('li', null, '한 손가락 길게: 스포이트'),
          h('li', null, '펜 측면 버튼 + 그리기: 스포이트, 두 번째 버튼: 화면 이동, 펜 뒤쪽 지우개: 지우개 (설정에서 변경)'),
          h('li', null, '퀵바 손잡이 탭: 버튼 편집 · 끌기: 이동'),
        ),
        h('h4', null, '키보드 보조'),
        h('ul', null,
          h('li', null, 'Space + 드래그: 화면 이동, Ctrl+Space: 확대, Shift+Space: 회전'),
          h('li', null, 'Alt + 클릭: 스포이트 · Shift + 그리기: 직선'),
          h('li', null, '휠: 확대/축소, Alt+휠: 회전'),
        ),
        h('h4', null, '도구'),
        h('table', null, ...toolRows)),
      h('div', null, h('h4', null, '명령'), h('table', null, ...rows))),
  });
}

/** 키보드 단축키 처리 */
export function initShortcuts() {
  const combo = (e) => {
    let k = e.key.toLowerCase();
    if (k === ' ') k = 'space';
    if (k === '+') k = '=';
    const parts = [];
    if (e.ctrlKey || e.metaKey) parts.push('ctrl');
    if (e.altKey) parts.push('alt');
    if (e.shiftKey && k.length > 1) parts.push('shift');
    else if (e.shiftKey && /^[a-z0-9]$/.test(k)) parts.push('shift');
    parts.push(k === '{' ? '[' : k === '}' ? ']' : k);
    return parts.join('+');
  };
  const lookup = () => {
    const map = new Map();
    for (const [id, k] of Object.entries({ ...app.defaultKeys, ...app.settings.keys })) if (k) map.set(k, id);
    for (const c of app.commands.values()) if (c.key && !map.has(c.key) && !(c.id in app.settings.keys)) map.set(c.key, c.id);
    return map;
  };
  const isField = (t) => t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t instanceof HTMLSelectElement || t?.isContentEditable;
  const stage = document.getElementById('stage');
  // 도구 키를 길게 누르고 있으면 그동안만 그 도구 (놓으면 원래 도구로)
  const springs = new Map();

  window.addEventListener('keydown', (e) => {
    heldKeys.ctrl = e.ctrlKey; heldKeys.alt = e.altKey; heldKeys.shift = e.shiftKey;
    if (isField(e.target)) return;
    if (document.querySelector('.modal-backdrop')) return;
    if (e.key === ' ') {
      e.preventDefault();
      if (!heldKeys.space) { heldKeys.space = true; stage.style.cursor = 'grab'; }
      return;
    }
    if (e.key === 'Alt') e.preventDefault();
    if (app.tool?.onKey && !e.ctrlKey && !e.metaKey && app.tool.onKey(e)) { e.preventDefault(); return; }
    const c = combo(e);
    const id = lookup().get(c);
    if (id) {
      e.preventDefault();
      app.run(id);
      return;
    }
    if (!e.ctrlKey && !e.altKey && !e.metaKey && e.key.length === 1) {
      const k = e.key.toLowerCase();
      // 같은 키를 쓰는 도구가 여럿이면 누를 때마다 다음 도구로 (세팅 프리셋에서 정한 순서 먼저)
      const order = Object.keys(app.settings.toolKeys || {});
      const rank = (t) => { const i = order.indexOf(t.id); return i < 0 ? 999 : i; };
      const hits = [...app.tools.values()].filter(t => t.key === k || (t.altKeys || []).includes(k)).sort((a, b) => rank(a) - rank(b));
      if (hits.length) {
        e.preventDefault();
        if (!e.repeat) {
          const i = hits.findIndex(t => t.id === app.toolId);
          springs.set(k, { prev: app.toolId, t: performance.now() });
          app.setTool(hits[(i + 1) % hits.length].id);
        }
        return;
      }
    }
  });
  window.addEventListener('keyup', (e) => {
    heldKeys.ctrl = e.ctrlKey; heldKeys.alt = e.altKey; heldKeys.shift = e.shiftKey;
    if (e.key === ' ') { heldKeys.space = false; stage.style.cursor = ''; app.bus.emit('tool:cursor'); }
    if (e.key === 'Alt') e.preventDefault();
    const sp = springs.get(e.key.toLowerCase());
    if (sp) {
      springs.delete(e.key.toLowerCase());
      if (performance.now() - sp.t > 350 && sp.prev && sp.prev !== app.toolId && !app.tool?.busy) app.setTool(sp.prev);
    }
  });
  window.addEventListener('blur', () => { heldKeys.space = heldKeys.ctrl = heldKeys.alt = heldKeys.shift = false; });
}
