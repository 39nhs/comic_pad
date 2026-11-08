// 아래쪽 큰 버튼 막대: 실행취소/다시실행처럼 자주 누르는 것들을 손이 닿기 쉬운 곳에
import { app } from '../app.js';
import { h, setHTML, esc } from '../core/util.js';
import { icon } from './icons.js';
import { slider } from './controls.js';
import { activePreset } from '../tools/brush.js';
import { form } from './dialogs.js';

export const DEFAULT_BOTTOM = ['undo', 'redo', 'brushTool', 'penEraser', 'eyedropper', 'hand', 'size', 'opacity', 'deselect', 'transform', 'newLayer', 'save'];

export function registerBottomActions() {
  const Q = (id, def) => app.registerQuickAction(id, def);
  Q('brushTool', { icon: 'pen', label: '브러시', run: () => app.setTool('brush'), active: () => app.toolId === 'brush' });
  Q('hand', { icon: 'hand', label: '화면 이동', run: () => app.setTool(app.toolId === 'view' ? (app.prevToolId || 'brush') : 'view'), active: () => app.toolId === 'view' });
  Q('panels', { icon: 'side', label: '패널', run: () => app.run('view.toggleSide') });
  Q('layersTab', { icon: 'layers', label: '레이어', run: () => { app.run('view.showSide'); app.bus.emit('dock:show', 'layers'); } });
  Q('colorTab', { icon: 'palette', label: '색', run: () => { app.run('view.showSide'); app.bus.emit('dock:show', 'color'); } });
  Q('fill', { icon: 'fill', label: '채우기', run: () => app.setTool('fill'), active: () => app.toolId === 'fill' });
  Q('selectTool', { icon: 'select', label: '선택', run: () => app.setTool('select'), active: () => app.toolId === 'select' });
  Q('lassoFillQ', { icon: 'lassofill', label: '올가미 채우기', run: () => app.setTool('lassofill'), active: () => app.toolId === 'lassofill' });
}

export function initBottombar() {
  const bar = document.getElementById('bottombar');
  const cat = () => (app.toolId === 'eraser' ? 'eraser' : 'brush');
  const conf = () => (app.settings.bottombar = app.settings.bottombar || { visible: true, items: null });

  const render = () => {
    const c = conf();
    document.body.classList.toggle('no-bottombar', c.visible === false);
    bar.innerHTML = '';
    const chip = h('button.bb-chip', { type: 'button', title: '색 (탭: 색 패널)' });
    chip.onclick = () => { app.run('view.showSide'); app.bus.emit('dock:show', 'color'); };
    bar.append(chip);
    for (const id of c.items || DEFAULT_BOTTOM) {
      const a = app.quickActions.get(id);
      if (!a) continue;
      if (a.type === 'size') {
        bar.append(h('div.bb-slider', null, slider('크기', { min: 0.5, max: 600, step: 0.5, log: true, get: () => activePreset(cat()).size, set: v => { activePreset(cat()).size = v; app.bus.emit('brush:changed', activePreset(cat())); app.requestRender(); }, onEnd: () => app.saveSettingsSoon(), format: v => (v < 10 ? v.toFixed(1) : Math.round(v)) })));
        continue;
      }
      if (a.type === 'opacity') {
        bar.append(h('div.bb-slider.short', null, slider('농도', { min: 0.01, max: 1, step: 0.01, get: () => activePreset(cat()).opacity, set: v => { activePreset(cat()).opacity = v; app.bus.emit('brush:changed', activePreset(cat())); }, onEnd: () => app.saveSettingsSoon(), format: v => Math.round(v * 100) + '%' })));
        continue;
      }
      const b = h('button.bb-btn', { type: 'button', title: a.label, 'data-id': id });
      setHTML(b, `${icon(a.icon, 24)}<span>${esc(a.label)}</span>`);
      b.addEventListener('click', () => { a.run(); sync(); });
      bar.append(b);
    }
    bar.append(h('span.bb-spacer'));
    bar.append(h('button.bb-btn.bb-edit', { type: 'button', title: '버튼 고르기', html: `${icon('gear', 20)}<span>편집</span>`, onclick: editDialog }));
    sync();
  };

  const sync = () => {
    bar.querySelector('.bb-chip')?.style.setProperty('background', app.color.fg);
    bar.querySelectorAll('.bb-btn[data-id]').forEach(b => {
      const a = app.quickActions.get(b.dataset.id);
      b.classList.toggle('on', !!a?.active?.());
      if (b.dataset.id === 'undo') b.disabled = !app.history?.canUndo;
      if (b.dataset.id === 'redo') b.disabled = !app.history?.canRedo;
    });
    bar.querySelectorAll('.ctl').forEach(s => s.sync?.());
  };

  const editDialog = async () => {
    const c = conf();
    const cur = c.items || DEFAULT_BOTTOM;
    const fields = [...app.quickActions.values()].map(a => ({ key: a.id, label: a.label, type: 'checkbox', value: cur.includes(a.id) }));
    const v = await form('아래 막대 버튼 고르기', [
      { key: '_visible', label: '아래 막대 보이기', type: 'checkbox', value: c.visible !== false },
      { key: 'info', type: 'info', label: '체크한 버튼이 아래 막대에 나옵니다 (순서는 체크한 순서대로 뒤에 붙음).' }, ...fields], { okLabel: '적용' });
    if (!v) return;
    c.visible = v._visible;
    c.items = [...cur.filter(id => v[id]), ...Object.keys(v).filter(id => id !== '_visible' && v[id] && !cur.includes(id))];
    app.saveSettingsSoon();
    render();
  };

  for (const e of ['tool:changed', 'brush:changed', 'color:changed', 'history:changed', 'settings:changed', 'view:changed']) app.bus.on(e, sync);
  app.bus.on('quick:changed', render);
  render();
  app.registerCommand('view.bottombar', { label: '아래 막대 보이기/숨기기', run: () => { conf().visible = conf().visible === false; app.saveSettingsSoon(); render(); } });
  app.registerCommand('view.bottombarEdit', { label: '아래 막대 버튼 고르기…', run: editDialog });
  app.addMenuItem('view', { command: 'view.bottombar' });
  app.addMenuItem('view', { command: 'view.bottombarEdit' });
}
