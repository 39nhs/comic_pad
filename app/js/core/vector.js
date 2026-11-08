// 벡터 레이어: 펜으로 그린 선을 점 목록으로 기억해 두고 언제든 다시 그린다.
// → 나중에 선 굵기 바꾸기, 선 단위로 지우기(닿은 부분만/선 전체), 변형해도 선이 깨지지 않음.
import { app } from '../app.js';
import { Stroke, commitBuffer, makePreset } from './brush.js';
import { createCanvas } from './util.js';

let vbuf = null;
const buffer = (doc) => {
  if (!vbuf || vbuf.width !== doc.width || vbuf.height !== doc.height) vbuf = createCanvas(doc.width, doc.height);
  return vbuf;
};

/** 브러시 설정 중 다시 그릴 때 필요한 것만 */
export function presetSnapshot(p) {
  const keys = ['size', 'opacity', 'flow', 'hardness', 'spacing', 'pressureSize', 'minSize', 'pressureOpacity', 'minOpacity', 'grain', 'buildup', 'tip', 'taperIn', 'taperOut', 'blend', 'speedThin', 'density', 'particle', 'flatRatio', 'angle', 'texture', 'textureStrength', 'textureScale'];
  const o = {};
  for (const k of keys) if (p[k] !== undefined) o[k] = p[k];
  if (o.tip === 'image') o.tip = 'round';
  return o;
}

export function drawVectorStroke(layer, s) {
  const doc = layer.doc;
  const buf = buffer(doc);
  const st = new Stroke({ canvas: buf, preset: makePreset({ ...s.preset, stabilizer: 0 }), color: s.color, doc, noClip: true });
  const pts = [];
  for (let i = 0; i < s.pts.length; i += 4) pts.push({ x: s.pts[i], y: s.pts[i + 1], p: s.pts[i + 2], t: s.pts[i + 3] });
  const r = st.replay(pts);
  if (r) commitBuffer(layer, buf, r, 'paint', s.preset.opacity ?? 1, s.preset.blend || 'source-over', s.preset);
}

/** 레이어 전체를 선 목록으로부터 다시 그림 */
export function renderVector(layer) {
  layer.ctx.clearRect(0, 0, layer.canvas.width, layer.canvas.height);
  for (const s of layer.meta?.strokes || []) drawVectorStroke(layer, s);
  layer.touch();
}

/** 선 목록 바꾸기 (실행취소 가능) */
export function setStrokes(layer, strokes, label = '벡터 편집', before = null) {
  const prev = before || structuredClone(layer.meta?.strokes || []);
  const next = structuredClone(strokes);
  const apply = (arr) => { layer.meta = { ...(layer.meta || {}), strokes: structuredClone(arr) }; renderVector(layer); };
  apply(next);
  app.history.push({ label, bytes: JSON.stringify(next).length + JSON.stringify(prev).length, undo: () => apply(prev), redo: () => apply(next) });
}

/** 새 선 추가 (이미 화면에 그려져 있으므로 다시 그리지 않고 기록만) */
export function addStroke(layer, stroke) {
  layer.meta = { ...(layer.meta || {}), strokes: [...(layer.meta?.strokes || []), stroke] };
  app.history.push({
    label: '벡터 선',
    bytes: stroke.pts.length * 8,
    undo: () => { layer.meta.strokes = layer.meta.strokes.filter(s => s !== stroke); renderVector(layer); },
    redo: () => { layer.meta.strokes = [...layer.meta.strokes, stroke]; renderVector(layer); },
  });
}

/**
 * 벡터 지우개: 원(x, y, r) 안의 점을 지우고 선을 나눈다. whole=true 면 닿은 선 전체를 지움.
 * 바뀐 게 있으면 true
 */
