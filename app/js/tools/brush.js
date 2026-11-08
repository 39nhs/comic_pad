// 브러시 / 지우개 도구
import { app } from '../app.js';
import { Stroke, commitBuffer, getStrokeBuffer, makePreset, DEFAULT_PRESETS, loadImageTip } from '../core/brush.js';
import { checkEditable } from '../core/ops.js';
import { uid } from '../core/util.js';
import { slider, toggle, select, segmented, colorInput, button, row, section } from '../ui/controls.js';
import { h } from '../core/util.js';
import { server } from '../core/io.js';
import { RulerSnap, rulersActive } from './ruler.js';
import { ROUGH_COLORS, warnNotRough, setRoughColor, defaultRoughColor } from '../core/rough.js';
import { presetSnapshot, addStroke, eraseAt, eraseToCross, renderVector, setStrokes } from '../core/vector.js';
import { presetThumb, brushTestPad, curveEditor } from '../ui/brushpreview.js';
import { nearestHex } from '../core/pixel.js';

export function presets(cat) {
  const s = app.settings;
  if (!s.brushes) s.brushes = structuredClone(DEFAULT_PRESETS);
  if (!s.brushes[cat]) s.brushes[cat] = structuredClone(DEFAULT_PRESETS[cat] || []);
  // 새 버전에서 추가된 기본 프리셋을 한 번씩 넣어 준다 (지운 건 다시 안 넣음)
  s.seenPresets = s.seenPresets || {};
  const seen = new Set(s.seenPresets[cat] || s.brushes[cat].map(p => p.id));
  for (const d of DEFAULT_PRESETS[cat] || []) {
    if (!seen.has(d.id) && !s.brushes[cat].some(p => p.id === d.id)) s.brushes[cat].push(structuredClone(d));
    seen.add(d.id);
  }
  s.seenPresets[cat] = [...seen];
  // 예전 '러프 연필(파랑)' 프리셋 → 러프 레이어 전용, 색은 레이어를 따름
  for (const p of s.brushes[cat]) if (p.id === 'rough' && p.roughOnly === undefined) { p.roughOnly = true; p.color = null; if (p.name === '러프 연필(파랑)') p.name = '러프 연필'; }
  s.brushes[cat] = s.brushes[cat].map(makePreset);
  return s.brushes[cat];
}

export function activePreset(cat) {
  const list = presets(cat);
  let p = list.find(x => x.id === app.settings.activePreset[cat]);
  if (!p) { p = list[0]; app.settings.activePreset[cat] = p?.id; }
  return p;
}

export function setActivePreset(cat, id) {
  app.settings.activePreset[cat] = id;
  const p = activePreset(cat);
  if (p.tip === 'image' && p.tipImage) loadImageTip(p.tipImage).catch(() => app.toast('브러시 팁 이미지를 불러오지 못했습니다', 'warn'));
  app.saveSettingsSoon();
  app.bus.emit('brush:changed', p);
}

export function addRecentColor(hex) {
  const r = app.settings.recentColors;
  const i = r.indexOf(hex);
  if (i === 0) return;
  if (i > 0) r.splice(i, 1);
  r.unshift(hex);
  if (r.length > 18) r.length = 18;
  app.bus.emit('recent:changed');
  app.saveSettingsSoon();
}

export class BrushTool {
  constructor({ id, name, icon, key, category, mode }) {
    Object.assign(this, { id, name, icon, key, category, mode });
    this.stroke = null;
    this.busy = false;
    this.cursor = 'none';
  }

  get preset() { return activePreset(this.category); }

  activate() {
    const p = this.preset;
    if (p.tip === 'image' && p.tipImage) loadImageTip(p.tipImage).catch(() => {});
  }

