// 스프라이트 시트 내보내기·가져오기 (Aseprite·TexturePacker 형식 JSON) + .aseprite 파일 열기
import { app } from '../app.js';
import { createCanvas, canvasToBlob, safeName, fileStamp, downloadBlob } from './util.js';
import { server } from './io.js';
import { Doc } from './doc.js';
import * as A from './anim.js';

const DIRS = { fwd: 'forward', rev: 'reverse', ping: 'pingpong' };
const DIRS_BACK = { forward: 'fwd', reverse: 'rev', pingpong: 'ping', pingpong_reverse: 'ping' };

/** 투명한 가장자리를 잘라낸 영역 (없으면 1x1) */
function opaqueBounds(c) {
  const { width: w, height: h } = c, d = c.getContext('2d').getImageData(0, 0, w, h).data;
  let x0 = w, y0 = h, x1 = -1, y1 = -1;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (d[(y * w + x) * 4 + 3]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
  return x1 < 0 ? { x: 0, y: 0, w: 1, h: 1 } : { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
}

/**
 * 시트 만들기. o: { scale, layout: 'grid'|'h'|'v', cols, pad, trim, tag (태그 번호|null), bg (null = 투명), json: 'hash'|'array', camera }
 * → { canvas, json }
 */
export function buildSheet(doc, o = {}) {
  const scale = o.scale ?? 1, pad = o.pad ?? 0;
  const { order } = A.exportOrder(o.tag ?? null, doc);
  // 같은 프레임 번호가 핑퐁으로 또 나오면 그림은 한 번만
  const uniq = [...new Set(order)];
  const imgs = uniq.map((f) => {
    let c = app.compositor.flatten(doc, { scale, frame: f, paper: !!o.bg, background: o.bg || undefined });   // 투명이면 종이도 빼고
    if (o.camera) c = A.frameCanvas(f, scale, o.bg || null, doc);
    const full = { x: 0, y: 0, w: c.width, h: c.height };
    const r = o.trim ? opaqueBounds(c) : full;
    return { f, c, r, full };
  });
  const n = imgs.length;
  const cw = Math.max(...imgs.map(i => i.r.w)), ch = Math.max(...imgs.map(i => i.r.h));
  const cols = o.layout === 'h' ? n : o.layout === 'v' ? 1 : Math.max(1, o.cols || Math.ceil(Math.sqrt(n)));
  const rows = Math.ceil(n / cols);
  const sheet = createCanvas(cols * cw + (cols + 1) * pad, rows * ch + (rows + 1) * pad), sx = sheet.getContext('2d');
  if (o.bg) { sx.fillStyle = o.bg; sx.fillRect(0, 0, sheet.width, sheet.height); }
  const base = safeName(doc.name);
  const frames = [];
  imgs.forEach((im, k) => {
    const x = pad + (k % cols) * (cw + pad), y = pad + Math.floor(k / cols) * (ch + pad);
    sx.drawImage(im.c, im.r.x, im.r.y, im.r.w, im.r.h, x, y, im.r.w, im.r.h);
    frames.push({
      filename: `${base} ${im.f}.png`,
      frame: { x, y, w: im.r.w, h: im.r.h }, rotated: false, trimmed: !!o.trim,
      spriteSourceSize: { x: im.r.x, y: im.r.y, w: im.r.w, h: im.r.h }, sourceSize: { w: im.full.w, h: im.full.h },
      duration: Math.round(A.frameMs(im.f, doc)),
    });
  });
  // 태그는 시트 안 순서로 (태그 하나만 내보내면 0..끝)
  const pos = new Map(uniq.map((f, k) => [f, k]));
  const tags = (o.tag != null ? [doc.anim.tags[o.tag]] : doc.anim.tags || []).filter(Boolean).map(t => {
    const inside = uniq.filter(f => f >= t.from && f <= t.to);
    return inside.length ? { name: t.name, from: pos.get(inside[0]), to: pos.get(inside.at(-1)), direction: DIRS[t.dir] || 'forward', color: t.color } : null;
  }).filter(Boolean);
  const meta = { app: 'ComicPad', version: '1', image: `${base}.png`, format: 'RGBA8888', size: { w: sheet.width, h: sheet.height }, scale: String(scale), frameTags: tags,
    layers: A.tracks(doc).map(t => ({ name: t.name, opacity: Math.round((t.opacity ?? 1) * 255), blendMode: 'normal' })) };
  const json = o.json === 'array' ? { frames, meta } : { frames: Object.fromEntries(frames.map(({ filename, ...rest }) => [filename, rest])), meta };
  return { canvas: sheet, json, cols, rows, cell: { w: cw, h: ch } };
}

export async function exportSheet(o = {}) {
  const doc = app.doc;
  const { canvas, json } = buildSheet(doc, o);
  const base = `${safeName(doc.name)}_시트_${fileStamp()}`;
  json.meta.image = `${base}.png`;
  const png = await canvasToBlob(canvas), js = new Blob([JSON.stringify(json, null, 1)], { type: 'application/json' });
  if (!server.online) { downloadBlob(png, `${base}.png`); downloadBlob(js, `${base}.json`); }
  else { await server.put('exports', `${base}.png`, png); await server.put('exports', `${base}.json`, js); }
  app.toast(`스프라이트 시트 저장: data/exports/${base}.png (+ .json, ${canvas.width}×${canvas.height})`);
  return { canvas, json };
}

/** 시트 그림(+ 있으면 JSON) → 프레임 목록 [{ canvas, duration }] 과 태그 */
export function sliceSheet(img, o = {}) {
  const out = [];
  let tags = [];
  if (o.json) {
    const fr = Array.isArray(o.json.frames) ? o.json.frames : Object.entries(o.json.frames).map(([filename, v]) => ({ filename, ...v }));
    for (const f of fr) {
      const src = f.sourceSize || { w: f.frame.w, h: f.frame.h }, ss = f.spriteSourceSize || { x: 0, y: 0 };
      const c = createCanvas(src.w, src.h);
      c.getContext('2d').drawImage(img, f.frame.x, f.frame.y, f.frame.w, f.frame.h, ss.x, ss.y, f.frame.w, f.frame.h);
      out.push({ canvas: c, duration: f.duration || null });
    }
    tags = (o.json.meta?.frameTags || []).map(t => ({ name: t.name, from: t.from, to: t.to, dir: DIRS_BACK[t.direction] || 'fwd', color: t.color || '#3d7be0' }));
  } else {
    const cols = Math.max(1, o.cols | 0), rows = Math.max(1, o.rows | 0), pad = o.pad | 0, margin = o.margin | 0;
    const fw = o.fw || Math.floor((img.width - 2 * margin - (cols - 1) * pad) / cols), fh = o.fh || Math.floor((img.height - 2 * margin - (rows - 1) * pad) / rows);
    for (let r = 0; r < rows; r++) for (let k = 0; k < cols; k++) {
      const c = createCanvas(fw, fh), x = c.getContext('2d');
      x.drawImage(img, margin + k * (fw + pad), margin + r * (fh + pad), fw, fh, 0, 0, fw, fh);
      if (o.skipEmpty && opaqueBounds(c).w === 1 && !x.getImageData(0, 0, 1, 1).data[3]) continue;
      out.push({ canvas: c, duration: null });
    }
  }
  return { frames: out, tags };
}

/** 프레임 그림들 → 새 작품 (트랙 하나) 또는 지금 작품의 새 트랙 */
export function framesToDoc(frames, { name = '스프라이트', tags = [], into = null, fps = 12 } = {}) {
  const w = Math.max(...frames.map(f => f.canvas.width)), h = Math.max(...frames.map(f => f.canvas.height));
  let doc = into;
  if (!doc) {
    doc = new Doc(w, h, { name });
    doc.anim.fps = fps;
    const track = doc.createLayer({ name: '애니메이션', kind: 'folder', meta: { anim: true, xs: [] } });
    doc.layers.push(track);
    frames.forEach((f, i) => {
      const l = doc.createLayer({ name: String(i + 1), parent: track.id });
      l.ctx.drawImage(f.canvas, 0, 0);
      doc.layers.splice(doc.layers.indexOf(track), 0, l);
      track.meta.xs.push(l.id);
    });
    doc.anim.length = frames.length;
    if (frames.some(f => f.duration)) doc.anim.ms = frames.map(f => f.duration || null);
    doc.anim.tags = tags;
    doc.anim.v = 2;
    doc.activeId = track.meta.xs[0];
    return doc;
  }
  // 지금 작품에 새 트랙으로
  A.editAnim('스프라이트 시트 가져오기', () => {
    const track = A.addTrackRaw(name);
    frames.forEach((f, i) => {
      const l = A.newCelRaw(track);
      l.ctx.drawImage(f.canvas, 0, 0); l.touch();
      track.meta.xs[i] = l.id;
    });
    if (tags.length) (doc.anim.tags ||= []).push(...tags);
  });
  return doc;
}

// ---------------------------------------------------------------------
// .aseprite / .ase 읽기 (공개된 파일 형식 문서 기준: 레이어·셀·연결 셀·태그·프레임 시간·팔레트)
// ---------------------------------------------------------------------
const BLEND = ['source-over', 'multiply', 'screen', 'overlay', 'darken', 'lighten', 'color-dodge', 'color-burn', 'hard-light', 'soft-light', 'difference', 'exclusion', 'hue', 'saturation', 'color', 'luminosity', 'lighter', 'source-over', 'source-over'];

async function inflate(bytes) {
  const ds = new DecompressionStream('deflate');                 // zlib 형식
  return new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(ds)).arrayBuffer());
}

