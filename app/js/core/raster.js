// 픽셀 단위 알고리즘: 채우기, 팽창/침식, 경계 계산
import { createCanvas, hexToRgb } from './util.js';

// 0/1 마스크를 정사각 커널로 팽창
export function dilate(src, w, h, r) {
  r = Math.round(r);
  if (r <= 0) return src;
  const tmp = new Uint8Array(w * h), out = new Uint8Array(w * h);
  for (let y = 0; y < h; y++) {
    const o = y * w;
    let count = 0;
    for (let x = 0; x < Math.min(r, w); x++) count += src[o + x];
    for (let x = 0; x < w; x++) {
      if (x + r < w) count += src[o + x + r];
      if (x - r - 1 >= 0) count -= src[o + x - r - 1];
      tmp[o + x] = count > 0 ? 1 : 0;
    }
  }
  for (let x = 0; x < w; x++) {
    let count = 0;
    for (let y = 0; y < Math.min(r, h); y++) count += tmp[y * w + x];
    for (let y = 0; y < h; y++) {
      if (y + r < h) count += tmp[(y + r) * w + x];
      if (y - r - 1 >= 0) count -= tmp[(y - r - 1) * w + x];
      out[y * w + x] = count > 0 ? 1 : 0;
    }
  }
  return out;
}

export function erode(src, w, h, r) {
  if (r <= 0) return src;
  const inv = new Uint8Array(w * h);
  for (let i = 0; i < inv.length; i++) inv[i] = src[i] ? 0 : 1;
  const d = dilate(inv, w, h, r);
  for (let i = 0; i < d.length; i++) d[i] = d[i] ? 0 : 1;
  return d;
}

// 스캔라인 플러드필. pass[i] 가 1 인 곳만 진행.
export function flood(pass, w, h, sx, sy) {
  const out = new Uint8Array(w * h);
  const b = { x0: sx, y0: sy, x1: sx, y1: sy };
  if (sx < 0 || sy < 0 || sx >= w || sy >= h || !pass[sy * w + sx]) return { mask: out, bounds: null };
  const stack = [sx, sy];
  while (stack.length) {
    const y = stack.pop(), x = stack.pop();
    const row = y * w;
    if (!pass[row + x] || out[row + x]) continue;
    let x0 = x, x1 = x;
    while (x0 > 0 && pass[row + x0 - 1] && !out[row + x0 - 1]) x0--;
    while (x1 < w - 1 && pass[row + x1 + 1] && !out[row + x1 + 1]) x1++;
    out.fill(1, row + x0, row + x1 + 1);
    if (x0 < b.x0) b.x0 = x0; if (x1 > b.x1) b.x1 = x1;
    if (y < b.y0) b.y0 = y; if (y > b.y1) b.y1 = y;
    for (let ny = y - 1; ny <= y + 1; ny += 2) {
      if (ny < 0 || ny >= h) continue;
      const nrow = ny * w;
      let inRun = false;
      for (let xx = x0; xx <= x1; xx++) {
        const j = nrow + xx;
        if (pass[j] && !out[j]) {
          if (!inRun) { stack.push(xx, ny); inRun = true; }
        } else inRun = false;
      }
    }
  }
  return { mask: out, bounds: { x: b.x0, y: b.y0, w: b.x1 - b.x0 + 1, h: b.y1 - b.y0 + 1 } };
}