  down(e) {
    const doc = app.doc;
    const owner = doc.active;
    const layer = owner?.target;
    const p = this.preset;
    if (p.roughOnly && this.mode !== 'erase' && !owner?.rough) { warnNotRough(); return; }
    if (!checkEditable(layer, { vector: true })) return;
    this.vector = owner.kind === 'vector' && !layer.isMask;
    if (this.vector && this.mode === 'erase') {
      // 벡터 지우개: 픽셀이 아니라 선을 지운다
      this.vErase = { layer: owner, before: structuredClone(owner.meta?.strokes || []), r: p.size / 2, whole: app.settings.vectorErase === 'whole', cross: app.settings.vectorErase === 'cross', last: e };
      this.busy = true;
      this._vErase(e);
      return;
    }
    if (p.tip === 'image' && p.tipImage) loadImageTip(p.tipImage).catch(() => {});
    let color = this.mode === 'erase' || layer.isMask ? '#000000' : p.roughOnly ? (owner.tint || defaultRoughColor()) : (p.color || app.color.fg);
    if (doc.pixel?.lock && this.mode !== 'erase' && !layer.isMask) color = nearestHex(color);          // 팔레트 고정 작품
    this.buf = getStrokeBuffer(doc);
    this.layer = layer;
    this.token = app.history.beginPixels(layer);
    this.opacity = p.opacity;
    // 혼색 브러시는 시작할 때 레이어 픽셀을 한 번 읽어 둔다
    const sample = (p.mix > 0 || p.shading) && this.mode !== 'erase' ? layer.ctx.getImageData(0, 0, layer.canvas.width, layer.canvas.height) : null;
    this.stroke = new Stroke({ canvas: this.buf, preset: { ...p }, color, doc, symmetry: this.vector ? 'none' : app.settings.symmetry, sample, noClip: this.vector });
    if (this.vector) { this.stroke.rec = []; this.vColor = color; }
    this.stroke.shadeFlip = !!e.alt;                                   // 명암 잉크: Alt 를 누르고 그리면 반대로
    this._tilt(e);
    this.blend = this.mode === 'erase' || layer.isMask ? 'source-over' : (p.blend || 'source-over');
    app.live = { layer: owner, canvas: this.buf, mode: this.mode, opacity: p.opacity, onMask: !!layer.isMask, blend: this.blend, preset: this.mode === 'erase' ? null : p, rect: () => this.stroke?.total };
    this.busy = true;
    this.start = { x: e.x, y: e.y, p: e.pressure };
    this.startS = { x: e.sx, y: e.sy };
    this.lastP = e.pressure;
    this.lineMode = !!(e.shift || app.modifiers.line);
    this.snap = null;
    if (this.lineMode) this.stroke.polyline([this.start, this.start]);
    else if (rulersActive()) { this.snap = new RulerSnap(e); this.pending = [{ x: e.x, y: e.y, p: e.pressure }]; }
    else this.stroke.begin({ x: e.x, y: e.y, p: e.pressure, t: e.time });
    if (this.mode !== 'erase' && !p.color && !p.roughOnly) addRecentColor(app.color.fg);
  }

  _tilt(e) {
    if (this.stroke && (e.tiltX || e.tiltY)) {
      this.stroke.tiltAngle = Math.atan2(e.tiltY, e.tiltX) + Math.PI / 2;
      this.stroke.tiltMag = Math.min(1, Math.hypot(e.tiltX, e.tiltY) / 60);   // 0 = 세움, 1 = 많이 눕힘
    }
    // 펜을 돌리는 각도(와콤 아트 펜 등의 twist)가 오면 납작 펜 각도는 그것을 우선
    if (this.stroke && e.twist) this.stroke.tiltAngle = (e.twist * Math.PI) / 180;
  }


  _vErase(e) {
    const v = this.vErase;
    // 빠르게 움직여도 빠지지 않도록 지난 위치부터 촘촘히
    const d = Math.hypot(e.x - v.last.x, e.y - v.last.y), n = Math.max(1, Math.ceil(d / Math.max(1, v.r * 0.5)));
    let changed = false;
    for (let i = 1; i <= n; i++) {
      const x = v.last.x + (e.x - v.last.x) * i / n, y = v.last.y + (e.y - v.last.y) * i / n;
      changed = (v.cross ? eraseToCross(v.layer, x, y, v.r) : eraseAt(v.layer, x, y, v.r, v.whole)) || changed;
    }
    v.last = e;
    if (changed && !v.queued) { v.queued = true; requestAnimationFrame(() => { v.queued = false; renderVector(v.layer); }); }
  }

  move(e) {
    if (this.vErase) { this._vErase(e); return; }
    if (!this.stroke) return;
    this._tilt(e);
    if (e.pressure > 0) this.lastP = e.pressure;
    if (this.lineMode) {
      let { x, y } = e;
      if (e.ctrl || app.modifiers.snap) {
        // 15도 단위 스냅
        const a = Math.atan2(y - this.start.y, x - this.start.x);
        const s = Math.round(a / (Math.PI / 12)) * (Math.PI / 12);
        const d = Math.hypot(x - this.start.x, y - this.start.y);
        x = this.start.x + Math.cos(s) * d; y = this.start.y + Math.sin(s) * d;
      }
      this.stroke.reset();
      const p = this.preset.pressureSize ? 1 : this.lastP;
      this.stroke.polyline([{ ...this.start, p }, { x, y, p }]);
    } else if (this.snap) {
      const pt = { x: e.x, y: e.y, p: e.pressure > 0 ? e.pressure : this.lastP, t: e.time };
      if (this.snap.c) { this.stroke.add(this.snap.project(pt)); return; }
      this.pending.push(pt);
      if (Math.hypot(e.sx - this.startS.x, e.sy - this.startS.y) < 8) return;
      if (!this.snap.decide(pt)) this.snap = null;
      const proj = (q) => (this.snap ? this.snap.project(q) : q);
      this.stroke.begin(proj(this.pending[0]));
      for (const q of this.pending.slice(1)) this.stroke.add(proj(q));
      this.pending = null;
    } else {
      this.stroke.add({ x: e.x, y: e.y, p: e.pressure > 0 ? e.pressure : this.lastP, t: e.time });
      this._holdCheck(e);
    }
  }

