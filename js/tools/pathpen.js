// 펜 도구 (베지어 패스): 탭 = 꺾인 점, 끌기 = 부드러운 점(손잡이). 패스 → 선택 영역 / 선 그리기 / 칠하기
import { app } from '../app.js';
import { uid } from '../core/util.js';
import { section, button, row } from '../ui/controls.js';
import { h } from '../core/util.js';
import { helpText } from './basic.js';
import { addIcon } from '../ui/icons.js';
import { checkEditable } from '../core/ops.js';
import { Stroke, commitBuffer, getStrokeBuffer } from '../core/brush.js';
import { activePreset } from './brush.js';

addIcon('pathpen', '<path d="M12 3l4 9-4 9-4-9z"/><circle cx="12" cy="12" r="1.6"/><path d="M4 21h16"/>');

const paths = (doc = app.doc) => (doc.paths ||= []);

/** 패스 → Path2D (문서 좌표) */
export function pathToPath2D(p) {
  const P = new Path2D(), a = p.pts;
  if (!a.length) return P;
  P.moveTo(a[0].x, a[0].y);
  const seg = (q, r) => P.bezierCurveTo(q.ox ?? q.x, q.oy ?? q.y, r.ix ?? r.x, r.iy ?? r.y, r.x, r.y);
  for (let i = 1; i < a.length; i++) seg(a[i - 1], a[i]);
  if (p.closed && a.length > 2) { seg(a[a.length - 1], a[0]); P.closePath(); }
  return P;
}

/** 패스를 따라 촘촘한 점들 (간격 step px) */
export function samplePath(p, step = 2) {
  const a = p.pts, out = [];
  if (!a.length) return out;
  const segs = [];
  for (let i = 1; i < a.length; i++) segs.push([a[i - 1], a[i]]);
  if (p.closed && a.length > 2) segs.push([a[a.length - 1], a[0]]);
  if (!segs.length) return [{ x: a[0].x, y: a[0].y }];
  for (const [q, r] of segs) {
    const P0 = [q.x, q.y], P1 = [q.ox ?? q.x, q.oy ?? q.y], P2 = [r.ix ?? r.x, r.iy ?? r.y], P3 = [r.x, r.y];
    const len = Math.hypot(P1[0] - P0[0], P1[1] - P0[1]) + Math.hypot(P2[0] - P1[0], P2[1] - P1[1]) + Math.hypot(P3[0] - P2[0], P3[1] - P2[1]);
    const n = Math.max(2, Math.ceil(len / step));
    for (let k = out.length ? 1 : 0; k <= n; k++) {
      const t = k / n, u = 1 - t;
      out.push({ x: u * u * u * P0[0] + 3 * u * u * t * P1[0] + 3 * u * t * t * P2[0] + t * t * t * P3[0], y: u * u * u * P0[1] + 3 * u * u * t * P1[1] + 3 * u * t * t * P2[1] + t * t * t * P3[1] });
    }
  }
  return out;
}

/** 실행취소 가능하게 패스 목록 바꾸기 */
function editPaths(label, fn) {
  const doc = app.doc, before = structuredClone(paths(doc));
  fn(paths(doc));
  const after = structuredClone(paths(doc));
  app.history.push({ label, bytes: 200, undo: () => { doc.paths = structuredClone(before); app.requestRender(); app.bus.emit('paths:changed'); }, redo: () => { doc.paths = structuredClone(after); app.requestRender(); app.bus.emit('paths:changed'); } });
  app.bus.emit('paths:changed');
  app.requestRender();
}

// ---------------------------------------------------------------------
// 패스 명령
// ---------------------------------------------------------------------
export function pathToSelection(p, op = 'replace') {
  if (!p || p.pts.length < 2) { app.toast('점이 2개 이상인 패스가 필요합니다'); return; }
  const P = pathToPath2D({ ...p, closed: true });
  const pts = samplePath({ ...p, closed: true }, 4);
  let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
  for (const q of pts) { x0 = Math.min(x0, q.x); y0 = Math.min(y0, q.y); x1 = Math.max(x1, q.x); y1 = Math.max(y1, q.y); }
  app.doc.selection.apply(ctx => ctx.fill(P), op, { x: Math.floor(x0), y: Math.floor(y0), w: Math.ceil(x1 - x0) + 2, h: Math.ceil(y1 - y0) + 2 }, [pts]);
}

