// 보정 도구: 복제 도장 / 흐림·선명 브러시 / 픽셀 유동화 / 유선 / 플래시
import { app } from '../app.js';
import { checkEditable, addLayer } from '../core/ops.js';
import { createCanvas, rectUnion } from '../core/util.js';
import { slider, toggle, segmented, colorInput, section } from '../ui/controls.js';
import { helpText } from './basic.js';
import { unsharpFilter } from '../core/filters.js';

function softTip(D, hardness) {
  const c = createCanvas(D, D), x = c.getContext('2d'), r = D / 2;
  const g = x.createRadialGradient(r, r, 0, r, r, r);
  g.addColorStop(0, '#fff'); g.addColorStop(Math.min(0.97, hardness), '#fff'); g.addColorStop(1, 'rgba(255,255,255,0)');
  x.fillStyle = g; x.fillRect(0, 0, D, D);
  return c;
}

function cursorCircle(ctx, view, size, extra) {
  const pt = app.pointer;
  if (!pt.inside || pt.type === 'touch') return;
  const r = Math.max(2, size / 2 * view.zoom);
  ctx.lineWidth = 1;
  ctx.strokeStyle = 'rgba(0,0,0,0.6)'; ctx.beginPath(); ctx.arc(pt.sx, pt.sy, r, 0, Math.PI * 2); ctx.stroke();
  ctx.strokeStyle = 'rgba(255,255,255,0.85)'; ctx.beginPath(); ctx.arc(pt.sx, pt.sy, r + 1, 0, Math.PI * 2); ctx.stroke();
  extra?.(pt);
}