export async function readAseprite(buf) {
  const v = new DataView(buf), u8 = new Uint8Array(buf);
  if (v.getUint16(4, true) !== 0xA5E0) throw new Error('Aseprite 파일이 아닙니다');
  const nFrames = v.getUint16(6, true), W = v.getUint16(8, true), H = v.getUint16(10, true), depth = v.getUint16(12, true);
  const transIdx = u8[28];
  let pal = [];
  const layers = [], frames = [], tags = [];
  const str = (p) => { const n = v.getUint16(p, true); return { s: new TextDecoder().decode(u8.subarray(p + 2, p + 2 + n)), end: p + 2 + n }; };
  let p = 128;
  for (let fi = 0; fi < nFrames; fi++) {
    const fBytes = v.getUint32(p, true);
    if (v.getUint16(p + 4, true) !== 0xF1FA) throw new Error('프레임 머리 이상');
    const oldN = v.getUint16(p + 6, true), dur = v.getUint16(p + 8, true), newN = v.getUint32(p + 12, true);
    const nChunks = newN || oldN, frame = { dur, cels: [] };
    let q = p + 16;
    for (let c = 0; c < nChunks; c++) {
      const size = v.getUint32(q, true), type = v.getUint16(q + 4, true), d = q + 6;
      if (type === 0x2004) {
        const flags = v.getUint16(d, true), lt = v.getUint16(d + 2, true), level = v.getUint16(d + 4, true), blend = v.getUint16(d + 10, true), op = u8[d + 12];
        layers.push({ visible: !!(flags & 1), type: lt, level, blend, opacity: op / 255, name: str(d + 16).s });
      } else if (type === 0x2005) {
        const li = v.getUint16(d, true), x = v.getInt16(d + 2, true), y = v.getInt16(d + 4, true), op = u8[d + 6], ct = v.getUint16(d + 7, true);
        const cel = { layer: li, x, y, opacity: op / 255, type: ct };
        if (ct === 1) cel.link = v.getUint16(d + 16, true);
        else if (ct === 0 || ct === 2) {
          cel.w = v.getUint16(d + 16, true); cel.h = v.getUint16(d + 18, true);
          const raw = u8.subarray(d + 20, q + size);
          cel.px = ct === 2 ? await inflate(raw) : raw.slice();
        }
        frame.cels.push(cel);
      } else if (type === 0x2019) {
        const first = v.getUint32(d + 4, true), last = v.getUint32(d + 8, true);
        let e = d + 20;
        for (let i = first; i <= last; i++) {
          const fl = v.getUint16(e, true);
          pal[i] = [u8[e + 2], u8[e + 3], u8[e + 4], u8[e + 5]];
          e += 6;
          if (fl & 1) e = str(e).end;
        }
      } else if ((type === 0x0004 || type === 0x0011) && !pal.length) {
        const packets = v.getUint16(d, true);
        let e = d + 2, idx = 0;
        for (let k = 0; k < packets; k++) {
          idx += u8[e]; let cnt = u8[e + 1] || 256; e += 2;
          for (let i = 0; i < cnt; i++, idx++, e += 3) pal[idx] = type === 0x0011 ? [u8[e] * 4, u8[e + 1] * 4, u8[e + 2] * 4, 255] : [u8[e], u8[e + 1], u8[e + 2], 255];
        }
      } else if (type === 0x2018) {
        const n = v.getUint16(d, true);
        let e = d + 10;
        for (let k = 0; k < n; k++) {
          const from = v.getUint16(e, true), to = v.getUint16(e + 2, true), dir = u8[e + 4];
          const rgb = [u8[e + 13], u8[e + 14], u8[e + 15]];
          const name = str(e + 17);
          tags.push({ name: name.s, from, to, dir: dir === 1 ? 'rev' : dir >= 2 ? 'ping' : 'fwd', color: '#' + rgb.map(c => c.toString(16).padStart(2, '0')).join('') });
          e = name.end;
        }
      }
      q += size;
    }
    frames.push(frame);
    p += fBytes;
  }
  return { W, H, depth, transIdx, pal, layers, frames, tags };
}

