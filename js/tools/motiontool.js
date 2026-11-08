// 모션 경로 도구: 지금 트랙의 그림을 끌어 이 프레임의 위치 키를 찍는다 (Shift = 회전, Alt = 크기).
// 경로와 키 점이 원고 위에 보이고, 키 점을 끌면 그 키를 옮긴다.
import { app } from '../app.js';
import { addIcon } from '../ui/icons.js';
import { section, slider, button, row, select, toggle } from '../ui/controls.js';
import { h } from '../core/util.js';
import * as A from '../core/anim.js';
import * as M from '../core/motion.js';

addIcon('motion', '<path d="M4 18c3-8 7-10 12-10"/><circle cx="4" cy="18" r="2"/><circle cx="12" cy="9.5" r="2"/><path d="M16 5l4 3-4 3"/>');

const track = () => A.animFolder();

export const motionTool = {
  id: 'motion', name: '모션 경로 (애니)', icon: 'motion', cursor: 'move',
  drag: null,
  get busy() { return !!this.drag; },

  down(e) {
    const t = track();
    if (!t) { app.toast('애니메이션 트랙을 먼저 고르세요 (애니 패널)'); return; }
    const doc = app.doc, f = doc.anim.frame, an = M.anchorOf(t, doc);
    const before = structuredClone(M.motionOf(t));
    // 키 점을 잡았는지 (화면 12px 안)
    const keys = M.motionOf(t)?.keys || [];
    let hit = null;
    for (const k of keys) {
      const p = app.view.toScreen(an.x + (k.x || 0), an.y + (k.y || 0));
      if (Math.hypot(p.x - e.sx, p.y - e.sy) < 12) { hit = k.f; break; }
    }
    const kf = hit ?? f;
    const start = M.valuesAt(t, kf) || { x: 0, y: 0, s: 1, r: 0, o: 1 };
    // 이 프레임에 키가 없으면 지금 값으로 하나 만들어 두고 끌기
    const m = structuredClone(M.motionOf(t) || { keys: [] });
    if (!m.keys.some(k => k.f === kf)) { m.keys.push({ f: kf, ...start, ease: 'inout' }); m.keys.sort((a, b) => a.f - b.f); }
    t.meta = { ...(t.meta || {}), motion: m };
    const c = { x: an.x + start.x, y: an.y + start.y };
    this.drag = { t, kf, before, start, e0: e, a0: Math.atan2(e.y - c.y, e.x - c.x), d0: Math.max(4, Math.hypot(e.x - c.x, e.y - c.y)), c, mode: e.shift ? 'rot' : e.alt ? 'scale' : 'move' };
  },

  move(e) {
    const d = this.drag;
    if (!d) return;
    const k = M.motionOf(d.t).keys.find(q => q.f === d.kf);
    if (d.mode === 'move') { k.x = d.start.x + (e.x - d.e0.x); k.y = d.start.y + (e.y - d.e0.y); }
    else if (d.mode === 'rot') { k.r = d.start.r + ((Math.atan2(e.y - d.c.y, e.x - d.c.x) - d.a0) * 180) / Math.PI; if (e.ctrl) k.r = Math.round(k.r / 15) * 15; }
    else k.s = Math.max(0.05, d.start.s * (Math.hypot(e.x - d.c.x, e.y - d.c.y) / d.d0));
    app.requestRender();
  },

  up() {
    const d = this.drag;
    this.drag = null;
    if (!d) return;
    const after = structuredClone(M.motionOf(d.t));
    const apply = (v) => { d.t.meta = { ...(d.t.meta || {}) }; if (v) d.t.meta.motion = structuredClone(v); else delete d.t.meta.motion; app.markModified(); app.bus.emit('anim:changed'); app.requestRender(); };
    app.history.push({ label: '모션 키', undo: () => apply(d.before), redo: () => apply(after) });
    app.markModified();
    app.bus.emit('anim:changed');
  },

  overlay(ctx, view) {
    const t = track(), doc = app.doc;
    if (!t) return;
    const n = A.frameCount(doc), an = M.anchorOf(t, doc), mo = M.motionOf(t);
    const pts = [];
    for (let f = 0; f < n; f++) { const v = M.valuesAt(t, f) || { x: 0, y: 0 }; pts.push(view.toScreen(an.x + v.x, an.y + v.y)); }
    ctx.save();
    ctx.lineWidth = 2; ctx.strokeStyle = 'rgba(232,163,61,.85)'; ctx.setLineDash([5, 4]);
    ctx.beginPath(); pts.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y))); ctx.stroke();
    ctx.setLineDash([]);
    pts.forEach((p, f) => { ctx.fillStyle = f === doc.anim.frame ? '#fff' : 'rgba(232,163,61,.7)'; ctx.beginPath(); ctx.arc(p.x, p.y, f === doc.anim.frame ? 4 : 2, 0, Math.PI * 2); ctx.fill(); });
    for (const k of mo?.keys || []) {
      const p = view.toScreen(an.x + (k.x || 0), an.y + (k.y || 0));
      ctx.fillStyle = k.f === doc.anim.frame ? '#e8a33d' : '#fff'; ctx.strokeStyle = '#e8a33d'; ctx.lineWidth = 2;
      ctx.beginPath(); ctx.rect(p.x - 6, p.y - 6, 12, 12); ctx.fill(); ctx.stroke();
      ctx.fillStyle = '#000'; ctx.font = '10px sans-serif'; ctx.fillText(String(k.f + 1), p.x + 8, p.y - 8);
    }
    // 기준점 (회전·크기의 중심)
    const a = view.toScreen(an.x, an.y);
    ctx.strokeStyle = 'rgba(61,123,224,.9)'; ctx.lineWidth = 1.5;
    ctx.beginPath(); ctx.moveTo(a.x - 8, a.y); ctx.lineTo(a.x + 8, a.y); ctx.moveTo(a.x, a.y - 8); ctx.lineTo(a.x, a.y + 8); ctx.stroke();
    ctx.restore();
  },

  options(el) {
    const t = track(), doc = app.doc;
    if (!t) { el.append(section('모션 경로', h('div.help', null, '애니메이션 트랙을 고른 뒤 쓰세요 (애니 패널).'))); return; }
    el.append(motionSection(t, doc));
  },
};

