// 보정 대화상자 (미리보기) · 조정 레이어 · 레이어 효과 · 톤화 · 필터 · 선택 보강
import { app } from '../app.js';
import * as ops from '../core/ops.js';
import { h, createCanvas, clamp, rectPad, debounce } from '../core/util.js';
import { slider, toggle, segmented, colorInput, select, button, row, section } from './controls.js';
import { openModal, form } from './dialogs.js';
import { ADJUST_TYPES, adjustInner, defineFilter, unsharpFilter, noiseFilter, curveFn } from '../core/filters.js';
import { selectionHistory } from '../tools/select.js';
import { referenceImage } from '../tools/basic.js';

const clone = (o) => structuredClone(o);

// ---------------------------------------------------------------- 히스토그램
function histogram(layer) {
  const doc = app.doc;
  const s = Math.min(1, 512 / Math.max(doc.width, doc.height));
  const c = createCanvas(doc.width * s, doc.height * s);
  const x = c.getContext('2d');
  if (layer && !layer.isFolder && layer.kind !== 'adjust') x.drawImage(layer.canvas, 0, 0, c.width, c.height);
  else app.compositor.render(x, doc, new DOMMatrix().scale(s), { paper: true, preview: null });
  const d = x.getImageData(0, 0, c.width, c.height).data, hist = new Uint32Array(256);
  for (let i = 0; i < d.length; i += 4) if (d[i + 3] > 8) hist[Math.round(0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2])]++;
  return hist;
}

function drawHist(cv, hist, over) {
  const x = cv.getContext('2d'), W = cv.width, H = cv.height;
  x.clearRect(0, 0, W, H);
  x.fillStyle = '#1e1e22'; x.fillRect(0, 0, W, H);
  const max = Math.max(1, ...hist.slice(2, 254));
  x.fillStyle = '#6b6b78';
  for (let i = 0; i < 256; i++) { const v = Math.min(1, hist[i] / max) * H; x.fillRect(i * W / 256, H - v, W / 256 + 0.5, v); }
  over?.(x, W, H);
}

