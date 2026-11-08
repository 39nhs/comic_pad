// 애니메이션 보조: 소리(배경음·대사 녹음)와 카메라 이동(팬·줌·회전)
import { app } from '../app.js';
import { createCanvas } from './util.js';

// ---------------------------------------------------------------------
// 시간: 프레임 길이(홀드)를 반영한 각 프레임의 시작 시각(초)
// ---------------------------------------------------------------------
export function frameTimes(doc, list, holdOf) {
  const fps = doc.anim.fps || 12, out = [];
  let t = 0;
  for (let i = 0; i < list.length; i++) { out.push(t); t += holdOf(i, doc) / fps; }
  out.push(t);
  return out;
}

// ---------------------------------------------------------------------
// 소리
// ---------------------------------------------------------------------
let actx = null, source = null;
const ctx = () => (actx ||= new AudioContext());

export async function audioBuffer(doc = app.doc) {
  const a = doc.anim.audio;
  if (!a?.data) return null;
  if (doc._audio?.key === a.data.length + ':' + a.name) return doc._audio.buf;
  const bin = await (await fetch(a.data)).arrayBuffer();
  const buf = await ctx().decodeAudioData(bin);
  doc._audio = { key: a.data.length + ':' + a.name, buf, peaks: null };
  return buf;
}

/** 소리 넣기 (파일 → 작품 안에 함께 저장) */
export async function setAudio(file) {
  const doc = app.doc;
  if (file.size > 30 * 1048576) { app.toast('소리 파일이 너무 큽니다 (30MB 까지)', 'warn'); return; }
  const data = await new Promise((res, rej) => { const r = new FileReader(); r.onload = () => res(r.result); r.onerror = rej; r.readAsDataURL(file); });
  const prev = doc.anim.audio || null;
  const next = { name: file.name, data, offset: 0, volume: 1 };
  const apply = (v) => { doc.anim.audio = v; doc._audio = null; app.markModified(); app.bus.emit('anim:changed'); };
  apply(next);
  try { await audioBuffer(doc); } catch { apply(prev); app.toast('이 소리 파일은 읽을 수 없습니다 (mp3·wav·ogg·m4a)', 'error'); return; }
  app.history.push({ label: '소리 넣기', undo: () => apply(prev), redo: () => apply(next) });
  app.toast(`소리: ${file.name} (${(await audioBuffer(doc)).duration.toFixed(1)}초)`);
}

export function removeAudio() {
  const doc = app.doc, prev = doc.anim.audio;
  if (!prev) return;
  const apply = (v) => { doc.anim.audio = v; doc._audio = null; app.markModified(); app.bus.emit('anim:changed'); };
  apply(null);
  app.history.push({ label: '소리 빼기', undo: () => apply(prev), redo: () => apply(null) });
}

/** t 초부터 재생 (dur 를 주면 그만큼만: 프레임 넘길 때 잠깐 듣기) */
export async function playAudio(t, dur) {
  stopAudio();
  const doc = app.doc, a = doc.anim.audio;
  if (!a) return;
  const buf = await audioBuffer(doc).catch(() => null);
  if (!buf) return;
  const at = t - (a.offset || 0);
  if (at >= buf.duration) return;
  const c = ctx();
  if (c.state === 'suspended') await c.resume();
  const g = c.createGain(); g.gain.value = a.volume ?? 1; g.connect(c.destination);
  source = c.createBufferSource(); source.buffer = buf; source.connect(g);
  const when = at < 0 ? c.currentTime - at : c.currentTime;
  source.start(when, Math.max(0, at), dur ? Math.max(0.02, dur) : undefined);
}

export function stopAudio() { try { source?.stop(); } catch { /* 이미 멈춤 */ } source = null; }

/** 프레임마다 소리 크기 (입 모양 맞추기용, 0~1) */
export async function framePeaks(doc, times) {
  const buf = await audioBuffer(doc).catch(() => null);
  if (!buf) return null;
  const d = buf.getChannelData(0), sr = buf.sampleRate, off = doc.anim.audio.offset || 0;
  const out = [];
  for (let i = 0; i < times.length - 1; i++) {
    const a = Math.max(0, Math.floor((times[i] - off) * sr)), b = Math.min(d.length, Math.floor((times[i + 1] - off) * sr));
    let m = 0;
    for (let k = a; k < b; k += 16) { const v = Math.abs(d[k]); if (v > m) m = v; }
    out.push(m);
  }
  return out;
}

/** 녹화용: 소리를 MediaStream 으로 (동영상에 함께 담기) */
export async function audioStream(startAt = 0) {
  const doc = app.doc, a = doc.anim.audio;
  const buf = a && await audioBuffer(doc).catch(() => null);
  if (!buf) return null;
  const c = ctx();
  if (c.state === 'suspended') await c.resume();
  const dest = c.createMediaStreamDestination();
  const g = c.createGain(); g.gain.value = a.volume ?? 1; g.connect(dest);
  const s = c.createBufferSource(); s.buffer = buf; s.connect(g);
  return { stream: dest.stream, start: () => { const at = startAt - (a.offset || 0); s.start(c.currentTime + Math.max(0, -at), Math.max(0, at)); }, stop: () => { try { s.stop(); } catch { /* 무시 */ } } };
}

