// 내장 AI 실행기: data/models 의 ONNX 모델을 브라우저 안에서 돌린다 (WebGPU, 안 되면 CPU).
// 그림은 이 PC 밖으로 나가지 않는다.
import { app } from '../app.js';
import { createCanvas } from '../core/util.js';
import { server } from '../core/io.js';
import { withKey } from '../core/net.js';

let reg = null;
export async function registry() {
  if (!reg) reg = (await (await fetch('ai-models.json', { cache: 'no-store' })).json()).models;
  return reg;
}
export async function modelInfo(id) { return (await registry()).find(m => m.id === id); }

/** 서버에 받아 둔 모델 상태 { id: { installed, bytes, job } } */
export async function modelStatus() {
  if (!server.http) return {};
  try { return await (await fetch('/api/models', { cache: 'no-store' })).json(); } catch { return {}; }
}
export async function installed(task) {
  const [list, st] = await Promise.all([registry(), modelStatus()]);
  return list.filter(m => (!task || m.task === task) && st[m.id]?.installed);
}
export const modelUrl = (id, file) => `/data/models/${id}/${encodeURIComponent(file)}`;

// 계산은 작업자(worker.js)에서: 화면이 멈추지 않고, WebGPU 가 안 되면 CPU 로 저절로
let worker = null, seq = 0;
const waiting = new Map();
function call(msg) {
  if (!worker) {
    worker = new Worker(new URL('./worker.js', import.meta.url), { type: 'module' });
    worker.onmessage = ({ data }) => { const w = waiting.get(data.id); if (!w) return; waiting.delete(data.id); data.ok ? w.ok(data) : w.no(new Error(data.error)); };
    worker.onerror = (e) => { for (const w of waiting.values()) w.no(new Error(e.message || 'AI 작업자 오류')); waiting.clear(); worker = null; };
    worker.postMessage({ id: 0, op: 'config', cpuOnly: !!app.settings.aiCpuOnly });
  }
  return new Promise((ok, no) => {
    const id = ++seq;
    // 그래픽 드라이버가 멈추는 등으로 답이 영영 안 오면: 작업자를 새로 만들고 실패로 알림 (앱이 '처리 중'에 갇히지 않게)
    const limit = msg.op === 'run' ? (app.settings.aiTimeoutSec || 600) * 1000 : 180000;
    const timer = setTimeout(() => {
      if (!waiting.has(id)) return;
      waiting.delete(id);
      try { worker?.terminate(); } catch { /* 무시 */ }
      worker = null;
      for (const w of waiting.values()) w.no(new Error('AI 작업자를 다시 시작했습니다'));
      waiting.clear();
      no(new Error('AI 계산이 너무 오래 걸려 멈췄습니다. 다시 해 보거나, 설정에서 "AI 는 CPU 로만" 을 켜 보세요'));
    }, limit);
    waiting.set(id, { ok: (v) => { clearTimeout(timer); ok(v); }, no: (e) => { clearTimeout(timer); no(e); } });
    worker.postMessage({ ...msg, id });
  });
}

/** 모델 열기 → { key, id, file, url, ep, s: { inputNames, outputNames } } */
export async function session(id, file) {
  if (!server.http) throw new Error('내장 AI 는 ComicPad 서버(바탕화면 아이콘)로 켰을 때만 됩니다');
  const key = `${id}/${file}`, url = new URL(withKey(modelUrl(id, file)), location.href).href;
  const prefer = (await modelInfo(id))?.ep;                       // 그래픽 가속이 안 되는 모델은 처음부터 CPU 로
  const r = await call({ op: 'open', key, url, prefer });
  return { key, id, file, url, prefer, ep: r.ep, s: { inputNames: r.inputNames, outputNames: r.outputNames } };
}

