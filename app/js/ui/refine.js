// 선택 다듬기 (매끄럽게·페더·대비·경계 이동, 바로 보기) + 머리카락 다듬기 브러시
import { app } from '../app.js';
import { createCanvas, h, rectUnion } from '../core/util.js';
import { openModal } from './dialogs.js';
import { slider, section } from './controls.js';
import { boxBlur } from '../tools/heal.js';
import { helpText } from '../tools/basic.js';
import { addIcon } from './icons.js';

addIcon('refine', '<path d="M4 20c4-1 6-5 7-9s3-7 9-7"/><path d="M8 20c3-2 4-5 5-8M12 20c2-2 3-4 4-7" opacity=".6"/>');

/**
 * 마스크 알파(0~1, 크기 w×h) 다듬기
 * o: { smooth(px), feather(px), contrast(0~1), shift(-px~+px) }
 */
export function refineAlpha(a, w, hgt, o) {
  let A = a;
  if (o.smooth > 0) { A = boxBlur(A, w, hgt, Math.round(o.smooth), 2); for (let i = 0; i < A.length; i++) A[i] = A[i] >= 0.5 ? 1 : 0; }
  if (o.shift) {
    const r = Math.max(1, Math.round(Math.abs(o.shift)));
    A = boxBlur(A, w, hgt, r, 1);
    const t = o.shift > 0 ? 0.08 : 0.92;
    for (let i = 0; i < A.length; i++) A[i] = A[i] > t ? 1 : 0;
  }
  if (o.feather > 0) A = boxBlur(A, w, hgt, Math.max(1, Math.round(o.feather / 2)), 2);
  if (o.contrast > 0) {
    const k = 1 + o.contrast * 24, lo = 1 / (1 + Math.exp(k * 0.5)), hi = 1 / (1 + Math.exp(-k * 0.5));
    for (let i = 0; i < A.length; i++) A[i] = (1 / (1 + Math.exp(-k * (A[i] - 0.5))) - lo) / (hi - lo);
  }
  return A;
}

export function refineDialog() {
  const sel = app.doc.selection;
  if (!sel.active) { app.toast('선택 영역이 없습니다'); return; }
  const orig = sel.snapshot();
  const o = app.settings.refineOpt || { smooth: 0, feather: 0, contrast: 0, shift: 0 };
  const pad = 60, b = orig.bounds;
  const R = { x: b.x - pad, y: b.y - pad, w: b.w + pad * 2, h: b.h + pad * 2 };
  const src = createCanvas(R.w, R.h);
  src.getContext('2d').drawImage(orig.canvas, pad, pad);
  const sd = src.getContext('2d').getImageData(0, 0, R.w, R.h).data;
  const base = new Float32Array(R.w * R.h);
  for (let i = 0; i < base.length; i++) base[i] = sd[i * 4 + 3] / 255;
  let timer = null;
  const preview = () => {
    clearTimeout(timer);
    timer = setTimeout(() => {
      const A = refineAlpha(base.slice(), R.w, R.h, o);
      const c = createCanvas(R.w, R.h), x = c.getContext('2d'), img = x.createImageData(R.w, R.h);
      for (let i = 0; i < A.length; i++) { img.data[i * 4] = img.data[i * 4 + 1] = img.data[i * 4 + 2] = 255; img.data[i * 4 + 3] = Math.round(Math.max(0, Math.min(1, A[i])) * 255); }
      x.putImageData(img, 0, 0);
      sel.apply(ctx => ctx.drawImage(c, R.x, R.y), 'replace', R);
    }, 90);
  };
  const body = h('div', null, section('선택 다듬기',
    slider('매끄럽게 (울퉁불퉁 정리)', { min: 0, max: 30, step: 1, get: () => o.smooth, set: v => { o.smooth = v; preview(); }, format: v => (v ? v + 'px' : '끔') }),
    slider('경계 이동 (− 안쪽 / + 바깥쪽)', { min: -30, max: 30, step: 1, get: () => o.shift, set: v => { o.shift = v; preview(); }, format: v => (v > 0 ? '+' : '') + v + 'px' }),
    slider('페더 (가장자리 흐리게)', { min: 0, max: 60, step: 1, get: () => o.feather, set: v => { o.feather = v; preview(); }, format: v => (v ? v + 'px' : '끔') }),
    slider('대비 (가장자리 또렷하게)', { min: 0, max: 1, step: 0.05, get: () => o.contrast, set: v => { o.contrast = v; preview(); }, format: v => Math.round(v * 100) + '%' }),
    helpText('그림 위 선택 영역에서 바로 확인됩니다. 머리카락·털 가장자리는 "머리카락 다듬기" 브러시(선택 메뉴)로 문지르세요.'),
  ));
  preview();
  openModal({
    title: '선택 다듬기', body,
    actions: [
      { label: '취소', run: () => { clearTimeout(timer); sel.restore(orig); } },
      { label: '적용', primary: true, run: () => {
        clearTimeout(timer);
        const A = refineAlpha(base.slice(), R.w, R.h, o);
        const c = createCanvas(R.w, R.h), x = c.getContext('2d'), img = x.createImageData(R.w, R.h);
        for (let i = 0; i < A.length; i++) { img.data[i * 4] = img.data[i * 4 + 1] = img.data[i * 4 + 2] = 255; img.data[i * 4 + 3] = Math.round(Math.max(0, Math.min(1, A[i])) * 255); }
        x.putImageData(img, 0, 0);
        sel.restore(orig);
        const before = sel.snapshot();
        sel.apply(ctx => ctx.drawImage(c, R.x, R.y), 'replace', R);
        const after = sel.snapshot();
        app.history.push({ label: '선택 다듬기', bytes: R.w * R.h * 8, undo: () => sel.restore(before), redo: () => sel.restore(after) });
        app.settings.refineOpt = { ...o };
        app.saveSettingsSoon();
      } },
    ],
    onClose: (r) => { if (r === null) { clearTimeout(timer); sel.restore(orig); } },
  });
}

