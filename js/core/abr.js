// 포토샵 브러시(.abr) 읽기: 그림으로 된 브러시 모양(샘플)을 꺼낸다
// 버전 1·2 (예전 포토샵) 와 6 (포토샵 7 ~ CC) 지원. 브러시 동작 설정(흩뿌리기 등)은 읽지 않고 모양과 간격만.
// 형식 참고: GIMP gimpbrush-load.c 의 ABR 읽기 방식

class Reader {
  constructor(buf) { this.v = new DataView(buf); this.p = 0; this.len = buf.byteLength; }
  u8() { return this.v.getUint8(this.p++); }
  i8() { return this.v.getInt8(this.p++); }
  u16() { const x = this.v.getUint16(this.p); this.p += 2; return x; }
  i32() { const x = this.v.getInt32(this.p); this.p += 4; return x; }
  u32() { const x = this.v.getUint32(this.p); this.p += 4; return x; }
  str4() { let s = ''; for (let i = 0; i < 4; i++) s += String.fromCharCode(this.u8()); return s; }
  ucs2() { const n = this.u32(); let s = ''; for (let i = 0; i < n; i++) { const c = this.u16(); if (c) s += String.fromCharCode(c); } return s; }
}

/** 줄마다 PackBits 로 줄인 그림 풀기 */
function unRLE(r, w, hgt, depth) {
  const bpp = depth >> 3, rowLen = w * bpp;
  const lens = [];
  for (let i = 0; i < hgt; i++) lens.push(r.u16());
  const out = new Uint8Array(rowLen * hgt);
  let k = 0;
  for (let i = 0; i < hgt; i++) {
    const end = r.p + lens[i];
    let row = 0;
    while (r.p < end) {
      let n = r.i8();
      if (n === -128) continue;
      if (n < 0) { n = -n + 1; const c = r.u8(); for (let j = 0; j < n && row < rowLen; j++, row++) out[k++] = c; }
      else { for (let j = 0; j <= n; j++) { const c = r.u8(); if (row < rowLen) { out[k++] = c; row++; } } }
    }
    r.p = end;
    k = (i + 1) * rowLen;
  }
  return out;
}

function readSample(r, w, hgt, depth, compression) {
  if (w <= 0 || hgt <= 0 || w > 10000 || hgt > 10000) throw new Error('브러시 크기가 이상합니다');
  const bpp = depth >> 3;
  let data;
  if (compression) data = unRLE(r, w, hgt, depth);
  else { data = new Uint8Array(r.v.buffer, r.v.byteOffset + r.p, w * hgt * bpp).slice(); r.p += w * hgt * bpp; }
  // 16비트면 위쪽 바이트만
  if (bpp === 2) { const d8 = new Uint8Array(w * hgt); for (let i = 0; i < d8.length; i++) d8[i] = data[i * 2]; data = d8; }
  return { w, h: hgt, mask: data };
}

function v12(r, version) {
  const count = r.u16(), out = [];
  for (let i = 0; i < count && r.p < r.len; i++) {
    const type = r.u16(), size = r.u32(), next = r.p + size;
    if (type === 2) {
      r.u32();                       // misc
      const spacing = r.u16();
      const name = version === 2 ? r.ucs2() : '';
      r.u8();                        // antialias
      r.p += 8;                      // short bounds
      const top = r.i32(), left = r.i32(), bottom = r.i32(), right = r.i32();
      const depth = r.u16(), comp = r.u8();
      try { out.push({ name, spacing: spacing / 100, ...readSample(r, right - left, bottom - top, depth, comp) }); } catch { /* 건너뜀 */ }
    }
    r.p = next;
  }
  return out;
}

// ---------------------------------------------------------------------
// 포토샵 "설명자(descriptor)": 브러시 크기·간격·각도·동작 설정이 들어 있다 ('desc' 블록)
// ---------------------------------------------------------------------
function idOrStr(r) { const n = r.u32(); if (n === 0) return r.str4(); let s = ''; for (let i = 0; i < n; i++) s += String.fromCharCode(r.u8()); return s; }

