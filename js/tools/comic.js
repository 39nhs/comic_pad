// 만화 도구: 컷(칸) 나누기, 말풍선, 텍스트
import { app } from '../app.js';
import { addLayer, setLayerProps, checkEditable, deleteLayer } from '../core/ops.js';
import { rectFromPoints, createCanvas } from '../core/util.js';
import { slider, toggle, segmented, colorInput, select, button, row, section } from '../ui/controls.js';
import { helpText } from './basic.js';
import { selectionHistory } from './select.js';
import { openModal } from '../ui/dialogs.js';
import { h } from '../core/util.js';

// =====================================================================
// 컷 (다각형: 사각형, 사선으로 자른 컷, 직접 찍은 다각형)
// =====================================================================
export function panelLayer(create = true) {
  const doc = app.doc;
  let l = doc.findLayer(x => x.kind === 'panel');
  if (!l && create) {
    l = addLayer({ name: '컷', kind: 'panel', parent: null, meta: { panels: [], line: Math.max(3, Math.round(doc.width / 400)), color: '#000000', maskOutside: true } }, doc.layers.length);
  }
  return l;
}

const rectPts = (r) => [{ x: r.x, y: r.y }, { x: r.x + r.w, y: r.y }, { x: r.x + r.w, y: r.y + r.h }, { x: r.x, y: r.y + r.h }];
/** 예전 형식 {x,y,w,h} 도 다각형으로 */
export const panelPts = (p) => p.pts || rectPts(p);
const roundPts = (q) => q.map(v => ({ x: Math.round(v.x * 10) / 10, y: Math.round(v.y * 10) / 10 }));

function tracePath(ctx, q) {
  ctx.beginPath();
  q.forEach((v, i) => (i ? ctx.lineTo(v.x, v.y) : ctx.moveTo(v.x, v.y)));
  ctx.closePath();
}

export function renderPanels(layer) {
  const m = layer.meta, ctx = layer.ctx;
  const W = layer.canvas.width, H = layer.canvas.height;
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, W, H);
  if (m.maskOutside && m.panels.length) {
    ctx.fillStyle = app.doc.paper.color;
    ctx.fillRect(0, 0, W, H);
    ctx.globalCompositeOperation = 'destination-out';
    for (const p of m.panels) { tracePath(ctx, panelPts(p)); ctx.fill(); }
    ctx.globalCompositeOperation = 'source-over';
  }
  // 테두리는 컷 안쪽으로만 (2배 굵기로 그리고 컷 모양으로 자름)
  ctx.strokeStyle = m.color;
  ctx.lineWidth = m.line * 2;
  ctx.lineJoin = 'miter';
  ctx.miterLimit = 20;
  for (const p of m.panels) {
    ctx.save();
    tracePath(ctx, panelPts(p));
    ctx.clip();
    ctx.stroke();
    ctx.restore();
  }
  ctx.restore();
  layer.touch();
}

/** 컷 정보 변경 + 다시 그리기 (실행취소 가능) */
export function setPanelsWithMeta(layer, meta) {
  // 컷 레이어 픽셀은 meta 로부터 다시 그릴 수 있으므로 meta 만 기록 (메모리 절약)
  const prev = structuredClone(layer.meta);
  const apply = (m) => { layer.meta = structuredClone(m); renderPanels(layer); app.bus.emit('panels:changed'); };
  apply(meta);
  app.history.push({ label: '컷 편집', undo: () => apply(prev), redo: () => apply(meta) });
}

function inPoly(q, x, y) {
  let inside = false;
  for (let i = 0, j = q.length - 1; i < q.length; j = i++) {
    if ((q[i].y > y) !== (q[j].y > y) && x < (q[j].x - q[i].x) * (y - q[i].y) / (q[j].y - q[i].y) + q[i].x) inside = !inside;
  }
  return inside;
}

function polyArea(q) {
  let a = 0;
  for (let i = 0, j = q.length - 1; i < q.length; j = i++) a += (q[j].x + q[i].x) * (q[j].y - q[i].y);
  return Math.abs(a / 2);
}

function panelAt(layer, x, y) {
  const ps = layer?.meta?.panels || [];
  for (let i = ps.length - 1; i >= 0; i--) if (inPoly(panelPts(ps[i]), x, y)) return i;
  return -1;
}

/** 다각형을 직선 a→b 로 자르고 양쪽을 간격 g 만큼 띄운다 */
function splitPoly(q, a, b, g) {
  const dx = b.x - a.x, dy = b.y - a.y, L = Math.hypot(dx, dy) || 1;
  const nx = -dy / L, ny = dx / L;
  const side = (p) => (p.x - a.x) * nx + (p.y - a.y) * ny;
  const clip = (k) => {
    const out = [];
    const f = (p) => k * side(p) - g / 2;
    for (let i = 0; i < q.length; i++) {
      const P = q[i], Q = q[(i + 1) % q.length], fp = f(P), fq = f(Q);
      if (fp >= 0) out.push(P);
      if ((fp >= 0) !== (fq >= 0)) { const t = fp / (fp - fq); out.push({ x: P.x + (Q.x - P.x) * t, y: P.y + (Q.y - P.y) * t }); }
    }
    return out;
  };
  return [clip(1), clip(-1)];
}

function segCross(a, b, c, d) {
  const o = (p, q, r) => Math.sign((q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x));
  return o(a, b, c) !== o(a, b, d) && o(c, d, a) !== o(c, d, b);
}

