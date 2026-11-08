// 컷 나누기 템플릿: 자주 쓰는 페이지 컷 배치를 골라 바로 쓰고, 지금 페이지의 컷을 템플릿으로 저장
import { app } from '../app.js';
import { h } from '../core/util.js';
import { openModal, form } from './dialogs.js';
import { printRects } from './manuscript.js';
import { panelLayer, setPanelsWithMeta, panelPts, panelTool } from '../tools/comic.js';

const R = (x, y, w, hh) => [[x, y], [x + w, y], [x + w, y + hh], [x, y + hh]];
const T3 = 1 / 3;
// 단위 좌표(0~1, 컷 들어갈 영역 기준). 칸 사이 간격은 적용할 때 넣음
export const PANEL_TEMPLATES = [
  { name: '1컷 (한 장 전체)', panels: [R(0, 0, 1, 1)] },
  { name: '2단', panels: [R(0, 0, 1, 0.5), R(0, 0.5, 1, 0.5)] },
  { name: '3단', panels: [R(0, 0, 1, T3), R(0, T3, 1, T3), R(0, 2 * T3, 1, T3)] },
  { name: '4컷 (세로 4단)', panels: [0, 1, 2, 3].map(i => R(0, i / 4, 1, 0.25)) },
  { name: '2×2', panels: [R(0, 0, 0.5, 0.5), R(0.5, 0, 0.5, 0.5), R(0, 0.5, 0.5, 0.5), R(0.5, 0.5, 0.5, 0.5)] },
  { name: '3단 + 가운데 2칸', panels: [R(0, 0, 1, T3), R(0, T3, 0.5, T3), R(0.5, T3, 0.5, T3), R(0, 2 * T3, 1, T3)] },
  { name: '계단 (2-1-2)', panels: [R(0, 0, 0.6, T3), R(0.6, 0, 0.4, T3), R(0, T3, 1, T3), R(0, 2 * T3, 0.4, T3), R(0.4, 2 * T3, 0.6, T3)] },
  { name: '큰 컷 + 아래 2칸', panels: [R(0, 0, 1, 0.62), R(0, 0.62, 0.5, 0.38), R(0.5, 0.62, 0.5, 0.38)] },
  { name: '위 2칸 + 큰 컷', panels: [R(0, 0, 0.5, 0.38), R(0.5, 0, 0.5, 0.38), R(0, 0.38, 1, 0.62)] },
  { name: '세로 2칸 + 오른쪽 긴 컷', panels: [R(0, 0, 0.55, 0.5), R(0, 0.5, 0.55, 0.5), R(0.55, 0, 0.45, 1)] },
  { name: '대각선 2컷', panels: [[[0, 0], [1, 0], [1, 0.4], [0, 0.6]], [[0, 0.6], [1, 0.4], [1, 1], [0, 1]]] },
  { name: '대각선 3단', panels: [[[0, 0], [1, 0], [1, 0.28], [0, 0.38]], [[0, 0.38], [1, 0.28], [1, 0.68], [0, 0.6]], [[0, 0.6], [1, 0.68], [1, 1], [0, 1]]] },
  { name: '가운데 사선 2칸', panels: [R(0, 0, 1, T3), [[0, T3], [0.62, T3], [0.38, 2 * T3], [0, 2 * T3]], [[0.62, T3], [1, T3], [1, 2 * T3], [0.38, 2 * T3]], R(0, 2 * T3, 1, T3)] },
  { name: '웹툰 세로 3컷', webtoon: true, panels: [R(0, 0, 1, 0.28), R(0, 0.36, 1, 0.28), R(0, 0.72, 1, 0.28)] },
  { name: '웹툰 큰 컷 + 작은 컷', webtoon: true, panels: [R(0, 0, 1, 0.55), R(0.15, 0.68, 0.7, 0.32)] },
];

