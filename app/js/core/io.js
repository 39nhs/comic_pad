// 로컬 서버(G: data 폴더)와의 입출력
import { app } from '../app.js';
import { Doc } from './doc.js';
import { setDoc } from './ops.js';
import { canvasToBlob, createCanvas, safeName, fileStamp, downloadBlob } from './util.js';
import { trimCanvas } from './raster.js';
import { withKey } from './net.js';
import { localStore } from './localstore.js';
import { folderStore, deviceStore, driveMirror } from './storage.js';

const enc = (s) => encodeURIComponent(s);

// PC 서버(Python)가 있으면 그쪽, 없으면 허락받은 폴더(G:), 그것도 없으면 이 기기 안에 저장
const http = {
  async list(kind) {
    const r = await fetch(`/api/list?kind=${kind}`, { cache: 'no-store' });
    if (!r.ok) throw new Error('목록을 읽지 못했습니다');
    return r.json();
  },
  async get(kind, name) {
    const r = await fetch(`/api/file?kind=${kind}&name=${enc(name)}`, { cache: 'no-store' });
    if (!r.ok) throw new Error(`파일을 읽지 못했습니다: ${name}`);
    return r;
  },
  async put(kind, name, body) {
    const r = await fetch(`/api/file?kind=${kind}&name=${enc(name)}`, { method: 'PUT', body });
    const j = await r.json();
    if (!r.ok) throw new Error(j.error || '저장 실패');
    return j;
  },
  async stat(kind, name) {
    try { return (await (await fetch(`/api/stat?kind=${kind}&name=${enc(name)}`, { cache: 'no-store' })).json()).mtime ?? null; } catch { return null; }
  },
  async del(kind, name) {
    const r = await fetch(`/api/file?kind=${kind}&name=${enc(name)}`, { method: 'DELETE' });
    if (!r.ok) throw new Error('삭제 실패');
    return r.json();
  },
};

const blobUrls = new Map();   // 폴더/기기 모드에서 그림·폰트 주소 (kind/name -> blob: URL)

export const server = {
  mode: 'device',     // http | folder | device
  http: false,        // PC 서버에 연결됨
  online: false,      // 파일로 남는 저장소가 있음 (서버 또는 폴더). false 면 내보내기는 다운로드로
  get backend() { return this.mode === 'http' ? http : this.mode === 'folder' ? folderStore : deviceStore; },
  /** 사람에게 보여 줄 저장 위치 */
  get where() { return this.mode === 'http' ? 'data' : this.mode === 'folder' ? `${folderStore.root?.name || '폴더'}/data` : '이 기기'; },

  async ping() {
    try {
      const r = await fetch('/api/ping', { cache: 'no-store' });
      this.http = r.ok && (r.headers.get('content-type') || '').includes('json');
      if (this.http) { const j = await r.json(); this.root = j.root; this.remote = !!j.remote; }
    } catch { this.http = false; }
    await this.chooseMode();
    return this.http;
  },

  async chooseMode() {
    const prev = this.mode;
    if (this.http) this.mode = 'http';
    else {
      if (folderStore.state === 'none' && !this._restored) { this._restored = true; await folderStore.restore().catch(() => {}); await driveMirror.restore().catch(() => {}); }
      this.mode = folderStore.state === 'granted' ? 'folder' : 'device';
      if (this.mode === 'folder') this.root = folderStore.root.name;
    }
    this.online = this.mode !== 'device';
    if (prev !== this.mode) { blobUrls.forEach(u => URL.revokeObjectURL(u)); blobUrls.clear(); app.bus.emit('storage:changed', this.mode); }
    return this.mode;
  },

  list(kind) { return this.backend.list(kind); },
  get(kind, name) { return this.backend.get(kind, name); },
  /** 파일이 마지막으로 바뀐 시각(초) 또는 null */
  stat(kind, name) { return this.backend.stat ? this.backend.stat(kind, name) : null; },
  async put(kind, name, body) { this._forget(kind, name); return this.backend.put(kind, name, body); },
  async del(kind, name) { this._forget(kind, name); return this.backend.del(kind, name); },
  _forget(kind, name) { const k = kind + '/' + name, u = blobUrls.get(k); if (u) { URL.revokeObjectURL(u); blobUrls.delete(k); } },

  async openFolder(kind) {
    if (this.mode !== 'http') { app.toast(this.mode === 'folder' ? `탐색기에서 ${this.where} 폴더를 열어 주세요 (서버 없이 쓸 때는 폴더 열기가 안 됩니다)` : '기기 안 저장소는 폴더로 열 수 없습니다'); return; }
    if (this.remote) { app.toast('폴더 열기는 PC 에서만 됩니다'); return; }
    await fetch(`/api/open-folder?kind=${kind}`, { method: 'POST' });
  },
  /** 서버 모드 전용 주소 (이미지 src 등). 다른 모드에서는 src() 를 쓴다 */
  url(kind, name) {
    const base = kind === 'plugins' ? '/plugins/' : `/data/${kind}/`;
    return withKey(base + name.split('/').map(enc).join('/'));
  },
  /** 어느 모드에서나 쓸 수 있는 주소 (폴더/기기 모드는 blob: 주소) */
  async src(kind, name) {
    if (this.mode === 'http') return this.url(kind, name);
    const k = kind + '/' + name;
    if (!blobUrls.has(k)) blobUrls.set(k, URL.createObjectURL(await (await this.get(kind, name)).blob()));
    return blobUrls.get(k);
  },
};

