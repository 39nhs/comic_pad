// 히스토리를 남기는 문서/레이어 조작 모음. 플러그인도 이 함수를 쓰면 실행취소가 된다.
import { app } from '../app.js';
import { Doc, Layer } from './doc.js';
import { createCanvas, rectClamp } from './util.js';
import { alphaBounds } from './raster.js';

const H = () => app.history;

export function setDoc(doc) {
  app.doc = doc;
  app.live = null;
  app.floating = null;
  app.history.clear();
  app.bus.emit('doc:changed', doc);
  app.view.fit(doc);
  app.requestRender();
}

export function newDocument(w, h, o = {}) {
  const doc = new Doc(w, h, o);
  const l = doc.createLayer({ name: o.firstLayerName || '레이어 1' });
  doc.layers.push(l);
  doc.activeId = l.id;
  setDoc(doc);
  return doc;
}

/** 레이어가 너무 많아 브라우저 메모리가 모자랄 것 같으면 한 번 알려 줌 (큰 원고) */
function memoryCheck(doc) {
  const px = doc.width * doc.height * 4;
  const n = doc.layers.filter(l => !l.isFolder && l.kind !== 'adjust').length + doc.layers.filter(l => l.mask).length + 1;
  const gb = (px * n * 2) / 1024 ** 3;   // 레이어 + 실행취소용 사본 등 대략 2배
  if (gb > 3 && !doc._memWarned) {
    doc._memWarned = true;
    app.toast(`레이어 메모리가 약 ${gb.toFixed(1)}GB 입니다. 느려지거나 창이 닫힐 수 있으니 다 그린 레이어는 병합하거나, 원고 설정에서 해상도를 낮춰 보세요`, 'warn', { duration: 9000 });
  }
}

export function addLayer(o = {}, index) {
  const doc = app.doc;
  const isInst = o instanceof Layer;
  const act = doc.active;
  let i = index, parent = isInst ? o.parent : (o.parent ?? null);
  if (index == null) {
    // 펼쳐진 폴더를 고른 상태면 그 폴더 맨 위에, 아니면 현재 레이어 바로 위(같은 폴더)에
    if (act?.isFolder && act.expanded) { i = doc.indexOf(act); if (!isInst && !('parent' in o)) parent = act.id; }
    else { i = act ? doc.indexOf(act) + 1 : doc.layers.length; if (!isInst && !('parent' in o)) parent = act ? doc.parentOf(act) : null; }
  }
  const layer = isInst ? o : doc.createLayer({ name: o.name || nextLayerName(), ...o, parent });
  memoryCheck(doc);
  const prevActive = doc.activeId;
  doc._insert(layer, i);
  doc.setActive(layer);
  H().push({
    label: '레이어 추가',
    undo: () => { doc._remove(layer); doc.activeId = prevActive; app.bus.emit('layers:changed'); },
    redo: () => { doc._insert(layer, i); doc.setActive(layer); },
  });
  return layer;
}

function nextLayerName() {
  let n = app.doc.layers.length + 1;
  const names = new Set(app.doc.layers.map(l => l.name));
  while (names.has(`레이어 ${n}`)) n++;
  return `레이어 ${n}`;
}

export function deleteLayer(layer = app.doc.active) {
  const doc = app.doc;
  if (!layer) return;
  if (layer.isFolder) {
    const gone = new Set([layer, ...doc.descendants(layer)]);
    if (!doc.layers.some(l => !gone.has(l) && !l.isFolder)) { app.toast('그림 레이어가 하나는 남아야 합니다'); return; }
    H().batch('폴더 삭제', () => { for (const l of [...gone].reverse()) deleteSingle(l); });
    return;
  }
  if (doc.layers.filter(l => !l.isFolder).length <= 1) { app.toast('마지막 레이어는 삭제할 수 없습니다'); return; }
  deleteSingle(layer);
}

function deleteSingle(layer) {
  const doc = app.doc;
  const i = doc.indexOf(layer);
  doc._remove(layer);
  app.bus.emit('layers:changed');
  H().push({
    label: '레이어 삭제',
    undo: () => { doc._insert(layer, i); doc.setActive(layer); },
    redo: () => { doc._remove(layer); app.bus.emit('layers:changed'); },
  });
}

