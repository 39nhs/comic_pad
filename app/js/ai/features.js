// 내장 AI 기능: 선화 추출 · 캐릭터/배경 따기 · 깊이 · 크기 키우기 (모두 이 PC 안에서)
import { app } from '../app.js';
import { createCanvas } from '../core/util.js';
import { form } from '../ui/dialogs.js';
import { addLayer, setDoc } from '../core/ops.js';
import { Doc } from '../core/doc.js';
import * as R from './runtime.js';
import { licenseGate, ncName, BADGE } from './license.js';

const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);

/** 설치된 모델 중에서 고르기 + 라이선스 확인. 없으면 모델 관리 창으로 */
export async function chooseModel(task, title, extra = [], okLabel = '실행') {
  const list = await R.installed(task);
  if (!list.length) {
    app.toast('이 기능에 쓸 AI 모델이 아직 없습니다. AI > AI 모델 관리에서 받으세요', 'warn', { duration: 6000 });
    app.run('ai.models');
    return null;
  }
  const last = app.settings.aiPick?.[task];
  const v = await form(title, [
    { key: 'model', label: '모델', type: 'select', options: list.map(m => [m.id, `${m.name} · ${BADGE[m.commercial]}`]), value: list.some(m => m.id === last) ? last : list[0].id },
    ...extra,
  ], { okLabel });
  if (!v) return null;
  (app.settings.aiPick ||= {})[task] = v.model;
  app.saveSettingsSoon();
  const m = list.find(x => x.id === v.model);
  if (!(await licenseGate(m))) return null;
  if (m.slow) app.toast(`${m.name}: ${m.slow}`, 'info', { duration: 5000 });
  return { m, v };
}

const SRC = { key: 'src', label: '바탕으로 쓸 것', type: 'segment', options: [['all', '보이는 그림 전체'], ['layer', '지금 레이어']], value: 'all' };

/** 흰 바탕에 합친 원본 (선택 영역이 있으면 그 부분만) */
function source(src, r, { transparent = false } = {}) {
  const doc = app.doc, c = createCanvas(r.w, r.h), x = c.getContext('2d');
  if (!transparent) { x.fillStyle = '#fff'; x.fillRect(0, 0, r.w, r.h); }
  const full = src === 'layer' ? doc.active.canvas : app.compositor.flatten(doc, transparent ? {} : { paper: true, background: '#ffffff' });
  x.drawImage(full, r.x, r.y, r.w, r.h, 0, 0, r.w, r.h);
  return c;
}
const region = () => { const d = app.doc; return d.selection.active ? { ...d.selection.bounds } : { x: 0, y: 0, w: d.width, h: d.height }; };

/** 결과를 새 레이어로 (선택 영역이 있으면 그 안만) */
function resultLayer(name, canvas, r, m, extra = {}) {
  const doc = app.doc;
  let layer = null;
  app.history.batch(name, () => {
    layer = addLayer({ name: ncName(name, m), ...extra });
    layer.ctx.drawImage(canvas, r.x, r.y, r.w, r.h);
    if (doc.selection.active) { layer.ctx.globalCompositeOperation = 'destination-in'; layer.ctx.drawImage(doc.selection.mask, 0, 0); layer.ctx.globalCompositeOperation = 'source-over'; }
    layer.touch();
  });
  app.requestRender();
  return layer;
}

/** 흑백(흰 바탕) → 투명 바탕 선 (어두운 만큼 진하게) */
function grayToLines(c, color = '#000000') {
  const x = c.getContext('2d'), img = x.getImageData(0, 0, c.width, c.height), d = img.data;
  const r = parseInt(color.slice(1, 3), 16), g = parseInt(color.slice(3, 5), 16), b = parseInt(color.slice(5, 7), 16);
  for (let i = 0; i < d.length; i += 4) {
    const lum = d[i] * 0.299 + d[i + 1] * 0.587 + d[i + 2] * 0.114;
    d[i] = r; d[i + 1] = g; d[i + 2] = b; d[i + 3] = clamp(Math.round((250 - lum) * 1.2), 0, 255);
  }
  x.putImageData(img, 0, 0);
  return c;
}

