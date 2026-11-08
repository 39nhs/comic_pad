// 선 정리 (계산) + 이 PC 의 이미지 AI(Stable Diffusion WebUI / Forge) 연결
import { app } from '../app.js';
import { h, createCanvas } from '../core/util.js';
import { form } from './dialogs.js';
import { checkEditable, addLayer } from '../core/ops.js';
import { cleanLines } from '../core/lineclean.js';
import { server } from '../core/io.js';

// ---------------------------------------------------------------------
// 선 정리
// ---------------------------------------------------------------------
export async function lineCleanDialog() {
  const doc = app.doc, layer = doc.active?.target;
  if (!checkEditable(layer)) return;
  const k = Math.max(1, Math.round(doc.width / 1200));   // 큰 원고일수록 값도 크게
  const v = await form('선 정리', [
    { key: 'specks', label: '잡티 지우기: 이보다 작은 점은 지움 (픽셀 수, 0 = 끔)', type: 'number', value: 12 * k * k, min: 0 },
    { key: 'close', label: '끊긴 선 잇기: 이 정도 틈은 메움 (px, 0 = 끔)', type: 'number', value: k, min: 0, max: 20 },
    { key: 'smooth', label: '가장자리 다듬기: 울퉁불퉁·계단 줄이기 (px, 0 = 끔)', type: 'number', value: k, min: 0, max: 10 },
    { key: 'weight', label: '선 굵기 (+ 굵게 / − 가늘게, px)', type: 'number', value: 0, min: -5, max: 5 },
    { key: 'darken', label: '선 색을 한 가지로 (지금 전경색)', type: 'checkbox', value: false },
    { key: 'fromScan', label: '스캔한 그림(흰 바탕 종이)에서 선만 뽑기', type: 'checkbox', value: false },
    { key: 'info', type: 'info', label: `"${doc.active.name}" 레이어에 적용합니다${doc.selection.active ? ' (선택 영역 안만)' : ''}. 마음에 안 들면 실행취소(Ctrl+Z).` },
  ], { okLabel: '정리하기' });
  if (!v) return;
  const sel = doc.selection.active ? doc.selection.bounds : null;
  const r = sel || { x: 0, y: 0, w: doc.width, h: doc.height };
  let removed = 0;
  await app.task('선 정리', async () => {
    app.history.pixels(layer, '선 정리', (ctx) => {
      const img = ctx.getImageData(r.x, r.y, r.w, r.h);
      const before = sel ? new Uint8ClampedArray(img.data) : null;
      ({ removed } = cleanLines(img, { specks: +v.specks, close: +v.close, smooth: +v.smooth, weight: +v.weight, darken: v.darken, fromScan: v.fromScan, color: app.color.fg }));
      if (sel) {
        // 선택 영역 밖은 원래대로
        const m = doc.selection.mask.getContext('2d').getImageData(r.x, r.y, r.w, r.h).data;
        for (let i = 0; i < m.length; i += 4) if (!m[i + 3]) for (let c = 0; c < 4; c++) img.data[i + c] = before[i + c];
      }
      ctx.putImageData(img, r.x, r.y);
      return r;
    });
  }, { delay: 0 });
  app.toast(`선을 정리했습니다${removed ? ` (잡티 ${removed}px 지움)` : ''}`);
}

// ---------------------------------------------------------------------
// 이미지 AI (이 PC 에서 돌아가는 Stable Diffusion WebUI / Forge 의 API)
// ---------------------------------------------------------------------
const aiUrl = () => app.settings.aiUrl || 'http://127.0.0.1:7860';

async function aiCall(path, body) {
  if (!server.http) throw new Error('이미지 AI 연결은 ComicPad 서버(바탕화면 아이콘)로 켰을 때만 됩니다');
  const r = await fetch(`/api/ai?base=${encodeURIComponent(aiUrl())}&path=${encodeURIComponent(path)}`, { method: body ? 'POST' : 'GET', body: body ? JSON.stringify(body) : undefined, headers: { 'Content-Type': 'application/json' } });
  const t = await r.text();
  if (!r.ok) throw new Error(t.slice(0, 200) || `AI 서버 오류 (${r.status})`);
  return JSON.parse(t);
}

export async function aiCheck() {
  try {
    const models = await aiCall('/sdapi/v1/sd-models');
    app.toast(`이미지 AI 연결됨: 모델 ${models.length}개 (${(models[0]?.model_name || '').slice(0, 30)})`);
    return true;
  } catch (e) {
    app.toast(`이미지 AI 에 연결하지 못했습니다: ${e.message}\n\nStable Diffusion WebUI(또는 Forge)를 --api 옵션으로 켜 두면 연결됩니다. 주소: ${aiUrl()}`, 'warn', { duration: 9000 });
    return false;
  }
}

