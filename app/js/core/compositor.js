// 레이어 합성기: 화면 표시, 내보내기, 스포이트 샘플링에 공통으로 사용
//  - 폴더: 자식들을 따로 합성한 뒤 폴더의 불투명도/합성 모드로 얹음
//  - 조정 레이어: 같은 단계에서 아래에 쌓인 그림 전체에 색 보정 필터 적용 (클리핑이면 바로 아래 레이어만)
//  - 레이어 효과(테두리/그림자/광선), 톤화(망점), 보정 미리보기
import { xsOf, frameCount } from './anim.js';
import { motionAt } from './motion.js';
import { drawPaper, drawTexture, applyGrain, hasBrushPost } from './paper.js';
import { app } from '../app.js';
import { createCanvas, rgbToHex } from './util.js';
import { adjustFilter, fxFilter, hasFx, defineFilter } from './filters.js';
import { halftone } from './raster.js';

const ONION_COLORS = { prev: '#e0455a', next: '#2fa866' };

export class Compositor {
  constructor() { this.pool = {}; }

  _scratch(key, w, h) {
    let c = this.pool[key];
    if (!c || c.width !== w || c.height !== h) { c = createCanvas(w, h); this.pool[key] = c; }
    const x = c.getContext('2d');
    x.setTransform(1, 0, 0, 1, 0, 0);
    x.globalAlpha = 1;
    x.globalCompositeOperation = 'source-over';
    x.filter = 'none';
    x.clearRect(0, 0, w, h);
    return x;
  }

  _visible(l, o) {
    if (!l.visible) return false;
    if (o.excludeDraft && l.draft) return false;
    return true;
  }

  _special(l, o) {
    return !!(l.isFolder || l.tint || l.tone || hasFx(l) || (l.mask && l.maskOn) || (o.live && o.live.layer === l)
      || (o.floating && o.floating.layer === l) || (o.preview && o.preview.layer === l));
  }

  _kids(o, folder) {
    return o.kids.get(folder ? folder.id : null) || [];
  }

  /** 톤화 레이어: 레이어 그림이 바뀔 때만 망점을 다시 계산 */
  _toneSource(l, o) {
    if (o.live && o.live.layer === l) return null;           // 그리는 중에는 원본(회색)으로 보여 줌
    const key = JSON.stringify(l.tone) + ':' + l.version;
    if (!l._toneCache || l._toneCache.key !== key) l._toneCache = { key, canvas: halftone(l.canvas, l.tone) };
    return l._toneCache.canvas;
  }

  /** 레이어(또는 폴더) 하나를 대상 크기 임시 캔버스에 그려서 돌려준다 */
  _renderLayer(key, l, m, w, h, o, d, tintOverride) {
    const x = this._scratch(key, w, h);
    x.imageSmoothingEnabled = o.smoothing;
    if (l.isFolder) {
      this._renderFolder(x, l, m, w, h, o, d);
    } else {
      x.setTransform(m);
      const tone = l.tone ? this._toneSource(l, o) : null;
      if (tone) x.drawImage(tone, 0, 0); else x.drawImage(l.displayCanvas, 0, 0, l.doc.width, l.doc.height);
      const live = o.live;
      if (live && live.layer === l && !live.onMask) {
        x.globalAlpha = live.opacity;
        x.globalCompositeOperation = live.mode === 'erase' ? 'destination-out'
          : (l.alphaLock || live.mode === 'atop') ? 'source-atop' : (live.blend || 'source-over');
        let src = live.canvas;
        if (hasBrushPost(live.preset)) {
          // 결은 버퍼에 직접 쓰지 않고 그릴 때마다 사본에 씌운다 (겹쳐 칠해도 결이 진해지지 않게)
          const r = live.rect?.() || { x: 0, y: 0, w: live.canvas.width, h: live.canvas.height };
          const sc = this._scratch('grain', live.canvas.width, live.canvas.height);
          sc.setTransform(1, 0, 0, 1, 0, 0);
          sc.clearRect(r.x - 2, r.y - 2, r.w + 4, r.h + 4);
          sc.drawImage(live.canvas, r.x, r.y, r.w, r.h, r.x, r.y, r.w, r.h);
          applyGrain(sc, o.doc, live.preset, r);
          x.drawImage(sc.canvas, r.x, r.y, r.w, r.h, r.x, r.y, r.w, r.h);   // 이번 획 범위만 (이전 획의 남은 것 제외)
          src = null;
        }
        if (src) x.drawImage(src, 0, 0);
        x.globalAlpha = 1;
        x.globalCompositeOperation = 'source-over';
      }
      const fl = o.floating;
      if (fl && fl.layer === l) {
        x.setTransform(m.multiply(fl.matrix));
        x.imageSmoothingEnabled = true;
        x.drawImage(fl.canvas, 0, 0);
        x.imageSmoothingEnabled = o.smoothing;
      }
    }
    const live = o.live;
    if (l.mask && l.maskOn) {
      let src = l.mask;
      if (live && live.layer === l && live.onMask) {
        const mk = this._scratch('mk', l.mask.width, l.mask.height);
        mk.drawImage(l.mask, 0, 0);
        mk.globalAlpha = live.opacity;
        mk.globalCompositeOperation = live.mode === 'erase' ? 'destination-out' : 'source-over';
        mk.drawImage(live.canvas, 0, 0);
        src = mk.canvas;
      }
      x.setTransform(m);
      x.globalCompositeOperation = 'destination-in';
      x.drawImage(src, 0, 0);
      x.globalCompositeOperation = 'source-over';
    }
    x.setTransform(1, 0, 0, 1, 0, 0);
    // 보정 미리보기 (대화상자에서 값 바꾸는 중)
    if (o.preview && o.preview.layer === l) this._filterInPlace(x, defineFilter('adj-preview', o.preview.inner, w, h), w, h, key);
    const tint = tintOverride || l.tint;
    if (tint) {
      x.globalCompositeOperation = 'source-in';
      x.fillStyle = tint;
      x.fillRect(0, 0, w, h);
      x.globalCompositeOperation = 'source-over';
    }
    if (hasFx(l)) {
      const s = Math.hypot(m.a, m.b);
      const f = fxFilter(l.id, l.fx, s, w, h);
      if (f) this._filterInPlace(x, f, w, h, key);
    }
    return x;
  }

