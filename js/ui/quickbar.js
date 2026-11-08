// 떠 있는 퀵바: 왼손으로 누르기 좋은 큰 버튼 + 세로 크기 슬라이더. 끌어서 위치 이동.
import { app } from '../app.js';
import { h, clamp } from '../core/util.js';
import { icon } from './icons.js';
import { activePreset } from '../tools/brush.js';
import { form } from './dialogs.js';

export const DEFAULT_QUICK = ['undo', 'redo', 'penEraser', 'eyedropper', 'size', 'opacity', 'line', 'fit', 'flip', 'deselect'];

export function registerDefaultQuickActions() {
  const Q = (id, def) => app.registerQuickAction(id, def);
  Q('undo', { icon: 'undo', label: '실행취소', run: () => app.run('edit.undo') });
  Q('redo', { icon: 'redo', label: '다시실행', run: () => app.run('edit.redo') });
  Q('penEraser', {
    icon: 'eraser', label: '펜↔지우개',
    run: () => app.setTool(app.toolId === 'eraser' ? 'brush' : 'eraser'),
    active: () => app.toolId === 'eraser',
  });
  Q('eyedropper', {
    icon: 'eyedropper', label: '스포이트(한 번)',
    run: () => {
      const prev = app.toolId;
      app.setTool('eyedropper');
      const off = app.bus.on('color:changed', () => { off(); setTimeout(() => app.setTool(prev), 120); });
    },
    active: () => app.toolId === 'eyedropper',
  });
  Q('size', { type: 'size', label: '브러시 크기' });
  Q('opacity', { type: 'opacity', label: '불투명도' });
  Q('line', {
    icon: 'straight', label: '직선 모드',
    run: () => { app.modifiers.line = !app.modifiers.line; app.toast(app.modifiers.line ? '직선 모드 켜짐' : '직선 모드 꺼짐'); },
    active: () => app.modifiers.line,
  });
  Q('snap', {
    icon: 'line', label: '각도 스냅 (Shift 대용)',
    run: () => { app.modifiers.snap = !app.modifiers.snap; app.modifiers.shift = app.modifiers.snap; },
    active: () => app.modifiers.snap,
  });
  Q('fit', { icon: 'fit', label: '화면 맞춤', run: () => app.run('view.fit') });
  Q('flip', { icon: 'flip', label: '좌우 반전 보기', run: () => app.run('view.flip'), active: () => app.view.flipped });
  Q('rotL', { icon: 'rotateL', label: '왼쪽 회전', run: () => app.run('view.rotL') });
  Q('rotR', { icon: 'rotate', label: '오른쪽 회전', run: () => app.run('view.rotR') });
  Q('deselect', { icon: 'deselect', label: '선택 해제', run: () => app.run('select.none') });
  Q('transform', { icon: 'transform', label: '변형', run: () => app.run('edit.transform') });
  Q('finger', { icon: 'finger', label: '손가락 그리기', run: () => app.run('settings.fingerDraw'), active: () => app.settings.fingerDraw });
  Q('newLayer', { icon: 'plus', label: '새 레이어', run: () => app.run('layer.new') });
  Q('rough', { icon: 'rough', label: '러프 레이어', run: () => app.run('layer.newRough') });
  Q('save', { icon: 'save', label: '저장', run: () => app.run('file.save') });
  Q('zen', { icon: 'side', label: '패널 숨기기', run: () => app.run('view.toggleUI') });
  Q('sym', { icon: 'sym', label: '대칭', run: () => app.run('view.symmetry'), active: () => app.settings.symmetry !== 'none' });
  Q('swap', { icon: 'swap', label: '색 바꾸기', run: () => app.run('color.swap') });
}

function vslider(label, get, set, min, max, log, fmt) {
  const toT = (v) => log ? Math.log(v / min) / Math.log(max / min) : (v - min) / (max - min);
  const fromT = (t) => log ? min * Math.pow(max / min, t) : min + (max - min) * t;
  const fill = h('div.vs-fill');
  const val = h('div.vs-val');
  const track = h('div.vs-track', { title: label }, fill);
  const el = h('div.vslider', null, val, track);
  const sync = () => { fill.style.height = clamp(toT(get()), 0, 1) * 100 + '%'; val.textContent = fmt(get()); };
  let y0 = 0, t0 = 0;
  track.addEventListener('pointerdown', (e) => {
    e.stopPropagation();
    track.setPointerCapture(e.pointerId);
    y0 = e.clientY; t0 = toT(get());
    if (e.pointerType !== 'touch') {
      const r = track.getBoundingClientRect();
      set(fromT(clamp(1 - (e.clientY - r.top) / r.height, 0, 1))); sync();
    }
    track.onpointermove = (ev) => {
      const r = track.getBoundingClientRect();
      const t = ev.pointerType === 'touch' ? t0 - (ev.clientY - y0) / r.height : 1 - (ev.clientY - r.top) / r.height;
      set(fromT(clamp(t, 0, 1))); sync();
    };
    track.onpointerup = () => { track.onpointermove = null; app.saveSettingsSoon(); };
  });
  el.sync = sync;
  sync();
  return el;
}

