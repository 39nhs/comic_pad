// 터치 친화적인 작은 UI 부품들. 각 요소는 el.sync() 로 값을 다시 읽는다.
import { h, clamp } from '../core/util.js';

/** 슬라이더: 손가락으로 끌기 쉬운 큰 막대 + 숫자 표시. log=true 면 로그 스케일 */
export function slider(label, o) {
  const { min = 0, max = 100, step = 1, get, set, format = (v) => String(Math.round(v * 100) / 100), log = false, onEnd } = o;
  const toT = (v) => log ? Math.log(v / min) / Math.log(max / min) : (v - min) / (max - min);
  const fromT = (t) => {
    let v = log ? min * Math.pow(max / min, t) : min + (max - min) * t;
    v = Math.round(v / step) * step;
    return clamp(v, min, max);
  };
  const fill = h('div.sl-fill');
  const val = h('span.sl-val');
  const track = h('div.sl-track', null, fill, h('span.sl-label', null, label), val);
  const el = h('div.ctl.slider', null, track);
  const sync = () => {
    const v = get();
    fill.style.width = `${clamp(toT(v), 0, 1) * 100}%`;
    val.textContent = format(v);
  };
  let dragging = false, startX = 0, startT = 0, moved = false;
  track.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    track.setPointerCapture(e.pointerId);
    dragging = true; moved = false;
    startX = e.clientX; startT = toT(get());
    // 터치는 상대 이동(실수로 값이 튀지 않게), 펜/마우스는 누른 위치로 바로 이동
    if (e.pointerType !== 'touch') {
      const r = track.getBoundingClientRect();
      set(fromT(clamp((e.clientX - r.left) / r.width, 0, 1)));
      sync();
    }
  });
  track.addEventListener('pointermove', (e) => {
    if (!dragging) return;
    const r = track.getBoundingClientRect();
    if (Math.abs(e.clientX - startX) > 3) moved = true;
    const t = e.pointerType === 'touch'
      ? startT + (e.clientX - startX) / r.width
      : (e.clientX - r.left) / r.width;
    set(fromT(clamp(t, 0, 1)));
    sync();
  });
  const end = (e) => {
    if (!dragging) return;
    dragging = false;
    if (e.pointerType === 'touch' && !moved) {
      const r = track.getBoundingClientRect();
      set(fromT(clamp((e.clientX - r.left) / r.width, 0, 1)));
      sync();
    }
    onEnd?.();
  };
  track.addEventListener('pointerup', end);
  track.addEventListener('pointercancel', end);
  // 숫자 직접 입력 (더블탭)
  val.addEventListener('dblclick', () => {
    const s = prompt(label, String(get()));
    if (s != null && !isNaN(+s)) { set(clamp(+s, min, max)); sync(); onEnd?.(); }
  });
  el.sync = sync;
  sync();
  return el;
}

export function toggle(label, get, set) {
  const box = h('span.tg-box');
  const el = h('button.ctl.toggle', { type: 'button' }, box, h('span', null, label));
  const sync = () => el.classList.toggle('on', !!get());
  el.addEventListener('click', () => { set(!get()); sync(); });
  el.sync = sync;
  sync();
  return el;
}

export function select(label, options, get, set) {
  const sel = h('select');
  for (const [v, t] of options) sel.append(h('option', { value: v }, t));
  const el = h('label.ctl.select', null, h('span', null, label), sel);
  const sync = () => { sel.value = String(get()); };
  sel.addEventListener('change', () => { set(sel.value); });
  el.sync = sync;
  el.setOptions = (opts) => { sel.innerHTML = ''; for (const [v, t] of opts) sel.append(h('option', { value: v }, t)); sync(); };
  sync();
  return el;
}

/** 버튼 여러 개 중 하나 선택 (세그먼트) */
export function segmented(options, get, set, cls = '') {
  const el = h(`div.ctl.seg${cls ? '.' + cls : ''}`);
  const btns = options.map(([v, t, title]) => {
    const b = h('button', { type: 'button', title: title || '' });
    if (t instanceof Node) b.append(t); else b.innerHTML = t;
    b.addEventListener('click', () => { set(v); sync(); });
    el.append(b);
    return [v, b];
  });
  const sync = () => { const cur = get(); for (const [v, b] of btns) b.classList.toggle('on', v === cur); };
  el.sync = sync;
  sync();
  return el;
}

export function colorInput(label, get, set) {
  const input = h('input', { type: 'color' });
  const el = h('label.ctl.color', null, h('span', null, label), input);
  const sync = () => { input.value = get() || '#000000'; };
  input.addEventListener('input', () => set(input.value));
  el.sync = sync;
  sync();
  return el;
}

export function button(label, onClick, cls = '') {
  const b = h(`button.btn${cls ? '.' + cls : ''}`, { type: 'button' });
  b.innerHTML = label;
  b.addEventListener('click', onClick);
  return b;
}

export function row(...children) { return h('div.row', null, ...children); }
export function section(title, ...children) { return h('div.section', null, title ? h('div.section-title', null, title) : null, ...children); }

export function syncAll(root) {
  root.querySelectorAll('.ctl').forEach(el => el.sync?.());
}
