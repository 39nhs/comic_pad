// 자(룰러): 평행선 / 방사선(1점 투시) / 2·3점 투시 / 동심원 — 브러시 선을 자에 맞춰 준다
import { app } from '../app.js';
import { segmented, toggle, button, row, section } from '../ui/controls.js';
import { helpText } from './basic.js';

const norm = (x, y) => { const l = Math.hypot(x, y) || 1; return { x: x / l, y: y / l }; };

/** 시작점 s 에서 갈 수 있는 방향 후보들 */
function candidates(s) {
  const out = [];
  for (const r of app.doc.rulers) {
    if (r.off) continue;
    if (r.type === 'parallel') out.push({ kind: 'line', d: norm(r.x2 - r.x1, r.y2 - r.y1) });
    else if (r.type === 'radial') out.push({ kind: 'line', d: norm(r.cx - s.x, r.cy - s.y) });
    else if (r.type === 'concentric') out.push({ kind: 'circle', c: { x: r.cx, y: r.cy }, r: Math.hypot(s.x - r.cx, s.y - r.cy) });
    else if (r.type === 'perspective') {
      for (const vp of r.vps) out.push({ kind: 'line', d: norm(vp.x - s.x, vp.y - s.y) });
      if (r.vps.length < 3 && r.vertical !== false) out.push({ kind: 'line', d: { x: 0, y: 1 } });
      if (r.vps.length === 1 && r.vertical !== false) out.push({ kind: 'line', d: { x: 1, y: 0 } });
    }
  }
  return out;
}

export function rulersActive() {
  return app.settings.rulerSnap !== false && app.doc?.rulers?.some(r => !r.off);
}

/** 브러시가 쓰는 스냅 도우미. 처음 움직인 방향으로 가장 가까운 자를 고른다. */
export class RulerSnap {
  constructor(start) { this.s = { x: start.x, y: start.y }; this.c = null; }

  decide(p) {
    const v = norm(p.x - this.s.x, p.y - this.s.y);
    let best = null, bestScore = Infinity;
    for (const c of candidates(this.s)) {
      let d = c.d;
      if (c.kind === 'circle') d = norm(-(this.s.y - c.c.y), this.s.x - c.c.x);
      const score = Math.abs(v.x * d.y - v.y * d.x);
      if (score < bestScore) { bestScore = score; best = c; }
    }
    this.c = best;
    return !!best;
  }

  project(p) {
    const c = this.c;
    if (!c) return p;
    if (c.kind === 'circle') {
      const d = norm(p.x - c.c.x, p.y - c.c.y);
      return { ...p, x: c.c.x + d.x * c.r, y: c.c.y + d.y * c.r };
    }
    const t = (p.x - this.s.x) * c.d.x + (p.y - this.s.y) * c.d.y;
    return { ...p, x: this.s.x + c.d.x * t, y: this.s.y + c.d.y * t };
  }
}

// ---- 화면 표시 --------------------------------------------------------
function drawRulers(ctx, view) {
  const doc = app.doc;
  if (!doc?.rulers?.length || app.settings.showRulers === false) return;
  const editing = app.toolId === 'ruler';
  const L = Math.hypot(doc.width, doc.height) * 2;
  ctx.lineWidth = 1;
  const seg = (ax, ay, bx, by) => { const a = view.toScreen(ax, ay), b = view.toScreen(bx, by); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); };
  for (const r of doc.rulers) {
    ctx.strokeStyle = r.off ? 'rgba(150,150,150,0.25)' : editing ? 'rgba(140,90,255,0.55)' : 'rgba(140,90,255,0.28)';
    ctx.beginPath();
    if (r.type === 'parallel') {
      const d = norm(r.x2 - r.x1, r.y2 - r.y1), n = { x: -d.y, y: d.x };
      const step = Math.max(doc.width, doc.height) / 12;
      for (let i = -14; i <= 14; i++) {
        const ox = r.x1 + n.x * step * i, oy = r.y1 + n.y * step * i;
        seg(ox - d.x * L, oy - d.y * L, ox + d.x * L, oy + d.y * L);
      }
    } else if (r.type === 'radial' || r.type === 'perspective') {
      const pts = r.type === 'radial' ? [{ x: r.cx, y: r.cy }] : r.vps;
      for (const vp of pts) for (let i = 0; i < 36; i++) {
        const a = (i / 36) * Math.PI * 2;
        seg(vp.x, vp.y, vp.x + Math.cos(a) * L, vp.y + Math.sin(a) * L);
      }
      if (r.type === 'perspective' && r.vps.length >= 2) {
        // 지평선
        const [a, b] = r.vps;
        const d = norm(b.x - a.x, b.y - a.y);
        ctx.stroke();
        ctx.beginPath();
        ctx.strokeStyle = 'rgba(255,90,90,0.6)';
        seg(a.x - d.x * L, a.y - d.y * L, a.x + d.x * L, a.y + d.y * L);
      }
    } else if (r.type === 'concentric') {
      const step = Math.max(doc.width, doc.height) / 16;
      for (let i = 1; i <= 20; i++) {
        const c = view.toScreen(r.cx, r.cy);
        const rr = step * i * view.zoom;
        ctx.moveTo(c.x + rr, c.y);
        ctx.arc(c.x, c.y, rr, 0, Math.PI * 2);
      }
    }
    ctx.stroke();
  }
  if (!editing) return;
  // 손잡이
  for (const h of handles()) {
    const p = view.toScreen(h.get().x, h.get().y);
    ctx.fillStyle = h.r.off ? '#888' : '#8c5aff';
    ctx.strokeStyle = '#fff';
    ctx.lineWidth = 2;
    ctx.beginPath(); ctx.arc(p.x, p.y, 9, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  }
}

