// 채우기, 스포이트, 화면 조작 도구
import { app } from '../app.js';
import { fillMaskAsync, maskToCanvas } from '../core/raster.js';
import { checkEditable } from '../core/ops.js';
import { sampleLayer } from '../core/compositor.js';
import { createCanvas, h } from '../core/util.js';
import { slider, segmented, section, row } from '../ui/controls.js';
import { addRecentColor } from './brush.js';

/** 채우기/자동선택이 참조할 이미지 */
export function referenceImage(source) {
  const doc = app.doc;
  if (source === 'layer') return doc.active.ctx.getImageData(0, 0, doc.width, doc.height);
  const c = createCanvas(doc.width, doc.height);
  const x = c.getContext('2d', { willReadFrequently: true });
  const refs = doc.layers.filter(l => l.ref);
  if (source === 'ref' && !refs.length) app.toast('참조 레이어가 없어 모든 레이어를 참조합니다 (레이어 패널 "참조")');
  // 밑그림 레이어는 참조에서 제외, 용지는 투명 취급
  app.compositor.render(x, doc, new DOMMatrix(), { paper: false, excludeDraft: true, only: source === 'ref' && refs.length ? new Set(refs) : null });
  return x.getImageData(0, 0, doc.width, doc.height);
}

export const fillTool = {
  id: 'fill', name: '채우기', icon: 'fill', key: 'g', cursor: 'crosshair',
  opt: { source: 'all', tolerance: 12, gap: 4, expand: 2, dither: 0, ditherSize: 4 },
  async down(e) {
    const doc = app.doc;
    const layer = doc.active?.target;
    if (!checkEditable(layer)) return;
    const x = Math.floor(e.x), y = Math.floor(e.y);
    if (x < 0 || y < 0 || x >= doc.width || y >= doc.height) return;
    if (this.working) return;
    this.working = true;
    let res;
    try {
      res = await app.task('채우기', async () => fillMaskAsync(referenceImage(this.opt.source), x, y, this.opt), { delay: 250 });
    } finally { this.working = false; }
    if (!res || app.doc !== doc) return;
    const { mask, bounds } = res;
    if (!bounds) return;
    const P = await import('../core/pixel.js');
    const fillColor = doc.pixel?.lock ? P.nearestHex(app.color.fg) : app.color.fg;
    const c = maskToCanvas(mask, doc.width, bounds, fillColor);
    const cx = c.getContext('2d');
    // 디더 무늬로 채우기 (문서 좌표 기준이라 옆 칸과 무늬가 이어짐)
    if (this.opt.dither > 0 && this.opt.dither < 1) {
      P.applyDither(cx, this.opt.dither, this.opt.ditherSize, { x: 0, y: 0, w: bounds.w, h: bounds.h }, bounds.x, bounds.y);
    }
    if (doc.selection.active) {
      cx.globalCompositeOperation = 'destination-in';
      cx.drawImage(doc.selection.mask, bounds.x, bounds.y, bounds.w, bounds.h, 0, 0, bounds.w, bounds.h);
    }
    app.history.pixels(layer, '채우기', (ctx) => {
      ctx.save();
      if (layer.alphaLock) ctx.globalCompositeOperation = 'source-atop';
      ctx.drawImage(c, bounds.x, bounds.y);
      ctx.restore();
    }, bounds);
    addRecentColor(app.color.fg);
  },
  options(el) {
    const o = this.opt;
    el.append(section('채우기',
      segmented([['all', '모든 레이어'], ['ref', '참조 레이어'], ['layer', '현재 레이어']], () => o.source, v => { o.source = v; }),
      slider('색 허용 오차', { min: 0, max: 255, get: () => o.tolerance, set: v => { o.tolerance = v; } }),
      slider('틈 막기 (이 너비까지 빈틈은 선으로)', { min: 0, max: 50, get: () => o.gap, set: v => { o.gap = v; }, format: v => (v ? v + 'px' : '끔') }),
      h('div.help', null, '선이 살짝 끊겨 있어도 이 너비 이하의 빈틈은 막힌 것으로 보고, 틈 너머로는 칠하지 않습니다. 선이 많이 끊긴 러프는 10~20px.'),
      slider('영역 확장', { min: 0, max: 10, get: () => o.expand, set: v => { o.expand = v; }, format: v => v + 'px' }),
      helpText('선 아래까지 칠하려면 "영역 확장"을 1~3px로. 선이 살짝 끊겨 있으면 "틈 닫기"를 올리세요.'),
      slider('디더 무늬 (칠하는 칸 비율)', { min: 0, max: 1, step: 0.0625, get: () => o.dither, set: v => { o.dither = v >= 1 ? 0 : v; }, format: v => (v > 0 && v < 1 ? Math.round(v * 100) + '%' : '끔') }),
      segmented([[2, '2×2'], [4, '4×4']], () => o.ditherSize, v => { o.ditherSize = v; }),
    ));
  },
};

