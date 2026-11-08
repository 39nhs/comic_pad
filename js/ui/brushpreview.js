// 브러시 미리보기 · 시험 그리기 칸 · 필압 곡선 편집기
import { app } from '../app.js';
import { h, createCanvas } from '../core/util.js';
import { Stroke, curveLut, pressureCurve, PRESSURE_PRESETS } from '../core/brush.js';
import { applyGrain, hasBrushPost } from '../core/paper.js';

const lumHex = (c) => { const v = parseInt(String(c).slice(1, 7), 16); return ((v >> 16) & 255) * 0.299 + ((v >> 8) & 255) * 0.587 + (v & 255) * 0.114; };
const fakeDoc = (w, hh) =>({ width: w, height: hh, selection: { clipTo() {}, active: false }, paper: {} });

/** 브러시 한 획을 그려 본 그림 (필압 0 → 1 → 0, S자). 크기는 칸에 맞게 줄여서 */
export function previewStroke(preset, { w = 240, h: hh = 64, color = null, erase = false, fit = true } = {}) {
  // 픽셀 끝: 작게 그려서 4배로 (픽셀이 보이게)
  if (preset.tip === 'pixel' && fit && w >= 32) {
    const k = 4, small = previewStroke({ ...preset, size: Math.min(preset.size, 6) }, { w: Math.round(w / k), h: Math.round(hh / k), color, erase, fit: false });
    const big = createCanvas(w, hh), bx = big.getContext('2d');
    bx.imageSmoothingEnabled = false;
    bx.drawImage(small, 0, 0, small.width * k, small.height * k);
    return big;
  }
  const out = createCanvas(w, hh), ox = out.getContext('2d');
  const s = fit ? Math.min(1, (hh * 0.42) / Math.max(1, preset.size)) : 1;
  const p = { ...preset, size: Math.max(0.6, preset.size * s), taperIn: (preset.taperIn || 0) * s, taperOut: (preset.taperOut || 0) * s, stabilizer: 0, mix: 0 };
  const buf = createCanvas(w, hh);
  // 명암 잉크: 팔레트 가운데 밝기 색 띠 위에 그려 봄 (밑 색이 있어야 보임)
  let base = null;
  if (preset.shading) {
    const pal = (app.settings.palette || []).slice().sort((a, b) => lumHex(a) - lumHex(b));
    base = createCanvas(w, hh);
    const bx = base.getContext('2d');
    bx.fillStyle = pal[Math.floor(pal.length / 2)] || '#888888';
    bx.fillRect(0, hh * 0.2, w, hh * 0.6);
  }
  try {
    const st = new Stroke({ canvas: buf, preset: p, color: erase ? '#777777' : (color || preset.color || app.color?.fg || '#000000'), doc: fakeDoc(w, hh), noClip: true, sample: base ? base.getContext('2d').getImageData(0, 0, w, hh) : null });
    if (base) ox.drawImage(base, 0, 0);
    const n = 70, pad = Math.min(16, w * 0.08);
    let last = null;
    for (let i = 0; i <= n; i++) {
      const t = i / n;
      const pt = { x: pad + (w - 2 * pad) * t, y: hh / 2 - Math.sin(t * Math.PI * 2) * hh * 0.22, p: Math.max(0.04, Math.sin(Math.PI * t)), t: i * 9 };
      if (!i) st.begin(pt); else st.add(pt);
      last = pt;
    }
    const r = st.end(last);
    if (r && hasBrushPost(p)) try { applyGrain(buf.getContext('2d'), fakeDoc(w, hh), p, r); } catch { /* 결 없이 */ }
  } catch (e) { console.warn('미리보기 실패', e); }
  ox.globalAlpha = preset.opacity ?? 1;
  ox.drawImage(buf, 0, 0);
  return out;
}

// 목록용 작은 미리보기는 같은 설정이면 다시 그리지 않음
const thumbCache = new Map();
export function presetThumb(preset, color, w = 96, hh = 26) {
  const key = JSON.stringify([preset, color, w, hh, app.settings.pressurePts, app.settings.pressureGamma]);
  let c = thumbCache.get(key);
  if (!c) {
    c = previewStroke(preset, { w, h: hh, color });
    if (thumbCache.size > 120) thumbCache.clear();
    thumbCache.set(key, c);
  }
  const img = createCanvas(w, hh);
  img.getContext('2d').drawImage(c, 0, 0);
  img.className = 'preset-thumb';
  return img;
}

