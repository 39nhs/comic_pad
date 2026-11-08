import { app } from '../app.js';
import { createCanvas, uid, canvasToBlob, blobToDataURL, loadImage } from './util.js';
import { Selection } from './selection.js';

export const BLEND_MODES = [
  ['source-over', '표준'], ['multiply', '곱하기'], ['screen', '스크린'], ['overlay', '오버레이'],
  ['darken', '어둡게'], ['lighten', '밝게'], ['color-dodge', '닷지'], ['color-burn', '번'],
  ['hard-light', '하드 라이트'], ['soft-light', '소프트 라이트'], ['difference', '차이'],
  ['hue', '색조'], ['saturation', '채도'], ['color', '컬러'], ['luminosity', '광도'],
  ['lighter', '더하기 (발광)'],
];

/** 픽셀(32비트)을 [개수, 값] 쌍으로 줄이기: 비어 있는 곳이 많은 선화·채색 레이어는 아주 작아짐 */
function rleEncode(ctx, w, hgt) {
  const parts = [];
  let total = 0;
  const step = Math.max(1, Math.floor(2_000_000 / w));
  let run = 0, val = 0, first = true;
  let cur = new Uint32Array(1 << 16), n = 0;
  const pushPair = (c, v) => { if (n + 2 > cur.length) { parts.push(cur); total += n; cur = new Uint32Array(cur.length * 2); n = 0; } cur[n++] = c; cur[n++] = v; };
  for (let y = 0; y < hgt; y += step) {
    const u = new Uint32Array(ctx.getImageData(0, y, w, Math.min(step, hgt - y)).data.buffer);
    for (let i = 0; i < u.length; i++) {
      const v = u[i];
      if (first) { val = v; run = 1; first = false; } else if (v === val && run < 0xffffffff) run++; else { pushPair(run, val); val = v; run = 1; }
    }
  }
  if (!first) pushPair(run, val);
  parts.push(cur.slice(0, n)); total += n;
  const out = new Uint32Array(total);
  let p = 0;
  for (const a of parts) { out.set(a, p); p += a.length; }
  return out;
}
function rleDecode(runs, ctx, w, hgt) {
  const step = Math.max(1, Math.floor(2_000_000 / w));
  let ri = 0, left = runs.length ? runs[0] : 0;
  for (let y = 0; y < hgt; y += step) {
    const rows = Math.min(step, hgt - y), img = ctx.createImageData(w, rows), u = new Uint32Array(img.data.buffer);
    for (let i = 0; i < u.length;) {
      const take = Math.min(left, u.length - i), v = runs[ri + 1];
      if (v) u.fill(v, i, i + take);
      i += take; left -= take;
      if (!left) { ri += 2; left = runs[ri] || 0; }
    }
    ctx.putImageData(img, 0, y);
  }
}

export class Layer {
  /** 메모리 절약: 쉬는 레이어는 압축해 두고 캔버스를 비운다. 코드가 canvas/ctx 를 쓰면 저절로 되살아남 */
  get canvas() { if (this._frozen) this.thaw(); return this._canvas; }
  set canvas(c) { this._canvas = c; this._frozen = null; }
  get ctx() { if (this._frozen) this.thaw(); return this._ctx; }
  set ctx(c) { this._ctx = c; }
  /** 화면 그리기·썸네일용: 쉬는 중이면 절반 크기 미리보기 (되살리지 않음) */
  get displayCanvas() { return this._frozen?.preview || this.canvas; }
  get frozen() { return !!this._frozen; }

  freeze() {
    if (this._frozen || this.kind === 'folder' || this.kind === 'adjust') return false;
    const c = this._canvas, w = c.width, hgt = c.height;
    const runs = rleEncode(this._ctx, w, hgt);
    if (runs.length > w * hgt * 0.6) return false;            // 빽빽한 그림은 줄어들지 않으니 그대로
    let preview = null;
    if (this.visible) {
      preview = createCanvas(Math.ceil(w / 2), Math.ceil(hgt / 2));
      const px = preview.getContext('2d');
      px.imageSmoothingQuality = 'high';
      px.drawImage(c, 0, 0, preview.width, preview.height);
    }
    this._frozen = { runs, w, hgt, preview };
    c.width = 1; c.height = 1;                               // 큰 캔버스 메모리 돌려주기
    return true;
  }

