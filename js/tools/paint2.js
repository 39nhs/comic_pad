// 추가 그리기 도구: 그라데이션 / 올가미 채우기 / 손끝(문지르기) / 선택 펜
import { app } from '../app.js';
import { Stroke, commitBuffer, getStrokeBuffer, makePreset, rgbaString } from '../core/brush.js';
import { checkEditable } from '../core/ops.js';
import { createCanvas, rectUnion, pointsBounds, rectClamp, rectPad } from '../core/util.js';
import { slider, toggle, segmented, section } from '../ui/controls.js';
import { helpText } from './basic.js';
import { selectionHistory } from './select.js';

function brushCircle(ctx, view, size) {
  const pt = app.pointer;
  if (!pt.inside || pt.type === 'touch') return;
  const r = size / 2 * view.zoom;
  ctx.lineWidth = 1;
  ctx.strokeStyle = 'rgba(0,0,0,0.6)';
  ctx.beginPath(); ctx.arc(pt.sx, pt.sy, Math.max(2, r), 0, Math.PI * 2); ctx.stroke();
  ctx.strokeStyle = 'rgba(255,255,255,0.8)';
  ctx.beginPath(); ctx.arc(pt.sx, pt.sy, Math.max(2, r) + 1, 0, Math.PI * 2); ctx.stroke();
}

// =====================================================================
// 그라데이션
// =====================================================================
export const gradientTool = {
  id: 'gradient', name: '그라데이션', icon: 'gradient', key: 'j', cursor: 'crosshair',
  opt: { type: 'linear', colors: 'fg-clear', opacity: 1 },
  st: null,
  get busy() { return !!this.st; },

  down(e) {
    const doc = app.doc, owner = doc.active, target = owner?.target;
    if (!checkEditable(target)) return;
    const sel = doc.selection;
    const region = sel.active ? { ...sel.bounds } : { x: 0, y: 0, w: doc.width, h: doc.height };
    this.st = { owner, target, region, x0: e.x, y0: e.y, x1: e.x, y1: e.y, buf: getStrokeBuffer(doc), token: app.history.beginPixels(target) };
    app.live = { layer: owner, canvas: this.st.buf, mode: 'paint', opacity: this.opt.opacity, onMask: !!target.isMask };
  },

  _draw() {
    const s = this.st, o = this.opt, r = s.region;
    const ctx = s.buf.getContext('2d');
    ctx.clearRect(r.x, r.y, r.w, r.h);
    const fg = app.color.fg;
    const end = o.colors === 'fg-bg' ? app.color.bg : rgbaString(fg, 0);
    const g = o.type === 'radial'
      ? ctx.createRadialGradient(s.x0, s.y0, 0, s.x0, s.y0, Math.max(1, Math.hypot(s.x1 - s.x0, s.y1 - s.y0)))
      : ctx.createLinearGradient(s.x0, s.y0, s.x1, s.y1);
    g.addColorStop(0, fg);
    g.addColorStop(1, end);
    ctx.fillStyle = g;
    ctx.fillRect(r.x, r.y, r.w, r.h);
    app.doc.selection.clipTo(ctx, r);
    app.requestRender();
  },

  move(e) {
    const s = this.st;
    if (!s) return;
    let { x, y } = e;
    if (e.shift || app.modifiers.snap) {
      const a = Math.round(Math.atan2(y - s.y0, x - s.x0) / (Math.PI / 4)) * (Math.PI / 4), d = Math.hypot(x - s.x0, y - s.y0);
      x = s.x0 + Math.cos(a) * d; y = s.y0 + Math.sin(a) * d;
    }
    s.x1 = x; s.y1 = y;
    this._draw();
  },

  up() {
    const s = this.st;
    if (!s) return;
    this.st = null;
    app.live = null;
    if (Math.hypot(s.x1 - s.x0, s.y1 - s.y0) < 1) { s.token.commit(null); app.requestRender(); return; }
    commitBuffer(s.target, s.buf, s.region, 'paint', this.opt.opacity);
    s.token.commit(s.region, '그라데이션');
    s.target.touch();
  },

  cancel() {
    const s = this.st;
    if (!s) return;
    s.buf.getContext('2d').clearRect(s.region.x, s.region.y, s.region.w, s.region.h);
    s.token.commit(null);
    this.st = null; app.live = null; app.requestRender();
  },

  overlay(ctx, view) {
    const s = this.st;
    if (!s) return;
    const a = view.toScreen(s.x0, s.y0), b = view.toScreen(s.x1, s.y1);
    ctx.strokeStyle = '#fff'; ctx.lineWidth = 3;
    ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
    ctx.strokeStyle = '#2f7cf6'; ctx.lineWidth = 1.5; ctx.stroke();
    for (const p of [a, b]) { ctx.fillStyle = '#fff'; ctx.beginPath(); ctx.arc(p.x, p.y, 5, 0, Math.PI * 2); ctx.fill(); ctx.stroke(); }
  },

  options(el) {
    const o = this.opt;
    el.append(section('그라데이션',
      segmented([['linear', '직선'], ['radial', '원형']], () => o.type, v => { o.type = v; }),
      segmented([['fg-clear', '전경색 → 투명'], ['fg-bg', '전경색 → 배경색']], () => o.colors, v => { o.colors = v; }),
      slider('불투명도', { min: 0, max: 1, step: 0.01, get: () => o.opacity, set: v => { o.opacity = v; }, format: v => Math.round(v * 100) + '%' }),
      helpText('드래그한 방향으로 칠합니다. 선택 영역이 있으면 그 안에만. Shift: 45° 단위.'),
    ));
  },
};