// ---------------------------------------------------------------------
// 카메라: 프레임마다 보이는 영역 (가운데 x,y = 0~1, 확대 z ≥ 1, 회전 r°)
// ---------------------------------------------------------------------
const ease = (t) => t * t * (3 - 2 * t);

export function cameraAt(doc, i) {
  const keys = (doc.anim.camera?.keys || []).slice().sort((a, b) => a.f - b.f);
  if (!keys.length) return null;
  if (i <= keys[0].f) return { ...keys[0] };
  if (i >= keys.at(-1).f) return { ...keys.at(-1) };
  const k = keys.findIndex(c => c.f > i), A = keys[k - 1], B = keys[k];
  let t = (i - A.f) / (B.f - A.f);
  if (doc.anim.camera.ease !== false) t = ease(t);
  return { f: i, x: A.x + (B.x - A.x) * t, y: A.y + (B.y - A.y) * t, z: A.z + (B.z - A.z) * t, r: A.r + (B.r - A.r) * t };
}

/** 카메라를 적용한 프레임 그림 (출력 크기는 원고와 같은 비율) */
export function applyCamera(doc, i, src) {
  const cam = cameraAt(doc, i);
  if (!cam || (cam.z === 1 && cam.x === 0.5 && cam.y === 0.5 && !cam.r)) return src;
  const c = createCanvas(src.width, src.height), x = c.getContext('2d');
  x.fillStyle = '#fff'; x.fillRect(0, 0, c.width, c.height);
  x.imageSmoothingQuality = 'high';
  x.translate(c.width / 2, c.height / 2);
  x.scale(cam.z, cam.z);
  x.rotate(-(cam.r || 0) * Math.PI / 180);
  x.translate(-cam.x * src.width, -cam.y * src.height);
  x.drawImage(src, 0, 0);
  return c;
}

/** 원고 위에 지금 프레임의 카메라 영역 표시 */
export function drawCameraOverlay(ctx2, view) {
  const doc = app.doc;
  if (!doc?.anim?.camera?.keys?.length || app.settings.showCamera === false) return;
  const cam = cameraAt(doc, doc.anim.frame || 0);
  if (!cam) return;
  const w = doc.width / cam.z, hgt = doc.height / cam.z, a = (cam.r || 0) * Math.PI / 180;
  const pts = [[-w / 2, -hgt / 2], [w / 2, -hgt / 2], [w / 2, hgt / 2], [-w / 2, hgt / 2]].map(([px, py]) => {
    const rx = px * Math.cos(a) - py * Math.sin(a), ry = px * Math.sin(a) + py * Math.cos(a);
    return view.toScreen(cam.x * doc.width + rx, cam.y * doc.height + ry);
  });
  ctx2.strokeStyle = '#e5484d'; ctx2.lineWidth = 2; ctx2.setLineDash([8, 5]);
  ctx2.beginPath(); pts.forEach((p, k) => (k ? ctx2.lineTo(p.x, p.y) : ctx2.moveTo(p.x, p.y))); ctx2.closePath(); ctx2.stroke();
  ctx2.setLineDash([]);
  ctx2.fillStyle = '#e5484d'; ctx2.font = '12px sans-serif';
  ctx2.fillText(`카메라 ${Math.round(cam.z * 100)}%`, pts[0].x + 4, pts[0].y + 14);
}

export function setCameraKey(doc, i, vals) {
  const prev = structuredClone(doc.anim.camera || null);
  const cam = structuredClone(doc.anim.camera || { keys: [], ease: true });
  const k = cam.keys.find(c => c.f === i);
  const base = cameraAt(doc, i) || { x: 0.5, y: 0.5, z: 1, r: 0 };
  if (k) Object.assign(k, vals); else cam.keys.push({ f: i, x: base.x, y: base.y, z: base.z, r: base.r || 0, ...vals });
  const apply = (v) => { doc.anim.camera = v; app.markModified(); app.bus.emit('anim:changed'); app.requestRender(); };
  apply(cam);
  app.history.push({ label: '카메라 키', undo: () => apply(prev), redo: () => apply(cam) });
}

export function removeCameraKey(doc, i) {
  if (!doc.anim.camera?.keys?.some(c => c.f === i)) return;
  const prev = structuredClone(doc.anim.camera);
  const cam = { ...structuredClone(prev), keys: prev.keys.filter(c => c.f !== i) };
  const apply = (v) => { doc.anim.camera = v; app.markModified(); app.bus.emit('anim:changed'); app.requestRender(); };
  apply(cam);
  app.history.push({ label: '카메라 키 지우기', undo: () => apply(prev), redo: () => apply(cam) });
}