  thaw() {
    const f = this._frozen;
    if (!f) return;
    this._frozen = null;
    const c = this._canvas;
    c.width = f.w; c.height = f.hgt;
    this._ctx = c.getContext('2d');
    rleDecode(f.runs, this._ctx, f.w, f.hgt);
    this._lastTouch = Date.now();
  }

  constructor(doc, o = {}) {
    this.doc = doc;
    this.id = o.id || uid('L');
    this.name = o.name || '레이어';
    this.kind = o.kind || 'raster';      // raster | text | panel | bubble | folder
    // 폴더는 픽셀이 없으므로 1x1
    this.canvas = this.kind === 'folder' || this.kind === 'adjust' ? createCanvas(1, 1) : createCanvas(doc.width, doc.height);
    this.ctx = this.canvas.getContext('2d');
    this.visible = o.visible ?? true;
    this.opacity = o.opacity ?? 1;
    this.blend = o.blend || 'source-over';
    this.locked = o.locked ?? false;
    this.alphaLock = o.alphaLock ?? false;
    this.clip = o.clip ?? false;         // 아래 레이어에서 클리핑
    this.draft = o.draft ?? false;       // 밑그림: 내보내기/채우기 참조에서 제외
    this.tint = o.tint ?? null;          // 레이어 컬러 (러프용 파란선 등)
    this.meta = o.meta ? structuredClone(o.meta) : null;
    this.ref = o.ref ?? false;           // 참조 레이어 (채우기/자동선택 기준)
    this.mask = null;                    // 레이어 마스크 (알파 = 보이는 부분)
    this.maskOn = o.maskOn ?? true;
    this.editMask = false;               // 마스크 편집 중
    this.parent = o.parent ?? null;      // 들어 있는 폴더 id
    this.expanded = o.expanded ?? true;  // 폴더 펼침
    this.rough = o.rough ?? false;       // 러프 전용 레이어 (러프 연필은 여기서만)
    this.tone = o.tone ? { ...o.tone } : null;   // 톤화(망점) { spacing, angle, shape, color }
    this.fx = o.fx ? structuredClone(o.fx) : null;  // 레이어 효과 { stroke, shadow, glow }
    this.version = 0;
  }

  get isFolder() { return this.kind === 'folder'; }

  /** 그리기 대상: 마스크 편집 중이면 마스크, 아니면 레이어 자신 */
  get target() {
    if (!this.editMask || !this.mask) return this;
    const l = this;
    return {
      isMask: true, layer: l, canvas: l.mask, ctx: l.mask.getContext('2d'),
      get locked() { return l.locked; }, get visible() { return l.visible; }, alphaLock: false, name: l.name + ' 마스크',
      touch: () => l.touch(),
    };
  }

  createMask(fromCanvas) {
    this.mask = createCanvas(this.doc.width, this.doc.height);
    const x = this.mask.getContext('2d');
    if (fromCanvas) x.drawImage(fromCanvas, 0, 0);
    else { x.fillStyle = '#fff'; x.fillRect(0, 0, this.mask.width, this.mask.height); }
    this.maskOn = true;
  }

  touch() {
    this.version++;
    this._lastTouch = Date.now();
    app.bus.emit('layer:pixels', this);
    app.markModified();
    app.requestRender();
  }

  props() {
    const { name, kind, visible, opacity, blend, locked, alphaLock, clip, draft, tint, meta, ref, maskOn, parent, expanded, tone, fx, rough } = this;
    return { name, kind, visible, opacity, blend, locked, alphaLock, clip, draft, tint, ref, maskOn, parent, expanded, rough, tone: tone ? { ...tone } : null, fx: fx ? structuredClone(fx) : null, meta: meta ? structuredClone(meta) : null };
  }

  setProps(p) {
    for (const k of Object.keys(p)) this[k] = (k === 'meta' || k === 'fx' || k === 'tone') && p[k] ? structuredClone(p[k]) : p[k];
  }

  clone(name) {
    const l = new Layer(this.doc, { ...this.props(), name: name ?? this.name + ' 복사' });
    l.ctx.drawImage(this.canvas, 0, 0);
    if (this.mask) l.createMask(this.mask);
    return l;
  }

