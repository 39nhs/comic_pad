// 선택 도구: 사각형 / 타원 / 올가미 / 자동 선택
import { app } from '../app.js';
import { fillMaskAsync } from '../core/raster.js';
import { rectFromPoints } from '../core/util.js';
import { slider, segmented, section } from '../ui/controls.js';
import { referenceImage, helpText } from './basic.js';

export const selectTool = {
  id: 'select', name: '선택', icon: 'select', key: 'm', cursor: 'crosshair', allowAltPick: false,
  opt: { shape: 'rect', op: 'replace', source: 'all', tolerance: 12, gap: 0, expand: 0, keepRatio: false },
  drag: null,

  _op(e) {
    if (e.shift && e.alt) return 'intersect';
    if (e.shift) return 'add';
    if (e.alt) return 'subtract';
    return this.opt.op;
  },

  down(e) {
    const op = this._op(e);
    if (this.opt.shape === 'wand') {
      const doc = app.doc;
      const x = Math.floor(e.x), y = Math.floor(e.y);
      if (x < 0 || y < 0 || x >= doc.width || y >= doc.height) return;
      app.task('자동 선택', async () => {
        const { mask, bounds } = await fillMaskAsync(referenceImage(this.opt.source), x, y, this.opt);
        if (bounds && app.doc === doc) selectionHistory(() => doc.selection.fromMask(mask, bounds, op));
      }, { delay: 250 });
      return;
    }
    this.drag = { op, x0: e.x, y0: e.y, sx0: e.sx, sy0: e.sy, x1: e.x, y1: e.y, pts: [{ x: e.x, y: e.y }], moved: false };
    this.busy = true;
  },

  move(e) {
    const d = this.drag;
    if (!d) return;
    if (Math.hypot(e.sx - d.sx0, e.sy - d.sy0) > 4) d.moved = true;
    let { x, y } = e;
    if ((this.opt.keepRatio || e.shift && this.opt.shape !== 'lasso') && this.opt.shape !== 'lasso') {
      const s = Math.max(Math.abs(x - d.x0), Math.abs(y - d.y0));
      x = d.x0 + Math.sign(x - d.x0 || 1) * s; y = d.y0 + Math.sign(y - d.y0 || 1) * s;
    }
    d.x1 = x; d.y1 = y;
    if (this.opt.shape === 'lasso') {
      const last = d.pts[d.pts.length - 1];
      if (Math.hypot(e.x - last.x, e.y - last.y) * app.view.zoom > 2) d.pts.push({ x: e.x, y: e.y });
    }
    app.requestRender();
  },

  up() {
    const d = this.drag;
    this.drag = null;
    this.busy = false;
    if (!d) return;
    const sel = app.doc.selection;
    if (!d.moved) {
      if (d.op === 'replace' && sel.active) selectionHistory(() => sel.clear());
      app.requestRender();
      return;
    }
    if (this.opt.shape === 'lasso') {
      selectionHistory(() => sel.polygon(d.pts, d.op));
    } else {
      const r = rectFromPoints(d.x0, d.y0, d.x1, d.y1);
      if (r.w < 1 || r.h < 1) return;
      if (this.opt.shape === 'ellipse') selectionHistory(() => sel.ellipse(r.x, r.y, r.w, r.h, d.op));
      else selectionHistory(() => sel.rect(Math.round(r.x), Math.round(r.y), Math.round(r.w), Math.round(r.h), d.op));
    }
  },

  cancel() { this.drag = null; this.busy = false; app.requestRender(); },

  overlay(ctx, view) {
    const d = this.drag;
    if (!d || !d.moved) return;
    ctx.lineWidth = 1.2;
    ctx.beginPath();
    if (this.opt.shape === 'lasso') {
      d.pts.forEach((p, i) => { const s = view.toScreen(p.x, p.y); if (i) ctx.lineTo(s.x, s.y); else ctx.moveTo(s.x, s.y); });
      ctx.closePath();
    } else {
      const r = rectFromPoints(d.x0, d.y0, d.x1, d.y1);
      if (this.opt.shape === 'ellipse') {
        const n = 64;
        for (let i = 0; i <= n; i++) {
          const a = i / n * Math.PI * 2;
          const s = view.toScreen(r.x + r.w / 2 + Math.cos(a) * r.w / 2, r.y + r.h / 2 + Math.sin(a) * r.h / 2);
          if (i) ctx.lineTo(s.x, s.y); else ctx.moveTo(s.x, s.y);
        }
      } else {
        [[r.x, r.y], [r.x + r.w, r.y], [r.x + r.w, r.y + r.h], [r.x, r.y + r.h]].forEach(([x, y], i) => {
          const s = view.toScreen(x, y);
          if (i) ctx.lineTo(s.x, s.y); else ctx.moveTo(s.x, s.y);
        });
        ctx.closePath();
      }
    }
    ctx.fillStyle = 'rgba(60,130,255,0.12)';
    ctx.fill();
    ctx.strokeStyle = '#000'; ctx.setLineDash([4, 4]); ctx.stroke();
    ctx.strokeStyle = '#fff'; ctx.lineDashOffset = 4; ctx.stroke();
  },

  options(el) {
    const o = this.opt;
    const body = document.createElement('div');
    const rebuild = () => {
      body.innerHTML = '';
      body.append(section('선택 방식',
        segmented([['rect', '사각형'], ['ellipse', '타원'], ['lasso', '올가미'], ['wand', '자동 선택']], () => o.shape, v => { o.shape = v; rebuild(); }),
        segmented([['replace', '새로'], ['add', '추가'], ['subtract', '빼기'], ['intersect', '교차']], () => o.op, v => { o.op = v; }),
        o.shape === 'wand' ? segmented([['all', '모든 레이어'], ['ref', '참조 레이어'], ['layer', '현재 레이어']], () => o.source, v => { o.source = v; }) : null,
        o.shape === 'wand' ? slider('색 허용 오차', { min: 0, max: 255, get: () => o.tolerance, set: v => { o.tolerance = v; } }) : null,
        o.shape === 'wand' ? slider('틈 막기 (이 너비까지 빈틈은 선으로)', { min: 0, max: 50, get: () => o.gap, set: v => { o.gap = v; }, format: v => (v ? v + 'px' : '끔') }) : null,
        o.shape === 'wand' ? slider('영역 확장', { min: 0, max: 10, get: () => o.expand, set: v => { o.expand = v; }, format: v => v + 'px' }) : null,
        helpText('Shift: 추가 · Alt: 빼기 · 빈 곳 탭: 선택 해제. 선택하면 아래에 선택 메뉴가 나타납니다.'),
      ));
    };
    rebuild();
    el.append(body);
  },
};

/** 선택 변경을 실행취소 가능하게 */
export function selectionHistory(fn, label = '선택 영역') {
  const sel = app.doc.selection;
  const before = sel.snapshot();
  fn();
  const after = sel.snapshot();
  app.history.push({
    label,
    bytes: (before ? before.bounds.w * before.bounds.h * 4 : 0) + (after ? after.bounds.w * after.bounds.h * 4 : 0),
    undo: () => sel.restore(before),
    redo: () => sel.restore(after),
  });
}
