// 서버 없이 저장하는 두 가지 방법
//  - 폴더: PC 의 Edge/Chrome 이 허락받은 ComicPad 폴더(G:\claude\comicpad)에 직접 읽고 쓴다 (Python 불필요)
//  - 기기: 브라우저 안 저장소(IndexedDB). 휴대폰·태블릿 단독 사용, 또는 아무 폴더도 없을 때
import { localStore } from './localstore.js';

// 서버의 data 폴더 구성과 똑같이
const DIRS = {
  projects: ['data', 'projects'], assets: ['data', 'assets'], autosave: ['data', 'autosave'], fonts: ['data', 'fonts'],
  exports: ['data', 'exports'], config: ['data', 'config'], versions: ['data', 'versions'], plugins: ['plugins'],
};
const TYPES = { cpad: 'application/json', json: 'application/json', png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', webp: 'image/webp', js: 'text/javascript', psd: 'application/octet-stream' };
const typeOf = (name) => TYPES[name.split('.').pop().toLowerCase()] || 'application/octet-stream';
const toBlob = (body, name) => (body instanceof Blob ? body : new Blob([body], { type: typeOf(name) }));
const stamp = (t = Date.now()) => {
  const d = new Date(t), p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
};
const cleanParts = (name) => {
  const parts = String(name).split('/').filter(Boolean);
  if (!parts.length || parts.some(s => s === '..' || s === '.')) throw new Error('잘못된 파일 이름: ' + name);
  return parts;
};

// ---------------------------------------------------------------------
// 고른 폴더 기억하기 (FileSystemDirectoryHandle 은 IndexedDB 에 그대로 넣을 수 있다)
// ---------------------------------------------------------------------
function handleDB() {
  return new Promise((res, rej) => {
    const r = indexedDB.open('comicpad-handles', 1);
    r.onupgradeneeded = () => r.result.createObjectStore('h');
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
}
async function hget(key) {
  const db = await handleDB();
  return new Promise((res) => { const q = db.transaction('h').objectStore('h').get(key); q.onsuccess = () => res(q.result || null); q.onerror = () => res(null); });
}
async function hset(key, val) {
  const db = await handleDB();
  return new Promise((res, rej) => {
    const t = db.transaction('h', 'readwrite');
    if (val == null) t.objectStore('h').delete(key); else t.objectStore('h').put(val, key);
    t.oncomplete = () => res();
    t.onerror = () => rej(t.error);
  });
}

export const folderSupported = () => typeof window.showDirectoryPicker === 'function';

// 구글 드라이브(데스크톱 앱) 쪽 사본: 내 드라이브\ComicPad\projects … (서버가 복사하는 곳과 같은 구성)
const DRIVE_DIRS = { projects: ['projects'], assets: ['assets'], exports: ['exports'], fonts: ['fonts'] };

/** 허락받은 폴더 하나를 저장소로 쓰는 객체 */
function makeFolder({ key, pickerId, dirs, trash: trash_, versions }) {
  return {
    root: null,          // FileSystemDirectoryHandle
    state: 'none',       // none | granted | prompt (다시 허락 필요)
    dirs,
    mirror: null,        // 저장/삭제할 때 따라 할 다른 폴더 (구글 드라이브)

    async restore() {
      if (!folderSupported()) return this.state;
      this.root = await hget(key).catch(() => null);
      if (!this.root) { this.state = 'none'; return this.state; }
      const p = await this.root.queryPermission({ mode: 'readwrite' }).catch(() => 'denied');
      this.state = p === 'granted' ? 'granted' : 'prompt';
      return this.state;
    },

    /** 버튼을 누른 직후(사용자 동작)에만 호출할 수 있다 */
    async reconnect() {
      if (!this.root) return false;
      const p = await this.root.requestPermission({ mode: 'readwrite' }).catch(() => 'denied');
      this.state = p === 'granted' ? 'granted' : 'prompt';
      return this.state === 'granted';
    },

    async choose() {
      const dir = await window.showDirectoryPicker({ id: pickerId, mode: 'readwrite', startIn: 'documents' });
      this.root = dir;
      this.state = 'granted';
      await hset(key, dir);
      return dir;
    },

    async forget() { this.root = null; this.state = 'none'; await hset(key, null); },

    /** 고른 폴더가 ComicPad 폴더처럼 보이는지 (data / server.py / app 중 하나) */
    async looksRight(dir = this.root) {
      for await (const name of dir.keys()) if (name === 'data' || name === 'server.py' || name === 'app') return true;
      return false;
    },

    async _dir(kind, sub = [], create = false) {
      const path = this.dirs[kind];
      if (!path) throw new Error('알 수 없는 종류: ' + kind);
      let d = this.root;
      for (const seg of [...path, ...sub]) d = await d.getDirectoryHandle(seg, { create });
      return d;
    },
    async _file(kind, name, create = false) {
      const parts = cleanParts(name);
      const dir = await this._dir(kind, parts.slice(0, -1), create);
      return dir.getFileHandle(parts.at(-1), { create });
    },

    async list(kind) {
      const out = [];
      let base;
      try { base = await this._dir(kind); } catch { return out; }
      const walk = async (dir, prefix) => {
        for await (const [name, h] of dir.entries()) {
          if (h.kind === 'directory') await walk(h, prefix + name + '/');
          else { const f = await h.getFile(); out.push({ name: prefix + name, size: f.size, mtime: f.lastModified / 1000 }); }
        }
      };
      await walk(base, '');
      return out.sort((a, b) => b.mtime - a.mtime);
    },

    async stat(kind, name) {
      try { return (await (await this._file(kind, name)).getFile()).lastModified / 1000; } catch { return null; }
    },

    async get(kind, name) {
      let f;
      try { f = await (await this._file(kind, name)).getFile(); } catch { throw new Error(`파일을 읽지 못했습니다: ${name}`); }
      return new Response(f, { headers: { 'Content-Type': typeOf(name) } });
    },

    async _write(kind, name, blob) {
      const fh = await this._file(kind, name, true);
      const w = await fh.createWritable();
      await w.write(blob);
      await w.close();
    },

    async put(kind, name, body) {
      if (kind === 'versions' || kind === 'plugins') throw new Error('여기에는 쓸 수 없습니다');
      // 서버와 똑같이: 작품을 덮어쓰기 전에 이전 판을 versions 에 보관 (최근 30개)
      if (versions && kind === 'projects' && name.endsWith('.cpad')) {
        try {
          const old = await (await this._file(kind, name)).getFile();
          const rel = cleanParts(name).join('/').slice(0, -5);
          await this._write('versions', `${rel}/${stamp(old.lastModified)}.cpad`, old);
          const vdir = await this._dir('versions', cleanParts(rel));
          const names = [];
          for await (const n of vdir.keys()) if (n.endsWith('.cpad')) names.push(n);
          names.sort();
          for (const n of names.slice(0, -30)) await vdir.removeEntry(n);
        } catch { /* 처음 저장 */ }
      }
      const blob = toBlob(body, name);
      await this._write(kind, name, blob);
      this._follow('put', kind, name, blob);
      return { ok: true };
    },

    /** 따라 하는 폴더(구글 드라이브)에도 반영. 실패해도 본 저장은 그대로 */
    _follow(op, kind, name, blob) {
      const m = this.mirror;
      if (!m || m.state !== 'granted' || !m.dirs[kind]) return;
      (op === 'put' ? m.put(kind, name, blob) : m.del(kind, name)).then(() => { m.lastOk = Date.now(); }, (e) => { m.lastError = `${name}: ${e.message}`; });
    },

    /** 삭제 = data/trash 로 옮기기 (영구 삭제 없음) */
    async del(kind, name) {
      const parts = cleanParts(name);
      const dir = await this._dir(kind, parts.slice(0, -1));
      const f = await (await dir.getFileHandle(parts.at(-1))).getFile();
      let trash = this.root;
      for (const seg of trash_) trash = await trash.getDirectoryHandle(seg, { create: true });
      const t = await trash.getFileHandle(`${stamp()}_${parts.at(-1)}`, { create: true });
      const w = await t.createWritable();
      await w.write(f);
      await w.close();
      await dir.removeEntry(parts.at(-1));
      this._follow('del', kind, name);
      return { ok: true };
    },
  };
}

export const folderStore = makeFolder({ key: 'root', pickerId: 'comicpad-root', dirs: DIRS, trash: ['data', 'trash'], versions: true });
export const driveMirror = makeFolder({ key: 'drive', pickerId: 'comicpad-drive', dirs: DRIVE_DIRS, trash: ['trash'], versions: false });
folderStore.mirror = driveMirror;

// ---------------------------------------------------------------------
// 기기 안 저장소 ("기기 임시 저장함"과 같은 곳)
// ---------------------------------------------------------------------
export const deviceStore = {
  async list(kind) {
    const pre = kind + '/';
    return (await localStore.list()).filter(i => i.name.startsWith(pre)).map(i => ({ ...i, name: i.name.slice(pre.length) }));
  },
  async stat(kind, name) {
    const key = `${kind}/${cleanParts(name).join('/')}`;
    return (await localStore.list()).find(i => i.name === key)?.mtime ?? null;
  },
  async get(kind, name) {
    const b = await localStore.get(`${kind}/${cleanParts(name).join('/')}`);
    if (!b) throw new Error(`파일을 읽지 못했습니다: ${name}`);
    return new Response(b, { headers: { 'Content-Type': b.type || typeOf(name) } });
  },
  async put(kind, name, body) {
    await localStore.put(`${kind}/${cleanParts(name).join('/')}`, toBlob(body, name));
    return { ok: true };
  },
  async del(kind, name) {
    await localStore.del(`${kind}/${cleanParts(name).join('/')}`);
    return { ok: true };
  },
};
