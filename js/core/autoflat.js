// 밑색 한 번에 칠하기: 선화로 닫힌 영역마다 색을 채운 "밑색" 레이어를 만든다 (클립스튜디오의 밑칠과 비슷)
// 계산은 워커에서 (큰 원고에서도 화면이 멈추지 않게)
import { dilate } from './raster.js';

/**
 * rgba: Uint8ClampedArray, w, h
 * o: { alpha: 선으로 볼 불투명도(0~255), gap: 틈 닫기(px), minArea: 이보다 작은 영역은 이웃 색으로, outside: 바깥(테두리에 닿는 영역) 제외, under: 선 아래로 번지게(px) }
 * → Int32Array 라벨 (0 = 비움, 1.. = 영역 번호)
 */
export function flatLabels(rgba, w, hgt, o = {}) {
  const n = w * hgt;
  const thr = o.alpha ?? 80, gap = Math.round(o.gap ?? 2), minArea = o.minArea ?? 30, under = Math.round(o.under ?? 3);
  const line = new Uint8Array(n);
  for (let i = 0, p = 3; i < n; i++, p += 4) line[i] = rgba[p] > thr ? 1 : 0;
  // gap = 이 너비(px) 이하의 빈틈은 선이 이어진 것으로 → 선을 gap/2 만큼 두껍게
  const rad = Math.ceil(gap / 2);
  const wall = rad > 0 ? dilate(line, w, hgt, rad) : line;
  const lab = new Int32Array(n);
  const stack = new Int32Array(n);
  const sizes = [0];
  const touches = [false];
  let next = 1;
  for (let s = 0; s < n; s++) {
    if (wall[s] || lab[s]) continue;
    const id = next++;
    let sp = 0, size = 0, edge = false;
    stack[sp++] = s; lab[s] = id;
    while (sp) {
      const i = stack[--sp];
      size++;
      const x = i % w, y = (i - x) / w;
      if (x === 0 || y === 0 || x === w - 1 || y === hgt - 1) edge = true;
      if (x > 0 && !wall[i - 1] && !lab[i - 1]) { lab[i - 1] = id; stack[sp++] = i - 1; }
      if (x < w - 1 && !wall[i + 1] && !lab[i + 1]) { lab[i + 1] = id; stack[sp++] = i + 1; }
      if (y > 0 && !wall[i - w] && !lab[i - w]) { lab[i - w] = id; stack[sp++] = i - w; }
      if (y < hgt - 1 && !wall[i + w] && !lab[i + w]) { lab[i + w] = id; stack[sp++] = i + w; }
    }
    sizes.push(size);
    touches.push(edge);
  }
  // 바깥 영역은 -1 (번지지도 않음), 너무 작은 영역은 0 (이웃 색이 번져 들어감)
  const map = new Int32Array(next);
  let k = 0;
  const edgeOf = [false];
  for (let id = 1; id < next; id++) { map[id] = o.outside !== false && touches[id] ? -1 : sizes[id] < minArea ? 0 : ++k; if (map[id] > 0) edgeOf[map[id]] = touches[id]; }
  for (let i = 0; i < n; i++) if (lab[i]) lab[i] = map[lab[i]];
  // 틈 닫기로 깎인 부분 + 선 아래까지 이웃 영역 번호로 채우기 (한 칸씩 넓히기)
  const steps = rad + under + 1;
  let front = [];
  // 바깥(-1) 영역도 함께 자라야 안쪽 색이 틈을 지나 바깥으로 새지 않음
  for (let i = 0; i < n; i++) if (lab[i] !== 0) front.push(i);
  for (let st = 0; st < steps && front.length; st++) {
    const nf = [];
    for (const i of front) {
      const id = lab[i], x = i % w;
      const nb = [x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, i - w, i + w];
      for (const j of nb) {
        if (j < 0 || j >= n || lab[j] !== 0) continue;
        // 선이 아닌 빈칸이거나(틈 닫기로 깎인 곳), 선 아래로 under 만큼
        lab[j] = id; nf.push(j);
      }
    }
    front = nf;
  }
  for (let i = 0; i < n; i++) if (lab[i] < 0) lab[i] = 0;
  return { labels: lab, count: k, edge: Uint8Array.from(edgeOf, Boolean) };
}

