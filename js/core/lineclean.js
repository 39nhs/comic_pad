// 선 정리 (AI 없이 계산으로): 잡티 지우기 · 끊긴 선 잇기 · 울퉁불퉁한 가장자리 다듬기 · 굵기 조절 · 진하게
import { dilate } from './raster.js';

/** 알파(0~255) 배열에 대해: 1 이면 잉크 */
const inkMask = (a, thr) => { const m = new Uint8Array(a.length); for (let i = 0; i < a.length; i++) m[i] = a[i] >= thr ? 1 : 0; return m; };
const erode = (m, w, hgt, r) => { const inv = new Uint8Array(m.length); for (let i = 0; i < m.length; i++) inv[i] = m[i] ? 0 : 1; const d = dilate(inv, w, hgt, r); const o = new Uint8Array(m.length); for (let i = 0; i < m.length; i++) o[i] = d[i] ? 0 : 1; return o; };

/** 잉크 덩어리 중 area 보다 작은 것을 지움. 지운 픽셀 수 반환 */
function removeSpecks(alpha, w, hgt, area, thr) {
  const ink = inkMask(alpha, thr), seen = new Uint8Array(ink.length), stack = new Int32Array(ink.length);
  let removed = 0;
  const comp = [];
  for (let s = 0; s < ink.length; s++) {
    if (!ink[s] || seen[s]) continue;
    let sp = 0; comp.length = 0;
    stack[sp++] = s; seen[s] = 1;
    while (sp) {
      const i = stack[--sp]; comp.push(i);
      const x = i % w;
      // 8방향: 대각선으로 이어진 선도 한 덩어리
      for (const j of [i - w, i + w, x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, x > 0 ? i - w - 1 : -1, x < w - 1 ? i - w + 1 : -1, x > 0 ? i + w - 1 : -1, x < w - 1 ? i + w + 1 : -1]) {
        if (j < 0 || j >= ink.length || seen[j] || !ink[j]) continue;
        seen[j] = 1; stack[sp++] = j;
      }
    }
    if (comp.length < area) {
      // 덩어리 둘레의 옅은 테두리까지 함께 지우기
      for (const i of comp) {
        alpha[i] = 0; removed++;
        const x = i % w;
        for (const j of [i - 1, i + 1, i - w, i + w]) if (j >= 0 && j < alpha.length && Math.abs((j % w) - x) <= 1 && alpha[j] < thr) alpha[j] = 0;
      }
    }
  }
  return removed;
}

/** 가장자리 다듬기: 알파를 살짝 흐린 뒤 대비를 다시 올림 → 계단·울퉁불퉁이 줄어듦 */
function smoothEdges(alpha, w, hgt, r) {
  const tmp = new Float32Array(alpha.length), out = new Float32Array(alpha.length);
  for (let y = 0; y < hgt; y++) {
    let acc = 0; const o = y * w;
    for (let x = -r; x < w + r; x++) {
      const add = x + r < w ? alpha[o + Math.min(w - 1, x + r)] : 0, sub = x - r - 1 >= 0 ? alpha[o + x - r - 1] : 0;
      acc += add - sub;
      if (x >= 0 && x < w) tmp[o + x] = acc / (2 * r + 1);
    }
  }
  for (let x = 0; x < w; x++) {
    let acc = 0;
    for (let y = -r; y < hgt + r; y++) {
      const add = y + r < hgt ? tmp[Math.min(hgt - 1, y + r) * w + x] : 0, sub = y - r - 1 >= 0 ? tmp[(y - r - 1) * w + x] : 0;
      acc += add - sub;
      if (y >= 0 && y < hgt) out[y * w + x] = acc / (2 * r + 1);
    }
  }
  // 50% 근처를 가파르게 → 깔끔한 가장자리, 굵기는 거의 그대로
  for (let i = 0; i < alpha.length; i++) { const v = (out[i] - 128) * 2.6 + 128; alpha[i] = v < 0 ? 0 : v > 255 ? 255 : v; }
}

/**
 * ImageData 를 제자리에서 정리. o: { specks(px), close(px), smooth(px), weight(-2~2 px), darken(bool), color }
 * 결과: { removed }
 */
export function cleanLines(img, o) {
  const { width: w, height: hgt, data: d } = img;
  const n = w * hgt, alpha = new Uint8ClampedArray(n);
  for (let i = 0; i < n; i++) {
    // 흰 바탕 스캔(불투명)이면 어두운 정도를 잉크로 본다
    const a = d[i * 4 + 3], lum = (d[i * 4] * 0.3 + d[i * 4 + 1] * 0.59 + d[i * 4 + 2] * 0.11);
    alpha[i] = o.fromScan ? 255 - lum : a;
  }
  let removed = 0;
  if (o.specks > 0) removed = removeSpecks(alpha, w, hgt, o.specks, 96);
  if (o.close > 0) {
    // 끊긴 곳 잇기: 넓혔다가 다시 깎기 (모양은 유지, 틈만 메움)
    const m = inkMask(alpha, 96), closed = erode(dilate(m, w, hgt, o.close), w, hgt, o.close);
    for (let i = 0; i < n; i++) if (closed[i] && !m[i]) alpha[i] = 255;
  }
  if (o.weight) {
    const m = inkMask(alpha, 96), r = Math.abs(Math.round(o.weight));
    const res = o.weight > 0 ? dilate(m, w, hgt, r) : erode(m, w, hgt, r);
    for (let i = 0; i < n; i++) alpha[i] = res[i] ? Math.max(alpha[i], o.weight > 0 ? 255 : alpha[i]) : (o.weight < 0 ? 0 : alpha[i]);
  }
  if (o.smooth > 0) smoothEdges(alpha, w, hgt, Math.round(o.smooth));
  const col = o.darken ? hexRgb(o.color || '#000000') : null;
  for (let i = 0; i < n; i++) {
    const a = alpha[i], p = i * 4;
    if (o.fromScan || col) {
      const c = col || [d[p], d[p + 1], d[p + 2]];
      if (o.fromScan && !col) { d[p] = d[p + 1] = d[p + 2] = 0; } else { d[p] = c[0]; d[p + 1] = c[1]; d[p + 2] = c[2]; }
    }
    d[p + 3] = a;
  }
  return { removed };
}
const hexRgb = (h) => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