/** 지금 브러시로 패스 따라 그리기 (필압: 시작·끝을 가늘게) */
export function strokePath(p, { taper = true } = {}) {
  const doc = app.doc, owner = doc.active, layer = owner?.target;
  if (!p || p.pts.length < 2) { app.toast('점이 2개 이상인 패스가 필요합니다'); return; }
  if (!checkEditable(layer)) return;
  const pre = { ...activePreset('brush'), stabilizer: 0 };
  const buf = getStrokeBuffer(doc);
  const st = new Stroke({ canvas: buf, preset: pre, color: pre.color || app.color.fg, doc });
  const pts = samplePath(p, Math.max(1, pre.size * 0.15));
  const n = pts.length;
  pts.forEach((q, i) => {
    const t = i / Math.max(1, n - 1), pr = taper ? Math.min(1, Math.sin(Math.PI * t) * 1.6 + 0.05) : 1;
    const pt = { x: q.x, y: q.y, p: pr, t: i * 8 };
    if (!i) st.begin(pt); else st.add(pt);
  });
  const r = st.end({ ...pts[n - 1], p: taper ? 0.05 : 1, t: n * 8 });
  if (!r) return;
  const tok = app.history.beginPixels(layer);
  commitBuffer(layer, buf, r, 'paint', pre.opacity, pre.blend || 'source-over', pre);
  tok.commit(r, '패스 선 그리기');
  layer.touch();
  app.requestRender();
}

export function fillPath(p, color = app.color.fg) {
  const layer = app.doc.active?.target;
  if (!p || p.pts.length < 3) { app.toast('점이 3개 이상인 패스가 필요합니다'); return; }
  if (!checkEditable(layer)) return;
  app.history.pixels(layer, '패스 칠하기', (ctx) => { ctx.save(); ctx.fillStyle = color; ctx.fill(pathToPath2D({ ...p, closed: true })); ctx.restore(); });
  app.requestRender();
}

