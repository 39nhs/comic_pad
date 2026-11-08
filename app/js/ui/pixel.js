// 픽셀아트 메뉴: 새 픽셀 작품 · 픽셀 격자 · 팔레트 고정 · 색 수 줄이기 · 팔레트 파일 · 팔레트 색 바꾸기
import { app } from '../app.js';
import { h, pickFile, downloadBlob, createCanvas } from '../core/util.js';
import { form } from './dialogs.js';
import { newDocument, checkEditable } from '../core/ops.js';
import { setActivePreset } from '../tools/brush.js';
import * as P from '../core/pixel.js';

const palChanged = () => { app.bus.emit('palette:changed'); app.bus.emit('recent:changed'); };
const pixelOf = (doc = app.doc) => (doc.pixel ||= { art: false, lock: false, palette: null });

async function newPixelDoc() {
  const v = await form('픽셀 아트 새로 만들기', [
    { key: 'size', label: '크기', type: 'segment', options: [['16', '16'], ['32', '32'], ['64', '64'], ['128', '128'], ['256', '256'], ['custom', '직접']], value: '64' },
    { key: 'w', label: '가로 (직접일 때)', type: 'number', value: 48, min: 1, max: 4096 },
    { key: 'h', label: '세로 (직접일 때)', type: 'number', value: 48, min: 1, max: 4096 },
    { key: 'clear', label: '투명 바탕 (스프라이트용)', type: 'checkbox', value: true },
    { key: 'lock', label: '팔레트 색으로만 칠하기', type: 'checkbox', value: false },
    { key: 'info', type: 'info', label: '픽셀 연필(1px, 픽셀 퍼펙트)로 시작하고, 확대하면 픽셀 격자가 보입니다. 애니메이션은 애니 패널에서.' },
  ], { okLabel: '만들기' });
  if (!v) return;
  const w = v.size === 'custom' ? Math.max(1, +v.w | 0) : +v.size, hh = v.size === 'custom' ? Math.max(1, +v.h | 0) : +v.size;
  const doc = newDocument(w, hh, { name: `픽셀 ${w}×${hh}`, paperVisible: !v.clear, dpi: 72 });
  doc.pixel = { art: true, lock: !!v.lock, palette: null };
  app.settings.pixelGrid = true;
  try { setActivePreset('brush', 'pixel1'); setActivePreset('eraser', 'er-pixel'); } catch { /* 프리셋 없음 */ }
  app.setTool('brush');
  app.requestRender();
}

async function quantizeDialog() {
  const doc = app.doc, layer = doc.active?.target;
  if (!checkEditable(layer)) return;
  const v = await form('색 수 줄이기 (픽셀아트 팔레트로)', [
    { key: 'mode', label: '색', type: 'segment', options: [['auto', '그림에서 뽑기'], ['palette', '지금 팔레트 색으로']], value: 'auto' },
    { key: 'n', label: '색 수 (그림에서 뽑을 때)', type: 'number', value: 16, min: 2, max: 256 },
    { key: 'dither', label: '디더링', type: 'segment', options: [['none', '없음'], ['bayer2', '격자 2×2'], ['bayer4', '격자 4×4'], ['fs', '오차 확산']], value: 'none' },
    { key: 'strength', label: '디더 세기', type: 'number', value: 1, min: 0.1, max: 2, step: 0.1 },
    { key: 'setPal', label: '뽑은 색을 팔레트로 쓰기 (이 작품)', type: 'checkbox', value: true },
    { key: 'info', type: 'info', label: `"${doc.active.name}" 레이어에 적용합니다. 반투명은 반 넘으면 불투명, 아니면 투명이 됩니다. 마음에 안 들면 실행취소.` },
  ], { okLabel: '줄이기' });
  if (!v) return;
  const W = layer.canvas.width, H = layer.canvas.height;
  let pal = null;
  await app.task('색 수 줄이기', async () => {
    const img = layer.ctx.getImageData(0, 0, W, H);
    pal = v.mode === 'palette' ? P.paletteOf(doc) : P.medianCut(img.data, Math.max(2, Math.min(256, +v.n | 0)));
    if (!pal.length) throw new Error('칠한 픽셀이 없습니다');
    app.history.pixels(layer, '색 수 줄이기', (ctx) => {
      ctx.putImageData(P.quantizeImage(img, pal, v.dither, +v.strength), 0, 0);
      return { x: 0, y: 0, w: W, h: H };
    });
  }, { delay: 0 });
  if (!pal) return;
  if (v.mode === 'auto' && v.setPal) {
    pixelOf(doc).palette = pal.map(c => P.toHex(...c));
    app.settings.palette = [...pixelOf(doc).palette];
    palChanged(); app.saveSettingsSoon();
  }
  layer.touch(); app.requestRender();
  app.toast(`${pal.length}가지 색으로 줄였습니다`);
}

async function importPalette() {
  const f = await pickFile('.gpl,.hex,.txt,.pal');
  if (!f) return;
  const colors = P.parsePaletteFile(await f.text(), f.name);
  if (!colors.length) { app.toast('팔레트 색을 찾지 못했습니다 (.gpl / .hex / .txt)', 'warn'); return; }
  const v = await form('팔레트 가져오기', [
    { key: 'info', type: 'info', label: `${f.name}: ${colors.length}가지 색` },
    { key: 'where', label: '어디에', type: 'segment', options: [['app', '앱 팔레트 바꾸기'], ['doc', '이 작품 팔레트로만']], value: 'app' },
  ], { okLabel: '가져오기' });
  if (!v) return;
  if (v.where === 'doc') pixelOf().palette = colors;
  else { app.settings.palette = colors; app.saveSettingsSoon(); }
  palChanged();
  app.toast(`팔레트 ${colors.length}색을 가져왔습니다`);
}

