// 망점(스크린톤) 플러그인 — 명령 + 선택 메뉴 버튼을 추가하는 예제
// 선택 영역(없으면 캔버스 전체)을 망점으로 채운 새 레이어를 만든다.

export default function setup(app, kit) {
  const last = { spacing: 10, density: 35, angle: 45, color: '#000000', shape: 'dot' };

  function tonePattern(ctx, o) {
    const s = Math.max(3, o.spacing);
    const tile = document.createElement('canvas');
    tile.width = tile.height = s;
    const t = tile.getContext('2d');
    t.fillStyle = o.color;
    // 농도(면적 비율)에 맞는 점 반지름
    const area = (o.density / 100) * s * s;
    if (o.shape === 'line') {
      t.fillRect(0, 0, s, Math.max(0.5, area / s));
    } else {
      const r = Math.sqrt(area / Math.PI);
      t.beginPath(); t.arc(s / 2, s / 2, Math.min(r, s * 0.72), 0, Math.PI * 2); t.fill();
    }
    const p = ctx.createPattern(tile, 'repeat');
    p.setTransform(new DOMMatrix().rotate(o.angle));
    return p;
  }

  async function applyTone() {
    const v = await app.ui.form('망점 톤', [
      { key: 'shape', label: '모양', type: 'segment', options: [['dot', '점'], ['line', '선']], value: last.shape },
      { key: 'spacing', label: '간격 (px, 작을수록 촘촘)', type: 'number', value: last.spacing, min: 3, max: 80 },
      { key: 'density', label: '농도 (%)', type: 'number', value: last.density, min: 1, max: 100 },
      { key: 'angle', label: '각도 (도)', type: 'number', value: last.angle },
      { key: 'color', label: '색', type: 'color', value: last.color },
      { key: 'info', type: 'info', label: '선택 영역이 없으면 캔버스 전체에 붙입니다. 결과는 새 레이어 "톤"에 들어갑니다.' },
    ], { okLabel: '붙이기' });
    if (!v) return;
    Object.assign(last, v);
    const doc = app.doc;
    const sel = doc.selection;
    app.history.batch('망점 톤', () => {
      const layer = kit.ops.addLayer({ name: `톤 ${v.density}%` });
      app.history.pixels(layer, '망점', (ctx) => {
        ctx.save();
        ctx.fillStyle = tonePattern(ctx, last);
        const r = sel.active ? sel.bounds : { x: 0, y: 0, w: doc.width, h: doc.height };
        ctx.fillRect(r.x, r.y, r.w, r.h);
        if (sel.active) {
          ctx.globalCompositeOperation = 'destination-in';
          ctx.drawImage(sel.mask, 0, 0);
        }
        ctx.restore();
      });
    });
  }

  app.registerCommand('tone.apply', { label: '망점 톤 붙이기…', run: applyTone });
  app.addMenuItem('comic', { command: 'tone.apply' });
  kit.registerSelectionAction({ icon: 'tone', label: '망점', run: applyTone });
  app.registerQuickAction('tone', { icon: 'tone', label: '망점 톤', run: applyTone });
  return { name: '망점 톤' };
}
