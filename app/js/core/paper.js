// 용지 질감: 용지 색 위에 종이 결을 곱하기로 얹는다 (화면·내보내기 모두, 용지를 켠 경우)
import { createCanvas } from './util.js';

export const PAPER_TYPES = [['none', '없음'], ['fine', '고운 종이'], ['rough', '거친 수채지'], ['canvas', '캔버스 천'], ['kraft', '섬유 종이']];
const SIZE = 512;
const cache = new Map();

/** 이어 붙여도 자국이 안 나는 값 노이즈 (0~1) */
function tileNoise(size, cells, seed) {
  const g = new Float32Array(cells * cells);
  let s = seed;
  const rnd = () => ((s = Math.imul(s ^ (s >>> 15), 2246822519) + 0x9e3779b9 | 0) >>> 0) / 4294967296;
  for (let i = 0; i < g.length; i++) g[i] = rnd();
  const out = new Float32Array(size * size);
  const f = (t) => t * t * (3 - 2 * t);
  for (let y = 0; y < size; y++) {
    const gy = (y / size) * cells, y0 = Math.floor(gy), ty = f(gy - y0);
    for (let x = 0; x < size; x++) {
      const gx = (x / size) * cells, x0 = Math.floor(gx), tx = f(gx - x0);
      const a = g[(y0 % cells) * cells + (x0 % cells)], b = g[(y0 % cells) * cells + ((x0 + 1) % cells)];
      const c = g[((y0 + 1) % cells) * cells + (x0 % cells)], d = g[((y0 + 1) % cells) * cells + ((x0 + 1) % cells)];
      out[y * size + x] = (a + (b - a) * tx) + ((c + (d - c) * tx) - (a + (b - a) * tx)) * ty;
    }
  }
  return out;
}

/** 질감 타일 (흰색 = 변화 없음, 어두울수록 결이 진함) */
export function paperTile(type) {
  if (cache.has(type)) return cache.get(type);
  const c = createCanvas(SIZE, SIZE), x = c.getContext('2d');
  const im = x.createImageData(SIZE, SIZE), d = im.data;
  const oct = (list) => { const acc = new Float32Array(SIZE * SIZE); let tw = 0; for (const [cells, w, seed] of list) { const n = tileNoise(SIZE, cells, seed); for (let i = 0; i < acc.length; i++) acc[i] += n[i] * w; tw += w; } for (let i = 0; i < acc.length; i++) acc[i] /= tw; return acc; };
  let v;
  if (type === 'fine') v = oct([[128, 1, 11], [256, 1, 12], [32, 0.4, 13]]).map(n => 1 - Math.max(0, n - 0.35) * 0.35);
  else if (type === 'rough') v = oct([[8, 1.2, 21], [32, 1, 22], [128, 0.8, 23], [256, 0.5, 24]]).map(n => 1 - Math.pow(Math.max(0, n - 0.3), 1.5) * 0.7);
  else if (type === 'canvas') {
    const n = oct([[64, 1, 31], [256, 0.6, 32]]);
    v = new Float32Array(SIZE * SIZE);
    for (let y = 0; y < SIZE; y++) for (let xx = 0; xx < SIZE; xx++) {
      const w = (Math.sin(xx / SIZE * Math.PI * 2 * 64) * Math.sin(y / SIZE * Math.PI * 2 * 64) + 1) / 2;
      v[y * SIZE + xx] = 1 - (w * 0.18 + n[y * SIZE + xx] * 0.12);
    }
  } else if (type === 'kraft') {
    v = oct([[16, 1, 41], [64, 0.7, 42]]).map(n => 1 - n * 0.12);
  } else return null;
  for (let i = 0; i < v.length; i++) { const g = Math.round(Math.max(0, Math.min(1, v[i])) * 255); d[i * 4] = d[i * 4 + 1] = d[i * 4 + 2] = g; d[i * 4 + 3] = 255; }
  x.putImageData(im, 0, 0);
  if (type === 'kraft') {
    // 섬유: 짧은 곡선을 여기저기 (이어 붙임 자리까지 감싸도록 9번 찍기)
    let s = 7;
    const rnd = () => ((s = Math.imul(s, 1103515245) + 12345 | 0) >>> 0) / 4294967296;
    x.lineCap = 'round';
    for (let i = 0; i < 260; i++) {
      const x0 = rnd() * SIZE, y0 = rnd() * SIZE, a = rnd() * Math.PI * 2, L = 10 + rnd() * 40, bend = (rnd() - 0.5) * 20;
      x.strokeStyle = `rgba(90,70,50,${0.05 + rnd() * 0.12})`;
      x.lineWidth = 0.6 + rnd() * 1.2;
      for (const ox of [-SIZE, 0, SIZE]) for (const oy of [-SIZE, 0, SIZE]) {
        x.beginPath();
        x.moveTo(x0 + ox, y0 + oy);
        x.quadraticCurveTo(x0 + ox + Math.cos(a) * L / 2 - Math.sin(a) * bend, y0 + oy + Math.sin(a) * L / 2 + Math.cos(a) * bend, x0 + ox + Math.cos(a) * L, y0 + oy + Math.sin(a) * L);
        x.stroke();
      }
    }
  }
  cache.set(type, c);
  return c;
}

