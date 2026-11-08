// 타임랩스: 그리는 동안 화면을 조금씩 찍어 두었다가 동영상(WebM)으로 만든다
// 찍은 장면은 이 브라우저 안(IndexedDB)에 작품마다 따로 보관 (기기 임시 저장함과는 별개)
import { app } from '../app.js';
import { uid, createCanvas, canvasToBlob, safeName, fileStamp, downloadBlob } from '../core/util.js';
import { server } from '../core/io.js';
import { form, confirmDialog } from './dialogs.js';

const DB = 'comicpad-timelapse', ST = 'frames';
function db() {
  return new Promise((res, rej) => {
    const r = indexedDB.open(DB, 1);
    r.onupgradeneeded = () => r.result.createObjectStore(ST);
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
}
async function tx(mode, fn) {
  const d = await db();
  return new Promise((res, rej) => {
    const t = d.transaction(ST, mode);
    const q = fn(t.objectStore(ST));
    t.oncomplete = () => res(q?.result);
    t.onerror = () => rej(t.error);
  });
}
const range = (id) => IDBKeyRange.bound(id + '/', id + '/￿');
const keysOf = (id) => tx('readonly', s => s.getAllKeys(range(id)));
const putFrame = (id, n, blob) => tx('readwrite', s => s.put(blob, `${id}/${String(n).padStart(6, '0')}`));
const getFrame = (key) => tx('readonly', s => s.get(key));
const clearFrames = (id) => tx('readwrite', s => s.delete(range(id)));

const MAX_SIDE = 1280;
let timer = 0, busy = false, count = 0, lastId = null;

const tl = () => app.doc?.timelapse;

async function capture() {
  const doc = app.doc, t = tl();
  if (!t?.on || busy) return;
  busy = true;
  try {
    const s = Math.min(1, MAX_SIDE / Math.max(doc.width, doc.height));
    const c = app.compositor.flatten(doc, { scale: s, paper: true, background: '#ffffff' });
    if (lastId !== t.id) { lastId = t.id; count = (await keysOf(t.id)).length; }
    await putFrame(t.id, count++, await canvasToBlob(c, 'image/jpeg', 0.82));
  } catch (e) { console.warn('타임랩스 저장 실패', e); }
  busy = false;
}

/** 그린 뒤 잠깐 쉬었을 때 한 장 (펜이 움직이는 동안은 찍지 않음) */
function schedule() {
  if (!tl()?.on) return;
  clearTimeout(timer);
  timer = setTimeout(() => (window.requestIdleCallback ? requestIdleCallback(capture, { timeout: 2000 }) : capture()), 1200);
}

function syncBadge() { document.body.classList.toggle('tl-rec', !!tl()?.on); }

export async function toggleTimelapse() {
  const doc = app.doc;
  if (!doc.timelapse) doc.timelapse = { id: uid('tl'), on: false };
  doc.timelapse.on = !doc.timelapse.on;
  app.markModified();
  syncBadge();
  if (doc.timelapse.on) { capture(); app.toast('타임랩스 기록 시작: 그릴 때마다 장면을 찍어 둡니다 (작품 파일에 켜짐 상태 저장)'); }
  else app.toast(`타임랩스 기록을 멈췄습니다 (${(await keysOf(doc.timelapse.id)).length}장)`);
}

export async function exportTimelapse() {
  const t = tl();
  const keys = t ? await keysOf(t.id) : [];
  if (keys.length < 2) { app.toast('찍힌 장면이 아직 없습니다. 보기 > 타임랩스 기록을 켜고 그려 보세요'); return; }
  const v = await form('타임랩스 동영상 만들기', [
    { key: 'sec', label: '동영상 길이 (초)', type: 'number', value: Math.min(30, Math.max(5, Math.round(keys.length / 10))) },
    { key: 'hold', label: '마지막 완성 그림 보여 주기 (초)', type: 'number', value: 2 },
    { key: 'info', type: 'info', label: `찍힌 장면 ${keys.length}장. 만드는 데 동영상 길이만큼 시간이 걸립니다.` },
  ], { okLabel: '만들기' });
  if (!v) return;
  const fps = 30, total = Math.max(2, Math.round(Math.max(1, v.sec) * fps)), hold = Math.max(0, Math.round((v.hold || 0) * fps));
  const first = await createImageBitmap(await getFrame(keys[0]));
  const c = createCanvas(Math.round(first.width / 2) * 2, Math.round(first.height / 2) * 2);
  const x = c.getContext('2d');
  const stream = c.captureStream(0);
  const track = stream.getVideoTracks()[0];
  const type = MediaRecorder.isTypeSupported('video/webm;codecs=vp9') ? 'video/webm;codecs=vp9' : 'video/webm';
  const rec = new MediaRecorder(stream, { mimeType: type, videoBitsPerSecond: 6e6 });
  const chunks = [];
  rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
  const done = new Promise(r => { rec.onstop = r; });
  await app.task('타임랩스 만드는 중', async () => {
    rec.start();
    let shown = -1, bmp = first;
    const draw = async (i) => {
      if (i !== shown) { bmp = await createImageBitmap(await getFrame(keys[i])); shown = i; }
      x.fillStyle = '#fff'; x.fillRect(0, 0, c.width, c.height);
      x.drawImage(bmp, 0, 0, c.width, c.height);
      // 창이 가려지면 브라우저가 녹화를 멈추므로 다시 보일 때까지 기다림
      if (document.hidden) { rec.pause?.(); app.toast('창이 가려져 동영상 만들기를 잠시 멈췄습니다. 창을 다시 보이게 하면 이어집니다', 'warn'); while (document.hidden) await new Promise(r => setTimeout(r, 300)); rec.resume?.(); }
      track.requestFrame?.();
      await new Promise(r => setTimeout(r, 1000 / fps));
    };
    for (let f = 0; f < total; f++) await draw(Math.min(keys.length - 1, Math.floor(f * keys.length / total)));
    for (let f = 0; f < hold; f++) await draw(keys.length - 1);
    rec.stop();
    await done;
  }, { delay: 0 });
  const blob = new Blob(chunks, { type: 'video/webm' });
  const name = `${safeName(app.doc.name)}_타임랩스_${fileStamp()}.webm`;
  if (server.online) { await server.put('exports', name, blob); app.toast(`타임랩스 저장: ${server.where}/exports/${name}`); }
  else downloadBlob(blob, name);
}

async function clearTimelapse() {
  const t = tl();
  if (!t) return;
  if (!(await confirmDialog('이 작품의 타임랩스 장면을 모두 지울까요? (그림은 그대로)', '지우기', true))) return;
  await clearFrames(t.id);
  count = 0;
  app.toast('타임랩스 장면을 지웠습니다');
}

export function initTimelapse() {
  app.bus.on('history:changed', schedule);
  app.bus.on('doc:changed', () => { lastId = null; syncBadge(); });
  const C = (id, label, run) => app.registerCommand(id, { label, run });
  C('timelapse.toggle', '타임랩스 기록 켜기/끄기', toggleTimelapse);
  C('timelapse.export', '타임랩스 동영상 만들기…', exportTimelapse);
  C('timelapse.clear', '타임랩스 장면 지우기…', clearTimelapse);
  for (const it of [{ sep: true }, { command: 'timelapse.toggle' }, { command: 'timelapse.export' }, { command: 'timelapse.clear' }]) app.addMenuItem('view', it);
  syncBadge();
}
