// 모달 대화상자, 입력 폼, 토스트 알림
import { app } from '../app.js';
import { h } from '../core/util.js';

const root = () => document.getElementById('modal-root');

export function openModal({ title, body, actions = [], onClose, wide = false, cls = '' }) {
  const backdrop = h('div.modal-backdrop');
  const box = h(`div.modal${wide ? '.wide' : ''}${cls ? '.' + cls : ''}`);
  const close = (result) => {
    backdrop.remove();
    document.removeEventListener('keydown', onKey, true);
    onClose?.(result);
  };
  const onKey = (e) => {
    if (e.key === 'Escape') { e.stopPropagation(); close(null); }
    if (e.key === 'Enter' && !(e.target instanceof HTMLTextAreaElement) && !(e.target instanceof HTMLSelectElement)) {
      const primary = actions.find(a => a.primary);
      if (primary) { e.preventDefault(); e.stopPropagation(); run(primary); }
    }
  };
  const run = async (a) => {
    const r = await a.run?.();
    if (r !== false) close(a.value ?? r);
  };
  const footer = h('div.modal-actions');
  for (const a of actions) {
    footer.append(h(`button.btn${a.primary ? '.primary' : ''}${a.danger ? '.danger' : ''}`, { type: 'button', onclick: () => run(a) }, a.label));
  }
  box.append(
    h('div.modal-head', null, h('div.modal-title', null, title), h('button.icon-btn', { type: 'button', onclick: () => close(null), html: '&times;' })),
    h('div.modal-body', null, body),
  );
  if (actions.length) box.append(footer);
  backdrop.append(box);
  backdrop.addEventListener('pointerdown', (e) => { if (e.target === backdrop) close(null); });
  root().append(backdrop);
  document.addEventListener('keydown', onKey, true);
  setTimeout(() => box.querySelector('input, textarea, select')?.focus(), 30);
  return { close, el: box };
}

/**
 * 입력 폼. fields: [{ key, label, type, value, options, min, max, step, hint }]
 * type: text | number | select | checkbox | color | textarea | segment
 */
export function form(title, fields, { okLabel = '확인', cancelLabel = '취소', wide = false, onChange, extra = [] } = {}) {
  // 자동 액션 재생 중이면 기록해 둔 값으로 바로 (창을 띄우지 않음)
  const auto = app.formAuto?.(title, fields);
  if (auto) return Promise.resolve(auto);
  return new Promise((done) => {
    const resolve = (v) => { if (v && typeof v === 'object') app.bus.emit('form:submit', { title, values: v }); done(v); };
    const inputs = {};
    const values = () => {
      const out = {};
      for (const f of fields) {
        if (f.type === 'info') continue;
        const el = inputs[f.key];
        if (f.type === 'checkbox') out[f.key] = el.checked;
        else if (f.type === 'number') out[f.key] = parseFloat(el.value);
        else if (f.type === 'segment') out[f.key] = el.dataset.value;
        else out[f.key] = el.value;
      }
      return out;
    };
    const body = h('div.form');
    for (const f of fields) {
      if (f.type === 'info') { body.append(h('div.help', null, f.label)); continue; }
      let input;
      if (f.type === 'select') {
        input = h('select');
        for (const o of f.options) {
          const [v, t] = Array.isArray(o) ? o : [o, o];
          input.append(h('option', { value: v }, t));
        }
        input.value = f.value ?? '';
      } else if (f.type === 'textarea') {
        input = h('textarea', { rows: f.rows || 4 });
        input.value = f.value ?? '';
      } else if (f.type === 'checkbox') {
        input = h('input', { type: 'checkbox' });
        input.checked = !!f.value;
      } else if (f.type === 'segment') {
        input = h('div.seg');
        input.dataset.value = f.value;
        for (const o of f.options) {
          const [v, t] = Array.isArray(o) ? o : [o, o];
          const b = h('button', { type: 'button' }, t);
          if (String(v) === String(f.value)) b.classList.add('on');
          b.onclick = () => {
            input.dataset.value = v;
            input.querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b));
            onChange?.(values(), inputs);
          };
          input.append(b);
        }
      } else {
        input = h('input', { type: f.type || 'text' });
        if (f.min != null) input.min = f.min;
        if (f.max != null) input.max = f.max;
        if (f.step != null) input.step = f.step;
        input.value = f.value ?? '';
      }
      inputs[f.key] = input;
      input.addEventListener('input', () => onChange?.(values(), inputs));
      input.addEventListener('change', () => onChange?.(values(), inputs));
      body.append(h(`label.field${f.type === 'checkbox' ? '.check' : ''}`, null, h('span', null, f.label), input, f.hint ? h('small', null, f.hint) : null));
    }
    openModal({
      title, body, wide,
      actions: [
        ...extra.map(x => ({ label: x.label, run: () => { resolve(x.value); } })),
        { label: cancelLabel, run: () => { resolve(null); } },
        { label: okLabel, primary: true, run: () => { resolve(values()); } },
      ],
      onClose: (r) => { if (r === null) resolve(null); },
    });
  });
}

