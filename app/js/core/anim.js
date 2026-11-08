// 애니메이션 (타임라인)
// - 트랙 = "애니메이션 폴더" 하나. 폴더 안 레이어 하나하나가 그림 칸(셀).
// - 전체 프레임 0..T-1 이 공통 시간. 트랙마다 meta.xs[프레임] = 그 프레임에 보일 셀 id (null = 빈 칸).
//   같은 셀이 이어지면 "길게 보여 주기", 떨어진 프레임에 같은 셀이 다시 나오면 "연결된 칸"(하나 고치면 같이 바뀜).
// - 프레임마다 길이(ms)를 따로 줄 수 있고, 태그(구간 이름 + 정방향/역방향/핑퐁)로 구간 재생·내보내기.
import { app } from '../app.js';
import * as ops from './ops.js';
import { createCanvas, canvasToBlob, safeName, fileStamp, downloadBlob } from './util.js';
import { server } from './io.js';
import { playAudio, stopAudio, applyCamera, audioStream } from './animx.js';
import { motionOf, motionAt, shiftKeys } from './motion.js';

const emit = () => { app.bus.emit('anim:changed'); app.requestRender(); };

// ---------------------------------------------------------------------
// 트랙 · 셀 · 노출표
// ---------------------------------------------------------------------
export const isTrack = (l) => !!(l?.isFolder && l.meta?.anim);
export function tracks(doc = app.doc) { return doc ? doc.layers.filter(isTrack) : []; }

/** 지금 고른 레이어가 속한 트랙 (없으면 첫 트랙) */
export function animFolder(doc = app.doc) {
  if (!doc) return null;
  const act = doc.active;
  if (act) {
    if (isTrack(act)) return act;
    const p = doc.parentOf(act), f = p && doc.byId(p);
    if (isTrack(f)) return f;
  }
  return tracks(doc).at(-1) || null;
}

/** 트랙의 노출표. 예전 파일(노출표 없음)은 폴더 순서 + 프레임 길이로 만들어 씀 */
export function xsOf(track, doc = app.doc) {
  const kids = doc.childrenOf(track);
  const ids = new Set(kids.map(k => k.id));
  if (Array.isArray(track.meta?.xs)) return track.meta.xs.map(id => (id && ids.has(id) ? id : null));
  const out = [];
  for (const k of kids) for (let r = Math.max(1, Math.round(doc.anim.holds?.[k.id] || 1)); r > 0; r--) out.push(k.id);
  return out;
}

/** 전체 프레임 수 */
export function frameCount(doc = app.doc) {
  if (!doc) return 0;
  return Math.max(doc.anim.length || 0, ...tracks(doc).map(t => xsOf(t, doc).length), 0);
}

/** 프레임 f 에서 트랙에 보일 셀 */
export function celAt(track, f, doc = app.doc) {
  const xs = xsOf(track, doc);
  const id = xs[f];
  return id ? doc.byId(id) : null;
}

/** 프레임 길이 (ms): 따로 정했으면 그것, 아니면 1/fps */
export const frameMs = (f, doc = app.doc) => doc.anim.ms?.[f] || 1000 / (doc.anim.fps || 12);

/** 각 프레임 시작 시각(초), 마지막은 전체 길이 */
export function timeline(doc = app.doc) {
  const n = frameCount(doc), out = [];
  let t = 0;
  for (let f = 0; f < n; f++) { out.push(t); t += frameMs(f, doc) / 1000; }
  out.push(t);
  return out;
}

/** 지금 트랙의 셀 목록 (옛 기능 호환: 레이어 패널·자동 점검 등) */
export function cels(doc = app.doc) {
  const f = animFolder(doc);
  return f ? doc.childrenOf(f) : [];
}