export const panelTool = {
  id: 'panel', name: '컷 나누기', icon: 'panel', key: 'k', cursor: 'crosshair',
  opt: { mode: 'split', gutterX: 0, gutterY: 0, snap: true },
  drag: null,
  poly: null,

  gutters() {
    const doc = app.doc;
    const gx = this.opt.gutterX || Math.round(doc.width * 0.02);
    const gy = this.opt.gutterY || Math.round(doc.width * 0.035);
    return { gx, gy };
  },

  _snapLine(d) {
    let a = { x: d.x0, y: d.y0 }, b = { x: d.x1, y: d.y1 };
    if (!this.opt.snap) return [a, b];
    const ang = Math.atan2(b.y - a.y, b.x - a.x) * 180 / Math.PI;
    const s = Math.round(ang / 90) * 90;
    if (Math.abs(ang - s) < 6) {
      if (Math.abs(s) % 180 === 0) { const y = (a.y + b.y) / 2; a = { x: a.x, y }; b = { x: b.x, y }; }
      else { const x = (a.x + b.x) / 2; a = { x, y: a.y }; b = { x, y: b.y }; }
    }
    return [a, b];
  },

  down(e) {
    if (this.opt.mode === 'poly') {
      if (!this.poly) this.poly = [];
      const first = this.poly[0];
      if (first && this.poly.length >= 3) {
        const s = app.view.toScreen(first.x, first.y);
        if (Math.hypot(s.x - e.sx, s.y - e.sy) < (e.type === 'touch' ? 26 : 16)) { this.finishPoly(); return; }
      }
      this.poly.push({ x: e.x, y: e.y });
      app.requestRender();
      return;
    }
    const layer = panelLayer(false);
    this.drag = { x0: e.x, y0: e.y, x1: e.x, y1: e.y, sx0: e.sx, sy0: e.sy, moved: false };
    if (this.opt.mode === 'move' && layer) {
      const i = panelAt(layer, e.x, e.y);
      if (i >= 0) { this.drag.panel = i; this.drag.orig = panelPts(layer.meta.panels[i]).map(p => ({ ...p })); }
    }
    this.busy = true;
  },

  move(e) {
    const d = this.drag;
    if (!d) return;
    d.x1 = e.x; d.y1 = e.y;
    if (Math.hypot(e.sx - d.sx0, e.sy - d.sy0) > 6) d.moved = true;
    if (this.opt.mode === 'move' && d.panel != null) {
      const l = panelLayer(false);
      l.meta.panels[d.panel] = { pts: d.orig.map(p => ({ x: p.x + e.x - d.x0, y: p.y + e.y - d.y0 })) };
      renderPanels(l);
    }
    app.requestRender();
  },

  hover(e) { if (this.poly) { this.cursor_ = { x: e.x, y: e.y }; app.requestRender(); } },

  up() {
    const d = this.drag;
    this.drag = null;
    this.busy = false;
    if (!d) return;
    const mode = this.opt.mode;
    if (mode === 'select' || !d.moved) {
      const layer = panelLayer(false);
      const i = panelAt(layer, d.x0, d.y0);
      if (i < 0) { app.toast('컷 안쪽을 탭하세요'); return; }
      const q = panelPts(layer.meta.panels[i]), ln = layer.meta.line;
      selectionHistory(() => { const sel = app.doc.selection; sel.polygon(q, 'replace'); sel.grow(-Math.ceil(ln)); }, '컷 선택');
      return;
    }
    if (mode === 'draw') {
      const r = rectFromPoints(d.x0, d.y0, d.x1, d.y1);
      if (r.w < 8 || r.h < 8) return;
      const layer = panelLayer(true);
      setPanelsWithMeta(layer, { ...layer.meta, panels: [...layer.meta.panels, { pts: roundPts(rectPts(r)) }] });
    } else if (mode === 'split') {
      this.split(d);
    } else if (mode === 'move' && d.panel != null) {
      const layer = panelLayer(false);
      const moved = layer.meta.panels[d.panel];
      layer.meta.panels[d.panel] = { pts: d.orig };
      renderPanels(layer);
      const panels = layer.meta.panels.map((p, i) => (i === d.panel ? { pts: roundPts(moved.pts) } : p));
      setPanelsWithMeta(layer, { ...layer.meta, panels });
    }
  },

  /** 직선(가로/세로/사선)으로 컷 자르기 */
  split(d) {
    const layer = panelLayer(false);
    if (!layer || !layer.meta.panels.length) { app.toast('먼저 "페이지 컷 만들기"로 기본 컷을 만드세요'); return; }
    const [a, b] = this._snapLine(d);
    const { gx, gy } = this.gutters();
    const ang = Math.atan2(b.y - a.y, b.x - a.x);
    const g = gy * Math.abs(Math.cos(ang)) + gx * Math.abs(Math.sin(ang));
    const panels = [];
    let did = false;
    for (const p of layer.meta.panels) {
      const q = panelPts(p);
      const crosses = q.some((v, i) => segCross(a, b, v, q[(i + 1) % q.length])) || inPoly(q, a.x, a.y) || inPoly(q, b.x, b.y);
      if (crosses) {
        const parts = splitPoly(q, a, b, g).filter(part => part.length >= 3 && polyArea(part) > 400);
        if (parts.length === 2) { for (const part of parts) panels.push({ pts: roundPts(part) }); did = true; continue; }
      }
      panels.push(p);
    }
    if (!did) { app.toast('컷을 가로지르도록 선을 그어 주세요'); return; }
    setPanelsWithMeta(layer, { ...layer.meta, panels });
  },

  finishPoly() {
    const q = this.poly;
    this.poly = null;
    app.requestRender();
    if (!q || q.length < 3) return;
    const layer = panelLayer(true);
    setPanelsWithMeta(layer, { ...layer.meta, panels: [...layer.meta.panels, { pts: roundPts(q) }] });
  },

  commitPending() { if (this.poly) this.finishPoly(); },
  deactivate() { this.poly = null; },
  cancel() { this.drag = null; this.busy = false; app.requestRender(); },

  overlay(ctx, view) {
    const layer = panelLayer(false);
    const outline = (q, close = true) => {
      ctx.beginPath();
      q.forEach((p, i) => { const s = view.toScreen(p.x, p.y); if (i) ctx.lineTo(s.x, s.y); else ctx.moveTo(s.x, s.y); });
      if (close) ctx.closePath();
      ctx.stroke();
    };
    if (layer && layer.visible) {
      ctx.strokeStyle = 'rgba(47,124,246,0.55)';
      ctx.lineWidth = 1;
      ctx.setLineDash([5, 4]);
      for (const p of layer.meta.panels) outline(panelPts(p));
      ctx.setLineDash([]);
    }
    if (this.poly) {
      ctx.strokeStyle = '#e8484a'; ctx.lineWidth = 2;
      outline(this.cursor_ ? [...this.poly, this.cursor_] : this.poly, false);
      for (const [i, p] of this.poly.entries()) {
        const s = view.toScreen(p.x, p.y);
        ctx.fillStyle = i === 0 ? '#e8484a' : '#fff';
        ctx.beginPath(); ctx.arc(s.x, s.y, i === 0 ? 8 : 5, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
      }
    }
    const d = this.drag;
    if (!d || !d.moved) return;
    ctx.strokeStyle = '#e8484a';
    ctx.lineWidth = 2;
    if (this.opt.mode === 'split') {
      const [a, b] = this._snapLine(d);
      outline([a, b], false);
    } else if (this.opt.mode === 'draw') {
      outline(rectPts(rectFromPoints(d.x0, d.y0, d.x1, d.y1)));
    }
  },

  options(el) {
    const o = this.opt;
    const layer = () => panelLayer(false);
    const help = document.createElement('div');
    const syncHelp = () => {
      help.className = 'help';
      help.textContent = {
        split: '컷을 가로지르는 선을 그으면 나뉩니다. 비스듬히 그으면 사선 컷 (간격 자동).',
        draw: '드래그해서 사각형 컷을 만듭니다.',
        poly: '탭으로 꼭짓점을 찍고, 첫 점(빨간 점)을 다시 탭하거나 Enter로 다각형 컷 완성.',
        select: '컷 안을 탭하면 그 컷 안쪽이 선택됩니다 → 그 안에서만 그려져요.',
        move: '컷을 끌어서 옮깁니다.',
      }[o.mode];
    };
    syncHelp();
    el.append(
      section('컷 도구',
        segmented([['split', '자르기'], ['draw', '사각형'], ['poly', '다각형'], ['select', '컷 선택'], ['move', '이동']], () => o.mode, v => { this.commitPending(); o.mode = v; syncHelp(); }),
        help,
        toggle('거의 수평/수직이면 맞추기', () => o.snap, v => { o.snap = v; }),
        row(
          button('페이지 컷 만들기', () => pageFrameDialog()),
          button('마지막 컷 삭제', () => {
            const l = layer();
            if (!l || !l.meta.panels.length) return;
            setPanelsWithMeta(l, { ...l.meta, panels: l.meta.panels.slice(0, -1) });
          }),
        ),
      ),
      section('컷 설정',
        slider('테두리 굵기', { min: 1, max: 40, get: () => layer()?.meta.line ?? 6, set: v => { const l = layer(); if (l) { l.meta.line = v; renderPanels(l); } } }),
        slider('좌우 간격', { min: 0, max: 200, get: () => this.gutters().gx, set: v => { o.gutterX = v; }, format: v => v + 'px' }),
        slider('상하 간격', { min: 0, max: 300, get: () => this.gutters().gy, set: v => { o.gutterY = v; }, format: v => v + 'px' }),
        toggle('컷 바깥을 용지색으로 가리기', () => layer()?.meta.maskOutside ?? true, v => { const l = layer(); if (l) { setPanelsWithMeta(l, { ...l.meta, maskOutside: v }); } }),
      ),
    );
  },
};

export async function pageFrameDialog() {
  const doc = app.doc;
  const v = await app.ui.form('페이지 컷 만들기', [
    { key: 'margin', label: '바깥 여백(px)', type: 'number', value: Math.round(doc.width * 0.07) },
    { key: 'rows', label: '세로 칸 수', type: 'number', value: 1, min: 1, max: 12 },
    { key: 'cols', label: '가로 칸 수', type: 'number', value: 1, min: 1, max: 6 },
    { key: 'gx', label: '좌우 간격(px)', type: 'number', value: Math.round(doc.width * 0.02) },
    { key: 'gy', label: '상하 간격(px)', type: 'number', value: Math.round(doc.width * 0.035) },
    { key: 'line', label: '테두리 굵기(px)', type: 'number', value: Math.max(3, Math.round(doc.width / 400)) },
    { key: 'info', type: 'info', label: '기존 컷은 대체됩니다. 이후 "자르기" 모드로 선을 그어 더 나눌 수 있습니다 (비스듬히 그으면 사선 컷).' },
  ], { okLabel: '만들기' });
  if (!v) return;
  const layer = panelLayer(true);
  const m = v.margin, W = doc.width - m * 2, H = doc.height - m * 2;
  const cw = (W - v.gx * (v.cols - 1)) / v.cols, ch = (H - v.gy * (v.rows - 1)) / v.rows;
  const panels = [];
  for (let r = 0; r < v.rows; r++) for (let c = 0; c < v.cols; c++) {
    panels.push({ pts: roundPts(rectPts({ x: m + c * (cw + v.gx), y: m + r * (ch + v.gy), w: cw, h: ch })) });
  }
  panelTool.opt.gutterX = v.gx; panelTool.opt.gutterY = v.gy;
  setPanelsWithMeta(layer, { ...layer.meta, panels, line: v.line });
  app.setTool('panel');
}

// =====================================================================
// 말풍선
// =====================================================================
function seeded(seed) {
  let s = seed % 2147483647; if (s <= 0) s += 2147483646;
  return () => (s = s * 16807 % 2147483647) / 2147483647;
}

export function bubblePaths(b) {
  const body = new Path2D();
  const cx = b.x + b.w / 2, cy = b.y + b.h / 2, rx = b.w / 2, ry = b.h / 2;
  const rnd = seeded(b.seed || 7);
  if (b.shape === 'rect') {
    body.roundRect(b.x, b.y, b.w, b.h, Math.min(b.w, b.h) * 0.18);
  } else if (b.shape === 'shout') {
    const spikes = Math.max(10, Math.round((rx + ry) / 18));
    for (let i = 0; i < spikes * 2; i++) {
      const a = (i / (spikes * 2)) * Math.PI * 2;
      const out = i % 2 === 0;
      const k = out ? 1.12 + rnd() * 0.18 : 0.86 + rnd() * 0.04;
      const x = cx + Math.cos(a) * rx * k, y = cy + Math.sin(a) * ry * k;
      if (i) body.lineTo(x, y); else body.moveTo(x, y);
    }
    body.closePath();
  } else if (b.shape === 'cloud') {
    const n = Math.max(8, Math.round((rx + ry) / 22));
    body.ellipse(cx, cy, rx * 0.86, ry * 0.86, 0, 0, Math.PI * 2);
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 2;
      const r = (Math.PI * (rx + ry) / n) * (0.55 + rnd() * 0.12);
      const x = cx + Math.cos(a) * rx * 0.9, y = cy + Math.sin(a) * ry * 0.9;
      body.moveTo(x + r, y);
      body.arc(x, y, r, 0, Math.PI * 2);
    }
  } else {
    body.ellipse(cx, cy, rx, ry, 0, 0, Math.PI * 2);
  }
  let tail = null;
  if (b.tail) {
    tail = new Path2D();
    const tx = b.tail.x, ty = b.tail.y;
    const dx = tx - cx, dy = ty - cy, L = Math.hypot(dx, dy) || 1;
    const nx = -dy / L, ny = dx / L;
    if (b.shape === 'cloud') {
      for (let i = 1; i <= 3; i++) {
        const t = 0.62 + i * 0.12;
        const x = cx + dx * t, y = cy + dy * t;
        const r = Math.min(rx, ry) * (0.16 - i * 0.035);
        tail.moveTo(x + r, y);
        tail.ellipse(x, y, r * 1.3, r, 0, 0, Math.PI * 2);
      }
    } else {
      const bw = Math.min(rx, ry) * (b.tailWidth ?? 0.45);
      const bx = cx + dx * 0.25, by = cy + dy * 0.25;
      const b1 = { x: bx + nx * bw / 2, y: by + ny * bw / 2 }, b2 = { x: bx - nx * bw / 2, y: by - ny * bw / 2 };
      const bend = b.shape === 'shout' ? 0 : L * 0.08;
      tail.moveTo(b1.x, b1.y);
      tail.quadraticCurveTo((b1.x + tx) / 2 + nx * bend, (b1.y + ty) / 2 + ny * bend, tx, ty);
      tail.quadraticCurveTo((b2.x + tx) / 2 + nx * bend * 0.4, (b2.y + ty) / 2 + ny * bend * 0.4, b2.x, b2.y);
      tail.closePath();
    }
  }
  return { body, tail };
}

