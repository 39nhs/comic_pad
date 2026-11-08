// 자동 업데이트: GitHub 릴리스(39nhs/comic_pad)의 최신판을 서버가 확인·서명 검사·적용, 여기서는 보여 주기만.
// 하루 한 번 켤 때 조용히 확인하고, 새 판이 있으면 알림. 인터넷이 없으면 아무 일도 안 함.
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal, confirmDialog } from './dialogs.js';
import { server } from '../core/io.js';
import { toggle, row, section } from './controls.js';

const mb = (b) => `${(b / 1e6).toFixed(1)}MB`;
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function api(path, opt) {
  const r = await fetch(path, opt);
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || `실패 (${r.status})`);
  return j;
}
const post = (path, body) => api(path, { method: 'POST', body: body ? JSON.stringify(body) : undefined });

/** 새 server.py 로 다시 시작하고 화면을 새로 고친다 */
async function restartNow(to) {
  if (app.doc?.modified) {
    const ok = await confirmDialog('저장하지 않은 작업이 있습니다. 저장하고 다시 시작할까요?', '저장하고 다시 시작');
    if (!ok) return;
    await app.run('file.save');
    if (app.doc?.modified) return;   // 저장 취소·실패
  }
  app.toast('ComicPad 를 다시 시작하는 중…', 'info', { duration: 0 });
  await post('/api/update/restart').catch(() => {});
  for (let i = 0; i < 60; i++) {
    await sleep(500);
    try {
      const p = await (await fetch('/api/ping', { cache: 'no-store' })).json();
      if (!to || p.version === to) break;
    } catch { /* 아직 꺼져 있음 */ }
  }
  navigator.serviceWorker?.controller?.postMessage('precache');
  location.reload();
}

export async function updateDialog(pre = null) {
  if (!server.http) { app.toast('업데이트는 ComicPad 서버(바탕화면 아이콘)로 켰을 때 됩니다', 'warn'); return; }
  let st;
  try { st = await api('/api/update'); } catch (e) { app.toast(e.message, 'warn'); return; }
  const body = h('div.update');
  let info = pre || st.info, busy = false, closed = false;
  const status = h('div.help');
  const render = () => {
    body.innerHTML = '';
    body.append(...[
      h('p', null, `지금 버전: ${st.current}`),
      section('자동 업데이트',
        toggle('켤 때 새 버전 확인 (하루 한 번, 인터넷 연결 시)', () => st.conf.auto, async (v) => { st.conf = await post('/api/update/config', { auto: v }); }),
        h('div.help', null, `GitHub (${st.conf.repo}) 의 최신 릴리스를 봅니다. 만든 사람의 서명이 맞는 파일만 받고, 작업물(data)은 건드리지 않습니다.`),
        st.signed ? null : h('div.help.warn', null, '⚠ 서명 키(release_pubkey.json)가 없어 업데이트를 받을 수 없습니다.'),
      ),
      info ? section(info.available ? `새 버전 ${info.latest}` : '최신 버전입니다',
        info.available ? h('div.help', null, `받을 크기 ${mb(info.size)}${info.date ? ' · ' + new Date(info.date).toLocaleDateString() : ''}`) : null,
        info.notes ? h('pre.update-notes', null, info.notes) : null,
        info.available && info.launcher ? h('div.help', null, '창 프로그램(ComicPad.exe)도 바뀝니다. 적용 후 ComicPad 를 완전히 껐다 켜야 그 부분이 반영됩니다.') : null,
      ) : null,
      st.rollback ? section('되돌리기',
        h('div.help', null, `지금 ${st.rollback.from} 에 문제가 있으면 업데이트 전 ${st.rollback.to} 로 되돌릴 수 있습니다. 작업물(data)은 그대로이고, 되돌린 버전은 자동 알림에서 빠집니다.`),
        row(h('button.btn', { type: 'button', disabled: busy, onclick: rollback }, `${st.rollback.to} 로 되돌리기`)),
      ) : null,
      status,
      row(
        h('button.btn', { type: 'button', disabled: busy, onclick: check }, '지금 확인'),
        info?.available ? h('button.btn.primary', { type: 'button', disabled: busy, onclick: install }, '받아서 적용') : null,
        /^https:\/\/github\.com\//.test(info?.page || '') ? h('button.btn', { type: 'button', onclick: () => window.open(info.page, '_blank') }, '릴리스 페이지') : null,
      ),
    ].filter(Boolean));
  };
  async function check() {
    busy = true; status.textContent = '확인하는 중…'; render();
    try { info = await post('/api/update/check'); status.textContent = ''; } catch (e) { status.textContent = '⚠ ' + e.message; }
    busy = false; render();
  }
  async function rollback() {
    const ok = await confirmDialog(`${st.rollback.to} 로 되돌릴까요? 프로그램 파일만 바뀌고 작업물은 그대로입니다.`, '되돌리기');
    if (!ok) return;
    await run('/api/update/rollback');
  }
  async function install() { await run('/api/update/install'); }
  async function run(endpoint) {
    busy = true; render();
    try {
      await post(endpoint);
      for (;;) {
        await sleep(400);
        const s = await api('/api/update');
        if (s.state === 'download') status.textContent = `받는 중… ${s.total ? Math.round(s.done / s.total * 100) + '%' : mb(s.done)}`;
        else if (s.state === 'apply') status.textContent = `적용하는 중… ${s.total ? `${s.done}/${s.total}` : ''}`;
        else if (s.state === 'error') throw new Error(s.error);
        else if (s.state === 'done') {
          status.textContent = s.applied.rollback
            ? `${s.applied.from} → ${s.applied.to} 로 되돌렸습니다 (파일 ${s.applied.files}개). ComicPad 를 다시 시작한 뒤, 창 프로그램까지 맞추려면 완전히 껐다 켜 주세요.`
            : `${s.applied.from} → ${s.applied.to} 적용 완료 (파일 ${s.applied.files}개). 이전 판은 data/updates 에 남겨 두었습니다.`
              + (s.applied.launcher ? ' 창 프로그램 변경은 ComicPad 를 완전히 껐다 켜면 반영됩니다.' : '');
          busy = false; info = null; st = await api('/api/update'); st.rollback = null; render();
          body.append(row(h('button.btn.primary', { type: 'button', onclick: () => restartNow(s.applied.to) }, '지금 다시 시작')));
          return;
        }
        if (closed) return;
      }
    } catch (e) { status.textContent = '⚠ ' + e.message; busy = false; render(); }
  }
  render();
  openModal({ title: '업데이트', body, actions: [{ label: '닫기', primary: true }], onClose: () => { closed = true; } });
}