export async function aiImg2Img() {
  const doc = app.doc;
  const v = await form('이미지 AI 로 그리기 (img2img)', [
    { key: 'url', label: 'AI 서버 주소 (이 PC)', type: 'text', value: aiUrl() },
    { key: 'src', label: '바탕으로 쓸 것', type: 'segment', options: [['all', '보이는 그림 전체'], ['layer', '지금 레이어']], value: 'all' },
    { key: 'prompt', label: '원하는 결과 (영어가 잘 됨, 예: anime style, flat color, clean lineart)', type: 'textarea', value: app.settings.aiPrompt || 'anime style, flat colors, clean lineart, comic' },
    { key: 'neg', label: '빼고 싶은 것', type: 'text', value: app.settings.aiNeg || 'blurry, low quality, text, watermark' },
    { key: 'den', label: '바뀌는 정도 (0.2 = 조금, 0.7 = 많이)', type: 'number', value: app.settings.aiDen ?? 0.45, step: 0.05, min: 0.05, max: 1 },
    { key: 'steps', label: '단계 수', type: 'number', value: 25, min: 5, max: 80 },
    { key: 'info', type: 'info', label: `결과는 "AI 결과" 새 레이어로 들어갑니다${doc.selection.active ? ' (선택 영역 부분만)' : ''}. 그림은 이 PC 밖으로 나가지 않습니다 (이 PC 의 AI 서버로만 보냄).` },
  ], { okLabel: '그리기' });
  if (!v) return;
  if (!/^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?\/?$/.test(v.url.trim())) { app.toast('이 PC 의 주소(127.0.0.1 또는 localhost)만 쓸 수 있습니다', 'warn'); return; }
  Object.assign(app.settings, { aiUrl: v.url.trim().replace(/\/$/, ''), aiPrompt: v.prompt, aiNeg: v.neg, aiDen: +v.den });
  app.saveSettingsSoon();
  const sel = doc.selection.active ? doc.selection.bounds : null;
  const r = sel || { x: 0, y: 0, w: doc.width, h: doc.height };
  const full = v.src === 'layer' ? doc.active.canvas : app.compositor.flatten(doc, { paper: true, background: '#ffffff' });
  // AI 는 1024 안쪽, 8의 배수 크기로
  const s = Math.min(1, 1024 / Math.max(r.w, r.h)), W = Math.max(64, Math.round(r.w * s / 8) * 8), H = Math.max(64, Math.round(r.h * s / 8) * 8);
  const c = createCanvas(W, H), x = c.getContext('2d');
  x.fillStyle = '#fff'; x.fillRect(0, 0, W, H);
  x.drawImage(full, r.x, r.y, r.w, r.h, 0, 0, W, H);
  const b64 = c.toDataURL('image/png').split(',')[1];
  let res;
  try {
    res = await app.task('이미지 AI 그리는 중 (몇십 초 걸릴 수 있음)', () => aiCall('/sdapi/v1/img2img', { init_images: [b64], prompt: v.prompt, negative_prompt: v.neg, denoising_strength: +v.den, steps: +v.steps, width: W, height: H, cfg_scale: 7, sampler_name: 'Euler a' }), { delay: 0 });
  } catch (e) { app.toast(e.message, 'error'); aiCheck(); return; }
  const out = res.images?.[0];
  if (!out) { app.toast('AI 가 그림을 돌려주지 않았습니다', 'error'); return; }
  const img = new Image();
  await new Promise((ok, no) => { img.onload = ok; img.onerror = no; img.src = 'data:image/png;base64,' + out; });
  app.history.batch('AI 결과', () => {
    const l = addLayer({ name: 'AI 결과' });
    l.ctx.drawImage(img, r.x, r.y, r.w, r.h);
    if (sel) { l.ctx.globalCompositeOperation = 'destination-in'; l.ctx.drawImage(doc.selection.mask, 0, 0); l.ctx.globalCompositeOperation = 'source-over'; }
    l.touch();
  });
  app.toast('AI 결과를 새 레이어로 넣었습니다 (불투명도·지우개로 섞어 쓰세요)');
}

export function initAITools() {
  const C = (id, label, run) => app.registerCommand(id, { label, run });
  C('filter.lineClean', '선 정리 (잡티·끊김·울퉁불퉁)…', lineCleanDialog);
  C('ai.img2img', '이미지 AI 로 그리기 (이 PC 의 SD WebUI)…', aiImg2Img);
  C('ai.check', '이미지 AI 연결 확인', aiCheck);
  for (const it of [{ sep: true }, { command: 'filter.lineClean' }, { command: 'ai.img2img' }, { command: 'ai.check' }]) app.addMenuItem('filter', it);
}
