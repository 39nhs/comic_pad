// 화면 그리기: 캔버스 + 선택 영역 + 그리드/대칭축 + 도구 오버레이
import { app } from '../app.js';

export function initRenderer(canvas, stage) {
  // 화면 캔버스는 펜 지연을 줄이는 저지연 모드(desynchronized)라 그리는 도중의 모습이 그대로 보일 수 있다.
  // (그래픽카드가 AI 등으로 바쁠 때 '지웠다 다시 그리는' 순간이 깜빡임으로 보임)
  // 그래서 모든 그리기는 뒤 버퍼에 하고, 다 그린 뒤 한 번에 화면으로 옮긴다.
  const screen = canvas.getContext('2d', app.settings.lowLatency === false ? { alpha: false } : { desynchronized: true, alpha: false });
  const back = document.createElement('canvas');
  const ctx = back.getContext('2d', { alpha: false });
  let dpr = window.devicePixelRatio || 1;
  let checker = null;
  const stageBg = getComputedStyle(document.documentElement).getPropertyValue('--stage-bg').trim() || '#38383d';

  const resize = () => {
    dpr = window.devicePixelRatio || 1;
    const r = stage.getBoundingClientRect();
    canvas.width = Math.max(1, Math.round(r.width * dpr));
    canvas.height = Math.max(1, Math.round(r.height * dpr));
    back.width = canvas.width; back.height = canvas.height;
    canvas.style.width = r.width + 'px';
    canvas.style.height = r.height + 'px';
    app.view.resize(r.width, r.height);
    app.requestRender();
  };
  new ResizeObserver(resize).observe(stage);
  resize();

  function checkerPattern() {
    if (checker) return checker;
    const c = document.createElement('canvas');
    c.width = c.height = 16;
    const x = c.getContext('2d');
    x.fillStyle = '#ffffff'; x.fillRect(0, 0, 16, 16);
    x.fillStyle = '#d8d8d8'; x.fillRect(0, 0, 8, 8); x.fillRect(8, 8, 8, 8);
    checker = ctx.createPattern(c, 'repeat');
    return checker;
  }

  function drawSelection(m, view) {
    const sel = app.doc.selection;
    if (!sel.active) return;
    const W = canvas.width, H = canvas.height;
    // 선택 밖을 살짝 어둡게
    const dim = app.compositor._scratch('seldim', W, H);
    dim.fillStyle = 'rgba(30,70,160,0.10)';
    dim.fillRect(0, 0, W, H);
    dim.setTransform(m);
    dim.globalCompositeOperation = 'destination-out';
    dim.drawImage(sel.mask, 0, 0);
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.drawImage(dim.canvas, 0, 0);
    if (sel.paths) {
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.lineWidth = 1;
      for (const [color, off] of [['#000', 0], ['#fff', 4]]) {
        ctx.strokeStyle = color;
        ctx.setLineDash([4, 4]);
        ctx.lineDashOffset = off;
        ctx.beginPath();
        for (const poly of sel.paths) {
          poly.forEach((p, i) => {
            const s = view.toScreen(p.x, p.y);
            if (i) ctx.lineTo(s.x, s.y); else ctx.moveTo(s.x, s.y);
          });
          ctx.closePath();
        }
        ctx.stroke();
      }
      ctx.setLineDash([]);
    } else if (sel.edge) {
      ctx.setTransform(m);
      ctx.imageSmoothingEnabled = view.zoom < 1;
      ctx.drawImage(sel.edge, sel.bounds.x, sel.bounds.y);
    }
  }

  function drawGuides(m, view) {
    const doc = app.doc, s = app.settings;
    ctx.setTransform(m);
    const px = 1 / (view.zoom * dpr);
    if (s.showGrid && s.gridSize > 0) {
      ctx.strokeStyle = 'rgba(60,120,255,0.25)';
      ctx.lineWidth = px;
      ctx.beginPath();
      for (let x = s.gridSize; x < doc.width; x += s.gridSize) { ctx.moveTo(x, 0); ctx.lineTo(x, doc.height); }
      for (let y = s.gridSize; y < doc.height; y += s.gridSize) { ctx.moveTo(0, y); ctx.lineTo(doc.width, y); }
      ctx.stroke();
    }
    // 픽셀 격자: 많이 확대했을 때만, 보이는 부분만
    if (s.pixelGrid && view.zoom >= 6) {
      const inv = m.inverse(), a = inv.transformPoint({ x: 0, y: 0 }), b = inv.transformPoint({ x: canvas.width, y: canvas.height });
      const c = inv.transformPoint({ x: canvas.width, y: 0 }), d = inv.transformPoint({ x: 0, y: canvas.height });
      const x0 = Math.max(0, Math.floor(Math.min(a.x, b.x, c.x, d.x))), x1 = Math.min(doc.width, Math.ceil(Math.max(a.x, b.x, c.x, d.x)));
      const y0 = Math.max(0, Math.floor(Math.min(a.y, b.y, c.y, d.y))), y1 = Math.min(doc.height, Math.ceil(Math.max(a.y, b.y, c.y, d.y)));
      if ((x1 - x0) + (y1 - y0) < 4000) {
        ctx.strokeStyle = view.zoom >= 12 ? 'rgba(128,128,128,0.45)' : 'rgba(128,128,128,0.25)';
        ctx.lineWidth = px;
        ctx.beginPath();
        for (let x = x0; x <= x1; x++) { ctx.moveTo(x, y0); ctx.lineTo(x, y1); }
        for (let y = y0; y <= y1; y++) { ctx.moveTo(x0, y); ctx.lineTo(x1, y); }
        ctx.stroke();
      }
    }
    if (s.symmetry !== 'none') {
      ctx.strokeStyle = 'rgba(230,80,160,0.8)';
      ctx.lineWidth = px * 1.5;
      ctx.setLineDash([8 * px, 6 * px]);
      ctx.beginPath();
      if (s.symmetry.includes('v')) { ctx.moveTo(doc.width / 2, 0); ctx.lineTo(doc.width / 2, doc.height); }
      if (s.symmetry === 'radial') {
        const n = Math.max(2, s.symN || 6), R = Math.hypot(doc.width, doc.height) / 2;
        for (let i = 0; i < n; i++) {
          const a = (i / n) * Math.PI * 2 - Math.PI / 2;
          ctx.moveTo(doc.width / 2, doc.height / 2);
          ctx.lineTo(doc.width / 2 + Math.cos(a) * R, doc.height / 2 + Math.sin(a) * R);
        }
      } else if (s.symmetry.includes('h')) { ctx.moveTo(0, doc.height / 2); ctx.lineTo(doc.width, doc.height / 2); }
      ctx.stroke();
      ctx.setLineDash([]);
    }
  }

  // ---- 획을 긋는 동안: 아래/위 레이어를 한 번만 합성해 두고 재사용 (레이어가 많아도 펜이 밀리지 않게)
  let strokeCache = null;
  function splitFor(doc, layer) {
    if (!layer || doc.parentOf(layer) || app.preview) return null;
    const roots = doc.childrenOf(null);
    const i = roots.indexOf(layer);
    if (i < 0 || layer.clip || roots[i + 1]?.clip) return null;
    if (roots.some(l => l.kind === 'adjust' && l.visible)) return null;
    const above = roots.slice(i + 1);
    if (above.some(l => l.visible && l.blend !== 'source-over')) return null;
    return { below: roots.slice(0, i), above };
  }
  function drawWithStrokeCache(m, ro) {
    const doc = app.doc, live = app.live;
    if (!live || app.floating) { strokeCache = null; return false; }
    const split = splitFor(doc, live.layer);
    if (!split) return false;
    const W = canvas.width, H = canvas.height, mk = m.toString();
    if (!strokeCache || strokeCache.live !== live || strokeCache.m !== mk || strokeCache.W !== W || strokeCache.H !== H) {
      const mkc = () => { const c = document.createElement('canvas'); c.width = W; c.height = H; return c; };
      const below = strokeCache?.below?.width === W && strokeCache.below.height === H ? strokeCache.below : mkc();
      const above = strokeCache?.above?.width === W && strokeCache.above.height === H ? strokeCache.above : mkc();
      const bx = below.getContext('2d'), ax = above.getContext('2d');
      bx.setTransform(1, 0, 0, 1, 0, 0); bx.clearRect(0, 0, W, H);
      ax.setTransform(1, 0, 0, 1, 0, 0); ax.clearRect(0, 0, W, H);
      app.compositor.render(bx, doc, m, { ...ro, live: null, roots: split.below, paper: true });
      app.compositor.render(ax, doc, m, { ...ro, live: null, roots: split.above, paper: false });
      strokeCache = { live, m: mk, W, H, below, above };
    }
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.drawImage(strokeCache.below, 0, 0);
    app.compositor.render(ctx, doc, m, { ...ro, roots: [live.layer], paper: false });
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.drawImage(strokeCache.above, 0, 0);
    return true;
  }

  const present = () => {
    screen.setTransform(1, 0, 0, 1, 0, 0);
    screen.globalAlpha = 1;
    screen.globalCompositeOperation = 'copy';
    screen.drawImage(back, 0, 0);
    screen.globalCompositeOperation = 'source-over';
  };

  app._renderFn = () => {
    try { draw(); } finally { present(); }
  };

  const draw = () => {
    const doc = app.doc, view = app.view;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.globalAlpha = 1;
    ctx.globalCompositeOperation = 'source-over';
    ctx.fillStyle = stageBg;
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    if (!doc) return;
    const m = new DOMMatrix().scale(dpr).multiply(view.m);

    // 용지 그림자 + 투명 체크무늬
    ctx.setTransform(m);
    ctx.save();
    ctx.shadowColor = 'rgba(0,0,0,0.45)';
    ctx.shadowBlur = 18;
    ctx.fillStyle = '#ffffff';
    if (!doc.paper.visible) {
      const p = checkerPattern();
      p.setTransform(m.inverse());
      ctx.fillStyle = p;
    }
    ctx.fillRect(0, 0, doc.width, doc.height);
    ctx.restore();

    // 픽셀아트 작품은 확대·축소 모두 또렷하게 (흐리게 섞지 않음)
    const ro = { w: canvas.width, h: canvas.height, live: app.live, floating: app.floating, onion: true, smoothing: view.zoom < 2 && !doc.pixel?.art };
    if (!drawWithStrokeCache(m, ro)) app.compositor.render(ctx, doc, m, ro);

    drawGuides(m, view);
    drawSelection(m, view);

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.imageSmoothingEnabled = true;
    if (app.tool?.overlay) {
      ctx.save();
      try { app.tool.overlay(ctx, view); } catch (e) { console.error(e); }
      ctx.restore();
    }
    for (const fn of app.overlays) {
      ctx.save();
      try { fn(ctx, view, m); } catch (e) { console.error(e); }
      ctx.restore();
    }
  };

  return { resize, get dpr() { return dpr; } };
}
