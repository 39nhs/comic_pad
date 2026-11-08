// 새 문서 / 열기 / 내보내기 / 자동저장 복구 대화상자
import { app } from '../app.js';
import { newDocument, placeImage } from '../core/ops.js';
import { server, openProject, openProjectFile, exportImage, saveProject, setImgSrc } from '../core/io.js';
import { h, pickFile, blobToImage } from '../core/util.js';
import { readPSD } from '../core/psdread.js';
import { Doc } from '../core/doc.js';
import { setDoc } from '../core/ops.js';
import { openModal, form, confirmDialog } from './dialogs.js';

export const SIZE_PRESETS = [
  { name: '웹툰 (세로 스크롤)', w: 800, h: 1280, note: '800×1280, 아래로 늘려 쓰기' },
  { name: '웹툰 긴 캔버스', w: 800, h: 6400, note: '800×6400' },
  { name: '만화 원고 A4 300dpi', w: 2480, h: 3508, note: '흑백/컬러 원고' },
  { name: '만화 원고 B5 350dpi', w: 2508, h: 3541, note: '' },
  { name: '일러스트 FHD', w: 1920, h: 1080, note: '가로' },
  { name: '정사각 2048', w: 2048, h: 2048, note: 'SNS/표지' },
  { name: '에셋 (투명) 1024', w: 1024, h: 1024, note: '배경 투명', transparent: true },
  { name: '에셋 (투명) 2048', w: 2048, h: 2048, note: '배경 투명', transparent: true },
  { name: '러프/아이디어 스케치', w: 1600, h: 1200, note: '러프 레이어 포함', rough: true },
];

async function guardUnsaved() {
  if (!app.doc?.modified) return true;
  return confirmDialog('저장하지 않은 변경사항이 있습니다. 계속할까요?', '계속');
}

export function createFromPreset(p) {
  const doc = newDocument(p.w, p.h, { name: p.name.replace(/^추천:\s*/, '').replace(/\s*\(.*\)/, ''), paperVisible: !p.transparent });
  if (p.rough) {
    const l = doc.layers[0];
    l.name = '러프 (파랑)';
    l.rough = true;
    l.tint = '#3d7be0';
    l.opacity = 0.6;
    l.draft = true;
    app.bus.emit('layers:changed');
  }
  // 인쇄 원고: 해상도와 재단선·도련·안전선 가이드 (세팅 프리셋의 추천 원고)
  if (p.dpi) doc.dpi = p.dpi;
  if (p.print) {
    const d = p.dpi || doc.dpi || 350, mm = (v) => Math.round(v / 25.4 * d);
    doc.print = { size: p.print.size, mmW: p.print.mmW, mmH: p.print.mmH, bleedMm: p.print.bleedMm, safeMm: p.print.safeMm,
      trimW: mm(p.print.mmW), trimH: mm(p.print.mmH), bleed: mm(p.print.bleedMm), safe: mm(p.print.safeMm) };
    app.requestRender();
  }
  app.settings.lastNew = { w: p.w, h: p.h, transparent: !!p.transparent };
  app.saveSettingsSoon();
}