/** 시험 그리기 칸: 지금 브러시로 마음대로 그려 보기 (그림에는 안 들어감) */
export function brushTestPad(getPreset, { erase = false } = {}) {
  const W = 300, H = 120;
  const pad = h('canvas.brush-pad', { width: W, height: H });
  const big = h('canvas.brush-pad-preview', { width: W, height: 64 });
  const ctx = pad.getContext('2d');
  const done = createCanvas(W, H), dctx = done.getContext('2d');      // 다 그린 획들
  const show = () => {
    ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, W, H);
    ctx.drawImage(done, 0, 0);
    if (buf) { ctx.globalAlpha = getPreset().opacity ?? 1; ctx.drawImage(buf, 0, 0); ctx.globalAlpha = 1; }
  };
  const paint = () => { dctx.clearRect(0, 0, W, H); show(); };
  let st = null, buf = null, last = null, queued = false;
  const draw = () => { if (queued) return; queued = true; requestAnimationFrame(() => { queued = false; show(); }); };
  const pos = (e) => { const r = pad.getBoundingClientRect(); return { x: (e.clientX - r.left) * W / r.width, y: (e.clientY - r.top) * H / r.height }; };
  pad.addEventListener('pointerdown', (e) => {
    pad.setPointerCapture(e.pointerId);
    const p = getPreset();
    buf = createCanvas(W, H);
    // 칸이 작으니 아주 큰 브러시는 줄여서
    const sp = { ...p, size: Math.min(p.size, H * 0.6), mix: 0 };
    st = new Stroke({ canvas: buf, preset: sp, color: erase ? '#888888' : (p.color || app.color.fg), doc: fakeDoc(W, H), noClip: true });
    last = { ...pos(e), p: e.pointerType === 'pen' ? e.pressure : 0.6, t: e.timeStamp };
    st.begin(last);
    draw();
  });
  pad.addEventListener('pointermove', (e) => {
    if (!st) return;
    for (const ev of e.getCoalescedEvents ? e.getCoalescedEvents() : [e]) {
      last = { ...pos(ev), p: ev.pointerType === 'pen' ? (ev.pressure || last.p) : 0.6, t: ev.timeStamp };
      st.add(last);
    }
    draw();
  });
  const finish = () => {
    if (!st) return;
    st.end(last);
    dctx.globalAlpha = getPreset().opacity ?? 1; dctx.drawImage(buf, 0, 0); dctx.globalAlpha = 1;
    st = null; buf = null;
    show();
    info.textContent = '지우려면 "칸 비우기"';
  };
  pad.addEventListener('pointerup', finish);
  pad.addEventListener('pointercancel', finish);
  show();
  const info = h('div.help', null, '여기에 펜으로 그려 보세요 (그림에는 안 들어감)');
  const refresh = () => {
    const c = previewStroke(getPreset(), { w: W, h: 64, erase });
    const bx = big.getContext('2d');
    bx.fillStyle = '#fff'; bx.fillRect(0, 0, W, 64);
    bx.drawImage(c, 0, 0);
  };
  refresh();
  const clear = h('button.btn', { type: 'button', onclick: () => { paint(); info.textContent = '여기에 펜으로 그려 보세요 (그림에는 안 들어감)'; } }, '칸 비우기');
  return { el: h('div.brush-test', null, big, pad, h('div.row', null, clear, info)), refresh };
}

/**
 * 필압 곡선 편집기. get() → 점 목록 또는 null(곧은 기본), set(점 목록)
 * 점을 끌어 옮기고, 빈 곳을 두 번 탭하면 점 추가, 점을 두 번 탭하면 지우기.
 */