/** 브러시 종이 결: 결이 진한 곳일수록 물감이 덜 묻는 알파 무늬 (문서 좌표에 고정) */
const grainCache = new Map();
export function grainMask(type, strength) {
  const key = type + ':' + strength.toFixed(2);
  if (grainCache.has(key)) return grainCache.get(key);
  const tile = paperTile(type);
  if (!tile) return null;
  const c = createCanvas(tile.width, tile.height), x = c.getContext('2d');
  const src = tile.getContext('2d').getImageData(0, 0, tile.width, tile.height).data;
  const im = x.createImageData(tile.width, tile.height);
  // 결을 대비를 키워서 쓴다 (종이 결 그대로는 너무 옅음)
  for (let i = 0; i < src.length; i += 4) {
    const dark = Math.min(1, (255 - src[i]) / 255 * 3.2);
    im.data[i + 3] = Math.round(255 * (1 - dark * strength));
  }
  x.putImageData(im, 0, 0);
  grainCache.set(key, c);
  return c;
}

/** 이 브러시에 획 뒤처리(종이 결·디더)가 있는지 */
export function hasBrushPost(p) {
  if (!p) return false;
  const grain = p.texture && p.texture !== 'none' && p.textureStrength > 0;
  const dither = p.dither > 0 && p.dither < 1 && p.tip !== 'pixel';
  return !!(grain || dither);
}

/**
 * 칠한 버퍼에 종이 결을 씌움 (rect 안만). doc 해상도에 맞춰 결 크기 유지.
 * textureMode: multiply(곱하기, 기본) · subtract(빼기: 옅은 곳이 먼저 사라짐) · hard(문턱: 결이 또렷)
 * 디더 무늬(p.dither)도 여기서 씌움 (픽셀 끝은 찍을 때 직접 처리)
 */