/** 테두리 2배 굵기로 먼저 그리고 채우기로 덮어 합쳐진 외곽선을 만든다 */
export function drawBubble(ctx, b) {
  const { body, tail } = bubblePaths(b);
  ctx.save();
  ctx.lineJoin = 'round';
  ctx.lineCap = 'round';
  if (b.line > 0) {
    ctx.strokeStyle = b.stroke;
    ctx.lineWidth = b.line * 2;
    ctx.stroke(body);
    if (tail) ctx.stroke(tail);
  }
  ctx.fillStyle = b.fill;
  ctx.fill(body);
  if (tail) ctx.fill(tail);
  ctx.restore();
}

/** 말풍선 레이어: meta.bubbles 목록으로부터 다시 그림 (나중에 이동·크기·꼬리·모양 수정 가능) */
export function renderBubbles(layer) {
  const ctx = layer.ctx;
  ctx.clearRect(0, 0, layer.canvas.width, layer.canvas.height);
  // 이어 붙인 풍선(같은 group)은 테두리를 모두 먼저 그리고 안을 한꺼번에 채워 하나의 외곽선으로
  const done = new Set();
  for (const b of layer.meta?.bubbles || []) {
    if (done.has(b)) continue;
    const group = b.group ? layer.meta.bubbles.filter(x => x.group === b.group) : [b];
    group.forEach(x => done.add(x));
    if (group.length === 1) { drawBubble(ctx, b); continue; }
    const paths = group.map(x => ({ b: x, ...bubblePaths(x) }));
    ctx.save();
    ctx.lineJoin = 'round'; ctx.lineCap = 'round';
    for (const { b: x, body, tail } of paths) {
      if (x.line > 0) { ctx.strokeStyle = x.stroke; ctx.lineWidth = x.line * 2; ctx.stroke(body); if (tail) ctx.stroke(tail); }
    }
    for (const { b: x, body, tail } of paths) { ctx.fillStyle = x.fill; ctx.fill(body); if (tail) ctx.fill(tail); }
    ctx.restore();
  }
  layer.touch();
}