// ---- 노출표 고치기 (실행취소 포함) ----
function snapshot(doc) {
  return { anim: structuredClone({ ...doc.anim, playing: false }), xs: tracks(doc).map(t => [t, structuredClone(t.meta?.xs ?? null), structuredClone(t.meta?.motion ?? null), structuredClone(t.meta?.lipMap ?? null)]) };
}
function restore(doc, s) {
  const playing = doc.anim.playing;
  doc.anim = { ...structuredClone(s.anim), playing };
  for (const [t, xs, mo, lip] of s.xs) {
    t.meta = { ...(t.meta || {}) };
    if (xs) t.meta.xs = structuredClone(xs); else delete t.meta.xs;
    if (mo) t.meta.motion = structuredClone(mo); else delete t.meta.motion;
    if (lip) t.meta.lipMap = structuredClone(lip); else delete t.meta.lipMap;
  }
  app.markModified(); emit(); app.bus.emit('layers:changed');
}
/** 노출표·태그·길이를 바꾸는 작업을 실행취소 한 번으로 */
export function editAnim(label, fn) {
  const doc = app.doc;
  return app.history.batch(label, () => {
    const before = snapshot(doc);
    // 예전 파일이면 노출표를 지금 모양 그대로 굳혀 둔다
    for (const t of tracks(doc)) if (!Array.isArray(t.meta?.xs)) t.meta = { ...(t.meta || {}), xs: xsOf(t, doc) };
    doc.anim.holds = {};
    const r = fn();
    // 길이 정리: 트랙 노출표를 전체 길이에 맞추기
    const n = Math.max(doc.anim.length || 0, ...tracks(doc).map(t => t.meta.xs.length));
    doc.anim.length = n;
    for (const t of tracks(doc)) { while (t.meta.xs.length < n) t.meta.xs.push(null); t.meta.xs.length = n; }
    if (doc.anim.ms) doc.anim.ms.length = Math.min(doc.anim.ms.length, n);
    doc.anim.frame = Math.max(0, Math.min(doc.anim.frame, n - 1));
    const after = snapshot(doc);
    app.history.push({ label, undo: () => restore(doc, before), redo: () => restore(doc, after) });
    app.markModified(); emit();
    return r;
  });
}

/** 더 이상 어느 프레임에도 안 쓰이는 셀 레이어 지우기 */
function dropUnused(doc) {
  const used = new Set(tracks(doc).flatMap(t => t.meta.xs.filter(Boolean)));
  for (const t of tracks(doc)) for (const k of doc.childrenOf(t)) if (!used.has(k.id) && !(doc.anim.light?.ids || []).includes(k.id)) ops.deleteLayer(k);
}

const celName = (track, doc) => String(doc.childrenOf(track).length + 1);
function newCel(track, doc, from = null) {
  const kids = doc.childrenOf(track), at = kids.length ? doc.indexOf(kids.at(-1)) + 1 : doc.indexOf(track);
  if (from) { const l = from.clone(celName(track, doc)); l.parent = track.id; return ops.addLayer(l, at); }
  return ops.addLayer({ name: celName(track, doc), parent: track.id }, at);
}

/** editAnim 안에서만: 빈 트랙 / 새 셀 (다른 모듈용) */
export function addTrackRaw(name) {
  const doc = app.doc;
  return ops.addLayer({ name, kind: 'folder', parent: null, meta: { anim: true, xs: Array(frameCount(doc)).fill(null) } }, doc.layers.length);
}
export const newCelRaw = (track, from = null) => newCel(track, app.doc, from);

// ---------------------------------------------------------------------
// 프레임 이동 · 만들기
// ---------------------------------------------------------------------
export function setFrame(i, { select = true } = {}) {
  const doc = app.doc, n = frameCount(doc);
  if (!n) return;
  doc.anim.frame = ((i % n) + n) % n;
  if (select && !doc.anim.playing) {
    const t = animFolder(doc), c = t && celAt(t, doc.anim.frame, doc);
    if (c) doc.setActive(c);
  }
  if (!doc.anim.playing && doc.anim.audio && app.settings.animScrub !== false) {
    const t = timeline(doc);
    playAudio(t[doc.anim.frame], t[doc.anim.frame + 1] - t[doc.anim.frame]);
  }
  emit();
}

/** 트랙 + 칸으로 이동 (타임라인 칸 누르기) */
export function selectCell(track, f) {
  const doc = app.doc;
  doc.anim.frame = f;
  const c = celAt(track, f, doc);
  doc.setActive(c || track);
  emit();
}

export function createAnimation(frames = 1) {
  const doc = app.doc;
  return editAnim('애니메이션 만들기', () => {
    const n = Math.max(frames, frameCount(doc));
    const f = ops.addLayer({ name: tracks(doc).length ? `트랙 ${tracks(doc).length + 1}` : '애니메이션', kind: 'folder', parent: null, meta: { anim: true, xs: [] } }, doc.layers.length);
    for (let i = 0; i < frames; i++) f.meta.xs.push(newCel(f, doc).id);
    // 다른 트랙보다 짧으면 마지막 칸을 이어 보여 줌
    while (f.meta.xs.length < n) f.meta.xs.push(f.meta.xs.at(-1));
    doc.anim.frame = 0;
    doc.setActive(doc.byId(f.meta.xs[0]));
    return f;
  });
}

