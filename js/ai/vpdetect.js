// 사진·배경 그림에서 소실점 찾기 → 퍼스 자 (AI 모델 없이 계산만: 선분 검출 + RANSAC)
import { app } from '../app.js';
import { createCanvas } from '../core/util.js';

/** 그림에서 직선 조각 찾기 (기울기 방향이 비슷한 가장자리 점을 이어 붙여 직선으로) */
export function detectSegments(img) {
  const { width: W, height: H, data: d } = img;
  const g = new Float32Array(W * H);
  for (let i = 0; i < W * H; i++) g[i] = d[i * 4] * 0.299 + d[i * 4 + 1] * 0.587 + d[i * 4 + 2] * 0.114;
  // 살짝 흐리게 (잡음 줄이기)
  const b = new Float32Array(W * H);
  for (let y = 1; y < H - 1; y++) for (let x = 1; x < W - 1; x++) {
    const i = y * W + x;
    b[i] = (g[i] * 4 + g[i - 1] * 2 + g[i + 1] * 2 + g[i - W] * 2 + g[i + W] * 2 + g[i - W - 1] + g[i - W + 1] + g[i + W - 1] + g[i + W + 1]) / 16;
  }
  const mag = new Float32Array(W * H), ang = new Float32Array(W * H);
  let sum = 0, cnt = 0;
  // 그림 가장자리 2px 은 빼기 (흐림 계산이 안 된 테두리가 선으로 잡히지 않게)
  for (let y = 2; y < H - 2; y++) for (let x = 2; x < W - 2; x++) {
    const i = y * W + x;
    const gx = b[i - W + 1] + 2 * b[i + 1] + b[i + W + 1] - b[i - W - 1] - 2 * b[i - 1] - b[i + W - 1];
    const gy = b[i + W - 1] + 2 * b[i + W] + b[i + W + 1] - b[i - W - 1] - 2 * b[i - W] - b[i - W + 1];
    mag[i] = Math.hypot(gx, gy); ang[i] = Math.atan2(gy, gx);
    sum += mag[i]; cnt++;
  }
  const thr = Math.max(40, (sum / cnt) * 2.2);
  const used = new Uint8Array(W * H), segs = [];
  const tol = Math.PI / 10;
  const same = (a, c) => { let dd = Math.abs(a - c) % Math.PI; if (dd > Math.PI / 2) dd = Math.PI - dd; return dd < tol; };
  const stack = [];
  for (let s = 0; s < W * H; s++) {
    if (used[s] || mag[s] < thr) continue;
    // 같은 방향의 가장자리 점 묶기
    const a0 = ang[s], pts = [];
    stack.length = 0; stack.push(s); used[s] = 1;
    while (stack.length && pts.length < 4000) {
      const i = stack.pop(); pts.push(i);
      const x = i % W, y = (i / W) | 0;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        const xx = x + dx, yy = y + dy;
        if (xx < 1 || yy < 1 || xx >= W - 1 || yy >= H - 1) continue;
        const j = yy * W + xx;
        if (!used[j] && mag[j] >= thr * 0.6 && same(ang[j], a0)) { used[j] = 1; stack.push(j); }
      }
    }
    if (pts.length < 18) continue;
    // 주성분으로 직선 맞추기
    let mx = 0, my = 0;
    for (const i of pts) { mx += i % W; my += (i / W) | 0; }
    mx /= pts.length; my /= pts.length;
    let sxx = 0, syy = 0, sxy = 0;
    for (const i of pts) { const dx = (i % W) - mx, dy = ((i / W) | 0) - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
    const tr = sxx + syy, det = sxx * syy - sxy * sxy, l1 = tr / 2 + Math.sqrt(Math.max(0, tr * tr / 4 - det)), l2 = tr - l1;
    if (l2 / Math.max(1e-6, l1) > 0.02) continue;                      // 곧지 않음
    const t = Math.atan2(l1 - sxx, sxy || 1e-9), ux = Math.cos(t), uy = Math.sin(t);
    let lo = Infinity, hi = -Infinity;
    for (const i of pts) { const p = ((i % W) - mx) * ux + (((i / W) | 0) - my) * uy; if (p < lo) lo = p; if (p > hi) hi = p; }
    const len = hi - lo;
    if (len < Math.max(20, Math.min(W, H) * 0.04)) continue;
    segs.push({ x1: mx + ux * lo, y1: my + uy * lo, x2: mx + ux * hi, y2: my + uy * hi, len, ux, uy, mx, my });
  }
  return segs;
}