// ---------------------------------------------------------------- 톤 커브 편집기
function curveEditor(params, onChange) {
  let ch = 'master';
  const cv = h('canvas.curve-ed', { width: 512, height: 512 });
  const hist = histogram(app.doc.active);
  const draw = () => drawHist(cv, hist, (x, W, H) => {
    x.strokeStyle = '#3a3a42'; x.lineWidth = 1;
    for (let i = 1; i < 4; i++) { x.beginPath(); x.moveTo(i * W / 4, 0); x.lineTo(i * W / 4, H); x.moveTo(0, i * H / 4); x.lineTo(W, i * H / 4); x.stroke(); }
    const colors = { master: '#ffffff', r: '#ff5c5c', g: '#5cd65c', b: '#5c9cff' };
    for (const k of ['r', 'g', 'b', 'master']) {
      if (k !== ch && k !== 'master' && params[k].length === 2 && params[k][0][1] === 0 && params[k][1][1] === 255) continue;
      const f = curveFn(params[k]);
      x.strokeStyle = colors[k]; x.globalAlpha = k === ch ? 1 : 0.35; x.lineWidth = k === ch ? 3 : 2;
      x.beginPath();
      for (let i = 0; i <= 128; i++) { const t = i / 128; const y = (1 - f(t)) * H; if (i) x.lineTo(t * W, y); else x.moveTo(0, y); }
      x.stroke();
    }
    x.globalAlpha = 1;
    for (const [px, py] of params[ch]) { x.fillStyle = '#e8a33d'; x.beginPath(); x.arc(px / 255 * W, (1 - py / 255) * H, 9, 0, Math.PI * 2); x.fill(); }
  });
  let drag = -1;
  const toPt = (e) => { const r = cv.getBoundingClientRect(); return [clamp((e.clientX - r.left) / r.width * 255, 0, 255), clamp((1 - (e.clientY - r.top) / r.height) * 255, 0, 255)]; };
  cv.addEventListener('pointerdown', (e) => {
    cv.setPointerCapture(e.pointerId);
    const [x, y] = toPt(e), pts = params[ch];
    const tol = e.pointerType === 'touch' ? 18 : 10;
    drag = pts.findIndex(([px, py]) => Math.hypot(px - x, py - y) < tol);
    if (drag < 0) { pts.push([x, y]); pts.sort((a, b) => a[0] - b[0]); drag = pts.findIndex(p => p[0] === x && p[1] === y); }
    draw(); onChange();
  });
  cv.addEventListener('pointermove', (e) => {
    if (drag < 0) return;
    const pts = params[ch];
    const [x, y] = toPt(e);
    const r = cv.getBoundingClientRect();
    const out = e.clientY < r.top - 30 || e.clientY > r.bottom + 30;
    if (out && pts.length > 2 && drag > 0 && drag < pts.length - 1) { pts.splice(drag, 1); drag = -1; }
    else {
      const lo = drag > 0 ? pts[drag - 1][0] + 1 : 0, hi = drag < pts.length - 1 ? pts[drag + 1][0] - 1 : 255;
      pts[drag] = [drag === 0 ? Math.min(x, hi) : drag === pts.length - 1 ? Math.max(x, lo) : clamp(x, lo, hi), y];
    }
    draw(); onChange();
  });
  cv.addEventListener('pointerup', () => { drag = -1; });
  cv.addEventListener('dblclick', (e) => {
    const [x, y] = toPt(e), pts = params[ch];
    const i = pts.findIndex(([px, py]) => Math.hypot(px - x, py - y) < 14);
    if (i > 0 && i < pts.length - 1) { pts.splice(i, 1); draw(); onChange(); }
  });
  draw();
  return h('div', null,
    segmented([['master', 'RGB'], ['r', '빨강'], ['g', '초록'], ['b', '파랑']], () => ch, v => { ch = v; draw(); }),
    cv,
    row(button('초기화', () => { params[ch] = [[0, 0], [255, 255]]; draw(); onChange(); }),
      button('S자 (대비↑)', () => { params[ch] = [[0, 0], [64, 48], [192, 208], [255, 255]]; draw(); onChange(); }),
      button('밝게', () => { params[ch] = [[0, 0], [128, 168], [255, 255]]; draw(); onChange(); })),
    h('div.help', null, '탭: 점 추가 · 끌기: 이동 · 위아래 밖으로 끌거나 더블탭: 점 삭제'),
  );
}

// ---------------------------------------------------------------- 그라데이션 맵 편집기
const GRADMAP_PRESETS = {
  '노을': [{ pos: 0, color: '#1b1430' }, { pos: 0.5, color: '#c2577a' }, { pos: 1, color: '#ffe9c4' }],
  '세피아': [{ pos: 0, color: '#2b1a0e' }, { pos: 0.6, color: '#a87a4f' }, { pos: 1, color: '#fbf0dc' }],
  '흑백': [{ pos: 0, color: '#000000' }, { pos: 1, color: '#ffffff' }],
  '푸른 밤': [{ pos: 0, color: '#05081a' }, { pos: 0.55, color: '#3b5ba5' }, { pos: 1, color: '#d9f2ff' }],
  '파스텔': [{ pos: 0, color: '#6c5b7b' }, { pos: 0.5, color: '#f8b195' }, { pos: 1, color: '#fff6e0' }],
  '청록·주황': [{ pos: 0, color: '#0c2f3a' }, { pos: 0.5, color: '#5d8a8c' }, { pos: 1, color: '#ffc58a' }],
};

function gradmapEditor(params, onChange) {
  const box = h('div');
  const bar = h('div.grad-bar');
  const render = () => {
    box.innerHTML = '';
    const stops = params.stops.sort((a, b) => a.pos - b.pos);
    bar.style.background = `linear-gradient(90deg, ${stops.map(s => `${s.color} ${s.pos * 100}%`).join(',')})`;
    box.append(bar);
    stops.forEach((s, i) => box.append(row(
      colorInput('', () => s.color, v => { s.color = v; bar.style.background = `linear-gradient(90deg, ${params.stops.map(t => `${t.color} ${t.pos * 100}%`).join(',')})`; onChange(); }),
      slider('위치', { min: 0, max: 1, step: 0.01, get: () => s.pos, set: v => { s.pos = v; onChange(); }, onEnd: render, format: v => Math.round(v * 100) + '%' }),
      stops.length > 2 ? button('×', () => { params.stops.splice(i, 1); render(); onChange(); }) : null,
    )));
    box.append(row(
      button('+ 색 추가', () => { params.stops.push({ pos: 0.5, color: app.color.fg }); render(); onChange(); }),
      select('프리셋', [['', '(선택)'], ...Object.keys(GRADMAP_PRESETS).map(k => [k, k])], () => '', v => { if (v) { params.stops = clone(GRADMAP_PRESETS[v]); render(); onChange(); } }),
    ));
  };
  render();
  return box;
}

