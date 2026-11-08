// 복구 브러시 (포토샵식): 원본의 결(무늬)을 가져오고, 색·밝기는 고칠 곳 주변에 맞춘다.
// 스팟 복구: 원본을 찍지 않아도 주변에서 가장 비슷한 곳을 골라 씀.
import { app } from '../app.js';
import { checkEditable } from '../core/ops.js';
import { createCanvas, rectUnion } from '../core/util.js';
import { slider, segmented, toggle, section } from '../ui/controls.js';
import { helpText } from './basic.js';
import { addIcon } from '../ui/icons.js';

addIcon('heal', '<rect x="3" y="9" width="18" height="6" rx="3" transform="rotate(-35 12 12)"/><path d="M10 11h4M12 9v4"/>');

function softTip(D, hardness) {
  const c = createCanvas(D, D), x = c.getContext('2d'), r = D / 2;
  const g = x.createRadialGradient(r, r, 0, r, r, r);
  g.addColorStop(0, '#fff'); g.addColorStop(Math.min(0.97, hardness), '#fff'); g.addColorStop(1, 'rgba(255,255,255,0)');
  x.fillStyle = g; x.fillRect(0, 0, D, D);
  return c;
}

/** 상자 흐림 (분리형, 여러 번 = 가우스 근사) — 한 채널 Float32 */
export function boxBlur(src, w, hgt, r, passes = 2) {
  let a = src;
  const k = 2 * r + 1, cx = (v) => (v < 0 ? 0 : v >= w ? w - 1 : v), cy = (v) => (v < 0 ? 0 : v >= hgt ? hgt - 1 : v);
  for (let p = 0; p < passes; p++) {
    const b = new Float32Array(a.length), o = new Float32Array(a.length);
    for (let y = 0; y < hgt; y++) {          // 가로
      const row = y * w;
      let s = 0;
      for (let x = -r; x <= r; x++) s += a[row + cx(x)];
      for (let x = 0; x < w; x++) { b[row + x] = s / k; s += a[row + cx(x + r + 1)] - a[row + cx(x - r)]; }
    }
    for (let x = 0; x < w; x++) {            // 세로
      let s = 0;
      for (let y = -r; y <= r; y++) s += b[cy(y) * w + x];
      for (let y = 0; y < hgt; y++) { o[y * w + x] = s / k; s += b[cy(y + r + 1) * w + x] - b[cy(y - r) * w + x]; }
    }
    a = o;
  }
  return a;
}

/**
 * 복구 계산: 원본 조각 src 의 결 + (고칠 곳 dst 의 저주파 색 − 원본의 저주파 색)
 * src, dst: RGBA ImageData 같은 크기. 반환: 새 RGBA (알파는 dst 그대로)
 */
export function healPatch(src, dst, w, hgt, radius) {
  const n = w * hgt, out = new Uint8ClampedArray(n * 4);
  const r = Math.max(1, Math.round(radius));
  for (let c = 0; c < 3; c++) {
    const S = new Float32Array(n), D = new Float32Array(n);
    for (let i = 0; i < n; i++) { S[i] = src[i * 4 + c]; D[i] = dst[i * 4 + c]; }
    const lS = boxBlur(S, w, hgt, r), lD = boxBlur(D, w, hgt, r);
    for (let i = 0; i < n; i++) out[i * 4 + c] = S[i] + (lD[i] - lS[i]);
  }
  for (let i = 0; i < n; i++) out[i * 4 + 3] = Math.max(dst[i * 4 + 3], src[i * 4 + 3]);
  return out;
}

