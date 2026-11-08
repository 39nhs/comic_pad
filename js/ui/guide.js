// 앱 안 기능 안내: 무엇을 어디서 하는지 찾아보고, 바로 실행
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal } from './dialogs.js';
import { server } from '../core/io.js';

// [제목, 설명, 실행할 명령 또는 도구(tool:id)]
const GUIDE = {
  '시작': [
    ['새 그림 / 열기 / 저장', 'Ctrl+N · Ctrl+O · Ctrl+S. 저장 위치는 파일 > 저장 위치에서 확인', 'file.new'],
    ['여러 페이지 작품', '만화 > 새 작품. 페이지 패널에서 넘기고 순서를 끌어서 바꿈', 'story.new'],
    ['기능 찾기', 'Ctrl+K 를 누르고 이름 일부를 치면 메뉴에 없는 기능도 바로 실행', 'palette.open'],
    ['설명서', '전체 기능을 정리한 설명서 (찾기 가능, 인쇄 가능)', 'help.manual'],
    ['화면 꾸미기', '자주 쓰는 도구·메뉴만 꺼내 두기, 패널 탭 고르기, 모든 기능 보기로 전환', 'ui.customize'],
  ],
  '그리기': [
    ['펜·브러시', 'B. 필압·손떨림 보정은 설정 > 펜, 이 펜에 맞추려면 펜 필기감 진단', 'tool:brush'],
    ['펜 필기감 진단', '네 단계로 그어 보면 이 기기에 맞는 필압 범위·보정값을 적용', 'pen.diagnose'],
    ['러프', '러프 연필은 러프 레이어에서만. 레이어 패널 "러프" 버튼, 색은 14가지', 'layer.newRough'],
    ['벡터 레이어', '그린 뒤에도 굵기·색 바꾸기, 교차점까지 지우기, 선 고르기 도구(O)로 옮기고 다듬기', 'layer.newVector'],
    ['원형 메뉴', '마우스 오른쪽 버튼 또는 펜 측면 버튼(설정). 밀고 떼면 선택', 'ui.radialEdit'],
    ['자 (투시·평행·방사)', '자 도구로 추가하고 끌어서 조정. 3D 창에서 시점 그대로 만들 수도', 'tool:ruler'],
  ],
  '만화': [
    ['컷 나누기', '컷 도구(K)로 칸 나누기, 대각선·다각형 컷', 'tool:panel'],
    ['말풍선', 'F. 만든 뒤 탭해서 옮기기·크기·꼬리·모양 수정, 대사도 같이 움직임', 'tool:bubble'],
    ['대본으로 대사 넣기', '대사를 한꺼번에 넣고 탭할 때마다 놓기, 또는 컷 순서대로 자동', 'tool:script'],
    ['밑색 한 번에', '선화로 닫힌 영역마다 색을 채운 밑색 레이어', 'color.autoFlat'],
    ['메모 핀', 'Y. 그림 위에 피드백 메모, 내보내기에는 안 나옴', 'tool:memo'],
    ['진행표', '페이지마다 단계·작업 시간·마감일', 'progress.open'],
    ['3D 소재', '데생 인형(체형·포즈), 손, 소품, 3D 모델 → 색·음영·선화 레이어로', '3d.open'],
  ],
  '채색·보정': [
    ['명도 확인', 'Shift+Y. 화면만 흑백 → 5단계 → 끔', 'view.luma'],
    ['용지 질감', '종이 결을 용지(와 그림 위)에', 'paper.texture'],
    ['보정·조정 레이어', '레벨·곡선·색조 등, 원본을 지키려면 조정 레이어', 'adjust.levels'],
  ],
  '내보내기·인쇄': [
    ['이미지 내보내기', 'PNG/JPG, 원고 설정이 있으면 재단선 기준 범위', 'file.export'],
    ['인쇄용 (PDF·1비트 TIFF)', '재단선 안·도련 포함, 여러 페이지를 PDF 하나로', 'file.printExport'],
    ['원고 설정', 'B5 등 판형 → 재단선·도련·안전선 가이드', 'print.setup'],
    ['웹툰 플랫폼 규격', '가로폭 맞춤 → 나누기 → 장당 용량 맞춤', 'file.webtoonExport'],
    ['펼침면·웹툰 미리보기', '책처럼 두 쪽씩 / 휴대폰 폭으로 스크롤', 'view.spread'],
  ],
  '저장·협업': [
    ['버전 기록·비교', '저장할 때마다 이전 판 보관, 비교·되돌리기', 'file.versions'],
    ['구글 드라이브 사본', '파일 > 저장 위치에서 켜기 (Google Drive 데스크톱 앱 폴더로 복사)', 'file.storage'],
    ['피드백 파일', '러프·메모만 작은 파일로 주고받기', 'feedback.export'],
    ['휴대폰·태블릿 연결', '같은 와이파이에서 QR 로 연결', 'mobile.connect'],
  ],
  '화면·도움': [
    ['앱 밖 창으로', '패널 탭 옆 ⧉ 또는 보기 > 패널을 앱 밖 창으로 (다른 모니터·항상 위)', 'popout.panel'],
    ['작업 화면 저장', '패널 배치·도구를 저장해 Ctrl+Alt+1~4 로 바꾸기', 'workspace.manage'],
    ['단축키 / 제스처', '두 손가락 탭 = 실행취소 등', 'help.keys'],
    ['자동 점검', '주요 기능이 제대로 되는지 한 번에 시험 (작업 중인 그림은 그대로)', 'selftest.run'],
  ],
};

