// SVG 필터 엔진: 캔버스 ctx.filter = 'url(#id)' 로 GPU 에서 색 보정/효과를 처리한다.
// 조정 레이어, 보정 미리보기, 레이어 효과, 언샤프 마스크 등이 모두 이걸 쓴다.

import { sanitizeInto } from './util.js';

const NS = 'http://www.w3.org/2000/svg';
let defs = null;

function ensureDefs() {
  if (defs) return defs;
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('width', '0');
  svg.setAttribute('height', '0');
  svg.style.cssText = 'position:absolute;width:0;height:0;pointer-events:none';
  defs = document.createElementNS(NS, 'defs');
  svg.append(defs);
  document.body.append(svg);
  return defs;
}

/** id 로 필터를 만들거나 갱신하고 'url(#id)' 를 돌려준다. 영역은 캔버스 전체(w,h) */
const made = [];
export function defineFilter(id, inner, w = 4096, h = 4096) {
  const d = ensureDefs();
  // 캔버스 크기마다 따로 (화면/내비게이터/내보내기가 같은 필터를 공유하면 영역이 엇갈림)
  // 내용이 바뀌면 새 id (브라우저가 같은 url 의 필터를 캐시해 옛 값을 쓰는 걸 막음)
  let hsh = 5381;
  for (let i = 0; i < inner.length; i++) hsh = ((hsh << 5) + hsh + inner.charCodeAt(i)) | 0;
  id = `${id}_${Math.ceil(w)}x${Math.ceil(h)}_${(hsh >>> 0).toString(36)}`;
  let f = d.querySelector(`#${CSS.escape(id)}`);
  if (!f) {
    made.push(id);
    if (made.length > 300) d.querySelector(`#${CSS.escape(made.shift())}`)?.remove();
  }
  if (!f) {
    f = document.createElementNS(NS, 'filter');
    f.id = id;
    f.setAttribute('filterUnits', 'userSpaceOnUse');
    f.setAttribute('color-interpolation-filters', 'sRGB');
    d.append(f);
  }
  // 영역은 그리는 캔버스 크기와 정확히 같게 (너무 크면 브라우저가 일부만 처리함)
  f.setAttribute('x', '0'); f.setAttribute('y', '0');
  f.setAttribute('width', String(Math.ceil(w))); f.setAttribute('height', String(Math.ceil(h)));
  if (f._inner !== inner) {
    // 작품 파일 속 보정 값이 그대로 들어가므로, 필터 부품(fe…)만 남기고 넣는다
    const t = document.createElement('template');
    t.innerHTML = `<svg xmlns="${NS}">${inner}</svg>`;
    const svg = t.content.firstElementChild;
    sanitizeInto(svg, (n) => /^fe[A-Za-z]+$/.test(n));
    f.replaceChildren(...svg.childNodes);
    f._inner = inner;
  }
  return `url(#${id})`;
}

const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
const table = (fn, n = 64) => Array.from({ length: n }, (_, i) => clamp01(fn(i / (n - 1))).toFixed(4)).join(' ');

/** 단조 3차 보간 (곡선 보정용). pts: [[x,y],...] 0~255 */
export function curveFn(pts) {
  const p = [...pts].sort((a, b) => a[0] - b[0]).map(([x, y]) => [x / 255, y / 255]);
  if (p.length < 2) return (v) => v;
  const n = p.length, dx = [], m = [], sl = [];
  for (let i = 0; i < n - 1; i++) { dx[i] = p[i + 1][0] - p[i][0] || 1e-6; sl[i] = (p[i + 1][1] - p[i][1]) / dx[i]; }
  m[0] = sl[0]; m[n - 1] = sl[n - 2];
  for (let i = 1; i < n - 1; i++) m[i] = sl[i - 1] * sl[i] <= 0 ? 0 : (sl[i - 1] + sl[i]) / 2;
  for (let i = 0; i < n - 1; i++) {
    if (sl[i] === 0) { m[i] = m[i + 1] = 0; continue; }
    const a = m[i] / sl[i], b = m[i + 1] / sl[i], s = a * a + b * b;
    if (s > 9) { const t = 3 / Math.sqrt(s); m[i] = t * a * sl[i]; m[i + 1] = t * b * sl[i]; }
  }
  return (x) => {
    if (x <= p[0][0]) return p[0][1];
    if (x >= p[n - 1][0]) return p[n - 1][1];
    let i = 0;
    while (i < n - 2 && x > p[i + 1][0]) i++;
    const h = dx[i], t = (x - p[i][0]) / h, t2 = t * t, t3 = t2 * t;
    return (2 * t3 - 3 * t2 + 1) * p[i][1] + (t3 - 2 * t2 + t) * h * m[i] + (-2 * t3 + 3 * t2) * p[i + 1][1] + (t3 - t2) * h * m[i + 1];
  };
}

const hexRGB = (hex) => { const n = parseInt(hex.slice(1), 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255].map(v => v / 255); };
const LUM = '0.299 0.587 0.114 0 0';

