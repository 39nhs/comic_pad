// 초기 세팅 프리셋: 처음 쓰는 사람이 익숙한 방식(클립스튜디오식·포토샵식·웹툰·태블릿)으로 바로 시작하게.
// - 적용 전 바뀌는 설정을 백업해 두고 버튼 하나로 되돌리기
// - 내 세팅을 .cpset 파일로 내보내 다른 사람에게 건네고, 받은 쪽은 고른 부분만 가져오기
// 프리셋은 필요한 설정만 바꾸고, 사용자가 만든 브러시·작업 화면·팔레트는 지우지 않는다.
import { app } from '../app.js';
import { h, downloadBlob, pickFile, fileStamp } from '../core/util.js';
import { aiPickSection } from './aimodels.js';
import { openModal, form } from './dialogs.js';
import { setActivePreset } from '../tools/brush.js';
import { SIMPLE_TOOLS, FREQUENT } from './layout.js';
import { DEFAULT_BOTTOM } from './bottombar.js';

// ---------------------------------------------------------------------
// 도구 단축키 바꾸기 (settings.toolKeys = { 도구id: 'k' | ['k','l'] | '' })
// 같은 키를 여러 도구에 주면 누를 때마다 차례로 바뀜 (설정 순서대로)
// ---------------------------------------------------------------------
function rememberDefaults(t) {
  if (t._defKey === undefined) { t._defKey = t.key || ''; t._defAlt = [...(t.altKeys || [])]; }
}
export function applyToolKeys(s = app.settings) {
  const map = s.toolKeys || {};
  for (const t of app.tools.values()) {
    rememberDefaults(t);
    if (Object.prototype.hasOwnProperty.call(map, t.id)) {
      const v = map[t.id];
      const arr = Array.isArray(v) ? v.filter(Boolean) : (v ? [v] : []);
      t.key = arr[0] || '';
      t.altKeys = arr.slice(1);
    } else {
      t.key = t._defKey;
      t.altKeys = [...t._defAlt];
    }
  }
}

// ---------------------------------------------------------------------
// 프리셋
// ---------------------------------------------------------------------
const B5_600 = { name: '흑백 만화 원고 B5 600dpi', w: 4441, h: 6213, dpi: 600, print: { size: 'b5', mmW: 182, mmH: 257, bleedMm: 3, safeMm: 5 }, note: '재단선·도련·안전선 가이드 포함' };
const WEBTOON = { name: '웹툰 (세로 긴 캔버스)', w: 800, h: 6400, dpi: 72, note: '800px 폭, 아래로 더 늘려 쓰기 가능' };

const PEN_DEFAULTS = { penButton: 'eyedropper', fingerDraw: false, touchRotate: true, twoFingerUndo: true, threeFingerRedo: true, longPressEyedropper: true, palmGuardMs: 350 };

