// AI 고르기 도구: 그림을 한 번 읽어 두고, 탭한 곳의 물체를 선택 영역으로 (SlimSAM)
import { app } from '../app.js';
import { addIcon } from '../ui/icons.js';
import { section, button } from '../ui/controls.js';
import * as R from './runtime.js';
import { licenseGate } from './license.js';
import { chooseModel } from './features.js';

addIcon('aisel', '<path d="M4 4h6M4 4v6M20 4h-6M20 4v6M4 20h6M4 20v-6M20 20h-6M20 20v-6"/><circle cx="12" cy="12" r="2.5"/>');

export const aiSelectTool = {
  id: 'aiselect', name: 'AI 고르기 (탭)', icon: 'aisel', cursor: 'crosshair',
  pts: [], emb: null, reading: false, model: null,
  get busy() { return false; },

  activate() { this.pts = []; if (!this.emb || this.emb.doc !== app.doc) this.read(); },
  deactivate() { this.pts = []; app.requestRender(); },

  async read() {
    if (this.reading) return;
    const list = await R.installed('select');
    if (!list.length) { app.toast('AI 고르기 모델이 없습니다. AI > AI 모델 관리에서 "SlimSAM" 을 받으세요', 'warn', { duration: 6000 }); return; }
    this.model = list[0];
    if (!(await licenseGate(this.model))) return;
    if (this.model.slow) app.toast(this.model.slow, 'info', { duration: 6000 });
    this.reading = true;
    try {
      this.emb = await app.task('그림 읽는 중 (AI 고르기)', async () => {
        const doc = app.doc, src = app.compositor.flatten(doc, { paper: true, background: '#ffffff' });
        const { data, box } = R.toCHW(src, 1024, 1024, { fit: 'contain', align: 'start', padZero: true, ...R.IMAGENET });
        const e = await R.session(this.model.id, 'vision_encoder.onnx');
        const out = await R.run(e, { pixel_values: await R.tensor(data, [1, 3, 1024, 1024]) });
        return { doc, box, scale: box.w / doc.width, emb: out.image_embeddings, pos: out.image_positional_embeddings };
      }, { delay: 0 });
      if (this.emb) app.toast('그림을 읽었습니다. 고를 물체를 탭하세요 (Shift+탭 = 더하기, Alt+탭 = 빼기)', 'info', { duration: 5000 });
    } finally { this.reading = false; }
  },

  async down(e) {
    if (!this.emb || this.emb.doc !== app.doc) { if (!this.reading) this.read(); app.toast('그림을 읽는 중입니다. 잠시 뒤 다시 탭하세요'); return; }
    if (!e.shift && !e.alt) this.pts = [];
    this.pts.push({ x: e.x, y: e.y, l: e.alt ? 0 : 1 });
    app.requestRender();
    await this.decode();
  },
  move() {},
  up() {},

  async decode() {
    const { emb, box, scale, doc } = this.emb, n = this.pts.length;
    const pts = new Float32Array(n * 2), lab = new BigInt64Array(n);
    this.pts.forEach((p, i) => { pts[i * 2] = p.x * scale; pts[i * 2 + 1] = p.y * scale; lab[i] = BigInt(p.l); });
    const e = await R.session(this.model.id, 'prompt_encoder_mask_decoder.onnx');
    const out = await R.run(e, {
      input_points: await R.tensor(pts, [1, 1, n, 2]), input_labels: await R.tensor(lab, [1, 1, n], 'int64'),
      image_embeddings: emb, image_positional_embeddings: this.emb.pos,
    });
    const iou = out.iou_scores.data;
    let best = 0;
    for (let k = 1; k < 3; k++) if (iou[k] > iou[best]) best = k;
    const m = out.pred_masks.data.subarray(best * 65536, (best + 1) * 65536);
    const sharp = new Float32Array(65536);
    for (let i = 0; i < 65536; i++) sharp[i] = m[i] * 3;                // 가장자리를 조금 또렷하게
    const mask = R.maskToCanvas(sharp, 256, 256, { x: 0, y: 0, w: box.w / 4, h: box.h / 4 }, doc.width, doc.height, { sigmoid: true });
    doc.selection.apply(ctx => ctx.drawImage(mask, 0, 0), 'replace', { x: 0, y: 0, w: doc.width, h: doc.height });
  },

  overlay(ctx, view) {
    for (const p of this.pts) {
      const s = view.toScreen(p.x, p.y);
      ctx.beginPath(); ctx.arc(s.x, s.y, 6, 0, Math.PI * 2);
      ctx.fillStyle = p.l ? '#22c55e' : '#ef4444'; ctx.fill();
      ctx.lineWidth = 2; ctx.strokeStyle = '#fff'; ctx.stroke();
    }
  },

  options(el) {
    el.append(section('AI 고르기',
      button('처음부터 (점 지우기)', () => { this.pts = []; app.run('select.none'); app.requestRender(); }),
      button('그림 다시 읽기 (그림을 고쳤으면)', () => { this.emb = null; this.read(); })));
  },
};

/** 메뉴에서: 모델 확인 후 도구로 */
export async function startAiSelect() {
  const list = await R.installed('select');
  if (!list.length) { await chooseModel('select', 'AI 고르기'); return; }
  app.setTool('aiselect');
}
