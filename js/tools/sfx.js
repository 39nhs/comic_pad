// 효과음(의성어) 글자: 글자마다 크기·각도·위치, 휘기(아치·물결·원근·흔들림·통통), 2중 외곽선, 그라데이션, 기울임.
// 글자 레이어(kind 'text') 에 meta.sfx = true 로 저장 → 변형 도구·이동·실행취소가 글자 레이어와 똑같이 되고, 언제든 다시 편집.
import { app } from '../app.js';
import { addLayer, setLayerProps } from '../core/ops.js';
import { h } from '../core/util.js';
import { slider, toggle, segmented, colorInput, select, button, row, section } from '../ui/controls.js';
import { openModal } from '../ui/dialogs.js';
import { addIcon } from '../ui/icons.js';
import { helpText } from './basic.js';
import { SYSTEM_FONTS, setSfxHooks, renderText } from './comic.js';

addIcon('sfx', '<path d="M4 18l3-12 3 9 3-9 3 12"/><path d="M18 5l2-2M19 9h3M18 13l2 2"/>');

export const SFX_DEFAULTS = {
  sfx: true, text: '쾅!', font: 'Malgun Gothic', size: 140, bold: true, italic: false, vertical: false,
  color: '#111111', gradient: false, color2: '#555555',
  in: { w: 8, color: '#ffffff' },        // 안쪽 외곽선 (글자 바로 둘레)
  out: { w: 4, color: '#111111' },       // 바깥 외곽선 (안쪽 외곽선 둘레)
  warp: 'none', warpAmt: 0.5, slant: 0, tracking: 0, chaos: 0, seed: 1,
  chars: [],                              // 글자별 { dx, dy, rot, scale } (글자 크기에 대한 비율, 각도는 도)
  align: 'center', lineHeight: 1, strokeWidth: 0, strokeColor: '#ffffff',
};

export const SFX_PRESETS = {
  '쾅!': { text: '쾅!', size: 160, color: '#111111', in: { w: 10, color: '#ffffff' }, out: { w: 5, color: '#111111' }, warp: 'persp', warpAmt: 0.7, slant: -8, chaos: 0.25 },
  '두근': { text: '두근', size: 110, color: '#e0245e', gradient: true, color2: '#ff9ab3', in: { w: 7, color: '#ffffff' }, out: { w: 3, color: '#7a0a2c' }, warp: 'bounce', warpAmt: 0.35, chaos: 0.15 },
  '스윽': { text: '스으윽', size: 90, bold: false, color: '#444444', in: { w: 5, color: '#ffffff' }, out: { w: 0, color: '#000000' }, warp: 'wave', warpAmt: 0.25, slant: 22, tracking: 30 },
  'ドーン': { text: 'ドーン', size: 150, vertical: true, color: '#ffffff', in: { w: 10, color: '#111111' }, out: { w: 6, color: '#ffffff' }, warp: 'arc', warpAmt: 0.4, chaos: 0.1 },
  '파바박': { text: '파바박', size: 120, color: '#111111', in: { w: 7, color: '#ffffff' }, out: { w: 3, color: '#111111' }, warp: 'shake', warpAmt: 0.9, chaos: 0.5 },
  '휘익': { text: '휘이익', size: 100, color: '#2a6bd1', gradient: true, color2: '#9fd0ff', in: { w: 6, color: '#ffffff' }, out: { w: 2, color: '#0b2a5c' }, warp: 'wave', warpAmt: 0.6, slant: 15, tracking: 10 },
};

const WARPS = [['none', '없음'], ['arc', '아치'], ['wave', '물결'], ['persp', '원근 (점점 크게)'], ['shake', '흔들림'], ['bounce', '통통']];

function rng(seed) { let s = (seed * 9301 + 49297) % 233280 || 1; return () => ((s = (s * 9301 + 49297) % 233280) / 233280); }