export const SETUP_PRESETS = [
  {
    id: 'csp', name: '클립스튜디오식 (기본 · 흑백 만화)', icon: 'pen',
    desc: '클립스튜디오에 익숙한 사람용. 도구 키 배치를 비슷하게, 펜 중심 도구 막대, 새 작품은 흑백 B5 600dpi 원고.',
    changes: [
      '도구 키: P·B 펜/브러시, E 지우개, G 채우기→그라데이션→올가미 채우기(누를 때마다), I 스포이트, M 선택→선택 펜, J 손끝→흐림, U 도형→컷 나누기→자, T 텍스트→말풍선, H 화면 이동, O 벡터 선 고르기',
      '도구 막대: 펜·컷·말풍선·자 중심 (그라데이션·올가미 채우기·벡터 선 고르기·유선 추가)',
      '브러시: G펜으로 시작',
      '펜: 필압 그대로(필압 다듬기·시작 굵기 줄이기 끔), 필압 곡선 직선, 커서는 펜촉 + 크기 원',
      '새 작품 추천: 흑백 만화 원고 B5 600dpi (재단선·도련 가이드)',
      'Ctrl+Shift+N 새 레이어, Ctrl+E 아래로 병합, Ctrl+Alt+G 클리핑, Ctrl+T 변형 등은 그대로 (이미 같음)',
    ],
    apply(s) {
      s.keys = {};
      s.toolKeys = {
        brush: ['b', 'p'], eraser: 'e',
        fill: 'g', gradient: 'g', lassofill: 'g',
        eyedropper: 'i', select: 'm', selpen: 'm',
        smudge: 'j', blur: 'j',
        shape: 'u', panel: 'u', ruler: 'u',
        text: 't', bubble: 't',
        view: 'h', vselect: 'o',
      };
      s.ui = { ...(s.ui || {}), mode: 'simple', tools: [...new Set([...SIMPLE_TOOLS, 'gradient', 'lassofill', 'vselect', 'speedlines', 'selpen'])] };
      (s.activePreset ||= {}).brush = 'gpen';
      s.startDoc = B5_600;
      // 펜: 클립스튜디오 기본처럼 필압 그대로(다듬기·시작 줄이기 없음), 선형 필압 곡선, 펜촉 커서
      s.penFeel = { ...(s.penFeel || {}), pressureSmooth: 0, startRamp: false };   // 펜 진단으로 잰 필압 범위(pmin·pmax)는 그대로
      s.pressurePts = null; s.pressureGamma = 1;
      s.brushCursor = 'nib';
    },
  },
  {
    id: 'comicpad', name: '간단히 (ComicPad 원래 화면)', icon: 'star',
    desc: '처음 설치했을 때와 같은 상태. 자주 쓰는 도구·메뉴만 보이고, 나머지는 "모든 항목 보기"·Ctrl+K 로.',
    changes: ['단축키·도구 키를 기본으로', '화면: 간단히 (자주 쓰는 것만)', '아래 큰 버튼 막대: 기본 버튼', '보통 크기 버튼, 펜 측면 버튼 = 스포이트', '새 작품 추천 크기 없음'],
    apply(s) {
      s.keys = {}; s.toolKeys = {};
      s.ui = { mode: 'simple' };
      s.bottombar = { visible: true, items: null };
      s.largeUI = false; s.leftHanded = false; s.sideVisible = true;
      Object.assign(s, PEN_DEFAULTS);
      s.startDoc = null;
    },
  },
  {
    id: 'ps', name: '포토샵식 (일러스트)', icon: 'brush',
    desc: '포토샵에 익숙한 사람용. 도구 키·단축키를 비슷하게, 모든 메뉴를 보여 줌.',
    changes: [
      '도구 키: B 브러시, E 지우개, G 채우기→그라데이션, I 스포이트, M·L·W 선택, Q 선택 펜(빠른 마스크 느낌), V 이동/변형, T 텍스트, U 도형, H 화면 이동, S 복제 도장, A 벡터 선 고르기',
      'Ctrl+1 100% 보기, Ctrl+Shift+E 보이는 레이어 결합, Ctrl+Alt+Shift+W 이미지 내보내기',
      'Ctrl+J 복제, Ctrl+E 아래로 병합, Ctrl+G 폴더로 묶기, Ctrl+Alt+G 클리핑, Ctrl+T 변형, Alt+Backspace 채우기 (이미 같음)',
      '화면: 모든 기능 보기 (메뉴 전부)',
      '브러시: 부드러운 브러시로 시작',
    ],
    apply(s) {
      s.keys = { 'view.actual': 'ctrl+1', 'layer.flatten': 'ctrl+shift+e', 'file.export': 'ctrl+alt+shift+w' };
      s.toolKeys = {
        brush: ['b', 'p'], eraser: 'e',
        fill: 'g', gradient: 'g', lassofill: '',
        eyedropper: 'i', select: ['m', 'l', 'w'], selpen: 'q', smudge: '',
        transform: 'v', text: 't', shape: 'u', view: 'h', clone: 's', vselect: 'a',
      };
      s.ui = { ...(s.ui || {}), mode: 'full' };
      (s.activePreset ||= {}).brush = 'soft';
      s.startDoc = null;
    },
  },
  {
    id: 'webtoon', name: '웹툰 (컬러, 세로 스크롤)', icon: 'phone',
    desc: '컬러 웹툰용. 세로로 긴 캔버스, 웹툰 미리보기·플랫폼 규격 내보내기·아래로 늘리기를 가까이.',
    changes: [
      '새 작품 추천: 800px 폭 세로 긴 캔버스',
      '아래 막대에 "웹툰 미리보기", "아래로 늘리기", "레이어", "색" 버튼 추가',
      '보기 메뉴에 웹툰 미리보기, 파일 메뉴에 웹툰 플랫폼 규격 내보내기',
      '브러시: G펜으로 시작',
    ],
    apply(s) {
      s.startDoc = WEBTOON;
      const items = s.bottombar?.items || DEFAULT_BOTTOM;
      s.bottombar = { visible: true, items: [...new Set([...items, 'webtoonView', 'extendDown', 'layersTab', 'colorTab'])] };
      const pick = { ...(s.ui?.menuPick || {}) };
      pick.view = [...new Set([...(pick.view || FREQUENT.view), 'view.webtoon', 'edit.extendDown'])];
      pick.file = [...new Set([...(pick.file || FREQUENT.file), 'file.webtoonExport'])];
      s.ui = { ...(s.ui || {}), menuPick: pick };
      (s.activePreset ||= {}).brush = 'gpen';
    },
  },
  {
    id: 'tablet', name: '펜·터치 태블릿 (S펜·아이패드 등)', icon: 'finger',
    desc: '키보드 없이 펜과 손가락으로. 큰 버튼, 아래 막대 넓게, 펜 측면 버튼으로 원형 메뉴.',
    changes: [
      '큰 버튼 (터치용)',
      '아래 큰 버튼 막대: 실행취소·다시실행·브러시/지우개·스포이트·크기·농도 + 레이어·색·화면 맞춤·회전',
      '펜 측면 버튼 = 원형 메뉴',
      '손가락: 화면 이동·확대·회전, 두 손가락 탭 실행취소, 세 손가락 탭 다시실행, 길게 눌러 스포이트 (손가락으로 그리기는 끔)',
    ],
    apply(s) {
      s.largeUI = true;
      s.bottombar = { visible: true, items: [...new Set([...DEFAULT_BOTTOM, 'layersTab', 'colorTab', 'fit', 'rotL', 'rotR'])] };
      Object.assign(s, PEN_DEFAULTS, { penButton: 'radial' });
      s.sideVisible = true;
    },
  },
];