  /** fast: 자동 저장용. 레이어 변경 번호가 그대로면 픽셀을 다시 읽지 않는다 */
  async serialize({ fast = false } = {}) {
    const out = { id: this.id, ...this.props(), data: null };
    if (this._frozen && this._ser && this._ser.version === this.version && !this.mask) fast = true;
    if (fast && this._ser && this._ser.version === this.version && (!this.mask || this._mser)) {
      out.data = this._ser.data; out.rect = this._ser.rect;
      if (this.mask) out.mask = this._mser.data;
      return out;
    }
    if (!this.isFolder && this.kind !== 'adjust') {
      // 그림이 있는 부분만 PNG 로 (큰 원고에서 저장이 훨씬 빠름). 내용이 그대로면 지난번 결과 재사용
      const sc = scanPixels(this.canvas);
      if (sc.bounds) {
        if (this._ser?.hash !== sc.hash || this._ser.w !== this.canvas.width) {
          const b = sc.bounds, c = createCanvas(b.w, b.h);
          c.getContext('2d').drawImage(this.canvas, -b.x, -b.y);
          this._ser = { hash: sc.hash, w: this.canvas.width, data: await blobToDataURL(await canvasToBlob(c)), rect: b };
        }
        out.data = this._ser.data;
        out.rect = this._ser.rect;
      } else this._ser = { hash: sc.hash, w: this.canvas.width, data: null, rect: null };
      this._ser.version = this.version;
    } else this._ser = { version: this.version, data: null, rect: null };
    if (this.mask) {
      const sc = scanPixels(this.mask, false);
      if (this._mser?.hash !== sc.hash) this._mser = { hash: sc.hash, data: await blobToDataURL(await canvasToBlob(this.mask)) };
      out.mask = this._mser.data;
    }
    return out;
  }
}

/**
 * 픽셀 훑기: 그림이 있는 영역(투명이 아닌 곳)과 내용 지문(해시).
 * 메모리를 아끼려고 몇백 줄씩 나눠 읽는다.
 */
export function scanPixels(canvas, wantBounds = true) {
  const w = canvas.width, hgt = canvas.height, ctx = canvas.getContext('2d');
  const step = Math.max(1, Math.floor(4_000_000 / Math.max(1, w)));
  let h1 = 0x811c9dc5 ^ w, h2 = hgt;
  let x0 = w, y0 = hgt, x1 = -1, y1 = -1;
  for (let y = 0; y < hgt; y += step) {
    const rows = Math.min(step, hgt - y);
    const u = new Uint32Array(ctx.getImageData(0, y, w, rows).data.buffer);
    for (let r = 0, i = 0; r < rows; r++) {
      let rowAny = false, rx0 = -1, rx1 = -1;
      for (let x = 0; x < w; x++, i++) {
        const v = u[i];
        h1 = Math.imul(h1 ^ v, 16777619);
        if (v) { h2 = (h2 + Math.imul(v, i + y * w + 1)) | 0; if (wantBounds && v >>> 24) { if (!rowAny) { rowAny = true; rx0 = x; } rx1 = x; } }
      }
      if (rowAny) { if (rx0 < x0) x0 = rx0; if (rx1 > x1) x1 = rx1; if (y + r < y0) y0 = y + r; y1 = y + r; }
    }
  }
  return { hash: (h1 >>> 0).toString(36) + (h2 >>> 0).toString(36), bounds: x1 < 0 ? null : { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 } };
}

export class Doc {
  constructor(width, height, o = {}) {
    this.width = Math.round(width);
    this.height = Math.round(height);
    this.name = o.name || '제목 없음';
    this.file = o.file || null;          // data/projects 안의 파일명
    this.paper = { color: o.paperColor ?? '#ffffff', visible: o.paperVisible ?? true };
    this.layers = [];
    this.activeId = null;
    this.selection = new Selection(this);
    this.rulers = [];                    // 자: { type: parallel|radial|perspective|concentric, ... }
    this.print = null;                   // 원고 설정 (재단/도련/안전선)
    this.notes = [];                     // 메모 핀
    this.anim = { fps: 12, frame: 0, onion: true, onionOpacity: 0.3, onionPrev: 1, onionNext: 1, loop: true, playing: false };
    this.modified = false;
    this.dpi = o.dpi || 350;
  }

  get active() {
    return this.layers.find(l => l.id === this.activeId) || this.layers[this.layers.length - 1] || null;
  }

  setActive(layer) {
    this.activeId = layer?.id ?? null;
    app.bus.emit('layers:active', layer);
    app.bus.emit('layers:changed');
  }

