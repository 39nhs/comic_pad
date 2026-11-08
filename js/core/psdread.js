// PSD/PSB 열기
//  - 색상 모드: 비트맵, 흑백, 인덱스, RGB, CMYK, 다중채널, 듀오톤(흑백으로), Lab
//  - 비트 깊이: 1 / 8 / 16 / 32  (모두 8비트 RGB 로 변환)
//  - 압축: 무압축 / RLE / ZIP / ZIP+예측
//  - 레이어 이름·불투명도·합성 모드·클리핑·숨김·투명 잠금, 폴더, 레이어 마스크
//  - 레이어 효과(그림자·획·외부 광선 → ComicPad 레이어 효과), 그라데이션 맵·색상 균형·자연 채도 조정 레이어
//  - 단색 칠/셰이프 레이어: 그림이 비어 있으면 벡터 마스크·선 설정으로 다시 그림
//  - 텍스트 레이어: 그림은 그대로 두고 글자 내용/크기/색/글꼴을 meta.psdText 에 보관 → 텍스트 도구로 탭하면 편집 가능한 텍스트로 변환
import { Doc, Layer } from './doc.js';
import { createCanvas, uid } from './util.js';
import { readVectorPaths, pathsToPath2D } from './psdvector.js';

const BLEND = {
  norm: 'source-over', pass: 'source-over', 'mul ': 'multiply', scrn: 'screen', over: 'overlay', dark: 'darken', lite: 'lighten',
  'div ': 'color-dodge', idiv: 'color-burn', hLit: 'hard-light', sLit: 'soft-light', diff: 'difference',
  'hue ': 'hue', 'sat ': 'saturation', colr: 'color', 'lum ': 'luminosity', lddg: 'lighter', lbrn: 'color-burn',
  smud: 'difference', dkCl: 'darken', lgCl: 'lighten', vLit: 'hard-light', lLit: 'hard-light', pLit: 'hard-light', hMix: 'hard-light', fsub: 'difference', fdiv: 'color-dodge',
};
// PSB 에서 길이가 8바이트인 추가 정보 키
const LONG_KEYS = new Set(['LMsk', 'Lr16', 'Lr32', 'Layr', 'Mt16', 'Mt32', 'Mtrn', 'Alph', 'FMsk', 'lnk2', 'FEid', 'FXid', 'PxSD']);
const MODE_NAMES = { 0: '비트맵', 1: '흑백', 2: '인덱스', 3: 'RGB', 4: 'CMYK', 7: '다중채널', 8: '듀오톤', 9: 'Lab' };

export class Reader {
  constructor(buf) { this.v = new DataView(buf); this.u8a = new Uint8Array(buf); this.o = 0; }
  u8() { return this.v.getUint8(this.o++); }
  u16() { const x = this.v.getUint16(this.o); this.o += 2; return x; }
  i16() { const x = this.v.getInt16(this.o); this.o += 2; return x; }
  u32() { const x = this.v.getUint32(this.o); this.o += 4; return x; }
  i32() { const x = this.v.getInt32(this.o); this.o += 4; return x; }
  u64() { const hi = this.u32(), lo = this.u32(); return hi * 4294967296 + lo; }
  f64() { const x = this.v.getFloat64(this.o); this.o += 8; return x; }
  str(n) { let s = ''; for (let i = 0; i < n; i++) s += String.fromCharCode(this.u8()); return s; }
  bytes(n) { const b = this.u8a.subarray(this.o, this.o + n); this.o += n; return b; }
  skip(n) { this.o += n; }
  peek4() { return this.o + 4 <= this.u8a.length ? String.fromCharCode(...this.u8a.subarray(this.o, this.o + 4)) : ''; }
}

// ---- 압축 해제 -----------------------------------------------------------
function unpackbits(src, out) {
  let i = 0, o = 0;
  while (i < src.length && o < out.length) {
    let n = src[i++];
    if (n > 127) n -= 256;
    if (n >= 0) { for (let k = 0; k <= n && o < out.length; k++) out[o++] = src[i++]; }
    else if (n !== -128) { const v = src[i++]; for (let k = 0; k < 1 - n && o < out.length; k++) out[o++] = v; }
  }
}

async function inflate(bytes) {
  const ds = new DecompressionStream('deflate');
  return new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(ds)).arrayBuffer());
}

const rowBytesOf = (w, depth) => (depth === 1 ? Math.ceil(w / 8) : w * (depth / 8));

