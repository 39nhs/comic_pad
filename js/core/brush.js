// 브러시 엔진: 필압 · 손떨림 보정 · 곡선 보간 · 대칭 · 선택 영역 클리핑
import { applyGrain, hasBrushPost } from './paper.js';
import { paletteOf, snapToPalette, shadeStep, bayerAt, toHex } from './pixel.js';
import { app } from '../app.js';
import { createCanvas, clamp, rectUnion, hexToRgb, rgbToHex, rgbToHsv, hsvToRgb } from './util.js';
import { server } from './io.js';

export const BRUSH_DEFAULTS = {
  size: 10, opacity: 1, flow: 1, hardness: 1, spacing: 0.08,
  pressureSize: true, minSize: 0.2, pressureOpacity: false, minOpacity: 0.2,
  stabilizer: 3, grain: 0, buildup: false, color: null,
  tip: 'round',            // round | grain | image
  tipImage: null,          // 에셋 경로 (tip:'image')
  tintTip: true, rotJitter: 0, scatter: 0, sizeJitter: 0, followDir: false,
  texture: 'none', textureStrength: 0.6, textureScale: 1,   // 종이 결 (브러시마다)
  // 동작: 펜 기울기·속도·필압에 따라 바뀌는 것들 (0 = 끔)
  tiltSize: 0, tiltOpacity: 0, speedOpacity: 0, hueJitter: 0, satJitter: 0, valJitter: 0, pressureColor: false,
  taperIn: 0, taperOut: 0,  // 입출 보정 (문서 px 길이)
  blend: 'source-over',     // 브러시 합성 모드 (곱하기, 닷지 등)
  mix: 0,                   // 혼색: 밑에 있는 색과 섞는 정도 (수채/유화)
  density: 24, particle: 1.2,          // 스프레이
  flatRatio: 0.3, angle: 45, useTilt: false,   // 캘리그래피(납작 펜)
  roughOnly: false,          // 러프 전용 레이어에서만 그려짐 (색은 레이어의 러프 색)
  speedThin: 0,              // 빠르게 그을수록 가늘게 (만년필 느낌)
  stabMode: 'auto',          // 흔들림 보정 방식: auto(설정 따름) | euro | avg | lazy(끈 당기기) | spring(무거운 펜)
  catchUp: true,             // 펜을 떼면 늦게 따라오던 선을 펜 끝까지 이어 주기
  pressurePts: null,         // 이 브러시만의 필압 곡선 (없으면 설정의 곡선)
  // 픽셀아트 (tip:'pixel')
  pixelPerfect: true,        // 1px 선의 ㄴ자 겹친 모서리 지우기 (Aseprite 식)
  shading: false, shadeDir: -1,   // 명암 잉크: 밑 색을 팔레트 밝기 사다리에서 한 칸 어둡게(-1)/밝게(+1)
  dither: 0, ditherSize: 4,  // 디더 무늬: 0 = 끔, 0~1 = 칠하는 칸 비율 (2×2 / 4×4 Bayer)
  // 클립스튜디오식 브러시 확장
  dualTip: 'none', dualSize: 0.35, dualDensity: 6, dualScatter: 1,   // 듀얼 브러시: 두 번째 끝 모양으로 첫 번째를 가림
  ribbon: false,             // 리본: 끝 모양 그림을 획 방향으로 이어 붙여 늘림
  textureMode: 'multiply',   // 종이 결 합성: multiply(곱하기) | subtract(빼기) | hard(문턱)
};

export const DEFAULT_PRESETS = {
  brush: [
    { id: 'notepen', name: '필기 펜 (노트 느낌)', size: 4, hardness: 1, minSize: 0.22, stabilizer: 3, speedThin: 0.35, spacing: 0.05 },
    { id: 'gpen', name: 'G펜', size: 6, hardness: 1, minSize: 0.04, stabilizer: 4 },
    { id: 'maru', name: '마루펜', size: 3, hardness: 1, minSize: 0.45, stabilizer: 3 },
    { id: 'pencil', name: '연필', size: 7, hardness: 0.55, tip: 'grain', grain: 0.65, pressureOpacity: true, minOpacity: 0.15, minSize: 0.55, stabilizer: 1, spacing: 0.1 },
    { id: 'rough', name: '러프 연필', size: 9, hardness: 0.5, tip: 'grain', grain: 0.55, pressureOpacity: true, minOpacity: 0.2, minSize: 0.6, stabilizer: 1, opacity: 0.85, roughOnly: true },
    { id: 'pencilgrain', name: '연필 (종이 결)', size: 9, hardness: 0.6, tip: 'grain', grain: 0.4, pressureOpacity: true, minOpacity: 0.2, minSize: 0.5, stabilizer: 1, spacing: 0.08, texture: 'rough', textureStrength: 0.75 },
    { id: 'watergrain', name: '수채 (종이 결)', size: 60, hardness: 0.3, flow: 0.4, opacity: 0.8, mix: 0.4, pressureOpacity: true, minOpacity: 0.15, stabilizer: 1, spacing: 0.12, texture: 'rough', textureStrength: 0.55 },
    { id: 'marker', name: '마커', size: 24, hardness: 0.95, pressureSize: false, stabilizer: 1 },
    { id: 'soft', name: '부드러운 브러시', size: 60, hardness: 0.1, flow: 0.35, pressureSize: false, pressureOpacity: true, minOpacity: 0, stabilizer: 0 },
    { id: 'air', name: '에어브러시', size: 120, hardness: 0, flow: 0.06, spacing: 0.12, pressureSize: false, pressureOpacity: true, minOpacity: 0, buildup: true, stabilizer: 0 },
    { id: 'fillpen', name: '채색 펜', size: 30, hardness: 0.98, pressureSize: true, minSize: 0.7, stabilizer: 0 },
    { id: 'water', name: '수채 (혼색)', size: 50, hardness: 0.35, flow: 0.45, opacity: 0.85, mix: 0.55, pressureSize: true, minSize: 0.5, pressureOpacity: true, minOpacity: 0.2, stabilizer: 1, spacing: 0.12 },
    { id: 'oil', name: '유화 (혼색)', size: 36, hardness: 0.9, mix: 0.45, pressureSize: true, minSize: 0.6, stabilizer: 1, spacing: 0.06 },
    { id: 'blendbrush', name: '색 섞기 (혼색만)', size: 40, hardness: 0.5, flow: 0.5, mix: 1, pressureSize: false, stabilizer: 0, spacing: 0.1 },
    { id: 'dodge', name: '발광 닷지', size: 80, hardness: 0.1, flow: 0.15, blend: 'color-dodge', pressureSize: false, pressureOpacity: true, minOpacity: 0, stabilizer: 0, buildup: true },
    { id: 'mulshadow', name: '곱하기 그림자', size: 40, hardness: 0.6, opacity: 0.6, blend: 'multiply', pressureSize: true, minSize: 0.5, stabilizer: 1, color: '#8a7aa8' },
    { id: 'overlaylight', name: '오버레이 빛', size: 90, hardness: 0.1, flow: 0.2, blend: 'overlay', pressureSize: false, pressureOpacity: true, minOpacity: 0, stabilizer: 0, buildup: true },
    { id: 'spray', name: '스프레이', size: 70, tip: 'spray', density: 26, particle: 1.3, pressureSize: false, pressureOpacity: true, minOpacity: 0.2, stabilizer: 0, spacing: 0.25 },
    { id: 'calli', name: '캘리그래피 펜', size: 18, tip: 'flat', flatRatio: 0.22, angle: 40, useTilt: true, hardness: 1, minSize: 0.5, stabilizer: 3, spacing: 0.04 },
    { id: 'pixel1', name: '픽셀 연필 (1px)', size: 1, tip: 'pixel', pixelPerfect: true, pressureSize: false, stabilizer: 0, spacing: 0.1 },
    { id: 'pixelshade', name: '픽셀 명암 잉크', size: 1, tip: 'pixel', shading: true, shadeDir: -1, pressureSize: false, stabilizer: 0, spacing: 0.1 },
    { id: 'pixeldither', name: '픽셀 디더 (50%)', size: 4, tip: 'pixel', pixelPerfect: false, dither: 0.5, ditherSize: 2, pressureSize: false, stabilizer: 0, spacing: 0.1 },
    { id: 'dualdry', name: '마른 붓 (듀얼)', size: 34, hardness: 0.8, tip: 'round', dualTip: 'grain', dualSize: 0.4, dualDensity: 7, dualScatter: 1, pressureSize: true, minSize: 0.5, stabilizer: 1, spacing: 0.08 },
    { id: 'ribbonbrush', name: '리본 붓', size: 26, hardness: 0.7, tip: 'grain', grain: 0.6, ribbon: true, pressureSize: true, minSize: 0.4, stabilizer: 2, spacing: 0.05 },
  ],
  eraser: [
    { id: 'er-hard', name: '딱딱한 지우개', size: 24, hardness: 1, pressureSize: false, stabilizer: 0 },
    { id: 'er-soft', name: '부드러운 지우개', size: 80, hardness: 0.1, flow: 0.4, pressureSize: false, pressureOpacity: true, minOpacity: 0, stabilizer: 0 },
    { id: 'er-fine', name: '세밀 지우개', size: 4, hardness: 1, minSize: 0.3, stabilizer: 2 },
    { id: 'er-pixel', name: '픽셀 지우개', size: 1, tip: 'pixel', pixelPerfect: false, pressureSize: false, stabilizer: 0, spacing: 0.1 },
  ],
};

