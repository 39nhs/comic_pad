// 명령, 메뉴, 단축키 정의
import { app } from './app.js';
import * as ops from './core/ops.js';
import { saveProject, exportImage, saveAsset, extractContent, server, downloadProject } from './core/io.js';
import { canvasToBlob, blobToImage } from './core/util.js';
import { activePreset } from './tools/brush.js';
import { selectionHistory } from './tools/select.js';
import { newDocDialog, openDialog, saveAsDialog, exportDialog, importImageDialog, recoverDialog, openLocalFile, versionsDialog, storageDialog, driveImportDialog } from './ui/files.js';
import { form } from './ui/dialogs.js';
import { pageFrameDialog } from './tools/comic.js';
import { newRoughLayer, setRough, setRoughColor, ROUGH_COLORS, defaultRoughColor } from './core/rough.js';

const C = (id, label, run, key, extra = {}) => app.registerCommand(id, { label, run, key, ...extra });

export const DEFAULT_KEYS = {
  'file.new': 'ctrl+n', 'file.open': 'ctrl+o', 'file.save': 'ctrl+s', 'file.saveAs': 'ctrl+shift+s', 'file.export': 'ctrl+shift+e',
  'edit.undo': 'ctrl+z', 'edit.redo': 'ctrl+y', 'edit.redo2': 'ctrl+shift+z',
  'edit.cut': 'ctrl+x', 'edit.copy': 'ctrl+c', 'edit.paste': 'ctrl+v', 'edit.clear': 'delete', 'edit.clear2': 'backspace',
  'edit.fill': 'alt+backspace', 'edit.transform': 'ctrl+t',
  'select.all': 'ctrl+a', 'select.none': 'ctrl+d', 'select.invert': 'ctrl+shift+i',
  'layer.new': 'ctrl+shift+n', 'layer.duplicate': 'ctrl+j', 'layer.mergeDown': 'ctrl+e', 'layer.clip': 'ctrl+alt+g',
  'view.fit': 'ctrl+0', 'view.actual': 'ctrl+alt+0', 'view.zoomIn': 'ctrl+=', 'view.zoomOut': 'ctrl+-',
  'view.zoomIn2': '=', 'view.zoomOut2': '-', 'view.rotL': 'ctrl+[', 'view.rotR': 'ctrl+]', 'view.flip': 'shift+h',
  'view.toggleUI': 'tab', 'brush.sizeDown': '[', 'brush.sizeUp': ']', 'color.swap': 'x',
  'tool.commit': 'enter', 'tool.escape': 'escape', 'help.keys': 'f1',
};

function currentBrushCategory() {
  return app.toolId === 'eraser' ? 'eraser' : 'brush';
}

