// 러프 전용 레이어: 정해진 단색(의견 색) 중 하나로 보이고, 내보내기에서 빠진다.
// 협업할 때 사람/의견마다 색을 나눠 쓰는 용도.
import { app } from '../app.js';
import * as ops from './ops.js';

export const ROUGH_COLORS = [
  ['#3d7be0', '파랑'], ['#e5484d', '빨강'], ['#f08c2e', '주황'], ['#d9b310', '노랑'],
  ['#7fbf2f', '연두'], ['#2fa35b', '초록'], ['#1fb5a8', '청록'], ['#4fb3e8', '하늘'],
  ['#3b4cc0', '남색'], ['#8e5bd6', '보라'], ['#e05fa8', '분홍'], ['#9a6a3a', '갈색'],
  ['#7d7d86', '회색'], ['#2b2b2b', '검정'],
];

export const colorName = (hex) => ROUGH_COLORS.find(c => c[0] === hex)?.[1] || hex;

/** 새 러프 레이어에 쓸 색 (마지막으로 고른 색) */
export function defaultRoughColor() {
  const c = app.settings.roughColor;
  return ROUGH_COLORS.some(x => x[0] === c) ? c : ROUGH_COLORS[0][0];
}

export function newRoughLayer(color = defaultRoughColor()) {
  return ops.addLayer({ name: `러프 (${colorName(color)})`, rough: true, tint: color, opacity: 0.6, draft: true });
}

/** 레이어를 러프 전용으로 / 일반으로 */
export function setRough(layer, on, color = defaultRoughColor()) {
  if (!layer || layer.isFolder || layer.kind === 'adjust') return;
  if (on) ops.setLayerProps(layer, { rough: true, tint: color, draft: true, opacity: layer.opacity === 1 ? 0.6 : layer.opacity }, '러프 레이어로');
  else ops.setLayerProps(layer, { rough: false, tint: null, draft: false }, '일반 레이어로');
}

/** 러프 레이어 색 바꾸기 (이름에 들어 있는 색 이름도 같이) */
export function setRoughColor(layer, color) {
  app.settings.roughColor = color;
  app.saveSettingsSoon();
  if (!layer?.rough) return;
  const name = /^러프 \(.+\)$/.test(layer.name) || layer.name === '러프' ? `러프 (${colorName(color)})` : layer.name;
  ops.setLayerProps(layer, { tint: color, name }, '러프 색');
}

/** 러프 연필을 일반 레이어에 쓰려 할 때: 바꿀 방법을 버튼으로 보여 주는 알림 */
export function warnNotRough() {
  const doc = app.doc;
  const existing = doc.findLayer(l => l.rough && l.visible && !l.locked);
  const actions = [];
  if (existing) actions.push({ label: `"${existing.name}" 로 이동`, run: () => doc.setActive(existing) });
  actions.push({ label: '새 러프 레이어', run: () => newRoughLayer() });
  const act = doc.active;
  if (act && !act.isFolder && act.kind === 'raster') actions.push({ label: '이 레이어를 러프로', run: () => setRough(act, true) });
  app.toast('러프 연필은 러프 전용 레이어에서만 쓸 수 있습니다. 레이어를 바꿔 주세요.', 'warn', { actions, duration: 7000 });
}