// =====================================================================
// 올가미 채우기 (둘러싸서 칠하기 / 지우기)
// =====================================================================
export const lassoFillTool = {
  id: 'lassofill', name: '올가미 채우기', icon: 'lassofill', key: 'l', cursor: 'crosshair', allowAltPick: false,
  opt: { mode: 'fill' },
  pts: null,

  down(e) { this.pts = [{ x: e.x, y: e.y }]; this.busy = true; this.erase = this.opt.mode === 'erase' || e.alt; },
  move(e) {
    if (!this.pts) return;
    const l = this.pts[this.pts.length - 1];
    if (Math.hypot(e.x - l.x, e.y - l.y) * app.view.zoom > 2) { this.pts.push({ x: e.x, y: e.y }); app.requestRender(); }
  },
  up() {
    const pts = this.pts;
    this.pts = null; this.busy = false;
    app.requestRender();
    if (!pts || pts.length < 3) return;
    const doc = app.doc, target = doc.active?.target;
    if (!checkEditable(target)) return;
    const b = rectClamp(rectPad(pointsBounds(pts), 2), doc.width, doc.height);
    if (!b) return;
    const tmp = createCanvas(b.w, b.h);
    const t = tmp.getContext('2d');
    t.translate(-b.x, -b.y);
    t.fillStyle = app.color.fg;
    t.beginPath();
    pts.forEach((p, i) => (i ? t.lineTo(p.x, p.y) : t.moveTo(p.x, p.y)));
    t.closePath();
    t.fill();
    if (doc.selection.active) {
      t.setTransform(1, 0, 0, 1, 0, 0);
      t.globalCompositeOperation = 'destination-in';
      t.drawImage(doc.selection.mask, b.x, b.y, b.w, b.h, 0, 0, b.w, b.h);
    }
    const erase = this.erase;
    app.history.pixels(target, erase ? '올가미 지우기' : '올가미 채우기', (ctx) => {
      ctx.save();
      ctx.globalCompositeOperation = erase ? 'destination-out' : (target.alphaLock ? 'source-atop' : 'source-over');
      ctx.drawImage(tmp, b.x, b.y);
      ctx.restore();
    }, b);
  },
  cancel() { this.pts = null; this.busy = false; app.requestRender(); },
  overlay(ctx, view) {
    if (!this.pts) return;
    ctx.beginPath();
    this.pts.forEach((p, i) => { const s = view.toScreen(p.x, p.y); if (i) ctx.lineTo(s.x, s.y); else ctx.moveTo(s.x, s.y); });
    ctx.closePath();
    ctx.globalAlpha = 0.45;
    ctx.fillStyle = this.erase ? '#ff5a5a' : app.color.fg;
    ctx.fill();
    ctx.globalAlpha = 1;
    ctx.strokeStyle = '#fff';
    ctx.setLineDash([4, 3]);
    ctx.stroke();
  },
  options(el) {
    el.append(section('올가미 채우기',
      segmented([['fill', '채우기'], ['erase', '지우기']], () => this.opt.mode, v => { this.opt.mode = v; }),
      helpText('둘러싼 영역을 전경색으로 칠합니다 (밑색 깔기에 편함). Alt를 누르고 그리면 지우기.'),
    ));
  },
};