export function duplicateLayer(layer = app.doc.active) {
  if (!layer) return;
  const doc = app.doc;
  if (!layer.isFolder) {
    const copy = layer.clone();
    copy.parent = layer.parent;
    return addLayer(copy, doc.indexOf(layer) + 1);
  }
  return H().batch('폴더 복제', () => {
    const map = new Map();
    const fcopy = layer.clone(layer.name + ' 복사');
    fcopy.parent = layer.parent;
    map.set(layer.id, fcopy.id);
    const desc = doc.descendants(layer).sort((a, b) => doc.indexOf(a) - doc.indexOf(b));
    const copies = desc.map(d => { const c = d.clone(d.name); map.set(d.id, c.id); return [d, c]; });
    for (const [d, c] of copies) c.parent = map.get(d.parent) ?? fcopy.id;
    let at = doc.indexOf(layer) + 1;
    for (const [, c] of copies) addLayer(c, at++);
    addLayer(fcopy, at);
    return fcopy;
  });
}

/** 새 폴더 (현재 레이어 위치에) */
export function addFolder(name = '폴더', meta = null) {
  return addLayer({ name, kind: 'folder', meta });
}

/** 현재 레이어를 새 폴더에 넣기 */
export function groupIntoFolder(layer = app.doc.active, name = '폴더') {
  const doc = app.doc;
  if (!layer) return;
  return H().batch('폴더로 묶기', () => {
    const f = addLayer({ name, kind: 'folder', parent: doc.parentOf(layer) }, doc.indexOf(layer) + 1);
    setLayerProps(layer, { parent: f.id }, '폴더에 넣기');
    doc.setActive(f);
    return f;
  });
}

/** layer 를 target 의 위(above)/아래(below)/안(inside, 폴더 맨 위)으로 옮긴다 */
export function moveRelative(layer, target, pos) {
  const doc = app.doc;
  if (!layer || !target || layer === target) return;
  if (layer.isFolder && doc.isInside(target, layer)) { app.toast('폴더를 자기 안으로 넣을 수 없습니다'); return; }
  if (pos === 'inside' && !target.isFolder) pos = 'above';
  H().batch('레이어 이동', () => {
    const newParent = pos === 'inside' ? target.id : doc.parentOf(target);
    if (doc.parentOf(layer) !== newParent) setLayerProps(layer, { parent: newParent }, '폴더 이동');
    const from = doc.indexOf(layer), ti = doc.indexOf(target);
    const to = pos === 'above' ? ti + (from < ti ? 0 : 1) : ti - (from < ti ? 1 : 0);
    moveLayer(layer, to);
    if (pos === 'inside' && !target.expanded) setLayerProps(target, { expanded: true }, '폴더 펼치기');
  });
}

/** 같은 폴더 안에서 한 칸 위/아래 */
export function stepLayer(layer, dir) {
  const doc = app.doc;
  const sib = doc.childrenOf(doc.parentOf(layer) ? doc.byId(doc.parentOf(layer)) : null);
  const i = sib.indexOf(layer), t = sib[i + dir];
  if (!t) {
    // 폴더 끝이면 폴더 밖으로
    const p = doc.parentOf(layer);
    if (p) moveRelative(layer, doc.byId(p), dir > 0 ? 'above' : 'below');
    return;
  }
  moveRelative(layer, t, dir > 0 ? 'above' : 'below');
}

/** 폴더를 하나의 그림 레이어로 합치기 */
export function mergeFolder(folder = app.doc.active) {
  const doc = app.doc;
  if (!folder?.isFolder) return;
  const tmp = createCanvas(doc.width, doc.height);
  app.compositor.render(tmp.getContext('2d'), doc, new DOMMatrix(), { paper: false, roots: [folder] });
  H().batch('폴더 합치기', () => {
    const l = addLayer({ name: folder.name, parent: doc.parentOf(folder) }, doc.indexOf(folder) + 1);
    l.ctx.drawImage(tmp, 0, 0);
    l.touch();
    deleteLayer(folder);
    doc.setActive(l);
  });
}


// ---------------------------------------------------------------------
// 여러 레이어 선택 (레이어 패널에서 Ctrl/Shift+탭, 또는 '여러 개' 버튼)
// ---------------------------------------------------------------------
export function selectedLayers() {
  const doc = app.doc;
  const ids = doc.multi;
  const list = ids?.size ? doc.layers.filter(l => ids.has(l.id)) : [];
  return list.length ? list : doc.active ? [doc.active] : [];
}

/** 선택 안에서 다른 선택 폴더에 들어 있는 것은 빼고 (폴더째 처리) */
const topLevel = (list) => list.filter(l => !list.some(o => o !== l && o.isFolder && app.doc.isInside(l, o)));