/** 고른 풍선과 겹치는 풍선들을 이어 붙이기 / 떼기 */
function bboxOf(b) {
  const xs = [b.x, b.x + b.w], ys = [b.y, b.y + b.h];
  if (b.tail) { xs.push(b.tail.x); ys.push(b.tail.y); }
  return { x0: Math.min(...xs), y0: Math.min(...ys), x1: Math.max(...xs), y1: Math.max(...ys) };
}

const isEditableBubbleLayer = (l) => l?.kind === 'bubble' && Array.isArray(l.meta?.bubbles);

export function bubbleLayer() {
  const doc = app.doc;
  const act = doc.active;
  if (isEditableBubbleLayer(act)) return act;
  const existing = doc.findLayer(isEditableBubbleLayer);
  if (existing) { doc.setActive(existing); return existing; }
  // 예전 방식(그림으로만 남은) 말풍선 레이어는 건드리지 않고 새로 만든다
  return addLayer({ name: '말풍선', kind: 'bubble', meta: { bubbles: [] } }, doc.layers.length);
}

let hitCtx = null;
function bubbleHit(b, x, y) {
  hitCtx ||= createCanvas(1, 1).getContext('2d');
  const { body, tail } = bubblePaths(b);
  hitCtx.lineWidth = Math.max(8, b.line * 2);
  return hitCtx.isPointInPath(body, x, y) || hitCtx.isPointInStroke(body, x, y) || (tail && hitCtx.isPointInPath(tail, x, y));
}

/** (x, y) 위의 말풍선: 현재 레이어 먼저, 위에 있는 것부터 */
function bubbleAt(x, y) {
  const doc = app.doc;
  const layers = doc.layers.filter(l => isEditableBubbleLayer(l) && l.visible && !l.locked);
  layers.sort((a, b) => (b === doc.active) - (a === doc.active) || doc.indexOf(b) - doc.indexOf(a));
  for (const l of layers) {
    const list = l.meta.bubbles;
    for (let i = list.length - 1; i >= 0; i--) if (bubbleHit(list[i], x, y)) return { layer: l, b: list[i] };
  }
  return null;
}

/** 연결된 대사 레이어 옮기기 */
function moveLinkedText(b, dx, dy, base) {
  const t = b.textId && app.doc.byId(b.textId);
  if (!t || t.kind !== 'text' || !base) return;
  if (base.mat) { t.meta.mat = [...base.mat]; t.meta.mat[4] += dx; t.meta.mat[5] += dy; }
  else { t.meta.x = base.x + dx; t.meta.y = base.y + dy; }
  renderText(t);
}

/** 말풍선 목록(+연결된 대사) 변경을 실행취소 기록으로 */
function pushBubbleHistory(layer, before, after, label, text) {
  const apply = (list, tm) => {
    layer.meta = { ...layer.meta, bubbles: structuredClone(list) };
    renderBubbles(layer);
    if (text && tm) { text.layer.meta = structuredClone(tm); renderText(text.layer); }
  };
  app.history.push({
    label,
    bytes: JSON.stringify(after).length * 2,
    undo: () => apply(before, text?.before),
    redo: () => apply(after, text?.after),
  });
}

const HANDLE = 12; // 화면 px