  _filterInPlace(x, filter, w, h, key) {
    const t = this._scratch('flt' + key, w, h);
    t.filter = filter;
    t.drawImage(x.canvas, 0, 0);
    t.filter = 'none';
    x.globalCompositeOperation = 'copy';
    x.drawImage(t.canvas, 0, 0);
    x.globalCompositeOperation = 'source-over';
  }

  /** 조정 레이어를 target(지금까지 쌓인 그림)에 적용 */
  _applyAdjust(target, l, m, w, h, o, d) {
    target.setTransform(1, 0, 0, 1, 0, 0);
    const t = this._scratch('adj' + d, w, h);
    const p = o.preview && o.preview.layer === l ? o.preview : null;
    t.filter = p ? defineFilter('adj-preview', p.inner, w, h) : adjustFilter(l.id, l.meta.type, l.meta.params, w, h);
    t.drawImage(target.canvas, 0, 0);
    t.filter = 'none';
    if (l.mask && l.maskOn) {
      t.setTransform(m);
      t.globalCompositeOperation = 'destination-in';
      t.drawImage(l.mask, 0, 0);
      t.setTransform(1, 0, 0, 1, 0, 0);
      t.globalCompositeOperation = 'source-over';
    }
    target.setTransform(1, 0, 0, 1, 0, 0);
    target.globalAlpha = l.opacity;
    target.globalCompositeOperation = 'source-atop';
    target.drawImage(t.canvas, 0, 0);
    target.globalAlpha = 1;
    target.globalCompositeOperation = 'source-over';
  }

