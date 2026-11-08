// PSD 내보내기 (레이어 유지, RLE 압축). 포토샵/클립스튜디오/크리타에서 열 수 있다.
import { alphaBounds } from './raster.js';
import { tyshBlock } from './psdtext.js';
import { socoBlock, vmskBlock, vstkBlock, shapeLoopsFromCanvas, cssRGB } from './psdvector.js';
import { createCanvas } from './util.js';
import { bubblePaths } from '../tools/comic.js';

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
 * 말풍선·컷·벡터 레이어 → 포토샵 셰이프 레이어 사양 목록. 못 만들면 null (그림 레이어로만 저장)
 * 반환: [{ name, canvas, blocks: [Uint8Array] }] — 2개 이상이면 폴더로 묶어 저장
 */
function shapeSpecs(l, doc) {
  const W = doc.width, H = doc.height;
  const m = l.meta;
  if (l.kind === 'panel' && Array.isArray(m?.panels) && m.panels.length) {
    // 컷: 문서 사각형에서 컷들을 뺀 모양 = "컷 바깥 용지색" 칠, 선은 바깥쪽(= 컷 안쪽)으로
    const loops = [{ pts: [0, 0, W, 0, W, H, 0, H], hole: false }];
    for (const pn of m.panels) {
      const q = pn.pts || [{ x: pn.x, y: pn.y }, { x: pn.x + pn.w, y: pn.y }, { x: pn.x + pn.w, y: pn.y + pn.h }, { x: pn.x, y: pn.y + pn.h }];
      loops.push({ pts: q.flatMap(v => [v.x, v.y]), hole: true });
    }
    return [{ name: l.name, canvas: l.canvas, blocks: [
      socoBlock(doc.paper?.color || '#ffffff'),
      vmskBlock(loops, W, H),
      vstkBlock({ fill: m.maskOutside !== false, stroke: true, width: m.line || 0, color: m.color || '#000000', align: 'outside', join: 'miter' }),
    ] }];
  }
  if (l.kind === 'bubble' && Array.isArray(m?.bubbles) && m.bubbles.length) {
    // 이어 붙인 풍선(group)은 한 덩어리로
    const units = [], seen = new Set();
    for (const b of m.bubbles) {
      if (seen.has(b)) continue;
      const g = b.group ? m.bubbles.filter(x => x.group === b.group) : [b];
      g.forEach(x => seen.add(x));
      units.push(g);
    }
    // 풍선(또는 이어 붙인 풍선 묶음)마다 셰이프 레이어 하나 → 앱처럼 겹친 풍선도 각자 테두리가 보임
    const out = [];
    units.forEach((g, ui) => {
      const st = g[0];
      // 풍선(꼬리·테두리 포함) 둘레만 그리는 작은 캔버스 (큰 원고에서도 빠르게)
      const box = unitBox(g, W, H);
      if (!box) return;
      // 칠 모양(테두리 제외)의 윤곽 → 경로, 선은 바깥쪽으로 테두리 굵기만큼 (앱도 2배 굵기 선을 칠 아래에 그림)
      const mask = createCanvas(box.w, box.h), mx = mask.getContext('2d');
      mx.translate(-box.x, -box.y);
      mx.fillStyle = '#000';
      for (const b of g) { const { body, tail } = bubblePaths(b); mx.fill(body); if (tail) mx.fill(tail); }
      const lb = alphaBounds(mask);
      const loops = shapeLoopsFromCanvas(mask, lb).map(lp => ({ ...lp, pts: lp.pts.map((v, i) => v + (i % 2 ? box.y : box.x)) }));
      if (!loops.length) return;
      let canvas = l.canvas, offset = null;
      if (units.length > 1) {
        canvas = createCanvas(box.w, box.h);
        const cx = canvas.getContext('2d');
        cx.translate(-box.x, -box.y);
        drawBubbleSet(cx, [g]);
        offset = { x: box.x, y: box.y };
      }
      out.push({ name: units.length === 1 ? l.name : `${l.name} ${ui + 1}`, canvas, offset, blocks: [
        socoBlock(st.fill), vmskBlock(loops, W, H),
        vstkBlock({ fill: cssRGB(st.fill)[3] > 0, stroke: st.line > 0, width: st.line || 0, color: st.stroke || '#000000', align: 'outside', join: 'round' }),
      ] });
    });
    return out.length ? out : null;
  }
  if (l.kind === 'vector' && Array.isArray(m?.strokes) && m.strokes.length) {
    // 색마다 셰이프 레이어 하나: 그 색 선들이 칠한 모양의 윤곽 (굵기·필압이 반영된 모양 그대로)
    const b = alphaBounds(l.canvas);
    if (!b) return null;
    const colors = [...new Set(m.strokes.map(s => s.color || '#000000'))];
    const rgbs = colors.map(c => cssRGB(c));
    const nearest = (r, g, bb) => {
      let best = 0, bd = Infinity;
      rgbs.forEach((c, i) => { const d = (c[0] - r) ** 2 + (c[1] - g) ** 2 + (c[2] - bb) ** 2; if (d < bd) { bd = d; best = i; } });
      return best;
    };
    const out = [];
    colors.forEach((c, ci) => {
      const keep = colors.length === 1 ? null : (r, g, bb, a) => a > 0 && nearest(r, g, bb) === ci;
      const loops = shapeLoopsFromCanvas(l.canvas, b, keep);
      if (!loops.length) return;
      let canvas = l.canvas;
      if (colors.length > 1) {
        canvas = createCanvas(W, H);
        const src = l.canvas.getContext('2d').getImageData(b.x, b.y, b.w, b.h);
        const d = src.data;
        for (let p = 0; p < d.length; p += 4) if (d[p + 3] && nearest(d[p], d[p + 1], d[p + 2]) !== ci) d[p + 3] = 0;
        canvas.getContext('2d').putImageData(src, b.x, b.y);
      }
      out.push({ name: colors.length === 1 ? l.name : `${l.name} (${c})`, canvas, blocks: [socoBlock(c), vmskBlock(loops, W, H)] });
    });
    return out.length ? out : null;
  }
  return null;
}

