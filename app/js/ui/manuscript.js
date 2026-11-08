// 원고 설정(재단선·도련·안전선 가이드, 재단 영역 내보내기), 웹툰 미리보기, 메모 핀
import { app } from '../app.js';
import { h, createCanvas, clamp } from '../core/util.js';
import { form, openModal } from './dialogs.js';
import { addIcon } from './icons.js';
import { section, toggle, button, row } from './controls.js';
import { resizeCanvas } from '../core/ops.js';

// =====================================================================
// 원고 설정
// =====================================================================
export const PRINT_SIZES = [
  ['b5', 'B5 단행본·동인지 (182×257mm)', 182, 257],
  ['a5', 'A5 (148×210mm)', 148, 210],
  ['b6', 'B6 소형 단행본 (128×182mm)', 128, 182],
  ['a4', 'A4 (210×297mm)', 210, 297],
  ['b4', '투고 원고 B4 (257×364mm)', 257, 364],
  ['custom', '직접 입력', 0, 0],
];

const mm2px = (mm, dpi) => Math.round(mm / 25.4 * dpi);

/** 재단/도련/안전 영역 (문서 px). 캔버스 가운데 기준 */
export function printRects(doc = app.doc) {
  const p = doc?.print;
  if (!p) return null;
  const tw = Math.min(p.trimW, doc.width), th = Math.min(p.trimH, doc.height);
  const trim = { x: Math.round((doc.width - tw) / 2), y: Math.round((doc.height - th) / 2), w: tw, h: th };
  const bleed = { x: trim.x - p.bleed, y: trim.y - p.bleed, w: tw + p.bleed * 2, h: th + p.bleed * 2 };
  const safe = { x: trim.x + p.safe, y: trim.y + p.safe, w: Math.max(0, tw - p.safe * 2), h: Math.max(0, th - p.safe * 2) };
  return { trim, bleed, safe };
}

export async function manuscriptDialog() {
  const doc = app.doc;
  const p = doc.print || {};
  const dpi = doc.dpi || 350;
  const v = await form('원고 설정 (인쇄용 가이드)', [
    { key: 'size', label: '완성(재단) 크기', type: 'select', options: PRINT_SIZES.map(s => [s[0], s[1]]), value: p.size || 'b5' },
    { key: 'w', label: '직접 입력: 가로 (mm)', type: 'number', value: p.mmW || 182 },
    { key: 'h', label: '직접 입력: 세로 (mm)', type: 'number', value: p.mmH || 257 },
    { key: 'dpi', label: '해상도 (dpi)', type: 'number', value: dpi },
    { key: 'bleed', label: '도련 (재단선 바깥, mm)', type: 'number', value: p.bleedMm ?? 3 },
    { key: 'safe', label: '안전선 (재단선 안쪽, mm)', type: 'number', value: p.safeMm ?? 5 },
    { key: 'fit', label: '캔버스 크기를 도련 크기에 맞추기', type: 'checkbox', value: !doc.print },
    { key: 'info', type: 'info', label: '빨간 점선(도련)까지 그림을 채우고, 대사·중요한 그림은 파란 점선(안전선) 안에 두세요. 재단 후에는 검은 선(재단선) 안쪽만 남습니다.' },
  ], { okLabel: '적용', extra: doc.print ? [{ label: '가이드 없애기', value: 'remove' }] : undefined });
  if (!v) return;
  if (v === 'remove') { setPrint(null); return; }
  const preset = PRINT_SIZES.find(s => s[0] === v.size);
  const mmW = preset && preset[2] ? preset[2] : +v.w, mmH = preset && preset[3] ? preset[3] : +v.h;
  const d = clamp(+v.dpi || 350, 72, 1200);
  const next = {
    size: v.size, mmW, mmH, bleedMm: +v.bleed, safeMm: +v.safe,
    trimW: mm2px(mmW, d), trimH: mm2px(mmH, d), bleed: mm2px(+v.bleed, d), safe: mm2px(+v.safe, d),
  };
  app.history.batch('원고 설정', () => {
    if (v.fit) {
      const W = next.trimW + next.bleed * 2, H = next.trimH + next.bleed * 2;
      if (W * H > 12000 * 12000) { app.toast('너무 큽니다. 해상도를 낮춰 주세요', 'warn'); return; }
      if (W !== doc.width || H !== doc.height) resizeCanvas(W, H, 0.5, 0.5);
    }
    doc.dpi = d;
    setPrint(next);
  });
}

