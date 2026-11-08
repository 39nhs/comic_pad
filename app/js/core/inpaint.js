// 내용 인식 채우기: 작업자에서 계산 (화면이 멈추지 않게)
import { createCanvas } from './util.js';

/** RGBA + 구멍(1) → 채운 RGBA. onProgress(0~1) */
export function inpaint(data, hole, w, h, onProgress) {
  return new Promise((resolve, reject) => {
    const wk = new Worker(new URL('./inpaintworker.js', import.meta.url));
    wk.onmessage = ({ data: m }) => {
      if (m.progress != null) onProgress?.(m.progress);
      else if (m.done) { wk.terminate(); resolve(m.data); }
      else if (m.error) { wk.terminate(); reject(new Error(m.error)); }
    };
    wk.onerror = (e) => { wk.terminate(); reject(new Error(e.message || '계산 오류')); };
    wk.postMessage({ w, h, data, hole });
  });
}

/**
 * 문서의 선택 영역을 주변으로 채운 그림(문서 크기 중 영역 부분)을 만든다.
 * src: 보고 채울 캔버스 (레이어 또는 보이는 그림 전체), maxSide: 이보다 크면 줄여서 계산
 * 반환 { canvas(영역 크기), rect }
 */
export async function contentAwareFill(src, selection, { margin = 1, maxSide = 1200, onProgress } = {}) {
  const doc = selection.doc, b = selection.bounds;
  const m = Math.max(48, Math.round(Math.max(b.w, b.h) * margin));
  const x0 = Math.max(0, b.x - m), y0 = Math.max(0, b.y - m), x1 = Math.min(doc.width, b.x + b.w + m), y1 = Math.min(doc.height, b.y + b.h + m);
  const rect = { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
  const s = Math.min(1, maxSide / Math.max(rect.w, rect.h));
  const W = Math.max(8, Math.round(rect.w * s)), H = Math.max(8, Math.round(rect.h * s));
  const c = createCanvas(W, H), cx = c.getContext('2d');
  cx.imageSmoothingQuality = 'high';
  cx.drawImage(src, rect.x, rect.y, rect.w, rect.h, 0, 0, W, H);
  const mk = createCanvas(W, H), mx = mk.getContext('2d');
  mx.drawImage(selection.mask, rect.x, rect.y, rect.w, rect.h, 0, 0, W, H);
  const md = mx.getImageData(0, 0, W, H).data, hole = new Uint8Array(W * H);
  for (let i = 0; i < hole.length; i++) hole[i] = md[i * 4 + 3] > 8 ? 1 : 0;
  // 가장자리 한 줄 더 (반쯤 선택된 테두리도 새로 채움)
  const grown = hole.slice();
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) if (hole[y * W + x]) for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) { const X = x + dx, Y = y + dy; if (X >= 0 && Y >= 0 && X < W && Y < H) grown[Y * W + X] = 1; }
  const data = cx.getImageData(0, 0, W, H).data;
  const out = await inpaint(data, grown, W, H, onProgress);
  const r = createCanvas(W, H);
  r.getContext('2d').putImageData(new ImageData(out, W, H), 0, 0);
  if (s === 1) return { canvas: r, rect };
  const big = createCanvas(rect.w, rect.h), bx = big.getContext('2d');
  bx.imageSmoothingQuality = 'high';
  bx.drawImage(r, 0, 0, rect.w, rect.h);
  return { canvas: big, rect };
}