function readValue(r, type) {
  switch (type) {
    case 'Objc': case 'GlbO': return readDescriptor(r);
    case 'VlLs': { const n = r.u32(), a = []; for (let i = 0; i < n; i++) a.push(readValue(r, r.str4())); return a; }
    case 'doub': { const v = r.v.getFloat64(r.p); r.p += 8; return v; }
    case 'UntF': { const unit = r.str4(); const v = r.v.getFloat64(r.p); r.p += 8; return { unit, value: v }; }
    case 'UnFl': { const unit = r.str4(), n = r.u32(), a = []; for (let i = 0; i < n; i++) { a.push(r.v.getFloat64(r.p)); r.p += 8; } return { unit, values: a }; }
    case 'TEXT': return r.ucs2();
    case 'enum': { const t = idOrStr(r); return { type: t, value: idOrStr(r) }; }
    case 'long': return r.i32();
    case 'comp': { const hi = r.i32(), lo = r.u32(); return hi * 4294967296 + lo; }
    case 'bool': return !!r.u8();
    case 'type': case 'GlbC': { r.ucs2(); return idOrStr(r); }
    case 'alis': case 'tdta': case 'Pth ': { const n = r.u32(); r.p += n; return null; }
    case 'obj ': {   // 참조: 브러시에서는 거의 안 쓰므로 건너뛰기만
      const n = r.u32();
      for (let i = 0; i < n; i++) {
        const t = r.str4();
        if (t === 'prop') { r.ucs2(); idOrStr(r); idOrStr(r); }
        else if (t === 'Clss') { r.ucs2(); idOrStr(r); }
        else if (t === 'Enmr') { r.ucs2(); idOrStr(r); idOrStr(r); idOrStr(r); }
        else if (t === 'rele') { r.ucs2(); idOrStr(r); r.u32(); }
        else if (t === 'Idnt' || t === 'indx') r.u32();
        else if (t === 'name') { r.ucs2(); idOrStr(r); r.ucs2(); }
      }
      return null;
    }
    default: throw new Error('알 수 없는 설정 형식 ' + type);
  }
}

export function readDescriptor(r) {
  r.ucs2();                 // 이름 (보통 비어 있음)
  const cls = idOrStr(r), n = r.u32(), out = { _class: cls };
  for (let i = 0; i < n; i++) { const key = idOrStr(r); out[key] = readValue(r, r.str4()); }
  return out;
}

const num = (v) => (v == null ? undefined : typeof v === 'number' ? v : v.value);

/** 브러시 설명자 → 우리 브러시 설정 (가능한 것만) */
export function brushSettings(d) {
  const b = d.Brsh || {}, o = { name: d['Nm  '] || '', sampleId: b.sampledData || null };
  const dm = num(b.Dmtr), sp = num(b.Spcn), an = num(b.Angl), rn = num(b.Rndn), hd = num(b.Hrdn);
  if (dm) o.size = dm;
  if (sp != null) o.spacing = Math.max(0.02, sp / 100);
  if (an != null) o.angle = an;
  if (rn != null && rn < 99) { o.roundness = rn / 100; }
  if (hd != null) o.hardness = hd / 100;
  // 모양 동작: 크기 흔들림·필압으로 크기 (bVTy 2 = 펜 압력)
  if (d.useTipDynamics !== false) {
    const sv = d.szVr || {}, mn = num(d.minimumDiameter);
    if (sv.bVTy === 2) { o.pressureSize = true; o.minSize = mn != null ? mn / 100 : 0.2; } else if (d.useTipDynamics) o.pressureSize = false;
    if (num(sv.jitter)) o.sizeJitter = num(sv.jitter) / 100;
    const av = d.angleDynamics || {};
    if (num(av.jitter)) o.rotJitter = num(av.jitter) / 100;
    if (av.bVTy === 6) o.followDir = true;   // 6 = 진행 방향
  }
  if (d.useScatter) { const sc = num(d.Sctr) ?? num(d.scatterDynamics?.jitter); if (sc) o.scatter = Math.min(3, sc / 100); }
  // 전달(투명도) 동작
  if (d.usePaintDynamics) {
    const ov = d.opVr || {};
    if (ov.bVTy === 2) { o.pressureOpacity = true; o.minOpacity = (num(d.minimumOpacity) ?? 0) / 100; }
  }
  if (d.useColorDynamics) {
    if (num(d['H   '])) o.hueJitter = num(d['H   ']) / 100 / 2;
    if (num(d.Strt)) o.satJitter = num(d.Strt) / 100;
    if (num(d.Brgh)) o.valJitter = num(d.Brgh) / 100;
  }
  return o;
}

