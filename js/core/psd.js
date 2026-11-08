// PSD 내보내기 (레이어 유지, RLE 압축). 포토샵/클립스튜디오/크리타에서 열 수 있다.
import { alphaBounds } from './raster.js';
import { tyshBlock } from './psdtext.js';

const BLEND = {
  'source-over': 'norm', multiply: 'mul ', screen: 'scrn', overlay: 'over', darken: 'dark', lighten: 'lite',
  'color-dodge': 'div ', 'color-burn': 'idiv', 'hard-light': 'hLit', 'soft-light': 'sLit', difference: 'diff',
  hue: 'hue ', saturation: 'sat ', color: 'colr', luminosity: 'lum ', lighter: 'lddg',
};

class Writer {
  constructor() { this.parts = []; this.length = 0; }
  bytes(a) { this.parts.push(a); this.length += a.length; }
  u8(v) { this.bytes(new Uint8Array([v & 255])); }
  u16(v) { const b = new Uint8Array(2); new DataView(b.buffer).setUint16(0, v); this.bytes(b); }
  i16(v) { const b = new Uint8Array(2); new DataView(b.buffer).setInt16(0, v); this.bytes(b); }
  u32(v) { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, v); this.bytes(b); }
  i32(v) { const b = new Uint8Array(4); new DataView(b.buffer).setInt32(0, v); this.bytes(b); }
  ascii(s) { this.bytes(new Uint8Array([...s].map(c => c.charCodeAt(0)))); }
  writer(w) { for (const p of w.parts) this.bytes(p); }
}

/** PackBits 로 한 줄 압축 */
function packbits(src) {
  const n = src.length;
  const out = new Uint8Array(n + Math.ceil(n / 128) + 2);
  let o = 0, i = 0;
  while (i < n) {
    let j = i + 1;
    while (j < n && j - i < 128 && src[j] === src[i]) j++;
    if (j - i >= 3) {
      out[o++] = (257 - (j - i)) & 255;
      out[o++] = src[i];
      i = j;
    } else {
      let k = i;
      while (k < n && k - i < 128 && !(k + 2 < n && src[k] === src[k + 1] && src[k] === src[k + 2])) k++;
      out[o++] = k - i - 1;
      for (let m = i; m < k; m++) out[o++] = src[m];
      i = k;
    }
  }
  return out.subarray(0, o);
}

/** RGBA ImageData 의 한 채널(ch 0~3)을 RLE 로: [줄별 길이 표, 데이터 조각들] */
function rleChannel(data, w, h, ch) {
  const row = new Uint8Array(w);
  const counts = new Writer(), body = [];
  for (let y = 0; y < h; y++) {
    for (let x = 0, p = (y * w) * 4 + ch; x < w; x++, p += 4) row[x] = data[p];
    const packed = packbits(row);
    counts.u16(packed.length);
    body.push(packed);
  }
  return { counts, body, length: counts.length + body.reduce((s, b) => s + b.length, 0) };
}

function pascalName(name) {
  // 레거시 이름은 ASCII 로 (한글은 luni 블록에)
  const ascii = [...name].map(c => (c.charCodeAt(0) < 128 ? c : '_')).join('').slice(0, 255);
  const len = 1 + ascii.length;
  const pad = (4 - (len % 4)) % 4;
  const w = new Writer();
  w.u8(ascii.length); w.ascii(ascii);
  for (let i = 0; i < pad; i++) w.u8(0);
  return w;
}

function unicodeName(name) {
  const w = new Writer();
  w.ascii('8BIM'); w.ascii('luni');
  const chars = [...name].slice(0, 255);
  const units = [];
  for (const c of chars) { const code = c.codePointAt(0); if (code > 0xffff) units.push(0x3f); else units.push(code); }
  let len = 4 + units.length * 2;
  const pad = len % 4 ? 4 - (len % 4) : 0;
  w.u32(len + pad);
  w.u32(units.length);
  for (const u of units) w.u16(u);
  for (let i = 0; i < pad; i++) w.u8(0);
  return w;
}

/**
 * doc 을 PSD Blob 으로. flatten: 합성 이미지 캔버스(문서 크기, 용지 포함)
 */
