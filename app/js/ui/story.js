// 여러 페이지 작품 관리: data/projects/<작품>/p001.cpad, p002.cpad ...
import { app } from '../app.js';
import { Doc } from '../core/doc.js';
import { server, saveProject, openProject, setImgSrc } from '../core/io.js';
import { h, safeName, canvasToBlob } from '../core/util.js';
import { form, confirmDialog } from './dialogs.js';
import { writePSD } from '../core/psd.js';
import { SIZE_PRESETS } from './files.js';
import { icon } from './icons.js';
import { button, row } from './controls.js';

export const storyOf = (file) => (file && file.includes('/') ? file.split('/')[0] : null);

export async function listPages(story) {
  const all = await server.list('projects');
  const pages = all.filter(i => i.name.startsWith(story + '/') && i.name.endsWith('.cpad')).sort((a, b) => a.name.localeCompare(b.name));
  // 작품 정보(story.json)에 순서가 있으면 그 순서로 (페이지를 끌어서 바꾼 순서)
  const order = (await readStoryInfo(story)).order || [];
  const at = (n) => { const i = order.indexOf(n); return i < 0 ? 1e6 : i; };
  return pages.sort((a, b) => at(a.name) - at(b.name) || a.name.localeCompare(b.name));
}

/** 작품 정보: { order: [파일], status: {파일: conti|line|color|done}, time: {파일: 초}, deadline: 'YYYY-MM-DD' } */
export async function readStoryInfo(story) {
  try { return await (await server.get('projects', `${story}/story.json`)).json(); } catch { return {}; }
}
export async function writeStoryInfo(story, info) {
  await server.put('projects', `${story}/story.json`, JSON.stringify(info, null, 1));
  app.bus.emit('story:changed');
}

/** 페이지 순서 바꾸기: from 번째를 to 번째로 */
export async function movePage(story, from, to) {
  const pages = await listPages(story);
  if (to < 0 || to >= pages.length || from === to) return;
  const names = pages.map(p => p.name);
  const [x] = names.splice(from, 1);
  names.splice(to, 0, x);
  const info = await readStoryInfo(story);
  info.order = names;
  await writeStoryInfo(story, info);
}

function pageName(n) { return 'p' + String(n).padStart(3, '0'); }

/** 빈 페이지 문서 (컷 틀 포함 가능) */
function blankPage(w, hgt, name, withFrame) {
  const doc = new Doc(w, hgt, { name });
  const l = doc.createLayer({ name: '레이어 1' });
  doc.layers.push(l);
  if (withFrame) {
    const m = Math.round(w * 0.07);
    const line = Math.max(3, Math.round(w / 400));
    const p = doc.createLayer({ name: '컷', kind: 'panel', meta: { panels: [{ pts: [{ x: m, y: m }, { x: w - m, y: m }, { x: w - m, y: hgt - m }, { x: m, y: hgt - m }] }], line, color: '#000000', maskOutside: true } });
    // 컷 테두리 그림은 처음 열 때 그려지도록 직접 그림
    const ctx = p.ctx;
    ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, w, hgt); ctx.clearRect(m, m, w - 2 * m, hgt - 2 * m);
    ctx.strokeStyle = '#000'; ctx.lineWidth = line; ctx.strokeRect(m + line / 2, m + line / 2, w - 2 * m - line, hgt - 2 * m - line);
    doc.layers.push(p);
  }
  doc.activeId = l.id;
  return doc;
}

async function savePageDoc(doc, file) {
  await server.put('projects', file, JSON.stringify(await doc.serialize()));
  const s = Math.min(1, 320 / Math.max(doc.width, doc.height));
  await server.put('projects', file.replace(/\.cpad$/, '.png'), await canvasToBlob(app.compositor.flatten(doc, { scale: s })));
}

