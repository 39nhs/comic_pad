// 픽셀아트 도우미: 디더 무늬(Bayer), 팔레트(가까운 색·색 수 줄이기·명암 단계), 팔레트 파일(.gpl/.hex)
import { app } from '../app.js';
import { createCanvas } from './util.js';

// ---------------------------------------------------------------------
// 디더 (Bayer 행렬)
// ---------------------------------------------------------------------
export const BAYER = {
  2: [[0, 2], [3, 1]],
  4: [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]],
};
/** (x, y) 칸의 문턱값 0~1 (이 값보다 농도가 크면 칠함) */
export function bayerAt(x, y, n = 4) {
  const m = BAYER[n] || BAYER[4];
  return (m[((y % n) + n) % n][((x % n) + n) % n] + 0.5) / (n * n);
}
const ditherCache = new Map();
/** 농도 level(0~1) 의 디더 무늬 한 칸 (불투명 = 칠할 곳) */
export function ditherTile(level, n = 4) {
  const key = `${n}:${level.toFixed(3)}`;
  if (ditherCache.has(key)) return ditherCache.get(key);
  const c = createCanvas(n, n), x = c.getContext('2d'), im = x.createImageData(n, n);
  for (let yy = 0; yy < n; yy++) for (let xx = 0; xx < n; xx++) if (level > bayerAt(xx, yy, n) - 1e-6) im.data[(yy * n + xx) * 4 + 3] = 255;
  x.putImageData(im, 0, 0);
  ditherCache.set(key, c);
  return c;
}
/** 캔버스(rect 안)에 디더 무늬를 씌워 칸을 비움 — 문서 좌표 기준이라 획을 겹쳐도 무늬가 어긋나지 않음 */
export function applyDither(ctx, level, n, rect, ox = 0, oy = 0) {
  if (!(level > 0 && level < 1)) return;
  const pat = ctx.createPattern(ditherTile(level, n), 'repeat');
  pat.setTransform(new DOMMatrix().translate(-ox, -oy));      // 캔버스 (0,0) 이 문서의 (ox, oy)
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.globalCompositeOperation = 'destination-in';
  ctx.imageSmoothingEnabled = false;
  ctx.fillStyle = pat;
  ctx.fillRect(rect.x, rect.y, rect.w, rect.h);
  ctx.restore();
}

// ---------------------------------------------------------------------
// 팔레트
// ---------------------------------------------------------------------
const hex2 = (v) => v.toString(16).padStart(2, '0');
export const toHex = (r, g, b) => `#${hex2(r)}${hex2(g)}${hex2(b)}`;
export function parseHex(h) {
  const s = String(h).replace('#', '');
  const v = parseInt(s.length === 3 ? s.split('').map(c => c + c).join('') : s.slice(0, 6), 16);
  return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
}
/** 이 작품이 쓰는 팔레트 (작품 팔레트가 없으면 앱 팔레트) */
export function paletteOf(doc = app.doc) {
  const p = doc?.pixel?.palette?.length ? doc.pixel.palette : app.settings.palette;
  return (p || []).map(parseHex);
}
export const lum = ([r, g, b]) => r * 0.299 + g * 0.587 + b * 0.114;

