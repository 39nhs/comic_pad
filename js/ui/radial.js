// 원형 메뉴: 펜 측면 버튼(설정) 또는 마우스 오른쪽 버튼을 누르면 펜 주위에 자주 쓰는 도구가 원형으로 뜬다.
// 누른 채 원하는 방향으로 밀고 떼면 선택 (짧게 눌렀다 떼면 메뉴가 남아 있어 탭으로 고르기)
import { app } from '../app.js';
import { h, esc } from '../core/util.js';
import { icon } from './icons.js';
import { form } from './dialogs.js';

const DEFAULT_ITEMS = ['tool:brush', 'tool:eraser', 'tool:eyedropper', 'tool:fill', 'tool:select', 'tool:transform', 'cmd:edit.undo', 'cmd:edit.redo'];
const R = 92;

const items = () => (app.settings.radialItems?.length ? app.settings.radialItems : DEFAULT_ITEMS);

function describe(id) {
  const [kind, key] = id.split(':');
  if (kind === 'tool') { const t = app.tools.get(key); return t && { label: t.name, icon: t.icon, run: () => app.setTool(key), on: app.toolId === key }; }
  const c = app.commands.get(key);
  const ic = { 'edit.undo': 'undo', 'edit.redo': 'redo', 'view.fit': 'fit', 'view.flip': 'flip', 'layer.new': 'plus', 'select.none': 'deselect', 'color.swap': 'swap', 'view.luma': 'eye' }[key] || 'dots';
  return c && { label: c.label.replace(/\s*\(.*?\)\s*/g, '').replace(/…$/, ''), icon: ic, run: () => app.run(key) };
}

let open = null;

export function openRadial(cx, cy, pointerId = null) {
  closeRadial();
  const list = items().map(describe).filter(Boolean);
  const n = list.length;
  const root = h('div.radial', { style: { left: cx + 'px', top: cy + 'px' } });
  const btns = list.map((it, i) => {
    const a = -Math.PI / 2 + (i / n) * Math.PI * 2;
    const b = h(`button.radial-item${it.on ? '.on' : ''}`, { type: 'button', style: { left: Math.cos(a) * R + 'px', top: Math.sin(a) * R + 'px' }, html: `${icon(it.icon, 22)}<span>${esc(it.label)}</span>` });
    b.onclick = (e) => { e.stopPropagation(); pick(i); };
    root.append(b);
    return b;
  });
  root.append(h('div.radial-center', { title: '닫기', onclick: closeRadial }, '×'));
  document.body.append(root);
  let hot = -1, moved = false;
  const pick = (i) => { closeRadial(); list[i]?.run(); };
  const onMove = (e) => {
    if (pointerId != null && e.pointerId !== pointerId) return;
    const dx = e.clientX - cx, dy = e.clientY - cy, d = Math.hypot(dx, dy);
    if (d > 12) moved = true;
    hot = d > 34 ? Math.round(((Math.atan2(dy, dx) + Math.PI / 2 + Math.PI * 2) % (Math.PI * 2)) / (Math.PI * 2) * n) % n : -1;
    btns.forEach((b, i) => b.classList.toggle('hot', i === hot));
  };
  const onUp = (e) => {
    if (pointerId != null && e.pointerId !== pointerId) return;
    removeEventListener('pointermove', onMove, true);
    removeEventListener('pointerup', onUp, true);
    if (moved && hot >= 0) pick(hot);   // 밀고 뗐으면 바로 선택, 아니면 열어 둔 채 탭 기다림
  };
  const onOutside = (e) => { if (!root.contains(e.target)) closeRadial(); };
  addEventListener('pointermove', onMove, true);
  addEventListener('pointerup', onUp, true);
  setTimeout(() => addEventListener('pointerdown', onOutside, true), 0);
  const onKey = (e) => { if (e.key === 'Escape') closeRadial(); };
  addEventListener('keydown', onKey, true);
  open = { root, cleanup: () => { removeEventListener('pointermove', onMove, true); removeEventListener('pointerup', onUp, true); removeEventListener('pointerdown', onOutside, true); removeEventListener('keydown', onKey, true); } };
}

export function closeRadial() {
  if (!open) return;
  open.cleanup();
  open.root.remove();
  open = null;
}

async function editRadial() {
  const opts = [['', '(비움)'],
    ...[...app.tools.values()].map(t => [`tool:${t.id}`, `도구: ${t.name}`]),
    ...['edit.undo', 'edit.redo', 'view.fit', 'view.flip', 'layer.new', 'select.none', 'color.swap', 'view.luma', 'edit.transform', 'layer.duplicate', 'view.toggleUI', 'file.save']
      .filter(k => app.commands.has(k)).map(k => [`cmd:${k}`, `명령: ${app.commands.get(k).label}`])];
  const cur = items();
  const v = await form('원형 메뉴 편집', [
    ...Array.from({ length: 8 }, (_, i) => ({ key: 's' + i, label: `${['위', '오른쪽 위', '오른쪽', '오른쪽 아래', '아래', '왼쪽 아래', '왼쪽', '왼쪽 위'][i]}`, type: 'select', options: opts, value: cur[i] || '' })),
    { key: 'info', type: 'info', label: '설정 > 펜 > "측면 버튼 누르고 그리기"를 "원형 메뉴"로 하면 펜 측면 버튼으로 열립니다. 마우스는 오른쪽 버튼.' },
  ], { okLabel: '저장' });
  if (!v) return;
  app.settings.radialItems = Array.from({ length: 8 }, (_, i) => v['s' + i]).filter(Boolean);
  app.saveSettingsSoon();
}

export function initRadial() {
  app.registerCommand('ui.radial', { label: '원형 메뉴 열기', run: () => { const p = app.pointer; openRadial(p?.cx ?? innerWidth / 2, p?.cy ?? innerHeight / 2); } });
  app.registerCommand('ui.radialEdit', { label: '원형 메뉴 편집…', run: editRadial });
  app.addMenuItem('view', { command: 'ui.radialEdit' });
  app.registerQuickAction('radial', { icon: 'dots', label: '원형 메뉴', run: () => openRadial(innerWidth / 2, innerHeight / 2) });
}
