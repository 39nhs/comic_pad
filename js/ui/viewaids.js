// 명도 확인 보기 (화면만 흑백/명도 단계로) + 용지 질감 설정
import { app } from '../app.js';
import { form } from './dialogs.js';
import { PAPER_TYPES } from '../core/paper.js';

const MODES = ['off', 'gray', 'steps'];

function ensureFilter() {
  if (document.getElementById('cp-luma-svg')) return;
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.id = 'cp-luma-svg';
  svg.setAttribute('width', '0'); svg.setAttribute('height', '0');
  svg.style.position = 'absolute';
  // 밝기(루마)로 바꾼 뒤 5단계로 나눔: 명암 덩어리가 잘 보이게
  svg.innerHTML = `<filter id="cp-luma-steps" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer><feFuncR type="discrete" tableValues="0.08 0.3 0.52 0.74 0.96"/><feFuncG type="discrete" tableValues="0.08 0.3 0.52 0.74 0.96"/><feFuncB type="discrete" tableValues="0.08 0.3 0.52 0.74 0.96"/></feComponentTransfer>
  </filter>`;
  document.body.append(svg);
}

export function setLumaView(mode) {
  ensureFilter();
  app.lumaView = mode;
  document.body.classList.toggle('luma-gray', mode === 'gray');
  document.body.classList.toggle('luma-steps', mode === 'steps');
  app.toast({ off: '명도 보기 끔', gray: '명도 보기: 흑백 (그림은 그대로)', steps: '명도 보기: 5단계 명암 덩어리' }[mode]);
}

export async function paperTextureDialog() {
  const doc = app.doc;
  const t = doc.paper.texture || { type: 'none', strength: 0.6, scale: 1 };
  const before = structuredClone(doc.paper);
  const apply = (v) => {
    doc.paper.texture = { type: v.type, strength: Math.max(0, Math.min(1, (+v.strength || 0) / 100)), scale: Math.max(0.25, Math.min(4, +v.scale || 1)), over: !!v.over };
    doc.paper.visible = true;
    app.requestRender();
  };
  const v = await form('용지 질감', [
    { key: 'type', label: '종류', type: 'segment', options: PAPER_TYPES, value: t.type },
    { key: 'strength', label: '진하기 (%)', type: 'number', value: Math.round((t.strength ?? 0.6) * 100), min: 0, max: 100 },
    { key: 'scale', label: '결 크기 (배)', type: 'number', value: t.scale || 1, min: 0.25, max: 4, step: 0.25 },
    { key: 'over', label: '칠한 그림 위에도 결 보이기 (수채·연필 느낌)', type: 'checkbox', value: !!t.over },
    { key: 'info', type: 'info', label: '용지에 곱하기로 얹혀서 그림 전체가 종이에 그린 듯 보입니다. 용지를 켠 채 내보내면 이미지에도 들어갑니다 (투명 PNG 로 내보내면 빠짐).' },
  ], { okLabel: '적용', onChange: (vals) => apply(vals) });
  if (!v) { doc.paper = before; app.requestRender(); return; }
  apply(v);
  const after = structuredClone(doc.paper);
  app.history.push({ label: '용지 질감', undo: () => { doc.paper = structuredClone(before); app.requestRender(); app.bus.emit('layers:changed'); }, redo: () => { doc.paper = structuredClone(after); app.requestRender(); app.bus.emit('layers:changed'); } });
  app.markModified();
}

export function initViewAids() {
  app.registerCommand('view.luma', { label: '명도 확인 보기 (끔 → 흑백 → 5단계)', key: 'shift+y', run: () => setLumaView(MODES[(MODES.indexOf(app.lumaView || 'off') + 1) % MODES.length]) });
  app.registerCommand('paper.texture', { label: '용지 질감 (종이 결)…', run: paperTextureDialog });
  app.registerQuickAction('luma', { icon: 'eye', label: '명도', run: () => app.run('view.luma') });
  for (const it of [{ sep: true }, { command: 'view.luma' }, { command: 'paper.texture' }]) app.addMenuItem('view', it);
}
