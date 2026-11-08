// AI 모델 라이선스: 비상업 모델은 쓸 때 경고 + 작품에 기록 + 내보낼 때 다시 알림
import { app } from '../app.js';
import { form, confirmDialog } from '../ui/dialogs.js';

const shown = new Set();
export const BADGE = { yes: '상업 가능', check: '확인 필요', no: '⚠ 비상업' };

/** 이 모델을 써도 되는지 (비상업이면 확인을 받음). false = 취소 */
export async function licenseGate(m) {
  if (!m || m.commercial === 'yes') return true;
  if (m.commercial === 'check') {
    if (!shown.has(m.id)) {
      shown.add(m.id);
      app.toast(`${m.name}: ${m.note || '학습된 모델 파일의 라이선스 표기가 따로 없습니다.'} 상업용으로 쓰기 전에 출처를 확인하세요.`, 'warn', { duration: 8000 });
    }
    return true;
  }
  const doc = app.doc;
  if (doc?.aiLog?.some(x => x.id === m.id && x.nc)) return true;          // 이 작품에서 이미 확인함
  const v = await form('⚠ 비상업용 AI 모델', [
    { key: 'i1', type: 'info', label: `"${m.name}" 은(는) 비상업용 라이선스(${m.license})입니다.` },
    { key: 'i2', type: 'info', label: '판매·연재·외주·광고·후원 수익·회사 업무처럼 돈을 벌거나 상업적으로 쓰는 작품에는 쓸 수 없습니다. 개인 공부·취미·연구용으로만 쓰세요.' },
    { key: 'i3', type: 'info', label: '이 작품에는 "비상업 AI 사용" 기록이 남고, 결과 레이어 이름에 (비상업 AI)가 붙으며, 내보낼 때 다시 알려 드립니다.' },
    { key: 'ok', type: 'checkbox', label: '이 작품은 상업용이 아닙니다 (개인 공부·취미·연구)', value: false },
  ], { okLabel: '이해했고 사용합니다' });
  if (!v) return false;
  if (!v.ok) { app.toast('"상업용이 아닙니다"에 체크해야 쓸 수 있습니다', 'warn'); return false; }
  (doc.aiLog ||= []).push({ id: m.id, name: m.name, license: m.license, nc: true, at: new Date().toISOString() });
  doc.modified = true;
  app.bus.emit('doc:modified', true);
  syncNcBadge();
  return true;
}

export const ncName = (base, m) => (m?.commercial === 'no' ? `${base} (비상업 AI)` : base);
export const ncUsed = (doc = app.doc) => (doc?.aiLog || []).filter(x => x.nc);

/** 내보내기 전에: 비상업 AI 결과가 있으면 한 번 더 묻기 */
export async function exportGuard() {
  const used = ncUsed();
  if (!used.length) return true;
  const names = [...new Set(used.map(x => x.name))].join(', ');
  return confirmDialog(`이 작품에는 비상업용 AI 로 만든 부분이 있습니다 (${names}).\n\n판매·연재·외주 등 상업용으로 내보내는 것이라면 라이선스 위반이 될 수 있습니다. 개인 공부·취미용이면 계속하세요.`, '계속 내보내기');
}

export function wrapExports() {
  for (const id of ['file.export', 'file.exportQuick', 'file.printExport', 'file.webtoonExport', 'file.exportPSD', 'file.exportSlices', 'file.exportLayers', 'anim.export']) {
    const c = app.commands.get(id);
    if (!c || c._ncWrapped) continue;
    const run = c.run;
    c.run = async (...a) => ((await exportGuard()) ? run(...a) : undefined);
    c._ncWrapped = true;
  }
}

let badge = null;
export function syncNcBadge() {
  if (!badge) {
    const bar = document.getElementById('statusbar');
    if (!bar) return;
    badge = document.createElement('span');
    badge.className = 'nc-badge';
    badge.title = '이 작품에 비상업용 AI 모델로 만든 부분이 있습니다. 상업용으로 쓰지 마세요.';
    bar.append(badge);
  }
  badge.textContent = ncUsed().length ? '⚠ 비상업 AI 사용' : '';
}
