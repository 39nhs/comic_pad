// 인쇄용 내보내기 (PDF 한 파일 / 1비트 흑백 TIFF / PNG, 재단선 기준) + 책 펼침면 미리보기
import { app } from '../app.js';
import { h, createCanvas, canvasToBlob, safeName, fileStamp, downloadBlob, pickFile } from '../core/util.js';
import { Doc } from '../core/doc.js';
import { server } from '../core/io.js';
import { form, openModal } from './dialogs.js';
import { printRects } from './manuscript.js';
import { storyOf, listPages } from './story.js';
import { parseICC, makeTransform, buildCube, canvasToCmyk, proofCanvas } from '../core/icc.js';

// ---------------------------------------------------------------------
// 페이지 그림 만들기
// ---------------------------------------------------------------------
function areaOf(doc, area) {
  const r = printRects(doc);
  if (!r || area === 'all') return { x: 0, y: 0, w: doc.width, h: doc.height };
  return area === 'bleed' ? r.bleed : r.trim;
}

/** 문서 → 잘라낸 페이지 캔버스 (흰 바탕) */
function pageCanvas(doc, area, scale = 1) {
  const full = app.compositor.flatten(doc, { scale, paper: true, background: '#ffffff' });
  const q = areaOf(doc, area);
  const c = createCanvas(Math.max(1, Math.round(q.w * scale)), Math.max(1, Math.round(q.h * scale)));
  const x = c.getContext('2d');
  x.fillStyle = '#fff';
  x.fillRect(0, 0, c.width, c.height);
  x.drawImage(full, -q.x * scale, -q.y * scale);
  return c;
}

/** 밝기(0~255) 배열 */
function grayOf(c) {
  const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
  const g = new Uint8Array(c.width * c.height);
  for (let i = 0, j = 0; j < g.length; i += 4, j++) {
    const a = d[i + 3] / 255;   // 투명은 흰색으로
    g[j] = Math.round((0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2]) * a + 255 * (1 - a));
  }
  return g;
}

/** 1비트: 비트 1 = 흰색 (한 줄은 바이트 단위로 채움). dither 면 회색을 점으로 (플로이드-스타인버그) */
function toBits(gray, w, hh, threshold = 128, dither = false) {
  const rowBytes = Math.ceil(w / 8);
  const out = new Uint8Array(rowBytes * hh);
  const err = dither ? new Float32Array(gray) : null;
  for (let y = 0; y < hh; y++) {
    for (let x = 0; x < w; x++) {
      const i = y * w + x;
      const v = dither ? err[i] : gray[i];
      const white = v >= threshold;
      if (white) out[y * rowBytes + (x >> 3)] |= 0x80 >> (x & 7);
      if (dither) {
        const e = v - (white ? 255 : 0);
        if (x + 1 < w) err[i + 1] += e * 7 / 16;
        if (y + 1 < hh) {
          if (x > 0) err[i + w - 1] += e * 3 / 16;
          err[i + w] += e * 5 / 16;
          if (x + 1 < w) err[i + w + 1] += e / 16;
        }
      }
    }
  }
  return out;
}

