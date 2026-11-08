// 타임라인 패널: 트랙 × 프레임 격자, 태그, 어니언 스킨, 라이트 테이블, 타임시트, 스프라이트 시트, 내보내기
import { app } from '../app.js';
import { h, pickFile, downloadBlob, safeName } from '../core/util.js';
import { icon } from './icons.js';
import { slider, toggle, row, button, section } from './controls.js';
import * as A from '../core/anim.js';
import * as X from '../core/animx.js';
import * as S from '../core/sprite.js';
import * as ops from '../core/ops.js';
import { setDoc } from '../core/ops.js';
import { form, openModal } from './dialogs.js';
import * as AX from '../core/animexport.js';
import * as LS from '../core/lipsync.js';
import { motionTool, motionSection } from '../tools/motiontool.js';

export function registerAnimCommands() {
  app.addOverlay(X.drawCameraOverlay);   // 원고 위에 카메라 영역
  const C = (id, label, run, key) => app.registerCommand(id, { label, run, key });
  C('anim.create', '애니메이션 만들기 (빈 프레임 3장)', () => A.createAnimation(3));
  C('anim.track', '트랙 추가 (레이어처럼 따로 움직이는 줄)', () => A.addTrack());
  C('anim.fromLayers', '최상위 레이어들을 프레임으로 바꾸기', () => A.layersToAnimation());
  C('anim.add', '빈 프레임 추가', () => A.addFrame('blank'));
  C('anim.dup', '현재 프레임 복제', () => A.addFrame('copy'));
  C('anim.link', '연결된 프레임 추가 (같은 그림 이어 쓰기)', () => A.addFrame('link'));
  C('anim.unlink', '칸 연결 끊기 (따로 고치기)', () => A.unlinkCell());
  C('anim.clearCell', '칸 비우기', () => A.clearCell());
  C('anim.delete', '프레임 삭제', () => A.deleteFrame());
  C('anim.prev', '이전 프레임', () => A.setFrame(app.doc.anim.frame - 1), ',');
  C('anim.next', '다음 프레임', () => A.setFrame(app.doc.anim.frame + 1), '.');
  C('anim.play', '재생 / 정지', () => A.play(), 'shift+enter');
  C('anim.onion', '어니언 스킨 켜기/끄기', () => { app.doc.anim.onion = !app.doc.anim.onion; app.bus.emit('anim:changed'); app.requestRender(); });
  C('anim.light', '라이트 테이블에 올리기/내리기 (지금 칸)', () => toggleLight());
  C('anim.tag', '태그(구간) 만들기…', () => tagDialog());
  C('anim.timesheet', '타임시트 (노출표) 보기·고치기…', () => timesheetDialog());
  C('anim.export', '애니메이션 내보내기 (GIF·WebP·APNG·MP4·WebM·PNG)…', () => exportDialog());
  C('anim.gifQuick', '움짤 만들기 (바로, GIF)', () => quickGif());
  C('anim.motion', '모션 경로 도구 (트윈)', () => app.setTool('motion'));
  C('anim.bake', '모션을 그림으로 굳히기', () => { const k = A.bakeMotion(); if (k) app.toast(`움직인 프레임 ${k}장을 그림으로 만들었습니다`); });
  app.registerTool(motionTool);
  C('anim.sheet', '스프라이트 시트 내보내기 (PNG + JSON)…', () => sheetDialog());
  C('anim.importSheet', '스프라이트 시트 가져오기…', () => importSheetDialog());
  C('anim.importAse', 'Aseprite 파일 열기 (.aseprite/.ase)…', () => importAseprite());
  C('anim.exportAse', 'Aseprite 파일로 저장 (.aseprite)', () => app.task('Aseprite 저장', () => S.exportAseprite(), { delay: 0 }));
  const M = (items) => { for (const it of items) app.addMenuItem('anim', it === '-' ? { sep: true } : { command: it }); };
  app.addMenu('anim', '애니');
  M(['anim.create', 'anim.track', 'anim.fromLayers', '-', 'anim.add', 'anim.dup', 'anim.link', 'anim.unlink', 'anim.clearCell', 'anim.delete', '-', 'anim.prev', 'anim.next', 'anim.play', 'anim.onion', 'anim.light', 'anim.tag', 'anim.timesheet', '-', 'anim.motion', 'anim.bake', '-', 'anim.gifQuick', 'anim.export', 'anim.sheet', 'anim.importSheet', 'anim.importAse', 'anim.exportAse']);
  app.registerQuickAction('animPrev', { icon: 'prev', label: '이전 프레임', run: () => app.run('anim.prev') });
  app.registerQuickAction('animNext', { icon: 'next', label: '다음 프레임', run: () => app.run('anim.next') });
  app.registerQuickAction('animPlay', { icon: 'play', label: '재생', run: () => app.run('anim.play'), active: () => !!app.doc?.anim.playing });

  // 레이어 패널에서 셀을 고르면 그 셀이 보이는 프레임으로
  app.bus.on('layers:active', (l) => {
    const doc = app.doc;
    if (!l || doc.anim.playing) return;
    const p = doc.parentOf(l), t = p && doc.byId(p);
    if (!A.isTrack(t)) return;
    const xs = A.xsOf(t, doc);
    if (xs[doc.anim.frame] === l.id) return;
    const f = xs.indexOf(l.id);
    if (f >= 0) { doc.anim.frame = f; app.bus.emit('anim:changed'); app.requestRender(); }
  });
  app.bus.on('doc:changed', (doc) => { A.migrate(doc); app.bus.emit('anim:changed'); });
}