export function clearMulti() {
  if (!app.doc.multi?.size) return;
  app.doc.multi = null;
  app.bus.emit('layers:changed');
}

export function deleteLayers(list = selectedLayers()) {
  const doc = app.doc;
  const top = topLevel(list);
  const gone = new Set(top.flatMap(l => [l, ...(l.isFolder ? doc.descendants(l) : [])]));
  if (!doc.layers.some(l => !gone.has(l) && !l.isFolder)) { app.toast('그림 레이어가 하나는 남아야 합니다'); return; }
  H().batch(`레이어 ${top.length}개 삭제`, () => { for (const l of top) deleteLayer(l); });
  doc.multi = null;
  app.bus.emit('layers:changed');
}

export function groupLayers(list = selectedLayers(), name = '폴더') {
  const doc = app.doc;
  const top = topLevel(list).sort((a, b) => doc.indexOf(a) - doc.indexOf(b));
  if (!top.length) return;
  const upper = top[top.length - 1];
  return H().batch('폴더로 묶기', () => {
    const f = addLayer({ name, kind: 'folder', parent: doc.parentOf(upper) }, doc.indexOf(upper) + 1);
    for (const l of top) setLayerProps(l, { parent: f.id }, '폴더에 넣기');
    doc.multi = null;
    doc.setActive(f);
    return f;
  });
}

export function mergeLayers(list = selectedLayers()) {
  const doc = app.doc;
  const top = topLevel(list).sort((a, b) => doc.indexOf(a) - doc.indexOf(b));
  if (top.length < 2) { if (top[0]?.isFolder) mergeFolder(top[0]); else mergeDown(top[0]); return; }
  const leaves = new Set(top.flatMap(l => l.isFolder ? doc.descendants(l).filter(d => !d.isFolder && d.visible) : [l]));
  const tmp = createCanvas(doc.width, doc.height);
  app.compositor.render(tmp.getContext('2d'), doc, new DOMMatrix(), { paper: false, only: leaves });
  const upper = top[top.length - 1];
  H().batch(`레이어 ${top.length}개 병합`, () => {
    const l = addLayer({ name: upper.name, parent: doc.parentOf(upper) }, doc.indexOf(upper) + 1);
    l.ctx.drawImage(tmp, 0, 0);
    l.touch();
    for (const x of top) deleteLayer(x);
    doc.multi = null;
    doc.setActive(l);
  });
}

export function setLayersVisible(list = selectedLayers(), v) {
  if (v == null) v = !list.every(l => l.visible);
  H().batch(v ? '레이어 보이기' : '레이어 숨기기', () => { for (const l of list) if (l.visible !== v) setLayerProps(l, { visible: v }, '표시'); });
}

export function moveLayer(layer, to) {
  const doc = app.doc;
  const from = doc.indexOf(layer);
  to = Math.max(0, Math.min(doc.layers.length - 1, to));
  if (from === to || from < 0) return;
  const mv = (a, b) => {
    doc.layers.splice(a, 1);
    doc.layers.splice(b, 0, layer);
    app.bus.emit('layers:changed');
    app.markModified();
    app.requestRender();
  };
  mv(from, to);
  H().push({ label: '레이어 순서', undo: () => mv(to, from), redo: () => mv(from, to) });
}

export function setLayerProps(layer, props, label = '레이어 속성') {
  const before = {};
  for (const k of Object.keys(props)) before[k] = k === 'meta' && layer.meta ? structuredClone(layer.meta) : layer[k];
  const apply = (p) => { layer.setProps(p); app.bus.emit('layers:changed'); app.markModified(); app.requestRender(); };
  apply(props);
  H().push({ label, undo: () => apply(before), redo: () => apply(props) });
}

/** 슬라이더처럼 연속 변경: begin 시 이전 값 저장, end 시 히스토리 */
export function beginPropEdit(layer, keys) {
  const before = {};
  for (const k of keys) before[k] = layer[k];
  return {
    end(label = '레이어 속성') {
      const after = {};
      let changed = false;
      for (const k of keys) { after[k] = layer[k]; if (after[k] !== before[k]) changed = true; }
      if (!changed) return;
      const apply = (p) => { layer.setProps(p); app.bus.emit('layers:changed'); app.requestRender(); };
      H().push({ label, undo: () => apply(before), redo: () => apply(after) });
    },
  };
}