export function registerCommands() {
  // ---- 파일 ----
  C('file.new', '새 캔버스…', () => newDocDialog());
  C('file.open', '프로젝트 열기…', () => openDialog());
  C('file.openLocal', '파일에서 열기 (.cpad / .psd / 이미지)…', () => openLocalFile());
  C('file.save', '저장', () => saveProject());
  C('file.saveAs', '다른 이름으로 저장…', () => saveAsDialog());
  C('file.download', '프로젝트 파일 다운로드', () => downloadProject());
  C('file.import', '이미지 가져오기 (새 레이어)…', () => importImageDialog());
  C('file.export', '이미지 내보내기…', () => exportDialog());
  C('file.exportQuick', 'PNG 빠른 내보내기', () => exportImage({ format: 'png' }));
  C('file.recover', '자동 저장 복구…', () => recoverDialog());
  C('file.versions', '버전 기록 (이전 저장본)…', () => versionsDialog());
  C('file.storage', '저장 위치 · 구글 드라이브 (서버 / 폴더 / 이 기기)…', () => storageDialog());
  C('file.drivePull', '구글 드라이브에서 가져오기…', () => driveImportDialog());
  C('file.folder', 'data 폴더 열기', () => server.openFolder('projects'));
  C('file.exportsFolder', '내보낸 이미지 폴더 열기', () => server.openFolder('exports'));

  // ---- 편집 ----
  C('edit.undo', '실행 취소', () => {
    if (app.floating && app.tool?.cancel) { app.tool.cancel(); return; }
    app.history.undo();
  });
  C('edit.redo', '다시 실행', () => app.history.redo());
  C('edit.redo2', '다시 실행', () => app.history.redo(), null, { hidden: true });
  C('edit.copy', '복사', () => copySelection(false));
  C('edit.cut', '잘라내기', () => copySelection(true));
  C('edit.paste', '붙여넣기', () => paste());
  C('edit.clear', '지우기', () => ops.clearLayer());
  C('edit.clear2', '지우기', () => ops.clearLayer(), null, { hidden: true });
  C('edit.fill', '선택 영역 채우기 (전경색)', () => ops.fillSelection());
  C('edit.transform', '이동/변형', () => { app.setTool('transform'); app.tool.start?.(); });
  C('edit.flipH', '레이어 좌우 반전', () => ops.flipLayer('h'));
  C('edit.flipV', '레이어 상하 반전', () => ops.flipLayer('v'));
  C('edit.canvasSize', '캔버스 크기…', async () => {
    const d = app.doc;
    const v = await form('캔버스 크기', [
      { key: 'w', label: '가로(px)', type: 'number', value: d.width },
      { key: 'h', label: '세로(px)', type: 'number', value: d.height },
      { key: 'anchor', label: '기준', type: 'segment', options: [['top', '위 고정 (아래로 늘리기)'], ['center', '가운데'], ['bottom', '아래 고정']], value: 'top' },
      { key: 'info', type: 'info', label: '웹툰은 "위 고정"으로 세로를 늘리면 아래로 이어 그릴 수 있습니다.' },
    ], { okLabel: '적용' });
    if (!v) return;
    const ay = v.anchor === 'top' ? 0 : v.anchor === 'bottom' ? 1 : 0.5;
    ops.resizeCanvas(Math.round(v.w), Math.round(v.h), 0.5, ay);
  });
  C('edit.extendDown', '웹툰: 아래로 캔버스 늘리기', () => {
    const d = app.doc;
    ops.resizeCanvas(d.width, d.height + Math.round(d.width * 1.6), 0.5, 0);
    app.toast(`세로 ${d.height}px`);
  });

  // ---- 레이어 ----
  C('layer.new', '새 레이어', () => ops.addLayer());
  C('layer.newRough', '새 러프 레이어 (의견 색, 밑그림)', () => {
    newRoughLayer();
    app.settings.activePreset.brush = 'rough';
    app.setTool('brush');
    app.bus.emit('brush:changed', activePreset('brush'));
  });
  C('layer.newLine', '새 펜선 레이어', () => {
    ops.addLayer({ name: '펜선' });
    app.settings.activePreset.brush = 'gpen';
    app.setTool('brush');
    app.bus.emit('brush:changed', activePreset('brush'));
  });
  C('layer.newVector', '새 벡터 레이어 (선 굵기·지우기 나중에 수정)', () => { ops.addLayer({ name: '벡터 펜선', kind: 'vector', meta: { strokes: [] } }); app.setTool('brush'); });
  C('vector.width', '벡터: 선 굵기 일괄 조정…', async () => {
    const l = app.doc.active;
    if (l?.kind !== 'vector') { app.toast('벡터 레이어를 고르세요'); return; }
    const v = await form('선 굵기 일괄 조정', [{ key: 'k', label: '배율 (%)', type: 'number', value: 80 }, { key: 'info', type: 'info', label: '100보다 작으면 가늘게, 크면 굵게. 이 레이어의 모든 선에 적용됩니다.' }], { okLabel: '적용' });
    if (v) { const V = await import('./core/vector.js'); V.scaleWidths(l, v.k / 100); }
  });
  C('vector.color', '벡터: 선 색을 지금 색으로', async () => { const l = app.doc.active; if (l?.kind === 'vector') (await import('./core/vector.js')).recolor(l, app.color.fg); });
  C('bubble.toRaster', '말풍선을 그림으로 (직접 그리기 가능, 다시 편집 불가)', () => { const l = app.doc.active; if (l?.kind === 'bubble') ops.setLayerProps(l, { meta: null }, '말풍선을 그림으로'); });
  C('vector.toRaster', '벡터를 일반 레이어로', () => { const l = app.doc.active; if (l?.kind === 'vector') ops.setLayerProps(l, { kind: 'raster', meta: null }, '일반 레이어로'); });
  C('layer.newColor', '새 채색 레이어 (아래 레이어에 클리핑)', () => ops.addLayer({ name: '채색', clip: true }));
  C('layer.rough', '러프 전용 레이어로 지정/해제', () => { const l = app.doc.active; setRough(l, !l?.rough); });
  C('layer.roughColor', '러프 색 바꾸기…', async () => {
    const l = app.doc.active;
    const v = await form(l?.rough ? `러프 색: ${l.name}` : '새 러프 레이어 색', [
      { key: 'c', label: '색', type: 'segment', options: ROUGH_COLORS.map(([hex, name]) => [hex, name]), value: l?.rough ? l.tint : defaultRoughColor() },
    ], { okLabel: '적용' });
    if (v) setRoughColor(l, v.c);
  });
  C('layer.duplicate', '레이어 복제', () => ops.duplicateLayer());
  C('layer.delete', '레이어 삭제', () => (app.doc.multi?.size > 1 ? ops.deleteLayers() : ops.deleteLayer()));
  C('layer.mergeDown', '아래 레이어와 병합 (여러 개 고르면 고른 것끼리)', () => (app.doc.multi?.size > 1 ? ops.mergeLayers() : ops.mergeDown()));
  C('layer.flatten', '보이는 레이어 결합', () => ops.flattenVisible());
  C('layer.clear', '레이어 내용 지우기', () => ops.clearLayer());
  C('layer.up', '레이어 위로', () => ops.stepLayer(app.doc.active, 1));
  C('layer.down', '레이어 아래로', () => ops.stepLayer(app.doc.active, -1));
  C('layer.newFolder', '새 폴더', () => ops.addFolder());
  C('layer.group', '폴더로 묶기 (여러 개 고르면 함께)', () => (app.doc.multi?.size > 1 ? ops.groupLayers() : ops.groupIntoFolder()), 'ctrl+g');
  C('layer.mergeFolder', '폴더를 한 레이어로 합치기', () => ops.mergeFolder());
  C('layer.clip', '아래 레이어에서 클리핑', () => { const l = app.doc.active; ops.setLayerProps(l, { clip: !l.clip }, '클리핑'); });
  C('layer.alphaLock', '투명 픽셀 잠금', () => { const l = app.doc.active; ops.setLayerProps(l, { alphaLock: !l.alphaLock }, '투명 잠금'); });
  C('layer.lock', '레이어 잠금', () => { const l = app.doc.active; ops.setLayerProps(l, { locked: !l.locked }, '잠금'); });
  C('layer.draft', '밑그림 레이어 (내보내기 제외)', () => { const l = app.doc.active; ops.setLayerProps(l, { draft: !l.draft }, '밑그림'); });
  C('layer.tint', '레이어 컬러 (파란 선으로 보기)', () => { const l = app.doc.active; ops.setLayerProps(l, { tint: l.tint ? null : '#3d7be0' }, '레이어 컬러'); });
  C('layer.selectAlpha', '레이어의 그림 부분을 선택', () => selectionHistory(() => app.doc.selection.fromAlpha(app.doc.active.canvas, 'replace')));

  // ---- 선택 ----
  C('select.all', '전체 선택', () => selectionHistory(() => app.doc.selection.selectAll()));
  C('select.none', '선택 해제', () => { if (app.doc.selection.active) selectionHistory(() => app.doc.selection.clear()); });
  C('select.invert', '선택 반전', () => selectionHistory(() => app.doc.selection.invert()));
  C('select.grow', '선택 확장 (+4px)', () => selectionHistory(() => app.doc.selection.grow(4)));
  C('select.shrink', '선택 축소 (-4px)', () => selectionHistory(() => app.doc.selection.grow(-4)));
  C('select.growBy', '선택 확장/축소…', async () => {
    const v = await form('선택 영역 확장/축소', [{ key: 'px', label: '픽셀 (음수 = 축소)', type: 'number', value: 4 }]);
    if (v) selectionHistory(() => app.doc.selection.grow(Math.round(v.px)));
  });
  C('select.toLayer', '선택 영역을 새 레이어로 복사', () => {
    const doc = app.doc, src = doc.active;
    const r = extractContent(src);
    if (!r) { app.toast('복사할 그림이 없습니다'); return; }
    const l = ops.addLayer({ name: src.name + ' 조각' });
    l.ctx.drawImage(r.canvas, r.bounds.x, r.bounds.y);
    l.touch();
  });

  // ---- 보기 ----
  C('view.fit', '화면에 맞춤', () => app.view.fit(app.doc));
  C('view.fitWidth', '가로 폭에 맞춤', () => app.view.fitWidth(app.doc));
  C('view.actual', '100%', () => app.view.actualSize(app.doc));
  C('view.zoomIn', '확대', () => app.view.zoomAt(1.25, app.view.w / 2, app.view.h / 2));
  C('view.zoomOut', '축소', () => app.view.zoomAt(0.8, app.view.w / 2, app.view.h / 2));
  C('view.zoomIn2', '확대', () => app.view.zoomAt(1.25, app.pointer.sx || app.view.w / 2, app.pointer.sy || app.view.h / 2), null, { hidden: true });
  C('view.zoomOut2', '축소', () => app.view.zoomAt(0.8, app.pointer.sx || app.view.w / 2, app.pointer.sy || app.view.h / 2), null, { hidden: true });
  C('view.rotL', '왼쪽으로 15° 회전', () => app.view.rotateAt(-15));
  C('view.rotR', '오른쪽으로 15° 회전', () => app.view.rotateAt(15));
  C('view.resetRotation', '회전 초기화', () => app.view.setAngle(0));
  C('view.flip', '화면 좌우 반전 (데생 확인)', () => app.view.flipH());
  C('view.toggleUI', '패널 모두 숨기기/보이기', () => document.body.classList.toggle('zen'));
  C('view.showSide', '패널 열기', () => {
    if (document.body.classList.contains('compact')) { document.body.classList.add('side-open'); return; }
    if (app.settings.sideVisible === false) app.run('view.toggleSide');
  }, null, { hidden: true });
  C('view.toggleSide', '오른쪽 패널 보이기/숨기기', () => {
    if (document.body.classList.contains('compact')) { document.body.classList.toggle('side-open'); return; }
    app.settings.sideVisible = !app.settings.sideVisible;
    document.body.classList.toggle('no-side', !app.settings.sideVisible);
    app.saveSettingsSoon();
  });
  C('view.fullscreen', '전체 화면', () => {
    if (document.fullscreenElement) document.exitFullscreen(); else document.documentElement.requestFullscreen();
  });
  C('view.grid', '격자 보기', () => { app.settings.showGrid = !app.settings.showGrid; app.saveSettingsSoon(); app.requestRender(); });
  C('view.symmetry', '대칭 그리기 (좌우)', () => {
    const s = app.settings;
    s.symmetry = s.symmetry === 'none' ? 'v' : s.symmetry === 'v' ? 'h' : s.symmetry === 'h' ? 'vh' : s.symmetry === 'vh' ? 'radial' : 'none';
    app.toast('대칭: ' + ({ none: '끔', v: '좌우', h: '상하', vh: '상하좌우' }[s.symmetry] || `방사 ${s.symN || 6}조각`));
    app.bus.emit('settings:changed');
    app.requestRender();
  });
  C('view.quickbar', '퀵바 보이기/숨기기', () => {
    app.settings.quickbar.visible = !app.settings.quickbar.visible;
    app.bus.emit('quick:changed');
    app.saveSettingsSoon();
  });

  // ---- 필터 ----
  C('filter.lineExtract', '선화 추출 (흰 배경 → 투명)', () => ops.applyPixelFilter('선화 추출', (img) => {
    const d = img.data;
    for (let i = 0; i < d.length; i += 4) {
      const lum = 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2];
      const a = (255 - lum) * d[i + 3] / 255;
      d[i] = d[i + 1] = d[i + 2] = 0;
      d[i + 3] = Math.min(255, a * 1.15);
    }
  }));
  C('filter.gray', '흑백', () => ops.applyPixelFilter('흑백', (img) => {
    const d = img.data;
    for (let i = 0; i < d.length; i += 4) { const v = 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2]; d[i] = d[i + 1] = d[i + 2] = v; }
  }));
  C('filter.invert', '색 반전', () => ops.applyPixelFilter('색 반전', (img) => {
    const d = img.data;
    for (let i = 0; i < d.length; i += 4) { d[i] = 255 - d[i]; d[i + 1] = 255 - d[i + 1]; d[i + 2] = 255 - d[i + 2]; }
  }));
  C('filter.mono', '2값화 (흑백 선화 정리)', async () => {
    const v = await form('2값화', [{ key: 't', label: '경계값 (0~255)', type: 'number', value: 128 }]);
    if (!v) return;
    ops.applyPixelFilter('2값화', (img) => {
      const d = img.data;
      for (let i = 0; i < d.length; i += 4) {
        if (d[i + 3] < 8) continue;
        const lum = 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2];
        const on = lum < v.t && d[i + 3] > 96;
        d[i] = d[i + 1] = d[i + 2] = 0; d[i + 3] = on ? 255 : 0;
      }
    });
  });
  C('filter.blur', '흐리게…', async () => {
    const v = await form('흐리게', [{ key: 'r', label: '반경(px)', type: 'number', value: 4 }]);
    if (v) ops.applyCanvasFilter('흐리게', `blur(${v.r}px)`);
  });
  C('filter.adjust', '밝기/대비/채도…', async () => {
    const v = await form('색조 보정', [
      { key: 'b', label: '밝기 %', type: 'number', value: 100 },
      { key: 'c', label: '대비 %', type: 'number', value: 100 },
      { key: 's', label: '채도 %', type: 'number', value: 100 },
      { key: 'hue', label: '색조 회전 (도)', type: 'number', value: 0 },
    ]);
    if (v) ops.applyCanvasFilter('색조 보정', `brightness(${v.b}%) contrast(${v.c}%) saturate(${v.s}%) hue-rotate(${v.hue}deg)`);
  });

  // ---- 에셋 ----
  const assetSave = async (merged, label) => {
    const r = extractContent(app.doc.active, merged);
    if (!r) { app.toast('저장할 그림이 없습니다'); return; }
    const v = await form(label, [
      { key: 'name', label: '이름', value: (merged ? app.doc.name : app.doc.active.name).replace(/[\\/:*?"<>|]/g, '_') },
      { key: 'folder', label: '폴더 (선택)', value: app.settings.lastAssetFolder || '', hint: '예: 배경/나무 — data/assets 아래에 만들어집니다' },
      { key: 'info', type: 'info', label: `${r.bounds.w}×${r.bounds.h}px 투명 PNG (빈 여백은 잘라냄)` },
    ], { okLabel: '저장' });
    if (!v) return;
    app.settings.lastAssetFolder = v.folder;
    await saveAsset(r.canvas, v.name, v.folder);
  };
  C('asset.saveLayer', '현재 레이어를 에셋으로 저장…', () => assetSave(false, '레이어 → 에셋'));
  C('asset.saveSelection', '선택 영역을 에셋으로 저장…', () => assetSave(false, '선택 영역 → 에셋'));
  C('asset.saveMerged', '보이는 그림 전체를 에셋으로 저장…', () => assetSave(true, '보이는 그림 → 에셋'));
  C('asset.folder', '에셋 폴더 열기', () => server.openFolder('assets'));
  C('panel.frame', '페이지 컷 만들기…', () => pageFrameDialog());

  // ---- 브러시/색 ----
  const resize = (f) => {
    const p = activePreset(currentBrushCategory());
    p.size = Math.max(0.5, Math.min(600, Math.round(p.size * f * 2) / 2 || 1));
    app.bus.emit('brush:changed', p);
    app.saveSettingsSoon();
    app.requestRender();
  };
  C('brush.sizeUp', '브러시 크게', () => resize(1.15));
  C('brush.sizeDown', '브러시 작게', () => resize(1 / 1.15));
  C('color.swap', '전경/배경색 바꾸기', () => {
    const { fg, bg } = app.color;
    app.color.fg = bg; app.color.bg = fg;
    app.bus.emit('color:changed', app.color);
  });

  // ---- 도구 공통 ----
  C('tool.commit', '확정', () => app.tool?.commitPending?.(), null, { hidden: true });
  C('tool.escape', '취소', () => {
    if (app.tool?.st || app.floating) { app.tool.cancel?.(); return; }
    if (app.doc.selection.active) app.run('select.none');
  }, null, { hidden: true });
  C('plugins.reload', '플러그인 다시 불러오기 (새로고침)', () => location.reload());
  C('plugins.folder', '플러그인 폴더 열기', () => server.openFolder('plugins'));
}

// ---- 복사/붙여넣기 ----
async function copySelection(cut) {
  const doc = app.doc;
  const r = extractContent(doc.active);
  if (!r) { app.toast('복사할 그림이 없습니다'); return; }
  app.clipboard = r;
  try {
    const blob = await canvasToBlob(r.canvas);
    await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })]);
  } catch { /* 시스템 클립보드 실패해도 앱 내부 복사는 됨 */ }
  if (cut) ops.clearLayer();
  app.toast(cut ? '잘라냈습니다' : '복사했습니다');
}