// =====================================================================
// 손끝 (문지르기)
// =====================================================================
export const smudgeTool = {
  id: 'smudge', name: '손끝 (문지르기)', icon: 'smudge', key: 'w', cursor: 'none',
  opt: { size: 40, strength: 0.65, pressure: true, hardness: 0.3 },
  st: null,
  get busy() { return !!this.st; },

  _tip(D) {
    if (this._tipC && this._tipC.width === D && this._tipH === this.opt.hardness) return this._tipC;
    const c = createCanvas(D, D), x = c.getContext('2d'), r = D / 2;
    const g = x.createRadialGradient(r, r, 0, r, r, r);
    g.addColorStop(0, '#fff'); g.addColorStop(Math.min(0.95, this.opt.hardness), '#fff'); g.addColorStop(1, 'rgba(255,255,255,0)');
    x.fillStyle = g; x.fillRect(0, 0, D, D);
    this._tipC = c; this._tipH = this.opt.hardness;
    return c;
  },

  down(e) {
    const target = app.doc.active?.target;
    if (!checkEditable(target)) return;
    const D = Math.max(2, Math.round(this.opt.size));
    this.st = { target, D, token: app.history.beginPixels(target), carry: createCanvas(D, D), tmp: createCanvas(D, D), last: { x: e.x, y: e.y }, next: 0, dirty: null };
    this._grab(e.x, e.y);
  },

  _grab(x, y) {
    const s = this.st, r = s.D / 2;
    const c = s.carry.getContext('2d');
    c.clearRect(0, 0, s.D, s.D);
    c.drawImage(s.target.canvas, Math.round(x - r), Math.round(y - r), s.D, s.D, 0, 0, s.D, s.D);
  },

  _dab(x, y, p) {
    const s = this.st, r = s.D / 2, D = s.D;
    const t = s.tmp.getContext('2d');
    t.globalCompositeOperation = 'copy';
    t.drawImage(s.carry, 0, 0);
    t.globalCompositeOperation = 'destination-in';
    t.drawImage(this._tip(D), 0, 0);
    const sel = app.doc.selection;
    const X = Math.round(x - r), Y = Math.round(y - r);
    if (sel.active) t.drawImage(sel.mask, X, Y, D, D, 0, 0, D, D);
    const ctx = s.target.ctx;
    ctx.save();
    ctx.globalAlpha = this.opt.strength * (this.opt.pressure ? Math.max(0.1, p) : 1);
    if (s.target.alphaLock) ctx.globalCompositeOperation = 'source-atop';
    ctx.drawImage(s.tmp, X, Y);
    ctx.restore();
    s.dirty = rectUnion(s.dirty, { x: X, y: Y, w: D, h: D });
    this._grab(x, y);
  },

  move(e) {
    const s = this.st;
    if (!s) return;
    const a = s.last, dx = e.x - a.x, dy = e.y - a.y, len = Math.hypot(dx, dy);
    const step = Math.max(1, s.D * 0.12);
    let t = s.next;
    while (t <= len) { this._dab(a.x + dx * t / len, a.y + dy * t / len, e.pressure); t += step; }
    s.next = t - len;
    s.last = { x: e.x, y: e.y };
    s.target.touch();
  },

  up() {
    const s = this.st;
    if (!s) return;
    this.st = null;
    s.token.commit(s.dirty, '문지르기');
    s.target.touch();
  },

  cancel() { if (this.st) { this.st.token.cancel(); this.st = null; } },
  overlay(ctx, view) { brushCircle(ctx, view, this.opt.size); },
  options(el) {
    const o = this.opt;
    el.append(section('손끝',
      slider('크기', { min: 4, max: 300, step: 1, log: true, get: () => o.size, set: v => { o.size = v; } }),
      slider('세기', { min: 0.05, max: 1, step: 0.01, get: () => o.strength, set: v => { o.strength = v; }, format: v => Math.round(v * 100) + '%' }),
      slider('경도', { min: 0, max: 0.95, step: 0.01, get: () => o.hardness, set: v => { o.hardness = v; }, format: v => Math.round(v * 100) + '%' }),
      toggle('필압으로 세기 조절', () => o.pressure, v => { o.pressure = v; }),
      helpText('색을 끌어서 섞습니다. 채색 경계를 부드럽게 할 때 사용하세요.'),
    ));
  },
};