/** 새 트랙 (지금 길이만큼, 빈 셀 하나를 처음부터 끝까지) */
export function addTrack() {
  const doc = app.doc, n = Math.max(1, frameCount(doc));
  return editAnim('트랙 추가', () => {
    const f = ops.addLayer({ name: `트랙 ${tracks(doc).length + 1}`, kind: 'folder', parent: null, meta: { anim: true, xs: [] } }, doc.layers.length);
    const c = newCel(f, doc);
    for (let i = 0; i < n; i++) f.meta.xs.push(c.id);
    doc.setActive(c);
    return f;
  });
}

/** 지금 보이는 레이어들(폴더 밖)을 각각 프레임으로 */
export function layersToAnimation() {
  const doc = app.doc;
  const src = doc.childrenOf(null).filter(l => !l.isFolder && l.kind === 'raster');
  if (src.length < 2) { app.toast('프레임으로 바꿀 레이어가 2개 이상 필요합니다'); return; }
  editAnim('레이어 → 프레임', () => {
    const f = ops.addLayer({ name: '애니메이션', kind: 'folder', parent: null, meta: { anim: true, xs: [] } }, doc.layers.length);
    for (const l of src) { ops.setLayerProps(l, { parent: f.id, visible: true }, '프레임으로'); f.meta.xs.push(l.id); }
    doc.anim.frame = 0;
  });
}

/**
 * 프레임 추가 (지금 프레임 뒤). how: 'blank' 빈 셀 | 'copy' 복제 | 'link' 같은 셀 연결
 * 지금 트랙만 새 셀을 받고, 다른 트랙은 앞 칸을 그대로 이어 보여 준다.
 */
export function addFrame(how = 'blank') {
  const doc = app.doc;
  let track = animFolder(doc);
  if (!track) { createAnimation(1); return; }
  const f = doc.anim.frame;
  editAnim({ blank: '빈 프레임 추가', copy: '프레임 복제', link: '연결된 프레임 추가' }[how], () => {
    const cur = celAt(track, f, doc);
    for (const t of tracks(doc)) {
      let id = t.meta.xs[f] ?? null;
      if (t === track) id = how === 'link' && cur ? cur.id : newCel(t, doc, how === 'copy' ? cur : null).id;
      t.meta.xs.splice(f + 1, 0, id);
    }
    if (doc.anim.ms?.length > f + 1) doc.anim.ms.splice(f + 1, 0, null);
    shiftAfter(doc, f + 1, 1);
    doc.anim.frame = f + 1;
    const c = celAt(track, f + 1, doc);
    if (c) doc.setActive(c);
  });
}
export const addCel = (duplicate = false) => addFrame(duplicate ? 'copy' : 'blank');

/** 태그·카메라 키를 프레임 삽입/삭제에 맞춰 밀기 */
function shiftAfter(doc, at, d) {
  for (const t of doc.anim.tags || []) { if (t.from >= at) t.from += d; if (t.to >= at) t.to += d; if (t.to < t.from) t.to = t.from; }
  if (doc.anim.camera?.keys) for (const k of doc.anim.camera.keys) if (k.f >= at) k.f = Math.max(at, k.f + d);
  shiftKeys(doc, at, d);
}

/** 모션 경로를 그림으로 굳히기: 움직이는 프레임마다 옮겨 그린 새 그림을 만들고 모션 키는 지움 */
export function bakeMotion(track = animFolder()) {
  const doc = app.doc;
  if (!track || !motionOf(track)?.keys?.length) { app.toast('이 트랙에 모션 키가 없습니다'); return 0; }
  let made = 0;
  editAnim('모션 굳히기', () => {
    const xs = track.meta.xs, cache = new Map();
    for (let f = 0; f < xs.length; f++) {
      const id = xs[f], mo = motionAt(track, f, doc);
      if (!id || !mo) continue;
      const key = id + '|' + mo.T.toString() + '|' + mo.alpha.toFixed(3);
      if (cache.has(key)) { xs[f] = cache.get(key); continue; }
      const src = doc.byId(id), l = newCel(track, doc);
      l.ctx.save(); l.ctx.globalAlpha = mo.alpha; l.ctx.setTransform(mo.T); l.ctx.drawImage(src.canvas, 0, 0); l.ctx.restore();
      l.touch();
      cache.set(key, l.id); xs[f] = l.id; made++;
    }
    delete track.meta.motion;
    dropUnused(doc);
  });
  return made;
}