const tagOptions = () => [['', '전체'], ...(app.doc.anim.tags || []).map((t, i) => [String(i), `${t.name} (${t.from + 1}~${t.to + 1})`])];

/** 내보내기 창: 형식·크기·반복·투명·색 수·디더링, 크기 미리 짐작 */
async function exportDialog(preset = {}) {
  const doc = app.doc;
  if (A.frameCount() < 1) { app.toast('애니메이션 프레임이 없습니다'); return; }
  const st = { fmt: 'gif', scale: doc.width <= 256 ? 4 : 0.5, tag: doc.anim.playTag ?? null, loop: 0, transparent: false, dither: true, colors: 256, quality: 0.9, ...(app.settings.animExport || {}), ...preset };
  const body = h('div.anim-export');
  const est = h('div.help');
  let timer = 0, gen = 0;
  const seg = (opts, key, conv = (x) => x) => h('div.seg', null, ...opts.map(([v, label]) => h(`button${String(st[key]) === String(v) ? '.on' : ''}`, { type: 'button', onclick: () => { st[key] = conv(v); render(); } }, label)));
  const lab = (t, el) => h('div.field', null, h('span', null, t), el);
  const check = (label, key) => { const c = h('input', { type: 'checkbox' }); c.checked = !!st[key]; c.onchange = () => { st[key] = c.checked; render(); }; return h('label.field.check', null, h('span', null, label), c); };
  const still = () => ['gif', 'webp', 'apng'].includes(st.fmt);
  const render = () => {
    body.innerHTML = '';
    const mp4 = !!AX.mp4Type();
    const tip = { gif: ' · 커뮤니티·메신저 어디나', webp: ' · GIF 보다 훨씬 작고 색이 정확', apng: ' · 색 손실 없음', mp4: ' · 인스타·카톡 등 WebM 을 못 올리는 곳용' }[st.fmt] || '';
    body.append(
      lab('형식', seg(Object.entries(AX.FORMATS).filter(([k]) => k !== 'mp4' || mp4).map(([k, f]) => [k, f.label.split(' (')[0]]), 'fmt')),
      h('div.help', null, AX.FORMATS[st.fmt].label + tip),
      lab('크기', seg([[0.25, '25%'], [0.5, '50%'], [1, '100%'], [2, '200%'], [4, '400%']], 'scale', Number)),
      h('div.help', null, `${Math.round(doc.width * st.scale)}×${Math.round(doc.height * st.scale)} · ${A.exportOrder(st.tag).order.length}장`),
      (doc.anim.tags || []).length ? lab('구간', seg(tagOptions().map(([v, l]) => [v === '' ? 'null' : v, l]), 'tag', (v) => (v === 'null' ? null : +v))) : null,
      still() ? lab('반복', seg([[0, '무한'], [1, '한 번'], [2, '2번'], [3, '3번']], 'loop', Number)) : null,
      still() ? check('배경 투명 (종이·흰 바탕 없이)', 'transparent') : null,
      st.fmt === 'gif' ? lab('색 수 (적을수록 작음)', seg([[256, '256'], [128, '128'], [64, '64'], [32, '32'], [16, '16']], 'colors', Number)) : null,
      st.fmt === 'gif' ? check('디더링 (부드러운 색 번짐, 끄면 작아짐)', 'dither') : null,
      st.fmt === 'webp' ? lab('화질', seg([[0.6, '작게'], [0.8, '보통'], [0.9, '좋게'], [1, '최고']], 'quality', Number)) : null,
      est,
    );
    clearTimeout(timer);
    est.textContent = still() ? '파일 크기 짐작 중…' : st.fmt === 'png' ? '프레임마다 PNG 파일로 저장합니다' : '재생하면서 녹화합니다 (전체 길이만큼 걸림)';
    const my = ++gen;
    if (still()) timer = setTimeout(async () => {
      try {
        const b = await AX.estimate(st.fmt, st);
        if (my === gen && b) est.textContent = `예상 크기: 약 ${b > 1048576 ? (b / 1048576).toFixed(1) + 'MB' : Math.round(b / 1024) + 'KB'}${b > 10 * 1048576 ? ' — 큰 편입니다 (크기·색 수를 줄여 보세요)' : ''}`;
      } catch (e) { if (my === gen) est.textContent = '크기를 짐작하지 못했습니다: ' + e.message; }
    }, 400);
  };
  render();
  openModal({ title: '애니메이션 내보내기 (움짤·동영상)', body, wide: true, actions: [
    { label: '스프라이트 시트…', run: () => { sheetDialog(st.tag, st.scale); } },
    { label: '취소' },
    { label: '내보내기', primary: true, run: () => {
      app.settings.animExport = { fmt: st.fmt, scale: st.scale, loop: st.loop, transparent: st.transparent, dither: st.dither, colors: st.colors, quality: st.quality };
      app.saveSettingsSoon();
      setTimeout(() => app.task('움짤 만들기', () => AX.exportAnim(st.fmt, { ...st }), { delay: 0 }), 0);
    } },
  ] });
}

/** 움짤 만들기: 묻지 않고 지난 설정(처음엔 GIF 50% 무한 반복)으로 바로 */
async function quickGif() {
  if (A.frameCount() < 2) { app.toast('프레임이 2장 이상 있어야 움짤이 됩니다'); return; }
  const o = { fmt: 'gif', scale: app.doc.width <= 256 ? 4 : 0.5, loop: 0, dither: true, colors: 256, ...(app.settings.animExport || {}), tag: app.doc.anim.playTag ?? null };
  if (!['gif', 'webp', 'apng'].includes(o.fmt)) o.fmt = 'gif';
  return app.task('움짤 만들기', () => AX.exportAnim(o.fmt, o), { delay: 0 });
}