/** 셀 픽셀 → 캔버스 (RGBA / 회색 / 팔레트) */
function celCanvas(f, cel, isBg) {
  const c = createCanvas(cel.w, cel.h), x = c.getContext('2d'), img = x.createImageData(cel.w, cel.h), d = img.data, s = cel.px;
  for (let i = 0; i < cel.w * cel.h; i++) {
    if (f.depth === 32) { d.set(s.subarray(i * 4, i * 4 + 4), i * 4); }
    else if (f.depth === 16) { d[i * 4] = d[i * 4 + 1] = d[i * 4 + 2] = s[i * 2]; d[i * 4 + 3] = s[i * 2 + 1]; }
    else {
      const k = s[i], col = f.pal[k] || [0, 0, 0, 255];
      d[i * 4] = col[0]; d[i * 4 + 1] = col[1]; d[i * 4 + 2] = col[2]; d[i * 4 + 3] = k === f.transIdx && !isBg ? 0 : col[3];
    }
  }
  x.putImageData(img, 0, 0);
  return c;
}

/** .aseprite → ComicPad 작품 (레이어마다 트랙, 연결 셀은 같은 셀로) */
export async function asepriteToDoc(buf, name = 'Aseprite') {
  const f = await readAseprite(buf);
  const doc = new Doc(f.W, f.H, { name });
  const durs = f.frames.map(fr => fr.dur || 100);
  const common = durs.slice().sort((a, b) => durs.filter(v => v === b).length - durs.filter(v => v === a).length)[0];
  doc.anim.fps = Math.max(1, Math.min(60, Math.round(1000 / common)));
  if (durs.some(d => Math.abs(d - 1000 / doc.anim.fps) > 1)) doc.anim.ms = durs.slice();
  doc.anim.length = f.frames.length;
  doc.anim.tags = f.tags;
  doc.anim.v = 2;
  // 그룹 이름은 앞에 붙여서 (그룹/레이어)
  const path = [];
  const tracks = f.layers.map((L, li) => {
    path[L.level] = L.name; path.length = L.level + 1;
    if (L.type !== 0) return null;
    const t = doc.createLayer({ name: path.join('/'), kind: 'folder', meta: { anim: true, xs: Array(f.frames.length).fill(null) } });
    t.visible = L.visible; t.opacity = L.opacity;
    doc.layers.push(t);
    return { t, L, li, byFrame: [] };
  });
  f.frames.forEach((fr, fi) => {
    for (const cel of fr.cels) {
      const T = tracks[cel.layer];
      if (!T) continue;
      if (cel.type === 1) { T.byFrame[fi] = T.byFrame[cel.link]; T.t.meta.xs[fi] = T.byFrame[fi]?.id || null; continue; }
      if (!cel.px) continue;
      const l = doc.createLayer({ name: String(fi + 1), parent: T.t.id });
      l.opacity = cel.opacity; l.blend = BLEND[T.L.blend] || 'source-over';
      l.ctx.drawImage(celCanvas(f, cel, T.li === 0 && /background|배경/i.test(T.L.name)), cel.x, cel.y);
      doc.layers.splice(doc.layers.indexOf(T.t), 0, l);
      T.byFrame[fi] = l;
      T.t.meta.xs[fi] = l.id;
    }
  });
  doc.importedPalette = f.pal.filter(Boolean).map(([r, g, b]) => '#' + [r, g, b].map(c => c.toString(16).padStart(2, '0')).join(''));
  // 픽셀아트 설정(가까운 픽셀 보기·팔레트)과 이어지게
  doc.pixel = { ...(doc.pixel || {}), art: f.W <= 512 && f.H <= 512, lock: false, palette: doc.importedPalette.length ? doc.importedPalette.slice(0, 256) : (doc.pixel?.palette || null) };
  const first = tracks.find(Boolean);
  doc.activeId = first?.t.meta.xs.find(Boolean) || first?.t.id || doc.layers[0]?.id;
  return doc;
}

