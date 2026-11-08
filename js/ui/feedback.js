// 피드백 파일 주고받기: 러프(의견) 레이어와 메모 핀만 작은 파일(.cpfb)로 보내고, 받은 파일을 내 원고 위에 얹어 본다
import { app } from '../app.js';
import { pickFile, safeName, fileStamp, downloadBlob } from '../core/util.js';
import { Layer } from '../core/doc.js';
import { addLayer } from '../core/ops.js';
import { server } from '../core/io.js';
import { form } from './dialogs.js';

export async function exportFeedback() {
  const doc = app.doc;
  const rough = doc.layers.filter(l => l.rough && !l.isFolder);
  const sel = doc.multi?.size ? doc.layers.filter(l => doc.multi.has(l.id) && !l.isFolder) : [];
  const v = await form('피드백 파일 보내기', [
    { key: 'name', label: '보내는 사람', type: 'text', value: app.settings.userName || '' },
    { key: 'what', label: '넣을 레이어', type: 'segment', options: [['rough', `러프 레이어 (${rough.length})`], ['sel', `고른 레이어 (${sel.length})`], ['none', '메모만']], value: rough.length ? 'rough' : sel.length ? 'sel' : 'none' },
    { key: 'notes', label: `메모 핀 넣기 (${doc.notes?.length || 0}개)`, type: 'checkbox', value: true },
    { key: 'info', type: 'info', label: '그림 전체가 아니라 의견만 담긴 작은 파일입니다. 받는 사람은 파일 > 피드백 파일 받기 (또는 창에 끌어다 놓기) 로 자기 원고 위에 얹어 봅니다.' },
  ], { okLabel: '만들기' });
  if (!v) return;
  if (v.name) { app.settings.userName = v.name; app.saveSettingsSoon(); }
  const layers = v.what === 'rough' ? rough : v.what === 'sel' ? sel : [];
  const json = {
    format: 'comicpad-feedback', version: 1, from: v.name || '', date: new Date().toISOString(),
    doc: { name: doc.name, width: doc.width, height: doc.height },
    notes: v.notes ? (doc.notes || []).map(n => ({ ...n, author: n.author || v.name || '' })) : [],
    layers: await Promise.all(layers.map(l => l.serialize())),
  };
  const blob = new Blob([JSON.stringify(json)], { type: 'application/json' });
  const name = `${safeName(doc.name)}_피드백_${safeName(v.name || '')}_${fileStamp()}.cpfb`.replace('__', '_');
  if (server.online) { await server.put('exports', name, blob); app.toast(`피드백 파일: ${server.where}/exports/${name} (이 파일을 메신저 등으로 보내세요)`, 'info', { duration: 6000 }); }
  else downloadBlob(blob, name);
}

export async function importFeedback(file) {
  file ||= await pickFile('.cpfb,application/json');
  if (!file) return;
  let fb;
  try { fb = JSON.parse(await file.text()); } catch { app.toast('피드백 파일을 읽지 못했습니다', 'error'); return; }
  if (fb.format !== 'comicpad-feedback') { app.toast('ComicPad 피드백 파일이 아닙니다', 'error'); return; }
  const doc = app.doc;
  const sx = doc.width / (fb.doc?.width || doc.width), sy = doc.height / (fb.doc?.height || doc.height);
  const same = Math.abs(sx - 1) < 0.001 && Math.abs(sy - 1) < 0.001;
  const who = fb.from || '이름 없음', when = new Date(fb.date).toLocaleString();
  // 그림을 먼저 다 읽어 두고 한 번에 넣는다 (실행취소 한 번으로 되돌릴 수 있게)
  const imgs = await Promise.all((fb.layers || []).map(ld => ld.data ? new Promise((res) => { const im = new Image(); im.onload = () => res(im); im.onerror = () => res(null); im.src = ld.data; }) : null));
  app.history.batch('피드백 받기', () => {
    if (fb.layers?.length) {
      const folder = addLayer({ name: `피드백: ${who} (${when})`, kind: 'folder', parent: null, expanded: true }, doc.layers.length);
      fb.layers.forEach((ld, i) => {
        const l = new Layer(doc, { ...ld, id: undefined, parent: folder.id, rough: true, locked: false });
        const img = imgs[i];
        if (img) l.ctx.drawImage(img, (ld.rect?.x || 0) * sx, (ld.rect?.y || 0) * sy, img.width * sx, img.height * sy);
        addLayer(l, doc.indexOf(folder));
      });
      doc.setActive(folder);
    }
    if (fb.notes?.length) {
      const before = structuredClone(doc.notes || []);
      const add = fb.notes.map(n => ({ ...n, id: 'n' + Math.random().toString(36).slice(2, 9), x: n.x * sx, y: n.y * sy, author: n.author || who }));
      const after = [...before, ...add];
      doc.notes = after;
      app.history.push({ label: '피드백 메모', undo: () => { doc.notes = structuredClone(before); app.requestRender(); }, redo: () => { doc.notes = structuredClone(after); app.requestRender(); } });
    }
  });
  app.markModified();
  app.requestRender();
  app.toast(`${who} 님의 피드백: 레이어 ${fb.layers?.length || 0}개, 메모 ${fb.notes?.length || 0}개${same ? '' : ' (원고 크기가 달라 맞춰 늘림)'}. 다 보면 폴더째 숨기거나 지우세요`, 'info', { duration: 7000 });
}

export function initFeedback() {
  app.registerCommand('feedback.export', { label: '피드백 파일 보내기 (러프·메모만)…', run: exportFeedback });
  app.registerCommand('feedback.import', { label: '피드백 파일 받기…', run: () => importFeedback() });
  for (const it of [{ sep: true }, { command: 'feedback.export' }, { command: 'feedback.import' }]) app.addMenuItem('file', it);
  addEventListener('drop', (e) => {
    const f = [...(e.dataTransfer?.files || [])].find(x => /\.cpfb$/i.test(x.name));
    if (f) { e.preventDefault(); e.stopImmediatePropagation(); importFeedback(f); }
  }, true);
}