export async function newDocDialog({ startup = false } = {}) {
  if (!startup && !(await guardUnsaved())) return;
  if (startup && app.setupFirstRun) await app.setupFirstRun();      // 처음 설치: 세팅 프리셋 먼저 고르기
  const list = h('div.preset-grid');
  let modal;
  const rec = app.settings.startDoc;
  for (const p of rec ? [{ ...rec, name: '추천: ' + rec.name }, ...SIZE_PRESETS] : SIZE_PRESETS) {
    const ratio = p.w / p.h;
    const bw = ratio > 1 ? 44 : 44 * ratio, bh = ratio > 1 ? 44 / ratio : 44;
    list.append(h('button.size-card', {
      type: 'button',
      onclick: () => { createFromPreset(p); modal.close(true); },
    }, h('span.size-thumb', null, h('i', { style: { width: Math.max(6, bw) + 'px', height: Math.max(6, bh) + 'px', background: p.transparent ? 'repeating-conic-gradient(#ccc 0 25%, #fff 0 50%) 0 0/8px 8px' : '#fff' } })),
    h('b', null, p.name), h('small', null, `${p.w}×${p.h}${p.dpi ? ' · ' + p.dpi + 'dpi' : ''}${p.note ? ' · ' + p.note : ''}`)));
  }
  const custom = h('div.row', null,
    h('button.btn', {
      type: 'button',
      onclick: async () => {
        modal.close(true);
        const v = await form('직접 지정', [
          { key: 'name', label: '이름', value: '새 작품' },
          { key: 'w', label: '가로(px)', type: 'number', value: app.settings.lastNew?.w || 2000, min: 16, max: 16000 },
          { key: 'h', label: '세로(px)', type: 'number', value: app.settings.lastNew?.h || 2000, min: 16, max: 30000 },
          { key: 'transparent', label: '배경 투명', type: 'checkbox', value: false },
        ], { okLabel: '만들기' });
        if (v) createFromPreset({ name: v.name, w: Math.round(v.w), h: Math.round(v.h), transparent: v.transparent });
      },
    }, '크기 직접 지정…'),
    h('button.btn', { type: 'button', onclick: () => { modal.close(true); openDialog(); } }, '프로젝트 열기…'),
  );
  const recent = h('div.recent');
  server.list('projects').then(items => {
    const projs = items.filter(i => i.name.endsWith('.cpad')).slice(0, 6);
    if (!projs.length) return;
    recent.append(h('div.section-title', null, '최근 프로젝트'));
    const grid = h('div.project-grid');
    for (const p of projs) grid.append(projectCard(p, () => modal.close(true)));
    recent.append(grid);
  }).catch(() => {});
  modal = openModal({
    title: startup ? 'ComicPad 시작' : '새 캔버스',
    body: h('div', null, recent, h('div.section-title', null, '새로 만들기'), list, custom),
    wide: true,
  });
}

function projectCard(p, onOpen) {
  const base = p.name.replace(/\.cpad$/, '');
  const d = new Date(p.mtime * 1000);
  return h('button.project-card', {
    type: 'button',
    onclick: async () => {
      onOpen?.();
      try { await openProject(p.name); } catch (e) { app.toast(e.message, 'error'); }
    },
  },
  setImgSrc(h('img', { onerror: (e) => { e.target.style.visibility = 'hidden'; } }), 'projects', base + '.png', p.mtime),
  h('b', null, base),
  h('small', null, `${d.toLocaleDateString()} ${d.toLocaleTimeString().slice(0, -3)} · ${(p.size / 1048576).toFixed(1)}MB`));
}

export async function openDialog() {
  if (!(await guardUnsaved())) return;
  let modal;
  const grid = h('div.project-grid', null, h('div.help', null, '불러오는 중…'));
  const body = h('div', null,
    h('div.row', null,
      h('button.btn', { type: 'button', onclick: async () => { modal.close(true); await openLocalFile(); } }, '다른 위치의 파일 열기…'),
      h('button.btn', { type: 'button', onclick: () => server.openFolder('projects') }, '프로젝트 폴더 열기'),
      h('button.btn', { type: 'button', onclick: () => { modal.close(true); recoverDialog(); } }, '자동 저장 복구…'),
    ),
    grid);
  modal = openModal({ title: '프로젝트 열기 (data/projects)', body, wide: true });
  try {
    const items = (await server.list('projects')).filter(i => i.name.endsWith('.cpad'));
    grid.innerHTML = '';
    if (!items.length) grid.append(h('div.help', null, '저장된 프로젝트가 없습니다.'));
    for (const p of items) grid.append(projectCard(p, () => modal.close(true)));
  } catch (e) {
    grid.innerHTML = '';
    grid.append(h('div.help', null, '서버에 연결할 수 없습니다: ' + e.message));
  }
}