// 설정 키 → 사람이 읽는 이름 (무엇이 바뀌는지 보여 줄 때)
const KEY_LABEL = {
  keys: '단축키', toolKeys: '도구 키', ui: '화면 구성(도구 막대·메뉴)', bottombar: '아래 큰 버튼 막대', largeUI: '큰 버튼',
  leftHanded: '왼손잡이 배치', sideVisible: '오른쪽 패널', penButton: '펜 측면 버튼', penButton2: '두 번째 측면 버튼', penNoPressure: '필압 없이 쓰기', fingerDraw: '손가락 그리기',
  touchRotate: '두 손가락 회전', twoFingerUndo: '두 손가락 실행취소', threeFingerRedo: '세 손가락 다시실행', longPressEyedropper: '길게 눌러 스포이트',
  palmGuardMs: '손바닥 무시 시간', startDoc: '새 작품 추천 크기', activePreset: '시작 브러시', brushes: '브러시', seenPresets: '브러시 목록 기록',
  workspaces: '작업 화면', palette: '팔레트', roughColor: '러프 색', radialItems: '원형 메뉴', quickbar: '떠 있는 퀵바',
  dock_top: '위 패널 탭', dock_bottom: '아래 패널 탭', dockSplit: '패널 위아래 비율', pressureGamma: '필압 곡선', penFeel: '필기감 보정', mousePressure: '마우스 필압',
};
const NOT_SETTINGS = new Set(['setupBackup', 'setupDone']);
const same = (a, b) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);

