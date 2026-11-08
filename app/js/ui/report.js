// 문제 보내기: 버전·환경·최근 오류를 모아 보여 주고, 사용자가 확인·고친 뒤 복사하거나 GitHub 이슈로 보냄.
// 자동으로 어디에도 보내지 않는다 (사용자가 "GitHub 에 쓰기" 를 눌러야 브라우저에서 작성 화면이 열림).
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal } from './dialogs.js';
import { server } from '../core/io.js';

const REPO = 'https://github.com/39nhs/comic_pad';

async function collect() {
  let d = {};
  if (server.http) {
    try { const r = await fetch('/api/diag'); if (r.ok) d = await r.json(); } catch { /* 서버 없음 */ }
  }
  const ua = navigator.userAgentData?.brands?.map(b => `${b.brand} ${b.version}`).join(', ') || navigator.userAgent;
  const lines = [
    '## 무슨 일이 있었나요?',
    '(여기에 어떤 작업을 하다가 어떤 문제가 생겼는지 적어 주세요)',
    '',
    '## 환경',
    `- ComicPad: ${d.version || '?'}${d.frozen_python ? ' (설치판)' : ''}`,
    `- Windows: ${d.os || navigator.platform} ${d.arch || ''}`,
    `- 브라우저 엔진: ${ua}`,
    `- 화면: ${screen.width}×${screen.height} @${devicePixelRatio}x, 펜 입력: ${matchMedia('(pointer: fine)').matches ? '있음' : '?'}`,
    `- 저장 방식: ${server.mode}`,
    '',
    '## 최근 오류 (화면)',
    ...(app.errors?.length ? app.errors.slice(-10).map(e => `- ${e.t} ${e.msg}${e.stack ? '\n```\n' + e.stack + '\n```' : ''}`) : ['- 없음']),
  ];
  if (d.server_errors?.length) lines.push('', '## 최근 오류 (서버)', '```', ...d.server_errors, '```');
  if (d.window_log) lines.push('', '## 창 프로그램 오류 기록', '```', d.window_log.trim(), '```');
  return lines.join('\n');
}

export async function reportDialog() {
  const text = h('textarea.report-text', { spellcheck: false });
  text.value = '정보를 모으는 중…';
  openModal({
    title: '문제 보내기',
    wide: true,
    body: h('div.report', null,
      h('p.help', null, '아래 내용은 이 PC 에서만 모았고, 아직 어디에도 보내지 않았습니다. 사용자 폴더 경로는 가렸습니다. 확인하고 필요하면 고친 뒤 복사하거나 GitHub 에 올리세요. 그림·작품 파일은 들어가지 않습니다.'),
      text,
    ),
    actions: [
      { label: '복사', run: async () => { await navigator.clipboard.writeText(text.value).catch(() => {}); app.toast('복사했습니다'); return false; } },
      {
        label: 'GitHub 에 쓰기', primary: true, run: () => {
          // 주소 길이 제한 때문에 길면 앞부분만 넣고 나머지는 붙여 넣기 안내
          let body = text.value;
          if (body.length > 6000) body = body.slice(0, 6000) + '\n\n(길어서 잘림 — 나머지는 "복사" 해서 붙여 넣어 주세요)';
          const first = (body.split('\n').find(l => l && !l.startsWith('#') && !l.startsWith('(')) || '문제 보고').slice(0, 80);
          window.open(`${REPO}/issues/new?title=${encodeURIComponent('[문제] ' + first)}&body=${encodeURIComponent(body)}`, '_blank');
        },
      },
      { label: '닫기' },
    ],
  });
  text.value = await collect();
}

export function initReport() {
  app.registerCommand('help.report', { label: '문제 보내기 (오류 보고)…', run: reportDialog });
  app.addMenuItem('help', { command: 'help.report' });
}