/** 버전 기록: 저장할 때마다 이전 판이 data/versions 에 보관됨 */
export async function versionsDialog() {
  const doc = app.doc;
  if (!doc.file) { app.toast('먼저 저장해야 버전이 쌓입니다'); return; }
  const base = doc.file.replace(/\.cpad$/, '') + '/';
  const items = (await server.list('versions')).filter(i => i.name.startsWith(base)).sort((a, b) => b.name.localeCompare(a.name));
  if (!items.length) { app.toast('아직 이전 버전이 없습니다 (두 번째 저장부터 쌓입니다)'); return; }
  let modal;
  const list = h('div.local-list');
  for (const it of items) {
    const stamp = it.name.slice(base.length).replace('.cpad', '');
    const when = `${stamp.slice(0, 4)}-${stamp.slice(4, 6)}-${stamp.slice(6, 8)} ${stamp.slice(9, 11)}:${stamp.slice(11, 13)}:${stamp.slice(13, 15)}`;
    list.append(h('div.local-row', null,
      h('div', null, h('b', null, when), h('small', null, ` · ${(it.size / 1048576).toFixed(1)}MB`)),
      h('div.row', null,
        h('button.btn', { type: 'button', onclick: async () => {
          const d2 = await Doc.deserialize(await (await server.get('versions', it.name)).json());
          (await import('./compare.js')).compareDocs(d2, doc, when, '지금');
        } }, '비교'),
        h('button.btn', { type: 'button', onclick: async () => {
          modal.close(true);
          const d2 = await Doc.deserialize(await (await server.get('versions', it.name)).json());
          d2.file = null; d2.name = `${doc.name} (${when})`;
          setDoc(d2); d2.modified = true;
          app.toast('이전 버전을 따로 열었습니다 (저장하면 새 이름으로)');
        } }, '따로 열기'),
        h('button.btn.primary', { type: 'button', onclick: async () => {
          if (!(await confirmDialog(`${when} 버전으로 되돌릴까요? 지금 상태는 저장하면 버전 기록에 남습니다.`, '되돌리기'))) return;
          modal.close(true);
          const file = doc.file;
          const d2 = await Doc.deserialize(await (await server.get('versions', it.name)).json());
          d2.file = file;
          setDoc(d2); d2.modified = true;
          app.bus.emit('doc:modified', true);
          app.toast('되돌렸습니다. 저장하면 이 상태가 작품 파일이 됩니다');
        } }, '이 버전으로 되돌리기'))));
  }
  modal = openModal({ title: `버전 기록: ${doc.name}`, body: h('div', null, h('div.help', null, '저장할 때마다 바로 전 판이 보관됩니다 (작품마다 최근 30개, data/versions 폴더).'), list), wide: true });
}

export async function recoverDialog() {
  const items = (await server.list('autosave')).filter(i => i.name.endsWith('.cpad'));
  if (!items.length) { app.toast('자동 저장 파일이 없습니다'); return; }
  const v = await form('자동 저장 복구', [
    { key: 'name', label: '파일', type: 'select', options: items.map(i => [i.name, `${i.name} (${new Date(i.mtime * 1000).toLocaleString()})`]), value: items[0].name },
  ], { okLabel: '열기' });
  if (v) await openProject(v.name, 'autosave');
}

export async function openPSD(f) {
  app.toast('PSD 여는 중…');
  const doc = await readPSD(await f.arrayBuffer(), f.name.replace(/\.psd$/i, ''));
  setDoc(doc);
  const info = doc.importInfo || {};
  app.toast(`PSD 열기: 레이어 ${doc.layers.length}개 (${info.mode || ''} ${info.depth || 8}비트${info.psb ? ', PSB' : ''})`);
  for (const n of info.notes || []) app.toast(n, 'warn');
}

/** 파일 하나 열기 (.cpad / .psd / 이미지) — 열기 버튼, 끌어다 놓기 공용 */
export async function openAnyFile(f, { asLayer = false } = {}) {
  if (/\.psd$/i.test(f.name)) { await openPSD(f); return; }
  if (/\.(aseprite|ase)$/i.test(f.name)) { const { importAseprite } = await import('./anim.js'); await importAseprite(f); return; }
  if (/\.(cpad|json)$/i.test(f.name)) { await openProjectFile(f); return; }
  if (asLayer && app.doc) { placeImage(await blobToImage(f), f.name.replace(/\.[^.]+$/, '')); return; }
  const img = await blobToImage(f);
  const doc = newDocument(img.width, img.height, { name: f.name.replace(/\.[^.]+$/, '') });
  doc.layers[0].ctx.drawImage(img, 0, 0);
  doc.layers[0].name = f.name;
  doc.layers[0].touch();
  doc.modified = false;
}

export async function openLocalFile() {
  const f = await pickFile('.cpad,.json,.psd,.aseprite,.ase,image/*');
  if (!f) return;
  if (/\.(cpad|json|psd)$/i.test(f.name)) { await openAnyFile(f); return; }
  const img = await blobToImage(f);
  const doc = newDocument(img.width, img.height, { name: f.name.replace(/\.[^.]+$/, '') });
  doc.layers[0].ctx.drawImage(img, 0, 0);
  doc.layers[0].name = f.name;
  doc.layers[0].touch();
  doc.modified = false;
}