/** 설정 사본에 프리셋을 적용해 본다 (원래 설정은 그대로). { next, changed: [키...] } */
export function applyPresetTo(settings, id) {
  const p = SETUP_PRESETS.find(x => x.id === id);
  if (!p) throw new Error('없는 프리셋: ' + id);
  const before = structuredClone(settings);
  const next = structuredClone(settings);
  p.apply(next);
  const changed = [...new Set([...Object.keys(before), ...Object.keys(next)])].filter(k => !NOT_SETTINGS.has(k) && !same(before[k], next[k]));
  return { next, changed };
}

// ---------------------------------------------------------------------
// 적용 · 백업 · 되돌리기
// ---------------------------------------------------------------------
/** 설정이 바뀐 뒤 화면을 다시 그리기 */
export function refreshAfterSettings(prevBrush) {
  const s = app.settings;
  document.body.classList.toggle('large-ui', !!s.largeUI);
  document.body.classList.toggle('left-handed', !!s.leftHanded);
  document.body.classList.toggle('no-side', s.sideVisible === false);
  document.getElementById('sidepanel')?.style.setProperty('--split', `${(s.dockSplit ?? 0.48) * 100}%`);
  applyToolKeys();
  if (s.activePreset?.brush && s.activePreset.brush !== prevBrush) {
    try { setActivePreset('brush', s.activePreset.brush); } catch { /* 없는 브러시면 무시 */ }
  }
  for (const e of ['ui:layout', 'menus:changed', 'panels:changed', 'quick:changed', 'settings:changed']) app.bus.emit(e);
  app.saveSettingsSoon();
}

/** 바뀔 키들의 지금 값을 백업하고, 새 값으로 바꾼다 */
function commit(changed, next, label) {
  const s = app.settings;
  const prevBrush = s.activePreset?.brush;
  const values = {}, absent = [];
  for (const k of changed) { if (k in s) values[k] = structuredClone(s[k]); else absent.push(k); }
  s.setupBackup = { label, at: Date.now(), values, absent };
  for (const k of changed) { if (k in next && next[k] !== undefined) s[k] = structuredClone(next[k]); else delete s[k]; }
  refreshAfterSettings(prevBrush);
}

export function undoSetup() {
  const s = app.settings, b = s.setupBackup;
  if (!b) { app.toast('되돌릴 세팅 변경이 없습니다'); return false; }
  const prevBrush = s.activePreset?.brush;
  for (const [k, v] of Object.entries(b.values || {})) s[k] = structuredClone(v);
  for (const k of b.absent || []) delete s[k];
  delete s.setupBackup;
  refreshAfterSettings(prevBrush);
  app.toast(`"${b.label}" 적용 전으로 되돌렸습니다`);
  return true;
}

export function applySetupPreset(id, { quiet = false } = {}) {
  const p = SETUP_PRESETS.find(x => x.id === id);
  const { next, changed } = applyPresetTo(app.settings, id);
  app.settings.setupDone = id;
  if (!changed.length) { if (!quiet) app.toast(`이미 "${p.name}" 세팅입니다`); app.saveSettingsSoon(); return; }
  commit(changed, next, p.name);
  if (!quiet) app.toast(`"${p.name}" 세팅을 적용했습니다`, 'info', { duration: 9000, actions: [{ label: '되돌리기', run: undoSetup }] });
}

// ---------------------------------------------------------------------
// 프리셋 고르기 창
// ---------------------------------------------------------------------
export const DEFAULT_SETUP = 'csp';