/** 압축된 채널 → 원본 바이트 (rowBytes*h) */
async function rawChannel(comp, body, w, h, c) {
  const rb = rowBytesOf(w, c.depth), total = rb * h;
  if (comp === 0) return body.subarray(0, total);
  if (comp === 1) {
    const cs = c.psb ? 4 : 2;
    const out = new Uint8Array(total), row = new Uint8Array(rb);
    let o = cs * h;
    for (let y = 0; y < h; y++) {
      const n = cs === 4 ? ((body[y * 4] << 24) | (body[y * 4 + 1] << 16) | (body[y * 4 + 2] << 8) | body[y * 4 + 3]) >>> 0 : (body[y * 2] << 8) | body[y * 2 + 1];
      row.fill(0);
      unpackbits(body.subarray(o, o + n), row);
      out.set(row, y * rb);
      o += n;
    }
    return out;
  }
  if (comp === 2 || comp === 3) {
    const raw = await inflate(body);
    if (comp === 2) return raw;
    // 예측(차분) 복원
    if (c.depth === 8) { for (let y = 0; y < h; y++) for (let x = 1; x < rb; x++) raw[y * rb + x] = (raw[y * rb + x] + raw[y * rb + x - 1]) & 255; return raw; }
    if (c.depth === 16) {
      const dv = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
      for (let y = 0; y < h; y++) for (let x = 1; x < w; x++) { const p = y * rb + x * 2; dv.setUint16(p, (dv.getUint16(p) + dv.getUint16(p - 2)) & 0xffff); }
      return raw;
    }
    if (c.depth === 32) {
      const out = new Uint8Array(total);
      for (let y = 0; y < h; y++) {
        const base = y * rb;
        for (let i = 1; i < rb; i++) raw[base + i] = (raw[base + i] + raw[base + i - 1]) & 255;
        for (let x = 0; x < w; x++) for (let b = 0; b < 4; b++) out[base + x * 4 + b] = raw[base + b * w + x];
      }
      return out;
    }
  }
  throw new Error('지원하지 않는 압축 방식: ' + comp);
}

/** 원본 바이트 → 8비트 값 (w*h) */
function to8(raw, w, h, c, linear) {
  const out = new Uint8Array(w * h);
  const rb = rowBytesOf(w, c.depth);
  if (c.depth === 8) { out.set(raw.subarray(0, w * h)); return out; }
  if (c.depth === 1) {
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) out[y * w + x] = (raw[y * rb + (x >> 3)] >> (7 - (x & 7))) & 1 ? 0 : 255;
    return out;
  }
  if (c.depth === 16) { for (let i = 0; i < w * h; i++) out[i] = raw[i * 2]; return out; }
  if (c.depth === 32) {
    const dv = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
    for (let i = 0; i < w * h; i++) {
      const f = Math.max(0, Math.min(1, dv.getFloat32(i * 4)));
      out[i] = Math.round((linear ? f : Math.pow(f, 1 / 2.2)) * 255);
    }
    return out;
  }
  throw new Error(`${c.depth}비트는 지원하지 않습니다`);
}

async function decodeChannel(data, w, h, c, linear) {
  if (data.length < 2 || !w || !h) return new Uint8Array(Math.max(0, w * h));
  const comp = (data[0] << 8) | data[1];
  return to8(await rawChannel(comp, data.subarray(2), w, h, c), w, h, c, linear);
}

// ---- 색 변환 ----------------------------------------------------------------
function labToRgb(L, a, b) {
  let y = (L + 16) / 116, x = a / 500 + y, z = y - b / 200;
  const f = (t) => (t ** 3 > 0.008856 ? t ** 3 : (t - 16 / 116) / 7.787);
  x = 0.95047 * f(x); y = f(y); z = 1.08883 * f(z);
  let r = x * 3.2406 + y * -1.5372 + z * -0.4986, g = x * -0.9689 + y * 1.8758 + z * 0.0415, bb = x * 0.0557 + y * -0.2040 + z * 1.0570;
  const gam = (v) => Math.round(255 * Math.max(0, Math.min(1, v > 0.0031308 ? 1.055 * v ** (1 / 2.4) - 0.055 : 12.92 * v)));
  return [gam(r), gam(g), gam(bb)];
}

function toCanvas(w, h, ch, c) {
  const cv = createCanvas(w, h);
  if (!w || !h) return cv;
  const x = cv.getContext('2d');
  const img = x.createImageData(w, h);
  const d = img.data, n = w * h, A = ch[-1];
  const z = new Uint8Array(n);
  const c0 = ch[0] || z, c1 = ch[1] || z, c2 = ch[2] || z, c3 = ch[3];
  for (let i = 0, p = 0; i < n; i++, p += 4) {
    let r, g, b;
    switch (c.mode) {
      case 3: r = c0[i]; g = c1[i]; b = c2[i]; break;
      case 4: { const k = c3 ? c3[i] : 255; r = c0[i] * k / 255; g = c1[i] * k / 255; b = c2[i] * k / 255; break; }
      case 2: { const v = c0[i]; r = c.palette?.[v] ?? v; g = c.palette?.[256 + v] ?? v; b = c.palette?.[512 + v] ?? v; break; }
      case 9: [r, g, b] = labToRgb(c0[i] * 100 / 255, c1[i] - 128, c2[i] - 128); break;
      default: r = g = b = c0[i];
    }
    d[p] = r; d[p + 1] = g; d[p + 2] = b; d[p + 3] = A ? A[i] : 255;
  }
  x.putImageData(img, 0, 0);
  return cv;
}