// ---------------------------------------------------------------- 보정 대화상자
function controlsFor(type, p, onChange) {
  const S = (label, key, min, max, step = 1, fmt) => slider(label, { min, max, step, get: () => p[key], set: v => { p[key] = v; onChange(); }, format: fmt });
  switch (type) {
    case 'levels': {
      const hist = histogram(app.doc.active);
      const cv = h('canvas.hist', { width: 512, height: 120 });
      const draw = () => drawHist(cv, hist, (x, W, H) => {
        x.fillStyle = '#e8a33d';
        for (const v of [p.inB, p.inW]) x.fillRect(v / 255 * W - 1, 0, 3, H);
        x.fillStyle = '#ccc'; const mid = p.inB + (p.inW - p.inB) * Math.pow(0.5, p.gamma); x.fillRect(mid / 255 * W - 1, 0, 2, H);
      });
      draw();
      const ch = () => { draw(); onChange(); };
      return [cv,
        slider('입력 검정', { min: 0, max: 254, get: () => p.inB, set: v => { p.inB = Math.min(v, p.inW - 1); ch(); } }),
        slider('감마 (중간톤)', { min: 0.1, max: 5, step: 0.01, log: true, get: () => p.gamma, set: v => { p.gamma = v; ch(); }, format: v => v.toFixed(2) }),
        slider('입력 흰색', { min: 1, max: 255, get: () => p.inW, set: v => { p.inW = Math.max(v, p.inB + 1); ch(); } }),
        slider('출력 검정', { min: 0, max: 255, get: () => p.outB, set: v => { p.outB = v; ch(); } }),
        slider('출력 흰색', { min: 0, max: 255, get: () => p.outW, set: v => { p.outW = v; ch(); } }),
        button('자동', () => {
          const tot = hist.reduce((a, b) => a + b, 0); let acc = 0, lo = 0, hi = 255;
          for (let i = 0; i < 256; i++) { acc += hist[i]; if (acc > tot * 0.005) { lo = i; break; } }
          acc = 0;
          for (let i = 255; i >= 0; i--) { acc += hist[i]; if (acc > tot * 0.005) { hi = i; break; } }
          p.inB = lo; p.inW = Math.max(lo + 1, hi); ch();
          cv.parentElement?.querySelectorAll('.ctl').forEach(c => c.sync?.());
        })];
    }
    case 'curves': return [curveEditor(p, onChange)];
    case 'hsl': return [S('색조', 'hue', -180, 180, 1, v => v + '°'), S('채도', 'sat', -100, 100), S('명도', 'light', -100, 100)];
    case 'balance': return [
      h('div.section-title', null, '어두운 부분'), S('시안 ↔ 빨강', 'sr', -100, 100), S('마젠타 ↔ 초록', 'sg', -100, 100), S('노랑 ↔ 파랑', 'sb', -100, 100),
      h('div.section-title', null, '중간 톤'), S('시안 ↔ 빨강', 'mr', -100, 100), S('마젠타 ↔ 초록', 'mg', -100, 100), S('노랑 ↔ 파랑', 'mb', -100, 100),
      h('div.section-title', null, '밝은 부분'), S('시안 ↔ 빨강', 'hr', -100, 100), S('마젠타 ↔ 초록', 'hg', -100, 100), S('노랑 ↔ 파랑', 'hb', -100, 100)];
    case 'bc': return [S('밝기', 'b', -100, 100), S('대비', 'c', -100, 100)];
    case 'gradmap': return [gradmapEditor(p, onChange)];
    case 'posterize': return [S('단계 수', 'levels', 2, 32)];
    case 'threshold': return [S('경계값', 't', 1, 255)];
    case 'bw': return [S('빨강', 'r', 0, 100), S('초록', 'g', 0, 100), S('파랑', 'b', 0, 100)];
    case 'colorize': return [colorInput('색', () => p.color, v => { p.color = v; onChange(); }), S('세기', 'amount', 0, 100, 1, v => v + '%')];
    case 'vibrance': return [S('채도 강화', 'v', -100, 100)];
    default: return [h('div.help', null, '설정할 값이 없습니다.')];
  }
}

