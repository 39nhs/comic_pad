// 내용 인식 채우기 계산 (작업자): 여러 크기 PatchMatch + 투표 (Barnes 외 2009, Wexler 외 2007 방식)
// 입력 { w, h, data: RGBA, hole: Uint8(1 = 채울 곳) } → 출력 RGBA
const R = 3, PS = 2 * R + 1;

function downsample(img, hole, w, h) {
  const W = Math.max(1, w >> 1), H = Math.max(1, h >> 1);
  const o = new Float32Array(W * H * 4), oh = new Uint8Array(W * H);
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    let n = 0, any = 0; const acc = [0, 0, 0, 0];
    for (let dy = 0; dy < 2; dy++) for (let dx = 0; dx < 2; dx++) {
      const sx = Math.min(w - 1, x * 2 + dx), sy = Math.min(h - 1, y * 2 + dy), i = sy * w + sx;
      if (hole[i]) { any = 1; continue; }
      for (let c = 0; c < 4; c++) acc[c] += img[i * 4 + c];
      n++;
    }
    const j = y * W + x;
    oh[j] = any;
    if (n) for (let c = 0; c < 4; c++) o[j * 4 + c] = acc[c] / n;
  }
  return { img: o, hole: oh, w: W, h: H };
}

/** 구멍을 주변 색으로 차례차례 채우기 (가장 작은 크기에서 시작값) */
function diffuseFill(img, hole, w, h) {
  const known = Uint8Array.from(hole, v => (v ? 0 : 1));
  let left = known.reduce((a, v) => a + (1 - v), 0), guard = 0;
  while (left > 0 && guard++ < w + h) {
    const next = known.slice();
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
      const i = y * w + x;
      if (known[i]) continue;
      let n = 0; const acc = [0, 0, 0, 0];
      for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1], [1, 1], [-1, -1], [1, -1], [-1, 1]]) {
        const X = x + dx, Y = y + dy;
        if (X < 0 || Y < 0 || X >= w || Y >= h || !known[Y * w + X]) continue;
        const j = Y * w + X;
        for (let c = 0; c < 4; c++) acc[c] += img[j * 4 + c];
        n++;
      }
      if (n) { for (let c = 0; c < 4; c++) img[i * 4 + c] = acc[c] / n; next[i] = 1; left--; }
    }
    known.set(next);
  }
}

function dilate(mask, w, h, r) {
  const t = new Uint8Array(mask.length), o = new Uint8Array(mask.length);
  for (let y = 0; y < h; y++) { let last = -1e9; for (let x = 0; x < w; x++) { if (mask[y * w + x]) last = x; t[y * w + x] = x - last <= r ? 1 : 0; } last = 1e9; for (let x = w - 1; x >= 0; x--) { if (mask[y * w + x]) last = x; if (last - x <= r) t[y * w + x] = 1; } }
  for (let x = 0; x < w; x++) { let last = -1e9; for (let y = 0; y < h; y++) { if (t[y * w + x]) last = y; o[y * w + x] = y - last <= r ? 1 : 0; } last = 1e9; for (let y = h - 1; y >= 0; y--) { if (t[y * w + x]) last = y; if (last - y <= r) o[y * w + x] = 1; } }
  return o;
}