/** 지금 프레임을 모든 트랙에서 지우기 */
export function deleteFrame() {
  const doc = app.doc, n = frameCount(doc), f = doc.anim.frame;
  if (n <= 1) { app.toast('프레임이 하나뿐입니다'); return; }
  editAnim('프레임 삭제', () => {
    for (const t of tracks(doc)) t.meta.xs.splice(f, 1);
    doc.anim.ms?.splice(f, 1);
    doc.anim.tags = (doc.anim.tags || []).filter(t => !(t.from === f && t.to === f));
    shiftAfter(doc, f + 1, -1);
    doc.anim.length = n - 1;
    dropUnused(doc);
  });
  setFrame(Math.min(f, frameCount(doc) - 1));
}
export const deleteCel = deleteFrame;

/** 지금 트랙의 지금 칸을 비우기 (그 프레임엔 아무것도 안 보임) */
export function clearCell() {
  const doc = app.doc, t = animFolder(doc), f = doc.anim.frame;
  if (!t) return;
  editAnim('칸 비우기', () => { t.meta.xs[f] = null; dropUnused(doc); });
  doc.setActive(t);
}

/** 빈 칸에 새 셀 만들기 */
export function newCelHere(track = animFolder(), f = app.doc.anim.frame) {
  const doc = app.doc;
  if (!track) return;
  editAnim('새 셀', () => { const c = newCel(track, doc); track.meta.xs[f] = c.id; doc.setActive(c); });
}

/** 연결 끊기: 같은 셀을 쓰던 다른 칸과 따로 고칠 수 있게 복제 (이어진 구간째) */
export function unlinkCell() {
  const doc = app.doc, t = animFolder(doc), f = doc.anim.frame;
  const cur = t && celAt(t, f, doc);
  if (!cur) return;
  editAnim('연결 끊기', () => {
    const c = newCel(t, doc, cur);
    const xs = t.meta.xs;
    let a = f, b = f;
    while (a > 0 && xs[a - 1] === cur.id) a--;
    while (b < xs.length - 1 && xs[b + 1] === cur.id) b++;
    for (let i = a; i <= b; i++) xs[i] = c.id;
    doc.setActive(c);
  });
}

/** 지금 칸을 다른 셀로 연결 (그 셀과 같은 그림이 보임) */
export function linkCellTo(celId) {
  const doc = app.doc, t = animFolder(doc), f = doc.anim.frame;
  if (!t) return;
  editAnim('칸 연결', () => { t.meta.xs[f] = celId; dropUnused(doc); doc.setActive(doc.byId(celId)); });
}

/** 지금 트랙에서 셀이 이어 보이는 장수 (길게 보여 주기) */
export function holdOf(i, doc = app.doc) {
  const t = animFolder(doc);
  if (!t) return 1;
  const xs = xsOf(t, doc), id = xs[i];
  if (!id) return 1;
  let a = i, b = i;
  while (a > 0 && xs[a - 1] === id) a--;
  while (b < xs.length - 1 && xs[b + 1] === id) b++;
  return b - a + 1;
}

/** 지금 칸의 셀을 n 장 동안 보이게 (뒤 칸들은 밀리거나 당겨짐 — 이 트랙만) */
export function setHold(i, n) {
  const doc = app.doc, t = animFolder(doc);
  if (!t) return;
  editAnim('프레임 길이', () => {
    const xs = t.meta.xs, id = xs[i];
    if (!id) return;
    let a = i, b = i;
    while (a > 0 && xs[a - 1] === id) a--;
    while (b < xs.length - 1 && xs[b + 1] === id) b++;
    const len = b - a + 1;
    if (n > len) xs.splice(b + 1, 0, ...Array(n - len).fill(id));
    else if (n < len) xs.splice(a + n, len - n);
    // 다른 트랙이 더 길면 그대로, 이 트랙이 짧아졌으면 끝을 빈 칸으로
  });
}

export function moveCel(dir) {
  const doc = app.doc, t = animFolder(doc), f = doc.anim.frame, g = f + dir;
  if (!t || g < 0 || g >= frameCount(doc)) return;
  editAnim('칸 옮기기', () => { const xs = t.meta.xs; [xs[f], xs[g]] = [xs[g], xs[f]]; });
  setFrame(g);
}

