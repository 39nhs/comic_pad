// 인쇄·투고 전 점검: 해상도, 도련, 안전선 밖 글자, 흑백 원고의 회색·컬러, 보이는 러프, 작은 글자, 없는 글꼴, 비상업 AI, 빈 페이지
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal } from './dialogs.js';
import { setLayerProps, addLayer } from '../core/ops.js';
import { printRects } from './manuscript.js';
import { SYSTEM_FONTS } from '../tools/comic.js';

const SEV = { error: ['❌', '고쳐야 함'], warn: ['⚠', '확인'], info: ['ℹ', '참고'] };
const inside = (b, r) => b.x >= r.x - 0.5 && b.y >= r.y - 0.5 && b.x + b.w <= r.x + r.w + 0.5 && b.y + b.h <= r.y + r.h + 0.5;

/** 합친 그림을 작게 줄여서 픽셀 통계 (흰색·검정·회색·컬러, 회색/컬러가 있는 영역) */
function pixelStats(doc, region = null) {
  const s = Math.min(1, 1600 / Math.max(doc.width, doc.height));
  const c = app.compositor.flatten(doc, { scale: s, background: '#ffffff' });
  const w = c.width, hh = c.height, d = c.getContext('2d').getImageData(0, 0, w, hh).data;
  let grey = 0, color = 0, ink = 0, n = 0;
  const gb = { x0: w, y0: hh, x1: -1, y1: -1 }, cb = { x0: w, y0: hh, x1: -1, y1: -1 };
  const grow = (b, x, y) => { if (x < b.x0) b.x0 = x; if (y < b.y0) b.y0 = y; if (x > b.x1) b.x1 = x; if (y > b.y1) b.y1 = y; };
  for (let y = 0; y < hh; y++) for (let x = 0; x < w; x++) {
    const p = (y * w + x) * 4, r = d[p], g = d[p + 1], b = d[p + 2];
    n++;
    const mx = Math.max(r, g, b), mn = Math.min(r, g, b);
    if (mx < 250) ink++;
    if (mx - mn > 28) { color++; grow(cb, x, y); }
    else if (mx > 40 && mx < 215) { grey++; grow(gb, x, y); }
  }
  const toDoc = (b) => (b.x1 < 0 ? null : { x: b.x0 / s, y: b.y0 / s, w: (b.x1 - b.x0 + 1) / s, h: (b.y1 - b.y0 + 1) / s });
  return { grey: grey / n, color: color / n, ink: ink / n, greyBox: toDoc(gb), colorBox: toDoc(cb), canvas: c, s };
}

/** 재단선에 닿은 그림이 도련까지 이어지는지 (네 변을 훑어봄) */
function bleedGaps(st, r) {
  const c = st.canvas, s = st.s, x = c.getContext('2d');
  const T = { x: r.trim.x * s, y: r.trim.y * s, w: r.trim.w * s, h: r.trim.h * s }, B = { x: r.bleed.x * s, y: r.bleed.y * s, w: r.bleed.w * s, h: r.bleed.h * s };
  const ink = (px, py) => { if (px < 0 || py < 0 || px >= c.width || py >= c.height) return false; const d = x.getImageData(px | 0, py | 0, 1, 1).data; return Math.max(d[0], d[1], d[2]) < 240; };
  let gaps = 0, total = 0;
  for (let k = 0; k <= 40; k++) {
    const u = k / 40;
    const pairs = [
      [[T.x + 1, T.y + u * T.h], [B.x + 1, T.y + u * T.h]], [[T.x + T.w - 2, T.y + u * T.h], [B.x + B.w - 2, T.y + u * T.h]],
      [[T.x + u * T.w, T.y + 1], [T.x + u * T.w, B.y + 1]], [[T.x + u * T.w, T.y + T.h - 2], [T.x + u * T.w, B.y + B.h - 2]],
    ];
    for (const [a, b] of pairs) if (ink(...a)) { total++; if (!ink(...b)) gaps++; }
  }
  return total >= 4 && gaps / total > 0.3 ? gaps : 0;
}

