// 휴대폰/태블릿: 작은 화면 배치, 같은 와이파이로 PC 에 연결(QR), 앱처럼 설치, 기기 임시 저장함
import { app } from '../app.js';
import { h, downloadBlob } from '../core/util.js';
import { openModal, confirmDialog } from './dialogs.js';
import { server, openProjectFile } from '../core/io.js';
import { localStore } from '../core/localstore.js';
import { qrCanvas } from '../core/qr.js';
import { button, row } from './controls.js';
import { icon } from './icons.js';

export function initMobile() {
  const isCompact = () => innerWidth < 900 || innerHeight < 520;
  let was = null;
  const apply = () => {
    const c = isCompact();
    document.body.classList.toggle('compact', c);
    if (!c) document.body.classList.remove('side-open');
    // 화면 배치가 바뀌면(회전 등) 그림을 화면에 다시 맞춤
    if (was !== null && was !== c) setTimeout(() => app.run('view.fit'), 250);
    was = c;
  };
  addEventListener('resize', apply);
  apply();
  setTimeout(() => app.run('view.fit'), 300);
  // 휴대폰: 메뉴바 대신 메뉴 버튼 하나
  const mb = h('button.icon-btn.menu-btn', { type: 'button', title: '메뉴', html: icon('menu', 22), onclick: menuSheet });
  document.getElementById('topbar').prepend(mb);
  // 서랍처럼 열린 패널은 그림을 만지면 닫힘
  document.getElementById('stage').addEventListener('pointerdown', () => document.body.classList.remove('side-open'), true);
  if (server.remote) document.body.classList.add('remote');

  // 앱으로 설치 (https 또는 PC 의 localhost 에서만 가능)
  if (window.isSecureContext && 'serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').then(() => navigator.serviceWorker.ready).then(r => setTimeout(() => r.active?.postMessage('precache'), 8000)).catch(() => {});   // 앱이 다 켜진 뒤에 (켜지는 중에 같이 받으면 로딩이 실패할 수 있음)
  let installPrompt = null;
  addEventListener('beforeinstallprompt', (e) => { e.preventDefault(); installPrompt = e; });

  app.registerCommand('mobile.connect', { label: '휴대폰·태블릿 연결 (QR)…', run: connectDialog });
  app.registerCommand('mobile.install', {
    label: '앱으로 설치 (홈 화면/시작 메뉴)',
    run: async () => {
      if (installPrompt) { installPrompt.prompt(); installPrompt = null; return; }
      openModal({
        title: '앱으로 설치', body: h('div.help-text', null,
          h('p', null, '안드로이드 Chrome / 삼성 인터넷: 메뉴(⋮) → "홈 화면에 추가" 또는 "앱 설치"'),
          h('p', null, '아이패드/아이폰 Safari: 공유 버튼 → "홈 화면에 추가"'),
          h('p', null, 'PC Edge: 주소창 오른쪽의 "앱 설치" 아이콘 (또는 바탕화면의 ComicPad 바로가기 사용)'),
          h('p', null, 'PC 없이 휴대폰·태블릿만으로 쓰기: 휴대폰 브라우저로 https://39nhs.github.io/comic_pad/ 를 열고 위 방법으로 설치하면, 인터넷이 없어도 열리고 작품은 그 기기 안에 저장됩니다 (AI 기능·PC 폴더 저장은 PC 버전에서만).')),
        actions: [{ label: '닫기', primary: true }],
      });
    },
  });
  app.registerCommand('file.local', { label: '기기 임시 저장함 (연결 끊겼을 때 저장된 것)…', run: localDialog });
  for (const id of ['mobile.connect', 'mobile.install']) app.addMenuItem('help', { command: id });
  app.addMenuItem('file', { command: 'file.local' });
  app.registerQuickAction('connect', { icon: 'phone', label: '휴대폰 연결', run: connectDialog });

  // 연결이 다시 되면 임시 저장된 게 있는지 알려 줌
  let wasOnline = server.online;
  app.bus.on('settings:changed', async () => {
    if (server.online && !wasOnline) {
      const items = await localStore.list().catch(() => []);
      if (items.some(i => i.name.startsWith('projects/'))) app.toast(`${server.mode === 'http' ? 'PC 연결' : '폴더 저장'}이 돌아왔습니다. 파일 > 기기 임시 저장함에서 옮기세요`);
    }
    wasOnline = server.online;
  });
}

function menuSheet() {
  const body = h('div.menu-sheet');
  let modal;
  for (const id of app.menuOrder) {
    const m = app.menus.get(id);
    const items = m.items.filter(it => !it.sep);
    if (!items.length) continue;
    const grid = h('div.menu-sheet-grid');
    for (const it of items) {
      const cmd = it.command ? app.commands.get(it.command) : null;
      const label = it.label || cmd?.label || it.command;
      grid.append(h('button.btn', { type: 'button', onclick: () => { modal.close(true); setTimeout(() => (it.run ? it.run() : app.run(it.command)), 0); } }, label));
    }
    body.append(h('details', { open: id === 'file' }, h('summary', null, m.label), grid));
  }
  modal = openModal({ title: '메뉴', body, cls: 'menu-modal' });
}