/** 프레임 길이를 ms 로 (null = fps 따라감) */
export function setFrameMs(f, ms) {
  const doc = app.doc;
  editAnim('프레임 시간', () => {
    const a = doc.anim.ms ||= [];
    while (a.length <= f) a.push(null);
    a[f] = ms ? Math.max(10, Math.round(ms)) : null;
    if (a.every(v => !v)) delete doc.anim.ms;
  });
}

// ---------------------------------------------------------------------
// 태그 (구간): { name, from, to, dir: 'fwd'|'rev'|'ping', color }
// ---------------------------------------------------------------------
export function addTag(tag) { editAnim('태그 추가', () => { (app.doc.anim.tags ||= []).push({ color: '#3d7be0', dir: 'fwd', ...tag }); }); }
export function updateTag(i, props) { editAnim('태그 고치기', () => { Object.assign(app.doc.anim.tags[i], props); }); }
export function removeTag(i) { editAnim('태그 지우기', () => { app.doc.anim.tags.splice(i, 1); if (app.doc.anim.playTag === i) app.doc.anim.playTag = null; }); }

/** 재생 순서 (태그를 고르면 그 구간만, 방향대로) */
export function playOrder(doc = app.doc) {
  const n = frameCount(doc);
  const tag = doc.anim.playTag != null ? doc.anim.tags?.[doc.anim.playTag] : null;
  const from = tag ? Math.max(0, tag.from) : 0, to = tag ? Math.min(n - 1, tag.to) : n - 1;
  const fwd = []; for (let f = from; f <= to; f++) fwd.push(f);
  const dir = tag?.dir || doc.anim.dir || 'fwd';
  if (dir === 'rev') return fwd.reverse();
  if (dir === 'ping') return fwd.length > 2 ? [...fwd, ...fwd.slice(1, -1).reverse()] : fwd;
  return fwd;
}

// ---------------------------------------------------------------------
// 재생
// ---------------------------------------------------------------------
let timer = 0;
export function play() {
  const doc = app.doc;
  if (frameCount(doc) < 2) { app.toast('프레임이 2개 이상이어야 재생됩니다'); return; }
  if (doc.anim.playing) { stop(); return; }
  doc.anim.playing = true;
  const order = playOrder(doc);
  let k = Math.max(0, order.indexOf(doc.anim.frame)), last = performance.now();
  const times = timeline(doc);
  const startSound = () => { if (doc.anim.audio) playAudio(times[order[k]]); };
  doc.anim.frame = order[k];
  startSound();
  const tick = (now) => {
    if (!doc.anim.playing) return;
    if (now - last >= frameMs(order[k], doc)) {
      last = now;
      if (k >= order.length - 1 && !doc.anim.loop) { stop(); return; }
      k = (k + 1) % order.length;
      setFrame(order[k], { select: false });
      if (k === 0) startSound();
    }
    timer = requestAnimationFrame(tick);
  };
  timer = requestAnimationFrame(tick);
  emit();
}

export function stop() {
  const doc = app.doc;
  cancelAnimationFrame(timer);
  stopAudio();
  if (!doc) return;
  doc.anim.playing = false;
  setFrame(doc.anim.frame);
}

// ---------------------------------------------------------------------
// 내보내기 (프레임 순서는 태그·방향을 따름)
// ---------------------------------------------------------------------
export function frameCanvas(i, scale, bg, doc = app.doc) {
  // 카메라 키가 있으면 그 영역만 확대·회전해서
  // bg === null: 투명 (종이도 빼고)
  return applyCamera(doc, i, app.compositor.flatten(doc, { scale, frame: i, paper: bg !== null, background: bg === null ? undefined : (bg || '#ffffff') }));
}

async function save(name, blob) {
  if (!server.online) { downloadBlob(blob, name.split('/').pop()); return; }
  await server.put('exports', name, blob);
}

/** 내보낼 프레임 순서: { order, tagName } — 태그를 골랐으면 그 구간 */
export function exportOrder(tagIndex = null, doc = app.doc) {
  const keep = doc.anim.playTag;
  doc.anim.playTag = tagIndex;
  const order = playOrder(doc);
  doc.anim.playTag = keep;
  return { order, tag: tagIndex != null ? doc.anim.tags?.[tagIndex] : null };
}