const colorChannels = (mode) => ({ 0: 1, 1: 1, 2: 1, 3: 3, 4: 4, 7: 1, 8: 1, 9: 3 }[mode] ?? 3);

// ---- 텍스트 레이어 (TySh) -------------------------------------------------
function readUnicode(r) { const n = r.u32(); let s = ''; for (let i = 0; i < n; i++) s += String.fromCharCode(r.u16()); return s.replace(/\0+$/, ''); }
function readKey(r) { const n = r.u32(); return r.str(n || 4); }

function readDescriptor(r) {
  readUnicode(r); readKey(r);
  const n = r.u32(), out = {};
  for (let i = 0; i < n; i++) { const k = readKey(r); out[k.trim()] = readItem(r, r.str(4)); }
  return out;
}

function readItem(r, t) {
  switch (t) {
    case 'Objc': case 'GlbO': return readDescriptor(r);
    case 'VlLs': { const n = r.u32(), a = []; for (let i = 0; i < n; i++) a.push(readItem(r, r.str(4))); return a; }
    case 'doub': return r.f64();
    case 'UntF': r.str(4); return r.f64();
    case 'UnFl': { r.str(4); const n = r.u32(), a = []; for (let i = 0; i < n; i++) a.push(r.f64()); return a; }
    case 'TEXT': return readUnicode(r);
    case 'enum': readKey(r); return readKey(r);
    case 'long': return r.i32();
    case 'comp': r.skip(8); return 0;
    case 'bool': return !!r.u8();
    case 'type': case 'GlbC': readUnicode(r); return readKey(r);
    case 'alis': case 'Pth ': r.skip(r.u32()); return null;
    case 'tdta': return r.bytes(r.u32());
    case 'obj ': {
      const n = r.u32();
      for (let i = 0; i < n; i++) {
        const k = r.str(4);
        if (k === 'prop') { readUnicode(r); readKey(r); readKey(r); }
        else if (k === 'Clss') { readUnicode(r); readKey(r); }
        else if (k === 'Enmr') { readUnicode(r); readKey(r); readKey(r); readKey(r); }
        else if (k === 'rele') { readUnicode(r); readKey(r); r.u32(); }
        else if (k === 'Idnt' || k === 'indx') r.u32();
        else if (k === 'name') { readUnicode(r); readKey(r); readUnicode(r); }
        else throw new Error('ref ' + k);
      }
      return null;
    }
    default: throw new Error('descriptor type ' + t);
  }
}

/** 설명자 속 색(RGBC·Grsc·HSBC·CMYC, 새 형식 redFloat 등) → '#rrggbb' */
function descColor(c) {
  if (!c) return '#000000';
  const hx = (v) => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0');
  let r = 0, g = 0, b = 0;
  if ('Rd' in c || 'Grn' in c) { r = c.Rd ?? 0; g = c.Grn ?? 0; b = c.Bl ?? 0; }
  else if ('redFloat' in c) { r = c.redFloat * 255; g = c.greenFloat * 255; b = c.blueFloat * 255; }
  else if ('Gry' in c) { r = g = b = 255 - (c.Gry ?? 0) * 2.55; }
  else if ('H' in c) {
    const h = ((c.H ?? 0) % 360) / 60, s2 = (c.Strt ?? 0) / 100, v = (c.Brgh ?? 0) / 100, i = Math.floor(h), f = h - i;
    const p2 = v * (1 - s2), q = v * (1 - s2 * f), t = v * (1 - s2 * (1 - f));
    [r, g, b] = [[v, t, p2], [q, v, p2], [p2, v, t], [p2, q, v], [t, p2, v], [v, p2, q]][i % 6].map(x => x * 255);
  } else if ('Cyn' in c) {
    const k = 1 - (c.Blck ?? 0) / 100;
    r = 255 * (1 - (c.Cyn ?? 0) / 100) * k; g = 255 * (1 - (c.Mgnt ?? 0) / 100) * k; b = 255 * (1 - (c.Ylw ?? 0) / 100) * k;
  }
  return '#' + hx(r) + hx(g) + hx(b);
}

