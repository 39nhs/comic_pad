// 벡터 선 고르기: 벡터 레이어의 선을 탭해서 골라 옮기기·다듬기·굵기·색·삭제
import { app } from '../app.js';
import { strokeAt, renderVector, setStrokes, smoothPts, editStrokes } from '../core/vector.js';
import { section, slider, button, row } from '../ui/controls.js';
import { helpText } from './basic.js';
import { addIcon } from '../ui/icons.js';

addIcon('vsel', '<path d="M4 4l7 16 2-7 7-2z"/><path d="M14 18c2-1 4-1 6 1" stroke-dasharray="2 2"/>');

const layerOf = () => { const l = app.doc?.active; return l?.kind === 'vector' ? l : null; };

export const vectorSelectTool = {
  id: 'vselect', name: '벡터 선 고르기', icon: 'vsel', key: 'o', cursor: 'default',
  sel: new Set(),     // 고른 선 번호
  drag: null,
  get busy() { return !!this.drag; },

  activate() { this.sel = new Set(); },
  deactivate() { this.sel = new Set(); this.drag = null; app.requestRender(); },

  down(e) {
    const l = layerOf();
    if (!l) { app.toast('벡터 레이어를 고르세요'); return; }
    // 선 하나만 골랐으면 조절점을 잡아 모양 고치기 (가까운 점들도 부드럽게 따라옴)
    if (this.sel.size === 1) {
      const si = [...this.sel][0], s = l.meta.strokes[si];
      // 가장 가까운 점 (축소해 보면 점끼리 붙어 있으므로 처음 걸린 점이 아니라 제일 가까운 점)
      let k = null, best = 11;
      if (s) for (const i of this._handles(s)) { const q = app.view.toScreen(s.pts[i * 4], s.pts[i * 4 + 1]); const d = Math.hypot(q.x - e.sx, q.y - e.sy); if (d <= best) { best = d; k = i; } }
      if (k != null) {
        this.drag = { l, point: k, si, x0: e.x, y0: e.y, sx0: e.sx, sy0: e.sy, moved: false, before: structuredClone(l.meta.strokes) };
        return;
      }
    }
    const tol = 8 / (app.view.zoom || 1);
    const hit = strokeAt(l, e.x, e.y, tol);
    if (!hit) { if (!e.shift) this.sel = new Set(); app.bus.emit('tool:options'); app.requestRender(); return; }
    if (e.shift) { if (this.sel.has(hit.index)) this.sel.delete(hit.index); else this.sel.add(hit.index); }
    else if (!this.sel.has(hit.index)) this.sel = new Set([hit.index]);
    this.drag = { l, x0: e.x, y0: e.y, sx0: e.sx, sy0: e.sy, moved: false, before: structuredClone(l.meta.strokes) };
    app.bus.emit('tool:options');
    app.requestRender();
  },

  move(e) {
    const d = this.drag;
    if (!d) return;
    if (Math.hypot(e.sx - d.sx0, e.sy - d.sy0) > 4) d.moved = true;
    if (!d.moved) return;
    const dx = e.x - d.x0, dy = e.y - d.y0;
    if (d.point != null) {
      const src = d.before[d.si], p = src.pts.slice(), n = p.length / 4;
      const R = Math.max(2, (this.reach ?? 0.12) * n);   // 몇 점까지 같이 움직일지
      for (let j = 0; j < n; j++) {
        const w = Math.exp(-(((j - d.point) / R) ** 2));
        if (w < 0.01) continue;
        p[j * 4] += dx * w; p[j * 4 + 1] += dy * w;
      }
      d.l.meta.strokes = d.before.map((s, i) => (i === d.si ? { ...s, pts: p } : s));
      if (!d.q) { d.q = true; requestAnimationFrame(() => { d.q = false; renderVector(d.l); app.requestRender(); }); }
      return;
    }
    d.l.meta.strokes = d.before.map((s, i) => {
      if (!this.sel.has(i)) return s;
      const p = s.pts.slice();
      for (let k = 0; k < p.length; k += 4) { p[k] += dx; p[k + 1] += dy; }
      return { ...s, pts: p };
    });
    if (!d.q) { d.q = true; requestAnimationFrame(() => { d.q = false; renderVector(d.l); app.requestRender(); }); }
  },

  up() {
    const d = this.drag;
    this.drag = null;
    if (!d?.moved) return;
    renderVector(d.l);
    setStrokes(d.l, d.l.meta.strokes, d.point != null ? '벡터 점 옮기기' : '벡터 선 이동', d.before);
  },

  /** 화면에 보일 조절점 번호 (점이 너무 많으면 골고루 추림) */
  _handles(s) {
    const n = s.pts.length / 4, step = Math.max(1, Math.ceil(n / 24));
    const out = [];
    for (let i = 0; i < n; i += step) out.push(i);
    if (out.at(-1) !== n - 1) out.push(n - 1);
    return out;
  },

  cancel() { const d = this.drag; this.drag = null; if (d) { d.l.meta.strokes = d.before; renderVector(d.l); } },

  onKey(e) {
    if ((e.key === 'Delete' || e.key === 'Backspace') && this.sel.size) { this.apply(() => null, '벡터 선 삭제'); return true; }
    if (e.key === 'Escape' && this.sel.size) { this.sel = new Set(); app.requestRender(); app.bus.emit('tool:options'); return true; }
    return false;
  },

  apply(fn, label) {
    const l = layerOf();
    if (!l || !this.sel.size) { app.toast('먼저 선을 고르세요'); return; }
    const deleting = fn({ pts: [0, 0, 0, 0], preset: {} }) === null;
    editStrokes(l, [...this.sel], fn, label);
    if (deleting) this.sel = new Set();
    app.bus.emit('tool:options');
    app.requestRender();
  },

  overlay(ctx, view) {
    const l = layerOf();
    if (!l || !this.sel.size) return;
    ctx.strokeStyle = '#2f7cf6';
    ctx.lineWidth = 2;
    ctx.setLineDash([5, 3]);
    for (const i of this.sel) {
      const s = l.meta.strokes[i];
      if (!s) continue;
      ctx.beginPath();
      for (let k = 0; k < s.pts.length; k += 4) { const q = view.toScreen(s.pts[k], s.pts[k + 1]); if (k) ctx.lineTo(q.x, q.y); else ctx.moveTo(q.x, q.y); }
      ctx.stroke();
    }
    if (this.sel.size === 1) {
      const s = l.meta.strokes[[...this.sel][0]];
      if (!s) return;
      ctx.setLineDash([]);
      ctx.fillStyle = '#fff';
      for (const i of this._handles(s)) {
        const q = view.toScreen(s.pts[i * 4], s.pts[i * 4 + 1]);
        ctx.beginPath(); ctx.arc(q.x, q.y, 4.5, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
      }
    }
  },

  options(el) {
    const n = this.sel.size;
    let k = 1;
    el.append(section(n ? `고른 선 ${n}개` : '벡터 선 고르기',
      n ? row(
        button('다듬기', () => this.apply(s => ({ ...s, pts: smoothPts(s.pts, 2) }), '선 다듬기')),
        button('많이 다듬기', () => this.apply(s => ({ ...s, pts: smoothPts(s.pts, 6) }), '선 다듬기')),
        button('지금 색으로', () => this.apply(s => ({ ...s, color: app.color.fg }), '선 색')),
        button('삭제', () => this.apply(() => null, '벡터 선 삭제')),
      ) : null,
      n ? slider('굵기 배율 (손을 떼면 적용)', { min: 0.2, max: 3, step: 0.05, get: () => k, set: v => { k = v; }, onEnd: () => { const f = k; k = 1; this.apply(s => ({ ...s, preset: { ...s.preset, size: Math.max(0.3, s.preset.size * f) } }), '선 굵기'); }, format: v => Math.round(v * 100) + '%' }) : null,
      n ? row(button('모두 고르기', () => { const l = layerOf(); if (l) { this.sel = new Set(l.meta.strokes.map((_, i) => i)); app.bus.emit('tool:options'); app.requestRender(); } }), button('선택 해제', () => { this.sel = new Set(); app.bus.emit('tool:options'); app.requestRender(); })) : null,
      n === 1 ? slider('점을 끌 때 같이 움직이는 범위', { min: 0.02, max: 0.5, step: 0.01, get: () => this.reach ?? 0.12, set: v => { this.reach = v; }, format: v => Math.round(v * 100) + '%' }) : null,
      helpText('벡터 레이어에서 선을 탭: 고르기 · Shift+탭: 여러 개 · 끌기: 이동 · 선 하나를 고르면 흰 점을 끌어 모양 고치기 · Delete: 삭제. 지우개의 "교차점까지"로 삐져나온 선 끝만 지울 수 있습니다.'),
    ));
  },
};