export async function importImageDialog() {
  const f = await pickFile('image/*');
  if (!f) return;
  placeImage(await blobToImage(f), f.name.replace(/\.[^.]+$/, ''));
}

export async function saveAsDialog() {
  const v = await form('다른 이름으로 저장', [
    { key: 'name', label: '이름', value: app.doc.name },
    { key: 'info', type: 'info', label: `저장 위치: ${server.root ? server.root + '\\' : ''}data\\projects` },
  ], { okLabel: '저장' });
  if (v?.name) await saveProject(v.name);
}

/** 저장 위치: PC 서버 / 폴더 직접 (Python 없이) / 이 기기 */
export async function storageDialog() {
  const S = await import('../core/storage.js');
  let modal;
  const body = h('div.help-text');
  const render = () => {
    body.innerHTML = '';
    const now = { http: 'PC 서버 (G: data 폴더, 휴대폰 연결 가능)', folder: `폴더에 직접 저장: ${S.folderStore.root?.name || ''} (Python 서버 없이)`, device: '이 기기(브라우저) 안' }[server.mode];
    body.append(
      h('p', null, h('b', null, '지금 저장 위치: '), now),
      h('p', null, '저장 위치는 자동으로 골라집니다: ① PC 서버가 켜져 있으면 서버 ② 서버가 없으면 아래에서 허락한 폴더 ③ 둘 다 없으면 이 기기 안.'),
    );
    if (S.folderSupported()) {
      const st = S.folderStore.state;
      body.append(
        h('p', null, h('b', null, '서버 없이 쓰는 폴더: '), st === 'none' ? '아직 고르지 않음' : `${S.folderStore.root?.name}${st === 'granted' ? ' (허락됨)' : ' (다시 허락 필요)'}`),
        h('div.row', null,
          h('button.btn.primary', { type: 'button', onclick: async () => {
            try {
              const dir = await S.folderStore.choose();
              if (!(await S.folderStore.looksRight(dir)) && !(await confirmDialog(`"${dir.name}" 폴더에는 data 폴더가 없습니다. 여기에 새로 만들까요?\n(보통은 G:\\claude\\comicpad 폴더를 고르면 됩니다)`, '여기에 만들기'))) { await S.folderStore.forget(); render(); return; }
              await server.chooseMode();
              app.toast(server.mode === 'folder' ? `이제 ${dir.name} 폴더에 직접 저장합니다` : `${dir.name} 폴더를 기억했습니다. PC 서버가 꺼져 있을 때 이 폴더를 씁니다`);
            } catch (e) { if (e.name !== 'AbortError') app.toast(e.message, 'error'); }
            render();
          } }, st === 'none' ? 'ComicPad 폴더 고르기…' : '다른 폴더 고르기…'),
          st === 'prompt' ? h('button.btn', { type: 'button', onclick: async () => { await S.folderStore.reconnect(); await server.chooseMode(); render(); } }, '다시 허락하기') : null,
          st !== 'none' ? h('button.btn', { type: 'button', onclick: async () => { await S.folderStore.forget(); await server.chooseMode(); render(); } }, '폴더 잊기') : null),
        h('p', null, h('small', null, 'G:\\claude\\comicpad 폴더를 고르세요. 서버 모드와 같은 data 폴더를 쓰므로 어느 쪽으로 저장해도 같은 파일입니다. 폴더 열기·휴대폰 연결은 서버 모드에서만 됩니다.')),
      );
    } else {
      body.append(h('p', null, '이 브라우저는 폴더에 직접 저장할 수 없습니다 (휴대폰·태블릿, Safari 등). 작업은 이 기기 안에 저장되고, 파일 > 기기 임시 저장함에서 다운로드하거나 PC 에 보낼 수 있습니다.'));
    }
    body.append(driveBox);
    renderDrive();
    body.append(h('p', null, h('small', null, '서버가 꺼져 있어도 앱이 열리게 하려면 도움말 > "앱으로 설치"로 한 번 설치해 두세요.')));
  };

  // ---- 구글 드라이브 (Google Drive 데스크톱 앱의 '내 드라이브' 폴더로 사본) ----
  const driveBox = h('div.drive-box');
  const ago = (t) => (t ? `${Math.max(0, Math.round((Date.now() - t) / 1000))}초 전` : '아직 없음');
  const add = (...xs) => driveBox.append(...xs.filter(Boolean));
  const renderDrive = async () => {
    driveBox.innerHTML = '';
    add(h('h4', null, '구글 드라이브에도 사본 저장'));
    if (server.mode === 'http') {
      let st;
      try { st = await (await fetch('/api/drive', { cache: 'no-store' })).json(); } catch { add(h('p', null, '상태를 읽지 못했습니다')); return; }
      if (!st.detected && !st.path) {
        add(h('p', null, '이 PC 에서 Google Drive 데스크톱 앱(내 드라이브 폴더)을 찾지 못했습니다. 설치하고 로그인하면 여기서 켤 수 있습니다.'));
        return;
      }
      const set = async (on) => {
        const r = await fetch(`/api/drive/config?on=${on ? 1 : 0}`, { method: 'POST' });
        if (!r.ok) { app.toast((await r.json()).error || '바꾸지 못했습니다', 'error'); return; }
        app.toast(on ? '켰습니다. 기존 작품도 드라이브로 복사하는 중입니다' : '드라이브 사본 저장을 껐습니다');
        renderDrive();
      };
      add(
        h('p', null, h('b', null, st.on ? '켜짐' : '꺼짐'), ` · ${st.path}`),
        st.on ? h('p', null, h('small', null, `복사 대기 ${st.pending}개 · 이번 실행에서 ${st.copied}개 복사 · 마지막 복사 ${st.lastOk ? ago(st.lastOk * 1000) : '아직 없음'}`)) : null,
        st.lastError ? h('p.warn-text', null, '⚠ ' + st.lastError) : null,
        h('div.row', null,
          server.remote ? null : h(`button.btn${st.on ? '' : '.primary'}`, { type: 'button', onclick: () => set(!st.on) }, st.on ? '끄기' : '켜기'),
          st.on ? h('button.btn', { type: 'button', onclick: async () => { const j = await (await fetch('/api/drive/sync', { method: 'POST' })).json(); app.toast(`${j.queued}개를 드라이브로 복사합니다`); setTimeout(renderDrive, 1500); } }, '지금 전부 맞추기') : null,
          h('button.btn', { type: 'button', onclick: () => driveImportDialog() }, '드라이브에서 가져오기…')),
        h('p', null, h('small', null, '작업은 계속 G: data 에 저장되고, 저장할 때마다 작품·에셋·내보낸 그림·글꼴이 드라이브에도 복사됩니다 (휴대폰에서 저장한 것도 PC 를 거쳐 복사). 지운 파일은 드라이브의 ComicPad\\trash 로 옮겨집니다.')),
      );
      return;
    }
    if (!S.folderSupported()) { add(h('p', null, '이 기기에서는 구글 드라이브 폴더를 직접 쓸 수 없습니다. PC 서버로 연결하면 PC 가 드라이브에 복사합니다.')); return; }
    const m = S.driveMirror;
    add(
      h('p', null, m.state === 'none' ? '서버 없이 쓸 때 사본을 둘 폴더를 아직 고르지 않았습니다.' : `${m.root.name}${m.state === 'granted' ? ' (허락됨)' : ' (다시 허락 필요)'}${m.lastError ? ' · ⚠ ' + m.lastError : ''}`),
      h('div.row', null,
        h('button.btn', { type: 'button', onclick: async () => {
          try { await m.choose(); app.toast(`이제 저장할 때마다 ${m.root.name} 에도 사본을 둡니다`); } catch (e) { if (e.name !== 'AbortError') app.toast(e.message, 'error'); }
          renderDrive();
        } }, m.state === 'none' ? '드라이브 폴더 고르기…' : '다른 폴더…'),
        m.state === 'prompt' ? h('button.btn', { type: 'button', onclick: async () => { await m.reconnect(); renderDrive(); } }, '다시 허락하기') : null,
        m.state !== 'none' ? h('button.btn', { type: 'button', onclick: () => driveImportDialog() }, '드라이브에서 가져오기…') : null,
        m.state !== 'none' ? h('button.btn', { type: 'button', onclick: async () => { await m.forget(); renderDrive(); } }, '그만 쓰기') : null),
      h('p', null, h('small', null, '내 드라이브 안에 ComicPad 폴더를 만들어 고르세요 (예: D:\\내 드라이브\\ComicPad). 서버가 복사하는 곳과 같은 폴더를 고르면 어느 쪽으로 저장해도 같은 곳에 사본이 모입니다.')),
    );
  };
  render();
  modal = openModal({ title: '저장 위치', body, actions: [{ label: '닫기', primary: true }] });
  return modal;
}

