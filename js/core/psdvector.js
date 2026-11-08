// PSD 셰이프 레이어 만들기: 말풍선·컷·벡터 레이어를 포토샵에서 고칠 수 있는 모양(벡터 마스크 + 단색 칠 + 선)으로.
//  - 모양: 레이어(또는 일부)를 칠한 알파를 윤곽선(마칭 스퀘어)으로 따서 겹치지 않는 고리들로 → 바깥은 합치기, 구멍은 빼기
//  - 'SoCo' (단색 칠 레이어) + 'vmsk' (벡터 마스크 경로) + 'vstk' (선 스타일)
//  - 레이어 그림(픽셀)은 그대로 함께 저장하므로 벡터를 모르는 프로그램에서도 똑같이 보인다
import { Buf, descriptorBlock, keyedBlock } from './psdtext.js';
import { createCanvas } from './util.js';

// ---- 색 ---------------------------------------------------------------------
let colorCtx = null;
/** CSS 색 → [r, g, b, a(0~1)] */
export function cssRGB(c) {
  colorCtx ||= createCanvas(1, 1).getContext('2d', { willReadFrequently: true });
  colorCtx.clearRect(0, 0, 1, 1);
  colorCtx.fillStyle = '#000';
  colorCtx.fillStyle = c || '#000';
  colorCtx.fillRect(0, 0, 1, 1);
  const d = colorCtx.getImageData(0, 0, 1, 1).data;
  return [d[0], d[1], d[2], d[3] / 255];
}
const rgbObj = ([r, g, b]) => ({ t: 'Objc', cls: 'RGBC', items: [['Rd  ', { t: 'doub', v: r }], ['Grn ', { t: 'doub', v: g }], ['Bl  ', { t: 'doub', v: b }]] });

// ---- 윤곽선 따기 (마칭 스퀘어) ---------------------------------------------------
// 모서리 비트: TL=8, TR=4, BR=2, BL=1. 변: T, R, B, L
const EDGE_MID = { T: [0.5, 0], R: [1, 0.5], B: [0.5, 1], L: [0, 0.5] };
const CORNER = { 8: [0, 0], 4: [1, 0], 2: [1, 1], 1: [0, 1] };
const ISOLATE = { 8: ['T', 'L'], 4: ['T', 'R'], 2: ['R', 'B'], 1: ['B', 'L'] };
/** 선분(변 a→b)이 기준 모서리를 안쪽이면 왼쪽(외적 > 0), 바깥이면 오른쪽에 두도록 방향을 정한다 */
function orient(a, b, corner, inside) {
  const P = EDGE_MID[a], Q = EDGE_MID[b], C = CORNER[corner];
  const cross = (Q[0] - P[0]) * (C[1] - P[1]) - (Q[1] - P[1]) * (C[0] - P[0]);
  return (cross > 0) === inside ? [a, b] : [b, a];
}
const CASES = (() => {
  const out = [];
  for (let c = 0; c < 16; c++) {
    const ins = (bit) => !!(c & bit);
    const crossed = [];
    if (ins(8) !== ins(4)) crossed.push('T');
    if (ins(4) !== ins(2)) crossed.push('R');
    if (ins(2) !== ins(1)) crossed.push('B');
    if (ins(1) !== ins(8)) crossed.push('L');
    if (crossed.length === 2) {
      const ref = [8, 4, 2, 1].find(ins);
      out.push({ segs: [orient(crossed[0], crossed[1], ref, true)] });
    } else if (crossed.length === 4) {
      // 안장점: 가운데 값이 안쪽이면 바깥 모서리를 떼어 내고, 아니면 안쪽 모서리를 떼어 낸다
      const insideCorners = [8, 4, 2, 1].filter(ins), outsideCorners = [8, 4, 2, 1].filter(b => !ins(b));
      out.push({
        saddle: true,
        centerIn: outsideCorners.map(k => orient(...ISOLATE[k], k, false)),
        centerOut: insideCorners.map(k => orient(...ISOLATE[k], k, true)),
      });
    } else out.push({ segs: [] });
  }
  return out;
})();