/** 글자 배치: 각 글자의 가운데·크기·각도 (회전·변형 전, 문서 좌표) */
export function sfxLayout(m, measure) {
  const glyphs = [...String(m.text || '').replace(/\n/g, '')];
  const n = glyphs.length;
  if (!n) return [];
  const R = rng(m.seed || 1);
  const out = [];
  // 1) 글자 크기 (원근이면 점점 크게)
  const sizes = glyphs.map((g, i) => {
    const u = n > 1 ? i / (n - 1) : 0.5;
    let s = m.size;
    if (m.warp === 'persp') s *= 1 - m.warpAmt * 0.6 + m.warpAmt * 1.2 * u;
    const c = m.chars?.[i];
    if (c?.scale) s *= c.scale;
    return s;
  });
  // 2) 진행 방향으로 차례로 놓기
  const track = (m.tracking || 0) / 100;
  const adv = glyphs.map((g, i) => (m.vertical ? sizes[i] * 1.0 : measure(g, sizes[i])) + sizes[i] * track);
  const total = adv.reduce((a, b) => a + b, 0) - sizes[n - 1] * track;
  let pos = -total / 2;
  for (let i = 0; i < n; i++) {
    const a = adv[i] - (i === n - 1 ? sizes[i] * track : 0);
    const center = pos + (m.vertical ? sizes[i] * 0.5 : measure(glyphs[i], sizes[i]) / 2);
    pos += adv[i];
    const u = n > 1 ? (i / (n - 1)) * 2 - 1 : 0;      // -1 ~ 1
    let off = 0, rot = 0;
    const A = m.warpAmt * m.size;
    if (m.warp === 'arc') { off = -A * 0.6 * (1 - u * u); rot = Math.atan(A * 1.2 * u / Math.max(1, total / 2)) * 180 / Math.PI; }
    else if (m.warp === 'wave') { off = Math.sin(u * Math.PI * 1.5) * A * 0.35; rot = Math.atan(Math.cos(u * Math.PI * 1.5) * A * 0.35 * Math.PI * 1.5 / Math.max(1, total / 2)) * 180 / Math.PI; }
    else if (m.warp === 'bounce') { off = (i % 2 ? 1 : -1) * A * 0.22; rot = (i % 2 ? 6 : -6) * m.warpAmt; }
    else if (m.warp === 'shake') { off = (R() - 0.5) * A * 0.5; rot = (R() - 0.5) * 40 * m.warpAmt; }
    if (m.chaos) { off += (R() - 0.5) * m.size * 0.3 * m.chaos; rot += (R() - 0.5) * 50 * m.chaos; }
    const c = m.chars?.[i] || {};
    let x, y;
    if (m.vertical) { x = m.x - off + (c.dx || 0) * m.size; y = m.y + center + (c.dy || 0) * m.size; }
    else { x = m.x + center + (c.dx || 0) * m.size; y = m.y + off + (c.dy || 0) * m.size; }
    if (m.warp === 'persp' && !m.vertical) y += (m.size - sizes[i]) * 0.35;      // 아랫선을 맞춰 원근 느낌
    out.push({ ch: glyphs[i], x, y, size: sizes[i], rot: rot + (c.rot || 0), w: a });
  }
  return out;
}

const fontOf = (m, s) => `${m.italic ? 'italic ' : ''}${m.bold ? '900 ' : ''}${s}px "${m.font}", "Malgun Gothic", sans-serif`;

