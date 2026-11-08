// 모션 경로 (트윈): 트랙마다 위치·크기·회전·불투명도 키를 찍으면 사이 프레임은 이징을 따라 자동으로 움직인다.
// 그림은 그대로 두고 그릴 때만 움직인다 (비파괴). "그림으로 굳히기" 로 프레임마다 그림을 만들 수 있다.
import { app } from '../app.js';

export const EASES = {
  linear: '일정하게', inout: '천천히 출발·정지', in: '천천히 출발', out: '천천히 정지', smooth: '부드럽게 (smoothstep)',
  back: '살짝 넘쳤다 돌아옴', bounce: '통통 튐', hold: '멈춰 있다 뚝 바뀜', bezier: '직접 곡선 (베지어)',
};

/** 0~1 → 0~1 이징 */
export function ease(name, t, bz) {
  t = Math.max(0, Math.min(1, t));
  switch (name) {
    case 'in': return t * t * t;
    case 'out': return 1 - Math.pow(1 - t, 3);
    case 'inout': return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
    case 'smooth': return t * t * (3 - 2 * t);
    case 'back': { const c = 1.70158, c3 = c + 1; return 1 + c3 * Math.pow(t - 1, 3) + c * Math.pow(t - 1, 2); }
    case 'bounce': {
      const n = 7.5625, d = 2.75;
      if (t < 1 / d) return n * t * t;
      if (t < 2 / d) { t -= 1.5 / d; return n * t * t + 0.75; }
      if (t < 2.5 / d) { t -= 2.25 / d; return n * t * t + 0.9375; }
      t -= 2.625 / d; return n * t * t + 0.984375;
    }
    case 'hold': return t < 1 ? 0 : 1;
    case 'bezier': return cubicBezier(bz || [0.42, 0, 0.58, 1], t);
    default: return t;
  }
}

/** CSS cubic-bezier(x1,y1,x2,y2) 의 t → y (x 로 풀기) */
export function cubicBezier([x1, y1, x2, y2], x) {
  const bx = (u) => 3 * x1 * u * (1 - u) * (1 - u) + 3 * x2 * u * u * (1 - u) + u * u * u;
  const by = (u) => 3 * y1 * u * (1 - u) * (1 - u) + 3 * y2 * u * u * (1 - u) + u * u * u;
  let lo = 0, hi = 1, u = x;
  for (let i = 0; i < 30; i++) { u = (lo + hi) / 2; if (bx(u) < x) lo = u; else hi = u; }
  return by(u);
}

const ID = { x: 0, y: 0, s: 1, r: 0, o: 1 };
export const motionOf = (track) => track?.meta?.motion || null;
export const anchorOf = (track, doc = app.doc) => { const m = motionOf(track); return { x: m?.ax ?? doc.width / 2, y: m?.ay ?? doc.height / 2 }; };

/** 프레임 f 의 값 { x, y, s, r, o } (키가 없으면 null) */
export function valuesAt(track, f) {
  const keys = motionOf(track)?.keys;
  if (!keys?.length) return null;
  const k = keys.slice().sort((a, b) => a.f - b.f);
  const pick = (q) => ({ x: q.x ?? 0, y: q.y ?? 0, s: q.s ?? 1, r: q.r ?? 0, o: q.o ?? 1 });
  if (f <= k[0].f) return pick(k[0]);
  if (f >= k.at(-1).f) return pick(k.at(-1));
  const i = k.findIndex(q => q.f > f), A = k[i - 1], B = k[i];
  const t = ease(A.ease || 'inout', (f - A.f) / (B.f - A.f), A.bz);
  const a = pick(A), b = pick(B), out = {};
  for (const key of Object.keys(ID)) out[key] = a[key] + (b[key] - a[key]) * t;
  return out;
}

/** 문서 좌표에서의 변환 (그리기용). 아무 변화가 없으면 null */
export function motionAt(track, f, doc = app.doc) {
  const v = valuesAt(track, f);
  if (!v) return null;
  if (Math.abs(v.x) < 1e-3 && Math.abs(v.y) < 1e-3 && Math.abs(v.s - 1) < 1e-4 && Math.abs(v.r) < 1e-3 && Math.abs(v.o - 1) < 1e-4) return null;
  const an = anchorOf(track, doc);
  const T = new DOMMatrix().translate(an.x + v.x, an.y + v.y).rotate(v.r).scale(v.s).translate(-an.x, -an.y);
  return { T, alpha: Math.max(0, Math.min(1, v.o)), v };
}

// ---- 키 고치기 (실행취소) ----
export function setMotion(track, next, label = '모션 키') {
  const prev = structuredClone(motionOf(track));
  const apply = (v) => { track.meta = { ...(track.meta || {}) }; if (v?.keys?.length || v?.ax != null) track.meta.motion = structuredClone(v); else delete track.meta.motion; app.markModified(); app.bus.emit('anim:changed'); app.requestRender(); };
  apply(next);
  app.history.push({ label, undo: () => apply(prev), redo: () => apply(next) });
}

/** 프레임 f 에 키 넣기 (값을 주지 않으면 지금 보이는 값 그대로) */
export function setKey(track, f, vals = {}, label = '모션 키') {
  const m = structuredClone(motionOf(track) || { keys: [] });
  const cur = valuesAt(track, f) || { ...ID };
  const k = m.keys.find(q => q.f === f);
  if (k) Object.assign(k, vals);
  else m.keys.push({ f, ...cur, ease: 'inout', ...vals });
  m.keys.sort((a, b) => a.f - b.f);
  setMotion(track, m, label);
}

export function removeKey(track, f) {
  const m = structuredClone(motionOf(track));
  if (!m) return;
  m.keys = m.keys.filter(q => q.f !== f);
  setMotion(track, m, '모션 키 지우기');
}

/** 키 프레임 번호 밀기 (프레임 넣기·지우기와 같이) */
export function shiftKeys(doc, at, d) {
  for (const t of doc.layers) {
    const m = t.meta?.motion;
    if (!m?.keys) continue;
    for (const k of m.keys) if (k.f >= at) k.f = Math.max(at, k.f + d);
  }
}