function solveLevel(L, nnfIn, iters, progress) {
  const { img, hole, w, h } = L;
  const near = dilate(hole, w, h, R);                       // 원본 조각이 구멍에 닿으면 안 됨
  const valid = [];
  for (let y = R; y < h - R; y++) for (let x = R; x < w - R; x++) if (!near[y * w + x]) valid.push(y * w + x);
  if (!valid.length) return null;
  const targets = [];
  for (let i = 0; i < w * h; i++) if (near[i]) targets.push(i);
  const isValid = (i) => { const x = i % w, y = (i / w) | 0; return x >= R && y >= R && x < w - R && y < h - R && !near[i]; };
  const nnf = new Int32Array(w * h).fill(-1), cost = new Float32Array(w * h).fill(Infinity);
  const dist = (t, s, best) => {
    const tx = t % w, ty = (t / w) | 0, sx = s % w, sy = (s / w) | 0;
    let d = 0;
    for (let dy = -R; dy <= R; dy++) {
      const TY = Math.min(h - 1, Math.max(0, ty + dy)), SY = sy + dy;
      for (let dx = -R; dx <= R; dx++) {
        const TX = Math.min(w - 1, Math.max(0, tx + dx));
        const a = (TY * w + TX) * 4, b = (SY * w + sx + dx) * 4;
        const e0 = img[a] - img[b], e1 = img[a + 1] - img[b + 1], e2 = img[a + 2] - img[b + 2], e3 = img[a + 3] - img[b + 3];
        d += e0 * e0 + e1 * e1 + e2 * e2 + e3 * e3;
      }
      if (d >= best) return d;
    }
    return d;
  };
  const tryS = (t, s) => { if (s < 0 || !isValid(s)) return; const d = dist(t, s, cost[t]); if (d < cost[t]) { cost[t] = d; nnf[t] = s; } };
  // 시작: 위 단계 결과를 늘려 쓰거나 아무 곳이나
  for (const t of targets) {
    let s = nnfIn ? nnfIn(t) : -1;
    if (s < 0 || !isValid(s)) s = valid[(Math.random() * valid.length) | 0];
    nnf[t] = s; cost[t] = dist(t, s, Infinity);
  }
  const vote = () => {
    const acc = new Float32Array(w * h * 4), wsum = new Float32Array(w * h);
    let mean = 0; for (const t of targets) mean += cost[t]; mean = mean / targets.length / (PS * PS) + 1;
    for (const t of targets) {
      const s = nnf[t], tx = t % w, ty = (t / w) | 0, sx = s % w, sy = (s / w) | 0;
      const wt = Math.exp(-cost[t] / (PS * PS) / (2 * mean));
      for (let dy = -R; dy <= R; dy++) {
        const Y = ty + dy; if (Y < 0 || Y >= h) continue;
        for (let dx = -R; dx <= R; dx++) {
          const X = tx + dx; if (X < 0 || X >= w) continue;
          const p = Y * w + X; if (!hole[p]) continue;
          const q = ((sy + dy) * w + sx + dx) * 4;
          acc[p * 4] += img[q] * wt; acc[p * 4 + 1] += img[q + 1] * wt; acc[p * 4 + 2] += img[q + 2] * wt; acc[p * 4 + 3] += img[q + 3] * wt;
          wsum[p] += wt;
        }
      }
    }
    for (let p = 0; p < w * h; p++) if (hole[p] && wsum[p] > 0) for (let c = 0; c < 4; c++) img[p * 4 + c] = acc[p * 4 + c] / wsum[p];
  };
  const maxR = Math.max(w, h);
  for (let it = 0; it < iters; it++) {
    for (let pass = 0; pass < 2; pass++) {
      const rev = (it + pass) % 2 === 1, order = rev ? targets.slice().reverse() : targets, dd = rev ? -1 : 1;
      for (const t of order) {
        const tx = t % w;
        // 이웃에서 전파
        if (tx - dd >= 0 && tx - dd < w) { const n = nnf[t - dd]; if (n >= 0) tryS(t, n + dd); }
        const up = t - dd * w;
        if (up >= 0 && up < w * h) { const n = nnf[up]; if (n >= 0) tryS(t, n + dd * w); }
        // 무작위 탐색 (범위를 반씩 줄이며)
        const s0 = nnf[t], sx = s0 % w, sy = (s0 / w) | 0;
        for (let rr = maxR; rr >= 1; rr >>= 1) {
          const X = sx + Math.round((Math.random() * 2 - 1) * rr), Y = sy + Math.round((Math.random() * 2 - 1) * rr);
          if (X >= R && Y >= R && X < w - R && Y < h - R) tryS(t, Y * w + X);
        }
      }
    }
    // 구멍 색이 바뀌었으니 비용 다시
    vote();
    for (const t of targets) cost[t] = dist(t, nnf[t], Infinity);
    progress?.((it + 1) / iters);
  }
  return nnf;
}

self.onmessage = ({ data: m }) => {
  try {
    const { w, h } = m;
    const img0 = Float32Array.from(m.data), hole0 = new Uint8Array(m.hole);
    // 여러 크기 단계 (가장 작은 단계에서 구멍이 몇 픽셀이 되도록)
    let hb = { x0: w, y0: h, x1: 0, y1: 0 };
    for (let i = 0; i < w * h; i++) if (hole0[i]) { const x = i % w, y = (i / w) | 0; hb = { x0: Math.min(hb.x0, x), y0: Math.min(hb.y0, y), x1: Math.max(hb.x1, x), y1: Math.max(hb.y1, y) }; }
    const holeSize = Math.max(1, hb.x1 - hb.x0 + 1, hb.y1 - hb.y0 + 1);
    const levels = [{ img: img0, hole: hole0, w, h }];
    while (levels.length < 8) {
      const L = levels[levels.length - 1];
      if (Math.min(L.w, L.h) < 24 || holeSize >> levels.length < 6) break;
      levels.push(downsample(L.img, L.hole, L.w, L.h));
    }
    const top = levels[levels.length - 1];
    diffuseFill(top.img, top.hole, top.w, top.h);
    let prev = null, prevW = 0, prevH = 0;
    const total = levels.length;
    for (let li = total - 1; li >= 0; li--) {
      const L = levels[li];
      if (li < total - 1) {
        // 위 단계 결과를 늘려 와서 이 단계 구멍을 먼저 칠해 두기
        const up0 = levels[li + 1], pw = up0.w, ph = up0.h;
        for (let i = 0; i < L.w * L.h; i++) {
          if (!L.hole[i]) continue;
          const x = i % L.w, y = (i / L.w) | 0, X = Math.min(pw - 1, x >> 1), Y = Math.min(ph - 1, y >> 1);
          const up = up0.img, j = (Y * pw + X) * 4;
          for (let c = 0; c < 4; c++) L.img[i * 4 + c] = up[j + c];
        }
      }
      const upNnf = prev ? (t) => {
        const x = t % L.w, y = (t / L.w) | 0, X = Math.min(prevW - 1, x >> 1), Y = Math.min(prevH - 1, y >> 1), s = prev[Y * prevW + X];
        if (s < 0) return -1;
        const sx = (s % prevW) * 2 + (x & 1), sy = ((s / prevW) | 0) * 2 + (y & 1);
        return sx < L.w && sy < L.h ? sy * L.w + sx : -1;
      } : null;
      const iters = li === 0 ? 2 : li === total - 1 ? 6 : 4;
      const nnf = solveLevel(L, upNnf, iters, (f) => self.postMessage({ progress: ((total - 1 - li) + f) / total }));
      if (!nnf) { if (li === 0) diffuseFill(L.img, L.hole, L.w, L.h); prev = null; continue; }
      prev = nnf; prevW = L.w; prevH = L.h;
    }
    const out = new Uint8ClampedArray(w * h * 4);
    for (let i = 0; i < out.length; i++) out[i] = levels[0].img[i];
    self.postMessage({ done: true, data: out }, [out.buffer]);
  } catch (e) { self.postMessage({ error: String(e?.message || e) }); }
};
