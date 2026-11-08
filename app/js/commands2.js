// 추가 명령: PSD/웹툰 분할/레이어별 내보내기, 마스크, 참조 레이어, 외곽선·그림자, 대칭, 자
import { app } from './app.js';
import * as ops from './core/ops.js';
import { server } from './core/io.js';
import { writePSD } from './core/psd.js';
import { canvasToBlob, createCanvas, downloadBlob, safeName, fileStamp } from './core/util.js';
import { trimCanvas } from './core/raster.js';
import { form } from './ui/dialogs.js';
import { addRuler } from './tools/ruler.js';
import { openReference } from './ui/extra.js';

const C = (id, label, run, extra = {}) => app.registerCommand(id, { label, run, ...extra });

async function saveExport(name, blob, download) {
  if (download || !server.online) { downloadBlob(blob, name.split('/').pop()); return; }
  await server.put('exports', name, blob);
}

export function registerMoreCommands() {
  // ---- 내보내기 ----
  C('file.exportPSD', 'PSD로 내보내기 (레이어 유지)…', async () => {
    const v = await form('PSD 내보내기', [
      { key: 'download', label: '다운로드 폴더로 받기 (끄면 data/exports)', type: 'checkbox', value: false },
      { key: 'info', type: 'info', label: '레이어 이름·불투명도·합성 모드·클리핑·숨김·폴더·레이어 마스크가 유지됩니다. 텍스트/컷/말풍선은 그림 레이어로 저장됩니다.' },
    ], { okLabel: '내보내기' });
    if (!v) return;
    app.toast('PSD 만드는 중…');
    await new Promise(r => setTimeout(r, 30));
    const doc = app.doc;
    const flat = app.compositor.flatten(doc, { excludeDraft: false, background: '#ffffff' });
    const blob = writePSD(doc, flat);
    const name = `${safeName(doc.name)}_${fileStamp()}.psd`;
    await saveExport(name, blob, v.download);
    app.toast(`PSD 저장: ${v.download ? '다운로드' : 'data/exports/' + name} (${(blob.size / 1048576).toFixed(1)}MB)`);
  });

  C('file.exportSlices', '웹툰 분할 내보내기…', async () => {
    const doc = app.doc;
    const v = await form('웹툰 분할 내보내기', [
      { key: 'h', label: '한 장 높이 (px)', type: 'number', value: app.settings.sliceH || 1280 },
      { key: 'width', label: '가로 크기 (px, 비우면 원본)', type: 'number', value: app.settings.sliceW || doc.width },
      { key: 'format', label: '형식', type: 'segment', options: [['jpg', 'JPG'], ['png', 'PNG']], value: 'jpg' },
      { key: 'quality', label: 'JPG 품질 (0.5~1)', type: 'number', value: 0.92, step: 0.01 },
      { key: 'info', type: 'info', label: `세로 ${doc.height}px → 약 ${Math.ceil(doc.height / (app.settings.sliceH || 1280))}장. data/exports/작품명_분할 폴더에 01, 02… 순서로 저장됩니다.` },
    ], { okLabel: '내보내기' });
    if (!v) return;
    app.settings.sliceH = v.h; app.settings.sliceW = v.width; app.saveSettingsSoon();
    const scale = (v.width || doc.width) / doc.width;
    const flat = app.compositor.flatten(doc, { scale, background: '#ffffff' });
    const sh = Math.round(v.h);
    const n = Math.ceil(flat.height / sh);
    const folder = `${safeName(doc.name)}_분할_${fileStamp()}`;
    for (let i = 0; i < n; i++) {
      const hh = Math.min(sh, flat.height - i * sh);
      const c = createCanvas(flat.width, hh);
      c.getContext('2d').drawImage(flat, 0, i * sh, flat.width, hh, 0, 0, flat.width, hh);
      const blob = await canvasToBlob(c, v.format === 'jpg' ? 'image/jpeg' : 'image/png', v.quality);
      await saveExport(`${folder}/${String(i + 1).padStart(2, '0')}.${v.format}`, blob, false);
    }
    app.toast(`${n}장 저장: data/exports/${folder}`);
  });

  C('file.exportLayers', '레이어별 PNG 내보내기…', async () => {
    const v = await form('레이어별 PNG', [
      { key: 'trim', label: '빈 여백 잘라내기 (에셋용)', type: 'checkbox', value: true },
      { key: 'hidden', label: '숨긴 레이어도 포함', type: 'checkbox', value: false },
      { key: 'toAssets', label: '에셋 폴더에 저장 (끄면 data/exports)', type: 'checkbox', value: false },
    ], { okLabel: '내보내기' });
    if (!v) return;
    const doc = app.doc;
    const folder = `${safeName(doc.name)}_레이어_${fileStamp()}`;
    let count = 0;
    for (const [i, l] of doc.layers.entries()) {
      if (!l.visible && !v.hidden) continue;
      let c = l.canvas;
      if (l.mask && l.maskOn) {
        c = createCanvas(doc.width, doc.height);
        const x = c.getContext('2d'); x.drawImage(l.canvas, 0, 0); x.globalCompositeOperation = 'destination-in'; x.drawImage(l.mask, 0, 0);
      }
      if (v.trim) { const t = trimCanvas(c); if (!t) continue; c = t.canvas; }
      const name = `${folder}/${String(i + 1).padStart(2, '0')}_${safeName(l.name)}.png`;
      const blob = await canvasToBlob(c);
      if (v.toAssets) await server.put('assets', name, blob); else await saveExport(name, blob, false);
      count++;
    }
    if (v.toAssets) app.bus.emit('assets:changed');
    app.toast(`${count}개 레이어 저장: data/${v.toAssets ? 'assets' : 'exports'}/${folder}`);
  });

  // ---- 에셋/스티커 효과 ----
  C('filter.outline', '외곽선 두르기 (스티커/에셋)…', async () => {
    const v = await form('외곽선', [
      { key: 'w', label: '두께 (px)', type: 'number', value: 8 },
      { key: 'color', label: '색', type: 'color', value: '#ffffff' },
      { key: 'info', type: 'info', label: '그림 바깥쪽에 테두리를 두릅니다 (선택 영역이 있으면 그 안만).' },
    ], { okLabel: '적용' });
    if (!v) return;
    const target = ops.activeTarget();
    if (!ops.checkEditable(target)) return;
    const W = target.canvas.width, H = target.canvas.height, r = Math.max(1, v.w);
    app.history.pixels(target, '외곽선', (ctx) => {
      const sil = createCanvas(W, H), s = sil.getContext('2d');
      s.drawImage(target.canvas, 0, 0);
      s.globalCompositeOperation = 'source-in';
      s.fillStyle = v.color; s.fillRect(0, 0, W, H);
      const ring = createCanvas(W, H), rg = ring.getContext('2d');
      const n = Math.max(16, Math.ceil(r * 2.5));
      for (let i = 0; i < n; i++) { const a = i / n * Math.PI * 2; rg.drawImage(sil, Math.cos(a) * r, Math.sin(a) * r); }
      for (let rr = r / 2; rr > 0.5; rr /= 2) for (let i = 0; i < 12; i++) { const a = i / 12 * Math.PI * 2; rg.drawImage(sil, Math.cos(a) * rr, Math.sin(a) * rr); }
      rg.drawImage(sil, 0, 0);
      if (app.doc.selection.active) { rg.globalCompositeOperation = 'destination-in'; rg.drawImage(app.doc.selection.mask, 0, 0); }
      ctx.save();
      ctx.globalCompositeOperation = 'destination-over';
      ctx.drawImage(ring, 0, 0);
      ctx.restore();
    });
  });

  C('filter.shadow', '그림자 넣기…', async () => {
    const v = await form('그림자', [
      { key: 'dx', label: '가로 이동', type: 'number', value: 12 },
      { key: 'dy', label: '세로 이동', type: 'number', value: 12 },
      { key: 'blur', label: '흐림', type: 'number', value: 10 },
      { key: 'color', label: '색', type: 'color', value: '#000000' },
      { key: 'alpha', label: '진하기 (0~1)', type: 'number', value: 0.45, step: 0.05 },
    ], { okLabel: '적용' });
    if (!v) return;
    const target = ops.activeTarget();
    if (!ops.checkEditable(target)) return;
    app.history.pixels(target, '그림자', (ctx) => {
      const W = target.canvas.width, H = target.canvas.height;
      const sh = createCanvas(W, H), s = sh.getContext('2d');
      s.filter = `blur(${v.blur}px)`;
      s.drawImage(target.canvas, v.dx, v.dy);
      s.filter = 'none';
      s.globalCompositeOperation = 'source-in';
      s.fillStyle = v.color; s.fillRect(0, 0, W, H);
      ctx.save();
      ctx.globalAlpha = v.alpha;
      ctx.globalCompositeOperation = 'destination-over';
      ctx.drawImage(sh, 0, 0);
      ctx.restore();
    });
  });

  C('filter.colorize', '단색으로 바꾸기 (선 색 변경)', () => {
    const target = ops.activeTarget();
    if (!ops.checkEditable(target)) return;
    app.history.pixels(target, '단색 변경', (ctx) => {
      ctx.save();
      ctx.globalCompositeOperation = 'source-in';
      ctx.fillStyle = app.color.fg;
      if (app.doc.selection.active) {
        const t = createCanvas(target.canvas.width, target.canvas.height), x = t.getContext('2d');
        x.drawImage(target.canvas, 0, 0);
        x.globalCompositeOperation = 'source-in'; x.fillStyle = app.color.fg; x.fillRect(0, 0, t.width, t.height);
        x.globalCompositeOperation = 'destination-in'; x.drawImage(app.doc.selection.mask, 0, 0);
        ctx.globalCompositeOperation = 'source-atop';
        ctx.drawImage(t, 0, 0);
      } else ctx.fillRect(0, 0, target.canvas.width, target.canvas.height);
      ctx.restore();
    });
  });

  // ---- 마스크 / 참조 ----
  C('layer.addMask', '레이어 마스크 만들기 (선택 영역 → 보이는 부분)', () => ops.addMask());
  C('layer.editMask', '마스크 편집 ↔ 그림 편집', () => {
    const l = app.doc.active;
    if (!l.mask) { ops.addMask(l); return; }
    l.editMask = !l.editMask;
    app.toast(l.editMask ? '마스크 편집: 지우개 = 가리기, 브러시 = 다시 보이기' : '그림 편집으로 돌아옴');
    app.bus.emit('layers:changed');
  });
  C('layer.maskOn', '마스크 켜기/끄기', () => { const l = app.doc.active; if (l.mask) ops.setLayerProps(l, { maskOn: !l.maskOn }, '마스크 켜기/끄기'); });
  C('layer.applyMask', '마스크 적용 (그림에 반영)', () => ops.applyMask());
  C('layer.deleteMask', '마스크 삭제', () => ops.deleteMask());
  C('layer.ref', '참조 레이어로 지정 (채우기 기준)', () => { const l = app.doc.active; ops.setLayerProps(l, { ref: !l.ref }, '참조 레이어'); });

  // ---- 대칭 / 자 / 참조 이미지 ----
  C('view.symmetryRadial', '방사 대칭 (만다라)…', async () => {
    const v = await form('방사 대칭', [
      { key: 'n', label: '조각 수', type: 'number', value: app.settings.symN || 6, min: 2, max: 36 },
      { key: 'mirror', label: '거울 대칭도 함께', type: 'checkbox', value: !!app.settings.symMirror },
    ], { okLabel: '켜기' });
    if (!v) return;
    Object.assign(app.settings, { symmetry: 'radial', symN: Math.round(v.n), symMirror: v.mirror });
    app.saveSettingsSoon(); app.bus.emit('settings:changed'); app.requestRender();
  });
  C('view.symmetryOff', '대칭 끄기', () => { app.settings.symmetry = 'none'; app.bus.emit('settings:changed'); app.requestRender(); });
  C('ruler.snap', '자에 맞추기 켜기/끄기', () => {
    app.settings.rulerSnap = app.settings.rulerSnap === false;
    app.toast(app.settings.rulerSnap ? '자에 맞추기 켜짐' : '자에 맞추기 꺼짐');
    app.bus.emit('settings:changed'); app.saveSettingsSoon();
  });
  C('ruler.p1', '1점 투시 자', () => addRuler('p1'));
  C('ruler.p2', '2점 투시 자', () => addRuler('p2'));
  C('ruler.p3', '3점 투시 자', () => addRuler('p3'));
  C('ruler.parallel', '평행선 자', () => addRuler('parallel'));
  C('ruler.radial', '방사선 자 (집중선용)', () => addRuler('radial'));
  C('ruler.concentric', '동심원 자', () => addRuler('concentric'));
  C('view.reference', '참조 이미지 창 열기…', () => openReference());

  const M = (menu, items) => { for (const it of items) app.addMenuItem(menu, it === '-' ? { sep: true } : typeof it === 'string' ? { command: it } : it); };
  M('file', ['-', 'file.exportPSD', 'file.exportSlices', 'file.exportLayers']);
  M('layer', ['-', 'layer.addMask', 'layer.editMask', 'layer.maskOn', 'layer.applyMask', 'layer.deleteMask', '-', 'layer.ref']);
  M('filter', ['-', 'filter.outline', 'filter.shadow', 'filter.colorize']);
  M('asset', ['-', 'filter.outline', 'filter.shadow', 'file.exportLayers']);
  M('view', ['-', 'view.symmetryRadial', 'view.symmetryOff', 'view.reference']);
  app.addMenu('ruler', '자');
  M('ruler', [{ label: '자 도구', run: () => app.setTool('ruler') }, 'ruler.snap', '-', 'ruler.p1', 'ruler.p2', 'ruler.p3', '-', 'ruler.parallel', 'ruler.radial', 'ruler.concentric']);
  M('comic', ['-', 'ruler.radial', 'file.exportSlices']);

  app.registerQuickAction('rulerSnap', { icon: 'ruler', label: '자에 맞추기', run: () => app.run('ruler.snap'), active: () => app.settings.rulerSnap !== false && app.doc?.rulers?.length > 0 });
  app.registerQuickAction('maskEdit', { icon: 'mask', label: '마스크 편집', run: () => app.run('layer.editMask'), active: () => !!app.doc?.active?.editMask });
  app.registerQuickAction('reference', { icon: 'refimg', label: '참조 이미지', run: () => app.run('view.reference') });
  app.registerQuickAction('smudge', { icon: 'smudge', label: '손끝', run: () => app.setTool('smudge'), active: () => app.toolId === 'smudge' });
  app.registerQuickAction('lassofill', { icon: 'lassofill', label: '올가미 채우기', run: () => app.setTool('lassofill'), active: () => app.toolId === 'lassofill' });
}
