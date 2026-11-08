// 작업물 백업 묶음: 하루 한 번 작품·에셋·글꼴·설정을 zip 하나로 (서버가 자동으로), 여기서 보기·지금 백업·꺼내기
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal, confirmDialog } from './dialogs.js';
import { server } from '../core/io.js';
import { slider, toggle, row, button, section } from './controls.js';

const mb = (b) => `${(b / 1e6).toFixed(b < 1e7 ? 1 : 0)}MB`;
const when = (t) => new Date(t * 1000).toLocaleString();

async function api(path, opt) {
  const r = await fetch(path, opt);
  const j = await r.json();
  if (!r.ok) throw new Error(j.error || '실패');
  return j;
}

export async function backupDialog() {
  if (!server.http) { app.toast('자동 백업은 ComicPad 서버(바탕화면 아이콘)로 켰을 때 됩니다', 'warn'); return; }
  const body = h('div.backup');
  let timer = null;
  const render = async () => {
    const st = await api('/api/backup');
    const c = st.conf;
    const save = (k) => async (v) => { await api('/api/backup/config', { method: 'POST', body: JSON.stringify({ [k]: v }) }); };
    body.innerHTML = '';
    body.append(...[
      h('div.help', null, `작품·에셋·글꼴·설정을 zip 하나로 묶어 ${st.folder} 에 둡니다. 휴대폰 연결 키·서버 설정·AI 모델은 넣지 않습니다.`),
      section('자동 백업',
        toggle('자동으로 백업', () => c.on, save('on')),
        slider('간격', { min: 1, max: 72, step: 1, get: () => c.every_h, set: (v) => { c.every_h = v; }, onEnd: () => save('every_h')(c.every_h), format: v => (v % 24 === 0 ? `${v / 24}일` : `${v}시간`) + '마다' }),
        slider('보관 개수', { min: 1, max: 60, step: 1, get: () => c.keep, set: (v) => { c.keep = v; }, onEnd: () => save('keep')(c.keep), format: v => `최근 ${v}개` }),
        toggle('구글 드라이브에도 한 부 (드라이브 사본이 켜져 있을 때)', () => c.drive, save('drive')),
        st.drive ? null : h('div.help', null, '구글 드라이브 사본이 꺼져 있어 이 PC 에만 둡니다 (파일 > 저장 위치에서 켤 수 있음).'),
      ),
      row(
        h('button.btn.primary', { type: 'button', disabled: st.state.running, onclick: async () => { await api('/api/backup/now', { method: 'POST' }); render(); } }, st.state.running ? '백업하는 중…' : '지금 백업'),
        button('백업 폴더 열기', () => fetch('/api/open-folder?kind=backups', { method: 'POST' })),
      ),
      st.state.error ? h('div.help.warn', null, '⚠ ' + st.state.error) : null,
      st.state.last ? h('div.help', null, `마지막 백업: ${st.state.last.name} · ${mb(st.state.last.size)} · 파일 ${st.state.last.files}개${st.state.last.drive ? ' · 드라이브에도 복사함' : ''}`) : null,
    ].filter(Boolean));
    const list = h('div.backup-list');
    if (!st.list.length) list.append(h('div.help', null, '아직 백업이 없습니다.'));
    for (const b of st.list) {
      const det = h('details');
      det.append(h('summary', null, `${b.name} — ${mb(b.size)} · ${when(b.mtime)}`));
      det.addEventListener('toggle', async () => {
        if (!det.open || det.dataset.loaded) return;
        det.dataset.loaded = '1';
        const files = await api(`/api/backup/contents?name=${encodeURIComponent(b.name)}`);
        if (!files.length) det.append(h('div.help', null, '작품 파일이 없습니다.'));
        for (const f of files) {
          det.append(h('div.row', null, h('span', null, f.name.replace(/^projects\//, '')), h('small', null, mb(f.size)),
            h('button.btn.sm', { type: 'button', onclick: async () => {
              if (!(await confirmDialog(`"${f.name.replace(/^projects\//, '')}" 을(를) 백업에서 꺼낼까요?\n지금 파일은 덮어쓰지 않고 "(복원 날짜)" 를 붙인 새 파일로 만듭니다.`, '꺼내기'))) return;
              const r = await api(`/api/backup/restore?name=${encodeURIComponent(b.name)}&file=${encodeURIComponent(f.name)}`, { method: 'POST' });
              app.toast(`꺼냈습니다: ${r.name} (파일 > 열기)`);
            } }, '꺼내기')));
        }
      });
      list.append(det);
    }
    body.append(section('백업 목록', list));
    clearTimeout(timer);
    if (st.state.running) timer = setTimeout(render, 1500);
  };
  await render();
  openModal({ title: '작업물 백업', body, wide: true, onClose: () => clearTimeout(timer), actions: [{ label: '닫기', primary: true }] });
}

export function initBackup() {
  app.registerCommand('file.backup', { label: '작업물 백업 (자동·지금·꺼내기)…', run: backupDialog });
  app.addMenuItem('file', { command: 'file.backup' });
}
