// 웹툰 플랫폼 규격으로 내보내기: 가로폭 맞추기 → 세로로 나누기(되도록 빈 줄에서) → 장당 용량에 맞춰 JPG 압축
import { app } from '../app.js';
import { createCanvas, canvasToBlob, safeName, fileStamp, downloadBlob } from '../core/util.js';
import { server } from '../core/io.js';
import { form } from './dialogs.js';

// 플랫폼 규격은 바뀔 수 있음: 업로드 전에 각 플랫폼 공지로 확인 (아래는 흔히 쓰는 값)
export const WEBTOON_PRESETS = [
  { id: 'canvas', name: 'WEBTOON CANVAS (글로벌)', w: 800, h: 1280, fmt: 'jpg', maxMB: 2, totalMB: 20 },
  { id: 'naver', name: '네이버 도전·베스트도전 (흔히 쓰는 값)', w: 690, h: 1600, fmt: 'jpg', maxMB: 2, totalMB: 0 },
  { id: 'w720', name: '가로 720 (카카오 등 투고용으로 흔함)', w: 720, h: 1600, fmt: 'jpg', maxMB: 0, totalMB: 0 },
  { id: 'custom', name: '직접 입력', w: 800, h: 1280, fmt: 'jpg', maxMB: 0, totalMB: 0 },
];

/** cut 근처(±range)에서 가장 "빈" 줄(색 변화가 가장 적은 줄)을 찾는다: 컷 사이 여백에서 자르기 */
function bestCut(ctx, w, cut, range, maxY) {
  const y0 = Math.max(1, cut - range), y1 = Math.min(maxY - 1, cut);
  if (y1 <= y0) return cut;
  const d = ctx.getImageData(0, y0, w, y1 - y0 + 1).data;
  let best = cut, bestScore = Infinity;
  for (let y = y1; y >= y0; y--) {
    const o = (y - y0) * w * 4;
    let mn = 255, mx = 0;
    for (let x = 0; x < w; x += 2) { const v = (d[o + x * 4] + d[o + x * 4 + 1] + d[o + x * 4 + 2]) / 3; if (v < mn) mn = v; if (v > mx) mx = v; }
    const score = mx - mn + (cut - y) * 0.02;   // 같은 조건이면 원래 자리에 가깝게
    if (score < bestScore) { bestScore = score; best = y; if (score < 1) break; }
  }
  return bestScore < 8 ? best : cut;
}

async function encode(c, fmt, maxBytes) {
  if (fmt === 'png') return canvasToBlob(c, 'image/png');
  let q = 0.92, b = await canvasToBlob(c, 'image/jpeg', q);
  while (maxBytes && b.size > maxBytes && q > 0.4) { q -= 0.08; b = await canvasToBlob(c, 'image/jpeg', q); }
  return b;
}

export async function webtoonExportDialog() {
  const doc = app.doc;
  const last = app.settings.webtoonExport || {};
  const pre = WEBTOON_PRESETS.find(p => p.id === (last.preset || 'canvas'));
  let shown = pre.id;
  const v = await form('웹툰 플랫폼 규격으로 내보내기', [
    { key: 'preset', label: '규격', type: 'select', options: WEBTOON_PRESETS.map(p => [p.id, p.name]), value: pre.id },
    { key: 'w', label: '가로폭 (px)', type: 'number', value: last.w ?? pre.w },
    { key: 'h', label: '한 장 세로 (px)', type: 'number', value: last.h ?? pre.h },
    { key: 'fmt', label: '형식', type: 'segment', options: [['jpg', 'JPG'], ['png', 'PNG']], value: last.fmt ?? pre.fmt },
    { key: 'maxMB', label: '한 장 최대 용량 (MB, 0 = 제한 없음 · 넘으면 JPG 화질을 낮춤)', type: 'number', value: last.maxMB ?? pre.maxMB, step: 0.5 },
    { key: 'smart', label: '되도록 컷 사이 빈 곳에서 자르기', type: 'checkbox', value: last.smart ?? true },
    { key: 'info', type: 'info', label: '규격을 고르면 아래 값이 바뀝니다. 플랫폼 규격은 바뀔 수 있으니 올리기 전에 공지를 확인하세요. 밑그림·메모는 빠집니다.' },
  ], {
    okLabel: '내보내기',
    onChange: (vals, inputs) => {
      // 규격을 바꿨을 때만 값 채우기 (직접 고친 값은 그대로)
      if (vals.preset === shown) return;
      shown = vals.preset;
      const p = WEBTOON_PRESETS.find(x => x.id === vals.preset);
      if (p && p.id !== 'custom') { inputs.w.value = p.w; inputs.h.value = p.h; inputs.maxMB.value = p.maxMB; }
    },
  });
  if (!v) return;
  app.settings.webtoonExport = { preset: v.preset, w: v.w, h: v.h, fmt: v.fmt, maxMB: v.maxMB, smart: v.smart };
  app.saveSettingsSoon();
  const W = Math.max(100, Math.round(v.w)), H = Math.max(200, Math.round(v.h));
  const preset = WEBTOON_PRESETS.find(p => p.id === v.preset);
  const folder = `${safeName(doc.name)}_웹툰_${W}_${fileStamp()}`;
  const files = [];
  await app.task('웹툰 규격 내보내기', async () => {
    const scale = W / doc.width;
    const full = app.compositor.flatten(doc, { scale, paper: true, background: '#ffffff' });
    const fx = full.getContext('2d', { willReadFrequently: true });
    let y = 0, n = 0;
    while (y < full.height) {
      let cut = Math.min(full.height, y + H);
      if (v.smart && cut < full.height) cut = bestCut(fx, full.width, cut, Math.round(H * 0.2), full.height);
      const c = createCanvas(full.width, cut - y);
      c.getContext('2d').drawImage(full, 0, -y);
      const blob = await encode(c, v.fmt, v.maxMB > 0 ? v.maxMB * 1048576 : 0);
      const name = `${folder}/${String(++n).padStart(3, '0')}.${v.fmt}`;
      files.push(blob.size);
      if (server.online) await server.put('exports', name, blob); else downloadBlob(blob, name.split('/').pop());
      y = cut;
    }
  });
  const total = files.reduce((a, b) => a + b, 0) / 1048576;
  const over = v.maxMB > 0 && files.some(s => s > v.maxMB * 1048576);
  const totalWarn = preset?.totalMB && total > preset.totalMB ? ` · ⚠ 합계가 ${preset.totalMB}MB 를 넘습니다` : '';
  app.toast(`${files.length}장 (${total.toFixed(1)}MB)${over ? ' · ⚠ 화질을 낮춰도 용량을 넘는 장이 있습니다' : ''}${totalWarn}${server.online ? ` → ${server.where}/exports/${folder}` : ''}`, over || totalWarn ? 'warn' : 'info', { duration: 7000 });
}

export function initWebtoonExport() {
  app.registerCommand('file.webtoonExport', { label: '웹툰 플랫폼 규격으로 내보내기…', run: webtoonExportDialog });
  app.addMenuItem('file', { command: 'file.webtoonExport' });
}
