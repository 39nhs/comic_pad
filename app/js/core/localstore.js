// 이 기기(브라우저) 안 임시 저장소 — PC 연결이 끊겼을 때 작업을 잃지 않도록
const DB = 'comicpad', STORE = 'files';

function open() {
  return new Promise((res, rej) => {
    const r = indexedDB.open(DB, 1);
    r.onupgradeneeded = () => r.result.createObjectStore(STORE);
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
}

async function tx(mode, fn) {
  const db = await open();
  return new Promise((res, rej) => {
    const t = db.transaction(STORE, mode);
    const out = fn(t.objectStore(STORE));
    t.oncomplete = () => res(out?.result ?? out);
    t.onerror = () => rej(t.error);
  });
}

export const localStore = {
  async put(key, blob) { return tx('readwrite', s => s.put({ blob, mtime: Date.now() / 1000, size: blob.size }, key)); },
  async get(key) { const r = await tx('readonly', s => s.get(key)); return r?.blob || null; },
  async del(key) { return tx('readwrite', s => s.delete(key)); },
  async list() {
    const db = await open();
    return new Promise((res, rej) => {
      const out = [];
      const req = db.transaction(STORE).objectStore(STORE).openCursor();
      req.onsuccess = () => {
        const c = req.result;
        if (!c) { res(out.sort((a, b) => b.mtime - a.mtime)); return; }
        out.push({ name: c.key, size: c.value.size, mtime: c.value.mtime });
        c.continue();
      };
      req.onerror = () => rej(req.error);
    });
  },
};
