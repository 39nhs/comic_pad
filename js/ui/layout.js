// 화면 구성: 기본은 "간단히" (자주 쓰는 도구·메뉴만), 나머지는 "모든 항목 보기"·기능 검색(Ctrl+K)으로.
// 화면 꾸미기 창에서 도구 막대·메뉴 항목·패널 탭을 직접 고를 수 있다.
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal } from './dialogs.js';
import { icon } from './icons.js';

export const SIMPLE_TOOLS = ['brush', 'eraser', 'fill', 'eyedropper', 'select', 'transform', 'shape', 'ruler', 'panel', 'bubble', 'text', 'view'];
const SIMPLE_MENUS = ['file', 'edit', 'layer', 'select', 'comic', 'adjust', 'filter', 'ai', 'view', 'help'];
export const FREQUENT = {
  file: ['file.new', 'file.open', 'file.save', 'file.saveAs', 'file.import', 'file.export', 'file.versions', 'file.storage', 'file.printExport', 'file.webtoonExport', 'file.backup', 'pixel.newDoc', 'file.ebook'],
  edit: ['edit.undo', 'edit.redo', 'edit.cut', 'edit.copy', 'edit.paste', 'edit.clear', 'edit.fill', 'edit.transform', 'edit.flipH', 'edit.canvasSize', 'edit.contentAwareFill', 'edit.puppet', 'macro.open'],
  layer: ['layer.new', 'layer.newVector', 'layer.newRough', 'layer.newFolder', 'layer.group', 'layer.duplicate', 'layer.delete', 'layer.mergeDown', 'layer.clip', 'layer.alphaLock', 'layer.lock', 'layer.addMask'],
  select: ['select.all', 'select.none', 'select.invert', 'select.grow', 'select.shrink', 'select.toLayer', 'select.refine', 'path.tool'],
  comic: ['panel.frame', 'panel.templates', '@컷 나누기 도구', 'sfx.tool', 'print.preflight', '@말풍선 도구', '@텍스트 도구', 'color.autoFlat', 'color.hintLayer', 'color.hintFill', 'progress.open', 'print.setup', 'story.new', 'story.prev', 'story.next', 'story.add', '3d.open'],
  adjust: ['adjust.levels', 'adjust.curves', 'adjust.hsl', 'adjust.bc', 'adjust.gradmap', 'adjust.bw'],
  filter: ['filter.lineExtract', 'filter.lineClean', 'filter.mono', 'filter.blur', 'filter.outline', 'filter.shadow', 'ai.img2img', 'pixel.quantize'],
  view: ['view.fit', 'view.actual', 'view.zoomIn', 'view.zoomOut', 'view.rotL', 'view.rotR', 'view.resetRotation', 'view.flip', 'view.luma', 'view.toggleUI', 'view.fullscreen', 'workspace.manage', 'ui.customize', 'ui.mode', 'pixel.grid'],
  ai: ['ai.lineart', 'ai.cutout', 'ai.select', 'ai.depth', 'ai.upscale', 'ai.pose', 'filter.lineClean', 'ai.img2img', 'ai.setup', 'ai.models', 'vt.studio', 'vt.setup'],
  help: ['settings.open', 'help.manual', 'setup.choose', 'palette.open', 'help.features', 'help.keys', 'pen.diagnose', 'mobile.connect', 'selftest.run', 'help.update', 'help.about'],
};

export const ui = () => (app.settings.ui ||= { mode: 'simple' });
export const isSimple = () => ui().mode !== 'full';
const itemKey = (it) => it.command || (it.label ? '@' + it.label : '');

/** 메뉴 막대에 보일 메뉴 */
export function menuShown(id) {
  const u = ui();
  if (u.menus?.[id] === false) return false;
  if (u.menus?.[id] === true) return true;
  return !isSimple() || SIMPLE_MENUS.includes(id);
}