export function curveEditor({ get, set, title = '필압 곡선' }) {
  const S = 180, PAD = 8;
  const c = h('canvas.curve-edit', { width: S, height: S });
  const x = c.getContext('2d');
  let pts = structuredClone(get() || [[0, 0], [1, 1]]), drag = -1, live = null;
  const toXY = ([u, v]) => [PAD + u * (S - 2 * PAD), S - PAD - v * (S - 2 * PAD)];
  const fromXY = (px, py) => [Math.min(1, Math.max(0, (px - PAD) / (S - 2 * PAD))), Math.min(1, Math.max(0, (S - PAD - py) / (S - 2 * PAD)))];
  const draw = () => {
    x.clearRect(0, 0, S, S);
    x.fillStyle = getComputedStyle(document.body).getPropertyValue('--panel') || '#222'; x.fillRect(0, 0, S, S);
    x.strokeStyle = 'rgba(128,128,128,.35)'; x.lineWidth = 1;
    for (let i = 0; i <= 4; i++) { const g = PAD + i * (S - 2 * PAD) / 4; x.beginPath(); x.moveTo(g, PAD); x.lineTo(g, S - PAD); x.moveTo(PAD, g); x.lineTo(S - PAD, g); x.stroke(); }
    x.setLineDash([3, 3]); x.beginPath(); x.moveTo(...toXY([0, 0])); x.lineTo(...toXY([1, 1])); x.stroke(); x.setLineDash([]);
    const lut = curveLut(pts);
    x.strokeStyle = '#e8a33d'; x.lineWidth = 2.5; x.beginPath();
    for (let i = 0; i < 256; i += 3) x.lineTo(...toXY([i / 255, lut[i]]));
    x.lineTo(...toXY([1, lut[255]])); x.stroke();
    for (const p of pts) { const [px, py] = toXY(p); x.fillStyle = '#fff'; x.strokeStyle = '#e8a33d'; x.lineWidth = 2; x.beginPath(); x.arc(px, py, 5, 0, 7); x.fill(); x.stroke(); }
    if (live != null) { const v = lut[Math.round(live * 255)]; const [px, py] = toXY([live, v]); x.fillStyle = '#3d7be0'; x.beginPath(); x.arc(px, py, 4, 0, 7); x.fill(); }
  };
  const commit = () => { const id = JSON.stringify(pts) === JSON.stringify([[0, 0], [1, 1]]); set(id ? null : structuredClone(pts)); };
  const pos = (e) => { const r = c.getBoundingClientRect(); return [(e.clientX - r.left) * S / r.width, (e.clientY - r.top) * S / r.height]; };
  const hit = (e) => { const [px, py] = pos(e); return pts.findIndex(p => { const [a, b] = toXY(p); return Math.hypot(a - px, b - py) < 12; }); };
  let lastTap = 0;
  c.addEventListener('pointerdown', (e) => {
    c.setPointerCapture(e.pointerId);
    const i = hit(e), now = e.timeStamp, dbl = now - lastTap < 350;
    lastTap = now;
    if (dbl && i > 0 && i < pts.length - 1) { pts.splice(i, 1); drag = -1; draw(); commit(); return; }
    if (i >= 0) { drag = i; return; }
    if (dbl) {
      const p = fromXY(...pos(e));
      pts.push(p); pts.sort((a, b) => a[0] - b[0]); drag = pts.indexOf(p); draw(); commit();
    }
  });
  c.addEventListener('pointermove', (e) => {
    if (e.pointerType === 'pen' && !e.buttons) { live = Math.min(1, Math.max(0, e.pressure)); }
    if (drag < 0) { if (e.buttons && e.pointerType === 'pen') live = e.pressure; draw(); return; }
    let [u, v] = fromXY(...pos(e));
    if (drag === 0) u = 0; else if (drag === pts.length - 1) u = 1;
    else u = Math.min(pts[drag + 1][0] - 0.02, Math.max(pts[drag - 1][0] + 0.02, u));
    pts[drag] = [+u.toFixed(3), +v.toFixed(3)];
    draw();
  });
  c.addEventListener('pointerup', () => { if (drag >= 0) commit(); drag = -1; });
  // 다른 곳(그림판)에서 펜을 누르는 동안 지금 필압을 곡선 위에 파란 점으로
  const onPress = (e) => { if (e.pointerType === 'pen' && e.buttons) { live = e.pressure; draw(); } };
  window.addEventListener('pointermove', onPress, { passive: true });
  const presetSel = h('select', { onchange: () => { if (!presetSel.value) return; pts = structuredClone(PRESSURE_PRESETS[presetSel.value]); presetSel.value = ''; draw(); commit(); } },
    h('option', { value: '' }, '곡선 고르기…'), ...Object.keys(PRESSURE_PRESETS).map(k => h('option', { value: k }, k)));
  draw();
  const el = h('div.curve-box', null,
    h('div.row', null, c, h('div.curve-side', null, h('b', null, title), presetSel,
      h('div.help', null, '점을 끌어 모양 바꾸기 · 빈 곳 두 번 탭 = 점 추가 · 점 두 번 탭 = 지우기. 왼쪽 = 약하게 누름, 오른쪽 = 세게. 위로 갈수록 굵게·진하게.'),
      h('button.btn', { type: 'button', onclick: () => { pts = [[0, 0], [1, 1]]; draw(); commit(); } }, '곧게 (초기화)'))));
  return { el, redraw: () => { pts = structuredClone(get() || [[0, 0], [1, 1]]); draw(); }, dispose: () => window.removeEventListener('pointermove', onPress) };
}

/** 지금 설정 곡선으로 p → 값 (설정 화면 시험용) */
export const curveValue = (p, preset) => pressureCurve(p, preset);