/**
 * 한 채널 값(0~255, w×h)에서 th 이상인 곳의 윤곽선 고리들을 문서 좌표로.
 * 반환: [{ pts: [x0,y0,x1,y1,...], area }] — 바깥 고리와 구멍 고리는 area 부호가 반대
 */
export function traceContours(V, w, h, ox = 0, oy = 0, th = 128) {
  const W2 = w + 2;
  const val = (i, j) => (i < 0 || j < 0 || i >= w || j >= h ? 0 : V[j * w + i]);
  const keyH = (i, j) => ((j + 1) * W2 + (i + 1)) * 2;
  const keyV = (i, j) => ((j + 1) * W2 + (i + 1)) * 2 + 1;
  const segStart = new Map();   // 시작점 키 -> [끝점 키, 시작 x, 시작 y]
  const lerp = (a, b) => { const d = b - a; return d === 0 ? 0.5 : Math.min(1, Math.max(0, (th - a) / d)); };
  for (let j = -1; j < h; j++) {
    for (let i = -1; i < w; i++) {
      const tl = val(i, j), tr = val(i + 1, j), br = val(i + 1, j + 1), bl = val(i, j + 1);
      const c = (tl >= th ? 8 : 0) | (tr >= th ? 4 : 0) | (br >= th ? 2 : 0) | (bl >= th ? 1 : 0);
      if (c === 0 || c === 15) continue;
      const cs = CASES[c];
      const segs = cs.saddle ? ((tl + tr + br + bl) / 4 >= th ? cs.centerIn : cs.centerOut) : cs.segs;
      const point = (e) => {
        switch (e) {
          case 'T': return [keyH(i, j), i + lerp(tl, tr), j];
          case 'B': return [keyH(i, j + 1), i + lerp(bl, br), j + 1];
          case 'L': return [keyV(i, j), i, j + lerp(tl, bl)];
          default: return [keyV(i + 1, j), i + 1, j + lerp(tr, br)];
        }
      };
      for (const [a, b] of segs) {
        const A = point(a), B = point(b);
        segStart.set(A[0], [B[0], A[1], A[2]]);
      }
    }
  }
  const loops = [];
  for (const [k0] of segStart) {
    if (!segStart.has(k0)) continue;
    const pts = [];
    let k = k0, guard = 0;
    while (segStart.has(k) && guard++ < 1e7) {
      const [nk, x, y] = segStart.get(k);
      segStart.delete(k);
      pts.push(x + 0.5 + ox, y + 0.5 + oy);   // 값은 픽셀 가운데에 있다
      k = nk;
    }
    if (pts.length < 6) continue;
    let area = 0;
    for (let p = 0, n = pts.length; p < n; p += 2) { const q = (p + 2) % n; area += pts[p] * pts[q + 1] - pts[q] * pts[p + 1]; }
    loops.push({ pts, area: area / 2 });
  }
  return loops;
}

/** 닫힌 고리를 더글러스-포이커로 줄이기 (tol 픽셀) */
export function simplifyLoop(pts, tol = 0.4) {
  const n = pts.length / 2;
  if (n <= 4) return pts;
  // 첫 점에서 가장 먼 점으로 둘로 나눠 각각 줄인다
  let far = 0, fd = -1;
  for (let i = 1; i < n; i++) { const d = (pts[i * 2] - pts[0]) ** 2 + (pts[i * 2 + 1] - pts[1]) ** 2; if (d > fd) { fd = d; far = i; } }
  const keep = new Uint8Array(n);
  keep[0] = keep[far] = 1;
  const stack = [[0, far], [far, n]];
  const P = (i) => [pts[(i % n) * 2], pts[(i % n) * 2 + 1]];
  while (stack.length) {
    const [a, b] = stack.pop();
    if (b - a < 2) continue;
    const [ax, ay] = P(a), [bx, by] = P(b);
    const dx = bx - ax, dy = by - ay, L = Math.hypot(dx, dy) || 1;
    let mi = -1, md = tol;
    for (let i = a + 1; i < b; i++) {
      const [x, y] = P(i);
      const d = Math.abs((x - ax) * dy - (y - ay) * dx) / L;
      if (d > md) { md = d; mi = i; }
    }
    if (mi >= 0) { keep[mi % n] = 1; stack.push([a, mi], [mi, b]); }
  }
  const out = [];
  for (let i = 0; i < n; i++) if (keep[i]) out.push(pts[i * 2], pts[i * 2 + 1]);
  return out;
}

