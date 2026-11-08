// 선택 영역: 문서 크기 알파 마스크 캔버스
import { app } from '../app.js';
import { createCanvas, rectClamp, rectUnion, pointsBounds, rectPad } from './util.js';
import { dilate, erode } from './raster.js';

export class Selection {
  constructor(doc) {
    this.doc = doc;
    this.mask = null;
    this.active = false;
    this.bounds = null;
    this.edge = null;       // 경계선 표시용 캔버스 (bounds 크기)
    this.paths = null;      // 벡터 경로 (사각/올가미일 때 점선 표시)
  }

  _ensure() {
    if (!this.mask || this.mask.width !== this.doc.width || this.mask.height !== this.doc.height) {
      this.mask = createCanvas(this.doc.width, this.doc.height);
    }
    return this.mask.getContext('2d');
  }

  clear(silent) {
    if (this.mask) this.mask.getContext('2d').clearRect(0, 0, this.mask.width, this.mask.height);
    this.active = false;
    this.bounds = this.edge = this.paths = null;
    if (!silent) this._changed();
  }

  _changed() {
    app.bus.emit('selection:changed', this);
    app.requestRender();
  }

  /** 상태 저장/복원 (히스토리용) */
  snapshot() {
    if (!this.active) return null;
    const c = createCanvas(this.bounds.w, this.bounds.h);
    c.getContext('2d').drawImage(this.mask, this.bounds.x, this.bounds.y, this.bounds.w, this.bounds.h, 0, 0, this.bounds.w, this.bounds.h);
    return { canvas: c, bounds: { ...this.bounds }, paths: this.paths };
  }

  restore(s) {
    this.clear(true);
    if (s) {
      const ctx = this._ensure();
      ctx.drawImage(s.canvas, s.bounds.x, s.bounds.y);
      this._recompute(s.bounds);
      this.paths = s.paths;
    }
    this._changed();
  }

  /**
   * drawFn(ctx) 가 흰색으로 그린 모양을 op 에 따라 합성
   * op: replace | add | subtract | intersect
   */
  apply(drawFn, op = 'replace', hint = null, paths = null) {
    const ctx = this._ensure();
    const wasActive = this.active;
    const prevBounds = this.bounds;
    const fresh = op === 'replace' || !wasActive;
    if (op === 'replace' || (!wasActive && op !== 'subtract')) {
      ctx.clearRect(0, 0, this.mask.width, this.mask.height);
      op = 'add';
    }
    if (op === 'subtract' && !wasActive) return;
    ctx.save();
    ctx.fillStyle = '#fff';
    ctx.strokeStyle = '#fff';
    if (op === 'subtract') ctx.globalCompositeOperation = 'destination-out';
    if (op === 'intersect') ctx.globalCompositeOperation = 'destination-in';
    drawFn(ctx);
    ctx.restore();
    let region = null;
    if (hint && op !== 'intersect') region = op === 'subtract' ? prevBounds : rectUnion(fresh ? null : prevBounds, rectPad(hint, 2));
    else if (op === 'intersect') region = prevBounds;
    this._recompute(region);
    this.paths = fresh ? paths : null;
    this._changed();
  }

  rect(x, y, w, h, op) {
    const pts = [{ x, y }, { x: x + w, y }, { x: x + w, y: y + h }, { x, y: y + h }];
    this.apply(ctx => ctx.fillRect(x, y, w, h), op, { x, y, w, h }, [pts]);
  }