function handles() {
  const out = [];
  for (const r of app.doc.rulers) {
    if (r.type === 'parallel') {
      out.push({ r, get: () => ({ x: r.x1, y: r.y1 }), set: (p) => { r.x1 = p.x; r.y1 = p.y; } });
      out.push({ r, get: () => ({ x: r.x2, y: r.y2 }), set: (p) => { r.x2 = p.x; r.y2 = p.y; } });
    } else if (r.type === 'radial' || r.type === 'concentric') {
      out.push({ r, get: () => ({ x: r.cx, y: r.cy }), set: (p) => { r.cx = p.x; r.cy = p.y; } });
    } else if (r.type === 'perspective') {
      r.vps.forEach((vp, i) => out.push({ r, get: () => r.vps[i], set: (p) => { r.vps[i] = { x: p.x, y: p.y }; } }));
    }
  }
  return out;
}

function rulerHistory(label, before) {
  const after = structuredClone(app.doc.rulers);
  const doc = app.doc;
  app.history.push({
    label,
    undo: () => { doc.rulers = structuredClone(before); app.requestRender(); app.bus.emit('rulers:changed'); },
    redo: () => { doc.rulers = structuredClone(after); app.requestRender(); app.bus.emit('rulers:changed'); },
  });
  app.bus.emit('rulers:changed');
  app.requestRender();
}

export function addRuler(type) {
  const doc = app.doc;
  const before = structuredClone(doc.rulers);
  const c = app.view.toDoc(app.view.w / 2, app.view.h / 2);
  const W = doc.width, H = doc.height;
  if (type === 'parallel') doc.rulers.push({ type, x1: c.x - W * 0.2, y1: c.y, x2: c.x + W * 0.2, y2: c.y });
  else if (type === 'radial' || type === 'concentric') doc.rulers.push({ type, cx: c.x, cy: c.y });
  else if (type === 'p1') doc.rulers.push({ type: 'perspective', vps: [{ x: c.x, y: H * 0.4 }] });
  else if (type === 'p2') doc.rulers.push({ type: 'perspective', vps: [{ x: -W * 0.3, y: H * 0.4 }, { x: W * 1.3, y: H * 0.4 }] });
  else if (type === 'p3') doc.rulers.push({ type: 'perspective', vps: [{ x: -W * 0.3, y: H * 0.3 }, { x: W * 1.3, y: H * 0.3 }, { x: W / 2, y: H * 2.2 }] });
  app.settings.showRulers = true;
  rulerHistory('자 추가', before);
  if (app.toolId !== 'ruler') app.setTool('ruler');
  app.toast('보라색 손잡이를 끌어 위치를 맞추세요. 화면을 축소하면 소실점이 보입니다.');
}

export const rulerTool = {
  id: 'ruler', name: '자 (투시/평행)', icon: 'ruler', key: 'r', cursor: 'default', allowAltPick: false,
  drag: null,
  down(e) {
    const r = e.type === 'touch' ? 26 : 16;
    const hit = handles().find(h => { const p = app.view.toScreen(h.get().x, h.get().y); return Math.hypot(p.x - e.sx, p.y - e.sy) <= r; });
    if (hit) { this.drag = { h: hit, before: structuredClone(app.doc.rulers) }; this.busy = true; }
  },
  move(e) { if (this.drag) { this.drag.h.set({ x: e.x, y: e.y }); app.requestRender(); } },
  up() { if (this.drag) rulerHistory('자 이동', this.drag.before); this.drag = null; this.busy = false; },
  cancel() { this.drag = null; this.busy = false; },
  options(el) {
    const s = app.settings;
    const list = document.createElement('div');
    const renderList = () => {
      list.innerHTML = '';
      const names = { parallel: '평행선', radial: '방사선', concentric: '동심원', perspective: '투시' };
      app.doc.rulers.forEach((r, i) => {
        const label = r.type === 'perspective' ? `${r.vps.length}점 투시` : names[r.type];
        list.append(row(
          toggle(label, () => !r.off, v => { const b = structuredClone(app.doc.rulers); r.off = !v; rulerHistory('자 켜기/끄기', b); }),
          button('삭제', () => { const b = structuredClone(app.doc.rulers); app.doc.rulers.splice(i, 1); rulerHistory('자 삭제', b); renderList(); }, 'danger'),
        ));
      });
      if (!app.doc.rulers.length) list.append(helpText('자가 없습니다.'));
    };
    const off = app.bus.on('rulers:changed', () => { if (!el.isConnected) { off(); return; } renderList(); });
    renderList();
    el.append(
      section('자 추가',
        row(button('평행선', () => addRuler('parallel')), button('방사선', () => addRuler('radial')), button('동심원', () => addRuler('concentric'))),
        row(button('1점 투시', () => addRuler('p1')), button('2점 투시', () => addRuler('p2')), button('3점 투시', () => addRuler('p3'))),
      ),
      section('자 목록', list),
      section('설정',
        toggle('브러시를 자에 맞추기', () => s.rulerSnap !== false, v => { s.rulerSnap = v; app.bus.emit('settings:changed'); app.saveSettingsSoon(); }),
        toggle('자 보이기', () => s.showRulers !== false, v => { s.showRulers = v; app.requestRender(); app.saveSettingsSoon(); }),
        helpText('브러시로 그리기 시작한 방향에 가장 가까운 자에 선이 붙습니다. 끄려면 퀵바의 "자" 버튼이나 이 설정을 끄세요.'),
      ),
    );
  },
};

app.addOverlay(drawRulers);