export function eraseAt(layer, x, y, r, whole) {
  const strokes = layer.meta?.strokes || [];
  const out = [];
  let changed = false;
  for (const s of strokes) {
    const p = s.pts;
    const hit = [];
    let any = false;
    for (let i = 0; i < p.length; i += 4) {
      const pr = (s.preset.size || 4) * 0.5 * (s.preset.pressureSize ? Math.max(0.3, p[i + 2]) : 1);
      const dx = p[i] - x, dy = p[i + 1] - y;
      const h = dx * dx + dy * dy <= (r + pr) * (r + pr);
      hit.push(h);
      if (h) any = true;
    }
    if (!any) { out.push(s); continue; }
    changed = true;
    if (whole) continue;
    let run = [];
    const flush = () => { if (run.length >= 8) out.push({ ...s, pts: run }); run = []; };
    for (let k = 0; k < hit.length; k++) {
      if (hit[k]) flush();
      else run.push(p[k * 4], p[k * 4 + 1], p[k * 4 + 2], p[k * 4 + 3]);
    }
    flush();
  }
  if (changed) layer.meta = { ...layer.meta, strokes: out };
  return changed;
}

/** 모든 선 굵기에 배율 곱하기 */
export function scaleWidths(layer, k) {
  const next = (layer.meta?.strokes || []).map(s => ({ ...s, preset: { ...s.preset, size: Math.max(0.3, s.preset.size * k) } }));
  setStrokes(layer, next, '선 굵기 일괄 조정');
}

/** 선 색 바꾸기 (전부) */
export function recolor(layer, color) {
  setStrokes(layer, (layer.meta?.strokes || []).map(s => ({ ...s, color })), '선 색 바꾸기');
}

/** 점 변환 (변형 도구용): fn({x,y}) -> {x,y}, sizeK 굵기 배율 */
export function transformStrokes(strokes, fn, sizeK = 1) {
  return strokes.map(s => {
    const p = s.pts.slice();
    for (let i = 0; i < p.length; i += 4) { const q = fn({ x: p[i], y: p[i + 1] }); p[i] = q.x; p[i + 1] = q.y; }
    return { ...s, pts: p, preset: { ...s.preset, size: s.preset.size * sizeK } };
  });
}

// ---------------------------------------------------------------------
// 교차점까지 지우기 · 선 고르기 · 다듬기
// ---------------------------------------------------------------------
function bounds(s) {
  if (s._b) return s._b;
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  const p = s.pts;
  for (let i = 0; i < p.length; i += 4) { if (p[i] < x0) x0 = p[i]; if (p[i] > x1) x1 = p[i]; if (p[i + 1] < y0) y0 = p[i + 1]; if (p[i + 1] > y1) y1 = p[i + 1]; }
  const pad = (s.preset?.size || 4) / 2;
  Object.defineProperty(s, '_b', { value: { x0: x0 - pad, y0: y0 - pad, x1: x1 + pad, y1: y1 + pad }, enumerable: false, configurable: true });
  return s._b;
}
const overlap = (a, b) => a.x0 <= b.x1 && b.x0 <= a.x1 && a.y0 <= b.y1 && b.y0 <= a.y1;

/** (x, y) 에서 tol 안에 있는 선: { index, seg(점 번호), d } — 위에 그린 선부터 */
export function strokeAt(layer, x, y, tol) {
  const list = layer.meta?.strokes || [];
  let best = null;
  for (let k = list.length - 1; k >= 0; k--) {
    const s = list[k], b = bounds(s), r = tol + (s.preset?.size || 4) / 2;
    if (x < b.x0 - tol || x > b.x1 + tol || y < b.y0 - tol || y > b.y1 + tol) continue;
    const p = s.pts;
    for (let i = 0; i + 4 < p.length; i += 4) {
      const ax = p[i], ay = p[i + 1], bx = p[i + 4], by = p[i + 5];
      const dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
      const t = L ? Math.max(0, Math.min(1, ((x - ax) * dx + (y - ay) * dy) / L)) : 0;
      const d = Math.hypot(ax + dx * t - x, ay + dy * t - y);
      if (d <= r && (!best || d < best.d)) best = { index: k, seg: i / 4 + t, d };
    }
    if (best) return best;
  }
  return best;
}

/** 두 선분의 교차: a 쪽 매개변수 t (0~1) 또는 null */
function segHit(ax, ay, bx, by, cx, cy, dx, dy) {
  const rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
  const den = rx * sy - ry * sx;
  if (Math.abs(den) < 1e-9) return null;
  const t = ((cx - ax) * sy - (cy - ay) * sx) / den, u = ((cx - ax) * ry - (cy - ay) * rx) / den;
  return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? t : null;
}