/** 레이어 효과 설명자(lfx2 / lmfx) → ComicPad fx { shadow, stroke, glow } */
function readEffects(d, globalAngle) {
  if (!d || d.masterFXSwitch === false) return null;
  const k = (d.Scl ?? 100) / 100;
  const pick = (one, many) => {
    const list = Array.isArray(d[many]) ? d[many] : (d[one] ? [d[one]] : []);
    return list.find(e => e && e.enab !== false) || null;
  };
  const fx = {};
  const ds = pick('DrSh', 'dropShadowMulti');
  if (ds) {
    const ang = ((ds.uglg !== false && globalAngle != null) ? globalAngle : (ds.lagl ?? 120)) * Math.PI / 180;
    const dist = (ds.Dstn ?? 5) * k;
    fx.shadow = { on: true, dx: Math.round(-Math.cos(ang) * dist), dy: Math.round(Math.sin(ang) * dist), blur: Math.round((ds.blur ?? 5) * k), color: descColor(ds['Clr']), opacity: Math.max(0, Math.min(1, (ds.Opct ?? 75) / 100)) };
  }
  const st = pick('FrFX', 'frameFXMulti');
  if (st) {
    const size = (st.Sz ?? 3) * k;
    // ComicPad 획은 바깥쪽만: 안쪽·가운데 획은 바깥 굵기로 근사
    const pos = st.Styl;
    fx.stroke = { on: true, size: Math.max(1, Math.round(pos === 'InsF' ? size / 2 : pos === 'CtrF' ? size / 2 : size)), color: descColor(st['Clr']) };
  }
  const og = pick('OrGl', 'outerGlowMulti');
  if (og) fx.glow = { on: true, size: Math.round((og.blur ?? 10) * k), color: descColor(og['Clr']), opacity: Math.max(0, Math.min(1, (og.Opct ?? 75) / 100)) };
  return Object.keys(fx).length ? fx : null;
}

/** 예전 형식 레이어 효과(lrFX): 그림자·외부 광선만 (lfx2 가 없을 때) */
function readLegacyEffects(r, end) {
  const fx = {};
  r.u16();
  const n = r.u16();
  const fixed = (v) => (v > 65535 ? v / 65536 : v);
  const color = () => { r.u16(); const c = [r.u16(), r.u16(), r.u16()]; r.u16(); return '#' + c.map(v => (v >> 8).toString(16).padStart(2, '0')).join(''); };
  for (let i = 0; i < n && r.o + 12 <= end; i++) {
    if (r.str(4) !== '8BIM') break;
    const key = r.str(4), size = r.u32(), start = r.o;
    if (key === 'dsdw') {
      r.u32(); const blur = fixed(r.u32()); r.u32(); const ang = r.i32(), dist = r.i32();
      const c = color(); r.skip(8); const on = r.u8(); r.u8(); const op = r.u8();
      if (on) fx.shadow = { on: true, dx: Math.round(-Math.cos(ang * Math.PI / 180) * dist), dy: Math.round(Math.sin(ang * Math.PI / 180) * dist), blur: Math.round(blur), color: c, opacity: op / 255 };
    } else if (key === 'oglw') {
      r.u32(); const blur = fixed(r.u32()); r.u32();
      const c = color(); r.skip(8); const on = r.u8(); const op = r.u8();
      if (on) fx.glow = { on: true, size: Math.round(blur), color: c, opacity: op / 255 };
    }
    r.o = start + size;
  }
  return Object.keys(fx).length ? fx : null;
}

function decodePSString(s) {
  // EngineData 안의 (þÿ....) UTF-16BE 문자열
  s = s.replace(/\\(.)/g, '$1');
  if (s.startsWith('þÿ')) {
    let out = '';
    for (let i = 2; i + 1 < s.length; i += 2) out += String.fromCharCode((s.charCodeAt(i) << 8) | s.charCodeAt(i + 1));
    return out;
  }
  return s;
}

function parseTySh(data) {
  const r = new Reader(data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength));
  r.u16();
  const mat = [r.f64(), r.f64(), r.f64(), r.f64(), r.f64(), r.f64()];
  r.u16(); r.u32();
  const d = readDescriptor(r);
  const info = { text: (d.Txt || '').replace(/\r/g, '\n'), mat, size: 24, color: '#000000', font: null, align: 'left' };
  if (d.EngineData) {
    let s = '';
    for (const b of d.EngineData) s += String.fromCharCode(b);
    const fs = s.match(/\/FontSize ([\d.]+)/);
    if (fs) info.size = parseFloat(fs[1]);
    const fc = s.match(/\/FillColor << \/Type 1 \/Values \[ ?([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+)/);
    if (fc) info.color = '#' + [fc[2], fc[3], fc[4]].map(v => Math.round(parseFloat(v) * 255).toString(16).padStart(2, '0')).join('');
    const fontIdx = parseInt((s.match(/\/StyleRunArray[\s\S]*?\/Font (\d+)/) || [])[1] || '0', 10);
    const names = [...s.matchAll(/\/FontSet \[[\s\S]*?\]\s*>>/g)][0]?.[0].matchAll(/\/Name \(((?:\\.|[^)])*)\)/g);
    if (names) { const list = [...names].map(m => decodePSString(m[1])); info.font = list[fontIdx] || list[0] || null; }
    const j = s.match(/\/Justification (\d)/);
    if (j) info.align = ['left', 'right', 'center'][+j[1]] || 'left';
    if (/\/Orientation 1/.test(s)) info.vertical = true;
  }
  if (d.Ornt === 'Vrtc') info.vertical = true;
  return info;
}