/** 선택 영역 테두리 → 패스 (마스크 윤곽을 따라가서 점을 줄이고 부드러운 곡선으로) */
export function selectionToPath(tolerance = 2) {
  const sel = app.doc.selection;
  if (!sel.active) { app.toast('선택 영역이 없습니다'); return null; }
  const b = sel.bounds, W = b.w + 2, H = b.h + 2;
  const d = sel.mask.getContext('2d').getImageData(b.x - 1, b.y - 1, W, H).data;
  const inside = (x, y) => x >= 0 && y >= 0 && x < W && y < H && d[(y * W + x) * 4 + 3] > 127;
  // 가장 바깥 윤곽 하나: 맨 위 왼쪽 안쪽 칸에서 시작하는 테두리 따라가기 (Moore 이웃)
  let sx = -1, sy = -1;
  outer: for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) if (inside(x, y)) { sx = x; sy = y; break outer; }
  if (sx < 0) return null;
  const dirs = [[1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1], [1, -1]];
  const contour = [[sx, sy]];
  let cx = sx, cy = sy, dir = 6, guard = 0;
  do {
    let found = false;
    for (let k = 0; k < 8; k++) {
      const nd = (dir + 6 + k) % 8, nx = cx + dirs[nd][0], ny = cy + dirs[nd][1];
      if (inside(nx, ny)) { cx = nx; cy = ny; dir = nd; found = true; break; }
    }
    if (!found) break;
    contour.push([cx, cy]);
  } while ((cx !== sx || cy !== sy) && guard++ < W * H * 4);
  // 점 줄이기 (Ramer–Douglas–Peucker)
  const rdp = (pts, eps) => {
    if (pts.length < 3) return pts;
    const [ax, ay] = pts[0], [bx, by] = pts[pts.length - 1];
    let md = 0, mi = 0;
    for (let i = 1; i < pts.length - 1; i++) {
      const [px, py] = pts[i], L = Math.hypot(bx - ax, by - ay) || 1;
      const dd = Math.abs((bx - ax) * (ay - py) - (ax - px) * (by - ay)) / L;
      if (dd > md) { md = dd; mi = i; }
    }
    return md > eps ? [...rdp(pts.slice(0, mi + 1), eps).slice(0, -1), ...rdp(pts.slice(mi), eps)] : [pts[0], pts[pts.length - 1]];
  };
  // 닫힌 윤곽은 시작점에서 가장 먼 점으로 둘로 나눠 줄이기 (양 끝이 같으면 직선 거리가 0 이라)
  let far = 0, fd = -1;
  contour.forEach(([x, y], i) => { const d2 = (x - sx) ** 2 + (y - sy) ** 2; if (d2 > fd) { fd = d2; far = i; } });
  let simp = far > 0 ? [...rdp(contour.slice(0, far + 1), tolerance).slice(0, -1), ...rdp(contour.slice(far), tolerance)] : contour;
  if (simp.length > 2 && simp[0][0] === simp[simp.length - 1][0] && simp[0][1] === simp[simp.length - 1][1]) simp = simp.slice(0, -1);
  // 캣멀-롬 → 베지어 손잡이
  const n = simp.length, pts = simp.map(([x, y]) => ({ x: x + b.x - 0.5, y: y + b.y - 0.5 }));
  for (let i = 0; i < n; i++) {
    const p0 = pts[(i - 1 + n) % n], p1 = pts[i], p2 = pts[(i + 1) % n];
    // 많이 꺾이는 곳(뾰족한 모서리)은 손잡이 없이 꺾인 점으로
    const a1 = Math.atan2(p1.y - p0.y, p1.x - p0.x), a2 = Math.atan2(p2.y - p1.y, p2.x - p1.x);
    let turn = Math.abs(a2 - a1); if (turn > Math.PI) turn = 2 * Math.PI - turn;
    if (turn > 0.9) { Object.assign(p1, { ix: p1.x, iy: p1.y, ox: p1.x, oy: p1.y }); continue; }
    const tx = (p2.x - p0.x) / 6, ty = (p2.y - p0.y) / 6;
    Object.assign(p1, { ix: p1.x - tx, iy: p1.y - ty, ox: p1.x + tx, oy: p1.y + ty });
  }
  const p = { id: uid('pa'), name: `패스 ${paths().length + 1}`, closed: true, pts };
  editPaths('선택 영역 → 패스', (list) => list.push(p));
  pathPenTool.cur = p.id;
  return p;
}