// ---------------------------------------------------------------------
// 선화 추출
// ---------------------------------------------------------------------
export async function lineartCanvas(m, src) {
  const e = await R.session(m.id, m.files[0].name);
  if (m.id === 'line-manga') {
    return R.runTiled(e, src, { tile: 512, pad: 32, multiple: 16, inC: 1, label: '선화 추출',
      enc: (s, p, b, i) => { b[i] = s[p] * 0.299 + s[p + 1] * 0.587 + s[p + 2] * 0.114; },
      dec: (od, q, on, d, p) => { const v = clamp(od[q], 0, 255); d[p] = d[p + 1] = d[p + 2] = v; d[p + 3] = 255; } });
  }
  if (m.id === 'line-informative') {
    return R.runTiled(e, src, { tile: 512, pad: 32, multiple: 8, inC: 3, label: '선화 추출',
      enc: (s, p, b, i, n) => { b[i] = s[p] / 255; b[n + i] = s[p + 1] / 255; b[2 * n + i] = s[p + 2] / 255; },
      dec: (od, q, on, d, p) => { const v = clamp(od[q] * 255, 0, 255); d[p] = d[p + 1] = d[p + 2] = v; d[p + 3] = 255; } });
  }
  // Anime2Sketch: 그림 전체를 한 번에 (256 의 배수 크기, 긴 쪽 최대 1024)
  const s = Math.min(1, 1024 / Math.max(src.width, src.height));
  const W = Math.max(256, Math.round(src.width * s / 256) * 256), H = Math.max(256, Math.round(src.height * s / 256) * 256);
  const { data } = R.toCHW(src, W, H, { mean: [0.5, 0.5, 0.5], std: [0.5, 0.5, 0.5] });
  const out = (await R.run(e, { [e.s.inputNames[0]]: await R.tensor(data, [1, 3, H, W]) }))[e.s.outputNames[0]];
  const c = createCanvas(W, H), x = c.getContext('2d'), img = x.createImageData(W, H);
  for (let i = 0; i < W * H; i++) { const v = clamp((out.data[i] + 1) * 127.5, 0, 255); img.data[i * 4] = img.data[i * 4 + 1] = img.data[i * 4 + 2] = v; img.data[i * 4 + 3] = 255; }
  x.putImageData(img, 0, 0);
  const back = createCanvas(src.width, src.height), bx = back.getContext('2d');
  bx.imageSmoothingQuality = 'high';
  bx.drawImage(c, 0, 0, src.width, src.height);
  return back;
}

export async function aiLineart() {
  const pick = await chooseModel('lineart', 'AI 선화 추출', [SRC,
    { key: 'color', label: '선 색을 지금 전경색으로', type: 'checkbox', value: false },
    { key: 'info', type: 'info', label: `결과는 "AI 선화" 새 레이어(흰 바탕 없이 선만)로 들어갑니다${app.doc.selection.active ? ' (선택 영역 부분만)' : ''}.` }], '추출하기');
  if (!pick) return;
  const { m, v } = pick, r = region();
  const out = await app.task('AI 선화 추출', async () => grayToLines(await lineartCanvas(m, source(v.src, r)), v.color ? app.color.fg : '#000000'), { delay: 0 });
  if (out) { resultLayer('AI 선화', out, r, m); app.toast('선화를 새 레이어로 넣었습니다'); }
}

// ---------------------------------------------------------------------
// 캐릭터·배경 따기
// ---------------------------------------------------------------------
export async function cutoutMask(m, src) {
  const e = await R.session(m.id, m.files[0].name);
  let inp, box = null, opt = {};
  if (m.id === 'seg-anime') { ({ data: inp, box } = R.toCHW(src, 1024, 1024, { fit: 'contain', bg: '#000000' })); }
  else if (m.id === 'seg-birefnet') { ({ data: inp } = R.toCHW(src, 1024, 1024, R.IMAGENET)); opt = { sigmoid: true }; }
  else { ({ data: inp } = R.toCHW(src, 1024, 1024, { mean: [0.5, 0.5, 0.5], std: [1, 1, 1] })); opt = { minmax: true }; }
  const out = (await R.run(e, { [e.s.inputNames[0]]: await R.tensor(inp, [1, 3, 1024, 1024]) }))[e.s.outputNames[0]];
  return R.maskToCanvas(out.data, 1024, 1024, box, src.width, src.height, opt);
}

export async function aiCutout() {
  const pick = await chooseModel('cutout', 'AI 캐릭터·배경 따기', [SRC,
    { key: 'out', label: '결과', type: 'segment', options: [['sel', '선택 영역으로'], ['layer', '오려 낸 새 레이어']], value: 'sel' }], '따기');
  if (!pick) return;
  const { m, v } = pick, doc = app.doc, r = region();
  const mask = await app.task('AI 따기', () => cutoutMask(m, source(v.src, r)), { delay: 0 });
  if (!mask) return;
  if (v.out === 'sel') {
    doc.selection.apply(ctx => ctx.drawImage(mask, r.x, r.y), 'replace', r);
    app.toast('선택 영역으로 만들었습니다 (가장자리는 부드럽게)');
  } else {
    const c = source(v.src, r, { transparent: true }), x = c.getContext('2d');
    x.globalCompositeOperation = 'destination-in'; x.drawImage(mask, 0, 0);
    resultLayer('AI 따기', c, r, m);
    app.toast('오려 낸 그림을 새 레이어로 넣었습니다');
  }
}