/** 효과음 레이어 다시 그리기 (renderText 가 meta.sfx 이면 이것을 부름) */
export function renderSfx(layer) {
  const m = { ...SFX_DEFAULTS, ...layer.meta };
  const ctx = layer.ctx;
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, layer.canvas.width, layer.canvas.height);
  const mat = m.mat ? new DOMMatrix(m.mat) : null;
  const base = mat || new DOMMatrix();
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.lineJoin = 'round';
  ctx.miterLimit = 2;
  const measure = (g, s) => { ctx.font = fontOf(m, s); return ctx.measureText(g).width; };
  const gl = sfxLayout(m, measure);
  const inW = Math.max(0, m.in?.w || 0), outW = Math.max(0, m.out?.w || 0);
  const slant = Math.tan(((m.slant || 0) * Math.PI) / 180);
  const each = (fn) => {
    for (const g of gl) {
      ctx.setTransform(base.translate(g.x, g.y).rotate(g.rot).multiply(new DOMMatrix([1, 0, -slant, 1, 0, 0])));
      ctx.font = fontOf(m, g.size);
      fn(g);
    }
  };
  // 바깥 외곽선 → 안쪽 외곽선 → 글자 (외곽선이 다른 글자를 덮지 않게 층별로)
  if (outW > 0) { ctx.strokeStyle = m.out.color; each(g => { ctx.lineWidth = (inW + outW) * 2 * g.size / m.size; ctx.strokeText(g.ch, 0, 0); }); }
  if (inW > 0) { ctx.strokeStyle = m.in.color; each(g => { ctx.lineWidth = inW * 2 * g.size / m.size; ctx.strokeText(g.ch, 0, 0); }); }
  each(g => {
    if (m.gradient) {
      const gr = ctx.createLinearGradient(0, -g.size / 2, 0, g.size / 2);
      gr.addColorStop(0, m.color); gr.addColorStop(1, m.color2 || m.color);
      ctx.fillStyle = gr;
    } else ctx.fillStyle = m.color;
    ctx.fillText(g.ch, 0, 0);
  });
  ctx.restore();
  // 테두리 상자 (글자 레이어와 같은 형식: lbox = 변형 전, quad/box = 변형 후)
  const pad = inW + outW + 4;
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const g of gl) {
    const r = Math.hypot(g.w, g.size) / 2 + pad + Math.abs(slant) * g.size / 2;
    x0 = Math.min(x0, g.x - r); y0 = Math.min(y0, g.y - r); x1 = Math.max(x1, g.x + r); y1 = Math.max(y1, g.y + r);
  }
  if (!gl.length) { x0 = m.x - 10; y0 = m.y - 10; x1 = m.x + 10; y1 = m.y + 10; }
  const lb = { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
  layer.meta.lbox = lb;
  const quad = [[lb.x, lb.y], [lb.x + lb.w, lb.y], [lb.x + lb.w, lb.y + lb.h], [lb.x, lb.y + lb.h]]
    .map(([x, y]) => (mat ? mat.transformPoint(new DOMPoint(x, y)) : { x, y })).map(p => ({ x: p.x, y: p.y }));
  layer.meta.quad = quad;
  const xs = quad.map(p => p.x), ys = quad.map(p => p.y);
  layer.meta.box = { x: Math.min(...xs), y: Math.min(...ys), w: Math.max(...xs) - Math.min(...xs), h: Math.max(...ys) - Math.min(...ys) };
  layer.touch();
}

