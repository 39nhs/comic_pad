// 새 네이티브 판(베타)으로 바꾸기: 웹 화면 없이 Windows 에서 바로 도는 ComicPad (native/ComicPadNative).
//  - 업데이트(1.2.0~)가 네이티브 판 파일을 넣어 두면, 여기서 data/config/app.json 을 {"app":"native"} 로 바꾸고 바로 켠다.
//  - 다음부터 ComicPad 아이콘(ComicPad.exe)이 네이티브 판으로 연다. 켤 때 Shift 를 누르고 있으면 이번만 웹 판.
//  - 돌아오기: 네이티브 판 도움말 > 웹 판으로 돌아가기. 작업물(data)·.cpad 는 두 판이 같이 쓴다.
//  - 설정(브러시·필압·단축키·화면 구성·팔레트 등)은 네이티브 판이 처음 켜질 때 data/config/settings.json 에서 가져간다.
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal, confirmDialog } from './dialogs.js';
import { server, saveSettings } from '../core/io.js';
import { section } from './controls.js';

const mb = (b) => `${(b / 1e6).toFixed(0)}MB`;
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function api(path, opt) {
  const r = await fetch(path, opt);
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || `실패 (${r.status})`);
  return j;
}
const post = (path, body) => api(path, { method: 'POST', body: JSON.stringify(body || {}) });

/** 이 창(웹 판)을 닫는다: ComicPad 창 프로그램이면 "app:close", 아니면 window.close() */
async function closeThisWindow() {
  try { await saveSettings(app.settings); } catch { /* 무시 */ }
  if (app.doc) app.doc.modified = false;   // 저장은 이미 끝남 (닫을 때 다시 묻지 않게)
  if (window.comicpadLauncher?.close && window.chrome?.webview) window.chrome.webview.postMessage('app:close');
  else window.close();
  await sleep(1500);
  // 아직 열려 있으면 (예전 창 프로그램·브라우저): 안내만
  openModal({
    title: '네이티브 판을 켰습니다',
    body: h('div.help', null, '이 창(웹 판)은 닫아도 됩니다. 다음부터 ComicPad 아이콘이 네이티브 판으로 엽니다.'),
    actions: [{ label: '확인', primary: true }],
  });
}

/** 바로 바꾸기 (저장 확인 → 스위치 → 네이티브 판 켜기 → 이 창 닫기) */
async function switchNow() {
  if (app.doc?.modified) {
    const ok = await confirmDialog('저장하지 않은 작업이 있습니다. 저장하고 네이티브 판으로 바꿀까요?', '저장하고 바꾸기');
    if (!ok) return;
    await app.run('file.save');
    if (app.doc?.modified) return;   // 저장 취소·실패
  }
  try { await saveSettings(app.settings); } catch { /* 설정 저장 실패해도 바꾸기는 함 */ }
  let r;
  try { r = await post('/api/app/switch', { app: 'native', launch: true, open: app.doc?.file || '' }); }
  catch (e) { app.toast(e.message, 'error'); return; }
  app.toast('네이티브 판을 켜는 중…', 'info', { duration: 4000 });
  if (r.pid) await sleep(1200);
  await closeThisWindow();
}

export async function nativeDialog() {
  if (!server.http) { app.toast('네이티브 판은 PC 의 ComicPad 창에서만 바꿀 수 있습니다', 'warn'); return; }
  let info;
  try { info = await api('/api/app'); } catch (e) { app.toast(e.message, 'warn'); return; }
  const present = info.native?.present;
  const body = h('div.update', null,
    h('p', null, '네이티브 판은 웹 화면 없이 Windows 에서 바로 도는 새 ComicPad 입니다. 펜 반응이 더 빠르고 큰 그림에서도 가볍습니다.'),
    section('바뀌는 것 · 그대로인 것',
      h('div.help', null, '• 작품(data 폴더)은 두 판이 같이 씁니다. .cpad 파일은 어느 판에서나 열립니다.'),
      h('div.help', null, '• 지금 설정(브러시·필압·필기감·단축키·화면 구성·팔레트·최근 색·처음 세팅)은 네이티브 판이 처음 켜질 때 가져갑니다.'),
      h('div.help', null, '• 자동 저장은 판마다 따로 (네이티브 판은 "이름 (네이티브).cpad"), 백업·버전 기록은 같은 곳에 쌓입니다.'),
      h('div.help', null, '• 휴대폰·태블릿 연결(QR)과 웹 플러그인은 웹 판에만 있습니다.'),
    ),
    section('언제든 돌아올 수 있습니다',
      h('div.help', null, '• 네이티브 판의 도움말 > 웹 판으로 돌아가기 를 누르면 다시 이 웹 판으로 켜집니다.'),
      h('div.help', null, '• ComicPad 를 켤 때 Shift 키를 누르고 있으면 이번만 웹 판으로 열립니다.'),
      h('div.help', null, '• 베타라 아직 빠진 기능이나 다른 점이 있을 수 있습니다. 도움말 > 문제 보내기 로 알려 주세요.'),
    ),
    present
      ? h('div.help', null, `네이티브 판 파일이 준비되어 있습니다 (${info.native.exe}, ${mb(info.native.size)}).`
        + (info.launcher ? '' : ' ⚠ 창 프로그램(ComicPad.exe)이 예전 판이라 다음에 켤 때는 웹 판으로 열립니다. 업데이트 뒤 ComicPad 를 완전히 껐다 켜 주세요.'))
      : h('div.help.warn', null, '⚠ 이 PC 에는 아직 네이티브 판 파일이 없습니다. 도움말 > 업데이트 확인 으로 1.2.0 이상을 받으면 생깁니다.'),
    info.app === 'native' ? h('div.help', null, '지금은 네이티브 판으로 켜지게 되어 있습니다 (이번에는 Shift 나 문제로 웹 판이 열림).') : null,
  );
  const actions = [{ label: '취소' }];
  if (info.app === 'native') actions.push({ label: '앞으로 웹 판으로 켜기', run: () => keepWeb() });
  if (present) actions.push({ label: '네이티브 판으로 바꾸기', primary: true, run: () => { setTimeout(switchNow, 0); } });
  else actions.push({ label: '업데이트 확인…', primary: true, run: () => { setTimeout(() => app.run('help.update'), 0); } });
  openModal({ title: '새 네이티브 판 써 보기 (베타)', body, actions });
}

async function keepWeb() {
  try { await post('/api/app/switch', { app: 'web' }); app.toast('앞으로도 웹 판으로 켭니다'); }
  catch (e) { app.toast(e.message, 'error'); }
}

/** 네이티브 판으로 되어 있는데 웹 판이 열렸을 때 (Shift·네이티브 판 문제): 한 번 알려 줌 */
async function notice() {
  if (!server.http) return;
  let info;
  try { info = await api('/api/app'); } catch { return; }
  if (info.app !== 'native') return;
  app.toast('지금은 웹 판입니다 (네이티브 판 베타로 설정되어 있음)', 'info', {
    duration: 12000,
    actions: [{ label: '네이티브 판으로', run: () => nativeDialog() }, { label: '앞으로 웹 판', run: () => keepWeb() }],
  });
}

export function initAppSwitch() {
  app.registerCommand('app.switchNative', { label: '새 네이티브 판 써 보기 (베타)…', run: () => nativeDialog() });
  app.addMenuItem('help', { command: 'app.switchNative' });
  setTimeout(notice, 4000);
}