/**
 * 캔버스의 알파(또는 keep 함수로 고른 픽셀)에서 모양 고리들.
 * 반환: [{ pts, hole }] — 가장 큰 고리의 방향을 바깥으로 보고, 반대 방향은 구멍
 */
export function shapeLoopsFromCanvas(canvas, bounds, keep = null) {
  if (!bounds || !bounds.w || !bounds.h) return [];
  const { x: bx, y: by, w, h } = bounds;
  const d = canvas.getContext('2d').getImageData(bx, by, w, h).data;
  const V = new Uint8Array(w * h);
  for (let i = 0, p = 0; i < w * h; i++, p += 4) V[i] = keep ? (keep(d[p], d[p + 1], d[p + 2], d[p + 3]) ? d[p + 3] : 0) : d[p + 3];
  const loops = traceContours(V, w, h, bx, by).filter(l => Math.abs(l.area) >= 0.6);
  if (!loops.length) return [];
  const outerSign = Math.sign(loops.reduce((m, l) => (Math.abs(l.area) > Math.abs(m.area) ? l : m)).area);
  return loops.map(l => ({ pts: simplifyLoop(l.pts), hole: Math.sign(l.area) !== outerSign }));
}

// ---- 포토샵 경로 레코드 -----------------------------------------------------------
/**
 * 벡터 마스크 경로 레코드 (각 26바이트). 좌표는 문서 크기에 대한 8.24 고정소수 (세로, 가로 순).
 * 하위 경로마다 겹치기 방식: 1 = 합치기, 2 = 빼기 (구멍)
 */
export function pathRecords(loops, W, H) {
  const b = new Buf();
  const zeros = (n) => { for (let i = 0; i < n; i++) b.u8(0); };
  b.u16(6); zeros(24);                       // 경로 채우기 규칙
  b.u16(8); b.u16(0); zeros(22);             // 처음 채우기: 비어 있음에서 시작
  const fx = (v, size) => Math.round((v / size) * 0x1000000);
  for (const l of loops) {
    const n = l.pts.length / 2;
    b.u16(0); b.u16(n); b.u16(l.hole ? 2 : 1); b.u16(1); zeros(18);   // 닫힌 하위 경로 길이
    for (let i = 0; i < n; i++) {
      const x = l.pts[i * 2], y = l.pts[i * 2 + 1];
      b.u16(2);                                // 닫힌 하위 경로의 꺾인 점 (조절점 = 점)
      for (let k = 0; k < 3; k++) { b.i32(fx(y, H)); b.i32(fx(x, W)); }
    }
  }
  return b.bytes();
}

/** 'vmsk' 블록 (버전 3, 플래그 0) */
export function vmskBlock(loops, W, H) {
  const d = new Buf();
  d.u32(3); d.u32(0);
  d.push(pathRecords(loops, W, H));
  return keyedBlock('vmsk', d.bytes());
}

/** 'SoCo' 단색 칠 */
export function socoBlock(color) {
  return descriptorBlock('SoCo', 'null', [['Clr ', rgbObj(cssRGB(color))]]);
}