export async function newStoryDialog() {
  const v = await form('새 작품 (여러 페이지)', [
    { key: 'name', label: '작품 이름', value: '새 작품' },
    { key: 'pages', label: '페이지 수', type: 'number', value: 8, min: 1, max: 200 },
    { key: 'size', label: '크기', type: 'select', options: SIZE_PRESETS.filter(p => !p.transparent).map((p, i) => [String(i), `${p.name} (${p.w}×${p.h})`]), value: '2' },
    { key: 'frame', label: '기본 컷 틀 넣기', type: 'checkbox', value: true },
    { key: 'info', type: 'info', label: 'data/projects/작품이름/ 폴더에 p001, p002… 로 만들어집니다.' },
  ], { okLabel: '만들기' });
  if (!v) return;
  const story = safeName(v.name);
  const preset = SIZE_PRESETS.filter(p => !p.transparent)[+v.size];
  const n = Math.max(1, Math.min(200, Math.round(v.pages)));
  await app.task('작품 만들기', async () => {
    for (let i = 1; i <= n; i++) {
      const doc = blankPage(preset.w, preset.h, `${story} ${i}쪽`, v.frame);
      await savePageDoc(doc, `${story}/${pageName(i)}.cpad`);
    }
  });
  await openProject(`${story}/${pageName(1)}.cpad`);
  app.bus.emit('story:changed');
}

export async function gotoPage(file) {
  const doc = app.doc;
  if (doc?.modified && doc.file) await saveProject();
  else if (doc?.modified && !(await confirmDialog('현재 그림이 저장되지 않았습니다. 버리고 이동할까요?', '이동'))) return;
  await openProject(file);
  app.bus.emit('story:changed');
}

export async function stepPage(dir) {
  const story = storyOf(app.doc?.file);
  if (!story) { app.toast('여러 페이지 작품이 아닙니다 (파일 > 새 작품)'); return; }
  const pages = await listPages(story);
  const i = pages.findIndex(p => p.name === app.doc.file);
  const t = pages[i + dir];
  if (t) await gotoPage(t.name); else app.toast(dir > 0 ? '마지막 페이지입니다' : '첫 페이지입니다');
}

export async function addPage(dup = false) {
  const doc = app.doc, story = storyOf(doc?.file);
  if (!story) { app.toast('여러 페이지 작품을 먼저 여세요'); return; }
  if (doc.modified) await saveProject();
  const pages = await listPages(story);
  const nums = pages.map(p => parseInt(p.name.match(/p(\d+)\.cpad$/)?.[1] || '0', 10));
  const file = `${story}/${pageName(Math.max(0, ...nums) + 1)}.cpad`;
  await app.task('페이지 추가', async () => {
    const nd = dup ? await Doc.deserialize(await doc.serialize()) : blankPage(doc.width, doc.height, `${story}`, !!doc.layers.find(l => l.kind === 'panel'));
    await savePageDoc(nd, file);
  });
  await openProject(file);
  app.bus.emit('story:changed');
}

export async function exportStory() {
  const story = storyOf(app.doc?.file);
  if (!story) { app.toast('여러 페이지 작품을 먼저 여세요'); return; }
  const v = await form('작품 전체 내보내기', [
    { key: 'fmt', label: '형식', type: 'segment', options: [['png', 'PNG'], ['jpg', 'JPG'], ['psd', 'PSD']], value: 'png' },
    { key: 'scale', label: '배율', type: 'segment', options: [['0.5', '50%'], ['1', '100%']], value: '1' },
  ], { okLabel: '내보내기' });
  if (!v) return;
  if (app.doc.modified) await saveProject();
  const pages = await listPages(story);
  await app.task(`${pages.length}쪽 내보내기`, async () => {
    for (const [i, p] of pages.entries()) {
      const r = await server.get('projects', p.name);
      const doc = await Doc.deserialize(await r.json());
      const base = `${story}_내보내기/${String(i + 1).padStart(3, '0')}`;
      if (v.fmt === 'psd') await server.put('exports', base + '.psd', writePSD(doc, app.compositor.flatten(doc, { background: '#fff' })));
      else {
        const c = app.compositor.flatten(doc, { scale: +v.scale, background: '#ffffff' });
        await server.put('exports', `${base}.${v.fmt}`, await canvasToBlob(c, v.fmt === 'jpg' ? 'image/jpeg' : 'image/png', 0.92));
      }
    }
  });
  app.toast(`data/exports/${story}_내보내기 에 저장했습니다`);
}