// =====================================================================
// 선택 펜 (브러시로 선택 영역 칠하기)
// =====================================================================
export const selPenTool = {
  id: 'selpen', name: '선택 펜', icon: 'selpen', key: 'q', cursor: 'none', allowAltPick: false,
  opt: { size: 40, hardness: 1, mode: 'add' },
  st: null,
  get busy() { return !!this.st; },

  down(e) {
    const doc = app.doc;
    const buf = getStrokeBuffer(doc);
    const preset = makePreset({ size: this.opt.size, hardness: this.opt.hardness, pressureSize: false, stabilizer: 2 });
    this.st = { buf, sub: this.opt.mode === 'sub' || e.alt, stroke: new Stroke({ canvas: buf, preset, color: '#ff3a6a', doc, noClip: true }) };
    this.st.stroke.begin({ x: e.x, y: e.y, p: 1 });
  },
  move(e) { this.st?.stroke.add({ x: e.x, y: e.y, p: 1 }); },
  up(e) {
    const s = this.st;
    if (!s) return;
    this.st = null;
    const r = s.stroke.end({ x: e.x, y: e.y, p: 1 });
    if (r) {
      const sel = app.doc.selection;
      selectionHistory(() => sel.apply((ctx) => ctx.drawImage(s.buf, r.x, r.y, r.w, r.h, r.x, r.y, r.w, r.h), s.sub ? 'subtract' : 'add', r), '선택 펜');
      s.buf.getContext('2d').clearRect(r.x - 1, r.y - 1, r.w + 2, r.h + 2);
    }
    app.requestRender();
  },
  cancel() {
    const s = this.st;
    if (!s) return;
    const r = s.stroke.total;
    if (r) s.buf.getContext('2d').clearRect(r.x - 1, r.y - 1, r.w + 2, r.h + 2);
    this.st = null; app.requestRender();
  },
  overlay(ctx, view) {
    if (this.st) {
      const m = view.m;
      ctx.save();
      ctx.transform(m.a, m.b, m.c, m.d, m.e, m.f);
      ctx.globalAlpha = 0.5;
      ctx.drawImage(this.st.buf, 0, 0);
      ctx.restore();
    }
    brushCircle(ctx, view, this.opt.size);
  },
  options(el) {
    const o = this.opt;
    el.append(section('선택 펜',
      segmented([['add', '선택 추가'], ['sub', '선택 지우기']], () => o.mode, v => { o.mode = v; }),
      slider('크기', { min: 1, max: 400, step: 1, log: true, get: () => o.size, set: v => { o.size = v; } }),
      slider('경도', { min: 0, max: 1, step: 0.01, get: () => o.hardness, set: v => { o.hardness = v; }, format: v => Math.round(v * 100) + '%' }),
      helpText('브러시로 칠한 곳이 선택됩니다. Alt를 누르고 칠하면 선택 지우기.'),
    ));
  },
};
