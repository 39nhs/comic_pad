// 원근·격자 왜곡 (여러 면) / 퍼펫 왜곡 (핀을 끌어 휘기)
import { app } from '../app.js';
import { createCanvas } from '../core/util.js';
import { section, segmented, toggle, button, row } from '../ui/controls.js';
import { helpText } from './basic.js';
import { addIcon } from '../ui/icons.js';
import { checkEditable } from '../core/ops.js';
import { alphaBounds } from '../core/raster.js';

const contentBounds = (owner) => (owner?.target ? alphaBounds(owner.target.canvas) : null);

addIcon('meshwarp', '<path d="M4 5l7 2 9-3v15l-9 2-7-3z"/><path d="M11 7v14M4 12l7 1 9-1"/>');
addIcon('puppet', '<circle cx="12" cy="5" r="2"/><path d="M12 7v7M7 10l5 2 5-2M9 21l3-7 3 7"/><circle cx="7" cy="10" r="1.3"/><circle cx="17" cy="10" r="1.3"/>');

/** 삼각형 하나를 원본(s) → 대상(d) 으로 아핀 변환해 그리기 */
export function drawTri(ctx, img, s, d) {
  const [[x0, y0], [x1, y1], [x2, y2]] = s, [[u0, v0], [u1, v1], [u2, v2]] = d;
  const den = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
  if (Math.abs(den) < 1e-9) return;
  const a = ((u1 - u0) * (y2 - y0) - (u2 - u0) * (y1 - y0)) / den, c = ((u2 - u0) * (x1 - x0) - (u1 - u0) * (x2 - x0)) / den;
  const b = ((v1 - v0) * (y2 - y0) - (v2 - v0) * (y1 - y0)) / den, dd = ((v2 - v0) * (x1 - x0) - (v1 - v0) * (x2 - x0)) / den;
  const e = u0 - a * x0 - c * y0, f = v0 - b * x0 - dd * y0;
  // 이음매가 보이지 않게 대상 삼각형을 아주 조금 키워서 자르기
  const cx = (u0 + u1 + u2) / 3, cy = (v0 + v1 + v2) / 3, g = (x, y) => { const L = Math.hypot(x - cx, y - cy) || 1; return [x + (x - cx) / L * 0.7, y + (y - cy) / L * 0.7]; };
  ctx.save();
  ctx.beginPath(); ctx.moveTo(...g(u0, v0)); ctx.lineTo(...g(u1, v1)); ctx.lineTo(...g(u2, v2)); ctx.closePath(); ctx.clip();
  ctx.transform(a, b, c, dd, e, f);
  const sx = Math.max(0, Math.floor(Math.min(x0, x1, x2)) - 2), sy = Math.max(0, Math.floor(Math.min(y0, y1, y2)) - 2);
  const sw = Math.min(img.width - sx, Math.ceil(Math.max(x0, x1, x2)) + 2 - sx), sh = Math.min(img.height - sy, Math.ceil(Math.max(y0, y1, y2)) + 2 - sy);
  if (sw > 0 && sh > 0) ctx.drawImage(img, sx, sy, sw, sh, sx, sy, sw, sh);
  ctx.restore();
}

/** 단위 정사각형 → 사각형 네 점 (원근 변환, Heckbert) */
function squareToQuad(q) {
  const [[x0, y0], [x1, y1], [x2, y2], [x3, y3]] = q;   // 왼위, 오른위, 오른아래, 왼아래
  const dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2, sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
  let g = 0, h = 0;
  if (Math.abs(sx) > 1e-9 || Math.abs(sy) > 1e-9) { const den = dx1 * dy2 - dx2 * dy1; g = (sx * dy2 - dx2 * sy) / den; h = (dx1 * sy - sx * dy1) / den; }
  const a = x1 - x0 + g * x1, b = x3 - x0 + h * x3, c = x0, d = y1 - y0 + g * y1, e = y3 - y0 + h * y3, f = y0;
  return (u, v) => { const w = g * u + h * v + 1; return [(a * u + b * v + c) / w, (d * u + e * v + f) / w]; };
}