/**
 * 보정 대화상자. editLayer 가 조정 레이어면 그 값을 편집, 아니면 현재 레이어에 적용(또는 조정 레이어로 만들기)
 */
export function adjustDialog(type, editLayer = null) {
  const doc = app.doc;
  const def = ADJUST_TYPES[type];
  const params = clone(editLayer ? { ...def.def, ...editLayer.meta.params } : def.def);
  const target = editLayer || doc.active;
  if (!editLayer && (!target || target.isFolder || target.kind === 'adjust')) {
    app.toast('보정할 그림 레이어를 고르거나, "조정 레이어"로 만드세요');
  }
  let previewOn = true;
  const update = debounce(() => {
    if (!previewOn) { app.preview = null; app.requestRender(); return; }
    app.preview = { layer: target, inner: adjustInner(type, params) };
    app.requestRender();
  }, 30);
  update.flush();
  const body = h('div.adjust-body', null, ...controlsFor(type, params, update),
    toggle('미리보기', () => previewOn, v => { previewOn = v; update(); }));
  const close = () => { app.preview = null; app.requestRender(); };
  const actions = editLayer
    ? [{ label: '취소', run: close }, {
      label: '확인', primary: true,
      run: () => { close(); const prev = clone(editLayer.meta); ops.setLayerProps(editLayer, { meta: { ...prev, params: clone(params) } }, def.name + ' 수정'); },
    }]
    : [{ label: '취소', run: close },
      { label: '조정 레이어로', run: () => { close(); addAdjustLayer(type, params); } },
      { label: '적용', primary: true, run: () => { close(); app.task(def.name, () => ops.applyCanvasFilter(def.name, defineFilter('adj-apply', adjustInner(type, params), app.doc.width, app.doc.height))); } }];
  openModal({ title: def.name + (editLayer ? ' (조정 레이어)' : ''), body, actions, cls: 'adjust-modal', onClose: (r) => { if (r === null) close(); } });
}

export function addAdjustLayer(type, params) {
  const def = ADJUST_TYPES[type];
  const l = ops.addLayer({ name: def.name, kind: 'adjust', meta: { type, params: clone(params || def.def) } });
  app.toast('조정 레이어: 더블탭으로 값 수정, 클리핑하면 바로 아래 레이어에만 적용');
  return l;
}

// ---------------------------------------------------------------- 레이어 효과
export function fxDialog(layer = app.doc.active) {
  if (!layer || layer.kind === 'adjust') { app.toast('그림/폴더 레이어를 고르세요'); return; }
  const before = layer.fx ? clone(layer.fx) : null;
  const fx = layer.fx = clone(before || {
    stroke: { on: false, size: 6, color: '#ffffff' },
    shadow: { on: false, dx: 8, dy: 8, blur: 10, color: '#000000', opacity: 0.5 },
    glow: { on: false, size: 16, color: '#ffe680', opacity: 0.8 },
  });
  for (const k of ['stroke', 'shadow', 'glow']) fx[k] = fx[k] || {};
  const upd = () => { layer.version++; app.requestRender(); };
  const S = (o, label, key, min, max, step = 1, fmt) => slider(label, { min, max, step, get: () => o[key], set: v => { o[key] = v; upd(); }, format: fmt });
  const body = h('div',
    null,
    section('테두리 (경계 효과)', toggle('켜기', () => fx.stroke.on, v => { fx.stroke.on = v; upd(); }), S(fx.stroke, '두께', 'size', 1, 60), colorInput('색', () => fx.stroke.color, v => { fx.stroke.color = v; upd(); })),
    section('그림자', toggle('켜기', () => fx.shadow.on, v => { fx.shadow.on = v; upd(); }), S(fx.shadow, '가로', 'dx', -100, 100), S(fx.shadow, '세로', 'dy', -100, 100), S(fx.shadow, '흐림', 'blur', 0, 100), S(fx.shadow, '진하기', 'opacity', 0, 1, 0.01, v => Math.round(v * 100) + '%'), colorInput('색', () => fx.shadow.color, v => { fx.shadow.color = v; upd(); })),
    section('외부 광선', toggle('켜기', () => fx.glow.on, v => { fx.glow.on = v; upd(); }), S(fx.glow, '크기', 'size', 1, 120), S(fx.glow, '진하기', 'opacity', 0, 1, 0.01, v => Math.round(v * 100) + '%'), colorInput('색', () => fx.glow.color, v => { fx.glow.color = v; upd(); })),
    h('div.help', null, '그림을 바꾸지 않는 효과입니다. 나중에 그림을 고쳐도 효과가 따라옵니다 (스티커 테두리, 글자 외곽선, 발광 등).'),
  );
  upd();
  let done = false;
  const finish = (ok) => {
    if (done) return;
    done = true;
    const after = clone(layer.fx);
    layer.fx = before;
    if (ok) ops.setLayerProps(layer, { fx: (after.stroke.on || after.shadow.on || after.glow.on) ? after : null }, '레이어 효과');
    upd();
    app.bus.emit('layers:changed');
  };
  openModal({ title: `레이어 효과: ${layer.name}`, body, actions: [{ label: '취소', run: () => finish(false) }, { label: '확인', primary: true, run: () => finish(true) }], onClose: (r) => { if (r === null) finish(false); } });
}