export const bubbleTool = {
  id: 'bubble', name: '말풍선', icon: 'bubble', key: 'f', cursor: 'crosshair',
  opt: { shape: 'ellipse', line: 4, tail: true, tailWidth: 0.45, fill: '#ffffff', stroke: '#000000', autoText: true },
  st: null,
  sel: null,      // { layer, id } 고른 말풍선
  drag: null,

  get busy() { return !!this.st || !!this.drag; },

  _bubble() {
    const s = this.st, o = this.opt;
    const r = rectFromPoints(s.x0, s.y0, s.x1, s.y1);
    return { ...r, shape: o.shape, line: o.line, fill: o.fill, stroke: o.stroke, tailWidth: o.tailWidth, seed: s.seed, tail: s.phase === 'tail' && o.tail ? s.tail : null };
  },

  selected() {
    const s = this.sel;
    if (!s || !app.doc.layers.includes(s.layer) || !isEditableBubbleLayer(s.layer)) return null;
    return s.layer.meta.bubbles.find(b => b.id === s.id) || null;
  },

  _handles(b) {
    const hs = [
      { k: 'nw', x: b.x, y: b.y }, { k: 'ne', x: b.x + b.w, y: b.y },
      { k: 'se', x: b.x + b.w, y: b.y + b.h }, { k: 'sw', x: b.x, y: b.y + b.h },
    ];
    if (b.tail) hs.push({ k: 'tail', x: b.tail.x, y: b.tail.y });
    return hs;
  },

  down(e) {
    if (this.st?.phase === 'tail') { this.st.tail = { x: e.x, y: e.y }; this.st.tailDrag = true; app.requestRender(); return; }
    const cur = this.selected();
    if (cur) {
      const z = app.view.zoom || 1;
      const hd = this._handles(cur).find(p => Math.hypot(p.x - e.x, p.y - e.y) * z <= HANDLE);
      if (hd) { this._startDrag(this.sel.layer, cur, hd.k, e); return; }
    }
    const hit = bubbleAt(e.x, e.y);
    if (hit) {
      this.sel = { layer: hit.layer, id: hit.b.id };
      if (app.doc.active !== hit.layer) app.doc.setActive(hit.layer);
      this._startDrag(hit.layer, hit.b, 'move', e);
      app.bus.emit('tool:options');
      return;
    }
    if (this.sel) { this.sel = null; app.bus.emit('tool:options'); }
    this.st = { phase: 'box', x0: e.x, y0: e.y, x1: e.x, y1: e.y, seed: (Math.random() * 1e6) | 0 };
  },

  _startDrag(layer, b, mode, e) {
    if (!checkEditable(layer, { bubble: true })) return;
    const t = b.textId && app.doc.byId(b.textId);
    this.drag = {
      layer, id: b.id, mode, x0: e.x, y0: e.y, sx0: e.sx, sy0: e.sy, moved: false,
      orig: structuredClone(b), before: structuredClone(layer.meta.bubbles),
      text: t?.kind === 'text' ? { layer: t, before: structuredClone(t.meta) } : null,
    };
  },

  _dragMove(e) {
    const d = this.drag;
    if (Math.hypot(e.sx - d.sx0, e.sy - d.sy0) > 4) d.moved = true;
    if (!d.moved) return;
    const b = d.layer.meta.bubbles.find(x => x.id === d.id);
    if (!b) return;
    const o = d.orig, dx = e.x - d.x0, dy = e.y - d.y0;
    if (d.mode === 'move') {
      b.x = o.x + dx; b.y = o.y + dy;
      if (o.tail) b.tail = { x: o.tail.x + dx, y: o.tail.y + dy };
      moveLinkedText(b, dx, dy, d.text?.before);
    } else if (d.mode === 'tail') {
      b.tail = { x: e.x, y: e.y };
    } else {
      // 모서리 크기 조절: 반대쪽 모서리 고정
      let x0 = o.x, y0 = o.y, x1 = o.x + o.w, y1 = o.y + o.h;
      if (d.mode.includes('w')) x0 = Math.min(x1 - 10, o.x + dx); else x1 = Math.max(x0 + 10, x1 + dx);
      if (d.mode.includes('n')) y0 = Math.min(y1 - 10, o.y + dy); else y1 = Math.max(y0 + 10, y1 + dy);
      Object.assign(b, { x: x0, y: y0, w: x1 - x0, h: y1 - y0 });
      // 대사는 풍선 가운데를 따라간다
      moveLinkedText(b, (x0 + x1 - o.x * 2 - o.w) / 2, (y0 + y1 - o.y * 2 - o.h) / 2, d.text?.before);
    }
    if (!d.queued) { d.queued = true; requestAnimationFrame(() => { d.queued = false; renderBubbles(d.layer); app.requestRender(); }); }
  },

  move(e) {
    if (this.drag) { this._dragMove(e); return; }
    const s = this.st;
    if (!s) return;
    if (s.phase === 'box') { s.x1 = e.x; s.y1 = e.y; }
    else s.tail = { x: e.x, y: e.y };
    app.requestRender();
  },

  hover(e) {
    if (this.st?.phase === 'tail') { this.st.tail = { x: e.x, y: e.y }; app.requestRender(); }
  },

  up() {
    if (this.drag) {
      const d = this.drag;
      this.drag = null;
      if (d.moved) {
        renderBubbles(d.layer);
        const after = structuredClone(d.layer.meta.bubbles);
        const text = d.text ? { ...d.text, after: structuredClone(d.text.layer.meta) } : null;
        pushBubbleHistory(d.layer, d.before, after, { move: '말풍선 이동', tail: '말풍선 꼬리' }[d.mode] || '말풍선 크기', text);
      }
      app.requestRender();
      return;
    }
    const s = this.st;
    if (!s) return;
    if (s.phase === 'box') {
      const r = rectFromPoints(s.x0, s.y0, s.x1, s.y1);
      if (r.w < 10 || r.h < 10) { this.st = null; app.requestRender(); return; }
      if (this.opt.tail) {
        s.phase = 'tail';
        s.tail = { x: r.x + r.w * 0.3, y: r.y + r.h * 1.35 };
        app.toast('꼬리 끝을 탭(또는 드래그)하세요');
        app.requestRender();
        return;
      }
    }
    this.commit();
  },

  commit() {
    const s = this.st;
    if (!s) return;
    const b = this._bubble();
    if (s.phase === 'tail' && this.opt.tail) b.tail = s.tail;
    b.id = 'b' + Date.now().toString(36) + ((Math.random() * 1e4) | 0);
    this.st = null;
    const layer = bubbleLayer();
    if (!checkEditable(layer, { bubble: true })) { app.requestRender(); return; }
    const before = structuredClone(layer.meta.bubbles);
    layer.meta.bubbles.push(b);
    renderBubbles(layer);
    pushBubbleHistory(layer, before, structuredClone(layer.meta.bubbles), '말풍선');
    this.sel = { layer, id: b.id };
    app.requestRender();
    if (this.opt.autoText) {
      const t = app.run('text.createAt', b.x + b.w / 2, b.y + b.h / 2, { center: true, maxW: b.w * 0.75 });
      if (t) b.textId = t.id;
    }
  },

  /** 고른 말풍선 속성 바꾸기 (옵션 패널에서) */
  _edit(fn, label = '말풍선 수정') {
    const b = this.selected();
    if (!b) return;
    const layer = this.sel.layer;
    const before = structuredClone(layer.meta.bubbles);
    fn(b);
    renderBubbles(layer);
    pushBubbleHistory(layer, before, structuredClone(layer.meta.bubbles), label);
    app.requestRender();
  },

  deleteSelected() {
    const b = this.selected();
    if (!b) return false;
    const layer = this.sel.layer;
    const t = b.textId && app.doc.byId(b.textId);
    app.history.batch('말풍선 삭제', () => {
      const before = structuredClone(layer.meta.bubbles);
      layer.meta.bubbles = layer.meta.bubbles.filter(x => x !== b);
      renderBubbles(layer);
      pushBubbleHistory(layer, before, structuredClone(layer.meta.bubbles), '말풍선 삭제');
      if (t) deleteLayer(t);
    });
    this.sel = null;
    app.bus.emit('tool:options');
    app.requestRender();
    return true;
  },

  onKey(e) {
    if ((e.key === 'Delete' || e.key === 'Backspace') && this.selected()) { this.deleteSelected(); return true; }
    if (e.key === 'Escape' && this.sel) { this.sel = null; app.requestRender(); app.bus.emit('tool:options'); return true; }
    return false;
  },

  commitPending() { if (this.st?.phase === 'tail') this.commit(); },
  deactivate() { this.commitPending(); this.st = null; this.sel = null; this.drag = null; app.requestRender(); },
  cancel() {
    if (this.drag) {
      const d = this.drag;
      this.drag = null;
      d.layer.meta.bubbles = d.before;
      renderBubbles(d.layer);
      if (d.text) { d.text.layer.meta = d.text.before; renderText(d.text.layer); }
    }
    if (this.st?.phase === 'box') this.st = null;
    app.requestRender();
  },

  overlay(ctx, view) {
    const b = this.selected();
    if (b && !this.st) {
      ctx.save();
      ctx.strokeStyle = '#2f7cf6';
      ctx.setLineDash([5, 4]);
      const p0 = view.toScreen(b.x, b.y), p1 = view.toScreen(b.x + b.w, b.y + b.h);
      ctx.strokeRect(Math.min(p0.x, p1.x), Math.min(p0.y, p1.y), Math.abs(p1.x - p0.x), Math.abs(p1.y - p0.y));
      ctx.setLineDash([]);
      for (const hd of this._handles(b)) {
        const p = view.toScreen(hd.x, hd.y);
        ctx.fillStyle = hd.k === 'tail' ? '#f08c2e' : '#fff';
        ctx.beginPath();
        if (hd.k === 'tail') ctx.arc(p.x, p.y, 7, 0, Math.PI * 2); else ctx.rect(p.x - 6, p.y - 6, 12, 12);
        ctx.fill(); ctx.stroke();
      }
      ctx.restore();
    }
    if (!this.st) return;
    const m = view.m;
    ctx.transform(m.a, m.b, m.c, m.d, m.e, m.f);
    ctx.globalAlpha = 0.85;
    drawBubble(ctx, this._bubble());
  },

  options(el) {
    const o = this.opt;
    const b = this.selected();
    if (b) {
      // 슬라이더는 끄는 동안 바로 보여 주고, 손을 뗄 때 한 번만 기록
      const layer = this.sel.layer;
      let liveBefore = null;
      const live = (k) => (v) => { liveBefore ||= structuredClone(layer.meta.bubbles); b[k] = v; renderBubbles(layer); };
      const endLive = (label) => () => { if (liveBefore) pushBubbleHistory(layer, liveBefore, structuredClone(layer.meta.bubbles), label); liveBefore = null; };
      el.append(section('고른 말풍선',
        segmented([['ellipse', '타원'], ['rect', '사각'], ['shout', '외침'], ['cloud', '생각']], () => b.shape, v => this._edit(x => { x.shape = v; }, '말풍선 모양')),
        slider('테두리 굵기', { min: 0, max: 30, step: 0.5, get: () => b.line, set: live('line'), onEnd: endLive('말풍선 테두리') }),
        slider('꼬리 폭', { min: 0.1, max: 1, step: 0.05, get: () => b.tailWidth ?? 0.45, set: live('tailWidth'), onEnd: endLive('꼬리 폭'), format: v => Math.round(v * 100) + '%' }),
        row(colorInput('채우기', () => b.fill, v => this._edit(x => { x.fill = v; }, '말풍선 색')), colorInput('테두리', () => b.stroke, v => this._edit(x => { x.stroke = v; }, '말풍선 색'))),
        row(
          button(b.tail ? '꼬리 없애기' : '꼬리 달기', () => { this._edit(x => { x.tail = x.tail ? null : { x: x.x + x.w * 0.3, y: x.y + x.h * 1.35 }; }, '말풍선 꼬리'); app.bus.emit('tool:options'); }),
          button('대사 편집', () => { const t = b.textId && app.doc.byId(b.textId); if (t?.kind === 'text') editTextLayer(t, false); else { const n = app.run('text.createAt', b.x + b.w / 2, b.y + b.h / 2, { center: true }); if (n) b.textId = n.id; } }),
          button('삭제', () => this.deleteSelected()),
        ),
        row(
          b.group
            ? button('이어 붙인 것 떼기', () => this._edit(() => { const g = b.group; for (const x of this.sel.layer.meta.bubbles) if (x.group === g) delete x.group; }, '말풍선 떼기'))
            : button('겹친 풍선과 이어 붙이기', () => {
              const list = this.sel.layer.meta.bubbles, bb = bboxOf(b);
              const touching = list.filter(x => x !== b && (() => { const o = bboxOf(x); return o.x0 < bb.x1 && bb.x0 < o.x1 && o.y0 < bb.y1 && bb.y0 < o.y1; })());
              if (!touching.length) { app.toast('겹친 말풍선이 없습니다. 풍선을 조금 겹치게 옮긴 뒤 누르세요'); return; }
              this._edit(() => { const g = b.group || touching.find(x => x.group)?.group || ('g' + Date.now().toString(36)); b.group = g; for (const x of touching) x.group = g; }, '말풍선 이어 붙이기');
              app.toast(`${touching.length + 1}개를 하나의 외곽선으로 이었습니다`);
              app.bus.emit('tool:options');
            }),
        ),
        helpText('풍선 안을 드래그: 이동(대사도 함께) · 네 모서리: 크기 · 주황 점: 꼬리 끝 · Delete: 삭제'),
      ));
    }
    el.append(section('새 말풍선',
      segmented([['ellipse', '타원'], ['rect', '사각'], ['shout', '외침'], ['cloud', '생각']], () => o.shape, v => { o.shape = v; }),
      slider('테두리 굵기', { min: 0, max: 30, step: 0.5, get: () => o.line, set: v => { o.line = v; } }),
      toggle('꼬리', () => o.tail, v => { o.tail = v; }),
      slider('꼬리 폭', { min: 0.1, max: 1, step: 0.05, get: () => o.tailWidth, set: v => { o.tailWidth = v; }, format: v => Math.round(v * 100) + '%' }),
      row(colorInput('채우기', () => o.fill, v => { o.fill = v; }), colorInput('테두리', () => o.stroke, v => { o.stroke = v; })),
      toggle('만든 뒤 바로 대사 입력', () => o.autoText, v => { o.autoText = v; }),
      helpText('빈 곳 드래그로 풍선 크기 → 꼬리 끝 탭. 만든 풍선은 탭해서 고른 뒤 이동·크기·꼬리·모양을 언제든 바꿀 수 있습니다.'),
    ));
  },
};