// ---- 레이어 정보 ------------------------------------------------------------
async function readLayerInfo(r, c) {
  const count = Math.abs(r.i16());
  const recs = [];
  for (let i = 0; i < count; i++) {
    const rec = { top: r.i32(), left: r.i32(), bottom: r.i32(), right: r.i32(), chans: [] };
    const nc = r.u16();
    for (let k = 0; k < nc; k++) rec.chans.push({ id: r.i16(), len: c.psb ? r.u64() : r.u32() });
    r.skip(4);
    rec.blend = r.str(4);
    rec.opacity = r.u8() / 255;
    rec.clip = r.u8() === 1;
    const flags = r.u8();
    rec.visible = !(flags & 2);
    rec.alphaLock = !!(flags & 1);
    r.skip(1);
    const exLen = r.u32(), exEnd = r.o + exLen;
    const mLen = r.u32(), mEnd = r.o + mLen;
    if (mLen >= 18) rec.mask = { top: r.i32(), left: r.i32(), bottom: r.i32(), right: r.i32(), def: r.u8(), flags: r.u8() };
    r.o = mEnd;
    r.skip(r.u32());
    const nl = r.u8();
    rec.name = r.str(nl);
    r.o += (4 - ((nl + 1) % 4)) % 4;
    while (r.o + 12 <= exEnd) {
      const sig = r.str(4);
      if (sig !== '8BIM' && sig !== '8B64') break;
      const key = r.str(4);
      const len = c.psb && LONG_KEYS.has(key) ? r.u64() : r.u32();
      const dStart = r.o;
      try {
        if (key === 'luni') { const n = r.u32(); let s = ''; for (let k = 0; k < n; k++) s += String.fromCharCode(r.u16()); rec.name = s.replace(/\0+$/, ''); }
        else if (key === 'lsct' || key === 'lsdk') rec.section = r.u32();
        else if (key === 'TySh') rec.text = parseTySh(r.bytes(len));
        else if (key === 'lfx2' || key === 'lmfx') { r.u32(); r.u32(); rec.fx = readEffects(readDescriptor(r), c.globalAngle); rec.hasLfx2 = true; }
        else if (key === 'lrFX') rec.fxLegacy = readLegacyEffects(r, dStart + len);
        else if (key === 'vmsk' || key === 'vsms') rec.vpaths = readVectorPaths(r.bytes(len), c.W, c.H);
        else if (key === 'vstk') { r.u32(); rec.vstroke = readDescriptor(r); }
        else if (key === 'SoCo') { r.u32(); rec.fillColor = descColor(readDescriptor(r)['Clr']); rec.fillLayer = 'SoCo'; }
        else if (key === 'GdFl' || key === 'PtFl') rec.fillLayer = key;
        else if (['brit', 'levl', 'curv', 'expA', 'vibA', 'hue ', 'hue2', 'blnc', 'blwh', 'phfl', 'mixr', 'clrL', 'nvrt', 'post', 'thrs', 'selc', 'grdm'].includes(key)) {
          rec.adjust = key;
          rec.adj = readAdjust(key, new Reader(r.u8a.slice(r.o, r.o + len).buffer));
        }
      } catch (e) { console.warn('PSD 추가 정보 읽기 실패', key, e); }
      r.o = dStart + len + (len % 2);
    }
    r.o = exEnd;
    if (!rec.hasLfx2 && rec.fxLegacy) rec.fx = rec.fxLegacy;   // 새 형식(lfx2)이 있으면 그것만 믿음
    recs.push(rec);
  }
  for (const rec of recs) {
    const w = rec.right - rec.left, hh = rec.bottom - rec.top;
    const ch = {};
    for (const cn of rec.chans) {
      const data = r.bytes(cn.len);
      if (cn.id === -2 && rec.mask) {
        const mw = rec.mask.right - rec.mask.left, mh = rec.mask.bottom - rec.mask.top;
        rec.maskData = await decodeChannel(data, mw, mh, c, true);
      } else if (cn.id >= -1 && cn.id <= 3) ch[cn.id] = await decodeChannel(data, w, hh, c, cn.id === -1);
    }
    rec.canvas = w > 0 && hh > 0 ? toCanvas(w, hh, ch, c) : null;
  }
  return recs;
}