/** 선 s 위의 위치(점 번호 + 비율)에 점 하나 만들기 */
function pointAt(p, pos) {
  const i = Math.min(Math.floor(pos), p.length / 4 - 2), t = pos - i, a = i * 4, b = a + 4;
  return [p[a] + (p[b] - p[a]) * t, p[a + 1] + (p[b + 1] - p[a + 1]) * t, p[a + 2] + (p[b + 2] - p[a + 2]) * t, p[a + 3] + (p[b + 3] - p[a + 3]) * t];
}

/** 선의 일부 [from, to] (점 번호 위치) 를 새 점 목록으로 */
function slice(p, from, to) {
  const out = [...pointAt(p, from)];
  for (let i = Math.ceil(from + 1e-6); i < to; i++) out.push(p[i * 4], p[i * 4 + 1], p[i * 4 + 2], p[i * 4 + 3]);
  out.push(...pointAt(p, to));
  return out;
}

/**
 * 교차점까지 지우기 (클립스튜디오의 "교점까지"): 닿은 선에서 양옆으로 가장 가까운 다른 선과의 교차점 사이만 지운다.
 * 삐져나온 선 끝 정리용. 바뀌면 true
 */
export function eraseToCross(layer, x, y, r) {
  const list = layer.meta?.strokes || [];
  const hit = strokeAt(layer, x, y, r);
  if (!hit) return false;
  const s = list[hit.index], p = s.pts, n = p.length / 4;
  if (n < 2) return false;
  const cuts = [];
  const sb = bounds(s);
  list.forEach((o, k) => {
    if (o === s && n < 3) return;
    if (!overlap(sb, bounds(o))) return;
    const q = o.pts;
    for (let i = 0; i + 4 < p.length; i += 4) {
      for (let j = 0; j + 4 < q.length; j += 4) {
        // 자기 자신과는 붙어 있는 선분끼리 빼고
        if (o === s && Math.abs(i - j) <= 4) continue;
        const t = segHit(p[i], p[i + 1], p[i + 4], p[i + 5], q[j], q[j + 1], q[j + 4], q[j + 5]);
        if (t != null) cuts.push(i / 4 + t);
      }
    }
  });
  const eps = 0.02;
  const left = Math.max(-1, ...cuts.filter(c => c < hit.seg - eps));
  const right = Math.min(Infinity, ...cuts.filter(c => c > hit.seg + eps));
  const pieces = [];
  if (left > 0) pieces.push({ ...s, pts: slice(p, 0, left) });
  if (right < n - 1) pieces.push({ ...s, pts: slice(p, right, n - 1) });
  const next = [...list];
  next.splice(hit.index, 1, ...pieces.filter(x => x.pts.length >= 8));
  layer.meta = { ...layer.meta, strokes: next };
  return true;
}

/** 선 다듬기: 양 끝은 그대로 두고 가운데 점들을 이웃과 평균 (k 번 반복) */
export function smoothPts(p, k = 2) {
  let a = p.slice();
  for (let r = 0; r < k; r++) {
    const b = a.slice();
    for (let i = 4; i + 4 < a.length; i += 4) {
      b[i] = (a[i - 4] + a[i] * 2 + a[i + 4]) / 4;
      b[i + 1] = (a[i - 3] + a[i + 1] * 2 + a[i + 5]) / 4;
      b[i + 2] = (a[i - 2] + a[i + 2] * 2 + a[i + 6]) / 4;
    }
    a = b;
  }
  return a;
}

/** 고른 선들만 바꾸기 (실행취소 가능): fn(stroke) -> stroke | null(삭제) */
export function editStrokes(layer, indices, fn, label) {
  const set = new Set(indices);
  const next = [];
  (layer.meta?.strokes || []).forEach((s, i) => {
    if (!set.has(i)) { next.push(s); return; }
    const r = fn(structuredClone(s));
    if (r) next.push(r);
  });
  setStrokes(layer, next, label);
}