// ---------------------------------------------------------------------
// 깊이 (밝을수록 가까움)
// ---------------------------------------------------------------------
export async function depthCanvas(m, src, res = 518) {
  const e = await R.session(m.id, m.files[0].name);
  const s = res / Math.max(src.width, src.height);
  const W = Math.max(14 * 8, Math.round(src.width * s / 14) * 14), H = Math.max(14 * 8, Math.round(src.height * s / 14) * 14);
  const { data } = R.toCHW(src, W, H, R.IMAGENET);
  const out = (await R.run(e, { [e.s.inputNames[0]]: await R.tensor(data, [1, 3, H, W]) }))[e.s.outputNames[0]];
  const oh = out.dims.at(-2), ow = out.dims.at(-1);
  const m2 = R.maskToCanvas(out.data, ow, oh, null, src.width, src.height, { minmax: true });
  // 알파 마스크 → 흑백 그림
  const c = createCanvas(src.width, src.height), x = c.getContext('2d');
  x.fillStyle = '#000'; x.fillRect(0, 0, c.width, c.height);
  const w = createCanvas(src.width, src.height), wx = w.getContext('2d');
  wx.fillStyle = '#fff'; wx.fillRect(0, 0, w.width, w.height);
  wx.globalCompositeOperation = 'destination-in'; wx.drawImage(m2, 0, 0);
  x.drawImage(w, 0, 0);
  return c;
}

export async function aiDepth() {
  const pick = await chooseModel('depth', 'AI 깊이 추정', [SRC,
    { key: 'res', label: '정밀도', type: 'segment', options: [['518', '보통'], ['770', '자세히'], ['1022', '아주 자세히 (느림)']], value: '518' },
    { key: 'info', type: 'info', label: '밝을수록 가까운 곳입니다. 명암·공기 원근·3D 배경 참고용으로 쓰세요 ("AI 깊이" 새 레이어).' }], '추정하기');
  if (!pick) return;
  const { m, v } = pick, r = region();
  const out = await app.task('AI 깊이 추정', () => depthCanvas(m, source(v.src, r), +v.res), { delay: 0 });
  if (out) { resultLayer('AI 깊이', out, r, m); app.toast('깊이 그림을 새 레이어로 넣었습니다 (밝을수록 가까움)'); }
}

// ---------------------------------------------------------------------
// 크기 키우기 → 새 작품
// ---------------------------------------------------------------------
export async function upscaleCanvas(m, src, outScale = 4) {
  const e = await R.session(m.id, m.files[0].name);
  return R.runTiled(e, src, { tile: m.id === 'up-anime6b' ? 128 : 192, pad: 12, scale: 4, outScale, inC: 3, label: '크기 키우기',
    enc: (s, p, b, i, n) => { b[i] = s[p] / 255; b[n + i] = s[p + 1] / 255; b[2 * n + i] = s[p + 2] / 255; },
    dec: (od, q, on, d, p) => { d[p] = clamp(od[q] * 255, 0, 255); d[p + 1] = clamp(od[on + q] * 255, 0, 255); d[p + 2] = clamp(od[2 * on + q] * 255, 0, 255); d[p + 3] = 255; } });
}

export async function aiUpscale() {
  const doc = app.doc, r = region();
  const pick = await chooseModel('upscale', 'AI 크기 키우기', [
    { key: 'scale', label: '배율', type: 'segment', options: [['2', '2배'], ['4', '4배']], value: '2' },
    { key: 'info', type: 'info', label: `보이는 그림${doc.selection.active ? '(선택 영역)' : ''}을 키워 새 작품으로 엽니다. 지금 작품은 그대로입니다.` }], '키우기');
  if (!pick) return;
  const { m, v } = pick, k = +v.scale;
  if (r.w * k * r.h * k > 80e6) { app.toast(`결과가 너무 큽니다 (${r.w * k}×${r.h * k}). 2배로 하거나 일부만 선택하세요`, 'warn'); return; }
  const out = await app.task('AI 크기 키우기', () => upscaleCanvas(m, source('all', r), k), { delay: 0 });
  if (!out) return;
  const d = new Doc(out.width, out.height, { name: `${doc.name} ×${k}` });
  const l = d.createLayer({ name: ncName('키운 그림', m) });
  d.layers.push(l); d.activeId = l.id;
  l.ctx.drawImage(out, 0, 0);
  if (doc.dpi) d.dpi = doc.dpi * k;
  if (doc.aiLog?.length) d.aiLog = structuredClone(doc.aiLog);
  setDoc(d); d.modified = true;
  app.toast(`${out.width}×${out.height} 새 작품으로 열었습니다`);
}