  ellipse(x, y, w, h, op) {
    const pts = [];
    const n = 96;
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 2;
      pts.push({ x: x + w / 2 + Math.cos(a) * w / 2, y: y + h / 2 + Math.sin(a) * h / 2 });
    }
    this.apply(ctx => { ctx.beginPath(); ctx.ellipse(x + w / 2, y + h / 2, w / 2, h / 2, 0, 0, Math.PI * 2); ctx.fill(); }, op, { x, y, w, h }, [pts]);
  }

  polygon(points, op) {
    if (points.length < 3) return;
    this.apply(ctx => {
      ctx.beginPath();
      ctx.moveTo(points[0].x, points[0].y);
      for (const p of points) ctx.lineTo(p.x, p.y);
      ctx.closePath();
      ctx.fill();
    }, op, pointsBounds(points), [points]);
  }

  /** 0/1 마스크(문서 크기)로 선택 */
  fromMask(mask, bounds, op) {
    if (!bounds) return;
    const c = createCanvas(bounds.w, bounds.h);
    const cx = c.getContext('2d');
    const img = cx.createImageData(bounds.w, bounds.h);
    const W = this.doc.width;
    for (let y = 0; y < bounds.h; y++) {
      const srow = (y + bounds.y) * W + bounds.x;
      for (let x = 0; x < bounds.w; x++) {
        if (mask[srow + x]) {
          const p = (y * bounds.w + x) * 4;
          img.data[p] = img.data[p + 1] = img.data[p + 2] = img.data[p + 3] = 255;
        }
      }
    }
    cx.putImageData(img, 0, 0);
    this.apply(ctx => ctx.drawImage(c, bounds.x, bounds.y), op, bounds, null);
  }

  /** 캔버스의 알파로 선택 (레이어 불투명 부분 선택) */
  fromAlpha(canvas, op) {
    this.apply(ctx => ctx.drawImage(canvas, 0, 0), op, { x: 0, y: 0, w: this.doc.width, h: this.doc.height });
  }

  selectAll() { this.rect(0, 0, this.doc.width, this.doc.height, 'replace'); }

  invert() {
    const ctx = this._ensure();
    const tmp = createCanvas(this.doc.width, this.doc.height);
    const t = tmp.getContext('2d');
    t.fillStyle = '#fff';
    t.fillRect(0, 0, tmp.width, tmp.height);
    if (this.active) {
      t.globalCompositeOperation = 'destination-out';
      t.drawImage(this.mask, 0, 0);
    }
    ctx.clearRect(0, 0, tmp.width, tmp.height);
    ctx.drawImage(tmp, 0, 0);
    this.paths = null;
    this._recompute(null);
    this._changed();
  }

  /** px 만큼 확장(+)/축소(-) */
  grow(px) {
    if (!this.active || !px) return;
    const W = this.doc.width, H = this.doc.height;
    const region = rectClamp(rectPad(this.bounds, Math.abs(px) + 2), W, H);
    const ctx = this._ensure();
    const img = ctx.getImageData(region.x, region.y, region.w, region.h);
    const n = region.w * region.h;
    let m = new Uint8Array(n);
    for (let i = 0; i < n; i++) m[i] = img.data[i * 4 + 3] >= 128 ? 1 : 0;
    m = px > 0 ? dilate(m, region.w, region.h, px) : erode(m, region.w, region.h, -px);
    for (let i = 0; i < n; i++) {
      const v = m[i] ? 255 : 0;
      img.data[i * 4] = img.data[i * 4 + 1] = img.data[i * 4 + 2] = img.data[i * 4 + 3] = v;
    }
    ctx.putImageData(img, region.x, region.y);
    this.paths = null;
    this._recompute(region);
    this._changed();
  }

  _recompute(region) {
    const W = this.doc.width, H = this.doc.height;
    region = rectClamp(region || { x: 0, y: 0, w: W, h: H }, W, H);
    if (!region) { this.active = false; this.bounds = this.edge = null; return; }
    const ctx = this.mask.getContext('2d');
    const d = ctx.getImageData(region.x, region.y, region.w, region.h).data;
    const rw = region.w, rh = region.h;
    let x0 = rw, y0 = rh, x1 = -1, y1 = -1;
    for (let y = 0; y < rh; y++) {
      for (let x = 0, p = y * rw * 4 + 3; x < rw; x++, p += 4) {
        if (d[p] >= 128) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; y1 = y; }
      }
    }
    if (x1 < 0) { this.active = false; this.bounds = this.edge = null; return; }
    this.active = true;
    const b = { x: region.x + x0, y: region.y + y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
    this.bounds = b;
    // 경계선 이미지: 바깥 이웃이 있는 안쪽 픽셀
    const e = createCanvas(b.w, b.h);
    const ex = e.getContext('2d');
    const out = ex.createImageData(b.w, b.h);
    const at = (x, y) => {
      const gx = x + b.x - region.x, gy = y + b.y - region.y;
      if (gx < 0 || gy < 0 || gx >= rw || gy >= rh) return 0;
      return d[(gy * rw + gx) * 4 + 3] >= 128 ? 1 : 0;
    };
    for (let y = 0; y < b.h; y++) {
      for (let x = 0; x < b.w; x++) {
        if (at(x, y) && (!at(x - 1, y) || !at(x + 1, y) || !at(x, y - 1) || !at(x, y + 1))) {
          const p = (y * b.w + x) * 4;
          const dark = ((x + y) >> 2) & 1;
          out.data[p] = out.data[p + 1] = out.data[p + 2] = dark ? 20 : 255;
          out.data[p + 3] = 255;
        }
      }
    }
    ex.putImageData(out, 0, 0);
    this.edge = e;
  }

  /** ctx 의 rect 영역을 선택 영역으로 잘라낸다 (선택 밖은 지움) */
  clipTo(ctx, r) {
    if (!this.active) return;
    const c = rectClamp(r, this.doc.width, this.doc.height);
    if (!c) return;
    ctx.save();
    ctx.beginPath();
    ctx.rect(c.x, c.y, c.w, c.h);
    ctx.clip();
    ctx.globalCompositeOperation = 'destination-in';
    ctx.drawImage(this.mask, c.x, c.y, c.w, c.h, c.x, c.y, c.w, c.h);
    ctx.restore();
  }

  contains(x, y) {
    if (!this.active) return false;
    const b = this.bounds;
    if (x < b.x || y < b.y || x >= b.x + b.w || y >= b.y + b.h) return false;
    return this.mask.getContext('2d').getImageData(Math.floor(x), Math.floor(y), 1, 1).data[3] >= 128;
  }
}
