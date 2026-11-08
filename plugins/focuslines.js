// 집중선 플러그인 — 도구 하나를 추가하는 예제
// 드래그로 "비워둘 가운데 타원"을 정하면 바깥에서 안쪽으로 뾰족한 선을 그린다.

export default function setup(app, kit) {
  const { slider, colorInput, section, toggle } = kit.controls;
  kit.addIcon('focus', '<circle cx="12" cy="12" r="3"/><path d="M12 2v5M12 17v5M2 12h5M17 12h5M5 5l3.5 3.5M15.5 15.5L19 19M5 19l3.5-3.5M15.5 8.5L19 5"/>');

  const opt = { count: 140, width: 7, jitter: 0.25, gapRandom: 0.35, color: '#000000', newLayer: true };

  function draw(ctx, doc, cx, cy, rx, ry) {
    const R = Math.hypot(doc.width, doc.height);
    ctx.save();
    ctx.fillStyle = opt.color;
    for (let i = 0; i < opt.count; i++) {
      const a = (i / opt.count) * Math.PI * 2 + (Math.random() - 0.5) * (Math.PI * 2 / opt.count);
      if (Math.random() < opt.gapRandom * 0.4) continue;           // 선 사이 빈틈
      const k = 1 + Math.random() * opt.jitter * 1.5;              // 안쪽 끝 들쭉날쭉
      const ix = cx + Math.cos(a) * rx * k, iy = cy + Math.sin(a) * ry * k;
      const ox = cx + Math.cos(a) * R, oy = cy + Math.sin(a) * R;
      const w = opt.width * (0.4 + Math.random()) * (R / 1500);
      const nx = -Math.sin(a) * w, ny = Math.cos(a) * w;
      ctx.beginPath();
      ctx.moveTo(ox + nx, oy + ny);
      ctx.lineTo(ix, iy);
      ctx.lineTo(ox - nx, oy - ny);
      ctx.closePath();
      ctx.fill();
    }
    ctx.restore();
  }

  let drag = null;
  app.registerTool({
    id: 'focuslines', name: '집중선', icon: 'focus', cursor: 'crosshair',
    down(e) { drag = { x0: e.x, y0: e.y, x1: e.x, y1: e.y }; this.busy = true; },
    move(e) { if (drag) { drag.x1 = e.x; drag.y1 = e.y; app.requestRender(); } },
    up() {
      const d = drag; drag = null; this.busy = false;
      if (!d) return;
      const doc = app.doc;
      const cx = (d.x0 + d.x1) / 2, cy = (d.y0 + d.y1) / 2;
      const rx = Math.max(20, Math.abs(d.x1 - d.x0) / 2), ry = Math.max(20, Math.abs(d.y1 - d.y0) / 2);
      app.history.batch('집중선', () => {
        const layer = opt.newLayer ? kit.ops.addLayer({ name: '집중선' }) : doc.active;
        if (!kit.ops.checkEditable(layer)) return;
        app.history.pixels(layer, '집중선', (ctx) => {
          draw(ctx, doc, cx, cy, rx, ry);
          if (doc.selection.active) {
            ctx.save();
            ctx.globalCompositeOperation = 'destination-in';
            ctx.drawImage(doc.selection.mask, 0, 0);
            ctx.restore();
          }
        });
      });
    },
    cancel() { drag = null; this.busy = false; },
    overlay(ctx, view) {
      if (!drag) return;
      const m = view.m;
      ctx.transform(m.a, m.b, m.c, m.d, m.e, m.f);
      ctx.strokeStyle = '#e8484a';
      ctx.lineWidth = 2 / view.zoom;
      ctx.beginPath();
      ctx.ellipse((drag.x0 + drag.x1) / 2, (drag.y0 + drag.y1) / 2, Math.abs(drag.x1 - drag.x0) / 2, Math.abs(drag.y1 - drag.y0) / 2, 0, 0, Math.PI * 2);
      ctx.stroke();
    },
    options(el) {
      el.append(section('집중선',
        slider('선 개수', { min: 20, max: 500, get: () => opt.count, set: v => { opt.count = v; } }),
        slider('굵기', { min: 1, max: 30, step: 0.5, get: () => opt.width, set: v => { opt.width = v; } }),
        slider('안쪽 들쭉날쭉', { min: 0, max: 1, step: 0.01, get: () => opt.jitter, set: v => { opt.jitter = v; }, format: v => Math.round(v * 100) + '%' }),
        slider('빈틈', { min: 0, max: 1, step: 0.01, get: () => opt.gapRandom, set: v => { opt.gapRandom = v; }, format: v => Math.round(v * 100) + '%' }),
        colorInput('색', () => opt.color, v => { opt.color = v; }),
        toggle('새 레이어에 그리기', () => opt.newLayer, v => { opt.newLayer = v; }),
        kit.helpText('드래그로 가운데 비울 영역(타원)을 정하세요. 선택 영역이 있으면 그 안에만 그립니다.'),
      ));
    },
  });

  app.addMenuItem('comic', { label: '집중선 도구', run: () => app.setTool('focuslines') });
  return { name: '집중선' };
}