/** 한 레이어를 고치는 동안의 공통 상태: 시작 모습을 들고 있다가 매번 다시 그림 */
function session(tool) {
  const layer = app.doc.active?.target;
  if (!checkEditable(layer)) return null;
  const b = contentBounds(app.doc.active) || { x: 0, y: 0, w: app.doc.width, h: app.doc.height };
  if (!b.w || !b.h) { app.toast('레이어에 그림이 없습니다'); return null; }
  const src = createCanvas(layer.canvas.width, layer.canvas.height);
  src.getContext('2d').drawImage(layer.canvas, 0, 0);
  return { tool, layer, src, b, token: app.history.beginPixels(layer), drawn: { ...b } };
}

function redraw(S, tris) {
  const ctx = S.layer.ctx;
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const [, d] of tris) for (const [x, y] of d) { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
  const now = { x: Math.floor(x0) - 2, y: Math.floor(y0) - 2, w: Math.ceil(x1 - x0) + 4, h: Math.ceil(y1 - y0) + 4 };
  // 그림은 모두 S.b 안에 있으므로(내용 경계), 지난번 자리·원래 자리·새 자리를 비우고 왜곡한 그림만 다시
  ctx.save();
  for (const r of [S.drawn, S.b, now]) ctx.clearRect(r.x, r.y, r.w, r.h);
  for (const [s, d] of tris) drawTri(ctx, S.src, s, d);
  ctx.restore();
  const u = (a, c) => { const X = Math.min(a.x, c.x), Y = Math.min(a.y, c.y); return { x: X, y: Y, w: Math.max(a.x + a.w, c.x + c.w) - X, h: Math.max(a.y + a.h, c.y + c.h) - Y }; };
  S.dirty = u(S.dirty || S.b, u(S.drawn, now));
  S.drawn = now;
  S.layer.touch();
  app.requestRender();
}

function finish(S, apply, label) {
  if (!S) return;
  if (apply) { S.token.commit(S.dirty || S.b, label); S.layer.touch(); }
  else S.token.cancel();
  app.requestRender();
}

const queueDraw = (tool) => {
  if (tool._q) return;
  tool._q = true;
  // 창이 가려져 rAF 가 안 오는 경우도 있으니 시간 제한도 함께
  const go = () => { if (!tool._q) return; tool._q = false; tool._render(); };
  requestAnimationFrame(go); setTimeout(go, 60);
};