/** 풍선 묶음이 차지하는 영역 (꼬리·테두리·구름 방울·뾰족 여유 포함, 문서 안으로 자름) */
function unitBox(g, W, H) {
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const b of g) {
    const pad = (b.line || 0) * 2 + Math.max(b.w, b.h) * 0.25 + 4;
    const xs = [b.x - pad, b.x + b.w + pad], ys = [b.y - pad, b.y + b.h + pad];
    if (b.tail) { xs.push(b.tail.x - pad, b.tail.x + pad); ys.push(b.tail.y - pad, b.tail.y + pad); }
    x0 = Math.min(x0, ...xs); y0 = Math.min(y0, ...ys); x1 = Math.max(x1, ...xs); y1 = Math.max(y1, ...ys);
  }
  x0 = Math.max(0, Math.floor(x0)); y0 = Math.max(0, Math.floor(y0));
  x1 = Math.min(W, Math.ceil(x1)); y1 = Math.min(H, Math.ceil(y1));
  return x1 > x0 && y1 > y0 ? { x: x0, y: y0, w: x1 - x0, h: y1 - y0 } : null;
}

/** 말풍선 몇 개만 그리기 (앱과 같은 방식: 같은 group 은 선을 먼저 모두 그리고 칠을 덮음) */
function drawBubbleSet(ctx, groups) {
  for (const g of groups) {
    const paths = g.map(b => ({ b, ...bubblePaths(b) }));
    ctx.save();
    ctx.lineJoin = 'round'; ctx.lineCap = 'round';
    for (const { b, body, tail } of paths) if (b.line > 0) { ctx.strokeStyle = b.stroke; ctx.lineWidth = b.line * 2; ctx.stroke(body); if (tail) ctx.stroke(tail); }
    for (const { b, body, tail } of paths) { ctx.fillStyle = b.fill; ctx.fill(body); if (tail) ctx.fill(tail); }
    ctx.restore();
  }
}

/**
 * doc 을 PSD Blob 으로. flatten: 합성 이미지 캔버스(문서 크기, 용지 포함)
 * opts.vector === false 면 말풍선·컷·벡터도 그림 레이어로만, opts.extra = { 레이어id: [추가 정보 블록] } (시험용)
 */