/** 립싱크: 입 트랙과 모양별 그림을 고르고 자동 배치 */
function lipSection(doc) {
  const a = doc.anim, ts = A.tracks(doc);
  if (!a.audio || !ts.length) return null;
  const t = ts.find(x => x.id === a.lipTrack) || ts.find(x => /입|mouth|lip/i.test(x.name)) || A.animFolder(doc);
  const kids = doc.childrenOf(t), map = { ...(t.meta?.lipMap || {}) };
  const opts = [['', '(없음)'], ...kids.map((c, k) => [c.id, `${k + 1}번 · ${c.name}`])];
  const sel = (shape, label) => {
    const s2 = h('select', { onchange: () => { map[shape] = s2.value || null; } }, ...opts.map(([v, l]) => h('option', { value: v }, l)));
    s2.value = map[shape] || '';
    return h('div.field', null, h('span', null, label), s2);
  };
  const tsel = h('select', { onchange: () => { a.lipTrack = tsel.value; app.bus.emit('anim:changed'); } }, ...ts.map(x => h('option', { value: x.id }, x.name)));
  tsel.value = t.id;
  const ext = h('input', { type: 'checkbox' });
  return section('립싱크 (입 모양 자동)',
    h('div.help', null, '입 모양 그림을 트랙 하나에 그려 두고(닫힘·반·ㅏ·ㅣ·ㅜ), 어느 그림이 어느 모양인지 고르면 소리에 맞춰 그 트랙의 칸을 자동으로 채웁니다. 소리 크기와 높낮이로 대략 고르는 것이라, 끝나고 타임라인에서 다듬으세요.'),
    h('div.field', null, h('span', null, '입 트랙'), tsel),
    ...LS.SHAPES.map(([k, l]) => sel(k, l)),
    h('label.field.check', null, h('span', null, '소리 길이만큼 프레임 늘리기'), ext),
    row(button('자동 배치', async () => {
      if (!Object.values(map).some(Boolean)) { app.toast('모양마다 그림을 하나 이상 골라 주세요'); return; }
      const r = await app.task('립싱크 분석', () => LS.applyLipSync(t, map, { extend: ext.checked }), { delay: 0 });
      if (r) app.toast(`립싱크: ${r.frames}프레임 배치 (${LS.SHAPES.map(([k, l]) => (r.count[k] ? l.split(' ')[0] + ' ' + r.count[k] : '')).filter(Boolean).join(', ')})`);
    }, 'primary')),
  );
}

async function sheetDialog(tag = null, scale = null) {
  if (A.frameCount() < 1) { app.toast('애니메이션 프레임이 없습니다'); return; }
  const v = await form('스프라이트 시트 내보내기', [
    { key: 'layout', label: '배치', type: 'segment', options: [['grid', '격자'], ['h', '가로 한 줄'], ['v', '세로 한 줄']], value: 'grid' },
    { key: 'cols', label: '격자 칸 수 (가로, 0 = 자동)', type: 'number', value: 0, min: 0, max: 64 },
    { key: 'scale', label: '크기 (배)', type: 'number', value: scale ?? (app.doc.width <= 256 ? 1 : 0.5), step: 0.25, min: 0.1, max: 8 },
    { key: 'pad', label: '칸 사이 여백 (px)', type: 'number', value: 0, min: 0, max: 32 },
    { key: 'trim', label: '투명한 가장자리 잘라내기 (JSON 에 원래 위치 기록)', type: 'checkbox', value: false },
    { key: 'bg', label: '흰 바탕 (끄면 투명)', type: 'checkbox', value: false },
    { key: 'camera', label: '카메라 적용', type: 'checkbox', value: false },
    { key: 'json', label: 'JSON 형식', type: 'segment', options: [['hash', 'Hash (Aseprite 기본)'], ['array', 'Array']], value: 'hash' },
    (app.doc.anim.tags || []).length ? { key: 'tag', label: '구간', type: 'select', options: tagOptions(), value: tag != null ? String(tag) : '' } : null,
    { key: 'info', type: 'info', label: '게임 엔진(Unity·Godot·Phaser 등)에서 쓰는 Aseprite 형식 JSON(프레임 위치·시간·태그)을 함께 저장합니다.' },
  ].filter(Boolean), { okLabel: '내보내기' });
  if (!v) return;
  await app.task('스프라이트 시트', () => S.exportSheet({ layout: v.layout, cols: +v.cols, scale: +v.scale, pad: +v.pad, trim: v.trim, bg: v.bg ? '#ffffff' : null, camera: v.camera, json: v.json, tag: v.tag ? +v.tag : null }), { delay: 0 });
}

