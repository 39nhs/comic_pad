// 패널·설정 창을 앱 밖 별도 창으로 꺼내기 (두 번째 모니터, 또는 "항상 위" 작은 창)
//  - 항상 위: Chrome/Edge 의 Document Picture-in-Picture (한 번에 하나)
//  - 보통 창: window.open (여러 개 가능, 다른 모니터로 옮기기 좋음)
// 같은 앱의 화면 조각을 그대로 옮기는 방식이라, 꺼낸 창에서 바꾸면 바로 반영된다.
import { app } from '../app.js';
import { h } from '../core/util.js';
import { form } from './dialogs.js';
import { showDockTab } from './panels.js';
import { settingsBody } from './settings.js';

const popped = new Map();   // panelId -> { win, el, home }
const isField = (t) => t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);

async function makeWindow(title, w, hgt, onTop) {
  let win;
  if (onTop && 'documentPictureInPicture' in window) {
    win = await window.documentPictureInPicture.requestWindow({ width: w, height: hgt });
  } else {
    win = window.open('', '', `popup=yes,width=${w},height=${hgt}`);
    if (!win) throw new Error('창이 막혔습니다. 브라우저의 팝업 차단을 이 주소에 대해 허용해 주세요');
  }
  const d = win.document;
  app.noNativeMenu?.(d);   // 꺼낸 창에서도 브라우저 오른쪽 클릭 메뉴 막기
  d.title = `${title} — ComicPad`;
  d.documentElement.lang = 'ko';
  d.documentElement.style.cssText = document.documentElement.style.cssText;
  for (const l of document.querySelectorAll('link[rel="stylesheet"]')) { const x = d.createElement('link'); x.rel = 'stylesheet'; x.href = l.href; d.head.append(x); }
  for (const st of document.querySelectorAll('style')) d.head.append(st.cloneNode(true));
  const meta = d.createElement('meta'); meta.name = 'viewport'; meta.content = 'width=device-width, initial-scale=1'; d.head.append(meta);
  d.body.className = `popout-win ${document.body.className}`;
  // 꺼낸 창에 손이 가 있어도 단축키(실행취소 등)는 앱에서 동작하도록 넘겨 줌
  for (const type of ['keydown', 'keyup']) {
    win.addEventListener(type, (e) => {
      if (isField(e.target)) return;
      const ev = new KeyboardEvent(type, { key: e.key, code: e.code, ctrlKey: e.ctrlKey, shiftKey: e.shiftKey, altKey: e.altKey, metaKey: e.metaKey, repeat: e.repeat, bubbles: true, cancelable: true });
      window.dispatchEvent(ev);
      if (ev.defaultPrevented) e.preventDefault();
    });
  }
  return win;
}

function header(win, title, onBack) {
  return h('div.popout-head', null, h('b', null, title), h('span.spacer'),
    onBack ? h('button.btn', { type: 'button', onclick: () => { onBack(); win.close(); } }, '앱으로 되돌리기') : null);
}

export async function popOutPanel(id, onTop = !!app.settings.popoutOnTop) {
  const p = app.panels.get(id);
  if (!p) return;
  if (popped.has(id)) { popped.get(id).win.focus(); return; }
  showDockTab(id);   // 아직 한 번도 안 열렸으면 만들어지도록
  const el = document.querySelector(`.panel-body[data-panel="${id}"]`);
  if (!el) { app.toast('패널을 찾지 못했습니다'); return; }
  let win;
  try { win = await makeWindow(p.title, 360, 560, onTop); } catch (e) { app.toast(e.message, 'warn'); return; }
  const home = el.parentNode;
  const back = () => {
    if (!popped.has(id)) return;
    popped.delete(id);
    el._popped = false;
    p.popped = false;
    home.append(el);
    app.bus.emit('panels:changed');
    showDockTab(id);
  };
  popped.set(id, { win, el, home });
  el._popped = true;
  p.popped = true;
  el.hidden = false;
  win.document.body.append(header(win, p.title, back), el);
  win.addEventListener('pagehide', back);
  app.bus.emit('panels:changed');   // 오른쪽 패널에서는 그 탭을 잠시 뺀다
}

export async function popOutSettings(onTop = !!app.settings.popoutOnTop) {
  let win;
  try { win = await makeWindow('환경 설정', 520, 720, onTop); } catch (e) { app.toast(e.message, 'warn'); return; }
  document.querySelectorAll('.modal-backdrop').forEach(m => { if (m.querySelector('.settings')) m.remove(); });
  win.document.body.append(header(win, '환경 설정'), h('div.popout-content', null, settingsBody()));
}

async function chooser() {
  const list = [...app.panels.values()];
  const v = await form('앱 밖 창으로 꺼내기', [
    { key: 'what', label: '꺼낼 것', type: 'select', options: [['settings', '환경 설정'], ...list.map(p => [p.id, `${p.title} 패널`])], value: 'color' },
    { key: 'top', label: '항상 위에 작은 창으로 (한 번에 하나만, 꺼내면 화면 위에 떠 있음)', type: 'checkbox', value: !!app.settings.popoutOnTop },
    { key: 'info', type: 'info', label: '보통 창은 다른 모니터로 끌어다 놓기 좋고, 여러 개를 꺼낼 수 있습니다. 창을 닫으면 패널은 오른쪽으로 돌아옵니다.' },
  ], { okLabel: '꺼내기' });
  if (!v) return;
  app.settings.popoutOnTop = v.top;
  app.saveSettingsSoon();
  if (v.what === 'settings') popOutSettings(v.top); else popOutPanel(v.what, v.top);
}

export function initPopout() {
  app.registerCommand('popout.panel', { label: '패널을 앱 밖 창으로…', run: (id) => (typeof id === 'string' ? popOutPanel(id) : chooser()) });
  app.registerCommand('popout.settings', { label: '환경 설정을 앱 밖 창으로', run: () => popOutSettings() });
  app.addMenuItem('view', { command: 'popout.panel' });
  // 앱을 닫으면 꺼낸 창도 닫기
  addEventListener('pagehide', () => { for (const { win } of popped.values()) try { win.close(); } catch { /* 무시 */ } });
}