  /** 선 끝에서 펜을 잠시 멈추고 있으면 지금까지 그린 선을 시작점→끝점 직선으로 바꾼다 (계속 움직이면 끝점 조절) */
  _holdCheck(e) {
    if (!app.settings.holdLine || this.vector) return;
    clearTimeout(this.holdTimer);
    const a = this.holdAt;
    if (!a || Math.hypot(e.sx - a.sx, e.sy - a.sy) > 4) this.holdAt = { sx: e.sx, sy: e.sy, x: e.x, y: e.y };
    this.holdTimer = setTimeout(() => {
      if (!this.stroke || this.lineMode || !this.holdAt) return;
      const end = this.holdAt;
      if (Math.hypot(end.x - this.start.x, end.y - this.start.y) * app.view.zoom < 30) return;   // 너무 짧으면 그대로
      this.stroke.ctx.restore();                                   // begin() 에서 저장한 상태 정리
      this.stroke.reset();
      this.lineMode = true;
      const pr = this.preset.pressureSize ? 1 : this.lastP;
      this.stroke.polyline([{ ...this.start, p: pr }, { x: end.x, y: end.y, p: pr }]);
      app.requestRender();
      app.toast('직선으로 바꿨습니다 (그대로 움직이면 끝점 조절, Ctrl = 15도 단위)', 'info', { duration: 2500 });
    }, 650);
  }

  up(e) {
    if (this.vErase) {
      const v = this.vErase;
      this.vErase = null;
      this.busy = false;
      const now = v.layer.meta?.strokes || [];
      if (JSON.stringify(now) !== JSON.stringify(v.before)) setStrokes(v.layer, now, '벡터 지우기', v.before);
      return;
    }
    if (!this.stroke) return;
    let last = { x: e.x, y: e.y, p: this.lastP, t: e.time };
    app.predicted = null;
    if (this.snap && !this.snap.c) { this.stroke.begin(this.pending[0]); last = this.pending[0]; }
    else if (this.snap) last = this.snap.project(last);
    const rect = this.lineMode ? this.stroke.total : this.stroke.end(last);
    if (rect) {
      commitBuffer(this.layer, this.buf, rect, this.mode, this.opacity, this.blend, this.mode === 'erase' ? null : this.stroke.p);
      if (this.vector && this.stroke.rec?.length >= 4) {
        this.token.commit(null);
        addStroke(this.layer, { pts: this.stroke.rec, preset: presetSnapshot(this.stroke.p), color: this.vColor });
      } else this.token.commit(rect, this.name);
      this.layer.touch();
    } else this.token.commit(null);
    this._reset();
  }

  cancel() {
    if (this.vErase) { const v = this.vErase; this.vErase = null; this.busy = false; v.layer.meta.strokes = v.before; renderVector(v.layer); return; }
    if (!this.stroke) return;
    const r = this.stroke.total;
    if (r) this.buf.getContext('2d').clearRect(r.x - 1, r.y - 1, r.w + 2, r.h + 2);
    this.token.commit(null);
    this._reset();
  }

  _reset() {
    clearTimeout(this.holdTimer); this.holdAt = null;
    app.predicted = null;
    this.stroke = null;
    this.busy = false;
    app.live = null;
    app.requestRender();
  }