async function deflate(u8) {
  const stream = new Blob([u8]).stream().pipeThrough(new CompressionStream('deflate'));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

// ---------------------------------------------------------------------
// PDF (페이지마다 그림 한 장)
// ---------------------------------------------------------------------
const te = new TextEncoder();

/** pages: [{ wPt, hPt, w, h, bpc, cs, filter, data }] */
export function buildPDF(pages, opt = {}) {
  const chunks = [];
  let off = 0;
  const offsets = [];
  const put = (x) => { const b = typeof x === 'string' ? te.encode(x) : x; chunks.push(b); off += b.length; };
  const obj = (n, body, stream) => {
    offsets[n] = off;
    put(`${n} 0 obj\n${body}\n`);
    if (stream) { put('stream\n'); put(stream); put('\nendstream\n'); }
    put('endobj\n');
  };
  put('%PDF-1.4\n%\xE2\xE3\xCF\xD3\n');
  const n = pages.length;
  const kids = pages.map((_, i) => `${3 + i * 3} 0 R`).join(' ');
  const iccObj = 3 + n * 3;
  const ascii = (t) => String(t || 'CMYK').replace(/[^\x20-\x7e]/g, '').replace(/[()\\]/g, '');
  obj(1, opt.intent
    ? `<< /Type /Catalog /Pages 2 0 R /OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (${ascii(opt.intent.name)}) /Info (${ascii(opt.intent.name)}) /DestOutputProfile ${iccObj} 0 R >>] >>`
    : '<< /Type /Catalog /Pages 2 0 R >>');
  obj(2, `<< /Type /Pages /Kids [${kids}] /Count ${n} >>`);
  pages.forEach((p, i) => {
    const pg = 3 + i * 3, content = pg + 1, img = pg + 2;
    const cs = te.encode(`q ${p.wPt.toFixed(2)} 0 0 ${p.hPt.toFixed(2)} 0 0 cm /Im0 Do Q`);
    obj(pg, `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${p.wPt.toFixed(2)} ${p.hPt.toFixed(2)}] /Resources << /XObject << /Im0 ${img} 0 R >> >> /Contents ${content} 0 R >>`);
    obj(content, `<< /Length ${cs.length} >>`, cs);
    obj(img, `<< /Type /XObject /Subtype /Image /Width ${p.w} /Height ${p.h} /ColorSpace /${p.cs} /BitsPerComponent ${p.bpc} /Filter /${p.filter} /Length ${p.data.length} >>`, p.data);
  });
  if (opt.intent) obj(iccObj, `<< /N 4 /Length ${opt.intent.icc.length} >>`, opt.intent.icc);
  const xref = off;
  const total = 3 + n * 3 + (opt.intent ? 1 : 0);
  let x = `xref\n0 ${total}\n0000000000 65535 f \n`;
  for (let i = 1; i < total; i++) x += `${String(offsets[i]).padStart(10, '0')} 00000 n \n`;
  put(x);
  put(`trailer\n<< /Size ${total} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`);
  return new Blob(chunks, { type: 'application/pdf' });
}

async function pdfPage(c, mode, dpi, o) {
  const wPt = c.width / dpi * 72, hPt = c.height / dpi * 72;
  if (mode === 'cmyk') return { wPt, hPt, w: c.width, h: c.height, bpc: 8, cs: 'DeviceCMYK', filter: 'FlateDecode', data: await deflate(await canvasToCmyk(c, o.cube)) };
  if (mode === 'color') {
    const data = new Uint8Array(await (await canvasToBlob(c, 'image/jpeg', 0.92)).arrayBuffer());
    return { wPt, hPt, w: c.width, h: c.height, bpc: 8, cs: 'DeviceRGB', filter: 'DCTDecode', data };
  }
  const g = grayOf(c);
  if (mode === 'gray') return { wPt, hPt, w: c.width, h: c.height, bpc: 8, cs: 'DeviceGray', filter: 'FlateDecode', data: await deflate(g) };
  return { wPt, hPt, w: c.width, h: c.height, bpc: 1, cs: 'DeviceGray', filter: 'FlateDecode', data: await deflate(toBits(g, c.width, c.height, o.threshold, o.dither)) };
}

// ---------------------------------------------------------------------
// 8비트 TIFF: 그레이(1)·RGB(3)·CMYK(4), Deflate 압축, 인쇄 프로파일 포함
// ---------------------------------------------------------------------
export async function tiff8(data, w, hgt, spp, dpi, icc = null) {
  const z = await deflate(data);
  const photometric = spp === 1 ? 1 : spp === 3 ? 2 : 5;
  const tags = [[256, 4, 1, w], [257, 4, 1, hgt], [258, 3, spp, spp === 1 ? 8 : 'bps'], [259, 3, 1, 8], [262, 3, 1, photometric],
    [273, 4, 1, 'data'], [277, 3, 1, spp], [278, 4, 1, hgt], [279, 4, 1, z.length], [282, 5, 1, 'xres'], [283, 5, 1, 'yres'], [284, 3, 1, 1], [296, 3, 1, 2]];
  if (spp === 4) tags.push([332, 3, 1, 1]);                    // InkSet = CMYK
  if (icc) tags.push([34675, 7, icc.length, 'icc']);
  tags.sort((a, b) => a[0] - b[0]);
  const ifd = 8, ifdSize = 2 + tags.length * 12 + 4;
  let extra = ifd + ifdSize;
  const place = {};
  const reserve = (key, n) => { place[key] = extra; extra += n + (n % 2); };
  if (spp > 1) reserve('bps', spp * 2);
  reserve('xres', 8); reserve('yres', 8);
  if (icc) reserve('icc', icc.length);
  reserve('data', z.length);
  const buf = new ArrayBuffer(extra), v = new DataView(buf), u8 = new Uint8Array(buf);
  v.setUint16(0, 0x4949); v.setUint16(2, 42, true); v.setUint32(4, ifd, true); v.setUint16(ifd, tags.length, true);
  tags.forEach(([tag, type, count, val], i) => {
    const o = ifd + 2 + i * 12;
    v.setUint16(o, tag, true); v.setUint16(o + 2, type, true); v.setUint32(o + 4, count, true);
    const value = typeof val === 'string' ? place[val] : val;
    if (type === 3 && count === 1) v.setUint16(o + 8, value, true); else v.setUint32(o + 8, value, true);
  });
  if (spp > 1) for (let k = 0; k < spp; k++) v.setUint16(place.bps + k * 2, 8, true);
  v.setUint32(place.xres, dpi, true); v.setUint32(place.xres + 4, 1, true);
  v.setUint32(place.yres, dpi, true); v.setUint32(place.yres + 4, 1, true);
  if (icc) u8.set(icc, place.icc);
  u8.set(z, place.data);
  return new Blob([buf], { type: 'image/tiff' });
}

// ---------------------------------------------------------------------
// 인쇄 프로파일 (Windows 에 들어 있는 것 + data/assets/profiles 에 넣은 것)
// ---------------------------------------------------------------------
const profCache = new Map();
export async function listProfiles() {
  const out = [];
  try { for (const f of await server.list('colorprofiles')) if (/\.ic[cm]$/i.test(f.name)) out.push({ kind: 'colorprofiles', name: f.name, label: `${f.name} (Windows)` }); } catch { /* 서버 없음 */ }
  try { for (const f of await server.list('assets')) if (/^profiles\/.+\.ic[cm]$/i.test(f.name)) out.push({ kind: 'assets', name: f.name, label: f.name.slice(9) }); } catch { /* 없음 */ }
  return out;
}
export async function loadProfile(sel) {
  const key = sel.kind + '/' + sel.name;
  if (!profCache.has(key)) {
    const bytes = new Uint8Array(await (await server.get(sel.kind, sel.name)).arrayBuffer());
    profCache.set(key, { bytes, prof: parseICC(bytes.buffer) });
  }
  return profCache.get(key);
}
export async function addProfileFile() {
  const f = await pickFile('.icc,.icm');
  if (!f) return null;
  const p = parseICC(await f.arrayBuffer());
  if (p.space !== 'CMYK') { app.toast(`CMYK 프로파일이 아닙니다 (${p.space})`, 'warn'); return null; }
  await server.put('assets', 'profiles/' + f.name, f);
  app.toast(`인쇄 프로파일 추가: ${p.name}`);
  return { kind: 'assets', name: 'profiles/' + f.name };
}
const cubeCache = new Map();
async function cubeFor(sel, intent) {
  const key = sel.kind + '/' + sel.name + ':' + intent;
  if (!cubeCache.has(key)) { const { prof } = await loadProfile(sel); cubeCache.set(key, buildCube(makeTransform(prof, intent))); }
  return cubeCache.get(key);
}

/** 인쇄 색 미리보기: 원래 색 ↔ 인쇄했을 때 색, 인쇄로 못 내는 색 표시 */
export async function cmykProof() {
  const profs = await listProfiles();
  if (!profs.length) { app.toast('인쇄 프로파일이 없습니다. 파일 > 인쇄용 내보내기에서 프로파일을 추가하세요', 'warn'); return; }
  const last = app.settings.printProfile;
  const v = await form('인쇄 색 미리보기 (CMYK)', [
    { key: 'p', label: '인쇄 프로파일', type: 'select', options: profs.map((p, i) => [String(i), p.label]), value: String(Math.max(0, profs.findIndex(p => last && p.kind === last.kind && p.name === last.name))) },
    { key: 'intent', label: '변환 방식', type: 'segment', options: [['0', '지각적 (자연스럽게)'], ['1', '상대 색도 (정확하게)']], value: String(app.settings.printIntent ?? 0) },
  ], { okLabel: '보기' });
  if (!v) return;
  const sel = profs[+v.p];
  let cube;
  try { cube = await app.task('인쇄 색 계산', () => cubeFor(sel, +v.intent), { delay: 0 }); } catch (e) { app.toast(e.message, 'error'); return; }
  app.settings.printProfile = { kind: sel.kind, name: sel.name }; app.settings.printIntent = +v.intent; app.saveSettingsSoon();
  const doc = app.doc;
  const src = app.compositor.flatten(doc, { scale: Math.min(1, 1400 / Math.max(doc.width, doc.height)), paper: true, background: '#ffffff' });
  const proof = proofCanvas(src, cube, false), warn = proofCanvas(src, cube, true);
  const view = h('div.cmyk-proof');
  const show = (c) => { view.innerHTML = ''; c.style.maxWidth = '100%'; c.style.maxHeight = '64vh'; c.style.display = 'block'; view.append(c); };
  const seg = h('div.seg', null, ...[['원래 색 (화면)', src], ['인쇄했을 때', proof], ['인쇄로 못 내는 색', warn]].map(([t, c], i) => {
    const b = h(`button${i === 1 ? '.on' : ''}`, { type: 'button' }, t);
    b.onclick = () => { seg.querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b)); show(c); };
    return b;
  }));
  show(proof);
  openModal({ title: `인쇄 색 미리보기: ${sel.label}`, body: h('div.compare-wrap', null, seg, h('div.help', null, '"인쇄로 못 내는 색"은 회색 줄무늬로 표시됩니다. 화면 색과 차이가 큰 곳은 인쇄하면 탁해질 수 있습니다.'), view), wide: true, actions: [{ label: '닫기', primary: true }] });
}