export function mergeDown(layer = app.doc.active) {
  const doc = app.doc;
  if (layer?.isFolder) { mergeFolder(layer); return; }
  const sib = doc.childrenOf(doc.parentOf(layer) ? doc.byId(doc.parentOf(layer)) : null);
  const below = sib[sib.indexOf(layer) - 1];
  if (!below) { app.toast('아래에 병합할 레이어가 없습니다'); return; }
  if (below.isFolder) { app.toast('아래가 폴더입니다. 폴더 안으로 옮기거나 폴더를 먼저 합치세요'); return; }
  H().batch('아래 레이어와 병합', () => {
    H().pixels(below, '병합', (ctx) => {
      // 클리핑/컬러 등을 반영해 두 레이어만 합성
      const tmp = createCanvas(doc.width, doc.height);
      const t = tmp.getContext('2d');
      app.compositor.render(t, doc, new DOMMatrix(), { paper: false, only: new Set([below, layer]) });
      ctx.save();
      ctx.globalCompositeOperation = 'copy';
      ctx.drawImage(tmp, 0, 0);
      ctx.restore();
    });
    if (below.opacity !== 1 || below.blend !== 'source-over' || below.tint) {
      setLayerProps(below, { opacity: 1, blend: 'source-over', tint: null });
    }
    deleteLayer(layer);
    doc.setActive(below);
  });
}

export function flattenVisible() {
  const doc = app.doc;
  const flat = app.compositor.flatten(doc, { paper: false, excludeDraft: false });
  H().batch('표시 레이어 결합', () => {
    const l = addLayer({ name: '결합', parent: null }, doc.layers.length);
    l.ctx.drawImage(flat, 0, 0);
    for (const x of [...doc.layers]) if (x !== l && x.visible && doc.layers.includes(x) && !doc.parentOf(x)) deleteLayer(x);
    doc.setActive(l);
  });
}

export function clearLayer(layer = activeTarget()) {
  if (layer?.kind === 'vector') { import('./vector.js').then(V => V.setStrokes(layer, [], '벡터 지우기')); return; }
  if (!checkEditable(layer)) return;
  const sel = app.doc.selection;
  H().pixels(layer, '지우기', (ctx) => {
    if (sel.active) {
      ctx.save();
      ctx.globalCompositeOperation = 'destination-out';
      ctx.drawImage(sel.mask, 0, 0);
      ctx.restore();
      return sel.bounds;
    }
    ctx.clearRect(0, 0, layer.canvas.width, layer.canvas.height);
  }, sel.active ? sel.bounds : null);
}

export function fillSelection(color = app.color.fg, layer = activeTarget()) {
  if (!checkEditable(layer)) return;
  const sel = app.doc.selection;
  H().pixels(layer, '채우기', (ctx) => {
    ctx.save();
    if (layer.alphaLock) ctx.globalCompositeOperation = 'source-atop';
    if (sel.active) {
      const tmp = createCanvas(layer.canvas.width, layer.canvas.height);
      const t = tmp.getContext('2d');
      t.drawImage(sel.mask, 0, 0);
      t.globalCompositeOperation = 'source-in';
      t.fillStyle = color;
      t.fillRect(0, 0, tmp.width, tmp.height);
      ctx.drawImage(tmp, 0, 0);
    } else {
      ctx.fillStyle = color;
      ctx.fillRect(0, 0, layer.canvas.width, layer.canvas.height);
    }
    ctx.restore();
  }, sel.active ? sel.bounds : null);
}

export function flipLayer(axis = 'h', layer = activeTarget()) {
  if (!checkEditable(layer)) return;
  H().pixels(layer, axis === 'h' ? '좌우 반전' : '상하 반전', (ctx) => {
    const tmp = createCanvas(layer.canvas.width, layer.canvas.height);
    tmp.getContext('2d').drawImage(layer.canvas, 0, 0);
    ctx.save();
    ctx.clearRect(0, 0, tmp.width, tmp.height);
    if (axis === 'h') { ctx.translate(tmp.width, 0); ctx.scale(-1, 1); } else { ctx.translate(0, tmp.height); ctx.scale(1, -1); }
    ctx.drawImage(tmp, 0, 0);
    ctx.restore();
  });
}