  overlay(ctx, view) {
    // 펜 위치 예측: 실제 선보다 조금 앞을 흐리게 미리 보여 줘서 따라오는 느낌을 줄인다
    const pr = app.predicted;
    if (this.stroke && pr?.length && this.mode !== 'erase') {
      const p0 = app.pointer;
      ctx.strokeStyle = this.preset.color || (this.preset.roughOnly ? (app.doc.active?.tint || '#3d7be0') : app.color.fg);
      ctx.globalAlpha = 0.55;
      ctx.lineCap = 'round';
      ctx.lineWidth = Math.max(1, this.preset.size * view.zoom * (this.preset.pressureSize ? 0.6 : 1));
      ctx.beginPath(); ctx.moveTo(p0.sx, p0.sy);
      for (const q of pr) ctx.lineTo(q.sx, q.sy);
      ctx.stroke();
      ctx.globalAlpha = 1;
    }
    // 끈 당기기·무거운 펜: 펜과 선 끝을 잇는 끈
    const st = this.stroke;
    if (st?.lz && st.lzPen && !this.lineMode) {
      const a = view.toScreen(st.lz.x, st.lz.y), b = view.toScreen(st.lzPen.x, st.lzPen.y);
      ctx.save();
      ctx.strokeStyle = 'rgba(232,163,61,.9)'; ctx.lineWidth = 1.5; ctx.setLineDash([4, 3]);
      ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke(); ctx.setLineDash([]);
      if (st.stabMode === 'lazy') { ctx.strokeStyle = 'rgba(232,163,61,.35)'; ctx.beginPath(); ctx.arc(b.x, b.y, st.p.stabilizer * 2.5, 0, Math.PI * 2); ctx.stroke(); }
      ctx.fillStyle = '#e8a33d'; ctx.beginPath(); ctx.arc(a.x, a.y, 3, 0, Math.PI * 2); ctx.fill();
      ctx.restore();
    }
    const pt = app.pointer;
    if (!pt.inside || pt.type === 'touch') return;
    const look = app.settings.brushCursor || 'circle';
    const r = (this.preset.size / 2) * view.zoom;
    ctx.lineWidth = 1;
    if (look === 'shape' && r > 2 && this._drawTipShape(ctx, pt, r, view)) { /* 브러시 모양으로 그렸음 */ }
    else if (look === 'cross') {
      ctx.strokeStyle = 'rgba(0,0,0,.7)';
      ctx.beginPath(); ctx.moveTo(pt.sx - 8, pt.sy); ctx.lineTo(pt.sx + 8, pt.sy); ctx.moveTo(pt.sx, pt.sy - 8); ctx.lineTo(pt.sx, pt.sy + 8); ctx.stroke();
      ctx.strokeStyle = 'rgba(255,255,255,.8)';
      ctx.beginPath(); ctx.moveTo(pt.sx - 8, pt.sy + 1); ctx.lineTo(pt.sx + 8, pt.sy + 1); ctx.stroke();
    } else if (look !== 'dot' && r > 3) {
      ctx.strokeStyle = 'rgba(0,0,0,0.6)';
      ctx.beginPath(); ctx.arc(pt.sx, pt.sy, r, 0, Math.PI * 2); ctx.stroke();
      ctx.strokeStyle = 'rgba(255,255,255,0.8)';
      ctx.beginPath(); ctx.arc(pt.sx, pt.sy, r + 1, 0, Math.PI * 2); ctx.stroke();
    }
    ctx.fillStyle = this.mode === 'erase' ? '#fff' : (this.preset.color || app.color.fg);
    ctx.strokeStyle = 'rgba(128,128,128,0.9)';
    ctx.beginPath(); ctx.arc(pt.sx, pt.sy, 1.6, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  }

  /** 커서를 브러시 끝 모양대로 (납작 펜 = 기운 타원, 이미지 = 그 모양, 연필결 = 거친 원) */
  _drawTipShape(ctx, pt, r, view) {
    const p = this.preset;
    ctx.save();
    ctx.translate(pt.sx, pt.sy);
    if (p.tip === 'flat') {
      ctx.rotate(((p.angle || 0) * Math.PI) / 180 + (view.rotation || 0));
      ctx.scale(1, Math.max(0.08, p.flatRatio || 0.3));
    }
    if (p.tip === 'image' && p.tipImage && this._tipImg?.src !== p.tipImage) {
      this._tipImg = { src: p.tipImage, img: null };
      loadImageTip(p.tipImage).then((img) => { if (this._tipImg?.src === p.tipImage) { this._tipImg.img = img; app.requestRender(); } }).catch(() => {});
    }
    if (p.tip === 'image' && this._tipImg?.img?.complete && this._tipImg.img.naturalWidth) {
      ctx.globalAlpha = 0.45;
      const im = this._tipImg.img, k = (2 * r) / Math.max(im.naturalWidth, im.naturalHeight);
      ctx.drawImage(im, -im.naturalWidth * k / 2, -im.naturalHeight * k / 2, im.naturalWidth * k, im.naturalHeight * k);
      ctx.globalAlpha = 1;
    }
    ctx.lineWidth = 1 / (p.tip === 'flat' ? Math.max(0.08, p.flatRatio || 0.3) : 1);
    ctx.strokeStyle = 'rgba(0,0,0,0.6)';
    ctx.setLineDash(p.tip === 'grain' || p.tip === 'spray' ? [2, 2] : []);
    ctx.beginPath(); ctx.arc(0, 0, r, 0, Math.PI * 2); ctx.stroke();
    // 부드러운(경도 낮은) 브러시는 안쪽에 진한 부분 표시
    if ((p.hardness ?? 1) < 0.9 && p.tip === 'round') { ctx.strokeStyle = 'rgba(0,0,0,0.3)'; ctx.beginPath(); ctx.arc(0, 0, r * Math.max(0.1, p.hardness), 0, Math.PI * 2); ctx.stroke(); }
    ctx.strokeStyle = 'rgba(255,255,255,0.8)'; ctx.setLineDash([]);
    ctx.beginPath(); ctx.arc(0, 0, r + 1, 0, Math.PI * 2); ctx.stroke();
    ctx.restore();
    return true;
  }

  options(el) {
    buildBrushOptions(el, this.category, this.mode);
  }
}

// ---- 옵션 패널 ------------------------------------------------------------
function buildBrushOptions(el, cat, mode) {
  const p = () => activePreset(cat);
  let refreshSoon = null;
  const set = (k) => (v) => { p()[k] = v; app.saveSettingsSoon(); app.bus.emit('brush:changed', p()); clearTimeout(refreshSoon); refreshSoon = setTimeout(() => { testPad?.refresh(); renderList?.(); }, 150); };
  const get = (k) => () => p()[k];

  const list = h('div.preset-list');
  const renderList = () => {
    list.innerHTML = '';
    for (const pr of presets(cat)) {
      const b = h('button.preset', { type: 'button' }, h('span.preset-name', null, pr.name), presetThumb(pr, mode === 'erase' ? '#888888' : (pr.color || app.color.fg)));
      b.classList.toggle('on', pr.id === p().id);
      b.addEventListener('click', () => { setActivePreset(cat, pr.id); renderList(); rebuild(); });
      list.append(b);
    }
  };

  const presetButtons = row(
    button('복제', () => {
      const cur = p();
      const name = prompt('새 프리셋 이름', cur.name + ' 2');
      if (!name) return;
      const n = { ...structuredClone(cur), id: uid('bp'), name };
      presets(cat).push(n);
      setActivePreset(cat, n.id);
      renderList(); rebuild();
    }),
    button('이름', () => {
      const name = prompt('프리셋 이름', p().name);
      if (name) { p().name = name; app.saveSettingsSoon(); renderList(); }
    }),
    button('기본값', () => {
      const def = (DEFAULT_PRESETS[cat] || []).find(d => d.id === p().id);
      if (!def) { app.toast('사용자 프리셋은 기본값이 없습니다'); return; }
      Object.assign(p(), makePreset(def));
      setActivePreset(cat, p().id);
      rebuild();
    }),
    button('삭제', () => {
      const l = presets(cat);
      if (l.length <= 1) return;
      if (!confirm(`'${p().name}' 프리셋을 삭제할까요?`)) return;
      l.splice(l.indexOf(p()), 1);
      setActivePreset(cat, l[0].id);
      renderList(); rebuild();
    }, 'danger'),
  );

  const testPad = brushTestPad(() => p(), { erase: mode === 'erase' });
  const body = h('div');
  const rebuild = () => {
    body.innerHTML = '';
    const cur = p();
    testPad.refresh();
    const add = (...els) => body.append(...els.filter(Boolean));
    add(
      section('기본',
        slider('크기', { min: 0.5, max: 600, step: 0.5, log: true, get: get('size'), set: set('size'), format: v => v < 10 ? v.toFixed(1) : Math.round(v) }),
        slider('불투명도', { min: 0, max: 1, step: 0.01, get: get('opacity'), set: set('opacity'), format: v => Math.round(v * 100) + '%' }),
        slider('빠를수록 가늘게', { min: 0, max: 1, step: 0.05, get: get('speedThin'), set: set('speedThin'), format: v => (v ? Math.round(v * 100) + '%' : '끔') }),
      ),
      section('미리보기 · 시험 그리기', testPad.el),
      section('흔들림 보정',
        segmented([['auto', '설정대로'], ['euro', '자동'], ['avg', '평균'], ['lazy', '끈 당기기'], ['spring', '무거운 펜']], get('stabMode'), (v) => { set('stabMode')(v); rebuild(); }),
        slider(cur.stabMode === 'lazy' ? '끈 길이 (클수록 떨림 적고 늦게 따라옴)' : cur.stabMode === 'spring' ? '펜 무게 (클수록 느리게·부드럽게)' : '보정 세기', { min: 0, max: 20, step: 1, get: get('stabilizer'), set: set('stabilizer'), format: v => (v ? String(v) : '끔') }),
        toggle('펜을 떼면 펜 끝까지 이어 그리기', () => p().catchUp !== false, set('catchUp')),
        toggle('선 끝에서 펜을 멈추고 있으면 직선으로 (모든 브러시)', () => !!app.settings.holdLine, (v) => { app.settings.holdLine = v; app.saveSettingsSoon(); }),
        h('div.help', null, {
          auto: '설정 > 펜 의 보정 방식을 따릅니다.', euro: '느리게 그리면 떨림을 많이, 빠르게 그리면 지연을 적게 잡습니다.', avg: '최근 점들의 평균으로 고르게 (예전 방식).',
          lazy: '펜과 선 끝 사이에 보이지 않는 끈이 있어, 끈이 팽팽해질 때만 선이 따라옵니다. 떨림이 거의 없어지고 곡선이 매끈해집니다 (그리는 동안 끈이 보임).',
          spring: '선 끝이 용수철에 매달린 무거운 펜처럼 살짝 늦게, 부드럽게 따라옵니다. 긴 곡선·머리카락에 좋습니다.',
        }[cur.stabMode || 'auto'])),
      section('입출 보정 (선 끝 가늘게)',
        slider('시작', { min: 0, max: 400, step: 1, get: get('taperIn'), set: set('taperIn'), format: v => (v ? Math.round(v) + 'px' : '끔') }),
        slider('끝', { min: 0, max: 400, step: 1, get: get('taperOut'), set: set('taperOut'), format: v => (v ? Math.round(v) + 'px' : '끔') }),
      ),
      section('필압',
        row(toggle('크기', get('pressureSize'), (v) => { set('pressureSize')(v); rebuild(); }),
          toggle('불투명도', get('pressureOpacity'), (v) => { set('pressureOpacity')(v); rebuild(); })),
        cur.pressureSize ? slider('최소 크기', { min: 0, max: 1, step: 0.01, get: get('minSize'), set: set('minSize'), format: v => Math.round(v * 100) + '%' }) : null,
        cur.pressureOpacity ? slider('최소 농도', { min: 0, max: 1, step: 0.01, get: get('minOpacity'), set: set('minOpacity'), format: v => Math.round(v * 100) + '%' }) : null,
        toggle('이 브러시만 필압 곡선 따로', () => !!p().pressurePts, (v) => { p().pressurePts = v ? structuredClone(app.settings.pressurePts || [[0, 0], [1, 1]]) : null; app.saveSettingsSoon(); app.bus.emit('brush:changed', p()); rebuild(); }),
        cur.pressurePts ? curveEditor({ title: '이 브러시의 필압 곡선', get: () => p().pressurePts, set: (pts) => { p().pressurePts = pts || [[0, 0], [1, 1]]; app.saveSettingsSoon(); app.bus.emit('brush:changed', p()); } }).el
          : h('div.help', null, '모든 브러시 공통 곡선은 설정 > 펜 에서 바꿉니다.'),
      ),
      section('브러시 끝',
        segmented([['round', '원형'], ['grain', '연필결'], ['spray', '스프레이'], ['flat', '납작 펜'], ['image', '이미지'], ['pixel', '픽셀']], get('tip'), (v) => { set('tip')(v); if (v === 'pixel') { set('pressureSize')(false); set('stabilizer')(0); } rebuild(); }),
        cur.tip === 'pixel' ? toggle('픽셀 퍼펙트 (1px 선의 ㄴ자 겹친 모서리 지우기)', get('pixelPerfect'), set('pixelPerfect')) : null,
        cur.tip === 'pixel' && mode !== 'erase' ? toggle('명암 잉크 (밑 색을 팔레트에서 한 칸씩 어둡게/밝게)', get('shading'), (v) => { set('shading')(v); rebuild(); }) : null,
        cur.shading ? segmented([[-1, '어둡게'], [1, '밝게']], get('shadeDir'), set('shadeDir')) : null,
        cur.shading ? h('div.help', null, '팔레트에서 같은 계열 색(비슷한 색상, 회색은 회색끼리)을 밝기 순으로 세운 사다리에서 한 칸씩 옮깁니다 (한 획에 한 번). Alt 를 누르고 그리면 반대로. 팔레트에 없는 색은 밝기만 조금 바뀝니다.') : null,
        cur.tip !== 'pixel' && !['spray', 'flat'].includes(cur.tip) ? toggle('리본 (끝 모양을 획 방향으로 이어 붙여 늘리기)', get('ribbon'), set('ribbon')) : null,
        cur.tip === 'spray' ? slider('입자 수', { min: 2, max: 120, step: 1, get: get('density'), set: set('density') }) : null,
        cur.tip === 'spray' ? slider('입자 크기', { min: 0.3, max: 8, step: 0.1, get: get('particle'), set: set('particle'), format: v => v.toFixed(1) }) : null,
        cur.tip === 'flat' ? slider('납작한 정도', { min: 0.05, max: 1, step: 0.01, get: get('flatRatio'), set: set('flatRatio'), format: v => Math.round(v * 100) + '%' }) : null,
        cur.tip === 'flat' ? slider('펜 각도', { min: -90, max: 90, step: 1, get: get('angle'), set: set('angle'), format: v => v + '°' }) : null,
        cur.tip === 'flat' ? toggle('펜 기울기·돌림으로 각도 정하기', get('useTilt'), set('useTilt')) : null,
        !['image', 'spray', 'flat', 'pixel'].includes(cur.tip) ? slider('경도', { min: 0, max: 1, step: 0.01, get: get('hardness'), set: set('hardness'), format: v => Math.round(v * 100) + '%' }) : null,
        cur.tip === 'grain' ? slider('입자', { min: 0, max: 1, step: 0.01, get: get('grain'), set: set('grain'), format: v => Math.round(v * 100) + '%' }) : null,
        cur.tip === 'image' ? tipImagePicker(cur, set) : null,
        slider('흐름', { min: 0.01, max: 1, step: 0.01, get: get('flow'), set: set('flow'), format: v => Math.round(v * 100) + '%' }),
        slider('간격', { min: 0.02, max: 2, step: 0.01, log: true, get: get('spacing'), set: set('spacing'), format: v => Math.round(v * 100) + '%' }),
        toggle('덧칠 누적 (에어브러시)', get('buildup'), set('buildup')),
      ),
      mode !== 'erase' ? section('동작 (기울기·속도·색)',
        slider('펜을 눕히면 굵게', { min: 0, max: 1, step: 0.01, get: get('tiltSize'), set: set('tiltSize'), format: v => (v ? Math.round(v * 100) + '%' : '끔') }),
        slider('펜을 눕히면 옅게', { min: 0, max: 1, step: 0.01, get: get('tiltOpacity'), set: set('tiltOpacity'), format: v => (v ? Math.round(v * 100) + '%' : '끔') }),
        slider('빠를수록 옅게', { min: 0, max: 1, step: 0.01, get: get('speedOpacity'), set: set('speedOpacity'), format: v => (v ? Math.round(v * 100) + '%' : '끔') }),
        toggle('필압으로 색 바뀜 (약하게 = 배경색, 세게 = 전경색)', get('pressureColor'), set('pressureColor')),
        slider('색상 흔들림', { min: 0, max: 0.5, step: 0.01, get: get('hueJitter'), set: set('hueJitter'), format: v => (v ? Math.round(v * 360) + '°' : '끔') }),
        slider('채도 흔들림', { min: 0, max: 1, step: 0.01, get: get('satJitter'), set: set('satJitter'), format: v => (v ? Math.round(v * 100) + '%' : '끔') }),
        slider('명도 흔들림', { min: 0, max: 1, step: 0.01, get: get('valJitter'), set: set('valJitter'), format: v => (v ? Math.round(v * 100) + '%' : '끔') }),
      ) : null,
      mode !== 'erase' ? section('종이 결',
        segmented([['none', '없음'], ['fine', '고운'], ['rough', '수채지'], ['canvas', '캔버스'], ['kraft', '섬유']], get('texture'), (v) => { set('texture')(v); rebuild(); }),
        cur.texture && cur.texture !== 'none' ? slider('결 세기', { min: 0.05, max: 1, step: 0.01, get: get('textureStrength'), set: set('textureStrength'), format: v => Math.round(v * 100) + '%' }) : null,
        cur.texture && cur.texture !== 'none' ? slider('결 크기', { min: 0.25, max: 4, step: 0.05, get: get('textureScale'), set: set('textureScale'), format: v => v.toFixed(2) + '배' }) : null,
        cur.texture && cur.texture !== 'none' ? segmented([['multiply', '곱하기'], ['subtract', '빼기'], ['hard', '문턱 (또렷)']], get('textureMode'), set('textureMode')) : null,
      ) : null,
      cur.tip !== 'pixel' && !['spray', 'flat'].includes(cur.tip) ? section('듀얼 브러시 (두 번째 끝 모양으로 가리기)',
        segmented([['none', '끔'], ['grain', '거친 결'], ['round', '원'], ['spray', '점']], get('dualTip'), (v) => { set('dualTip')(v); rebuild(); }),
        cur.dualTip && cur.dualTip !== 'none' ? slider('두 번째 크기', { min: 0.05, max: 1, step: 0.01, get: get('dualSize'), set: set('dualSize'), format: v => Math.round(v * 100) + '%' }) : null,
        cur.dualTip && cur.dualTip !== 'none' ? slider('찍는 수', { min: 1, max: 30, step: 1, get: get('dualDensity'), set: set('dualDensity') }) : null,
        cur.dualTip && cur.dualTip !== 'none' ? slider('퍼짐', { min: 0, max: 1.5, step: 0.05, get: get('dualScatter'), set: set('dualScatter'), format: v => Math.round(v * 100) + '%' }) : null,
      ) : null,
      section('디더 무늬 (점 무늬로 칠하기)',
        slider('칠하는 칸 비율', { min: 0, max: 1, step: 0.0625, get: get('dither'), set: (v) => set('dither')(v >= 1 ? 0 : v), format: v => (v > 0 && v < 1 ? Math.round(v * 100) + '%' : '끔') }),
        segmented([[2, '2×2'], [4, '4×4']], get('ditherSize'), set('ditherSize')),
      ),
      cur.tip === 'image' || cur.scatter || cur.rotJitter || cur.sizeJitter ? section('흩뿌리기',
        slider('흩뿌림', { min: 0, max: 3, step: 0.05, get: get('scatter'), set: set('scatter'), format: v => v.toFixed(2) }),
        slider('회전 랜덤', { min: 0, max: 1, step: 0.01, get: get('rotJitter'), set: set('rotJitter'), format: v => Math.round(v * 360) + '°' }),
        slider('크기 랜덤', { min: 0, max: 1, step: 0.01, get: get('sizeJitter'), set: set('sizeJitter'), format: v => Math.round(v * 100) + '%' }),
        toggle('진행 방향으로 회전', get('followDir'), set('followDir')),
      ) : null,
      mode === 'erase' && app.doc?.active?.kind === 'vector' ? section('벡터 지우개',
        segmented([['part', '닿은 부분만'], ['cross', '교차점까지'], ['whole', '선 전체']], () => app.settings.vectorErase || 'part', v => { app.settings.vectorErase = v; app.saveSettingsSoon(); })) : null,
      mode !== 'erase' && cur.roughOnly ? roughSection(rebuild) : null,
      mode !== 'erase' ? section('러프', toggle('러프 레이어 전용으로 쓰기', get('roughOnly'), (v) => { set('roughOnly')(v); rebuild(); })) : null,
      mode !== 'erase' ? section('합성 · 혼색',
        select('합성 모드', [['source-over', '표준'], ['multiply', '곱하기'], ['screen', '스크린'], ['overlay', '오버레이'], ['color-dodge', '닷지 (발광)'], ['lighter', '더하기 (발광)'], ['color-burn', '번'], ['soft-light', '소프트 라이트'], ['hard-light', '하드 라이트'], ['lighten', '밝게'], ['darken', '어둡게'], ['color', '컬러'], ['hue', '색조'], ['saturation', '채도'], ['luminosity', '광도']], get('blend'), set('blend')),
        slider('혼색 (밑색과 섞기)', { min: 0, max: 1, step: 0.01, get: get('mix'), set: set('mix'), format: v => (v ? Math.round(v * 100) + '%' : '끔') }),
      ) : null,
      mode !== 'erase' && !cur.roughOnly ? section('색',
        row(toggle('브러시 전용 색 사용', () => !!p().color, (v) => { set('color')(v ? (p().color || '#3d7be0') : null); rebuild(); }),
          cur.color ? colorInput('', () => p().color, set('color')) : null),
      ) : null,
    );
  };

  el.append(h('div.section', null, list, presetButtons), body);
  renderList();
  rebuild();
}

/** 러프 색 (의견 색) 14가지 */
function roughSection(rebuild) {
  const act = app.doc?.active;
  const cur = act?.rough ? act.tint : defaultRoughColor();
  const grid = h('div.rough-colors');
  for (const [hex, name] of ROUGH_COLORS) {
    const b = h(`button.rough-sw${hex === cur ? '.on' : ''}`, { type: 'button', title: name },
      h('i', { style: { background: hex } }), h('span', null, name));
    b.onclick = () => { setRoughColor(app.doc.active, hex); rebuild(); };
    grid.append(b);
  }
  return section('러프 색 (의견 색)', grid,
    h('div.help', null, act?.rough ? '지금 러프 레이어의 색을 바꿉니다. 협업할 때 사람·의견마다 색을 나눠 쓰세요.' : '러프 레이어를 고르면 그 레이어 색이 바뀝니다. 지금 고른 색은 새 러프 레이어에 쓰입니다.'));
}

function tipImagePicker(cur, set) {
  const sel = select('이미지', [['', '(선택)']], () => cur.tipImage || '', (v) => {
    set('tipImage')(v || null);
    if (v) loadImageTip(v).catch(() => app.toast('이미지를 불러오지 못했습니다', 'warn'));
  });
  server.list('assets').then(items => {
    sel.setOptions([['', '(에셋 선택)'], ...items.filter(i => /\.png$/i.test(i.name)).map(i => [i.name, i.name])]);
  }).catch(() => {});
  return h('div', null, sel, toggle('현재 색으로 칠하기', () => cur.tintTip, set('tintTip')));
}

// 러프 연필을 쓰는 중에 레이어를 바꾸면 러프 색 표시를 새로 그림
const refreshRough = () => { if (app.tool?.category === 'brush' && activePreset('brush').roughOnly) app.bus.emit('brush:presets'); };
app.bus.on('layers:active', refreshRough);
app.bus.on('layers:changed', refreshRough);