async function paste() {
  try {
    const items = await navigator.clipboard.read();
    for (const it of items) {
      const type = it.types.find(t => t.startsWith('image/'));
      if (!type) continue;
      const img = await blobToImage(await it.getType(type));
      // 방금 앱에서 복사한 그림이면 원래 위치에
      const cb = app.clipboard;
      if (cb && cb.canvas.width === img.width && cb.canvas.height === img.height) break;
      ops.placeImage(img, '붙여넣기');
      return;
    }
  } catch { /* 권한 없음 → 내부 클립보드 사용 */ }
  const cb = app.clipboard;
  if (!cb) { app.toast('붙여넣을 내용이 없습니다'); return; }
  const l = ops.addLayer({ name: '붙여넣기' });
  l.ctx.drawImage(cb.canvas, cb.bounds.x, cb.bounds.y);
  l.touch();
  app.setTool('transform');
  app.tool.start?.();
}

export function registerMenus() {
  const M = (id, label, items) => {
    app.addMenu(id, label);
    for (const it of items) app.addMenuItem(id, it === '-' ? { sep: true } : typeof it === 'string' ? { command: it } : it);
  };
  M('file', '파일', ['file.new', 'file.open', 'file.openLocal', '-', 'file.save', 'file.saveAs', 'file.download', '-', 'file.import', 'file.export', 'file.exportQuick', '-', 'file.recover', 'file.versions', 'file.storage', 'file.drivePull', 'file.folder', 'file.exportsFolder']);
  M('edit', '편집', ['edit.undo', 'edit.redo', '-', 'edit.cut', 'edit.copy', 'edit.paste', 'edit.clear', 'edit.fill', '-', 'edit.transform', 'edit.flipH', 'edit.flipV', '-', 'edit.canvasSize', 'edit.extendDown']);
  M('layer', '레이어', ['layer.new', 'layer.newVector', 'vector.width', 'vector.color', 'vector.toRaster', 'bubble.toRaster', 'layer.newFolder', 'layer.group', 'layer.mergeFolder', 'layer.newRough', 'layer.rough', 'layer.roughColor', 'layer.newLine', 'layer.newColor', '-', 'layer.duplicate', 'layer.delete', 'layer.mergeDown', 'layer.flatten', 'layer.clear', '-', 'layer.clip', 'layer.alphaLock', 'layer.lock', 'layer.draft', 'layer.tint', 'layer.selectAlpha']);
  M('select', '선택', ['select.all', 'select.none', 'select.invert', '-', 'select.grow', 'select.shrink', 'select.growBy', '-', 'select.toLayer', 'layer.selectAlpha']);
  M('comic', '만화', ['panel.frame', { label: '컷 나누기 도구', run: () => app.setTool('panel') }, { label: '말풍선 도구', run: () => app.setTool('bubble') }, { label: '텍스트 도구', run: () => app.setTool('text') }, '-', 'layer.newRough', 'layer.newLine', 'layer.newColor', '-', 'edit.extendDown']);
  M('asset', '에셋', ['asset.saveLayer', 'asset.saveSelection', 'asset.saveMerged', '-', 'asset.folder']);
  M('filter', '필터', ['filter.lineExtract', 'filter.mono', 'filter.gray', 'filter.invert', 'filter.blur', 'filter.adjust']);
  M('view', '보기', ['view.fit', 'view.fitWidth', 'view.actual', 'view.zoomIn', 'view.zoomOut', '-', 'view.rotL', 'view.rotR', 'view.resetRotation', 'view.flip', '-', 'view.grid', 'view.symmetry', '-', 'view.toggleSide', 'view.quickbar', 'view.toggleUI', 'view.fullscreen']);
  M('plugin', '플러그인', ['plugins.folder', 'plugins.reload', '-']);
  M('help', '도움말', [{ command: 'settings.open' }, { command: 'help.keys' }, { command: 'help.about' }]);
}