/** 메뉴 항목: 간단히 모드에서는 고른 것만. 반환 { items, hidden(개수) } */
export function menuItems(id, items, showAll = false) {
  if (showAll || !isSimple()) return { items, hidden: 0 };
  const pick = ui().menuPick?.[id];
  const want = new Set(pick || FREQUENT[id] || items.filter(i => !i.sep).map(itemKey));
  const out = [];
  let hidden = 0;
  for (const it of items) {
    if (it.sep) { if (out.length && !out[out.length - 1].sep) out.push(it); continue; }
    if (want.has(itemKey(it))) out.push(it); else hidden++;
  }
  while (out.length && out[out.length - 1].sep) out.pop();
  return { items: out, hidden };
}

/** 도구 막대: { shown, more } */
export function toolLists(allIds) {
  if (!isSimple()) return { shown: allIds, more: [] };
  const pick = ui().tools || SIMPLE_TOOLS;
  const shown = allIds.filter(id => pick.includes(id));
  if (app.toolId && !shown.includes(app.toolId) && allIds.includes(app.toolId)) shown.push(app.toolId);   // 지금 도구는 늘 보이게
  return { shown, more: allIds.filter(id => !shown.includes(id)) };
}

export const panelShown = (id) => !(ui().hiddenPanels || []).includes(id);

function refresh() {
  app.saveSettingsSoon();
  app.bus.emit('ui:layout');
  app.bus.emit('menus:changed');
  app.bus.emit('panels:changed');
}

export function setMode(mode) {
  ui().mode = mode;
  refresh();
  app.toast(mode === 'full' ? '모든 도구·메뉴를 보여 줍니다' : '자주 쓰는 것만 보여 줍니다 (나머지는 메뉴 맨 아래 "모든 항목 보기" 또는 Ctrl+K)');
}

/** 도구 막대 "더 보기" 팝업 */
export function moreToolsPopup(anchor, ids) {
  const layer = document.getElementById('menu-layer');
  layer.innerHTML = '';
  const r = anchor.getBoundingClientRect();
  const box = h('div.menu.tool-more', { style: { left: r.right + 6 + 'px', top: Math.max(4, r.top - 40) + 'px' } });
  for (const id of ids) {
    const t = app.tools.get(id);
    box.append(h('button.menu-item', { type: 'button', onclick: () => { layer.innerHTML = ''; layer.classList.remove('open'); app.setTool(id); } },
      h('span', { html: `${icon(t.icon, 18)} ${t.name}` }), h('kbd', null, t.key ? t.key.toUpperCase() : '')));
  }
  box.append(h('div.menu-sep'), h('button.menu-item', { type: 'button', onclick: () => { layer.innerHTML = ''; layer.classList.remove('open'); customizeDialog('tools'); } }, h('span', null, '도구 막대 꾸미기…')));
  layer.append(box);
  layer.classList.add('open');
  const mr = box.getBoundingClientRect();
  if (mr.bottom > innerHeight) box.style.top = Math.max(4, innerHeight - mr.height - 8) + 'px';
}