  _renderFolder(x, f, m, w, h, o, d) {
    const kids = this._kids(o, f);
    if (!(f.meta && f.meta.anim) || !kids.length) { this._renderList(x, kids, m, w, h, o, d + 1); return; }
    // 애니메이션 트랙: 지금 프레임에 노출된 셀만 (+ 어니언 스킨, 라이트 테이블)
    const doc = o.doc, a = doc.anim, n = Math.max(1, frameCount(doc));
    const frame = Math.max(0, Math.min(n - 1, o.frame ?? a.frame));
    const xs = xsOf(f, doc), byId = new Map(kids.map(k => [k.id, k]));
    const curId = xs[frame] || null;
    // 모션 경로: 문서 좌표 변환 T 를 대상 좌표로 (m·T·m⁻¹)
    const moMatrix = (g) => { const mo = motionAt(f, g, doc); return mo ? { M: m.multiply(mo.T).multiply(m.inverse()), alpha: mo.alpha } : null; };
    const ghostOf = (c, color, alpha, g = null) => {
      if (!c || !c.visible) return;
      const t = this._renderLayer('onion' + d, c, m, w, h, { ...o, live: null, floating: null }, d + 1, color);
      const mo = g == null ? null : moMatrix(g);
      x.globalAlpha = alpha * (mo ? mo.alpha : 1);
      if (mo) x.setTransform(mo.M);
      x.drawImage(t.canvas, 0, 0);
      x.setTransform(1, 0, 0, 1, 0, 0);
      x.globalAlpha = 1;
    };
    // 라이트 테이블: 올려 둔 셀을 늘 흐리게
    const light = a.light?.ids?.length && !a.playing && o.onion ? a.light.ids.filter(id => byId.has(id) && id !== curId) : [];
    for (const id of light) ghostOf(byId.get(id), a.light.color || '#8a63d2', a.light.opacity ?? 0.25);
    if (o.onion && a.onion && !a.playing) {
      // 앞뒤로 "다른 그림"인 셀만 (같은 셀을 길게 보여 주는 칸은 건너뜀)
      const collect = (dir, count) => {
        const out = [], seen = new Set([motionAt(f, frame, doc) ? curId + '@' + frame : curId]);
        for (let k = 1; k < n && out.length < count; k++) {
          let g = frame + dir * k;
          if (g < 0 || g >= n) { if (!a.loop) break; g = ((g % n) + n) % n; }
          const id = xs[g], key = motionAt(f, g, doc) ? id + '@' + g : id;      // 모션이 있으면 같은 그림도 프레임마다 다르게 보임
          if (id && !seen.has(key)) { seen.add(key); out.push([id, g]); }
        }
        return out;
      };
      const prev = collect(-1, a.onionPrev || 0), next = collect(1, a.onionNext || 0);
      const base = a.onionOpacity ?? 0.3, fall = a.onionFalloff ?? 0.5;
      prev.slice().reverse().forEach(([id, g], k, arr) => ghostOf(byId.get(id), a.onionPrevColor || ONION_COLORS.prev, base * Math.pow(1 - fall, arr.length - 1 - k), g));
      next.slice().reverse().forEach(([id, g], k, arr) => ghostOf(byId.get(id), a.onionNextColor || ONION_COLORS.next, base * Math.pow(1 - fall, arr.length - 1 - k), g));
    }
    const cel = curId && byId.get(curId);
    if (!cel) return;
    const mo = moMatrix(frame);
    if (!mo) { this._renderList(x, [cel], m, w, h, o, d + 1); return; }
    // 모션이 있는 프레임: 따로 그린 뒤 옮겨서 (레이어 불투명도·합성·효과는 그대로)
    const sc = this._scratch('motion' + d, w, h);
    this._renderList(sc, [cel], m, w, h, o, d + 1);
    x.save();
    x.setTransform(mo.M);
    x.globalAlpha = mo.alpha;
    x.drawImage(sc.canvas, 0, 0);
    x.restore();
  }

  /** 같은 단계의 레이어들(아래→위)을 ctx 에 합성. 클리핑은 바로 아래 기준 레이어에 */
  _renderList(ctx, L, m, w, h, o, d) {
    const hasAdjust = L.some(l => l.kind === 'adjust' && !l.clip && this._visible(l, o));
    let out = ctx;
    if (hasAdjust) {
      // 조정 레이어가 있으면 이 단계를 따로 모아서 그 위에 보정을 건다
      out = this._scratch('lvl' + d, w, h);
      if (d === 0 && o._paperInLevel) {
        out.setTransform(m);
        drawPaper(out, o.doc);
        out.setTransform(1, 0, 0, 1, 0, 0);
      }
    }
    let i = 0;
    while (i < L.length) {
      const base = L[i];
      let j = i + 1;
      while (j < L.length && L[j].clip) j++;
      const clips = L.slice(i + 1, j).filter(c => this._visible(c, o));
      i = j;
      if (!this._visible(base, o) || base.opacity <= 0) continue;
      if (base.kind === 'adjust') {
        if (hasAdjust) this._applyAdjust(out, base, m, w, h, o, d);
        continue;
      }
      if (!clips.length && !this._special(base, o)) {
        out.setTransform(m);
        out.globalAlpha = base.opacity;
        out.globalCompositeOperation = base.blend;
        out.drawImage(base.displayCanvas, 0, 0, base.doc.width, base.doc.height);
        continue;
      }
      const bx = this._renderLayer('base' + d, base, m, w, h, o, d);
      if (clips.length) {
        let mask = null;
        if (clips.some(c => c.blend !== 'source-over' && c.kind !== 'adjust')) {
          mask = this._scratch('mask' + d, w, h);
          mask.drawImage(bx.canvas, 0, 0);
        }
        for (const c of clips) {
          if (c.kind === 'adjust') { this._applyAdjust(bx, c, m, w, h, o, 'c' + d); continue; }
          const cx = this._renderLayer('clip' + d, c, m, w, h, o, d);
          if (c.blend === 'source-over') {
            bx.globalCompositeOperation = 'source-atop';
          } else {
            cx.globalCompositeOperation = 'destination-in';
            cx.drawImage(mask.canvas, 0, 0);
            bx.globalCompositeOperation = c.blend;
          }
          bx.globalAlpha = c.opacity;
          bx.drawImage(cx.canvas, 0, 0);
          bx.globalAlpha = 1;
          bx.globalCompositeOperation = 'source-over';
        }
      }
      out.setTransform(1, 0, 0, 1, 0, 0);
      out.globalAlpha = base.opacity;
      out.globalCompositeOperation = base.blend;
      out.drawImage(bx.canvas, 0, 0);
    }
    out.setTransform(1, 0, 0, 1, 0, 0);
    out.globalAlpha = 1;
    out.globalCompositeOperation = 'source-over';
    if (hasAdjust) {
      // ctx 에는 용지를 그릴 때 쓴 변환이 남아 있을 수 있으므로 원래대로 돌리고 얹는다
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.globalAlpha = 1;
      ctx.globalCompositeOperation = 'source-over';
      ctx.drawImage(out.canvas, 0, 0);
    }
  }