export function writePSD(doc, flatten, onProgress) {
  const W = doc.width, H = doc.height;
  // 폴더는 PSD 규칙대로: [그룹 끝 표시] 자식들… [폴더] (아래→위)
  const entries = [];
  const walk = (folder) => {
    for (const l of doc.childrenOf(folder)) {
      if (l.isFolder) { entries.push({ l, divider: true }); walk(l); entries.push({ l, folder: true }); }
      else entries.push({ l });
    }
  };
  walk(null);
  const records = new Writer(), channelData = new Writer();
  entries.forEach((e, idx) => {
    onProgress?.(idx / entries.length);
    const l = e.l;
    let chans, b = { x: 0, y: 0, w: 0, h: 0 };
    if (e.divider || e.folder) {
      chans = [-1, 0, 1, 2].map(id => ({ id, counts: new Writer(), body: [], length: 0 }));
    } else {
      const src = l.canvas;
      b = alphaBounds(src) || b;
      if (b.w && b.h) {
        const img = src.getContext('2d').getImageData(b.x, b.y, b.w, b.h).data;
        chans = [[-1, 3], [0, 0], [1, 1], [2, 2]].map(([id, ch]) => ({ id, ...rleChannel(img, b.w, b.h, ch) }));
      } else {
        chans = [-1, 0, 1, 2].map(id => ({ id, counts: new Writer(), body: [], length: 0 }));
      }
      // 레이어 마스크: 문서 전체 크기의 회색 채널 (흰색 = 보임)
      if (l.mask) {
        const md = l.mask.getContext('2d').getImageData(0, 0, W, H).data;
        chans.push({ id: -2, ...rleChannel(md, W, H, 3) });
      }
    }
    records.i32(b.y); records.i32(b.x); records.i32(b.y + b.h); records.i32(b.x + b.w);
    records.u16(chans.length);
    for (const c of chans) { records.i16(c.id); records.u32(c.length + 2); }
    records.ascii('8BIM');
    records.ascii(e.divider ? 'norm' : (BLEND[l.blend] || 'norm'));
    records.u8(e.divider ? 255 : Math.round(l.opacity * 255));
    records.u8(!e.divider && !e.folder && l.clip ? 1 : 0);
    records.u8(e.divider ? 0 : ((l.alphaLock ? 1 : 0) | (l.visible ? 0 : 2)));
    records.u8(0);
    const extra = new Writer();
    if (!e.divider && !e.folder && l.mask) {
      extra.u32(20);
      extra.i32(0); extra.i32(0); extra.i32(H); extra.i32(W);
      extra.u8(0);                         // 사각형 밖 기본값: 가림
      extra.u8(l.maskOn ? 0 : 2);          // 2 = 마스크 꺼짐
      extra.u16(0);
    } else extra.u32(0);
    extra.u32(0);           // 블렌딩 범위 없음
    const name = e.divider ? '</Layer group>' : l.name;
    extra.writer(pascalName(name));
    extra.writer(unicodeName(name));
    if (e.divider || e.folder) {
      extra.ascii('8BIM'); extra.ascii('lsct'); extra.u32(4);
      extra.u32(e.divider ? 3 : (l.expanded ? 1 : 2));
    }
    // 글자 레이어는 포토샵 글자로도 (그림은 그대로 함께 저장 → 어느 프로그램에서나 보임)
    if (!e.divider && !e.folder && l.kind === 'text' && l.meta?.text && !l.meta.sfx) {   // 효과음은 모양이 복잡해 그림으로만

      try { extra.bytes(tyshBlock(l.meta, b)); } catch (err) { console.warn('PSD 글자 저장 실패', err); }
    }
    records.u32(extra.length);
    records.writer(extra);
    for (const c of chans) {
      channelData.u16(c.length ? 1 : 0);
      if (c.length) { channelData.writer(c.counts); for (const p of c.body) channelData.bytes(p); }
    }
  });

  const layerInfo = new Writer();
  layerInfo.i16(entries.length);
  layerInfo.writer(records);
  layerInfo.writer(channelData);
  if (layerInfo.length % 2) layerInfo.u8(0);

  const out = new Writer();
  out.ascii('8BPS'); out.u16(1); out.bytes(new Uint8Array(6));
  out.u16(3); out.u32(H); out.u32(W); out.u16(8); out.u16(3);
  out.u32(0);   // color mode data
  out.u32(0);   // image resources
  out.u32(4 + layerInfo.length + 4);
  out.u32(layerInfo.length);
  out.writer(layerInfo);
  out.u32(0);   // global layer mask

  // 합성 이미지 (RGB, RLE)
  const comp = flatten.getContext('2d').getImageData(0, 0, W, H).data;
  const rgb = [0, 1, 2].map(ch => rleChannel(comp, W, H, ch));
  out.u16(1);
  for (const c of rgb) out.writer(c.counts);
  for (const c of rgb) for (const p of c.body) out.bytes(p);
  onProgress?.(1);
  return new Blob(out.parts, { type: 'image/vnd.adobe.photoshop' });
}
