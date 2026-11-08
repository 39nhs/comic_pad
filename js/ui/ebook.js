// 전자책 내보내기: EPUB 3 (고정 레이아웃, 한 쪽 = 그림 한 장, 만화는 오른쪽→왼쪽) / CBZ
import { app } from '../app.js';
import { canvasToBlob, createCanvas, downloadBlob, safeName } from '../core/util.js';
import { form } from './dialogs.js';
import { server, saveProject } from '../core/io.js';
import { Doc } from '../core/doc.js';
import { makeZip } from '../core/zip.js';
import { storyOf, listPages } from './story.js';
import { printRects } from './manuscript.js';

const esc = (s) => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

/** 쪽 그림 목록 → EPUB Blob. pages: [{ blob(JPEG/PNG), w, h, type }] */
export async function buildEPUB(pages, { title = '만화', author = '', rtl = true, lang = 'ko', id = null } = {}) {
  const uuid = id || `urn:uuid:${crypto.randomUUID?.() || Date.now().toString(16)}`;
  const W = pages[0]?.w || 1000, H = pages[0]?.h || 1414;
  const files = [{ name: 'mimetype', data: 'application/epub+zip', store: true }];
  files.push({ name: 'META-INF/container.xml', data: '<?xml version="1.0" encoding="UTF-8"?>\n<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>' });
  const manifest = [], spine = [], nav = [];
  pages.forEach((p, i) => {
    const n = String(i + 1).padStart(3, '0'), ext = p.type === 'image/png' ? 'png' : 'jpg';
    files.push({ name: `OEBPS/images/p${n}.${ext}`, data: p.blob, store: true });
    files.push({ name: `OEBPS/p${n}.xhtml`, data: `<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE html>\n<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="${lang}"><head><meta charset="UTF-8"/><title>${i + 1}</title><meta name="viewport" content="width=${p.w}, height=${p.h}"/><style>html,body{margin:0;padding:0}img{display:block;width:${p.w}px;height:${p.h}px}</style></head><body><img src="images/p${n}.${ext}" alt="${i + 1}"/></body></html>` });
    manifest.push(`<item id="i${n}" href="images/p${n}.${ext}" media-type="${p.type}"${i === 0 ? ' properties="cover-image"' : ''}/>`, `<item id="p${n}" href="p${n}.xhtml" media-type="application/xhtml+xml"/>`);
    // 펼침면: 첫 쪽은 한쪽, 그다음부터 좌우 번갈아
    const side = i === 0 ? 'page-spread-center' : ((i % 2 === 1) === rtl ? 'page-spread-right' : 'page-spread-left');
    spine.push(`<itemref idref="p${n}" properties="${side}"/>`);
    nav.push(`<li><a href="p${n}.xhtml">${i + 1}</a></li>`);
  });
  files.push({ name: 'OEBPS/nav.xhtml', data: `<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE html>\n<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="${lang}"><head><meta charset="UTF-8"/><title>${esc(title)}</title></head><body><nav epub:type="toc" id="toc"><ol>${nav.join('')}</ol></nav></body></html>` });
  const now = new Date().toISOString().replace(/\.\d+Z$/, 'Z');
  files.push({ name: 'OEBPS/content.opf', data: `<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="${lang}" prefix="rendition: http://www.idpf.org/vocab/rendition/#">
<metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
<dc:identifier id="bookid">${esc(uuid)}</dc:identifier><dc:title>${esc(title)}</dc:title><dc:language>${lang}</dc:language>${author ? `<dc:creator>${esc(author)}</dc:creator>` : ''}
<meta property="dcterms:modified">${now}</meta>
<meta property="rendition:layout">pre-paginated</meta><meta property="rendition:spread">landscape</meta><meta property="rendition:orientation">auto</meta>
<meta name="original-resolution" content="${W}x${H}"/><meta name="fixed-layout" content="true"/><meta name="book-type" content="comic"/>
</metadata>
<manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>${manifest.join('')}</manifest>
<spine page-progression-direction="${rtl ? 'rtl' : 'ltr'}">${spine.join('')}</spine>
</package>` });
  const z = await makeZip(files);
  return new Blob([z], { type: 'application/epub+zip' });
}

/** CBZ: 그림 + ComicInfo.xml */
export async function buildCBZ(pages, { title = '만화', author = '', rtl = true } = {}) {
  const files = pages.map((p, i) => ({ name: `${String(i + 1).padStart(3, '0')}.${p.type === 'image/png' ? 'png' : 'jpg'}`, data: p.blob, store: true }));
  files.push({ name: 'ComicInfo.xml', data: `<?xml version="1.0" encoding="utf-8"?>\n<ComicInfo><Title>${esc(title)}</Title>${author ? `<Writer>${esc(author)}</Writer>` : ''}<PageCount>${pages.length}</PageCount><Manga>${rtl ? 'YesAndRightToLeft' : 'No'}</Manga><LanguageISO>ko</LanguageISO></ComicInfo>` });
  return makeZip(files);
}

