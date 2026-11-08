// 펜 필기감 진단 (S펜·와콤·애플 펜슬 등): 이 기기에서 펜 신호가 얼마나 자주·얼마나 고르게 오는지 재고, 보정값을 맞춰 준다
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal } from './dialogs.js';

const STEPS = [
  { id: 'free', title: '1. 자유롭게 그어 보세요', help: '평소처럼 빠르게·느리게 여러 번 그어 주세요 (5초 이상).' },
  { id: 'light', title: '2. 가장 약하게', help: '선이 겨우 그어질 만큼 가장 약하게 몇 번 그어 주세요.' },
  { id: 'strong', title: '3. 가장 세게', help: '평소 가장 세게 누를 때만큼 눌러서 몇 번 그어 주세요.' },
  { id: 'hold', title: '4. 가만히 대고 있기', help: '펜을 화면에 댄 채 2초 동안 움직이지 마세요.' },
];

const pct = (arr, q) => { if (!arr.length) return 0; const a = [...arr].sort((x, y) => x - y); return a[Math.min(a.length - 1, Math.floor(q * a.length))]; };
const median = (arr) => pct(arr, 0.5);
const std = (arr) => { if (arr.length < 2) return 0; const m = arr.reduce((s, v) => s + v, 0) / arr.length; return Math.sqrt(arr.reduce((s, v) => s + (v - m) ** 2, 0) / arr.length); };