/** 옵션·애니 패널 공용: 지금 프레임의 키 값과 버튼 */
export function motionSection(t, doc = app.doc) {
  const f = doc.anim.frame, m = M.motionOf(t), key = m?.keys?.find(k => k.f === f), v = M.valuesAt(t, f) || { x: 0, y: 0, s: 1, r: 0, o: 1 };
  const setK = (prop) => (val) => M.setKey(t, f, { [prop]: val });
  const sl = (label, prop, min, max, step, fmt) => slider(label, { min, max, step, get: () => (M.valuesAt(t, f) || v)[prop], set: (val) => { const k = M.motionOf(t)?.keys?.find(q => q.f === f); if (k) { k[prop] = val; app.requestRender(); } }, onEnd: () => setK(prop)((M.valuesAt(t, f) || v)[prop]), format: fmt });
  return section(`모션 경로 · "${t.name}" ${f + 1}번 프레임${key ? ' (키)' : ''}`,
    h('div.help', null, m?.keys?.length ? '키 사이 프레임은 이징을 따라 자동으로 움직입니다. 원고 위 경로의 네모 = 키 (끌어서 옮기기).' : '모션 경로 도구로 그림을 끌면 이 프레임에 위치 키가 생깁니다 (Shift = 회전, Alt = 크기). 키를 두 개 이상 찍으면 사이가 자동으로 움직입니다.'),
    row(button(key ? '키 다시 넣기' : '이 프레임에 키', () => M.setKey(t, f, {})), key ? button('키 지우기', () => M.removeKey(t, f)) : null, button('모션 경로 도구', () => app.setTool('motion'))),
    key ? sl('가로 이동', 'x', -doc.width, doc.width, 1, (x) => Math.round(x) + 'px') : null,
    key ? sl('세로 이동', 'y', -doc.height, doc.height, 1, (x) => Math.round(x) + 'px') : null,
    key ? sl('크기', 's', 0.05, 4, 0.01, (x) => Math.round(x * 100) + '%') : null,
    key ? sl('회전', 'r', -360, 360, 1, (x) => Math.round(x) + '°') : null,
    key ? sl('불투명도', 'o', 0, 1, 0.01, (x) => Math.round(x * 100) + '%') : null,
    key ? select('다음 키까지 움직임', Object.entries(M.EASES), () => key.ease || 'inout', (val) => M.setKey(t, f, { ease: val })) : null,
    key?.ease === 'bezier' ? row(...[0, 1, 2, 3].map(i => h('input.num-sm', { type: 'number', step: 0.05, value: (key.bz || [0.42, 0, 0.58, 1])[i], title: ['x1', 'y1', 'x2', 'y2'][i], onchange: (e) => { const bz = [...(key.bz || [0.42, 0, 0.58, 1])]; bz[i] = +e.target.value; M.setKey(t, f, { bz }); } }))) : null,
    m ? row(button('기준점 = 원고 가운데', () => M.setMotion(t, { ...structuredClone(m), ax: doc.width / 2, ay: doc.height / 2 }, '모션 기준점')),
      button('기준점 = 이 그림 가운데', () => { const c = A.celAt(t, f, doc); const b = c && opaqueCenter(c); if (b) M.setMotion(t, { ...structuredClone(m), ax: b.x, ay: b.y }, '모션 기준점'); })) : null,
    m ? row(button('모든 키 지우기', () => M.setMotion(t, null, '모션 지우기')), button('그림으로 굳히기', () => { const k = A.bakeMotion(t); if (k) app.toast(`움직인 프레임 ${k}장을 그림으로 만들었습니다`); })) : null,
  );
}

function opaqueCenter(l) {
  const c = l.canvas, d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
  let x0 = c.width, y0 = c.height, x1 = -1, y1 = -1;
  for (let y = 0; y < c.height; y += 2) for (let x = 0; x < c.width; x += 2) if (d[(y * c.width + x) * 4 + 3]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
  return x1 < 0 ? null : { x: (x0 + x1) / 2, y: (y0 + y1) / 2 };
}