async function importSheetDialog() {
  const f = await pickFile('image/*');
  if (!f) return;
  const img = new Image();
  img.src = URL.createObjectURL(f);
  await img.decode();
  const v = await form('스프라이트 시트 가져오기', [
    { key: 'info0', type: 'info', label: `${f.name} · ${img.width}×${img.height}` },
    { key: 'how', label: '자르는 방법', type: 'segment', options: [['grid', '칸 수로'], ['size', '칸 크기로'], ['json', 'JSON 파일로']], value: 'grid' },
    { key: 'cols', label: '가로 칸 수', type: 'number', value: Math.max(1, Math.round(img.width / Math.min(img.width, img.height))), min: 1 },
    { key: 'rows', label: '세로 칸 수', type: 'number', value: 1, min: 1 },
    { key: 'fw', label: '칸 너비 (칸 크기로)', type: 'number', value: Math.min(img.width, img.height), min: 1 },
    { key: 'fh', label: '칸 높이 (칸 크기로)', type: 'number', value: Math.min(img.width, img.height), min: 1 },
    { key: 'pad', label: '칸 사이 여백', type: 'number', value: 0, min: 0 },
    { key: 'skip', label: '완전히 빈 칸은 빼기', type: 'checkbox', value: true },
    { key: 'into', label: '넣을 곳', type: 'segment', options: [['new', '새 작품'], ['here', '지금 작품의 새 트랙']], value: 'new' },
  ], { okLabel: '가져오기' });
  if (!v) return;
  let o = { pad: +v.pad, skipEmpty: v.skip };
  if (v.how === 'json') {
    const jf = await pickFile('.json');
    if (!jf) return;
    o = { json: JSON.parse(await jf.text()) };
  } else if (v.how === 'size') {
    o.fw = +v.fw; o.fh = +v.fh;
    o.cols = Math.floor((img.width + o.pad) / (o.fw + o.pad)); o.rows = Math.floor((img.height + o.pad) / (o.fh + o.pad));
  } else { o.cols = +v.cols; o.rows = +v.rows; }
  const { frames, tags } = S.sliceSheet(img, o);
  if (!frames.length) { app.toast('잘라 낸 칸이 없습니다', 'warn'); return; }
  const name = f.name.replace(/\.[^.]+$/, '');
  if (v.into === 'here' && app.doc) S.framesToDoc(frames, { name, tags, into: app.doc });
  else { const d = S.framesToDoc(frames, { name, tags }); setDoc(d); d.modified = true; }
  app.toast(`${frames.length}장을 프레임으로 가져왔습니다`);
}

export async function importAseprite(file = null) {
  const f = file || await pickFile('.aseprite,.ase');
  if (!f) return;
  const d = await app.task('Aseprite 열기', async () => S.asepriteToDoc(await f.arrayBuffer(), f.name.replace(/\.(aseprite|ase)$/i, '')), { delay: 0 });
  if (!d) return;
  setDoc(d); d.modified = true;
  app.toast(`Aseprite: 트랙 ${A.tracks(d).length}개 · ${A.frameCount(d)}프레임${d.anim.tags?.length ? ` · 태그 ${d.anim.tags.length}개` : ''}${d.importedPalette?.length ? ` · 팔레트 ${d.importedPalette.length}색` : ''}`, 'info', {
    duration: 7000,
    actions: d.importedPalette?.length ? [{ label: '팔레트로 쓰기', run: () => { app.settings.palette = d.importedPalette.slice(0, 256); app.saveSettingsSoon(); app.bus.emit('palette:changed'); } }] : [],
  });
}

function toggleLight() {
  const doc = app.doc, t = A.animFolder(doc), c = t && A.celAt(t, doc.anim.frame, doc);
  if (!c) { app.toast('라이트 테이블에 올릴 그림 칸을 고르세요'); return; }
  const L = doc.anim.light ||= { ids: [], opacity: 0.25 };
  const i = L.ids.indexOf(c.id);
  if (i >= 0) L.ids.splice(i, 1); else L.ids.push(c.id);
  app.markModified(); app.bus.emit('anim:changed'); app.requestRender();
  app.toast(i >= 0 ? '라이트 테이블에서 내렸습니다' : `"${c.name}" 을(를) 라이트 테이블에 올렸습니다 (어느 프레임에서나 흐리게 보임)`);
}

async function tagDialog(i = null) {
  const doc = app.doc, n = A.frameCount(doc), t = i != null ? doc.anim.tags[i] : null, f = doc.anim.frame;
  if (!n) { app.toast('애니메이션 프레임이 없습니다'); return; }
  const v = await form(t ? '태그 고치기' : '태그(구간) 만들기', [
    { key: 'name', label: '이름 (예: 걷기, 점프)', type: 'text', value: t?.name || `구간 ${(doc.anim.tags?.length || 0) + 1}` },
    { key: 'from', label: '시작 프레임', type: 'number', value: (t?.from ?? f) + 1, min: 1, max: n },
    { key: 'to', label: '끝 프레임', type: 'number', value: (t?.to ?? Math.min(n - 1, f + 3)) + 1, min: 1, max: n },
    { key: 'dir', label: '재생 방향', type: 'segment', options: [['fwd', '정방향 →'], ['rev', '역방향 ←'], ['ping', '핑퐁 ↔']], value: t?.dir || 'fwd' },
    { key: 'color', label: '색', type: 'color', value: t?.color || '#3d7be0' },
  ], { okLabel: t ? '고치기' : '만들기' });
  if (!v) return;
  const tag = { name: v.name || '구간', from: Math.max(0, Math.min(+v.from, +v.to) - 1), to: Math.min(n - 1, Math.max(+v.from, +v.to) - 1), dir: v.dir, color: v.color };
  if (t) A.updateTag(i, tag); else A.addTag(tag);
}

