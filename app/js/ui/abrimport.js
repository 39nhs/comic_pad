// 포토샵 브러시(.abr) 가져오기 → 브러시 목록에 "이미지 팁" 브러시로 추가
import { app } from '../app.js';
import { pickFile, canvasToBlob, safeName } from '../core/util.js';
import { server } from '../core/io.js';
import { makePreset } from '../core/brush.js';
import { presets } from '../tools/brush.js';
import { readABR, sampleToCanvas } from '../core/abr.js';

/** 포토샵 브러시 설정 중 우리가 쓸 수 있는 것만 */
function abrSettings(st) {
  if (!st) return {};
  const o = {};
  for (const k of ['pressureSize', 'minSize', 'pressureOpacity', 'minOpacity', 'sizeJitter', 'rotJitter', 'followDir', 'scatter', 'hueJitter', 'satJitter', 'valJitter']) if (st[k] !== undefined) o[k] = st[k];
  if (st.size) o.size = Math.round(Math.min(600, Math.max(1, st.size)));
  if (st.spacing) o.spacing = Math.max(0.02, Math.min(2, st.spacing));
  return o;
}

export async function importABR(file) {
  file ||= await pickFile('.abr');
  if (!file) return;
  const base = safeName(file.name.replace(/\.abr$/i, ''));
  let samples;
  try { samples = readABR(await file.arrayBuffer()); } catch (e) { app.toast(`ABR 를 읽지 못했습니다: ${e.message}`, 'error'); return; }
  if (!samples.length) { app.toast('이 파일에는 가져올 수 있는 그림 브러시가 없습니다 (계산형 브러시만 있음)', 'warn'); return; }
  const list = presets('brush');
  const stamp = Date.now().toString(36);
  let n = 0;
  await app.task(`브러시 ${samples.length}개 가져오기`, async () => {
    for (const [i, s] of samples.slice(0, 200).entries()) {
      const c = sampleToCanvas(s);
      const path = `brushes/${base}/${String(i + 1).padStart(3, '0')}.png`;
      await server.put('assets', path, await canvasToBlob(c));
      list.push(makePreset({
        id: `abr_${stamp}_${i}`, name: `${s.name || base} ${samples.length > 1 ? '#' + (i + 1) : ''}`.trim(),
        tip: 'image', tipImage: path, tintTip: true,
        size: Math.round(Math.min(120, Math.max(8, Math.max(s.w, s.h) / 4))), spacing: Math.max(0.05, Math.min(1, s.spacing || 0.25)),
        hardness: 1, pressureSize: true, minSize: 0.2, pressureOpacity: false, opacity: 1, flow: 1, abr: base,
        ...abrSettings(s.settings),
      }));
      n++;
    }
  });
  app.saveSettingsSoon();
  app.bus.emit('brush:presets');
  app.bus.emit('assets:changed');
  app.toast(`브러시 ${n}개를 가져왔습니다 (브러시 목록 맨 아래 "${base}")${samples.length > 200 ? ' · 200개까지만' : ''}`);
}

export function initABR() {
  app.registerCommand('brush.importAbr', { label: '포토샵 브러시(.abr) 가져오기…', run: () => importABR() });
  app.addMenuItem('file', { command: 'brush.importAbr' });
  // 창에 .abr 을 끌어다 놓아도 가져오기
  addEventListener('drop', (e) => {
    const f = [...(e.dataTransfer?.files || [])].find(x => /\.abr$/i.test(x.name));
    if (f) { e.preventDefault(); e.stopImmediatePropagation(); importABR(f); }
  }, true);
}