/** 그라데이션 색 정지점 -> 0~1 사이 값의 색 */
function gradSample(stops, t) {
  const s = [...stops].sort((a, b) => a.pos - b.pos);
  if (t <= s[0].pos) return hexRGB(s[0].color);
  for (let i = 0; i < s.length - 1; i++) {
    if (t <= s[i + 1].pos) {
      const u = (t - s[i].pos) / ((s[i + 1].pos - s[i].pos) || 1);
      const a = hexRGB(s[i].color), b = hexRGB(s[i + 1].color);
      return a.map((v, k) => v + (b[k] - v) * u);
    }
  }
  return hexRGB(s[s.length - 1].color);
}

export const ADJUST_TYPES = {
  levels: { name: '레벨 (밝기 범위)', def: { inB: 0, inW: 255, gamma: 1, outB: 0, outW: 255 } },
  curves: { name: '톤 커브 (곡선)', def: { master: [[0, 0], [255, 255]], r: [[0, 0], [255, 255]], g: [[0, 0], [255, 255]], b: [[0, 0], [255, 255]] } },
  hsl: { name: '색조/채도/명도 (HSL)', def: { hue: 0, sat: 0, light: 0 } },
  balance: { name: '색상 균형', def: { sr: 0, sg: 0, sb: 0, mr: 0, mg: 0, mb: 0, hr: 0, hg: 0, hb: 0 } },
  bc: { name: '밝기/대비', def: { b: 0, c: 0 } },
  gradmap: { name: '그라데이션 맵', def: { stops: [{ pos: 0, color: '#1b1430' }, { pos: 0.5, color: '#c2577a' }, { pos: 1, color: '#ffe9c4' }] } },
  posterize: { name: '포스터화', def: { levels: 4 } },
  threshold: { name: '2값화 (한계값)', def: { t: 128 } },
  invert: { name: '반전', def: {} },
  bw: { name: '흑백', def: { r: 30, g: 59, b: 11 } },
  colorize: { name: '단색 입히기 (세피아 등)', def: { color: '#c08040', amount: 100 } },
  vibrance: { name: '자연 채도', def: { v: 30 } },
};

/** 조정 종류와 값 -> SVG 필터 내용 */
export function adjustInner(type, p) {
  p = { ...ADJUST_TYPES[type]?.def, ...p };
  const ct = (r, g, b) => `<feComponentTransfer><feFuncR type="table" tableValues="${r}"/><feFuncG type="table" tableValues="${g}"/><feFuncB type="table" tableValues="${b}"/></feComponentTransfer>`;
  switch (type) {
    case 'levels': {
      const t = table((v) => {
        const x = clamp01((v * 255 - p.inB) / Math.max(1, p.inW - p.inB));
        return (p.outB + (p.outW - p.outB) * Math.pow(x, 1 / Math.max(0.05, p.gamma))) / 255;
      }, 128);
      return ct(t, t, t);
    }
    case 'curves': {
      const M = curveFn(p.master), R = curveFn(p.r), G = curveFn(p.g), B = curveFn(p.b);
      return ct(table(v => R(M(v)), 128), table(v => G(M(v)), 128), table(v => B(M(v)), 128));
    }
    case 'hsl': {
      const l = p.light / 100;
      const slope = l > 0 ? 1 - l : 1 + l, icpt = l > 0 ? l : 0;
      return `<feColorMatrix type="hueRotate" values="${p.hue}"/><feColorMatrix type="saturate" values="${Math.max(0, 1 + p.sat / 100)}"/>`
        + `<feComponentTransfer><feFuncR type="linear" slope="${slope}" intercept="${icpt}"/><feFuncG type="linear" slope="${slope}" intercept="${icpt}"/><feFuncB type="linear" slope="${slope}" intercept="${icpt}"/></feComponentTransfer>`;
    }
    case 'balance': {
      const f = (s, m, h) => table((v) => v + (s / 100) * 0.4 * Math.pow(1 - v, 2) + (m / 100) * 0.4 * 4 * v * (1 - v) + (h / 100) * 0.4 * v * v, 128);
      return ct(f(p.sr, p.mr, p.hr), f(p.sg, p.mg, p.hg), f(p.sb, p.mb, p.hb));
    }
    case 'bc': {
      const c = Math.tan(((p.c + 100) / 200) * Math.PI / 2), t = table(v => (v - 0.5) * c + 0.5 + p.b / 200, 128);
      return ct(t, t, t);
    }
    case 'gradmap': {
      const n = 64, rs = [], gs = [], bs = [];
      for (let i = 0; i < n; i++) { const [r, g, b] = gradSample(p.stops, i / (n - 1)); rs.push(r.toFixed(4)); gs.push(g.toFixed(4)); bs.push(b.toFixed(4)); }
      return `<feColorMatrix type="matrix" values="${LUM} ${LUM} ${LUM} 0 0 0 1 0"/>` + ct(rs.join(' '), gs.join(' '), bs.join(' '));
    }
    case 'posterize': {
      const n = Math.max(2, Math.round(p.levels)), v = Array.from({ length: n }, (_, i) => (i / (n - 1)).toFixed(4)).join(' ');
      return `<feComponentTransfer><feFuncR type="discrete" tableValues="${v}"/><feFuncG type="discrete" tableValues="${v}"/><feFuncB type="discrete" tableValues="${v}"/></feComponentTransfer>`;
    }
    case 'threshold': {
      const n = 256, v = Array.from({ length: n }, (_, i) => (i >= p.t ? 1 : 0)).join(' ');
      return `<feColorMatrix type="matrix" values="${LUM} ${LUM} ${LUM} 0 0 0 1 0"/><feComponentTransfer><feFuncR type="discrete" tableValues="${v}"/><feFuncG type="discrete" tableValues="${v}"/><feFuncB type="discrete" tableValues="${v}"/></feComponentTransfer>`;
    }
    case 'invert': return '<feComponentTransfer><feFuncR type="table" tableValues="1 0"/><feFuncG type="table" tableValues="1 0"/><feFuncB type="table" tableValues="1 0"/></feComponentTransfer>';
    case 'bw': {
      const s = (p.r + p.g + p.b) || 1, w = `${p.r / s} ${p.g / s} ${p.b / s} 0 0`;
      return `<feColorMatrix type="matrix" values="${w} ${w} ${w} 0 0 0 1 0"/>`;
    }
    case 'colorize': {
      // 밝기 × 지정 색 (amount 만큼 원래 색과 섞음)
      const [r, g, b] = hexRGB(p.color), a = p.amount / 100;
      const row = (c) => `${(0.299 * c * 1.6).toFixed(4)} ${(0.587 * c * 1.6).toFixed(4)} ${(0.114 * c * 1.6).toFixed(4)} 0 0`;
      return `<feColorMatrix in="SourceGraphic" type="matrix" values="${row(r)} ${row(g)} ${row(b)} 0 0 0 1 0" result="tint"/>`
        + `<feComposite in="tint" in2="SourceGraphic" operator="arithmetic" k1="0" k2="${a.toFixed(3)}" k3="${(1 - a).toFixed(3)}" k4="0"/>`;
    }
    case 'vibrance': return `<feColorMatrix type="saturate" values="${1 + p.v / 100}"/>`;
    default: return '';
  }
}