export const eyedropperTool = {
  id: 'eyedropper', name: '스포이트', icon: 'eyedropper', key: 'i', cursor: 'crosshair', allowAltPick: false,
  opt: { source: 'all' },
  pick(e) {
    const doc = app.doc;
    if (e.x < 0 || e.y < 0 || e.x >= doc.width || e.y >= doc.height) return;
    const s = this.opt.source === 'layer' ? sampleLayer(doc.active, e.x, e.y) : app.compositor.sample(doc, e.x, e.y);
    if (s.a === 0 && this.opt.source === 'layer') return;
    this.preview = s.hex;
    app.setColor(s.hex);
  },
  down(e) { this.pick(e); this.busy = true; },
  move(e) { this.pick(e); },
  up() { this.busy = false; this.preview = null; app.requestRender(); },
  cancel() { this.busy = false; this.preview = null; },
  overlay(ctx) {
    if (!this.busy || !this.preview) return;
    const p = app.pointer;
    ctx.fillStyle = this.preview;
    ctx.strokeStyle = '#fff';
    ctx.lineWidth = 3;
    ctx.beginPath(); ctx.arc(p.sx, p.sy - 50, 22, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
  },
  options(el) {
    const o = this.opt;
    el.append(section('스포이트',
      segmented([['all', '보이는 색'], ['layer', '현재 레이어']], () => o.source, v => { o.source = v; }),
      helpText('펜 측면 버튼, Alt 키, 손가락 길게 누르기로도 색을 집을 수 있습니다.'),
    ));
  },
};

app.registerCommand('eyedropper.sampleAt', {
  label: '색 집기',
  run: (x, y) => {
    eyedropperTool.pick({ x, y });
    app.toast('색을 가져왔습니다');
  },
});

export const viewTool = {
  id: 'view', name: '화면 이동', icon: 'hand', key: 'h', cursor: 'grab',
  mode: 'pan',
  options(el) {
    el.append(section('화면 조작',
      segmented([['pan', '이동'], ['zoom', '확대(좌우 드래그)'], ['rotate', '회전']], () => this.mode, v => { this.mode = v; }),
      row(
        btn('맞춤', () => app.run('view.fit')),
        btn('100%', () => app.run('view.actual')),
        btn('회전 초기화', () => app.run('view.resetRotation')),
        btn('좌우 반전', () => app.run('view.flip')),
      ),
      helpText('두 손가락: 이동/확대/회전 · 두 손가락 탭: 실행취소 · 세 손가락 탭: 다시실행 · 네 손가락 탭: 패널 숨기기'),
    ));
  },
};

function btn(label, fn) {
  const b = document.createElement('button');
  b.className = 'btn';
  b.textContent = label;
  b.onclick = fn;
  return b;
}

export function helpText(t) {
  const d = document.createElement('div');
  d.className = 'help';
  d.textContent = t;
  return d;
}