export async function exportPNGs(scale = 1, tagIndex = null) {
  const doc = app.doc, { order } = exportOrder(tagIndex, doc);
  const folder = `${safeName(doc.name)}_프레임_${fileStamp()}`;
  // 프레임 길이(ms)가 다르면 fps 기준 장수만큼 같은 그림을 이어서 저장 (영상 편집 프로그램에서 그대로 쓰도록)
  const base = 1000 / (doc.anim.fps || 12);
  let k = 0;
  for (const f of order) {
    const blob = await canvasToBlob(frameCanvas(f, scale));
    for (let r = Math.max(1, Math.round(frameMs(f, doc) / base)); r > 0; r--) await save(`${folder}/${String(++k).padStart(4, '0')}.png`, blob);
  }
  app.toast(`${k}장 저장: data/exports/${folder}`);
}

export async function exportWebM(scale = 1, tagIndex = null, { mime = null, ext = 'webm' } = {}) {
  const doc = app.doc, { order } = exportOrder(tagIndex, doc);
  const c = createCanvas(Math.round(doc.width * scale / 2) * 2, Math.round(doc.height * scale / 2) * 2);
  const x = c.getContext('2d');
  const stream = c.captureStream(0);
  const track = stream.getVideoTracks()[0];
  // 소리가 있으면 동영상에 함께 (한 번만 재생, 태그 구간이면 소리 없이)
  const snd = doc.anim.audio && tagIndex == null ? await audioStream(0) : null;
  if (snd) for (const t of snd.stream.getAudioTracks()) stream.addTrack(t);
  // 소리가 있을 때 'vp9,opus' 를 직접 지정하면 빈 파일이 나오는 브라우저가 있어, 형식은 브라우저에 맡김
  const type = mime || (snd ? 'video/webm' : MediaRecorder.isTypeSupported('video/webm;codecs=vp9') ? 'video/webm;codecs=vp9' : 'video/webm');
  const rec = new MediaRecorder(stream, { mimeType: type, videoBitsPerSecond: 8e6 });
  const chunks = [];
  rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
  const done = new Promise(r => { rec.onstop = r; });
  rec.start();
  snd?.start();
  const total = order.reduce((s, f) => s + frameMs(f, doc), 0);
  const loops = snd ? 1 : Math.max(1, Math.ceil(2000 / Math.max(1, total)));   // 짧으면 2초 이상 되도록 반복
  for (let k = 0; k < loops; k++) {
    for (const f of order) {
      x.fillStyle = '#fff'; x.fillRect(0, 0, c.width, c.height);
      x.drawImage(frameCanvas(f, scale), 0, 0, c.width, c.height);
      // 창이 가려지면 브라우저가 녹화를 멈추므로 다시 보일 때까지 기다림
      if (document.hidden) { rec.pause?.(); app.toast('창이 가려져 동영상 만들기를 잠시 멈췄습니다. 창을 다시 보이게 하면 이어집니다', 'warn'); while (document.hidden) await new Promise(r => setTimeout(r, 300)); rec.resume?.(); }
      track.requestFrame?.();
      await new Promise(r => setTimeout(r, frameMs(f, doc)));
    }
  }
  snd?.stop();
  rec.stop();
  await done;
  const blob = new Blob(chunks, { type: type.split(';')[0] });
  const name = `${safeName(doc.name)}_${fileStamp()}.${ext}`;
  await save(name, blob);
  app.toast(`동영상 저장: data/exports/${name}`);
}

// ---- GIF 인코더 (프레임마다 최적 256색 팔레트: 미디언 컷 + 오차 확산) ----
const k15 = (r, g, b) => ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);

export function buildPalette(data, maxColors = 256) {
  const n = data.length / 4;
  const step = Math.max(1, Math.floor(n / 120000));
  const hist = new Map();
  for (let i = 0; i < n; i += step) {
    const p = i * 4;
    if (data[p + 3] < 128) continue;                      // 투명한 곳은 팔레트에 넣지 않음
    const k = k15(data[p], data[p + 1], data[p + 2]);
    hist.set(k, (hist.get(k) || 0) + 1);
  }
  const cols = [...hist].map(([k, c]) => ({ r: ((k >> 10) & 31) * 8 + 4, g: ((k >> 5) & 31) * 8 + 4, b: (k & 31) * 8 + 4, c }));
  let boxes = [cols];
  const range = (box, ch) => { let lo = 255, hi = 0; for (const x of box) { if (x[ch] < lo) lo = x[ch]; if (x[ch] > hi) hi = x[ch]; } return hi - lo; };
  if (!cols.length) cols.push({ r: 255, g: 255, b: 255, c: 1 });
  while (boxes.length < Math.max(2, Math.min(256, maxColors))) {
    // 가장 넓고 픽셀 많은 상자를 자른다
    let best = -1, bestScore = 0, bestCh = 'r';
    boxes.forEach((box, i) => {
      if (box.length < 2) return;
      const count = box.reduce((s2, x) => s2 + x.c, 0);
      for (const ch of ['r', 'g', 'b']) { const sc = range(box, ch) * Math.sqrt(count); if (sc > bestScore) { bestScore = sc; best = i; bestCh = ch; } }
    });
    if (best < 0) break;
    const box = boxes[best].sort((x, y) => x[bestCh] - y[bestCh]);
    const total = box.reduce((s2, x) => s2 + x.c, 0);
    let acc = 0, cut = 1;
    for (let i = 0; i < box.length - 1; i++) { acc += box[i].c; if (acc >= total / 2) { cut = i + 1; break; } }
    boxes.splice(best, 1, box.slice(0, cut), box.slice(cut));
  }
  const pal = new Uint8Array(256 * 3);
  boxes.forEach((box, i) => {
    let r = 0, g = 0, b = 0, c = 0;
    for (const x of box) { r += x.r * x.c; g += x.g * x.c; b += x.b * x.c; c += x.c; }
    pal[i * 3] = Math.round(r / c); pal[i * 3 + 1] = Math.round(g / c); pal[i * 3 + 2] = Math.round(b / c);
  });
  return { pal, size: boxes.length };
}

