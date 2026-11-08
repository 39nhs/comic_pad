// 내비게이터 패널, 작업 기록 패널, 참조 이미지 창
import { app } from '../app.js';
import { h, clamp, pickFile, blobToImage, rgbToHex, loadImage } from '../core/util.js';
import { icon } from './icons.js';
import { slider, row, button } from './controls.js';
import { server } from '../core/io.js';

// =====================================================================
// 내비게이터
// =====================================================================
export function navigatorPanel(el) {
  const cv = h('canvas.nav');
  const zoom = slider('확대', { min: 0.02, max: 16, log: true, step: 0.001, get: () => app.view.zoom, set: v => app.view.setZoom(v), format: v => Math.round(v * 1000) / 10 + '%' });
  const rot = slider('회전', { min: -180, max: 180, step: 1, get: () => { const a = ((app.view.angle % 360) + 540) % 360 - 180; return a; }, set: v => app.view.setAngle(v), format: v => Math.round(v) + '°' });
  el.append(h('div.panel-scroll', null, cv, zoom, rot,
    row(button('맞춤', () => app.run('view.fit')), button('가로 맞춤', () => app.run('view.fitWidth')), button('100%', () => app.run('view.actual')), button('좌우 반전', () => app.run('view.flip')))));
  let thumb = null, dirty = true, timer = 0;

  const layout = () => {
    const doc = app.doc;
    if (!doc) return null;
    const W = cv.clientWidth || 260, H = Math.min(260, Math.max(120, W * doc.height / doc.width));
    cv.style.height = H + 'px';
    const dpr = devicePixelRatio || 1;
    if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); dirty = true; }
    const s = Math.min(cv.width / doc.width, cv.height / doc.height);
    return { s, ox: (cv.width - doc.width * s) / 2, oy: (cv.height - doc.height * s) / 2 };
  };

  const draw = () => {
    if (!el.isConnected || el.hidden || !cv.offsetParent) return;
    const L = layout();
    if (!L) return;
    const doc = app.doc, x = cv.getContext('2d');
    if (dirty || !thumb) { thumb = app.compositor.flatten(doc, { scale: L.s, excludeDraft: false }); dirty = false; }
    x.clearRect(0, 0, cv.width, cv.height);
    x.drawImage(thumb, L.ox, L.oy, doc.width * L.s, doc.height * L.s);
    // 현재 화면 영역
    const v = app.view;
    const pts = [[0, 0], [v.w, 0], [v.w, v.h], [0, v.h]].map(([sx, sy]) => { const d = v.toDoc(sx, sy); return { x: L.ox + d.x * L.s, y: L.oy + d.y * L.s }; });
    x.strokeStyle = '#e8a33d'; x.lineWidth = 2 * (devicePixelRatio || 1);
    x.beginPath(); pts.forEach((p, i) => (i ? x.lineTo(p.x, p.y) : x.moveTo(p.x, p.y))); x.closePath(); x.stroke();
    zoom.sync(); rot.sync();
  };

  const go = (e) => {
    const L = layout();
    const r = cv.getBoundingClientRect(), dpr = devicePixelRatio || 1;
    const dx = ((e.clientX - r.left) * dpr - L.ox) / L.s, dy = ((e.clientY - r.top) * dpr - L.oy) / L.s;
    const s = app.view.toScreen(dx, dy);
    app.view.panBy(app.view.w / 2 - s.x, app.view.h / 2 - s.y);
  };
  cv.addEventListener('pointerdown', (e) => { cv.setPointerCapture(e.pointerId); go(e); cv.onpointermove = go; cv.onpointerup = () => { cv.onpointermove = null; }; });
  cv.style.touchAction = 'none';

  const later = () => { dirty = true; clearTimeout(timer); timer = setTimeout(draw, 500); };
  app.bus.on('layer:pixels', later);
  app.bus.on('layers:changed', later);
  app.bus.on('doc:changed', () => { dirty = true; draw(); });
  app.bus.on('view:changed', draw);
  new ResizeObserver(() => { dirty = true; draw(); }).observe(cv);
  new MutationObserver(draw).observe(el, { attributes: true, attributeFilter: ['hidden'] });
  draw();
}