// ---------------------------------------------------------------------
// 1비트 TIFF (인쇄소에 흔히 내는 형식, 압축 없음)
// ---------------------------------------------------------------------
export function tiff1bit(bits, w, hh, dpi) {
  const tags = [
    [256, 4, 1, w], [257, 4, 1, hh], [258, 3, 1, 1], [259, 3, 1, 1], [262, 3, 1, 0],   // 0 = 흰색이 0 → 비트 반전 필요
    [273, 4, 1, 0], [277, 3, 1, 1], [278, 4, 1, hh], [279, 4, 1, bits.length],
    [282, 5, 1, 0], [283, 5, 1, 0], [296, 3, 1, 2],
  ];
  const ifdSize = 2 + tags.length * 12 + 4;
  const resOff = 8 + ifdSize, dataOff = resOff + 16;
  const buf = new ArrayBuffer(dataOff + bits.length);
  const v = new DataView(buf);
  v.setUint16(0, 0x4949); v.setUint16(2, 42, true); v.setUint32(4, 8, true);
  v.setUint16(8, tags.length, true);
  tags.forEach(([tag, type, count, value], i) => {
    const p = 10 + i * 12;
    v.setUint16(p, tag, true); v.setUint16(p + 2, type, true); v.setUint32(p + 4, count, true);
    if (tag === 273) value = dataOff;
    if (tag === 282) value = resOff;
    if (tag === 283) value = resOff + 8;
    if (type === 3) v.setUint16(p + 8, value, true); else v.setUint32(p + 8, value, true);
  });
  v.setUint32(10 + tags.length * 12, 0, true);
  v.setUint32(resOff, dpi, true); v.setUint32(resOff + 4, 1, true);
  v.setUint32(resOff + 8, dpi, true); v.setUint32(resOff + 12, 1, true);
  // WhiteIsZero: 우리 비트는 1 = 흰색이므로 뒤집어 넣는다
  const out = new Uint8Array(buf, dataOff);
  for (let i = 0; i < bits.length; i++) out[i] = ~bits[i] & 0xff;
  return new Blob([buf], { type: 'image/tiff' });
}