export function maskBounds(mask, w, h) {
  let x0 = w, y0 = h, x1 = -1, y1 = -1;
  for (let y = 0; y < h; y++) {
    const row = y * w;
    for (let x = 0; x < w; x++) {
      if (mask[row + x]) {
        if (x < x0) x0 = x; if (x > x1) x1 = x;
        if (y < y0) y0 = y; y1 = y;
      }
    }
  }
  return x1 < 0 ? null : { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
}

/**
 * 색 영역 채우기 마스크를 계산한다.
 * data: ImageData (참조 이미지), (sx, sy): 시작점
 * opt.tolerance 0~255, opt.gap 틈 닫기(px), opt.expand 영역 확장(px)
 */
export function fillMask(data, sx, sy, opt = {}) {
  const { width: w, height: h, data: px } = data;
  const tol = opt.tolerance ?? 10, gap = Math.round(opt.gap ?? 0), expand = Math.round(opt.expand ?? 0);
  const n = w * h;
  const si = (sy * w + sx) * 4;
  const sa = px[si + 3];
  const sr = px[si] * sa / 255, sg = px[si + 1] * sa / 255, sb = px[si + 2] * sa / 255;
  const match = new Uint8Array(n);
  for (let i = 0, p = 0; i < n; i++, p += 4) {
    const a = px[p + 3];
    const k = a / 255;
    const d = Math.max(Math.abs(px[p] * k - sr), Math.abs(px[p + 1] * k - sg), Math.abs(px[p + 2] * k - sb), Math.abs(a - sa));
    match[i] = d <= tol ? 1 : 0;
  }
  // 틈 막기: gap(px) 너비 이하의 빈틈은 선이 이어진 것으로 본다 (선을 gap/2 만큼 두껍게 해서 막음)
  const r = Math.ceil(gap / 2);
  let pass = match;
  if (r > 0) {
    const wall = new Uint8Array(n);
    for (let i = 0; i < n; i++) wall[i] = match[i] ? 0 : 1;
    const thick = dilate(wall, w, h, r);
    pass = new Uint8Array(n);
    for (let i = 0; i < n; i++) pass[i] = match[i] && !thick[i] ? 1 : 0;
    if (!pass[sy * w + sx]) pass = match;
  }
  let { mask, bounds } = flood(pass, w, h, sx, sy);
  if (!bounds) return { mask, bounds: null };
  if (pass !== match) {
    // 두껍게 한 만큼 선 쪽으로 되돌려 채우기. 다른 영역도 동시에 자라게 해서 (먼저 닿는 쪽이 차지)
    // 틈을 지나 옆 영역으로 새어 나가지 않게 한다.
    const own = new Uint8Array(n);                 // 0 = 아직, 1 = 이 영역, 2 = 다른 영역
    let front = [];
    for (let i = 0; i < n; i++) if (pass[i]) { own[i] = mask[i] ? 1 : 2; front.push(i); }
    for (let st = 0; st <= r && front.length; st++) {
      const nf = [];
      for (const i of front) {
        const x = i % w, o = own[i];
        for (const j of [x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, i - w, i + w]) {
          if (j < 0 || j >= n || own[j] || !match[j]) continue;
          own[j] = o; nf.push(j);
        }
      }
      front = nf;
    }
    for (let i = 0; i < n; i++) if (own[i] === 1) mask[i] = 1;
  }
  if (expand > 0) mask = dilate(mask, w, h, expand);
  bounds = maskBounds(mask, w, h);
  return { mask, bounds };
}

// 마스크를 색이 칠해진 캔버스로 (bounds 크기)
export function maskToCanvas(mask, w, bounds, color = '#000000', alpha = 255) {
  const c = createCanvas(bounds.w, bounds.h);
  const ctx = c.getContext('2d');
  const img = ctx.createImageData(bounds.w, bounds.h);
  const { r, g, b } = hexToRgb(color);
  const d = img.data;
  for (let y = 0; y < bounds.h; y++) {
    const srow = (y + bounds.y) * w + bounds.x;
    for (let x = 0; x < bounds.w; x++) {
      if (mask[srow + x]) {
        const p = (y * bounds.w + x) * 4;
        d[p] = r; d[p + 1] = g; d[p + 2] = b; d[p + 3] = alpha;
      }
    }
  }
  ctx.putImageData(img, 0, 0);
  return c;
}

// 캔버스에서 불투명 픽셀의 경계
export function alphaBounds(canvas, region) {
  const r = region || { x: 0, y: 0, w: canvas.width, h: canvas.height };
  if (r.w <= 0 || r.h <= 0) return null;
  const d = canvas.getContext('2d').getImageData(r.x, r.y, r.w, r.h).data;
  let x0 = r.w, y0 = r.h, x1 = -1, y1 = -1;
  for (let y = 0; y < r.h; y++) {
    let row = y * r.w * 4 + 3;
    for (let x = 0; x < r.w; x++, row += 4) {
      if (d[row]) {
        if (x < x0) x0 = x; if (x > x1) x1 = x;
        if (y < y0) y0 = y; y1 = y;
      }
    }
  }
  return x1 < 0 ? null : { x: r.x + x0, y: r.y + y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
}

export function trimCanvas(canvas) {
  const b = alphaBounds(canvas);
  if (!b) return null;
  const c = createCanvas(b.w, b.h);
  c.getContext('2d').drawImage(canvas, b.x, b.y, b.w, b.h, 0, 0, b.w, b.h);
  return { canvas: c, bounds: b };
}

/**
 * 망점(톤화): 레이어의 어두운 정도를 점/선 망점으로. 결과는 문서 크기 캔버스.
 * o: { spacing(px), angle(도), shape: 'dot'|'line', color }
 */
export function halftone(src, o) {
  const w = src.width, h = src.height;
  const sd = src.getContext('2d').getImageData(0, 0, w, h).data;
  const out = createCanvas(w, h);
  const ox = out.getContext('2d');
  const img = ox.createImageData(w, h);
  const d = img.data;
  const { r, g, b } = hexToRgb(o.color || '#000000');
  const sp = Math.max(2, o.spacing || 8), a = (o.angle || 0) * Math.PI / 180;
  const ca = Math.cos(a) / sp, sa = Math.sin(a) / sp;
  const line = o.shape === 'line';
  for (let y = 0; y < h; y++) {
    for (let x = 0, p = y * w * 4; x < w; x++, p += 4) {
      const al = sd[p + 3];
      if (!al) continue;
      const dark = (1 - (0.299 * sd[p] + 0.587 * sd[p + 1] + 0.114 * sd[p + 2]) / 255) * al / 255;
      if (dark <= 0.01) continue;
      const u = x * ca + y * sa, v = -x * sa + y * ca;
      const fu = u - Math.floor(u) - 0.5, fv = v - Math.floor(v) - 0.5;
      const t = line ? Math.abs(fv) * 2 : (fu * fu + fv * fv) * 2;
      if (dark >= 0.98 || dark > t * (line ? 1 : 1.5708)) { d[p] = r; d[p + 1] = g; d[p + 2] = b; d[p + 3] = 255; }
    }
  }
  ox.putImageData(img, 0, 0);
  return out;
}

// ---- 워커로 채우기 영역 계산 (실패하면 화면 쪽에서 직접) ----
let fillWorker = null, fillSeq = 0;
const fillWaits = new Map();
export function fillMaskAsync(data, x, y, opt) {
  try {
    if (!fillWorker) {
      fillWorker = new Worker(new URL('./fillworker.js', import.meta.url), { type: 'module' });
      fillWorker.onmessage = (e) => {
        const w = fillWaits.get(e.data.id);
        fillWaits.delete(e.data.id);
        if (!w) return;
        if (e.data.error) w.reject(new Error(e.data.error)); else w.resolve({ mask: e.data.mask, bounds: e.data.bounds });
      };
      fillWorker.onerror = () => { for (const w of fillWaits.values()) w.fallback(); fillWaits.clear(); fillWorker = null; };
    }
    const id = ++fillSeq;
    return new Promise((resolve, reject) => {
      fillWaits.set(id, { resolve, reject, fallback: () => resolve(fillMask(data, x, y, opt)) });
      const buf = data.data.buffer.slice(0);
      fillWorker.postMessage({ id, buf, w: data.width, h: data.height, x, y, opt }, [buf]);
    });
  } catch {
    return Promise.resolve(fillMask(data, x, y, opt));
  }
}
