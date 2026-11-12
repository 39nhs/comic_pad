// 그림판 위 마우스·펜 커서: 도구마다 정한 커서를 실제 Windows 커서로 건다.
// 브러시류는 펜촉(클립스튜디오 기본과 비슷)·십자·점 중 하나를 시스템 커서로 쓰고, 크기 원은 화면에 따로 그린다.
// (시스템 화살표 커서가 남아 있으면 YoloMouse 같은 커서 바꾸기 프로그램의 커서가 그대로 보이고 펜촉도 안 보였음)
import { app } from '../app.js';

const svgUrl = (w, h, body) => `url("data:image/svg+xml;utf8,${encodeURIComponent(
  `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">${body}</svg>`)}")`;

// 펜촉: 왼쪽 아래 끝(1,31)이 그리는 점. 흰 테두리 + 검은 선이라 어떤 배경에서도 보임
const NIB_PATH = 'M1.5 30.5 L4 21.5 L22 3.5 Q25 0.5 28.5 3.5 Q31.5 7 28.5 10 L10.5 28 Z';
const NIB = svgUrl(32, 32,
  `<path d="${NIB_PATH}" fill="none" stroke="#fff" stroke-width="3" stroke-linejoin="round"/>`
  + `<path d="${NIB_PATH}" fill="#fff" stroke="#000" stroke-width="1.2" stroke-linejoin="round"/>`
  + '<path d="M1.5 30.5 L4 21.5 L10.5 28 Z" fill="#000"/>'
  + '<path d="M1.5 30.5 L7.5 24.5" stroke="#fff" stroke-width="1"/>'
  + '<circle cx="7.6" cy="24.4" r="1.3" fill="#fff"/>'
  + '<path d="M17 9 L23 15" stroke="#000" stroke-width="1"/>') + ' 1 31';
const CROSS = svgUrl(23, 23,
  '<path d="M11.5 2v7M11.5 14v7M2 11.5h7M14 11.5h7" stroke="#fff" stroke-width="3" stroke-linecap="round"/>'
  + '<path d="M11.5 2v7M11.5 14v7M2 11.5h7M14 11.5h7" stroke="#000" stroke-width="1.2" stroke-linecap="round"/>'
  + '<circle cx="11.5" cy="11.5" r="1" fill="#000" stroke="#fff" stroke-width=".8"/>') + ' 11 11';
const DOT = svgUrl(9, 9, '<circle cx="4.5" cy="4.5" r="2.2" fill="#000" stroke="#fff" stroke-width="1.4"/>') + ' 4 4';

/** 지금 도구의 커서 (CSS cursor 값) */
export function cursorFor(tool) {
  if (!tool) return 'default';
  const c = tool.cursor || 'default';
  if (c !== 'none') return c;
  // 크기 원을 화면에 그리는 도구(브러시·지우개·손끝 등): 가운데 표시는 시스템 커서로
  const look = app.settings.brushCursor || 'nib';
  if (look === 'dot') return `${DOT}, crosshair`;
  if (look === 'nib' && tool.mode !== 'erase') return `${NIB}, crosshair`;
  return `${CROSS}, crosshair`;
}

export function initCursor(stage) {
  const apply = () => { stage.style.cursor = cursorFor(app.tool); };
  for (const e of ['tool:changed', 'tool:cursor', 'settings:changed']) app.bus.on(e, apply);
  apply();
  app.applyCursor = apply;
}
