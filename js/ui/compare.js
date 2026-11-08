// 버전 비교: 이전 저장본과 지금을 나란히 / 밀어서 겹쳐 보기 / 달라진 곳 빨갛게
import { app } from '../app.js';
import { h, createCanvas } from '../core/util.js';
import { openModal } from './dialogs.js';

const fit = (doc, max) => app.compositor.flatten(doc, { scale: Math.min(1, max / Math.max(doc.width, doc.height)), paper: true, background: '#ffffff' });

/** a: 이전 문서, b: 지금 문서 */
export function compareDocs(a, b, labelA = '이전', labelB = '지금') {
  const MAX = Math.min(1400, Math.max(innerWidth, innerHeight));
  const ca = fit(a, MAX), cb = fit(b, MAX);
  const W = Math.max(ca.width, cb.width), H = Math.max(ca.height, cb.height);
  let mode = 'wipe', pos = 0.5;
  const view = createCanvas(W, H);
  view.className = 'compare-canvas';
  const x = view.getContext('2d');
  // 달라진 곳: 밝기 차이가 있는 픽셀을 빨갛게, 나머지는 흐리게
  const diff = (() => {
    const c = createCanvas(W, H), cx = c.getContext('2d', { willReadFrequently: true });
    cx.fillStyle = '#fff'; cx.fillRect(0, 0, W, H); cx.drawImage(ca, 0, 0);
    const da = cx.getImageData(0, 0, W, H);
    cx.fillRect(0, 0, W, H); cx.drawImage(cb, 0, 0);
    const db = cx.getImageData(0, 0, W, H);
    const out = cx.createImageData(W, H);
    let changed = 0;
    for (let i = 0; i < out.data.length; i += 4) {
      const d = Math.abs(da.data[i] - db.data[i]) + Math.abs(da.data[i + 1] - db.data[i + 1]) + Math.abs(da.data[i + 2] - db.data[i + 2]);
      const g = (db.data[i] + db.data[i + 1] + db.data[i + 2]) / 3;
      if (d > 24) { out.data[i] = 235; out.data[i + 1] = 40; out.data[i + 2] = 40; changed++; }
      else { const v = 255 - (255 - g) * 0.25; out.data[i] = out.data[i + 1] = out.data[i + 2] = v; }
      out.data[i + 3] = 255;
    }
    cx.putImageData(out, 0, 0);
    return { canvas: c, ratio: changed / (W * H) };
  })();
  const draw = () => {
    x.fillStyle = '#fff'; x.fillRect(0, 0, W, H);
    if (mode === 'diff') { x.drawImage(diff.canvas, 0, 0); return; }
    if (mode === 'wipe') {
      x.drawImage(cb, 0, 0);
      x.save(); x.beginPath(); x.rect(0, 0, W * pos, H); x.clip(); x.fillStyle = '#fff'; x.fillRect(0, 0, W, H); x.drawImage(ca, 0, 0); x.restore();
      x.fillStyle = '#e5484d'; x.fillRect(W * pos - 1, 0, 3, H);
      return;
    }
    x.globalAlpha = 1; x.drawImage(cb, 0, 0); x.globalAlpha = pos; x.drawImage(ca, 0, 0); x.globalAlpha = 1;
  };
  const side = h('div.compare-side', null,
    h('figure', null, ca, h('figcaption', null, labelA)),
    h('figure', null, cb, h('figcaption', null, labelB)));
  const range = h('input', { type: 'range', min: 0, max: 1, step: 0.01, value: pos });
  range.oninput = () => { pos = +range.value; draw(); };
  // 그림 위를 끌어도 경계가 따라옴
  view.addEventListener('pointermove', (e) => { if (mode === 'wipe' && e.buttons) { const r = view.getBoundingClientRect(); pos = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)); range.value = pos; draw(); } });
  const stage = h('div.compare-stage', null, view);
  const info = h('div.help');
  const tabs = h('div.seg', null, ...[['wipe', '밀어서 비교'], ['fade', '겹쳐 보기'], ['diff', '달라진 곳'], ['side', '나란히']].map(([k, t]) => {
    const b = h(`button${k === mode ? '.on' : ''}`, { type: 'button' }, t);
    b.onclick = () => { mode = k; tabs.querySelectorAll('button').forEach(z => z.classList.toggle('on', z === b)); sync(); };
    return b;
  }));
  const sync = () => {
    stage.innerHTML = '';
    stage.append(mode === 'side' ? side : view);
    range.style.display = mode === 'wipe' || mode === 'fade' ? '' : 'none';
    info.textContent = mode === 'wipe' ? `왼쪽: ${labelA} · 오른쪽: ${labelB} (그림 위를 끌거나 슬라이더)` : mode === 'fade' ? `슬라이더를 오른쪽으로 갈수록 ${labelA}` : mode === 'diff' ? `빨간 곳이 달라진 부분 (전체의 ${(diff.ratio * 100).toFixed(1)}%)` : '';
    if (mode !== 'side') draw();
  };
  sync();
  openModal({ title: `버전 비교: ${labelA} ↔ ${labelB}`, body: h('div.compare-wrap', null, tabs, range, info, stage), wide: true, actions: [{ label: '닫기', primary: true }] });
}