// =====================================================================
// 텍스트 (다시 편집 가능한 텍스트 레이어)
// =====================================================================
export const SYSTEM_FONTS = [
  ['Malgun Gothic', '맑은 고딕'], ['Gulim', '굴림'], ['Dotum', '돋움'], ['Batang', '바탕'], ['Gungsuh', '궁서'],
  ['NanumGothic', '나눔고딕'], ['NanumMyeongjo', '나눔명조'], ['NanumPen', '나눔손글씨 펜'], ['NanumBarunGothic', '나눔바른고딕'],
  ['Arial', 'Arial'], ['Comic Sans MS', 'Comic Sans'], ['Impact', 'Impact'], ['Yu Gothic', 'Yu Gothic'], ['MS Gothic', 'MS Gothic'],
];

/** "{漢字|かんじ}" → [{ text, ruby }] (루비: 글자 위/옆에 작게 읽는 법) */
export function parseRuby(ln) {
  const out = [];
  let last = 0;
  for (const mm of ln.matchAll(/\{([^{}|]+)\|([^{}]+)\}/g)) {
    if (mm.index > last) out.push({ text: ln.slice(last, mm.index) });
    out.push({ text: mm[1], ruby: mm[2] });
    last = mm.index + mm[0].length;
  }
  if (last < ln.length) out.push({ text: ln.slice(last) });
  return out;
}
const PUNCT = new Set([...'、。，．・：；！？!?…‥「」『』（）()〈〉《》“”‘’,.~～']);

const ROTATE_V = new Set([...'ー－—―‐-~～…‥()（）[]［］{}｛｝「」『』〈〉《》<>＜＞=＝']);

export const TEXT_DEFAULTS = {
  text: '', font: 'Malgun Gothic', size: 42, color: '#000000', bold: true, italic: false,
  lineHeight: 1.3, vertical: false, align: 'center', strokeWidth: 0, strokeColor: '#ffffff',
};

export function textFontString(m) {
  return `${m.italic ? 'italic ' : ''}${m.bold ? 'bold ' : ''}${m.size}px "${m.font}", "Malgun Gothic", sans-serif`;
}

// 효과음 글자(tools/sfx.js)가 글자 레이어 그리기·편집에 끼어드는 자리
let sfxHooks = null;
export function setSfxHooks(h) { sfxHooks = h; }