// ---- 조정 레이어 (포토샵 형식 → ComicPad 조정 레이어) -------------------------
/** 지원: 레벨·곡선·밝기/대비·색조/채도·반전·포스터화·한계값·흑백. 그 밖은 null */
export function readAdjust(key, r) {
  try {
    if (key === 'nvrt') return { type: 'invert', params: {} };
    if (key === 'post') return { type: 'posterize', params: { levels: Math.max(2, r.u16()) } };
    if (key === 'thrs') return { type: 'threshold', params: { t: r.u16() } };
    if (key === 'blwh') return { type: 'bw', params: { r: 30, g: 59, b: 11 } };
    if (key === 'brit') { const b = r.i16(), c = r.i16(); return { type: 'bc', params: { b: Math.max(-100, Math.min(100, Math.round(b / 1.5))), c: Math.max(-100, Math.min(100, c)) } }; }
    if (key === 'levl') {
      r.u16();                                                     // 버전
      const inB = r.u16(), inW = r.u16(), outB = r.u16(), outW = r.u16(), g = r.u16();
      return { type: 'levels', params: { inB, inW: inW || 255, outB, outW: outW || 255, gamma: (g || 100) / 100 } };
    }
    if (key === 'curv') {
      r.u8(); const ver = r.u16(), bits = r.u32();
      const p = { master: [[0, 0], [255, 255]], r: [[0, 0], [255, 255]], g: [[0, 0], [255, 255]], b: [[0, 0], [255, 255]] };
      const names = ['master', 'r', 'g', 'b'];
      if (ver === 1) for (let ch = 0; ch < 32; ch++) {
        if (!(bits & (1 << ch))) continue;
        const n = r.u16(), pts = [];
        for (let k = 0; k < n; k++) { const out = r.u16(), inp = r.u16(); pts.push([inp, out]); }
        if (names[ch]) p[names[ch]] = pts;
      }
      return { type: 'curves', params: p };
    }
    if (key === 'blnc') {
      // 어두운 곳·중간·밝은 곳 × (청록-빨강, 자홍-초록, 노랑-파랑)
      const v = Array.from({ length: 9 }, () => Math.max(-100, Math.min(100, r.i16())));
      return { type: 'balance', params: { sr: v[0], sg: v[1], sb: v[2], mr: v[3], mg: v[4], mb: v[5], hr: v[6], hg: v[7], hb: v[8] } };
    }
    if (key === 'grdm') {
      const ver = r.u16();
      if (ver !== 1 && ver !== 3) return null;
      const reversed = !!r.u8(); r.u8();
      if (ver === 3) r.skip(4);                                     // 보간 방식
      r.skip(r.u32() * 2);                                          // 이름 (유니코드)
      const n = r.u16(), stops = [];
      for (let i = 0; i < n; i++) {
        const loc = r.u32(); r.u32();                               // 위치(0~4096), 중간점
        const space = r.u16(), c1 = r.u16(), c2 = r.u16(), c3 = r.u16(); r.u16();
        r.skip(2);
        let rgb = [c1, c2, c3].map(x => x >> 8);
        if (space === 8) rgb = [c1, c1, c1].map(x => 255 - Math.round(x / 10000 * 255));   // 흑백(0~10000)
        const pos = Math.max(0, Math.min(1, loc / 4096));
        stops.push({ pos: reversed ? 1 - pos : pos, color: '#' + rgb.map(x => Math.max(0, Math.min(255, x)).toString(16).padStart(2, '0')).join('') });
      }
      if (stops.length < 2) return null;
      stops.sort((a, b) => a.pos - b.pos);
      return { type: 'gradmap', params: { stops } };
    }
    if (key === 'vibA') {
      r.u32();
      const d = readDescriptor(r);
      return { type: 'vibrance', params: { v: Math.max(-100, Math.min(100, d.vibrance ?? 0)) } };
    }
    if (key === 'hue2' || key === 'hue ') {
      r.u16(); r.u8(); r.u8();                                     // 버전, 단색 입히기 여부, 채움
      r.i16(); r.i16(); r.i16();                                   // 단색 입히기 값
      const hue = r.i16(), sat = r.i16(), light = r.i16();
      return { type: 'hsl', params: { hue, sat, light } };
    }
  } catch (e) { console.warn('PSD 조정 레이어 읽기 실패', key, e); }
  return null;
}

function hasPixels(cv) {
  if (!cv || !cv.width || !cv.height) return false;
  const d = cv.getContext('2d').getImageData(0, 0, cv.width, cv.height).data;
  for (let i = 3; i < d.length; i += 4) if (d[i]) return true;
  return false;
}