function setPrint(next) {
  const doc = app.doc;
  const prev = doc.print || null;
  const apply = (x) => { doc.print = x ? { ...x } : null; app.markModified(); app.requestRender(); };
  apply(next);
  app.history.push({ label: next ? '원고 가이드' : '원고 가이드 제거', undo: () => apply(prev), redo: () => apply(next) });
}

function drawPrintGuides(ctx, view) {
  const doc = app.doc;
  const r = printRects(doc);
  if (!r || app.settings.printGuides === false) return;
  const rect = (q) => { const a = view.toScreen(q.x, q.y), b = view.toScreen(q.x + q.w, q.y + q.h); return [Math.min(a.x, b.x), Math.min(a.y, b.y), Math.abs(b.x - a.x), Math.abs(b.y - a.y)]; };
  const [bx, by, bw, bh] = rect(r.bleed);
  // 도련 바깥은 살짝 어둡게
  const [cx, cy, cw, ch] = rect({ x: 0, y: 0, w: doc.width, h: doc.height });
  ctx.fillStyle = 'rgba(0,0,0,0.18)';
  ctx.beginPath();
  ctx.rect(cx, cy, cw, ch);
  ctx.rect(bx + bw, by, -bw, bh);
  ctx.fill('evenodd');
  ctx.lineWidth = 1;
  ctx.setLineDash([6, 4]);
  ctx.strokeStyle = '#e5484d';
  ctx.strokeRect(bx + 0.5, by + 0.5, bw, bh);
  ctx.strokeStyle = '#2f7cf6';
  ctx.strokeRect(...rect(r.safe));
  ctx.setLineDash([]);
  ctx.strokeStyle = '#111';
  ctx.lineWidth = 1.2;
  const [tx, ty, tw, th] = rect(r.trim);
  ctx.strokeRect(tx, ty, tw, th);
  // 모서리 재단 표시(돔보)
  const L = 18;
  ctx.beginPath();
  for (const [x, y, sx, sy] of [[tx, ty, -1, -1], [tx + tw, ty, 1, -1], [tx, ty + th, -1, 1], [tx + tw, ty + th, 1, 1]]) {
    ctx.moveTo(x, y + sy * 4); ctx.lineTo(x, y + sy * L);
    ctx.moveTo(x + sx * 4, y); ctx.lineTo(x + sx * L, y);
  }
  ctx.stroke();
}

/** 재단(또는 도련) 영역만 잘라 내보내기용 캔버스 */
export function cropToPrint(canvas, area, scale = 1) {
  const r = printRects();
  if (!r || area === 'all') return canvas;
  const q = area === 'bleed' ? r.bleed : r.trim;
  const c = createCanvas(Math.round(q.w * scale), Math.round(q.h * scale));
  c.getContext('2d').drawImage(canvas, -q.x * scale, -q.y * scale);
  return c;
}

