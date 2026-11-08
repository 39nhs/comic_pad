// 이동/변형: 선택 영역(없으면 레이어 전체)을 떼어 내 이동·확대·회전·반전
import { app } from '../app.js';
import { checkEditable, setLayerProps } from '../core/ops.js';
import { renderText, renderPanels, panelPts } from './comic.js';
import { renderVector, transformStrokes } from '../core/vector.js';
import { alphaBounds } from '../core/raster.js';
import { createCanvas, rectUnion } from '../core/util.js';
import { slider, toggle, row, button, section, segmented } from '../ui/controls.js';
import { showBar, hideBar } from '../ui/bars.js';
import { icon } from '../ui/icons.js';
import { helpText } from './basic.js';

const HANDLES = [
  // [이름, 손잡이 위치(로컬 0~1), 기준점 위치]
  ['nw', 0, 0, 1, 1], ['ne', 1, 0, 0, 1], ['se', 1, 1, 0, 0], ['sw', 0, 1, 1, 0],
  ['n', 0.5, 0, 0.5, 1], ['e', 1, 0.5, 0, 0.5], ['s', 0.5, 1, 0.5, 0], ['w', 0, 0.5, 1, 0.5],
];

export const transformTool = {
  id: 'transform', name: '이동/변형', icon: 'transform', key: 'v', cursor: 'move', allowAltPick: false,
  opt: { keepAspect: true, mode: 'affine' },
  st: null,
  drag: null,

  activate() {
    // 도구를 고르면 자동 시작. 같은 틱에 명령이 직접 start() 했거나 다른 도구로 바뀌었으면 건너뜀
    this._autoStart = true;
    setTimeout(() => { if (this._autoStart && app.toolId === 'transform') this.start(); this._autoStart = false; }, 0);
  },
  deactivate() { this.commit(); },
  commitPending() { this.commit(); },

  get busy() { return !!this.drag; },

  start() {
    this._autoStart = false;
    if (this.st) return;
    const doc = app.doc;
    const layer = doc.active;
    if (!layer || !checkEditable(layer, { vector: true })) return;
    const sel = doc.selection;
    const b = sel.active ? sel.bounds : alphaBounds(layer.canvas);
    if (!b) { app.toast('변형할 그림이 없습니다'); return; }
    // 텍스트/컷 레이어는 글자·컷 정보 자체를 변형해서 계속 편집 가능하게 둔다 (선택 영역 일부만 변형할 때는 그림으로)
    let metaMode = (layer.kind === 'text' || layer.kind === 'panel' || layer.kind === 'vector') && !sel.active;
    if ((layer.kind === 'text' || layer.kind === 'panel' || layer.kind === 'vector') && sel.active) {
      setLayerProps(layer, { kind: 'raster', meta: null }, '일반 레이어로 변환');
      app.toast('선택 영역만 변형하므로 일반 그림 레이어로 바꿨습니다');
    }
    const token = app.history.beginPixels(layer);
    const fl = createCanvas(b.w, b.h);
    const fx = fl.getContext('2d');
    fx.drawImage(layer.canvas, b.x, b.y, b.w, b.h, 0, 0, b.w, b.h);
    const ctx = layer.ctx;
    if (sel.active) {
      fx.globalCompositeOperation = 'destination-in';
      fx.drawImage(sel.mask, b.x, b.y, b.w, b.h, 0, 0, b.w, b.h);
      ctx.save();
      ctx.globalCompositeOperation = 'destination-out';
      ctx.drawImage(sel.mask, b.x, b.y, b.w, b.h, b.x, b.y, b.w, b.h);
      ctx.restore();
    } else {
      ctx.clearRect(b.x, b.y, b.w, b.h);
    }
    layer.touch();
    this.st = {
      layer, token, canvas: fl, w: b.w, h: b.h, orig: { ...b },
      cx: b.x + b.w / 2, cy: b.y + b.h / 2, sx: 1, sy: 1, rot: 0,
      selSnap: sel.active ? sel.snapshot() : null,
      metaMode, prevMeta: metaMode ? structuredClone(layer.meta) : null,
    };
    if (sel.active) sel.clear(true);
    app.floating = { layer, canvas: fl, matrix: this.matrix() };
    showBar('transform', [
      { icon: 'check', label: '확정', run: () => this.commit(), cls: 'primary' },
      { icon: 'x', label: '취소', run: () => this.cancel() },
      { icon: 'flip', label: '좌우', run: () => this.flip('h') },
      { icon: 'flipV', label: '상하', run: () => this.flip('v') },
      { icon: 'rotate', label: '90°', run: () => this.rotate90() },
      { icon: 'transform', label: '자유 변형', run: () => this.setMode(this.st?.corners ? 'affine' : 'distort') },
    ]);
    if (this.opt.mode === 'distort') this.setMode('distort');
    app.bus.emit('transform:changed', this.st);
    app.requestRender();
  },

  matrix() {
    const s = this.st;
    return new DOMMatrix().translate(s.cx, s.cy).rotate(s.rot).scale(s.sx, s.sy).translate(-s.w / 2, -s.h / 2);
  },

  /** affine(이동/크기/회전) <-> distort(네 모서리 자유 변형) */
  setMode(mode) {
    this.opt.mode = mode;
    const s = this.st;
    if (s) {
      if (mode === 'distort' && !s.corners) s.corners = this.corners().map(p => ({ x: p.x, y: p.y }));
      else if (mode === 'affine' && s.corners) { this.commit(); this.start(); return; }
      this._update();
    }
    app.bus.emit('transform:mode', mode);
  },

  _warp() {
    const s = this.st, q = s.corners;
    const xs = q.map(p => p.x), ys = q.map(p => p.y);
    const bx = Math.floor(Math.min(...xs)) - 1, by = Math.floor(Math.min(...ys)) - 1;
    const bw = Math.min(16000, Math.ceil(Math.max(...xs)) + 2 - bx), bh = Math.min(16000, Math.ceil(Math.max(...ys)) + 2 - by);
    const out = createCanvas(bw, bh), ctx = out.getContext('2d');
    const qm = this._quadMap();
    const map = (u, v) => { const p = qm(u, v); return { x: p.x - bx, y: p.y - by }; };
    const N = 12, W = s.w, H = s.h;
    const tri = (s0, s1, s2, d0, d1, d2) => {
      const dn = (s1.x - s0.x) * (s2.y - s0.y) - (s2.x - s0.x) * (s1.y - s0.y);
      if (Math.abs(dn) < 1e-9) return;
      const a = ((d1.x - d0.x) * (s2.y - s0.y) - (d2.x - d0.x) * (s1.y - s0.y)) / dn;
      const c = ((d2.x - d0.x) * (s1.x - s0.x) - (d1.x - d0.x) * (s2.x - s0.x)) / dn;
      const b = ((d1.y - d0.y) * (s2.y - s0.y) - (d2.y - d0.y) * (s1.y - s0.y)) / dn;
      const d = ((d2.y - d0.y) * (s1.x - s0.x) - (d1.y - d0.y) * (s2.x - s0.x)) / dn;
      const e = d0.x - a * s0.x - c * s0.y, f = d0.y - b * s0.x - d * s0.y;
      const cx = (d0.x + d1.x + d2.x) / 3, cy = (d0.y + d1.y + d2.y) / 3;
      const grow = (p) => { const L = Math.hypot(p.x - cx, p.y - cy) || 1; return { x: p.x + (p.x - cx) / L * 0.7, y: p.y + (p.y - cy) / L * 0.7 }; };
      const [g0, g1, g2] = [grow(d0), grow(d1), grow(d2)];
      ctx.save();
      ctx.beginPath(); ctx.moveTo(g0.x, g0.y); ctx.lineTo(g1.x, g1.y); ctx.lineTo(g2.x, g2.y); ctx.closePath(); ctx.clip();
      ctx.setTransform(a, b, c, d, e, f);
      ctx.drawImage(s.canvas, 0, 0);
      ctx.restore();
    };
    for (let j = 0; j < N; j++) for (let i = 0; i < N; i++) {
      const u0 = i / N, u1 = (i + 1) / N, v0 = j / N, v1 = (j + 1) / N;
      const S = [{ x: u0 * W, y: v0 * H }, { x: u1 * W, y: v0 * H }, { x: u1 * W, y: v1 * H }, { x: u0 * W, y: v1 * H }];
      const T = [map(u0, v0), map(u1, v0), map(u1, v1), map(u0, v1)];
      tri(S[0], S[1], S[2], T[0], T[1], T[2]);
      tri(S[0], S[2], S[3], T[0], T[2], T[3]);
    }
    return { canvas: out, x: bx, y: by };
  },

  /** 단위 사각형 (u,v) -> 네 모서리 사각형 투영 변환 (Heckbert) */
  _quadMap() {
    const [p0, p1, p2, p3] = this.st.corners;
    const dx1 = p1.x - p2.x, dx2 = p3.x - p2.x, dx3 = p0.x - p1.x + p2.x - p3.x;
    const dy1 = p1.y - p2.y, dy2 = p3.y - p2.y, dy3 = p0.y - p1.y + p2.y - p3.y;
    let g = 0, h = 0;
    const den = dx1 * dy2 - dx2 * dy1;
    if ((Math.abs(dx3) > 1e-6 || Math.abs(dy3) > 1e-6) && Math.abs(den) > 1e-9) {
      g = (dx3 * dy2 - dx2 * dy3) / den; h = (dx1 * dy3 - dx3 * dy1) / den;
    }
    const A = p1.x - p0.x + g * p1.x, B = p3.x - p0.x + h * p3.x, C = p0.x;
    const D = p1.y - p0.y + g * p1.y, E = p3.y - p0.y + h * p3.y, F = p0.y;
    return (u, v) => { const w = g * u + h * v + 1; return { x: (A * u + B * v + C) / w, y: (D * u + E * v + F) / w }; };
  },

  _update() {
    if (!this.st) return;
    if (this.st.corners) {
      const w = this._warp();
      app.floating.canvas = w.canvas;
      app.floating.matrix = new DOMMatrix().translate(w.x, w.y);
      this.st.warped = w;
      app.bus.emit('transform:changed', this.st);
      app.requestRender();
      return;
    }
    app.floating.matrix = this.matrix();
    app.bus.emit('transform:changed', this.st);
    app.requestRender();
  },

  flip(axis) {
    const s = this.st;
    if (!s) return;
    if (s.corners) { const [a, b, c, d] = s.corners; s.corners = axis === 'h' ? [b, a, d, c] : [d, c, b, a]; }
    else if (axis === 'h') s.sx *= -1; else s.sy *= -1;
    this._update();
  },
  rotate90() {
    const s = this.st;
    if (!s) return;
    if (s.corners) { const [a, b, c, d] = s.corners; s.corners = [d, a, b, c]; }
    else s.rot = (s.rot + 90) % 360;
    this._update();
  },

  /** 텍스트/컷 레이어: 정보(글자 행렬, 컷 꼭짓점)를 변형하고 다시 그린다 */
  _commitMeta() {
    const s = this.st, layer = s.layer;
    const toDoc = s.corners
      ? (() => { const qm = this._quadMap(); return (p) => qm((p.x - s.orig.x) / s.w, (p.y - s.orig.y) / s.h); })()
      : (() => { const M = this.matrix().multiply(new DOMMatrix().translate(-s.orig.x, -s.orig.y)); return (p) => { const q = M.transformPoint(new DOMPoint(p.x, p.y)); return { x: q.x, y: q.y }; }; })();
    const next = structuredClone(s.prevMeta);
    if (layer.kind === 'panel') {
      next.panels = next.panels.map(p => ({ pts: panelPts(p).map(toDoc) }));
    } else if (layer.kind === 'vector') {
      const M = this.matrix();
      const k = s.corners ? 1 : Math.sqrt(Math.abs(M.a * M.d - M.b * M.c));
      next.strokes = transformStrokes(next.strokes || [], toDoc, k);
    } else {
      if (s.corners) return false;   // 글자를 원근 왜곡하면 그림으로 처리
      const A = this.matrix().multiply(new DOMMatrix().translate(-s.orig.x, -s.orig.y));
      const old = next.mat ? new DOMMatrix(next.mat) : new DOMMatrix();
      const m = A.multiply(old);
      next.mat = [m.a, m.b, m.c, m.d, m.e, m.f];
    }
    s.token.commit(null);
    const prev = s.prevMeta;
    const apply = (meta) => { layer.meta = structuredClone(meta); if (layer.kind === 'panel') renderPanels(layer); else if (layer.kind === 'vector') renderVector(layer); else renderText(layer); };
    apply(next);
    app.history.push({ label: '변형', undo: () => apply(prev), redo: () => apply(next) });
    this._end();
    return true;
  },

  commit() {
    const s = this.st;
    if (!s) return;
    if (s.metaMode) {
      if (this._commitMeta()) return;
      // 글자 원근 변형: 일반 레이어 변환 + 그림 변형을 실행취소 한 번에 되돌리도록 묶음
      app.history.batch('변형', () => {
        setLayerProps(s.layer, { kind: 'raster', meta: null }, '일반 레이어로 변환');
        this._commitPixels(s);
      });
      app.toast('글자를 원근 변형해서 일반 그림 레이어가 되었습니다');
      this._end();
      return;
    }
    this._commitPixels(s);
    this._end();
  },

  _commitPixels(s) {
    const ctx = s.layer.ctx;
    ctx.save();
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';
    if (s.corners) {
      const w = s.warped || this._warp();
      ctx.drawImage(w.canvas, w.x, w.y);
    } else {
      ctx.setTransform(this.matrix());
      ctx.drawImage(s.canvas, 0, 0);
    }
    ctx.restore();
    const pts = this.corners();
    const xs = pts.map(p => p.x), ys = pts.map(p => p.y);
    const nb = { x: Math.floor(Math.min(...xs)) - 2, y: Math.floor(Math.min(...ys)) - 2, w: 0, h: 0 };
    nb.w = Math.ceil(Math.max(...xs)) + 2 - nb.x; nb.h = Math.ceil(Math.max(...ys)) + 2 - nb.y;
    s.token.commit(rectUnion(s.orig, nb), '변형');
    s.layer.touch();
  },

  cancel() {
    const s = this.st;
    if (!s) return;
    s.token.cancel();
    if (s.selSnap) app.doc.selection.restore(s.selSnap);
    this._end();
  },

  _end() {
    this.st = null;
    this.drag = null;
    app.floating = null;
    hideBar('transform');
    app.bus.emit('transform:changed', null);
    app.requestRender();
  },

  corners() {
    if (this.st.corners) return this.st.corners;
    const m = this.matrix(), s = this.st;
    return [[0, 0], [s.w, 0], [s.w, s.h], [0, s.h]].map(([x, y]) => m.transformPoint(new DOMPoint(x, y)));
  },

  _handlePos(view) {
    if (this.st.corners) return this.st.corners.map((p, i) => ({ n: 'c' + i, i, ...view.toScreen(p.x, p.y) }));
    const m = this.matrix(), s = this.st;
    const out = HANDLES.map(([n, hx, hy]) => {
      const p = m.transformPoint(new DOMPoint(hx * s.w, hy * s.h));
      return { n, ...view.toScreen(p.x, p.y) };
    });
    // 회전 손잡이: 위쪽 가운데에서 화면 기준 36px 바깥
    const top = out.find(o => o.n === 'n'), c = view.toScreen(s.cx, s.cy);
    const dx = top.x - c.x, dy = top.y - c.y, L = Math.hypot(dx, dy) || 1;
    out.push({ n: 'rot', x: top.x + dx / L * 36, y: top.y + dy / L * 36 });
    return out;
  },

  _inside(e) {
    if (this.st.corners) {
      const q = this.st.corners;
      let inside = false;
      for (let i = 0, j = 3; i < 4; j = i++) {
        if ((q[i].y > e.y) !== (q[j].y > e.y) && e.x < (q[j].x - q[i].x) * (e.y - q[i].y) / (q[j].y - q[i].y) + q[i].x) inside = !inside;
      }
      return inside;
    }
    const inv = this.matrix().inverse();
    const p = inv.transformPoint(new DOMPoint(e.x, e.y));
    return p.x >= 0 && p.y >= 0 && p.x <= this.st.w && p.y <= this.st.h;
  },

  down(e) {
    if (!this.st) { this.start(); if (!this.st) return; }
    const view = app.view;
    const r = e.type === 'touch' ? 26 : 16;
    const hit = this._handlePos(view).find(h => Math.hypot(h.x - e.sx, h.y - e.sy) <= r);
    const s = this.st;
    if (s.corners) {
      const orig = s.corners.map(p => ({ ...p }));
      if (hit) this.drag = { mode: 'corner', i: hit.i, orig, start: { x: e.x, y: e.y } };
      else if (this._inside(e)) this.drag = { mode: 'quad', orig, start: { x: e.x, y: e.y } };
      return;
    }
    const base = { ...s, start: { x: e.x, y: e.y }, m0: this.matrix() };
    if (hit && hit.n !== 'rot') {
      const H = HANDLES.find(x => x[0] === hit.n);
      this.drag = { mode: 'scale', base, h: H };
    } else if (hit || !this._inside(e)) {
      this.drag = { mode: 'rotate', base };
    } else {
      this.drag = { mode: 'move', base };
    }
  },

  move(e) {
    const d = this.drag;
    if (!d) return;
    const s = this.st, b = d.base;
    if (d.mode === 'corner' || d.mode === 'quad') {
      const dx = e.x - d.start.x, dy = e.y - d.start.y;
      s.corners = d.orig.map((p, i) => (d.mode === 'quad' || i === d.i ? { x: p.x + dx, y: p.y + dy } : { ...p }));
      this._update();
      return;
    }
    if (d.mode === 'move') {
      s.cx = b.cx + e.x - b.start.x;
      s.cy = b.cy + e.y - b.start.y;
    } else if (d.mode === 'rotate') {
      const a0 = Math.atan2(b.start.y - b.cy, b.start.x - b.cx);
      const a1 = Math.atan2(e.y - b.cy, e.x - b.cx);
      let rot = b.rot + (a1 - a0) * 180 / Math.PI;
      if (e.shift || app.modifiers.snap) rot = Math.round(rot / 15) * 15;
      s.rot = rot;
    } else {
      const [, hx, hy, ax, ay] = d.h;
      const Hl = { x: hx * b.w, y: hy * b.h }, Al = { x: ax * b.w, y: ay * b.h };
      const A = b.m0.transformPoint(new DOMPoint(Al.x, Al.y));
      const rad = b.rot * Math.PI / 180, cos = Math.cos(rad), sin = Math.sin(rad);
      const vx0 = e.x - A.x, vy0 = e.y - A.y;
      const vx = vx0 * cos + vy0 * sin, vy = -vx0 * sin + vy0 * cos;
      let sx = Hl.x !== Al.x ? vx / (Hl.x - Al.x) : b.sx;
      let sy = Hl.y !== Al.y ? vy / (Hl.y - Al.y) : b.sy;
      const corner = Hl.x !== Al.x && Hl.y !== Al.y;
      const keep = corner && (this.opt.keepAspect !== !!e.shift);
      if (keep) {
        const k = Math.max(Math.abs(sx / b.sx), Math.abs(sy / b.sy));
        sx = Math.sign(sx || 1) * Math.abs(b.sx) * k;
        sy = Math.sign(sy || 1) * Math.abs(b.sy) * k;
      }
      if (Math.abs(sx * b.w) < 1) sx = Math.sign(sx || 1) / b.w;
      if (Math.abs(sy * b.h) < 1) sy = Math.sign(sy || 1) / b.h;
      s.sx = sx; s.sy = sy;
      // 기준점이 제자리에 있도록 중심 재계산: C = A - R S (Al - half)
      const lx = (Al.x - b.w / 2) * sx, ly = (Al.y - b.h / 2) * sy;
      s.cx = A.x - (lx * cos - ly * sin);
      s.cy = A.y - (lx * sin + ly * cos);
    }
    this._update();
  },

  up() { this.drag = null; app.requestRender(); },

  overlay(ctx, view) {
    if (!this.st) return;
    const pts = this.corners().map(p => view.toScreen(p.x, p.y));
    ctx.strokeStyle = '#2f7cf6';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    pts.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
    ctx.closePath();
    ctx.stroke();
    const hp = this._handlePos(view);
    const top = hp.find(o => o.n === 'n'), rot = hp.find(o => o.n === 'rot');
    if (top) { ctx.beginPath(); ctx.moveTo(top.x, top.y); ctx.lineTo(rot.x, rot.y); ctx.stroke(); }
    const big = app.pointer.type === 'touch' ? 7 : 5;
    for (const p of hp) {
      ctx.fillStyle = '#fff';
      ctx.beginPath();
      if (p.n === 'rot') ctx.arc(p.x, p.y, big + 1, 0, Math.PI * 2);
      else ctx.rect(p.x - big, p.y - big, big * 2, big * 2);
      ctx.fill();
      ctx.stroke();
    }
  },

  options(el) {
    const s = () => this.st;
    const pct = (k) => ({
      get: () => s() ? Math.abs(s()[k]) * 100 : 100,
      set: (v) => { if (!s()) return; s()[k] = Math.sign(s()[k] || 1) * v / 100; this._update(); },
    });
    const ctlBox = document.createElement('div');
    const off = app.bus.on('transform:changed', () => {
      if (!el.isConnected) { off(); return; }
      ctlBox.querySelectorAll('.ctl').forEach(c => c.sync?.());
    });
    ctlBox.append(
      slider('가로 배율', { min: 1, max: 400, step: 1, ...pct('sx'), format: v => Math.round(v) + '%' }),
      slider('세로 배율', { min: 1, max: 400, step: 1, ...pct('sy'), format: v => Math.round(v) + '%' }),
      slider('회전', { min: -180, max: 180, step: 1, get: () => s()?.rot ?? 0, set: v => { if (s()) { s().rot = v; this._update(); } }, format: v => Math.round(v) + '°' }),
    );
    el.append(section('변형',
      segmented([['affine', '이동·크기·회전'], ['distort', '자유 변형 (원근)']], () => this.opt.mode, v => this.setMode(v)),
      toggle('모서리: 비율 유지', () => this.opt.keepAspect, v => { this.opt.keepAspect = v; }),
      ctlBox,
      row(
        button(icon('check', 16) + ' 확정', () => this.commit(), 'primary'),
        button('취소', () => this.cancel()),
        button('다시 시작', () => { this.commit(); this.start(); }),
      ),
      helpText('안쪽 드래그: 이동 · 바깥 드래그: 회전 · 모서리: 크기 · Shift: 비율/15° 스냅 · Enter 확정 · Esc 취소'),
    ));
  },
};