// =====================================================================
// 원근·격자 왜곡
// =====================================================================
const GRIDS = { '1x1': [1, 1], '2x1': [2, 1], '1x2': [1, 2], '3x3': [3, 3], '4x4': [4, 4] };
export const meshWarpTool = {
  id: 'meshwarp', name: '원근·격자 왜곡', icon: 'meshwarp', key: null, cursor: 'default',
  opt: { grid: '2x1', showGrid: true },
  S: null, P: null, drag: null,
  get busy() { return !!this.drag; },

  _begin() {
    const S = session(this);
    if (!S) return false;
    const [C, Rw] = GRIDS[this.opt.grid];
    const b = S.b;
    this.S = S; this.C = C; this.Rw = Rw;
    this.P = [];
    for (let j = 0; j <= Rw; j++) for (let i = 0; i <= C; i++) this.P.push([b.x + b.w * i / C, b.y + b.h * j / Rw]);
    app.requestRender();
    return true;
  },
  activate() { this.S = null; this._hint = undefined; },
  deactivate() { if (this.S) this.apply(); },
  apply() { finish(this.S, true, '원근·격자 왜곡'); this.S = null; this.P = null; this._hint = undefined; },
  cancelWarp() { finish(this.S, false); this.S = null; this.P = null; },

  _render() {
    const { S, P, C, Rw } = this;
    if (!S) return;
    const b = S.b, N = Math.max(4, Math.round(24 / Math.max(C, Rw))), tris = [];
    for (let j = 0; j < Rw; j++) for (let i = 0; i < C; i++) {
      const k = (jj, ii) => P[jj * (C + 1) + ii];
      const H = squareToQuad([k(j, i), k(j, i + 1), k(j + 1, i + 1), k(j + 1, i)]);
      const sx0 = b.x + b.w * i / C, sy0 = b.y + b.h * j / Rw, cw = b.w / C, ch = b.h / Rw;
      for (let v = 0; v < N; v++) for (let u = 0; u < N; u++) {
        const s = (uu, vv) => [sx0 + cw * uu / N, sy0 + ch * vv / N], d = (uu, vv) => H(uu / N, vv / N);
        tris.push([[s(u, v), s(u + 1, v), s(u + 1, v + 1)], [d(u, v), d(u + 1, v), d(u + 1, v + 1)]]);
        tris.push([[s(u, v), s(u + 1, v + 1), s(u, v + 1)], [d(u, v), d(u + 1, v + 1), d(u, v + 1)]]);
      }
    }
    redraw(S, tris);
  },

  down(e) {
    if (!this.S && !this._begin()) return;
    const v = app.view;
    let best = -1, bd = 14;
    this.P.forEach(([x, y], i) => { const s = v.toScreen(x, y), d = Math.hypot(s.x - e.sx, s.y - e.sy); if (d < bd) { bd = d; best = i; } });
    if (best < 0) return;
    this.drag = { i: best, x0: e.x, y0: e.y, p0: [...this.P[best]], all: e.shift ? this.P.map(p => [...p]) : null };
  },
  move(e) {
    const d = this.drag;
    if (!d) return;
    const dx = e.x - d.x0, dy = e.y - d.y0;
    if (d.all) this.P = d.all.map(([x, y]) => [x + dx, y + dy]);
    else this.P[d.i] = [d.p0[0] + dx, d.p0[1] + dy];
    queueDraw(this);
  },
  up() { this.drag = null; },
  cancel() { this.drag = null; },
  onKey(e) { if (e.key === 'Enter' && this.S) { this.apply(); return true; } if (e.key === 'Escape' && this.S) { this.cancelWarp(); return true; } return false; },

  overlay(ctx, view) {
    if (!this.P) {
      const b = (this._hint ??= contentBounds(app.doc.active) || 0) || null;
      if (b) { const a = view.toScreen(b.x, b.y), c = view.toScreen(b.x + b.w, b.y + b.h); ctx.setLineDash([5, 4]); ctx.strokeStyle = '#2f7cf6'; ctx.strokeRect(a.x, a.y, c.x - a.x, c.y - a.y); ctx.setLineDash([]); }
      return;
    }
    const { P, C, Rw } = this, S2 = P.map(([x, y]) => view.toScreen(x, y));
    if (this.opt.showGrid) {
      ctx.strokeStyle = 'rgba(47,124,246,.9)'; ctx.lineWidth = 1.2;
      for (let j = 0; j <= Rw; j++) { ctx.beginPath(); for (let i = 0; i <= C; i++) { const s = S2[j * (C + 1) + i]; i ? ctx.lineTo(s.x, s.y) : ctx.moveTo(s.x, s.y); } ctx.stroke(); }
      for (let i = 0; i <= C; i++) { ctx.beginPath(); for (let j = 0; j <= Rw; j++) { const s = S2[j * (C + 1) + i]; j ? ctx.lineTo(s.x, s.y) : ctx.moveTo(s.x, s.y); } ctx.stroke(); }
    }
    for (const s of S2) { ctx.fillStyle = '#fff'; ctx.strokeStyle = '#2f7cf6'; ctx.beginPath(); ctx.arc(s.x, s.y, 6, 0, Math.PI * 2); ctx.fill(); ctx.stroke(); }
  },

  options(el) {
    const o = this.opt;
    el.append(section('원근·격자 왜곡',
      segmented([['1x1', '한 면'], ['2x1', '두 면 (가로)'], ['1x2', '두 면 (세로)'], ['3x3', '격자 3×3'], ['4x4', '격자 4×4']], () => o.grid, v => { if (this.S) this.cancelWarp(); o.grid = v; }),
      toggle('격자선 보이기', () => o.showGrid, v => { o.showGrid = v; app.requestRender(); }),
      row(button('적용 (Enter)', () => this.S && this.apply()), button('취소 (Esc)', () => this.S && this.cancelWarp())),
      helpText('지금 레이어의 그림 둘레에 면이 생깁니다. 점을 끌어 모양을 맞추세요 (Shift+끌기 = 전체 이동). 두 면은 건물 모서리처럼 꺾인 벽에, 격자는 천·종이 휘기에. 도구를 바꾸면 자동 적용.'),
    ));
  },
};