/** 선분 → 소실점들 (RANSAC). 결과: [{ x, y, support(길이 합), segs }] — 거의 평행(무한대)한 무리는 빼고 */
export function findVanishingPoints(segs, W, H, maxVP = 3) {
  const out = [];
  let rest = segs.slice();
  const angTol = 2.2 * Math.PI / 180;
  const far = Math.max(W, H) * 25;
  const line = (s) => [s.y1 - s.y2, s.x2 - s.x1, s.x1 * s.y2 - s.x2 * s.y1];   // 동차 좌표 직선
  const supports = (s, vx, vy) => {
    // 선분 가운데에서 소실점 쪽 방향과 선분 방향의 각도
    const dx = vx - s.mx, dy = vy - s.my, l = Math.hypot(dx, dy) || 1;
    const c = Math.abs((dx * s.ux + dy * s.uy) / l);
    if (Math.acos(Math.min(1, c)) >= angTol) return false;
    // 선 끝이 그 점에 닿아 있으면 소실점이 아니라 모서리(선이 만나는 곳)
    const e = Math.min(Math.hypot(vx - s.x1, vy - s.y1), Math.hypot(vx - s.x2, vy - s.y2));
    return e > Math.max(12, s.len * 0.25);
  };
  let seed = 12345;
  const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
  for (let k = 0; k < maxVP && rest.length >= 3; k++) {
    const total = rest.reduce((a, s) => a + s.len, 0);
    let best = null;
    const tries = Math.min(1500, rest.length * rest.length);
    for (let t = 0; t < tries; t++) {
      const a = rest[(rnd() * rest.length) | 0], c = rest[(rnd() * rest.length) | 0];
      if (a === c) continue;
      const L1 = line(a), L2 = line(c);
      const p = [L1[1] * L2[2] - L1[2] * L2[1], L1[2] * L2[0] - L1[0] * L2[2], L1[0] * L2[1] - L1[1] * L2[0]];
      if (Math.abs(p[2]) < 1e-9) continue;
      const vx = p[0] / p[2], vy = p[1] / p[2];
      if (Math.hypot(vx - W / 2, vy - H / 2) > far) continue;          // 사실상 평행
      // 선분 위(그림 안 가운데)에 걸린 교점은 소실점이 아님
      if (vx > 0 && vx < W && vy > 0 && vy < H && Math.hypot(vx - a.mx, vy - a.my) < a.len && Math.hypot(vx - c.mx, vy - c.my) < c.len) continue;
      let sc = 0;
      for (const s of rest) if (supports(s, vx, vy)) sc += s.len;
      if (!best || sc > best.sc) best = { sc, vx, vy };
    }
    if (!best || best.sc < total * 0.12 || best.sc < Math.min(W, H) * 0.6) break;
    if (out.length && best.sc < out[0].support * 0.3) break;             // 첫 소실점보다 훨씬 약하면 우연
    // 받쳐 주는 선분들로 다시 맞추기 (최소제곱)
    const inl = rest.filter(s => supports(s, best.vx, best.vy));
    // 서로 다른 선 3개 이상이 모여야 소실점 (한 직선이 끊겨 여러 조각인 것은 하나로 침)
    const dirs = [...new Set(inl.map(q => Math.round(Math.atan2(q.my - best.vy, q.mx - best.vx) * 180 / Math.PI)))];
    // 그림 안쪽 점은 우연히 몇 선이 만나기 쉬우니 더 많은 선이 모여야 인정 (1점 투시는 선이 많이 모임)
    const inside = best.vx > 0 && best.vx < W && best.vy > 0 && best.vy < H;
    if (inl.length < 4 || dirs.length < (inside ? 5 : 3)) { rest = rest.filter(q => !inl.includes(q)); k--; if (rest.length < 3) break; continue; }
    let A = 0, B = 0, C = 0, D = 0, E = 0;
    for (const s of inl) {
      const [a, b, c] = line(s), n = Math.hypot(a, b) || 1, w = s.len;
      const na = a / n, nb = b / n, nc = c / n;
      A += w * na * na; B += w * na * nb; C += w * nb * nb; D += -w * na * nc; E += -w * nb * nc;
    }
    const det = A * C - B * B;
    let vx = best.vx, vy = best.vy;
    if (Math.abs(det) > 1e-9) { vx = (D * C - B * E) / det; vy = (A * E - B * D) / det; }
    out.push({ x: vx, y: vy, support: best.sc, segs: inl });
    rest = rest.filter(s => !inl.includes(s));
  }
  return out;
}

