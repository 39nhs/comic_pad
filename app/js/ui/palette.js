// 명령 검색창 (모든 기능을 이름으로 찾아 실행) + 단축키 편집
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal } from './dialogs.js';

function allEntries() {
  const out = [];
  for (const c of app.commands.values()) {
    if (c.hidden || !c.label) continue;
    out.push({ label: c.label.replace(/…$/, ''), hint: menuOf(c.id), run: () => app.run(c.id), id: c.id });
  }
  for (const t of app.tools.values()) out.push({ label: `도구: ${t.name}`, hint: t.key ? t.key.toUpperCase() : '', run: () => app.setTool(t.id), id: 'tool:' + t.id });
  return out;
}

function menuOf(id) {
  for (const mid of app.menuOrder) {
    const m = app.menus.get(mid);
    if (m.items.some(i => i.command === id)) return m.label;
  }
  return '';
}

/** 초성/부분 일치 검색 */
const CHO = 'ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ';
const chosung = (s) => [...s].map(c => { const k = c.charCodeAt(0) - 0xac00; return k >= 0 && k < 11172 ? CHO[Math.floor(k / 588)] : c; }).join('');
function score(label, q) {
  const L = label.toLowerCase(), Q = q.toLowerCase().replace(/\s+/g, '');
  const flat = L.replace(/\s+/g, '');
  if (flat.includes(Q)) return 100 - flat.indexOf(Q);
  if (chosung(flat).includes(Q)) return 50;
  let i = 0;
  for (const c of flat) if (c === Q[i]) i++;
  return i === Q.length ? 10 : -1;
}

export function openPalette() {
  const input = h('input.palette-input', { type: 'search', placeholder: '기능 이름 검색 (예: 곡선, 톤, ㄱㅅ)…' });
  const list = h('div.palette-list');
  const entries = allEntries();
  let sel = 0, shown = [];
  let modal;
  const render = () => {
    const q = input.value.trim();
    shown = q ? entries.map(e => ({ e, s: Math.max(score(e.label, q), score(e.hint || '', q) - 30) })).filter(x => x.s >= 0).sort((a, b) => b.s - a.s).map(x => x.e).slice(0, 60)
      : (app.settings.recentCommands || []).map(id => entries.find(e => e.id === id)).filter(Boolean).concat(entries.slice(0, 30));
    sel = Math.min(sel, Math.max(0, shown.length - 1));
    list.innerHTML = '';
    shown.forEach((e, i) => {
      const b = h(`button.palette-item${i === sel ? '.on' : ''}`, { type: 'button' }, h('span', null, e.label), h('small', null, e.hint));
      b.onclick = () => go(e);
      list.append(b);
    });
  };
  const go = (e) => {
    modal.close(true);
    const r = app.settings.recentCommands = [e.id, ...(app.settings.recentCommands || []).filter(x => x !== e.id)].slice(0, 8);
    void r;
    app.saveSettingsSoon();
    setTimeout(() => e.run(), 0);
  };
  input.addEventListener('input', () => { sel = 0; render(); });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown') { sel = Math.min(shown.length - 1, sel + 1); render(); e.preventDefault(); }
    if (e.key === 'ArrowUp') { sel = Math.max(0, sel - 1); render(); e.preventDefault(); }
    if (e.key === 'Enter' && shown[sel]) { e.preventDefault(); e.stopPropagation(); go(shown[sel]); }
  });
  modal = openModal({ title: '기능 검색', body: h('div', null, input, list), cls: 'palette-modal' });
  render();
  setTimeout(() => input.focus(), 30);
}

export function shortcutEditor() {
  const list = h('div.key-list');
  const filter = h('input', { type: 'search', placeholder: '검색…' });
  const keyOf = (id) => (id in app.settings.keys ? app.settings.keys[id] : app.defaultKeys[id] || app.commands.get(id)?.key || '');
  const render = () => {
    list.innerHTML = '';
    const q = filter.value.trim();
    for (const c of app.commands.values()) {
      if (c.hidden || !c.label || (q && !c.label.includes(q))) continue;
      const kb = h('button.keycap', { type: 'button', title: '눌러서 새 단축키 입력' }, keyOf(c.id) || '—');
      kb.onclick = () => {
        kb.textContent = '키를 누르세요… (Esc 취소, Backspace 지우기)';
        const onKey = (e) => {
          e.preventDefault(); e.stopPropagation();
          if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return;
          window.removeEventListener('keydown', onKey, true);
          if (e.key === 'Escape') { render(); return; }
          if (e.key === 'Backspace') { app.settings.keys[c.id] = ''; app.saveSettingsSoon(); render(); return; }
          let k = e.key.toLowerCase();
          if (k === ' ') k = 'space';
          const parts = [];
          if (e.ctrlKey || e.metaKey) parts.push('ctrl');
          if (e.altKey) parts.push('alt');
          if (e.shiftKey) parts.push('shift');
          parts.push(k);
          const combo = parts.join('+');
          // 같은 키를 쓰던 명령은 비워 준다
          for (const other of app.commands.values()) if (other.id !== c.id && keyOf(other.id) === combo) app.settings.keys[other.id] = '';
          app.settings.keys[c.id] = combo;
          app.saveSettingsSoon();
          render();
        };
        window.addEventListener('keydown', onKey, true);
      };
      list.append(h('div.key-row', null, h('span', null, c.label), kb));
    }
  };
  filter.addEventListener('input', render);
  render();
  openModal({
    title: '단축키 편집', wide: true,
    body: h('div', null, filter, list),
    actions: [{ label: '모두 기본값으로', run: () => { app.settings.keys = {}; app.saveSettingsSoon(); render(); return false; } }, { label: '닫기', primary: true }],
  });
}

export function registerPaletteCommands() {
  app.registerCommand('palette.open', { label: '기능 검색…', run: openPalette, key: 'ctrl+k' });
  app.registerCommand('settings.keys', { label: '단축키 편집…', run: shortcutEditor });
  app.addMenuItem('help', { command: 'palette.open' });
  app.addMenuItem('help', { command: 'settings.keys' });
  app.registerQuickAction('palette', { icon: 'search', label: '기능 검색', run: openPalette });
}
