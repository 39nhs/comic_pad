// 큰 원고 메모리 절약: 한동안 안 건드린 레이어(숨긴 레이어는 더 빨리)를 압축해 두고 캔버스를 비운다.
// 보이는 레이어는 절반 크기 미리보기로 그려지고, 고르거나 그리기 시작하면 바로 원래대로 돌아온다.
import { app } from '../app.js';

const BIG = 0.8 * 1024 ** 3;        // 레이어 메모리가 이보다 크면 절약 시작 (약 0.8GB)
const IDLE_VISIBLE = 90_000, IDLE_HIDDEN = 15_000;

export function layerBytes(doc) {
  return doc.layers.filter(l => !l.isFolder && l.kind !== 'adjust').length * doc.width * doc.height * 4;
}

function sweep() {
  const doc = app.doc;
  const mode = app.settings.memSave || 'auto';          // auto | always | off
  if (!doc || mode === 'off' || app.tool?.busy || app.doc?.anim?.playing) return;
  if (mode === 'auto' && layerBytes(doc) < BIG) return;
  const now = Date.now(), act = doc.active, live = app.live?.layer;
  let n = 0;
  for (const l of doc.layers) {
    if (l.frozen || l.isFolder || l.kind === 'adjust' || l === act || l === live || l.editMask) continue;
    const idle = now - (l._lastTouch || 0);
    if (idle < (l.visible ? IDLE_VISIBLE : IDLE_HIDDEN)) continue;
    if (l.freeze()) n++;
    if (n >= 3) break;                                     // 한 번에 조금씩 (멈춤 없이)
  }
  if (n) { app.requestRender(); syncBadge(); }
}

let badge = null;
export function syncBadge() {
  const doc = app.doc;
  const k = doc ? doc.layers.filter(l => l.frozen).length : 0;
  if (!badge) {
    const bar = document.getElementById('statusbar');
    if (!bar) return;
    badge = document.createElement('span');
    badge.className = 'mem-badge';
    badge.title = '메모리 절약: 한동안 안 쓴 레이어를 압축해 두었습니다 (고르면 바로 돌아옴). 설정 > 저장에서 바꿀 수 있음';
    bar.append(badge);
  }
  badge.textContent = k ? `💤 레이어 ${k}개 절약 중` : '';
}

export function initMemSave() {
  // 처음 열거나 새로 만든 레이어는 지금 막 쓴 것으로
  app.bus.on('doc:changed', (doc) => { const t = Date.now(); for (const l of doc?.layers || []) l._lastTouch = l._lastTouch || t; syncBadge(); });
  // 고른 레이어는 미리 되살려 두기 (그리기 시작할 때 멈칫하지 않게)
  app.bus.on('layers:active', (l) => { if (l?.frozen) { l.thaw(); app.requestRender(); syncBadge(); } else if (l) l._lastTouch = Date.now(); });
  app.bus.on('layers:changed', () => setTimeout(syncBadge, 0));
  setInterval(sweep, 5000);
  app.registerCommand('mem.freezeNow', { label: '메모리 절약: 지금 안 쓰는 레이어 압축', run: () => {
    const doc = app.doc; let n = 0;
    for (const l of doc.layers) if (l !== doc.active && !l.frozen && l.freeze()) n++;
    app.requestRender(); syncBadge();
    app.toast(n ? `레이어 ${n}개를 압축했습니다 (고르면 바로 돌아옴)` : '압축할 레이어가 없습니다 (빽빽한 레이어는 줄지 않아 그대로 둡니다)');
  } });
  app.addMenuItem('view', { command: 'mem.freezeNow' });
}