/** 픽셀 필터: fn(ImageData) 를 레이어(선택 영역)에 적용 */
export function applyPixelFilter(label, fn, layer = activeTarget()) {
  if (!checkEditable(layer)) return;
  const sel = app.doc.selection;
  const r = rectClamp(sel.active ? sel.bounds : { x: 0, y: 0, w: layer.canvas.width, h: layer.canvas.height }, layer.canvas.width, layer.canvas.height);
  if (!r) return;
  H().pixels(layer, label, (ctx) => {
    const img = ctx.getImageData(r.x, r.y, r.w, r.h);
    const orig = sel.active ? new Uint8ClampedArray(img.data) : null;
    fn(img);
    if (sel.active) {
      const m = sel.mask.getContext('2d').getImageData(r.x, r.y, r.w, r.h).data;
      for (let i = 0; i < m.length; i += 4) {
        const k = m[i + 3] / 255;
        if (k < 1) for (let c = 0; c < 4; c++) img.data[i + c] = orig[i + c] + (img.data[i + c] - orig[i + c]) * k;
      }
    }
    ctx.putImageData(img, r.x, r.y);
  }, r);
}

/** 캔버스(ctx.filter) 필터 */
export function applyCanvasFilter(label, filter, layer = activeTarget()) {
  applyCanvasOp(label, (t, src) => { t.filter = filter; t.drawImage(src, 0, 0); t.filter = 'none'; }, layer);
}

/** drawFn(tmpCtx, 원본캔버스) 로 처리한 결과를 레이어(선택 영역 안)에 적용 */
export function applyCanvasOp(label, drawFn, layer = activeTarget()) {
  if (!checkEditable(layer)) return;
  const sel = app.doc.selection;
  H().pixels(layer, label, (ctx) => {
    const tmp = createCanvas(layer.canvas.width, layer.canvas.height);
    const t = tmp.getContext('2d');
    drawFn(t, layer.canvas, tmp);
    t.setTransform(1, 0, 0, 1, 0, 0);
    t.globalAlpha = 1;
    if (sel.active) {
      t.globalCompositeOperation = 'destination-in';
      t.drawImage(sel.mask, 0, 0);
      ctx.save();
      ctx.globalCompositeOperation = 'destination-out';
      ctx.drawImage(sel.mask, 0, 0);
      ctx.restore();
      ctx.drawImage(tmp, 0, 0);
    } else {
      ctx.save();
      ctx.globalCompositeOperation = 'copy';
      ctx.drawImage(tmp, 0, 0);
      ctx.restore();
    }
  });
}

export function resizeCanvas(w, h, anchorX = 0.5, anchorY = 0.5) {
  const doc = app.doc;
  const ow = doc.width, oh = doc.height;
  const dx = Math.round((w - ow) * anchorX), dy = Math.round((h - oh) * anchorY);
  const olds = doc.layers.map(l => ({ c: l.canvas, m: l.mask }));
  const resizeTo = (W, HH, ox, oy, sources) => {
    doc.width = W; doc.height = HH;
    doc.layers.forEach((l, i) => {
      if (!l.isFolder && l.kind !== 'adjust') {
        const c = createCanvas(W, HH);
        c.getContext('2d').drawImage(sources[i].c, ox, oy);
        l.canvas = c; l.ctx = c.getContext('2d');
      }
      if (sources[i].m) {
        const mk = createCanvas(W, HH), x = mk.getContext('2d');
        x.fillStyle = '#fff'; x.fillRect(0, 0, W, HH);
        x.clearRect(ox, oy, sources[i].m.width, sources[i].m.height);
        x.drawImage(sources[i].m, ox, oy);
        l.mask = mk;
      }
      l.version++;
    });
    doc.selection.clear(true);
    app.bus.emit('doc:resized', doc);
    app.bus.emit('layers:changed');
    app.requestRender();
  };
  resizeTo(w, h, dx, dy, olds);
  const news = doc.layers.map(l => ({ c: l.canvas, m: l.mask }));
  H().push({
    label: '캔버스 크기',
    bytes: w * h * 4 * doc.layers.length,
    undo: () => {
      doc.width = ow; doc.height = oh;
      doc.layers.forEach((l, i) => { l.canvas = olds[i].c; l.ctx = olds[i].c.getContext('2d'); l.mask = olds[i].m; l.version++; });
      doc.selection.clear(true);
      app.bus.emit('doc:resized', doc);
      app.bus.emit('layers:changed');
      app.requestRender();
    },
    redo: () => {
      doc.width = w; doc.height = h;
      doc.layers.forEach((l, i) => { l.canvas = news[i].c; l.ctx = news[i].c.getContext('2d'); l.mask = news[i].m; l.version++; });
      doc.selection.clear(true);
      app.bus.emit('doc:resized', doc);
      app.bus.emit('layers:changed');
      app.requestRender();
    },
  });
}