export function makePreset(p) { return { ...BRUSH_DEFAULTS, ...p }; }

// ---- 팁 이미지 ----------------------------------------------------------
const TIP = 128;
const tipCache = new Map();
const imageTips = new Map();   // path -> HTMLImageElement

function noise(seed) {
  let s = seed * 9301 + 49297;
  return () => { s = (s * 9301 + 49297) % 233280; return s / 233280; };
}

function makeMaskTip(hardness, grain, variant) {
  const key = `${hardness.toFixed(2)}|${grain.toFixed(2)}|${variant}`;
  if (tipCache.has(key)) return tipCache.get(key);
  const c = createCanvas(TIP, TIP);
  const x = c.getContext('2d');
  const r = TIP / 2;
  const g = x.createRadialGradient(r, r, 0, r, r, r);
  const hh = clamp(hardness, 0, 0.99);
  g.addColorStop(0, 'rgba(255,255,255,1)');
  g.addColorStop(hh, 'rgba(255,255,255,1)');
  g.addColorStop(Math.min(1, hh + (1 - hh) * 0.5), `rgba(255,255,255,${hardness > 0.5 ? 0.6 : 0.35})`);
  g.addColorStop(1, 'rgba(255,255,255,0)');
  x.fillStyle = g;
  x.fillRect(0, 0, TIP, TIP);
  if (grain > 0) {
    const img = x.getImageData(0, 0, TIP, TIP);
    const rnd = noise(variant + 1);
    for (let i = 0; i < img.data.length; i += 4) {
      const n = rnd();
      const keep = n > grain * 0.85 ? 1 : n / Math.max(0.01, grain * 0.85) * 0.35;
      img.data[i + 3] = img.data[i + 3] * keep;
    }
    x.putImageData(img, 0, 0);
  }
  tipCache.set(key, c);
  return c;
}

function tintTip(mask, color) {
  const c = createCanvas(mask.width, mask.height);
  const x = c.getContext('2d');
  x.drawImage(mask, 0, 0);
  x.globalCompositeOperation = 'source-in';
  x.fillStyle = color;
  x.fillRect(0, 0, c.width, c.height);
  return c;
}

export async function loadImageTip(path) {
  if (imageTips.has(path)) return imageTips.get(path);
  const img = new Image();
  img.src = await server.src('assets', path);
  await img.decode();
  imageTips.set(path, img);
  return img;
}