export function writePSD(doc, flatten, onProgress, opts = {}) {
  const W = doc.width, H = doc.height;
  // 기록 사양: { name, canvas, divider, folder, expanded, blend, opacity, clip, visible, alphaLock, mask, maskOn, text, blocks }
  const props = (l) => ({ blend: l.blend, opacity: l.opacity, clip: l.clip, visible: l.visible, alphaLock: l.alphaLock });
  const specs = [];
  // 폴더는 PSD 규칙대로: [그룹 끝 표시] 자식들… [폴더] (아래→위)
  const walk = (folder) => {
    for (const l of doc.childrenOf(folder)) {
      if (l.isFolder) {
        specs.push({ divider: true });
        walk(l);
        specs.push({ folder: true, name: l.name, expanded: l.expanded, ...props(l) });
        continue;
      }
      let shapes = null;
      if (opts.vector !== false) {
        try { shapes = shapeSpecs(l, doc); } catch (err) { console.warn('PSD 셰이프 저장 실패 → 그림으로', l.name, err); }
      }
      if (shapes?.length === 1) {
        specs.push({ name: l.name, canvas: shapes[0].canvas, offset: shapes[0].offset, mask: l.mask, maskOn: l.maskOn, blocks: shapes[0].blocks, ...props(l) });
      } else if (shapes?.length > 1) {
        specs.push({ divider: true });
        for (const s of shapes) specs.push({ name: s.name, canvas: s.canvas, offset: s.offset, blocks: s.blocks, blend: 'source-over', opacity: 1, visible: true });
        specs.push({ folder: true, name: l.name, expanded: false, ...props(l) });
      } else {
        specs.push({ name: l.name, canvas: l.canvas, mask: l.mask, maskOn: l.maskOn, text: l.kind === 'text' && l.meta?.text && !l.meta.sfx ? l.meta : null, blocks: opts.extra?.[l.id], ...props(l) });
      }
    }
  };
  walk(null);
  const records = new Writer(), channelData = new Writer();
  specs.forEach((e, idx) => {
    onProgress?.(idx / specs.length);
    let chans, b = { x: 0, y: 0, w: 0, h: 0 };
    if (e.divider || e.folder) {
      chans = [-1, 0, 1, 2].map(id => ({ id, counts: new Writer(), body: [], length: 0 }));
    } else {
      const src = e.canvas;
      const lb = alphaBounds(src);
      const off = e.offset || { x: 0, y: 0 };
      if (lb) b = { x: lb.x + off.x, y: lb.y + off.y, w: lb.w, h: lb.h };
      if (b.w && b.h) {
        const img = src.getContext('2d').getImageData(lb.x, lb.y, lb.w, lb.h).data;
        chans = [[-1, 3], [0, 0], [1, 1], [2, 2]].map(([id, ch]) => ({ id, ...rleChannel(img, b.w, b.h, ch) }));
      } else {
        chans = [-1, 0, 1, 2].map(id => ({ id, counts: new Writer(), body: [], length: 0 }));
      }
      // 레이어 마스크: 문서 전체 크기의 회색 채널 (흰색 = 보임)
      if (e.mask) {
        const md = e.mask.getContext('2d').getImageData(0, 0, W, H).data;
        chans.push({ id: -2, ...rleChannel(md, W, H, 3) });
      }
    }
    records.i32(b.y); records.i32(b.x); records.i32(b.y + b.h); records.i32(b.x + b.w);
    records.u16(chans.length);
    for (const c of chans) { records.i16(c.id); records.u32(c.length + 2); }
    records.ascii('8BIM');
    records.ascii(e.divider ? 'norm' : (BLEND[e.blend] || 'norm'));
    records.u8(e.divider ? 255 : Math.round((e.opacity ?? 1) * 255));
    records.u8(!e.divider && !e.folder && e.clip ? 1 : 0);
    records.u8(e.divider ? 0 : ((e.alphaLock ? 1 : 0) | (e.visible === false ? 2 : 0)));
    records.u8(0);
    const extra = new Writer();
    if (!e.divider && !e.folder && e.mask) {
      extra.u32(20);
      extra.i32(0); extra.i32(0); extra.i32(H); extra.i32(W);
      extra.u8(0);                         // 사각형 밖 기본값: 가림
      extra.u8(e.maskOn ? 0 : 2);          // 2 = 마스크 꺼짐
      extra.u16(0);
    } else extra.u32(0);
    extra.u32(0);           // 블렌딩 범위 없음
    const name = e.divider ? '</Layer group>' : e.name;
    extra.writer(pascalName(name));
    extra.writer(unicodeName(name));
    if (e.divider || e.folder) {
      extra.ascii('8BIM'); extra.ascii('lsct'); extra.u32(4);
      extra.u32(e.divider ? 3 : (e.expanded ? 1 : 2));
    }
    // 셰이프 레이어: 단색 칠 + 벡터 마스크 + 선 (그림은 위에서 함께 저장 → 벡터를 모르는 프로그램에서도 보임)
    for (const blk of e.blocks || []) extra.bytes(blk);
    // 글자 레이어는 포토샵 글자로도 (그림은 그대로 함께 저장 → 어느 프로그램에서나 보임). 효과음은 그림으로만
    if (e.text) {
      try { extra.bytes(tyshBlock(e.text, b)); } catch (err) { console.warn('PSD 글자 저장 실패', err); }
    }
    records.u32(extra.length);
    records.writer(extra);
    for (const c of chans) {
      channelData.u16(c.length ? 1 : 0);
      if (c.length) { channelData.writer(c.counts); for (const p of c.body) channelData.bytes(p); }
    }
  });

  const layerInfo = new Writer();
  layerInfo.i16(specs.length);
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
