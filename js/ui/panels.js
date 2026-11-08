// 도구막대, 도킹 패널(도구 속성/색/레이어/에셋), 메뉴바, 상태바
import { app } from '../app.js';
import { h, hsvToRgb, rgbToHsv, hexToRgb, rgbToHex, clamp, throttleRAF } from '../core/util.js';
import { icon } from './icons.js';
import { slider, toggle, select, syncAll, button, row, section } from './controls.js';
import * as ops from '../core/ops.js';
import { BLEND_MODES } from '../core/doc.js';
import { server, setImgSrc } from '../core/io.js';
import { loadImage } from '../core/util.js';
import { activePreset, presets, setActivePreset } from '../tools/brush.js';
import { confirmDialog } from './dialogs.js';
import { toolLists, menuShown, menuItems, panelShown, moreToolsPopup, isSimple } from './layout.js';

// =====================================================================
// 도구막대
// =====================================================================
export const TOOL_ORDER = ['brush', 'eraser', 'smudge', 'blurbrush', 'fill', 'lassofill', 'gradient', 'eyedropper', 'select', 'selpen', 'transform', 'liquify', 'clone', 'shape', 'ruler', 'panel', 'bubble', 'text', 'speedlines', 'view'];

export function initToolbar() {
  const bar = document.getElementById('toolbar');
  const render = () => {
    bar.innerHTML = '';
    const all = [...TOOL_ORDER.filter(id => app.tools.has(id)), ...[...app.tools.keys()].filter(id => !TOOL_ORDER.includes(id))];
    const { shown: ids, more } = toolLists(all);
    for (const id of ids) {
      const t = app.tools.get(id);
      const b = h('button.tool-btn', { type: 'button', title: `${t.name}${t.key ? ` (${t.key.toUpperCase()})` : ''}`, 'data-tool': id });
      b.innerHTML = icon(t.icon, 24);
      b.addEventListener('click', () => {
        if (app.toolId === id) document.body.classList.remove('no-side');
        app.setTool(id);
      });
      bar.append(b);
    }
    if (more.length) {
      const mb = h('button.tool-btn.tool-more-btn', { type: 'button', title: `다른 도구 ${more.length}개`, html: icon('dots', 24) });
      mb.onclick = () => moreToolsPopup(mb, more);
      bar.append(mb);
    }
    bar.append(h('div.tool-sep'));
    const chips = h('div.color-chips');
    const fg = h('button.chip.fg', { type: 'button', title: '전경색' });
    const bg = h('button.chip.bg', { type: 'button', title: '배경색 (탭: 바꾸기)' });
    fg.onclick = () => showDockTab('color');
    bg.onclick = () => app.run('color.swap');
    chips.append(bg, fg);
    bar.append(chips);
    syncChips();
    highlight();
  };
  const syncChips = () => {
    bar.querySelector('.chip.fg')?.style.setProperty('background', app.color.fg);
    bar.querySelector('.chip.bg')?.style.setProperty('background', app.color.bg);
    document.documentElement.style.setProperty('--fg-color', app.color.fg);
  };
  const highlight = () => bar.querySelectorAll('.tool-btn').forEach(b => b.classList.toggle('on', b.dataset.tool === app.toolId));
  app.bus.on('tools:registered', render);
  app.bus.on('ui:layout', render);
  // 간단히 모드에서 막대에 없는 도구를 고르면 잠깐 막대에 보이도록
  app.bus.on('tool:changed', () => { if (isSimple() && !bar.querySelector(`[data-tool="${app.toolId}"]`)) render(); else highlight(); });
  app.bus.on('color:changed', syncChips);
  render();
}

// =====================================================================
// 도킹 영역
// =====================================================================
const docks = {};

export function showDockTab(id) {
  const p = app.panels.get(id);
  if (!p) return;
  const d = docks[p.dock];
  d.select(id);
}