// =====================================================================
// 퍼펫 왜곡 (MLS 강체 변형, Schaefer 외 2006)
// =====================================================================
function mlsRigid(pins, x, y) {
  if (!pins.length) return [x, y];
  let sw = 0, px = 0, py = 0, qx = 0, qy = 0;
  const w = pins.map(p => { const d2 = (p.x0 - x) ** 2 + (p.y0 - y) ** 2; return d2 < 1e-6 ? 1e6 : 1 / d2; });
  pins.forEach((p, i) => { sw += w[i]; px += w[i] * p.x0; py += w[i] * p.y0; qx += w[i] * p.x; qy += w[i] * p.y; });
  px /= sw; py /= sw; qx /= sw; qy /= sw;
  const dx = x - px, dy = y - py;
  if (pins.length === 1) return [dx + qx, dy + qy];
  // 복소수로: f = |d| · normalize(Σ w q̂ conj(p̂) d) + q*
  let fr = 0, fi = 0;
  pins.forEach((p, i) => {
    const ax = p.x0 - px, ay = p.y0 - py, bx = p.x - qx, by = p.y - qy;
    const cr = bx * ax + by * ay, ci = by * ax - bx * ay;       // q̂ · conj(p̂)
    fr += w[i] * (cr * dx - ci * dy); fi += w[i] * (cr * dy + ci * dx);
  });
  const L = Math.hypot(fr, fi), dl = Math.hypot(dx, dy);
  if (L < 1e-9) return [dx + qx, dy + qy];
  return [fr / L * dl + qx, fi / L * dl + qy];
}