/** 단색 칠 + 벡터 마스크 + 선(vstk) → 그림 */
function drawFillLayer(ctx, rec, W, H) {
  const st = rec.vstroke || {};
  const path = rec.vpaths?.length ? pathsToPath2D(rec.vpaths) : null;
  const rule = rec.vpaths?.some(p => p.op === 2 || p.op === 0) ? 'evenodd' : 'nonzero';
  ctx.save();
  if (st.fillEnabled !== false) {
    ctx.fillStyle = rec.fillColor || '#000';
    if (path) ctx.fill(path, rule); else ctx.fillRect(0, 0, W, H);
  }
  if (path && st.strokeEnabled) {
    const w = st.strokeStyleLineWidth || 1;
    ctx.strokeStyle = descColor(st.strokeStyleContent?.['Clr']);
    ctx.lineJoin = { strokeStyleRoundJoin: 'round', strokeStyleBevelJoin: 'bevel' }[st.strokeStyleLineJoinType] || 'miter';
    const align = st.strokeStyleLineAlignment;
    if (align === 'strokeStyleAlignInside' || align === 'strokeStyleAlignOutside') {
      // 안쪽/바깥쪽 선: 2배 굵기로 그리고 모양 안/밖으로 잘라 냄
      const t = createCanvas(W, H), tx = t.getContext('2d');
      tx.lineJoin = ctx.lineJoin; tx.strokeStyle = ctx.strokeStyle; tx.lineWidth = w * 2; tx.stroke(path);
      tx.globalCompositeOperation = align === 'strokeStyleAlignInside' ? 'destination-in' : 'destination-out';
      tx.fill(path, rule);
      ctx.drawImage(t, 0, 0);
    } else { ctx.lineWidth = w; ctx.stroke(path); }
  }
  ctx.restore();
}

