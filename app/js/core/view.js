// 화면 변환 (확대/이동/회전/좌우반전). m: 문서 좌표 -> 화면(CSS px) 좌표
import { app } from '../app.js';
import { clamp } from './util.js';

const MIN_ZOOM = 0.02, MAX_ZOOM = 64;

export class View {
  constructor() {
    this.m = new DOMMatrix();
    this._inv = null;
    this.w = 1; this.h = 1;
  }

  get zoom() { return Math.hypot(this.m.c, this.m.d); }
  get angle() { return Math.atan2(-this.m.c, this.m.d) * 180 / Math.PI; }
  get flipped() { return this.m.a * this.m.d - this.m.b * this.m.c < 0; }

  get inv() { return this._inv || (this._inv = this.m.inverse()); }

  set(m) {
    this.m = m;
    this._inv = null;
    app.bus.emit('view:changed', this);
    app.requestRender();
  }

  /** 화면 공간 변환 s 를 앞에 곱한다 */
  applyScreen(s) {
    const next = s.multiply(this.m);
    const z = Math.hypot(next.c, next.d);
    if (z < MIN_ZOOM || z > MAX_ZOOM) return false;
    this.set(next);
    return true;
  }

  toDoc(sx, sy) {
    const p = this.inv.transformPoint(new DOMPoint(sx, sy));
    return { x: p.x, y: p.y };
  }

  toScreen(x, y) {
    const p = this.m.transformPoint(new DOMPoint(x, y));
    return { x: p.x, y: p.y };
  }

  zoomAt(f, sx, sy) {
    const z = this.zoom;
    f = clamp(z * f, MIN_ZOOM, MAX_ZOOM) / z;
    this.applyScreen(new DOMMatrix().translate(sx, sy).scale(f).translate(-sx, -sy));
  }

  setZoom(z, sx = this.w / 2, sy = this.h / 2) { this.zoomAt(z / this.zoom, sx, sy); }

  rotateAt(deg, sx = this.w / 2, sy = this.h / 2) {
    this.applyScreen(new DOMMatrix().translate(sx, sy).rotate(deg).translate(-sx, -sy));
  }

  setAngle(deg) { this.rotateAt(deg - this.angle); }

  panBy(dx, dy) { this.applyScreen(new DOMMatrix().translate(dx, dy)); }

  flipH() {
    const cx = this.w / 2, cy = this.h / 2;
    this.applyScreen(new DOMMatrix().translate(cx, cy).scale(-1, 1).translate(-cx, -cy));
  }

  /** 문서를 화면에 맞춤 (회전/반전 초기화) */
  fit(doc, pad = 0.92) {
    if (!doc) return;
    if (this.w < 8 || this.h < 8) { this._pendingFit = doc; return; }
    this._pendingFit = null;
    const z = Math.min(this.w / doc.width, this.h / doc.height) * pad;
    this.set(new DOMMatrix().translate(this.w / 2, this.h / 2).scale(z).translate(-doc.width / 2, -doc.height / 2));
  }

  /** 가로 폭 맞춤 (웹툰처럼 긴 캔버스용) */
  fitWidth(doc) {
    const z = (this.w / doc.width) * 0.94;
    const top = Math.max(0, this.toDoc(this.w / 2, 0).y);
    this.set(new DOMMatrix().translate(this.w / 2, 16).scale(z).translate(-doc.width / 2, -top));
  }

  actualSize(doc) {
    const c = this.toDoc(this.w / 2, this.h / 2);
    this.set(new DOMMatrix().translate(this.w / 2, this.h / 2).scale(1).translate(-c.x, -c.y));
  }

  resize(w, h) {
    // 화면 크기가 바뀌면 중심을 유지
    const dx = (w - this.w) / 2, dy = (h - this.h) / 2;
    this.w = w; this.h = h;
    if (this._pendingFit && w >= 8 && h >= 8) { this.fit(this._pendingFit); return; }
    this.set(new DOMMatrix().translate(dx, dy).multiply(this.m));
  }
}