export function adjustFilter(id, type, params, w, h) {
  return defineFilter('adj-' + id, adjustInner(type, params), w, h);
}

/** 레이어 효과 (테두리 / 그림자 / 외부 광선). s = 문서->대상 배율 */
export function fxFilter(id, fx, s, w, h) {
  const parts = [], merge = [];
  const k = (v) => (v * s).toFixed(2);
  if (fx.shadow?.on) {
    const o = fx.shadow;
    parts.push(`<feGaussianBlur in="SourceAlpha" stdDeviation="${k(o.blur / 2)}" result="sb"/><feOffset in="sb" dx="${k(o.dx)}" dy="${k(o.dy)}" result="so"/><feFlood flood-color="${o.color}" flood-opacity="${o.opacity}"/><feComposite in2="so" operator="in" result="shadow"/>`);
    merge.push('shadow');
  }
  if (fx.glow?.on) {
    const o = fx.glow;
    parts.push(`<feMorphology in="SourceAlpha" operator="dilate" radius="${k(o.size / 3)}" result="gd"/><feGaussianBlur in="gd" stdDeviation="${k(o.size / 2)}" result="gb"/><feFlood flood-color="${o.color}" flood-opacity="${o.opacity}"/><feComposite in2="gb" operator="in" result="glow"/>`);
    merge.push('glow');
  }
  if (fx.stroke?.on) {
    const o = fx.stroke;
    parts.push(`<feMorphology in="SourceAlpha" operator="dilate" radius="${k(o.size)}" result="sd"/><feFlood flood-color="${o.color}"/><feComposite in2="sd" operator="in" result="stroke"/>`);
    merge.push('stroke');
  }
  if (!merge.length) return null;
  const inner = parts.join('') + `<feMerge>${merge.map(m => `<feMergeNode in="${m}"/>`).join('')}<feMergeNode in="SourceGraphic"/></feMerge>`;
  return defineFilter(`fx-${id}-${Math.round(s * 1000)}`, inner, w, h);
}

export function hasFx(l) { return !!(l.fx && (l.fx.stroke?.on || l.fx.shadow?.on || l.fx.glow?.on)); }

export function unsharpFilter(radius, amount, w, h) {
  return defineFilter(`usm-${radius}-${amount}`, `<feGaussianBlur in="SourceGraphic" stdDeviation="${radius}" result="b"/><feComposite in="SourceGraphic" in2="b" operator="arithmetic" k1="0" k2="${1 + amount}" k3="${-amount}" k4="0"/>`, w, h);
}

export function noiseFilter(amount, mono, seed, w, h) {
  const a = amount / 100;
  return defineFilter(`noise-${amount}-${mono ? 1 : 0}-${seed}`,
    `<feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" seed="${seed}" result="n"/>`
    + (mono ? '<feColorMatrix in="n" type="saturate" values="0" result="n"/>' : '')
    + `<feComposite in="n" in2="SourceGraphic" operator="arithmetic" k1="0" k2="${a}" k3="1" k4="${-a / 2}" result="m"/><feComposite in="m" in2="SourceGraphic" operator="in"/>`, w, h);
}
