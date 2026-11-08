// 대본으로 대사 넣기: 대사를 한꺼번에 써 두고, 탭할 때마다 한 줄씩 말풍선+글자로 놓거나 컷 순서대로 자동 배치
import { app } from '../app.js';
import { h } from '../core/util.js';
import { addLayer } from '../core/ops.js';
import { form } from '../ui/dialogs.js';
import { section, toggle, slider, segmented, button, row } from '../ui/controls.js';
import { helpText } from './basic.js';
import { addIcon } from '../ui/icons.js';
import { TEXT_DEFAULTS, textTool, textFontString, renderText, renderBubbles, bubbleLayer, bubbleTool, panelLayer, panelPts } from './comic.js';

addIcon('script', '<path d="M6 3h9l3 3v15H6z"/><path d="M9 9h6M9 12h6M9 15h4"/>');

/**
 * 대본 한 줄 → { text, shape }.
 *   (생각) …  → 구름 풍선     ! 로 시작 / (외침) → 외침 풍선
 *   # … / [나레이션] … → 사각 상자    이름: 대사 → 이름은 빼고 대사만
 */
export function parseLine(raw) {
  let t = raw.trim(), shape = 'ellipse';
  if (!t) return null;
  const tag = (re, s) => { if (re.test(t)) { t = t.replace(re, '').trim(); shape = s; return true; } return false; };
  tag(/^\((생각|속마음)\)\s*/, 'cloud') || tag(/^\((외침|고함)\)\s*/, 'shout') || tag(/^!\s*/, 'shout') || tag(/^(#|\[나레이션\]|\(나레이션\))\s*/, 'rect');
  t = t.replace(/^[^\s:：]{1,12}[:：]\s*/, '');   // "철수: 대사" → "대사"
  return t ? { text: t, shape } : null;
}

/** 글자 폭에 맞춰 줄바꿈 (공백 기준, 너무 긴 단어는 글자 단위) */
function wrap(text, meta, maxW) {
  const c = document.createElement('canvas').getContext('2d');
  c.font = textFontString(meta);
  const out = [];
  for (const para of text.split(/\\n|\n/)) {
    let line = '';
    for (const word of para.split(/(\s+)/)) {
      const tryLine = line + word;
      if (line && c.measureText(tryLine.trim()).width > maxW) { out.push(line.trim()); line = word.trimStart(); }
      else line = tryLine;
      while (c.measureText(line).width > maxW * 1.3 && line.length > 1) {
        let k = line.length - 1;
        while (k > 1 && c.measureText(line.slice(0, k)).width > maxW) k--;
        out.push(line.slice(0, k)); line = line.slice(k);
      }
    }
    if (line.trim()) out.push(line.trim());
  }
  return out.join('\n');
}

/** (cx, cy) 를 가운데로 대사 하나를 말풍선 + 글자 레이어로 */
export function placeLine(cx, cy, item, o = {}) {
  const doc = app.doc;
  const t = textTool.opt;
  const meta = { ...TEXT_DEFAULTS, ...t, align: 'center', vertical: !!o.vertical, text: '', x: cx, y: cy };
  meta.text = o.vertical ? item.text : wrap(item.text, meta, meta.size * (o.chars || 9));
  let tl;
  app.history.batch('대사 배치', () => {
    const bl = bubbleLayer();   // 풍선 레이어를 먼저 (글자가 그 위에 오도록)
    tl = addLayer({ name: item.text.slice(0, 12), kind: 'text', meta }, doc.layers.length);
    renderText(tl);
    // 가운데 맞추기: 그려 본 크기만큼 위로/오른쪽으로
    const b0 = tl.meta.box;
    tl.meta.y = cy - b0.h / 2 + (meta.y - b0.y);
    if (o.vertical) tl.meta.x = cx + b0.w / 2 - (b0.x + b0.w - meta.x) + 0;
    renderText(tl);
    const b = tl.meta.box;
    const k = item.shape === 'rect' ? 1.15 : item.shape === 'cloud' ? 1.55 : 1.45;
    const pad = meta.size * 0.6;
    const bw = b.w * k + pad, bh = b.h * k + pad;
    const o2 = bubbleTool.opt;
    const bub = {
      id: 'b' + Date.now().toString(36) + ((Math.random() * 1e4) | 0),
      x: b.x + b.w / 2 - bw / 2, y: b.y + b.h / 2 - bh / 2, w: bw, h: bh,
      shape: item.shape, line: o2.line, fill: o2.fill, stroke: o2.stroke, tailWidth: o2.tailWidth, seed: (Math.random() * 1e6) | 0,
      tail: item.shape === 'rect' || !o.tail ? null : { x: b.x + b.w / 2 + bw * 0.15, y: b.y + b.h / 2 + bh * 0.9 },
      textId: tl.id,
    };
    const before = structuredClone(bl.meta.bubbles);
    bl.meta.bubbles.push(bub);
    renderBubbles(bl);
    const after = structuredClone(bl.meta.bubbles);
    app.history.push({ label: '말풍선', undo: () => { bl.meta.bubbles = structuredClone(before); renderBubbles(bl); }, redo: () => { bl.meta.bubbles = structuredClone(after); renderBubbles(bl); } });
    // 글자가 풍선보다 위에 오도록 맨 위로
    doc.setActive(tl);
  });
  return tl;
}

/** 컷을 읽는 순서로: 위 줄부터, 줄 안에서는 왼→오(웹툰·한국식) 또는 오→왼(일본식) */
function panelsInOrder(rtl) {
  const l = panelLayer(false);
  const list = (l?.meta?.panels || []).map(p => {
    const pts = panelPts(p);
    const xs = pts.map(q => q.x), ys = pts.map(q => q.y);
    return { x: Math.min(...xs), y: Math.min(...ys), w: Math.max(...xs) - Math.min(...xs), h: Math.max(...ys) - Math.min(...ys) };
  });
  list.sort((a, b) => a.y - b.y);
  const rows = [];
  for (const p of list) {
    const r = rows.find(r => Math.abs(r[0].y - p.y) < Math.min(r[0].h, p.h) * 0.4);
    if (r) r.push(p); else rows.push([p]);
  }
  return rows.flatMap(r => r.sort((a, b) => (rtl ? b.x - a.x : a.x - b.x)));
}

export const scriptTool = {
  id: 'script', name: '대본 배치', icon: 'script', key: null, cursor: 'crosshair',
  queue: [], o: { tail: true, vertical: false, chars: 9, rtl: false },

  down() {},
  up(e) {
    const item = this.queue[0];
    if (!item) { app.toast('남은 대사가 없습니다. 옵션에서 대본을 넣으세요'); return; }
    placeLine(e.x, e.y, item, this.o);
    this.queue.shift();
    app.bus.emit('tool:options');
    app.requestRender();
    if (!this.queue.length) app.toast('대사를 모두 놓았습니다');
  },

  async load() {
    const v = await form('대본 넣기', [
      { key: 'text', label: '대사 (한 줄 = 말풍선 하나)', type: 'textarea', rows: 10, value: app.settings.scriptDraft || '' },
      { key: 'info', type: 'info', label: '"이름: 대사" 는 이름을 빼고 넣습니다. (생각) → 구름, ! 또는 (외침) → 외침, # 또는 [나레이션] → 사각 상자. 줄 안에서 \\n 은 줄바꿈.' },
    ], { okLabel: '넣기' });
    if (!v) return;
    app.settings.scriptDraft = v.text;
    app.saveSettingsSoon();
    this.queue = v.text.split('\n').map(parseLine).filter(Boolean);
    app.toast(`대사 ${this.queue.length}줄. 놓을 곳을 차례로 탭하세요`);
    app.bus.emit('tool:options');
  },

  /** 컷 순서대로 자동 배치: 대사를 컷 수만큼 나눠 각 컷 위쪽부터 */
  autoPlace() {
    const panels = panelsInOrder(this.o.rtl);
    const doc = app.doc;
    const items = this.queue.splice(0);
    if (!items.length) { app.toast('먼저 대본을 넣으세요'); return; }
    const areas = panels.length ? panels : [{ x: doc.width * 0.05, y: doc.height * 0.05, w: doc.width * 0.9, h: doc.height * 0.9 }];
    const per = Math.ceil(items.length / areas.length);
    app.history.batch('대본 자동 배치', () => {
      areas.forEach((a, i) => {
        const mine = items.slice(i * per, (i + 1) * per);
        mine.forEach((it, j) => {
          // 읽는 방향으로 지그재그: 첫 대사는 읽기 시작하는 쪽 위
          const fx = mine.length === 1 ? 0.5 : (this.o.rtl ? 0.75 - 0.5 * j / (mine.length - 1) : 0.25 + 0.5 * j / (mine.length - 1));
          const fy = 0.2 + 0.6 * j / Math.max(1, mine.length);
          placeLine(a.x + a.w * fx, a.y + a.h * fy, it, this.o);
        });
      });
    });
    app.toast(`${items.length}줄을 ${panels.length ? `컷 ${areas.length}개에` : '페이지에'} 나눠 놓았습니다. 말풍선 도구로 위치를 다듬으세요`);
    app.bus.emit('tool:options');
  },

  options(el) {
    const o = this.o;
    el.append(section('대본',
      row(button('대본 넣기…', () => this.load()), button('컷 순서대로 자동 배치', () => this.autoPlace())),
      h('div.script-queue', null, ...(this.queue.length ? this.queue.slice(0, 8).map((it, i) => h(`div${i === 0 ? '.next' : ''}`, null, `${i === 0 ? '▶ ' : ''}${it.text}`)) : [h('div.help', null, '남은 대사가 없습니다')]), this.queue.length > 8 ? h('small', null, `… 외 ${this.queue.length - 8}줄`) : null),
      this.queue.length ? row(button('이 대사 건너뛰기', () => { this.queue.shift(); app.bus.emit('tool:options'); }), button('비우기', () => { this.queue = []; app.bus.emit('tool:options'); })) : null,
      toggle('꼬리 달기', () => o.tail, v => { o.tail = v; }),
      toggle('세로쓰기', () => o.vertical, v => { o.vertical = v; }),
      slider('한 줄 글자 수', { min: 4, max: 20, step: 1, get: () => o.chars, set: v => { o.chars = v; } }),
      segmented([['ltr', '왼→오 (웹툰·한국식)'], ['rtl', '오→왼 (일본식)']], () => (o.rtl ? 'rtl' : 'ltr'), v => { o.rtl = v === 'rtl'; }),
      helpText('글자 크기·글꼴은 텍스트 도구의 기본값을 따릅니다. 놓은 뒤에는 말풍선 도구로 옮기고 크기를 바꿀 수 있습니다 (대사도 같이 움직임).'),
    ));
  },
};