// =====================================================================
// 작업 기록
// =====================================================================
export function historyPanel(el) {
  const list = h('div.hist-list');
  el.append(h('div.panel-scroll', null, h('div.help', null, '항목을 탭하면 그 시점으로 돌아갑니다 (다시 앞으로도 갈 수 있음).'), list));
  const render = () => {
    const H = app.history;
    if (!H) return;
    list.innerHTML = '';
    const goto = (n) => {
      while (H.undoStack.length > n && H.canUndo) H.undo();
      while (H.undoStack.length < n && H.canRedo) H.redo();
    };
    list.append(h(`button.hist${H.undoStack.length === 0 ? '.on' : ''}`, { type: 'button', onclick: () => goto(0) }, '(시작)'));
    H.undoStack.forEach((e, i) => list.append(h(`button.hist${i === H.undoStack.length - 1 ? '.on' : ''}`, { type: 'button', onclick: () => goto(i + 1) }, e.label || '작업')));
    [...H.redoStack].reverse().forEach((e, i) => list.append(h('button.hist.redo', { type: 'button', onclick: () => goto(H.undoStack.length + i + 1) }, e.label || '작업')));
    list.querySelector('.on')?.scrollIntoView({ block: 'nearest' });
  };
  app.bus.on('history:changed', render);
  render();
}