/** 타임시트: 프레임(행) × 트랙(열). 칸 = 셀 번호, 빈칸 = 안 보임, "|" = 위 칸 이어 보이기 */
async function timesheetDialog() {
  const doc = app.doc, ts = A.tracks(doc).slice().reverse(), n = A.frameCount(doc);
  if (!ts.length) { app.toast('애니메이션 트랙이 없습니다'); return; }
  const nums = ts.map(t => new Map(doc.childrenOf(t).map((c, k) => [c.id, String(k + 1)])));
  const table = h('table.timesheet');
  table.append(h('tr', null, h('th', null, '프레임'), h('th', null, 'ms'), ...ts.map(t => h('th', null, t.name))));
  const inputs = ts.map(() => []);
  for (let f = 0; f < n; f++) {
    const tr = h(`tr${doc.anim.frame === f ? '.on' : ''}`, null, h('td', null, String(f + 1)), h('td', null, String(Math.round(A.frameMs(f, doc)))));
    ts.forEach((t, k) => {
      const xs = A.xsOf(t, doc), id = xs[f];
      const val = !id ? '' : f > 0 && xs[f - 1] === id ? '|' : nums[k].get(id) || '?';
      const inp = h('input', { value: val, size: 3 });
      inputs[k][f] = inp;
      tr.append(h('td', null, inp));
    });
    table.append(tr);
  }
  const apply = () => {
    A.editAnim('타임시트', () => {
      ts.forEach((t, k) => {
        const kids = doc.childrenOf(t), xs = t.meta.xs;
        let prev = null;
        for (let f = 0; f < n; f++) {
          const s = inputs[k][f].value.trim();
          if (s === '|' || s === '-') { xs[f] = prev; continue; }
          if (!s) { xs[f] = null; prev = null; continue; }
          const c = kids[(+s | 0) - 1];
          xs[f] = c ? c.id : xs[f];
          prev = xs[f];
        }
      });
    });
  };
  const csv = () => {
    const rows = [['프레임', 'ms', ...ts.map(t => t.name)]];
    for (let f = 0; f < n; f++) rows.push([f + 1, Math.round(A.frameMs(f, doc)), ...ts.map((t, k) => inputs[k][f].value)]);
    downloadBlob(new Blob(['﻿' + rows.map(r => r.join(',')).join('\n')], { type: 'text/csv' }), `${safeName(doc.name)}_타임시트.csv`);
  };
  openModal({
    title: '타임시트 (노출표)', wide: true,
    body: h('div', null, h('div.help', null, '칸에 셀 번호(트랙 안 몇 번째 그림)를 적습니다. "|" = 위 칸 그림을 이어 보이기, 빈칸 = 아무것도 안 보임. 같은 번호를 떨어진 칸에 다시 쓰면 같은 그림(연결)입니다.'), h('div.timesheet-wrap', null, table)),
    actions: [{ label: 'CSV 로 저장', run: () => { csv(); return false; } }, { label: '닫기' }, { label: '적용', primary: true, run: apply }],
  });
}

/** 소리: 넣기·빼기·시작 위치·크기, 프레임 넘길 때 잠깐 듣기 */
function soundSection(a, list) {
  const au = a.audio;
  return section('소리',
    au ? h('div.help', null, `🎵 ${au.name}`) : h('div.help', null, '배경음·대사 녹음을 넣으면 재생·동영상에 함께 나오고, 프레임마다 소리 크기 막대가 보여 입 모양 맞추기에 좋습니다.'),
    row(button(au ? '다른 소리로' : '소리 넣기…', async () => { const f = await pickFile('audio/*'); if (f) X.setAudio(f); }), au ? button('빼기', () => X.removeAudio()) : null),
    au ? slider('시작 위치 (초)', { min: -10, max: 10, step: 0.05, get: () => au.offset || 0, set: v => { au.offset = v; app.doc._audio && (app.doc._audio.peaks = null); app.markModified(); }, onEnd: () => app.bus.emit('anim:changed'), format: v => (v > 0 ? '+' : '') + v.toFixed(2) + '초' }) : null,
    au ? slider('크기', { min: 0, max: 2, step: 0.05, get: () => au.volume ?? 1, set: v => { au.volume = v; app.markModified(); }, format: v => Math.round(v * 100) + '%' }) : null,
    au ? toggle('프레임 넘길 때 소리 잠깐 듣기', () => app.settings.animScrub !== false, v => { app.settings.animScrub = v; app.saveSettingsSoon(); }) : null,
  );
}

/** 카메라: 프레임마다 보이는 영역 (팬·줌·회전), 사이 프레임은 부드럽게 이어짐 */
function cameraSection(a) {
  const doc = app.doc, i = a.frame || 0;
  const cur = X.cameraAt(doc, i) || { x: 0.5, y: 0.5, z: 1, r: 0 };
  const isKey = !!a.camera?.keys?.some(k => k.f === i);
  const set = (k) => (v) => X.setCameraKey(doc, i, { [k]: v });
  const sl = (label, k, min, max, step, fmt) => slider(label, { min, max, step, get: () => (X.cameraAt(doc, i) || cur)[k], set: (v) => { const c = doc.anim.camera?.keys?.find(q => q.f === i); if (c) { c[k] = v; app.requestRender(); } }, onEnd: () => set(k)((X.cameraAt(doc, i) || cur)[k]), format: fmt });
  return section(`카메라 (${i + 1}번 프레임${isKey ? ' · 키' : ''})`,
    isKey ? null : h('div.help', null, a.camera?.keys?.length ? '이 프레임은 앞뒤 키 사이에서 자동으로 움직입니다. 값을 바꾸려면 키를 넣으세요.' : '카메라 키를 두 개 이상 넣으면 그 사이 프레임에서 화면이 부드럽게 이동·확대합니다 (내보내기에 반영).'),
    row(button(isKey ? '키 다시 넣기' : '이 프레임에 카메라 키', () => X.setCameraKey(doc, i, {})), isKey ? button('키 지우기', () => X.removeCameraKey(doc, i)) : null),
    isKey ? sl('가로 위치', 'x', 0, 1, 0.005, v => Math.round(v * 100) + '%') : null,
    isKey ? sl('세로 위치', 'y', 0, 1, 0.005, v => Math.round(v * 100) + '%') : null,
    isKey ? sl('확대', 'z', 1, 6, 0.01, v => Math.round(v * 100) + '%') : null,
    isKey ? sl('회전', 'r', -45, 45, 0.5, v => v + '°') : null,
    a.camera?.keys?.length ? row(toggle('부드럽게 출발·정지', () => a.camera.ease !== false, v => { a.camera.ease = v; app.markModified(); app.requestRender(); }), toggle('원고에 카메라 영역 표시', () => app.settings.showCamera !== false, v => { app.settings.showCamera = v; app.saveSettingsSoon(); app.requestRender(); })) : null,
    a.camera?.keys?.length || a.audio ? row(button('카메라·소리로 미리보기', () => previewPlayer())) : null,
  );
}