export async function chooseSetupDialog({ firstRun = false } = {}) {
  // 처음 설치: 쓸 AI 기능도 같이 고르기 (고른 모델만 받음)
  const ai = firstRun ? await aiPickSection().catch(() => null) : null;
  return new Promise((resolve) => {
    let chosen = null;
    const detail = h('div.setup-detail');
    const cards = h('div.setup-cards');
    const show = (p) => {
      chosen = p;
      cards.querySelectorAll('.setup-card').forEach(c => c.classList.toggle('on', c.dataset.id === p.id));
      const { changed } = applyPresetTo(app.settings, p.id);
      detail.innerHTML = '';
      detail.append(
        h('b', null, p.name), h('p.help', null, p.desc),
        h('ul', null, ...p.changes.map(t => h('li', null, t))),
        h('div.help', null, firstRun ? '나중에 도움말 > 초기 세팅 프리셋 고르기 에서 언제든 바꿀 수 있습니다.'
          : changed.length ? `바뀌는 설정: ${changed.map(k => KEY_LABEL[k] || k).join(', ')}. 적용 전 상태는 백업되어 "되돌리기" 한 번으로 돌아갑니다. 내가 만든 브러시·작업 화면·팔레트는 지우지 않습니다.`
            : '지금 설정과 같아서 바뀌는 것이 없습니다.'),
      );
    };
    for (const p of SETUP_PRESETS) {
      const c = h('button.setup-card', { type: 'button', 'data-id': p.id, onclick: () => show(p) }, h('b', null, p.name), h('small', null, p.desc));
      cards.append(c);
    }
    const body = h('div.setup-choose', null,
      firstRun ? h('div.help', null, 'ComicPad 에 오신 것을 환영합니다. 기본은 클립스튜디오식입니다. 다른 방식이 익숙하면 골라 주세요 (단축키·화면·새 작품 크기를 맞춰 드립니다).') : null,
      cards, detail, ai ? ai.el : null);
    const actions = [
      { label: firstRun ? '기본(클립스튜디오식)으로 시작' : '닫기', value: 'skip' },
      { label: '이 세팅으로 시작', primary: true, value: 'apply' },
    ];
    if (!firstRun && app.settings.setupBackup) actions.unshift({ label: `되돌리기 ("${app.settings.setupBackup.label}" 전으로)`, value: 'undo' });
    openModal({
      title: firstRun ? '처음 세팅 고르기' : '초기 세팅 프리셋',
      body, wide: true,
      actions: actions.map(a => ({ ...a, run: () => {
        if (a.value === 'apply' && chosen) { applySetupPreset(chosen.id); ai?.start(); }
        else if (a.value === 'apply') { app.toast('위에서 하나를 골라 주세요', 'warn'); return false; }
        else if (a.value === 'undo') undoSetup();
        else if (firstRun) { applySetupPreset(DEFAULT_SETUP, { quiet: true }); ai?.start(); }
        resolve(a.value);
      } })),
      onClose: () => resolve(null),
    });
    show(SETUP_PRESETS.find(p => p.id === app.settings.setupDone) || SETUP_PRESETS.find(p => p.id === DEFAULT_SETUP));
  });
}

// ---------------------------------------------------------------------
// 내 세팅 내보내기 / 가져오기 (.cpset)
// ---------------------------------------------------------------------
export const SET_PARTS = [
  ['keys', '단축키·도구 키', ['keys', 'toolKeys']],
  ['layout', '화면 구성 (도구 막대·메뉴·패널·아래 막대·원형 메뉴·큰 버튼)', ['ui', 'dock_top', 'dock_bottom', 'dockSplit', 'sideVisible', 'leftHanded', 'quickbar', 'bottombar', 'radialItems', 'largeUI']],
  ['brushes', '브러시 (내가 만든 것 포함)', ['brushes', 'activePreset']],
  ['workspaces', '작업 화면', ['workspaces']],
  ['palette', '팔레트·러프 색', ['palette', 'roughColor']],
  ['pen', '펜·터치 설정 (필압·필기감·제스처·측면 버튼)', ['pressureGamma', 'pressurePts', 'brushCursor', 'holdLine', 'penButton2', 'penNoPressure', 'penFeel', 'mousePressure', 'fingerDraw', 'palmGuardMs', 'touchRotate', 'twoFingerUndo', 'threeFingerRedo', 'longPressEyedropper', 'penButton']],
  ['startDoc', '새 작품 추천 크기', ['startDoc']],
  ['panelTemplates', '내 컷 나누기 템플릿', ['panelTemplates']],
];