/** <img> 에 저장소 그림 넣기 (모드와 상관없이) */
export function setImgSrc(img, kind, name, bust = '') {
  server.src(kind, name).then(u => { img.src = server.mode === 'http' && bust ? `${u}?t=${bust}` : u; }).catch(() => { img.style.visibility = 'hidden'; });
  return img;
}

function thumbnail(doc, max = 320) {
  const s = Math.min(1, max / Math.max(doc.width, doc.height));
  return app.compositor.flatten(doc, { scale: s, excludeDraft: false });
}

export async function saveProject(name) {
  const doc = app.doc;
  if (app.tool?.commitPending) app.tool.commitPending();
  // 이름을 주지 않으면 원래 파일 (작품 폴더 안의 페이지면 그 경로 그대로)
  let file;
  if (name) { name = safeName(name); file = `${name}.cpad`; doc.name = name; }
  else if (doc.file) file = doc.file;
  else { name = safeName(doc.name); file = `${name}.cpad`; }
  // 연 뒤에 다른 곳(다른 창·휴대폰·다른 PC·드라이브)에서 이 파일이 저장됐으면 덮어쓰기 전에 묻기
  if (file === doc.file && doc.fileMtime) {
    const cur = await server.stat('projects', file);
    if (cur && cur > doc.fileMtime + 1.5) {
      const how = await conflictDialog(file, cur);
      if (how === 'cancel') return null;
      if (how === 'copy') { const base = file.replace(/\.cpad$/, ''); return saveProject(`${base.split('/').pop()} (내 판 ${fileStamp()})`); }
    }
  }
  const json = await doc.serialize();
  try {
    await server.put('projects', file, JSON.stringify(json));
    await server.put('projects', file.replace(/\.cpad$/, '.png'), await canvasToBlob(thumbnail(doc)));
  } catch (e) {
    // PC 와 연결이 끊겼으면 이 기기에 임시 저장 (나중에 "기기 임시 저장함"에서 PC 로 보내기)
    if (server.mode === 'device') throw e;
    await localStore.put('projects/' + file, new Blob([JSON.stringify(json)], { type: 'application/json' }));
    server.online = false;
    app.toast('PC 에 저장하지 못해 이 기기에 임시 저장했습니다 (파일 > 기기 임시 저장함)', 'warn');
    doc.modified = false;
    app.bus.emit('doc:modified', false);
    return file;
  }
  doc.file = file;
  doc.fileMtime = await server.stat('projects', file);
  doc.modified = false;
  app.bus.emit('doc:modified', false);
  app.bus.emit('doc:saved', doc);
  app.toast(server.mode === 'device' ? `이 기기에 저장됨: ${file}` : `저장됨: ${server.where}/projects/${file}`);
  return file;
}

export async function openProject(file, kind = 'projects') {
  const r = await server.get(kind, file);
  const doc = await Doc.deserialize(await r.json());
  doc.file = kind === 'projects' ? file : null;
  if (doc.file) doc.fileMtime = await server.stat('projects', file);
  setDoc(doc);
  app.toast(`열었습니다: ${doc.name}`);
}

/** 저장 충돌: 'overwrite' | 'copy' | 'cancel' */
function conflictDialog(file, mtime) {
  return new Promise((resolve) => {
    import('../ui/dialogs.js').then(({ openModal }) => {
      const when = new Date(mtime * 1000).toLocaleString();
      const body = document.createElement('div');
      body.className = 'confirm-text';
      body.textContent = `"${file}" 이(가) 연 뒤에 다른 곳에서 저장되었습니다 (${when}).\n다른 창·휴대폰·다른 PC·구글 드라이브에서 고친 내용일 수 있습니다.\n\n덮어쓰면 그쪽 내용은 버전 기록에만 남습니다.`;
      body.style.whiteSpace = 'pre-line';
      openModal({
        title: '저장 충돌', body,
        actions: [
          { label: '취소', run: () => resolve('cancel') },
          { label: '내 판을 새 이름으로', run: () => resolve('copy') },
          { label: '덮어쓰기', danger: true, run: () => resolve('overwrite') },
        ],
        onClose: (r) => { if (r === null) resolve('cancel'); },
      });
    });
  });
}