  indexOf(layer) { return this.layers.indexOf(layer); }
  byId(id) { return this.layers.find(l => l.id === id); }

  createLayer(o) { return new Layer(this, o); }

  // 히스토리 없이 직접 조작 (ops.js 가 히스토리를 붙여 호출)
  _insert(layer, index) {
    this.layers.splice(index ?? this.layers.length, 0, layer);
    app.bus.emit('layers:changed');
    app.markModified();
    app.requestRender();
  }

  _remove(layer) {
    const i = this.layers.indexOf(layer);
    if (i >= 0) this.layers.splice(i, 1);
    if (this.activeId === layer.id) this.activeId = (this.layers[Math.max(0, i - 1)] || this.layers[0])?.id ?? null;
    app.bus.emit('layers:changed');
    app.markModified();
    app.requestRender();
    return i;
  }

  /** 폴더(또는 null = 최상위)의 직속 자식들 (아래→위 순) */
  childrenOf(folder) {
    const id = folder?.id ?? null;
    return this.layers.filter(l => this.parentOf(l) === id);
  }

  /** 실제로 존재하는 폴더만 부모로 인정 */
  parentOf(l) {
    if (!l.parent) return null;
    const p = this.byId(l.parent);
    return p && p.isFolder ? p.id : null;
  }

  descendants(folder) {
    const out = [];
    const walk = (f) => { for (const c of this.childrenOf(f)) { out.push(c); if (c.isFolder) walk(c); } };
    walk(folder);
    return out;
  }

  depthOf(l) {
    let d = 0, p = this.parentOf(l);
    while (p && d < 32) { d++; p = this.parentOf(this.byId(p)); }
    return d;
  }

  isInside(l, folder) {
    let p = this.parentOf(l);
    for (let i = 0; p && i < 64; i++) { if (p === folder.id) return true; p = this.parentOf(this.byId(p)); }
    return false;
  }

  findLayer(pred) {
    for (let i = this.layers.length - 1; i >= 0; i--) if (pred(this.layers[i])) return this.layers[i];
    return null;
  }

  async serialize(opt = {}) {
    const layers = [];
    for (const l of this.layers) layers.push(await l.serialize(opt));
    return {
      format: 'comicpad', version: 1,
      name: this.name, width: this.width, height: this.height, dpi: this.dpi,
      paper: this.paper, activeId: this.activeId, rulers: this.rulers, anim: { ...this.anim, playing: false }, print: this.print || null, notes: this.notes || [], timelapse: this.timelapse || null, aiLog: this.aiLog?.length ? this.aiLog : undefined, vtuber: this.vtuber || undefined, pixel: this.pixel || undefined, paths: this.paths?.length ? this.paths : undefined, workTime: Math.round(this.workTime || 0), layers,
    };
  }

  static async deserialize(json) {
    if (json.format !== 'comicpad') throw new Error('ComicPad 프로젝트 파일이 아닙니다');
    const doc = new Doc(json.width, json.height, { name: json.name, dpi: json.dpi });
    doc.paper = { ...doc.paper, ...json.paper };
    doc.rulers = json.rulers || [];
    doc.print = json.print || null;
    doc.notes = json.notes || [];
    doc.timelapse = json.timelapse || null;
    doc.aiLog = json.aiLog || null;
    doc.vtuber = json.vtuber || null;          // 버튜버 리그: { parts: {레이어id: {role, depth, key}}, opts }
    doc.pixel = json.pixel || null;          // 픽셀아트: { art, lock, palette }
    doc.paths = json.paths || [];
    doc.workTime = json.workTime || 0;
    if (json.anim) doc.anim = { ...doc.anim, ...json.anim, playing: false };
    for (const ld of json.layers) {
      // 예전 파일: 파란선+밑그림 레이어는 러프 레이어로 본다
      if (ld.rough === undefined && ld.draft && ld.tint) ld.rough = true;
      const l = new Layer(doc, ld);
      if (ld.data) {
        const img = await loadImage(ld.data);
        l.ctx.drawImage(img, ld.rect?.x || 0, ld.rect?.y || 0);
      }
      if (ld.mask) l.createMask(await loadImage(ld.mask));
      doc.layers.push(l);
    }
    doc.activeId = json.activeId && doc.byId(json.activeId) ? json.activeId : doc.layers.at(-1)?.id;
    return doc;
  }
}