// ---- 필압 곡선 -----------------------------------------------------------
// 점 [[입력, 출력], ...] (0~1) 을 지나는 매끄러운 단조 곡선 (Fritsch–Carlson). 256칸 표로 만들어 둠
const lutCache = new Map();
export function curveLut(pts) {
  const key = JSON.stringify(pts);
  let lut = lutCache.get(key);
  if (lut) return lut;
  const P = pts.map(([x, y]) => [clamp(x, 0, 1), clamp(y, 0, 1)]).sort((a, b) => a[0] - b[0]);
  if (P[0][0] > 0) P.unshift([0, P[0][1]]);
  if (P[P.length - 1][0] < 1) P.push([1, P[P.length - 1][1]]);
  const n = P.length, d = [], m = [];
  for (let i = 0; i < n - 1; i++) d.push((P[i + 1][1] - P[i][1]) / Math.max(1e-6, P[i + 1][0] - P[i][0]));
  m[0] = d[0]; m[n - 1] = d[n - 2];
  for (let i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
  for (let i = 0; i < n - 1; i++) {
    if (!d[i]) { m[i] = m[i + 1] = 0; continue; }
    const a = m[i] / d[i], b = m[i + 1] / d[i], h2 = a * a + b * b;
    if (h2 > 9) { const t = 3 / Math.sqrt(h2); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
  }
  lut = new Float32Array(256);
  let k = 0;
  for (let j = 0; j < 256; j++) {
    const x = j / 255;
    while (k < n - 2 && x > P[k + 1][0]) k++;
    const x0 = P[k][0], x1 = P[k + 1][0], hgt = Math.max(1e-6, x1 - x0), t = clamp((x - x0) / hgt, 0, 1);
    const t2 = t * t, t3 = t2 * t;
    lut[j] = clamp((2 * t3 - 3 * t2 + 1) * P[k][1] + (t3 - 2 * t2 + t) * hgt * m[k] + (-2 * t3 + 3 * t2) * P[k + 1][1] + (t3 - t2) * hgt * m[k + 1], 0, 1);
  }
  if (lutCache.size > 50) lutCache.clear();
  lutCache.set(key, lut);
  return lut;
}

/** 필압 곡선 모음 (설정·브러시 옵션에서 고르기) */
export const PRESSURE_PRESETS = {
  '기본 (곧게)': [[0, 0], [1, 1]],
  '부드럽게 (살짝만 눌러도 진하게)': [[0, 0], [0.25, 0.5], [1, 1]],
  '딱딱하게 (세게 눌러야 굵게)': [[0, 0], [0.6, 0.3], [1, 1]],
  'S자 (가운데에서 크게 변함)': [[0, 0], [0.3, 0.12], [0.7, 0.88], [1, 1]],
  '약한 손힘 (힘 덜 들게)': [[0, 0.05], [0.15, 0.45], [0.5, 0.85], [1, 1]],
  '처음부터 조금 굵게': [[0, 0.25], [1, 1]],
  '세게 눌러도 덜 굵게': [[0, 0], [0.5, 0.55], [1, 0.8]],
};

/** 펜 필압(0~1) → 곡선을 거친 값. preset 에 따로 정한 곡선이 있으면 그것을 */
export function pressureCurve(p, preset = null) {
  const s = app.settings;
  // 진단에서 잰 이 펜의 실제 필압 범위(가장 약하게~가장 세게)를 0~1 로 늘린다
  const lo = s.penFeel?.pmin ?? 0, hi = s.penFeel?.pmax ?? 1;
  const x = clamp((p - lo) / Math.max(0.05, hi - lo), 0, 1);
  const pts = preset?.pressurePts || s.pressurePts;
  if (pts?.length >= 2) return curveLut(pts)[Math.round(x * 255)];
  return Math.pow(x, s.pressureGamma || 1);
}

// ---- 스트로크 -----------------------------------------------------------
export class Stroke {
  /**
   * o: { canvas (스트로크 버퍼, 문서 크기), preset, color, doc, symmetry }
   */
  constructor(o) {
    this.buf = o.canvas;
    this.ctx = this.buf.getContext('2d');
    this.p = o.preset;
    this.color = o.color;
    this.doc = o.doc;
    this.sym = o.symmetry || 'none';
    this.noClip = !!o.noClip;
    this.sample = o.sample || null;          // 혼색용: 레이어 픽셀 { data, width, height }
    this.mixColor = this.p.mix > 0 ? hexToRgb(this.color) : null;
    this.mixTips = new Map();
    this.tiltAngle = null;
    this.raw = [];
    this.pts = [];
    this.next = 0;
    this.total = null;
    this.batch = null;
    this.rnd = Math.random;
    this.lastDir = 0;
    this.travel = 0;
    this.queue = [];
    this.lastD = 0;
    this._prepareTips();
  }

  _prepareTips() {
    const p = this.p;
    this.hardRound = p.tip === 'round' && p.hardness >= 0.98;
    this.tips = [];
    this.maskTips = [];
    this.pixel = p.tip === 'pixel';
    if (this.pixel) { this.hardRound = false; this.vector = false; this.overlap = 1; return; }
    if (p.tip === 'spray' || p.tip === 'flat') { this.hardRound = false; this.vector = true; }
    else if (p.tip === 'image' && p.tipImage && imageTips.has(p.tipImage)) {
      const img = imageTips.get(p.tipImage);
      const c = createCanvas(img.width, img.height);
      c.getContext('2d').drawImage(img, 0, 0);
      this.tips.push(p.tintTip ? tintTip(c, this.color) : c);
      this.hardRound = false;
    } else if (!this.hardRound) {
      const variants = p.tip === 'grain' ? 4 : 1;
      for (let v = 0; v < variants; v++) {
        const mk = makeMaskTip(p.hardness, p.tip === 'grain' ? p.grain : 0, v);
        this.maskTips.push(mk);
        this.tips.push(tintTip(mk, this.color));
      }
    }
    // 리본·듀얼은 끝 모양 그림이 있어야 함 (딱딱한 원이라도)
    if ((p.ribbon || (p.dualTip && p.dualTip !== 'none')) && !this.tips.length && !this.vector) {
      this.hardRound = false;
      const mk = makeMaskTip(p.hardness, p.tip === 'grain' ? p.grain : 0, 0);
      this.maskTips.push(mk); this.tips.push(tintTip(mk, this.color));
    }
    // 선뭉침 방지: 딱딱한 원 + 늘 불투명이면 찍은 원들을 하나의 모양으로 합쳐 칠한다 (가장자리 부드러움이 겹쳐 쌓이지 않게)
    this.union = this.hardRound && !this.vector && !this.pixel && (p.flow ?? 1) >= 1 && !p.pressureOpacity && !p.speedOpacity
      && !p.tiltOpacity && !(p.mix > 0) && !p.pressureColor && !p.hueJitter && !p.satJitter && !p.valJitter && !p.shading
      && !(p.dualTip && p.dualTip !== 'none') && !p.ribbon && app.settings.penFeel?.unionDabs !== false;
    this.ud = []; this.ug = new Map();
    // 겹침 보정: 압력 불투명도는 한 지점에 겹치는 dab 수를 고려
    this.overlap = Math.max(1, 1 / Math.max(0.02, p.spacing));
  }

  _size(pr) {
    const p = this.p;
    let v = p.speedThin ? 1 - p.speedThin * 0.7 * Math.min(1, (this.vel || 0) / 2.5) : 1;
    // 펜을 눕힐수록 굵게 (연필 옆면으로 칠하듯)
    if (p.tiltSize && this.tiltMag) v *= 1 + p.tiltSize * 2.5 * this.tiltMag;
    if (!p.pressureSize) return p.size * v;
    return p.size * (p.minSize + (1 - p.minSize) * pressureCurve(pr, p)) * v;
  }

  _alpha(pr) {
    const p = this.p;
    let a = p.flow;
    if (p.pressureOpacity) {
      const target = p.minOpacity + (1 - p.minOpacity) * pressureCurve(pr, p);
      a *= p.buildup ? target : 1 - Math.pow(1 - clamp(target, 0, 0.999), 1 / this.overlap);
    }
    if (p.tiltOpacity && this.tiltMag) a *= 1 - p.tiltOpacity * 0.85 * this.tiltMag;          // 눕힐수록 옅게
    if (p.speedOpacity) a *= 1 - p.speedOpacity * 0.85 * Math.min(1, (this.vel || 0) / 2.5);  // 빠를수록 옅게
    return a;
  }

  /** 한 번 찍을 때의 색: 필압으로 배경색↔전경색, 색상·채도·명도 흔들림 */
  _dabColor(pr) {
    const p = this.p;
    if (!p.pressureColor && !p.hueJitter && !p.satJitter && !p.valJitter) return null;
    let { r, g, b } = hexToRgb(this.color);
    if (p.pressureColor) {
      const t = pressureCurve(pr, p), bg = hexToRgb(app.color?.bg || '#ffffff');
      r = bg.r + (r - bg.r) * t; g = bg.g + (g - bg.g) * t; b = bg.b + (b - bg.b) * t;
    }
    if (p.hueJitter || p.satJitter || p.valJitter) {
      let { h: hh, s: ss, v: vv } = rgbToHsv(r, g, b);
      hh = (hh + (Math.random() - 0.5) * p.hueJitter * 360 + 360) % 360;
      ss = clamp(ss + (Math.random() - 0.5) * p.satJitter, 0, 1);
      vv = clamp(vv + (Math.random() - 0.5) * p.valJitter, 0, 1);
      ({ r, g, b } = hsvToRgb(hh, ss, vv));
    }
    return rgbToHex(Math.round(r), Math.round(g), Math.round(b));
  }

  _spacing(pr) { return Math.max(0.35, this._size(pr) * this.p.spacing); }

  _mark(x0, y0, x1, y1) {
    const r = { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
    this.batch = rectUnion(this.batch, r);
  }

  /** 혼색: 밑 색을 집어 현재 붓 색과 섞는다 */
  _mixAt(x, y) {
    const s = this.sample;
    if (!s || !this.mixColor) return null;
    const ix = Math.round(x), iy = Math.round(y);
    let r = 0, g = 0, b = 0, a = 0;
    for (let dy = -2; dy <= 2; dy += 2) for (let dx = -2; dx <= 2; dx += 2) {
      const px = ix + dx, py = iy + dy;
      if (px < 0 || py < 0 || px >= s.width || py >= s.height) continue;
      const i = (py * s.width + px) * 4, al = s.data[i + 3];
      r += s.data[i] * al; g += s.data[i + 1] * al; b += s.data[i + 2] * al; a += al;
    }
    const mc = this.mixColor;
    if (a > 0) {
      const k = this.p.mix * Math.min(1, a / (9 * 255) * 1.5);
      mc.r += (r / a - mc.r) * k; mc.g += (g / a - mc.g) * k; mc.b += (b / a - mc.b) * k;
    }
    return `rgb(${mc.r | 0},${mc.g | 0},${mc.b | 0})`;
  }

  _tipFor(color) {
    const key = color + this.maskTips.length;
    let list = this.mixTips.get(key);
    if (!list) {
      if (this.mixTips.size > 400) this.mixTips.clear();
      list = this.maskTips.map(mk => tintTip(mk, color));
      this.mixTips.set(key, list);
    }
    return list[(Math.random() * list.length) | 0];
  }

  _dabOne(x, y, size, alpha, rot) {
    const ctx = this.ctx;
    const r = size / 2;
    const mix = this.mixColor ? this._mixAt(x, y) : this.dabColor || null;
    if (mix) ctx.fillStyle = mix;
    if (this.vector) {
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      const p = this.p;
      if (p.tip === 'spray') {
        ctx.globalAlpha = alpha;
        const n = Math.max(1, Math.round(p.density * Math.min(4, Math.max(0.3, size / 60))));
        ctx.beginPath();
        for (let k = 0; k < n; k++) {
          const a = Math.random() * Math.PI * 2, d = r * Math.sqrt(Math.random()), pr = p.particle * (0.5 + Math.random());
          ctx.moveTo(x + Math.cos(a) * d + pr, y + Math.sin(a) * d);
          ctx.arc(x + Math.cos(a) * d, y + Math.sin(a) * d, pr, 0, Math.PI * 2);
        }
        ctx.fill();
      } else {
        // 납작 펜: 펜 기울기 방향(있으면) 또는 고정 각도
        const ang = p.useTilt && this.tiltAngle != null ? this.tiltAngle : p.angle * Math.PI / 180;
        ctx.globalAlpha = alpha;
        ctx.beginPath();
        ctx.ellipse(x, y, Math.max(r, 0.5), Math.max(r * p.flatRatio, 0.5), ang, 0, Math.PI * 2);
        ctx.fill();
      }
    } else if (this.hardRound && this.union) {
      this._udab(x, y, Math.max(r, 0.5), size < 1 ? alpha * size : alpha);   // 그리는 것은 _flush 에서 한 번에
    } else if (this.hardRound) {
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.globalAlpha = size < 1 ? alpha * size : alpha;
      ctx.beginPath();
      ctx.arc(x, y, Math.max(r, 0.5), 0, Math.PI * 2);
      ctx.fill();
    } else {
      const tip = mix && this.maskTips.length ? this._tipFor(mix) : this.tips[(Math.random() * this.tips.length) | 0];
      ctx.globalAlpha = alpha;
      const asp = tip.height / tip.width;
      if (rot) {
        ctx.setTransform(1, 0, 0, 1, x, y);
        ctx.rotate(rot);
        ctx.drawImage(tip, -r, -r * asp, size, size * asp);
        ctx.setTransform(1, 0, 0, 1, 0, 0);
      } else {
        ctx.drawImage(tip, x - r, y - r * asp, size, size * asp);
      }
    }
    const e = r + 2;
    this._mark(x - e, y - e, x + e, y + e);
  }

  /** 한 번 찍기: 명암 잉크 / 듀얼 브러시 / 보통 */
  _stamp(x, y, size, alpha, rot) {
    const p = this.p;
    if (p.shading) { this._shadeArea(x, y, size, false); return; }
    if (p.dualTip && p.dualTip !== 'none' && !this.vector && !this.mixColor) { this._dual(x, y, size, alpha, rot); return; }
    this._dabOne(x, y, size, alpha, rot);
  }

  /** 듀얼 브러시: 첫 번째 끝 모양을 작은 칸에 찍고, 두 번째 끝 모양(흩뿌린 자국)이 닿은 곳만 남긴다 */
  _dual(x, y, size, alpha, rot) {
    const p = this.p, R = Math.ceil(size / 2 + 2), D = R * 2;
    if (!this.dualC || this.dualC.width < D) { this.dualC = createCanvas(D, D); this.dualM = createCanvas(D, D); }
    const t = this.dualC.getContext('2d'), m = this.dualM.getContext('2d');
    t.setTransform(1, 0, 0, 1, 0, 0); t.globalCompositeOperation = 'source-over'; t.globalAlpha = 1; t.clearRect(0, 0, this.dualC.width, this.dualC.height);
    m.setTransform(1, 0, 0, 1, 0, 0); m.clearRect(0, 0, this.dualM.width, this.dualM.height);
    const fx = x - Math.floor(x), fy = y - Math.floor(y);
    const real = this.ctx, mk = this._mark;
    t.fillStyle = real.fillStyle;
    this.ctx = t; this._mark = () => {};
    try { this._dabOne(R + fx, R + fy, size, alpha, rot); } finally { this.ctx = real; this._mark = mk; }
    // 두 번째 끝 모양: 문서에 고정된 격자 위의 흩뿌린 자국 (겹쳐 찍어도 같은 무늬라 결이 살아 있음)
    const ds = Math.max(1.5, size * (p.dualSize || 0.35)), g = ds * 0.85;
    const keep = Math.max(0.05, Math.min(1, (p.dualDensity || 6) / 15)), sc = p.dualScatter ?? 1;
    if (!this.dualTips) this.dualTips = [0, 1, 2, 3].map(v => makeMaskTip(0.6, p.dualTip === 'grain' ? 0.7 : 0, v));
    const hash = (i, j, k) => { let h0 = (i * 374761393 + j * 668265263 + k * 2147483647) | 0; h0 = (h0 ^ (h0 >>> 13)) * 1274126177 | 0; return ((h0 ^ (h0 >>> 16)) >>> 0) / 4294967296; };
    const ox = Math.floor(x) - R, oy = Math.floor(y) - R;
    m.fillStyle = '#000';
    for (let j = Math.floor((oy - ds) / g); j <= Math.ceil((oy + D + ds) / g); j++) {
      for (let i = Math.floor((ox - ds) / g); i <= Math.ceil((ox + D + ds) / g); i++) {
        if (hash(i, j, 1) > keep) continue;
        const cx = (i + 0.5 + (hash(i, j, 2) - 0.5) * sc) * g - ox, cy = (j + 0.5 + (hash(i, j, 3) - 0.5) * sc) * g - oy;
        if (p.dualTip === 'spray') {
          for (let k = 0; k < 6; k++) { const b = hash(i, j, 10 + k) * Math.PI * 2, e = hash(i, j, 20 + k) * ds / 2; m.fillRect(cx + Math.cos(b) * e, cy + Math.sin(b) * e, 1.2, 1.2); }
        } else if (p.dualTip === 'round') {
          m.beginPath(); m.arc(cx, cy, ds / 2, 0, Math.PI * 2); m.fill();
        } else {
          m.drawImage(this.dualTips[(hash(i, j, 4) * 4) | 0], cx - ds / 2, cy - ds / 2, ds, ds);
        }
      }
    }
    t.globalCompositeOperation = 'destination-in';
    t.drawImage(this.dualM, 0, 0);
    t.globalCompositeOperation = 'source-over';
    real.setTransform(1, 0, 0, 1, 0, 0);
    real.globalAlpha = 1;
    real.drawImage(this.dualC, 0, 0, D, D, Math.floor(x) - R, Math.floor(y) - R, D, D);
    this._mark(Math.floor(x) - R, Math.floor(y) - R, Math.floor(x) + R, Math.floor(y) + R);
  }

  /** 리본: 끝 모양 그림을 획 방향으로 계속 이어 붙인다 (그림의 가로가 획을 따라 흐름) */
  _ribbon(x, y, size, alpha) {
    const prev = this.ribPrev;
    this.ribPrev = { x, y };
    const tip = this.dabColor && this.maskTips.length ? this._tipFor(this.dabColor) : this.tips[0];
    if (!prev || !tip) return;
    const dx = x - prev.x, dy = y - prev.y, len = Math.hypot(dx, dy);
    if (len < 0.01) return;
    const ctx = this.ctx, tw = tip.width, th = tip.height;
    const srcW = Math.max(0.5, len / Math.max(1, size) * tw);
    let u = this.ribU || 0, done = 0;
    ctx.setTransform(1, 0, 0, 1, prev.x, prev.y);
    ctx.rotate(Math.atan2(dy, dx));
    ctx.globalAlpha = alpha;
    while (done < srcW - 1e-6) {
      const sw = Math.min(srcW - done, tw - u);
      ctx.drawImage(tip, u, 0, Math.max(0.5, sw), th, done / srcW * len, -size / 2, sw / srcW * len + 0.6, size);
      done += sw; u = (u + sw) % tw;
    }
    this.ribU = u;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    const e = size / 2 + 2;
    this._mark(Math.min(prev.x, x) - e, Math.min(prev.y, y) - e, Math.max(prev.x, x) + e, Math.max(prev.y, y) + e);
  }

  // ---- 픽셀 끝 -------------------------------------------------------
  /** 지난 픽셀부터 이번 픽셀까지 빈틈없이 (Bresenham) */
  _pixelTo(x, y, pr) {
    const ix = Math.floor(x), iy = Math.floor(y);
    const n = Math.max(1, Math.round(this._size(pr)));
    const lp = this.lastPix;
    if (!lp) { this._plot(ix, iy, n); this.lastPix = { x: ix, y: iy }; return; }
    if (lp.x === ix && lp.y === iy) return;
    let x0 = lp.x, y0 = lp.y;
    const dx = Math.abs(ix - x0), dy = -Math.abs(iy - y0), sx = x0 < ix ? 1 : -1, sy = y0 < iy ? 1 : -1;
    let err = dx + dy;
    while (x0 !== ix || y0 !== iy) {
      const e2 = 2 * err;
      if (e2 >= dy) { err += dy; x0 += sx; }
      if (e2 <= dx) { err += dx; y0 += sy; }
      this._plot(x0, y0, n);
    }
    this.lastPix = { x: ix, y: iy };
  }

  /** 픽셀 하나(크기 n 의 네모) 찍기 + 픽셀 퍼펙트(ㄴ자 모서리 지우기) */
  _plot(px, py, n) {
    const p = this.p;
    if (p.pixelPerfect && n === 1 && !p.shading) {
      const hist = (this.ppHist ||= []);
      const b = hist[hist.length - 1], a = hist[hist.length - 2];
      if (a && b && Math.abs(a.x - px) === 1 && Math.abs(a.y - py) === 1
        && ((b.x === a.x && b.y === py) || (b.y === a.y && b.x === px))) {
        this._pixelsAt(b.x, b.y, 1, true);                     // 가운데 모서리 픽셀 지우기
        hist.pop();
      }
      hist.push({ x: px, y: py });
      if (hist.length > 3) hist.shift();
    }
    this._pixelsAt(px, py, n, false);
  }

  /** 대칭까지 포함해 실제 픽셀 칠하기/지우기 */
  _pixelsAt(px, py, n, erase) {
    const W = this.doc.width, H = this.doc.height, o = Math.floor((n - 1) / 2);
    const spots = [[px, py]];
    if (this.sym === 'v' || this.sym === 'vh') spots.push([W - 1 - px, py]);
    if (this.sym === 'h' || this.sym === 'vh') spots.push([px, H - 1 - py]);
    if (this.sym === 'vh') spots.push([W - 1 - px, H - 1 - py]);
    const ctx = this.ctx, p = this.p;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.globalAlpha = 1;
    for (const [qx, qy] of spots) {
      const x0 = qx - o, y0 = qy - o;
      if (erase) ctx.clearRect(x0, y0, n, n);
      else if (p.shading) this._shadeArea(x0 + n / 2, y0 + n / 2, n, true);
      else if (p.dither > 0 && p.dither < 1) {
        for (let yy = y0; yy < y0 + n; yy++) for (let xx = x0; xx < x0 + n; xx++) if (p.dither > bayerAt(xx, yy, p.ditherSize || 4) - 1e-6) ctx.fillRect(xx, yy, 1, 1);
      } else ctx.fillRect(x0, y0, n, n);
      this._mark(x0 - 1, y0 - 1, x0 + n + 1, y0 + n + 1);
    }
  }

  /** 명암 잉크: 밑 그림 색을 팔레트 밝기 사다리에서 한 칸씩 (한 획에 한 픽셀은 한 번만) */
  _shadeArea(cx, cy, size, square) {
    const s = this.sample;
    if (!s) return;
    const pal = this.shadePal ||= paletteOf(this.doc);
    const seen = this.shadeSeen ||= new Set();
    const dir = (this.p.shadeDir || -1) * (this.shadeFlip ? -1 : 1);
    const r = size / 2, x0 = Math.floor(cx - r), y0 = Math.floor(cy - r), x1 = Math.ceil(cx + r), y1 = Math.ceil(cy + r);
    const ctx = this.ctx;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.globalAlpha = 1;
    for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) {
      if (x < 0 || y < 0 || x >= s.width || y >= s.height) continue;
      if (!square && Math.hypot(x + 0.5 - cx, y + 0.5 - cy) > r + 0.01) continue;
      const k = y * s.width + x;
      if (seen.has(k)) continue;
      seen.add(k);
      const i = k * 4;
      if (!s.data[i + 3]) continue;
      let c = shadeStep(s.data[i], s.data[i + 1], s.data[i + 2], pal, dir);
      if (!c) {                                                    // 팔레트에 없는 색이면 밝기만 12% 옮기기
        const f = dir < 0 ? 0.88 : 1.12;
        c = [0, 1, 2].map(j => Math.max(0, Math.min(255, Math.round(s.data[i + j] * f + (dir > 0 ? 8 : 0)))));
      }
      ctx.fillStyle = toHex(c[0], c[1], c[2]);
      ctx.fillRect(x, y, 1, 1);
    }
    this._mark(x0 - 1, y0 - 1, x1 + 1, y1 + 1);
  }

  /** 입출 보정: 시작은 바로 가늘게, 끝은 taperOut 길이만큼 늦게 그려서 마지막에 가늘게 */
  _emit(x, y, pr, d) {
    const p = this.p;
    if (this.noTaper || (!p.taperIn && !p.taperOut)) { this._dab(x, y, pr, 1); return; }
    const k = p.taperIn ? Math.min(1, 0.08 + d / p.taperIn) : 1;
    this.lastD = d;
    if (!p.taperOut) { this._dab(x, y, pr, k); return; }
    this.queue.push({ x, y, pr, d, k });
    while (this.queue.length && d - this.queue[0].d > p.taperOut) {
      const q = this.queue.shift();
      this._dab(q.x, q.y, q.pr, q.k);
    }
  }

  _flushTaper() {
    const p = this.p;
    for (const q of this.queue) this._dab(q.x, q.y, q.pr, q.k * Math.min(1, 0.08 + (this.lastD - q.d) / p.taperOut));
    this.queue = [];
  }

  _dab(x, y, pr, k = 1) {
    const p = this.p;
    if (this.pixel) { this._pixelTo(x, y, pr); return; }
    if (p.ribbon && !this.vector) { this.dabColor = this._dabColor(pr); this._ribbon(x, y, this._size(pr) * k, this._alpha(pr)); return; }
    let size = this._size(pr) * k;
    if (p.sizeJitter) size *= 1 - Math.random() * p.sizeJitter;
    const alpha = this._alpha(pr);
    let rot = 0;
    if (p.tip === 'grain') rot = Math.random() * Math.PI * 2;
    if (p.followDir) rot = this.lastDir;
    if (p.rotJitter) rot += (Math.random() - 0.5) * p.rotJitter * Math.PI * 2;
    if (p.scatter) {
      const a = Math.random() * Math.PI * 2, d = Math.random() * p.scatter * size;
      x += Math.cos(a) * d; y += Math.sin(a) * d;
    }
    this.dabColor = this._dabColor(pr);
    this._stamp(x, y, size, alpha, rot);
    const W = this.doc.width, H = this.doc.height;
    if (this.sym === 'v' || this.sym === 'vh') this._stamp(W - x, y, size, alpha, -rot);
    if (this.sym === 'h' || this.sym === 'vh') this._stamp(x, H - y, size, alpha, -rot);
    if (this.sym === 'vh') this._stamp(W - x, H - y, size, alpha, rot);
    if (this.sym === 'radial') {
      const n = Math.max(2, app.settings.symN || 6), cx = W / 2, cy = H / 2, dx = x - cx, dy = y - cy;
      for (let i = 0; i < n; i++) {
        const a = (i / n) * Math.PI * 2, c = Math.cos(a), s = Math.sin(a);
        if (i) this._stamp(cx + dx * c - dy * s, cy + dx * s + dy * c, size, alpha, rot + a);
        if (app.settings.symMirror) this._stamp(cx - (dx * c - dy * s), cy + dx * s + dy * c, size, alpha, -rot - a);
      }
    }
  }

  _line(a, b) {
    const dx = b.x - a.x, dy = b.y - a.y;
    const len = Math.hypot(dx, dy);
    if (len < 1e-6) return;
    this.lastDir = Math.atan2(dy, dx);
    let t = this.next;
    while (t <= len) {
      const u = t / len;
      const pr = a.p + (b.p - a.p) * u;
      this._emit(a.x + dx * u, a.y + dy * u, pr, this.travel + t);
      t += this._spacing(pr);
    }
    this.next = t - len;
    this.travel += len;
  }

  _quad(a, c, b) {
    // a -> b, 제어점 c. 길이에 따라 선분으로 분할
    const len = Math.hypot(c.x - a.x, c.y - a.y) + Math.hypot(b.x - c.x, b.y - c.y);
    const n = Math.max(1, Math.min(32, Math.ceil(len / 3)));
    let prev = a;
    for (let i = 1; i <= n; i++) {
      const t = i / n, it = 1 - t;
      const q = {
        x: it * it * a.x + 2 * it * t * c.x + t * t * b.x,
        y: it * it * a.y + 2 * it * t * c.y + t * t * b.y,
        p: a.p + (b.p - a.p) * t,
      };
      this._line(prev, q);
      prev = q;
    }
  }

  /** 필압 다듬기: 펜을 대는 순간의 튀는 값과 떨림을 줄인다 */
  _pressure(p) {
    const f = app.settings.penFeel || {};
    this.np = (this.np || 0) + 1;
    if (f.startRamp && this.np <= 4) p = Math.min(p, 0.15 + this.np * 0.2);
    const k = 1 - Math.min(0.9, f.pressureSmooth ?? 0);
    this.ps = this.ps == null ? p : this.ps + (p - this.ps) * k;
    return this.ps;
  }

  /** 1€ 필터 (Casiez 외, CHI 2012): 느릴 땐 떨림을 많이, 빠를 땐 지연을 적게 */
  _euro(pt) {
    const s = Math.max(1, this.p.stabilizer);
    const minCut = 4 / (1 + 0.5 * s) * (app.settings.penFeel?.euroK ?? 1), beta = 0.006, dCut = 1;
    const t = pt.t ?? (this.et ?? 0) + 8;
    const dt = this.et == null ? 1 / 120 : Math.max(1 / 1000, (t - this.et) / 1000);
    this.et = t;
    const alpha = (cut) => { const tau = 1 / (2 * Math.PI * cut); return 1 / (1 + tau / dt); };
    if (!this.eu) { this.eu = { x: pt.x, y: pt.y, v: 0 }; return { ...pt }; }
    const e = this.eu, z = app.view?.zoom || 1;
    const speed = Math.hypot(pt.x - e.x, pt.y - e.y) * z / dt;          // 화면 px/초
    e.v += (speed - e.v) * alpha(dCut);
    const a = alpha(minCut + beta * e.v);
    e.x += (pt.x - e.x) * a;
    e.y += (pt.y - e.y) * a;
    return { x: e.x, y: e.y, p: pt.p, t };
  }

  /** 보정 방식 (브러시에서 정한 것, 아니면 설정의 것) */
  get stabMode() { const m = this.p.stabMode; return m && m !== 'auto' ? m : (app.settings.penFeel?.filter || 'euro'); }

  /** 끈 당기기: 펜이 끈 길이(반지름)보다 멀어질 때만 선 끝이 끌려온다 — 떨림이 거의 없어짐 */
  _lazy(pt) {
    const R = this.p.stabilizer * 2.5 / (app.view?.zoom || 1);
    this.lzPen = pt;
    if (!this.lz) { this.lz = { x: pt.x, y: pt.y }; return { ...pt }; }
    const dx = pt.x - this.lz.x, dy = pt.y - this.lz.y, d = Math.hypot(dx, dy);
    if (d > R) { const k = (d - R) / d; this.lz.x += dx * k; this.lz.y += dy * k; }
    return { x: this.lz.x, y: this.lz.y, p: pt.p, t: pt.t };
  }

  /** 무거운 펜: 선 끝이 용수철에 매달린 것처럼 살짝 늦게, 부드럽게 따라온다 */
  _spring(pt, steps = null) {
    const tau = 0.006 + this.p.stabilizer * 0.011;            // 늦게 따라오는 정도 (초)
    const w = 1 / tau;
    if (!this.sp) { this.sp = { x: pt.x, y: pt.y, vx: 0, vy: 0, tx: pt.x, ty: pt.y, t: pt.t ?? 0 }; this.lzPen = pt; return { ...pt }; }
    const s = this.sp, t = pt.t ?? s.t + 8;
    const dt = clamp((t - s.t) / 1000, 0.001, 0.05);
    const n = steps || Math.max(1, Math.ceil(dt / 0.004)), hs = dt / n;
    for (let i = 1; i <= n; i++) {
      const tx = s.tx + (pt.x - s.tx) * i / n, ty = s.ty + (pt.y - s.ty) * i / n;   // 목표도 그 사이를 따라 움직임
      s.vx += (w * w * (tx - s.x) - 2 * w * s.vx) * hs; s.vy += (w * w * (ty - s.y) - 2 * w * s.vy) * hs;
      s.x += s.vx * hs; s.y += s.vy * hs;
    }
    s.tx = pt.x; s.ty = pt.y; s.t = t;
    this.lzPen = pt; this.lz = { x: s.x, y: s.y };
    return { x: s.x, y: s.y, p: pt.p, t };
  }

  _smooth(pt) {
    const n = Math.round(this.p.stabilizer);
    pt = { ...pt, p: this._pressure(pt.p) };
    this.raw.push(pt);
    if (n <= 0) return pt;
    const mode = this.stabMode;
    if (mode === 'lazy') return this._lazy(pt);
    if (mode === 'spring') return this._spring(pt);
    if (mode === 'euro') return this._euro(pt);
    const k = Math.min(this.raw.length, n * 2 + 1);
    let sx = 0, sy = 0, sp = 0, sw = 0;
    for (let i = 0; i < k; i++) {
      const q = this.raw[this.raw.length - 1 - i];
      const w = k - i;
      sx += q.x * w; sy += q.y * w; sp += q.p * w; sw += w;
    }
    if (this.raw.length > 64) this.raw.splice(0, this.raw.length - 64);
    return { x: sx / sw, y: sy / sw, p: sp / sw, t: pt.t };
  }

  _push(pt) {
    if (this.rec) this.rec.push(+pt.x.toFixed(2), +pt.y.toFixed(2), +pt.p.toFixed(3), Math.round(pt.t || 0));
    const pts = this.pts;
    // 속도 (화면 px/ms) — 속도에 따른 굵기용
    const prev = pts[pts.length - 1];
    if (prev && pt.t != null && prev.t != null && pt.t > prev.t) {
      const v = Math.hypot(pt.x - prev.x, pt.y - prev.y) * (app.view?.zoom || 1) / (pt.t - prev.t);
      this.vel = (this.vel || 0) + (v - (this.vel || 0)) * 0.3;
    }
    pts.push(pt);
    const n = pts.length;
    if (n === 1) {
      this._emit(pt.x, pt.y, pt.p, 0);
      this.next = this._spacing(pt.p);
      this.lastMid = pt;
      return;
    }
    const a = pts[n - 2], b = pts[n - 1];
    const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, p: (a.p + b.p) / 2 };
    if (n === 2) this._line(this.lastMid, mid);
    else this._quad(this.lastMid, a, mid);
    this.lastMid = mid;
    if (pts.length > 4) pts.shift();
  }

  /** 미뤄 둔 합쳐 칠하기를 지금 하기 (선택 영역 자르기도 그 다음에) */
  _syncUnion() {
    this.uq = false;
    const b = this.udirty;
    if (!b) return;
    this.udirty = null;
    const rb = this._unionRedraw(b);
    this.ctx.setTransform(1, 0, 0, 1, 0, 0);
    this.ctx.globalAlpha = 1;
    if (!this.noClip) this.doc.selection.clipTo(this.ctx, rb);
  }

  /** 합쳐 칠할 원 하나 기억 (64px 칸으로 나눠 두어 근처 것만 빨리 찾음) */
  _udab(x, y, r, a) {
    const i = this.ud.length, C = 64;
    this.ud.push({ x, y, r, a });
    for (let cy = Math.floor((y - r - 1) / C); cy <= Math.floor((y + r + 1) / C); cy++) {
      for (let cx = Math.floor((x - r - 1) / C); cx <= Math.floor((x + r + 1) / C); cx++) {
        const k = cx * 100003 + cy;
        let l = this.ug.get(k);
        if (!l) this.ug.set(k, (l = []));
        l.push(i);
      }
    }
  }

  /** 바뀐 영역을 지우고, 그 영역에 닿는 원들을 하나의 모양으로 한 번에 칠한다 → 겹친 가장자리가 진해지지 않음 */
  _unionRedraw(b) {
    const ctx = this.ctx, C = 64;
    const x0 = Math.floor(b.x), y0 = Math.floor(b.y), x1 = Math.ceil(b.x + b.w), y1 = Math.ceil(b.y + b.h);
    const full = new Path2D(), faint = [], seen = new Set();
    for (let cy = Math.floor(y0 / C); cy <= Math.floor(y1 / C); cy++) {
      for (let cx = Math.floor(x0 / C); cx <= Math.floor(x1 / C); cx++) {
        for (const i of this.ug.get(cx * 100003 + cy) || []) {
          if (seen.has(i)) continue;
          seen.add(i);
          const d = this.ud[i];
          if (d.x + d.r + 1 < x0 || d.x - d.r - 1 > x1 || d.y + d.r + 1 < y0 || d.y - d.r - 1 > y1) continue;
          if (d.a >= 0.999) { full.moveTo(d.x + d.r, d.y); full.arc(d.x, d.y, d.r, 0, Math.PI * 2); } else faint.push(d);
        }
      }
    }
    ctx.save();
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.beginPath(); ctx.rect(x0, y0, x1 - x0, y1 - y0); ctx.clip();
    ctx.clearRect(x0, y0, x1 - x0, y1 - y0);
    ctx.globalCompositeOperation = 'source-over';
    ctx.fillStyle = this.color;
    ctx.globalAlpha = 1;
    ctx.fill(full);
    for (const d of faint) { ctx.globalAlpha = d.a; ctx.beginPath(); ctx.arc(d.x, d.y, d.r, 0, Math.PI * 2); ctx.fill(); }   // 아주 가는 끝부분
    ctx.restore();
    return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
  }

  _flush() {
    if (!this.batch) return null;
    let b = this.batch;
    this.batch = null;
    if (this.union && this.ud.length) {
      // 합쳐 칠하기는 이벤트 하나(펜 점 여러 개)가 끝난 뒤 한 번만 (점마다 하면 느림)
      b = { x: Math.floor(b.x), y: Math.floor(b.y), w: Math.ceil(b.x + b.w) - Math.floor(b.x), h: Math.ceil(b.y + b.h) - Math.floor(b.y) };
      this.udirty = rectUnion(this.udirty, b);
      this.total = rectUnion(this.total, b);
      if (!this.uq) { this.uq = true; queueMicrotask(() => this._syncUnion()); }
      app.requestRender();
      return b;
    }
    this.ctx.setTransform(1, 0, 0, 1, 0, 0);
    this.ctx.globalAlpha = 1;
    if (!this.noClip) this.doc.selection.clipTo(this.ctx, b);
    this.total = rectUnion(this.total, b);
    app.requestRender();
    return b;
  }

  begin(pt) {
    this.ctx.save();
    this.ctx.fillStyle = this.color;
    this._push(this._smooth(pt));
    this._flush();
  }

  add(pt) {
    this._push(this._smooth(pt));
    this._flush();
  }

  end(lastPt) {
    // 보정 지연분을 마지막 지점까지 따라잡기 (필압은 지금 값 → 펜을 뗄 때 잰 값으로 점점, 끝이 뭉툭하게 뭉치지 않게)
    if (lastPt && this.p.stabilizer > 0 && this.p.catchUp !== false) {
      const p0 = this.ps ?? lastPt.p, p1 = Math.min(p0, lastPt.p ?? p0);
      const pAt = (i, n) => p0 + (p1 - p0) * (i / n);
      if (this.sp) {
        // 무거운 펜: 펜 끝에 멈춰 설 때까지 조금 더 움직이기
        const t0 = this.sp.t;
        for (let i = 1; i <= 12; i++) this._push(this._spring({ ...lastPt, p: pAt(i, 12), t: t0 + i * 16 }));
        this._push({ x: lastPt.x, y: lastPt.y, p: p1, t: t0 + 220 });
      } else if (this.lz) {
        this._push({ x: lastPt.x, y: lastPt.y, p: p1, t: lastPt.t });
      } else if (this.eu) {
        const t0 = this.et ?? 0;
        for (let i = 1; i <= 8; i++) this._push(this._euro({ ...lastPt, p: pAt(i, 8), t: t0 + i * 12 }));
        this._push({ x: lastPt.x, y: lastPt.y, p: p1, t: t0 + 120 });
      } else for (let i = 0; i < this.p.stabilizer * 2 + 1; i++) this._push(this._smooth(lastPt));
    }
    const last = this.pts[this.pts.length - 1];
    if (last && this.lastMid && this.pts.length > 1) this._line(this.lastMid, last);
    if (this.queue.length) this._flushTaper();
    this._flush();
    this._syncUnion();
    this.ctx.restore();
    return this.total;
  }

  /** 보정이 끝난 점들로 다시 그리기 (벡터 레이어) */
  replay(points) {
    this.ctx.save();
    this.ctx.fillStyle = this.color;
    for (const pt of points) this._push(pt);
    const last = this.pts[this.pts.length - 1];
    if (last && this.lastMid && this.pts.length > 1) this._line(this.lastMid, last);
    if (this.queue.length) this._flushTaper();
    this._flush();
    this._syncUnion();
    this.ctx.restore();
    return this.total;
  }

  /** 버퍼 내용을 지우고 다시 그리기 (직선/도형 미리보기용) */
  reset() {
    if (this.total) {
      this.ctx.setTransform(1, 0, 0, 1, 0, 0);
      this.ctx.clearRect(this.total.x, this.total.y, this.total.w, this.total.h);
    }
    this.raw = []; this.pts = []; this.next = 0; this.batch = null; this.lastMid = null;
    this.travel = 0; this.queue = [];
    this.ud = []; this.ug = new Map(); this.udirty = null;
    const keep = this.total;
    this.total = null;
    return keep;
  }

  /** 점 목록을 보정 없이 그대로 그린다 */
  polyline(points, closed) {
    const saved = this.p.stabilizer;
    this.p = { ...this.p, stabilizer: 0 };
    this.noTaper = true;
    this.ctx.save();
    this.ctx.fillStyle = this.color;
    const list = closed ? [...points, points[0]] : points;
    this._dab(list[0].x, list[0].y, list[0].p);
    this.next = this._spacing(list[0].p);
    for (let i = 1; i < list.length; i++) this._line(list[i - 1], list[i]);
    this._flush();
    this._syncUnion();                       // 도형·직선은 바로 이어서 확정하는 경우가 있어 지금 칠함
    this.ctx.restore();
    this.p = { ...this.p, stabilizer: saved };
    this.noTaper = false;
    return this.total;
  }
}

/** 버퍼를 레이어에 확정 */
export function commitBuffer(layer, buf, rect, mode, opacity, blend = 'source-over', preset = null) {
  if (hasBrushPost(preset)) applyGrain(buf.getContext('2d'), layer.doc || app.doc, preset, rect);
  // 팔레트 고정 작품: 칠한 색을 팔레트 색으로, 반투명 가장자리는 없앰
  const pd = layer.doc || app.doc;
  if (pd?.pixel?.lock && mode !== 'erase') snapToPalette(buf.getContext('2d'), rect, paletteOf(pd));
  const ctx = layer.ctx;
  ctx.save();
  ctx.globalAlpha = opacity;
  ctx.globalCompositeOperation = mode === 'erase' ? 'destination-out' : (layer.alphaLock ? 'source-atop' : blend || 'source-over');
  ctx.drawImage(buf, rect.x, rect.y, rect.w, rect.h, rect.x, rect.y, rect.w, rect.h);
  ctx.restore();
  buf.getContext('2d').clearRect(rect.x - 1, rect.y - 1, rect.w + 2, rect.h + 2);
}

let strokeBuf = null;
export function getStrokeBuffer(doc) {
  if (!strokeBuf || strokeBuf.width !== doc.width || strokeBuf.height !== doc.height) {
    strokeBuf = createCanvas(doc.width, doc.height);
  }
  return strokeBuf;
}

export function rgbaString(hex, a) {
  const { r, g, b } = hexToRgb(hex);
  return `rgba(${r},${g},${b},${a})`;
}