export function checkEditable(layer = app.doc?.active, opt = {}) {
  if (!layer) { app.toast('레이어가 없습니다'); return false; }
  if (layer.isFolder) { app.toast('폴더에는 그릴 수 없습니다. 안의 레이어를 고르세요', 'warn'); return false; }
  if (layer.kind === 'adjust') { app.toast('조정 레이어에는 그릴 수 없습니다 (마스크 편집은 가능)', 'warn'); return false; }
  if (layer.kind === 'bubble' && Array.isArray(layer.meta?.bubbles) && !opt.bubble) { app.toast('말풍선 레이어는 말풍선 도구로 고치세요 (직접 그리려면 레이어 메뉴 > 말풍선을 그림으로)', 'warn'); return false; }
  if (layer.kind === 'vector' && !opt.vector) { app.toast('벡터 레이어에는 펜·지우개·변형만 쓸 수 있습니다 (레이어 메뉴 > 벡터를 일반 레이어로)', 'warn'); return false; }
  if (layer.locked) { app.toast('잠긴 레이어입니다', 'warn'); return false; }
  if (!layer.visible) { app.toast('숨겨진 레이어에는 그릴 수 없습니다', 'warn'); return false; }
  return true;
}

/** 이미지를 새 레이어로 넣고 변형 모드로 */
export function placeImage(img, name = '이미지', { fit = true, transform = true } = {}) {
  const doc = app.doc;
  let w = img.width, h = img.height;
  if (fit && (w > doc.width || h > doc.height)) {
    const s = Math.min(doc.width / w, doc.height / h) * 0.9;
    w *= s; h *= s;
  }
  const c = app.view.toDoc(app.view.w / 2, app.view.h / 2);
  const x = Math.round(Math.max(0, Math.min(doc.width - w, c.x - w / 2)));
  const y = Math.round(Math.max(0, Math.min(doc.height - h, c.y - h / 2)));
  const layer = addLayer({ name });
  layer.ctx.imageSmoothingQuality = 'high';
  layer.ctx.drawImage(img, x, y, w, h);
  layer.touch();
  if (transform) {
    app.setTool('transform');
    app.tool.start?.();
  }
  return layer;
}

/** 현재 레이어(또는 선택영역)의 내용 경계 */
export function contentBounds(layer = app.doc.active) {
  const sel = app.doc.selection;
  if (sel.active) return sel.bounds;
  return alphaBounds(layer.canvas);
}

/** 지금 그릴 대상 (마스크 편집 중이면 마스크) */
export function activeTarget() {
  return app.doc?.active?.target ?? null;
}

// ---- 레이어 마스크 ----
function maskHistory(layer, label, before, after) {
  const apply = (st) => {
    layer.mask = st.mask; layer.maskOn = st.maskOn; layer.editMask = st.editMask;
    app.bus.emit('layers:changed'); layer.touch();
  };
  H().push({ label, bytes: layer.canvas.width * layer.canvas.height * 4, undo: () => apply(before), redo: () => apply(after) });
}

const maskState = (l) => ({ mask: l.mask, maskOn: l.maskOn, editMask: l.editMask });

/** 마스크 만들기: 선택 영역이 있으면 그 부분만 보이게, 없으면 전부 보이게 */
export function addMask(layer = app.doc.active) {
  if (!layer) return;
  const before = maskState(layer);
  const sel = app.doc.selection;
  layer.createMask(sel.active ? sel.mask : null);
  layer.editMask = true;
  maskHistory(layer, '마스크 추가', before, maskState(layer));
  app.bus.emit('layers:changed');
  layer.touch();
  app.toast('마스크 편집 중: 지우개 = 가리기, 브러시 = 보이기');
}

export function deleteMask(layer = app.doc.active) {
  if (!layer?.mask) return;
  const before = maskState(layer);
  layer.mask = null; layer.editMask = false;
  maskHistory(layer, '마스크 삭제', before, maskState(layer));
  app.bus.emit('layers:changed');
  layer.touch();
}

/** 마스크를 그림에 적용하고 마스크 제거 */
export function applyMask(layer = app.doc.active) {
  if (!layer?.mask) return;
  H().batch('마스크 적용', () => {
    H().pixels(layer, '마스크 적용', (ctx) => {
      ctx.save();
      ctx.globalCompositeOperation = 'destination-in';
      ctx.drawImage(layer.mask, 0, 0);
      ctx.restore();
    });
    deleteMask(layer);
  });
}
