// 캔버스 아래쪽에 뜨는 작업 막대 (선택 메뉴, 변형 확정 등)
import { app } from '../app.js';
import { h } from '../core/util.js';
import { icon } from './icons.js';

const bars = new Map();

function host() {
  let el = document.getElementById('bars');
  if (!el) {
    el = h('div.bars');
    el.id = 'bars';
    document.getElementById('stage-overlay').append(el);
  }
  return el;
}

/** items: [{ icon, label, run, cls }] */
export function showBar(id, items) {
  hideBar(id);
  const bar = h('div.actbar');
  for (const it of items) {
    if (it.sep) { bar.append(h('span.actbar-sep')); continue; }
    const b = h(`button.actbtn${it.cls ? '.' + it.cls : ''}`, { type: 'button', title: it.label });
    b.innerHTML = `${it.icon ? icon(it.icon, 20) : ''}<span>${it.label}</span>`;
    b.addEventListener('click', (e) => { e.stopPropagation(); it.run(); });
    bar.append(b);
  }
  bars.set(id, bar);
  host().append(bar);
  return bar;
}

export function hideBar(id) {
  bars.get(id)?.remove();
  bars.delete(id);
}

// ---- 선택 영역 메뉴 (CSP의 선택 범위 런처) ----
export const selectionActions = [
  { icon: 'deselect', label: '해제', command: 'select.none' },
  { icon: 'invert', label: '반전', command: 'select.invert' },
  { icon: 'plus', label: '확장', command: 'select.grow' },
  { icon: 'minus', label: '축소', command: 'select.shrink' },
  { icon: 'trash', label: '지우기', command: 'edit.clear' },
  { icon: 'fill', label: '채우기', command: 'edit.fill' },
  { icon: 'transform', label: '변형', command: 'edit.transform' },
  { icon: 'copy', label: '새 레이어로', command: 'select.toLayer' },
  { icon: 'star', label: '에셋 저장', command: 'asset.saveSelection' },
];

export function registerSelectionAction(a) {
  selectionActions.push(a);
  refreshSelectionBar();
}

export function refreshSelectionBar() {
  const sel = app.doc?.selection;
  if (sel?.active && !app.floating) {
    showBar('selection', selectionActions.map(a => ({ ...a, run: () => (a.run ? a.run() : app.run(a.command)) })));
  } else hideBar('selection');
}

app.bus.on('selection:changed', refreshSelectionBar);
app.bus.on('transform:changed', refreshSelectionBar);
app.bus.on('doc:changed', refreshSelectionBar);
app.registerSelectionAction = registerSelectionAction;