export function initDocks() {
  for (const name of ['top', 'bottom']) {
    const sec = document.getElementById(`dock-${name}`);
    const tabs = sec.querySelector('.dock-tabs');
    const body = sec.querySelector('.dock-body');
    const mounted = new Map();
    let current = null;
    const dock = {
      select(id) {
        current = id;
        app.settings[`dock_${name}`] = id;
        for (const [pid, el] of mounted) if (!el._popped) el.hidden = pid !== id;
        if (!mounted.has(id)) {
          const p = app.panels.get(id);
          const el = h('div.panel-body', { 'data-panel': id });
          mounted.set(id, el);
          body.append(el);
          p.mount(el);
        }
        tabs.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.id === id));
      },
      render() {
        tabs.innerHTML = '';
        const list = [...app.panels.values()].filter(p => p.dock === name && panelShown(p.id) && !p.popped);
        for (const p of list) {
          const b = h('button', { type: 'button', 'data-id': p.id });
          b.innerHTML = `${icon(p.icon || 'dots', 16)}<span>${p.title}</span>`;
          b.onclick = () => dock.select(p.id);
          tabs.append(b);
        }
        tabs.append(h('button.popout-btn', { type: 'button', title: '지금 탭을 앱 밖 창으로 꺼내기 (다른 모니터로 옮기거나 항상 위에)', onclick: () => current && app.run('popout.panel', current) }, '⧉'));
        const want = app.settings[`dock_${name}`];
        const first = list.find(p => p.id === want) || list[0];
        if (first) dock.select(current && list.some(p => p.id === current) ? current : first.id);
      },
    };
    docks[name] = dock;
  }
  app.bus.on('panels:changed', () => { docks.top.render(); docks.bottom.render(); });
  app.bus.on('dock:show', (id) => showDockTab(id));
  docks.top.render();
  docks.bottom.render();

  // 위/아래 영역 크기 조절
  const split = document.getElementById('dock-split');
  const side = document.getElementById('sidepanel');
  const apply = () => side.style.setProperty('--split', `${app.settings.dockSplit * 100}%`);
  apply();
  split.addEventListener('pointerdown', (e) => {
    split.setPointerCapture(e.pointerId);
    const r = side.getBoundingClientRect();
    const mv = (ev) => { app.settings.dockSplit = clamp((ev.clientY - r.top) / r.height, 0.15, 0.85); apply(); };
    const up = () => { split.removeEventListener('pointermove', mv); split.removeEventListener('pointerup', up); app.saveSettingsSoon(); };
    split.addEventListener('pointermove', mv);
    split.addEventListener('pointerup', up);
  });
}

// =====================================================================
// 도구 속성 패널
// =====================================================================
export function toolPropsPanel(el) {
  const head = h('div.panel-head');
  const body = h('div.panel-scroll');
  el.append(head, body);
  const render = () => {
    const t = app.tool;
    body.innerHTML = '';
    head.innerHTML = t ? `${icon(t.icon, 18)}<b>${t.name}</b>` : '';
    if (t?.options) {
      try { t.options(body); } catch (e) { console.error(e); body.append(h('div.help', null, '옵션 표시 오류: ' + e.message)); }
    } else body.append(h('div.help', null, '설정할 옵션이 없습니다.'));
  };
  app.bus.on('tool:changed', render);
  app.bus.on('brush:changed', () => syncAll(body));
  app.bus.on('brush:presets', render);
  app.bus.on('tool:options', render);
  render();
}