export function penDiagnostic() {
  const data = { free: [], light: [], strong: [], hold: [] };   // 획 목록: [{ t, x, y, p, tx, ty }...]
  const meta = { events: 0, coalesced: 0, predicted: false, lat: [], type: '', tilt: false };
  let step = 0, cur = null;

  const pad = h('canvas.pen-pad', { width: 640, height: 300 });
  const ctx = pad.getContext('2d');
  const title = h('b'), help = h('div.help'), result = h('div.pen-result');
  const clear = () => { ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, pad.width, pad.height); };
  const scale = () => pad.width / pad.getBoundingClientRect().width;

  const point = (e) => {
    const r = pad.getBoundingClientRect(), k = scale();
    return { t: e.timeStamp, x: (e.clientX - r.left) * k, y: (e.clientY - r.top) * k, p: e.pressure, tx: e.tiltX || 0, ty: e.tiltY || 0 };
  };
  pad.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    try { pad.setPointerCapture(e.pointerId); } catch { /* 무시 */ }
    meta.type = e.pointerType;
    cur = [point(e)];
  });
  pad.addEventListener('pointermove', (e) => {
    if (!cur) return;
    meta.events++;
    meta.lat.push(performance.now() - e.timeStamp);
    const list = e.getCoalescedEvents ? e.getCoalescedEvents() : [];
    meta.coalesced += Math.max(1, list.length);
    if (e.getPredictedEvents?.().length) meta.predicted = true;
    for (const ce of (list.length ? list : [e])) {
      const q = point(ce);
      if (q.tx || q.ty) meta.tilt = true;
      const last = cur[cur.length - 1];
      ctx.strokeStyle = '#222';
      ctx.lineCap = 'round';
      ctx.lineWidth = 1 + q.p * 10;
      ctx.beginPath(); ctx.moveTo(last.x, last.y); ctx.lineTo(q.x, q.y); ctx.stroke();
      cur.push(q);
    }
  });
  const end = () => { if (cur && cur.length > 1) data[STEPS[step].id].push(cur); cur = null; sync(); };
  pad.addEventListener('pointerup', end);
  pad.addEventListener('pointercancel', end);

  const enough = () => {
    const id = STEPS[step].id, strokes = data[id];
    if (id === 'hold') return strokes.some(s => s.at(-1).t - s[0].t > 1800);
    if (id === 'free') return strokes.reduce((n, s) => n + s.length, 0) > 300;
    return strokes.length >= 2;
  };
  const next = h('button.btn.primary', { type: 'button', onclick: () => { if (step < STEPS.length - 1) { step++; clear(); sync(); } else analyze(); } });
  const redo = h('button.btn', { type: 'button', onclick: () => { data[STEPS[step].id] = []; clear(); sync(); } }, '이 단계 다시');
  const sync = () => {
    title.textContent = STEPS[step].title;
    help.textContent = STEPS[step].help + (enough() ? ' ✓ 충분합니다' : '');
    next.textContent = step < STEPS.length - 1 ? '다음 단계' : '결과 보기';
    next.disabled = !enough();
  };

  let rec = null;
  function analyze() {
    const all = data.free.flat();
    // 초당 점 개수: 획마다 (점 수 / 걸린 시간)
    const rates = data.free.filter(s => s.length > 10).map(s => (s.length - 1) / ((s.at(-1).t - s[0].t) / 1000)).filter(isFinite);
    const rate = median(rates);
    const ps = all.map(q => q.p);
    const levels = new Set(ps.map(p => p.toFixed(4))).size;
    const lightP = data.light.flat().map(q => q.p).filter(p => p > 0);
    const strongP = data.strong.flat().map(q => q.p).filter(p => p > 0);
    const hold = data.hold.flat().filter((q, i, a) => q.t - a[0].t > 300);   // 대는 순간은 빼고
    const jitter = Math.hypot(std(hold.map(q => q.x)), std(hold.map(q => q.y))) / scale();
    const pNoise = std(hold.slice(1).map((q, i) => q.p - hold[i].p));
    const lat = median(meta.lat);
    const pressureOK = levels > 20 && pct(ps, 0.9) - pct(ps, 0.1) > 0.05;

    // 추천값
    const pmin = pressureOK ? Math.max(0, Math.min(0.4, pct(lightP, 0.25) * 0.8)) : 0;
    let pmax = pressureOK ? Math.min(1, Math.max(pmin + 0.3, pct(strongP, 0.9))) : 1;
    if (pmax - pmin < 0.3) pmax = Math.min(1, pmin + 0.3);
    const euroK = jitter < 0.25 ? 1.4 : jitter < 0.6 ? 1.1 : jitter < 1.2 ? 0.8 : 0.55;
    const pressureSmooth = Math.max(0.15, Math.min(0.65, 0.2 + pNoise * 12));
    rec = { pmin: +pmin.toFixed(3), pmax: +pmax.toFixed(3), euroK, pressureSmooth: +pressureSmooth.toFixed(2), predict: meta.predicted, startRamp: true };

    const row = (k, v, note = '') => h('tr', null, h('td', null, k), h('td', null, h('b', null, v)), h('td', null, h('small', null, note)));
    result.innerHTML = '';
    result.append(
      h('table.pen-table', null,
        row('입력 장치', meta.type || '?', meta.type === 'pen' ? '' : meta.type === 'mouse' ? '펜이 마우스로 들어옵니다 → 와콤·휴이온·XP-Pen 드라이버 설정에서 "Windows Ink" 를 켜야 필압·기울기가 됩니다' : '펜이 아니라 손가락(터치)으로 측정되었습니다'),
        row('초당 점 개수', `${Math.round(rate)}개`, rate >= 200 ? '아주 촘촘함' : rate >= 100 ? '보통' : '적음 → 보정을 조금 더'),
        row('한 번에 묶여 오는 점', (meta.coalesced / Math.max(1, meta.events)).toFixed(1) + '개', '모두 빠짐없이 사용 중'),
        row('필압 단계', `${levels}단계`, pressureOK ? '' : '필압이 거의 안 들어옵니다 (Windows Ink/드라이버 확인)'),
        row('가장 약하게 / 세게', `${pct(lightP, 0.5).toFixed(2)} / ${pct(strongP, 0.9).toFixed(2)}`, '이 범위를 0~100% 로 늘려 씀'),
        row('펜 끝 떨림 (가만히)', `${jitter.toFixed(2)}px`, jitter < 0.6 ? '안정적' : '떨림이 있어 보정을 강하게'),
        row('필압 흔들림 (가만히)', pNoise.toFixed(4), ''),
        row('기울기 인식', meta.tilt ? '됨' : '안 됨', ''),
        row('입력 지연 (화면 처리)', `${lat.toFixed(1)}ms`, meta.predicted ? '위치 예측 사용 가능' : '위치 예측 지원 안 함'),
      ),
      h('div.help', null, `추천: 필압 범위 ${rec.pmin}~${rec.pmax}, 떨림 보정 배율 ${rec.euroK}, 필압 다듬기 ${Math.round(rec.pressureSmooth * 100)}%, 위치 예측 ${rec.predict ? '켬' : '끔'}`),
      h('div.row', null,
        h('button.btn.primary', { type: 'button', onclick: () => {
          app.settings.penFeel = { ...(app.settings.penFeel || {}), ...rec };
          app.saveSettingsSoon(); app.bus.emit('settings:changed');
          app.toast('이 펜에 맞춘 값으로 바꿨습니다. 그려 보고 어색하면 "기본값으로"를 누르세요');
        } }, '추천값 적용'),
        h('button.btn', { type: 'button', onclick: () => {
          const f = { ...(app.settings.penFeel || {}) };
          for (const k of ['pmin', 'pmax', 'euroK']) delete f[k];
          app.settings.penFeel = { ...f, pressureSmooth: 0.35, startRamp: true, predict: true };
          app.saveSettingsSoon(); app.bus.emit('settings:changed');
          app.toast('필기감을 기본값으로 되돌렸습니다');
        } }, '기본값으로'),
        h('button.btn', { type: 'button', onclick: () => { step = 0; for (const k in data) data[k] = []; Object.assign(meta, { events: 0, coalesced: 0, lat: [] }); result.innerHTML = ''; clear(); sync(); } }, '처음부터 다시')),
    );
  }

  clear();
  sync();
  openModal({ title: '펜 필기감 진단', body: h('div.pen-diag', null, title, help, pad, h('div.row', null, redo, next), result), wide: true, actions: [{ label: '닫기', primary: true }] });
}

export function initPenDiagnostic() {
  app.registerCommand('pen.diagnose', { label: '펜 필기감 진단 (이 기기·이 펜에 맞추기)…', run: penDiagnostic });
  app.addMenuItem('help', { command: 'pen.diagnose' });
}