export function buildSetFile(settings, parts) {
  const out = { format: 'comicpad-settings', version: 1, created: new Date().toISOString(), parts: {} };
  for (const [id, , keys] of SET_PARTS) {
    if (!parts.includes(id)) continue;
    const o = {};
    for (const k of keys) if (settings[k] !== undefined) o[k] = structuredClone(settings[k]);
    out.parts[id] = o;
  }
  return out;
}

/** 받은 세팅을 사본에 합치기. 브러시·작업 화면은 합치고(같은 것은 바꿈), 나머지는 바꿈 */
export function mergeSetFile(settings, file, parts) {
  const next = structuredClone(settings);
  for (const id of parts) {
    const o = file.parts?.[id];
    if (!o) continue;
    for (const [k, v] of Object.entries(o)) {
      if (k === 'brushes' && v && typeof v === 'object') {
        next.brushes = next.brushes ? structuredClone(next.brushes) : {};
        for (const [cat, list] of Object.entries(v)) {
          if (!Array.isArray(list)) continue;
          const cur = next.brushes[cat] ||= [];
          for (const b of list) { const i = cur.findIndex(x => x.id === b.id); if (i >= 0) cur[i] = b; else cur.push(b); }
        }
      } else if (k === 'workspaces' && Array.isArray(v)) {
        const cur = [...(next.workspaces || [])];
        for (const w of v) { const i = cur.findIndex(x => x.name === w.name); if (i >= 0) cur[i] = w; else cur.push(w); }
        next.workspaces = cur;
      } else if (k === 'activePreset' && v && typeof v === 'object') {
        next.activePreset = { ...(next.activePreset || {}), ...v };
      } else next[k] = v;
    }
  }
  const changed = [...new Set([...Object.keys(settings), ...Object.keys(next)])].filter(k => !NOT_SETTINGS.has(k) && !same(settings[k], next[k]));
  return { next, changed };
}

async function exportSetDialog() {
  const v = await form('내 세팅 내보내기 (.cpset)', [
    { key: 'info', type: 'info', label: '고른 부분만 작은 파일 하나로 저장합니다. 다른 사람에게 건네면 "세팅 가져오기"로 똑같이 쓸 수 있습니다. 그림·작품은 들어가지 않습니다.' },
    ...SET_PARTS.map(([id, label]) => ({ key: id, label, type: 'checkbox', value: id !== 'startDoc' || !!app.settings.startDoc })),
    { key: 'name', label: '파일 이름', value: '내 세팅' },
  ], { okLabel: '저장' });
  if (!v) return;
  const parts = SET_PARTS.map(p => p[0]).filter(id => v[id]);
  if (!parts.length) { app.toast('내보낼 부분을 하나 이상 골라 주세요', 'warn'); return; }
  const file = buildSetFile(app.settings, parts);
  const imageTips = Object.values(app.settings.brushes || {}).flat().filter(b => b?.tipImage).length;
  downloadBlob(new Blob([JSON.stringify(file, null, 1)], { type: 'application/json' }), `${(v.name || '세팅').replace(/[\\/:*?"<>|]/g, '_')}_${fileStamp()}.cpset`);
  app.toast(imageTips && parts.includes('brushes') ? `세팅을 저장했습니다. 그림 모양 브러시 ${imageTips}개는 모양 그림 파일이 따로 필요합니다 (에셋 폴더)` : '세팅 파일을 저장했습니다 (다운로드 폴더)');
}