let overlay = null;
/** 지금 그림(또는 지금 레이어)에서 소실점을 찾아 퍼스 자 만들기 */
export async function rulerFromPhoto() {
  const doc = app.doc;
  const src = app.compositor.flatten(doc, { background: '#ffffff' });
  const s = Math.min(1, 900 / Math.max(doc.width, doc.height));
  const c = createCanvas(Math.round(doc.width * s), Math.round(doc.height * s)), x = c.getContext('2d');
  x.drawImage(src, 0, 0, c.width, c.height);
  const res = await app.task('소실점 찾는 중', async () => {
    const segs = detectSegments(x.getImageData(0, 0, c.width, c.height));
    return { segs, vps: findVanishingPoints(segs, c.width, c.height, 3) };
  }, { delay: 0 });
  if (!res) return null;
  const vps = res.vps.map(v => ({ x: v.x / s, y: v.y / s, segs: v.segs.map(q => ({ x1: q.x1 / s, y1: q.y1 / s, x2: q.x2 / s, y2: q.y2 / s })) }));
  if (!vps.length) { app.toast('곧은 선(건물·가구·바닥 무늬 등)이 충분하지 않아 소실점을 찾지 못했습니다', 'warn'); return null; }
  const rule = { type: 'perspective', vps: vps.map(({ x: vx, y: vy }) => ({ x: vx, y: vy })), fromPhoto: true };
  const before = structuredClone(doc.rulers || []);
  doc.rulers = [...before, rule];
  app.history.push({ label: '사진에서 퍼스 자', undo: () => { doc.rulers = structuredClone(before); app.requestRender(); }, redo: () => { doc.rulers = [...structuredClone(before), structuredClone(rule)]; app.requestRender(); } });
  app.markModified?.();
  // 찾은 소실선을 잠깐 보여 주기
  const colors = ['#e5484d', '#3d7be0', '#2fa866'];
  overlay = { vps, until: performance.now() + 7000 };
  app.requestRender();
  setTimeout(() => { overlay = null; app.requestRender(); }, 7200);
  app.toast(`${vps.length}점 투시 자를 만들었습니다 (빨강·파랑·초록 선 = 찾은 소실선). 자 도구로 점을 옮겨 다듬을 수 있습니다`, 'info', { duration: 7000 });
  return { vps: rule.vps, colors };
}

export function initVP() {
  app.addOverlay((ctx, view) => {
    if (!overlay) return;
    const colors = ['#e5484d', '#3d7be0', '#2fa866'];
    ctx.save(); ctx.lineWidth = 2;
    overlay.vps.forEach((v, k) => {
      ctx.strokeStyle = colors[k % 3];
      for (const q of v.segs.slice(0, 80)) { const a = view.toScreen(q.x1, q.y1), b = view.toScreen(q.x2, q.y2); ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke(); }
    });
    ctx.restore();
  });
  app.registerCommand('ruler.fromPhoto', { label: '사진·배경에서 퍼스 자 만들기 (소실점 자동)', run: rulerFromPhoto });
}