async function exportPalette() {
  const colors = app.doc?.pixel?.palette?.length ? app.doc.pixel.palette : app.settings.palette;
  const v = await form('팔레트 내보내기', [
    { key: 'fmt', label: '형식', type: 'segment', options: [['gpl', '.gpl (GIMP·Aseprite·Krita)'], ['hex', '.hex (Lospec)']], value: 'gpl' },
    { key: 'name', label: '이름', type: 'text', value: app.doc?.name || 'ComicPad' },
  ], { okLabel: '내보내기' });
  if (!v) return;
  const text = v.fmt === 'gpl' ? P.paletteToGpl(colors, v.name) : P.paletteToHex(colors);
  downloadBlob(new Blob([text], { type: 'text/plain' }), `${v.name || 'palette'}.${v.fmt}`);
}

async function replaceColorDialog() {
  const doc = app.doc, layer = doc.active?.target;
  if (!checkEditable(layer)) return;
  const pal = (doc.pixel?.palette?.length ? doc.pixel.palette : app.settings.palette) || [];
  const v = await form('팔레트 색 바꾸기', [
    { key: 'from', label: '바꿀 색', type: 'color', value: app.color.bg && pal.includes(app.color.bg) ? app.color.bg : (pal[0] || '#000000') },
    { key: 'to', label: '새 색 (지금 전경색)', type: 'color', value: app.color.fg },
    { key: 'tol', label: '허용 오차 (0 = 똑같은 색만)', type: 'number', value: 0, min: 0, max: 128 },
    { key: 'pal', label: '팔레트의 그 색도 바꾸기', type: 'checkbox', value: true },
    { key: 'info', type: 'info', label: `"${doc.active.name}" 레이어의 같은 색 픽셀을 한꺼번에 바꿉니다.` },
  ], { okLabel: '바꾸기' });
  if (!v) return;
  const W = layer.canvas.width, H = layer.canvas.height;
  let n = 0;
  app.history.pixels(layer, '팔레트 색 바꾸기', (ctx) => {
    const img = ctx.getImageData(0, 0, W, H);
    n = P.replaceColor(img, P.parseHex(v.from), P.parseHex(v.to), +v.tol);
    ctx.putImageData(img, 0, 0);
    return { x: 0, y: 0, w: W, h: H };
  });
  if (v.pal) {
    const swap = (list) => list?.map(c => (c.toLowerCase() === v.from.toLowerCase() ? v.to : c));
    if (doc.pixel?.palette) doc.pixel.palette = swap(doc.pixel.palette);
    app.settings.palette = swap(app.settings.palette);
    palChanged(); app.saveSettingsSoon();
  }
  layer.touch(); app.requestRender();
  app.toast(`${n}개 픽셀의 색을 바꿨습니다`);
}

export function initPixel() {
  const C = (id, label, run) => app.registerCommand(id, { label, run });
  C('pixel.newDoc', '픽셀 아트 새로 만들기…', newPixelDoc);
  C('pixel.grid', '픽셀 격자 (많이 확대했을 때)', () => { app.settings.pixelGrid = !app.settings.pixelGrid; app.saveSettingsSoon(); app.requestRender(); app.toast(app.settings.pixelGrid ? '픽셀 격자: 켜짐 (6배 이상 확대하면 보임)' : '픽셀 격자: 꺼짐'); });
  C('pixel.crisp', '픽셀아트로 보기 (축소해도 또렷하게, 이 작품)', () => { const p = pixelOf(); p.art = !p.art; app.requestRender(); app.toast(p.art ? '픽셀아트 보기: 켜짐' : '픽셀아트 보기: 꺼짐'); });
  C('pixel.lock', '팔레트 색으로만 칠하기 (이 작품)', () => { const p = pixelOf(); p.lock = !p.lock; app.toast(p.lock ? '팔레트 고정: 브러시·채우기 색이 팔레트 색으로 맞춰지고 반투명 가장자리가 없어집니다' : '팔레트 고정: 꺼짐'); });
  C('pixel.quantize', '색 수 줄이기 (팔레트·디더링)…', quantizeDialog);
  C('pixel.paletteImport', '팔레트 가져오기 (.gpl/.hex)…', importPalette);
  C('pixel.paletteExport', '팔레트 내보내기 (.gpl/.hex)…', exportPalette);
  C('pixel.replaceColor', '팔레트 색 바꾸기 (같은 색 한꺼번에)…', replaceColorDialog);
  app.addMenuItem('file', { command: 'pixel.newDoc' });
  for (const id of ['pixel.grid', 'pixel.crisp']) app.addMenuItem('view', { command: id });
  for (const it of [{ sep: true }, { command: 'pixel.quantize' }, { command: 'pixel.replaceColor' }, { command: 'pixel.lock' }, { command: 'pixel.paletteImport' }, { command: 'pixel.paletteExport' }]) app.addMenuItem('filter', it);
}

/** 시험용 (자동 점검): 캔버스 위 칠한 픽셀 수 */
export function countInk(c) { const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data; let n = 0; for (let i = 3; i < d.length; i += 4) if (d[i]) n++; return n; }
export const _test = { createCanvas, h };