async function importSetDialog() {
  const f = await pickFile('.cpset,.json');
  if (!f) return;
  let file;
  try { file = JSON.parse(await f.text()); } catch { app.toast('세팅 파일을 읽지 못했습니다', 'error'); return; }
  if (file?.format !== 'comicpad-settings' || !file.parts) { app.toast('ComicPad 세팅 파일(.cpset)이 아닙니다', 'error'); return; }
  const have = SET_PARTS.filter(([id]) => file.parts[id]);
  if (!have.length) { app.toast('파일에 가져올 내용이 없습니다', 'warn'); return; }
  const v = await form(`세팅 가져오기: ${f.name}`, [
    { key: 'info', type: 'info', label: `${file.created ? new Date(file.created).toLocaleString() + ' 에 만든 파일. ' : ''}가져올 부분을 고르세요. 브러시·작업 화면은 내 것에 합쳐지고(이름/번호가 같으면 바뀜), 나머지는 바뀝니다. 가져오기 전 상태는 백업되어 되돌릴 수 있습니다.` },
    ...have.map(([id, label]) => ({ key: id, label, type: 'checkbox', value: true })),
  ], { okLabel: '가져오기' });
  if (!v) return;
  const parts = have.map(p => p[0]).filter(id => v[id]);
  const { next, changed } = mergeSetFile(app.settings, file, parts);
  if (!changed.length) { app.toast('지금 설정과 같아서 바뀐 것이 없습니다'); return; }
  commit(changed, next, `세팅 가져오기 (${f.name})`);
  app.toast(`세팅을 가져왔습니다: ${changed.map(k => KEY_LABEL[k] || k).join(', ')}`, 'info', { duration: 9000, actions: [{ label: '되돌리기', run: undoSetup }] });
}

// ---------------------------------------------------------------------
export function initSetupPresets() {
  const s = app.settings;
  applyToolKeys();
  app.bus.on('tools:registered', () => applyToolKeys());

  // 처음 설치인지: 이미 쓰던 설정(지난 새 작품·단축키·작업 화면·아래 막대 기록)이 있으면 조용히 "완료"로
  if (!s.setupDone) {
    const used = s.lastNew || s.bottomMigrated || (s.keys && Object.keys(s.keys).length) || s.workspaces?.length;
    if (used) s.setupDone = 'existing';
    else {
      // 시작 창(newDocDialog startup)이 열리기 직전에 한 번 묻는다
      app.setupFirstRun = async () => {
        app.setupFirstRun = null;
        await chooseSetupDialog({ firstRun: true });
        if (!app.settings.setupDone) applySetupPreset(DEFAULT_SETUP, { quiet: true });   // 창을 그냥 닫아도 기본은 클립스튜디오식
        app.saveSettingsSoon();
      };
    }
  }

  app.registerQuickAction('webtoonView', { icon: 'phone', label: '웹툰 미리보기', run: () => app.run('view.webtoon') });
  app.registerQuickAction('extendDown', { icon: 'down', label: '아래로 늘리기', run: () => app.run('edit.extendDown') });

  app.registerCommand('setup.choose', { label: '초기 세팅 프리셋 고르기 (클립스튜디오식·포토샵식·웹툰·태블릿)…', run: () => chooseSetupDialog() });
  app.registerCommand('setup.undo', { label: '세팅 되돌리기 (마지막 프리셋·가져오기 전으로)', run: undoSetup });
  app.registerCommand('setup.export', { label: '내 세팅 내보내기 (.cpset)…', run: exportSetDialog });
  app.registerCommand('setup.import', { label: '세팅 가져오기 (.cpset)…', run: importSetDialog });
  for (const it of [{ sep: true }, { command: 'setup.choose' }, { command: 'setup.export' }, { command: 'setup.import' }, { command: 'setup.undo' }]) app.addMenuItem('help', it);
}