/**
 * 머리카락 다듬기: 문지른 곳에서 "확실히 안(전경)·확실히 밖(배경)" 색을 찾아,
 * 각 픽셀이 그 둘 중 어느 쪽에 가까운지로 선택 정도를 다시 정한다 (간단한 색 분리 매팅).
 */
export function refinePixels(img, mask, w, cx, cy, r) {
  let fr = 0, fg = 0, fb = 0, fn = 0, br = 0, bg = 0, bb = 0, bn = 0;
  const R2 = (r * 2) ** 2;
  const hgt = mask.length / w;
  for (let y = Math.max(0, cy - 2 * r); y < Math.min(hgt, cy + 2 * r); y++) for (let x = Math.max(0, cx - 2 * r); x < Math.min(w, cx + 2 * r); x++) {
    if ((x - cx) ** 2 + (y - cy) ** 2 > R2) continue;
    const i = y * w + x, m = mask[i];
    if (m > 0.95) { fr += img[i * 4]; fg += img[i * 4 + 1]; fb += img[i * 4 + 2]; fn++; } else if (m < 0.05) { br += img[i * 4]; bg += img[i * 4 + 1]; bb += img[i * 4 + 2]; bn++; }
  }
  if (!fn || !bn) return false;
  fr /= fn; fg /= fn; fb /= fn; br /= bn; bg /= bn; bb /= bn;
  const dr = fr - br, dg = fg - bg, db = fb - bb, L2 = dr * dr + dg * dg + db * db;
  if (L2 < 300) return false;                        // 안팎 색이 거의 같으면 판단 불가
  for (let y = Math.max(0, cy - r); y < Math.min(hgt, cy + r); y++) for (let x = Math.max(0, cx - r); x < Math.min(w, cx + r); x++) {
    const d2 = (x - cx) ** 2 + (y - cy) ** 2;
    if (d2 > r * r) continue;
    const i = y * w + x;
    const t = ((img[i * 4] - br) * dr + (img[i * 4 + 1] - bg) * dg + (img[i * 4 + 2] - bb) * db) / L2;
    const a = Math.max(0, Math.min(1, (t - 0.1) / 0.8));
    const f = 1 - Math.sqrt(d2) / r, k = Math.min(1, f * 2);   // 브러시 가장자리는 조금만
    mask[i] = mask[i] * (1 - k) + a * k;
  }
  return true;
}