/** 효과음 편집 창 */
export function editSfxLayer(layer, isNew) {
  const m = layer.meta;
  for (const [k, v] of Object.entries(SFX_DEFAULTS)) if (m[k] === undefined) m[k] = structuredClone(v);
  const orig = structuredClone(m);
  const upd = () => renderText(layer);
  const ta = h('input.text-input', { value: m.text, placeholder: '효과음 (예: 쾅!, 두근, 스윽)' });
  ta.addEventListener('input', () => { m.text = ta.value; m.chars = (m.chars || []).slice(0, [...ta.value].length); upd(); charPick(); });
  const fonts = [...SYSTEM_FONTS, ...(app.fonts || []).map(f => [f, f + ' (내 폰트)'])];
  const presets = h('div.s3d-prims', null, ...Object.keys(SFX_PRESETS).map(k => h('button.btn', { type: 'button', onclick: () => {
    const keep = { x: m.x, y: m.y, mat: m.mat, font: m.font };
    Object.assign(m, structuredClone(SFX_DEFAULTS), structuredClone(SFX_PRESETS[k]), keep, { text: m.text && !isNew ? m.text : SFX_PRESETS[k].text, chars: [] });
    ta.value = m.text; upd(); rebuild();
  } }, k)));
  // 글자 하나씩
  let ci = 0;
  const charBox = h('div');
  const charPick = () => {
    const gl = [...String(m.text || '')];
    charBox.innerHTML = '';
    if (!gl.length) return;
    ci = Math.min(ci, gl.length - 1);
    const c = () => (m.chars[ci] ||= { dx: 0, dy: 0, rot: 0, scale: 1 });
    charBox.append(
      h('div.s3d-prims', null, ...gl.map((g, i) => h(`button.btn${i === ci ? '.primary' : ''}`, { type: 'button', onclick: () => { ci = i; charPick(); } }, g === ' ' ? '␣' : g))),
      slider('크기', { min: 0.3, max: 2.5, step: 0.05, get: () => c().scale ?? 1, set: v => { c().scale = v; upd(); }, format: v => Math.round(v * 100) + '%' }),
      slider('각도', { min: -90, max: 90, step: 1, get: () => c().rot || 0, set: v => { c().rot = v; upd(); }, format: v => v + '°' }),
      slider('가로 이동', { min: -1.5, max: 1.5, step: 0.02, get: () => c().dx || 0, set: v => { c().dx = v; upd(); } }),
      slider('세로 이동', { min: -1.5, max: 1.5, step: 0.02, get: () => c().dy || 0, set: v => { c().dy = v; upd(); } }),
      button('이 글자 원래대로', () => { m.chars[ci] = { dx: 0, dy: 0, rot: 0, scale: 1 }; upd(); charPick(); }),
    );
  };
  const ctl = h('div.text-ctl');
  const rebuild = () => {
    ctl.innerHTML = '';
    ctl.append(
      section('모양 모음', presets),
      select('글꼴', fonts, () => m.font, v => { m.font = v; upd(); }),
      slider('크기', { min: 12, max: 800, step: 1, log: true, get: () => m.size, set: v => { m.size = v; upd(); } }),
      row(toggle('굵게', () => m.bold, v => { m.bold = v; upd(); }), toggle('기울임 글꼴', () => m.italic, v => { m.italic = v; upd(); }), toggle('세로', () => m.vertical, v => { m.vertical = v; upd(); })),
      slider('기울기', { min: -40, max: 40, step: 1, get: () => m.slant || 0, set: v => { m.slant = v; upd(); }, format: v => v + '°' }),
      slider('자간', { min: -40, max: 120, step: 1, get: () => m.tracking || 0, set: v => { m.tracking = v; upd(); }, format: v => v + '%' }),
      section('휘기',
        segmented(WARPS, () => m.warp, v => { m.warp = v; upd(); }),
        slider('세기', { min: 0, max: 1.5, step: 0.05, get: () => m.warpAmt, set: v => { m.warpAmt = v; upd(); } }),
        slider('제멋대로 (글자마다 무작위)', { min: 0, max: 1, step: 0.05, get: () => m.chaos || 0, set: v => { m.chaos = v; upd(); } }),
        button('무작위 다시', () => { m.seed = Math.floor(Math.random() * 99999) + 1; upd(); }),
      ),
      section('색',
        row(colorInput('글자색', () => m.color, v => { m.color = v; upd(); }), toggle('그라데이션', () => m.gradient, v => { m.gradient = v; upd(); rebuild(); })),
        m.gradient ? colorInput('아래쪽 색', () => m.color2, v => { m.color2 = v; upd(); }) : null,
        row(colorInput('안쪽 외곽선', () => m.in.color, v => { m.in.color = v; upd(); }), slider('굵기', { min: 0, max: 40, step: 0.5, get: () => m.in.w, set: v => { m.in.w = v; upd(); } })),
        row(colorInput('바깥 외곽선', () => m.out.color, v => { m.out.color = v; upd(); }), slider('굵기', { min: 0, max: 40, step: 0.5, get: () => m.out.w, set: v => { m.out.w = v; upd(); } })),
      ),
      section('글자 하나씩', charBox),
      helpText('다 만든 뒤에도 효과음 도구로 탭하면 다시 고칠 수 있고, 변형 도구(Ctrl+T)로 크기·회전·원근을 줘도 글자 정보는 그대로입니다. 그림으로 굳히려면 레이어를 "그림으로 바꾸기".'),
    );
    charPick();
  };
  rebuild();
  upd();
  let done = false;
  const finish = (ok) => {
    if (done) return;
    done = true;
    if (!ok || !String(m.text).trim()) {
      if (isNew) { app.history.undo(); app.history.redoStack.pop(); app.bus.emit('history:changed'); }
      else { layer.meta = orig; renderText(layer); }
      return;
    }
    layer.name = '효과음: ' + m.text.slice(0, 10);
    if (!isNew) {
      const after = structuredClone(layer.meta);
      app.history.push({
        label: '효과음 편집',
        undo: () => { layer.meta = structuredClone(orig); renderText(layer); app.bus.emit('layers:changed'); },
        redo: () => { layer.meta = structuredClone(after); renderText(layer); app.bus.emit('layers:changed'); },
      });
    } else app.markModified();
    app.bus.emit('layers:changed');
  };
  openModal({
    title: isNew ? '효과음 넣기' : '효과음 고치기', cls: 'text-modal',
    body: h('div.text-dialog', null, ta, ctl),
    actions: [
      !isNew ? { label: '그림으로 바꾸기', run: () => { finish(true); sfxToPixels(layer); } } : null,
      { label: '취소', run: () => finish(false) },
      { label: '확인', primary: true, run: () => finish(true) },
    ].filter(Boolean),
    onClose: (r) => { if (r === null) finish(false); },
  });
  setTimeout(() => ta.focus(), 50);
}