// =====================================================================
// 색 패널
// =====================================================================
export function colorPanel(el) {
  let hsv = (() => { const { r, g, b } = hexToRgb(app.color.fg); return rgbToHsv(r, g, b); })();
  const sv = h('canvas.sv');
  const hue = h('canvas.hue');
  const hex = h('input.hex', { type: 'text', maxlength: 7, spellcheck: false });
  const fgChip = h('button.chip.fg', { type: 'button', title: '전경색' });
  const bgChip = h('button.chip.bg', { type: 'button', title: '배경색' });
  const swap = h('button.icon-btn', { type: 'button', title: '바꾸기 (X)', html: icon('swap', 18), onclick: () => app.run('color.swap') });
  const recent = h('div.swatches.recent');
  const palette = h('div.swatches');
  // HSV / RGB 숫자 슬라이더
  const cur = () => { const { r, g, b } = hexToRgb(app.color.fg); return { r, g, b, ...rgbToHsv(r, g, b) }; };
  const setRGB = (k) => (v) => { const c = cur(); c[k] = v; app.setColor(rgbToHex(c.r, c.g, c.b)); };
  const setHSV = (k) => (v) => { const c = cur(); const hs = { h: k === 'h' ? v : (k === 's' || k === 'v' ? hsv.h : c.h), s: k === 's' ? v / 100 : c.s, v: k === 'v' ? v / 100 : c.v }; const { r, g, b } = hsvToRgb(hs.h, hs.s, hs.v); if (k === 'h') hsv.h = v; app.setColor(rgbToHex(r, g, b)); };
  const sliders = h('div.color-sliders', null,
    slider('H', { min: 0, max: 359, get: () => Math.round(hsv.h), set: setHSV('h'), format: v => Math.round(v) + '°' }),
    slider('S', { min: 0, max: 100, get: () => Math.round(cur().s * 100), set: setHSV('s'), format: v => Math.round(v) + '%' }),
    slider('V', { min: 0, max: 100, get: () => Math.round(cur().v * 100), set: setHSV('v'), format: v => Math.round(v) + '%' }),
    slider('R', { min: 0, max: 255, get: () => cur().r, set: setRGB('r') }),
    slider('G', { min: 0, max: 255, get: () => cur().g, set: setRGB('g') }),
    slider('B', { min: 0, max: 255, get: () => cur().b, set: setRGB('b') }),
  );
  app.bus.on('color:changed', () => sliders.querySelectorAll('.ctl').forEach(c => c.sync?.()));
  el.append(h('div.panel-scroll', null,
    h('div.color-top', null, h('div.chips-big', null, bgChip, fgChip), swap, hex),
    sv, hue, sliders,
    h('div.section-title', null, '최근 색'), recent,
    h('div.section-title', null, '팔레트 ', h('small', null, '(길게 누르면 삭제)')), palette,
  ));

  const drawSV = () => {
    const W = sv.clientWidth || 240, H = sv.clientHeight || 150;
    const dpr = window.devicePixelRatio || 1;
    if (sv.width !== Math.round(W * dpr)) { sv.width = Math.round(W * dpr); sv.height = Math.round(H * dpr); }
    const x = sv.getContext('2d');
    const { r, g, b } = hsvToRgb(hsv.h, 1, 1);
    x.fillStyle = `rgb(${r},${g},${b})`;
    x.fillRect(0, 0, sv.width, sv.height);
    let gr = x.createLinearGradient(0, 0, sv.width, 0);
    gr.addColorStop(0, '#fff'); gr.addColorStop(1, 'rgba(255,255,255,0)');
    x.fillStyle = gr; x.fillRect(0, 0, sv.width, sv.height);
    gr = x.createLinearGradient(0, 0, 0, sv.height);
    gr.addColorStop(0, 'rgba(0,0,0,0)'); gr.addColorStop(1, '#000');
    x.fillStyle = gr; x.fillRect(0, 0, sv.width, sv.height);
    const px = hsv.s * sv.width, py = (1 - hsv.v) * sv.height;
    x.lineWidth = 2 * dpr;
    x.strokeStyle = hsv.v > 0.5 ? '#000' : '#fff';
    x.beginPath(); x.arc(px, py, 7 * dpr, 0, Math.PI * 2); x.stroke();
  };
  const drawHue = () => {
    const W = hue.clientWidth || 240, H = hue.clientHeight || 22;
    const dpr = window.devicePixelRatio || 1;
    if (hue.width !== Math.round(W * dpr)) { hue.width = Math.round(W * dpr); hue.height = Math.round(H * dpr); }
    const x = hue.getContext('2d');
    const gr = x.createLinearGradient(0, 0, hue.width, 0);
    for (let i = 0; i <= 6; i++) gr.addColorStop(i / 6, `hsl(${i * 60},100%,50%)`);
    x.fillStyle = gr; x.fillRect(0, 0, hue.width, hue.height);
    const px = hsv.h / 360 * hue.width;
    x.lineWidth = 3 * dpr; x.strokeStyle = '#fff';
    x.strokeRect(px - 3 * dpr, 1, 6 * dpr, hue.height - 2);
  };
  const commit = () => {
    const { r, g, b } = hsvToRgb(hsv.h, hsv.s, hsv.v);
    app.setColor(rgbToHex(r, g, b));
  };
  const drag = (canvas, fn) => {
    canvas.addEventListener('pointerdown', (e) => {
      canvas.setPointerCapture(e.pointerId);
      const mv = (ev) => { const r = canvas.getBoundingClientRect(); fn(clamp((ev.clientX - r.left) / r.width, 0, 1), clamp((ev.clientY - r.top) / r.height, 0, 1)); commit(); };
      mv(e);
      canvas.onpointermove = mv;
      canvas.onpointerup = () => { canvas.onpointermove = null; };
    });
  };
  drag(sv, (u, v) => { hsv.s = u; hsv.v = 1 - v; drawSV(); });
  drag(hue, (u) => { hsv.h = u * 359.9; drawSV(); drawHue(); });
  hex.addEventListener('change', () => {
    let v = hex.value.trim();
    if (!v.startsWith('#')) v = '#' + v;
    if (/^#[0-9a-f]{6}$/i.test(v)) app.setColor(v.toLowerCase());
  });
  bgChip.onclick = () => app.run('color.swap');

  const swatch = (c, onTap, onLong) => {
    const s = h('button.sw', { type: 'button', title: c, style: { background: c } });
    let t = 0, long = false;
    s.addEventListener('pointerdown', () => { long = false; t = setTimeout(() => { long = true; onLong?.(); }, 600); });
    s.addEventListener('pointerup', () => clearTimeout(t));
    s.addEventListener('pointerleave', () => clearTimeout(t));
    s.addEventListener('click', () => { if (!long) onTap(); });
    s.addEventListener('contextmenu', (e) => { e.preventDefault(); onLong?.(); });
    return s;
  };
  const renderSwatches = () => {
    recent.innerHTML = '';
    for (const c of app.settings.recentColors) recent.append(swatch(c, () => app.setColor(c)));
    palette.innerHTML = '';
    app.settings.palette.forEach((c, i) => palette.append(swatch(c, () => app.setColor(c), async () => {
      if (await confirmDialog(`${c} 색을 팔레트에서 뺄까요?`, '빼기')) { app.settings.palette.splice(i, 1); app.saveSettingsSoon(); renderSwatches(); }
    })));
    palette.append(h('button.sw.add', { type: 'button', title: '현재 색 추가', html: icon('plus', 16), onclick: () => { app.settings.palette.push(app.color.fg); app.saveSettingsSoon(); renderSwatches(); } }));
  };
  const sync = () => {
    const { r, g, b } = hexToRgb(app.color.fg);
    const n = rgbToHsv(r, g, b);
    // 무채색일 때 색조 유지
    if (n.s > 0.001 && n.v > 0.001) hsv.h = n.h;
    hsv.s = n.s; hsv.v = n.v;
    hex.value = app.color.fg;
    fgChip.style.background = app.color.fg;
    bgChip.style.background = app.color.bg;
    drawSV(); drawHue();
  };
  app.bus.on('color:changed', sync);
  app.bus.on('recent:changed', renderSwatches);
  new ResizeObserver(() => { drawSV(); drawHue(); }).observe(sv);
  renderSwatches();
  sync();
}

// =====================================================================
// 레이어 패널
// =====================================================================
export function layersPanel(el) {
  const tb = h('div.layer-toolbar');
  const tbtn = (ic, title, cmd) => h('button.icon-btn', { type: 'button', title, html: icon(ic, 18), onclick: () => app.run(cmd) });
  tb.append(
    tbtn('plus', '새 레이어', 'layer.new'),
    tbtn('folder', '새 폴더', 'layer.newFolder'),
    h('button.icon-btn.txt', { type: 'button', title: '새 벡터 레이어', onclick: () => app.run('layer.newVector') }, '벡터'),
    h('button.icon-btn.txt', { type: 'button', title: '새 러프 레이어', onclick: () => app.run('layer.newRough') }, '러프'),
    tbtn('copy', '복제', 'layer.duplicate'),
    tbtn('merge', '아래로 병합', 'layer.mergeDown'),
    tbtn('up', '위로', 'layer.up'),
    tbtn('down', '아래로', 'layer.down'),
    tbtn('trash', '삭제', 'layer.delete'),
  );
  // 여러 개 선택 (터치에서는 이 버튼을 켜고 탭으로 고르기, 키보드는 Ctrl/Shift+클릭)
  let multiMode = false;
  const multiBtn = h('button.icon-btn.txt', { type: 'button', title: '여러 레이어 선택 (Ctrl/Shift+클릭과 같음)', onclick: () => {
    multiMode = !multiMode;
    multiBtn.classList.toggle('on', multiMode);
    if (!multiMode) ops.clearMulti(); else if (app.doc.active) { app.doc.multi = new Set([app.doc.active.id]); render(); }
  } }, '여러 개');
  tb.append(multiBtn);
  const multiBar = h('div.multi-bar');
  const props = h('div.layer-props');
  const list = h('div.layer-list');
  el.append(tb, multiBar, props, list);

  let edit = null;
  const L = () => app.doc?.active;
  const opacity = slider('불투명도', {
    min: 0, max: 1, step: 0.01, get: () => L()?.opacity ?? 1,
    set: (v) => { const l = L(); if (!l) return; if (!edit) edit = ops.beginPropEdit(l, ['opacity']); l.opacity = v; app.requestRender(); },
    format: v => Math.round(v * 100) + '%',
    onEnd: () => { edit?.end('불투명도'); edit = null; app.bus.emit('layers:changed'); },
  });
  const blend = select('합성', BLEND_MODES, () => L()?.blend ?? 'source-over', (v) => ops.setLayerProps(L(), { blend: v }, '합성 모드'));
  const flag = (label, key, title, val) => {
    const b = h('button.flag', { type: 'button', title }, label);
    b.onclick = () => { const l = L(); if (l) ops.setLayerProps(l, { [key]: val ? (l[key] ? null : val) : !l[key] }, title); };
    b.sync = () => b.classList.toggle('on', !!L()?.[key]);
    return b;
  };
  const flags = [
    flag('클리핑', 'clip', '아래 레이어에서 클리핑'),
    flag('투명잠금', 'alphaLock', '투명 픽셀 잠금'),
    flag('잠금', 'locked', '레이어 잠금'),
    flag('밑그림', 'draft', '밑그림 (내보내기·채우기 참조 제외)'),
    flag('파란선', 'tint', '레이어 컬러', '#3d7be0'),
    flag('참조', 'ref', '참조 레이어 (채우기·자동선택 기준)'),
  ];
  const maskBtn = h('button.flag', { type: 'button', title: '레이어 마스크: 없으면 만들고, 있으면 마스크 편집 전환' }, '+마스크');
  maskBtn.onclick = () => app.run('layer.editMask');
  maskBtn.sync = () => {
    const l = L();
    maskBtn.classList.toggle('on', !!l?.editMask);
    maskBtn.textContent = l?.mask ? (l.editMask ? '마스크 편집 중' : '마스크') : '+마스크';
  };
  flags.push(maskBtn);
  const cmdFlag = (label, title, cmd, on) => {
    const b = h('button.flag', { type: 'button', title }, label);
    b.onclick = () => app.run(cmd);
    b.sync = () => b.classList.toggle('on', !!on(L()));
    return b;
  };
  flags.push(cmdFlag('효과', '레이어 효과 (테두리/그림자/광선)', 'layer.fx', l => l?.fx), cmdFlag('톤', '톤화 (망점)', 'layer.tone', l => l?.tone),
    cmdFlag('러프', '러프 전용 레이어 (러프 연필은 여기서만)', 'layer.rough', l => l?.rough), cmdFlag('러프 색', '러프 색 바꾸기', 'layer.roughColor', () => false));
  props.append(opacity, blend, h('div.flags', null, ...flags));

  const thumbs = new Map();   // layer.id -> {canvas, version}
  const drawThumb = (l, c) => {
    const doc = app.doc;
    const s = Math.min(c.width / doc.width, c.height / doc.height);
    const x = c.getContext('2d');
    x.clearRect(0, 0, c.width, c.height);
    const w = doc.width * s, hh = doc.height * s;
    x.fillStyle = '#fff';
    x.fillRect((c.width - w) / 2, (c.height - hh) / 2, w, hh);
    x.drawImage(l.displayCanvas, (c.width - w) / 2, (c.height - hh) / 2, w, hh);
  };

  const makeRow = (l, depth) => {
    const doc = app.doc;
    const eye = h('button.icon-btn.eye', { type: 'button', title: '보이기/숨기기', html: icon(l.visible ? 'eye' : 'eyeOff', 18) });
    eye.onclick = (e) => { e.stopPropagation(); ops.setLayerProps(l, { visible: !l.visible }, '표시'); };
    let th;
    if (l.isFolder) {
      const anim = l.meta?.anim;
      th = h('button.folder-tg', { type: 'button', title: '펼치기/접기', html: `<span class="caret">${l.expanded ? '▾' : '▸'}</span>${icon(anim ? 'film' : 'folder', 22)}` });
      th.onclick = (e) => { e.stopPropagation(); l.expanded = !l.expanded; render(); };
    } else if (l.kind === 'adjust') {
      th = h('button.folder-tg', { type: 'button', title: '값 수정', html: icon('adjust', 24) });
      th.onclick = (e) => { e.stopPropagation(); doc.setActive(l); app.run('layer.editAdjust'); };
    } else {
      th = h('canvas.thumb', { width: 64, height: 48 });
      drawThumb(l, th);
      thumbs.set(l.id, { canvas: th, version: l.version });
    }
    const badges = h('span.badges', null,
      l.clip ? h('i.b-clip', { title: '클리핑' }, '↳') : null,
      l.locked ? h('i', { html: icon('lock', 12) }) : null,
      l.alphaLock ? h('i', { title: '투명잠금' }, 'α') : null,
      l.draft ? h('i', { title: '밑그림' }, '밑') : null,
      l.rough ? h('i.b-rough', { title: '러프 레이어', style: { background: l.tint || '#3d7be0' } }, '러프') : null,
      l.ref ? h('i', { title: '참조 레이어' }, '참') : null,
      l.fx ? h('i', { title: '레이어 효과' }, 'fx') : null,
      l.tone ? h('i', { title: '톤화' }, '톤') : null,
      l.mask ? h(`i${l.editMask ? '.b-on' : ''}`, { title: l.maskOn ? '마스크' : '마스크 꺼짐' }, l.maskOn ? 'M' : 'm') : null,
      !['raster', 'folder', 'adjust'].includes(l.kind) ? h('i', { title: l.kind }, { text: 'T', panel: '컷', bubble: '풍', vector: '벡터' }[l.kind] || '') : null,
    );
    const sub = l.isFolder
      ? `${doc.childrenOf(l).length}개${l.meta?.anim ? ' 프레임' : ''} · ${Math.round(l.opacity * 100)}%`
      : `${Math.round(l.opacity * 100)}% ${BLEND_MODES.find(b => b[0] === l.blend)?.[1] || ''}`;
    const name = h('div.lname', null, h('span', null, l.name), h('small', null, sub));
    const grip = h('span.grip', { html: icon('grip', 16), title: '끌어서 순서 변경 / 폴더에 넣기' });
    const row = h(`div.layer${l === doc.active ? '.on' : ''}${l.clip ? '.clipped' : ''}${!l.visible ? '.hidden' : ''}${l.isFolder ? '.folder' : ''}`, { 'data-id': l.id }, eye, th, name, badges, grip);
    row.style.marginLeft = depth * 14 + 'px';
    if (l.tint) row.style.setProperty('--tint', l.tint);
    if (doc.multi?.has(l.id)) row.classList.add('multi');
    row.addEventListener('click', (e) => {
      if (e.shiftKey || e.ctrlKey || e.metaKey || multiMode) {
        const set = doc.multi || new Set(doc.active ? [doc.active.id] : []);
        if (e.shiftKey && doc.active) {
          // 화면에 보이는 순서로 범위 선택
          const ids = [...list.querySelectorAll('.layer[data-id]')].map(r => r.dataset.id);
          const a = ids.indexOf(doc.active.id), b = ids.indexOf(l.id);
          for (let i = Math.min(a, b); i <= Math.max(a, b); i++) if (i >= 0) set.add(ids[i]);
        } else if (set.has(l.id) && set.size > 1) set.delete(l.id);
        else set.add(l.id);
        doc.multi = set;
        if (set.has(l.id)) doc.setActive(l); else render();
        return;
      }
      if (doc.multi) doc.multi = null;
      if (doc.active !== l) doc.setActive(l); else render();
    });
    row.addEventListener('dblclick', async () => {
      if (l.kind === 'adjust') { doc.setActive(l); app.run('layer.editAdjust'); return; }
      const n = prompt('레이어 이름', l.name);
      if (n) ops.setLayerProps(l, { name: n }, '이름 변경');
    });
    grip.addEventListener('pointerdown', (e) => startDrag(e, l, row));
    return row;
  };

  const render = () => {
    const doc = app.doc;
    list.innerHTML = '';
    if (!doc) return;
    // 위에서 아래로, 폴더는 펼쳐져 있으면 안쪽도
    const walk = (folder, depth) => {
      const kids = doc.childrenOf(folder);
      for (let i = kids.length - 1; i >= 0; i--) {
        const l = kids[i];
        list.append(makeRow(l, depth));
        if (l.isFolder && l.expanded) walk(l, depth + 1);
      }
    };
    walk(null, 0);
    // 여러 개 선택했을 때 일괄 작업
    if (doc.multi) for (const id of [...doc.multi]) if (!doc.byId(id)) doc.multi.delete(id);
    const n = doc.multi?.size || 0;
    multiBar.innerHTML = '';
    multiBar.style.display = n > 1 ? '' : 'none';
    if (n > 1) {
      const b = (label, fn) => h('button.btn', { type: 'button', onclick: fn }, label);
      multiBar.append(h('b', null, `${n}개 선택`),
        b('보이기/숨기기', () => ops.setLayersVisible()),
        b('폴더로 묶기', () => ops.groupLayers()),
        b('병합', () => ops.mergeLayers()),
        b('삭제', () => ops.deleteLayers()),
        b('선택 해제', () => { multiMode = false; multiBtn.classList.remove('on'); ops.clearMulti(); }));
    }
    // 용지
    const pv = doc.paper;
    const paperEye = h('button.icon-btn.eye', { type: 'button', html: icon(pv.visible ? 'eye' : 'eyeOff', 18), onclick: () => { pv.visible = !pv.visible; app.markModified(); render(); app.requestRender(); } });
    const pc = h('input', { type: 'color', value: pv.color });
    pc.oninput = () => { pv.color = pc.value; app.markModified(); app.requestRender(); };
    const tex = h('button.icon-btn.txt', { type: 'button', title: '용지 질감 (종이 결)', onclick: () => app.run('paper.texture') }, pv.texture && pv.texture.type !== 'none' ? '질감 ✓' : '질감');
    list.append(h('div.layer.paper', null, paperEye, h('div.lname', null, h('span', null, '용지')), tex, pc));
    syncProps();
  };

  const syncProps = () => {
    opacity.sync(); blend.sync();
    flags.forEach(f => f.sync());
  };

  // 끌어서 순서 바꾸기 / 폴더에 넣기 (폴더 가운데에 놓으면 안으로)
  function startDrag(e, layer, row) {
    e.preventDefault();
    e.stopPropagation();
    const grip = e.currentTarget;
    grip.setPointerCapture(e.pointerId);
    row.classList.add('dragging');
    const rows = [...list.querySelectorAll('.layer:not(.paper)')];
    let target = null;
    const mv = (ev) => {
      rows.forEach(r => r.classList.remove('drop-above', 'drop-below', 'drop-inside'));
      target = null;
      for (const r of rows) {
        const b = r.getBoundingClientRect();
        if (ev.clientY >= b.top && ev.clientY < b.bottom) {
          const t = (ev.clientY - b.top) / b.height;
          const isFolder = r.classList.contains('folder');
          const pos = isFolder && t > 0.28 && t < 0.72 ? 'inside' : t < 0.5 ? 'above' : 'below';
          r.classList.add('drop-' + pos);
          target = { id: r.dataset.id, pos };
        }
      }
      // 목록 끝에서 자동 스크롤
      const lb = list.getBoundingClientRect();
      if (ev.clientY < lb.top + 24) list.scrollTop -= 12;
      if (ev.clientY > lb.bottom - 24) list.scrollTop += 12;
    };
    const up = () => {
      grip.removeEventListener('pointermove', mv);
      grip.removeEventListener('pointerup', up);
      row.classList.remove('dragging');
      rows.forEach(r => r.classList.remove('drop-above', 'drop-below', 'drop-inside'));
      if (!target || target.id === layer.id) return;
      ops.moveRelative(layer, app.doc.byId(target.id), target.pos);
    };
    grip.addEventListener('pointermove', mv);
    grip.addEventListener('pointerup', up);
  }

  // 썸네일 갱신 (가끔씩만)
  let pending = new Set(), timer = 0;
  app.bus.on('layer:pixels', (l) => {
    pending.add(l.id);
    if (timer) return;
    timer = setTimeout(() => {
      timer = 0;
      for (const id of pending) {
        const t = thumbs.get(id), l2 = app.doc?.byId(id);
        if (t && l2 && t.version !== l2.version) { drawThumb(l2, t.canvas); t.version = l2.version; }
      }
      pending.clear();
    }, 400);
  });
  app.bus.on('layers:changed', render);
  app.bus.on('doc:changed', render);
  app.bus.on('doc:resized', render);
  render();
}

// =====================================================================
// 에셋 패널
// =====================================================================
export function assetsPanel(el) {
  const grid = h('div.asset-grid');
  const filter = h('input.asset-filter', { type: 'search', placeholder: '검색…' });
  el.append(
    h('div.panel-scroll', null,
      row(
        button('레이어→에셋', () => app.run('asset.saveLayer')),
        button('보이는 그림→에셋', () => app.run('asset.saveMerged')),
      ),
      row(
        button(icon('folder', 16) + ' 폴더', () => app.run('asset.folder')),
        button('새로고침', () => load()),
        filter,
      ),
      grid,
      h('div.help', null, '탭: 캔버스에 배치 · 길게 누르기: 브러시 팁으로 쓰기/삭제. data/assets 폴더에 PNG를 넣어도 됩니다.'),
    ));
  let items = [];
  const renderGrid = () => {
    grid.innerHTML = '';
    const q = filter.value.trim().toLowerCase();
    const list = items.filter(i => /\.(png|jpe?g|webp)$/i.test(i.name) && (!q || i.name.toLowerCase().includes(q)));
    if (!list.length) grid.append(h('div.help', null, '에셋이 없습니다.'));
    for (const it of list) {
      const card = h('button.asset', { type: 'button', title: it.name }, setImgSrc(h('img', { loading: 'lazy' }), 'assets', it.name, it.mtime), h('span', null, it.name.replace(/\.[^.]+$/, '')));
      let t = 0, long = false;
      card.addEventListener('pointerdown', () => { long = false; t = setTimeout(() => { long = true; assetMenu(it); }, 550); });
      card.addEventListener('pointerup', () => clearTimeout(t));
      card.addEventListener('pointerleave', () => clearTimeout(t));
      card.addEventListener('contextmenu', (e) => { e.preventDefault(); clearTimeout(t); assetMenu(it); });
      card.addEventListener('click', async () => {
        if (long) return;
        const img = await loadImage(src);
        ops.placeImage(img, it.name.replace(/\.[^.]+$/, '').split('/').pop(), { fit: true });
      });
      grid.append(card);
    }
  };
  const load = async () => {
    try { items = await server.list('assets'); } catch { items = []; }
    renderGrid();
  };
  const assetMenu = (it) => {
    app.ui.openModal({
      title: it.name,
      body: h('div.menu-list', null,
        h('button.btn', { type: 'button', onclick: async (e) => {
          e.target.closest('.modal-backdrop').remove();
          const name = it.name.replace(/\.[^.]+$/, '').split('/').pop();
          const list = presets('brush');
          const p = { ...structuredClone(activePreset('brush')), id: 'img-' + Date.now(), name: '스탬프: ' + name, tip: 'image', tipImage: it.name, spacing: 0.6, pressureSize: true, minSize: 0.5, stabilizer: 0, tintTip: false, rotJitter: 0, scatter: 0, color: null };
          list.push(p);
          setActivePreset('brush', p.id);
          app.setTool('brush');
          app.bus.emit('brush:presets');
          app.toast('브러시 프리셋 추가: ' + p.name);
        } }, '이 이미지로 스탬프 브러시 만들기'),
        h('button.btn.danger', { type: 'button', onclick: async (e) => {
          e.target.closest('.modal-backdrop').remove();
          await server.del('assets', it.name);
          app.toast('휴지통(data/trash)으로 옮겼습니다');
          load();
        } }, '삭제 (data/trash 로 이동)'),
      ),
    });
  };
  filter.addEventListener('input', renderGrid);
  app.bus.on('assets:changed', load);
  load();
}

// =====================================================================
// 메뉴바
// =====================================================================
export function initMenubar() {
  const bar = document.getElementById('menubar');
  const layer = document.getElementById('menu-layer');
  let open = null;
  const close = () => { layer.innerHTML = ''; layer.classList.remove('open'); open = null; bar.querySelectorAll('button').forEach(b => b.classList.remove('on')); };
  const keyLabel = (id) => {
    const k = app.settings.keys[id] || app.defaultKeys?.[id];
    return k ? k.replace('ctrl', 'Ctrl').replace('shift', 'Shift').replace('alt', 'Alt').replace(/\+(\w)$/, (m, c) => '+' + c.toUpperCase()) : '';
  };
  const show = (id, btn, all = false) => {
    close();
    const m = app.menus.get(id);
    open = id;
    btn.classList.add('on');
    const r = btn.getBoundingClientRect();
    const box = h('div.menu', { style: { left: r.left + 'px', top: r.bottom + 'px' } });
    const { items, hidden } = menuItems(id, m.items, all);
    for (const it of items) {
      if (it.sep) { box.append(h('div.menu-sep')); continue; }
      const cmd = it.command ? app.commands.get(it.command) : null;
      const label = it.label || cmd?.label || it.command;
      const item = h('button.menu-item', { type: 'button' }, h('span', null, label), h('kbd', null, it.command ? keyLabel(it.command) : ''));
      item.onclick = () => { close(); if (it.run) it.run(); else app.run(it.command); };
      box.append(item);
    }
    if (hidden) {
      box.append(h('div.menu-sep'), h('button.menu-item.menu-more', { type: 'button', onclick: (e) => { e.stopPropagation(); show(id, btn, true); } }, h('span', null, `모든 항목 보기 (${hidden}개 더)`), h('kbd', null, '')));
    }
    layer.append(box);
    layer.classList.add('open');
    const mr = box.getBoundingClientRect();
    if (mr.right > innerWidth) box.style.left = Math.max(0, innerWidth - mr.width - 4) + 'px';
    if (mr.bottom > innerHeight) box.style.maxHeight = (innerHeight - r.bottom - 8) + 'px';
  };
  layer.addEventListener('pointerdown', (e) => { if (e.target === layer) close(); });
  const render = () => {
    bar.innerHTML = '';
    for (const id of app.menuOrder) {
      if (!menuShown(id)) continue;
      const m = app.menus.get(id);
      const b = h('button', { type: 'button' }, m.label);
      b.onclick = () => (open === id ? close() : show(id, b));
      b.onpointerenter = (e) => { if (open && open !== id && e.pointerType === 'mouse') show(id, b); };
      bar.append(b);
    }
  };
  app.bus.on('menus:changed', render);
  render();
}

// =====================================================================
// 상단 버튼 / 상태바
// =====================================================================
export function initTopActions() {
  const el = document.getElementById('top-actions');
  const b = (ic, title, cmd, cls = '') => {
    const x = h(`button.icon-btn${cls ? '.' + cls : ''}`, { type: 'button', title, html: icon(ic, 20) });
    x.onclick = () => app.run(cmd);
    return x;
  };
  const undo = b('undo', '실행 취소 (Ctrl+Z / 두 손가락 탭)', 'edit.undo');
  const redo = b('redo', '다시 실행 (Ctrl+Y / 세 손가락 탭)', 'edit.redo');
  const save = b('save', '저장 (Ctrl+S)', 'file.save');
  el.append(b('search', '기능 검색 (Ctrl+K)', 'palette.open'), undo, redo, h('span.top-sep'), save, b('export', '내보내기', 'file.export'), h('span.top-sep'),
    b('finger', '손가락으로 그리기 켜기/끄기', 'settings.fingerDraw', 'finger'),
    b('side', '패널 보이기/숨기기', 'view.toggleSide'), b('fullscreen', '전체 화면', 'view.fullscreen'), b('gear', '설정', 'settings.open'));
  const sync = () => {
    undo.disabled = !app.history?.canUndo;
    redo.disabled = !app.history?.canRedo;
    save.classList.toggle('dirty', !!app.doc?.modified);
    el.querySelector('.finger')?.classList.toggle('on', app.settings.fingerDraw);
  };
  app.bus.on('history:changed', sync);
  app.bus.on('doc:modified', sync);
  app.bus.on('doc:changed', sync);
  app.bus.on('settings:changed', sync);
  sync();
}

export function initStatusbar() {
  const el = document.getElementById('statusbar');
  const zoom = h('button.st', { type: 'button', title: '탭: 화면 맞춤' });
  const angle = h('button.st', { type: 'button', title: '탭: 회전 초기화' });
  const size = h('span.st');
  const layer = h('span.st');
  const sym = h('button.st', { type: 'button', title: '대칭 그리기' });
  const saved = h('span.st.saved');
  const srv = h('span.st');
  zoom.onclick = () => app.run('view.fit');
  angle.onclick = () => app.run('view.resetRotation');
  sym.onclick = () => app.run('view.symmetry');
  el.append(zoom, angle, sym, h('span.spacer'), layer, size, saved, srv);
  const syncView = throttleRAF(() => {
    const v = app.view;
    zoom.textContent = `${Math.round(v.zoom * 1000) / 10}%`;
    const a = Math.round(((v.angle % 360) + 360) % 360);
    angle.textContent = `${a > 180 ? a - 360 : a}°${v.flipped ? ' ⇋' : ''}`;
  });
  const syncDoc = () => {
    const d = app.doc;
    if (!d) return;
    size.textContent = `${d.width}×${d.height}`;
    layer.textContent = d.active ? `▣ ${d.active.name}` : '';
    saved.textContent = d.modified ? '● 수정됨' : (d.file ? '저장됨' : '');
    sym.textContent = '대칭: ' + ({ none: '끔', v: '좌우', h: '상하', vh: '상하좌우' }[app.settings.symmetry] || `방사 ${app.settings.symN || 6}`);
    srv.textContent = server.mode === 'http' ? '' : server.mode === 'folder' ? '서버 없이 폴더에 저장' : '⚠ 이 기기 안에 저장 중';
  };
  app.bus.on('view:changed', syncView);
  for (const e of ['doc:changed', 'doc:modified', 'layers:changed', 'layers:active', 'doc:resized', 'settings:changed', 'doc:saved']) app.bus.on(e, syncDoc);
  app.bus.on('autosaved', (d) => { saved.textContent = `자동 저장 ${d.toLocaleTimeString().slice(0, -3)}`; });
  syncView();
  syncDoc();
}
