// 도형 도구: 직선 / 사각형 / 타원 / 꺾은선 — 현재 브러시로 선을 그린다
import { app } from '../app.js';
import { Stroke, commitBuffer, getStrokeBuffer } from '../core/brush.js';
import { checkEditable } from '../core/ops.js';
import { rectFromPoints, rectUnion } from '../core/util.js';
import { segmented, toggle, slider, section } from '../ui/controls.js';
import { activePreset } from './brush.js';
import { helpText } from './basic.js';

export const shapeTool = {
  id: 'shape', name: '도형', icon: 'shape', key: 'u', cursor: 'crosshair',
  opt: { shape: 'line', paint: 'stroke', keepRatio: false, fromCenter: false, taper: 0 },
  st: null,
  poly: null,

  get busy() { return !!this.st; },

  _points(x0, y0, x1, y1) {
    const o = this.opt;
    if (o.shape === 'line') {
      const n = 24, pts = [];
      for (let i = 0; i <= n; i++) {
        const t = i / n;
        // 입출 보정: 양끝을 가늘게
        const p = o.taper ? Math.min(1, Math.min(t, 1 - t) / (o.taper / 2) + 0.05) : 1;
        pts.push({ x: x0 + (x1 - x0) * t, y: y0 + (y1 - y0) * t, p });
      }
      return { pts, closed: false };
    }
    let r = rectFromPoints(x0, y0, x1, y1);
    if (o.keepRatio) { const s = Math.max(r.w, r.h); r = { x: x1 < x0 ? x0 - s : x0, y: y1 < y0 ? y0 - s : y0, w: s, h: s }; }
    if (o.fromCenter) r = { x: x0 - Math.abs(x1 - x0), y: y0 - Math.abs(y1 - y0), w: Math.abs(x1 - x0) * 2, h: Math.abs(y1 - y0) * 2 };
    if (o.shape === 'rect') {
      return { pts: [{ x: r.x, y: r.y, p: 1 }, { x: r.x + r.w, y: r.y, p: 1 }, { x: r.x + r.w, y: r.y + r.h, p: 1 }, { x: r.x, y: r.y + r.h, p: 1 }], closed: true };
    }
    const per = Math.PI * (r.w + r.h) / 2;
    const n = Math.max(24, Math.min(720, Math.round(per / 3)));
    const pts = [];
    for (let i = 0; i < n; i++) {
      const a = i / n * Math.PI * 2;
      pts.push({ x: r.x + r.w / 2 + Math.cos(a) * r.w / 2, y: r.y + r.h / 2 + Math.sin(a) * r.h / 2, p: 1 });
    }
    return { pts, closed: true };
  },

  _draw(pts, closed) {
    const s = this.st;
    const prev = s.stroke.reset();
    const ctx = s.buf.getContext('2d');
    let r = null;
    if (closed && this.opt.paint !== 'stroke') {
      ctx.save();
      ctx.fillStyle = app.color.fg;
      ctx.beginPath();
      pts.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
      ctx.closePath();
      ctx.fill();
      ctx.restore();
      const xs = pts.map(p => p.x), ys = pts.map(p => p.y);
      r = { x: Math.min(...xs) - 2, y: Math.min(...ys) - 2, w: Math.max(...xs) - Math.min(...xs) + 4, h: Math.max(...ys) - Math.min(...ys) + 4 };
      app.doc.selection.clipTo(ctx, r);
    }
    if (!closed || this.opt.paint !== 'fill') s.stroke.polyline(pts, closed);
    s.stroke.total = rectUnion(s.stroke.total, r);
    s.dirty = rectUnion(s.dirty, rectUnion(prev, s.stroke.total));
    app.requestRender();
  },

  _begin() {
    const doc = app.doc, layer = doc.active;
    if (!checkEditable(layer)) return false;
    const p = { ...activePreset('brush') };
    const buf = getStrokeBuffer(doc);
    this.st = {
      layer, buf, preset: p,
      token: app.history.beginPixels(layer),
      stroke: new Stroke({ canvas: buf, preset: p, color: p.color || app.color.fg, doc, symmetry: app.settings.symmetry }),
    };
    app.live = { layer, canvas: buf, mode: 'paint', opacity: p.opacity };
    return true;
  },

  down(e) {
    if (this.opt.shape === 'polyline') {
      if (!this.st && !this._begin()) return;
      if (!this.poly) this.poly = [];
      this.poly.push({ x: e.x, y: e.y, p: 1 });
      this.cursorPt = { x: e.x, y: e.y };
      this._draw([...this.poly, { ...this.cursorPt, p: 1 }], false);
      return;
    }
    if (!this._begin()) return;
    this.st.x0 = e.x; this.st.y0 = e.y;
  },

  move(e) {
    if (!this.st) return;
    if (this.opt.shape === 'polyline') { this.cursorPt = { x: e.x, y: e.y }; this._draw([...this.poly, { ...this.cursorPt, p: 1 }], false); return; }
    let { x, y } = e;
    if (this.opt.shape === 'line' && (e.shift || app.modifiers.snap)) {
      const a = Math.atan2(y - this.st.y0, x - this.st.x0), s = Math.round(a / (Math.PI / 4)) * (Math.PI / 4);
      const d = Math.hypot(x - this.st.x0, y - this.st.y0);
      x = this.st.x0 + Math.cos(s) * d; y = this.st.y0 + Math.sin(s) * d;
    }
    const { pts, closed } = this._points(this.st.x0, this.st.y0, x, y);
    this._draw(pts, closed);
  },

  hover(e) {
    if (this.opt.shape === 'polyline' && this.poly) { this.cursorPt = { x: e.x, y: e.y }; this._draw([...this.poly, { ...this.cursorPt, p: 1 }], false); }
  },

  up() {
    if (this.opt.shape === 'polyline') return;
    this._finish();
  },

  /** 꺾은선 마무리 */
  finishPolyline(close = false) {
    if (!this.poly || !this.st) return;
    if (this.poly.length > 1) this._draw(this.poly, close);
    this.poly = null;
    this._finish();
  },

  commitPending() { if (this.poly) this.finishPolyline(); },
  deactivate() { this.commitPending(); },

  _finish() {
    const s = this.st;
    if (!s) return;
    const r = s.stroke.total;
    if (r) {
      commitBuffer(s.layer, s.buf, r, 'paint', s.preset.opacity);
      if (s.dirty) s.buf.getContext('2d').clearRect(s.dirty.x - 2, s.dirty.y - 2, s.dirty.w + 4, s.dirty.h + 4);
      s.token.commit(rectUnion(r, s.dirty), '도형');
      s.layer.touch();
    } else s.token.commit(null);
    this.st = null;
    app.live = null;
    app.requestRender();
  },

  cancel() {
    const s = this.st;
    if (!s) return;
    const d = rectUnion(s.dirty, s.stroke.total);
    if (d) s.buf.getContext('2d').clearRect(d.x - 2, d.y - 2, d.w + 4, d.h + 4);
    s.token.commit(null);
    this.st = null; this.poly = null;
    app.live = null;
    app.requestRender();
  },

  options(el) {
    const o = this.opt;
    const body = document.createElement('div');
    const rebuild = () => {
      body.innerHTML = '';
      body.append(section('도형',
        segmented([['line', '직선'], ['rect', '사각형'], ['ellipse', '타원'], ['polyline', '꺾은선']], () => o.shape, v => { this.commitPending(); o.shape = v; rebuild(); }),
        o.shape === 'rect' || o.shape === 'ellipse' ? segmented([['stroke', '선'], ['fill', '채우기'], ['both', '선+채우기']], () => o.paint, v => { o.paint = v; }) : null,
        o.shape === 'rect' || o.shape === 'ellipse' ? toggle('정비율', () => o.keepRatio, v => { o.keepRatio = v; }) : null,
        o.shape === 'rect' || o.shape === 'ellipse' ? toggle('중심에서 그리기', () => o.fromCenter, v => { o.fromCenter = v; }) : null,
        o.shape === 'line' ? slider('입출 보정(양끝 가늘게)', { min: 0, max: 1, step: 0.05, get: () => o.taper, set: v => { o.taper = v; }, format: v => Math.round(v * 100) + '%' }) : null,
        o.shape === 'polyline' ? helpText('탭으로 점 추가 · Enter 또는 "꺾은선 끝내기"로 마무리') : null,
        o.shape === 'polyline' ? buttonRow() : null,
        helpText('선 굵기/색은 현재 브러시 설정을 따릅니다.'),
      ));
    };
    const buttonRow = () => {
      const d = document.createElement('div');
      d.className = 'row';
      const b1 = document.createElement('button'); b1.className = 'btn'; b1.textContent = '꺾은선 끝내기'; b1.onclick = () => this.finishPolyline(false);
      const b2 = document.createElement('button'); b2.className = 'btn'; b2.textContent = '닫고 끝내기'; b2.onclick = () => this.finishPolyline(true);
      d.append(b1, b2);
      return d;
    };
    rebuild();
    el.append(body);
  },
};