/** 텍스트 레이어 다시 그리기. meta.x, meta.y 는 기준점(정렬에 따라 왼쪽/가운데/오른쪽, 위쪽) */
export function renderText(layer) {
  if (layer.meta?.sfx && sfxHooks) { sfxHooks.render(layer); return; }
  const m = layer.meta, ctx = layer.ctx;
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, layer.canvas.width, layer.canvas.height);
  // 변형(이동/크기/회전)된 텍스트는 행렬을 적용해서 그린다 → 계속 편집 가능
  const mat = m.mat ? new DOMMatrix(m.mat) : null;
  if (mat) ctx.setTransform(mat);
  ctx.font = textFontString(m);
  ctx.textBaseline = 'top';
  ctx.lineJoin = 'round';
  ctx.fillStyle = m.color;
  ctx.strokeStyle = m.strokeColor;
  ctx.lineWidth = m.strokeWidth * 2;
  const track = (m.tracking || 0) / 100;   // 자간 (글자 크기의 %)
  if (!m.vertical && track && 'letterSpacing' in ctx) ctx.letterSpacing = `${(m.size * track).toFixed(2)}px`;
  const lines = String(m.text || '').split('\n');
  const lh = m.size * m.lineHeight;
  const draw = (t, x, y) => {
    if (m.strokeWidth > 0) ctx.strokeText(t, x, y);
    ctx.fillText(t, x, y);
  };
  let box;
  const rich = /\{[^{}|]+\|[^{}]+\}/.test(m.text || '') || m.kern;
  const rubySize = m.size * 0.42;
  const rubyFont = textFontString({ ...m, size: rubySize, bold: false });
  const baseFont = ctx.font;
  // 문장부호 좁게: 글자 폭을 줄여서 다음 글자를 당김
  const adv = (ch) => ctx.measureText(ch).width * (m.kern && PUNCT.has(ch) ? 0.62 : 1);
  if (!m.vertical && rich) {
    // 루비·문장부호 좁게가 있으면 한 글자씩 그린다
    ctx.textAlign = 'left';
    let maxW = 0, rubyTop = false;
    lines.forEach((ln, i) => {
      const runs = parseRuby(ln);
      const W = runs.reduce((a, r) => a + [...r.text].reduce((b, ch) => b + adv(ch), 0), 0) - m.size * track;
      let x = m.align === 'center' ? m.x - W / 2 : m.align === 'right' ? m.x - W : m.x;
      const y = m.y + i * lh;
      for (const r of runs) {
        const x0 = x;
        for (const ch of r.text) { draw(ch, x, y); x += adv(ch); }
        if (r.ruby) {
          ctx.font = rubyFont; ctx.textAlign = 'center';
          draw(r.ruby, (x0 + x) / 2, y - rubySize * 1.08);
          ctx.font = baseFont; ctx.textAlign = 'left';
          if (i === 0) rubyTop = true;
        }
      }
      maxW = Math.max(maxW, W);
    });
    const left = m.align === 'center' ? m.x - maxW / 2 : m.align === 'right' ? m.x - maxW : m.x;
    const top = rubyTop ? m.y - rubySize * 1.1 : m.y;
    box = { x: left, y: top, w: maxW, h: lines.length * lh + (m.y - top) };
  } else if (!m.vertical) {
    ctx.textAlign = m.align;
    let maxW = 0;
    // 자간을 주면 마지막 글자 뒤에도 간격이 붙으므로 그만큼 보정
    const tail = m.align === 'center' ? m.size * track / 2 : m.align === 'right' ? m.size * track : 0;
    lines.forEach((ln, i) => { draw(ln, m.x + tail, m.y + i * lh); maxW = Math.max(maxW, ctx.measureText(ln).width - m.size * track); });
    const left = m.align === 'center' ? m.x - maxW / 2 : m.align === 'right' ? m.x - maxW : m.x;
    box = { x: left, y: m.y, w: maxW, h: lines.length * lh };
  } else {
    ctx.textAlign = 'center';
    const colW = lh, step = m.size * (1.05 + track);
    let maxLen = 0;
    lines.forEach((ln, i) => {
      const cx = m.x - i * colW;
      let j = 0;
      for (const r of parseRuby(ln)) {
        const j0 = j;
        for (const ch of r.text) {
          const y = m.y + j * step;
          if (ROTATE_V.has(ch)) {
            ctx.save();
            ctx.translate(cx, y + m.size / 2);
            ctx.rotate(Math.PI / 2);
            ctx.textBaseline = 'middle';
            draw(ch, 0, 0);
            ctx.restore();
          } else draw(ch, cx, y);
          j += m.kern && PUNCT.has(ch) ? 0.62 : 1;
        }
        if (r.ruby) {
          // 세로쓰기 루비: 글자 오른쪽에 위에서 아래로
          const rc = [...r.ruby], span = (j - j0) * step, rs = Math.min(rubySize * 1.05, span / rc.length);
          ctx.font = rubyFont;
          rc.forEach((ch, k) => draw(ch, cx + m.size * 0.5 + rubySize * 0.6, m.y + j0 * step + (span - rc.length * rs) / 2 + k * rs));
          ctx.font = baseFont;
        }
      }
      maxLen = Math.max(maxLen, j);
    });
    box = { x: m.x - (lines.length - 0.5) * colW, y: m.y, w: lines.length * colW, h: maxLen * step };
  }
  ctx.restore();
  const pad = m.strokeWidth + 4;
  const lb = { x: box.x - pad, y: box.y - pad, w: box.w + pad * 2, h: box.h + pad * 2 };
  layer.meta.lbox = lb;
  const quad = [[lb.x, lb.y], [lb.x + lb.w, lb.y], [lb.x + lb.w, lb.y + lb.h], [lb.x, lb.y + lb.h]]
    .map(([x, y]) => (mat ? mat.transformPoint(new DOMPoint(x, y)) : { x, y })).map(p => ({ x: p.x, y: p.y }));
  layer.meta.quad = quad;
  const xs = quad.map(p => p.x), ys = quad.map(p => p.y);
  layer.meta.box = { x: Math.min(...xs), y: Math.min(...ys), w: Math.max(...xs) - Math.min(...xs), h: Math.max(...ys) - Math.min(...ys) };
  layer.touch();
}

function textLayerAt(x, y) {
  const doc = app.doc;
  const act = doc.active;
  const inBox = (b) => b && x >= b.x && y >= b.y && x <= b.x + b.w && y <= b.y + b.h;
  const hit = (l) => {
    if (!l.visible) return false;
    if (l.kind === 'text') return inBox(l.meta?.box);
    // PSD 에서 가져온 텍스트(아직 그림): 글자 픽셀 위를 탭했는지
    if (l.meta?.psdText && x >= 0 && y >= 0 && x < l.canvas.width && y < l.canvas.height) return l.ctx.getImageData(Math.floor(x), Math.floor(y), 1, 1).data[3] > 0;
    return false;
  };
  if (act && hit(act)) return act;
  return doc.findLayer(hit);
}

/** 텍스트 편집 대화상자. 입력하는 동안 캔버스에 바로 반영 */
export function editTextLayer(layer, isNew) {
  if (layer.meta?.sfx && sfxHooks) { sfxHooks.edit(layer, isNew); return; }
  const m = layer.meta;
  const orig = structuredClone(m);
  const ta = h('textarea.text-input', { rows: 4, placeholder: '대사를 입력하세요' });
  ta.value = m.text;
  const fonts = [...SYSTEM_FONTS, ...(app.fonts || []).map(f => [f, f + ' (내 폰트)'])];
  const ctl = h('div.text-ctl');
  const upd = () => renderText(layer);
  ctl.append(
    select('글꼴', fonts, () => m.font, v => { m.font = v; upd(); }),
    slider('크기', { min: 6, max: 400, step: 1, log: true, get: () => m.size, set: v => { m.size = v; upd(); } }),
    slider('줄 간격', { min: 0.8, max: 2.5, step: 0.05, get: () => m.lineHeight, set: v => { m.lineHeight = v; upd(); }, format: v => v.toFixed(2) }),
    slider('자간', { min: -30, max: 100, step: 1, get: () => m.tracking || 0, set: v => { m.tracking = v; upd(); }, format: v => v + '%' }),
    toggle('문장부호 좁게 (…, !?, 「」 앞뒤를 당김)', () => !!m.kern, v => { m.kern = v; upd(); }),
    h('div.help', null, '루비(읽는 법): {漢字|かんじ} 처럼 쓰면 글자 위(세로쓰기는 오른쪽)에 작게 붙습니다.'),
    row(
      toggle('굵게', () => m.bold, v => { m.bold = v; upd(); }),
      toggle('기울임', () => m.italic, v => { m.italic = v; upd(); }),
      toggle('세로쓰기', () => m.vertical, v => { m.vertical = v; upd(); }),
    ),
    segmented([['left', '왼쪽'], ['center', '가운데'], ['right', '오른쪽']], () => m.align, v => {
      // 박스 위치를 유지하도록 기준점 이동
      const b = m.lbox || m.box;
      if (b && !m.vertical) m.x = v === 'left' ? b.x + m.strokeWidth + 4 : v === 'center' ? b.x + b.w / 2 : b.x + b.w - m.strokeWidth - 4;
      m.align = v; upd();
    }),
    row(colorInput('글자색', () => m.color, v => { m.color = v; upd(); }), colorInput('외곽선', () => m.strokeColor, v => { m.strokeColor = v; upd(); })),
    slider('외곽선 굵기(효과음용)', { min: 0, max: 30, step: 0.5, get: () => m.strokeWidth, set: v => { m.strokeWidth = v; upd(); } }),
  );
  ta.addEventListener('input', () => { m.text = ta.value; upd(); });
  const body = h('div.text-dialog', null, ta, ctl);
  let done = false;
  const finish = (ok) => {
    if (done) return;
    done = true;
    if (!ok || !m.text.trim()) {
      if (isNew) { app.history.undo(); app.history.redoStack.pop(); app.bus.emit('history:changed'); }
      else { layer.meta = orig; renderText(layer); }
      return;
    }
    layer.name = '텍스트: ' + m.text.replace(/\s+/g, ' ').slice(0, 12);
    if (!isNew) {
      const after = structuredClone(layer.meta);
      app.history.push({
        label: '텍스트 편집',
        undo: () => { layer.meta = structuredClone(orig); renderText(layer); app.bus.emit('layers:changed'); },
        redo: () => { layer.meta = structuredClone(after); renderText(layer); app.bus.emit('layers:changed'); },
      });
    } else {
      // 새 레이어: 레이어 추가 기록에 최종 내용이 남도록 다시 그린 상태 유지
      app.markModified();
    }
    app.bus.emit('layers:changed');
  };
  openModal({
    title: isNew ? '텍스트 입력' : '텍스트 편집',
    body, cls: 'text-modal',
    actions: [
      { label: '취소', run: () => finish(false) },
      { label: '확인', primary: true, run: () => finish(true) },
    ],
    onClose: (r) => { if (r === null) finish(false); },
  });
}

