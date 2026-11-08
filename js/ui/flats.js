// 밑색 한 번에 칠하기 대화상자
import { app } from '../app.js';
import { form } from './dialogs.js';
import { addLayer } from '../core/ops.js';
import { flatLabels, colorize, colorizeHints } from '../core/autoflat.js';
import { referenceImage } from '../tools/basic.js';

function labelsAsync(img, opt) {
  return new Promise((resolve) => {
    let worker;
    const fallback = () => resolve(flatLabels(img.data, img.width, img.height, opt));
    try { worker = new Worker(new URL('../core/flatworker.js', import.meta.url), { type: 'module' }); } catch { fallback(); return; }
    worker.onmessage = (e) => { worker.terminate(); if (e.data.error) fallback(); else resolve(e.data); };
    worker.onerror = () => { worker.terminate(); fallback(); };
    const buf = img.data.buffer.slice(0);
    worker.postMessage({ buf, w: img.width, h: img.height, opt }, [buf]);
  });
}

export async function autoFlatDialog() {
  const doc = app.doc;
  const hasRef = doc.layers.some(l => l.ref);
  const v = await form('밑색 한 번에 칠하기', [
    { key: 'source', label: '선화로 볼 것', type: 'segment', options: [['ref', '참조 레이어'], ['layer', '지금 레이어'], ['all', '보이는 레이어 전체']], value: hasRef ? 'ref' : 'layer' },
    { key: 'mode', label: '색', type: 'segment', options: [['colors', '영역마다 다른 색'], ['one', '한 가지 색']], value: 'colors' },
    { key: 'color', label: '한 가지 색일 때', type: 'color', value: '#d9d9d9' },
    { key: 'gap', label: '틈 막기: 이 너비(px)까지 빈틈은 선이 이어진 것으로 (0 = 끔, 러프는 10~20)', type: 'number', value: 4, min: 0, max: 50 },
    { key: 'under', label: '선 아래로 번지게 (px, 선 굵기만큼)', type: 'number', value: Math.max(2, Math.round(doc.width / 800)), min: 0, max: 30 },
    { key: 'minArea', label: '이보다 작은 조각은 옆 색으로 (픽셀 수)', type: 'number', value: 40, min: 0 },
    { key: 'outside', label: '바깥(테두리에 닿는 곳)은 칠하지 않기', type: 'checkbox', value: true },
    { key: 'info', type: 'info', label: '선화 레이어 아래에 "밑색" 레이어가 새로 생깁니다. 이후 채우기 도구(지금 레이어, 허용 0)로 영역 색을 바꾸거나, 자동선택으로 한 영역씩 고를 수 있습니다.' },
  ], { okLabel: '칠하기' });
  if (!v) return;
  const line = doc.active;
  const img = referenceImage(v.source);
  const opt = { gap: v.gap, under: v.under, minArea: v.minArea, outside: v.outside };
  const res = await app.task('밑색 영역 나누는 중', () => labelsAsync(img, opt), { delay: 0 });
  if (!res.count) { app.toast('닫힌 영역을 찾지 못했습니다. 틈 닫기를 늘리거나 "바깥 제외"를 꺼 보세요', 'warn'); return; }
  const out = colorize(res.labels, res.count, doc.width, doc.height, v.mode, v.color);
  app.history.batch('밑색 칠하기', () => {
    const at = line ? doc.indexOf(line) : 0;
    const l = addLayer({ name: '밑색', parent: line ? doc.parentOf(line) : null }, at);
    l.ctx.putImageData(out, 0, 0);
    l.touch();
  });
  app.toast(`영역 ${res.count}개를 칠했습니다`);
}

// ---------------------------------------------------------------------
// 색 점으로 칠하기: "색 힌트" 레이어에 색을 콕콕 찍으면 선화 영역을 그 색으로 (크리타 색칠 마스크와 비슷, AI 아님)
// ---------------------------------------------------------------------
const hintLayerOf = (doc) => doc.layers.find(l => l.meta?.hint);

export function makeHintLayer() {
  const doc = app.doc;
  let h = hintLayerOf(doc);
  if (!h) {
    app.history.batch('색 힌트 레이어', () => {
      const line = doc.active;
      h = addLayer({ name: '색 힌트 (색 점 찍기)', meta: { hint: true }, opacity: 0.75, draft: true }, line ? doc.indexOf(line) + 1 : doc.layers.length);
    });
  }
  doc.setActive(h);
  app.setTool('brush');
  app.toast('"색 힌트" 레이어에 칠할 색으로 영역마다 점이나 짧은 선을 찍으세요. 다 찍으면 만화 > 색 점으로 칠하기', 'info', { duration: 8000 });
  app.bus.emit('layers:changed');
}

