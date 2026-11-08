// 포토샵식 편집 도구 묶음: 복구 브러시 · 내용 인식 채우기 · 펜(패스) · 원근/격자/퍼펫 왜곡 · 선택 다듬기 · 자동 액션 · 전자책
import { app } from '../app.js';
import { form } from './dialogs.js';
import { addLayer, checkEditable } from '../core/ops.js';
import { contentAwareFill } from '../core/inpaint.js';
import { healTool } from '../tools/heal.js';
import { pathPenTool, initPaths } from '../tools/pathpen.js';
import { meshWarpTool, puppetTool } from '../tools/warp.js';
import { refineBrushTool, refineDialog } from './refine.js';
import { initMacros } from './macros.js';
import { initEbook } from './ebook.js';

export async function contentAwareFillDialog() {
  const doc = app.doc, sel = doc.selection;
  if (!sel.active) { app.toast('채울 곳을 먼저 선택하세요 (지울 물체를 대충 둘러싸면 됩니다)'); return; }
  const layer = doc.active?.target;
  if (!checkEditable(layer)) return;
  const v = await form('내용 인식 채우기', [
    { key: 'src', label: '보고 채울 것', type: 'segment', options: [['layer', '지금 레이어'], ['all', '보이는 그림 전체']], value: app.settings.cafSrc || 'layer' },
    { key: 'margin', label: '주변을 얼마나 넓게 볼지', type: 'segment', options: [['0.6', '가까이'], ['1', '보통'], ['2', '넓게']], value: '1' },
    { key: 'out', label: '결과', type: 'segment', options: [['layer', '이 레이어에'], ['new', '새 레이어로']], value: app.settings.cafOut || 'layer' },
    { key: 'info', type: 'info', label: '선택한 곳을 주변 무늬로 자연스럽게 메웁니다 (배경의 물체·글자·잡티 지우기). 큰 영역은 몇십 초 걸릴 수 있습니다.' },
  ], { okLabel: '채우기' });
  if (!v) return;
  Object.assign(app.settings, { cafSrc: v.src, cafOut: v.out });
  app.saveSettingsSoon();
  const src = v.src === 'all' ? app.compositor.flatten(doc, { paper: false }) : layer.canvas;
  const busy = (t) => { const el = document.querySelector('#busy span'); if (el) el.textContent = t; };
  const res = await app.task('내용 인식 채우기', () => contentAwareFill(src, sel, { margin: +v.margin, onProgress: (f) => busy(`내용 인식 채우기 ${Math.round(f * 100)}%`) }), { delay: 0 });
  if (!res) return;
  const { canvas, rect } = res;
  // 선택 영역 모양대로만 (가장자리는 선택의 부드러움 그대로)
  const cut = document.createElement('canvas');
  cut.width = rect.w; cut.height = rect.h;
  const cx = cut.getContext('2d');
  cx.drawImage(canvas, 0, 0);
  cx.globalCompositeOperation = 'destination-in';
  cx.drawImage(sel.mask, rect.x, rect.y, rect.w, rect.h, 0, 0, rect.w, rect.h);
  app.history.batch('내용 인식 채우기', () => {
    const target = v.out === 'new' ? addLayer({ name: '내용 인식 채우기' }) : layer;
    app.history.pixels(target, '내용 인식 채우기', (ctx) => {
      ctx.save();
      if (v.out !== 'new') { ctx.globalCompositeOperation = 'destination-out'; ctx.drawImage(sel.mask, rect.x, rect.y, rect.w, rect.h, rect.x, rect.y, rect.w, rect.h); ctx.globalCompositeOperation = 'source-over'; }
      ctx.drawImage(cut, rect.x, rect.y);
      ctx.restore();
      return rect;
    });
  });
  app.requestRender();
  app.toast('채웠습니다 (마음에 안 들면 실행취소 후 범위를 바꿔 다시)');
}

export function initPSTools() {
  for (const t of [healTool, pathPenTool, meshWarpTool, puppetTool, refineBrushTool]) app.registerTool(t);
  const C = (id, label, run) => app.registerCommand(id, { label, run });
  C('edit.contentAwareFill', '내용 인식 채우기 (선택한 곳을 주변으로 메우기)…', contentAwareFillDialog);
  C('edit.heal', '복구 브러시 (잡티 지우기)', () => app.setTool('heal'));
  C('edit.meshWarp', '원근·격자 왜곡 (여러 면)', () => app.setTool('meshwarp'));
  C('edit.puppet', '퍼펫 왜곡 (핀으로 휘기)', () => app.setTool('puppet'));
  C('select.refine', '선택 다듬기 (매끄럽게·페더·대비·경계 이동)…', refineDialog);
  C('select.refineBrush', '머리카락 다듬기 브러시', () => app.setTool('refinebrush'));
  for (const it of [{ sep: true }, { command: 'edit.contentAwareFill' }, { command: 'edit.heal' }, { command: 'edit.meshWarp' }, { command: 'edit.puppet' }]) app.addMenuItem('edit', it);
  for (const it of [{ sep: true }, { command: 'select.refine' }, { command: 'select.refineBrush' }]) app.addMenuItem('select', it);
  initPaths();
  initMacros();
  initEbook();
  app.registerSelectionAction?.({ icon: 'heal', label: '내용 인식 채우기', command: 'edit.contentAwareFill' });
}