/** 내보낼 결과(카메라 적용 + 소리 + 태그 구간)를 그대로 재생해 보기 */
function previewPlayer() {
  const doc = app.doc, n = A.frameCount(doc);
  if (!n) return;
  const s = Math.min(1, 720 / Math.max(doc.width, doc.height));
  const c = h('canvas', { width: Math.round(doc.width * s), height: Math.round(doc.height * s), style: { maxWidth: '100%', maxHeight: '64vh', background: '#fff', display: 'block', imageRendering: doc.width <= 256 ? 'pixelated' : 'auto' } });
  const x = c.getContext('2d');
  const order = A.playOrder(doc), times = A.timeline(doc);
  let k = 0, playing = true, last = performance.now();
  const frames = new Map();
  const frame = (f) => { if (!frames.has(f)) frames.set(f, X.applyCamera(doc, f, app.compositor.flatten(doc, { scale: s, frame: f, background: '#ffffff' }))); return frames.get(f); };
  const tick = (now) => {
    if (!playing) return;
    if (now - last >= A.frameMs(order[k], doc)) { last = now; k = (k + 1) % order.length; if (k === 0) X.playAudio(times[order[0]]); }
    x.drawImage(frame(order[k]), 0, 0);
    requestAnimationFrame(tick);
  };
  X.playAudio(times[order[0]]);
  requestAnimationFrame(tick);
  openModal({ title: '미리보기 (카메라·소리·구간 적용)', body: h('div', null, c), wide: true, actions: [{ label: '닫기', primary: true }], onClose: () => { playing = false; X.stopAudio(); } });
}