const near = (a, b) => Math.abs(a - b) < 1e-6;
/** 다각형 안쪽으로 변마다 다른 거리만큼 줄이기 (볼록 다각형) */
function insetPoly(pts, distOf) {
  const n = pts.length;
  let area = 0;
  for (let i = 0; i < n; i++) { const [x1, y1] = pts[i], [x2, y2] = pts[(i + 1) % n]; area += x1 * y2 - x2 * y1; }
  const sgn = area > 0 ? 1 : -1;
  const lines = [];
  for (let i = 0; i < n; i++) {
    const a = pts[i], b = pts[(i + 1) % n];
    const dx = b[0] - a[0], dy = b[1] - a[1], L = Math.hypot(dx, dy) || 1;
    const nx = -dy / L * sgn, ny = dx / L * sgn;               // 안쪽 법선
    const d = distOf(a, b, nx, ny);
    lines.push({ p: [a[0] + nx * d, a[1] + ny * d], v: [dx, dy] });
  }
  const out = [];
  for (let i = 0; i < n; i++) {
    const A = lines[(i - 1 + n) % n], B = lines[i];
    const den = A.v[0] * B.v[1] - A.v[1] * B.v[0];
    if (Math.abs(den) < 1e-9) { out.push(B.p); continue; }
    const t = ((B.p[0] - A.p[0]) * B.v[1] - (B.p[1] - A.p[1]) * B.v[0]) / den;
    out.push([A.p[0] + A.v[0] * t, A.p[1] + A.v[1] * t]);
  }
  return out;
}

/** 컷이 들어갈 영역: 원고 설정이 있으면 안전선 안(또는 재단선 안), 없으면 가장자리 여백 */
export function frameArea(doc, mode = 'safe', margin = null) {
  const r = printRects(doc);
  if (r && mode === 'safe') return { ...r.safe };
  if (r && mode === 'trim') return { ...r.trim };
  const m = margin ?? Math.round(doc.width * 0.07);
  return { x: m, y: m, w: doc.width - m * 2, h: doc.height - m * 2 };
}

/** 템플릿 → 문서 좌표 컷 목록 */
export function layoutPanels(tmpl, area, gx, gy) {
  return tmpl.panels.map((poly) => {
    // 바깥 테두리에 붙은 변은 그대로, 다른 컷과 맞닿은 변만 간격의 절반씩 안으로
    const P = tmpl.raw ? poly : insetPoly(poly.map(([x, y]) => [x * area.w, y * area.h]), (a, b, nx, ny) => {
      const ax = a[0] / area.w, ay = a[1] / area.h, bx = b[0] / area.w, by = b[1] / area.h;
      const onEdge = (near(ax, 0) && near(bx, 0)) || (near(ax, 1) && near(bx, 1)) || (near(ay, 0) && near(by, 0)) || (near(ay, 1) && near(by, 1));
      return onEdge ? 0 : (Math.abs(nx) * gx + Math.abs(ny) * gy) / 2;
    }).map(([x, y]) => [x / area.w, y / area.h]);
    return { pts: P.map(([x, y]) => ({ x: Math.round((area.x + x * area.w) * 10) / 10, y: Math.round((area.y + y * area.h) * 10) / 10 })) };
  });
}

function thumb(tmpl, w = 54, hh = 76) {
  const c = h('canvas.pt-thumb', { width: w, height: hh }), x = c.getContext('2d');
  x.fillStyle = '#fff'; x.fillRect(0, 0, w, hh);
  const area = { x: 4, y: 4, w: w - 8, h: hh - 8 };
  x.strokeStyle = '#111'; x.lineWidth = 1.5;
  for (const p of layoutPanels(tmpl, area, 3, 4)) { x.beginPath(); p.pts.forEach((q, i) => (i ? x.lineTo(q.x, q.y) : x.moveTo(q.x, q.y))); x.closePath(); x.stroke(); }
  return c;
}

const saved = () => (app.settings.panelTemplates ||= []);

export function applyTemplate(tmpl, o = {}) {
  const doc = app.doc;
  const area = o.area || frameArea(doc, o.mode || 'safe', o.margin);
  const gx = o.gx ?? panelTool.opt.gutterX ?? Math.round(doc.width * 0.02);
  const gy = o.gy ?? panelTool.opt.gutterY ?? Math.round(doc.width * 0.035);
  const panels = layoutPanels(tmpl, area, gx, gy);
  const layer = panelLayer(true);
  setPanelsWithMeta(layer, { ...layer.meta, panels, line: o.line ?? layer.meta.line });
  panelTool.opt.gutterX = gx; panelTool.opt.gutterY = gy;
  return panels;
}