/** 문서 한 장 → 쪽 그림 (긴 쪽 maxSide 이하) */
export async function pageImage(doc, { maxSide = 2400, fmt = 'jpg', area = 'trim' } = {}) {
  let c = app.compositor.flatten(doc, { background: '#ffffff', paper: true });
  // 원고 설정(재단선)이 있으면 재단선 안쪽만
  const r = area === 'trim' ? printRects(doc) : null;
  if (r?.trim) { const t = r.trim, k = createCanvas(Math.round(t.w), Math.round(t.h)); k.getContext('2d').drawImage(c, -t.x, -t.y); c = k; }
  const s = Math.min(1, maxSide / Math.max(c.width, c.height));
  if (s < 1) { const k = createCanvas(Math.round(c.width * s), Math.round(c.height * s)), x = k.getContext('2d'); x.imageSmoothingQuality = 'high'; x.drawImage(c, 0, 0, k.width, k.height); c = k; }
  const type = fmt === 'png' ? 'image/png' : 'image/jpeg';
  return { blob: await canvasToBlob(c, type, 0.9), w: c.width, h: c.height, type };
}

export async function ebookDialog(kind = 'epub') {
  const doc = app.doc, story = storyOf(doc?.file);
  const v = await form(kind === 'cbz' ? 'CBZ (만화책 압축 파일) 내보내기' : '전자책 (EPUB) 내보내기', [
    { key: 'kind', label: '형식', type: 'segment', options: [['epub', 'EPUB 3 (전자책)'], ['cbz', 'CBZ (만화 뷰어)']], value: kind },
    { key: 'range', label: '범위', type: 'segment', options: story ? [['story', `작품 전체 (${story})`], ['doc', '지금 쪽만']] : [['doc', '지금 그림']], value: story ? 'story' : 'doc' },
    { key: 'title', label: '제목', type: 'text', value: story || doc.name || '만화' },
    { key: 'author', label: '지은이', type: 'text', value: app.settings.ebookAuthor || '' },
    { key: 'rtl', label: '오른쪽에서 왼쪽으로 넘기기 (일본식 만화)', type: 'checkbox', value: app.settings.ebookRtl ?? false },
    { key: 'max', label: '그림 긴 쪽 최대 (px)', type: 'number', value: 2400, min: 600, max: 8000 },
    { key: 'fmt', label: '그림 형식', type: 'segment', options: [['jpg', 'JPG (작음)'], ['png', 'PNG (선명)']], value: 'jpg' },
    { key: 'info', type: 'info', label: '고정 레이아웃 EPUB 3: 한 쪽 = 그림 한 장. 원고 설정(재단선)이 있으면 재단선 안쪽만 넣습니다. 결과는 data/exports 에 저장하고 내려받기도 합니다.' },
  ], { okLabel: '만들기' });
  if (!v) return;
  Object.assign(app.settings, { ebookAuthor: v.author, ebookRtl: v.rtl });
  app.saveSettingsSoon();
  const out = await app.task('전자책 만드는 중', async () => {
    const pages = [];
    if (v.range === 'story') {
      if (doc.modified) await saveProject();
      for (const p of await listPages(story)) {
        const d = await Doc.deserialize(await (await server.get('projects', p.name)).json());
        pages.push(await pageImage(d, { maxSide: +v.max, fmt: v.fmt }));
      }
    } else pages.push(await pageImage(doc, { maxSide: +v.max, fmt: v.fmt }));
    const opt = { title: v.title, author: v.author, rtl: v.rtl };
    return v.kind === 'cbz' ? buildCBZ(pages, opt) : buildEPUB(pages, opt);
  }, { delay: 0 });
  if (!out) return;
  const name = `${safeName(v.title || 'comic')}.${v.kind}`;
  try { await server.put('exports', name, out); } catch { /* 저장소가 없으면 내려받기만 */ }
  downloadBlob(out, name);
  app.toast(`${name} 을(를) 만들었습니다 (data/exports, ${(out.size / 1e6).toFixed(1)}MB)`);
}

export function initEbook() {
  app.registerCommand('file.ebook', { label: '전자책 내보내기 (EPUB / CBZ)…', run: () => ebookDialog('epub') });
  app.addMenuItem('file', { command: 'file.ebook' });
}
