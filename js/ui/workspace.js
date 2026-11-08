// 작업 화면 배치 저장: 패널 탭·위아래 비율·왼손잡이·퀵바·아래 막대·도구·브러시를 이름 붙여 저장하고 바꿔 쓰기
import { app } from '../app.js';
import { h } from '../core/util.js';
import { form, openModal, confirmDialog } from './dialogs.js';
import { showDockTab } from './panels.js';
import { setActivePreset } from '../tools/brush.js';

const KEYS = ['dock_top', 'dock_bottom', 'dockSplit', 'sideVisible', 'leftHanded', 'quickbar', 'bottombar'];
const list = () => (app.settings.workspaces ||= []);

function snapshot(name) {
  const s = app.settings;
  const o = { name, tool: app.toolId, preset: { brush: s.activePreset?.brush, eraser: s.activePreset?.eraser } };
  const DEF = { sideVisible: true, leftHanded: false, dockSplit: 0.48 };
  for (const k of KEYS) { const v = s[k] ?? DEF[k]; if (v !== undefined) o[k] = structuredClone(v); }
  return o;
}

export function applyWorkspace(w) {
  const s = app.settings;
  for (const k of KEYS) if (w[k] !== undefined) s[k] = structuredClone(w[k]);
  document.body.classList.toggle('left-handed', !!s.leftHanded);
  document.body.classList.toggle('no-side', s.sideVisible === false);
  document.getElementById('sidepanel')?.style.setProperty('--split', `${(s.dockSplit ?? 0.48) * 100}%`);
  if (w.dock_top) showDockTab(w.dock_top);
  if (w.dock_bottom) showDockTab(w.dock_bottom);
  if (w.preset?.brush) setActivePreset('brush', w.preset.brush);
  if (w.preset?.eraser) setActivePreset('eraser', w.preset.eraser);
  if (w.tool && app.tools.has(w.tool)) app.setTool(w.tool);
  app.bus.emit('quick:changed');
  app.bus.emit('settings:changed');
  app.saveSettingsSoon();
  setTimeout(() => app.run('view.fit'), 200);
  app.toast(`작업 화면: ${w.name}`);
}

async function saveDialog() {
  const v = await form('작업 화면 저장', [
    { key: 'name', label: '이름 (예: 펜선, 채색, 보정)', type: 'text', value: '' },
    { key: 'info', type: 'info', label: '지금의 패널 탭·위아래 비율·왼손잡이·퀵바·아래 막대 버튼·도구·브러시를 저장합니다. 앞의 네 개는 Ctrl+Alt+1~4 로 바로 바꿀 수 있습니다.' },
  ], { okLabel: '저장' });
  if (!v?.name?.trim()) return;
  const name = v.name.trim();
  const arr = list();
  const i = arr.findIndex(w => w.name === name);
  if (i >= 0) arr[i] = snapshot(name); else arr.push(snapshot(name));
  app.saveSettingsSoon();
  app.toast(`"${name}" 작업 화면을 저장했습니다`);
}

function manageDialog() {
  let modal;
  const body = h('div.local-list');
  const render = () => {
    body.innerHTML = '';
    const arr = list();
    if (!arr.length) body.append(h('div.help', null, '저장한 작업 화면이 없습니다. "지금 화면 저장"을 눌러 보세요.'));
    arr.forEach((w, i) => body.append(h('div.local-row', null,
      h('div', null, h('b', null, w.name), h('small', null, i < 4 ? ` · Ctrl+Alt+${i + 1}` : '')),
      h('div.row', null,
        h('button.btn.primary', { type: 'button', onclick: () => { modal.close(true); applyWorkspace(w); } }, '바꾸기'),
        h('button.btn', { type: 'button', onclick: () => { arr[i] = snapshot(w.name); app.saveSettingsSoon(); app.toast('지금 화면으로 덮어썼습니다'); } }, '지금 화면으로 덮어쓰기'),
        i > 0 ? h('button.btn', { type: 'button', onclick: () => { [arr[i - 1], arr[i]] = [arr[i], arr[i - 1]]; app.saveSettingsSoon(); render(); } }, '▲') : null,
        h('button.btn', { type: 'button', onclick: async () => { if (await confirmDialog(`"${w.name}" 을(를) 지울까요?`, '지우기', true)) { arr.splice(i, 1); app.saveSettingsSoon(); render(); } } }, '지우기')))));
  };
  render();
  modal = openModal({ title: '작업 화면', body: h('div', null, h('div.row', null, h('button.btn', { type: 'button', onclick: async () => { await saveDialog(); render(); } }, '지금 화면 저장…')), body), actions: [{ label: '닫기', primary: true }] });
}

export function initWorkspaces() {
  app.registerCommand('workspace.save', { label: '작업 화면 저장…', run: saveDialog });
  app.registerCommand('workspace.manage', { label: '작업 화면 바꾸기·관리…', run: manageDialog });
  for (let i = 0; i < 4; i++) {
    app.registerCommand(`workspace.${i + 1}`, {
      label: `작업 화면 ${i + 1}번으로`, key: `ctrl+alt+${i + 1}`,
      run: () => { const w = list()[i]; if (w) applyWorkspace(w); else app.toast(`${i + 1}번 작업 화면이 없습니다 (보기 > 작업 화면 저장)`); },
    });
  }
  for (const it of [{ sep: true }, { command: 'workspace.save' }, { command: 'workspace.manage' }]) app.addMenuItem('view', it);
}