/** 효과음을 보통 그림 레이어로 굳히기 (붓으로 다듬을 때) */
export function sfxToPixels(layer) {
  setLayerProps(layer, { kind: 'raster', meta: null }, '효과음을 그림으로');
  app.toast('효과음을 그림 레이어로 바꿨습니다 (이제 글자 편집은 안 됩니다)');
}

export function createSfxAt(x, y, preset = null) {
  const p = preset ? SFX_PRESETS[preset] : (sfxTool.opt.preset && SFX_PRESETS[sfxTool.opt.preset]);
  const meta = { ...structuredClone(SFX_DEFAULTS), ...(p ? structuredClone(p) : {}), x, y, font: sfxTool.opt.font || 'Malgun Gothic' };
  if (sfxTool.opt.size) meta.size = Math.round((p?.size || meta.size) * sfxTool.opt.size / 100);
  const layer = addLayer({ name: '효과음', kind: 'text', meta }, app.doc.layers.length);
  layer.meta = meta;
  renderText(layer);
  return layer;
}

const sfxAt = (x, y) => {
  const inBox = (b) => b && x >= b.x && y >= b.y && x <= b.x + b.w && y <= b.y + b.h;
  const act = app.doc.active;
  if (act?.kind === 'text' && act.meta?.sfx && act.visible && inBox(act.meta.box)) return act;
  return app.doc.findLayer(l => l.kind === 'text' && l.meta?.sfx && l.visible && inBox(l.meta.box));
};