export const puppetTool = {
  id: 'puppet', name: '퍼펫 왜곡', icon: 'puppet', key: null, cursor: 'crosshair',
  opt: { density: 24, showMesh: true },
  S: null, pins: [], drag: null,
  get busy() { return !!this.drag; },

  _begin() {
    const S = session(this);
    if (!S) return false;
    this.S = S; this.pins = [];
    const b = S.b, n = this.opt.density, step = Math.max(8, Math.max(b.w, b.h) / n);
    this.cols = Math.max(1, Math.ceil(b.w / step)); this.rows = Math.max(1, Math.ceil(b.h / step));
    this.V = [];
    for (let j = 0; j <= this.rows; j++) for (let i = 0; i <= this.cols; i++) this.V.push([b.x + b.w * i / this.cols, b.y + b.h * j / this.rows]);
    return true;
  },
  activate() { this.S = null; this._hint = undefined; },
  deactivate() { if (this.S) this.apply(); },
  apply() { finish(this.S, true, '퍼펫 왜곡'); this.S = null; this.pins = []; this._hint = undefined; },
  cancelWarp() { finish(this.S, false); this.S = null; this.pins = []; },

  _deformed() { return this.V.map(([x, y]) => mlsRigid(this.pins, x, y)); },
  _render() {
    if (!this.S) return;
    const D = this._deformed(), V = this.V, C = this.cols, tris = [];
    for (let j = 0; j < this.rows; j++) for (let i = 0; i < C; i++) {
      const a = j * (C + 1) + i, b = a + 1, c = a + C + 2, d = a + C + 1;
      tris.push([[V[a], V[b], V[c]], [D[a], D[b], D[c]]], [[V[a], V[c], V[d]], [D[a], D[c], D[d]]]);
    }
    redraw(this.S, tris);
  },

  down(e) {
    if (!this.S && !this._begin()) return;
    const v = app.view;
    const hit = this.pins.findIndex(p => { const s = v.toScreen(p.x, p.y); return Math.hypot(s.x - e.sx, s.y - e.sy) < 12; });
    if (hit >= 0 && e.alt) { this.pins.splice(hit, 1); queueDraw(this); return; }
    if (hit >= 0) { this.drag = { i: hit }; return; }
    const b = this.S.b;
    if (e.x < b.x || e.y < b.y || e.x > b.x + b.w || e.y > b.y + b.h) { app.toast('핀은 그림 안쪽에 꽂으세요'); return; }
    // 이미 휜 상태에서 꽂으면, 지금 보이는 자리의 원래 위치를 거꾸로 찾아 둠 (가까운 격자점 기준)
    let x0 = e.x, y0 = e.y;
    if (this.pins.length) {
      const D = this._deformed();
      let bi = 0, bd = Infinity;
      D.forEach(([x, y], i) => { const d = (x - e.x) ** 2 + (y - e.y) ** 2; if (d < bd) { bd = d; bi = i; } });
      x0 = this.V[bi][0] + (e.x - D[bi][0]); y0 = this.V[bi][1] + (e.y - D[bi][1]);
    }
    this.pins.push({ x0, y0, x: e.x, y: e.y });
    this.drag = { i: this.pins.length - 1 };
    app.requestRender();
  },
  move(e) {
    const d = this.drag;
    if (!d) return;
    const p = this.pins[d.i];
    p.x = e.x; p.y = e.y;
    queueDraw(this);
  },
  up() { this.drag = null; },
  cancel() { this.drag = null; },
  onKey(e) { if (e.key === 'Enter' && this.S) { this.apply(); return true; } if (e.key === 'Escape' && this.S) { this.cancelWarp(); return true; } return false; },

  overlay(ctx, view) {
    if (!this.S) {
      const b = (this._hint ??= contentBounds(app.doc.active) || 0) || null;
      if (b) { const a = view.toScreen(b.x, b.y), c = view.toScreen(b.x + b.w, b.y + b.h); ctx.setLineDash([5, 4]); ctx.strokeStyle = '#e8a33d'; ctx.strokeRect(a.x, a.y, c.x - a.x, c.y - a.y); ctx.setLineDash([]); }
      return;
    }
    if (this.opt.showMesh) {
      const D = this._deformed().map(([x, y]) => view.toScreen(x, y)), C = this.cols;
      ctx.strokeStyle = 'rgba(232,163,61,.35)'; ctx.lineWidth = 1;
      ctx.beginPath();
      for (let j = 0; j <= this.rows; j++) for (let i = 0; i <= C; i++) {
        const a = D[j * (C + 1) + i];
        if (i < C) { const b = D[j * (C + 1) + i + 1]; ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); }
        if (j < this.rows) { const b = D[(j + 1) * (C + 1) + i]; ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); }
      }
      ctx.stroke();
    }
    for (const p of this.pins) {
      const s = view.toScreen(p.x, p.y);
      ctx.fillStyle = '#e8a33d'; ctx.strokeStyle = '#fff'; ctx.lineWidth = 2;
      ctx.beginPath(); ctx.arc(s.x, s.y, 7, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
    }
  },

  options(el) {
    const o = this.opt;
    el.append(section('퍼펫 왜곡',
      segmented([['12', '거칠게 (빠름)'], ['24', '보통'], ['40', '촘촘히']], () => String(o.density), v => { if (this.S) this.cancelWarp(); o.density = +v; }),
      toggle('격자 보이기', () => o.showMesh, v => { o.showMesh = v; app.requestRender(); }),
      row(button('적용 (Enter)', () => this.S && this.apply()), button('취소 (Esc)', () => this.S && this.cancelWarp())),
      helpText('그림 위를 탭해 핀을 꽂고(관절·고정할 곳), 핀을 끌면 나머지가 자연스럽게 따라 휩니다. Alt+탭 = 핀 빼기. 도구를 바꾸면 자동 적용.'),
    ));
  },
};

export { mlsRigid };