/** 펜 경로를 따라 간격마다 dab(x, y, pressure) 호출하는 공통 도구 뼈대 */
function dabTool(def) {
  return {
    cursor: 'none',
    st: null,
    get busy() { return !!this.st; },
    down(e) {
      const target = app.doc.active?.target;
      if (!checkEditable(target)) return;
      const D = Math.max(2, Math.round(this.opt.size));
      this.st = { target, D, token: app.history.beginPixels(target), last: { x: e.x, y: e.y }, next: 0, dirty: null, tip: softTip(D, this.opt.hardness ?? 0.4), tmp: createCanvas(D, D) };
      if (this.begin?.(e) === false) { this.st.token.commit(null); this.st = null; return; }
      this.dab(e.x, e.y, e.pressure || 1, 0, 0);
      app.requestRender();
    },
    move(e) {
      const s = this.st;
      if (!s) return;
      const a = s.last, dx = e.x - a.x, dy = e.y - a.y, len = Math.hypot(dx, dy);
      const step = Math.max(1, s.D * (this.spacing || 0.15));
      let t = s.next;
      while (t <= len) { this.dab(a.x + dx * t / len, a.y + dy * t / len, e.pressure || 1, dx / (len || 1), dy / (len || 1)); t += step; }
      s.next = t - len;
      s.last = { x: e.x, y: e.y };
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
    mark(r) { this.st.dirty = rectUnion(this.st.dirty, r); },
    /** tmp 캔버스 내용을 부드러운 원 모양으로 잘라 (x,y) 에 그린다 */
    stamp(x, y, alpha) {
      const s = this.st, D = s.D, X = Math.round(x - D / 2), Y = Math.round(y - D / 2);
      const t = s.tmp.getContext('2d');
      t.globalCompositeOperation = 'destination-in';
      t.drawImage(s.tip, 0, 0);
      const sel = app.doc.selection;
      if (sel.active) t.drawImage(sel.mask, X, Y, D, D, 0, 0, D, D);
      t.globalCompositeOperation = 'source-over';
      const ctx = s.target.ctx;
      ctx.save();
      ctx.globalAlpha = alpha;
      if (s.target.alphaLock) ctx.globalCompositeOperation = 'source-atop';
      ctx.drawImage(s.tmp, X, Y);
      ctx.restore();
      this.mark({ x: X, y: Y, w: D, h: D });
    },
    overlay(ctx, view) { cursorCircle(ctx, view, this.opt.size, this.extraOverlay?.bind(this, view)); },
    ...def,
  };
}

// =====================================================================
// 복제 도장
// =====================================================================
export const cloneTool = dabTool({
  id: 'clone', name: '복제 도장', icon: 'clone', key: 's', allowAltPick: false,
  opt: { size: 60, hardness: 0.5, opacity: 1, aligned: true, sampleAll: false },
  source: null, offset: null,
  spacing: 0.1,
  begin(e) {
    if (e.alt || app.modifiers.snap) {
      this.source = { x: e.x, y: e.y };
      this.offset = null;
      app.toast('복제할 원본 위치를 정했습니다');
      return false;
    }
    if (!this.source) { app.toast('먼저 Alt+클릭(또는 퀵바 "각도 스냅" 켜고 탭)으로 복제할 원본을 찍으세요', 'warn'); return false; }
    if (!this.offset || !this.opt.aligned) this.offset = { x: this.source.x - e.x, y: this.source.y - e.y };
    // 원본: 시작할 때 모습으로 고정 (방금 찍은 걸 또 복제하지 않도록)
    const doc = app.doc;
    const snap = createCanvas(doc.width, doc.height);
    const sx = snap.getContext('2d');
    if (this.opt.sampleAll) app.compositor.render(sx, doc, new DOMMatrix(), { paper: true });
    else sx.drawImage(this.st.target.canvas, 0, 0);
    this.st.src = snap;
  },
  dab(x, y, p) {
    const s = this.st, D = s.D;
    const t = s.tmp.getContext('2d');
    t.globalCompositeOperation = 'copy';
    t.drawImage(s.src, Math.round(x + this.offset.x - D / 2), Math.round(y + this.offset.y - D / 2), D, D, 0, 0, D, D);
    this.stamp(x, y, this.opt.opacity * Math.max(0.2, p));
  },
  overlay(ctx, view) {
    cursorCircle(ctx, view, this.opt.size);
    if (!this.source) return;
    const src = this.offset ? { x: app.pointer.x + this.offset.x, y: app.pointer.y + this.offset.y } : this.source;
    const s = view.toScreen(src.x, src.y);
    ctx.strokeStyle = '#2f7cf6'; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(s.x - 8, s.y); ctx.lineTo(s.x + 8, s.y); ctx.moveTo(s.x, s.y - 8); ctx.lineTo(s.x, s.y + 8); ctx.stroke();
    ctx.beginPath(); ctx.arc(s.x, s.y, this.opt.size / 2 * view.zoom, 0, Math.PI * 2); ctx.stroke();
  },
  options(el) {
    const o = this.opt;
    el.append(section('복제 도장',
      slider('크기', { min: 4, max: 400, step: 1, log: true, get: () => o.size, set: v => { o.size = v; } }),
      slider('경도', { min: 0, max: 0.97, step: 0.01, get: () => o.hardness, set: v => { o.hardness = v; }, format: v => Math.round(v * 100) + '%' }),
      slider('불투명도', { min: 0.05, max: 1, step: 0.01, get: () => o.opacity, set: v => { o.opacity = v; }, format: v => Math.round(v * 100) + '%' }),
      toggle('정렬 (원본 위치를 계속 따라감)', () => o.aligned, v => { o.aligned = v; this.offset = null; }),
      toggle('보이는 그림 전체에서 복제', () => o.sampleAll, v => { o.sampleAll = v; }),
      helpText('Alt+클릭으로 원본 지정 → 고칠 곳을 칠하면 원본이 복사됩니다. 터치만 쓸 땐 퀵바 "각도 스냅"을 켜고 탭하면 원본 지정.'),
    ));
  },
});

// =====================================================================
// 흐림 / 선명 브러시
// =====================================================================
export const blurTool = dabTool({
  id: 'blurbrush', name: '흐림/선명 브러시', icon: 'blurbrush', key: null,
  opt: { size: 50, hardness: 0.3, strength: 0.5, mode: 'blur' },
  spacing: 0.25,
  dab(x, y, p) {
    const s = this.st, D = s.D, pad = 8, B = D + pad * 2;
    if (!s.big || s.big.width !== B) s.big = createCanvas(B, B);
    const bx = s.big.getContext('2d');
    bx.clearRect(0, 0, B, B);
    bx.filter = this.opt.mode === 'blur' ? `blur(${Math.max(0.5, D / 12 * this.opt.strength * 2)}px)` : unsharpFilter(1.2, this.opt.strength * 2.5, B, B);
    bx.drawImage(s.target.canvas, Math.round(x - B / 2), Math.round(y - B / 2), B, B, 0, 0, B, B);
    bx.filter = 'none';
    const t = s.tmp.getContext('2d');
    t.globalCompositeOperation = 'copy';
    t.drawImage(s.big, pad, pad, D, D, 0, 0, D, D);
    this.stamp(x, y, Math.min(1, 0.35 + this.opt.strength * 0.5) * Math.max(0.2, p));
  },
  options(el) {
    const o = this.opt;
    el.append(section('흐림 / 선명',
      segmented([['blur', '흐리게'], ['sharp', '선명하게']], () => o.mode, v => { o.mode = v; }),
      slider('크기', { min: 4, max: 400, step: 1, log: true, get: () => o.size, set: v => { o.size = v; } }),
      slider('세기', { min: 0.05, max: 1, step: 0.01, get: () => o.strength, set: v => { o.strength = v; }, format: v => Math.round(v * 100) + '%' }),
      helpText('문지른 곳만 흐리거나 선명하게. 배경 거리감, 피부결 정리, 초점 강조에.'),
    ));
  },
});

// =====================================================================
// 픽셀 유동화
// =====================================================================
export const liquifyTool = dabTool({
  id: 'liquify', name: '픽셀 유동화', icon: 'liquify', key: null,
  opt: { size: 120, strength: 0.6, mode: 'push' },
  spacing: 0.12,
  dab(x, y, p, ux, uy) {
    const s = this.st, R = Math.round(s.D / 2), D = R * 2;
    const X = Math.round(x) - R, Y = Math.round(y) - R;
    const W = s.target.canvas.width, H = s.target.canvas.height;
    const x0 = Math.max(0, X), y0 = Math.max(0, Y), x1 = Math.min(W, X + D), y1 = Math.min(H, Y + D);
    if (x1 <= x0 || y1 <= y0) return;
    const ctx = s.target.ctx;
    const src = ctx.getImageData(x0, y0, x1 - x0, y1 - y0);
    const out = new ImageData(new Uint8ClampedArray(src.data), src.width, src.height);
    const sw = src.width, sh = src.height, sd = src.data, od = out.data;
    const st = this.opt.strength * Math.max(0.3, p), mode = this.opt.mode, step = s.D * (this.spacing || 0.12);
    const sel = app.doc.selection;
    const md = sel.active ? sel.mask.getContext('2d').getImageData(x0, y0, sw, sh).data : null;
    for (let j = 0; j < sh; j++) {
      for (let i = 0; i < sw; i++) {
        const dx = x0 + i - x, dy = y0 + j - y, d2 = dx * dx + dy * dy;
        if (d2 >= R * R) continue;
        if (md && md[(j * sw + i) * 4 + 3] < 128) continue;
        const f = 1 - Math.sqrt(d2) / R, w = f * f * (3 - 2 * f) * st;
        let sx, sy;
        if (mode === 'push') { sx = dx - ux * step * w * 1.6; sy = dy - uy * step * w * 1.6; }
        else if (mode === 'bloat') { sx = dx * (1 - w * 0.25); sy = dy * (1 - w * 0.25); }
        else if (mode === 'pinch') { sx = dx * (1 + w * 0.25); sy = dy * (1 + w * 0.25); }
        else { const a = w * 0.25 * (mode === 'twirlL' ? -1 : 1), c = Math.cos(a), sn = Math.sin(a); sx = dx * c - dy * sn; sy = dx * sn + dy * c; }
        // 양선형 보간으로 원래 그림에서 가져오기
        const fx = x + sx - x0, fy = y + sy - y0;
        const ix = Math.floor(fx), iy = Math.floor(fy), tx = fx - ix, ty = fy - iy;
        if (ix < 0 || iy < 0 || ix >= sw - 1 || iy >= sh - 1) continue;
        const a00 = (iy * sw + ix) * 4, a10 = a00 + 4, a01 = a00 + sw * 4, a11 = a01 + 4, o = (j * sw + i) * 4;
        for (let c = 0; c < 4; c++) {
          od[o + c] = (sd[a00 + c] * (1 - tx) + sd[a10 + c] * tx) * (1 - ty) + (sd[a01 + c] * (1 - tx) + sd[a11 + c] * tx) * ty;
        }
      }
    }
    ctx.putImageData(out, x0, y0);
    this.mark({ x: x0, y: y0, w: sw, h: sh });
  },
  options(el) {
    const o = this.opt;
    el.append(section('픽셀 유동화',
      segmented([['push', '밀기'], ['bloat', '부풀리기'], ['pinch', '오므리기'], ['twirlR', '오른쪽 회전'], ['twirlL', '왼쪽 회전']], () => o.mode, v => { o.mode = v; }),
      slider('크기', { min: 10, max: 400, step: 1, log: true, get: () => o.size, set: v => { o.size = v; } }),
      slider('세기', { min: 0.05, max: 1, step: 0.01, get: () => o.strength, set: v => { o.strength = v; }, format: v => Math.round(v * 100) + '%' }),
      helpText('얼굴형·눈 크기·옷 주름 등을 손으로 밀어서 고칩니다. 부풀리기/오므리기/회전은 제자리에서 누르고 있거나 문지르세요.'),
    ));
  },
});

// =====================================================================
// 유선 (평행 효과선)
// =====================================================================
export const speedLinesTool = {
  id: 'speedlines', name: '유선', icon: 'speedlines', key: null, cursor: 'crosshair',
  opt: { gap: 14, width: 3, jitter: 0.6, lengthMin: 0.4, color: '#000000', newLayer: true },
  drag: null,
  down(e) { this.drag = { x0: e.x, y0: e.y, x1: e.x, y1: e.y }; this.busy = true; },
  move(e) { if (this.drag) { this.drag.x1 = e.x; this.drag.y1 = e.y; app.requestRender(); } },
  up() {
    const d = this.drag;
    this.drag = null; this.busy = false;
    if (!d) return;
    const L = Math.hypot(d.x1 - d.x0, d.y1 - d.y0);
    if (L < 20) { app.toast('선 방향으로 길게 드래그하세요'); return; }
    const doc = app.doc, o = this.opt;
    const ux = (d.x1 - d.x0) / L, uy = (d.y1 - d.y0) / L, nx = -uy, ny = ux;
    const sel = doc.selection;
    const area = sel.active ? sel.bounds : { x: 0, y: 0, w: doc.width, h: doc.height };
    const cx = area.x + area.w / 2, cy = area.y + area.h / 2, span = Math.hypot(area.w, area.h);
    app.history.batch('유선', () => {
      const layer = o.newLayer ? addLayer({ name: '유선' }) : doc.active;
      if (!checkEditable(layer)) return;
      app.history.pixels(layer, '유선', (ctx) => {
        ctx.save();
        ctx.fillStyle = o.color;
        for (let k = -span / 2; k < span / 2; k += o.gap * (0.6 + Math.random() * 0.8)) {
          const len = span * (o.lengthMin + Math.random() * (1 - o.lengthMin)) * 0.6;
          const along = (Math.random() - 0.5) * span * o.jitter;
          const mx = cx + nx * k + ux * along, my = cy + ny * k + uy * along;
          const w = o.width * (0.4 + Math.random() * 0.9);
          // 양끝이 뾰족한 가는 마름모
          ctx.beginPath();
          ctx.moveTo(mx - ux * len / 2, my - uy * len / 2);
          ctx.lineTo(mx + nx * w / 2, my + ny * w / 2);
          ctx.lineTo(mx + ux * len / 2, my + uy * len / 2);
          ctx.lineTo(mx - nx * w / 2, my - ny * w / 2);
          ctx.closePath();
          ctx.fill();
        }
        if (sel.active) { ctx.globalCompositeOperation = 'destination-in'; ctx.drawImage(sel.mask, 0, 0); }
        ctx.restore();
      });
    });
  },
  cancel() { this.drag = null; this.busy = false; },
  overlay(ctx, view) {
    const d = this.drag;
    if (!d) return;
    const a = view.toScreen(d.x0, d.y0), b = view.toScreen(d.x1, d.y1);
    ctx.strokeStyle = '#e8484a'; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
  },
  options(el) {
    const o = this.opt;
    el.append(section('유선',
      slider('간격', { min: 3, max: 80, step: 1, get: () => o.gap, set: v => { o.gap = v; }, format: v => v + 'px' }),
      slider('굵기', { min: 0.5, max: 20, step: 0.5, get: () => o.width, set: v => { o.width = v; } }),
      slider('위치 흩어짐', { min: 0, max: 1, step: 0.01, get: () => o.jitter, set: v => { o.jitter = v; }, format: v => Math.round(v * 100) + '%' }),
      slider('최소 길이', { min: 0.05, max: 1, step: 0.01, get: () => o.lengthMin, set: v => { o.lengthMin = v; }, format: v => Math.round(v * 100) + '%' }),
      colorInput('색', () => o.color, v => { o.color = v; }),
      toggle('새 레이어에 그리기', () => o.newLayer, v => { o.newLayer = v; }),
      helpText('선이 향할 방향으로 드래그. 선택 영역이 있으면 그 안에만 그립니다 (속도감·배경 효과).'),
    ));
  },
};

// =====================================================================
// 플래시 (우니 플래시 / 베타 플래시)
// =====================================================================
export async function flashDialog() {
  const doc = app.doc, sel = doc.selection;
  const b = sel.active ? sel.bounds : { x: doc.width * 0.3, y: doc.height * 0.3, w: doc.width * 0.4, h: doc.height * 0.3 };
  const v = await app.ui.form('플래시 효과', [
    { key: 'style', label: '종류', type: 'segment', options: [['uni', '우니 플래시 (흰 가시 + 테두리)'], ['beta', '베타 플래시 (검은 가시)'], ['burst', '폭발 (안쪽 흰 + 바깥 검정)']], value: 'uni' },
    { key: 'count', label: '가시 개수', type: 'number', value: 90 },
    { key: 'len', label: '가시 길이 (%)', type: 'number', value: 70 },
    { key: 'jitter', label: '길이 들쭉날쭉 (%)', type: 'number', value: 45 },
    { key: 'info', type: 'info', label: '선택 영역이 있으면 그 크기의 타원 둘레에, 없으면 화면 가운데에 그립니다. 새 레이어에 만들어집니다.' },
  ], { okLabel: '그리기' });
  if (!v) return;
  const cx = b.x + b.w / 2, cy = b.y + b.h / 2, rx = b.w / 2, ry = b.h / 2;
  app.history.batch('플래시', () => {
    const layer = addLayer({ name: '플래시' });
    app.history.pixels(layer, '플래시', (ctx) => {
      ctx.save();
      const spikes = [];
      for (let i = 0; i < v.count; i++) {
        const a = (i / v.count) * Math.PI * 2 + (Math.random() - 0.5) * 0.04;
        const L = Math.max(rx, ry) * (v.len / 100) * (1 - Math.random() * v.jitter / 100);
        const w = (Math.PI * 2 * Math.max(rx, ry) / v.count) * (0.35 + Math.random() * 0.3);
        const bx = cx + Math.cos(a) * rx, by = cy + Math.sin(a) * ry;
        const nx = -Math.sin(a), ny = Math.cos(a);
        spikes.push([[bx + nx * w, by + ny * w], [bx + Math.cos(a) * L, by + Math.sin(a) * L], [bx - nx * w, by - ny * w]]);
      }
      const path = (p) => { ctx.beginPath(); ctx.moveTo(...p[0]); ctx.lineTo(...p[1]); ctx.lineTo(...p[2]); ctx.closePath(); };
      if (v.style === 'beta') {
        ctx.fillStyle = '#000';
        for (const s of spikes) { path(s); ctx.fill(); }
      } else {
        ctx.lineJoin = 'miter';
        ctx.strokeStyle = '#000'; ctx.lineWidth = Math.max(2, doc.width / 600) * 2;
        for (const s of spikes) { path(s); ctx.stroke(); }
        ctx.fillStyle = '#fff';
        for (const s of spikes) { path(s); ctx.fill(); }
        ctx.beginPath(); ctx.ellipse(cx, cy, rx * 1.02, ry * 1.02, 0, 0, Math.PI * 2); ctx.fill();
        if (v.style === 'burst') {
          ctx.globalCompositeOperation = 'destination-over';
          ctx.fillStyle = '#000';
          ctx.beginPath(); ctx.ellipse(cx, cy, rx + Math.max(rx, ry) * v.len / 100 * 1.1, ry + Math.max(rx, ry) * v.len / 100 * 1.1, 0, 0, Math.PI * 2); ctx.fill();
        }
      }
      ctx.restore();
    });
  });
}