// ---------------------------------------------------------------------
// 대화상자
// ---------------------------------------------------------------------
async function pageDocs(all) {
  const story = storyOf(app.doc?.file);
  if (!all || !story) return [{ doc: app.doc, name: app.doc.name }];
  if (app.doc.modified) { const { saveProject } = await import('../core/io.js'); await saveProject(); }
  const pages = await listPages(story);
  return pages.map(p => ({ load: async () => Doc.deserialize(await (await server.get('projects', p.name)).json()), name: p.name }));
}

async function save(name, blob) {
  if (!server.online) { downloadBlob(blob, name.split('/').pop()); return; }
  await server.put('exports', name, blob);
}

export async function printExportDialog() {
  const doc = app.doc;
  const story = storyOf(doc.file);
  let profs = await listProfiles();
  const last = app.settings.printProfile;
  const profIdx = Math.max(0, profs.findIndex(p => last && p.kind === last.kind && p.name === last.name));
  const v = await form('인쇄용 내보내기', [
    { key: 'fmt', label: '형식', type: 'segment', options: [['pdf', 'PDF 한 파일'], ['tif', 'TIFF (쪽마다)'], ['png', 'PNG (쪽마다)']], value: 'pdf' },
    { key: 'mode', label: '색', type: 'segment', options: [['mono', '흑백 1비트'], ['gray', '그레이'], ['color', 'RGB 컬러'], ['cmyk', 'CMYK (인쇄 색)']], value: 'mono' },
    { key: 'prof', label: 'CMYK 일 때 인쇄 프로파일 (인쇄소가 정해 주면 그것으로, 모르면 SWOP)', type: 'select', options: [...profs.map((p, i) => [String(i), p.label]), ['add', '+ 프로파일 파일 추가 (.icc/.icm)…']], value: String(profIdx) },
    { key: 'intent', label: 'CMYK 변환 방식', type: 'segment', options: [['0', '지각적 (자연스럽게)'], ['1', '상대 색도 (정확하게)']], value: String(app.settings.printIntent ?? 0) },
    { key: 'area', label: '범위', type: 'segment', options: [['trim', '재단선 안'], ['bleed', '도련 포함'], ['all', '캔버스 전체']], value: doc.print ? 'bleed' : 'all' },
    { key: 'dither', label: '흑백일 때 회색을 점(디더링)으로 — 끄면 기준값으로 딱 나눔 (톤을 붙인 원고는 끄기)', type: 'checkbox', value: false },
    { key: 'threshold', label: '흑백 기준 밝기 (0~255)', type: 'number', value: 128, min: 1, max: 254 },
    { key: 'all', label: story ? `작품의 모든 페이지 (${story})` : '여러 페이지 작품이 아니라 지금 페이지만 나갑니다', type: 'checkbox', value: !!story },
    { key: 'info', type: 'info', label: `해상도 ${doc.dpi || 350}dpi 기준으로 실제 크기가 정해집니다 (원고 설정에서 변경). 밑그림·러프 레이어와 메모는 빠집니다.` },
  ], { okLabel: '내보내기' });
  if (!v) return;
  const o = { threshold: Math.max(1, Math.min(254, v.threshold || 128)), dither: v.dither };
  let prof = null;
  if (v.mode === 'cmyk') {
    let sel = v.prof === 'add' ? await addProfileFile() : profs[+v.prof];
    if (!sel) { app.toast('CMYK 로 내보내려면 인쇄 프로파일이 필요합니다', 'warn'); return; }
    try {
      o.cube = await app.task('인쇄 색 계산', () => cubeFor(sel, +v.intent), { delay: 0 });
      prof = await loadProfile(sel);
    } catch (e) { app.toast(e.message, 'error'); return; }
    app.settings.printProfile = { kind: sel.kind, name: sel.name }; app.settings.printIntent = +v.intent; app.saveSettingsSoon();
  }
  const list = await pageDocs(v.all);
  const base = `${safeName(story || doc.name)}_인쇄_${fileStamp()}`;
  await app.task(`인쇄용 ${list.length}쪽`, async () => {
    const pdfPages = [];
    for (const [i, it] of list.entries()) {
      const d = it.doc || await it.load();
      const dpi = d.dpi || 350;
      const c = pageCanvas(d, v.area);
      const num = String(i + 1).padStart(3, '0');
      if (v.fmt === 'pdf') pdfPages.push(await pdfPage(c, v.mode, dpi, o));
      else if (v.fmt === 'tif') {
        if (v.mode === 'cmyk') await save(`${base}/${num}.tif`, await tiff8(await canvasToCmyk(c, o.cube), c.width, c.height, 4, dpi, prof.bytes));
        else if (v.mode === 'gray') await save(`${base}/${num}.tif`, await tiff8(grayOf(c), c.width, c.height, 1, dpi));
        else if (v.mode === 'color') {
          const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data, rgb = new Uint8Array(c.width * c.height * 3);
          for (let i = 0, j = 0; i < d.length; i += 4, j += 3) { const a = d[i + 3] / 255; rgb[j] = d[i] * a + 255 * (1 - a); rgb[j + 1] = d[i + 1] * a + 255 * (1 - a); rgb[j + 2] = d[i + 2] * a + 255 * (1 - a); }
          await save(`${base}/${num}.tif`, await tiff8(rgb, c.width, c.height, 3, dpi));
        } else await save(`${base}/${num}.tif`, tiff1bit(toBits(grayOf(c), c.width, c.height, o.threshold, o.dither), c.width, c.height, dpi));
      } else {
        if (v.mode !== 'color') {
          const g = grayOf(c), x = c.getContext('2d'), im = x.getImageData(0, 0, c.width, c.height);
          const bits = v.mode === 'mono' ? toBits(g, c.width, c.height, o.threshold, o.dither) : null, rb = Math.ceil(c.width / 8);
          for (let j = 0; j < g.length; j++) {
            const val = bits ? ((bits[Math.floor(j / c.width) * rb + ((j % c.width) >> 3)] >> (7 - (j % c.width & 7))) & 1) * 255 : g[j];
            im.data[j * 4] = im.data[j * 4 + 1] = im.data[j * 4 + 2] = val; im.data[j * 4 + 3] = 255;
          }
          x.putImageData(im, 0, 0);
        }
        await save(`${base}/${num}.png`, await canvasToBlob(c));
      }
    }
    if (v.fmt === 'pdf') await save(`${base}.pdf`, buildPDF(pdfPages, prof ? { intent: { icc: prof.bytes, name: prof.prof.name } } : {}));
  });
  app.toast(server.online ? `내보냄: ${server.where}/exports/${base}${v.fmt === 'pdf' ? '.pdf' : ''}` : '다운로드했습니다');
}