/** 영역 번호 → 색 (색 모드: 겹치지 않게 골고루 / 한 색 모드) */
export function colorize(labels, count, w, hgt, mode = 'colors', one = '#d9d9d9') {
  const img = new ImageData(w, hgt);
  const cols = new Uint8Array((count + 1) * 3);
  const hex = (s) => [1, 3, 5].map(i => parseInt(s.slice(i, i + 2), 16));
  for (let k = 1; k <= count; k++) {
    let rgb;
    if (mode === 'one') rgb = hex(one);
    else {
      // 황금각으로 색상을 돌려 이웃끼리 덜 비슷하게, 연한 파스텔
      const hh = (k * 137.508) % 360, s = 0.45 + (k % 3) * 0.12, l = 0.72 + (k % 2) * 0.08;
      const c = (1 - Math.abs(2 * l - 1)) * s, x = c * (1 - Math.abs(((hh / 60) % 2) - 1)), m = l - c / 2;
      const [r, g, b] = hh < 60 ? [c, x, 0] : hh < 120 ? [x, c, 0] : hh < 180 ? [0, c, x] : hh < 240 ? [0, x, c] : hh < 300 ? [x, 0, c] : [c, 0, x];
      rgb = [(r + m) * 255, (g + m) * 255, (b + m) * 255].map(Math.round);
    }
    cols.set(rgb, k * 3);
  }
  const d = img.data;
  for (let i = 0, p = 0; i < labels.length; i++, p += 4) {
    const k = labels[i];
    if (!k) continue;
    d[p] = cols[k * 3]; d[p + 1] = cols[k * 3 + 1]; d[p + 2] = cols[k * 3 + 2]; d[p + 3] = 255;
  }
  return img;
}

/**
 * 색 점(힌트)으로 영역 칠하기 (크리타의 색칠 마스크와 비슷): 영역 안에 찍힌 색 중 가장 많은 색으로.
 * 힌트가 없는 영역은 spread 면 가장 길게 맞닿은 이웃 영역 색을 따라감 (그림 테두리에 닿는 바깥 영역은 따라가지 않음)
 * labels/count/edge: flatLabels 결과, hint: 힌트 레이어 RGBA
 */
export function colorizeHints(labels, count, edge, hint, w, hgt, { spread = true } = {}) {
  const votes = new Array(count + 1);
  for (let i = 0, p = 3; i < labels.length; i++, p += 4) {
    const k = labels[i];
    if (!k || hint[p] < 128) continue;
    const key = (hint[p - 3] << 16) | (hint[p - 2] << 8) | hint[p - 1];
    const m = votes[k] ||= new Map();
    m.set(key, (m.get(key) || 0) + 1);
  }
  const color = new Int32Array(count + 1).fill(-1);
  for (let k = 1; k <= count; k++) {
    const m = votes[k];
    if (!m) continue;
    let best = -1, bc = 0;
    for (const [key, c] of m) if (c > bc) { bc = c; best = key; }
    color[k] = best;
  }
  const hinted = color.slice();
  if (spread) {
    // 이웃 관계: 선을 사이에 둔 영역끼리 (몇 px 건너서) 맞닿은 길이
    const adj = new Map();
    const add = (a, b) => { if (a === b || !a || !b) return; const key = a < b ? a * 1e6 + b : b * 1e6 + a; adj.set(key, (adj.get(key) || 0) + 1); };
    const R = 4;
    for (let y = 0; y < hgt; y += 2) for (let x = 0; x < w; x += 2) {
      const a = labels[y * w + x];
      if (!a) continue;
      if (x + R < w) add(a, labels[y * w + x + R]);
      if (y + R < hgt) add(a, labels[(y + R) * w + x]);
    }
    const nb = Array.from({ length: count + 1 }, () => []);
    for (const [key, c] of adj) { const a = Math.floor(key / 1e6), b = key % 1e6; nb[a].push([b, c]); nb[b].push([a, c]); }
    let changed = true;
    for (let round = 0; changed && round < 50; round++) {
      changed = false;
      for (let k = 1; k <= count; k++) {
        if (color[k] >= 0 || edge?.[k]) continue;
        let best = -1, bc = 0;
        for (const [j, c] of nb[k]) if (color[j] >= 0 && c > bc) { bc = c; best = color[j]; }
        if (best >= 0) { color[k] = best; changed = true; }
      }
    }
  }
  const img = new ImageData(w, hgt), d = img.data;
  for (let i = 0, p = 0; i < labels.length; i++, p += 4) {
    const c = color[labels[i]];
    if (!labels[i] || c < 0) continue;
    d[p] = (c >> 16) & 255; d[p + 1] = (c >> 8) & 255; d[p + 2] = c & 255; d[p + 3] = 255;
  }
  const n = hinted.filter(v => v >= 0).length - (hinted[0] >= 0 ? 1 : 0);
  return { img, hinted: n, filled: color.filter((v, i) => i && v >= 0).length };
}