// =====================================================================
// 웹툰 미리보기 (휴대폰 화면 폭으로 스크롤해 보기)
// =====================================================================
export function webtoonPreview() {
  const doc = app.doc;
  let width = app.settings.webtoonPreviewW || 390;
  const frame = h('div.wt-frame');
  const body = h('div.wt-scroll');
  frame.append(body);
  const info = h('div.help');
  const draw = () => {
    const dpr = window.devicePixelRatio || 1;
    const scale = Math.min(1, (width * dpr) / doc.width);
    const img = app.compositor.flatten(doc, { scale, paper: true, background: doc.paper.visible ? doc.paper.color : '#ffffff' });
    img.style.width = '100%';
    img.style.display = 'block';
    body.innerHTML = '';
    body.append(img);
    frame.style.width = width + 'px';
    const screens = (doc.height * (width / doc.width)) / (width * 2.1);
    info.textContent = `${width}px 폭 기준, 화면 약 ${screens.toFixed(1)}장 분량 (위아래로 스크롤해 보세요)`;
  };
  const sizes = h('div.row', null, ...[[360, '작은 폰 360'], [390, '보통 폰 390'], [430, '큰 폰 430'], [600, '태블릿 600']].map(([w, label]) =>
    h('button.btn', { type: 'button', onclick: () => { width = w; app.settings.webtoonPreviewW = w; app.saveSettingsSoon?.(); draw(); } }, label)));
  openModal({ title: '웹툰 미리보기', body: h('div.wt-wrap', null, sizes, info, frame), wide: true });
  draw();
}

// =====================================================================
// 메모 핀 (작업 메모·피드백을 그림 위에 꽂아 두기. 내보내기에는 안 나옴)
// =====================================================================
addIcon('memo', '<path d="M5 4h14v12l-4 4H5z"/><path d="M15 20v-4h4"/><path d="M8 9h8M8 12h5"/>');

const PIN = 13; // 화면 px

function noteAt(sx, sy) {
  const notes = app.doc?.notes || [];
  for (let i = notes.length - 1; i >= 0; i--) {
    const p = app.view.toScreen(notes[i].x, notes[i].y);
    if (Math.hypot(p.x - sx, p.y - sy) <= PIN + 4) return notes[i];
  }
  return null;
}

function setNotes(next, label) {
  const doc = app.doc;
  const prev = structuredClone(doc.notes || []);
  const apply = (x) => { doc.notes = structuredClone(x); app.markModified(); app.requestRender(); if (app.toolId === 'memo') app.bus.emit('tool:options'); };
  apply(next);
  app.history.push({ label, undo: () => apply(prev), redo: () => apply(next) });
}

async function editNote(n, isNew) {
  const v = await form(isNew ? '새 메모' : '메모', [
    { key: 'text', label: '내용', type: 'textarea', value: n.text || '' },
    { key: 'color', label: '색', type: 'select', options: [['#f5c518', '노랑'], ['#e5484d', '빨강 (수정 요청)'], ['#2fa35b', '초록 (확인)'], ['#2f7cf6', '파랑 (참고)']], value: n.color || '#f5c518' },
    { key: 'done', label: '해결됨', type: 'checkbox', value: !!n.done },
  ], { okLabel: '저장', extra: isNew ? undefined : [{ label: '삭제', value: 'delete' }] });
  const notes = structuredClone(app.doc.notes || []);
  if (v === 'delete') { setNotes(notes.filter(x => x.id !== n.id), '메모 삭제'); return; }
  if (!v) return;
  const item = { ...n, text: v.text, color: v.color, done: !!v.done };
  const i = notes.findIndex(x => x.id === n.id);
  if (i >= 0) notes[i] = item; else notes.push(item);
  setNotes(notes, isNew ? '메모 추가' : '메모 수정');
}

function drawNotes(ctx, view) {
  const notes = app.doc?.notes;
  if (!notes?.length || app.settings.showNotes === false) return;
  ctx.font = '12px "Malgun Gothic", sans-serif';
  notes.forEach((n, i) => {
    const p = view.toScreen(n.x, n.y);
    ctx.globalAlpha = n.done ? 0.45 : 1;
    ctx.fillStyle = n.color || '#f5c518';
    ctx.strokeStyle = 'rgba(0,0,0,0.6)';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(p.x, p.y);
    ctx.arc(p.x, p.y - PIN - 4, PIN, Math.PI * 0.62, Math.PI * 2.38);
    ctx.closePath();
    ctx.fill(); ctx.stroke();
    ctx.fillStyle = '#000';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(n.done ? '✓' : String(i + 1), p.x, p.y - PIN - 4);
    // 짧은 미리보기
    const t = (n.text || '').split('\n')[0].slice(0, 18);
    if (t && app.toolId === 'memo') {
      ctx.textAlign = 'left';
      const w = ctx.measureText(t).width + 10;
      ctx.fillStyle = 'rgba(255,255,240,0.95)';
      ctx.fillRect(p.x + PIN + 2, p.y - PIN - 14, w, 20);
      ctx.fillStyle = '#222';
      ctx.fillText(t, p.x + PIN + 7, p.y - PIN - 4);
    }
  });
  ctx.globalAlpha = 1;
}