export function quantize(data, w, h, P, { dither = true, transparent = -1 } = {}) {
  const { pal, size } = P;
  const cache = new Int16Array(32768).fill(-1);
  const nearest = (r, g, b) => {
    const k = k15(r, g, b);
    if (cache[k] >= 0) return cache[k];
    let bi = 0, bd = Infinity;
    for (let i = 0; i < size; i++) {
      const dr = pal[i * 3] - r, dg = pal[i * 3 + 1] - g, db = pal[i * 3 + 2] - b;
      const d = dr * dr * 2 + dg * dg * 4 + db * db * 3;
      if (d < bd) { bd = d; bi = i; }
    }
    cache[k] = bi;
    return bi;
  };
  const out = new Uint8Array(w * h);
  const err = new Float32Array(w * h * 3);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = y * w + x, p = i * 4;
    if (transparent >= 0 && data[p + 3] < 128) { out[i] = transparent; continue; }
    const r = Math.max(0, Math.min(255, data[p] + err[i * 3]));
    const g = Math.max(0, Math.min(255, data[p + 1] + err[i * 3 + 1]));
    const b = Math.max(0, Math.min(255, data[p + 2] + err[i * 3 + 2]));
    const ci = nearest(r | 0, g | 0, b | 0);
    out[i] = ci;
    if (!dither) continue;
    const er = r - pal[ci * 3], eg = g - pal[ci * 3 + 1], eb = b - pal[ci * 3 + 2];
    const spread = (dx, dy, f) => {
      const xx = x + dx, yy = y + dy;
      if (xx < 0 || xx >= w || yy >= h) return;
      const j = (yy * w + xx) * 3;
      err[j] += er * f; err[j + 1] += eg * f; err[j + 2] += eb * f;
    };
    spread(1, 0, 7 / 16); spread(-1, 1, 3 / 16); spread(0, 1, 5 / 16); spread(1, 1, 1 / 16);
  }
  return out;
}

export function lzw(indices, minSize = 8) {
  const clear = 1 << minSize, eoi = clear + 1;
  let size = minSize + 1, next = eoi + 1;
  let dict = new Map();
  const out = [];
  let cur = 0, bits = 0;
  const emit = (code) => { cur |= code << bits; bits += size; while (bits >= 8) { out.push(cur & 255); cur >>>= 8; bits -= 8; } };
  emit(clear);
  let prefix = indices[0];
  for (let i = 1; i < indices.length; i++) {
    const k = indices[i], key = (prefix << 8) | k;
    const hit = dict.get(key);
    if (hit !== undefined) { prefix = hit; continue; }
    emit(prefix);
    if (next === 4096) { emit(clear); dict = new Map(); size = minSize + 1; next = eoi + 1; }
    else { if (next >= (1 << size)) size++; dict.set(key, next++); }
    prefix = k;
  }
  emit(prefix);
  emit(eoi);
  if (bits > 0) out.push(cur & 255);
  return out;
}

/**
 * GIF 만들기. o: { scale, tag, loop (0 = 무한, n = n번), dither, colors (2~256), transparent (배경 투명), frames (미리보기용 일부만) }
 */