/** 창으로 돌아왔을 때, 열어 둔 작품이 다른 곳에서 바뀌었으면 알려 줌 */
export async function checkExternalChange() {
  const doc = app.doc;
  if (!doc?.file || !doc.fileMtime || doc._extWarned === doc.fileMtime) return;
  const cur = await server.stat('projects', doc.file);
  if (!cur || cur <= doc.fileMtime + 1.5) return;
  doc._extWarned = doc.fileMtime;
  app.toast(`"${doc.name}" 이(가) 다른 곳에서 저장되었습니다 (${new Date(cur * 1000).toLocaleTimeString()})`, 'warn', {
    duration: 0,
    actions: [{ label: '그쪽 판 열기', run: async () => { if (!doc.modified || await app.ui.confirm('지금 고친 내용을 버리고 다른 곳에서 저장한 판을 열까요? (지금 내용은 자동 저장에 남음)', '열기')) openProject(doc.file); } }],
  });
}

export async function openProjectFile(blob) {
  const doc = await Doc.deserialize(JSON.parse(await blob.text()));
  setDoc(doc);
}

export async function downloadProject() {
  const json = await app.doc.serialize();
  downloadBlob(new Blob([JSON.stringify(json)], { type: 'application/json' }), `${safeName(app.doc.name)}.cpad`);
}

let autosaving = false;
export async function autosave() {
  const doc = app.doc;
  if (!doc || !doc.modified || autosaving || app.tool?.busy || app.selftesting) return;
  autosaving = true;
  try {
    const json = await doc.serialize({ fast: true });
    const body = JSON.stringify(json);
    try { await server.put('autosave', `${safeName(doc.name)}.cpad`, body); }
    catch { await localStore.put(`autosave/${safeName(doc.name)}.cpad`, new Blob([body], { type: 'application/json' })); }
    app.bus.emit('autosaved', new Date());
  } catch (e) {
    console.warn('자동 저장 실패', e);
  } finally {
    autosaving = false;
  }
}

/** 합성 이미지를 data/exports 에 저장 */
export async function exportImage({ format = 'png', scale = 1, transparent = false, quality = 0.92, download = false, area = 'all' } = {}) {
  const doc = app.doc;
  const bg = format === 'jpg' ? (doc.paper.visible ? doc.paper.color : '#ffffff') : null;
  let canvas = app.compositor.flatten(doc, { scale, paper: !transparent, background: bg });
  if (area !== 'all') canvas = (await import('../ui/manuscript.js')).cropToPrint(canvas, area, scale);
  const type = format === 'jpg' ? 'image/jpeg' : 'image/png';
  const blob = await canvasToBlob(canvas, type, quality);
  const name = `${safeName(doc.name)}_${fileStamp()}.${format}`;
  if (download || !server.online) downloadBlob(blob, name);
  else {
    await server.put('exports', name, blob);
    app.toast(`내보냄: ${server.where}/exports/${name}`);
  }
  return name;
}

/** 캔버스를 투명 PNG 에셋으로 저장 */
export async function saveAsset(canvas, name, folder = '') {
  const file = `${folder ? folder.replace(/\/+$/, '') + '/' : ''}${safeName(name)}.png`;
  await server.put('assets', file, await canvasToBlob(canvas));
  app.bus.emit('assets:changed');
  app.toast(`에셋 저장: ${server.where}/assets/${file}`);
  return file;
}

// 휴대폰/태블릿은 PC 와 설정(패널 위치 등)을 따로 쓴다
const settingsName = () => (server.remote ? 'settings-mobile.json' : 'settings.json');

export async function loadSettings() {
  if (server.online) {
    try { return await (await server.get('config', settingsName())).json(); } catch { /* 첫 실행 */ }
  }
  try { return JSON.parse(localStorage.getItem('comicpad.settings') || 'null'); } catch { return null; }
}

export async function saveSettings(settings) {
  const body = JSON.stringify(settings, null, 1);
  try { localStorage.setItem('comicpad.settings', body); } catch { /* 무시 */ }
  if (server.online) await server.put('config', settingsName(), body);
}

/** 레이어 내용(선택 영역이 있으면 그 안쪽만)을 잘라낸 캔버스. merged=true 면 보이는 레이어 합성 */
export function extractContent(layer, merged = false) {
  const doc = app.doc;
  const sel = doc.selection;
  const c = createCanvas(doc.width, doc.height);
  const x = c.getContext('2d');
  if (merged) app.compositor.render(x, doc, new DOMMatrix(), { paper: false, excludeDraft: true });
  else x.drawImage(layer.canvas, 0, 0);
  if (sel.active) {
    x.globalCompositeOperation = 'destination-in';
    x.drawImage(sel.mask, 0, 0);
  }
  return trimCanvas(c);
}