async function connectDialog() {
  if (server.remote) {
    openModal({ title: '연결 상태', body: h('div.help-text', null, h('p', null, `이 기기는 PC 의 ComicPad 에 연결되어 있습니다 (${location.host}).`), h('p', null, '작업은 PC 의 G: 드라이브에 저장됩니다. 같은 그림을 PC 와 동시에 고치면 나중에 저장한 쪽이 남습니다.')), actions: [{ label: '닫기', primary: true }] });
    return;
  }
  let info;
  try { info = await (await fetch('/api/info', { cache: 'no-store' })).json(); } catch { app.toast('서버에 연결되지 않았습니다', 'error'); return; }
  const body = h('div.connect');
  let modal;
  const restart = async (on) => {
    modal.close(true);
    await app.task(on ? '휴대폰 연결 켜는 중' : '휴대폰 연결 끄는 중', async () => {
      await fetch(`/api/lan?on=${on ? 1 : 0}`, { method: 'POST' });
      // 서버가 다시 켜질 때까지 기다림
      for (let i = 0; i < 40; i++) { await new Promise(r => setTimeout(r, 400)); if (await server.ping()) break; }
    }, { delay: 0 });
    connectDialog();
  };
  if (!info.lan) {
    body.append(
      h('p', null, '같은 와이파이에 있는 휴대폰·태블릿에서 이 PC 의 ComicPad 를 쓸 수 있습니다. 작업은 PC(G: 드라이브)에 저장됩니다.'),
      h('p.help', null, '처음 켤 때 Windows 방화벽 창이 뜨면 "개인 네트워크"에 체크하고 허용을 눌러 주세요. 연결에는 보안 키가 필요해서 QR 을 모르는 사람은 들어올 수 없습니다.'),
      row(button(icon('phone', 16) + ' 휴대폰 연결 켜기', () => restart(true), 'primary')),
    );
  } else {
    const url = info.urls[0];
    if (url) {
      const qr = qrCanvas(url, 7);
      qr.className = 'qr';
      body.append(h('div.qr-wrap', null, qr), h('p', null, '휴대폰 카메라로 QR 을 찍으면 바로 열립니다.'));
    } else body.append(h('p', null, '이 PC 의 와이파이 주소를 찾지 못했습니다. 네트워크 연결을 확인하세요.'));
    for (const u of info.urls) body.append(h('div.url-row', null, h('code', null, u), button('복사', () => { navigator.clipboard?.writeText(u); app.toast('주소를 복사했습니다'); })));
    body.append(
      h('ul.help', null,
        h('li', null, '휴대폰과 PC 가 같은 와이파이여야 합니다.'),
        h('li', null, '열린 뒤 브라우저 메뉴 → "홈 화면에 추가"로 앱처럼 쓸 수 있습니다.'),
        h('li', null, '연결이 끊기면 기기에 임시 저장되고, 다시 연결되면 PC 로 보낼 수 있습니다.'),
        h('li', null, '안 열리면 Windows 방화벽에서 Python 을 허용했는지 확인하세요.')),
      row(button('휴대폰 연결 끄기', () => restart(false), 'danger')),
    );
  }
  modal = openModal({ title: '휴대폰·태블릿 연결', body, actions: [{ label: '닫기', primary: true }] });
}

async function localDialog() {
  const items = await localStore.list().catch(() => []);
  const list = h('div.local-list');
  let modal;
  const render = (arr) => {
    list.innerHTML = '';
    if (!arr.length) list.append(h('div.help', null, '임시 저장된 것이 없습니다.'));
    for (const it of arr) {
      const name = it.name.split('/').pop();
      list.append(h('div.local-row', null,
        h('div', null, h('b', null, name), h('small', null, ` ${it.name.split('/')[0]} · ${new Date(it.mtime * 1000).toLocaleString()} · ${(it.size / 1048576).toFixed(1)}MB`)),
        row(
          button('열기', async () => { modal.close(true); await openProjectFile(await localStore.get(it.name)); }),
          button('PC로 보내기', async () => {
            await server.ping();
            if (!server.online) { app.toast('아직 PC 서버나 저장 폴더에 연결되지 않았습니다', 'warn'); return; }
            const [kind, ...rest] = it.name.split('/');
            await server.put(kind === 'autosave' ? 'autosave' : 'projects', rest.join('/'), await localStore.get(it.name));
            await localStore.del(it.name);
            app.toast('PC 로 보냈습니다');
            render(await localStore.list());
          }),
          button('다운로드', async () => downloadBlob(await localStore.get(it.name), name)),
          button('삭제', async () => { if (await confirmDialog(`${name} 을(를) 이 기기에서 지울까요?`, '삭제', true)) { await localStore.del(it.name); render(await localStore.list()); } }, 'danger'),
        )));
    }
  };
  render(items);
  modal = openModal({ title: '기기 임시 저장함', body: list, wide: true, actions: [{ label: '닫기', primary: true }] });
}