export let lastGpuError = null;
/** 실행 → { 이름: { data, dims, type } } */
// AI 를 한동안 안 쓰면 작업자를 닫아 그래픽카드(WebGPU)를 돌려준다 — 켜 둔 채면 그림 화면이 버벅이거나 깜빡일 수 있음
let idleTimer = 0;
function idleRelease() {
  clearTimeout(idleTimer);
  idleTimer = setTimeout(() => { if (!waiting.size && worker) resetWorker(); }, (app.settings.aiIdleSec ?? 40) * 1000);
}

export async function run(e, feeds) {
  clearTimeout(idleTimer);
  let r;
  try { r = await call({ op: 'run', key: e.key, url: e.url, prefer: e.prefer, feeds }); } finally { idleRelease(); }
  e.ep = r.ep;
  if (r.gpuErr) lastGpuError = r.gpuErr;
  return r.out;
}
export async function tensor(data, dims, type = 'float32') { return { type, data, dims }; }
/** 설정(그래픽 가속 끄기 등)을 바꾼 뒤 작업자를 새로 */
export function resetWorker() { try { worker?.terminate(); } catch { /* 무시 */ } worker = null; for (const w of waiting.values()) w.no(new Error('AI 작업자를 다시 시작했습니다')); waiting.clear(); }
export function closeAll() { if (worker) call({ op: 'close' }).catch(() => {}); }

/** 처리 중 표시 글자 바꾸기 */
export function busyText(t) { const el = document.querySelector('#busy span'); if (el) el.textContent = t; }

const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
export const IMAGENET = { mean: [0.485, 0.456, 0.406], std: [0.229, 0.224, 0.225] };

/**
 * 그림 → 모델 입력 (CHW Float32).
 * fit: 'stretch'(꽉 채우기) | 'contain'(비율 유지, 남는 곳은 pad) / align: 'center' | 'start'
 * padZero: 남는 곳을 정규화 뒤 0 으로 (SAM)
 */
export function toCHW(src, w, h, o = {}) {
  const { fit = 'stretch', align = 'center', mean = [0, 0, 0], std = [1, 1, 1], scale = 1 / 255, gray = false, bg = '#ffffff', padZero = false } = o;
  const c = createCanvas(w, h), x = c.getContext('2d');
  x.fillStyle = bg; x.fillRect(0, 0, w, h);
  let box = { x: 0, y: 0, w, h };
  if (fit === 'contain') {
    const s = Math.min(w / src.width, h / src.height), dw = Math.max(1, Math.round(src.width * s)), dh = Math.max(1, Math.round(src.height * s));
    box = { x: align === 'center' ? Math.floor((w - dw) / 2) : 0, y: align === 'center' ? Math.floor((h - dh) / 2) : 0, w: dw, h: dh };
  }
  x.imageSmoothingQuality = 'high';
  x.drawImage(src, box.x, box.y, box.w, box.h);
  const d = x.getImageData(0, 0, w, h).data, n = w * h, out = new Float32Array((gray ? 1 : 3) * n);
  for (let y = 0; y < h; y++) {
    const inY = y >= box.y && y < box.y + box.h;
    for (let xx = 0; xx < w; xx++) {
      const i = y * w + xx, p = i * 4;
      if (padZero && !(inY && xx >= box.x && xx < box.x + box.w)) continue;
      if (gray) out[i] = ((d[p] * 0.299 + d[p + 1] * 0.587 + d[p + 2] * 0.114) * scale - mean[0]) / std[0];
      else for (let k = 0; k < 3; k++) out[k * n + i] = (d[p + k] * scale - mean[k]) / std[k];
    }
  }
  return { data: out, box };
}