// ---------------------------------------------------------------------
// 책 펼침면 미리보기
// ---------------------------------------------------------------------
export async function spreadPreview() {
  const story = storyOf(app.doc?.file);
  const list = await pageDocs(!!story);
  let rtl = !!app.settings.bookRtl, at = 0;
  const cache = new Map();
  const H = Math.min(innerHeight * 0.7, 820);
  const render = async (i) => {
    if (i < 0 || i >= list.length) return null;
    if (!cache.has(i)) {
      const d = list[i].doc || await list[i].load();
      const q = areaOf(d, 'trim');
      cache.set(i, pageCanvas(d, 'trim', Math.min(1, H / q.h)));
    }
    return cache.get(i);
  };
  // 펼침: 1쪽은 혼자 (책의 첫 장), 이후 2·3, 4·5 …
  const spreads = [[0]];
  for (let i = 1; i < list.length; i += 2) spreads.push(list[i + 1] != null ? [i, i + 1] : [i]);
  const view = h('div.spread-view'), info = h('div.help');
  const draw = async () => {
    const s = spreads[at];
    const cs = await Promise.all(s.map(render));
    view.innerHTML = '';
    // 왼쪽 넘김(한국식): 짝수쪽이 왼쪽 / 오른쪽 넘김(일본식): 순서 반대
    let order = s.length === 2 ? (rtl ? [cs[1], cs[0]] : cs) : cs;
    // 첫 쪽: 한국식 책은 오른쪽, 일본식 책은 왼쪽에 혼자
    if (s.length === 1 && s[0] === 0) order = rtl ? [cs[0], null] : [null, cs[0]];
    for (const c of order) {
      const page = h('div.spread-page');
      if (c) { c.style.height = H + 'px'; c.style.width = 'auto'; page.append(c); } else page.classList.add('blank');
      view.append(page);
    }
    info.textContent = `${s.map(i => i + 1).join(' · ')}쪽 / 전체 ${list.length}쪽 · ${rtl ? '오른쪽으로 넘김 (일본식)' : '왼쪽으로 넘김 (한국식)'} · 방향키로 넘기기`;
  };
  const go = (d) => { const n = at + (rtl ? -d : d); if (n >= 0 && n < spreads.length) { at = n; draw(); } };
  const onKey = (e) => { if (e.key === 'ArrowRight') { go(1); e.preventDefault(); } if (e.key === 'ArrowLeft') { go(-1); e.preventDefault(); } };
  addEventListener('keydown', onKey, true);
  const bar = h('div.row', null,
    h('button.btn', { type: 'button', onclick: () => go(-1) }, '◀'),
    h('button.btn', { type: 'button', onclick: () => go(1) }, '▶'),
    h('button.btn', { type: 'button', onclick: () => { rtl = !rtl; app.settings.bookRtl = rtl; app.saveSettingsSoon(); draw(); } }, '넘김 방향 바꾸기'));
  openModal({ title: story ? `펼침면 미리보기: ${story}` : '펼침면 미리보기 (한 페이지)', body: h('div.spread-wrap', null, bar, info, view), wide: true, onClose: () => removeEventListener('keydown', onKey, true) });
  await draw();
}

export function initPrintout() {
  app.registerCommand('view.cmykProof', { label: '인쇄 색 미리보기 (CMYK)…', run: cmykProof });
  app.addMenuItem('view', { command: 'view.cmykProof' });
  app.registerCommand('file.printExport', { label: '인쇄용 내보내기 (PDF / 1비트 TIFF)…', run: printExportDialog });
  app.registerCommand('view.spread', { label: '펼침면 미리보기 (책처럼 보기)', run: spreadPreview });
  app.addMenuItem('file', { command: 'file.printExport' });
  app.addMenuItem('view', { command: 'view.spread' });
}