// =====================================================================
// 참조 이미지 창 (여러 개 띄울 수 있음)
// =====================================================================
let refCount = 0;
export async function openReference(img) {
  const host = document.getElementById('stage-overlay');
  const win = h('div.refwin', { style: { left: 80 + (refCount % 5) * 24 + 'px', top: 60 + (refCount % 5) * 24 + 'px' } });
  refCount++;
  const cv = h('canvas.ref-canvas');
  const st = { img: null, z: 1, ox: 0, oy: 0, flip: false, pick: false, gray: false };
  const btn = (ic, title, fn) => { const b = h('button.icon-btn', { type: 'button', title, html: icon(ic, 16) }); b.onclick = (e) => { e.stopPropagation(); fn(b); }; return b; };
  const head = h('div.ref-head', null,
    h('span.ref-title', null, '참조'),
    btn('image', '이미지 파일 열기', async () => { const f = await pickFile('image/*'); if (f) setImg(await blobToImage(f)); }),
    btn('copy', '현재 캔버스 캡처', () => setImg(app.compositor.flatten(app.doc, { scale: Math.min(1, 2000 / Math.max(app.doc.width, app.doc.height)) }))),
    btn('star', '에셋에서', () => pickAsset()),
    btn('eyedropper', '스포이트 (탭해서 색 집기)', (b) => { st.pick = !st.pick; b.classList.toggle('on', st.pick); }),
    btn('flip', '좌우 반전', () => { st.flip = !st.flip; draw(); }),
    btn('invert', '흑백 보기', () => { st.gray = !st.gray; draw(); }),
    btn('fit', '맞춤', () => fit()),
    btn('x', '닫기', () => win.remove()),
  );
  const grip = h('div.ref-grip', { title: '크기 조절' });
  win.append(head, cv, grip);
  host.append(win);

  const draw = () => {
    const dpr = devicePixelRatio || 1;
    const W = cv.clientWidth, H = cv.clientHeight;
    if (cv.width !== Math.round(W * dpr)) { cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); }
    const x = cv.getContext('2d');
    x.setTransform(1, 0, 0, 1, 0, 0);
    x.fillStyle = '#1e1e22'; x.fillRect(0, 0, cv.width, cv.height);
    if (!st.img) {
      x.fillStyle = '#888'; x.font = `${13 * dpr}px Malgun Gothic`; x.textAlign = 'center';
      x.fillText('위 버튼으로 이미지를 여세요', cv.width / 2, cv.height / 2);
      return;
    }
    x.setTransform(dpr * st.z * (st.flip ? -1 : 1), 0, 0, dpr * st.z, dpr * (st.flip ? W - st.ox : st.ox), dpr * st.oy);
    x.filter = st.gray ? 'grayscale(1)' : 'none';
    x.imageSmoothingQuality = 'high';
    x.drawImage(st.img, 0, 0);
    x.filter = 'none';
  };
  const fit = () => {
    if (!st.img) return;
    const W = cv.clientWidth, H = cv.clientHeight;
    st.z = Math.min(W / st.img.width, H / st.img.height);
    st.ox = (W - st.img.width * st.z) / 2; st.oy = (H - st.img.height * st.z) / 2;
    draw();
  };
  const setImg = (im) => { st.img = im; fit(); };
  const pickAsset = async () => {
    const items = (await server.list('assets')).filter(i => /\.(png|jpe?g|webp)$/i.test(i.name));
    if (!items.length) { app.toast('에셋이 없습니다'); return; }
    const v = await app.ui.form('참조할 에셋', [{ key: 'n', label: '파일', type: 'select', options: items.map(i => i.name), value: items[0].name }]);
    if (v) setImg(await loadImage(await server.src('assets', v.n)));
  };

  // 이동 / 확대 / 스포이트 / 창 이동 / 크기 조절
  const pts = new Map();
  let base = null;
  cv.addEventListener('pointerdown', (e) => {
    e.stopPropagation();
    cv.setPointerCapture(e.pointerId);
    pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (st.pick && st.img && pts.size === 1) {
      const r = cv.getBoundingClientRect(), dpr = devicePixelRatio || 1;
      const d = cv.getContext('2d').getImageData(Math.round((e.clientX - r.left) * dpr), Math.round((e.clientY - r.top) * dpr), 1, 1).data;
      app.setColor(rgbToHex(d[0], d[1], d[2]));
      return;
    }
    base = { z: st.z, ox: st.ox, oy: st.oy, pts: new Map([...pts].map(([k, v]) => [k, { ...v }])) };
  });
  cv.addEventListener('pointermove', (e) => {
    if (!pts.has(e.pointerId) || !base) return;
    pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
    const ids = [...pts.keys()].filter(k => base.pts.has(k));
    if (ids.length >= 2) {
      const [a, b] = ids;
      const d0 = Math.hypot(base.pts.get(a).x - base.pts.get(b).x, base.pts.get(a).y - base.pts.get(b).y);
      const d1 = Math.hypot(pts.get(a).x - pts.get(b).x, pts.get(a).y - pts.get(b).y);
      const f = d1 / Math.max(1, d0);
      const r = cv.getBoundingClientRect();
      const cx = (pts.get(a).x + pts.get(b).x) / 2 - r.left, cy = (pts.get(a).y + pts.get(b).y) / 2 - r.top;
      st.z = clamp(base.z * f, 0.02, 32);
      st.ox = cx - (cx - base.ox) * (st.z / base.z); st.oy = cy - (cy - base.oy) * (st.z / base.z);
    } else if (ids.length === 1) {
      const p0 = base.pts.get(ids[0]), p1 = pts.get(ids[0]);
      st.ox = base.ox + (st.flip ? -(p1.x - p0.x) : p1.x - p0.x); st.oy = base.oy + p1.y - p0.y;
    }
    draw();
  });
  const up = (e) => { pts.delete(e.pointerId); base = pts.size ? { z: st.z, ox: st.ox, oy: st.oy, pts: new Map([...pts].map(([k, v]) => [k, { ...v }])) } : null; };
  cv.addEventListener('pointerup', up);
  cv.addEventListener('pointercancel', up);
  cv.addEventListener('wheel', (e) => {
    e.preventDefault(); e.stopPropagation();
    const r = cv.getBoundingClientRect(), cx = e.clientX - r.left, cy = e.clientY - r.top;
    const f = e.deltaY < 0 ? 1.15 : 1 / 1.15, z = clamp(st.z * f, 0.02, 32);
    st.ox = cx - (cx - st.ox) * (z / st.z); st.oy = cy - (cy - st.oy) * (z / st.z); st.z = z;
    draw();
  }, { passive: false });
  const dragWin = (handle, fn) => handle.addEventListener('pointerdown', (e) => {
    if (e.target.closest('button')) return;
    e.stopPropagation();
    handle.setPointerCapture(e.pointerId);
    const sx = e.clientX, sy = e.clientY, r0 = { l: win.offsetLeft, t: win.offsetTop, w: win.offsetWidth, h: win.offsetHeight };
    handle.onpointermove = (ev) => { fn(r0, ev.clientX - sx, ev.clientY - sy); draw(); };
    handle.onpointerup = () => { handle.onpointermove = null; };
  });
  dragWin(head, (r0, dx, dy) => { win.style.left = r0.l + dx + 'px'; win.style.top = r0.t + dy + 'px'; });
  dragWin(grip, (r0, dx, dy) => { win.style.width = Math.max(180, r0.w + dx) + 'px'; win.style.height = Math.max(140, r0.h + dy) + 'px'; });
  new ResizeObserver(draw).observe(cv);

  if (img) setImg(img);
  else draw();
  return win;
}