  /**
   * ctx 에 문서를 그린다. m 은 문서 -> 대상 픽셀 변환.
   * o: { w, h, paper, excludeDraft, only:Set, roots:[], live, floating, smoothing, onion, frame, preview }
   */
  render(ctx, doc, m, o = {}) {
    const w = o.w ?? ctx.canvas.width, h = o.h ?? ctx.canvas.height;
    o = { paper: true, smoothing: true, live: null, floating: null, onion: false, preview: app.preview || null, ...o, doc };
    const kids = new Map();
    for (const l of doc.layers) {
      const p = doc.parentOf(l);
      if (!kids.has(p)) kids.set(p, []);
      kids.get(p).push(l);
    }
    o.kids = kids;
    let roots;
    if (o.only) roots = doc.layers.filter(l => o.only.has(l) && !l.isFolder);   // 구조 무시, 지정한 레이어만
    else roots = o.roots || kids.get(null) || [];
    ctx.save();
    ctx.imageSmoothingEnabled = o.smoothing;
    ctx.imageSmoothingQuality = 'medium';
    const paper = o.paper && doc.paper.visible;
    if (paper && roots.some(l => l.kind === 'adjust' && !l.clip && l.visible)) o._paperInLevel = true;
    else if (paper) {
      ctx.setTransform(m);
      drawPaper(ctx, doc);
    }
    this._renderList(ctx, roots, m, w, h, o, 0);
    // 용지 질감을 그림 위에도 (물감이 종이 결에 먹은 느낌)
    if (paper && doc.paper.texture?.over) { ctx.save(); ctx.setTransform(m); drawTexture(ctx, doc, 0.7); ctx.restore(); }
    ctx.restore();
  }

  /** 문서 크기의 합성 이미지 */
  flatten(doc, o = {}) {
    const scale = o.scale ?? 1;
    const c = createCanvas(doc.width * scale, doc.height * scale);
    const ctx = c.getContext('2d');
    if (o.background) { ctx.fillStyle = o.background; ctx.fillRect(0, 0, c.width, c.height); }
    this.render(ctx, doc, new DOMMatrix().scale(scale), { excludeDraft: true, preview: null, ...o, smoothing: true });
    return c;
  }

  /** (x, y) 의 합성 색 */
  sample(doc, x, y, o = {}) {
    const c = this.pool.__px || (this.pool.__px = createCanvas(1, 1));
    const ctx = c.getContext('2d', { willReadFrequently: true });
    ctx.clearRect(0, 0, 1, 1);
    this.render(ctx, doc, new DOMMatrix().translate(-Math.floor(x), -Math.floor(y)), { w: 1, h: 1, smoothing: false, live: null, ...o });
    const d = ctx.getImageData(0, 0, 1, 1).data;
    return { hex: rgbToHex(d[0], d[1], d[2]), a: d[3] };
  }
}

export function sampleLayer(layer, x, y) {
  const d = layer.ctx.getImageData(Math.floor(x), Math.floor(y), 1, 1).data;
  return { hex: rgbToHex(d[0], d[1], d[2]), a: d[3] };
}

app.compositor = new Compositor();