// ---------------------------------------------------------------------
// 도구
// ---------------------------------------------------------------------
export const pathPenTool = {
  id: 'pathpen', name: '펜 (패스)', icon: 'pathpen', key: null, cursor: 'crosshair',
  cur: null, drag: null, lastTap: null,
  get busy() { return !!this.drag; },
  get path() { return paths().find(p => p.id === this.cur) || null; },

  _hit(e) {
    const p = this.path;
    if (!p) return null;
    const v = app.view, near = (x, y) => { const s = v.toScreen(x, y); return Math.hypot(s.x - e.sx, s.y - e.sy) < 9; };
    for (let i = 0; i < p.pts.length; i++) {
      const q = p.pts[i];
      if (q.ox != null && (q.ox !== q.x || q.oy !== q.y) && near(q.ox, q.oy)) return { kind: 'out', i };
      if (q.ix != null && (q.ix !== q.x || q.iy !== q.y) && near(q.ix, q.iy)) return { kind: 'in', i };
    }
    for (let i = 0; i < p.pts.length; i++) if (near(p.pts[i].x, p.pts[i].y)) return { kind: 'anchor', i };
    return null;
  },

  down(e) {
    const hit = this._hit(e), now = performance.now();
    this.before = structuredClone(paths());
    let p = this.path;
    if (hit?.kind === 'anchor') {
      const dbl = this.lastTap && this.lastTap.i === hit.i && now - this.lastTap.t < 350;
      this.lastTap = { i: hit.i, t: now };
      if (e.alt) { p.pts.splice(hit.i, 1); this._commit('패스 점 지우기'); return; }
      if (hit.i === 0 && !p.closed && p.pts.length > 2) { p.closed = true; this._commit('패스 닫기'); this.cur = p.id; return; }
      if (dbl) {
        const q = p.pts[hit.i];
        if (q.ox === q.x && q.oy === q.y) {     // 꺾인 점 → 부드러운 점
          const a = p.pts[hit.i - 1] || p.pts[p.pts.length - 1], c = p.pts[hit.i + 1] || p.pts[0];
          const tx = (c.x - a.x) / 6, ty = (c.y - a.y) / 6;
          Object.assign(q, { ix: q.x - tx, iy: q.y - ty, ox: q.x + tx, oy: q.y + ty });
        } else Object.assign(q, { ix: q.x, iy: q.y, ox: q.x, oy: q.y });
        this._commit('점 모양 바꾸기');
        return;
      }
      this.drag = { ...hit, x0: e.x, y0: e.y, orig: structuredClone(p.pts[hit.i]) };
      return;
    }
    if (hit) { this.drag = { ...hit }; return; }
    // 새 점
    if (!p || p.closed) {
      p = { id: uid('pa'), name: `패스 ${paths().length + 1}`, closed: false, pts: [] };
      paths().push(p);
      this.cur = p.id;
    }
    p.pts.push({ x: e.x, y: e.y, ix: e.x, iy: e.y, ox: e.x, oy: e.y });
    this.drag = { kind: 'new', i: p.pts.length - 1 };
    app.requestRender();
  },

  move(e) {
    const d = this.drag, p = this.path;
    if (!d || !p) return;
    const q = p.pts[d.i];
    if (d.kind === 'anchor') {
      const dx = e.x - d.x0, dy = e.y - d.y0, o = d.orig;
      Object.assign(q, { x: o.x + dx, y: o.y + dy, ix: o.ix + dx, iy: o.iy + dy, ox: o.ox + dx, oy: o.oy + dy });
    } else if (d.kind === 'new' || d.kind === 'out') {
      q.ox = e.x; q.oy = e.y;
      if (!e.alt) { q.ix = 2 * q.x - e.x; q.iy = 2 * q.y - e.y; }
    } else if (d.kind === 'in') {
      q.ix = e.x; q.iy = e.y;
      if (!e.alt) { q.ox = 2 * q.x - e.x; q.oy = 2 * q.y - e.y; }
    }
    app.requestRender();
  },

  up() {
    if (!this.drag) return;
    const k = this.drag.kind;
    this.drag = null;
    this._commit(k === 'new' ? '패스 점 추가' : '패스 고치기');
  },

  _commit(label) {
    const doc = app.doc, before = this.before || [], after = structuredClone(paths());
    this.before = null;
    if (JSON.stringify(before) === JSON.stringify(after)) return;
    app.history.push({ label, bytes: 200, undo: () => { doc.paths = structuredClone(before); app.requestRender(); app.bus.emit('paths:changed'); }, redo: () => { doc.paths = structuredClone(after); app.requestRender(); app.bus.emit('paths:changed'); } });
    app.bus.emit('paths:changed');
    app.requestRender();
  },

  cancel() { this.drag = null; },
  onKey(e) {
    if (e.key === 'Enter' || e.key === 'Escape') { this.cur = null; app.requestRender(); app.bus.emit('paths:changed'); return true; }
    if ((e.key === 'Delete' || e.key === 'Backspace') && this.path) { const id = this.cur; editPaths('패스 지우기', (l) => l.splice(l.findIndex(x => x.id === id), 1)); this.cur = null; return true; }
    return false;
  },

  overlay(ctx, view) {
    for (const p of paths()) {
      const on = p.id === this.cur;
      ctx.beginPath();
      const pts = samplePath(p, 3 / Math.max(0.05, view.zoom));
      pts.forEach((q, i) => { const s = view.toScreen(q.x, q.y); if (i) ctx.lineTo(s.x, s.y); else ctx.moveTo(s.x, s.y); });
      if (p.closed) ctx.closePath();
      ctx.lineWidth = on ? 2 : 1.2;
      ctx.strokeStyle = '#fff'; ctx.globalAlpha = 0.8; ctx.stroke();
      ctx.setLineDash(on ? [] : [4, 3]); ctx.strokeStyle = on ? '#2f7cf6' : '#666'; ctx.globalAlpha = 1; ctx.lineWidth = on ? 1.4 : 1; ctx.stroke(); ctx.setLineDash([]);
      if (!on) continue;
      for (const q of p.pts) {
        const s = view.toScreen(q.x, q.y);
        for (const [hx, hy] of [[q.ix, q.iy], [q.ox, q.oy]]) {
          if (hx == null || (hx === q.x && hy === q.y)) continue;
          const t = view.toScreen(hx, hy);
          ctx.strokeStyle = '#2f7cf6'; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(s.x, s.y); ctx.lineTo(t.x, t.y); ctx.stroke();
          ctx.fillStyle = '#fff'; ctx.beginPath(); ctx.arc(t.x, t.y, 3.5, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
        }
        ctx.fillStyle = '#2f7cf6'; ctx.fillRect(s.x - 4, s.y - 4, 8, 8);
        ctx.strokeStyle = '#fff'; ctx.strokeRect(s.x - 4, s.y - 4, 8, 8);
      }
    }
  },

  options(el) {
    const box = h('div');
    const render = () => {
      box.innerHTML = '';
      const list = h('div.path-list');
      for (const p of paths()) {
        list.append(h(`button.preset${p.id === this.cur ? '.on' : ''}`, { type: 'button', onclick: () => { this.cur = p.id; render(); app.requestRender(); } },
          `${p.name} (점 ${p.pts.length}${p.closed ? ', 닫힘' : ''})`));
      }
      if (!paths().length) list.append(helpText('아직 패스가 없습니다. 그림 위를 탭(꺾인 점)하거나 끌어(곡선) 점을 찍으세요.'));
      const P = () => this.path || (app.toast('패스를 먼저 고르세요'), null);
      box.append(section('패스', list,
        row(button('새 패스', () => { this.cur = null; render(); }), button('이름', () => { const p = P(); if (!p) return; const n = prompt('패스 이름', p.name); if (n) editPaths('패스 이름', () => { p.name = n; }); }),
          button('지우기', () => { const id = this.cur; if (!id) return; editPaths('패스 지우기', (l) => l.splice(l.findIndex(x => x.id === id), 1)); this.cur = null; })),
        row(button('선택 영역으로', () => pathToSelection(P())), button('선택에 더하기', () => pathToSelection(P(), 'add')), button('선택 → 패스', () => { selectionToPath(); render(); })),
        row(button('선 그리기 (지금 브러시)', () => strokePath(P())), button('칠하기 (전경색)', () => fillPath(P()))),
        helpText('탭 = 꺾인 점, 끌기 = 곡선 점. 첫 점을 탭하면 닫힘. 점 끌기 = 옮기기, 손잡이 끌기 = 곡선 (Alt = 한쪽만). 점 두 번 탭 = 꺾임↔곡선, Alt+탭 = 점 지우기, Enter = 이 패스 끝내기.'),
      ));
    };
    render();
    app.bus.on('paths:changed', () => { if (box.isConnected) render(); });
    el.append(box);
  },
};

export function initPaths() {
  const C = (id, label, run) => app.registerCommand(id, { label, run });
  C('path.toSelection', '패스 → 선택 영역', () => pathToSelection(pathPenTool.path || paths().at(-1)));
  C('path.fromSelection', '선택 영역 → 패스', () => selectionToPath());
  C('path.stroke', '패스 따라 선 그리기 (지금 브러시)', () => strokePath(pathPenTool.path || paths().at(-1)));
  C('path.fill', '패스 칠하기 (전경색)', () => fillPath(pathPenTool.path || paths().at(-1)));
  C('path.tool', '펜 도구 (베지어 패스)', () => app.setTool('pathpen'));
  for (const it of [{ sep: true }, { command: 'path.tool' }, { command: 'path.fromSelection' }, { command: 'path.toSelection' }]) app.addMenuItem('select', it);
}