export function featureGuide() {
  let modal;
  const q = h('input', { type: 'search', placeholder: '찾을 기능 (예: 말풍선, 인쇄, 필압)' });
  const list = h('div.guide-list');
  const run = (cmd) => { modal.close(true); if (cmd.startsWith('tool:')) app.setTool(cmd.slice(5)); else app.run(cmd); };
  const render = () => {
    const k = q.value.trim().toLowerCase();
    list.innerHTML = '';
    for (const [sec, rows] of Object.entries(GUIDE)) {
      const hit = rows.filter(([t, d]) => !k || (t + d + sec).toLowerCase().includes(k));
      if (!hit.length) continue;
      list.append(h('h4', null, sec));
      for (const [t, d, cmd] of hit) {
        const ok = cmd.startsWith('tool:') ? app.tools.has(cmd.slice(5)) : app.commands.has(cmd);
        list.append(h('div.guide-row', null, h('div', null, h('b', null, t), h('small', null, d)), ok ? h('button.btn', { type: 'button', onclick: () => run(cmd) }, '열기') : null));
      }
    }
    if (!list.children.length) list.append(h('div.help', null, '찾는 기능이 없습니다. Ctrl+K 기능 검색도 써 보세요.'));
  };
  q.oninput = render;
  render();
  modal = openModal({ title: '기능 안내', body: h('div.guide', null, q, list), wide: true, actions: [{ label: '닫기', primary: true }] });
  setTimeout(() => q.focus(), 50);
}

/** 설명서 (manual.html) 를 앱 안에서 열기 */
export function openManual(section = '') {
  const frame = h('iframe.manual-frame', { src: 'manual.html' + (section ? '#' + section : '') });
  openModal({ title: 'ComicPad 설명서', body: frame, wide: true, actions: [
    { label: '새 창으로', run: () => window.open('manual.html', 'comicpad-manual') },
    { label: '닫기', primary: true },
  ] });
}

/** ComicPad 정보·라이선스 (Apache-2.0 + 함께 들어 있는 것들의 라이선스 전문 = LICENSES.txt) */
export async function openAbout() {
  const pre = h('pre.license-text', null, '불러오는 중…');
  const ver = h('span', null, '');
  fetch('/api/ping').then(r => r.json()).then(j => { if (j.version) ver.textContent = ` (버전 ${j.version})`; }).catch(() => {});
  openModal({ title: 'ComicPad 정보 · 라이선스', wide: true, body: h('div.about', null,
    h('p', null, '내가 필요한 기능만 넣어 쓰는 만화 그림판.', ver),
    h('p', null, `데이터 위치: ${server.mode === 'device' ? '이 기기 안' : (server.root || '') + '\\data'} (${{ http: 'PC 서버', folder: '서버 없이 폴더 직접', device: '기기 저장' }[server.mode] || server.mode})`),
    h('p', null, '기능 추가: plugins 폴더에 .js 파일을 넣고 새로고침 (plugins/README.md 참고).'),
    h('p', null, 'ComicPad 는 Apache License 2.0 으로 배포하는 프로그램입니다. 이 프로그램으로 그린 그림의 권리는 그린 사람에게 있습니다.'),
    h('p.help', null, '아래는 ComicPad 와 함께 들어 있는 라이브러리·AI 모델의 라이선스 전문입니다. AI 모델 관리 창에서 따로 받는 모델은 각 모델의 라이선스를 따릅니다.'),
    pre,
  ), actions: [{ label: '닫기', primary: true }] });
  try {
    const r = await fetch('LICENSES.txt');
    if (!r.ok) throw new Error(r.status);
    pre.textContent = await r.text();
  } catch { pre.textContent = '라이선스 파일(LICENSES.txt)을 읽지 못했습니다. 설치 폴더의 LICENSE·NOTICE 파일을 확인하세요.'; }
}

export function initGuide() {
  app.registerCommand('help.about', { label: 'ComicPad 정보 · 라이선스', run: openAbout }); // 메뉴 자리는 commands.js
  app.registerCommand('help.manual', { label: 'ComicPad 설명서 (전체 기능)', run: () => openManual() });
  app.addMenuItem('help', { command: 'help.manual' });
  app.registerCommand('help.features', { label: '기능 안내 (무엇을 어디서)…', run: featureGuide, key: 'f2' });
  app.addMenuItem('help', { command: 'help.features' });
}