/** 'vstk' 선 스타일. align: 'inside' | 'center' | 'outside' */
export function vstkBlock({ stroke = true, fill = true, width = 0, color = '#000000', align = 'center', join = 'round', opacity = 100 }) {
  const alignKey = { inside: 'strokeStyleAlignInside', center: 'strokeStyleAlignCenter', outside: 'strokeStyleAlignOutside' }[align];
  const joinKey = { round: 'strokeStyleRoundJoin', miter: 'strokeStyleMiterJoin', bevel: 'strokeStyleBevelJoin' }[join];
  return descriptorBlock('vstk', 'strokeStyle', [
    ['strokeStyleVersion', { t: 'long', v: 2 }],
    ['strokeEnabled', { t: 'bool', v: !!stroke && width > 0 }],
    ['fillEnabled', { t: 'bool', v: !!fill }],
    ['strokeStyleLineWidth', { t: 'UntF', unit: '#Pxl', v: width }],
    ['strokeStyleLineDashOffset', { t: 'UntF', unit: '#Pnt', v: 0 }],
    ['strokeStyleMiterLimit', { t: 'doub', v: 100 }],
    ['strokeStyleLineCapType', { t: 'enum', type: 'strokeStyleLineCapType', v: 'strokeStyleButtCap' }],
    ['strokeStyleLineJoinType', { t: 'enum', type: 'strokeStyleLineJoinType', v: joinKey }],
    ['strokeStyleLineAlignment', { t: 'enum', type: 'strokeStyleLineAlignment', v: alignKey }],
    ['strokeStyleScaleLock', { t: 'bool', v: false }],
    ['strokeStyleStrokeAdjust', { t: 'bool', v: false }],
    ['strokeStyleLineDashSet', { t: 'VlLs', v: [] }],
    ['strokeStyleBlendMode', { t: 'enum', type: 'BlnM', v: 'Nrml' }],
    ['strokeStyleOpacity', { t: 'UntF', unit: '#Prc', v: opacity }],
    ['strokeStyleContent', { t: 'Objc', cls: 'solidColorLayer', items: [['Clr ', rgbObj(cssRGB(color))]] }],
    ['strokeStyleResolution', { t: 'doub', v: 72 }],
  ]);
}

// ---- 읽기 (열 때: 경로 → 그림) -------------------------------------------------------
/** 'vmsk'/'vsms' 데이터 → [{ pts:[x,y,...] (문서 좌표, 베지어는 조절점 포함 [ax,ay,c1x,c1y,c2x,c2y]), op }] */
export function readVectorPaths(bytes, W, H) {
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let o = 8;   // 버전, 플래그
  const paths = [];
  let cur = null;
  const fx = (p, size) => (dv.getInt32(p) / 0x1000000) * size;
  while (o + 26 <= bytes.length) {
    const sel = dv.getUint16(o);
    if (sel === 0 || sel === 3) {
      const op = dv.getInt16(o + 4);
      cur = { knots: [], closed: sel === 0, op: op === -1 ? 1 : op };
      paths.push(cur);
    } else if ((sel === 1 || sel === 2 || sel === 4 || sel === 5) && cur) {
      const pt = (k) => [fx(o + 2 + k * 8 + 4, W), fx(o + 2 + k * 8, H)];
      cur.knots.push({ before: pt(0), anchor: pt(1), after: pt(2) });
    }
    o += 26;
  }
  return paths;
}

/** 읽은 경로를 Path2D 로 (합치기는 그대로, 빼기·교차는 evenodd 로 근사) */
export function pathsToPath2D(paths) {
  const p = new Path2D();
  for (const s of paths) {
    const k = s.knots;
    if (!k.length) continue;
    p.moveTo(...k[0].anchor);
    for (let i = 1; i <= k.length; i++) {
      if (i === k.length && !s.closed) break;
      const a = k[i - 1], b = k[i % k.length];
      p.bezierCurveTo(a.after[0], a.after[1], b.before[0], b.before[1], b.anchor[0], b.anchor[1]);
    }
    if (s.closed) p.closePath();
  }
  return p;
}