export async function encodeGIF(o = {}, doc = app.doc) {
  const scale = o.scale ?? 0.5, { order } = exportOrder(o.tag ?? null, doc);
  const list = o.frames ? order.slice(0, o.frames) : order;
  const w = Math.max(1, Math.round(doc.width * scale)), hh = Math.max(1, Math.round(doc.height * scale));
  const colors = Math.max(2, Math.min(256, o.colors || 256)), transp = !!o.transparent;
  const bytes = [];
  const u16 = (v) => bytes.push(v & 255, (v >> 8) & 255);
  const str = (s) => { for (const c of s) bytes.push(c.charCodeAt(0)); };
  str('GIF89a'); u16(w); u16(hh); bytes.push(0x70, 0, 0);   // 전역 팔레트 없음 (프레임마다 자기 팔레트)
  const loop = o.loop ?? (doc.anim.loop ? 0 : 1);
  if (loop !== 1) { bytes.push(0x21, 0xff, 0x0b); str('NETSCAPE2.0'); bytes.push(3, 1); u16(loop === 0 ? 0 : loop - 1); bytes.push(0); }
  for (const f of list) {
    const fc = frameCanvas(f, scale, transp ? null : '#ffffff', doc);
    const data = fc.getContext('2d').getImageData(0, 0, w, hh).data;
    const P = buildPalette(data, transp ? colors - 1 : colors);
    const ti = transp ? P.size : -1;
    if (transp) P.size = Math.min(256, P.size);
    const idx = quantize(data, w, hh, P, { dither: o.dither !== false, transparent: ti });
    // 그래픽 제어: 투명이면 다음 프레임 전에 지우기(2) + 투명색 번호
    bytes.push(0x21, 0xf9, 4, transp ? 0x09 : 0x04); u16(Math.max(2, Math.round(frameMs(f, doc) / 10))); bytes.push(transp ? ti : 0, 0);
    bytes.push(0x2c); u16(0); u16(0); u16(w); u16(hh); bytes.push(0x87);
    for (let k = 0; k < 256; k++) bytes.push(P.pal[k * 3] || 0, P.pal[k * 3 + 1] || 0, P.pal[k * 3 + 2] || 0);
    bytes.push(8);
    const lz = lzw(idx, 8);
    for (let p = 0; p < lz.length; p += 255) {
      const len = Math.min(255, lz.length - p);
      bytes.push(len);
      for (let k = 0; k < len; k++) bytes.push(lz[p + k]);
    }
    bytes.push(0);
    await new Promise(r => setTimeout(r, 0));
  }
  bytes.push(0x3b);
  return new Blob([new Uint8Array(bytes)], { type: 'image/gif' });
}

export async function exportGIF(scale = 0.5, tagIndex = null, o = {}) {
  const doc = app.doc;
  const blob = await encodeGIF({ ...o, scale, tag: tagIndex }, doc);
  const name = `${safeName(doc.name)}_${fileStamp()}.gif`;
  await save(name, blob);
  app.toast(`GIF 저장: data/exports/${name} (${(blob.size / 1024).toFixed(0)}KB)`);
  return blob;
}
export const saveExport = save;

// ---------------------------------------------------------------------
// 예전 파일 옮기기: 노출표가 없는 트랙 → 노출표, 카메라 키(셀 번호) → 프레임 번호
// ---------------------------------------------------------------------
export function migrate(doc = app.doc) {
  if (!doc || doc.anim.v >= 2) return;
  const ts = tracks(doc);
  if (ts.length && ts.some(t => !Array.isArray(t.meta?.xs))) {
    const first = ts[0], kids = doc.childrenOf(first);
    // 카메라 키는 예전엔 "몇 번째 셀" 기준이었음 → 그 셀이 시작하는 프레임으로
    if (doc.anim.camera?.keys?.length) {
      const start = []; let t = 0;
      for (const k of kids) { start.push(t); t += Math.max(1, Math.round(doc.anim.holds?.[k.id] || 1)); }
      for (const key of doc.anim.camera.keys) key.f = start[Math.min(start.length - 1, key.f)] ?? key.f;
    }
    for (const tr of ts) if (!Array.isArray(tr.meta?.xs)) tr.meta = { ...(tr.meta || {}), xs: xsOf(tr, doc) };
    doc.anim.holds = {};
    doc.anim.length = Math.max(...ts.map(tr => tr.meta.xs.length));
    for (const tr of ts) while (tr.meta.xs.length < doc.anim.length) tr.meta.xs.push(tr.meta.xs.at(-1) ?? null);
  }
  doc.anim.v = 2;
}