export const refineBrushTool = {
  id: 'refinebrush', name: '머리카락 다듬기', icon: 'refine', key: null, cursor: 'none',
  opt: { size: 40 },
  st: null,
  get busy() { return !!this.st; },
  down(e) {
    const doc = app.doc, sel = doc.selection;
    if (!sel.active) { app.toast('먼저 대략 선택한 뒤, 가장자리(머리카락·털)를 문지르세요', 'warn'); return; }
    const img = app.compositor.flatten(doc, { paper: true, background: '#ffffff' });
    this.st = { before: sel.snapshot(), img: img.getContext('2d'), dirty: null };
    this.dab(e);
  },
  move(e) { if (this.st) this.dab(e); },
  up() {
    const s = this.st;
    if (!s) return;
    this.st = null;
    const sel = app.doc.selection, after = sel.snapshot();
    app.history.push({ label: '머리카락 다듬기', bytes: 1e5, undo: () => sel.restore(s.before), redo: () => sel.restore(after) });
  },
  cancel() { if (this.st) { app.doc.selection.restore(this.st.before); this.st = null; } },
  dab(e) {
    const sel = app.doc.selection, r = Math.max(3, Math.round(this.opt.size / 2)), R = r * 2;
    const x0 = Math.max(0, Math.round(e.x) - R), y0 = Math.max(0, Math.round(e.y) - R);
    const x1 = Math.min(app.doc.width, Math.round(e.x) + R), y1 = Math.min(app.doc.height, Math.round(e.y) + R);
    const w = x1 - x0, hh = y1 - y0;
    if (w <= 0 || hh <= 0) return;
    const mctx = sel.mask.getContext('2d');
    const md = mctx.getImageData(x0, y0, w, hh), img = this.st.img.getImageData(x0, y0, w, hh).data;
    const m = new Float32Array(w * hh);
    for (let i = 0; i < m.length; i++) m[i] = md.data[i * 4 + 3] / 255;
    if (!refinePixels(img, m, w, Math.round(e.x) - x0, Math.round(e.y) - y0, r)) return;
    for (let i = 0; i < m.length; i++) { md.data[i * 4] = md.data[i * 4 + 1] = md.data[i * 4 + 2] = 255; md.data[i * 4 + 3] = Math.round(m[i] * 255); }
    mctx.putImageData(md, x0, y0);
    sel._recompute(rectUnion(sel.bounds, { x: x0, y: y0, w, h: hh }));
    sel.paths = null;
    sel._changed();
  },
  overlay(ctx, view) {
    const pt = app.pointer;
    if (!pt.inside || pt.type === 'touch') return;
    const r = Math.max(2, this.opt.size / 2 * view.zoom);
    ctx.lineWidth = 1;
    ctx.strokeStyle = 'rgba(232,163,61,.9)'; ctx.beginPath(); ctx.arc(pt.sx, pt.sy, r, 0, Math.PI * 2); ctx.stroke();
    ctx.setLineDash([3, 3]); ctx.beginPath(); ctx.arc(pt.sx, pt.sy, r * 2, 0, Math.PI * 2); ctx.stroke(); ctx.setLineDash([]);
  },
  options(el) {
    const o = this.opt;
    el.append(section('머리카락 다듬기',
      slider('크기', { min: 6, max: 300, step: 1, log: true, get: () => o.size, set: v => { o.size = v; } }),
      helpText('대략 선택한 뒤 머리카락·털·흐린 가장자리를 따라 문지르세요. 브러시 둘레(점선 원)에서 확실한 안쪽 색과 바깥 색을 찾아, 그 사이 픽셀을 얼마나 선택할지 다시 정합니다.'),
    ));
  },
};