// ---------------------------------------------------------------- 톤화
export function toneDialog(layer = app.doc.active) {
  if (!layer || layer.isFolder || layer.kind === 'adjust') { app.toast('그림 레이어를 고르세요'); return; }
  const before = layer.tone ? { ...layer.tone } : null;
  const t = layer.tone = { spacing: 8, angle: 45, shape: 'dot', color: '#000000', ...(before || {}) };
  const upd = debounce(() => { layer.tone = { ...t }; app.requestRender(); }, 120);
  const S = (label, key, min, max, step = 1, fmt) => slider(label, { min, max, step, get: () => t[key], set: v => { t[key] = v; upd(); }, format: fmt });
  const body = h('div', null,
    segmented([['dot', '점 망점'], ['line', '선 망점']], () => t.shape, v => { t.shape = v; upd(); }),
    S('망점 간격 (px)', 'spacing', 3, 40, 0.5),
    S('각도', 'angle', 0, 90, 1, v => v + '°'),
    colorInput('망점 색', () => t.color, v => { t.color = v; upd(); }),
    h('div.help', null, `현재 간격 ≈ ${Math.round((app.doc.dpi || 350) / 8)}선 (350dpi 기준). 회색으로 칠하면 진하기에 맞춰 점이 커집니다. 그리는 동안은 회색으로 보이고, 펜을 떼면 망점으로 바뀝니다.`),
  );
  upd.flush();
  let done = false;
  const finish = (ok) => {
    if (done) return;
    done = true;
    const after = { ...layer.tone };
    layer.tone = before;
    if (ok) ops.setLayerProps(layer, { tone: after }, '톤화');
    app.requestRender();
    app.bus.emit('layers:changed');
  };
  openModal({
    title: `톤화: ${layer.name}`, body,
    actions: [
      before ? { label: '톤화 끄기', run: () => { done = true; layer.tone = before; ops.setLayerProps(layer, { tone: null }, '톤화 끄기'); app.requestRender(); } } : null,
      { label: '취소', run: () => finish(false) }, { label: '확인', primary: true, run: () => finish(true) },
    ].filter(Boolean),
    onClose: (r) => { if (r === null) finish(false); },
  });
}