app.registerCommand('text.createAt', {
  label: '텍스트 추가',
  run: (x, y, o = {}) => {
    const t = textTool.opt;
    const meta = { ...TEXT_DEFAULTS, ...t, text: '', x, y, align: o.center ? 'center' : t.align };
    const layer = addLayer({ name: '텍스트', kind: 'text', meta }, app.doc.layers.length);
    if (o.center) meta.y = y - meta.size * meta.lineHeight / 2;
    layer.meta = meta;
    renderText(layer);
    editTextLayer(layer, true);
    return layer;
  },
});

/** PSD 에서 가져온 텍스트 레이어(그림)를 편집 가능한 텍스트 레이어로 */
export async function convertPsdText(layer) {
  const t = layer.meta?.psdText;
  if (!t) return;
  const ok = await app.ui.confirm(`PSD 텍스트 "${t.text.slice(0, 24)}" 를 다시 편집할 수 있는 텍스트로 바꿀까요?\n(글꼴이 이 컴퓨터에 없으면 비슷한 글꼴로 보입니다)`, '바꾸기');
  if (!ok) return;
  const meta = {
    ...TEXT_DEFAULTS, text: t.text, size: t.size, color: t.color, font: t.font || 'Malgun Gothic', bold: false,
    align: t.align, vertical: !!t.vertical, lineHeight: 1.2, x: 0, y: -t.size * 0.82, mat: t.mat,
  };
  app.history.batch('PSD 텍스트 변환', () => {
    setLayerProps(layer, { kind: 'text', meta }, '텍스트로 변환');
    app.history.pixels(layer, '텍스트로 변환', () => renderText(layer));
  });
  app.doc.setActive(layer);
  app.bus.emit('layers:changed');
  editTextLayer(layer, false);
}

export const textTool = {
  id: 'text', name: '텍스트', icon: 'text', key: 't', cursor: 'text',
  opt: { font: 'Malgun Gothic', size: 42, color: '#000000', bold: true, vertical: false, align: 'center', strokeWidth: 0, strokeColor: '#ffffff', lineHeight: 1.3 },
  drag: null,

  down(e) {
    const l = textLayerAt(e.x, e.y);
    if (l && l.kind !== 'text') { this.drag = null; convertPsdText(l); return; }
    if (l) {
      app.doc.setActive(l);
      this.drag = { layer: l, x0: e.x, y0: e.y, mx: l.meta.x, my: l.meta.y, mat0: l.meta.mat ? [...l.meta.mat] : null, sx0: e.sx, sy0: e.sy, moved: false, orig: structuredClone(l.meta) };
    } else {
      this.drag = { create: true, x: e.x, y: e.y };
    }
    this.busy = true;
  },

  move(e) {
    const d = this.drag;
    if (!d || !d.layer) return;
    if (Math.hypot(e.sx - d.sx0, e.sy - d.sy0) > 5) d.moved = true;
    if (d.moved) {
      if (d.mat0) { d.layer.meta.mat[4] = d.mat0[4] + e.x - d.x0; d.layer.meta.mat[5] = d.mat0[5] + e.y - d.y0; }
      else { d.layer.meta.x = d.mx + e.x - d.x0; d.layer.meta.y = d.my + e.y - d.y0; }
      renderText(d.layer);
    }
  },

  up() {
    const d = this.drag;
    this.drag = null;
    this.busy = false;
    if (!d) return;
    if (d.create) { app.run('text.createAt', d.x, d.y); return; }
    if (!d.moved) { editTextLayer(d.layer, false); return; }
    const l = d.layer, before = d.orig, after = structuredClone(l.meta);
    app.history.push({
      label: '텍스트 이동',
      undo: () => { l.meta = structuredClone(before); renderText(l); },
      redo: () => { l.meta = structuredClone(after); renderText(l); },
    });
  },

  cancel() { this.drag = null; this.busy = false; },

  overlay(ctx, view) {
    for (const l of app.doc.layers) {
      if (l.kind !== 'text' || !l.visible || !l.meta?.box) continue;
      const b = l.meta.box;
      const pts = (l.meta.quad || [{ x: b.x, y: b.y }, { x: b.x + b.w, y: b.y }, { x: b.x + b.w, y: b.y + b.h }, { x: b.x, y: b.y + b.h }]).map(p => view.toScreen(p.x, p.y));
      ctx.strokeStyle = l === app.doc.active ? '#2f7cf6' : 'rgba(47,124,246,0.4)';
      ctx.setLineDash([4, 3]);
      ctx.beginPath(); pts.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y))); ctx.closePath(); ctx.stroke();
    }
  },

  options(el) {
    const o = this.opt;
    const fonts = [...SYSTEM_FONTS, ...(app.fonts || []).map(f => [f, f + ' (내 폰트)'])];
    el.append(section('새 텍스트 기본값',
      select('글꼴', fonts, () => o.font, v => { o.font = v; }),
      slider('크기', { min: 6, max: 400, step: 1, log: true, get: () => o.size, set: v => { o.size = v; } }),
      row(toggle('굵게', () => o.bold, v => { o.bold = v; }), toggle('세로쓰기', () => o.vertical, v => { o.vertical = v; })),
      colorInput('글자색', () => o.color, v => { o.color = v; }),
      slider('외곽선', { min: 0, max: 30, step: 0.5, get: () => o.strokeWidth, set: v => { o.strokeWidth = v; } }),
      helpText('빈 곳 탭: 새 텍스트 · 텍스트 탭: 편집 · 텍스트 드래그: 이동. data/fonts 폴더에 폰트 파일(.ttf/.otf)을 넣으면 목록에 나타납니다.'),
    ));
  },
};