/** 지금 페이지의 컷을 템플릿으로 (컷 영역 기준 비율로 저장 → 다른 크기 원고에도 맞음) */
export function templateFromCurrent(name, mode = 'safe') {
  const doc = app.doc, layer = panelLayer(false);
  if (!layer?.meta?.panels?.length) return null;
  const area = frameArea(doc, mode);
  const t = { name, raw: true, panels: layer.meta.panels.map(p => panelPts(p).map(q => [(q.x - area.x) / area.w, (q.y - area.y) / area.h])) };
  return t;
}

export function templatesDialog() {
  const doc = app.doc;
  const hasPrint = !!printRects(doc);
  const opts = { mode: hasPrint ? 'safe' : 'margin', gx: panelTool.opt.gutterX ?? Math.round(doc.width * 0.02), gy: panelTool.opt.gutterY ?? Math.round(doc.width * 0.035) };
  const body = h('div.pt-dialog');
  const render = () => {
    body.innerHTML = '';
    const webtoon = doc.height > doc.width * 2.2;
    const list = [...saved().map(t => ({ ...t, mine: true })), ...PANEL_TEMPLATES.filter(t => !!t.webtoon === webtoon || !webtoon)];
    const grid = h('div.pt-grid');
    for (const t of list) {
      const card = h('button.pt-card', { type: 'button', title: t.name, onclick: () => { applyTemplate(t, opts); app.toast(`"${t.name}" 컷을 넣었습니다 (실행취소 가능, 컷 도구로 다듬기)`); app.setTool('panel'); m.close?.(); } }, thumb(t), h('small', null, t.name));
      if (t.mine) {
        const i = saved().findIndex(s => s.name === t.name);
        card.append(h('span.pt-mine', null,
          h('i', { title: '이름 바꾸기', onclick: async (e) => { e.stopPropagation(); const v = await form('템플릿 이름', [{ key: 'n', label: '이름', type: 'text', value: t.name }]); if (v?.n) { saved()[i].name = v.n; app.saveSettingsSoon(); render(); } } }, '✎'),
          h('i', { title: '지우기', onclick: (e) => { e.stopPropagation(); saved().splice(i, 1); app.saveSettingsSoon(); render(); } }, '×')));
      }
      grid.append(card);
    }
    const num = (label, k) => h('label.pt-num', null, label, h('input', { type: 'number', value: opts[k], min: 0, onchange: (e) => { opts[k] = Math.max(0, +e.target.value || 0); } }));
    body.append(
      h('div.row', null,
        h('select', { onchange: (e) => { opts.mode = e.target.value; } }, ...[['safe', '안전선 안'], ['trim', '재단선까지'], ['margin', '가장자리 여백']].filter(([k]) => hasPrint || k === 'margin').map(([k, l]) => h('option', { value: k, selected: opts.mode === k }, l))),
        num('좌우 간격', 'gx'), num('상하 간격', 'gy')),
      h('div.help', null, '누르면 지금 페이지의 컷이 바뀝니다 (실행취소 가능). 원고 설정이 있으면 안전선(또는 재단선)에 맞춰 넣습니다.'),
      grid,
      h('div.row', null, h('button.btn', { type: 'button', onclick: async () => {
        if (!panelLayer(false)?.meta?.panels?.length) { app.toast('지금 페이지에 컷이 없습니다'); return; }
        const v = await form('지금 컷을 템플릿으로 저장', [{ key: 'n', label: '이름', type: 'text', value: `내 템플릿 ${saved().length + 1}` }]);
        if (!v?.n) return;
        const t = templateFromCurrent(v.n, opts.mode === 'margin' ? 'margin' : opts.mode);
        saved().push(t); app.saveSettingsSoon(); render();
        app.toast(`"${v.n}" 템플릿을 저장했습니다`);
      } }, '지금 컷을 템플릿으로 저장…')),
    );
  };
  render();
  const m = openModal({ title: '컷 나누기 템플릿', body, wide: true, actions: [{ label: '닫기', primary: true }] });
}

export function initPanelTemplates() {
  app.registerCommand('panel.templates', { label: '컷 나누기 템플릿…', run: templatesDialog });
  app.addMenuItem('comic', { command: 'panel.templates' });
}