export function applyGrain(ctx, doc, p, rect) {
  if (!p) return;
  if (p.dither > 0 && p.dither < 1 && p.tip !== 'pixel' && rect) applyDitherMask(ctx, p.dither, p.ditherSize || 4, rect);
  if (!p.texture || p.texture === 'none' || !(p.textureStrength > 0)) return;
  const m = grainMask(p.texture, p.textureStrength);
  if (!m) return;
  const scale = (doc.dpi || 350) / 350 * (p.textureScale || 1);
  const mode = p.textureMode || 'multiply';
  if (mode !== 'multiply' && rect) {
    const x0 = Math.max(0, Math.floor(rect.x)), y0 = Math.max(0, Math.floor(rect.y));
    const w = Math.min(ctx.canvas.width - x0, Math.ceil(rect.w) + 1), hh = Math.min(ctx.canvas.height - y0, Math.ceil(rect.h) + 1);
    if (w <= 0 || hh <= 0) return;
    const tw = m.width, th = m.height;
    const g = (grainData.get(m) || grainData.set(m, m.getContext('2d').getImageData(0, 0, tw, th).data).get(m));
    const img = ctx.getImageData(x0, y0, w, hh), d = img.data;
    for (let y = 0; y < hh; y++) {
      const ty = (Math.floor((y0 + y) / scale) % th + th) % th;
      for (let x = 0; x < w; x++) {
        const i = (y * w + x) * 4 + 3;
        if (!d[i]) continue;
        const tx = (Math.floor((x0 + x) / scale) % tw + tw) % tw;
        const gv = g[(ty * tw + tx) * 4 + 3];                       // 255 = 결 없음, 낮을수록 종이 홈
        if (mode === 'subtract') d[i] = Math.max(0, d[i] - (255 - gv) * 1.4);
        else if (mode === 'hard') { if (gv < 200 - p.textureStrength * 60) d[i] = 0; }
      }
    }
    ctx.putImageData(img, x0, y0);
    return;
  }
  const pat = ctx.createPattern(m, 'repeat');
  pat.setTransform(new DOMMatrix().scale(scale));
  ctx.save();
  ctx.globalCompositeOperation = 'destination-in';
  ctx.fillStyle = pat;
  if (rect) { ctx.beginPath(); ctx.rect(rect.x, rect.y, rect.w, rect.h); ctx.clip(); }
  ctx.fillRect(rect ? rect.x : 0, rect ? rect.y : 0, rect ? rect.w : doc.width, rect ? rect.h : doc.height);
  ctx.restore();
}
const grainData = new Map();

/** 디더 무늬(Bayer)로 칸 비우기 — 문서 좌표 기준 */
function applyDitherMask(ctx, level, n, rect) {
  const M = n === 2 ? [[0, 2], [3, 1]] : [[0, 8, 2, 10], [12, 4, 14, 6], [3, 11, 1, 9], [15, 7, 13, 5]];
  const t = createCanvas(n, n), tx = t.getContext('2d'), im = tx.createImageData(n, n);
  for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) if (level > (M[y][x] + 0.5) / (n * n) - 1e-6) im.data[(y * n + x) * 4 + 3] = 255;
  tx.putImageData(im, 0, 0);
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.globalCompositeOperation = 'destination-in';
  ctx.imageSmoothingEnabled = false;
  ctx.fillStyle = ctx.createPattern(t, 'repeat');
  ctx.beginPath(); ctx.rect(rect.x, rect.y, rect.w, rect.h); ctx.clip();
  ctx.fillRect(rect.x, rect.y, rect.w, rect.h);
  ctx.restore();
}

/** 용지 그리기: ctx 는 이미 문서 좌표 변환이 걸려 있어야 함 */
export function drawPaper(ctx, doc) {
  ctx.fillStyle = doc.paper.color;
  ctx.fillRect(0, 0, doc.width, doc.height);
  drawTexture(ctx, doc, 1);
}

/** 결만 곱하기로 (k: 진하기 배율). 그림 위에 얹을 때도 사용 */
export function drawTexture(ctx, doc, k0 = 1) {
  const t = doc.paper.texture;
  if (!t || t.type === 'none' || !(t.strength > 0)) return;
  const tile = paperTile(t.type);
  if (!tile) return;
  const pat = ctx.createPattern(tile, 'repeat');
  const k = (doc.dpi || 350) / 350 * (t.scale || 1);   // 해상도가 높으면 결도 같은 실제 크기로
  pat.setTransform(new DOMMatrix().scale(k));
  ctx.save();
  ctx.globalCompositeOperation = 'multiply';
  ctx.globalAlpha = Math.min(1, t.strength * k0);
  ctx.fillStyle = pat;
  ctx.fillRect(0, 0, doc.width, doc.height);
  ctx.restore();
}