/** 작품 하나 점검 → [{ sev, msg, layerId?, rect?, fix?: { label, run } }] */
export function checkDoc(doc) {
  const out = [];
  const add = (sev, msg, o = {}) => out.push({ sev, msg, ...o });
  const r = printRects(doc);
  const dpi = doc.dpi || 0;
  const st = pixelStats(doc);
  const mono = st.color < 0.002;                      // 흑백 원고로 보임
  // 1) 해상도
  if (r) {
    if (!dpi) add('warn', '해상도(dpi)가 정해져 있지 않습니다. 원고 설정에서 정하세요.');
    else if (mono && dpi < 600) add('warn', `흑백 원고인데 해상도가 ${dpi}dpi 입니다. 흑백(1비트) 인쇄는 600dpi 이상을 권합니다.`);
    else if (!mono && dpi < 300) add('error', `컬러 원고 해상도가 ${dpi}dpi 입니다. 인쇄는 300dpi 이상이 필요합니다.`);
    if (doc.width < r.bleed.w - 1 || doc.height < r.bleed.h - 1) add('error', '캔버스가 도련(재단선 바깥 여유)보다 작습니다. 원고 설정에서 "캔버스 크기를 도련에 맞추기"를 하세요.');
    const gaps = bleedGaps(st, r);
    if (gaps) add('warn', '재단선에 닿은 그림이 도련(빨간 점선)까지 이어지지 않은 곳이 있습니다. 재단 때 흰 틈이 생길 수 있습니다.', { rect: r.bleed });
  } else add('info', '원고 설정(재단선·도련·안전선)이 없습니다. 인쇄할 원고면 만화 > 원고 설정을 하세요.');
  // 2) 글자·말풍선이 안전선 밖
  const safe = r?.safe;
  for (const l of doc.layers) {
    if (!l.visible) continue;
    if (l.kind === 'text' && l.meta?.box) {
      if (safe && !inside(l.meta.box, safe)) add('warn', `글자 "${(l.meta.text || '').slice(0, 14)}" 가 안전선(파란 점선) 밖에 있어 잘릴 수 있습니다.`, { layerId: l.id, rect: l.meta.box });
      // 작은 글자 (pt = px / dpi × 72)
      if (dpi && !l.meta.sfx) {
        const pt = l.meta.size / dpi * 72 * (l.meta.mat ? Math.hypot(l.meta.mat[0], l.meta.mat[1]) : 1);
        if (pt < 7) add('warn', `글자 "${(l.meta.text || '').slice(0, 14)}" 가 ${pt.toFixed(1)}pt 로 작습니다 (인쇄에서는 7pt 이상 권장).`, { layerId: l.id, rect: l.meta.box });
      }
      // 없는 글꼴
      const f = l.meta.font;
      if (f && !SYSTEM_FONTS.some(([k]) => k === f) && !(app.fonts || []).includes(f) && !document.fonts.check(`12px "${f}"`)) add('warn', `글꼴 "${f}" 이(가) 이 PC 에 없습니다 (다른 글꼴로 보임).`, { layerId: l.id, rect: l.meta.box });
    }
    if (l.kind === 'bubble' && safe) {
      for (const b of l.meta?.bubbles || []) if (!inside({ x: b.x, y: b.y, w: b.w, h: b.h }, safe)) add('warn', '말풍선이 안전선 밖으로 나가 있습니다.', { layerId: l.id, rect: { x: b.x, y: b.y, w: b.w, h: b.h } });
    }
    // 3) 보이는 러프·밑그림 이름의 레이어가 내보내기에 들어감
    if (!l.draft && !l.isFolder && (l.rough || /러프|rough|밑그림|스케치|sketch|콘티/i.test(l.name))) {
      add('warn', `"${l.name}" 레이어가 보이는 상태라 인쇄·내보내기에 들어갑니다.`, { layerId: l.id, fix: { label: '밑그림으로 (내보내기 제외)', run: () => setLayerProps(l, { draft: true }, '밑그림으로') } });
    }
  }
  // 4) 흑백 원고의 회색·컬러
  if (r && mono && st.grey > 0.003) {
    add('warn', `흑백 원고에 망점이 아닌 회색이 ${(st.grey * 100).toFixed(1)}% 있습니다. 흑백(1비트) 인쇄에서 뭉개지거나 사라질 수 있습니다 (톤·망점으로 바꾸거나 2값화).`, {
      rect: st.greyBox, fix: { label: '맨 위에 2값화 조정 레이어', run: () => { const l = addLayer({ name: '2값화 (인쇄 점검)', kind: 'adjust', meta: { type: 'threshold', params: { t: 128 } } }, doc.layers.length); app.doc.setActive(l); } },
    });
  }
  if (r && !mono && st.color < 0.02 && st.grey + st.color > 0) add('info', `컬러가 조금(${(st.color * 100).toFixed(2)}%)만 있습니다. 흑백 인쇄라면 섞인 색이 회색으로 바뀝니다.`, { rect: st.colorBox });
  // 5) 비상업 AI
  const nc = (doc.aiLog || []).filter(x => x.nc);
  if (nc.length) add('error', `비상업용 AI 로 만든 부분이 있습니다 (${[...new Set(nc.map(x => x.name))].join(', ')}). 판매·연재·투고에는 쓸 수 없습니다.`);
  // 6) 빈 페이지
  if (st.ink < 0.001) add('warn', '빈 페이지로 보입니다 (그린 것이 거의 없음).');
  return out;
}