/** 구글 드라이브의 사본 중 PC 보다 새것(다른 PC 에서 저장한 것 등)을 가져오기 */
export async function driveImportDialog() {
  const S = await import('../core/storage.js');
  const viaServer = server.mode === 'http';
  let remote;
  try {
    remote = viaServer
      ? (await server.list('drive')).filter(i => i.name.startsWith('projects/') && i.name.endsWith('.cpad')).map(i => ({ ...i, name: i.name.slice(9) }))
      : (await S.driveMirror.list('projects')).filter(i => i.name.endsWith('.cpad'));
  } catch (e) { app.toast('드라이브 폴더를 읽지 못했습니다: ' + e.message, 'error'); return; }
  const local = new Map((await server.list('projects').catch(() => [])).map(i => [i.name, i]));
  if (!remote.length) { app.toast('드라이브에 작품이 아직 없습니다'); return; }
  const pull = async (name) => {
    if (viaServer) {
      const r = await fetch(`/api/drive/pull?name=${encodeURIComponent('projects/' + name)}`, { method: 'POST' });
      if (!r.ok) throw new Error((await r.json()).error || '가져오지 못했습니다');
    } else {
      await server.put('projects', name, await (await S.driveMirror.get('projects', name)).blob());
      try { await server.put('projects', name.replace(/\.cpad$/, '.png'), await (await S.driveMirror.get('projects', name.replace(/\.cpad$/, '.png'))).blob()); } catch { /* 썸네일 없음 */ }
    }
  };
  let modal;
  const list = h('div.local-list');
  remote.sort((a, b) => b.mtime - a.mtime);
  for (const it of remote) {
    const loc = local.get(it.name);
    const diff = !loc ? 'new' : it.mtime > loc.mtime + 2 ? 'newer' : it.mtime < loc.mtime - 2 ? 'older' : 'same';
    const label = { new: 'PC 에 없음', newer: '드라이브가 더 새것', older: 'PC 가 더 새것', same: '같음' }[diff];
    list.append(h('div.local-row', null,
      h('div', null, h('b', null, it.name.replace(/\.cpad$/, '')), h('small', null, ` · ${label} · ${new Date(it.mtime * 1000).toLocaleString()}`)),
      h('div.row', null,
        h(`button.btn${diff === 'new' || diff === 'newer' ? '.primary' : ''}`, { type: 'button', onclick: async () => {
          if (loc && !(await confirmDialog(`PC 의 "${it.name}" 를 드라이브 것으로 바꿀까요? 지금 것은 버전 기록에 남습니다.`, '가져오기'))) return;
          try { await pull(it.name); app.toast(`가져왔습니다: ${it.name}`); modal.close(true); } catch (e) { app.toast(e.message, 'error'); }
        } }, '가져오기'))));
  }
  modal = openModal({ title: '구글 드라이브에서 가져오기', body: h('div', null, h('div.help', null, '다른 PC 에서 드라이브에 저장한 작품을 이 PC 로 가져옵니다. 가져온 뒤 파일 > 열기에서 여세요.'), list), wide: true });
}

export async function exportDialog() {
  const v = await form('이미지 내보내기', [
    { key: 'format', label: '형식', type: 'segment', options: [['png', 'PNG'], ['jpg', 'JPG']], value: 'png' },
    { key: 'scale', label: '배율', type: 'segment', options: [['0.25', '25%'], ['0.5', '50%'], ['1', '100%'], ['2', '200%']], value: '1' },
    { key: 'transparent', label: '배경(용지) 빼기 — PNG 투명', type: 'checkbox', value: !app.doc.paper.visible },
    { key: 'download', label: '다운로드 폴더로 받기 (끄면 data/exports 에 저장)', type: 'checkbox', value: false },
    ...(app.doc.print ? [{ key: 'area', label: '범위', type: 'segment', options: [['trim', '재단선 안'], ['bleed', '도련 포함'], ['all', '캔버스 전체']], value: 'bleed' }] : []),
    { key: 'info', type: 'info', label: '밑그림(러프) 레이어와 숨긴 레이어, 메모 핀은 내보내지 않습니다.' },
  ], { okLabel: '내보내기' });
  if (!v) return;
  await exportImage({ format: v.format, scale: parseFloat(v.scale), transparent: v.transparent, download: v.download, area: v.area || 'all' });
}