export function initQuickbar() {
  const bar = document.getElementById('quickbar');
  const cat = () => (app.toolId === 'eraser' ? 'eraser' : 'brush');
  const render = () => {
    const q = app.settings.quickbar;
    bar.innerHTML = '';
    bar.hidden = !q.visible;
    const items = q.items || DEFAULT_QUICK;
    const grip = h('div.qb-grip', { title: '끌어서 이동 · 탭: 버튼 편집', html: icon('grip', 18) });
    bar.append(grip);
    for (const id of items) {
      const a = app.quickActions.get(id);
      if (!a) continue;
      if (a.type === 'size') {
        bar.append(vslider('크기', () => activePreset(cat()).size, (v) => { activePreset(cat()).size = Math.round(v * 2) / 2; app.bus.emit('brush:changed', activePreset(cat())); app.requestRender(); }, 0.5, 600, true, v => (v < 10 ? v.toFixed(1) : Math.round(v)) + ''));
        continue;
      }
      if (a.type === 'opacity') {
        bar.append(vslider('불투명도', () => activePreset(cat()).opacity, (v) => { activePreset(cat()).opacity = Math.round(v * 100) / 100; app.bus.emit('brush:changed', activePreset(cat())); }, 0.01, 1, false, v => Math.round(v * 100) + '%'));
        continue;
      }
      const b = h('button.qb-btn', { type: 'button', title: a.label, 'data-id': id, html: icon(a.icon, 24) });
      b.addEventListener('click', (e) => { e.stopPropagation(); a.run(); sync(); });
      bar.append(b);
    }
    place();
    sync();
    // 끌기
    let sx = 0, sy = 0, ox = 0, oy = 0, moved = false;
    grip.addEventListener('pointerdown', (e) => {
      e.stopPropagation();
      grip.setPointerCapture(e.pointerId);
      sx = e.clientX; sy = e.clientY; ox = q.x; oy = q.y; moved = false;
      grip.onpointermove = (ev) => {
        if (Math.hypot(ev.clientX - sx, ev.clientY - sy) > 5) moved = true;
        q.x = ox + ev.clientX - sx; q.y = oy + ev.clientY - sy; place();
      };
      grip.onpointerup = () => { grip.onpointermove = null; app.saveSettingsSoon(); if (!moved) editDialog(); };
    });
  };
  const place = () => {
    const q = app.settings.quickbar;
    const stage = document.getElementById('stage').getBoundingClientRect();
    const r = bar.getBoundingClientRect();
    q.x = clamp(q.x, 0, Math.max(0, stage.width - r.width));
    q.y = clamp(q.y, 0, Math.max(0, stage.height - r.height));
    bar.style.left = q.x + 'px';
    bar.style.top = q.y + 'px';
  };
  const sync = () => {
    bar.querySelectorAll('.qb-btn').forEach(b => {
      const a = app.quickActions.get(b.dataset.id);
      b.classList.toggle('on', !!a?.active?.());
    });
    bar.querySelectorAll('.vslider').forEach(s => s.sync());
  };
  const editDialog = async () => {
    const q = app.settings.quickbar;
    const cur = new Set(q.items || DEFAULT_QUICK);
    const fields = [...app.quickActions.values()].map(a => ({ key: a.id, label: a.label, type: 'checkbox', value: cur.has(a.id) }));
    const v = await form('퀵바 버튼 고르기', [{ key: 'info', type: 'info', label: '체크한 버튼이 퀵바에 나타납니다. 손잡이를 끌어 위치를 옮길 수 있어요.' }, ...fields], { okLabel: '적용' });
    if (!v) return;
    const order = [...(q.items || DEFAULT_QUICK).filter(id => v[id]), ...Object.keys(v).filter(id => v[id] && !(q.items || DEFAULT_QUICK).includes(id))];
    q.items = order;
    app.saveSettingsSoon();
    render();
  };
  for (const e of ['tool:changed', 'brush:changed', 'settings:changed', 'view:changed']) app.bus.on(e, sync);
  app.bus.on('quick:changed', render);
  window.addEventListener('resize', place);
  render();
}