export function timelinePanel(el) {
  const body = h('div.panel-scroll');
  el.append(body);
  const thumbs = new Map();
  const drawThumb = (l, c) => {
    const doc = app.doc, s = Math.min(c.width / doc.width, c.height / doc.height);
    const x = c.getContext('2d');
    x.clearRect(0, 0, c.width, c.height);
    const w = doc.width * s, hh = doc.height * s;
    x.fillStyle = '#fff'; x.fillRect((c.width - w) / 2, (c.height - hh) / 2, w, hh);
    x.imageSmoothingEnabled = doc.width > 256;
    if (!l.isFolder) x.drawImage(l.displayCanvas || l.canvas, (c.width - w) / 2, (c.height - hh) / 2, w, hh);
  };
  const ib = (ic, title, cmd, on) => h(`button.icon-btn${on ? '.on' : ''}`, { type: 'button', title, html: icon(ic, 20), onclick: () => app.run(cmd) });
  const tb = (label, title, run, on) => h(`button.btn.sm${on ? '.primary' : ''}`, { type: 'button', title, onclick: run }, label);

  const render = () => {
    const doc = app.doc;
    body.innerHTML = '';
    thumbs.clear();
    if (!doc) return;
    const ts = A.tracks(doc), n = A.frameCount(doc);
    if (!ts.length || !n) {
      body.append(
        h('div.help', null, '애니메이션 트랙(폴더) 안의 레이어 하나하나가 그림 칸이 됩니다. 트랙을 여러 개 만들면 몸·팔·배경을 따로 움직일 수 있습니다 (Aseprite·클립스튜디오식 타임라인).'),
        row(button('애니메이션 만들기', () => app.run('anim.create'), 'primary'), button('레이어들을 프레임으로', () => app.run('anim.fromLayers'))),
        row(button('스프라이트 시트 가져오기', () => app.run('anim.importSheet')), button('Aseprite 파일 열기', () => app.run('anim.importAse'))),
      );
      return;
    }
    const a = doc.anim, cur = A.animFolder(doc), f0 = a.frame;
    // ---- 위 막대 ----
    body.append(h('div.anim-controls', null,
      ib('prev', '이전 (,)', 'anim.prev'),
      ib(a.playing ? 'stop' : 'play', '재생/정지 (Shift+Enter)', 'anim.play', a.playing),
      ib('next', '다음 (.)', 'anim.next'),
      h('span.anim-pos', null, `${f0 + 1} / ${n}`),
      tb('+빈', '빈 프레임 추가 (지금 트랙에 새 그림)', () => A.addFrame('blank')),
      tb('+복제', '지금 그림을 복제해서 다음 프레임에', () => A.addFrame('copy')),
      tb('+연결', '같은 그림을 다음 프레임에 이어 쓰기 (하나 고치면 같이 바뀜)', () => A.addFrame('link')),
      ib('trash', '프레임 삭제 (모든 트랙)', 'anim.delete'),
      tb('어니언', '어니언 스킨', () => app.run('anim.onion'), a.onion),
      tb('라이트', '지금 칸을 라이트 테이블에 올리기/내리기', () => app.run('anim.light'), !!(cur && a.light?.ids?.includes(A.celAt(cur, f0, doc)?.id))),
    ));
    // ---- 타임라인 격자: 트랙(줄) × 프레임(칸) ----
    const grid = h('div.tl-grid', { style: { gridTemplateColumns: `110px repeat(${n}, 40px)` } });
    // 태그 줄
    grid.append(h('div.tl-name.tl-head', null, '태그'));
    const tagRow = Array(n).fill(null);
    (a.tags || []).forEach((t, i) => { for (let f = t.from; f <= Math.min(n - 1, t.to); f++) tagRow[f] ||= { t, i, start: f === t.from }; });
    for (let f = 0; f < n; f++) {
      const g = tagRow[f];
      grid.append(h(`div.tl-tag${g ? '' : '.none'}`, { style: g ? { background: g.t.color } : null, title: g ? `${g.t.name} (${g.t.from + 1}~${g.t.to + 1}, ${{ fwd: '정방향', rev: '역방향', ping: '핑퐁' }[g.t.dir]})` : '', onclick: () => g && tagDialog(g.i) }, g?.start ? g.t.name : ''));
    }
    // 프레임 번호 줄
    grid.append(h('div.tl-name.tl-head', null, '프레임'));
    for (let f = 0; f < n; f++) grid.append(h(`div.tl-num${f === f0 ? '.on' : ''}`, { title: `${Math.round(A.frameMs(f, doc))}ms`, onclick: () => A.setFrame(f) }, String(f + 1), a.ms?.[f] ? h('i', null, '•') : null));
    // 트랙 줄 (위에 있는 트랙이 위)
    for (const t of ts.slice().reverse()) {
      const xs = A.xsOf(t, doc);
      const count = new Map(); xs.forEach((id, f) => { if (id && (f === 0 || xs[f - 1] !== id)) count.set(id, (count.get(id) || 0) + 1); });
      const kidNum = new Map(doc.childrenOf(t).map((c, k) => [c.id, k + 1]));
      const eye = h('button.tl-eye', { type: 'button', title: '보이기/숨기기', html: icon(t.visible ? 'eye' : 'eyeOff', 14), onclick: (e) => { e.stopPropagation(); ops.setLayerProps(t, { visible: !t.visible }, '트랙 보이기'); } });
      grid.append(h(`div.tl-name${t === cur ? '.on' : ''}`, { title: t.name, onclick: () => A.selectCell(t, f0) }, eye, h('span', null, t.name)));
      for (let f = 0; f < n; f++) {
        const id = xs[f], start = id && (f === 0 || xs[f - 1] !== id), end = id && (f === n - 1 || xs[f + 1] !== id);
        const cls = ['tl-cell'];
        if (!id) cls.push('empty'); else if (!start) cls.push('hold');
        if (id && count.get(id) > 1) cls.push('linked');
        if (end && id) cls.push('end');
        if (t === cur && f === f0) cls.push('on'); else if (f === f0) cls.push('col');
        const cell = h('div.' + cls.join('.'), { title: !id ? '빈 칸 (두 번 누르면 새 그림)' : `${kidNum.get(id) || '?'}번 그림${count.get(id) > 1 ? ' · 다른 프레임과 연결됨' : ''}` });
        if (start) {
          const c = h('canvas', { width: 36, height: 27 });
          const l = doc.byId(id);
          if (l) { drawThumb(l, c); thumbs.set(id, [...(thumbs.get(id) || []), c]); }
          cell.append(c, h('b', null, String(kidNum.get(id) || '?')));
        }
        cell.onclick = () => A.selectCell(t, f);
        cell.ondblclick = () => { if (!id) A.newCelHere(t, f); };
        grid.append(cell);
      }
    }
    body.append(h('div.tl-wrap', null, grid));
    body.append(row(tb('+ 트랙', '트랙 추가', () => app.run('anim.track')), tb('칸 비우기', '지금 트랙의 지금 칸을 비우기', () => app.run('anim.clearCell')), tb('연결 끊기', '이 칸만 따로 고칠 수 있게 복제', () => app.run('anim.unlink')),
      tb('◀ 칸', '지금 칸을 앞으로', () => A.moveCel(-1)), tb('칸 ▶', '지금 칸을 뒤로', () => A.moveCel(1)), tb('타임시트', '노출표로 보기·고치기', () => app.run('anim.timesheet'))));

    // ---- 지금 칸 ----
    const hold = A.holdOf(f0);
    body.append(section(`${f0 + 1}번 프레임`,
      h('div.help', null, '지금 트랙의 그림을 몇 장 동안 보여 줄지 (뒤 칸이 밀림)'),
      h('div.s3d-prims', null, ...[1, 2, 3, 4, 6, 8].map(k => h(`button.btn${hold === k ? '.primary' : ''}`, { type: 'button', onclick: () => A.setHold(f0, k) }, k === 1 ? '1장' : `${k}장`))),
      (() => { let ms = a.ms?.[f0] || 0; return slider('이 프레임 시간 (Aseprite식, 0 = 속도 따라감)', { min: 0, max: 1000, step: 10, get: () => ms, set: (v) => { ms = v; }, onEnd: () => A.setFrameMs(f0, ms || null), format: v => (v ? `${v}ms` : `기본 (${Math.round(1000 / a.fps)}ms)`) }); })(),
    ));

    // ---- 태그 ----
    body.append(section('태그 (구간)',
      ...(a.tags || []).map((t, i) => h('div.tl-tagrow', null,
        h('i', { style: { background: t.color } }), h('span', null, `${t.name} · ${t.from + 1}~${t.to + 1} · ${{ fwd: '→', rev: '←', ping: '↔' }[t.dir]}`),
        tb(a.playTag === i ? '전체 재생' : '이 구간만 재생', '', () => { a.playTag = a.playTag === i ? null : i; render(); }, a.playTag === i),
        tb('고치기', '', () => tagDialog(i)), tb('×', '지우기', () => A.removeTag(i)))),
      row(button('태그 만들기…', () => tagDialog()), h('span.help', null, '걷기·점프처럼 구간에 이름을 붙이고 정방향·역방향·핑퐁으로 반복합니다. 스프라이트 시트 JSON 에도 들어갑니다.')),
    ));

    // ---- 재생 ----
    body.append(section('재생',
      slider('속도', { min: 1, max: 60, step: 1, get: () => a.fps, set: v => { a.fps = v; app.markModified(); }, format: v => v + ' fps' }),
      row(toggle('반복', () => a.loop, v => { a.loop = v; }), h('span.help', null, '방향'),
        ...[['fwd', '→'], ['rev', '←'], ['ping', '↔']].map(([k, lab]) => tb(lab, { fwd: '정방향', rev: '역방향', ping: '핑퐁' }[k], () => { a.dir = k; app.markModified(); render(); }, (a.dir || 'fwd') === k))),
      row(button('미리보기 (카메라·소리·구간)', () => previewPlayer())),
    ));

    // ---- 어니언 스킨 ----
    body.append(section('어니언 스킨',
      row(toggle('켜기', () => a.onion, v => { a.onion = v; app.requestRender(); render(); }), toggle('끝과 처음 이어서', () => a.loop, v => { a.loop = v; app.requestRender(); })),
      slider('진하기', { min: 0.05, max: 0.8, step: 0.05, get: () => a.onionOpacity, set: v => { a.onionOpacity = v; app.requestRender(); }, format: v => Math.round(v * 100) + '%' }),
      slider('멀수록 옅게', { min: 0, max: 0.9, step: 0.05, get: () => a.onionFalloff ?? 0.5, set: v => { a.onionFalloff = v; app.requestRender(); }, format: v => Math.round(v * 100) + '%' }),
      row(slider('앞 장수', { min: 0, max: 5, step: 1, get: () => a.onionPrev, set: v => { a.onionPrev = v; app.requestRender(); } }),
        slider('뒤 장수', { min: 0, max: 5, step: 1, get: () => a.onionNext, set: v => { a.onionNext = v; app.requestRender(); } })),
      row(h('label.tl-color', null, '앞 색 ', h('input', { type: 'color', value: a.onionPrevColor || '#e0455a', oninput: (e) => { a.onionPrevColor = e.target.value; app.requestRender(); } })),
        h('label.tl-color', null, '뒤 색 ', h('input', { type: 'color', value: a.onionNextColor || '#2fa866', oninput: (e) => { a.onionNextColor = e.target.value; app.requestRender(); } }))),
      h('div.help', null, '같은 그림을 길게 보여 주는 칸은 건너뛰고 "다른 그림"만 잔상으로 보여 줍니다.'),
    ));

    // ---- 라이트 테이블 ----
    const L = a.light || { ids: [] };
    body.append(section('라이트 테이블',
      h('div.help', null, '올려 둔 그림은 어느 프레임에서나 흐리게 보입니다 (원화·중간 그림 그릴 때 기준). 내보내기에는 안 나옵니다.'),
      ...L.ids.map(id => { const l = doc.byId(id); return l ? h('div.tl-tagrow', null, h('i', { style: { background: L.color || '#8a63d2' } }), h('span', null, `${doc.byId(doc.parentOf(l))?.name || ''} / ${l.name}`), tb('내리기', '', () => { L.ids = L.ids.filter(x => x !== id); app.requestRender(); render(); })) : null; }),
      row(button('지금 칸 올리기/내리기', () => app.run('anim.light'))),
      L.ids.length ? slider('진하기', { min: 0.05, max: 0.8, step: 0.05, get: () => L.opacity ?? 0.25, set: v => { L.opacity = v; app.requestRender(); }, format: v => Math.round(v * 100) + '%' }) : null,
    ));

    const mt = A.animFolder(doc);
    if (mt) body.append(motionSection(mt, doc));
    body.append(soundSection(a));
    const lip = lipSection(doc);
    if (lip) body.append(lip);
    body.append(cameraSection(a));
    body.append(row(button(icon('export', 16) + ' 움짤 만들기 (GIF)', () => app.run('anim.gifQuick'), 'primary'), button('내보내기 (형식 고르기)…', () => app.run('anim.export')), button('스프라이트 시트', () => app.run('anim.sheet'))));
    body.append(h('div.help', null, '칸: 그림 = 새 그림 시작, 옅은 칸 = 앞 그림 이어 보이기, 점선 테두리 = 다른 프레임과 연결된 그림, 빈 칸 = 안 보임(두 번 누르면 새 그림). 트랙 이름을 누르면 그 줄을 고릅니다.'));
    if (a.audio) X.framePeaks(doc, A.timeline(doc)).then(pk => {
      if (!pk) return;
      body.querySelectorAll('.tl-num').forEach((b, k) => b.append(h('i.frame-wave', { style: { height: Math.max(1, Math.round((pk[k] || 0) * 12)) + 'px' }, title: '소리 크기' })));
    });
    body.querySelector('.tl-num.on')?.scrollIntoView({ inline: 'nearest', block: 'nearest' });
  };
  let t = 0;
  const soon = () => { clearTimeout(t); t = setTimeout(render, 50); };
  app.bus.on('anim:changed', soon);
  app.bus.on('layers:changed', soon);
  app.bus.on('layer:pixels', (l) => {
    const cs = thumbs.get(l.id);
    if (!cs) return;
    clearTimeout(cs.t);
    cs.t = setTimeout(() => cs.forEach(c => drawThumb(l, c)), 300);
  });
  render();
}