/** 업데이트 뒤 처음 켤 때 한 번: 바뀐 점 */
async function whatsNew() {
  if (!server.http) return;
  let st;
  try { st = await api('/api/update'); } catch { return; }
  const j = st.conf.just;
  if (!j || !j.to || j.to !== st.current) return;
  post('/api/update/seen').catch(() => {});
  openModal({
    title: `ComicPad ${j.to} 로 업데이트되었습니다`,
    body: h('div.update', null,
      h('p', null, `${j.from} → ${j.to}`),
      j.notes ? h('pre.update-notes', null, j.notes) : h('p.help', null, '바뀐 점 설명이 없습니다.'),
      h('div.help', null, '문제가 있으면 도움말 > 업데이트 확인 에서 이전 버전으로 되돌릴 수 있습니다.'),
    ),
    actions: [{ label: '되돌리기…', run: () => updateDialog() }, { label: '확인', primary: true }],
  });
}

/** 켤 때 하루 한 번 조용히 확인 */
async function autoCheck() {
  if (!server.http) return;
  let st;
  try { st = await api('/api/update'); } catch { return; }   // 휴대폰 등 다른 기기에서는 안 함
  if (!st.conf.auto || !st.signed) return;
  if (Date.now() / 1000 - (st.conf.checked || 0) < 20 * 3600) return;
  let info;
  try { info = await post('/api/update/check'); } catch { return; }   // 인터넷 없음·릴리스 없음 → 조용히
  if (!info.available || info.latest === st.conf.skip) return;
  app.toast(`ComicPad 새 버전 ${info.latest} 이 나왔습니다`, 'info', { duration: 0, actions: [{ label: '업데이트', run: () => updateDialog(info) }] });
}

export function initUpdate() {
  app.registerCommand('help.update', { label: '업데이트 확인…', run: () => updateDialog() });
  app.addMenuItem('help', { command: 'help.update' });
  setTimeout(whatsNew, 2500);
  setTimeout(autoCheck, 8000);
}