export const memoTool = {
  id: 'memo', name: '메모', icon: 'memo', key: 'y', cursor: 'crosshair',
  drag: null,
  get busy() { return !!this.drag; },
  down(e) {
    const n = noteAt(e.sx, e.sy);
    this.drag = n ? { n, x0: e.x, y0: e.y, sx0: e.sx, sy0: e.sy, ox: n.x, oy: n.y, moved: false, before: structuredClone(app.doc.notes) } : { create: true, x: e.x, y: e.y };
  },
  move(e) {
    const d = this.drag;
    if (!d?.n) return;
    if (Math.hypot(e.sx - d.sx0, e.sy - d.sy0) > 5) d.moved = true;
    if (d.moved) { d.n.x = d.ox + e.x - d.x0; d.n.y = d.oy + e.y - d.y0; app.requestRender(); }
  },
  up() {
    const d = this.drag;
    this.drag = null;
    if (!d) return;
    if (d.create) { editNote({ id: 'n' + Date.now().toString(36), x: d.x, y: d.y, text: '', color: '#f5c518', author: app.settings.userName || '' }, true); return; }
    if (!d.moved) { editNote(d.n, false); return; }
    const after = structuredClone(app.doc.notes);
    app.doc.notes = d.before;
    setNotes(after, '메모 이동');
  },
  cancel() { if (this.drag?.n) { this.drag.n.x = this.drag.ox; this.drag.n.y = this.drag.oy; } this.drag = null; app.requestRender(); },
  options(el) {
    const notes = app.doc?.notes || [];
    el.append(section('메모',
      toggle('메모 핀 보이기', () => app.settings.showNotes !== false, v => { app.settings.showNotes = v; app.saveSettingsSoon?.(); app.requestRender(); }),
      h('div.help', null, '빈 곳 탭: 새 메모 · 핀 탭: 보기/수정/삭제 · 핀 드래그: 이동. 메모는 작품 파일에 저장되고 내보낸 그림에는 나오지 않습니다.'),
      ...notes.map((n, i) => h('div.memo-row', { onclick: () => { const p = app.view.toScreen(n.x, n.y); app.view.panBy(app.view.w / 2 - p.x, app.view.h / 2 - p.y); app.requestRender(); editNote(n, false); } },
        h('i', { style: { background: n.color } }, n.done ? '✓' : String(i + 1)), h('span', null, (n.text || '(빈 메모)').split('\n')[0]))),
      notes.length ? row(button('해결된 메모 지우기', () => setNotes(notes.filter(n => !n.done), '해결된 메모 정리'))) : null,
    ));
  },
};

export function initManuscript() {
  app.registerTool(memoTool);
  app.addOverlay(drawPrintGuides);
  app.addOverlay(drawNotes);
  const C = (id, label, run) => app.registerCommand(id, { label, run });
  C('print.setup', '원고 설정 (재단선·도련·안전선)…', () => manuscriptDialog());
  C('print.toggle', '원고 가이드 보이기/숨기기', () => { app.settings.printGuides = app.settings.printGuides === false; app.saveSettingsSoon?.(); app.requestRender(); });
  C('view.webtoon', '웹툰 미리보기 (휴대폰 폭)', () => webtoonPreview());
  C('memo.tool', '메모 핀 도구', () => app.setTool('memo'));
  app.bus.on('doc:changed', () => app.requestRender());
}