// ---- 메인 --------------------------------------------------------------------
export async function readPSD(buffer, name = 'PSD') {
  const r = new Reader(buffer);
  if (r.str(4) !== '8BPS') throw new Error('PSD 파일이 아닙니다');
  const ver = r.u16();
  if (ver !== 1 && ver !== 2) throw new Error('알 수 없는 PSD 버전');
  r.skip(6);
  const nch = r.u16(), H = r.u32(), W = r.u32(), depth = r.u16(), mode = r.u16();
  if (![1, 8, 16, 32].includes(depth)) throw new Error(`${depth}비트 PSD는 지원하지 않습니다`);
  if (!(mode in MODE_NAMES)) throw new Error('지원하지 않는 색상 모드입니다 (' + mode + ')');
  if (W * H > 268000000) throw new Error('너무 큰 이미지입니다');
  const c = { psb: ver === 2, depth, mode, W, H };
  const cmLen = r.u32();
  if (mode === 2 && cmLen >= 768) c.palette = r.bytes(768).slice();
  else r.skip(cmLen);
  {
    // 이미지 리소스: 전체 조명 각도(1037, 그림자 방향에 씀)
    const irLen = r.u32(), irEnd = r.o + irLen;
    while (r.o + 12 <= irEnd) {
      if (r.str(4) !== '8BIM') break;
      const id = r.u16();
      const nl = r.u8(); r.skip(nl + ((nl + 1) % 2));
      const size = r.u32(), start = r.o;
      if (id === 1037 && size >= 4) c.globalAngle = r.i32();
      r.o = start + size + (size % 2);
    }
    r.o = irEnd;
  }

  const lmLen = c.psb ? r.u64() : r.u32(), lmEnd = r.o + lmLen;
  let recs = [];
  if (lmLen > 0) {
    const liLen = c.psb ? r.u64() : r.u32(), liStart = r.o;
    if (liLen > 0) recs = await readLayerInfo(r, c);
    r.o = liStart + liLen;
    if (r.o + 4 <= lmEnd) r.skip(r.u32());       // global layer mask
    // 16/32비트 PSD 는 레이어 정보가 Lr16 / Lr32 블록 안에 들어 있다
    while (r.o + 12 <= lmEnd) {
      let sig = r.peek4();
      if (sig !== '8BIM' && sig !== '8B64') { r.o = Math.ceil(r.o / 4) * 4; sig = r.peek4(); if (sig !== '8BIM' && sig !== '8B64') break; }
      r.skip(4);
      const key = r.str(4);
      const len = c.psb && LONG_KEYS.has(key) ? r.u64() : r.u32();
      const dStart = r.o;
      if ((key === 'Lr16' || key === 'Lr32' || key === 'Layr') && !recs.length && len > 0) recs = await readLayerInfo(r, c);
      r.o = dStart + len;
    }
  }
  r.o = lmEnd;

  const doc = new Doc(W, H, { name, paperVisible: true });
  const stack = [];
  const notes = new Set();
  for (const rec of recs) {
    if (rec.section === 3) { stack.push({ id: uid('L'), parent: stack.at(-1)?.id ?? null }); continue; }
    const parent = stack.at(-1)?.id ?? null;
    if (rec.section === 1 || rec.section === 2) {
      const g = stack.pop() || { id: uid('L'), parent: null };
      doc.layers.push(new Layer(doc, {
        id: g.id, kind: 'folder', name: rec.name || '폴더', parent: g.parent, expanded: rec.section === 1,
        visible: rec.visible, opacity: rec.opacity, blend: BLEND[rec.blend] || 'source-over',
      }));
      continue;
    }
    if (rec.adjust && rec.adj) {
      // 조정 레이어: 효과를 그대로 (레벨·곡선·밝기/대비·색조/채도·반전·포스터화·한계값·흑백)
      doc.layers.push(new Layer(doc, { name: rec.name || '조정', kind: 'adjust', parent, visible: rec.visible, opacity: rec.opacity, clip: rec.clip, meta: { type: rec.adj.type, params: rec.adj.params } }));
      continue;
    }
    if (rec.adjust) notes.add('일부 조정 레이어(선택 색상·채널 혼합 등)는 효과 없이 빈 레이어로 들어왔습니다');
    const l = new Layer(doc, {
      name: rec.name || '레이어', parent, visible: rec.visible, opacity: rec.opacity,
      blend: BLEND[rec.blend] || 'source-over', clip: rec.clip, alphaLock: rec.alphaLock,
      meta: rec.text ? { psdText: rec.text } : null, fx: rec.fx || null,
    });
    if (rec.fx) notes.add('레이어 효과(그림자·획·외부 광선)는 ComicPad 레이어 효과로 들어왔습니다 (그 밖의 효과는 빠짐)');
    if (rec.canvas) l.ctx.drawImage(rec.canvas, rec.left, rec.top);
    // 단색 칠/셰이프 레이어인데 그림이 비어 있으면 벡터 마스크·선 설정으로 직접 그림
    if (rec.fillLayer && !hasPixels(rec.canvas)) {
      if (rec.fillLayer === 'SoCo') { drawFillLayer(l.ctx, rec, W, H); notes.add('단색 칠·셰이프 레이어는 그림 레이어로 들어왔습니다'); }
      else notes.add('그라데이션·패턴 칠 레이어는 빈 레이어로 들어왔습니다');
    }
    if (rec.maskData) {
      const m = rec.mask, mw = m.right - m.left, mh = m.bottom - m.top;
      const mk = createCanvas(W, H), mx = mk.getContext('2d');
      if (m.def) { mx.fillStyle = '#fff'; mx.fillRect(0, 0, W, H); }
      if (mw > 0 && mh > 0) {
        mx.clearRect(m.left, m.top, mw, mh);
        const img = mx.createImageData(mw, mh);
        for (let i = 0; i < mw * mh; i++) { img.data[i * 4] = img.data[i * 4 + 1] = img.data[i * 4 + 2] = 255; img.data[i * 4 + 3] = rec.maskData[i]; }
        const t = createCanvas(mw, mh); t.getContext('2d').putImageData(img, 0, 0);
        mx.drawImage(t, m.left, m.top);
      }
      l.mask = mk;
      l.maskOn = !(m.flags & 2);
    }
    if (rec.text) notes.add('텍스트 레이어: 텍스트 도구로 탭하면 다시 편집할 수 있는 글자로 바뀝니다');
    doc.layers.push(l);
  }

  // 레이어가 없으면 합성 이미지 사용
  if (!doc.layers.some(l => !l.isFolder)) {
    const comp = r.u16();
    const cc = colorChannels(mode), rb = rowBytesOf(W, depth);
    const ch = {};
    if (comp === 1) {
      const cs = c.psb ? 4 : 2;
      const counts = [];
      for (let i = 0; i < nch * H; i++) counts.push(cs === 4 ? r.u32() : r.u16());
      for (let k = 0; k < nch; k++) {
        const raw = new Uint8Array(rb * H), row = new Uint8Array(rb);
        for (let y = 0; y < H; y++) { row.fill(0); unpackbits(r.bytes(counts[k * H + y]), row); raw.set(row, y * rb); }
        if (k < cc) ch[k] = to8(raw, W, H, c, false);
        else if (k === cc) ch[-1] = to8(raw, W, H, c, true);
      }
    } else if (comp === 0) {
      for (let k = 0; k < Math.min(nch, cc + 1); k++) ch[k < cc ? k : -1] = to8(r.bytes(rb * H), W, H, c, k >= cc);
    } else {
      const all = await rawChannel(comp, r.bytes(r.u8a.length - r.o), W, H * nch, c);
      for (let k = 0; k < Math.min(nch, cc + 1); k++) ch[k < cc ? k : -1] = to8(all.subarray(k * rb * H), W, H, c, k >= cc);
    }
    const l = new Layer(doc, { name: '배경' });
    l.ctx.drawImage(toCanvas(W, H, ch, c), 0, 0);
    doc.layers.push(l);
  }
  doc.activeId = doc.layers.filter(l => !l.isFolder).at(-1)?.id;
  doc.importInfo = { mode: MODE_NAMES[mode], depth, psb: c.psb, notes: [...notes] };
  return doc;
}