export function pagesPanel(el) {
  const body = h('div.panel-scroll');
  el.append(body);
  const render = async () => {
    body.innerHTML = '';
    const story = storyOf(app.doc?.file);
    if (!story) {
      body.append(h('div.help', null, '여러 페이지로 된 만화를 한 작품으로 관리합니다. 페이지를 넘기면 자동 저장됩니다.'),
        row(button('새 작품 만들기', () => newStoryDialog(), 'primary')));
      return;
    }
    const pages = await listPages(story);
    body.append(h('div.section-title', null, `${story} — ${pages.length}쪽`),
      row(button(icon('prev', 16) + ' 이전', () => stepPage(-1)), button('다음 ' + icon('next', 16), () => stepPage(1)),
        button('+ 빈 페이지', () => addPage(false)), button('+ 복제', () => addPage(true))),
      row(button('전체 내보내기', () => exportStory()), button('폴더 열기', () => server.openFolder('projects'))));
    const info = await readStoryInfo(story);
    const cur = pages.findIndex(p => p.name === app.doc.file);
    body.append(row(
      button('◀ 이 쪽을 앞으로', () => movePage(story, cur, cur - 1)),
      button('이 쪽을 뒤로 ▶', () => movePage(story, cur, cur + 1)),
      button('진행표…', () => app.run('progress.open'))));
    const grid = h('div.page-grid');
    const ST = { conti: ['콘티', '#8e5bd6'], line: ['선화', '#3d7be0'], color: ['채색', '#f08c2e'], done: ['완료', '#2fa35b'] };
    pages.forEach((p, i) => {
      const st = ST[info.status?.[p.name]];
      const card = h(`button.page-card${p.name === app.doc.file ? '.on' : ''}`, { type: 'button', draggable: 'true', onclick: () => p.name !== app.doc.file && gotoPage(p.name) },
        setImgSrc(h('img', { onerror: (e) => { e.target.style.visibility = 'hidden'; } }), 'projects', p.name.replace(/\.cpad$/, '.png'), p.mtime),
        h('span', null, `${i + 1}`),
        st ? h('i.page-st', { style: { background: st[1] } }, st[0]) : null);
      // 끌어서 다른 카드 위에 놓으면 그 자리로
      card.addEventListener('dragstart', (e) => { e.dataTransfer.setData('text/x-page', String(i)); card.classList.add('dragging'); });
      card.addEventListener('dragend', () => card.classList.remove('dragging'));
      card.addEventListener('dragover', (e) => { e.preventDefault(); card.classList.add('drop'); });
      card.addEventListener('dragleave', () => card.classList.remove('drop'));
      card.addEventListener('drop', (e) => { e.preventDefault(); card.classList.remove('drop'); const from = +e.dataTransfer.getData('text/x-page'); if (!isNaN(from)) movePage(story, from, i); });
      grid.append(card);
    });
    body.append(grid, h('div.help', null, '페이지 카드를 끌어다 다른 카드 위에 놓으면 순서가 바뀝니다 (펜·터치는 위 버튼으로).'));
  };
  app.bus.on('story:changed', render);
  app.bus.on('doc:changed', render);
  app.bus.on('doc:saved', render);
  render();
}

export function registerStoryCommands() {
  const C = (id, label, run, key) => app.registerCommand(id, { label, run, key });
  C('story.new', '새 작품 (여러 페이지)…', () => newStoryDialog());
  C('story.next', '다음 페이지', () => stepPage(1), 'pagedown');
  C('story.prev', '이전 페이지', () => stepPage(-1), 'pageup');
  C('story.add', '페이지 추가', () => addPage(false));
  C('story.dup', '현재 페이지 복제', () => addPage(true));
  C('story.export', '작품 전체 내보내기…', () => exportStory());
  app.addMenuItem('comic', { sep: true });
  for (const id of ['story.new', 'story.prev', 'story.next', 'story.add', 'story.dup', 'story.export']) app.addMenuItem('comic', { command: id });
}