export const healTool = {
  id: 'heal', name: '복구 브러시', icon: 'heal', key: null, cursor: 'none', allowAltPick: false,
  opt: { size: 50, hardness: 0.55, mode: 'spot', aligned: true },
  source: null, offset: null, st: null,
  get busy() { return !!this.st; },

  down(e) {
    const target = app.doc.active?.target;
    if (this.opt.mode === 'source' && (e.alt || app.modifiers.snap)) { this.source = { x: e.x, y: e.y }; this.offset = null; app.toast('원본 위치를 정했습니다'); return; }
    if (!checkEditable(target)) return;
    if (this.opt.mode === 'source' && !this.source) { app.toast('먼저 Alt+클릭으로 결을 가져올 원본을 찍으세요', 'warn'); return; }
    const D = Math.max(4, Math.round(this.opt.size));
    const snap = createCanvas(target.canvas.width, target.canvas.height);
    snap.getContext('2d').drawImage(target.canvas, 0, 0);
    if (this.opt.mode === 'source' && (!this.offset || !this.opt.aligned)) this.offset = { x: this.source.x - e.x, y: this.source.y - e.y };
    this.st = { target, D, snap, tip: softTip(D, this.opt.hardness), token: app.history.beginPixels(target), last: { x: e.x, y: e.y }, next: 0, dirty: null };
    this.dab(e.x, e.y);
    app.requestRender();
  },
  move(e) {
    const s = this.st;
    if (!s) return;
    const a = s.last, dx = e.x - a.x, dy = e.y - a.y, len = Math.hypot(dx, dy), step = Math.max(1, s.D * 0.25);
    let t = s.next;
    while (t <= len) { this.dab(a.x + dx * t / len, a.y + dy * t / len); t += step; }
    s.next = t - len; s.last = { x: e.x, y: e.y };
    s.target.touch();
  },
  up() {
    const s = this.st;
    if (!s) return;
    this.st = null;
    s.token.commit(s.dirty, this.name);
    s.target.touch();
  },
  cancel() { if (this.st) { this.st.token.cancel(); this.st = null; } },

  /** 스팟: 주변 8방향 후보 중 테두리가 가장 비슷한 곳 */
  _spotOffset(x, y, D) {
    const sx = this.st.snap.getContext('2d');
    const ring = (cx, cy) => {
      const d = sx.getImageData(Math.round(cx - D / 2), Math.round(cy - D / 2), D, D).data, v = [];
      for (let k = 0; k < 24; k++) { const a = k / 24 * Math.PI * 2, px = Math.round(D / 2 + Math.cos(a) * D * 0.45), py = Math.round(D / 2 + Math.sin(a) * D * 0.45), i = (py * D + px) * 4; v.push(d[i], d[i + 1], d[i + 2], d[i + 3]); }
      return v;
    };
    const here = ring(x, y);
    let best = null, bestE = Infinity;
    for (const dist of [1.1, 1.6]) for (let k = 0; k < 8; k++) {
      const a = k / 8 * Math.PI * 2, ox = Math.cos(a) * D * dist, oy = Math.sin(a) * D * dist;
      const cx = x + ox, cy = y + oy;
      if (cx < D / 2 || cy < D / 2 || cx > this.st.snap.width - D / 2 || cy > this.st.snap.height - D / 2) continue;
      const v = ring(cx, cy);
      let e = 0; for (let i = 0; i < v.length; i++) e += (v[i] - here[i]) ** 2;
      if (e < bestE) { bestE = e; best = { x: ox, y: oy }; }
    }
    return best || { x: D, y: 0 };
  },

  dab(x, y) {
    const s = this.st, D = s.D, P = Math.round(D / 2), B = D + P * 2;     // 둘레 여유를 두고 계산
    const X = Math.round(x - D / 2), Y = Math.round(y - D / 2);
    const off = this.opt.mode === 'source' ? this.offset : this._spotOffset(x, y, D);
    const tctx = s.target.ctx;
    const dst = tctx.getImageData(X - P, Y - P, B, B).data;
    const src = s.snap.getContext('2d').getImageData(Math.round(X - P + off.x), Math.round(Y - P + off.y), B, B).data;
    const res = healPatch(src, dst, B, B, D / 3);
    const tmp = createCanvas(B, B), tx = tmp.getContext('2d');
    tx.putImageData(new ImageData(res, B, B), 0, 0);
    // 부드러운 원 모양으로만
    const m = createCanvas(B, B), mx = m.getContext('2d');
    mx.drawImage(tmp, P, P, D, D, P, P, D, D);
    mx.globalCompositeOperation = 'destination-in';
    mx.drawImage(s.tip, P, P);
    const sel = app.doc.selection;
    if (sel.active) mx.drawImage(sel.mask, X - P, Y - P, B, B, 0, 0, B, B);
    tctx.save();
    if (s.target.alphaLock) tctx.globalCompositeOperation = 'source-atop';
    tctx.drawImage(m, X - P, Y - P);
    tctx.restore();
    s.dirty = rectUnion(s.dirty, { x: X, y: Y, w: D, h: D });
  },

  overlay(ctx, view) {
    const pt = app.pointer;
    if (pt.inside && pt.type !== 'touch') {
      const r = Math.max(2, this.opt.size / 2 * view.zoom);
      ctx.lineWidth = 1;
      ctx.strokeStyle = 'rgba(0,0,0,.6)'; ctx.beginPath(); ctx.arc(pt.sx, pt.sy, r, 0, Math.PI * 2); ctx.stroke();
      ctx.strokeStyle = 'rgba(255,255,255,.85)'; ctx.beginPath(); ctx.arc(pt.sx, pt.sy, r + 1, 0, Math.PI * 2); ctx.stroke();
    }
    if (this.opt.mode !== 'source' || !this.source) return;
    const src = this.offset ? { x: pt.x + this.offset.x, y: pt.y + this.offset.y } : this.source;
    const s = view.toScreen(src.x, src.y);
    ctx.strokeStyle = '#22a06b'; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(s.x - 8, s.y); ctx.lineTo(s.x + 8, s.y); ctx.moveTo(s.x, s.y - 8); ctx.lineTo(s.x, s.y + 8); ctx.stroke();
  },

  options(el) {
    const o = this.opt;
    el.append(section('복구 브러시',
      segmented([['spot', '스팟 복구 (자동)'], ['source', '복구 브러시 (Alt+클릭 원본)']], () => o.mode, v => { o.mode = v; this.offset = null; }),
      slider('크기', { min: 4, max: 400, step: 1, log: true, get: () => o.size, set: v => { o.size = v; } }),
      slider('경도', { min: 0, max: 0.95, step: 0.01, get: () => o.hardness, set: v => { o.hardness = v; }, format: v => Math.round(v * 100) + '%' }),
      o.mode === 'source' ? toggle('정렬 (원본 위치를 계속 따라감)', () => o.aligned, v => { o.aligned = v; this.offset = null; }) : null,
      helpText('잡티·먼지·흉터 위를 문지르면, 다른 곳의 결을 가져오되 색·밝기는 그 자리 주변에 맞춰 자연스럽게 지웁니다. 큰 부분은 선택 후 편집 > 내용 인식 채우기.'),
    ));
  },
};