export function confirmDialog(message, okLabel = '확인', danger = false) {
  return new Promise((resolve) => {
    openModal({
      title: '확인',
      body: h('div.confirm-text', null, message),
      actions: [
        { label: '취소', run: () => resolve(false) },
        { label: okLabel, primary: !danger, danger, run: () => resolve(true) },
      ],
      onClose: (r) => { if (r === null) resolve(false); },
    });
  });
}

let toastTimer = 0;
/** opt.actions: [{ label, run }] — 누를 수 있는 버튼이 달린 알림 */
export function toast(msg, type = 'info', opt = {}) {
  const r = document.getElementById('toast-root');
  const el = h(`div.toast.${type}`, null, h('span', null, msg));
  const hide = () => { el.classList.add('out'); setTimeout(() => el.remove(), 300); };
  if (opt.actions?.length) {
    el.classList.add('has-actions');
    const row = h('div.toast-actions');
    for (const a of opt.actions) row.append(h('button.btn', { type: 'button', onclick: () => { hide(); a.run(); } }, a.label));
    row.append(h('button.btn.toast-x', { type: 'button', title: '닫기', onclick: hide }, '×'));
    el.append(row);
  }
  r.append(el);
  while (r.children.length > 3) r.firstChild.remove();
  clearTimeout(toastTimer);
  if (opt.duration !== 0) setTimeout(hide, opt.duration || (type === 'error' ? 5000 : 2600));   // 0 = 직접 닫을 때까지
}

app.toast = toast;

/**
 * 오래 걸릴 수 있는 작업: 화면에 "처리 중" 표시를 띄우고(delay 뒤), 한 프레임 쉬어서 화면이 먼저 그려지게 한 다음 실행.
 * 오류가 나도 앱이 멈추지 않고 알림만 띄운다.
 */
let busyCount = 0;
app.task = async function task(label, fn, { delay = 120 } = {}) {
  let el = document.getElementById('busy');
  if (!el) {
    el = h('div.busy');
    el.id = 'busy';
    el.append(h('div.spinner'), h('span'));
    document.body.append(el);
  }
  busyCount++;
  const timer = setTimeout(() => { el.querySelector('span').textContent = label + ' 처리 중…'; el.classList.add('show'); }, delay);
  // 화면이 한 번 그려질 시간을 준다 (창이 가려져 있으면 rAF 가 안 오므로 시간 제한도 함께)
  await new Promise(r => { let done = false; const go = () => { if (!done) { done = true; setTimeout(r, 0); } }; requestAnimationFrame(go); setTimeout(go, 60); });
  try {
    return await fn();
  } catch (e) {
    console.error(e);
    toast(`${label} 실패: ${e.message || e}`, 'error');
    return null;
  } finally {
    clearTimeout(timer);
    if (--busyCount <= 0) { busyCount = 0; el.classList.remove('show'); }
  }
};
app.ui = { openModal, form, confirm: confirmDialog, toast, h };
