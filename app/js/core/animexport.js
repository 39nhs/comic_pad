// 움짤 형식: APNG (색 손실 없음 + 투명) · 움직이는 WebP (작고 선명) · MP4 (SNS 용). GIF 는 anim.js.
import { app } from '../app.js';
import { canvasToBlob, safeName, fileStamp } from './util.js';
import { crc32 } from './zip.js';
import * as A from './anim.js';

const be32 = (v) => [(v >>> 24) & 255, (v >>> 16) & 255, (v >>> 8) & 255, v & 255];
const be16 = (v) => [(v >> 8) & 255, v & 255];
const le24 = (v) => [v & 255, (v >> 8) & 255, (v >> 16) & 255];
const le32 = (v) => [v & 255, (v >> 8) & 255, (v >> 16) & 255, (v >>> 24) & 255];
const ascii = (s) => [...s].map(c => c.charCodeAt(0));

/** 내보낼 프레임 그림들 (태그·방향 순서, 크기, 배경) */
function frames(o, doc) {
  const { order } = A.exportOrder(o.tag ?? null, doc);
  const list = o.frames ? order.slice(0, o.frames) : order;
  return list.map(f => ({ f, canvas: A.frameCanvas(f, o.scale ?? 0.5, o.transparent ? null : '#ffffff', doc), ms: Math.round(A.frameMs(f, doc)) }));
}

// ---------------------------------------------------------------------
// APNG: PNG 덩어리(IHDR·IDAT)를 꺼내 acTL / fcTL / fdAT 로 다시 묶는다
// ---------------------------------------------------------------------
function pngChunks(u8) {
  const v = new DataView(u8.buffer, u8.byteOffset, u8.byteLength), out = [];
  let p = 8;
  while (p < u8.length) {
    const len = v.getUint32(p), type = String.fromCharCode(...u8.subarray(p + 4, p + 8));
    out.push({ type, data: u8.subarray(p + 8, p + 8 + len) });
    p += 12 + len;
  }
  return out;
}
function chunk(type, data) {
  const body = new Uint8Array([...ascii(type), ...data]);
  return [...be32(data.length), ...body, ...be32(crc32(body))];
}

export async function encodeAPNG(o = {}, doc = app.doc) {
  const list = frames(o, doc);
  if (!list.length) throw new Error('프레임이 없습니다');
  const w = list[0].canvas.width, h = list[0].canvas.height;
  const parts = [[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]];
  let seq = 0;
  for (let i = 0; i < list.length; i++) {
    const png = new Uint8Array(await (await canvasToBlob(list[i].canvas, 'image/png')).arrayBuffer());
    const ch = pngChunks(png);
    if (i === 0) {
      parts.push(chunk('IHDR', ch.find(c => c.type === 'IHDR').data));
      parts.push(chunk('acTL', [...be32(list.length), ...be32(o.loop ?? 0)]));      // 반복 횟수 (0 = 무한)
    }
    // 프레임 머리: 크기·위치·시간(ms/1000)·지우기(배경으로)·덮어쓰기
    parts.push(chunk('fcTL', [...be32(seq++), ...be32(w), ...be32(h), ...be32(0), ...be32(0), ...be16(Math.min(65535, list[i].ms)), ...be16(1000), o.transparent ? 1 : 0, 0]));
    for (const c of ch.filter(c => c.type === 'IDAT')) {
      if (i === 0) parts.push(chunk('IDAT', c.data));
      else parts.push(chunk('fdAT', [...be32(seq++), ...c.data]));
    }
    await new Promise(r => setTimeout(r, 0));
  }
  parts.push(chunk('IEND', []));
  return new Blob(parts.map(p => new Uint8Array(p)), { type: 'image/apng' });
}

// ---------------------------------------------------------------------
// 움직이는 WebP: 브라우저가 만든 정지 WebP 를 ANIM / ANMF 로 묶는다
// ---------------------------------------------------------------------
function riffChunks(u8) {
  const v = new DataView(u8.buffer, u8.byteOffset, u8.byteLength), out = [];
  let p = 12;
  while (p + 8 <= u8.length) {
    const type = String.fromCharCode(...u8.subarray(p, p + 4)), len = v.getUint32(p + 4, true);
    out.push({ type, data: u8.subarray(p + 8, p + 8 + len) });
    p += 8 + len + (len & 1);
  }
  return out;
}
const rchunk = (type, data) => [...ascii(type), ...le32(data.length), ...data, ...(data.length & 1 ? [0] : [])];