// ---------------------------------------------------------------------
// 화면 꾸미기 창
// ---------------------------------------------------------------------
export function customizeDialog(tab = 'tools') {
  const u = ui();
  const body = h('div.customize');
  const tabs = [['tools', '도구 막대'], ['menus', '메뉴'], ['panels', '패널 탭'], ['etc', '그 밖']];
  const head = h('div.seg');
  const content = h('div.customize-body');
  const select = (k) => {
    tab = k;
    head.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.k === k));
    render();
  };
  for (const [k, t] of tabs) head.append(h('button', { type: 'button', 'data-k': k, onclick: () => select(k) }, t));
  const check = (label, on, set, extra) => {
    const c = h('input', { type: 'checkbox' });
    c.checked = on;
    c.onchange = () => { set(c.checked); refresh(); };
    return h('label.cz-row', null, c, h('span', { html: label }), extra || null);
  };
  const render = () => {
    content.innerHTML = '';
    content.append(h('div.row', null,
      h(`button.btn${isSimple() ? '.primary' : ''}`, { type: 'button', onclick: () => { setMode('simple'); render(); } }, '간단히 (자주 쓰는 것만)'),
      h(`button.btn${!isSimple() ? '.primary' : ''}`, { type: 'button', onclick: () => { setMode('full'); render(); } }, '모든 기능 보기')));
    if (tab === 'tools') {
      const ids = [...app.tools.keys()];
      const pick = new Set(u.tools || SIMPLE_TOOLS);
      content.append(h('div.help', null, '간단히 모드에서 왼쪽 도구 막대에 꺼내 둘 도구. 나머지는 도구 막대의 "…" 버튼에 있습니다.'));
      const grid = h('div.cz-grid');
      for (const id of ids) {
        const t = app.tools.get(id);
        grid.append(check(`${icon(t.icon, 18)} ${t.name}`, pick.has(id), (v) => { if (v) pick.add(id); else pick.delete(id); u.tools = ids.filter(x => pick.has(x)); }));
      }
      content.append(grid);
    } else if (tab === 'menus') {
      content.append(h('div.help', null, '메뉴 막대에 보일 메뉴와, 간단히 모드에서 각 메뉴에 꺼내 둘 항목. 숨긴 항목은 메뉴 맨 아래 "모든 항목 보기"로 언제든 볼 수 있습니다.'));
      for (const id of app.menuOrder) {
        const m = app.menus.get(id);
        const box = h('details.cz-menu');
        const sum = h('summary', null);
        const mc = h('input', { type: 'checkbox' });
        mc.checked = menuShown(id);
        mc.onclick = (e) => e.stopPropagation();
        mc.onchange = () => { (u.menus ||= {})[id] = mc.checked; refresh(); };
        sum.append(mc, h('b', null, ` ${m.label}`));
        box.append(sum);
        const keys = m.items.filter(i => !i.sep).map(itemKey);
        const cur = new Set(u.menuPick?.[id] || FREQUENT[id] || keys);
        for (const it of m.items) {
          if (it.sep) continue;
          const k = itemKey(it);
          const label = it.label || app.commands.get(it.command)?.label || it.command;
          box.append(check(label, cur.has(k), (v) => { if (v) cur.add(k); else cur.delete(k); (u.menuPick ||= {})[id] = keys.filter(x => cur.has(x)); }));
        }
        content.append(box);
      }
    } else if (tab === 'panels') {
      content.append(h('div.help', null, '오른쪽 패널에 보일 탭. (패널 탭 옆 ⧉ 버튼으로 앱 밖 창으로 꺼낼 수도 있습니다)'));
      for (const [id, p] of app.panels) {
        content.append(check(`${icon(p.icon || 'dots', 16)} ${p.title}`, panelShown(id), (v) => {
          const set = new Set(u.hiddenPanels || []);
          if (v) set.delete(id); else set.add(id);
          u.hiddenPanels = [...set];
        }));
      }
    } else {
      content.append(
        h('div.row', null, h('button.btn', { type: 'button', onclick: () => app.run('view.bottombarEdit') }, '아래 큰 버튼 막대 고르기…'), h('button.btn', { type: 'button', onclick: () => app.run('ui.radialEdit') }, '원형 메뉴 편집…')),
        h('div.row', null, h('button.btn', { type: 'button', onclick: () => app.run('workspace.manage') }, '작업 화면 저장·바꾸기…'), h('button.btn', { type: 'button', onclick: () => app.run('popout.settings') }, '설정 창을 앱 밖으로…')),
        h('div.row', null, h('button.btn', { type: 'button', onclick: () => { delete u.tools; delete u.menus; delete u.menuPick; delete u.hiddenPanels; u.mode = 'simple'; refresh(); render(); app.toast('화면 구성을 처음 상태로 되돌렸습니다'); } }, '화면 구성 초기화')),
      );
    }
  };
  body.append(head, content);
  select(tab);
  openModal({ title: '화면 꾸미기', body, wide: true, actions: [{ label: '닫기', primary: true }] });
}

export function initLayout() {
  app.registerCommand('ui.customize', { label: '화면 꾸미기 (도구·메뉴·패널)…', run: () => customizeDialog() });
  app.registerCommand('ui.mode', { label: '간단히 ↔ 모든 기능 보기', run: () => setMode(isSimple() ? 'full' : 'simple') });
  for (const it of [{ sep: true }, { command: 'ui.customize' }, { command: 'ui.mode' }]) app.addMenuItem('view', it);
}