/** 가까운 색 (사람 눈에 맞춘 가중 거리) → 팔레트 번호 */
export function nearestIndex(r, g, b, pal) {
  let best = 0, bd = Infinity;
  for (let i = 0; i < pal.length; i++) {
    const c = pal[i], rm = (r + c[0]) / 2;
    const dr = r - c[0], dg = g - c[1], db = b - c[2];
    const d = (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
    if (d < bd) { bd = d; best = i; }
  }
  return best;
}
export function nearestHex(hex, pal = paletteOf()) {
  if (!pal.length) return hex;
  const [r, g, b] = parseHex(hex);
  const c = pal[nearestIndex(r, g, b, pal)];
  return toHex(...c);
}

/** rect 안 픽셀을 팔레트 색으로 (알파는 반 넘으면 불투명, 아니면 투명 = 계단 없는 픽셀) */
export function snapToPalette(ctx, rect, pal, { binaryAlpha = true } = {}) {
  if (!pal.length || !rect || rect.w <= 0 || rect.h <= 0) return;
  const x0 = Math.max(0, Math.floor(rect.x)), y0 = Math.max(0, Math.floor(rect.y));
  const w = Math.min(ctx.canvas.width - x0, Math.ceil(rect.w) + 1), hh = Math.min(ctx.canvas.height - y0, Math.ceil(rect.h) + 1);
  if (w <= 0 || hh <= 0) return;
  const img = ctx.getImageData(x0, y0, w, hh), d = img.data;
  const cache = new Map();
  for (let i = 0; i < d.length; i += 4) {
    if (!d[i + 3]) continue;
    if (binaryAlpha) { if (d[i + 3] < 128) { d[i + 3] = 0; continue; } d[i + 3] = 255; }
    const key = (d[i] << 16) | (d[i + 1] << 8) | d[i + 2];
    let k = cache.get(key);
    if (k == null) { k = nearestIndex(d[i], d[i + 1], d[i + 2], pal); cache.set(key, k); }
    d[i] = pal[k][0]; d[i + 1] = pal[k][1]; d[i + 2] = pal[k][2];
  }
  ctx.putImageData(img, x0, y0);
}

/** 미디언 컷: 이미지 색을 n 가지로 대표 (투명 픽셀은 뺌) */
export function medianCut(data, n) {
  const px = [];
  const step = Math.max(1, Math.floor(data.length / 4 / 200000));     // 큰 그림은 골고루 뽑아서
  for (let i = 0; i < data.length; i += 4 * step) if (data[i + 3] > 127) px.push([data[i], data[i + 1], data[i + 2]]);
  if (!px.length) return [];
  let boxes = [px];
  while (boxes.length < n) {
    // 가장 넓게 퍼진 상자를 가장 넓은 채널로 반 나누기
    let bi = -1, bw = -1, bc = 0;
    boxes.forEach((b, i) => {
      if (b.length < 2) return;
      for (let c = 0; c < 3; c++) {
        let lo = 255, hi = 0;
        for (const p of b) { if (p[c] < lo) lo = p[c]; if (p[c] > hi) hi = p[c]; }
        if (hi - lo > bw) { bw = hi - lo; bi = i; bc = c; }
      }
    });
    if (bi < 0 || bw <= 0) break;
    const b = boxes[bi].sort((a, c) => a[bc] - c[bc]), mid = b.length >> 1;
    boxes.splice(bi, 1, b.slice(0, mid), b.slice(mid));
  }
  return boxes.map(b => { const s = [0, 0, 0]; for (const p of b) for (let c = 0; c < 3; c++) s[c] += p[c]; return s.map(v => Math.round(v / b.length)); });
}

/** ImageData 를 팔레트 색으로 바꾸기. dither: 'none' | 'bayer2' | 'bayer4' | 'fs' (Floyd–Steinberg) */
export function quantizeImage(img, pal, dither = 'none', strength = 1) {
  const { width: w, height: hh, data: d } = img;
  if (!pal.length) return img;
  if (dither === 'fs') {
    const err = new Float32Array(w * hh * 3);
    for (let y = 0; y < hh; y++) for (let x = 0; x < w; x++) {
      const i = y * w + x, p = i * 4;
      if (d[p + 3] < 128) { d[p + 3] = 0; continue; }
      d[p + 3] = 255;
      const r = d[p] + err[i * 3], g = d[p + 1] + err[i * 3 + 1], b = d[p + 2] + err[i * 3 + 2];
      const c = pal[nearestIndex(Math.max(0, Math.min(255, r)), Math.max(0, Math.min(255, g)), Math.max(0, Math.min(255, b)), pal)];
      d[p] = c[0]; d[p + 1] = c[1]; d[p + 2] = c[2];
      const er = (r - c[0]) * strength, eg = (g - c[1]) * strength, eb = (b - c[2]) * strength;
      const spread = (xx, yy, k) => { if (xx < 0 || xx >= w || yy >= hh) return; const j = (yy * w + xx) * 3; err[j] += er * k; err[j + 1] += eg * k; err[j + 2] += eb * k; };
      spread(x + 1, y, 7 / 16); spread(x - 1, y + 1, 3 / 16); spread(x, y + 1, 5 / 16); spread(x + 1, y + 1, 1 / 16);
    }
    return img;
  }
  const n = dither === 'bayer2' ? 2 : dither === 'bayer4' ? 4 : 0;
  const amp = n ? 48 * strength : 0;
  for (let y = 0; y < hh; y++) for (let x = 0; x < w; x++) {
    const p = (y * w + x) * 4;
    if (d[p + 3] < 128) { d[p + 3] = 0; continue; }
    d[p + 3] = 255;
    const o = n ? (bayerAt(x, y, n) - 0.5) * amp : 0;
    const c = pal[nearestIndex(Math.max(0, Math.min(255, d[p] + o)), Math.max(0, Math.min(255, d[p + 1] + o)), Math.max(0, Math.min(255, d[p + 2] + o)), pal)];
    d[p] = c[0]; d[p + 1] = c[1]; d[p + 2] = c[2];
  }
  return img;
}

/** 색상(0~360)·채도(0~1) */
function hueSat([r, g, b]) {
  const mx = Math.max(r, g, b), mn = Math.min(r, g, b), d = mx - mn;
  const sat = mx ? d / mx : 0;
  let hue = 0;
  if (d) { hue = mx === r ? ((g - b) / d) % 6 : mx === g ? (b - r) / d + 2 : (r - g) / d + 4; hue = (hue * 60 + 360) % 360; }
  return [hue, sat];
}
/**
 * 명암 잉크: 팔레트에서 같은 계열(비슷한 색상, 회색은 회색끼리) 색만 밝기 순으로 세운 사다리에서 한 칸 위/아래.
 * 팔레트에 없는 색이면 null
 */
export function shadeStep(r, g, b, pal, dir = -1) {
  const me = pal.findIndex(c => c[0] === r && c[1] === g && c[2] === b);
  if (me < 0) return null;
  const [h0, s0] = hueSat(pal[me]);
  const gray = s0 < 0.15;
  const ramp = pal.filter((c, i) => {
    if (i === me) return true;
    const [hh, ss] = hueSat(c);
    if (gray) return ss < 0.15;
    if (ss < 0.15) return false;
    const dh = Math.abs(hh - h0);
    return Math.min(dh, 360 - dh) <= 40;
  }).map(c => ({ c, l: lum(c) })).sort((a, c) => a.l - c.l);
  const at = ramp.findIndex(e => e.c === pal[me]);
  const j = Math.max(0, Math.min(ramp.length - 1, at + (dir < 0 ? -1 : 1)));
  return ramp[j].c;
}

/** 한 색을 다른 색으로 (허용 오차 안) — 바꾼 픽셀 수 */
export function replaceColor(img, from, to, tol = 0) {
  const d = img.data;
  let n = 0;
  for (let i = 0; i < d.length; i += 4) {
    if (!d[i + 3]) continue;
    if (Math.abs(d[i] - from[0]) <= tol && Math.abs(d[i + 1] - from[1]) <= tol && Math.abs(d[i + 2] - from[2]) <= tol) {
      d[i] = to[0]; d[i + 1] = to[1]; d[i + 2] = to[2]; n++;
    }
  }
  return n;
}

// ---------------------------------------------------------------------
// 팔레트 파일
// ---------------------------------------------------------------------
/** .gpl (GIMP/Aseprite), .hex (Lospec), .txt(Paint.NET) → ['#rrggbb', ...] */
export function parsePaletteFile(text, name = '') {
  const out = [];
  if (/^GIMP Palette/m.test(text) || /\.gpl$/i.test(name)) {
    for (const line of text.split(/\r?\n/)) {
      const m = line.trim().match(/^(\d{1,3})\s+(\d{1,3})\s+(\d{1,3})/);
      if (m) out.push(toHex(+m[1], +m[2], +m[3]));
    }
  } else {
    for (const line of text.split(/\r?\n/)) {
      const t = line.trim();
      if (!t || t.startsWith(';') || t.startsWith('//')) continue;
      const m = t.match(/#?([0-9a-f]{8}|[0-9a-f]{6})\b/i);
      if (m) out.push('#' + m[1].slice(-6).toLowerCase());
    }
  }
  return [...new Set(out)];
}
export function paletteToGpl(colors, name = 'ComicPad') {
  return `GIMP Palette\nName: ${name}\nColumns: 8\n#\n` + colors.map(c => { const [r, g, b] = parseHex(c); return `${String(r).padStart(3)} ${String(g).padStart(3)} ${String(b).padStart(3)}\t${c}`; }).join('\n') + '\n';
}
export const paletteToHex = (colors) => colors.map(c => c.replace('#', '')).join('\n') + '\n';