/** 한 장짜리 마스크(0~1 또는 로짓) → 원래 크기의 알파 캔버스 */
export function maskToCanvas(arr, mw, mh, box, outW, outH, { sigmoid = false, minmax = false } = {}) {
  let lo = 0, hi = 1;
  if (minmax) { lo = Infinity; hi = -Infinity; for (let i = 0; i < mw * mh; i++) { const v = arr[i]; if (v < lo) lo = v; if (v > hi) hi = v; } if (hi - lo < 1e-6) hi = lo + 1; }
  const m = createCanvas(mw, mh), mx = m.getContext('2d'), img = mx.createImageData(mw, mh);
  for (let i = 0; i < mw * mh; i++) {
    let v = arr[i];
    if (sigmoid) v = 1 / (1 + Math.exp(-v));
    if (minmax) v = (v - lo) / (hi - lo);
    img.data[i * 4 + 3] = clamp(Math.round(v * 255), 0, 255);
  }
  mx.putImageData(img, 0, 0);
  const out = createCanvas(outW, outH), ox = out.getContext('2d');
  ox.imageSmoothingQuality = 'high';
  const b = box || { x: 0, y: 0, w: mw, h: mh };
  ox.drawImage(m, b.x, b.y, b.w, b.h, 0, 0, outW, outH);
  return out;
}

/**
 * 크기와 상관없이 도는 모델(선화·크기 키우기)을 조각으로 나눠 돌리기.
 * enc(src, p, buf, idx, n): 입력 픽셀 p(RGBA 시작)를 buf 의 idx 자리에 (채널 간격 n)
 * dec(od, q, on, dst, p): 출력 q 자리(채널 간격 on)를 dst(RGBA) 의 p 자리에
 */
export async function runTiled(e, srcCanvas, o) {
  const { tile = 256, pad = 16, scale = 1, outScale = scale, inC = 3, multiple = 1, enc, dec, label = 'AI' } = o;
  const W = srcCanvas.width, H = srcCanvas.height;
  const src = srcCanvas.getContext('2d').getImageData(0, 0, W, H).data;
  const dest = createCanvas(Math.round(W * outScale), Math.round(H * outScale)), dx = dest.getContext('2d');
  dx.imageSmoothingQuality = 'high';
  const inName = e.s.inputNames[0], outName = e.s.outputNames[0];
  const tiles = [];
  for (let y = 0; y < H; y += tile) for (let x = 0; x < W; x += tile) tiles.push([x, y]);
  let k = 0;
  for (const [x0, y0] of tiles) {
    busyText(`${label} ${Math.round(k++ / tiles.length * 100)}%`);
    const tw = Math.min(tile, W - x0), th = Math.min(tile, H - y0);
    const ix = x0 - pad, iy = y0 - pad;
    const iw = Math.ceil((tw + 2 * pad) / multiple) * multiple, ih = Math.ceil((th + 2 * pad) / multiple) * multiple;
    const n = iw * ih, buf = new Float32Array(inC * n);
    for (let yy = 0; yy < ih; yy++) {
      const sy = clamp(iy + yy, 0, H - 1);
      for (let xx = 0; xx < iw; xx++) enc(src, (sy * W + clamp(ix + xx, 0, W - 1)) * 4, buf, yy * iw + xx, n);
    }
    const out = (await run(e, { [inName]: await tensor(buf, [1, inC, ih, iw]) }))[outName];
    const oh = out.dims[2], ow = out.dims[3], od = out.data, on = oh * ow;
    const cw = tw * scale, ch = th * scale, cx = pad * scale, cy = pad * scale;
    const tc = createCanvas(cw, ch), tx = tc.getContext('2d'), img = tx.createImageData(cw, ch);
    for (let yy = 0; yy < ch; yy++) for (let xx = 0; xx < cw; xx++) dec(od, (cy + yy) * ow + cx + xx, on, img.data, (yy * cw + xx) * 4);
    tx.putImageData(img, 0, 0);
    dx.drawImage(tc, Math.round(x0 * outScale), Math.round(y0 * outScale), Math.round(tw * outScale), Math.round(th * outScale));
    out.dispose?.();
    await new Promise(r => setTimeout(r, 0));               // 화면이 멈추지 않게
  }
  return dest;
}