// ---------------------------------------------------------------------
// .aseprite 쓰기: 위쪽 레이어(폴더 밖 그림 레이어) + 트랙 → Aseprite 레이어, 같은 셀은 연결 셀로
// ---------------------------------------------------------------------
async function deflate(bytes) {
  const cs = new CompressionStream('deflate');                    // zlib 형식
  return new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(cs)).arrayBuffer());
}
const BLEND_ID = Object.fromEntries(BLEND.map((b, i) => [b, i]).reverse());

export async function docToAseprite(doc) {
  const W = doc.width, H = doc.height;
  const enc = new TextEncoder();
  const top = doc.layers.filter(l => !doc.parentOf(l) && (A.isTrack(l) || (!l.isFolder && l.kind !== 'adjust' && !l.draft)));
  const n = Math.max(1, A.frameCount(doc));
  class Buf { constructor() { this.a = []; } u8(...v) { this.a.push(...v); } u16(v) { this.a.push(v & 255, (v >> 8) & 255); } i16(v) { this.u16(v & 0xffff); } u32(v) { this.u16(v & 0xffff); this.u16((v >>> 16) & 0xffff); } str(s) { const b = enc.encode(s); this.u16(b.length); for (const x of b) this.a.push(x); } bytes(b) { for (let i = 0; i < b.length; i++) this.a.push(b[i]); } }
  const chunk = (type, fill) => { const b = new Buf(); fill(b); const out = new Buf(); out.u32(b.a.length + 6); out.u16(type); out.a.push(...b.a); return out.a; };
  const imgCel = async (li, layer, opacity) => {
    const c = layer.canvas, r = opaqueBounds(c);
    const d = c.getContext('2d').getImageData(r.x, r.y, r.w, r.h).data;
    const z = await deflate(new Uint8Array(d.buffer, d.byteOffset, d.byteLength));
    return chunk(0x2005, (b) => { b.u16(li); b.i16(r.x); b.i16(r.y); b.u8(Math.round(opacity * 255)); b.u16(2); b.i16(0); b.u8(0, 0, 0, 0, 0); b.u16(r.w); b.u16(r.h); b.bytes(z); });
  };
  const linkCel = (li, f) => chunk(0x2005, (b) => { b.u16(li); b.i16(0); b.i16(0); b.u8(255); b.u16(1); b.i16(0); b.u8(0, 0, 0, 0, 0); b.u16(f); });
  const frames = [];
  const first = top.map(() => new Map());                        // 레이어마다 셀 id → 처음 나온 프레임
  for (let f = 0; f < n; f++) {
    const chunks = [];
    if (f === 0) {
      top.forEach((l) => chunks.push(chunk(0x2004, (b) => { b.u16(l.visible ? 1 : 0); b.u16(0); b.u16(0); b.u16(0); b.u16(0); b.u16(A.isTrack(l) ? 0 : (BLEND_ID[l.blend] || 0)); b.u8(Math.round((l.opacity ?? 1) * 255)); b.u8(0, 0, 0); b.str(l.name || '레이어'); })));
      const tags = doc.anim.tags || [];
      if (tags.length) chunks.push(chunk(0x2018, (b) => {
        b.u16(tags.length); b.u8(0, 0, 0, 0, 0, 0, 0, 0);
        for (const t of tags) {
          const rgb = (t.color || '#3d7be0').slice(1).match(/../g).map(x => parseInt(x, 16));
          b.u16(t.from); b.u16(t.to); b.u8({ fwd: 0, rev: 1, ping: 2 }[t.dir] ?? 0); b.u16(0); b.u8(0, 0, 0, 0, 0, 0); b.u8(...rgb); b.u8(0); b.str(t.name || 'tag');
        }
      }));
    }
    for (let li = 0; li < top.length; li++) {
      const l = top[li];
      const cel = A.isTrack(l) ? A.celAt(l, f, doc) : l;
      if (!cel) continue;
      const seen = first[li].get(cel.id);
      if (seen != null) { chunks.push(linkCel(li, seen)); continue; }
      first[li].set(cel.id, f);
      chunks.push(await imgCel(li, cel, A.isTrack(l) ? (cel.opacity ?? 1) : 1));
    }
    frames.push({ chunks, dur: Math.round(A.frameMs(f, doc)) });
  }
  const body = new Buf();
  for (const fr of frames) {
    const flat = fr.chunks.flat();
    body.u32(16 + flat.length); body.u16(0xF1FA); body.u16(Math.min(0xffff, fr.chunks.length)); body.u16(fr.dur); body.u8(0, 0); body.u32(fr.chunks.length);
    body.a.push(...flat);
  }
  const head = new Buf();
  head.u32(128 + body.a.length); head.u16(0xA5E0); head.u16(n); head.u16(W); head.u16(H); head.u16(32); head.u32(1); head.u16(100); head.u32(0); head.u32(0);
  head.u8(0, 0, 0, 0); head.u16(0); head.u8(1, 1); head.i16(0); head.i16(0); head.u16(16); head.u16(16);
  for (let i = 0; i < 84; i++) head.u8(0);
  return new Blob([new Uint8Array(head.a), new Uint8Array(body.a)], { type: 'application/octet-stream' });
}

export async function exportAseprite() {
  const doc = app.doc;
  const blob = await docToAseprite(doc);
  const name = `${safeName(doc.name)}_${fileStamp()}.aseprite`;
  if (!server.online) downloadBlob(blob, name); else await server.put('exports', name, blob);
  app.toast(`Aseprite 파일 저장: data/exports/${name} (${(blob.size / 1024).toFixed(0)}KB)`);
}