function v6(r, sub) {
  // '8BIM' 블록 중 'samp' 에 모양, 'desc' 에 설정이 있다
  const out = [];
  let settings = [];
  while (r.p + 12 <= r.len) {
    const sig = r.str4(), key = r.str4();
    const size = r.u32(), end = r.p + size;
    if (sig !== '8BIM') break;
    if (key === 'samp') {
      while (r.p < end - 4) {
        const bsize = r.u32();
        const next = r.p + bsize + ((4 - (bsize % 4)) % 4);
        try {
          const idStart = r.p, idLen = r.u8();
          let id = ''; for (let i = 0; i < idLen; i++) id += String.fromCharCode(r.u8());
          r.p = idStart + (sub === 1 ? 47 : 301);
          const top = r.i32(), left = r.i32(), bottom = r.i32(), right = r.i32();
          const depth = r.u16(), comp = r.u8();
          out.push({ id, name: '', spacing: 0.25, ...readSample(r, right - left, bottom - top, depth, comp) });
        } catch { /* 이 모양은 건너뜀 */ }
        r.p = next;
      }
    }
    if (key === 'desc') {
      try { r.u32(); const d = readDescriptor(r); settings = (d.Brsh || []).map(brushSettings); } catch (e) { console.warn('ABR 설정을 읽지 못했습니다', e); }
    }
    r.p = end + (size % 2);   // 짝수 맞춤
    if (r.p > r.len) break;
  }
  // 모양과 설정 짝짓기: 모양 ID 로, 없으면 순서대로 (계산형 브러시는 모양이 없어 건너뜀)
  const sampled = settings.filter(s => s.sampleId);
  out.forEach((s, i) => {
    const st = settings.find(x => x.sampleId && x.sampleId === s.id) || sampled[i];
    if (st) { s.settings = st; if (st.name) s.name = st.name; if (st.spacing) s.spacing = st.spacing; }
  });
  return out;
}

/** ArrayBuffer → [{ name, spacing, w, h, mask(Uint8Array, 255 = 칠함) }] */
export function readABR(buf) {
  const r = new Reader(buf);
  const version = r.u16();
  if (version === 1 || version === 2) return v12(r, version);
  if (version === 6 || version === 7 || version === 10) { const sub = r.u16(); return v6(r, sub); }
  throw new Error(`지원하지 않는 ABR 버전입니다 (${version})`);
}

/** 모양 → 검정 + 투명도 캔버스 (너무 크면 줄임) */
export function sampleToCanvas(s, max = 512) {
  const c = document.createElement('canvas');
  c.width = s.w; c.height = s.h;
  const x = c.getContext('2d');
  const im = x.createImageData(s.w, s.h);
  for (let i = 0; i < s.mask.length; i++) { im.data[i * 4 + 3] = s.mask[i]; }
  x.putImageData(im, 0, 0);
  const k = Math.min(1, max / Math.max(s.w, s.h));
  if (k >= 1) return c;
  const d = document.createElement('canvas');
  d.width = Math.max(1, Math.round(s.w * k)); d.height = Math.max(1, Math.round(s.h * k));
  const dx = d.getContext('2d');
  dx.imageSmoothingQuality = 'high';
  dx.drawImage(c, 0, 0, d.width, d.height);
  return d;
}