/** 화면을 그 영역으로 */
function goTo(doc, item) {
  if (item.layerId && doc.byId(item.layerId)) doc.setActive(doc.byId(item.layerId));
  const r = item.rect;
  if (r && app.view.w && app.view.h) {
    const v = app.view, z = Math.min(v.w / (r.w * 1.4 + 40), v.h / (r.h * 1.4 + 40), 4);
    v.setZoom(Math.max(0.02, z));
    const c = v.toScreen(r.x + r.w / 2, r.y + r.h / 2);
    v.panBy(v.w / 2 - c.x, v.h / 2 - c.y);
    app.bus.emit('view:changed');
    app.requestRender();
  }
}

export async function preflightDialog() {
  const doc = app.doc;
  const pages = [];
  const { storyOf, listPages } = await import('./story.js');
  const story = storyOf(doc.file);
  let list = [];
  await app.task('인쇄 전 점검', async () => {
    list = checkDoc(doc).map(x => ({ ...x, page: null }));
    if (story) {
      const { server } = await import('../core/io.js');
      const { Doc } = await import('../core/doc.js');
      for (const p of await listPages(story)) {
        if (p.name === doc.file) continue;
        try {
          const d = await Doc.deserialize(await (await server.get('projects', p.name)).json());
          for (const x of checkDoc(d)) list.push({ ...x, page: p.name, fix: null });
          pages.push(p.name);
        } catch (e) { list.push({ sev: 'warn', msg: `열지 못함: ${e.message}`, page: p.name }); }
        await new Promise(r => setTimeout(r, 0));
      }
    }
  }, { delay: 0 });
  const order = { error: 0, warn: 1, info: 2 };
  list.sort((a, b) => order[a.sev] - order[b.sev]);
  const body = h('div.preflight');
  const counts = Object.fromEntries(Object.keys(SEV).map(k => [k, list.filter(x => x.sev === k).length]));
  body.append(h('div.help', null, list.length ? `고쳐야 함 ${counts.error} · 확인 ${counts.warn} · 참고 ${counts.info}${story ? ` (작품 ${pages.length + 1}쪽 전체)` : ''}` : '문제를 찾지 못했습니다. 인쇄·투고해도 좋습니다.'));
  for (const it of list) {
    const acts = h('div.pf-acts');
    if (it.page) acts.append(h('button.btn.sm', { type: 'button', onclick: async () => { m.close?.(); const { gotoPage } = await import('./story.js'); await gotoPage(it.page); } }, '그 페이지 열기'));
    else if (it.layerId || it.rect) acts.append(h('button.btn.sm', { type: 'button', onclick: () => goTo(doc, it) }, '가서 보기'));
    if (it.fix) acts.append(h('button.btn.sm.primary', { type: 'button', onclick: (e) => { it.fix.run(); e.target.disabled = true; e.target.textContent = '고쳤음'; app.requestRender(); } }, it.fix.label));
    body.append(h(`div.pf-row.${it.sev}`, null, h('span.pf-ic', null, SEV[it.sev][0]), h('span.pf-msg', null, (it.page ? `[${it.page.split('/').pop()}] ` : '') + it.msg), acts));
  }
  const m = openModal({ title: '인쇄·투고 전 점검', body, wide: true, actions: [{ label: '다시 점검', run: () => { setTimeout(preflightDialog, 0); } }, { label: '닫기', primary: true }] });
  return list;
}

export function initPreflight() {
  app.registerCommand('print.preflight', { label: '인쇄·투고 전 점검…', run: preflightDialog });
  app.addMenuItem('comic', { command: 'print.preflight' });
  app.addMenuItem('file', { command: 'print.preflight' });
}