export const sfxTool = {
  id: 'sfx', name: '효과음 글자', icon: 'sfx', cursor: 'text',
  opt: { preset: '쾅!', size: 100, font: 'Malgun Gothic' },
  drag: null,
  down(e) {
    const l = sfxAt(e.x, e.y);
    this.drag = l ? { layer: l, x0: e.x, y0: e.y, mx: l.meta.x, my: l.meta.y, mat0: l.meta.mat ? [...l.meta.mat] : null, sx0: e.sx, sy0: e.sy, moved: false, orig: structuredClone(l.meta) } : { create: true, x: e.x, y: e.y };
    if (l) app.doc.setActive(l);
    this.busy = true;
  },
  move(e) {
    const d = this.drag;
    if (!d?.layer) return;
    if (Math.hypot(e.sx - d.sx0, e.sy - d.sy0) > 5) d.moved = true;
    if (!d.moved) return;
    if (d.mat0) { d.layer.meta.mat[4] = d.mat0[4] + e.x - d.x0; d.layer.meta.mat[5] = d.mat0[5] + e.y - d.y0; }
    else { d.layer.meta.x = d.mx + e.x - d.x0; d.layer.meta.y = d.my + e.y - d.y0; }
    renderText(d.layer);
  },
  up() {
    const d = this.drag;
    this.drag = null; this.busy = false;
    if (!d) return;
    if (d.create) { editSfxLayer(createSfxAt(d.x, d.y), true); return; }
    if (!d.moved) { editSfxLayer(d.layer, false); return; }
    const l = d.layer, before = d.orig, after = structuredClone(l.meta);
    app.history.push({ label: '효과음 이동', undo: () => { l.meta = structuredClone(before); renderText(l); }, redo: () => { l.meta = structuredClone(after); renderText(l); } });
  },
  cancel() { this.drag = null; this.busy = false; },
  overlay(ctx, view) {
    for (const l of app.doc.layers) {
      if (l.kind !== 'text' || !l.meta?.sfx || !l.visible || !l.meta.quad) continue;
      const pts = l.meta.quad.map(p => view.toScreen(p.x, p.y));
      ctx.strokeStyle = l === app.doc.active ? '#e8a33d' : 'rgba(232,163,61,0.45)';
      ctx.setLineDash([4, 3]);
      ctx.beginPath(); pts.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y))); ctx.closePath(); ctx.stroke();
      ctx.setLineDash([]);
    }
  },
  options(el) {
    const o = this.opt;
    const fonts = [...SYSTEM_FONTS, ...(app.fonts || []).map(f => [f, f + ' (내 폰트)'])];
    el.append(section('새 효과음',
      h('div.s3d-prims', null, ...Object.keys(SFX_PRESETS).map(k => h(`button.btn${o.preset === k ? '.primary' : ''}`, { type: 'button', onclick: (ev) => { o.preset = k; ev.target.parentNode.querySelectorAll('button').forEach(b => b.classList.toggle('primary', b === ev.target)); } }, k))),
      select('글꼴', fonts, () => o.font, v => { o.font = v; }),
      slider('크기 배율', { min: 20, max: 400, step: 5, get: () => o.size, set: v => { o.size = v; }, format: v => v + '%' }),
      helpText('빈 곳 탭: 새 효과음 · 효과음 탭: 고치기 · 끌기: 옮기기. 아치·물결·원근·흔들림으로 휘고, 글자마다 크기·각도를 따로 줄 수 있습니다.'),
    ));
  },
};

export function initSfx() {
  setSfxHooks({ render: renderSfx, edit: editSfxLayer });
  app.registerTool(sfxTool);
  app.registerCommand('sfx.tool', { label: '효과음 글자 도구', run: () => app.setTool('sfx') });
  app.registerCommand('sfx.toPixels', { label: '효과음을 그림으로 바꾸기', run: () => { const l = app.doc.active; if (l?.kind === 'text' && l.meta?.sfx) sfxToPixels(l); else app.toast('효과음 레이어를 고르세요'); } });
  app.addMenuItem('comic', { command: 'sfx.tool' });
}