export async function encodeWebP(o = {}, doc = app.doc) {
  const list = frames(o, doc);
  if (!list.length) throw new Error('프레임이 없습니다');
  const w = list[0].canvas.width, h = list[0].canvas.height, q = o.quality ?? 0.9;
  let hasAlpha = false;
  const anmf = [];
  for (const fr of list) {
    const blob = await canvasToBlob(fr.canvas, 'image/webp', q);
    if (blob.type !== 'image/webp') throw new Error('이 브라우저는 WebP 를 만들 수 없습니다');
    const ch = riffChunks(new Uint8Array(await blob.arrayBuffer()));
    // 그림 데이터만 (ALPH + VP8, 또는 VP8L)
    const img = ch.filter(c => c.type === 'ALPH' || c.type === 'VP8 ' || c.type === 'VP8L');
    if (img.some(c => c.type === 'ALPH' || c.type === 'VP8L')) hasAlpha = hasAlpha || !!o.transparent;
    const payload = [...le24(0), ...le24(0), ...le24(w - 1), ...le24(h - 1), ...le24(Math.min(0xffffff, fr.ms)), 0x02];   // 앞 프레임과 섞지 않고 통째로 바꿈
    for (const c of img) payload.push(...rchunk(c.type, c.data));
    anmf.push(rchunk('ANMF', payload));
    await new Promise(r => setTimeout(r, 0));
  }
  const vp8x = rchunk('VP8X', [0x02 | (hasAlpha || o.transparent ? 0x10 : 0), 0, 0, 0, ...le24(w - 1), ...le24(h - 1)]);
  const loop = o.loop ?? 0;
  const anim = rchunk('ANIM', [255, 255, 255, o.transparent ? 0 : 255, loop & 255, (loop >> 8) & 255]);   // 배경색(BGRA), 반복 횟수 (0 = 무한)
  const body = [...ascii('WEBP'), ...vp8x, ...anim, ...anmf.flat()];
  return new Blob([new Uint8Array([...ascii('RIFF'), ...le32(body.length), ...body])], { type: 'image/webp' });
}

// ---------------------------------------------------------------------
// 내보내기 공통
// ---------------------------------------------------------------------
export const FORMATS = {
  gif: { label: 'GIF (어디서나 됨)', ext: 'gif' },
  webp: { label: 'WebP (작고 선명, 투명 가능)', ext: 'webp' },
  apng: { label: 'APNG (색 그대로·투명, 큼)', ext: 'png' },
  mp4: { label: 'MP4 동영상 (SNS 용)', ext: 'mp4' },
  webm: { label: 'WebM 동영상', ext: 'webm' },
  png: { label: 'PNG 연번 (프레임마다 파일)', ext: 'png' },
};
export const mp4Type = () => ['video/mp4;codecs=avc1.42E01E', 'video/mp4;codecs=avc1', 'video/mp4'].find(t => window.MediaRecorder?.isTypeSupported?.(t)) || null;

export async function encode(fmt, o, doc = app.doc) {
  if (fmt === 'gif') return A.encodeGIF(o, doc);
  if (fmt === 'apng') return encodeAPNG(o, doc);
  if (fmt === 'webp') return encodeWebP(o, doc);
  throw new Error('알 수 없는 형식: ' + fmt);
}

/** 크기 미리 짐작: 앞쪽 몇 장만 만들어 보고 전체 장수로 늘림 */
export async function estimate(fmt, o, doc = app.doc) {
  const { order } = A.exportOrder(o.tag ?? null, doc);
  const k = Math.min(order.length, 3);
  if (!k || !['gif', 'apng', 'webp'].includes(fmt)) return null;
  const b = await encode(fmt, { ...o, frames: k }, doc);
  return Math.round((b.size / k) * order.length);
}

export async function exportAnim(fmt, o = {}) {
  const doc = app.doc;
  if (fmt === 'webm') return A.exportWebM(o.scale, o.tag ?? null);
  if (fmt === 'mp4') {
    const t = mp4Type();
    if (!t) { app.toast('이 기기는 MP4 로 녹화할 수 없어 WebM 으로 만듭니다', 'warn'); return A.exportWebM(o.scale, o.tag ?? null); }
    return A.exportWebM(o.scale, o.tag ?? null, { mime: t, ext: 'mp4' });
  }
  if (fmt === 'png') return A.exportPNGs(o.scale, o.tag ?? null);
  const blob = await encode(fmt, o, doc);
  const name = `${safeName(doc.name)}_${fileStamp()}.${FORMATS[fmt].ext}`;
  await A.saveExport(name, blob);
  app.toast(`${fmt.toUpperCase()} 저장: data/exports/${name} (${(blob.size / 1024).toFixed(0)}KB)`);
  return { blob, name };
}