export async function hintFillDialog() {
  const doc = app.doc;
  const hint = hintLayerOf(doc);
  if (!hint) { makeHintLayer(); return; }
  const hasRef = doc.layers.some(l => l.ref);
  const lineGuess = doc.layers.find(l => !l.meta?.hint && !l.meta?.hintResult && !l.isFolder && l.kind !== 'adjust' && l.visible);
  const v = await form('색 점으로 칠하기', [
    { key: 'source', label: '선화로 볼 것', type: 'segment', options: [['ref', '참조 레이어'], ['all', '보이는 레이어 전체 (힌트·결과 제외)']], value: hasRef ? 'ref' : 'all' },
    { key: 'gap', label: '틈 막기: 이 너비(px)까지 빈틈은 선이 이어진 것으로 (0 = 끔, 러프는 10~20)', type: 'number', value: app.settings.hintGap ?? 4, min: 0, max: 50 },
    { key: 'under', label: '선 아래로 번지게 (px)', type: 'number', value: Math.max(2, Math.round(doc.width / 800)), min: 0, max: 30 },
    { key: 'spread', label: '점이 없는 영역은 가장 많이 맞닿은 이웃 색으로', type: 'checkbox', value: true },
    { key: 'hideHint', label: '칠한 뒤 힌트 레이어 숨기기', type: 'checkbox', value: true },
    { key: 'info', type: 'info', label: '힌트 레이어의 색 점을 보고, 선화로 막힌 영역마다 가장 많이 찍힌 색으로 칠합니다. 결과는 "밑색 (색 점)" 레이어(다시 하면 바뀜)이고, 점을 고쳐 다시 실행하면 됩니다.' },
  ], { okLabel: '칠하기' });
  if (!v) return;
  // 선화: 힌트·이전 결과 레이어는 빼고 합치기
  let img;
  if (v.source === 'ref') img = referenceImage('ref');
  else {
    const hidden = doc.layers.filter(l => (l.meta?.hint || l.meta?.hintResult) && l.visible);
    hidden.forEach(l => { l.visible = false; });
    try { img = app.compositor.flatten(doc, { paper: false }).getContext('2d').getImageData(0, 0, doc.width, doc.height); }
    finally { hidden.forEach(l => { l.visible = true; }); }
  }
  app.settings.hintGap = +v.gap; app.saveSettingsSoon();
  const opt = { gap: v.gap, under: v.under, minArea: 20, outside: false };
  const res = await app.task('영역 나누는 중', () => labelsAsync(img, opt), { delay: 0 });
  if (!res?.count) { app.toast('선화로 막힌 영역을 찾지 못했습니다', 'warn'); return; }
  const hd = hint.ctx.getImageData(0, 0, doc.width, doc.height).data;
  const out = colorizeHints(res.labels, res.count, res.edge, hd, doc.width, doc.height, { spread: v.spread });
  if (!out.hinted) { app.toast('힌트 레이어에 찍힌 색이 없습니다', 'warn'); return; }
  app.history.batch('색 점으로 칠하기', () => {
    let r = doc.layers.find(l => l.meta?.hintResult);
    if (!r) {
      const below = lineGuess && !lineGuess.meta?.hint ? doc.indexOf(lineGuess) : 0;
      r = addLayer({ name: '밑색 (색 점)', meta: { hintResult: true } }, Math.max(0, below));
    }
    app.history.pixels(r, '색 점으로 칠하기', (ctx) => { ctx.clearRect(0, 0, doc.width, doc.height); ctx.putImageData(out.img, 0, 0); return { x: 0, y: 0, w: doc.width, h: doc.height }; });
    if (v.hideHint) hint.visible = false;
  });
  app.bus.emit('layers:changed'); app.requestRender();
  app.toast(`색 점 ${out.hinted}곳 → 영역 ${out.filled}개를 칠했습니다`);
}

export function initFlats() {
  app.registerCommand('color.autoFlat', { label: '밑색 한 번에 칠하기 (선화 영역마다)…', run: autoFlatDialog });
  app.registerCommand('color.hintLayer', { label: '색 힌트 레이어 만들기 (색 점 찍기)', run: makeHintLayer });
  app.registerCommand('color.hintFill', { label: '색 점으로 칠하기 (힌트대로 밑색)…', run: hintFillDialog });
  for (const it of [{ sep: true }, { command: 'color.autoFlat' }, { command: 'color.hintLayer' }, { command: 'color.hintFill' }]) app.addMenuItem('comic', it);
}