// ---------------------------------------------------------------- 필터 · 선택
export function registerAdjustCommands() {
  const C = (id, label, run, extra = {}) => app.registerCommand(id, { label, run, ...extra });
  for (const [type, def] of Object.entries(ADJUST_TYPES)) {
    C(`adjust.${type}`, def.name + '…', () => adjustDialog(type));
    C(`adjlayer.${type}`, '조정 레이어: ' + def.name, () => addAdjustLayer(type));
  }
  C('layer.fx', '레이어 효과 (테두리/그림자/광선)…', () => fxDialog());
  C('layer.tone', '톤화 (망점으로 보이기)…', () => toneDialog());
  C('layer.editAdjust', '조정 레이어 값 수정', () => { const l = app.doc.active; if (l?.kind === 'adjust') adjustDialog(l.meta.type, l); });

  C('filter.unsharp', '언샤프 마스크 (선명하게)…', async () => {
    const v = await form('언샤프 마스크', [{ key: 'r', label: '반경(px)', type: 'number', value: 2, step: 0.5 }, { key: 'a', label: '양(%)', type: 'number', value: 80 }]);
    if (v) app.task('선명하게', () => ops.applyCanvasFilter('언샤프 마스크', unsharpFilter(v.r, v.a / 100, app.doc.width, app.doc.height)));
  });
  C('filter.noise', '노이즈 추가…', async () => {
    const v = await form('노이즈', [{ key: 'a', label: '양(%)', type: 'number', value: 20 }, { key: 'mono', label: '흑백 노이즈', type: 'checkbox', value: true }]);
    if (v) app.task('노이즈', () => ops.applyCanvasFilter('노이즈', noiseFilter(v.a, v.mono, (Math.random() * 1000) | 0, app.doc.width, app.doc.height)));
  });
  C('filter.mosaic', '모자이크…', async () => {
    const v = await form('모자이크', [{ key: 's', label: '칸 크기(px)', type: 'number', value: Math.max(8, Math.round(app.doc.width / 80)) }, { key: 'info', type: 'info', label: '선택 영역이 있으면 그 안만 (가리기용).' }]);
    if (!v) return;
    const s = Math.max(2, Math.round(v.s));
    app.task('모자이크', () => ops.applyCanvasOp('모자이크', (t, src) => {
      const small = createCanvas(Math.ceil(src.width / s), Math.ceil(src.height / s));
      const sx = small.getContext('2d');
      sx.imageSmoothingQuality = 'high';
      sx.drawImage(src, 0, 0, small.width, small.height);
      t.imageSmoothingEnabled = false;
      t.drawImage(small, 0, 0, small.width * s, small.height * s);
    }));
  });
  C('filter.motion', '방향 흐림 (모션 블러)…', async () => {
    const v = await form('방향 흐림', [{ key: 'a', label: '각도(도)', type: 'number', value: 0 }, { key: 'd', label: '거리(px)', type: 'number', value: 30 }]);
    if (!v) return;
    app.task('방향 흐림', () => ops.applyCanvasOp('방향 흐림', (t, src) => {
      const n = Math.min(64, Math.max(4, Math.round(v.d / 2))), rad = v.a * Math.PI / 180;
      for (let i = 0; i < n; i++) {
        const k = (i / (n - 1) - 0.5) * v.d;
        t.globalAlpha = 1 / (i + 1);
        t.drawImage(src, Math.cos(rad) * k, Math.sin(rad) * k);
      }
    }));
  });
  C('filter.zoomblur', '방사형 흐림 (줌)…', async () => {
    const doc = app.doc, sel = doc.selection;
    const v = await form('방사형 흐림', [{ key: 'amt', label: '세기(%)', type: 'number', value: 15 }, { key: 'info', type: 'info', label: '중심은 선택 영역 가운데(없으면 캔버스 가운데). 집중·속도감 연출.' }]);
    if (!v) return;
    const c = sel.active ? { x: sel.bounds.x + sel.bounds.w / 2, y: sel.bounds.y + sel.bounds.h / 2 } : { x: doc.width / 2, y: doc.height / 2 };
    app.task('방사형 흐림', () => ops.applyCanvasOp('방사형 흐림', (t, src) => {
      const n = 24;
      for (let i = 0; i < n; i++) {
        const s = 1 + (i / (n - 1)) * v.amt / 100;
        t.setTransform(s, 0, 0, s, c.x * (1 - s), c.y * (1 - s));
        t.globalAlpha = 1 / (i + 1);
        t.drawImage(src, 0, 0);
      }
    }));
  });
  C('filter.glowBlur', '빛 번짐 (블룸)…', async () => {
    const v = await form('빛 번짐', [{ key: 'r', label: '번짐 반경(px)', type: 'number', value: 20 }, { key: 'a', label: '세기(%)', type: 'number', value: 50 }]);
    if (!v) return;
    app.task('빛 번짐', () => ops.applyCanvasOp('빛 번짐', (t, src) => {
      t.drawImage(src, 0, 0);
      t.globalCompositeOperation = 'screen';
      t.globalAlpha = v.a / 100;
      t.filter = `blur(${v.r}px) brightness(1.2)`;
      t.drawImage(src, 0, 0);
      t.filter = 'none';
    }));
  });

  C('select.feather', '선택 경계 흐리기…', async () => {
    const sel = app.doc.selection;
    if (!sel.active) { app.toast('선택 영역이 없습니다'); return; }
    const v = await form('경계 흐리기', [{ key: 'r', label: '반경(px)', type: 'number', value: 8 }]);
    if (!v) return;
    selectionHistory(() => {
      const t = createCanvas(sel.mask.width, sel.mask.height), x = t.getContext('2d');
      x.filter = `blur(${v.r}px)`;
      x.drawImage(sel.mask, 0, 0);
      const mx = sel.mask.getContext('2d');
      mx.clearRect(0, 0, t.width, t.height);
      mx.drawImage(t, 0, 0);
      const b = rectPad(sel.bounds, v.r * 3);
      sel.paths = null;
      sel._recompute(b);
      sel._changed();
    }, '경계 흐리기');
  });
  C('select.colorRange', '색역 선택 (같은 색 전부)…', async () => {
    const v = await form('색역 선택', [
      { key: 'color', label: '고를 색', type: 'color', value: app.color.fg },
      { key: 'tol', label: '허용 범위 (0~255)', type: 'number', value: 32 },
      { key: 'src', label: '기준', type: 'segment', options: [['all', '보이는 그림'], ['layer', '현재 레이어']], value: 'all' },
      { key: 'op', label: '방식', type: 'segment', options: [['replace', '새로'], ['add', '추가'], ['subtract', '빼기']], value: 'replace' },
    ], { okLabel: '선택' });
    if (!v) return;
    await app.task('색역 선택', () => {
      const doc = app.doc, img = referenceImage(v.src), d = img.data, n = doc.width * doc.height;
      const r0 = parseInt(v.color.slice(1, 3), 16), g0 = parseInt(v.color.slice(3, 5), 16), b0 = parseInt(v.color.slice(5, 7), 16);
      const mask = new Uint8Array(n);
      let x0 = doc.width, y0 = doc.height, x1 = -1, y1 = -1;
      for (let i = 0, p = 0; i < n; i++, p += 4) {
        if (d[p + 3] < 16) continue;
        if (Math.max(Math.abs(d[p] - r0), Math.abs(d[p + 1] - g0), Math.abs(d[p + 2] - b0)) <= v.tol) {
          mask[i] = 1;
          const x = i % doc.width, y = (i / doc.width) | 0;
          if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
        }
      }
      if (x1 < 0) { app.toast('그 색을 찾지 못했습니다'); return; }
      selectionHistory(() => doc.selection.fromMask(mask, { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 }, v.op), '색역 선택');
    });
  });

  // 메뉴
  app.addMenu('adjust', '보정');
  const M = (items) => { for (const it of items) app.addMenuItem('adjust', it === '-' ? { sep: true } : typeof it === 'string' ? { command: it } : it); };
  M(['adjust.levels', 'adjust.curves', 'adjust.hsl', 'adjust.balance', 'adjust.bc', 'adjust.vibrance', '-', 'adjust.gradmap', 'adjust.colorize', 'adjust.bw', 'adjust.posterize', 'adjust.threshold', 'adjust.invert', '-',
    { label: '— 조정 레이어 만들기 (원본 보존) —', run: () => {} },
    ...Object.keys(ADJUST_TYPES).map(t => `adjlayer.${t}`), '-', 'layer.fx', 'layer.tone']);
  for (const id of ['filter.unsharp', 'filter.noise', 'filter.mosaic', 'filter.motion', 'filter.zoomblur', 'filter.glowBlur']) app.addMenuItem('filter', { command: id });
  for (const id of ['select.feather', 'select.colorRange']) app.addMenuItem('select', { command: id });
  for (const id of ['layer.fx', 'layer.tone']) app.addMenuItem('layer', { command: id });
}
